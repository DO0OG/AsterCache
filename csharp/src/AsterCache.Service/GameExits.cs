using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 게임 종료 감지: 등록된 ExeNames 가 살아 있으면 실행 중이며, 사라진 뒤 Grace 가 지나면 종료입니다.
public sealed class GameExitTracker
{
    /// 등록 이름이 모두 사라진 뒤 이 시간 안에 다시 뜨면 종료가 아님 (런처 -> 본체 전환 틈). 시작 스트림 쿨다운과 같은 값 재사용.
    public static readonly TimeSpan Grace = GameDispatcher.Cooldown;
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _goneSince = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal); // Poll 이 한 번이라도 띄운 등록 게임 (시작 이벤트만 있던 짧은 실행과 구분)
    private readonly Func<DateTimeOffset> _now;

    /// 등록 이름이 모두 사라져 Grace 를 기다리는 (종료 유예 중) 게임 이름.
    public IReadOnlySet<string> GraceNames => new HashSet<string>(_goneSince.Keys, StringComparer.OrdinalIgnoreCase);

    /// 읽기 전용: names 기준으로 등록 이름이 모두 사라진 실행 추적 게임 (except 제외). Poll 과 달리 상태를 바꾸지 않아 종료 판정은 틱에만 남습니다.
    public IReadOnlySet<string> GoneNames(IReadOnlyList<GameSettings> games, IReadOnlySet<string> names, string? except = null) =>
        new HashSet<string>(games.Where(g => _running.Contains(g.Name) && !string.Equals(g.Name, except, StringComparison.OrdinalIgnoreCase) && !g.ExeNames.Any(names.Contains)).Select(g => g.Name), StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> RunningNames => new HashSet<string>(_running, StringComparer.OrdinalIgnoreCase);

    public GameExitTracker(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    /// 시작 이벤트는 폴링 간격보다 짧은 게임 실행도 종료 추적에 넣습니다 (자동 모드 한 판·디스크 양보 해제용). Poll 이 한 번도 보지 못한 실행은 Poll 의 unseen 으로 나옵니다.
    public void ObserveStarted(GameSettings game)
    {
        _ = _running.Add(game.Name);
        _ = _goneSince.Remove(game.Name);
    }

    /// names 는 지금 떠 있는 프로세스 이미지 이름. 이번 호출에서 끝난 게임을 돌려줍니다 (설정에서 빠진 게임은 잊음). unseen 을 주면 그중 Poll 이 한 번도 보지 못한 짧은 실행을 거기에도 모읍니다.
    /// 설치 폴더 아래 미등록 프로세스는 보지 않습니다: 등록 이름만 모두 사라지고 Grace 가 지나야 종료입니다.
    public IReadOnlyList<GameSettings> Poll(IReadOnlyList<GameSettings> games, IReadOnlySet<string> names, List<GameSettings>? unseen = null)
    {
        List<GameSettings> exited = [];
        DateTimeOffset now = _now();
        foreach (GameSettings g in games)
        {
            if (g.ExeNames.Any(names.Contains))
            {
                _ = _running.Add(g.Name);
                _ = _goneSince.Remove(g.Name);
                _ = _seen.Add(g.Name);
            }
            else if (_running.Contains(g.Name))
            {
                if (!_goneSince.TryAdd(g.Name, now))
                {
                    if (now - _goneSince[g.Name] >= Grace)
                    {
                        _ = _running.Remove(g.Name);
                        _ = _goneSince.Remove(g.Name);
                        if (!_seen.Remove(g.Name))
                        {
                            unseen?.Add(g);
                        }

                        exited.Add(g);
                    }
                }
            }
        }

        _running.IntersectWith(games.Select(g => g.Name));
        _seen.IntersectWith(games.Select(g => g.Name));
        foreach (string gone in _goneSince.Keys.Where(k => !_running.Contains(k)).ToList())
        {
            _ = _goneSince.Remove(gone);
        }

        return exited;
    }
}
