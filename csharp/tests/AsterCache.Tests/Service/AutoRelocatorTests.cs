using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Tests.Core;
using AsterCache.Tracer;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class IdleTrackerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Need = TimeSpan.FromMinutes(3);

    [Fact]
    public void IdleOnlyAfterTheFullTimeWithoutInterruption()
    {
        var t = new IdleTracker();

        Assert.False(t.Sample(T0, true, Need));
        Assert.False(t.Sample(T0 + TimeSpan.FromMinutes(2), true, Need));
        Assert.True(t.Sample(T0 + Need, true, Need));
        Assert.True(t.Sample(T0 + Need + TimeSpan.FromSeconds(30), true, Need));
    }

    [Fact]
    public void AnyBusySampleStartsOver()
    {
        var t = new IdleTracker();
        t.Sample(T0, true, Need);
        t.Sample(T0 + TimeSpan.FromMinutes(2), true, Need);

        Assert.False(t.Sample(T0 + TimeSpan.FromMinutes(2.5), false, Need));
        Assert.False(t.Sample(T0 + TimeSpan.FromMinutes(3), true, Need)); // 여기서부터 다시 3 분
        Assert.False(t.Sample(T0 + TimeSpan.FromMinutes(5.9), true, Need));
        Assert.True(t.Sample(T0 + TimeSpan.FromMinutes(6), true, Need));
    }

    [Fact]
    public void ResetForgetsTheStreak()
    {
        var t = new IdleTracker();
        t.Sample(T0, true, Need);
        t.Reset();

        Assert.False(t.Sample(T0 + Need, true, Need));
    }
}

/// 자동 계획·첫 재배치의 상태 기계. 가짜 볼륨과 실제 임시 파일, 가짜 시계·디스크·CPU·전원을 씁니다.
public sealed partial class AutoRelocatorTests : ApplyTestBase
{
    private static readonly TimeSpan Step = Worker.CheckTick;
    private readonly FakeWorld _w = new();
    private readonly ListLog _log = new();
    private readonly AutoStatus _status;
    private readonly ReapplyGate _gate = new();
    private GameSettings _game;
    private AsterSettings _settings;
    private AutoRelocator? _relocator;

