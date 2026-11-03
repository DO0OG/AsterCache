using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;

namespace AsterCache.Core.Settings;

/// %ProgramData%\AsterCache\config.json 읽기와 검증. 설정 파일은 신뢰 경계라 알려진 키만, 정확한 형으로만 받습니다.
/// plan·trace 는 plansDir (관리자·SYSTEM 전용 ACL 폴더) 안에 있어야 하고, origin_root 는 allowed_roots 중 하나 안에 있어야 합니다.
public static class SettingsStore
{
    public const int DefaultPollMs = 50;
    public const int DefaultReapplyMinutes = 1440;
    public const int DefaultMaxStreamMib = 4096;
    public const int DefaultMaxZoneMib = 2048;
    public const string DefaultSource = "manual";

    /// 프로세스 시작 감지: "poll" (기본, 지연이 짧음) 또는 "etw" (관리자, 실시간 세션이라 지연이 더 길 수 있음).
    public const string DefaultWatcher = "poll";

    /// 화면·명령줄 언어: "auto" (OS 표시 언어를 따름, 기본), "ko", "en".
    public const string DefaultLanguage = "auto";
    public const long MaxFileBytes = 1 << 20;
    public const int MaxGames = 32;

    private static readonly string[] TopKeys = ["games", "poll_ms", "watcher", "language", "auto", "max_zone_mib"];
    private static readonly string[] GameKeys =
        ["name", "exe_names", "trace", "plan", "origin_root", "allowed_roots", "volume_label", "reapply_interval_minutes", "prewarm", "max_stream_mib", "source", "anticheat"];
    private static readonly string[] AutoKeys =
    [
        "enabled", "discover", "trace", "relocate", "prewarm_recent", "idle_minutes", "trace_cap_seconds", "trace_cap_mib",
        "max_relocations_per_day", "trace_runs", "trace_merge_min_mib", "ignore_exes", "rewarm_minutes", "idle_seconds", "learn", "full_prewarm", "full_prewarm_max_mib", "engine_profiles", "shared_profiles",
        "ssd_tier", "post_update_delay_min", "recovery_max_min", "recovery_hard_max_min", "relocate_chunk_mib", "relocate_parallel", "relocate_io_priority", "relocate_verify", "relocate_order", "max_relocate_gib_per_day", "post_relocate_recovery", "keep_awake", "keep_stream_on_game_start", "load_boundary", "restream_cooldown_min", "rewarm_after_exit_min", "startup_warm_games", "startup_warm_max_mib",
    ];
    private static readonly string[] PrewarmKeys = ["on_service_start", "trigger_exes", "min_free_ram_mib", "cooldown_min", "pin"];
    private static readonly string[] LearnKeys = ["enabled", "window_mib", "max_total_mib", "mode", "saturation_mbps"];
    private static readonly string[] SharedProfilesKeys = ["enabled", "url"];
    private static readonly string[] SsdTierKeys = ["enabled", "budget_gib", "root"];
    private const int MaxUrlChars = 2048;

    public static string DefaultDataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AsterCache");

    public static AsterSettings Load(string configPath, string plansDir)
    {
        if (new FileInfo(configPath).Length > MaxFileBytes)
        {
            throw new SettingsException("config: file too large");
        }

        try
        {
            return Parse(JsonRead.ReadFile(configPath), plansDir);
        }
        catch (PyValueException e)
        {
            throw new SettingsException("config: " + e.Message);
        }
    }

