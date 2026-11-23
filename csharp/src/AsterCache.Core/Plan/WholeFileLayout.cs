using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Model;

namespace AsterCache.Core.Plan;

/// 설정 auto.relocate_layout 의 값: whole_file 은 파일 전체를 한 조각으로 zone 에 놓고, ranges 는 핫 범위를 접근 순서로 이어 붙임 (예전 방식).
public static class RelocateLayouts
{
    public const string WholeFile = "whole_file";
    public const string Ranges = "ranges";

    public static bool IsValid(string? v) => v is WholeFile or Ranges;
}

/// zone 에 놓는 방법. 나중에 큰 조각 배치 (파일의 한 구간만 한 조각으로) 를 값 하나로 더함: 배정과 검사는 (경로, vcn, 길이) 구간으로 다룸.
public enum Placement
{
    WholeFile,
    InPlace,
}

/// 파일 하나의 배치 결정. Reason 은 제자리일 때의 사유 (통째면 null).
public sealed record FileDecision(string Path, int FirstAccess, long HotClusters, long FileClusters, Placement Placement, string? Reason);

/// 통째 배정 결과: 작업·통계·접근 순서 (ZoneAllocator 와 같은 모양) 와 실제로 쓴 파일별 결정 (구멍이 있어 제자리로 내린 파일 반영).
/// Offsets: 통째 파일마다 zone 시작에서 그 파일이 놓일 자리까지의 클러스터 수 (이미 그 자리에 있어 작업이 없는 파일 포함).
public sealed record WholePlan(AllocResult Alloc, IReadOnlyList<FileDecision> Decisions, IReadOnlyDictionary<string, long> Offsets);

/// 파일 전체를 한 조각으로 zone 에 놓는 배치 (예약 드라이브 최적화가 한 조각 파일은 옮기지 않는다는 관찰에 기대). ZoneAllocator 는 건드리지 않고 옆에 둠: ranges 방식이 설정으로 계속 쓰임.
/// 계획 형식은 version 을 올리지 않고 stats 의 표지 (layout_rule) 로 구분합니다: 표지가 없으면 예전 범위 계획, 알려진 키 밖의 stats 는 읽을 때 그대로 보존됨.
public static class WholeFileLayout
{
    /// 계획 stats 의 표지: 통째 배치 규칙으로 만든 계획.
    public const string RuleKey = "layout_rule";

    public const long RuleVersion = 1;

    /// 계획 stats: 통째로 놓은 파일 경로 (첫 접근 순).
    public const string WholeFilesKey = "whole_files";

    /// 계획 stats: 제자리로 둔 파일 수의 사유별 합 (진단용).
    public const string InPlaceFilesKey = "in_place_files";

    /// 계획 stats: whole_files 와 같은 순서로 각 파일이 놓일 자리의 zone 시작 기준 오프셋 (클러스터). 이미 목표에 있어 작업이 없는 파일도 있어서,
    /// 잔여 재계획이 "이미 목표에 놓인 파일" 을 알아보는 이전 목표로 씀 (작업만으로는 알 수 없음).
    public const string WholeOffsetsKey = "whole_offsets";

    public const string VolatileReason = "volatile";
    public const string UnmovableReason = "unmovable";
    public const string SparseHolesReason = "sparse_holes";
    public const string MostlyColdReason = "mostly_cold";
    public const string OverDailyLimitReason = "over_daily_limit";
    public const string ZoneFullReason = "zone_full";
    public const string UnknownSizeReason = "unknown_size";

    /// 통째 배치 대상 파일 크기의 상한: 핫 바이트의 이 배수.
    public const double WholeFileRatio = 2.0;

