using System.Security.Cryptography;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

/// 저널 교체 전 검증 (Applier.VerifyPending): 표본 구간 복원, 원본 변경과 검증 오류의 구분, sha_mismatch 로 끝난 저널.
[Collection(Cli.CliTests.VolumeSeam)]
public sealed class PendingVerificationTests : ApplyTestBase
{
    private static readonly DateTime Now = new(2026, 11, 5, 3, 0, 0, DateTimeKind.Utc);

    private static ApplyOptions Exec() => new(Execute: true, ChunkBytes: 8 * Cb, WriteCap: 2L << 30, Verify: VerifyMode.All);

    private ApplyHooks Edge() => new()
    {
        Extents = p => Vol.Ext[p],
        Digest = (p, size, extra) => SampleHasher.Compute(p, size, extra, null),
        EdgeDigest = (p, size, extra) => SampleHasher.Compute(p, size, extra, null, null, 0),
        SkipReason = _ => null,
    };

    // 같은 크기로 내용만 바꾸고 수정 시각은 되돌림: 원본 변경이 아니라 이동이 내용을 망친 것처럼 보임.
    private static void CorruptKeepingStamp(string path)
    {
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes((int)new FileInfo(path).Length));
        File.SetLastWriteTimeUtc(path, stamp);
    }

    private static void Rewrite(string path)
    {
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes((int)new FileInfo(path).Length));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(1)); // 크기가 같아도 수정 시각이 다름
    }

    // 취소로 after 검증을 미룬 저널 (sha 행에 표본 구간과 지문이 남음).
    private (PlanFile A, string PlanPath) Unsettled()
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
        Assert.Equal("cancelled", Applier.Apply(a, Vol, [Root], JournalPath, Hooks(), Exec(), cts.Token).Stopped);
        Vol.AfterMove = null;
        string planPath = Path.Combine(Root, "g.plan.json");
        a.Save(planPath);
        return (a, planPath);
    }

    private string PendingPath()
    {
        var jr = new Journal(JournalPath);
        long idx = (long)Applier.PendingBefore(jr.Rows)[0]["idx"]!;
        return (string)jr.Rows.First(r => Equals(r["type"], "step") && Equals(r["idx"], idx))["path"]!;
    }

    // ---- 3: 표본 구간 복원

    // 한 파일에 작업 40 개 (각 33 클러스터 = 132 KiB 라 앞뒤 구간이 겹치지 않음) 인 계획과, 그 일부 (처음 32 개) 만 기록된 옛 형식 저널.
    private (PlanFile Plan, string File, List<(long, long)> Original) Forty(bool recordAll)
    {
        string file = Path.Combine(Root, "big.bin");
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(40 * 33 * (int)Cb));
        var tasks = Enumerable.Range(0, 40).Select(i => new PlanTask(file, i * 33, 33, 20000 + (i * 33))).ToList();
        PlanFile plan = Make().Resolve(20000) with { Tasks = tasks, Fingerprints = [PlanFile.FingerprintOf(file)] };
        long size = new FileInfo(file).Length;
        HashSet<(long, long)> keep = Applier.EdgeRanges(Applier.MovedRanges(plan)[file], size).ToHashSet();
        long lo = 12 * 33 * Cb;
        long hi = 13 * 33 * Cb;
        List<(long, long)> original = new[] { (lo, Applier.TaskSpan), (hi - Applier.TaskSpan, Applier.TaskSpan) }.Where(keep.Contains).ToList();
        Assert.Equal([(hi - Applier.TaskSpan, Applier.TaskSpan)], original); // 작업 12 의 원래 표본은 끝 단독

        using var jr = new Journal(JournalPath);
        jr.Add(new JObject { { "type", "header" }, { "plan_sha", plan.Sha() }, { "cluster_bytes", Cb }, { "digest_rule", Applier.EdgeRule } });
        for (int i = 0; i < (recordAll ? 40 : 32); i++)
        {
            jr.Add(new JObject { { "type", "step" }, { "idx", (long)i }, { "state", "planned" }, { "path", file }, { "vcn", i * 33L }, { "clusters", 33L }, { "target_lcn", 20000L + (i * 33) }, { "from", new List<object?>() } });
        }

        return (plan, file, original);
    }

    private void AddSha(string file, List<(long, long)> ranges, bool storeRanges)
    {
        using var jr = new Journal(JournalPath);
        string sha = Edge().EdgeDigest!(file, new FileInfo(file).Length, ranges);
        var row = new JObject { { "type", "sha" }, { "idx", 12L }, { "sha_before", sha } };
        if (storeRanges)
        {
            row["ranges"] = ranges.Select(r => (object?)new List<object?> { r.Item1, r.Item2 }).ToList();
        }

        jr.Add(row);
    }

    [Fact]
    public void PartialJournalOfAFortyTaskFileIsNotJudgedChangedForAnOldJournal()
    {
        (_, string file, List<(long, long)> original) = Forty(recordAll: false);
        AddSha(file, original, storeRanges: false);

        Assert.Equal(1, Applier.VerifyPending(JournalPath, Edge())); // 끝 단독 표본도 후보에 포함
    }

    [Fact]
    public void TheOriginalPlanRecomputesTheExactSampleEvenWhenOnlyPartOfTheTasksWereRecorded()
    {
        (PlanFile plan, string file, List<(long, long)> original) = Forty(recordAll: false);
        AddSha(file, original, storeRanges: false);
        CorruptKeepingStamp(file); // 내용이 바뀜 (지문은 그대로): 정확한 복원이면 검증 오류로 거절
        bool rejected = false;
        try
        {
            _ = Applier.VerifyPending(JournalPath, Edge(), [plan]);
        }
        catch (PlanRejectedException e)
        {
            rejected = e.Message.StartsWith("unverified journal entry changed", StringComparison.Ordinal);
        }

        Assert.True(rejected);
    }

    [Fact]
    public void AnOriginalPlanMakesAMatchingOldJournalVerify()
    {
        (PlanFile plan, string file, List<(long, long)> original) = Forty(recordAll: false);
        AddSha(file, original, storeRanges: false);

        Assert.Equal(1, Applier.VerifyPending(JournalPath, Edge(), [plan]));
    }

    [Fact]
    public void StoredRangesAreReadBackExactlyForANewJournal()
    {
        (_, string file, _) = Forty(recordAll: false);
        List<(long, long)> odd = [(1000, 5000), (200000, 777)]; // 어떤 후보 규칙으로도 나오지 않는 구간
        AddSha(file, odd, storeRanges: true);

        Assert.Equal(1, Applier.VerifyPending(JournalPath, Edge()));
    }

    [Fact]
    public void ExecuteRecordsTheSampleRangesAndTheFingerprintInTheShaRow()
    {
        _ = Unsettled();

        List<JObject> sha = new Journal(JournalPath).Rows.Where(r => Equals(r["type"], "sha")).ToList();

        Assert.NotEmpty(sha);
        Assert.All(sha, r =>
        {
            Assert.IsType<List<object?>>(r["ranges"]);
            Assert.IsType<List<object?>>(r["fp"]);
        });
        Assert.True(Applier.VerifyPending(JournalPath, Hooks()) > 0); // 기록된 구간 그대로 읽어 일치
    }

    // ---- 4: 원본 변경과 검증 오류

    [Fact]
    public void ASourceChangedFileIsHeldWithItsOldShaKeptAndTheOthersAreVerified()
    {
        _ = Unsettled();
        string changed = PendingPath();
        int pending = Applier.PendingBefore(new Journal(JournalPath).Rows).Count;
        Rewrite(changed);

        Applier.PendingVerification result = Applier.VerifyPendingDetailed(JournalPath, Hooks());

        Assert.Equal([changed], result.SourceChanged);
        List<JObject> rows = new Journal(JournalPath).Rows.ToList();
        Assert.All(rows.Where(r => Equals(r["type"], Applier.HeldType)), r => Assert.Equal(Applier.SourceChangedReason, r["reason"]));
        Assert.Equal(pending, rows.Count(r => Equals(r["type"], "sha_verified")) + rows.Count(r => Equals(r["type"], Applier.HeldType)));
        Assert.Equal(pending, rows.Count(r => Equals(r["type"], "sha"))); // 옛 sha_before 는 지워지지 않음
        Assert.Empty(Applier.PendingBefore(rows));
        Assert.Equal(0, Applier.VerifyPending(JournalPath, Hooks())); // 두 번째는 할 일이 없고 다시 거절하지도 않음
    }

    [Fact]
    public void ASourceChangedFileDoesNotRejectThePlanSwitchAndTheJournalIsArchived()
    {
        (_, string planPath) = Unsettled();
        Rewrite(PendingPath());
        PlanFile b = Make().Resolve(30000);

        SwitchResult r = PlanSwitch.Commit(b, planPath, JournalPath, () => Now, hooks: Hooks());

        Assert.NotNull(r.SourceChanged);
        Assert.Single(r.SourceChanged!);
        Assert.False(File.Exists(JournalPath));
        Assert.True(File.Exists(r.ArchivedJournal));
        Assert.Equal(b.Sha(), PlanFile.Load(planPath).Sha());
    }

    [Fact]
    public void ADifferentContentWithTheSameFingerprintIsAVerificationErrorAndIsRejected()
    {
        (PlanFile a, string planPath) = Unsettled();
        CorruptKeepingStamp(PendingPath());
        byte[] journal = File.ReadAllBytes(JournalPath);

        PlanRejectedException e = Assert.Throws<PlanRejectedException>(() => PlanSwitch.Commit(Make().Resolve(30000), planPath, JournalPath, () => Now, hooks: Hooks()));

        Assert.StartsWith("unverified journal entry changed", e.Message, StringComparison.Ordinal);
        Assert.Equal(journal, File.ReadAllBytes(JournalPath));
        Assert.Equal(a.Sha(), PlanFile.Load(planPath).Sha());
    }

    [Fact]
    public void AnOldJournalWithoutAFingerprintUsesTheOriginalPlansFingerprints()
    {
        (PlanFile plan, string file, List<(long, long)> original) = Forty(recordAll: false);
        AddSha(file, original, storeRanges: false);
        Rewrite(file);

        // 기준 지문이 없으면 (옛 계획을 모르면) 불일치는 검증 오류
        _ = Assert.Throws<PlanRejectedException>(() => Applier.VerifyPending(JournalPath, Edge()));

        // header 와 같은 옛 계획의 지문과 지금이 다르면 원본 변경
        Assert.Equal([file], Applier.VerifyPendingDetailed(JournalPath, Edge(), [plan]).SourceChanged);
    }

    [Fact]
    public void AMissingFileCountsAsASourceChange()
    {
        _ = Unsettled();
        string gone = PendingPath();
        File.Move(gone, gone + ".gone");

        Applier.PendingVerification result = Applier.VerifyPendingDetailed(JournalPath, Hooks());

        Assert.Contains(gone, result.SourceChanged);
        File.Move(gone + ".gone", gone);
    }

    [Fact]
    public void AnUnreadableFileIsRejectedAsUnreadable()
    {
        _ = Unsettled();
        var hooks = new ApplyHooks
        {
            Extents = p => Vol.Ext[p],
            Digest = (p, size, extra) => SampleHasher.Compute(p, size, extra, null),
            SkipReason = _ => null,
            FileSize = _ => throw new IOException("locked"),
        };

        PlanRejectedException e = Assert.Throws<PlanRejectedException>(() => Applier.VerifyPending(JournalPath, hooks));

        Assert.Contains("unreadable", e.Message, StringComparison.Ordinal);
    }

    // ---- 5: sha_mismatch 로 끝난 저널은 검증 완료가 아님

    private PlanFile Mismatched()
    {
        PlanFile a = Make().Resolve(20000);
        Vol.AfterMove = n =>
        {
            if (n == 1)
            {
                CorruptKeepingStamp(F1);
                CorruptKeepingStamp(F2);
            }
        };
        _ = Assert.Throws<ShaMismatchException>(() => Applier.Apply(a, Vol, [Root], JournalPath, Hooks(), Exec()));
        Vol.AfterMove = null;
        return a;
    }

    [Fact]
    public void AnEndRowWithShaMismatchIsNotAVerificationBoundary()
    {
        _ = Mismatched();
        List<JObject> rows = new Journal(JournalPath).Rows.ToList();

        Assert.Contains(rows, r => Equals(r["type"], "end") && r["sha_mismatch"] is List<object?> { Count: > 0 });
        Assert.NotEmpty(Applier.PendingBefore(rows));
        PlanRejectedException e = Assert.Throws<PlanRejectedException>(() => Applier.VerifyPending(JournalPath, Hooks()));
        Assert.StartsWith("unverified journal entry changed", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePlanSwitchIsRefusedAndTheJournalKeptAfterAShaMismatch()
    {
        PlanFile a = Mismatched();
        string planPath = Path.Combine(Root, "g.plan.json");
        a.Save(planPath);
        byte[] journal = File.ReadAllBytes(JournalPath);

        _ = Assert.Throws<PlanRejectedException>(() => PlanSwitch.Commit(Make().Resolve(30000), planPath, JournalPath, () => Now, hooks: Hooks()));

        Assert.Equal(journal, File.ReadAllBytes(JournalPath));
        Assert.Equal(a.Sha(), PlanFile.Load(planPath).Sha());
        Assert.False(File.Exists(planPath + PlanSwitch.RecordSuffix));
    }

    [Fact]
    public void ARerunAfterAShaMismatchChecksTheUnverifiedEntriesAgainInsteadOfConsumingThem()
    {
        PlanFile a = Mismatched();

        _ = Assert.Throws<ShaMismatchException>(() => Applier.Apply(a, Vol, [Root], JournalPath, Hooks(), Exec()));

        Assert.NotEmpty(Applier.PendingBefore(new Journal(JournalPath).Rows));
    }

    [Fact]
    public void StatusDoesNotCallAMismatchedJournalEnded()
    {
        _ = Mismatched();

        (int code, string text, _) = Cli.CliTests.Run("status", "--journal", JournalPath);

        Assert.Equal(0, code);
        var journal = (JObject)((JObject)JsonRead.Parse(text)!)["journal"]!;
        Assert.Equal(false, journal["ended"]);
        Assert.True((long)journal["unverified"]! > 0);
    }
}
