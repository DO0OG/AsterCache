using System.Text.Json.Nodes;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 설정 검증 (신뢰 경계): 형식, 경로, 허용 루트 밖 거부, 누락 필드.
public sealed class SettingsTests
{
    private const string Plans = @"C:\ProgramData\AsterCache\plans";

    private static JsonObject Game() => new()
    {
        ["name"] = "mw4",
        ["exe_names"] = new JsonArray("game.exe", "Launcher.exe"),
        ["trace"] = @"C:\ProgramData\AsterCache\plans\mw4.prefetch.json",
        ["plan"] = @"C:\ProgramData\AsterCache\plans\mw4.plan.json",
        ["origin_root"] = @"D:\Games\MW4",
        ["allowed_roots"] = new JsonArray(@"D:\Games"),
        ["volume_label"] = "GAMES",
    };

    private static string Cfg(JsonObject game, Action<JsonObject>? top = null)
    {
        var root = new JsonObject { ["games"] = new JsonArray(game) };
        top?.Invoke(root);
        return root.ToJsonString();
    }

    private static AsterSettings Parse(JsonObject game) => SettingsStore.Parse(Cfg(game), Plans);

    [Fact]
    public void ValidConfigGetsDefaultsAndNormalizedPaths()
    {
        JsonObject g = Game();
        g["origin_root"] = "D:/Games/MW4/";
        AsterSettings s = Parse(g);

        GameSettings game = Assert.Single(s.Games);
        Assert.Equal((250, 1440), (s.PollMs, game.ReapplyMinutes));
        Assert.Equal(@"D:\Games\MW4", game.OriginRoot);
        Assert.Equal(["game.exe", "Launcher.exe"], game.ExeNames);
        Assert.Equal([@"D:\Games"], game.AllowedRoots);
    }

    [Fact]
    public void ExplicitPollAndIntervalAreKept()
    {
        JsonObject g = Game();
        g["reapply_interval_minutes"] = 30;
        AsterSettings s = SettingsStore.Parse(Cfg(g, t => t["poll_ms"] = 100), Plans);

        Assert.Equal((100, 30), (s.PollMs, s.Games[0].ReapplyMinutes));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("exe_names")]
    [InlineData("trace")]
    [InlineData("plan")]
    [InlineData("origin_root")]
    [InlineData("allowed_roots")]
    [InlineData("volume_label")]
    public void MissingRequiredFieldIsRejected(string field)
    {
        JsonObject g = Game();
        g.Remove(field);

        SettingsException e = Assert.Throws<SettingsException>(() => Parse(g));
        Assert.Contains(field, e.Message);
    }

    public static TheoryData<string, string> BadValues => new()
    {
        { "trace", @"D:\evil\x.json" },
        { "trace", @"C:\ProgramData\AsterCache\plans\..\config.json" },
        { "trace", @"C:\ProgramData\AsterCache\plansX\a.json" },
        { "trace", @"C:\ProgramData\AsterCache\plans" },
        { "plan", "plan.json" },
        { "plan", @"\\host\share\p.json" },
        { "plan", @"\\?\C:\ProgramData\AsterCache\plans\p.json" },
        { "plan", @"C:\ProgramData\AsterCache\plans\p.json:stream" },
        { "plan", @"C:\ProgramData\AsterCache\plans\p.txt" },
        { "plan", @"C:\ProgramData\AsterCache\plans\p*.json" },
        { "origin_root", @"E:\Other" },
        { "origin_root", @"D:\GamesX\MW4" },
        { "origin_root", @"D:\Games\..\Windows" },
        { "origin_root", "relative\\path" },
        { "origin_root", "" },
        { "name", "a b" },
        { "name", "" },
        { "volume_label", "" },
        { "volume_label", "0123456789012345678901234567890123" },
        { "reapply_interval_minutes", "1" },
        { "reapply_interval_minutes", "\"60\"" },
        { "reapply_interval_minutes", "true" },
        { "reapply_interval_minutes", "60.5" },
        { "extra", "1" },
    };

    [Theory]
    [MemberData(nameof(BadValues))]
    public void BadValueIsRejectedWithoutLeakingIt(string field, string value)
    {
        JsonObject g = Game();
        g[field] = value is "1" or "true" or "60.5" || value.StartsWith('"') ? JsonNode.Parse(value) : value;

        SettingsException e = Assert.Throws<SettingsException>(() => Parse(g));
        Assert.DoesNotContain("evil", e.Message);
    }

