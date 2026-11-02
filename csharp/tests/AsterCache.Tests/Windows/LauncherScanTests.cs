using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

public sealed class LauncherScanTests
{
    private sealed class FakeEnv : ILauncherEnv
    {
        public Dictionary<string, string> Text { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Reg { get; } = new(StringComparer.OrdinalIgnoreCase); // "M|키|값" 또는 "U|키|값"

        public HashSet<string> Dirs { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, long> Exes { get; } = new(StringComparer.OrdinalIgnoreCase); // 전체 경로 -> 크기

        public Dictionary<string, string[]> Entries { get; } = new(StringComparer.OrdinalIgnoreCase); // 폴더 -> 항목 이름

        public Dictionary<string, bool?> Hdd { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> DiskQueries { get; } = [];

        public string ProgramData => @"C:\ProgramData";

        public string SystemDrive => "C:";

        public List<string> Drives { get; } = ["C:", "D:"];

        public List<UninstallEntry> Uninstalls { get; } = [];

        public Dictionary<string, long> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase); // 폴더 -> 전체 크기

        public HashSet<string> BigFiles { get; } = new(StringComparer.OrdinalIgnoreCase); // 256 MiB 이상 파일이 든 폴더 (exe 가 아닌 데이터 파일도 셈)

        public HashSet<string> Denied { get; } = new(StringComparer.OrdinalIgnoreCase); // exe 를 열거하면 접근 거부가 나는 폴더

        public IReadOnlyList<string> FixedDrives() => Drives;

        public IReadOnlyList<string> SubDirs(string dir) =>
            Dirs.Where(d => string.Equals(Path.GetDirectoryName(d), dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)).ToList();

        public IReadOnlyList<UninstallEntry> UninstallEntries() => Uninstalls;

        public bool FolderAtLeast(string dir, long bytes) => Sizes.GetValueOrDefault(dir) >= bytes;

        public bool FileAtLeast(string dir, long bytes) =>
            BigFiles.Contains(dir) || Exes.Any(x => x.Key.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase) && x.Value >= bytes);

        public string? RegString(bool machine, string subKey, string name) => Reg.GetValueOrDefault($"{(machine ? "M" : "U")}|{subKey}|{name}");

        public IReadOnlyList<string> RegSubKeys(string subKey) =>
            Reg.Keys.Select(k => k.Split('|')).Where(p => p[0] == "M" && p[1].StartsWith(subKey + "\\", StringComparison.OrdinalIgnoreCase))
                .Select(p => p[1][(subKey.Length + 1)..].Split('\\')[0]).Distinct().ToList();

        public string? ReadText(string path) => Text.GetValueOrDefault(path);

        public IReadOnlyList<string> Files(string dir, string pattern) =>
            Text.Keys.Where(k => string.Equals(Path.GetDirectoryName(k), dir, StringComparison.OrdinalIgnoreCase)).ToList();

        public bool DirectoryExists(string path) => Dirs.Contains(path.TrimEnd('\\'));

        public bool FileExists(string path) => Exes.ContainsKey(path);

        public IEnumerable<(string Path, long Size)> ExeFiles(string dir, int maxDepth) =>
            Denied.Contains(dir) ? throw new UnauthorizedAccessException() : Exes.Where(e => e.Key.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)).Select(e => (e.Key, e.Value));

        public List<int> EntryDepths { get; } = [];

        public IReadOnlyList<string> EntryNames(string dir, int maxDepth)
        {
            EntryDepths.Add(maxDepth);
            return Entries.GetValueOrDefault(dir) ?? [];
        }

        public bool? IncursSeekPenalty(string letter)
        {
            DiskQueries.Add(letter);
            return Hdd.GetValueOrDefault(letter);
        }
    }

    private static string Acf(string id, string name, string dir) => $"\"AppState\"\n{{\n\t\"appid\"\t\"{id}\"\n\t\"name\"\t\"{name}\"\n\t\"installdir\"\t\"{dir}\"\n}}\n";

    private static FakeEnv SteamEnv()
    {
        var env = new FakeEnv();
        env.Reg[@"M|SOFTWARE\WOW6432Node\Valve\Steam|InstallPath"] = @"C:\Steam";
        env.Text[@"C:\Steam\steamapps\libraryfolders.vdf"] = "\"libraryfolders\" { \"0\" { \"path\" \"C:\\\\Steam\" } \"1\" { \"path\" \"D:\\\\Lib\" } }";
        env.Text[@"C:\Steam\steamapps\appmanifest_1.acf"] = Acf("1", "Fast Game", "FastGame");
        env.Text[@"D:\Lib\steamapps\appmanifest_2.acf"] = Acf("2", "Slow Game", "SlowGame");
        env.Text[@"D:\Lib\steamapps\appmanifest_3.acf.123.tmp"] = Acf("3", "Temp", "Temp"); // 임시 파일은 건너뜀
        env.Dirs.UnionWith([@"C:\Steam\steamapps\common\FastGame", @"D:\Lib\steamapps\common\SlowGame"]);
        env.Exes[@"C:\Steam\steamapps\common\FastGame\fast.exe"] = 10;
        env.Exes[@"D:\Lib\steamapps\common\SlowGame\slow.exe"] = 10;
        env.Hdd["C:"] = false;
        env.Hdd["D:"] = true;
        return env;
    }

