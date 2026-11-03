using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;

namespace AsterCache.Core.Apply;

/// relayout/apply.py 의 apply_plan, _execute, _run_tasks, _finish, _move_chunk, moved_ranges.
/// 취소 토큰은 조각 사이에서만 봅니다 (한 조각의 MOVE_FILE 은 끝까지 감). 취소되면 Stopped = "cancelled" 이고 다음 실행이 이어받습니다.
/// 저널은 이어하기 힌트일 뿐이고 진실은 매번 다시 조회한 extent 입니다. Execute 가 아니면 아무것도 옮기지 않고 저널도 쓰지 않습니다.
public static class Applier
{
    public const int MoveRetries = 3;

    /// 대상 클러스터가 이미 쓰이는 중이라 이동이 거부된 작업의 이유 (작업별 실패로 기록하고 다음 작업을 계속).
    public const string TargetBusy = "target busy";

    /// 이 횟수를 넘겨 연속으로 TargetBusy 면 존이 점유됐다고 보고 멈춥니다 (남은 작업을 하나씩 실패시키지 않음).
    public const int MaxBusyInARow = 20;

    /// 위 경우 report.Stopped 에 넣는 이유. 서비스는 이 문자열을 보면 계획을 다시 세웁니다.
    public const string ZoneOccupied = "zone region appears occupied, stopping";

    /// 이동 전후 비교 표본 규칙의 이름. 저널 header 의 digest_rule 이 없으면 옛 규칙 (파일 단위: 처음·가운데·끝 1 MiB + 옮긴 범위) 입니다.
    /// edge64k-v3 (새 저널): v2 와 같은 작업 단위 표본인데 verify=sample 이면 각 파일의 첫 작업과 인덱스가 4 의 배수인 작업만 읽음.
    /// edge64k-v2: 작업 단위 표본 (그 작업 범위의 앞뒤 64 KiB, 파일당 합계 1 MiB 상한) 을 모든 작업의 이동 직전과 직후에 읽음 (이어하기만 지원, verify=sample 이어도 전부).
    /// edge64k-v1: 파일 단위 같은 표본 (이어하기만 지원).
    public const string EdgeRule = "edge64k-v3";
    public const string EdgeTaskRule = "edge64k-v2";
    public const string EdgeFileRule = "edge64k-v1";

    /// verify=sample 에서 표본을 읽는 작업: 각 파일의 첫 작업과 작업 인덱스가 이 수의 배수인 작업.
    public const int SampleEvery = 4;

    /// planned 행을 미리 기록하는 작업 수: 이만큼의 from 을 한 번에 쓰고 fsync 한 뒤 이동합니다.
    public const int PlanAhead = 32;

    /// 새 규칙: 파일당 표본 합계 상한.
    public const long DigestCapBytes = 1 << 20;

    private const int ErrorAccessDenied = 5;
    private const int StatusAlreadyCommitted = unchecked((int)0xC0000021); // 때로 Win32 오류 5 로 보이지만 NTSTATUS 그대로 오기도 함

    /// 옮긴 범위마다 앞뒤 이만큼의 바이트를 SHA 표본에 넣습니다.
    public const long TaskSpan = 64 << 10;

