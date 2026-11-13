using System.Collections.Concurrent;
using System.Reflection;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 런처 트리거 예열이 같은 디스크의 다른 게임의 배경 예열을 기다리지 않고 멈추게 하는가 (선점), 멈춘 읽기의 이어 읽기·재시도, 락 대기의 취소, 트리거 예열의 보정 읽기 생략과 시간 상한.
public sealed class PrewarmTriggerPreemptTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(400);
    private readonly ListLog _log = new();
    private readonly BlockingStreamer _streamer = new();
    private long _nowTicks = DateTimeOffset.UnixEpoch.Ticks;

    private DateTimeOffset Now => new(Interlocked.Read(ref _nowTicks), TimeSpan.Zero);

    private static GameSettings Game(string name, string? trigger = null, bool otherDisk = false)
    {
        GameSettings g = Games.Make(name, name + ".exe") with { Prewarm = PrewarmSettings.Default with { TriggerExes = trigger is null ? [] : [trigger] } };
        return otherDisk ? g with { OriginRoot = @"C:\Games\" + name } : g;
    }

    private Prewarmer Make(PrewarmEnv? env = null, params GameSettings[] games) => new(games, _streamer, env ?? Env(), _log, null, () => Now);

    private static PrewarmEnv Env(Action<GameSettings, IReadOnlyList<ReadOp>, string?, DiskReadCoordinator.Lease?, CancellationToken, CancellationToken>? calibration = null, Action<string, CancellationToken>? yieldBackground = null) => new()
    {
        AvailableRamMib = () => 16000,
        GameRunning = _ => false,
        ZoneBytes = _ => 100L << 20,
        TraceExists = _ => true,
        DeviceCalibration = calibration,
        YieldBackground = yieldBackground ?? ((_, _) => { }),
    };

    // 배경 경로의 락 시도: 잡히면 바로 놓고 true (Monitor 는 잡은 스레드에서 놓아야 해서 같은 스레드에서 처리).
    private static bool TryIdle(Prewarmer p)
    {
        if (!p.TryEnterRunIdle())
        {
            return false;
        }

        Monitor.Exit(typeof(Prewarmer).GetField("_run", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(p)!);
        return true;
    }

    private static ProcessStart Launcher(string exe = "launcher.exe") => new(1, exe, 0);

    [Fact]
    public async Task TriggerStopsAnotherGamesBackgroundStreamAndReadsItsOwn()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b", "launcher.exe");
        Prewarmer p = Make(null, a, b);
        _streamer.Block("a");
        Task<PrewarmResult> background = Task.Run(() => p.Run(a, "test"));
        Assert.True(_streamer.Started("a").Wait(Bound));

        IReadOnlyList<PrewarmResult> results = await Task.Run(() => p.OnProcessStart(Launcher())).WaitAsync(Bound);

        PrewarmResult stopped = await background.WaitAsync(Bound);
        Assert.False(stopped.Ran);
        Assert.Equal(Prewarmer.Cancelled, stopped.SkipReason);
        Assert.Equal(Prewarmer.PreemptedByTrigger, stopped.CancelDetail);
        Assert.True(Assert.Single(results).Ran);
        Assert.Contains("b", _streamer.Calls);
        Assert.Contains(_log.Lines, l => l.Contains("a: prewarm cancelled (test)", StringComparison.Ordinal) && l.Contains(Prewarmer.PreemptedByTrigger, StringComparison.Ordinal));
        Assert.True(p.HasPendingWarm("a")); // 기존 대기 재시도에 올라감
        Assert.False(p.TriggerWaitPending());
    }

    [Fact]
    public async Task OnlyGamesTheTriggerWillReadAreTargetsSoACooledDownGamesBackgroundStreamIsStopped()
    {
        GameSettings b = Game("b", "launcher.exe");
        GameSettings c = Game("c", "launcher.exe");
        Prewarmer p = Make(null, b, c);
        Assert.True(p.Run(c, "test").Ran); // c 는 방금 예열해 쿨다운 안: 이 트리거는 c 를 읽지 않음
        _streamer.Block("c");
        Task<PrewarmResult> background = Task.Run(() => p.Run(c, "test", ignoreCooldown: true));
        Assert.True(_streamer.Started("c").Wait(Bound));

        IReadOnlyList<PrewarmResult> results = await Task.Run(() => p.OnProcessStart(Launcher())).WaitAsync(Bound);

        PrewarmResult stopped = await background.WaitAsync(Bound);
        Assert.Equal(Prewarmer.PreemptedByTrigger, stopped.CancelDetail); // c 의 스트림은 "대상 자신의 것" 으로 보호되지 않음
        Assert.True(results.Single(r => r.Game == "b").Ran);
        Assert.False(results.Single(r => r.Game == "c").Ran);
        Assert.False(p.TriggerWaitPending());
    }

    [Theory]
    [InlineData("own")]
    [InlineData("trigger")]
    [InlineData("other disk")]
    [InlineData("will skip")]
    public async Task TriggerDoesNotStopTheStreamWhenItIsNotAnotherGamesBackgroundRead(string how)
    {
        GameSettings a = Game("a", how == "trigger" ? "first.exe" : null, otherDisk: how == "other disk");
        GameSettings b = Game("b", "launcher.exe");
        Prewarmer p = Make(null, a, b);
        if (how == "will skip")
        {
            Assert.True(p.Run(b, "test").Ran); // 방금 예열해 쿨다운 안: 트리거는 읽지 않을 것
        }

        _streamer.Block(how == "own" ? "b" : "a");
        GameSettings holder = how == "own" ? b : a;
        Task<PrewarmResult> first = Task.Run(() => how == "trigger" ? p.OnProcessStart(Launcher("first.exe")).Single() : p.Run(holder, "test"));
        Assert.True(_streamer.Started(holder.Name).Wait(Bound));

        Task<IReadOnlyList<PrewarmResult>> trigger = Task.Run(() => p.OnProcessStart(Launcher()));
        await Task.Delay(Settle);

        Assert.False(first.IsCompleted); // 멈추지 않음: 아직 읽는 중
        Assert.Empty(_streamer.Stopped);
        _streamer.ReleaseAll();
        PrewarmResult done = await first.WaitAsync(Bound);
        await trigger.WaitAsync(Bound);
        Assert.True(done.Ran);
        Assert.False(p.TriggerWaitPending());
    }

    [Fact]
    public async Task TriggerWaitMarkKeepsBackgroundPathsOutUntilEveryOverlappingTriggerEnds()
    {
        Prewarmer p = Make(null, Game("a"), Game("b"));
        Assert.False(p.TriggerWaitPending());
        Assert.True(TryIdle(p));

        IDisposable first = p.BeginTriggerWait([Game("a")]);
        IDisposable second = p.BeginTriggerWait([Game("b")]);
        Assert.True(await Task.Run(() => !TryIdle(p))); // 표시가 서 있는 동안 락을 잡지 않음
        first.Dispose();
        first.Dispose(); // 두 번 지워도 다른 표시는 그대로
        Assert.True(p.TriggerWaitPending());
        Assert.True(await Task.Run(() => !TryIdle(p)));
        second.Dispose();

        Assert.False(p.TriggerWaitPending());
        Assert.True(await Task.Run(() => TryIdle(p)));
    }

    [Fact]
    public void TriggerWaitMarkIsClearedWhenTheTriggerThrows()
    {
        Prewarmer p = Make(Env(yieldBackground: (_, _) => throw new InvalidOperationException("boom")), Game("b", "launcher.exe"));

        Assert.Throws<InvalidOperationException>(() => p.OnProcessStart(Launcher()));

        Assert.False(p.TriggerWaitPending());
    }

    [Fact]
    public async Task TriggerGivesUpWaitingForTheLockWhenCancelledAndClearsItsMark()
    {
        GameSettings b = Game("b", "launcher.exe");
        Prewarmer p = Make(null, b);
        _streamer.Block("b"); // 대상 자신의 읽기라 멈추지 않고 트리거가 락을 기다림
        Task<PrewarmResult> holder = Task.Run(() => p.Run(b, "test"));
        Assert.True(_streamer.Started("b").Wait(Bound));
        using var cts = new CancellationTokenSource();
        Task<IReadOnlyList<PrewarmResult>> trigger = Task.Run(() => p.OnProcessStart(Launcher(), ct: cts.Token));
        await Task.Delay(Settle);
        Assert.False(trigger.IsCompleted);
        Assert.True(p.TriggerWaitPending());

        cts.Cancel();

        PrewarmResult r = Assert.Single(await trigger.WaitAsync(Bound)); // 락 보유자가 끝나기 전에 포기
        Assert.False(holder.IsCompleted);
        Assert.False(r.Ran);
        Assert.Equal(Prewarmer.Cancelled, r.SkipReason);
        Assert.Equal("caller cancelled", r.CancelDetail);
        Assert.False(p.TriggerWaitPending());
        _streamer.ReleaseAll();
        await holder.WaitAsync(Bound);
    }

    [Fact]
    public async Task TriggerThatWaitedAtLeastASecondLogsWhoItWaitedFor()
    {
        GameSettings b = Game("b", "launcher.exe");
        Prewarmer p = Make(null, b);
        _streamer.Block("b");
        Task<PrewarmResult> holder = Task.Run(() => p.Run(b, "test"));
        Assert.True(_streamer.Started("b").Wait(Bound));
        Task<IReadOnlyList<PrewarmResult>> trigger = Task.Run(() => p.OnProcessStart(Launcher()));
        await Task.Delay(Settle);

        Interlocked.Add(ref _nowTicks, TimeSpan.FromSeconds(5).Ticks);
        _streamer.ReleaseAll();
        await holder.WaitAsync(Bound);
        await trigger.WaitAsync(Bound);

        Assert.Contains(_log.Lines, l => l.Contains("b: trigger prewarm waited 5 s for b", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABackgroundRunStartedAfterTheTriggerMarkIsNotReadAndWaitsInTheRetryQueue()
    {
        GameSettings a = Game("a");
        Prewarmer p = Make(null, a, Game("b"));
        using IDisposable mark = p.BeginTriggerWait([Game("b")]);

        PrewarmResult r = await Task.Run(() => p.Run(a, "test")); // 표시를 세우기 전에 락 대기에 들어가 있던 경로와 같음

        Assert.Equal(Prewarmer.PreemptedByTrigger, r.CancelDetail);
        Assert.Empty(_streamer.Calls);
        Assert.True(p.HasPendingWarm("a"));
    }

    [Fact]
    public void CalibrationIsSkippedForTheTriggerAndRunsForOtherReasons()
    {
        int calls = 0;
        GameSettings b = Game("b", "launcher.exe");
        Prewarmer p = Make(Env((_, _, _, _, _, _) => calls++), b);

        Assert.True(Assert.Single(p.OnProcessStart(Launcher())).Ran);
        Assert.Equal(0, calls);

        Assert.True(p.Run(b, "test", ignoreCooldown: true).Ran);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void CalibrationThatReachesTheLimitIsCutOffAndThePrewarmContinues()
    {
        GameSettings b = Game("b");
        Prewarmer p = Make(Env((_, _, _, _, _, limit) => limit.WaitHandle.WaitOne(Bound)), b); // 상한까지 읽는 표본
        p.CalibrationLimit = TimeSpan.FromMilliseconds(50);

        PrewarmResult r = p.Run(b, "test");

        Assert.True(r.Ran);
        Assert.Contains(_log.Lines, l => l.Contains("b: device calibration stopped at the", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("prewarm phases (test)", StringComparison.Ordinal) && l.Contains("(capped)", StringComparison.Ordinal));
    }

    // 지정한 게임의 스트림은 멈추라는 신호나 해제까지 읽는 척하며 기다립니다 (멈춘 게임 이름을 기록).
    private sealed class BlockingStreamer : IGameStreamer
    {
        private readonly ConcurrentDictionary<string, ManualResetEventSlim> _started = new();
        private readonly ConcurrentDictionary<string, bool> _blocking = new();
        private readonly ManualResetEventSlim _release = new();

        public ConcurrentQueue<string> Calls { get; } = new();

        public ConcurrentQueue<string> Stopped { get; } = new();

        public void Block(string game) => _blocking[game] = true;

        public ManualResetEventSlim Started(string game) => _started.GetOrAdd(game, _ => new ManualResetEventSlim());

        public void ReleaseAll() => _release.Set();

        public StreamStats Stream(GameSettings game) => new(100, 2, 0, 0.01);

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            Calls.Enqueue(game.Name);
            if (_blocking.ContainsKey(game.Name))
            {
                Started(game.Name).Set();
                WaitHandle.WaitAny([stop.WaitHandle, _release.WaitHandle], Bound);
                if (stop.IsCancellationRequested)
                {
                    Stopped.Enqueue(game.Name);
                    return (new StreamStats(0, 0, 0, 0, Aborted: true), false);
                }
            }

            return (new StreamStats(100, 2, 0, 0.01), true);
        }
    }
}