    [Fact]
    public void SteamGamesAreSplitByDriveType()
    {
        FakeEnv env = SteamEnv();

        IReadOnlyList<DiscoveredGame> games = LauncherScan.Scan(env);

        Assert.Equal(2, games.Count);
        DiscoveredGame fast = games.Single(g => g.Name == "Fast_Game");
        DiscoveredGame slow = games.Single(g => g.Name == "Slow_Game");
        Assert.Equal((false, "steam", "steam.exe", @"C:\Steam\steamapps\common\FastGame\fast.exe"), (fast.OnHdd, fast.Launcher, fast.TriggerExe, fast.ExePath));
        Assert.Equal(true, slow.OnHdd);
        Assert.Equal(@"D:\Lib\steamapps\common\SlowGame", slow.InstallDir);
    }

    [Fact]
    public void ScanCarriesTheSteamAndEpicFingerprints()
    {
        FakeEnv env = SteamEnv();
        env.Text[@"D:\Lib\steamapps\appmanifest_2.acf"] =
            "\"AppState\" { \"appid\" \"2\" \"name\" \"Slow Game\" \"installdir\" \"SlowGame\" \"InstalledDepots\" { \"3\" { \"manifest\" \"99\" } } }";
        env.Text[@"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\a.item"] =
            "{\"DisplayName\":\"Epic One\",\"InstallLocation\":\"D:\\\\Epic\\\\One\",\"AppName\":\"abc\",\"AppVersionString\":\"7\",\"LaunchExecutable\":\"one.exe\"}";
        env.Dirs.Add(@"D:\Epic\One");
        env.Exes[@"D:\Epic\One\one.exe"] = 1;

        IReadOnlyList<DiscoveredGame> games = LauncherScan.Scan(env);

        Assert.Equal("99", games.Single(g => g.Name == "Slow_Game").Steam!.InstalledDepots["3"]);
        Assert.Null(games.Single(g => g.Name == "Fast_Game").Epic);
        DiscoveredGame epic = games.Single(g => g.Launcher == "epic");
        Assert.Equal(("abc", "7"), (epic.Epic!.AppName, epic.Epic.AppVersionString));
        Assert.Null(epic.Steam);
    }

    [Fact]
    public void LooksLikeGameNeedsABigFileOrALauncherManifestTrace()
    {
        var env = new FakeEnv();
        env.Dirs.UnionWith([@"D:\Small", @"D:\Big", @"D:\Epic", @"D:\Epic\.egstore", @"D:\Steamy", @"D:\Steamy\Binaries", @"D:\Gog", @"D:\Lib\steamapps\common\Y", @"D:\Other\Y"]);
        env.BigFiles.Add(@"D:\Big");
        env.Exes[@"D:\Steamy\Binaries\steam_appid.txt"] = 1;
        env.Text[@"D:\Gog\goggame-1.info"] = "x";
        env.Text[@"D:\Lib\steamapps\appmanifest_5.acf"] = "x";

        Assert.False(LauncherScan.LooksLikeGame(@"D:\Small", env));
        Assert.True(LauncherScan.LooksLikeGame(@"D:\Big", env));
        Assert.True(LauncherScan.LooksLikeGame(@"D:\Epic", env));
        Assert.True(LauncherScan.LooksLikeGame(@"D:\Steamy", env)); // 하위 폴더 안의 steam_appid.txt
        Assert.True(LauncherScan.LooksLikeGame(@"D:\Gog", env));
        Assert.True(LauncherScan.LooksLikeGame(@"D:\Lib\steamapps\common\Y", env)); // 상위 steamapps 의 appmanifest
        Assert.False(LauncherScan.LooksLikeGame(@"D:\Other\Y", env));
    }

    [Theory]
    [InlineData("HYSafeMode.exe", true)]
    [InlineData("GameLauncher.exe", true)]
    [InlineData("unins000.exe", true)]
    [InlineData("CrashReportClient.exe", true)]
    [InlineData("Game-Win64-Shipping.exe", false)]
    [InlineData("Genshin.exe", false)]
    public void ExcludedExeNamesFollowThePickExeRules(string name, bool excluded) => Assert.Equal(excluded, LauncherScan.IsExcludedExeName(name));

