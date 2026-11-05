using AsterCache.Core.Apply;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 서비스가 계획·저널 파일을 바꾸는 구간 전체를 게임 단위 잠금으로 감싸고, 잠금을 기다리는 동안 CLI 가 계획을 바꿨으면 옛 계획을 저장·이동·보관·되돌리지 않는지.
public sealed partial class AutoRelocatorTests
{
    // 지문 (SHA) 이 다른 활성 계획: 핫 순서 끝 항목을 하나 뺌 (SHA 는 tasks 와 order 만 봄).
    private static PlanFile Different(PlanFile plan) => plan with { Order = [.. plan.Order.Take(plan.Order.Count - 1)] };

    // 잠금을 기다리는 동안 CLI 가 새 활성 계획 (SHA 가 다른 같은 내용) 을 저장한 것처럼 만드는 가짜 잠금: when 이 참인 첫 호출에서 한 번만 바꿈.
    private void CliReplacesThePlanWhileTheServiceWaits(Func<bool> when, out PlanFile replacement)
    {
        PlanFile swapped = Different(PlanFile.Load(_game.Plan));
        replacement = swapped;
        bool done = false;
        _w.Lock = g =>
        {
            if (!done && when())
            {
                done = true;
                swapped.Save(g.Plan);
            }

            return new FakeGate();
        };
    }

    [Fact]
    public void ThePlanReplacedWhileTheServiceWaitsForTheLockIsNeitherSetAsideNorArchivedAndTheNextCheckSeesTheNewOne()
    {
        _ = DoneAndReadyToReplan(out _);
        string before = PlanFile.Load(_game.Plan).Sha();
        Update();
        bool armed = false;
        CliReplacesThePlanWhileTheServiceWaits(() => armed, out PlanFile replacement);
        armed = true;
        _w.Now += AutoRelocator.StaleCheckEvery;

        Tick();

        Assert.NotEqual(before, replacement.Sha());
        Assert.Equal(replacement.Sha(), PlanFile.Load(_game.Plan).Sha()); // 새 활성 계획이 그대로
        Assert.False(File.Exists(_game.Plan + ".stale"));
        Assert.True(File.Exists(JournalPath)); // 저널도 보관하지 않음
        Assert.False(File.Exists(JournalPath + ".old"));
        Assert.DoesNotContain(_w.Moved, m => m.From == _game.Plan);
        Assert.Equal(AutoStage.Done, Entry.Stage);

        Tick(); // 점검 주기를 기다리지 않고 새 활성 계획으로 다시 따짐

        Assert.False(File.Exists(_game.Plan));
        Assert.Equal(replacement.Sha(), PlanFile.Load(_game.Plan + ".stale").Sha()); // 이번에는 새 계획이 그대로 치워짐
        Assert.True(File.Exists(JournalPath + ".old"));
    }

    [Fact]
    public void ABusyLockLeavesTheFilesAloneForTheUpdateReplanAndItIsRetriedOnTheNextTick()
    {
        _ = DoneAndReadyToReplan(out _);
        Update();
        bool busy = true;
        _w.Lock = _ => busy ? null : new FakeGate();
        _w.Now += AutoRelocator.StaleCheckEvery;

        Tick();

        Assert.True(File.Exists(_game.Plan));
        Assert.True(File.Exists(JournalPath));
        Assert.Empty(_w.Moved);
        Assert.Equal(AutoStage.Done, Entry.Stage);

        busy = false;
        Tick();

        Assert.False(File.Exists(_game.Plan));
        Assert.True(File.Exists(_game.Plan + ".stale"));
    }

    [Fact]
    public void ARelocationAttemptRunsInsideOneLockAndAFirstPlanIsNotMadeWhenTheLockIsBusy()
    {
        bool busy = true;
        int locks = 0, held = 0;
        _w.Lock = _ =>
        {
            locks++;
            if (busy)
            {
                return null;
            }

            held++;
            return new FakeGate(() => held--);
        };
        int applyHeld = 0;
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> inner = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            applyHeld = Math.Max(applyHeld, held); // 적용 호출 때 이미 게임 단위 잠금이 잡혀 있음
            return inner(g, plan, options, ct);
        };

        Tick();

        Assert.False(File.Exists(_game.Plan)); // 잠금을 못 잡으면 계획을 만들지 않음 (실패로도 기록하지 않음)
        Assert.NotEqual(AutoStage.Failed, Entry.Stage);
        Assert.True(locks >= 1);

        busy = false;
        RunUntilDone(80);

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.True(applyHeld >= 1);
        Assert.Equal(0, held); // 모두 놓음
    }

    [Fact]
    public void ARemainderReplanWhoseActivePlanWasReplacedWhileWaitingIsRejectedAndRetriedFromTheNewActivePlan()
    {
        PartialState();
        PlanFile stale = PlanFile.Load(_game.Plan);
        bool swapNow = false;
        CliReplacesThePlanWhileTheServiceWaits(() => swapNow && _log.Lines.Any(l => l.Contains("replanning only the remaining part", StringComparison.Ordinal)), out PlanFile replacement);
        Func<GameSettings, PlanFile, IReadOnlyList<PlanFile>, IReadOnlyList<IReadOnlyList<AsterCache.Core.Trace.TraceEvent>>?, PlanFile> inner = _w.ReplanResidual!;
        var candidates = new List<string>();
        _w.ReplanResidual = (g, c, o, s) =>
        {
            candidates.Add(c.Sha());
            return inner(g, c, o, s);
        };
        swapNow = true;

        TickUntil(() => _log.Lines.Any(l => l.Contains("replanning only the remaining part", StringComparison.Ordinal)));
        Tick();

        Assert.Empty(candidates); // 옛 활성 계획은 후보로 넘어가지 않음
        Assert.NotEqual(AutoStage.Failed, Entry.Stage);
        Assert.Null(_status.Recovery("bench"));
        Assert.Equal(replacement.Sha(), PlanFile.Load(_game.Plan).Sha()); // 새 활성 계획을 되돌리지 않음

        _w.Now += AutoRelocator.TransientRetry + Step;
        RunUntilDone(120);

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.DoesNotContain(stale.Sha(), candidates);
        Assert.Contains(replacement.Sha(), candidates); // 다음 시도는 새 활성 계획으로
        Assert.Equal(0, Remaining(PlanFile.Load(_game.Plan)));
    }
}

internal sealed class FakeGate(Action? onDispose = null) : IDisposable
{
    public void Dispose() => onDispose?.Invoke();
}
