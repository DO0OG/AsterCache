using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 런처 발견 등록의 걸러내기와 실행 중 읽기량 휴리스틱.
public sealed class AutoRegistryTests : IDisposable
{
    private readonly AutoRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private FakeWorld World => _rig.World;

    private static DiscoveredGame Found(string name, string dir, string exe, bool? hdd = true, string? trigger = "steam.exe") =>
        new(name, "steam", dir, dir + "\\" + exe, hdd, trigger);

    private IReadOnlyList<string> Registered() => _rig.Editor.Load()?.Games.Select(g => g.Name).ToList() ?? [];

    // ---- 런처 발견 ----

    [Fact]
    public void DiscoveryRegistersAnHddGameWithTheInferredFields()
    {
        World.Found.Add(Found("Mw4", @"D:\Games\Mw4", "mw4.exe"));

        int n = _rig.Registry().DiscoverOnce();

        GameSettings g = Assert.Single(_rig.Reload().Games);
        Assert.Equal(1, n);
        Assert.Equal(("Mw4", @"D:\Games\Mw4", "GAMES", "auto"), (g.Name, g.OriginRoot, g.VolumeLabel, g.Source));
        Assert.Equal(["mw4.exe"], g.ExeNames);
        Assert.Equal([@"D:\Games\Mw4"], g.AllowedRoots);
        Assert.Equal(Path.Combine(_rig.Dir, "plans", "Mw4.plan.json"), g.Plan);
        Assert.Equal(g.Plan + ".prefetch.json", g.Trace);
        Assert.False(g.Prewarm.OnServiceStart);
        Assert.Equal(["steam.exe"], g.Prewarm.TriggerExes);
        AutoGameEntry e = _rig.Status.Get("Mw4")!;
        Assert.Equal(("auto", "steam", AutoStage.Discovered), (e.Source, e.Launcher, e.Stage));
        Assert.Equal(["discovered"], _rig.Status.Events.Select(x => x.Kind));
    }

    [Fact]
    public void SettingsReloadClearsTemporaryRegistrationClaimsForRemovedGames()
    {
        World.Found.Add(Found("Removed", @"D:\Games\Removed", "removed.exe"));
        AutoRegistry registry = _rig.Registry();

        Assert.Equal(1, registry.DiscoverOnce());
        GameSettings removed = Assert.Single(_rig.Reload().Games);
        _rig.Editor.Remove(removed.Name, removed); // 자동 정리 경로: 제외 목록에 넣지 않음
        AsterSettings reloaded = _rig.Reload();
        registry.SettingsReloaded(reloaded, [removed]);

        Assert.Equal(1, registry.DiscoverOnce());
        Assert.Contains(removed.Name, Registered());
    }

    [Fact]
    public void LauncherDisplayNameGoesToTheStatusFileNeverToTheConfig()
    {
        string emojiTail = new string('a', 127) + "🎮";
        World.Found.Add(Found("Call of Duty: Modern Warfare", @"D:\Games\Cod", "cod.exe"));
        World.Found.Add(Found(emojiTail, @"D:\Games\Long", "long.exe"));

        _rig.Registry().DiscoverOnce();

        Assert.DoesNotContain("display_name", File.ReadAllText(_rig.Editor.ConfigPath), StringComparison.Ordinal); // 옛 판이 모르는 키로 설정을 거부하지 않게
        Assert.Equal("Call of Duty: Modern Warfare", _rig.Status.Get("Call_of_Duty_Modern_Warfare")!.DisplayName);
        string cut = _rig.Status.Get(Registered()[1])!.DisplayName!;
        Assert.Equal(new string('a', 127), cut); // 서로게이트 쌍을 가르지 않음
        AsterCache.Tray.AutoStatusFile loaded = AsterCache.Tray.AutoStatusFile.Load(Path.Combine(_rig.Dir, "auto_status.json"))!;
        Assert.Equal("Call of Duty: Modern Warfare", loaded.Games["Call_of_Duty_Modern_Warfare"].DisplayName);
    }

    [Fact]
    public void AlreadyRegisteredGameGetsTheLauncherNameOnceAndIsNotRewritten()
    {
        _rig.Add("old", "old.exe", @"D:\Games\Old", "auto");
        _rig.Status.Set("old", "auto", "steam", AutoStage.Done, null);
        World.Found.Add(Found("Old Game: Remastered", @"D:\Games\Old", "old.exe"));
        AutoRegistry registry = _rig.Registry();

        registry.DiscoverOnce();

        Assert.Equal("Old Game: Remastered", _rig.Status.Get("old")!.DisplayName);
        Assert.Equal(["old"], Registered());
        string path = Path.Combine(_rig.Dir, "auto_status.json");
        File.Delete(path);
        registry.DiscoverOnce();
        Assert.False(File.Exists(path)); // 같은 값이면 다시 저장하지 않음
    }

    [Fact]
    public void LauncherFoldersTheScanSkippedAreLoggedOnceNotRegistered()
    {
        World.Skipped.Add(new SkippedFolder("HoYoPlay", @"D:\HoYoPlay"));
        World.Found.Add(Found("Mw4", @"D:\Games\Mw4", "mw4.exe"));
        AutoRegistry registry = _rig.Registry();

        registry.DiscoverOnce();
        registry.DiscoverOnce();

        Assert.Equal(["Mw4"], Registered());
        string line = Assert.Single(_rig.Log.Lines, l => l.Contains("launcher folder skipped", StringComparison.Ordinal)); // 1 시간마다 반복되지 않음
        Assert.Contains("HoYoPlay", line, StringComparison.Ordinal);
    }

