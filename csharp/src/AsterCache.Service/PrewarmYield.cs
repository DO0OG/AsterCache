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

/// 스트림이 보통·낮은 I/O 우선순위로 읽은 양과 시간 (묶음 점검 사이를 그 사이의 우선순위로 셈)과 우선순위가 바뀐 횟수.
internal readonly record struct ReadMix(long NormalBytes, double NormalSeconds, long LowBytes, double LowSeconds, int Switches)
{
    public static ReadMix operator +(ReadMix a, ReadMix b) => new(a.NormalBytes + b.NormalBytes, a.NormalSeconds + b.NormalSeconds, a.LowBytes + b.LowBytes, a.LowSeconds + b.LowSeconds, a.Switches + b.Switches);

    /// 예열 끝·취소 줄 뒤에 덧붙임. 낮은 우선순위를 한 번도 쓰지 않았으면 비움 (평소 줄은 그대로).
    public string Note() => Switches == 0 && LowBytes == 0 && LowSeconds <= 0 ? string.Empty
        : $", read {NormalBytes >> 20} MiB in {NormalSeconds:F1} s at normal I/O priority and {LowBytes >> 20} MiB in {LowSeconds:F1} s at low ({Switches} switches)";
}

/// 스트림 한 번의 양보 상태: 읽는 스레드의 I/O 우선순위가 바뀐 구간을 세고, 끝나면 보통으로 되돌립니다.
/// Apply·Finish 는 읽는 스레드에서 불러야 합니다 (setLow 가 현재 스레드에만 적용됨).
internal sealed class ReadYield(Func<bool, bool> setLow, bool startYielding)
{
    // 한가함이 연속 이만큼 확인되면 관측 멈춤 간격을 IdleObserveEvery 로 늘림 (한가하지 않은 결과 한 번에 바로 원래 간격). 스트림마다 새로 세므로 관측 시각 (_driveObservedAt) 과 수명이 같음.
    private const int IdleObservationsForSlow = 3;
    public static readonly TimeSpan IdleObserveEvery = TimeSpan.FromSeconds(30);

    private bool _low;
    private bool _everYielded;
    private DateTimeOffset _since;
    private TimeSpan _total;
    private TimeSpan _excluded;
    private int _idleStreak;
    private int _switches;
    private int _toNormal;
    private (long Done, DateTimeOffset At)? _noted;
    private long _normalBytes;
    private long _lowBytes;
    private TimeSpan _normalTime;
    private TimeSpan _lowTime;

    /// 양보로 시작했고 첫 관측을 아직 하지 않음 (첫 묶음 직전에 한 번 관측하고 내림).
    public bool FirstObservationPending { get; set; } = startYielding;

    /// 지금까지 보통·낮은 우선순위로 읽은 양과 시간, 우선순위가 바뀐 횟수.
    public ReadMix Mix => new(_normalBytes, _normalTime.TotalSeconds, _lowBytes, _lowTime.TotalSeconds, _switches);

    /// 낮음에서 보통으로 돌아온 횟수 (처음 돌아온 때만 로그를 남기려고).
    public int ToNormal => _toNormal;

    /// 한가함이 연속 확인된 만큼 관측 간격을 늘린 값 (normal 은 평소 간격).
    public TimeSpan ObserveEvery(TimeSpan normal) => _idleStreak >= IdleObservationsForSlow ? IdleObserveEvery : normal;

    /// 관측 결과: 한가함이 확인됐으면 연속 횟수를 늘리고, 바쁨·표본 없음·낡음은 한가함으로 세지 않아 0 으로 돌림.
    public void NoteObservation(bool idle) => _idleStreak = idle ? _idleStreak + 1 : 0;

    /// 묶음 점검 때마다 누적 읽은 양 (done) 을 알림: 직전 점검 이후의 양과 시간을 지금의 (직전 점검 이후 바뀌지 않은) 우선순위로 셈. 양이 줄면 (새 읽기 목록) 기준만 다시 잡음.
    public void NoteRead(long done, DateTimeOffset now)
    {
        if (_noted is { } prev && done >= prev.Done)
        {
            if (_low)
            {
                (_lowBytes, _lowTime) = (_lowBytes + done - prev.Done, _lowTime + (now - prev.At));
            }
            else
            {
                (_normalBytes, _normalTime) = (_normalBytes + done - prev.Done, _normalTime + (now - prev.At));
            }
        }

        _noted = (done, now);
    }

    /// 낮은 우선순위로 읽은 시간 (초). 디스크를 읽지 않은 일 (다시 만지기) 에 쓴 시간은 뺍니다.
    public double Seconds => Math.Max(0, (_total - _excluded).TotalSeconds);

    /// 낮은 우선순위인 동안 디스크를 읽지 않는 일에 seconds 를 썼음을 알려 양보 시간에서 뺍니다.
    public void Exclude(double seconds)
    {
        if (_low)
        {
            _excluded += TimeSpan.FromSeconds(seconds);
        }
    }

    /// 상태를 바꿉니다 (low: 낮음, 아니면 보통). 바꾸지 못하면 (setLow 실패) 상태는 그대로이며 다음 관측 때 다시 시도합니다.
    /// 이 스트림에서 처음 낮음으로 바뀐 때만 true (로그 한 줄용).
    public bool Apply(bool low, DateTimeOffset now)
    {
        if (low == _low || !setLow(low))
        {
            return false;
        }

        _low = low;
        _switches++;
        if (low)
        {
            _since = now;
            bool first = !_everYielded;
            _everYielded = true;
            return first;
        }

        _toNormal++;
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
