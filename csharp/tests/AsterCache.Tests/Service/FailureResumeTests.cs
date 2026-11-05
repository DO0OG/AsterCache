using AsterCache.Core.Apply;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 실패 분류: 무결성 실패 (data check failed) 만 계속 멈추고, 그 밖의 실패는 트레이스 금지 게임도 한 시간 뒤 자동으로 다시 열림.
/// 이동 뒤 예외로 되돌린 바이트도 하루 예산 집계에 들어감.
public sealed partial class AutoRelocatorTests
{
    private void AfterAnHour() => _w.Now += AutoRelocator.StoppedRecheck + TimeSpan.FromMinutes(1);

    [Fact]
    public void StepsFailedOnATraceForbiddenGameIsReopenedAnHourLaterAndTheRestIsMoved()
    {
        _w.AntiCheats[Root] = "eac";
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        bool failing = true;
        int executes = 0;
        _w.Apply = (g, plan, options, ct) =>
        {
            if (options.Execute && failing)
            {
                executes++;
                var rep = new ApplyReport { Executed = true };
                rep.Failed.Add(new FailedStep(0, F1, "locked"));
                return rep;
            }

            return real(g, plan, options, ct);
        };

        Run(TimeSpan.FromMinutes(3.5));
        Assert.Equal((AutoStage.Failed, "1 step(s) failed"), (Entry.Stage, Entry.Detail));
        Assert.Null(Entry.RelocatedAt);

        _w.Now += TimeSpan.FromMinutes(40);
        int before = executes;
        Run(TimeSpan.FromMinutes(4));
        Assert.Equal((before, AutoStage.Failed), (executes, Entry.Stage)); // 한 시간 전에는 다시 시도하지 않음

        failing = false;
        AfterAnHour();
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.Equal(0, Remaining(PlanFile.Load(_game.Plan)));
    }

    [Fact]
    public void APlanFailureOnATraceForbiddenGameIsReopenedAnHourLater()
    {
        _w.AntiCheats[Root] = "eac";
        Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, PlanFile> real = _w.MakePlan;
        bool failing = true;
        _w.MakePlan = (sessions, origin, zoneMib) => failing ? throw new IOException("locked") : real(sessions, origin, zoneMib);

        Run(TimeSpan.FromMinutes(3.5));
        Assert.Equal((AutoStage.Failed, "plan failed: IOException"), (Entry.Stage, Entry.Detail));

        failing = false;
        AfterAnHour();
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.Equal(0, Remaining(PlanFile.Load(_game.Plan)));
    }

    [Fact]
    public void NoHotDataOnATraceForbiddenGameIsReopenedAnHourLater()
    {
        _w.AntiCheats[Root] = "eac";
        Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, PlanFile> real = _w.MakePlan;
        bool empty = true;
        _w.MakePlan = (sessions, origin, zoneMib) =>
        {
            PlanFile plan = real(sessions, origin, zoneMib);
            if (empty)
            {
                plan.Stats["hot_bytes"] = 0L;
            }

            return plan;
        };

        Run(TimeSpan.FromMinutes(3.5));
        Assert.Equal((AutoStage.Failed, "no hot data"), (Entry.Stage, Entry.Detail));

        empty = false;
        AfterAnHour();
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
    }

    [Fact]
    public void ExtensionWhoseJournalCouldNotBeArchivedFinishesTheArchiveAnHourLaterAndRestoresThePlan()
    {
        int tries = 0;
        FailExtensionWith(null, g => ++tries == 1 ? throw new IOException("locked") : "archived", out _);
        Assert.Equal(AutoStage.Failed, Entry.Stage);
        Assert.StartsWith("extension journal could not be archived", Entry.Detail, StringComparison.Ordinal);

        AfterAnHour();
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.True(tries >= 2);
        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix) || _w.Files.Contains(_game.Plan + AutoRelocator.ExtendSuffix));
    }

    [Fact]
    public void ExtensionWhoseRevertWasIncompleteRevertsTheRestAnHourLaterThenArchivesAndRestoresThePlan()
    {
        int reverts = 0;
        int archived = 0;
        FailExtensionWith((_, _, _) => ++reverts == 1 ? new RevertReport { Stopped = "cancelled" } : new RevertReport { Restored = 1, MovedBytes = 4096 }, g => { archived++; return "archived"; }, out _);
        Assert.Equal(AutoStage.Failed, Entry.Stage);
        Assert.StartsWith("extension revert incomplete", Entry.Detail, StringComparison.Ordinal);
        Assert.Equal(0, archived);

        AfterAnHour();
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.Equal((2, 1), (reverts, archived)); // 남은 되돌리기를 끝낸 뒤에 보관
        Assert.False(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix) || _w.Files.Contains(_game.Plan + AutoRelocator.ExtendSuffix));
    }

    [Fact]
    public void ExtensionRecoveryThatStillCannotRevertStaysFailedAndIsTriedAgainTheNextHour()
    {
        int reverts = 0;
        FailExtensionWith((_, _, _) => ++reverts <= 2 ? new RevertReport { Stopped = "cancelled" } : new RevertReport(), null, out _);

        AfterAnHour();
        RunUntilDone(24); // 유휴 3 분을 채운 뒤 두 번째 시도
        Assert.Equal((2, AutoStage.Failed), (reverts, Entry.Stage));
        Assert.StartsWith("extension revert incomplete", Entry.Detail, StringComparison.Ordinal);

        AfterAnHour();
        RunUntilDone();
        Assert.True(Entry.Stage == AutoStage.Done, Why());
    }

    [Fact]
    public void UndoAfterAMoveIsAddedToTheDailyByteCount()
    {
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        bool moved = false;
        long movedBytes = 0;
        _w.Apply = (g, plan, options, ct) =>
        {
            ApplyReport rep = real(g, plan, options, ct);
            if (options.Execute)
            {
                moved = true;
                movedBytes = rep.MovedBytes;
            }

            return rep;
        };
        Func<string, IReadOnlyList<Extent>?> extents = _w.Extents;
        _w.Extents = p => moved ? throw new IOException("disk gone") : extents(p); // 이동이 끝난 뒤 표본 확인에서 예외
        long reverted = 0;
        long countedBeforeRevert = -1;
        Func<GameSettings, int, CancellationToken, RevertReport?> inner = _w.Revert;
        _w.Revert = (g, skip, ct) =>
        {
            countedBeforeRevert = _status.RelocationBytesWithin(TimeSpan.FromDays(1));
            RevertReport? r = inner(g, skip, ct);
            reverted = r?.MovedBytes ?? 0;
            return r;
        };

        Run(TimeSpan.FromMinutes(3.5));

        Assert.Equal((AutoStage.Failed, "relocation failed: IOException"), (Entry.Stage, Entry.Detail));
        Assert.True(movedBytes > 0 && reverted > 0, Why());
        Assert.Equal(movedBytes, countedBeforeRevert);
        Assert.Equal(movedBytes + reverted, _status.RelocationBytesWithin(TimeSpan.FromDays(1))); // 이동 + 되돌리기
    }
}
