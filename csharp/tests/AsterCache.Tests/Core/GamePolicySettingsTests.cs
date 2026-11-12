using System.Text.RegularExpressions;
using AsterCache.Core.Settings;
using Xunit;

namespace AsterCache.Tests.Core;

/// 게임별 정책·일시 중지, 시작 스트림 스위치, 재배치 시간대, 업데이트 키의 해석·저장·편집·판정.
public sealed class GamePolicySettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_policy_" + Guid.NewGuid().ToString("N"));

    public GamePolicySettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private GameInput Game(string name) => new(
        name, [name + ".exe"], Path.Combine(_dir, "plans", name + ".prefetch.json"), Path.Combine(_dir, "plans", name + ".plan.json"),
        Path.Combine(_dir, "Games", name), [Path.Combine(_dir, "Games")], "게임");

    private ConfigEditor Editor()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a"));
        return editor;
    }

    private static string Text(ConfigEditor e) => File.ReadAllText(e.ConfigPath);

    private static string Esc(string p) => p.Replace("\\", "\\\\", StringComparison.Ordinal);

    private AsterSettings Parse(string extraTop = "", string autoBody = "", string gameExtra = "")
    {
        string plans = Path.Combine(_dir, "plans");
        string game = "{\"name\":\"a\",\"exe_names\":[\"a.exe\"],\"trace\":\"" + Esc(Path.Combine(plans, "a.t.json")) + "\",\"plan\":\"" + Esc(Path.Combine(plans, "a.p.json"))
            + "\",\"origin_root\":\"" + Esc(Path.Combine(_dir, "g", "a")) + "\",\"allowed_roots\":[\"" + Esc(Path.Combine(_dir, "g")) + "\"],\"volume_label\":\"V\"" + gameExtra + "}";
        string auto = autoBody.Length > 0 ? ",\"auto\":{" + autoBody + "}" : "";
        return SettingsStore.Parse("{\"games\":[" + game + "]" + auto + extraTop + "}", plans);
    }

    [Fact]
    public void FileWithoutNewKeysReadsAsDefaultsAndSavingAddsNone()
    {
        ConfigEditor editor = Editor();
        editor.SetAuto(AutoSettings.Default);
        string before = Text(editor);

        AsterSettings s = editor.Load()!;
        editor.SetAuto(s.Auto);
        editor.UpdateAuto(a => a);

        Assert.Equal((GameMode.Inherit, false, true, 0, UpdateMode.Auto, false), (s.Games[0].Mode, s.Games[0].Paused, s.Auto.StartStreamEnabled, s.Auto.RelocateHours.Count, s.Update.Mode, s.Update.Prerelease));
        Assert.Equal(before, Text(editor));
        foreach (string key in new[] { "inherit", "\"paused\"", "start_stream_enabled", "relocate_hours", "\"update\"", "prerelease" })
        {
            Assert.DoesNotContain(key, before, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ValidValuesAreReadAndRoundTripThroughSetAuto()
    {
        AsterSettings s = Parse(
            ",\"update\":{\"mode\":\"notify\",\"prerelease\":true}", "\"start_stream_enabled\":false,\"relocate_hours\":[3,0,23]", ",\"mode\":\"relocate_only\",\"paused\":true");
        Assert.Equal((GameMode.RelocateOnly, true, false, UpdateMode.Notify, true), (s.Games[0].Mode, s.Games[0].Paused, s.Auto.StartStreamEnabled, s.Update.Mode, s.Update.Prerelease));
        Assert.Equal([3, 0, 23], s.Auto.RelocateHours);

        ConfigEditor editor = Editor();
        editor.SetAuto(s.Auto);
        editor.SetUpdateMode(UpdateMode.Notify);
        AutoSettings read = editor.Load()!.Auto;
        Assert.False(read.StartStreamEnabled);
        Assert.Equal([3, 0, 23], read.RelocateHours);
        Assert.Equal(UpdateMode.Notify, editor.Load()!.Update.Mode);

        editor.UpdateAuto(a => a with { Relocate = false }); // 다른 값만 바꿔도 새 키가 보존됨
        Assert.Equal((false, 3), (editor.Load()!.Auto.StartStreamEnabled, editor.Load()!.Auto.RelocateHours.Count));
    }

    [Theory]
    [InlineData("game", "\"mode\":\"weird\"")]
    [InlineData("game", "\"mode\":1")]
    [InlineData("game", "\"paused\":\"yes\"")]
    [InlineData("auto", "\"start_stream_enabled\":1")]
    [InlineData("auto", "\"relocate_hours\":[24]")]
    [InlineData("auto", "\"relocate_hours\":[-1]")]
    [InlineData("auto", "\"relocate_hours\":[1,1]")]
    [InlineData("auto", "\"relocate_hours\":[0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,16,17,18,19,20,21,22,23,0]")]
    [InlineData("auto", "\"relocate_hours\":\"3\"")]
    [InlineData("auto", "\"relocate_hours\":[1.5]")]
    [InlineData("update", "{\"mode\":\"later\"}")]
    [InlineData("update", "{\"prerelease\":\"no\"}")]
    [InlineData("update", "{\"channel\":\"x\"}")]
    [InlineData("update", "[]")]
    public void BadValuesAreRejected(string where, string json)
    {
        Assert.Throws<SettingsException>(() => where switch
        {
            "game" => Parse(gameExtra: "," + json),
            "auto" => Parse(autoBody: json),
            _ => Parse(extraTop: ",\"update\":" + json),
        });
    }

    [Fact]
    public void AllTwentyFourHoursAreAccepted()
    {
        AsterSettings s = Parse(autoBody: "\"relocate_hours\":[" + string.Join(',', Enumerable.Range(0, 24)) + "]");
        Assert.Equal(24, s.Auto.RelocateHours.Count);
    }

    [Fact]
    public void EditorMethodsWriteValuesAndDefaultsRemoveTheKeys()
    {
        ConfigEditor editor = Editor();
        string pristine = Text(editor);

        editor.SetGameMode("A", GameMode.PrewarmOnly); // 이름은 대소문자 무시
        editor.SetGamePaused("a", true);
        editor.SetStartStreamEnabled(false);
        editor.SetRelocateHours([2, 3]);
        editor.SetUpdateMode(UpdateMode.Off);
        editor.SetUpdatePrerelease(true);
        AsterSettings s = editor.Load()!;
        Assert.Equal((GameMode.PrewarmOnly, true, false, UpdateMode.Off, true), (s.Games[0].Mode, s.Games[0].Paused, s.Auto.StartStreamEnabled, s.Update.Mode, s.Update.Prerelease));
        Assert.Equal([2, 3], s.Auto.RelocateHours);

        editor.SetGameMode("a", GameMode.Inherit);
        editor.SetGamePaused("a", false);
        editor.SetStartStreamEnabled(true);
        editor.SetRelocateHours([]);
        editor.SetUpdateMode(UpdateMode.Auto);
        editor.SetUpdatePrerelease(false);
        Assert.Equal(pristine, Text(editor)); // 기본값으로 되돌리면 키와 빈 객체까지 사라짐
    }

    [Fact]
    public void EditorKeepsOtherKeysAndRejectsBadInputWithoutChangingTheFile()
    {
        ConfigEditor editor = Editor();
        editor.SetAuto(AutoSettings.Default with { IdleMinutes = 9 });
        editor.SetStartStreamEnabled(false);
        Assert.Equal(9, editor.Load()!.Auto.IdleMinutes);
        byte[] before = File.ReadAllBytes(editor.ConfigPath);

        Assert.Throws<SettingsException>(() => editor.SetRelocateHours([24]));
        Assert.Throws<SettingsException>(() => editor.SetRelocateHours([1, 1]));
        Assert.Throws<SettingsException>(() => editor.SetRelocateHours(Enumerable.Repeat(0, 25).ToList()));
        Assert.Throws<SettingsException>(() => editor.SetGameMode("nope", GameMode.Off)); // 없는 게임
        Assert.Throws<SettingsException>(() => editor.SetGamePaused("nope", true));
        Assert.Throws<SettingsException>(() => editor.SetGameMode("a", (GameMode)99));
        Assert.Equal(before, File.ReadAllBytes(editor.ConfigPath));
    }

    [Fact]
    public void EditorOnMissingConfigCreatesOnlyWhatIsNeeded()
    {
        var editor = new ConfigEditor(_dir);
        editor.SetStartStreamEnabled(true); // 지울 것도 쓸 것도 없음
        Assert.False(File.Exists(editor.ConfigPath));

        editor.SetUpdatePrerelease(true);
        Assert.True(editor.Load()!.Update.Prerelease);
    }

    public static TheoryData<GameMode, bool, bool, bool, bool> Truth => new()
    {
        { GameMode.Inherit, false, true, true, true },
        { GameMode.PrewarmOnly, false, true, false, true },
        { GameMode.RelocateOnly, false, false, true, true },
        { GameMode.Off, false, false, false, false },
        { GameMode.Inherit, true, false, false, false },
        { GameMode.PrewarmOnly, true, false, false, false },
        { GameMode.RelocateOnly, true, false, false, false },
        { GameMode.Off, true, false, false, false },
    };

    [Theory]
    [MemberData(nameof(Truth))]
    public void PolicyTruthTable(GameMode mode, bool paused, bool prewarm, bool relocate, bool tracking)
    {
        GameSettings g = Parse().Games[0] with { Mode = mode, Paused = paused };

        Assert.Equal((prewarm, relocate, tracking), (GamePolicy.AllowsPrewarm(g), GamePolicy.AllowsRelocate(g), GamePolicy.AllowsTracking(g)));
    }

    [Fact]
    public void RelocateHoursGateTheStartHour()
    {
        Assert.All(Enumerable.Range(0, 24), h => Assert.True(GamePolicy.AllowsRelocateAt(AutoSettings.Default, h)));

        AutoSettings night = AutoSettings.Default with { RelocateHours = [2, 3, 23] };
        Assert.Equal([2, 3, 23], Enumerable.Range(0, 24).Where(h => GamePolicy.AllowsRelocateAt(night, h)));
    }

    [Fact]
    public void SetGameOptionsWritesPolicyAndNamesInOneSaveAndDefaultsRemoveTheKeys()
    {
        ConfigEditor editor = Editor();
        string pristine = Text(editor);

        editor.SetGameOptions("A", GameMode.Off, true, "Default", new Dictionary<string, string> { ["ko"] = "한국어", ["en"] = "  " });

        GameSettings g = editor.Load()!.Games[0];
        Assert.Equal(("a", GameMode.Off, true, "Default"), (g.Name, g.Mode, g.Paused, g.DisplayName));
        Assert.Equal("한국어", g.DisplayNames!["ko"]);
        Assert.False(g.DisplayNames.ContainsKey("en"));

        editor.SetGameOptions("a", GameMode.Inherit, false, null, new Dictionary<string, string>());
        Assert.Equal(pristine, Text(editor));
    }

    [Fact]
    public void SetGameOptionsRejectsBadNamesAndUnknownGamesWithoutChangingTheFile()
    {
        ConfigEditor editor = Editor();
        string before = Text(editor);

        Assert.Throws<SettingsException>(() => editor.SetGameOptions("a", GameMode.Off, false, "a	b", null));
        Assert.Throws<SettingsException>(() => editor.SetGameOptions("a", GameMode.Off, false, null, new Dictionary<string, string> { ["ko"] = new string('x', 500) }));
        Assert.Throws<SettingsException>(() => editor.SetGameOptions("zzz", GameMode.Off, false, null, null));
        Assert.Equal(before, Text(editor));
    }

    [Fact]
    public void UpdateAutoWritesTheCacheBudgetToEveryGameAndZeroRemovesItKeepingOtherPrewarmKeys()
    {
        ConfigEditor editor = Editor();
        editor.Add(Game("b") with { OnServiceStart = false });

        editor.UpdateAuto(a => a, maxCacheMib: 2048);
        Assert.All(editor.Load()!.Games, g => Assert.Equal(2048, g.Prewarm.MaxCacheMib));

        editor.UpdateAuto(a => a, maxCacheMib: 0);
        AsterSettings s = editor.Load()!;
        Assert.All(s.Games, g => Assert.Equal(0, g.Prewarm.MaxCacheMib));
        Assert.DoesNotContain("max_cache_mib", Text(editor), StringComparison.Ordinal);
        Assert.False(s.Games[1].Prewarm.OnServiceStart); // b 의 다른 prewarm 키는 남음
        Assert.Equal(1, Regex.Count(Text(editor), "\"prewarm\"")); // a 의 빈 prewarm 객체는 사라짐
    }
}
