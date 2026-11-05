using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

/// 활성 계획 교체의 내구 절차 (PlanSwitch): 단계별 중단 뒤 재개, 보관 이름 충돌, 보관 실패, 미검증 SHA 검증.
public sealed class PlanSwitchTests : ApplyTestBase
{
    private static readonly DateTime Now = new(2026, 11, 5, 3, 0, 0, DateTimeKind.Utc);

    private static ApplyOptions Exec(long writeCap = 2L << 30) => new(Execute: true, ChunkBytes: 8 * Cb, WriteCap: writeCap);

    private static Action<string> CrashAt(string point) => p =>
    {
        if (p == point)
        {
            throw new InvalidOperationException("crash at " + p);
        }
    };

    // 계획 A 를 쓰기 상한으로 일부만 적용한 상태 (저널은 A 의 것, 검증 끝남). B 는 다른 존의 계획.
    private (PlanFile A, PlanFile B, string PlanPath, int Rows) Partial()
    {
        PlanFile a = Make().Resolve(20000);
        Assert.Equal("write_cap", Apply(a, Exec(64 * Cb)).Stopped);
        string planPath = Path.Combine(Root, "g.plan.json");
        a.Save(planPath);
        return (a, Make().Resolve(30000), planPath, new Journal(JournalPath).Rows.Count);
    }

    // 보관된 저널: j.jsonl.<시각>[.n] ("j.jsonl.*" 패턴은 j.jsonl 자신도 잡으므로 이름으로 가름)
    private string[] Archives() => Directory.GetFiles(Root).Where(f => Path.GetFileName(f).StartsWith("j.jsonl.", StringComparison.Ordinal)).Order().ToArray();

    private void AssertSwitched(PlanFile b, string planPath, int journalRows)
    {
        Assert.Equal(b.Sha(), PlanFile.Load(planPath).Sha());
        Assert.False(File.Exists(planPath + PlanSwitch.NextSuffix));
        Assert.False(File.Exists(planPath + PlanSwitch.RecordSuffix));
        Assert.Empty(Directory.GetFiles(Root, "*.tmp"));
        Assert.False(File.Exists(JournalPath));
        string archive = Assert.Single(Archives());
        Assert.Equal(journalRows, new Journal(archive).Rows.Count); // 저널은 지워지지 않고 그대로 보관
    }

    [Fact]
    public void CommitArchivesTheJournalAndPromotesTheCandidate()
    {
        (_, PlanFile b, string planPath, int rows) = Partial();

        SwitchResult r = PlanSwitch.Commit(b, planPath, JournalPath, () => Now, hooks: Hooks());

        Assert.False(r.Resumed);
        Assert.Equal(b.Sha(), r.NewPlanSha);
        Assert.Equal(JournalPath + ".20261105T030000Z", r.ArchivedJournal);
        AssertSwitched(b, planPath, rows);
    }

    [Theory]
    [InlineData("verified")]
    [InlineData("prepared")]
    [InlineData("recorded")]
    [InlineData("archived")]
    [InlineData("promoted")]
    [InlineData("next_removed")]
    public void InterruptionAtAnyStepConvergesToTheSameState(string point)
    {
        (PlanFile a, PlanFile b, string planPath, int rows) = Partial();

        Assert.Throws<InvalidOperationException>(() => PlanSwitch.Commit(b, planPath, JournalPath, () => Now, hooks: Hooks(), checkpoint: CrashAt(point)));
        SwitchResult? resumed = PlanSwitch.Resume(planPath, JournalPath);

        if (point is "verified" or "prepared")
        {
            Assert.Null(resumed); // 기록 전에 멈춤: 활성 계획과 저널은 그대로
            Assert.Equal(a.Sha(), PlanFile.Load(planPath).Sha());
            Assert.Equal(rows, new Journal(JournalPath).Rows.Count);
            Assert.False(PlanSwitch.Commit(b, planPath, JournalPath, () => Now, hooks: Hooks()).Resumed);
        }
        else
        {
            Assert.True(resumed!.Resumed);
            Assert.Equal(b.Sha(), resumed.NewPlanSha);
        }

        AssertSwitched(b, planPath, rows);
        Assert.Null(PlanSwitch.Resume(planPath, JournalPath)); // 끝난 뒤에는 할 일이 없음
    }

