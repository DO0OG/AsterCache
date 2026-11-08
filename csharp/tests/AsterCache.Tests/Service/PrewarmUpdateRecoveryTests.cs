using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 업데이트 뒤 지연의 판정 통일: Prewarmer 도 probe 모드에서 회복 추적이 있으면 IDriveRecovery 판정을, 없으면 고정 분을 따른다.
public sealed class PrewarmUpdateRecoveryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeStreamer _streamer = new();
    private readonly ListLog _log = new();
    private readonly GameSettings _game = Games.Make("a", "a.exe");

    private sealed class Recovery : IDriveRecovery
    {
        public bool? Verdict { get; set; }

        public (string Drive, DateTimeOffset At, TimeSpan Limit)? Asked { get; private set; }

        public bool IsRecovering(string drive) => false;

        public bool? WatchUpdate(string drive, DateTimeOffset updatedAt, TimeSpan limit)
        {
            Asked = (drive, updatedAt, limit);
            return Verdict;
        }
    }

    private const string ReplanHeld = "new prewarm held until the re-plan after the update finishes";

    private Prewarmer Make(Recovery recovery, TimeSpan? fixedWait, string mode = AutoSettings.RecoveryProbe, bool kept = true)
    {
        var prewarm = new Prewarmer([_game], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = _ => false,
            ZoneBytes = _ => 100L << 20,
            TraceExists = _ => true,
        }, _log, null, () => T0)
        {
            Recovery = recovery,
            PostUpdateWait = _ => fixedWait,
            ReplanStart = _ => T0,
            ReplanKept = _ => kept,
        };
        prewarm.SetAuto(AutoSettings.Default with { PostUpdateDelayMin = 10, RecoveryMaxMin = 40, PostRelocateRecovery = mode });
        return prewarm;
    }

    [Theory]
    [InlineData(true, null, "until the drive has recovered")]  // 추적이 아직 회복 중이면 고정 분이 끝났어도 지연으로 기다림
    [InlineData(false, 5, ReplanHeld)]                         // 추적이 회복했다면 고정 분이 남았어도 지연은 끝: 재계획이 안 끝났으면 그 사유로 기다림
    public void TrackedDriveFollowsTheRecoveryVerdict(bool recovering, int? fixedMinutes, string line)
    {
        var recovery = new Recovery { Verdict = recovering };
        Prewarmer prewarm = Make(recovery, fixedMinutes is { } m ? TimeSpan.FromMinutes(m) : null);

        PrewarmResult r = prewarm.Run(_game, "service start");

        Assert.False(r.Ran);
        Assert.Equal(Prewarmer.PostUpdateDelay, r.SkipReason);
        Assert.Single(_log.Lines, l => l.Contains(line, StringComparison.Ordinal));
        Assert.Equal(("D:", T0, TimeSpan.FromMinutes(40)), recovery.Asked);
    }

    [Theory]
    [InlineData(5, "min after update")]  // 추적 없음: 고정 분이 남았으면 기다림
    [InlineData(null, ReplanHeld)]       // 고정 분이 끝나도 재계획이 안 끝났으면 기다림
    public void UntrackedDriveFallsBackToFixedMinutes(int? fixedMinutes, string line)
    {
        var recovery = new Recovery { Verdict = null };
        Prewarmer prewarm = Make(recovery, fixedMinutes is { } m ? TimeSpan.FromMinutes(m) : null);

        PrewarmResult r = prewarm.Run(_game, "service start");

        Assert.False(r.Ran);
        Assert.Single(_log.Lines, l => l.Contains(line, StringComparison.Ordinal));
        Assert.NotNull(recovery.Asked);
    }

    [Theory]
    [InlineData(AutoSettings.RecoveryProbe)]
    [InlineData(AutoSettings.RecoveryFixed)]
    public void NewPrewarmIsHeldWhileTheReplanAfterAnUpdateIsPendingAndRunsOnceItEnded(string mode)
    {
        Prewarmer prewarm = Make(new Recovery { Verdict = false }, null, mode);

        Assert.Equal(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason); // 지연이 끝났어도 재계획 중
        Assert.Equal(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason);
        Assert.Empty(_streamer.Calls);
        Assert.Single(_log.Lines, l => l.Contains(ReplanHeld, StringComparison.Ordinal)); // 보류 한 번에 로그는 한 줄

        prewarm.ReplanKept = _ => false; // 재계획이 끝나 (성공이든 낡은 계획 복원이든) 업데이트 재계획 상태에서 벗어남
        PrewarmResult r = prewarm.Run(_game, "service start");

        Assert.True(r.Ran);
        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void TheHoldReasonIsLoggedAgainWhenTheDelayTurnsIntoTheReplanHold()
    {
        Prewarmer prewarm = Make(new Recovery { Verdict = null }, TimeSpan.FromMinutes(5), AutoSettings.RecoveryFixed);

        Assert.Equal(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason);
        prewarm.PostUpdateWait = _ => null; // 지연이 끝나고 재계획 보류로 바뀜
        Assert.Equal(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason);
        Assert.Equal(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason);

        Assert.Single(_log.Lines, l => l.Contains("min after update", StringComparison.Ordinal));
        Assert.Single(_log.Lines, l => l.Contains(ReplanHeld, StringComparison.Ordinal)); // 사유가 바뀌었으므로 다시 한 줄
    }

    [Fact]
    public void AReplanThatWaitsForTheNextPlayToTraceAgainIsNotHeldByTheReplanWait()
    {
        Prewarmer prewarm = Make(new Recovery { Verdict = false }, null, kept: false); // 다음 플레이에서 다시 트레이스하는 상태는 재계획이 다음 플레이까지 안 오므로 지연 밖에서는 막지 않음

        PrewarmResult r = prewarm.Run(_game, "service start");

        Assert.NotEqual(Prewarmer.PostUpdateDelay, r.SkipReason);
        Assert.DoesNotContain(_log.Lines, l => l.Contains(ReplanHeld, StringComparison.Ordinal));
    }

    [Fact]
    public void WaitLineSaysRecoveryCheckWithItsLimitWhenTheDriveIsTracked()
    {
        _ = Make(new Recovery { Verdict = true }, null).Run(_game, "service start");

        Assert.Single(_log.Lines, l => l.Contains("a: waiting after update until the drive has recovered (up to 40 min)", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("min after update", StringComparison.Ordinal));
    }

    [Fact]
    public void WaitLineSaysTheFixedMinutesWhenTheDriveIsNotTracked()
    {
        _ = Make(new Recovery { Verdict = null }, TimeSpan.FromMinutes(5)).Run(_game, "service start");

        Assert.Single(_log.Lines, l => l.Contains("a: waiting 10 min after update (drive may still be reorganizing)", StringComparison.Ordinal));
    }

    [Fact]
    public void NonProbeModeNeverAsksTheRecovery()
    {
        var recovery = new Recovery { Verdict = false };
        Prewarmer prewarm = Make(recovery, TimeSpan.FromMinutes(5), AutoSettings.RecoveryFixed);

        PrewarmResult r = prewarm.Run(_game, "service start");

        Assert.False(r.Ran);
        Assert.Null(recovery.Asked);
    }
}
