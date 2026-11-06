using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

/// 잔여 재계획 연결 도우미 (ResidualReplan.Run): 이전 목표 수집 → 잔여 계획 → 저널 보관과 승격.
public sealed class ResidualReplanTests : ApplyTestBase
{
    private static readonly DateTime Now = new(2026, 11, 5, 3, 0, 0, DateTimeKind.Utc);

    private static ApplyOptions Exec(long writeCap) => new(Execute: true, ChunkBytes: 8 * Cb, WriteCap: writeCap);

    private string PlanPath => Path.Combine(Root, "g.plan.json");

    private string[] Archives() => Directory.GetFiles(Root).Where(f => Path.GetFileName(f).StartsWith("j.jsonl.", StringComparison.Ordinal)).Order().ToArray();

    // 계획 A (존 20000) 를 일부만 적용한 상태: 저널은 A 의 것, 활성 계획도 A.
    private PlanFile PartialA()
    {
        PlanFile a = Make().Resolve(20000);
        Assert.Equal("write_cap", Apply(a, Exec(64 * Cb)).Stopped);
        a.Save(PlanPath);
        return a;
    }

    [Fact]
    public void MovedPartsAreKeptAndOnlyTheRestMovesAfterAJournalSwitch()
    {
        PlanFile a = PartialA();
        long movedClusters = Vol.Moves.Sum(m => m.N);
        Assert.True(movedClusters > 0 && movedClusters < 80);
        Vol.Moves.Clear();
        int rows = new Journal(JournalPath).Rows.Count;

        (PlanFile plan, SwitchResult sw) = ResidualReplan.Run(Make(), PlanPath, JournalPath, Vol, null, Hooks(), () => Now, null, []);

        Assert.Equal(plan.Sha(), PlanFile.Load(PlanPath).Sha());
        Assert.NotEqual(a.Sha(), plan.Sha());
        Assert.Equal(JournalPath + ".20261105T030000Z", sw.ArchivedJournal);
        Assert.Equal(rows, new Journal(sw.ArchivedJournal!).Rows.Count);
        Assert.False(File.Exists(JournalPath));
        Assert.Equal(movedClusters, (long)plan.Stats["fixed_clusters"]!);
        ApplyReport rep = Apply(plan, Exec(2L << 30));
        Assert.Equal((80 - movedClusters) * Cb, rep.MovedBytes);
        Assert.Equal(80 - movedClusters, Vol.Moves.Sum(m => m.N));
        Assert.Equal(0, LayoutVerifier.Verify(plan, p => Vol.Ext[p]).MissingFiles);
    }

    [Fact]
    public void AnInterruptedSwitchIsFinishedFirstAndReservedRangesAreAvoided()
    {
        PlanFile a = PartialA();
        PlanFile b = Make().Resolve(30000);
        Assert.Throws<InvalidOperationException>(() => PlanSwitch.Commit(b, PlanPath, JournalPath, () => Now, hooks: Hooks(), checkpoint: p =>
        {
            if (p == "recorded")
            {
                throw new InvalidOperationException("crash");
            }
        }));

        (PlanFile plan, SwitchResult sw) = ResidualReplan.Run(Make(), PlanPath, JournalPath, Vol, [(0, 21000)], Hooks(), () => Now.AddMinutes(1), null, [a]);

        Assert.False(File.Exists(PlanPath + PlanSwitch.RecordSuffix));
        Assert.False(File.Exists(PlanPath + PlanSwitch.NextSuffix));
        Assert.Single(Archives()); // 중단된 교체의 보관만 있음: 새 저널은 아직 없음
        Assert.Null(sw.ArchivedJournal);
        Assert.True(plan.Zone.StartLcn >= 21000);
        Assert.Equal(plan.Sha(), PlanFile.Load(PlanPath).Sha());
    }

    // 부분 적용 계획 A (존 20000) 를 잔여 재계획한 결과. 이미 옮긴 조각은 20000 부터 이어지므로 그 끝은 20000 + 고정 클러스터.
    private (PlanFile Plan, long End) ReplanAfterPartialA(long? reservedOffset = null, long? blockOffset = null)
    {
        _ = PartialA();
        long end = 20000 + Vol.Moves.Sum(m => m.N);
        if (blockOffset is { } at)
        {
            Vol.Ext["other.bin"] = [new Extent(0, end + at, 4)];
        }

        (long, long)[]? reserved = reservedOffset is { } r ? [(end + r, 5)] : null;
        PlanFile plan = ResidualReplan.Run(Make(), PlanPath, JournalPath, Vol, reserved, Hooks(), () => Now, null, []).Plan;
        return (plan, end);
    }

