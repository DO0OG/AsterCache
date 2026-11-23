using AsterCache.Core.Apply;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 설정 auto.relocate_layout: 서비스가 어떤 계획을 만드는지, 예전 범위 계획이 적용된 게임을 통째 계획으로 이행하는 단계 (ReplanLegacyLayout) 와 그 뒤 이동.
public sealed partial class AutoRelocatorTests
{
    private readonly List<(long CapMib, long MaxFile, long? Start, long Used)> _wholeCalls = [];
    private readonly List<string> _switched = [];

    private void UseWholeFile(bool whole = true)
    {
        SetAuto(a => a with { RelocateLayout = whole ? RelocateLayouts.WholeFile : RelocateLayouts.Ranges });
        Sessions = [Run(Enumerable.Range(0, 10).Select(b => (F1, (long)b)).Concat(Enumerable.Range(0, 10).Select(b => (F2, (long)b))).ToArray())];
        _w.MakePlanWhole = (sessions, origin, zoneMib, capMib, maxFile, start, used) =>
        {
            _wholeCalls.Add((capMib, maxFile, start, used));
            return PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Files[p], zoneStartLcn: start, fixedClusters: used, capacityBytes: capMib << 20, maxFileBytes: maxFile);
        };
        _w.SwitchPlan = (g, plan, olds) =>
        {
            _switched.Add(g.Name);
            _ = PlanSwitch.Commit(plan, g.Plan, JournalPath, () => _w.Now.UtcDateTime, hooks: Hooks(), oldPlans: olds);
        };
    }

    // 예전 범위 계획으로 재배치를 마친 상태를 직접 만듦: 존이 정해진 범위 계획, 완료 상태, 트레이스는 있음.
    private PlanFile LegacyRangeDone(GameSettings? game = null, DateTimeOffset? played = null)
    {
        GameSettings g = game ?? _game;
        PlanFile plan = Make(20000);
        plan.Save(g.Plan);
        _w.Files.Add(AutoTracer.RawTracePath(g));
        _status.Complete(g.Name, "auto", _w.Now, counted: false, null);
        if (played is { } at)
        {
            _status.MarkPlayed(g.Name, "auto", AutoStage.Done, at);
        }

        return plan;
    }

    [Fact]
    public void TheDefaultSettingMakesARangePlanAndNeverCallsTheWholePlanFunction()
    {
        UseWholeFile(whole: false);

        Tick();

        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.Null(WholeFileLayout.WholeFiles(plan));
        Assert.Empty(_wholeCalls);
    }

    [Fact]
    public void TheWholeFileSettingMakesAWholePlanWithTheEffectiveZoneLimitAndTheDailyBudgetAsTheFileLimit()
    {
        UseWholeFile();

        Tick();

        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.Equal([F1, F2], WholeFileLayout.WholeFiles(plan));
        (long capMib, long maxFile, long? start, long used) = Assert.Single(_wholeCalls);
        Assert.Equal((2048L, 8L << 30, (long?)null, 0L), (capMib, maxFile, start, used)); // max_zone_mib 기본값, 하루 8 GiB
        Assert.Contains(_log.Lines, l => l.Contains("whole-file plan, 2 file(s) 2 MiB placed in the zone, 0 file(s) 0 MiB hot left in place (none), zone 2 MiB of 2048 MiB, 0 file(s) left in place because of the zone limit", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnlimitedDailyBudgetPassesNoFileSizeLimit()
    {
        UseWholeFile();
        SetAuto(a => a with { MaxRelocateGibPerDay = 0, MaxRelocateGibPerDaySet = true });

        Tick();

        Assert.Equal(long.MaxValue, Assert.Single(_wholeCalls).MaxFile);
    }

    [Fact]
    public void AWholeSettingWithoutAWholePlanFunctionStillMakesARangePlan()
    {
        UseWholeFile();
        _w.MakePlanWhole = null;

        Tick();

        Assert.Null(WholeFileLayout.WholeFiles(PlanFile.Load(_game.Plan)));
    }

    [Fact]
    public void AWholePlanWithNoFileThatQualifiesFallsBackToTheRangePlan()
    {
        UseWholeFile();
        Sessions = [Run((F1, 0), (F2, 0))]; // 핫이 파일의 1/16 뿐이라 둘 다 mostly_cold

        Tick();

        Assert.Single(_wholeCalls);
        Assert.Null(WholeFileLayout.WholeFiles(PlanFile.Load(_game.Plan)));
        Assert.Contains(_log.Lines, l => l.Contains("no file qualifies for whole-file placement, planning by ranges instead", StringComparison.Ordinal));
    }

    [Fact]
    public void ALegacyRangePlanIsRebuiltOnceAsAWholePlanThenMovedAndVerified()
    {
        UseWholeFile();
        PlanFile legacy = LegacyRangeDone();
        int moves = Vol.Moves.Count;

        Tick();

        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.Equal([F1, F2], WholeFileLayout.WholeFiles(plan));
        Assert.Null(plan.Zone.StartLcn); // 새 zone: 적용 때 정함
        Assert.Equal(legacy.Order, plan.Order);
        Assert.Equal(["bench"], _switched);
        Assert.Equal(moves, Vol.Moves.Count); // 교체는 파일을 옮기지 않음
        Assert.Null(Entry.RelocatedAt); // 완료가 아님: 이후 자동 재배치 흐름이 최근 플레이 순서로 이어받음
        Assert.NotEqual(AutoStage.Done, Entry.Stage);
        Assert.Contains(_log.Lines, l => l.Contains("plan rebuilt for whole-file placement, 2 file(s) 2 MiB whole, 0 left in place (none), 2 MiB to move; old zone pieces of in-place files stay where they are", StringComparison.Ordinal));

        Tick();
        Tick();
        Assert.Equal(["bench"], _switched); // 서비스 실행당 한 번

        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.True(Vol.Ext.Values.All(e => e.Count == 1));
        Assert.Contains(_log.Lines, l => l.Contains("auto relocation verified: contiguous", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("whole-file placement complete, 2 of 2 file(s) in one piece inside the zone", StringComparison.Ordinal));
        Assert.NotNull(PlanFile.Load(_game.Plan).Zone.StartLcn); // 존이 정해져 저장됨
    }

    [Fact]
    public void WithTheRangesSettingALegacyPlanIsNeverMigratedAndAnExistingWholePlanIsKept()
    {
        LegacyRangeDone();
        UseWholeFile(whole: false);

        Tick();
        Tick();

        Assert.Empty(_switched);
        Assert.Null(WholeFileLayout.WholeFiles(PlanFile.Load(_game.Plan)));
        Assert.Equal(AutoStage.Done, Entry.Stage);
    }

    [Fact]
    public void AnExistingWholePlanIsNotMigratedAgainEvenWhenTheSettingChangesBackAndForth()
    {
        UseWholeFile();
        Tick();
        RunUntilDone();
        Assert.Equal(AutoStage.Done, Entry.Stage);
        string sha = PlanFile.Load(_game.Plan).Sha();

        SetAuto(a => a with { RelocateLayout = RelocateLayouts.Ranges });
        _relocator = null;
        Tick();
        SetAuto(a => a with { RelocateLayout = RelocateLayouts.WholeFile });
        _relocator = null;
        Tick();

        Assert.Empty(_switched);
        Assert.Equal(sha, PlanFile.Load(_game.Plan).Sha());
    }

    [Theory]
    [InlineData("running")]
    [InlineData("relocate_off")]
    [InlineData("extending")]
    [InlineData("stale_file")]
    [InlineData("switch_file")]
    [InlineData("not_done")]
    [InlineData("no_trace")]
    public void TheMigrationDoesNothingWhileAConditionIsNotMet(string why)
    {
        UseWholeFile();
        LegacyRangeDone();
        switch (why)
        {
            case "running":
                _w.Running = true;
                break;
            case "relocate_off":
                SetAuto(a => a with { Relocate = false });
                break;
            case "extending":
                File.Copy(_game.Plan, _game.Plan + ".extend");
                break;
            case "stale_file":
                _w.Files.Add(_game.Plan + ".stale");
                break;
            case "switch_file":
                _w.Files.Add(_game.Plan + ".switch");
                break;
            case "not_done":
                _status.ResetForReplan(_game.Name, "x");
                break;
            case "no_trace":
                _w.Files.Remove(AutoTracer.RawTracePath(_game));
                break;
        }

        string sha = PlanFile.Load(_game.Plan).Sha();

        Tick();

        Assert.Empty(_switched);
        Assert.Equal(sha, PlanFile.Load(_game.Plan).Sha());
    }

    [Fact]
    public void ARunningGameIsCheckedAgainAfterItStopsAndNotMarkedAsCheckedWhileItRuns()
    {
        UseWholeFile();
        LegacyRangeDone();
        _w.Running = true;
        Tick();
        Assert.Empty(_switched);

        _w.Running = false;
        _w.Now += Step;
        Tick();

        Assert.Equal(["bench"], _switched);
    }

    [Fact]
    public void ALegacyPlanWhoseSourceFilesChangedIsLeftToTheUpdateReplan()
    {
        UseWholeFile();
        LegacyRangeDone();
        File.AppendAllText(F1, "x");

        Tick();

        Assert.Empty(_switched);
    }

    [Fact]
    public void AFileThatCannotBeReadDelaysTheMigrationUntilItCanAndThenItHappens()
    {
        UseWholeFile();
        LegacyRangeDone();
        _w.Extents = p => p == F2 ? null : Vol.Ext[p];

        Tick();

        Assert.Empty(_switched);
        Assert.Contains(_log.Lines, l => l.Contains("whole-file placement not planned yet, a file of the plan cannot be read", StringComparison.Ordinal));

        _w.Extents = p => Vol.Ext[p];
        _w.Now += AutoRelocator.TransientRetry + Step;
        Tick();

        Assert.Equal(["bench"], _switched);
    }

    [Fact]
    public void WhenNoFreeRunIsLongEnoughTheLimitIsLoweredAndTheLateFilesStayInPlace()
    {
        UseWholeFile();
        LegacyRangeDone();
        Vol.TotalClusters = 1000;
        Vol.Ext["blocker1"] = [new Extent(0, 250, 10)];
        Vol.Ext["blocker2"] = [new Extent(0, 500, 10)]; // 빈 구간 [0,250) [260,500) [510,1000): 가장 긴 것이 통째 합 (512) 보다 짧음
        _w.Bitmap = _ => Vol.Bitmap();

        Tick();

        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.Equal([F1], WholeFileLayout.WholeFiles(plan)); // 늦게 접근하는 F2 가 제자리로
        Assert.Equal(1L, ((AsterCache.Core.Json.JObject)plan.Stats[WholeFileLayout.InPlaceFilesKey]!)[WholeFileLayout.ZoneFullReason]);
        Assert.Contains(_log.Lines, l => l.Contains("limit lowered to", StringComparison.Ordinal));
        Assert.Equal(["bench"], _switched);
    }

    [Fact]
    public void WhenNoFreeRunHoldsAnyFileTheMigrationChangesNothing()
    {
        UseWholeFile();
        PlanFile legacy = LegacyRangeDone();
        Vol.TotalClusters = 100; // 빈 구간이 어떤 파일 (256) 도 못 담음
        _w.Bitmap = _ => Vol.Bitmap();

        Tick();

        Assert.Empty(_switched);
        Assert.Equal(legacy.Sha(), PlanFile.Load(_game.Plan).Sha());
        Assert.Contains(_log.Lines, l => l.Contains("no file qualifies for whole-file placement, plan unchanged", StringComparison.Ordinal)); // 한도를 가장 긴 빈 구간으로 줄이면 통째로 놓을 파일이 없음
    }

    [Fact]
    public void TheMigrationFollowsTheMostRecentlyPlayedGameFirst()
    {
        string plans = Path.Combine(Root, "plans");
        string plan2 = Path.Combine(plans, "other.plan.json");
        GameSettings other = new("other", ["other.exe"], plan2 + ".prefetch.json", plan2, Root, [Root], "GAMES", 1440) { Source = "auto" };
        _settings = _settings with { Games = [_game, other] };
        DateTimeOffset now = _w.Now;
        UseWholeFile();
        LegacyRangeDone(_game, now - TimeSpan.FromDays(3)); // 오래 전에 플레이
        LegacyRangeDone(other, now - TimeSpan.FromHours(1)); // 최근에 플레이

        Tick();

        Assert.Equal(["other", "bench"], _switched);
    }

    [Fact]
    public void AGameThatIsStillRelocatingWithARangePlanIsMigratedOnlyAfterItIsDone()
    {
        UseWholeFile(whole: false);
        Tick(); // 범위 계획을 만들고 재배치 시작 전
        Assert.NotEqual(AutoStage.Done, Entry.Stage);
        UseWholeFile();

        Tick();

        Assert.Empty(_switched); // 아직 완료가 아님: 서비스 실행 중 완료되면 그때 따짐
    }

    [Fact]
    public void ADeferredFileThatCannotFitTodaysBudgetPausesTheGameInsteadOfLoopingEveryTick()
    {
        UseWholeFile();
        SetAuto(a => a with { MaxRelocateGibPerDay = 1, MaxRelocateGibPerDaySet = true });
        Tick();
        _status.BeginRelocation(_w.Now, (1L << 30) - (1 << 19)); // 오늘 이미 1 GiB 에서 0.5 MiB 만 남기고 씀: 파일 하나 (1 MiB) 도 못 옮김

        for (int i = 0; i < 12; i++)
        {
            _w.Now += Step;
            Tick();
        }

        Assert.Empty(Vol.Moves);
        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Contains(_log.Lines, l => l.Contains("file(s) larger than the remaining daily write budget, retrying after", StringComparison.Ordinal));
        Assert.Equal(1, _log.Lines.Count(l => l.Contains("auto relocation starting", StringComparison.Ordinal))); // 한 번 시도하고 쉼: 틱마다 되풀이하지 않음
    }
}