    public static ApplyReport Apply(PlanFile plan, IVolume vol, IReadOnlyList<string> roots, string journalPath, ApplyHooks hooks,
        ApplyOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ApplyOptions();
        Func<string, long, IReadOnlyList<(long Offset, long Length)>, string> digest = hooks.Digest ?? throw new ArgumentException("hooks.Digest is required");
        Func<string, string?> skipReason = hooks.SkipReason ?? throw new ArgumentException("hooks.SkipReason is required");
        plan = PlanFile.FromJson(JsonRead.Parse(plan.Dumps())); // 계획 파일은 믿을 수 없는 입력: 형식 검증을 다시 통과해야 함
        PlanGuards.CheckPlan(plan, roots, vol.Letter, hooks);
        long cb = vol.ClusterBytes;
        if (cb != plan.ClusterBytes)
        {
            throw new PlanRejectedException($"cluster size {cb} != plan {plan.ClusterBytes}");
        }

        bool autoZone = plan.Zone.StartLcn is null;
        plan = PlanGuards.ResolveZone(plan, vol, hooks.Extents, journalPath, out bool rezoned);
        long chunk = Math.Max(1, options.ChunkBytes / cb);
        var rep = new ApplyReport();
        foreach (string p in plan.Tasks.Select(t => t.Path).Distinct(StringComparer.Ordinal).OrderBy(p => p, CodePointComparer.Instance))
        {
            if (skipReason(p) is { } why)
            {
                rep.SkippedFiles.Add(new(p, why));
            }
        }

        var skipped = rep.SkippedFiles.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var todo = new List<(int Idx, PlanTask Task, long Done)>();
        var sourceLcn = new Dictionary<int, long>();
        var sources = new List<(long A, long B, int Owner)>();
        bool bySource = options.Order == RelocateOrder.Source;
        for (int i = 0; i < plan.Tasks.Count; i++)
        {
            PlanTask t = plan.Tasks[i];
            IReadOnlyList<Extent>? ext = ApplyTimings.Time(ref rep.Timings.OpenTicks, () => hooks.Extents(t.Path));
            long d = PlanGuards.DonePrefix(ext, t.Vcn, t.Clusters, t.TargetLcn);
            if (!skipped.Contains(t.Path) && d < t.Clusters)
            {
                todo.Add((i, t, d));
                if (bySource)
                {
                    // 실행 순서만 바꿈: 대상 LCN 은 계획에 고정이고 저널·되돌리기는 작업 인덱스로 찾음. 원본을 모르면 맨 뒤.
                    var pieces = ZoneAllocator.Pieces(ext ?? [], t.Vcn + d, t.Vcn + t.Clusters);
                    sourceLcn[i] = pieces.Select(x => x.Lcn).DefaultIfEmpty(long.MaxValue).First();
                    sources.AddRange(pieces.Select(x => (x.Lcn, x.Lcn + x.N, i)));
                }
            }
        }

        if (bySource)
        {
            if (TargetsOverlapSources(todo, sources))
            {
                rep.OrderUsed = RelocateOrder.Target; // 어떤 작업의 대상이 다른 작업의 현재 원본이면 원본 순서가 이동을 막을 수 있음: 계획 순서 그대로
                rep.OrderFallbackReason = OverlapReason;
            }
            else
            {
                rep.OrderUsed = RelocateOrder.Source;
                todo = todo.OrderBy(x => sourceLcn[x.Idx]).ThenBy(x => x.Idx).ToList();
            }
        }

        rep.ZoneStartLcn = plan.Zone.StartLcn;
        rep.TasksTotal = plan.Tasks.Count;
        rep.TasksTodo = todo.Count;
        rep.BytesTodo = todo.Sum(x => (x.Task.Clusters - x.Done) * cb);
        rep.Executed = options.Execute;
        bool persist = options.SaveTo is not null && (autoZone || rezoned) && (options.Execute || options.DryRun); // 정한 존은 이어하기를 위해 남아야 함
        rep.ZoneRetargeted = rezoned;
        if (rezoned && (persist || options.Execute))
        {
            rep.JournalArchivedTo = ArchiveJournal(journalPath, hooks); // 새 계획은 다른 저널 header 를 쓰므로, 끝난 저널은 되돌리기 기록으로 옆에 보관
        }

        if (persist)
        {
            plan.Save(options.SaveTo!);
        }

        rep.DryRun = options.DryRun;
        rep.PlanSaved = persist;
        return options.Execute ? Execute(plan, vol, todo, rep, journalPath, chunk, options.WriteCap, hooks, digest, Math.Max(1, options.PlanAhead),
            VerifyMode.IsValid(options.Verify) ? options.Verify : VerifyMode.Sample, cancellationToken) : rep;
    }

