using AsterCache.Core.Json;

namespace AsterCache.Core.Settings;

/// 등록할 게임 한 개의 입력값 (config.json 의 한 항목).
public sealed record GameInput(
    string Name, IReadOnlyList<string> ExeNames, string Trace, string Plan, string OriginRoot, IReadOnlyList<string> AllowedRoots,
    string VolumeLabel, int? ReapplyMinutes = null, IReadOnlyList<string>? TriggerExes = null)
{
    /// "auto" (자동 등록) 이면 source 로 기록. 비우거나 "manual" 이면 키를 쓰지 않음.
    public string? Source { get; init; }

    /// 값이 있으면 prewarm.on_service_start 로 기록.
    public bool? OnServiceStart { get; init; }

    /// 값이 있으면 anticheat 로 기록.
    public string? AntiCheat { get; init; }
}

/// config.json 편집. 바꾼 결과 전체를 SettingsStore 검증에 통과시킨 뒤에만 씁니다 (서비스가 읽을 수 없는 설정을 만들지 않음).
/// 쓰기 형식은 고정 (들여쓰기 2, UTF-8 BOM 없음, LF) 이라 이 형식의 파일은 읽고 쓰면 바이트가 그대로입니다.
/// 읽기-수정-쓰기는 데이터 폴더의 config.json.lock 을 독점으로 잡고 하므로 트레이와 서비스가 동시에 고쳐도 서로의 변경을 덮지 않습니다.
/// sleep (밀리초)·now 는 잠금·교체 재시도의 대기와 시계 (시험이 바꿈, 기본은 실제로 잠).
public sealed class ConfigEditor(string dataDir, int lockWaitMs = 5000, Action<int>? sleep = null, Func<DateTime>? now = null)
{
    private const int LockRetryMs = 100;
    private const int MoveTries = 5;
    private readonly Action<int> _sleep = sleep ?? Thread.Sleep;
    private readonly Func<DateTime> _now = now ?? (() => DateTime.UtcNow);

    public string ConfigPath { get; } = Path.Combine(dataDir, "config.json");

    public string PlansDir { get; } = Path.Combine(dataDir, "plans");

    /// 설정이 아직 없으면 null. 있는데 검증을 통과하지 못하면 SettingsException.
    public AsterSettings? Load() => File.Exists(ConfigPath) ? SettingsStore.Load(ConfigPath, PlansDir) : null;

    /// 수동 등록은 겹치는 폴더를 막지 않습니다 (기존 동작). source 가 auto 인 입력만 잠금 안에서 겹침을 검사해 거부합니다 (SettingsException).
    public void Add(GameInput g)
    {
        using FileStream _ = Lock();
        JObject root = ReadRoot();
        List<object?> games = GamesOf(root);
        if (g.Source == "auto")
        {
            RejectOverlap(games, g);
        }

        games.Add(GameObject(g));
        Save(root);
    }

    /// 자동 등록. 이미 등록된 게임의 origin_root 와 같거나 안쪽·바깥이면 거부합니다 (SettingsException, 파일은 그대로). 항목이 꽉 찼으면 source=auto 중 lastPlayed 가 가장 오래된 하나 (기록 없음이 가장 오래됨, 동률이면 앞쪽) 를 빼고 넣습니다. 뺀 이름 (없으면 null) 반환.
    public string? AddAuto(GameInput g, IReadOnlyDictionary<string, DateTimeOffset> lastPlayed)
    {
        using FileStream _ = Lock();
        JObject root = ReadRoot();
        List<object?> games = GamesOf(root);
        RejectOverlap(games, g);
        string? evicted = null;
        if (games.Count >= SettingsStore.MaxGames)
        {
            int at = -1;
            DateTimeOffset oldest = DateTimeOffset.MaxValue;
            for (int i = 0; i < games.Count; i++)
            {
                if (games[i] is JObject o && o.TryGetValue("source", out object? s) && s is "auto" && o.TryGetValue("name", out object? n) && n is string name)
                {
                    DateTimeOffset t = lastPlayed.TryGetValue(name, out DateTimeOffset p) ? p : DateTimeOffset.MinValue;
                    if (t < oldest)
                    {
                        (at, oldest, evicted) = (i, t, name);
                    }
                }
            }

            if (at < 0)
            {
                throw new SettingsException("games: full, no auto entry to replace");
            }

            games.RemoveAt(at);
        }

        games.Add(GameObject(g with { Source = "auto" }));
        Save(root);
        return evicted;
    }

    /// 기존 게임의 exe_names 에 추가 (최대 16, 중복·형식은 검증이 거부).
    public void AddExeName(string game, string exe)
    {
        using FileStream _ = Lock();
        JObject root = ReadRoot();
        if (FindGame(GamesOf(root), game) is not { } o || o["exe_names"] is not List<object?> exes)
        {
            throw new SettingsException("games: no game with that name");
        }

        exes.Add(exe);
        Save(root);
    }

