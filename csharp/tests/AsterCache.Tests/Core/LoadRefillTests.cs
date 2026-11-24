using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Trace;
using Xunit;

namespace AsterCache.Tests.Core;

/// 로드 구간 블록 목록: 가짜 집계 두 개로 창·비캐시 제외·합집합·점수·상한·디스크 위치 순서와 저장 파일 왕복을 시험합니다.
public sealed class LoadRefillTests : IDisposable
{
    private const long Block = LoadBoundaries.Block;
    private const string A = @"D:\Games\a.bin";
    private const string B = @"D:\Games\b.bin";
    private static readonly DateTimeOffset Started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
    private static readonly LoadRefillFileStat Stat = new(10 * Block, 7); // 두 파일 모두 블록 10 개짜리

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_refill_" + Guid.NewGuid().ToString("N"));

    public LoadRefillTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static long Sec(double s) => (long)(s * 1_000_000);

    private static IoSummaryRow Row(int file, long block, double firstSec, long paging = 1, long latencyUs = 100) =>
        new(file, block, 1, Block, latencyUs, latencyUs, Sec(firstSec), paging);

    private static LoadRefillSource Source(IReadOnlyList<long> starts, IReadOnlyList<IoSummaryFile> files, params IoSummaryRow[] rows) =>
        new(new IoSummary(Started, Started.AddMinutes(30), [], new IoSummaryQuality(default, 0, default, default, default, default, default, 0, 0, 0), files, rows), starts);

    private static LoadRefillFileStat? Existing(string path) => path is A or B ? Stat : null;

    [Fact]
    public void OnlyBlocksFirstAccessedInsideTheWindowAfterAStartAreKeptAndNoCacheOnlyBlocksAreLeftOut()
    {
        LoadRefillSource source = Source([Sec(10), Sec(200)], [new IoSummaryFile(A)],
            Row(0, 0, 10.5), // 첫 로드 창 안
            Row(0, 1, 129.9), // 첫 로드 창 끝 직전
            Row(0, 2, 130), // 창 (120 초) 밖이고 두 번째 시작 전
            Row(0, 3, 5), // 어떤 시작보다 앞
            Row(0, 4, 205), // 두 번째 로드 창 안
            Row(0, 5, 11, paging: 0)); // 캐시를 거치지 않고만 읽힘

        LoadRefillList list = LoadRefillPlanner.Collect([source], Existing, TimeSpan.FromSeconds(120), 1L << 30);

        Assert.Equal([0L, 1L, 4L], list.Blocks.Select(b => b.Block).Order());
        Assert.Equal(1, list.Plays);
        Assert.Equal(2, list.Loads);
        Assert.Equal(1, list.SkippedNoCacheBlocks);
    }

    [Fact]
    public void WithoutAWindowEveryPhaseOfEveryPlayCountsAndPlaysWithoutStartsAreKept()
    {
        var files = new[] { new IoSummaryFile(A), new IoSummaryFile(B) };
        LoadRefillSource first = Source([Sec(1)], files, Row(0, 0, 2, latencyUs: 50), Row(0, 1, 900, latencyUs: 10), Row(1, 0, 4, latencyUs: 700), Row(0, 3, 6, paging: 0)); // 창 밖 (900 초)·시작 전 블록도 모음
        LoadRefillSource second = Source([], files, Row(0, 0, 5, latencyUs: 50), Row(1, 2, 8, latencyUs: 5)); // 시작 시각 없는 집계도 한 플레이

        LoadRefillList list = LoadRefillPlanner.Collect([first, second], Existing, null, 1L << 30);

        Assert.Equal([(A, 0L, 2), (B, 0L, 1), (A, 1L, 1), (B, 2L, 1)], list.Blocks.Select(b => (b.Path, b.Block, b.Count))); // 플레이 수 → 지연 합 (700, 10, 5)
        Assert.Equal((2, 0, 0, 1), (list.Plays, list.Loads, list.PlaysWithoutStarts, (int)list.SkippedNoCacheBlocks));
    }

