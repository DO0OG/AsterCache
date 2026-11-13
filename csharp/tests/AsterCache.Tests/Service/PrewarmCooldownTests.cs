using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 쿨다운 안이어도 표본으로 캐시가 식었음이 확인되면 예열하고, 따뜻하거나 확인 불가면 건너뜀. 트리거의 배경 메모리 양보는 예열할 게임이 있을 때만.
public sealed class PrewarmCooldownTests
{
    private static readonly ProbeResult ColdSample = new(8, 8, 9.0);
    private static readonly ProbeResult WarmSample = new(8, 0, 0.3);

    private readonly FakeStreamer _streamer = new();
    private readonly ListLog _log = new();
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    private ProbeResult? _probe;
    private int _probeCalls;
    private int _yields;
    private bool _gameRunning;

    private static GameSettings Game(string name, int cooldownMin = 30) =>
        Games.Make(name, name + ".exe") with { Prewarm = PrewarmSettings.Default with { TriggerExes = ["Steam.exe"], CooldownMin = cooldownMin } };

    private Prewarmer Make(params GameSettings[] games) => new(games, _streamer, new PrewarmEnv
    {
        AvailableRamMib = () => 16000,
        GameRunning = _ => _gameRunning,
        ZoneBytes = g => g.Name == "noplan" ? null : 1610L << 20,
        TraceExists = _ => true,
        ProbeReadMs = _ =>
        {
            _probeCalls++;
            return _probe;
        },
        YieldBackground = (_, _) => _yields++,
    }, _log, now: () => _now);

    // 첫 예열을 한 뒤 minutes 분이 지난 상태: 쿨다운 (30 분) 안이면 마지막 예열 시각이 그대로 기준이 됨.
    private Prewarmer WarmedThenWait(GameSettings g, int minutes)
    {
        Prewarmer p = Make(g);
        Assert.True(p.Run(g, "first").Ran);
        _now += TimeSpan.FromMinutes(minutes);
        _probeCalls = 0;
        return p;
    }

    [Fact]
    public void ColdSampleInsideTheCooldownWarmsAgain()
    {
        GameSettings g = Game("a");
        Prewarmer p = WarmedThenWait(g, 5);
        _probe = ColdSample;

        PrewarmResult r = p.Run(g, "trigger steam.exe");

        Assert.True(r.Ran);
        Assert.Equal(2, _streamer.Calls.Count);
        Assert.Equal(1, _probeCalls);
        Assert.Contains(_log.Lines, l => l.Contains("a: cooldown ignored, the cache is cold [trigger steam.exe]", StringComparison.Ordinal));
    }

    [Fact]
    public void WarmSampleInsideTheCooldownStillSkips()
    {
        GameSettings g = Game("a");
        Prewarmer p = WarmedThenWait(g, 5); // 서비스만 재시작한 경우처럼 캐시가 살아 있음
        _probe = WarmSample;

        PrewarmResult r = p.Run(g, "trigger steam.exe");

        Assert.False(r.Ran);
        Assert.StartsWith("cooldown", r.SkipReason, StringComparison.Ordinal);
        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void UnreadableSampleInsideTheCooldownStillSkips()
    {
        GameSettings g = Game("a");
        Prewarmer p = WarmedThenWait(g, 5);
        _probe = null;

        PrewarmResult r = p.Run(g, "trigger steam.exe");

        Assert.StartsWith("cooldown", r.SkipReason, StringComparison.Ordinal);
        Assert.Single(_streamer.Calls);
        Assert.Equal(1, _probeCalls);
    }

    [Fact]
    public void ARunningGameKeepsTheCooldownWithoutReadingASample()
    {
        GameSettings g = Game("a");
        Prewarmer p = WarmedThenWait(g, 5);
        _probe = ColdSample;
        _gameRunning = true;

        PrewarmResult r = p.Run(g, "trigger steam.exe");

        Assert.StartsWith("cooldown", r.SkipReason, StringComparison.Ordinal);
        Assert.Equal(0, _probeCalls); // 같은 디스크에서 게임이 도는 동안은 표본도 읽지 않음
    }

    [Fact]
    public void OutsideTheCooldownOrIgnoringItReadsNoSample()
    {
        GameSettings g = Game("a");
        Prewarmer p = WarmedThenWait(g, 31);
        _probe = ColdSample;

        Assert.True(p.Run(g, "trigger steam.exe").Ran);
        _now += TimeSpan.FromMinutes(1);
        Assert.True(p.Run(g, "rewarm", ignoreCooldown: true).Ran);

        Assert.Equal(0, _probeCalls);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("cooldown ignored", StringComparison.Ordinal));
    }

    [Fact]
    public void TriggerYieldsBackgroundMemoryWhenAColdCacheLiftsTheCooldown()
    {
        GameSettings g = Game("a");
        Prewarmer p = WarmedThenWait(g, 5);
        _probe = ColdSample;

        PrewarmResult r = Assert.Single(p.OnProcessStart(new ProcessStart(1, "steam.exe", 0)));

        Assert.True(r.Ran);
        Assert.Equal(1, _yields);
        Assert.Equal(1, _probeCalls); // 양보 판정의 표본을 본 예열이 다시 읽지 않고 씀
    }

    [Theory]
    [InlineData("warm")]
    [InlineData("unreadable")]
    public void TriggerDoesNotYieldWhenEveryGameWillBeSkippedByTheCooldown(string sample)
    {
        GameSettings g = Game("a");
        Prewarmer p = WarmedThenWait(g, 5);
        _probe = sample == "warm" ? WarmSample : null;

        PrewarmResult r = Assert.Single(p.OnProcessStart(new ProcessStart(1, "steam.exe", 0)));

        Assert.StartsWith("cooldown", r.SkipReason, StringComparison.Ordinal);
        Assert.Equal(0, _yields);
    }

    [Fact]
    public void TriggerYieldsWhenOnlyOneOfTheGamesWillRun()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        Prewarmer p = Make(a, b);
        Assert.True(p.Run(a, "first").Ran);
        _now += TimeSpan.FromMinutes(5);
        _probe = WarmSample; // a 는 쿨다운 안이고 따뜻함, b 는 예열한 적이 없어 쿨다운이 없음

        IReadOnlyList<PrewarmResult> results = p.OnProcessStart(new ProcessStart(1, "steam.exe", 0));

        Assert.Equal(1, _yields);
        Assert.Equal(["b"], results.Where(x => x.Ran).Select(x => x.Game));
    }

    [Fact]
    public void TriggerDoesNotYieldWhenNoGameHasAPlan()
    {
        GameSettings g = Game("noplan");
        Prewarmer p = Make(g);

        PrewarmResult r = Assert.Single(p.OnProcessStart(new ProcessStart(1, "steam.exe", 0)));

        Assert.False(r.Ran);
        Assert.Equal(0, _yields);
    }
}
