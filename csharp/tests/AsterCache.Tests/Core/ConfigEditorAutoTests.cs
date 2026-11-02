using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using Xunit;

namespace AsterCache.Tests.Core;

/// ConfigEditor 의 자동 모드 편집과 동시 쓰기 잠금.
public sealed class ConfigEditorAutoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_cfgauto_" + Guid.NewGuid().ToString("N"));

    public ConfigEditorAutoTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private GameInput Game(string name, string? source = null) => new(
        name, [name + ".exe"], Path.Combine(_dir, "plans", name + ".prefetch.json"), Path.Combine(_dir, "plans", name + ".plan.json"),
        Path.Combine(_dir, "Games", name), [Path.Combine(_dir, "Games")], "게임")
    {
        Source = source,
    };

    [Fact]
    public void SetAutoRoundTripsAndWritesEveryKeyEvenAtDefault()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a"));
        editor.SetAuto(AutoSettings.Default);

        Assert.Equal(11, CountKeys(File.ReadAllText(editor.ConfigPath), "auto"));
        Assert.Equal(AutoSettings.Default, editor.Load()!.Auto with { IgnoreExes = [] });

        var custom = new AutoSettings(false, false, true, false, 3, 7, 90, 128, 5, ["a.exe", "b.exe"]);
        editor.SetAuto(custom);
        AutoSettings read = editor.Load()!.Auto;
        Assert.Equal(custom with { IgnoreExes = [] }, read with { IgnoreExes = [] });
        Assert.Equal(["a.exe", "b.exe"], read.IgnoreExes);

        editor.SetAuto(AutoSettings.Default with { RewarmMinutes = 0 });
        Assert.Equal(0, editor.Load()!.Auto.RewarmMinutes);
    }

    [Fact]
    public void SetMaxZoneAutoIsStoredAndNumberTurnsItOff()
    {
        var editor = new ConfigEditor(_dir);
        editor.SetMaxZoneAuto();
        Assert.Equal((true, 2048), (editor.Load()!.MaxZoneAuto, editor.Load()!.MaxZoneMib));

        editor.SetMaxZoneMib(4096);
        Assert.Equal((false, 4096), (editor.Load()!.MaxZoneAuto, editor.Load()!.MaxZoneMib));
    }

    [Fact]
    public void AntiCheatIsWrittenOnlyWhenGivenAndRejectedWhenUnknown()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a") with { AntiCheat = "battleye" });
        editor.Add(Game("b"));

        AsterSettings s = editor.Load()!;

        Assert.Equal("battleye", s.Games[0].AntiCheat);
        Assert.Null(s.Games[1].AntiCheat);
        Assert.DoesNotContain("anticheat", File.ReadAllText(editor.ConfigPath).Split("\"b.exe\"")[1], StringComparison.Ordinal);
        Assert.Throws<SettingsException>(() => editor.Add(Game("c") with { AntiCheat = "robot" }));
    }

    // 최상위 객체 obj 의 키 개수.
    private static int CountKeys(string json, string obj) =>
        ((JObject)((JObject)JsonRead.Parse(json)!)[obj]!).Count;

    [Fact]
    public void RejectedAutoDoesNotChangeTheFile()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a"));
        byte[] before = File.ReadAllBytes(editor.ConfigPath);

        Assert.Throws<SettingsException>(() => editor.SetAuto(AutoSettings.Default with { IdleMinutes = 0 }));
        Assert.Throws<SettingsException>(() => editor.SetMaxZoneMib(10));

        Assert.Equal(before, File.ReadAllBytes(editor.ConfigPath));
    }

    [Fact]
    public void SetMaxZoneMibIsStored()
    {
        var editor = new ConfigEditor(_dir);
        editor.SetMaxZoneMib(4096);

        Assert.Equal(4096, editor.Load()!.MaxZoneMib);
    }

    [Fact]
    public void ConfigWithoutNewKeysReadsAsDefaultsAndLoadLeavesBytesAlone()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a"));
        byte[] before = File.ReadAllBytes(editor.ConfigPath);

        AsterSettings s = editor.Load()!;

        Assert.Equal((AutoSettings.Default, 2048, "manual"), (s.Auto, s.MaxZoneMib, s.Games[0].Source));
        Assert.Equal(before, File.ReadAllBytes(editor.ConfigPath));
        Assert.DoesNotContain("source", File.ReadAllText(editor.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void SourceAndOnServiceStartAreWrittenOnlyWhenGiven()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a", "auto") with { OnServiceStart = false, TriggerExes = ["steam.exe"] });
        editor.Add(Game("b", "manual"));

        AsterSettings s = editor.Load()!;

        Assert.Equal("auto", s.Games[0].Source);
        Assert.False(s.Games[0].Prewarm.OnServiceStart);
        Assert.Equal(["steam.exe"], s.Games[0].Prewarm.TriggerExes);
        Assert.Equal("manual", s.Games[1].Source);
        Assert.DoesNotContain("source", File.ReadAllText(editor.ConfigPath).Split("\"b.exe\"")[1], StringComparison.Ordinal);
        Assert.Throws<SettingsException>(() => editor.Add(Game("c", "robot")));
    }

    private ConfigEditor Full(int manual, int auto)
    {
        var editor = new ConfigEditor(_dir);
        for (int i = 0; i < manual; i++)
        {
            editor.Add(Game("m" + i));
        }

        for (int i = 0; i < auto; i++)
        {
            editor.AddAuto(Game("a" + i), new Dictionary<string, DateTimeOffset>());
        }

        return editor;
    }

    [Fact]
    public void AddAutoBelowTheLimitEvictsNothing()
    {
        ConfigEditor editor = Full(1, 0);

        Assert.Null(editor.AddAuto(Game("x"), new Dictionary<string, DateTimeOffset>()));
        Assert.Equal("auto", editor.Load()!.Games.Single(g => g.Name == "x").Source);
    }

    [Fact]
    public void AddAutoAtTheLimitEvictsTheLeastRecentlyPlayedAutoEntry()
    {
        ConfigEditor editor = Full(2, 30); // 32 개
        var now = DateTimeOffset.UtcNow;
        var played = Enumerable.Range(0, 30).ToDictionary(i => "a" + i, i => now.AddHours(-i));
        played["a7"] = now.AddDays(-400); // 가장 오래됨
        played["m0"] = now.AddYears(-20); // 수동 항목은 더 오래돼도 밀리지 않음

        string? evicted = editor.AddAuto(Game("new"), played);

        Assert.Equal("a7", evicted);
        AsterSettings s = editor.Load()!;
        Assert.Equal(32, s.Games.Count);
        Assert.DoesNotContain(s.Games, g => g.Name == "a7");
        Assert.Contains(s.Games, g => g.Name == "m0");
        Assert.Equal("auto", s.Games.Single(g => g.Name == "new").Source);
    }

    [Fact]
    public void NoPlayRecordCountsAsOldestAndTiesGoToTheEarlierEntry()
    {
        ConfigEditor editor = Full(0, 32);
        var played = Enumerable.Range(0, 32).Where(i => i is not (4 or 9)).ToDictionary(i => "a" + i, _ => DateTimeOffset.UtcNow);

        Assert.Equal("a4", editor.AddAuto(Game("new"), played));
    }

    [Fact]
    public void AddAutoFailsWhenFullOfManualEntriesAndKeepsTheFile()
    {
        ConfigEditor editor = Full(32, 0);
        byte[] before = File.ReadAllBytes(editor.ConfigPath);

        Assert.Throws<SettingsException>(() => editor.AddAuto(Game("new"), new Dictionary<string, DateTimeOffset>()));

        Assert.Equal(before, File.ReadAllBytes(editor.ConfigPath));
    }

    [Fact]
    public void FailedAddAutoDoesNotEvictAnything()
    {
        ConfigEditor editor = Full(0, 32);
        byte[] before = File.ReadAllBytes(editor.ConfigPath);

        Assert.Throws<SettingsException>(() => editor.AddAuto(Game("a3"), new Dictionary<string, DateTimeOffset>())); // 이름 중복으로 검증 실패

        Assert.Equal(before, File.ReadAllBytes(editor.ConfigPath));
    }

    [Fact]
    public void AddExeNameAppendsToAnExistingGame()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a"));

        editor.AddExeName("A", "Launcher.exe");

        Assert.Equal(["a.exe", "Launcher.exe"], editor.Load()!.Games[0].ExeNames);
    }

    [Fact]
    public void AddExeNameRejectsUnknownGameDuplicateBadNameAndTheSeventeenth()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a"));
        editor.Add(Game("b"));
        Assert.Throws<SettingsException>(() => editor.AddExeName("nope", "x.exe"));
        Assert.Throws<SettingsException>(() => editor.AddExeName("a", "A.EXE"));
        Assert.Throws<SettingsException>(() => editor.AddExeName("a", "b.exe")); // 다른 게임이 쓰는 이름
        Assert.Throws<SettingsException>(() => editor.AddExeName("a", @"C:\x\y.exe"));
        for (int i = 0; i < 15; i++)
        {
            editor.AddExeName("a", $"x{i}.exe");
        }

        Assert.Throws<SettingsException>(() => editor.AddExeName("a", "x15.exe"));
        Assert.Equal(16, editor.Load()!.Games[0].ExeNames.Count);
    }

    [Fact]
    public void HeldLockMakesTheEditWaitThenFailWithoutChangingTheConfig()
    {
        var editor = new ConfigEditor(_dir, 300);
        editor.Add(Game("a"));
        byte[] before = File.ReadAllBytes(editor.ConfigPath);

        using (new FileStream(editor.ConfigPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            Assert.Throws<IOException>(() => editor.Add(Game("b")));
            Assert.InRange(watch.ElapsedMilliseconds, 250, 4000);
        }

        Assert.Equal(before, File.ReadAllBytes(editor.ConfigPath));
        editor.Add(Game("b")); // 풀린 뒤에는 정상
        Assert.Equal(2, editor.Load()!.Games.Count);
    }

    [Fact]
    public async Task LockReleasedWhileWaitingLetsTheEditThrough()
    {
        var editor = new ConfigEditor(_dir, 5000);
        var held = new FileStream(editor.ConfigPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        Task release = Task.Run(() =>
        {
            Thread.Sleep(250);
            held.Dispose();
        });

        editor.Add(Game("a"));

        await release;
        Assert.Single(editor.Load()!.Games);
    }

    [Fact]
    public void ConcurrentEditorsDoNotLoseEachOthersChanges()
    {
        var a = new ConfigEditor(_dir);
        var b = new ConfigEditor(_dir);

        Parallel.For(0, 20, i => (i % 2 == 0 ? a : b).Add(Game("g" + i)));

        Assert.Equal(20, a.Load()!.Games.Count);
    }

    [Fact]
    public void RemovingAnAutoGameIgnoresItsExesAndKeepsOtherAutoKeys()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a", "auto"));
        Assert.Empty(editor.Remove("a")); // auto 객체가 없던 파일
        Assert.Equal(["a.exe"], editor.Load()!.Auto.IgnoreExes);
        Assert.Equal(AutoSettings.Default.Enabled, editor.Load()!.Auto.Enabled);

        editor.Add(Game("a", "auto")); // 이미 있으면 중복 없음 (대소문자 무시)
        editor.SetAuto(editor.Load()!.Auto with { IgnoreExes = ["A.EXE", "x.exe"], PrewarmRecent = 4 });
        editor.Remove("a");
        AutoSettings auto = editor.Load()!.Auto;
        Assert.Equal(["A.EXE", "x.exe"], auto.IgnoreExes);
        Assert.Equal(4, auto.PrewarmRecent);
    }

    [Fact]
    public void RemovingAManualGameLeavesIgnoreExesAlone()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a"));
        editor.Remove("a");
        Assert.Empty(editor.Load()!.Auto.IgnoreExes);
        Assert.DoesNotContain("ignore_exes", File.ReadAllText(editor.ConfigPath), StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoreExesAtTheLimitPushesOutTheOldestNames()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a", "auto"));
        editor.SetAuto(AutoSettings.Default with { IgnoreExes = Enumerable.Range(0, 64).Select(i => $"e{i}.exe").ToList() });

        IReadOnlyList<string> dropped = editor.Remove("a");

        Assert.Equal(["e0.exe"], dropped);
        IReadOnlyList<string> ignore = editor.Load()!.Auto.IgnoreExes;
        Assert.Equal((64, "e1.exe", "a.exe"), (ignore.Count, ignore[0], ignore[^1]));
    }

    [Fact]
    public void UpdateAutoKeepsValuesChangedInTheFileAndWritesOnce()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a"));
        editor.SetAuto(AutoSettings.Default with { IdleMinutes = 9, IgnoreExes = ["z.exe"] });

        editor.UpdateAuto(a => a with { RewarmMinutes = 3 }, 4096);

        AsterSettings s = editor.Load()!;
        Assert.Equal((9, 3, 4096, false), (s.Auto.IdleMinutes, s.Auto.RewarmMinutes, s.MaxZoneMib, s.MaxZoneAuto));
        Assert.Equal(["z.exe"], s.Auto.IgnoreExes);
        editor.UpdateAuto(a => a, maxZoneAuto: true);
        Assert.True(editor.Load()!.MaxZoneAuto);
    }
}
