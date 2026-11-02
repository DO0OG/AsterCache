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
    public void SteamworksRedistributableIsNotAGame() => Assert.Null(LauncherManifests.ParseAppManifest(Fixture("appmanifest_228980.acf")));

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
}
