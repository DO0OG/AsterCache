using AsterCache.Core.Discovery;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 게임 자동 등록: 런처 목록 발견 (1 시간마다) 과 실행 중 읽기량 휴리스틱 (2 초마다). 등록은 ConfigEditor 로 config.json 에 쓰고, 서비스는 그 변경을 다시 읽습니다.
public sealed class AutoRegistry
{
    public const int MaxExeNames = 16;
    private const int MaxLabelChars = 32;
    private const int MaxRootClimb = 3;

    // 런처 보조 프로그램·도구: 많이 읽어도 게임이 아님 (런처 본체는 LauncherExes.All).
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steamwebhelper.exe", "EpicWebHelper.exe", "Agent.exe", "explorer.exe", "aster.exe",
    };

    // 휴리스틱 후보에서 제외하는 이미지 경로의 폴더 (빌드 산출물, 저장소, 사용자 데이터)와 개발·시스템 도구 exe (끝의 * 는 접두). auto.ignore_exes 와 별도의 고정 목록.
    private static readonly string[] NonGameFolders = ["\\bin\\Debug\\", "\\bin\\Release\\", "\\obj\\", "\\.git\\", "\\node_modules\\", "\\AppData\\Local\\Temp\\"];

    // 시스템 볼륨에서만 더 거르는 폴더: Windows 구성요소·앱 데이터·마이크로소프트 앱. Program Files 의 나머지는 게임처럼 보일 때 (LooksLikeGame) 만 받음.
    private static readonly string[] SystemFolders =
        ["\\AppData\\", "\\ProgramData\\", "\\$Recycle.Bin\\", "\\Windows\\", "\\WindowsApps\\", "\\Common Files\\", "\\Program Files\\Microsoft", "\\Program Files (x86)\\Microsoft", "\\Program Files\\Windows", "\\Program Files (x86)\\Windows"];

    private static readonly string[] ToolExes =
    [
        "testhost*", "dotnet", "msbuild", "vstest*", "python*", "node", "java*", "powershell", "pwsh", "cmd", "conhost", "explorer", "7z*", "winrar",
    ];

    // exe 폴더가 이 이름이면 설치 폴더는 그 위 (Unreal 의 Game\Binaries\Win64 등).
    private static readonly HashSet<string> BinaryFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Binaries", "Win64", "Win32", "x64", "x86", "bin", "Bin64", "Bin32",
    };

    private readonly ConfigEditor _editor;
    private readonly AutoEnv _env;
    private readonly AutoStatus _status;
    private readonly ILog _log;
    private readonly Func<AsterSettings> _settings;
    private readonly Dictionary<string, bool?> _hdd = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _refusedLogged = new(StringComparer.OrdinalIgnoreCase); // 거부 이유를 이미 남긴 대상 (같은 줄이 반복되지 않게)
    private readonly Dictionary<int, DateTimeOffset> _pending = [];
    private bool _exesReviewed; // 제거 목록 게임의 exe 재평가는 서비스 시작 뒤 첫 발견 때 한 번
    private readonly object _lock = new(); // 발견 루프와 휴리스틱 루프가 아래 두 목록을 함께 씀
    private readonly List<GameSettings> _added = []; // 방금 쓴 등록 (설정을 다시 읽기 전까지 겹침 검사에 포함)
    private readonly HashSet<string> _addedExes = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<int>? _known;

    public AutoRegistry(ConfigEditor editor, AutoEnv env, AutoStatus status, ILog log, Func<AsterSettings> settings)
    {
        _editor = editor;
        _env = env;
        _status = status;
        _log = log;
        _settings = settings;
    }

    /// 가장 최근 발견에서 본 런처 게임 목록 (공유 프로필 지문 맞추기용).
    public IReadOnlyList<DiscoveredGame> LastScan { get; private set; } = [];

    /// 런처 목록을 한 번 훑어 새 게임을 등록합니다. 등록한 수를 돌려줍니다.
    public int DiscoverOnce()
    {
        AutoSettings a = _settings().Auto;
        if (!a.Enabled || !a.Discover)
        {
            return 0;
        }

        ReviewUninstallExes();
        int added = 0;
        IReadOnlyList<DiscoveredGame> found = LastScan = _env.Scan();
        foreach (SkippedFolder f in (found as ScanResult)?.Skipped ?? [])
        {
            LogRefusedOnce("launcher|" + f.InstallDir, $"auto uninstall: launcher folder skipped: {f.Name} ({f.InstallDir})"); // 같은 줄이 6 시간마다 반복되지 않게
        }

        foreach (DiscoveredGame d in found)
        {
            if (d.ExePath is null)
            {
                continue; // exe 를 모르면 감시할 수 없음
            }

            DriveCapabilities caps = _env.Capabilities(d.InstallDir);
            string? refused = caps.CanDiscover ? (d.OnHdd == true ? null : "not an HDD") : caps.Reason; // 시스템 SSD·SSD·종류를 모르는 드라이브는 재배치할 이유가 없거나 건드리면 안 됨 (시스템 HDD 는 예열만)
            if (refused is not null)
            {
                LogRefusedOnce(d.InstallDir, $"auto {d.Launcher}: skipped {d.Name} ({refused})");
                continue;
            }

            string exe = Path.GetFileName(d.ExePath);
            IReadOnlyList<GameSettings> games = AllGames();
            if (Ignored(a, exe) || Registered(games, exe))
            {
                continue;
            }

            if (games.FirstOrDefault(g => Overlaps(g.OriginRoot, d.InstallDir)) is { } owner)
            {
                if (owner.Source != "auto")
                {
                    LogRefusedOnce("overlap|" + d.InstallDir, $"auto {d.Launcher}: skipped {d.Name} (same folder as the registered game {owner.Name})"); // 수동 등록이 있으면 건드리지 않음
                }

                continue;
            }

            if (games.Count >= SettingsStore.MaxGames)
            {
                _log.Info("auto discovery: game list is full, skipping the rest"); // 빈 자리가 있을 때만 (밀어내기는 실제로 실행된 게임에만): 안 쓴 게임끼리 번갈아 밀어내는 일을 막음
                break;
            }

            List<string> extra = d.ExtraExes.Where(x => !x.Equals(exe, StringComparison.OrdinalIgnoreCase) && !Ignored(a, x) && !Registered(games, x))
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(MaxExeNames - 1).ToList();
            if (Register(d.Name, d.Launcher, exe, d.InstallDir, LauncherExes.ForLauncher(d.Launcher, d.TriggerExe), d.AntiCheat, evict: false, extra))
            {
                added++;
            }
        }

        return added;
    }

    // 예전 규칙으로 고른 제거 목록 게임의 exe (HYSafeMode.exe, 런처 셸 Games.exe 등) 를 지금 PickExe 규칙으로 다시 고름. 읽기 전용 열거 + config 편집 한 번.
    private void ReviewUninstallExes()
    {
        if (_exesReviewed)
        {
            return;
        }

        _exesReviewed = true;
        foreach (GameSettings g in _settings().Games.Where(x => x.Source == "auto" && x.ExeNames.Count > 0 && _status.Get(x.Name)?.Launcher == "uninstall"))
        {
            string current = g.ExeNames[0];
            if (LauncherScan.ReselectExe(_env.ExeFiles(g.OriginRoot), current) is not { } picked)
            {
                continue;
            }

            string exe = Path.GetFileName(picked);
            if (AllGames().Any(o => o.Name != g.Name && o.ExeNames.Contains(exe, StringComparer.OrdinalIgnoreCase)))
            {
                continue; // 다른 게임이 이미 쓰는 이름: 설정이 거부함
            }

            List<string> exes = [exe, .. g.ExeNames.Skip(1).Where(x => !x.Equals(exe, StringComparison.OrdinalIgnoreCase))];
            try
            {
                _editor.ReplaceExeNames(g.Name, exes.Take(MaxExeNames).ToList());
                lock (_lock)
                {
                    _addedExes.Add(exe);
                }

                _log.Info($"auto uninstall: {g.Name}: main exe changed from {current} to {exe}");
            }
            catch (Exception e) when (e is SettingsException or IOException or UnauthorizedAccessException or PyValueException)
            {
                _log.Warn($"auto uninstall: {g.Name}: could not change the main exe: {e.Message}");
            }
        }
    }

    /// 이미지 경로가 빌드 산출물·저장소·사용자 데이터 폴더 안이거나 개발·시스템 도구 exe 이면 게임 후보에서 뺍니다 (고정 규칙).
    public static bool IsNonGameImage(string image)
    {
        string dir = (Path.GetDirectoryName(image) ?? string.Empty) + "\\";
        string stem = Path.GetFileNameWithoutExtension(image);
        return NonGameFolders.Any(p => dir.Contains(p, StringComparison.OrdinalIgnoreCase))
            || ToolExes.Any(t => t.EndsWith('*') ? stem.StartsWith(t[..^1], StringComparison.OrdinalIgnoreCase) : stem.Equals(t, StringComparison.OrdinalIgnoreCase));
    }

    /// 새로 뜬 프로세스를 지켜보다가 HeuristicWindowSeconds 뒤 그 동안 HeuristicReadMib 이상 읽었으면 게임 후보로 봅니다.
    public void HeuristicTick()
    {
        IReadOnlyList<int> pids = _env.Pids();
        DateTimeOffset now = _env.Now();
        HashSet<int> current = [.. pids];
        if (_known is null)
        {
            _known = current; // 서비스 시작 때 떠 있던 프로세스는 제외
            return;
        }

        foreach (int pid in current.Where(p => !_known.Contains(p)))
        {
            _pending[pid] = now;
        }

        _known = current;
        foreach (int gone in _pending.Keys.Where(p => !current.Contains(p)).ToList())
        {
            _pending.Remove(gone);
        }

        AsterSettings s = _settings();
        bool on = s.Auto.Enabled && s.Auto.Discover;
        foreach (int pid in _pending.Where(kv => now - kv.Value >= TimeSpan.FromSeconds(AutoSettings.HeuristicWindowSeconds)).Select(kv => kv.Key).ToList())
        {
            _pending.Remove(pid);
            if (!on)
            {
                continue;
            }

            // 프로세스를 열 수 없으면 (보호된 프로세스) 건너뜀: 커널 ETW 세션을 걸지 않음 (안티치트 드라이버가 세션을 보고 클라이언트를 끊음), 다시 열지도 않음
            if (_env.ReadBytes(pid) is { } read)
            {
                Evaluate(pid, s.Auto, read);
            }
        }
    }

    /// 설정을 다시 읽어 방금 쓴 등록이 설정에 들어왔으면 임시 기록에서 뺍니다.
    public void SettingsReloaded(AsterSettings s)
    {
        lock (_lock)
        {
            _added.RemoveAll(a => s.Games.Any(g => g.Name == a.Name));
        }
    }

    // 트레이스는 다음 실행부터 잡힘: 이번 실행은 이미 시작 부분의 읽기를 놓쳤고 정확한 게임인지도 이제야 알았기 때문.
    private void Evaluate(int pid, AutoSettings a, long read)
    {
        string? image = _env.ImagePath(pid);
        if (read < (long)AutoSettings.HeuristicReadMib << 20 || image is null)
        {
            return;
        }

        string exe = Path.GetFileName(image);
        string folder = Path.GetDirectoryName(image) ?? string.Empty;
        string? letter = image.Length > 2 && char.IsAsciiLetter(image[0]) && image[1] == ':' ? image[..2].ToUpperInvariant() : null;
        if (letter is null || Ignored(a, exe) || NotGames.Contains(exe) || LauncherExes.Is(exe) || exe.StartsWith("AsterCache.", StringComparison.OrdinalIgnoreCase)
            || IsNonGameImage(image) || Under(image, _env.WindowsDir) || Under(image, _env.ServiceDir))
        {
            return;
        }

        DriveCapabilities caps = _env.Capabilities(letter);
        if (!caps.CanDiscover)
        {
            LogRefusedOnce(letter, $"auto heuristic: ignoring programs on {letter} (system SSD, SSD or unknown drive type)");
            return;
        }

        if (caps.IsSystem && SystemFolders.Any(p => (folder + "\\").Contains(p, StringComparison.OrdinalIgnoreCase)))
        {
            return; // 시스템 HDD 의 Windows·앱 데이터·마이크로소프트 앱 폴더는 게임 후보가 아님
        }

        IReadOnlyList<GameSettings> games = AllGames();
        if (Registered(games, exe))
        {
            return;
        }

        if (games.FirstOrDefault(g => Under(folder, g.OriginRoot) || string.Equals(folder.TrimEnd('\\'), g.OriginRoot, StringComparison.OrdinalIgnoreCase)) is { } owner)
        {
            AddExe(owner, exe);
            return; // 수동 등록 게임이면 건드리지 않음
        }

        string origin = OriginRoot(folder);
        if (origin.Length <= 2)
        {
            _log.Info("auto heuristic: skipped a process at a drive root");
            return;
        }

        if (!games.Any(g => Overlaps(g.OriginRoot, origin)))
        {
            if (!_env.LooksLikeGame(origin))
            {
                LogRefusedOnce("notgame|" + origin, $"auto heuristic: skipped {exe} (the folder does not look like a game: no file of 256 MiB or more and no launcher manifest)");
                return;
            }

            Register(Path.GetFileName(origin), "heuristic", exe, origin, LauncherExes.ForOrigin(origin), AutoEnv.KnownAntiCheat(_env.AntiCheatOf(origin)), evict: true);
        }
    }

    private void AddExe(GameSettings owner, string exe)
    {
        if (owner.Source != "auto")
        {
            return;
        }

        if (owner.ExeNames.Count >= MaxExeNames)
        {
            _log.Info($"{owner.Name}: exe list is full, not adding {exe}");
            return;
        }

        try
        {
            _editor.AddExeName(owner.Name, exe);
            lock (_lock)
            {
                _addedExes.Add(exe);
            }

            _log.Info($"{owner.Name}: added exe {exe} (read heavily after start)");
        }
        catch (Exception e) when (e is SettingsException or IOException or UnauthorizedAccessException or PyValueException)
        {
            _log.Warn($"{owner.Name}: could not add exe {exe}: {e.Message}");
        }
    }

    private bool Register(string display, string launcher, string exe, string installDir, IReadOnlyList<string> triggers, string? antiCheat, bool evict, IReadOnlyList<string>? extraExes = null)
    {
        string letter = installDir[..2].ToUpperInvariant();
        string label = _env.VolumeLabel(letter);
        if (label.Length == 0 || label.Length > MaxLabelChars)
        {
            _log.Info($"auto {launcher}: skipped {display}: volume has no usable label");
            return false;
        }

        if (FreshOverlap(installDir) is { } other)
        {
            _log.Info($"auto {launcher}: skipped {display} (same folder as the registered game {other})"); // 서비스가 들고 있는 설정 뒤에 방금 등록된 게임까지 쓰기 직전 파일 기준으로 다시 봄
            return false;
        }

        IReadOnlyList<GameSettings> games = AllGames();
        var taken = new HashSet<string>(games.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
        string folderName = Path.GetFileName(installDir);
        string name = LauncherManifests.MakeUnique(LauncherManifests.SafeName(display, folderName, Path.GetFileNameWithoutExtension(exe)), taken);
        string plan = Path.Combine(_editor.PlansDir, name + ".plan.json");
        IReadOnlyList<string> exes = [exe, .. extraExes ?? []];
        var input = new GameInput(name, exes, plan + ".prefetch.json", plan, installDir, [installDir], label, null, triggers) { Source = "auto", OnServiceStart = false, AntiCheat = antiCheat };
        string? evicted = null;
        try
        {
            if (evict)
            {
                evicted = _editor.AddAuto(input, _status.LastPlayed());
            }
            else
            {
                _editor.Add(input); // 빈 자리가 없으면 SettingsException
            }
        }
        catch (Exception e) when (e is SettingsException or IOException or UnauthorizedAccessException or PyValueException)
        {
            _log.Warn($"auto {launcher}: could not register {name}: {e.Message}");
            return false;
        }

        lock (_lock)
        {
            _added.Add(new GameSettings(name, input.ExeNames, input.Trace, input.Plan, installDir, input.AllowedRoots, label, SettingsStore.DefaultReapplyMinutes)
            {
                Source = "auto",
                AntiCheat = antiCheat,
                Prewarm = new PrewarmSettings(false, triggers, PrewarmSettings.Default.MinFreeRamMib, PrewarmSettings.Default.CooldownMin),
            });
            _addedExes.UnionWith(exes);
        }

        _status.Set(name, "auto", launcher, AutoStage.Discovered, _env.Capabilities(installDir).CanRelocate ? null : DriveCapabilities.PrewarmOnly);
        _status.AddEvent(name, "discovered");
        _log.Info($"auto {launcher}: registered {name} ({exe})");
        if (antiCheat is not null)
        {
            _log.Info($"{name}: anti-cheat detected ({antiCheat}); AsterCache only reads files and never opens the game process beyond query access");
        }
        if (evicted is not null)
        {
            _status.Remove(evicted);
            lock (_lock)
            {
                _added.RemoveAll(g => g.Name == evicted);
            }

            _log.Info($"auto {launcher}: game list was full, removed the least recently played auto game {evicted}");
        }

        return true;
    }

    private string? FreshOverlap(string root)
    {
        try
        {
            return _editor.Load()?.Games.FirstOrDefault(g => Overlaps(g.OriginRoot, root))?.Name;
        }
        catch (Exception e) when (e is SettingsException or IOException or UnauthorizedAccessException or PyValueException)
        {
            return null; // 읽지 못하면 쓰기 쪽 검증이 판단함
        }
    }

    private IReadOnlyList<GameSettings> AllGames()
    {
        AsterSettings s = _settings();
        lock (_lock)
        {
            return [.. s.Games, .. _added.Where(a => s.Games.All(g => g.Name != a.Name))];
        }
    }

    private bool Registered(IReadOnlyList<GameSettings> games, string exe)
    {
        lock (_lock)
        {
            return _addedExes.Contains(exe) || games.Any(g => g.ExeNames.Contains(exe, StringComparer.OrdinalIgnoreCase));
        }
    }

    private void LogRefusedOnce(string key, string message)
    {
        bool first;
        lock (_lock)
        {
            first = _refusedLogged.Add(key);
        }

        if (first)
        {
            _log.Info(message);
        }
    }

    private bool IsHdd(string letter)
    {
        if (!_hdd.TryGetValue(letter, out bool? hdd))
        {
            _hdd[letter] = hdd = _env.IsHdd(letter);
        }

        return hdd == true;
    }

    private static bool Ignored(AutoSettings a, string exe) => a.IgnoreExes.Contains(exe, StringComparer.OrdinalIgnoreCase);

    private static bool Under(string path, string root) =>
        root.Length > 0 && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);

    private static bool Overlaps(string a, string b)
    {
        string x = a.TrimEnd('\\');
        string y = b.TrimEnd('\\');
        return string.Equals(x, y, StringComparison.OrdinalIgnoreCase) || Under(x, y) || Under(y, x);
    }

    /// exe 폴더에서 Binaries/Win64 같은 폴더 이름인 동안 (최대 3 번) 위로 올라간 설치 폴더. 드라이브 루트까지 오면 길이 2 ("D:") 이하.
    public static string OriginRoot(string exeFolder)
    {
        string cur = exeFolder.TrimEnd('\\');
        for (int i = 0; i < MaxRootClimb && cur.Length > 2 && BinaryFolders.Contains(Path.GetFileName(cur)); i++)
        {
            cur = (Path.GetDirectoryName(cur) ?? string.Empty).TrimEnd('\\');
        }

        return cur;
    }
}
