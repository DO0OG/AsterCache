using AsterCache.Windows;

namespace AsterCache.Service;

/// BackgroundYield 의 쓰고 있는 프로세스의 메모리 우선순위 (설정 memory.background_in_use_memory_priority).
/// 쓰고 있는 프로세스는 작업 집합을 비우지도 매우 낮음 (1) 으로 낮추지도 않고, 보통보다 낮음 (4) 으로만 낮춥니다: 그 프로세스가 쓰다 놓은 페이지가 예열해 둔 게임 캐시 (서비스가 만진 페이지, 우선순위 5) 보다 먼저 재사용되게 할 뿐 그 프로세스의 메모리는 건드리지 않습니다.
/// 원래 값이 4 이하인 프로세스는 건드리지 않고, 되돌릴 값은 기존 상태 파일의 원래 값 그대로입니다.
public sealed partial class BackgroundYield
{
    private bool InUseMemoryEnabled => Enabled && settings().Memory.BackgroundInUseMemoryPriority;

    // 메모리 우선순위로 낮출 값: 쉬는 프로세스는 매우 낮음 (1), 쓰고 있는 프로세스는 설정이 켜져 있을 때만 보통보다 낮음 (4), 아니면 낮추지 않음.
    private int MemoryGoal(int pid) =>
        !_usage.IsInUse(pid) ? ProcessMemory.PriorityVeryLow : InUseMemoryEnabled ? ProcessMemory.PriorityBelowNormal : NotLowered;

    // 유지 회차에서: 이미 낮춘 프로세스가 쓰고 있다가 쉬게 됐거나 그 반대면 설정해 둔 값을 바꿉니다. 같은 프로세스로 확인된 대상만 (끝났거나 번호를 이어받은 프로세스는 열지 않음).
    // 쓰고 있는데 원래 값이 4 이하였던 프로세스는 원래 값으로 둡니다 (원래보다 높이지 않음). 설정이 세션 중 꺼졌으면 쓰고 있는 프로세스의 값은 그대로 두고 세션 끝에 되돌림.
    private void ReclassifyInUse(YieldTally tally)
    {
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
    }
}
