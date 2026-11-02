using AsterCache.Core.Discovery;

namespace AsterCache.Windows;

public static partial class LauncherScan
{
    private const string RiotClientExe = "RiotClientServices.exe";
    private const long MinUninstallBytes = 1L << 30;
    private const long MinGameContentBytes = 256L << 20; // 이 크기 이상의 파일이나 하위 게임 폴더가 없으면 런처 폴더로 봄

    /// 런처 본체의 프로그램 이름: 이 exe 가 든 폴더는 게임이 아니라 런처 (제거 목록에서 뺌).
    public static readonly IReadOnlyList<string> LauncherPrograms =
    [
        "steam.exe", "EpicGamesLauncher.exe", "GalaxyClient.exe", "Battle.net.exe", RiotClientExe, "EADesktop.exe", "upc.exe", "UbisoftConnect.exe",
    ];

    // Riot 제품 (Metadata 폴더 이름의 .live 앞부분) 별 표시 이름과 실행 exe 이름. 이름이 없으면 폴더에서 가장 큰 exe.
    // 전략 롤체스(teamfighttactics)는 리그 클라이언트로 실행하므로 같은 폴더면 하나로 합쳐지고, 따로 설치돼 있으면 같은 exe 이름을 씁니다.
    private static readonly string[] LeagueExes = ["LeagueClient.exe", "League of Legends.exe"];

