using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Model;

namespace AsterCache.Core.Plan;

/// 부분 적용된 계획의 잔여 재계획: 이미 이전 목표 자리에 놓인 조각은 "현재 LCN 이 목표인 고정 task" 로 두고 (이동 0), 나머지만 새 빈 구간에 이어 붙입니다.
/// 판정은 저널의 done 표시가 아니라 파일의 현재 extent 로 합니다 (저널·옛 계획은 이전 목표의 힌트일 뿐).
public static class ResidualPlanner
{
    /// 계획 stats 의 보류 파일 {경로: 사유}: task 가 없고 옮기지 않으며 계획에 남음.
    public const string HeldKey = "held_files";

    public const string UnreadableReason = "unreadable";

    /// 계획 stats 의 보류 파일의 이전 목표 목록 (task 와 같은 모양의 행): 다음 잔여 재계획이 이전 목표로 읽음.
    public const string HeldTargetsKey = "held_targets";

    private readonly record struct Part(string Path, long Vcn, long Lcn, long N, bool Fixed);

    /// 저널의 planned 행 (path, vcn, clusters, target_lcn) 과 옛 계획들의 tasks 에서 이전 목표 목록 (중복 제거, 저널 먼저).
    /// 저널이 없거나 모양이 틀린 행은 건너뜁니다. 저널 파일을 읽지 못하면 (IOException, 중간 줄 손상 PyValueException) 그대로 던져 호출자가 재시도 대기로 남기게 합니다:
    /// 이전 목표를 모르고 계획하면 이미 옮긴 데이터를 다시 옮기게 됩니다. 존 시작이 없는 옛 계획의 목표는 상대 오프셋이라 쓰지 않습니다.
    public static IReadOnlyList<PlanTask> PreviousTargets(string? journalPath, IEnumerable<PlanFile> oldPlans)
    {
        var seen = new HashSet<PlanTask>();
        var result = new List<PlanTask>();
        if (journalPath is not null && File.Exists(journalPath))
        {
            using var jr = new Journal(journalPath);
            foreach (JObject r in jr.Rows)
            {
                if (r.TryGetValue("type", out object? t) && Equals(t, "step") && r.TryGetValue("state", out object? s) && Equals(s, "planned")
                    && r.TryGetValue("path", out object? p) && p is string path && r.TryGetValue("vcn", out object? v) && v is long vcn && vcn >= 0
                    && r.TryGetValue("clusters", out object? c) && c is long clusters && clusters > 0
                    && r.TryGetValue("target_lcn", out object? g) && g is long target && target >= 0)
                {
                    PlanTask task = new(path, vcn, clusters, target);
                    if (seen.Add(task))
                    {
                        result.Add(task);
                    }
                }
            }
        }

        foreach (PlanFile plan in oldPlans.Where(x => x.Zone.StartLcn is not null))
        {
            foreach (PlanTask task in plan.Tasks.Concat(HeldTargets(plan)).Where(x => seen.Add(x)))
            {
                result.Add(task);
            }
        }

        return result;
    }

    // 보류 파일의 이전 목표: 읽지 못한 동안 task 가 없어도, 읽히게 된 뒤 다시 계획할 때 이미 옮긴 조각을 알아보게 계획에 남겨 둠.
    private static IEnumerable<PlanTask> HeldTargets(PlanFile plan)
    {
        if (plan.Stats.TryGetValue(HeldTargetsKey, out object? raw) && raw is List<object?> rows)
        {
            foreach (JObject r in rows.OfType<JObject>())
            {
                if (r.TryGetValue("path", out object? p) && p is string path && r.TryGetValue("vcn", out object? v) && v is long vcn && vcn >= 0
                    && r.TryGetValue("clusters", out object? c) && c is long clusters && clusters > 0 && r.TryGetValue("target_lcn", out object? g) && g is long target && target >= 0)
                {
                    yield return new PlanTask(path, vcn, clusters, target);
                }
            }
        }
    }

