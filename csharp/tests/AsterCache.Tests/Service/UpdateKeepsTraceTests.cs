using AsterCache.Core.Plan;
using AsterCache.Service;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Service;

/// 트레이스 금지 게임 (다시 트레이스할 수 없음) 의 업데이트: 트레이스를 .stale 로 사장시키지 않고 유지한 채 다시 계획하고, 이미 사장된 트레이스는 시작 때 되살림.
public sealed partial class AutoRelocatorTests
{
    private const string KeptLine = "game updated; trace kept (tracing is not allowed for this game), re-planning";

    private void UseAntiCheat(string? kind)
    {
        _game = _game with { AntiCheat = kind };
        _settings = _settings with { Games = [_game] };
    }

    // 업데이트를 감지하는 틱까지.
    private void UpdateAndDetect()
    {
        Update();
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
    }

    [Fact]
    public void UpdateOfATraceForbiddenGameKeepsTheTraceAndPlansAgainIntoTheSameZone()
    {
        UseAntiCheat("vanguard");
        long start = DoneAndReadyToReplan(out string trace);
        _w.Files.Add(TraceWriter.RunPath(trace, 1));
        int daily = _status.RelocationsWithin(AutoRelocator.DailyWindow);

        UpdateAndDetect();

        Assert.Contains(trace, _w.Files); // 트레이스(와 run 파일)는 그대로
        Assert.Contains(TraceWriter.RunPath(trace, 1), _w.Files);
        Assert.DoesNotContain(trace + ".stale", _w.Files);
        Assert.DoesNotContain(TraceWriter.RunPath(trace, 1) + ".stale", _w.Files);
        Assert.False(File.Exists(_game.Plan));
        Assert.True(File.Exists(_game.Plan + ".stale")); // 이전 존 시작을 알려 주는 옛 계획
        Assert.Contains(_log.Lines, l => l.Contains(KeptLine, StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("source files changed since the plan (game update)", StringComparison.Ordinal));
        Assert.Equal(AutoStage.Discovered, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
        Assert.Equal(daily, _status.RelocationsWithin(AutoRelocator.DailyWindow));

        Run(TimeSpan.FromMinutes(20)); // 업데이트 뒤 지연 (15 분) 이 지나면 같은 트레이스로 다시 계획하고 재배치

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotNull(Entry.RelocatedAt);
        Assert.Equal([start], _w.PlanStarts);
        Assert.Equal(start, PlanFile.Load(_game.Plan).Zone.StartLcn);
        Assert.Contains(trace, _w.Files);
        Assert.Equal(1, _log.Lines.Count(l => l.Contains(KeptLine, StringComparison.Ordinal)));
    }

    [Fact]
    public void UpdateOfATraceForbiddenGameWhoseTraceHasNothingLeftFallsBackToTheStaleTrace()
    {
        UseAntiCheat("eac");
        DoneAndReadyToReplan(out string trace);
        _w.LoadSessions = () => [[]]; // 트레이스의 파일이 모두 없어짐: 읽으면 남는 블록 없음

        UpdateAndDetect();

        Assert.DoesNotContain(trace, _w.Files);
        Assert.Contains(trace + ".stale", _w.Files);
        Assert.Contains(_log.Lines, l => l.Contains("source files changed since the plan (game update)", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains(KeptLine, StringComparison.Ordinal));
        Assert.Equal(AutoStage.Discovered, Entry.Stage);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ricochet")]
    public void UpdateOfATraceAllowedGameStillSetsTheTraceAside(string? kind)
    {
        UseAntiCheat(kind);
        DoneAndReadyToReplan(out string trace);

        UpdateAndDetect();

        Assert.DoesNotContain(trace, _w.Files);
        Assert.Contains(trace + ".stale", _w.Files);
        Assert.DoesNotContain(_log.Lines, l => l.Contains(KeptLine, StringComparison.Ordinal));
    }

    // 업데이트가 트레이스를 .stale 로 치웠고 (옛 빌드), 그 사이 boot 계획이 만들어진 상태: 이전 계획은 .stale, 트레이스·run 파일도 .stale.
    private long TraceSetAsideByAnOlderBuild(out string trace, bool withBootPlan)
    {
        UseAntiCheat("vanguard");
        long start = DoneAndReadyToReplan(out trace);
        File.Move(_game.Plan, _game.Plan + ".stale");
        foreach (string f in new[] { trace, TraceWriter.RunPath(trace, 1), TraceWriter.RunPath(trace, 2) })
        {
            _w.Files.Remove(f);
            _w.Files.Add(f + ".stale");
        }

        if (withBootPlan)
        {
            PlanFile boot = _w.MakePlan(Sessions, Root, 1);
            boot.Stats["source"] = AutoRelocator.PlanSourceBoot;
            PlanOutput.Save(boot, Sessions, _game.Plan);
            Assert.Null(PlanFile.Load(_game.Plan).Zone.StartLcn); // boot 계획은 새 존
        }

        _status.ResetForReplan(_game.Name, "game updated, will trace again on next run");
        _relocator = null; // 서비스 재시작
        return start;
    }

    [Fact]
    public void RestartRestoresTheTraceSetAsideByAnEarlierUpdateAndReusesTheSameZone()
    {
        long start = TraceSetAsideByAnOlderBuild(out string trace, withBootPlan: true);
        Assert.Contains(trace + ".stale", _w.Files);

        Tick();

        Assert.Contains(trace, _w.Files);
        Assert.Contains(TraceWriter.RunPath(trace, 1), _w.Files);
        Assert.Contains(TraceWriter.RunPath(trace, 2), _w.Files);
        Assert.DoesNotContain(trace + ".stale", _w.Files);
        Assert.DoesNotContain(TraceWriter.RunPath(trace, 1) + ".stale", _w.Files);
        Assert.DoesNotContain(TraceWriter.RunPath(trace, 2) + ".stale", _w.Files);
        Assert.True(File.Exists(_game.Plan + ".boot")); // boot 계획은 옆으로, 이전 계획의 존 시작은 .stale 에 그대로
        Assert.Equal(start, PlanFile.Load(_game.Plan + ".stale").Zone.StartLcn);
        Assert.Equal(1, _log.Lines.Count(l => l.Contains("trace set aside by an earlier update restored", StringComparison.Ordinal)));

        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal([start], _w.PlanStarts); // 이미 옮긴 존 자리에 맞춰 계획: 새 존을 찾지 않음
        Assert.Equal(start, PlanFile.Load(_game.Plan).Zone.StartLcn);
        Tick();
        Assert.Equal(1, _log.Lines.Count(l => l.Contains("trace set aside by an earlier update restored", StringComparison.Ordinal))); // 한 번뿐
    }

    [Fact]
    public void RestartRestoresTheTraceEvenWithoutABootPlan()
    {
        long start = TraceSetAsideByAnOlderBuild(out string trace, withBootPlan: false);

        Tick();
        Assert.Contains(trace, _w.Files);

        Run(TimeSpan.FromMinutes(20));

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal([start], _w.PlanStarts);
    }

    // 손상된 boot 계획 + 유효한 .stale 계획 + .stale 트레이스: 부트 계획 읽기가 실패해도 트레이스 복원이 건너뛰어지지 않고, 이전 계획이 done 으로 되살아나지도 않아야 함.
    private long CorruptBootPlanWithTheTraceSetAside(out string trace)
    {
        long start = TraceSetAsideByAnOlderBuild(out trace, withBootPlan: true);
        File.WriteAllText(_game.Plan, "{\"truncat");
        return start;
    }

    private void AssertTraceRestoredAndReplanned(string trace, long start)
    {
        Assert.True(File.Exists(_game.Plan + AutoRelocator.BadSuffix)); // 손상 계획은 치움
        Assert.Contains(trace, _w.Files);
        Assert.DoesNotContain(trace + ".stale", _w.Files);
        Assert.True(File.Exists(_game.Plan + AutoRelocator.StaleSuffix)); // 이전 계획은 done 으로 되살리지 않고 존 시작 힌트로 둠
        Assert.NotEqual(AutoStage.Done, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
        Assert.Equal(1, _log.Lines.Count(l => l.Contains("trace set aside by an earlier update restored", StringComparison.Ordinal)));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("previous plan restored", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("an earlier replan was interrupted", StringComparison.Ordinal));

        Run(TimeSpan.FromMinutes(20));

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal([start], _w.PlanStarts); // 복원한 트레이스로 이미 옮긴 존 자리에 맞춰 다시 계획
        Assert.Equal(start, PlanFile.Load(_game.Plan).Zone.StartLcn);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(AutoStage.Failed)]
    [InlineData(AutoStage.Done)]
    public void RestartRestoresTheTraceEvenWhenTheBootPlanIsUnreadableAndReplansInsteadOfRestoringTheStalePlan(string? stage)
    {
        long start = CorruptBootPlanWithTheTraceSetAside(out string trace);
        if (stage == AutoStage.Done)
        {
            _status.Complete(_game.Name, _game.Source, _w.Now, counted: false, null); // 재배치 기록이 남은 상태 (옛 빌드가 .stale 로 치우다 꺼짐)
        }
        else if (stage is not null)
        {
            _status.Set(_game.Name, _game.Source, null, stage, "plan failed");
        }

        Tick();

        AssertTraceRestoredAndReplanned(trace, start);
    }

    // 트레이스를 되살린 직후 (새 계획을 저장하기 전) 또 꺼졌다 켜져도: .bad 와 .stale 계획이 있다고 이전 계획을 done 으로 되살리지 않고 다시 계획해야 함.
    [Fact]
    public void RestartRightAfterTheTraceWasRestoredStillPlansAgainInsteadOfRestoringTheStalePlan()
    {
        long start = CorruptBootPlanWithTheTraceSetAside(out string trace);
        File.Move(_game.Plan, _game.Plan + AutoRelocator.BadSuffix); // 첫 시작이 한 일: 손상 계획 치움, 트레이스 복원, 상태를 다시 계획으로
        _ = _w.Files.Remove(trace + ".stale");
        _ = _w.Files.Add(trace);
        _status.Set(_game.Name, _game.Source, null, AutoStage.Discovered, "first run recorded, planning again");

        _relocator = null; // 새 계획을 저장하기 전에 꺼졌다 다시 시작
        Tick();

        Assert.Contains(trace, _w.Files);
        Assert.True(File.Exists(_game.Plan + AutoRelocator.BadSuffix));
        Assert.True(File.Exists(_game.Plan + AutoRelocator.StaleSuffix));
        Assert.NotEqual(AutoStage.Done, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("previous plan restored", StringComparison.Ordinal));

        Run(TimeSpan.FromMinutes(20));

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal([start], _w.PlanStarts);
        Assert.Equal(start, PlanFile.Load(_game.Plan).Zone.StartLcn);
    }

    [Fact]
    public void RestartWithoutAPlanRestoresTheTraceAndPlansAgainEvenWhenTheStateWasFailed()
    {
        long start = TraceSetAsideByAnOlderBuild(out string trace, withBootPlan: false);
        _status.Set(_game.Name, _game.Source, null, AutoStage.Failed, "plan failed");

        Tick();

        Assert.Contains(trace, _w.Files);
        Assert.NotEqual(AutoStage.Done, Entry.Stage); // 이전 계획 복원 (done) 이 아니라 다시 계획
        Assert.DoesNotContain(_log.Lines, l => l.Contains("previous plan restored", StringComparison.Ordinal));

        Run(TimeSpan.FromMinutes(20));

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal([start], _w.PlanStarts);
    }

    [Fact]
    public void RestartRestoresOneVersionOfTheTraceAndLeavesFilesOfTheOtherSuffixAlone()
    {
        _ = TraceSetAsideByAnOlderBuild(out string trace, withBootPlan: false);
        string run1 = TraceWriter.RunPath(trace, 1);
        foreach (string f in new[] { trace, run1 })
        {
            _w.Files.Add(f + ".stale.update"); // 두 번째 전체 치움의 최신 원본 (.stale 은 첫 치움의 것)
        }

        Tick();

        Assert.Contains(trace, _w.Files);
        Assert.Contains(run1, _w.Files);
        Assert.DoesNotContain(TraceWriter.RunPath(trace, 2), _w.Files); // .update 가 없는 run 은 옛 버전이라 되살리지 않고 .stale 로 남음 (버전이 섞이지 않음)
        Assert.Contains(TraceWriter.RunPath(trace, 2) + ".stale", _w.Files);
        Assert.Contains(_w.Moved, m => m.From == trace + ".stale.update" && m.To == trace);
        Assert.Contains(_w.Moved, m => m.From == run1 + ".stale.update" && m.To == run1);
        Assert.DoesNotContain(_w.Moved, m => m.From == TraceWriter.RunPath(trace, 2) + ".stale");
        Assert.DoesNotContain(_w.Moved, m => m.From == trace + ".stale" || m.From == run1 + ".stale"); // 옛 .stale 은 그대로
        Assert.Contains(trace + ".stale", _w.Files);
        Assert.Contains(run1 + ".stale", _w.Files);
    }

    [Fact]
    public void RestartKeepsAStaleTraceThatHasNothingLeftOrBelongsToATraceAllowedGame()
    {
        _ = TraceSetAsideByAnOlderBuild(out string trace, withBootPlan: false);
        _w.LoadSessions = () => [[]];
        Tick();
        Assert.DoesNotContain(trace, _w.Files); // 남는 블록이 없는 트레이스는 되살리지 않음
        Assert.Contains(trace + ".stale", _w.Files);

        _w.LoadSessions = () => Sessions;
        UseAntiCheat(null);
        _relocator = null;
        Tick();
        Assert.DoesNotContain(trace, _w.Files); // 트레이스할 수 있는 게임은 다시 트레이스함
        Assert.Contains(trace + ".stale", _w.Files);
    }

    [Fact]
    public void RestartLeavesAnExistingTraceAndATracedPlanAlone()
    {
        UseAntiCheat("vanguard");
        DoneAndReadyToReplan(out string trace);
        _w.Files.Add(trace + ".stale"); // 옛 .stale 가 남았어도 트레이스가 있으면 건드리지 않음
        _relocator = null;

        Tick();

        Assert.Contains(trace, _w.Files);
        Assert.Contains(trace + ".stale", _w.Files);
        Assert.True(File.Exists(_game.Plan));
        Assert.Empty(_w.Moved);
        Assert.Equal(AutoStage.Done, Entry.Stage);
    }
}