    [Theory]
    [InlineData(@"D:\")]
    [InlineData(@"E:\Games")]
    [InlineData(@"D:\Games\..\..")]
    public void BadAllowedRootIsRejected(string root)
    {
        JsonObject g = Game();
        g["allowed_roots"] = new JsonArray(root);

        Assert.Throws<SettingsException>(() => Parse(g));
    }

    [Theory]
    [InlineData("game.txt")]
    [InlineData(@"C:\x\game.exe")]
    [InlineData("sub/game.exe")]
    [InlineData(".exe")]
    [InlineData("")]
    public void BadExeNameIsRejected(string exe)
    {
        JsonObject g = Game();
        g["exe_names"] = new JsonArray(exe);

        Assert.Throws<SettingsException>(() => Parse(g));
    }

    [Fact]
    public void EmptyOrWrongTypedListsAreRejected()
    {
        JsonObject a = Game();
        a["exe_names"] = new JsonArray();
        JsonObject b = Game();
        b["allowed_roots"] = @"D:\Games";

        Assert.Throws<SettingsException>(() => Parse(a));
        Assert.Throws<SettingsException>(() => Parse(b));
    }

    [Fact]
    public void DuplicateGameNameOrSharedExeIsRejected()
    {
        JsonObject other = Game();
        JsonObject sharedExe = Game();
        sharedExe["name"] = "other";
        sharedExe["exe_names"] = new JsonArray("GAME.EXE");
        string dupName = new JsonObject { ["games"] = new JsonArray(Game(), other) }.ToJsonString();
        string dupExe = new JsonObject { ["games"] = new JsonArray(Game(), sharedExe) }.ToJsonString();

        Assert.Throws<SettingsException>(() => SettingsStore.Parse(dupName, Plans));
        Assert.Throws<SettingsException>(() => SettingsStore.Parse(dupExe, Plans));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"games\":{}}")]
    [InlineData("{\"games\":[1]}")]
    [InlineData("not json")]
    public void WrongShapeOrInvalidJsonIsRejected(string json)
    {
        Assert.Throws<SettingsException>(() => SettingsStore.Parse(json, Plans));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(6000)]
    public void PollOutOfRangeIsRejected(int poll)
    {
        Assert.Throws<SettingsException>(() => SettingsStore.Parse(Cfg(Game(), t => t["poll_ms"] = poll), Plans));
    }

    [Fact]
    public void EmptyGameListIsValid()
    {
        AsterSettings s = SettingsStore.Parse("{\"games\":[]}", Plans);

        Assert.Empty(s.Games);
        Assert.Equal(SettingsStore.DefaultPollMs, s.PollMs);
        Assert.Throws<SettingsException>(() => SettingsStore.Parse("{}", Plans)); // games 키 자체는 여전히 필요
    }

    [Fact]
    public void LoadReadsFileAndRefusesOversizedOne()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Combine("config.json"), Cfg(Game()));
        File.WriteAllText(tmp.Combine("big.json"), new string(' ', (int)SettingsStore.MaxFileBytes + 1));

        Assert.Equal("mw4", SettingsStore.Load(tmp.Combine("config.json"), Plans).Games[0].Name);
        Assert.Throws<SettingsException>(() => SettingsStore.Load(tmp.Combine("big.json"), Plans));
    }
}

public sealed class FileLogTests
{
    [Fact]
    public void RollsBySizeAndKeepsLimitedFiles()
    {
        using var tmp = new TempDir();
        var log = new FileLog(tmp.Combine("logs\\service.log"), maxBytes: 200, keep: 2);

        for (int i = 0; i < 20; i++)
        {
            log.Info("line " + i + " ".PadRight(60, 'x'));
        }

        string[] files = Directory.GetFiles(tmp.Combine("logs")).Select(Path.GetFileName).Order().ToArray()!;
        Assert.Equal(["service.log", "service.log.1", "service.log.2"], files);
        Assert.Contains("line 19", File.ReadAllText(tmp.Combine("logs\\service.log")));
    }

    [Fact]
    public void ProfilePathIsMaskedInMessageAndException()
    {
        using var tmp = new TempDir();
        var log = new FileLog(tmp.Combine("service.log"));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        log.Error($"cannot read {home}\\x.json", new IOException($"denied {home.ToUpperInvariant()}\\y"));

        string text = File.ReadAllText(tmp.Combine("service.log"));
        Assert.DoesNotContain(home, text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("%USERPROFILE%\\x.json", text);
        Assert.Contains("[ERROR]", text);
        Assert.Contains("IOException", text);
    }
}
