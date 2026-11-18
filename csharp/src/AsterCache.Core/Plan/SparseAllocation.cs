using AsterCache.Core.Model;

namespace AsterCache.Core.Plan;

public readonly record struct AllocatedRange(long Vcn, long Clusters);

public static class SparseAllocation
{
    public const long MoveClusters = 16;

    /// 서비스가 만든 계획 stats 의 표지 키: 스파스 파일의 할당 기준값을 기록하는 규칙으로 만든 계획. 표지가 없는 이전 형식 계획은 스파스 파일을 옮기지 못하므로, 서비스가 한 번 기준값을 더해 잔여 재계획으로 바꿉니다 (WithMissingBaselines).
    public const string RuleKey = "sparse_rule";

    public const long RuleVersion = 1;

    public static IReadOnlyList<AllocatedRange> Normalize(IReadOnlyList<Extent> extents)
    {
        var ranges = extents.Where(e => e.Clusters > 0).Select(e => (Start: e.Vcn, End: e.Vcn + e.Clusters))
            .OrderBy(r => r.Start).ThenBy(r => r.End).ToList();
        var result = new List<AllocatedRange>();
        foreach ((long start, long end) in ranges)
        {
            if (result.Count > 0 && start <= result[^1].Vcn + result[^1].Clusters)
            {
                AllocatedRange prior = result[^1];
                result[^1] = prior with { Clusters = Math.Max(prior.Vcn + prior.Clusters, end) - prior.Vcn };
            }
            else
            {
                result.Add(new AllocatedRange(start, end - start));
            }
        }

        return result;
    }

    /// 할당 기준값이 없는 이전 형식 계획의 사본에, Order 에 든 스파스 파일의 지금 할당 구간을 기준값으로 더하고 RuleKey 표지를 붙인 후보 (작업·지문·Order 는 그대로).
    /// 할당 구간을 읽지 못하거나 (null) 디스크 구간이 없는 파일은 더하지 않고, 이미 기준값이 있는 파일은 건드리지 않습니다. 더할 파일이 없으면 null.
    public static PlanFile? WithMissingBaselines(PlanFile plan, Func<string, bool> isSparse, Func<string, IReadOnlyList<Extent>?> extents)
    {
        var added = new Dictionary<string, IReadOnlyList<AllocatedRange>>(plan.SparseAllocations, StringComparer.Ordinal);
        foreach (string path in plan.Order.Select(o => o.Path).Distinct(StringComparer.Ordinal).Where(p => !added.ContainsKey(p) && isSparse(p)))
        {
            if (extents(path) is { Count: > 0 } e)
            {
                added[path] = Normalize(e);
            }
        }

        if (added.Count == plan.SparseAllocations.Count)
        {
            return null;
        }

        var stats = new Json.JObject();
        foreach ((string key, object? value) in plan.Stats)
        {
            stats.Add(key, value);
        }

        stats[RuleKey] = RuleVersion;
        return plan with { SparseAllocations = added, Stats = stats };
    }

    public static bool Covers(IReadOnlyList<Extent>? extents, long vcn, long clusters)
    {
        if (extents is null || clusters <= 0)
        {
            return false;
        }

        long end = checked(vcn + clusters);
        long cursor = vcn;
        foreach (AllocatedRange range in Normalize(extents))
        {
            if (range.Vcn + range.Clusters <= cursor)
            {
                continue;
            }

            if (range.Vcn > cursor)
            {
                return false;
            }

            cursor = Math.Min(end, range.Vcn + range.Clusters);
            if (cursor == end)
            {
                return true;
            }
        }

        return false;
    }

    public static bool SupportsMove(long vcn, long clusters, long fileClusters)
    {
        if (vcn < 0 || clusters <= 0 || vcn % MoveClusters != 0)
        {
            return false;
        }

        if (clusters % MoveClusters == 0)
        {
            return true;
        }

        return clusters < MoveClusters && vcn + clusters == fileClusters;
    }

    public static long? TailStart(long fileClusters)
    {
        long remainder = fileClusters % MoveClusters;
        return remainder == 0 ? null : fileClusters - remainder;
    }
}
