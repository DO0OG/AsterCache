using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Service;

/// 등록값은 null 인데 폴더 이름으로만 안티치트가 보이는 게임 (이미 등록된 게임): 저장값만 보던 호출부가 탐지 결과도 반영함.
public sealed partial class AutoRelocatorTests
{
    private int _profileCalls;

    private void DetectAntiCheat(string? kind) => _w.AntiCheats[_game.OriginRoot] = kind;

    private IReadOnlyList<TraceEvent> WholeFiles() => [new TraceEvent(F2, 0, OneMib), new TraceEvent(F1, 0, OneMib)];

    [Fact]
    public void DetectedKernelAntiCheatWithNothingStoredGetsTheProfileSupplementInItsPlan()
    {
        DetectAntiCheat("kernel");
        ReportFullMoves();
        var seen = CaptureSessions();
        _w.ProfileEvents = (_, _) => WholeFiles();

        Tick();

        Assert.Null(_game.AntiCheat);
        Assert.Equal(2 * OneMib, UniqueBytes(seen[0][0])); // 트레이스 ∪ 프로필 = 두 파일 전체
        Assert.True(PlanFile.Load(_game.Plan).Stats.ContainsKey("profile_supplement_mib"));
    }

    [Fact]
    public void NothingStoredAndNothingDetectedKeepsTheTraceOnlyPlan()
    {
        ReportFullMoves();
        var seen = CaptureSessions();
        _w.ProfileEvents = (_, _) =>
        {
            _profileCalls++;
            return WholeFiles();
        };

        Tick();

        Assert.Equal(0, _profileCalls);
        Assert.Equal(Sessions[0], seen[0][0]);
        var stats = PlanFile.Load(_game.Plan).Stats;
        Assert.False(stats.ContainsKey("profile_supplement_mib") || stats.ContainsKey("profile_supplement_checked"));
    }

    [Fact]
    public void UpdateOfAGameWithDetectedKernelAntiCheatKeepsTheTrace()
    {
        DetectAntiCheat("kernel"); // 등록값은 null
        long start = DoneAndReadyToReplan(out string trace);
        _w.Files.Add(TraceWriter.RunPath(trace, 1));

        UpdateAndDetect();

        Assert.Contains(trace, _w.Files);
        Assert.Contains(TraceWriter.RunPath(trace, 1), _w.Files);
        Assert.DoesNotContain(trace + ".stale", _w.Files);
        Assert.Contains(_log.Lines, l => l.Contains(KeptLine, StringComparison.Ordinal));

        Run(TimeSpan.FromMinutes(20));

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(start, PlanFile.Load(_game.Plan).Zone.StartLcn);
    }

    [Fact]
    public void RestartRestoresTheTraceOfAGameWithDetectedKernelAntiCheat()
    {
        DetectAntiCheat("kernel");
        TraceSetAsideByAnOlderBuild(out string trace, withBootPlan: true);

        Tick();

        Assert.Contains(trace, _w.Files);
        Assert.DoesNotContain(trace + ".stale", _w.Files);
    }
}

/// 등록값이 없는 게임의 폴더 탐지가 ETW 감시기 선택에 반영되는지 (탐지는 주입, 실제 폴더를 보지 않음).
public sealed class EffectiveWatcherDetectionTests
{
    private static AsterSettings Settings(string watcher) => new(250, [Games.Make("g", "g.exe")], watcher);

    [Theory]
    [InlineData("kernel", "poll")]
    [InlineData("eac", "poll")]
    [InlineData(AutoEnv.AntiCheatUnknown, "poll")] // 탐지 실패도 트레이스 금지 판정과 같아 ETW 를 막음
    [InlineData("ricochet", "etw")] // 리코셰는 트레이스 허용 (실측)
    [InlineData(null, "etw")]
    public void DetectedAntiCheatForcesPollingOnlyWhenItForbidsTracing(string? kind, string expected) =>
        Assert.Equal(expected, Worker.EffectiveWatcher(Settings("etw"), _ => kind));

    [Fact]
    public void DetectionIsNotAskedWhenTheWatcherIsAlreadyPoll() =>
        Assert.Equal("poll", Worker.EffectiveWatcher(Settings("poll"), _ => throw new InvalidOperationException("detect")));

    [Fact]
    public void RegisteredValueWinsWithoutDetection() =>
        Assert.Equal("poll", Worker.EffectiveWatcher(new AsterSettings(250, [Games.Make("g", "g.exe") with { AntiCheat = "eac" }], "etw"), _ => throw new InvalidOperationException("detect")));
}

/// 프로필이 없다는 건너뜀 문구: 등록값 없이 폴더 탐지로만 안티치트가 보이는 게임도 "trace needed" 를 붙이지 않음.
public sealed class ProfileSkipAntiCheatTests
{
    private static PrewarmResult Run(string? detected)
    {
        var p = new Prewarmer([Games.Make("g", "g.exe")], new FakeStreamer(), new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            PhysicalRamBytes = () => 64L << 30,
            GameRunning = _ => false,
            ZoneBytes = _ => null,
            TraceExists = _ => false,
            RawTraceExists = _ => false,
            DiskBytesPerSecond = _ => 0,
            ColdPlanner = (_, _, _, _) => ColdPlan.None(FolderWarm.NoEngineProfile),
            AntiCheatOf = _ => detected,
        }, new ListLog(), null, () => DateTimeOffset.UnixEpoch);
        p.SetAuto(AutoSettings.Default);
        return p.Run(Games.Make("g", "g.exe"), "t");
    }

    [Theory]
    [InlineData(null, "no profile; trace needed")]
    [InlineData("ricochet", "no profile; trace needed")]
    [InlineData("kernel", "no profile")]
    public void SkipReasonFollowsTheDetectedAntiCheat(string? detected, string expected) => Assert.Equal(expected, Run(detected).SkipReason);
}
