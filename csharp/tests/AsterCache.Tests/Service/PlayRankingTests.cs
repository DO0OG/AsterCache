using AsterCache.Core.Plan;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 플레이 전체 기록의 블록 순위 목록이 예열 선택과 실행 중 유지에 쓰이는 방식: 선택 단계 순서, 겹침 제거, 순위 목록이 없을 때 지금과 같음, 유지 순서.
public sealed class PlayRankingTests : IDisposable
{
    private const long Block = LoadBoundaries.Block;
    private const string A = @"D:\Games\a.bin";
    private const string B = @"D:\Games\b.bin";

    private const long Mib = 1L << 20;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_playranking_" + Guid.NewGuid().ToString("N"));

    public PlayRankingTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static TraceEvent Ev(string path, long block, long blocks = 1, long? tMs = null, bool observed = true) => new(path, block * Block, blocks * Block, TMs: tMs, Observed: observed);

    private static IEnumerable<(string, long, long)> Shape(IEnumerable<TraceEvent> rows) => rows.Select(r => (r.Path, r.Offset / Block, r.Length / Block));

    [Fact]
    public void WithoutARankedListTheSelectionIsExactlyTheExistingOne()
    {
        TraceEvent[] rows = [Ev(A, 0, 2), Ev(A, 10, 2), Ev("C", 0, 2, tMs: 1_000), Ev("C", 5, 2, tMs: 90_000), Ev(B, 0, 2, observed: false)];
        long budget = 5 * Block;

        List<TraceEvent> existing = PrefetchStreamer.SelectToBudget(rows, budget, 2, 60_000, out long existingFront);
        List<TraceEvent> none = PrefetchStreamer.SelectToBudget(rows, budget, 2, 60_000, out long noneFront, null, 3 * Block);
        List<TraceEvent> empty = PrefetchStreamer.SelectToBudget(rows, budget, 2, 60_000, out long emptyFront, [], 3 * Block);

        Assert.Equal(existing, none);
        Assert.Equal(existing, empty);
        Assert.Equal((existingFront, existingFront), (noneFront, emptyFront));
    }

    [Fact]
    public void TheRankedRowsComeAfterTheEarlyWarmOnlyRowsAndTier0AndBeforeTheRestOfThePlanRows()
    {
        TraceEvent[] rows = [Ev(A, 0, 2), Ev(A, 2, 2), Ev(A, 4, 2), Ev("C", 0, 1, tMs: 1_000), Ev("C", 9, 1, tMs: 90_000), Ev(B, 0, 1, observed: false)];
        TraceEvent[] ranked = [Ev("R", 7), Ev("R", 3)];

        // tier0 = 계획 행 앞 2 블록 (첫 행). 예산이 넉넉해도 전체가 들어가지 않을 만큼만 (총 9 블록 > 8)
        long rankedBytes = -1;
        List<TraceEvent> kept = PrefetchStreamer.SelectToBudget(rows, 8 * Block, 3, 60_000, out long front, ranked, 2 * Block, bytes => rankedBytes = bytes);

        Assert.Equal([("C", 0L, 1L), (A, 0L, 2L), ("R", 7L, 1L), ("R", 3L, 1L), (A, 2L, 2L), (A, 4L, 1L)], Shape(kept)); // 앞부분 예열 전용 → tier0 → 순위 → 나머지 계획 행 (예산에서 자름)
        Assert.Equal(Block, front);
        Assert.Equal(2 * Block, rankedBytes); // 순위 목록에서 담은 양 (로그에 남김)
    }

