namespace AsterCache.Service;

/// 프로세스 목록 스냅샷 (한 번 약 11 ms 의 커널 시간) 을 호출자마다 찍지 않고 짧게 나눠 쓰는 저장소.
/// 쓰기 전에 PID 목록 (한 번 약 0.06 ms) 이 직전 스냅샷을 찍을 때와 같은지 확인하므로, 그 사이에 프로세스가 새로 뜨거나 끝났으면 바로 새로 찍습니다:
/// 게임이 방금 시작했는데 낡은 목록으로 "실행 중 아님" 이라 보는 일이 없습니다. 나이 한도는 같은 PID 를 다른 프로그램이 다시 받는 드문 경우를 위한 것입니다.
/// 게임 시작·종료를 알아채는 경로 (감시기, 시작 이벤트, 30 초 종료 점검) 는 이 저장소를 거치지 않고 항상 새로 찍습니다.
internal sealed class SharedProcessSnapshot(Func<IReadOnlyList<int>> pids, Func<IReadOnlyList<(int Pid, string Exe)>> take, Func<long> nowMs)
{
    /// 한 번의 점검 (수십~수백 회의 판정이 0.1~1 초 안에 몰림) 을 덮고, PID 재사용 때문에 낡은 이름이 남아도 이 시간을 넘지 않는 값.
    internal const long MaxAgeMs = 1000;

    private sealed record Entry(IReadOnlyList<int> Pids, IReadOnlyList<(int Pid, string Exe)> Processes, long AtMs);

    private volatile Entry? _last;

    public IReadOnlyList<(int Pid, string Exe)> Get()
    {
        IReadOnlyList<int> ids = pids(); // 스냅샷보다 먼저 읽음: 그 사이에 뜬 프로세스는 스냅샷에는 있고 목록에는 없어 다음 호출에서 다시 찍음
        long now = nowMs();
        if (_last is { } last && now - last.AtMs is >= 0 and < MaxAgeMs && ids.SequenceEqual(last.Pids))
        {
            return last.Processes;
        }

        IReadOnlyList<(int Pid, string Exe)> processes = take();
        _last = new Entry(ids, processes, now);
        return processes;
    }
}
