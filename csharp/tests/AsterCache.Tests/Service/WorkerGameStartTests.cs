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
        _worker = new Worker(new AsterSettings(1000, games), _log, _watcher, null, new FakeStreamer(), new ReapplyChecker(ReapplyTests.NoIoEnv(), _log), coordinator: _coordinator, playRecorder: recorder);
        await _worker.StartAsync(CancellationToken.None);
        for (int i = 0; i < 200 && _watcher.OnStart is null; i++)
        {
            await Task.Delay(25);
        }

        Assert.NotNull(_watcher.OnStart);
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
        JsonElement sample = Assert.Single(json.RootElement.GetProperty("games").GetProperty(game.Name).EnumerateArray());
        Assert.True(sample.GetProperty("observedAfterGameStart").GetBoolean());
        Assert.Equal("skipped", sample.GetProperty("startStream").GetProperty("status").GetString());
        Assert.Equal("game_exit", sample.GetProperty("endReason").GetString());
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

    private void Start(GameSettings game) => _watcher.OnStart!(new ProcessStart(1, game.ExeNames[0], Stopwatch.GetTimestamp()));

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
