using AsterCache.Windows;

namespace AsterCache.Service;

/// BackgroundYield 의 쓰고 있는 프로세스의 메모리 우선순위 (설정 memory.background_in_use_memory_priority).
/// 쓰고 있는 프로세스는 작업 집합을 비우지도 매우 낮음 (1) 으로 낮추지도 않고, 보통보다 낮음 (4) 으로만 낮춥니다. 서비스는 그 프로세스의 작업 집합을 비우지 않습니다.
/// 다만 메모리가 모자랄 때 Windows 는 우선순위가 낮은 페이지를 작업 집합에서 먼저 잘라내므로 (MEMORY_PRIORITY_INFORMATION 문서), 그 프로세스의 페이지가 예열한 게임 캐시 (서비스가 만진 페이지, 우선순위 5) 보다 먼저 잘려 재사용될 수 있고,
/// 그 프로세스가 그 페이지를 다시 쓰면 디스크에서 다시 읽느라 느려질 수 있습니다. 메모리가 넉넉하면 달라지는 것이 없습니다.
/// 원래 값이 4 이하인 프로세스는 건드리지 않고, 되돌릴 값은 기존 상태 파일의 원래 값 그대로입니다.
public sealed partial class BackgroundYield
{
    private bool InUseMemoryEnabled => Enabled && settings().Memory.BackgroundInUseMemoryPriority;

    // 메모리 우선순위로 낮출 값: 쉬는 프로세스는 매우 낮음 (1), 쓰고 있는 프로세스는 설정이 켜져 있을 때만 보통보다 낮음 (4), 아니면 낮추지 않음.
    private int MemoryGoal(int pid) =>
        !_usage.IsInUse(pid) ? ProcessMemory.PriorityVeryLow : InUseMemoryEnabled ? ProcessMemory.PriorityBelowNormal : NotLowered;

    // 쓰기 직전에 지금 그 번호를 쓰는 프로세스가 고른 때와 같은 프로세스인지 확인합니다 (Measure·Restore 와 같은 확인): 지금 목록의 이름, 세션 0 이 아님, 조회로 지금 읽은 생성 시각.
    // 번호를 같은 이름의 다른 프로세스가 이어받았으면 거짓이라, 고른 적 없는 프로세스 (경로로만 걸러지는 보호 대상일 수 있음) 에 쓰지 않습니다.
    private bool IsSameProcess(int pid, string exe, long created, Dictionary<int, string> names) =>
        names.TryGetValue(pid, out string? current) && string.Equals(current, exe, StringComparison.OrdinalIgnoreCase)
        && env.SessionId(pid) is not (null or 0) && env.Describe(pid) is { } info && info.Created == created;

    // 유지 회차에서: 이미 낮춘 프로세스가 쓰고 있다가 쉬게 됐거나 그 반대면 설정해 둔 값을 바꿉니다. 쓰기 전에 같은 프로세스인지 확인하고, 아니면 대상과 기록에서 뺍니다 (되돌릴 프로세스가 아님).
    // 쓰고 있는데 원래 값이 4 이하였던 프로세스는 원래 값으로 둡니다 (원래보다 높이지 않음). 설정이 세션 중 꺼졌으면 쓰고 있는 프로세스의 값은 그대로 두고 세션 끝에 되돌림.
    private void ReclassifyInUse(IReadOnlyList<(int Pid, string Exe)> processes, YieldTally tally)
    {
        Dictionary<int, string>? names = null;
        bool dropped = false;
        foreach (LoweredProcess p in _lowered.Values.Where(p => p.Priority != NotLowered && !p.Inherited).ToList())
        {
            if (!_targets.TryGetValue(p.Pid, out YieldTarget t) || t.Created != p.Created)
            {
                continue;
            }

            int goal = !_usage.IsInUse(p.Pid) ? ProcessMemory.PriorityVeryLow : InUseMemoryEnabled ? Math.Min(ProcessMemory.PriorityBelowNormal, p.Priority) : p.Applied;
            if (goal == p.Applied)
            {
                continue;
            }

            names ??= NamesOf(processes);
            if (!IsSameProcess(p.Pid, t.Exe, p.Created, names))
            {
                _ = _targets.Remove(p.Pid);
                _ = _lowered.Remove(p.Pid);
                dropped = true;
                continue;
            }

            if (env.SetMemoryPriority(p.Pid, goal))
            {
                _lowered[p.Pid] = p with { Applied = goal };
                tally.Switched++;
            }
            else
            {
                tally.Failed++;
            }
        }

        if (dropped)
        {
            _ = SaveState();
        }
    }

