using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Cli.Commands;

/// 읽기 전용 현황 (관리자 불필요): 권한, 환경변수, 그리고 --plan 의 현재 배치 검사 (verify 와 같음), --journal 의 진행 요약.
internal static class StatusCommand
{
    public static readonly ArgOption[] Options = [new("plan"), new("journal"), new("prewarm-status"), new("auto-status"), new("data-dir")];

    /// 드라이브의 디스크 조회 (시험에서 바꿈).
    internal static Func<string, DiskDetails> Disk { get; set; } = DiskInfo.Query;

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
            { "disks", Disks(Path.GetFullPath(a.Text("data-dir") is { } d ? PathText.Str(d) : SettingsStore.DefaultDataDir)) },
            { "prewarm", PrewarmStatus(a.Text("prewarm-status") ?? Path.Combine(SettingsStore.DefaultDataDir, "prewarm_status.json")) },
            { "auto", AutoStatus(a.Text("auto-status") ?? Path.Combine(SettingsStore.DefaultDataDir, "auto_status.json")) },
        });
    }

    // 등록된 게임이 놓인 드라이브별 디스크 종류 (같은 드라이브는 한 번), 설정이 없거나 읽지 못하면 null.
    private static object? Disks(string dataDir)
    {
        try
        {
            string config = Path.Combine(dataDir, "config.json");
            if (!File.Exists(config))
            {
                return null;
            }

            return SettingsStore.Load(config, Path.Combine(dataDir, "plans")).Games
                .Select(g => PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant()).Distinct()
                .Select(letter =>
                {
                    DiskDetails disk = Disk(letter);
                    return (object?)new JObject { { "drive", letter }, { "model", disk.Model }, { "kind", DiskInfo.Kind(disk.IncursSeekPenalty, disk.Trim) } };
                }).ToList();
        }
        catch (Exception e) when (e is SettingsException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // 서비스가 남긴 마지막 예열 결과 (게임별 시각·결과·바이트·초·MB/s·건너뜀 이유), 없으면 null.
    private static object? PrewarmStatus(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonRead.ReadFile(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return new JObject { { "error", "unreadable prewarm status" } };
        }
    }

    // 서비스가 남긴 자동 모드 상태 중 games·relocations 만, 없거나 읽지 못하면 null.
    private static object? AutoStatus(string path)
    {
        try
        {
            return File.Exists(path) && JsonRead.ReadFile(path) is JObject root
                ? new JObject { { "games", root.TryGetValue("games", out object? g) ? g : null }, { "relocations", root.TryGetValue("relocations", out object? r) ? r : null } }
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
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
