using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Service;

/// 최근 게임 예열 선택, 게임 시작 처리, 설정 교체, 워커 연결.
public sealed class AutoModeTests : IDisposable
{
    private readonly AutoRig _rig = new();

    public AutoModeTests() => _rig.Editor.SetIoSummaryEnabled(false);

    public void Dispose() => _rig.Dispose();

    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private AutoMode Mode() => new(_rig.Editor, _rig.World.Env(), _rig.Status, _rig.Log, _rig.Settings);

    private void AddGame(string name, string source, bool onServiceStart = false, IReadOnlyList<string>? triggers = null)
    {
        _rig.Editor.Add(_rig.Input(name, name + ".exe", @"D:\Games\" + name, source, triggers) with { OnServiceStart = onServiceStart });
        _rig.Reload();
    }

    private void Played(string name, int hoursAgo) => _rig.Status.MarkPlayed(name, "auto", AutoStage.WaitingIdle, T0 - TimeSpan.FromHours(hoursAgo));

    private static IEnumerable<string> Names(IEnumerable<GameSettings> games) => games.Select(g => g.Name);

    private Task StartSavingTrace(AutoMode mode, ManualResetEventSlim saveStarted, ManualResetEventSlim finishSave)
    {
        _rig.World.StartTraceFactory = number =>
        {
            var trace = new FakeTrace();
            if (number == 1)
            {
                trace.Records.Add(TraceRecord.Start(DateTime.UtcNow, 100, 1, "foo.exe"));
                trace.Records.Add(TraceRecord.Read(DateTime.UtcNow, 100, @"D:\Games\foo\a.bin", 0, 65536));
            }

            return trace;
        };
        _rig.World.OnCanonicalize = () =>
        {
            saveStarted.Set();
            finishSave.Wait();
        };

        mode.Tracer.Start(Game("foo"));
        mode.Tracer.Step();
        _rig.World.Now += GameExitTracker.Grace;
        Task saving = Task.Run(mode.Tracer.Step);
        Assert.True(saveStarted.Wait(TimeSpan.FromSeconds(30)));
        return saving;
    }

    private Task StartSavingHeldTrace(AutoMode mode, ManualResetEventSlim saveStarted, ManualResetEventSlim finishSave)
    {
        _rig.World.StartTraceFactory = _ => new FakeTrace();
        mode.Tracer.Start(Game("foo"));
        _rig.World.Running = true;
        mode.Tracer.Step();

        AsterSettings original = _rig.Settings;
        mode.SetSettings(original with { Games = [original.Games[0] with { Paused = true }] });
        mode.Tracer.Step(); // 정책 변경으로 세션을 멈춰 저장 보류 목록에 둠
        mode.SetSettings(original);

        _rig.World.Running = false;
        mode.Tracer.Step(); // 종료 시각을 기록
        _rig.World.Now += GameExitTracker.Grace;
        _rig.World.OnCanonicalize = () =>
        {
            saveStarted.Set();
            finishSave.Wait();
        };
        Task saving = Task.Run(mode.Tracer.Step);
        Assert.True(saveStarted.Wait(TimeSpan.FromSeconds(30)));
        return saving;
    }

    [Fact]
    public async Task RegisteringANewAutoGameWakesTheTickAtOnceButOtherReloadsDoNot()
    {
        AutoMode mode = Mode();
        mode.SetSettings(_rig.Reload()); // 새 게임 없음
        Assert.False(await mode.WaitForTickAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));

        _rig.Editor.Add(_rig.Input("g", "g.exe", @"D:\Games\g", "auto", null));
        mode.SetSettings(_rig.Reload());

        Assert.True(await mode.WaitForTickAsync(TimeSpan.FromSeconds(30), CancellationToken.None));
        Assert.False(await mode.WaitForTickAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None)); // 요청은 한 번에 하나로 합쳐짐
    }

    // ---- 기록 없는 예열의 상태 표시 ----

    [Theory]
    [InlineData("full", "prewarmed (full)")]
    [InlineData("profile Unreal", "prewarmed (profile Unreal)")]
    public void ColdPrewarmFromAnyPathShowsTheModeInAutoStatus(string label, string expected)
    {
        AddGame("g", "auto");
        var prewarm = new Prewarmer(_rig.Settings.Games, new FakeStreamer(), new PrewarmEnv
        {
            AvailableRamMib = () => 99999,
            GameRunning = _ => false,
            ZoneBytes = _ => null,
            TraceExists = _ => false,
            ColdPlanner = (_, _, _, _) => new AsterCache.Prefetch.ColdPlan(label, [new AsterCache.Core.Prefetch.ReadOp(@"D:\Games\g.bin", 0, 1 << 20)], 1 << 20, null),
            ColdRead = (ops, _) => (new AsterCache.Prefetch.StreamStats(1 << 20, 1, 0, 0.1), true),
        }, _rig.Log, null, () => T0);
        _ = new AutoMode(_rig.Editor, _rig.World.Env(), _rig.Status, _rig.Log, _rig.Settings, prewarm);
        _rig.Status.Set("g", "auto", null, AutoStage.Discovered, null);

        prewarm.RunServiceStart([_rig.Settings.Games[0]]); // 서비스 시작 경로 (틱 밖)

        Assert.Equal((AutoStage.Discovered, expected), (_rig.Status.Get("g")!.Stage, _rig.Status.Get("g")!.Detail));
    }