    [Fact]
    public void DiscoverySkipsGamesThatCannotOrShouldNotBeRegistered()
    {
        _rig.Add("old", "old.exe", @"D:\Games\Old", "manual");
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { IgnoreExes = ["ignored.exe"] } };
        World.Labels["E:"] = string.Empty;
        World.Labels["F:"] = new string('x', 33);
        World.Found.AddRange(
        [
            Found("Ssd", @"D:\Games\Ssd", "ssd.exe", hdd: false),
            Found("Unknown", @"D:\Games\Unknown", "unknown.exe", hdd: null),
            new DiscoveredGame("NoExe", "steam", @"D:\Games\NoExe", null, true, null),
            Found("Ignored", @"D:\Games\Ignored", "IGNORED.exe"),
            Found("DupExe", @"D:\Games\DupExe", "old.exe"),
            Found("Inside", @"D:\Games\Old\Sub", "inside.exe"),
            Found("Same", @"D:\Games\Old", "same.exe"),
            Found("Parent", @"D:\Games", "parent.exe"),
            Found("NoLabel", @"E:\Games\NoLabel", "nolabel.exe"),
            Found("LongLabel", @"F:\Games\LongLabel", "longlabel.exe"),
            Found("Ok", @"D:\Games\Ok", "ok.exe"),
        ]);

        int n = _rig.Registry().DiscoverOnce();

