using System.Text.Json.Nodes;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// HDD 깨워 두기 (KeepAwake): 조건 조합, 10 분 주기, powercfg 출력 해석. 실제 디스크·powercfg 는 쓰지 않음.
public sealed class KeepAwakeTests
{
    // 2026-11-03 은 화요일.
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 20, 0, 0, TimeSpan.FromHours(9));

    private readonly ListLog _log = new();
    private readonly List<string> _reads = [];
    private DateTimeOffset _now = T0;

    private sealed record Case(bool Hdd, bool Launcher, bool Habit, bool Power, int? IdleSeconds);

    private static GameSettings Game() => Games.Make("g", "g.exe") with { Prewarm = PrewarmSettings.Default with { TriggerExes = ["steam.exe"] } };

    private KeepAwake Make(Case c, AutoSettings? auto = null, Action<string>? read = null)
    {
        var env = new KeepAwakeEnv
        {
            Now = () => _now,
            IsHdd = _ => c.Hdd,
            AnyRunning = exes => c.Launcher && exes.Contains("steam.exe"),
            PowerOk = () => c.Power,
            Plays = () => c.Habit
                ? new Dictionary<string, IReadOnlyList<DateTimeOffset>> { ["g"] = [T0.AddDays(-7).AddMinutes(30), T0.AddDays(-14)] }
                : new Dictionary<string, IReadOnlyList<DateTimeOffset>>(),
            PickFile = g => $@"D:\Games\{g.Name}\a.bin",
            ReadSector = read ?? (p => _reads.Add(p)),
            DiskIdleSeconds = () => c.IdleSeconds,
        };
        return new KeepAwake(env, _log, () => new AsterSettings(50, [Game()]) { Auto = auto ?? AutoSettings.Default });
    }

    [Theory]
    [InlineData(true, true, false, true, 600, true)]   // HDD + 런처
    [InlineData(true, false, true, true, 600, true)]   // HDD + 습관
    [InlineData(true, true, true, true, null, true)]   // powercfg 를 읽지 못해도 켜 둠
    [InlineData(true, false, false, true, 600, false)] // 둘 다 아님
    [InlineData(false, true, true, true, 600, false)]  // SSD
    [InlineData(true, true, true, false, 600, false)]  // 배터리
    [InlineData(true, true, true, true, 0, false)]     // 디스크 끄기 안 함
    public void ReadsOnlyWhenHddAndLauncherOrHabitAndPowerAndTimeout(bool hdd, bool launcher, bool habit, bool power, int? idle, bool expected)
    {
        KeepAwake k = Make(new Case(hdd, launcher, habit, power, idle));

        k.Tick();

        Assert.Equal(expected ? 1 : 0, _reads.Count);
        Assert.Equal(expected, _log.Lines.Any(l => l.Contains("keep-awake started: D:", StringComparison.Ordinal)));
    }

    [Fact]
    public void TimeoutZeroLogsOnceAndStaysOff()
    {
        KeepAwake k = Make(new Case(true, true, true, true, 0));

        k.Tick();
        _now += KeepAwake.Every;
        k.Tick();

        Assert.Empty(_reads);
        Assert.Single(_log.Lines, l => l.Contains("disk idle timeout off, keep-awake not needed", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadsEveryTenMinutesNotSooner()
    {
        KeepAwake k = Make(new Case(true, true, false, true, 600));

        k.Tick();
        _now += TimeSpan.FromMinutes(9);
        k.Tick();
        Assert.Single(_reads);
        _now += TimeSpan.FromMinutes(1);
        k.Tick();
        Assert.Equal(2, _reads.Count);
        _now += TimeSpan.FromMinutes(10);
        k.Tick();

        Assert.Equal(3, _reads.Count);
        Assert.Single(_log.Lines, l => l.Contains("keep-awake started", StringComparison.Ordinal)); // 계속 켜져 있으면 로그는 한 번
    }

    [Fact]
    public void StopLogsOnceWhenKeepingAwake()
    {
        KeepAwake k = Make(new Case(true, true, false, true, 600));
        k.Tick();

        k.Stop();
        k.Stop();

        Assert.Single(_log.Lines, l => l.Contains("keep-awake stopped: D:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void SettingOffOrAutoDisabledNeverReads(bool autoEnabled, bool keepAwake)
    {
        KeepAwake k = Make(new Case(true, true, true, true, 600), AutoSettings.Default with { Enabled = autoEnabled, KeepAwake = keepAwake });

        k.Tick();

        Assert.Empty(_reads);
    }

    [Fact]
    public void ReadFailureIsSwallowedAndNotLoggedAsStarted()
    {
        KeepAwake k = Make(new Case(true, true, false, true, 600), read: _ => throw new IOException("busy"));

        k.Tick();

        Assert.DoesNotContain(_log.Lines, l => l.Contains("keep-awake", StringComparison.Ordinal));
    }

    [Fact]
    public void HabitNeedsSameWeekdayAndHourWindow()
    {
        Assert.True(KeepAwake.PlayedAround([T0.AddDays(-7).AddMinutes(119)], T0));
        Assert.False(KeepAwake.PlayedAround([T0.AddDays(-7).AddMinutes(121)], T0)); // 시간대 밖
        Assert.False(KeepAwake.PlayedAround([T0.AddDays(-1)], T0)); // 다른 요일
        Assert.False(KeepAwake.PlayedAround([], T0));
    }

    [Theory]
    [InlineData("    Current AC Power Setting Index: 0x00000708\r\n    Current DC Power Setting Index: 0x000002d0\r\n", 1800)]
    [InlineData("  Possible Settings units: Seconds\n  현재 AC 전원 설정 인덱스: 0x00000000\n  현재 DC 전원 설정 인덱스: 0x00000258\n", 0)]
    [InlineData("Minimum Possible Setting: 0x00000000\nMaximum Possible Setting: 0xffffffff\nCurrent AC Power Setting Index: 0x0000003c\nCurrent DC Power Setting Index: 0x0000003c", 60)]
    public void ParsesAcIndexFromPowercfgOutput(string output, int expected) => Assert.Equal(expected, KeepAwake.ParseAcIdleSeconds(output));

    [Theory]
    [InlineData("")]
    [InlineData("powercfg: access denied")]
    [InlineData("Current AC Power Setting Index: 0x00000708")]
    public void UnparseablePowercfgOutputIsNull(string output) => Assert.Null(KeepAwake.ParseAcIdleSeconds(output));

    [Fact]
    public void KeepAwakeSettingDefaultsOnAndParses()
    {
        static AutoSettings Parse(JsonObject auto) => SettingsStore.Parse(new JsonObject { ["games"] = new JsonArray(), ["auto"] = auto }.ToJsonString(), @"C:\p\plans").Auto;

        Assert.True(AutoSettings.Default.KeepAwake);
        Assert.True(Parse([]).KeepAwake);
        Assert.False(Parse(new JsonObject { ["keep_awake"] = false }).KeepAwake);
        Assert.Throws<SettingsException>(() => Parse(new JsonObject { ["keep_awake"] = "no" }));
    }
}
