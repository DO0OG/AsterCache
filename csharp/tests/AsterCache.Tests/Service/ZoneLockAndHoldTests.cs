using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 서비스 쪽 처리: 잠금 busy·계획 변경 거절은 완료도 실패도 아닌 "다음에 다시", 시작 복구의 한 게임 거절이 뒤 게임을 막지 않음,
/// 보류 파일의 승계와 미완료 계획의 완료 복원 금지, 일시 사유와 검증 오류의 분리 (검증 오류도 저빈도로 다시 평가).
public sealed partial class AutoRelocatorTests
{
    private const string PlanChanged = "plan changed while waiting for the zone lock";

    private static ApplyReport BusyReport() => new() { Stopped = DataLock.BusyReason };

    private void PartialState()
    {
        Assert.Equal("write_cap", Apply(Make().Resolve(20000), Partial(32)).Stopped);
        Make().Resolve(30000).Save(_game.Plan);
        WaitingForTheRest();
    }

    // 틱을 돌리다 조건이 참이 되는 틱에서 멈춤 (그 틱의 상태를 바로 봄).
    private void TickUntil(Func<bool> hit, int maxTicks = 80)
    {
        for (int i = 0; i < maxTicks && !hit(); i++)
        {
            Tick();
            if (!hit())
            {
                _w.Now += Step;
            }
        }

        Assert.True(hit(), "the expected call never happened");
    }

    [Fact]
    public void ABusyDryRunIsRetriedLaterAndNeverCompletedAsAlreadyInPlace()
    {
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        int busy = 0;
        _w.Apply = (g, plan, options, ct) => options.DryRun && busy++ < 2 ? BusyReport() : inner(g, plan, options, ct);

        TickUntil(() => busy == 1);

        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Contains("zone lock", Entry.Detail, StringComparison.Ordinal);
        Assert.Null(Entry.RelocatedAt);
        Assert.Empty(Vol.Moves);

        RunUntilDone(120);

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.True(busy >= 3); // 두 번 밀린 뒤 세 번째 dry-run 이 실제로 봄
        Assert.Equal(80, Vol.Moves.Sum(m => m.N));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("already in place", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("layout already in place", StringComparison.Ordinal));
    }

