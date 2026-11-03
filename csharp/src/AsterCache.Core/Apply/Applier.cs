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
        for (int i = 0; i < plan.Tasks.Count; i++)
        {
            PlanTask t = plan.Tasks[i];
            long d = PlanGuards.DonePrefix(ApplyTimings.Time(ref rep.Timings.OpenTicks, () => hooks.Extents(t.Path)), t.Vcn, t.Clusters, t.TargetLcn);
            if (!skipped.Contains(t.Path) && d < t.Clusters)
            {
                todo.Add((i, t, d));
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
        return options.Execute ? Execute(plan, vol, todo, rep, journalPath, chunk, options.WriteCap, hooks, digest, cancellationToken) : rep;
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
        CancellationToken ct)
    {
        ApplyTimings tm = rep.Timings;
        using var jr = new Journal(journalPath);
        var before = new OrderedDictionary<string, string?>(StringComparer.Ordinal);
        Dictionary<string, List<(long Offset, long Length)>> ranges = MovedRanges(plan);

        // 매 실행이 같은 범위를 보므로 이어하기 실행은 저널에 적힌 before 값을 그대로 쓸 수 있음
        string Digest(string p) => digestFn(p, hooks.FileSize(p), ranges.TryGetValue(p, out List<(long, long)>? r) ? r : []);

        string psha = plan.Sha();
        if (jr.Rows.Count > 0 && jr.First("header", "plan_sha", psha) is null)
        {
            throw new PlanRejectedException("journal belongs to a different plan");
        }

        if (jr.Rows.Count == 0)
        {
            ApplyTimings.Time(ref tm.JournalTicks, () => jr.Add(new JObject
            {
                { "type", "header" },
                { "plan_sha", psha },
                { "zone_start_lcn", plan.Zone.StartLcn },
                { "cluster_bytes", (long)vol.ClusterBytes },
                { "started", hooks.Clock() },
            }));
        }

        try
        {
            RunTasks(vol, todo, rep, jr, before, chunk, writeCap, hooks, Digest, ct);
        }
        catch (Exception)
        {
            try
            {
                Finish(rep, jr, before, Digest, strict: false);
            }
            catch (Exception)
            {
                // 원래 오류를 가리지 않음
            }

            throw;
        }

        ApplyReport done = Finish(rep, jr, before, Digest, strict: true);
        tm.Stop();
        return done;
    }

    // 쓴 양은 예외가 나도 end 행에 남도록 rep.MovedBytes 에 계속 누적합니다.
    private static void RunTasks(IVolume vol, List<(int Idx, PlanTask Task, long Done)> todo, ApplyReport rep, Journal jr,
        OrderedDictionary<string, string?> before, long chunk, long writeCap, ApplyHooks hooks, Func<string, string> digest, CancellationToken ct)
    {
        long cb = vol.ClusterBytes;
        ApplyTimings tm = rep.Timings;
        int busyInARow = 0;
        foreach ((int i, PlanTask t, long d) in todo)
        {
            if (ct.IsCancellationRequested) // 다음 작업으로 넘어가기 전에도 확인: 취소된 뒤에는 새 단계의 planned 행을 남기지 않음
            {
                rep.Stopped = "cancelled";
                return;
            }

            string p = t.Path;
            if (!before.ContainsKey(p))
            {
                JObject? row = jr.First("file", "path", p);
                before[p] = row is null ? ApplyTimings.Time(ref tm.DigestBeforeTicks, () => digest(p)) : row["sha_before"] as string;
                if (row is null)
                {
                    ApplyTimings.Time(ref tm.JournalTicks, () => jr.Add(new JObject { { "type", "file" }, { "path", p }, { "sha_before", before[p] } }));
                }
            }

            if (jr.First("step", "idx", (long)i) is null) // 맨 처음 from 을 지킴: 이어하기가 덮어쓰면 안 됨
            {
                var src = ZoneAllocator.Pieces(ApplyTimings.Time(ref tm.OpenTicks, () => hooks.Extents(p)) ?? [], t.Vcn, t.Vcn + t.Clusters)
                    .Select(x => (object?)new List<object?> { x.Vcn, x.Lcn, x.N }).ToList();
                ApplyTimings.Time(ref tm.JournalTicks, () => jr.Add(new JObject
                {
                    { "type", "step" },
                    { "idx", (long)i },
                    { "state", "planned" },
                    { "path", p },
                    { "vcn", t.Vcn },
                    { "clusters", t.Clusters },
                    { "target_lcn", t.TargetLcn },
                    { "from", src },
                }));
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
            ApplyTimings.Time(ref tm.JournalTicks, () => jr.Add(new JObject { { "type", "step" }, { "idx", (long)i }, { "state", ok ? "done" : "failed" }, { "reason", reason } }));
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

    /// 건드린 모든 파일의 SHA 표본을 다시 비교합니다. 달라졌으면 크게 알리고 (strict) 절대 삼키지 않습니다. end 행은 어느 경우든 기록.
    private static ApplyReport Finish(ApplyReport rep, Journal jr, OrderedDictionary<string, string?> before, Func<string, string> digest, bool strict)
    {
        ApplyTimings tm = rep.Timings;
        var bad = before.Where(kv => ApplyTimings.Time(ref tm.DigestAfterTicks, () => digest(kv.Key)) != kv.Value).Select(kv => kv.Key).ToList();
        rep.ShaChecked = before.Count;
        tm.Files = before.Count;
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
        return bad.Count > 0 && strict ? throw new ShaMismatchException(bad) : rep;
    }
}
