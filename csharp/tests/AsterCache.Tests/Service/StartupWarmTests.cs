using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 서비스 시작 예열 다이어트: 플레이한 게임만, 부팅 60 초 + 디스크 20 초 유휴 뒤, 캐시가 따뜻하면 읽지 않음, 재시작 뒤 중복 없음.
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
    private string? _unreadableGame;
    private ProbeResult? _probe;
    private int _probeCalls;
    private int _probeWarmAfter = int.MaxValue; // 이 횟수를 넘는 표본은 따뜻함 (앞부분 예열이 데운 것처럼)
    private bool _rawTrace;
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
        DiskBytesPerSecond = g => _diskUnreadable || g.Name == _unreadableGame ? null : _diskBusy() ? 50 << 20 : 0,
        ProbeReadMs = _ =>
        {
            _probeCalls++;
            return _probeCalls > _probeWarmAfter ? new ProbeResult(8, 0, 0.3) : _probe;
        },
        RawTraceExists = _ => _rawTrace,
    }, _log, StatusPath, () => _now);

    [Fact]
    public void OneUnreadableDriveHoldsTheStartupWarmEvenIfTheOtherIsIdle()
    {
        Prewarmer p = Make(Game("a"), Game("b"));
        _unreadableGame = "b";
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
        Assert.Single(_log.Lines, l => l.Contains("held: disk activity cannot be measured", StringComparison.Ordinal));
    }

    private static GameSettings Game(string name) => Games.Make(name, name + ".exe");

    [Fact]
    public void StartupPrewarmIsHeldWhileTheGameIsRelocatingAndWaitsForIdleAfterwards()
    {
        Prewarmer p = Make(Game("a"));
        bool relocating = true;
        p.Relocating = _ => relocating;
        using var cts = new CancellationTokenSource();
        int sleeps = 0;
        TimeSpan freedAt = TimeSpan.Zero;
        _onSleep = () =>
        {
            if (++sleeps == 200)
            {
                Assert.Empty(_streamer.Calls); // 재배치 중에는 한 번도 읽지 않음
                relocating = false;
                freedAt = _waited;
            }

            if (sleeps > 400)
            {
                cts.Cancel();
            }
        };

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart(null, cts.Token);

        Assert.True(Assert.Single(results).Ran); // 재배치가 끝난 뒤 예열이 다시 실행됨
        Assert.True(_waited - freedAt >= TimeSpan.FromSeconds(15)); // 보류 동안 유휴 시간이 초기화됨 (PrewarmIdleNeeded 는 5 초 칸 3 번 기다린 뒤 통과)
        Assert.Equal(1, _log.Lines.Count(l => l.Contains("startup prewarm held while relocation is running", StringComparison.Ordinal)));
    }

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
    public void ServiceStartWaitsSixtySecondsAfterBootThenTwentySecondsOfIdleDisk()
    {
        Prewarmer p = Make(Game("a"));

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart();

        Assert.True(Assert.Single(results).Ran);
        Assert.InRange(_uptimeMs, 60_000 + 15_000, 60_000 + 20_000); // 부팅 60 초가 지난 뒤 유휴 20 초 (그 전에는 Sleep 이 읽기 0 을 확인)
        Assert.Contains(_log.Lines, l => l.Contains("waits for 60 s after boot and 20 s of idle disk", StringComparison.Ordinal));
        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void ServiceStartKeepsWaitingWhileTheDiskIsBusyAfterTheBootDelay()
    {
        Prewarmer p = Make(Game("a"));
        _diskBusy = () => _uptimeMs < 3 * 60_000; // 부팅 60 초가 지나도 3 분까지는 디스크가 바쁨

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart();

        Assert.True(Assert.Single(results).Ran);
        Assert.True(_uptimeMs >= 3 * 60_000 + 15_000, $"started too early: {_uptimeMs} ms"); // 바쁨이 끝난 뒤 20 초 유휴
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
    public void AGameThatWillSkipForTheCooldownDoesNotTakeTheStartupCapFromTheNextOne()
    {
        GameSettings[] games = [Game("a"), Game("b")];
        Prewarmer p = Make(games);
        p.SetAuto(AutoSettings.Default with { StartupWarmGames = 2, StartupWarmMaxMib = 150 }); // 게임마다 100 MiB
        Assert.True(p.Run(games[0], "test").Ran); // a 는 방금 읽어 쿨다운 중
        _streamer.Calls.Clear();
        _streamer.Seen.Clear();
        _streamer.BytesOf = _ =>
        {
            _now += TimeSpan.FromHours(1); // b 를 읽는 사이 a 의 쿨다운이 끝나도
            return 100;
        };

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart(games, default, null, PlayedDaysAgo(_now, ("a", 1), ("b", 2)));

        Assert.StartsWith("cooldown", results.Single(r => r.Game == "a").SkipReason, StringComparison.Ordinal);
        Assert.Equal(["b"], _streamer.Calls.Select(c => c.Game)); // b 만 읽음: 분류 때 정한 a 의 건너뜀이 상한 없는 읽기로 바뀌지 않음
        Assert.Equal([games[1].MaxStreamMib], _streamer.Seen.Select(g => g.MaxStreamMib)); // b 는 상한 150 을 통째로 받아 줄여 읽지 않음
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

    [Fact]
    public void RebootInsideTheCooldownWarmsWhenTheSampleIsColdAndReadsItOnce()
    {
        _rawTrace = true; // 원시 트레이스가 있으면 부팅 전 예열 시각이 쿨다운 기준에 그대로 쓰임
        Assert.True(Assert.Single(Make(Game("a")).RunServiceStart()).Ran);
        _callsBefore = 1;

        _now += TimeSpan.FromMinutes(2); // 재부팅: 부팅이 예열보다 뒤인데 쿨다운 (30 분) 안
        _uptimeMs = 60_000;
        _probe = new ProbeResult(8, 8, 9.0);
        _probeCalls = 0;
        PrewarmResult again = Assert.Single(Make(Game("a")).RunServiceStart());

        Assert.True(again.Ran);
        Assert.Equal(2, _streamer.Calls.Count);
        Assert.Equal(1, _probeCalls); // 분류의 표본을 쿨다운 판정과 본 예열이 다시 읽지 않고 씀
        Assert.Contains(_log.Lines, l => l.Contains("a: cooldown ignored, the cache is cold [service start]", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoGamesInsideTheCooldownKeepTheColdVerdictThroughTheHeadsAndTheMainWarm()
    {
        _rawTrace = true;
        Assert.Equal(2, Make(Game("a"), Game("b")).RunServiceStart().Count(r => r.Ran));
        _callsBefore = _streamer.Calls.Count;

        _now += TimeSpan.FromMinutes(2);
        _uptimeMs = 60_000;
        _probe = new ProbeResult(8, 8, 9.0);
        _probeWarmAfter = 2; // 분류의 표본 뒤에 다시 읽으면 앞부분이 데운 것처럼 따뜻함으로 나옴
        _probeCalls = 0;
        IReadOnlyList<PrewarmResult> again = Make(Game("a"), Game("b")).RunServiceStart();

        Assert.Equal(["a", "b"], again.Where(r => r.Ran).Select(r => r.Game).Order()); // 앞부분 예열 뒤에도 본 예열이 쿨다운으로 건너뛰지 않음
        Assert.Equal(2, _probeCalls); // 게임마다 분류에서 한 번뿐
    }

    [Fact]
    public void RestartedServiceWithAWarmSampleReadsNothingAgain()
    {
        _rawTrace = true;
        Assert.True(Assert.Single(Make(Game("a")).RunServiceStart()).Ran);
        _callsBefore = 1;

        _now += TimeSpan.FromMinutes(2); // 서비스만 재시작: 부팅은 예열보다 앞, 캐시는 살아 있음
        _uptimeMs = 2 * 3_600_000;
        _probe = new ProbeResult(8, 0, 0.3);
        PrewarmResult again = Assert.Single(Make(Game("a")).RunServiceStart());

        Assert.False(again.Ran);
        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void RebootInsideTheCooldownStillSkipsWhenTheSampleCannotBeRead()
    {
        _rawTrace = true;
        Assert.True(Assert.Single(Make(Game("a")).RunServiceStart()).Ran);
        _callsBefore = 1;

        _now += TimeSpan.FromMinutes(2);
        _uptimeMs = 60_000;
        _probe = null; // 표본을 못 읽음: 확인 불가는 쿨다운 유지
        PrewarmResult again = Assert.Single(Make(Game("a")).RunServiceStart());

        Assert.StartsWith("cooldown", again.SkipReason, StringComparison.Ordinal);
        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void StartupPrewarmWaitsBetweenGamesWhenARelocationStartsMidBatchThenContinues()
    {
        Prewarmer p = Make(Game("a"), Game("b"));
        bool relocating = false;
        bool freed = false;
        int held = 0;
        TimeSpan freedAt = TimeSpan.Zero;
        p.Relocating = _ =>
        {
            if (!freed && _streamer.Calls.Count == 1)
            {
                (relocating, _callsBefore) = (true, 1); // 첫 게임을 읽은 직후 재배치가 시작됨
            }

            return relocating && !freed;
        };
        _onSleep = () =>
        {
            if (relocating && !freed && ++held == 100)
            {
                Assert.Equal(["a"], _streamer.Calls.Select(c => c.Game)); // 재배치 중에는 다음 게임을 읽지 않음
                (freed, freedAt) = (true, _waited);
            }
        };

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart();

        Assert.Equal(["a", "b"], results.Where(r => r.Ran).Select(r => r.Game));
        Assert.Equal(["a", "b"], _streamer.Calls.Select(c => c.Game));
        Assert.True(held >= 100);
        Assert.True(_waited - freedAt >= TimeSpan.FromSeconds(55)); // 재배치가 끝난 뒤 유휴를 다시 기다림 (게임 사이 대기는 기존 60 초 유휴)
    }

    [Fact]
    public void StartupPrewarmCancelledWhileWaitingOutARelocationIsNotRecordedAndRunsAgainLater()
    {
        Prewarmer p = Make(Game("a"), Game("b"));
        bool relocating = false;
        bool over = false;
        p.Relocating = _ =>
        {
            if (!over && _streamer.Calls.Count == 1)
            {
                (relocating, _callsBefore) = (true, 1);
            }

            return relocating;
        };
        using var cts = new CancellationTokenSource();
        int held = 0;
        _onSleep = () =>
        {
            if (relocating && ++held == 20)
            {
                cts.Cancel(); // 게임 시작 등으로 취소
            }
        };

        IReadOnlyList<PrewarmResult> first = p.RunServiceStart(null, cts.Token);

        Assert.Equal(["a"], first.Where(r => r.Ran).Select(r => r.Game));
        Assert.Equal(Prewarmer.Cancelled, first.Single(r => r.Game == "b").SkipReason);
        Assert.Null(p.LastWarmed("b")); // 끝난 것으로 기록되지 않음: 다음 기회에 다시 읽음

        (relocating, over) = (false, true);
        _onSleep = null;
        IReadOnlyList<PrewarmResult> second = p.RunServiceStart();

        Assert.True(second.Single(r => r.Game == "b").Ran);
        Assert.Equal(["a", "b"], _streamer.Calls.Select(c => c.Game).Distinct());
        Assert.NotNull(p.LastWarmed("b"));
    }

    [Fact]
    public void StreamingOnDiskIsTrueOnlyWhileAStreamReadsTheSameDisk()
    {
        bool during = false;
        bool other = true;
        Prewarmer q = null!;
        var streamer = new ProbeStreamer(() => (during, other) = (q.StreamingOnDisk("a"), q.StreamingOnDisk("zzz")));
        q = new([Game("a")], streamer, new PrewarmEnv { AvailableRamMib = () => 16000, GameRunning = _ => false, ZoneBytes = _ => 1 << 20, TraceExists = _ => true }, _log, null, () => _now);

        Assert.False(q.StreamingOnDisk("a"));
        Assert.True(q.Run(Game("a"), "t").Ran);

        Assert.True(during);
        Assert.False(other); // 모르는 이름은 같은 디스크로 치지 않음
        Assert.False(q.StreamingOnDisk("a")); // 끝나면 풀림
    }

    private sealed class ProbeStreamer(Action onStream) : IGameStreamer
    {
        public StreamStats Stream(GameSettings game)
        {
            onStream();
            return new StreamStats(100, 2, 0, 0.01);
        }
    }
}