    [Fact]
    public void ABusyExecuteMovesNothingDoesNotUseUpTheDailyCountAndIsRetried()
    {
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        int busy = 0;
        _w.Apply = (g, plan, options, ct) => options.Execute && busy++ < 2 ? BusyReport() : inner(g, plan, options, ct);

        TickUntil(() => busy == 1);

        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Contains("zone lock", Entry.Detail, StringComparison.Ordinal);
        Assert.Equal(0, _status.RelocationsWithin(TimeSpan.FromDays(1)));
        Assert.Equal(0, _status.RelocationBytesWithin(TimeSpan.FromDays(1)));
        Assert.Empty(Vol.Moves);

        RunUntilDone(120);

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.Equal(80, Vol.Moves.Sum(m => m.N));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void APlanChangedRejectionWhileWaitingForTheLockIsRetriedNotFailed(bool dryRun)
    {
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        int rejected = 0;
        _w.Apply = (g, plan, options, ct) => options.DryRun == dryRun && rejected++ < 2 ? throw new PlanRejectedException(PlanChanged) : inner(g, plan, options, ct);

        for (int i = 0; i < 120 && _status.Get("bench")?.Stage != AutoStage.Done; i++)
        {
            Tick();
            _w.Now += Step;
            Assert.NotEqual(AutoStage.Failed, _status.Get("bench")?.Stage);
        }

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.True(rejected >= 3);
        Assert.Equal(80, Vol.Moves.Sum(m => m.N));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("already in place", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AStartupSwitchRejectedForOneGameDoesNotSkipTheNextGameAndIsRetriedLater(bool busy)
    {
        _ = AddOtherGameWithAPlanAt(40000, Letter);
        var calls = new List<string>();
        int failures = 1;
        _w.ResumeSwitch = g =>
        {
            calls.Add(g.Name);
            return g.Name == "bench" && failures-- > 0
                ? throw new PlanRejectedException(busy ? DataLock.BusyMessage : "plan switch record is stale: the active plan changed after it was recorded")
                : null;
        };

        Tick();

        Assert.Equal(["bench", "other"], calls); // 첫 게임이 거절돼도 둘째 게임의 시작 복구는 진행됨

        _w.Now += Step;
        Tick();

        if (busy)
        {
            Assert.Equal(["bench", "other", "bench"], calls); // busy 는 다음 틱에 그 게임만 다시
        }
        else
        {
            Assert.Equal(["bench", "other"], calls); // 세대 거절은 BusyRetry 전에는 되풀이하지 않음
            _w.Now += AutoRelocator.BusyRetry;
            Tick();
            Assert.Equal(["bench", "other", "bench"], calls);
        }

        _w.Now += AutoRelocator.BusyRetry;
        Tick();
        Tick();
        Assert.Equal(3, calls.Count); // 풀린 뒤에는 더 부르지 않음
    }

    // 모든 파일 또는 일부 파일의 extent 를 읽을 수 없는 세계: 후보 계획, 잔여 재계획, 배치 조회가 같은 판정을 씀.
    private void UseUnreadable(Func<string, bool> isReadable)
    {
        Func<string, IReadOnlyList<Extent>?> ext = p => isReadable(p) ? Vol.Ext[p] : null;
        var hooks = new ApplyHooks { Extents = ext, Digest = (p, size, extra) => SampleHasher.Compute(p, size, extra, null), SkipReason = _ => null };
        _w.Extents = ext;
        _w.ReplanResidual = (g, c, o, sessions) => ResidualReplan.Run(c, g.Plan, JournalPath, Vol, _w.Reserved(g), hooks, () => _w.Now.UtcDateTime,
            sessions is null ? null : (p, path) => PlanOutput.Save(p, sessions, path), o).Plan;
        _w.MakePlan = (sessions, origin, zoneMib) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, ext, preserveBytes: 64 << 20);
        _w.MakePlanAt = (sessions, origin, zoneMib, lcn, used) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, ext, zoneStartLcn: lcn, preserveBytes: 64 << 20, fixedClusters: used);
    }

    private JObject HeldOf(PlanFile plan) => Assert.IsType<JObject>(plan.Stats["held_files"]);

    [Fact]
    public void FilesStillUnreadableAfterTheHoldStayHeldAndTheRestoredPlanIsNeverMarkedDone()
    {
        PartialState();
        long moved = Vol.Moves.Sum(m => m.N);
        Vol.Moves.Clear();
        bool readable = false;
        UseUnreadable(_ => readable);

        Run(TimeSpan.FromMinutes(6));

        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Equal(2, HeldOf(PlanFile.Load(_game.Plan)).Count);

        for (int round = 0; round < 3; round++)
        {
            _w.Now += AutoRelocator.UnreadableRetry; // 보류가 끝나도 여전히 읽을 수 없음: 후보에서 모두 빠져 계획이 실패하고 이전 계획이 복원됨
            Run(TimeSpan.FromMinutes(4));

            Assert.Equal(AutoStage.WaitingIdle, Entry.Stage); // 완료로 복원하지 않고 다시 보류
            Assert.Null(Entry.RelocatedAt);
            Assert.Contains("unreadable", Entry.Detail, StringComparison.Ordinal);
            Assert.Equal(2, HeldOf(PlanFile.Load(_game.Plan)).Count);
            Assert.Empty(Vol.Moves); // 이미 옮긴 조각을 다시 옮기지 않음
        }

        readable = true;
        _w.Now += AutoRelocator.UnreadableRetry;
        Run(TimeSpan.FromMinutes(4));
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.Equal(80 - moved, Vol.Moves.Sum(m => m.N)); // 읽히게 된 뒤에는 정상 처리: 잔여만
        Assert.Equal(0, Remaining(PlanFile.Load(_game.Plan)));
        Assert.False(PlanFile.Load(_game.Plan).Stats.ContainsKey("held_files"));
    }

    [Fact]
    public void AFileStillUnreadableAfterTheHoldIsInheritedByTheNewCandidateWithItsFingerprintAndTargets()
    {
        PartialState();
        long moved = Vol.Moves.Sum(m => m.N);
        Assert.Contains(Vol.Moves, m => m.Path == F2); // 읽지 못하게 될 파일의 조각이 이미 옮겨져 있음
        Vol.Moves.Clear();
        bool f2Readable = false;
        UseUnreadable(p => p != F2 || f2Readable);

        Run(TimeSpan.FromMinutes(6));
        RunUntilDone(60);

        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage); // F1 의 잔여는 옮겼으나 F2 가 보류로 남아 완료로 확정하지 않음
        Assert.Contains("unreadable", Entry.Detail, StringComparison.Ordinal);
        Assert.Equal([F2], HeldOf(PlanFile.Load(_game.Plan)).Select(kv => kv.Key).ToList());
        int movesAfterF1 = Vol.Moves.Count;
        Assert.DoesNotContain(Vol.Moves, m => m.Path == F2);

        _w.Now += AutoRelocator.UnreadableRetry; // 보류가 끝나 다시 계획: 후보는 F2 를 읽지 못해 Order 에서 뺌
        Run(TimeSpan.FromMinutes(4));

        PlanFile again = PlanFile.Load(_game.Plan);
        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
        Assert.Equal([F2], HeldOf(again).Select(kv => kv.Key).ToList()); // 보류가 사라지지 않음
        Assert.Contains(again.Order, o => o.Path == F2);
        Assert.Contains(again.Fingerprints, f => f.Path == F2);
        Assert.Contains(Assert.IsType<List<object?>>(again.Stats["held_targets"]).OfType<JObject>(), r => (string)r["path"]! == F2); // 이미 옮긴 조각의 목표도 승계
        Assert.Equal(movesAfterF1, Vol.Moves.Count);

        f2Readable = true; // 읽히게 된 뒤에는 정상 처리: 이미 옮긴 조각은 재이동 0, 남은 조각만
        _w.Now += AutoRelocator.UnreadableRetry;
        Run(TimeSpan.FromMinutes(4));
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.Equal(80 - moved, Vol.Moves.Sum(m => m.N));
        Assert.Equal(0, Remaining(PlanFile.Load(_game.Plan)));
        Assert.False(PlanFile.Load(_game.Plan).Stats.ContainsKey("held_files"));
    }

