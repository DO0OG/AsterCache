using System.Security.Cryptography;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 배치까지 끝낸 계획의 작은 파일이 바뀌어도 가장 큰 파일 몇 개 (표본) 는 그대로인 상황 (게임이 셰이더 캐시를 스스로 다시 씀, 작은 파일만 고친 패치).
/// 트레이스 가변 표시 파일은 한 번 다시 계획해 계획에서 빼고, 표시 없는 파일의 변경은 업데이트로 다룹니다.
public sealed partial class AutoRelocatorTests
{
    private const string MarkedLine = "plan holds 1 file(s) the trace marked as rewritten by the game (changed between runs)";

    // 핫 파일 F1·F2 (1 MiB) 보다 큰 파일 셋 (2 MiB) 을 계획에 더해, F2 가 가장 큰 3개 표본 밖에 있게 함. exe 지문은 없음.
    private void BigFilesBesideTheSmallOnes()
    {
        var events = new List<AsterCache.Core.Trace.TraceEvent>(Sessions[0]);
        foreach ((string name, long lcn) in new[] { ("big3.bin", 11_000L), ("big4.bin", 12_000L), ("big5.bin", 13_000L) })
        {
            string path = Path.Combine(Root, name);
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(2 << 20));
            Files[path] = [new Extent(0, lcn, (2 << 20) / Cb)];
            Vol.Ext[path] = [new Extent(0, lcn, (2 << 20) / Cb)];
            events.AddRange(Run((path, 0)));
        }

        Sessions = [events];
    }

    private void MarkAsVolatile(string file)
    {
        _w.Volatile[file] = "changed between runs";
        _w.FileTimes[AutoTracer.VolatilePath(_game)] = _w.Now; // 표시 파일이 새로 쓰임
    }

    // 서비스의 배치 검사 (ReapplyChecker) 를 실제 파일·가짜 볼륨 배치로 한 번 돌려 판정만 봄 (재적용은 하지 않음).
    private ReapplyVerdict CheckLayout() => new ReapplyChecker(new ReapplyEnv
    {
        CanApply = false,
        GameRunning = _ => false,
        DiskBusy = _ => false,
        LoadPlan = PlanFile.Load,
        Extents = p => Vol.Ext[p],
        Stat = PlanFile.FingerprintOf,
        RealPath = Path.GetFullPath,
        Apply = (_, _, _, _) => throw new InvalidOperationException(),
    }, _log).Check(_game);

    [Fact]
    public void ATraceMarkedSmallFileInACompletedPlanIsPlannedAgainOnceWithoutItAndNotAgain()
    {
        BigFilesBesideTheSmallOnes();
        long start = DoneAndReadyToReplan(out _);
        Assert.Contains(PlanFile.Load(_game.Plan).Order, o => o.Path == F2);
        UpdateRewritten(F2); // 게임이 플레이 중 다시 씀: 지문이 달라짐
        MarkAsVolatile(F2);
        _w.Now += AutoRelocator.StaleCheckEvery;
        Assert.Equal(ReapplyVerdict.PlanChanged, CheckLayout()); // 시작 상태: 배치 검사는 바뀜으로 멈춰 있음

        Tick();

        Assert.True(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("source files changed since the plan", StringComparison.Ordinal)); // 업데이트로 보지 않음
        Assert.Single(_log.Lines, l => l.Contains(MarkedLine, StringComparison.Ordinal));

        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        PlanFile fresh = PlanFile.Load(_game.Plan);
        Assert.DoesNotContain(fresh.Order, o => o.Path == F2);
        Assert.DoesNotContain(fresh.Fingerprints, f => f.Path == F2);
        Assert.Equal(start, fresh.Zone.StartLcn); // 같은 존
        Assert.Equal(1L, fresh.Stats[VolatileFiles.MarksKey]);
        Assert.Equal(ReapplyVerdict.Healthy, CheckLayout()); // 다시 계획한 뒤 배치 검사는 정상 진행

        MarkAsVolatile(F2); // 표시 파일이 다시 쓰이고 서비스를 다시 시작해도 같은 이유로 되풀이하지 않음
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        _relocator = null;
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Single(_log.Lines, l => l.Contains("trace marked as rewritten by the game", StringComparison.Ordinal));
    }

    [Fact]
    public void ASmallUnmarkedFileChangedOutsideTheSampleCountsAsAnUpdate()
    {
        BigFilesBesideTheSmallOnes();
        DropRowsFromSessions();
        DoneAndReadyToReplan(out string trace);
        UpdateRewritten(F2); // 가장 큰 3개 (big3~5) 는 그대로

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Equal([F2], Assert.Single(_dropped).Order().ToArray());
        Assert.Contains(_log.Lines, l => l.Contains(OnlyChangedLine, StringComparison.Ordinal));
        Assert.Contains(trace, _w.Files); // 바뀐 파일의 행만 뺌
        Assert.True(File.Exists(_game.Plan + ".stale"));
        Assert.Equal(AutoStage.Discovered, Entry.Stage);
    }

    [Fact]
    public void AMarkedFileHeldOutOfTheLayoutIsNotAnUpdateAndNotPlannedAgain()
    {
        BigFilesBesideTheSmallOnes();
        DropRowsFromSessions();
        DoneAndReadyToReplan(out _);
        PlanFile plan = PlanFile.Load(_game.Plan);
        plan.Stats[ResidualPlanner.HeldKey] = new JObject { { F2, "unreadable" } }; // F2 는 읽지 못해 보류로 남은 파일: 다시 계획해도 계획에 남으므로 표시가 늘어도 다시 계획하지 않음 (배치 검사는 이 상태에서 PlanChanged 로 남음: 보류 파일, 후속 과제)
        plan.Save(_game.Plan);
        UpdateRewritten(F2);
        MarkAsVolatile(F2);

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Empty(_dropped);
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.False(File.Exists(_game.Plan + ".stale"));
        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("source files changed since the plan", StringComparison.Ordinal));
    }

    [Fact]
    public void WithAutoRelocationOffATraceMarkedFileDoesNotLeaveAnExtendMarkerThatBlocksReapply()
    {
        BigFilesBesideTheSmallOnes();
        DoneAndReadyToReplan(out _);
        SetAuto(a => a with { Relocate = false });
        UpdateRewritten(F2);
        MarkAsVolatile(F2);

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("trace marked as rewritten by the game", StringComparison.Ordinal));
        Assert.NotEqual(ReapplyVerdict.NotApplied, Relocator.ReapplyBlock(_game)); // 재적용이 .extend 로 막히지 않음
    }
}
