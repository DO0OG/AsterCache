using System.Threading.Channels;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using Microsoft.Extensions.Hosting;

namespace AsterCache.Service;

/// 서비스 본체: 프로세스 시작 감지 → 스트림, 주기 배치 검사 → 재적용. 임의 경로·명령을 받는 IPC 는 없습니다 (설정 파일과 계획 폴더만 읽음).
public sealed class Worker : BackgroundService
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckTick = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ResumeRetry = TimeSpan.FromMinutes(5);

    private readonly AsterSettings _settings;
    private readonly ILog _log;
    private readonly IProcessWatcher _primary;
    private readonly IProcessWatcher? _fallback;
    private readonly GameDispatcher _dispatcher;
    private readonly ReapplyChecker _checker;
    private readonly ReapplyGate _gate;
    private readonly Prewarmer? _prewarm;

    public Worker(AsterSettings settings, ILog log, IProcessWatcher primary, IProcessWatcher? fallback, IGameStreamer streamer, ReapplyChecker checker,
        ReapplyGate? gate = null, Prewarmer? prewarm = null)
    {
        _prewarm = prewarm;
        _settings = settings;
        _log = log;
        _primary = primary;
        _fallback = fallback;
        _dispatcher = new GameDispatcher(settings.Games, streamer, log);
        _checker = checker;
        _gate = gate ?? new ReapplyGate();
    }

    /// 설정이 etw 이고 관리자면 ETW (실패하면 폴링으로 내려감), 아니면 폴링 (기본).
    public static (IProcessWatcher Primary, IProcessWatcher? Fallback) ChooseWatchers(AsterSettings s, bool admin)
    {
        IEnumerable<string> names = s.Games.SelectMany(g => g.ExeNames.Concat(g.Prewarm.TriggerExes)).Distinct(StringComparer.OrdinalIgnoreCase);
        var polling = new PollingProcessWatcher(names, TimeSpan.FromMilliseconds(s.PollMs));
        return admin && s.Watcher == "etw" ? (new EtwProcessWatcher(names), polling) : (polling, null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_settings.Games.Count == 0)
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

        _log.Info($"service started: {_settings.Games.Count} game(s), watcher {_primary.GetType().Name}");
        if (_prewarm is not null)
        {
            _ = Task.Run(() => RunPrewarm(() => _prewarm.RunServiceStart()), CancellationToken.None);
        }

        var channel = Channel.CreateUnbounded<ProcessStart>();
        Task watch = WatchAsync(channel.Writer, stoppingToken);
        Task consume = Task.Run(() => ConsumeAsync(channel.Reader, stoppingToken), CancellationToken.None);
        Task reapply = Task.Run(() => ReapplyLoopAsync(stoppingToken), CancellationToken.None);
        await Task.WhenAll(watch, consume, reapply);
        _log.Info("service stopped");
    }

    private async Task WatchAsync(ChannelWriter<ProcessStart> writer, CancellationToken ct)
    {
        try
        {
            try
            {
                _log.Info($"process watcher: {_primary.GetType().Name}");
                await _primary.RunAsync(OnStart(writer), ct);
                if (!ct.IsCancellationRequested)
                {
                    _log.Warn("process watcher ended unexpectedly");
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested)
            {
                _log.Error("process watcher failed", e);
            }

            if (!ct.IsCancellationRequested && _fallback is not null)
            {
                _log.Warn($"falling back to {_fallback.GetType().Name}");
                _log.Info($"process watcher: {_fallback.GetType().Name}");
                await _fallback.RunAsync(OnStart(writer), ct);
            }
        }
        finally
        {
            writer.TryComplete();
        }
    }

    // 게임이 뜨면 돌고 있는 재적용을 먼저 멈춥니다 (읽기 큐 처리보다 앞서, 감시 스레드에서 바로).
    private Action<ProcessStart> OnStart(ChannelWriter<ProcessStart> writer) => s =>
    {
        if (_settings.Games.Any(g => g.ExeNames.Contains(s.ExeName, StringComparer.OrdinalIgnoreCase)))
        {
            _gate.Cancel(); // 재적용은 게임 exe 시작에서만 멈춥니다 (런처 같은 트리거 전용 프로그램은 아님)
        }

        writer.TryWrite(s);
        if (_prewarm?.IsTrigger(s.ExeName) == true)
        {
            _ = Task.Run(() => RunPrewarm(() => _prewarm.OnProcessStart(s)), CancellationToken.None);
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

    private async Task ReapplyLoopAsync(CancellationToken ct)
    {
        DateTimeOffset start = DateTimeOffset.UtcNow;
        var due = _settings.Games.ToDictionary(g => g.Name, _ => start + FirstCheckDelay);
        var resuming = new HashSet<string>(StringComparer.Ordinal); // 취소돼 반쯤 적용된 게임: 게임이 끝날 때까지 짧게 다시 확인
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (GameSettings g in _settings.Games.Where(g => DateTimeOffset.UtcNow >= due[g.Name]))
                {
                    ReapplyVerdict v = _gate.Run(ct, token => _checker.Check(g, token));
                    bool waiting = v == ReapplyVerdict.Cancelled || (resuming.Contains(g.Name) && v is ReapplyVerdict.GameRunning or ReapplyVerdict.DiskBusy);
                    _ = waiting ? resuming.Add(g.Name) : resuming.Remove(g.Name);
                    due[g.Name] = DateTimeOffset.UtcNow + (waiting ? ResumeRetry : TimeSpan.FromMinutes(g.ReapplyMinutes));
                }

                await Task.Delay(CheckTick, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 종료
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
