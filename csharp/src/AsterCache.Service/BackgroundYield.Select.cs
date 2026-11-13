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

            found.Add(new YieldTarget(pid, exe, info.Path, info.Created, info.WorkingSetBytes));
        }

        return found;
    }

    // 원래 값이 보통 (1 보다 큼) 인 대상만 매우 낮음 (1) 으로 낮춥니다. 비정상 종료에도 되돌릴 수 있게 값을 바꾸기 전에 원래 값을 상태 파일에 먼저 적고, 적지 못하면 이번에는 낮추지 않습니다. 새로 낮춘 수를 돌려줍니다.
    private int Lower(IEnumerable<YieldTarget> targets)
    {
        var plan = new List<LoweredProcess>();
        foreach (YieldTarget t in targets)
        {
            if (_lowered.TryGetValue(t.Pid, out LoweredProcess known) && known.Created == t.Created)
            {
                continue;
            }

            if (env.MemoryPriority(t.Pid) is { } original && original > ProcessMemory.PriorityVeryLow)
            {
                plan.Add(new LoweredProcess(t.Pid, t.Exe, t.Path, t.Created, original));
            }
        }

        if (plan.Count == 0)
        {
            return 0;
        }

        foreach (LoweredProcess p in plan)
        {
            _lowered[p.Pid] = p;
        }

        if (!SaveState())
        {
            foreach (LoweredProcess p in plan)
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
}