    /// 파일별로 통째로 놓을지 제자리에 둘지 정함 (디스크를 읽지 않는 순수 함수). 결과는 첫 접근 순서.
    /// 규칙 (처음 맞는 것): 호출자가 준 제자리 사유 (가변·옮길 수 없음·구멍 있는 스파스) → 크기 모름 → 크기 > 핫 × ratio → 크기 > maxFileBytes (하루 예산) →
    /// 통째 후보의 크기 누적이 capacityClusters 를 넘는 첫 파일부터 끝까지 (늦게 접근하는 파일부터 제자리, ZoneAllocator 의 가득 참 규칙과 같은 모양).
    public static List<FileDecision> Classify(IReadOnlyList<PlanRange> order, IReadOnlyDictionary<string, long> sizes, long clusterBytes, long capacityClusters,
        Func<string, string?> inPlaceReason, long maxFileBytes = long.MaxValue, double ratio = WholeFileRatio)
    {
        var hot = new Dictionary<string, long>(StringComparer.Ordinal);
        var names = new List<string>();
        foreach (PlanRange o in order)
        {
            if (!hot.TryGetValue(o.Path, out long sum))
            {
                names.Add(o.Path);
            }

            hot[o.Path] = sum + o.Clusters;
        }

        var result = new List<FileDecision>(names.Count);
        long used = 0;
        bool full = false;
        for (int i = 0; i < names.Count; i++)
        {
            string path = names[i];
            long hotClusters = hot[path];
            long fileClusters = sizes.TryGetValue(path, out long size) ? CeilDiv(size, clusterBytes) : 0;
            string? why = inPlaceReason(path);
            if (why is null && fileClusters <= 0)
            {
                why = UnknownSizeReason;
            }

            if (why is null && fileClusters > hotClusters * ratio)
            {
                why = MostlyColdReason;
            }

            if (why is null && fileClusters * clusterBytes > maxFileBytes)
            {
                why = OverDailyLimitReason;
            }

            if (why is null)
            {
                if (full || used + fileClusters > capacityClusters)
                {
                    full = true; // 접근 순서의 앞부분만 통째로 놓고 나머지는 제자리 (뒤의 작은 파일도 같음)
                    why = ZoneFullReason;
                }
                else
                {
                    used += fileClusters;
                }
            }

            result.Add(new FileDecision(path, i, hotClusters, fileClusters, why is null ? Placement.WholeFile : Placement.InPlace, why));
        }

        return result;
    }

    /// 파일을 통째로 놓을 수 없는 사유 (가변 파일, 옮길 수 없는 파일, 구멍 있는 스파스), 없으면 null. 계획을 처음 만들 때와 예전 계획을 이행할 때가 같은 규칙을 씀.
    public static string? InPlaceReason(string path, IReadOnlyList<Extent>? extents, long size, long clusterBytes, bool sparse, Func<string, bool>? unmovable) =>
        VolatileFiles.Ignored(path) ? VolatileReason
        : unmovable?.Invoke(path) == true ? UnmovableReason
        : sparse && HasHoles(extents, CeilDiv(size, clusterBytes)) ? SparseHolesReason : null;

    /// 파일의 [0, fileClusters) 가 디스크 구간으로 빠짐없이 덮이지 않으면 (구멍 있는 스파스 등) true: 한 조각을 보장할 수 없어 제자리로 둠.
    public static bool HasHoles(IReadOnlyList<Extent>? extents, long fileClusters) =>
        extents is null || ZoneAllocator.Pieces(extents, 0, fileClusters).Sum(p => p.N) != fileClusters;