    [Fact]
    public void ReselectExePicksAGameExeWhenTheRegisteredOneIsLauncherLike()
    {
        List<(string Path, long Size)> files = [(@"D:\HoYoPlay\launcher\HYSafeMode.exe", 900), (@"D:\HoYoPlay\launcher\HoYoPlay.exe", 800), (@"D:\HoYoPlay\games\Genshin\GenshinImpact.exe", 50)];

        Assert.Equal(@"D:\HoYoPlay\games\Genshin\GenshinImpact.exe", LauncherScan.ReselectExe(files, "HYSafeMode.exe"));
        Assert.Null(LauncherScan.ReselectExe(files, "GenshinImpact.exe")); // 이미 맞음
    }

    [Fact]
    public void ReselectExePrefersTheGamesSubfolderOverALauncherShellOutsideIt()
    {
        List<(string Path, long Size)> files = [(@"D:\GRYPHLINK\1.0\Games.exe", 900), (@"D:\GRYPHLINK\1.0\games\Arknights\Arknights.exe", 100)];

        Assert.Equal(@"D:\GRYPHLINK\1.0\games\Arknights\Arknights.exe", LauncherScan.ReselectExe(files, "Games.exe"));
        Assert.Null(LauncherScan.ReselectExe(files, "Arknights.exe"));
        Assert.Null(LauncherScan.ReselectExe([(@"D:\X\Big.exe", 900), (@"D:\X\Small.exe", 100)], "Small.exe")); // games 폴더도 제외 단어도 없으면 그대로
        Assert.Null(LauncherScan.ReselectExe([], "HYSafeMode.exe"));
        Assert.Null(LauncherScan.ReselectExe([(@"D:\X\updater.exe", 100)], "updater.exe")); // 고를 것이 없음
    }

    [Fact]
    public void EachDriveIsQueriedOnce()
    {
        FakeEnv env = SteamEnv();
        env.Text[@"D:\Lib\steamapps\appmanifest_4.acf"] = Acf("4", "Another", "Another");
        env.Dirs.Add(@"D:\Lib\steamapps\common\Another");

        LauncherScan.Scan(env);

        Assert.Equal(["C:", "D:"], env.DiskQueries.Order());
    }

    [Fact]
    public void SteamFallsBackToUserRegistryWhenMachineKeyIsMissing()
    {
        FakeEnv env = SteamEnv();
        env.Reg.Clear();
        env.Reg[@"U|Software\Valve\Steam|SteamPath"] = @"C:\Steam";

        Assert.Equal(2, LauncherScan.Scan(env).Count);
    }

    [Fact]
    public void LargestExeWinsAndExcludedNamesAreIgnored()
    {
        FakeEnv env = SteamEnv();
        const string dir = @"C:\Steam\steamapps\common\FastGame";
        env.Exes[dir + @"\bin\game.exe"] = 500;
        env.Exes[dir + @"\unins000.exe"] = 9_000;
        env.Exes[dir + @"\VC_Redist.x64.exe"] = 9_000;
        env.Exes[dir + @"\EasyAntiCheat_EOS_Setup.exe"] = 9_000;
        env.Exes[dir + @"\CrashHandler.exe"] = 9_000;

        DiscoveredGame fast = LauncherScan.Scan(env).Single(g => g.Name == "Fast_Game");

        Assert.Equal(dir + @"\bin\game.exe", fast.ExePath);
    }

    [Fact]
    public void FolderWithOnlyExcludedExesHasNoExe()
    {
        FakeEnv env = SteamEnv();
        env.Exes.Clear();
        env.Exes[@"C:\Steam\steamapps\common\FastGame\setup.exe"] = 100;

        Assert.Null(LauncherScan.Scan(env).Single(g => g.Name == "Fast_Game").ExePath);
    }

    [Fact]
    public void MissingInstallFolderAndDriveRootAreSkipped()
    {
        FakeEnv env = SteamEnv();
        env.Dirs.Remove(@"D:\Lib\steamapps\common\SlowGame");
        env.Reg[@"M|SOFTWARE\WOW6432Node\GOG.com\Games\1|gameName"] = "Whole Drive";
        env.Reg[@"M|SOFTWARE\WOW6432Node\GOG.com\Games\1|path"] = @"D:\";
        env.Dirs.Add(@"D:");

        IReadOnlyList<DiscoveredGame> games = LauncherScan.Scan(env);

        Assert.Equal(["Fast_Game"], games.Select(g => g.Name));
    }

    [Fact]
    public void UnsafeInstallDirNamesAreIgnored()
    {
        FakeEnv env = SteamEnv();
        env.Text[@"C:\Steam\steamapps\appmanifest_5.acf"] = Acf("5", "Evil", "..");
        env.Text[@"C:\Steam\steamapps\appmanifest_6.acf"] = Acf("6", "Evil2", @"..\..\Windows");

        Assert.Equal(2, LauncherScan.Scan(env).Count);
    }

