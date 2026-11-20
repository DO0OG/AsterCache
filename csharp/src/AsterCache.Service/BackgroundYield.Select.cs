using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// BackgroundYield 의 대상 선택과 우선순위 낮추기.
public sealed partial class BackgroundYield
{
    // 순서가 중요: 이름·세션 단계는 프로세스를 열지 않고, 조회 권한 (0x1000) 으로 경로까지 확인한 것만 대상입니다. 제외 대상은 쓰기 권한으로 열지 않음.
    private List<YieldTarget> Select(IReadOnlyList<(int Pid, string Exe)> processes, Func<int, bool> skip)
    {
        IReadOnlyList<GameSettings> games = settings().Games;
        HashSet<string> gameExes = BackgroundProcessRules.GameExes(games);
        IReadOnlyList<string> knownFolders = env.KnownGameFolders();
        var found = new List<YieldTarget>();
        foreach ((int pid, string exe) in processes)
        {
            if (BackgroundProcessRules.ExcludedByName(exe, pid, env.SelfPid, gameExes) || skip(pid))
            {
                continue;
            }

            uint? session = env.SessionId(pid);
            if (session is null or 0)
            {
                continue;
            }

            if (env.Describe(pid) is not { } info || BackgroundProcessRules.ExcludedByPath(info.Path, env.WindowsDir, games, knownFolders))
            {
                _ = _skipped.Add(pid);
                continue;
            }

            found.Add(new YieldTarget(pid, exe, info.Path, info.Created, info.WorkingSetBytes, info.Parent));
        }

        return found;
    }

    // 원래 값이 보통 (1 보다 큼) 인 대상만 매우 낮음 (1) 으로 낮춥니다. 비정상 종료에도 되돌릴 수 있게 값을 바꾸기 전에 원래 값을 상태 파일에 먼저 적고, 적지 못하면 이번에는 낮추지 않습니다. 새로 낮춘 수를 돌려줍니다.
    // 이미 가장 낮은 값인 대상은 낮춘 조상에게 물려받은 것이면 (Inherit) 조상의 원래 값으로 함께 기록합니다 (낮춘 수에는 세지 않음). inheritOnly 는 세션 끝에서 물려받은 것만 찾을 때입니다 (새로 낮추지 않음).
    private int Lower(IEnumerable<YieldTarget> targets, bool inheritOnly = false)
    {
        long at = env.Now().ToFileTime();
        var plan = new List<LoweredProcess>();
        var lowest = new List<YieldTarget>();
        foreach (YieldTarget t in targets)
        {
            if (_lowered.TryGetValue(t.Pid, out LoweredProcess known) && known.Created == t.Created)
            {
                continue;
            }

            if (inheritOnly)
            {
                lowest.Add(t); // 값은 부모가 기록에 있는 것만 읽음
            }
            else if (env.MemoryPriority(t.Pid) is { } original)
            {
                if (original > ProcessMemory.PriorityVeryLow)
                {
                    plan.Add(new LoweredProcess(t.Pid, t.Exe, t.Path, t.Created, original, at));
                }
                else if (original == ProcessMemory.PriorityVeryLow)
                {
                    lowest.Add(t);
                }
            }
        }

        foreach (LoweredProcess p in plan)
        {
            _lowered[p.Pid] = p;
        }

        List<LoweredProcess> inherited = Inherit(lowest, inheritOnly);
        if (plan.Count == 0 && inherited.Count == 0)
        {
            return 0;
        }

        if (!SaveState())
        {
            foreach (LoweredProcess p in plan.Concat(inherited))
            {
                _ = _lowered.Remove(p.Pid); // 복구 근거를 남기지 못하면 낮추지 않음 (비우기는 계속)
            }

            return 0;
        }

        int lowered = 0;
        foreach (LoweredProcess p in plan)
        {
            if (env.SetMemoryPriority(p.Pid, ProcessMemory.PriorityVeryLow))
            {
                lowered++;
            }
            else
            {
                _ = _lowered.Remove(p.Pid); // 못 낮췄으면 되돌릴 것도 없음
            }
        }

        if (lowered < plan.Count)
        {
            _ = SaveState();
        }

        return lowered;
    }

    // 부모가 기록에 있고 (부모가 이미 끝났어도) 부모가 낮춰진 뒤에 만들어진 프로세스는 낮은 값을 물려받은 것입니다. 조상의 원래 값으로 기록하고, 그 자식도 같은 규칙으로 이어지게 더 늘지 않을 때까지 되풀이합니다.
    // 부모 pid 가 지금 다른 프로세스 (만든 시각이 다름) 가 쓰는 것이면 부모가 아닙니다. 원래부터 낮았던 프로세스는 건드리지 않습니다. checkPriority 면 기록 전에 값이 가장 낮은지 읽어 봅니다.
    private List<LoweredProcess> Inherit(List<YieldTarget> lowest, bool checkPriority)
    {
        var found = new List<LoweredProcess>();
        for (bool grew = lowest.Count > 0; grew;)
        {
            grew = false;
            foreach (YieldTarget t in lowest.ToList())
            {
                if (!_lowered.TryGetValue(t.Parent, out LoweredProcess parent) || t.Created <= parent.LoweredAt
                    || (_targets.TryGetValue(t.Parent, out YieldTarget current) && current.Created != parent.Created))
                {
                    continue;
                }

                _ = lowest.Remove(t);
                if (checkPriority && env.MemoryPriority(t.Pid) != ProcessMemory.PriorityVeryLow)
                {
                    continue;
                }

                var entry = new LoweredProcess(t.Pid, t.Exe, t.Path, t.Created, parent.Priority, t.Created, true);
                _lowered[t.Pid] = entry;
                found.Add(entry);
                grew = true;
            }
        }

        return found;
    }
}