    /// candidate 의 Order (현재 핫 범위), Fingerprints, 기타 stats 를 그대로 쓰고 tasks·zone·stats 만 다시 만듭니다.
    /// 반환: 고정 task (현재 LCN == 이전 목표, TargetLcn = 현재 LCN) + 잔여 task (findRun 이 준 새 구간에 접근 순서대로 연속 배치), 접근 순서대로 섞여 있음.
    /// 잔여가 없으면 findRun 을 부르지 않습니다. extents 를 읽을 수 없는 (null) 파일은 order·지문에 남기고 task 만 만들지 않은 채 stats.held_files 에 "unreadable" 로 적습니다:
    /// 고정할 근거도 옮길 근거도 없는 보류이고, 호출자는 held_files 가 비어 있지 않은 동안 완료로 확정하지 말고 다시 계획해야 합니다. 조회 API 가 일시 실패와 희소·압축 같은 영구 사유를 구분하지 못해 둘 다 같은 보류입니다.
    /// 디스크 extent 가 없는 (빈 목록: 파일 크기 0 이나 MFT 상주) 파일은 옮길 것이 없어 기존 계획 규칙대로 order·지문·task 에서 빼고 skipped_files 에 셉니다.
    public static PlanFile Build(PlanFile candidate, IReadOnlyList<PlanTask> previous, Func<string, IReadOnlyList<Extent>?> extents, Func<long, long?> findRun)
    {
        long cb = candidate.ClusterBytes;
        Dictionary<(string Path, long Delta), List<(long A, long B)>> prior = Prior(previous);
        var ext = new Dictionary<string, IReadOnlyList<Extent>?>(StringComparer.Ordinal);
        var order = new List<PlanRange>();
        var parts = new List<Part>();
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var held = new JObject();
        foreach (PlanRange o in candidate.Order)
        {
            if (!ext.TryGetValue(o.Path, out IReadOnlyList<Extent>? e))
            {
                ext[o.Path] = e = extents(o.Path);
            }

            if (e is null)
            {
                if (!held.ContainsKey(o.Path))
                {
                    held.Add(o.Path, UnreadableReason);
                }

                order.Add(o);
                continue;
            }

            if (e.Count == 0)
            {
                _ = dropped.Add(o.Path);
                continue;
            }

            order.Add(o);
            foreach ((long v, long lcn, long n) in ZoneAllocator.Pieces(e, o.Vcn, o.Vcn + o.Clusters))
            {
                Split(o.Path, v, lcn, n, prior, parts);
            }
        }

        long residual = parts.Where(p => !p.Fixed).Sum(p => p.N);
        long? run = residual > 0 ? findRun(residual) ?? throw new PlanRejectedException($"no free run of {residual} clusters") : null;

        var tasks = new List<PlanTask>();
        var lastFixed = new List<bool>();
        var final = new List<(long Lcn, long N)>();
        long off = 0, fixedClusters = 0, misaligned = 0;
        foreach (Part p in parts)
        {
            long target = p.Fixed ? p.Lcn : run!.Value + off;
            if (p.Fixed)
            {
                fixedClusters += p.N;
            }
            else
            {
                off += p.N;
                if ((target - p.Lcn) % PlanGuards.ZoneAlign != 0)
                {
                    misaligned++;
                }
            }

            final.Add((target, p.N));
            if (tasks.Count > 0 && lastFixed[^1] == p.Fixed && tasks[^1].Path == p.Path && tasks[^1].Vcn + tasks[^1].Clusters == p.Vcn
                && tasks[^1].TargetLcn + tasks[^1].Clusters == target)
            {
                tasks[^1] = tasks[^1] with { Clusters = tasks[^1].Clusters + p.N };
            }
            else
            {
                tasks.Add(new PlanTask(p.Path, p.Vcn, p.N, target));
                lastFixed.Add(p.Fixed);
            }
        }

        int moving = lastFixed.Count(x => !x);
        long moved = tasks.Where((_, i) => !lastFixed[i]).Sum(t => t.Clusters);
        long skipped = (candidate.Stats.TryGetValue("skipped_files", out object? sk) && sk is long s ? s : 0) + dropped.Count;
        JObject stats = new ZoneStats(0, 0, fixedClusters * cb, 0, misaligned, parts.Sum(p => p.N) * cb, moved * cb, moving,
            moving == 0 ? 0 : PyFloat.Round2((double)moved / moving), residual, skipped, ZoneAllocator.CountRuns(parts.Select(p => (p.Lcn, p.N)).ToList()),
            ZoneAllocator.CountRuns(final)).ToJson();
        foreach ((string key, object? value) in candidate.Stats.Where(kv => !stats.ContainsKey(kv.Key) && kv.Key != HeldKey && kv.Key != HeldTargetsKey)) // 이전 계획의 보류는 이번 판정으로 다시 정함
        {
            stats.Add(key, value); // hot_blocks, units 와 호출자가 덧붙인 표지는 그대로
        }

        if (held.Count > 0)
        {
            stats.Add(HeldKey, held);
            stats.Add(HeldTargetsKey, previous.Where(t => held.ContainsKey(t.Path)).Distinct().Select(t => (object?)new JObject { { "path", t.Path }, { "vcn", t.Vcn }, { "clusters", t.Clusters }, { "target_lcn", t.TargetLcn } }).ToList());
        }

        stats.Add("fixed_clusters", fixedClusters);
        stats.Add("residual", 1L);
        // 존 시작은 null 이면 안 됨: ResolveZone 이 빈 구간을 다시 찾고 모든 목표를 밀어 고정 task 를 망가뜨림. 잔여가 없으면 첫 task 의 목표 (없으면 0) 로 정한 값일 뿐 의미 없음.
        long start = run ?? (tasks.Count > 0 ? tasks[0].TargetLcn : 0);
        return candidate with
        {
            Zone = new PlanZone(start, residual),
            Tasks = tasks,
            Order = order,
            Fingerprints = candidate.Fingerprints.Where(f => !dropped.Contains(f.Path)).ToList(),
            Stats = stats,
        };
    }

