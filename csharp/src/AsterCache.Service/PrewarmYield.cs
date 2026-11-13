namespace AsterCache.Service;

/// 핫 집합 예열 스트림이 읽는 동안 본 디스크 부하에 대한 판정.
public enum ReadLoad
{
    /// 같은 디스크에서 다른 등록 게임이 돌고 외부 부하가 바쁨: 스트림을 취소 (게임 실행 중 읽기 금지 규칙).
    Cancel,

    /// 취소하지 않고 낮은 I/O 우선순위로 계속 읽음 (다른 읽기가 있거나 부하를 모름).
    Yield,

    /// 보통 I/O 우선순위로 읽음 (디스크가 한가함).
    Normal,
}

/// 양보 판정 (순수 함수).
public static class PrewarmYield
{
    // 부팅 직후에는 런처·Windows 의 다른 시작 읽기와 겹칠 가능성이 커서 양보로 시작합니다.
    private static readonly TimeSpan BootWindow = TimeSpan.FromMinutes(15);

    /// 다른 게임이 돌고 바쁘면 취소 (기존 규칙: 못 읽는 고장은 바쁨으로 보지 않음). 아니면 한가하다고 확인됐을 때만 보통, 바쁘거나 표본을 모르면 양보 (모르면 양보 쪽).
    public static ReadLoad Decide(bool otherGameRunning, double? bps, DiskActivity.SampleState state)
    {
        bool busy = DiskActivity.IsBusy(bps, state);
        if (otherGameRunning && busy)
        {
            return ReadLoad.Cancel;
        }

        return state == DiskActivity.SampleState.Ok && bps is not null && !busy ? ReadLoad.Normal : ReadLoad.Yield;
    }

    /// 스트림을 양보 상태로 시작하는가: 런처 계기 예열 (사유가 "trigger ...") 이거나 가동 시간이 BootWindow 미만.
    public static bool StartsYielding(string? why, long uptimeMs) =>
        why?.StartsWith("trigger", StringComparison.Ordinal) == true || uptimeMs < BootWindow.TotalMilliseconds;
}

/// 스트림 한 번의 양보 상태: 읽는 스레드의 I/O 우선순위가 바뀐 구간을 세고, 끝나면 보통으로 되돌립니다.
/// Apply·Finish 는 읽는 스레드에서 불러야 합니다 (setLow 가 현재 스레드에만 적용됨).
internal sealed class ReadYield(Func<bool, bool> setLow, bool startYielding)
{
    private bool _low;
    private bool _everYielded;
    private DateTimeOffset _since;
    private TimeSpan _total;

    /// 양보로 시작했고 첫 관측을 아직 하지 않음 (첫 묶음 직전에 한 번 관측하고 내림).
    public bool FirstObservationPending { get; set; } = startYielding;

    /// 낮은 우선순위로 읽은 시간 (초).
    public double Seconds => _total.TotalSeconds;

    /// 상태를 바꿉니다 (low: 낮음, 아니면 보통). 바꾸지 못하면 (setLow 실패) 상태는 그대로이며 다음 관측 때 다시 시도합니다.
    /// 이 스트림에서 처음 낮음으로 바뀐 때만 true (로그 한 줄용).
    public bool Apply(bool low, DateTimeOffset now)
    {
        if (low == _low || !setLow(low))
        {
            return false;
        }

        _low = low;
        if (low)
        {
            _since = now;
            bool first = !_everYielded;
            _everYielded = true;
            return first;
        }

        _total += now - _since;
        return false;
    }

    /// 스트림이 끝나면 (정상·취소·예외 모두) 낮음이 남지 않게 보통으로 되돌립니다. 낮음이었는데 되돌리지 못했으면 false.
    public bool Finish(DateTimeOffset now)
    {
        if (!_low)
        {
            return true;
        }

        bool restored = setLow(false);
        _total += now - _since;
        _low = false;
        return restored;
    }
}