    [Fact]
    public void RankedRowsDropWhatTheEarlierStepsAlreadyHoldAndLaterRowsDropWhatTheRankedRowsHold()
    {
        TraceEvent[] rows = [Ev(A, 0, 4), Ev(A, 10, 4), Ev(A, 20, 4)];
        // 순위: A 의 1~2 (tier0 안, 이미 담김), A 의 5 (새 것), A 의 11~12 (뒤쪽 계획 행 안), 다른 파일 하나
        TraceEvent[] ranked = [Ev(A, 1, 2), Ev(A, 5), Ev(A, 11, 2), Ev("R", 0)];

        long rankedBytes = -1;
        List<TraceEvent> kept = PrefetchStreamer.SelectToBudget(rows, 10 * Block, 3, 0, out _, ranked, 4 * Block, bytes => rankedBytes = bytes);

        Assert.Equal([(A, 0L, 4L), (A, 5L, 1L), (A, 11L, 2L), ("R", 0L, 1L), (A, 10L, 1L), (A, 13L, 1L)], Shape(kept)); // 같은 구간을 두 번 담지 않고, 예산 (10 블록) 에서 자름
        Assert.Equal(10 * Block, kept.Sum(r => r.Length));
        Assert.Equal(4 * Block, rankedBytes); // 앞 단계와 겹치는 A 1~2 는 세지 않음
        Assert.DoesNotContain(kept.GroupBy(r => (r.Path, r.Offset)), g => g.Count() > 1);
    }

