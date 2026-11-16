using System.Collections.Concurrent;

namespace AsterCache.Service;

/// 디스패처 대기열에 있는 게임 시작의 게임별 개수. 감시기 스레드는 Add 만 하고 (잠금 없음), 디스패처가 꺼낼 때 Remove 합니다.
internal sealed class QueuedStarts
{
    private readonly ConcurrentDictionary<string, int> _counts = new(StringComparer.OrdinalIgnoreCase);

    public void Add(string game) => _counts.AddOrUpdate(game, 1, (_, n) => n + 1);

    public void Remove(string game) => _counts.AddOrUpdate(game, 0, (_, n) => Math.Max(0, n - 1));

    /// game 이 아닌 다른 게임의 시작이 대기 중인가.
    public bool AnyOther(string game) => _counts.Any(pair => pair.Value > 0 && !string.Equals(pair.Key, game, StringComparison.OrdinalIgnoreCase));
}
