using AsterCache.Core.Json;
using AsterCache.Core.Plan;

namespace AsterCache.Core.Apply;

/// 같은 볼륨의 다른 게임이 아직 쓸 존 범위. 자동 존은 이 범위를 피해 고르고, 정한 존은 겹치면 거절됩니다 (ApplyOptions.Reserved).
public static class ZoneReservations
{
    /// plansDir 의 활성 계획 (*.plan.json, 존 시작이 정해진 것) 과 진행 중인 교체 기록 (*.plan.json.switch) 이 가리키는 후보 (.next) 의 task 목표 범위를 합쳐 시작순으로 돌려줍니다.
    /// exceptPlanPath (지금 계획하는 게임 자신) 의 계획과 교체 기록은 뺍니다. .stale / .boot / 보관 저널은 이름만으로는 예약하지 않으며, 읽을 수 없는 파일은 건너뜁니다.
    public static IReadOnlyList<(long Lcn, long Clusters)> Collect(string plansDir, string volume, string exceptPlanPath)
    {
        var found = new List<(long A, long B)>();
        if (!Directory.Exists(plansDir))
        {
            return [];
        }

        string except = Path.GetFullPath(exceptPlanPath);
        string vol = NormVolume(volume);
        const string planSuffix = ".plan.json";
        foreach (string f in Directory.EnumerateFiles(plansDir))
        {
            string full = Path.GetFullPath(f);
            if (full.EndsWith(planSuffix, StringComparison.OrdinalIgnoreCase) && !SamePath(full, except))
            {
                Add(found, full, vol);
            }
            else if (full.EndsWith(planSuffix + PlanSwitch.RecordSuffix, StringComparison.OrdinalIgnoreCase) && !SamePath(full[..^PlanSwitch.RecordSuffix.Length], except))
            {
                Add(found, full[..^PlanSwitch.RecordSuffix.Length] + PlanSwitch.NextSuffix, vol);
            }
        }

        found.Sort();
        var merged = new List<(long Lcn, long Clusters)>();
        long end = long.MinValue;
        foreach ((long a, long b) in found)
        {
            if (merged.Count > 0 && a <= end)
            {
                end = Math.Max(end, b);
                merged[^1] = (merged[^1].Lcn, end - merged[^1].Lcn);
            }
            else
            {
                merged.Add((a, b - a));
                end = b;
            }
        }

        return merged;
    }

    private static void Add(List<(long A, long B)> found, string planFile, string vol)
    {
        try
        {
            PlanFile plan = PlanFile.Load(planFile);
            if (plan.Zone.StartLcn is not null && NormVolume(plan.Volume) == vol)
            {
                found.AddRange(plan.Tasks.Where(t => t.Clusters > 0).Select(t => (t.TargetLcn, t.TargetLcn + t.Clusters)));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            // 쓰다 멈췄거나 읽을 수 없는 파일: 예약하지 않음 (예약 조회가 계획을 막으면 안 됨)
        }
    }

    private static string NormVolume(string v) => v.Trim().TrimEnd('\\', '/').ToUpperInvariant();

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