    /// order=source 를 요청했는데 target 순서로 실행한 이유 (보고서의 order_fallback_reason).
    public const string OverlapReason = "zone overlaps current extents";

    /// 어떤 작업의 남은 대상 범위가 다른 작업의 현재 원본 조각과 겹치는가. 원본 조각을 시작순으로 정렬해 접두 최대 끝 (서로 다른 소유자 둘까지) 로 훑음: O(n log n).
    internal static bool TargetsOverlapSources(List<(int Idx, PlanTask Task, long Done)> todo, List<(long A, long B, int Owner)> sources)
    {
        sources.Sort((x, y) => x.A.CompareTo(y.A));
        long[] starts = sources.Select(s => s.A).ToArray();
        var best1 = new (long End, int Owner)[sources.Count + 1]; // [k]: 앞 k 개 중 가장 먼 끝과 그 소유자
        var best2 = new long[sources.Count + 1]; // [k]: best1 과 소유자가 다른 조각 중 가장 먼 끝
        best1[0] = (long.MinValue, -1);
        best2[0] = long.MinValue;
        for (int k = 0; k < sources.Count; k++)
        {
            (long end, int owner) = (sources[k].B, sources[k].Owner);
            (long End, int Owner) b1 = best1[k];
            long b2 = best2[k];
            if (owner == b1.Owner)
            {
                b1 = (Math.Max(b1.End, end), owner);
            }
            else if (end > b1.End)
            {
                b2 = b1.End;
                b1 = (end, owner);
            }
            else
            {
                b2 = Math.Max(b2, end);
            }

            best1[k + 1] = b1;
            best2[k + 1] = b2;
        }

        foreach ((int idx, PlanTask t, long done) in todo)
        {
            long lo = t.TargetLcn + done;
            long hi = t.TargetLcn + t.Clusters;
            int k = Array.BinarySearch(starts, hi);
            k = k < 0 ? ~k : k;
            while (k > 0 && starts[k - 1] >= hi) // 시작이 hi 이상인 조각은 후보가 아님 (같은 시작이 여럿일 때 BinarySearch 의 위치가 임의)
            {
                k--;
            }

            (long End, int Owner) b = best1[k];
            if ((b.Owner != idx ? b.End : best2[k]) > lo)
            {
                return true;
            }
        }

        return false;
    }

    /// 새 규칙의 표본: 범위 (이미 각 작업의 처음·끝 TaskSpan 바이트) 를 파일 안으로 자르고 정렬해, 합계가 DigestCapBytes 를 넘으면 고르게 솎습니다. 같은 입력이면 항상 같은 결과.
    public static List<(long Offset, long Length)> EdgeRanges(IReadOnlyList<(long Offset, long Length)> ranges, long size)
    {
        List<(long Offset, long Length)> all = ranges.Where(r => r.Offset >= 0 && r.Offset < size && r.Length > 0)
            .Select(r => (r.Offset, Math.Min(r.Length, size - r.Offset))).Distinct().OrderBy(r => r.Item1).ThenBy(r => r.Item2).ToList();
        if (all.Count == 0 || all.Sum(r => r.Length) <= DigestCapBytes)
        {
            return all;
        }

        long keep = Math.Max(1, DigestCapBytes / all.Max(r => r.Length));
        return keep >= all.Count ? all : Enumerable.Range(0, (int)keep).Select(i => all[(int)(i * (long)all.Count / keep)]).ToList();
    }

    // 끝난 저널을 <이름>.<UTC 시각> 으로 옮깁니다. 저널이 없으면 null.
    private static string? ArchiveJournal(string journalPath, ApplyHooks hooks)
    {
        if (!File.Exists(journalPath))
        {
            return null;
        }

        string stamp = DateTime.UnixEpoch.AddSeconds(hooks.Clock()).ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        string dest = $"{journalPath}.{stamp}";
        File.Move(journalPath, dest);
        return dest;
    }