    [Theory]
    [InlineData("zone lock busy: another apply or plan switch is running")]
    [InlineData("unverified journal entry unreadable: f1.bin: The process cannot access the file")]
    [InlineData("no free run of 48 clusters")]
    [InlineData(PlanChanged)]
    public void TemporaryReasonsOfTheRemainderReplanAreRetriedWithoutEverStoppingAndFinishOnceTheyClear(string message)
    {
        PartialState();
        Func<GameSettings, PlanFile, IReadOnlyList<PlanFile>, IReadOnlyList<IReadOnlyList<AsterCache.Core.Trace.TraceEvent>>?, PlanFile> inner = _w.ReplanResidual!;
        int rejects = 4, calls = 0;
        _w.ReplanResidual = (g, c, o, s) => ++calls <= rejects ? throw new PlanRejectedException(message) : inner(g, c, o, s);

        for (int round = 0; round < rejects; round++)
        {
            Run(TimeSpan.FromMinutes(4));

            Assert.NotEqual(AutoStage.Failed, Entry.Stage); // 같은 사유가 되풀이돼도 실패로 멈추지 않음
            Assert.Null(_status.Recovery("bench")); // 30 분 보류·재계획 횟수에도 세지 않음
            _w.Now += AutoRelocator.ResourceRetry + TimeSpan.FromMinutes(1);
        }

        Run(TimeSpan.FromMinutes(4));
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.True(calls > rejects);
        Assert.Equal(0, Remaining(PlanFile.Load(_game.Plan)));
    }

