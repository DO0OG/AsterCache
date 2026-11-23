using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 원본이 그대로인데 흐트러져 서비스가 다시 놓은 회차가 7일에 3번이면 그 게임의 재배치를 쉬는 제한: 세는 회차, 쉬는 동안 하는 일, 풀리는 조건.
public sealed partial class AutoRelocatorTests
{
    private static readonly TimeSpan RepeatWindow = TimeSpan.FromDays(7);

    // 재배치를 끝낸 게임에 되풀이 기록을 만듦 (재적용 게이트가 부르면 만들어짐).
    private void ArmRepeatLimit()
    {
        DoneAndReadyToReplan(out _);
        _ = Relocator.ReapplyBlock(_game);
        Assert.NotNull(_status.ReapplyLimit("bench"));
    }

    private void ReappliedRound(long bytes = 4096, bool split = false) => Relocator.NoteReapplyMoved(_game, bytes, 0, split);

    private int RepeatWarnings() => _log.Lines.Count(l => l.StartsWith("Warn", StringComparison.Ordinal) && l.Contains("reapply paused after 3 unchanged-source rounds in 7 days", StringComparison.Ordinal));

    [Fact]
    public void TheThirdUnchangedSourceRoundPausesTheGameLogsOnceAndShowsTheReason()
    {
        ArmRepeatLimit();

        ReappliedRound();
        ReappliedRound();
        Assert.NotEqual(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));
        Assert.Equal(0, RepeatWarnings());

        ReappliedRound();