    /// 기존 게임의 exe_names 를 통째로 바꿈 (최대 16, 중복·형식은 검증이 거부). 다른 키는 그대로.
    public void ReplaceExeNames(string game, IReadOnlyList<string> exes)
    {
        using FileStream _ = Lock();
        JObject root = ReadRoot();
        if (FindGame(GamesOf(root), game) is not { } o || !o.ContainsKey("exe_names"))
        {
            throw new SettingsException("games: no game with that name");
        }

        o["exe_names"] = exes.Cast<object?>().ToList();
        Save(root);
    }

    /// auto 객체 전체를 기록 (기본값과 같아도 모든 키를 씀).
    public void SetAuto(AutoSettings a)
    {
        using FileStream _ = Lock();
        JObject root = ReadRoot();
        WriteAuto(root, a);
        Save(root);
    }

    /// 잠금 안에서 최신 파일의 auto 를 읽어 change 로 바꾼 뒤 한 번에 씁니다 (그 사이 서비스나 다른 편집이 바꾼 값을 보존). max_zone 은 선택.
    public void UpdateAuto(Func<AutoSettings, AutoSettings> change, int? maxZoneMib = null, bool maxZoneAuto = false)
    {
        using FileStream _ = Lock();
        JObject root = ReadRoot();
        AutoSettings current = File.Exists(ConfigPath) ? SettingsStore.Load(ConfigPath, PlansDir).Auto : AutoSettings.Default;
        WriteAuto(root, change(current));
        if (maxZoneAuto)
        {
            root["max_zone_mib"] = "auto";
        }
        else if (maxZoneMib is int mib)
        {
            root["max_zone_mib"] = (long)mib;
        }

        Save(root);
    }

    private static void WriteAuto(JObject root, AutoSettings a)
    {
        var auto = new JObject
        {
            { "enabled", a.Enabled },
            { "discover", a.Discover },
            { "trace", a.Trace },
            { "relocate", a.Relocate },
            { "prewarm_recent", (long)a.PrewarmRecent },
            { "idle_minutes", (long)a.IdleMinutes },
            { "trace_cap_seconds", (long)a.TraceCapSeconds },
            { "trace_cap_mib", (long)a.TraceCapMib },
            { "max_relocations_per_day", (long)a.MaxRelocationsPerDay },
            { "max_relocate_gib_per_day", (long)a.MaxRelocateGibPerDay },
            { "relocate_chunk_mib", (long)a.RelocateChunkMib },
            { "relocate_io_priority", a.RelocateIoPriority },
            { "relocate_verify", a.RelocateVerify },
            { "relocate_order", a.RelocateOrder },
            { "ignore_exes", a.IgnoreExes.Cast<object?>().ToList() },
            { "rewarm_minutes", (long)a.RewarmMinutes },
            { "post_update_delay_min", (long)a.PostUpdateDelayMin },
            { "recovery_max_min", (long)a.RecoveryMaxMin },
            { "post_relocate_recovery", a.PostRelocateRecovery },
            { "keep_awake", a.KeepAwake },
            { "keep_stream_on_game_start", a.KeepStreamOnGameStart },
            { "full_prewarm", a.FullPrewarm },
            { "full_prewarm_max_mib", (long)a.FullPrewarmMaxMib },
            { "engine_profiles", a.EngineProfiles },
            { "learn", new JObject { { "enabled", a.Learn.Enabled }, { "window_mib", (long)a.Learn.WindowMib }, { "max_total_mib", (long)a.Learn.MaxTotalMib }, { "mode", a.Learn.Mode }, { "saturation_mbps", (long)a.Learn.SaturationMbps } } },
            { "shared_profiles", new JObject { { "enabled", a.SharedProfiles.Enabled }, { "url", a.SharedProfiles.Url } } },
            { "ssd_tier", new JObject { { "enabled", a.SsdTier.Enabled }, { "budget_gib", (long)a.SsdTier.BudgetGib }, { "root", a.SsdTier.Root } } },
        };
        if (a.RelocateParallelSet) // 명시되지 않은 값은 쓰지 않음 (자동 규칙 유지)
        {
            auto["relocate_parallel"] = (long)a.RelocateParallel;
        }

        if (a.IdleSecondsSet || !a.IdleMinutesSet) // idle_minutes 만 명시된 설정은 그 값이 이기도록 idle_seconds 를 쓰지 않음
        {
            auto.Add("idle_seconds", (long)a.IdleSeconds);
        }

        root["auto"] = auto;
    }

    public void SetMaxZoneMib(int mib)
    {
        using FileStream _ = Lock();
        JObject root = ReadRoot();
        root["max_zone_mib"] = (long)mib;
        Save(root);
    }

    /// max_zone_mib 를 "auto" (RAM 에 맞춰 서비스가 계산) 로 기록.
    public void SetMaxZoneAuto()
    {
        using FileStream _ = Lock();
        JObject root = ReadRoot();
        root["max_zone_mib"] = "auto";
        Save(root);
    }