    [Fact]
    public void ThePrewarmPhasesLineShowsTheBytesSelectedFromTheRankedListOnlyWhenThereWereAny()
    {
        using PrewarmRun run = PrewarmRun.Begin("g");
        Assert.DoesNotContain("ranked", run.Summary(), StringComparison.Ordinal);

        PrewarmRun.NoteRankedBytes(5L << 20);

        Assert.EndsWith(", 5 MiB selected from the ranked block list", run.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void SparseRangesOfAPlanWhoseRankedListHasNoSparseBlockCompeteAfterTheRankedRowsWithoutAFixedShare()
    {
        TraceEvent[] rows = [Ev("S", 0, 4), Ev(A, 0, 4)]; // 계획 (선읽기 행): 스파스 파일 4 블록, 일반 4 블록
        TraceEvent[] ranked = [Ev("R", 0, 3)]; // 스파스 블록이 없는 예전 순위 목록

        List<TraceEvent> some = PrefetchStreamer.SelectToBudget(rows, 5 * Block, 2, 0, out _, ranked, 0);
        List<TraceEvent> none = PrefetchStreamer.SelectToBudget(rows, 3 * Block, 2, 0, out _, ranked, 0);

        Assert.Equal([("R", 0L, 3L), ("S", 0L, 2L)], Shape(some)); // 순위 행이 먼저, 남은 예산은 계획 행 (스파스 범위 포함) 이 계획 순서로 다툼
        Assert.Equal([("R", 0L, 3L)], Shape(none)); // 예산이 순위 행에 다 들어가면 스파스 몫을 따로 두지 않음
    }

    [Fact]
    public void ARankedRowAppearsOnlyWhenTheListIsTooBigForTheBudget()
    {
        TraceEvent[] rows = [Ev(A, 0, 2), Ev(A, 4, 2)];

        List<TraceEvent> fits = PrefetchStreamer.SelectToBudget(rows, 4 * Block, 2, 0, out long front, [Ev("R", 0)], 2 * Block);

        Assert.Equal(rows, fits); // 전부 들어가면 지금처럼 목록 그대로 (예산 안의 순서만 바뀌는 기능)
        Assert.Equal(0, front);
    }

    [Fact]
    public void ReorderPutsRankedBlocksFirstInRankOrderMergesNeighboursAndKeepsTheRestInTheirOrder()
    {
        ReadOp[] reads = [new(A, 0, 4 * Block), new(B, 0, 2 * Block), new(A, 10 * Block, 2 * Block)];
        TraceEvent[] ranked = [Ev(A, 11), Ev(B, 1), Ev(A, 2), Ev(A, 3), Ev("R", 0), Ev(A, 50)]; // R 와 A 의 50 은 읽기 범위 밖

        IReadOnlyList<ReadOp> result = PlayRanking.Reorder(reads, ranked);

        Assert.Equal([new ReadOp(A, 11 * Block, Block), new ReadOp(B, 1 * Block, Block), new ReadOp(A, 2 * Block, 2 * Block), // 순위 순 (이어진 A 2~3 은 한 범위)
            new ReadOp(A, 0, 2 * Block), new ReadOp(B, 0, Block), new ReadOp(A, 10 * Block, Block)], result); // 순위 밖은 지금 순서로 뒤에
        Assert.Equal(reads.Sum(r => r.Length), result.Sum(r => r.Length)); // 읽기 범위 밖을 끌어오지도 잃지도 않음
    }

    [Fact]
    public void ReorderReturnsTheSameListWhenNoRankedBlockIsInTheReadsAndTheCutKeepsTheHighestRanks()
    {
        ReadOp[] reads = [new(A, 0, 4 * Block)];

        Assert.Same(reads, PlayRanking.Reorder(reads, [Ev(B, 0), Ev(A, 40)])); // 순위 블록이 없으면 지금과 같음

        IReadOnlyList<ReadOp> ordered = PlayRanking.Reorder(reads, [Ev(A, 3), Ev(A, 1)]);
        IReadOnlyList<ReadOp> cut = PrefetchStreamer.Head(ordered, 2 * Block); // 유지가 사용 가능 메모리 비율로 자르는 것과 같은 자르기

        Assert.Equal([new ReadOp(A, 3 * Block, Block), new ReadOp(A, 1 * Block, Block)], cut);
    }

    // 선읽기 행 a.bin (300 MiB) 과 b.bin (200 MiB) 이 예산 (max_stream_mib 400) 보다 크고, c.bin 에 순위 블록이 있는 게임
    private GameSettings Rig(string name)
    {
        string root = Path.Combine(_dir, name);
        Directory.CreateDirectory(root);
        var rows = new List<TraceEvent>();
        foreach ((string file, long mib) in new[] { ("a.bin", 300L), ("b.bin", 200L), ("c.bin", 8L) })
        {
            string path = Path.Combine(root, file);
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength(mib * Mib);
            if (file != "c.bin")
            {
                rows.Add(new TraceEvent(path, 0, mib * Mib));
            }
        }

        string plan = Path.Combine(_dir, name + ".plan.json");
        string trace = Path.Combine(_dir, name + ".prefetch.json");
        new PlanFile(PlanFile.CurrentVersion, root, PlanFile.SplitDrive(root).ToUpperInvariant(), 4096, 65536, new PlanZone(0, 1), [], [], [], new AsterCache.Core.Json.JObject()).Save(plan);
        PlanOutput.SavePrefetch(trace, rows);
        return Games.Make(name, name + ".exe") with { OriginRoot = root, AllowedRoots = [root], Plan = plan, Trace = trace, MaxStreamMib = 400, MaxStreamSet = true };
    }

    private static int _writes;

    // 순위 파일을 쓴다 (순위 순 (파일, 블록 번호)).
    private static void WriteRanked(GameSettings game, params (string File, long Block)[] blocks)
    {
        var files = new Dictionary<string, LoadRefillFileStat>();
        foreach (string file in blocks.Select(b => Path.Combine(game.OriginRoot, b.File)).Distinct())
        {
            var info = new FileInfo(file);
            files[file] = new LoadRefillFileStat(info.Length, info.LastWriteTimeUtc.Ticks);
        }

        var list = new LoadRefillList([.. blocks.Select(b => new LoadRefillBlock(Path.Combine(game.OriginRoot, b.File), b.Block, 1, 10))], files, 1, 0, 0, 0, 0);
        LoadRefillStore.Write(LoadRefillPlans.RankedPathOf(game), list, DateTimeOffset.UtcNow);
        File.SetLastWriteTimeUtc(LoadRefillPlans.RankedPathOf(game), DateTime.UtcNow.AddSeconds(Interlocked.Increment(ref _writes))); // 연속으로 써도 수정 시각이 달라지게
    }

    // 범위들을 파일별로 정렬·병합한 (경로, 시작, 끝)
    private static List<(string Path, long Start, long End)> Merged(IEnumerable<ReadOp> ops)
    {
        var merged = new List<(string Path, long Start, long End)>();
        foreach (ReadOp op in ops.OrderBy(o => o.Path, StringComparer.OrdinalIgnoreCase).ThenBy(o => o.Offset))
        {
            if (merged.Count > 0 && string.Equals(merged[^1].Path, op.Path, StringComparison.OrdinalIgnoreCase) && op.Offset <= merged[^1].End)
            {
                merged[^1] = (merged[^1].Path, merged[^1].Start, Math.Max(merged[^1].End, op.Offset + op.Length));
            }
            else
            {
                merged.Add((op.Path, op.Offset, op.Offset + op.Length));
            }
        }

        return merged;
    }

    // touch 에 있는데 stream 에는 없는 바이트 (사다리가 만지다 디스크를 읽게 되는 양)
    private static long Uncovered(IEnumerable<ReadOp> touch, IEnumerable<ReadOp> stream)
    {
        List<(string Path, long Start, long End)> read = Merged(stream);
        long missing = 0;
        foreach ((string path, long start, long end) in Merged(touch))
        {
            long at = start;
            foreach ((string _, long s, long e) in read.Where(r => string.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase) && r.End > start && r.Start < end))
            {
                missing += Math.Max(0, s - at);
                at = Math.Max(at, e);
            }

            missing += Math.Max(0, end - at);
        }

        return missing;
    }

    [Fact]
    public void TheGeneralStreamAndTheTouchListChooseTheSameRangesWithARankedList()
    {
        GameSettings game = Rig("general");
        WriteRanked(game, ("c.bin", 0), ("c.bin", 1), ("b.bin", 2999));

        List<ReadOp> stream = PrefetchStreamer.PrewarmOps(game, 500 * Mib).Ops; // 일반 예열 스트림 (틈 메우기 예산 > 0)
        IReadOnlyList<ReadOp> touch = new PrefetchStreamer().PlannedPrewarmReads(game)!; // 사다리·시작 만지기·유지가 쓰는 목록

        Assert.Equal(Merged(stream), Merged(touch));
        Assert.Contains(Merged(stream), r => r.Path.EndsWith("c.bin", StringComparison.Ordinal)); // 순위 블록이 스트림에 들어감
        Assert.Equal(0, Uncovered(touch, stream));
        Assert.Equal(400 * Mib, stream.Sum(o => o.Length)); // 예산은 그대로
    }

    [Fact]
    public void WithoutARankedFileTheGeneralStreamKeepsItsOrderAndSkipsTheRangeThatDoesNotFit()
    {
        GameSettings game = Rig("plain");

        List<ReadOp> stream = PrefetchStreamer.PrewarmOps(game, 500 * Mib).Ops;

        Assert.Equal([(Path.Combine(game.OriginRoot, "a.bin"), 0L, 300 * Mib)], Merged(stream)); // 지금과 같음: 앞의 행 (300 MiB) 만 들어가고 다음 행 (200 MiB) 은 예산 (400 MiB) 에 안 들어가 건너뜀
    }

    [Fact]
    public void ThePartialStreamAndTheTouchListChooseTheSameRangesWithARankedList()
    {
        GameSettings game = Rig("partial");
        WriteRanked(game, ("c.bin", 0), ("b.bin", 100));
        GameSettings covered = game with { MaxStreamMib = 320, MaxStreamSet = true }; // 메모리가 모자라 줄여 읽은 앞부분

        List<ReadOp> stream;
        using (PrewarmRun.Begin(game.Name))
        {
            stream = PrefetchStreamer.PrewarmOps(covered, 0).Ops; // 부분 예열 스트림 (틈 메우기 예산 0)
            LoadRefillPlans.CommitRanked(game); // 스트림이 읽음
        }

        IReadOnlyList<ReadOp> touch = new PrefetchStreamer().PlannedPrewarmReads(covered)!;

        Assert.Equal(Merged(stream), Merged(touch));
        Assert.Equal(0, Uncovered(touch, stream));
        Assert.Contains(Merged(stream), r => r.Path.EndsWith("c.bin", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTouchListKeepsTheSelectionOfTheLastPrewarmUntilANewPrewarmReadsTheChangedRankedFile()
    {
        GameSettings game = Rig("pinned");
        WriteRanked(game, ("c.bin", 0), ("b.bin", 2999));
        var streamer = new PrefetchStreamer();

        List<ReadOp> first;
        using (PrewarmRun.Begin(game.Name))
        {
            first = PrefetchStreamer.PrewarmOps(game, 500 * Mib).Ops; // 예열 한 회: 이 순위로 읽음
            LoadRefillPlans.CommitRanked(game); // 스트림이 읽음
        }

        WriteRanked(game, ("b.bin", 100), ("b.bin", 101)); // 게임이 끝나 순위 파일이 새로 만들어짐 (다음 예열 전)
        Assert.Equal(Merged(first), Merged(streamer.PlannedPrewarmReads(game)!)); // 만지는 목록은 마지막 예열이 읽은 선택 그대로: 읽지 않은 블록이 앞에 오지 않음
        Assert.Equal(0, Uncovered(streamer.PlannedPrewarmReads(game)!, first)); // 사다리가 디스크를 읽게 될 범위가 없음 = 재예열 점검이 다시 읽을 것이 없음

        List<ReadOp> second;
        using (PrewarmRun.Begin(game.Name))
        {
            second = PrefetchStreamer.PrewarmOps(game, 500 * Mib).Ops; // 새 예열: 새 순위로 읽음
            LoadRefillPlans.CommitRanked(game);
        }

        Assert.NotEqual(Merged(first), Merged(second));
        Assert.Equal(Merged(second), Merged(streamer.PlannedPrewarmReads(game)!)); // 그 뒤의 만지는 목록은 새 선택
        Assert.Equal(0, Uncovered(streamer.PlannedPrewarmReads(game)!, second));
    }

    [Fact]
    public void AnotherGamesPrewarmRunDoesNotChangeTheRememberedSelectionOfAGame()
    {
        GameSettings a = Rig("others-a");
        GameSettings b = Rig("others-b");
        WriteRanked(b, ("c.bin", 0), ("b.bin", 2999));
        var streamer = new PrefetchStreamer();
        List<ReadOp> before = [.. streamer.PlannedPrewarmReads(b)!]; // B 의 캐시에 있는 선택 (기억함)

        WriteRanked(b, ("b.bin", 100), ("b.bin", 101)); // B 를 끝내 순위 파일이 새로 만들어짐
        using (PrewarmRun.Begin(a.Name)) // A 를 예열하다 B 를 보호·표본함
        {
            _ = PrefetchStreamer.ObservedBytes(b);
            _ = PrefetchStreamer.PrewarmOps(b, 0);
        }

        Assert.Equal(Merged(before), Merged(streamer.PlannedPrewarmReads(b)!)); // B 의 만지는 목록은 그대로
        using (PrewarmRun.Begin(b.Name))
        {
            Assert.NotEqual(Merged(before), Merged(PrefetchStreamer.PrewarmOps(b, 500 * Mib).Ops)); // B 자신의 예열 회는 새 파일을 읽음 (시험이 의미 있는지)
        }
    }

    [Fact]
    public void ARunThatReadNothingKeepsThePreviousSelectionAndOnlyAStreamThatReadCommitsTheNewOne()
    {
        GameSettings game = Rig("skipped");
        WriteRanked(game, ("c.bin", 0), ("b.bin", 2999));
        var streamer = new PrefetchStreamer();
        List<ReadOp> before;
        using (PrewarmRun.Begin(game.Name))
        {
            before = PrefetchStreamer.PrewarmOps(game, 500 * Mib).Ops;
            LoadRefillPlans.CommitRanked(game);
        }

        WriteRanked(game, ("b.bin", 100), ("b.bin", 101));
        using (PrewarmRun.Begin(game.Name))
        {
            _ = PrefetchStreamer.PrewarmOps(game, 500 * Mib); // 계획만 하고 low memory 등으로 건너뛰거나 읽기 전에 취소됨 (확정하지 않음)
        }

        Assert.Equal(Merged(before), Merged(streamer.PlannedPrewarmReads(game)!)); // 이전 기억 유지

        using (PrewarmRun.Begin(game.Name))
        {
            _ = PrefetchStreamer.PrewarmOps(game, 500 * Mib); // 같은 새 파일을 다시 계획 (이미 읽은 결과를 재사용)
            LoadRefillPlans.CommitRanked(game); // 이번에는 스트림이 읽음
        }

        Assert.NotEqual(Merged(before), Merged(streamer.PlannedPrewarmReads(game)!));
    }

    [Fact]
    public void WithoutARememberedSelectionTheTouchListReadsTheCurrentRankedFile()
    {
        GameSettings game = Rig("fresh"); // 서비스를 막 시작한 때: 기억한 것이 없음
        WriteRanked(game, ("c.bin", 0));

        IReadOnlyList<ReadOp> touch = new PrefetchStreamer().PlannedPrewarmReads(game)!;

        Assert.Contains(Merged(touch), r => r.Path.EndsWith("c.bin", StringComparison.Ordinal));
    }
}
