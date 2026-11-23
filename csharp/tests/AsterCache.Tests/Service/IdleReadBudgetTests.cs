using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임 실행 계기 없이 도는 읽기 (재예열 점검, 보충 예열) 의 드라이브별 24시간 상한: 세는 값, 기본 상한 규칙, 상한에 닿았을 때 멈추는 것과 멈추지 않는 것, 최근 플레이 순서, 재시작.
public sealed class IdleReadBudgetTests : IDisposable
{
    private const long Mib = 1L << 20;
    private const long Gib = 1L << 30;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_idle_" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly CountingStreamer _streamer = new();
    private DateTimeOffset _now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private long _zoneMib = 100;
    private long _avail = 16000;
    private ProbeResult? _probe = new ProbeResult(8, 8, 120.0); // 식음
    private double? _disk = 0;

    public IdleReadBudgetTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string StatusPath => Path.Combine(_dir, "prewarm_status.json");

    private sealed class CountingStreamer : IGameStreamer
    {
        public List<GameSettings> Seen { get; } = [];

        public long ReadMib { get; set; } = 100;

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            Seen.Add(game);
            long mib = Math.Min(ReadMib, game.MaxStreamMib);
            return (new StreamStats(mib * Mib, 1, 0, 0.01, RequestedBytes: mib * Mib), !stop.IsCancellationRequested);
        }
    }

    private static readonly AutoSettings On = AutoSettings.Default with { Enabled = true, RewarmMinutes = 10, RewarmAfterExitMin = 3 };
    private static readonly AutoSettings NoRewarm = AutoSettings.Default with { Enabled = false };

    private Prewarmer Make(AutoSettings auto, params GameSettings[] games)
    {
        var p = new Prewarmer(games, _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = _ => false,
            ZoneBytes = g => Math.Min(_zoneMib, g.MaxStreamMib) * Mib,
            TraceExists = _ => true,
            DiskBytesPerSecond = _ => _disk,
            ProbeReadMs = _ => _probe,
        }, _log, StatusPath, () => _now);
        p.SetAuto(auto);
        return p;
    }

    private static GameSettings Game(string name = "g") => Games.Make(name, name + ".exe");

    // 30 초 간격 틱 n 번.
    private void Ticks(Prewarmer p, int n, AutoSettings auto)
    {
        for (int i = 0; i < n; i++)
        {
            p.Tick(auto);
            _now += TimeSpan.FromSeconds(30);
        }
    }

    private long Used(Prewarmer p, ReadPurpose purpose) => p.DiskReads.Used(@"D:\", purpose).Bytes;

    // 처음 예열하고 (계기 있는 읽기), 재예열 점검이 한 번 돌 때가 되게 만듦.
    private Prewarmer StartedAndDue(AutoSettings auto, params GameSettings[] games)
    {
        Prewarmer p = Make(auto, games);
        foreach (GameSettings g in games)
        {
            Assert.True(p.Run(g, "service start", ignoreCooldown: true).Ran);
        }

        Ticks(p, 1, auto); // 켜진 때부터 한 주기 뒤에 처음 점검
        _now += TimeSpan.FromMinutes(11);
        return p;
    }

    [Fact]
    public void AStreamStartedByTheServiceCountsAsTriggeredAndAnIdleRewarmCheckAsIdleByTheBytesRead()
    {
        Prewarmer p = StartedAndDue(On, Game());
        Assert.Equal((100 * Mib, 0), (Used(p, ReadPurpose.Trigger), Used(p, ReadPurpose.Idle)));

        Ticks(p, 1, On); // 표본이 식어 다시 예열

        Assert.Equal(2, _streamer.Seen.Count);
        Assert.Equal(100 * Mib, Used(p, ReadPurpose.Trigger)); // 시작 예열의 몫은 그대로
        Assert.Equal(100 * Mib + 8 * Mib, Used(p, ReadPurpose.Idle)); // 스트림이 읽은 100 MiB + 느린 표본 8 곳 x 1 MiB
    }

    [Fact]
    public void AWarmSampleCountsOnlyItsSlowSamplesAndAColdRewarmStreamCountsWhatItRead()
    {
        Prewarmer p = StartedAndDue(On, Game());
        _probe = new ProbeResult(8, 1, 0.4); // 따뜻함 (느린 표본 1 곳은 식음 기준 미만)

        Ticks(p, 1, On);

        Assert.Single(_streamer.Seen); // 재예열 없음
        Assert.Equal(Mib, Used(p, ReadPurpose.Idle)); // 디스크에서 읽힌 표본 1 곳뿐
    }

    [Theory]
    [InlineData(0, 100, 8 * Gib)] // 목록이 작으면 바닥 8 GiB
    [InlineData(3, 6000, 3 * Gib)] // 설정이 있으면 그 값
    public void TheDefaultLimitIsEightGibForSmallListsAndTheSettingOverridesIt(int setGib, long zoneMib, long expected)
    {
        _zoneMib = zoneMib;
        Prewarmer p = Make(On with { IdleReadGibPerDay = setGib }, Game());

        Assert.Equal(expected, p.IdleReadCap(Game()));
    }

    [Fact]
    public void TheDefaultLimitSumsTheListsOfTheGamesOnTheSameDriveOnly()
    {
        _zoneMib = 3000;
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        GameSettings other = Game("c") with { OriginRoot = @"E:\Games\c" };
        Prewarmer p = Make(On, a, b, other);

        Assert.Equal(12000 * Mib, p.IdleReadCap(a)); // D: 두 게임 3000 + 3000 의 2 배
        Assert.Equal(8 * Gib, p.IdleReadCap(other)); // E: 은 한 게임 3000 의 2 배가 8 GiB 미만
    }

    [Fact]
    public void AtTheLimitTheIdleCheckStopsAndLogsOnceWhileAReadStartedByAGameStillRunsAndTheLimitIsFreedADayLater()
    {
        GameSettings g = Game();
        Prewarmer p = StartedAndDue(On, g);
        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 9 * Gib); // 상한 8 GiB 를 넘음
        int before = _streamer.Seen.Count;

        Ticks(p, 1, On);
        _now += TimeSpan.FromMinutes(11);
        Ticks(p, 1, On);

        Assert.Equal(before, _streamer.Seen.Count); // 계기 없는 재예열은 쉼
        Assert.Single(_log.Lines, line => line.Contains("idle disk reads of drive D: paused", StringComparison.Ordinal) && line.Contains("reads started by a game or launcher are not limited", StringComparison.Ordinal));

        Assert.True(p.Run(g, "trigger g.exe", ignoreCooldown: true).Ran); // 게임·런처 시작 예열은 상한과 무관
        Assert.Equal(before + 1, _streamer.Seen.Count);
        Assert.Equal(100 * Mib * 2, Used(p, ReadPurpose.Trigger)); // 계기 있는 읽기는 세기만 함

        _now += TimeSpan.FromHours(25);
        Ticks(p, 1, On);

        Assert.Equal(before + 2, _streamer.Seen.Count); // 하루 뒤 창에서 빠져 다시 읽음
        Assert.Contains(_log.Lines, line => line.Contains("idle disk reads of drive D: resumed", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCheckAfterAGameExitReadsEvenAtTheLimitAndTheNextRegularCheckIsHeldBack()
    {
        GameSettings g = Game();
        Prewarmer p = StartedAndDue(On, g);
        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 9 * Gib);
        int before = _streamer.Seen.Count;

        p.NoteGameExit(); // 게임이 끝남: 점검을 앞당김
        _now += TimeSpan.FromMinutes(4);
        Ticks(p, 1, On);

        Assert.Equal(before + 1, _streamer.Seen.Count); // 게임 종료 뒤 한 번의 다시 채우기는 상한 밖
        Assert.Equal(9 * Gib, Used(p, ReadPurpose.Idle)); // 그 읽기는 계기 있는 읽기로 세어 계기 없는 몫을 늘리지 않음
        Assert.Equal(100 * Mib + 8 * Mib + 100 * Mib, Used(p, ReadPurpose.Trigger)); // 시작 예열 + 느린 표본 8 곳 + 게임 종료 뒤 다시 읽기

        _now += TimeSpan.FromMinutes(11);
        Ticks(p, 1, On);

        Assert.Equal(before + 1, _streamer.Seen.Count); // 그다음 주기 점검은 다시 상한에 막힘
    }

    [Fact]
    public void TheSupplementTickPausesAtTheLimitAndContinuesOnceTheWindowHasPassed()
    {
        GameSettings g = Game();
        Prewarmer p = Make(NoRewarm with { StartupWarmMaxMib = 40 }, g);
        Assert.True(Assert.Single(p.RunServiceStart()).Ran);
        Assert.True(p.SupplementPending("g"));
        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 9 * Gib);

        Ticks(p, 6, NoRewarm);

        Assert.Single(_streamer.Seen); // 한가해도 계기 없는 보충은 쉼
        Assert.True(p.SupplementPending("g"));

        _now += TimeSpan.FromHours(25);
        Ticks(p, 6, NoRewarm);

        Assert.Equal(2, _streamer.Seen.Count); // 하루가 지나 다시 이어 읽음
        Assert.False(p.SupplementPending("g"));
        Assert.True(Used(p, ReadPurpose.Idle) >= 60 * Mib); // 보충이 읽은 나머지 (100 - 40 MiB) 는 계기 없는 읽기로 셈
    }

    [Fact]
    public void ASupplementReadByTheServiceStartPathIsNotLimited()
    {
        GameSettings g = Game();
        Prewarmer p = Make(NoRewarm with { StartupWarmMaxMib = 40 }, g);
        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 9 * Gib); // 상한을 넘은 채로 시작

        Assert.True(Assert.Single(p.RunServiceStart()).Ran); // 서비스 시작 예열은 상한 밖

        Assert.Single(_streamer.Seen);
        Assert.Equal(40 * Mib, Used(p, ReadPurpose.Trigger));
    }

    [Fact]
    public void TheBudgetIsSpentOnTheMostRecentlyPlayedGameFirst()
    {
        _zoneMib = 600;
        _streamer.ReadMib = 600;
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        GameSettings c = Game("c");
        AutoSettings auto = On with { IdleReadGibPerDay = 1 }; // 1 GiB: 두 번째 재예열에서 넘음
        Prewarmer p = StartedAndDue(auto, a, b, c);
        DateTimeOffset played = _now;
        p.LastPlayedAt = name => name switch { "c" => played, "a" => played - TimeSpan.FromHours(1), "b" => played - TimeSpan.FromHours(2), _ => null };
        int before = _streamer.Seen.Count;

        Ticks(p, 1, auto);

        Assert.Equal(["c", "a"], _streamer.Seen.Skip(before).Select(g => g.Name)); // 가장 오래전에 플레이한 b 는 상한에 막힘
        Assert.True(Used(p, ReadPurpose.Idle) >= 1 * Gib);
    }

    [Fact]
    public void TheIdleReadsOfTheLastDaySurviveARestartAndAClockSetBackDoesNotFreeThem()
    {
        GameSettings g = Game();
        Prewarmer first = Make(On, g);
        first.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 300 * Mib); // 64 MiB 이상이라 바로 상태 파일에 씀

        Assert.Contains("disk_reads", File.ReadAllText(StatusPath), StringComparison.Ordinal);
        Prewarmer second = Make(On, g);
        Assert.Equal(300 * Mib, Used(second, ReadPurpose.Idle));

        _now -= TimeSpan.FromHours(10);
        Assert.Equal(300 * Mib, Used(second, ReadPurpose.Idle));
        _now += TimeSpan.FromHours(35);
        Assert.Equal(0, Used(second, ReadPurpose.Idle));
    }

    [Fact]
    public void TheStatusFileKeepsTheGameEntriesNextToTheReadRecord()
    {
        GameSettings g = Game();
        Prewarmer p = Make(On, g);
        Assert.True(p.Run(g, "service start", ignoreCooldown: true).Ran);

        JObject root = (JObject)JsonRead.ReadFile(StatusPath)!;

        Assert.True(((JObject)root["games"]!).ContainsKey("g"));
        Assert.True(root.ContainsKey("disk_reads"));
    }

    [Fact]
    public void OneLineAboutTheLastDayIsLoggedOncePerDay()
    {
        GameSettings g = Game();
        Prewarmer p = StartedAndDue(On, g); // 첫 틱에서 시작 예열이 읽은 양으로 한 줄
        Ticks(p, 1, On);

        string[] lines = [.. _log.Lines.Where(line => line.Contains("disk reads in the last 24 h:", StringComparison.Ordinal))];
        Assert.Single(lines);
        Assert.Contains("D: trigger reads 100.00 MiB, idle reads 0.00 MiB of 8192.00 MiB limit, recovery reads 0.00 MiB in 0 samples", lines[0], StringComparison.Ordinal);

        Ticks(p, 4, On);
        Assert.Single(_log.Lines, line => line.Contains("disk reads in the last 24 h:", StringComparison.Ordinal));

        _now += TimeSpan.FromHours(25);
        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Trigger, Mib);
        Ticks(p, 1, On);
        Assert.Equal(2, _log.Lines.Count(line => line.Contains("disk reads in the last 24 h:", StringComparison.Ordinal)));
    }
}