    /// 통째로 놓을 파일을 first access 순서로 zone 에 하나씩 이어 붙임. 파일 하나의 목표는 [zoneStart + off, + fileClusters) 입니다.
    /// 파일의 지금 조각 가운데 목표 자리에 이미 있는 것은 그대로 두고, 나머지만 작업으로 만듦 (같은 파일의 이어진 조각은 한 작업으로 합침: 소스가 여러 조각이어도 작업은 하나).
    /// fixedClusters: 같은 zone 을 이어 쓰는 계획에서 이미 쓰던 앞부분 — 그 안에 한 조각으로 있는 파일은 그 자리를 그대로 쓰고, 새 파일은 그 뒤에 붙임.
    /// zoneStartLcn 이 null 이면 zone 을 아직 못 정한 것: ZoneAllocator 처럼 목표를 존 시작 기준 오프셋으로 돌려줌.
    /// 결과 Order 는 입력 order 그대로 (예열 목록과 hot_bytes 를 쓰는 곳이 바뀌지 않게).
    public static WholePlan Allocate(IReadOnlyList<PlanRange> order, IReadOnlyList<FileDecision> decisions,
        IReadOnlyDictionary<string, IReadOnlyList<Extent>?> extentsByFile, long? zoneStartLcn, long clusterBytes,
        long fixedClusters = 0, IReadOnlySet<string>? sparseFiles = null, long skippedFiles = 0, long alignClusters = 4)
    {
        long cb = clusterBytes;
        bool rel = zoneStartLcn is null;
        long zoneStart = zoneStartLcn ?? ZoneAllocator.RelBase;
        long fixedEnd = zoneStart + (rel ? 0 : fixedClusters);
        long curOff = fixedEnd - zoneStart;

        var flat = new List<(long Lcn, long N)>();
        foreach (PlanRange o in order)
        {
            flat.AddRange(ZoneAllocator.Pieces(extentsByFile[o.Path]!, o.Vcn, o.Vcn + o.Clusters).Select(p => (p.Lcn, p.N)));
        }

        var tasks = new List<PlanTask>();
        var sources = new List<List<(long Lcn, long N)>>();
        var effective = new List<FileDecision>(decisions.Count);
        var offsets = new Dictionary<string, long>(StringComparer.Ordinal);
        long inPlaceBytes = 0, overflowBytes = 0, misaligned = 0, wholeCount = 0;
        foreach (FileDecision d in decisions)
        {
            IReadOnlyList<Extent> ext = extentsByFile[d.Path]!;
            if (d.Placement == Placement.WholeFile && HasHoles(ext, d.FileClusters))
            {
                effective.Add(d with { Placement = Placement.InPlace, Reason = SparseHolesReason });
                overflowBytes += d.HotClusters * cb;
                continue;
            }

            effective.Add(d);
            List<(long Vcn, long Lcn, long N)> pieces = ZoneAllocator.Pieces(ext, 0, d.FileClusters);
            if (d.Placement != Placement.WholeFile)
            {
                overflowBytes += d.HotClusters * cb;
                continue;
            }

            wholeCount++;
            if (!rel && fixedClusters > 0 && pieces.Count > 0 && ZoneAllocator.CountRuns(pieces.Select(p => (p.Lcn, p.N)).ToList()) == 1
                && pieces[0].Lcn >= zoneStart && pieces[0].Lcn + d.FileClusters <= fixedEnd)
            {
                inPlaceBytes += d.FileClusters * cb; // 이미 쓰던 구간 안에 한 조각: 그 자리를 그대로 씀
                offsets[d.Path] = pieces[0].Lcn - zoneStart;
                continue;
            }

            long target = rel ? zoneStart + curOff : ResolveSelfOverlap(pieces, zoneStart + curOff, d.FileClusters);
            offsets[d.Path] = target - zoneStart;
            curOff = target - zoneStart + d.FileClusters;
            if (sparseFiles?.Contains(d.Path) == true)
            {
                AddSparseTasks(d, pieces, ext, target, tasks, sources, ref inPlaceBytes, cb);
                continue;
            }

            foreach ((long vcn, long lcn, long n) in pieces)
            {
                long goal = target + vcn;
                if (lcn == goal)
                {
                    inPlaceBytes += n * cb;
                    continue;
                }

                if ((goal - lcn) % alignClusters != 0)
                {
                    misaligned++;
                }

                PlanTask? last = tasks.Count > 0 ? tasks[^1] : null;
                if (last is not null && last.Path == d.Path && last.Vcn + last.Clusters == vcn && last.TargetLcn + last.Clusters == goal)
                {
                    tasks[^1] = last with { Clusters = last.Clusters + n };
                    sources[^1].Add((lcn, n));
                }
                else
                {
                    tasks.Add(new PlanTask(d.Path, vcn, n, goal));
                    sources.Add([(lcn, n)]);
                }
            }
        }

        if (sources.Any(s => s.Any(x => zoneStart < x.Lcn + x.N && x.Lcn < zoneStart + curOff)))
        {
            tasks = ZoneAllocator.OrderTasks(tasks, sources);
        }

        if (rel)
        {
            tasks = tasks.Select(t => t with { TargetLcn = t.TargetLcn - ZoneAllocator.RelBase }).ToList();
        }

        long moved = tasks.Sum(t => t.Clusters);
        var stats = new ZoneStats(0, 0, inPlaceBytes, overflowBytes, misaligned,
            flat.Sum(p => p.N) * cb, moved * cb, tasks.Count, tasks.Count == 0 ? 0 : PyFloat.Round2((double)moved / tasks.Count),
            curOff, skippedFiles, ZoneAllocator.CountRuns(flat), wholeCount);
        return new WholePlan(new AllocResult(tasks, stats, order), effective, offsets);
    }

