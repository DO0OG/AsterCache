using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;

namespace AsterCache.Core.Apply;

/// 부분 적용된 계획을 잔여만 다시 계획해 활성 계획으로 바꾸는 공통 절차 (서비스·CLI 가 같이 씀):
/// 중단된 교체를 먼저 끝내고 → 이전 목표 (저널 planned 행 + 옛 계획 + 읽히는 현재 활성 계획) 를 모아 → 잔여 계획을 만들고 → PlanSwitch.Commit 으로 저널 보관과 승격.
/// 저널을 읽지 못하면 예외가 그대로 올라갑니다 (이전 목표를 모른 채 계획하면 이미 옮긴 데이터를 다시 옮김): 호출자는 재시도 대기로 남길 것.
public static class ResidualReplan
{
    public static (PlanFile Plan, SwitchResult Switch) Run(PlanFile candidate, string planPath, string journalPath, IVolume vol, IReadOnlyList<(long Lcn, long Clusters)>? reserved,
        ApplyHooks hooks, Func<DateTime> utcNow, Action<PlanFile, string>? savePlan, IEnumerable<PlanFile> oldPlans)
    {
        _ = PlanSwitch.Resume(planPath, journalPath, savePlan);
        var olds = oldPlans.ToList();
        if (File.Exists(planPath))
        {
            try
            {
                olds.Add(PlanFile.Load(planPath));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
            {
                // 읽히지 않는 활성 계획은 이전 목표의 힌트에서만 빠짐 (저널이 기준이므로 데이터는 잃지 않음)
            }
        }

        IReadOnlyList<PlanTask> previous = ResidualPlanner.PreviousTargets(journalPath, olds);
        PlanFile built = ResidualPlanner.Build(candidate, previous, hooks.Extents,
            need => vol.Bitmap().FindFreeRun(need, preferLowLcn: true, align: PlanGuards.ZoneAlign, excluded: reserved));
        return (built, PlanSwitch.Commit(built, planPath, journalPath, utcNow, savePlan, hooks));
    }
}
