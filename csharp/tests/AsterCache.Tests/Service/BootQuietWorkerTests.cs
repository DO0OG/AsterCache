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
    private bool _idle;
    private int _layoutChecks;

    public BootQuietWorkerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private BootQuiet Quiet() => new(() => _uptimeMs, () => _idle, _log, () => _now);

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
        _now = _now.AddSeconds(60);
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

    private sealed class Silent : IProcessWatcher
    {
        public Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
