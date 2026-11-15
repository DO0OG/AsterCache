using System.Diagnostics;
using System.Text.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tests.Windows;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 두 게임 번갈아 시작: 시작 이벤트·재적용 틱이 종료 유예 표시와 양보 등록을 어떻게 다루는지. 프로세스 이름은 실제 스냅샷이라 지금 시험 프로세스 이름을 '실행 중인 게임' 으로 씁니다.
public sealed class WorkerGameStartTests : IAsyncDisposable
{
    private static readonly string Running = Process.GetCurrentProcess().ProcessName + ".exe";
    private readonly ListLog _log = new();
    private readonly DiskReadCoordinator _coordinator;
    private readonly CapturingWatcher _watcher = new();
    private Worker? _worker;
    private QuickExitGuard? _quickExit;
    private BackgroundYield? _backgroundYield;
    private AutoMode? _auto;
    private Prewarmer? _prewarm;
    private DateTimeOffset? _workerNow;
    private int _quietReads;

    public WorkerGameStartTests() => _coordinator = new DiskReadCoordinator(new VolumeDisks(letter => letter switch
    {
        "D:" => new HashSet<uint> { 1 },
        "E:" => new HashSet<uint> { 2 },
        _ => null,
    }, () => 0), _log);

    public async ValueTask DisposeAsync()
    {
        if (_worker is not null)
        {
            await _worker.StopAsync(CancellationToken.None);
        }
    }

    private Task<Worker> StartWorker(params GameSettings[] games) => StartWorker(null, games);

    private async Task<Worker> StartWorker(PlayRecorder? recorder, params GameSettings[] games)
    {
        var quiet = new BootQuiet(() =>
        {
            _ = Interlocked.Increment(ref _quietReads);
            return (long)TimeSpan.FromDays(1).TotalMilliseconds; // 부팅 조용 구간이 아님 (재적용 루프의 첫 틱이 시작됐는지 세는 용도)
        }, () => false, _log);
        _worker = new Worker(new AsterSettings(1000, games), _log, _watcher, null, new FakeStreamer(), new ReapplyChecker(ReapplyTests.NoIoEnv(), _log), quiet: quiet, prewarm: _prewarm, coordinator: _coordinator, playRecorder: recorder, now: _workerNow is null ? null : () => _workerNow.Value, backgroundYield: _backgroundYield, quickExit: _quickExit, auto: _auto);
        await _worker.StartAsync(CancellationToken.None);
        for (int i = 0; i < 200 && _watcher.OnStart is null; i++)
        {
            await Task.Delay(25);
        }

        Assert.NotNull(_watcher.OnStart);
        for (int i = 0; i < 3000 && Volatile.Read(ref _quietReads) < 2; i++)
        {
            await Task.Delay(10); // 재적용 루프의 첫 틱 (종료 감지 포함) 이 게임 시작 뒤로 밀리면 소멸 시각이 어긋나므로 먼저 지나가게 함
        }

        await Task.Delay(100);
        return _worker;
    }