    /// "poll" 또는 "etw" (그 밖의 값은 검증에서 거부됨).
    public void SetWatcher(string watcher)
    {
        using FileStream _ = Lock();
        JObject root = ReadRoot();
        root["watcher"] = watcher;
        Save(root);
    }

    /// "auto", "ko" 또는 "en" (그 밖의 값은 검증에서 거부됨).
    public void SetLanguage(string language)
    {
        using FileStream _ = Lock();
        JObject root = ReadRoot();
        root["language"] = language;
        Save(root);
    }

    /// 게임 삭제. 자동 등록 항목이면 그 exe_names 를 auto.ignore_exes 에 넣어 다음 발견 때 다시 등록되지 않게 합니다 (64 개가 넘으면 앞쪽부터 빼고, 뺀 이름 반환).
    public IReadOnlyList<string> Remove(string name)
    {
        using FileStream _ = Lock();
        JObject root = ReadRoot();
        List<object?> games = GamesOf(root);
        int at = games.FindIndex(x => x is JObject o && IsNamed(o, name));
        if (at < 0)
        {
            throw new SettingsException("games: no game with that name");
        }

        var dropped = new List<string>();
        if (games[at] is JObject g && g.TryGetValue("source", out object? src) && src is "auto" && g.TryGetValue("exe_names", out object? en) && en is List<object?> exes)
        {
            if (!root.TryGetValue("auto", out object? ao))
            {
                root["auto"] = ao = new JObject();
            }

            JObject auto = ao as JObject ?? throw new SettingsException("auto: object expected");
            if (!auto.TryGetValue("ignore_exes", out object? io))
            {
                auto["ignore_exes"] = io = new List<object?>();
            }

            List<object?> ignore = io as List<object?> ?? throw new SettingsException("auto.ignore_exes: list expected");
            foreach (string exe in exes.OfType<string>())
            {
                if (!ignore.Any(x => string.Equals(x as string, exe, StringComparison.OrdinalIgnoreCase)))
                {
                    ignore.Add(exe);
                }
            }

            while (ignore.Count > 64)
            {
                dropped.Add(ignore[0] as string ?? string.Empty);
                ignore.RemoveAt(0);
            }
        }

        games.RemoveAt(at);
        Save(root);
        return dropped;
    }

    // 같은 잠금 안에서 최신 파일 기준으로 판단: 호출한 쪽이 먼저 본 목록 이후에 다른 쪽이 등록한 게임과도 겹치지 않게 함.
    private static void RejectOverlap(List<object?> games, GameInput g)
    {
        foreach (JObject o in games.OfType<JObject>())
        {
            if (o.TryGetValue("origin_root", out object? root) && root is string existing && Overlaps(existing, g.OriginRoot))
            {
                throw new SettingsException("games: origin_root overlaps the registered game " + (o.TryGetValue("name", out object? n) && n is string name ? name : existing));
            }
        }
    }

    // 대소문자를 무시하고 경로를 정규화해 같거나 한쪽이 다른 쪽 안에 있으면 겹침.
    private static bool Overlaps(string a, string b)
    {
        string x = Full(a) + "\\";
        string y = Full(b) + "\\";
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }

    private static string Full(string path)
    {
        try
        {
            return Path.GetFullPath(path).TrimEnd('\\', '/');
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd('\\', '/');
        }
    }

    private static bool IsNamed(JObject o, string name) =>
        o.TryGetValue("name", out object? n) && string.Equals(n as string, name, StringComparison.OrdinalIgnoreCase);

    private static JObject? FindGame(List<object?> games, string name) => games.OfType<JObject>().FirstOrDefault(o => IsNamed(o, name));

    private static JObject GameObject(GameInput g)
    {
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

        var prewarm = new JObject();
        if (g.OnServiceStart is bool onStart)
        {
            prewarm.Add("on_service_start", onStart);
        }

        if (g.TriggerExes is { Count: > 0 })
        {
            prewarm.Add("trigger_exes", g.TriggerExes.Cast<object?>().ToList());
        }

        if (prewarm.Count > 0)
        {
            game.Add("prewarm", prewarm);
        }

        if (g.AntiCheat is { } antiCheat)
        {
            game.Add("anticheat", antiCheat);
        }

        if (g.Source is { } source and not "manual") // 잘못된 값도 그대로 써서 검증이 거부하게 함
        {
            game.Add("source", source);
        }

        return game;
    }

    /// config.json.lock 을 독점으로 엽니다 (잠겨 있으면 100 ms 간격으로 lockWaitMs 까지 재시도 후 IOException). 닫으면 풀립니다.
    private FileStream Lock()
    {
        Directory.CreateDirectory(dataDir);
        string path = ConfigPath + ".lock";
        DateTime until = _now().AddMilliseconds(lockWaitMs);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (_now() < until)
            {
                _sleep(LockRetryMs);
            }
        }
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
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(tmp, ConfigPath, true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= MoveTries)
                {
                    File.Delete(tmp);
                    throw;
                }

                _sleep(LockRetryMs); // 서비스나 백신이 잠깐 잡고 있는 공유 위반
            }
        }
    }
}
