using AsterCache.Core.Json;
using AsterCache.Core.Model;

namespace AsterCache.Core.Plan;

/// 옮길 작업: Path 의 VCN [Vcn, Vcn + Clusters) 를 TargetLcn 으로.
public sealed record PlanTask(string Path, long Vcn, long Clusters, long TargetLcn)
{
    public JObject ToJson() => new() { { "path", Path }, { "vcn", Vcn }, { "clusters", Clusters }, { "target_lcn", TargetLcn } };
}

/// 접근 순서의 핫 범위.
public sealed record PlanRange(string Path, long Vcn, long Clusters)
{
    public JObject ToJson() => new() { { "path", Path }, { "vcn", Vcn }, { "clusters", Clusters } };
}

/// allocate_zone 의 stats. avg_clusters_per_task 는 작업이 없으면 정수 0, 있으면 round(x, 2).
public sealed record ZoneStats(
    long PreservedSegments, long PreservedBytes, long InPlaceBytes, long OverflowBytes, long MisalignedPieces,
    long HotBytes, long MovedBytes, long Tasks, double AvgClustersPerTask, long ZoneUsedClusters, long SkippedFiles,
    long CurrentRuns, long ExpectedRuns)
{
    public JObject ToJson() => new()
    {
        { "preserved_segments", PreservedSegments },
        { "preserved_bytes", PreservedBytes },
        { "in_place_bytes", InPlaceBytes },
        { "overflow_bytes", OverflowBytes },
        { "misaligned_pieces", MisalignedPieces },
        { "hot_bytes", HotBytes },
        { "moved_bytes", MovedBytes },
        { "tasks", Tasks },
        { "avg_clusters_per_task", Tasks == 0 ? 0L : (object)AvgClustersPerTask },
        { "zone_used_clusters", ZoneUsedClusters },
        { "skipped_files", SkippedFiles },
        { "current_runs", CurrentRuns },
        { "expected_runs", ExpectedRuns },
    };
}

public sealed record AllocResult(IReadOnlyList<PlanTask> Tasks, ZoneStats Stats, IReadOnlyList<PlanRange> Order);

/// relayout/plan.py 의 존 배치: _subtract, _pieces, count_runs, _order_tasks, _hot_ranges, allocate_zone.
public static class ZoneAllocator
{
    /// 존 시작을 아직 모를 때 쓰는 가짜 시작 LCN. 실제 LCN 과 겹치지 않습니다.
    public const long RelBase = 1L << 60;

    private readonly record struct Piece(string Path, long Vcn, long Lcn, long N);

    /// [a, b) 에서 이미 차지한 구간을 뺀 나머지.
    internal static List<(long A, long B)> Subtract((long A, long B) rng, List<(long A, long B)> claimed)
    {
        var parts = new List<(long A, long B)>();
        long cur = rng.A;
        foreach ((long a, long b) in claimed)
        {
            if (b <= cur || a >= rng.B)
            {
                continue;
            }

            if (a > cur)
            {
                parts.Add((cur, a));
            }

            cur = Math.Max(cur, b);
        }

        if (cur < rng.B)
        {
            parts.Add((cur, rng.B));
        }

        return parts;
    }

    /// 익스텐트를 VCN 범위 [a, b) 와 교차시킨 (vcn, lcn, n) 목록.
    public static List<(long Vcn, long Lcn, long N)> Pieces(IReadOnlyList<Extent> ext, long a, long b)
    {
        var result = new List<(long Vcn, long Lcn, long N)>();
        foreach (Extent e in ext)
        {
            long lo = Math.Max(a, e.Vcn);
            long hi = Math.Min(b, e.Vcn + e.Clusters);
            if (lo < hi)
            {
                result.Add((lo, e.Lcn + lo - e.Vcn, hi - lo));
            }
        }

        return result;
    }

    /// (lcn, n) 열에서 물리적으로 이어진 구간의 수.
    public static long CountRuns(IReadOnlyList<(long Lcn, long N)> lcns)
    {
        long runs = 0;
        for (int i = 0; i < lcns.Count; i++)
        {
            if (i == 0 || lcns[i - 1].Lcn + lcns[i - 1].N != lcns[i].Lcn)
            {
                runs++;
            }
        }

        return runs;
    }

