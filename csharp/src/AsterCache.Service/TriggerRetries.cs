namespace AsterCache.Service;

/// 같은 디스크의 다른 게임이 실행 중이라 거절된 트리거 예열의 대기표: 막던 게임이 모두 사라진 것을 처음 볼 때 트리거 프로세스가 아직 살아 있으면 한 번 다시 시도하게 합니다.
internal sealed class TriggerRetries
{
    /// 거절이 이보다 오래됐으면 다시 시도하지 않음 (런처만 켜 둔 채 한참 뒤에 막던 게임이 꺼져도 읽지 않게).
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(30);

    private sealed record Entry(string Exe, IReadOnlySet<string> Blockers, DateTimeOffset At);

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _waiting = new(StringComparer.OrdinalIgnoreCase); // 게임 이름 -> 트리거 exe, 막던 게임들, 거절 시각

    /// 막던 게임이 없으면 적지 않습니다 (기다릴 사건이 없음). 같은 게임의 이전 표는 덮어씁니다.
    public void Wait(string game, string exe, IReadOnlySet<string> blockers, DateTimeOffset at)
    {
        if (blockers.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            _waiting[game] = new Entry(exe, blockers, at);
        }
    }

    /// gone: 지금 종료 유예 중인 게임들. 막던 게임 중 하나 이상이 gone 이고 나머지는 이미 떠났으면 (stillActive 거짓) 표에서 빼고,
    /// 트리거 프로세스가 살아 있는 것만 돌려줍니다 (이미 꺼졌으면 기존 대기 규칙에 맡김). 나이 상한을 넘은 표는 버립니다.
    public IReadOnlyList<(string Game, string Exe)> NoteGone(IReadOnlySet<string> gone, Func<string, bool> stillActive, Func<string, bool> processRunning, DateTimeOffset now)
    {
        List<(string Game, string Exe)> due = [];
        KeyValuePair<string, Entry>[] candidates;
        lock (_sync)
        {
            if (_waiting.Count == 0)
            {
                return due;
            }

            foreach (string old in _waiting.Where(pair => now - pair.Value.At > MaxAge).Select(pair => pair.Key).ToArray())
            {
                _ = _waiting.Remove(old);
            }

            candidates = [.. _waiting.Where(pair => pair.Value.Blockers.Any(gone.Contains))];
        }

        // 프로세스·등록 조회는 잠금 밖에서 하고, 뺄 때 같은 표인지 다시 확인
        foreach ((string game, Entry entry) in candidates.Where(c => c.Value.Blockers.All(b => gone.Contains(b) || !stillActive(b))))
        {
            lock (_sync)
            {
                if (!_waiting.TryGetValue(game, out Entry? current) || !ReferenceEquals(current, entry))
                {
                    continue;
                }

                _ = _waiting.Remove(game);
            }

            if (processRunning(entry.Exe))
            {
                due.Add((game, entry.Exe));
            }
        }

        return due;
    }
}
