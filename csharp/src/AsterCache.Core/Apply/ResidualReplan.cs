using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;

namespace AsterCache.Core.Apply;

/// 부분 적용된 계획을 잔여만 다시 계획해 활성 계획으로 바꾸는 공통 절차 (서비스·CLI 가 같이 씀):
/// 중단된 교체를 먼저 끝내고 → 이전 목표 (저널 planned 행 + 옛 계획 + 읽히는 현재 활성 계획) 를 모아 → 잔여 계획을 만들고 → PlanSwitch.Commit 으로 저널 보관과 승격.
/// 저널을 읽지 못하면 예외가 그대로 올라갑니다 (이전 목표를 모른 채 계획하면 이미 옮긴 데이터를 다시 옮김): 호출자는 재시도 대기로 남길 것.
/// 미검증 SHA 중 원본이 바뀐 파일은 거절하지 않고 보류합니다: 후보 Order 에서 빼 나머지 파일만 복구하고, 결과 계획 stats.held_files[경로] = "source_changed" 로 남깁니다.
/// 호출자는 DataLock 으로 예약 조회부터 이 호출과 적용까지를 직렬화해야 합니다.
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
        IReadOnlyList<string> held = Applier.VerifyPendingDetailed(journalPath, hooks, olds).SourceChanged;
        if (held.Count > 0)
        {
            var heldSet = held.ToHashSet(StringComparer.Ordinal);
            candidate = candidate with
            {
                Order = candidate.Order.Where(o => !heldSet.Contains(o.Path)).ToList(),
                Fingerprints = candidate.Fingerprints.Where(f => !heldSet.Contains(f.Path)).ToList(),
            };
        }

        PlanFile built = ResidualPlanner.Build(candidate, previous, hooks.Extents,
            need => vol.Bitmap().FindFreeRun(need, preferLowLcn: true, align: PlanGuards.ZoneAlign, excluded: reserved));
        if (held.Count > 0)
        {
            JObject heldFiles = built.Stats.TryGetValue("held_files", out object? hf) && hf is JObject existing ? existing : new JObject();
            foreach (string path in held.Where(h => !heldFiles.ContainsKey(h)))
            {
                heldFiles.Add(path, Applier.SourceChangedReason);
            }

            built.Stats["held_files"] = heldFiles;
        }

        return (built, PlanSwitch.Commit(built, planPath, journalPath, utcNow, savePlan, hooks, oldPlans: olds));
    }
}
