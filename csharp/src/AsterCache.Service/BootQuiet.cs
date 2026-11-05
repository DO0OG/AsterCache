using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 부팅 조용 구간: 서비스가 부팅 뒤 Prewarmer.BootDelay 안에 떴을 때만, 부팅 BootDelay 가 지나고 게임 드라이브가 Prewarmer.IdleNeeded 동안 한가해질 때까지
/// 서비스가 스스로 시작하는 게임 드라이브 작업 (계기 예열, 재배치·새 트레이스 예열, 배치 검사와 재적용, 유지 예열, HDD 깨워 두기) 을 보류합니다.
/// 한 번 끝나면 다시 조용해지지 않습니다. 보류한 작업은 버리지 않고, 호출하는 쪽이 구간이 끝난 뒤 기존 조건으로 다시 판단합니다.
public sealed class BootQuiet
{
    private readonly bool _applies;
    private readonly Func<long> _uptimeMs;
    private readonly Func<bool> _idle;
    private readonly Func<DateTimeOffset> _now;
    private readonly ILog _log;
    private readonly object _gate = new();
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal); // 무엇별 보류 로그를 구간당 한 번만, _gate 로 보호
    private bool _ended; // 구간이 끝났음 (되돌아가지 않음), _gate 로 보호
    private DateTimeOffset? _idleSince; // 부팅 BootDelay 뒤 게임 드라이브가 한가해지기 시작한 시각, _gate 로 보호

    /// uptimeMs: 시스템 가동 시간 (ms). idle: 게임 드라이브가 지금 모두 한가한가 (읽을 수 없으면 false). now: 시계 (IdleNeeded 를 잼).
    public BootQuiet(Func<long> uptimeMs, Func<bool> idle, ILog log, Func<DateTimeOffset>? now = null)
    {
        _uptimeMs = uptimeMs;
        _idle = idle;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _applies = uptimeMs() < Prewarmer.BootDelay.TotalMilliseconds; // 부팅 뒤에 뜬 서비스 (재시작 포함) 는 구간이 없음
    }

    /// 실제 환경: 가동 시간은 uptimeMs (없으면 Environment.TickCount64), 유휴는 게임 드라이브 전부의 처리량이 바쁨 기준 미만 (하나라도 읽을 수 없으면 유휴 아님).
    public static BootQuiet Real(ILog log, Func<IEnumerable<GameSettings>> games, Func<long>? uptimeMs = null) => new(
        uptimeMs ?? (() => Environment.TickCount64),
        () => games().All(g => DiskActivity.BytesPerSecond(PlanFile.SplitDrive(g.OriginRoot)) is { } bps && bps < DiskActivity.BusyBytesPerSecond),
        log);

    /// 지금 구간인가: 부팅 뒤 BootDelay 안에 뜬 서비스이고, 아직 끝나지 않았습니다.
    public bool Active
    {
        get
        {
            lock (_gate)
            {
                return _applies && !_ended;
            }
        }
    }

    /// 구간 상태를 한 번 갱신합니다 (재적용 틱마다). BootDelay 가 지나고 게임 드라이브가 IdleNeeded 동안 한가하면 구간을 끝냅니다.
    public void Refresh()
    {
        if (!Active)
        {
            return;
        }

        bool idle = _uptimeMs() >= Prewarmer.BootDelay.TotalMilliseconds && _idle();
        lock (_gate)
        {
            DateTimeOffset now = _now();
            _idleSince = idle ? _idleSince ?? now : null;
            if (_idleSince is { } since && now - since >= Prewarmer.IdleNeeded)
            {
                _ended = true;
                _log.Info("boot quiet period ended: deferred work runs under the normal idle rules");
            }
        }
    }

    /// 이 작업을 구간 동안 보류해야 하면 true (구간이 아니면 false). 보류 로그는 무엇별로 구간당 한 번만 남깁니다.
    public bool Defers(string what)
    {
        lock (_gate)
        {
            if (!_applies || _ended)
            {
                return false;
            }

            if (_logged.Add(what))
            {
                _log.Info($"{what} deferred: boot quiet period");
            }

            return true;
        }
    }
}
