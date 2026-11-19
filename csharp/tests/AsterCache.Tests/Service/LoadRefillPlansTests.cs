using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tracer;
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
}
