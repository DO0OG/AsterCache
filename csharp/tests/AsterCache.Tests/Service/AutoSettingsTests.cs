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
        Assert.Equal((true, true, true, true, 0, 3, 600, 6144, 4), (s.Auto.Enabled, s.Auto.Discover, s.Auto.Trace, s.Auto.Relocate, s.Auto.PrewarmRecent, s.Auto.IdleMinutes, s.Auto.TraceCapSeconds, s.Auto.TraceCapMib, s.Auto.MaxRelocationsPerDay));
        Assert.Equal((4, "low", 8), (s.Auto.RelocateChunkMib, s.Auto.RelocateIoPriority, s.Auto.MaxRelocateGibPerDay));
        Assert.Equal((3, 64), (s.Auto.TraceRuns, s.Auto.TraceMergeMinMib));
        Assert.Equal(8, s.Auto.RelocateParallel);
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
    [InlineData("prewarm_recent", -2)]
    [InlineData("prewarm_recent", 33)]
    [InlineData("idle_minutes", 0)]
    [InlineData("idle_minutes", 1441)]
    [InlineData("idle_seconds", 9)]
    [InlineData("idle_seconds", 86401)]
    [InlineData("trace_cap_seconds", 29)]
    [InlineData("trace_cap_seconds", 7201)]
    [InlineData("trace_cap_mib", 63)]
    [InlineData("trace_cap_mib", 65537)]
    [InlineData("trace_runs", 0)]
    [InlineData("trace_runs", 33)]
    [InlineData("trace_merge_min_mib", 0)]
    [InlineData("trace_merge_min_mib", 65537)]
    [InlineData("max_relocations_per_day", -1)]
    [InlineData("max_relocations_per_day", 25)]
    [InlineData("relocate_chunk_mib", 0)]
    [InlineData("relocate_chunk_mib", 257)]
    [InlineData("relocate_parallel", 0)]
    [InlineData("relocate_parallel", 9)]
    [InlineData("max_relocate_gib_per_day", -1)]
    [InlineData("max_relocate_gib_per_day", 1048577)]
    public void OutOfRangeAutoNumberIsRejected(string key, int value)
    {
        var e = Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { [key] = value }));
        Assert.Contains("auto." + key, e.Message);
    }

    [Theory]
    [InlineData("prewarm_recent", 32)]
    [InlineData("idle_seconds", 10)]
    [InlineData("idle_seconds", 86400)]
    [InlineData("idle_minutes", 1440)]
    [InlineData("trace_cap_seconds", 30)]
    [InlineData("trace_cap_mib", 65536)]
    [InlineData("trace_runs", 1)]
    [InlineData("trace_runs", 32)]
    [InlineData("trace_merge_min_mib", 1)]
    [InlineData("trace_merge_min_mib", 65536)]
    [InlineData("max_relocations_per_day", 0)]
    [InlineData("relocate_chunk_mib", 1)]
    [InlineData("relocate_chunk_mib", 256)]
    [InlineData("relocate_parallel", 1)]
    [InlineData("relocate_parallel", 8)]
    [InlineData("max_relocate_gib_per_day", 0)]
    [InlineData("max_relocate_gib_per_day", 1048576)]
    public void BoundaryAutoNumbersAreAccepted(string key, int value) => ParseAuto(new JsonObject { [key] = value });

    [Fact]
    public void RelocateParallelIsParsed() =>
        Assert.Equal(4, ParseAuto(new JsonObject { ["relocate_parallel"] = 4 }).Auto.RelocateParallel);

    [Theory]
    [InlineData(false, true, 2)] // 미명시 + USB 또는 종류 모름
    [InlineData(false, false, 8)] // 미명시 + 내장 SATA/NVMe
    [InlineData(true, true, 4)] // 명시값은 그대로
    [InlineData(true, false, 4)]
    public void ParallelFollowsDiskBusUnlessExplicit(bool explicitly, bool slowBus, int expected)
    {
        AutoSettings a = ParseAuto(explicitly ? new JsonObject { ["relocate_parallel"] = 4 } : new JsonObject()).Auto;

        Assert.Equal(explicitly, a.RelocateParallelSet);
        Assert.Equal(expected, a.ParallelFor(slowBus));
    }

    [Fact]
    public void RelocateThroughputKeysAreParsed()
    {
        AutoSettings a = ParseAuto(new JsonObject { ["relocate_chunk_mib"] = 64, ["relocate_io_priority"] = "normal", ["max_relocate_gib_per_day"] = 0 }).Auto;

        Assert.Equal((64, "normal", 0), (a.RelocateChunkMib, a.RelocateIoPriority, a.MaxRelocateGibPerDay));
    }

    [Theory]
    [InlineData("high")]
    [InlineData("")]
    [InlineData("LOW")]
    public void UnknownRelocateIoPriorityFallsBackToLow(string value) =>
        Assert.Equal("low", ParseAuto(new JsonObject { ["relocate_io_priority"] = value }).Auto.RelocateIoPriority);

    [Theory]
    [InlineData("all")]
    [InlineData("off")]
    [InlineData("sample")]
    public void RelocateVerifyIsParsed(string value) =>
        Assert.Equal(value, ParseAuto(new JsonObject { ["relocate_verify"] = value }).Auto.RelocateVerify);

    [Theory]
    [InlineData("ALL")]
    [InlineData("")]
    [InlineData("none")]
    public void UnknownOrMissingRelocateVerifyFallsBackToSample(string value)
    {
        Assert.Equal("sample", ParseAuto(new JsonObject { ["relocate_verify"] = value }).Auto.RelocateVerify);
        Assert.Equal("sample", ParseAuto(new JsonObject { ["relocate_verify"] = 3 }).Auto.RelocateVerify);
        Assert.Equal("sample", ParseAuto(new JsonObject()).Auto.RelocateVerify);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("target")]
    [InlineData("source")]
    public void RelocateOrderIsParsed(string value) =>
        Assert.Equal(value, ParseAuto(new JsonObject { ["relocate_order"] = value }).Auto.RelocateOrder);

    [Theory]
    [InlineData("SOURCE")]
    [InlineData("")]
    [InlineData("none")]
    public void UnknownOrMissingRelocateOrderFallsBackToAuto(string value)
    {
        Assert.Equal("auto", ParseAuto(new JsonObject { ["relocate_order"] = value }).Auto.RelocateOrder);
        Assert.Equal("auto", ParseAuto(new JsonObject { ["relocate_order"] = 3 }).Auto.RelocateOrder);
        Assert.Equal("auto", ParseAuto(new JsonObject()).Auto.RelocateOrder);
    }

    [Fact]
    public void NonStringRelocateIoPriorityFallsBackToLow() =>
        Assert.Equal("low", ParseAuto(new JsonObject { ["relocate_io_priority"] = 3 }).Auto.RelocateIoPriority);

    [Theory]
    [InlineData("enabled")]
    [InlineData("discover")]
    [InlineData("trace")]
    [InlineData("relocate")]
    public void NonBoolFlagIsRejected(string key) => Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { [key] = "yes" }));

    [Fact]
    public void ColdStartKeysDefaultToOnAndAutoBudget()
    {
        AutoSettings a = ParseTop(_ => { }).Auto;

        Assert.Equal((true, 0, true), (a.FullPrewarm, a.FullPrewarmMaxMib, a.EngineProfiles));
    }

    [Fact]
    public void ColdStartKeysAreParsed()
    {
        AutoSettings a = ParseAuto(new JsonObject { ["full_prewarm"] = false, ["full_prewarm_max_mib"] = 8192, ["engine_profiles"] = false }).Auto;

        Assert.Equal((false, 8192, false), (a.FullPrewarm, a.FullPrewarmMaxMib, a.EngineProfiles));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1048576, true)]
    [InlineData(-1, false)]
    [InlineData(1048577, false)]
    public void FullPrewarmMaxMibRange(int value, bool ok)
    {
        if (ok)
        {
            Assert.Equal(value, ParseAuto(new JsonObject { ["full_prewarm_max_mib"] = value }).Auto.FullPrewarmMaxMib);
        }
        else
        {
            Assert.Contains("auto.full_prewarm_max_mib", Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["full_prewarm_max_mib"] = value })).Message);
        }
    }

    [Theory]
    [InlineData("full_prewarm")]
    [InlineData("engine_profiles")]
    public void ColdStartFlagsMustBeBooleans(string key) =>
        Assert.Contains("auto." + key, Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { [key] = 1 })).Message);

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

    [Theory]
    [InlineData(0, true)]
    [InlineData(1440, true)]
    [InlineData(-1, false)]
    [InlineData(1441, false)]
    public void RestreamCooldownMinRange(int value, bool ok)
    {
        Assert.Equal(10, ParseAuto(new JsonObject()).Auto.RestreamCooldownMin);
        if (ok)
        {
            Assert.Equal(value, ParseAuto(new JsonObject { ["restream_cooldown_min"] = value }).Auto.RestreamCooldownMin);
        }
        else
        {
            Assert.Contains("auto.restream_cooldown_min", Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["restream_cooldown_min"] = value })).Message);
        }
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1440, true)]
    [InlineData(-1, false)]
    [InlineData(1441, false)]
    public void RewarmAfterExitMinRange(int value, bool ok)
    {
        Assert.Equal(2, ParseAuto(new JsonObject()).Auto.RewarmAfterExitMin);
        if (ok)
        {
            Assert.Equal(value, ParseAuto(new JsonObject { ["rewarm_after_exit_min"] = value }).Auto.RewarmAfterExitMin);
        }
        else
        {
            Assert.Contains("auto.rewarm_after_exit_min", Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["rewarm_after_exit_min"] = value })).Message);
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
    public void NewAutoKeysHaveDefaults()
    {
        AutoSettings a = ParseAuto(new JsonObject()).Auto;
        Assert.Equal((0, 30, LearnSettings.Default), (a.PrewarmRecent, a.IdleSeconds, a.Learn));
        Assert.Equal(new LearnSettings(false, 512, 0), LearnSettings.Default); // E7 재측정 전까지 기본 꺼짐, 스트림 모드·창 512 MiB
        Assert.Equal(LearnSettings.StreamMode, LearnSettings.Default.Mode);
    }

    [Theory]
    [InlineData(1, 3, true)] // beta.3 트레이가 항상 쓰던 기본
    [InlineData(2, 3, false)]
    [InlineData(1, 5, false)]
    [InlineData(0, 3, false)]
    public void Beta3DefaultsMigrateOnlyWhenBothMatchAndThereIsNoLearnKey(int recent, int idleMinutes, bool migrated)
    {
        AutoSettings a = ParseAuto(new JsonObject { ["prewarm_recent"] = recent, ["idle_minutes"] = idleMinutes }).Auto;

        Assert.Equal(migrated, a.Migrated);
        Assert.Equal(migrated ? 0 : recent, a.PrewarmRecent);
        Assert.Equal(migrated ? TimeSpan.FromSeconds(30) : TimeSpan.FromMinutes(idleMinutes), a.IdleWindow);
    }

    [Fact]
    public void LearnKeyOrMissingKeysPreventTheMigration()
    {
        Assert.False(ParseAuto(new JsonObject { ["prewarm_recent"] = 1, ["idle_minutes"] = 3, ["learn"] = new JsonObject() }).Auto.Migrated);
        Assert.False(ParseAuto(new JsonObject { ["prewarm_recent"] = 1 }).Auto.Migrated);
        Assert.False(ParseAuto(new JsonObject()).Auto.Migrated);
    }

    [Fact]
    public void PrewarmRecentAcceptsMinusOneAndRejectsBelow()
    {
        Assert.Equal(-1, ParseAuto(new JsonObject { ["prewarm_recent"] = -1 }).Auto.PrewarmRecent);
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["prewarm_recent"] = -2 }));
    }

    [Fact]
    public void MinusOneSelectsNoRecentGames()
    {
        GameSettings g = new("a", ["a.exe"], "t", "p", @"D:\G", [@"D:\G"], "L", 1440);
        var played = new Dictionary<string, DateTimeOffset> { ["a"] = DateTimeOffset.UtcNow };

        Assert.Empty(AsterCache.Service.AutoMode.Recent([g], played, -1));
        Assert.Single(AsterCache.Service.AutoMode.Recent([g], played, 0));
    }

    [Fact]
    public void WithoutApplyRightsAutoModeIsSwitchedOffWithALogLine()
    {
        var log = new ListLog();
        AsterSettings s = AsterCache.Service.Worker.ForApply(ParseAuto(new JsonObject()), canApply: false, log);

        Assert.False(s.Auto.Enabled);
        Assert.Single(log.Lines);
        Assert.True(AsterCache.Service.Worker.ForApply(ParseAuto(new JsonObject()), canApply: true, log).Auto.Enabled);
    }

    [Fact]
    public void IdleWindowUsesIdleSecondsWhenGivenOrWhenNeitherIsGiven()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), ParseAuto(new JsonObject()).Auto.IdleWindow);
        Assert.Equal(TimeSpan.FromSeconds(45), ParseAuto(new JsonObject { ["idle_seconds"] = 45 }).Auto.IdleWindow);
        Assert.Equal(TimeSpan.FromSeconds(45), ParseAuto(new JsonObject { ["idle_seconds"] = 45, ["idle_minutes"] = 10 }).Auto.IdleWindow);
    }

    [Fact]
    public void IdleWindowUsesIdleMinutesWhenOnlyItIsGiven()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), ParseAuto(new JsonObject { ["idle_minutes"] = 10 }).Auto.IdleWindow);
    }

    [Fact]
    public void LearnValuesAreParsedWithDefaults()
    {
        Assert.Equal(LearnSettings.Default, ParseAuto(new JsonObject()).Auto.Learn);
        Assert.Equal(new LearnSettings(false, 1024, 65536), ParseAuto(new JsonObject
        {
            ["learn"] = new JsonObject { ["enabled"] = false, ["window_mib"] = 1024, ["max_total_mib"] = 65536 },
        }).Auto.Learn);
        Assert.Equal(new LearnSettings(false, 512, 0), ParseAuto(new JsonObject { ["learn"] = new JsonObject() }).Auto.Learn);
        Assert.Equal(new LearnSettings(true, 512, 0), ParseAuto(new JsonObject { ["learn"] = new JsonObject { ["enabled"] = true } }).Auto.Learn);
        Assert.Equal(new LearnSettings(false, 512, 0) { Mode = LearnSettings.GapMode }, ParseAuto(new JsonObject { ["learn"] = new JsonObject { ["mode"] = "gap" } }).Auto.Learn);
    }

    [Theory]
    [InlineData("window_mib", 0)]
    [InlineData("window_mib", 1025)]
    [InlineData("max_total_mib", -1)]
    [InlineData("max_total_mib", 65537)]
    public void LearnNumbersOutOfRangeAreRejected(string key, int value)
    {
        var learn = new JsonObject { [key] = value };
        var e = Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["learn"] = learn }));
        Assert.Contains("auto.learn." + key, e.Message);
    }

    [Fact]
    public void LearnModeBoundedIsAccepted() =>
        Assert.Equal(LearnSettings.BoundedMode, ParseAuto(new JsonObject { ["learn"] = new JsonObject { ["mode"] = "bounded" } }).Auto.Learn.Mode);

    [Fact]
    public void LearnModeAdaptiveAndSaturationMbpsAreParsedWithDefault80()
    {
        Assert.Equal(80, ParseAuto(new JsonObject { ["learn"] = new JsonObject() }).Auto.Learn.SaturationMbps);
        Assert.Equal(LearnSettings.AdaptiveMode, ParseAuto(new JsonObject { ["learn"] = new JsonObject { ["mode"] = "adaptive" } }).Auto.Learn.Mode);
        Assert.False(ParseAuto(new JsonObject()).Auto.Learn.Enabled);
        Assert.Equal(LearnSettings.StreamMode, ParseAuto(new JsonObject()).Auto.Learn.Mode);
        Assert.Equal(1, ParseAuto(new JsonObject { ["learn"] = new JsonObject { ["saturation_mbps"] = 1 } }).Auto.Learn.SaturationMbps);
        Assert.Equal(10000, ParseAuto(new JsonObject { ["learn"] = new JsonObject { ["saturation_mbps"] = 10000 } }).Auto.Learn.SaturationMbps);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10001)]
    [InlineData(-5)]
    public void LearnSaturationMbpsOutOfRangeIsRejected(int value)
    {
        var e = Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["learn"] = new JsonObject { ["saturation_mbps"] = value } }));
        Assert.Contains("auto.learn.saturation_mbps", e.Message);
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["learn"] = new JsonObject { ["saturation_mbps"] = "80" } }));
    }

    [Fact]
    public void SharedProfilesDefaultToOnWithTheOfficialUrl()
    {
        SharedProfilesSettings d = ParseAuto(new JsonObject()).Auto.SharedProfiles;

        Assert.True(d.Enabled);
        Assert.Equal("https://github.com/DO0OG/AsterCache-profiles/releases/latest/download/profiles.zip", d.Url);
        Assert.Equal(new SharedProfilesSettings(false, d.Url), ParseAuto(new JsonObject { ["shared_profiles"] = new JsonObject { ["enabled"] = false } }).Auto.SharedProfiles);
        Assert.Equal(new SharedProfilesSettings(true, "https://example.org/p.zip"), ParseAuto(new JsonObject { ["shared_profiles"] = new JsonObject { ["url"] = "https://example.org/p.zip" } }).Auto.SharedProfiles);
    }

    [Theory]
    [InlineData("http://example.org/p.zip")]
    [InlineData("ftp://example.org/p.zip")]
    [InlineData("file:///C:/p.zip")]
    [InlineData("example.org/p.zip")]
    [InlineData("https://user:pw@example.org/p.zip")]
    [InlineData("")]
    public void SharedProfilesUrlMustBeAnAbsoluteHttpsUrl(string url)
    {
        var e = Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["shared_profiles"] = new JsonObject { ["url"] = url } }));
        Assert.Contains("auto.shared_profiles.url", e.Message);
    }

    [Fact]
    public void SharedProfilesBadTypesAndUnknownKeysAreRejected()
    {
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["shared_profiles"] = new JsonObject { ["url"] = 1 } }));
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["shared_profiles"] = new JsonObject { ["enabled"] = "yes" } }));
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["shared_profiles"] = new JsonObject { ["bogus"] = 1 } }));
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["shared_profiles"] = true }));
    }

    [Theory]
    [InlineData("burst")]
    [InlineData("")]
    [InlineData("STREAM")]
    public void LearnModeOtherThanStreamGapOrBoundedIsRejected(string mode)
    {
        var e = Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["learn"] = new JsonObject { ["mode"] = mode } }));
        Assert.Contains("auto.learn.mode", e.Message);
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["learn"] = new JsonObject { ["mode"] = 1 } }));
    }

    [Fact]
    public void UnknownLearnKeyAndBadLearnTypesAreRejected()
    {
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["learn"] = new JsonObject { ["bogus"] = 1 } }));
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["learn"] = new JsonObject { ["enabled"] = "yes" } }));
        Assert.Throws<SettingsException>(() => ParseAuto(new JsonObject { ["learn"] = 1 }));
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

    [Fact]
    public void AutoParallelIsOneOnSmrAndKeepsTheBusRules()
    {
        Assert.Equal(1, AutoSettings.Default.ParallelFor(slowBus: false, smr: true));
        Assert.Equal(2, AutoSettings.Default.ParallelFor(slowBus: true));
        Assert.Equal(8, AutoSettings.Default.ParallelFor(slowBus: false));
    }
}