    [Fact]
    public void PlaysAreMergedAndSortedByPlayCountThenLatencyAndCutAtTheCap()
    {
        var files = new[] { new IoSummaryFile(A), new IoSummaryFile(B) };
        LoadRefillSource first = Source([Sec(1)], files, Row(0, 0, 2, latencyUs: 50), Row(0, 1, 3, latencyUs: 900), Row(1, 0, 4, latencyUs: 10));
        LoadRefillSource second = Source([Sec(5)], files, Row(0, 0, 6, latencyUs: 50), Row(1, 0, 7, latencyUs: 10), Row(1, 2, 8, latencyUs: 5));

        LoadRefillList all = LoadRefillPlanner.Collect([first, second], Existing, TimeSpan.FromSeconds(120), 1L << 30);

        // 두 플레이에 나온 블록이 먼저 (지연 합 큰 순), 그 뒤 한 플레이에서만 나온 블록 (지연 큰 순)
        Assert.Equal([(A, 0L, 2), (B, 0L, 2), (A, 1L, 1), (B, 2L, 1)], all.Blocks.Select(b => (b.Path, b.Block, b.Count)));
        Assert.Equal(2, all.Plays);
        Assert.Equal(2, all.Loads);

        LoadRefillList capped = LoadRefillPlanner.Collect([first, second], Existing, TimeSpan.FromSeconds(120), 3 * Block);

        Assert.Equal([(A, 0L), (B, 0L), (A, 1L)], capped.Blocks.Select(b => (b.Path, b.Block)));
        Assert.Equal(1, capped.DroppedByCapBlocks);
        Assert.Equal(3 * Block, capped.Bytes);
    }

    [Fact]
    public void ChangedMissingAndOutOfRangeFilesAndSummariesWithoutStartsAreLeftOut()
    {
        var files = new[] { new IoSummaryFile(A, ChangedDuringSession: true), new IoSummaryFile(B), new IoSummaryFile(@"D:\Games\gone.bin") };
        LoadRefillSource source = Source([Sec(1)], files, Row(0, 0, 2), Row(1, 0, 2), Row(1, 10, 2), Row(2, 0, 2));
        LoadRefillSource noStarts = Source([], files, Row(1, 5, 2));

        LoadRefillList list = LoadRefillPlanner.Collect([source, noStarts], Existing, TimeSpan.FromSeconds(120), 1L << 30);

        Assert.Equal([(B, 0L)], list.Blocks.Select(b => (b.Path, b.Block))); // 바뀐 파일·없는 파일·파일 끝 밖 블록 제외
        Assert.Equal(1, list.PlaysWithoutStarts);
        Assert.Equal(1, list.Plays);
    }

    [Fact]
    public void OpsAreMergedIntoRunsAndOrderedByDiskPositionWithTheTailClippedToTheFileSize()
    {
        var list = new LoadRefillList(
            [new(A, 9, 2, 0), new(A, 2, 2, 0), new(A, 3, 1, 0), new(B, 4, 1, 0)],
            new Dictionary<string, LoadRefillFileStat> { [A] = new(9 * Block + 100, 7), [B] = Stat }, 2, 2, 0, 0, 0);
        FileLayoutInfo? Layout(string path) => path == B
            ? new FileLayoutInfo([new Extent(0, 100, 160)], 4096) // 디스크 앞쪽
            : new FileLayoutInfo([new Extent(0, 9000, 160)], 4096); // 디스크 뒤쪽

        List<ReadOp> ops = LoadRefillPlanner.ToOps(list, path => list.Files[path], 1L << 30, Layout);

        Assert.Equal([new ReadOp(B, 4 * Block, Block), new ReadOp(A, 2 * Block, 2 * Block), new ReadOp(A, 9 * Block, 100)], ops);
    }

    [Fact]
    public void OpsDropFilesThatChangedSinceTheListWasSavedAndHonorTheMemoryCap()
    {
        var list = new LoadRefillList(
            [new(A, 0, 2, 0), new(B, 0, 2, 0), new(B, 1, 1, 0)],
            new Dictionary<string, LoadRefillFileStat> { [A] = Stat, [B] = Stat }, 2, 2, 0, 0, 0);

        List<ReadOp> changed = LoadRefillPlanner.ToOps(list, path => path == A ? new LoadRefillFileStat(Stat.Size, Stat.MTimeTicks + 1) : Stat, 1L << 30, null);
        List<ReadOp> capped = LoadRefillPlanner.ToOps(list, path => Stat, 2 * Block, null);

        Assert.Equal([new ReadOp(B, 0, 2 * Block)], changed); // A 는 수정 시각이 달라 뺌
        Assert.Equal([new ReadOp(A, 0, Block), new ReadOp(B, 0, Block)], capped); // 점수 순 앞의 두 블록만
        Assert.Empty(LoadRefillPlanner.ToOps(list, _ => null, 1L << 30, null));
        Assert.Empty(LoadRefillPlanner.ToOps(list, _ => Stat, 0, null));
    }

