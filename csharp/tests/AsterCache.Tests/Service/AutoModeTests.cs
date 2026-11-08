using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 최근 게임 예열 선택, 게임 시작 처리, 설정 교체, 워커 연결.
public sealed class AutoModeTests : IDisposable
{
    private readonly AutoRig _rig = new();

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
    public void GameStartRecordsLastPlayedAndOpensAnAutoTraceWhenTheRawTraceIsMissing()
    {
        AddGame("foo", "auto");
        _rig.World.Session = new FakeTrace();
        _rig.World.Now = T0;

        Mode().OnGameStart(Game());

        Assert.Equal(T0, _rig.Status.Get("foo")!.LastPlayed);
        Assert.Equal(AutoStage.Tracing, _rig.Status.Get("foo")!.Stage); // auto.learn 기본 꺼짐 (E7 재측정 전): 일반 트레이스 단계
        Assert.Equal(1, _rig.World.TraceStarts);
    }

    [Fact]
    public void GameStartWithATraceAlreadyOnDiskOnlyRecordsThePlay()
    {
        AddGame("foo", "manual");
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { TraceRuns = 1 } };
        _rig.World.Files.Add(AutoTracer.RawTracePath(Game()));

        Mode().OnGameStart(Game());

        Assert.Equal((AutoStage.WaitingIdle, "manual"), (_rig.Status.Get("foo")!.Stage, _rig.Status.Get("foo")!.Source));
        Assert.NotNull(_rig.Status.Get("foo")!.LastPlayed);
        Assert.Equal(0, _rig.World.TraceStarts);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void GameStartHonorsTheTraceAndEnabledSwitches(bool traceOn, bool enabled)
    {
        AddGame("foo", "auto");
        _rig.World.Session = new FakeTrace();
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { Trace = traceOn, Enabled = enabled } };

        Mode().OnGameStart(Game());

        Assert.Equal(0, _rig.World.TraceStarts);
        Assert.Equal(enabled, _rig.Status.Get("foo") is not null);
    }

    [Fact]
    public void ExceptionsInsideAnAutoActionAreLoggedNotThrown()
    {
        AddGame("foo", "auto");
        _rig.World.StartTraceThrows = new InvalidOperationException("boom");

        Mode().OnGameStart(Game()); // 세션 시작 실패는 failed 로 기록됨
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
        }, _rig.Log);
        var emitted = new TaskCompletionSource();
        _rig.World.Files.Add(AutoTracer.RawTracePath(Game("warm")));
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
    public void GameStartKeepsRecordingExtraRunsUntilTraceRunsIsReachedAndLeavesTheStageAlone()
    {
        AddGame("foo", "manual");
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { TraceRuns = 3 } };
        _rig.World.Files.Add(AutoTracer.RawTracePath(Game())); // 첫 기록 1 회
        _rig.World.Session = new FakeTrace();
        _rig.Status.Complete("foo", "manual", T0, counted: false, null);

        Mode().OnGameStart(Game());

        Assert.Equal(1, _rig.World.TraceStarts);
        Assert.Equal(AutoStage.Done, _rig.Status.Get("foo")!.Stage);
    }

    [Fact]
    public void GameStartTracesAnExtraRunOnlyWhileCoverageIsLowAndTheLastMergeAddedNewBlocks()
    {
        AddGame("foo", "auto");
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { TraceRuns = 2 } };
        string raw = AutoTracer.RawTracePath(Game());
        Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
        _rig.World.Files.UnionWith([raw, AsterCache.Tracer.TraceWriter.RunPath(raw, 1), AsterCache.Tracer.TraceWriter.RunPath(raw, 2)]);
        File.WriteAllText(AutoTracer.MergeMetaPath(Game()), "{\"run\": 2, \"new_mib\": 100}");
        _rig.World.Session = new FakeTrace();
        _rig.Status.Set("foo", "auto", null, AutoStage.Done, null);
        File.WriteAllText(AutoTracer.MergeMetaPath(Game()), "{\"run\": 2, \"new_mib\": 10}");
        _rig.Status.RecordCoverage("foo", 22);

        Mode().OnGameStart(Game());
        Assert.Equal(0, _rig.World.TraceStarts); // 새 블록 10 MiB < 64

        File.WriteAllText(AutoTracer.MergeMetaPath(Game()), "{\"run\": 2, \"new_mib\": 100}");
        _rig.Status.RecordCoverage("foo", 100); // 짧은 재실행이 덮어쓴 값
        Mode().OnGameStart(Game());

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
    public void LoadBoundaryWatchIsCancelledWhenTheGameExitedBeforeItRegistered()
    {
        AddGame("foo", "auto");
        _rig.Settings = _rig.Settings with { Auto = AutoSettings.Default with { LoadBoundary = true } };
        _rig.World.Session = new FakeTrace();
        AutoMode mode = Mode();
        bool cancelledOnEntry = false;
        mode.BoundaryRunner = (_, ct) => cancelledOnEntry = ct.IsCancellationRequested;

        mode.OnGameStart(Game()); // 게임은 이미 꺼져 있음 (Running = false): 등록 뒤 확인이 취소해야 함

        Assert.True(cancelledOnEntry);
    }
}
