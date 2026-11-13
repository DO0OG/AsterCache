namespace AsterCache.Service;

/// 예산 계산 (WarmBudget) 이 쓰는 게임별 추정값 (전체 크기, 관찰량) 을 입력 서명이 같고 MaxAge 안이면 다시 계산하지 않고 돌려줍니다.
/// 값 (모름 = null 포함) 과 서명만 보관합니다: 계획·목록 해석 결과는 붙잡지 않음. 예열이 실제로 읽을 범위를 정하는 호출은 이 캐시를 거치지 않습니다.
internal sealed class WarmEstimateCache(Func<DateTimeOffset> now)
{
    // 입력 파일이 그대로여도 게임 파일 크기 (게임 갱신) 는 서명에 없어서, 이 시간 안에 반영되게 합니다.
    internal static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(10);

    private sealed record Entry(string Stamp, DateTimeOffset At, long? Value);

    private readonly object _lock = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);

    /// stamp 가 null 이면 (서명을 만들 수 없음) 저장하지 않고 compute 를 그대로 부릅니다. compute 는 잠금 밖에서 돕니다.
    public long? Get(string kind, string game, string? stamp, Func<long?> compute)
    {
        if (stamp is null)
        {
            return compute();
        }

        string key = kind + "|" + game;
        DateTimeOffset at = now();
        lock (_lock)
        {
            if (_entries.TryGetValue(key, out Entry? hit) && hit.Stamp == stamp && at >= hit.At && at - hit.At < MaxAge)
            {
                return hit.Value;
            }
        }

        long? value = compute();
        lock (_lock)
        {
            _entries[key] = new Entry(stamp, at, value);
        }

        return value;
    }

    /// 새로 읽은 값을 넣습니다. stamp 는 읽기 전에 잡은 것 (읽는 동안 입력이 바뀌면 다음에 다시 계산됨), null 이면 넣지 않음.
    public void Set(string kind, string game, string? stamp, long? value)
    {
        if (stamp is null)
        {
            return;
        }

        DateTimeOffset at = now();
        lock (_lock)
        {
            _entries[kind + "|" + game] = new Entry(stamp, at, value);
        }
    }

    /// 목록에 없는 게임의 항목을 지웁니다 (games 는 대소문자 무시 집합) (항목 수가 등록 게임 수를 넘지 않게).
    public void Retain(IReadOnlySet<string> games)
    {
        lock (_lock)
        {
            foreach (string key in _entries.Keys.Where(k => !games.Contains(k[(k.IndexOf('|') + 1)..])).ToList())
            {
                _ = _entries.Remove(key);
            }
        }
    }

    internal int Count
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }
}
