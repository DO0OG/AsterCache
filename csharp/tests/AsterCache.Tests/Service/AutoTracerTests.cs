using AsterCache.Core.Apply;
using AsterCache.Core.Discovery;
using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
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
        _rig.Settings = new AsterSettings(50, [_game]) { Auto = AutoSettings.Default with { TraceRuns = 1, TraceCapSeconds = 30, TraceCapMib = 64, TraceLateWindows = 0, IoSummaryEnabled = false, Learn = LearnSettings.Default with { Enabled = false } } };
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

    // 끝난 뒤 변환의 입력: 디스크 읽기 한 건짜리 요청 표 (가짜 세션이 Stop 뒤에 내줌)
    private IoRequestSnapshot IoRequests(params IoSummaryWindow[] windows)
    {
        var collector = new IoSummaryCollector(_rig.World.Now);
        collector.AddDiskRead(1, 0, 4096, 2500, 1_000_000, paging: false);
        return collector.Snapshot(_rig.World.Now.AddMinutes(1), windows, eventsLost: 1, null);
    }

    private void EndAfterExitGrace(AutoTracer? tracer = null)
    {
        _rig.World.Running = false;
        AutoTracer target = tracer ?? _tracer;
        target.Step();
        _rig.World.Now += GameExitTracker.Grace;
        target.Step();
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
        _rig.World.Files.Add(TraceStageIndex.IndexPath(Raw));
        _tracer.Start(_game);
        Assert.Equal(1, _rig.World.TraceStarts); // 원시 트레이스가 이미 있음
    }

    [Fact]
    public void TheStartThatOpenedTheSessionIsHandedToItAsAnExeStartTime()
    {
        _tracer.Start(_game);

        Assert.Equal(1, _rig.World.Session!.TriggerStarts);
    }

    [Theory]
    [InlineData(GameMode.Inherit, false, true)]
    [InlineData(GameMode.PrewarmOnly, false, true)]
    [InlineData(GameMode.RelocateOnly, false, true)]
    [InlineData(GameMode.Off, false, false)]
    [InlineData(GameMode.Inherit, true, false)]
    public void OffAndPausedGamesNeverStartATraceSessionAndTheReasonIsLoggedOnce(GameMode mode, bool paused, bool starts)
    {
        _rig.Settings = _rig.Settings with { Games = [_game with { Mode = mode, Paused = paused }] };

        _tracer.Start(_game);
        _tracer.Shutdown();
        _tracer.Start(_game);

        Assert.Equal(starts ? 2 : 0, _rig.World.TraceStarts);
        Assert.Equal(starts ? 0 : 1, _rig.Log.Lines.Count(l => l.Contains("foo: trace skipped (game policy: ", StringComparison.Ordinal)));
        Assert.False(File.Exists(Raw)); // 아무 파일도 만들지 않음
    }

    [Fact]
    public void PausingAGameMidTraceStopsTheSessionAtTheNextStepAndKeepsWhatWasRead()
    {
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 5L << 20)];
        _rig.World.Running = true;
        _tracer.Step();
        Assert.True(_tracer.Active);

        _rig.Settings = _rig.Settings with { Games = [_game with { Paused = true }] };
        _tracer.Step();

        Assert.False(_tracer.Active);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("foo: trace stopped (game policy: paused)", StringComparison.Ordinal));
        _rig.World.Running = false;
        _tracer.Step();
        _rig.World.Now += GameExitTracker.Grace;
        _tracer.Step();
        Assert.True(File.Exists(Raw)); // 읽은 만큼은 게임이 끝난 뒤 저장
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
        _rig.World.Now += GameExitTracker.Grace;
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
        Assert.True(_tracer.Active);
        _rig.World.Now += GameExitTracker.Grace;
        _tracer.Step();

        Assert.False(_tracer.Active);
        Assert.True(_trace.Stopped);
        Assert.True(_trace.Disposed);
        var events = (List<object?>)JsonRead.ReadFile(Raw)!;
        Assert.Equal(2, events.Count);
        Assert.Equal(@"D:\Games\Foo\a.bin", ((JObject)events[0]!)["path"]);
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(Raw)!, "foo.trace.run1.json")));
        Assert.Equal([new StageMark("foo.exe", 0)], TraceStageIndex.ReadMarks(TraceWriter.RunPath(Raw, 1)));
        Assert.Equal("foo.exe", Assert.Single(TraceStageIndex.Read(TraceStageIndex.IndexPath(Raw))!.Stages).Exe);
        Assert.Equal(AutoStage.WaitingIdle, _rig.Status.Get("foo")!.Stage);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("auto trace ended (game exited)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestartSplitEndsThePreviousTraceEvenThoughTheSameProgramIsStillRunningAndIgnoresALateEarlierExit()
    {
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 5L << 20)];
        _rig.World.Running = true; // 같은 이름의 새 프로세스가 이미 떠 있어 종료 유예로는 끝나지 않음
        _tracer.Step();

        await _tracer.EndGameSessionAsync(_game, _rig.World.Now.AddSeconds(-5)); // 이전 실행의 늦은 종료: 지금 실행을 닫지 않음
        _tracer.Step();
        Assert.True(_tracer.Active);

        Task ending = _tracer.EndGameSessionAsync(_game, _rig.World.Now.AddSeconds(1));
        Assert.False(ending.IsCompleted);
        _tracer.Step();

        await ending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(_tracer.Active);
        Assert.True(_trace.Disposed);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("auto trace ended (game exited)", StringComparison.Ordinal));
    }

    [Fact]
    public void ARunEndRecordsTheOrbitShadowPlanAndLeavesTheOtherStatusFieldsAlone()
    {
        Reads((0, 65536), (65536, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 5L << 20)];
        _rig.World.Running = true;
        _tracer.Step();
        EndAfterExitGrace();
        AutoGameEntry before = _rig.Status.Get("foo")!;

        AutoGameEntry? after = null;
        for (int i = 0; i < 200 && after?.OrbitShadow is null; i++) // 그림자 계획은 BelowNormal 스레드에서 돌아 잠시 기다림
        {
            Thread.Sleep(10);
            _tracer.Step(); // 트레이스 루프가 끝난 결과를 상태로 옮김
            after = _rig.Status.Get("foo");
        }

        Assert.NotNull(after?.OrbitShadow);
        Assert.Equal("baseline", after!.OrbitShadow!.Selected);
        Assert.True(after.OrbitShadow.Order > 0);
        Assert.Equal(before with { OrbitShadow = null }, after with { OrbitShadow = null });
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

    // 한 세션에 시각이 다른 읽기를 미리 넣어 둠: 가짜 세션은 창 밖 읽기를 거르지 않으므로 창 여닫기는 Collecting 으로 확인
    private void LateWindowSettings(int windows, int intervalMin, int windowSeconds, int capMib = 64)
    {
        _rig.Settings = _rig.Settings with
        {
            Auto = _rig.Settings.Auto with { TraceCapMib = capMib, TraceLateWindows = windows, TraceLateIntervalMin = intervalMin, TraceLateWindowSeconds = windowSeconds },
        };
        _rig.World.Running = true;
    }

    [Fact]
    public void LateTraceWindowsKeepTheFirstSessionOpenAndSaveSessionRelativeEventTimes()
    {
        LateWindowSettings(2, 1, 90);
        DateTime start = _rig.World.Now.UtcDateTime;
        _trace.Records.Add(TraceRecord.Start(start, 100, 1, "foo.exe"));
        foreach ((int seconds, long offset) in new[] { (1, 0L), (91, 65536L), (241, 131072L) })
        {
            _trace.Records.Add(TraceRecord.Read(start.AddSeconds(seconds), 100, @"D:\Games\Foo\a.bin", offset, 65536));
        }

        DateTimeOffset startedAt = _rig.World.Now;
        _tracer.Start(_game);
        _rig.World.Now = startedAt.AddSeconds(30);
        _tracer.Step(); // 첫 창 종료: 세션은 그대로, 읽기 수집만 닫음
        Assert.False(_trace.Collecting);

        _rig.World.Now = startedAt.AddSeconds(90);
        _tracer.Step(); // 첫 번째 추가 창 시작
        Assert.True(_trace.Collecting);
        Assert.Equal("late window 1/2", _rig.Status.Get("foo")!.Detail);

        _rig.World.Now = startedAt.AddSeconds(180);
        _tracer.Step();
        Assert.False(_trace.Collecting);
        _rig.World.Now = startedAt.AddSeconds(240);
        _tracer.Step(); // 두 번째 추가 창 시작
        Assert.True(_trace.Collecting);
        Assert.Equal("late window 2/2", _rig.Status.Get("foo")!.Detail);

        _rig.World.Now = startedAt.AddSeconds(330);
        _tracer.Step(); // 모든 추가 창 종료, 게임 종료 후 저장 대기
        Assert.False(File.Exists(Raw));
        Assert.True(_trace.Stopped);
        Assert.Equal(1, _rig.World.TraceStarts);

        _rig.World.Running = false;
        _tracer.Step();
        _rig.World.Now += GameExitTracker.Grace;
        _tracer.Step();

        Assert.True(File.Exists(Raw), string.Join(" | ", _rig.Log.Lines));
        List<TraceEvent> saved = ((List<object?>)JsonRead.ReadFile(Raw)!).Select(TraceEvents.FromJson).ToList();
        Assert.Equal([1_000L, 91_000L, 241_000L], saved.Select(e => e.TMs!.Value));
        Assert.True(_trace.Disposed);
        Assert.Contains(_rig.Log.Lines, line => line.Contains("late window 1/2 started", StringComparison.Ordinal));
        Assert.Contains(_rig.Log.Lines, line => line.Contains("late window 2/2 started", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFirstLateWindowOpensRightAfterTheFirstWindowAndTheIntervalOnlySeparatesLaterWindows()
    {
        LateWindowSettings(2, 10, 30);
        DateTimeOffset startedAt = _rig.World.Now;
        _tracer.Start(_game);
        _rig.World.Now = startedAt.AddSeconds(30);
        _tracer.Step(); // 첫 창 종료
        Assert.False(_trace.Collecting);

        _tracer.Step(); // 첫 추가 창이 간격 없이 바로 열림
        Assert.True(_trace.Collecting);
        Assert.Equal("late window 1/2", _rig.Status.Get("foo")!.Detail);

        _rig.World.Now = startedAt.AddSeconds(60);
        _tracer.Step(); // 추가 창 하나가 끝남 (창 길이 30 초는 그대로)
        Assert.False(_trace.Collecting);

        _rig.World.Now = startedAt.AddSeconds(60 + (9 * 60));
        _tracer.Step(); // 두 번째 창은 앞 창이 끝난 뒤 간격 (10 분) 이 차야 열림
        Assert.False(_trace.Collecting);

        _rig.World.Now = startedAt.AddSeconds(60 + (10 * 60));
        _tracer.Step();
        Assert.True(_trace.Collecting);
        Assert.Equal("late window 2/2", _rig.Status.Get("foo")!.Detail);

        _rig.World.Now = startedAt.AddSeconds(90 + (10 * 60));
        _tracer.Step(); // 창 수 상한 (2) 에서 끝남
        Assert.False(_trace.Collecting);
        Assert.True(_trace.Stopped);
        Assert.Equal(2, _rig.Log.Lines.Count(l => l.Contains("late window", StringComparison.Ordinal) && l.Contains("started", StringComparison.Ordinal)));
    }

    [Fact]
    public void EachWindowEndLogsTheReadsAndTheTargetProcesses()
    {
        LateWindowSettings(1, 1, 30);
        _trace.Window = new WindowStats(12, 2, 3);
        _tracer.Start(_game);
        _rig.World.Now += TimeSpan.FromSeconds(30);
        _tracer.Step();

        Assert.Contains(_rig.Log.Lines, l => l.Contains("trace window ended, 12 reads, 2 target processes, 3 events lost so far", StringComparison.Ordinal));
    }

    [Fact]
    public void ReadCapIgnoresReadsWhileNoWindowIsOpen()
    {
        LateWindowSettings(2, 1, 30, capMib: 1);
        DateTimeOffset startedAt = _rig.World.Now;
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 0)];
        _tracer.Step(); // 첫 표본은 기준
        _rig.World.Now = startedAt.AddSeconds(30);
        _rig.World.Procs = [(100, 512 * 1024)];
        _tracer.Step(); // 첫 창에서 0.5 MiB, 창이 닫힘
        _tracer.Step(); // 첫 추가 창이 바로 열림

        _rig.World.Now = startedAt.AddSeconds(60);
        _tracer.Step(); // 첫 추가 창이 읽기 없이 닫힘

        _rig.World.Now = startedAt.AddSeconds(90);
        _rig.World.Procs = [(100, 40L << 20)];
        _tracer.Step(); // 창 밖에서 40 MiB: 세지 않음
        Assert.True(_tracer.Active);

        _rig.World.Now = startedAt.AddSeconds(120);
        _tracer.Step(); // 두 번째 추가 창 시작: 읽기량 기준을 이 값으로 다시 잡음
        Assert.True(_tracer.Active);
        _rig.World.Now = startedAt.AddSeconds(130);
        _rig.World.Procs = [(100, (40L << 20) + (512 * 1024))];
        _tracer.Step(); // 창 안 0.5 MiB 가 더해져 상한 (1 MiB) 도달
        Assert.False(_tracer.Active);
        Assert.Contains(_rig.Log.Lines, line => line.Contains("(read cap)", StringComparison.Ordinal));
    }

    [Fact]
    public void LateWindowsOfALaterRunLeaveTheStatusEntryUntouched()
    {
        SetRuns(3);
        FirstRunOfTwoBlocks();
        _rig.Status.Complete("foo", "auto", _rig.World.Now, counted: false, null);
        AutoGameEntry before = _rig.Status.Get("foo")!;
        LateWindowSettings(1, 1, 30);
        Reads((131072, 65536));
        DateTimeOffset startedAt = _rig.World.Now;
        _tracer.Start(_game);
        _rig.World.Now = startedAt.AddSeconds(30);
        _tracer.Step();
        _rig.World.Now = startedAt.AddSeconds(90);
        _tracer.Step(); // 후반 창 시작: 2회차 이후 기록은 상태를 건드리지 않음 (At 이 바뀌면 재학습 판정이 흔들림)
        Assert.True(_trace.Collecting);
        Assert.Equal(before with { OrbitShadow = null }, _rig.Status.Get("foo")! with { OrbitShadow = null }); // 이전 회차의 그림자 계획 기록은 따로 붙음

        EndAfterExitGrace();

        Assert.Equal(before with { OrbitShadow = null }, _rig.Status.Get("foo")! with { OrbitShadow = null });
        Assert.Contains(_rig.Log.Lines, l => l.Contains("(run 2, merged)", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailedWindowCloseEndsTheTraceOnceAndKeepsTheReadsSoFar()
    {
        LateWindowSettings(1, 1, 30);
        Reads((0, 65536));
        _trace.PauseFailure = new InvalidOperationException("session gone");
        _tracer.Start(_game);
        _rig.World.Now += TimeSpan.FromSeconds(30);

        _tracer.Step(); // 첫 창을 닫지 못함: 예외를 올리지 않고 지금까지의 기록을 저장하도록 끝냄
        _tracer.Step();

        Assert.False(_tracer.Active);
        Assert.True(_trace.Stopped);
        Assert.Single(_rig.Log.Lines, l => l.Contains("trace window could not end", StringComparison.Ordinal));
        EndAfterExitGrace();
        Assert.True(File.Exists(Raw));
    }

    [Fact]
    public void ATargetStartOpensTheNextLateWindowAtOnceAndLaterTimedWindowsWaitForTheInterval()
    {
        LateWindowSettings(2, 10, 30);
        DateTimeOffset startedAt = _rig.World.Now;
        _tracer.Start(_game);
        _tracer.NoteTargetStart("foo", "foo.exe"); // 첫 창이 아직 열려 있음: 무시
        _rig.World.Now = startedAt.AddSeconds(10);
        _tracer.Step();
        Assert.True(_trace.Collecting);
        _rig.World.Now = startedAt.AddSeconds(30);
        _tracer.Step(); // 첫 창 종료
        _tracer.Step(); // 첫 추가 창 (간격 없이)
        _rig.World.Now = startedAt.AddSeconds(60);
        _tracer.Step();
        Assert.False(_trace.Collecting);

        _rig.World.Now = startedAt.AddSeconds(100); // 예약 시각 (앞 창 끝 + 10 분) 보다 한참 이름
        _tracer.NoteTargetStart("foo", "foo.exe");
        _tracer.Step();

        Assert.True(_trace.Collecting);
        Assert.Equal("late window 2/2", _rig.Status.Get("foo")!.Detail);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("late window 2/2 started at a target start (foo.exe)", StringComparison.Ordinal));
        Assert.DoesNotContain(_rig.Log.Lines, l => l.Contains("late window 1/2 started at a target start", StringComparison.Ordinal));
    }

    [Fact]
    public void ATargetStartDoesNothingWhileAWindowIsOpenOrNoWindowIsLeftOrTheGameIsAnother()
    {
        LateWindowSettings(1, 10, 30);
        DateTimeOffset startedAt = _rig.World.Now;
        _tracer.Start(_game);
        _rig.World.Now = startedAt.AddSeconds(30);
        _tracer.Step();
        _tracer.Step(); // 유일한 추가 창이 열림
        Assert.True(_trace.Collecting);

        _tracer.NoteTargetStart("foo", "foo.exe"); // 이미 열려 있음
        _rig.World.Now = startedAt.AddSeconds(40);
        _tracer.Step();
        Assert.Single(_rig.Log.Lines, l => l.Contains("late window", StringComparison.Ordinal) && l.Contains("started", StringComparison.Ordinal));

        _rig.World.Now = startedAt.AddSeconds(60);
        _tracer.Step(); // 창 수 상한 (1) 에서 끝남
        Assert.False(_trace.Collecting);
        _tracer.NoteTargetStart("foo", "foo.exe"); // 남은 창 없음
        _tracer.Step();
        Assert.False(_trace.Collecting);
        Assert.Single(_rig.Log.Lines, l => l.Contains("late window", StringComparison.Ordinal) && l.Contains("started", StringComparison.Ordinal));
    }

    [Fact]
    public void ATargetStartOfAnotherGameIsIgnored()
    {
        LateWindowSettings(2, 10, 30);
        DateTimeOffset startedAt = _rig.World.Now;
        _tracer.Start(_game);
        _rig.World.Now = startedAt.AddSeconds(30);
        _tracer.Step();
        _tracer.Step(); // 첫 추가 창
        _rig.World.Now = startedAt.AddSeconds(60);
        _tracer.Step();

        _rig.World.Now = startedAt.AddSeconds(100);
        _tracer.NoteTargetStart("bar", "bar.exe");
        _tracer.Step();

        Assert.False(_trace.Collecting);
    }

    [Fact]
    public void ServiceStartRebuildsTheLoadRefillListOnlyWhenItIsMissingOrOlderThanTheNewestSummary()
    {
        string summary = IoSummaryStore.RunPath(TraceWriter.RunPath(Raw, 1));
        string list = LoadRefillPlans.PathOf(_game);
        DateTimeOffset now = _rig.World.Now;
        bool Built() => _rig.Log.Lines.Any(l => l.Contains("load refill list built from", StringComparison.Ordinal) && l.Contains("(service start)", StringComparison.Ordinal));

        _tracer.RebuildStaleLoadRefills(default); // 집계 없음
        Assert.False(Built());

        _rig.World.FileTimes[summary] = now;
        _rig.World.Running = true;
        _tracer.RebuildStaleLoadRefills(default); // 게임 실행 중
        Assert.False(Built());

        _rig.World.Running = false;
        _rig.World.FileTimes[list] = now.AddMinutes(1);
        _tracer.RebuildStaleLoadRefills(default); // 목록이 더 새로움
        Assert.False(Built());

        _rig.World.FileTimes[list] = now.AddMinutes(-1);
        _tracer.RebuildStaleLoadRefills(default); // 목록이 오래됨
        Assert.True(Built());
    }

    [Fact]
    public void ServiceStartRebuildsTheUnbufferedFileListOnlyWhenItIsMissingOrOlderThanTheNewestSummaryAndNoGameRuns()
    {
        string summary = IoSummaryStore.RunPath(TraceWriter.RunPath(Raw, 1));
        string list = UnbufferedFiles.PathOf(_game);
        DateTimeOffset now = _rig.World.Now;
        bool Built() => _rig.Log.Lines.Any(l => l.Contains("unbuffered file list built from", StringComparison.Ordinal) && l.Contains("(service start)", StringComparison.Ordinal));

        _tracer.RebuildStaleLoadRefills(default); // 집계 없음
        Assert.False(Built());

        _rig.World.FileTimes[summary] = now;
        _rig.World.Running = true;
        _tracer.RebuildStaleLoadRefills(default); // 게임 실행 중
        Assert.False(Built());

        _rig.World.Running = false;
        _rig.World.FileTimes[list] = now.AddMinutes(1);
        _rig.World.FileTimes[UnbufferedFiles.NoCacheReadPathOf(_game)] = now.AddMinutes(1); // 붙들기용 캐시 없는 읽기 목록도 같이 만들므로 둘 다 더 새로워야 함
        _tracer.RebuildStaleLoadRefills(default); // 목록이 더 새로움
        Assert.False(Built());

        _rig.World.FileTimes[list] = now.AddMinutes(-1);
        _tracer.RebuildStaleLoadRefills(default); // 목록이 오래됨
        Assert.True(Built());
    }

    [Fact]
    public void ASessionThatEndedEarlyOpensNoMoreLateWindows()
    {
        LateWindowSettings(2, 1, 30);
        Reads((0, 65536));
        _trace.ResumeFailure = new TraceIncompleteException("pump ended");
        DateTimeOffset startedAt = _rig.World.Now;
        _tracer.Start(_game);
        _rig.World.Now = startedAt.AddSeconds(30);
        _tracer.Step();
        _rig.World.Now = startedAt.AddSeconds(90);
        _tracer.Step(); // 후반 창을 열지 못함

        Assert.False(_tracer.Active);
        Assert.Single(_rig.Log.Lines, l => l.Contains("trace session ended early", StringComparison.Ordinal) && l.StartsWith("Warn", StringComparison.Ordinal));
        _rig.World.Now = startedAt.AddSeconds(200);
        _tracer.Step();
        Assert.Equal(1, _rig.World.TraceStarts);
        EndAfterExitGrace();
        Assert.True(File.Exists(Raw));
    }

    [Fact]
    public void FailedStopAtTheEndFlagsTheUnconfirmedCleanup()
    {
        _trace.StopFailure = new IOException("stop failed");
        _trace.DisposeFailure = new IOException("dispose failed");
        _rig.World.Running = true;

        _tracer.Start(_game);
        _rig.World.Now += TimeSpan.FromSeconds(30);
        Assert.ThrowsAny<IOException>(() => _tracer.Step()); // 시간 상한으로 세션을 멈추는 데 실패하고 마지막 정리도 실패해 예외가 올라옴

        Assert.Equal(AutoStage.Failed, _rig.Status.Get("foo")!.Stage);
        Assert.Contains(_rig.Log.Lines, line => line.Contains("trace cleanup could not be confirmed", StringComparison.Ordinal));

        _tracer.Start(_game); // 정리를 확인하지 못했으니 새 세션을 열지 않음

        Assert.Equal(1, _rig.World.TraceStarts);
    }

    [Fact]
    public void LateTraceWindowsShareTheOriginalReadCapAndWaitToSaveUntilExit()
    {
        LateWindowSettings(1, 1, 30, capMib: 1);
        Reads((0, 65536));
        DateTimeOffset startedAt = _rig.World.Now;
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 0)];
        _tracer.Step(); // 첫 표본은 기준
        _rig.World.Now = startedAt.AddSeconds(30);
        _rig.World.Procs = [(100, 512 * 1024)];
        _tracer.Step(); // 첫 창에서 0.5 MiB
        _rig.World.Now = startedAt.AddSeconds(90);
        _tracer.Step(); // 누적 캡을 초기화하지 않고 추가 창 시작
        Assert.Equal(1, _rig.World.TraceStarts);

        _rig.World.Now = startedAt.AddSeconds(100);
        _rig.World.Procs = [(100, 1L << 20)];
        _tracer.Step(); // 다음 창에서 0.5 MiB 추가되어 세션 누적 상한 도달
        Assert.False(_tracer.Active);
        Assert.True(_trace.Stopped);
        Assert.False(File.Exists(Raw));
        Assert.Contains(_rig.Log.Lines, line => line.Contains("(read cap)", StringComparison.Ordinal));

        _rig.World.Running = false;
        _tracer.Step();
        _rig.World.Now += GameExitTracker.Grace;
        _tracer.Step();
        Assert.True(File.Exists(Raw));
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
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { TraceCapSeconds = 120 } };
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = []; // 보호된 프로세스: 읽기량을 못 열어 목록에는 없지만 이름으로는 실행 중
        _rig.World.Running = true;

        _tracer.Step();

        Assert.True(_tracer.Active);
        _rig.World.Now += GameExitTracker.Grace + TimeSpan.FromSeconds(1);
        _tracer.Step(); // 등록된 ExeNames 가 살아 있으면 유예 상한이 지나도 종료가 아님
        Assert.True(_tracer.Active);
        _rig.World.Running = false;
        _tracer.Step();
        Assert.True(_tracer.Active);
        _rig.World.Now += GameExitTracker.Grace;
        _tracer.Step();
        Assert.False(_tracer.Active);
    }

    [Fact]
    public void IoSummaryKeepsObservationAliveAfterTheRawTraceWindowEnds()
    {
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = true } };
        GameSettings game = _game with { ExeNames = ["foo.exe", "late.exe"] };
        _rig.Settings = _rig.Settings with { Games = [game] };
        Reads((0, 65536));
        _trace.IoRequests = IoRequests(new IoSummaryWindow(0, 2000000));

        _tracer.Start(game);
        _rig.World.Procs = [(100, 5L << 20)];
        _rig.World.Running = true;
        _tracer.Step();
        _rig.World.Now += TimeSpan.FromSeconds(30);
        _tracer.Step();

        Assert.True(_tracer.Active);
        Assert.False(_trace.Stopped);
        Assert.False(_trace.Collecting);
        Assert.True(_trace.RawRecordsFrozen);
        Assert.False(File.Exists(Raw));
        Assert.Equal(0, _rig.World.BuildIoSummaryCalls); // 게임이 도는 동안에는 파일 배치를 읽지 않음

        _trace.Records.Add(TraceRecord.Start(DateTime.UtcNow, 1001, 1, "late.exe"));
        _trace.Records.Add(TraceRecord.Read(DateTime.UtcNow, 1001, @"D:\Games\Foo\late.bin", 0, 4096));

        EndAfterExitGrace();

        Assert.False(_tracer.Active);
        Assert.True(_trace.Stopped);
        List<object?> raw = (List<object?>)JsonRead.ReadFile(TraceWriter.RunPath(Raw, 1))!;
        Assert.DoesNotContain(raw, value => value is JObject row && (string?)row["path"] == @"D:\Games\Foo\late.bin");
        Assert.DoesNotContain(TraceStageIndex.ReadMarks(TraceWriter.RunPath(Raw, 1)) ?? [], mark => mark.Exe == "late.exe");
        IoSummary saved = Assert.IsType<IoSummary>(IoSummaryStore.ReadForRun(TraceWriter.RunPath(Raw, 1)));
        Assert.Equal(1, saved.Quality.Total.Reads);
        Assert.Equal(1, _rig.World.BuildIoSummaryCalls);
        Assert.Contains(_rig.Log.Lines, line => line.Contains("I/O summary: observed", StringComparison.Ordinal));
        string[] lines = [.. _rig.Log.Lines];
        int summaryLine = Array.FindIndex(lines, line => line.Contains("I/O summary: observed", StringComparison.Ordinal));
        Assert.Contains(": read modes:", lines[summaryLine + 1]);
    }

    [Fact]
    public void IoSummaryIsSavedWhenTheRawRunIsRejectedAsTooSmall()
    {
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = true } };
        Extra(runs: 1, max: 1);
        SeedRuns(1);
        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings) { MinRunBytes = 1 << 20 };
        _trace.IoRequests = IoRequests();
        Reads((0, 65536));

        tracer.Start(_game);
        EndAfterExitGrace(tracer);

        string run2 = TraceWriter.RunPath(Raw, 2);
        Assert.False(File.Exists(run2));
        Assert.False(File.Exists(IoSummaryStore.RunPath(run2)));
        IoSummary saved = Assert.IsType<IoSummary>(IoSummaryStore.ReadStandaloneForRaw(IoSummaryStore.StandalonePath(Raw, 1), Raw));
        Assert.Equal(1, saved.Quality.Total.Reads);
        Assert.Contains(_rig.Log.Lines, line => line.Contains("too small", StringComparison.Ordinal));
    }

    [Fact]
    public void IoSummaryDoesNotAttachToARawTraceChangedDuringTheSession()
    {
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = true } };
        _trace.IoRequests = IoRequests();

        _tracer.Start(_game);
        _rig.World.FileTimes[Raw] = _rig.World.Now.AddSeconds(1);
        EndAfterExitGrace();

        Assert.False(File.Exists(IoSummaryStore.RunPath(TraceWriter.RunPath(Raw, 1))));
        Assert.Contains(_rig.Log.Lines, line => line.Contains("I/O summary not saved, the trace changed", StringComparison.Ordinal));
    }

    [Fact]
    public void DisablingIoSummaryKeepsThePreviousTimeCapAndDoesNotRequestDiskKeywords()
    {
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = false } };
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Running = true;
        _rig.World.Procs = [(100, 1L << 20)];
        _tracer.Step();
        Assert.Null(Assert.Single(_rig.World.IoSummaryRoots));

        _rig.World.Now += TimeSpan.FromSeconds(30);
        _tracer.Step();

        Assert.False(_tracer.Active);
        Assert.True(_trace.Stopped);
        Assert.DoesNotContain(_rig.World.Files, path => path.EndsWith(".io.json", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("registered", "vanguard")]
    [InlineData("folder", "eac")]
    [InlineData("riot", "vanguard")]
    [InlineData("registered", "kernel")]
    [InlineData("folder", "kernel")]
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
    public void AnUnregisteredProcessOnlyGetsTheRegisteredNameExitGrace()
    {
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 1L << 20)];
        _rig.World.Running = true;
        _tracer.Step();

        _rig.World.Running = false; // cod.exe 가 빠짐
        _rig.World.Procs = [];
        _rig.World.Reads[200] = 40L << 20;
        _tracer.Step();
        Assert.True(_tracer.Active);
        _rig.World.Reads[200] = 105L << 20; // 미등록 helper 의 읽기는 게임 읽기량으로 보지 않음
        _tracer.Step();
        Assert.True(_tracer.Active);
        _rig.World.Now += GameExitTracker.Grace;
        _tracer.Step();
        Assert.False(_tracer.Active);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("auto trace ended (game exited)", StringComparison.Ordinal));
    }

    [Fact]
    public void RegisteredExeKeepsTheTraceAliveAndThenUsesTheRegisteredNameGrace()
    {
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = [];
        _rig.World.Running = false;
        _rig.World.Reads[200] = 1L << 20;
        _tracer.Step();
        Assert.True(_tracer.Active);

        _tracer.Step();
        Assert.True(_tracer.Active); // 등록 이름이 사라진 직후에는 런처 전환 유예
        _rig.World.Now += GameExitTracker.Grace;
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
        _rig.World.Now += GameExitTracker.Grace;
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
        _rig.World.Now += GameExitTracker.Grace;
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
    public void SecondRunIsSavedAsRun2AndMergedIntoTheRawTraceNewestRunFirstWithoutTouchingTheStage()
    {
        SetRuns(3);
        FirstRunOfTwoBlocks();
        _rig.Status.Complete("foo", "auto", _rig.World.Now, counted: false, null); // 재배치까지 끝난 게임

        Reads((131072, 65536), (0, 65536)); // 새 블록 2 와 이미 있는 블록 0
        _tracer.Start(_game);
        Assert.True(_tracer.Active);
        Assert.Null(_rig.World.Listener); // 두 번째부터는 선행 읽기를 켜지 않음
        Assert.Equal(AutoStage.Done, _rig.Status.Get("foo")!.Stage);
        EndAfterExitGrace();

        Assert.Equal(AutoStage.Done, _rig.Status.Get("foo")!.Stage);
        Assert.Equal(2, AutoTracer.RunCount(_game, File.Exists));
        var run2 = (List<object?>)JsonRead.ReadFile(TraceWriter.RunPath(Raw, 2))!;
        Assert.Equal([131072L, 0L], run2.Select(r => ((JObject)r!)["offset"]));
        var merged = (List<object?>)JsonRead.ReadFile(Raw)!;
        Assert.Equal([131072L, 0L, 65536L], merged.Select(r => ((JObject)r!)["offset"])); // 최신 run2 의 순서가 앞, run1 에서만 본 블록은 뒤
        Assert.Equal([new StageMark("foo.exe", 0)], TraceStageIndex.ReadMarks(TraceWriter.RunPath(Raw, 2)));
        Assert.Equal([65536L, 131072L], TraceStageIndex.Read(TraceStageIndex.IndexPath(Raw))!.Stages[0].Ranges.Select(r => r.Length)); // 블록 2 와 이어진 블록 0·1 한 범위
        Assert.False(File.Exists(Raw + ".tmp"));
        Assert.Contains(_rig.Log.Lines, l => l.Contains("(run 2, merged)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheMergeWaitsForTheZoneLockAndDropsThePlayWhenACleanupRanMeanwhile()
    {
        SetRuns(3);
        SeedRuns(2);
        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings) { MinRunBytes = 0 };
        Reads((1000L * MiB, 35L << 20));
        tracer.Start(_game);
        string run3 = TraceWriter.RunPath(Raw, 3);

        Task end;
        using (DataLock? gate = DataLock.TryAcquire(_game.Plan)) // 업데이트 정리 (DropChanged) 가 잠금 안에서 파일을 고치는 중
        {
            Assert.NotNull(gate);
            end = Task.Run(() => EndAfterExitGrace(tracer)); // 두 번째 이후 플레이의 저장·병합은 다른 스레드에서 같은 잠금을 기다림
            Assert.False(SpinWait.SpinUntil(() => end.IsCompleted, 400));
            Assert.False(File.Exists(run3)); // 정리가 끝나기 전에는 run·병합본을 쓰지 않음
            AutoTracer.DropChanged(_game, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\Games\Foo\a.bin" });
            _rig.World.FileTimes[Raw] = _rig.World.Now; // 실제 원시 트레이스를 고쳤으니 가짜 파일 시각도 맞춤
        }

        await end.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.False(File.Exists(run3)); // 정리가 있었으므로 이 플레이는 저장하지 않음 (정리 전 오프셋 재유입 방지)
        Assert.Empty((List<object?>)JsonRead.ReadFile(Raw)!); // 병합본은 정리 결과 그대로: 뺀 행이 다시 들어오지 않음
        Assert.Contains(_rig.Log.Lines, l => l.Contains("while it waited for the zone lock", StringComparison.Ordinal));
    }

    [Fact]
    public void APlayKeptWhileTheZoneLockIsBusyIsSavedOnALaterStep()
    {
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 0L)]; // 첫 표본은 기준
        _tracer.Step();
        _rig.World.Procs = [(100, 70L << 20)]; // 읽기 상한 (64 MiB) 을 넘김: 세션을 멈추고 게임이 끝날 때까지 보관
        _tracer.Step();

        using (DataLock? gate = DataLock.TryAcquire(_game.Plan)) // 정리·병합 중이라 잠금이 바쁨
        {
            Assert.NotNull(gate);
            _rig.World.Running = false;
            _rig.World.Now += GameExitTracker.Grace;
            _tracer.Step();
            Assert.False(File.Exists(Raw)); // 버리지 않고 보관
            Assert.Contains(_rig.Log.Lines, l => l.Contains("waits for the zone lock", StringComparison.Ordinal));
        }

        _tracer.Step(); // 잠금이 풀린 뒤 다음 Step 에서 저장
        Assert.True(File.Exists(Raw));
        Assert.Contains(_rig.Log.Lines, l => l.Contains("read events saved", StringComparison.Ordinal));
    }

    [Fact]
    public void ARunThatReadsLessThanTheMinimumIsNotSavedAndDoesNotUseUpARun()
    {
        SetRuns(3);
        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings); // 기본 최소량 64 MiB
        _trace.Records.Clear();
        Reads((0, 40L << 20), (40L << 20, 40L << 20)); // 한 이벤트는 64 MiB 까지
        tracer.Start(_game);
        EndAfterExitGrace(tracer);
        Assert.Equal(1, AutoTracer.RunCount(_game, File.Exists));
        _trace.Records.Clear();

        Reads((131072, 65536)); // 메뉴만 켠 정도
        tracer.Start(_game);
        EndAfterExitGrace(tracer);

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
        EndAfterExitGrace();

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
        EndAfterExitGrace();

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
        EndAfterExitGrace();
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
        EndAfterExitGrace();

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
        EndAfterExitGrace();
        _rig.World.StartTraceThrows = new IOException("etw");
        _tracer.Start(_game);

        Assert.False(_tracer.Active);
        Assert.Equal(before, File.ReadAllText(Raw));
        Assert.False(File.Exists(TraceWriter.RunPath(Raw, 2)));
        Assert.Equal(1, AutoTracer.RunCount(_game, File.Exists));
        Assert.Equal(AutoStage.Done, _rig.Status.Get("foo")!.Stage);
    }

    // ---- 유효 회차 (읽기량 64 MiB 이상), 다음 저장 번호, 추가 수집 (auto.trace_target_coverage / trace_runs_max) ----

    private const long MiB = 1L << 20;

    // 유효한 run 파일 count 개 (각 100 MiB 를 읽고 서로 다른 블록) 와 그 합집합 원시 트레이스를 씀.
    private void SeedRuns(int count, params (int Run, long Mib)[] sizes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Raw)!);
        var sessions = new List<IReadOnlyList<AsterCache.Core.Trace.TraceEvent>>();
        for (int i = 0; i < count; i++)
        {
            long mib = sizes.Where(s => s.Run == i + 1).Select(s => s.Mib).DefaultIfEmpty(100).First();
            sessions.Add([new AsterCache.Core.Trace.TraceEvent(@"D:\Games\Foo\a.bin", i * 200 * MiB, mib * MiB)]);
        }

        TraceWriter.Write(sessions, Raw);
        for (int i = 0; i < count; i++)
        {
            TraceStageIndex.WriteMarks(TraceWriter.RunPath(Raw, i + 1), [new StageMark($"foo{i + 1}.exe", 0)]);
        }

        TraceStageIndex.Write(TraceStageIndex.IndexPath(Raw), new TraceStageIndex([new TraceStage("foo.exe", 0, [])]));
        _rig.World.RunReadBytes = AutoTracer.RunReadBytes;
    }

    private void Extra(int runs = 3, int max = 8, int target = 70, int mergeMin = 64) =>
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { TraceRuns = runs, TraceRunsMax = max, TraceTargetCoverage = target, TraceMergeMinMib = mergeMin } };

    private IoSummary IoSummaryWithReadModes(string sessionId)
    {
        IoSummary summary = IoSummaries.With(_rig.World.Now, @"D:\Games\Foo\a.bin");
        return summary with
        {
            SessionId = sessionId,
            ReadModes = [new IoSummaryReadMode(@"D:\Games\Foo\a.bin", 1, 4096, 0, 0, 0, 0)],
            Quality = summary.Quality with { ReadModesObservedUntilUtc = summary.EndedAtUtc },
        };
    }

    private void Coverage(int percent)
    {
        _rig.Status.Set("foo", "auto", null, AutoStage.Done, null);
        _rig.Status.RecordCoverage("foo", percent);
    }

    private void Gain(int run, long newMib) => File.WriteAllText(AutoTracer.MergeMetaPath(_game), $"{{\"run\": {run}, \"new_mib\": {newMib}}}");

    private static HashSet<(string Path, long Offset, long Length)> TraceBlocks(string path) =>
        ((List<object?>)JsonRead.ReadFile(path)!).Select(row => TraceEvents.FromJson(row)).Select(e => (e.Path, e.Offset, e.Length)).ToHashSet();

    [Fact]
    public void RunCountSkipsRunFilesBelowTheMinimumReadIncludingRun1AndKeepsTheNextNumberAfterTheLastFile()
    {
        var files = new HashSet<string> { Raw, TraceWriter.RunPath(Raw, 1), TraceWriter.RunPath(Raw, 2), TraceWriter.RunPath(Raw, 3) };
        var reads = new Dictionary<string, long?>
        {
            [TraceWriter.RunPath(Raw, 1)] = 1 * MiB, // 첫 회차도 기준 미만이면 세지 않음
            [TraceWriter.RunPath(Raw, 2)] = 2270 * MiB,
            [TraceWriter.RunPath(Raw, 3)] = 6 * MiB, // 크래시 회차
        };

        Assert.Equal(1, AutoTracer.RunCount(_game, files.Contains, p => reads[p])); // run2 만 유효
        Assert.Equal(3, AutoTracer.LastRunNumber(_game, files.Contains)); // 다음 저장 번호는 4
        Assert.Equal(3, AutoTracer.RunCount(_game, files.Contains)); // 읽기량을 안 주면 파일 수
        Assert.Equal(3, AutoTracer.RunCount(_game, files.Contains, _ => null)); // 못 읽으면 셈
        Assert.Equal(0, AutoTracer.LastRunNumber(_game, new HashSet<string>().Contains));
        Assert.Equal(1, AutoTracer.LastRunNumber(_game, new HashSet<string> { Raw }.Contains));
        Assert.Equal(4, AutoTracer.LastRunNumber(_game, new HashSet<string> { Raw, TraceWriter.RunPath(Raw, 1), TraceWriter.RunPath(Raw, 4) }.Contains)); // 끊겨도 가장 큰 번호
    }

    [Fact]
    public void ALoneRawTraceWithoutRunFilesStillCountsAsOneRunWhateverItsSize()
    {
        var files = new HashSet<string> { Raw };

        Assert.Equal(1, AutoTracer.RunCount(_game, files.Contains, _ => 1 * MiB)); // 옛 형식: 읽기량을 따지지 않음 (호환)
        Assert.Equal(1, AutoTracer.RunCount(_game, files.Contains, _ => throw new InvalidOperationException("must not be read")));
    }

    [Fact]
    public void ASmallRun1IsNotValidSoTheNextRunIsStillAnAdditionalRecordSavedAsRun2AndMergedWithoutRun1()
    {
        SetRuns(3);
        SeedRuns(1, (1, 1)); // run1 이 1 MiB 뿐
        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings); // 최소 읽기량 64 MiB
        string run1 = TraceWriter.RunPath(Raw, 1);
        Assert.Equal(0, AutoTracer.RunCount(_game, File.Exists, AutoTracer.RunReadBytes));
        _rig.Status.Complete("foo", "auto", _rig.World.Now, counted: false, null);

        Reads((100L * MiB, 40L << 20), (140L * MiB, 40L << 20));
        tracer.Start(_game);
        Assert.True(tracer.Active);
        Assert.Equal(AutoStage.Done, _rig.Status.Get("foo")?.Stage); // 첫 기록 경로 (Tracing·Learning 상태, 선행 읽기) 와 섞이지 않음
        Assert.Contains(_rig.Log.Lines, l => l.Contains("auto trace started (run 1/3", StringComparison.Ordinal));
        EndAfterExitGrace(tracer);

        Assert.True(File.Exists(TraceWriter.RunPath(Raw, 2))); // 번호는 최대 번호 + 1: run1 을 덮어쓰지 않음
        Assert.Contains(_rig.Log.Lines, l => l.Contains("(run 2, merged)", StringComparison.Ordinal));
        Assert.Equal(1, AutoTracer.RunCount(_game, File.Exists, AutoTracer.RunReadBytes)); // run2 만 유효
        Assert.Equal(AutoStage.Done, _rig.Status.Get("foo")?.Stage);
        Assert.True(File.Exists(run1));
        Assert.Equal(80 * 16, ((List<object?>)JsonRead.ReadFile(Raw)!).Count); // 합집합에 1 MiB run1 (오프셋 0) 의 블록은 없음
    }

    [Fact]
    public void ASmallLegacyRun1StaysValidAfterTheMigrationAndIsMergedIntoRun3AfterARestart()
    {
        SetRuns(5);
        Directory.CreateDirectory(Path.GetDirectoryName(Raw)!);
        TraceWriter.WriteFile(Raw, [new AsterCache.Core.Trace.TraceEvent(@"D:\Games\Foo.bin", 0, 1 * MiB)]); // 옛 형식: run 파일 없이 1 MiB 원시 트레이스
        _rig.World.RunReadBytes = AutoTracer.RunReadBytes;
        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings);

        Reads((1000L * MiB, 35L << 20), (1035L * MiB, 35L << 20));
        tracer.Start(_game);
        EndAfterExitGrace(tracer);
        Assert.True(File.Exists(AutoTracer.LegacyRun1Path(_game)));
        Assert.Equal(2, AutoTracer.RunCount(_game, File.Exists, AutoTracer.RunReadBytes)); // 옛 run1 + run2

        var restarted = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings);
        Reads((2000L * MiB, 35L << 20), (2035L * MiB, 35L << 20));
        restarted.Start(_game);
        EndAfterExitGrace(restarted);

        Assert.True(File.Exists(TraceWriter.RunPath(Raw, 3)));
        Assert.Equal(3, AutoTracer.RunCount(_game, File.Exists, AutoTracer.RunReadBytes));
        var merged = (List<object?>)JsonRead.ReadFile(Raw)!;
        Assert.Contains(merged, r => (long)((JObject)r!)["offset"]! == 0L); // 옛 run1 의 블록이 병합 raw 에 남음
    }

    [Fact]
    public void ASmallFirstRecordOfANewGameStaysInvalidAfterRun2AndLeavesNoLegacyMark()
    {
        SetRuns(3);
        _rig.World.RunReadBytes = AutoTracer.RunReadBytes;
        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings); // 최소 읽기량 64 MiB
        Reads((0, 1 * MiB));
        tracer.Start(_game);
        EndAfterExitGrace(tracer);
        Assert.True(File.Exists(TraceWriter.RunPath(Raw, 1))); // 새 게임의 첫 기록은 run1 도 같이 씀

        Reads((1000L * MiB, 35L << 20), (1035L * MiB, 35L << 20));
        tracer.Start(_game);
        EndAfterExitGrace(tracer);

        Assert.True(File.Exists(TraceWriter.RunPath(Raw, 2)));
        Assert.False(File.Exists(AutoTracer.LegacyRun1Path(_game)));
        Assert.Equal(1, AutoTracer.RunCount(_game, File.Exists, AutoTracer.RunReadBytes)); // 1 MiB run1 은 무효, run2 만 유효
    }

    [Fact]
    public void AStaleLegacyMarkLeftAfterTheTraceWasSetAsideDoesNotMakeANewSmallRun1Valid()
    {
        SetRuns(3);
        _rig.World.RunReadBytes = AutoTracer.RunReadBytes;
        File.WriteAllText(AutoTracer.LegacyRun1Path(_game), "1"); // 업데이트 재계획이 트레이스만 .stale 로 치운 뒤 남은 표지
        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings);
        Reads((0, 1 * MiB));
        tracer.Start(_game);
        EndAfterExitGrace(tracer);

        Assert.False(File.Exists(AutoTracer.LegacyRun1Path(_game)));
        Assert.Equal(0, AutoTracer.RunCount(_game, File.Exists, AutoTracer.RunReadBytes));
    }

    [Fact]
    public void AFirstRecordIsNotSavedWhenTheStaleLegacyMarkCannotBeRemovedAndIsSavedAfterARestart()
    {
        SetRuns(3);
        _rig.World.RunReadBytes = AutoTracer.RunReadBytes;
        string mark = AutoTracer.LegacyRun1Path(_game);
        File.WriteAllText(mark, "1");
        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings);
        Reads((0, 1 * MiB));
        using (new FileStream(mark, FileMode.Open, FileAccess.Read, FileShare.None)) // 삭제를 막는 잠금
        {
            tracer.Start(_game);
            tracer.Step();
            _rig.World.Now += GameExitTracker.Grace;
            tracer.Step();
        }

        Assert.False(File.Exists(Raw));
        Assert.False(File.Exists(TraceWriter.RunPath(Raw, 1)));
        Assert.Equal(1, _rig.Log.Lines.Count(l => l.Contains("legacy run1 mark not removed", StringComparison.Ordinal)));
        Assert.Equal(AutoStage.Failed, _rig.Status.Get("foo")?.Stage);

        var restarted = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings);
        Reads((0, 1 * MiB));
        restarted.Start(_game);
        EndAfterExitGrace(restarted);

        Assert.False(File.Exists(mark));
        Assert.True(File.Exists(TraceWriter.RunPath(Raw, 1)));
        Assert.Equal(0, AutoTracer.RunCount(_game, File.Exists, AutoTracer.RunReadBytes));
    }

    [Fact]
    public void RunReadBytesSumsTheRowLengthsAndFollowsTheFileWhenItChanges()
    {
        SeedRuns(2, (1, 10), (2, 20));
        string run1 = TraceWriter.RunPath(Raw, 1);

        Assert.Equal<long?>(10 * MiB, AutoTracer.RunReadBytes(run1));
        Assert.Equal<long?>(10 * MiB, AutoTracer.RunReadBytes(run1)); // 캐시
        TraceWriter.WriteFile(run1, [new AsterCache.Core.Trace.TraceEvent(@"D:\Games\Foo\a.bin", 0, 30 * MiB), new AsterCache.Core.Trace.TraceEvent(@"D:\Games\Foo\a.bin", 0, 2 * MiB)]);
        Assert.Equal<long?>(32 * MiB, AutoTracer.RunReadBytes(run1));
        Assert.Null(AutoTracer.RunReadBytes(Path.Combine(_rig.Dir, "plans", "none.json")));
    }

    [Fact]
    public void ASixMiBRun3IsNotAValidRunSoTheNextRunIsSavedAsRun4WithoutOverwritingIt()
    {
        SetRuns(3);
        SeedRuns(3, (3, 6)); // beta.17 의 콜 오브 듀티: run1 100, run2 100, run3 6 MiB
        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings); // 최소 읽기량 64 MiB
        string run3 = TraceWriter.RunPath(Raw, 3);
        string run3Text = File.ReadAllText(run3);
        Assert.Equal(2, AutoTracer.RunCount(_game, File.Exists, AutoTracer.RunReadBytes));

        Reads((0, 40L << 20), (40L << 20, 40L << 20));
        tracer.Start(_game);
        Assert.True(tracer.Active); // 유효 회차 2 < trace_runs 3
        EndAfterExitGrace(tracer);

        Assert.True(File.Exists(TraceWriter.RunPath(Raw, 4)));
        Assert.Equal(run3Text, File.ReadAllText(run3)); // 덮어쓰지 않음
        Assert.Equal(3, AutoTracer.RunCount(_game, File.Exists, AutoTracer.RunReadBytes)); // run1 + run2 + run4
        Assert.Contains(_rig.Log.Lines, l => l.Contains("(run 4, merged)", StringComparison.Ordinal));
        Assert.DoesNotContain(_rig.Log.Lines, l => l.Contains("run 3 ", StringComparison.Ordinal) && l.Contains("saved", StringComparison.Ordinal));
    }

    [Fact]
    public void MergeLeavesOutRunsBelowTheMinimumReadAndNotesTheNewBlocksOfThisRun()
    {
        SetRuns(5);
        SeedRuns(3, (3, 6));
        File.WriteAllText(Raw, "[]"); // 합집합을 비워 두고 병합이 run 파일에서 다시 만드는지 봄
        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings);

        Reads((1000L * MiB, 35L << 20), (1035L * MiB, 35L << 20));
        tracer.Start(_game);
        EndAfterExitGrace(tracer);

        var merged = (List<object?>)JsonRead.ReadFile(Raw)!;
        long firstOffset = (long)((JObject)merged[0]!)["offset"]!;
        Assert.Equal(1000L * MiB, firstOffset); // 최신 회차가 앞
        // run3 (6 MiB, 오프셋 400 MiB 부터) 의 블록은 합집합에서 빠짐: 처음 100 MiB 두 덩어리 + 새 70 MiB
        Assert.Equal((100 + 100 + 70) * 16, merged.Count);
        Assert.Contains("\"new_mib\": 70", File.ReadAllText(AutoTracer.MergeMetaPath(_game)), StringComparison.Ordinal);
        Assert.Contains("\"run\": 4", File.ReadAllText(AutoTracer.MergeMetaPath(_game)), StringComparison.Ordinal);
    }

    [Fact]
    public void MergeLeavesTheCommonSplitBesideTheTraceWithoutChangingTheMergedTrace()
    {
        SetRuns(5);
        SeedRuns(2);
        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings) { MinRunBytes = 0 };

        Reads((1000L * MiB, 35L << 20));
        tracer.Start(_game);
        EndAfterExitGrace(tracer);

        Assert.True(File.Exists(TracePhases.CommonPath(_game)));
        Assert.Contains("\"runs\": 3", File.ReadAllText(TracePhases.CommonPath(_game)), StringComparison.Ordinal);
        Assert.Equal(3, AutoTracer.LastRunNumber(_game, File.Exists));
    }

    [Fact]
    public void UpdateRewriteRecountsTheSavedCommonSoTheChangedFileLeavesIt()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Raw)!);
        var a = new AsterCache.Core.Trace.TraceEvent(@"D:\Games\Foo\a.bin", 0, 100 * MiB);
        var b = new AsterCache.Core.Trace.TraceEvent(@"D:\Games\Foo\b.bin", 0, 100 * MiB);
        for (int n = 1; n <= 3; n++)
        {
            TraceWriter.WriteFile(TraceWriter.RunPath(Raw, n), [a, b]);
        }

        TraceWriter.WriteFile(Raw, [a, b]);
        TraceStageIndex.Write(TraceStageIndex.IndexPath(Raw), new TraceStageIndex([new TraceStage("foo.exe", 0, [])]));
        TracePhases.Recount(_game);
        Assert.Equal(2, TracePhases.CommonOf(_game)!.Count); // 세 회차 모두 읽은 a, b 가 common

        using DataLock? gate = DataLock.TryAcquire(_game.Plan);
        Assert.NotNull(gate);
        AutoTracer.DropChanged(_game, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { b.Path }); // 업데이트로 b.bin 이 바뀜

        Assert.False(File.Exists(TraceStageIndex.IndexPath(Raw)));
        var only = Assert.Single(TracePhases.CommonOf(_game)!);
        Assert.Equal(a.Path, only.Path); // 다시 쓴 run 을 반영해 common 이 갱신되고 b.bin 은 빠짐
    }

    [Fact]
    public void UpdateInvalidatesIoSummaryWhenChangedFileAppearsOnlyInReadModes()
    {
        SeedRuns(1);
        string run = TraceWriter.RunPath(Raw, 1);
        string sidecar = IoSummaryStore.RunPath(run);
        IoSummary summary = IoSummaryWithReadModes(Guid.NewGuid().ToString("N")) with { Files = [], Rows = [] };
        IoSummaryStore.WriteForRun(run, summary);

        using DataLock? gate = DataLock.TryAcquire(_game.Plan);
        Assert.NotNull(gate);
        AutoTracer.DropChanged(_game, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"D:\Games\Foo\a.bin" });

        Assert.False(File.Exists(sidecar));
        Assert.Null(IoSummaryStore.ReadForRun(run));
    }

    [Fact]
    public void ExtraRunsAreRecordedOnlyWhileCoverageIsLowAndTheLastMergeAddedEnoughNewBlocks()
    {
        Extra();
        SeedRuns(3);
        Coverage(22);
        Gain(3, 200);

        Assert.True(_tracer.WantsRun(_game)); // 22 % < 70, 새 블록 200 MiB >= 64, 3 < 8
        _tracer.Start(_game);
        Assert.True(_tracer.Active);
        _tracer.Shutdown();
        Assert.Contains(_rig.Log.Lines, l => l.Contains("auto trace started (run 4/8, coverage 22%)", StringComparison.Ordinal));

        Gain(3, 10); // 새 블록이 64 MiB 미만
        Assert.False(_tracer.WantsRun(_game));
        Gain(3, 64); // 경계는 포함
        Assert.True(_tracer.WantsRun(_game));
        Coverage(100); // 짧은 재실행이 덮어쓴 값: 병합 증가량이 충분하면 계속 수집
        Assert.True(_tracer.WantsRun(_game));
        Gain(3, 63);
        Assert.False(_tracer.WantsRun(_game));
        Gain(3, 200);
        Gain(2, 500); // 다른 병합의 메타는 믿지 않음
        Assert.False(_tracer.WantsRun(_game));
        File.Delete(AutoTracer.MergeMetaPath(_game)); // 값이 없으면 기존 정책
        Assert.False(_tracer.WantsRun(_game));
        Gain(3, 200);
        File.WriteAllText(AutoTracer.MergeMetaPath(_game), "not json");
        Assert.False(_tracer.WantsRun(_game));
    }

    [Fact]
    public void ExtraRunsStopAtTraceRunsMaxAndWithoutACoverageValue()
    {
        Extra(max: 4);
        SeedRuns(4);
        Coverage(22);
        Gain(4, 200);
        Assert.False(_tracer.WantsRun(_game)); // 유효 회차 4 = 상한

        Extra(max: 5);
        Assert.True(_tracer.WantsRun(_game));
        Extra(max: 2); // 상한이 trace_runs 보다 작으면 trace_runs 가 기준
        Assert.Equal(3, AutoTracer.NextTrace(_game, _rig.Settings.Auto, _rig.World.Env(), _rig.Status).Limit);
        Assert.False(_tracer.WantsRun(_game));

        Extra(max: 8);
        _rig.Status.Remove("foo"); // 커버리지 값이 없어도 병합 증가량이 충분하면 수집
        Assert.True(_tracer.WantsRun(_game));
    }

    [Fact]
    public void MissingStageIndexRequestsOneAdditionalTraceAfterTheConfiguredRunLimit()
    {
        Extra(runs: 1, max: 1);
        SeedRuns(1);
        File.Delete(TraceStageIndex.IndexPath(Raw));

        Assert.True(_tracer.WantsRun(_game));
        Reads((0, 65536));
        _tracer.Start(_game);
        Assert.True(_tracer.Active);
        EndAfterExitGrace();

        Assert.True(File.Exists(TraceStageIndex.IndexPath(Raw)));
        Assert.False(_tracer.WantsRun(_game));
    }

    [Fact]
    public void OneAdditionalTraceCanRequestBothTheStageIndexAndIoSummary()
    {
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = true } };
        Extra(runs: 1, max: 1);
        SeedRuns(1);
        File.Delete(TraceStageIndex.IndexPath(Raw));
        Assert.True(_tracer.WantsRun(_game));

        _tracer.Start(_game);

        Assert.True(_tracer.Active);
        Assert.Equal(1, _rig.World.TraceStarts);
        Assert.Contains(_rig.Log.Lines, line => line.Contains("for stage index, attempt 1/2 and I/O summary, attempt 1/4", StringComparison.Ordinal));
        _tracer.Shutdown();
    }

    [Fact]
    public void IoSummaryAdditionalTracingStopsAfterTheAttemptLimit()
    {
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = true } };
        Extra(runs: 1, max: 1);
        SeedRuns(1);
        for (int attempt = 1; attempt <= AutoTracer.MaxIoSummaryTries; attempt++)
        {
            Assert.True(_tracer.WantsRun(_game));
            _rig.World.Session = new FakeTrace();
            _rig.World.Running = true;
            _tracer.Start(_game);
            Assert.Contains(_rig.Log.Lines, line => line.Contains($"I/O summary, attempt {attempt}/{AutoTracer.MaxIoSummaryTries}", StringComparison.Ordinal));
            EndAfterExitGrace();
        }

        Assert.False(_tracer.WantsRun(_game));
        Assert.Equal(AutoTracer.MaxIoSummaryTries, IoSummaryStore.ReadTried(Raw));
        Assert.Equal(AutoTracer.MaxIoSummaryTries, _rig.World.TraceStarts);
    }

    [Fact]
    public void FailedIoSummaryAttemptPersistenceBlocksFurtherAttemptsForThisServiceLifetime()
    {
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = true } };
        Extra(runs: 1, max: 1);
        SeedRuns(1);
        _rig.World.WriteIoSummaryTries = (_, _) => throw new IOException("read only");
        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings) { MinRunBytes = 0 };
        Assert.True(tracer.WantsRun(_game));
        _rig.World.Session = new FakeTrace();
        _rig.World.Running = true;

        tracer.Start(_game);
        EndAfterExitGrace(tracer);

        Assert.Contains(_rig.Log.Lines, line => line.Contains("I/O summary attempt not recorded", StringComparison.Ordinal));
        Assert.False(tracer.WantsRun(_game));
        tracer.Start(_game);

        Assert.Equal(1, _rig.World.TraceStarts);
        Assert.False(tracer.Active);
    }

    [Fact]
    public void OneReadModeSummaryRequestsAnotherTraceUntilTwoSessionsAreCollected()
    {
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = true } };
        Extra(runs: 1, max: 1);
        SeedRuns(1);
        _rig.World.Files.Remove(TraceWriter.RunPath(Raw, 1) + ".io.json");

        Assert.True(_tracer.WantsRun(_game));

        _rig.World.Files.Add(TraceWriter.RunPath(Raw, 1) + ".io.json");
        Assert.True(_tracer.WantsRun(_game));
        _rig.World.Files.Add(IoSummaryStore.StandalonePath(Raw, 1));
        Assert.False(_tracer.WantsRun(_game));
    }

    [Fact]
    public void MissingIoSummaryCanRequestAnAdditionalTraceWhenRunFilesAreAtTheirLimit()
    {
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = true } };
        Extra(runs: 1, max: 1);
        SeedRuns(AutoTracer.MaxRunFiles);

        Assert.Equal(1, AutoTracer.IoSummaryAttempt(_game, _rig.Settings.Auto, _rig.World.Env()));
        Assert.True(_tracer.WantsRun(_game));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void OldIoSummaryAttemptCountsLeaveTwoFurtherObservationAttempts(int previousAttempts)
    {
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = true } };
        Extra(runs: 1, max: 1);
        SeedRuns(1);
        IoSummaryStore.WriteTried(Raw, previousAttempts);

        int first = AutoTracer.IoSummaryAttempt(_game, _rig.Settings.Auto, _rig.World.Env());
        Assert.Equal(previousAttempts + 1, first);
        IoSummaryStore.WriteTried(Raw, first);
        int second = AutoTracer.IoSummaryAttempt(_game, _rig.Settings.Auto, _rig.World.Env());

        Assert.Equal(previousAttempts + 2, second);
    }

    [Fact]
    public void IoSummaryTracingWaitsForTwoDistinctReadModeSessionsAndIgnoresSaveRetries()
    {
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = true } };
        Extra(runs: 2, max: 2);
        SeedRuns(2);
        string run1 = TraceWriter.RunPath(Raw, 1);
        string run2 = TraceWriter.RunPath(Raw, 2);
        IoSummary first = IoSummaryWithReadModes(Guid.NewGuid().ToString("N"));
        IoSummaryStore.WriteForRun(run1, first);
        IoSummaryStore.WriteStandalone(Raw, first); // 같은 플레이의 재저장
        IoSummaryStore.WriteForRun(run2, IoSummaries.With(_rig.World.Now, @"D:\Games\Foo\a.bin")); // v3 와 같은 읽기 방식 미상

        Assert.Equal(1, AutoTracer.IoSummaryAttempt(_game, _rig.Settings.Auto, _rig.World.Env()));

        IoSummaryStore.WriteForRun(run2, IoSummaryWithReadModes(Guid.NewGuid().ToString("N")));

        Assert.Equal(0, AutoTracer.IoSummaryAttempt(_game, _rig.Settings.Auto, _rig.World.Env()));
    }

    // 지금 plans 상태와 같은 모양: 유효 회차가 상한에 닿았고 단계 색인이 있으며 읽기 방식 집계가 2세션인데, 시작 시각이 있는 플레이는 1번뿐
    private void ThinLoadListState(int timedPlays)
    {
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = true } };
        Extra(runs: 1, max: 1);
        SeedRuns(1);
        _rig.World.Files.Add(TraceStageIndex.IndexPath(Raw));
        IoSummaryStore.WriteForRun(TraceWriter.RunPath(Raw, 1), IoSummaryWithReadModes(Guid.NewGuid().ToString("N")));
        IoSummaryStore.WriteStandalone(Raw, IoSummaryWithReadModes(Guid.NewGuid().ToString("N")));
        _rig.World.LoadListTimedPlays = _ => timedPlays; // 저장된 목록 파일의 plays 값
    }

    [Fact]
    public void ALoadListFromFewerThanTwoTimedPlaysRequestsAnotherTraceWithinTheTryLimit()
    {
        ThinLoadListState(timedPlays: 1);

        Assert.Equal(1, AutoTracer.LoadListAttempt(_game, _rig.Settings.Auto, _rig.World.Env()));
        Assert.True(_tracer.WantsRun(_game));
        Assert.Equal(0, AutoTracer.IoSummaryAttempt(_game, _rig.Settings.Auto, _rig.World.Env())); // 다른 항은 모두 거짓

        LoadRefillStore.WriteTried(Raw, AutoTracer.MaxLoadListTries - 1);
        Assert.Equal(AutoTracer.MaxLoadListTries, AutoTracer.LoadListAttempt(_game, _rig.Settings.Auto, _rig.World.Env()));
        LoadRefillStore.WriteTried(Raw, AutoTracer.MaxLoadListTries);
        Assert.False(_tracer.WantsRun(_game));
    }

    [Fact]
    public void ALoadListFromTwoTimedPlaysNeedsNoExtraTrace()
    {
        ThinLoadListState(timedPlays: 2);

        Assert.Equal(0, AutoTracer.LoadListAttempt(_game, _rig.Settings.Auto, _rig.World.Env()));
        Assert.False(_tracer.WantsRun(_game));
    }

    [Fact]
    public void ALoadListExtraTraceIsNotRequestedForABlockedGameOrWithoutIoSummaries()
    {
        ThinLoadListState(timedPlays: 1);
        Assert.True(_tracer.WantsRun(_game));
        Assert.False(_tracer.WantsRun(_game with { AntiCheat = "vanguard" })); // 안티치트 금지 게임
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = false } };
        Assert.False(_tracer.WantsRun(_game));
    }

    [Fact]
    public void StartingALoadListExtraTraceRecordsTheTryAndLogsItsPurpose()
    {
        ThinLoadListState(timedPlays: 1);

        _tracer.Start(_game);
        _tracer.Shutdown();

        Assert.Equal(1, LoadRefillStore.ReadTried(Raw));
        Assert.Contains(_rig.Log.Lines, l => l.Contains($"auto trace started (run 2 for load list, attempt 1/{AutoTracer.MaxLoadListTries})", StringComparison.Ordinal));
    }

    [Fact]
    public void NotNeededLineListsTheValuesBehindTheDecision()
    {
        ThinLoadListState(timedPlays: 2);

        Assert.Equal("auto trace not needed (valid 1/limit 1, relearn no (plays 0/5), stage index yes, io summaries 2, load list from 2 timed plays)", _tracer.NotNeededLine(_game));
    }

    [Fact]
    public void StandaloneReadModeSummarySurvivesTheNextNormalMergeWithItsSessionId()
    {
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = true } };
        Extra(runs: 1, max: 1);
        SeedRuns(1);
        IoSummary first = IoSummaryWithReadModes(Guid.NewGuid().ToString("N"));
        string standalone = IoSummaryStore.WriteStandalone(Raw, first);
        IoSummaryStore.WriteTried(Raw, 1);
        _trace.IoRequests = IoRequests();
        Reads((200L * MiB, MiB));

        Assert.True(_tracer.WantsRun(_game));
        _tracer.Start(_game);
        EndAfterExitGrace();

        IoSummary preserved = Assert.IsType<IoSummary>(IoSummaryStore.ReadStandaloneForRaw(standalone, Raw));
        Assert.Equal(first.SessionId, preserved.SessionId);
        Assert.NotNull(IoSummaryStore.ReadForRun(TraceWriter.RunPath(Raw, 2)));
        Assert.Equal(0, AutoTracer.IoSummaryAttempt(_game, _rig.Settings.Auto, _rig.World.Env()));
    }

    [Fact]
    public void AStandaloneSummaryThatCannotBeRewrittenDoesNotStopTheMerge()
    {
        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = true } };
        Extra(runs: 1, max: 1);
        SeedRuns(1);
        string standalone = IoSummaryStore.WriteStandalone(Raw, IoSummaryWithReadModes(Guid.NewGuid().ToString("N")));
        IoSummaryStore.WriteTried(Raw, 1);
        _trace.IoRequests = IoRequests();
        Reads((200L * MiB, MiB));

        _tracer.Start(_game);
        using (new FileStream(standalone, FileMode.Open, FileAccess.Read, FileShare.Read)) // 읽을 수는 있지만 바꿔 쓸 수는 없는 상태
        {
            EndAfterExitGrace();
        }

        Assert.Contains(_rig.Log.Lines, line => line.Contains("I/O summary file not rewritten", StringComparison.Ordinal));
        Assert.Contains("\"run\": 2", File.ReadAllText(AutoTracer.MergeMetaPath(_game)), StringComparison.Ordinal); // 병합의 뒤 단계까지 감
        Assert.NotNull(IoSummaryStore.ReadForRun(TraceWriter.RunPath(Raw, 2))); // 이번 회차의 집계도 저장됨
        Assert.Null(IoSummaryStore.ReadStandaloneForRaw(standalone, Raw)); // 다시 쓰지 못한 집계는 옛 지문이라 유효하지 않음
    }

    [Fact]
    public void StageIndexTracesStopAfterTwoAttemptsEvenWhenTheRunIsNotSaved()
    {
        Extra(runs: 1, max: 1);
        SeedRuns(1);
        File.Delete(TraceStageIndex.IndexPath(Raw));

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            Assert.True(_tracer.WantsRun(_game));
            _rig.World.Session = new FakeTrace(); // 읽기가 하나도 없어 저장되지 않는 run
            _rig.World.Running = true;
            _tracer.Start(_game);
            Assert.True(_tracer.Active);
            Assert.Contains(_rig.Log.Lines, l => l.Contains($"for stage index, attempt {attempt}/2)", StringComparison.Ordinal));
            EndAfterExitGrace();
            Assert.False(_tracer.Active);
        }

        Assert.False(File.Exists(TraceStageIndex.IndexPath(Raw)));
        Assert.Equal(2, TraceStageIndex.ReadTried(Raw)); // 표식으로 남아 서비스를 다시 시작해도 유지
        Assert.False(_tracer.WantsRun(_game));
        Assert.Equal(2, _rig.World.TraceStarts); // 시도 두 번만 세션을 엶
        Assert.Equal(2, _rig.Log.Lines.Count(l => l.Contains("for stage index, attempt", StringComparison.Ordinal)));
    }

    [Fact]
    public void StageIndexTraceDoesNotChangeTheLearningProgressNorRunWhenTracingIsOff()
    {
        Extra(runs: 1, max: 1);
        SeedRuns(1);
        File.Delete(TraceStageIndex.IndexPath(Raw));
        AutoEnv env = _rig.World.Env();

        Assert.False(AutoTracer.NextTrace(_game, _rig.Settings.Auto, env, _rig.Status).Wanted); // 진행 표시 (trace N/M) 의 근거는 그대로
        Assert.True(_tracer.WantsRun(_game));

        _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { Trace = false } };
        Assert.False(_tracer.WantsRun(_game)); // 전역 추적을 끈 사용자에게는 아무것도 하지 않음
        Assert.Equal(0, AutoTracer.StageTraceAttempt(_game, _rig.Settings.Auto, env));
    }

    [Fact]
    public void FailingToRemoveTheOldStageIndexKeepsTheRunAndTheStaleIndexIsIgnored()
    {
        SetRuns(3);
        FirstRunOfTwoBlocks();
        string index = TraceStageIndex.IndexPath(Raw);
        Assert.NotNull(TraceStageIndex.Read(index));

        Reads((131072, 65536));
        _rig.World.Running = true;
        using (var held = new FileStream(index, FileMode.Open, FileAccess.Read, FileShare.Read)) // 삭제를 막는 열린 핸들 (공유 위반)
        {
            _tracer.Start(_game);
            EndAfterExitGrace();
        }

        Assert.True(File.Exists(TraceWriter.RunPath(Raw, 2))); // 부가 파일 때문에 본 기록을 잃지 않음
        Assert.Contains(_rig.Log.Lines, l => l.Contains("old stage index not removed", StringComparison.Ordinal));
        Assert.DoesNotContain(_rig.Log.Lines, l => l.Contains("auto trace failed", StringComparison.Ordinal));
        Assert.Null(TraceStageIndex.Read(index)); // 낡은 색인은 병합 추적과 맞지 않아 단계 정보 없음으로 취급
    }

    [Fact]
    public void MissingStageIndexDoesNotRequestATraceForAnAntiCheatGame()
    {
        Extra(runs: 1, max: 1);
        SeedRuns(1);
        File.Delete(TraceStageIndex.IndexPath(Raw));
        _rig.World.AntiCheats[_game.OriginRoot] = "vanguard";

        Assert.False(_tracer.WantsRun(_game));
        Assert.Equal(0, _rig.World.TraceStarts);
    }

    [Fact]
    public void ExtraRunsContinueAfterAShortRerunSetCoverageTo100UntilTheMaximum()
    {
        Extra(max: 5);
        SeedRuns(4);
        Coverage(100);
        Gain(4, 200);
        Assert.True(_tracer.WantsRun(_game)); // 유효 4 < 5, 증가량 200 >= 64

        Gain(4, 17); // 짧은 회차의 작은 증가량
        Assert.False(_tracer.WantsRun(_game));

        Gain(4, 200);
        SeedRuns(5);
        Gain(5, 200);
        Assert.False(_tracer.WantsRun(_game)); // 최대 회수
    }

    [Fact]
    public void ExtraRunsFallBackToTheOldPolicyWithoutCoverageOrMergeNotes()
    {
        Extra();
        SeedRuns(3);
        Coverage(10);
        File.Delete(AutoTracer.MergeMetaPath(_game));
        Assert.False(_tracer.WantsRun(_game)); // 병합 메타 없음 (옛 설치)
    }

    [Fact]
    public void AnAntiCheatGameIsNeverTracedEvenWhenTheExtraRunConditionsHold()
    {
        Extra();
        SeedRuns(3);
        Coverage(22);
        Gain(3, 500);
        GameSettings vanguard = _game with { AntiCheat = "vanguard" };

        Assert.True(_tracer.WantsRun(_game));
        _tracer.Start(vanguard);

        Assert.False(_tracer.Active);
        Assert.Equal(0, _rig.World.TraceStarts);
    }

    [Fact]
    public void ExtraRunsAreNotStartedOnceRun32Exists()
    {
        Extra(runs: 3, max: 32);
        SeedRuns(32);
        Coverage(22);
        Gain(32, 500);

        Assert.False(_tracer.WantsRun(_game));
    }

    [Fact]
    public void SmallMergeGainAllowsAnotherTraceAfterFiveGameStartsSinceTheLastValidRun()
    {
        Extra();
        SeedRuns(8);
        Gain(8, 17);
        DateTimeOffset lastTrace = _rig.World.Now;
        _rig.World.FileTimes[TraceWriter.RunPath(Raw, 8)] = lastTrace;

        Assert.False(_tracer.WantsRun(_game));
        for (int i = 1; i < 5; i++)
        {
            DateTimeOffset played = lastTrace.AddMinutes(i);
            _rig.World.Now = played;
            _rig.Status.MarkPlayed(_game.Name, _game.Source, AutoStage.Done, played);
            Assert.False(_tracer.WantsRun(_game));
        }

        DateTimeOffset fifth = lastTrace.AddMinutes(5);
        _rig.World.Now = fifth;
        _rig.Status.MarkPlayed(_game.Name, _game.Source, AutoStage.Done, fifth);

        Assert.True(_tracer.WantsRun(_game));
    }

    [Fact]
    public void SmallMergeGainAllowsAnotherTraceAfterSevenDays()
    {
        Extra();
        SeedRuns(3);
        Gain(3, 17);
        DateTimeOffset lastTrace = _rig.World.Now;
        _rig.World.FileTimes[TraceWriter.RunPath(Raw, 3)] = lastTrace;

        _rig.World.Now = lastTrace.AddDays(7).AddTicks(-1);
        Assert.False(_tracer.WantsRun(_game));

        _rig.World.Now = lastTrace.AddDays(7);
        Assert.True(_tracer.WantsRun(_game));
    }

    [Fact]
    public void GameUpdateReopensTraceLearningWithoutMergeGrowth()
    {
        Extra();
        SeedRuns(3);
        Gain(3, 17);
        _rig.Status.Set(_game.Name, _game.Source, null, AutoStage.Discovered, "game updated, will trace again on next run");

        Assert.True(_tracer.WantsRun(_game));
    }

    [Fact]
    public void AnAntiCheatGameIsNeverTracedWhenAnUpdateReopensTraceLearning()
    {
        Extra();
        SeedRuns(3);
        Gain(3, 17);
        _rig.Status.Set(_game.Name, _game.Source, null, AutoStage.Discovered, "game updated, will trace again on next run");
        GameSettings vanguard = _game with { AntiCheat = "vanguard" };

        Assert.True(_tracer.WantsRun(_game));
        _tracer.Start(vanguard);

        Assert.False(_tracer.Active);
        Assert.Equal(0, _rig.World.TraceStarts);
    }

    [Fact]
    public void RelearningAtTheRunFileLimitCompactsTheMergedTraceAndFreesANewRunNumber()
    {
        Extra(runs: 3, max: 8);
        SeedRuns(AutoTracer.MaxRunFiles);
        DateTimeOffset ioStarted = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        for (int run = 1; run <= AutoTracer.MaxRunFiles; run++)
        {
            DateTimeOffset started = ioStarted.AddMinutes(run);
            IoSummary io = IoSummaries.With(started, @"D:\Games\Foo\a.bin");
            IoSummaryStore.Write(IoSummaryStore.RunPath(TraceWriter.RunPath(Raw, run)), io);
        }

        HashSet<(string Path, long Offset, long Length)> before = TraceBlocks(Raw);
        _rig.World.FileTimes[TraceWriter.RunPath(Raw, AutoTracer.MaxRunFiles)] = _rig.World.Now.AddDays(-7);
        Reads(Enumerable.Range(0, 20).Select(i => (8000L * MiB + i * 2L * MiB, 65536L)).ToArray());

        _tracer.Start(_game);

        Assert.True(_tracer.Active);
        Assert.Equal(AutoTracer.MaxRunFiles - 1, AutoTracer.LastRunNumber(_game, File.Exists)); // 병합본을 기준으로 run 파일 번호를 당김
        Assert.Equal(ioStarted.AddMinutes(2), IoSummaryStore.Read(IoSummaryStore.RunPath(TraceWriter.RunPath(Raw, 1)))!.ObservedAtUtc);
        Assert.False(File.Exists(IoSummaryStore.RunPath(TraceWriter.RunPath(Raw, AutoTracer.MaxRunFiles)))); // 가장 오래된 회차의 집계도 함께 버림
        _rig.World.Running = false;
        _tracer.Step();
        _rig.World.Now += GameExitTracker.Grace;
        _tracer.Step();

        Assert.True(File.Exists(TraceWriter.RunPath(Raw, AutoTracer.MaxRunFiles)));
        Assert.False(File.Exists(TraceWriter.RunPath(Raw, AutoTracer.MaxRunFiles + 1)));
        Assert.Equal(AutoTracer.MaxRunFiles, AutoTracer.LastRunNumber(_game, File.Exists));
        Assert.Equal("foo2.exe", Assert.Single(TraceStageIndex.ReadMarks(TraceWriter.RunPath(Raw, 1))!).Exe);
        HashSet<(string Path, long Offset, long Length)> after = TraceBlocks(Raw);
        Assert.Subset(after, before); // 기존 병합본의 블록이 모두 남음
        Assert.Contains((@"D:\Games\Foo\a.bin", 8000L * MiB, 65536L), after);
    }

    [Fact]
    public void TheCapStopsTheSessionAtOnceButNormalizingAndSavingWaitForTheGameAndItsDiskNeighboursToExit()
    {
        bool neighbour = true;
        AutoEnv env = _rig.World.Env();
        env.GameRunningOnDisk = _ => neighbour;
        var tracer = new AutoTracer(env, _rig.Status, _rig.Log, () => _rig.Settings) { MinRunBytes = 0 };
        Reads((0, 65536));
        tracer.Start(_game);
        _rig.World.Procs = [(100, 1L << 20)];
        _rig.World.Running = true;
        tracer.Step();

        _rig.World.Now += TimeSpan.FromSeconds(30);
        tracer.Step(); // 시간 상한: 게임은 아직 실행 중
        Assert.False(tracer.Active);
        Assert.True(_trace.Stopped);
        Assert.Equal(0, _rig.World.CanonicalizeCalls);
        Assert.False(File.Exists(Raw));

        tracer.Start(_game); // 저장 대기 중인 게임은 새로 시작하지 않음
        Assert.Equal(1, _rig.World.TraceStarts);

        _rig.World.Running = false; // 게임은 끝났지만 같은 디스크의 다른 등록 게임이 실행 중
        tracer.Step();
        Assert.Equal(0, _rig.World.CanonicalizeCalls);
        Assert.False(File.Exists(Raw));

        _rig.World.Now += GameExitTracker.Grace;
        tracer.Step(); // 게임 종료 유예는 지났지만 같은 디스크 게임이 있어 저장은 보류
        Assert.Equal(0, _rig.World.CanonicalizeCalls);
        Assert.False(File.Exists(Raw));

        neighbour = false;
        tracer.Step();
        Assert.Equal(1, _rig.World.CanonicalizeCalls);
        Assert.True(File.Exists(Raw));
        tracer.Step();
        Assert.Equal(1, _rig.World.CanonicalizeCalls); // 한 번만 저장
        Assert.Contains(_rig.Log.Lines, l => l.Contains("(time cap)", StringComparison.Ordinal) && l.Contains("read events saved", StringComparison.Ordinal));
    }

    [Fact]
    public void AHeldTraceIsSavedOnceTheRegisteredExeEndsAfterGrace()
    {
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 1L << 20)];
        _rig.World.Running = true;
        _tracer.Step();
        _rig.World.Now += TimeSpan.FromSeconds(30);
        _tracer.Step(); // 시간 상한: 세션을 멈추고 게임이 끝나길 기다림
        Assert.False(File.Exists(Raw));

        _rig.World.Running = false;
        _tracer.Step();
        Assert.False(File.Exists(Raw));
        _rig.World.Now += GameExitTracker.Grace;
        _tracer.Step();

        Assert.True(File.Exists(Raw));
    }

    [Fact]
    public void AStartDuringTheDeferredNormalizationDoesNotOpenASessionAndTakeTheSameRunNumber()
    {
        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings) { MinRunBytes = 0 };
        int startsDuringSave = -1;
        _rig.World.OnCanonicalize = () =>
        {
            tracer.Start(_game); // 정규화 도중 같은 게임의 시작
            startsDuringSave = _rig.World.TraceStarts;
        };
        Reads((0, 65536));
        tracer.Start(_game);
        _rig.World.Procs = [(100, 1L << 20)];
        _rig.World.Running = true;
        tracer.Step();
        _rig.World.Now += TimeSpan.FromSeconds(30);
        tracer.Step();
        _rig.World.Running = false;

        tracer.Step();
        _rig.World.Now += GameExitTracker.Grace;
        tracer.Step();

        Assert.Equal(1, startsDuringSave);
        Assert.True(File.Exists(Raw));
        Assert.False(tracer.Active);
    }

    [Fact]
    public void ShutdownSavesATraceHeldAfterTheCapInsteadOfLosingIt()
    {
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 1L << 20)];
        _rig.World.Running = true;
        _tracer.Step();
        _rig.World.Now += TimeSpan.FromSeconds(30);
        _tracer.Step();
        Assert.False(File.Exists(Raw));

        _tracer.Shutdown();

        Assert.True(File.Exists(Raw));
    }

    [Fact]
    public void AMergeBuildsFromTheRunsAloneUnlessRunFilesWereRotatedOut()
    {
        Extra(runs: 1, max: 8);
        SeedRuns(2);
        TraceWriter.WriteFile(Raw, [new AsterCache.Core.Trace.TraceEvent(@"D:\Games\Foo\a.bin", 9000L * MiB, 65536)]); // 병합본에만 있는 옛 블록
        Gain(2, 100);
        Reads((8000L * MiB, 65536));

        _tracer.Start(_game);
        _rig.World.Running = false;
        _tracer.Step();
        _rig.World.Now += GameExitTracker.Grace;
        _tracer.Step();

        HashSet<(string Path, long Offset, long Length)> merged = TraceBlocks(Raw);
        Assert.Contains((@"D:\Games\Foo\a.bin", 8000L * MiB, 65536L), merged);
        Assert.DoesNotContain((@"D:\Games\Foo\a.bin", 9000L * MiB, 65536L), merged);
    }

    [Theory]
    [InlineData(2048, 3000, 3000)] // 명시한 읽기 캡이 이김
    [InlineData(2048, 6144, 6144)] // 기본값 (6144): 존 상한 x 2 = 4096 보다 크므로 6144
    [InlineData(4096, 6144, 8192)] // 기본값: max(6144, 4096 x 2)
    public void ReadCapDefaultFollowsTheZoneLimitAndAnExplicitValueWins(int zoneMib, int capMib, int stopAtMib)
    {
        _rig.Settings = _rig.Settings with { MaxZoneMib = zoneMib, Auto = _rig.Settings.Auto with { TraceCapSeconds = 7200, TraceCapMib = capMib } };
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Running = true;
        _rig.World.Procs = [(100, 0)];
        _tracer.Step();

        _rig.World.Procs = [(100, (stopAtMib - 1L) << 20)];
        _tracer.Step();
        Assert.True(_tracer.Active);

        _rig.World.Procs = [(100, (long)stopAtMib << 20)];
        _tracer.Step();
        Assert.False(_tracer.Active);
        Assert.Contains(_rig.Log.Lines, l => l.Contains("(read cap)", StringComparison.Ordinal));
    }

    // ---- 끝난 뒤 변환·서비스 종료·집계 파일 정리 (집계 스위치를 켠 상태) ----

    private void EnableIoSummary() => _rig.Settings = _rig.Settings with { Auto = _rig.Settings.Auto with { IoSummaryEnabled = true } };

    private void RunUntilRawIsFrozen()
    {
        Reads((0, 65536));
        _trace.IoRequests = IoRequests();
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 5L << 20)];
        _rig.World.Running = true;
        _tracer.Step();
        _rig.World.Now += TimeSpan.FromSeconds(30);
        _tracer.Step();
    }

    [Fact]
    public void ARawTraceFrozenAtTheCapIsSavedWhenTheServiceShutsDownAndTheSummaryIsSkipped()
    {
        EnableIoSummary();
        RunUntilRawIsFrozen();
        Assert.True(_trace.RawRecordsFrozen);
        Assert.False(File.Exists(Raw));

        _tracer.Shutdown();

        Assert.True(File.Exists(Raw)); // 이 변경 전에는 상한에서 세션을 멈추고 보류해 둔 기록이라 종료 때 저장됨
        Assert.True(_trace.Stopped);
        Assert.True(_trace.Disposed);
        Assert.Equal(0, _rig.World.BuildIoSummaryCalls);
        Assert.False(File.Exists(IoSummaryStore.RunPath(TraceWriter.RunPath(Raw, 1))));
        Assert.Contains(_rig.Log.Lines, line => line.Contains("saving the raw trace", StringComparison.Ordinal));
    }

    [Fact]
    public void AServiceShutdownBeforeTheRawCaptureEndsStillDiscardsTheRecordsAsBefore()
    {
        EnableIoSummary();
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 5L << 20)];
        _rig.World.Running = true;
        _tracer.Step();

        _tracer.Shutdown();

        Assert.False(File.Exists(Raw));
        Assert.True(_trace.Disposed);
        Assert.False(_trace.Stopped);
        Assert.Equal(0, _rig.World.BuildIoSummaryCalls);
    }

    [Fact]
    public void ShutdownWhileTheRawTraceIsFrozenWithTheSwitchOffKeepsTheOldHoldAndSavePath()
    {
        Reads((0, 65536));
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 5L << 20)];
        _rig.World.Running = true;
        _tracer.Step();
        _rig.World.Now += TimeSpan.FromSeconds(30);
        _tracer.Step(); // 스위치가 꺼져 있으면 상한에서 세션을 멈추고 기록을 보류함 (이전 동작)
        Assert.True(_trace.Stopped);
        Assert.False(File.Exists(Raw));

        _tracer.Shutdown();

        Assert.True(File.Exists(Raw));
        Assert.Equal(0, _rig.World.BuildIoSummaryCalls);
    }

    [Fact]
    public void ASummaryBuiltWhileThePlanChangedIsDroppedButTheRawTraceIsStillSaved()
    {
        EnableIoSummary();
        Reads((0, 65536));
        _trace.IoRequests = IoRequests();
        _rig.World.BuildIoSummary = (snapshot, _, _) =>
        {
            _rig.World.FileTimes[_game.Plan] = _rig.World.Now.AddMinutes(5); // 변환하는 사이 재배치가 계획을 바꿈
            return new IoBlockMapper(snapshot).Build(0, 0, 0, 0, 0, false);
        };

        _tracer.Start(_game);
        EndAfterExitGrace();

        Assert.True(File.Exists(Raw));
        Assert.False(File.Exists(IoSummaryStore.RunPath(TraceWriter.RunPath(Raw, 1))));
        Assert.Contains(_rig.Log.Lines, line => line.Contains("I/O summary dropped, the plan or journal changed", StringComparison.Ordinal));
    }

    [Fact]
    public void AFailingFileLayoutScanOnlyWarnsAndTheRawTraceIsStillSaved()
    {
        EnableIoSummary();
        Reads((0, 65536));
        _trace.IoRequests = IoRequests();
        _rig.World.BuildIoSummary = (_, _, _) => throw new IOException("scan failed");

        _tracer.Start(_game);
        EndAfterExitGrace();

        Assert.True(File.Exists(Raw));
        Assert.Equal(1, _rig.World.BuildIoSummaryCalls);
        Assert.False(File.Exists(IoSummaryStore.RunPath(TraceWriter.RunPath(Raw, 1))));
        Assert.Contains(_rig.Log.Lines, line => line.Contains("I/O summary not built (IOException)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheScanReceivesTheGameRootsAndTheRequestTableOnceAfterTheGameExits()
    {
        EnableIoSummary();
        Reads((0, 65536));
        _trace.IoRequests = IoRequests();
        IReadOnlyCollection<string>? seenRoots = null;
        _rig.World.BuildIoSummary = (snapshot, roots, _) =>
        {
            seenRoots = roots;
            return new IoBlockMapper(snapshot).Build(1, 0, 0, 0, 3, false);
        };

        _tracer.Start(_game);
        EndAfterExitGrace();

        Assert.Equal(_game.AllowedRoots, seenRoots);
        Assert.Equal(1, _rig.World.BuildIoSummaryCalls);
        Assert.Contains(_rig.Log.Lines, line => line.Contains("1 files scanned in 3 ms", StringComparison.Ordinal));
    }

    [Fact]
    public void ALockedIoSummaryFileOnlyWarnsAndTheRawRunIsStillMergedWithTheSwitchOff()
    {
        SetRuns(3);
        FirstRunOfTwoBlocks();
        string run2Io = IoSummaryStore.RunPath(TraceWriter.RunPath(Raw, 2));
        File.WriteAllText(run2Io, "{}");
        using var hold = new FileStream(run2Io, FileMode.Open, FileAccess.Read, FileShare.None);
        Reads((131072, 65536));

        _tracer.Start(_game);
        EndAfterExitGrace();

        Assert.True(File.Exists(TraceWriter.RunPath(Raw, 2)));
        Assert.Contains(_rig.Log.Lines, l => l.Contains("(run 2, merged)", StringComparison.Ordinal));
        Assert.Contains(_rig.Log.Lines, l => l.Contains("I/O summary file not removed", StringComparison.Ordinal));
    }

    [Fact]
    public void LockedIoSummaryFilesDoNotBlockTheRunFileRotation()
    {
        Extra(runs: 3, max: 8);
        SeedRuns(AutoTracer.MaxRunFiles);
        string oldestIo = IoSummaryStore.RunPath(TraceWriter.RunPath(Raw, 1));
        string secondIo = IoSummaryStore.RunPath(TraceWriter.RunPath(Raw, 2));
        IoSummaryStore.Write(oldestIo, IoSummaries.With(_rig.World.Now, @"D:\Games\Foo\a.bin"));
        IoSummaryStore.Write(secondIo, IoSummaries.With(_rig.World.Now, @"D:\Games\Foo\a.bin"));
        IoSummaryStore.WriteTried(Raw, 2);
        using var holdOldest = new FileStream(oldestIo, FileMode.Open, FileAccess.Read, FileShare.None);
        using var holdTried = new FileStream(IoSummaryStore.TriedPath(Raw), FileMode.Open, FileAccess.Read, FileShare.None);
        _rig.World.FileTimes[TraceWriter.RunPath(Raw, AutoTracer.MaxRunFiles)] = _rig.World.Now.AddDays(-7);
        Reads(Enumerable.Range(0, 20).Select(i => (8000L * MiB + i * 2L * MiB, 65536L)).ToArray());

        _tracer.Start(_game);

        Assert.True(_tracer.Active);
        Assert.Equal(AutoTracer.MaxRunFiles - 1, AutoTracer.LastRunNumber(_game, File.Exists)); // 집계 파일을 못 지워도 회전은 끝남
        Assert.Contains(_rig.Log.Lines, l => l.Contains("I/O summary file not removed", StringComparison.Ordinal));
    }

    [Fact]
    public void DeletingTheAttemptFileResetsTheInMemoryCountButAFailedPersistenceStillBlocks()
    {
        EnableIoSummary();
        Extra(runs: 1, max: 1);
        SeedRuns(1);
        for (int attempt = 1; attempt <= AutoTracer.MaxIoSummaryTries; attempt++)
        {
            Assert.True(_tracer.WantsRun(_game));
            _rig.World.Session = new FakeTrace();
            _rig.World.Running = true;
            _tracer.Start(_game);
            EndAfterExitGrace();
        }

        Assert.False(_tracer.WantsRun(_game));

        File.Delete(IoSummaryStore.TriedPath(Raw)); // 업데이트·회전이 시도 횟수 파일을 지움: 새 세대

        Assert.True(_tracer.WantsRun(_game));
    }

    // ---- 끝난 뒤 변환의 양보·예산, 동결 실패, 종료와 Finish 의 겹침 ----

    private static void BlockUntilCancelled(CancellationToken ct, ManualResetEventSlim? entered = null)
    {
        entered?.Set();
        ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
        ct.ThrowIfCancellationRequested();
    }

    [Fact]
    public async Task AGameStartCancelsTheFileLayoutScanAndTheRawTraceIsStillSaved()
    {
        EnableIoSummary();
        Reads((0, 65536));
        _trace.IoRequests = IoRequests();
        using var entered = new ManualResetEventSlim();
        _rig.World.BuildIoSummary = (_, _, ct) =>
        {
            BlockUntilCancelled(ct, entered);
            return null;
        };
        _tracer.Start(_game);

        Task end = Task.Run(() => EndAfterExitGrace());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));
        _tracer.CancelShadow("other"); // 감시기가 등록 게임 시작마다 부름: 어느 게임이든 변환을 멈춤
        await end.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(File.Exists(Raw));
        Assert.False(File.Exists(IoSummaryStore.RunPath(TraceWriter.RunPath(Raw, 1))));
        Assert.Contains(_rig.Log.Lines, line => line.Contains("I/O summary not built (a game started)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheTimeBudgetStopsTheFileLayoutScanAndTheRawTraceIsStillSaved()
    {
        EnableIoSummary();
        Reads((0, 65536));
        _trace.IoRequests = IoRequests();
        _rig.World.BuildIoSummary = (_, _, ct) =>
        {
            BlockUntilCancelled(ct);
            return null;
        };
        var tracer = new AutoTracer(_rig.World.Env(), _rig.Status, _rig.Log, () => _rig.Settings) { MinRunBytes = 0, IoScanBudget = TimeSpan.FromMilliseconds(50) };

        tracer.Start(_game);
        EndAfterExitGrace(tracer);

        Assert.True(File.Exists(Raw));
        Assert.Contains(_rig.Log.Lines, line => line.Contains("I/O summary not built (time budget)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AServiceShutdownDuringTheScanIsLoggedAsShutdown()
    {
        EnableIoSummary();
        Reads((0, 65536));
        _trace.IoRequests = IoRequests();
        using var entered = new ManualResetEventSlim();
        _rig.World.BuildIoSummary = (_, _, ct) =>
        {
            BlockUntilCancelled(ct, entered);
            return null;
        };
        _tracer.Start(_game);
        Task end = Task.Run(() => EndAfterExitGrace());
        Assert.True(entered.Wait(TimeSpan.FromSeconds(30)));

        _tracer.Shutdown();
        await end.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Contains(_rig.Log.Lines, line => line.Contains("I/O summary not built (service shutdown)", StringComparison.Ordinal));
    }

    [Fact]
    public void AGameAlreadyRunningOnTheDiskDefersTheScanButTheRawTraceAndTheWrittenSizesAreSavedAtOnce()
    {
        EnableIoSummary();
        Reads((0, 65536));
        _trace.IoRequests = IoRequests();
        AutoEnv env = _rig.World.Env();
        env.GameRunningOnDisk = _ => true;
        var tracer = new AutoTracer(env, _rig.Status, _rig.Log, () => _rig.Settings) { MinRunBytes = 0 };

        tracer.Start(_game);
        EndAfterExitGrace(tracer);

        Assert.True(File.Exists(Raw));
        Assert.Equal(0, _rig.World.BuildIoSummaryCalls);
        Assert.Contains(_rig.Log.Lines, line => line.Contains("I/O summary deferred (a game is running on the disk)", StringComparison.Ordinal));
        Assert.True(File.Exists(WrittenFileHistory.PathFor(Raw))); // 크기 기록은 집계와 떼어 바로 남음
    }

    [Fact]
    public void ADeferredScanIsBuiltAndSavedWhenTheGameOnTheDiskExitsAndNotBefore()
    {
        EnableIoSummary();
        Reads((0, 65536));
        _trace.IoRequests = IoRequests();
        bool neighbour = true;
        AutoEnv env = _rig.World.Env();
        env.GameRunningOnDisk = _ => neighbour;
        var tracer = new AutoTracer(env, _rig.Status, _rig.Log, () => _rig.Settings) { MinRunBytes = 0 };
        tracer.Start(_game);
        EndAfterExitGrace(tracer);

        tracer.Step(); // 다른 게임이 아직 떠 있음

        Assert.Equal(0, _rig.World.BuildIoSummaryCalls);
        Assert.DoesNotContain(IoSummaryStore.StandalonePaths(Raw), File.Exists);

        neighbour = false;
        tracer.Step();

        Assert.Equal(1, _rig.World.BuildIoSummaryCalls);
        Assert.Single(IoSummaryStore.StandalonePaths(Raw), File.Exists); // 단독 집계로 저장
        Assert.Contains(_rig.Log.Lines, line => line.Contains("deferred I/O summary built and saved", StringComparison.Ordinal));
        tracer.Step();
        Assert.Equal(1, _rig.World.BuildIoSummaryCalls); // 한 번만
    }

    [Fact]
    public void ADeferredPlayHeldForTheZoneLockIsSavedByTheRetryOnlyAndNotCountedTwice()
    {
        EnableIoSummary();
        Reads((0, 65536));
        _trace.IoRequests = IoRequests();
        bool neighbour = true;
        AutoEnv env = _rig.World.Env();
        env.GameRunningOnDisk = _ => neighbour;
        var tracer = new AutoTracer(env, _rig.Status, _rig.Log, () => _rig.Settings) { MinRunBytes = 0 };
        tracer.Start(_game);

        using (DataLock? gate = DataLock.TryAcquire(_game.Plan)) // 정리·병합 중이라 잠금이 바쁨: 미룬 채 보류됨
        {
            Assert.NotNull(gate);
            _rig.World.Running = false;
            tracer.Step();
            _rig.World.Now += GameExitTracker.Grace;
            tracer.Step();
            Assert.False(File.Exists(Raw));
            Assert.Equal(0, _rig.World.BuildIoSummaryCalls);
            neighbour = false; // 잠금이 풀리기 전에 다른 게임이 끝남
        }

        tracer.Step(); // 보류한 저장의 재시도가 집계를 만들어 회차에 붙여 저장하고, 같은 Step 의 미룬 집계는 건너뜀

        Assert.Equal(1, _rig.World.BuildIoSummaryCalls);
        Assert.True(File.Exists(IoSummaryStore.RunPath(TraceWriter.RunPath(Raw, 1))));
        Assert.DoesNotContain(IoSummaryStore.StandalonePaths(Raw), File.Exists); // 같은 플레이의 두 번째 집계가 없음
    }

    [Fact]
    public void OnlyTheNewestDeferredPlayOfAGameIsKept()
    {
        EnableIoSummary();
        Reads((0, 65536));
        _trace.IoRequests = IoRequests();
        bool neighbour = true;
        AutoEnv env = _rig.World.Env();
        env.GameRunningOnDisk = _ => neighbour;
        var tracer = new AutoTracer(env, _rig.Status, _rig.Log, () => _rig.Settings) { MinRunBytes = 0 };
        tracer.Start(_game);
        EndAfterExitGrace(tracer);
        _rig.World.Running = true;
        tracer.Start(_game); // 같은 게임의 다음 플레이도 다른 게임이 떠 있는 동안 끝남
        EndAfterExitGrace(tracer);

        Assert.Contains(_rig.Log.Lines, line => line.Contains("an older deferred I/O summary was dropped", StringComparison.Ordinal));
        neighbour = false;
        tracer.Step();

        Assert.Equal(1, _rig.World.BuildIoSummaryCalls); // 오래된 플레이의 집계는 만들지 않음
    }

    [Fact]
    public void WithoutAnotherGameTheScanIsBuiltAtTheEndAsBeforeAndTheSizesAreRecordedOnce()
    {
        EnableIoSummary();
        Reads((0, 65536));
        _trace.IoRequests = IoRequests();
        AutoEnv env = _rig.World.Env();
        env.GameRunningOnDisk = _ => false;
        var tracer = new AutoTracer(env, _rig.Status, _rig.Log, () => _rig.Settings) { MinRunBytes = 0 };

        tracer.Start(_game);
        EndAfterExitGrace(tracer);

        Assert.Equal(1, _rig.World.BuildIoSummaryCalls);
        Assert.DoesNotContain(_rig.Log.Lines, line => line.Contains("deferred", StringComparison.Ordinal));
        Assert.True(File.Exists(WrittenFileHistory.PathFor(Raw)));
    }

    [Fact]
    public void AFailedRawFreezeSavesAndScansOnlyAfterTheGameExits()
    {
        EnableIoSummary();
        Reads((0, 65536));
        _trace.IoRequests = IoRequests();
        _trace.FreezeFailure = new IOException("pump ended");
        _tracer.Start(_game);
        _rig.World.Procs = [(100, 5L << 20)];
        _rig.World.Running = true;
        _tracer.Step();
        _rig.World.Now += TimeSpan.FromSeconds(30);

        _tracer.Step(); // 시간 상한: 동결이 실패해도 게임이 도는 동안에는 저장·변환하지 않음

        Assert.True(_trace.Stopped);
        Assert.False(File.Exists(Raw));
        Assert.Equal(0, _rig.World.BuildIoSummaryCalls);
        Assert.Contains(_rig.Log.Lines, line => line.Contains("raw trace could not be frozen", StringComparison.Ordinal));

        EndAfterExitGrace();

        Assert.True(File.Exists(Raw));
        Assert.Equal(1, _rig.World.BuildIoSummaryCalls);
    }

    [Fact]
    public async Task AServiceShutdownDuringFinishDoesNotStopTheSameSessionTwice()
    {
        EnableIoSummary();
        RunUntilRawIsFrozen();
        using var gate = new ManualResetEventSlim();
        _trace.StopGate = gate;
        _rig.World.Running = false;
        Task finish = Task.Run(() =>
        {
            _tracer.Step();
            _rig.World.Now += GameExitTracker.Grace;
            _tracer.Step();
        });
        Assert.True(_trace.StopStarted.Wait(TimeSpan.FromSeconds(30))); // Finish 가 세션을 멈추는 중

        _tracer.Shutdown();
        gate.Set();
        await finish.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, _trace.StopCalls);
        Assert.True(File.Exists(Raw)); // 저장은 Finish 쪽에서 한 번
    }

    [Fact]
    public void AnUnreadableAttemptFileDoesNotLowerTheInMemoryCount()
    {
        EnableIoSummary();
        Extra(runs: 1, max: 1);
        SeedRuns(1);
        for (int attempt = 1; attempt <= AutoTracer.MaxIoSummaryTries; attempt++)
        {
            Assert.True(_tracer.WantsRun(_game));
            _rig.World.Session = new FakeTrace();
            _rig.World.Running = true;
            _tracer.Start(_game);
            EndAfterExitGrace();
        }

        using var hold = new FileStream(IoSummaryStore.TriedPath(Raw), FileMode.Open, FileAccess.Read, FileShare.None); // 있지만 읽을 수 없음

        Assert.Null(IoSummaryStore.ReadTriedOrNull(Raw));
        Assert.False(_tracer.WantsRun(_game)); // "파일 없음" 과 달리 메모리 횟수 (2) 를 그대로 믿음
    }

    // ---- 변환 전후 상태 조회 (저널) 실패 ----

    private static bool ScanSourceCleared(AutoTracer tracer) =>
        typeof(AutoTracer).GetField("_ioScanSource", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(tracer) is null;

    [Theory]
    [InlineData(1)] // 변환 전
    [InlineData(2)] // 변환 뒤
    public void AFailingStateLookupBeforeOrAfterTheScanOnlyDropsTheSummaryAndTheRawTraceIsStillSaved(int failingCall)
    {
        EnableIoSummary();
        Reads((0, 65536));
        _trace.IoRequests = IoRequests();
        int calls = 0;
        _rig.World.JournalRows = _ => ++calls == failingCall ? throw new IOException("journal locked") : 0;

        _tracer.Start(_game);
        EndAfterExitGrace();

        Assert.True(File.Exists(Raw));
        Assert.False(File.Exists(IoSummaryStore.RunPath(TraceWriter.RunPath(Raw, 1))));
        Assert.Equal(failingCall == 2 ? 1 : 0, _rig.World.BuildIoSummaryCalls);
        Assert.True(ScanSourceCleared(_tracer));
        Assert.Contains(_rig.Log.Lines, line => line.Contains("I/O summary not built (IOException)", StringComparison.Ordinal));
    }

    [Fact]
    public void AGameStartingWhileTheChangeJournalIsReadDropsTheResultWhileANormalReadIsAppliedToTheHistory()
    {
        string written = @"D:\Games\Foo\xpak_cache\cache_0_2.xpakdata";
        string historyPath = WrittenFileHistory.PathFor(Raw);
        var history = new WrittenFileHistory();
        history.Record("s1", [(written, 100L << 20, 0)], 0);
        history.Write(historyPath);
        DateTime t0 = new(2026, 11, 21, 0, 0, 0, DateTimeKind.Utc);
        _rig.World.PlayRecords = [new SessionSpan("p1", t0.AddHours(1), t0.AddHours(2))];
        _rig.World.FileReference = _ => 7UL;
        UsnRead Read() => new(t0, [new UsnEvent(7, t0.AddHours(1.5), UsnReasons.DataExtend)]);

        _rig.World.ReadUsn = (_, _, _) =>
        {
            _rig.World.Running = true; // 읽는 도중 게임이 시작됨
            return Read();
        };
        _tracer.FillFromJournal(_game, [written]);

        Assert.Empty(WrittenFileHistory.Read(historyPath).Files.Single().Journal ?? []); // 결과를 반영하지 않음
        Assert.Contains(_rig.Log.Lines, l => l.Contains("change journal result dropped, a game started", StringComparison.Ordinal));
        Assert.DoesNotContain(_rig.Log.Lines, l => l.Contains("(after the change journal)", StringComparison.Ordinal)); // 플레이 중에 순위 목록을 다시 만들지 않음

        _rig.World.Running = false;
        _rig.World.ReadUsn = (_, _, _) => Read();
        _tracer.FillFromJournal(_game, [written]);

        WrittenJournalSession judged = Assert.Single(WrittenFileHistory.Read(historyPath).Files.Single().Journal!);
        Assert.Equal(("p1", 'e'), (judged.Session, judged.Verdict));
    }
}
