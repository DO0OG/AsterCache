using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 부팅 조용 구간: 서비스가 부팅 뒤 Prewarmer.BootDelay 안에 떴을 때만, 부팅 BootDelay 가 지나고 게임 드라이브가 Prewarmer.IdleNeeded 동안 한가해질 때까지
/// 서비스가 스스로 시작하는 게임 드라이브 작업 (계기 예열, 재배치·새 트레이스 예열, 배치 검사와 재적용, 유지 예열, HDD 깨워 두기) 을 보류합니다.
/// 한 번 끝나면 다시 조용해지지 않습니다. 보류한 작업은 버리지 않고, 호출하는 쪽이 구간이 끝난 뒤 기존 조건으로 다시 판단합니다.
/// 상한: 가동 시간이 BootDelay + MaxQuiet 에 이르면 유휴를 기다리지 않고 끝냅니다 (읽을 수 없거나 계속 바쁜 드라이브가 재배치·검사를 서비스 재시작까지 막지 않게).
public sealed class BootQuiet
{
    // 상한 10 분: 부팅 뒤 5 분 + 10 분이면 시작 직후 경쟁은 지나고, 드라이브가 끝내 한가해지지 않아도 배치 검사와 재배치가 멈춰 있는 시간을 제한합니다.
    public static readonly TimeSpan MaxQuiet = TimeSpan.FromMinutes(10);
    // 벽시계가 가동 시간보다 이만큼 더 앞서면 절전·최대 절전에서 복귀한 것으로 봅니다 (틱 사이의 정상 차이는 이보다 훨씬 작음).
    private static readonly TimeSpan ResumeJump = TimeSpan.FromMinutes(2);

    private bool _applies; // 구간 중인가 (절전 복귀 때 다시 켬), _gate 로 보호
    private readonly Func<long> _uptimeMs;
    private readonly Func<bool> _idle;
    private readonly Func<DateTimeOffset> _now;
    private readonly ILog _log;
    private readonly object _gate = new();
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal); // 무엇별 보류 로그를 구간당 한 번만, _gate 로 보호
    private bool _ended; // 구간이 끝났음 (되돌아가지 않음), _gate 로 보호
    private DateTimeOffset? _idleSince; // 부팅 BootDelay 뒤 게임 드라이브가 한가해지기 시작한 시각, _gate 로 보호
    private long _origin; // 구간이 시작한 가동 시간 (복귀 때 그 시점으로 다시 맞춤), _gate 로 보호
    private (long Uptime, DateTimeOffset Now)? _prev; // 직전 Refresh 의 가동 시간과 벽시계 (복귀 판정), _gate 로 보호

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

    /// 구간 상태를 한 번 갱신합니다 (재적용 틱마다). 벽시계가 가동 시간보다 ResumeJump 이상 앞서면 절전 복귀로 보고 그 시점부터 구간을 다시 시작합니다.
    /// BootDelay 가 지나고 게임 드라이브가 IdleNeeded 동안 한가하면 구간을 끝냅니다. 구간의 가동 시간이 BootDelay + MaxQuiet 에 이르면 유휴와 무관하게 끝냅니다.
    public void Refresh()
    {
        long uptime = _uptimeMs();
        DateTimeOffset now = _now();
        long origin;
        lock (_gate)
        {
            if (_prev is { } prev && now - prev.Now - TimeSpan.FromMilliseconds(uptime - prev.Uptime) >= ResumeJump)
            {
                _applies = true;
                _ended = false;
                _origin = uptime;
                _idleSince = null;
                _logged.Clear();
                _log.Info("boot quiet period restarted: resume from sleep detected");
            }

            _prev = (uptime, now);
            if (!_applies || _ended)
            {
                return;
            }

            origin = _origin;
        }

        long elapsed = uptime - origin;
        bool capped = elapsed >= (Prewarmer.BootDelay + MaxQuiet).TotalMilliseconds;
        bool idle = !capped && elapsed >= Prewarmer.BootDelay.TotalMilliseconds && _idle();
        lock (_gate)
        {
            _idleSince = idle ? _idleSince ?? now : null;
            if (capped)
            {
                _ended = true;
                _log.Info("boot quiet period ended: uptime cap (BootDelay + MaxQuiet) reached, deferred work runs under the normal idle rules");
            }
            else if (_idleSince is { } since && now - since >= Prewarmer.IdleNeeded)
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
