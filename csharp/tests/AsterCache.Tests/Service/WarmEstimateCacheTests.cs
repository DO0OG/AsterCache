using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 예산 계산 (WarmBudget) 이 같은 입력의 게임별 추정 (전체 크기, 관찰량) 을 다시 읽지 않고, 입력·설정·시간이 바뀌면 다시 계산하는가.
public sealed class WarmEstimateCacheTests
{
    private const long Mib = 1L << 20;
    private readonly ListLog _log = new();
    private DateTimeOffset _now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private string _stamp = "v1";
    private int _memoryGeneration;
    private long? _zoneMib = 100;
    private int _zoneCalls;
    private int _observedCalls;
    private int _cachedCalls;
    private bool _running;
    private long _avail = 6000;
    private readonly Streamer _streamer = new();

    private sealed class Streamer : IGameStreamer
    {
        public int Calls { get; private set; }

        public long ReadMib { get; set; } = 100;

        public long? TotalMib { get; set; }

        public Action<Action<long, long>>? OnProgress { get; set; }

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes)
        {
            Calls++;
            progress?.Invoke(0, (TotalMib ?? ReadMib) * Mib);
            if (OnProgress is not null && progress is not null)
            {
                OnProgress(progress);
            }

            return (new StreamStats(ReadMib * Mib, 1, 0, 0.01), !stop.IsCancellationRequested);
        }

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress) =>
            StreamPrewarm(game, stop, progress, 0);
    }

    private static GameSettings Game(string name) => Games.Make(name, name + ".exe") with { OriginRoot = @"D:\Games\" + name };

    private Prewarmer Make(params GameSettings[] games) => new(games, _streamer, new PrewarmEnv
    {
        AvailableRamMib = () => _avail,
        GameRunning = _ => _running,
        PhysicalRamBytes = () => 16L << 30,
        ZoneBytes = _ =>
        {
            _zoneCalls++;
            return _zoneMib is { } z ? z * Mib : null;
        },
        ObservedBytes = _ =>
        {
            _observedCalls++;
            return 40 * Mib;
        },
        ObservedBytesIfCached = _ =>
        {
            _cachedCalls++;
            return 30 * Mib;
        },
        EstimateStamp = (_, memoryCaches) => memoryCaches ? $"{_stamp}:{_memoryGeneration}" : _stamp,
        TraceExists = _ => true,
        ProbeReadMs = _ => new ProbeResult(8, 8, 5.0),
    }, _log, null, () => _now);

    private Prewarmer Ranked(params GameSettings[] games)
    {
        Prewarmer p = Make(games);
        p.WarmRank = list => list;
        Assert.True(p.Run(games[0], "service start").Ran);
        return p;
    }

    [Fact]
    public void ASecondBudgetOnTheSameInputsDoesNotRecomputeTheEstimates()
    {
        Prewarmer p = Ranked(Game("a"));
        var first = p.WarmBudget()!.Value;
        (int zone, int observed) = (_zoneCalls, _observedCalls);

        var second = p.WarmBudget()!.Value;

        Assert.Equal((zone, observed), (_zoneCalls, _observedCalls));
        Assert.Equal((first.UsedMib, first.AllocMib["a"]), (second.UsedMib, second.AllocMib["a"]));
        Assert.Equal(100, second.AllocMib["a"]); // 관찰 40 + 보충 60
    }

    [Fact]
    public void AChangedInputSignatureRecomputesTheEstimates()
    {
        Prewarmer p = Ranked(Game("a"));
        _ = p.WarmBudget();
        int zone = _zoneCalls;

        _stamp = "v2"; // 입력 파일의 길이·수정 시각이 달라짐
        _zoneMib = 200;
        var budget = p.WarmBudget()!.Value;

        Assert.True(_zoneCalls > zone);
        Assert.Equal(200, budget.AllocMib["a"]);
    }

    [Fact]
    public void EstimatesExpireAfterTheMaxAge()
    {
        Prewarmer p = Ranked(Game("a"));
        _ = p.WarmBudget();
        int zone = _zoneCalls;

        _now += WarmEstimateCache.MaxAge - TimeSpan.FromSeconds(1);
        _ = p.WarmBudget();
        Assert.Equal(zone, _zoneCalls);

        _now += TimeSpan.FromSeconds(2);
        _ = p.WarmBudget();
        Assert.True(_zoneCalls > zone);
    }

    [Fact]
    public void AChangedGameSettingRecomputesTheEstimates()
    {
        GameSettings a = Game("a");
        Prewarmer p = Ranked(a);
        _ = p.WarmBudget();
        int zone = _zoneCalls;

        p.SetGames([a with { MaxStreamMib = 64, MaxStreamSet = true }]);
        _ = p.WarmBudget();

        Assert.True(_zoneCalls > zone);
    }

    [Fact]
    public void AnUnknownEstimateIsReusedUntilTheInputAppears()
    {
        Prewarmer p = Ranked(Game("a"));
        (_stamp, _zoneMib) = ("v2", null); // 계획이 사라짐
        _ = p.WarmBudget();
        int zone = _zoneCalls;

        _ = p.WarmBudget();
        Assert.Equal(zone, _zoneCalls);

        (_stamp, _zoneMib) = ("v3", 100); // 계획 파일이 생김
        _ = p.WarmBudget();
        Assert.True(_zoneCalls > zone);
    }

    [Fact]
    public void RunReadsTheListFreshEvenWhenTheBudgetEstimateIsCached()
    {
        GameSettings a = Game("a");
        Prewarmer p = Ranked(a);
        _ = p.WarmBudget();
        int zone = _zoneCalls;

        _ = p.Run(a, "service start"); // 읽을 범위를 정하는 호출은 서명이 같아도 새로 읽음

        Assert.True(_zoneCalls > zone);
    }

    // 메모리 한도로 앞부분 (windowMib 중 readMib) 만 읽고 멈춘 부분 예열 (목록 hotMib, 시작 가용 RAM startAvail).
    private Prewarmer Partial(GameSettings g, long hotMib, long startAvail, long windowMib, long readMib)
    {
        (_zoneMib, _avail) = (hotMib, startAvail);
        (_streamer.ReadMib, _streamer.TotalMib) = (readMib, windowMib);
        Prewarmer p = Make(g);
        p.WarmRank = list => list;
        _streamer.OnProgress = progress =>
        {
            _avail = 4096;
            progress(readMib * Mib, windowMib * Mib);
        };
        PrewarmResult r = p.Run(g, "test");
        Assert.True(r.Ran, r.SkipReason + " | " + string.Join("; ", _log.Lines));
        _streamer.OnProgress = null;
        (_streamer.ReadMib, _streamer.TotalMib) = (100, null);
        _now += TimeSpan.FromMinutes(3);
        return p;
    }

    [Fact]
    public void ASupplementThatOnlyWaitsForMemoryDoesNotParseTheListAgain()
    {
        GameSettings a = Game("a");
        Prewarmer p = Partial(a, 8000, 6000, 1648, 300);
        _avail = 4096 + Prewarmer.FinalShrinkHeadroomMib + 300 + 255; // 들어가는 범위가 읽은 양보다 255 MiB 만 큼: 읽지 않고 기다림
        int reads = _streamer.Calls;

        Assert.False(p.Supplement(a, CancellationToken.None));
        int zone = _zoneCalls;
        Assert.False(p.Supplement(a, CancellationToken.None));
        Assert.False(p.Supplement(a, CancellationToken.None));

        Assert.Equal(zone, _zoneCalls); // 두 번째부터 계획 해석 0
        Assert.Equal(reads, _streamer.Calls);
        Assert.Single(_log.Lines, l => l.Contains("a: supplement waits for memory (read 300 MiB, room for 555 MiB)", StringComparison.Ordinal));
    }

    [Fact]
    public void ASupplementThatReadsFetchesTheListFreshRightBeforeReading()
    {
        GameSettings a = Game("a");
        Prewarmer p = Partial(a, 8000, 6000, 1648, 300);
        _avail = 20000;
        _ = p.WarmBudget(); // 추정이 캐시에 있음
        (int zone, int reads) = (_zoneCalls, _streamer.Calls);

        Assert.True(p.Supplement(a, CancellationToken.None));

        Assert.True(_zoneCalls > zone); // 읽기 직전에 새로 읽음
        Assert.Equal(reads + 1, _streamer.Calls);
    }

    [Fact]
    public void WhenTheFreshListDiffersFromTheEstimateThisRoundDoesNotReadAndTheNextOneJudgesWithTheFreshValue()
    {
        GameSettings a = Game("a");
        Prewarmer p = Partial(a, 1000, 5000, 648, 500);
        _avail = 5100;
        _ = p.WarmBudget(); // 추정: 목록 1000 MiB
        _zoneMib = 3000; // 게임 파일이 커져 목록이 늘었지만 입력 파일의 서명은 같음
        (int reads, var before, int logs) = (_streamer.Calls, p.PartialInfo("a"), _log.Lines.Count);

        // 추정으로는 통째로 들어가 읽을 판이지만 읽기 직전의 새 값 (3000 MiB) 이 달라 이번 회는 읽지 않고, 상태도 로그도 바꾸지 않음
        Assert.False(p.Supplement(a, CancellationToken.None));
        Assert.Equal((reads, before, logs), (_streamer.Calls, p.PartialInfo("a"), _log.Lines.Count));

        // 다음 보충은 새 값으로 판정: 들어가는 범위 748 MiB 가 읽은 500 MiB 와 256 미만 차이라 기다림
        int zone = _zoneCalls;
        Assert.False(p.Supplement(a, CancellationToken.None));
        Assert.Equal(zone, _zoneCalls);
        Assert.Equal(reads, _streamer.Calls);
        Assert.Equal(1000, p.PartialInfo("a")!.Value.HotMib); // 기다리기만 한 회는 기억한 목록 크기를 올리지 않음 (상태는 읽기 직전에 새로 읽은 값으로만 고침)
        Assert.Single(_log.Lines, l => l.Contains("a: supplement waits for memory (read 500 MiB, room for 748 MiB)", StringComparison.Ordinal));
    }

    [Fact]
    public void AStaleLargerEstimateNeverRaisesTheRememberedListSizeAndTheNextRoundReads()
    {
        GameSettings a = Game("a");
        Prewarmer p = Partial(a, 1000, 5000, 648, 500);
        _avail = 20000;
        _zoneMib = 3000;
        _ = p.WarmBudget(); // 낡은 추정: 3000 MiB
        _zoneMib = 1000; // 게임 갱신으로 목록의 파일이 줄었지만 입력 파일의 서명은 같음
        int reads = _streamer.Calls;

        Assert.False(p.Supplement(a, CancellationToken.None)); // 새 값이 다르므로 이번 회는 읽지 않음
        Assert.Equal(reads, _streamer.Calls);
        Assert.Equal(1000, p.PartialInfo("a")!.Value.HotMib); // 추정값 3000 으로 올라가지 않음

        Assert.True(p.Supplement(a, CancellationToken.None)); // 다음 회는 새 값으로 판정해 읽음
        Assert.Equal(reads + 1, _streamer.Calls);
    }

    [Fact]
    public void WhileAGameRunsOnTheDiskOnlyTheCachedSourceEstimateIsReusedAndNoSourceIsRead()
    {
        Prewarmer p = Ranked(Game("a"));
        _running = true;
        _ = p.WarmBudget();
        (int observed, int cached) = (_observedCalls, _cachedCalls);
        Assert.True(cached > 0);

        _ = p.WarmBudget();
        Assert.Equal((observed, cached), (_observedCalls, _cachedCalls));

        _memoryGeneration++; // 출처 캐시가 채워짐: 캐시에서만 찾는 추정은 새로 계산하되 원본은 여전히 읽지 않음
        _ = p.WarmBudget();
        Assert.True(_cachedCalls > cached);
        Assert.Equal(observed, _observedCalls);
    }

    [Fact]
    public void CacheKeepsOnlyRetainedGamesAndRefreshesAfterTheMaxAge()
    {
        var cache = new WarmEstimateCache(() => _now);
        int calls = 0;
        long? Compute() => ++calls;

        Assert.Equal(1, cache.Get("zone", "a", "s", Compute));
        Assert.Equal(1, cache.Get("zone", "a", "s", Compute));
        Assert.Equal(2, cache.Get("zone", "a", "t", Compute)); // 서명이 달라짐
        Assert.Equal(3, cache.Get("zone", "a", null, Compute)); // 서명을 못 만들면 저장하지 않음
        Assert.Equal(4, cache.Get("zone", "a", null, Compute));
        _ = cache.Get("zone", "b", "s", Compute);
        _ = cache.Get("observed", "b", "s", Compute);
        Assert.Equal(3, cache.Count);

        cache.Retain(new HashSet<string>(["A"], StringComparer.OrdinalIgnoreCase));
        Assert.Equal(1, cache.Count);

        _now += WarmEstimateCache.MaxAge;
        Assert.Equal(7, cache.Get("zone", "a", "t", Compute));
    }

    [Fact]
    public void TheInputStampChangesForEachInputFileAndTheMemoryCacheGenerationAndNotOtherwise()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aster_estimate_stamp_" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(dir, "game");
        _ = Directory.CreateDirectory(root);
        try
        {
            string asset = Path.Combine(root, "asset.bin");
            File.WriteAllBytes(asset, new byte[8 * (int)Mib]);
            GameSettings game = Game("stamp") with
            {
                OriginRoot = root,
                AllowedRoots = [root],
                Plan = Path.Combine(dir, "stamp.plan.json"),
                Trace = Path.Combine(dir, "stamp.prefetch.json"),
            };
            string?[] files =
            [
                game.Plan, game.Trace, PlanOutput.WarmOnlyPath(game.Trace), AutoTracer.VolatilePath(game), AutoTracer.RawTracePath(game), AutoTracer.BootTracePath(game),
            ];
            string? last = PrefetchStreamer.EstimateStamp(game, false);
            Assert.Equal(last, PrefetchStreamer.EstimateStamp(game, false));
            foreach (string? file in files)
            {
                File.WriteAllText(file!, "{}"); // 생성
                string? created = PrefetchStreamer.EstimateStamp(game, false);
                Assert.NotEqual(last, created);

                File.WriteAllText(file!, "{ }"); // 길이 변경
                string? resized = PrefetchStreamer.EstimateStamp(game, false);
                Assert.NotEqual(created, resized);

                File.SetLastWriteTimeUtc(file!, DateTime.UtcNow.AddHours(-1)); // 수정 시각 변경
                last = PrefetchStreamer.EstimateStamp(game, false);
                Assert.NotEqual(resized, last);
            }

            Assert.NotEqual(last, PrefetchStreamer.EstimateStamp(game, true)); // 캐시 세대가 더해짐

            foreach (string? file in files.Where(f => f != game.Trace && f != AutoTracer.RawTracePath(game)))
            {
                File.Delete(file!); // 아래 파싱에는 목록과 원시 트레이스만 필요
            }

            PlanOutput.SavePrefetch(game.Trace, [new TraceEvent(asset, 0, 8 * Mib)]);
            PlanOutput.SavePrefetch(AutoTracer.RawTracePath(game), [new TraceEvent(asset, 6 * Mib, 2 * Mib)]);
            string? beforeParse = PrefetchStreamer.EstimateStamp(game, true);
            string? beforeParseFiles = PrefetchStreamer.EstimateStamp(game, false);
            _ = PrefetchStreamer.ObservedBytes(game); // 원시 트레이스를 파싱해 출처 캐시를 채움
            Assert.Equal(beforeParseFiles, PrefetchStreamer.EstimateStamp(game, false));
            string? afterParse = PrefetchStreamer.EstimateStamp(game, true);
            Assert.NotEqual(beforeParse, afterParse); // 캐시가 채워지면 캐시에서만 찾는 추정의 서명이 달라짐
            PrefetchStreamer.ForgetRemovedGames([game]);
            Assert.NotEqual(afterParse, PrefetchStreamer.EstimateStamp(game, true));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ChangingTheOriginRootOrTheStreamLimitModeRecomputesTheEstimates()
    {
        GameSettings a = Game("a");
        Prewarmer p = Ranked(a);
        _ = p.WarmBudget();
        int zone = _zoneCalls;

        p.SetGames([a with { OriginRoot = @"D:\Other" }]);
        _ = p.WarmBudget();
        Assert.True(_zoneCalls > zone);

        zone = _zoneCalls;
        p.SetGames([a with { OriginRoot = @"D:\Other", MaxStreamSet = true }]);
        _ = p.WarmBudget();
        Assert.True(_zoneCalls > zone);
    }
}
