using AsterCache.Core.Discovery;
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
        _rig.Settings = new AsterSettings(50, [_game]) { Auto = AutoSettings.Default with { TraceRuns = 1, TraceCapSeconds = 30, TraceCapMib = 64, Learn = LearnSettings.Default with { Enabled = false } } };
        _rig.World.Session = _trace;
        _tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings) { MinRunBytes = 0 };
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
    [InlineData("registered", "vanguard")]
    [InlineData("folder", "eac")]
    [InlineData("riot", "vanguard")]
    [InlineData("detect-failed", "unknown")]
    public void AntiCheatGamesNeverStartATraceInAnyRunAndTheReasonIsLoggedOnce(string how, string kind)
    {
        GameSettings g = _game;
        switch (how)
        {
            case "registered": g = _game with { AntiCheat = kind }; break;
            case "folder": _rig.World.AntiCheats[g.OriginRoot] = kind; break;
            case "riot": g = AntiCheat.WithRiotRule(new AsterSettings(50, [_game with { OriginRoot = @"D:\Riot Games\League of Legends" }]), () => true).Games[0]; break;
            default: _rig.World.AntiCheats[g.OriginRoot] = AutoEnv.AntiCheatUnknown; break;
        }

        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { TraceRuns = 3 } };
        _tracer.Start(g);
        _rig.World.Files.Add(Raw); // run 2 도 막힘
        _tracer.Start(g);

        Assert.False(_tracer.Active);
        Assert.Equal(0, _rig.World.TraceStarts);
        Assert.Single(_rig.Log.Lines, l => l.Contains($"foo: trace skipped (anti-cheat: {kind})", StringComparison.Ordinal));
    }

    [Fact]
    public void AntiCheatSetAfterACachedNoneIsRefusedAndStopsARunningTrace()
    {
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { TraceRuns = 3 } };
        _tracer.Start(_game); // 없음으로 캐시됨
        Assert.True(_tracer.Active);
        GameSettings vanguard = _game with { AntiCheat = "vanguard" };
        _rig.Settings = _rig.Settings with { Games = [vanguard] };

        _tracer.Step(); // 돌던 트레이스도 새 금지 상태를 봄

        Assert.False(_tracer.Active);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("foo: trace stopped (anti-cheat: vanguard)", StringComparison.Ordinal));
        _rig.World.Files.Add(Raw);
        _tracer.Start(vanguard);
        Assert.Equal(1, _rig.World.TraceStarts);
    }

    [Fact]
    public void RicochetAndPlainGamesAreStillTracedAndRiotNeedsVanguardOnTheSystem()
    {
        _rig.World.AntiCheats[_game.OriginRoot] = "ricochet";
        _tracer.Start(_game);
        Assert.True(_tracer.Active);
        _tracer.Shutdown();

        _tracer.Start(_game with { OriginRoot = @"D:\Riot Games\League of Legends" });
        Assert.True(_tracer.Active); // Vanguard 가 없는 시스템의 Riot 폴더
    }

    [Fact]
    public void ARegisteredExeThatLeftButWhoseChildStillRunsUnderTheGameFolderKeepsTheTraceGoingAndItsReadsCount()
    {
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 1L << 20)];
        _rig.World.Running = true;
        _tracer.Step();

        _rig.World.Running = false; // cod.exe 가 빠짐
        _rig.World.Procs = [];
        _rig.World.RootPids = [200]; // sp26-cod.exe 는 폴더 아래
        _rig.World.Reads[200] = 40L << 20;
        _tracer.Step();
        Assert.True(_tracer.Active);
        _rig.World.Reads[200] = 105L << 20; // 세션 뒤에 시작한 프로세스는 0 에서 세므로 64 MiB 를 넘음
        _tracer.Step();
        Assert.False(_tracer.Active);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("(read cap)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTraceEndsAsGameExitedOnlyWhenNeitherTheRegisteredExeNorAnyProcessUnderTheFolderRemains()
    {
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = [];
        _rig.World.Running = false;
        _rig.World.RootPids = [200];
        _rig.World.Reads[200] = 1L << 20;
        _tracer.Step();
        Assert.True(_tracer.Active);

        _rig.World.RootPids = [];
        _tracer.Step();
        Assert.False(_tracer.Active);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("auto trace ended (game exited)", StringComparison.Ordinal));
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

        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings) { MinRunBytes = 0 }; // 시스템 드라이브는 만들 때 정해짐
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

    // ---- 추가 플레이 기록 (auto.trace_runs) ----

    private void FirstRunOfTwoBlocks()
    {
        Reads((0, 65536), (65536, 65536));
        _tracer.Start(_game);
        _rig.World.Running = false;
        _tracer.Step();
        Assert.False(_tracer.Active);
        _trace.Records.Clear();
    }

    private void SetRuns(int runs) => _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { TraceRuns = runs } };

    [Fact]
    public void RunCountIsTheConsecutiveRunFilesOrOneForALoneRawTraceOrZeroWithoutIt()
    {
        var files = new HashSet<string>();
        Assert.Equal(0, AutoTracer.RunCount(_game, files.Contains));
        files.Add(TraceWriter.RunPath(Raw, 1)); // 원시 트레이스가 없으면 남은 run 파일은 세지 않음 (업데이트 뒤 옛 파일)
        Assert.Equal(0, AutoTracer.RunCount(_game, files.Contains));
        files.Remove(TraceWriter.RunPath(Raw, 1));
        files.Add(Raw);
        Assert.Equal(1, AutoTracer.RunCount(_game, files.Contains));
        files.UnionWith([TraceWriter.RunPath(Raw, 1), TraceWriter.RunPath(Raw, 2), TraceWriter.RunPath(Raw, 4)]);
        Assert.Equal(2, AutoTracer.RunCount(_game, files.Contains)); // 끊기면 거기까지
    }

    [Fact]
    public void SecondRunIsSavedAsRun2AndMergedIntoTheRawTraceInTheFirstRunsOrderWithoutTouchingTheStage()
    {
        SetRuns(3);
        FirstRunOfTwoBlocks();
        _rig.Status.Complete("foo", "auto", _rig.World.Now, counted: false, null); // 재배치까지 끝난 게임

        Reads((131072, 65536), (0, 65536)); // 새 블록 2 와 이미 있는 블록 0
        _tracer.Start(_game);
        Assert.True(_tracer.Active);
        Assert.Null(_rig.World.Listener); // 두 번째부터는 선행 읽기를 켜지 않음
        Assert.Equal(AutoStage.Done, _rig.Status.Get("foo")!.Stage);
        _tracer.Step();

        Assert.Equal(AutoStage.Done, _rig.Status.Get("foo")!.Stage);
        Assert.Equal(2, AutoTracer.RunCount(_game, File.Exists));
        var run2 = (List<object?>)JsonRead.ReadFile(TraceWriter.RunPath(Raw, 2))!;
        Assert.Equal([131072L, 0L], run2.Select(r => ((JObject)r!)["offset"]));
        var merged = (List<object?>)JsonRead.ReadFile(Raw)!;
        Assert.Equal([0L, 65536L, 131072L], merged.Select(r => ((JObject)r!)["offset"])); // run1 의 순서 뒤에 새 블록
        Assert.False(File.Exists(Raw + ".tmp"));
        Assert.Contains(_rig.Log.Lines, l => l.Contains("(run 2, merged)", StringComparison.Ordinal));
    }

    [Fact]
    public void ARunThatReadsLessThanTheMinimumIsNotSavedAndDoesNotUseUpARun()
    {
        SetRuns(3);
        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings); // 기본 최소량 64 MiB
        _trace.Records.Clear();
        Reads((0, 40L << 20), (40L << 20, 40L << 20)); // 한 이벤트는 64 MiB 까지
        tracer.Start(_game);
        tracer.Step();
        Assert.Equal(1, AutoTracer.RunCount(_game, File.Exists));
        _trace.Records.Clear();

        Reads((131072, 65536)); // 메뉴만 켠 정도
        tracer.Start(_game);
        tracer.Step();

        Assert.Equal(1, AutoTracer.RunCount(_game, File.Exists));
        Assert.False(File.Exists(TraceWriter.RunPath(Raw, 2)));
        Assert.Contains(_rig.Log.Lines, l => l.Contains("run 2 too small", StringComparison.Ordinal) && l.Contains("not saved and not counted", StringComparison.Ordinal));
        tracer.Start(_game); // 회차가 남아 있으니 다음 플레이도 기록함
        Assert.True(tracer.Active);
        tracer.Shutdown();
    }

    [Fact]
    public void ARunWithUnderOnePercentOfTheFirstRunsEventsIsNotSaved()
    {
        SetRuns(3);
        Directory.CreateDirectory(Path.GetDirectoryName(Raw)!);
        TraceWriter.Write([[.. Enumerable.Range(0, 200).Select(i => new AsterCache.Core.Trace.TraceEvent(@"D:\Games\Foo\a.bin", i * 65536L, 65536))]], Raw);
        Reads((131072L * 1000, 65536)); // 이벤트 1 건 < 200 건의 1 %

        _tracer.Start(_game);
        _tracer.Step();

        Assert.Equal(1, AutoTracer.RunCount(_game, File.Exists));
        Assert.Contains(_rig.Log.Lines, l => l.Contains("1 events against 200 in run 1", StringComparison.Ordinal));
    }

    [Fact]
    public void FilesWrittenByTheGameOrChangedSinceThePreviousRunAreMarkedVolatile()
    {
        SetRuns(3);
        FirstRunOfTwoBlocks();
        DateTimeOffset firstRunSaved = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        _rig.World.FileTimes[TraceWriter.RunPath(Raw, 1)] = firstRunSaved;
        _rig.World.Stat = p => new AsterCache.Core.Plan.Fingerprint(p, 1, ((firstRunSaved.UtcTicks - AsterCache.Core.Plan.Fingerprint.UnixEpochTicks) + (p.EndsWith("c.bin", StringComparison.Ordinal) ? TimeSpan.TicksPerHour : -TimeSpan.TicksPerHour)) * 100);
        _trace.Records.Add(TraceRecord.Start(DateTime.UtcNow, 100, 1, "foo.exe"));
        _trace.Records.Add(TraceRecord.Read(DateTime.UtcNow, 100, @"D:\Games\Foo\a.bin", 0, 65536));
        _trace.Records.Add(TraceRecord.Read(DateTime.UtcNow, 100, @"D:\Games\Foo\b.bin", 0, 65536));
        _trace.Records.Add(TraceRecord.Read(DateTime.UtcNow, 100, @"D:\Games\Foo\c.bin", 0, 65536));
        _trace.Records.Add(TraceRecord.Write(DateTime.UtcNow, 100, @"D:\Games\Foo\b.bin"));
        _trace.Records.Add(TraceRecord.Write(DateTime.UtcNow, 999, @"D:\Games\Foo\a.bin")); // 게임 프로세스가 아님

        _tracer.Start(_game);
        _tracer.Step();

        IReadOnlyDictionary<string, string> marks = AutoTracer.ReadVolatile(AutoTracer.VolatilePath(_game));
        Assert.Equal(["opened for write", "changed between runs"], [marks[@"D:\Games\Foo\b.bin"], marks[@"D:\Games\Foo\c.bin"]]);
        Assert.False(marks.ContainsKey(@"D:\Games\Foo\a.bin"));
    }

    [Fact]
    public void NoMoreRunsAreRecordedOnceTheConfiguredCountIsReached()
    {
        SetRuns(2);
        FirstRunOfTwoBlocks();
        Reads((131072, 65536));
        _tracer.Start(_game);
        _tracer.Step();
        Assert.Equal(2, AutoTracer.RunCount(_game, File.Exists));

        _tracer.Start(_game);

        Assert.False(_tracer.Active);
        Assert.Equal(2, _rig.World.TraceStarts);
    }

    [Fact]
    public void ARawTraceWithoutRunFilesCountsAsRun1AndIsCopiedBeforeMerging()
    {
        SetRuns(3);
        FirstRunOfTwoBlocks();
        File.Delete(TraceWriter.RunPath(Raw, 1)); // 옛 방식으로 모은 트레이스
        Assert.Equal(1, AutoTracer.RunCount(_game, File.Exists));

        Reads((131072, 65536));
        _tracer.Start(_game);
        _tracer.Step();

        Assert.Equal(2, AutoTracer.RunCount(_game, File.Exists));
        Assert.True(File.Exists(TraceWriter.RunPath(Raw, 1)));
        Assert.Equal(3, ((List<object?>)JsonRead.ReadFile(Raw)!).Count);
    }

    [Fact]
    public void AnExtraRunThatCapturesNothingOrCannotStartKeepsTheTraceTheCountAndTheStatus()
    {
        SetRuns(3);
        FirstRunOfTwoBlocks();
        _rig.Status.Complete("foo", "auto", _rig.World.Now, counted: false, null);
        string before = File.ReadAllText(Raw);

        _tracer.Start(_game); // 읽기 0 건
        _tracer.Step();
        _rig.World.StartTraceThrows = new IOException("etw");
        _tracer.Start(_game);

        Assert.False(_tracer.Active);
        Assert.Equal(before, File.ReadAllText(Raw));
        Assert.False(File.Exists(TraceWriter.RunPath(Raw, 2)));
        Assert.Equal(1, AutoTracer.RunCount(_game, File.Exists));
        Assert.Equal(AutoStage.Done, _rig.Status.Get("foo")!.Stage);
    }
}
