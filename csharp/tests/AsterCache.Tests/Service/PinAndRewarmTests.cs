using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 상주 고정 (pin), 예열 유지 (re-prewarm), 묶음 예열의 RAM 예산. 바깥 세계는 전부 가짜.
public sealed class PinAndRewarmTests
{
    private const long Gib = 1L << 30;
    private readonly FakeStreamer _streamer = new();
    private readonly ListLog _log = new();
    private readonly List<FakePin> _pins = [];
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    private long _avail = 16000;
    private long _ram = 32 * Gib;
    private bool _running;
    private bool _launcherRunning;
    private double? _cpu = 1;
    private double? _disk = 0;
    private int _cpuSamples;
    private double? _probe = 0.5;
    private int _probeCalls;
    private long _zoneMib = 1610;
    private Action? _onOpen;

    private sealed class FakePin(IReadOnlyList<string> paths) : IDisposable
    {
        public IReadOnlyList<string> Paths { get; } = paths;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private Prewarmer Make(params GameSettings[] games) => new(games, _streamer, new PrewarmEnv
    {
        AvailableRamMib = () => _avail,
        GameRunning = _ => _running,
        ZoneBytes = _ => _zoneMib << 20,
        TraceExists = _ => true,
        PhysicalRamBytes = () => _ram,
        LauncherRunning = () => _launcherRunning,
        CpuPercent = () =>
        {
            _cpuSamples++;
            return _cpu;
        },
        DiskBytesPerSecond = _ => _disk,
        PinPaths = g => [$@"D:\Games\{g.Name}\a.bin", $@"D:\Games\{g.Name}\b.bin"],
        OpenPin = paths =>
        {
            var pin = new FakePin(paths);
            _pins.Add(pin);
            _onOpen?.Invoke();
            return new PinHandle(pin, paths.Count, 0);
        },
        ProbeReadMs = _ =>
        {
            _probeCalls++;
            return _probe is { } ms ? new ProbeResult(8, ms > CacheProbe.ColdThresholdMs ? 8 : 0, ms) : null;
        },
    }, _log, null, () => _now);

    private static GameSettings Game(string name, bool pin = true, params string[] triggers) =>
        Games.Make(name, name + ".exe") with { Prewarm = PrewarmSettings.Default with { Pin = pin, PinSet = true, TriggerExes = triggers } };

    private static AutoSettings Auto(int rewarmMinutes = 10) => AutoSettings.Default with { RewarmMinutes = rewarmMinutes };

    // ---- 고정 ----