    // (path, 목표 - vcn) 별로 합친 VCN 구간: 같은 오프셋 (delta) 이면 조각의 현재 LCN 이 이전 목표와 같은 것.
    private static Dictionary<(string Path, long Delta), List<(long A, long B)>> Prior(IReadOnlyList<PlanTask> previous)
    {
        var raw = new Dictionary<(string Path, long Delta), List<(long A, long B)>>();
        foreach (PlanTask t in previous.Where(x => x.Clusters > 0))
        {
            (string, long) key = (t.Path, t.TargetLcn - t.Vcn);
            if (!raw.TryGetValue(key, out List<(long A, long B)>? list))
            {
                raw[key] = list = [];
            }

            list.Add((t.Vcn, t.Vcn + t.Clusters));
        }

        foreach (List<(long A, long B)> list in raw.Values)
        {
            list.Sort();
            int w = 0;
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].A <= list[w].B)
                {
                    list[w] = (list[w].A, Math.Max(list[w].B, list[i].B));
                }
                else
                {
                    list[++w] = list[i];
                }
            }

            list.RemoveRange(w + 1, list.Count - w - 1);
        }

        return raw;
    }

    // 조각 [v, v+n) (현재 LCN lcn) 을 이전 목표와 일치하는 부분 (고정) 과 나머지 (잔여) 로 나눠 VCN 순서로 덧붙임.
    private static void Split(string path, long v, long lcn, long n, Dictionary<(string Path, long Delta), List<(long A, long B)>> prior, List<Part> parts)
    {
        long cur = v, end = v + n;
        if (prior.TryGetValue((path, lcn - v), out List<(long A, long B)>? iv))
        {
            int i = 0, hi = iv.Count;
            while (i < hi) // 첫 구간: 끝이 cur 뒤인 것
            {
                int mid = (i + hi) / 2;
                if (iv[mid].B > cur)
                {
                    hi = mid;
                }
                else
                {
                    i = mid + 1;
                }
            }

            for (; i < iv.Count && iv[i].A < end; i++)
            {
                long a = Math.Max(cur, iv[i].A), b = Math.Min(end, iv[i].B);
                if (a > cur)
                {
                    parts.Add(new Part(path, cur, lcn + cur - v, a - cur, false));
                }

                parts.Add(new Part(path, a, lcn + a - v, b - a, true));
                cur = b;
            }
        }

        if (cur < end)
        {
            parts.Add(new Part(path, cur, lcn + cur - v, end - cur, false));
        }
    }
}
