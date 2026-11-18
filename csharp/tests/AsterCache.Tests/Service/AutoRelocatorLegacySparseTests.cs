using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 스파스 파일의 할당 기준값이 없는 이전 형식 계획: 재배치를 마친 게임을 서비스 실행당 한 번 잔여 재계획 교체로 바꾸는 단계 (ReplanLegacySparse).
public sealed partial class AutoRelocatorTests
{
    private int _residualCalls;
    private Exception? _residualThrows;

    private ApplyHooks SparseHooks() => new()
    {
        Extents = p => Vol.Ext[p],
        Digest = (p, size, extra) => SampleHasher.Compute(p, size, extra, null),
        SkipReason = _ => null,
        IsSparse = p => p == F2,
        Sleep = _ => { },
    };

    // 이전 형식 계획으로 재배치를 마친 상태: F2 는 스파스 속성이라 적용이 건너뛰었고 (그 목표 자리는 이미 다른 데이터가 차지함), F1 만 존으로 옮겨짐.
    private void LegacySparseDone()
    {
        var inner = _w.ReplanResidual!; // 재배치기를 처음 쓰기 전에 감쌈 (환경이 이때의 값을 복사함)
        _w.ReplanResidual = (g, candidate, olds, sessions) =>
        {
            _residualCalls++;
            return _residualThrows is { } ex ? throw ex : inner(g, candidate, olds, sessions);
        };
        _w.IsSparse = p => p == F2;
        _w.Apply = (_, plan, options, ct) =>
        {
            ApplyCalls++;
            return Applier.Apply(plan, Vol, [Root], JournalPath, SparseHooks(), options, ct);
        };
        Tick();
        _w.Now += Step;
        RunUntilDone();
        Assert.Equal(AutoStage.Done, Entry.Stage);
        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.Empty(plan.SparseAllocations);
        Assert.True(plan.Stats.Remove(SparseAllocation.RuleKey)); // 서비스가 지금 만든 계획에는 표지가 있으므로 이전 형식처럼 지움
        plan.Save(_game.Plan);
        Assert.Contains(plan.Tasks, t => t.Path == F2);
        Assert.DoesNotContain(Vol.Moves, m => m.Path == F2);
        Vol.Ext[Path.Combine(Root, "other.bin")] = [.. plan.Tasks.Where(t => t.Path == F2).Select(t => new Extent(t.Vcn, t.TargetLcn, t.Clusters))];
    }

    private string Dump() => string.Join(" | ", _log.Lines.TakeLast(8));

    private bool Replaced => _log.Lines.Any(l => l.Contains("legacy plan replaced", StringComparison.Ordinal));

    [Fact]
    public void ALegacyPlanIsReplacedOnceAndTheStateAndSetAsideFilesStayAsTheyWere()
    {
        LegacySparseDone();
        (string stage, DateTimeOffset? relocatedAt, string? detail) = (Entry.Stage, Entry.RelocatedAt, Entry.Detail);
        int moves = Vol.Moves.Count;
        int moved = _w.Moved.Count;

        Tick();

        Assert.Equal(1, _residualCalls);
        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.Equal([new AllocatedRange(0, 256)], plan.SparseAllocations[F2]);
        Assert.Equal(1L, plan.Stats[SparseAllocation.RuleKey]);
        Assert.Equal(1L, plan.Stats["residual"]);
        Assert.Equal(moves, Vol.Moves.Count); // 교체는 파일을 옮기지 않음 (이동은 이후 정기 재적용)
        Assert.Equal((stage, relocatedAt, detail), (Entry.Stage, Entry.RelocatedAt, Entry.Detail)); // 상태를 되돌리거나 다시 표시하지 않음
        Assert.Equal(moved, _w.Moved.Count);
        Assert.False(File.Exists(_game.Plan + ".extend") || File.Exists(_game.Plan + ".stale"));
        Assert.True(Replaced, Dump());
        Assert.Contains(_log.Lines, l => l.Contains("1 sparse file(s) gained an allocation baseline, 32 sparse cluster(s) to move, new run starts at LCN", StringComparison.Ordinal) && l.Contains("layout degraded", StringComparison.Ordinal));
        List<PlanTask> sparse = [.. plan.Tasks.Where(t => t.Path == F2).OrderBy(t => t.Vcn)];
        Assert.Equal(32L, plan.Zone.Clusters);
        Assert.Equal(plan.Zone.StartLcn, sparse[0].TargetLcn);
        Assert.Equal(sparse[0].TargetLcn + sparse[0].Clusters, sparse[1].TargetLcn); // 한 연속 구간
        Assert.True(Vol.Bitmap().RangeFree(plan.Zone.StartLcn!.Value, 32)); // 옛 목표 (다른 데이터가 차지함) 가 아님
        Assert.All(plan.Tasks.Where(t => t.Path == F1), t => Assert.DoesNotContain(sparse, s => s.TargetLcn == t.TargetLcn)); // 이미 놓인 일반 조각은 제자리

        string sha = plan.Sha();
        _w.Now += Step;
        Tick();
        Tick();

        Assert.Equal(1, _residualCalls); // 서비스 실행당 한 번
        Assert.Equal(sha, PlanFile.Load(_game.Plan).Sha());

        _relocator = null; // 서비스를 다시 시작한 것과 같음: 교체한 계획은 기준값과 표지가 있어 대상이 아님
        _w.Now += Step;
        Tick();
        Tick();

        Assert.Equal(1, _residualCalls);
        Assert.Equal(sha, PlanFile.Load(_game.Plan).Sha());
    }

