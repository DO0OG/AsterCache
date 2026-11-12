using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class PrewarmTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_prewarm_" + Guid.NewGuid().ToString("N"));
    private readonly FakeStreamer _streamer = new();
    private readonly ListLog _log = new();
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public PrewarmTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string StatusPath => Path.Combine(_dir, "prewarm_status.json");

    private bool GameRunning { get; set; }

    private long AvailableMib { get; set; } = 16000;

    private long? ZoneBytes { get; set; } = 1610L << 20;

    private bool TraceExists { get; set; } = true;

    private Prewarmer Make(params GameSettings[] games) => new(games, _streamer, new PrewarmEnv
    {
        AvailableRamMib = () => AvailableMib,
        GameRunning = _ => GameRunning,
        ZoneBytes = _ => ZoneBytes,
        TraceExists = _ => TraceExists,
    }, _log, StatusPath, () => _now);

    private static GameSettings Game(string name, PrewarmSettings? p = null) => Games.Make(name, name + ".exe") with { Prewarm = p ?? PrewarmSettings.Default };

    [Fact]
    public void PlayStartSnapshotTreatsMissingWarmHistoryAsUnavailable()
    {
        Prewarmer prewarmer = Make(Game("game"));

        Assert.Null(prewarmer.PlayStartSnapshot("game", _now));
    }

    // ---- 조건 판정 ----

    [Theory]
    [InlineData(1610, true, false, 16000, 4096, null, null)] // 모두 만족
    [InlineData(-1, true, false, 16000, 4096, null, "no plan file")]
    [InlineData(1610, false, false, 16000, 4096, null, "no trace file")]
    [InlineData(1610, true, true, 16000, 4096, null, "game is running")]
    [InlineData(1610, true, false, 5705, 4096, null, "low memory")] // 1610 + 4096 = 5706 필요
    [InlineData(1610, true, false, 5706, 4096, null, null)] // 정확히 충족
    [InlineData(1610, true, false, 1610, 0, null, null)] // 여유 0 이면 존 크기만 필요
    public void SkipReasonFollowsTheConditions(int zoneMib, bool trace, bool running, int availMib, int minFree, double? sinceMin, string? expectedStart)
    {
        string? reason = PrewarmPolicy.SkipReason(zoneMib < 0 ? null : (long)zoneMib << 20, trace, running, availMib, minFree,
            sinceMin is { } m ? TimeSpan.FromMinutes(m) : null, TimeSpan.FromMinutes(30));

        if (expectedStart is null)
        {
            Assert.Null(reason);
        }
        else
        {
            Assert.StartsWith(expectedStart, reason, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void SkipLogKeysStayBoundedWhenMemoryDetailsChange()
    {
        string[] lowMemory = Enumerable.Range(0, 2000)
            .Select(available => Prewarmer.SkipLogKey("g", $"low memory (available {available} MiB < zone 2000 MiB + reserve 4096 MiB)"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[] reserve = Enumerable.Range(0, 2000)
            .Select(available => Prewarmer.SkipLogKey("g", $"prewarm cancelled memory reserve: available {available} MiB, final set 4000 MiB + reserve 4096 MiB"))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Single(lowMemory);
        Assert.Single(reserve);
        Assert.NotEqual(lowMemory[0], reserve[0]);
        Assert.NotEqual(reserve[0], Prewarmer.SkipLogKey("g", "prewarm cancelled game started"));
    }

    [Fact]
    public void EmptyZoneIsSkippedNotPassed()
    {
        Assert.Equal("no zone", PrewarmPolicy.SkipReason(0, true, false, 99999, 0, null, TimeSpan.Zero));
    }

    [Theory]
    [InlineData(2020, 4096, 1000, 2020)] // 존 2.02 GB 는 1 GiB 에서 잘리지 않음
    [InlineData(9000, 4096, 1000, 4096)] // 상한 max_stream_mib
    [InlineData(0, 4096, 1500, 1500)] // 존이 비면 추적 총 바이트
    [InlineData(-1, 4096, 1500, 1500)] // 계획이 없으면 추적 총 바이트
    [InlineData(-1, 64, 1500, 64)]
    public void StreamBudgetIsTheZoneCappedByMaxStream(int zoneMib, int maxMib, int traceMib, int expectedMib)
    {
        GameSettings g = Game("a") with { MaxStreamMib = maxMib, MaxStreamSet = true };

        Assert.Equal((long)expectedMib << 20, PrefetchStreamer.Budget(g, zoneMib < 0 ? null : (long)zoneMib << 20, (long)traceMib << 20));
    }

    [Fact]
    public void CooldownBlocksUntilItHasPassed()
    {
        Assert.StartsWith("cooldown", PrewarmPolicy.SkipReason(1 << 20, true, false, 99999, 0, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)), StringComparison.Ordinal);
        Assert.Null(PrewarmPolicy.SkipReason(1 << 20, true, false, 99999, 0, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30)));
        Assert.Null(PrewarmPolicy.SkipReason(1 << 20, true, false, 99999, 0, TimeSpan.Zero, TimeSpan.Zero));
    }

    private sealed class BatchesStreamer(int batches, Action<int> afterBatch) : IGameStreamer
    {
        private const long Batch = 64L << 20;

        public int Read { get; private set; }

        public StreamStats Stream(GameSettings game) => new(batches * Batch, batches, 0, 0.01);

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            Read = 0;
            while (Read < batches)
            {
                if (stop.IsCancellationRequested)
                {
                    return (new StreamStats(Read * Batch, Read, 0, 0.01), false);
                }

                Read++;
                afterBatch(Read);
                progress?.Invoke(Read * Batch, batches * Batch);
            }

            return (new StreamStats(batches * Batch, batches, 0, 0.01), true);
        }
    }

    [Theory]
    [InlineData("b", 2, false)] // 같은 디스크의 다른 등록 게임이 알림 없이 뜸 (미등록 exe 로 시작해 NoteGameStart 가 오지 않는 경우): 설정과 무관하게 취소
    [InlineData("a", 6, true)] // 읽던 게임 자신의 실행만 예외
    public void KeepStreamOnGameStartStillCancelsWhenAnotherGameOnTheDriveRuns(string runner, int read, bool ran)
    {
        GameSettings a = Game("a"), b = Game("b");
        bool running = false;
        var streamer = new BatchesStreamer(6, n => running = running || n == 2);
        Prewarmer p = new([a, b], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => AvailableMib,
            GameRunning = g => running && g.Name == runner,
            ZoneBytes = _ => ZoneBytes,
            TraceExists = _ => TraceExists,
        }, _log, StatusPath, () => _now);
        p.SetAuto(AutoSettings.Default with { KeepStreamOnGameStart = true });

        PrewarmResult r = p.Run(a, "test");

        Assert.Equal(ran, r.Ran);
        Assert.Equal(read, streamer.Read);
    }

    [Fact]
    public void ASkipBecauseAGameIsRunningNamesTheBlockingProcessOnceUntilTheLogIntervalPasses()
    {
        GameSettings a = Game("a");
        int described = 0;
        GameRunning = true;
        Prewarmer p = new([a], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => AvailableMib,
            GameRunning = _ => GameRunning,
            ZoneBytes = _ => ZoneBytes,
            TraceExists = _ => TraceExists,
            DescribeBlockers = _ =>
            {
                described++;
                return @"helper.exe(1234) D:/Games/a/bin/helper.exe";
            },
        }, _log, StatusPath, () => _now);

        Assert.Equal("game is running", p.Run(a, "test").SkipReason);
        Assert.Equal("game is running", p.Run(a, "test").SkipReason);

        Assert.Single(_log.Lines, l => l.Contains("a: prewarm skipped (game is running) [test] [blocked by helper.exe(1234) ", StringComparison.Ordinal));
        Assert.Equal(1, described); // 억제된 틱은 프로세스 스냅샷을 부르지 않음
    }

    // ---- 서비스 시작과 트리거 ----

    [Fact]
    public void ServiceStartMergesRecentGamesIntoOneBatchWithoutDuplicates()
    {
        GameSettings a = Game("a"), b = Game("b", PrewarmSettings.Default with { OnServiceStart = false }), c = Game("c", PrewarmSettings.Default with { OnServiceStart = false });
        Prewarmer p = Make(a, b, c);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 1 << 20 }); // 이 시험은 병합과 중복 제거만 봄

        p.RunServiceStart([b, a, c]);

        Assert.Equal(["a", "b", "c"], _streamer.Calls.Select(x => x.Game));
    }

    [Fact]
    public void ServiceStartWarmsOnlyGamesThatAskForItAndRecordsTheResult()
    {
        Prewarmer p = Make(Game("a"), Game("b", PrewarmSettings.Default with { OnServiceStart = false }));

        IReadOnlyList<PrewarmResult> results = p.RunServiceStart();

        Assert.Equal(["a"], _streamer.Calls.Select(c => c.Game));
        Assert.True(Assert.Single(results).Ran);
        Assert.Contains(_log.Lines, l => l.StartsWith("Info: a: prewarm done (service start)", StringComparison.Ordinal));
        var status = (JObject)((JObject)JsonRead.ReadFile(StatusPath)!)["games"]!;
        var a = (JObject)status["a"]!;
        Assert.Equal("ok", a["result"]);
        Assert.Equal(100L, a["bytes"]);
        Assert.False(status.ContainsKey("b"));
    }

    [Fact]
    public void PartialSupplementDoesNoWorkWhenTheGameRestartsDuringPostExitGrace()
    {
        GameSettings game = Game("a");
        bool running = false;
        _streamer.BytesOf = _ => 1L << 20;
        var p = new Prewarmer([game], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => AvailableMib,
            GameRunning = _ => running,
            ZoneBytes = _ => 2L << 20,
            TraceExists = _ => TraceExists,
            DiskBytesPerSecond = _ => 0,
        }, _log, StatusPath, () => _now);
        AutoSettings auto = AutoSettings.Default with { StartupWarmMaxMib = 1, RewarmAfterExitMin = 3, RewarmMinutes = 0 };
        p.SetAuto(auto);
        _ = p.RunServiceStart();
        Assert.True(p.SupplementPending("a"));

        Assert.False(p.SupplementWhenIdle(game, default)); // idle streak begins before exit
        var exits = new GameExitTracker(() => _now);
        exits.ObserveStarted(game);
        _ = exits.Poll([game], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        p.SetGameExitTracker(exits);
        _now += GameExitTracker.Grace;
        Assert.Single(exits.Poll([game], new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        p.Tick(auto);
        Assert.Single(_streamer.Calls); // 3 minute shared grace still blocks supplementation

        _now += TimeSpan.FromSeconds(1);
        running = true;
        Assert.Empty(exits.Poll([game], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "a.exe" }));
        p.Tick(auto);

        Assert.Single(_streamer.Calls); // 재기동 뒤에도 무거운 작업은 시작하지 않음
    }

    [Fact]
    public void TriggerProgramStartsTheWarmupCaseInsensitivelyAndOthersDoNot()
    {
        GameSettings g = Game("a", PrewarmSettings.Default with { OnServiceStart = false, TriggerExes = ["Steam.exe"] });
        Prewarmer p = Make(g, Game("b"));

        Assert.True(p.IsTrigger("STEAM.EXE"));
        Assert.False(p.IsTrigger("a.exe")); // 게임 exe 자체는 트리거가 아님 (기존 동시 스트림이 맡음)
        Assert.Empty(p.OnProcessStart(new ProcessStart(1, "notepad.exe", 0)));
        PrewarmResult r = Assert.Single(p.OnProcessStart(new ProcessStart(2, "steam.exe", 0, "poll")));

        Assert.True(r.Ran);
        Assert.Equal(["a"], _streamer.Calls.Select(c => c.Game));
        Assert.Contains(_log.Lines, l => l.Contains("prewarm done (trigger steam.exe)", StringComparison.Ordinal));
    }

    [Fact]
    public void RepeatedTriggersWithinTheCooldownAreSkippedThenAllowed()
    {
        GameSettings g = Game("a", PrewarmSettings.Default with { TriggerExes = ["Steam.exe"], CooldownMin = 30 });
        Prewarmer p = Make(g);

        Assert.True(p.Run(g, "first").Ran);
        _now += TimeSpan.FromMinutes(5);
        PrewarmResult second = p.Run(g, "second");
        _now += TimeSpan.FromMinutes(26);
        PrewarmResult third = p.Run(g, "third");

        Assert.False(second.Ran);
        Assert.StartsWith("cooldown", second.SkipReason, StringComparison.Ordinal);
        Assert.True(third.Ran);
        Assert.Equal(2, _streamer.Calls.Count);
    }

    [Fact]
    public void LastSuccessSurvivesASkipAndAServiceRestart()
    {
        GameSettings g = Game("a", PrewarmSettings.Default with { TriggerExes = ["Steam.exe"], CooldownMin = 30 });
        Prewarmer p = Make(g);
        Assert.True(p.Run(g, "first").Ran);
        DateTimeOffset warmedAt = _now;
        _now += TimeSpan.FromMinutes(5);
        Assert.False(p.Run(g, "second").Ran); // 쿨다운으로 건너뜀: 마지막 시도 결과는 skipped

        Prewarmer restarted = Make(g); // 서비스 재시작: 상태 파일만 남음
        Assert.Equal(warmedAt, restarted.LastWarmed("a"));
    }

    [Fact]
    public void RemovingAGameKeepsItsSuccessfulWarmHistory()
    {
        GameSettings g = Game("a");
        Prewarmer p = Make(g);
        Assert.True(p.Run(g, "first").Ran);
        PlayWarmSnapshot? before = p.PlayStartSnapshot("a", _now);
        Assert.NotNull(before);

        p.SetGames([]);

        Assert.Equal(before, p.PlayStartSnapshot("a", _now));
    }

    [Fact]
    public void ChangingTheTraceGenerationInvalidatesARecentWarmProbe()
    {
        string plan = Path.Combine(_dir, "a.plan.json");
        string trace = Path.Combine(_dir, "a.prefetch.json");
        File.WriteAllText(plan, "plan");
        File.WriteAllText(trace, "trace");
        GameSettings g = Game("a") with { Plan = plan, Trace = trace };
        Prewarmer p = new([g], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => AvailableMib,
            GameRunning = _ => false,
            ZoneBytes = _ => ZoneBytes,
            ProbeReadMs = _ => new AsterCache.Windows.ProbeResult(8, 0, 0.3),
            TraceExists = _ => true,
            FileStamp = PlanFile.FingerprintOf,
        }, _log, now: () => _now);

        Assert.True(p.AlreadyWarm(g));
        Assert.Equal(_now, p.RecentWarmProbeAt(g));

        File.WriteAllText(trace, "trace changed");

        Assert.Null(p.RecentWarmProbeAt(g));
    }

    [Fact]
    public void RunningAPlanPrewarmInvalidatesTheRecentWarmProbe()
    {
        GameSettings g = Game("a");
        Prewarmer p = new([g], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => AvailableMib,
            GameRunning = _ => false,
            ZoneBytes = _ => ZoneBytes,
            TraceExists = _ => TraceExists,
            ProbeReadMs = _ => new AsterCache.Windows.ProbeResult(8, 0, 0.3),
        }, _log, now: () => _now);

        Assert.True(p.AlreadyWarm(g));
        Assert.NotNull(p.RecentWarmProbeAt(g));
        Assert.True(p.Run(g, "test", ignoreCooldown: true).Ran);

        Assert.Null(p.RecentWarmProbeAt(g));
    }

    [Theory]
    [InlineData("running")]
    [InlineData("memory")]
    [InlineData("plan")]
    [InlineData("trace")]
    public void SkippedWarmupsStreamNothingAndSayWhy(string cause)
    {
        GameRunning = cause == "running";
        AvailableMib = cause == "memory" ? 1000 : 16000;
        ZoneBytes = cause == "plan" ? null : 1610L << 20;
        TraceExists = cause != "trace";
        GameSettings g = Game("a");

        PrewarmResult r = Make(g).Run(g, "service start");

        Assert.False(r.Ran);
        Assert.NotNull(r.SkipReason);
        Assert.Empty(_streamer.Calls);
        Assert.Contains(_log.Lines, l => l.StartsWith("Info: a: prewarm skipped (", StringComparison.Ordinal) && l.Contains(r.SkipReason!, StringComparison.Ordinal));
        Assert.Equal("skipped", ((JObject)((JObject)((JObject)JsonRead.ReadFile(StatusPath)!)["games"]!)["a"]!)["result"]);
    }

    [Fact]
    public void StreamFailureIsLoggedAndRecordedNotThrownAndDoesNotStartTheCooldown()
    {
        _streamer.Throw = new IOException("disk gone");
        GameSettings g = Game("a");
        Prewarmer p = Make(g);

        PrewarmResult r = p.Run(g, "service start");
        _streamer.Throw = null;
        PrewarmResult again = p.Run(g, "retry");

        Assert.False(r.Ran);
        Assert.StartsWith("failed", r.SkipReason, StringComparison.Ordinal);
        Assert.Contains(_log.Lines, l => l.StartsWith("Error: a: prewarm failed", StringComparison.Ordinal));
        Assert.True(again.Ran);
    }

    [Theory]
    [InlineData("trigger X.exe", "X.exe")]
    [InlineData("service start", "service start")]
    [InlineData("auto after relocation", "after relocation")]
    [InlineData("rewarm", "rewarm")]
    [InlineData("auto cold start", "cold start")]
    public void PrewarmLogNamesTheTriggerCause(string why, string cause)
    {
        GameSettings g = Game("a");

        PrewarmResult r = Make(g).Run(g, why);

        Assert.Equal(cause, Prewarmer.TriggerCause(why));
        Assert.True(r.Ran);
        Assert.Contains(_log.Lines, l => l.StartsWith($"Info: a: prewarm triggered by {cause}", StringComparison.Ordinal));
    }

    [Fact]
    public void ColdPrewarmLogsTheTriggerCauseBeforeReading()
    {
        GameSettings g = Game("a");
        var p = new Prewarmer([g], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => AvailableMib,
            GameRunning = _ => false,
            ZoneBytes = _ => null,
            TraceExists = _ => false,
            ColdPlanner = (_, _, _, _) => new ColdPlan("full", [new ReadOp(Path.Combine(_dir, "a.bin"), 0, 1 << 20)], 1 << 20, null),
            ColdRead = (_, _) => (new AsterCache.Prefetch.StreamStats(1 << 20, 1, 0, 0.01), true),
        }, _log, null, () => _now);

        PrewarmResult r = p.Run(g, "auto cold start");

        Assert.True(r.Ran);
        Assert.Contains(_log.Lines, l => l.StartsWith("Info: a: prewarm triggered by cold start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerRunsTheServiceStartWarmupWithoutBlockingTheWatcher()
    {
        GameSettings g = Game("a");
        Prewarmer p = Make(g);
        var worker = new Worker(new AsterSettings(50, [g]), _log, new IdleWatcher(), null, new FakeStreamer(), new ReapplyChecker(ReapplyTests.NoIoEnv(), _log), prewarm: p);

        await worker.StartAsync(CancellationToken.None);
        for (int i = 0; i < 300 && (_streamer.Calls.IsEmpty || !File.Exists(StatusPath)); i++) // 상태 파일 쓰기까지 기다림: 정리가 쓰는 중인 임시 파일과 겹치지 않게
        {
            await Task.Delay(10);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(["a"], _streamer.Calls.Select(c => c.Game));
    }

    [Fact]
    public void CancelledTokenSkipsTheStreamWithoutRecordingAStatus()
    {
        GameSettings g = Game("a");
        Prewarmer p = Make(g);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        PrewarmResult r = p.Run(g, "test", ct: cts.Token);

        Assert.Equal((false, Prewarmer.Cancelled), (r.Ran, r.SkipReason));
        Assert.Empty(_streamer.Calls);
        Assert.Null(p.LastWarmed("a"));
        Assert.False(File.Exists(StatusPath));
    }

    [Fact]
    public void CancellationAfterTheLastBatchIsRecordedAsSkippedNotOk()
    {
        GameSettings g = Game("a");
        using var cts = new CancellationTokenSource();
        var streamer = new CancelOnProgressStreamer(cts);
        var p = new Prewarmer([g], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => AvailableMib,
            GameRunning = _ => false,
            ZoneBytes = _ => 1 << 20,
            TraceExists = _ => true,
        }, _log, StatusPath, () => _now);

        PrewarmResult result = p.Run(g, "test", ct: cts.Token);
        var root = (JObject)JsonRead.ReadFile(StatusPath)!;
        var games = (JObject)root["games"]!;
        var status = (JObject)games["a"]!;

        Assert.Equal(1, streamer.Calls);
        Assert.True(streamer.StopObserved);
        Assert.False(result.Ran);
        Assert.Equal(Prewarmer.Cancelled, result.SkipReason);
        Assert.Equal("skipped", status["result"]);
        Assert.Null(p.LastWarmed("a"));
    }

    private sealed class CancelOnProgressStreamer(CancellationTokenSource cancel) : IGameStreamer
    {
        public int Calls { get; private set; }
        public bool StopObserved { get; private set; }

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            Calls++;
            progress?.Invoke(1, 1);
            cancel.Cancel();
            StopObserved = stop.IsCancellationRequested;
            return (new StreamStats(1, 1, 0, 0.01), true);
        }
    }

    [Fact]
    public async Task LastWarmedAnswersWhileAStreamIsStillRunning()
    {
        GameSettings g = Game("a");
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var p = new Prewarmer([g], new HoldingStreamer(started, release), new PrewarmEnv
        {
            AvailableRamMib = () => AvailableMib,
            GameRunning = _ => false,
            ZoneBytes = _ => ZoneBytes,
            TraceExists = _ => true,
        }, _log, null, () => _now);
        Task<PrewarmResult> run = Task.Run(() => p.Run(g, "test"));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        Task<DateTimeOffset?> probe = Task.Run(() => p.LastWarmed("a")); // 스트림이 도는 동안 틱 스레드가 물어도 막히지 않아야 함
        Assert.Same(probe, await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Null(await probe);

        release.Set();
        Assert.True((await run).Ran);
        Assert.NotNull(p.LastWarmed("a"));
    }

    private sealed class HoldingStreamer(ManualResetEventSlim started, ManualResetEventSlim release) : IGameStreamer
    {
        public AsterCache.Prefetch.StreamStats Stream(GameSettings game)
        {
            started.Set();
            release.Wait(TimeSpan.FromSeconds(30));
            return new AsterCache.Prefetch.StreamStats(100, 2, 0, 0.01);
        }
    }

    private sealed class IdleWatcher : IProcessWatcher
    {
        public async Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 취소는 정상 종료
            }
        }
    }
}
