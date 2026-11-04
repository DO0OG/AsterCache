using AsterCache.Core.Discovery;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tests.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Core;

/// 안티치트 흔적 판정: 가짜 설치 폴더 (tests/fixtures/anticheat, 빈 파일) 의 실제 열거 결과와 이름 목록 직접 판정.
public sealed class AntiCheatTests
{
    private static string? Detect(string fixture) =>
        AntiCheat.Detect(LauncherScan.ListNames(Path.Combine(AppContext.BaseDirectory, "fixtures", "anticheat", fixture), 2));

    [Theory]
    [InlineData("eac", "eac")]
    [InlineData("eac_start", "eac")]
    [InlineData("battleye", "battleye")]
    [InlineData("vanguard", "vanguard")]
    [InlineData("ricochet", "ricochet")]
    [InlineData("unknown", "unknown")]
    [InlineData("none", null)]
    public void FixtureFoldersAreClassified(string fixture, string? expected) => Assert.Equal(expected, Detect(fixture));

    [Fact]
    public void FirstRuleInOrderWinsWhenSeveralArePresent() => Assert.Equal("eac", Detect("order"));

    [Fact]
    public void OrderIsEacBattleyeVanguardRicochetUnknown()
    {
        Assert.Equal("battleye", AntiCheat.Detect(["BEService.exe", "vgk.sys", "ricochet.dll", "xigncode"]));
        Assert.Equal("vanguard", AntiCheat.Detect(["vgk.sys", "ricochet.dll", "xigncode"]));
        Assert.Equal("ricochet", AntiCheat.Detect(["RICOCHET.dll", "xigncode"]));
        Assert.Equal("unknown", AntiCheat.Detect(["nProtect GameGuard", "Game.exe"]));
    }

    [Theory]
    [InlineData("EasyAntiCheat_EOS", "eac")]
    [InlineData("easyanticheat_launcher.exe", "eac")]
    [InlineData("start_protected_game.exe", "eac")]
    [InlineData("battleye", "battleye")]
    [InlineData("BEClient_x64.dll", "battleye")]
    [InlineData("VGC.EXE", "vanguard")]
    [InlineData("Riot Vanguard", "vanguard")]
    [InlineData("randgrid.sys", "ricochet")]
    [InlineData("Anti-Cheat", "unknown")]
    [InlineData(@"Binaries\Win64\EasyAntiCheat", "eac")]
    public void SingleNamesAndPathsAreMatchedByLeafIgnoringCase(string name, string expected) => Assert.Equal(expected, AntiCheat.Detect([name]));

    [Theory]
    [InlineData("Game.exe")]
    [InlineData("mavgcfoo.exe")]
    [InlineData("vgcore.dll")]
    [InlineData("BEClient.exe")]
    public void OrdinaryNamesGiveNull(string name) => Assert.Null(AntiCheat.Detect([name]));

    // 접근 거부가 난 열거를 흉내 냄: 이름 목록과 완전성 표시만 돌려줌.
    private static Func<string, int, (List<string>, bool)> Lister(bool complete, params string[] names) => (_, _) => ([.. names], complete);

    [Fact]
    public void IncompleteListingWithNothingFoundIsUnknown() => Assert.Equal(LauncherScan.AntiCheatUnknown, LauncherScan.DetectAntiCheat("X", Lister(false, "Game.exe")));

    [Fact]
    public void IncompleteListingThatFoundSomethingKeepsThatValue() => Assert.Equal("eac", LauncherScan.DetectAntiCheat("X", Lister(false, "Game.exe", "EasyAntiCheat")));

    [Fact]
    public void IncompleteListingWithOnlyRicochetIsUnknown() => Assert.Equal(LauncherScan.AntiCheatUnknown, LauncherScan.DetectAntiCheat("X", Lister(false, "Game.exe", "ricochet.dll")));

    [Fact]
    public void CompleteListingWithRicochetKeepsRicochet() => Assert.Equal("ricochet", LauncherScan.DetectAntiCheat("X", Lister(true, "Game.exe", "ricochet.dll")));