    /// 목표가 다른 작업의 원본과 겹치면 그 작업 뒤로 미룸 (Kahn, 작은 인덱스 우선). 순환이면 PyValueException.
    internal static List<PlanTask> OrderTasks(List<PlanTask> tasks, List<List<(long Lcn, long N)>> sources)
    {
        var iv = new List<(long Start, long End, int Task)>();
        for (int i = 0; i < sources.Count; i++)
        {
            foreach ((long lcn, long n) in sources[i])
            {
                iv.Add((lcn, lcn + n, i));
            }
        }

        iv.Sort();
        var deps = new HashSet<int>[tasks.Count];
        var blockedBy = new List<int>[tasks.Count];
        for (int i = 0; i < tasks.Count; i++)
        {
            deps[i] = [];
            blockedBy[i] = [];
        }

        for (int i = 0; i < tasks.Count; i++)
        {
            long tgt = tasks[i].TargetLcn;
            int j = Math.Max(0, UpperBound(iv, tgt) - 1);
            while (j < iv.Count && iv[j].Start < tgt + tasks[i].Clusters)
            {
                if (iv[j].End > tgt)
                {
                    deps[i].Add(iv[j].Task);
                    blockedBy[iv[j].Task].Add(i);
                }

                j++;
            }
        }

        var heap = new PriorityQueue<int, int>();
        for (int i = 0; i < tasks.Count; i++)
        {
            if (deps[i].Count == 0)
            {
                heap.Enqueue(i, i);
            }
        }

        var done = new List<PlanTask>(tasks.Count);
        while (heap.TryDequeue(out int next, out _))
        {
            done.Add(tasks[next]);
            foreach (int k in blockedBy[next])
            {
                if (deps[k].Remove(next) && deps[k].Count == 0)
                {
                    heap.Enqueue(k, k);
                }
            }
        }

        if (done.Count != tasks.Count)
        {
            throw new PyValueException("zone overlaps hot data in a cycle; choose another zone");
        }

        return done;
    }

    // bisect_right(starts, value): value 보다 큰 첫 시작의 위치
    private static int UpperBound(List<(long Start, long End, int Task)> iv, long value)
    {
        int lo = 0;
        int hi = iv.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (iv[mid].Start <= value)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }

    /// 단위들 -> 접근 순서의 VCN 범위 (파일 안에서 중복 제거) 와 건너뛴 경로 수.
    internal static (List<PlanRange> Order, int Skipped) HotRanges(IReadOnlyList<Unit> units,
        IReadOnlyDictionary<string, IReadOnlyList<Extent>?> extentsByFile, long cb, long block, IReadOnlyDictionary<string, long>? sizes)
    {
        var claimed = new Dictionary<string, List<(long A, long B)>>(StringComparer.Ordinal);
        var order = new List<PlanRange>();
        var skipped = new HashSet<string>(StringComparer.Ordinal);
        foreach (Unit u in units)
        {
            if (!extentsByFile.TryGetValue(u.Path, out IReadOnlyList<Extent>? ext) || ext is null || ext.Count == 0)
            {
                skipped.Add(u.Path);
                continue;
            }

            long v1 = Math.Min(CeilDiv((u.Start + u.Count) * block, cb), ext[^1].Vcn + ext[^1].Clusters);
            if (sizes is not null && sizes.Count > 0 && sizes.TryGetValue(u.Path, out long size))
            {
                v1 = Math.Min(v1, CeilDiv(size, cb));
            }

            if (!claimed.TryGetValue(u.Path, out List<(long A, long B)>? mine))
            {
                claimed[u.Path] = mine = [];
            }

            foreach ((long a, long b) in Subtract((u.Start * block / cb, v1), mine))
            {
                int at = mine.BinarySearch((a, b));
                mine.Insert(at < 0 ? ~at : at, (a, b));
                order.Add(new PlanRange(u.Path, a, b - a));
            }
        }

        return (order, skipped.Count);
    }

    private static long CeilDiv(long x, long y) => (x + y - 1) / y;

