using AsterCache.Core.Json;
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
        GameSettings g = Game("a") with { MaxStreamMib = maxMib };

        Assert.Equal((long)expectedMib << 20, PrefetchStreamer.Budget(g, zoneMib < 0 ? null : (long)zoneMib << 20, (long)traceMib << 20));
    }

    [Fact]
    public void CooldownBlocksUntilItHasPassed()
    {
        Assert.StartsWith("cooldown", PrewarmPolicy.SkipReason(1 << 20, true, false, 99999, 0, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)), StringComparison.Ordinal);
        Assert.Null(PrewarmPolicy.SkipReason(1 << 20, true, false, 99999, 0, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30)));
        Assert.Null(PrewarmPolicy.SkipReason(1 << 20, true, false, 99999, 0, TimeSpan.Zero, TimeSpan.Zero));
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
