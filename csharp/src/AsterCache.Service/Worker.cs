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
    private readonly object _watchLock = new();
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

    public Worker(AsterSettings settings, ILog log, IProcessWatcher primary, IProcessWatcher? fallback, IGameStreamer streamer, ReapplyChecker checker,
        ReapplyGate? gate = null, Prewarmer? prewarm = null, AutoMode? auto = null, SettingsReloader? reload = null, KeepAwake? keepAwake = null, DispatchEnv? dispatchEnv = null,
        RiotPhaseWatcher? riot = null, BootQuiet? quiet = null)
    {
        _prewarm = prewarm;
        _prewarm?.SetAuto(settings.Auto);
        _auto = auto;
        _reload = reload;
        _settings = settings;
        _keepAwake = keepAwake ?? (auto is null ? null : new KeepAwake(KeepAwakeEnv.Create(auto.Status.Plays), log, () => _settings)); // 자동 모드가 있는 진짜 서비스에서만 기본으로 켬
        if (prewarm is not null && auto is not null)
        {
            AutoStatus status = auto.Status;
            prewarm.PostUpdateWait = g => AutoRelocator.PostUpdateWait(status, _settings.Auto, g.Name, DateTimeOffset.UtcNow);
            prewarm.ReplanStart = g => AutoRelocator.Replanning(status, g.Name)?.At;
            prewarm.Relocating = name => status.Get(name)?.Stage == AutoStage.Relocating;
            prewarm.HasPlayed = auto.HasPlayed;
        }

        _log = log;
        _riot = riot ?? (prewarm is null ? null : new RiotPhaseWatcher(() => _settings, RiotWatchEnv.Create(prewarm), log)); // 설정 riot.champ_select_prewarm 이 꺼져 있으면 아무것도 부르지 않고 기다림
        DiskActivity.Log = log;
        _watchers = (primary, fallback);
        _watchKey = WatchKey(settings);
        LogWatcherOverride(settings);
        _dispatcher = new GameDispatcher(settings.Games, streamer, log, env: dispatchEnv is null ? null : dispatchEnv with { RestreamCooldownMin = () => _settings.Auto.RestreamCooldownMin, IsWarm = prewarm is null ? dispatchEnv.IsWarm : prewarm.AlreadyWarm }); // 설정을 다시 읽어도 최신 값
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
        DiskKinds.LogAtStart(_settings.Games, _log); // 게임 드라이브별 디스크 종류 (진단용 로그)
        if (_prewarm is not null)
        {
            _ = Task.Run(() => RunPrewarm(() =>
            {
                if (_auto is not null)
                {
                    BootProbe.RunReal(_settings, _auto.Status, _log, _auto.WarmToken); // 서비스 시작 예열이 파일을 데우기 전의 콜드 비용 (부팅 뒤 첫 시작일 때만)
                }

                using var startup = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _auto?.WarmToken ?? default); // 서비스 종료에도 시작 게이트 대기가 끝나게 (자동 모드가 없으면 WarmToken 이 취소되지 않음)
                _prewarm.RunServiceStart(_auto?.StartupRecent(), startup.Token, _auto is { } am ? g => am.HasPlayed(g.Name) : null,
                    _auto is { } am2 && _settings.Auto.Enabled ? name => am2.Status.LastPlayed().TryGetValue(name, out DateTimeOffset at) ? at : null : null);
            }), CancellationToken.None);
        }

        var channel = Channel.CreateUnbounded<ProcessStart>();
        Task watch = WatchAsync(channel.Writer, stoppingToken);
        Task consume = Task.Run(() => ConsumeAsync(channel.Reader, stoppingToken), CancellationToken.None);
        Task reapply = Task.Run(() => ReapplyLoopAsync(stoppingToken), CancellationToken.None);
        Task reload = Task.Run(() => ReloadLoopAsync(stoppingToken), CancellationToken.None);
        Task keep = Task.Run(() => KeepAwakeLoopAsync(stoppingToken), CancellationToken.None);
        Task auto = _auto is null ? Task.CompletedTask : Task.Run(() => _auto.RunAsync(stoppingToken), CancellationToken.None);
        Task riotWatch = _riot is null ? Task.CompletedTask : Task.Run(() => _riot.RunAsync(stoppingToken), CancellationToken.None); // 챔피언 선택 선읽기는 재배치 루프·게임 시작 처리와 따로
        await Task.WhenAll(watch, consume, reapply, reload, auto, keep, riotWatch);
        _auto?.Shutdown();
        _prewarm?.Shutdown();
        _log.Info("service stopped");
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
        if (_settings.Games.FirstOrDefault(g => g.ExeNames.Contains(s.ExeName, StringComparer.OrdinalIgnoreCase)) is { } game)
        {
            _prewarm?.NoteGameStart(game.Name); // 그 게임을 예열하던 스트림은 아래 취소에도 끝까지 (다른 게임의 예열은 묶음 경계에서 멈춤), 기록 없는 예열의 예산은 풂
            _prewarm?.ReleasePin(game.Name, "game started"); // 고정은 스트림·재적용 취소보다 먼저 풂
            _gate.Cancel(); // 재적용은 게임 exe 시작에서만 멈춥니다 (런처 같은 트리거 전용 프로그램은 아님)
            _prewarm?.Recovery?.YieldTo(game.OriginRoot); // 같은 디스크에서 재는 중인 회복 표본도 멈춤
            _auto?.CancelWarm(); // 자동 모드의 예열 작업도 (핫 집합 스트림은 묶음 경계에서 멈추고, 그 뒤 예열은 시작하지 않음)
            if (_auto is not null)
            {
                _ = Task.Run(() => _auto.OnGameStart(game), CancellationToken.None); // last_played 와 자동 트레이스 시작은 감시기 스레드를 막지 않게 따로
            }
        }

        if (LauncherExes.Is(s.ExeName))
        {
            _prewarm?.ReleasePins($"launcher {s.ExeName} started"); // 런처 업데이트가 파일을 고칠 수 있게
        }

        writer.TryWrite(s);
        if (_prewarm?.IsTrigger(s.ExeName) == true && (IsGameExe(s.ExeName) || !HoldTrigger(s)))
        {
            RunTrigger(s);
        }
    };

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

    private async Task ConsumeAsync(ChannelReader<ProcessStart> reader, CancellationToken ct)
    {
        try
        {
            await foreach (ProcessStart s in reader.ReadAllAsync(ct))
            {
                _dispatcher.Handle(s);
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
    private void Apply(AsterSettings s)
    {
        _settings = s;
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
        var resuming = new HashSet<string>(StringComparer.Ordinal); // 취소돼 반쯤 적용된 게임: 게임이 끝날 때까지 짧게 다시 확인
        try
        {
            while (!ct.IsCancellationRequested)
            {
                _quiet.Refresh();
                RunHeldTriggers();
                foreach (GameSettings g in _settings.Games)
                {
                    due.TryAdd(g.Name, start + FirstCheckDelay);
                    if (DateTimeOffset.UtcNow < due[g.Name] || _quiet.Defers("layout check and reapply"))
                    {
                        continue; // 부팅 조용 구간의 검사는 기한을 그대로 둬서 구간이 끝난 다음 틱에 함
                    }

                    ReapplyVerdict v = _gate.Run(ct, token => _checker.Check(g, token));
                    bool waiting = v == ReapplyVerdict.Cancelled || (resuming.Contains(g.Name) && v is ReapplyVerdict.GameRunning or ReapplyVerdict.DiskBusy or ReapplyVerdict.Recovering or ReapplyVerdict.OnBattery or ReapplyVerdict.NotIdle or ReapplyVerdict.PostUpdateDelay);
                    _ = waiting ? resuming.Add(g.Name) : resuming.Remove(g.Name);
                    due[g.Name] = DateTimeOffset.UtcNow + (waiting ? ResumeRetry : TimeSpan.FromMinutes(g.ReapplyMinutes));
                }

                if (_auto is not null && !_quiet.Defers("auto relocation and new trace prewarm"))
                {
                    _auto.RelocateTick(_gate, ct);
                }

                NoteGameExits();
                MaintainPrewarm();
                await (_auto is null ? Task.Delay(CheckTick, ct) : _auto.WaitForTickAsync(CheckTick, ct)); // 발견이 새 게임을 등록하면 일찍 깨움
            }
        }
        catch (OperationCanceledException)
        {
            // 종료
        }
    }

    // 폴링·ETW 어느 쪽이든 재적용 틱마다 (30 초) 프로세스 이름을 한 번 읽어 실행 중이던 게임이 사라졌는지 봅니다.
    private void NoteGameExits()
    {
        if (_settings.Games.Count == 0)
        {
            return;
        }

        try
        {
            IReadOnlyList<(int Pid, string Exe)> procs = ProcessSnapshot.Take();
            NoteReads(procs);
            HashSet<string> names = new(procs.Select(p => p.Exe), StringComparer.OrdinalIgnoreCase);
            Dictionary<int, string?> images = [];
            string? Image(int pid)
            {
                if (!images.ContainsKey(pid))
                {
                    images[pid] = ProcessSnapshot.ImagePath(pid);
                }

                return images[pid];
            }

            bool UnderRoot(GameSettings g) => procs.Any(p => p.Pid != Environment.ProcessId && DispatchEnv.UnderRoot(g, Image(p.Pid)));
            foreach (GameSettings g in _exits.Poll(_settings.Games, names, UnderRoot))
            {
                OnGameExit(g);
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
        _prewarm?.NoteGameExit();
        _auto?.NoteGameExit(game);
        NoteCoverage(game);
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
