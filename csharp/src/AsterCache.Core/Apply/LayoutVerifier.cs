using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;

namespace AsterCache.Core.Apply;

/// verify_layout 결과. CLI 는 ToJson 에 verdict 를 덧붙입니다.
public sealed record LayoutReport(
    long HotClusters, long OrderedRuns, long ExpectedRuns, long CurrentRunsAtPlanTime, bool Ascending, long? InZoneClusters,
    long MissingFiles, bool Contiguous, long SparseAllocationChangedFiles = 0, long SparseHeldFiles = 0)
{
    /// 일부만 존에 들어간 부분 적용 (재적용이 도중에 멈췄거나 일부가 되돌려짐). 오류가 아니라 apply 를 다시 돌리면 이어가는 상태입니다.
    public bool ResumeNeeded => !Contiguous && MissingFiles == 0 && SparseAllocationChangedFiles == 0 && InZoneClusters is > 0 && InZoneClusters < HotClusters;

    public JObject ToJson()
    {
        var json = new JObject
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
        if (SparseAllocationChangedFiles > 0)
        {
            json.Add("sparse_allocation_changed_files", SparseAllocationChangedFiles);
        }

        return json;
    }
}

/// relayout/apply.py 의 verify_layout: 핫 데이터를 계획 순서로 읽을 때 물리적으로 이어지는가 (계획이 약속한 구간 수 이하인가).
public static class LayoutVerifier
{
    /// unmovable: 적용이 옮기지 않는 파일 (스파스·압축·암호화 …) 이면 true. 옮겨지지 않을 파일은 존 소속·구간 수 판정에서 뺌 (가변 파일과 같음).
    /// isSparse: 스파스 속성인가. 계획이 할당 기준값을 모르는 스파스 파일 (이전 형식 계획 등) 은 적용이 건너뛰므로 unmovable 과 같이 뺍니다.
    /// holdChangedSparse: 할당 구간이 기준값과 달라진 (또는 조회에 실패한) 스파스 파일도 적용이 건너뛰는 파일처럼 판정에서 뺍니다 (SparseHeldFiles 로 수만 보고, 연속 판정·SparseAllocationChangedFiles 에는 세지 않음).
    public static LayoutReport Verify(PlanFile plan, Func<string, IReadOnlyList<Extent>?> extents, Func<string, bool>? unmovable = null, Func<string, bool>? isSparse = null,
        bool holdChangedSparse = false)
    {
        if (WholeFileLayout.WholeFiles(plan) is { } wholeFiles)
        {
            return VerifyWholeFiles(plan, wholeFiles, extents, unmovable, isSparse, holdChangedSparse); // 통째 배치 계획: 놓기로 한 파일이 각각 한 조각으로 zone 안에 있는가
        }

        var skip = new Dictionary<string, bool>(StringComparer.Ordinal);
        var ext = new Dictionary<string, IReadOnlyList<Extent>?>(StringComparer.Ordinal);
        var seq = new List<(long Lcn, long N)>();
        long missing = 0;
        var missingSparse = new HashSet<string>(StringComparer.Ordinal);
        var sparseChanged = new HashSet<string>(StringComparer.Ordinal);
        var sparseHeld = new HashSet<string>(StringComparer.Ordinal);
        long leftOut = 0;
        bool lastUnmovable = false;
        foreach (PlanRange o in plan.Order)
        {
            if (VolatileFiles.Ignored(o.Path))
            {
                lastUnmovable = false;
                leftOut++; // 옛 계획 (excluded_files 키 없음) 에 든 가변 파일은 게임이 스스로 다시 써 존 밖으로 나가므로 존 소속·구간 수 판정에서 뺌
                continue;
            }

            if ((unmovable is not null || isSparse is not null) && (skip.TryGetValue(o.Path, out bool no) ? no
                : skip[o.Path] = unmovable?.Invoke(o.Path) == true || !plan.SparseAllocations.ContainsKey(o.Path) && isSparse?.Invoke(o.Path) == true))
            {
                // 적용이 건너뛰는 파일 (스파스 등) 은 영원히 제자리라 남은 이동으로 세면 이어 옮기기 필요가 풀리지 않음.
                // 연달아 빠진 범위 묶음은 이어진 구간을 한 번만 가르므로 여유도 묶음마다 1.
                if (!lastUnmovable)
                {
                    leftOut++;
                }

                lastUnmovable = true;
                continue;
            }

            lastUnmovable = false;

            if (!ext.TryGetValue(o.Path, out IReadOnlyList<Extent>? e))
            {
                ext[o.Path] = e = extents(o.Path);
            }

            if (holdChangedSparse && plan.SparseAllocations.TryGetValue(o.Path, out IReadOnlyList<AllocatedRange>? baseline)
                && (e is null || e.Count == 0 || !baseline.SequenceEqual(SparseAllocation.Normalize(e))))
            {
                _ = sparseHeld.Add(o.Path); // 할당이 바뀐 스파스 파일은 적용이 건너뛰므로 영원히 제자리: 옮길 수 없는 파일과 같이 뺌
                if (!lastUnmovable)
                {
                    leftOut++;
                }

                lastUnmovable = true;
                continue;
            }

            if (e is null || e.Count == 0)
            {
                missing++;
                if (plan.SparseAllocations.ContainsKey(o.Path))
                {
                    _ = sparseChanged.Add(o.Path);
                }

                continue;
            }

            if (plan.SparseAllocations.TryGetValue(o.Path, out IReadOnlyList<AllocatedRange>? expectedAllocation))
            {
                if (!expectedAllocation.SequenceEqual(SparseAllocation.Normalize(e)))
                {
                    _ = sparseChanged.Add(o.Path);
                }

                // 계획의 범위가 지금 할당 구간으로 모두 덮여야 함: 구간이 사라진 것을 정상 완료로 세지 않음
                if (!SparseAllocation.Covers(e, o.Vcn, o.Clusters) && missingSparse.Add(o.Path))
                {
                    missing++;
                }
            }

            seq.AddRange(ZoneAllocator.Pieces(e, o.Vcn, o.Vcn + o.Clusters).Select(x => (x.Lcn, x.N)));
        }

        long runs = ZoneAllocator.CountRuns(seq);
        long expected = (long)plan.Stats["expected_runs"]! + leftOut; // shortcut: 뺀 범위 하나가 이어진 구간을 둘로 가를 수 있어 그만큼 여유를 줌, 계획 시점에 가변 파일을 뺀 새 계획에는 해당 없음
        long? inZone = null;
        if (plan.Zone.StartLcn is { } start)
        {
            long end = start + plan.Zone.Clusters;
            inZone = plan.IsResidual ? InTargets(plan, seq) : seq.Sum(x => Math.Max(0, Math.Min(x.Lcn + x.N, end) - Math.Max(x.Lcn, start)));
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
            missing == 0 && sparseChanged.Count == 0 && runs <= expected, sparseChanged.Count, sparseHeld.Count);
    }

