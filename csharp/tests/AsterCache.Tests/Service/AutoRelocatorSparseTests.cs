using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Service;

/// 자동 재배치가 스파스 파일을 다루는 방식: 계획·이동·가변 파일 제외·할당 변화 보류와 다시 계획.
public sealed partial class AutoRelocatorTests
{
    private void PlanF2AsSparse()
    {
        _w.MakePlan = (sessions, origin, zoneMib) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], preserveBytes: 64 << 20, sparseFn: p => p == F2);
        _w.MakePlanAt = (sessions, origin, zoneMib, lcn, used) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], zoneStartLcn: lcn, preserveBytes: 64 << 20, fixedClusters: used, sparseFn: p => p == F2);
    }

    [Fact]
    public void ASparseFileIsPlannedWithItsAllocationAndMovedInWholeUnits()
    {
        PlanF2AsSparse();

        Tick();
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.Equal([new AllocatedRange(0, 256)], plan.SparseAllocations[F2]);
        Assert.Contains(plan.Tasks, t => t.Path == F2);
        Assert.All(Vol.Moves.Where(m => m.Path == F2), m => Assert.True(m.Vcn % 16 == 0 && m.N % 16 == 0));
        Assert.Contains(_log.Lines, l => l.Contains("auto relocation moved", StringComparison.Ordinal) && l.Contains(", sparse moved ", StringComparison.Ordinal));
    }

    [Fact]
    public void ASparseFileMarkedVolatileIsStillLeftOutOfThePlan()
    {
        PlanF2AsSparse();
        _w.Volatile[F2] = "changed between runs";

        Tick();
        RunUntilDone();

        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.DoesNotContain(plan.Order, o => o.Path == F2);
        Assert.DoesNotContain(plan.Tasks, t => t.Path == F2);
        Assert.DoesNotContain(plan.Fingerprints, f => f.Path == F2);
        Assert.Empty(plan.SparseAllocations);
        Assert.DoesNotContain(Vol.Moves, m => m.Path == F2);
    }

    [Fact]
    public void AllocationThatChangesAfterPlanningHoldsTheFileDoesNotCompleteAndPlansAgainLater()
    {
        PlanF2AsSparse();
        var apply = _w.Apply;
        bool changed = false;
        _w.Apply = (g, plan, options, ct) =>
        {
            if (!changed)
            {
                changed = true;
                Vol.Ext[F2] = [new Extent(128, 9000, 128)]; // 계획을 세운 뒤 첫 적용 직전에 앞쪽 할당 구간이 해제됨
            }

            return apply(g, plan, options, ct);
        };

        Run(TimeSpan.FromMinutes(5));

        Assert.NotEqual(AutoStage.Done, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
        Assert.DoesNotContain(Vol.Moves, m => m.Path == F2);
        Assert.Contains(_log.Lines, l => l.Contains("1 sparse file(s) changed allocation since the plan", StringComparison.Ordinal));

        _w.Now += AutoRelocator.UnreadableRetry + TimeSpan.FromMinutes(1); // 보류가 끝나면 계획을 낡은 것으로 치우고 지금 할당 구간으로 다시 계획
        RunUntilDone(80);

        Assert.Contains(_log.Lines, l => l.Contains("held files remain, plan set aside as", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("zone region appears occupied", StringComparison.Ordinal)); // 존 점유 문구는 존 점유 때만
        Assert.Equal(AutoStage.Done, Entry.Stage);
        PlanFile again = PlanFile.Load(_game.Plan);
        Assert.Equal([new AllocatedRange(128, 128)], again.SparseAllocations[F2]);
        Assert.All(again.Order.Where(o => o.Path == F2), o => Assert.True(o.Vcn >= 128));
    }

    // 스파스 파일의 지원 구간 밖 블록이 많아도 확장 판정은 추적의 블록 수와 비교하므로, 같은 추적이 병합돼도 다시 계획하지 않음.
    [Fact]
    public void MergedTraceWithBlocksOutsideTheSupportedRangesDoesNotReplanAGameWithSparseFiles()
    {
        Vol.Ext[F1] = [new Extent(0, 1000, 64)]; // 나머지 구간은 구멍
        Vol.Ext[F2] = [new Extent(0, 7000, 16)];
        _w.MakePlan = (sessions, origin, zoneMib) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], preserveBytes: 64 << 20, sparseFn: _ => true);
        _w.MakePlanAt = (sessions, origin, zoneMib, lcn, used) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], zoneStartLcn: lcn, preserveBytes: 64 << 20, fixedClusters: used, sparseFn: _ => true);
        Sessions = [Run([.. Enumerable.Range(0, 16).SelectMany(i => new[] { (F1, (long)i), (F2, (long)i) })])];
        Tick();
        _w.Now += Step;
        RunUntilDone();
        Assert.Equal(AutoStage.Done, Entry.Stage);
        PlanFile planned = PlanFile.Load(_game.Plan);
        Assert.Equal(32L, planned.Stats["hot_blocks"]);
        Assert.True((long)planned.Stats["supported_hot_blocks"]! < 32);
        string raw = AutoTracer.RawTracePath(_game);
        SetAuto(a => a with { TraceRuns = 3, TraceMergeMinMib = 1, PostRelocateRecovery = AutoSettings.RecoveryOff });
        _w.Files.Add(TraceWriter.RunPath(raw, 1));
        _w.Files.Add(TraceWriter.RunPath(raw, 2));
        _w.FileTimes[_game.Plan] = _w.Now - TimeSpan.FromHours(1);
        _w.FileTimes[raw] = _w.Now;
        Vol.Moves.Clear();

        Tick();
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.True(File.Exists(_game.Plan));
        Assert.Empty(Vol.Moves);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("extend", StringComparison.OrdinalIgnoreCase));
    }
}