    [Fact]
    public void AutoModeHandsItsRelocatorRecoveryToThePrewarmer()
    {
        var prewarm = new Prewarmer(_rig.Settings.Games, new FakeStreamer(), new PrewarmEnv
        {
            AvailableRamMib = () => 99999,
            GameRunning = _ => false,
            ZoneBytes = _ => null,
            TraceExists = _ => false,
            ColdPlanner = (_, _, _, _) => new AsterCache.Prefetch.ColdPlan("full", [], 0, null),
            ColdRead = (ops, _) => (new AsterCache.Prefetch.StreamStats(0, 0, 0, 0.1), true),
        }, _rig.Log, null, () => T0);

        _ = new AutoMode(_rig.Editor, _rig.World.Env(), _rig.Status, _rig.Log, _rig.Settings, prewarm);

        Assert.NotNull(prewarm.Recovery);
    }

    // ---- 최근 게임 선택 ----

    [Fact]
    public void RecentIsNewestFirstLimitedToNAndSkipsGamesNeverPlayed()
    {
        foreach (string n in new[] { "a", "b", "c", "d" })
        {
            AddGame(n, "auto");
        }

        Played("a", 5);
        Played("b", 1);
        Played("c", 3);

        Assert.Equal(["b", "c"], Names(AutoMode.Recent(_rig.Settings.Games, _rig.Status.LastPlayed(), 2)));
        Assert.Equal(["b", "c", "a"], Names(AutoMode.Recent(_rig.Settings.Games, _rig.Status.LastPlayed(), 8)));
        Assert.Equal(["b", "c", "a"], Names(AutoMode.Recent(_rig.Settings.Games, _rig.Status.LastPlayed(), 0))); // 0 = 개수 제한 없음 (예산이 정함)
    }

