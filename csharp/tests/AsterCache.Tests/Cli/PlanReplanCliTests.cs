using AsterCache.Cli.Commands;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Tests.Core;
using Xunit;

namespace AsterCache.Tests.Cli;

/// VolumeCommands.Open 은 프로세스 전역이라, 이 대역을 바꾸는 시험과 실제 볼륨을 여는 CliTests 가 동시에 돌지 않게 묶습니다.
[CollectionDefinition(CliTests.VolumeSeam, DisableParallelization = true)]
public sealed class VolumeSeamCollection;

/// CLI 의 plan --journal / --from-plan (잔여 재계획), apply 의 저널 대조·예약 범위. 볼륨과 훅은 대역 (VolumeCommands.Open 을 바꿈).
[Collection(CliTests.VolumeSeam)]
public sealed class PlanReplanCliTests : ApplyTestBase
{
    private static ApplyOptions Exec(long writeCap) => new(Execute: true, ChunkBytes: 8 * Cb, WriteCap: writeCap);

    private string PlanPath => Path.Combine(Root, "g.plan.json");

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    private IDisposable UseVolume()
    {
        var previous = VolumeCommands.Open;
        VolumeCommands.Open = (_, _) => (Vol, Hooks());
        return new Restore(() => VolumeCommands.Open = previous);
    }

    // 계획 A (존 20000) 를 일부만 적용한 상태: 저널은 A 의 것, 활성 계획도 A. 옮긴 클러스터 수를 돌려줌 (이동 기록은 비움).
    private long PartialA()
    {
        PlanFile a = Make().Resolve(20000);
        Assert.Equal("write_cap", Apply(a, Exec(64 * Cb)).Stopped);
        a.Save(PlanPath);
        long moved = Vol.Moves.Sum(m => m.N);
        Assert.True(moved > 0 && moved < 80);
        Vol.Moves.Clear();
        return moved;
    }

    private string WriteTrace()
    {
        var rows = new List<object?>();
        foreach ((string path, long block) in new[] { (F1, 12L), (F2, 0L), (F1, 0L), (F1, 4L), (F2, 8L) })
        {
            rows.Add(new JObject { { "path", path }, { "offset", block * Blk }, { "length", Blk }, { "cost", 8.0 } });
        }

        string trace = Path.Combine(Root, "trace.json");
        File.WriteAllText(trace, PyJson.Dumps(rows));
        return trace;
    }

    private static JObject Json(string text) => (JObject)JsonRead.Parse(text)!;

    [Fact]
    public void FromPlanKeepsMovedRangesFixedAndOnlyTheRestMoves()
    {
        long moved = PartialA();
        byte[] oldJournal = File.ReadAllBytes(JournalPath);
        using IDisposable scope = UseVolume();

        (int code, string text, _) = CliTests.Run("plan", "--from-plan", PlanPath, "--journal", JournalPath);

        Assert.Equal(0, code);
        JObject json = Json(text);
        Assert.Equal(moved, json["fixed_clusters"]);
        string archived = Assert.IsType<string>(json["archived_journal"]);
        Assert.Equal(oldJournal, File.ReadAllBytes(archived));
        Assert.False(File.Exists(JournalPath));
        PlanFile plan = PlanFile.Load(PlanPath);
        Assert.Equal(PlanPath + PlanOutput.PrefetchSuffix, json["prefetch_trace"]); // 세션이 없어도 승격한 계획의 Order 로 선읽기 파일을 새로 씀
        Assert.Equal(moved * Cb, (long)plan.Stats["fixed_clusters"]! * Cb);
        Apply(plan, Exec(2L << 30));
        Assert.Equal(80 - moved, Vol.Moves.Sum(m => m.N)); // 이미 옮긴 범위는 다시 옮기지 않음
    }

    [Fact]
    public void FromPlanRewritesTheStalePrefetchFileFromThePromotedOrder()
    {
        _ = PartialA();
        File.WriteAllText(PlanPath + PlanOutput.PrefetchSuffix, "[]\n"); // 옛 계획의 선읽기
        using IDisposable scope = UseVolume();

        (int code, _, _) = CliTests.Run("plan", "--from-plan", PlanPath, "--journal", JournalPath);

        Assert.Equal(0, code);
        string expected = Path.Combine(Root, "expected.plan.json");
        _ = PlanOutput.Save(PlanFile.Load(PlanPath), [], expected);
        Assert.Equal(File.ReadAllBytes(expected + PlanOutput.PrefetchSuffix), File.ReadAllBytes(PlanPath + PlanOutput.PrefetchSuffix));
        Assert.NotEqual("[]", File.ReadAllText(PlanPath + PlanOutput.PrefetchSuffix).Trim());
    }

