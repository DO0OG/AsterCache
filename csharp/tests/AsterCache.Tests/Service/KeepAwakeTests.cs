using System.Text.Json.Nodes;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// HDD 깨워 두기 (KeepAwake): 조건 조합, 디스크 끄기 시간에 맞춘 주기, 1 시간 재조회, 파일 선택, powercfg 출력 해석. 실제 디스크·powercfg 는 쓰지 않음.
public sealed class KeepAwakeTests
{
    // 2026-11-03 은 화요일.
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 20, 0, 0, TimeSpan.FromHours(9));

    private readonly ListLog _log = new();
    private readonly List<string> _reads = [];
    private DateTimeOffset _now = T0;
    private int? _idleOverride; // 설정하면 DiskIdleSeconds 가 Case 의 값 대신 이 값을 돌려줌 (전원 설정이 바뀐 것을 흉내)
    private int _idleCalls;

    private sealed record Case(bool? Hdd, bool Launcher, bool Habit, bool Power, int? IdleSeconds);

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
            DiskIdleSeconds = () =>
            {
                _idleCalls++;
                return _idleOverride ?? c.IdleSeconds;
            },
        };
        return new KeepAwake(env, _log, () => new AsterSettings(50, [Game()]) { Auto = auto ?? AutoSettings.Default });
    }

    [Theory]
    [InlineData(true, true, false, true, 600, true)]   // HDD + 런처
    [InlineData(true, false, true, true, 600, true)]   // HDD + 습관
    [InlineData(true, true, true, true, null, false)]  // powercfg 를 읽지 못하면 쉼
    [InlineData(true, true, true, true, 119, false)]   // 끄기 시간 2 분 미만이면 쉼
    [InlineData(true, true, true, true, 120, true)]
    [InlineData(true, false, false, true, 600, false)] // 둘 다 아님
    [InlineData(false, true, true, true, 600, false)]  // SSD
    [InlineData(null, true, true, true, 600, false)]   // 종류 미확인
    [InlineData(true, true, true, false, 600, false)]  // 배터리
    [InlineData(true, true, true, true, 0, false)]     // 디스크 끄기 안 함
    public void ReadsOnlyWhenHddAndLauncherOrHabitAndPowerAndTimeout(bool? hdd, bool launcher, bool habit, bool power, int? idle, bool expected)
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
        KeepAwake k = Make(new Case(true, true, false, true, 1800)); // 0.6 x 30 분 = 18 분 > 10 분 상한

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

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(119, null)]
    [InlineData(120, 72.0)]
    [InlineData(300, 180.0)]
    [InlineData(999, 599.4)]
    [InlineData(1000, 600.0)]
    [InlineData(3600, 600.0)]
    public void IntervalIsSixtyPercentOfTheDiskTimeoutCappedAtTenMinutes(int? idle, double? seconds)
    {
        TimeSpan? interval = KeepAwake.Interval(idle);

        Assert.Equal(seconds is null, interval is null);
        if (seconds is { } expected)
        {
            Assert.Equal(expected, interval!.Value.TotalSeconds, 3);
        }
    }

    [Fact]
    public void ShortTimeoutReadsMoreOftenThanTenMinutes()
    {
        KeepAwake k = Make(new Case(true, true, false, true, 300)); // 3 분마다

        k.Tick();
        _now += TimeSpan.FromMinutes(2);
        k.Tick();
        Assert.Single(_reads);
        _now += TimeSpan.FromMinutes(1);
        k.Tick();

        Assert.Equal(2, _reads.Count);
    }

    [Fact]
    public void UnknownDriveKindIsSkippedAndLoggedOnce()
    {
        KeepAwake k = Make(new Case(null, true, true, true, 600));

        k.Tick();
        _now += TimeSpan.FromMinutes(10);
        k.Tick();

        Assert.Empty(_reads);
        Assert.Single(_log.Lines, l => l.Contains("keep-awake skipped: D: drive type unknown", StringComparison.Ordinal));
    }

    [Fact]
    public void PowerSettingIsReReadEveryHourOnly()
    {
        KeepAwake k = Make(new Case(true, true, false, true, 1800));

        for (int i = 0; i < 6; i++) // 0..50 분: 10 분마다
        {
            k.Tick();
            _now += TimeSpan.FromMinutes(10);
        }

        Assert.Equal(1, _idleCalls);
        _idleOverride = 300; // 1 시간 뒤 설정이 3 분으로 바뀜
        k.Tick();
        Assert.Equal(2, _idleCalls);
        int before = _reads.Count;
        _now += TimeSpan.FromMinutes(3);
        k.Tick();

        Assert.Equal(before + 1, _reads.Count); // 새 주기 (3 분) 를 따름
        Assert.Equal(2, _idleCalls);
    }

    [Fact]
    public void PowerSettingChangedToOffPausesAfterTheHourlyReRead()
    {
        KeepAwake k = Make(new Case(true, true, false, true, 1800));
        k.Tick();
        _idleOverride = 0;
        _now += TimeSpan.FromHours(1);

        k.Tick();

        Assert.Single(_reads);
        Assert.Contains(_log.Lines, l => l.Contains("keep-awake stopped: D:", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("disk idle timeout off", StringComparison.Ordinal));
    }

    [Fact]
    public void FirstFilePicksAFileOfAtLeastOneMibPreferringTheTopFolder()
    {
        using var dir = new TempDir();
        dir.WriteRandom("tiny.bin", 4096);
        Assert.Null(KeepAwake.FirstFile(dir.Path)); // 큰 파일이 없으면 읽지 않음

        Directory.CreateDirectory(dir.Combine("sub"));
        dir.WriteRandom(Path.Combine("sub", "deep.bin"), 1 << 20);
        Assert.Equal(dir.Combine(Path.Combine("sub", "deep.bin")), KeepAwake.FirstFile(dir.Path));

        dir.WriteRandom("top.bin", (1 << 20) + 1);
        Assert.Equal(dir.Combine("top.bin"), KeepAwake.FirstFile(dir.Path));
        Assert.Null(KeepAwake.FirstFile(dir.Combine("missing")));
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
