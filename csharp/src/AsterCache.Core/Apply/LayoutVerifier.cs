using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;

namespace AsterCache.Core.Apply;

/// verify_layout 결과. CLI 는 ToJson 에 verdict 를 덧붙입니다.
public sealed record LayoutReport(
    long HotClusters, long OrderedRuns, long ExpectedRuns, long CurrentRunsAtPlanTime, bool Ascending, long? InZoneClusters,
    long MissingFiles, bool Contiguous)
{
    public JObject ToJson() => new()
    {
        { "hot_clusters", HotClusters },
        { "ordered_runs", OrderedRuns },
        { "expected_runs", ExpectedRuns },
        { "current_runs_at_plan_time", CurrentRunsAtPlanTime },
        { "ascending", Ascending },
        { "in_zone_clusters", InZoneClusters },
        { "missing_files", MissingFiles },
        { "contiguous", Contiguous },
    };
}

/// relayout/apply.py 의 verify_layout: 핫 데이터를 계획 순서로 읽을 때 물리적으로 이어지는가 (계획이 약속한 구간 수 이하인가).
public static class LayoutVerifier
{
    public static LayoutReport Verify(PlanFile plan, Func<string, IReadOnlyList<Extent>?> extents)
    {
        var ext = new Dictionary<string, IReadOnlyList<Extent>?>(StringComparer.Ordinal);
        var seq = new List<(long Lcn, long N)>();
        long missing = 0;
        foreach (PlanRange o in plan.Order)
        {
            if (!ext.TryGetValue(o.Path, out IReadOnlyList<Extent>? e))
            {
                ext[o.Path] = e = extents(o.Path);
            }

            if (e is null || e.Count == 0)
            {
                missing++;
                continue;
            }

            seq.AddRange(ZoneAllocator.Pieces(e, o.Vcn, o.Vcn + o.Clusters).Select(x => (x.Lcn, x.N)));
        }

        long runs = ZoneAllocator.CountRuns(seq);
        long expected = (long)plan.Stats["expected_runs"]!;
        long? inZone = null;
        if (plan.Zone.StartLcn is { } start)
        {
            long end = start + plan.Zone.Clusters;
            inZone = seq.Sum(x => Math.Max(0, Math.Min(x.Lcn + x.N, end) - Math.Max(x.Lcn, start)));
        }

        var starts = new List<long>();
        for (int i = 0; i < seq.Count; i++)
        {
            if (i == 0 || seq[i - 1].Lcn + seq[i - 1].N != seq[i].Lcn)
            {
                starts.Add(seq[i].Lcn);
            }
        }

        bool ascending = starts.Zip(starts.Skip(1), (a, b) => a <= b).All(x => x);
        return new LayoutReport(seq.Sum(x => x.N), runs, expected, (long)plan.Stats["current_runs"]!, ascending, inZone, missing,
            missing == 0 && runs <= expected);
    }
}
