using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 시작 예열이 상한으로 일부만 읽은 핫 집합을 유휴 때 한 번 보충하는가 (상태 기록, 취소, 예산, 재예열 점검, 재시작).
public sealed class SupplementWarmTests : IDisposable
{
    private const long Mib = 1L << 20;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_supp_" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly CapStreamer _streamer = new();
    private DateTimeOffset _now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private bool _running;
    private bool _helperRunning;
    private bool _relocating;
    private double? _disk = 0;
    private long _avail = 16000;
    private long _zoneMib = 100;
    private ProbeResult? _probe;
    private int _probeCalls;

    public SupplementWarmTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string StatusPath => Path.Combine(_dir, "prewarm_status.json");

    private sealed class CapStreamer : IGameStreamer
    {
        public List<GameSettings> Seen { get; } = [];

        public Func<GameSettings, bool>? OnStream { get; set; }

        public long? ReadMib { get; set; }

        public long? RequestedMib { get; set; }

        public long ErrorCount { get; set; }

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            Seen.Add(game);
            bool go = OnStream?.Invoke(game) ?? true;
            long windowMib = Math.Min(100L, game.MaxStreamMib);
            return (new StreamStats((ReadMib ?? windowMib) * Mib, 1, ErrorCount, 0.01, RequestedBytes: (RequestedMib ?? windowMib) * Mib), go && !stop.IsCancellationRequested);
        }
    }

    // 핫 집합 100 MiB, 실제 환경처럼 max_stream_mib 로 자른 값을 돌려줌.
    private Prewarmer Make(GameSettings game, string? statusPath = null)
    {
        var p = new Prewarmer([game], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = g => _running || (_helperRunning && DispatchEnv.IsGameRunning(g, [(1234, "body.exe")])),
            ZoneBytes = g => Math.Min(_zoneMib, g.MaxStreamMib) * Mib,
            TraceExists = _ => true,
            DiskBytesPerSecond = _ => _disk,
            ProbeReadMs = _ =>
            {
                _probeCalls++;
                return _probe;
            },
        }, _log, statusPath ?? StatusPath, () => _now);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 40 });
        p.Relocating = _ => _relocating;
        return p;
    }

    private static GameSettings Game() => Games.Make("g", "g.exe");

    private static readonly AutoSettings NoRewarm = AutoSettings.Default with { Enabled = false };

    // 30 초 간격 틱 n 번.
    private void Ticks(Prewarmer p, int n, AutoSettings? auto = null)
    {
        for (int i = 0; i < n; i++)
        {
            p.Tick(auto ?? NoRewarm);
            _now += TimeSpan.FromSeconds(30);
        }
    }

    private JObject Status(string game) => (JObject)((JObject)((JObject)JsonRead.ReadFile(StatusPath)!)["games"]!)[game]!;

    [Fact]
    public void StartupCapLeavesTheRestPendingAndTheStatusRecordsWarmedAndHot()
    {
        Prewarmer p = Make(Game());

        PrewarmResult r = Assert.Single(p.RunServiceStart());

        Assert.True(r.Ran);
        Assert.Equal(40, _streamer.Seen[0].MaxStreamMib); // 시작 상한으로 줄인 사본
        Assert.True(p.SupplementPending("g"));
        JObject s = Status("g");
        Assert.Equal((40L, 100L), ((long)s["warmed_mib"]!, (long)s["hot_mib"]!));
    }

    [Fact]
    public void WithoutATruncationNothingIsPending()
    {
        Prewarmer p = Make(Game());
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 1024 });

        Assert.True(Assert.Single(p.RunServiceStart()).Ran);

        Assert.False(p.SupplementPending("g"));
        Assert.False(Status("g").ContainsKey("warmed_mib"));
    }

    [Fact]
    public void IdleDriveReadsTheRestAfterSixtySecondsExactlyOnce()
    {
        GameSettings g = Game();
        Prewarmer p = Make(g);
        p.RunServiceStart();

        Ticks(p, 2); // 한가함을 막 보기 시작해서 30 초 뒤
        Assert.Single(_streamer.Seen);

        Ticks(p, 1); // 60 초 한가함
        Assert.Equal(2, _streamer.Seen.Count);
        Assert.Equal(g.MaxStreamMib, _streamer.Seen[1].MaxStreamMib); // 설정 상한 그대로: 전체 핫 집합
        Assert.False(p.SupplementPending("g"));
        JObject s = Status("g");
        Assert.Equal((100L, 100L), ((long)s["warmed_mib"]!, (long)s["hot_mib"]!));

        Ticks(p, 6);
        Assert.Equal(2, _streamer.Seen.Count); // 한 번뿐
    }

    [Fact]
    public void NoProgressIsHeldAcrossThirtySecondTicksAndSixtySecondIdleWindows()
    {
        _zoneMib = 1000;
        GameSettings g = Game();
        Prewarmer p = Make(g);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 40, RewarmMinutes = 5 });
        Assert.True(Assert.Single(p.RunServiceStart()).Ran);
        _avail = 4096 + 500 + Prewarmer.FinalShrinkHeadroomMib; // 창 500 MiB 까지만 들어감 (창을 잡을 때 두는 여유 포함): 읽은 양보다 의미 있게 더 들어가 보충을 시작함
        (_streamer.ReadMib, _streamer.RequestedMib, _streamer.ErrorCount) = (128, 256, 1);

        Ticks(p, 3); // 첫 보충은 40 MiB 에서 128 MiB 로 진전
        Ticks(p, 3); // 두 번째 완료는 같은 128 MiB: 5분 기한 시작
        Assert.Equal(3, _streamer.Seen.Count);
        Assert.Single(_log.Lines, line => line.Contains("supplement prewarm deferred (no progress", StringComparison.Ordinal));

        Ticks(p, 9); // 30초 점검과 60초 연속 유휴가 여러 번 지나도 같은 범위를 읽지 않음
        Assert.Equal(3, _streamer.Seen.Count);
        Assert.Single(_log.Lines, line => line.Contains("supplement prewarm deferred (no progress", StringComparison.Ordinal));

        Ticks(p, 3); // 기한과 새 60초 유휴가 모두 지나면 다시 한 번 시도
        Assert.Equal(4, _streamer.Seen.Count);
        Assert.Single(_log.Lines, line => line.Contains("supplement prewarm deferred (no progress", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangingTheReadListReleasesADeferredSupplement()
    {
        _zoneMib = 1000;
        string root = Path.Combine(_dir, "game");
        Directory.CreateDirectory(root);
        string plan = Path.Combine(_dir, "plan.json");
        string trace = Path.Combine(_dir, "trace.json");
        File.WriteAllText(plan, "plan");
        File.WriteAllText(trace, "trace");
        GameSettings g = Game() with { OriginRoot = root, Plan = plan, Trace = trace };
        Prewarmer p = Make(g);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 40, RewarmMinutes = 5 });
        Assert.True(Assert.Single(p.RunServiceStart()).Ran);
        _avail = 4096 + 500 + Prewarmer.FinalShrinkHeadroomMib;
        (_streamer.ReadMib, _streamer.RequestedMib, _streamer.ErrorCount) = (128, 256, 1);

        Ticks(p, 3); // 40 -> 128 MiB 진전
        Ticks(p, 3); // 같은 수락량으로 지연 등록
        Assert.Equal(3, _streamer.Seen.Count);

        File.WriteAllText(trace, "trace list changed"); // TraceLoader 의 입력이 바뀌면 계획 세대가 바뀜
        Assert.True(p.Supplement(g, CancellationToken.None));
        Assert.Equal(4, _streamer.Seen.Count);
    }

    [Fact]
    public void SupplementWaitsForTheSecondRegisteredExeToExit()
    {
        GameSettings g = Game() with { OriginRoot = @"D:\Games\g", ExeNames = ["g.exe", "body.exe"] }; // 런처 + 학습으로 더한 본체 이름, 런처는 이미 끝남
        Prewarmer p = Make(g);
        Assert.True(Assert.Single(p.RunServiceStart()).Ran);
        Assert.True(p.SupplementPending("g"));
        _helperRunning = true;

        Assert.False(p.Supplement(g, CancellationToken.None));
        Assert.Single(_streamer.Seen);

        _helperRunning = false;
        Assert.True(p.Supplement(g, CancellationToken.None));
        Assert.Equal(2, _streamer.Seen.Count);
        Assert.False(p.SupplementPending("g"));
    }

    [Theory]
    [InlineData("game")]
    [InlineData("relocation")]
    [InlineData("busy")]
    [InlineData("unreadable")]
    public void NoSupplementWhileTheGameRunsRelocatesOrTheDriveIsBusy(string why)
    {
        Prewarmer p = Make(Game());
        p.RunServiceStart();
        (_running, _relocating, _disk) = (why == "game", why == "relocation", why == "busy" ? 50 << 20 : why == "unreadable" ? null : 0);

        Ticks(p, 10);

        Assert.Single(_streamer.Seen);
        Assert.True(p.SupplementPending("g"));
        _running = _relocating = false; // 한가해지면 처음부터 60 초를 다시 셈
        _disk = 0;
        Ticks(p, 2);
        Assert.Single(_streamer.Seen);
        Ticks(p, 1);
        Assert.Equal(2, _streamer.Seen.Count);
    }

    [Fact]
    public void GameStartDuringTheSupplementStopsItAndItIsRetriedLater()
    {
        Prewarmer p = Make(Game());
        p.RunServiceStart();
        bool first = true;
        _streamer.OnStream = g =>
        {
            if (g.MaxStreamMib != 40 && first)
            {
                first = false;
                p.NoteGameStart("g"); // 보충 읽기 도중 게임 시작: 다음 묶음 경계에서 멈춤
            }

            return true;
        };

        Ticks(p, 3);

        Assert.Equal(2, _streamer.Seen.Count);
        Assert.True(p.SupplementPending("g")); // 끝까지 읽지 못해 남음
        Assert.Contains(_log.Lines, l => l.Contains("supplement prewarm cancelled", StringComparison.Ordinal));

        Ticks(p, 3);
        Assert.Equal(3, _streamer.Seen.Count);
        Assert.False(p.SupplementPending("g"));
    }

    [Fact]
    public void SupplementStaysInsideTheMemoryBudget()
    {
        Prewarmer p = Make(Game());
        p.RunServiceStart();
        _avail = 100 + 4096 - 1; // 핫 집합 100 MiB + 기본 여유 4096 MiB 에 1 MiB 모자람

        Ticks(p, 6);

        Assert.Single(_streamer.Seen);
        Assert.True(p.SupplementPending("g"));
        Assert.Contains(_log.Lines, l => l.Contains("supplement prewarm skipped", StringComparison.Ordinal));

        _avail = 16000;
        Ticks(p, 3);
        Assert.Equal(2, _streamer.Seen.Count);
    }

    [Fact]
    public void WarmSamplesDoNotSkipTheRewarmCheckUntilTheSupplementIsDone()
    {
        Prewarmer p = Make(Game());
        p.RunServiceStart();
        _probe = new ProbeResult(8, 0, 0.3); // 표본은 따뜻함
        AutoSettings on = AutoSettings.Default with { Enabled = true, RewarmMinutes = 10 };

        _disk = 50 << 20; // 첫 틱: 재예열 주기만 시작, 보충은 바쁨 때문에 대기 안 함
        Ticks(p, 1, on);
        _now += TimeSpan.FromMinutes(11);
        _disk = 0; // 주기가 되면 한가함: 유휴가 막 시작됐으므로 재예열 점검도 보충하지 않음 (60 초 연속 유휴 판정을 거침)
        Ticks(p, 1, on);

        Assert.Single(_streamer.Seen);
        Assert.True(p.SupplementPending("g"));
        Assert.Contains(_log.Lines, l => l.Contains("rewarm check: the rest of the hot set", StringComparison.Ordinal));

        Ticks(p, 1, on); // 30 초 뒤: 아직 모자람
        Assert.Single(_streamer.Seen);
        Ticks(p, 1, on); // 60 초 뒤: 보충
        Assert.Equal(2, _streamer.Seen.Count);
        Assert.False(p.SupplementPending("g"));

        _now += TimeSpan.FromMinutes(11);
        Ticks(p, 1, on); // 보충이 끝난 뒤에는 따뜻하면 건너뜀
        Assert.Equal(2, _streamer.Seen.Count);
        Assert.Contains(_log.Lines, l => l.Contains("rewarm check: warm", StringComparison.Ordinal));
    }

    [Fact]
    public void ColdSamplesWhileSupplementPendingRewarmOnlyThePartAlreadyRead()
    {
        Prewarmer p = Make(Game());
        p.RunServiceStart();
        _probe = new ProbeResult(8, 8, 120.0); // 표본은 cold
        AutoSettings on = AutoSettings.Default with { Enabled = true, RewarmMinutes = 10 };

        _disk = 50 << 20;
        Ticks(p, 1, on);
        _now += TimeSpan.FromMinutes(11);
        _disk = 0; // 유휴가 막 시작됨: 전체 재예열 대신 보충 대기
        Ticks(p, 1, on);

        Assert.Equal(2, _streamer.Seen.Count);
        Assert.Equal(40, _streamer.Seen[1].MaxStreamMib);
        Assert.True(p.SupplementPending("g"));

        Ticks(p, 2, on); // 앞부분을 읽은 일이 유휴 기준을 새로 시작하게 하므로 60 초가 차기 전에는 보충하지 않음
        Assert.Equal(2, _streamer.Seen.Count);
        Ticks(p, 1, on); // 60 초 뒤: 보충
        Assert.Equal(3, _streamer.Seen.Count);
        Assert.False(p.SupplementPending("g"));
    }

    [Fact]
    public void AColdPartialIsRewarmedWhenTheNewMemoryBudgetIsTooSmallAndTheSamePartWaitsForTheRewarmInterval()
    {
        _zoneMib = 1000;
        GameSettings g = Game();
        Prewarmer p = Make(g);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 40, RewarmMinutes = 10 });
        Assert.True(Assert.Single(p.RunServiceStart()).Ran);
        _avail = 4096 + 40 + 255 + Prewarmer.FinalShrinkHeadroomMib; // 읽은 양보다 255 MiB 만 더 들어가 최소 보충 여유에 못 미칩니다.
        _probe = new ProbeResult(8, 8, 120.0);
        AutoSettings on = AutoSettings.Default with { Enabled = true, RewarmMinutes = 10 };

        _disk = 0;
        Ticks(p, 1, on);
        _now += TimeSpan.FromMinutes(11);
        Ticks(p, 1, on);

        Assert.Equal(2, _streamer.Seen.Count);
        Assert.Equal(40, _streamer.Seen[1].MaxStreamMib);
        Assert.Contains(_log.Lines, line => line.Contains("supplement waits for memory", StringComparison.Ordinal));

        _probe = new ProbeResult(8, 0, 0.3);
        Ticks(p, 8, on); // 재예열 간격 안에는 같은 앞부분을 다시 읽지 않습니다.
        Assert.Equal(2, _streamer.Seen.Count);
    }

    private Prewarmer StartWithColdPartial(GameSettings g)
    {
        _zoneMib = 1000;
        Prewarmer p = Make(g);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 40, RewarmMinutes = 10 });
        Assert.True(Assert.Single(p.RunServiceStart()).Ran);
        _avail = 4096 + 40 + 255 + Prewarmer.FinalShrinkHeadroomMib; // 보충은 메모리를 기다려 읽지 않음
        _probe = new ProbeResult(8, 8, 120.0);
        return p;
    }

    private static readonly AutoSettings On = AutoSettings.Default with { Enabled = true, RewarmMinutes = 10 };

    [Fact]
    public void AColdPartialIsRewarmedAtMostTwiceADayAndTheCountSurvivesAServiceRestart()
    {
        GameSettings g = Game();
        Prewarmer p = StartWithColdPartial(g);
        Ticks(p, 1, On);
        for (int i = 0; i < 2; i++)
        {
            _now += TimeSpan.FromHours(2); // 서로 2 시간 떨어져 있어 곧 다시 식는 경우가 아님
            Ticks(p, 1, On);
        }

        Assert.Equal(3, _streamer.Seen.Count);
        _now += TimeSpan.FromHours(2);
        int probesBefore = _probeCalls;
        Ticks(p, 1, On);
        Assert.Equal(3, _streamer.Seen.Count);
        Assert.Equal(probesBefore, _probeCalls); // 쉬는 동안 식은 구간의 표본도 읽지 않음
        Assert.Contains(_log.Lines, line => line.Contains("already read again 2 times in the last 24 h", StringComparison.Ordinal));

        Prewarmer second = Make(g); // 서비스를 다시 시작해도 횟수가 이어짐
        Assert.True(second.SupplementPending("g"));
        Ticks(second, 1, On);
        _now += TimeSpan.FromHours(2);
        Ticks(second, 1, On);
        Assert.Equal(3, _streamer.Seen.Count);

        _now += TimeSpan.FromHours(24); // 하루가 지나면 다시 읽음
        Ticks(second, 1, On);
        Assert.Equal(4, _streamer.Seen.Count);
    }

    [Fact]
    public void AColdPartialThatGoesColdAgainSoonAfterTheReadRestsForADayAlsoAfterARestart()
    {
        GameSettings g = Game();
        Prewarmer p = StartWithColdPartial(g);
        Ticks(p, 1, On);
        _now += TimeSpan.FromMinutes(11);
        Ticks(p, 1, On);
        Assert.Equal(2, _streamer.Seen.Count); // 한 번 다시 읽음

        _now += TimeSpan.FromMinutes(11); // 읽은 지 2 × rewarm_minutes (20 분) 안에 또 식음
        Ticks(p, 1, On);
        Assert.Equal(2, _streamer.Seen.Count);
        Assert.Contains(_log.Lines, line => line.Contains("went cold again within 20 min of the last read", StringComparison.Ordinal));
        Assert.NotNull(p.PartialRewarmPause("g"));

        Prewarmer second = Make(g);
        int probesBefore = _probeCalls;
        Ticks(second, 1, On);
        _now += TimeSpan.FromHours(6);
        Ticks(second, 1, On);
        Assert.Equal(2, _streamer.Seen.Count);
        Assert.Equal(probesBefore, _probeCalls);

        _now += TimeSpan.FromHours(24);
        Ticks(second, 1, On);
        Assert.Equal(3, _streamer.Seen.Count);
    }

    [Fact]
    public void ASetBackClockDoesNotReleaseTheDailyLimit()
    {
        GameSettings g = Game();
        Prewarmer p = StartWithColdPartial(g);
        Ticks(p, 1, On);
        for (int i = 0; i < 2; i++)
        {
            _now += TimeSpan.FromHours(2);
            Ticks(p, 1, On);
        }

        Assert.NotNull(p.PartialRewarmPause("g"));
        _now -= TimeSpan.FromHours(10);

        (DateTimeOffset until, _) = p.PartialRewarmPause("g")!.Value;
        Assert.True(until - _now <= TimeSpan.FromHours(24) && until > _now);
    }

    [Fact]
    public void AShortSupplementListRecordsTheBytesReadAndKeepsTheWindowForTheNextCapCheck()
    {
        _zoneMib = 1000;
        GameSettings g = Game();
        Prewarmer p = Make(g);
        Assert.True(Assert.Single(p.RunServiceStart()).Ran);
        _avail = 4096 + 500 + Prewarmer.FinalShrinkHeadroomMib;
        (_streamer.ReadMib, _streamer.RequestedMib) = (100, 100);

        Assert.True(p.Supplement(g, CancellationToken.None));

        Assert.Equal((100L, 1000L), ((long)Status("g")["warmed_mib"]!, (long)Status("g")["hot_mib"]!));
        Assert.Equal(500L, (long)Status("g")["window_mib"]!);
        Assert.Equal(2, _streamer.Seen.Count);

        _now += TimeSpan.FromMinutes(11); // 재시도 기한 (10 분) 이 지나도 같은 계획 창을 다시 읽지 않습니다.
        Assert.False(p.Supplement(g, CancellationToken.None));
        Assert.Equal(2, _streamer.Seen.Count);
        Assert.Contains(_log.Lines, line => line.Contains("already read: 100 of 1000 MiB of a 500 MiB window whose plan list ended there", StringComparison.Ordinal)); // 로그에 읽은 양과 창 크기가 함께 나옴
    }

    [Fact]
    public void AColdShortSupplementListIsReadAgainFromTheSameWindowWithTheSameSparseMarkAndNeverShrinks()
    {
        _zoneMib = 1000;
        GameSettings g = Game();
        Prewarmer p = Make(g);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 40, RewarmMinutes = 10 });
        Assert.True(Assert.Single(p.RunServiceStart()).Ran);
        _avail = 4096 + 500 + Prewarmer.FinalShrinkHeadroomMib;
        (_streamer.ReadMib, _streamer.RequestedMib) = (100, 100);
        Assert.True(p.Supplement(g, CancellationToken.None)); // 500 MiB 창의 계획 목록은 100 MiB 에서 끝납니다.
        Assert.Equal((100L, 500L), ((long)Status("g")["warmed_mib"]!, (long)Status("g")["window_mib"]!));
        p.SetSparseOut("g", true); // 처음 예열이 스파스 범위를 뺐음
        _probe = new ProbeResult(8, 8, 120.0);

        _disk = 50 << 20;
        Ticks(p, 1, On); // 재예열 점검 주기만 시작합니다.
        _now += TimeSpan.FromMinutes(11);
        _disk = 0;
        Ticks(p, 1, On); // 유휴 판정이 새로 시작돼 보충은 기다리고, 식은 앞부분을 같은 범위로 재예열합니다.

        Assert.Equal(3, _streamer.Seen.Count);
        Assert.Equal((500, true), (_streamer.Seen[2].MaxStreamMib, _streamer.Seen[2].SkipSparse)); // 읽은 양 (100) 이 아니라 처음 목록을 만든 창, 스파스 표시도 그대로
        Assert.True(p.WithSparseMark(g).SkipSparse); // 재예열이 표시를 지우지 않음
        Assert.True(p.SupplementPending("g"));

        _streamer.ReadMib = 80; // 다음에는 목록이 더 짧게 읽혀도 읽은 양이 줄지 않음
        _now += TimeSpan.FromHours(3);
        Ticks(p, 1, On);
        Assert.Equal(4, _streamer.Seen.Count);
        Assert.Equal((500, true), (_streamer.Seen[3].MaxStreamMib, _streamer.Seen[3].SkipSparse));
        Assert.Equal((100L, 500L), ((long)Status("g")["warmed_mib"]!, (long)Status("g")["window_mib"]!));
        Assert.True(p.WithSparseMark(g).SkipSparse);
    }

    [Theory]
    [InlineData("game")]
    [InlineData("busy")]
    public void ColdPartialRewarmWaitsWhileTheGameRunsOrTheDriveIsBusy(string blocker)
    {
        _zoneMib = 1000;
        Prewarmer p = Make(Game());
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 40, RewarmMinutes = 10 });
        Assert.True(Assert.Single(p.RunServiceStart()).Ran);
        _probe = new ProbeResult(8, 8, 120.0);
        AutoSettings on = AutoSettings.Default with { Enabled = true, RewarmMinutes = 10 };

        _disk = 50 << 20;
        Ticks(p, 1, on);
        _now += TimeSpan.FromMinutes(11);
        _running = blocker == "game";
        _disk = blocker == "busy" ? 50 << 20 : 0;
        Ticks(p, 1, on);

        Assert.Single(_streamer.Seen);
        Assert.True(p.SupplementPending("g"));
    }

    [Fact]
    public void SupplementWhenIdleWaitsForSixtySecondsOfContinuousIdleAndBusyResetsIt()
    {
        GameSettings g = Game();
        Prewarmer p = Make(g);
        p.RunServiceStart();
        Assert.Single(_streamer.Seen);

        Assert.False(p.SupplementWhenIdle(g, default)); // 유휴 첫 호출: 읽지 않고 시각만 기록
        _now += TimeSpan.FromSeconds(59);
        Assert.False(p.SupplementWhenIdle(g, default));
        _disk = 50 << 20; // 바빠지면 유휴 시각을 지움
        _now += TimeSpan.FromSeconds(30);
        Assert.False(p.SupplementWhenIdle(g, default));
        _disk = 0;
        Assert.False(p.SupplementWhenIdle(g, default)); // 다시 처음부터
        _now += TimeSpan.FromSeconds(59);
        Assert.False(p.SupplementWhenIdle(g, default));
        Assert.Single(_streamer.Seen);

        _now += TimeSpan.FromSeconds(1);
        Assert.True(p.SupplementWhenIdle(g, default));
        Assert.Equal(2, _streamer.Seen.Count);
    }

    [Fact]
    public void RestartedServiceKeepsThePendingSupplementFromTheStatusFile()
    {
        Prewarmer first = Make(Game());
        first.RunServiceStart();

        Prewarmer second = Make(Game()); // 같은 상태 파일, 부팅은 하루 전

        Assert.True(second.SupplementPending("g"));
        Ticks(second, 3);
        Assert.Equal(2, _streamer.Seen.Count);
        Assert.False(second.SupplementPending("g"));
    }

    [Fact]
    public void LostStatusFileIsNotAnErrorAndNothingIsPending()
    {
        Prewarmer first = Make(Game());
        first.RunServiceStart();
        File.Delete(StatusPath);

        Prewarmer second = Make(Game());

        Assert.False(second.SupplementPending("g"));
        Ticks(second, 4);
        Assert.Single(_streamer.Seen);
    }

    [Fact]
    public void AFullRunAtTheConfiguredCapClearsThePendingSupplement()
    {
        GameSettings g = Game();
        Prewarmer p = Make(g);
        p.RunServiceStart();
        Assert.True(p.SupplementPending("g"));

        Assert.True(p.Run(g, "t", ignoreCooldown: true).Ran);

        Assert.False(p.SupplementPending("g"));
    }

    [Fact]
    public void AFailedSupplementReadIsGivenUpAndNotRetried()
    {
        Prewarmer p = Make(Game());
        p.RunServiceStart();
        _streamer.OnStream = g => g.MaxStreamMib == 40 ? true : throw new IOException("supplement read failed");

        Ticks(p, 3);

        Assert.Equal(2, _streamer.Seen.Count);
        Assert.False(p.SupplementPending("g"));
        Assert.Contains(_log.Lines, l => l.Contains("supplement given up", StringComparison.Ordinal));

        Ticks(p, 6);
        Assert.Equal(2, _streamer.Seen.Count); // 다음 틱에서 다시 읽지 않음
    }
}
