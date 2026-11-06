using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Volume;

namespace AsterCache.Core.Apply;

/// 부분 적용된 계획을 잔여만 다시 계획해 활성 계획으로 바꾸는 공통 절차 (서비스·CLI 가 같이 씀):
/// 중단된 교체를 먼저 끝내고 → 이전 목표 (저널 planned 행 + 옛 계획 + 읽히는 현재 활성 계획) 를 모아 → 잔여 계획을 만들고 → PlanSwitch.Commit 으로 저널 보관과 승격.
/// 저널을 읽지 못하면 예외가 그대로 올라갑니다 (이전 목표를 모른 채 계획하면 이미 옮긴 데이터를 다시 옮김): 호출자는 재시도 대기로 남길 것.
/// 미검증 SHA 중 원본이 바뀐 파일은 거절하지 않고 보류합니다: 후보 Order 에서 빼 나머지 파일만 복구하고, 결과 계획 stats.held_files[경로] = "source_changed" 로 남깁니다.
/// 호출자는 DataLock 으로 예약 조회부터 이 호출과 적용까지를 직렬화해야 합니다.
public static class ResidualReplan
{
    private const int MaxGenerationBumps = 8;

    // 저널 header 의 plan_sha (저널이 없거나 header 가 없으면 null). 읽지 못하면 예외가 그대로 올라감 (PreviousTargets 와 같은 재시도 처리).
    private static string? JournalPlanSha(string journalPath)
    {
        if (!File.Exists(journalPath))
        {
            return null;
        }

        using var jr = new Journal(journalPath);
        JObject? header = jr.Rows.FirstOrDefault(r => Equals(r["type"], "header"));
        return header is not null && header.TryGetValue("plan_sha", out object? v) ? v as string : null;
    }

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
            catch (PyValueException)
            {
                // 손상된 활성 계획은 이전 목표의 힌트에서만 빠짐 (저널이 기준이므로 데이터는 잃지 않고, 아래 저널 SHA 대조가 세대를 보장).
                // 있는데 읽지 못한 경우 (IOException·UnauthorizedAccessException: 잠김 등) 는 그대로 올려 호출자가 일시 오류로 다시 시도.
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

        long prevGen = olds.Append(candidate).Max(p => p.Stats.TryGetValue(ResidualPlanner.GenerationKey, out object? gv) && gv is long g ? g : 0L); // 알려진 계획 중 가장 높은 세대 (결정적)
        var candidateStats = new JObject();
        foreach ((string key, object? value) in candidate.Stats)
        {
            candidateStats.Add(key, value);
        }

        candidateStats[ResidualPlanner.GenerationKey] = prevGen;
        candidate = candidate with { Stats = candidateStats };
        // 잔여는 이미 옮긴 조각 바로 뒤의 빈 공간을 먼저 쓰고 (분산 방지), 없으면 예약 제외 빈 구간 중 낮은 LCN
        PlanFile built = ResidualPlanner.Build(candidate, previous, hooks.Extents,
            need => vol.Bitmap().FindFreeRun(need, preferLowLcn: true, align: PlanGuards.ZoneAlign, excluded: reserved),
            (need, ends) =>
            {
                VolumeBitmap bm = vol.Bitmap();
                return ends.Where(e => bm.RangeFree(e, need) && !(reserved ?? []).Any(r => r.Lcn < e + need && e < r.Lcn + r.Clusters)).Select(e => (long?)e).FirstOrDefault();
            });
        // 최종 방어선: 이전 계획 파일을 못 읽어 세대를 모르더라도 새 계획의 SHA 가 현재 저널의 계획 SHA 와 같을 수 없게 세대를 올림 (같으면 옛 검증 표본을 재사용함)
        string? journalSha = JournalPlanSha(journalPath);
        for (int bumps = 0; journalSha is not null && built.Sha() == journalSha; bumps++)
        {
            if (bumps >= MaxGenerationBumps)
            {
                throw new PlanRejectedException("residual generation could not be made different from the journal plan");
            }

            built.Stats[ResidualPlanner.GenerationKey] = (built.Stats[ResidualPlanner.GenerationKey] as long? ?? 0L) + 1;
        }

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
