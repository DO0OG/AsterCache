using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 같은 조각을 되풀이해 옮기는 회차의 비율 계산.
public sealed class MoveRepeatTests
{
    private const long Cb = 4096;
    private const long Clusters = (80L << 20) / Cb; // 80 MiB

    private static JObject Planned(long idx, string path, long vcn, long clusters) => new() { { "type", "step" }, { "idx", idx }, { "state", "planned" }, { "path", path }, { "vcn", vcn }, { "clusters", clusters } };

    private static JObject Done(long idx) => new() { { "type", "step" }, { "idx", idx }, { "state", "done" } };

    private static List<JObject> Moved(string path, long vcn, long clusters) => [Planned(0, path, vcn, clusters), Done(0)];

    [Fact]
    public void TheSamePieceCountsAsRepeatedAndADifferentPieceDoesNot()
    {
        (long same, long total) = MoveRepeat.Measure(Moved("a", 0, Clusters), 0, Moved("a", 0, Clusters), Cb);
        Assert.Equal((Clusters * Cb, Clusters * Cb), (same, total));
        Assert.True(MoveRepeat.IsRepeat(same, total));

        (long otherRange, _) = MoveRepeat.Measure(Moved("a", 0, Clusters), 0, Moved("a", Clusters, Clusters), Cb);
        (long otherFile, _) = MoveRepeat.Measure(Moved("a", 0, Clusters), 0, Moved("b", 0, Clusters), Cb);
        Assert.Equal((0L, 0L), (otherRange, otherFile));
    }

    [Fact]
    public void AnOverlapCountsOnlyTheSharedClusters()
    {
        (long shared, long total) = MoveRepeat.Measure(Moved("a", 0, 2 * Clusters), 0, Moved("a", Clusters, 2 * Clusters), Cb);

        Assert.Equal((Clusters * Cb, 2 * Clusters * Cb), (shared, total));
        Assert.True(MoveRepeat.IsRepeat(shared, total)); // 정확히 절반이면 비율 기준 (50%) 에 닿음
    }

    [Fact]
    public void OnlyPiecesDoneAfterTheMarkAndDoneInTheEarlierJournalCount()
    {
        List<JObject> current = [Planned(0, "a", 0, Clusters), Done(0), Planned(1, "b", 0, Clusters), Done(1)]; // 앞의 두 줄 (a) 은 이전 회차에서 끝남
        List<JObject> previous = [Planned(0, "a", 0, Clusters), Planned(1, "b", 0, Clusters), Done(1)]; // a 는 계획만 되고 끝나지 않음

        (long repeated, long moved) = MoveRepeat.Measure(current, 2, previous, Cb);

        Assert.Equal((Clusters * Cb, Clusters * Cb), (repeated, moved)); // mark 뒤에 끝난 b 만, 앞선 저널에서 b 가 끝났으므로 되풀이
        Assert.Equal((0L, Clusters * 2 * Cb), MoveRepeat.Measure(current, 0, [Planned(0, "c", 0, Clusters), Done(0)], Cb)); // mark 0 이면 둘 다 이번 회차, 앞선 저널과는 다른 조각
    }

    private static List<JObject> MovedFrom(string path, long vcn, long clusters, long fromLcn, long target)
    {
        JObject planned = Planned(0, path, vcn, clusters);
        planned["target_lcn"] = target;
        planned["from"] = new List<object?> { new List<object?> { vcn, fromLcn, clusters } };
        return [planned, Done(0)];
    }

    [Fact]
    public void APieceMovedAwayFromWhereTheEarlierJournalPlacedItCountsAndOneLeftInPlaceDoesNot()
    {
        List<JObject> earlier = MovedFrom("a", 0, Clusters, 9000, 1000); // 앞선 저널은 이 범위를 LCN 1000 에 놓음

        (long movedBack, long total) = MoveRepeat.Measure(MovedFrom("a", 0, Clusters, 5000, 2000), 0, earlier, Cb); // 최적화가 5000 으로 옮겨 놓은 것을 다시 옮김
        (long inPlace, _) = MoveRepeat.Measure(MovedFrom("a", 0, Clusters, 1000, 2000), 0, earlier, Cb); // 서비스가 놓은 자리 (1000) 에서 새 zone 으로 일부러 옮김 (다시 계획)

        Assert.Equal((Clusters * Cb, Clusters * Cb), (movedBack, total));
        Assert.Equal(0, inPlace);
    }