    [Fact]
    public void BrokenManifestDoesNotStopOtherGamesOrLaunchers()
    {
        FakeEnv env = SteamEnv();
        env.Text[@"C:\Steam\steamapps\appmanifest_7.acf"] = "\"AppState\" {";
        env.Text[@"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\a.item"] =
            "{\"DisplayName\":\"Epic One\",\"InstallLocation\":\"C:\\\\Epic\\\\One\",\"LaunchExecutable\":\"one.exe\",\"AppCategories\":[\"games\"]}";
        env.Dirs.Add(@"C:\Epic\One");
        env.Exes[@"C:\Epic\One\one.exe"] = 1;

        IReadOnlyList<DiscoveredGame> games = LauncherScan.Scan(env);

        DiscoveredGame epic = games.Single(g => g.Launcher == "epic");
        Assert.Equal(("Epic_One", @"C:\Epic\One\one.exe", "EpicGamesLauncher.exe"), (epic.Name, epic.ExePath, epic.TriggerExe));
        Assert.Equal(3, games.Count);
    }

    [Fact]
    public void EpicManifestExeOutsideInstallFolderFallsBackToLargest()
    {
        var env = new FakeEnv();
        env.Text[@"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests\a.item"] =
            "{\"DisplayName\":\"Epic One\",\"InstallLocation\":\"C:\\\\Epic\\\\One\",\"LaunchExecutable\":\"..\\\\Other\\\\evil.exe\"}";
        env.Dirs.Add(@"C:\Epic\One");
        env.Exes[@"C:\Epic\Other\evil.exe"] = 99;
        env.Exes[@"C:\Epic\One\real.exe"] = 1;

        Assert.Equal(@"C:\Epic\One\real.exe", LauncherScan.Scan(env).Single().ExePath);
    }

    [Fact]
    public void GogAndUbisoftComeFromRegistry()
    {
        var env = new FakeEnv();
        env.Reg[@"M|SOFTWARE\WOW6432Node\GOG.com\Games\100|gameName"] = "Witcher: Test";
        env.Reg[@"M|SOFTWARE\WOW6432Node\GOG.com\Games\100|path"] = @"D:\GOG\Witcher";
        env.Reg[@"M|SOFTWARE\WOW6432Node\GOG.com\Games\100|exeFile"] = "witcher.exe";
        env.Reg[@"M|SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs\7|InstallDir"] = "D:/Ubisoft/Far Cry/";
        env.Dirs.UnionWith([@"D:\GOG\Witcher", @"D:\Ubisoft\Far Cry"]);
        env.Exes[@"D:\GOG\Witcher\witcher.exe"] = 5;
        env.Exes[@"D:\Ubisoft\Far Cry\bin\fc.exe"] = 5;

        IReadOnlyList<DiscoveredGame> games = LauncherScan.Scan(env);

        DiscoveredGame gog = games.Single(g => g.Launcher == "gog");
        DiscoveredGame ubi = games.Single(g => g.Launcher == "ubisoft");
        Assert.Equal(("Witcher_Test", @"D:\GOG\Witcher\witcher.exe", "GalaxyClient.exe"), (gog.Name, gog.ExePath, gog.TriggerExe));
        Assert.Equal(("Far_Cry", @"D:\Ubisoft\Far Cry", @"D:\Ubisoft\Far Cry\bin\fc.exe", "upc.exe"), (ubi.Name, ubi.InstallDir, ubi.ExePath, ubi.TriggerExe));
        Assert.Null(gog.OnHdd); // 가짜가 모름으로 답함
    }

    [Fact]
    public void SameNameAcrossLaunchersGetsSuffix()
    {
        FakeEnv env = SteamEnv();
        env.Reg[@"M|SOFTWARE\WOW6432Node\GOG.com\Games\1|gameName"] = "Fast Game";
        env.Reg[@"M|SOFTWARE\WOW6432Node\GOG.com\Games\1|path"] = @"C:\GOG\FastGame";
        env.Dirs.Add(@"C:\GOG\FastGame");

        Assert.Equal(["Fast_Game", "Fast_Game-2", "Slow_Game"], LauncherScan.Scan(env).Select(g => g.Name).Order());
    }

    [Fact]
    public void AntiCheatComesFromTheInstallFolderEntryNames()
    {
        FakeEnv env = SteamEnv();
        env.Entries[@"C:\Steam\steamapps\common\FastGame"] = ["fast.exe", "EasyAntiCheat", "settings.json"];

        IReadOnlyList<DiscoveredGame> games = LauncherScan.Scan(env);

        Assert.Equal("eac", games.Single(g => g.Name == "Fast_Game").AntiCheat);
        Assert.Null(games.Single(g => g.Name == "Slow_Game").AntiCheat);
        Assert.All(env.EntryDepths, d => Assert.Equal(4, d)); // 깊은 위치의 안티치트 폴더까지 이름을 봄
    }