    // 통째 배치 계획의 판정: stats.whole_files 의 파일이 각각 한 조각이고 허용 구간 안에 있으면 연속. 제자리로 둔 파일은 흐트러져도 판정에서 뺌.
    // 가변·옮길 수 없음·스파스 기준값 없음·할당이 바뀐 스파스는 적용이 건너뛰므로 위 범위 판정과 같은 규칙으로 뺌 (수만 SparseHeldFiles 로 보고).
    // 허용 구간: 비잔여는 [zone 시작, 시작 + max(zone.clusters, zone_used_clusters)), 잔여는 그 파일 작업들의 목표 범위. zone 시작이 아직 없으면 한 조각이기만 하면 됨.
    private static LayoutReport VerifyWholeFiles(PlanFile plan, IReadOnlyList<string> wholeFiles, Func<string, IReadOnlyList<Extent>?> extents,
        Func<string, bool>? unmovable, Func<string, bool>? isSparse, bool holdChangedSparse)
    {
        long cb = plan.ClusterBytes;
        long used = plan.Stats.TryGetValue("zone_used_clusters", out object? u) && u is long usedClusters ? usedClusters : 0;
        long? zoneEnd = plan.Zone.StartLcn is { } zoneStart ? zoneStart + Math.Max(plan.Zone.Clusters, used) : null;
        Dictionary<string, List<(long A, long B)>>? targets = plan.IsResidual ? TargetRanges(plan) : null;
        var sizes = plan.Fingerprints.ToDictionary(f => f.Path, f => f.Size, StringComparer.Ordinal);
        var sparseChanged = new HashSet<string>(StringComparer.Ordinal);
        var sparseHeld = new HashSet<string>(StringComparer.Ordinal);
        var starts = new List<long>();
        long files = 0, hot = 0, inZone = 0, fragmented = 0, outside = 0, missing = 0;
        foreach (string path in wholeFiles)
        {
            if (VolatileFiles.Ignored(path)
                || ((unmovable is not null || isSparse is not null) && (unmovable?.Invoke(path) == true || !plan.SparseAllocations.ContainsKey(path) && isSparse?.Invoke(path) == true)))
            {
                continue;
            }

            IReadOnlyList<Extent>? e = extents(path);
            plan.SparseAllocations.TryGetValue(path, out IReadOnlyList<AllocatedRange>? baseline);
            if (holdChangedSparse && baseline is not null && (e is null || e.Count == 0 || !baseline.SequenceEqual(SparseAllocation.Normalize(e))))
            {
                _ = sparseHeld.Add(path);
                continue;
            }

            files++;
            if (e is null || e.Count == 0)
            {
                missing++; // 읽지 못함: 판정 불가 (호출자는 건강으로 봄)
                if (baseline is not null)
                {
                    _ = sparseChanged.Add(path);
                }

                continue;
            }

            if (baseline is not null && !baseline.SequenceEqual(SparseAllocation.Normalize(e)))
            {
                _ = sparseChanged.Add(path);
            }

            long fileClusters = sizes.TryGetValue(path, out long size) ? (size + cb - 1) / cb : e.Max(x => x.Vcn + x.Clusters);
            List<(long Lcn, long N)> pieces = ZoneAllocator.Pieces(e, 0, fileClusters).Select(x => (x.Lcn, x.N)).ToList();
            hot += fileClusters;
            if (pieces.Sum(x => x.N) != fileClusters || ZoneAllocator.CountRuns(pieces) != 1)
            {
                fragmented++;
                continue;
            }

            starts.Add(pieces[0].Lcn);
            if (targets is not null ? Within(targets, path, pieces[0].Lcn, fileClusters) : plan.Zone.StartLcn is not { } start || pieces[0].Lcn >= start && pieces[0].Lcn + fileClusters <= zoneEnd)
            {
                inZone += fileClusters;
            }
            else
            {
                outside++;
            }
        }

        bool ascending = starts.Zip(starts.Skip(1), (a, b) => a <= b).All(x => x);
        long currentRuns = plan.Stats.TryGetValue("current_runs", out object? c) && c is long runsAtPlan ? runsAtPlan : 0;
        return new LayoutReport(hot, fragmented, files, currentRuns, ascending, plan.Zone.StartLcn is null ? null : inZone, missing,
            missing == 0 && sparseChanged.Count == 0 && fragmented == 0 && outside == 0, sparseChanged.Count, sparseHeld.Count);
    }

