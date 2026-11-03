using System.Text.Json.Nodes;
using AsterCache.Core.Settings;
using Xunit;

namespace AsterCache.Tests.Service;

/// auto.ssd_tier 설정: 기본 꺼짐 (8 GiB, auto), 부분 지정, 알 수 없는 키·형·범위 거부, 편집기 왕복.
public sealed class TierSettingsTests : IDisposable
{
    private const string Plans = @"C:\ProgramData\AsterCache\plans";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_tiercfg_" + Guid.NewGuid().ToString("N"));

    public TierSettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static AutoSettings Parse(JsonNode? tier)
    {
        var auto = new JsonObject();
        if (tier is not null)
        {
            auto["ssd_tier"] = tier;
        }

        return SettingsStore.Parse(new JsonObject { ["games"] = new JsonArray(), ["auto"] = auto }.ToJsonString(), Plans).Auto;
    }

    [Fact]
    public void DefaultIsOffWithEightGibAndAnAutomaticFolder()
    {
        var d = new SsdTierSettings(false, 8, "auto");

        Assert.Equal(d, SsdTierSettings.Default);
        Assert.Equal(d, AutoSettings.Default.SsdTier);
        Assert.Equal(d, Parse(null).SsdTier);
        Assert.Equal(d, SettingsStore.Parse("""{"games": []}""", Plans).Auto.SsdTier);
    }

    [Fact]
    public void PartialAndFullSettingsAreRead()
    {
        Assert.Equal(new SsdTierSettings(true, 8, "auto"), Parse(new JsonObject { ["enabled"] = true }).SsdTier);
        Assert.Equal(new SsdTierSettings(false, 16, "auto"), Parse(new JsonObject { ["budget_gib"] = 16 }).SsdTier);
        Assert.Equal(new SsdTierSettings(true, 4096, @"E:\Tier\games"), Parse(new JsonObject { ["enabled"] = true, ["budget_gib"] = 4096, ["root"] = "E:/Tier/games/" }).SsdTier);
        Assert.Equal("auto", Parse(new JsonObject { ["root"] = "auto" }).SsdTier.Root);
    }

    [Fact]
    public void UnknownKeysBadTypesAndOutOfRangeValuesAreRejected()
    {
        var unknown = Assert.Throws<SettingsException>(() => Parse(new JsonObject { ["bogus"] = 1 }));
        Assert.Contains("auto.ssd_tier: unknown key bogus", unknown.Message);
        foreach (JsonNode? bad in new JsonNode?[]
        {
            true, new JsonObject { ["enabled"] = "yes" }, new JsonObject { ["enabled"] = 1 }, new JsonObject { ["budget_gib"] = 0 }, new JsonObject { ["budget_gib"] = 4097 },
            new JsonObject { ["budget_gib"] = "8" }, new JsonObject { ["budget_gib"] = 1.5 }, new JsonObject { ["root"] = 5 }, new JsonObject { ["root"] = "Tier" },
            new JsonObject { ["root"] = @"..\Tier" }, new JsonObject { ["root"] = @"\\server\share\tier" }, new JsonObject { ["root"] = "" },
        })
        {
            Assert.Throws<SettingsException>(() => Parse(bad));
        }
    }

    [Fact]
    public void AWholeDriveAsTheFolderIsRefused()
    {
        var e = Assert.Throws<SettingsException>(() => Parse(new JsonObject { ["root"] = @"E:\" }));

        Assert.Contains("auto.ssd_tier.root", e.Message);
    }

    [Fact]
    public void EditorWritesTheKeysAndReadsThemBack()
    {
        var editor = new ConfigEditor(_dir);
        editor.SetAuto(AutoSettings.Default with { SsdTier = new SsdTierSettings(true, 12, @"E:\Tier") });

        Assert.Equal(new SsdTierSettings(true, 12, @"E:\Tier"), editor.Load()!.Auto.SsdTier);
        Assert.Contains("\"ssd_tier\"", File.ReadAllText(editor.ConfigPath));

        editor.UpdateAuto(a => a with { SsdTier = a.SsdTier with { Enabled = false } }); // 다른 값 (폴더, 예산) 은 보존
        Assert.Equal(new SsdTierSettings(false, 12, @"E:\Tier"), editor.Load()!.Auto.SsdTier);
    }
}