        Assert.Equal(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));
        Assert.Equal(1, RepeatWarnings());
        Assert.Equal((AutoStage.Done, AutoRelocator.ReapplyRepeatDetail), (Entry.Stage, Entry.Detail));
        Assert.Contains(_log.Lines, l => l.Contains("resumes when source files change and a new plan is made, the layout setting changes, or fewer than 3 rounds remain within 7 days", StringComparison.Ordinal));
        _ = Relocator.ReapplyBlock(_game);
        ReappliedRound(); // 쉬는 중의 수동·기타 회차가 같은 경고를 되풀이하지 않음
        Assert.Equal(1, RepeatWarnings());
    }

    [Fact]
    public void RoundsThatMovedNothingAreNotCounted()
    {
        ArmRepeatLimit();

        for (int i = 0; i < 5; i++)
        {
            ReappliedRound(0);
        }

        Assert.Equal(0, _status.ReapplyRoundsWithin("bench", RepeatWindow));
        Assert.NotEqual(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));
    }

    [Fact]
    public void ThePauseEndsWhenAnOldRoundLeavesTheSevenDayWindowAndTheStatusReasonIsRemoved()
    {
        ArmRepeatLimit();
        DateTimeOffset first = _w.Now;
        ReappliedRound();
        _w.Now = first + TimeSpan.FromDays(1);
        ReappliedRound();
        _w.Now = first + TimeSpan.FromDays(2);
        ReappliedRound();
        Assert.Equal(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));

        _w.Now = first + RepeatWindow - TimeSpan.FromMinutes(1);
        Assert.Equal(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game)); // 아직 3회가 창 안

        _w.Now = first + RepeatWindow + TimeSpan.FromMinutes(1);
        Assert.NotEqual(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));
        Assert.Equal(2, _status.ReapplyRoundsWithin("bench", RepeatWindow));
        Assert.Equal((AutoStage.Done, (string?)null), (Entry.Stage, Entry.Detail));
    }

    [Fact]
    public void ASourceChangeThatMakesANewPlanEndsThePauseAndForgetsTheRounds()
    {
        ArmRepeatLimit();
        ReappliedRound();
        ReappliedRound();
        ReappliedRound();
        Assert.Equal(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));

        Update(); // 게임 업데이트: 원본 지문이 바뀜
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Equal(AutoStage.Discovered, Entry.Stage);
        Assert.Null(_status.ReapplyLimit("bench"));
        Assert.Equal(0, _status.ReapplyRoundsWithin("bench", RepeatWindow));
    }

    [Fact]
    public void AChangedPlanSourceKeyStartsANewCountEvenWithoutAReplan()
    {
        ArmRepeatLimit();
        ReappliedRound();
        ReappliedRound();
        ReappliedRound();
        Assert.Equal(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));

        PlanFile plan = PlanFile.Load(_game.Plan);
        (plan with { Fingerprints = [.. plan.Fingerprints.Select(f => f with { Size = f.Size + 1 })] }).Save(_game.Plan); // 계획이 다른 원본 지문을 가짐
        Assert.NotEqual(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));
        Assert.Equal(0, _status.ReapplyRoundsWithin("bench", RepeatWindow));
    }

    [Fact]
    public void ChangingTheLayoutSettingOfTheGameEndsThePause()
    {
        ArmRepeatLimit();
        ReappliedRound();
        ReappliedRound();
        ReappliedRound();
        Assert.Equal(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));

        UseWholeFile(); // 전역 배치 방식을 ranges 에서 whole_file 로

        Assert.NotEqual(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));
        Assert.Equal(0, _status.ReapplyRoundsWithin("bench", RepeatWindow)); // 새 방식으로 처음부터 셈
    }

    [Fact]
    public void AGameLayoutOverrideChangeEndsThePauseToo()
    {
        ArmRepeatLimit();
        ReappliedRound();
        ReappliedRound();
        ReappliedRound();
        Assert.Equal(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));

        _game = _game with { Layout = RelocateLayouts.WholeFile };
        _settings = _settings with { Games = [_game] };

        Assert.NotEqual(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));
    }

    [Fact]
    public void AFirstPlacementWithItsWriteCapContinuationsNeverCreatesARepeatCount()
    {
        Tick();
        _w.Now += Step;
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotEmpty(Vol.Moves);
        Assert.Equal(0, _status.ReapplyRoundsWithin("bench", RepeatWindow));
        Assert.Equal(0, RepeatWarnings());
    }

    [Fact]
    public void TheFirstMoveAfterAZoneHandOverIsCountedOnceAndWhatFollowsIsNot()
    {
        DoneAndReadyToReplan(out _);
        _ = Relocator.ReapplyBlock(_game);
        int rejected = 0;
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            if (options.DryRun && rejected++ == 0)
            {
                throw new PlanRejectedException("zone_not_free: LCN 777 is in use by another file");
            }

            ApplyReport rep = real(g, plan, options, ct);
            if (options.DryRun)
            {
                rep.TasksTodo = 1; // 가짜 볼륨은 이미 제자리라 할 일이 없으므로 옮길 것이 있는 것으로 보고
                rep.BytesTodo = 8192;
            }
            else
            {
                rep.MovedBytes = 8192;
            }

            return rep;
        };
        _relocator = null;
        Assert.True(Relocator.HandOffZoneBlocked(_game));
        Assert.True(_status.ReapplyLimit("bench")!.HandoffPending);

        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(1, _status.ReapplyRoundsWithin("bench", RepeatWindow)); // 넘긴 뒤의 첫 이동 한 번
        Assert.False(_status.ReapplyLimit("bench")!.HandoffPending);
    }

    [Fact]
    public void APausedGameIsNotMovedAnyMoreButItsTraceIsStillPrewarmedAndTheReasonShows()
    {
        UsePrewarm();
        _status.EnsureReapplyLimit("bench", string.Empty, RelocateLayouts.Ranges); // 기준은 다음에 보는 계획의 지문
        for (int i = 0; i < 3; i++)
        {
            _ = _status.RecordReapplyRound("bench", 4096, RepeatWindow, 3);
        }

        Run(TimeSpan.FromMinutes(10));

        Assert.Empty(Vol.Moves);
        Assert.Equal(0, ApplyCalls);
        Assert.Equal(1, Streams); // 예열은 그대로
        Assert.Contains(AutoRelocator.ReapplyRepeatDetail, Entry.Detail, StringComparison.Ordinal);
        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);

        _status.ClearReapplyLimit("bench"); // 쉬는 조건이 풀리면 옮김
        RunUntilDone();
        Assert.NotEmpty(Vol.Moves);
    }

    [Fact]
    public void ThePauseReasonIsAddedToTheExistingDetailAndTakenOffAgainWhenItEnds()
    {
        ArmRepeatLimit();
        _status.Set("bench", "auto", null, AutoStage.Done, "relocated (profile), prewarmed");
        DateTimeOffset first = _w.Now;
        ReappliedRound();
        ReappliedRound();
        _w.Now += TimeSpan.FromDays(1);
        ReappliedRound();

        Assert.Equal("relocated (profile), prewarmed, " + AutoRelocator.ReapplyRepeatDetail, Entry.Detail);
        Tick();
        Assert.Equal("relocated (profile), prewarmed, " + AutoRelocator.ReapplyRepeatDetail, Entry.Detail); // 틱이 표시를 두 번 붙이거나 덮지 않음

        _w.Now = first + RepeatWindow + TimeSpan.FromMinutes(1);
        Tick();

        Assert.Equal((AutoStage.Done, "relocated (profile), prewarmed"), (Entry.Stage, Entry.Detail));
    }

    [Fact]
    public void ARoundSplitByTheWriteCapIsCountedOnceEvenAcrossAServiceRestart()
    {
        ArmRepeatLimit();
        ReappliedRound(split: true); // 흐트러짐 한 번: 첫 회차가 상한에서 멈춤

        var restarted = new AutoStatus(Path.Combine(Root, "auto_status.json"), _log, () => _w.Now, ticks: FakeClock.Ticks(() => _w.Now));
        var relocator = new AutoRelocator(WorldEnv(), restarted, _log, () => _settings);
        relocator.NoteReapplyMoved(_game, 4096, 0, partial: true); // 재시작 뒤 이어 옮기기
        relocator.NoteReapplyMoved(_game, 4096, 0, partial: false); // 이어 옮기기가 끝남
        Assert.Equal(1, restarted.ReapplyRoundsWithin("bench", RepeatWindow));

        relocator.NoteReapplyMoved(_game, 4096, 0, partial: false); // 끝난 뒤 다시 풀린 배치는 새 회차
        Assert.Equal(2, restarted.ReapplyRoundsWithin("bench", RepeatWindow));
    }

    [Fact]
    public void AContiguousLayoutEndsTheSplitRoundSoTheNextOneIsNew()
    {
        ArmRepeatLimit();
        ReappliedRound(split: true);

        Relocator.NoteReapplyHealthy(_game);
        ReappliedRound();

        Assert.Equal(2, _status.ReapplyRoundsWithin("bench", RepeatWindow));
    }

    [Fact]
    public void APausedGameStillRelocatingIsReleasedWhenItsSourceFilesChange()
    {
        UsePrewarm();
        _status.EnsureReapplyLimit("bench", string.Empty, RelocateLayouts.Ranges);
        for (int i = 0; i < 3; i++)
        {
            _ = _status.RecordReapplyRound("bench", 4096, RepeatWindow, 3);
        }

        Run(TimeSpan.FromMinutes(5));
        Assert.Empty(Vol.Moves);
        Assert.NotNull(_status.ReapplyLimit("bench"));

        Update(); // 게임 업데이트
        _w.Now += AutoRelocator.StaleCheckEvery;
        RunUntilDone();

        Assert.Null(_status.ReapplyLimit("bench"));
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotEmpty(Vol.Moves);
        Assert.Contains(_log.Lines, l => l.Contains("source files changed since the plan", StringComparison.Ordinal) || l.Contains("planning again", StringComparison.Ordinal));
    }

    [Fact]
    public void TheMigrationToAWholePlanStartsWithNoRepeatRecordAndItsMovesAreNotCounted()
    {
        UseWholeFile();
        _ = LegacyRangeDone();
        _status.EnsureReapplyLimit("bench", "old plan", RelocateLayouts.WholeFile);
        _ = _status.RecordReapplyRound("bench", 4096, RepeatWindow, 3);

        Tick(); // 이행: 새 통째 계획으로 교체
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotEmpty(Vol.Moves);
        Assert.Equal(0, _status.ReapplyRoundsWithin("bench", RepeatWindow));
    }

    [Fact]
    public void ARepeatRecordIsForgottenWhenTheGameIsRemovedAndRegisteredAgain()
    {
        ArmRepeatLimit();
        ReappliedRound();

        _status.Remove("bench");

        Assert.Null(_status.ReapplyLimit("bench"));
        Assert.DoesNotContain("reapply_limits", File.ReadAllText(Path.Combine(Root, "auto_status.json")), StringComparison.Ordinal);
    }

    // 이슈 주기의 가짜 세계: dry-run 은 rejects 번 존 점유로 거절하고 아니면 옮길 것이 있다고 보고, 실제 적용은 가짜 볼륨에서 실제로 옮기되 상한에서 멈춘 것으로 보고함.
    // 놓았던 조각이 제자리를 잃은 양은 값을 덮어쓰지 않고, 가짜 볼륨에서 파일을 다른 자리로 옮겨 만듦 (OptimizerMovesTheFilesAway).
    private (Func<int> Moves, Action<int> Reject) InstallIssueCycle()
    {
        int rejects = 0;
        int moves = 0;
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            if (options.DryRun && rejects > 0)
            {
                rejects--;
                throw new PlanRejectedException("zone_not_free: LCN 777 is in use by another file");
            }

            ApplyReport rep = real(g, plan, options, ct);
            if (options.DryRun)
            {
                rep.TasksTodo = 1; // 옮길 것이 남은 것으로 보고
                rep.BytesTodo = 8192;
            }
            else
            {
                moves++;
                rep.MovedBytes = Math.Max(rep.MovedBytes, 8192);
                rep.Stopped = "write_cap";
            }

            return rep;
        };
        _relocator = null;
        Relocator.PlacementLostMinBytes = 64 << 10; // 시험 계획은 수백 KiB: 문턱을 64 KiB 로 낮춤
        return (() => moves, n => rejects = n);
    }

    // 예약 최적화가 서비스가 놓은 조각을 존 밖의 다른 자리로 옮김: 가짜 볼륨에서 실제로 옮김.
    private int _awayRounds;

    private void OptimizerMovesTheFilesAway()
    {
        long at = 100000 + 4000 * _awayRounds++; // 매번 다른 빈 자리
        Vol.Move(F1, 0, at, 256);
        Vol.Move(F2, 0, at + 1000, 256);
    }

    [Fact]
    public void ARelocatingGameWithNoRecordWhosePlacedPiecesKeepGettingMovedAwayPausesAtTheThirdRoundByTicksAlone()
    {
        // 기록 없이 재배치 단계에서 시작 (주기 중간에 설치됨): 옮김 → 옮겨짐 → 거절 → 보류 → 잔여 재계획 → 다시 옮김
        (Func<int> moves, Action<int> reject) = InstallIssueCycle();
        RunTicksUntil(() => moves() >= 1); // 처음 배치
        Assert.Null(_status.ReapplyLimit("bench"));
        Assert.Equal(0, _status.ReapplyRoundsWithin("bench", RepeatWindow));

        for (int round = 1; round <= 3; round++)
        {
            OptimizerMovesTheFilesAway();
            reject(round == 1 ? 1 : 2); // 첫 주기는 바로 잔여 재계획, 이후는 거절 (보류) → 보류가 끝난 뒤 거절 (잔여 재계획)
            RunTicksUntil(() => _status.ReapplyRoundsWithin("bench", RepeatWindow) >= round, ticks: 400);
            Assert.Equal(round, _status.ReapplyRoundsWithin("bench", RepeatWindow));
        }

        Assert.Equal(1, RepeatWarnings());
        Assert.Equal(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));
        Assert.Contains(AutoRelocator.ReapplyRepeatDetail, Entry.Detail, StringComparison.Ordinal);

        int before = moves();
        OptimizerMovesTheFilesAway();
        reject(2);
        Run(TimeSpan.FromHours(2));
        Assert.Equal(before, moves()); // 쉬는 동안 옮기지 않음
        Assert.Equal(1, RepeatWarnings());
    }

    [Fact]
    public void TheCycleOfARelocatedGameHandedOverByTheReapplyCheckAlsoPausesAtTheThirdRound()
    {
        DoneAndReadyToReplan(out _);
        _ = Relocator.ReapplyBlock(_game);
        (Func<int> moves, Action<int> reject) = InstallIssueCycle();
        OptimizerMovesTheFilesAway();
        reject(1);
        Assert.True(Relocator.HandOffZoneBlocked(_game)); // 첫 회차: 정기 재적용이 존 점유로 넘김
        RunTicksUntil(() => _status.ReapplyRoundsWithin("bench", RepeatWindow) >= 1);
        RunTicksUntil(() => moves() >= 3); // 이어 옮기기는 세지 않음
        Assert.Equal(1, _status.ReapplyRoundsWithin("bench", RepeatWindow));

        for (int round = 2; round <= 3; round++)
        {
            OptimizerMovesTheFilesAway();
            reject(2);
            RunTicksUntil(() => _status.ReapplyRoundsWithin("bench", RepeatWindow) >= round, ticks: 400);
            Assert.Equal(round, _status.ReapplyRoundsWithin("bench", RepeatWindow));
        }

        Assert.Equal(1, RepeatWarnings());
        Assert.Equal(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));
    }

    [Fact]
    public void AReplanThatKeepsEverythingTheServicePlacedIsNotCountedHoweverOftenItHappens()
    {
        // 놓은 조각이 그대로이고 존만 다시 잡는 정상 진행 (여러 날에 걸친 처음 배치): 몇 번을 거쳐도 0회
        (Func<int> moves, Action<int> reject) = InstallIssueCycle();
        RunTicksUntil(() => moves() >= 1); // 처음 배치
        for (int round = 1; round <= 4; round++)
        {
            int movesBefore = moves();
            reject(round == 1 ? 1 : 2);
            RunTicksUntil(() => moves() >= movesBefore + 2, ticks: 400);
        }

        Assert.Equal(0, _status.ReapplyRoundsWithin("bench", RepeatWindow));
        Assert.Equal(0, RepeatWarnings());
        Assert.NotEqual(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));
    }

    [Theory]
    [InlineData(0, true)] // 잃은 양이 문턱과 같으면 셈
    [InlineData(1, false)] // 문턱보다 1 바이트라도 적으면 세지 않음
    public void OnlyALostPlacementAtOrAboveTheThresholdCountsAsARound(long thresholdAboveLost, bool counted)
    {
        (Func<int> moves, Action<int> reject) = InstallIssueCycle();
        RunTicksUntil(() => moves() >= 1); // 첫 계획과 처음 배치
        PlanFile first = PlanFile.Load(_game.Plan);
        long lostBytes = ReapplyEnv.JournalDoneClusters(JournalPath, first) * Cb;
        Assert.True(lostBytes > 0);
        Relocator.PlacementLostMinBytes = lostBytes + thresholdAboveLost;
        OptimizerMovesTheFilesAway(); // 놓은 조각이 모두 제자리를 잃음

        reject(1);
        RunTicksUntil(() => moves() >= 4, ticks: 400);

        Assert.Equal(counted ? 1 : 0, _status.ReapplyRoundsWithin("bench", RepeatWindow));
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void TheReplanOfAPlanSetAsideBecauseTheZoneWasTakenCountsOnlyWhenWhatWasPlacedIsLost(bool movedAway, int rounds)
    {
        bool first = true;
        _w.MakePlanAt = (sessions, origin, zoneMib, lcn, used) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], zoneStartLcn: lcn, preserveBytes: 64 << 20, fixedClusters: used);
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            ApplyReport rep = real(g, plan, options, ct);
            if (options.DryRun)
            {
                rep.TasksTodo = 1;
                rep.BytesTodo = 8192;
            }
            else if (first)
            {
                first = false;
                if (movedAway)
                {
                    OptimizerMovesTheFilesAway();
                }

                rep.Stopped = Applier.ZoneOccupied; // 존이 막혀 멈춤: 계획을 치우고 다시 계획
            }
            else
            {
                rep.MovedBytes = Math.Max(rep.MovedBytes, 8192);
            }

            return rep;
        };
        _relocator = null;
        Relocator.PlacementLostMinBytes = 64 << 10;
        RunTicksUntil(() => _status.Get("bench")?.Stage == AutoStage.Done, ticks: 100);

        Assert.Contains(_log.Lines, l => l.Contains("zone region appears occupied, plan set aside", StringComparison.Ordinal));
        Assert.Equal(rounds, _status.ReapplyRoundsWithin("bench", RepeatWindow));
    }

    [Fact]
    public void ALayoutSettingChangeAloneBetweenTheSetAsidePlanAndItsReplanIsNotCountedAsLostPlacement()
    {
        // 범위 계획으로 놓은 뒤 존이 막혀 치우고, 그 사이 설정이 whole_file 로 바뀌어 후보가 통째 계획이 됨: 놓은 조각은 그대로이므로 0회
        bool first = true;
        UseWholeFile(whole: false); // 통째 계획 함수는 연결하고 설정은 ranges
        _w.MakePlanAt = (sessions, origin, zoneMib, lcn, used) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], zoneStartLcn: lcn, preserveBytes: 64 << 20, fixedClusters: used);
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            ApplyReport rep = real(g, plan, options, ct);
            if (options.DryRun)
            {
                rep.TasksTodo = 1;
                rep.BytesTodo = 8192;
            }
            else if (first)
            {
                first = false;
                SetAuto(a => a with { RelocateLayout = RelocateLayouts.WholeFile }); // 설정이 통째 배치로 바뀜: 다시 계획할 후보는 통째 계획
                rep.Stopped = Applier.ZoneOccupied;
            }
            else
            {
                rep.MovedBytes = Math.Max(rep.MovedBytes, 8192);
            }

            return rep;
        };
        _relocator = null;
        Relocator.PlacementLostMinBytes = 64 << 10;
        RunTicksUntil(() => _status.Get("bench")?.Stage == AutoStage.Done, ticks: 100);

        Assert.NotEmpty(_wholeCalls); // 후보가 통째 계획으로 만들어졌음
        Assert.Equal(0, _status.ReapplyRoundsWithin("bench", RepeatWindow));
    }

    [Fact]
    public void AReplanForVolatileFilesComparesNothingAgainstAnOldStalePlanAndCountsNoRound()
    {
        // 가변 파일 표시로 활성 계획을 치우지 않고 다시 계획: 남아 있던 예전 .stale 은 기준이 아님
        (Func<int> moves, Action<int> _) = InstallIssueCycle();
        RunTicksUntil(() => moves() >= 1); // 처음 배치: 저널에 done 이 생김
        PlanFile active = PlanFile.Load(_game.Plan);
        PlanFile staleOld = active with { Stats = active.Stats };
        staleOld.Stats["residual"] = 1L;
        staleOld.Stats["in_place_bytes"] = 1L << 30; // 크게 놓은 것으로 보이는 예전 계획
        staleOld.Save(_game.Plan + ".stale");
        _status.EnsureReapplyLimit("bench", string.Empty, RelocateLayouts.Ranges);
        _w.Volatile[F1] = "rewritten by the game"; // 트레이스가 가변으로 표시

        RunTicksUntil(() => moves() >= 3, ticks: 100);

        Assert.Contains(_log.Lines, l => l.Contains("planning again", StringComparison.Ordinal) && l.Contains("rewritten", StringComparison.Ordinal));
        Assert.Equal(0, _status.ReapplyRoundsWithin("bench", RepeatWindow));
        Assert.False(_status.ReapplyLimit("bench")?.HandoffPending ?? false);
    }

    [Fact]
    public void AJournalReadErrorWhileReplanningASetAsidePlanHoldsTemporarilyInsteadOfFailingThePlan()
    {
        bool first = true;
        _w.MakePlanAt = (sessions, origin, zoneMib, lcn, used) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], zoneStartLcn: lcn, preserveBytes: 64 << 20, fixedClusters: used);
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            ApplyReport rep = real(g, plan, options, ct);
            if (options.DryRun)
            {
                rep.TasksTodo = 1;
                rep.BytesTodo = 8192;
            }
            else if (first)
            {
                first = false;
                rep.Stopped = Applier.ZoneOccupied;
            }

            return rep;
        };
        _w.JournalDoneClusters = (_, _) => throw new IOException("journal is locked");
        _relocator = null;
        RunTicksUntil(() => _log.Lines.Any(l => l.Contains("will retry", StringComparison.Ordinal)), ticks: 40);

        Assert.True(_log.Lines.Any(l => l.Contains("journal is locked", StringComparison.Ordinal) && l.Contains("nothing was changed, will retry", StringComparison.Ordinal)), string.Join(Environment.NewLine, _log.Lines));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("replan failed", StringComparison.Ordinal));
        Assert.NotEqual(AutoStage.Done, Entry.Stage);
        Assert.True(File.Exists(_game.Plan + ".stale")); // 치운 계획은 그대로 (되살리지 않음)
    }

    private void RunTicksUntil(Func<bool> done, int ticks = 200)
    {
        for (int i = 0; i < ticks && !done(); i++)
        {
            Tick();
            _w.Now += Step;
        }
    }

    [Fact]
    public void ThePauseAppliesToAWholeFilePlanTheSameWay()
    {
        UseWholeFile();
        Tick();
        _w.Now += Step;
        RunUntilDone();
        Assert.NotNull(WholeFileLayout.WholeFiles(PlanFile.Load(_game.Plan)));
        _ = Relocator.ReapplyBlock(_game);

        ReappliedRound();
        ReappliedRound();
        ReappliedRound();

        Assert.Equal(ReapplyVerdict.ReapplyLimit, Relocator.ReapplyBlock(_game));
    }

    [Fact]
    public void ThePauseAndTheRoundsSurviveAServiceRestart()
    {
        ArmRepeatLimit();
        ReappliedRound();
        ReappliedRound();
        ReappliedRound();

        var restarted = new AutoStatus(Path.Combine(Root, "auto_status.json"), _log, () => _w.Now, ticks: FakeClock.Ticks(() => _w.Now));
        var relocator = new AutoRelocator(WorldEnv(), restarted, _log, () => _settings);

        Assert.Equal(3, restarted.ReapplyRoundsWithin("bench", RepeatWindow));
        Assert.Equal(ReapplyVerdict.ReapplyLimit, relocator.ReapplyBlock(_game));
    }
}
