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