    [Theory]
    [InlineData("running")]
    [InlineData("relocate_off")]
    [InlineData("policy")]
    [InlineData("extending")]
    [InlineData("stale_file")]
    [InlineData("switch_file")]
    [InlineData("not_done")]
    [InlineData("stale_detail")]
    [InlineData("recovery_held")]
    [InlineData("fingerprint_changed")]
    [InlineData("plan_failed")]
    [InlineData("drive_recovering")]
    [InlineData("origin_missing")]
    public void TheStepDoesNothingWhileAConditionIsNotMet(string why)
    {
        LegacySparseDone();
        switch (why)
        {
            case "running":
                _w.Running = true;
                break;
            case "relocate_off":
                SetAuto(a => a with { Relocate = false });
                break;
            case "policy":
                SetPolicy(GameMode.PrewarmOnly);
                break;
            case "extending":
                File.Copy(_game.Plan, _game.Plan + ".extend");
                break;
            case "stale_file":
                _w.Files.Add(_game.Plan + ".stale");
                break;
            case "switch_file":
                _w.Files.Add(_game.Plan + ".switch");
                break;
            case "not_done":
                _status.Set("bench", "auto", null, AutoStage.WaitingIdle, null);
                break;
            case "stale_detail":
                _status.Complete("bench", "auto", _w.Now, counted: false, AutoRelocator.StalePlanDetail);
                break;
            case "recovery_held":
                _status.SetRecovery("bench", new RecoveryAttempt(null, null, 1, _w.Now + TimeSpan.FromHours(1)));
                break;
            case "plan_failed":
                var failed = (System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset>)typeof(AutoRelocator).GetField("_planFailedAt", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(Relocator)!;
                failed[_game.Name] = _w.Now; // 계획 실패 기록 (그 상태를 만드는 경로는 서비스 안 비공개 사전이라 시험에서 직접 넣음)
                break;
            case "drive_recovering":
                ((DriveRecovery)Relocator.Recovery).Begin(Drive, null, TimeSpan.FromHours(1)); // 쓰기 뒤 회복 대기 중: 추가 재배치 보류
                break;
            case "origin_missing":
                _w.DirectoryExists = _ => false;
                break;
            default:
                File.SetLastWriteTimeUtc(F1, File.GetLastWriteTimeUtc(F1).AddHours(1)); // 원본이 바뀜: 업데이트 다시 계획이 맡음
                break;
        }

        Tick();

        Assert.False(Replaced, Dump());
        Assert.Equal(0, _residualCalls);
        Assert.Empty(PlanFileOrNew().SparseAllocations);
    }

    private PlanFile PlanFileOrNew() => File.Exists(_game.Plan) ? PlanFile.Load(_game.Plan) : PlanFile.Load(_game.Plan + ".stale");

    [Fact]
    public void AFingerprintThatCannotBeReadIsRetriedAfterTheTransientRetryTimeNotGivenUp()
    {
        LegacySparseDone();
        _w.Stat = _ => throw new IOException("locked");

        Tick();

        Assert.False(Replaced, Dump());
        Assert.Equal(0, _residualCalls);
        _w.Stat = PlanFile.FingerprintOf;
        _w.Now += Step;
        Tick();
        Assert.False(Replaced, Dump()); // 지문을 틱마다 다시 읽지 않고 잠시 쉼
        _w.Now += AutoRelocator.TransientRetry;
        Tick();
        Assert.True(Replaced, Dump());
        Assert.Equal(1, _residualCalls);
    }

    [Fact]
    public void AZoneLockThatCannotBeTakenOrAPlanThatChangesWhileWaitingIsRetriedOnTheNextTick()
    {
        LegacySparseDone();
        var gate = _w.Lock;
        _w.Lock = _ => null;

        Tick();

        Assert.False(Replaced, Dump());
        bool changed = false;
        _w.Lock = g =>
        {
            if (!changed)
            {
                changed = true;
                PlanFile again = PlanFile.Load(_game.Plan);
                again.Stats[ResidualPlanner.GenerationKey] = 5L; // 계획 SHA 가 달라지는 변경
                again.Save(_game.Plan); // 잠금을 기다리는 동안 다른 곳이 계획을 바꿈
            }

            return gate(g);
        };
        _w.Now += Step;
        Tick();

        Assert.False(Replaced, Dump());
        Assert.True(_residualCalls == 0, Dump());
        _w.Now += Step;
        Tick();
        Assert.True(Replaced, Dump());
        Assert.Equal(1, _residualCalls);
    }

    [Fact]
    public void ANoFreeRunRejectionChangesNothingLogsOnceAndIsNotRetriedDuringThisServiceRun()
    {
        LegacySparseDone();
        string before = PlanFile.Load(_game.Plan).Sha();
        var reserved = _w.Reserved;
        _w.Reserved = _ => [(0, 1_000_000)]; // 빈 구간이 없음

        Tick();

        Assert.False(Replaced, Dump());
        Assert.Equal(1, _residualCalls);
        Assert.Equal(before, PlanFile.Load(_game.Plan).Sha());
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Single(_log.Lines, l => l.StartsWith("Info", StringComparison.Ordinal) && l.Contains("legacy plan not replaced, no free run of", StringComparison.Ordinal) && l.Contains("will try again on the next service start", StringComparison.Ordinal));
        _w.Reserved = reserved;
        _w.Now += AutoRelocator.StoppedRecheck;
        Tick();
        Assert.Equal(1, _residualCalls); // 이번 서비스 실행에서는 다시 시도하지 않음 (볼륨 비트맵을 다시 읽지 않음)
        Assert.False(Replaced, Dump());

        _relocator = null; // 서비스를 다시 시작함: 한 번 더 시도
        _w.Now += Step;
        Tick();
        Assert.True(Replaced, Dump());
        Assert.Equal(2, _residualCalls);
    }

    [Fact]
    public void ATransientIoFailureIsRetriedAfterTheTransientRetryTime()
    {
        LegacySparseDone();
        _residualThrows = new IOException("locked");

        Tick();
        _w.Now += Step;
        Tick();

        Assert.Equal(1, _residualCalls); // 잠시 쉬는 동안은 다시 시도하지 않음
        _residualThrows = null;
        _w.Now += AutoRelocator.TransientRetry;
        Tick();
        Assert.True(Replaced, Dump());
        Assert.Equal(2, _residualCalls);
    }

    [Fact]
    public void AnyOtherRejectionOnlyWarnsAndIsNotRetriedDuringThisRun()
    {
        LegacySparseDone();
        _residualThrows = new PlanRejectedException("unverified journal entry changed");

        Tick();
        _w.Now += Step;
        Tick();

        Assert.Equal(1, _residualCalls);
        Assert.True(_log.Lines.Count(l => l.StartsWith("Warn", StringComparison.Ordinal) && l.Contains("sparse baseline replan failed", StringComparison.Ordinal)) == 1, Dump());
        Assert.Empty(PlanFile.Load(_game.Plan).SparseAllocations);
        Assert.Equal(AutoStage.Done, Entry.Stage);
    }

    // 교체한 계획 (잔여 계획 형태) 이 업데이트 뒤 다시 계획할 때 이전 계획으로 쓰이고 정상으로 끝남.
    [Fact]
    public void TheReplacedPlanIsAcceptedAsThePreviousPlanWhenAGameUpdateMakesTheServicePlanAgain()
    {
        LegacySparseDone();
        Tick();
        Assert.True(Replaced, Dump());
        _w.MakePlan = (sessions, origin, zoneMib) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], preserveBytes: 64 << 20, sparseFn: p => p == F2);
        _w.MakePlanAt = (sessions, origin, zoneMib, lcn, used) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], zoneStartLcn: lcn, preserveBytes: 64 << 20, fixedClusters: used, sparseFn: p => p == F2);
        _w.ArchiveJournal = _ =>
        {
            if (!File.Exists(JournalPath))
            {
                return null;
            }

            File.Move(JournalPath, JournalPath + ".old");
            return JournalPath + ".old";
        };
        File.SetLastWriteTimeUtc(F1, File.GetLastWriteTimeUtc(F1).AddHours(1)); // 게임 업데이트
        _w.Now += AutoRelocator.StaleCheckEvery;

        Tick();

        Assert.True(File.Exists(_game.Plan + ".stale")); // 교체한 계획이 이전 계획으로 치워짐
        _w.Files.Add(AutoTracer.RawTracePath(_game));
        _status.Set("bench", "auto", null, AutoStage.WaitingIdle, null);
        RunUntilDone(80);

        Assert.Equal(AutoStage.Done, Entry.Stage);
        PlanFile again = PlanFile.Load(_game.Plan);
        Assert.Equal([new AllocatedRange(0, 256)], again.SparseAllocations[F2]);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("replan failed", StringComparison.Ordinal));
    }
}
