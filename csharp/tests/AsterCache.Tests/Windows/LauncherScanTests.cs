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
            Exes.Where(e => e.Key.StartsWith(dir + "\\", StringComparison.OrdinalIgnoreCase)).Select(e => (e.Key, e.Value));

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

    [Fact]
    public void NothingInstalledGivesEmptyList() => Assert.Empty(LauncherScan.Scan(new FakeEnv()));
}
