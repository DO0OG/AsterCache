using System.Threading.Channels;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Windows;
using Microsoft.Extensions.Hosting;

namespace AsterCache.Service;

/// 서비스 본체: 프로세스 시작 감지 → 스트림, 주기 배치 검사 → 재적용. 임의 경로·명령을 받는 IPC 는 없습니다 (설정 파일과 계획 폴더만 읽음).
public sealed class Worker : BackgroundService
{
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckTick = TimeSpan.FromSeconds(30);

    private readonly AsterSettings _settings;
    private readonly ILog _log;
    private readonly IProcessWatcher _primary;
    private readonly IProcessWatcher? _fallback;
    private readonly GameDispatcher _dispatcher;
    private readonly ReapplyChecker _checker;

    public Worker(AsterSettings settings, ILog log, IProcessWatcher primary, IProcessWatcher? fallback, IGameStreamer streamer, ReapplyChecker checker)
    {
        _settings = settings;
        _log = log;
        _primary = primary;
        _fallback = fallback;
        _dispatcher = new GameDispatcher(settings.Games, streamer, log);
        _checker = checker;
    }

    /// 관리자면 ETW (실패하면 폴링으로 내려감), 아니면 폴링.
    public static (IProcessWatcher Primary, IProcessWatcher? Fallback) ChooseWatchers(AsterSettings s, bool admin)
    {
        IEnumerable<string> names = s.Games.SelectMany(g => g.ExeNames);
        var polling = new PollingProcessWatcher(names, TimeSpan.FromMilliseconds(s.PollMs));
        return admin ? (new EtwProcessWatcher(names), polling) : (polling, null);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.Info($"service started: {_settings.Games.Count} game(s), watcher {_primary.GetType().Name}");
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
                await _primary.RunAsync(s => writer.TryWrite(s), ct);
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
                await _fallback.RunAsync(s => writer.TryWrite(s), ct);
            }
        }
        finally
        {
            writer.TryComplete();
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
        try
        {
            while (!ct.IsCancellationRequested)
            {
                foreach (GameSettings g in _settings.Games.Where(g => DateTimeOffset.UtcNow >= due[g.Name]))
                {
                    due[g.Name] = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(g.ReapplyMinutes);
                    _checker.Check(g);
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
    public static bool CanApply(bool admin, IEnumerable<string> folders, ILog log)
    {
        bool ok = admin;
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
