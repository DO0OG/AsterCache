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
    private Action? _onProbe; // 표본 점검이 도는 동안 부를 것

    public IdleReadBudgetTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private string StatusPath => Path.Combine(_dir, "prewarm_status.json");

    private sealed class CountingStreamer : IGameStreamer
    {
        public List<GameSettings> Seen { get; } = [];

        public long ReadMib { get; set; } = 100;

        public double Seconds { get; set; } = 10; // 읽는 데 걸린 시간 (100 MiB 가 10 s: 디스크 속도로 읽음, 아주 짧으면 메모리에 있던 것)

        public long RefreshMib { get; set; } // 스트림 끝의 앞부분 다시 읽기 (1 s 에 읽음)

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            Seen.Add(game);
            long mib = Math.Min(ReadMib, game.MaxStreamMib);
            return (new StreamStats(mib * Mib, 1, 0, Seconds, RequestedBytes: mib * Mib) { RefreshBytes = RefreshMib * Mib, RefreshSeconds = 1 }, !stop.IsCancellationRequested);
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
            ProbeReadMs = _ =>
            {
                _onProbe?.Invoke();
                return _probe;
            },
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
    [InlineData(0, 100, 24 * Gib)] // 목록이 작으면 바닥 24 GiB
    [InlineData(3, 6000, 3 * Gib)] // 설정이 있으면 그 값
    public void TheDefaultLimitIsTwentyFourGibForSmallListsAndTheSettingOverridesIt(int setGib, long zoneMib, long expected)
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

        Assert.Equal(36000 * Mib, p.IdleReadCap(a)); // D: 두 게임 3000 + 3000 의 6 배
        Assert.Equal(24 * Gib, p.IdleReadCap(other)); // E: 은 한 게임 3000 의 6 배가 24 GiB 미만
    }

    [Fact]
    public void AtTheLimitTheIdleCheckStopsAndLogsOnceWhileAReadStartedByAGameStillRunsAndTheLimitIsFreedADayLater()
    {
        GameSettings g = Game();
        Prewarmer p = StartedAndDue(On, g);
        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 25 * Gib, g.Name); // 상한 24 GiB 를 넘음
        int before = _streamer.Seen.Count;

        Ticks(p, 1, On);
        _now += TimeSpan.FromMinutes(11);
        Ticks(p, 1, On);

        Assert.Equal(before, _streamer.Seen.Count); // 계기 없는 재예열은 쉼
        Assert.Single(_log.Lines, line => line.Contains("idle disk reads of g on drive D: paused", StringComparison.Ordinal) && line.Contains("reads started by a game or launcher are not limited", StringComparison.Ordinal));

        Assert.True(p.Run(g, "trigger g.exe", ignoreCooldown: true).Ran); // 게임·런처 시작 예열은 상한과 무관
        Assert.Equal(before + 1, _streamer.Seen.Count);
        Assert.Equal(100 * Mib * 2, Used(p, ReadPurpose.Trigger)); // 계기 있는 읽기는 세기만 함

        _now += TimeSpan.FromHours(25);
        Ticks(p, 1, On);

        Assert.Equal(before + 2, _streamer.Seen.Count); // 하루 뒤 창에서 빠져 다시 읽음
        Assert.Contains(_log.Lines, line => line.Contains("idle disk reads of g on drive D: resumed", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCheckAfterAGameExitStopsAtTheLimitLikeTheRegularChecksAndReadsAgainOnceTheWindowHasPassed()
    {
        GameSettings g = Game();
        Prewarmer p = StartedAndDue(On, g);
        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 25 * Gib, g.Name);
        int before = _streamer.Seen.Count;

        p.NoteGameExit(); // 게임이 끝남: 점검을 앞당김
        _now += TimeSpan.FromMinutes(4);
        Ticks(p, 1, On);

        Assert.Equal(before, _streamer.Seen.Count); // 게임 종료 뒤 점검도 계기 없는 읽기의 하루 상한에 막힘
        Assert.Equal(25 * Gib, Used(p, ReadPurpose.Idle));
        Assert.Equal(100 * Mib, Used(p, ReadPurpose.Trigger)); // 시작 예열뿐: 표본도 읽지 않음

        _now += TimeSpan.FromHours(25);
        Ticks(p, 1, On);

        Assert.Equal(before + 1, _streamer.Seen.Count); // 하루 뒤 창에서 빠지면 읽음
    }

    [Fact]
    public void TheCheckAfterAGameExitCountsWhatItReadsAsIdleReads()
    {
        GameSettings g = Game();
        Prewarmer p = StartedAndDue(On, g);

        p.NoteGameExit();
        _now += TimeSpan.FromMinutes(4);
        Ticks(p, 1, On);

        Assert.Equal(2, _streamer.Seen.Count);
        Assert.Equal(100 * Mib, Used(p, ReadPurpose.Trigger)); // 시작 예열의 몫은 그대로
        Assert.Equal(100 * Mib + 8 * Mib, Used(p, ReadPurpose.Idle)); // 종료 뒤 점검의 스트림 100 MiB + 느린 표본 8 곳 x 1 MiB
    }

    [Fact]
    public void TheSupplementTickPausesAtTheLimitAndContinuesOnceTheWindowHasPassed()
    {
        GameSettings g = Game();
        Prewarmer p = Make(NoRewarm with { StartupWarmMaxMib = 40 }, g);
        Assert.True(Assert.Single(p.RunServiceStart()).Ran);
        Assert.True(p.SupplementPending("g"));
        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 25 * Gib, g.Name);

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
        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 25 * Gib, g.Name); // 상한을 넘은 채로 시작

        Assert.True(Assert.Single(p.RunServiceStart()).Ran); // 서비스 시작 예열은 상한 밖

        Assert.Single(_streamer.Seen);
        Assert.Equal(40 * Mib + 8 * Mib, Used(p, ReadPurpose.Trigger)); // 스트림 40 MiB + 시작 전 캐시 확인의 느린 표본 8 곳
    }

    [Fact]
    public void TheChecksGoThroughTheMostRecentlyPlayedGameFirst()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        GameSettings c = Game("c");
        Prewarmer p = StartedAndDue(On, a, b, c);
        DateTimeOffset played = _now;
        p.LastPlayedAt = name => name switch { "c" => played, "a" => played - TimeSpan.FromHours(1), "b" => played - TimeSpan.FromHours(2), _ => null };
        int before = _streamer.Seen.Count;

        Ticks(p, 1, On);

        Assert.Equal(["c", "a", "b"], _streamer.Seen.Skip(before).Select(g => g.Name));
    }

    [Fact]
    public void OneGameReadingOverAndOverCannotUseTheShareOfAnotherGameAndTheDriveTotalIsCheckedToo()
    {
        _zoneMib = 600;
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        AutoSettings auto = On with { IdleReadGibPerDay = 1 }; // 1 GiB 를 두 목록 크기에 비례해 나눔: 게임마다 512 MiB
        Prewarmer p = Make(auto, a, b);

        Assert.Equal(512 * Mib, p.IdleReadShare(a));
        for (int i = 0; i < 2; i++)
        {
            p.DiskReads.Add(a.OriginRoot, ReadPurpose.Idle, 400 * Mib, a.Name);
            Assert.Equal(i >= 1, p.IdleReadsExhausted(a)); // 400 MiB 는 몫 안, 800 MiB 부터 a 의 몫을 넘음
        }

        Assert.False(p.IdleReadsExhausted(b)); // b 의 몫은 남음
        Assert.Equal(224 * Mib, p.IdleReadRemaining(b)); // 단 드라이브 상한 (1024 MiB) 에 남은 양까지만
        using (DiskReadLedger.Scope(ReadPurpose.Idle))
        {
            Assert.Equal(224L, p.IdleReadRoom(b, null)); // 읽기 시작 전에 남은 양이 그 읽기의 예산 상한이 됨
        }


        p.DiskReads.Add(b.OriginRoot, ReadPurpose.Idle, 300 * Mib, b.Name); // 드라이브 합계 1100 MiB: b 의 몫 (512) 은 남았어도 드라이브 상한을 넘음
        Assert.True(p.IdleReadsExhausted(b));
        Assert.Contains(_log.Lines, line => line.Contains("idle disk reads of a on drive D: paused", StringComparison.Ordinal));
    }

    [Fact]
    public void AGameWithoutAKnownListGetsASmallFixedShareAndADeletedGamesReadsOnlyStayInTheDriveTotal()
    {
        _zoneMib = 0; // 목록 크기를 모름
        GameSettings a = Game("a");
        Prewarmer p = Make(On with { IdleReadGibPerDay = 5 }, a);
        Assert.Equal(Prewarmer.IdleReadUnknownListShare, p.IdleReadShare(a));

        _zoneMib = 100;
        GameSettings b = Game("b");
        p = Make(On with { IdleReadGibPerDay = 2 }, a, b);
        long shareA = p.IdleReadShare(a);
        p.DiskReads.Add(a.OriginRoot, ReadPurpose.Idle, 700 * Mib, "deleted"); // 설정에 없는 (지운) 게임이 쓴 양

        Assert.Equal(shareA, p.IdleReadShare(a)); // 남은 게임의 몫은 그대로
        Assert.Equal(2048 * Mib - 700 * Mib, 2048 * Mib - p.DiskReads.Used(a.OriginRoot, ReadPurpose.Idle).Bytes); // 드라이브 합계에는 남음
        Assert.Equal(Math.Min(shareA, 1348 * Mib), p.IdleReadRemaining(a));
    }

    [Fact]
    public void ACacheHitStreamCountsAlmostNothingAndADiskSpeedReadCountsWhatItAccepted()
    {
        _streamer.Seconds = 0.01; // 100 MiB 를 0.01 s 에 받아들임: 메모리에 있던 것
        Prewarmer p = StartedAndDue(On, Game());

        Ticks(p, 1, On);

        Assert.Equal(2, _streamer.Seen.Count);
        Assert.True(Used(p, ReadPurpose.Idle) < 8 * Mib + 3 * Mib); // 느린 표본 8 곳 + 시간 x 상한 약 2.5 MiB
        Assert.True(Used(p, ReadPurpose.Trigger) < 3 * Mib); // 시작 예열도 메모리에 있던 것

        _streamer.Seconds = 0.4; // 100 MiB 가 0.4 s = 250 MiB/s: 디스크가 낼 수 있는 가장 빠른 속도
        _now += TimeSpan.FromMinutes(11);
        long before = Used(p, ReadPurpose.Idle);
        Ticks(p, 1, On);

        Assert.Equal(3, _streamer.Seen.Count);
        Assert.Equal(100 * Mib + 8 * Mib, Used(p, ReadPurpose.Idle) - before); // 받아들인 100 MiB 전부 + 느린 표본
    }

    [Fact]
    public void TheCountedBytesAreTheSmallerOfTheAcceptedBytesAndTheDiskIncreaseThenTheTimeBoundThenTheAcceptedBytes()
    {
        Assert.Equal(100 * Mib, Prewarmer.CountedDiskBytes(100 * Mib, 0.4, 250)); // 디스크 속도 (250 MiB/s) 로 읽음: 받아들인 바이트 그대로
        Assert.Equal(100 * Mib, Prewarmer.CountedDiskBytes(100 * Mib, 0.3, 250)); // 상한보다 조금 빠른 디스크 읽기 (333 MiB/s, 상한의 2 배 이하) 도 전부 셈
        Assert.Equal(100 * Mib, Prewarmer.CountedDiskBytes(100 * Mib, 0.2, 250)); // 정확히 상한의 2 배도 전부 셈
        Assert.Equal(25 * Mib, Prewarmer.CountedDiskBytes(100 * Mib, 0.1, 250)); // 상한의 2 배를 넘는 속도 (1000 MiB/s) 는 메모리가 섞인 것으로 보고 상한 x 시간
        Assert.Equal(100 * Mib, Prewarmer.CountedDiskBytes(100 * Mib, 9, 250)); // 시간 기준이 더 커도 받아들인 바이트까지
        Assert.Equal(0, Prewarmer.CountedDiskBytes(100 * Mib, 0, 250)); // 재었는데 0 초: 아주 짧은 시간이라 0
        Assert.Equal(100 * Mib, Prewarmer.CountedDiskBytes(100 * Mib, null, 250)); // 시간 값이 없으면 받아들인 바이트
        Assert.Equal(100 * Mib, Prewarmer.CountedDiskBytes(100 * Mib, 0.01, 0)); // 상한을 모르면 받아들인 바이트
        Assert.Equal(100 * Mib, Prewarmer.CountedDiskBytes(100 * Mib, double.NaN, 250)); // 시간이 유효하지 않으면 받아들인 바이트
    }

    [Fact]
    public void ServiceRestartsThatReadWhatIsAlreadyInMemoryDoNotFillTheLimit()
    {
        _streamer.Seconds = 0.001; // 메모리에 있던 것
        GameSettings g = Game();
        for (int restart = 0; restart < 10; restart++)
        {
            Prewarmer p = Make(On, g);
            p.RunServiceStart();
            Ticks(p, 3, On);
        }

        Prewarmer last = Make(On, g);
        Assert.Equal(0, Used(last, ReadPurpose.Idle));
        Assert.True(Used(last, ReadPurpose.Trigger) < 10 * 9 * Mib); // 계기 있는 읽기이지만 캐시에 있던 것은 시간만큼만: 재시작마다 느린 표본 8 MiB 와 스트림 0.3 MiB 남짓
        Assert.False(last.IdleReadsExhausted(g));
    }

    [Fact]
    public void ARetryFromTheWaitingListOfAnIdleRewarmFollowsTheLimitButARetryOfARequestDoesNot()
    {
        GameSettings g = Game();
        Prewarmer p = StartedAndDue(On, g);
        p.WarmRank = list => list; // 대기 재시도는 예열 예산 배정이 있어야 함
        _avail = 100; // 메모리가 모자라 계기 없는 재예열이 읽지 못하고 대기 목록으로 넘어감
        Ticks(p, 1, On);
        Assert.True(p.HasPendingWarm("g"));
        int before = _streamer.Seen.Count;

        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 25 * Gib, g.Name); // 상한 초과
        _avail = 16000;
        _now += TimeSpan.FromMinutes(30);
        Ticks(p, 6, NoRewarm);

        Assert.Equal(before, _streamer.Seen.Count); // 상한에 막혀 재시도하지 않음
        Assert.True(p.HasPendingWarm("g"));

        _now += TimeSpan.FromHours(25);
        Ticks(p, 6, NoRewarm);

        Assert.True(_streamer.Seen.Count > before); // 창에서 빠져 재시도함

        // 계기 있는 요청 (게임 시작 예열) 이 실패해 넘어간 재시도는 상한 밖
        Prewarmer second = Make(On, g);
        second.WarmRank = list => list;
        second.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 25 * Gib, g.Name);
        _avail = 100;
        Assert.False(second.Run(g, "trigger g.exe", ignoreCooldown: true).Ran);
        Assert.True(second.HasPendingWarm("g"));
        _avail = 16000;
        int seen = _streamer.Seen.Count;
        _now += TimeSpan.FromMinutes(30);
        Ticks(second, 6, NoRewarm);

        Assert.True(_streamer.Seen.Count > seen);
    }

    [Fact]
    public void ThePrewarmAfterARelocationFollowsTheLimitAndIsCountedAsIdle()
    {
        GameSettings g = Game();
        Prewarmer p = Make(On, g);
        Assert.True(p.Guarded(g, "auto after relocation", pin: true, ignoreCooldown: true).Ran);
        Assert.Equal(100 * Mib, Used(p, ReadPurpose.Idle));
        Assert.Equal(0, Used(p, ReadPurpose.Trigger));

        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 25 * Gib, g.Name);
        int before = _streamer.Seen.Count;
        PrewarmResult limited = p.Guarded(g, "auto after relocation", pin: true, ignoreCooldown: true);

        Assert.False(limited.Ran);
        Assert.Equal(Prewarmer.IdleReadLimit, limited.SkipReason);
        Assert.Equal(before, _streamer.Seen.Count);
        Assert.False(p.HasPendingWarm("g")); // 다음 게임·런처 시작의 예열이 채움
        Assert.True(p.Run(g, "trigger g.exe", ignoreCooldown: true).Ran); // 그 예열은 상한 밖
    }

    [Fact]
    public void TheCheckAfterAGameExitStillRunsWhenTheFirstOneWasBlockedByABusyDrive()
    {
        GameSettings g = Game();
        Prewarmer p = StartedAndDue(On, g);
        int before = _streamer.Seen.Count;
        p.NoteGameExit();
        _disk = 50 << 20; // 드라이브가 바쁨
        _now += TimeSpan.FromMinutes(4);
        Ticks(p, 1, On);
        Assert.Equal(before, _streamer.Seen.Count); // 막혀서 읽지 못함

        _disk = 0;
        _now += TimeSpan.FromMinutes(11);
        Ticks(p, 1, On);

        Assert.Equal(before + 1, _streamer.Seen.Count); // 바쁨이 풀린 뒤 다음 점검이 계기 없는 읽기로 돎
    }

    [Fact]
    public void TheIdleReadsOfTheLastDaySurviveARestartAndAClockSetBackDoesNotFreeThem()
    {
        GameSettings g = Game();
        Prewarmer first = Make(On, g);
        first.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 300 * Mib, g.Name);
        first.Shutdown(); // 서비스 종료 때 저장하지 않은 변경을 상태 파일에 씀

        Assert.Contains("disk_reads", File.ReadAllText(StatusPath), StringComparison.Ordinal);
        Prewarmer second = Make(On, g);
        Assert.Equal(300 * Mib, Used(second, ReadPurpose.Idle));
        Assert.Equal(300 * Mib, second.DiskReads.Used(g.OriginRoot, ReadPurpose.Idle, g.Name).Bytes); // 게임별 몫도 이어짐

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
        Assert.Contains("D: trigger reads 100.00 MiB, idle reads 0.00 MiB of 24576.00 MiB limit, recovery reads 0.00 MiB in 0 samples", lines[0], StringComparison.Ordinal);

        Ticks(p, 4, On);
        Assert.Single(_log.Lines, line => line.Contains("disk reads in the last 24 h:", StringComparison.Ordinal));

        _now += TimeSpan.FromHours(25);
        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Trigger, Mib);
        Ticks(p, 1, On);
        Assert.Equal(2, _log.Lines.Count(line => line.Contains("disk reads in the last 24 h:", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheCheckAfterAGameExitOfAGameThatWasSkippedWaitsForTheLimitLikeAnyOtherCheck()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        Prewarmer p = StartedAndDue(On, a, b);
        p.NoteGameExit();
        _now += TimeSpan.FromMinutes(4);
        Assert.True(p.Run(b, "service start", ignoreCooldown: true).Ran); // b 는 방금 예열해 이 점검이 건너뜀 (최근 예열)
        int before = _streamer.Seen.Count;

        Ticks(p, 1, On);

        Assert.Equal(before + 1, _streamer.Seen.Count); // a 의 종료 뒤 점검만 읽음
        Assert.Equal("a", _streamer.Seen[^1].Name);
        Assert.False(p.AfterExitCheckPending("a")); // 점검을 마친 게임의 종료 표시는 지워짐
        Assert.True(p.AfterExitCheckPending("b")); // 건너뛴 게임의 표시는 남음

        p.DiskReads.Add(a.OriginRoot, ReadPurpose.Idle, 25 * Gib, a.Name); // 이제 두 게임 다 상한에 닿음
        p.DiskReads.Add(b.OriginRoot, ReadPurpose.Idle, 25 * Gib, b.Name);
        _now += TimeSpan.FromMinutes(11);
        Ticks(p, 1, On);

        Assert.Equal(before + 1, _streamer.Seen.Count); // b 의 종료 뒤 점검도 상한에 막혀 읽지 않음
        Assert.True(p.AfterExitCheckPending("b")); // 막힌 게임의 표시도 남음

        _now += TimeSpan.FromHours(25);
        Ticks(p, 1, On);

        Assert.Equal(before + 3, _streamer.Seen.Count); // 하루 뒤 창에서 빠지면 두 게임 모두 읽음
    }

    [Fact]
    public void ARequestThatFailedFirstStaysARequestEvenIfALaterPeriodicReadFailsToo()
    {
        GameSettings g = Game();
        Prewarmer p = Make(On, g);
        p.WarmRank = list => list;
        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 25 * Gib, g.Name); // 계기 없는 읽기는 상한을 넘은 상태
        _avail = 100;
        Assert.False(p.Run(g, "trigger g.exe", ignoreCooldown: true).Ran); // 계기 있는 요청이 메모리 부족으로 실패해 대기 목록으로
        Assert.True(p.HasPendingWarm("g"));

        _now += TimeSpan.FromMinutes(30);
        using (DiskReadLedger.Scope(ReadPurpose.Idle))
        {
            Assert.False(p.Run(g, "rewarm", ignoreCooldown: true).Ran); // 재시도 기한이 지난 뒤 계기 없는 읽기도 실패해 항목이 새로 쓰임
        }

        _avail = 16000;
        int before = _streamer.Seen.Count;
        _now += TimeSpan.FromMinutes(30);
        Ticks(p, 6, NoRewarm);

        Assert.True(_streamer.Seen.Count > before); // 처음 요청의 분류가 남아 상한 밖으로 재시도함
    }

    [Fact]
    public void AReadStartsWithTheRemainingAmountAsItsBudgetOrNotAtAllWhenTheRemainderIsTooSmall()
    {
        _zoneMib = 600;
        GameSettings g = Game();
        Prewarmer p = Make(On with { IdleReadGibPerDay = 1 }, g); // 상한 1024 MiB = 게임 몫
        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 624 * Mib, g.Name);

        using (DiskReadLedger.Scope(ReadPurpose.Idle))
        {
            Assert.Equal(400L, p.IdleReadRoom(g, null)); // 남은 400 MiB 가 목록 (600 MiB) 보다 작아 예산 상한이 됨
            Assert.Equal(300L, p.IdleReadRoom(g, 300)); // 이미 더 작은 상한이 있으면 그대로
        }

        Assert.Null(p.IdleReadRoom(g, null)); // 계기 있는 읽기는 바뀌지 않음

        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 400 * Mib, g.Name);
        using (DiskReadLedger.Scope(ReadPurpose.Idle))
        {
            Assert.True(p.IdleReadsExhausted(g)); // 다 쓰면 읽지 않음
            Assert.Equal(0L, p.IdleReadRoom(g, null));
        }
    }

    [Fact]
    public void TheCacheCheckAtServiceStartIsCountedAsTriggeredReads()
    {
        GameSettings g = Game();
        Prewarmer p = Make(On, g);

        _ = p.ProbeFront(g, purpose: "already warm");

        Assert.Equal(8 * Mib, Used(p, ReadPurpose.Trigger)); // 느린 표본 8 곳
        Assert.Equal(0, Used(p, ReadPurpose.Idle));
    }

    [Fact]
    public void AnAfterRelocationPrewarmGetsWhatIsLeftAsItsWindowBudget()
    {
        _zoneMib = 600;
        GameSettings g = Game();
        Prewarmer p = Make(On with { IdleReadGibPerDay = 1 }, g);
        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 624 * Mib, g.Name); // 남은 양 400 MiB

        Assert.True(p.Guarded(g, "auto after relocation", pin: true, ignoreCooldown: true).Ran);

        Assert.True(_streamer.Seen[^1].MaxStreamMib <= 400); // 목록 (600 MiB) 전체가 아니라 남은 양까지만 읽음
    }

    [Fact]
    public void AFailedStatusWriteKeepsTheReadsUnsavedAndTheNextWriteSavesThem()
    {
        string folder = Path.Combine(_dir, "later");
        string path = Path.Combine(folder, "prewarm_status.json");
        GameSettings g = Game();
        var p = new Prewarmer([g], _streamer, new PrewarmEnv { AvailableRamMib = () => _avail, GameRunning = _ => false, ZoneBytes = _ => 100 * Mib, TraceExists = _ => true }, _log, path, () => _now);
        p.DiskReads.Add(g.OriginRoot, ReadPurpose.Idle, 300 * Mib, g.Name);

        p.Shutdown(); // 폴더가 없어 쓰기 실패
        Assert.True(p.DiskReads.HasUnsaved);

        Directory.CreateDirectory(folder);
        p.Shutdown();

        Assert.False(p.DiskReads.HasUnsaved);
        Assert.Contains("disk_reads", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public void ACheckThatFinishesDoesNotClearTheMarkOfAGameExitThatArrivedWhileItRan()
    {
        GameSettings g = Game();
        Prewarmer p = StartedAndDue(On, g);
        p.NoteGameExit();
        _now += TimeSpan.FromMinutes(4);
        bool exitedAgain = false;
        _onProbe = () =>
        {
            if (!exitedAgain)
            {
                exitedAgain = true;
                p.NoteGameExit(); // 이 점검이 도는 동안 새 종료가 들어옴
            }
        };
        int before = _streamer.Seen.Count;

        Ticks(p, 1, On);
        Assert.Equal(before + 1, _streamer.Seen.Count); // 첫 종료의 점검
        Assert.True(p.AfterExitCheckPending("g")); // 도는 중에 들어온 새 종료의 표시는 이 점검이 지우지 않음

        _onProbe = null;
        _now += TimeSpan.FromMinutes(11);
        Ticks(p, 1, On);

        Assert.Equal(before + 2, _streamer.Seen.Count); // 새 종료의 점검도 도는 중에 들어온 종료 때문에 막히지 않고 읽음
        Assert.False(p.AfterExitCheckPending("g")); // 그 점검을 마치면 표시가 지워짐
    }

    [Fact]
    public void TheFrontRefreshIsCountedEvenWhenTheStreamItselfReadNothing()
    {
        GameSettings g = Game();
        _streamer.ReadMib = 0;
        _streamer.RefreshMib = 64;
        Prewarmer p = Make(On, g);

        _ = p.Run(g, "service start", ignoreCooldown: true);

        Assert.Equal(64 * Mib, Used(p, ReadPurpose.Trigger));
    }

    private Prewarmer MakeProbing(GameSettings g, Func<IReadOnlyList<(string Path, long Offset, int Bytes)>, Func<bool>, ProbeResult?> checkedProbe) => new([g], _streamer, new PrewarmEnv
    {
        AvailableRamMib = () => _avail,
        GameRunning = _ => false,
        ZoneBytes = _ => _zoneMib * Mib,
        TraceExists = _ => true,
        ProbeSamplesChecked = checkedProbe,
    }, _log, StatusPath, () => _now);

    private (string File, (string, long, int)[] Samples) SampleFile()
    {
        string file = Path.Combine(_dir, "sample.bin");
        File.WriteAllBytes(file, new byte[2 * (int)Mib]);
        return (file, [(file, 0, (int)Mib), (file, 0, (int)Mib)]);
    }

    [Fact]
    public void ASlowSampleIsCountedEvenWhenTheCancelCameAfterTheLastSampleAndTheResultWasDiscarded()
    {
        GameSettings g = Game();
        (_, (string, long, int)[] samples) = SampleFile();
        using var cts = new CancellationTokenSource();
        Prewarmer p = MakeProbing(g, (s, canIssue) =>
        {
            ProbeResult? read = CacheProbe.Probe(s, slowMs: -1, canIssue: canIssue, pageReads: () => null); // 두 표본 모두 느림
            cts.Cancel(); // 마지막 표본을 읽은 뒤 취소: 호출한 쪽은 결과를 버림
            return read;
        });

        ProbeResult? result = p.ProbeSamplesForGame(g, samples, "test", cts.Token);

        Assert.Null(result);
        Assert.Equal(2 * Mib, Used(p, ReadPurpose.Trigger));
    }

    [Fact]
    public void TheCancelledSampleCountOfAnEarlierCallOnTheSameThreadIsNotCountedForALaterCallThatReadNothing()
    {
        GameSettings g = Game();
        (_, (string, long, int)[] samples) = SampleFile();
        int calls = 0;
        Assert.Null(CacheProbe.Probe(samples, slowMs: -1, canIssue: () => ++calls <= 1, pageReads: () => null)); // 취소 전에 느린 표본 한 곳을 읽고 이 스레드에 수를 남김
        Prewarmer p = MakeProbing(g, (_, _) => null); // 표본을 읽지 않고 결과 없음
        using var cts = new CancellationTokenSource();

        Assert.Null(p.ProbeSamplesForGame(g, samples, "test", cts.Token));

        Assert.Equal(0, Used(p, ReadPurpose.Trigger));
    }

    [Fact]
    public void TheFrontRefreshAtTheEndOfAStreamAndTheSamplesReadBeforeACancelAreCounted()
    {
        GameSettings g = Game();
        Prewarmer p = Make(On, g);

        p.NoteStreamRead(g, new StreamStats(100 * Mib, 1, 0, 10) { RefreshBytes = 64 * Mib, RefreshSeconds = 1 });
        Assert.Equal(164 * Mib, Used(p, ReadPurpose.Trigger)); // 스트림 100 MiB + 끝의 앞부분 다시 읽기 64 MiB (64 MiB/s 라 전부)

        string file = Path.Combine(_dir, "sample.bin");
        File.WriteAllBytes(file, new byte[2 * (int)Mib]);
        int calls = 0;
        ProbeResult? result = CacheProbe.Probe([(file, 0, (int)Mib), (file, 0, (int)Mib)], slowMs: -1, canIssue: () => ++calls <= 1, pageReads: () => null);
        Assert.Null(result); // 두 번째 표본 전에 취소돼 결과는 없음
        p.NoteProbeRead(g, result);

        Assert.Equal(164 * Mib + Mib, Used(p, ReadPurpose.Trigger)); // 그 전에 읽은 느린 표본 한 곳은 셈
    }
}
