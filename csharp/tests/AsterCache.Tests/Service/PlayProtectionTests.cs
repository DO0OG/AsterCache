using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tests.Core;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임이 도는 동안 유지·점검 경로 (회복 표본, keep-awake, 배치 점검, 고정, 부팅 진단) 가 같은 물리 디스크를 읽지 않는지. 바깥 세계는 전부 가짜.
public sealed class PlayProtectionTests
{
    private const string Drive = @"E:\";
    private static readonly DriveBaseline Both = new(new ProbeSpot(10, 100), new ProbeSpot(20, 80));
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 20, 0, 0, TimeSpan.FromHours(9));

    // ---- 회복 표본 ----

    private readonly FakeWorld _w = new();
    private readonly ListLog _log = new();
    private readonly List<long> _probed = [];
    private bool _game;
    private bool _connected = true;

    private (DriveRecovery Recovery, AutoStatus Status) OpenRecovery(string statusPath)
    {
        _w.ProbeMbps = (_, lcn, _) =>
        {
            _probed.Add(lcn);
            return lcn == 10 ? 10 : 8; // 기준의 10%: 회복되지 않음
        };
        AutoEnv env = _w.Env();
        if (_connected)
        {
            env.GameRunningOnDisk = _ => _game;
        }

        var status = new AutoStatus(statusPath, _log, () => _w.Now);
        return (new DriveRecovery(env, _log, status), status);
    }

    private DriveRecovery NewRecovery() => OpenRecovery(Path.Combine(Path.GetTempPath(), "aster_pp_" + Guid.NewGuid().ToString("N") + ".json")).Recovery;

    private void Pass(DriveRecovery r, int minutes)
    {
        _w.Now += TimeSpan.FromMinutes(minutes);
        r.Poll();
    }

    [Fact]
    public void NoRecoverySampleIsReadWhileAGameRunsOnTheDisk()
    {
        DriveRecovery r = NewRecovery();
        r.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        _game = true;

        Pass(r, 2);
        Pass(r, 2);
        Pass(r, 10);

        Assert.Empty(_probed);
    }

    [Fact]
    public void AnUnconnectedGameCheckCountsAsProtected()
    {
        _connected = false;
        DriveRecovery r = NewRecovery();
        r.Begin(Drive, Both, TimeSpan.FromMinutes(60));

        Pass(r, 2);
        Pass(r, 10);

        Assert.Empty(_probed);
    }

    [Fact]
    public void PlayTimeDoesNotUseUpTheLimitAndSamplingResumesAfterTheGame()
    {
        DriveRecovery r = NewRecovery();
        r.Begin(Drive, Both, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(180));
        Pass(r, 10);
        _probed.Clear();

        _game = true;
        r.Poll(); // 멈춤
        Pass(r, 300); // 5 시간 플레이
        Pass(r, 60);
        Assert.True(r.IsRecovering(Drive));
        Assert.True(r.IsRelocationHeld(Drive));
        Assert.Empty(_probed);

        _game = false;
        r.Poll(); // 이어감 (잔여 시간: 소프트 50 분, 하드 170 분)
        Pass(r, 2);
        Assert.NotEmpty(_probed); // 종료 뒤 표본이 다시 돎

        Pass(r, 46);
        Assert.True(r.IsRecovering(Drive));
        Pass(r, 2);
        Assert.False(r.IsRecovering(Drive)); // 플레이 시간이 아니라 실제로 지난 시간으로 소프트 상한
        Assert.True(r.IsRelocationHeld(Drive));
        Pass(r, 119);
        Assert.True(r.IsRelocationHeld(Drive));
        Pass(r, 2);
        Assert.False(r.IsRelocationHeld(Drive)); // 하드 상한도 무한 연장되지 않음
    }

    [Fact]
    public void GameStartNotificationFreezesTheClockBeforeThePollSeesTheProcess()
    {
        DriveRecovery r = NewRecovery();
        r.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        _game = true;

        r.YieldTo(Drive);
        _w.Now += TimeSpan.FromHours(3);

        Assert.True(r.IsRecovering(Drive)); // Poll 전에도 플레이 시간이 깎이지 않음
    }

    [Fact]
    public void ADeferredRoundDoesNotSpendTheRotationOrTheNextSampleTime()
    {
        DriveRecovery r = NewRecovery();
        r.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        _w.Now += TimeSpan.FromMinutes(2);
        _game = true;
        r.Poll();
        _game = false;
        _probed.Clear();

        r.Poll(); // 게임이 끝난 틱: 표본 차례가 그대로 남아 바로 잼

        Assert.Equal([10L, 20L], _probed);
    }

    [Fact]
    public void PausedTrackRestoresWithTheFrozenRemainingTimeAndExpiredSoftWithHardRemainingSurvivesRestarts()
    {
        string path = Path.Combine(Path.GetTempPath(), "aster_pp_" + Guid.NewGuid().ToString("N") + ".json");
        (DriveRecovery first, _) = OpenRecovery(path);
        first.Begin(Drive, Both, TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(180));
        Pass(first, 20);
        _game = true;
        first.Poll();
        _w.Now += TimeSpan.FromHours(5); // 멈춘 채 5 시간 지난 뒤 재시작

        (DriveRecovery second, AutoStatus status) = OpenRecovery(path);
        Assert.True(second.IsRecovering(Drive));
        DriveRecoveryRecord saved = Assert.Single(status.DriveRecoveries());
        Assert.NotNull(saved.PausedSince); // 첫 Poll 의 실행 판정까지 멈춘 채로 저장
        Assert.Equal(TimeSpan.FromMinutes(40), saved.Until - saved.PausedSince!.Value); // 소프트 잔여 40 분 (재시작 중 플레이 시간이 다시 깎이지 않음)
        Assert.Equal(TimeSpan.FromMinutes(160), saved.HardUntil!.Value - saved.PausedSince.Value);

        _game = false;
        second.Poll();
        Pass(second, 45); // 소프트 상한은 지났고 하드 잔여 115 분
        Assert.False(second.IsRecovering(Drive));
        (DriveRecovery third, AutoStatus again) = OpenRecovery(path); // 소프트 잔여 0 으로 저장된 기록을 다시 재시작해도
        Assert.True(third.IsRelocationHeld(Drive));
        Assert.Single(again.DriveRecoveries());
        (DriveRecovery fourth, _) = OpenRecovery(path);
        Assert.True(fourth.IsRelocationHeld(Drive));
    }

    // ---- keep-awake ----

    [Fact]
    public void KeepAwakeSkipsTheReadWhenAGameStartsWhileTheFileIsBeingPicked()
    {
        var reads = new List<string>();
        int gameChecks = 0;
        GameSettings g = Games.Make("g", "g.exe") with { Prewarm = PrewarmSettings.Default with { TriggerExes = ["steam.exe"] } };
        var env = new KeepAwakeEnv
        {
            Now = () => T0,
            Elapsed = () => TimeSpan.Zero,
            IsHdd = _ => true,
            AnyRunning = exes => exes.Contains("steam.exe") || (exes.Contains("g.exe") && ++gameChecks > 1), // 첫 판정 (탐색 전) 은 통과, 읽기 직전 판정에서 게임이 떠 있음
            PowerOk = () => true,
            Plays = () => new Dictionary<string, IReadOnlyList<DateTimeOffset>>(),
            PickFile = x => $@"D:\Games\{x.Name}\a.bin",
            ReadSector = reads.Add,
            DiskIdleSeconds = () => 600,
        };
        var keepAwake = new KeepAwake(env, _log, () => new AsterSettings(50, [g]) { Auto = AutoSettings.Default });

        keepAwake.Tick();

        Assert.Empty(reads);
        Assert.True(gameChecks >= 2);
    }

    // ---- 배치 점검 ----

    private static readonly GameSettings Bench = new("bench", ["bench.exe"], @"C:\p\t.json", @"C:\p\plan.json", @"D:\AsterBench", [@"D:\AsterBench"], "BENCH", 60);

    private sealed class CheckRig
    {
        public CheckRig() => Plan = PlanFile.Load(PlanFixtures.PathOf("e3/plan.json"));

        public PlanFile Plan { get; }

        public bool Running { get; set; }

        public int Loads { get; private set; }

        public int Stats { get; private set; }

        public int Extents { get; private set; }

        public int RealPaths { get; private set; }

        public Action? OnStat { get; set; }

        public Func<GameSettings, bool>? DiskRunning { get; set; }

        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public ReapplyVerdict Check(CancellationToken ct = default)
        {
            Dictionary<string, Fingerprint> fps = Plan.Fingerprints.ToDictionary(f => f.Path, StringComparer.Ordinal);
            ILookup<string, PlanTask> byPath = Plan.Tasks.ToLookup(t => t.Path, StringComparer.Ordinal);
            var env = new ReapplyEnv
            {
                CanApply = true,
                GameRunning = _ => Running,
                Now = () => Now,
                DiskBusy = _ => false,
                LoadPlan = _ =>
                {
                    Loads++;
                    return Plan;
                },
                Extents = p =>
                {
                    Extents++;
                    return byPath[p].OrderBy(t => t.Vcn).Select(t => new Extent(t.Vcn, t.TargetLcn, t.Clusters)).ToList();
                },
                Stat = p =>
                {
                    Stats++;
                    OnStat?.Invoke();
                    return fps[p];
                },
                RealPath = p =>
                {
                    RealPaths++;
                    return Path.GetFullPath(p);
                },
                Apply = (_, _, _, _) => throw new InvalidOperationException(),
            };
            return new ReapplyChecker(env, new ListLog()).Check(Bench, ct, DiskRunning);
        }
    }

    [Fact]
    public void LayoutCheckTouchesNothingWhileAGameRuns()
    {
        var rig = new CheckRig { Running = true };

        Assert.Equal(ReapplyVerdict.GameRunning, rig.Check());

        Assert.Equal((0, 0, 0, 0), (rig.Loads, rig.Stats, rig.Extents, rig.RealPaths));
    }

    [Fact]
    public void LayoutCheckTouchesNothingWhenTheGateAlreadyCancelled()
    {
        var rig = new CheckRig();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal(ReapplyVerdict.GameRunning, rig.Check(cts.Token));

        Assert.Equal((0, 0, 0, 0), (rig.Loads, rig.Stats, rig.Extents, rig.RealPaths));
    }

    [Fact]
    public void LayoutCheckStopsBetweenFilesWhenTheGateCancelsMidway()
    {
        var rig = new CheckRig();
        using var cts = new CancellationTokenSource();
        rig.OnStat = cts.Cancel; // 첫 지문 조회 중에 게임 시작 알림

        Assert.Equal(ReapplyVerdict.GameRunning, rig.Check(cts.Token));

        Assert.Equal(1, rig.Stats);
        Assert.Equal(0, rig.Extents);
    }

    [Fact]
    public void LayoutCheckStopsBetweenFilesWhenOnlyTheRunningStateChangesAfterTheGuardInterval()
    {
        var rig = new CheckRig();
        rig.OnStat = () =>
        {
            rig.Running = true; // 취소 토큰은 그대로, 시작 알림을 놓친 게임이 첫 지문 조회 중에 뜸
            rig.Now += ReapplyChecker.GuardInterval;
        };

        Assert.Equal(ReapplyVerdict.GameRunning, rig.Check());

        Assert.Equal(1, rig.Stats);
        Assert.Equal(0, rig.Extents);
    }

    [Fact]
    public void LayoutCheckStopsBetweenFilesWhenAnotherGameOnTheSameDiskStarts()
    {
        var rig = new CheckRig();
        bool other = false;
        rig.DiskRunning = _ => other;
        rig.OnStat = () =>
        {
            other = true; // 자기 게임은 그대로, 같은 디스크의 다른 게임이 첫 지문 조회 중에 뜸
            rig.Now += ReapplyChecker.GuardInterval;
        };

        Assert.Equal(ReapplyVerdict.GameRunning, rig.Check());

        Assert.Equal(1, rig.Stats);
        Assert.Equal(0, rig.Extents);
    }

    [Fact]
    public void LayoutCheckStopsWhenTheSameDiskJudgementFails()
    {
        var rig = new CheckRig();
        rig.DiskRunning = _ => throw new InvalidOperationException(); // 판정 실패는 보호 중

        Assert.Equal(ReapplyVerdict.GameRunning, rig.Check());

        Assert.Equal((0, 0, 0, 0), (rig.Loads, rig.Stats, rig.Extents, rig.RealPaths));
    }

    [Fact]
    public void GateCancelsTheWorkWhenTheTimerJudgementFails()
    {
        var gate = new ReapplyGate { RunningPoll = TimeSpan.FromMilliseconds(20) };
        int polls = 0;

        bool cancelled = gate.Run(CancellationToken.None, token =>
        {
            _ = token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5));
            return token.IsCancellationRequested;
        }, () => ++polls == 1 ? false : throw new InvalidOperationException()); // 시작 전에는 통과, 타이머 조회는 실패

        Assert.True(cancelled);
    }

    [Fact]
    public void LayoutCheckStillRunsWhenNoGameIsRunning()
    {
        var rig = new CheckRig();

        Assert.Equal(ReapplyVerdict.Healthy, rig.Check());

        Assert.True(rig.Extents > 0);
    }

    [Fact]
    public void GateDoesNotCallTheWorkWhenAGameIsAlreadyRunningAndHasAFallback()
    {
        var gate = new ReapplyGate();
        int calls = 0;

        int result = gate.Run(CancellationToken.None, _ =>
        {
            calls++;
            return 1;
        }, () => true, () => 7);

        Assert.Equal((0, 7), (calls, result));
    }

    [Fact]
    public void GateStartsTheWorkCancelledWhenAGameIsAlreadyRunning()
    {
        var gate = new ReapplyGate();

        Assert.True(gate.Run(CancellationToken.None, token => token.IsCancellationRequested, () => true));
        Assert.True(gate.Run(CancellationToken.None, token => token.IsCancellationRequested, () => throw new InvalidOperationException())); // 판정 실패도 실행 중으로 봄
        Assert.False(gate.Run(CancellationToken.None, token => token.IsCancellationRequested, () => false));
    }

    // ---- 고정 ----

    private int _pathCalls;
    private int _opens;
    private int _stamps;
    private readonly HashSet<string> _runningGames = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TrackedPin> _handles = [];
    private Action? _onPaths;
    private Action? _onOpen;

    private sealed class TrackedPin : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private PinKeeper NewPins() => new(new PrewarmEnv
    {
        AvailableRamMib = () => 16000,
        GameRunning = g => _runningGames.Contains(g.Name),
        ZoneBytes = _ => 1L << 30,
        TraceExists = _ => true,
        PhysicalRamBytes = () => 32L << 30,
        FileStamp = p =>
        {
            _stamps++;
            return new Fingerprint(p, 1, 0);
        },
        PinPaths = g =>
        {
            _pathCalls++;
            _onPaths?.Invoke();
            return [$@"D:\Games\{g.Name}\a.bin"];
        },
        OpenPin = paths =>
        {
            _opens++;
            var pin = new TrackedPin();
            _handles.Add(pin);
            _onOpen?.Invoke();
            return new PinHandle(pin, paths.Count, 0);
        },
    }, _log, () => T0);

    private static GameSettings PinGame(string name) =>
        Games.Make(name, name + ".exe") with { Prewarm = PrewarmSettings.Default with { Pin = true, PinSet = true } };

    [Fact]
    public void PinIsDeferredWhileAnotherGameRunsOnTheSameDisk()
    {
        PinKeeper pins = NewPins();
        GameSettings a = PinGame("a");
        GameSettings b = PinGame("b");
        pins.Reconcile([a, b]);
        _runningGames.Add("b");

        pins.TryPin(a);

        Assert.Equal((0, 0), (_pathCalls, _opens));
        _runningGames.Clear();
        pins.TryPin(a);
        Assert.Equal((1, 1), (_pathCalls, _opens));
    }


    [Fact]
    public void ADeferredPinIsRetriedOnTheNextTickOnceTheGameHasEnded()
    {
        PinKeeper pins = NewPins();
        GameSettings a = PinGame("a");
        GameSettings b = PinGame("b");
        pins.Reconcile([a, b]);
        _runningGames.Add("b");
        pins.TryPin(a);

        pins.CheckMemory([a, b]); // 아직 도는 동안은 다시 시도하지 않음
        Assert.Equal((0, 0), (_pathCalls, _opens));

        _runningGames.Clear();
        pins.CheckMemory([a, b]);

        Assert.Equal((1, 1), (_pathCalls, _opens));
        Assert.Contains("a", pins.Held);
        pins.CheckMemory([a, b]);
        Assert.Equal((1, 1), (_pathCalls, _opens)); // 한 번만
    }
    [Fact]
    public void PinDoesNotOpenWhenAGameOnTheSameDiskStartsWhileThePathsAreResolved()
    {
        PinKeeper pins = NewPins();
        GameSettings a = PinGame("a");
        GameSettings b = PinGame("b");
        pins.Reconcile([a, b]); // 서비스 시작 때 Worker 가 주는 등록 목록 (첫 유지 틱 전)
        _onPaths = () => _runningGames.Add("b");

        pins.TryPin(a);

        Assert.Equal((1, 0), (_pathCalls, _opens));
        Assert.Empty(pins.Held);
        _onPaths = null;
        _runningGames.Clear();
        pins.CheckMemory([a, b]);
        Assert.Equal((2, 1), (_pathCalls, _opens));
        Assert.Contains("a", pins.Held);
    }

    [Fact]
    public void PinIsDroppedAndRetriedWhenAGameOnTheSameDiskStartsWhileOpening()
    {
        PinKeeper pins = NewPins();
        GameSettings a = PinGame("a");
        GameSettings b = PinGame("b");
        pins.Reconcile([a, b]);
        _onOpen = () => _runningGames.Add("b");

        pins.TryPin(a);

        Assert.Equal(1, _opens);
        Assert.True(_handles[0].Disposed); // 직후 판정이 다른 게임을 보고 바로 풂
        Assert.Empty(pins.Held);
        _onOpen = null;
        pins.CheckMemory([a, b]);
        Assert.Equal(1, _opens); // 아직 도는 동안은 다시 시도하지 않음
        _runningGames.Clear();
        pins.CheckMemory([a, b]);
        Assert.Equal(2, _opens);
        Assert.Contains("a", pins.Held);
        Assert.False(_handles[1].Disposed);
    }

    [Fact]
    public void ADeferredPinIsClaimedByOnlyOneOfTwoOverlappingRetries()
    {
        PinKeeper pins = NewPins();
        GameSettings a = PinGame("a");
        GameSettings b = PinGame("b");
        pins.Reconcile([a, b]);
        _runningGames.Add("b");
        pins.TryPin(a);
        _runningGames.Clear();
        bool reentered = false;
        _onPaths = () =>
        {
            if (!reentered)
            {
                reentered = true;
                pins.CheckMemory([a, b]); // 첫 재시도가 경로를 찾는 사이에 다른 점검이 같은 대기 항목을 봄
            }
        };

        pins.Reconcile([a, b]);

        Assert.Equal((1, 1), (_pathCalls, _opens));
        Assert.Single(pins.Held);
    }

    [Fact]
    public void ARacingSecondPinNeverOverwritesTheHeldHandle()
    {
        PinKeeper pins = NewPins();
        GameSettings a = PinGame("a");
        pins.Reconcile([a]);
        bool reentered = false;
        _onOpen = () =>
        {
            if (!reentered)
            {
                reentered = true;
                pins.TryPin(a); // 바깥 호출이 여는 사이에 다른 호출이 먼저 고정을 마침
            }
        };

        pins.TryPin(a);

        Assert.Equal(2, _opens);
        Assert.Single(pins.Held);
        Assert.False(_handles[1].Disposed); // 먼저 끝낸 안쪽 핸들을 유지
        Assert.True(_handles[0].Disposed); // 바깥 핸들은 덮어쓰지 않고 풂
    }

    [Fact]
    public void RecoveryPinCheckSkipsTheFileStatsWhileAGameRuns()
    {
        PinKeeper pins = NewPins();
        pins.Recovery = new AlwaysRecovering();
        GameSettings a = PinGame("a");
        pins.Reconcile([a, PinGame("b")]);
        pins.TryPin(a, afterRelocation: true);
        Assert.Single(pins.Held);
        int before = _stamps;

        _runningGames.Add("b");
        pins.CheckRecovery();
        Assert.Equal(before, _stamps);

        _runningGames.Clear();
        pins.CheckRecovery();
        Assert.True(_stamps > before);
    }

    private sealed class AlwaysRecovering : IDriveRecovery
    {
        public bool IsRecovering(string drive) => true;

        public bool? WatchUpdate(string drive, DateTimeOffset updatedAt, TimeSpan limit) => null;
    }

    // ---- 부팅 진단 ----

    [Fact]
    public void BootProbeRechecksTheGameBeforeEveryOpen()
    {
        using var dir = new TempDir();
        var status = new AutoStatus(dir.Combine("auto_status.json"), _log, () => T0);
        GameSettings g = Games.Make("g", "g.exe");
        status.MarkPlayed("g", "auto", AutoStage.Done, T0);
        var opened = new List<string>();
        int checks = 0;

        AutoBootProbe? probe = BootProbe.Run(new AsterSettings(50, [g]) { Auto = AutoSettings.Default }, status, _log, T0, 1000, x => [@"D:\Games\g\1", @"D:\Games\g\2", @"D:\Games\g\3", @"D:\Games\g\4"], p =>
        {
            opened.Add(p);
            return true;
        }, () => TimeSpan.Zero, _ => ++checks > 2); // 입구 판정과 첫 파일 직전은 통과, 두 번째 파일 직전에 게임이 뜸

        Assert.Equal([@"D:\Games\g\1"], opened);
        Assert.Equal(1, probe?.Files);
    }

    [Fact]
    public void BootProbeOpensNothingWhenAGameIsRunningAtTheStart()
    {
        using var dir = new TempDir();
        var status = new AutoStatus(dir.Combine("auto_status.json"), _log, () => T0);
        status.MarkPlayed("g", "auto", AutoStage.Done, T0);
        int opens = 0;

        AutoBootProbe? probe = BootProbe.Run(new AsterSettings(50, [Games.Make("g", "g.exe")]) { Auto = AutoSettings.Default }, status, _log, T0, 1000, x => [@"D:\Games\g\1"], _ =>
        {
            opens++;
            return true;
        }, () => TimeSpan.Zero, _ => true);

        Assert.Null(probe);
        Assert.Equal(0, opens);
    }
}