    // 목표 [target, target + 파일 길이) 가 이 파일 자신의 지금 조각 (이미 목표 자리에 있는 조각은 빼고) 과 겹치면, 그 이동은 자기 자신이 막혀 순환 오류가 됨 (OrderTasks 는 자기 의존을 빼지 않음).
    // 파일이 한 조각이고 목표 뒤쪽에 있으면 그 자리를 목표로 인정하고 (순서만 포기, 다시 쓰지 않음), 아니면 겹치지 않을 때까지 목표를 자기 조각 끝 뒤로 미룸 (사이는 빈 채로 남음).
    // shortcut: 미루면 zone_used_clusters 가 한도 (Classify 의 합) 를 넘을 수 있음, 다른 파일끼리의 순환 (서로의 소스에 목표가 겹침) 은 풀지 않고 OrderTasks 의 오류로 새 zone 을 찾게 둠.
    private static long ResolveSelfOverlap(List<(long Vcn, long Lcn, long N)> pieces, long target, long fileClusters)
    {
        bool single = pieces.Count > 0 && pieces.Sum(p => p.N) == fileClusters && ZoneAllocator.CountRuns(pieces.Select(p => (p.Lcn, p.N)).ToList()) == 1;
        long t = target;
        for (bool again = true; again;)
        {
            again = false;
            foreach ((long vcn, long lcn, long n) in pieces)
            {
                if (lcn == t + vcn || lcn >= t + fileClusters || lcn + n <= t)
                {
                    continue; // 이미 목표 자리이거나 겹치지 않음
                }

                if (single && pieces[0].Lcn - pieces[0].Vcn >= target)
                {
                    return pieces[0].Lcn - pieces[0].Vcn;
                }

                t = lcn + n;
                again = true;
                break;
            }
        }

        return t;
    }

    // 스파스 파일 (구멍 없음): 작업은 16 클러스터 경계로만 (SupportsMove). 16 배수가 아니면 [0, 꼬리 시작) 과 꼬리로 나눔 (ZoneAllocator 의 꼬리 규칙과 같음).
    // shortcut: 파일이 이미 목표에 한 조각이 아니면 파일 전체를 옮김 (일부가 목표에 있어도 제외하지 않음), 실제로 통째 조건에 드는 스파스 파일이 없어 이 이상은 만들지 않음.
    private static void AddSparseTasks(FileDecision d, List<(long Vcn, long Lcn, long N)> pieces, IReadOnlyList<Extent> ext, long target,
        List<PlanTask> tasks, List<List<(long Lcn, long N)>> sources, ref long inPlaceBytes, long cb)
    {
        if (pieces.Count > 0 && ZoneAllocator.CountRuns(pieces.Select(p => (p.Lcn, p.N)).ToList()) == 1 && pieces[0].Lcn == target)
        {
            inPlaceBytes += d.FileClusters * cb;
            return;
        }

        long fc = d.FileClusters;
        long split = !SparseAllocation.SupportsMove(0, fc, fc) && SparseAllocation.TailStart(fc) is { } tail ? tail : fc;
        foreach ((long a, long b) in split < fc ? new[] { (0L, split), (split, fc) } : [(0L, fc)])
        {
            tasks.Add(new PlanTask(d.Path, a, b - a, target + a));
            sources.Add(ZoneAllocator.Pieces(ext, a, b).Select(p => (p.Lcn, p.N)).ToList());
        }
    }

    /// 통째 계획의 stats 표지: layout_rule, whole_files (첫 접근 순), in_place_files (사유별 파일 수).
    public static void AddStats(JObject stats, IReadOnlyList<FileDecision> decisions, IReadOnlyDictionary<string, long>? offsets = null)
    {
        stats.Add(RuleKey, RuleVersion);
        List<string> whole = [.. decisions.Where(d => d.Placement == Placement.WholeFile).Select(d => d.Path)];
        stats.Add(WholeFilesKey, whole.Select(p => (object?)p).ToList());
        if (offsets is not null && whole.All(offsets.ContainsKey))
        {
            stats.Add(WholeOffsetsKey, whole.Select(p => (object?)offsets[p]).ToList());
        }

        var reasons = new JObject();
        foreach (IGrouping<string, FileDecision> g in decisions.Where(d => d.Placement == Placement.InPlace).GroupBy(d => d.Reason ?? string.Empty).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            reasons.Add(g.Key, (long)g.Count());
        }

        stats.Add(InPlaceFilesKey, reasons);
    }

    /// 이행에서 새 계획이 물려받지 않는 예전 계획의 stats 키: 잔여·보류 표지는 이번 판정으로 다시 정하고, zone 자리 기록은 새 zone 을 정할 때 다시 씀.
    private static readonly string[] RebuildDropKeys = [ResidualPlanner.HeldKey, ResidualPlanner.HeldTargetsKey, ResidualPlanner.AdjacentKey, ResidualPlanner.GenerationKey,
        "residual", "fixed_clusters", PlanGuards.ZoneMarginKey, PlanGuards.ZoneRunStartKey];

