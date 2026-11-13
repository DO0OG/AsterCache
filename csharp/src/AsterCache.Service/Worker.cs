using System.Diagnostics;
using System.Threading.Channels;
using AsterCache.Core.Discovery;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using Microsoft.Extensions.Hosting;

namespace AsterCache.Service;

/// 서비스 본체: 프로세스 시작 감지 → 스트림, 주기 배치 검사 → 재적용, 자동 모드 (발견·트레이스·첫 재배치), 설정 다시 읽기.
/// 임의 경로·명령을 받는 IPC 는 없습니다 (설정 파일과 계획 폴더만 읽음).
public sealed class Worker : BackgroundService
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckTick = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ResumeRetry = TimeSpan.FromMinutes(5);

    private readonly ILog _log;
    private readonly GameDispatcher _dispatcher;
    private readonly ReapplyChecker _checker;
    private readonly ReapplyGate _gate;
    private readonly Prewarmer? _prewarm;
    private readonly AutoMode? _auto;
    private readonly SettingsReloader? _reload;
    private readonly KeepAwake? _keepAwake;
    private readonly RiotPhaseWatcher? _riot;
    private readonly PlayRecorder? _playRecorder;
    private readonly BackgroundYield? _backgroundYield;
    private readonly DiskReadCoordinator _coordinator;
    private readonly object _watchLock = new();
    private readonly object _gameStateLock = new();
    private volatile AsterSettings _settings;
    private (IProcessWatcher Primary, IProcessWatcher? Fallback) _watchers;
    private string _watchKey;
    private CancellationTokenSource? _generation;
    private bool _restart;
    private int _maintaining;
    private readonly GameExitTracker _exits = new();
    private readonly SessionReads _reads = new();
    private readonly BootQuiet _quiet;
    private readonly object _heldLock = new();
    private readonly List<ProcessStart> _heldTriggers = []; // 부팅 조용 구간에 보류한 계기 (게임 exe 가 아닌 트리거), _heldLock 으로 보호
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, string> _reapplyHold = new(StringComparer.Ordinal); // 게임 정책·재배치 시간대로 재적용을 미룬 사유 (바뀔 때만 로그), 재적용 틱 스레드에서만 씀

    public Worker(AsterSettings settings, ILog log, IProcessWatcher primary, IProcessWatcher? fallback, IGameStreamer streamer, ReapplyChecker checker,
        ReapplyGate? gate = null, Prewarmer? prewarm = null, AutoMode? auto = null, SettingsReloader? reload = null, KeepAwake? keepAwake = null, DispatchEnv? dispatchEnv = null,
        RiotPhaseWatcher? riot = null, BootQuiet? quiet = null, DiskReadCoordinator? coordinator = null, PlayRecorder? playRecorder = null, Func<DateTimeOffset>? now = null, BackgroundYield? backgroundYield = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _coordinator = coordinator ?? new DiskReadCoordinator(AsterCache.Windows.VolumeDisks.Shared, log);
        _prewarm = prewarm;
        _prewarm?.SetDiskReadCoordinator(_coordinator);
        _prewarm?.SetAuto(settings.Auto);
        _prewarm?.SetGames(settings.Games); // 고정이 첫 유지 틱 전에도 같은 디스크의 다른 등록 게임을 알도록
        _auto = auto;
        _auto?.SetDiskReadCoordinator(_coordinator);
        _auto?.SetGameExitTracker(_exits);
        _prewarm?.SetGameExitTracker(_exits);
        _reload = reload;
        _playRecorder = playRecorder;
        _backgroundYield = backgroundYield;
        _settings = settings;
        _keepAwake = keepAwake ?? (auto is null ? null : new KeepAwake(KeepAwakeEnv.Create(auto.Status.Plays), log, () => _settings, _coordinator)); // 자동 모드가 있는 진짜 서비스에서만 기본으로 켬
        _keepAwake?.SetDiskReadCoordinator(_coordinator);
        if (prewarm is not null && auto is not null)
        {
            AutoStatus status = auto.Status;
            prewarm.PostUpdateWait = g => AutoRelocator.PostUpdateWait(status, _settings.Auto, g.Name, DateTimeOffset.UtcNow);
            prewarm.ReplanStart = g => AutoRelocator.Replanning(status, g.Name)?.At;
            prewarm.ReplanKept = g => _settings.Auto.Enabled && AutoRelocator.ReplanKept(status, g.Name); // 자동 모드가 꺼지면 재계획이 돌지 않으므로 보류하지 않음 (설정을 다시 읽어도 최신 값)
            prewarm.Relocating = name => status.Get(name)?.Stage == AutoStage.Relocating;
            prewarm.HasPlayed = auto.HasPlayed;
            prewarm.WarmRank = auto.WarmRank;
        }

        _log = log;
        _riot = riot ?? (prewarm is null ? null : new RiotPhaseWatcher(() => _settings, RiotWatchEnv.Create(prewarm), log)); // 설정 riot.champ_select_prewarm 이 꺼져 있으면 아무것도 부르지 않고 기다림
        DiskActivity.Log = log;
        _watchers = (primary, fallback);
        _watchKey = WatchKey(settings);
        LogWatcherOverride(settings);
        DispatchEnv dispatch = dispatchEnv ?? new DispatchEnv();
        _dispatcher = new GameDispatcher(settings.Games, streamer, log, env: dispatch with
        {
            RestreamCooldownMin = () => _settings.Auto.RestreamCooldownMin,
            StartStreamCapSeconds = () => _settings.Auto.StartStreamCapSeconds,
            StartStreamEnabled = () => _settings.Auto.StartStreamEnabled,
            IsWarm = prewarm is null ? dispatch.IsWarm : prewarm.AlreadyWarm,
            RecentWarmProbeAt = prewarm is null ? dispatch.RecentWarmProbeAt : prewarm.RecentWarmProbeAt,
            RecentWarmProbeMinutes = () => _settings.Auto.RewarmMinutes,
        }, coordinator: _coordinator, streamResult: _playRecorder is null ? null : (start, result) => _playRecorder.RecordStartStream(start, result)); // 설정을 다시 읽어도 최신 값
        _checker = checker;
        _gate = gate ?? new ReapplyGate();
        _quiet = quiet ?? BootQuiet.Real(log, () => _settings.Games, () => prewarm?.UptimeMs() ?? Environment.TickCount64); // 부팅 조용 구간 (시험은 주입)
    }

    /// 설정이 etw 이고 관리자면 ETW (실패하면 폴링으로 내려감), 아니면 폴링 (기본).
    public static (IProcessWatcher Primary, IProcessWatcher? Fallback) ChooseWatchers(AsterSettings s, bool admin)
    {
        IEnumerable<string> names = WatchNames(s, SystemMemory.TotalBytes()).Distinct(StringComparer.OrdinalIgnoreCase);
        var polling = new PollingProcessWatcher(names, TimeSpan.FromMilliseconds(s.PollMs));
        return admin && EffectiveWatcher(s) == "etw" ? (new EtwProcessWatcher(names), polling) : (polling, null);
    }

    private static readonly TraceBlockCache Detected = new(dir => AutoEnv.DetectAntiCheat(dir)); // 폴더 탐지는 etw 일 때만, 게임당 한 번 (캐시)

    /// 등록된 게임 중 안티치트 (트레이스 금지) 게임이 하나라도 있으면 설정과 무관하게 poll: ETW 커널 세션을 안티치트 게임 옆에 열지 않음.
    /// 등록값이 없는 게임은 폴더 이름으로 탐지한 결과를 봄 (트레이스 금지 판정과 같은 규칙: 탐지 실패 "unknown" 도 금지). blocker 는 시험이 탐지를 바꿔 끼움.
    public static string EffectiveWatcher(AsterSettings s, Func<GameSettings, string?>? blocker = null) =>
        s.Watcher == "etw" && s.Games.Any(g => AntiCheat.ForbidsTrace(g.AntiCheat) || AntiCheat.ForbidsTrace((blocker ?? Detected.Of)(g))) ? "poll" : s.Watcher;

    private void LogWatcherOverride(AsterSettings s)
    {
        if (s.Watcher != EffectiveWatcher(s))
        {
            _log.Info("process watcher: poll (anti-cheat game registered)");
        }
    }

    // 감시할 이름: 게임 exe 와 트리거, 그리고 고정을 켠 게임이 있으면 런처 전체 (런처가 뜨면 고정을 풀기 위해).
    public static IEnumerable<string> WatchNames(AsterSettings s, long physicalRamBytes = 0) =>
        s.Games.SelectMany(g => g.ExeNames.Concat(g.Prewarm.TriggerExes)).Concat(s.Games.Any(g => PinKeeper.Wanted(g.Prewarm, physicalRamBytes)) ? LauncherExes.All : []);

    // 감시기를 다시 만들어야 하는지 가리는 열쇠: 감시할 이름, 폴링 간격, 감시기 종류.
    private static string WatchKey(AsterSettings s) =>
        $"{EffectiveWatcher(s)}|{s.PollMs}|" + string.Join('|', WatchNames(s, SystemMemory.TotalBytes()).Select(n => n.ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _coordinator.SetServiceToken(stoppingToken);
        _backgroundYield?.Recover(); // 직전 실행이 비정상 종료하며 낮춰 둔 우선순위가 있으면 먼저 되돌림
        if (_settings.Games.Count == 0 && _auto is null && _reload is null)
        {
            _log.Info("service started: no games registered, idle (nothing is watched or reapplied)");
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // 종료
            }

            _log.Info("service stopped");
            return;
        }

        _log.Info(_settings.Games.Count == 0
            ? "service started: no games registered yet, waiting for auto discovery or a config change"
            : $"service started: {_settings.Games.Count} game(s), watcher {_watchers.Primary.GetType().Name}");
        NoteGameExits(); // 감시기 시작 전부터 실행 중인 등록 게임도 예열보다 먼저 디스크 양보에 등록
        DiskKinds.LogAtStart(_settings.Games, _log); // 게임 드라이브별 디스크 종류 (진단용 로그)
        if (_prewarm is not null)
        {
            _ = Task.Run(() => RunPrewarm(() =>
            {
                if (_auto is not null)
                {
                    BootProbe.RunReal(_settings, _auto.Status, _log, _auto.WarmToken, _coordinator); // 서비스 시작 예열이 파일을 데우기 전의 콜드 비용 (부팅 뒤 첫 시작일 때만)
                }

                using var startup = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _auto?.WarmToken ?? default); // 서비스 종료에도 시작 게이트 대기가 끝나게 (자동 모드가 없으면 WarmToken 이 취소되지 않음)
                _prewarm.RunServiceStart(_auto?.StartupRecent(), startup.Token, _auto is { } am ? g => am.HasPlayed(g.Name) : null,
                    _auto is { } am2 && _settings.Auto.Enabled ? name => am2.Status.LastPlayed().TryGetValue(name, out DateTimeOffset at) ? at : null : null);
            }), CancellationToken.None);
        }

        var channel = Channel.CreateUnbounded<ProcessStart>();
        var dispatch = Channel.CreateUnbounded<ProcessStart>();
        Task watch = WatchAsync(channel.Writer, stoppingToken);
        Task consume = Task.Run(() => ConsumeAsync(channel.Reader, dispatch.Writer, stoppingToken), CancellationToken.None);
        Task handle = Task.Run(() => DispatchAsync(dispatch.Reader, stoppingToken), CancellationToken.None);
        Task reapply = Task.Run(() => ReapplyLoopAsync(stoppingToken), CancellationToken.None);
        Task reload = Task.Run(() => ReloadLoopAsync(stoppingToken), CancellationToken.None);
        Task keep = Task.Run(() => KeepAwakeLoopAsync(stoppingToken), CancellationToken.None);
        Task auto = _auto is null ? Task.CompletedTask : Task.Run(() => _auto.RunAsync(stoppingToken), CancellationToken.None);
        Task riotWatch = _riot is null ? Task.CompletedTask : Task.Run(() => _riot.RunAsync(stoppingToken), CancellationToken.None); // 챔피언 선택 선읽기는 재배치 루프·게임 시작 처리와 따로
        try
        {
            await Task.WhenAll(watch, consume, handle, reapply, reload, auto, keep, riotWatch);
        }
        finally
        {
            _playRecorder?.Dispose();
            _auto?.Shutdown();
            _prewarm?.Shutdown();
            _backgroundYield?.Shutdown(); // 예열이 끝난 뒤: 이후 도착한 Begin 은 무시되고 낮춘 우선순위는 모두 되돌림
            _log.Info("service stopped");
        }
    }

    // 감시기는 설정이 바뀌어 감시할 이름이 달라지면 (Apply) 새로 만들어 다시 돌립니다. 처음부터 끝까지 한 번 돌고 끝나는 경우는 예전과 같습니다.
    private async Task WatchAsync(ChannelWriter<ProcessStart> writer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                using var generation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                (IProcessWatcher primary, IProcessWatcher? fallback) watchers;
                bool nothingToWatch;
                lock (_watchLock)
                {
                    (_generation, _restart, watchers, nothingToWatch) = (generation, false, _watchers, _settings.Games.Count == 0);
                }

                if (nothingToWatch)
                {
                    await WaitUntilCancelled(generation.Token); // 등록된 게임이 생기면 설정을 다시 읽으면서 깨움
                }
                else
                {
                    await RunWatchers(watchers.primary, watchers.fallback, writer, generation.Token);
                }

                bool again;
                lock (_watchLock)
                {
                    (again, _generation) = (_restart, null);
                }

                if (ct.IsCancellationRequested || !again)
                {
                    break;
                }
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private async Task RunWatchers(IProcessWatcher primary, IProcessWatcher? fallback, ChannelWriter<ProcessStart> writer, CancellationToken ct)
    {
        try
        {
            _log.Info($"process watcher: {primary.GetType().Name}");
            await primary.RunAsync(OnStart(writer), ct);
            if (!ct.IsCancellationRequested)
            {
                _log.Warn("process watcher ended unexpectedly");
            }
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            _log.Error("process watcher failed", e);
        }

        if (!ct.IsCancellationRequested && fallback is not null)
        {
            _log.Warn($"falling back to {fallback.GetType().Name}");
            _log.Info($"process watcher: {fallback.GetType().Name}");
            await fallback.RunAsync(OnStart(writer), ct);
        }
    }

    private static async Task WaitUntilCancelled(CancellationToken ct)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }
        catch (OperationCanceledException)
        {
            // 종료 또는 설정 변경
        }
    }

    // 게임이 뜨면 돌고 있는 재적용을 먼저 멈춥니다 (읽기 큐 처리보다 앞서, 감시 스레드에서 바로).
    private Action<ProcessStart> OnStart(ChannelWriter<ProcessStart> writer) => s =>
    {
        _riot?.NoteProcessStart(s.ExeName); // 리그 오브 레전드 게임 exe 가 뜨면 챔피언 선택 선읽기를 바로 멈춤
        GameSettings? game;
        lock (_gameStateLock)
        {
            game = _settings.Games.FirstOrDefault(g => g.ExeNames.Contains(s.ExeName, StringComparer.OrdinalIgnoreCase));
        }

        if (game is not null)
        {
            IReadOnlySet<string>? names = CurrentNames();
            lock (_gameStateLock)
            {
                game = _settings.Games.FirstOrDefault(g => string.Equals(g.Name, game.Name, StringComparison.OrdinalIgnoreCase)
                    && g.ExeNames.Contains(s.ExeName, StringComparer.OrdinalIgnoreCase));
                if (game is not null)
                {
                    if (_playRecorder is { } recorder)
                    {
                        TryPlayRecording("start", () => recorder.ObserveStart(game, s, _prewarm?.PlayStartSnapshot(game.Name, DateTimeOffset.UtcNow)));
                    }

                    if (names is not null)
                    {
                        _coordinator.MarkExiting(_exits.GoneNames(_settings.Games, names, game.Name)); // 틱 전에 이미 끝난 다른 게임은 시작 스트림 판단에서 뺌 (종료 판정은 틱에서만)
                    }

                    _coordinator.Yield(game.OriginRoot, "game started", game.Name);
                    _exits.ObserveStarted(game);
                }
            }

            if (game is not null)
            {
                _prewarm?.NoteGameStart(game.Name); // 그 게임을 예열하던 스트림은 아래 취소에도 끝까지 (다른 게임의 예열은 묶음 경계에서 멈춤), 기록 없는 예열의 예산은 풂
                _prewarm?.ReleasePin(game.Name, "game started"); // 고정은 스트림·재적용 취소보다 먼저 풂
                _gate.Cancel(); // 재적용은 게임 exe 시작에서만 멈춥니다 (런처 같은 트리거 전용 프로그램은 아님)
                _prewarm?.Recovery?.YieldTo(game.OriginRoot); // 같은 디스크에서 재는 중인 회복 표본도 멈춤
                _auto?.Tracer.CancelShadow(game.Name); // 돌고 있는 그림자 계획은 게임과 CPU 를 다투지 않게 취소
                _auto?.CancelWarm(); // 자동 모드의 예열 작업도 (핫 집합 스트림은 묶음 경계에서 멈추고, 그 뒤 예열은 시작하지 않음)
                if (_backgroundYield is { } yielder)
                {
                    _ = Task.Run(() => yielder.Begin("game started", settle: false), CancellationToken.None); // 비우기는 감시기 스레드를 막지 않게 따로, 기다리지 않음 (트리거 경로가 이미 했으면 60초 안이라 무시됨)
                }

                if (_auto is not null)
                {
                    _ = Task.Run(() => _auto.OnGameStart(game), CancellationToken.None); // last_played 와 자동 트레이스 시작은 감시기 스레드를 막지 않게 따로
                }
            }
        }

        if (LauncherExes.Is(s.ExeName))
        {
            _prewarm?.ReleasePins($"launcher {s.ExeName} started"); // 런처 업데이트가 파일을 고칠 수 있게
        }

        writer.TryWrite(s with { QueueTimestamp = Stopwatch.GetTimestamp() });
        if (_prewarm?.IsTrigger(s.ExeName) == true && (IsGameExe(s.ExeName) || !HoldTrigger(s)))
        {
            RunTrigger(s);
        }
    };

    // 시작 이벤트 시점의 프로세스 이름만 읽습니다 (프로세스를 열지 않음, 이름 스냅샷 한 번). 실패하면 null 이며 유예 표시는 틱에 맡깁니다.
    private IReadOnlySet<string>? CurrentNames()
    {
        if (_settings.Games.Count < 2)
        {
            return null;
        }

        try
        {
            return new HashSet<string>(ProcessSnapshot.Take().Select(p => p.Exe), StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Error("game start process snapshot failed", e);
            return null;
        }
    }

    private bool IsGameExe(string exe) => _settings.Games.Any(g => g.ExeNames.Contains(exe, StringComparer.OrdinalIgnoreCase));

    // 부팅 조용 구간에는 게임 exe 가 아닌 계기 예열 (런처 등) 을 쌓아 둡니다. 구간이 끝나면 RunHeldTriggers 가 돌립니다. 쌓았으면 true.
    private bool HoldTrigger(ProcessStart s)
    {
        if (!_quiet.Defers($"prewarm {s.ExeName}"))
        {
            return false;
        }

        lock (_heldLock)
        {
            _heldTriggers.Add(s);
        }

        return true;
    }

    private void RunHeldTriggers()
    {
        if (_quiet.Active)
        {
            return;
        }

        ProcessStart[] held;
        lock (_heldLock)
        {
            held = [.. _heldTriggers];
            _heldTriggers.Clear();
        }

        foreach (ProcessStart s in held)
        {
            RunTrigger(s);
        }
    }

    private void RunTrigger(ProcessStart s)
    {
        if (_prewarm is { } prewarm)
        {
            _ = Task.Run(() => RunPrewarm(() => prewarm.OnProcessStart(s, _auto is null ? null : _auto.NarrowTriggered, _auto?.WarmToken ?? default)), CancellationToken.None);
        }
    }

    // 예열은 스트림 한 번이 수 초라 감시기 스레드를 막지 않게 따로 돌립니다. 알 수 없는 예외도 서비스를 죽이지 않음.
    private void RunPrewarm(Action run)
    {
        try
        {
            run();
        }
        catch (Exception e)
        {
            _log.Error("prewarm failed", e);
        }
    }

    private async Task ConsumeAsync(ChannelReader<ProcessStart> reader, ChannelWriter<ProcessStart> dispatch, CancellationToken ct)
    {
        try
        {
            await foreach (ProcessStart s in reader.ReadAllAsync(ct))
            {
                dispatch.TryWrite(s);
            }
        }
        catch (OperationCanceledException)
        {
            // 종료
        }
        finally
        {
            dispatch.TryComplete();
        }
    }

    private async Task DispatchAsync(ChannelReader<ProcessStart> reader, CancellationToken ct)
    {
        try
        {
            await foreach (ProcessStart s in reader.ReadAllAsync(ct))
            {
                _dispatcher.Handle(s, ct); // 순서와 쿨다운 의미를 유지하며 별도 루프에서 처리
            }
        }
        catch (OperationCanceledException)
        {
            // 종료
        }
    }

    private async Task ReloadLoopAsync(CancellationToken ct)
    {
        if (_reload is null)
        {
            return;
        }

        try
        {
            using var timer = new PeriodicTimer(_reload.Interval);
            while (await timer.WaitForNextTickAsync(ct))
            {
                try
                {
                    if (_reload.Poll() is { } s)
                    {
                        Apply(s);
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.Error("settings reload failed", e);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 종료
        }
    }

    // HDD 깨워 두기: 서비스가 뜨고 FirstCheckDelay 뒤부터 틱마다 (KeepAwake 가 10 분 주기를 스스로 지킴). 스핀업을 기다릴 수 있어 재적용 루프와 따로 돌림.
    private async Task KeepAwakeLoopAsync(CancellationToken ct)
    {
        if (_keepAwake is null)
        {
            return;
        }

        try
        {
            await Task.Delay(FirstCheckDelay, ct);
            using var timer = new PeriodicTimer(CheckTick);
            do
            {
                try
                {
                    if (!_quiet.Defers("keep-awake read"))
                    {
                        _keepAwake.Tick();
                    }
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.Error("keep-awake failed", e);
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException)
        {
            // 종료
        }

        _keepAwake.Stop();
    }

    // 새 설정으로 감시기·디스패처·예열 대상·자동 모드를 바꿉니다. 쿨다운 같은 상태는 유지하고, 감시할 이름이 달라졌을 때만 감시기를 다시 만듭니다.
    internal void Apply(AsterSettings s)
    {
        bool relocationRevoked;
        lock (_gameStateLock)
        {
            relocationRevoked = _settings.Games.Any(old => GamePolicy.AllowsRelocate(old) && s.Games.FirstOrDefault(g => string.Equals(g.Name, old.Name, StringComparison.OrdinalIgnoreCase)) is { } now && !GamePolicy.AllowsRelocate(now));
            _settings = s;
        }

        if (relocationRevoked)
        {
            _gate.Cancel(); // 돌고 있는 재배치·재적용은 기존의 안전한 중단 지점 (저널이 이어 갈 수 있는 곳) 에서 멈춤
        }

        if (_playRecorder is { } recorder)
        {
            TryPlayRecording("settings reload", () => recorder.StopGamesExcept(s.Games.Select(game => game.Name), "settings_reloaded"));
        }

        _dispatcher.SetGames(s.Games);
        _prewarm?.SetGames(s.Games);
        _prewarm?.SetAuto(s.Auto);
        _auto?.SetSettings(s);
        _log.Info($"settings reloaded: {s.Games.Count} game(s)");

        string key = WatchKey(s);
        lock (_watchLock)
        {
            if (key == _watchKey)
            {
                return;
            }

            (_watchKey, _watchers, _restart) = (key, _reload!.Watchers(s), true);
            LogWatcherOverride(s);
            _generation?.Cancel();
        }
    }

    private async Task ReapplyLoopAsync(CancellationToken ct)
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        var due = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var resuming = new HashSet<string>(StringComparer.Ordinal); // 일시 보류·취소된 미완료 배치: 짧은 재검사 예약 상태
        try
        {
            while (!ct.IsCancellationRequested)
            {
                ReapplyTick(start, due, resuming, ct);
                await (_auto is null ? Task.Delay(CheckTick, ct) : _auto.WaitForTickAsync(CheckTick, ct)); // 발견이 새 게임을 등록하면 일찍 깨움
            }
        }
        catch (OperationCanceledException)
        {
            // 종료
        }
    }

    /// 재적용 틱 하나: 재적용 루프가 부르고, 시험도 같은 틱을 직접 부릅니다. 부팅 조용 구간이면 배치 검사와 자동 재배치를 보류하고 기한을 그대로 둬서 구간이 끝난 다음 틱에 합니다. 보류한 계기는 구간이 끝나면 돌립니다.
    public void ReapplyTick(DateTimeOffset start, Dictionary<string, DateTimeOffset> due, HashSet<string> resuming, CancellationToken ct)
    {
        _quiet.Refresh();
        RunHeldTriggers();
        GameSettings[] games = [.. _settings.Games];
        HashSet<string> registered = games.Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
        _checker.RetainGames(registered);
        foreach (string name in due.Keys.Where(name => !registered.Contains(name)).ToArray())
        {
            due.Remove(name);
        }

        resuming.RemoveWhere(name => !registered.Contains(name));
        foreach (string name in _reapplyHold.Keys.Where(name => !registered.Contains(name)).ToArray())
        {
            _reapplyHold.Remove(name);
        }

        foreach (GameSettings g in games)
        {
            due.TryAdd(g.Name, start + FirstCheckDelay);
            if (DateTimeOffset.UtcNow < due[g.Name] || _quiet.Defers("layout check and reapply"))
            {
                continue; // 부팅 조용 구간의 검사는 기한을 그대로 둬서 구간이 끝난 다음 틱에 함
            }

            if (ReapplyHeld(g, resuming.Contains(g.Name)))
            {
                continue; // 기한을 그대로 둬서 정책이 풀리거나 시간대에 들어서는 첫 틱에 함
            }

            using DiskReadCoordinator.Lease? lease = _coordinator.TryBegin(g.OriginRoot, "reapply", ct); // 시작 이벤트를 놓쳐도 양보 중인 디스크의 해시 읽기·재적용은 하지 않음 (null 이면 양보 중)
            ReapplyVerdict v = lease is null
                ? ReapplyVerdict.GameRunning
                : _gate.Run(lease.Token, token => _checker.Check(g, token, o => SameDiskRunning(o) || !lease.IsValid), () => SameDiskRunning(g) || !lease.IsValid, () => ReapplyVerdict.GameRunning); // 진입 게이트와 파일 사이 점검이 같은 판정을 씀 (같은 물리 디스크의 등록 게임 전체 + 대여 유효), 실행 중이면 검사 자체를 부르지 않음
            bool wasResuming = resuming.Contains(g.Name);
            bool waiting = v == ReapplyVerdict.Cancelled || _checker.ShouldRetrySoon(g.Name, v, wasResuming);
            _ = waiting ? resuming.Add(g.Name) : resuming.Remove(g.Name);
            due[g.Name] = DateTimeOffset.UtcNow + (waiting ? ResumeRetry : TimeSpan.FromMinutes(g.ReapplyMinutes));
        }

        NoteGameExits();

        if (_auto is not null && RelocationAllowed(_quiet))
        {
            _auto.RelocateTick(_gate, ct);
        }

        MaintainPrewarm();
    }

    // 게임별 정책이 재배치를 막았거나, 재배치 시간대 (auto.relocate_hours) 밖이면 true. 이미 시작한 미완료 배치 (resuming) 는 시간대와 상관없이 이어 끝냅니다.
    private bool ReapplyHeld(GameSettings g, bool resuming)
    {
        string? why = !GamePolicy.AllowsRelocate(g) ? $"game policy: {(g.Paused ? "paused" : GamePolicy.ModeName(g.Mode))}"
            : !resuming && !GamePolicy.AllowsRelocateAt(_settings.Auto, _now().ToLocalTime().Hour) ? "outside auto.relocate_hours" : null;
        if (why is null)
        {
            _ = _reapplyHold.Remove(g.Name);
            return false;
        }

        if (!_reapplyHold.TryGetValue(g.Name, out string? last) || last != why)
        {
            _reapplyHold[g.Name] = why;
            _log.Info($"{g.Name}: layout check and reapply skipped ({why})");
        }

        return true;
    }

    // g 와 같은 물리 디스크의 등록 게임 (g 포함) 이 도는가.
    private bool SameDiskRunning(GameSettings g) => AutoRelocator.SameDriveGames(_settings.Games, [g], AsterCache.Windows.VolumeDisks.SameDisk).Concat([g]).Any(DispatchEnv.IsGameRunning);

    /// 이번 틱에 자동 재배치를 불러도 되는가: 부팅 조용 구간이 아니어야 합니다 (구간이면 보류 로그를 남김).
    public static bool RelocationAllowed(BootQuiet quiet) => !quiet.Defers("auto relocation and new trace prewarm");

    // 폴링·ETW 어느 쪽이든 재적용 틱마다 (30 초) 프로세스 이름을 한 번 읽어 실행 중이던 게임이 사라졌는지 봅니다.
    internal void NoteGameExits()
    {
        try
        {
            IReadOnlyList<(int Pid, string Exe)> procs = _settings.Games.Count == 0 ? [] : ProcessSnapshot.Take();
            if (procs.Count > 0)
            {
                NoteReads(procs);
            }

            HashSet<string> names = new(procs.Select(p => p.Exe), StringComparer.OrdinalIgnoreCase);
            GameSettings[] exited;
            IReadOnlySet<string> grace;
            List<GameSettings> unseen = [];
            lock (_gameStateLock)
            {
                exited = [.. _exits.Poll(_settings.Games, names, unseen)]; // 보이지 않은 짧은 실행도 포함 (양보 해제·자동 모드 알림용)
                Dictionary<string, string> registered = _exits.RunningNames
                    .Where(name => _settings.Games.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
                    .ToDictionary(name => name, name => _settings.Games.First(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)).OriginRoot, StringComparer.OrdinalIgnoreCase);
                _coordinator.ReconcileRunningGames(registered);
                grace = _exits.GraceNames;
                foreach (GameSettings game in _settings.Games.Where(g => registered.ContainsKey(g.Name) && (!grace.Contains(g.Name) || !_coordinator.IsRegistered(g.Name))))
                {
                    _coordinator.Yield(game.OriginRoot, "registered game running", game.Name); // 시작 이벤트 누락·서비스 재시작에도 실행 중에는 새 배경 I/O 를 예약하지 않음
                }

                foreach (GameSettings game in exited)
                {
                    _coordinator.Resume(game.OriginRoot, game.Name);
                }

                _coordinator.MarkExiting(grace); // 종료 유예 중 게임은 양보는 유지하되 다른 게임 시작 스트림 판단에서 뺌
            }

            if (_playRecorder is { } recorder)
            {
                TryPlayRecording("gone", () => recorder.NoteGone(_exits.GoneSince));
            }

            _prewarm?.NoteGamesGone(grace);
            _backgroundYield?.Tick(procs);
            ObserveRunningGames(procs);
            foreach (GameSettings g in exited)
            {
                CompleteDetectedExit(g, unseen.Contains(g));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Error("game exit check failed", e);
        }
    }

    // 게임 종료가 한 곳으로 모이는 자리: 로그와 재예열 앞당김 (종료 시 읽은 양 집계 같은 후속 작업도 여기에 붙임).
    private void OnGameExit(GameSettings game)
    {
        _log.Info($"{game.Name}: exited");
        if (_playRecorder is { } recorder)
        {
            TryPlayRecording("end", () => recorder.EndSession(game.Name));
        }

        _prewarm?.NoteGameExit(_exits.HeavyWorkNotBefore(game.Name, _settings.Auto.RewarmAfterExitMin));
        _auto?.NoteGameExit(game);
        NoteCoverage(game);
        if (!_exits.RunningNames.Any(name => !string.Equals(name, game.Name, StringComparison.OrdinalIgnoreCase)))
        {
            _backgroundYield?.End(); // 다른 등록 게임이 없으면 낮춘 우선순위를 바로 되돌림
        }
    }

    internal void CompleteDetectedExit(GameSettings game, bool unseen)
    {
        if (!unseen)
        {
            OnGameExit(game);
            return;
        }

        if (_playRecorder is { } recorder)
        {
            TryPlayRecording("end", () => recorder.EndSession(game.Name));
        }

        _auto?.NoteGameExit(game); // 보이지 않은 짧은 실행: 나머지 종료 처리는 받지 않습니다.
    }

    /// 현재 프로세스 이름 스냅샷에 이미 실행 중인 게임의 관측을 시작합니다.
    internal void ObserveRunningGames(IReadOnlyList<(int Pid, string Exe)> processes)
    {
        if (_playRecorder is not { } recorder)
        {
            return;
        }

        foreach (GameSettings game in _settings.Games)
        {
            if (processes.Any(process => game.ExeNames.Contains(process.Exe, StringComparer.OrdinalIgnoreCase)))
            {
                TryPlayRecording("observe running game", () => recorder.ObserveExisting(game));
            }
        }
    }

    private void TryPlayRecording(string operation, Action record)
    {
        try
        {
            record();
        }
        catch (Exception e)
        {
            try
            {
                _log.Error($"play recorder {operation} failed", e);
            }
            catch (Exception)
            {
                // 기록기 오류와 로거 오류가 기존 시작·종료 처리를 막지 않게 합니다.
            }
        }
    }

    // 실행 중인 게임 프로세스가 읽은 누적 바이트를 pid 별로 기억합니다 (종료 뒤에는 열 수 없음). 이 틱의 스냅샷만 씁니다.
    private void NoteReads(IReadOnlyList<(int Pid, string Exe)> procs)
    {
        foreach (GameSettings g in _settings.Games)
        {
            foreach ((int pid, string exe) in procs)
            {
                if (g.ExeNames.Contains(exe, StringComparer.OrdinalIgnoreCase) && ProcessSnapshot.ReadBytes(pid) is { } bytes)
                {
                    _reads.Observe(g.Name, pid, bytes);
                }
            }
        }
    }

    // 존 커버리지 진단: 종료 뒤 계획 파일을 한 번 읽어 핫 바이트를 세션 읽기량과 맞춥니다. 읽기량을 못 열었거나 계획이 없으면 생략.
    private void NoteCoverage(GameSettings game)
    {
        long? read = _reads.Take(game.Name);
        if (read is null || !File.Exists(game.Plan))
        {
            return;
        }

        try
        {
            long hot = PlanFile.Load(game.Plan).Stats.TryGetValue("hot_bytes", out object? h) && h is long bytes ? bytes : 0;
            if (SessionCoverage.Percent(read, hot) is not { } percent)
            {
                return;
            }

            _log.Info(SessionCoverage.Line(game.Name, read.Value, hot, percent));
            _auto?.Status.RecordCoverage(game.Name, percent);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warn($"{game.Name}: zone coverage not recorded: {e.GetType().Name}");
        }
    }

    // 고정의 RAM 확인과 예열 유지 점검: 다시 예열은 수 초 걸리므로 재적용 틱을 막지 않게 따로, 겹치지 않게 하나씩.
    private void MaintainPrewarm()
    {
        if (_prewarm is null || _quiet.Defers("prewarm maintenance") || Interlocked.CompareExchange(ref _maintaining, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                CancellationToken warmToken = _auto?.WarmToken ?? CancellationToken.None;
                RunPrewarm(() => _prewarm.Tick(_settings.Auto, warmToken));
            }
            finally
            {
                Interlocked.Exchange(ref _maintaining, 0);
            }
        }, CancellationToken.None);
    }

    /// 시작·다시 읽을 때 설정 보정: 이관 로그 한 줄, canApply 가 거짓이면 자동 모드를 꺼서 (취소 토큰 없는 통째 예열 등이 돌지 않게) 넘김.
    public static AsterSettings ForApply(AsterSettings s, bool canApply, ILog log, ConfigEditor? editor = null)
    {
        if (s.Auto.Migrated)
        {
            RewriteMigrated(editor, log);
        }

        if (!canApply && s.Auto.Enabled)
        {
            log.Warn("auto mode disabled: administrator rights and a trusted data folder are required");
            return s with { Auto = s.Auto with { Enabled = false } };
        }

        return s;
    }

    /// beta.3 설정을 config 잠금 아래 새 키로 한 번 다시 씁니다 (다음 시작부터 이관 로그가 나오지 않음). 실패하면 경고 한 줄만 남기고 계속합니다.
    public static void RewriteMigrated(ConfigEditor? editor, ILog log)
    {
        const string From = "config: beta.3 auto settings (prewarm_recent 1, idle_minutes 3) read as the new defaults (prewarm_recent 0, idle_seconds 30)";
        if (editor is null)
        {
            log.Info(From + "; the file is kept because the data folder is not trusted");
            return;
        }

        try
        {
            editor.UpdateAuto(a => a);
            log.Info(From + "; rewrote the file with the new keys");
        }
        catch (Exception e) when (e is SettingsException or IOException or UnauthorizedAccessException)
        {
            log.Warn(From + "; the file could not be rewritten, will retry on the next start: " + e.Message);
        }
    }

    /// 시작 때 설정·계획 폴더 권한을 확인해 재적용 허용 여부를 정합니다 (관리자 + 두 폴더 모두 신뢰 밖 쓰기 권한 없음).
    public static bool CanApply(bool admin, IEnumerable<string> folders, ILog log, IEnumerable<string>? files = null)
    {
        bool ok = admin;
        foreach (string file in files ?? [])
        {
            IReadOnlyList<string> bad = File.Exists(file) ? AclCheck.UntrustedFileWriters(file) : [];
            if (bad.Count > 0)
            {
                log.Error($"file {Path.GetFileName(file)} is owned or writable by non-admin principals ({string.Join(", ", bad.Take(3))}); reapply disabled");
                ok = false;
            }
        }

        foreach (string dir in folders)
        {
            IReadOnlyList<string> bad = Directory.Exists(dir) ? AclCheck.UntrustedWriters(dir) : [];
            if (bad.Count > 0)
            {
                log.Error($"folder {Path.GetFileName(dir)} is writable by non-admin principals ({string.Join(", ", bad.Take(3))}{(bad.Count > 3 ? $" +{bad.Count - 3} more" : string.Empty)}); reapply disabled");
                ok = false;
            }
        }

        if (!admin)
        {
            log.Warn("not running as administrator: reapply disabled, process detection uses polling");
        }

        return ok;
    }
}
