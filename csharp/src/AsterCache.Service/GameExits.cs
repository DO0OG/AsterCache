using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 게임 종료 감지: 등록된 ExeNames 가 살아 있으면 실행 중이며, 사라진 뒤 Grace 가 지나면 종료입니다.
public sealed class GameExitTracker
{
    /// 등록 이름이 모두 사라진 뒤 이 시간 안에 다시 뜨면 종료가 아님 (런처 -> 본체 전환 틈). 시작 스트림 쿨다운과 같은 값 재사용.
    public static readonly TimeSpan Grace = GameDispatcher.Cooldown;
    private readonly HashSet<string> _running = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _goneSince = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _exitSince = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seen = new(StringComparer.Ordinal); // Poll 이 한 번이라도 띄운 등록 게임 (시작 이벤트만 있던 짧은 실행과 구분)
    private static readonly IReadOnlyDictionary<string, DateTimeOffset> EmptyGoneSince = new Dictionary<string, DateTimeOffset>();
    private readonly object _sync = new();
    private readonly Func<DateTimeOffset> _now;

    /// 등록 이름이 모두 사라져 Grace 를 기다리는 (종료 유예 중) 게임 이름.
    public IReadOnlySet<string> GraceNames
    {
        get
        {
            lock (_sync)
            {
                return new HashSet<string>(_goneSince.Keys, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// 종료 유예 중인 게임과 소멸을 처음 본 시각 (종료 확정 시각이 아님). 유예가 없으면 빈 사전을 그대로 돌려줍니다.
    public IReadOnlyDictionary<string, DateTimeOffset> GoneSince
    {
        get
        {
            lock (_sync)
            {
                return _goneSince.Count == 0 ? EmptyGoneSince : new Dictionary<string, DateTimeOffset>(_goneSince, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// 읽기 전용: names 기준으로 등록 이름이 모두 사라진 실행 추적 게임 (except 제외). Poll 과 달리 상태를 바꾸지 않아 종료 판정은 틱에만 남습니다.
    public IReadOnlySet<string> GoneNames(IReadOnlyList<GameSettings> games, IReadOnlySet<string> names, string? except = null)
    {
        lock (_sync)
        {
            return new HashSet<string>(games.Where(g => _running.Contains(g.Name) && !string.Equals(g.Name, except, StringComparison.OrdinalIgnoreCase) && !g.ExeNames.Any(names.Contains)).Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlySet<string> RunningNames
    {
        get
        {
            lock (_sync)
            {
                return new HashSet<string>(_running, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    public GameExitTracker(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.UtcNow);

    /// 시작 이벤트는 폴링 간격보다 짧은 게임 실행도 종료 추적에 넣습니다 (자동 모드 한 판·디스크 양보 해제용). Poll 이 한 번도 보지 못한 실행은 Poll 의 unseen 으로 나옵니다.
    public void ObserveStarted(GameSettings game)
    {
        lock (_sync)
        {
            _ = _running.Add(game.Name);
            _ = _goneSince.Remove(game.Name);
            _ = _exitSince.Remove(game.Name);
        }
    }

    /// 같은 물리 디스크의 종료 후 작업이 시작되기까지 기다릴 시간입니다.
    public static TimeSpan HeavyWorkGrace(int rewarmAfterExitMin) =>
        TimeSpan.FromTicks(Math.Max(Grace.Ticks, TimeSpan.FromMinutes(Math.Max(0, rewarmAfterExitMin)).Ticks));

    /// 게임 종료를 확인한 시각부터 종료 후 작업을 허용할 시각입니다.
    public static DateTimeOffset HeavyWorkNotBeforeAt(DateTimeOffset exitedAt, int rewarmAfterExitMin) =>
        exitedAt + HeavyWorkGrace(rewarmAfterExitMin);

    /// Poll 에서 추적한 게임 하나가 사라진 뒤 무거운 작업을 시작할 수 있는 시각입니다.
    public DateTimeOffset? HeavyWorkNotBefore(string gameName, int rewarmAfterExitMin)
    {
        lock (_sync)
        {
            DateTimeOffset? exitedAt = _goneSince.TryGetValue(gameName, out DateTimeOffset pending) ? pending
                : _exitSince.TryGetValue(gameName, out DateTimeOffset exited) ? exited
                : null;
            return exitedAt is { } at ? HeavyWorkNotBeforeAt(at, rewarmAfterExitMin) : null;
        }
    }

    /// 대상과 같은 물리 디스크에서 가장 최근에 사라진 등록 게임의 유예가 끝날 시각입니다.
    public DateTimeOffset? HeavyWorkNotBefore(GameSettings target, IReadOnlyList<GameSettings> games,
        Func<string, string, bool> sameDisk, int rewarmAfterExitMin)
    {
        lock (_sync)
        {
            DateTimeOffset? latest = null;
            foreach (GameSettings game in games)
            {
                if (!sameDisk(target.OriginRoot, game.OriginRoot) || HeavyWorkNotBefore(game.Name, rewarmAfterExitMin) is not { } due)
                {
                    continue;
                }

                if (latest is null || due > latest)
                {
                    latest = due;
                }
            }

            return latest;
        }
    }

    public bool HeavyWorkAllowed(GameSettings target, IReadOnlyList<GameSettings> games,
        Func<string, string, bool> sameDisk, int rewarmAfterExitMin, DateTimeOffset now) =>
        HeavyWorkNotBefore(target, games, sameDisk, rewarmAfterExitMin) is not { } due || now >= due;

    /// names 는 지금 떠 있는 프로세스 이미지 이름. 이번 호출에서 끝난 게임을 돌려줍니다 (설정에서 빠진 게임은 잊음). unseen 을 주면 그중 Poll 이 한 번도 보지 못한 짧은 실행을 거기에도 모읍니다.
    /// 설치 폴더 아래 미등록 프로세스는 보지 않습니다: 등록 이름만 모두 사라지고 Grace 가 지나야 종료입니다.
    public IReadOnlyList<GameSettings> Poll(IReadOnlyList<GameSettings> games, IReadOnlySet<string> names, List<GameSettings>? unseen = null)
    {
        lock (_sync)
        {
            return PollCore(games, names, unseen);
        }
    }

    private IReadOnlyList<GameSettings> PollCore(IReadOnlyList<GameSettings> games, IReadOnlySet<string> names, List<GameSettings>? unseen)
    {
        List<GameSettings> exited = [];
        DateTimeOffset now = _now();
        foreach (GameSettings g in games)
        {
            if (g.ExeNames.Any(names.Contains))
            {
                _ = _running.Add(g.Name);
                _ = _goneSince.Remove(g.Name);
                _ = _exitSince.Remove(g.Name);
                _ = _seen.Add(g.Name);
            }
            else if (_running.Contains(g.Name))
            {
                if (!_goneSince.TryAdd(g.Name, now))
                {
                    if (now - _goneSince[g.Name] >= Grace)
                    {
                        _exitSince[g.Name] = _goneSince[g.Name];
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

        HashSet<string> configured = new(games.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
        foreach (string removed in _exitSince.Keys.Where(k => !configured.Contains(k)).ToList())
        {
            _ = _exitSince.Remove(removed);
        }

        return exited;
    }
}