    /// path → [(바이트 오프셋, 길이)]: 모든 작업의 VCN 범위 앞뒤 TaskSpan 바이트. SHA 표본에 들어갑니다.
    public static Dictionary<string, List<(long Offset, long Length)>> MovedRanges(PlanFile plan)
    {
        long cb = plan.ClusterBytes;
        var result = new Dictionary<string, List<(long Offset, long Length)>>(StringComparer.Ordinal);
        foreach (PlanTask t in plan.Tasks)
        {
            long lo = t.Vcn * cb;
            long hi = (t.Vcn + t.Clusters) * cb;
            long n = Math.Min(TaskSpan, hi - lo);
            if (!result.TryGetValue(t.Path, out List<(long Offset, long Length)>? list))
            {
                result[t.Path] = list = [];
            }

            list.Add((lo, n));
            list.Add((hi - n, n));
        }

        return result;
    }

    /// 한 번에 옮기는 조각. 실패하면 MoveRetries 번까지 다시 시도하고, 끝내 실패하면 마지막 오류 문구를 돌려줍니다 (성공이면 null).
    internal static string? MoveChunk(IVolume vol, string path, long vcn, long targetLcn, long off, long clusters, Action<TimeSpan> sleep) =>
        MoveChunk(vol, path, vcn, targetLcn, off, clusters, sleep, out _);

    /// busy: 마지막 실패가 대상 클러스터가 이미 쓰이는 중이라는 오류 (ERROR_ACCESS_DENIED / STATUS_ALREADY_COMMITTED) 였는가.
    internal static string? MoveChunk(IVolume vol, string path, long vcn, long targetLcn, long off, long clusters, Action<TimeSpan> sleep, out bool busy)
    {
        string? err = null;
        busy = false;
        for (int attempt = 0; attempt < MoveRetries; attempt++)
        {
            try
            {
                vol.Move(path, vcn + off, targetLcn + off, checked((uint)clusters));
                return null;
            }
            catch (Exception e) when (e is Win32Exception or IOException)
            {
                err = e.Message;
                busy = e is Win32Exception { NativeErrorCode: ErrorAccessDenied or StatusAlreadyCommitted };
                if (attempt + 1 < MoveRetries)
                {
                    sleep(TimeSpan.FromSeconds(0.2));
                }
            }
        }

        return err;
    }