        Assert.Equal(1, n);
        Assert.Equal(["old", "Ok"], Registered());
        Assert.Contains(_rig.Log.Lines, l => l.Contains("NoLabel", StringComparison.Ordinal) && l.Contains("label", StringComparison.Ordinal));
        Assert.Null(_rig.Status.Get("NoLabel")); // 상태 파일에는 올리지 않고 로그만
    }

    [Fact]
    public void DiscoveryNeverEvictsAndStopsWhenTheListIsFull()
    {
        for (int i = 0; i < SettingsStore.MaxGames; i++)
        {
            _rig.Editor.Add(_rig.Input("g" + i, $"g{i}.exe", $@"D:\Games\G{i}", i == 0 ? "auto" : null));
        }

        _rig.Reload();
        World.Found.Add(Found("Extra", @"D:\Games\Extra", "extra.exe"));

        Assert.Equal(0, _rig.Registry().DiscoverOnce());

        Assert.Equal(SettingsStore.MaxGames, Registered().Count);
        Assert.Contains("g0", Registered());
        Assert.Contains(_rig.Log.Lines, l => l.Contains("full", StringComparison.Ordinal));
    }

    [Fact]
    public void DiscoveryMakesNamesUniqueAndHonorsTheSwitches()
    {
        _rig.Add("Dup", "other.exe", @"D:\Elsewhere");
        World.Found.Add(Found("Dup", @"D:\Games\Dup", "dup.exe"));
        AutoSettings on = _rig.Settings.Auto;

        _rig.Settings = _rig.Settings with { Auto = on with { Discover = false } };
        Assert.Equal(0, _rig.Registry().DiscoverOnce());
        _rig.Settings = _rig.Settings with { Auto = on with { Enabled = false } };
        Assert.Equal(0, _rig.Registry().DiscoverOnce());
        _rig.Settings = _rig.Settings with { Auto = on };
        Assert.Equal(1, _rig.Registry().DiscoverOnce());

        Assert.Equal(["Dup", "Dup-2"], Registered());
    }

    [Fact]
    public void DiscoveryAddsSeveralGamesFromOneScanWithoutWaitingForAReload()
    {
        World.Found.Add(Found("A", @"D:\Games\A", "shared.exe"));
        World.Found.Add(Found("B", @"D:\Games\B", "shared.exe")); // 같은 exe 이름: 설정이 거부하므로 먼저 등록한 하나만
        World.Found.Add(Found("C", @"D:\Games\C", "c.exe"));

        Assert.Equal(2, _rig.Registry().DiscoverOnce());

        Assert.Equal(["A", "C"], Registered());
    }

    [Theory]
    [InlineData("system")]
    [InlineData("ssd")]
    [InlineData("unknown")]
    public void DiscoveryRefusesSystemVolumeSsdAndUnknownDrivesEvenWhenTheScannerSaidHdd(string kind)
    {
        switch (kind)
        {
            case "system": (World.SystemDrive, World.Hdd["D:"]) = ("D:", false); break; // 시스템 SSD (시스템 HDD 는 발견을 허용)
            case "ssd": World.Hdd["D:"] = false; break;
            default: World.Hdd["D:"] = null; break;
        }

        World.Found.Add(Found("Mw4", @"D:\Games\Mw4", "mw4.exe"));
        AutoRegistry reg = _rig.Registry();

        Assert.Equal(0, reg.DiscoverOnce());
        Assert.Equal(0, reg.DiscoverOnce());

        Assert.Empty(Registered());
        Assert.Single(_rig.Log.Lines, l => l.Contains("skipped Mw4", StringComparison.Ordinal)); // 1 시간마다 다시 훑어도 한 번만
    }

    [Fact]
    public void DiscoveryRegistersGamesOnASystemHddAsPrewarmOnly()
    {
        World.SystemDrive = "D:"; // D: 는 기본이 HDD
        World.Found.Add(Found("Mw4", @"D:\Games\Mw4", "mw4.exe"));

        Assert.Equal(1, _rig.Registry().DiscoverOnce());

        Assert.Single(Registered());
        Assert.Equal(DriveCapabilities.PrewarmOnly, _rig.Status.Get("Mw4")!.Detail);
    }

    [Theory]
    [InlineData(@"D:\Users\me\AppData\Roaming\Foo\foo.exe", 0)]
    [InlineData(@"D:\Program Files\WindowsApps\Foo\foo.exe", 0)]
    [InlineData(@"D:\Program Files\Microsoft Office\foo.exe", 0)]
    [InlineData(@"D:\Program Files\Foo\foo.exe", 1)]
    public void HeuristicOnASystemHddKeepsSystemFoldersOutButAcceptsOtherGames(string image, int expected)
    {
        World.SystemDrive = "D:"; // 시스템 HDD

        Run(_rig.Registry(), 10, image, 900);

        Assert.Equal(expected, Registered().Count);
    }

    [Fact]
    public void HeuristicIgnoresProgramsOnTheSystemSsdAndLogsOncePerDrive()
    {
        World.Hdd["C:"] = false;
        AutoRegistry reg = _rig.Registry();

        Run(reg, 10, @"C:\Games\Bigig.exe", 900);
        Run(reg, 11, @"C:\Games\Other\other.exe", 900);

        Assert.Empty(Registered());
        Assert.Single(_rig.Log.Lines, l => l.Contains("ignoring programs on C:", StringComparison.Ordinal));
    }

    // ---- 휴리스틱 ----

    /// 기준선 틱 → 새 프로세스 → window 직전 → window 시점의 틱으로 판정까지 돌립니다.
    private void Run(AutoRegistry reg, int pid, string image, long readMib)
    {
        reg.HeuristicTick();
        World.Pids = [.. World.Pids, pid];
        World.Images[pid] = image;
        World.Reads[pid] = readMib << 20;
        reg.HeuristicTick();
        World.Now += TimeSpan.FromSeconds(AutoSettings.HeuristicWindowSeconds);
        reg.HeuristicTick();
    }

    [Theory]
    [InlineData(255, 0)]
    [InlineData(256, 1)]
    [InlineData(4000, 1)]
    public void ReadVolumeBoundaryIsInclusiveAt256MiB(long readMib, int expected)
    {
        Run(_rig.Registry(), 10, @"D:\Games\Foo\foo.exe", readMib);

        Assert.Equal(expected, Registered().Count);
    }

    [Fact]
    public void ProcessIsJudgedOnlyOnceItIs20SecondsOld()
    {
        AutoRegistry reg = _rig.Registry();
        reg.HeuristicTick();
        World.Pids = [10];
        World.Images[10] = @"D:\Games\Foo\foo.exe";
        World.Reads[10] = 300L << 20;
        reg.HeuristicTick();

        World.Now += TimeSpan.FromSeconds(AutoSettings.HeuristicWindowSeconds - 1);
        reg.HeuristicTick();
        Assert.Empty(Registered());

        World.Now += TimeSpan.FromSeconds(1);
        reg.HeuristicTick();
        Assert.Equal(["Foo"], Registered());
    }

    [Fact]
    public void RegisteredHeuristicGameUsesTheInstallFolderAndTheHeuristicLauncher()
    {
        Run(_rig.Registry(), 10, @"D:\Games\Foo Bar\Binaries\Win64\foo.exe", 300);

        GameSettings g = Assert.Single(_rig.Reload().Games);
        Assert.Equal(("Foo_Bar", @"D:\Games\Foo Bar", "auto", "GAMES"), (g.Name, g.OriginRoot, g.Source, g.VolumeLabel));
        Assert.Equal(["foo.exe"], g.ExeNames);
        Assert.Equal([@"D:\Games\Foo Bar"], g.AllowedRoots);
        Assert.False(g.Prewarm.OnServiceStart);
        Assert.Equal("heuristic", _rig.Status.Get("Foo_Bar")!.Launcher);
        Assert.Equal(["discovered"], _rig.Status.Events.Select(x => x.Kind));
    }

    [Fact]
    public void ProcessesAlreadyRunningAtStartAndThoseThatDieEarlyAreNotCandidates()
    {
        World.Pids = [5];
        World.Images[5] = @"D:\Games\Old\old.exe";
        World.Reads[5] = 900L << 20;
        AutoRegistry reg = _rig.Registry();
        reg.HeuristicTick(); // 기준선

        World.Pids = [5, 6];
        World.Images[6] = @"D:\Games\Quick\quick.exe";
        World.Reads[6] = 900L << 20;
        reg.HeuristicTick();
        World.Pids = [5];
        World.Now += TimeSpan.FromSeconds(30);
        reg.HeuristicTick();

        Assert.Empty(Registered());
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\tool.exe")]
    [InlineData(@"C:\Program Files\AsterCache\tool.exe")]
    [InlineData(@"D:\Apps\Steam\steam.exe")]
    [InlineData(@"D:\Apps\Steam\SteamWebHelper.exe")]
    [InlineData(@"D:\Apps\Epic\EpicGamesLauncher.exe")]
    [InlineData(@"D:\Apps\Gog\GalaxyClient.exe")]
    [InlineData(@"D:\Apps\Ubisoft\upc.exe")]
    [InlineData(@"D:\Apps\Ubisoft\UbisoftConnect.exe")]
    [InlineData(@"D:\Apps\Blizzard\Battle.net.exe")]
    [InlineData(@"D:\Apps\Blizzard\Agent.exe")]
    [InlineData(@"D:\Apps\EA\EADesktop.exe")]
    [InlineData(@"D:\Apps\Riot\RiotClientServices.exe")]
    [InlineData(@"D:\Apps\explorer.exe")]
    [InlineData(@"D:\Apps\Aster\aster.exe")]
    [InlineData(@"D:\Apps\Aster\AsterCache.Tray.exe")]
    [InlineData(@"D:\Apps\Aster\asterCache.service.EXE")]
    [InlineData(@"D:\Games\Skip\skip.exe")]
    [InlineData(@"E:\Games\OnSsd\game.exe")]
    [InlineData(@"G:\Games\Unknown\game.exe")]
    [InlineData(@"D:\bin\x64\game.exe")]
    [InlineData(@"D:\game.exe")]
    public void ExcludedProcessesAreNeverRegistered(string image)
    {
        World.Hdd["E:"] = false;
        World.Hdd["G:"] = null;
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { IgnoreExes = ["skip.exe"] } };

        Run(_rig.Registry(), 10, image, 900);

        Assert.Empty(Registered());
    }

    [Theory]
    [InlineData(@"D:\Git\Repo\tests\bin\Release\net10.0-windows\testhost.exe")]
    [InlineData(@"D:\Git\Repo\obj\Debug\game.exe")]
    [InlineData(@"D:\Work\.git\hooks\game.exe")]
    [InlineData(@"D:\Work\node_modules\app\game.exe")]
    [InlineData(@"D:\Users\Me\AppData\Local\Temp\Prog\prog.exe")]
    [InlineData(@"D:\Git\Repo\BIN\DEBUG\net10.0\game.exe")]
    [InlineData(@"D:\Games\Foo\TESTHOST.EXE")]
    [InlineData(@"D:\Games\Foo\testhost.net10.0.exe")]
    [InlineData(@"D:\Games\Foo\dotnet.exe")]
    [InlineData(@"D:\Games\Foo\MSBuild.exe")]
    [InlineData(@"D:\Games\Foo\vstest.console.exe")]
    [InlineData(@"D:\Games\Foo\python.exe")]
    [InlineData(@"D:\Games\Foo\pythonw3.12.exe")]
    [InlineData(@"D:\Games\Foo\node.exe")]
    [InlineData(@"D:\Games\Foo\java.exe")]
    [InlineData(@"D:\Games\Foo\javaw.exe")]
    [InlineData(@"D:\Games\Foo\powershell.exe")]
    [InlineData(@"D:\Games\Foo\pwsh.exe")]
    [InlineData(@"D:\Games\Foo\cmd.exe")]
    [InlineData(@"D:\Games\Foo\conhost.exe")]
    [InlineData(@"D:\Games\Foo\7zG.exe")]
    [InlineData(@"D:\Games\Foo\WinRAR.exe")]
    public void BuildOutputsDevToolsAndArchiversAreNeverGameCandidates(string image)
    {
        Run(_rig.Registry(), 10, image, 900);

        Assert.Empty(Registered());
        Assert.True(AutoRegistry.IsNonGameImage(image));
    }

    [Theory]
    [InlineData(@"D:\Games\Foo\foo.exe")]
    [InlineData(@"D:\Games\Foo\Binaries\Win64\foo-Win64-Shipping.exe")]
    [InlineData(@"D:\Games\Nodes\node_game.exe")]
    [InlineData(@"D:\Games\Cmdr\cmdr.exe")]
    [InlineData(@"D:\Games\Foo\Bin64\foo.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe")]
    [InlineData(@"D:\Games\The Witcher 3\bin\x64\witcher3.exe")]
    [InlineData(@"D:\Games\Foo\bin\x64\foo.exe")]
    [InlineData(@"D:\Users\Me\AppData\Local\Programs\Game\game.exe")]
    public void OrdinaryGameImagesAreNotExcludedByTheFixedRules(string image) => Assert.False(AutoRegistry.IsNonGameImage(image));

    [Fact]
    public void FixedExclusionsDoNotDependOnOrChangeIgnoreExes()
    {
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { IgnoreExes = [] } };

        Run(_rig.Registry(), 10, @"D:\Games\Foo\testhost.exe", 900);

        Assert.Empty(Registered());
        Assert.Empty(_rig.Settings.Auto.IgnoreExes);
    }

    [Fact]
    public void FolderThatDoesNotLookLikeAGameIsNotRegisteredAndLoggedOnce()
    {
        World.LooksLikeGame = _ => false;
        AutoRegistry reg = _rig.Registry();

        Run(reg, 10, @"D:\Tools\Tiny\tiny.exe", 900);
        Run(reg, 11, @"D:\Tools\Tiny\tiny2.exe", 900);

        Assert.Empty(Registered());
        Assert.Single(_rig.Log.Lines, l => l.Contains("does not look like a game", StringComparison.Ordinal));
    }

    [Fact]
    public void GameLikeFolderCheckGetsTheInstallRootAndAllowsTheRegistration()
    {
        var seen = new List<string>();
        World.LooksLikeGame = d =>
        {
            seen.Add(d);
            return d == @"D:\Games\Foo Bar";
        };

        Run(_rig.Registry(), 10, @"D:\Games\Foo Bar\Binaries\Win64\foo.exe", 900);

        Assert.Equal([@"D:\Games\Foo Bar"], seen);
        Assert.Equal(["Foo_Bar"], Registered());
    }

    // ---- 제거 목록 게임의 exe 재평가 ----

    private void UninstallGame(string name, string exe, string root, string launcher = "uninstall", string source = "auto")
    {
        _rig.Add(name, exe, root, source);
        _rig.Status.Set(name, source, launcher, AutoStage.Discovered, null);
    }

    [Fact]
    public void FirstDiscoveryReselectsALauncherLikeExeOfAnUninstallGameAndKeepsTheOtherNames()
    {
        UninstallGame("HoYoPlay", "HYSafeMode.exe", @"D:\HoYoPlay");
        _rig.Editor.AddExeName("HoYoPlay", "extra.exe");
        _rig.Reload();
        World.ExeFiles[@"D:\HoYoPlay"] = [(@"D:\HoYoPlay\launcher\HYSafeMode.exe", 900), (@"D:\HoYoPlay\games\Genshin\GenshinImpact.exe", 50)];

        _rig.Registry().DiscoverOnce();

        Assert.Equal(["GenshinImpact.exe", "extra.exe"], _rig.Reload().Games.Single().ExeNames);
        Assert.Single(_rig.Log.Lines, l => l.Contains("HoYoPlay: main exe changed from HYSafeMode.exe to GenshinImpact.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void GamesFolderExeReplacesALauncherShellOutsideIt()
    {
        UninstallGame("GRYPHLINK", "Games.exe", @"D:\GRYPHLINK");
        World.ExeFiles[@"D:\GRYPHLINK"] = [(@"D:\GRYPHLINK\1.0\Games.exe", 900), (@"D:\GRYPHLINK\1.0\games\Arknights\Arknights.exe", 100)];

        _rig.Registry().DiscoverOnce();

        Assert.Equal(["Arknights.exe"], _rig.Reload().Games.Single().ExeNames);
    }

    [Fact]
    public void ReselectRunsOnlyOncePerServiceStartAndOnlyForAutoUninstallGames()
    {
        UninstallGame("Steamy", "Safe.exe", @"D:\Steamy", launcher: "steam");
        UninstallGame("Manual", "Safe2.exe", @"D:\Manual", source: "manual");
        UninstallGame("Fine", "Fine.exe", @"D:\Fine");
        UninstallGame("Late", "LateSafe.exe", @"D:\Late");
        World.ExeFiles[@"D:\Steamy"] = [(@"D:\Steamy\Safe.exe", 9), (@"D:\Steamy\Real.exe", 5)];
        World.ExeFiles[@"D:\Manual"] = [(@"D:\Manual\Safe2.exe", 9), (@"D:\Manual\Real2.exe", 5)];
        World.ExeFiles[@"D:\Fine"] = [(@"D:\Fine\Fine.exe", 9), (@"D:\Fine\Small.exe", 5)];
        AutoRegistry reg = _rig.Registry();

        reg.DiscoverOnce();
        World.ExeFiles[@"D:\Late"] = [(@"D:\Late\LateSafe.exe", 9), (@"D:\Late\LateReal.exe", 5)]; // 첫 주기 뒤에 생긴 목록
        reg.DiscoverOnce();

        Dictionary<string, string> exes = _rig.Reload().Games.ToDictionary(g => g.Name, g => g.ExeNames[0]);
        Assert.Equal(("Safe.exe", "Safe2.exe", "Fine.exe", "LateSafe.exe"), (exes["Steamy"], exes["Manual"], exes["Fine"], exes["Late"]));
    }

    [Fact]
    public void ReselectSkipsAnExeAnotherGameAlreadyUsesAndEmptyListings()
    {
        UninstallGame("A", "ASafe.exe", @"D:\A");
        _rig.Add("B", "Real.exe", @"D:\B", "auto");
        UninstallGame("C", "CSafe.exe", @"D:\C"); // 열거가 비어 있음
        World.ExeFiles[@"D:\A"] = [(@"D:\A\ASafe.exe", 9), (@"D:\A\Real.exe", 5)];

        _rig.Registry().DiscoverOnce();

        Dictionary<string, string> exes = _rig.Reload().Games.ToDictionary(g => g.Name, g => g.ExeNames[0]);
        Assert.Equal(("ASafe.exe", "CSafe.exe"), (exes["A"], exes["C"]));
    }

    [Fact]
    public void ReselectIsSkippedWhileAutoDiscoveryIsOff()
    {
        UninstallGame("HoYoPlay", "HYSafeMode.exe", @"D:\HoYoPlay");
        World.ExeFiles[@"D:\HoYoPlay"] = [(@"D:\HoYoPlay\games\G\Game.exe", 50), (@"D:\HoYoPlay\HYSafeMode.exe", 900)];
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { Discover = false } };
        AutoRegistry reg = _rig.Registry();

        reg.DiscoverOnce();

        Assert.Equal("HYSafeMode.exe", _rig.Reload().Games.Single().ExeNames[0]);
    }

    // ---- 상주 유틸리티 (고정 제외 목록) ----

    private const string SteamApps = @"D:\SteamLibrary\steamapps";

    private static string Acf(string appId, string installDir) => $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{appId}\"\n\t\"name\"\t\t\"x\"\n\t\"installdir\"\t\t\"{installDir}\"\n}}\n";

    [Theory]
    [InlineData(@"D:\Apps\WallpaperEngine\wallpaper64.exe")]
    [InlineData(@"D:\Apps\WallpaperEngine\bin\WALLPAPER32.EXE")]
    [InlineData(@"D:\Apps\YoloMouse\YoloMouse.exe")]
    [InlineData(@"D:\Apps\obs-studio\bin\64bit\obs64.exe")]
    [InlineData(@"D:\SteamLibrary\steamapps\common\SteamVR\bin\win64\vrserver.exe")]
    public void ResidentUtilityProcessesAreNeverGameCandidates(string image)
    {
        Run(_rig.Registry(), 10, image, 900);

        Assert.Empty(Registered());
    }

    [Fact]
    public void DiscoverySkipsResidentUtilityExesAndStillRegistersOthers()
    {
        World.Found.Add(Found("Wallpaper Engine", @"D:\Games\WallpaperEngine", "wallpaper64.exe"));
        World.Found.Add(Found("Ok", @"D:\Games\Ok", "ok.exe"));

        int n = _rig.Registry().DiscoverOnce();

        Assert.Equal(1, n);
        Assert.Equal(["Ok"], Registered());
    }

    [Fact]
    public void ResidentAutoGamesAreUnregisteredOnceAndNotRegisteredAgain()
    {
        UninstallGame("wallpaper_engine", "main.exe", $@"{SteamApps}\common\wallpaper_engine", launcher: "steam");
        UninstallGame("ExeOnly", "wallpaper32.exe", @"D:\Games\ExeOnly", launcher: "heuristic");
        UninstallGame("Real", "real.exe", $@"{SteamApps}\common\Real", launcher: "steam");
        UninstallGame("Mixed", "wallpaper64.exe", @"D:\Games\Mixed", launcher: "heuristic");
        _rig.Editor.AddExeName("Mixed", "mixed.exe");
        UninstallGame("OtherFolder", "other.exe", $@"{SteamApps}\common\OtherFolder", launcher: "steam");
        UninstallGame("Manual", "wallpaperui.exe", @"D:\Games\Manual", source: "manual");
        _rig.Reload();
        World.Texts[$@"{SteamApps}\appmanifest_431960.acf"] = Acf("431960", "wallpaper_engine");
        World.Texts[$@"{SteamApps}\appmanifest_1283970.acf"] = Acf("1283970", "SomethingElse");
        AutoRegistry reg = _rig.Registry();

        reg.DiscoverOnce();
        _rig.Reload();
        reg.DiscoverOnce();
        Run(reg, 10, $@"{SteamApps}\common\wallpaper_engine\main.exe", 900); // 해제한 유틸리티의 설치 폴더 프로세스는 다시 후보가 되지 않음
        Run(reg, 11, $@"{SteamApps}\common\OtherFolder\other2.exe", 900); // 목록 appid 의 폴더가 아니면 전처럼 후보 (이미 등록된 폴더의 exe 추가)

        Assert.Equal(["Real", "Mixed", "OtherFolder", "Manual"], Registered());
        Assert.Empty(_rig.Settings.Auto.IgnoreExes); // 자동 정리는 사용자 제외 목록을 건드리지 않음
        Assert.Equal(["other.exe", "other2.exe"], _rig.Reload().Games.Single(g => g.Name == "OtherFolder").ExeNames);
        Assert.Single(_rig.Log.Lines, l => l.Contains("unregistered wallpaper_engine", StringComparison.Ordinal));
        Assert.Single(_rig.Log.Lines, l => l.Contains("unregistered ExeOnly", StringComparison.Ordinal));
        Assert.Equal(2, _rig.Log.Lines.Count(l => l.Contains("unregistered", StringComparison.Ordinal)));
    }

    [Fact]
    public void ManualEntryRegisteredAfterTheSettingsWereReadIsNotUnregistered()
    {
        UninstallGame("Wp", "wallpaper64.exe", @"D:\Games\Wp", launcher: "heuristic");
        _rig.Editor.Remove("Wp"); // 사용자가 지우고 같은 이름·같은 exe 로 직접 등록 (서비스의 메모리 설정은 아직 옛것)
        _rig.Editor.Add(_rig.Input("Wp", "wallpaper64.exe", @"D:\Games\Wp", "manual"));
        AutoRegistry reg = _rig.Registry();

        reg.DiscoverOnce();

        GameSettings kept = Assert.Single(_rig.Editor.Load()!.Games);
        Assert.Equal(("Wp", "manual"), (kept.Name, kept.Source));
        Assert.DoesNotContain(_rig.Log.Lines, l => l.Contains("unregistered", StringComparison.Ordinal));
    }

    [Fact]
    public void ResidentAutoGameIsUnregisteredEvenWhileRunningButNotWhileRelocating()
    {
        UninstallGame("Wp", "wallpaper64.exe", @"D:\Games\Wp", launcher: "heuristic");
        UninstallGame("Obs", "obs64.exe", @"D:\Games\Obs", launcher: "heuristic");
        _rig.Status.Set("Obs", "auto", "heuristic", AutoStage.Relocating, null);
        World.RunningExes.Add("wallpaper64.exe"); // 상주 프로그램은 계속 실행 중이어도 해제됨
        AutoRegistry reg = _rig.Registry();

        reg.DiscoverOnce();

        Assert.Equal(["Obs"], Registered()); // 재배치 중인 항목만 미룸

        _rig.Reload();
        reg.DiscoverOnce();
        Assert.Equal(["Obs"], Registered());

        _rig.Status.Set("Obs", "auto", "heuristic", AutoStage.Done, null);
        _rig.Reload();
        reg.DiscoverOnce();
        Assert.Empty(Registered());
    }

    [Fact]
    public void ResidentReviewIsSkippedWhileAutoDiscoveryIsOff()
    {
        UninstallGame("Wp", "wallpaper64.exe", @"D:\Games\Wp", launcher: "heuristic");
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { Discover = false } };

        _rig.Registry().DiscoverOnce();

        Assert.Equal(["Wp"], Registered());
    }

    [Fact]
    public void ExeAlreadyRegisteredIsSkipped()
    {
        _rig.Add("known", "known.exe", @"D:\Games\Known", "auto");

        Run(_rig.Registry(), 10, @"D:\Elsewhere\KNOWN.EXE", 900);

        Assert.Equal(["known"], Registered());
        Assert.Equal(["known.exe"], _rig.Settings.Games[0].ExeNames);
    }

    [Fact]
    public void ExeInsideAnAutoGameIsAddedToItButNotInsideAManualOne()
    {
        _rig.Add("auto1", "a.exe", @"D:\Games\A", "auto");
        _rig.Add("man", "m.exe", @"D:\Games\M");
        AutoRegistry reg = _rig.Registry();

        Run(reg, 10, @"D:\Games\A\sub\launcher2.exe", 300);
        _rig.Reload();
        Run(reg, 11, @"D:\Games\M\m2.exe", 300);
        _rig.Reload();

        Assert.Equal(["a.exe", "launcher2.exe"], _rig.Settings.Games.Single(g => g.Name == "auto1").ExeNames);
        Assert.Equal(["m.exe"], _rig.Settings.Games.Single(g => g.Name == "man").ExeNames);
        Assert.Equal(["auto1", "man"], Registered());
    }

    [Fact]
    public void ExeListOfAnAutoGameStopsAtSixteen()
    {
        _rig.Editor.Add(_rig.Input("full", "e0.exe", @"D:\Games\Full", "auto") with { ExeNames = Enumerable.Range(0, AutoRegistry.MaxExeNames).Select(i => $"e{i}.exe").ToList() });
        _rig.Reload();

        Run(_rig.Registry(), 10, @"D:\Games\Full\extra.exe", 300);

        Assert.Equal(AutoRegistry.MaxExeNames, _rig.Reload().Games[0].ExeNames.Count);
    }

    [Fact]
    public void TwoProcessesOfTheSameExeRegisterOneGameBeforeTheSettingsAreReloaded()
    {
        AutoRegistry reg = _rig.Registry();
        reg.HeuristicTick();
        World.Pids = [10, 11];
        foreach (int pid in World.Pids)
        {
            World.Images[pid] = @"D:\Games\Foo\foo.exe";
            World.Reads[pid] = 300L << 20;
        }

        reg.HeuristicTick();
        World.Now += TimeSpan.FromSeconds(20);
        reg.HeuristicTick();

        Assert.Equal(["Foo"], Registered());
    }

    [Fact]
    public void FullListEvictsTheLeastRecentlyPlayedAutoGameForAPlayedOne()
    {
        for (int i = 0; i < SettingsStore.MaxGames; i++)
        {
            _rig.Editor.Add(_rig.Input("g" + i, $"g{i}.exe", $@"D:\Games\G{i}", i is 3 or 4 ? "auto" : null));
        }

        _rig.Reload();
        _rig.Status.MarkPlayed("g4", "auto", AutoStage.WaitingIdle, World.Now); // g3 는 한 번도 안 함
        _rig.Status.Set("g3", "auto", null, AutoStage.Discovered, null);

        Run(_rig.Registry(), 10, @"D:\Games\Foo\foo.exe", 300);

        Assert.Equal(SettingsStore.MaxGames, Registered().Count);
        Assert.DoesNotContain("g3", Registered());
        Assert.Contains("Foo", Registered());
        Assert.Contains("g4", Registered());
        Assert.Null(_rig.Status.Get("g3"));
        Assert.Contains(_rig.Log.Lines, l => l.Contains("removed the least recently played auto game g3", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(@"D:\Games\Foo\Binaries\Win64", @"D:\Games\Foo")]
    [InlineData(@"D:\Games\Foo\bin\x64", @"D:\Games\Foo")]
    [InlineData(@"D:\Games\Foo", @"D:\Games\Foo")]
    [InlineData(@"D:\Games\Foo\", @"D:\Games\Foo")]
    [InlineData(@"D:\X\bin\bin\bin\bin", @"D:\X\bin")] // 최대 3 번
    [InlineData(@"D:\Games\Foo\Tools\Win64", @"D:\Games\Foo\Tools")]
    [InlineData(@"D:\bin\x64", "D:")]
    public void InstallFolderClimbsAtMostThreeBinaryFolders(string exeFolder, string expected)
    {
        Assert.Equal(expected, AutoRegistry.OriginRoot(exeFolder));
    }

    // ---- 안티치트 표시 ----

    [Fact]
    public void DiscoveryRecordsTheAntiCheatAndLogsItWithoutChangingBehavior()
    {
        World.Found.Add(Found("Mw4", @"D:\Games\Mw4", "mw4.exe") with { AntiCheat = "eac" });
        World.Found.Add(Found("Plain", @"D:\Games\Plain", "plain.exe"));

        Assert.Equal(2, _rig.Registry().DiscoverOnce());

        IReadOnlyList<GameSettings> games = _rig.Reload().Games;
        Assert.Equal("eac", games.Single(g => g.Name == "Mw4").AntiCheat);
        Assert.Null(games.Single(g => g.Name == "Plain").AntiCheat);
        Assert.Contains(_rig.Log.Lines, l => l == "Info: Mw4: anti-cheat detected (eac); AsterCache only reads files and never opens the game process beyond query access");
        Assert.DoesNotContain(_rig.Log.Lines, l => l.Contains("Plain: anti-cheat", StringComparison.Ordinal));
    }

    [Fact]
    public void HeuristicDetectsTheAntiCheatFromTheInstallFolder()
    {
        World.AntiCheats[@"D:\Games\Foo"] = "battleye";

        Run(_rig.Registry(), 10, @"D:\Games\Foo\Binaries\Win64\foo.exe", 300);
        Run(_rig.Registry(), 11, @"D:\Games\Bar\bar.exe", 300);

        IReadOnlyList<GameSettings> games = _rig.Reload().Games;
        Assert.Equal("battleye", games.Single(g => g.Name == "Foo").AntiCheat);
        Assert.Null(games.Single(g => g.Name == "Bar").AntiCheat);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("Foo: anti-cheat detected (battleye)", StringComparison.Ordinal));
    }

    // ---- 런처 트리거 ----

    [Fact]
    public void UbisoftGamesGetBothLauncherPrograms()
    {
        World.Found.Add(new DiscoveredGame("Fc", "ubisoft", @"D:\Ubi\Fc", @"D:\Ubi\Fc\fc.exe", true, "upc.exe"));

        _rig.Registry().DiscoverOnce();

        Assert.Equal(["upc.exe", "UbisoftConnect.exe"], Assert.Single(_rig.Reload().Games).Prewarm.TriggerExes);
    }

    [Theory]
    [InlineData(@"D:\SteamLibrary\steamapps\common\Foo\foo.exe", "steam.exe")]
    [InlineData(@"D:\Epic Games\Foo\foo.exe", "EpicGamesLauncher.exe")]
    [InlineData(@"D:\Games\Foo\foo.exe", null)]
    public void HeuristicGamesGetTheLauncherOfTheirLibraryOrNone(string image, string? trigger)
    {
        Run(_rig.Registry(), 10, image, 300);

        Assert.Equal(trigger is null ? Array.Empty<string>() : new[] { trigger }, Assert.Single(_rig.Reload().Games).Prewarm.TriggerExes);
    }

    [Fact]
    public void LauncherListHasTheEightProgramsAndDrivesThePinRelease()
    {
        Assert.Equal(["steam.exe", "EpicGamesLauncher.exe", "GalaxyClient.exe", "Battle.net.exe", "RiotClientServices.exe", "EADesktop.exe", "upc.exe", "UbisoftConnect.exe"], LauncherExes.All);
        Assert.True(LauncherExes.Is("STEAM.EXE"));
        Assert.False(LauncherExes.Is("game.exe"));
    }

    // ---- Riot · 제거 목록 · Xbox ----

    private static DiscoveredGame Lol(string dir = @"D:\Games\Riot Games\League of Legends") =>
        new("League_of_Legends", "riot", dir, dir + @"\LeagueClient.exe", true, "RiotClientServices.exe") { AntiCheat = "vanguard", ExtraExes = ["League of Legends.exe"] };

    [Fact]
    public void RiotGameIsRegisteredWithBothExesTheClientTriggerAndVanguard()
    {
        World.Found.Add(Lol());

        Assert.Equal(1, _rig.Registry().DiscoverOnce());

        GameSettings g = Assert.Single(_rig.Reload().Games);
        Assert.Equal(["LeagueClient.exe", "League of Legends.exe"], g.ExeNames);
        Assert.Equal(["RiotClientServices.exe"], g.Prewarm.TriggerExes);
        Assert.Equal("vanguard", g.AntiCheat);
        Assert.Equal("riot", _rig.Status.Get("League_of_Legends")!.Launcher);
    }

    [Fact]
    public void ExtraExesAlreadyRegisteredOrIgnoredAreLeftOut()
    {
        _rig.Add("other", "League of Legends.exe", @"D:\Games\Other");
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { IgnoreExes = ["ignored.exe"] } };
        World.Found.Add(Lol() with { ExtraExes = ["League of Legends.exe", "Extra.exe", "IGNORED.exe", "leagueclient.EXE", "extra.exe"] });

        Assert.Equal(1, _rig.Registry().DiscoverOnce());

        Assert.Equal(["LeagueClient.exe", "Extra.exe"], _rig.Reload().Games.Single(g => g.Name == "League_of_Legends").ExeNames);
    }

    [Fact]
    public void ManualGameInTheSameFolderIsLeftAloneAndLoggedOnce()
    {
        _rig.Add("LeagueOfLegens", "lol.exe", @"D:\Games\Riot Games\League of Legends", "manual");
        World.Found.Add(Lol());
        AutoRegistry reg = _rig.Registry();

        Assert.Equal(0, reg.DiscoverOnce());
        Assert.Equal(0, reg.DiscoverOnce());

        Assert.Equal(["LeagueOfLegens"], Registered());
        Assert.Single(_rig.Log.Lines, l => l.Contains("LeagueOfLegens", StringComparison.Ordinal) && l.Contains("same folder", StringComparison.Ordinal));
    }

    [Fact]
    public void GameAddedAfterTheServiceReadItsSettingsIsStillSeenJustBeforeWriting()
    {
        World.Found.Add(Lol());
        _rig.Editor.Add(_rig.Input("LeagueOfLegens", "lol.exe", @"D:\Games\Riot Games\League of Legends")); // 서비스가 들고 있는 설정은 아직 모름

        Assert.Equal(0, _rig.Registry().DiscoverOnce());

        Assert.Equal(["LeagueOfLegens"], Registered());
        Assert.Contains(_rig.Log.Lines, l => l.Contains("League_of_Legends", StringComparison.Ordinal) && l.Contains("same folder", StringComparison.Ordinal));
    }

    [Fact]
    public void AutoGameOfAnEarlierScanIsNotLoggedAsAnOverlap()
    {
        World.Found.Add(Lol());
        AutoRegistry reg = _rig.Registry();
        reg.DiscoverOnce();
        _rig.Reload();

        Assert.Equal(0, reg.DiscoverOnce());

        Assert.DoesNotContain(_rig.Log.Lines, l => l.Contains("same folder", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("riot")]
    [InlineData("xbox")]
    [InlineData("uninstall")]
    public void NewLaunchersFollowTheSameDriveRules(string launcher)
    {
        World.Found.Add(new DiscoveredGame("G", launcher, @"D:\Games\G", @"D:\Games\G\g.exe", true, null));
        World.Hdd["D:"] = false;
        Assert.Equal(0, _rig.Registry().DiscoverOnce());

        World.Hdd["D:"] = null;
        World.SystemDrive = "D:";
        Assert.Equal(0, _rig.Registry().DiscoverOnce());

        Assert.Empty(Registered());
    }

    [Theory]
    [InlineData("xbox")]
    [InlineData("uninstall")]
    public void LaunchersWithoutAProgramRegisterWithoutTriggers(string launcher)
    {
        World.Found.Add(new DiscoveredGame("G", launcher, @"D:\Games\G", @"D:\Games\G\g.exe", true, null));

        Assert.Equal(1, _rig.Registry().DiscoverOnce());

        Assert.Empty(Assert.Single(_rig.Reload().Games).Prewarm.TriggerExes);
        Assert.Equal(launcher, _rig.Status.Get("G")!.Launcher);
    }

    // ---- 읽기량을 못 여는 프로세스: 건너뜀 (ETW 세션을 걸지 않음) ----

    // 새 PID 하나를 띄우고 20 초가 지난 틱까지 (읽기량을 열 수 없는 PID 는 Reads 에 넣지 않음).
    private AutoRegistry DeniedProcess(int pid = 10)
    {
        AutoRegistry reg = _rig.Registry();
        reg.HeuristicTick();
        World.Pids = [pid];
        reg.HeuristicTick();
        World.Now += TimeSpan.FromSeconds(AutoSettings.HeuristicWindowSeconds);
        reg.HeuristicTick();
        return reg;
    }

    [Fact]
    public void DeniedProcessIsSkippedAndOpenedExactlyOnce()
    {
        World.Images[10] = @"D:\Games\Foooo.exe";
        AutoRegistry reg = DeniedProcess();

        World.Now += TimeSpan.FromSeconds(2);
        reg.HeuristicTick();
        reg.HeuristicTick();
        reg.HeuristicTick();

        Assert.Empty(Registered());
        Assert.Equal([10], World.ReadOpens); // 수명 동안 다시 열지 않음
    }

    [Fact]
    public void HeuristicRespectsTheDiscoverSwitch()
    {
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { Discover = false } };

        Run(_rig.Registry(), 10, @"D:\Games\Foo\foo.exe", 900);

        Assert.Empty(Registered());
    }
}
