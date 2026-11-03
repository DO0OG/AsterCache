using System.Diagnostics;
using AsterCache.Core;
using AsterCache.Core.Discovery;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Profiles;
using AsterCache.Core.Settings;
using AsterCache.Core.Tier;
using AsterCache.Core.Trace;
using AsterCache.Windows;

namespace AsterCache.Cli.Commands;

/// aster tier status|apply|revert|clean: SSD 보조 저장 (선택 기능, auto.ssd_tier). 링크를 만들고 지우는 apply·revert·clean 은 관리자 전용
/// (서비스가 같은 일을 자동으로 합니다). 데이터 폴더의 tier 아래 저널 (<게임>.json) 이 기준입니다. 원본은 `.aster-orig` 로 남고, 사본은 clean 에서만 지웁니다.
internal static class TierCommand
{
    public static readonly ArgOption[] StatusOptions = [new("data-dir")];

    public static readonly ArgOption[] ApplyOptions = [new("game", Required: true), new("data-dir")];

    public static readonly ArgOption[] RevertOptions = [new("game"), new("all", ArgKind.Flag), new("data-dir")];

    public static readonly ArgOption[] CleanOptions = [new("data-dir")];

    /// 시험이 바꿔 끼우는 바깥 세계: 관리자 판정, 파일 시스템, 'X:' 가 HDD 인가, 게임 실행 여부, 안티치트, 시스템 드라이브.
    internal static Func<bool> IsAdmin { get; set; } = Elevation.IsAdmin;

    internal static Func<ITierFs> Fs { get; set; } = () => new TierFs(new IoPriorityHint(_ => { }));

    internal static Func<string, bool?> IsHdd { get; set; } = letter => DiskInfo.Query(letter).IncursSeekPenalty;

    internal static Func<GameSettings, bool> Running { get; set; } = g => g.ExeNames.Any(e => Process.GetProcessesByName(Path.GetFileNameWithoutExtension(e)).Length > 0);

    internal static Func<GameSettings, string?> AntiCheatOf { get; set; } = g => g.AntiCheat ?? DetectAntiCheat(g.OriginRoot);

