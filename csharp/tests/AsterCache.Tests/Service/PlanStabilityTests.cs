using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tracer;
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
        string cache = Path.Combine(Root, "cache_1_2.xpakdata");
        File.WriteAllBytes(cache, new byte[100]);
        _w.LoadSessions = () => [[.. Sessions[0], new TraceEvent(Path.Combine(Root, "settings.json"), 0, 65536),
            new TraceEvent(cache, 90, 20), new TraceEvent(cache, 100, 10)]];

        Tick();
        RunUntilDone();

        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.DoesNotContain(plan.Fingerprints, f => f.Path == F2 || f.Path.EndsWith("settings.json", StringComparison.Ordinal));
        Assert.DoesNotContain(plan.Fingerprints, f => f.Path == cache);
        Assert.DoesNotContain(plan.Order, o => o.Path == F2);
        Assert.DoesNotContain(plan.Order, o => o.Path == cache);
        Assert.DoesNotContain(plan.Tasks, task => task.Path == cache);
        var excluded = (JObject)plan.Stats[VolatileFiles.StatsKey]!;
        Assert.Equal("changed between runs", excluded[F2]);
        Assert.Equal("volatile file name", excluded[Path.Combine(Root, "settings.json")]);
        Assert.Equal("volatile file name", excluded[cache]);
        string warmOnly = PlanOutput.WarmOnlyPath(_game.Trace);
        Assert.Contains(ReadWarmRows(warmOnly), row => row.Path == cache && row.Offset == 90 && row.Length == 20);
        Assert.DoesNotContain(ReadWarmRows(_game.Trace), row => row.Path == cache); // 가변 범위는 공용 선읽기 목록에 없음

        using FileStream shared = new(cache, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Assert.Contains(TraceLoader.LoadRanges(warmOnly, Root), row => row.Path == cache && row.Offset == 90 && row.Length == 10);
        Assert.DoesNotContain(TraceLoader.LoadRanges(warmOnly, Root), row => row.Path == cache && row.Offset >= 100);
        Assert.Contains(_log.Lines, l => l.Contains("3 volatile file(s) left out of the plan", StringComparison.Ordinal));
    }

    [Fact]
    public void NewVolatileTraceRowsRefreshPrewarmWithoutChangingACompletedPlanOrJournal()
    {
        string cache = Path.Combine(Root, "cache_1_2.xpakdata");
        File.WriteAllBytes(cache, new byte[100]);
        _w.LoadSessions = () => [[.. Sessions[0], new TraceEvent(cache, 80, 10)]];

        Tick();
        RunUntilDone();

        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.DoesNotContain(plan.Tasks, task => task.Path == cache);
        byte[] planBefore = File.ReadAllBytes(_game.Plan);
        byte[] journalBefore = File.ReadAllBytes(JournalPath);
        int movesBefore = Vol.Moves.Count;
        string raw = AutoTracer.RawTracePath(_game);
        SetAuto(a => a with { TraceRuns = 3, TraceMergeMinMib = 1 });
        _w.Files.Add(TraceWriter.RunPath(raw, 1));
        _w.Files.Add(TraceWriter.RunPath(raw, 2));
        _w.FileTimes[_game.Plan] = _w.Now - TimeSpan.FromHours(1);
        _w.FileTimes[raw] = _w.Now;
        _w.LoadSessions = () => [[.. Sessions[0], new TraceEvent(cache, 80, 10), new TraceEvent(cache, 10, 10)]];

        Tick();

        Assert.Contains(ReadWarmRows(PlanOutput.WarmOnlyPath(_game.Trace)), row => row.Path == cache && row.Offset == 10 && row.Length == 10);
        Assert.DoesNotContain(ReadWarmRows(_game.Trace), row => row.Path == cache);
        Assert.Equal(planBefore, File.ReadAllBytes(_game.Plan));
        Assert.Equal(journalBefore, File.ReadAllBytes(JournalPath));
        Assert.Equal(movesBefore, Vol.Moves.Count);
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
        _w.MakePlanAt = (_, _, _, _, _) => throw new PyValueException("zone overlaps hot data in a cycle; choose another zone");
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
        _w.MakePlanAt = (sessions, origin, zoneMib, lcn, used) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], zoneStartLcn: lcn, preserveBytes: 64 << 20, fixedClusters: used);
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

    [Fact]
    public void FailedGameWhoseTraceIsOlderThanTheFailureWithOnlyAStalePlanGetsItBackOnRestart()
    {
        DoneAndReadyToReplan(out string trace);
        string before = File.ReadAllText(_game.Plan);
        File.Move(_game.Plan, _game.Plan + ".stale"); // 재계획이 실패해 계획은 .stale 로만 남은 채 failed
        _w.FileTimes[trace] = _w.Now - TimeSpan.FromHours(1); // 트레이스가 실패보다 먼저 쓰였고 새 트레이스도 없음
        _status.ResetForReplan("bench", "x");
        _status.Set("bench", "auto", null, AutoStage.Failed, "plan failed: PyValueException");
        _w.Now += Step;
        _relocator = null; // 서비스 재시작

        Tick();

        Assert.Equal((AutoStage.Done, AutoRelocator.StalePlanDetail), (Entry.Stage, Entry.Detail));
        Assert.NotNull(Entry.RelocatedAt);
        Assert.Equal(before, File.ReadAllText(_game.Plan));
        Assert.False(File.Exists(_game.Plan + ".stale"));
    }

    [Fact]
    public void FailedGameWithoutAStalePlanIsRetriedOncePerServiceRunEvenIfTheTraceIsOlder()
    {
        int attempts = 0;
        _w.FileTimes[AutoTracer.RawTracePath(_game)] = _w.Now - TimeSpan.FromHours(1);
        Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, PlanFile> working = _w.MakePlan;
        _w.MakePlan = (_, _, _) =>
        {
            attempts++;
            throw new IOException("disk");
        };
        _status.Set("bench", "auto", null, AutoStage.Failed, "plan failed: IOException"); // 이전 실행의 실패

        Tick();
        for (int i = 0; i < 3; i++)
        {
            _w.Now += Step;
            Tick();
        }

        Assert.Equal(1, attempts); // 이번 실행에서 한 번만

        _relocator = null; // 서비스 재시작: 다시 한 번
        _w.MakePlan = working;
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
    }

    [Fact]
    public void RestoredStalePlanIsReplannedAfterRestartFromTheSameTraceWithoutSettingItAside()
    {
        DoneAndReadyToReplan(out string trace);
        Update();
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        NewTraceFromNextRun();
        _w.MakePlanAt = (_, _, _, _, _) => throw new PyValueException("zone overlaps hot data in a cycle; choose another zone");
        Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, PlanFile> working = _w.MakePlan;
        _w.MakePlan = (sessions, origin, zoneMib) => zoneMib == ZoneSize.ProbeMib ? working(sessions, origin, zoneMib) : throw new PyValueException("zone overlaps hot data in a cycle; choose another zone");
        Tick();
        Assert.Equal((AutoStage.Done, AutoRelocator.StalePlanDetail), (Entry.Stage, Entry.Detail));
        _w.FileTimes.Remove(trace);
        _w.Files.Add(trace);
        _relocator = null; // 서비스 재시작: 실패 시각 기억이 없음

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.True(_w.Files.Contains(trace) || File.Exists(trace)); // 트레이스는 그대로
        Assert.Contains(_log.Lines, l => l.Contains("trace updated after a failed replan", StringComparison.Ordinal));
    }

    [Fact]
    public void OldPlanWithAVolatileFileInItsOrderIsPlannedAgainOncePerRunWithoutItInTheSameZone()
    {
        long start = DoneAndReadyToReplan(out _);
        Vol.Moves.Clear();
        PlanFile old = PlanFile.Load(_game.Plan);
        (old with { Order = [.. old.Order, new PlanRange(Path.Combine(Root, "settings.json"), 0, 4)] }).Save(_game.Plan); // 옛 계획: excluded_files 키가 없고 핫 순서에 가변 파일이 들어 있음
        Assert.False(PlanFile.Load(_game.Plan).Stats.ContainsKey(VolatileFiles.StatsKey));
        _relocator = null; // 서비스 재시작 (이번 실행의 확인은 한 번)

        Tick(); // 같은 틱에 다시 계획해 유휴를 기다림

        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
        Assert.True(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Contains(_log.Lines, l => l.Contains("plan holds files the game rewrites by itself", StringComparison.Ordinal));

        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        PlanFile fresh = PlanFile.Load(_game.Plan);
        Assert.DoesNotContain(fresh.Order, o => VolatileFiles.Ignored(o.Path));
        Assert.Equal(start, fresh.Zone.StartLcn); // 존 시작 유지
        Assert.Empty(fresh.Tasks); // 남은 블록은 순서 그대로 제자리, 제거된 파일의 자리는 비워 둠
        Assert.Empty(Vol.Moves);
        _relocator = null; // 서비스 재시작
        Tick();
        Assert.Equal(AutoStage.Done, Entry.Stage); // 새 계획에는 가변 파일이 없어 되풀이하지 않음
        Assert.Single(_log.Lines, l => l.Contains("plan holds files the game rewrites by itself", StringComparison.Ordinal));
    }
}