    [Fact]
    public void MarkerInsideAHiddenFolderIsDetected()
    {
        string root = Path.Combine(Path.GetTempPath(), "aster-ac-" + Guid.NewGuid().ToString("N"));
        string hidden = Path.Combine(root, "hid");
        Directory.CreateDirectory(Path.Combine(hidden, "EasyAntiCheat"));
        File.SetAttributes(hidden, FileAttributes.Hidden);
        try
        {
            Assert.Equal("eac", LauncherScan.DetectAntiCheat(root));
        }
        finally
        {
            File.SetAttributes(hidden, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void CompleteListingWithNothingFoundIsNull() => Assert.Null(LauncherScan.DetectAntiCheat("X", Lister(true, "Game.exe")));

    [Fact]
    public void ServiceDetectionLogsOnceWhenTheListingIsIncomplete()
    {
        var log = new ListLog();

        Assert.Equal(AutoEnv.AntiCheatUnknown, AutoEnv.DetectAntiCheat("X", Lister(false, "Game.exe"), log));
        Assert.Equal("eac", AutoEnv.DetectAntiCheat("X", Lister(false, "EasyAntiCheat"), log));
        Assert.Null(AutoEnv.DetectAntiCheat("X", Lister(true, "Game.exe"), log));

        Assert.Single(log.Lines);
        Assert.Contains("anti-cheat check incomplete", log.Lines.Single());
    }

    [Fact]
    public void MissingFolderListingIsIncompleteSoDetectionIsUnknown() =>
        Assert.Equal(LauncherScan.AntiCheatUnknown, LauncherScan.DetectAntiCheat(Path.Combine(AppContext.BaseDirectory, "fixtures", "anticheat", "no-such-folder")));

    [Fact]
    public void EmptyListGivesNull() => Assert.Null(AntiCheat.Detect([]));

    [Theory]
    [InlineData(@"D:\Riot Games\League of Legends", "LeagueClient.exe", true, "vanguard")]
    [InlineData(@"D:\Riot Games\League of Legends", "LeagueClient.exe", false, null)]
    [InlineData(@"E:\Anything", "RiotClientServices.exe", true, "vanguard")]
    [InlineData(@"E:\Valorant", "valorant-win64-shipping.exe", true, "vanguard")]
    [InlineData(@"D:\Riot Games\Other", "game.exe", true, "vanguard")]
    [InlineData(@"D:\Riot Games Fan", "game.exe", true, null)]
    [InlineData(@"D:\Call of Duty", "cod.exe", true, null)]
    public void RiotGamesAreVanguardOnlyWhenVanguardIsInstalled(string root, string exe, bool installed, string? expected) =>
        Assert.Equal(expected, AntiCheat.RiotVanguard(root, [exe], installed));

    [Fact]
    public void ManualRiotEntriesWithoutAnAntiCheatGetVanguardWhenReadAndOthersStayAsTheyAre()
    {
        GameSettings Make(string name, string root, string exe, string? cheat) =>
            new(name, [exe], "t", "p", root, [root], "GAMES", 1440) { AntiCheat = cheat };
        var s = new AsterSettings(50,
        [
            Make("lol", @"D:\Riot Games\League of Legends", "LeagueClient.exe", null),
            Make("cod", @"D:\Call of Duty", "cod.exe", null),
            Make("ric", @"D:\Riot Games\Other", "game.exe", "ricochet"),
        ]);
        int checks = 0;

        AsterSettings read = AntiCheat.WithRiotRule(s, () =>
        {
            checks++;
            return true;
        });

        Assert.Equal(["vanguard", null, "ricochet"], read.Games.Select(g => g.AntiCheat));
        Assert.Equal(1, checks);
        Assert.Equal([null, null, "ricochet"], AntiCheat.WithRiotRule(s, () => false).Games.Select(g => g.AntiCheat));
    }

    [Theory]
    [InlineData("eac", true)]
    [InlineData("battleye", true)]
    [InlineData("vanguard", true)]
    [InlineData("unknown", true)]
    [InlineData("ricochet", false)]
    [InlineData(null, false)]
    public void OnlyKernelAntiCheatsAndFailedDetectionForbidATrace(string? kind, bool expected) => Assert.Equal(expected, AntiCheat.ForbidsTrace(kind));

    [Theory]
    [InlineData("eac", true)]
    [InlineData("battleye", true)]
    [InlineData("vanguard", true)]
    [InlineData("unknown", true)]
    [InlineData("ricochet", true)]
    [InlineData(null, false)]
    public void RicochetForbidsPinningButNotATrace(string? kind, bool expected) => Assert.Equal(expected, AntiCheat.ForbidsPin(kind));
}
