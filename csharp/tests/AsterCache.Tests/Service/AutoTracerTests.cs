using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Service;

/// 자동 트레이스: 시작 조건, 종료 조건 (게임 종료·시간·읽기량), 결과 기록. ETW 세션은 가짜입니다.
public sealed class AutoTracerTests : IDisposable
{
    private readonly AutoRig _rig = new();
    private readonly FakeTrace _trace = new();
    private readonly GameSettings _game;
    private readonly AutoTracer _tracer;

    public AutoTracerTests()
    {
        Directory.CreateDirectory(Path.Combine(_rig.Dir, "plans"));
        string plan = Path.Combine(_rig.Dir, "plans", "foo.plan.json");
        _game = new GameSettings("foo", ["foo.exe"], plan + ".prefetch.json", plan, @"D:\Games\Foo", [@"D:\Games\Foo"], "GAMES", 1440) { Source = "auto" };
        _rig.Settings = new AsterSettings(50, [_game]) { Auto = AutoSettings.Default with { TraceCapSeconds = 30, TraceCapMib = 64, Learn = LearnSettings.Default with { Enabled = false } } };
        _rig.World.Session = _trace;
        _tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings);
    }

    public void Dispose() => _rig.Dispose();

    private string Raw => AutoTracer.RawTracePath(_game);

    private void Reads(params (long Offset, long Length)[] reads)
    {
        _trace.Records.Add(TraceRecord.Start(DateTime.UtcNow, 100, 1, "foo.exe"));
        foreach ((long offset, long length) in reads)
        {
            _trace.Records.Add(TraceRecord.Read(DateTime.UtcNow, 100, @"D:\Games\Foo\a.bin", offset, length));
        }

        _trace.Records.Add(TraceRecord.Read(DateTime.UtcNow, 100, @"D:\Other\b.bin", 0, 4096)); // 원점 루트 밖
        _trace.Records.Add(TraceRecord.Read(DateTime.UtcNow, 999, @"D:\Games\Foo\c.bin", 0, 4096)); // 게임 프로세스가 아님
    }

    [Fact]
    public void StartOpensOneSessionOnlyWhenTheRawTraceIsMissingAndNoOtherRuns()
    {
        _tracer.Start(_game);

        Assert.True(_tracer.Active);
        Assert.Equal(AutoStage.Tracing, _rig.Status.Get("foo")!.Stage);
        _tracer.Start(_game with { Name = "other" });
        Assert.Equal(1, _rig.World.TraceStarts);

        _tracer.Shutdown();
        _rig.World.Files.Add(Raw);
        _tracer.Start(_game);
        Assert.Equal(1, _rig.World.TraceStarts); // 원시 트레이스가 이미 있음
    }

    [Fact]
    public void ABootTraceAloneDoesNotStopTheFirstRunRecordingAndTheRawTraceIsWrittenNextToIt()
    {
        string boot = AutoTracer.BootTracePath(_game);
        _rig.World.Files.Add(boot); // 가져온 프로필
        Reads((0, 65536));

        _tracer.Start(_game);

        Assert.True(_tracer.Active);
        Assert.Equal(1, _rig.World.TraceStarts);
        _rig.World.Procs = [];
        _rig.World.Running = false;
        _tracer.Step();
        Assert.True(File.Exists(Raw));
        Assert.EndsWith("foo.boot.trace.json", boot, StringComparison.Ordinal);
        bool Exists(string path) => _rig.World.Files.Contains(path) || File.Exists(path);
        Assert.Equal(Raw, AutoTracer.PlanTracePath(_game, Exists)); // 원시가 생기면 그쪽이 우선
        File.Delete(Raw);
        Assert.Equal(boot, AutoTracer.PlanTracePath(_game, Exists));
        _rig.World.Files.Remove(boot);
        Assert.Null(AutoTracer.PlanTracePath(_game, Exists));
    }

    [Fact]
    public void GameExitEndsTheTraceAndWritesTheSameFilesAsAManualCollection()
    {
        Reads((0, 65536), (65536, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 5L << 20)];
        _rig.World.Running = true;
        _tracer.Step();
        Assert.True(_tracer.Active);

        _rig.World.Procs = [];
        _rig.World.Running = false;
        _tracer.Step();

        Assert.False(_tracer.Active);
        Assert.True(_trace.Stopped);
        Assert.True(_trace.Disposed);
        var events = (List<object?>)JsonRead.ReadFile(Raw)!;
        Assert.Equal(2, events.Count);
        Assert.Equal(@"D:\Games\Foo\a.bin", ((JObject)events[0]!)["path"]);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(Raw)!, "foo.trace.run1.json")));
        Assert.Equal(AutoStage.WaitingIdle, _rig.Status.Get("foo")!.Stage);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("auto trace ended (game exited)", StringComparison.Ordinal));
    }

    [Fact]
    public void TimeCapEndsTheTraceAtExactlyTheConfiguredSeconds()
    {
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 1L << 20)];
        _rig.World.Running = true;

        _rig.World.Now += TimeSpan.FromSeconds(29);
        _tracer.Step();
        Assert.True(_tracer.Active);

        _rig.World.Now += TimeSpan.FromSeconds(1);
        _tracer.Step();
        Assert.False(_tracer.Active);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("(time cap)", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadCapCountsOnlyTheGrowthSinceTheSessionOpenedIncludingLaterProcesses()
    {
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 500L << 20)]; // 첫 표본은 기준
        _rig.World.Running = true;
        _tracer.Step();

        _rig.World.Procs = [(100, 530L << 20), (200, 33L << 20)]; // 30 + 33 (세션 뒤에 시작한 프로세스는 0 에서) = 63 MiB
        _tracer.Step();
        Assert.True(_tracer.Active);

        _rig.World.Procs = [(100, 530L << 20), (200, 34L << 20)]; // 64 MiB
        _tracer.Step();
        Assert.False(_tracer.Active);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("(read cap)", StringComparison.Ordinal));
    }

    [Fact]
    public void GameProcessWhoseReadCounterCannotBeOpenedStillCountsAsRunning()
    {
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = []; // 보호된 프로세스: 읽기량을 못 열어 목록에는 없지만 이름으로는 실행 중
        _rig.World.Running = true;

        _tracer.Step();

        Assert.True(_tracer.Active);
        _rig.World.Running = false;
        _tracer.Step();
        Assert.False(_tracer.Active);
    }

    [Theory]
    [InlineData("system")]
    [InlineData("ssd")]
    [InlineData("unknown")]
    public void SystemVolumeSsdAndUnknownDrivesNeverStartATraceAndTheReasonIsLoggedOnce(string kind)
    {
        switch (kind)
        {
            case "system": (_rig.World.SystemDrive, _rig.World.Hdd["D:"]) = ("D:", false); break; // 시스템 SSD (시스템 HDD 는 읽기 전용 트레이스를 허용)
            case "ssd": _rig.World.Hdd["D:"] = false; break;
            default: _rig.World.Hdd["D:"] = null; break;
        }

        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings); // 시스템 드라이브는 만들 때 정해짐
        tracer.Start(_game);
        tracer.Start(_game);

        Assert.False(tracer.Active);
        Assert.Equal(0, _rig.World.TraceStarts);
        Assert.Equal(AutoStage.Skipped, _rig.Status.Get("foo")!.Stage);
        Assert.Single(_rig.Log.Lines, l => l.Contains("auto trace and relocation skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void NoReadsCapturedIsAFailureAndLeavesNoTraceSoTheNextRunTriesAgain()
    {
        _tracer.Start(_game);
        _rig.World.Procs = [];

        _tracer.Step();

        AutoGameEntry e = _rig.Status.Get("foo")!;
        Assert.Equal((AutoStage.Failed, "no reads captured"), (e.Stage, e.Detail));
        Assert.False(File.Exists(Raw));
        Assert.True(_trace.Disposed);

        _rig.World.Session = new FakeTrace();
        _tracer.Start(_game);
        Assert.Equal(2, _rig.World.TraceStarts);
    }

    [Fact]
    public void SessionThatCannotStartIsAFailureWithALogAndNoActiveTrace()
    {
        _rig.World.StartTraceThrows = new InvalidOperationException("denied");

        _tracer.Start(_game);

        Assert.False(_tracer.Active);
        Assert.Equal((AutoStage.Failed, "trace session failed to start"), (_rig.Status.Get("foo")!.Stage, _rig.Status.Get("foo")!.Detail));
        Assert.Contains(_rig.Log.Lines, l => l.StartsWith("Error: foo: auto trace could not start", StringComparison.Ordinal));
    }

    [Fact]
    public void ShutdownClosesTheSessionWithoutWritingAnything()
    {
        Reads((0, 65536));
        _tracer.Start(_game);

        _tracer.Shutdown();

        Assert.True(_trace.Disposed);
        Assert.False(_trace.Stopped);
        Assert.False(File.Exists(Raw));
        Assert.False(_tracer.Active);
        _tracer.Step(); // 돌고 있는 것이 없으면 아무 일도 없음
    }
}
