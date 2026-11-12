using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임별 정책 (mode, paused) 과 auto.relocate_hours 가 자동 계획·예열·재배치에 적용되는가.
public sealed partial class AutoRelocatorTests
{
    private void SetPolicy(GameMode mode, bool paused = false)
    {
        _game = _game with { Mode = mode, Paused = paused };
        _settings = _settings with { Games = [_game] };
    }

    [Fact]
    public void PrewarmOnlyPlansAndPrewarmsLikeRelocationOffButNeverMovesAFile()
    {
        SetPolicy(GameMode.PrewarmOnly);
        UsePrewarm();

        Run(TimeSpan.FromMinutes(6));

        Assert.Equal(AutoStage.Skipped, Entry.Stage);
        Assert.Equal("game policy: prewarm_only", Entry.Detail?.Split(", ")[0]);
        Assert.Equal(1, Streams);
        Assert.NotEmpty(_w.PlanZones); // 예열이 쓰는 계획은 만듦
        Assert.Empty(Vol.Moves);
        Assert.Equal(0, ApplyCalls);
    }

    [Fact]
    public void RelocateOnlyRelocatesButNeverStartsAPrewarm()
    {
        SetPolicy(GameMode.RelocateOnly);
        UsePrewarm();

        Tick(); // 첫 틱이 상태 항목을 만듦
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotEmpty(Vol.Moves);
        Assert.Equal(0, Streams);
    }

    [Theory]
    [InlineData(GameMode.Off, false)]
    [InlineData(GameMode.Inherit, true)]
    [InlineData(GameMode.PrewarmOnly, true)]
    [InlineData(GameMode.RelocateOnly, true)]
    public void OffAndPausedGamesGetNoPlanNoPrewarmAndNoMove(GameMode mode, bool paused)
    {
        SetPolicy(mode, paused);
        UsePrewarm();

        Run(TimeSpan.FromMinutes(6));

        Assert.Equal(0, Streams);
        Assert.Empty(Vol.Moves);
        Assert.Empty(_w.PlanZones);
        Assert.Equal(0, ApplyCalls);
        Assert.Null(_status.Get("bench")); // 상태·이력은 건드리지 않음
    }

    [Fact]
    public void ReturningToInheritResumesPlanningAndRelocationFromTheNextTick()
    {
        SetPolicy(GameMode.Off);
        Run(TimeSpan.FromMinutes(2));
        Assert.Empty(Vol.Moves);

        SetPolicy(GameMode.Inherit);
        Tick();
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotEmpty(Vol.Moves);
    }

    [Fact]
    public void RelocateHoursKeepNewRelocationsOutsideTheWindowAndLogTheStateOnlyWhenItChanges()
    {
        int hour = _w.Now.ToLocalTime().Hour;
        int[] outside = [.. Enumerable.Range(0, 24).Where(h => h != hour && h != (hour + 1) % 24)]; // 시험 도중 시각이 넘어가도 시간대 밖
        SetAuto(a => a with { RelocateHours = outside });

        Run(TimeSpan.FromMinutes(10));

        Assert.Empty(Vol.Moves);
        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Single(_log.Lines, l => l.Contains("auto relocation waits: outside auto.relocate_hours", StringComparison.Ordinal));

        SetAuto(a => a with { RelocateHours = [hour, (hour + 1) % 24] });
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotEmpty(Vol.Moves);
        Assert.Single(_log.Lines, l => l.Contains("auto relocation resumes: inside auto.relocate_hours", StringComparison.Ordinal));
    }

    [Fact]
    public void AnEmptyRelocateHoursListAlwaysAllowsRelocation()
    {
        SetAuto(a => a with { RelocateHours = [] });

        Tick();
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("relocate_hours", StringComparison.Ordinal));
    }

    [Fact]
    public void APrewarmOnlyGameStaysInTheTransientHoldInsteadOfReplanningEveryTick()
    {
        SetPolicy(GameMode.PrewarmOnly);
        int attempts = 0;
        _w.JournalRows = _ => 3; // 저널이 남아 잔여 재계획 경로로 감
        _w.ReplanResidual = (_, _, _, _) =>
        {
            attempts++;
            throw new IOException("locked");
        };

        Tick();
        Assert.Equal(1, attempts);
        Assert.Contains("temporary IOException", Entry.Detail, StringComparison.Ordinal);

        _w.Now += Step;
        Tick();
        _w.Now += Step;
        Tick();

        Assert.Equal(1, attempts); // 2 분 보류 중에는 다시 계획하지 않음
        Assert.Single(_log.Lines, l => l.Contains("nothing was changed, will retry", StringComparison.Ordinal));
    }

    [Fact]
    public void AnInterruptedRelocationWithAJournalResumesOutsideTheHoursButANewOneDoesNotStart()
    {
        ReportFullMoves();
        Vol.AfterMove = n =>
        {
            if (n == 1)
            {
                _gate.Cancel(); // 게임 시작과 같은 취소: 저널이 남음
            }
        };
        Run(TimeSpan.FromMinutes(3));
        Assert.Equal((AutoStage.WaitingIdle, "cancelled by game start"), (Entry.Stage, Entry.Detail));
        Vol.AfterMove = _ => { };

        int hour = _w.Now.ToLocalTime().Hour;
        SetAuto(a => a with { RelocateHours = [.. Enumerable.Range(0, 24).Where(h => h != hour && h != (hour + 1) % 24)] });
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage); // 이미 시작한 회차는 시간대 밖에서도 끝냄
        Assert.Contains(_log.Lines, l => l.Contains("auto relocation waits: outside auto.relocate_hours", StringComparison.Ordinal));
    }
}
