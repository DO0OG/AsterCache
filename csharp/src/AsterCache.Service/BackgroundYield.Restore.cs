using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// BackgroundYield 의 되돌리기·다시 거르기·측정: 이미 기억한 대상과 낮춘 프로세스를 다룹니다.
public sealed partial class BackgroundYield
{
    // 같은 프로세스 (pid 와 생성 시각이 같음) 일 때만 원래 값으로 되돌립니다. 열기 전에 현재 프로세스 목록에서 pid 의 이름을 확인해
    // 없거나 (끝남), 기록한 이름과 다르거나, 기록이 없는 옛 항목이 이름 제외 규칙에 걸리거나, 세션 0·모름이면 열지 않고 버립니다 (pid 재사용).
    // 같은 프로세스인데 쓰기에 실패한 항목만 kept 에 담아 다음에 다시 시도하게 합니다. 되돌린 수를 돌려줍니다.
    private int Restore(IEnumerable<LoweredProcess> entries, IReadOnlyList<(int Pid, string Exe)> processes, List<LoweredProcess> kept)
    {
        Dictionary<int, string> names = NamesOf(processes);
        HashSet<string> gameExes = BackgroundProcessRules.GameExes(settings().Games);
        int restored = 0;
        foreach (LoweredProcess p in entries)
        {
            if (!names.TryGetValue(p.Pid, out string? current)
                || (p.Exe is null ? BackgroundProcessRules.ExcludedByName(current, p.Pid, env.SelfPid, gameExes) : !string.Equals(current, p.Exe, StringComparison.OrdinalIgnoreCase))
                || env.SessionId(p.Pid) is null or 0)
            {
                continue;
            }

            if (env.Describe(p.Pid) is not { } info || info.Created != p.Created)
            {
                continue;
            }

            if (env.SetMemoryPriority(p.Pid, p.Priority))
            {
                restored++;
            }
            else
            {
                kept.Add(p);
            }
        }

        return restored;
    }

    private static Dictionary<int, string> NamesOf(IReadOnlyList<(int Pid, string Exe)> processes)
    {
        var names = new Dictionary<int, string>(processes.Count);
        foreach ((int pid, string exe) in processes)
        {
            names[pid] = exe;
        }

        return names;
    }

    // 이미 기억한 대상과 낮춘 프로세스를 현재 설정으로 다시 거릅니다 (세션 중 게임이 등록되거나 설정이 바뀔 수 있음).
    // 걸리는 것은 대상에서 빼고, 낮춘 상태면 그 자리에서 원래 값으로 되돌린 뒤 기억에서 지웁니다 (되돌리기에 실패한 것만 남김).
    private void Revalidate(IReadOnlyList<(int Pid, string Exe)> processes)
    {
        IReadOnlyList<GameSettings> games = settings().Games;
        HashSet<string> gameExes = BackgroundProcessRules.GameExes(games);
        IReadOnlyList<string> knownFolders = env.KnownGameFolders(); // 항목마다 만들지 않고 한 번만
        foreach (YieldTarget t in _targets.Values.Where(t => ExcludedNow(t.Pid, t.Exe, t.Path, games, gameExes, knownFolders)).ToList())
        {
            _ = _targets.Remove(t.Pid);
        }

        List<LoweredProcess> released = [.. _lowered.Values.Where(l => l.Exe is not null && ExcludedNow(l.Pid, l.Exe, l.Path, games, gameExes, knownFolders))];
        if (released.Count == 0)
        {
            return;
        }

        var kept = new List<LoweredProcess>();
        _ = Restore(released, processes, kept);
        foreach (LoweredProcess r in released)
        {
            _ = _lowered.Remove(r.Pid);
        }

        foreach (LoweredProcess k in kept)
        {
            _lowered[k.Pid] = k;
        }

        _ = SaveState();
    }

    private bool ExcludedNow(int pid, string exe, string? path, IReadOnlyList<GameSettings> games, IReadOnlySet<string> gameExes, IReadOnlyList<string> knownFolders) =>
        BackgroundProcessRules.ExcludedByName(exe, pid, env.SelfPid, gameExes)
        || (path is not null && BackgroundProcessRules.ExcludedByPath(path, env.WindowsDir, games, knownFolders));

    // 대상들의 작업 집합을 다시 읽어 갱신합니다. 열기 전에 현재 목록의 이름·세션을 확인해, 끝났거나 이름이 다르거나 세션 0 인 pid 와 생성 시각이 달라진 것은 대상에서 뺍니다.
    private List<YieldTarget> Measure(IReadOnlyList<(int Pid, string Exe)> processes)
    {
        Dictionary<int, string> names = NamesOf(processes);
        var result = new List<YieldTarget>();
        foreach (YieldTarget t in _targets.Values.ToList())
        {
            if (names.TryGetValue(t.Pid, out string? current) && string.Equals(current, t.Exe, StringComparison.OrdinalIgnoreCase) && env.SessionId(t.Pid) is not (null or 0)
                && env.Describe(t.Pid) is { } info && info.Created == t.Created)
            {
                YieldTarget updated = t with { WorkingSetBytes = info.WorkingSetBytes };
                _targets[t.Pid] = updated;
                result.Add(updated);
            }
            else
            {
                _ = _targets.Remove(t.Pid);
            }
        }

        return result;
    }
}
