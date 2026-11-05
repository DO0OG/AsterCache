using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Service;

/// 부분 적용된 계획의 이어 옮기기: 실기기에서 본 두 상태 (콜 오브 듀티형, 리그 오브 레전드형) 를 줄여 만들고, 서비스가 수동 조작 없이 복구해 남은 부분만 옮기는지 봅니다.
public sealed partial class AutoRelocatorTests
{
    private static ApplyOptions Partial(long clusters) => new(Execute: true, ChunkBytes: 8 * Cb, WriteCap: clusters * Cb);

    private string[] JournalArchives() => Directory.GetFiles(Root).Where(f => Path.GetFileName(f).StartsWith("j.jsonl.", StringComparison.Ordinal)).Order().ToArray();

    private void WaitingForTheRest() => _status.Set("bench", "auto", null, AutoStage.WaitingIdle, "write limit reached, will continue");

    // 목표 자리에 이미 놓인 클러스터를 뺀 남은 양
    private long Remaining(PlanFile plan) => plan.Tasks.Sum(t => t.Clusters - PlanGuards.DonePrefix(Vol.Ext[t.Path], t.Vcn, t.Clusters, t.TargetLcn));

    private string Why() => Entry.Stage + " " + Entry.Detail + Environment.NewLine + string.Join(Environment.NewLine, _log.Lines);

    private GameSettings AddOtherGameWithAPlanAt(long zoneStart, string volume)
    {
        GameSettings other = _game with { Name = "other", ExeNames = ["other.exe"], Plan = Path.Combine(Root, "plans", "other.plan.json") };
        _settings = _settings with { Games = [_game, other] };
        (Make().Resolve(zoneStart) with { Volume = volume }).Save(other.Plan);
        return other;
    }

    private void AssertStableAfterRestart(long movesBefore)
    {
        _relocator = null;
        _w.Now += TimeSpan.FromMinutes(5);
        Run(TimeSpan.FromMinutes(4));

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(movesBefore, Vol.Moves.Count);
        Assert.Null(_status.Recovery("bench"));
    }

    [Fact]
    public void CallOfDutyShapedStateIsRecoveredByTheServiceAndOnlyTheRemainderMoves()
    {
        PlanFile first = Make().Resolve(20000);
        Assert.Equal("write_cap", Apply(first, Partial(32)).Stopped); // 새 존 계획이 접두부 일부만 적용됨
        long moved = Vol.Moves.Sum(m => m.N);
        Assert.InRange(moved, 1, 79);
        int rowsBefore = JournalRowCount();
        Make().Resolve(30000).Save(_game.Plan); // 활성 계획이 다른 존의 새 계획으로 덮어써짐, 저널은 첫 계획의 것
        string otherFile = Path.Combine(Root, "other.bin");
        Vol.Ext[otherFile] = [new(0, 20000 + moved, 16)]; // 옛 존의 다음 구간은 다른 파일이 점유
        WaitingForTheRest();
        Vol.Moves.Clear();

        Run(TimeSpan.FromMinutes(4));
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.Equal(80 - moved, Vol.Moves.Sum(m => m.N)); // 이동량 = 실제 잔여량
        Assert.DoesNotContain(Vol.Moves, m => m.Lcn < 20000 + moved && m.Lcn + m.N > 20000); // 이미 목표에 있는 범위로는 이동 0
        Assert.Equal([new Extent(0, 20000 + moved, 16)], Vol.Ext[otherFile]);
        Assert.Equal(0, Remaining(PlanFile.Load(_game.Plan)));
        Assert.Equal(rowsBefore, new Journal(Assert.Single(JournalArchives())).Rows.Count); // 첫 저널은 지워지지 않고 보관
        Assert.Contains(_log.Lines, l => l.Contains("replanning only the remaining part", StringComparison.Ordinal));
        AssertStableAfterRestart(Vol.Moves.Count);
    }

