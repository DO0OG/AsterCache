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
        "max_relocations_per_day", "ignore_exes", "rewarm_minutes",
    ];
    private static readonly string[] PrewarmKeys = ["on_service_start", "trigger_exes", "min_free_ram_mib", "cooldown_min", "pin"];

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

        return new AutoSettings(
            OptBool(o, "enabled", d.Enabled), OptBool(o, "discover", d.Discover), OptBool(o, "trace", d.Trace), OptBool(o, "relocate", d.Relocate),
            OptInt(o, "prewarm_recent", 0, 8, d.PrewarmRecent), OptInt(o, "idle_minutes", 1, 1440, d.IdleMinutes),
            OptInt(o, "trace_cap_seconds", 30, 7200, d.TraceCapSeconds), OptInt(o, "trace_cap_mib", 64, 65536, d.TraceCapMib),
            OptInt(o, "max_relocations_per_day", 0, 24, d.MaxRelocationsPerDay), ignore)
        {
            RewarmMinutes = OptInt(o, "rewarm_minutes", 0, 1440, d.RewarmMinutes),
        };
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
        return new PrewarmSettings(onStart, triggers, minFree, cooldown) { Pin = pin };
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