    private static ApplyReport Execute(PlanFile plan, IVolume vol, List<(int Idx, PlanTask Task, long Done)> todo, ApplyReport rep,
        string journalPath, long chunk, long writeCap, ApplyHooks hooks, Func<string, long, IReadOnlyList<(long Offset, long Length)>, string> digestFn,
        int planAhead, string verify, CancellationToken ct)
    {
        ApplyTimings tm = rep.Timings;
        using var jr = new Journal(journalPath);
        var before = new OrderedDictionary<string, string?>(StringComparer.Ordinal);
        Dictionary<string, List<(long Offset, long Length)>> ranges = MovedRanges(plan);

        string psha = plan.Sha();
        JObject? header = jr.Rows.Count > 0 ? jr.First("header", "plan_sha", psha) : null;
        if (jr.Rows.Count > 0 && header is null)
        {
            throw new PlanRejectedException("journal belongs to a different plan");
        }

        // 표본 규칙은 저널을 처음 만든 실행이 정함: 이어하기는 저널의 sha_before 와 같은 규칙으로 다시 계산해야 비교가 유효 (digest_rule 이 없는 옛 저널 = 옛 규칙)
        object? rule = null;
        _ = header?.TryGetValue("digest_rule", out rule);
        bool sampling = jr.Rows.Count == 0 || Equals(rule, EdgeRule); // v3: 일부 작업만 읽을 수 있음
        bool perTask = sampling || Equals(rule, EdgeTaskRule);
        bool edge = perTask || Equals(rule, EdgeFileRule);
        var firstOf = plan.Tasks.Select((t, i) => (t.Path, i)).GroupBy(x => x.Path, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Min(x => x.i), StringComparer.Ordinal);
        string? SampledDigest(int idx, PlanTask t) =>
            verify == VerifyMode.Sample && sampling && idx % SampleEvery != 0 && firstOf[t.Path] != idx ? null : TaskDigest(idx, t);
        var edgeFn = hooks.EdgeDigest ?? digestFn;
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        var capped = new Dictionary<string, HashSet<(long, long)>>(StringComparer.Ordinal);
        long SizeOf(string p) => sizes.TryGetValue(p, out long z) ? z : sizes[p] = hooks.FileSize(p);
        IReadOnlyList<(long Offset, long Length)> RangesOf(string p) => ranges.TryGetValue(p, out List<(long, long)>? x) ? x : [];
        string Digest(string p)
        {
            long size = SizeOf(p);
            return edge ? edgeFn(p, size, EdgeRanges(RangesOf(p), size)) : digestFn(p, size, RangesOf(p));
        }

        // 작업 단위: 파일의 상한 적용된 표본 중 이 작업의 앞뒤 구간에 해당하는 것만. 없으면 null (읽을 것이 없음).
        string? TaskDigest(int idx, PlanTask t)
        {
            string p = t.Path;
            long size = SizeOf(p);
            if (!capped.TryGetValue(p, out HashSet<(long, long)>? keep))
            {
                capped[p] = keep = EdgeRanges(RangesOf(p), size).ToHashSet();
            }

            long cb0 = vol.ClusterBytes;
            long lo = t.Vcn * cb0;
            long hi = (t.Vcn + t.Clusters) * cb0;
            long n = Math.Min(TaskSpan, hi - lo);
            var mine = new[] { (lo, n), (hi - n, n) }.Where(r => r.Item1 >= 0 && r.Item1 < size).Select(r => (r.Item1, Math.Min(r.Item2, size - r.Item1)))
                .Where(keep.Contains).Distinct().ToList();
            return mine.Count == 0 ? null : edgeFn(p, size, mine);
        }

        if (jr.Rows.Count == 0)
        {
            ApplyTimings.Time(ref tm.JournalTicks, () => jr.AddBatched(new JObject
            {
                { "type", "header" },
                { "plan_sha", psha },
                { "zone_start_lcn", plan.Zone.StartLcn },
                { "cluster_bytes", (long)vol.ClusterBytes },
                { "started", hooks.Clock() },
                { "digest_rule", EdgeRule },
            }));
        }

        try
        {
            RunTasks(vol, todo, rep, jr, before, chunk, writeCap, hooks, Digest, perTask ? SampledDigest : null, planAhead, verify == VerifyMode.Off, ct);
        }
        catch (Exception)
        {
            try
            {
                Finish(rep, jr, before, perTask ? k => TaskDigest(TaskOf(k), plan.Tasks[TaskOf(k)]) : Digest, perTask ? k => plan.Tasks[TaskOf(k)].Path : k => k, strict: false);
            }
            catch (Exception)
            {
                // 원래 오류를 가리지 않음
            }

            throw;
        }

        ApplyReport done = Finish(rep, jr, before, perTask ? k => TaskDigest(TaskOf(k), plan.Tasks[TaskOf(k)]) : Digest, perTask ? k => plan.Tasks[TaskOf(k)].Path : k => k, strict: true);
        tm.Stop();
        return done;
    }

    // 작업 단위 표본의 키: "t<idx>"
    private static string TaskKey(int idx) => "t" + idx.ToString(CultureInfo.InvariantCulture);

    private static int TaskOf(string key) => int.Parse(key.AsSpan(1), CultureInfo.InvariantCulture);