    [Fact]
    public async Task UnseenShortGameExitEndsItsPlayRecording()
    {
        using var dir = new TempDir();
        GameSettings game = Games.Make("brief game", "brief-game.exe");
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), _log, _ => null, sampleInterval: TimeSpan.FromMilliseconds(5));
        Worker worker = await StartWorker(recorder, game);
        Start(game);

        worker.CompleteDetectedExit(game, unseen: true);

        await WaitUntil(() => File.Exists(dir.Combine("play_samples.json")));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        JsonElement sample = Assert.Single(json.RootElement.GetProperty("games").GetProperty(game.Name).EnumerateArray());
        Assert.NotNull(sample.GetProperty("endedAt").GetString());
        Assert.Equal("game_exit", sample.GetProperty("endReason").GetString());
    }

    [Fact]
    public async Task RunningGameFoundAtServiceObservationIsMarkedAsLate()
    {
        using var dir = new TempDir();
        GameSettings game = Games.Make("already running", Running);
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), _log, _ => null, sampleInterval: TimeSpan.FromMilliseconds(5));
        Worker worker = await StartWorker(recorder, game);

        worker.Apply(new AsterSettings(1000, [game]));
        worker.CompleteDetectedExit(game, unseen: false);

        await WaitUntil(() => File.Exists(dir.Combine("play_samples.json")));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        // 가짜 프로세스는 종료 처리 뒤에도 실행 중으로 보이므로, 다음 관측 틱이 세션을 하나 더 만들 수 있음: 종료로 끝난 세션을 골라 확인
        JsonElement sample = json.RootElement.GetProperty("games").GetProperty(game.Name).EnumerateArray()
            .First(s => s.GetProperty("endReason").GetString() == "game_exit");
        Assert.True(sample.GetProperty("observedAfterGameStart").GetBoolean());
        Assert.Equal("skipped", sample.GetProperty("startStream").GetProperty("status").GetString());
        Assert.Contains(_log.Lines, line => line.Contains("observation started after game start", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RecorderStartExceptionDoesNotSkipDiskYieldOrProcessDelivery()
    {
        GameSettings game = Games.Make("recording error", "recording-error.exe") with { OriginRoot = @"D:\Games\recording-error" };
        var recorder = new PlayRecorder(null, _log, _ => null, sampleInterval: TimeSpan.FromMilliseconds(5), timestamp: () => throw new InvalidOperationException("recorder failed"));
        _ = await StartWorker(recorder, game);

        _watcher.OnStart!(new ProcessStart(91, game.ExeNames[0], 0));

        Assert.True(_coordinator.IsRegistered(game.Name));
        Assert.Contains(_log.Lines, line => line.Contains("play recorder start failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StartLogSeparatesDetectionTimeFromQueueWait()
    {
        GameSettings game = Games.Make("queue timing", "queue-timing.exe");
        _ = await StartWorker(game);
        DateTimeOffset detectedAt = DateTimeOffset.UtcNow.AddSeconds(-2);
        _watcher.OnStart!(new ProcessStart(123, game.ExeNames[0], Stopwatch.GetTimestamp(), "poll") { DetectedAt = detectedAt });

        await WaitUntil(() => _log.Lines.Any(line => line.Contains("queue-timing.exe started (pid 123) via poll", StringComparison.Ordinal)));

        string line = Assert.Single(_log.Lines, message => message.Contains("queue-timing.exe started (pid 123)", StringComparison.Ordinal));
        Assert.Contains($"detected_at={detectedAt:O}", line, StringComparison.Ordinal);
        Assert.Contains("queue_wait_ms=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("queue_wait_ms=unknown", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecorderExitLogExceptionDoesNotSkipExistingExitHandling()
    {
        GameSettings game = Games.Make("exit log error", "exit-log-error.exe");
        var recorder = new PlayRecorder(null, new ThrowOnPlayRecordLog(), _ => null, sampleInterval: TimeSpan.FromMilliseconds(5));
        Worker worker = await StartWorker(recorder, game);
        Start(game);

        worker.CompleteDetectedExit(game, unseen: false);

        Assert.Contains(_log.Lines, line => line.Contains("exit log error: exited", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, line => line.Contains("play recorder end failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BackedOffSessionSkipsTheStartStreamAndMemoryYieldButStillYieldsTheDisk()
    {
        GameSettings game = Games.Make("backed off", "backed-off.exe") with { OriginRoot = @"D:\Games\backed-off" };
        DateTimeOffset now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        _workerNow = now;
        _quickExit = new QuickExitGuard(new QuickExitEnv { Now = () => now }, _log);
        _ = _quickExit.BeginSession(game.Name);
        now = now.AddSeconds(20);
        Assert.True(_quickExit.EndSession(game.Name)); // 짧은 종료 하나
        now = now.AddMinutes(1);
        _workerNow = now;
        var world = new YieldWorld();
        _ = world.Add(50, "big.exe", wsMib: 500);
        _backgroundYield = new BackgroundYield(() => new AsterSettings(1000, [game]), world.Env(), _log);
        _ = await StartWorker(game);

        Start(game);

        await WaitUntil(() => _log.Lines.Any(line => line.Contains("backed-off.exe started", StringComparison.Ordinal))
            && _log.Lines.Any(line => line.Contains("start stream skipped (quick exit guard: backing off)", StringComparison.Ordinal)));
        await Task.Delay(200); // 비동기로 도는 배경 양보가 불리지 않았음을 확인할 틈
        Assert.Empty(world.Sets);
        Assert.Contains(_log.Lines, line => line.Contains("backed off: backing off for this run", StringComparison.Ordinal));
        Assert.True(_coordinator.IsRegistered(game.Name)); // 디스크 양보는 그대로
    }

    [Fact]
    public async Task SameProgramStartedAgainAfterItVanishedStartsANewSessionSoTheQuickExitIsCounted()
    {
        GameSettings game = Games.Make("relaunch", "relaunch-game.exe");
        DateTimeOffset now = DateTimeOffset.UtcNow; // 종료 시각은 서비스 종료 추적기의 실제 시계에서 올 수도 있어 같은 시계 근처에서 시작
        _workerNow = now;
        _quickExit = new QuickExitGuard(new QuickExitEnv
        {
            Now = () => now,
            Background = action =>
            {
                action();
                return Task.CompletedTask;
            },
        }, _log);
        _ = await StartWorker(game);
        _watcher.OnStart!(new ProcessStart(1, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = now });

        now = now.AddSeconds(20); // 첫 실행은 20초 만에 사라지고 (종료 유예에 묶여 아직 같은 세션으로 보임) 같은 프로그램이 다시 뜸
        _workerNow = now;
        _watcher.OnStart!(new ProcessStart(2, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = now });

        Assert.Contains(_log.Lines, line => line.Contains("relaunch: exited quickly (", StringComparison.Ordinal));
        Assert.True(_quickExit.IsBackingOff(game.Name));
    }

    [Fact]
    public async Task RestartInsidePollGraceUsesTheFirstGoneTimeForThePriorRunLength()
    {
        GameSettings game = Games.Make("polled relaunch", "polled-relaunch.exe");
        DateTimeOffset time = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        _workerNow = time;
        _quickExit = new QuickExitGuard(new QuickExitEnv
        {
            Now = () => time,
            Background = action =>
            {
                action();
                return Task.CompletedTask;
            },
        }, _log);
        Worker worker = await StartWorker(game);
        _watcher.OnStart!(new ProcessStart(1, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = time });

        time = time.AddSeconds(80);
        _workerNow = time;
        worker.NoteGameExits(); // Poll 이 처음 소멸을 본 시각
        time = time.AddSeconds(20);
        _workerNow = time;
        _watcher.OnStart!(new ProcessStart(2, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = time });

        Assert.Contains(_log.Lines, line => line.Contains("polled relaunch: exited quickly (80s)", StringComparison.Ordinal));
        Assert.True(_quickExit.IsBackingOff(game.Name));
    }

    [Fact]
    public async Task SameProgramRestartingItselfWithinSecondsIsNotCountedAsAQuickExit()
    {
        GameSettings game = Games.Make("selfrelaunch", "selfrelaunch-game.exe");
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _workerNow = now;
        _quickExit = new QuickExitGuard(new QuickExitEnv
        {
            Now = () => now,
            Background = action =>
            {
                action();
                return Task.CompletedTask;
            },
        }, _log);
        _ = await StartWorker(game);
        _watcher.OnStart!(new ProcessStart(1, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = now });

        now = now.AddSeconds(2); // 첫 프로세스가 2초 만에 끝나고 같은 이름의 새 pid 로 다시 뜸: 짧은 종료로 세지 않고 세션을 이어 감
        _workerNow = now;
        _watcher.OnStart!(new ProcessStart(2, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = now });
        Assert.DoesNotContain(_log.Lines, line => line.Contains("exited quickly", StringComparison.Ordinal));
        Assert.False(_quickExit.IsBackingOff(game.Name));
        now = now.AddSeconds(20); // 진짜 실행이 20초 만에 끝났다가 다시 뜸: 이제는 나눔
        _workerNow = now;
        _watcher.OnStart!(new ProcessStart(3, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = now });

        Assert.Single(_log.Lines, line => line.Contains("selfrelaunch: exited quickly (", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, line => line.Contains("selfrelaunch: exited quickly (22s)", StringComparison.Ordinal)); // 세션은 처음부터 이어 졌으므로 22초
    }

    [Fact]
    public async Task RepeatedTwoSecondPidRestartsDoNotAccumulateTheSplitThreshold()
    {
        GameSettings game = Games.Make("rapid self restart", "rapid-self-restart.exe");
        DateTimeOffset time = DateTimeOffset.UtcNow;
        _workerNow = time;
        _quickExit = new QuickExitGuard(new QuickExitEnv { Now = () => time }, _log);
        _ = await StartWorker(game);
        _watcher.OnStart!(new ProcessStart(1, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = time });

        for (int pid = 2; pid <= 5; pid++)
        {
            time = time.AddSeconds(2);
            _workerNow = time;
            _watcher.OnStart!(new ProcessStart(pid, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = time });
        }

        Assert.DoesNotContain(_log.Lines, line => line.Contains("exited quickly", StringComparison.Ordinal));
        Assert.False(_quickExit.IsBackingOff(game.Name));
    }

    [Fact]
    public async Task LatePollExitAfterRestartDoesNotCloseTheNewQuickExitSession()
    {
        GameSettings game = Games.Make("late poll exit", "late-poll-exit.exe");
        DateTimeOffset time = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        _workerNow = time;
        _quickExit = new QuickExitGuard(new QuickExitEnv
        {
            Now = () => time,
            Background = action =>
            {
                action();
                return Task.CompletedTask;
            },
        }, _log);
        Worker worker = await StartWorker(game);
        _watcher.OnStart!(new ProcessStart(1, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = time });
        time = time.AddSeconds(52);
        _workerNow = time;
        worker.NoteGameExits();
        DateTimeOffset oldExit = time;
        time = time.AddSeconds(10);
        _workerNow = time;
        _watcher.OnStart!(new ProcessStart(2, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = time });

        worker.CompleteDetectedExit(game, unseen: false, exitedAt: oldExit); // Poll 결과가 시작 뒤에 도착한 경우

        Assert.True(_quickExit.IsBackingOff(game.Name));
        Assert.Equal(1, _log.Lines.Count(line => line == "Info: late poll exit: exited")); // 재실행 분리가 남긴 한 줄뿐, 늦게 도착한 종료는 더하지 않음
    }

    [Fact]
    public async Task BackingOffGameDoesNotSuppressAnotherGamesSharedTrigger()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _workerNow = now;
        GameSettings backingOff = Games.Make("backing off trigger", "shared.exe") with
        {
            Prewarm = PrewarmSettings.Default with { TriggerExes = ["shared.exe"] },
        };
        GameSettings other = Games.Make("other trigger target", "other-game.exe") with
        {
            Prewarm = PrewarmSettings.Default with { TriggerExes = ["shared.exe"] },
        };
        _quickExit = new QuickExitGuard(new QuickExitEnv { Now = () => now }, _log);
        _ = _quickExit.BeginSession(backingOff.Name);
        now = now.AddSeconds(20);
        _workerNow = now;
        Assert.True(_quickExit.EndSession(backingOff.Name));

        var streamer = new FakeStreamer();
        var prewarm = new Prewarmer([backingOff, other], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 99999,
            GameRunning = _ => false,
            ZoneBytes = _ => 1L << 20,
            TraceExists = _ => true,
        }, _log, now: () => now);
        _prewarm = prewarm;
        _ = await StartWorker(backingOff, other);

        _watcher.OnStart!(new ProcessStart(2, "shared.exe", Stopwatch.GetTimestamp()) { DetectedAt = now });
        static bool Trigger(string line, string game) => line.Contains(game + ":", StringComparison.Ordinal)
            && (line.Contains("[trigger shared.exe]", StringComparison.Ordinal) || line.Contains("triggered by shared.exe", StringComparison.Ordinal)); // 서비스 시작 예열 줄과 구분
        await WaitUntil(() => _log.Lines.Any(line => Trigger(line, other.Name))); // 다른 게임은 (실제 예열이든 건너뜀이든) 트리거로 처리됨

        Assert.DoesNotContain(_log.Lines, line => Trigger(line, backingOff.Name));
    }

    [Fact]
    public async Task RestartSplitRestoresTheMemoryYieldSplitsThePlayRecordingAndTheBackedOffRunDoesNotYieldAgain()
    {
        using var dir = new TempDir();
        GameSettings game = Games.Make("restart cleanup", "restart-cleanup.exe") with { OriginRoot = @"D:\Games\restart-cleanup" };
        DateTimeOffset time = DateTimeOffset.UtcNow;
        _workerNow = time;
        _quickExit = new QuickExitGuard(new QuickExitEnv { Now = () => time }, _log);
        var world = new YieldWorld();
        _ = world.Add(50, "big.exe", wsMib: 500);
        _backgroundYield = new BackgroundYield(() => new AsterSettings(1000, [game]), world.Env(), _log);
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), _log, _ => null, sampleInterval: TimeSpan.FromMilliseconds(5));
        _ = await StartWorker(recorder, game);
        Start(game);
        await WaitUntil(() => world.Sets.Contains((50, 1))); // 첫 실행의 배경 메모리 양보

        time = time.AddSeconds(20);
        _workerNow = time;
        _watcher.OnStart!(new ProcessStart(2, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = time });

        await WaitUntil(() => world.Sets.Contains((50, 5))); // 이전 실행의 양보가 복원됨
        await Task.Delay(200); // 물러나는 새 실행이 다시 양보를 시작하지 않았음을 확인할 틈
        Assert.Equal(1, world.Sets.Count(set => set.Priority == 1));
        Assert.True(_quickExit.IsBackingOff(game.Name));
        await WaitUntil(() => File.Exists(dir.Combine("play_samples.json")));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        Assert.Contains(json.RootElement.GetProperty("games").GetProperty(game.Name).EnumerateArray(), sample => sample.GetProperty("endReason").GetString() == "quick_exit");
    }

    [Fact]
    public async Task TodaysSequenceCountsOneBacksOffTheSecondRunWarnsAtTheSecondExitAndLeavesTheThirdRunNormal()
    {
        GameSettings game = Games.Make("today", "today-game.exe");
        DateTimeOffset origin = new(2026, 11, 15, 23, 10, 6, TimeSpan.Zero);
        DateTimeOffset time = origin;
        _workerNow = time;
        List<QuickExitRecord> notices = [];
        _quickExit = new QuickExitGuard(new QuickExitEnv
        {
            Now = () => time,
            Background = action =>
            {
                action();
                return Task.CompletedTask;
            },
            Notify = (_, record) => notices.Add(record),
        }, _log);
        Worker worker = await StartWorker(game);

        void At(int seconds)
        {
            time = origin.AddSeconds(seconds);
            _workerNow = time;
        }

        void Launch(int pid) => _watcher.OnStart!(new ProcessStart(pid, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = time });

        Launch(1);
        At(52);
        worker.NoteGameExits(); // 첫 실행이 52초 만에 사라짐 (종료 유예 중)
        At(58);
        Launch(2);
        Assert.Contains(_log.Lines, line => line.Contains("today: exited quickly (52s)", StringComparison.Ordinal));
        Assert.True(_quickExit.IsBackingOff(game.Name)); // 두 번째 실행은 물러남
        At(63);
        worker.NoteGameExits(); // 두 번째 실행은 5초
        At(84);
        Launch(3);
        Assert.Contains(_log.Lines, line => line.Contains("today: quick exit 2 in a row (runs 52s, 5s); backed-off run: exited too", StringComparison.Ordinal));
        Assert.Equal(2, Assert.Single(notices).Count);
        Assert.False(_quickExit.IsBackingOff(game.Name)); // 세 번째 실행은 평소대로
        At(169);
        worker.NoteGameExits();
        At(229);
        worker.NoteGameExits(); // 85초 실행의 종료 확정: 기록은 계속
        Assert.Contains(_log.Lines, line => line.Contains("today: quick exit 3 in a row (runs 52s, 5s, 85s)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestartAfterAnAlreadyHandledExitTakesTheUsualStartPathWithoutAnotherExitHandling()
    {
        GameSettings game = Games.Make("handled exit", "handled-exit.exe") with { OriginRoot = @"D:\Games\handled-exit" };
        DateTimeOffset origin = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset time = origin;
        _workerNow = time;
        _quickExit = new QuickExitGuard(new QuickExitEnv { Now = () => time, Background = Inline }, _log);
        var world = new YieldWorld();
        _ = world.Add(50, "big.exe", wsMib: 500);
        _backgroundYield = new BackgroundYield(() => new AsterSettings(1000, [game]), world.Env(), _log);
        Worker worker = await StartWorker(game);
        _watcher.OnStart!(new ProcessStart(1, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = time });
        await WaitUntil(() => world.Sets.Contains((50, 1)));

        time = origin.AddSeconds(100);
        _workerNow = time;
        worker.NoteGameExits();
        time = origin.AddSeconds(170);
        _workerNow = time;
        worker.NoteGameExits(); // 종료가 확정되고 처리됨 (가짜 프로세스는 Poll 이 본 적이 없어 보이지 않은 실행의 처리)
        time = origin.AddHours(2);
        _workerNow = time;
        _watcher.OnStart!(new ProcessStart(2, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = time });
        await Task.Delay(300);

        Assert.DoesNotContain(_log.Lines, line => line == "Info: handled exit: exited"); // 재실행이 종료 처리를 한 번 더 하지 않음
        Assert.Equal([(50, 1)], world.Sets); // 양보를 복원했다가 다시 비우지 않음 (두 번째 시작의 양보는 60초 안이라 무시)
    }

    [Fact]
    public async Task ExitConfirmedAfterAPollSeenRunIsHandledOnceAndALaterRestartAfterALauncherYieldDoesNotRestoreOrRedoIt()
    {
        using var dir = new TempDir();
        string exe = $"aster-seen-{Guid.NewGuid():N}.exe";
        string copy = dir.Combine(exe);
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), copy);
        GameSettings game = Games.Make("seen exit", exe) with { OriginRoot = @"D:\Games\seen-exit" };
        DateTimeOffset origin = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset time = origin;
        _workerNow = time;
        _quickExit = new QuickExitGuard(new QuickExitEnv { Now = () => time, Background = Inline }, _log);
        var world = new YieldWorld();
        _ = world.Add(50, "big.exe", wsMib: 500);
        _backgroundYield = new BackgroundYield(() => new AsterSettings(1000, [game]), world.Env(), _log);
        Worker worker = await StartWorker(game);
        using var helper = Process.Start(new ProcessStartInfo(copy, "-n 60 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true })!; // 게임이 아닌 시험용 대기 프로세스 (Poll 이 실제로 보게 함)
        try
        {
            _watcher.OnStart!(new ProcessStart(helper.Id, exe, Stopwatch.GetTimestamp()) { DetectedAt = time });
            await WaitUntil(() => world.Sets.Contains((50, 1)));
            time = origin.AddSeconds(1);
            _workerNow = time;
            worker.NoteGameExits(); // Poll 이 실행을 직접 봄
        }
        finally
        {
            helper.Kill();
            helper.WaitForExit();
        }

        time = origin.AddSeconds(100);
        _workerNow = time;
        worker.NoteGameExits(); // 사라진 것을 처음 봄 (가짜 시계의 유예가 지나야 확정)
        time = origin.AddSeconds(170);
        _workerNow = time;
        worker.NoteGameExits(); // 종료 확정·처리
        Assert.Single(_log.Lines, line => line == "Info: seen exit: exited");
        Assert.Contains((50, 5), world.Sets); // 정상 종료 처리가 양보를 복원함

        _backgroundYield.Begin("trigger", settle: false); // 런처 같은 트리거가 먼저 양보를 엶
        int afterLauncher = world.Sets.Count;
        time = origin.AddHours(2);
        _workerNow = time;
        _watcher.OnStart!(new ProcessStart(helper.Id + 1, exe, Stopwatch.GetTimestamp()) { DetectedAt = time });
        await Task.Delay(300);

        Assert.Single(_log.Lines, line => line == "Info: seen exit: exited"); // 재실행이 종료 처리를 한 번 더 하지 않음
        Assert.Equal(afterLauncher, world.Sets.Count); // 게임 exe 시작에서 양보 복원도 재비우기도 없음
    }

    [Fact]
    public async Task RepeatedShortPidRestartsSeparatedByTickGapsDoNotAddUpToASplit()
    {
        GameSettings game = Games.Make("gap restarts", "gap-restarts.exe");
        DateTimeOffset origin = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset time = origin;
        _workerNow = time;
        _quickExit = new QuickExitGuard(new QuickExitEnv { Now = () => time, Background = Inline }, _log);
        Worker worker = await StartWorker(game);

        void At(int seconds)
        {
            time = origin.AddSeconds(seconds);
            _workerNow = time;
        }

        void Launch(int pid) => _watcher.OnStart!(new ProcessStart(pid, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = time });

        Launch(1);
        At(2);
        worker.NoteGameExits(); // 틱이 2초 실행의 끝을 봄
        At(5);
        Launch(2);
        At(7);
        worker.NoteGameExits();
        At(10);
        Launch(3); // 실행은 2초씩이지만 끝 사이 간격은 5초

        Assert.DoesNotContain(_log.Lines, line => line.Contains("exited quickly", StringComparison.Ordinal));
        Assert.False(_quickExit.IsBackingOff(game.Name));
    }

    [Fact]
    public async Task SlowTraceEndWithAShortRunThenABackedOffRunNeverOpensTraceOrBoundaryReadsForThoseRuns()
    {
        using var rig = new AutoRig();
        rig.Editor.Add(rig.Input("late-start", "late-start.exe", @"D:\Games\late-start", "auto"));
        rig.Reload();
        rig.Settings = rig.Settings with { Auto = AutoSettings.Default with { LoadBoundary = true } };
        using var gate = new ManualResetEventSlim(false);
        rig.World.Session = new FakeTrace { StopGate = gate };
        rig.World.Running = true;
        _auto = new AutoMode(rig.Editor, rig.World.Env(), rig.Status, rig.Log, rig.Settings);
        int boundary = 0;
        _auto.BoundaryRunner = (_, _) => Interlocked.Increment(ref boundary);
        GameSettings game = rig.Settings.Games[0];
        DateTimeOffset origin = rig.World.Now;
        DateTimeOffset time = origin;
        _workerNow = time;
        _quickExit = new QuickExitGuard(new QuickExitEnv { Now = () => time, Background = Inline }, _log);
        _ = await StartWorker(game);

        _watcher.OnStart!(new ProcessStart(1, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = time });
        await WaitUntil(() => rig.World.TraceStarts == 1 && Volatile.Read(ref boundary) == 1); // 긴 첫 실행의 추적
        time = origin.AddSeconds(200);
        _workerNow = time;
        _watcher.OnStart!(new ProcessStart(2, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = time }); // 짧은 실행 B
        time = origin.AddSeconds(208);
        _workerNow = time;
        _watcher.OnStart!(new ProcessStart(3, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = time }); // 물러나는 실행 C
        Assert.True(_quickExit.IsBackingOff(game.Name));
        gate.Set(); // 느리던 첫 추적 종료가 이제 끝남
        for (int i = 0; i < 3000 && !rig.Log.Lines.Any(line => line.Contains("auto trace skipped (quick exit guard: backing off)", StringComparison.Ordinal)); i++)
        {
            await Task.Delay(10);
        }

        await Task.Delay(500);
        Assert.Contains(rig.Log.Lines, line => line.Contains("auto trace skipped (quick exit guard: backing off)", StringComparison.Ordinal));
        Assert.Equal(1, rig.World.TraceStarts);
        Assert.Equal(1, Volatile.Read(ref boundary));
    }

    [Fact]
    public async Task LauncherTriggerSkipsAGameThatWouldBackOffButStillServesOthers()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _workerNow = now;
        PrewarmSettings trigger = PrewarmSettings.Default with { TriggerExes = ["launcher-x.exe"] };
        GameSettings backingOff = Games.Make("launcher backing off", "launcher-backing-off.exe") with { Prewarm = trigger };
        GameSettings other = Games.Make("launcher other", "launcher-other.exe") with { Prewarm = trigger };
        _quickExit = new QuickExitGuard(new QuickExitEnv { Now = () => now, Background = Inline }, _log);
        _ = _quickExit.BeginSession(backingOff.Name);
        now = now.AddSeconds(20);
        _workerNow = now;
        Assert.True(_quickExit.EndSession(backingOff.Name));
        _prewarm = new Prewarmer([backingOff, other], new FakeStreamer(), new PrewarmEnv
        {
            AvailableRamMib = () => 99999,
            GameRunning = _ => false,
            ZoneBytes = _ => 1L << 20,
            TraceExists = _ => true,
        }, _log, now: () => now);
        _ = await StartWorker(backingOff, other);

        _watcher.OnStart!(new ProcessStart(5, "launcher-x.exe", Stopwatch.GetTimestamp()) { DetectedAt = now });
        await WaitUntil(() => _log.Lines.Any(line => TriggerLine(line, other.Name, "launcher-x.exe")));
        await Task.Delay(300);

        Assert.DoesNotContain(_log.Lines, line => TriggerLine(line, backingOff.Name, "launcher-x.exe"));
        Assert.False(_quickExit.IsBackingOff(backingOff.Name)); // 읽기 전용 질문이라 세션도 "연속당 한 번" 도 건드리지 않음
        _watcher.OnStart!(new ProcessStart(6, backingOff.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = now });
        Assert.True(_quickExit.IsBackingOff(backingOff.Name)); // 뒤따르는 게임 exe 시작은 물러남
    }

    [Fact]
    public async Task LauncherTriggerServesEveryGameWhenNoneWouldBackOff()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _workerNow = now;
        PrewarmSettings trigger = PrewarmSettings.Default with { TriggerExes = ["launcher-y.exe"] };
        GameSettings first = Games.Make("launcher first", "launcher-first.exe") with { Prewarm = trigger };
        GameSettings second = Games.Make("launcher second", "launcher-second.exe") with { Prewarm = trigger };
        _quickExit = new QuickExitGuard(new QuickExitEnv { Now = () => now, Background = Inline }, _log);
        _prewarm = new Prewarmer([first, second], new FakeStreamer(), new PrewarmEnv
        {
            AvailableRamMib = () => 99999,
            GameRunning = _ => false,
            ZoneBytes = _ => 1L << 20,
            TraceExists = _ => true,
        }, _log, now: () => now);
        _ = await StartWorker(first, second);

        _watcher.OnStart!(new ProcessStart(5, "launcher-y.exe", Stopwatch.GetTimestamp()) { DetectedAt = now });

        await WaitUntil(() => _log.Lines.Any(line => TriggerLine(line, first.Name, "launcher-y.exe")) && _log.Lines.Any(line => TriggerLine(line, second.Name, "launcher-y.exe")));
    }

    private static bool TriggerLine(string line, string game, string exe) => line.Contains(game + ":", StringComparison.Ordinal)
        && (line.Contains($"[trigger {exe}]", StringComparison.Ordinal) || line.Contains($"triggered by {exe}", StringComparison.Ordinal)); // 서비스 시작 예열 줄과 구분

    private static Task Inline(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task LauncherHandOffToTheMainProgramStaysOneSession()
    {
        GameSettings game = Games.Make("handoff", "handoff-launcher.exe", "handoff-main.exe");
        DateTimeOffset now = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        _quickExit = new QuickExitGuard(new QuickExitEnv { Now = () => now }, _log);
        _ = await StartWorker(game);
        _watcher.OnStart!(new ProcessStart(1, "handoff-launcher.exe", Stopwatch.GetTimestamp()));

        now = now.AddSeconds(5); // 런처가 사라진 뒤 본체가 뜸: 다른 프로그램이므로 새 세션이 아님
        _watcher.OnStart!(new ProcessStart(2, "handoff-main.exe", Stopwatch.GetTimestamp()));
        _watcher.OnStart!(new ProcessStart(2, "handoff-main.exe", Stopwatch.GetTimestamp())); // 같은 프로세스의 중복 알림도 마찬가지

        Assert.DoesNotContain(_log.Lines, line => line.Contains("exited quickly", StringComparison.Ordinal));
        Assert.False(_quickExit.IsBackingOff(game.Name));
    }

    private void Start(GameSettings game)
    {
        DateTimeOffset at = _workerNow ?? DateTimeOffset.UtcNow;
        _watcher.OnStart!(new ProcessStart(1, game.ExeNames[0], Stopwatch.GetTimestamp()) { DetectedAt = at });
    }

    private DiskReadCoordinator.Lease? BeginStart(GameSettings game) =>
        _coordinator.TryBegin(game.OriginRoot, "start stream", default, game.OriginRoot, allowWhileYielded: true, preserveGameKey: game.Name);

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(5);
        }

        Assert.True(condition(), "condition was not met before the timeout");
    }

    [Fact]
    public async Task SecondGameStartedRightAfterTheFirstExitedBeforeTheTickMayStream()
    {
        GameSettings a = Games.Make("a", "aster-test-a-never-running.exe");
        GameSettings b = Games.Make("b", Running);
        _ = await StartWorker(a, b);
        Start(a); // A 가 떴다가 (이름 스냅샷에는 없음 = 이미 종료)

        Start(b); // 틱이 오기 전에 B 가 뜸

        using DiskReadCoordinator.Lease? lease = BeginStart(b);
        Assert.NotNull(lease);
        Assert.Null(_coordinator.TryBegin(@"D:\Games\Other", "warm files")); // 양보는 유지
    }

    [Fact]
    public async Task SecondGameStartIsStillRejectedWhileTheFirstReallyRuns()
    {
        GameSettings a = Games.Make("a", Running);
        GameSettings b = Games.Make("b", "aster-test-b-never-running.exe");
        _ = await StartWorker(a, b);
        Start(a);

        Start(b);

        Assert.Null(BeginStart(b));
    }

    [Fact]
    public async Task ReapplyTickKeepsTheGraceMarkInsteadOfClearingItWithAnotherYield()
    {
        GameSettings a = Games.Make("a", "aster-test-a-never-running.exe");
        GameSettings b = Games.Make("b", Running);
        Worker worker = await StartWorker(a, b);
        Start(a);
        Start(b);
        using (DiskReadCoordinator.Lease? before = BeginStart(b))
        {
            Assert.NotNull(before);
        }

        worker.NoteGameExits(); // 틱: A 는 처음 사라진 틱 = 유예 시작, B 는 실행 중

        using DiskReadCoordinator.Lease? lease = BeginStart(b);
        Assert.NotNull(lease); // 유예 게임에 Yield 를 다시 걸면 유예 표시가 지워져 여기서 거절됨
    }

    [Fact]
    public async Task GraceGameWhoseRootChangedIsRegisteredAgainOnTheNewDisk()
    {
        GameSettings a = Games.Make("a", "aster-test-a-never-running.exe");
        GameSettings b = Games.Make("b", Running);
        Worker worker = await StartWorker(a, b);
        Start(a);
        worker.NoteGameExits(); // A 유예 시작
        GameSettings moved = a with { OriginRoot = @"E:\Games" };
        using (DiskReadCoordinator.Lease? open = _coordinator.TryBegin(@"E:\Other", "warm files"))
        {
            Assert.NotNull(open); // E 디스크는 아직 열려 있음
        }

        worker.Apply(new AsterSettings(1000, [moved, b]));

        worker.NoteGameExits(); // 아직 유예 중에 설치 폴더가 바뀜: 등록이 지워졌다가 새 디스크에 다시 걸려야 함

        Assert.True(_coordinator.IsRegistered("a"));
        Assert.Null(_coordinator.TryBegin(@"E:\Other", "warm files"));
    }

    private sealed class CapturingWatcher : IProcessWatcher
    {
        public volatile Action<ProcessStart>? OnStart;

        public async Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
        {
            OnStart = onStart;
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 종료
            }
        }
    }

    private sealed class ThrowOnPlayRecordLog : ILog
    {
        public void Write(LogLevel level, string message, Exception? exception = null)
        {
            if (message.Contains(": play record:", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("log failed");
            }
        }
    }
}