    [Fact]
    public void ApplyReportsBusyWhenTheServiceHoldsTheLockFromAnotherPlanFolder()
    {
        PlanFile plan = Make().Resolve(20000);
        plan.Save(PlanPath);
        using var service = new ForeignLock(Path.Combine(Root, "elsewhere", "x.plan.json"), JournalPath); // 서비스: 저널 폴더 기준
        TimeSpan previous = DataLock.DefaultWait;
        DataLock.DefaultWait = TimeSpan.Zero;
        try
        {
            using IDisposable scope = UseVolume();

            (int code, string text, _) = CliTests.Run("apply", "--plan", PlanPath, "--journal", JournalPath, "--allowed-root", Root, "--execute");

            Assert.Equal(4, code);
            Assert.Contains("\"error\":\"busy\"", text, StringComparison.Ordinal);
            Assert.Empty(Vol.Moves);
        }
        finally
        {
            DataLock.DefaultWait = previous;
        }
    }

    [Fact]
    public async Task ApplyReadsThePlanOnlyAfterGettingTheLockSoAPlanChangedWhileWaitingIsNotOverwritten()
    {
        PlanFile oldPlan = Make(); // 존 시작 미정: 옛 계획으로 dry-run 하면 존을 정해 다시 저장함
        PlanFile newPlan = Make().Resolve(30000);
        oldPlan.Save(PlanPath);
        var holder = new ForeignLock(PlanPath, JournalPath);
        TimeSpan previous = DataLock.DefaultWait;
        DataLock.DefaultWait = TimeSpan.FromSeconds(20);
        try
        {
            using IDisposable scope = UseVolume();

            Task<(int Code, string Text, string Err)> waiting = Task.Run(() => CliTests.Run("apply", "--plan", PlanPath, "--journal", JournalPath, "--allowed-root", Root, "--dry-run"));
            await Task.Delay(300);
            newPlan.Save(PlanPath); // 서비스가 새 계획을 승격하고 잠금을 풂
            holder.Dispose();

            Assert.Equal(0, (await waiting).Code);
            Assert.Equal(newPlan.Sha(), PlanFile.Load(PlanPath).Sha());
            Assert.Equal(30000, PlanFile.Load(PlanPath).Zone.StartLcn);
        }
        finally
        {
            DataLock.DefaultWait = previous;
        }
    }

    [Fact]
    public void FromPlanAvoidsTheRangeAnotherGamesPlanReserves()
    {
        _ = PartialA();
        PlanFile other = Make().Resolve(100);
        other.Save(Path.Combine(Root, "other.plan.json"));
        using IDisposable scope = UseVolume();

        (int code, _, _) = CliTests.Run("plan", "--from-plan", PlanPath, "--journal", JournalPath);

        Assert.Equal(0, code);
        PlanFile plan = PlanFile.Load(PlanPath);
        long start = plan.Zone.StartLcn!.Value;
        long end = start + plan.Zone.Clusters;
        Assert.All(other.Tasks, t => Assert.True(end <= t.TargetLcn || t.TargetLcn + t.Clusters <= start, $"zone [{start},{end}) overlaps {t.TargetLcn}"));
    }

    [Fact]
    public void TraceRouteWithPartialJournalDoesNotOverwriteTheActivePlan()
    {
        long moved = PartialA();
        string trace = WriteTrace();
        string before = PlanFile.Load(PlanPath).Sha();
        using IDisposable scope = UseVolume();

        (int code, string text, _) = CliTests.Run("plan", "--trace", trace, "--origin-root", Root, "--zone-mib", "2", "--out", PlanPath, "--journal", JournalPath);

        Assert.Equal(0, code);
        JObject json = Json(text);
        Assert.Equal(moved, json["fixed_clusters"]);
        Assert.NotNull(json["archived_journal"]);
        Assert.True(File.Exists(PlanPath + PlanOutput.PrefetchSuffix));
        PlanFile plan = PlanFile.Load(PlanPath);
        Assert.NotEqual(before, plan.Sha());
        Apply(plan, Exec(2L << 30));
        Assert.Equal(80 - moved, Vol.Moves.Sum(m => m.N));
    }

