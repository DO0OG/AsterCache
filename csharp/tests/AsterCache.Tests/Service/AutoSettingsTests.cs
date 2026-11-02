using System.Text.Json.Nodes;
using AsterCache.Core.Settings;
using Xunit;

namespace AsterCache.Tests.Service;

/// 자동 모드 설정 (auto, max_zone_mib, 게임의 source): 기본값, 부분 지정, 범위·형 거부.
public sealed class AutoSettingsTests
{
    private const string Plans = @"C:\ProgramData\AsterCache\plans";

    private static JsonObject Game() => new()
    {
        ["name"] = "mw4",
        ["exe_names"] = new JsonArray("game.exe"),
        ["trace"] = @"C:\ProgramData\AsterCache\plans\mw4.prefetch.json",
        ["plan"] = @"C:\ProgramData\AsterCache\plans\mw4.plan.json",
        ["origin_root"] = @"D:\Games\MW4",
        ["allowed_roots"] = new JsonArray(@"D:\Games"),
        ["volume_label"] = "GAMES",
    };

    private static AsterSettings ParseTop(Action<JsonObject> top)
    {
        var root = new JsonObject { ["games"] = new JsonArray() };
        top(root);
        return SettingsStore.Parse(root.ToJsonString(), Plans);
    }

    private static AsterSettings ParseAuto(JsonObject auto) => ParseTop(t => t["auto"] = auto);

    [Fact]
    public void MissingKeysGetDefaults()
    {
        AsterSettings s = ParseTop(_ => { });

        Assert.Equal(AutoSettings.Default, s.Auto);
        Assert.Equal(2048, s.MaxZoneMib);
        Assert.Equal((true, true, true, true, 1, 3, 600, 6144, 2), (s.Auto.Enabled, s.Auto.Discover, s.Auto.Trace, s.Auto.Relocate, s.Auto.PrewarmRecent, s.Auto.IdleMinutes, s.Auto.TraceCapSeconds, s.Auto.TraceCapMib, s.Auto.MaxRelocationsPerDay));
        Assert.Empty(s.Auto.IgnoreExes);
        Assert.Equal((20, 256), (AutoSettings.HeuristicWindowSeconds, AutoSettings.HeuristicReadMib));
    }

    [Fact]
    public void PartialAutoKeepsTheRestAtDefault()
    {
        AsterSettings s = ParseAuto(new JsonObject { ["relocate"] = false, ["idle_minutes"] = 10, ["ignore_exes"] = new JsonArray("a.exe", "b.exe") });

        Assert.False(s.Auto.Relocate);
        Assert.Equal(10, s.Auto.IdleMinutes);
        Assert.Equal(["a.exe", "b.exe"], s.Auto.IgnoreExes);
        Assert.True(s.Auto.Enabled);
        Assert.Equal(6144, s.Auto.TraceCapMib);
    }

    [Fact]
    public void GameSourceDefaultsToManual()
    {
        var root = new JsonObject { ["games"] = new JsonArray(Game()) };
        Assert.Equal("manual", SettingsStore.Parse(root.ToJsonString(), Plans).Games[0].Source);

        JsonObject g = Game();
        g["source"] = "auto";
        Assert.Equal("auto", SettingsStore.Parse(new JsonObject { ["games"] = new JsonArray(g) }.ToJsonString(), Plans).Games[0].Source);
    }