    [Fact]
    public void RowsKeepTheScoreOrderOneBlockEachClipTheTailAndDropFilesThatChanged()
    {
        var list = new LoadRefillList(
            [new(B, 4, 3, 0), new(A, 9, 2, 0), new(A, 2, 1, 0)],
            new Dictionary<string, LoadRefillFileStat> { [A] = new(9 * Block + 100, 7), [B] = Stat }, 3, 0, 0, 0, 0);

        List<TraceEvent> rows = LoadRefillPlanner.ToRows(list, path => list.Files[path]);
        List<TraceEvent> changed = LoadRefillPlanner.ToRows(list, path => path == A ? new LoadRefillFileStat(1, 1) : Stat);

        Assert.Equal([(B, 4 * Block, Block), (A, 9 * Block, 100L), (A, 2 * Block, Block)], rows.Select(r => (r.Path, r.Offset, r.Length))); // 점수 순 그대로, 끝 블록은 파일 끝에서 자름
        Assert.All(rows, r => Assert.True(r.Observed));
        Assert.Equal([(B, 4 * Block, Block)], changed.Select(r => (r.Path, r.Offset, r.Length)));
    }

    [Fact]
    public void ListSurvivesAWriteAndReadAndBadFilesReadAsNone()
    {
        var list = new LoadRefillList(
            [new(A, 3, 2, 400), new(B, 1, 1, 7)],
            new Dictionary<string, LoadRefillFileStat> { [A] = Stat, [B] = new(Block, 99) }, 2, 3, 1, 4, 5);
        string path = Path.Combine(_dir, "g.trace.json" + LoadRefillStore.Suffix);

        LoadRefillStore.Write(path, list, Started);
        LoadRefillList restored = Assert.IsType<LoadRefillList>(LoadRefillStore.Read(path));

        Assert.Equal(list.Blocks, restored.Blocks);
        Assert.Equal(list.Files.OrderBy(f => f.Key), restored.Files.OrderBy(f => f.Key));
        Assert.Equal((2, 3, 1, 4L, 5L), (restored.Plays, restored.Loads, restored.PlaysWithoutStarts, restored.SkippedNoCacheBlocks, restored.DroppedByCapBlocks));
        Assert.False(File.Exists(path + ".tmp"));

        File.WriteAllText(path, "{\"version\": 9}");
        Assert.Null(LoadRefillStore.Read(path));
        Assert.Null(LoadRefillStore.Read(Path.Combine(_dir, "none.json")));
    }

    [Fact]
    public void ClearSidecarsRemovesTheListOfARestartedTrace()
    {
        string raw = Path.Combine(_dir, "h.trace.json");
        File.WriteAllText(raw, "[]");
        File.WriteAllText(LoadRefillStore.PathFor(raw), "{}");

        TraceStageIndex.ClearSidecars(raw);

        Assert.False(File.Exists(LoadRefillStore.PathFor(raw)));
    }

    [Fact]
    public void SummaryStoreKeepsTargetStartsAndReadsOldSummariesWithoutThem()
    {
        string path = Path.Combine(_dir, "i.trace.run1.json.io.json");
        IoSummary summary = new IoSummary(Started, Started.AddSeconds(1), [], new IoSummaryQuality(default, 0, default, default, default, default, default, 0, 0, 0), [], [], TargetStartsUs: [5, 7_000_000]);

        IoSummaryStore.Write(path, summary);
        Assert.Equal([5L, 7_000_000L], Assert.IsType<IoSummary>(IoSummaryStore.Read(path)).TargetStartsUs);

        string text = File.ReadAllText(path).Replace("\"target_starts_us\"", "\"x_target_starts_us\"", StringComparison.Ordinal);
        File.WriteAllText(path, text);
        Assert.Null(Assert.IsType<IoSummary>(IoSummaryStore.Read(path)).TargetStartsUs);
    }

