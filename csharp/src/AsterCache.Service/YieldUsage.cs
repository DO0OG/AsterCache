using AsterCache.Windows;

namespace AsterCache.Service;

/// 프로세스별 CPU 시간·읽기/쓰기 전송 바이트 누계 표본의 짧은 기록: 최근 구간에 일을 한 ("쓰고 있는") 프로세스를 가립니다.
/// 표본은 프로세스를 열지 않는 조회 (ProcessFaults.Snapshot) 한 번의 결과이고 약 4 ms 입니다. 게임이 없을 때도 30초 틱마다 하나씩 쌓아 두므로 처음 비우기 전에도 판정할 수 있습니다
/// (세션 시작 때 몇 초 간격으로 두 번 재는 방식은 시작을 그만큼 늦추고 짧은 구간이라 간헐적으로 일하는 프로그램을 놓치기 쉬워 쓰지 않음).
/// 문턱 값의 근거 (2026-11-21, 이 PC 의 사용자 세션 프로세스 295개를 30초 간격으로 잰 값): 방송·오버레이·메신저·브라우저처럼 계속 일하는 프로세스는 CPU 가 한 코어의 약 1~20%, 쉬는 프로세스 (대기 중인 터미널·셸·도구 서버) 는 0.5% 아래였습니다.
/// CPU 0.5% 이상은 43개, 1% 이상은 31개였고, 전송 16 KiB/s 이상은 19개, 64 KiB/s 이상은 16개였습니다. 그 사이의 값을 문턱으로 잡았습니다.
internal sealed class UsageWindow
{
    /// 판정에 쓰는 구간: 이보다 오래된 표본 가운데 가장 새것을 기준으로 삼습니다.
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// 기준 표본과 최근 표본의 간격이 이보다 짧으면 판정하지 않습니다 (쓰는 것으로 봄).
    public static readonly TimeSpan MinSpan = TimeSpan.FromSeconds(10);

    /// 이 이상 쓰면 쓰고 있는 프로세스: CPU 는 초당 100 ns 단위로 한 코어의 0.5%, 전송은 초당 32 KiB (읽기 + 쓰기, 디스크·파이프를 가리지 않음).
    public const long CpuTicksPerSecond = 50_000;

    public const long IoBytesPerSecond = 32L << 10;

    private readonly List<(DateTimeOffset At, Dictionary<int, (long Cpu, long Io)> Rows)> _samples = [];

    public void Add(DateTimeOffset at, IReadOnlyList<ProcessUsage> usage)
    {
        var rows = new Dictionary<int, (long Cpu, long Io)>(usage.Count);
        foreach (ProcessUsage u in usage)
        {
            rows[u.Pid] = (u.CpuTicks, u.ReadBytes + u.WriteBytes);
        }

        _samples.Add((at, rows));
        while (_samples.Count >= 2 && at - _samples[1].At >= Window)
        {
            _samples.RemoveAt(0); // 기준은 구간보다 오래된 것 가운데 가장 새 표본
        }
    }

    /// 판정할 수 없으면 (표본이 모자라거나 간격이 짧음, 가장 최근 표본에 이 프로세스가 없음) true: 확실히 쉬는 프로세스만 양보시킵니다.
    /// 기준 표본에 없던 프로세스는 구간 안에 뜬 것이라 누계 전체를 구간의 사용량으로 봅니다 (pid 를 이어받은 새 프로세스는 누계가 작아져 같은 방식으로 셈).
    public bool IsInUse(int pid)
    {
        if (_samples.Count < 2)
        {
            return true;
        }

        (DateTimeOffset from, Dictionary<int, (long Cpu, long Io)> old) = _samples[0];
        (DateTimeOffset to, Dictionary<int, (long Cpu, long Io)> latest) = _samples[^1];
        double seconds = (to - from).TotalSeconds;
        if (seconds < MinSpan.TotalSeconds || !latest.TryGetValue(pid, out (long Cpu, long Io) now))
        {
            return true;
        }

        (long cpu0, long io0) = old.TryGetValue(pid, out (long Cpu, long Io) before) && before.Cpu <= now.Cpu && before.Io <= now.Io ? before : (0, 0);
        return (now.Cpu - cpu0) / seconds >= CpuTicksPerSecond || (now.Io - io0) / seconds >= IoBytesPerSecond;
    }
}