    [Fact]
    public void AStoppedVerificationErrorIsEvaluatedAgainEveryHourEvenWithoutANewTraceAndFinishesOnceTheCauseIsGone()
    {
        PartialState();
        _w.AntiCheats[Root] = "eac"; // 트레이스를 다시 만들 수 없는 게임: 새 트레이스가 오지 않음
        Func<GameSettings, PlanFile, IReadOnlyList<PlanFile>, IReadOnlyList<IReadOnlyList<AsterCache.Core.Trace.TraceEvent>>?, PlanFile> inner = _w.ReplanResidual!;
        int residuals = 0;
        bool cleared = false;
        _w.ReplanResidual = (g, c, o, s) =>
        {
            residuals++;
            return cleared ? inner(g, c, o, s) : throw new PlanRejectedException("unverified journal entry changed: " + F1);
        };

        Run(TimeSpan.FromMinutes(4));
        _w.Now = _status.Recovery("bench")!.NextAllowed!.Value + TimeSpan.FromMinutes(1);
        Run(TimeSpan.FromMinutes(4));

        Assert.Equal((2, AutoStage.Failed), (residuals, Entry.Stage)); // 같은 사유 두 번이면 멈춤

        _w.Now += TimeSpan.FromMinutes(40);
        Run(TimeSpan.FromMinutes(4));
        Assert.Equal((2, AutoStage.Failed), (residuals, Entry.Stage)); // 한 시간 전에는 다시 평가하지 않음

        _w.Now += TimeSpan.FromMinutes(30);
        Run(TimeSpan.FromMinutes(4));
        Assert.Equal((3, AutoStage.Failed), (residuals, Entry.Stage)); // 한 시간 뒤 사용자 조작·새 트레이스 없이 다시 평가 (원인이 그대로라 다시 멈춤)

        cleared = true;
        _w.Now += AutoRelocator.StoppedRecheck + TimeSpan.FromMinutes(1);
        Run(TimeSpan.FromMinutes(4));
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why()); // 원인이 사라지면 자동으로 재개되어 끝남
        Assert.Equal(0, Remaining(PlanFile.Load(_game.Plan)));
    }

    [Fact]
    public void ARelocationIOExceptionOnATraceForbiddenGameIsRetriedAnHourLaterAndFinishesOnceTheLockIsGone()
    {
        _w.AntiCheats[Root] = "eac"; // 트레이스를 다시 만들 수 없는 게임: 새 트레이스가 오지 않음
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        bool locked = true;
        int executes = 0;
        _w.Apply = (g, plan, options, ct) =>
        {
            if (options.Execute)
            {
                executes++;
                if (locked)
                {
                    throw new IOException("locked");
                }
            }

            return real(g, plan, options, ct);
        };

        Run(TimeSpan.FromMinutes(3.5));
        Assert.Equal((AutoStage.Failed, "relocation failed: IOException"), (Entry.Stage, Entry.Detail));
        int failedAttempts = executes;
        Assert.True(failedAttempts > 0, Why());

        _w.Now += TimeSpan.FromMinutes(40);
        Run(TimeSpan.FromMinutes(4));
        Assert.Equal((failedAttempts, AutoStage.Failed), (executes, Entry.Stage)); // 한 시간 전에는 다시 시도하지 않음

        locked = false;
        _w.Now += TimeSpan.FromMinutes(30);
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why()); // 한 시간 뒤 다시 시도해 잠금이 풀린 뒤 끝남
        Assert.Equal(0, Remaining(PlanFile.Load(_game.Plan)));
    }

    [Fact]
    public void ADataCheckFailureOnATraceForbiddenGameIsNotReopenedByTheHourlyRecheck()
    {
        _w.AntiCheats[Root] = "eac";
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        int executes = 0;
        _w.Apply = (g, plan, options, ct) =>
        {
            if (options.Execute)
            {
                executes++;
                throw new ShaMismatchException([F1]);
            }

            return real(g, plan, options, ct);
        };

        Run(TimeSpan.FromMinutes(3.5));
        Assert.Equal((AutoStage.Failed, "data check failed"), (Entry.Stage, Entry.Detail));
        int failedAttempts = executes;

        _w.Now += TimeSpan.FromHours(2);
        Run(TimeSpan.FromMinutes(4));

        Assert.Equal((failedAttempts, AutoStage.Failed, "data check failed"), (executes, Entry.Stage, Entry.Detail)); // 무결성 정지는 시간이 지나도 저절로 다시 열지 않음
    }
}
