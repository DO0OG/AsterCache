using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 재예열·보충 예열 대상이 게임들 합계로 캐시 예산 (기본 물리 RAM/2) 을 넘지 않고, 순위가 높은 게임이 먼저 들어가는가.
public sealed class RewarmBudgetTests
{
    private const long Mib = 1L << 20;
    private const long HotMib = 5000; // 둘의 합 10000 > 예산 8192 (물리 RAM 16 GiB 의 1/2)
    private readonly ListLog _log = new();
    private readonly CountStreamer _streamer = new();
    private DateTimeOffset _now = new(2026, 11, 6, 12, 0, 0, TimeSpan.Zero);
    private readonly Dictionary<string, long> _observedMib = new(StringComparer.OrdinalIgnoreCase);
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
        PhysicalRamBytes = () => 16L << 30,
        ZoneBytes = g => Math.Min(_streamer.ZoneMib, g.StreamCapBytes(_streamer.ZoneMib * Mib) / Mib) * Mib,
        ObservedBytes = g => _observedMib.TryGetValue(g.Name, out long mib) ? mib * Mib : null,
        TraceExists = _ => true,
        ProbeReadMs = _ => new ProbeResult(8, 8, 5.0), // 식음
    }, _log, null, () => _now);

    private Prewarmer Ranked(params GameSettings[] games)
    {
        Prewarmer p = Make(games);
        p.WarmRank = list => _order.Select(n => list.FirstOrDefault(g => g.Name == n)).OfType<GameSettings>(); // 목록에 없는 이름은 건너뜀 (트리거로 걸린 일부만 받을 때)
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
        _streamer.ZoneMib = 12000; // 예산 8192 (물리 RAM 16 GiB 의 1/2) 보다 큰 핫 집합 하나
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

    private static GameSettings Launched(string name, int maxCacheMib = 0) =>
        Game(name) with { Prewarm = PrewarmSettings.Default with { TriggerExes = ["launcher.exe"], MaxCacheMib = maxCacheMib } };

    [Theory]
    [InlineData(32, 0, 16384)] // 설정이 없으면 물리 RAM 의 절반
    [InlineData(32, 4000, 4000)] // 양수면 그 값
    [InlineData(32, 100000, 32768)] // 물리 RAM 을 넘지 못함
    public void TheCacheBudgetIsHalfOfRamUnlessConfiguredAndNeverAbovePhysicalRam(long ramGib, int configured, long expectedMib) =>
        Assert.Equal(expectedMib, Prewarmer.CacheCapMib(ramGib << 30, [Launched("a", configured)]));

    [Fact]
    public void TheLargestConfiguredBudgetWinsAndUnknownRamHasNoLimit()
    {
        Assert.Equal(6000, Prewarmer.CacheCapMib(32L << 30, [Launched("a", 3000), Launched("b", 6000), Launched("c")]));
        Assert.Equal(long.MaxValue, Prewarmer.CacheCapMib(0, [Launched("a")]));
        Assert.Equal(3000, Prewarmer.CacheCapMib(0, [Launched("a", 3000)]));
    }

    [Fact]
    public void AvailableRamStillLimitsTheReadWhenTheBudgetIsLarge()
    {
        long budget = Prewarmer.CacheCapMib(32L << 30, [Game("a")]); // 16384
        Assert.Equal(1904, Prewarmer.PartialCapMib(Game("a"), 12000, 6000, budget)); // 가용 6000 - 예비 4096
        Assert.Null(Prewarmer.PartialCapMib(Game("a"), 12000, 4200, budget)); // 256 MiB 도 못 들어감
        Assert.Equal(budget, Prewarmer.PartialCapMib(Game("a"), 20000, 40000, budget)); // 가용이 넉넉하면 예산이 한도
    }

    [Fact]
    public void ObservedRangesAcrossGamesAreAllocatedBeforeOneGamesSupplement()
    {
        GameSettings a = Game("a"), b = Game("b");
        _avail = 20000;
        _observedMib["a"] = 1000; // a 의 나머지 4000 MiB 는 보충 범위
        _observedMib["b"] = 5000;
        Prewarmer p = Ranked(a, b);
        Assert.True(p.Run(a, "service start").Ran);
        Assert.True(p.Run(b, "service start").Ran);

        var budget = p.WarmBudget()!.Value;

        Assert.Equal(8192, budget.UsedMib);
        Assert.Equal(5000, budget.AllocMib["b"]); // b 의 관찰 범위가 전부 배정됨
        Assert.Equal(3192, budget.AllocMib["a"]); // a 는 관찰 1000 + 남은 예산의 보충 2192
    }

    [Fact]
    public void SupplementStaysWithinTheGamesAllocationSoItDoesNotTakeAnotherGamesObservedShare()
    {
        GameSettings a = Game("a"), b = Game("b");
        _avail = 6000;
        _observedMib["a"] = 1000; // a 의 나머지 4000 MiB 는 보충 범위
        _observedMib["b"] = 5000;
        Prewarmer p = Ranked(a, b);
        Assert.True(p.Run(a, "service start").Ran); // 가용 6000: 앞부분만 읽은 보충 대기
        Assert.True(p.Run(b, "service start").Ran);
        Assert.True(p.SupplementPending("a"));
        _avail = 20000;
        _streamer.Calls.Clear();
        p.SupplementWhenIdle(a, CancellationToken.None);
        _now += TimeSpan.FromSeconds(61);

        Assert.True(p.SupplementWhenIdle(a, CancellationToken.None));

        Assert.Equal(("a", 3192), Assert.Single(_streamer.Calls)); // 보충 5000 MiB 전체가 아니라 b 의 관찰분 5000 MiB 를 뺀 배정량까지만
        Assert.Equal(5000, p.WarmBudget()!.Value.AllocMib["b"]);
    }

    [Fact]
    public void TheConfiguredBudgetReplacesTheAutomaticOneInTheRewarmBudget()
    {
        GameSettings a = Launched("a", 6000);
        GameSettings b = Launched("b");
        _avail = 16000;
        Prewarmer p = Ranked(a, b);
        Assert.True(p.Run(a, "service start").Ran);
        Assert.True(p.Run(b, "service start").Ran);

        Assert.Equal(6000, p.WarmBudget()!.Value.CapMib);
        Assert.Contains("a", p.WarmBudget()!.Value.In);
        Assert.DoesNotContain("b", p.WarmBudget()!.Value.In); // 5000 + 5000 > 6000
    }

    [Fact]
    public void TheGameStartedByATriggerTakesTheBudgetBeforeTheHigherRankedOne()
    {
        GameSettings a = Game("a"); // 순위 1위지만 트리거가 아님
        GameSettings b = Launched("b");
        _avail = 16000;
        Prewarmer p = Ranked(a, b);
        Assert.True(p.Run(a, "service start").Ran);
        Assert.True(p.Run(b, "service start").Ran);
        Assert.Contains("a", p.WarmBudget()!.Value.In); // 트리거 전: 순위대로
        Assert.DoesNotContain("b", p.WarmBudget()!.Value.In);

        p.OnProcessStart(new ProcessStart(1, "launcher.exe", 0));

        Assert.Contains("b", p.WarmBudget()!.Value.In);
        Assert.DoesNotContain("a", p.WarmBudget()!.Value.In); // 양보: 이미 올린 a 의 캐시를 비우지는 않고 이후 점검 대상에서만 빠짐
        _now += Prewarmer.TriggerPriorityWindow + TimeSpan.FromMinutes(1);
        Assert.Contains("a", p.WarmBudget()!.Value.In); // 기간이 지나면 기존 순위
        Assert.DoesNotContain("b", p.WarmBudget()!.Value.In);
    }

    [Fact]
    public void GamesSharingATriggerKeepTheirRankAmongThemselves()
    {
        GameSettings a = Launched("a");
        GameSettings b = Launched("b");
        GameSettings c = Game("c");
        _avail = 16000;
        _order = ["c", "b", "a"];
        Prewarmer p = Ranked(a, b, c);
        Assert.True(p.Run(a, "service start").Ran);
        Assert.True(p.Run(b, "service start").Ran);
        Assert.True(p.Run(c, "service start").Ran);

        p.OnProcessStart(new ProcessStart(1, "launcher.exe", 0));

        Assert.Equal(["b"], p.WarmBudget()!.Value.In.OrderBy(n => n)); // 트리거를 공유한 a, b 중 순위가 높은 b 만 앞섬: 5000 + 5000 > 8192 라 순위 1위 c 도 밀림
    }
}
