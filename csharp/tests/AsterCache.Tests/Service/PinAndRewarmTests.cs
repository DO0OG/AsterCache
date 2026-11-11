using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
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
    private readonly FakeRecovery _recovery = new();
    private Queue<long>? _availSeq;
    private readonly Dictionary<string, long> _sizes = [];
    private readonly Dictionary<string, FakePin> _activePins = new(StringComparer.OrdinalIgnoreCase);

    private sealed class FakeRecovery : IDriveRecovery
    {
        public bool Recovering { get; set; } = true;

        public bool IsRecovering(string drive) => Recovering;

        public bool? WatchUpdate(string drive, DateTimeOffset updatedAt, TimeSpan limit) => null;
    }

    private sealed class FakePin(IReadOnlyList<string> paths) : IDisposable
    {
        private readonly List<string> _paths = [.. paths];

        public IReadOnlyList<string> Paths => _paths;

        public bool Disposed { get; private set; }

        public void AddPaths(IReadOnlyList<string> paths) => _paths.AddRange(paths);

        public void Dispose() => Disposed = true;
    }

    private sealed class ThrowingPin : IDisposable
    {
        public void Dispose() => throw new InvalidOperationException("dispose failed");
    }

    private Prewarmer Make(params GameSettings[] games) => new(games, _streamer, new PrewarmEnv
    {
        AvailableRamMib = () => _availSeq is { Count: > 0 } q ? q.Dequeue() : _avail,
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
        FileStamp = path => new Fingerprint(path, _sizes.GetValueOrDefault(path), 0),
        PinPaths = g => [$@"D:\Games\{g.Name}\a.bin", $@"D:\Games\{g.Name}\b.bin"],
        OpenPin = paths =>
        {
            string gameName = Path.GetFileName(Path.GetDirectoryName(paths[0]))!;
            if (!_activePins.TryGetValue(gameName, out FakePin? pin) || pin.Disposed)
            {
                pin = new FakePin([]);
                _activePins[gameName] = pin;
                _pins.Add(pin);
            }

            pin.AddPaths(paths);
            _onOpen?.Invoke();
            return new PinHandle(pin, paths.Count, 0);
        },
        ProbeReadMs = _ =>
        {
            _probeCalls++;
            return _probe is { } ms ? new ProbeResult(8, ms > CacheProbe.ColdThresholdMs ? 8 : 0, ms) : null;
        },
    }, _log, null, () => _now)
    {
        Recovery = _recovery,
    };

    [Fact]
    public void PinningStopsBeforeTheNextFileWhenAGameStartsAndReleasesOpenedHandles()
    {
        GameSettings game = Game("a");
        _onOpen = () => // 첫 파일을 연 뒤, 재확인 간격(50 ms)이 지난 뒤 다음 파일 전에 게임 시작
        {
            _running = true;
            _now += TimeSpan.FromMilliseconds(50);
        };
        var env = new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = _ => _running,
            ZoneBytes = _ => _zoneMib << 20,
            TraceExists = _ => true,
            PhysicalRamBytes = () => _ram,
            PinPaths = _ => [@"D:\Games\a\a.bin", @"D:\Games\a\b.bin"],
            OpenPin = paths =>
            {
                var pin = new FakePin(paths);
                _pins.Add(pin);
                _onOpen?.Invoke();
                return new PinHandle(pin, paths.Count, 0);
            },
        };
        var keeper = new PinKeeper(env, _log, () => _now);

        keeper.TryPin(game);

        Assert.Single(_pins);
        Assert.True(_pins[0].Disposed);
        Assert.Empty(keeper.Held);
        Assert.Contains(_log.Lines, line => line.Contains("a: pin deferred (a game started while pinning", StringComparison.Ordinal));
    }

    [Fact]
    public void StopChecksWithinTheIntervalReuseOneProcessSnapshot()
    {
        GameSettings game = Game("a");
        int gameChecks = 0;
        var env = new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = _ =>
            {
                gameChecks++;
                return false;
            },
            ZoneBytes = _ => _zoneMib << 20,
            TraceExists = _ => true,
            PhysicalRamBytes = () => _ram,
            PinPaths = _ => [@"D:\Games\a\1.bin", @"D:\Games\a\2.bin", @"D:\Games\a\3.bin", @"D:\Games\a\4.bin", @"D:\Games\a\5.bin"],
            OpenPin = paths =>
            {
                var pin = new FakePin(paths);
                _pins.Add(pin);
                return new PinHandle(pin, paths.Count, 0);
            },
        };
        var keeper = new PinKeeper(env, _log, () => _now);

        keeper.TryPin(game);

        Assert.Equal(5, _pins.Count);
        Assert.Equal(4, gameChecks); // 경로 전 확인 2회 + 파일 다섯 개 사이 재확인 스냅샷 1회 + 열기 뒤 확인 1회
    }

    [Fact]
    public void PinningOpensNothingWhileTheDiskIsYieldedToAGameAndRetriesAfterItEnds()
    {
        GameSettings g = Game("a");
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log, () => 0);
        int pathLookups = 0;
        var env = new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            ZoneBytes = _ => _zoneMib << 20,
            TraceExists = _ => true,
            GameRunning = _ => false, // 시작 이벤트를 놓쳐 게임 판정은 아직 모름
            PhysicalRamBytes = () => _ram,
            PinPaths = _ =>
            {
                pathLookups++;
                return [@"D:\Games.bin"];
            },
            OpenPin = paths =>
            {
                var pin = new FakePin(paths);
                _pins.Add(pin);
                return new PinHandle(pin, paths.Count, 0);
            },
        };
        var keeper = new PinKeeper(env, _log, () => _now) { Coordinator = coordinator };
        coordinator.Yield(g.OriginRoot, "game started", g.Name);

        keeper.TryPin(g);

        Assert.Equal(0, pathLookups); // 경로 해석도 파일 열기도 하지 않음
        Assert.Empty(_pins);
        Assert.Contains(_log.Lines, l => l.Contains("a: pin deferred (disk reads are yielded", StringComparison.Ordinal));

        coordinator.Resume(g.OriginRoot, g.Name);
        keeper.TryPin(g);

        Assert.Single(_pins);
    }

    private static GameSettings Game(string name, bool pin = true, params string[] triggers) =>
        Games.Make(name, name + ".exe") with { Prewarm = PrewarmSettings.Default with { Pin = pin, PinSet = true, TriggerExes = triggers } };

    private static AutoSettings Auto(int rewarmMinutes = 10) => AutoSettings.Default with { RewarmMinutes = rewarmMinutes };

    // ---- 고정 ----

    [Fact]
    public void AGameUnregisteredWhileItWasBeingPrewarmedIsNotPinned()
    {
        _ram = 64 * Gib;
        GameSettings g = Game("a");
        Prewarmer p = Make(g);
        p.WarmMeasured += (_, _) => p.SetGames([]); // 읽기가 끝난 직후 설정 재적재로 등록이 해제됨

        Assert.True(p.Run(g, "service start").Ran);

        Assert.Empty(p.Pinned);
        Assert.Empty(_pins);
    }

    [Fact]
    public void APinOpenedWhileTheGameGetsUnregisteredIsDiscardedNotKept()
    {
        _ram = 64 * Gib;
        GameSettings g = Game("a");
        Prewarmer p = Make(g);
        _onOpen = () => p.SetGames([]); // 파일을 여는 도중 설정 재적재로 등록이 해제됨 (Reconcile 은 아직 저장 안 된 pin 을 못 봄)

        Assert.True(p.Run(g, "service start").Ran);

        Assert.Empty(p.Pinned);
        Assert.NotEmpty(_pins);
        Assert.All(_pins, pin => Assert.True(pin.Disposed));
    }

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
    [InlineData("vanguard")]
    [InlineData("eac")]
    [InlineData("battleye")]
    [InlineData("unknown")]
    [InlineData("ricochet")]
    public void AntiCheatGamesAreNeverPinnedEvenWhenPinIsOnAndRamIsPlentiful(string kind)
    {
        _ram = 64 * Gib;
        GameSettings g = Game("a") with { AntiCheat = kind };
        Prewarmer p = Make(g);

        Assert.True(p.Run(g, "service start").Ran); // 읽기 전용 예열은 그대로
        Assert.True(p.Run(g, "again", ignoreCooldown: true).Ran);

        Assert.Empty(p.Pinned);
        Assert.Empty(_pins);
        Assert.Single(_log.Lines, l => l.Contains($"a: pin skipped (anti-cheat: {kind})", StringComparison.Ordinal));
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
    public void HeldPinIsLoggedOncePerGameWhileTheReasonStaysTheSame()
    {
        _launcherRunning = true;
        GameSettings g = Game("a");
        Prewarmer p = Make(g);

        _ = p.Run(g, "service start");
        _ = p.Run(g, "service start");

        Assert.Equal(1, _log.Lines.Count(l => l.Contains("a: pin held back", StringComparison.Ordinal)));
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
    public void SettingAnAntiCheatKindOnAPinnedGameReleasesTheExistingPin()
    {
        GameSettings a = Game("a");
        Prewarmer p = Make(a);
        p.RunAll([a], "x");
        Assert.Equal(["a"], p.Pinned);

        p.SetGames([a with { AntiCheat = "vanguard" }]);

        Assert.Empty(p.Pinned);
        Assert.Contains(_log.Lines, l => l.Contains("a: pin released (anti-cheat set in settings", StringComparison.Ordinal));
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
    public void ReleaseStillFreesTheOtherHandlesWhenAnEarlierDisposeThrows()
    {
        GameSettings g = Game("a");
        var rest = new FakePin([]);
        int opens = 0;
        var env = new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = _ => false,
            ZoneBytes = _ => _zoneMib << 20,
            TraceExists = _ => true,
            PhysicalRamBytes = () => _ram,
            PinPaths = _ => [@"D:\Games.bin", @"D:\Games.bin"],
            OpenPin = _ => new PinHandle(++opens == 1 ? new ThrowingPin() : rest, 1, 0),
        };
        var keeper = new PinKeeper(env, _log, () => _now);
        keeper.TryPin(g);

        keeper.Release("a", "test");

        Assert.True(rest.Disposed);
        Assert.Empty(keeper.Held);
    }

    [Fact]
    public void StopMidPinStillFreesTheOpenedHandlesWhenAnEarlierDisposeThrows()
    {
        GameSettings g = Game("a");
        var rest = new FakePin([]);
        int opens = 0;
        var env = new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = _ => _running,
            ZoneBytes = _ => _zoneMib << 20,
            TraceExists = _ => true,
            PhysicalRamBytes = () => _ram,
            PinPaths = _ => [@"D:\Games.bin", @"D:\Games.bin", @"D:\Games\c.bin"],
            OpenPin = _ =>
            {
                opens++;
                _now += TimeSpan.FromMilliseconds(50); // 파일 사이 재확인 간격이 지나도록
                if (opens == 2)
                {
                    _running = true; // 두 번째 파일을 연 뒤 게임 시작 → 셋째 파일 전에 멈춤
                }

                return new PinHandle(opens == 1 ? new ThrowingPin() : rest, 1, 0);
            },
        };
        var keeper = new PinKeeper(env, _log, () => _now);

        keeper.TryPin(g);

        Assert.True(rest.Disposed);
        Assert.Empty(keeper.Held);
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

    // ---- 회복 기간 고정 ----

    private const string AfterRelocation = "auto after relocation";

    private static GameSettings Unset(string name) => Game(name) with { Prewarm = PrewarmSettings.Default }; // pin 키 없음

    [Fact]
    public void RecoveryPinSurvivesALauncherAndSmallRamUntilTheDriveRecovers()
    {
        _ram = 16 * Gib; // 평소 규칙이면 pin 을 적지 않은 게임은 24 GiB 부터라 고정 안 됨
        _launcherRunning = true;
        GameSettings g = Unset("a");
        Prewarmer p = Make(g);

        Assert.True(p.Run(g, AfterRelocation).Ran);
        Assert.Equal(["a"], p.Pinned);
        Assert.Contains(_log.Lines, l => l.Contains("a: pinned until drive recovers (2 file(s))", StringComparison.Ordinal));

        p.ReleasePins("launcher steam.exe started"); // 런처 시작은 회복 기간 고정을 풀지 않음
        p.Tick(Auto(0));
        Assert.Equal(["a"], p.Pinned);
        Assert.False(_pins[0].Disposed);

        _recovery.Recovering = false;
        p.Tick(Auto(0));

        Assert.Empty(p.Pinned);
        Assert.True(_pins[0].Disposed);
        Assert.Contains(_log.Lines, l => l.Contains("a: pin released (drive recovered)", StringComparison.Ordinal));
    }

    [Fact]
    public void RecoveryPinExpiresAfterSixtyMinutes()
    {
        GameSettings g = Unset("a");
        Prewarmer p = Make(g);
        p.Run(g, AfterRelocation);

        _now += TimeSpan.FromMinutes(59);
        p.Tick(Auto(0));
        Assert.Equal(["a"], p.Pinned);

        _now += TimeSpan.FromMinutes(1);
        p.Tick(Auto(0));

        Assert.Empty(p.Pinned);
        Assert.Contains(_log.Lines, l => l.Contains("a: pin released (recovery pin expired)", StringComparison.Ordinal));
    }

    [Fact]
    public void RecoveryPinIsNotTakenWhenAvailableRamIsBelowTheReserve()
    {
        GameSettings g = Unset("a");
        Prewarmer p = Make(g);
        _availSeq = new Queue<long>([16000, 16000, 4095]); // 진입·최종 예산 판단 뒤 고정 직전에 여유 아래로

        Assert.True(p.Run(g, AfterRelocation).Ran);

        Assert.Empty(p.Pinned);
        Assert.Empty(_pins);
    }

    [Fact]
    public void RecoveryPinIsReleasedAtOnceWhenAPinnedFileChanges()
    {
        GameSettings g = Unset("a");
        Prewarmer p = Make(g);
        p.Run(g, AfterRelocation);

        p.Tick(Auto(0));
        Assert.Equal(["a"], p.Pinned);

        _sizes[@"D:\Games\a\b.bin"] = 7; // 런처 업데이트가 파일을 고침
        p.Tick(Auto(0));

        Assert.Empty(p.Pinned);
        Assert.Contains(_log.Lines, l => l.Contains("a: pin released (file changed: b.bin)", StringComparison.Ordinal));
    }

    [Fact]
    public void RecoveryPinIsNotTakenForExplicitPinFalseOrOtherReasonsOrWithoutARecoveryCheck()
    {
        _ram = 16 * Gib;
        GameSettings off = Game("a", pin: false);
        GameSettings unset = Unset("b");
        GameSettings noCheck = Unset("c");
        Prewarmer p = Make(off, unset, noCheck);

        p.Run(off, AfterRelocation);
        p.Run(unset, "service start");
        p.Recovery = null; // 배선 전이면 기능이 꺼진 것과 같음
        p.Run(noCheck, AfterRelocation);

        Assert.Empty(p.Pinned);
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
    public void RewarmCheckLogsOnlyWhenTheResultChanges()
    {
        Prewarmer p = WarmedGame();
        TickAt(p, TimeSpan.Zero);
        _probe = 0.5;

        TickAt(p, TimeSpan.FromMinutes(10));
        TickAt(p, TimeSpan.FromMinutes(10)); // 같은 결과 (warm) 는 다시 남기지 않음

        Assert.Equal(1, _log.Lines.Count(l => l.Contains("a: rewarm check: warm", StringComparison.Ordinal)));
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
    public void RewarmIsSkippedWhenTheSameGameWasJustWarmed()
    {
        Prewarmer p = WarmedGame();
        GameSettings g = Game("a", pin: false);
        TickAt(p, TimeSpan.Zero);
        _probe = 5.0;

        _now += TimeSpan.FromMinutes(9.5);
        Assert.True(p.Run(g, "auto new trace", ignoreCooldown: true).Ran);
        int streams = _streamer.Calls.Count;
        _now += TimeSpan.FromSeconds(30);
        TickAt(p, TimeSpan.Zero);

        Assert.Equal(streams, _streamer.Calls.Count);
        Assert.Equal(0, _probeCalls);
    }

    [Fact]
    public async Task RewarmCheckSkipsWhileTheSameGameIsBeingPrewarmed()
    {
        GameSettings g = Game("a", pin: false);
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        using var tickStarted = new ManualResetEventSlim();
        int calls = 0;
        var streamer = new BlockingSecondStreamer(started, release, () => Interlocked.Increment(ref calls));
        Prewarmer p = new([g], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = _ => _running,
            ZoneBytes = _ => _zoneMib << 20,
            TraceExists = _ => true,
            ProbeReadMs = _ =>
            {
                _probeCalls++;
                return new ProbeResult(8, 8, 5.0);
            },
            CpuPercent = () =>
            {
                tickStarted.Set();
                return 0;
            },
        }, _log, null, () => _now);
        Assert.True(p.Run(g, "service start").Ran);
        p.Tick(Auto()); // 첫 재확인을 예약
        tickStarted.Reset();
        _probeCalls = 0;
        _probe = 5.0;

        _now += TimeSpan.FromMinutes(9);
        Task<PrewarmResult> warm = Task.Run(() => p.Run(g, "auto new trace", ignoreCooldown: true));
        Task? tick = null;
        bool entered = false;
        bool returnedWhileWarmWasRunning = false;
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(30)));
            _now += TimeSpan.FromMinutes(1);
            tick = Task.Factory.StartNew(() => p.Tick(Auto()), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            entered = tickStarted.Wait(TimeSpan.FromSeconds(30));
            if (entered)
            {
                try
                {
                    await tick.WaitAsync(TimeSpan.FromSeconds(30));
                    returnedWhileWarmWasRunning = true;
                }
                catch (TimeoutException)
                {
                }
            }
        }
        finally
        {
            release.Set(); // 차단 스트리머는 이 신호가 있어야만 풀림: 실패해도 작업을 회수
            _ = await warm.WaitAsync(TimeSpan.FromSeconds(30));
            if (tick is not null)
            {
                await tick.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }

        Assert.True(entered);
        Assert.True(returnedWhileWarmWasRunning);
        Assert.Equal(0, _probeCalls);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void RewarmWaitsForTheSecondRegisteredExeToExit()
    {
        GameSettings g = Game("a", pin: false) with { OriginRoot = @"D:\Games\a", ExeNames = ["a.exe", "body.exe"] }; // 런처 + 학습으로 더한 본체 이름, 런처는 이미 끝남
        bool helperRunning = false;
        IReadOnlyList<(int Pid, string Exe)> processes = [(1234, "body.exe")];
        int probes = 0;
        Prewarmer p = new([g], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = game => helperRunning && DispatchEnv.IsGameRunning(game, processes),
            ZoneBytes = _ => _zoneMib << 20,
            TraceExists = _ => true,
            ProbeReadMs = _ =>
            {
                probes++;
                return new ProbeResult(8, 8, 5.0);
            },
        }, _log, null, () => _now);
        Assert.True(p.Run(g, "service start").Ran);
        p.Tick(Auto());
        helperRunning = true;
        _now += TimeSpan.FromMinutes(10);

        p.Tick(Auto());

        Assert.Single(_streamer.Calls);
        Assert.Equal(0, probes);
        Assert.Contains(_log.Lines, l => l.Contains("rewarm check skipped: a game is running", StringComparison.Ordinal));

        helperRunning = false;
        _now += TimeSpan.FromMinutes(10);
        p.Tick(Auto());

        Assert.Equal(2, _streamer.Calls.Count);
        Assert.Equal(1, probes);
    }

    [Fact]
    public void RewarmSkipNamesTheBlockingProcessAndDoesNotRepeatItEveryCheck()
    {
        GameSettings g = Game("a", pin: false);
        bool running = false;
        Prewarmer p = new([g], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = _ => running,
            DescribeBlockers = _ => @"helper.exe(1234) D:/Games/a/bin/helper.exe",
            ZoneBytes = _ => _zoneMib << 20,
            TraceExists = _ => true,
            ProbeReadMs = _ => new ProbeResult(8, 8, 5.0),
        }, _log, null, () => _now);
        Assert.True(p.Run(g, "service start").Ran);
        p.Tick(Auto());
        running = true;

        for (int i = 0; i < 3; i++)
        {
            _now += TimeSpan.FromMinutes(10);
            p.Tick(Auto());
        }

        Assert.Single(_log.Lines, l => l.Contains("rewarm check skipped: a game is running (blocked by helper.exe(1234) ", StringComparison.Ordinal));
    }

    private sealed class BlockingSecondStreamer(ManualResetEventSlim started, ManualResetEventSlim release, Action count) : IGameStreamer
    {
        private int _calls;

        public StreamStats Stream(GameSettings game)
        {
            count();
            if (Interlocked.Increment(ref _calls) == 2)
            {
                started.Set();
                release.Wait();
            }

            return new StreamStats(100, 2, 0, 0.01);
        }
    }

    [Fact]
    public void ThresholdIsExclusive()
    {
        Prewarmer p = WarmedGame();
        TickAt(p, TimeSpan.Zero);
        _probe = 0.8; // ColdThresholdMs 와 같으면 아직 따뜻함

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

    private void ExitAndTick(Prewarmer p, TimeSpan exitAfter, TimeSpan tickAfter)
    {
        _now += exitAfter;
        p.NoteGameExit();
        TickAt(p, tickAfter);
    }

    [Fact]
    public void GameExitPullsTheCheckInToAfterExitMinutesAndColdRestreams()
    {
        Prewarmer p = WarmedGame();
        p.SetAuto(Auto());
        TickAt(p, TimeSpan.Zero); // 다음 점검은 10 분 뒤
        _probe = 5.0;
        _now += TimeSpan.FromMinutes(1);
        p.NoteGameExit(); // 1 분 지점에 종료 → 3 분 지점

        TickAt(p, TimeSpan.FromSeconds(119));
        Assert.Equal(0, _probeCalls);

        TickAt(p, TimeSpan.FromSeconds(1));
        Assert.Equal(1, _probeCalls);
        Assert.Equal(2, _streamer.Calls.Count);
    }

    [Fact]
    public void GameExitDoesNotPostponeAnEarlierCheck()
    {
        Prewarmer p = WarmedGame();
        p.SetAuto(Auto());
        TickAt(p, TimeSpan.Zero); // 다음 점검은 10 분 지점
        TickAt(p, TimeSpan.FromMinutes(8.5));
        p.NoteGameExit(); // 8.5 + 2 = 10.5 분은 더 늦으므로 무시

        TickAt(p, TimeSpan.FromMinutes(1.5));

        Assert.Equal(1, _probeCalls);
    }

    [Fact]
    public void GameExitCheckIsHeldWhileAnotherGameRunsOnTheDrive()
    {
        Prewarmer p = WarmedGame();
        p.SetAuto(Auto());
        TickAt(p, TimeSpan.Zero);
        _running = true;

        ExitAndTick(p, TimeSpan.Zero, TimeSpan.FromMinutes(2));

        Assert.Equal(0, _probeCalls);
        Assert.Contains(_log.Lines, l => l.Contains("rewarm check skipped: a game is running", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0, 10)] // 종료 뒤 앞당김을 끔
    [InlineData(2, 0)] // 재예열 자체를 끔
    public void GameExitDoesNothingWhenItsKeyOrRewarmIsOff(int afterExitMin, int rewarmMin)
    {
        Prewarmer p = WarmedGame();
        p.SetAuto(AutoSettings.Default with { RewarmMinutes = rewarmMin, RewarmAfterExitMin = afterExitMin });
        TickAt(p, TimeSpan.Zero, rewarmMin);

        ExitAndTick(p, TimeSpan.Zero, TimeSpan.FromMinutes(2));

        Assert.Equal(0, _probeCalls);
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
        _avail = 8000; // 가용 − 여유 = 3904 → 존 1610 은 둘까지 (3220), 셋째는 남은 684 MiB 만 (부분 예열), 넷째는 남은 예산 없음
        _streamer.BytesOf = g => (g.MaxStreamSet ? g.MaxStreamMib : _zoneMib) << 20; // 예산 차감은 실제 읽은 양

        IReadOnlyList<PrewarmResult> results = p.RunAll(games, "recent");

        Assert.Equal(["a", "b", "c"], _streamer.Calls.Select(c => c.Game));
        Assert.Equal([true, true, true, false], results.Select(r => r.Ran));
        Assert.Equal((684L, 1610L), p.PartialInfo("c")); // 실제로 읽은 양만 예산에서 뺌
        Assert.Equal("batch memory budget", results[3].SkipReason);
        Assert.Contains(_log.Lines, l => l.Contains("d: prewarm skipped (batch zones", StringComparison.Ordinal));
    }

    [Fact]
    public void RepeatedBatchSkipOfTheSameGameIsLoggedOncePerMinuteWithTheSuppressedCount()
    {
        GameSettings[] games = [Game("a", false), Game("b", false)];
        Prewarmer p = Make(games);
        _ram = Gib / 4; // 1/2 = 128 MiB: 부분 예열 최소 (256 MiB) 에도 못 미쳐 처음부터 자름

        for (int i = 0; i < 9; i++)
        {
            p.RunAll(games, "trigger Riot Client.exe");
            _now += TimeSpan.FromSeconds(4);
        }

        Assert.Single(_log.Lines, l => l.StartsWith("Info: a: prewarm skipped (batch zones", StringComparison.Ordinal));

        _now += Prewarmer.SkipLogEvery; // 60 초 뒤 다시 남기고 억제된 횟수를 붙임
        p.RunAll(games, "trigger Riot Client.exe");
        string[] lines = [.. _log.Lines.Where(l => l.StartsWith("Info: a: prewarm skipped (batch zones", StringComparison.Ordinal))];
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("(suppressed 8)", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void BatchBudgetIsReducedByWhatWasActuallyReadNotByTheConfiguredZone()
    {
        GameSettings[] games = [Game("a", false), Game("b", false), Game("c", false), Game("d", false)];
        Prewarmer p = Make(games);
        _avail = 8000; // 가용 − 여유 = 3904: 존 1610 으로 치면 셋째부터 모자라지만
        _streamer.BytesOf = _ => 500L << 20; // 실제로는 게임마다 500 MiB 만 읽음 (겹치는 구간이 이미 캐시에 있었던 경우)

        IReadOnlyList<PrewarmResult> results = p.RunAll(games, "recent");

        Assert.Equal([true, true, true, true], results.Select(r => r.Ran)); // 500 x 4 = 2000 이 모두 들어감
        Assert.All(games, g => Assert.Null(p.PartialInfo(g.Name)));
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
    public void BatchBudgetIsAtMostHalfOfPhysicalRamEvenWhenMoreIsAvailable()
    {
        GameSettings[] games = [Game("a", false), Game("b", false), Game("c", false)];
        Prewarmer p = Make(games);
        _ram = 4 * Gib; // 1/2 = 2048 MiB: 존 1610 은 하나, 둘째는 남은 438 MiB 만 (부분 예열), 셋째는 남은 예산 없음
        _streamer.BytesOf = g => (g.MaxStreamSet ? g.MaxStreamMib : _zoneMib) << 20;
        _avail = 40000;

        IReadOnlyList<PrewarmResult> results = p.RunAll(games, "recent");

        Assert.Equal(["a", "b"], _streamer.Calls.Select(c => c.Game));
        Assert.Equal([true, true, false], results.Select(r => r.Ran));
        Assert.Equal((438L, 1610L), p.PartialInfo("b"));
        Assert.Equal("batch memory budget", results[2].SkipReason);
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

    [Fact]
    public void RewarmCheckStopsWhenARelocationStartsBetweenGamesAndTheRestIsCheckedNextPeriod()
    {
        GameSettings a = Game("a", pin: false);
        GameSettings b = Game("b", pin: false);
        Prewarmer p = Make(a, b);
        p.Run(a, "service start");
        p.Run(b, "service start");
        int before = _streamer.Calls.Count;
        _probe = 5.0; // 식음
        p.Relocating = _ => _streamer.Calls.Count > before; // 첫 게임을 다시 예열한 직후 재배치가 시작됨

        p.Tick(Auto(10));
        _now += TimeSpan.FromMinutes(10);
        p.Tick(Auto(10));

        Assert.Equal(before + 1, _streamer.Calls.Count); // 두 번째 게임은 읽지 않음
        Assert.Single(_log.Lines, l => l.Contains("rewarm check stopped: a relocation started", StringComparison.Ordinal));

        p.Relocating = _ => false; // 재배치가 끝남
        _now += TimeSpan.FromMinutes(10);
        p.Tick(Auto(10));

        Assert.Contains("b", _streamer.Calls.Skip(before + 1).Select(c => c.Game)); // 다음 주기에 이어서 점검·재예열
    }
}
