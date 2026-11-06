using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 재예열·보충 예열 대상이 게임들 합계로 캐시 예산 (물리 RAM/4) 을 넘지 않고, 순위가 높은 게임이 먼저 들어가는가.
public sealed class RewarmBudgetTests
{
    private const long Mib = 1L << 20;
    private const long HotMib = 5000; // 둘의 합 10000 > 예산 8192 (물리 RAM 32 GiB 의 1/4)
    private readonly ListLog _log = new();
    private readonly CountStreamer _streamer = new();
    private DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private long _avail = 6000;
    private string[] _order = ["a", "b"];

    private sealed class CountStreamer : IGameStreamer
    {
        public List<(string Game, int MaxStreamMib)> Calls { get; } = [];

        /// 핫 집합 크기 (MiB).
        public long ZoneMib { get; set; } = HotMib;

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes)
        {
            Calls.Add((game.Name, game.MaxStreamMib));
            long total = Math.Min(game.MaxStreamMib == 0 ? ZoneMib : game.MaxStreamMib, ZoneMib) * Mib;
            progress?.Invoke(0, total);
            return (new StreamStats(total, 1, 0, 0.01), !stop.IsCancellationRequested);
        }

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress) =>
            StreamPrewarm(game, stop, progress, 0);
    }

    private static GameSettings Game(string name) => Games.Make(name, name + ".exe") with { OriginRoot = @"D:\Games\" + name };

    private Prewarmer Make(params GameSettings[] games) => new(games, _streamer, new PrewarmEnv
    {
        AvailableRamMib = () => _avail,
        GameRunning = _ => false,
        PhysicalRamBytes = () => 32L << 30,
        ZoneBytes = g => Math.Min(_streamer.ZoneMib, g.StreamCapBytes(_streamer.ZoneMib * Mib) / Mib) * Mib,
        TraceExists = _ => true,
        ProbeReadMs = _ => new ProbeResult(8, 8, 5.0), // 식음
    }, _log, null, () => _now);

    private Prewarmer Ranked(params GameSettings[] games)
    {
        Prewarmer p = Make(games);
        p.WarmRank = list => _order.Select(n => list.First(g => g.Name == n));
        return p;
    }

    private static AutoSettings Auto() => AutoSettings.Default with { RewarmMinutes = 10 };

    private void Cycle(Prewarmer p, AutoSettings auto)
    {
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
    }

    [Fact]
    public void OnlyTheHigherRankedGameIsRewarmedWhenTheHotSetsExceedTheBudget()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        _avail = 16000;
        Prewarmer p = Ranked(a, b);
        Assert.True(p.Run(a, "service start").Ran);
        Assert.True(p.Run(b, "service start").Ran);
        AutoSettings auto = Auto();
        p.Tick(auto);
        _streamer.Calls.Clear();

        Cycle(p, auto);

        Assert.Equal("a", Assert.Single(_streamer.Calls).Game);
        Assert.Contains(_log.Lines, l => l.Contains("b: rewarm check skipped (the cache budget of 8192 MiB is used by higher-ranked games: 5000 MiB)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheRewarmedGameChangesWhenTheRankChanges()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        _avail = 16000;
        Prewarmer p = Ranked(a, b);
        Assert.True(p.Run(a, "service start").Ran);
        Assert.True(p.Run(b, "service start").Ran);
        AutoSettings auto = Auto();
        p.Tick(auto);
        Cycle(p, auto);
        _streamer.Calls.Clear();

        _order = ["b", "a"];
        Cycle(p, auto);

        Assert.Equal("b", Assert.Single(_streamer.Calls).Game);
        Assert.Contains(_log.Lines, l => l.Contains("a: rewarm check skipped (the cache budget", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutARankEveryWarmedGameIsRewarmed()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        _avail = 16000;
        Prewarmer p = Make(a, b);
        Assert.True(p.Run(a, "service start").Ran);
        Assert.True(p.Run(b, "service start").Ran);
        AutoSettings auto = Auto();
        p.Tick(auto);
        _streamer.Calls.Clear();

        Cycle(p, auto);

        Assert.Equal(2, _streamer.Calls.Count);
    }

    [Fact]
    public void ARewarmedGameWhoseHotSetExceedsTheBudgetReadsOnlyItsAllocation()
    {
        GameSettings a = Game("a");
        _streamer.ZoneMib = 12000; // 예산 8192 (물리 RAM 32 GiB 의 1/4) 보다 큰 핫 집합 하나
        _avail = 20000;
        _order = ["a"];
        Prewarmer p = Ranked(a);
        Assert.True(p.Run(a, "service start").Ran);
        AutoSettings auto = Auto();
        p.Tick(auto);
        _streamer.Calls.Clear();

        Cycle(p, auto);

        Assert.Equal(("a", 8192), Assert.Single(_streamer.Calls)); // 셈에 쓴 8192 MiB 만 읽음 (12000 전체가 아님)
        Assert.True(p.SupplementPending("a")); // 나머지는 보충 대기
        Assert.Equal((8192L, 12000L), p.PartialInfo("a"));
    }

    [Fact]
    public void SupplementUsesTheSameAllocationAndFollowsTheRank()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        Prewarmer p = Ranked(a, b);
        Assert.True(p.Run(a, "service start").Ran); // 가용 6000: 둘 다 앞부분만 읽은 보충 대기
        Assert.True(p.Run(b, "service start").Ran);
        Assert.True(p.SupplementPending("a"));
        Assert.True(p.SupplementPending("b"));
        _avail = 16000;
        _streamer.Calls.Clear();

        Assert.False(p.SupplementWhenIdle(b, CancellationToken.None)); // 낮은 순위: 유휴 시계와 상관없이 건너뜀
        _now += TimeSpan.FromSeconds(61);
        Assert.False(p.SupplementWhenIdle(b, CancellationToken.None));
        Assert.Empty(_streamer.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("b: supplement prewarm skipped (the cache budget of 8192 MiB", StringComparison.Ordinal));

        p.SupplementWhenIdle(a, CancellationToken.None);
        _now += TimeSpan.FromSeconds(61);
        Assert.True(p.SupplementWhenIdle(a, CancellationToken.None));
        Assert.Equal("a", Assert.Single(_streamer.Calls).Game);

        _order = ["b", "a"]; // 순위가 바뀌면 b 가 대상
        p.SupplementWhenIdle(b, CancellationToken.None);
        _now += TimeSpan.FromSeconds(61);
        Assert.True(p.SupplementWhenIdle(b, CancellationToken.None));
        Assert.Equal("b", _streamer.Calls[^1].Game);
    }
}
