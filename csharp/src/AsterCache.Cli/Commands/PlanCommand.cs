using System.Globalization;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Trace;
using AsterCache.Windows;

namespace AsterCache.Cli.Commands;

/// relayout/cli.py cmd_plan. 트레이스에서 계획을 만들고, --dry-run 이 아니면 --out 과 <out>.prefetch.json 을 씁니다.
internal static class PlanCommand
{
    public static readonly ArgOption[] Options =
    [
        new("trace", ArgKind.List, Required: true),
        new("origin-root", Required: true),
        new("zone-mib", Type: ArgType.Int, Required: true),
        new("zone-start-lcn", Default: "auto"),
        new("cluster-bytes", Type: ArgType.Int),
        new("preserve-mib", Type: ArgType.Int, Default: "0"),
        new("min-sessions", Type: ArgType.Int),
        new("out"),
        new("dry-run", ArgKind.Flag),
    ];

    private const long Mib = 1L << 20;

    public static int Run(ParsedArgs a, TextWriter o)
    {
        string originRoot = PathText.Str(a.Text("origin-root")!);
        int? queried = FileLayout.ClusterBytes(originRoot);
        long cb = queried is > 0 ? queried.Value : a.Int("cluster-bytes") ?? 0;
        if (cb == 0)
        {
            throw new PyValueException("cluster size unknown: pass --cluster-bytes");
        }

        List<List<TraceEvent>> sessions = a.List("trace").Select(t => TraceLoader.LoadRanges(t, originRoot, RealPaths.Resolve)).ToList();
        string startText = a.Text("zone-start-lcn")!;
        long? start = startText == "auto" ? null
            : long.TryParse(startText, NumberStyles.AllowLeadingSign | NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out long s) ? s
            : throw new PyValueException($"invalid literal for int() with base 10: {PyRepr.Str(startText)}");
        long minSessions = a.Int("min-sessions") is { } m and not 0 ? m : sessions.Count >= 2 ? 2 : 1;
        if (minSessions > sessions.Count)
        {
            throw new PyValueException($"--min-sessions {minSessions} exceeds the {sessions.Count} traces given");
        }

        PlanFile plan = PlanFile.Make(sessions, originRoot, a.Int("zone-mib")!.Value * Mib, cb, FileLayout.Extents, zoneStartLcn: start,
            minSessions: (int)minSessions, preserveBytes: a.Int("preserve-mib")!.Value * Mib);
        bool dry = a.Flag("dry-run");
        string? outPath = a.Text("out") is { } raw ? PathText.Str(raw) : null;
        string? prefetchPath = null;
        if (!dry)
        {
            if (outPath is null)
            {
                throw new PyValueException("--out is required unless --dry-run");
            }

            prefetchPath = PlanOutput.Save(plan, sessions, outPath);
        }

        JObject json = plan.ToJson();
        return AsterCli.Emit(o, new JObject
        {
            { "dry_run", dry },
            { "out", dry ? null : outPath },
            { "prefetch_trace", prefetchPath },
            { "min_sessions", minSessions },
            { "sessions", (long)sessions.Count },
            { "cluster_bytes", cb },
            { "zone", json["zone"] },
            { "stats", json["stats"] },
        });
    }
}