    [Fact]
    public void OnlyTheClustersThatLeftADifferentPlaceCountWithinOneOverlap()
    {
        List<JObject> earlier = MovedFrom("a", 0, 2 * Clusters, 9000, 1000);
        JObject planned = Planned(0, "a", 0, 2 * Clusters);
        planned["target_lcn"] = 2000L;
        planned["from"] = new List<object?> { new List<object?> { 0L, 1000L, Clusters }, new List<object?> { Clusters, 70000L, Clusters } }; // 앞 절반은 놓은 자리, 뒤 절반은 다른 곳

        (long repeated, long total) = MoveRepeat.Measure([planned, Done(0)], 0, earlier, Cb);

        Assert.Equal((Clusters * Cb, 2 * Clusters * Cb), (repeated, total));
    }

    [Fact]
    public void ARoundNeedsEnoughBytesAndShareToCountAsARepeat()
    {
        Assert.False(MoveRepeat.IsRepeat(63L << 20, 63L << 20)); // 옮긴 양이 64 MiB 미만
        Assert.False(MoveRepeat.IsRepeat(63L << 20, 100L << 20)); // 되풀이가 64 MiB 미만
        Assert.False(MoveRepeat.IsRepeat(64L << 20, 200L << 20)); // 비율 50% 미만
        Assert.True(MoveRepeat.IsRepeat(64L << 20, 128L << 20));
    }
}

/// 되풀이 회차가 두 번 이어지면 그 게임의 범위 방식 재배치를 쉬고, 계획이나 원본이 바뀌면 풀림.
public sealed partial class AutoRelocatorTests
{
    private const long RepeatClusters = (80L << 20) / Cb; // 한 회차가 80 MiB

    private PlanFile PlannedGame(int minutes = 0)
    {
        Run(TimeSpan.FromMinutes(minutes)); // 계획을 만들고 재배치
        _w.FileTimes[_game.Plan] = _w.Now;
        return PlanFile.Load(_game.Plan);
    }

    private long _fromLcn = 5000; // 이번 회차 조각의 출발 자리: 앞선 저널이 놓은 자리 (PlacedAt) 와 다르면 다른 도구가 옮겨 놓은 것
    private const long PlacedAt = 1000;

    private List<JObject> Piece(string path, long vcn) =>
    [
        new() { { "type", "step" }, { "idx", 0L }, { "state", "planned" }, { "path", path }, { "vcn", vcn }, { "clusters", RepeatClusters }, { "target_lcn", PlacedAt }, { "from", new List<object?> { new List<object?> { vcn, _fromLcn, RepeatClusters } } } },
        new() { { "type", "step" }, { "idx", 0L }, { "state", "done" } },
    ];

    // 가짜 저널: 이번 회차는 (F1, 0) 조각을 옮겼고, 앞선 저널은 before 가 돌려주는 조각 (없으면 앞선 저널 없음).
    private void JournalsOf(Func<(string Path, long Vcn)?> before)
    {
        _w.JournalRounds = g =>
        {
            List<JObject> current = [.. Enumerable.Range(0, _w.JournalRows(g)).Select(_ => new JObject { { "type", "note" } }), .. Piece(F1, 0)];
            return (current, before() is { } b ? Piece(b.Path, b.Vcn) : null);
        };
        _relocator = null;
    }

    private void TwoRepeatRounds(PlanFile plan)
    {
        Relocator.NoteRepeatRound(_game, plan, 0, 1);
        Relocator.NoteRepeatRound(_game, plan, 0, 1);
    }

    [Fact]
    public void TwoRepeatRoundsInARowRestTheGameAndTheRestSurvivesARestart()
    {
        PlanFile plan = PlannedGame();
        JournalsOf(() => (F1, 0));

        Relocator.NoteRepeatRound(_game, plan, 0, 1);
        Assert.Equal(1, _status.Recovery("bench")!.RepeatRounds);
        Assert.Null(_status.Recovery("bench")!.RestStamp);

        Relocator.NoteRepeatRound(_game, plan, 0, 1);
        Assert.Equal(_w.Now.UtcTicks, _status.Recovery("bench")!.RestStamp);
        Assert.Contains(_log.Lines, l => l.Contains("a drive optimization or another tool keeps moving pieces back", StringComparison.Ordinal) && l.Contains("whole_file", StringComparison.Ordinal));

        var restarted = new AutoStatus(Path.Combine(Root, "auto_status.json"), _log, () => _w.Now, ticks: FakeClock.Ticks(() => _w.Now));
        Assert.Equal((2, _w.Now.UtcTicks), (restarted.Recovery("bench")!.RepeatRounds, restarted.Recovery("bench")!.RestStamp!.Value));
    }