    // ---- Riot ----

    private static FakeEnv RiotEnv()
    {
        var env = new FakeEnv();
        string root = Path.Combine(AppContext.BaseDirectory, "fixtures", "launchers", "riot", "Metadata");
        const string meta = @"C:\ProgramData\Riot Games\Metadata";
        env.Dirs.Add(meta);
        foreach (string dir in Directory.GetDirectories(root))
        {
            env.Dirs.Add(Path.Combine(meta, Path.GetFileName(dir)));
            foreach (string file in Directory.GetFiles(dir))
            {
                env.Text[Path.Combine(meta, Path.GetFileName(dir), Path.GetFileName(file))] = File.ReadAllText(file);
            }
        }

        env.Dirs.UnionWith([@"D:\Games\Riot Games\League of Legends", @"D:\Games\Riot Games\VALORANT\live", @"D:\Games\Riot Games\LoR", @"C:\Riot Games\League of Legends (PBE)", @"D:\Games\Riot Games\Patch"]);
        env.Exes[@"D:\Games\Riot Games\League of Legends\LeagueClient.exe"] = 30;
        env.Exes[@"D:\Games\Riot Games\League of Legends\LeagueClientUx.exe"] = 40; // 더 큰 exe 보다 이름 목록이 먼저
        env.Exes[@"D:\Games\Riot Games\League of Legends\Game\League of Legends.exe"] = 50;
        env.Exes[@"D:\Games\Riot Games\VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe"] = 90;
        env.Exes[@"D:\Games\Riot Games\VALORANT\live\VALORANT.exe"] = 10;
        env.Exes[@"D:\Games\Riot Games\LoR\small.exe"] = 1;
        env.Exes[@"D:\Games\Riot Games\LoR\LoR.exe"] = 5;
        env.Exes[@"C:\Riot Games\League of Legends (PBE)\LeagueClient.exe"] = 30;
        env.Exes[@"D:\Games\Riot Games\Patch\patch.exe"] = 30;
        env.Hdd["D:"] = true;
        return env;
    }

    [Fact]
    public void RiotLiveProductsComeFromMetadataAndTftIsMergedIntoLeague()
    {
        IReadOnlyList<DiscoveredGame> games = LauncherScan.Scan(RiotEnv());

        Assert.Equal(["Legends_of_Runeterra", "League_of_Legends", "VALORANT"], games.Select(g => g.Name)); // pbe·game_patch·같은 폴더의 TFT 는 없음
        DiscoveredGame lol = games[1];
        Assert.Equal(("riot", @"D:\Games\Riot Games\League of Legends", @"D:\Games\Riot Games\League of Legends\LeagueClient.exe", true, "RiotClientServices.exe", "vanguard"),
            (lol.Launcher, lol.InstallDir, lol.ExePath, lol.OnHdd, lol.TriggerExe, lol.AntiCheat));
        Assert.Equal(["League of Legends.exe"], lol.ExtraExes);
        Assert.Equal(@"D:\Games\Riot Games\VALORANT\live\VALORANT.exe", games[2].ExePath);
        Assert.Equal(["VALORANT-Win64-Shipping.exe"], games[2].ExtraExes);
        Assert.Equal(@"D:\Games\Riot Games\LoR\LoR.exe", games[0].ExePath); // 이름 목록이 없으면 가장 큰 exe
        Assert.Empty(games[0].ExtraExes);
        Assert.All(games, g => Assert.Equal("vanguard", g.AntiCheat));
    }

    [Fact]
    public void RiotProductWithoutFolderOrExeIsSkipped()
    {
        FakeEnv env = RiotEnv();
        env.Dirs.Remove(@"D:\Games\Riot Games\VALORANT\live");
        env.Exes.Remove(@"D:\Games\Riot Games\LoR\LoR.exe");
        env.Exes.Remove(@"D:\Games\Riot Games\LoR\small.exe");

        Assert.Equal(["League_of_Legends"], LauncherScan.Scan(env).Select(g => g.Name));
    }

    // ---- Xbox ----