    /// 예전 범위 계획을 통째 배치 계획으로 다시 세움 (이행): 접근 순서 (order) 와 지문은 그대로 쓰고, 파일 크기는 지문에서, 지금 배치는 extentsFn 으로 읽음.
    /// zone 시작은 미정으로 둬서 적용 때 빈 구간을 새로 고름 (예전 zone 의 조각은 고정하지 않음: 고정하면 파일이 통째가 되지 않음).
    /// 읽지 못하는 파일 (extents null) 이 있으면 지금은 판단할 수 없어 null: 호출자가 나중에 다시 시도. 디스크 구간이 없는 파일 (크기 0·MFT 상주) 은 계획에서 빼고 skipped_files 에 셈.
    public static PlanFile? Rebuild(PlanFile old, Func<string, IReadOnlyList<Extent>?> extentsFn, Func<string, bool> sparseFn, Func<string, bool>? unmovableFn, long capacityBytes, long maxFileBytes = long.MaxValue)
    {
        long cb = old.ClusterBytes;
        var ext = new Dictionary<string, IReadOnlyList<Extent>?>(StringComparer.Ordinal);
        var order = new List<PlanRange>();
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        foreach (PlanRange o in old.Order)
        {
            if (!ext.TryGetValue(o.Path, out IReadOnlyList<Extent>? e))
            {
                ext[o.Path] = e = extentsFn(o.Path);
            }

            if (e is null)
            {
                return null;
            }

            if (e.Count == 0)
            {
                _ = dropped.Add(o.Path);
                continue;
            }

            order.Add(o);
        }

        Dictionary<string, long> sizes = old.Fingerprints.ToDictionary(f => f.Path, f => f.Size, StringComparer.Ordinal);
        var sparse = new HashSet<string>(order.Select(o => o.Path).Distinct(StringComparer.Ordinal).Where(sparseFn), StringComparer.Ordinal);
        List<FileDecision> classified = Classify(order, sizes, cb, capacityBytes / cb,
            p => InPlaceReason(p, ext[p], sizes.GetValueOrDefault(p), cb, sparse.Contains(p), unmovableFn), maxFileBytes);
        long skipped = (old.Stats.TryGetValue("skipped_files", out object? sk) && sk is long s ? s : 0) + dropped.Count;
        WholePlan whole = Allocate(order, classified, ext, null, cb, 0, sparse, skipped);
        JObject stats = whole.Alloc.Stats.ToJson();
        AddStats(stats, whole.Decisions, whole.Offsets);
        foreach ((string key, object? value) in old.Stats.Where(kv => !stats.ContainsKey(kv.Key) && !RebuildDropKeys.Contains(kv.Key)))
        {
            stats.Add(key, value);
        }

        var printed = order.Select(o => o.Path).ToHashSet(StringComparer.Ordinal);
        return old with
        {
            Zone = new PlanZone(null, order.Sum(o => o.Clusters)),
            Tasks = whole.Alloc.Tasks,
            Order = order,
            Fingerprints = [.. old.Fingerprints.Where(f => !dropped.Contains(f.Path))],
            SparseAllocations = sparse.Where(printed.Contains).ToDictionary(p => p, p => old.SparseAllocations.TryGetValue(p, out IReadOnlyList<AllocatedRange>? a) ? a : SparseAllocation.Normalize(ext[p]!), StringComparer.Ordinal),
            Stats = stats,
        };
    }

    /// 통째 배치 계획이면 통째로 놓은 파일 경로 (첫 접근 순), 예전 범위 계획이면 null.
    public static IReadOnlyList<string>? WholeFiles(PlanFile plan)
    {
        if (!plan.Stats.ContainsKey(RuleKey))
        {
            return null;
        }

        return plan.Stats.TryGetValue(WholeFilesKey, out object? raw) && raw is List<object?> rows ? rows.OfType<string>().ToList() : [];
    }

    /// 통째 배치 계획의 파일별 목표 자리 (경로, zone 시작 기준 오프셋). 예전 계획이거나 오프셋이 없으면 비어 있음.
    public static IReadOnlyList<(string Path, long Offset)> WholeOffsets(PlanFile plan)
    {
        IReadOnlyList<string>? files = WholeFiles(plan);
        return files is not null && plan.Stats.TryGetValue(WholeOffsetsKey, out object? raw) && raw is List<object?> rows && rows.Count == files.Count && rows.All(r => r is long)
            ? [.. files.Select((p, i) => (p, (long)rows[i]!))] : [];
    }

    private static long CeilDiv(long x, long y) => (x + y - 1) / y;
}
