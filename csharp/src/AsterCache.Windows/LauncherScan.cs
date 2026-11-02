using AsterCache.Core.Discovery;
using AsterCache.Core.Json;
using Microsoft.Win32;

namespace AsterCache.Windows;

/// 런처가 설치한 게임 한 개. ExePath 는 모르면 null, OnHdd 는 설치 드라이브가 HDD 인지 (판정 못 하면 null), TriggerExe 는 그 런처의 프로그램 이름 (없으면 null).
public sealed record DiscoveredGame(string Name, string Launcher, string InstallDir, string? ExePath, bool? OnHdd, string? TriggerExe)
{
    /// ExePath 말고 같은 게임이 실행하는 다른 exe 이름 (폴더 안에 실제로 있는 것만, 이름만).
    public IReadOnlyList<string> ExtraExes { get; init; } = [];

    /// 설치 폴더 이름에서 알아낸 안티치트 ("eac" | "battleye" | "vanguard" | "ricochet" | "unknown"), 흔적이 없으면 null.
    public string? AntiCheat { get; init; }
}

/// 프로그램 제거 목록의 항목 한 개 (값이 없으면 빈 문자열).
public sealed record UninstallEntry(string DisplayName, string Publisher, string InstallLocation);

/// LauncherScan 이 바깥 세계에 접근하는 길 (시험에서는 가짜로 바꿈).
public interface ILauncherEnv
{
    string ProgramData { get; }

    /// 시스템(Windows) 볼륨의 드라이브 ("C:").
    string SystemDrive { get; }

    /// 고정 드라이브 ("C:" 형식).
    IReadOnlyList<string> FixedDrives();

    /// 폴더 바로 아래 폴더의 전체 경로. 폴더가 없거나 읽을 수 없으면 빈 목록.
    IReadOnlyList<string> SubDirs(string dir);

    /// 프로그램 제거 목록 (HKLM 64·32비트 보기, HKCU) 의 항목, 시스템 구성 요소는 뺌.
    IReadOnlyList<UninstallEntry> UninstallEntries();

    /// 폴더 아래 파일 크기 합이 bytes 이상인가. 깊이·항목 수에 상한을 두고 넘으면 곧바로 멈춤 (상한까지 모자라면 false).
    bool FolderAtLeast(string dir, long bytes);

    /// HKLM (machine = true, 64비트 보기) 또는 HKCU 의 문자열 값. 없거나 문자열이 아니면 null.
    string? RegString(bool machine, string subKey, string name);

    /// HKLM 하위 키 이름.
    IReadOnlyList<string> RegSubKeys(string subKey);

    /// 텍스트 파일 내용, 없거나 너무 크면 null.
    string? ReadText(string path);

    /// 폴더 바로 아래의 파일 경로 (패턴 일치). 폴더가 없으면 빈 목록.
    IReadOnlyList<string> Files(string dir, string pattern);

    bool DirectoryExists(string path);

    bool FileExists(string path);

    /// 폴더 아래 maxDepth 단계까지의 .exe 와 크기.
    IEnumerable<(string Path, long Size)> ExeFiles(string dir, int maxDepth);

    /// 폴더 아래 maxDepth 단계까지의 파일·폴더 이름 (경로 아님, 읽기 전용 열거).
    IReadOnlyList<string> EntryNames(string dir, int maxDepth);

    /// 'X:' 드라이브의 탐색 지연 유무 (true = HDD), 모르면 null.
    bool? IncursSeekPenalty(string letter);
}

/// 설치된 런처 (Steam, Epic, GOG, Ubisoft, Riot, Xbox) 의 설치 목록과, 그 밖의 큰 설치 폴더 (프로그램 제거 목록) 를 읽어 게임을 찾습니다. 읽기 전용이며 관리자가 필요 없습니다.
/// 목록이 없는 나머지는 제거 목록이나 실행 중 읽기량 휴리스틱으로 잡힙니다.
public static partial class LauncherScan
{
    private const string GogKey = @"SOFTWARE\WOW6432Node\GOG.com\Games";
    private const string UbisoftKey = @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallKey32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall";
    private const string UninstallKeyUser = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";
    private const int ExeDepth = 3;
    public const int AntiCheatDepth = 4; // 이름만 열거하므로 깊은 위치의 안티치트 폴더까지 봄