    [Fact]
    public void XboxGamesAreTheContentFoldersOfEveryFixedDriveWithoutATrigger()
    {
        var env = new FakeEnv();
        env.Dirs.UnionWith([@"D:\XboxGames", @"D:\XboxGames\Forza Horizon", @"D:\XboxGames\Forza Horizon\Content", @"D:\XboxGames\NoContent", @"D:\XboxGames\Denied", @"D:\XboxGames\Denied\Content", @"D:\XboxGames\Empty", @"D:\XboxGames\Empty\Content", @"C:\XboxGames", @"C:\XboxGames\Fast", @"C:\XboxGames\Fast\Content"]);
        env.Exes[@"D:\XboxGames\Forza Horizon\Content\gamelaunchhelper.exe"] = 1;
        env.Exes[@"D:\XboxGames\Forza Horizon\Content\ForzaHorizon5.exe"] = 100;
        env.Exes[@"D:\XboxGames\Denied\Content\x.exe"] = 100;
        env.Exes[@"C:\XboxGames\Fast\Content\fast.exe"] = 100;
        env.Denied.Add(@"D:\XboxGames\Denied\Content");
        env.Hdd["C:"] = false;
        env.Hdd["D:"] = true;

        IReadOnlyList<DiscoveredGame> games = LauncherScan.Scan(env);

        Assert.Equal(["Fast", "Forza_Horizon"], games.Select(g => g.Name).Order()); // 접근 거부·Content 없음·exe 없음은 건너뜀
        DiscoveredGame forza = games.Single(g => g.Name == "Forza_Horizon");
        Assert.Equal(("xbox", @"D:\XboxGames\Forza Horizon\Content", @"D:\XboxGames\Forza Horizon\Content\ForzaHorizon5.exe", true, null), (forza.Launcher, forza.InstallDir, forza.ExePath, forza.OnHdd, forza.TriggerExe));
        Assert.Equal(false, games.Single(g => g.Name == "Fast").OnHdd); // 거부는 등록 쪽 몫
    }

    // ---- 제거 목록 ----

    private const long Gib = 1L << 30;

    private static FakeEnv UninstallEnv(params (string Name, string Publisher, string Dir, long Size, long ExeSize)[] items)
    {
        var env = new FakeEnv();
        env.Hdd["C:"] = true; // 시스템 볼륨이 HDD 여도 제외
        env.Hdd["D:"] = true;
        env.Hdd["E:"] = false;
        foreach ((string name, string publisher, string dir, long size, long exeSize) in items)
        {
            env.Uninstalls.Add(new UninstallEntry(name, publisher, dir));
            string plain = dir.Trim('"').TrimEnd('\\');
            env.Dirs.Add(plain);
            env.Sizes[plain] = size;
            env.BigFiles.Add(plain); // 이 도우미가 만드는 폴더는 모두 큰 데이터 파일이 든 진짜 게임 형태
            if (exeSize > 0)
            {
                env.Exes[plain + @"\game.exe"] = exeSize;
            }
        }

        return env;
    }

