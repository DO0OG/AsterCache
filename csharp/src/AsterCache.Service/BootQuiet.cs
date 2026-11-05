using System.Runtime.InteropServices;
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
    // 절전 누적 시간 (가동 시간 − 절전 제외 가동 시간) 이 직전 Refresh 보다 이만큼 늘면 절전·최대 절전에서 복귀한 것으로 봅니다.
    // 두 시계를 잇따라 읽는 차이는 수 ms 라 5 초면 충분히 작고, 30 초 같은 짧은 절전도 놓치지 않습니다. 벽시계는 UTC 보정에 흔들려 쓰지 않습니다.
    private static readonly TimeSpan ResumeSleep = TimeSpan.FromSeconds(5);

    private bool _applies; // 구간 중인가 (절전 복귀 때 다시 켬), _gate 로 보호
    private readonly Func<long> _uptimeMs;
    private readonly Func<long> _unbiasedMs; // 절전 시간을 뺀 가동 시간 (복귀 판정 전용)
    private readonly Func<bool> _idle;
    private readonly ILog _log;
    private readonly object _gate = new();
    private readonly HashSet<string> _logged = new(StringComparer.Ordinal); // 무엇별 보류 로그를 구간당 한 번만, _gate 로 보호
    private bool _ended; // 구간이 끝났음 (되돌아가지 않음), _gate 로 보호
    private long? _idleSince; // 부팅 BootDelay 뒤 게임 드라이브가 한가해지기 시작한 가동 시간 (ms, 절전 포함 단조 시계), _gate 로 보호
    private long _origin; // 구간이 시작한 가동 시간 (복귀 때 그 시점으로 다시 맞춤), _gate 로 보호
    private long? _prevSlept; // 직전 Refresh 의 누적 절전 시간 (ms, 복귀 판정), _gate 로 보호

    /// uptimeMs: 시스템 가동 시간 (ms, 절전 포함). idle: 게임 드라이브가 지금 모두 한가한가 (읽을 수 없으면 false). now: 벽시계, 판정에 쓰지 않습니다 (시험이 UTC 보정을 주입하는 데만 씀).
    /// unbiasedMs: 절전 시간을 뺀 가동 시간 (ms, 복귀 판정 전용). 없으면 QueryUnbiasedInterruptTime.
    public BootQuiet(Func<long> uptimeMs, Func<bool> idle, ILog log, Func<DateTimeOffset>? now = null, Func<long>? unbiasedMs = null)
    {
        _uptimeMs = uptimeMs;
        _unbiasedMs = unbiasedMs ?? BootQuietNative.UnbiasedMs;
        _idle = idle;
        _log = log;
        _applies = uptimeMs() < Prewarmer.BootDelay.TotalMilliseconds; // 부팅 뒤에 뜬 서비스 (재시작 포함) 는 구간이 없음
    }

    /// 실제 환경: 가동 시간은 uptimeMs (없으면 Environment.TickCount64), 유휴는 게임 드라이브 전부의 처리량이 바쁨 기준 미만 (하나라도 읽을 수 없으면 유휴 아님).
    public static BootQuiet Real(ILog log, Func<IEnumerable<GameSettings>> games, Func<long>? uptimeMs = null, Func<long>? unbiasedMs = null) => new(
        uptimeMs ?? (() => Environment.TickCount64),
        () => games().All(g => DiskActivity.BytesPerSecond(PlanFile.SplitDrive(g.OriginRoot)) is { } bps && bps < DiskActivity.BusyBytesPerSecond),
        log,
        null,
        unbiasedMs);

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

    /// 구간 상태를 한 번 갱신합니다 (재적용 틱마다). 절전 누적 시간이 직전 Refresh 보다 ResumeSleep 이상 늘면 절전 복귀로 보고 그 시점부터 구간을 다시 시작합니다 (벽시계는 쓰지 않습니다).
    /// BootDelay 가 지나고 게임 드라이브가 IdleNeeded 동안 한가하면 구간을 끝냅니다. 구간의 가동 시간이 BootDelay + MaxQuiet 에 이르면 유휴와 무관하게 끝냅니다.
    public void Refresh()
    {
        long uptime = _uptimeMs();
        // 절전 누적 시간 = 절전 포함 가동 시간 − 절전 제외 가동 시간. 빠른 시작(Fast Startup): 세션 0 이 최대 절전되었다가 깨어나 서비스 프로세스가 이어지므로 이 판정으로 잡힙니다.
        long slept = uptime - _unbiasedMs();
        long origin;
        lock (_gate)
        {
            if (_prevSlept is { } prevSlept && slept - prevSlept >= ResumeSleep.TotalMilliseconds)
            {
                _applies = true;
                _ended = false;
                _origin = uptime;
                _idleSince = null;
                _logged.Clear();
                _log.Info("boot quiet period restarted: resume from sleep detected");
            }

            _prevSlept = slept;
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
            _idleSince = idle ? _idleSince ?? uptime : null;
            if (capped)
            {
                _ended = true;
                _log.Info("boot quiet period ended: uptime cap (BootDelay + MaxQuiet) reached, deferred work runs under the normal idle rules");
            }
            else if (_idleSince is { } since && uptime - since >= Prewarmer.IdleNeeded.TotalMilliseconds)
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

/// 절전을 뺀 단조 시계. Environment.TickCount64 (절전 포함)와의 차이가 절전 누적 시간이며, 복귀 판정은 이 차이로 합니다.
internal static partial class BootQuietNative
{
    // https://learn.microsoft.com/en-us/windows/win32/api/realtimeapiset/nf-realtimeapiset-queryunbiasedinterrupttime
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);

    /// 절전을 뺀 가동 시간 (ms, 100ns 단위를 나눔). 호출이 실패하면 예외를 던집니다.
    public static long UnbiasedMs()
    {
        if (!QueryUnbiasedInterruptTime(out ulong time))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        return (long)(time / 10_000);
    }
}
