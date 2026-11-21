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
}