    // 잔여 계획: 파일별 작업 목표 범위 (이어진 것은 합침).
    private static Dictionary<string, List<(long A, long B)>> TargetRanges(PlanFile plan)
    {
        var result = new Dictionary<string, List<(long A, long B)>>(StringComparer.Ordinal);
        foreach (IGrouping<string, PlanTask> file in plan.Tasks.Where(t => t.Clusters > 0).GroupBy(t => t.Path, StringComparer.Ordinal))
        {
            var merged = new List<(long A, long B)>();
            foreach (PlanTask t in file.OrderBy(t => t.TargetLcn))
            {
                if (merged.Count > 0 && t.TargetLcn <= merged[^1].B)
                {
                    merged[^1] = (merged[^1].A, Math.Max(merged[^1].B, t.TargetLcn + t.Clusters));
                }
                else
                {
                    merged.Add((t.TargetLcn, t.TargetLcn + t.Clusters));
                }
            }

            result[file.Key] = merged;
        }

        return result;
    }

    private static bool Within(Dictionary<string, List<(long A, long B)>> targets, string path, long lcn, long clusters) =>
        targets.TryGetValue(path, out List<(long A, long B)>? ranges) && ranges.Any(r => r.A <= lcn && lcn + clusters <= r.B);

    // 잔여 재계획: 존은 새 구간뿐이므로, 목표 자리 (task 의 목표 범위, 서로 겹치지 않음) 에 놓인 클러스터를 존 소속으로 셈.
    private static long InTargets(PlanFile plan, List<(long Lcn, long N)> seq)
    {
        List<PlanTask> targets = [.. plan.Tasks.Where(t => t.Clusters > 0).OrderBy(t => t.TargetLcn)];
        List<long> starts = [.. targets.Select(t => t.TargetLcn)];
        long hit = 0;
        foreach ((long lcn, long n) in seq)
        {
            int i = starts.BinarySearch(lcn);
            for (i = i < 0 ? Math.Max(0, ~i - 1) : i; i < targets.Count && targets[i].TargetLcn < lcn + n; i++)
            {
                hit += Math.Max(0, Math.Min(lcn + n, targets[i].TargetLcn + targets[i].Clusters) - Math.Max(lcn, targets[i].TargetLcn));
            }
        }

        return hit;
    }
}
