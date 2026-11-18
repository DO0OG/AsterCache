using System.ComponentModel;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Volume;

namespace AsterCache.Core.Apply;

/// relayout/apply.py 의 계획 거부 검사와 존 확인: inside, check_roots, check_plan, done_prefix, check_zone_free, resolve_zone.
public static class PlanGuards
{
    /// 자동 존 시작 정렬 (클러스터). 4 = 16 KiB (4 KiB 클러스터), 섀도 복사본에 맞는 크기.
    public const long ZoneAlign = 4;

    /// os.path.normcase: 소문자, '/' 를 역슬래시로.
    private const char Bs = (char)92; // 역슬래시

    private static string NormCase(string p) => p.Replace('/', Bs).ToLowerInvariant();

    /// path 가 (링크를 푼 뒤) 허용 루트 중 하나 안인가.
    public static bool Inside(string path, IEnumerable<string> roots, Func<string, string> realPath)
    {
        string p = NormCase(realPath(path));
        foreach (string root in roots)
        {
            string r = NormCase(realPath(root));
            string rt = r.TrimEnd(Bs);
            if (p.TrimEnd(Bs) == rt || p.StartsWith(rt + Bs, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// 링크를 푼 경로의 드라이브 ("D:" 대문자).
    public static string Drive(string path, Func<string, string> realPath) => PlanFile.SplitDrive(realPath(path)).ToUpperInvariant();

    /// 허용 루트는 전부 지금 열 볼륨 위에 있어야 합니다.
    public static void CheckRoots(IReadOnlyList<string> roots, string letter, Func<string, string> realPath)
    {
        if (roots.Count == 0)
        {
            throw new PlanRejectedException("no allowed root given");
        }

        foreach (string r in roots)
        {
            string d = Drive(r, realPath);
            if (d != letter)
            {
                throw new PlanRejectedException($"allowed root {r} is on {d}, not on the opened volume {letter}");
            }
        }
    }

    /// 계획 볼륨·원본 루트·허용 루트·열린 볼륨이 어긋나거나, 어떤 경로든 (링크를 푼 뒤) 허용 루트 밖이거나 볼륨 밖이거나,
    /// 계획 뒤 파일이 바뀌었으면 계획 전체를 거부합니다.
    public static void CheckPlan(PlanFile plan, IReadOnlyList<string> roots, string letter, ApplyHooks hooks)
    {
        letter = letter.ToUpperInvariant().TrimEnd(Bs);
        Func<string, string> real = hooks.RealPath;
        if (plan.Volume.ToUpperInvariant() != letter || Drive(plan.OriginRoot, real) != letter)
        {
            throw new PlanRejectedException($"plan volume {plan.Volume} / origin {plan.OriginRoot} != opened volume {letter}");
        }

        CheckRoots(roots, letter, real);
        var paths = new HashSet<string>(plan.Tasks.Select(t => t.Path), StringComparer.Ordinal);
        paths.UnionWith(plan.Order.Select(o => o.Path));
        foreach (string p in paths)
        {
            if (Drive(p, real) != letter || !Inside(p, roots, real))
            {
                throw new PlanRejectedException("outside_allowed_root: " + p);
            }
        }

        foreach (Fingerprint fp in plan.Fingerprints.Where(f => !VolatileFiles.Ignored(f.Path) && !plan.SparseAllocations.ContainsKey(f.Path))) // 스파스 파일은 파일 단위로 보류하고 재계획합니다.
        {
            Fingerprint now = hooks.Stat(fp.Path);
            if (now.Size != fp.Size || now.MtimeNs != fp.MtimeNs)
            {
                throw new PlanRejectedException("source_changed: " + fp.Path);
            }
        }

        Dictionary<string, Fingerprint> fingerprints = plan.Fingerprints.ToDictionary(f => f.Path, StringComparer.Ordinal);
        foreach ((string path, IReadOnlyList<AllocatedRange> ranges) in plan.SparseAllocations)
        {
            if (ranges.Count == 0 || !fingerprints.TryGetValue(path, out Fingerprint? fp))
            {
                throw new PlanRejectedException("malformed sparse allocation: " + path);
            }

            long fileClusters = (fp.Size + plan.ClusterBytes - 1) / plan.ClusterBytes;
            foreach (PlanTask task in plan.Tasks.Where(t => t.Path == path))
            {
                if (!SparseAllocation.SupportsMove(task.Vcn, task.Clusters, fileClusters))
                {
                    throw new PlanRejectedException("unsupported_sparse_range: " + path);
                }
            }
        }
    }

    /// 현재 논리 할당 범위가 계획 당시와 다른 희소 파일을 반환합니다. 조회 실패도 보류 대상으로 돌려줍니다.
    public static IReadOnlySet<string> ChangedSparseFiles(PlanFile plan, Func<string, IReadOnlyList<Extent>?> extents, Func<string, Fingerprint>? stat = null)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        Dictionary<string, Fingerprint> fingerprints = plan.Fingerprints.ToDictionary(f => f.Path, StringComparer.Ordinal);
        changed.UnionWith(ChangedSparseFiles(plan.SparseAllocations, extents));
        if (stat is null)
        {
            return changed;
        }

        foreach ((string path, IReadOnlyList<AllocatedRange> _) in plan.SparseAllocations)
        {
            try
            {
                if (stat is not null && fingerprints.TryGetValue(path, out Fingerprint? fp))
                {
                    Fingerprint now = stat(path);
                    if (now.Size != fp.Size || now.MtimeNs != fp.MtimeNs)
                    {
                        _ = changed.Add(path);
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
            {
                _ = changed.Add(path);
            }
        }

        return changed;
    }

    public static IReadOnlySet<string> ChangedSparseFiles(IReadOnlyDictionary<string, IReadOnlyList<AllocatedRange>> expected,
        Func<string, IReadOnlyList<Extent>?> extents)
    {
        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string path, IReadOnlyList<AllocatedRange> ranges) in expected)
        {
            try
            {
                IReadOnlyList<Extent>? current = extents(path);
                if (current is null || !ranges.SequenceEqual(SparseAllocation.Normalize(current)))
                {
                    _ = changed.Add(path);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
            {
                _ = changed.Add(path);
            }
        }

        return changed;
    }

    public static bool IsAllocated(IReadOnlyList<Extent>? extents, long vcn, long clusters) => SparseAllocation.Covers(extents, vcn, clusters);

    /// [vcn, vcn + n) 안에서 target + 오프셋 에 놓인 클러스터 수 (앞에서부터 이어진 양이 아니라 전체).
    public static long PlacedClusters(IReadOnlyList<Extent>? ext, long vcn, long n, long target) =>
        ZoneAllocator.Pieces(ext ?? [], vcn, vcn + n).Where(x => x.Lcn - x.Vcn == target - vcn).Sum(x => x.N);

    /// [vcn, vcn + n) 의 앞에서부터 이미 target + 오프셋 에 놓인 클러스터 수.
    public static long DonePrefix(IReadOnlyList<Extent>? ext, long vcn, long n, long target)
    {
        long done = 0;
        foreach ((long v, long lcn, long m) in ZoneAllocator.Pieces(ext ?? [], vcn, vcn + n))
        {
            if (v != vcn + done || lcn != target + done)
            {
                break;
            }

            done += m;
        }

        return done;
    }

    /// 남은 목표 클러스터는 비어 있거나 계획 자신의 파일이 차지해야 합니다 (이동이 먼저 비웁니다).
    /// reserved: 다른 게임 계획이 예약한 존 범위. 남은 목표가 겹치면 같은 zone_not_free 로 거절합니다.
    public static void CheckZoneFree(PlanFile plan, VolumeBitmap bitmap, Func<string, IReadOnlyList<Extent>?> extents,
        IReadOnlyList<(long Lcn, long Clusters)>? reserved = null, IReadOnlySet<string>? ignoredPaths = null)
    {
        bool Ignored(string path) => ignoredPaths?.Contains(path) == true;
        var own = plan.Tasks.Select(t => t.Path).Where(p => !Ignored(p)).Distinct(StringComparer.Ordinal)
            .SelectMany(p => (extents(p) ?? []).Select(e => (A: e.Lcn, B: e.Lcn + e.Clusters)))
            .OrderBy(x => x.A).ThenBy(x => x.B).ToList();
        foreach (PlanTask t in plan.Tasks.Where(t => !Ignored(t.Path)))
        {
            long done = DonePrefix(extents(t.Path), t.Vcn, t.Clusters, t.TargetLcn);
            foreach ((long a, long n) in done < t.Clusters ? reserved ?? [] : [])
            {
                if (a < t.TargetLcn + t.Clusters && t.TargetLcn + done < a + n)
                {
                    throw new PlanRejectedException($"zone_not_free: LCN {Math.Max(a, t.TargetLcn + done)} is reserved by another plan");
                }
            }

            for (long lcn = t.TargetLcn + done; lcn < t.TargetLcn + t.Clusters; lcn++)
            {
                int j = LastStartAtOrBefore(own, lcn);
                if (bitmap.Used(lcn) && !(j >= 0 && lcn < own[j].B))
                {
                    throw new PlanRejectedException($"zone_not_free: LCN {lcn} is in use by another file");
                }
            }
        }
    }

    /// 존 시작을 정하고 (계획에 없으면 비트맵의 빈 구간) 목표를 확인합니다.
    public static PlanFile ResolveZone(PlanFile plan, IVolume vol, Func<string, IReadOnlyList<Extent>?> extents) =>
        ResolveZone(plan, vol, extents, null, out _);

    /// journalPath 를 주면, 이미 정한 존이 다른 파일에 막혔고 (zone_not_free) 그 저널에 끝나지 않은 단계가 없으며 계획이 제자리 조각 없이
    /// 존 오프셋만으로 된 것이면 (in_place_bytes == 0) 새 빈 구간을 찾아 목표를 옮깁니다 (rezoned = true). 이어할 단계가 있으면 기존처럼 거부합니다.
    public static PlanFile ResolveZone(PlanFile plan, IVolume vol, Func<string, IReadOnlyList<Extent>?> extents, string? journalPath, out bool rezoned,
        IReadOnlyList<(long Lcn, long Clusters)>? reserved = null, IReadOnlySet<string>? ignoredPaths = null)
    {
        rezoned = false;
        VolumeBitmap bm = vol.Bitmap();
        if (plan.Zone.StartLcn is null)
        {
            ZonePlacement fresh = FindZone(plan, bm, reserved);
            return Stamp(plan.Resolve(fresh.Start), fresh);
        }

        try
        {
            CheckZoneFree(plan, bm, extents, reserved, ignoredPaths);
            return plan;
        }
        catch (PlanRejectedException e) when (journalPath is not null && e.Message.StartsWith("zone_not_free", StringComparison.Ordinal)
            && plan.Stats.TryGetValue("in_place_bytes", out object? inPlace) && inPlace is 0L && !HasUnfinishedStep(journalPath))
        {
            long oldStart = plan.Zone.StartLcn.Value;
            ZonePlacement placed = FindZone(plan, bm, reserved);
            long start = placed.Start;
            rezoned = true;
            return Stamp(plan with { Zone = plan.Zone with { StartLcn = start }, Tasks = plan.Tasks.Select(t => t with { TargetLcn = t.TargetLcn - oldStart + start }).ToList() }, placed);
        }
    }

    /// 저널에 마지막 state 가 done 이 아닌 단계 (planned·failed) 가 있나. 저널이 없으면 없음.
    public static bool HasUnfinishedStep(string journalPath)
    {
        if (!File.Exists(journalPath))
        {
            return false;
        }

        using var jr = new Journal(journalPath);
        var last = new Dictionary<long, string?>();
        foreach (JObject r in jr.Rows)
        {
            if (r.TryGetValue("type", out object? t) && Equals(t, "step") && r.TryGetValue("idx", out object? i) && i is long idx)
            {
                last[idx] = r.TryGetValue("state", out object? s) ? s as string : null;
            }
        }

        return last.Values.Any(s => s != "done");
    }

    /// 계획 stats 의 여유 기록 키: 새 존에 앞 여유를 두었을 때만 남고 (서비스 로그용), 계획 SHA 에는 들어가지 않음.
    public const string ZoneMarginKey = "zone_margin_clusters";

    public const string ZoneRunStartKey = "zone_run_start_lcn";

    // 다른 프로그램의 새 파일이 큰 빈 구간의 앞쪽부터 채워지므로, 구간이 충분히 길면 시작이 아니라 앞에 여유를 둔 자리에 존을 둠 (VolumeBitmap.FindZoneRun).
    private static ZonePlacement FindZone(PlanFile plan, VolumeBitmap bm, IReadOnlyList<(long Lcn, long Clusters)>? reserved)
    {
        long need = (long)plan.Stats["zone_used_clusters"]!;
        return bm.FindZoneRun(need, plan.ClusterBytes, ZoneAlign, reserved) ?? throw new PlanRejectedException($"no free run of {need} clusters");
    }

    private static PlanFile Stamp(PlanFile plan, ZonePlacement placed)
    {
        _ = plan.Stats.Remove(ZoneMarginKey);
        _ = plan.Stats.Remove(ZoneRunStartKey);
        if (placed.MarginClusters > 0)
        {
            plan.Stats[ZoneMarginKey] = placed.MarginClusters;
            plan.Stats[ZoneRunStartKey] = placed.RunStart;
        }

        return plan;
    }

    // bisect_right(starts, lcn) - 1
    private static int LastStartAtOrBefore(List<(long A, long B)> own, long lcn)
    {
        int lo = 0;
        int hi = own.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (own[mid].A <= lcn)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo - 1;
    }
}
