namespace AsterCache.Service;

/// 게임 실행 중 프로세스가 읽은 누적 바이트를 pid 별 마지막 값으로 기억합니다 (종료 뒤에는 프로세스를 열 수 없으므로).
public sealed class SessionReads
{
    private readonly Dictionary<string, Dictionary<int, long>> _byGame = new(StringComparer.Ordinal);

    /// 게임의 pid 가 지금까지 읽은 누적 바이트. 같은 pid 는 최신 값으로 덮어씁니다.
    public void Observe(string game, int pid, long readBytes)
    {
        if (!_byGame.TryGetValue(game, out Dictionary<int, long>? pids))
        {
            pids = new();
            _byGame[game] = pids;
        }

        pids[pid] = readBytes;
    }

    /// 종료 때 꺼내고 잊습니다: pid 별 마지막 값의 합. 한 번도 못 읽었으면 null.
    public long? Take(string game)
    {
        if (!_byGame.Remove(game, out Dictionary<int, long>? pids) || pids.Count == 0)
        {
            return null;
        }

        return pids.Values.Sum();
    }
}

/// 존 커버리지 진단 (핫 바이트 / 세션 읽기량). 순수 계산이라 시험이 직접 부릅니다.
public static class SessionCoverage
{
    private const long Mib = 1L << 20;

    /// 읽기량이 없거나 0 이면 null. 100% 를 넘지 않습니다.
    public static int? Percent(long? read, long hot) =>
        read is > 0 and var r ? (int)Math.Min(100, Math.Max(0, hot) * 100 / r) : null;

    public static string Line(string game, long read, long hot, int percent) =>
        $"{game}: session read {read / Mib} MiB, plan hot set {hot / Mib} MiB = {percent}% of the bytes the process read (not the cache hit rate)";
}