    [Fact]
    public void GameWrittenFilesAreRankedByTheSameScoreAsStaticFilesButOnlyForBlocksReadInTwoOrMorePlays()
    {
        const string W = @"D:\Games\w.bin";
        var files = new[] { new IoSummaryFile(A), new IoSummaryFile(W) };
        LoadRefillFileStat? Both(string path) => path is A or W ? Stat : null;
        // 두 플레이: 쓰는 파일 블록 0 은 두 번 (지연 합 1,400), 블록 1 은 한 번 (지연 큼). 정적 파일 블록 0 은 두 번 (지연 합 20), 블록 1 은 한 번.
        LoadRefillSource first = Source([], files, Row(0, 0, 2, latencyUs: 10), Row(0, 1, 3, latencyUs: 30), Row(1, 0, 4, latencyUs: 700), Row(1, 1, 5, latencyUs: 9000));
        LoadRefillSource second = Source([], files, Row(0, 0, 2, latencyUs: 10), Row(1, 0, 4, latencyUs: 700));

        LoadRefillList plain = LoadRefillPlanner.Collect([first, second], Both, null, 1L << 30);
        LoadRefillList withWritten = LoadRefillPlanner.Collect([first, second], Both, null, 1L << 30, path => path == W);

        Assert.Equal(4, plain.Blocks.Count); // 묻지 않으면 지금과 같게 모든 블록
        Assert.Equal([(W, 0L, 2), (A, 0L, 2), (A, 1L, 1)], withWritten.Blocks.Select(b => (b.Path, b.Block, b.Count))); // 같은 점수 (플레이 수, 지연 합) 로 한 순서, 쓰는 파일의 한 번 읽힌 블록 (W 의 블록 1) 은 빠짐
    }

    [Fact]
    public void ReadsOfAPlayThatModifiedTheFileCountOnlyForAppendOnlyWrittenFilesNotForOthers()
    {
        const string W = @"D:\Games\w.bin";
        var changed = new[] { new IoSummaryFile(A, true), new IoSummaryFile(W, true) };
        LoadRefillFileStat? Both(string path) => path is A or W ? Stat : null;
        LoadRefillSource first = Source([], changed, Row(0, 0, 2), Row(1, 3, 3, latencyUs: 500)); // 두 파일 모두 이 플레이에서 수정됨
        LoadRefillSource second = Source([], [new IoSummaryFile(A), new IoSummaryFile(W)], Row(0, 0, 2), Row(1, 3, 3, latencyUs: 500));

        LoadRefillList without = LoadRefillPlanner.Collect([first, second], Both, null, 1L << 30, path => path == W);
        LoadRefillList with = LoadRefillPlanner.Collect([first, second], Both, null, 1L << 30, path => path == W, path => path == W);

        Assert.Equal([(A, 0L, 1)], without.Blocks.Select(b => (b.Path, b.Block, b.Count))); // 수정된 플레이는 빠져 W 블록은 한 플레이뿐이라 두 플레이 기준에 못 미침
        Assert.Equal([(W, 3L, 2), (A, 0L, 1)], with.Blocks.Select(b => (b.Path, b.Block, b.Count))); // 덧붙이기로 보는 W 만 두 플레이 (수정된 플레이 포함), 정적 A 는 그대로
    }

    [Fact]
    public void WithoutAWindowEachPlayScoresOnePointAndSingleTouchReadsAfterTheLoadWindowThreeAndWithAWindowThereIsNoScore()
    {
        var files = new[] { new IoSummaryFile(A), new IoSummaryFile(B) };
        // 시작 10 초: 로드 창은 10~130 초. 블록 0 창 안, 1 창 밖 (경기), 2 창 밖이지만 두 번 닿음 (시각을 나눌 수 없음), 3 첫 시작보다 앞
        LoadRefillSource first = Source([Sec(10)], files, Row(0, 0, 11), Row(0, 1, 200), new IoSummaryRow(0, 2, 2, Block, 100, 100, Sec(200), 2), Row(0, 3, 5));
        LoadRefillSource second = Source([], files, Row(0, 1, 200), Row(1, 0, 200)); // 시작 시각이 없는 요약은 구간을 모름: 1점

        LoadRefillList ranked = LoadRefillPlanner.Collect([first, second], Existing, null, 1L << 30);
        LoadRefillList loads = LoadRefillPlanner.Collect([first], Existing, TimeSpan.FromSeconds(120), 1L << 30);

        Assert.Equal([(0L, 1), (1L, 4), (2L, 1), (3L, 1)], ranked.Blocks.Where(b => b.Path == A).OrderBy(b => b.Block).Select(b => (b.Block, b.Score))); // 블록 1: 3 + 1
        Assert.Equal(1, ranked.Blocks.Single(b => b.Path == B).Score);
        Assert.All(loads.Blocks, b => Assert.Equal(0, b.Score)); // 로드 구간 목록에는 점수를 세지 않음 (목록 파일 모양이 그대로)
    }

