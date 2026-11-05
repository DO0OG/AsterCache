using AsterCache.Core;
using AsterCache.Core.Apply;
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
