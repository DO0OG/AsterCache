using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 업데이트 뒤 지연의 판정 통일: Prewarmer 도 probe 모드에서 회복 추적이 있으면 IDriveRecovery 판정을, 없으면 고정 분을 따른다.
public sealed class PrewarmUpdateRecoveryTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeStreamer _streamer = new();
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

    private Prewarmer Make(Recovery recovery, TimeSpan? fixedWait, string mode = AutoSettings.RecoveryProbe)
    {
        var prewarm = new Prewarmer([_game], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = _ => false,
            ZoneBytes = _ => 100L << 20,
            TraceExists = _ => true,
        }, new ListLog(), null, () => T0)
        {
            Recovery = recovery,
            PostUpdateWait = _ => fixedWait,
            ReplanStart = _ => T0,
        };
        prewarm.SetAuto(AutoSettings.Default with { PostUpdateDelayMin = 10, RecoveryMaxMin = 40, PostRelocateRecovery = mode });
        return prewarm;
    }

    [Theory]
    [InlineData(true, null, false)]  // 추적이 아직 회복 중이면 고정 분이 끝났어도 기다림
    [InlineData(false, 5, true)]     // 추적이 회복했다면 고정 분이 남았어도 예열함
    public void TrackedDriveFollowsTheRecoveryVerdict(bool recovering, int? fixedMinutes, bool ran)
    {
        var recovery = new Recovery { Verdict = recovering };
        Prewarmer prewarm = Make(recovery, fixedMinutes is { } m ? TimeSpan.FromMinutes(m) : null);

        PrewarmResult r = prewarm.Run(_game, "service start");

        Assert.Equal(ran, r.Ran);
        Assert.Equal(("D:", T0, TimeSpan.FromMinutes(40)), recovery.Asked);
    }

    [Theory]
    [InlineData(5, false)]  // 추적 없음: 고정 분이 남았으면 기다림
    [InlineData(null, true)] // 고정 분이 끝났으면 예열함
    public void UntrackedDriveFallsBackToFixedMinutes(int? fixedMinutes, bool ran)
    {
        var recovery = new Recovery { Verdict = null };
        Prewarmer prewarm = Make(recovery, fixedMinutes is { } m ? TimeSpan.FromMinutes(m) : null);

        PrewarmResult r = prewarm.Run(_game, "service start");

        Assert.Equal(ran, r.Ran);
        Assert.NotNull(recovery.Asked);
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
