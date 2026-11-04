using System.Text.Json.Nodes;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// max_stream_mib 미명시 기본 (존 전체) 와 명시값 우선, riot 설정 키.
public sealed class StreamCapSettingsTests
{
    private const string Plans = @"C:\ProgramData\AsterCache\plans";
    private const long Mib = 1L << 20;

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

    private static JsonObject WithMax(int mib)
    {
        JsonObject g = Game();
        g["max_stream_mib"] = mib;
        return g;
    }

    private static AsterSettings Parse(JsonObject game, JsonNode? riot = null)
    {
        var root = new JsonObject { ["games"] = new JsonArray(game) };
        if (riot is not null)
        {
            root["riot"] = riot;
        }

        return SettingsStore.Parse(root.ToJsonString(), Plans);
    }

    [Fact]
    public void UnsetMaxStreamMibReadsTheWholeZoneAndAnExplicitValueWins()
    {
        GameSettings unset = Parse(Game()).Games[0];
        Assert.False(unset.MaxStreamSet);
        Assert.Equal(8192 * Mib, PrefetchStreamer.Budget(unset, 8192 * Mib, 0)); // 존 8 GiB 는 4096 에서 잘리지 않음
        Assert.Equal(2000 * Mib, PrefetchStreamer.Budget(unset, 2000 * Mib, 0));
        Assert.Equal(4096 * Mib, PrefetchStreamer.Budget(unset, null, 9000 * Mib)); // 계획이 없으면 추적 총 바이트를 기본 상한으로 자름

        GameSettings explicitCap = Parse(WithMax(4096)).Games[0]; // 기본과 같은 값을 명시해도 명시값
        Assert.True(explicitCap.MaxStreamSet);
        Assert.Equal(4096, explicitCap.MaxStreamMib);
        Assert.Equal(4096 * Mib, PrefetchStreamer.Budget(explicitCap, 8192 * Mib, 0));
        Assert.Equal(8192 * Mib, PrefetchStreamer.Budget(Parse(WithMax(16384)).Games[0], 8192 * Mib, 0));
    }

    [Fact]
    public void StreamCapBytesIsTheLargerOfDefaultAndZoneOnlyWhenUnset()
    {
        var g = new GameSettings("a", ["a.exe"], "t", "p", @"D:\a", [@"D:\"], "L", 60);

        Assert.Equal(4096 * Mib, g.StreamCapBytes(100 * Mib));
        Assert.Equal(8192 * Mib, g.StreamCapBytes(8192 * Mib));
        GameSettings capped = g with { MaxStreamMib = 1024, MaxStreamSet = true };
        Assert.Equal(1024 * Mib, capped.StreamCapBytes(8192 * Mib));
    }

    [Fact]
    public void RiotSettingsDefaultOffAndAreValidated()
    {
        RiotSettings d = Parse(Game()).Riot;
        Assert.Equal((false, 2048), (d.ChampSelectPrewarm, d.ChampSelectBudgetMib));

        RiotSettings on = Parse(Game(), new JsonObject { ["champ_select_prewarm"] = true, ["champ_select_budget_mib"] = 1024 }).Riot;
        Assert.Equal((true, 1024), (on.ChampSelectPrewarm, on.ChampSelectBudgetMib));

        Assert.Throws<SettingsException>(() => Parse(Game(), new JsonObject { ["champ_select_prewarm"] = "yes" }));
        Assert.Throws<SettingsException>(() => Parse(Game(), new JsonObject { ["champ_select_budget_mib"] = 1 }));
        Assert.Throws<SettingsException>(() => Parse(Game(), new JsonObject { ["unknown"] = 1 }));
        Assert.Throws<SettingsException>(() => Parse(Game(), JsonValue.Create(true)));
    }
}