    // 쓴 양은 예외가 나도 end 행에 남도록 rep.MovedBytes 에 계속 누적합니다.
    private static void RunTasks(IVolume vol, List<(int Idx, PlanTask Task, long Done)> todo, ApplyReport rep, Journal jr,
        OrderedDictionary<string, string?> before, long chunk, long writeCap, ApplyHooks hooks, Func<string, string> digest,
        Func<int, PlanTask, string?>? taskDigest, int planAhead, bool noVerify, CancellationToken ct)
    {
        long cb = vol.ClusterBytes;
        ApplyTimings tm = rep.Timings;
        int busyInARow = 0;
        int plannedUpTo = 0;
        var fresh = new HashSet<int>(); // 이번 실행이 planned 행을 쓴 작업
        for (int n = 0; n < todo.Count; n++)
        {
            (int i, PlanTask t, long d) = todo[n];
            if (ct.IsCancellationRequested) // 다음 작업으로 넘어가기 전에도 확인: 취소된 뒤에는 새 단계의 planned 행을 남기지 않음
            {
                rep.Stopped = "cancelled";
                return;
            }

            string p = t.Path;
            if (n >= plannedUpTo)
            {
                PlanRows(todo, n, jr, hooks, tm, fresh, planAhead); // 되돌리기의 근거 (from) 는 이동 전에 디스크에 있어야 함: PlanAhead 개를 한 번에 쓰고 fsync
                plannedUpTo = n + planAhead;
            }

            if (d == 0 && !fresh.Contains(i) && jr.First("step", "idx", (long)i) is { } planned)
            {
                // 앞선 실행이 미리 기록만 하고 시작하지 않은 작업: 이번 실행의 단계로 세도록 같은 from 을 새로 남김 (자동 재배치의 되돌리기는 이번 실행이 쓴 줄만 봄)
                ApplyTimings.Time(ref tm.JournalTicks, () => jr.AddBatched(new JObject
                {
                    { "type", "step" },
                    { "idx", (long)i },
                    { "state", "resumed" },
                    { "path", p },
                    { "vcn", t.Vcn },
                    { "clusters", t.Clusters },
                    { "target_lcn", t.TargetLcn },
                    { "from", planned["from"] },
                }));
            }

            if (!noVerify && taskDigest is null && !before.ContainsKey(p))
            {
                JObject? row = jr.First("file", "path", p);
                before[p] = row is null ? ApplyTimings.Time(ref tm.DigestBeforeTicks, () => digest(p)) : row["sha_before"] as string;
                if (row is null)
                {
                    ApplyTimings.Time(ref tm.JournalTicks, () => jr.AddBatched(new JObject { { "type", "file" }, { "path", p }, { "sha_before", before[p] } }));
                }
            }
            if (!noVerify && taskDigest is not null && !before.ContainsKey(TaskKey(i)))
            {
                // 이 작업의 이동 직전에 그 범위의 가장자리만 읽음 (헤드가 원본 위치에 한 번만 감). 이어하기는 저널에 적힌 값을 씀.
                JObject? row = jr.First("sha", "idx", (long)i);
                string? sha = row is null ? ApplyTimings.Time(ref tm.DigestBeforeTicks, () => taskDigest(i, t)) : row["sha_before"] as string;
                if (sha is not null)
                {
                    before[TaskKey(i)] = sha;
                    if (row is null)
                    {
                        ApplyTimings.Time(ref tm.JournalTicks, () => jr.AddBatched(new JObject { { "type", "sha" }, { "idx", (long)i }, { "sha_before", sha } }));
                    }
                }
            }

            long off = d;
            string? err = null;
            bool busy = false;
            while (off < t.Clusters && err is null)
            {
                if (ct.IsCancellationRequested) // 진행 중이던 조각은 이미 끝났음: 저널의 planned 행이 재개 지점 (DonePrefix 가 실제 extent 로 다시 계산)
                {
                    rep.Stopped = "cancelled";
                    return;
                }

                long c = Math.Min(chunk, t.Clusters - off);
                if (rep.MovedBytes + (c * cb) > writeCap)
                {
                    rep.Stopped = "write_cap";
                    return;
                }

                long t0 = Stopwatch.GetTimestamp();
                err = MoveChunk(vol, p, t.Vcn, t.TargetLcn, off, c, hooks.Sleep, out busy);
                tm.MoveTicks += Stopwatch.GetTimestamp() - t0;
                if (err is null)
                {
                    off += c;
                    rep.MovedBytes += c * cb;
                }
            }

            bool ok = err is null && PlanGuards.DonePrefix(ApplyTimings.Time(ref tm.OpenTicks, () => hooks.Extents(p)), t.Vcn, t.Clusters, t.TargetLcn) == t.Clusters;
            bool isBusy = err is not null && busy;
            string? reason = ok ? null : isBusy ? TargetBusy : err ?? "extent_mismatch";
            ApplyTimings.Time(ref tm.JournalTicks, () => jr.AddBatched(new JObject { { "type", "step" }, { "idx", (long)i }, { "state", ok ? "done" : "failed" }, { "reason", reason } }));
            tm.Tasks++;
            if (!ok)
            {
                rep.Failed.Add(new FailedStep(i, p, reason!));
            }

            busyInARow = isBusy ? busyInARow + 1 : 0;
            if (busyInARow > MaxBusyInARow)
            {
                rep.Stopped = ZoneOccupied;
                return;
            }
        }
    }