    // 설치 폴더의 가장 큰 exe 를 고를 때 빼는 이름 조각 (제거 프로그램, 재배포 패키지, 크래시 보고, 안티치트).
    private static readonly string[] ExcludedExeParts =
        ["unins", "redist", "vcredist", "dxsetup", "directx", "crashhandler", "crashreport", "setup", "installer", "easyanticheat", "battleye", "ue4prereq"];

    private sealed record Raw(string Display, string Launcher, string InstallDir, string? Exe, string? Trigger)
    {
        public IReadOnlyList<string> Extra { get; init; } = [];

        public string? AntiCheat { get; init; }
    }

    public static IReadOnlyList<DiscoveredGame> Scan(ILauncherEnv? env = null)
    {
        env ??= new SystemLauncherEnv();
        var raws = new List<Raw>();
        foreach (Func<ILauncherEnv, IEnumerable<Raw>> source in new Func<ILauncherEnv, IEnumerable<Raw>>[] { Steam, Epic, Gog, Ubisoft, Riot, Xbox })
        {
            raws.AddRange(Collect(() => source(env)));
        }

        var result = new List<DiscoveredGame>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var disks = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase);
        bool? IsHdd(string letter)
        {
            if (!disks.TryGetValue(letter, out bool? hdd))
            {
                disks[letter] = hdd = env.IncursSeekPenalty(letter);
            }

            return hdd;
        }

        void Add(Raw r)
        {
            string? dir = Normalize(r.InstallDir);
            if (dir is null || dir.Length <= 3 || !env.DirectoryExists(dir) || !dirs.Add(dir))
            {
                return; // 드라이브 루트 (설정이 드라이브 전체를 거부), 없는 폴더, 이미 나온 폴더
            }

            string? exe = ResolveExe(env, dir, r.Exe);
            string name = LauncherManifests.MakeUnique(
                LauncherManifests.SafeName(r.Display, Path.GetFileName(dir), exe is null ? string.Empty : Path.GetFileNameWithoutExtension(exe)), names);
            result.Add(new DiscoveredGame(name, r.Launcher, dir, exe, IsHdd(dir[..2].ToUpperInvariant()), r.Trigger)
            {
                AntiCheat = r.AntiCheat ?? DetectAntiCheat(env, dir),
                ExtraExes = r.Extra,
            });
        }

        raws.ForEach(Add);
        foreach (Raw r in Collect(() => Uninstall(env, dirs, IsHdd))) // 제거 목록은 마지막: 앞의 결과와 겹치면 그쪽이 우선
        {
            Add(r);
        }

