using System.Threading.Channels;
using AsterCache.Core.Logging;
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
    private readonly object _watchLock = new();
    private volatile AsterSettings _settings;
    private (IProcessWatcher Primary, IProcessWatcher? Fallback) _watchers;
    private string _watchKey;
    private CancellationTokenSource? _generation;
    private bool _restart;
    private int _maintaining;

    public Worker(AsterSettings settings, ILog log, IProcessWatcher primary, IProcessWatcher? fallback, IGameStreamer streamer, ReapplyChecker checker,
        ReapplyGate? gate = null, Prewarmer? prewarm = null, AutoMode? auto = null, SettingsReloader? reload = null, KeepAwake? keepAwake = null)
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
        }

        _log = log;
        _watchers = (primary, fallback);
        _watchKey = WatchKey(settings);
        _dispatcher = new GameDispatcher(settings.Games, streamer, log);
        _checker = checker;
        _gate = gate ?? new ReapplyGate();
    }

    /// 설정이 etw 이고 관리자면 ETW (실패하면 폴링으로 내려감), 아니면 폴링 (기본).
    public static (IProcessWatcher Primary, IProcessWatcher? Fallback) ChooseWatchers(AsterSettings s, bool admin)
    {
        IEnumerable<string> names = WatchNames(s, SystemMemory.TotalBytes()).Distinct(StringComparer.OrdinalIgnoreCase);
        var polling = new PollingProcessWatcher(names, TimeSpan.FromMilliseconds(s.PollMs));
        return admin && s.Watcher == "etw" ? (new EtwProcessWatcher(names), polling) : (polling, null);
    }

    // 감시할 이름: 게임 exe 와 트리거, 그리고 고정을 켠 게임이 있으면 런처 전체 (런처가 뜨면 고정을 풀기 위해).
    public static IEnumerable<string> WatchNames(AsterSettings s, long physicalRamBytes = 0) =>
        s.Games.SelectMany(g => g.ExeNames.Concat(g.Prewarm.TriggerExes)).Concat(s.Games.Any(g => PinKeeper.Wanted(g.Prewarm, physicalRamBytes)) ? LauncherExes.All : []);

    // 감시기를 다시 만들어야 하는지 가리는 열쇠: 감시할 이름, 폴링 간격, 감시기 종류.
    private static string WatchKey(AsterSettings s) =>
        $"{s.Watcher}|{s.PollMs}|" + string.Join('|', WatchNames(s, SystemMemory.TotalBytes()).Select(n => n.ToUpperInvariant()).Distinct().Order(StringComparer.Ordinal));

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
                    BootProbe.RunReal(_settings, _auto.Status, _log); // 서비스 시작 예열이 파일을 데우기 전의 콜드 비용 (부팅 뒤 첫 시작일 때만)
                }

                _prewarm.RunServiceStart(_auto?.StartupRecent(), _auto?.WarmToken ?? default);
            }), CancellationToken.None);
        }

        var channel = Channel.CreateUnbounded<ProcessStart>();
        Task watch = WatchAsync(channel.Writer, stoppingToken);
        Task consume = Task.Run(() => ConsumeAsync(channel.Reader, stoppingToken), CancellationToken.None);
        Task reapply = Task.Run(() => ReapplyLoopAsync(stoppingToken), CancellationToken.None);
        Task reload = Task.Run(() => ReloadLoopAsync(stoppingToken), CancellationToken.None);
        Task keep = Task.Run(() => KeepAwakeLoopAsync(stoppingToken), CancellationToken.None);
        Task auto = _auto is null ? Task.CompletedTask : Task.Run(() => _auto.RunAsync(stoppingToken), CancellationToken.None);
        await Task.WhenAll(watch, consume, reapply, reload, auto, keep);
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
        if (_settings.Games.FirstOrDefault(g => g.ExeNames.Contains(s.ExeName, StringComparer.OrdinalIgnoreCase)) is { } game)
        {
            _prewarm?.NoteGameStart(game.Name); // 그 게임을 예열하던 스트림은 아래 취소에도 끝까지 (다른 게임의 예열은 묶음 경계에서 멈춤), 기록 없는 예열의 예산은 풂
            _prewarm?.ReleasePin(game.Name, "game started"); // 고정은 스트림·재적용 취소보다 먼저 풂
            _gate.Cancel(); // 재적용은 게임 exe 시작에서만 멈춥니다 (런처 같은 트리거 전용 프로그램은 아님)
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
        if (_prewarm?.IsTrigger(s.ExeName) == true)
        {
            _ = Task.Run(() => RunPrewarm(() => _prewarm.OnProcessStart(s, _auto is null ? null : _auto.NarrowTriggered, _auto?.WarmToken ?? default)), CancellationToken.None);
        }
    };

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
                    _keepAwake.Tick();
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
                foreach (GameSettings g in _settings.Games)
                {
                    due.TryAdd(g.Name, start + FirstCheckDelay);
                    if (DateTimeOffset.UtcNow < due[g.Name])
                    {
                        continue;
                    }

                    ReapplyVerdict v = _gate.Run(ct, token => _checker.Check(g, token));
                    bool waiting = v == ReapplyVerdict.Cancelled || (resuming.Contains(g.Name) && v is ReapplyVerdict.GameRunning or ReapplyVerdict.DiskBusy);
                    _ = waiting ? resuming.Add(g.Name) : resuming.Remove(g.Name);
                    due[g.Name] = DateTimeOffset.UtcNow + (waiting ? ResumeRetry : TimeSpan.FromMinutes(g.ReapplyMinutes));
                }

                _auto?.RelocateTick(_gate, ct);
                MaintainPrewarm();
                await Task.Delay(CheckTick, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 종료
        }
    }

    // 고정의 RAM 확인과 예열 유지 점검: 다시 예열은 수 초 걸리므로 재적용 틱을 막지 않게 따로, 겹치지 않게 하나씩.
    private void MaintainPrewarm()
    {
        if (_prewarm is null || Interlocked.CompareExchange(ref _maintaining, 1, 0) != 0)
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
