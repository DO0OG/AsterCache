using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 게임 종료 감지: 실행 중인 것을 본 게임의 ExeNames 가 모두 사라진 채 유예가 지나면 종료로 봅니다. 감시기 종류 (ETW·폴링) 와 무관하게 Worker 의 주기 루프에서 한 스레드로 부릅니다.
public sealed class GameExitTracker
{
    /// 등록 이름이 모두 사라진 뒤 이 시간 안에 다시 뜨면 종료가 아님 (런처 -> 본체 전환 틈). 시작 스트림 쿨다운과 같은 값 재사용.
    public static readonly TimeSpan Grace = GameDispatcher.Cooldown;

    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _goneSince = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _now;

    public GameExitTracker(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    /// names 는 지금 떠 있는 프로세스 이미지 이름. 이번 호출에서 끝난 게임을 돌려줍니다 (설정에서 빠진 게임은 잊음).
    /// 설치 폴더 아래 미등록 프로세스는 보지 않습니다: 등록 이름만 모두 사라지고 Grace 가 지나야 종료입니다.
    public IReadOnlyList<GameSettings> Poll(IReadOnlyList<GameSettings> games, IReadOnlySet<string> names)
    {
        List<GameSettings> exited = [];
        DateTimeOffset now = _now();
        foreach (GameSettings g in games)
        {
            if (g.ExeNames.Any(names.Contains))
            {
                _ = _running.Add(g.Name);
                _ = _goneSince.Remove(g.Name);
            }
            else if (_running.Contains(g.Name))
            {
                if (!_goneSince.TryAdd(g.Name, now) && now - _goneSince[g.Name] >= Grace)
                {
                    _ = _running.Remove(g.Name);
                    _ = _goneSince.Remove(g.Name);
                    exited.Add(g);
                }
            }
        }

        _running.IntersectWith(games.Select(g => g.Name));
        foreach (string gone in _goneSince.Keys.Where(k => !_running.Contains(k)).ToList())
        {
            _ = _goneSince.Remove(gone);
        }

        return exited;
    }
}