    [Fact]
    public void TheRestIsPlacedRightAfterTheMovedPartWhenTheSpaceThereIsFree()
    {
        (PlanFile plan, long end) = ReplanAfterPartialA();

        Assert.Equal(end, plan.Zone.StartLcn); // 되돌리면 낮은 LCN 의 빈 구간 (0) 으로 감
        Assert.Equal(1L, plan.Stats[ResidualPlanner.AdjacentKey]);
        _ = Apply(plan, Exec(2L << 30));
        Assert.Equal(0, LayoutVerifier.Verify(plan, p => Vol.Ext[p]).MissingFiles);
    }

    [Fact]
    public void ASpaceOccupiedInTheMiddleByAnotherFileFallsBackToTheGeneralSearch()
    {
        (PlanFile plan, long end) = ReplanAfterPartialA(blockOffset: 10);

        Assert.NotEqual(end, plan.Zone.StartLcn);
        Assert.True(plan.Zone.StartLcn < 1000);
        Assert.Equal(0L, plan.Stats[ResidualPlanner.AdjacentKey]);
    }

    [Fact]
    public void ASpaceOverlappingAnotherPlansReservationFallsBackToTheGeneralSearch()
    {
        (PlanFile plan, long end) = ReplanAfterPartialA(reservedOffset: 10);

        Assert.NotEqual(end, plan.Zone.StartLcn);
        Assert.Equal(0L, plan.Stats[ResidualPlanner.AdjacentKey]);
    }

    // 부분 이동 뒤 원본 내용·mtime 이 바뀐 상태에서, 인접 공간이 비어 같은 task 의 계획이 다시 나와도 세대 표지로 SHA 가 달라 옛 저널은 보관되고 새 Apply 가 sha_mismatch 없이 끝나야 함.
    // moveActiveAside: 활성 계획을 .stale 로 옮기고 oldPlans 로 넘김.
    private void AssertSamePlanWithChangedSourcesArchivesTheJournal(bool moveActiveAside)
    {
        PlanFile a = PartialA();
        string journalBefore = File.ReadAllText(JournalPath);
        foreach (string f in new[] { F1, F2 })
        {
            File.WriteAllBytes(f, System.Security.Cryptography.RandomNumberGenerator.GetBytes((int)new FileInfo(f).Length)); // 같은 크기의 다른 내용 (게임 업데이트)
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(1));
        }

        PlanFile[] olds = [];
        if (moveActiveAside)
        {
            File.Move(PlanPath, PlanPath + ".stale");
            olds = [PlanFile.Load(PlanPath + ".stale")];
        }

        (PlanFile plan, SwitchResult sw) = ResidualReplan.Run(Make(), PlanPath, JournalPath, Vol, null, Hooks(), () => Now, null, olds);