    [Fact]
    public void StartupRecentLeavesOutGamesTheServiceStartAlreadyWarms()
    {
        AddGame("warm", "auto", onServiceStart: true);
        AddGame("x", "auto");
        AddGame("y", "manual");
        Played("warm", 1);
        Played("x", 2);
        Played("y", 3);

        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { PrewarmRecent = 2 } };
        Assert.Equal(["x", "y"], Names(Mode().StartupRecent()));

        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { PrewarmRecent = 0 } };
        Assert.Equal(["x", "y"], Names(Mode().StartupRecent())); // 0 = 가능한 만큼, 최근 순
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { Enabled = false } };
        Assert.Empty(Mode().StartupRecent());
    }

    [Fact]
    public void TriggerWarmsEveryManualGameButOnlyTheNewestAutoGames()
    {
        AddGame("m1", "manual", triggers: ["steam.exe"]);
        AddGame("m2", "manual", triggers: ["steam.exe"]);
        AddGame("a1", "auto", triggers: ["steam.exe"]);
        AddGame("a2", "auto", triggers: ["steam.exe"]);
        AddGame("a3", "auto", triggers: ["steam.exe"]);
        Played("a1", 9);
        Played("a2", 1);
        Played("a3", 5);
        IReadOnlyList<GameSettings> hit = _rig.Settings.Games;

        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { PrewarmRecent = 2 } };
        Assert.Equal(["m1", "m2", "a2", "a3"], Names(Mode().NarrowTriggered(hit)));

        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { PrewarmRecent = 0 } };
        Assert.Equal(["m1", "m2", "a2", "a3", "a1"], Names(Mode().NarrowTriggered(hit))); // 0 = 가능한 만큼, 최근 순

        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { Enabled = false } };
        Assert.Equal(["m1", "m2", "a1", "a2", "a3"], Names(Mode().NarrowTriggered(hit)));
    }

    [Fact]
    public void PrewarmerStreamsOnlyTheGamesTheTriggerNarrowingKept()
    {
        AddGame("m1", "manual", triggers: ["steam.exe"]);
        AddGame("a1", "auto", triggers: ["steam.exe"]);
        AddGame("a2", "auto", triggers: ["steam.exe"]);
        Played("a1", 1);
        Played("a2", 2);
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { PrewarmRecent = 1 } };
        var streamer = new FakeStreamer();
        var prewarm = new Prewarmer(_rig.Settings.Games, streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 99999,
            GameRunning = _ => false,
            ZoneBytes = _ => 1 << 20,
            TraceExists = _ => true,
        }, _rig.Log);

        prewarm.OnProcessStart(new ProcessStart(1, "steam.exe", 0), Mode().NarrowTriggered);

        Assert.Equal(["m1", "a1"], streamer.Calls.Select(c => c.Game));
    }

    // ---- 게임 시작 ----

    private GameSettings Game(string name = "foo") => _rig.Settings.Games.Single(g => g.Name == name);

    [Fact]
    public async Task GameStartRecordsLastPlayedAndOpensAnAutoTraceWhenTheRawTraceIsMissing()
    {
        AddGame("foo", "auto");
        _rig.World.Session = new FakeTrace();
        _rig.World.Now = T0;

        await Mode().OnGameStart(Game());

        Assert.Equal(T0, _rig.Status.Get("foo")!.LastPlayed);
        Assert.Equal(AutoStage.Tracing, _rig.Status.Get("foo")!.Stage); // auto.learn 기본 꺼짐 (E7 재측정 전): 일반 트레이스 단계
        Assert.Equal(1, _rig.World.TraceStarts);
    }

    [Fact]
    public async Task GameStartWithATraceAlreadyOnDiskOnlyRecordsThePlay()
    {
        AddGame("foo", "manual");
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { TraceRuns = 1 } };
        string raw = AutoTracer.RawTracePath(Game());
        _rig.World.Files.UnionWith([raw, TraceStageIndex.IndexPath(raw)]);

        await Mode().OnGameStart(Game());

        Assert.Equal((AutoStage.WaitingIdle, "manual"), (_rig.Status.Get("foo")!.Stage, _rig.Status.Get("foo")!.Source));
        Assert.NotNull(_rig.Status.Get("foo")!.LastPlayed);
        Assert.Equal(0, _rig.World.TraceStarts);
    }

    [Fact]
    public async Task GameStartLogsWhyNoTraceIsNeededOnlyOnceForTheSameReason()
    {
        AddGame("foo", "manual");
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { TraceRuns = 1 } };
        string raw = AutoTracer.RawTracePath(Game());
        _rig.World.Files.UnionWith([raw, TraceStageIndex.IndexPath(raw)]);
        AutoMode mode = Mode();

        await mode.OnGameStart(Game());
        await mode.OnGameStart(Game());

        Assert.Single(_rig.Log.Lines, line => line.Contains("foo: auto trace not needed (valid ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GameStartWaitsForAPreviousTraceSaveThenStartsOnce()
    {
        AddGame("foo", "auto");
        AddGame("bar", "auto");
        AutoMode mode = Mode();
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { LoadBoundary = true } };
        mode.SetSettings(_rig.Settings);
        using var saveStarted = new ManualResetEventSlim();
        using var finishSave = new ManualResetEventSlim();
        Task saving = StartSavingTrace(mode, saveStarted, finishSave);
        _rig.World.RunningExes.Add("bar.exe");
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var boundaryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? waitLimit = null;
        mode.BoundaryRunner = (_, _) => boundaryStarted.TrySetResult();
        mode.TraceFinishWaiter = async (finished, timeout, cancellationToken) =>
        {
            waitLimit = timeout;
            waitStarted.TrySetResult();
            await finished.WaitAsync(cancellationToken);
            return true;
        };

        Task starting = Task.Run(() => mode.OnGameStart(Game("bar"), isLatestRunningStart: () => true));
        await Task.WhenAll(waitStarted.Task, boundaryStarted.Task).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(finishSave.IsSet);
        Assert.Equal(1, _rig.World.TraceStarts);

        finishSave.Set();
        await Task.WhenAll(saving, starting).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(AutoMode.PreviousTraceFinishTimeout, waitLimit);
        Assert.Equal(2, _rig.World.TraceStarts);
        Assert.True(File.Exists(AutoTracer.RawTracePath(Game("foo"))));
        Assert.Contains(_rig.Log.Lines, line => line.Contains("bar: auto trace start resumed after waiting for the previous trace", StringComparison.Ordinal));
    }

    // 대기 중 일어나는 변화 (settings 와 world 를 고침) 를 주고, 대기 뒤 트레이스가 시작됐는지 돌려줌. 게임 시작 시점의 WantsRun 은 true.
    private async Task<bool> StartsAfterWaitingWhile(Action<AutoMode> duringWait)
    {
        AddGame("foo", "auto");
        AddGame("bar", "auto");
        AutoMode mode = Mode();
        using var saveStarted = new ManualResetEventSlim();
        using var finishSave = new ManualResetEventSlim();
        Task saving = StartSavingTrace(mode, saveStarted, finishSave);
        _rig.World.RunningExes.Add("bar.exe");
        mode.TraceFinishWaiter = async (finished, _, cancellationToken) =>
        {
            await finished.WaitAsync(cancellationToken);
            duringWait(mode);
            return true;
        };

        Task starting = Task.Run(() => mode.OnGameStart(Game("bar"), isLatestRunningStart: () => true));
        await Task.Delay(100); // 대기에 들어갈 시간
        finishSave.Set();
        await Task.WhenAll(saving, starting).WaitAsync(TimeSpan.FromSeconds(30));
        return _rig.World.TraceStarts == 2;
    }

    [Fact]
    public async Task GameStartKeepsItsWantsRunDecisionAfterWaitingEvenIfThePreviousRunChangesIt()
    {
        bool started = await StartsAfterWaitingWhile(mode => mode.SetSettings(_rig.Settings with { Auto = _rig.Settings.Auto with { TraceRuns = 0, TraceRunsMax = 0 } })); // 직전 실행 저장 뒤 WantsRun 이 false 가 되는 상황

        Assert.True(started);
        Assert.Contains(_rig.Log.Lines, line => line.Contains("bar: auto trace start resumed after waiting for the previous trace", StringComparison.Ordinal));
    }

    // 요약 전용으로 정한 시작 (bar) 이 앞선 저장 (foo) 을 기다리는 사이 duringWait 가 설정을 바꿈. 돌려주는 값: (원시 세션 수, 요약 세션 수), 원시 세션 수에는 foo 의 1 개가 들어 있음.
    private async Task<(int Raw, int Summary)> SummaryOnlyStartAfterWaitingWhile(Func<AsterSettings, AsterSettings> duringWait)
    {
        AddGame("foo", "auto");
        AddGame("bar", "auto");
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { TraceRuns = 1, IoSummaryEnabled = true } };
        AutoMode mode = Mode();
        string raw = AutoTracer.RawTracePath(Game("bar"));
        Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
        _rig.World.Files.UnionWith([raw, TraceStageIndex.IndexPath(raw)]);
        IoSummaryStore.WriteTried(raw, IoSummaryStore.MaxTried); // 읽기 방식 보강은 끝남: 원시 트레이스를 더 뜰 이유가 없고 요약만 남길 차례
        _rig.World.SummarySession = new FakeTrace();
        using var saveStarted = new ManualResetEventSlim();
        using var finishSave = new ManualResetEventSlim();
        Task saving = StartSavingTrace(mode, saveStarted, finishSave);
        _rig.World.RunningExes.Add("bar.exe");
        mode.TraceFinishWaiter = async (finished, _, cancellationToken) =>
        {
            await finished.WaitAsync(cancellationToken);
            mode.SetSettings(duringWait(_rig.Settings));
            return true;
        };

        Task starting = Task.Run(() => mode.OnGameStart(Game("bar"), isLatestRunningStart: () => true));
        await Task.Delay(100); // 대기에 들어갈 시간
        finishSave.Set();
        await Task.WhenAll(saving, starting).WaitAsync(TimeSpan.FromSeconds(30));
        return (_rig.World.TraceStarts, _rig.World.SummaryStarts);
    }

    [Fact]
    public async Task ASummaryOnlyStartThatWaitedStillOpensOnlyTheSummarySession()
    {
        Assert.Equal((1, 1), await SummaryOnlyStartAfterWaitingWhile(s => s));
    }

    [Fact]
    public async Task TurningTheSummaryOffWhileASummaryOnlyStartWaitsOpensNoSessionAndNeverARawOne()
    {
        Assert.Equal((1, 0), await SummaryOnlyStartAfterWaitingWhile(s => s with { Auto = s.Auto with { IoSummaryEnabled = false } }));
    }

    [Fact]
    public async Task ARawTraceConditionThatTurnsTrueWhileASummaryOnlyStartWaitsDoesNotMakeItARawSession()
    {
        // trace_runs 를 올려 NextTrace 가 참이 됨: 원시 세션을 열면 게임 프로세스 읽기량 조회 경로로 들어가므로 요약 전용 그대로여야 함
        (int raw, int summary) = await SummaryOnlyStartAfterWaitingWhile(s => s with { Auto = s.Auto with { TraceRuns = 5, TraceRunsMax = 5 } });

        Assert.Equal((1, 1), (raw, summary));
    }

    [Fact]
    public async Task AnAntiCheatSettledAsForbiddenWhileAStartWaitsOpensNoSession()
    {
        (int raw, int summary) = await SummaryOnlyStartAfterWaitingWhile(s => s with { Games = [.. s.Games.Select(g => g.Name == "bar" ? g with { AntiCheat = "eac" } : g)] });

        Assert.Equal((1, 0), (raw, summary));
    }

    [Fact]
    public async Task GameStartDoesNotStartAfterWaitingWhenTracingWasTurnedOff()
    {
        Assert.False(await StartsAfterWaitingWhile(mode => mode.SetSettings(_rig.Settings with { Auto = _rig.Settings.Auto with { Trace = false } })));
    }

    [Fact]
    public async Task GameStartDoesNotStartAfterWaitingWhenTheGameWasPaused()
    {
        Assert.False(await StartsAfterWaitingWhile(mode => mode.SetSettings(_rig.Settings with { Games = _rig.Settings.Games.Select(g => g.Name == "bar" ? g with { Paused = true } : g).ToList() })));
    }

    [Fact]
    public async Task GameStartDoesNotStartAfterWaitingWhenAnAntiCheatBlocksTheTrace()
    {
        Assert.False(await StartsAfterWaitingWhile(mode => mode.SetSettings(_rig.Settings with { Games = _rig.Settings.Games.Select(g => g.Name == "bar" ? g with { AntiCheat = "eac" } : g).ToList() })));
    }

    [Fact]
    public async Task RestartedGameUsesOneBoundedWaitForThePreviousTrace()
    {
        AddGame("foo", "auto");
        AutoMode mode = Mode();
        _rig.World.StartTraceFactory = _ => new FakeTrace();
        _rig.World.Running = true;
        mode.Tracer.Start(Game("foo"));
        mode.EndRestartedGame(Game("foo"), _rig.World.Now.AddSeconds(1));

        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int waits = 0;
        TimeSpan? waitLimit = null;
        mode.TraceFinishWaiter = async (finished, timeout, cancellationToken) =>
        {
            waitLimit = timeout;
            Interlocked.Increment(ref waits);
            waitStarted.TrySetResult();
            await finished.WaitAsync(cancellationToken);
            return true;
        };
        Task starting = Task.Run(() => mode.OnGameStart(Game("foo")));
        await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        mode.Tracer.Step(); // 재실행 분리에서 요청된 종료를 마무리
        await starting.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, waits);
        Assert.Equal(AutoMode.PreviousTraceFinishTimeout, waitLimit);
        Assert.Equal(2, _rig.World.TraceStarts);
    }

    [Fact]
    public async Task GameStartDoesNotRetryAfterTheGameExitsWhileWaiting()
    {
        AddGame("foo", "auto");
        AddGame("bar", "auto");
        AutoMode mode = Mode();
        using var saveStarted = new ManualResetEventSlim();
        using var finishSave = new ManualResetEventSlim();
        Task saving = StartSavingTrace(mode, saveStarted, finishSave);
        _rig.World.RunningExes.Add("bar.exe");
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mode.TraceFinishWaiter = async (finished, _, cancellationToken) =>
        {
            waitStarted.TrySetResult();
            await finished.WaitAsync(cancellationToken);
            return true;
        };

        Task starting = Task.Run(() => mode.OnGameStart(Game("bar"), isLatestRunningStart: () => true));
        await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // 감시기 목록에 아직 남은 exe 와 실제 실행 상태를 분리해 확인.
        _rig.World.RunningExes.Remove("bar.exe");
        finishSave.Set();
        await Task.WhenAll(saving, starting).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, _rig.World.TraceStarts);
        Assert.Contains(_rig.Log.Lines, line => line.Contains("bar: auto trace start abandoned after waiting for the previous trace (game exited before retry)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GameStartWaitsForASavingStoppedTraceThenStartsOnce()
    {
        AddGame("foo", "auto");
        AutoMode mode = Mode();
        using var saveStarted = new ManualResetEventSlim();
        using var finishSave = new ManualResetEventSlim();
        Task saving = StartSavingHeldTrace(mode, saveStarted, finishSave);
        _rig.World.Running = true;
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mode.TraceFinishWaiter = async (finished, _, cancellationToken) =>
        {
            waitStarted.TrySetResult();
            await finished.WaitAsync(cancellationToken);
            return true;
        };

        Task starting = Task.Run(() => mode.OnGameStart(Game("foo")));
        await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(1, _rig.World.TraceStarts);

        finishSave.Set();
        await Task.WhenAll(saving, starting).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, _rig.World.TraceStarts);
        Assert.Contains(_rig.Log.Lines, line => line.Contains("foo: auto trace start resumed after waiting for the previous trace", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OlderGameStartResumingAfterANewerStartKeepsTheNewLoadBoundaryWatch()
    {
        AddGame("foo", "auto");
        AutoMode mode = Mode();
        using var saveStarted = new ManualResetEventSlim();
        using var finishSave = new ManualResetEventSlim();
        Task saving = StartSavingHeldTrace(mode, saveStarted, finishSave);
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { LoadBoundary = true } };
        mode.SetSettings(_rig.Settings);
        _rig.World.Running = true;
        int latestStart = 1;
        var waitStarted = new SemaphoreSlim(0, 2);
        mode.TraceFinishWaiter = async (finished, _, cancellationToken) =>
        {
            waitStarted.Release();
            await finished.WaitAsync(cancellationToken);
            return true;
        };
        var boundaryStarted = new SemaphoreSlim(0, 2);
        var tokens = new List<CancellationToken>();
        var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _rig.World.StartTraceFactory = number =>
        {
            if (number == 2)
            {
                retryStarted.TrySetResult();
            }

            return new FakeTrace();
        };
        mode.BoundaryRunner = (_, token) =>
        {
            lock (tokens)
            {
                tokens.Add(token);
            }

            boundaryStarted.Release();
            token.WaitHandle.WaitOne();
        };

        Task older = Task.Run(() => mode.OnGameStart(Game("foo"), isLatestRunningStart: () => Volatile.Read(ref latestStart) == 1));
        await Task.WhenAll(waitStarted.WaitAsync(TimeSpan.FromSeconds(30)), boundaryStarted.WaitAsync(TimeSpan.FromSeconds(30)));
        Volatile.Write(ref latestStart, 2);
        Task newer = Task.Run(() => mode.OnGameStart(Game("foo"), isLatestRunningStart: () => Volatile.Read(ref latestStart) == 2));
        await Task.WhenAll(waitStarted.WaitAsync(TimeSpan.FromSeconds(30)), boundaryStarted.WaitAsync(TimeSpan.FromSeconds(30)));

        finishSave.Set();
        await Task.WhenAll(saving, older).WaitAsync(TimeSpan.FromSeconds(30));
        await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(2, _rig.World.TraceStarts);
        Assert.True(tokens[0].IsCancellationRequested);
        Assert.False(tokens[1].IsCancellationRequested);
        mode.NoteGameExit(Game("foo"));
        Assert.True(tokens[1].IsCancellationRequested);
        await newer.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task GameStartGivesUpWhenThePreviousTraceWaitTimesOut()
    {
        AddGame("foo", "auto");
        AddGame("bar", "auto");
        AutoMode mode = Mode();
        using var saveStarted = new ManualResetEventSlim();
        using var finishSave = new ManualResetEventSlim();
        Task saving = StartSavingTrace(mode, saveStarted, finishSave);
        _rig.World.RunningExes.Add("bar.exe");
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TimeSpan? waitLimit = null;
        mode.TraceFinishWaiter = (_, timeout, _) =>
        {
            waitLimit = timeout;
            waitStarted.TrySetResult();
            return Task.FromResult(false);
        };

        await mode.OnGameStart(Game("bar"));
        await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        finishSave.Set();
        await saving.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(AutoMode.PreviousTraceFinishTimeout, waitLimit);
        Assert.Equal(1, _rig.World.TraceStarts);
        Assert.Contains(_rig.Log.Lines, line => line.Contains("bar: auto trace start abandoned after waiting for the previous trace (previous trace did not finish within 30s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GameStartDoesNotWaitWhenAnotherGameHasALiveTrace()
    {
        AddGame("foo", "auto");
        AddGame("bar", "auto");
        AutoMode mode = Mode();
        _rig.World.StartTraceFactory = _ => new FakeTrace();
        _rig.World.RunningExes.Add("bar.exe");
        mode.Tracer.Start(Game("foo"));
        int waits = 0;
        mode.TraceFinishWaiter = (_, _, _) =>
        {
            waits++;
            return Task.FromResult(true);
        };

        await mode.OnGameStart(Game("bar"));

        Assert.Equal(0, waits);
        Assert.Equal(1, _rig.World.TraceStarts);
        Assert.DoesNotContain(_rig.Log.Lines, line => line.Contains("bar: auto trace start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GameStartWaitIsCancelledByServiceShutdown()
    {
        AddGame("foo", "auto");
        AddGame("bar", "auto");
        AutoMode mode = Mode();
        using var saveStarted = new ManualResetEventSlim();
        using var finishSave = new ManualResetEventSlim();
        Task saving = StartSavingTrace(mode, saveStarted, finishSave);
        _rig.World.RunningExes.Add("bar.exe");
        var waitStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mode.TraceFinishWaiter = async (_, _, cancellationToken) =>
        {
            waitStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return true;
        };

        Task starting = Task.Run(() => mode.OnGameStart(Game("bar")));
        await waitStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        mode.Shutdown();
        await starting.WaitAsync(TimeSpan.FromSeconds(30));
        finishSave.Set();
        await saving.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, _rig.World.TraceStarts);
        Assert.Contains(_rig.Log.Lines, line => line.Contains("bar: auto trace start abandoned after waiting for the previous trace (service shutdown)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackedOffGameStartRecordsThePlayButOpensNoTraceOrBoundaryRead()
    {
        AddGame("foo", "auto");
        _rig.World.Session = new FakeTrace();
        _rig.World.Now = T0;

        await Mode().OnGameStart(Game(), backOff: true);

        Assert.Equal(T0, _rig.Status.Get("foo")!.LastPlayed);
        Assert.Equal(0, _rig.World.TraceStarts);
        Assert.Contains(_rig.Log.Lines, line => line.Contains("foo: auto trace skipped (quick exit guard: backing off)", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task GameStartHonorsTheTraceAndEnabledSwitches(bool traceOn, bool enabled)
    {
        AddGame("foo", "auto");
        _rig.World.Session = new FakeTrace();
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { Trace = traceOn, Enabled = enabled } };

        await Mode().OnGameStart(Game());

        Assert.Equal(0, _rig.World.TraceStarts);
        Assert.Equal(enabled, _rig.Status.Get("foo") is not null);
    }

    [Fact]
    public async Task ExceptionsInsideAnAutoActionAreLoggedNotThrown()
    {
        AddGame("foo", "auto");
        _rig.World.StartTraceThrows = new InvalidOperationException("boom");

        await Mode().OnGameStart(Game()); // 세션 시작 실패는 failed 로 기록됨
        Mode().RelocateTick(new ReapplyGate(), CancellationToken.None); // 가짜 세계가 CpuPercent 만 쓰고 끝남

        Assert.Equal(AutoStage.Failed, _rig.Status.Get("foo")!.Stage);
    }

    [Fact]
    public void SettingsReplacementForgetsGamesThatLeftTheConfig()
    {
        AddGame("keep", "auto");
        AddGame("gone", "auto");
        _rig.Status.Set("keep", "auto", null, AutoStage.Discovered, null);
        _rig.Status.Set("gone", "auto", null, AutoStage.Discovered, null);
        AutoMode mode = Mode();
        _rig.Editor.Remove("gone");

        mode.SetSettings(_rig.Reload());

        Assert.NotNull(_rig.Status.Get("keep"));
        Assert.Null(_rig.Status.Get("gone"));
    }

    // ---- 워커 연결 ----

    [Fact]
    public async Task WorkerRecordsPlaysAndWarmsTheRecentGamesAfterTheServiceStartWarmup()
    {
        AddGame("warm", "auto", onServiceStart: true);
        AddGame("recent", "auto");
        AddGame("old", "auto");
        Played("warm", 9);
        Played("recent", 1);
        Played("old", 9);
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { PrewarmRecent = 1 } };
        var streamer = new FakeStreamer();
        var prewarm = new Prewarmer(_rig.Settings.Games, streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 99999,
            GameRunning = _ => false,
            ZoneBytes = _ => 1 << 20,
            TraceExists = _ => true,
        }, _rig.Log, null, () => T0); // 가짜 시계: 실제 시각이면 마지막 플레이가 14 일 창 (StartupPlayedWithin) 밖으로 흘러 시험이 어느 날부터 실패함
        var emitted = new TaskCompletionSource();
        string warmRaw = AutoTracer.RawTracePath(Game("warm"));
        _rig.World.Files.UnionWith([warmRaw, TraceStageIndex.IndexPath(warmRaw)]);
        var worker = new Worker(_rig.Settings, _rig.Log, new OneShotStart(new ProcessStart(7, "warm.exe", 0), emitted, () => streamer.Calls.Count >= 2), null, new FakeStreamer(),
            new ReapplyChecker(ReapplyTests.NoIoEnv(), _rig.Log), prewarm: prewarm, auto: Mode());

        await worker.StartAsync(CancellationToken.None);
        await emitted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        for (int i = 0; i < 3000 && _rig.Status.Get("warm")!.LastPlayed < T0; i++)
        {
            await Task.Delay(10);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(["recent", "warm"], streamer.Calls.Select(c => c.Game)); // 시작 예열도 마지막 플레이 순 (on_service_start 라고 앞서지 않음)
        Assert.Equal(T0, _rig.Status.Get("warm")!.LastPlayed); // 게임 시작 알림이 last_played 를 기록함 (가짜 시계)
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void WorkerHoldsNewPrewarmForAKeptReplanOnlyWhileAutoModeIsOn(bool enabled, bool expected)
    {
        AddGame("g", "auto");
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { Enabled = enabled } };
        var prewarm = new Prewarmer(_rig.Settings.Games, new FakeStreamer(), new PrewarmEnv
        {
            AvailableRamMib = () => 99999,
            GameRunning = _ => false,
            ZoneBytes = _ => 1 << 20,
            TraceExists = _ => true,
        }, _rig.Log);
        _rig.Status.Set("g", "auto", null, AutoStage.Discovered, null);
        _rig.Status.ResetForReplan("g", AutoRelocator.ReplanDetail + "; trace kept, planning again"); // 트레이스를 유지한 채 곧 다시 계획하는 상태

        _ = new Worker(_rig.Settings, _rig.Log, new OneShotStart(new ProcessStart(7, "g.exe", 0), new TaskCompletionSource(), () => false), null, new FakeStreamer(), new ReapplyChecker(ReapplyTests.NoIoEnv(), _rig.Log), prewarm: prewarm, auto: Mode());

        Assert.Equal(expected, prewarm.ReplanKept(_rig.Settings.Games[0])); // 자동 모드가 꺼지면 재계획이 돌지 않으니 영영 풀리지 않는 보류를 걸지 않음
    }

    // warmDone 이 참이 된 뒤에 시작을 알립니다: 게임 시작은 예열을 취소하므로, 서비스 시작 예열이 끝나기 전에 알리면 순서가 시간에 달림.
    private sealed class OneShotStart(ProcessStart start, TaskCompletionSource emitted, Func<bool> warmDone) : IProcessWatcher
    {
        public async Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
        {
            try
            {
                while (!warmDone())
                {
                    await Task.Delay(1, cancellationToken);
                }

                onStart(start);
                emitted.TrySetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 취소는 정상 종료
            }
        }
    }

    [Fact]
    public async Task GameStartKeepsRecordingExtraRunsUntilTraceRunsIsReachedAndLeavesTheStageAlone()
    {
        AddGame("foo", "manual");
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { TraceRuns = 3 } };
        _rig.World.Files.Add(AutoTracer.RawTracePath(Game())); // 첫 기록 1 회
        _rig.World.Session = new FakeTrace();
        _rig.Status.Complete("foo", "manual", T0, counted: false, null);

        await Mode().OnGameStart(Game());

        Assert.Equal(1, _rig.World.TraceStarts);
        Assert.Equal(AutoStage.Done, _rig.Status.Get("foo")!.Stage);
    }

    [Fact]
    public async Task GameStartTracesAnExtraRunOnlyWhileCoverageIsLowAndTheLastMergeAddedNewBlocks()
    {
        AddGame("foo", "auto");
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { TraceRuns = 2 } };
        string raw = AutoTracer.RawTracePath(Game());
        Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
        _rig.World.Files.UnionWith([raw, TraceStageIndex.IndexPath(raw), AsterCache.Tracer.TraceWriter.RunPath(raw, 1), AsterCache.Tracer.TraceWriter.RunPath(raw, 2)]);
        File.WriteAllText(AutoTracer.MergeMetaPath(Game()), "{\"run\": 2, \"new_mib\": 100}");
        _rig.World.Session = new FakeTrace();
        _rig.Status.Set("foo", "auto", null, AutoStage.Done, null);
        File.WriteAllText(AutoTracer.MergeMetaPath(Game()), "{\"run\": 2, \"new_mib\": 10}");
        _rig.Status.RecordCoverage("foo", 22);

        await Mode().OnGameStart(Game());
        Assert.Equal(0, _rig.World.TraceStarts); // 새 블록 10 MiB < 64

        File.WriteAllText(AutoTracer.MergeMetaPath(Game()), "{\"run\": 2, \"new_mib\": 100}");
        _rig.Status.RecordCoverage("foo", 100); // 짧은 재실행이 덮어쓴 값
        await Mode().OnGameStart(Game());

        Assert.Equal(1, _rig.World.TraceStarts);
        Assert.Equal(AutoStage.Done, _rig.Status.Get("foo")!.Stage);
    }

    [Fact]
    public async Task LoadBoundaryWatchStopsWhenTheGameExits()
    {
        AddGame("foo", "auto");
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { LoadBoundary = true } };
        _rig.World.Session = new FakeTrace();
        _rig.World.Running = true;
        AutoMode mode = Mode();
        var started = new TaskCompletionSource();
        CancellationToken watched = default;
        mode.BoundaryRunner = (_, ct) =>
        {
            watched = ct;
            started.TrySetResult();
            ct.WaitHandle.WaitOne();
        };

        Task run = Task.Run(() => mode.OnGameStart(Game()));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(watched.IsCancellationRequested);

        mode.NoteGameExit(Game());
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(watched.IsCancellationRequested);
    }

    [Fact]
    public async Task RestartingTheSameGameCancelsAndDisposesThePreviousLoadBoundaryWatch()
    {
        AddGame("foo", "auto");
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { LoadBoundary = true } };
        _rig.World.Session = new FakeTrace();
        _rig.World.Running = true;
        AutoMode mode = Mode();
        var tokens = new List<CancellationToken>();
        var started = new SemaphoreSlim(0);
        mode.BoundaryRunner = (_, ct) =>
        {
            lock (tokens)
            {
                tokens.Add(ct);
            }

            started.Release();
            ct.WaitHandle.WaitOne();
        };

        Task first = Task.Run(() => mode.OnGameStart(Game()));
        await started.WaitAsync(TimeSpan.FromSeconds(30));
        Task second = Task.Run(() => mode.OnGameStart(Game())); // 같은 게임이 다시 뜸
        await started.WaitAsync(TimeSpan.FromSeconds(30));
        await first.WaitAsync(TimeSpan.FromSeconds(30)); // 이전 감시는 멈춤

        Assert.True(tokens[0].IsCancellationRequested);
        Assert.Throws<ObjectDisposedException>(() => _ = tokens[0].WaitHandle); // 치워짐
        Assert.False(tokens[1].IsCancellationRequested);

        mode.NoteGameExit(Game());
        await second.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(tokens[1].IsCancellationRequested);
    }

    [Fact]
    public async Task LateExitFromAnEarlierSessionDoesNotCancelTheNewLoadBoundaryWatch()
    {
        AddGame("foo", "auto");
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { LoadBoundary = true, Trace = false } };
        _rig.World.Running = true;
        AutoMode mode = Mode();
        var tokens = new List<CancellationToken>();
        var started = new SemaphoreSlim(0);
        mode.BoundaryRunner = (_, ct) =>
        {
            lock (tokens)
            {
                tokens.Add(ct);
            }

            started.Release();
            ct.WaitHandle.WaitOne();
        };

        Task first = Task.Run(() => mode.OnGameStart(Game(), startedAt: T0));
        await started.WaitAsync(TimeSpan.FromSeconds(30));
        Task second = Task.Run(() => mode.OnGameStart(Game(), startedAt: T0.AddSeconds(20)));
        await started.WaitAsync(TimeSpan.FromSeconds(30));
        await first.WaitAsync(TimeSpan.FromSeconds(30));

        mode.NoteGameExit(Game(), T0.AddSeconds(10)); // 이전 실행의 늦은 종료

        Assert.False(tokens[1].IsCancellationRequested);
        mode.NoteGameExit(Game(), T0.AddSeconds(30));
        await second.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(tokens[1].IsCancellationRequested);
    }

    [Fact]
    public async Task LoadBoundaryWatchIsCancelledWhenTheGameExitedBeforeItRegistered()
    {
        AddGame("foo", "auto");
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { LoadBoundary = true } };
        _rig.World.Session = new FakeTrace();
        AutoMode mode = Mode();
        bool cancelledOnEntry = false;
        mode.BoundaryRunner = (_, ct) => cancelledOnEntry = ct.IsCancellationRequested;

        await mode.OnGameStart(Game()); // 게임은 이미 꺼져 있음 (Running = false): 등록 뒤 확인이 취소해야 함

        Assert.True(cancelledOnEntry);
    }

    [Theory]
    [InlineData("eac", false)]
    [InlineData("vanguard", false)]
    [InlineData(AutoEnv.AntiCheatUnknown, false)]
    [InlineData(null, true)]
    public async Task LoadBoundaryWatchStartsOnlyWhenTheAntiCheatPolicyAllowsIt(string? cheat, bool runs)
    {
        AddGame("foo", "auto");
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { LoadBoundary = true } };
        _rig.World.Session = new FakeTrace();
        _rig.World.Running = true;
        _rig.World.AntiCheats[@"D:\Games\foo"] = cheat;
        AutoMode mode = Mode();
        int started = 0;
        mode.BoundaryRunner = (_, _) => started++;

        await mode.OnGameStart(Game());

        Assert.Equal(runs ? 1 : 0, started);
    }
}
