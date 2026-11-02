using AsterCache.Core.Logging;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// config.json 이 바뀌었는지 5 초마다 (길이, 수정 시각) 으로 보고 새로 읽습니다. 검증을 통과하지 못하면 옛 설정을 그대로 두고 로그만 남깁니다.
/// 트레이·자동 등록이 설정을 고쳐도 서비스를 다시 시작하지 않게 하려는 것입니다.
public sealed class SettingsReloader
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(5);

    private readonly Func<(long Length, long Ticks)?> _stamp;
    private readonly Func<AsterSettings> _load;
    private readonly Func<AsterSettings, (IProcessWatcher Primary, IProcessWatcher? Fallback)> _watchers;
    private readonly ILog _log;
    private (long Length, long Ticks)? _last;
    private bool _readFailureLogged;

    /// initial 은 처음 설정을 읽기 직전에 잰 값 (그 사이의 변경을 놓치지 않게).
    public SettingsReloader(Func<(long Length, long Ticks)?> stamp, Func<AsterSettings> load,
        Func<AsterSettings, (IProcessWatcher Primary, IProcessWatcher? Fallback)> watchers, ILog log, (long Length, long Ticks)? initial, TimeSpan? interval = null)
    {
        Interval = interval ?? DefaultInterval;
        _stamp = stamp;
        _load = load;
        _watchers = watchers;
        _log = log;
        _last = initial;
    }

    public TimeSpan Interval { get; }

    public static (long Length, long Ticks)? StampOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : null;
    }

    public (IProcessWatcher Primary, IProcessWatcher? Fallback) Watchers(AsterSettings s) => _watchers(s);

    /// 바뀌었고 유효하면 새 설정, 아니면 null.
    public AsterSettings? Poll()
    {
        (long Length, long Ticks)? now = _stamp();
        if (now == _last || now is null)
        {
            return null; // 파일이 사라졌어도 옛 설정을 유지
        }

        try
        {
            AsterSettings s = _load();
            (_last, _readFailureLogged) = (now, false);
            return s;
        }
        catch (SettingsException e)
        {
            _last = now; // 같은 잘못된 파일을 5 초마다 다시 읽지 않음
            _log.Error("config.json changed but is invalid, keeping the previous settings: " + e.Message);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!_readFailureLogged)
            {
                _readFailureLogged = true;
                _log.Warn("config.json could not be read, will retry: " + e.GetType().Name); // _last 를 올리지 않으므로 다음 확인에서 다시 시도
            }

            return null;
        }
    }
}
