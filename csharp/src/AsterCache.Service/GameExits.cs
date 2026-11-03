using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 게임 종료 감지: 실행 중인 것을 본 게임의 ExeNames 가 모두 사라지면 종료로 봅니다. 감시기 종류 (ETW·폴링) 와 무관하게 Worker 의 주기 루프에서 한 스레드로 부릅니다.
public sealed class GameExitTracker
{
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);

    /// names 는 지금 떠 있는 프로세스 이미지 이름. 이번 호출에서 끝난 게임을 돌려줍니다 (설정에서 빠진 게임은 잊음).
    /// underRoot 는 그 게임 폴더 아래 프로세스가 남아 있는가. 이미 추적 중인 게임에만 물어서, 런처 등 무관한 프로세스로 추적이 새로 시작되지 않게 합니다.
    public IReadOnlyList<GameSettings> Poll(IReadOnlyList<GameSettings> games, IReadOnlySet<string> names, Func<GameSettings, bool>? underRoot = null)
    {
        List<GameSettings> exited = [];
        foreach (GameSettings g in games)
        {
            if (g.ExeNames.Any(names.Contains))
            {
                _ = _running.Add(g.Name);
            }
            else if (_running.Contains(g.Name) && underRoot?.Invoke(g) == true)
            {
                // 폴더 아래 프로세스가 남아 있으므로 아직 실행 중
            }
            else if (_running.Remove(g.Name))
            {
                exited.Add(g);
            }
        }

        _running.IntersectWith(games.Select(g => g.Name));
        return exited;
    }
}
