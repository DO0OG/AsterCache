using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Windows;

namespace AsterCache.Cli.Commands;

/// 읽기 전용 현황 (관리자 불필요): 권한, 환경변수, 그리고 --plan 의 현재 배치 검사 (verify 와 같음), --journal 의 진행 요약.
internal static class StatusCommand
{
    public static readonly ArgOption[] Options = [new("plan"), new("journal")];

    public static int Run(ParsedArgs a, TextWriter o)
    {
        object? plan = a.Given("plan") ? VolumeCommands.Verdict(LayoutVerifier.Verify(PlanFile.Load(a.Text("plan")!), FileLayout.Extents)) : null;
        object? journal = null;
        if (a.Given("journal"))
        {
            using var jr = new Journal(a.Text("journal")!);
            journal = Summarize(jr);
        }

        return AsterCli.Emit(o, new JObject
        {
            { "admin", Elevation.IsAdmin() },
            { "aster_trace", PrefetchCommand.EnvOrNull("ASTER_TRACE") },
            { "aster_root", PrefetchCommand.EnvOrNull("ASTER_ROOT") },
            { "plan", plan },
            { "journal", journal },
        });
    }

    private static JObject Summarize(Journal jr)
    {
        var steps = jr.Rows.Where(r => Equals(r.TryGetValue("type", out object? t) ? t : null, "step") && r.ContainsKey("from")).Select(r => (long)r["idx"]!).Distinct().ToList();
        List<string?> states = steps.Select(jr.LastState).ToList();
        return new JObject
        {
            { "rows", (long)jr.Rows.Count },
            { "steps", (long)steps.Count },
            { "done", (long)states.Count(s => s == "done") },
            { "failed", (long)states.Count(s => s == "failed") },
            { "ended", jr.Rows.Any(r => Equals(r.TryGetValue("type", out object? t) ? t : null, "end")) },
        };
    }
}