    [Fact]
    public void ARoundThatMovesOtherPiecesStartsTheCountOver()
    {
        PlanFile plan = PlannedGame();
        (string, long)? earlier = (F1, 0);
        JournalsOf(() => earlier);
        Relocator.NoteRepeatRound(_game, plan, 0, 1);
        Assert.Equal(1, _status.Recovery("bench")!.RepeatRounds);

        earlier = (F1, RepeatClusters); // 앞선 저널은 다른 범위
        Relocator.NoteRepeatRound(_game, plan, 0, 1);
        Assert.True(_status.Recovery("bench") is { RepeatRounds: 0, RestStamp: null });

        earlier = (F1, 0);
        Relocator.NoteRepeatRound(_game, plan, 0, 1);
        earlier = null; // 앞선 저널 없음
        Relocator.NoteRepeatRound(_game, plan, 0, 1);
        Assert.True(_status.Recovery("bench") is { RepeatRounds: 0, RestStamp: null });
    }

    [Fact]
    public void AWholeFilePlanIsNeverRested()
    {
        PlanWholeFiles();
        Tick();
        RunUntilDone();
        _w.FileTimes[_game.Plan] = _w.Now;
        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.True(plan.Stats.ContainsKey(WholeFileLayout.RuleKey));
        JournalsOf(() => (F1, 0));

        for (int i = 0; i < 3; i++)
        {
            TwoRepeatRounds(plan);
        }

        Assert.True(_status.Recovery("bench") is null or { RepeatRounds: 0, RestStamp: null });
    }

    // 쉬는 중인 게임을 세우고 (한 회차 32 클러스터씩 쓰다 멈추는 부분 적용), 쉬는 동안 실제 이동이 없고 정기 재적용도 비켜 서는지 확인.
    private bool _repeating;

    private void RestedGame()
    {
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        int runs = 0;
        _w.Apply = (g, p, options, ct) =>
        {
            if (!options.DryRun)
            {
                runs++;
            }

            return real(g, p, options.DryRun ? options : options with { WriteCap = 32 * Cb }, ct);
        };
        JournalsOf(() => _repeating ? (F1, 0) : null); // 되풀이는 아래에서 켤 때부터
        _ = UseWiredPrewarm();
        PlanFile plan = PlannedGame(4);
        Assert.True(runs > 0 && Entry.RelocatedAt is null); // 부분 적용으로 멈춤
        _repeating = true;
        TwoRepeatRounds(plan);
        Assert.NotNull(_status.Recovery("bench")!.RestStamp);

        int before = runs;
        Run(TimeSpan.FromMinutes(20));

        Assert.Equal(before, runs); // 쉬는 동안 이동 없음
        Assert.Contains("relocation paused", Entry.Detail, StringComparison.Ordinal);
        Assert.Equal(ReapplyVerdict.NotApplied, Relocator.ReapplyBlock(_game)); // 정기 재적용도 쉼
        _applyRuns = runs;
    }

    private int _applyRuns;

    [Fact]
    public void ARestedGameMovesAgainOnceThePlanChanges()
    {
        RestedGame();
        int before = _applyRuns;

        _w.FileTimes[_game.Plan] = _w.Now + TimeSpan.FromMinutes(1); // 다시 계획해 계획 파일이 새로 쓰임
        Run(TimeSpan.FromMinutes(5));

        Assert.Null(_status.Recovery("bench")?.RestStamp);
        Assert.Contains(_log.Lines, l => l.Contains("relocation resumes: the plan changed", StringComparison.Ordinal));
        Assert.True(before >= 1);
    }