    private static LoadRefillBlock Scored(string path, long block, int count, int score) => new(path, block, count, 10, score);

    [Fact]
    public void WithPlayOrderStoresTheScoreOrderAndTheSparseFilesOnceAndALegacyListStaysAsItIs()
    {
        var files = new Dictionary<string, LoadRefillFileStat> { [A] = Stat, [B] = Stat };
        var list = new LoadRefillList([Scored(A, 0, 3, 3), Scored(B, 0, 1, 3), Scored(A, 1, 2, 4), Scored(A, 2, 1, 1)], files, 3, 0, 0, 0, 0);
        int sparseCalls = 0;

        LoadRefillList ordered = LoadRefillPlanner.WithPlayOrder(list, path =>
        {
            sparseCalls++;
            return path == B;
        });

        Assert.Equal([2, 0, 1, 3], ordered.PlayOrder); // 점수 내림, 같으면 목록 순서
        Assert.Equal([B], ordered.SparseFiles);
        Assert.Equal(2, sparseCalls); // 파일마다 한 번

        var legacy = new LoadRefillList([Scored(A, 0, 3, 0)], files, 3, 0, 0, 0, 0);
        Assert.Same(legacy, LoadRefillPlanner.WithPlayOrder(legacy, _ => false)); // 구간을 모르는 (옛) 목록은 그대로
    }

    private LoadRefillList Stored(out string path)
    {
        var files = new Dictionary<string, LoadRefillFileStat> { [A] = Stat, [B] = Stat };
        path = Path.Combine(_dir, "j.trace.json" + LoadRefillStore.RankedSuffix);
        // 플레이 수 2 · 가중 1회 = 4점, 플레이 수 1 · 가중 1회 = 3점
        var list = LoadRefillPlanner.WithPlayOrder(new LoadRefillList([Scored(A, 3, 2, 4), Scored(B, 4, 1, 3), Scored(A, 5, 1, 1)], files, 2, 0, 0, 0, 0), p => p == B);
        LoadRefillStore.Write(path, list, Started);
        return list;
    }

    private static string Blocks(LoadRefillList? list) => string.Join(' ', list!.Blocks.Select(b => $"{b.Block}:{b.Count}:{b.Score}"));

    [Fact]
    public void ScoresAreSavedApartFromTheFourCellRowsSoAnOlderReaderStillReadsTheRanking()
    {
        LoadRefillList written = Stored(out string path);

        // 이전 판의 읽기 규칙: 필수 최상위 항목만 보고, 블록 행은 정확히 네 칸 (모르는 최상위 항목은 보지 않음)
        var root = Assert.IsType<JObject>(JsonRead.ReadFile(path));
        foreach (string key in new[] { "version", "block_size", "plays", "loads", "plays_without_starts", "skipped_nocache_blocks", "dropped_by_cap_blocks", "files", "blocks" })
        {
            Assert.True(root.ContainsKey(key), key);
        }

        Assert.All(Assert.IsType<List<object?>>(root["blocks"]), row => Assert.Equal(4, Assert.IsType<List<object?>>(row).Count));
        LoadRefillList restored = Assert.IsType<LoadRefillList>(LoadRefillStore.Read(path));
        Assert.Equal("3:2:4 4:1:3 5:1:1", Blocks(restored));
        Assert.Equal(written.PlayOrder, restored.PlayOrder);
        Assert.Equal([B], restored.SparseFiles);

        LoadRefillStore.Write(path, new LoadRefillList([new(A, 3, 2, 400)], new Dictionary<string, LoadRefillFileStat> { [A] = Stat }, 2, 0, 0, 0, 0), Started);
        Assert.DoesNotContain("play_", File.ReadAllText(path), StringComparison.Ordinal); // 점수가 없으면 파일 모양도 예전과 같음
    }