    private static readonly Dictionary<string, (string Display, string[] Exes)> RiotProducts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["league_of_legends"] = ("League of Legends", LeagueExes),
        ["teamfighttactics"] = ("Teamfight Tactics", ["TFTClient-Win64-Shipping.exe", .. LeagueExes]),
        ["valorant"] = ("VALORANT", ["VALORANT.exe", "VALORANT-Win64-Shipping.exe"]),
        ["bacon"] = ("Legends of Runeterra", []),
        ["lion"] = ("2XKO", []),
    };

    // 일반 소프트웨어 게시자: 제거 목록에서 게임으로 보지 않음 (단어 단위로 비교).
    private static readonly string[] SoftwarePublishers =
    [
        "Microsoft", "Intel", "NVIDIA", "AMD", "Advanced Micro Devices", "Realtek", "Adobe", "Google", "Mozilla", "Oracle", "Apple", "Autodesk",
        "JetBrains", "Python Software Foundation", "VideoLAN", "Dell", "HP", "Lenovo", "Logitech", "Corsair",
    ];

    // Riot 은 ProgramData\Riot Games\Metadata\<제품>.live\<제품>.live.product_settings.yaml 에 설치 폴더를 적어 둡니다 (.pbe, .game_patch 는 실게임이 아님).
    private static IEnumerable<Raw> Riot(ILauncherEnv env)
    {
        string meta = Path.Combine(env.ProgramData, "Riot Games", "Metadata");
        foreach (string sub in env.SubDirs(meta).OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) // 리그가 전략 롤체스보다 먼저
        {
            string id = Path.GetFileName(sub);
            if (!id.EndsWith(".live", StringComparison.OrdinalIgnoreCase)
                || env.ReadText(Path.Combine(sub, id + ".product_settings.yaml")) is not { } yaml
                || LauncherManifests.ParseRiotInstallPath(yaml) is not { } dir
                || !env.DirectoryExists(dir)
                || ExeList(env, dir) is not { } files)
            {
                continue;
            }

            string product = id[..^".live".Length];
            (string display, string[] names) = RiotProducts.GetValueOrDefault(product, (product, Array.Empty<string>()));
            string[] found = names.Where(n => files.Any(f => Path.GetFileName(f.Path).Equals(n, StringComparison.OrdinalIgnoreCase))).ToArray();
            string? exe = found.Length > 0 ? files.First(f => Path.GetFileName(f.Path).Equals(found[0], StringComparison.OrdinalIgnoreCase)).Path : PickGameExe(files);
            if (exe is not null)
            {
                yield return new Raw(display, "riot", dir, exe, RiotClientExe) { Extra = found.Skip(1).ToList(), AntiCheat = "vanguard" }; // Riot 게임은 모두 Vanguard
            }
        }
    }

    // 각 고정 드라이브의 XboxGames\<게임>\Content. 읽을 수 없거나 exe 가 없으면 건너뜀, 런처 프로그램은 없음.
    private static IEnumerable<Raw> Xbox(ILauncherEnv env)
    {
        foreach (string letter in env.FixedDrives())
        {
            foreach (string game in env.SubDirs(letter + @"\XboxGames"))
            {
                string content = Path.Combine(game, "Content");
                if (env.DirectoryExists(content) && ExeList(env, content) is { } files && PickGameExe(files) is { } exe)
                {
                    yield return new Raw(Path.GetFileName(game), "xbox", content, exe, null);
                }
            }
        }
    }

    // 프로그램 제거 목록: HDD 의 시스템 볼륨 밖에서 1 GiB 이상이고 exe 가 있으며 일반 소프트웨어 게시자나 런처가 아닌 설치 폴더. 앞선 결과와 겹치는 폴더는 뺌.
    // 런처형 폴더 (exe 가 런처형 이름뿐이거나, 256 MiB 이상 파일과 큰 하위 게임 폴더가 모두 없음) 는 등록하지 않고 skipped 에 남깁니다.
    private static IEnumerable<Raw> Uninstall(ILauncherEnv env, IReadOnlyCollection<string> known, Func<string, bool?> isHdd, List<SkippedFolder> skipped)
    {
        var taken = new List<string>(known);
        foreach (UninstallEntry e in env.UninstallEntries())
        {
            string? dir = Normalize(e.InstallLocation.Trim().Trim('"'));
            if (dir is null || dir.Length <= 3 || dir[..2].Equals(env.SystemDrive, StringComparison.OrdinalIgnoreCase)
                || SoftwarePublishers.Any(p => HasWord(e.Publisher, p)) || taken.Any(t => Overlaps(t, dir))
                || isHdd(dir[..2].ToUpperInvariant()) != true || !env.DirectoryExists(dir) || !env.FolderAtLeast(dir, MinUninstallBytes)
                || ExeList(env, dir) is not { } files || files.Count == 0 || files.Any(f => LauncherPrograms.Contains(Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (PickExe(files) is not { } exe || !HasGameContent(env, dir))
            {
                skipped.Add(new SkippedFolder(e.DisplayName, dir));
                continue;
            }

            taken.Add(dir);
            yield return new Raw(e.DisplayName, "uninstall", dir, exe, null);
        }
    }

    private const int MaxTraceSubDirs = 64;

    /// 휴리스틱 후보의 설치 폴더가 게임처럼 보이는가: 256 MiB 이상 파일이 하나라도 있거나 (열거 상한은 FileAtLeast 와 같음),
    /// 런처 매니페스트 흔적 (steam_appid.txt, .egstore 폴더, goggame-*.info — 폴더 바로 아래나 그 하위 폴더 64 개 안, 또는 steamapps\common 의 상위 appmanifest *.acf) 이 있음. 읽을 수 없으면 false.
    public static bool LooksLikeGame(string dir, ILauncherEnv? env = null)
    {
        env ??= new SystemLauncherEnv();
        try
        {
            return env.FileAtLeast(dir, MinGameContentBytes) || HasLauncherTrace(env, dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasLauncherTrace(ILauncherEnv env, string dir)
    {
        foreach (string d in new[] { dir }.Concat(env.SubDirs(dir).Take(MaxTraceSubDirs)))
        {
            if (env.FileExists(Path.Combine(d, "steam_appid.txt")) || env.DirectoryExists(Path.Combine(d, ".egstore")) || env.Files(d, "goggame-*.info").Count > 0)
            {
                return true;
            }
        }

        string? common = Path.GetDirectoryName(dir);
        return common is not null && Path.GetFileName(common).Equals("common", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(common) is { } apps && env.Files(apps, "*.acf").Count > 0;
    }

    // 게임 내용이 있는 폴더: 256 MiB 이상 파일이 있거나, 그만큼 큰 하위 폴더에 게임 exe 가 있음.
    private static bool HasGameContent(ILauncherEnv env, string dir) =>
        env.FileAtLeast(dir, MinGameContentBytes)
        || env.SubDirs(dir).Any(sub => env.FolderAtLeast(sub, MinGameContentBytes) && ExeList(env, sub) is { } files && PickExe(files) is not null);

    // 폴더의 exe 목록, 접근 거부 등으로 읽을 수 없으면 null.
    private static List<(string Path, long Size)>? ExeList(ILauncherEnv env, string dir)
    {
        try
        {
            return env.ExeFiles(dir, ExeDepth).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool Overlaps(string a, string b)
    {
        string x = a.TrimEnd('\\') + "\\";
        string y = b.TrimEnd('\\') + "\\";
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }

    // text 안에 word 가 글자·숫자로 이어지지 않는 낱말로 들어 있는가 (대소문자 무시).
    private static bool HasWord(string text, string word)
    {
        for (int i = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(word, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            int end = i + word.Length;
            if ((i == 0 || !char.IsLetterOrDigit(text[i - 1])) && (end == text.Length || !char.IsLetterOrDigit(text[end])))
            {
                return true;
            }
        }

        return false;
    }
}
