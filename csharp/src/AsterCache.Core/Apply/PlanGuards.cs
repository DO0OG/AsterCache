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

        foreach (Fingerprint fp in plan.Fingerprints)
        {
            Fingerprint now = hooks.Stat(fp.Path);
            if (now.Size != fp.Size || now.MtimeNs != fp.MtimeNs)
            {
                throw new PlanRejectedException("source_changed: " + fp.Path);
            }
        }
    }

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
    public static void CheckZoneFree(PlanFile plan, VolumeBitmap bitmap, Func<string, IReadOnlyList<Extent>?> extents)
    {
        var own = plan.Tasks.Select(t => t.Path).Distinct(StringComparer.Ordinal)
            .SelectMany(p => (extents(p) ?? []).Select(e => (A: e.Lcn, B: e.Lcn + e.Clusters)))
            .OrderBy(x => x.A).ThenBy(x => x.B).ToList();
        foreach (PlanTask t in plan.Tasks)
        {
            long done = DonePrefix(extents(t.Path), t.Vcn, t.Clusters, t.TargetLcn);
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
    public static PlanFile ResolveZone(PlanFile plan, IVolume vol, Func<string, IReadOnlyList<Extent>?> extents)
    {
        VolumeBitmap bm = vol.Bitmap();
        if (plan.Zone.StartLcn is null)
        {
            long need = (long)plan.Stats["zone_used_clusters"]!;
            long? baseLcn = bm.FindFreeRun(need, preferLowLcn: true, align: ZoneAlign);
            return baseLcn is null ? throw new PlanRejectedException($"no free run of {need} clusters") : plan.Resolve(baseLcn.Value);
        }

        CheckZoneFree(plan, bm, extents);
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
