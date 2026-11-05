using System.Globalization;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.I18n;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Trace;
using AsterCache.Windows;

namespace AsterCache.Cli.Commands;

/// relayout/cli.py cmd_plan. 트레이스에서 계획을 만들고, --dry-run 이 아니면 --out 과 <out>.prefetch.json 을 씁니다.
/// --journal 을 주고 그 저널에 행이 있으면 활성 계획을 덮어쓰지 않고 잔여 재계획 (ResidualReplan) 으로 바꿉니다: 이미 옮긴 범위는 고정하고 저널은 보관.
/// --from-plan 은 트레이스 대신 그 계획의 Order 를 후보로 써서 같은 잔여 재계획을 합니다 (--trace, --origin-root, --zone-mib 불필요, --out 기본값은 그 계획 자신).
internal static class PlanCommand
{
    public static readonly ArgOption[] Options =
    [
        new("trace", ArgKind.List),
        new("origin-root"),
        new("zone-mib", Type: ArgType.Int),
        new("zone-start-lcn", Default: "auto"),
        new("cluster-bytes", Type: ArgType.Int),
        new("preserve-mib", Type: ArgType.Int, Default: "0"),
        new("min-sessions", Type: ArgType.Int),
        new("out"),
        new("dry-run", ArgKind.Flag),
        new("journal"),
        new("from-plan"),
    ];

    private const long Mib = 1L << 20;

    public static int Run(ParsedArgs a, TextWriter o)
    {
        string? fromPlan = a.Text("from-plan") is { } fp ? PathText.Str(fp) : null;
        string[] missing = fromPlan is null ? new[] { "trace", "origin-root", "zone-mib" }.Where(n => !a.Given(n)).Select(n => "--" + n).ToArray() : [];
        if (missing.Length > 0)
        {
            throw new ArgException(L.Get(a.Lang, "Cli_Arg_Required", string.Join(", ", missing)));
        }

        List<List<TraceEvent>> sessions = [];
        long minSessions = 0;
        long cb;
        PlanFile plan;
        if (fromPlan is null)
        {
            string originRoot = PathText.Str(a.Text("origin-root")!);
            int? queried = FileLayout.ClusterBytes(originRoot);
            cb = queried is > 0 ? queried.Value : a.Int("cluster-bytes") ?? 0;
            if (cb == 0)
            {
                throw new PyValueException("cluster size unknown: pass --cluster-bytes");
            }

            sessions = a.List("trace").Select(t => TraceLoader.LoadRanges(t, originRoot, RealPaths.Resolve)).ToList();
            string startText = a.Text("zone-start-lcn")!;
            long? start = startText == "auto" ? null
                : long.TryParse(startText, NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out long s) ? s
                : throw new PyValueException($"invalid literal for int() with base 10: {PyRepr.Str(startText)}");
            minSessions = a.Int("min-sessions") is { } m and not 0 ? m : sessions.Count >= 2 ? 2 : 1;
            if (minSessions > sessions.Count)
            {
                throw new PyValueException($"--min-sessions {minSessions} exceeds the {sessions.Count} traces given");
            }

            plan = PlanFile.Make(sessions, originRoot, a.Int("zone-mib")!.Value * Mib, cb, FileLayout.Extents, zoneStartLcn: start,
                minSessions: (int)minSessions, preserveBytes: a.Int("preserve-mib")!.Value * Mib);
        }
        else
        {
            plan = PlanFile.Load(fromPlan);
            cb = plan.ClusterBytes;
        }

        bool dry = a.Flag("dry-run");
        string? outPath = a.Text("out") is { } raw ? PathText.Str(raw) : fromPlan;
        string? journal = a.Text("journal") is { } jr ? PathText.Str(jr) : null;
        string? prefetchPath = null;
        SwitchResult? replaced = null;
        if (!dry)
        {
            if (outPath is null)
            {
                throw new PyValueException("--out is required unless --dry-run");
            }

            // 활성 계획을 저장·교체하는 구간 전체 (저널 행 확인 포함) 를 서비스와 같은 잠금 안에서: 안쪽 Replan 의 잠금은 재진입
            using DataLock gate = DataLock.TryAcquire(outPath, journalPath: journal ?? outPath + ".journal.jsonl") ?? throw new PlanRejectedException(DataLock.BusyMessage);
            if (fromPlan is not null || (journal is not null && HasRows(journal)))
            {
                (plan, replaced) = Replan(plan, fromPlan, outPath, journal ?? outPath + ".journal.jsonl", sessions);
                prefetchPath = outPath + PlanOutput.PrefetchSuffix;
            }
            else
            {
                prefetchPath = PlanOutput.Save(plan, sessions, outPath);
            }
        }

        JObject json = plan.ToJson();
        var result = new JObject
        {
            { "dry_run", dry },
            { "out", dry ? null : outPath },
            { "prefetch_trace", prefetchPath },
            { "min_sessions", minSessions },
            { "sessions", (long)sessions.Count },
            { "cluster_bytes", cb },
            { "zone", json["zone"] },
            { "stats", json["stats"] },
        };
        if (replaced is not null)
        {
            // 잔여 재계획을 한 경우에만 (기존 출력은 그대로)
            result.Add("fixed_clusters", json["stats"] is JObject st && st.TryGetValue("fixed_clusters", out object? fc) && fc is long fixedClusters ? fixedClusters : 0L);
            result.Add("archived_journal", replaced.ArchivedJournal);
        }

        return AsterCli.Emit(o, result);
    }

    // 저널에 행이 하나라도 있나 (읽지 못하면 예외: 상태를 모른 채 활성 계획을 덮어쓰지 않음).
    private static bool HasRows(string journal)
    {
        using var jr = new Journal(journal);
        return jr.Rows.Count > 0;
    }

    // 후보를 잔여 재계획해 활성 계획으로 승격 (저장은 PlanOutput.Save 와 같음, 트레이스가 없으면 계획만). 예약은 같은 폴더의 다른 게임 계획.
    // fromPlan 이 있으면 후보는 그 파일이라 잠금을 잡은 뒤 다시 읽음 (기다리는 동안 바뀐 계획을 옛 후보로 덮어쓰지 않음). 선읽기 파일은 세션이 없어도 승격할 계획의 Order 로 새로 씀.
    private static (PlanFile Plan, SwitchResult Switch) Replan(PlanFile candidate, string? fromPlan, string outPath, string journal, IReadOnlyList<List<TraceEvent>> sessions)
    {
        using DataLock gate = DataLock.TryAcquire(outPath, journalPath: journal) ?? throw new PlanRejectedException(DataLock.BusyMessage); // 예약 조회부터 전환까지 서비스와 같은 잠금
        if (fromPlan is not null)
        {
            candidate = PlanFile.Load(fromPlan);
        }

        (IVolume vol, ApplyHooks hooks) = VolumeCommands.Open(candidate.Volume, null);
        using IVolume volume = vol;
        IReadOnlyList<(long Lcn, long Clusters)> reserved = ZoneReservations.Collect(Path.GetDirectoryName(Path.GetFullPath(outPath))!, candidate.Volume, outPath);
        Action<PlanFile, string> save = (p, path) => _ = PlanOutput.Save(p, sessions, path);
        return ResidualReplan.Run(candidate, outPath, journal, volume, reserved, hooks, () => DateTime.UtcNow, save, []);
    }
}
