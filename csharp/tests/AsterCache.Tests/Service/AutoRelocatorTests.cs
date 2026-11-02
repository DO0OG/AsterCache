using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tests.Core;
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
public sealed class AutoRelocatorTests : ApplyTestBase
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
        _settings = new AsterSettings(50, [_game]);
        _status = new AutoStatus(Path.Combine(Root, "auto_status.json"), _log, () => _w.Now);
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
    }

    private int ApplyCalls { get; set; }

    private int RevertCalls { get; set; }

    private int LastSkipRows { get; set; } = -1;

    private int JournalRowCount()
    {
        using var jr = new Journal(JournalPath);
        return jr.Rows.Count;
    }

    private AutoRelocator Relocator => _relocator ??= new AutoRelocator(_w.Env(), _status, _log, () => _settings);

    private AutoGameEntry Entry => _status.Get("bench")!;

    private void Tick() => Relocator.Tick(_gate, CancellationToken.None);

    private void SetAuto(Func<AutoSettings, AutoSettings> change) => _settings = _settings with { Auto = change(_settings.Auto) };

    // 30 초 틱을 span 만큼 돌립니다 (첫 틱은 현재 시각).
    private void Run(TimeSpan span)
    {
        DateTimeOffset end = _w.Now + span;
        Tick();
        while (_w.Now < end)
        {
            _w.Now += Step;
            Tick();
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
    public void WholeFlowPlansWaitsForIdleMovesAndRecordsTheFirstRelocation()
    {
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
    [InlineData("cpu")]
    [InlineData("cpu-unknown")]
    [InlineData("power")]
    [InlineData("game")]
    public void AnyBusyConditionRestartsTheIdleWindow(string busy)
    {
        Run(TimeSpan.FromMinutes(2.5));
        Assert.Empty(Vol.Moves);

        switch (busy)
        {
            case "disk": _w.Disk = DiskActivityBytes() + 1; break;
            case "cpu": _w.Cpu = 20; break; // 20 % 이상이면 바쁨
            case "cpu-unknown": _w.Cpu = null; break;
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

    [Fact]
    public void DailyLimitHoldsTheNextRelocationUntilThe24HourWindowPasses()
    {
        SetAuto(a => a with { MaxRelocationsPerDay = 1 });
        _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1));

        Run(TimeSpan.FromMinutes(10));
        Assert.Equal((AutoStage.WaitingIdle, "daily limit reached"), (Entry.Stage, Entry.Detail));
        Assert.Empty(Vol.Moves);

        _w.Now += TimeSpan.FromHours(24);
        Run(TimeSpan.FromMinutes(3));
        Assert.Equal(AutoStage.Done, Entry.Stage);
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
        _settings = new AsterSettings(50, [_game, second]);
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
            case "system": _w.SystemDrive = Letter; break;
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
    public void DailyLimitStateIsLoggedOnceNotOnEveryTick()
    {
        SetAuto(a => a with { MaxRelocationsPerDay = 1 });
        _status.BeginRelocation(_w.Now - TimeSpan.FromHours(1));

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
        long firstStep = JournalRows().Where(r => Equals(r["type"], "step") && r.ContainsKey("from")).Select(r => (long)r["idx"]!).Min();
        _w.Apply = (_, plan, options, ct) => Applier.Apply(plan, Vol, [Root], JournalPath, Hooks((_, _, _) => "changed"), options, ct);

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

        var restarted = new AutoStatus(Path.Combine(Root, "auto_status.json"), _log, () => _w.Now);

        Assert.Equal(Entry, restarted.Get("bench"));
        var saved = (JObject)JsonRead.ReadFile(Path.Combine(Root, "auto_status.json"))!;
        Assert.Equal("relocated", ((JObject)((List<object?>)saved["events"]!)[0]!)["kind"]);
    }
}