    [Theory]
    [InlineData("\"play_scores\": \"4,3,1\"", "\"play_scores\": \"4,3\"")] // 길이가 블록 수와 다름
    [InlineData("\"play_scores\": \"4,3,1\"", "\"play_scores\": \"99,3,1\"")] // 플레이 수 2 에 99점 (범위 밖: 플레이 수에 디스크 전용 요약의 최대 수를 더한 값의 3 배 초과)
    [InlineData("\"play_scores\": \"4,3,1\"", "\"play_scores\": \"1,3,1\"")] // 플레이 수 2 에 1점 (플레이 수보다 적음)
    [InlineData("\"play_scores\": \"4,3,1\"", "\"play_scores\": \"4,3,99999999999999999999\"")] // 정수 범위를 벗어난 값
    [InlineData("\"play_scores\": \"4,3,1\"", "\"play_scores\": \"4,x,1\"")] // 숫자가 아닌 값
    [InlineData("\"play_order\": \"0,1,2\"", "\"play_order\": \"0,1,1\"")] // 순열이 아님
    [InlineData("\"play_sparse\": \"1\"", "\"play_sparse\": \"9\"")] // 파일 번호 범위 밖
    public void AWrongScoreItemOnlyDropsTheScoresAndTheFourCellRowsAreStillRead(string from, string to)
    {
        _ = Stored(out string path);
        string text = File.ReadAllText(path);
        Assert.Contains(from, text, StringComparison.Ordinal);

        File.WriteAllText(path, text.Replace(from, to, StringComparison.Ordinal));
        LoadRefillList restored = Assert.IsType<LoadRefillList>(LoadRefillStore.Read(path));

        Assert.Equal("3:2:0 4:1:0 5:1:0", Blocks(restored)); // 순위는 그대로, 점수는 없음 (예전 순서)
        Assert.Null(restored.PlayOrder);
    }

    [Fact]
    public void ADiskOnlySourceOnlyAddsScoreToBlocksAlreadyListedAndNeverChangesCountLatencyOrOrder()
    {
        var files = new[] { new IoSummaryFile(A), new IoSummaryFile(B) };
        LoadRefillSource first = Source([Sec(10)], files, Row(0, 0, 11, latencyUs: 900), Row(1, 0, 200, latencyUs: 5), Row(0, 1, 300, latencyUs: 50));
        LoadRefillSource second = Source([Sec(10)], files, Row(0, 0, 12, latencyUs: 900));
        // 디스크 전용 요약: B0 은 경기 중 한 번 (3점), A5 는 목록에 없는 블록, A0 은 로드 창 안 (1점)
        LoadRefillSource diskOnly = Source([Sec(10)], files, Row(1, 0, 400), Row(0, 5, 400), Row(0, 0, 12));

        LoadRefillList without = LoadRefillPlanner.Collect([first, second], Existing, null, 1L << 30);
        LoadRefillList with = LoadRefillPlanner.Collect([first, second], Existing, null, 1L << 30, scoreOnly: [diskOnly]);
        LoadRefillList loadsWithout = LoadRefillPlanner.Collect([first, second], Existing, TimeSpan.FromSeconds(120), 1L << 30);
        LoadRefillList loadsWith = LoadRefillPlanner.Collect([first, second], Existing, TimeSpan.FromSeconds(120), 1L << 30, scoreOnly: [diskOnly]);

        Assert.Equal([(A, 0L, 2, 1800L, 2), (A, 1L, 1, 50L, 3), (B, 0L, 1, 5L, 3)], without.Blocks.Select(b => (b.Path, b.Block, b.Count, b.LatencyUs, b.Score)));
        Assert.Equal([(A, 0L, 2, 1800L, 3), (A, 1L, 1, 50L, 3), (B, 0L, 1, 5L, 6)], with.Blocks.Select(b => (b.Path, b.Block, b.Count, b.LatencyUs, b.Score))); // 블록·순서·플레이 수·지연 합은 그대로, 점수만 더해짐, A5 는 목록에 들지 않음
        Assert.Equal((without.Plays, without.Loads, without.PlaysWithoutStarts), (with.Plays, with.Loads, with.PlaysWithoutStarts));
        Assert.Equal(loadsWithout.Blocks, loadsWith.Blocks); // 로드 목록 (창이 있음) 은 이 요약이 있어도 같음
    }