    // todo[from..] 의 PlanAhead 개 중 planned 행이 없는 것을 쓰고 fsync. 맨 처음 from 을 지킴: 이어하기가 덮어쓰면 안 됨.
    private static void PlanRows(List<(int Idx, PlanTask Task, long Done)> todo, int from, Journal jr, ApplyHooks hooks, ApplyTimings tm, HashSet<int> fresh, int planAhead)
    {
        for (int n = from; n < Math.Min(todo.Count, from + planAhead); n++)
        {
            (int i, PlanTask t, _) = todo[n];
            if (jr.First("step", "idx", (long)i) is not null)
            {
                continue;
            }

            var src = ZoneAllocator.Pieces(ApplyTimings.Time(ref tm.OpenTicks, () => hooks.Extents(t.Path)) ?? [], t.Vcn, t.Vcn + t.Clusters)
                .Select(x => (object?)new List<object?> { x.Vcn, x.Lcn, x.N }).ToList();
            _ = fresh.Add(i);
            ApplyTimings.Time(ref tm.JournalTicks, () => jr.AddBatched(new JObject
            {
                { "type", "step" },
                { "idx", (long)i },
                { "state", "planned" },
                { "path", t.Path },
                { "vcn", t.Vcn },
                { "clusters", t.Clusters },
                { "target_lcn", t.TargetLcn },
                { "from", src },
            }));
        }

        ApplyTimings.Time(ref tm.JournalTicks, jr.Flush);
    }

    /// 건드린 모든 파일의 SHA 표본을 다시 비교합니다. 달라졌으면 크게 알리고 (strict) 절대 삼키지 않습니다. end 행은 어느 경우든 기록.
    private static ApplyReport Finish(ApplyReport rep, Journal jr, OrderedDictionary<string, string?> before, Func<string, string?> digest, Func<string, string> pathOf, bool strict)
    {
        ApplyTimings tm = rep.Timings;
        var bad = before.Where(kv => ApplyTimings.Time(ref tm.DigestAfterTicks, () => digest(kv.Key)) != kv.Value).Select(kv => pathOf(kv.Key)).Distinct().ToList();
        int files = before.Select(kv => pathOf(kv.Key)).Distinct().Count();
        rep.ShaChecked = files;
        tm.Files = files;
        rep.ShaMismatch = bad;
        ApplyTimings.Time(ref tm.JournalTicks, () =>
        {
            jr.Add(new JObject
            {
                { "type", "end" },
                { "stopped", rep.Stopped },
                { "written_bytes", rep.MovedBytes },
                { "sha_mismatch", bad.Cast<object?>().ToList() },
            });
            jr.Close();
        });
        tm.JournalFlushes = jr.Flushes;
        return bad.Count > 0 && strict ? throw new ShaMismatchException(bad) : rep;
    }
}
