using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 묶음 예열의 예산: 전체 예열 (RunBatch) 과 앞부분 예열 (WarmHeads) 이 같은 남은 예산으로 같은 게임을 고르는가, 예산이 모자랄 때 바이트당 가치 순서.
public sealed class BatchBudgetTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
    private readonly RecordingStreamer _streamer = new();
    private readonly Dictionary<string, long> _zoneMib = [];
    private readonly Dictionary<string, long> _observedMib = [];
    private long _avail;
    private long _physical;

    private sealed class RecordingStreamer : IGameStreamer
    {
        public List<string> Events { get; } = [];

        public List<GameSettings> Fulls { get; } = [];

        public Func<GameSettings, long> Bytes { get; set; } = _ => 0;

        public StreamStats Stream(GameSettings game)
        {
            Events.Add("full " + game.Name);
            Fulls.Add(game);
            return new StreamStats(Bytes(game), 2, 0, 0.01);
        }

        public List<long> HeadReadMib { get; } = [];

        public Func<GameSettings, long> Tier0Mib { get; set; } = _ => 1L << 20; // 로딩 앞부분 (tier0) 의 크기 (MiB): 기본은 상한에 안 걸리게 큼

        public void StreamHead(GameSettings game, CancellationToken stop)
        {
            Events.Add("head " + game.Name);
            long bound = game.MaxStreamSet ? game.MaxStreamMib : long.MaxValue; // 받은 상한 (MiB)
            HeadReadMib.Add(Math.Min(Tier0Mib(game), bound));
        }
    }

    private readonly HashSet<string> _noTrace = [];
    private readonly HashSet<string> _noZone = [];

    private static GameSettings Game(string name, int minFree = 0) => Games.Make(name, name + ".exe") with { Prewarm = PrewarmSettings.Default with { OnServiceStart = false, MinFreeRamMib = minFree } };

    private Prewarmer Make(params GameSettings[] games)
    {
        _streamer.Bytes = g => (g.MaxStreamSet ? g.MaxStreamMib : _zoneMib[g.Name]) << 20; // 실제로 읽은 양: 줄인 상한이 있으면 그만큼
        return new Prewarmer(games, _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = _ => false,
            ZoneBytes = g => _noZone.Contains(g.Name) ? null : _zoneMib[g.Name] << 20,
            ObservedBytes = g => _observedMib.TryGetValue(g.Name, out long mib) ? mib << 20 : null,
            TraceExists = g => !_noTrace.Contains(g.Name),
            PhysicalRamBytes = () => _physical,
        }, new ListLog(), null, () => Now);
    }

    [Fact]
    public void RequestLargerThanTheBudgetReadsAtMost904MibAndIsNotRecordedAsFullInBothPasses()
    {
        (_avail, _physical) = (5000, 8L << 30); // 가용 5000, 예약 4096, 요청 5000
        _zoneMib["a"] = 5000;
        _zoneMib["b"] = 100;
        GameSettings a = Game("a", 4096), b = Game("b", 4096);
        Prewarmer p = Make(a, b);

        IReadOnlyList<PrewarmResult> results = p.RunAll([a, b], "t");

        Assert.Equal(["head a", "full a"], _streamer.Events); // 앞부분도 같은 판단: b 는 남은 예산이 없어 둘 다 건너뜀
        Assert.Equal(904, Assert.Single(_streamer.Fulls).MaxStreamMib);
        Assert.True(p.SupplementPending("a")); // 요청 전체를 읽은 것으로 기록하지 않음
        Assert.Equal([("a", true), ("b", false)], results.Select(r => (r.Game, r.Ran)));
        Assert.Equal("batch memory budget", results[1].SkipReason);
    }

    [Fact]
    public void AFirstCandidateBelowThePartialMinimumIsSkippedAndALaterSmallOneIsStillReviewedInBothPasses()
    {
        (_avail, _physical) = (2500, 0); // 앞 게임이 2308 MiB 를 올리면 남은 192 MiB
        (_zoneMib["pre"], _zoneMib["big"], _zoneMib["small"]) = (2308, 1024, 128);
        GameSettings pre = Game("pre"), big = Game("big"), small = Game("small");
        Prewarmer p = Make(pre, big, small);

        IReadOnlyList<PrewarmResult> results = p.RunAll([pre, big, small], "t");

        Assert.Equal(["head pre", "head small", "full pre", "full small"], _streamer.Events);
        Assert.Equal([("pre", true), ("big", false), ("small", true)], results.Select(r => (r.Game, r.Ran)));
    }

    [Fact]
    public void LoadHeadsAreCountedOnceSoTheHeadPassAndTheFullPassPickTheSameGames()
    {
        _avail = 2500;
        (_zoneMib["a"], _zoneMib["b"], _zoneMib["c"]) = (1000, 1000, 1000);
        GameSettings a = Game("a"), b = Game("b"), c = Game("c");
        Prewarmer p = Make(a, b, c);

        p.RunAll([a, b, c], "t");

        Assert.Equal(["head a", "head b", "full a", "full b"], _streamer.Events); // 앞부분을 예산에서 두 번 빼면 b 가 밀림
    }

    [Fact]
    public void WithEnoughMemoryTheRankOrderIsUnchanged()
    {
        _avail = 100000;
        (_zoneMib["x"], _zoneMib["y"], _zoneMib["z"]) = (1000, 5000, 100);
        GameSettings x = Game("x"), y = Game("y"), z = Game("z");
        Prewarmer p = Make(x, y, z);

        p.RunAll([x, y, z], "t");

        Assert.Equal(["x", "y", "z"], _streamer.Fulls.Select(g => g.Name));
    }

    [Fact]
    public void EveryGamesObservedRangesAreAllocatedBeforeAnotherGamesSupplement()
    {
        (_avail, _physical) = (10000, 2200L << 20); // 1100 MiB cache budget
        (_zoneMib["a"], _zoneMib["b"]) = (1000, 700);
        (_observedMib["a"], _observedMib["b"]) = (400, 700); // a 의 나머지 600 MiB 는 보충 범위
        GameSettings a = Game("a"), b = Game("b");
        Prewarmer p = Make(a, b);

        IReadOnlyList<PrewarmResult> results = p.RunAll([a, b], "t");

        Assert.All(results, r => Assert.True(r.Ran));
        Assert.Equal(["head a", "head b", "full a", "full b"], _streamer.Events);
        GameSettings aRead = _streamer.Fulls.Single(g => g.Name == "a");
        GameSettings bRead = _streamer.Fulls.Single(g => g.Name == "b");
        Assert.Equal((400, true), (aRead.MaxStreamMib, aRead.MaxStreamSet)); // a 의 추측 범위는 선택되지 않음
        Assert.False(bRead.MaxStreamSet); // b 의 관찰 범위는 전부 예산을 받음
    }

    [Fact]
    public void ServiceStartCapAllocatesEveryGamesObservedRangesBeforeAnotherGamesSupplement()
    {
        (_avail, _physical) = (100000, 0);
        (_zoneMib["a"], _zoneMib["b"]) = (1024, 768);
        (_observedMib["a"], _observedMib["b"]) = (256, 768); // a 의 나머지 768 MiB 는 보충 범위
        GameSettings a = Game("a"), b = Game("b");
        Prewarmer p = Make(a, b);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 1024 });

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart([a, b]);

        Assert.All(results, r => Assert.True(r.Ran)); // 게임 전체 크기로 상한을 먼저 쓰면 b 가 빠짐
        GameSettings aRead = _streamer.Fulls.Single(g => g.Name == "a");
        GameSettings bRead = _streamer.Fulls.Single(g => g.Name == "b");
        Assert.Equal((256, true), (aRead.MaxStreamMib, aRead.MaxStreamSet));
        Assert.False(bRead.MaxStreamSet);
    }

    [Fact]
    public void ServiceStartSkippedUnrecordedGameDoesNotUseUpTheCapForLaterGames()
    {
        (_avail, _physical) = (100000, 0);
        (_zoneMib["a"], _zoneMib["c"]) = (600, 300);
        _noZone.Add("b"); // 기록 없는 게임: 통째 예열 기본 1024 MiB 는 남은 424 MiB 에 안 들어가 건너뜀
        _noTrace.Add("b");
        GameSettings a = Game("a"), b = Game("b"), c = Game("c");
        Prewarmer p = Make(a, b, c);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 1024 });

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart([a, b, c]);

        Assert.Equal(["a", "c"], _streamer.Fulls.Select(g => g.Name)); // c 가 건너뛴 b 의 몫 때문에 빠지지 않음
        Assert.Equal("startup byte cap", results.Single(r => r.Game == "b").SkipReason);
        Assert.True(results.Single(r => r.Game == "c").Ran);
    }

    [Fact]
    public void WhenMemoryIsShortTheCandidatesAfterTheFittingOnesGoByValuePerByte()
    {
        _avail = 1100; // x (1000) 가 들어가면 남은 100 MiB: 부분 예열 최소 (256) 에 못 미침
        (_zoneMib["x"], _zoneMib["y"], _zoneMib["z"], _zoneMib["w"], _zoneMib["v"]) = (1000, 5000, 90, 40, 50);
        GameSettings[] list = [Game("x"), Game("y"), Game("z"), Game("w"), Game("v")];
        Prewarmer p = Make(list);

        IReadOnlyList<PrewarmResult> results = p.RunAll(list, "t");

        Assert.Equal(["x", "w", "v"], _streamer.Fulls.Select(g => g.Name)); // 순위 순이면 z (90) 하나만 들어가고 w·v 는 못 들어감
        Assert.Equal(["x", "w", "v"], results.Where(r => r.Ran).Select(r => r.Game));
    }

    [Fact]
    public void TheHeadPassNeverReadsPastTheCapEvenWhenTier0IsLarger()
    {
        (_avail, _physical) = (5000, 8L << 30); // RV-01: cap 904 MiB
        _zoneMib["a"] = 5000;
        _zoneMib["b"] = 100;
        _streamer.Tier0Mib = _ => 2000; // 로딩 앞부분이 cap 보다 큼
        GameSettings a = Game("a", 4096), b = Game("b", 4096);
        Prewarmer p = Make(a, b);

        p.RunAll([a, b], "t");

        Assert.Equal([904], _streamer.HeadReadMib);
    }

    [Fact]
    public void SkippedGamesAmongTheCandidatesKeepTheRankOrderWhenMemoryIsEnough()
    {
        (_avail, _physical) = (100000, 0);
        (_zoneMib["x"], _zoneMib["s"], _zoneMib["y"], _zoneMib["z"]) = (1000, 3000, 500, 100);
        _noTrace.Add("s"); // 추적 없음: 건너뜀
        GameSettings x = Game("x"), s = Game("s"), y = Game("y"), z = Game("z");
        Prewarmer p = Make(x, s, y, z);

        IReadOnlyList<PrewarmResult> results = p.RunAll([x, s, y, z], "t");

        Assert.Equal(["x", "y", "z"], _streamer.Fulls.Select(g => g.Name));
        Assert.False(results.Single(r => r.Game == "s").Ran);
    }

    [Fact]
    public void TheFirstRankKeepsItsSlotEvenWhenItDoesNotFit()
    {
        GameSettings[] list = [Game("A"), Game("B"), Game("C")];
        (_zoneMib["A"], _zoneMib["B"], _zoneMib["C"]) = (2000, 10, 20);

        IReadOnlyList<GameSettings> order = Prewarmer.OrderByValuePerByte(list, g => _zoneMib[g.Name], (g, used) => 100); // 남은 예산 100 < 최소 부분 예열 256

        Assert.Equal(["A", "B", "C"], order.Select(g => g.Name));
    }
}