    [Fact]
    public void ResumeItselfCanBeInterruptedRepeatedly()
    {
        (_, PlanFile b, string planPath, int rows) = Partial();
        Assert.Throws<InvalidOperationException>(() => PlanSwitch.Commit(b, planPath, JournalPath, () => Now, hooks: Hooks(), checkpoint: CrashAt("recorded")));

        Assert.Throws<InvalidOperationException>(() => PlanSwitch.Resume(planPath, JournalPath, checkpoint: CrashAt("archived")));
        Assert.Throws<InvalidOperationException>(() => PlanSwitch.Resume(planPath, JournalPath, checkpoint: CrashAt("promoted")));

        Assert.True(PlanSwitch.Resume(planPath, JournalPath)!.Resumed);
        AssertSwitched(b, planPath, rows);
    }

    [Fact]
    public void CommitFinishesAnInterruptedSwitchBeforeStartingTheNextOne()
    {
        (_, PlanFile b, string planPath, int rows) = Partial();
        Assert.Throws<InvalidOperationException>(() => PlanSwitch.Commit(b, planPath, JournalPath, () => Now, hooks: Hooks(), checkpoint: CrashAt("recorded")));
        PlanFile c = Make().Resolve(40000);

        PlanSwitch.Commit(c, planPath, JournalPath, () => Now.AddMinutes(1), hooks: Hooks());

        Assert.Equal(c.Sha(), PlanFile.Load(planPath).Sha());
        Assert.Equal(rows, new Journal(Assert.Single(Archives())).Rows.Count);
    }

    [Fact]
    public void ArchiveNamesAreUniqueWithinTheSameSecond()
    {
        string first = PlanSwitch.ArchiveName(JournalPath, Now);
        File.WriteAllText(first, "x");
        string second = PlanSwitch.ArchiveName(JournalPath, Now);
        File.WriteAllText(second, "x");

        Assert.Equal(JournalPath + ".20261105T030000Z", first);
        Assert.Equal(first + ".1", second);
        Assert.Equal(first + ".2", PlanSwitch.ArchiveName(JournalPath, Now));
    }

    [Fact]
    public void TwoSwitchesInTheSameSecondKeepBothJournals()
    {
        (_, PlanFile b, string planPath, int rows) = Partial();
        PlanSwitch.Commit(b, planPath, JournalPath, () => Now, hooks: Hooks());
        Assert.Equal("write_cap", Apply(b, Exec(64 * Cb)).Stopped); // B 의 새 저널
        int rowsB = new Journal(JournalPath).Rows.Count;

        PlanSwitch.Commit(Make().Resolve(40000), planPath, JournalPath, () => Now, hooks: Hooks());

        string[] archives = Archives();
        Assert.Equal(2, archives.Length);
        Assert.EndsWith(".1", archives[1]);
        Assert.Equal([rows, rowsB], archives.Select(f => new Journal(f).Rows.Count).ToArray());
    }

    [Fact]
    public void ArchiveFailureKeepsThePlanAndJournalAndARetryCompletes()
    {
        (PlanFile a, PlanFile b, string planPath, int rows) = Partial();
        byte[] journalBytes = File.ReadAllBytes(JournalPath);

        using (new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.Read)) // 열려 있어 옮길 수 없음
        {
            Assert.ThrowsAny<IOException>(() => PlanSwitch.Commit(b, planPath, JournalPath, () => Now, hooks: Hooks()));
            Assert.Equal(a.Sha(), PlanFile.Load(planPath).Sha()); // 새 계획은 승격되지 않음
            Assert.Equal(journalBytes, File.ReadAllBytes(JournalPath));
            Assert.ThrowsAny<IOException>(() => PlanSwitch.Resume(planPath, JournalPath));
            Assert.Equal(a.Sha(), PlanFile.Load(planPath).Sha());
        }

