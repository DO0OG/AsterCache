using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Trace;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class VolatileFilesTests
{
    [Theory]
    [InlineData(@"D:\g\Cache\x.bin", null)]
    [InlineData(@"D:\g\cache_index.dat", "volatile file name")]
    [InlineData(@"D:\g\Settings_user.bin", "volatile file name")]
    [InlineData(@"D:\g\a.LOG", "volatile file name")]
    [InlineData(@"D:\g\a.yaml", "volatile file name")]
    [InlineData(@"D:\g\a.ini", "volatile file name")]
    [InlineData(@"D:\g\a.json", "volatile file name")]
    [InlineData(@"D:\g\a.txt", "volatile file name")]
    [InlineData(@"D:\g\a.cfg", "volatile file name")]
    [InlineData(@"D:\g\slot1.sav", "volatile file name")]
    [InlineData(@"D:\g\data.pak", null)]
    [InlineData(@"D:\g\catalog.bin", null)]
    public void NameRuleCatchesCacheSettingsAndTheSavedStateExtensionsOnly(string path, string? reason)
    {
        Assert.Equal(reason, VolatileFiles.NameReason(path));
    }
}

/// 계획 안정성: 가변 파일 제외, 재계획 실패 때 낡은 계획 복원, 실패 뒤 새 트레이스로 다시 시도.
public sealed partial class AutoRelocatorTests
{
    [Fact]
    public void VolatileFilesAreLeftOutOfThePlanAndTheirReasonsAreKeptInTheStats()
    {
        _w.Volatile[F2] = "changed between runs";
        _w.LoadSessions = () => [[.. Sessions[0], new TraceEvent(Path.Combine(Root, "settings.json"), 0, 65536)]];

        Tick();
        RunUntilDone();

        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.DoesNotContain(plan.Fingerprints, f => f.Path == F2 || f.Path.EndsWith("settings.json", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Order, o => o.Path == F2);
        var excluded = (JObject)plan.Stats[VolatileFiles.StatsKey]!;
        Assert.Equal("changed between runs", excluded[F2]);
        Assert.Equal("volatile file name", excluded[Path.Combine(Root, "settings.json")]);
        Assert.Contains(_log.Lines, l => l.Contains("2 volatile file(s) left out of the plan", StringComparison.Ordinal));
    }

    [Fact]
    public void ReplanThatFailsEvenWithoutThePreviousZoneRestoresTheStalePlanAndStaysDoneUntilANewerTrace()
    {
        DoneAndReadyToReplan(out string trace);
        string before = File.ReadAllText(_game.Plan);
        Update();
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        Assert.False(File.Exists(_game.Plan));
        NewTraceFromNextRun();
        int planAttempts = 0;
        Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, PlanFile> working = _w.MakePlan;
        _w.MakePlanAt = (_, _, _, _) => throw new PyValueException("zone overlaps hot data in a cycle; choose another zone");
        _w.MakePlan = (sessions, origin, zoneMib) =>
        {
            if (zoneMib == ZoneSize.ProbeMib)
            {
                return working(sessions, origin, zoneMib); // 탐색 계획은 됨
            }

            planAttempts++;
            throw new PyValueException("zone overlaps hot data in a cycle; choose another zone");
        };

        Tick();

        Assert.Equal(1, planAttempts);
        Assert.Equal((AutoStage.Done, AutoRelocator.StalePlanDetail), (Entry.Stage, Entry.Detail));
        Assert.NotNull(Entry.RelocatedAt);
        Assert.Equal(before, File.ReadAllText(_game.Plan)); // .stale 가 원래 이름으로 돌아옴
        Assert.False(File.Exists(_game.Plan + ".stale"));
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("Error", StringComparison.Ordinal));

        for (int i = 0; i < 3; i++) // 같은 트레이스로는 되풀이하지 않음
        {
            _w.Now += AutoRelocator.StaleCheckEvery;
            Tick();
        }

        Assert.Equal(1, planAttempts);
        Assert.Equal(AutoStage.Done, Entry.Stage);

        // 트레이스가 새로 쓰이면 (다음 플레이) 그 트레이스를 지우지 않고 다시 계획함
        _w.FileTimes[trace] = _w.Now + TimeSpan.FromMinutes(1);
        _w.MakePlan = working;
        _w.MakePlanAt = (sessions, origin, zoneMib, lcn) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], zoneStartLcn: lcn, preserveBytes: 64 << 20);
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        Assert.True(_w.Files.Contains(trace) || File.Exists(trace)); // 새 버전의 트레이스라 치우지 않음
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotEqual(AutoRelocator.StalePlanDetail, Entry.Detail);
        Assert.Contains(_log.Lines, l => l.Contains("trace updated after a failed replan", StringComparison.Ordinal));
    }

    [Fact]
    public void FailedPlanIsRetriedOnlyAfterTheTraceIsWrittenAgain()
    {
        int attempts = 0;
        _w.FileTimes[AutoTracer.RawTracePath(_game)] = _w.Now - TimeSpan.FromHours(1);
        Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, PlanFile> working = _w.MakePlan;
        _w.MakePlan = (_, _, _) =>
        {
            attempts++;
            throw new IOException("disk");
        };

        Tick();
        Assert.Equal((AutoStage.Failed, "plan failed: IOException"), (Entry.Stage, Entry.Detail));
        for (int i = 0; i < 3; i++)
        {
            _w.Now += Step;
            Tick();
        }

        Assert.Equal(1, attempts); // 같은 트레이스로는 되풀이하지 않음

        _w.FileTimes[AutoTracer.RawTracePath(_game)] = _w.Now + TimeSpan.FromMinutes(1); // 다음 플레이가 트레이스를 다시 씀
        _w.MakePlan = working;
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.True(File.Exists(_game.Plan));
    }

    [Fact]
    public void UnreadablePlanFileHoldsTheFirstRelocationInsteadOfPlanningAgain()
    {
        Tick(); // 계획을 만들고 대기
        Assert.True(File.Exists(_game.Plan));
        string locked = PlanFile.Load(_game.Plan).Fingerprints[0].Path;
        int planZones = _w.PlanZones.Count;
        _w.Stat = p => p == locked ? throw new IOException("locked") : PlanFile.FingerprintOf(p);

        Run(TimeSpan.FromMinutes(5));

        Assert.Equal(planZones, _w.PlanZones.Count); // 재계획하지 않음
        Assert.Empty(Vol.Moves);
        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Contains(_log.Lines, l => l.Contains("plan files unreadable, will retry", StringComparison.Ordinal));

        _w.Stat = PlanFile.FingerprintOf; // 풀리면 이어서 재배치
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
    }
}
