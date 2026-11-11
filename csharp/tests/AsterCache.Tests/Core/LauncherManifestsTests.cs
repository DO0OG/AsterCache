using AsterCache.Core.Discovery;
using Xunit;

namespace AsterCache.Tests.Core;

public sealed class LauncherManifestsTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "launchers", name));

    [Fact]
    public void LibraryFoldersListsBothLibrariesWithEscapedPaths()
    {
        IReadOnlyList<string> libs = LauncherManifests.ParseLibraryFolders(Fixture("libraryfolders.vdf"));

        Assert.Equal([@"C:\Program Files (x86)\Steam", "D:\\Steam \"Games\""], libs);
    }

    [Fact]
    public void OldStyleLibraryFoldersIsRead()
    {
        const string old = "\"LibraryFolders\"\n{\n\t\"TimeNextStatsReport\"\t\"1\"\n\t\"1\"\t\"E:\\\\Games\"\n}\n";

        Assert.Equal([@"E:\Games"], LauncherManifests.ParseLibraryFolders(old));
    }

    [Fact]
    public void AppManifestGivesIdNameAndFolder()
    {
        SteamApp? app = LauncherManifests.ParseAppManifest(Fixture("appmanifest_480.acf"));

        Assert.Equal(new SteamApp("480", "Space Wars: Ultimate Edition!", "Space Wars"), app);
        Assert.Equal("붕괴", LauncherManifests.ParseAppManifest(Fixture("appmanifest_1234.acf"))!.Name);
    }

    [Fact]
    public void AppManifestGivesInstalledDepotsWithManifestAndLastPlayed()
    {
        const string acf = "\"AppState\" { \"appid\" \"730\" \"name\" \"G\" \"installdir\" \"G\" \"LastPlayed\" \"1700000000\" "
            + "\"InstalledDepots\" { \"731\" { \"manifest\" \"111\" \"size\" \"5\" } \"732\" { \"size\" \"1\" } \"733\" { \"manifest\" \"333\" } } }";

        SteamApp app = LauncherManifests.ParseAppManifest(acf)!;

        Assert.Equal(new Dictionary<string, string> { ["731"] = "111", ["733"] = "333" }, app.InstalledDepots); // manifest 가 없는 depot 은 뺌
        Assert.Equal(1700000000L, app.LastPlayed);
    }

    [Theory]
    [InlineData("\"LastPlayed\" \"0\"")]
    [InlineData("\"LastPlayed\" \"soon\"")]
    [InlineData("")]
    public void AppManifestWithoutUsableLastPlayedOrDepotsKeepsTheOldShape(string extra)
    {
        SteamApp app = LauncherManifests.ParseAppManifest("\"AppState\" { \"appid\" \"1\" \"name\" \"n\" \"installdir\" \"d\" " + extra + " }")!;

        Assert.Equal(new SteamApp("1", "n", "d"), app);
        Assert.Empty(app.InstalledDepots);
        Assert.Null(app.LastPlayed);
    }

    [Fact]
    public void EpicItemGivesAppVersionString()
    {
        EpicApp app = LauncherManifests.ParseEpicItem("{\"InstallLocation\":\"D:\\\\x\",\"AppName\":\"abc\",\"AppVersionString\":\"1.2.3-4\"}")!;

        Assert.Equal("1.2.3-4", app.AppVersionString);
        Assert.Equal(string.Empty, LauncherManifests.ParseEpicItem(Fixture("epic_game.item"))!.AppVersionString);
    }

    [Theory]
    [InlineData("\"SizeOnDisk\" \"1073741823\"", false)]
    [InlineData("\"SizeOnDisk\" \"1073741824\"", true)]
    [InlineData("", true)]
    public void SteamAppUnderOneGiBOnDiskIsNotAGame(string extra, bool registered)
    {
        SteamApp? app = LauncherManifests.ParseAppManifest("\"AppState\" { \"appid\" \"1\" \"name\" \"n\" \"installdir\" \"d\" " + extra + " }");

        Assert.Equal(registered, app is not null);
    }

    [Fact]
    public void SmallFixtureAppIsNotAGameAndTheBigOneKeepsItsSize()
    {
        Assert.Null(LauncherManifests.ParseAppManifest(Fixture("appmanifest_5678.acf")));
        Assert.NotNull(LauncherManifests.ParseAppManifest(Fixture("appmanifest_480.acf")));
    }

    [Fact]
    public void SteamworksRedistributableIsNotAGame() => Assert.Null(LauncherManifests.ParseAppManifest(Fixture("appmanifest_228980.acf")));

    [Theory]
    [InlineData("431960", false)] // Wallpaper Engine
    [InlineData("1283970", false)] // YoloMouse
    [InlineData("1", true)]
    [InlineData("431961", true)]
    public void ResidentUtilityAppIsNotAGameEvenWhenItIsBig(string appId, bool registered)
    {
        string acf = $"\"AppState\" {{ \"appid\" \"{appId}\" \"name\" \"n\" \"installdir\" \"d\" \"SizeOnDisk\" \"5368709120\" }}";

        Assert.Equal(registered, LauncherManifests.ParseAppManifest(acf) is not null);
    }

    [Fact]
    public void ManifestWithoutInstallDirIsNull() => Assert.Null(LauncherManifests.ParseAppManifest("\"AppState\" { \"appid\" \"1\" }"));

    [Fact]
    public void EpicItemGivesFields()
    {
        EpicApp? app = LauncherManifests.ParseEpicItem(Fixture("epic_game.item"));

        Assert.Equal(new EpicApp("abc123", "Epic Test Game", @"D:\Epic\TestGame", @"Game\Binaries\Win64\Game.exe"), app);
    }

    [Fact]
    public void EpicSoftwareAndDlcAreSkipped()
    {
        Assert.Null(LauncherManifests.ParseEpicItem("{\"InstallLocation\":\"D:\\\\x\",\"AppCategories\":[\"public\",\"software\"]}"));
        Assert.Null(LauncherManifests.ParseEpicItem("{\"InstallLocation\":\"D:\\\\x\",\"AppName\":\"dlc\",\"MainGameAppName\":\"main\"}"));
        Assert.Null(LauncherManifests.ParseEpicItem("{\"DisplayName\":\"x\"}"));
    }

    [Fact]
    public void KeyValuesHandlesCommentsBareWordsAndNesting()
    {
        var kv = LauncherManifests.ParseKeyValues("// c\n\"a\" { b 1 \"c\" { \"d\" \"x\\ty\" } } \"E\" \"}\"");

        var a = Assert.IsType<Dictionary<string, object>>(kv["A"]); // 키는 대소문자 무시
        Assert.Equal("1", a["b"]);
        Assert.Equal("x\ty", ((Dictionary<string, object>)a["c"])["d"]);
        Assert.Equal("}", kv["e"]); // 따옴표 안의 } 는 값
    }

    [Theory]
    [InlineData("\"a\" {")]
    [InlineData("\"a\" }")]
    [InlineData("\"a\"")]
    [InlineData("\"a\" \"unterminated")]
    [InlineData("{ \"a\" \"b\" }")]
    public void BrokenKeyValuesThrow(string text) => Assert.Throws<FormatException>(() => LauncherManifests.ParseKeyValues(text));

    [Fact]
    public void DeepNestingIsRejected() =>
        Assert.Throws<FormatException>(() => LauncherManifests.ParseKeyValues(string.Concat(Enumerable.Repeat("\"a\" {", 100))));

    [Theory]
    [InlineData("Space Wars: Ultimate Edition!", "x", "y", "Space_Wars_Ultimate_Edition")]
    [InlineData("붕괴", "Honkai Star", "y", "Honkai_Star")] // 표시 이름이 전부 비면 다음 후보
    [InlineData("", "한글", "game-start", "game-start")]
    [InlineData("한글", "또한글", "더", "game")]
    [InlineData("  a   b  ", "", "", "a_b")]
    [InlineData("..name..", "", "", "name")]
    [InlineData("Half-Life 2: Ep.1", "", "", "Half-Life_2_Ep.1")]
    public void SafeNameFollowsConfigRules(string display, string folder, string stem, string expected)
    {
        string name = LauncherManifests.SafeName(display, folder, stem);

        Assert.Equal(expected, name);
        Assert.Matches("^[A-Za-z0-9._-]{1,64}$", name);
    }

    [Fact]
    public void SafeNameIsCappedAt64()
    {
        string name = LauncherManifests.SafeName(new string('a', 100), "", "");

        Assert.Equal(64, name.Length);
    }

    [Fact]
    public void DuplicatesGetNumberedSuffixesWithinTheLimit()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal("Game", LauncherManifests.MakeUnique("Game", taken));
        Assert.Equal("game-2", LauncherManifests.MakeUnique("game", taken)); // 대소문자 무시
        Assert.Equal("Game-3", LauncherManifests.MakeUnique("Game", taken));
        string longName = new('a', 64);
        LauncherManifests.MakeUnique(longName, taken);
        string second = LauncherManifests.MakeUnique(longName, taken);
        Assert.Equal(64, second.Length);
        Assert.EndsWith("-2", second);
    }

    [Theory]
    [InlineData("product_install_full_path: \"D:/Riot Games/League of Legends\"\n", @"D:\Riot Games\League of Legends")]
    [InlineData("a: 1\r\nproduct_install_full_path: 'D:/It''s/Games'\r\nb: 2\r\n", @"D:\It's\Games")]
    [InlineData("product_install_full_path: \"D:\\\\Games\\uAC8C\\uC784\"\n", "D:\\Games\uAC8C\uC784")]
    [InlineData("product_install_full_path: E:/Plain Games\n", @"E:\Plain Games")]
    [InlineData("settings:\n    product_install_full_path: \"D:/Nested\"\n", null)]
    [InlineData("product_install_full_path: \"\"\n", null)]
    [InlineData("", null)]
    public void RiotInstallPathIsTheTopLevelLineWithQuotesAndEscapesResolved(string yaml, string? expected) =>
        Assert.Equal(expected, LauncherManifests.ParseRiotInstallPath(yaml));

    [Fact]
    public void RiotFixtureGivesTheLiveAndPbeFolders()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "fixtures", "launchers", "riot", "Metadata");

        string Path_(string product) => LauncherManifests.ParseRiotInstallPath(File.ReadAllText(Path.Combine(root, product, product + ".product_settings.yaml")))!;

        Assert.Equal(@"D:\Games\Riot Games\League of Legends", Path_("league_of_legends.live"));
        Assert.Equal(@"C:\Riot Games\League of Legends (PBE)", Path_("league_of_legends.pbe"));
    }
}