    public AutoRelocatorTests()
    {
        string plans = Path.Combine(Root, "plans");
        Directory.CreateDirectory(plans);
        string plan = Path.Combine(plans, "bench.plan.json");
        _game = new GameSettings("bench", ["bench.exe"], plan + ".prefetch.json", plan, Root, [Root], "GAMES", 1440) { Source = "auto" };
        _settings = new AsterSettings(50, [_game]) with { Auto = AutoSettings.Default with { TraceRuns = 1, IdleMinutesSet = true, RelocateParallel = 1, RelocateParallelSet = true, PostRelocateRecovery = AutoSettings.RecoveryOff } }; // 기존 시험은 idle_minutes (3 분) 만 명시된 설정, 쓰기 뒤 회복 대기는 아래 별도 시험
        _status = new AutoStatus(Path.Combine(Root, "auto_status.json"), _log, () => _w.Now, ticks: FakeClock.Ticks(() => _w.Now));
        _w.Files.Add(AutoTracer.RawTracePath(_game));
        _w.LoadSessions = () => Sessions;
        _w.MakePlan = (sessions, origin, zoneMib) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Files[p], preserveBytes: 64 << 20);
        _w.Apply = (_, plan, options, ct) =>
        {
            ApplyCalls++;
            return Applier.Apply(plan, Vol, [Root], JournalPath, Hooks(), options, ct);
        };
        _w.SystemDrive = Letter == "Z:" ? "Y:" : "Z:"; // 시험 폴더가 있는 드라이브는 시스템 볼륨이 아닌 HDD 로 취급
        _w.JournalRows = _ => File.Exists(JournalPath) ? JournalRowCount() : 0;
        _w.Revert = (_, skipRows, ct) =>
        {
            RevertCalls++;
            LastSkipRows = skipRows;
            return File.Exists(JournalPath) ? Reverter.Revert(JournalPath, Vol, [Root], Hooks(), new ApplyOptions(Execute: true), ct, skipRows) : null;
        };
        _w.Extents = p => Vol.Ext[p];
        _w.Reserved = g => ZoneReservations.Collect(Path.GetDirectoryName(g.Plan)!, Letter, g.Plan);
        _w.ReplanResidual = (g, candidate, olds, sessions) => ResidualReplan.Run(candidate, g.Plan, JournalPath, Vol, _w.Reserved(g), Hooks(), () => _w.Now.UtcDateTime,
            sessions is null ? null : (p, path) => PlanOutput.Save(p, sessions, path), olds).Plan;
        _w.ResumeSwitch = g => PlanSwitch.Resume(g.Plan, JournalPath);
        _w.HasPendingVerification = _ =>
        {
            if (!File.Exists(JournalPath))
            {
                return false;
            }

            using var jr = new Journal(JournalPath);
            return Applier.PendingBefore(jr.Rows.ToList()).Count > 0;
        };
        _w.JournalPlanSha = g => g.Name == "bench" && File.Exists(JournalPath) ? new Journal(JournalPath).Rows.FirstOrDefault(r => Equals(r["type"], "header"))?["plan_sha"] as string : null;
    }

    private int ApplyCalls { get; set; }

    private int RevertCalls { get; set; }

    private int LastSkipRows { get; set; } = -1;

    private int JournalRowCount()
    {
        using var jr = new Journal(JournalPath);
        return jr.Rows.Count;
    }

    // 회복 추적은 같은 디스크 게임 판정이 연결돼 있어야 시계가 흐름 (연결 전에는 보호 중으로 봄): 이 시험들은 게임 없음.
    private AutoEnv WorldEnv()
    {
        AutoEnv env = _w.Env();
        env.GameRunningOnDisk = _ => false;
        return env;
    }

    private AutoRelocator Relocator => _relocator ??= new AutoRelocator(WorldEnv(), _status, _log, () => _settings);

    private AutoGameEntry Entry => _status.Get("bench")!;

    private FakeStreamer _streamer = new();
    private long _avail = 99999;

    // 가짜 스트리머를 쓰는 진짜 Prewarmer 를 붙인 재배치기 (트레이스는 막 생긴 것으로 침).
    private void UsePrewarm(IGameStreamer? streamer = null)
    {
        var prewarm = new Prewarmer(_settings.Games, streamer ?? _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = _ => _w.Running,
            ZoneBytes = _ => 1 << 20,
            TraceExists = _ => true,
        }, _log, null, () => _w.Now);
        _relocator = new AutoRelocator(WorldEnv(), _status, _log, () => _settings, prewarm);
        _w.FileTimes[AutoTracer.RawTracePath(_game)] = _w.Now - TimeSpan.FromMinutes(1);
    }

    private int Streams => _streamer.Calls.Count;

    // 예열은 따로 돌므로 틱이 돌아온 뒤 그 작업이 끝나기를 기다려 결과를 봅니다 (틱 자체가 안 막히는지는 아래 별도 시험).
    private void Tick(ReapplyGate? gate = null)
    {
        Relocator.Tick(gate ?? _gate, CancellationToken.None);
        WarmDone();
    }

    private void WarmDone() => Relocator.WaitWarmAsync().GetAwaiter().GetResult();

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    /// 풀어 줄 때까지 스트림이 끝나지 않는 가짜 (오래 걸리는 예열).
    private sealed class BlockingStreamer : IGameStreamer
    {
        private int _calls;

        public ManualResetEventSlim Started { get; } = new();

        public ManualResetEventSlim Release { get; } = new();

        public int Calls => _calls;

        public StreamStats Stream(GameSettings game)
        {
            Interlocked.Increment(ref _calls);
            Started.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
            return new StreamStats(100, 2, 0, 0.01);
        }
    }

    // 틱을 별도 작업에서 돌려 제한 시간 안에 돌아오는지 봅니다 (예열이 틱 스레드에서 돌면 풀어 주기 전까지 안 돌아옴).
    private void TickReturnsPromptly() => Assert.True(Task.Run(() => Relocator.Tick(_gate, CancellationToken.None)).Wait(Bound));

    private void SetAuto(Func<AutoSettings, AutoSettings> change) => _settings = _settings with { Auto = change(_settings.Auto) };

    // 30 초 틱을 span 만큼 돌립니다 (첫 틱은 현재 시각).
    private void Run(TimeSpan span, ReapplyGate? gate = null)
    {
        DateTimeOffset end = _w.Now + span;
        Tick(gate);
        while (_w.Now < end)
        {
            _w.Now += Step;
            Tick(gate);
        }
    }

    private void RunUntilDone(int maxTicks = 40)
    {
        for (int i = 0; i < maxTicks && Entry.Stage != AutoStage.Done; i++)
        {
            Tick();
            _w.Now += Step;
        }
    }

    [Fact]
    public void DefaultIdleWindowIsThirtySecondsAfterTheGameStopped()
    {
        _settings = _settings with { Auto = AutoSettings.Default }; // idle_minutes 를 명시하지 않은 설정
        Tick();
        Assert.Empty(Vol.Moves);

        _w.Now += TimeSpan.FromSeconds(29);
        Tick();
        Assert.Empty(Vol.Moves);

        _w.Now += TimeSpan.FromSeconds(1);
        Tick();
        Assert.NotEmpty(Vol.Moves); // 처리량이 낮은 상태가 30 초 연속
    }

    [Fact]
    public void RelocationWaitsForTheSharedPostExitHeavyWorkGrace()
    {
        _settings = _settings with { Auto = AutoSettings.Default with { TraceRuns = 1, IdleMinutes = 0, IdleMinutesSet = true, RewarmAfterExitMin = 3 } };
        var exits = new GameExitTracker(() => _w.Now);
        exits.ObserveStarted(_game);
        _ = exits.Poll([_game], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        _w.Now += GameExitTracker.Grace;
        Assert.Single(exits.Poll([_game], new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        _relocator = new AutoRelocator(WorldEnv(), _status, _log, () => _settings);
        _relocator.SetGameExitTracker(exits);

        Tick();
        _w.Now += TimeSpan.FromMinutes(3) - GameExitTracker.Grace - TimeSpan.FromSeconds(1);
        Tick();
        Assert.Empty(Vol.Moves);

        _w.Now += TimeSpan.FromSeconds(1);
        Tick();
        if (Vol.Moves.Count == 0)
        {
            _w.Now += TimeSpan.FromSeconds(1);
            Tick();
        }

        Assert.NotEmpty(Vol.Moves);
    }

    [Fact]
    public void GameRestartedDuringPostExitGraceDoesNotStartHeavyRelocationWork()
    {
        _settings = _settings with { Auto = AutoSettings.Default with { TraceRuns = 1, IdleMinutes = 0, IdleMinutesSet = true, RewarmAfterExitMin = 3 } };
        var exits = new GameExitTracker(() => _w.Now);
        exits.ObserveStarted(_game);
        _ = exits.Poll([_game], new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        _w.Now += GameExitTracker.Grace;
        Assert.Single(exits.Poll([_game], new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
        _w.Now += TimeSpan.FromSeconds(1);
        _w.Running = true;
        _w.RunningExes.Add("bench.exe");
        Assert.Empty(exits.Poll([_game], new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bench.exe" }));
        _relocator = new AutoRelocator(WorldEnv(), _status, _log, () => _settings);
        _relocator.SetGameExitTracker(exits);

        Tick();

        Assert.Empty(Vol.Moves);
        Assert.Equal(0, ApplyCalls);
        Assert.Empty(_w.PlanZones);
    }

    [Fact]
    public void GameRunningNeverCountsAsIdleNoMatterHowLongTheWindow()
    {
        _settings = _settings with { Auto = AutoSettings.Default };
        _w.Running = true;
        _w.Files.Add(_game.Plan); // 게임이 도는 중에는 계획도 새로 만들지 않으므로 계획만 미리 둠
        Tick();
        _w.Now += TimeSpan.FromMinutes(5);
        Tick();
        Assert.Empty(Vol.Moves);
    }

    [Fact]
    public void UnregisteredProcessUnderInstallRootDoesNotBlockPlanningButARegisteredExeDoes()
    {
        _w.Images[77] = Path.Combine(Root, "helper.exe");

        Tick();

        Assert.NotEmpty(_w.PlanZones);
        Assert.True(File.Exists(_game.Plan));

        _w.RunningExes.Add(_game.ExeNames[0]);
        _w.Now += Step;
        Tick();
        Assert.Empty(Vol.Moves);

        _w.RunningExes.Clear();
        RunUntilDone();
        Assert.NotEmpty(Vol.Moves);
    }

    // 동작 고정: 재배치 계획 경로는 안티치트 종류와 상관없이 막지 않습니다. Capabilities.Relocate 는 unknown·빈 문자열을 막으므로 (결과가 달라짐) 이 경로에 연결하지 않았습니다.
    [Theory]
    [InlineData("eac")]
    [InlineData("battleye")]
    [InlineData("kernel")]
    [InlineData("ricochet")]
    [InlineData("unknown")]
    [InlineData("Vanguard")]
    [InlineData("")]
    [InlineData("unrecognized-anticheat")]
    public void UnknownOrUnrecognizedAntiCheatDoesNotBlockTheGeneralPlanPath(string antiCheatKind)
    {
        _settings = _settings with { Games = [_game with { AntiCheat = antiCheatKind }] };

        Tick();

        Assert.True(_w.PlanZones.Count >= 2); // 탐색 계획과 일반 재배치 계획
        Assert.True(File.Exists(_game.Plan));
    }

    [Fact]
    public void ABlockingProcessIsLoggedOncePerRunningSpellAndAgainAfterItEndsAndReturns()
    {
        string exe = _game.ExeNames[0];
        _w.RunningExes.Add(exe);
        _w.DescribeBlockers = g => g.Name == _game.Name ? $"{exe}(77) {Path.Combine(Root, exe)}" : null;
        string expected = $"{_game.Name}: auto skipped while a game on the same disk is running; blocking processes: {_game.Name}: {exe}(77) {Path.Combine(Root, exe)}";

        for (int i = 0; i < 3; i++)
        {
            Tick();
            _w.Now += Step;
        }

        Assert.Single(_log.Lines, l => l.EndsWith(expected, StringComparison.Ordinal)); // 틱마다 반복하지 않음

        _w.RunningExes.Clear();
        Tick();
        _w.Now += Step;
        _w.RunningExes.Add(exe);
        Tick();

        Assert.Equal(2, _log.Lines.Count(l => l.EndsWith(expected, StringComparison.Ordinal))); // 사라졌다 다시 나타나면 다시 남김
    }

    // 다른 게임 하나를 추가합니다 (originRoot 로 드라이브를 정함, 트레이스가 없어 재배치 후보는 아님).
    private void AddOtherGame(string originRoot)
    {
        GameSettings other = _game with { Name = "other", ExeNames = ["other.exe"], Plan = Path.Combine(Root, "plans", "other.plan.json"), OriginRoot = originRoot };
        _settings = _settings with { Auto = AutoSettings.Default, Games = [_game, other] };
    }

    [Fact]
    public void GameRunningOnAnotherDriveDoesNotBlockRelocation()
    {
        AddOtherGame(@"Q:\Other");
        _w.RunningExes.Add("other.exe");
        Tick();
        _w.Now += TimeSpan.FromSeconds(30);
        Tick();
        Assert.NotEmpty(Vol.Moves);
    }

    [Fact]
    public void GameRunningOnTheSameDriveBlocksRelocation()
    {
        AddOtherGame(Path.Combine(Root, "other"));
        _w.RunningExes.Add("other.exe");
        Tick();
        _w.Now += TimeSpan.FromMinutes(5);
        Tick();
        Assert.Empty(Vol.Moves);
    }

    [Fact]
    public void DryRunLeaseDenialDoesNotMarkRelocationComplete()
    {
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log);
        coordinator.Yield(_game.OriginRoot, "game started", "other game");
        Relocator.SetDiskReadCoordinator(coordinator);

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Contains("dry-run cancelled", Entry.Detail, StringComparison.Ordinal);
        Assert.Equal(0, _status.RelocationsWithin(AutoRelocator.DailyWindow));
        Assert.Empty(Vol.Moves);
    }

    [Fact]
    public void ExplicitIdleMinutesStillWinsOverTheShortDefault()
    {
        SetAuto(a => a with { IdleMinutesSet = true, IdleMinutes = 2 });
        Tick();

        _w.Now += TimeSpan.FromSeconds(90);
        Tick();
        Assert.Empty(Vol.Moves);

        _w.Now += TimeSpan.FromSeconds(30);
        Tick();
        Assert.NotEmpty(Vol.Moves);
    }

    [Fact]
    public void NewTraceIsPrewarmedOnceRightAwayBeforeRelocationAndShowsPrewarmed()
    {
        UsePrewarm();

        Tick();

        Assert.Equal(1, Streams);
        Assert.Equal((AutoStage.WaitingIdle, AutoRelocator.Prewarmed), (Entry.Stage, Entry.Detail));
        Assert.Empty(Vol.Moves);

        _w.Now += Step;
        Tick();
        Assert.Equal(1, Streams); // 같은 트레이스로는 다시 예열하지 않음
        Assert.Equal((AutoStage.WaitingIdle, AutoRelocator.Prewarmed), (Entry.Stage, Entry.Detail)); // 대기 표시가 깜박이지 않음
    }

    [Theory]
    [InlineData("auto", "target")]
    [InlineData("target", "target")]
    [InlineData("source", "source")]
    public void RelocateOrderFollowsTheSettingAndIsLogged(string setting, string expected)
    {
        _settings = _settings with { Auto = _settings.Auto with { RelocateOrder = setting } };
        string? order = null;
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            order = options.Execute ? options.Order : order;
            return inner(g, plan, options, ct);
        };

        Tick();
        _w.Now += Step;
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(expected, order);
        Assert.Contains(_log.Lines, l => l.Contains("auto relocation moved", StringComparison.Ordinal) && l.Contains($"order {expected}", StringComparison.Ordinal));
    }

    [Fact]
    public void RelocationEndsWithAnImmediatePrewarmEvenInsideTheCooldown()
    {
        UsePrewarm();

        Tick();
        _w.Now += Step;
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(2, Streams); // 트레이스 직후 + 재배치 직후 (쿨다운 무시)
        Assert.Equal(AutoRelocator.Prewarmed, Entry.Detail);
        Assert.NotNull(Entry.RelocatedAt);
    }

    [Fact]
    public void FailedRelocationStillPrewarmsFromTheTrace()
    {
        UsePrewarm();
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) => options.DryRun ? inner(g, plan, options, ct) : throw new IOException("disk");

        Run(TimeSpan.FromMinutes(3.5));

        Assert.Equal(AutoStage.Failed, Entry.Stage);
        Assert.Equal(2, Streams);
    }

    [Fact]
    public void RelocationOffStillPrewarmsTheNewTrace()
    {
        SetAuto(a => a with { Relocate = false });
        UsePrewarm();

        Tick();

        Assert.Equal(AutoStage.Skipped, Entry.Stage);
        Assert.Equal(1, Streams);
        Assert.Empty(Vol.Moves);
    }

    [Fact]
    public void DailyLimitKeepsBothTheLimitNoteAndPrewarmed()
    {
        SetAuto(a => a with { MaxRelocationsPerDay = 1 });
        _status.BeginRelocation(_w.Now, 64L << 20);
        UsePrewarm();

        Tick();
        Tick();

        Assert.Equal((AutoStage.WaitingIdle, "daily limit reached, prewarmed"), (Entry.Stage, Entry.Detail));
        Assert.Equal(1, Streams);
    }

    [Fact]
    public void NewerTraceIsPrewarmedAgainButASkippedPrewarmIsNotRetriedEveryTick()
    {
        _avail = 100; // 메모리 부족: 예열이 건너뛰어짐
        UsePrewarm();
        Tick();
        _w.Now += Step;
        Tick();
        _w.Now += Step;
        Tick();

        Assert.Equal(0, Streams);
        Assert.Single(_log.Lines, l => l.Contains("prewarm skipped (low memory", StringComparison.Ordinal)); // 같은 트레이스로는 한 번만 시도

        _avail = 99999;
        _w.FileTimes[AutoTracer.RawTracePath(_game)] = _w.Now + TimeSpan.FromSeconds(5); // 더 새로운 트레이스
        _w.Now += Step;
        Tick();

        Assert.Equal(1, Streams);
    }

    [Fact]
    public void TickReturnsWhileAPrewarmIsStillStreamingAndThePrewarmFinishesInTheBackground()
    {
        var blocking = new BlockingStreamer();
        UsePrewarm(blocking);

        TickReturnsPromptly();
        Assert.True(blocking.Started.Wait(Bound)); // 스트림은 틱 밖에서 읽는 중
        _w.Now += Step;
        TickReturnsPromptly(); // 읽는 중에도 다음 틱이 막히지 않음 (같은 트레이스는 다시 요청하지 않음)
        Assert.Equal(1, blocking.Calls);
        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Null(Entry.Detail); // 아직 예열 전

        blocking.Release.Set();
        WarmDone();

        Assert.Equal((AutoStage.WaitingIdle, AutoRelocator.Prewarmed), (Entry.Stage, Entry.Detail));
        Assert.Equal(1, blocking.Calls);
    }

    [Fact]
    public void CancelWarmDropsQueuedPrewarmsAndTheCancelledTraceIsWarmedAgainOnTheNextTick()
    {
        var blocking = new BlockingStreamer();
        UsePrewarm(blocking);
        TickReturnsPromptly();
        Assert.True(blocking.Started.Wait(Bound));
        _w.FileTimes[AutoTracer.RawTracePath(_game)] = _w.Now + TimeSpan.FromSeconds(5); // 더 새로운 트레이스: 읽는 동안 두 번째 예열이 줄에 섬
        _w.Now += Step;
        TickReturnsPromptly();

        Relocator.CancelWarm(); // 게임 시작
        blocking.Release.Set();
        WarmDone();

        Assert.Equal(1, blocking.Calls); // 줄 서 있던 예열은 시작하지 않음

        _w.Now += Step;
        Tick();

        Assert.Equal(2, blocking.Calls); // 취소된 트레이스는 게임이 끝난 뒤 다시 예열
    }

    [Fact]
    public void WarmTokenIsCancelledByCancelWarmAndReplacedByAFreshOne()
    {
        CancellationToken before = Relocator.WarmToken;

        Relocator.CancelWarm();

        Assert.True(before.IsCancellationRequested);
        Assert.False(Relocator.WarmToken.IsCancellationRequested);
    }

    [Fact]
    public void NoPrewarmWhileTheGameIsRunning()
    {
        UsePrewarm();
        Tick(); // 계획 만들기 + 첫 예열
        int before = Streams;
        _w.FileTimes[AutoTracer.RawTracePath(_game)] = _w.Now + TimeSpan.FromSeconds(5);
        _w.Running = true;
        _w.Now += Step;
        Tick();

        Assert.Equal(before, Streams);
    }

    [Fact]
    public void WholeFlowPlansWaitsForIdleMovesAndRecordsTheFirstRelocation()
    {
        ReportFullMoves();
        DateTimeOffset t0 = _w.Now;
        Tick();

        Assert.True(File.Exists(_game.Plan));
        Assert.True(File.Exists(_game.Plan + ".prefetch.json"));
        Assert.Equal(new long[] { ZoneSize.ProbeMib, 16 }, _w.PlanZones); // 탐색 존으로 핫 바이트를 재고, 그 1.1 배를 16 MiB 단위로 올린 존으로 다시 계획
        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Empty(Vol.Moves);

        Run(TimeSpan.FromMinutes(2.5));
        Assert.Empty(Vol.Moves); // 3 분 연속 유휴가 아직 아님

        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(t0 + TimeSpan.FromMinutes(3), Entry.RelocatedAt); // 첫 유휴 표본에서 정확히 3 분 뒤
        Assert.NotEmpty(Vol.Moves);
        Assert.Equal(1, _status.RelocationsWithin(AutoRelocator.DailyWindow));
        Assert.Equal(["relocated"], _status.Events.Select(e => e.Kind));
        Assert.True(File.Exists(JournalPath));
        PlanFile saved = PlanFile.Load(_game.Plan);
        Assert.NotNull(saved.Zone.StartLcn); // 존 시작이 계획에 저장됨 (이어하기용)
        Assert.True(Verify(saved).Contiguous);

        int calls = ApplyCalls;
        Run(TimeSpan.FromMinutes(10));
        Assert.Equal(calls, ApplyCalls); // 게임당 자동 첫 재배치는 한 번
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("power")]
    [InlineData("game")]
    public void AnyBusyConditionRestartsTheIdleWindow(string busy)
    {
        Run(TimeSpan.FromMinutes(2.5));
        Assert.Empty(Vol.Moves);

        switch (busy)
        {
            case "disk": _w.Disk = DiskActivityBytes() + 1; break;
            case "power": _w.Power = false; break;
            default: _w.Running = true; break;
        }

        _w.Now += Step;
        Tick();
        _w.Disk = 0;
        _w.Cpu = 1;
        _w.Power = true;
        _w.Running = false;

        Run(TimeSpan.FromMinutes(2.5)); // 합치면 3 분을 넘지만 연속이 아님
        Assert.Empty(Vol.Moves);
        Assert.Equal(0, ApplyCalls);

        Run(TimeSpan.FromMinutes(1));
        Assert.NotEmpty(Vol.Moves);
    }

    private static double DiskActivityBytes() => DiskActivity.BusyBytesPerSecond;

    [Fact]
    public void DiskExactlyBelowTheBusyLineCountsAsQuiet()
    {
        _w.Disk = DiskActivityBytes() - 1;

        Run(TimeSpan.FromMinutes(3));

        Assert.NotEmpty(Vol.Moves);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(87.0)]
    public void HighOrUnknownCpuDoesNotBlockRelocationWhenTheDiskIsQuiet(double? cpu)
    {
        SetAuto(_ => AutoSettings.Default); // 유휴 창 30 초
        _w.Cpu = cpu;
        DateTimeOffset t0 = _w.Now;

        Run(TimeSpan.FromMinutes(1));

        Assert.Equal(t0 + TimeSpan.FromSeconds(30), Entry.RelocatedAt); // CPU 가 바빠도 디스크가 조용하면 첫 유휴 표본에서 30 초 뒤
        Assert.NotEmpty(Vol.Moves);
    }

    [Fact]
    public void LongWaitForIdleIsNotedOnceWithTheReasonAndCountedAgainAfterIdle()
    {
        _w.Disk = DiskActivityBytes() + 1;

        Run(TimeSpan.FromHours(6.5));

        Assert.Single(_log.Lines, l => l.Contains("auto waiting for idle for 6 h (disk busy)", StringComparison.Ordinal));
        Assert.Empty(Vol.Moves);

        _w.Disk = 0;
        Run(TimeSpan.FromMinutes(1)); // 유휴 표본이 잡혀 대기 구간이 끝남 (창 3 분은 아직 안 참)
        _w.Disk = DiskActivityBytes() + 1;
        Run(TimeSpan.FromHours(6.5));

        Assert.Equal(2, _log.Lines.Count(l => l.Contains("auto waiting for idle for 6 h (disk busy)", StringComparison.Ordinal)));
    }

    [Fact]
    public void DailyLimitHoldsTheNextRelocationUntilThe24HourWindowPasses()
    {
        SetAuto(a => a with { MaxRelocationsPerDay = 1 });
        _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1), 64L << 20);

        Run(TimeSpan.FromMinutes(10));
        Assert.Equal((AutoStage.WaitingIdle, "daily limit reached"), (Entry.Stage, Entry.Detail));
        Assert.Empty(Vol.Moves);

        _w.Now += TimeSpan.FromHours(24);
        Run(TimeSpan.FromMinutes(3));
        Assert.Equal(AutoStage.Done, Entry.Stage);
    }

    [Fact]
    public void UnreadableStatusFileHoldsRelocationAndTheReapplyGateFor24HoursThenReleases()
    {
        string path = Path.Combine(Root, "bad_status.json");
        File.WriteAllText(path, string.Empty);
        var status = new AutoStatus(path, _log, () => _w.Now, ticks: FakeClock.Ticks(() => _w.Now));
        _relocator = new AutoRelocator(WorldEnv(), status, _log, () => _settings);

        Assert.Equal(ReapplyVerdict.DailyLimit, GateAfterIdle());
        Run(TimeSpan.FromMinutes(10));
        Assert.Equal(AutoStage.WaitingIdle, status.Get("bench")!.Stage);
        Assert.Contains("status file was unreadable; relocation held until", status.Get("bench")!.Detail, StringComparison.Ordinal);
        Assert.Empty(Vol.Moves);
        Assert.Equal(0L, status.RelocationBytesWithin(AutoRelocator.DailyWindow)); // 실제 이동 바이트 집계는 건드리지 않음

        _w.Now += TimeSpan.FromHours(24);
        status.Complete(_game.Name, _game.Source, _w.Now, counted: false, "done"); // 자동 상태 항목이 있으면 재배치를 끝낸 게임만 게이트를 지남
        Assert.Null(GateAfterIdle());
    }

    [Fact]
    public void DailyCountLimitOf4AllowsTheFourthRelocation()
    {
        ReportFullMoves();
        for (int i = 0; i < 3; i++)
        {
            _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1 + i), 64L << 20);
        }

        Run(TimeSpan.FromMinutes(10));

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(4, _status.RelocationsWithin(AutoRelocator.DailyWindow));
    }

    [Fact]
    public void DailyCountLimitOf4HoldsTheFifthRelocation()
    {
        for (int i = 0; i < 4; i++)
        {
            _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1 + i), 64L << 20);
        }

        Run(TimeSpan.FromMinutes(10));

        Assert.Equal((AutoStage.WaitingIdle, "daily limit reached"), (Entry.Stage, Entry.Detail));
        Assert.Empty(Vol.Moves);
    }

    // 가짜 볼륨의 실제 이동 양은 상한(64 MiB) 보다 작아 횟수로 세지 않으므로, 실제 적용의 보고만 상한 이상으로 올립니다.
    private void ReportFullMoves()
    {
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            ApplyReport r = real(g, plan, options, ct);
            if (!options.DryRun)
            {
                r.MovedBytes = Math.Max(r.MovedBytes, AutoStatus.CountedRelocationMinBytes);
            }

            return r;
        };
    }

    // 예행이 말하는 이동 예정 바이트를 bytes 로 바꾸고, 실행 때 받은 옵션을 모아 돌려줍니다.
    private List<ApplyOptions> FakeBytesTodo(long bytes)
    {
        var executed = new List<ApplyOptions>();
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            ApplyReport r = real(g, plan, options, ct);
            if (options.DryRun)
            {
                r.BytesTodo = bytes;
            }
            else
            {
                executed.Add(options);
            }

            return r;
        };
        return executed;
    }

    [Fact]
    public void DailyWriteBudgetAllowsASumExactlyAtTheLimit()
    {
        List<ApplyOptions> executed = FakeBytesTodo(2L << 30);
        _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1), 6L << 30);

        Run(TimeSpan.FromMinutes(10));

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Single(executed);
        Assert.InRange(_status.RelocationBytesWithin(AutoRelocator.DailyWindow), 6L << 30, 8L << 30); // 앞 기록 6 GiB + 이번 실행의 실제 이동 바이트 (시험 볼륨에서는 예정보다 작음)
    }

    [Fact]
    public void DailyWriteBudgetHoldsOnceTheLimitIsUsedUpAndReleasesAfter24Hours()
    {
        List<ApplyOptions> executed = FakeBytesTodo(2L << 30);
        _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1), 8L << 30);

        Run(TimeSpan.FromMinutes(10));

        Assert.Equal((AutoStage.WaitingIdle, "daily write budget reached"), (Entry.Stage, Entry.Detail));
        Assert.Empty(executed);
        Assert.Empty(Vol.Moves);
        Assert.Equal(1, _status.RelocationsWithin(AutoRelocator.DailyWindow)); // 보류는 횟수를 늘리지 않음
        int applyCalls = ApplyCalls;
        Run(TimeSpan.FromMinutes(10));
        Assert.Equal(applyCalls, ApplyCalls); // 이동량을 알고 보류 중이면 예행도 다시 하지 않음

        _w.Now += TimeSpan.FromHours(24);
        Run(TimeSpan.FromMinutes(3));
        Assert.Equal(AutoStage.Done, Entry.Stage);
    }

    [Fact]
    public void DailyWriteBudgetSmallerThanOneRunCapsTheWriteCapAtTheBudgetInsteadOfAllowingAFirstMove()
    {
        SetAuto(a => a with { MaxRelocateGibPerDay = 1 });
        List<ApplyOptions> executed = FakeBytesTodo(2L << 30);

        Run(TimeSpan.FromMinutes(10));

        Assert.Equal(1L << 30, Assert.Single(executed).WriteCap); // 상한(1 GiB)보다 큰 2 GiB 도 1 GiB 까지만 쓰고 나머지는 이어하기
        Assert.Equal(AutoStage.Done, Entry.Stage);
    }

    [Fact]
    public void WriteCapIsTheSmallerOfTheRunCapAndWhatIsLeftOfTheDay()
    {
        List<ApplyOptions> executed = FakeBytesTodo(2L << 30);
        _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1), (8L << 30) - (300L << 20)); // 하루 8 GiB 중 300 MiB 남음

        Run(TimeSpan.FromMinutes(10));

        Assert.Equal(300L << 20, Assert.Single(executed).WriteCap);
    }

    [Theory]
    [InlineData(false, 8, 2)] // 비 SMR: 일일 8 GiB, 회당 2 GiB
    [InlineData(true, 2, 1)] // SMR: 일일 2 GiB, 회당 1 GiB
    public void SmrDisksLowerTheDefaultDailyBudgetAndRunCap(bool smr, int dailyGib, int runGib)
    {
        _w.IsSmr = _ => smr;
        List<ApplyOptions> executed = FakeBytesTodo(4L << 30);
        _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1), ((long)dailyGib << 30) - (3L << 30 >> 1)); // 하루 상한 중 1.5 GiB 남음

        Run(TimeSpan.FromMinutes(10));

        Assert.Equal(Math.Min((long)runGib << 30, 3L << 30 >> 1), Assert.Single(executed).WriteCap);
    }

    [Fact]
    public void ExplicitDailyBudgetIsKeptOnSmrDisks()
    {
        _w.IsSmr = _ => true;
        SetAuto(a => a with { MaxRelocateGibPerDay = 8, MaxRelocateGibPerDaySet = true });
        List<ApplyOptions> executed = FakeBytesTodo(4L << 30);
        _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1), 3L << 30); // 기본값 (2 GiB) 이었다면 이미 넘음

        Run(TimeSpan.FromMinutes(10));

        Assert.Equal(1L << 30, Assert.Single(executed).WriteCap); // 회당 상한만 SMR 값
    }

    [Fact]
    public void ZeroDailyWriteBudgetMeansUnlimited()
    {
        SetAuto(a => a with { MaxRelocateGibPerDay = 0 });
        _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1), 500L << 30);

        Run(TimeSpan.FromMinutes(10));

        Assert.Equal(AutoStage.Done, Entry.Stage);
    }

    [Fact]
    public void RelocationBytesRecordedForTheBudgetAreTheBytesActuallyMovedNotTheEstimate()
    {
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            ApplyReport r = real(g, plan, options, ct);
            if (options.DryRun)
            {
                r.BytesTodo = 3L << 30; // 예정은 3 GiB
            }
            else
            {
                r.MovedBytes = 1500L << 20; // 실제로 옮긴 양
            }

            return r;
        };

        Run(TimeSpan.FromMinutes(10));

        Assert.Equal(1500L << 20, _status.RelocationBytesWithin(AutoRelocator.DailyWindow));
    }

    [Theory]
    [InlineData("low", true, 32)]
    [InlineData("normal", false, 1)]
    public void RelocateChunkSizeAndIoPriorityReachTheApplier(string priority, bool low, int mib)
    {
        List<ApplyOptions> executed = FakeBytesTodo(1 << 20);
        SetAuto(a => a with { RelocateChunkMib = mib, RelocateIoPriority = priority });

        Run(TimeSpan.FromMinutes(10));

        ApplyOptions o = Assert.Single(executed);
        Assert.Equal(((long)mib << 20, low), (o.ChunkBytes, o.LowIoPriority));
    }

    [Fact]
    public void DefaultRelocateOptionsAre4MibAtLowPriority()
    {
        List<ApplyOptions> executed = FakeBytesTodo(1 << 20);

        Run(TimeSpan.FromMinutes(10));

        ApplyOptions o = Assert.Single(executed);
        Assert.Equal((4L << 20, true), (o.ChunkBytes, o.LowIoPriority));
    }

    [Theory]
    [InlineData(false, 2)]
    [InlineData(true, 0)]
    public void RelocateOffOrZeroPerDayOnlyMakesThePlanAndLaterEnablingMovesIt(bool relocateFlag, int perDay)
    {
        SetAuto(a => a with { Relocate = relocateFlag, MaxRelocationsPerDay = perDay });

        Run(TimeSpan.FromMinutes(10));

        Assert.True(File.Exists(_game.Plan));
        Assert.Equal((AutoStage.Skipped, "auto relocate off"), (Entry.Stage, Entry.Detail));
        Assert.Empty(Vol.Moves);
        Assert.Equal(new long[] { ZoneSize.ProbeMib, 16 }, _w.PlanZones); // 계획은 한 번만

        SetAuto(_ => AutoSettings.Default);
        Run(TimeSpan.FromMinutes(3));

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(2, _w.PlanZones.Count);
    }

    [Fact]
    public void NoTraceMeansNothingHappens()
    {
        _w.Files.Clear();

        Run(TimeSpan.FromMinutes(10));

        Assert.Null(_status.Get("bench"));
        Assert.Equal(0, ApplyCalls);
    }

    [Fact]
    public void ZoneIsCappedAtMaxZoneMib()
    {
        _settings = _settings with { MaxZoneMib = 8 };

        Tick();

        Assert.Equal(new long[] { ZoneSize.ProbeMib, 8 }, _w.PlanZones);
    }

    [Fact]
    public void AutoZoneLimitReplacesTheNumericOneAndFollowsPhysicalRam()
    {
        _settings = _settings with { MaxZoneMib = 8, MaxZoneAuto = true };
        _w.PhysicalRam = 4L << 30; // 자동 상한 = 2048 MiB (RAM 의 1/4, 최소 2048)

        Tick();

        Assert.Equal(new long[] { ZoneSize.ProbeMib, 16 }, _w.PlanZones); // 숫자 상한 8 은 무시됨
        Assert.Equal(2048, ZoneSize.AutoMaxMib(_w.PhysicalRam));
    }

    [Fact]
    public void EmptyHotSetFailsWithoutMovingAndIsNotRetried()
    {
        _w.LoadSessions = () => [[]];

        Run(TimeSpan.FromMinutes(5));

        Assert.Equal((AutoStage.Failed, "no hot data"), (Entry.Stage, Entry.Detail));
        Assert.False(File.Exists(_game.Plan));
        Assert.Single(_w.PlanZones); // 탐색 계획 한 번뿐
        Assert.Equal(0, ApplyCalls);
    }

    [Fact]
    public void PlanFailureIsRecordedAsFailed()
    {
        _w.MakePlan = (_, _, _) => throw new IOException("disk");

        Tick();

        Assert.Equal((AutoStage.Failed, "plan failed: IOException"), (Entry.Stage, Entry.Detail));
        Assert.Contains(_log.Lines, l => l.StartsWith("Error: bench: auto relocation stopped", StringComparison.Ordinal));
    }

    [Fact]
    public void GameStartDuringTheMoveCancelsItAndTheNextIdleWindowResumesFromTheJournal()
    {
        ReportFullMoves();
        Vol.AfterMove = n =>
        {
            if (n == 1)
            {
                _gate.Cancel(); // OnStart 가 부르는 것과 같은 취소
            }
        };

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal((AutoStage.WaitingIdle, "cancelled by game start"), (Entry.Stage, Entry.Detail));
        Assert.Null(Entry.RelocatedAt);
        Assert.Equal(1, _status.RelocationsWithin(AutoRelocator.DailyWindow)); // 하루 상한은 시작 기준: 취소된 시도도 셈
        Assert.Single(Vol.Moves);
        Assert.True(File.Exists(JournalPath));
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("Error", StringComparison.Ordinal));

        Vol.AfterMove = null;
        Run(TimeSpan.FromMinutes(3.5)); // 유휴를 처음부터 다시 기다림

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.True(Verify(PlanFile.Load(_game.Plan)).Contiguous);
        Assert.Equal(2, _status.RelocationsWithin(AutoRelocator.DailyWindow)); // 이어하기도 한 번 더 시작으로 셈, 끝날 때 또 세지 않음
        Assert.Equal(["relocated"], _status.Events.Select(e => e.Kind));
    }

    // 이동 실행은 마지막 조각을 옮긴 직후 양보되고 (미룬 검증이 저널에 남음), 검증 전용 실행 (이동할 것 없음) 은 deferVerification 이 true 인 동안 양보됨.
    private void DeferAfterTheLastMove(Func<bool> deferVerification, Func<bool>? mismatch = null)
    {
        int executes = 0;
        _w.Apply = (_, plan, options, ct) =>
        {
            ApplyOptions run = options;
            if (options.Execute && ++executes == 1)
            {
                run = options with { CanIssue = () => !Verify(PlanFile.Load(_game.Plan)).Contiguous };
            }
            else if (options.Execute && deferVerification())
            {
                run = options with { CanIssue = () => false };
            }

            string Digest(string path, long size, IReadOnlyList<(long Offset, long Length)> extra) => mismatch?.Invoke() == true ? "changed" : SampleHasher.Compute(path, size, extra, null);
            return Applier.Apply(plan, Vol, [Root], JournalPath, Hooks(Digest), run, ct);
        };
    }

    [Fact]
    public void ShaVerificationDeferredAfterTheLastMoveRunsOnTheNextTickBeforeCompletion()
    {
        bool defer = true;
        DeferAfterTheLastMove(() => defer);

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal((AutoStage.WaitingIdle, "cancelled by game start"), (Entry.Stage, Entry.Detail));
        Assert.True(Verify(PlanFile.Load(_game.Plan)).Contiguous); // 이동은 끝남
        using (var jr = new Journal(JournalPath))
        {
            Assert.NotEmpty(Applier.PendingBefore(jr.Rows.ToList())); // 미룬 검증이 남음
        }

        Run(TimeSpan.FromMinutes(3.5)); // 검증 전용 실행도 양보되면 또 미룸: 완료로 확정하지 않음

        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Contains(_log.Lines, l => l.Contains("verification deferred", StringComparison.Ordinal));
        Assert.Null(Entry.RelocatedAt);

        defer = false;
        Run(TimeSpan.FromMinutes(3.5));

        Assert.Equal((AutoStage.Done, "already in place"), (Entry.Stage, Entry.Detail));
        using var done = new Journal(JournalPath);
        Assert.Empty(Applier.PendingBefore(done.Rows.ToList()));
        Assert.DoesNotContain(done.Rows, r => Equals(r["type"], "end") && r.TryGetValue("sha_mismatch", out object? bad) && bad is System.Collections.IList { Count: > 0 });
    }

    [Fact]
    public void VerificationRunReadsThePlanFileAsItIsNow()
    {
        bool defer = true;
        DeferAfterTheLastMove(() => defer);
        Run(TimeSpan.FromMinutes(3));
        Assert.Equal((AutoStage.WaitingIdle, "cancelled by game start"), (Entry.Stage, Entry.Detail));

        bool rewrite = false;
        bool sawNewPlan = false;
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            if (options.DryRun && rewrite) // 이 틱의 dry-run 뒤에만 계획 파일을 바꿈 (Sha 는 그대로): 검증 실행은 바뀐 파일을 봐야 함
            {
                rewrite = false;
                PlanFile now = PlanFile.Load(_game.Plan);
                var stats = new JObject();
                foreach ((string key, object? value) in now.Stats)
                {
                    stats.Add(key, value);
                }

                stats["verify_marker"] = 1L;
                (now with { Stats = stats }).Save(_game.Plan);
            }

            sawNewPlan |= options.Execute && plan.Stats.ContainsKey("verify_marker");
            return inner(g, plan, options, ct);
        };

        rewrite = true;
        Run(TimeSpan.FromMinutes(3.5));

        Assert.True(sawNewPlan);
        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
    }

    [Fact]
    public void VerificationRunThatMovesBytesDoesNotCompleteUntilTheNextTick()
    {
        bool defer = true;
        DeferAfterTheLastMove(() => defer);
        Run(TimeSpan.FromMinutes(3));
        Assert.Equal((AutoStage.WaitingIdle, "cancelled by game start"), (Entry.Stage, Entry.Detail));

        defer = false;
        bool armed = true;
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            ApplyReport r = inner(g, plan, options, ct);
            if (armed && options.Execute)
            {
                r.MovedBytes = 1; // 검증 전용 실행이 무언가를 옮긴 것처럼 보이게 함
            }

            return r;
        };

        Run(TimeSpan.FromMinutes(3.5));

        Assert.NotEqual(AutoStage.Done, Entry.Stage); // 완료로 확정하지 않음
        Assert.Null(Entry.RelocatedAt);

        armed = false;
        Run(TimeSpan.FromMinutes(3.5));

        Assert.Equal((AutoStage.Done, "already in place"), (Entry.Stage, Entry.Detail));
    }

    [Fact]
    public void VerificationReadIOExceptionHoldsTransientlyInsteadOfFailing()
    {
        bool defer = true;
        DeferAfterTheLastMove(() => defer);
        Run(TimeSpan.FromMinutes(3));
        Assert.Equal((AutoStage.WaitingIdle, "cancelled by game start"), (Entry.Stage, Entry.Detail));

        defer = false;
        bool failVerify = true;
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) => options.Execute && failVerify ? throw new IOException("locked") : inner(g, plan, options, ct);

        Run(TimeSpan.FromMinutes(3.5));

        Assert.NotEqual(AutoStage.Failed, Entry.Stage);
        Assert.StartsWith("temporary IOException", Entry.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("relocation failed", StringComparison.Ordinal));
        Assert.Null(Entry.RelocatedAt);

        failVerify = false;
        Run(TimeSpan.FromMinutes(10));

        Assert.Equal((AutoStage.Done, "already in place"), (Entry.Stage, Entry.Detail));
    }

    [Fact]
    public void ShaVerificationRunLaterThatMismatchesTakesTheDataCheckFailurePath()
    {
        bool mismatch = false;
        bool defer = false;
        DeferAfterTheLastMove(() => defer, () => mismatch);
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            ApplyReport r = inner(g, plan, options, ct);
            mismatch |= options.Execute; // 첫 이동 실행이 끝난 뒤의 표본은 달라짐
            return r;
        };

        Run(TimeSpan.FromMinutes(3));
        Assert.Equal("cancelled by game start", Entry.Detail);
        Run(TimeSpan.FromMinutes(3.5));

        Assert.Equal(AutoStage.Failed, Entry.Stage);
        Assert.Contains(_log.Lines, l => l.Contains("data check failed", StringComparison.Ordinal));
        Assert.Null(Entry.RelocatedAt);
    }

    [Fact]
    public void RegisteredExeStartingDuringTheMoveWithoutAStartNoticeStopsItAtTheNextPieceAndTheJournalResumes()
    {
        ReportFullMoves();
        using var observed = new ManualResetEventSlim();
        bool seen = false;
        Vol.AfterMove = n =>
        {
            if (n == 1)
            {
                _w.RunningExes.Add(_game.ExeNames[0]); // 등록 exe 가 떴지만 시작 알림은 오지 않음
                int moveThread = Environment.CurrentManagedThreadId;
                int polls = 0;
                _w.OnAnyRunning = _ =>
                {
                    if (Environment.CurrentManagedThreadId != moveThread && Interlocked.Increment(ref polls) >= 2) // 점검 타이머의 첫 판정이 취소를 건 뒤의 다음 판정까지 기다림
                    {
                        observed.Set();
                    }
                };
                seen = observed.Wait(TimeSpan.FromSeconds(5));
            }
        };

        Run(TimeSpan.FromMinutes(3), new ReapplyGate { RunningPoll = TimeSpan.FromMilliseconds(10) });

        Assert.True(seen);
        Assert.Equal((AutoStage.WaitingIdle, "cancelled by game start"), (Entry.Stage, Entry.Detail));
        Assert.Null(Entry.RelocatedAt);
        Assert.Single(Vol.Moves);
        Assert.True(File.Exists(JournalPath));

        Vol.AfterMove = null;
        _w.OnAnyRunning = null;
        _w.RunningExes.Clear();
        Run(TimeSpan.FromMinutes(3.5)); // 프로세스가 끝난 뒤 저널에서 이어 옮김

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.True(Verify(PlanFile.Load(_game.Plan)).Contiguous);
    }

    [Fact]
    public void GameAlreadyRunningWhenTheMoveWouldStartDoesNotMoveAnything()
    {
        Run(TimeSpan.FromMinutes(2.5));
        _w.Apply = (_, _, _, _) =>
        {
            ApplyCalls++;
            throw new InvalidOperationException("must not apply");
        };

        _w.Running = true;
        Run(TimeSpan.FromMinutes(5));

        Assert.Equal(0, ApplyCalls);
    }

    [Fact]
    public void DataCheckFailureRevertsTheJournalAndMarksFailed()
    {
        var calls = new List<string>();
        _w.Apply = (_, plan, options, ct) =>
        {
            ApplyCalls++;
            string Digest(string path, long size, IReadOnlyList<(long Offset, long Length)> extra)
            {
                calls.Add(path);
                return calls.Count <= 2 ? "a" : "b"; // 이동 뒤의 표본이 달라짐
            }

            return Applier.Apply(plan, Vol, [Root], JournalPath, Hooks(Digest), options, ct);
        };
        Dictionary<string, List<Extent>> original = Files.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal);

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal((AutoStage.Failed, "data check failed"), (Entry.Stage, Entry.Detail));
        Assert.Null(Entry.RelocatedAt);
        Assert.Equal(1, RevertCalls);
        Assert.True(SameExtents(original, Vol.Ext)); // 원래 배치로 되돌아감
        Assert.Contains(_log.Lines, l => l.StartsWith("Error: bench: auto relocation stopped: data check failed", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("reverted", StringComparison.Ordinal));

        int applied = ApplyCalls;
        Run(TimeSpan.FromMinutes(10));
        Assert.Equal(applied, ApplyCalls); // 실패한 게임은 다시 시도하지 않음
    }

    [Fact]
    public void FailureWhileAGameIsRunningKeepsTheJournalInsteadOfReverting()
    {
        var calls = new List<string>();
        _w.Apply = (_, plan, options, ct) =>
        {
            string Digest(string path, long size, IReadOnlyList<(long Offset, long Length)> extra)
            {
                calls.Add(path);
                if (calls.Count == 3)
                {
                    _w.Running = true; // 이동이 끝난 뒤 게임이 뜸
                }

                return calls.Count <= 2 ? "a" : "b";
            }

            return Applier.Apply(plan, Vol, [Root], JournalPath, Hooks(Digest), options, ct);
        };

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal(AutoStage.Failed, Entry.Stage);
        Assert.Equal(0, RevertCalls);
        Assert.True(File.Exists(JournalPath));
        Assert.Contains(_log.Lines, l => l.Contains("revert skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void RevertDeniedByTheDiskLeaseIsLoggedAndKeepsTheJournalForRetry()
    {
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log);
        Relocator.SetDiskReadCoordinator(coordinator);
        var calls = new List<string>();
        _w.Apply = (_, plan, options, ct) =>
        {
            string Digest(string path, long size, IReadOnlyList<(long Offset, long Length)> extra)
            {
                calls.Add(path);
                return calls.Count <= 2 ? "a" : "b";
            }

            try
            {
                return Applier.Apply(plan, Vol, [Root], JournalPath, Hooks(Digest), options, ct);
            }
            catch (ShaMismatchException)
            {
                coordinator.Yield(_game.OriginRoot, "game started", "other game"); // 데이터 검사 실패 직후 같은 디스크에서 다른 게임이 뜸
                throw;
            }
        };

        Run(TimeSpan.FromMinutes(3));

        Assert.Contains(_log.Lines, l => l.Contains("auto relocation stopped: data check failed", StringComparison.Ordinal)); // 이후 틱은 양보 중이라 단계가 대기로 바뀌므로 실패 로그로 확인
        Assert.Equal(0, RevertCalls);
        Assert.True(File.Exists(JournalPath));
        Assert.Contains(_log.Lines, l => l.Contains("revert deferred", StringComparison.Ordinal));
    }

    [Fact]
    public void OccupiedZoneStopSetsThePlanAsideAndPlansAgainOnTheNextTicks()
    {
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        int busy = 0;
        _w.MakePlanAt = (sessions, origin, zoneMib, lcn, used) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], zoneStartLcn: lcn, preserveBytes: 64 << 20, fixedClusters: used);
        _w.ArchiveJournal = _ => null; // 가짜 적용은 저널을 쓰지 않음
        _w.Apply = (g, plan, options, ct) => options.Execute && busy++ == 0 ? new ApplyReport { Executed = true, Stopped = Applier.ZoneOccupied } : real(g, plan, options, ct);

        Run(TimeSpan.FromMinutes(3));
        RunUntilDone();

        Assert.Equal(2, busy); // 한 번 막혀 멈춘 뒤 다시 계획해 끝까지 감
        Assert.True(File.Exists(_game.Plan + ".stale"));
        Assert.Contains(_log.Lines, l => l.Contains("zone region appears occupied", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("stopped: " + Applier.ZoneOccupied, StringComparison.Ordinal) && l.Contains("(open ", StringComparison.Ordinal));
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.True(Verify(PlanFile.Load(_game.Plan)).Contiguous);
    }

    [Fact]
    public void RejectedPlanFailsBeforeAnyMoveAndHasNothingOfItsOwnToRevert()
    {
        _w.Apply = (_, _, _, _) =>
        {
            ApplyCalls++;
            throw new PlanRejectedException("volume label mismatch");
        };

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal((AutoStage.Failed, "relocation failed: PlanRejectedException"), (Entry.Stage, Entry.Detail));
        Assert.Empty(Vol.Moves);
        Assert.Equal(0, RevertCalls); // 이번 적용이 저널에 쓴 줄이 없으므로 되돌리기를 부르지 않음
        Assert.Contains(_log.Lines, l => l.Contains("nothing to revert", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanPathsOutsideTheAllowedRootsAreRejectedBeforeApply()
    {
        _game = _game with { AllowedRoots = [Path.Combine(Root, "elsewhere")] };
        _settings = new AsterSettings(50, [_game]);

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal((AutoStage.Failed, "relocation failed: PlanRejectedException"), (Entry.Stage, Entry.Detail));
        Assert.Equal(0, ApplyCalls);
        Assert.Empty(Vol.Moves);
    }

    [Fact]
    public void LayoutThatAlreadyMatchesIsDoneWithoutMovingOrCountingAgainstTheDailyLimit()
    {
        PlanFile applied = Make().Resolve(20000);
        Apply(applied, new ApplyOptions(Execute: true));
        applied.Save(_game.Plan);
        int moved = Vol.Moves.Count;

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal((AutoStage.Done, "already in place"), (Entry.Stage, Entry.Detail));
        Assert.Equal(moved, Vol.Moves.Count);
        Assert.Equal(0, _status.RelocationsWithin(AutoRelocator.DailyWindow));
        Assert.Empty(_w.PlanZones); // 계획 파일이 이미 있으면 다시 만들지 않음
    }

    [Fact]
    public void OnlyOneGameMovesPerIdleWindowMostRecentlyPlayedFirst()
    {
        string plan2 = Path.Combine(Root, "plans", "second.plan.json");
        GameSettings second = _game with { Name = "second", ExeNames = ["second.exe"], Plan = plan2, Trace = plan2 + ".prefetch.json" };
        _settings = _settings with { Games = [_game, second] };
        _w.Files.Add(AutoTracer.RawTracePath(second));
        _status.MarkPlayed("bench", "auto", AutoStage.WaitingIdle, _w.Now - TimeSpan.FromDays(2));
        _status.MarkPlayed("second", "auto", AutoStage.WaitingIdle, _w.Now - TimeSpan.FromDays(1));
        var order = new List<string>();
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            if (options.DryRun)
            {
                order.Add(g.Name);
            }

            return inner(g, plan, options, ct);
        };

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal(["second"], order);
        Assert.Equal(AutoStage.Done, _status.Get("second")!.Stage);
        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
    }

    [Fact]
    public void DisabledAutoModeDoesNothing()
    {
        SetAuto(a => a with { Enabled = false });

        Run(TimeSpan.FromMinutes(10));

        Assert.Null(_status.Get("bench"));
        Assert.False(File.Exists(_game.Plan));
    }

    [Theory]
    [InlineData("system")]
    [InlineData("ssd")]
    [InlineData("unknown")]
    public void SystemVolumeSsdAndUnknownDrivesAreNeverPlannedOrMoved(string kind)
    {
        switch (kind)
        {
            case "system": (_w.SystemDrive, _w.Hdd[Letter]) = (Letter, false); break; // 시스템 SSD
            case "ssd": _w.Hdd[Letter] = false; break;
            default: _w.Hdd[Letter] = null; break;
        }

        Run(TimeSpan.FromMinutes(10));

        Assert.False(File.Exists(_game.Plan));
        Assert.Empty(_w.PlanZones);
        Assert.Equal(0, ApplyCalls);
        Assert.Empty(Vol.Moves);
        Assert.Equal(AutoStage.Skipped, Entry.Stage);
        Assert.Single(_log.Lines, l => l.Contains("auto skipped", StringComparison.Ordinal)); // 틱마다 반복해서 남기지 않음
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public void SystemHddGamesAreOnlyPlannedAndPrewarmedNeverMoved()
    {
        (_w.SystemDrive, _w.Hdd[Letter]) = (Letter, true); // 시스템 볼륨이 회전 디스크
        UsePrewarm();

        Run(TimeSpan.FromMinutes(10));

        Assert.True(File.Exists(_game.Plan)); // 계획은 만들어 두어 핫 집합 예열이 쓸 수 있음
        Assert.Equal(1, Streams); // 새 트레이스로 한 번 예열
        Assert.Equal(0, ApplyCalls);
        Assert.Empty(Vol.Moves);
        Assert.Equal((AutoStage.Skipped, DriveCapabilities.PrewarmOnly), (Entry.Stage, Entry.Detail));
    }

    [Fact]
    public void DailyLimitStateIsLoggedOnceNotOnEveryTick()
    {
        SetAuto(a => a with { MaxRelocationsPerDay = 1 });
        _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1), 64L << 20);

        Run(TimeSpan.FromMinutes(10));

        Assert.Single(_log.Lines, l => l.Contains("auto waiting_idle", StringComparison.Ordinal));
        Assert.Equal((AutoStage.WaitingIdle, "daily limit reached"), (Entry.Stage, Entry.Detail));
    }

    [Fact]
    public void ManualGameIsPlannedAndRelocatedAutomaticallyToo()
    {
        _game = _game with { Source = "manual" };
        _settings = new AsterSettings(50, [_game]);

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotEmpty(Vol.Moves);
    }

    [Fact]
    public void ChangedSourceFilePlansAgainBeforeApplying()
    {
        Tick();
        Assert.Equal(2, _w.PlanZones.Count);
        File.SetLastWriteTimeUtc(F1, File.GetLastWriteTimeUtc(F1).AddMinutes(5)); // 계획 뒤 게임 업데이트가 파일을 다시 씀

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal(4, _w.PlanZones.Count); // 탐색 + 존, 한 번 더
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Contains(_log.Lines, l => l.Contains("source files changed since the plan", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.StartsWith("Error", StringComparison.Ordinal));
        PlanFile saved = PlanFile.Load(_game.Plan);
        Assert.Equal(PlanFile.FingerprintOf(F1), saved.Fingerprints.Single(f => f.Path == F1));
        Assert.True(Verify(saved).Contiguous);
    }

    [Fact]
    public void FileDeletedAfterThePlanIsLeftOutOfTheNewPlan()
    {
        Tick();
        Assert.Contains(PlanFile.Load(_game.Plan).Fingerprints, f => f.Path == F2);
        File.Delete(F2);
        Sessions = [Run((F1, 12), (F1, 0), (F1, 4))]; // 없어진 파일의 읽기는 트레이스를 다시 읽을 때 빠짐

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.DoesNotContain(PlanFile.Load(_game.Plan).Fingerprints, f => f.Path == F2);
    }

    [Fact]
    public void FailureOnlyRevertsWhatThisAutoApplyWroteNotAnEarlierManualApply()
    {
        _w.ReplanResidual = null; // 이 시험은 실패 뒤 되돌리기 범위를 봄: 다른 계획의 저널은 잔여 재계획이 이어받지 않는 경로로 둠 (이어받는 경로는 PartialPlanRecoveryTests)
        Apply(Make().Resolve(20000), new ApplyOptions(Execute: true)); // 예전 수동 적용이 남긴 저널
        Dictionary<string, List<Extent>> afterManual = Vol.Ext.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal);
        int rowsBefore = JournalRowCount();

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal((AutoStage.Failed, "relocation failed: PlanRejectedException"), (Entry.Stage, Entry.Detail)); // 저널은 다른 계획의 것
        Assert.Equal(0, RevertCalls);
        Assert.True(SameExtents(afterManual, Vol.Ext));
        Assert.Equal(rowsBefore, JournalRowCount());
        Assert.DoesNotContain(JournalRows(), r => Equals(r["type"], "revert"));
    }

    [Fact]
    public void ResumedRunThatFailsTheDataCheckRevertsOnlyItsOwnSteps()
    {
        Vol.AfterMove = n =>
        {
            if (n == 1)
            {
                _gate.Cancel();
            }
        };
        Run(TimeSpan.FromMinutes(3));
        Assert.Single(Vol.Moves);
        Vol.AfterMove = null;
        int rowsBefore = JournalRowCount();
        long firstStep = JournalRows().Where(r => Equals(r["type"], "step") && r.ContainsKey("from")).Select(r => (long)r["idx"]!).First(); // 저널의 planned 행은 실행 순서
        int changed = 0;
        _w.Apply = (_, plan, options, ct) => Applier.Apply(plan, Vol, [Root], JournalPath, Hooks((_, _, _) => "c" + changed++), options, ct);

        Run(TimeSpan.FromMinutes(3.5));

        Assert.Equal((AutoStage.Failed, "data check failed"), (Entry.Stage, Entry.Detail));
        Assert.Equal(1, RevertCalls);
        Assert.Equal(rowsBefore, LastSkipRows);
        long[] reverted = JournalRows().Where(r => Equals(r["type"], "revert")).Select(r => (long)r["idx"]!).Distinct().ToArray();
        Assert.NotEmpty(reverted);
        Assert.DoesNotContain(firstStep, reverted); // 처음 시도가 옮긴 단계는 이번 실행의 몫이 아님
    }

    [Fact]
    public void RevertStopsBetweenPiecesWhenCancelledAndLeavesTheJournal()
    {
        Apply(Make().Resolve(20000), new ApplyOptions(Execute: true));
        int moved = Vol.Moves.Count;
        int rows = JournalRowCount();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        RevertReport rep = Reverter.Revert(JournalPath, Vol, [Root], Hooks(), new ApplyOptions(Execute: true), cts.Token);

        Assert.Equal("cancelled", rep.Stopped);
        Assert.Equal(0, rep.Restored);
        Assert.Equal(moved, Vol.Moves.Count);
        Assert.Equal(rows, JournalRowCount());
    }

    [Fact]
    public void RevertSkipRowsIgnoresEarlierJournalLines()
    {
        Apply(Make().Resolve(20000), new ApplyOptions(Execute: true));
        int moved = Vol.Moves.Count;

        RevertReport none = Reverter.Revert(JournalPath, Vol, [Root], Hooks(), new ApplyOptions(Execute: true), skipRows: JournalRowCount());
        Assert.Equal(0, none.Restored);
        Assert.Equal(moved, Vol.Moves.Count);

        RevertReport all = Reverter.Revert(JournalPath, Vol, [Root], Hooks(), new ApplyOptions(Execute: true), skipRows: 0);
        Assert.True(all.Restored > 0);
        Assert.True(SameExtents(Vol.Ext, Files));
    }

    private List<JObject> JournalRows()
    {
        using var jr = new Journal(JournalPath);
        return [.. jr.Rows];
    }

    [Fact]
    public void StatusFileKeepsTheStagesAcrossARestart()
    {
        Run(TimeSpan.FromMinutes(3));
        Assert.Equal(AutoStage.Done, Entry.Stage);

        var restarted = new AutoStatus(Path.Combine(Root, "auto_status.json"), _log, () => _w.Now, ticks: FakeClock.Ticks(() => _w.Now));

        Assert.Equal(Entry, restarted.Get("bench"));
        var saved = (JObject)JsonRead.ReadFile(Path.Combine(Root, "auto_status.json"))!;
        Assert.Equal("relocated", ((JObject)((List<object?>)saved["events"]!)[0]!)["kind"]);
    }

    // ---- 기록 없는 게임: 첫 실행 없이 예열 ----

    private readonly List<string> _coldReads = [];
    private string _coldLabel = "full";
    private bool _coldDiskBusy;
    private Prewarmer? _cold;

    // 원시 트레이스도 prefetch 기록도 없는 게임: 가짜 계획기·읽기를 쓰는 진짜 Prewarmer (AutoMode 가 하는 대로 ColdWarmed 를 재배치기에 연결).
    private void UseCold(bool played = true)
    {
        _w.Files.Remove(AutoTracer.RawTracePath(_game));
        if (played)
        {
            _settings.Games.ToList().ForEach(g => _status.MarkPlayed(g.Name, "auto", AutoStage.Discovered, _w.Now)); // 기록 없는 예열은 플레이한 적 있는 게임만
        }

        _cold = new Prewarmer(_settings.Games, _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            PhysicalRamBytes = () => 32L << 30,
            GameRunning = _ => _w.Running,
            ZoneBytes = _ => null,
            TraceExists = _ => false,
            DiskBytesPerSecond = _ => _coldDiskBusy ? 100 << 20 : 0,
            ColdPlanner = (g, _, _, _) => new ColdPlan(_coldLabel, [new AsterCache.Core.Prefetch.ReadOp(Path.Combine(g.OriginRoot, "a.bin"), 0, 10 << 20)], 10 << 20, null),
            ColdRead = (ops, _) =>
            {
                _coldReads.AddRange(ops.Select(o => o.Path));
                return (new StreamStats(ops.Sum(o => o.Length), ops.Count, 0, 0.1), true);
            },
        }, _log, null, () => _w.Now);
        _cold.SetAuto(_settings.Auto);
        _cold.ColdWarmed += (g, label) => Relocator.NoteColdWarmed(g, label);
        _relocator = new AutoRelocator(WorldEnv(), _status, _log, () => _settings, _cold);
    }

    [Fact]
    public void GameWithoutATraceIsWarmedFromItsFolderOnTheFirstTickAndShowsPrewarmedFull()
    {
        UseCold();
        _status.Set("bench", "auto", null, AutoStage.Discovered, null);

        Tick();

        Assert.Equal([Path.Combine(Root, "a.bin")], _coldReads);
        Assert.Equal((AutoStage.Discovered, "prewarmed (full)"), (Entry.Stage, Entry.Detail));
        Assert.Equal("prewarmed (full)", AutoRelocator.PrewarmedBy("full"));
        Assert.Empty(Vol.Moves);
    }

    [Theory]
    [InlineData(true, 2)] // 다른 볼륨이 같은 물리 디스크
    [InlineData(false, 1)] // 다른 디스크: 같은 문자의 게임만
    public void SameDriveGamesFollowTheInjectedSameDiskJudgement(bool sameDisk, int count)
    {
        GameSettings d = Games.Make("d", "d.exe") with { OriginRoot = @"D:\Games\d" };
        GameSettings e = Games.Make("e", "e.exe") with { OriginRoot = @"E:\Games\e" };

        Assert.Equal(count, AutoRelocator.SameDriveGames([d, e], [d], (a, b) => a.Equals(b, StringComparison.Ordinal) || sameDisk).Count());
    }

    [Fact]
    public void MeasuredPrewarmSpeedDoesNotChangeTheRecoveryCeiling()
    {
        UseCold();
        _cold!.WarmMeasured += (g, mbps) => Relocator.NoteWarmMbps(g, mbps); // AutoMode 의 연결과 같음
        string drive = PlanFile.SplitDrive(_game.OriginRoot);
        _status.Set("bench", "auto", null, AutoStage.Discovered, null);
        Assert.Equal(DriveRecovery.CeilingMbps, ((DriveRecovery)Relocator.Recovery).CeilingFor(drive));

        Tick(); // 10 MiB / 0.1 s = 100 MB/s

        Assert.Equal(DriveRecovery.CeilingMbps, ((DriveRecovery)Relocator.Recovery).CeilingFor(drive));
    }

    [Fact]
    public void ProfileWarmShowsTheEngineInTheDetail()
    {
        UseCold();
        _coldLabel = "profile Unity";
        _status.Set("bench", "auto", null, AutoStage.Tracing, null);

        Tick();

        Assert.Equal((AutoStage.Tracing, "prewarmed (profile Unity)"), (Entry.Stage, Entry.Detail));
    }

    [Fact]
    public void ColdWarmSkipsAGameThatWasNeverPlayed()
    {
        UseCold(played: false);
        _status.Set("bench", "auto", null, AutoStage.Discovered, null);

        Tick();
        _w.Now += TimeSpan.FromHours(1);
        Tick();

        Assert.Empty(_coldReads); // 미플레이 게임은 첫 실행 때 트리거 예열만
    }

    [Fact]
    public void ColdWarmIsOncePerServiceRunAndTransientSkipsAreRetried()
    {
        UseCold();
        _status.Set("bench", "auto", null, AutoStage.Discovered, null);
        _coldDiskBusy = true;

        Tick();
        Assert.Empty(_coldReads);
        Assert.Null(Entry.Detail); // 디스크가 바빠 건너뜀

        _coldDiskBusy = false;
        _w.Now += Step;
        Tick();
        Assert.Empty(_coldReads); // 디스크 바쁨은 다음 틱이 아니라 BusyRetry 뒤에 다시

        _w.Now += AutoRelocator.BusyRetry;
        Tick();
        Assert.Single(_coldReads);

        _w.Now += Step;
        Tick();
        _w.Now += TimeSpan.FromHours(1);
        Tick();
        Assert.Single(_coldReads); // 예열한 게임은 다시 하지 않음
    }

    [Fact]
    public void RemovingAndReaddingAGameClearsItsColdWarmAttemptCache()
    {
        UseCold();
        _status.Set("bench", "auto", null, AutoStage.Discovered, null);
        _coldDiskBusy = true;
        Tick();
        Assert.Empty(_coldReads); // 디스크가 바빠 BusyRetry 뒤로 미뤄짐

        _coldDiskBusy = false;
        _settings = _settings with { Games = [] };
        Relocator.SettingsReloaded([_game]);
        _w.Now += Step;
        Tick(); // 설정 제거를 틱 소유 스레드에서 정리
        _settings = _settings with { Games = [_game] };
        _w.Now += Step;
        Tick();

        Assert.Single(_coldReads); // 다시 등록된 게임은 BusyRetry 를 기다리지 않음
    }

    [Fact]
    public void PermanentSkipIsNotRetriedEveryTick()
    {
        UseCold();
        int plans = 0;
        _cold = new Prewarmer([_game], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            PhysicalRamBytes = () => 32L << 30,
            GameRunning = _ => false,
            ZoneBytes = _ => null,
            TraceExists = _ => false,
            ColdPlanner = (_, _, _, _) =>
            {
                plans++;
                return ColdPlan.None("no engine profile matched");
            },
        }, _log, null, () => _w.Now);
        _relocator = new AutoRelocator(WorldEnv(), _status, _log, () => _settings, _cold);

        Tick();
        _w.Now += Step;
        Tick();

        Assert.Equal(1, plans);
    }

    [Fact]
    public void PermanentSkipIsRetriedEveryDiscoveryPeriod()
    {
        UseCold();
        int plans = 0;
        _cold = new Prewarmer([_game], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            PhysicalRamBytes = () => 32L << 30,
            GameRunning = _ => false,
            ZoneBytes = _ => null,
            TraceExists = _ => false,
            ColdPlanner = (_, _, _, _) =>
            {
                plans++;
                return ColdPlan.None("no engine profile matched");
            },
        }, _log, null, () => _w.Now);
        _relocator = new AutoRelocator(WorldEnv(), _status, _log, () => _settings, _cold);

        Tick();
        _w.Now += AutoRelocator.RetryEvery - Step;
        Tick();
        Assert.Equal(1, plans); // 주기 전에는 다시 열거하지 않음

        _w.Now += Step;
        Tick();
        Assert.Equal(1, plans); // 프로필 미일치는 폴더 mtime 이 같으면 주기가 돌아도 다시 열거하지 않음 (캐시)
    }

    [Fact]
    public void ColdWarmSkipsSsdAndSystemVolumeGamesAndRunningGames()
    {
        UseCold();
        _w.Hdd[Letter] = false;
        Tick();
        _w.Hdd[Letter] = true;
        _w.Running = true;
        _w.Now += Step;
        Tick();

        Assert.Empty(_coldReads);

        _w.Running = false;
        _w.Now += Step;
        Tick();
        Assert.Single(_coldReads);
    }

    [Fact]
    public void ColdWarmOffInSettingsDoesNothing()
    {
        UseCold();
        SetAuto(a => a with { FullPrewarm = false, EngineProfiles = false });
        _cold!.SetAuto(_settings.Auto);

        Tick();

        Assert.Empty(_coldReads);
    }

    [Fact]
    public void GameStartCancelsAColdWarmInFlightAndTheNextTickRetries()
    {
        UseCold();
        int reads = 0;
        _cold = new Prewarmer([_game], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            PhysicalRamBytes = () => 32L << 30,
            GameRunning = _ => false,
            ZoneBytes = _ => null,
            TraceExists = _ => false,
            ColdPlanner = (_, _, _, _) => new ColdPlan("full", [new AsterCache.Core.Prefetch.ReadOp(Path.Combine(Root, "a.bin"), 0, 1 << 20)], 1 << 20, null),
            ColdRead = (ops, ct) =>
            {
                reads++;
                if (reads == 1)
                {
                    _relocator!.CancelWarm(); // 읽는 중 게임 exe 가 뜸
                }

                return (new StreamStats(1 << 20, 1, 0, 0.1), !ct.IsCancellationRequested && reads > 1);
            },
        }, _log, null, () => _w.Now);
        _relocator = new AutoRelocator(WorldEnv(), _status, _log, () => _settings, _cold);

        Tick();
        Assert.Null(_cold.LastWarmed("bench")); // 끊긴 예열은 성공으로 치지 않음

        _w.Now += Step;
        Tick();
        Assert.Equal(2, reads);
        Assert.NotNull(_cold.LastWarmed("bench"));
    }

    // ---- 핫 집합 예열 진행 표시 ----

    [Fact]
    public void ProgressIsAddedToTheDetailAndReplacedOnEachUpdateWithoutLogging()
    {
        _status.Set("bench", "auto", null, AutoStage.WaitingIdle, "daily limit reached");
        int logged = _log.Lines.Count;

        Relocator.NoteProgress(_game, 128, 512);
        Assert.Equal((AutoStage.WaitingIdle, "daily limit reached, prewarming 128/512 MiB"), (Entry.Stage, Entry.Detail));
        Relocator.NoteProgress(_game, 256, 512);
        Assert.Equal("daily limit reached, prewarming 256/512 MiB", Entry.Detail);
        Assert.Equal(logged, _log.Lines.Count); // 10 초마다 줄이 쌓이지 않음
    }

    [Fact]
    public void EndingTheStreamClearsTheProgressAndACompleteOneShowsPrewarmed()
    {
        _status.Set("bench", "auto", null, AutoStage.WaitingIdle, null);
        Relocator.NoteProgress(_game, 64, 512);

        Relocator.NoteStreamEnded(_game, false); // 취소: 표시만 지움
        Assert.Null(Entry.Detail);
        Relocator.NoteProgress(_game, 64, 512);
        Relocator.NoteStreamEnded(_game, true);

        Assert.Equal("prewarmed", Entry.Detail); // 끝까지 읽었으면 바로 예열됨
    }

    [Fact]
    public void APrewarmThatOnlyFitPartlyInMemoryShowsPartialInsteadOfPrewarmed()
    {
        _status.Set("bench", "auto", null, AutoStage.Done, null);
        _streamer.BytesOf = g => g.StreamCapBytes(8000L << 20); // max_stream_mib 를 명시하지 않은 계획은 존 전체를 읽음
        long available = 6000;
        var prewarm = new Prewarmer(_settings.Games, _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => available,
            GameRunning = _ => false,
            ZoneBytes = _ => 8000L << 20,
            TraceExists = _ => true,
        }, _log, null, () => _w.Now);
        _relocator = new AutoRelocator(WorldEnv(), _status, _log, () => _settings, prewarm);
        prewarm.StreamEnded += (g, complete) => Relocator.NoteStreamEnded(g, complete);

        Assert.True(prewarm.Run(_game, "test").Ran);

        Assert.Equal((AutoStage.Done, "prewarmed (partial 1904/8000 MiB)"), (Entry.Stage, Entry.Detail));

        available = 20000; // 이제 전체가 들어감: 보충이 끝나면 부분 표시가 남지 않음
        Assert.True(prewarm.Supplement(_game, default));

        Assert.Equal((AutoStage.Done, "prewarmed"), (Entry.Stage, Entry.Detail));
    }

    [Fact]
    public void AZeroByteStreamDoesNotMarkTheGameAsPrewarmedInTheAutoStatus()
    {
        _status.Set("bench", "auto", null, AutoStage.Done, null);
        _streamer.BytesOf = _ => 0; // 아무것도 읽지 못함
        var prewarm = new Prewarmer(_settings.Games, _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 99999,
            GameRunning = _ => false,
            ZoneBytes = _ => 1 << 20,
            TraceExists = _ => true,
        }, _log, null, () => _w.Now);
        _relocator = new AutoRelocator(WorldEnv(), _status, _log, () => _settings, prewarm);
        prewarm.StreamEnded += (g, complete) => Relocator.NoteStreamEnded(g, complete);

        Assert.False(prewarm.Run(_game, "test").Ran);

        Assert.DoesNotContain("prewarmed", Entry.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherNotesKeepTheLiveProgressTag()
    {
        _status.Set("bench", "auto", null, AutoStage.Discovered, null);
        Relocator.NoteProgress(_game, 64, 512);

        Relocator.NoteColdWarmed(_game, "full"); // 다른 쪽이 detail 을 써도 진행 중 표시는 유지

        Assert.Equal("prewarmed (full), prewarming 64/512 MiB", Entry.Detail);
        Relocator.NoteProgress(_game, 128, 512);
        Assert.Equal("prewarmed (full), prewarming 128/512 MiB", Entry.Detail);
    }

    [Fact]
    public void FinishedStreamOnADoneGameShowsPrewarmedAndLearningOrRelocatingDetailsAreLeftAlone()
    {
        _status.Set("bench", "auto", null, AutoStage.Done, null);
        Relocator.NoteProgress(_game, 64, 512);
        Relocator.NoteStreamEnded(_game, true);
        Assert.Equal((AutoStage.Done, "prewarmed"), (Entry.Stage, Entry.Detail));

        _status.Set("bench", "auto", null, AutoStage.Learning, "512 MiB read ahead");
        Relocator.NoteProgress(_game, 64, 512);
        Assert.Equal("512 MiB read ahead", Entry.Detail); // 학습 표시의 형식을 깨지 않음
        _status.Set("bench", "auto", null, AutoStage.Relocating, "planning");
        Relocator.NoteProgress(_game, 64, 512);
        Assert.Equal("planning", Entry.Detail);
        Relocator.NoteStreamEnded(_game, true);
    }

    // 업데이트 뒤 재계획 시험의 준비: 한 번 재배치를 끝내고, 이후의 계획은 이동된 배치 (Vol.Ext) 를 기준으로 만들게 함.
    private long DoneAndReadyToReplan(out string trace)
    {
        Tick();
        _w.Now += Step;
        RunUntilDone();
        Assert.Equal(AutoStage.Done, Entry.Stage);
        trace = AutoTracer.RawTracePath(_game);
        long start = PlanFile.Load(_game.Plan).Zone.StartLcn!.Value;
        _w.MakePlan = (sessions, origin, zoneMib) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], preserveBytes: 64 << 20);
        _w.MakePlanAt = (sessions, origin, zoneMib, lcn, used) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], zoneStartLcn: lcn, preserveBytes: 64 << 20, fixedClusters: used);
        _w.ArchiveJournal = _ =>
        {
            if (!File.Exists(JournalPath))
            {
                return null;
            }

            File.Move(JournalPath, JournalPath + ".old");
            return JournalPath + ".old";
        };
        Tick(); // 변화 없음: 첫 검사
        Assert.Equal(AutoStage.Done, Entry.Stage);
        return start;
    }

    // 게임 업데이트: 핫 파일의 수정 시각이 바뀜.
    private void Update() => File.SetLastWriteTimeUtc(F1, File.GetLastWriteTimeUtc(F1).AddHours(1));

    // 다음 실행의 자동 트레이스가 끝난 것과 같은 상태.
    private void NewTraceFromNextRun()
    {
        _w.Files.Add(AutoTracer.RawTracePath(_game));
        _status.Set("bench", "auto", null, AutoStage.WaitingIdle, null);
    }

    [Fact]
    public void UpdateAfterRelocationSetsTraceAndPlanAsideThenTheNextRunPlansAgainIntoTheSameZone()
    {
        long start = DoneAndReadyToReplan(out string trace);
        int daily = _status.RelocationsWithin(AutoRelocator.DailyWindow);
        Update();
        Tick();
        Assert.Equal(AutoStage.Done, Entry.Stage); // 검사 주기 전에는 보지 않음

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Equal(AutoStage.Discovered, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
        Assert.False(_w.Files.Contains(trace) || File.Exists(trace));
        Assert.Contains(trace + ".stale", _w.Files);
        Assert.False(File.Exists(_game.Plan));
        Assert.True(File.Exists(_game.Plan + ".stale"));
        Assert.True(File.Exists(JournalPath + ".old"));
        Assert.Contains(_log.Lines, l => l.Contains("source files changed since the plan (game update)", StringComparison.Ordinal));
        Assert.Equal(daily, _status.RelocationsWithin(AutoRelocator.DailyWindow)); // 일일 상한 기록은 그대로

        Tick();
        Assert.Equal(AutoStage.Discovered, Entry.Stage); // 트레이스가 없으면 아무 일도 하지 않고 기다림
        Assert.False(File.Exists(_game.Plan));

        NewTraceFromNextRun();
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotNull(Entry.RelocatedAt);
        Assert.Equal([start], _w.PlanStarts); // 이전 존 시작에 맞춰 계획함
        Assert.Equal(start, PlanFile.Load(_game.Plan).Zone.StartLcn);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("previous zone is not free", StringComparison.Ordinal));
    }

    [Fact]
    public void GameUpdateDelaysPlanningByPostUpdateDelayMinThenProceeds()
    {
        DoneAndReadyToReplan(out _);
        Update();
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick(); // 업데이트 감지
        Assert.Equal(AutoStage.Discovered, Entry.Stage);
        _w.Files.Add(AutoTracer.BootTracePath(_game)); // 다시 계획할 기록이 생김

        for (int i = 0; i < 29; i++)
        {
            _w.Now += Step;
            Tick();
        }

        Assert.False(File.Exists(_game.Plan)); // 14 분 30 초: 아직 기다림
        Assert.Equal(AutoStage.Discovered, Entry.Stage);
        Assert.Single(_log.Lines, l => l.Contains("bench: waiting 15 min after update (drive may still be reorganizing)", StringComparison.Ordinal)); // 매 틱 찍지 않음

        _w.Now += Step;
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.True(File.Exists(_game.Plan));
        Assert.Single(_log.Lines, l => l.Contains("waiting 15 min after update", StringComparison.Ordinal));
    }

    [Fact]
    public void ZeroPostUpdateDelayPlansAtOnce()
    {
        SetAuto(a => a with { PostUpdateDelayMin = 0 });
        DoneAndReadyToReplan(out _);
        Update();
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        _w.Files.Add(AutoTracer.BootTracePath(_game));

        Tick();

        Assert.True(File.Exists(_game.Plan));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("after update", StringComparison.Ordinal));
    }

    [Fact]
    public void PreviousZoneThatIsNoLongerFreeFallsBackToTheNormalZoneSearch()
    {
        long start = DoneAndReadyToReplan(out _);
        Update();
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        NewTraceFromNextRun();
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        int rejected = 0;
        _w.Apply = (g, plan, options, ct) => options.DryRun && plan.Zone.StartLcn is not null && rejected++ == 0
            ? throw new PlanRejectedException("zone_not_free: LCN 1 is in use by another file")
            : inner(g, plan, options, ct);

        RunUntilDone();

        Assert.Equal(1, rejected);
        Assert.Equal([start], _w.PlanStarts); // 시작을 지정한 계획은 한 번, 그다음은 시작을 정하지 않은 계획
        Assert.Contains(_log.Lines, l => l.Contains("previous zone is not free, looking for a new one", StringComparison.Ordinal));
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotNull(PlanFile.Load(_game.Plan).Zone.StartLcn);
    }

    [Fact]
    public void PreviousZoneThatCannotBePlannedInPlaceBecauseOfACycleFallsBackToTheNormalZoneSearch()
    {
        long start = DoneAndReadyToReplan(out _);
        Update();
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        NewTraceFromNextRun();
        _w.MakePlanAt = (_, _, _, _, _) => throw new PyValueException("zone overlaps hot data in a cycle; choose another zone");

        RunUntilDone();

        Assert.Equal([start], _w.PlanStarts);
        Assert.Contains(_log.Lines, l => l.Contains("previous zone cannot be planned in place (zone overlaps hot data in a cycle; choose another zone), looking for a new one", StringComparison.Ordinal));
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotNull(PlanFile.Load(_game.Plan).Zone.StartLcn);
    }

    [Fact]
    public void RunningGameOrUnchangedFilesAreLeftAlone()
    {
        DoneAndReadyToReplan(out string trace);
        Update();
        _w.Running = true;
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Equal(AutoStage.Done, Entry.Stage); // 게임이 도는 중에는 건드리지 않음
        Assert.True(File.Exists(_game.Plan));
        Assert.Empty(_w.Moved);

        _w.Running = false;
        File.SetLastWriteTimeUtc(F1, File.GetLastWriteTimeUtc(F1).AddHours(-1)); // 원래대로 (같은 수정 시각)
        Tick();
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Empty(_w.Moved);
        Assert.Contains(trace, _w.Files);
    }

    [Fact]
    public void FailingToMoveTheFilesAsideKeepsTheStateAndIsRetriedAtTheNextCheck()
    {
        DoneAndReadyToReplan(out _);
        Update();
        _w.MoveFails = true;
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotNull(Entry.RelocatedAt);
        Assert.True(File.Exists(_game.Plan));
        Assert.Contains(_log.Lines, l => l.Contains("update check failed, will retry", StringComparison.Ordinal));

        _w.MoveFails = false;
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Equal(AutoStage.Discovered, Entry.Stage);
        Assert.False(File.Exists(_game.Plan));
    }

    // ---- 어떤 게임이든 실행 중이면 예열을 시작하지 않음 ----

    private GameSettings AddSecondGame()
    {
        string plan2 = Path.Combine(Root, "plans", "second.plan.json");
        GameSettings second = _game with { Name = "second", ExeNames = ["second.exe"], Plan = plan2, Trace = plan2 + ".prefetch.json" };
        _settings = _settings with { Games = [_game, second] };
        return second;
    }

    [Fact]
    public void NewTraceWarmWaitsWhileAnyOtherRegisteredGameIsRunning()
    {
        GameSettings second = AddSecondGame();
        _w.Files.Add(AutoTracer.RawTracePath(second));
        UsePrewarm();
        _w.FileTimes[AutoTracer.RawTracePath(second)] = _w.Now - TimeSpan.FromMinutes(1);
        _w.RunningExes.Add("bench.exe"); // 게임 A 만 실행 중

        Tick();
        Assert.Equal(0, Streams); // B 의 새 트레이스 예열도 시작하지 않음

        _w.RunningExes.Clear();
        _w.Now += Step;
        Tick();
        Assert.Equal(["bench", "second"], _streamer.Calls.Select(c => c.Game).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void WholeFolderWarmWaitsWhileAnyOtherRegisteredGameIsRunning()
    {
        AddSecondGame();
        UseCold();
        _w.RunningExes.Add("bench.exe");

        Tick();
        Assert.Empty(_coldReads);

        _w.RunningExes.Clear();
        _w.Now += Step;
        Tick();
        Assert.Equal(2, _coldReads.Count);
    }

    // ---- 재계획 검사: 읽을 수 없는 것은 변경이 아님 ----

    private void ExpectStillPlanned()
    {
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotNull(Entry.RelocatedAt);
        Assert.True(File.Exists(_game.Plan));
        Assert.Empty(_w.Moved);
    }

    [Fact]
    public void MissingOriginFolderOrLockedFilesAreSkippedNotTreatedAsAnUpdate()
    {
        DoneAndReadyToReplan(out _);
        Update();
        _w.DirectoryExists = _ => false; // 드라이브가 분리됨
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        ExpectStillPlanned();

        _w.DirectoryExists = _ => true;
        _w.Stat = _ => throw new IOException("locked");
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        ExpectStillPlanned();

        _w.Stat = _ => throw new UnauthorizedAccessException("denied");
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        ExpectStillPlanned();

        _w.Stat = PlanFile.FingerprintOf; // 다시 읽을 수 있으면 다음 검사에 업데이트로 처리
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        Assert.Equal(AutoStage.Discovered, Entry.Stage);
        Assert.False(File.Exists(_game.Plan));
    }

    [Fact]
    public void FileRemovedByAnUpdateStillCountsAsChanged()
    {
        DoneAndReadyToReplan(out _);
        _w.Stat = p => p == F1 ? throw new FileNotFoundException(p) : PlanFile.FingerprintOf(p);
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Equal(AutoStage.Discovered, Entry.Stage);
    }

    // ---- 재계획 중단 복구 ----

    [Fact]
    public void InterruptedReplanIsFinishedOnTheFirstTickAfterRestart()
    {
        DoneAndReadyToReplan(out string trace);
        _w.Files.Remove(trace);
        File.Move(_game.Plan, _game.Plan + ".stale"); // 계획은 옮겼는데 상태를 되돌리기 전에 꺼짐
        Assert.NotNull(Entry.RelocatedAt);
        _relocator = null; // 서비스 재시작

        Tick();

        Assert.Equal(AutoStage.Discovered, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
        Assert.Contains(_log.Lines, l => l.Contains("earlier replan was interrupted", StringComparison.Ordinal));
        int lines = _log.Lines.Count(l => l.Contains("earlier replan was interrupted", StringComparison.Ordinal));
        Tick();
        Assert.Equal(lines, _log.Lines.Count(l => l.Contains("earlier replan was interrupted", StringComparison.Ordinal)));
    }

    [Fact]
    public void RelocatedGameWithItsPlanInPlaceIsNotResetOnRestart()
    {
        DoneAndReadyToReplan(out _);
        File.WriteAllText(_game.Plan + ".stale", "{}"); // 옛 .stale 이 남아 있어도 계획이 있으면 그대로
        _relocator = null;

        Tick();

        ExpectStillPlanned();
    }

    // ---- boot 트레이스 (가져온 프로필) ----

    private void UseBootOnly()
    {
        _w.Files.Remove(AutoTracer.RawTracePath(_game));
        _w.Files.Add(AutoTracer.BootTracePath(_game));
    }

    [Fact]
    public void BootTraceAloneIsPlannedAndRelocatedAndShowsRelocatedProfile()
    {
        UseBootOnly();

        Tick();
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(AutoRelocator.RelocatedProfile, Entry.Detail);
        Assert.NotNull(Entry.RelocatedAt);
        Assert.Equal("boot", PlanFile.Load(_game.Plan).Stats["source"] as string);
    }

    [Fact]
    public void BootTraceIsPrewarmedAndTheProfileTagSurvivesThePrewarmedTag()
    {
        UseBootOnly();
        UsePrewarm();
        _w.FileTimes[AutoTracer.BootTracePath(_game)] = _w.Now - TimeSpan.FromMinutes(1);

        Tick();
        Assert.Equal(1, Streams); // 새 boot 트레이스로 바로 예열
        _w.Now += Step;
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal($"{AutoRelocator.RelocatedProfile}, {AutoRelocator.Prewarmed}", Entry.Detail);
    }

    [Fact]
    public void RawTraceWinsOverBootAndLeavesNoBootSourceInThePlan()
    {
        _w.Files.Add(AutoTracer.BootTracePath(_game));

        Tick();
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Null(Entry.Detail);
        Assert.False(PlanFile.Load(_game.Plan).Stats.ContainsKey("source"));
    }

    [Fact]
    public void FirstRunRecordingReplacesTheBootPlanAndPlansAgainIntoTheSameZone()
    {
        UseBootOnly();
        long start = DoneAndReadyToReplan(out string raw);
        Assert.Equal(AutoRelocator.RelocatedProfile, Entry.Detail);
        int daily = _status.RelocationsWithin(AutoRelocator.DailyWindow);

        _w.Files.Add(raw); // 첫 실행이 끝나 원시 트레이스가 생김 (자동 트레이스의 끝과 같은 상태)
        _status.Set("bench", "auto", null, AutoStage.WaitingIdle, null);
        Tick();
        Assert.NotNull(Entry.RelocatedAt); // 검사 주기 전에는 보지 않음

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage); // 같은 틱에서 원시 트레이스로 바로 다시 계획하고 유휴를 기다림
        Assert.Null(Entry.RelocatedAt);
        Assert.Contains(raw, _w.Files); // 방금 기록한 원시 트레이스는 치우지 않음
        Assert.DoesNotContain(raw + ".stale", _w.Files);
        Assert.Equal([start], _w.PlanStarts); // 이전 존 시작에 맞춰 계획함
        Assert.True(File.Exists(_game.Plan + ".stale"));
        Assert.True(File.Exists(JournalPath + ".old"));
        Assert.Contains(_log.Lines, l => l.Contains("first run recorded", StringComparison.Ordinal));
        Assert.Equal(daily, _status.RelocationsWithin(AutoRelocator.DailyWindow));

        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal("already in place", Entry.Detail); // 같은 존이라 옮길 것이 없고, 이제 프로필 기반 표시도 없음
        Assert.Equal([start], _w.PlanStarts);
        Assert.Equal(start, PlanFile.Load(_game.Plan).Zone.StartLcn);
        Assert.False(PlanFile.Load(_game.Plan).Stats.ContainsKey("source"));
    }

    [Fact]
    public void BootPlanIsKeptWhileNoRawTraceExists()
    {
        UseBootOnly();
        DoneAndReadyToReplan(out _);

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.True(File.Exists(_game.Plan));
        Assert.Empty(_w.Moved);
    }

    // ---- 부트스트랩 S2: 프리페치 기록으로 만든 boot 트레이스 ----

    // 원시·boot 트레이스가 없는 게임에 프리페치 기록이 있는 것처럼: 부르면 boot 트레이스가 생기고 "prefetch" 표시가 붙음.
    private void UsePrefetchRecord(int ranges = 3)
    {
        _w.Files.Remove(AutoTracer.RawTracePath(_game));
        _w.PrefetchBoot = _ =>
        {
            string boot = AutoTracer.BootTracePath(_game);
            _w.Files.Add(boot);
            _w.TraceSources[boot] = AutoRelocator.PlanSourcePrefetch;
            return ranges;
        };
    }

    [Fact]
    public void PrefetchRecordMakesTheBootTraceThenPlansRelocatesAndShowsRelocatedPrefetch()
    {
        UsePrefetchRecord();

        Tick();
        RunUntilDone();

        Assert.Equal(["bench"], _w.PrefetchCalls);
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal("relocated (prefetch)", Entry.Detail);
        Assert.NotNull(Entry.RelocatedAt);
        Assert.Equal("prefetch", PlanFile.Load(_game.Plan).Stats["source"] as string);
        Assert.Contains(_log.Lines, l => l.Contains("boot trace made from the prefetch record (3 ranges)", StringComparison.Ordinal));
    }

    [Fact]
    public void PrefetchTagSurvivesThePrewarmedTag()
    {
        UsePrefetchRecord();
        UsePrewarm();
        _w.Files.Remove(AutoTracer.RawTracePath(_game));
        _w.FileTimes.Remove(AutoTracer.RawTracePath(_game));
        _w.FileTimes[AutoTracer.BootTracePath(_game)] = _w.Now - TimeSpan.FromMinutes(1);

        Tick();
        Assert.Equal(1, Streams); // 새 boot 트레이스로 바로 예열
        _w.Now += Step;
        RunUntilDone();

        Assert.Equal($"{AutoRelocator.RelocatedPrefetch}, {AutoRelocator.Prewarmed}", Entry.Detail);
    }

    [Fact]
    public void SharedProfileBootComesFirstAndPrefetchIsNeverAsked()
    {
        UseBootOnly(); // S1: 공유 프로필이 만든 boot
        UsePrefetchRecord();
        _w.Files.Add(AutoTracer.BootTracePath(_game));

        Tick();
        RunUntilDone();

        Assert.Empty(_w.PrefetchCalls);
        Assert.Equal(AutoRelocator.RelocatedProfile, Entry.Detail);
        Assert.Equal("boot", PlanFile.Load(_game.Plan).Stats["source"] as string);
    }

    [Fact]
    public void RawTraceMeansPrefetchIsNeverAsked()
    {
        _w.PrefetchBoot = _ => 5;

        Tick();

        Assert.Empty(_w.PrefetchCalls);
    }

    [Theory]
    [InlineData("ssd")]
    [InlineData("system")]
    [InlineData("unknown")]
    [InlineData("running")]
    public void PrefetchIsNotAskedForSystemVolumeSsdUnknownDriveOrARunningGame(string why)
    {
        UsePrefetchRecord();
        switch (why)
        {
            case "ssd":
                _w.Hdd[Letter] = false;
                break;
            case "unknown":
                _w.Hdd[Letter] = null;
                break;
            case "system":
                (_w.SystemDrive, _w.Hdd[Letter]) = (Letter, false); // 시스템 SSD
                break;
            default:
                _w.Running = true;
                break;
        }

        Tick();

        Assert.Empty(_w.PrefetchCalls);
    }

    [Fact]
    public void MissingPrefetchRecordIsAskedOncePerDiscoveryPeriodAndFallsBackToNothing()
    {
        _w.Files.Remove(AutoTracer.RawTracePath(_game));

        Tick();
        _w.Now += Step;
        Tick();
        _w.Now += Step;
        Tick();
        Assert.Equal(["bench"], _w.PrefetchCalls); // 30 초마다 .pf 를 다시 훑지 않음

        _w.Now += AutoRelocator.RetryEvery;
        Tick();
        Assert.Equal(["bench", "bench"], _w.PrefetchCalls);
        Assert.False(File.Exists(_game.Plan));
    }

    [Fact]
    public void PrefetchFailureIsLoggedAndDoesNotStopTheTick()
    {
        _w.Files.Remove(AutoTracer.RawTracePath(_game));
        _w.PrefetchBoot = _ => throw new UnauthorizedAccessException();

        Tick();

        Assert.Contains(_log.Lines, l => l.Contains("prefetch record could not be used (UnauthorizedAccessException)", StringComparison.Ordinal));
    }

    [Fact]
    public void PrefetchPlanTakesWholeSmallFilesInPhysicalOrder()
    {
        UsePrefetchRecord();
        _w.LoadSessions = () => [[new TraceEvent(F2, 0, 1 << 20), new TraceEvent(F1, 0, 1 << 20)]]; // 프리페치 순서는 F2 가 먼저지만 물리 위치는 F1 (LCN 1000) 이 앞
        Tick();

        PlanFile small = PlanFile.Load(_game.Plan);
        Assert.Equal([F1, F2], small.Order.Select(o => o.Path).Distinct()); // 물리 순서
        Assert.Equal(256, small.Order.Where(o => o.Path == F1).Sum(o => o.Clusters)); // 파일 통째 (1 MiB / 4 KiB)
        Assert.Equal(256, small.Order.Where(o => o.Path == F2).Sum(o => o.Clusters));
        Assert.Equal(0, small.Order.Where(o => o.Path == F1).Min(o => o.Vcn));
    }

    // F2 를 1 GiB 짜리 파일 하나로: 실제 파일은 SetLength 로 늘림 (내용을 쓰지 않음), 볼륨의 extent 는 가짜.
    private void MakeBigF2(long bytes)
    {
        using (var f = new FileStream(F2, FileMode.Open, FileAccess.Write))
        {
            f.SetLength(bytes);
        }

        Files[F2] = [new Extent(0, 100000, bytes / Cb)];
        _w.Extents = p => p == F2 ? Files[F2] : Vol.Ext[p];
    }

    [Fact]
    public void PrefetchPlanOfABigFileTakesEachReferencedRangeWithPadding()
    {
        const long gib = 1L << 30;
        const long mib = 1L << 20;
        UsePrefetchRecord();
        MakeBigF2(gib);
        _w.LoadSessions = () => [[new TraceEvent(F2, 100 * mib, mib), new TraceEvent(F2, 800 * mib, mib)]];

        Tick();

        List<PlanRange> ranges = [.. PlanFile.Load(_game.Plan).Order.Where(o => o.Path == F2)];
        Assert.Equal(2, ranges.Count); // 큰 파일도 참조한 두 범위만 (앞뒤 64 MiB 포함)
        Assert.Equal((36 * mib / Cb, 129 * mib / Cb), (ranges[0].Vcn, ranges[0].Clusters));
        Assert.Equal((736 * mib / Cb, 129 * mib / Cb), (ranges[1].Vcn, ranges[1].Clusters));
    }

    [Fact]
    public void PrefetchRangesThatTouchAfterPaddingAreMerged()
    {
        const long gib = 1L << 30;
        const long mib = 1L << 20;
        UsePrefetchRecord();
        MakeBigF2(gib);
        _w.LoadSessions = () => [[new TraceEvent(F2, 100 * mib, mib), new TraceEvent(F2, 200 * mib, mib)]]; // 패딩 뒤 100 MiB 간격이 겹침

        Tick();

        List<PlanRange> ranges = [.. PlanFile.Load(_game.Plan).Order.Where(o => o.Path == F2)];
        Assert.Single(ranges);
        Assert.Equal(229 * mib / Cb, ranges[0].Clusters); // 36 .. 265 MiB
    }

    [Fact]
    public void FirstRunRecordingReplacesThePrefetchPlanAndPlansAgainIntoTheSameZone()
    {
        UsePrefetchRecord();
        long start = DoneAndReadyToReplan(out string raw);
        Assert.Equal(AutoRelocator.RelocatedPrefetch, Entry.Detail);

        _w.Files.Add(raw); // 첫 실행이 끝나 원시 트레이스가 생김
        _status.Set("bench", "auto", null, AutoStage.WaitingIdle, null);
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
        Assert.Contains(raw, _w.Files);
        Assert.Equal([start], _w.PlanStarts); // 이전 존 시작에 맞춰 먼저 계획함
        Assert.Contains(_log.Lines, l => l.Contains("previous zone holds data this plan does not move", StringComparison.Ordinal)); // 존에 남은 자기 파일의 나머지 부분이 목표를 막아 새 존으로
        Assert.True(File.Exists(_game.Plan + ".stale"));
        Assert.Single(_w.PrefetchCalls); // 다시 묻지 않음

        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.False(PlanFile.Load(_game.Plan).Stats.ContainsKey("source"));
        Assert.DoesNotContain(AutoRelocator.RelocatedPrefetch, Entry.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    // ---- 지문 검사 비용: 대표 파일만 먼저 ----

    [Fact]
    public void FullFingerprintComparisonRunsOnlyWhenARepresentativeFileChanged()
    {
        DoneAndReadyToReplan(out _);
        string exe = Path.Combine(Root, "bench.exe");
        PlanFile plan = PlanFile.Load(_game.Plan);
        List<Fingerprint> fps = [.. plan.Fingerprints, .. Enumerable.Range(1, 6).Select(i => new Fingerprint(Path.Combine(Root, $"x{i}.bin"), 100 * i, i)), new(exe, 10, 5)];
        (plan with { Fingerprints = fps }).Save(_game.Plan);
        string biggestOutsideSample = Path.Combine(Root, "x1.bin"); // 가장 큰 3 개 (f1, f2, x6) 와 exe 밖
        var changed = new HashSet<string>(StringComparer.Ordinal);
        var stats = new List<string>();
        _w.Stat = p =>
        {
            stats.Add(p);
            Fingerprint fp = fps.First(f => f.Path == p);
            return changed.Contains(p) ? fp with { Size = fp.Size + 1 } : fp;
        };

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        ExpectStillPlanned();
        Assert.Equal(4, stats.Count); // exe + 큰 3 개만 봄 (전체 9 개 아님)
        Assert.DoesNotContain(biggestOutsideSample, stats);

        changed.Add(biggestOutsideSample); // 대표가 아닌 파일만 바뀜: 다음 대표 변경 때 잡힘
        stats.Clear();
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        ExpectStillPlanned();
        Assert.Equal(4, stats.Count);

        changed.Remove(biggestOutsideSample);
        changed.Add(exe); // 대표 변경 -> 전체 비교 -> 재계획
        stats.Clear();
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        Assert.Equal(AutoStage.Discovered, Entry.Stage);
        Assert.Equal(1 + 2 * fps.Count, stats.Count); // 대표 (exe) 1 번 + 전체 비교 + 잠금 안에서 바뀐 파일을 가리는 전체 비교
        Assert.Contains(biggestOutsideSample, stats);
    }

    // ---- 쓰기 뒤 회복 (auto.post_relocate_recovery) ----

    private string Drive => Path.GetPathRoot(Root)!;

    private readonly List<(string Drive, long Lcn)> _probed = [];
    private double _zoneMbps = 98;
    private double _refMbps = 72;
    private const long RefLcn = 777_000;

    // 표본을 가짜로: 기준 구간은 RefLcn, 존이 이미 할당돼 있으면 (zoneAllocated) 존도 잼. 속도는 _zoneMbps/_refMbps 를 시험이 바꿈.
    private void UseProbe(string mode, bool zoneAllocated)
    {
        SetAuto(a => a with { PostRelocateRecovery = mode });
        _w.LocateProbeSpots = (_, _) => (zoneAllocated, RefLcn);
        _w.ProbeStepLcn = _ => 64;
        _w.ProbeMbps = (drive, lcn, _) =>
        {
            _probed.Add((drive, lcn));
            return lcn >= RefLcn ? _refMbps : _zoneMbps;
        };
    }

    // 쓰기 도중 상한에 걸려 멈춘 것으로 만듦 (재배치는 끝나지 않고 다음 유휴에 이어짐, 쓴 바이트는 있음).
    private void StopAfterWriting()
    {
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        int executed = 0;
        _w.Apply = (g, plan, options, ct) =>
        {
            ApplyReport rep = inner(g, plan, options, ct);
            if (options.Execute && executed++ == 0)
            {
                rep.Stopped = "write cap";
                (_zoneMbps, _refMbps) = (10, 10); // 기준은 이미 쟀음: 쓴 뒤라 느려짐
            }

            return rep;
        };
    }

    [Fact]
    public void ProbeModeMeasuresTheBaselineJustBeforeTheMoveAndOnlyTheReferenceWhenTheZoneIsStillEmpty()
    {
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: false);
        int probedAtMove = -1;
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            probedAtMove = options.Execute ? _probed.Count : probedAtMove;
            return inner(g, plan, options, ct);
        };

        Tick();
        _w.Now += Step;
        RunUntilDone();

        Assert.Equal(3, probedAtMove); // 이동 직전에 같은 1~3 칸을 각각 한 번씩 측정
        Assert.Equal(new[] { 64L, 128L, 192L }.Select(offset => (PlanFile.SplitDrive(Root).ToUpperInvariant(), RefLcn + offset)), _probed.Take(3)); // 빈 존은 기준에서 뺌
        Assert.True(Relocator.Recovery.IsRecovering(Drive));
    }

    [Fact]
    public void ProbeBaselineUsesTheMedianOfTheSameThreeCellsAndKeepsTheStartLcns()
    {
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        double[] rates = [100, 20, 90, 80, 70, 60]; // zone/ref 쌍을 칸 1~3 순으로 반환
        int read = 0;
        _w.ProbeMbps = (drive, lcn, _) =>
        {
            _probed.Add((drive, lcn));
            return rates[Math.Min(read++, rates.Length - 1)];
        };

        Tick();
        _w.Now += Step;
        RunUntilDone();

        DriveRecoveryRecord saved = Assert.Single(_status.DriveRecoveries());
        Assert.Equal(90, saved.Zone!.Value.Mbps); // 100, 90, 70 의 중앙값
        Assert.Equal(60, saved.Ref!.Value.Mbps); // 20, 80, 60 의 중앙값
        Assert.Equal(_probed[0].Lcn - 64, saved.Zone.Value.Lcn);
        Assert.Equal(RefLcn, saved.Ref.Value.Lcn);
        Assert.True(read >= 6);
    }

    [Fact]
    public void MatchingMedianBaselineAndFourOfSixFluctuatingSamplesRecoverWithinTwelveMinutes()
    {
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        double[] baselineRates = [100, 80, 90, 80, 70, 80];
        double[] zoneRates = [90, 90, 70, 90, 90, 70];
        double[] refRates = [60, 80, 80, 60, 80, 80];
        int baselineReads = 0;
        int recoveryReads = 0;
        bool afterWrite = false;
        _w.ProbeMbps = (drive, lcn, _) =>
        {
            _probed.Add((drive, lcn));
            if (!afterWrite)
            {
                return baselineRates[Math.Min(baselineReads++, baselineRates.Length - 1)];
            }

            int sample = Math.Min(recoveryReads / 2, zoneRates.Length - 1);
            double speed = lcn >= RefLcn ? refRates[sample] : zoneRates[sample];
            recoveryReads++;
            return speed;
        };
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            ApplyReport report = inner(g, plan, options, ct);
            afterWrite |= options.Execute && report.MovedBytes > 0;
            return report;
        };

        Tick();
        _w.Now += Step;
        RunUntilDone();

        DriveRecoveryRecord saved = Assert.Single(_status.DriveRecoveries());
        Assert.Equal(90, saved.Zone!.Value.Mbps);
        Assert.Equal(80, saved.Ref!.Value.Mbps);
        Assert.Equal(6, baselineReads);
        Run(TimeSpan.FromMinutes(12));

        Assert.False(Relocator.Recovery.IsRecovering(Drive));
        Assert.Equal(12, recoveryReads);
        Assert.Contains(_log.Lines, line => line.Contains("recovered after 12 min (statistical recovery:", StringComparison.Ordinal));
    }

    [Fact]
    public void BaselineSelfReadsAreIgnoredAfterFreshObservationWindows()
    {
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        int executed = 0;
        TimeSpan selfReadBusyRemaining = TimeSpan.Zero;
        _w.ProbeMbps = (drive, lcn, _) =>
        {
            _probed.Add((drive, lcn));
            _w.Disk = DiskActivity.BusyBytesPerSecond; // 기준 읽기가 바쁨 카운터에 반영됨
            selfReadBusyRemaining = TimeSpan.FromMilliseconds(3_100);
            return _zoneMbps;
        };
        _w.WaitOverride = (duration, token) =>
        {
            _w.Now += duration;
            selfReadBusyRemaining -= duration;
            if (selfReadBusyRemaining <= TimeSpan.Zero)
            {
                _w.Disk = 0; // 새 관측 구간에는 기준 읽기가 포함되지 않음
            }

            return !token.IsCancellationRequested;
        };
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            executed += options.Execute ? 1 : 0;
            return inner(g, plan, options, ct);
        };

        Tick();
        _w.Now += Step;
        RunUntilDone();

        Assert.Equal(6, _probed.Count);
        Assert.True(executed > 0);
        Assert.Equal(TimeSpan.FromMilliseconds(6_200), _w.WaitDurations.Aggregate(TimeSpan.Zero, (sum, duration) => sum + duration));
    }

    [Fact]
    public void BusyExternalLoadAfterFreshObservationDefersBaselineAndWrite()
    {
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        int executed = 0;
        _w.ProbeMbps = (drive, lcn, _) =>
        {
            _probed.Add((drive, lcn));
            _w.Disk = DiskActivity.BusyBytesPerSecond;
            return _zoneMbps;
        };
        _w.WaitOverride = (duration, token) =>
        {
            _w.Now += duration;
            _w.Disk = DiskActivity.BusyBytesPerSecond; // 관측 구간에도 외부 작업이 이어짐
            return !token.IsCancellationRequested;
        };
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            executed += options.Execute ? 1 : 0;
            return inner(g, plan, options, ct);
        };

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal(2, _probed.Count); // 첫 칸까지만 측정
        Assert.Equal(0, executed);
        Assert.Equal(TimeSpan.FromMilliseconds(3_100), _w.WaitDurations.Aggregate(TimeSpan.Zero, (sum, duration) => sum + duration));
    }

    [Fact]
    public void GameStartingDuringFreshObservationWaitStopsBaselineAndDefersWrite()
    {
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        _w.ProbeMbps = (drive, lcn, _) =>
        {
            _probed.Add((drive, lcn));
            return _zoneMbps;
        };
        _w.WaitOverride = (duration, token) =>
        {
            _w.Now += duration;
            _w.Running = true;
            return !token.IsCancellationRequested;
        };
        AutoEnv env = WorldEnv();
        env.GameRunningOnDisk = _ => _w.Running;
        _relocator = new AutoRelocator(env, _status, _log, () => _settings);
        int executed = 0;
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            executed += options.Execute ? 1 : 0;
            return inner(g, plan, options, ct);
        };

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal(2, _probed.Count);
        Assert.Equal(0, executed);
        Assert.Equal(TimeSpan.FromMilliseconds(100), _w.WaitDurations.Aggregate(TimeSpan.Zero, (sum, duration) => sum + duration));
        Assert.Equal((AutoStage.WaitingIdle, "baseline interrupted (game start or disk busy), will retry"), (Entry.Stage, Entry.Detail));
    }

    [Fact]
    public void CancellationDuringFreshObservationWaitStopsBaselineAndDefersWrite()
    {
        using var cts = new CancellationTokenSource();
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        _w.ProbeMbps = (drive, lcn, _) =>
        {
            _probed.Add((drive, lcn));
            return _zoneMbps;
        };
        _w.WaitOverride = (duration, token) =>
        {
            _w.Now += duration;
            cts.Cancel();
            return !token.IsCancellationRequested;
        };
        _relocator = new AutoRelocator(WorldEnv(), _status, _log, () => _settings);
        int executed = 0;
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            executed += options.Execute ? 1 : 0;
            return inner(g, plan, options, ct);
        };

        for (int i = 0; i < 8 && _w.WaitDurations.Count == 0; i++)
        {
            Relocator.Tick(_gate, cts.Token);
            _w.Now += Step;
        }

        Assert.Equal(2, _probed.Count);
        Assert.Equal(0, executed);
        Assert.Equal(TimeSpan.FromMilliseconds(100), _w.WaitDurations.Aggregate(TimeSpan.Zero, (sum, duration) => sum + duration));
        Assert.Equal((AutoStage.WaitingIdle, "baseline interrupted (game start or disk busy), will retry"), (Entry.Stage, Entry.Detail));
    }

    [Fact]
    public void ProbeBaselineWithoutAKnownRotationIntervalUsesTheFixedWait()
    {
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        _w.ProbeStepLcn = _ => 0;

        Tick();
        _w.Now += Step;
        RunUntilDone();
        _probed.Clear();

        Assert.True(Relocator.Recovery.IsRecovering(Drive));
        Run(TimeSpan.FromMinutes(14));
        Assert.True(Relocator.Recovery.IsRecovering(Drive));
        Run(TimeSpan.FromMinutes(2));
        Assert.False(Relocator.Recovery.IsRecovering(Drive));
        Assert.Empty(_probed);
    }

    [Fact]
    public void RecoveringAfterRelocationLogsValuesThenRecoversOnTwoGoodSamples()
    {
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        Tick();
        _w.Now += Step;
        RunUntilDone();
        Assert.True(Relocator.Recovery.IsRecovering(Drive));

        (_zoneMbps, _refMbps) = (61, 70);
        Run(TimeSpan.FromMinutes(5));
        Assert.True(Relocator.Recovery.IsRecovering(Drive));
        string[] samples = [.. _log.Lines.Where(line => line.Contains("recovery sample cell", StringComparison.Ordinal))];
        Assert.Equal(2, samples.Length);
        Assert.All(samples, line => Assert.Contains("zone 61/98 MB/s, ref 70/72 MB/s, verdict Bad", line, StringComparison.Ordinal));

        (_zoneMbps, _refMbps) = (95, 70);
        Run(TimeSpan.FromMinutes(5));

        Assert.False(Relocator.Recovery.IsRecovering(Drive));
        Assert.Contains(_log.Lines, line => line.Contains("recovered after ", StringComparison.Ordinal)
            && line.Contains(" min (two consecutive passes)", StringComparison.Ordinal));
    }

    [Fact]
    public void RecoveryGivesUpAtFourTimesThePostUpdateDelay()
    {
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        Tick();
        _w.Now += Step;
        RunUntilDone();
        (_zoneMbps, _refMbps) = (10, 10);

        Run(TimeSpan.FromMinutes(59));
        Assert.True(Relocator.Recovery.IsRecovering(Drive));

        Run(TimeSpan.FromMinutes(2));
        Assert.False(Relocator.Recovery.IsRecovering(Drive)); // 15 x 4 = 60 분
    }

    [Fact]
    public void FixedModeWaitsPostUpdateDelayMinutesWithoutSampling()
    {
        UseProbe(AutoSettings.RecoveryFixed, zoneAllocated: true);
        Tick();
        _w.Now += Step;
        RunUntilDone();
        _probed.Clear();

        Run(TimeSpan.FromMinutes(14));
        Assert.True(Relocator.Recovery.IsRecovering(Drive));
        Run(TimeSpan.FromMinutes(2));

        Assert.False(Relocator.Recovery.IsRecovering(Drive));
        Assert.Empty(_probed);
    }

    [Fact]
    public void OffModeDoesNotWaitOrSample()
    {
        UseProbe(AutoSettings.RecoveryOff, zoneAllocated: true);
        Tick();
        _w.Now += Step;
        RunUntilDone();

        Assert.False(Relocator.Recovery.IsRecovering(Drive));
        Assert.Empty(_probed);
    }

    [Fact]
    public void GameStartAfterTheBaselineZoneSampleReadsNothingMoreAndDoesNotRelocateThisTick()
    {
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        int executes = 0;
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            executes += options.Execute ? 1 : 0;
            return inner(g, plan, options, ct);
        };
        _w.ProbeMbps = (drive, lcn, round) =>
        {
            _probed.Add((drive, lcn));
            round.BytesRead += 1L << 20; // 존을 1 MiB 읽은 뒤
            _gate.Cancel(); // 게임 시작
            return _zoneMbps;
        };

        Run(TimeSpan.FromMinutes(3));

        Assert.Single(_probed); // 참조는 읽지 않음
        Assert.Equal(0, executes);
        Assert.Equal((AutoStage.WaitingIdle, "baseline interrupted (game start or disk busy), will retry"), (Entry.Stage, Entry.Detail));
        Assert.False(Relocator.Recovery.IsRecovering(Drive)); // 기준 없이 고정 대기로 강행하지 않음
        Assert.Equal(0, _status.RelocationsWithin(AutoRelocator.DailyWindow));
    }

    [Fact]
    public void MissingBaselineLeaseStopsBeforeSamplingOrRelocation()
    {
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log);
        Relocator.SetDiskReadCoordinator(coordinator);
        _w.LocateProbeSpots = (_, _) =>
        {
            coordinator.Yield(_game.OriginRoot, "game started", "other game");
            return (true, RefLcn);
        };
        int executes = 0;
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            executes += options.Execute ? 1 : 0;
            return inner(g, plan, options, ct);
        };

        Run(TimeSpan.FromMinutes(3));

        Assert.Empty(_probed);
        Assert.Equal(0, executes);
        Assert.Equal((AutoStage.WaitingIdle, "baseline interrupted (game start or disk busy), will retry"), (Entry.Stage, Entry.Detail));
        Assert.False(Relocator.Recovery.IsRecovering(Drive));
    }

    [Fact]
    public void DiskActivityDuringBaselineCellDefersBeforeTheNextCell()
    {
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        int executes = 0;
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            executes += options.Execute ? 1 : 0;
            return inner(g, plan, options, ct);
        };
        _w.ProbeMbps = (drive, lcn, _) =>
        {
            _probed.Add((drive, lcn));
            _w.Disk = 10L << 20; // 존을 재는 사이에 다른 읽기가 시작됨
            return _zoneMbps;
        };

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal(2, _probed.Count); // 존·참조 한 칸을 마친 뒤 바쁨이 지속되면 다음 칸을 읽지 않음
        Assert.Equal(0, executes);
        Assert.Equal(TimeSpan.FromMilliseconds(3_100), _w.WaitDurations.Aggregate(TimeSpan.Zero, (sum, duration) => sum + duration));
    }

    [Fact]
    public void BaselineZoneAndReferenceShareOneRoundWithinTheByteBudget()
    {
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        var rounds = new List<ProbeRound>();
        _w.ProbeMbps = (drive, lcn, round) =>
        {
            _probed.Add((drive, lcn));
            rounds.Add(round);
            return DiskProbe.Run(0, _ => true, () => 0, DiskProbe.WallLimit, round);
        };

        Tick();
        _w.Now += Step;
        RunUntilDone();

        Assert.Equal(6, rounds.Count);
        for (int offset = 0; offset < rounds.Count; offset += 2)
        {
            Assert.Same(rounds[offset], rounds[offset + 1]);
            Assert.Equal((2, ProbeRound.MaxBytes, null), (rounds[offset].Parts, rounds[offset].BytesRead, rounds[offset].Aborted));
        }

        Assert.NotSame(rounds[0], rounds[2]);
        Assert.NotSame(rounds[2], rounds[4]);
        Assert.True(Relocator.Recovery.IsRecovering(Drive));
    }

    [Fact]
    public void UnreadableBaselineStillRelocatesWithTheFixedDelay()
    {
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        _w.ProbeMbps = (drive, lcn, _) =>
        {
            _probed.Add((drive, lcn));
            return null; // 읽기 실패 (취소·바쁨 아님)
        };

        Tick();
        _w.Now += Step;
        RunUntilDone();
        _probed.Clear();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Run(TimeSpan.FromMinutes(14));
        Assert.True(Relocator.Recovery.IsRecovering(Drive));
        Run(TimeSpan.FromMinutes(2));
        Assert.False(Relocator.Recovery.IsRecovering(Drive));
        Assert.Empty(_probed); // 기준이 없으니 표본 없이 고정 대기
    }

    [Fact]
    public void ProbeModeThatCannotMeasureABaselineFallsBackToTheFixedMinutes()
    {
        SetAuto(a => a with { PostRelocateRecovery = AutoSettings.RecoveryProbe }); // 기준 구간을 못 찾는 환경 (기본 가짜)
        Tick();
        _w.Now += Step;
        RunUntilDone();

        Run(TimeSpan.FromMinutes(14));
        Assert.True(Relocator.Recovery.IsRecovering(Drive));
        Run(TimeSpan.FromMinutes(2));
        Assert.False(Relocator.Recovery.IsRecovering(Drive));
    }

    [Fact]
    public void RecoveringDriveBlocksTheNextMoveButNotPrewarm()
    {
        UsePrewarm();
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        StopAfterWriting();
        for (int i = 0; i < 40 && _status.Get("bench")?.Detail?.Contains("write limit reached", StringComparison.Ordinal) != true; i++)
        {
            Tick();
            _w.Now += Step;
        }

        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        int applies = ApplyCalls;
        int streams = Streams;

        Run(TimeSpan.FromMinutes(10));
        Assert.Equal(applies, ApplyCalls); // 회복 전에는 이어하기도 시작하지 않음
        Assert.Contains(AutoRelocator.DriveRecovering, Entry.Detail);

        _w.FileTimes[AutoTracer.RawTracePath(_game)] = _w.Now; // 새 트레이스는 회복 중에도 예열함
        Tick();
        Assert.Equal(streams + 1, Streams);

        (_zoneMbps, _refMbps) = (98, 72);
        Run(TimeSpan.FromMinutes(6));
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage); // 회복되면 이어서 끝냄
        Assert.True(ApplyCalls > applies);
    }

    [Fact]
    public void UpdateDelayWithoutAnyEarlierBaselineStaysTheFixedMinutesEvenInProbeMode()
    {
        SetAuto(a => a with { PostRelocateRecovery = AutoSettings.RecoveryProbe });
        DoneAndReadyToReplan(out _);
        Run(TimeSpan.FromMinutes(20)); // 첫 재배치의 고정 대기가 끝나게
        Update();
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        _w.Files.Add(AutoTracer.BootTracePath(_game));

        for (int i = 0; i < 29; i++)
        {
            _w.Now += Step;
            Tick();
        }

        Assert.False(File.Exists(_game.Plan)); // 14 분 30 초: 기준이 없으니 고정 15 분
        Assert.Single(_log.Lines, l => l.Contains("bench: waiting 15 min after update (drive may still be reorganizing)", StringComparison.Ordinal)); // 기준이 없으면 probe 모드여도 고정 분 문구
        _w.Now += Step;
        RunUntilDone();
        Assert.Equal(AutoStage.Done, Entry.Stage);
    }

    [Fact]
    public void UpdateDelayWithABaselineFollowsTheProbeInsteadOfTheFixedMinutes()
    {
        UseProbe(AutoSettings.RecoveryProbe, zoneAllocated: true);
        DoneAndReadyToReplan(out _);
        Run(TimeSpan.FromMinutes(6)); // 첫 재배치 뒤 회복
        Assert.False(Relocator.Recovery.IsRecovering(Drive));
        Update();
        _w.Now += AutoRelocator.StaleCheckEvery;
        _zoneMbps = 30; // 업데이트가 흩어 쓴 뒤 느림
        Tick();
        _w.Files.Add(AutoTracer.BootTracePath(_game));

        Run(TimeSpan.FromMinutes(20)); // 고정 15 분이 지나도 기다림
        Assert.False(File.Exists(_game.Plan));
        Assert.Single(_log.Lines, l => l.Contains("bench: waiting after update until the drive has recovered (up to 60 min)", StringComparison.Ordinal)); // probe 판정을 따를 때는 고정 분이 아니라 회복 확인 문구 (상한 recovery_max_min)
        Assert.DoesNotContain(_log.Lines, l => l.Contains("waiting 15 min after update", StringComparison.Ordinal));

        _zoneMbps = 98;
        Run(TimeSpan.FromMinutes(6));
        Tick();

        Assert.True(File.Exists(_game.Plan));
    }
    // ---- 정기 재적용 게이트: 자동 재배치와 같은 조건 ----

    // 자동 상태 항목이 있는 게임을 재배치를 끝낸 게임으로 만듭니다.
    private void MarkRelocated() => _status.Complete(_game.Name, _game.Source, _w.Now, counted: false, "done");

    private ReapplyVerdict? GateAfterIdle()
    {
        _ = Relocator.ReapplyBlock(_game); // 첫 표본이 유휴 시작
        _w.Now += AutoSettings.Default.IdleWindow + TimeSpan.FromMinutes(3);
        return Relocator.ReapplyBlock(_game);
    }

    [Fact]
    public void ReapplyGateNeedsTheIdleWindowThenPasses()
    {
        Assert.Equal(ReapplyVerdict.NotIdle, Relocator.ReapplyBlock(_game));
        _w.Now += TimeSpan.FromMinutes(5); // ResumeRetry 보다 긴 검사 공백이어도 30 분 유휴 기록은 이어짐

        Assert.Null(Relocator.ReapplyBlock(_game));
    }

    [Fact]
    public void ReapplyGateDefersWhileAnotherRegisteredGameOnTheSameDiskRuns()
    {
        var other = new GameSettings("other", ["other.exe"], "t.json", "p.json", Root, [Root], "GAMES", 1440);
        _settings = _settings with { Games = [_game, other] };
        _w.RunningExes.Add("other.exe");

        Assert.Equal(ReapplyVerdict.GameRunning, GateAfterIdle());
    }

    [Fact]
    public void ReapplyGateDefersWhileTheDriveIsRecovering()
    {
        SetAuto(a => a with { PostRelocateRecovery = AutoSettings.RecoveryFixed });
        Relocator.NoteReapplyStarted(_game, 1 << 20);
        Relocator.NoteReapplied(_game, 1 << 20);

        Assert.Equal(ReapplyVerdict.Recovering, GateAfterIdle());
    }

    [Fact]
    public void ReapplyGateDefersWhenTheDailyBytesBudgetIsUsed()
    {
        _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1), 8L << 30);

        Assert.Equal(ReapplyVerdict.DailyLimit, GateAfterIdle());
    }

    [Fact]
    public void ReapplyGateDefersWhenTheDailyCountLimitIsReached()
    {
        SetAuto(a => a with { MaxRelocateGibPerDay = 0 });
        for (int i = 0; i < _settings.Auto.MaxRelocationsPerDay; i++)
        {
            _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1), 64L << 20);
        }

        Assert.Equal(ReapplyVerdict.DailyLimit, GateAfterIdle());
    }

    [Fact]
    public void ReapplyGateDefersOnBattery()
    {
        _w.Power = false;

        Assert.Equal(ReapplyVerdict.OnBattery, GateAfterIdle());
    }

    [Fact]
    public void ReapplyGateDefersOnASystemHdd()
    {
        (_w.SystemDrive, _w.Hdd[Letter]) = (Letter, true);

        Assert.Equal(ReapplyVerdict.NotRelocatable, GateAfterIdle());
    }

    [Fact]
    public void ReapplyGateDefersRightAfterAGameUpdate()
    {
        SetAuto(a => a with { PostRelocateRecovery = AutoSettings.RecoveryOff });
        MarkRelocated();
        _status.Set("bench", "auto", null, AutoStage.Discovered, "game updated, will trace again on next run");

        Assert.Equal(ReapplyVerdict.PostUpdateDelay, GateAfterIdle());
    }

    [Fact]
    public void ReappliedBytesCountAgainstTheDailyBudgetAndStartRecovery()
    {
        SetAuto(a => a with { PostRelocateRecovery = AutoSettings.RecoveryFixed });

        Relocator.NoteReapplyStarted(_game, 500);
        Relocator.NoteReapplied(_game, (64L << 20) + 123);

        Assert.Equal((1, (64L << 20) + 123), (_status.RelocationsWithin(AutoRelocator.DailyWindow), _status.RelocationBytesWithin(AutoRelocator.DailyWindow)));
        Assert.True(Relocator.Recovery.IsRecovering(Letter));
    }

    [Fact]
    public void ReapplyOptionsUseTheSmallerOfTheRunCapAndTheRemainingBudgetOnSmr()
    {
        _w.IsSmr = _ => true;
        Assert.Equal(1L << 30, Relocator.ReapplyOptionsFor(_game).WriteCap); // 회당 1 GiB (예산 2 GiB 가 남음)

        _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1), (2L << 30) - (1L << 29));
        Assert.Equal(1L << 29, Relocator.ReapplyOptionsFor(_game).WriteCap);

        _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1), 1L << 30);
        Assert.Equal(0, Relocator.ReapplyOptionsFor(_game).WriteCap);
        Assert.Equal(_game.Plan, Relocator.ReapplyOptionsFor(_game).SaveTo);
    }

    [Fact]
    public void ReappliedReplacesThePlannedBytesWithTheActualMovedBytes()
    {
        Relocator.NoteReapplyStarted(_game, 200L << 20);
        Relocator.NoteReapplied(_game, 64L << 20);

        Assert.Equal((1, 64L << 20), (_status.RelocationsWithin(AutoRelocator.DailyWindow), _status.RelocationBytesWithin(AutoRelocator.DailyWindow)));
    }

    [Fact]
    public void LayoutWarnIsAllowedOnlyAfterACompleteRelocationOutsideExtension()
    {
        Assert.False(Relocator.LayoutWarnAllowed(_game)); // 재배치 기록 없음

        _status.Complete(_game.Name, _game.Source, _w.Now, counted: false, "done");
        Assert.True(Relocator.LayoutWarnAllowed(_game));

        _w.Files.Add(_game.Plan + AutoRelocator.ExtendSuffix);
        Assert.False(Relocator.LayoutWarnAllowed(_game)); // 확장 중
        _w.Files.Remove(_game.Plan + AutoRelocator.ExtendSuffix);

        _status.Set(_game.Name, _game.Source, null, AutoStage.WaitingIdle, "write limit reached, will continue");
        Assert.False(Relocator.LayoutWarnAllowed(_game)); // 쓰기 상한으로 멈춘 부분 적용
    }

    // ---- 존 확장 재계획 (플레이를 더 모아 병합한 트레이스) ----

    private static IEnumerable<(string, long)> OldBlocks(string f1, string f2) => [(f1, 12), (f2, 0), (f1, 0), (f1, 4), (f2, 8)];

    // 첫 기록 5 블록 뒤에 extra 가 이어지는 병합 트레이스 (run1 의 순서가 앞).
    private List<TraceEvent> Merged(IEnumerable<(string, long)> extra) => Run([.. OldBlocks(F1, F2), .. extra]);

    // 두 파일의 나머지 모든 블록 (총 32 블록).
    private IEnumerable<(string, long)> RestOfTheBlocks() =>
        Enumerable.Range(0, 16).SelectMany(i => new[] { (F1, (long)i), (F2, (long)i) }).Except(OldBlocks(F1, F2));

    // 재배치를 끝낸 게임에 두 번째 플레이가 병합돼 트레이스가 계획보다 새로운 상태.
    private long DoneThenMergedTrace(List<TraceEvent> merged, int minMib = 1)
    {
        long start = DoneAndReadyToReplan(out string raw);
        SetAuto(a => a with { TraceRuns = 3, TraceMergeMinMib = minMib, PostRelocateRecovery = AutoSettings.RecoveryOff });
        Sessions = [merged];
        _w.Files.Add(TraceWriter.RunPath(raw, 1));
        _w.Files.Add(TraceWriter.RunPath(raw, 2));
        _w.FileTimes[_game.Plan] = _w.Now - TimeSpan.FromHours(1);
        _w.FileTimes[raw] = _w.Now;
        Vol.Moves.Clear();
        return start;
    }

    [Fact]
    public void MergedTraceGrowthBelowTheMinimumDoesNotReplan()
    {
        DoneThenMergedTrace(Merged([(F1, 1), (F1, 2)])); // 128 KiB 증가, 기준 1 MiB

        Tick();
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotNull(Entry.RelocatedAt);
        Assert.True(File.Exists(_game.Plan));
        Assert.Empty(_w.Moved);
        Assert.Empty(Vol.Moves);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("extend", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MergedTraceGrowthExtendsTheZoneByAppendingOnlyNewBlocksAfterTheExistingOnes()
    {
        ReportFullMoves();
        long start = DoneThenMergedTrace(Merged(RestOfTheBlocks()));
        PlanFile before = PlanFile.Load(_game.Plan);
        long usedBefore = (long)before.Stats["zone_used_clusters"]!;
        int daily = _status.RelocationsWithin(AutoRelocator.DailyWindow);

        Tick();

        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
        Assert.True(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix)); // 확장 중 표시 (이전 계획)
        PlanFile planned = PlanFile.Load(_game.Plan);
        Assert.Equal(start, planned.Zone.StartLcn); // 같은 존 시작
        Assert.Equal(5L, before.Stats["hot_blocks"]);
        Assert.Equal(32L, planned.Stats["hot_blocks"]);
        Assert.All(planned.Tasks, t => Assert.True(t.TargetLcn >= start + usedBefore)); // 이미 존에 있는 블록 (접두) 은 작업이 없음
        Assert.Equal(before.Stats["hot_bytes"], planned.Stats["in_place_bytes"]);

        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotEmpty(Vol.Moves);
        Assert.All(Vol.Moves, m => Assert.True(m.Lcn >= start + usedBefore)); // 실제 이동도 존 뒤쪽으로만
        Assert.True(Verify(PlanFile.Load(_game.Plan)).Contiguous);
        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix) || _w.Files.Contains(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Equal(5L, PlanFile.Load(_game.Plan + ".stale").Stats["hot_blocks"]); // 이전 계획은 일반 .stale 자리로
        Assert.Contains(_log.Lines, l => l.Contains("zone extended by 1 MiB (run 2)", StringComparison.Ordinal));
        Assert.Equal(daily + 1, _status.RelocationsWithin(AutoRelocator.DailyWindow)); // 일반 재배치와 같이 일일 집계에 듦
        Assert.Equal([start], _w.PlanStarts); // 이전 존 시작에 맞춰 한 번만 계획함
    }

    [Fact]
    public void ExtensionBeyondMaxZoneKeepsTheEarliestRunBlocksAndLogsTheOverflow()
    {
        SetAuto(a => a with { TraceRuns = 3 });
        _settings = _settings with { MaxZoneMib = 1 }; // 병합 트레이스는 2 MiB (32 블록), 존은 16 블록까지
        long start = DoneThenMergedTrace(Merged(RestOfTheBlocks()), minMib: 0);
        PlanFile before = PlanFile.Load(_game.Plan);
        long usedBefore = (long)before.Stats["zone_used_clusters"]!;

        Tick();
        RunUntilDone();

        PlanFile after = PlanFile.Load(_game.Plan);
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal((16L, 256L), ((long)after.Stats["hot_blocks"]!, after.Zone.Clusters));
        Assert.Equal(before.Stats["hot_bytes"], after.Stats["in_place_bytes"]); // run1 의 블록은 그대로 앞에
        Assert.All(after.Tasks, t => Assert.True(t.TargetLcn >= start + usedBefore));
        Assert.DoesNotContain(after.Order, o => o.Path == F2 && o.Vcn <= 240 && 240 < o.Vcn + o.Clusters); // 가장 늦게 본 블록이 잘림
        Assert.Contains(_log.Lines, l => l.Contains("overflow 1 MiB dropped", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtensionWaitsForTheGameToStopAndPassesTheSameGatesAsAnyRelocation()
    {
        ReportFullMoves();
        DoneThenMergedTrace(Merged(RestOfTheBlocks()));
        _w.Running = true;
        Tick();
        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix)); // 게임이 도는 동안은 계획에 손대지 않음
        Assert.Equal(AutoStage.Done, Entry.Stage);

        _w.Running = false;
        SetAuto(a => a with { MaxRelocationsPerDay = 1 }); // 첫 재배치가 이미 하루 상한을 채움
        for (int i = 0; i < 6; i++)
        {
            Tick();
            _w.Now += TimeSpan.FromMinutes(5);
        }

        Assert.Empty(Vol.Moves);
        Assert.Equal((AutoStage.WaitingIdle, "daily limit reached, trace 2/3"), (Entry.Stage, Entry.Detail));

        _w.Now += AutoRelocator.DailyWindow;
        RunUntilDone();
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotEmpty(Vol.Moves);
    }

    [Fact]
    public void BlockedExtensionRestoresThePreviousPlanKeepsDoneAndDoesNotRetryTheSameTrace()
    {
        long start = DoneThenMergedTrace(Merged(RestOfTheBlocks()));
        PlanFile before = PlanFile.Load(_game.Plan);
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) => options.DryRun && plan.Zone.StartLcn is not null
            ? throw new PlanRejectedException("source_changed: " + F1) // 존 뒤가 비어 있지 않은 것 (zone_not_free) 은 새 존으로 가므로 그 밖의 거부
            : inner(g, plan, options, ct);

        Tick();
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotNull(Entry.RelocatedAt);
        Assert.Empty(Vol.Moves); // 아무것도 옮기지 않음
        Assert.Equal(before.Sha(), PlanFile.Load(_game.Plan).Sha()); // 이전 계획이 되살아남
        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix) || _w.Files.Contains(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Single(_log.Lines, l => l.Contains("zone extension blocked: source_changed", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("looking for a new one", StringComparison.Ordinal)); // 전체 재배치로 떨어지지 않음
        Assert.Equal([start], _w.PlanStarts);

        for (int i = 0; i < 3; i++)
        {
            _w.Now += AutoRelocator.StaleCheckEvery;
            Tick();
        }

        Assert.Equal([start], _w.PlanStarts); // 같은 트레이스로는 다시 시도하지 않음
        Assert.Equal(AutoStage.Done, Entry.Stage);

        _w.FileTimes[AutoTracer.RawTracePath(_game)] = _w.Now; // 다음 플레이가 쌓여 트레이스가 바뀜
        Tick();
        RunUntilDone();
        Assert.Equal([start, start], _w.PlanStarts);
        Assert.Equal(2, _log.Lines.Count(l => l.Contains("zone extension blocked", StringComparison.Ordinal)));
        Assert.Equal(AutoStage.Done, Entry.Stage);
    }

    [Fact]
    public void ExtensionKeepsTheBlocksAlreadyInTheZoneInTheirPhysicalOrderEvenWhenTheNewAccessOrderIsReversed()
    {
        // 실제 로그 시나리오: 병합된 트레이스에서 존 안의 기존 블록 순서가 새 접근 순서와 반대 (예전에는 순환으로 확장이 차단됨)
        long start = DoneThenMergedTrace(Run([.. OldBlocks(F1, F2).Reverse(), .. RestOfTheBlocks()]));
        long usedBefore = (long)PlanFile.Load(_game.Plan).Stats["zone_used_clusters"]!;

        Tick();

        Assert.True(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("zone extension blocked", StringComparison.Ordinal));
        Assert.All(PlanFile.Load(_game.Plan).Tasks, t => Assert.True(t.TargetLcn >= start + usedBefore)); // 기존 블록 이동 작업 0, 새 블록만 뒤에

        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotEmpty(Vol.Moves);
        Assert.All(Vol.Moves, m => Assert.True(m.Lcn >= start + usedBefore));
        Assert.Contains(_log.Lines, l => l.Contains("zone extended by 1 MiB (run 2)", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtensionThatAlsoDropsAVolatileFileFromTheOldPlanKeepsTheRemainingBlocksInPlace()
    {
        long start = DoneThenMergedTrace(Run([.. OldBlocks(F1, F2).Reverse(), .. RestOfTheBlocks(), (Path.Combine(Root, "settings.json"), 0L)]));
        long usedBefore = (long)PlanFile.Load(_game.Plan).Stats["zone_used_clusters"]!;

        Tick();
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        PlanFile fresh = PlanFile.Load(_game.Plan);
        Assert.DoesNotContain(fresh.Order, o => VolatileFiles.Ignored(o.Path));
        Assert.Equal(start, fresh.Zone.StartLcn);
        Assert.All(Vol.Moves, m => Assert.True(m.Lcn >= start + usedBefore));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("zone extension blocked", StringComparison.Ordinal));
    }

    [Fact]
    public void ExtensionPlanFailureLogsTheExceptionMessageFirstLineAndStillRestoresThePreviousPlan()
    {
        DoneThenMergedTrace(Merged(RestOfTheBlocks()));
        PlanFile before = PlanFile.Load(_game.Plan);
        _w.MakePlanAt = (_, _, _, _, _) => throw new PyValueException("expect 1..32 sessions\nsecond line");

        Tick();
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Single(_log.Lines, l => l.Contains("zone extension blocked: plan failed: PyValueException: expect 1..32 sessions", StringComparison.Ordinal) && !l.Contains("second line", StringComparison.Ordinal));
        Assert.Equal(before.Sha(), PlanFile.Load(_game.Plan).Sha());
    }

    [Fact]
    public void ExtensionBiggerThanTheDailyWriteBudgetIsAppliedInCappedSteps()
    {
        DoneThenMergedTrace(Merged(RestOfTheBlocks()));
        SetAuto(a => a with { MaxRelocateGibPerDay = 1 });
        _w.Now += AutoRelocator.DailyWindow + TimeSpan.FromHours(1); // 하루 창이 비어 있음
        var executed = new List<ApplyOptions>();
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            if (!options.DryRun)
            {
                executed.Add(options);
            }

            return options.DryRun ? new ApplyReport { TasksTotal = 1, TasksTodo = 1, BytesTodo = 3L << 30 } : real(g, plan, options, ct);
        };

        Tick();
        RunUntilDone();

        Assert.Equal(1L << 30, Assert.Single(executed).WriteCap); // 막지 않고 하루 상한까지만 씀
        Assert.DoesNotContain(_log.Lines, l => l.Contains("zone extension blocked", StringComparison.Ordinal));
    }

    [Fact]
    public void StatusSaysTraceIsUnavailableForAnAntiCheatGameInsteadOfWaitingForMorePlays()
    {
        DoneAndReadyToReplan(out string raw);
        SetAuto(a => a with { TraceRuns = 3 });
        _w.Files.Add(TraceWriter.RunPath(raw, 1));
        _w.Files.Add(TraceWriter.RunPath(raw, 2));

        Relocator.NoteProgress(_game with { AntiCheat = "vanguard" }, 1, 2);

        Assert.Equal("trace unavailable (vanguard), 2 earlier run(s) used, prewarming 1/2 MiB", Entry.Detail);
        Assert.Equal("트레이스 불가 (vanguard), 기존 기록 2회 사용", AsterCache.Tray.AutoStatusFile.StageExtra(new AsterCache.Tray.AutoGame("auto", null, Entry.Stage, "trace unavailable (vanguard), 2 earlier run(s) used", null, null), "ko")); // 영어·한국어 문구
        Assert.Equal("trace unavailable (vanguard), 2 earlier run(s) used", AsterCache.Tray.AutoStatusFile.StageExtra(new AsterCache.Tray.AutoGame("auto", null, Entry.Stage, "trace unavailable (vanguard), 2 earlier run(s) used", null, null), "en"));

        // 금지가 아니면 예전처럼 회차 표시
        Relocator.NoteProgress(_game, 1, 2);
        Assert.Equal("trace 2/3, prewarming 1/2 MiB", Entry.Detail);
    }

    [Fact]
    public void StatusShowsHowManyPlaysAreRecordedWhileMoreAreStillToCome()
    {
        DoneAndReadyToReplan(out string raw);
        SetAuto(a => a with { TraceRuns = 3 });
        _w.Files.Add(TraceWriter.RunPath(raw, 1));
        _w.Files.Add(TraceWriter.RunPath(raw, 2));

        Relocator.NoteProgress(_game, 1, 2);
        Assert.Equal("trace 2/3, prewarming 1/2 MiB", Entry.Detail);

        SetAuto(a => a with { TraceRuns = 2 }); // 다 모았으면 표시 없음
        Relocator.NoteProgress(_game, 1, 2);
        Assert.Equal("prewarming 1/2 MiB", Entry.Detail);
    }

    // ---- 병합 지문·존 상한 변경 재계획 (계획 stats: effective_zone_mib, merge_policy, trace_fingerprint) ----

    private static readonly string[] NoteKeys = ["effective_zone_mib", "merge_policy", "trace_fingerprint"];

    // 계획에서 메타 키를 지워 (옛 계획) 다시 저장: 계획 지문 (Sha) 은 그대로.
    private void StripNotes(params string[] keys)
    {
        PlanFile plan = PlanFile.Load(_game.Plan);
        var stats = new JObject();
        foreach ((string key, object? value) in plan.Stats.Where(kv => !keys.Contains(kv.Key)))
        {
            stats.Add(key, value);
        }

        (plan with { Stats = stats }).Save(_game.Plan);
    }

    // 트레이스는 계획보다 오래된 상태 (새 플레이가 병합된 것이 아님): 존 상한·정책 검사만 가능.
    private void TraceOlderThanThePlan(string raw)
    {
        _w.FileTimes[_game.Plan] = _w.Now;
        _w.FileTimes[raw] = _w.Now - TimeSpan.FromHours(1);
    }

    private int ExtendLogs() => _log.Lines.Count(l => l.Contains("set aside as .extend", StringComparison.Ordinal));

    [Fact]
    public void PlansRecordTheZoneLimitMergePolicyAndFingerprintOfTheirTrace()
    {
        _w.TraceFingerprint = _ => "fp-1";

        DoneAndReadyToReplan(out _);

        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.Equal(2048L, plan.Stats["effective_zone_mib"]);
        Assert.Equal(AutoTracer.MergePolicy, plan.Stats["merge_policy"]);
        Assert.Equal("fp-1", plan.Stats["trace_fingerprint"]);
    }

    [Fact]
    public void RaisingTheZoneLimitReplansOnceWhenTheTraceOutgrewThePlanAndNeverRepeats()
    {
        _settings = _settings with { MaxZoneMib = 1 };
        ReportFullMoves();
        long start = DoneAndReadyToReplan(out string raw); // 상한 1 MiB 로 만든 계획 (핫 5 블록)
        Assert.Equal(1L, PlanFile.Load(_game.Plan).Stats["effective_zone_mib"]);
        Sessions = [Merged(RestOfTheBlocks())]; // 트레이스는 이미 32 블록 (2 MiB)
        SetAuto(a => a with { TraceMergeMinMib = 1 });
        TraceOlderThanThePlan(raw);
        Tick();
        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix)); // 상한이 그대로면 아무 일도 없음
        _settings = _settings with { MaxZoneMib = 2 };

        Tick();

        Assert.True(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Contains(_log.Lines, l => l.Contains("zone limit 2 MiB", StringComparison.Ordinal));
        RunUntilDone();
        PlanFile after = PlanFile.Load(_game.Plan);
        Assert.Equal((32L, 2L), ((long)after.Stats["hot_blocks"]!, after.Stats["effective_zone_mib"]));
        Assert.Equal([start], _w.PlanStarts);
        Assert.Equal(1, ExtendLogs());

        for (int i = 0; i < 3; i++)
        {
            _w.Now += AutoRelocator.StaleCheckEvery;
            Tick();
        }

        _relocator = null; // 서비스 재시작
        Tick();
        Assert.Equal(1, ExtendLogs()); // 같은 값이면 반복하지 않음
        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Equal([start], _w.PlanStarts);
    }

    [Fact]
    public void LoweringTheZoneLimitBelowThePlansHotSetReplansOnceIntoTheSameZoneAndNeverRepeats()
    {
        _settings = _settings with { MaxZoneMib = 2 };
        Sessions = [Merged(RestOfTheBlocks())]; // 32 블록 (2 MiB) 을 그대로 담은 계획
        ReportFullMoves();
        long start = DoneAndReadyToReplan(out string raw);
        Assert.Equal((32L, 2L), ((long)PlanFile.Load(_game.Plan).Stats["hot_blocks"]!, PlanFile.Load(_game.Plan).Stats["effective_zone_mib"]));
        SetAuto(a => a with { TraceMergeMinMib = 1 });
        TraceOlderThanThePlan(raw);
        _settings = _settings with { MaxZoneMib = 1 }; // 계획의 핫셋이 새 상한을 넘음

        Tick();

        Assert.True(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Equal(2L, PlanFile.Load(_game.Plan + AutoRelocator.ExtendSuffix).Stats["effective_zone_mib"]); // 메타만 새 상한으로 적지 않음
        Assert.Contains(_log.Lines, l => l.Contains("above the zone limit 1 MiB", StringComparison.Ordinal));

        RunUntilDone();

        PlanFile after = PlanFile.Load(_game.Plan);
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal((16L, 1L), ((long)after.Stats["hot_blocks"]!, after.Stats["effective_zone_mib"])); // 새 상한 안의 계획이 메타를 가짐
        Assert.Equal(start, after.Zone.StartLcn);
        Assert.Equal([start], _w.PlanStarts);
        Assert.Equal(1, ExtendLogs());
        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));

        for (int i = 0; i < 3; i++)
        {
            _w.Now += AutoRelocator.StaleCheckEvery;
            Tick();
        }

        _relocator = null; // 서비스 재시작
        Tick();

        Assert.Equal(1, ExtendLogs()); // 다음 틱에도 되풀이하지 않음
        Assert.Equal([start], _w.PlanStarts);
        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
    }

    [Fact]
    public void ALoweredOrUnchangedZoneLimitOrAPlanThatIsNotOutgrownNeverReplans()
    {
        DoneAndReadyToReplan(out string raw);
        Sessions = [Merged(RestOfTheBlocks())];
        SetAuto(a => a with { TraceMergeMinMib = 1 });
        TraceOlderThanThePlan(raw);
        _relocator = null;

        Tick(); // 상한·정책이 계획과 같음: 트레이스가 더 커도 새 플레이가 병합되기 전에는 다시 보지 않음
        _settings = _settings with { MaxZoneMib = 1 }; // 낮춤: 늘어날 일이 없음
        Tick();

        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(1L, PlanFile.Load(_game.Plan).Stats["effective_zone_mib"]); // 지금 상한을 계획에 적어 둠
        Assert.Empty(_w.PlanStarts);
    }

    [Fact]
    public void APlanWithoutNotesIsOnlyNotedWhenTheTraceDidNotOutgrowItAndKeepsItsPlanHash()
    {
        _w.TraceFingerprint = _ => "fp-now";
        DoneAndReadyToReplan(out string raw);
        string sha = PlanFile.Load(_game.Plan).Sha();
        StripNotes(NoteKeys);
        TraceOlderThanThePlan(raw);
        _relocator = null;

        Tick();

        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Equal(AutoStage.Done, Entry.Stage);
        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.Equal((2048L, AutoTracer.MergePolicy, "fp-now"), (plan.Stats["effective_zone_mib"], plan.Stats["merge_policy"], plan.Stats["trace_fingerprint"]));
        Assert.Equal(sha, plan.Sha());
        Assert.Empty(_w.Moved);
        Assert.Contains(_log.Lines, l => l.Contains("plan notes recorded", StringComparison.Ordinal));
        Tick();
        Assert.Single(_log.Lines, l => l.Contains("plan notes recorded", StringComparison.Ordinal)); // 한 번만
    }

    [Fact]
    public void APlanWithoutNotesIsReplannedOnceWhenTheTraceOutgrewIt()
    {
        ReportFullMoves();
        long start = DoneAndReadyToReplan(out string raw);
        StripNotes(NoteKeys);
        Sessions = [Merged(RestOfTheBlocks())];
        SetAuto(a => a with { TraceMergeMinMib = 1 });
        TraceOlderThanThePlan(raw);
        _relocator = null;

        Tick();
        RunUntilDone();

        Assert.Equal(32L, PlanFile.Load(_game.Plan).Stats["hot_blocks"]);
        Assert.Equal(2048L, PlanFile.Load(_game.Plan).Stats["effective_zone_mib"]);
        Assert.Equal([start], _w.PlanStarts);
        _relocator = null;
        Tick();
        Assert.Equal(1, ExtendLogs());
    }

    [Fact]
    public void AChangedMergeFingerprintReplansEvenWhenTheTraceDidNotGrow()
    {
        _w.TraceFingerprint = _ => "fp-a";
        ReportFullMoves();
        long start = DoneThenMergedTrace(Merged([])); // 같은 5 블록, 크기 증가 없음
        _w.TraceFingerprint = _ => "fp-b";

        Tick();

        Assert.True(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Contains(_log.Lines, l => l.Contains("blocks or order changed", StringComparison.Ordinal));
        RunUntilDone();
        Assert.Equal("fp-b", PlanFile.Load(_game.Plan).Stats["trace_fingerprint"]);
        Assert.Equal([start], _w.PlanStarts);
        TraceOlderThanThePlan(AutoTracer.RawTracePath(_game)); // 같은 트레이스
        _relocator = null;
        Tick();
        Assert.Equal(1, ExtendLogs());
    }

    [Fact]
    public void AZoneAlreadyFilledToTheLimitIsNotReplannedByAChangedMergeFingerprintButTheWarmListFollowsTheNewTrace()
    {
        _settings = _settings with { MaxZoneMib = 2 };
        Sessions = [Merged(RestOfTheBlocks())]; // 32 블록 (2 MiB) = 존 상한을 가득 채운 계획
        _w.TraceFingerprint = _ => "fp-a";
        ReportFullMoves();
        DoneThenMergedTrace(Merged(RestOfTheBlocks()));
        Assert.Equal(32L, PlanFile.Load(_game.Plan).Stats["hot_blocks"]);
        string sha = PlanFile.Load(_game.Plan).Sha();
        _w.TraceFingerprint = _ => "fp-b";

        Tick();

        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(sha, PlanFile.Load(_game.Plan).Sha()); // 이미 옮긴 배치를 건드리지 않음
        Assert.Empty(Vol.Moves);
        Assert.Equal(0, ExtendLogs());
        Assert.Contains(_log.Lines, l => l.Contains("prewarm trace refreshed from the merged trace", StringComparison.Ordinal));
    }

    [Fact]
    public void ASameMergeFingerprintDoesNotReplanWithoutGrowth()
    {
        _w.TraceFingerprint = _ => "fp-a";
        DoneThenMergedTrace(Merged([]));

        Tick();
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(0, ExtendLogs());
    }

    [Fact]
    public void AnOldPlanWithoutAFingerprintOnlyRecordsTheCurrentOneAndDoesNotReplan()
    {
        _w.TraceFingerprint = _ => "fp-a";
        DoneThenMergedTrace(Merged([]));
        StripNotes("trace_fingerprint");
        _w.TraceFingerprint = _ => "fp-b";

        Tick();

        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal("fp-b", PlanFile.Load(_game.Plan).Stats["trace_fingerprint"]);
        Assert.Equal(0, ExtendLogs());
    }

    [Fact]
    public void AnOldPlanWithVolatileHistoryButNoStableFingerprintStillReplansOnAChangedRawFingerprint()
    {
        _w.TraceFingerprint = _ => "fp-a";
        ReportFullMoves();
        DoneThenMergedTrace(Merged([])); // 같은 5 블록, 크기 증가 없음
        PlanFile plan = PlanFile.Load(_game.Plan);
        var stats = new JObject();
        foreach ((string key, object? value) in plan.Stats.Where(kv => kv.Key != "relocation_trace_fingerprint"))
        {
            stats.Add(key, value);
        }

        stats.Add(VolatileFiles.StatsKey, new JObject { { Path.Combine(Root, "cache_1_2.xpakdata"), "volatile file name" } });
        (plan with { Stats = stats }).Save(_game.Plan); // 가변 파일 이력이 있고 안정 지문이 없는 옛 계획
        _w.TraceFingerprint = _ => "fp-b";

        Tick();

        Assert.True(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Contains(_log.Lines, l => l.Contains("blocks or order changed", StringComparison.Ordinal));
    }

    [Fact]
    public void ABlockedExtensionNotesTheLimitOnTheRestoredPlanSoARestartDoesNotTryAgain()
    {
        _settings = _settings with { MaxZoneMib = 1 };
        long start = DoneAndReadyToReplan(out string raw);
        Sessions = [Merged(RestOfTheBlocks())];
        SetAuto(a => a with { TraceMergeMinMib = 1 });
        TraceOlderThanThePlan(raw);
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) => options.DryRun && plan.Zone.StartLcn is not null
            ? throw new PlanRejectedException("source_changed: " + F1)
            : inner(g, plan, options, ct);
        _settings = _settings with { MaxZoneMib = 2 };

        Tick();
        RunUntilDone();

        Assert.Equal([start], _w.PlanStarts);
        Assert.Single(_log.Lines, l => l.Contains("zone extension blocked", StringComparison.Ordinal));
        Assert.Equal(2L, PlanFile.Load(_game.Plan).Stats["effective_zone_mib"]);
        _relocator = null; // 서비스 재시작
        Tick();
        RunUntilDone();
        Assert.Equal([start], _w.PlanStarts);
        Assert.Single(_log.Lines, l => l.Contains("zone extension blocked", StringComparison.Ordinal));
    }

    [Fact]
    public void StatusShowsExtraTraceRunsWithTheCoverageWhileTheyAreStillWanted()
    {
        DoneAndReadyToReplan(out string raw);
        SetAuto(a => a with { TraceRuns = 3 });
        _w.Files.Add(TraceWriter.RunPath(raw, 1));
        _w.Files.Add(TraceWriter.RunPath(raw, 2));
        _w.Files.Add(TraceWriter.RunPath(raw, 3));
        File.WriteAllText(AutoTracer.MergeMetaPath(_game), "{\"run\": 3, \"new_mib\": 200}");
        _status.RecordCoverage("bench", 22);

        Relocator.NoteProgress(_game, 1, 2);
        Assert.Contains("trace 3/8 (coverage 22%)", Entry.Detail, StringComparison.Ordinal);
        Relocator.NoteProgress(_game, 2, 2); // 다시 붙일 때 옛 표시를 지움
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(Entry.Detail!, "trace 3/8"));

        File.WriteAllText(AutoTracer.MergeMetaPath(_game), "{\"run\": 3, \"new_mib\": 10}");
        Relocator.NoteProgress(_game, 1, 2);
        Assert.DoesNotContain("trace", Entry.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void GameUpdateSetsTheRunFilesAsideToo()
    {
        DoneAndReadyToReplan(out string raw);
        SetAuto(a => a with { TraceRuns = 3 });
        _w.Files.Add(TraceWriter.RunPath(raw, 1));
        _w.Files.Add(TraceWriter.RunPath(raw, 2));
        Update();
        _w.Now += AutoRelocator.StaleCheckEvery;

        Tick();

        Assert.Contains(TraceWriter.RunPath(raw, 1) + ".stale", _w.Files);
        Assert.Contains(TraceWriter.RunPath(raw, 2) + ".stale", _w.Files);
        Assert.DoesNotContain(TraceWriter.RunPath(raw, 1), _w.Files);
        Assert.Equal(0, AutoTracer.RunCount(_game, _w.Env().FileExists));
    }

    [Fact]
    public void ReapplyIsBlockedAsNotAppliedOnlyForAnUnfinishedAutoRelocationOrWhileExtending()
    {
        Assert.NotEqual(ReapplyVerdict.NotApplied, Relocator.ReapplyBlock(_game)); // 자동 상태 항목 없음 (수동 적용 게임)

        _status.Set(_game.Name, "auto", null, AutoStage.Discovered, "d");
        Assert.Equal(ReapplyVerdict.NotApplied, Relocator.ReapplyBlock(_game)); // 재배치 기록 없음 (확장 계획도 StartLcn 이 있어 적용됨으로 보일 수 있음)

        _status.Set(_game.Name, "auto", null, AutoStage.Skipped, "off");
        Assert.NotEqual(ReapplyVerdict.NotApplied, Relocator.ReapplyBlock(_game)); // 자동 재배치를 끈 게임

        MarkRelocated();
        Assert.NotEqual(ReapplyVerdict.NotApplied, Relocator.ReapplyBlock(_game)); // 적용 완료는 기존 게이트대로

        _w.Files.Add(_game.Plan + AutoRelocator.ExtendSuffix);
        Assert.Equal(ReapplyVerdict.NotApplied, Relocator.ReapplyBlock(_game)); // 확장 중
    }

    [Fact]
    public void ExtensionThatFailsTheDataCheckRestoresThePreviousPlanAndIsNotRetriedAfterARestart()
    {
        long start = DoneThenMergedTrace(Merged(RestOfTheBlocks()));
        PlanFile before = PlanFile.Load(_game.Plan);
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) => options.DryRun ? real(g, plan, options, ct) : throw new ShaMismatchException([F1]);

        Tick();
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotNull(Entry.RelocatedAt);
        Assert.Equal(before.Sha(), PlanFile.Load(_game.Plan).Sha());
        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix) || _w.Files.Contains(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Contains(_log.Lines, l => l.Contains("zone extension blocked: data check failed", StringComparison.Ordinal));

        _relocator = null; // 서비스 재시작
        for (int i = 0; i < 3; i++)
        {
            _w.Now += AutoRelocator.StaleCheckEvery;
            Tick();
        }

        Assert.Equal([start], _w.PlanStarts); // 복원한 계획의 수정 시각이 트레이스보다 새로워 다시 확장하지 않음
        Assert.Equal(AutoStage.Done, Entry.Stage);
    }

    [Fact]
    public void ExtensionWithFailedStepsRestoresThePreviousPlanInsteadOfFailing()
    {
        DoneThenMergedTrace(Merged(RestOfTheBlocks()));
        PlanFile before = PlanFile.Load(_game.Plan);
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            if (options.DryRun)
            {
                return real(g, plan, options, ct);
            }

            var rep = new ApplyReport { Executed = true };
            rep.Failed.Add(new FailedStep(0, F1, "locked"));
            return rep;
        };

        Tick();
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(before.Sha(), PlanFile.Load(_game.Plan).Sha());
        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix) || _w.Files.Contains(_game.Plan + AutoRelocator.ExtendSuffix));
    }

    [Fact]
    public void RelocationThatThrowsAfterTheMoveStillStartsTheRecoveryWait()
    {
        SetAuto(a => a with { PostRelocateRecovery = AutoSettings.RecoveryFixed });
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) => options.Execute ? throw new IOException("disk gone") : real(g, plan, options, ct);

        Run(TimeSpan.FromMinutes(3.5));

        Assert.Equal((AutoStage.Failed, "relocation failed: IOException"), (Entry.Stage, Entry.Detail));
        Assert.True(Relocator.Recovery.IsRecovering(Drive)); // 예외 경로에서도 예정 바이트로 회복 대기를 시작
    }

    // 확장 재배치가 데이터 검사 실패로 끝난 상태에서 되돌리기·저널 보관을 가짜로 바꿔 돌립니다. 보관은 실제 적용 이후 호출만 셈.
    private void FailExtensionWith(Func<GameSettings, int, CancellationToken, RevertReport?>? revert, Func<GameSettings, string?>? archive, out int archived)
    {
        DoneThenMergedTrace(Merged(RestOfTheBlocks()));
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        bool armed = false;
        int archives = 0;
        _w.Apply = (g, plan, options, ct) =>
        {
            if (!options.DryRun)
            {
                armed = true;
                throw new ShaMismatchException([F1]);
            }

            return real(g, plan, options, ct);
        };
        Func<GameSettings, string?> inner = _w.ArchiveJournal;
        _w.ArchiveJournal = g =>
        {
            archives += armed ? 1 : 0;
            return armed && archive is not null ? archive(g) : inner(g);
        };
        if (revert is not null)
        {
            Func<GameSettings, int> rows = _w.JournalRows;
            _w.JournalRows = g => armed ? int.MaxValue - 1 : rows(g); // 이번 적용이 쓴 줄이 있는 것으로 만들어 되돌리기가 불리게 함
            _w.Revert = revert;
        }

        Tick();
        RunUntilDone();
        archived = archives;
    }

    [Fact]
    public void ExtensionWhoseJournalCannotBeArchivedStaysFailedInsteadOfRestoring()
    {
        FailExtensionWith(null, _ => throw new IOException("locked"), out int archived);

        Assert.True(archived >= 1);
        Assert.Equal(AutoStage.Failed, Entry.Stage);
        Assert.Contains("journal could not be archived", Entry.Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix) || _w.Files.Contains(_game.Plan + AutoRelocator.ExtendSuffix)); // 이전 계획을 되살리지 않음
    }

    [Fact]
    public void ExtensionWhoseRevertIsCancelledKeepsTheJournalAndIsNotDone()
    {
        FailExtensionWith((_, _, _) => new RevertReport { Stopped = "cancelled" }, null, out int archived);

        Assert.Equal(0, archived); // 되돌리기를 끝내지 못했으니 저널을 보관하지 않음
        Assert.Equal(AutoStage.Failed, Entry.Stage);
        Assert.Contains("revert incomplete", Entry.Detail, StringComparison.Ordinal);
        Assert.True(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix) || _w.Files.Contains(_game.Plan + AutoRelocator.ExtendSuffix));
    }

    [Fact]
    public void ExtensionIgnoresOldPiecesInsideThePinnedPartOfTheZone()
    {
        long start = DoneThenMergedTrace(Merged([(F1, 1), (F1, 2)]), minMib: 0);
        _w.Extents = p => p == F1 ? [.. Vol.Ext[p], new Extent(9 * (Blk / Cb), start, 4)] : Vol.Ext[p]; // 핫이 아닌 F1 의 9 번 블록 조각이 이전 존 (고정 구간) 안에 있음

        Tick();

        Assert.True(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("zone extension blocked", StringComparison.Ordinal));
    }

    [Fact]
    public void TruncatedPlanWithAnExtendFileIsSetAsideAndThePreviousPlanIsRestored()
    {
        DoneAndReadyToReplan(out _);
        PlanFile good = PlanFile.Load(_game.Plan);
        File.Copy(_game.Plan, _game.Plan + AutoRelocator.ExtendSuffix);
        File.WriteAllText(_game.Plan, "{\"truncat");
        _status.ResetForReplan(_game.Name, "trace grew, extending zone"); // 재배치 기록을 지움 (확장·재계획이 시작된 상태)
        _status.Set(_game.Name, _game.Source, null, AutoStage.Failed, "relocation failed: JsonException");
        _relocator = null; // 서비스 재시작

        Tick();

        Assert.True(File.Exists(_game.Plan + AutoRelocator.BadSuffix));
        Assert.Equal(good.Sha(), PlanFile.Load(_game.Plan).Sha());
        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix) || _w.Files.Contains(_game.Plan + AutoRelocator.ExtendSuffix));
        Assert.Equal(AutoStage.Done, Entry.Stage);
    }

    [Fact]
    public void TruncatedPlanWithOnlyAStaleFileIsSetAsideAndTheStalePlanIsRestored()
    {
        DoneAndReadyToReplan(out _);
        PlanFile good = PlanFile.Load(_game.Plan);
        File.Copy(_game.Plan, _game.Plan + AutoRelocator.StaleSuffix);
        File.WriteAllText(_game.Plan, "{\"truncat");
        _status.ResetForReplan(_game.Name, "trace grew, extending zone"); // 재배치 기록을 지움 (확장·재계획이 시작된 상태)
        _status.Set(_game.Name, _game.Source, null, AutoStage.Failed, "relocation failed: JsonException");
        _relocator = null; // 서비스 재시작

        Tick();

        Assert.True(File.Exists(_game.Plan + AutoRelocator.BadSuffix));
        Assert.Equal(good.Sha(), PlanFile.Load(_game.Plan).Sha());
        Assert.Equal((AutoStage.Done, AutoRelocator.StalePlanDetail), (Entry.Stage, Entry.Detail));
    }

    [Fact]
    public void BadPlanSetAsideBeforeARestartIsReplacedByTheStalePlanWithoutTheInMemoryMark()
    {
        DoneAndReadyToReplan(out _);
        PlanFile good = PlanFile.Load(_game.Plan);
        File.Move(_game.Plan, _game.Plan + AutoRelocator.StaleSuffix);
        File.WriteAllText(_game.Plan + AutoRelocator.BadSuffix, "{\"truncat"); // 잘린 계획을 .bad 로 치운 직후 꺼진 상태
        _status.ResetForReplan(_game.Name, "trace grew, extending zone");
        _status.Set(_game.Name, _game.Source, null, AutoStage.WaitingIdle, "x");
        _relocator = null; // 서비스 재시작

        Tick();

        Assert.Equal(good.Sha(), PlanFile.Load(_game.Plan).Sha());
        Assert.Equal((AutoStage.Done, AutoRelocator.StalePlanDetail), (Entry.Stage, Entry.Detail));
    }

    [Theory]
    [InlineData(AutoStage.WaitingIdle)]
    [InlineData(AutoStage.Done)]
    [InlineData(AutoStage.Failed)]
    public void TruncatedPlanIsReplacedByTheStalePlanWhateverTheStateWas(string stage)
    {
        DoneAndReadyToReplan(out _);
        PlanFile good = PlanFile.Load(_game.Plan);
        File.Copy(_game.Plan, _game.Plan + AutoRelocator.StaleSuffix);
        File.WriteAllText(_game.Plan, "{\"truncat");
        if (stage != AutoStage.Done)
        {
            _status.ResetForReplan(_game.Name, "trace grew, extending zone");
            _status.Set(_game.Name, _game.Source, null, stage, "x");
        }

        _relocator = null; // 서비스 재시작

        Tick();

        Assert.True(File.Exists(_game.Plan + AutoRelocator.BadSuffix));
        Assert.Equal(good.Sha(), PlanFile.Load(_game.Plan).Sha()); // 계획 없는 상태로 남지 않음
        Assert.Equal((AutoStage.Done, AutoRelocator.StalePlanDetail), (Entry.Stage, Entry.Detail));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("an earlier replan was interrupted", StringComparison.Ordinal)); // 기존 복원 루프와 겹치지 않음
    }

    [Fact]
    public void ReapplyIsNotBlockedAsNotAppliedWhenAutoRelocationIsOff()
    {
        SetAuto(a => a with { Relocate = false });
        foreach (string stage in new[] { AutoStage.Discovered, AutoStage.WaitingIdle })
        {
            _status.Set(_game.Name, "auto", null, stage, "d");
            Assert.NotEqual(ReapplyVerdict.NotApplied, Relocator.ReapplyBlock(_game));
        }

        _w.Files.Add(_game.Plan + AutoRelocator.ExtendSuffix);
        Assert.Equal(ReapplyVerdict.NotApplied, Relocator.ReapplyBlock(_game)); // 확장 중은 그대로 막음
    }

    [Fact]
    public void ReapplyIsNotBlockedAsNotAppliedWhenAutoModeIsOff()
    {
        SetAuto(a => a with { Enabled = false });
        foreach (string stage in new[] { AutoStage.Discovered, AutoStage.WaitingIdle })
        {
            _status.Set(_game.Name, "auto", null, stage, "d");
            Assert.NotEqual(ReapplyVerdict.NotApplied, Relocator.ReapplyBlock(_game));
        }

        _w.Files.Add(_game.Plan + AutoRelocator.ExtendSuffix);
        Assert.Equal(ReapplyVerdict.NotApplied, Relocator.ReapplyBlock(_game)); // 확장 중은 그대로 막음
    }

    [Fact]
    public void FailedExtensionArchivesItsJournalSoTheRestoredPlanCanBeAppliedAgain()
    {
        DoneThenMergedTrace(Merged(RestOfTheBlocks()));
        PlanFile before = PlanFile.Load(_game.Plan);
        int archived = 0;
        _w.ArchiveJournal = _ => File.Exists(JournalPath) ? Archive(JournalPath + ".old" + archived++) : null; // 확장 시작 때 보관한 .old 와 이름이 겹치지 않게
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            ApplyReport rep = real(g, plan, options, ct); // 확장 계획의 저널을 남김
            if (options.Execute)
            {
                rep.Failed.Add(new FailedStep(0, F1, "locked"));
            }

            return rep;
        };

        Tick();
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(before.Sha(), PlanFile.Load(_game.Plan).Sha());
        Assert.False(File.Exists(JournalPath)); // 확장 계획의 저널은 옆으로 보관됨
        Assert.True(File.Exists(JournalPath + ".old1")); // 0 은 확장 시작 때, 1 은 확장 실패 때
        ApplyReport again = Applier.Apply(before, Vol, [Root], JournalPath, Hooks(), new ApplyOptions(Execute: true), default); // 저널 불일치로 거부되지 않음
        Assert.Empty(again.Failed);
    }

    private string Archive(string dest)
    {
        File.Move(JournalPath, dest);
        return dest;
    }

    [Fact]
    public void ThrownRelocationKeepsThePlannedBytesInTheDailyBudget()
    {
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) => options.DryRun ? inner(g, plan, options, ct) : throw new IOException("disk");

        Run(TimeSpan.FromMinutes(3.5));

        Assert.Equal(AutoStage.Failed, Entry.Stage);
        Assert.True(_status.RelocationBytesWithin(AutoRelocator.DailyWindow) > 0); // 시작 때 넣은 예정 바이트가 지워지지 않음
    }

    [Fact]
    public void ExplicitBudgetAboveTheSmrDefaultIsLoggedOncePerGame()
    {
        _w.IsSmr = _ => true;
        SetAuto(a => a with { MaxRelocateGibPerDay = 8, MaxRelocateGibPerDaySet = true });

        Tick();
        RunUntilDone();
        Tick();

        Assert.Single(_log.Lines, l => l.Contains("bench: max_relocate_gib_per_day 8 is above the SMR default 2; using the explicit value", StringComparison.Ordinal));
    }
}