    [Fact]
    public void LeagueOfLegendsShapedStateWithFailedStepsAndAnOccupiedActiveZoneIsRecovered()
    {
        Assert.Equal("write_cap", Apply(Make().Resolve(20000), Partial(32)).Stopped); // 옛 존 A 에 일부
        File.Move(JournalPath, JournalPath + ".20261101T000000Z");
        PlanFile b = Make().Resolve(30000);
        Vol.FailNext = 3; // 새 존 B: 한 조각은 끝내 실패, 나머지는 성공 (end.stopped 없음)
        ApplyReport rep = Apply(b, Partial(1000));
        Assert.Single(rep.Failed);
        Assert.Null(rep.Stopped);
        long left = Remaining(b);
        Assert.InRange(left, 1, 79);
        int rowsOfB = JournalRowCount();
        Make().Resolve(40000).Save(_game.Plan); // 활성 계획은 또 다른 존 C
        AddOtherGameWithAPlanAt(40000, Letter); // C 는 다른 게임 존과 겹침
        Vol.Ext[Path.Combine(Root, "other.bin")] = [new(0, 40040, 16)]; // 그 자리를 다른 파일이 점유
        WaitingForTheRest();
        Vol.Moves.Clear();

        Run(TimeSpan.FromMinutes(4));
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.Equal(left, Vol.Moves.Sum(m => m.N));
        Assert.All(Vol.Moves, m => Assert.False(m.Lcn < 40080 && m.Lcn + m.N > 40000)); // 다른 게임이 쓸 구간은 피함
        Assert.Equal(0, Remaining(PlanFile.Load(_game.Plan)));
        Assert.Contains(rowsOfB, JournalArchives().Select(f => new Journal(f).Rows.Count)); // B 의 저널도 지워지지 않고 보관 (A 의 저널은 시험이 미리 보관)
        AssertStableAfterRestart(Vol.Moves.Count);
    }

    [Fact]
    public void SourceChangeOnAPartiallyAppliedPlanKeepsTheMovedPartsAndTheJournalIsArchived()
    {
        PlanFile first = Make().Resolve(20000);
        Assert.Equal("write_cap", Apply(first, Partial(32)).Stopped);
        long moved = Vol.Moves.Sum(m => m.N);
        first.Save(_game.Plan);
        WaitingForTheRest();
        Vol.Moves.Clear();
        Fingerprint real = PlanFile.FingerprintOf(F2);
        _w.Stat = p => p == F2 ? new Fingerprint(p, real.Size + 1, real.MtimeNs) : PlanFile.FingerprintOf(p); // 원본이 일부 바뀜
        Func<GameSettings, PlanFile, IReadOnlyList<PlanFile>, IReadOnlyList<IReadOnlyList<AsterCache.Core.Trace.TraceEvent>>?, PlanFile> inner = _w.ReplanResidual!;
        _w.ReplanResidual = (g, c, o, sessions) =>
        {
            _w.Stat = PlanFile.FingerprintOf; // 새 계획이 바뀐 원본을 기준으로 삼은 뒤에는 다시 바뀐 것으로 보지 않게
            return inner(g, c, o, sessions);
        };

        Run(TimeSpan.FromMinutes(4));
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.Contains(_log.Lines, l => l.Contains("source files changed since the plan", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("partially applied plan replanned", StringComparison.Ordinal));
        Assert.True(Vol.Moves.Sum(m => m.N) <= 80 - moved);
        Assert.Single(JournalArchives());
        Assert.Equal(0, Remaining(PlanFile.Load(_game.Plan)));
    }

    [Fact]
    public void ARepeatedConflictHoldsFor30MinutesAcrossTicksRestartsAndNewPlanShas()
    {
        Assert.Equal("write_cap", Apply(Make().Resolve(20000), Partial(32)).Stopped);
        Make().Resolve(30000).Save(_game.Plan);
        WaitingForTheRest();
        int residuals = 0, dryRuns = 0;
        Func<GameSettings, PlanFile, IReadOnlyList<PlanFile>, IReadOnlyList<IReadOnlyList<AsterCache.Core.Trace.TraceEvent>>?, PlanFile> inner = _w.ReplanResidual!;
        _w.ReplanResidual = (g, c, o, s) =>
        {
            residuals++;
            return inner(g, c, o, s);
        };
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            if (!options.DryRun)
            {
                return real(g, plan, options, ct);
            }

            dryRuns++;
            throw new PlanRejectedException("zone_not_free: LCN 777 is in use by another file");
        };

        Run(TimeSpan.FromMinutes(4));

        Assert.Equal(1, residuals); // 첫 점유 충돌에 잔여 재계획 한 번
        Assert.Equal(1, dryRuns); // 저널 불일치는 dry-run 전에 알아내 재계획하고, 그 뒤 dry-run 이 또 막히면 보류
        RecoveryAttempt held = _status.Recovery("bench")!;
        Assert.Equal((777L, 1), (held.RejectedLcn, held.Replans));
        Assert.InRange(held.NextAllowed!.Value, _w.Now, _w.Now + AutoRelocator.RecoveryHold);
        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);