    [Theory]
    [InlineData("prewarm_recent", -1)]
    [InlineData("prewarm_recent", 9)]
    [InlineData("idle_minutes", 0)]
    [InlineData("idle_minutes", 1441)]
    [InlineData("trace_cap_seconds", 29)]
    [InlineData("trace_cap_seconds", 7201)]
    [InlineData("trace_cap_mib", 63)]
    [InlineData("trace_cap_mib", 65537)]
    [InlineData("max_relocations_per_day", -1)]
    [InlineData("max_relocations_per_day", 25)]
    public void OutOfRangeAutoNumberIsRejected(string key, int value)
    {
        var e = Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { [key] = value }));
        Assert.Contains("auto." + key, e.Message);
    }

    [Theory]
    [InlineData("prewarm_recent", 8)]
    [InlineData("idle_minutes", 1440)]
    [InlineData("trace_cap_seconds", 30)]
    [InlineData("trace_cap_mib", 65536)]
    [InlineData("max_relocations_per_day", 0)]
    public void BoundaryAutoNumbersAreAccepted(string key, int value) => ParseAuto(new JsonObject { [key] = value });

    [Theory]
    [InlineData("enabled")]
    [InlineData("discover")]
    [InlineData("trace")]
    [InlineData("relocate")]
    public void NonBoolFlagIsRejected(string key) => Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { [key] = "yes" }));

    [Fact]
    public void WrongTypesAndUnknownKeysAreRejected()
    {
        Assert.Throws<SettingsException>(() => ParseTop(t => t["auto"] = "on"));
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["idle_minutes"] = "3" }));
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["idle_minutes"] = 3.5 }));
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["bogus"] = 1 }));
    }

    [Fact]
    public void IgnoreExesAreValidated()
    {
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["ignore_exes"] = "a.exe" }));
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["ignore_exes"] = new JsonArray(@"C:\x\a.exe") }));
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["ignore_exes"] = new JsonArray("a.exe", "A.EXE") }));
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["ignore_exes"] = new JsonArray(Enumerable.Range(0, 65).Select(i => (JsonNode?)$"x{i}.exe").ToArray()) }));
        Assert.Equal(64, ParseAuto(new JsonObject { ["ignore_exes"] = new JsonArray(Enumerable.Range(0, 64).Select(i => (JsonNode?)$"x{i}.exe").ToArray()) }).Auto.IgnoreExes.Count);
    }

    [Fact]
    public void InvalidSourceIsRejected()
    {
        JsonObject g = Game();
        g["source"] = "robot";
        string json = new JsonObject { ["games"] = new JsonArray(g) }.ToJsonString();
        var e = Assert.Throws<SettingsException>(() => SettingsStore.Parse(json, Plans));
        Assert.Contains("games[0].source", e.Message);
        Assert.DoesNotContain("robot", e.Message);

        JsonObject g2 = Game();
        g2["source"] = 1;
        Assert.Throws<SettingsException>(() => SettingsStore.Parse(new JsonObject { ["games"] = new JsonArray(g2) }.ToJsonString(), Plans));
    }

    [Theory]
    [InlineData(64, true)]
    [InlineData(65536, true)]
    [InlineData(63, false)]
    [InlineData(65537, false)]
    public void MaxZoneMibRange(int value, bool ok)
    {
        if (ok)
        {
            Assert.Equal(value, ParseTop(t => t["max_zone_mib"] = value).MaxZoneMib);
        }
        else
        {
            Assert.Throws<SettingsException>(() => ParseTop(t => t["max_zone_mib"] = value));
        }
    }

    [Fact]
    public void MaxZoneMibWrongTypeIsRejected() => Assert.Throws<SettingsException>(() => ParseTop(t => t["max_zone_mib"] = "2048"));

    [Fact]
    public void NewKeysHaveDefaults()
    {
        AsterSettings s = ParseTop(_ => { });
        Assert.Equal((10, false), (s.Auto.RewarmMinutes, s.MaxZoneAuto));

        var root = new JsonObject { ["games"] = new JsonArray(Game()) };
        GameSettings g = SettingsStore.Parse(root.ToJsonString(), Plans).Games[0];
        Assert.Null(g.AntiCheat);
        Assert.False(g.Prewarm.Pin);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1440, true)]
    [InlineData(-1, false)]
    [InlineData(1441, false)]
    public void RewarmMinutesRange(int value, bool ok)
    {
        if (ok)
        {
            Assert.Equal(value, ParseAuto(new JsonObject { ["rewarm_minutes"] = value }).Auto.RewarmMinutes);
        }
        else
        {
            Assert.Contains("auto.rewarm_minutes", Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["rewarm_minutes"] = value })).Message);
        }
    }

    [Fact]
    public void MaxZoneAutoStringIsAcceptedOthersAreNot()
    {
        AsterSettings s = ParseTop(t => t["max_zone_mib"] = "auto");
        Assert.Equal((true, 2048), (s.MaxZoneAuto, s.MaxZoneMib));
        Assert.False(ParseTop(t => t["max_zone_mib"] = 4096).MaxZoneAuto);
        Assert.Throws<SettingsException>(() => ParseTop(t => t["max_zone_mib"] = "AUTO"));
        Assert.Throws<SettingsException>(() => ParseTop(t => t["max_zone_mib"] = true));
    }

    [Theory]
    [InlineData("eac")]
    [InlineData("battleye")]
    [InlineData("vanguard")]
    [InlineData("ricochet")]
    [InlineData("unknown")]
    public void AntiCheatValuesAreAccepted(string value)
    {
        JsonObject g = Game();
        g["anticheat"] = value;
        Assert.Equal(value, SettingsStore.Parse(new JsonObject { ["games"] = new JsonArray(g) }.ToJsonString(), Plans).Games[0].AntiCheat);
    }

    [Fact]
    public void BadAntiCheatIsRejectedWithoutEchoingTheValue()
    {
        JsonObject g = Game();
        g["anticheat"] = "robot";
        var e = Assert.Throws<SettingsException>(() => SettingsStore.Parse(new JsonObject { ["games"] = new JsonArray(g) }.ToJsonString(), Plans));
        Assert.Contains("games[0].anticheat", e.Message);
        Assert.DoesNotContain("robot", e.Message);

        JsonObject g2 = Game();
        g2["anticheat"] = 1;
        Assert.Throws<SettingsException>(() => SettingsStore.Parse(new JsonObject { ["games"] = new JsonArray(g2) }.ToJsonString(), Plans));
    }

    [Fact]
    public void PrewarmPinIsParsedAndMustBeBool()
    {
        JsonObject g = Game();
        g["prewarm"] = new JsonObject { ["pin"] = true };
        Assert.True(SettingsStore.Parse(new JsonObject { ["games"] = new JsonArray(g) }.ToJsonString(), Plans).Games[0].Prewarm.Pin);

        JsonObject bad = Game();
        bad["prewarm"] = new JsonObject { ["pin"] = "yes" };
        Assert.Throws<SettingsException>(() => SettingsStore.Parse(new JsonObject { ["games"] = new JsonArray(bad) }.ToJsonString(), Plans));
    }
}