        Assert.Equal(a.Tasks, plan.Tasks); // 인접 공간이 비어 같은 위치로 재계획
        Assert.NotEqual(a.Sha(), plan.Sha()); // 그래도 SHA 는 달라 저널 교체 (검증 후 보관) 를 거침
        Assert.Equal(1L, plan.Stats[ResidualPlanner.AdjacentKey]);
        Assert.NotNull(sw.ArchivedJournal);
        Assert.Equal(journalBefore, File.ReadAllText(sw.ArchivedJournal!));
        Assert.False(File.Exists(JournalPath));
        ApplyReport rep = Apply(plan, Exec(2L << 30) with { Verify = VerifyMode.All });
        Assert.Empty(rep.Failed);
        Assert.Null(rep.Stopped);
    }

    [Fact]
    public void ASamePlaceReplanStillArchivesTheJournalSoOldShaSamplesAreNotReused() =>
        AssertSamePlanWithChangedSourcesArchivesTheJournal(moveActiveAside: false);

    [Fact]
    public void ASamePlaceReplanStillArchivesTheJournalWhenTheActivePlanWasSetAside() =>
        AssertSamePlanWithChangedSourcesArchivesTheJournal(moveActiveAside: true);

    [Fact]
    public void TheResidualGenerationIsThePreviousOnePlusOneAndDeterministic()
    {
        PlanFile a = PartialA();
        Assert.False(a.Stats.ContainsKey(ResidualPlanner.GenerationKey));

        (PlanFile first, _) = ResidualReplan.Run(Make(), PlanPath, JournalPath, Vol, null, Hooks(), () => Now, null, []);
        Assert.Equal(1L, first.Stats[ResidualPlanner.GenerationKey]);
        Assert.Equal(1L, PlanFile.Load(PlanPath).Stats[ResidualPlanner.GenerationKey]);

        (PlanFile second, _) = ResidualReplan.Run(Make(), PlanPath, JournalPath, Vol, null, Hooks(), () => Now.AddMinutes(1), null, []);
        Assert.Equal(2L, second.Stats[ResidualPlanner.GenerationKey]);
        Assert.NotEqual(first.Sha(), second.Sha());
        Assert.Equal(first.Tasks, second.Tasks);
    }

    [Fact]
    public void WithNoPreviousPlanToReadTheGenerationIsRaisedUntilTheShaDiffersFromTheJournalPlan()
    {
        (PlanFile first, _) = ResidualReplan.Run(Make(), PlanPath, JournalPath, Vol, null, Hooks(), () => Now, null, []); // 옮긴 것이 없는 상태의 세대 1 계획
        Assert.Equal(1L, first.Stats[ResidualPlanner.GenerationKey]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        _ = Applier.Apply(first, Vol, [Root], JournalPath, Hooks(), Exec(2L << 30), cancelled.Token); // 아무것도 옮기기 전에 멈춘 세대 1 계획의 저널
        Assert.True(File.Exists(JournalPath));
        File.Delete(PlanPath); // 활성 계획도 oldPlans 도 없어 세대를 알 수 없음
        string journalSha = (string)new Journal(JournalPath).Rows.First(r => Equals(r["type"], "header"))["plan_sha"]!;
        Assert.Equal(first.Sha(), journalSha);

        (PlanFile plan, SwitchResult sw) = ResidualReplan.Run(Make(), PlanPath, JournalPath, Vol, null, Hooks(), () => Now.AddMinutes(1), null, []);

        Assert.Equal(first.Tasks, plan.Tasks);
        Assert.NotEqual(journalSha, plan.Sha());
        Assert.Equal(2L, plan.Stats[ResidualPlanner.GenerationKey]);
        Assert.NotNull(sw.ArchivedJournal);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void AnActivePlanThatExistsButCannotBeReadIsRetriedNotIgnored()
    {
        _ = PartialA();
        byte[] journal = File.ReadAllBytes(JournalPath);

        using (new FileStream(PlanPath, FileMode.Open, FileAccess.Read, FileShare.None)) // 다른 프로세스가 잠금
        {
            _ = Assert.ThrowsAny<IOException>(() => ResidualReplan.Run(Make(), PlanPath, JournalPath, Vol, null, Hooks(), () => Now, null, []));
        }

        Assert.Equal(journal, File.ReadAllBytes(JournalPath));
        Assert.Empty(Archives());
    }

    [Fact]
    public void AnUnreadableJournalStopsTheReplanWithoutChangingAnything()
    {
        PlanFile a = PartialA();
        File.AppendAllText(JournalPath, "{broken\n{\"type\":\"step\"}\n");
        byte[] bytes = File.ReadAllBytes(JournalPath);

        Assert.ThrowsAny<Exception>(() => ResidualReplan.Run(Make(), PlanPath, JournalPath, Vol, null, Hooks(), () => Now, null, []));

        Assert.Equal(a.Sha(), PlanFile.Load(PlanPath).Sha());
        Assert.Equal(bytes, File.ReadAllBytes(JournalPath));
        Assert.Empty(Archives());
    }

    [Fact]
    public void ASourceChangedFileIsHeldAndTheRestIsStillRecovered()
    {
        PlanFile a = Make().Resolve(20000);
        using var cts = new CancellationTokenSource();
        Vol.AfterMove = n =>
        {
            if (n == 3)
            {
                cts.Cancel();
            }
        };
        Assert.Equal("cancelled", Applier.Apply(a, Vol, [Root], JournalPath, Hooks(), Exec(2L << 30) with { Verify = VerifyMode.All }, cts.Token).Stopped);
        Vol.AfterMove = null;
        a.Save(PlanPath);
        var jr = new Journal(JournalPath);
        long idx = (long)Applier.PendingBefore(jr.Rows)[0]["idx"]!;
        string held = (string)jr.Rows.First(r => Equals(r["type"], "step") && Equals(r["idx"], idx))["path"]!;
        string other = held == F1 ? F2 : F1;
        File.WriteAllBytes(held, System.Security.Cryptography.RandomNumberGenerator.GetBytes((int)new FileInfo(held).Length));
        File.SetLastWriteTimeUtc(held, DateTime.UtcNow.AddDays(1)); // 게임 업데이트가 덮어씀
        Vol.Moves.Clear();

        (PlanFile plan, SwitchResult sw) = ResidualReplan.Run(Make(), PlanPath, JournalPath, Vol, null, Hooks(), () => Now, null, []);

        Assert.DoesNotContain(plan.Order, o => o.Path == held);
        Assert.DoesNotContain(plan.Tasks, t => t.Path == held);
        Assert.Contains(plan.Order, o => o.Path == other);
        Assert.DoesNotContain(plan.Fingerprints, f => f.Path == held);
        var heldFiles = Assert.IsType<AsterCache.Core.Json.JObject>(plan.Stats["held_files"]);
        Assert.Equal("source_changed", heldFiles[held]);
        Assert.Equal([held], sw.SourceChanged);
        Assert.Equal(plan.Sha(), PlanFile.Load(PlanPath).Sha());
        List<AsterCache.Core.Json.JObject> archived = new Journal(sw.ArchivedJournal!).Rows.ToList();
        Assert.Contains(archived, r => Equals(r["type"], Applier.HeldType) && Equals(r["path"], held));
        Assert.Contains(archived, r => Equals(r["type"], "sha")); // 옛 sha_before 는 그대로
        _ = Apply(plan, Exec(2L << 30));
        Assert.DoesNotContain(Vol.Moves, m => m.Path == held); // 보류한 파일은 건드리지 않고 나머지만 복구
    }

    [Fact]
    public void AHeldFileStaysHeldWhenTheFirstReplanAttemptFailsAndIsRetried()
    {
        PlanFile a = Make().Resolve(20000);
        using var cts = new CancellationTokenSource();
        Vol.AfterMove = n =>
        {
            if (n == 3)
            {
                cts.Cancel();
            }
        };
        _ = Applier.Apply(a, Vol, [Root], JournalPath, Hooks(), Exec(2L << 30) with { Verify = VerifyMode.All }, cts.Token);
        Vol.AfterMove = null;
        a.Save(PlanPath);
        var jr = new Journal(JournalPath);
        long idx = (long)Applier.PendingBefore(jr.Rows)[0]["idx"]!;
        string held = (string)jr.Rows.First(r => Equals(r["type"], "step") && Equals(r["idx"], idx))["path"]!;
        File.WriteAllBytes(held, System.Security.Cryptography.RandomNumberGenerator.GetBytes((int)new FileInfo(held).Length));
        File.SetLastWriteTimeUtc(held, DateTime.UtcNow.AddDays(1));

        // 첫 시도는 빈 구간이 없어 거절 (보류 행은 이미 저널에 남음), 다시 시도해도 같은 파일이 보류로 빠지고 거절하지 않음
        _ = Assert.Throws<PlanRejectedException>(() => ResidualReplan.Run(Make(), PlanPath, JournalPath, Vol, [(0, 200000)], Hooks(), () => Now, null, []));
        Assert.True(File.Exists(JournalPath));
        (PlanFile plan, _) = ResidualReplan.Run(Make(), PlanPath, JournalPath, Vol, null, Hooks(), () => Now, null, []);

        Assert.DoesNotContain(plan.Order, o => o.Path == held);
        Assert.Equal("source_changed", ((AsterCache.Core.Json.JObject)plan.Stats["held_files"]!)[held]);
    }
}
