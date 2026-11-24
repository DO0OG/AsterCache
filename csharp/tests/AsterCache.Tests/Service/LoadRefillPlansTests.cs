using System.Runtime.InteropServices;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tracer;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AsterCache.Tests.Service;

/// 저장된 플레이 집계 → 로드 구간 목록 파일 → 단계 스트림용 읽기 목록: 실제 임시 폴더의 집계 파일로 만들기·저장·읽기를 시험합니다.
public sealed class LoadRefillPlansTests : IDisposable
{
    private const long Block = LoadBoundaries.Block;
    private static readonly DateTimeOffset Started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_refillplans_" + Guid.NewGuid().ToString("N"));
    private readonly string _gameFile;
    private readonly GameSettings _game;

    public LoadRefillPlansTests()
    {
        string games = Path.Combine(_dir, "games");
        string plans = Path.Combine(_dir, "plans");
        Directory.CreateDirectory(games);
        Directory.CreateDirectory(plans);
        _gameFile = Path.Combine(games, "a.bin");
        File.WriteAllBytes(_gameFile, new byte[4 * Block]);
        _game = new("cod", ["cod.exe", "sp.exe"], Path.Combine(plans, "cod.prefetch.json"), Path.Combine(plans, "cod.plan.json"), games, [games], "GAMES", 60);
        File.WriteAllText(AutoTracer.RawTracePath(_game), "[]");
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private static IoSummary Summary(string file, long block, double firstSec, IReadOnlyList<long>? starts, long? zeroUs = null) =>
        new(Started, Started.AddMinutes(5), [], new IoSummaryQuality(default, 0, default, default, default, default, default, 0, 0, 0),
            [new IoSummaryFile(file)], [new IoSummaryRow(0, block, 1, Block, 10, 10, (long)(firstSec * 1_000_000), 1)],
            RawTraceZeroUs: zeroUs, RawTraceZeroAtUtc: zeroUs is { } z ? Started.AddTicks(z * TimeSpan.TicksPerMicrosecond) : null, TargetStartsUs: starts);

    private string Run(int number)
    {
        string run = TraceWriter.RunPath(AutoTracer.RawTracePath(_game), number);
        File.WriteAllText(run, "[]");
        return run;
    }

    [Fact]
    public void RebuildCollectsRecordedStartsAndFallsBackToRunMarksForOldSummariesThenPlanReadsTheListBack()
    {
        string run1 = Run(1);
        IoSummaryStore.WriteForRun(run1, Summary(_gameFile, 0, 2, [1_000_000]));
        string run2 = Run(2);
        IoSummaryStore.WriteForRun(run2, Summary(_gameFile, 1, 4, null, zeroUs: 0)); // 옛 집계: 시작 시각이 없어 단계 표지 (3 초) 로 대신
        TraceStageIndex.WriteMarks(run2, [new StageMark("cod.exe", 0), new StageMark("sp.exe", 3000)]);

        string line = LoadRefillPlans.Rebuild(_game, Started);

        Assert.StartsWith("load refill list built from 2 plays (0 without start times), 2 loads: 2 blocks/0.1 MiB", line, StringComparison.Ordinal);
        Assert.True(File.Exists(LoadRefillPlans.PathOf(_game)));
        LoadRefillPlan plan = Assert.IsType<LoadRefillPlan>(LoadRefillPlans.Plan(_game, 1L << 30, null, default));
        Assert.Equal([new ReadOp(_gameFile, 0, 2 * Block)], plan.Ops);
        Assert.Equal((2, 2), (plan.Plays, plan.Loads));
    }

    [Fact]
    public void PlanIsNullWithoutAListAndAfterTheFileChangedOrIsMarkedVolatile()
    {
        Assert.Null(LoadRefillPlans.Plan(_game, 1L << 30, null, default)); // 목록 없음 = 지금 동작 그대로

        IoSummaryStore.WriteForRun(Run(1), Summary(_gameFile, 0, 2, [1_000_000]));
        LoadRefillPlans.Rebuild(_game, Started);
        Assert.NotNull(LoadRefillPlans.Plan(_game, 1L << 30, null, default));
        Assert.Null(LoadRefillPlans.Plan(_game, 0, null, default)); // 메모리 게이트가 허용하는 양이 0

        File.WriteAllBytes(_gameFile, new byte[5 * Block]); // 업데이트로 파일이 바뀜
        Assert.Null(LoadRefillPlans.Plan(_game, 1L << 30, null, default));

        LoadRefillPlans.Rebuild(_game, Started); // 새 파일 기준으로 다시 만들면 쓸 수 있음
        Assert.NotNull(LoadRefillPlans.Plan(_game, 1L << 30, null, default));
        File.WriteAllText(AutoTracer.VolatilePath(_game), $"{{\"{_gameFile.Replace("\\", "\\\\", StringComparison.Ordinal)}\": \"rewritten by the game\"}}");
        Assert.Null(LoadRefillPlans.Plan(_game, 1L << 30, null, default)); // 가변 파일 표시
    }

    [Fact]
    public void IsStaleIsTrueOnlyForAMissingOrOlderListWithSummariesPresent()
    {
        Func<string, DateTimeOffset?> time = p => File.Exists(p) ? File.GetLastWriteTimeUtc(p) : null;
        Assert.False(LoadRefillPlans.IsStale(_game, time)); // 집계 없음: 만들 것이 없음

        string summary = IoSummaryStore.RunPath(Run(1));
        IoSummaryStore.WriteForRun(Run(1), Summary(_gameFile, 0, 2, [1_000_000]));
        Assert.True(LoadRefillPlans.IsStale(_game, time)); // 목록 없음

        LoadRefillPlans.Rebuild(_game, Started);
        Assert.False(LoadRefillPlans.IsStale(_game, time)); // 목록이 더 새로움 (같은 시각 포함)

        File.SetLastWriteTimeUtc(summary, File.GetLastWriteTimeUtc(LoadRefillPlans.PathOf(_game)).AddMinutes(1));
        Assert.True(LoadRefillPlans.IsStale(_game, time)); // 집계가 더 새로움
    }

    private static IoSummary Rows(IReadOnlyList<IoSummaryFile> files, params IoSummaryRow[] rows) =>
        new(Started, Started.AddMinutes(5), [], new IoSummaryQuality(default, 0, default, default, default, default, default, 0, 0, 0), files, rows);

    private static IoSummaryRow Row(int file, long block, double firstSec, long paging = 1, long latencyUs = 10) =>
        new(file, block, 1, Block, latencyUs, latencyUs, (long)(firstSec * 1_000_000), paging);

    [Fact]
    public void RankedListCoversTheWholePlayAndRanksByPlayCountThenLatencyApartFromTheLoadList()
    {
        // 두 플레이: 블록 1 은 두 플레이 모두 (경기 중 = 시작 후 900 초), 블록 0 은 첫 플레이에서만 (지연 큼), 블록 2 는 둘째에서만
        IoSummaryStore.WriteForRun(Run(1), Rows([new IoSummaryFile(_gameFile)], Row(0, 1, 900, latencyUs: 5), Row(0, 0, 2, latencyUs: 700)));
        IoSummaryStore.WriteForRun(Run(2), Rows([new IoSummaryFile(_gameFile)], Row(0, 1, 800, latencyUs: 5), Row(0, 2, 1, latencyUs: 9)));

        string line = LoadRefillPlans.RebuildRanked(_game, Started);

        Assert.StartsWith("ranked block list built from 2 plays: 3 blocks/0.2 MiB", line, StringComparison.Ordinal);
        Assert.Equal([(_gameFile, 1L * Block, Block), (_gameFile, 0L, Block), (_gameFile, 2L * Block, Block)],
            LoadRefillPlans.RankedRows(_game)!.Select(r => (r.Path, r.Offset, r.Length)));
        Assert.False(File.Exists(LoadRefillPlans.PathOf(_game))); // 로드 목록과 따로: 이 호출은 로드 목록을 건드리지 않음
        Assert.NotEqual(LoadRefillPlans.PathOf(_game), LoadRefillPlans.RankedPathOf(_game));
    }

    [Fact]
    public void PlayOrderPutsBlocksReadAfterTheLoadWindowFirstWhileTheSelectionOrderStaysTheSame()
    {
        // 두 플레이 (시작 0): 블록 0 은 두 플레이 모두 로드 창 안 (2 점, 플레이 수 2), 블록 1 은 한 플레이의 경기 중 (3 점), 블록 2 는 다른 플레이의 경기 중 (3 점, 지연이 더 작음)
        IoSummaryStore.WriteForRun(Run(1), Rows([new IoSummaryFile(_gameFile)], Row(0, 0, 2, latencyUs: 700), Row(0, 1, 900, latencyUs: 50)) with { TargetStartsUs = [0] });
        IoSummaryStore.WriteForRun(Run(2), Rows([new IoSummaryFile(_gameFile)], Row(0, 0, 3, latencyUs: 700), Row(0, 2, 800, latencyUs: 9)) with { TargetStartsUs = [0] });
        _ = LoadRefillPlans.RebuildRanked(_game, Started);

        Assert.Equal([0L, 1L, 2L], LoadRefillPlans.RankedRows(_game)!.Select(r => r.Offset / Block)); // 예열 선택: 플레이 수, 지연 합 순위 그대로
        PlayRankedRows play = LoadRefillPlans.PlayRankedRows(_game)!;
        Assert.Equal([0L, 1L, 2L], play.Ranked.Select(r => r.Offset / Block));
        Assert.Equal([1L, 2L, 0L], play.ByScore.Select(r => r.Offset / Block)); // 유지·붙들기: 경기 가중 점수 순
    }

    private IoSummary DiskOnlyPlay(long fileSize, params IoSummaryRow[] rows) =>
        Rows([new IoSummaryFile(_gameFile, false, fileSize, new FileInfo(_gameFile).LastWriteTimeUtc.Ticks)], rows) with { TargetStartsUs = [0], Play = new IoSummaryPlay(true, [], []) };

    [Fact]
    public void ADiskOnlyPlaySummaryChangesOnlyTheScoreOrderAndLeavesTheLoadListAndTheRankedBlocksAsTheyWere()
    {
        IoSummaryStore.WriteForRun(Run(1), Rows([new IoSummaryFile(_gameFile)], Row(0, 0, 2, latencyUs: 700), Row(0, 1, 900, latencyUs: 50)) with { TargetStartsUs = [0] });
        IoSummaryStore.WriteForRun(Run(2), Rows([new IoSummaryFile(_gameFile)], Row(0, 0, 3, latencyUs: 700), Row(0, 2, 800, latencyUs: 9)) with { TargetStartsUs = [0] });
        string raw = AutoTracer.RawTracePath(_game);
        _ = LoadRefillPlans.Rebuild(_game, Started);
        _ = LoadRefillPlans.RebuildRanked(_game, Started);
        string loadsBefore = File.ReadAllText(LoadRefillPlans.PathOf(_game));
        LoadRefillList before = LoadRefillStore.Read(LoadRefillPlans.RankedPathOf(_game))!;
        Assert.Equal([2, 3, 3], before.Blocks.Select(b => b.Score)); // 블록 0: 두 플레이 모두 로드 창 안, 1·2: 경기 중 한 번씩
        Assert.Equal([1, 2, 0], before.PlayOrder);
        int sources = LoadRefillPlans.Sources(raw).Count;

        // 디스크 전용 요약: 블록 0 은 경기 중 확실한 한 번 (3점), 블록 2 는 로드 창 안 (1점), 블록 3 은 목록에 없음
        IoSummaryStore.WritePlayDisk(raw, DiskOnlyPlay(4 * Block, Row(0, 0, 700), Row(0, 2, 3), Row(0, 3, 600)));
        _ = LoadRefillPlans.Rebuild(_game, Started);
        _ = LoadRefillPlans.RebuildRanked(_game, Started);
        LoadRefillList after = LoadRefillStore.Read(LoadRefillPlans.RankedPathOf(_game))!;

        Assert.Equal(loadsBefore, File.ReadAllText(LoadRefillPlans.PathOf(_game))); // 초기 로드 목록은 한 바이트도 달라지지 않음
        Assert.Equal(sources, LoadRefillPlans.Sources(raw).Count); // 로드 목록·예열 순위·캐시 없이 읽는 파일의 입력에는 들어가지 않음
        Assert.Equal(before.Blocks.Select(b => (b.Path, b.Block, b.Count, b.LatencyUs)), after.Blocks.Select(b => (b.Path, b.Block, b.Count, b.LatencyUs))); // 순위 목록의 블록·순서·플레이 수·지연 합 그대로
        Assert.Equal((before.Plays, before.Loads), (after.Plays, after.Loads));
        Assert.Equal([5, 3, 4], after.Blocks.Select(b => b.Score));
        Assert.Equal([0, 2, 1], after.PlayOrder);
    }

    [Fact]
    public void RescoringAfterTheGameFileWasModifiedKeepsTheRankedBlocksAndFileGenerationsSoTheWarmOrderIsTheSameAsWithoutTheSummary()
    {
        IoSummaryStore.WriteForRun(Run(1), Rows([new IoSummaryFile(_gameFile)], Row(0, 0, 2, latencyUs: 700), Row(0, 1, 900, latencyUs: 50)) with { TargetStartsUs = [0] });
        IoSummaryStore.WriteForRun(Run(2), Rows([new IoSummaryFile(_gameFile)], Row(0, 0, 3, latencyUs: 700), Row(0, 2, 800, latencyUs: 9)) with { TargetStartsUs = [0] });
        string raw = AutoTracer.RawTracePath(_game);
        _ = LoadRefillPlans.RebuildRanked(_game, Started);
        LoadRefillList before = LoadRefillStore.Read(LoadRefillPlans.RankedPathOf(_game))!;
        long oldTicks = new FileInfo(_gameFile).LastWriteTimeUtc.Ticks;
        IoSummaryStore.WritePlayDisk(raw, DiskOnlyPlay(4 * Block, Row(0, 0, 700), Row(0, 2, 3)));

        File.WriteAllBytes(_gameFile, new byte[4 * Block]); // 요약을 저장하기 전에 게임이 파일을 고침 (수정 시각이 바뀜)
        File.SetLastWriteTimeUtc(_gameFile, new DateTime(oldTicks, DateTimeKind.Utc).AddHours(3));
        string line = LoadRefillPlans.RescoreRanked(_game, Started);
        LoadRefillList after = LoadRefillStore.Read(LoadRefillPlans.RankedPathOf(_game))!;

        Assert.StartsWith("ranked scores updated from 1 play summaries", line, StringComparison.Ordinal);
        Assert.Equal(before.Blocks.Select(b => (b.Path, b.Block, b.Count, b.LatencyUs)), after.Blocks.Select(b => (b.Path, b.Block, b.Count, b.LatencyUs)));
        Assert.Equal(before.Files, after.Files); // 지금 파일의 (바뀐) 수정 시각을 옛 블록에 새로 붙이지 않음
        Assert.Equal(oldTicks, after.Files[_gameFile].MTimeTicks);
        Assert.NotEqual(oldTicks, new FileInfo(_gameFile).LastWriteTimeUtc.Ticks);
        Assert.Empty(LoadRefillPlans.RankedRows(_game) ?? []); // 파일이 바뀌었으므로 예열 후보는 세대 대조에서 빠짐: 요약이 없을 때와 같음
        Assert.NotEqual(before.Blocks.Select(b => b.Score), after.Blocks.Select(b => b.Score));
    }

    [Fact]
    public void RescoringWithoutARankedListWithScoresTouchesNothing()
    {
        Assert.StartsWith("ranked scores not updated", LoadRefillPlans.RescoreRanked(_game, Started), StringComparison.Ordinal);
        Assert.False(File.Exists(LoadRefillPlans.RankedPathOf(_game)));
    }

    [Fact]
    public void ADiskOnlyPlayOfAChangedFileIsNotCountedAndTheStaleCheckDoesNotLookAtTheDiskSlots()
    {
        IoSummaryStore.WriteForRun(Run(1), Rows([new IoSummaryFile(_gameFile)], Row(0, 0, 2, latencyUs: 700), Row(0, 1, 900, latencyUs: 50)) with { TargetStartsUs = [0] });
        string raw = AutoTracer.RawTracePath(_game);
        _ = LoadRefillPlans.Rebuild(_game, Started);
        _ = LoadRefillPlans.RebuildRanked(_game, Started);
        int[] scores = [.. LoadRefillStore.Read(LoadRefillPlans.RankedPathOf(_game))!.Blocks.Select(b => b.Score)];
        static DateTimeOffset? WriteTime(string path) => File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        Assert.False(LoadRefillPlans.RankedIsStale(_game, WriteTime));

        string slot = IoSummaryStore.WritePlayDisk(raw, DiskOnlyPlay(3 * Block, Row(0, 0, 700), Row(0, 1, 700))); // 요약을 남긴 뒤 파일 크기가 달라졌다고 보는 요약 (크기가 지금과 다름)
        File.SetLastWriteTimeUtc(slot, DateTime.UtcNow.AddMinutes(5));

        Assert.False(LoadRefillPlans.RankedIsStale(_game, WriteTime)); // 디스크 전용 요약은 서비스 시작 때 순위를 다시 만드는 조건이 아님 (점수는 요약을 저장할 때 갱신)
        Assert.False(LoadRefillPlans.IsStale(_game, WriteTime));
        _ = LoadRefillPlans.RebuildRanked(_game, Started);
        Assert.Equal(scores, LoadRefillStore.Read(LoadRefillPlans.RankedPathOf(_game))!.Blocks.Select(b => b.Score)); // 파일 세대가 다른 요약은 점수에 들지 않음
    }

    [Fact]
    public void ThePlayOrderIsFixedWhenTheRankedFileIsWrittenSoReadingItLaterNeedsNoSortOrAttributeLookup()
    {
        IoSummaryStore.WriteForRun(Run(1), Rows([new IoSummaryFile(_gameFile)], Row(0, 0, 2, latencyUs: 700), Row(0, 1, 900, latencyUs: 50)) with { TargetStartsUs = [0] });
        int sparseLookups = 0;

        _ = LoadRefillPlans.RebuildRanked(_game, Started, path =>
        {
            sparseLookups++;
            return false;
        });
        int afterRebuild = sparseLookups;
        PlayRankedRows first = LoadRefillPlans.PlayRankedRows(_game)!;
        PlayRankedRows second = LoadRefillPlans.PlayRankedRows(_game)!;

        Assert.Equal(1, afterRebuild); // 파일마다 한 번, 순위 파일을 쓸 때만 (게임이 꺼져 있을 때)
        Assert.Equal(afterRebuild, sparseLookups); // 목록을 계산하는 호출은 속성 조회를 더하지 않음
        Assert.Equal(first.ByScore, second.ByScore); // 저장된 순서를 읽을 뿐
        Assert.Equal([1L, 0L], first.ByScore.Select(r => r.Offset / Block));
    }

    [Fact]
    public void WithoutAStoredPlayOrderThereAreNoPlayRows()
    {
        IoSummaryStore.WriteForRun(Run(1), Rows([new IoSummaryFile(_gameFile)], Row(0, 0, 2)));
        _ = LoadRefillPlans.RebuildRanked(_game, Started);
        string path = LoadRefillPlans.RankedPathOf(_game);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"play_order\"", "\"x_play_order\"", StringComparison.Ordinal)); // 점수 순서가 없는 (예전) 순위 파일

