namespace AsterCache.Service;

/// 유휴 판정: 모든 조건이 필요한 시간 동안 끊김 없이 참이어야 유휴입니다. 하나라도 거짓이면 처음부터 다시 잽니다.
public sealed class IdleTracker
{
    /// 이 이상 CPU 를 쓰는 중이면 다른 일을 하는 PC 로 봄 (백그라운드 작업 정도는 허용). 예열·재배치 판정에서는 쓰지 않음.
    public const double CpuBusyPercent = 20;

    private DateTimeOffset? _since;

    public bool Sample(DateTimeOffset now, bool quiet, TimeSpan needed)
    {
        if (!quiet)
        {
            _since = null;
            return false;
        }

        _since ??= now;
        return now - _since.Value >= needed;
    }

    public void Reset() => _since = null;
}
