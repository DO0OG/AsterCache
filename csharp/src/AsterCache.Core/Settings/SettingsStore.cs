using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;

namespace AsterCache.Core.Settings;

/// %ProgramData%\AsterCache\config.json 읽기와 검증. 설정 파일은 신뢰 경계라 알려진 키만, 정확한 형으로만 받습니다.
/// plan·trace 는 plansDir (관리자·SYSTEM 전용 ACL 폴더) 안에 있어야 하고, origin_root 는 allowed_roots 중 하나 안에 있어야 합니다.
public static class SettingsStore
{
    public const int DefaultPollMs = 250;
    public const int DefaultReapplyMinutes = 1440;
    public const long MaxFileBytes = 1 << 20;
    public const int MaxGames = 32;

    private static readonly string[] TopKeys = ["games", "poll_ms"];
    private static readonly string[] GameKeys =
        ["name", "exe_names", "trace", "plan", "origin_root", "allowed_roots", "volume_label", "reapply_interval_minutes"];

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
        return new AsterSettings(poll, result);
    }

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
        return new GameSettings(name, exeNames, trace, plan, origin, allowed, label, minutes);
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