    [Theory]
    [InlineData(32, true, true)]
    [InlineData(16, true, true)]
    [InlineData(15, true, false)] // 16 GiB 미만이면 고정 안 함
    [InlineData(32, false, false)] // 게임 설정에서 pin 이 꺼져 있음
    public void PrewarmPinsTheZoneFilesOnlyWhenPinIsOnAndRamIsAtLeast16GiB(long ramGib, bool pin, bool expected)
    {
        _ram = ramGib * Gib;
        GameSettings g = Game("a", pin);
        Prewarmer p = Make(g);

        Assert.True(p.Run(g, "service start").Ran);

        Assert.Equal(expected ? ["a"] : [], p.Pinned);
        Assert.Equal(expected ? 1 : 0, _pins.Count);
        if (expected)
        {
            Assert.Equal([@"D:\Games\a\a.bin", @"D:\Games\a\b.bin"], _pins[0].Paths);
            Assert.Contains(_log.Lines, l => l.Contains("a: pinned 2 file(s), 0 skipped", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(23, false)]
    [InlineData(24, true)]
    [InlineData(64, true)]
    public void UnsetPinTurnsOnAutomaticallyFrom24GiB(long ramGib, bool expected)
    {
        _ram = ramGib * Gib;
        GameSettings g = Game("a") with { Prewarm = PrewarmSettings.Default }; // pin 키 없음
        Prewarmer p = Make(g);

        Assert.True(p.Run(g, "service start").Ran);

        Assert.Equal(expected ? ["a"] : [], p.Pinned);
    }

    [Theory]
    [InlineData(64, false)] // 명시한 false 는 RAM 이 커도 그대로
    [InlineData(16, true)] // 명시한 true 는 16 GiB 이상이면 (자동 하한 24 GiB 와 무관)
    public void ExplicitPinIsNeverOverriddenByTheAutomaticRule(long ramGib, bool pin)
    {
        _ram = ramGib * Gib;
        GameSettings g = Game("a", pin);
        Prewarmer p = Make(g);

        Assert.True(p.Run(g, "service start").Ran);

        Assert.Equal(pin ? ["a"] : [], p.Pinned);
    }

    [Fact]
    public void ParsedPinRemembersWhetherTheKeyWasWritten()
    {
        Assert.False(PinKeeper.Wanted(PrewarmSettings.Default, 16 * Gib));
        Assert.True(PinKeeper.Wanted(PrewarmSettings.Default, 24 * Gib));
        Assert.False(PinKeeper.Wanted(PrewarmSettings.Default with { PinSet = true }, 64 * Gib));
    }

    [Fact]
    public void AnyRunningLauncherHoldsThePinBackAndALaterRewarmPinsOnce()
    {
        _launcherRunning = true;
        GameSettings g = Game("a");
        Prewarmer p = Make(g);

        Assert.True(p.Run(g, "service start").Ran);

        Assert.Empty(p.Pinned);
        Assert.Empty(_pins);
        Assert.Contains(_log.Lines, l => l.Contains("a: pin held back (a launcher is running", StringComparison.Ordinal));

        _launcherRunning = false;
        _probe = 5.0;
        p.Tick(Auto(10));
        _now += TimeSpan.FromMinutes(10);
        p.Tick(Auto(10));

        Assert.Equal(["a"], p.Pinned);
    }

    [Theory]
    [InlineData(1610L, 32L, 100000L, 4096, 1610L)] // 존이 가장 작음
    [InlineData(1610L, 4L, 100000L, 4096, 1024L)] // 물리 RAM 의 1/4 = 1 GiB
    [InlineData(1610L, 32L, 5000L, 4096, 904L)] // 가용 5000 − 여유 4096 = 904 MiB
    [InlineData(1610L, 32L, 4000L, 4096, 0L)] // 여유가 가용보다 크면 0
    [InlineData(null, 32L, 100000L, 4096, 8192L)] // 존 크기를 모르면 RAM 상한만
    [InlineData(0L, 32L, 100000L, 4096, 8192L)] // 존이 비어 있어도 마찬가지
    public void PinCapIsTheSmallestOfZoneQuarterRamAndAvailableMinusReserve(long? zoneMib, long ramGib, long availMib, int reserveMib, long expectedMib)
    {
        long cap = PrefetchStreamer.PinCapBytes(zoneMib is null ? null : zoneMib << 20, ramGib * Gib, availMib, reserveMib);

        Assert.Equal(expectedMib << 20, cap);
    }

    [Fact]
    public void OnlyTheLeadingFilesThatFitUnderTheCapArePinned()
    {
        string[] paths = ["a", "b", "c", "d"];
        var sizes = new Dictionary<string, long> { ["a"] = 40, ["b"] = 40, ["c"] = 40, ["d"] = 5 };

        Assert.Equal(["a", "b"], PrefetchStreamer.WithinCap(paths, 100, p => sizes[p]));
        Assert.Equal(["a", "b", "c", "d"], PrefetchStreamer.WithinCap(paths, long.MaxValue, p => sizes[p]));
        Assert.Empty(PrefetchStreamer.WithinCap(paths, 39, p => sizes[p]));
    }

    [Fact]
    public void SkippedPrewarmDoesNotPin()
    {
        _running = true;
        GameSettings g = Game("a");
        Prewarmer p = Make(g);

        Assert.False(p.Run(g, "service start").Ran);

        Assert.Empty(p.Pinned);
    }

    [Fact]
    public void ARepeatedPrewarmKeepsTheSinglePin()
    {
        GameSettings g = Game("a");
        Prewarmer p = Make(g);

        p.Run(g, "first");
        _now += TimeSpan.FromHours(1);
        p.Run(g, "second");

        Assert.Single(_pins);
        Assert.False(_pins[0].Disposed);
    }

    [Fact]
    public void GameStartReleasesOnlyThatGamesPin()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        Prewarmer p = Make(a, b);
        p.RunAll([a, b], "x");

        p.ReleasePin("a", "game started");

        Assert.Equal(["b"], p.Pinned);
        Assert.True(_pins[0].Disposed);
        Assert.False(_pins[1].Disposed);
        Assert.Contains(_log.Lines, l => l.Contains("a: pin released (game started)", StringComparison.Ordinal));
    }

    [Fact]
    public void LauncherStartReleasesEveryPin()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        Prewarmer p = Make(a, b);
        p.RunAll([a, b], "x");

        p.ReleasePins("launcher steam.exe started");

        Assert.Empty(p.Pinned);
        Assert.All(_pins, x => Assert.True(x.Disposed));
        Assert.Contains(_log.Lines, l => l.Contains("pin released (launcher steam.exe started)", StringComparison.Ordinal));
    }

    [Fact]
    public void LowAvailableRamReleasesThePinOnTheNextTickOnly()
    {
        GameSettings g = Game("a");
        Prewarmer p = Make(g);
        p.Run(g, "x");

        _avail = 4096; // min_free_ram_mib 와 같으면 아직 풀지 않음
        p.Tick(Auto(0));
        Assert.Equal(["a"], p.Pinned);

        _avail = 4095;
        p.Tick(Auto(0));

        Assert.Empty(p.Pinned);
        Assert.Contains(_log.Lines, l => l.Contains("pin released (low memory", StringComparison.Ordinal));
    }

    [Fact]
    public void TurningPinOffOrRemovingTheGameInSettingsReleasesIt()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        GameSettings c = Game("c");
        Prewarmer p = Make(a, b, c);
        p.RunAll([a, b, c], "x");

        p.SetGames([a with { Prewarm = a.Prewarm with { Pin = false } }, b]);

        Assert.Equal(["b"], p.Pinned);
        Assert.Contains(_log.Lines, l => l.Contains("a: pin released (pin turned off", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("c: pin released (pin turned off or game removed", StringComparison.Ordinal));
    }

    [Fact]
    public void ServiceShutdownReleasesEverything()
    {
        GameSettings g = Game("a");
        Prewarmer p = Make(g);
        p.Run(g, "x");

        p.Shutdown();

        Assert.Empty(p.Pinned);
        Assert.True(_pins[0].Disposed);
        Assert.Contains(_log.Lines, l => l.Contains("pin released (service stopping)", StringComparison.Ordinal));
    }

    [Fact]
    public void GameThatStartsWhileFilesAreBeingPinnedDropsThePinAtOnce()
    {
        GameSettings g = Game("a");
        Prewarmer p = Make(g);
        _onOpen = () => _running = true;

        p.Run(g, "x");

        Assert.Empty(p.Pinned);
        Assert.True(_pins[0].Disposed);
    }

    [Fact]
    public void PrewarmTriggeredByALauncherDoesNotPinButOtherTriggersDo()
    {
        GameSettings viaLauncher = Game("a", true, "steam.exe");
        GameSettings viaOther = Game("b", true, "custom.exe");
        Prewarmer p = Make(viaLauncher, viaOther);

        Assert.True(Assert.Single(p.OnProcessStart(new ProcessStart(1, "steam.exe", 0))).Ran);
        Assert.Empty(p.Pinned); // 런처 업데이트가 파일을 고칠 수 있게
        Assert.True(Assert.Single(p.OnProcessStart(new ProcessStart(2, "custom.exe", 0))).Ran);

        Assert.Equal(["b"], p.Pinned);
    }

    [Fact]
    public void WatcherNamesIncludeEveryLauncherOnlyWhenSomeGamePins()
    {
        var withPin = new AsterSettings(50, [Game("a", true)]);
        var autoPin = new AsterSettings(50, [Game("a", true) with { Prewarm = PrewarmSettings.Default }]);
        var withoutPin = new AsterSettings(50, [Game("a", false)]);

        Assert.All(LauncherExes.All, e => Assert.Contains(e, Worker.WatchNames(withPin)));
        Assert.DoesNotContain("steam.exe", Worker.WatchNames(withoutPin));
        Assert.DoesNotContain("steam.exe", Worker.WatchNames(autoPin, 23 * Gib)); // pin 을 적지 않은 자동 기본은 RAM 24 GiB 부터
        Assert.Contains("steam.exe", Worker.WatchNames(autoPin, 24 * Gib));
    }

    // ---- 예열 유지 ----

    private Prewarmer WarmedGame()
    {
        GameSettings g = Game("a", pin: false);
        Prewarmer p = Make(g);
        Assert.True(p.Run(g, "service start").Ran);
        return p;
    }

    private void TickAt(Prewarmer p, TimeSpan after, int minutes = 10)
    {
        _now += after;
        p.Tick(Auto(minutes));
    }

    [Fact]
    public void WarmHotSetIsLeftAlone()
    {
        Prewarmer p = WarmedGame();
        TickAt(p, TimeSpan.Zero); // 주기 시작
        _probe = 0.5;

        TickAt(p, TimeSpan.FromMinutes(10));

        Assert.Equal(1, _probeCalls);
        Assert.Single(_streamer.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("a: rewarm check: warm", StringComparison.Ordinal));
    }

    [Fact]
    public void ColdHotSetIsPrewarmedAgainEvenInsideTheCooldown()
    {
        Prewarmer p = WarmedGame();
        TickAt(p, TimeSpan.Zero);
        _probe = 5.0;

        TickAt(p, TimeSpan.FromMinutes(10)); // 쿨다운 30 분 안

        Assert.Equal(2, _streamer.Calls.Count);
        Assert.Contains(_log.Lines, l => l.Contains("a: rewarm check: cold", StringComparison.Ordinal) && l.Contains("cooldown ignored", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("a: prewarm done (rewarm)", StringComparison.Ordinal));
    }

    [Fact]
    public void ThresholdIsExclusive()
    {
        Prewarmer p = WarmedGame();
        TickAt(p, TimeSpan.Zero);
        _probe = 2.0; // ColdThresholdMs 와 같으면 아직 따뜻함

        TickAt(p, TimeSpan.FromMinutes(10));

        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void NothingHappensWhileAnyGameRuns()
    {
        Prewarmer p = WarmedGame();
        TickAt(p, TimeSpan.Zero);
        _probe = 5.0;
        _running = true;

        TickAt(p, TimeSpan.FromMinutes(10));

        Assert.Equal(0, _probeCalls);
        Assert.Single(_streamer.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("rewarm check skipped: a game is running", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("disk-unknown")]
    public void BusyCpuOrGameDriveSkipsTheCheckAndUnknownCountsAsBusy(string busy)
    {
        Prewarmer p = WarmedGame();
        TickAt(p, TimeSpan.Zero);
        _probe = 5.0;
        switch (busy)
        {
            case "disk": _disk = DiskActivity.BusyBytesPerSecond; break;
            default: _disk = null; break;
        }

        TickAt(p, TimeSpan.FromMinutes(10));

        Assert.Equal(0, _probeCalls);
        Assert.Single(_streamer.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("rewarm check skipped", StringComparison.Ordinal) && l.Contains("busy", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(90.0)]
    [InlineData(null)]
    public void BusyOrUnknownCpuDoesNotSkipTheRewarmWhenTheDiskIsQuiet(double? cpu)
    {
        Prewarmer p = WarmedGame();
        TickAt(p, TimeSpan.Zero);
        _probe = 5.0;
        _cpu = cpu;

        TickAt(p, TimeSpan.FromMinutes(10));

        Assert.Equal(1, _probeCalls);
        Assert.Equal(2, _streamer.Calls.Count);
    }

    [Fact]
    public void QuietJustBelowTheBusyLinesStillChecksAndTheCpuIsSampledEveryTick()
    {
        Prewarmer p = WarmedGame();
        _cpu = IdleTracker.CpuBusyPercent - 0.1;
        _disk = DiskActivity.BusyBytesPerSecond - 1;
        TickAt(p, TimeSpan.Zero);
        int before = _cpuSamples;

        TickAt(p, TimeSpan.FromMinutes(5)); // 점검 주기 전에도 CPU 표본은 갱신
        TickAt(p, TimeSpan.FromMinutes(5));

        Assert.Equal(before + 2, _cpuSamples);
        Assert.Equal(1, _probeCalls);
    }

    [Fact]
    public void ColdButLowRamDoesNotPrewarm()
    {
        Prewarmer p = WarmedGame();
        TickAt(p, TimeSpan.Zero);
        _probe = 5.0;
        _avail = 3000; // 존 1610 + 여유 4096 에 모자람

        TickAt(p, TimeSpan.FromMinutes(10));

        Assert.Single(_streamer.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("a: prewarm skipped (low memory", StringComparison.Ordinal));
    }

    [Fact]
    public void CheckHappensOnlyEveryRewarmMinutesAndNeverWhenOff()
    {
        Prewarmer p = WarmedGame();
        TickAt(p, TimeSpan.Zero);
        TickAt(p, TimeSpan.FromMinutes(9));
        Assert.Equal(0, _probeCalls);

        TickAt(p, TimeSpan.FromMinutes(1));
        Assert.Equal(1, _probeCalls);

        TickAt(p, TimeSpan.FromMinutes(5));
        Assert.Equal(1, _probeCalls);

        TickAt(p, TimeSpan.FromMinutes(60), minutes: 0);
        TickAt(p, TimeSpan.FromMinutes(60), minutes: 0);
        Assert.Equal(1, _probeCalls);
    }

    [Fact]
    public void GamesNeverPrewarmedAreNotChecked()
    {
        GameSettings g = Game("a", pin: false);
        Prewarmer p = Make(g);
        TickAt(p, TimeSpan.Zero);

        TickAt(p, TimeSpan.FromMinutes(10));

        Assert.Equal(0, _probeCalls);
    }

    [Fact]
    public void UnreadableSamplesAreReportedNotTreatedAsCold()
    {
        Prewarmer p = WarmedGame();
        TickAt(p, TimeSpan.Zero);
        _probe = null;

        TickAt(p, TimeSpan.FromMinutes(10));

        Assert.Single(_streamer.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("rewarm check skipped (no readable samples)", StringComparison.Ordinal));
    }

    // ---- 묶음 예열의 RAM 예산 ----

    [Fact]
    public void BatchStopsWhenTheZonesAddUpPastAvailableRamMinusReserve()
    {
        GameSettings[] games = [Game("a", false), Game("b", false), Game("c", false), Game("d", false)];
        Prewarmer p = Make(games);
        _avail = 8000; // 가용 − 여유 = 3904 → 존 1610 은 둘까지 (3220), 셋째 (4830) 부터 자름

        IReadOnlyList<PrewarmResult> results = p.RunAll(games, "recent");

        Assert.Equal(["a", "b"], _streamer.Calls.Select(c => c.Game));
        Assert.Equal([true, true, false, false], results.Select(r => r.Ran));
        Assert.Equal("batch memory budget", results[2].SkipReason);
        Assert.Contains(_log.Lines, l => l.Contains("c: prewarm skipped (batch zones", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("d: prewarm skipped (batch zones", StringComparison.Ordinal));
    }

    [Fact]
    public void BatchWithEnoughRamWarmsEveryGame()
    {
        GameSettings[] games = [Game("a", false), Game("b", false), Game("c", false)];
        Prewarmer p = Make(games);
        _avail = 40000;

        p.RunAll(games, "recent");

        Assert.Equal(["a", "b", "c"], _streamer.Calls.Select(c => c.Game));
    }

    [Fact]
    public void BatchBudgetIsAtMostAQuarterOfPhysicalRamEvenWhenMoreIsAvailable()
    {
        GameSettings[] games = [Game("a", false), Game("b", false), Game("c", false)];
        Prewarmer p = Make(games);
        _ram = 8 * Gib; // 1/4 = 2048 MiB: 존 1610 은 하나만
        _avail = 40000;

        IReadOnlyList<PrewarmResult> results = p.RunAll(games, "recent");

        Assert.Equal(["a"], _streamer.Calls.Select(c => c.Game));
        Assert.Equal([true, false, false], results.Select(r => r.Ran));
        Assert.Equal("batch memory budget", results[1].SkipReason);
    }

    [Fact]
    public void FirstGameInABatchIsLeftToItsOwnMemoryCheck()
    {
        GameSettings[] games = [Game("a", false), Game("b", false)];
        Prewarmer p = Make(games);
        _avail = 3000;

        IReadOnlyList<PrewarmResult> results = p.RunAll(games, "recent");

        Assert.All(results, r => Assert.False(r.Ran));
        Assert.StartsWith("low memory", results[0].SkipReason, StringComparison.Ordinal);
    }

    // ---- 작업자 연결 ----

    [Fact]
    public async Task WorkerReleasesPinsWhenTheGameOrALauncherStarts()
    {
        GameSettings g = Game("a") with { Prewarm = PrewarmSettings.Default with { OnServiceStart = false, Pin = true } };
        foreach ((string started, string reason) in new[] { ("a.exe", "pin released (game started)"), ("steam.exe", "pin released (launcher steam.exe started)") })
        {
            Prewarmer p = Make(g);
            p.Run(g, "x");
            Assert.Equal(["a"], p.Pinned);
            var emitted = new TaskCompletionSource();
            var worker = new Worker(new AsterSettings(50, [g]), _log, new StartOnce(new ProcessStart(5, started, 0), emitted), null, new FakeStreamer(),
                new ReapplyChecker(ReapplyTests.NoIoEnv(), _log), prewarm: p);

            await worker.StartAsync(CancellationToken.None);
            await emitted.Task;
            await worker.StopAsync(CancellationToken.None);

            Assert.Empty(p.Pinned);
            Assert.Contains(_log.Lines, l => l.Contains(reason, StringComparison.Ordinal)); // 종료 때의 해제가 아니라 시작 알림이 풀었음
        }
    }

    private sealed class StartOnce(ProcessStart start, TaskCompletionSource emitted) : IProcessWatcher
    {
        public async Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
        {
            onStart(start);
            emitted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 취소는 정상 종료
            }
        }
    }
}
