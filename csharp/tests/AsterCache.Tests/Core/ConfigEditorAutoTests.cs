using System.Text.Json.Nodes;
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

        Assert.Equal(19, CountKeys(File.ReadAllText(editor.ConfigPath), "auto"));
        Assert.Equal(Plain(AutoSettings.Default), Plain(editor.Load()!.Auto));

        var custom = new AutoSettings(false, false, true, false, 3, 7, 90, 128, 5, ["a.exe", "b.exe"])
        {
            IdleSeconds = 45,
            Learn = new LearnSettings(false, 256, 1024) { Mode = LearnSettings.AdaptiveMode, SaturationMbps = 120 },
            SharedProfiles = new SharedProfilesSettings(false, "https://example.com/profiles.zip"),
            FullPrewarm = false,
            FullPrewarmMaxMib = 2048,
            EngineProfiles = false,
            PostUpdateDelayMin = 25,
        };
        editor.SetAuto(custom);
        AutoSettings read = editor.Load()!.Auto;
        Assert.Equal(Plain(custom), Plain(read));
        Assert.Equal(45, read.IdleSeconds);
        Assert.Equal(["a.exe", "b.exe"], read.IgnoreExes);
        Assert.Equal((false, 2048, false), (read.FullPrewarm, read.FullPrewarmMaxMib, read.EngineProfiles));
        Assert.Equal((LearnSettings.AdaptiveMode, false, "https://example.com/profiles.zip"), (read.Learn.Mode, read.SharedProfiles.Enabled, read.SharedProfiles.Url));
        Assert.Equal(120, read.Learn.SaturationMbps);
        Assert.Equal(25, read.PostUpdateDelayMin);

        editor.SetAuto(AutoSettings.Default with { RewarmMinutes = 0 });
        Assert.Equal(0, editor.Load()!.Auto.RewarmMinutes);
    }

    [Fact]
    public void ReplaceExeNamesSwapsTheListKeepsTheRestAndRejectsDuplicatesAcrossGames()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a", "auto"));
        editor.Add(Game("b"));

        editor.ReplaceExeNames("a", ["real.exe", "extra.exe"]);

        GameSettings a = editor.Load()!.Games.Single(g => g.Name == "a");
        Assert.Equal(["real.exe", "extra.exe"], a.ExeNames);
        Assert.Equal("auto", a.Source);
        Assert.Throws<SettingsException>(() => editor.ReplaceExeNames("a", ["b.exe"])); // 다른 게임이 쓰는 이름
        Assert.Equal(["real.exe", "extra.exe"], editor.Load()!.Games.Single(g => g.Name == "a").ExeNames); // 거부되면 파일은 그대로
        Assert.Throws<SettingsException>(() => editor.ReplaceExeNames("nope", ["x.exe"]));
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

    // 파싱 때만 정해지는 명시 여부 (idle_seconds·idle_minutes 키)는 비교에서 뺌.
    private static AutoSettings Plain(AutoSettings a) => a with { IgnoreExes = [], IdleSecondsSet = false, IdleMinutesSet = false };

    // 최상위 객체 obj 의 키 개수.
    private static int CountKeys(string json, string obj) =>
        ((JObject)((JObject)JsonRead.Parse(json)!)[obj]!).Count;

    [Fact]
    public void IdleMinutesOnlyFileKeepsItsWindowAcrossWriteAuto()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a"));
        JsonObject root = JsonNode.Parse(File.ReadAllText(editor.ConfigPath))!.AsObject();
        root["auto"] = new JsonObject { ["idle_minutes"] = 10 };
        File.WriteAllText(editor.ConfigPath, root.ToJsonString());

        editor.UpdateAuto(a => a with { Relocate = false });

        AsterSettings s = editor.Load()!;
        Assert.Equal((TimeSpan.FromMinutes(10), false), (s.Auto.IdleWindow, s.Auto.Relocate));
        Assert.DoesNotContain("idle_seconds", File.ReadAllText(editor.ConfigPath), StringComparison.Ordinal);
    }

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

    private GameInput GameAt(string name, string root, string? source = null) => Game(name, source) with { OriginRoot = root };

    [Theory]
    [InlineData("Games/a")] // 같은 폴더
    [InlineData("GAMES/A/")] // 대소문자·끝 구분자 무시
    [InlineData("Games/a/sub")] // 안쪽
    [InlineData("Games/a/../a/sub")] // 정규화
    [InlineData("Games")] // 바깥
    public void AddAutoRejectsAnOverlappingOriginRootInsideTheLockAndKeepsTheFile(string relative)
    {
        var editor = new ConfigEditor(_dir);
        editor.AddAuto(Game("a"), new Dictionary<string, DateTimeOffset>());
        byte[] before = File.ReadAllBytes(editor.ConfigPath);

        SettingsException e = Assert.Throws<SettingsException>(() => editor.AddAuto(GameAt("b", Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar)), "auto"), new Dictionary<string, DateTimeOffset>()));

        Assert.Contains("overlaps the registered game a", e.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllBytes(editor.ConfigPath));
    }

    [Fact]
    public void OverlapIsRejectedBeforeAnythingIsEvictedAndSiblingFoldersPass()
    {
        ConfigEditor editor = Full(0, 32);
        byte[] before = File.ReadAllBytes(editor.ConfigPath);

        Assert.Throws<SettingsException>(() => editor.AddAuto(GameAt("new", Path.Combine(_dir, "Games", "a5", "sub")), new Dictionary<string, DateTimeOffset>()));
        Assert.Equal(before, File.ReadAllBytes(editor.ConfigPath)); // 가득 찬 상태에서도 아무것도 밀어내지 않음

        Assert.Equal("a0", editor.AddAuto(Game("a50"), new Dictionary<string, DateTimeOffset>())); // 이웃 폴더 (a5 와 a50) 는 겹치지 않음
    }

    [Fact]
    public void AddWithAutoSourceIsCheckedLikeAddAutoButManualAddKeepsItsOldBehavior()
    {
        var editor = new ConfigEditor(_dir);
        editor.Add(Game("a"));

        Assert.Throws<SettingsException>(() => editor.Add(GameAt("b", Path.Combine(_dir, "Games", "a", "sub"), "auto")));
        editor.Add(GameAt("c", Path.Combine(_dir, "Games", "a", "sub"))); // 수동 등록은 기존처럼 막지 않음
        editor.Add(GameAt("d", Path.Combine(_dir, "Games"), "manual"));

        Assert.Equal(["a", "c", "d"], editor.Load()!.Games.Select(g => g.Name));
    }

    [Fact]
    public void TwoOverlappingAutoRegistrationsAtOnceLetExactlyOneIn()
    {
        var a = new ConfigEditor(_dir);
        var b = new ConfigEditor(_dir);
        int accepted = 0;

        Parallel.Invoke(
            () => Interlocked.Add(ref accepted, Try(a, GameAt("x", Path.Combine(_dir, "Games", "x")))),
            () => Interlocked.Add(ref accepted, Try(b, GameAt("y", Path.Combine(_dir, "Games", "x", "inner")))));

        Assert.Equal(1, accepted);
        Assert.Single(a.Load()!.Games);

        static int Try(ConfigEditor editor, GameInput g)
        {
            try
            {
                editor.AddAuto(g, new Dictionary<string, DateTimeOffset>());
                return 1;
            }
            catch (SettingsException)
            {
                return 0;
            }
        }
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
        DateTime clock = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc); // 가짜 시계: 대기는 시계를 앞으로 보낼 뿐 실제로 자지 않음
        var waits = new List<int>();
        var editor = new ConfigEditor(_dir, 300, ms =>
        {
            waits.Add(ms);
            clock = clock.AddMilliseconds(ms);
        }, () => clock);
        editor.Add(Game("a"));
        byte[] before = File.ReadAllBytes(editor.ConfigPath);

        using (new FileStream(editor.ConfigPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => editor.Add(Game("b")));
            Assert.Equal([100, 100, 100], waits); // 300 ms 한도를 100 ms 간격으로 채우고 포기
        }

        Assert.Equal(before, File.ReadAllBytes(editor.ConfigPath));
        editor.Add(Game("b")); // 풀린 뒤에는 정상
        Assert.Equal(2, editor.Load()!.Games.Count);
    }

    [Fact]
    public void LockReleasedWhileWaitingLetsTheEditThrough()
    {
        FileStream? held = null;
        var editor = new ConfigEditor(_dir, 5000, _ =>
        {
            held?.Dispose(); // 첫 대기 중에 잠금이 풀림
            held = null;
        });
        held = new FileStream(editor.ConfigPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        editor.Add(Game("a"));

        Assert.Null(held);
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
    public void MinusOneAndMigratedSettingsRoundTripInTheNewFormat()
    {
        var editor = new ConfigEditor(_dir);
        editor.SetAuto(AutoSettings.Default with { PrewarmRecent = -1 });
        Assert.Equal(-1, editor.Load()!.Auto.PrewarmRecent);

        File.WriteAllText(editor.ConfigPath, """{"games":[],"auto":{"prewarm_recent":1,"idle_minutes":3}}""");
        AutoSettings old = editor.Load()!.Auto;
        Assert.True(old.Migrated);
        editor.SetAuto(old);
        AutoSettings now = editor.Load()!.Auto;
        Assert.False(now.Migrated);
        Assert.Equal((0, TimeSpan.FromSeconds(30)), (now.PrewarmRecent, now.IdleWindow));
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