    [Theory]
    [InlineData(1, 1, 0, true, true, 3)] // 한 번만, 시작 뒤 120 초 넘어 확실한 읽기: 3 점
    [InlineData(1, 1, 1, true, true, 1)] // 서비스 읽기와 겹쳐 출처가 불확실: 1 점 (빼지도 3 점으로 세지도 않음)
    [InlineData(2, 1, 0, true, true, 1)] // 두 번 닿음: 1 점
    [InlineData(1, 0, 0, true, true, 0)] // 캐시를 거치지 않은 읽기: 점수 없음
    [InlineData(1, 1, 0, false, true, 1)] // 이벤트 유실·상한 초과가 있는 회차: 구간 가중 없이 1 점
    [InlineData(1, 1, 0, true, false, 0)] // 요약을 남긴 뒤 파일 크기가 달라짐: 점수 없음
    public void DiskOnlyScoringGivesThreePointsOnlyToACertainSingleReadOutsideTheLoadWindowAndNeverDeductsForUncertainRows(long requests, long paging, long uncertain, bool exact, bool sameGeneration, int added)
    {
        LoadRefillSource listed = Source([Sec(1)], [new IoSummaryFile(A)], Row(0, 4, 2));
        var file = new IoSummaryFile(A, false, sameGeneration ? Stat.Size : Stat.Size + 1, Stat.MTimeTicks);
        IoSummary summary = Source([Sec(0)], [file], new IoSummaryRow(0, 4, requests, Block * requests, 100, 100, Sec(200), paging, uncertain)).Summary;
        var diskOnly = new LoadRefillSource(exact ? summary : summary with { Quality = summary.Quality with { EventsLost = 5 } }, [Sec(0)]);

        int before = LoadRefillPlanner.Collect([listed], Existing, null, 1L << 30).Blocks.Single().Score;
        int after = LoadRefillPlanner.Collect([listed], Existing, null, 1L << 30, scoreOnly: [diskOnly]).Blocks.Single().Score;

        Assert.Equal(before + added, after);
    }

    [Fact]
    public void ScoringChangesNeitherTheRankedOrderNorTheLoadListAndALossyPlayGetsNoPhaseWeight()
    {
        var files = new[] { new IoSummaryFile(A), new IoSummaryFile(B) };
        // 블록 A0 는 두 플레이 모두 로드 창 안 (지연 큼), 블록 B0 는 한 플레이의 경기 중 (3 점이지만 플레이 수 1)
        LoadRefillSource first = Source([Sec(10)], files, Row(0, 0, 11, latencyUs: 900), Row(1, 0, 200, latencyUs: 5));
        LoadRefillSource second = Source([Sec(10)], files, Row(0, 0, 12, latencyUs: 900));

        LoadRefillList ranked = LoadRefillPlanner.Collect([first, second], Existing, null, 1L << 30);
        LoadRefillList loads = LoadRefillPlanner.Collect([first, second], Existing, TimeSpan.FromSeconds(120), 1L << 30);

        Assert.Equal([(A, 0L, 2, 1800L, 2), (B, 0L, 1, 5L, 3)], ranked.Blocks.Select(b => (b.Path, b.Block, b.Count, b.LatencyUs, b.Score))); // 순서는 플레이 수 → 지연 합 그대로, 점수는 따로
        Assert.Equal([(A, 0L, 2, 1800L, 0)], loads.Blocks.Select(b => (b.Path, b.Block, b.Count, b.LatencyUs, b.Score))); // 로드 목록은 점수 없이 지금과 같음

        LoadRefillSource lossy = first with { Summary = first.Summary with { Quality = first.Summary.Quality with { EventsLost = 4 } } };
        LoadRefillList withLoss = LoadRefillPlanner.Collect([lossy, second], Existing, null, 1L << 30);
        Assert.Equal(1, withLoss.Blocks.Single(b => b.Path == B).Score); // 이벤트 유실이 있는 회차는 구간 가중 없이 1점
    }
}