        for (int i = 0; i < 10; i++)
        {
            _relocator = null; // 서비스를 다시 시작해도 보류는 풀리지 않음
            _w.Now += TimeSpan.FromMinutes(2);
            Tick();
        }

        Assert.Equal((1, 1), (residuals, dryRuns));
        Assert.Equal(held.NextAllowed, new AutoStatus(Path.Combine(Root, "auto_status.json"), _log).Recovery("bench")!.NextAllowed); // 파일에 저장됨

        _w.Now = held.NextAllowed!.Value + TimeSpan.FromMinutes(1);
        Run(TimeSpan.FromMinutes(4));

        Assert.Equal((2, 3), (residuals, dryRuns)); // 보류가 끝나면 다음 주기에 한 번 더, 새 계획 SHA 가 생겨도 한 주기에 한 번뿐
        Assert.True(_status.Recovery("bench")!.NextAllowed > _w.Now);
    }

    [Fact]
    public void ALockedJournalDefersTheReplanWithoutLosingAnythingAndTheSameRunFinishesItOnceUnlocked()
    {
        PlanFile first = Make().Resolve(20000);
        Assert.Equal("write_cap", Apply(first, Partial(32)).Stopped);
        long moved = Vol.Moves.Sum(m => m.N);
        Make().Resolve(30000).Save(_game.Plan);
        string planSha = PlanFile.Load(_game.Plan).Sha();
        byte[] journalBytes = File.ReadAllBytes(JournalPath);
        WaitingForTheRest();
        Vol.Moves.Clear();

        using (new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read)) // 열려 있어 보관할 수 없음
        {
            Run(TimeSpan.FromMinutes(6));

            Assert.Equal(AutoStage.WaitingIdle, Entry.Stage); // 실패로 기록하지 않고 기다림
            Assert.Contains(_log.Lines, l => l.Contains("nothing was changed, will retry", StringComparison.Ordinal));
            Assert.Empty(Vol.Moves); // 아무것도 더 옮기지 않음
            Assert.Equal(planSha, PlanFile.Load(_game.Plan).Sha()); // 새 계획은 승격되지 않음
            Assert.Equal(journalBytes, File.ReadAllBytes(JournalPath)); // 저널은 그대로
            Assert.Empty(JournalArchives());
        }

        _w.Now += AutoRelocator.TransientRetry; // 서비스를 다시 시작하지 않음: 잠금이 풀린 뒤 다음 틱들에서 이어짐
        Run(TimeSpan.FromMinutes(4));
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.Equal(80 - moved, Vol.Moves.Sum(m => m.N)); // 이미 옮긴 것은 다시 옮기지 않고 잔여만
        Assert.Equal(journalBytes.Length, new FileInfo(Assert.Single(JournalArchives())).Length);
    }

    [Fact]
    public void ATemporaryErrorWhileFinishingAnInterruptedSwitchAtStartupIsRetriedInTheSameRun()
    {
        PlanFile first = Make().Resolve(20000);
        Assert.Equal("write_cap", Apply(first, Partial(32)).Stopped);
        first.Save(_game.Plan);
        PlanFile next = Make().Resolve(30000);
        Assert.Throws<InvalidOperationException>(() => PlanSwitch.Commit(next, _game.Plan, JournalPath, () => _w.Now.UtcDateTime, hooks: Hooks(), checkpoint: p =>
        {
            if (p == "recorded")
            {
                throw new InvalidOperationException("crash");
            }
        }));
        WaitingForTheRest();
        Func<GameSettings, SwitchResult?> real = _w.ResumeSwitch;
        int failures = 2;
        _w.ResumeSwitch = g => failures-- > 0 ? throw new IOException("locked") : real(g);

        Tick();

        Assert.True(File.Exists(_game.Plan + PlanSwitch.RecordSuffix)); // 아직 끝내지 못함
        Assert.NotEqual(AutoStage.Failed, Entry.Stage);

        for (int i = 0; i < 4; i++)
        {
            _w.Now += Step;
            Tick(); // 서비스를 다시 시작하지 않고 다음 틱들에서 재개
        }

        Assert.False(File.Exists(_game.Plan + PlanSwitch.RecordSuffix));
        Assert.Equal(next.Sha(), PlanFile.Load(_game.Plan).Sha());
        Assert.Single(JournalArchives());
    }

    [Fact]
    public void ARejectionRepeatedAfterTheHoldForTheSamePlanJournalAndReasonStopsInsteadOfRetryingEvery30Minutes()
    {
        Assert.Equal("write_cap", Apply(Make().Resolve(20000), Partial(32)).Stopped);
        Make().Resolve(30000).Save(_game.Plan);
        WaitingForTheRest();
        int residuals = 0;
        _w.ReplanResidual = (_, _, _, _) =>
        {
            residuals++;
            throw new PlanRejectedException("no free run of 48 clusters");
        };

        Run(TimeSpan.FromMinutes(4));

        Assert.Equal((1, AutoStage.WaitingIdle), (residuals, Entry.Stage)); // 첫 거절은 30 분 보류
        RecoveryAttempt held = _status.Recovery("bench")!;
        Assert.Equal("no free run of 48 clusters", held.Reason);

        _w.Now = held.NextAllowed!.Value + TimeSpan.FromMinutes(1); // 보류가 끝난 뒤 같은 입력, 같은 사유
        Run(TimeSpan.FromMinutes(4));

        Assert.Equal(2, residuals);
        Assert.Equal(AutoStage.Failed, Entry.Stage);
        Assert.Contains("recovery stopped", Entry.Detail, StringComparison.Ordinal);
        Assert.Contains("no free run of 48 clusters", Entry.Detail, StringComparison.Ordinal);

        for (int i = 0; i < 8; i++)
        {
            _w.Now += TimeSpan.FromMinutes(31); // 30 분마다 되풀이하지 않음
            Tick();
        }

        Assert.Equal((2, AutoStage.Failed), (residuals, Entry.Stage));

        _w.FileTimes[AutoTracer.RawTracePath(_game)] = _w.Now; // 새 트레이스가 쓰이면 다시 평가
        Run(TimeSpan.FromMinutes(4));

        Assert.Equal(3, residuals);
        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.NotEqual(held.Stamp, _status.Recovery("bench")!.Stamp);
    }

    [Fact]
    public void AResidualPlanThatLeavesEveryFileUnreadableIsNeverCompletedAndIsPlannedAgainLaterKeepingWhatWasMoved()
    {
        PlanFile first = Make().Resolve(20000);
        Assert.Equal("write_cap", Apply(first, Partial(32)).Stopped);
        long moved = Vol.Moves.Sum(m => m.N);
        Make().Resolve(30000).Save(_game.Plan);
        WaitingForTheRest();
        Vol.Moves.Clear();
        bool readable = false;
        var unreadable = new ApplyHooks { Extents = p => readable ? Vol.Ext[p] : null, Digest = (p, size, extra) => SampleHasher.Compute(p, size, extra, null), SkipReason = _ => null };
        _w.ReplanResidual = (g, c, o, sessions) => ResidualReplan.Run(c, g.Plan, JournalPath, Vol, _w.Reserved(g), unreadable, () => _w.Now.UtcDateTime,
            sessions is null ? null : (p, path) => PlanOutput.Save(p, sessions, path), o).Plan;
        _w.MakePlanAt = (sessions, origin, zoneMib, lcn, used) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], zoneStartLcn: lcn, preserveBytes: 64 << 20, fixedClusters: used);

        Run(TimeSpan.FromMinutes(6));

        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage); // 조회 불가를 "이미 배치됨" 으로 확정하지 않음
        Assert.Null(Entry.RelocatedAt);
        Assert.Contains("unreadable", Entry.Detail, StringComparison.Ordinal);
        Assert.Empty(Vol.Moves);
        Assert.Equal(2, Assert.IsType<AsterCache.Core.Json.JObject>(PlanFile.Load(_game.Plan).Stats["held_files"]).Count);

        readable = true; // 읽히게 된 뒤
        _w.Now += AutoRelocator.UnreadableRetry;
        Run(TimeSpan.FromMinutes(4));
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.Equal(80 - moved, Vol.Moves.Sum(m => m.N)); // 다시 계획해도 이미 옮긴 조각은 그대로, 잔여만
        Assert.Equal(0, Remaining(PlanFile.Load(_game.Plan)));
        Assert.False(PlanFile.Load(_game.Plan).Stats.ContainsKey("held_files"));
    }

    [Fact]
    public void ARemainderReplanWritesThePrefetchFileAgainFromTheTraceOrThePlanOrder()
    {
        Assert.Equal("write_cap", Apply(Make().Resolve(20000), Partial(32)).Stopped);
        Make().Resolve(30000).Save(_game.Plan);
        WaitingForTheRest();
        string prefetch = _game.Plan + PlanOutput.PrefetchSuffix;
        File.WriteAllText(prefetch, "[]"); // 옛 선읽기 파일

        Run(TimeSpan.FromMinutes(4));
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.NotEqual("[]", File.ReadAllText(prefetch)); // 새 계획에 맞게 다시 씀
        Assert.NotEmpty(JsonRead.Parse(File.ReadAllText(prefetch)) as List<object?> ?? []);
    }

    [Fact]
    public void AnExtensionOfAResidualPlanKeepsEveryPieceAlreadyFixedInTheOldZonesAndMovesOnlyNewBlocks()
    {
        PlanFile first = Make().Resolve(20000);
        Assert.Equal("write_cap", Apply(first, Partial(32)).Stopped);
        Make().Resolve(30000).Save(_game.Plan);
        WaitingForTheRest();
        Run(TimeSpan.FromMinutes(4));
        RunUntilDone();
        Assert.True(Entry.Stage == AutoStage.Done, Why());
        PlanFile residual = PlanFile.Load(_game.Plan);
        Assert.Equal(1L, residual.Stats["residual"]);
        Assert.Contains(residual.Tasks, t => t.TargetLcn < residual.Zone.StartLcn || t.TargetLcn >= residual.Zone.StartLcn + residual.Zone.Clusters); // 존 밖 (옛 존) 에 고정된 조각이 있음
        var before = residual.Order.Select(o => (o, Pieces: ZoneAllocator.Pieces(Vol.Ext[o.Path], o.Vcn, o.Vcn + o.Clusters).ToList())).ToList();

        SetAuto(a => a with { TraceRuns = 3, TraceMergeMinMib = 1, PostRelocateRecovery = AutoSettings.RecoveryOff });
        Sessions = [Merged(RestOfTheBlocks())];
        string raw = AutoTracer.RawTracePath(_game);
        _w.Files.Add(TraceWriter.RunPath(raw, 1));
        _w.Files.Add(TraceWriter.RunPath(raw, 2));
        _w.FileTimes[_game.Plan] = _w.Now - TimeSpan.FromHours(1);
        _w.FileTimes[raw] = _w.Now;
        _w.MakePlan = (sessions, origin, zoneMib) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], preserveBytes: 64 << 20);
        _w.MakePlanAt = (sessions, origin, zoneMib, lcn, used) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Vol.Ext[p], zoneStartLcn: lcn, preserveBytes: 64 << 20, fixedClusters: used);
        _w.ArchiveJournal = _ => File.Exists(JournalPath) ? Archive() : null;
        Vol.Moves.Clear();

        Tick();
        Assert.True(File.Exists(_game.Plan + AutoRelocator.ExtendSuffix) || File.Exists(_game.Plan + ".stale"), Why());
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.NotEmpty(Vol.Moves); // 새 블록은 옮김
        Assert.Equal(32L, PlanFile.Load(_game.Plan).Stats["hot_blocks"]);
        foreach ((PlanRange o, List<(long Vcn, long Lcn, long N)> pieces) in before)
        {
            Assert.Equal(pieces, ZoneAllocator.Pieces(Vol.Ext[o.Path], o.Vcn, o.Vcn + o.Clusters).ToList()); // 옛 핫 범위는 그 자리 그대로
        }

        Assert.DoesNotContain(Vol.Moves, m => before.Any(b => b.o.Path == m.Path && m.Vcn < b.o.Vcn + b.o.Clusters && b.o.Vcn < m.Vcn + m.N)); // 옛 존 고정 조각에 Move 0
        Assert.Equal(0, Remaining(PlanFile.Load(_game.Plan)));
    }

    private string Archive()
    {
        string dest = JournalPath + ".old";
        File.Move(JournalPath, dest);
        return dest;
    }

    [Fact]
    public void ASwitchInterruptedAfterTheRecordIsFinishedByTheServiceAtStartup()
    {
        PlanFile first = Make().Resolve(20000);
        Assert.Equal("write_cap", Apply(first, Partial(32)).Stopped);
        first.Save(_game.Plan);
        int rows = JournalRowCount();
        PlanFile next = Make().Resolve(30000);
        Assert.Throws<InvalidOperationException>(() => PlanSwitch.Commit(next, _game.Plan, JournalPath, () => _w.Now.UtcDateTime, hooks: Hooks(), checkpoint: p =>
        {
            if (p == "recorded")
            {
                throw new InvalidOperationException("crash");
            }
        }));
        Assert.True(File.Exists(_game.Plan + PlanSwitch.RecordSuffix));
        WaitingForTheRest();

        Tick(); // 서비스 시작 첫 틱

        Assert.False(File.Exists(_game.Plan + PlanSwitch.RecordSuffix));
        Assert.Equal(next.Sha(), PlanFile.Load(_game.Plan).Sha());
        Assert.Equal(rows, new Journal(Assert.Single(JournalArchives())).Rows.Count);
        Assert.Contains(_log.Lines, l => l.Contains("interrupted plan switch finished", StringComparison.Ordinal));
    }

    private sealed class Midway
    {
        public long Moved { get; set; }

        public int Rows { get; set; }

        public List<(string Path, long Vcn, long Lcn, long N)> Done { get; set; } = [];
    }

    // 실행 도중 존이 막혀 멈춘 상태: 첫 적용이 일부만 옮기고 막힘으로 멈추고, 다음 계획은 새 존에서 처음부터 만들어짐 (가짜 볼륨의 Move 기록과 저널은 진짜).
    private Midway ZoneBlockedMidway()
    {
        _w.MakePlanAt = (_, _, _, _, _) => throw new PyValueException("zone overlaps hot data in a cycle; choose another zone"); // 이전 존 자리에서는 계획할 수 없어 새 존을 찾음
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        var midway = new Midway();
        _w.ArchiveJournal = _ => // 서비스 환경의 저널 보관과 같게
        {
            if (!File.Exists(JournalPath))
            {
                return null;
            }

            string dest = PlanSwitch.ArchiveName(JournalPath, _w.Now.UtcDateTime);
            File.Move(JournalPath, dest);
            return dest;
        };
        int executes = 0;
        _w.Apply = (g, plan, options, ct) =>
        {
            if (!options.Execute || executes++ > 0)
            {
                return real(g, plan, options, ct);
            }

            ApplyReport rep = real(g, plan, options with { ChunkBytes = 8 * Cb, WriteCap = 32 * Cb }, ct);
            midway.Moved = Vol.Moves.Sum(m => m.N);
            midway.Rows = JournalRowCount();
            midway.Done = [.. Vol.Moves];
            Vol.Ext[Path.Combine(Root, "other.bin")] = [new(0, midway.Done.Max(m => m.Lcn + m.N), 16)]; // 막힌 까닭: 존의 다음 구간을 다른 파일이 점유
            _w.MakePlan = (_, _, _) => Make().Resolve(30000); // 막혀서 다음 계획은 다른 존에서 처음부터 (이미 옮긴 부분도 새 존으로 가는 계획)
            rep.Stopped = Applier.ZoneOccupied;
            return rep;
        };
        return midway;
    }

    private void AssertOnlyTheRemainderMovedAndTheEarlierJournalIsKept(Midway midway)
    {
        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.InRange(midway.Moved, 1, 79);
        Assert.Equal(80, Vol.Moves.Sum(m => m.N)); // 처음 옮긴 양 + 실제 잔여량 = 전체, 다시 옮긴 것 없음
        Assert.DoesNotContain(Vol.Moves.Skip(midway.Done.Count), m => midway.Done.Any(d => d.Path == m.Path && m.Vcn < d.Vcn + d.N && d.Vcn < m.Vcn + m.N)); // 이미 옮긴 범위로는 Move 0
        Assert.Equal(midway.Rows, new Journal(Assert.Single(JournalArchives())).Rows.Count); // 이전 저널은 보관 파일로 남음
        Assert.True(File.Exists(_game.Plan + ".stale"));
        Assert.Contains(_log.Lines, l => l.Contains("partially applied plan replanned", StringComparison.Ordinal));
        Assert.Equal(0, Remaining(PlanFile.Load(_game.Plan)));
    }

    [Fact]
    public void ZoneBlockedMidwayKeepsTheJournalSoTheNextReplanMovesOnlyTheRemainder()
    {
        Midway midway = ZoneBlockedMidway();

        Run(TimeSpan.FromMinutes(3));
        RunUntilDone();

        AssertOnlyTheRemainderMovedAndTheEarlierJournalIsKept(midway);
    }

    [Fact]
    public void ZoneBlockedMidwayThenAServiceRestartBeforeTheReplanStillMovesOnlyTheRemainder()
    {
        Midway midway = ZoneBlockedMidway();
        for (int i = 0; i < 40 && midway.Moved == 0; i++)
        {
            Tick();
            _w.Now += Step;
        }

        Assert.True(midway.Moved > 0, Why());
        Assert.False(File.Exists(_game.Plan)); // 계획은 치워졌고
        Assert.Equal(midway.Rows, JournalRowCount()); // 저널은 그대로 (보관은 다음 계획이 교체할 때)
        _relocator = null; // 서비스 재시작
        _w.Now += TimeSpan.FromMinutes(5);

        Run(TimeSpan.FromMinutes(3));
        RunUntilDone();

        AssertOnlyTheRemainderMovedAndTheEarlierJournalIsKept(midway);
    }

    [Fact]
    public void ReapplyArchiveKeepsBothJournalsWhenCalledTwiceInTheSameSecond()
    {
        string dir = Path.Combine(Root, "journals");
        Directory.CreateDirectory(dir);
        string journal = Path.Combine(dir, "bench.jsonl");
        File.WriteAllText(journal, "one");
        string first = ReapplyEnv.ArchiveJournal(dir, _game)!;
        File.WriteAllText(journal, "two");

        string second = ReapplyEnv.ArchiveJournal(dir, _game)!;

        Assert.NotEqual(first, second);
        Assert.Equal(["one", "two"], new[] { File.ReadAllText(first), File.ReadAllText(second) }.Order().ToArray());
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public void ApplyOptionsCarryTheRangesOtherGamesPlanToUseAndTheyAreReleasedWithTheirPlan()
    {
        GameSettings other = AddOtherGameWithAPlanAt(0, Letter); // 다른 게임이 [0, 80) 을 쓸 계획
        var seen = new List<IReadOnlyList<(long Lcn, long Clusters)>?>();
        Func<GameSettings, PlanFile, ApplyOptions, CancellationToken, ApplyReport> real = _w.Apply;
        _w.Apply = (g, plan, options, ct) =>
        {
            seen.Add(options.Reserved);
            return real(g, plan, options, ct);
        };

        Run(TimeSpan.FromMinutes(4));
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.All(seen, r => Assert.Contains((0L, 80L), r!));
        Assert.True(PlanFile.Load(_game.Plan).Zone.StartLcn >= 80); // 다른 게임의 빈 구간을 고르지 않음
        Assert.Equal([(PlanFile.Load(_game.Plan).Zone.StartLcn!.Value, 80L)], ZoneReservations.Collect(Path.Combine(Root, "plans"), Letter, other.Plan)); // 반대로 다른 게임 쪽에서는 이 게임의 존이 예약, 자기 계획은 예약이 아님

        File.Delete(other.Plan); // 다른 게임의 계획이 사라지면 예약도 풀림
        Assert.Empty(_w.Reserved(_game));
        (Make().Resolve(0) with { Volume = "Q:" }).Save(other.Plan); // 다른 볼륨의 계획은 예약하지 않음
        Assert.Empty(_w.Reserved(_game));
    }
}

public sealed class RecoveryAttemptStatusTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "aster_status_" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void AttemptsAreSavedReloadedAndClearedAndTheKeyIsOmittedWhenThereAreNone()
    {
        var log = new ListLog();
        var at = new DateTimeOffset(2026, 11, 5, 3, 0, 0, TimeSpan.Zero);
        var status = new AutoStatus(_path, log, () => at);
        status.Set("g", "auto", null, AutoStage.WaitingIdle, "x");
        Assert.DoesNotContain("replan_attempts", File.ReadAllText(_path), StringComparison.Ordinal);

        var attempt = new RecoveryAttempt("abc", 777, 1, at + TimeSpan.FromMinutes(30));
        status.SetRecovery("g", attempt);

        Assert.Equal(attempt, new AutoStatus(_path, log, () => at).Recovery("g"));
        status.ClearRecovery("g");
        Assert.Null(new AutoStatus(_path, log, () => at).Recovery("g"));
        Assert.DoesNotContain("replan_attempts", File.ReadAllText(_path), StringComparison.Ordinal);
    }

    [Fact]
    public void AnOlderFileWithoutAttemptsStillLoads()
    {
        File.WriteAllText(_path, "{\"updated\":\"2026-11-05T03:00:00.0000000+00:00\",\"relocations\":[],\"games\":{\"g\":{\"source\":\"auto\",\"stage\":\"done\",\"at\":\"2026-11-05T03:00:00.0000000+00:00\"}},\"events\":[]}");

        var status = new AutoStatus(_path, new ListLog());

        Assert.Equal(AutoStage.Done, status.Get("g")!.Stage);
        Assert.Null(status.Recovery("g"));
    }
}
