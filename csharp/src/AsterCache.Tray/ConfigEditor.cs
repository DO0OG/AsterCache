using AsterCache.Core.Json;
using AsterCache.Core.Settings;

namespace AsterCache.Tray;

/// 등록할 게임 한 개의 입력값 (config.json 의 한 항목).
public sealed record GameInput(
    string Name, IReadOnlyList<string> ExeNames, string Trace, string Plan, string OriginRoot, IReadOnlyList<string> AllowedRoots,
    string VolumeLabel, int? ReapplyMinutes = null, IReadOnlyList<string>? TriggerExes = null);

/// config.json 편집. 바꾼 결과 전체를 SettingsStore 검증에 통과시킨 뒤에만 씁니다 (서비스가 읽을 수 없는 설정을 만들지 않음).
/// 쓰기 형식은 고정 (들여쓰기 2, UTF-8 BOM 없음, LF) 이라 이 형식의 파일은 읽고 쓰면 바이트가 그대로입니다.
public sealed class ConfigEditor(string dataDir)
{
    public string ConfigPath { get; } = Path.Combine(dataDir, "config.json");

    public string PlansDir { get; } = Path.Combine(dataDir, "plans");

    /// 설정이 아직 없으면 null. 있는데 검증을 통과하지 못하면 SettingsException.
    public AsterSettings? Load() => File.Exists(ConfigPath) ? SettingsStore.Load(ConfigPath, PlansDir) : null;

    public void Add(GameInput g)
    {
        JObject root = ReadRoot();
        var game = new JObject
        {
            { "name", g.Name },
            { "exe_names", g.ExeNames.Cast<object?>().ToList() },
            { "trace", g.Trace },
            { "plan", g.Plan },
            { "origin_root", g.OriginRoot },
            { "allowed_roots", g.AllowedRoots.Cast<object?>().ToList() },
            { "volume_label", g.VolumeLabel },
        };
        if (g.ReapplyMinutes is int minutes)
        {
            game.Add("reapply_interval_minutes", (long)minutes);
        }

        if (g.TriggerExes is { Count: > 0 })
        {
            game.Add("prewarm", new JObject { { "trigger_exes", g.TriggerExes.Cast<object?>().ToList() } });
        }

        GamesOf(root).Add(game);
        Save(root);
    }

    /// "poll" 또는 "etw" (그 밖의 값은 검증에서 거부됨).
    public void SetWatcher(string watcher)
    {
        JObject root = ReadRoot();
        root["watcher"] = watcher;
        Save(root);
    }

    public void Remove(string name)
    {
        JObject root = ReadRoot();
        List<object?> games = GamesOf(root);
        int at = games.FindIndex(x => x is JObject o && o.TryGetValue("name", out object? n) && string.Equals(n as string, name, StringComparison.OrdinalIgnoreCase));
        if (at < 0)
        {
            throw new SettingsException("games: no game with that name");
        }

        games.RemoveAt(at);
        Save(root);
    }

    private JObject ReadRoot()
    {
        if (!File.Exists(ConfigPath))
        {
            return new JObject { { "games", new List<object?>() } };
        }

        try
        {
            return JsonRead.ReadFile(ConfigPath) as JObject ?? throw new SettingsException("config: object expected");
        }
        catch (PyValueException e)
        {
            throw new SettingsException("config: " + e.Message);
        }
    }

    private static List<object?> GamesOf(JObject root) =>
        root.TryGetValue("games", out object? g) && g is List<object?> list ? list : throw new SettingsException("games: list expected");

    private void Save(JObject root)
    {
        string text = PyJson.Dumps(root, indent: 2, ensureAscii: false) + "\n";
        SettingsStore.Parse(text, PlansDir); // 거부되면 SettingsException, 파일은 그대로
        Directory.CreateDirectory(dataDir);
        string tmp = ConfigPath + ".tmp";
        File.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(text));
        File.Move(tmp, ConfigPath, true);
    }
}
