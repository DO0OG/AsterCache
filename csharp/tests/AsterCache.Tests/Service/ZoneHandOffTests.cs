using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 정기 재적용이 존 점유 (zone_not_free) 로 거절되면 자동 재배치의 잔여 재계획에 넘김: 넘긴 뒤 상태, 기존 게이트·보류·멈춤을 그대로 탐, 넘기지 않는 조건.
public sealed partial class AutoRelocatorTests
{
    private const string ZoneTaken = "zone_not_free: LCN 777 is in use by another file";

    private int _handOffResiduals;
    private int _handOffDryRuns;

    // 잔여 재계획과 dry-run 횟수를 세고, dry-run 을 존 점유로 거절하는 세계.
    private void CountResidualsAndBlockDryRuns()
    {
        Func<GameSettings, PlanFile, IReadOnlyList<PlanFile>, IReadOnlyList<IReadOnlyList<AsterCache.Core.Trace.TraceEvent>>?, PlanFile> residual = _w.ReplanResidual!;
        _w.ReplanResidual = (g, c, o, s) =>
        {
            _handOffResiduals++;
            return residual(g, c, o, s);
        };
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            if (!options.DryRun)
            {
                return real(g, plan, options, ct);
            }

            _handOffDryRuns++;
            throw new PlanRejectedException(ZoneTaken);
        };
        _relocator = null; // 재배치기는 만들 때의 세계를 쥐므로 바꾼 세계를 쓰는 새 재배치기로 (상태 파일은 그대로)
    }

    [Fact]
    public void HandingOverARelocatedGameResetsItToPlanningAndStepsReapplyAside()
    {
        DoneAndReadyToReplan(out _);

        Assert.True(Relocator.HandOffZoneBlocked(_game));

        Assert.Equal(AutoStage.Discovered, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
        Assert.Contains("planning again", Entry.Detail, StringComparison.Ordinal);
        Assert.Equal(ReapplyVerdict.NotApplied, Relocator.ReapplyBlock(_game)); // 다시 배치되기 전에는 재적용이 비켜 줌

        // 예열 보류 판정 (Prewarmer.Deferred) 이 쓰는 입력: 업데이트 재계획 표시가 아니므로 예열·재예열은 기존 계획 파일로 계속됨
        Assert.Null(AutoRelocator.PostUpdateWait(_status, _settings.Auto, "bench", _w.Now));
        Assert.Null(AutoRelocator.Replanning(_status, "bench"));
        Assert.False(AutoRelocator.ReplanKept(_status, "bench"));
        Assert.Contains(_log.Lines, l => l == "Info: bench: reapply blocked by zone_not_free, handing over to auto relocation to replan the remaining part");
    }

    [Fact]
    public void AfterTheHandOverTheNextTickReplansTheRemainderOnceAndASecondRejectionHolds()
    {
        DoneAndReadyToReplan(out _);
        CountResidualsAndBlockDryRuns();
        long moves = Vol.Moves.Count;
        Assert.True(Relocator.HandOffZoneBlocked(_game));

        Run(TimeSpan.FromMinutes(4));

        Assert.True(_handOffResiduals == 1, Why()); // 잔여 재계획은 한 번
        Assert.Equal(2, _handOffDryRuns); // 거절된 dry-run 한 번 + 잔여 재계획 뒤 dry-run 이 또 거절되면 보류
        RecoveryAttempt held = _status.Recovery("bench")!;
        Assert.Equal((777L, 1), (held.RejectedLcn, held.Replans));
        Assert.InRange(held.NextAllowed!.Value, _w.Now, _w.Now + AutoRelocator.RecoveryHold);
        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
        Assert.Equal(moves, Vol.Moves.Count);

        Run(TimeSpan.FromMinutes(20));

        Assert.Equal((1, 2), (_handOffResiduals, _handOffDryRuns)); // 30 분 보류 중에는 아무것도 하지 않음
    }

    [Fact]
    public void AHandedOverGameThatStaysBlockedKeepsHoldingAndIsNeverHandedOverOrReappliedAgain()
    {
        DoneAndReadyToReplan(out _);
        CountResidualsAndBlockDryRuns();
        int applies = ApplyCalls;
        Assert.True(Relocator.HandOffZoneBlocked(_game));

        for (int round = 0; round < 3; round++)
        {
            Run(TimeSpan.FromMinutes(4));

            Assert.Equal(round + 1, _handOffResiduals); // 보류가 끝날 때마다 한 번씩만 (30 분 주기)
            Assert.Null(Entry.RelocatedAt);
            Assert.False(Relocator.HandOffZoneBlocked(_game)); // 재배치 기록이 없는 게임은 다시 넘기지 않음
            Assert.Equal(ReapplyVerdict.NotApplied, Relocator.ReapplyBlock(_game)); // 재적용 검사가 다시 거절될 일도 없음
            _w.Now = _status.Recovery("bench")!.NextAllowed!.Value + TimeSpan.FromMinutes(1);
        }

        Assert.Equal(applies, ApplyCalls); // 실제 적용은 한 번도 없음 (dry-run 은 가짜가 거절)
    }

    [Fact]
    public void AHandedOverGameWhoseRemainderReplanKeepsBeingRejectedEndsStoppedAndIsNeverHandedOverAgain()
    {
        DoneAndReadyToReplan(out _);
        CountResidualsAndBlockDryRuns();
        _w.ReplanResidual = (_, _, _, _) =>
        {
            _handOffResiduals++;
            throw new PlanRejectedException("unverified journal entry changed: " + F1);
        };
        Assert.True(Relocator.HandOffZoneBlocked(_game));

        Run(TimeSpan.FromMinutes(4));
        _w.Now = _status.Recovery("bench")!.NextAllowed!.Value + TimeSpan.FromMinutes(1);
        Run(TimeSpan.FromMinutes(4));

        Assert.Equal((2, AutoStage.Failed), (_handOffResiduals, Entry.Stage)); // 같은 사유 두 번이면 멈춤
        Assert.False(Relocator.HandOffZoneBlocked(_game)); // 멈춘 게임을 재적용이 다시 넘기지 못함
        Assert.Equal(ReapplyVerdict.NotApplied, Relocator.ReapplyBlock(_game));
        Run(TimeSpan.FromMinutes(40));
        Assert.Equal(2, _handOffResiduals); // 한 시간 전에는 다시 평가하지 않음
    }

    [Fact]
    public void AHandOverWithAnEmptyJournalReplansFromTheActivePlanAndFinishesWithoutMovingAnything()
    {
        DoneAndReadyToReplan(out _);
        File.Delete(JournalPath); // 아무것도 옮기지 않은 재적용 실패와 같은 저널 0행
        CountResidualsAndBlockDryRuns();
        long moves = Vol.Moves.Count;
        _w.Apply = (g, plan, options, ct) => options.DryRun && _handOffDryRuns++ == 0 ? throw new PlanRejectedException(ZoneTaken) : Applier.Apply(plan, Vol, [Root], JournalPath, Hooks(), options, ct); // 첫 dry-run 만 점유로 거절
        Assert.True(Relocator.HandOffZoneBlocked(_game));

        RunUntilDone();

        Assert.Equal(1, _handOffResiduals);
        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.NotNull(Entry.RelocatedAt);
        Assert.Equal(moves, Vol.Moves.Count); // 이미 제자리인 조각은 다시 옮기지 않음
        Assert.Null(_status.Recovery("bench"));
    }

    [Fact]
    public void ARecoveringDriveOrTheDailyLimitStopsTheHandedOverGameFromMoving()
    {
        DoneAndReadyToReplan(out _);
        CountResidualsAndBlockDryRuns();
        SetAuto(a => a with { PostRelocateRecovery = AutoSettings.RecoveryFixed });
        Relocator.NoteReapplyStarted(_game, 1 << 20);
        Relocator.NoteReapplied(_game, 1 << 20); // 다른 이동 뒤 드라이브 회복 대기
        Assert.True(Relocator.HandOffZoneBlocked(_game));

        Run(TimeSpan.FromMinutes(10));

        Assert.Contains(AutoRelocator.DriveRecovering, Entry.Detail, StringComparison.Ordinal);
        Assert.Equal((0, 0), (_handOffResiduals, _handOffDryRuns));

        SetAuto(a => a with { PostRelocateRecovery = AutoSettings.RecoveryOff, MaxRelocationsPerDay = 1 });
        _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1), 64L << 20);
        _relocator = null;
        _w.Now += TimeSpan.FromMinutes(1);
        Run(TimeSpan.FromMinutes(10));

        Assert.Equal((AutoStage.WaitingIdle, "daily limit reached"), (Entry.Stage, Entry.Detail));
        Assert.Equal((0, 0), (_handOffResiduals, _handOffDryRuns));
    }

    [Fact]
    public void ARejectionBeforeAnyMoveReportedAsZeroBytesLeavesNeitherADailyCountNorARecoveryWait()
    {
        SetAuto(a => a with { PostRelocateRecovery = AutoSettings.RecoveryFixed });
        MarkRelocated();

        Relocator.NoteReapplyStarted(_game, 64L << 20); // 하루 횟수에 세는 최소 바이트 이상
        Assert.Equal(1, _status.RelocationsWithin(AutoRelocator.DailyWindow));
        Relocator.NoteReapplied(_game, 0);

        Assert.Equal(0, _status.RelocationsWithin(AutoRelocator.DailyWindow));
        Assert.Equal(0L, _status.RelocationBytesWithin(AutoRelocator.DailyWindow));
        Assert.NotEqual(ReapplyVerdict.Recovering, GateAfterIdle());
    }

    public static TheoryData<string> NotHandedOver => new() { "no trace", "relocate off", "auto off", "no daily relocations", "no residual replan", "extending", "data check failed", "never relocated" };

    [Theory]
    [MemberData(nameof(NotHandedOver))]
    public void TheHandOverIsRefusedAndTheStateLeftAloneWhen(string reason)
    {
        DoneAndReadyToReplan(out _);
        switch (reason)
        {
            case "no trace":
                _ = _w.Files.Remove(AutoTracer.RawTracePath(_game));
                break;
            case "relocate off":
                SetAuto(a => a with { Relocate = false });
                break;
            case "auto off":
                SetAuto(a => a with { Enabled = false });
                break;
            case "no daily relocations":
                SetAuto(a => a with { MaxRelocationsPerDay = 0 });
                break;
            case "no residual replan":
                _w.ReplanResidual = null;
                break;
            case "extending":
                _w.Files.Add(_game.Plan + AutoRelocator.ExtendSuffix);
                break;
            case "data check failed":
                _status.Set(_game.Name, _game.Source, null, AutoStage.Failed, "data check failed"); // 무결성 실패로 멈춘 상태는 자동으로 풀지 않음
                break;
            default:
                _status.ResetForReplan(_game.Name, "d");
                break;
        }

        AutoGameEntry before = Entry;
        _relocator = null; // 바뀐 설정·세계를 다시 읽음

        Assert.False(Relocator.HandOffZoneBlocked(_game));

        Assert.Equal(before, Entry);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("handing over", StringComparison.Ordinal));
    }

    [Fact]
    public void AStoppedRelocationThatIsNotADataCheckFailureCanBeHandedOver()
    {
        DoneAndReadyToReplan(out _);
        _status.Set(_game.Name, _game.Source, null, AutoStage.Failed, "relocation failed: IOException"); // 한 시간마다 다시 열리는 실패

        Assert.True(Relocator.HandOffZoneBlocked(_game));

        Assert.Equal(AutoStage.Discovered, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
    }
}