        Assert.Null(LoadRefillPlans.PlayRankedRows(_game));
        Assert.NotNull(LoadRefillPlans.RankedRows(_game)); // 순위는 그대로 읽힘
    }

    [Fact]
    public void RankedListLeavesOutNoCacheBlocksVolatileUnbufferedAndUnlistedFilesKeepsSparseBlocksAndLogsWhatItDropped()
    {
        string sparse = Path.Combine(Path.GetDirectoryName(_gameFile)!, "sparse.bin");
        string volatileFile = Path.Combine(Path.GetDirectoryName(_gameFile)!, "vol.bin");
        string unbuffered = Path.Combine(Path.GetDirectoryName(_gameFile)!, "unbuffered.bin");
        string outside = Path.Combine(_dir, "outside.bin"); // 허용 루트 밖
        foreach (string f in new[] { sparse, volatileFile, unbuffered, outside })
        {
            File.WriteAllBytes(f, new byte[4 * Block]);
        }

        var info = new FileInfo(unbuffered);
        UnbufferedFileStore.Write(UnbufferedFiles.PathOf(_game), new UnbufferedFileList(new Dictionary<string, LoadRefillFileStat> { [unbuffered] = new(info.Length, info.LastWriteTimeUtc.Ticks) }, 2), Started);

        File.WriteAllText(AutoTracer.VolatilePath(_game), $"{{\"{volatileFile.Replace("\\", "\\\\", StringComparison.Ordinal)}\": \"rewritten by the game\"}}");
        IReadOnlyList<IoSummaryFile> files = [new(_gameFile), new(sparse), new(volatileFile), new(outside), new(unbuffered)];
        IoSummaryStore.WriteForRun(Run(1), Rows(files, Row(0, 0, 2), Row(0, 1, 3, paging: 0), Row(1, 0, 2), Row(2, 0, 2), Row(3, 0, 2), Row(4, 0, 2)));

        string line = LoadRefillPlans.RebuildRanked(_game, Started, path => path == sparse);

        Assert.Equal([(_gameFile, 0L), (sparse, 0L)], LoadRefillPlans.RankedRows(_game)!.Select(r => (r.Path, r.Offset))); // 캐시 없이만 읽힌 블록·가변·캐시 없이 읽는 파일·허용 루트 밖은 빠지고, 스파스 파일의 블록은 같은 점수로 같은 목록에 듦
        Assert.Contains("2 blocks/0.1 MiB, 0.1 MiB left out as read only without the cache, 0.1 MiB in 1 sparse files", line, StringComparison.Ordinal);
    }

    [Fact]
    public void SparseBlocksAreRankedByTheSamePlayCountAndLatencyAsOrdinaryBlocksInOneList()
    {
        string sparse = Path.Combine(Path.GetDirectoryName(_gameFile)!, "sparse.bin");
        File.WriteAllBytes(sparse, new byte[4 * Block]);
        IReadOnlyList<IoSummaryFile> files = [new(_gameFile), new(sparse)];
        // 두 플레이: 일반 블록 0 은 두 플레이 (지연 작음), 스파스 블록 0 은 한 플레이 (지연 큼), 일반 블록 1 은 한 플레이 (지연 작음), 스파스 블록 1 은 한 플레이 (지연 가장 큼)
        IoSummaryStore.WriteForRun(Run(1), Rows(files, Row(0, 0, 2, latencyUs: 5), Row(1, 0, 2, latencyUs: 900), Row(0, 1, 3, latencyUs: 7), Row(1, 1, 4, latencyUs: 950)));
        IoSummaryStore.WriteForRun(Run(2), Rows(files, Row(0, 0, 2, latencyUs: 5)));

        _ = LoadRefillPlans.RebuildRanked(_game, Started, path => path == sparse);

        List<TraceEvent> ranked = LoadRefillPlans.RankedRows(_game)!;
        Assert.Equal([(_gameFile, 0L), (sparse, 1L * Block), (sparse, 0L), (_gameFile, 1L * Block)], ranked.Select(r => (r.Path, r.Offset))); // 나온 플레이 수, 그다음 지연 합: 스파스 블록이 앞이나 뒤에 통째로 몰리지 않고 점수 순으로 섞임
        List<TraceEvent> kept = PrefetchStreamer.SelectToBudget(ranked, 2 * Block, 0, 0, out _, ranked, 0);
        Assert.Equal([(_gameFile, 0L), (sparse, 1L * Block)], kept.Select(r => (r.Path, r.Offset))); // 예산이 작으면 점수가 낮은 스파스 블록과 일반 블록이 함께 밀림
    }

    [Fact]
    public void WithSparseKeepingTurnedOffTheRankedListLeavesSparseBlocksOutAsBeforeThisChange()
    {
        string sparse = Path.Combine(Path.GetDirectoryName(_gameFile)!, "sparse.bin");
        File.WriteAllBytes(sparse, new byte[4 * Block]);
        IReadOnlyList<IoSummaryFile> files = [new(_gameFile), new(sparse)];
        IoSummaryStore.WriteForRun(Run(1), Rows(files, Row(0, 0, 2), Row(1, 0, 2)));

        string line = LoadRefillPlans.RebuildRanked(_game, Started, path => path == sparse, includeSparse: false);

        Assert.Equal([_gameFile], LoadRefillPlans.RankedRows(_game)!.Select(r => r.Path)); // memory.keep_sparse 가 꺼져 있으면 예열 예산을 쓰고도 게임 중 지켜지지 않을 스파스 블록을 순위에 넣지 않음
        Assert.Contains("1 sparse files left out", line, StringComparison.Ordinal);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);

    [Fact]
    public void ForAGameWhoseMemoryPrewarmLeavesSparseFilesOutTheRankedRowsLeaveThemOutToo()
    {
        string sparse = Path.Combine(Path.GetDirectoryName(_gameFile)!, "sparse.bin");
        File.WriteAllBytes(sparse, new byte[4 * Block]);
        using (SafeFileHandle h = File.OpenHandle(sparse, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(DeviceIoControl(h, 0x900C4, 0, 0, 0, 0, out _, 0)); // FSCTL_SET_SPARSE: 시험 임시 폴더의 파일에만 씀
        }

        IReadOnlyList<IoSummaryFile> files = [new(_gameFile), new(sparse)];
        IoSummaryStore.WriteForRun(Run(1), Rows(files, Row(0, 0, 2), Row(1, 0, 2)));
        _ = LoadRefillPlans.RebuildRanked(_game, Started);

        Assert.Equal([_gameFile, sparse], LoadRefillPlans.RankedRows(_game)!.Select(r => r.Path));
        Assert.Equal([_gameFile], LoadRefillPlans.RankedRows(_game with { SkipSparse = true })!.Select(r => r.Path)); // 어차피 빠질 스파스 블록이 예산을 쓰지 않음
    }

    [Fact]
    public void RankedRowsAreNullWithoutAListAndDropFilesThatChangedAndRankedIsStaleFollowsTheSummaries()
    {
        Func<string, DateTimeOffset?> time = p => File.Exists(p) ? File.GetLastWriteTimeUtc(p) : null;
        Assert.Null(LoadRefillPlans.RankedRows(_game)); // 목록 없음 = 지금 동작 그대로
        Assert.False(LoadRefillPlans.RankedIsStale(_game, time)); // 집계 없음

        IoSummaryStore.WriteForRun(Run(1), Rows([new IoSummaryFile(_gameFile)], Row(0, 0, 2)));
        Assert.True(LoadRefillPlans.RankedIsStale(_game, time));
        LoadRefillPlans.RebuildRanked(_game, Started);
        Assert.False(LoadRefillPlans.RankedIsStale(_game, time));
        Assert.NotNull(LoadRefillPlans.RankedRows(_game));

        File.WriteAllBytes(_gameFile, new byte[5 * Block]); // 업데이트로 파일이 바뀜
        Assert.Null(LoadRefillPlans.RankedRows(_game));

        File.Delete(_gameFile); // 블록이 하나도 남지 않아도 다시 만들었음은 남김
        LoadRefillPlans.RebuildRanked(_game, Started);
        Assert.True(File.Exists(LoadRefillPlans.RankedPathOf(_game)));
        Assert.Null(LoadRefillPlans.RankedRows(_game));

        File.Delete(IoSummaryStore.RunPath(Run(1))); // 집계가 없으면 낡은 목록을 지움
        LoadRefillPlans.RebuildRanked(_game, Started);
        Assert.False(File.Exists(LoadRefillPlans.RankedPathOf(_game)));
    }

    [Fact]
    public void RankedListIsKeptEmptyWhenSummariesExistButNoneIsUsableSoServiceStartDoesNotRebuildEveryTime()
    {
        Func<string, DateTimeOffset?> time = p => File.Exists(p) ? File.GetLastWriteTimeUtc(p) : null;
        IoSummaryStore.WriteForRun(Run(1), Rows([new IoSummaryFile(_gameFile)], Row(0, 0, 2)));
        File.WriteAllText(TraceWriter.RunPath(AutoTracer.RawTracePath(_game), 1), "[ ]"); // 회차 파일이 바뀌어 집계의 지문이 맞지 않음

        string line = LoadRefillPlans.RebuildRanked(_game, Started);

        Assert.StartsWith("ranked block list built from 0 plays: 0 blocks", line, StringComparison.Ordinal);
        Assert.True(File.Exists(LoadRefillPlans.RankedPathOf(_game))); // 지우지 않고 빈 목록을 남김
        File.SetLastWriteTimeUtc(IoSummaryStore.RunPath(TraceWriter.RunPath(AutoTracer.RawTracePath(_game), 1)), File.GetLastWriteTimeUtc(LoadRefillPlans.RankedPathOf(_game)).AddSeconds(-5));
        Assert.False(LoadRefillPlans.RankedIsStale(_game, time));
        Assert.Null(LoadRefillPlans.RankedRows(_game));
    }

    [Fact]
    public void RebuildRemovesTheOldListWhenNoBlockIsLeft()
    {
        IoSummaryStore.WriteForRun(Run(1), Summary(_gameFile, 0, 2, [1_000_000]));
        LoadRefillPlans.Rebuild(_game, Started);
        Assert.True(File.Exists(LoadRefillPlans.PathOf(_game)));

        File.Delete(_gameFile);
        string line = LoadRefillPlans.Rebuild(_game, Started);

        Assert.False(File.Exists(LoadRefillPlans.PathOf(_game)));
        Assert.Contains("0 blocks/0.0 MiB", line, StringComparison.Ordinal);
    }

    [Fact]
    public void RankedListPutsGameWrittenFilesInTheSameOrderOnlyWhenAskedAndDropsThemWhenTheFileChangedAfterwards()
    {
        string written = Path.Combine(Path.GetDirectoryName(_gameFile)!, "cache_0_2.xpakdata");
        File.WriteAllBytes(written, new byte[4 * Block]);
        File.WriteAllText(AutoTracer.VolatilePath(_game), $"{{\"{written.Replace("\\", "\\\\", StringComparison.Ordinal)}\": \"opened for write\"}}");
        IReadOnlyList<IoSummaryFile> files = [new(_gameFile), new(written)];
        // 쓰는 파일의 블록 1 은 두 플레이에서, 블록 2 는 한 플레이에서만 읽힘. 정적 파일 블록 0 은 한 플레이.
        IoSummaryStore.WriteForRun(Run(1), Rows(files, Row(0, 0, 2, latencyUs: 10), Row(1, 1, 900, latencyUs: 700), Row(1, 2, 901, latencyUs: 9000)));
        IoSummaryStore.WriteForRun(Run(2), Rows(files, Row(1, 1, 800, latencyUs: 700)));

        string without = LoadRefillPlans.RebuildRanked(_game, Started);
        Assert.Equal([(_gameFile, 0L)], LoadRefillPlans.RankedRows(_game)!.Select(r => (r.Path, r.Offset))); // 끄면 (기본) 예전처럼 가변 파일은 순위 밖
        Assert.DoesNotContain("game-written", without, StringComparison.Ordinal);

        LoadRefillPlans.ForgetRanked(_game); // 마지막 예열이 읽은 순위 기억을 버려 새로 만든 목록을 읽게 함
        string line = LoadRefillPlans.RebuildRanked(_game, Started, includeWritten: true);

        Assert.Contains("2 blocks/0.1 MiB", line, StringComparison.Ordinal);
        Assert.EndsWith(", 0.1 MiB in game-written files", line, StringComparison.Ordinal);
        Assert.Equal([(written, 1L * Block), (_gameFile, 0L)], LoadRefillPlans.RankedRows(_game)!.Select(r => (r.Path, r.Offset))); // 두 플레이 읽힌 블록이 앞, 한 플레이만 읽힌 쓰는 파일 블록 2 는 빠짐

        File.WriteAllBytes(written, new byte[3 * Block]); // 게임이 다시 씀: 저장한 크기·수정 시각과 달라짐
        Assert.Equal([(_gameFile, 0L)], LoadRefillPlans.RankedRows(_game)!.Select(r => (r.Path, r.Offset)));
    }

    [Fact]
    public void RankedListCountsTheReadsOfPlaysThatModifiedAWrittenFileOnlyWhenItsSizeHistoryIsGoodAndNeverShrank()
    {
        string written = Path.Combine(Path.GetDirectoryName(_gameFile)!, "cache_0_2.xpakdata");
        File.WriteAllBytes(written, new byte[4 * Block]);
        File.WriteAllText(AutoTracer.VolatilePath(_game), $"{{\"{written.Replace("\\", "\\\\", StringComparison.Ordinal)}\": \"opened for write\"}}");
        IReadOnlyList<IoSummaryFile> files = [new(_gameFile), new(written, true)]; // 쓰는 파일은 두 플레이 모두 수정됨
        IoSummaryStore.WriteForRun(Run(1), Rows(files, Row(1, 1, 900, latencyUs: 700)));
        IoSummaryStore.WriteForRun(Run(2), Rows(files, Row(1, 1, 800, latencyUs: 700)));
        string historyPath = WrittenFileHistory.PathFor(AutoTracer.RawTracePath(_game));

        LoadRefillPlans.RebuildRanked(_game, Started, includeWritten: true); // 크기 기록 없음: 수정된 플레이는 예전처럼 뺌
        LoadRefillPlans.ForgetRanked(_game);
        Assert.Null(LoadRefillPlans.RankedRows(_game));

        var history = new WrittenFileHistory();
        foreach (string s in new[] { "s1", "s2", "s3" })
        {
            history.Record(s, [(written, 4 * Block, 7)], 0);
        }

        history.Write(historyPath);
        LoadRefillPlans.RebuildRanked(_game, Started, includeWritten: true);
        LoadRefillPlans.ForgetRanked(_game);
        Assert.Equal([(written, 1L * Block)], LoadRefillPlans.RankedRows(_game)!.Select(r => (r.Path, r.Offset))); // 줄어든 적 없음: 수정된 두 플레이의 읽기가 점수에 듦

        history.Record("s4", [(written, 2 * Block, 7)], 0); // 줄어듦
        history.Write(historyPath);
        LoadRefillPlans.RebuildRanked(_game, Started, includeWritten: true);
        LoadRefillPlans.ForgetRanked(_game);
        Assert.Null(LoadRefillPlans.RankedRows(_game));
    }
}
