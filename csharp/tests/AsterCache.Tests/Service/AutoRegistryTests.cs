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
            case "system": World.SystemDrive = "D:"; break;
            case "ssd": World.Hdd["D:"] = false; break;
            default: World.Hdd["D:"] = null; break;
        }

        World.Found.Add(Found("Mw4", @"D:\Games\Mw4", "mw4.exe"));
        AutoRegistry reg = _rig.Registry();

        Assert.Equal(0, reg.DiscoverOnce());
        Assert.Equal(0, reg.DiscoverOnce());

        Assert.Empty(Registered());
        Assert.Single(_rig.Log.Lines, l => l.Contains("skipped Mw4", StringComparison.Ordinal)); // 6 시간마다 다시 훑어도 한 번만
    }

    [Fact]
    public void HeuristicIgnoresProgramsOnTheSystemVolumeAndLogsOncePerDrive()
    {
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

    // ---- 읽기량을 못 여는 프로세스: ETW 대체 ----

    private FakeReadWatch UseWatch() => World.Watch = new FakeReadWatch();

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
    public void DeniedProcessSwitchesToTheEtwCountAndIsOpenedExactlyOnce()
    {
        FakeReadWatch watch = UseWatch();
        watch.Images[10] = @"D:\Games\Foo\foo.exe"; // 열기 실패라 이미지 경로도 세션이 본 것을 씀
        AutoRegistry reg = DeniedProcess();

        Assert.Equal(1, World.WatchStarts);
        Assert.Empty(Registered());

        watch.Reads[10] = 300L << 20;
        World.Now += TimeSpan.FromSeconds(2);
        reg.HeuristicTick();
        reg.HeuristicTick();
        reg.HeuristicTick();

        Assert.Equal(["Foo"], Registered());
        Assert.Equal([10], World.ReadOpens); // 수명 동안 다시 열지 않음
        Assert.True(watch.Disposed);
        Assert.Equal(1, World.WatchStarts);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("read-count session stopped (no candidates left)", StringComparison.Ordinal));
    }

    [Fact]
    public void EtwCountBelowTheThresholdNeverRegistersAndTheWaitEnds()
    {
        FakeReadWatch watch = UseWatch();
        watch.Images[10] = @"D:\Games\Foo\foo.exe";
        watch.Reads[10] = 255L << 20;
        AutoRegistry reg = DeniedProcess();

        World.Now += TimeSpan.FromSeconds(AutoSettings.HeuristicWindowSeconds);
        reg.HeuristicTick();
        watch.Reads[10] = 900L << 20; // 대기가 끝난 뒤의 읽기는 보지 않음
        reg.HeuristicTick();

        Assert.Empty(Registered());
        Assert.True(watch.Disposed);
        Assert.Equal([10], World.ReadOpens);
    }

    [Fact]
    public void SessionStartsOnlyWhenACandidateNeedsIt()
    {
        UseWatch();
        World.Reads[10] = 300L << 20; // 열 수 있는 프로세스
        World.Images[10] = @"D:\Games\Foo\foo.exe";
        AutoRegistry reg = _rig.Registry();
        reg.HeuristicTick();
        World.Pids = [10];
        reg.HeuristicTick();
        World.Now += TimeSpan.FromSeconds(AutoSettings.HeuristicWindowSeconds);
        reg.HeuristicTick();

        Assert.Equal(["Foo"], Registered());
        Assert.Equal(0, World.WatchStarts);
    }

    [Fact]
    public void SessionStopsWhenTheWaitingProcessExits()
    {
        FakeReadWatch watch = UseWatch();
        AutoRegistry reg = DeniedProcess();
        Assert.False(watch.Disposed);

        World.Pids = [];
        reg.HeuristicTick();

        Assert.True(watch.Disposed);
        Assert.Empty(Registered());
    }

    [Fact]
    public void ShutdownStopsARunningSession()
    {
        FakeReadWatch watch = UseWatch();
        AutoRegistry reg = DeniedProcess();

        reg.Shutdown();

        Assert.True(watch.Disposed);
    }

    [Fact]
    public void FailureToStartTheSessionIsLoggedAndNotRetriedForTheSameProcess()
    {
        AutoRegistry reg = DeniedProcess(); // Watch 가 없어 StartReadWatch 가 던짐

        reg.HeuristicTick();
        reg.HeuristicTick();

        Assert.Equal(1, World.WatchStarts);
        Assert.Equal([10], World.ReadOpens);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("read-count session could not start", StringComparison.Ordinal));
    }

    [Fact]
    public void HeuristicRespectsTheDiscoverSwitch()
    {
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { Discover = false } };

        Run(_rig.Registry(), 10, @"D:\Games\Foo\foo.exe", 900);

        Assert.Empty(Registered());
    }
}
