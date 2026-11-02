using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tests.Prefetch;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Service;

/// 첫 실행 학습: 자동 트레이스 세션의 알림이 선행 읽기 엔진으로 가고, 상태가 learning 으로 보이며, 끝나면 트레이스가 저장됩니다. ETW 세션과 파일 읽기는 가짜입니다.
public sealed class AutoLearnTests : IDisposable
{
    private const long Mib = 1L << 20;
    private const string File1 = @"D:\Games\Foo\a.bin";

    private readonly AutoRig _rig = new();
    private readonly FakeTrace _trace = new();
    private readonly FakeReadIo _io = new();
    private readonly GameSettings _game;
    private readonly AutoTracer _tracer;

    public AutoLearnTests()
    {
        Directory.CreateDirectory(Path.Combine(_rig.Dir, "plans"));
        string plan = Path.Combine(_rig.Dir, "plans", "foo.plan.json");
        _game = new GameSettings("foo", ["foo.exe"], plan + ".prefetch.json", plan, @"D:\Games\Foo", [@"D:\Games\Foo"], "GAMES", 1440) { Source = "auto" };
        Configure(LearnSettings.Default with { Enabled = true, Mode = LearnSettings.GapMode }); // 기존 시험은 틈 모드의 규칙을 봄
        _rig.World.Session = _trace;
        _io.Sizes[File1] = 12 * Mib;
        _tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings, new LearnEnv { OpenIo = () => _io, AvailableRamMib = () => 1 << 20 });
    }

    public void Dispose() => _rig.Dispose();

    private void Configure(LearnSettings learn) =>
        _rig.Settings = new AsterSettings(50, [_game]) { Auto = AutoSettings.Default with { TraceCapSeconds = 600, TraceCapMib = 6144, Learn = learn } };

    // 같은 파일을 1 MiB 씩 순차로 여덟 번 읽음: 순차로 판정돼 선행 읽기가 시작되는 최소 패턴 (창 중 [8, 12) MiB 만 파일 안, 틈 100 ms 뒤에 시작).
    private static void SequentialReads(Action<string, long, long> notify)
    {
        for (int i = 0; i < 8; i++)
        {
            notify(File1, i * Mib, Mib);
        }
    }

    private static void WaitFor(Func<bool> done)
    {
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < until)
        {
            Thread.Sleep(10);
        }

        Assert.True(done());
    }

    [Fact]
    public void LearnEnabledPassesAListenerAndShowsTheLearningStage()
    {
        _tracer.Start(_game);

        Assert.NotNull(_rig.World.Listener);
        AutoGameEntry e = _rig.Status.Get("foo")!;
        Assert.Equal((AutoStage.Learning, "0 MiB read ahead"), (e.Stage, e.Detail));
        Assert.Equal(1, _rig.World.TraceStarts); // 세션은 트레이스와 하나
        _tracer.Shutdown();
        Assert.True(_io.Disposed);
    }

    [Fact]
    public void LearnDisabledKeepsThePlainTracingStageWithoutAListener()
    {
        Configure(LearnSettings.Default with { Enabled = false });

        _tracer.Start(_game);

        Assert.Null(_rig.World.Listener);
        Assert.Equal(AutoStage.Tracing, _rig.Status.Get("foo")!.Stage);
        _tracer.Shutdown();
    }

    [Fact]
    public void SessionCallbacksReachTheEngineAndTheStatusUpdatesNoMoreThanEveryTenSeconds()
    {
        _tracer.Start(_game);
        SequentialReads(_rig.World.Listener!.OnRead);
        WaitFor(() => _io.ReadBytes == 4 * Mib);
        _rig.World.Running = true;
        _rig.World.Procs = [(100, 1L << 20)];

        _rig.World.Now += TimeSpan.FromSeconds(9);
        _tracer.Step();
        Assert.Equal("0 MiB read ahead", _rig.Status.Get("foo")!.Detail);

        _rig.World.Now += TimeSpan.FromSeconds(1);
        _tracer.Step();
        AutoGameEntry e = _rig.Status.Get("foo")!;
        Assert.Equal((AutoStage.Learning, "4 MiB read ahead"), (e.Stage, e.Detail));
        _tracer.Shutdown();
    }

    [Fact]
    public void WindowAndCapComeFromTheSettings()
    {
        _io.Sizes[File1] = 500 * Mib;
        Configure(LearnSettings.Default with { Enabled = true, WindowMib = 4, MaxTotalMib = 1, Mode = LearnSettings.GapMode });
        _tracer.Start(_game);

        SequentialReads(_rig.World.Listener!.OnRead);

        WaitFor(() => _io.ReadBytes == Mib);
        _tracer.Shutdown(); // 읽기 스레드를 멈추고 기다린 뒤에 확인 (고정 대기 없이)

        Assert.Equal(Mib, _io.ReadBytes); // 창 3 MiB 라도 상한 1 MiB 에서 멈춤
    }

    [Fact]
    public void GameExitStopsBothSavesTheTraceAndLogsTheLearnSummary()
    {
        _trace.Records.Add(TraceRecord.Start(DateTime.UtcNow, 100, 1, "foo.exe"));
        _trace.Records.Add(TraceRecord.Read(DateTime.UtcNow, 100, File1, 0, 65536));
        _tracer.Start(_game);
        SequentialReads(_rig.World.Listener!.OnRead);
        WaitFor(() => _io.ReadBytes == 4 * Mib);

        _rig.World.Running = false;
        _tracer.Step();

        Assert.False(_tracer.Active);
        Assert.True(_trace.Stopped);
        Assert.True(_io.Disposed);
        Assert.Equal(AutoStage.WaitingIdle, _rig.Status.Get("foo")!.Stage);
        Assert.True(File.Exists(AutoTracer.RawTracePath(_game)));
        Assert.Contains(_rig.Log.Lines, l => l.Contains("learn ended, 4 MiB read ahead, hit estimate 0% of 8 MiB game reads", StringComparison.Ordinal));
        Assert.Contains(_rig.Log.Lines, l => l.EndsWith("gap_count 1, yielded_requests 0, read_ahead_while_idle_bytes 4194304, saturated_ms 0, paused_requests 0", StringComparison.Ordinal)); // 틈 하나에 4 MiB, 게임 읽기에 양보한 적 없음
    }

    [Fact]
    public void AdaptiveModeWithALowThresholdWaitsOutTheSaturatedWindowAndLogsSaturatedMs()
    {
        Configure(LearnSettings.Default with { Enabled = true, Mode = LearnSettings.AdaptiveMode, SaturationMbps = 1 });
        _trace.Records.Add(TraceRecord.Start(DateTime.UtcNow, 100, 1, "foo.exe"));
        _trace.Records.Add(TraceRecord.Read(DateTime.UtcNow, 100, File1, 0, 65536));
        _tracer.Start(_game);
        SequentialReads(_rig.World.Listener!.OnRead); // 8 MiB / 0.5 s 는 기준 1 MiB/s 이상: 창이 비는 약 500 ms 뒤에야 읽음

        WaitFor(() => _io.ReadBytes == 4 * Mib);
        _rig.World.Running = false;
        _tracer.Step();

        Assert.Contains(_rig.Log.Lines, l => System.Text.RegularExpressions.Regex.IsMatch(l, @"saturated_ms [1-9]\d*, paused_requests 0$"));
    }

    [Fact]
    public void StreamModeIsTheDefaultAndReadsAheadWithoutAGap()
    {
        Configure(LearnSettings.Default with { Enabled = true });
        _trace.Records.Add(TraceRecord.Start(DateTime.UtcNow, 100, 1, "foo.exe"));
        _trace.Records.Add(TraceRecord.Read(DateTime.UtcNow, 100, File1, 0, 65536));
        _tracer.Start(_game);
        SequentialReads(_rig.World.Listener!.OnRead); // 읽기 사이에 100 ms 틈이 없어도 읽음

        WaitFor(() => _io.ReadBytes == 4 * Mib);
        _rig.World.Running = false;
        _tracer.Step();

        Assert.Contains(_rig.Log.Lines, l => l.EndsWith("gap_count 0, yielded_requests 0, read_ahead_while_idle_bytes 4194304, saturated_ms 0, paused_requests 0", StringComparison.Ordinal));
    }

    [Fact]
    public void LateReadsReachTheEngineAndAreCountedButNotAsGameReads()
    {
        _trace.Records.Add(TraceRecord.Start(DateTime.UtcNow, 100, 1, "foo.exe"));
        _trace.Records.Add(TraceRecord.Read(DateTime.UtcNow, 100, File1, 0, 65536));
        _tracer.Start(_game);
        SequentialReads(_rig.World.Listener!.OnLateRead!);
        WaitFor(() => _io.ReadBytes == 4 * Mib);

        _rig.World.Running = false;
        _tracer.Step();

        Assert.Contains(_rig.Log.Lines, l => l.Contains("0 MiB game reads, 0 resolved reads, 8 late reads, 1 sequential files", StringComparison.Ordinal));
    }

    [Fact]
    public void SessionThatCannotStartAlsoReleasesTheEngine()
    {
        _rig.World.StartTraceThrows = new InvalidOperationException("denied");

        _tracer.Start(_game);

        Assert.False(_tracer.Active);
        Assert.True(_io.Disposed);
        Assert.Equal(AutoStage.Failed, _rig.Status.Get("foo")!.Stage);
    }
}