    /// 단위들을 접근 순서대로 존의 연속 클러스터에 배치합니다. 반환: 옮길 작업, 통계, 접근 순서의 전체 핫 범위.
    /// zoneStartLcn 이 null 이면 존을 아직 못 정한 것: 목표는 존 시작 기준 오프셋으로 돌려주고, 제자리·존 겹침은 없다고 봅니다.
    public static AllocResult Allocate(IReadOnlyList<Unit> units, IReadOnlyDictionary<string, IReadOnlyList<Extent>?> extentsByFile,
        long? zoneStartLcn, long zoneClusters, long clusterBytes, long block = HotSet.Block,
        IReadOnlyDictionary<string, long>? sizes = null, long preserveBytes = 0, long alignClusters = 4, long fixedClusters = 0)
    {
        long cb = clusterBytes;
        bool rel = zoneStartLcn is null;
        long zoneStart = zoneStartLcn ?? RelBase;
        (List<PlanRange> order, int skipped) = HotRanges(units, extentsByFile, cb, block, sizes);

        var flat = new List<Piece>();
        foreach (PlanRange o in order)
        {
            foreach ((long v, long lcn, long n) in Pieces(extentsByFile[o.Path]!, o.Vcn, o.Vcn + o.Clusters))
            {
                flat.Add(new Piece(o.Path, v, lcn, n));
            }
        }

        var groups = new List<List<Piece>>();
        foreach (Piece piece in flat)
        {
            if (groups.Count > 0 && groups[^1][^1].Lcn + groups[^1][^1].N == piece.Lcn)
            {
                groups[^1].Add(piece);
            }
            else
            {
                groups.Add([piece]);
            }
        }

        var tasks = new List<PlanTask>();
        var sources = new List<List<(long Lcn, long N)>>();
        var final = new List<(long Lcn, long N)>();
        long curOff = 0;
        long fixedEnd = zoneStart + (rel ? 0 : Math.Min(fixedClusters, zoneClusters));
        bool InZone(Piece p) => p.Lcn >= zoneStart && p.Lcn + p.N <= fixedEnd;
        curOff = fixedEnd - zoneStart; // 같은 존을 이어 쓰는 계획: 이미 쓰던 구간 [시작, 시작 + fixedClusters) 안의 조각은 지금 물리 순서 그대로 두고 (접근 순서와 달라도 순환이 아님) 새 조각은 그 뒤에 덧붙임

        bool full = false;
        long preservedSegments = 0, preservedBytes = 0, inPlaceBytes = 0, overflowBytes = 0, misaligned = 0;
        foreach (List<Piece> g in groups)
        {
            long total = g.Sum(p => p.N);
            if (preserveBytes > 0 && total * cb >= preserveBytes)
            {
                preservedSegments++;
                preservedBytes += total * cb;
                final.AddRange(g.Select(p => (p.Lcn, p.N)));
                continue;
            }

            foreach (Piece p in g)
            {
                if (InZone(p))
                {
                    inPlaceBytes += p.N * cb;
                    final.Add((p.Lcn, p.N));
                    continue;
                }

                if (full || curOff + p.N > zoneClusters)
                {
                    full = true; // 접근 순서의 앞부분만 유지하고 나머지는 제자리
                    overflowBytes += p.N * cb;
                    final.Add((p.Lcn, p.N));
                    continue;
                }

                long target = zoneStart + curOff;
                curOff += p.N;
                final.Add((target, p.N));
                if (p.Lcn == target)
                {
                    inPlaceBytes += p.N * cb;
                    continue;
                }

                if ((target - p.Lcn) % alignClusters != 0)
                {
                    misaligned++;
                }

                PlanTask? last = tasks.Count > 0 ? tasks[^1] : null;
                if (last is not null && last.Path == p.Path && last.Vcn + last.Clusters == p.Vcn && last.TargetLcn + last.Clusters == target)
                {
                    tasks[^1] = last with { Clusters = last.Clusters + p.N };
                    sources[^1].Add((p.Lcn, p.N));
                }
                else
                {
                    tasks.Add(new PlanTask(p.Path, p.Vcn, p.N, target));
                    sources.Add([(p.Lcn, p.N)]);
                }
            }
        }

        if (sources.Any(s => s.Any(x => zoneStart < x.Lcn + x.N && x.Lcn < zoneStart + zoneClusters)))
        {
            tasks = OrderTasks(tasks, sources);
        }

        if (rel)
        {
            tasks = tasks.Select(t => t with { TargetLcn = t.TargetLcn - RelBase }).ToList();
        }

        long moved = tasks.Sum(t => t.Clusters);
        var stats = new ZoneStats(preservedSegments, preservedBytes, inPlaceBytes, overflowBytes, misaligned,
            flat.Sum(p => p.N) * cb, moved * cb, tasks.Count, tasks.Count == 0 ? 0 : PyFloat.Round2((double)moved / tasks.Count),
            curOff, skipped, CountRuns(flat.Select(p => (p.Lcn, p.N)).ToList()), CountRuns(final));
        return new AllocResult(tasks, stats, order);
    }
}