    [Fact]
    public void WithoutJournalRowsPlanBehavesAsBefore()
    {
        string trace = WriteTrace();
        string plain = Path.Combine(Root, "plain.plan.json");
        string withJournal = Path.Combine(Root, "journaled.plan.json");

        (int c1, string t1, _) = CliTests.Run("plan", "--trace", trace, "--origin-root", Root, "--zone-mib", "2", "--out", plain);
        (int c2, string t2, _) = CliTests.Run("plan", "--trace", trace, "--origin-root", Root, "--zone-mib", "2", "--out", withJournal, "--journal", JournalPath);

        Assert.Equal((0, 0), (c1, c2));
        Assert.Equal(File.ReadAllBytes(plain), File.ReadAllBytes(withJournal));
        Assert.Equal(Json(t1).Select(kv => kv.Key), Json(t2).Select(kv => kv.Key)); // 잔여 재계획을 안 했으면 출력 모양도 그대로
        Assert.NotNull(Json(t1)["prefetch_trace"]);
        Assert.True(File.Exists(withJournal + PlanOutput.PrefetchSuffix));
    }

    [Fact]
    public void DryRunWithAJournalChangesNothing()
    {
        _ = PartialA();
        string trace = WriteTrace();
        byte[] plan = File.ReadAllBytes(PlanPath);
        byte[] journal = File.ReadAllBytes(JournalPath);
        string[] files = Directory.GetFiles(Root).Order().ToArray();

        (int code, string text, _) = CliTests.Run("plan", "--trace", trace, "--origin-root", Root, "--zone-mib", "2", "--dry-run", "--journal", JournalPath);

        Assert.Equal(0, code);
        Assert.Equal(true, Json(text)["dry_run"]);
        Assert.Equal(plan, File.ReadAllBytes(PlanPath));
        Assert.Equal(journal, File.ReadAllBytes(JournalPath));
        Assert.Equal(files, Directory.GetFiles(Root).Order().ToArray());
    }

    [Fact]
    public void PlanStillRequiresItsInputsWithoutFromPlan()
    {
        (int code, _, string err) = CliTests.Run("plan", "--trace", "t.json", "--zone-mib", "5");

        Assert.Equal(2, code);
        Assert.Contains("--origin-root", err, StringComparison.Ordinal);
    }

    [Fact]
    public void ApplyDryRunWithAnotherPlansJournalIsRejectedAndKeepsTheActivePlan()
    {
        PlanFile a = Make().Resolve(20000);
        Assert.Equal("write_cap", Apply(a, Exec(64 * Cb)).Stopped);
        PlanFile b = Make().Resolve(30000);
        b.Save(PlanPath);
        byte[] plan = File.ReadAllBytes(PlanPath);
        byte[] journal = File.ReadAllBytes(JournalPath);

        (int code, string text, _) = CliTests.Run("apply", "--plan", PlanPath, "--journal", JournalPath, "--allowed-root", Root, "--dry-run");

        Assert.Equal(1, code);
        Assert.Equal("""{"error":"PlanRejected","detail":"journal belongs to a different plan"}""" + Environment.NewLine, text);
        Assert.Equal(plan, File.ReadAllBytes(PlanPath));
        Assert.Equal(journal, File.ReadAllBytes(JournalPath));
    }

    [Fact]
    public void ApplyDryRunWithoutAZoneStartSkipsRangesOtherGamesReserve()
    {
        string first = Path.Combine(Root, "first.plan.json");
        string second = Path.Combine(Root, "second.plan.json");
        Make().Save(first);
        Make().Save(second);
        using IDisposable scope = UseVolume();

        (int c1, _, _) = CliTests.Run("apply", "--plan", first, "--journal", Path.Combine(Root, "first.jsonl"), "--allowed-root", Root, "--dry-run");
        (int c2, _, _) = CliTests.Run("apply", "--plan", second, "--journal", Path.Combine(Root, "second.jsonl"), "--allowed-root", Root, "--dry-run");

        Assert.Equal((0, 0), (c1, c2));
        PlanFile a = PlanFile.Load(first);
        PlanFile b = PlanFile.Load(second);
        Assert.NotNull(a.Zone.StartLcn);
        Assert.NotNull(b.Zone.StartLcn);
        Assert.All(a.Tasks, ta => Assert.All(b.Tasks, tb => Assert.True(
            ta.TargetLcn + ta.Clusters <= tb.TargetLcn || tb.TargetLcn + tb.Clusters <= ta.TargetLcn, $"{ta.TargetLcn} overlaps {tb.TargetLcn}")));
    }