        Assert.True(PlanSwitch.Resume(planPath, JournalPath)!.Resumed);
        AssertSwitched(b, planPath, rows);
    }

    [Fact]
    public void ACandidateWithTheJournalsOwnShaKeepsTheJournal()
    {
        (PlanFile a, _, string planPath, int rows) = Partial();

        SwitchResult r = PlanSwitch.Commit(a, planPath, JournalPath, () => Now);

        Assert.Null(r.ArchivedJournal);
        Assert.Equal(rows, new Journal(JournalPath).Rows.Count);
        Assert.Empty(Archives());
    }

    [Fact]
    public void WithoutAJournalTheCandidateIsJustPromoted()
    {
        PlanFile a = Make().Resolve(20000);
        string planPath = Path.Combine(Root, "g.plan.json");
        a.Save(planPath);

        SwitchResult r = PlanSwitch.Commit(Make().Resolve(30000), planPath, JournalPath, () => Now);

        Assert.Null(r.ArchivedJournal);
        Assert.Equal(30000, PlanFile.Load(planPath).Zone.StartLcn);
    }

    [Fact]
    public void SavePlanCallbackWritesThePromotedPlan()
    {
        (_, PlanFile b, string planPath, _) = Partial();
        var written = new List<string>();

        PlanSwitch.Commit(b, planPath, JournalPath, () => Now, (p, path) =>
        {
            written.Add(path);
            p.Save(path);
        }, Hooks());

        Assert.Equal([planPath], written);
    }

    // ---- 미검증 sha_before: 취소로 after 검증을 미룬 저널

    private (PlanFile A, PlanFile B, string PlanPath) Unsettled()
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
        Assert.Equal("cancelled", Applier.Apply(a, Vol, [Root], JournalPath, Hooks(), Exec() with { Verify = VerifyMode.All }, cts.Token).Stopped);
        Vol.AfterMove = null;
        string planPath = Path.Combine(Root, "g.plan.json");
        a.Save(planPath);
        return (a, Make().Resolve(30000), planPath);
    }

    [Fact]
    public void UnsettledShaThatStillMatchesIsVerifiedBeforeArchiving()
    {
        (_, PlanFile b, string planPath) = Unsettled();
        int pending = new Journal(JournalPath).Rows.Count(r => Equals(r["type"], "sha"));
        Assert.Equal(0, new Journal(JournalPath).Rows.Count(r => Equals(r["type"], "sha_verified")));

        PlanSwitch.Commit(b, planPath, JournalPath, () => Now, hooks: Hooks());

        List<JObject> archived = new Journal(Assert.Single(Archives())).Rows.ToList();
        Assert.True(pending > 0);
        Assert.Equal(pending, archived.Count(r => Equals(r["type"], "sha_verified")));
        Assert.Equal(b.Sha(), PlanFile.Load(planPath).Sha());
    }

    [Fact]
    public void VerifyPendingWritesVerifiedRowsOnlyOnce()
    {
        Unsettled();

        int first = Applier.VerifyPending(JournalPath, Hooks());

        Assert.True(first > 0);
        Assert.Equal(0, Applier.VerifyPending(JournalPath, Hooks()));
    }

    [Fact]
    public void UnsettledShaThatNoLongerMatchesRejectsTheSwitchAndChangesNothing()
    {
        (PlanFile a, PlanFile b, string planPath) = Unsettled();
        byte[] journalBytes = File.ReadAllBytes(JournalPath);
        foreach (string f in new[] { F1, F2 })
        {
            File.WriteAllBytes(f, System.Security.Cryptography.RandomNumberGenerator.GetBytes(1 << 20));
        }

        _ = Assert.Throws<PlanRejectedException>(() => PlanSwitch.Commit(b, planPath, JournalPath, () => Now, hooks: Hooks()));

        Assert.Equal(journalBytes, File.ReadAllBytes(JournalPath));
        Assert.Equal(a.Sha(), PlanFile.Load(planPath).Sha());
        Assert.Empty(Archives());
        Assert.False(File.Exists(planPath + PlanSwitch.RecordSuffix));
        Assert.False(File.Exists(planPath + PlanSwitch.NextSuffix));
    }

    [Fact]
    public void UnsettledShaOnAnUnreadableFileRejectsTheSwitch()
    {
        (_, PlanFile b, string planPath) = Unsettled();
        var hooks = new ApplyHooks
        {
            Extents = p => Vol.Ext[p],
            Digest = (p, size, extra) => SampleHasher.Compute(p, size, extra, null),
            SkipReason = _ => null,
            FileSize = _ => throw new IOException("locked"),
        };

        _ = Assert.Throws<PlanRejectedException>(() => PlanSwitch.Commit(b, planPath, JournalPath, () => Now, hooks: hooks));

        Assert.True(File.Exists(JournalPath));
        Assert.Empty(Archives());
    }

    [Fact]
    public void ArchivingAJournalNeedsHooksToVerifyIt()
    {
        (_, PlanFile b, string planPath, _) = Partial();

        _ = Assert.Throws<ArgumentException>(() => PlanSwitch.Commit(b, planPath, JournalPath, () => Now));

        Assert.True(File.Exists(JournalPath));
    }
}