    // 빠른 경로가 고른 프로세스 가운데 그때 쓰고 있는 것으로 보여 (사용 기록에 없는 새 프로세스) 메모리 쪽을 건너뛴 것은, 2분 유지에서 쉬는 것으로 판정되면 그때 낮춥니다 (유지가 처음 고르는 프로세스와 같은 결과).
    // Lower 와 같은 순서 (원래 값을 상태 파일에 먼저 적고 낮춤) 이고, 쓰기 전에 같은 프로세스인지 확인합니다. 아직 쓰고 있으면 다음 유지로 넘깁니다.
    private void LowerDeferredMemory(IReadOnlyList<(int Pid, string Exe)> processes, YieldTally tally)
    {
        if (_deferred.Count == 0)
        {
            return;
        }

        Dictionary<int, string> names = NamesOf(processes);
        long at = env.Now().ToFileTime();
        var plan = new List<LoweredProcess>();
        foreach ((int pid, long created) in _deferred.ToList())
        {
            if (!_targets.TryGetValue(pid, out YieldTarget t) || t.Created != created || t.InheritOnly)
            {
                _ = _deferred.Remove(pid);
                continue;
            }

            int goal = MemoryGoal(pid);
            if (goal == NotLowered)
            {
                continue; // 아직 쓰고 있음: 다음 유지에서 다시 판정
            }

            _ = _deferred.Remove(pid);
            if (!IsSameProcess(pid, t.Exe, created, names))
            {
                _ = _targets.Remove(pid);
                continue;
            }

            bool known = _lowered.TryGetValue(pid, out LoweredProcess entry) && entry.Created == created;
            if ((known && entry.Priority != NotLowered) || env.MemoryPriority(pid) is not { } m || m <= goal)
            {
                continue;
            }

            plan.Add(known ? entry with { Priority = m, Applied = goal } : new LoweredProcess(pid, t.Exe, t.Path, created, m, at, Applied: goal));
        }

        foreach (LoweredProcess p in plan)
        {
            _lowered[p.Pid] = p;
        }

        if (plan.Count == 0)
        {
            return;
        }

        if (!SaveState())
        {
            foreach (LoweredProcess p in plan)
            {
                DropParts(p.Pid, true, false, false); // 복구 근거를 남기지 못하면 낮추지 않음
            }

            tally.Failed += plan.Count;
            return;
        }

        bool failed = false;
        foreach (LoweredProcess p in plan)
        {
            if (env.SetMemoryPriority(p.Pid, p.Applied))
            {
                tally.Memory += p.Applied == ProcessMemory.PriorityVeryLow ? 1 : 0;
                tally.InUse += p.Applied == ProcessMemory.PriorityVeryLow ? 0 : 1;
                _ = Interlocked.Increment(ref _appliedTotal);
            }
            else
            {
                failed = true;
                tally.Failed++;
                DropParts(p.Pid, true, false, false);
            }
        }

        if (failed)
        {
            _ = SaveState();
        }
    }

    // 끝났거나 다른 이름의 프로세스가 이어받은 번호의 기록을 치웁니다 (이름만 보고 프로세스는 열지 않음): 되돌릴 수 없는 항목이라 상태 파일이 세션 내내 커지지 않게.
    // 같은 이름으로 이어받은 번호는 쓰기 전의 확인이 거릅니다. 파일은 다음에 쓸 때 (새 항목이 있을 때) 함께 줄어들고, 치우기만으로는 쓰지 않습니다.
    private void PruneEnded(Dictionary<int, string> names)
    {
        foreach (LoweredProcess e in _lowered.Values.Where(e => e.Exe is not null && !(names.TryGetValue(e.Pid, out string? current) && string.Equals(current, e.Exe, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _ = _lowered.Remove(e.Pid);
        }
    }
}
