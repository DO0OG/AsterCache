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

            if (env.Describe(pid) is not { } info)
            {
                _ = _skipped.Add(pid);
                continue;
            }

            // Windows 폴더 규칙에만 걸리는 프로세스 (다른 제외 사유를 가를 수 없으면 아래 else 로 열지 않음) 는 낮춘 부모의 자식일 때 물려받은 값을 되돌리려고만 후보로 둡니다.
            bool windowsOnly = false;
            if (BackgroundProcessRules.ExcludedByPath(info.Path, env.WindowsDir, games, knownFolders))
            {
                if (BackgroundProcessRules.ExcludedByPath(info.Path, string.Empty, games, knownFolders))
                {
                    _ = _skipped.Add(pid);
                    continue;
                }

                windowsOnly = true;
            }

            found.Add(new YieldTarget(pid, exe, info.Path, info.Created, info.WorkingSetBytes, info.Parent, windowsOnly));
        }

        return found;
    }

    // 원래 값이 보통인 대상만 낮춥니다: 메모리 우선순위는 쓰고 있지 않은 프로세스에 한해 1 보다 크면 매우 낮음 (1) 으로, I/O 우선순위는 보통 (2) 이면 낮음 (1) 으로, CPU 등급은 보통이면 보통보다 낮음으로 (I/O·CPU 설정이 꺼져 있으면 읽지도 않음). 매우 낮음·낮음이던 것과 높은 값은 건드리지 않습니다 (CPU 는 보통보다 높음·높음·실시간·보통보다 낮음·유휴를 모두 그대로 둠).
    // 비정상 종료에도 되돌릴 수 있게 값을 바꾸기 전에 원래 값을 상태 파일에 먼저 적고, 적지 못하면 이번에는 낮추지 않습니다. 새로 낮춘 수를 (메모리, I/O, CPU) 로 돌려줍니다. 어느 쪽을 못 낮췄으면 그쪽 기록만 지웁니다.
    // 이미 낮은 대상은 낮춘 조상에게 물려받은 것이면 (Inherit) 조상의 원래 값으로 함께 기록합니다 (낮춘 수에는 세지 않음). inheritOnly 는 세션 끝에서 물려받은 것만 찾을 때입니다 (새로 낮추지 않음).
    private (int Memory, int Io, int Cpu) Lower(IEnumerable<YieldTarget> targets, IReadOnlyList<(int Pid, string Exe)> processes, bool inheritOnly = false)
    {
        long at = env.Now().ToFileTime();
        bool ioOn = IoEnabled;
        bool cpuOn = CpuEnabled && GameRunning(processes);
        var plan = new List<LoweredProcess>();
        var candidates = new List<YieldTarget>();
        foreach (YieldTarget t in targets)
        {
            if (_lowered.TryGetValue(t.Pid, out LoweredProcess known) && known.Created == t.Created)
            {
                continue;
            }

            candidates.Add(t); // 값은 부모가 기록에 있는 것만 읽음
            if (inheritOnly || t.InheritOnly)
            {
                continue;
            }

            int memory = !_usage.IsInUse(t.Pid) && env.MemoryPriority(t.Pid) is { } m && m > ProcessMemory.PriorityVeryLow ? m : NotLowered; // 쓰고 있는 프로세스는 메모리 우선순위를 낮추지 않음 (I/O 는 낮춤: 게임 디스크 경합에서 실측된 효과)
            int io = ioOn && env.IoPriority(t.Pid) is { } d && d == ProcessMemory.IoPriorityNormal ? d : NotLowered;
            int cpu = cpuOn && env.CpuClass(t.Pid) is ProcessMemory.CpuClassNormal ? ProcessMemory.CpuClassNormal : NotLowered;
            if (memory != NotLowered || io != NotLowered || cpu != NotLowered)
            {
                plan.Add(new LoweredProcess(t.Pid, t.Exe, t.Path, t.Created, memory, at, Io: io, Cpu: cpu));
            }
        }

        foreach (LoweredProcess p in plan)
        {
            _lowered[p.Pid] = p;
        }

        List<LoweredProcess> inherited = Inherit(candidates, NamesOf(processes));
        if (plan.Count == 0 && inherited.Count == 0)
        {
            return (0, 0, 0);
        }

        if (!SaveState())
        {
            foreach (LoweredProcess p in plan)
            {
                DropParts(p.Pid, p.Priority != NotLowered, p.Io != NotLowered, p.Cpu != NotLowered); // 복구 근거를 남기지 못하면 새로 낮추려던 것은 낮추지 않음 (비우기는 계속). 물려받은 항목은 이미 낮으므로 기록에 남겨 되돌림
            }

            return (0, 0, 0);
        }

        int lowered = 0;
        int ioLowered = 0;
        int cpuLowered = 0;
        bool failed = false;
        foreach (LoweredProcess p in plan)
        {
            bool memoryOk = p.Priority == NotLowered || env.SetMemoryPriority(p.Pid, ProcessMemory.PriorityVeryLow);
            bool ioOk = p.Io == NotLowered || env.SetIoPriority(p.Pid, ProcessMemory.IoPriorityLow);
            bool cpuOk = p.Cpu == NotLowered || env.SetCpuClass(p.Pid, ProcessMemory.CpuClassBelowNormal);
            lowered += p.Priority != NotLowered && memoryOk ? 1 : 0;
            ioLowered += p.Io != NotLowered && ioOk ? 1 : 0;
            cpuLowered += p.Cpu != NotLowered && cpuOk ? 1 : 0;
            if (!memoryOk || !ioOk || !cpuOk)
            {
                failed = true;
                DropParts(p.Pid, !memoryOk, !ioOk, !cpuOk); // 못 낮췄으면 되돌릴 것도 없음
            }
        }

        if (failed)
        {
            _ = SaveState();
        }

        return (lowered, ioLowered, cpuLowered);
    }

    private bool IsAnotherProcess(int pid, LoweredProcess recorded, Dictionary<int, string> names)
    {
        bool alive = names.TryGetValue(pid, out string? liveName);
        return _targets.TryGetValue(pid, out YieldTarget known)
            ? known.Created != recorded.Created || (alive && !string.Equals(known.Exe, liveName, StringComparison.OrdinalIgnoreCase))
            : alive;
    }

    // 기록에서 메모리·I/O·CPU 쪽을 지우고, 모두 없어지면 항목을 지웁니다 (물려받은 쪽은 그대로 남음).
    private void DropParts(int pid, bool memory, bool io, bool cpu)
    {
        if (!_lowered.TryGetValue(pid, out LoweredProcess e))
        {
            return;
        }

        e = e with { Priority = memory ? NotLowered : e.Priority, Io = io ? NotLowered : e.Io, Cpu = cpu ? NotLowered : e.Cpu };
        if (e.Priority == NotLowered && e.Io == NotLowered && e.Cpu == NotLowered)
        {
            _ = _lowered.Remove(pid);
        }
        else
        {
            _lowered[pid] = e;
        }
    }

    // 부모가 기록에 있고 (부모가 이미 끝났어도) 부모가 낮춰진 뒤에 만들어진 프로세스는 낮은 값을 물려받은 것입니다. 부모가 기록한 쪽 (메모리·I/O) 마다 값이 정말 낮은지 읽어 보고, 조상의 원래 값으로 기록하고, 그 자식도 같은 규칙으로 이어지게 더 늘지 않을 때까지 되풀이합니다.
    // Windows 폴더 규칙에만 걸린 프로세스 (InheritOnly) 도 후보이므로 낮춘 부모가 띄운 cmd·powershell 같은 자식과 그 아래 손자까지 이어서 되돌립니다.
    // 부모 pid 가 지금 다른 프로세스 (만든 시각이 다름) 가 쓰는 것이면 부모가 아닙니다. 원래부터 낮았던 프로세스는 건드리지 않습니다. 같은 프로세스를 이번에 직접 낮춘 기록이 있으면 (한쪽만 낮은 경우) 물려받은 쪽만 채웁니다.
    private List<LoweredProcess> Inherit(List<YieldTarget> candidates, Dictionary<int, string> names)
    {
        var found = new List<LoweredProcess>();
        for (bool grew = candidates.Count > 0; grew;)
        {
            grew = false;
            foreach (YieldTarget t in candidates.ToList())
            {
                // 부모 pid 가 지금 살아 있으면 대상 목록에 같은 생성 시각·같은 이름으로 있어야 부모입니다 (대상이 아닌 프로세스나 이름이 다른 프로세스가 끝난 부모의 pid 를 다시 쓴 경우를 가름, 열지 않고 이미 가진 목록만 봄).
                if (!_lowered.TryGetValue(t.Parent, out LoweredProcess parent) || t.Created <= parent.LoweredAt || IsAnotherProcess(t.Parent, parent, names))
                {
                    continue;
                }

                _ = candidates.Remove(t);
                int? childIo = parent.Io != NotLowered ? env.IoPriority(t.Pid) : null;
                bool background = childIo == 0; // 스스로 배경 모드에 들어간 자식 (I/O 매우 낮음, 메모리도 1) 은 물려받은 것이 아님
                int memory = parent.Priority != NotLowered && !background && env.MemoryPriority(t.Pid) == ProcessMemory.PriorityVeryLow ? parent.Priority : NotLowered;
                int io = childIo == ProcessMemory.IoPriorityLow ? parent.Io : NotLowered;
                int cpu = parent.Cpu != NotLowered && env.CpuClass(t.Pid) is ProcessMemory.CpuClassBelowNormal ? parent.Cpu : NotLowered; // 보통보다 낮음은 Windows 가 부모에게서 물려줌
                if (memory == NotLowered && io == NotLowered && cpu == NotLowered)
                {
                    continue;
                }

                LoweredProcess entry = _lowered.TryGetValue(t.Pid, out LoweredProcess own) && own.Created == t.Created
                    ? own with { Priority = own.Priority == NotLowered ? memory : own.Priority, Io = own.Io == NotLowered ? io : own.Io, Cpu = own.Cpu == NotLowered ? cpu : own.Cpu, LoweredAt = Math.Min(own.LoweredAt, t.Created) } // 물려받은 쪽은 만든 시각부터 낮았음
                    : new LoweredProcess(t.Pid, t.Exe, t.Path, t.Created, memory, t.Created, true, io, cpu);
                _lowered[t.Pid] = entry;
                found.Add(entry);
                grew = true;
            }
        }

        return found;
    }
}
