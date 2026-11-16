using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 부팅 조용 구간의 Worker 판단: 게임 exe 계기는 바로, 런처 계기·배치 검사·재배치는 구간 동안 보류하고 구간이 끝나면 한 번 실행. 가동 5 분 이후 시작은 그대로.
/// 재적용 틱은 ReapplyTick 을 직접 불러 시험 (재적용 루프는 첫 검사까지 1 분을 기다리므로).
public sealed class BootQuietWorkerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    private const long Minute = 60_000;
    private static readonly PrewarmSettings Launcher = new(false, ["steam.exe"], 4096, 30); // 서비스 시작 예열은 끔: 계기 예열만 본다
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_bootquiet_worker_" + Guid.NewGuid().ToString("N"));
    private string StatusPath => Path.Combine(_dir, "prewarm_status.json");
    private readonly ListLog _log = new();
    private readonly FakeStreamer _streamer = new();
    private readonly GameSettings _game = Games.Make("a", "a.exe") with { Prewarm = Launcher };
    private DateTimeOffset _now = T0;
    private long _uptimeMs = Minute;
    private long _sleptMs; // 절전 중 흐른 시간: 가동 시간에는 들어가고 절전 제외 시계에는 빠짐
    private bool _idle;
    private int _layoutChecks;

    public BootQuietWorkerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private BootQuiet Quiet() => new(() => _uptimeMs, () => _idle, _log, () => _now, () => _uptimeMs - _sleptMs);

    // 시간만 흐름 (절전 없음): 벽시계와 가동 시간이 함께 흐름
    private void Elapse(TimeSpan span)
    {
        _now += span;
        _uptimeMs += (long)span.TotalMilliseconds;
    }

    // 절전: 벽시계와 가동 시간은 흐르고 절전 제외 시계는 멈춤
    private void Sleep(TimeSpan span)
    {
        Elapse(span);
        _sleptMs += (long)span.TotalMilliseconds;
    }

    private Prewarmer MakePrewarm() => new([_game], _streamer, new PrewarmEnv
    {
        AvailableRamMib = () => 16000,
        GameRunning = _ => false,
        ZoneBytes = _ => 100L << 20,
        TraceExists = _ => true,
        UptimeMs = () => _uptimeMs,
    }, _log, StatusPath, () => _now);

    private Worker MakeWorker(IProcessWatcher watcher, Prewarmer? prewarm, BootQuiet quiet) => new(new AsterSettings(50, [_game]), _log, watcher, null, _streamer,
        new ReapplyChecker(CheckerEnv(), _log), prewarm: prewarm, quiet: quiet);

    // 재적용 검사가 계획을 읽으려 하면 센다 (계획 없음으로 끝남)
    private ReapplyEnv CheckerEnv() => new()
    {
        CanApply = false,
        GameRunning = _ => false,
        DiskBusy = _ => false,
        LoadPlan = _ =>
        {
            _layoutChecks++;
            throw new FileNotFoundException();
        },
        Extents = _ => throw new InvalidOperationException(),
        Stat = _ => throw new InvalidOperationException(),
        RealPath = Path.GetFullPath,
        Apply = (_, _, _, _) => throw new InvalidOperationException(),
    };

    private static void Tick(Worker w, DateTimeOffset start, Dictionary<string, DateTimeOffset> due) =>
        w.ReapplyTick(start, due, new HashSet<string>(StringComparer.Ordinal), CancellationToken.None);

    private void EndQuiet(BootQuiet q)
    {
        _uptimeMs = 5 * Minute;
        _idle = true;
        q.Refresh();
        Elapse(TimeSpan.FromSeconds(60));
        q.Refresh();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(10);
        }
    }

    [Fact]
    public void LayoutCheckIsDeferredWhileQuietAndRunsOnceAfterIt()
    {
        BootQuiet q = Quiet();
        Worker w = MakeWorker(new Silent(), null, q);
        DateTimeOffset start = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2); // 첫 검사 기한 (1 분) 이 지남
        var due = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        Tick(w, start, due);
        Tick(w, start, due);
        Assert.Equal(0, _layoutChecks);
        Assert.Single(_log.Lines, line => line.Contains("layout check and reapply deferred: boot quiet period", StringComparison.Ordinal));

        EndQuiet(q);
        Tick(w, start, due);
        Assert.Equal(1, _layoutChecks);
    }

    [Fact]
    public void LayoutCheckRunsRightAwayAfterTheBootWindow()
    {
        _uptimeMs = 6 * Minute;
        Worker w = MakeWorker(new Silent(), null, Quiet());
        var due = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        Tick(w, DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2), due);

        Assert.Equal(1, _layoutChecks);
        Assert.DoesNotContain(_log.Lines, line => line.Contains("deferred", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LauncherStartIsHeldWhileQuietAndRunsOnceAfterIt()
    {
        var emitted = new TaskCompletionSource();
        BootQuiet q = Quiet();
        Worker w = MakeWorker(new OneShot(new ProcessStart(1, "steam.exe", 0), emitted), MakePrewarm(), q);

        await w.StartAsync(CancellationToken.None);
        await emitted.Task;
        await WaitFor(() => _log.Lines.Any(line => line.Contains("prewarm steam.exe deferred: boot quiet period", StringComparison.Ordinal)));
        Assert.Empty(_streamer.Calls);

        EndQuiet(q);
        Tick(w, DateTimeOffset.UtcNow, new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));
        await WaitFor(() => !_streamer.Calls.IsEmpty && File.Exists(StatusPath));
        await w.StopAsync(CancellationToken.None);

        Assert.Equal("a", Assert.Single(_streamer.Calls).Game);
        Assert.Single(_log.Lines, line => line.Contains("prewarm steam.exe deferred: boot quiet period", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HeldLauncherStartIsReleasedAtSixtySecondsAndTwentySecondsOfIdleWhileTheQuietContinues()
    {
        var emitted = new TaskCompletionSource();
        BootQuiet q = Quiet();
        Worker w = MakeWorker(new OneShot(new ProcessStart(4, "steam.exe", 0), emitted), MakePrewarm(), q);
        DateTimeOffset start = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2); // 첫 검사 기한이 지남

        await w.StartAsync(CancellationToken.None);
        await emitted.Task;
        await WaitFor(() => _log.Lines.Any(line => line.Contains("prewarm steam.exe deferred: boot quiet period", StringComparison.Ordinal)));

        _idle = true;
        Tick(w, start, new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)); // 가동 1 분: 유휴 시작
        Assert.Empty(_streamer.Calls);

        Elapse(TimeSpan.FromSeconds(20));
        Tick(w, start, new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)); // 유휴 20 초: 계기만 풀림
        await WaitFor(() => !_streamer.Calls.IsEmpty && File.Exists(StatusPath));
        await w.StopAsync(CancellationToken.None);

        Assert.Equal("a", Assert.Single(_streamer.Calls).Game);
        Assert.True(q.Active); // 구간은 끝나지 않음
        Assert.Equal(0, _layoutChecks); // 배치 검사 등 다른 작업은 계속 보류
        Assert.Single(_log.Lines, line => line.Contains("held prewarm triggers released: 1 (boot + idle disk)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheShortBootGateRunsOnlyWhileAHeldLauncherTriggerWaitsInsideTheQuiet()
    {
        var emitted = new TaskCompletionSource();
        BootQuiet q = Quiet();
        Worker w = MakeWorker(new OneShot(new ProcessStart(5, "steam.exe", 0), emitted), MakePrewarm(), q);
        Assert.False(w.BootGatePending()); // 보류한 계기가 없으면 평소 틱만

        await w.StartAsync(CancellationToken.None);
        await emitted.Task;
        await WaitFor(() => _log.Lines.Any(line => line.Contains("prewarm steam.exe deferred: boot quiet period", StringComparison.Ordinal)));
        Assert.True(w.BootGatePending());

        _idle = true;
        w.BootGateTick(); // 가동 1 분: 유휴 시작
        Assert.True(w.BootGatePending());
        Elapse(TimeSpan.FromSeconds(20));
        w.BootGateTick(); // 유휴 20 초: 풀림, 보류한 계기를 돌림
        await WaitFor(() => !_streamer.Calls.IsEmpty && File.Exists(StatusPath));
        await w.StopAsync(CancellationToken.None);

        Assert.False(w.BootGatePending()); // 풀린 뒤에는 짧은 주기를 쓰지 않음
        Assert.Equal("a", Assert.Single(_streamer.Calls).Game);
        Assert.True(q.Active); // 구간은 그대로
    }

    [Fact]
    public async Task AHeldLauncherTriggerIsReleasedAtTheFirstFiveSecondGateTickAfterTwentySecondsOfIdle()
    {
        var emitted = new TaskCompletionSource();
        BootQuiet q = Quiet();
        Worker w = MakeWorker(new OneShot(new ProcessStart(6, "steam.exe", 0), emitted), MakePrewarm(), q);
        await w.StartAsync(CancellationToken.None);
        await emitted.Task;
        await WaitFor(() => _log.Lines.Any(line => line.Contains("prewarm steam.exe deferred: boot quiet period", StringComparison.Ordinal)));

        _idle = true;
        w.BootGateTick(); // 가동 60 초: 유휴 시작
        for (int i = 0; i < 3; i++)
        {
            Elapse(TimeSpan.FromSeconds(5));
            w.BootGateTick(); // 65·70·75 초
        }

        Assert.True(w.BootGatePending());
        Assert.Empty(_streamer.Calls);

        Elapse(TimeSpan.FromSeconds(5));
        w.BootGateTick(); // 80 초: 유휴 20 초
        await WaitFor(() => !_streamer.Calls.IsEmpty && File.Exists(StatusPath));
        await w.StopAsync(CancellationToken.None);

        Assert.Equal("a", Assert.Single(_streamer.Calls).Game);
        Assert.Single(_log.Lines, line => line.Contains("held prewarm triggers released: 1 (boot + idle disk)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALauncherTriggerHeldDuringTheThirtySecondWaitWakesTheLoopAtOnce()
    {
        int idleCalls = 0;
        var q = new BootQuiet(() => _uptimeMs, () =>
        {
            Interlocked.Increment(ref idleCalls);
            return false;
        }, _log, () => _now, () => _uptimeMs - _sleptMs);
        var open = new TaskCompletionSource();
        var waiting = new TaskCompletionSource();
        Worker w = MakeWorker(new GatedShot(new ProcessStart(7, "steam.exe", 0), open.Task), MakePrewarm(), q);
        w.TickWaitRegistered = () => waiting.TrySetResult(); // 재적용 루프가 첫 틱 (가동 1 분: 유휴 조회를 함) 뒤 30초 대기를 깨움 토큰에 연결한 순간

        await w.StartAsync(CancellationToken.None);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); // 이 신호 뒤의 트리거 보류는 그 대기를 반드시 깨움 (유휴 조회 횟수로 대기 진입을 추측하지 않음)
        int before = Volatile.Read(ref idleCalls);
        Assert.True(before >= 1);

        open.SetResult(); // 대기 도중 계기 도착: 보류하며 대기를 깨움
        await WaitFor(() => Volatile.Read(ref idleCalls) > before); // 5초나 30초를 기다리지 않고 바로 다음 틱이 돎
        await w.StopAsync(CancellationToken.None);

        Assert.True(Volatile.Read(ref idleCalls) > before);
    }

    [Fact]
    public async Task GameExeStartRunsWhileQuiet()
    {
        var emitted = new TaskCompletionSource();
        Worker w = MakeWorker(new OneShot(new ProcessStart(2, "a.exe", 0), emitted), MakePrewarm(), Quiet());

        await w.StartAsync(CancellationToken.None);
        await emitted.Task;
        await WaitFor(() => !_streamer.Calls.IsEmpty && File.Exists(StatusPath));
        await w.StopAsync(CancellationToken.None);

        Assert.Equal("a", Assert.Single(_streamer.Calls).Game);
        Assert.DoesNotContain(_log.Lines, line => line.Contains("a.exe", StringComparison.Ordinal) && line.Contains("deferred", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LauncherStartAfterTheBootWindowRunsAtOnce()
    {
        _uptimeMs = 6 * Minute;
        var emitted = new TaskCompletionSource();
        Worker w = MakeWorker(new OneShot(new ProcessStart(3, "steam.exe", 0), emitted), MakePrewarm(), Quiet());

        await w.StartAsync(CancellationToken.None);
        await emitted.Task;
        await WaitFor(() => !_streamer.Calls.IsEmpty && File.Exists(StatusPath));
        await w.StopAsync(CancellationToken.None);

        Assert.Equal("a", Assert.Single(_streamer.Calls).Game);
        Assert.DoesNotContain(_log.Lines, line => line.Contains("a.exe", StringComparison.Ordinal) && line.Contains("deferred", StringComparison.Ordinal));
    }

    private sealed class OneShot(ProcessStart start, TaskCompletionSource emitted) : IProcessWatcher
    {
        public async Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
        {
            onStart(start);
            emitted.TrySetResult();
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

    // gate 가 끝난 뒤에 트리거 하나를 알리는 감시기: 재적용 루프가 이미 30초 대기에 들어간 뒤 트리거가 들어오는 상황을 만듦.
    private sealed class GatedShot(ProcessStart start, Task gate) : IProcessWatcher
    {
        public async Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
        {
            try
            {
                await gate.WaitAsync(cancellationToken);
                onStart(start);
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 취소는 정상 종료
            }
        }
    }

    private sealed class Silent : IProcessWatcher
    {
        public Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    [Fact]
    public void LayoutCheckWaitsForAQuietWindowStartedByResumeFromSleep()
    {
        _uptimeMs = 6 * Minute; // 절전 복귀 뒤 가동 시간은 이미 5 분을 넘음
        BootQuiet q = Quiet();
        Worker w = MakeWorker(new Silent(), null, q);
        DateTimeOffset start = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);

        Tick(w, start, new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));
        Assert.Equal(1, _layoutChecks);

        Sleep(TimeSpan.FromHours(1)); // 절전: 벽시계와 가동 시간은 흐르고 절전 제외 시계는 멈춤
        Tick(w, start, new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));
        Assert.Equal(1, _layoutChecks);
        Assert.Single(_log.Lines, line => line.Contains("layout check and reapply deferred: boot quiet period", StringComparison.Ordinal));

        _uptimeMs += 5 * Minute; // BootDelay 가 지났지만 유휴 시작 (아직 구간)
        _idle = true;
        Tick(w, start, new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));
        Assert.Equal(1, _layoutChecks);

        Elapse(TimeSpan.FromSeconds(60)); // 60 초 유휴 → 구간 끝, 이번 틱에 실행
        Tick(w, start, new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal));
        Assert.Equal(2, _layoutChecks);
    }

    [Fact]
    public void LayoutCheckReadsNothingWhileTheDiskIsYieldedToAGameEvenIfTheStartEventWasMissed()
    {
        _uptimeMs = 6 * Minute;
        var coordinator = new DiskReadCoordinator(new AsterCache.Windows.VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log, () => 0);
        Worker w = new(new AsterSettings(50, [_game]), _log, new Silent(), null, _streamer, new ReapplyChecker(CheckerEnv(), _log), quiet: Quiet(), coordinator: coordinator);
        DateTimeOffset start = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        var due = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        coordinator.Yield(_game.OriginRoot, "game started", _game.Name); // 게임 판정 (GameRunning) 은 모르지만 디스크는 이미 양보 중

        Tick(w, start, due);

        Assert.Equal(0, _layoutChecks); // 계획 읽기·지문·extent 조회 전에 멈춤

        coordinator.Resume(_game.OriginRoot, _game.Name);
        due.Clear();
        Tick(w, start, due);

        Assert.Equal(1, _layoutChecks); // 양보가 풀리면 평소대로
    }

    [Fact]
    public void AutoRelocationIsHeldWhileQuietAndAllowedAfterIt()
    {
        BootQuiet q = Quiet();

        Assert.False(Worker.RelocationAllowed(q));
        Assert.Single(_log.Lines, line => line.Contains("auto relocation and new trace prewarm deferred: boot quiet period", StringComparison.Ordinal));

        EndQuiet(q);
        Assert.True(Worker.RelocationAllowed(q));
    }
}