    [Fact]
    public void UninstallListFindsBigGamesOnHddsOutsideTheSystemVolume()
    {
        FakeEnv env = UninstallEnv(
            ("Big Game", "Some Studio", @"D:\Apps\Big Game", 2 * Gib, 7),
            ("Quoted", "", "\"D:\\Apps\\Quoted\\\"", Gib, 3),
            ("Twin", "Studio", @"D:\Apps\Twin", 2 * Gib, 7),
            ("Twin Again", "Studio", @"D:\Apps\Twin\", 2 * Gib, 7), // 다른 보기에 같은 폴더가 한 번 더
            ("Intelligent Games", "Intelligent Games Ltd.", @"D:\Apps\Intelligent", Gib, 7), // Intel 이 아님 (낱말 단위 비교)
            ("Office", "Microsoft Corporation", @"D:\Apps\Office", 5 * Gib, 7),
            ("Driver", "NVIDIA Corporation", @"D:\Apps\NV", 5 * Gib, 7),
            ("Chip", "Advanced Micro Devices, Inc.", @"D:\Apps\AMD", 5 * Gib, 7),
            ("Small", "Studio", @"D:\Apps\Small", Gib - 1, 7),
            ("OnSsd", "Studio", @"E:\Apps\OnSsd", 2 * Gib, 7),
            ("OnSystem", "Studio", @"C:\Apps\OnSystem", 2 * Gib, 7),
            ("NoExe", "Studio", @"D:\Apps\NoExe", 2 * Gib, 0),
            ("Missing", "Studio", @"D:\Apps\Missing", 2 * Gib, 7),
            ("Root", "Studio", @"D:\", 2 * Gib, 7),
            ("Launcher", "Studio", @"D:\Apps\Launcher", 2 * Gib, 7));
        env.Dirs.Remove(@"D:\Apps\Missing");
        env.Exes[@"D:\Apps\Launcher\Battle.net.exe"] = 1; // 런처 본체가 든 폴더는 게임이 아님

        IReadOnlyList<DiscoveredGame> games = LauncherScan.Scan(env);

        Assert.Equal(["Big_Game", "Quoted", "Twin", "Intelligent_Games"], games.Select(g => g.Name));
        Assert.All(games, g => Assert.Equal(("uninstall", true, null), (g.Launcher, g.OnHdd, g.TriggerExe)));
        Assert.Equal(@"D:\Apps\Big Game\game.exe", games[0].ExePath);
        Assert.Equal(@"D:\Apps\Quoted", games[1].InstallDir);
    }

    [Fact]
    public void UninstallEntriesOverlappingEarlierResultsOrEachOtherAreDropped()
    {
        FakeEnv env = SteamEnv();
        env.Hdd["D:"] = true;
        env.Dirs.UnionWith([@"D:\Lib", @"D:\Lib\steamapps"]);
        env.Uninstalls.AddRange(
        [
            new UninstallEntry("Slow Game (again)", "Valve", @"D:\Lib\steamapps\common\SlowGame"), // 같은 폴더
            new UninstallEntry("Steam Library", "Valve", @"D:\Lib"), // 바깥
            new UninstallEntry("Inside", "Valve", @"D:\Lib\steamapps\common\SlowGame\Sub"), // 안쪽
            new UninstallEntry("Outer App", "Studio", @"D:\Outer"),
            new UninstallEntry("Inner App", "Studio", @"D:\Outer\Inner"),
        ]);
        env.Dirs.UnionWith([@"D:\Lib\steamapps\common\SlowGame\Sub", @"D:\Outer", @"D:\Outer\Inner"]);
        foreach (string d in new[] { @"D:\Lib", @"D:\Lib\steamapps\common\SlowGame", @"D:\Lib\steamapps\common\SlowGame\Sub", @"D:\Outer", @"D:\Outer\Inner" })
        {
            env.Sizes[d] = 2 * Gib;
        }

        env.BigFiles.Add(@"D:\Outer");
        env.Exes[@"D:\Outer\o.exe"] = 4;
        env.Exes[@"D:\Outer\Inner\i.exe"] = 4;

        IReadOnlyList<DiscoveredGame> games = LauncherScan.Scan(env);

        Assert.Equal(["steam", "steam", "uninstall"], games.Select(g => g.Launcher)); // 제거 목록은 마지막, 겹치면 앞 결과 우선
        Assert.Equal("Outer_App", games[^1].Name);
    }

    [Fact]
    public void UninstallScanQueriesEachDriveOnceAndFailureDoesNotHideLauncherGames()
    {
        FakeEnv env = SteamEnv();
        env.Hdd["D:"] = true;
        env.Uninstalls.Add(new UninstallEntry("X", "Studio", @"D:\X"));
        env.Dirs.Add(@"D:\X");
        env.Sizes[@"D:\X"] = 2 * Gib;
        env.Denied.Add(@"D:\X");

        IReadOnlyList<DiscoveredGame> games = LauncherScan.Scan(env);

        Assert.Equal(2, games.Count);
        Assert.Equal(["C:", "D:"], env.DiskQueries.Order());
    }

    private const long Mib = 1L << 20;

    private static void Folder(FakeEnv env, string dir, long size, params (string Exe, long Size)[] exes)
    {
        env.Dirs.Add(dir);
        env.Sizes[dir] = size;
        foreach ((string exe, long exeSize) in exes)
        {
            env.Exes[dir + "\\" + exe] = exeSize;
        }
    }

    private static FakeEnv LauncherShapes()
    {
        FakeEnv env = UninstallEnv();
        // HoYoPlay 형태: 런처형 이름의 exe 뿐이고 내려받은 게임이 없음
        Folder(env, @"D:\HoYoPlay", 2 * Gib, ("launcher.exe", 40 * Mib), ("HYSafeMode.exe", 90 * Mib), ("HoYoPlayUpdate.exe", 3 * Mib));
        // GRYPHLINK 형태: 런처형 이름이 아닌 exe 가 있어도 큰 파일도 게임 하위 폴더도 없음
        Folder(env, @"D:\GRYPHLINK", 2 * Gib, ("Games.exe", 30 * Mib), ("crashpad_handler.exe", 2 * Mib));
        // 정상 게임: 256 MiB 이상 데이터 파일
        Folder(env, @"D:\Apps\Real Game", 20 * Gib, ("RealGame.exe", 80 * Mib), ("CrashReporter.exe", 5 * Mib));
        env.BigFiles.Add(@"D:\Apps\Real Game");
        // 런처 아래 큰 하위 게임 폴더가 있으면 게임이 든 폴더로 봄 (런처형 이름 exe 는 고르지 않음)
        Folder(env, @"D:\HoYoPlay Full", 30 * Gib, ("launcher.exe", 40 * Mib), ("HYSafeMode.exe", 90 * Mib));
        Folder(env, @"D:\HoYoPlay Full\games", 25 * Gib, ("GenshinImpact.exe", 70 * Mib));
        foreach ((string name, string dir) in new[] { ("HoYoPlay", @"D:\HoYoPlay"), ("GRYPHLINK", @"D:\GRYPHLINK"), ("Real Game", @"D:\Apps\Real Game"), ("HoYoPlay Full", @"D:\HoYoPlay Full") })
        {
            env.Uninstalls.Add(new UninstallEntry(name, "Studio", dir));
        }

        return env;
    }

    [Fact]
    public void LauncherShapedUninstallFoldersAreNotRegisteredAndAreReported()
    {
        ScanResult result = LauncherScan.Scan(LauncherShapes());

        Assert.Equal(["Real_Game", "HoYoPlay_Full"], result.Select(g => g.Name));
        Assert.Equal([@"D:\HoYoPlay", @"D:\GRYPHLINK"], result.Skipped.Select(s => s.InstallDir));
        Assert.Equal(["HoYoPlay", "GRYPHLINK"], result.Skipped.Select(s => s.Name));
    }

    [Fact]
    public void UninstallPicksTheBiggestNonLauncherExe()
    {
        ScanResult result = LauncherScan.Scan(LauncherShapes());

        Assert.Equal(@"D:\Apps\Real Game\RealGame.exe", result.Single(g => g.Name == "Real_Game").ExePath); // CrashReporter 는 제외
        Assert.Equal(@"D:\HoYoPlay Full\games\GenshinImpact.exe", result.Single(g => g.Name == "HoYoPlay_Full").ExePath); // HYSafeMode 가 더 커도 제외
    }

    [Theory]
    [InlineData("HYSafeMode.exe")]
    [InlineData("Launcher.exe")]
    [InlineData("GameClient.exe")]
    [InlineData("AutoUpdate.exe")]
    [InlineData("CrashSender.exe")]
    [InlineData("BugReport.exe")]
    [InlineData("unins000.exe")]
    [InlineData("vc_setup.exe")]
    [InlineData("EpicWebHelper.exe")]
    [InlineData("crashpad_handler.exe")]
    [InlineData("PrereqInstall.exe")]
    [InlineData("ACE-Service64.exe")]
    public void PickExeSkipsLauncherLikeAndInstallerNamesCaseInsensitively(string name)
    {
        (string, long)[] files = [(@"D:\G\" + name.ToUpperInvariant(), 900), (@"D:\G\game.exe", 5)];

        Assert.Equal(@"D:\G\game.exe", LauncherScan.PickExe(files));
        Assert.Null(LauncherScan.PickExe(files.Take(1)));
    }

    [Fact]
    public void PickExePrefersShippingExeOverHelpersAndClients()
    {
        (string, long)[] files =
        [
            (@"D:\T\Engine\Binaries\Win64\EpicWebHelper.exe", 900), (@"D:\T\Client\Small.exe", 100), (@"D:\T\TFT\Binaries\Win64\TFTClient-Win64-Shipping.exe", 50),
        ];

        Assert.Equal(@"D:\T\TFT\Binaries\Win64\TFTClient-Win64-Shipping.exe", LauncherScan.PickExe(files));
    }

    [Fact]
    public void PickExePrefersExesUnderGamesFolderOverLauncherShell()
    {
        (string, long)[] files = [(@"D:\L\1.4.0\Games.exe", 27000), (@"D:\L\games\X\Endfield.exe", 800), (@"D:\L\games\X\UnityCrashHandler64.exe", 900)];

        Assert.Equal(@"D:\L\games\X\Endfield.exe", LauncherScan.PickExe(files));
        Assert.Equal(@"D:\L\1.4.0\Games.exe", LauncherScan.PickExe(files.Take(1)));
    }

    [Fact]
    public void KnownLauncherGamesKeepALauncherLikeExeWhenItIsTheOnlyChoice()
    {
        FakeEnv env = SteamEnv();
        env.Exes.Remove(@"D:\Lib\steamapps\common\SlowGame\slow.exe");
        env.Exes[@"D:\Lib\steamapps\common\SlowGame\SlowLauncher.exe"] = 10;
        env.Exes[@"C:\Steam\steamapps\common\FastGame\FastClient.exe"] = 99; // 더 큰 런처형 이름이 있어도 일반 이름이 먼저
        env.Exes[@"C:\Steam\steamapps\common\FastGame\fast.exe"] = 10;

        IReadOnlyList<DiscoveredGame> games = LauncherScan.Scan(env);

        Assert.Equal(@"D:\Lib\steamapps\common\SlowGame\SlowLauncher.exe", games.Single(g => g.Name == "Slow_Game").ExePath); // 알려진 게임의 exe 를 잃지 않음
        Assert.Equal(@"C:\Steam\steamapps\common\FastGame\fast.exe", games.Single(g => g.Name == "Fast_Game").ExePath);
    }

    [Fact]
    public void NothingInstalledGivesEmptyList() => Assert.Empty(LauncherScan.Scan(new FakeEnv()));
}
