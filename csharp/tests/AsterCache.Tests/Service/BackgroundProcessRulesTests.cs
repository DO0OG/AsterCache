using AsterCache.Core.Discovery;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 배경 프로세스 제외 규칙의 이름·경로 판정.
public sealed class BackgroundProcessRulesTests
{
    private static readonly GameSettings Game = Games.Make("cod", "cod.exe") with { Prewarm = PrewarmSettings.Default with { TriggerExes = ["codlauncher.exe"] } };
    private static readonly HashSet<string> Exes = BackgroundProcessRules.GameExes([Game]);

    [Theory]
    [InlineData("cod.exe", true)]
    [InlineData("COD.EXE", true)]
    [InlineData("codlauncher.exe", true)]
    [InlineData("steam.exe", true)]
    [InlineData("steamwebhelper.exe", true)]
    [InlineData("EpicGamesLauncher.exe", true)]
    [InlineData("vgtray.exe", true)]
    [InlineData("vgc.exe", true)]
    [InlineData("EasyAntiCheat.exe", true)]
    [InlineData("EasyAntiCheat_EOS.exe", true)]
    [InlineData("BEService.exe", true)]
    [InlineData("RiotClientServices.exe", true)]
    [InlineData("LeagueClientUx.exe", true)]
    [InlineData("AsterCache.Service.exe", true)]
    [InlineData("aster.exe", true)]
    [InlineData("chrome.exe", false)]
    [InlineData("discord.exe", false)]
    [InlineData("python.exe", false)]
    public void NameRuleExcludesGamesLaunchersAntiCheatAndOurOwnProcesses(string exe, bool excluded) =>
        Assert.Equal(excluded, BackgroundProcessRules.ExcludedByName(exe, 100, 200, Exes));

    [Theory]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(200)]
    public void SystemPidsAndTheServiceItselfAreExcludedWhateverTheirName(int pid) =>
        Assert.True(BackgroundProcessRules.ExcludedByName("chrome.exe", pid, 200, Exes));

    [Theory]
    [InlineData(@"C:\Windows\explorer.exe", true)]
    [InlineData(@"C:\Windows\System32\svchost.exe", true)]
    [InlineData(@"D:\Games\cod\bin\helper.exe", true)] // 등록 게임의 설치 폴더
    [InlineData(@"D:\Games\other\x.exe", true)] // 허용 루트
    [InlineData(@"C:\Program Files (x86)\Steam\steamservice.exe", true)]
    [InlineData(@"F:\SteamLibrary\steamapps\common\Game\g.exe", true)]
    [InlineData(@"C:\Program Files\Epic Games\Launcher\Portal\x.exe", true)]
    [InlineData(@"C:\Riot Games\League of Legends\x.exe", true)]
    [InlineData(@"C:\Program Files\Riot Vanguard\vgc.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Battle.net\Battle.net.exe", true)]
    [InlineData(@"C:\XboxGames\Game\x.exe", true)]
    [InlineData(@"C:\Program Files (x86)\GOG Galaxy\GalaxyClient.exe", true)]
    [InlineData(@"C:\Program Files\EA Games\x.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\upc.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Common Files\EasyAntiCheat_EOS\x.exe", true)]
    [InlineData(@"C:\Program Files (x86)\Steam", true)] // 폴더 이름 끝에서도 일치
    [InlineData(@"C:\Users\u\AppData\Local\Discord\app\discord.exe", false)]
    [InlineData(@"C:\Program Files\Google\Chrome\chrome.exe", false)]
    [InlineData(@"D:\Tools\python.exe", false)] // 허용 루트 D:\Games 밖
    [InlineData(@"C:\WindowsApps\x.exe", false)] // Windows 폴더와 이름이 비슷할 뿐
    public void PathRuleExcludesWindowsGameFoldersAndKnownLauncherLibraries(string path, bool excluded) =>
        Assert.Equal(excluded, BackgroundProcessRules.ExcludedByPath(path, @"C:\Windows", [Game]));

    [Fact]
    public void GameInstallFoldersKnownToTheLauncherScanAreExcludedByPath()
    {
        string[] known = [@"C:\Program Files (x86)\Overwatch"];
        Assert.True(BackgroundProcessRules.ExcludedByPath(@"C:\Program Files (x86)\Overwatch\Overwatch.exe", @"C:\Windows", [Game], known));
        Assert.False(BackgroundProcessRules.ExcludedByPath(@"C:\Program Files (x86)\Overwatch2\x.exe", @"C:\Windows", [Game], known)); // 접두만 같은 다른 폴더
        Assert.False(BackgroundProcessRules.ExcludedByPath(@"C:\Program Files (x86)\Overwatch\Overwatch.exe", @"C:\Windows", [Game]));
    }

    [Fact]
    public void AllowedRootWithATrailingBackslashExcludesTheWholeDrive() =>
        Assert.True(BackgroundProcessRules.ExcludedByPath(@"D:\Tools\x.exe", @"C:\Windows", [Game with { AllowedRoots = [@"D:\"] }]));

    [Theory]
    [InlineData("vgtray.exe", true)]
    [InlineData("vgk.sys", true)]
    [InlineData("EasyAntiCheat_EOS.exe", true)]
    [InlineData("start_protected_game.exe", true)]
    [InlineData("BEService_x64.exe", true)]
    [InlineData("BlackCipher", true)]
    [InlineData("GameGuard", true)]
    [InlineData("BlackCipher64.aes", true)]
    [InlineData("GameMon64.des", true)]
    [InlineData("Overwatch_BE.exe", true)]
    [InlineData("SGuard64.exe", true)]
    [InlineData("SGuardSvc64.exe", true)]
    [InlineData("PnkBstrA.exe", true)]
    [InlineData("PnkBstrB.exe", true)]
    [InlineData("chrome.exe", false)]
    public void AntiCheatProcessNameRuleCoversTrayHelpersAndFileNameRules(string name, bool expected) => Assert.Equal(expected, AntiCheat.IsProcessName(name));

    [Theory]
    [InlineData("RiotClientServices.exe", true)]
    [InlineData("RiotClientUxRender.exe", true)]
    [InlineData("LeagueClient.exe", true)]
    [InlineData("VALORANT-Win64-Shipping.exe", true)]
    [InlineData("chrome.exe", false)]
    public void RiotComponentNamesIncludeDerivedClientProcesses(string name, bool expected) => Assert.Equal(expected, AntiCheat.IsRiotExe(name));
}