    /// 언어만 느슨하게 읽습니다 (명령줄·트레이가 시작할 때 쓰며, 다른 칸이 잘못돼도 언어는 읽음). "ko"/"en" 이 아니거나 읽지 못하면 null.
    public static string? ReadLanguage(string configPath)
    {
        try
        {
            return File.Exists(configPath) && new FileInfo(configPath).Length <= MaxFileBytes && JsonRead.ReadFile(configPath) is JObject o
                && o.TryGetValue("language", out object? v) && v is "ko" or "en" ? (string)v : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
    }

    public static AsterSettings Parse(string json, string plansDir)
    {
        try
        {
            return Parse(JsonRead.Parse(json), plansDir);
        }
        catch (PyValueException e)
        {
            throw new SettingsException("config: " + e.Message);
        }
    }

    private static AsterSettings Parse(object? root, string plansDir)
    {
        JObject top = root as JObject ?? throw new SettingsException("config: object expected");
        Unknown(top, TopKeys, "config");
        string watcher = DefaultWatcher;
        if (top.TryGetValue("watcher", out object? w))
        {
            watcher = w is "etw" or "poll" ? (string)w : throw new SettingsException("watcher: \"etw\" or \"poll\" expected");
        }

        string language = DefaultLanguage;
        if (top.TryGetValue("language", out object? lang))
        {
            language = lang is "auto" or "ko" or "en" ? (string)lang : throw new SettingsException("language: \"auto\", \"ko\" or \"en\" expected");
        }

        AutoSettings auto = top.TryGetValue("auto", out object? au) ? ParseAuto(au) : AutoSettings.Default;
        bool zoneAuto = top.TryGetValue("max_zone_mib", out object? mz) && mz is "auto";
        int maxZone = top.ContainsKey("max_zone_mib") && !zoneAuto ? Int(top["max_zone_mib"], "max_zone_mib", 64, 65536) : DefaultMaxZoneMib;
        int poll = top.ContainsKey("poll_ms") ? Int(top["poll_ms"], "poll_ms", 50, 5000) : DefaultPollMs;
        if (!top.TryGetValue("games", out object? g) || g is not List<object?> games || games.Count > MaxGames)
        {
            throw new SettingsException($"games: list of 0..{MaxGames} expected"); // 빈 목록은 정상: 서비스는 대기만 함
        }

        string plans = FullPath(plansDir, "plans_dir");
        var result = new List<GameSettings>();
        for (int i = 0; i < games.Count; i++)
        {
            result.Add(ParseGame(games[i], $"games[{i}]", plans));
        }

        CheckUnique(result.Select(x => x.Name), "games: duplicate name");
        CheckUnique(result.SelectMany(x => x.ExeNames), "games: exe name used twice");
        return new AsterSettings(poll, result, watcher, language) { Auto = auto, MaxZoneMib = maxZone, MaxZoneAuto = zoneAuto };
    }

    private static AutoSettings ParseAuto(object? v)
    {
        JObject o = v as JObject ?? throw new SettingsException("auto: object expected");
        Unknown(o, AutoKeys, "auto");
        AutoSettings d = AutoSettings.Default;
        List<string> ignore = [];
        if (o.TryGetValue("ignore_exes", out object? ig))
        {
            if (ig is not List<object?> list || list.Count > 64)
            {
                throw new SettingsException("auto.ignore_exes: list of 0..64 expected");
            }

            ignore = list.Select((e, k) => ExeName(e, $"auto.ignore_exes[{k}]")).ToList();
            CheckUnique(ignore, "auto.ignore_exes: duplicate");
        }

        LearnSettings learn = o.TryGetValue("learn", out object? ln) ? ParseLearn(ln) : LearnSettings.Default;
        int recent = OptInt(o, "prewarm_recent", -1, 32, d.PrewarmRecent);
        int idleMin = OptInt(o, "idle_minutes", 1, 1440, d.IdleMinutes);
        // beta.3 트레이가 항상 쓰던 기본 (1, 3 분) 은 새 기본으로 해석 (파일은 다음 저장 때 새 형식으로 바뀜)
        bool migrated = !o.ContainsKey("learn") && o.ContainsKey("prewarm_recent") && o.ContainsKey("idle_minutes") && recent == 1 && idleMin == 3;
        if (migrated)
        {
            recent = d.PrewarmRecent;
        }

        return new AutoSettings(
            OptBool(o, "enabled", d.Enabled), OptBool(o, "discover", d.Discover), OptBool(o, "trace", d.Trace), OptBool(o, "relocate", d.Relocate),
            recent, idleMin,
            OptInt(o, "trace_cap_seconds", 30, 7200, d.TraceCapSeconds), OptInt(o, "trace_cap_mib", 64, 65536, d.TraceCapMib),
            OptInt(o, "max_relocations_per_day", 0, 24, d.MaxRelocationsPerDay), ignore)
        {
            TraceRuns = OptInt(o, "trace_runs", 1, 32, d.TraceRuns),
            TraceMergeMinMib = OptInt(o, "trace_merge_min_mib", 1, 65536, d.TraceMergeMinMib),
            RewarmMinutes = OptInt(o, "rewarm_minutes", 0, 1440, d.RewarmMinutes),
            RestreamCooldownMin = OptInt(o, "restream_cooldown_min", 0, 1440, d.RestreamCooldownMin),
            RewarmAfterExitMin = OptInt(o, "rewarm_after_exit_min", 0, 1440, d.RewarmAfterExitMin),
            StartupWarmGames = OptInt(o, "startup_warm_games", 0, 32, d.StartupWarmGames),
            StartupWarmMaxMib = OptInt(o, "startup_warm_max_mib", 1, 1048576, d.StartupWarmMaxMib),
            PostUpdateDelayMin = OptInt(o, "post_update_delay_min", 0, 1440, d.PostUpdateDelayMin),
            RecoveryMaxMin = OptInt(o, "recovery_max_min", 1, 1440, d.RecoveryMaxMin),
            RecoveryHardMaxMin = OptInt(o, "recovery_hard_max_min", 1, 1440, d.RecoveryHardMaxMin),
            PostRelocateRecovery = o.TryGetValue("post_relocate_recovery", out object? prr) && prr is AutoSettings.RecoveryProbe or AutoSettings.RecoveryFixed or AutoSettings.RecoveryOff ? (string)prr : d.PostRelocateRecovery,
            KeepAwake = OptBool(o, "keep_awake", d.KeepAwake),
            KeepStreamOnGameStart = OptBool(o, "keep_stream_on_game_start", d.KeepStreamOnGameStart),
            LoadBoundary = OptBool(o, "load_boundary", d.LoadBoundary),
            IdleSeconds = OptInt(o, "idle_seconds", 10, 86400, d.IdleSeconds),
            IdleSecondsSet = o.ContainsKey("idle_seconds"),
            IdleMinutesSet = o.ContainsKey("idle_minutes") && !migrated,
            Migrated = migrated,
            Learn = learn,
            SharedProfiles = o.TryGetValue("shared_profiles", out object? sp) ? ParseSharedProfiles(sp) : SharedProfilesSettings.Default,
            FullPrewarm = OptBool(o, "full_prewarm", d.FullPrewarm),
            FullPrewarmMaxMib = OptInt(o, "full_prewarm_max_mib", 0, 1048576, d.FullPrewarmMaxMib),
            EngineProfiles = OptBool(o, "engine_profiles", d.EngineProfiles),
            RelocateChunkMib = OptInt(o, "relocate_chunk_mib", 1, 256, d.RelocateChunkMib),
            RelocateParallel = OptInt(o, "relocate_parallel", 1, 8, d.RelocateParallel),
            RelocateParallelSet = o.ContainsKey("relocate_parallel"),
            RelocateIoPriority = o.TryGetValue("relocate_io_priority", out object? io) && io is AutoSettings.IoLow or AutoSettings.IoNormal ? (string)io : d.RelocateIoPriority,
            RelocateVerify = o.TryGetValue("relocate_verify", out object? rv) && rv is string rvs && VerifyMode.IsValid(rvs) ? rvs : d.RelocateVerify,
            RelocateOrder = o.TryGetValue("relocate_order", out object? ro) && ro is string ros && RelocateOrder.IsValidSetting(ros) ? ros : d.RelocateOrder,
            MaxRelocateGibPerDay = OptInt(o, "max_relocate_gib_per_day", 0, 1048576, d.MaxRelocateGibPerDay),
            MaxRelocateGibPerDaySet = o.ContainsKey("max_relocate_gib_per_day"),
            SsdTier = o.TryGetValue("ssd_tier", out object? tier) ? ParseSsdTier(tier) : SsdTierSettings.Default,
        };
    }

    private static LearnSettings ParseLearn(object? v)
    {
        JObject o = v as JObject ?? throw new SettingsException("auto.learn: object expected");
        Unknown(o, LearnKeys, "auto.learn");
        LearnSettings d = LearnSettings.Default;
        bool enabled = o.TryGetValue("enabled", out object? b) ? b as bool? ?? throw new SettingsException("auto.learn.enabled: true or false expected") : d.Enabled;
        int window = o.TryGetValue("window_mib", out object? wm) ? Int(wm, "auto.learn.window_mib", 1, 1024) : d.WindowMib;
        int total = o.TryGetValue("max_total_mib", out object? mt) ? Int(mt, "auto.learn.max_total_mib", 0, 65536) : d.MaxTotalMib;
        string mode = o.TryGetValue("mode", out object? m) ? m is LearnSettings.StreamMode or LearnSettings.GapMode or LearnSettings.BoundedMode or LearnSettings.AdaptiveMode ? (string)m : throw new SettingsException("auto.learn.mode: \"stream\", \"gap\", \"bounded\" or \"adaptive\" expected") : d.Mode;
        int saturation = o.TryGetValue("saturation_mbps", out object? sm) ? Int(sm, "auto.learn.saturation_mbps", 1, 10000) : d.SaturationMbps;
        return new LearnSettings(enabled, window, total) { Mode = mode, SaturationMbps = saturation };
    }

    private static SharedProfilesSettings ParseSharedProfiles(object? v)
    {
        JObject o = v as JObject ?? throw new SettingsException("auto.shared_profiles: object expected");
        Unknown(o, SharedProfilesKeys, "auto.shared_profiles");
        SharedProfilesSettings d = SharedProfilesSettings.Default;
        bool enabled = o.TryGetValue("enabled", out object? b) ? b as bool? ?? throw new SettingsException("auto.shared_profiles.enabled: true or false expected") : d.Enabled;
        string url = o.TryGetValue("url", out object? u)
            ? u is string s && s.Length <= MaxUrlChars && Uri.TryCreate(s, UriKind.Absolute, out Uri? uri) && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0
                ? s
                : throw new SettingsException("auto.shared_profiles.url: https URL expected")
            : d.Url;
        return new SharedProfilesSettings(enabled, url);
    }

    private static SsdTierSettings ParseSsdTier(object? v)
    {
        JObject o = v as JObject ?? throw new SettingsException("auto.ssd_tier: object expected");
        Unknown(o, SsdTierKeys, "auto.ssd_tier");
        SsdTierSettings d = SsdTierSettings.Default;
        bool enabled = o.TryGetValue("enabled", out object? b) ? b as bool? ?? throw new SettingsException("auto.ssd_tier.enabled: true or false expected") : d.Enabled;
        int budget = o.TryGetValue("budget_gib", out object? g) ? Int(g, "auto.ssd_tier.budget_gib", 1, 4096) : d.BudgetGib;
        string root = d.Root;
        if (o.TryGetValue("root", out object? r) && r is not SsdTierSettings.AutoRoot)
        {
            root = FullPath(r, "auto.ssd_tier.root");
            if (SamePath(root, Path.GetPathRoot(root)!))
            {
                throw new SettingsException("auto.ssd_tier.root: a whole drive is too broad");
            }
        }

        return new SsdTierSettings(enabled, budget, root);
    }

    private static bool OptBool(JObject o, string key, bool dflt) =>
        o.TryGetValue(key, out object? v) ? v as bool? ?? throw new SettingsException($"auto.{key}: true or false expected") : dflt;

    private static int OptInt(JObject o, string key, int min, int max, int dflt) =>
        o.TryGetValue(key, out object? v) ? Int(v, $"auto.{key}", min, max) : dflt;

    private static GameSettings ParseGame(object? v, string at, string plansDir)
    {
        JObject g = v as JObject ?? throw new SettingsException($"{at}: object expected");
        Unknown(g, GameKeys, at);
        string name = Str(Need(g, "name", at), $"{at}.name");
        if (name.Length > 64 || !name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
        {
            throw new SettingsException($"{at}.name: 1..64 chars of letters, digits, . _ -");
        }

        if (Need(g, "exe_names", at) is not List<object?> exes || exes.Count == 0 || exes.Count > 16)
        {
            throw new SettingsException($"{at}.exe_names: list of 1..16 expected");
        }

        List<string> exeNames = exes.Select((e, k) => ExeName(e, $"{at}.exe_names[{k}]")).ToList();
        CheckUnique(exeNames, $"{at}.exe_names: duplicate");
        string trace = FullPath(Need(g, "trace", at), $"{at}.trace");
        string plan = FullPath(Need(g, "plan", at), $"{at}.plan");
        foreach ((string p, string field) in new[] { (trace, "trace"), (plan, "plan") })
        {
            if (!p.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || !PlanGuards.Inside(p, [plansDir], Path.GetFullPath) || SamePath(p, plansDir))
            {
                throw new SettingsException($"{at}.{field}: must be a .json file inside the plans folder");
            }
        }

        string origin = FullPath(Need(g, "origin_root", at), $"{at}.origin_root");
        if (Need(g, "allowed_roots", at) is not List<object?> roots || roots.Count == 0 || roots.Count > 16)
        {
            throw new SettingsException($"{at}.allowed_roots: list of 1..16 expected");
        }

        List<string> allowed = roots.Select((r, k) => FullPath(r, $"{at}.allowed_roots[{k}]")).ToList();
        string drive = PlanFile.SplitDrive(origin).ToUpperInvariant();
        for (int k = 0; k < allowed.Count; k++)
        {
            if (SamePath(allowed[k], Path.GetPathRoot(allowed[k])!))
            {
                throw new SettingsException($"{at}.allowed_roots[{k}]: a whole drive is too broad");
            }

            if (PlanFile.SplitDrive(allowed[k]).ToUpperInvariant() != drive)
            {
                throw new SettingsException($"{at}.allowed_roots[{k}]: must be on the same drive as origin_root");
            }
        }

        if (!PlanGuards.Inside(origin, allowed, Path.GetFullPath))
        {
            throw new SettingsException($"{at}.origin_root: outside allowed_roots");
        }

        string label = Str(Need(g, "volume_label", at), $"{at}.volume_label");
        if (label.Length > 32)
        {
            throw new SettingsException($"{at}.volume_label: 1..32 chars");
        }

        int minutes = g.ContainsKey("reapply_interval_minutes")
            ? Int(g["reapply_interval_minutes"], $"{at}.reapply_interval_minutes", 5, 10080)
            : DefaultReapplyMinutes;
        PrewarmSettings prewarm = g.TryGetValue("prewarm", out object? pw) ? ParsePrewarm(pw, $"{at}.prewarm") : PrewarmSettings.Default;
        int maxStream = g.ContainsKey("max_stream_mib") ? Int(g["max_stream_mib"], $"{at}.max_stream_mib", 64, 1 << 20) : DefaultMaxStreamMib;
        string source = DefaultSource;
        if (g.TryGetValue("source", out object? src))
        {
            source = src is "manual" or "auto" ? (string)src : throw new SettingsException($"{at}.source: \"manual\" or \"auto\" expected");
        }

        string? antiCheat = null;
        if (g.TryGetValue("anticheat", out object? ac))
        {
            antiCheat = ac is "eac" or "battleye" or "vanguard" or "ricochet" or "unknown"
                ? (string)ac : throw new SettingsException($"{at}.anticheat: \"eac\", \"battleye\", \"vanguard\", \"ricochet\" or \"unknown\" expected");
        }

        return new GameSettings(name, exeNames, trace, plan, origin, allowed, label, minutes)
        {
            Prewarm = prewarm,
            MaxStreamMib = maxStream,
            Source = source,
            AntiCheat = antiCheat,
        };
    }

    private static PrewarmSettings ParsePrewarm(object? v, string at)
    {
        JObject o = v as JObject ?? throw new SettingsException($"{at}: object expected");
        Unknown(o, PrewarmKeys, at);
        PrewarmSettings d = PrewarmSettings.Default;
        bool onStart = o.TryGetValue("on_service_start", out object? b) ? b as bool? ?? throw new SettingsException($"{at}.on_service_start: true or false expected") : d.OnServiceStart;
        List<string> triggers = [];
        if (o.TryGetValue("trigger_exes", out object? t))
        {
            if (t is not List<object?> list || list.Count > 16)
            {
                throw new SettingsException($"{at}.trigger_exes: list of 0..16 expected");
            }

            triggers = list.Select((e, k) => ExeName(e, $"{at}.trigger_exes[{k}]")).ToList();
            CheckUnique(triggers, $"{at}.trigger_exes: duplicate");
        }

        int minFree = o.TryGetValue("min_free_ram_mib", out object? m) ? Int(m, $"{at}.min_free_ram_mib", 0, 1 << 20) : d.MinFreeRamMib;
        int cooldown = o.TryGetValue("cooldown_min", out object? c) ? Int(c, $"{at}.cooldown_min", 0, 10080) : d.CooldownMin;
        bool pin = o.TryGetValue("pin", out object? pn) ? pn as bool? ?? throw new SettingsException($"{at}.pin: true or false expected") : d.Pin;
        return new PrewarmSettings(onStart, triggers, minFree, cooldown) { Pin = pin, PinSet = o.ContainsKey("pin") };
    }

    private static object? Need(JObject o, string key, string at) =>
        o.TryGetValue(key, out object? v) ? v : throw new SettingsException($"{at}.{key}: missing");

    private static void Unknown(JObject o, string[] known, string at)
    {
        foreach (KeyValuePair<string, object?> kv in o)
        {
            if (Array.IndexOf(known, kv.Key) < 0)
            {
                throw new SettingsException($"{at}: unknown key {kv.Key}");
            }
        }
    }

    private static string Str(object? v, string field) =>
        v is string s && s.Length > 0 ? s : throw new SettingsException($"{field}: non-empty string expected");

    private static int Int(object? v, string field, int min, int max) =>
        v is long n && n >= min && n <= max ? (int)n : throw new SettingsException($"{field}: integer {min}..{max} expected");

    private static void CheckUnique(IEnumerable<string> values, string message)
    {
        if (values.GroupBy(x => x, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1))
        {
            throw new SettingsException(message);
        }
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// 파일 이름만 (경로·드라이브·스트림 표기 없음), .exe 로 끝남.
    private static string ExeName(object? v, string field)
    {
        string s = Str(v, field);
        if (s.Length > 255 || s != Path.GetFileName(s) || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || s.Length == 4)
        {
            throw new SettingsException($"{field}: bare file name ending in .exe expected");
        }

        return s;
    }

    /// 드라이브 문자 절대 경로 (X:\ 또는 X:/ 로 시작, UNC·장치 경로·상대 경로·'..' 조각 거부)를 정규화해 돌려줍니다.
    private static string FullPath(object? v, string field)
    {
        string s = Str(v, field);
        if (s.Length > 1024 || s.Length < 3 || !char.IsAsciiLetter(s[0]) || s[1] != ':' || s[2] is not ('\\' or '/')
            || s.IndexOfAny(['\0', '*', '?', '"', '<', '>', '|']) >= 0 || s.IndexOf(':', 2) >= 0
            || s.Split('\\', '/').Contains(".."))
        {
            throw new SettingsException($"{field}: absolute local path (X:\\...) without '..' expected");
        }

        string full = Path.GetFullPath(s);
        return full.Length > 3 ? full.TrimEnd('\\') : full;
    }
}