    internal static Func<string> SystemDrive { get; set; } = () => Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))![..2].ToUpperInvariant();

    /// 사본 폴더 선택 (시험이 바꿔 끼움: 시험 임시 폴더는 HDD 와 SSD 판정을 따로 줄 수 없어서).
    internal static Func<SsdTierSettings, ITierFs, TierRootChoice> ResolveRoot { get; set; } = (t, fs) => TierRoot.Resolve(t, SystemDrive(), fs.FixedDrives(), IsHdd, fs.FreeBytes);

    public static int Status(ParsedArgs a, TextWriter o)
    {
        (string dataDir, TierEngine engine) = Open(a);
        AsterSettings? s = TryLoad(dataDir);
        SsdTierSettings? t = s?.Auto.SsdTier;
        TierRootChoice? choice = t is null ? null : ResolveRoot(t, engine.Fs);
        return AsterCli.Emit(o, new JObject
        {
            { "enabled", s is null ? null : s.Auto.Enabled && t!.Enabled },
            { "budget_gib", t is null ? null : (long)t.BudgetGib },
            { "root", choice?.Root },
            { "root_error", choice?.Why },
            { "games", engine.Status().Select(g => (object?)Row(g)).ToList() },
        });
    }

    public static int Apply(ParsedArgs a, TextWriter o)
    {
        RequireAdmin();
        (string dataDir, TierEngine engine) = Open(a);
        AsterSettings s = SettingsStore.Load(Path.Combine(dataDir, "config.json"), Path.Combine(dataDir, "plans"));
        string name = a.Text("game")!;
        GameSettings g = s.Games.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) ?? throw new PyValueException($"game not registered: {name}");
        SsdTierSettings t = s.Auto.SsdTier;
        if (!s.Auto.Enabled || !t.Enabled)
        {
            return Fail(o, "ssd_tier_disabled");
        }

        string letter = PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant();
        if (letter == SystemDrive())
        {
            return Fail(o, "system_volume");
        }

        if (IsHdd(letter) != true)
        {
            return Fail(o, "not_an_hdd");
        }

        if (Running(g))
        {
            return Fail(o, "game_running");
        }

        if (AntiCheatOf(g) is { } cheat)
        {
            return Fail(o, "anti_cheat", cheat);
        }

        TierRootChoice choice = ResolveRoot(t, engine.Fs);
        if (choice.Root is not { } root)
        {
            return Fail(o, "no_ssd_root", choice.Why);
        }

        string plans = Path.Combine(dataDir, "plans");
        string trace = new[] { Path.Combine(plans, g.Name + ".trace.json"), Path.Combine(plans, g.Name + GameProfile.BootTraceSuffix) }.FirstOrDefault(File.Exists) ?? string.Empty;
        if (trace.Length == 0)
        {
            return Fail(o, "no_trace");
        }

        TierSelection pick = TierSelector.Select(TraceLoader.LoadRanges(trace, g.OriginRoot, RealPaths.Resolve), (long)t.BudgetGib << 30, g.AllowedRoots, p => engine.Fs.Stat(p)?.Length);
        TierApplyReport rep = engine.Apply(g, root, t.BudgetGib, pick, CancellationToken.None);
        return AsterCli.Emit(o, new JObject
        {
            { "game", g.Name },
            { "root", root },
            { "selected", (long)pick.Files.Count },
            { "selected_bytes", pick.MovedBytes },
            { "applied", (long)rep.Applied },
            { "copied_bytes", rep.CopiedBytes },
            { "skipped", rep.Skipped.Select(x => (object?)new JObject { { "path", x.Path }, { "reason", x.Reason } }).ToList() },
            { "stopped", rep.Stopped },
        }, rep.Stopped is null ? 0 : 1);
    }

    public static int Revert(ParsedArgs a, TextWriter o)
    {
        if (a.Flag("all") == a.Given("game"))
        {
            throw new ArgException("exactly one of --game or --all is required");
        }

        RequireAdmin();
        (_, TierEngine engine) = Open(a);
        IEnumerable<string> games = a.Flag("all") ? engine.Games() : [a.Text("game")!];
        var rows = new List<object?>();
        int reverted = 0, failed = 0;
        foreach (string game in games)
        {
            TierRevertReport r = engine.Revert(game, a.Flag("all") ? "revert --all" : "revert");
            reverted += r.Reverted;
            failed += r.Failed + (r.Corrupt ? 1 : 0);
            rows.Add(new JObject { { "game", game }, { "reverted", (long)r.Reverted }, { "stale", (long)r.Stale }, { "failed", (long)r.Failed }, { "corrupt", r.Corrupt } });
        }

        return AsterCli.Emit(o, new JObject { { "reverted", (long)reverted }, { "failed", (long)failed }, { "games", rows } }, failed == 0 ? 0 : 1);
    }

    public static int Clean(ParsedArgs a, TextWriter o)
    {
        RequireAdmin();
        (_, TierEngine engine) = Open(a);
        (int files, long bytes) = engine.Clean();
        return AsterCli.Emit(o, new JObject { { "deleted_files", (long)files }, { "freed_bytes", bytes } });
    }

    private static void RequireAdmin()
    {
        if (!IsAdmin())
        {
            throw new NotAdminException();
        }
    }

    private static (string DataDir, TierEngine Engine) Open(ParsedArgs a)
    {
        string dataDir = Path.GetFullPath(a.Text("data-dir") is { } d ? PathText.Str(d) : SettingsStore.DefaultDataDir);
        return (dataDir, new TierEngine(Fs(), new ConsoleLog(), Path.Combine(dataDir, "tier")));
    }

    private static AsterSettings? TryLoad(string dataDir)
    {
        try
        {
            string config = Path.Combine(dataDir, "config.json");
            return File.Exists(config) ? SettingsStore.Load(config, Path.Combine(dataDir, "plans")) : null;
        }
        catch (Exception e) when (e is SettingsException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static JObject Row(TierGameInfo g) => new()
    {
        { "game", g.Game },
        { "root", g.Root },
        { "active", (long)g.Active },
        { "stale", (long)g.Stale },
        { "reverted", (long)g.Reverted },
        { "active_bytes", g.ActiveBytes },
        { "copy_bytes", g.CopyBytes },
        { "corrupt", g.Corrupt },
    };

    private static int Fail(TextWriter o, string error, string? detail = null) =>
        AsterCli.Emit(o, detail is null ? new JObject { { "error", error } } : new JObject { { "error", error }, { "detail", detail } }, 1);

    private static string? DetectAntiCheat(string dir)
    {
        try
        {
            return AntiCheat.Detect(LauncherScan.ListNames(dir, LauncherScan.AntiCheatDepth));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // 표준 출력은 한 줄 JSON 이라 로그는 표준 오류로 (경고 이상만).
    private sealed class ConsoleLog : ILog
    {
        public void Write(LogLevel level, string message, Exception? exception = null)
        {
            if (level >= LogLevel.Warn)
            {
                Console.Error.WriteLine($"[{level.ToString().ToUpperInvariant()}] {message}");
            }
        }
    }
}