    [Fact]
    public void ARestedGameIsStillWarmedForANewTrace()
    {
        RestedGame();
        int before = Streams;

        _w.Now += TimeSpan.FromMinutes(16);
        _w.FileTimes[AutoTracer.RawTracePath(_game)] = _w.Now; // 마지막 예열보다 새 트레이스
        Run(TimeSpan.FromMinutes(2));

        Assert.True(Streams > before, Why());
        Assert.NotNull(_status.Recovery("bench")!.RestStamp);
    }

    [Fact]
    public void ARestedGameResumesWhenASourceFileChanges()
    {
        RestedGame();
        _w.Now += AutoRelocator.StaleCheckEvery;

        UpdateRewritten(F1); // 게임이 원본을 다시 씀: 지문이 달라짐
        Run(TimeSpan.FromMinutes(1));

        Assert.Null(_status.Recovery("bench")?.RestStamp);
        Assert.Contains(_log.Lines, l => l.Contains("relocation resumes: source files changed", StringComparison.Ordinal));
    }

    [Fact]
    public void ARestedGameResumesWhenTheLayoutBecomesWholeFile()
    {
        RestedGame();

        _settings = _settings with { Auto = _settings.Auto with { RelocateLayout = RelocateLayouts.WholeFile } };
        Run(TimeSpan.FromMinutes(1));

        Assert.Null(_status.Recovery("bench")?.RestStamp);
        Assert.Contains(_log.Lines, l => l.Contains("relocation resumes: layout is now whole_file", StringComparison.Ordinal));
    }

    [Fact]
    public void RoundsThatMoveFromWhereTheServicePlacedThemAfterAnUpdateOrAGrownTraceDoNotRest()
    {
        PlanFile plan = PlannedGame();
        JournalsOf(() => (F1, 0));
        _fromLcn = PlacedAt; // 다시 계획한 새 zone 으로 서비스가 자기가 놓은 자리에서 옮김 (업데이트 감지, 트레이스 증가)

        for (int i = 0; i < 4; i++)
        {
            Relocator.NoteRepeatRound(_game, plan, 0, 1);
        }

        Assert.True(_status.Recovery("bench") is null or { RepeatRounds: 0, RestStamp: null });
    }

    [Fact]
    public void RoundsThatMovePiecesBackAfterADriveOptimizationMovedThemKeepResting()
    {
        PlanFile plan = PlannedGame();
        JournalsOf(() => (F1, 0));
        _fromLcn = PlacedAt + 4000; // 최적화가 zone 밖으로 옮겨 놓은 조각을 다시 옮김

        TwoRepeatRounds(plan);

        Assert.NotNull(_status.Recovery("bench")!.RestStamp);
    }

    [Fact]
    public void AFailureWhileComparingJournalsNeverUndoesTheRound()
    {
        _w.JournalRounds = _ => throw new ArgumentException("bad bytes in the earlier journal"); // 엄격 UTF-8 해독 실패 같은 예상 밖 예외
        _relocator = null;

        Run(TimeSpan.FromMinutes(3));

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("reverted", StringComparison.Ordinal) || l.Contains("stopped: relocation failed", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("repeat check skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void TheResidualReplanAndTheHoldKeepTheRepeatFields()
    {
        DoneAndReadyToReplan(out _);
        CountResidualsAndBlockDryRuns();
        _status.SetRecovery("bench", new RecoveryAttempt(null, null, 0, null, RepeatRounds: 1));
        Assert.True(Relocator.HandOffZoneBlocked(_game));

        Run(TimeSpan.FromMinutes(4));

        Assert.True(_handOffResiduals == 1, Why());
        RecoveryAttempt held = _status.Recovery("bench")!;
        Assert.Equal((1, 1, true), (held.Replans, held.RepeatRounds, held.NextAllowed is not null)); // 잔여 재계획과 보류를 지나도 남음
    }

    [Fact]
    public void AStatusFileFromAnEarlierVersionWithoutTheNewKeysStillLoads()
    {
        string path = Path.Combine(Root, "auto_status_old.json");
        File.WriteAllText(path, """{"replan_attempts": {"bench": {"plan_sha": "abc", "rejected_lcn": 7, "replans": 1, "occupied_streak": 2}}}""");

        RecoveryAttempt loaded = new AutoStatus(path, _log).Recovery("bench")!;

        Assert.Equal((1, 2, 0, (long?)null), (loaded.Replans, loaded.OccupiedStreak, loaded.RepeatRounds, loaded.RestStamp));
    }
}