        return result;
    }

    // 한 원천의 읽기 실패가 다른 원천 열거를 막지 않게 함
    private static List<Raw> Collect(Func<IEnumerable<Raw>> source)
    {
        try
        {
            return source().ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or FormatException or PyValueException or ArgumentException)
        {
            return [];
        }
    }

    private static string? DetectAntiCheat(ILauncherEnv env, string dir)
    {
        try
        {
            return AntiCheat.Detect(env.EntryNames(dir, AntiCheatDepth));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null; // 열거 실패는 안티치트 모름으로 둠
        }
    }

    /// 후보 중 가장 큰 exe (같으면 경로가 짧은 것, 그다음 사전순). 제외 이름만 남으면 null.
    internal static string? PickExe(IEnumerable<(string Path, long Size)> files) =>
        files.Where(f => f.Path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && !ExcludedExeParts.Any(p => System.IO.Path.GetFileName(f.Path).Contains(p, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(f => f.Size).ThenBy(f => f.Path.Length).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(f => f.Path).FirstOrDefault();

    // 폴더 아래 maxDepth 단계까지의 항목 이름 (접근 못 하는 하위는 건너뜀).
    public static List<string> ListNames(string dir, int maxDepth)
    {
        var names = new List<string>();
        var options = new EnumerationOptions { IgnoreInaccessible = true };
        foreach (string entry in Directory.EnumerateFileSystemEntries(dir, "*", options))
        {
            names.Add(Path.GetFileName(entry));
            if (maxDepth > 1 && Directory.Exists(entry))
            {
                names.AddRange(ListNames(entry, maxDepth - 1));
            }
        }

        return names;
    }

    // 드라이브 문자 절대 경로만, 끝 역슬래시 없음 (루트 X:\ 는 그대로). UNC·상대 경로·잘못된 경로는 null.
    private static string? Normalize(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return full.Length >= 3 && char.IsAsciiLetter(full[0]) && full[1] == ':' && full[2] == '\\' ? (full.Length > 3 ? full.TrimEnd('\\') : full) : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    // 매니페스트의 exe 가 설치 폴더 안에 실제로 있으면 그것, 아니면 폴더에서 가장 큰 exe.
    private static string? ResolveExe(ILauncherEnv env, string dir, string? manifestExe)
    {
        if (!string.IsNullOrEmpty(manifestExe))
        {
            string? full = Normalize(Path.IsPathRooted(manifestExe) ? manifestExe : Path.Combine(dir, manifestExe));
            if (full is not null && full.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase) && env.FileExists(full))
            {
                return full;
            }
        }

        return PickExe(env.ExeFiles(dir, ExeDepth));
    }

    private static IEnumerable<Raw> Steam(ILauncherEnv env)
    {
        string? root = env.RegString(true, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath") ?? env.RegString(false, @"Software\Valve\Steam", "SteamPath"); // 서비스는 SYSTEM 이라 HKCU 는 사용자 것이 아님
        if (string.IsNullOrWhiteSpace(root))
        {
            yield break;
        }

        var libs = new List<string> { root };
        if (env.ReadText(Path.Combine(root, "steamapps", "libraryfolders.vdf")) is { } vdf)
        {
            libs.AddRange(LauncherManifests.ParseLibraryFolders(vdf));
        }

        foreach (string lib in libs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string apps = Path.Combine(lib, "steamapps");
            foreach (string file in env.Files(apps, "appmanifest_*.acf").Where(f => f.EndsWith(".acf", StringComparison.OrdinalIgnoreCase)))
            {
                SteamApp? app;
                try
                {
                    app = env.ReadText(file) is { } text ? LauncherManifests.ParseAppManifest(text) : null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
                {
                    continue; // 쓰는 중이거나 깨진 매니페스트 하나는 건너뜀
                }

                if (app is not null && app.InstallDir == Path.GetFileName(app.InstallDir) && app.InstallDir is not ("." or ".."))
                {
                    yield return new Raw(app.Name, "steam", Path.Combine(apps, "common", app.InstallDir), null, "steam.exe");
                }
            }
        }
    }

    private static IEnumerable<Raw> Epic(ILauncherEnv env)
    {
        string dir = Path.Combine(env.ProgramData, "Epic", "EpicGamesLauncher", "Data", "Manifests");
        foreach (string file in env.Files(dir, "*.item").Where(f => f.EndsWith(".item", StringComparison.OrdinalIgnoreCase)))
        {
            EpicApp? app;
            try
            {
                app = env.ReadText(file) is { } text ? LauncherManifests.ParseEpicItem(text) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
            {
                continue;
            }

            if (app is not null)
            {
                yield return new Raw(app.DisplayName, "epic", app.InstallLocation, app.LaunchExecutable, "EpicGamesLauncher.exe");
            }
        }
    }

    private static IEnumerable<Raw> Gog(ILauncherEnv env)
    {
        foreach (string id in env.RegSubKeys(GogKey))
        {
            string key = GogKey + "\\" + id;
            if (env.RegString(true, key, "path") is not { Length: > 0 } path)
            {
                continue;
            }

            string? exe = env.RegString(true, key, "exe");
            if (string.IsNullOrEmpty(exe) && env.RegString(true, key, "exeFile") is { Length: > 0 } file)
            {
                exe = Path.Combine(path, file);
            }

            yield return new Raw(env.RegString(true, key, "gameName") ?? string.Empty, "gog", path, exe, "GalaxyClient.exe");
        }
    }

    private static IEnumerable<Raw> Ubisoft(ILauncherEnv env)
    {
        foreach (string id in env.RegSubKeys(UbisoftKey))
        {
            if (env.RegString(true, UbisoftKey + "\\" + id, "InstallDir") is { Length: > 0 } dir)
            {
                yield return new Raw(string.Empty, "ubisoft", dir, null, "upc.exe");
            }
        }
    }

    private sealed class SystemLauncherEnv : ILauncherEnv
    {
        private const long MaxTextBytes = 4 << 20;
        private const int SizeDepth = 6;
        private const int SizeEntries = 20_000; // shortcut: 파일이 이만큼 넘게 잘게 쪼개진 폴더는 크기를 못 채우면 작다고 봄, 느려지는 게 문제되면 올림

        public string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        public string SystemDrive { get; } = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))![..2].ToUpperInvariant();

        public IReadOnlyList<string> FixedDrives() => DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed).Select(d => d.Name[..2].ToUpperInvariant()).ToList();

        public IReadOnlyList<string> SubDirs(string dir) =>
            Directory.Exists(dir) ? Directory.GetDirectories(dir, "*", new EnumerationOptions { IgnoreInaccessible = true }) : [];

        public IReadOnlyList<UninstallEntry> UninstallEntries()
        {
            var list = new List<UninstallEntry>();
            foreach ((bool machine, string root) in new[] { (true, UninstallKey), (true, UninstallKey32), (false, UninstallKeyUser) })
            {
                using RegistryKey? key = Open(machine, root);
                foreach (string id in key?.GetSubKeyNames() ?? [])
                {
                    using RegistryKey? item = key!.OpenSubKey(id);
                    if (item?.GetValue("InstallLocation") is string { Length: > 0 } loc && item.GetValue("DisplayName") is string { Length: > 0 } display && item.GetValue("SystemComponent") is not 1)
                    {
                        list.Add(new UninstallEntry(display, item.GetValue("Publisher") as string ?? string.Empty, loc));
                    }
                }
            }

            return list;
        }

        public bool FolderAtLeast(string dir, long bytes)
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, RecurseSubdirectories = true, MaxRecursionDepth = SizeDepth, AttributesToSkip = FileAttributes.ReparsePoint };
            long sum = 0;
            int seen = 0;
            foreach (FileInfo f in new DirectoryInfo(dir).EnumerateFiles("*", options))
            {
                sum += f.Length;
                if (sum >= bytes || ++seen >= SizeEntries)
                {
                    break;
                }
            }

            return sum >= bytes;
        }

        public string? RegString(bool machine, string subKey, string name)
        {
            using RegistryKey? key = Open(machine, subKey);
            return key?.GetValue(name) as string;
        }

        public IReadOnlyList<string> RegSubKeys(string subKey)
        {
            using RegistryKey? key = Open(true, subKey);
            return key?.GetSubKeyNames() ?? [];
        }

        public string? ReadText(string path) => File.Exists(path) && new FileInfo(path).Length <= MaxTextBytes ? File.ReadAllText(path) : null;

        public IReadOnlyList<string> Files(string dir, string pattern) => Directory.Exists(dir) ? Directory.GetFiles(dir, pattern) : [];

        public bool DirectoryExists(string path) => Directory.Exists(path);

        public bool FileExists(string path) => File.Exists(path);

        public IEnumerable<(string Path, long Size)> ExeFiles(string dir, int maxDepth)
        {
            var options = new EnumerationOptions { IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
            foreach (string f in Directory.EnumerateFiles(dir, "*.exe", options))
            {
                yield return (f, new FileInfo(f).Length);
            }

            if (maxDepth <= 0)
            {
                yield break;
            }

            foreach (string sub in Directory.EnumerateDirectories(dir, "*", options))
            {
                foreach ((string, long) x in ExeFiles(sub, maxDepth - 1))
                {
                    yield return x;
                }
            }
        }

        public IReadOnlyList<string> EntryNames(string dir, int maxDepth) => ListNames(dir, maxDepth);

        public bool? IncursSeekPenalty(string letter) => DiskInfo.Query(letter).IncursSeekPenalty;

        private static RegistryKey? Open(bool machine, string subKey)
        {
            using RegistryKey hive = RegistryKey.OpenBaseKey(machine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
            return hive.OpenSubKey(subKey);
        }
    }
}