    [Fact]
    public void ApplyWithAFixedZoneStillAvoidsARangeAnotherGamePlanReserves()
    {
        string mine = Path.Combine(Root, "mine.plan.json");
        Make().Resolve(20000).Save(mine);
        PlanFile other = Make().Resolve(20010); // 아직 비어 있지만 다른 게임이 쓸 구간과 겹침
        other.Save(Path.Combine(Root, "other.plan.json"));
        using IDisposable scope = UseVolume();

        (int code, string text, _) = CliTests.Run("apply", "--plan", mine, "--journal", Path.Combine(Root, "mine.jsonl"), "--allowed-root", Root, "--dry-run");

        Assert.Equal(0, code);
        Assert.Equal(true, Json(text)["zone_retargeted"]); // 예약을 모르면 20000 을 그대로 써서 이 점유 fallback 도 일어나지 않음
        PlanFile moved = PlanFile.Load(mine);
        Assert.All(moved.Tasks, t => Assert.All(other.Tasks, o => Assert.True(
            t.TargetLcn + t.Clusters <= o.TargetLcn || o.TargetLcn + o.Clusters <= t.TargetLcn, $"{t.TargetLcn} overlaps {o.TargetLcn}")));
    }

    [Fact]
    public void ApplyReportsBusyWithoutDoingAnythingWhileAnotherHoldsTheLock()
    {
        PlanFile plan = Make().Resolve(20000);
        plan.Save(PlanPath);
        byte[] before = File.ReadAllBytes(PlanPath);
        using var holder = new ForeignLock(PlanPath);
        TimeSpan previous = DataLock.DefaultWait;
        DataLock.DefaultWait = TimeSpan.Zero;
        try
        {
            using IDisposable scope = UseVolume();

            (int code, string text, _) = CliTests.Run("apply", "--plan", PlanPath, "--journal", JournalPath, "--allowed-root", Root, "--execute");

            Assert.Equal(4, code);
            Assert.Contains("\"error\":\"busy\"", text, StringComparison.Ordinal);
            Assert.Empty(Vol.Moves);
            Assert.False(File.Exists(JournalPath));
            Assert.Equal(before, File.ReadAllBytes(PlanPath));
        }
        finally
        {
            DataLock.DefaultWait = previous;
        }
    }

    [Fact]
    public void RevertReportsBusyWhileAnotherHoldsTheLockAndLeavesTheJournalAlone()
    {
        _ = PartialA();
        byte[] journal = File.ReadAllBytes(JournalPath);
        using var holder = new ForeignLock(PlanPath, JournalPath);
        TimeSpan previous = DataLock.DefaultWait;
        DataLock.DefaultWait = TimeSpan.Zero;
        try
        {
            using IDisposable scope = UseVolume();

            (int code, string text, _) = CliTests.Run("revert", "--journal", JournalPath, "--allowed-root", Root, "--execute");

            Assert.Equal(4, code);
            Assert.Contains("\"error\":\"busy\"", text, StringComparison.Ordinal);
            Assert.Empty(Vol.Moves);
            Assert.Equal(journal, File.ReadAllBytes(JournalPath));
        }
        finally
        {
            DataLock.DefaultWait = previous;
        }
    }

    [Fact]
    public void FromPlanReplanIsRefusedWithoutChangingAnythingWhileAnotherHoldsTheLock()
    {
        _ = PartialA();
        byte[] plan = File.ReadAllBytes(PlanPath);
        byte[] journal = File.ReadAllBytes(JournalPath);
        using var holder = new ForeignLock(PlanPath);
        TimeSpan previous = DataLock.DefaultWait;
        DataLock.DefaultWait = TimeSpan.Zero;
        try
        {
            using IDisposable scope = UseVolume();

            (int code, string text, _) = CliTests.Run("plan", "--from-plan", PlanPath, "--journal", JournalPath);

            Assert.Equal(1, code);
            Assert.Contains("zone lock busy", text, StringComparison.Ordinal);
            Assert.Equal(plan, File.ReadAllBytes(PlanPath));
            Assert.Equal(journal, File.ReadAllBytes(JournalPath));
        }
        finally
        {
            DataLock.DefaultWait = previous;
        }
    }
}
