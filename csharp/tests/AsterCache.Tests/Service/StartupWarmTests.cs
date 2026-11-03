using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 서비스 시작 예열 다이어트: 플레이한 게임만, 부팅 5 분 + 디스크 60 초 유휴 뒤, 캐시가 따뜻하면 읽지 않음, 재시작 뒤 중복 없음.
public sealed class StartupWarmTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_startup_" + Guid.NewGuid().ToString("N"));
    private readonly FakeStreamer _streamer = new();
    private readonly ListLog _log = new();
    private DateTimeOffset _now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private long _uptimeMs = 10_000;
    private int _callsBefore;
    private TimeSpan _waited;
    private Func<bool> _diskBusy = () => false;
    private bool _diskUnreadable;
    private ProbeResult? _probe;
    private Action? _onSleep;

    public StartupWarmTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string StatusPath => Path.Combine(_dir, "prewarm_status.json");

    private Prewarmer Make(params GameSettings[] games) => new(games, _streamer, new PrewarmEnv
    {
        AvailableRamMib = () => 16000,
        GameRunning = _ => false,
        ZoneBytes = _ => 100L << 20,
        TraceExists = _ => true,
        UptimeMs = () => _uptimeMs,
        Sleep = (t, _) =>
        {
            Assert.Equal(_callsBefore, _streamer.Calls.Count); // 기다리는 동안은 읽지 않음
            (_uptimeMs, _waited) = (_uptimeMs + (long)t.TotalMilliseconds, _waited + t);
            _onSleep?.Invoke();
        },
        DiskBytesPerSecond = _ => _diskUnreadable ? null : _diskBusy() ? 50 << 20 : 0,
        ProbeReadMs = _ => _probe,
    }, _log, StatusPath, () => _now);

    private static GameSettings Game(string name) => Games.Make(name, name + ".exe");

    [Fact]
    public void OnlyThePlayedGameIsWarmedAtServiceStartAmongSix()
    {
        GameSettings[] games = [.. Enumerable.Range(1, 6).Select(i => Game("g" + i))];
        Prewarmer p = Make(games);

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart(null, default, g => g.Name == "g6");

        Assert.Equal(["g6"], _streamer.Calls.Select(c => c.Game));
        Assert.Equal(["g6"], results.Where(r => r.Ran).Select(r => r.Game));
    }

    [Fact]
    public void ServiceStartWaitsFiveMinutesAfterBootThenAMinuteOfIdleDisk()
    {
        Prewarmer p = Make(Game("a"));
        _diskBusy = () => _uptimeMs < 6 * 60_000; // 부팅 5 분이 지나도 6 분까지는 디스크가 바쁨

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart();

        Assert.True(Assert.Single(results).Ran);
        Assert.True(_uptimeMs >= 6 * 60_000 + 55_000, $"started too early: {_uptimeMs} ms"); // 바쁨이 끝난 뒤 60 초 유휴
        Assert.Contains(_log.Lines, l => l.Contains("waits for 5 min after boot and 60 s of idle disk", StringComparison.Ordinal));
        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void UnreadableDiskThroughputHoldsTheStartupWarmAndLogsOnce()
    {
        Prewarmer p = Make(Game("a"));
        _diskUnreadable = true;
        using var cts = new CancellationTokenSource();
        int sleeps = 0;
        _onSleep = () =>
        {
            if (++sleeps > 100)
            {
                cts.Cancel();
            }
        };

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart(null, cts.Token);

        Assert.Empty(results);
        Assert.Empty(_streamer.Calls);
        Assert.True(_uptimeMs > 5 * 60_000); // 부팅 조건이 지난 뒤에도 읽지 않고 기다림
        Assert.Single(_log.Lines, l => l.Contains("held: disk activity cannot be measured", StringComparison.Ordinal));
    }

    private static Func<string, DateTimeOffset?> PlayedDaysAgo(DateTimeOffset now, params (string Name, int Days)[] plays) =>
        name => plays.Where(x => x.Name == name).Select(x => (DateTimeOffset?)(now - TimeSpan.FromDays(x.Days))).FirstOrDefault();

    [Fact]
    public void OnlyTheTwoMostRecentlyPlayedOfFiveGamesAreWarmedAtStart()
    {
        GameSettings[] games = [.. Enumerable.Range(1, 5).Select(i => Game("g" + i))];
        Prewarmer p = Make(games);

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart(games, default, null, PlayedDaysAgo(_now, ("g1", 5), ("g2", 4), ("g3", 3), ("g4", 2), ("g5", 1)));

        Assert.Equal(["g5", "g4"], _streamer.Calls.Select(c => c.Game));
        Assert.Equal(["g5", "g4"], results.Where(r => r.Ran).Select(r => r.Game));
    }

    [Fact]
    public void GamesNotPlayedWithinFourteenDaysAreNotWarmedAtStart()
    {
        GameSettings[] games = [Game("new"), Game("old")];
        Prewarmer p = Make(games);

        p.RunServiceStart(games, default, null, PlayedDaysAgo(_now, ("new", 1), ("old", 20)));

        Assert.Equal(["new"], _streamer.Calls.Select(c => c.Game));
    }

    [Fact]
    public void StartupWarmBeyondTheByteCapIsNotWarmed()
    {
        GameSettings[] games = [Game("a"), Game("b"), Game("c")];
        Prewarmer p = Make(games);
        p.SetAuto(AutoSettings.Default with { StartupWarmGames = 3, StartupWarmMaxMib = 150 }); // 게임마다 100 MiB

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart(games, default, null, PlayedDaysAgo(_now, ("a", 1), ("b", 2), ("c", 3)));

        Assert.Equal(["a", "b"], _streamer.Calls.Select(c => c.Game)); // c 는 상한을 다 쓴 뒤
        Assert.Equal([games[0].MaxStreamMib, 50], _streamer.Seen.Select(g => g.MaxStreamMib)); // b 는 남은 50 MiB 만
        Assert.Contains(results, r => r is { Game: "c", Ran: false, SkipReason: "startup byte cap" });
    }

    [Fact]
    public void WarmCacheMeansNoReadAndAColdOneStillStreams()
    {
        Prewarmer p = Make(Game("a"));
        _probe = new ProbeResult(8, 0, 0.3);

        PrewarmResult skipped = Assert.Single(p.RunServiceStart());
        Assert.Equal((false, "already warm"), (skipped.Ran, skipped.SkipReason));
        Assert.Empty(_streamer.Calls);

        _probe = new ProbeResult(8, 8, 9.0);
        Assert.True(Assert.Single(p.RunServiceStart()).Ran);
        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void RestartedServiceDoesNotWarmAgainButARebootDoes()
    {
        Assert.True(Assert.Single(Make(Game("a")).RunServiceStart()).Ran);
        Assert.Single(_streamer.Calls);
        _callsBefore = 1;

        _now += TimeSpan.FromMinutes(2); // 서비스만 재시작: 부팅은 예열보다 앞
        _uptimeMs = 2 * 3_600_000;
        PrewarmResult again = Assert.Single(Make(Game("a")).RunServiceStart());
        Assert.StartsWith("cooldown", again.SkipReason, StringComparison.Ordinal);
        Assert.Single(_streamer.Calls);

        _now += TimeSpan.FromMinutes(10); // 재부팅: 부팅이 예열보다 뒤라 파일 캐시가 비었음
        _uptimeMs = 60_000;
        Assert.True(Assert.Single(Make(Game("a")).RunServiceStart()).Ran);
        Assert.Equal(2, _streamer.Calls.Count);
    }
}
