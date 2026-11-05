using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

/// 잔여 재계획: 이미 이전 목표에 놓인 조각은 고정 (이동 0), 나머지만 새 빈 구간에. 기본 핫 범위는 F1@192, F2@0, F1@0, F1@64, F2@128 (각 16 클러스터, 합 80).
public sealed class ResidualPlannerTests : ApplyTestBase
{
    private const long HotTotal = 80;

    private PlanFile Candidate() => PlanFile.Make(Sessions, Root, 2 << 20, Cb, p => Vol.Ext[p], preserveBytes: 64 << 20);

    private long? FindRun(long n) => Vol.Bitmap().FindFreeRun(n, true, 0, PlanGuards.ZoneAlign);

    private PlanFile Build(PlanFile candidate, params PlanTask[] previous) => ResidualPlanner.Build(candidate, previous, p => Vol.Ext[p], FindRun);

    private static List<PlanTask> Residual(PlanFile plan) => plan.Tasks.Where(t => t.TargetLcn >= plan.Zone.StartLcn && t.TargetLcn < plan.Zone.StartLcn + plan.Zone.Clusters).ToList();

    // 계획이 약속한 대로: 고정 task 는 DonePrefix 로 빠지고 존 점유 검사가 거절하지 않으며, 옮길 바이트는 실제 잔여량.
    private void AssertDryRunMovesOnlyTheResidual(PlanFile plan, long residualClusters)
    {
        Vol.Moves.Clear();

        ApplyReport rep = Apply(plan, new ApplyOptions(DryRun: true));

        Assert.Equal(residualClusters * Cb, rep.BytesTodo);
        Assert.Equal(Residual(plan).Count, rep.TasksTodo);
        Assert.Equal(plan.Zone.StartLcn, rep.ZoneStartLcn);
        Assert.Empty(Vol.Moves);
        Assert.Equal(residualClusters, plan.Zone.Clusters);
        Assert.Equal(residualClusters, plan.Stats["zone_used_clusters"]);
        Assert.Equal(residualClusters * Cb, plan.Stats["moved_bytes"]);
        Assert.Equal(1L, plan.Stats["residual"]);
    }

    [Fact]
    public void DefaultHotRangesAreTheFiveExpectedOnes()
    {
        Assert.Equal([new PlanRange(F1, 192, 16), new PlanRange(F2, 0, 16), new PlanRange(F1, 0, 16), new PlanRange(F1, 64, 16), new PlanRange(F2, 128, 16)], Candidate().Order);
    }

    [Fact]
    public void AStoppedTaskKeepsItsMovedClustersFixedAndOnlyTheRestIsPlanned()
    {
        PlanFile cand = Candidate();
        Vol.Move(F1, 192, 20000, 6); // 16 클러스터 task 의 앞 6 개만 옮기고 멈춤

        PlanFile plan = Build(cand, new PlanTask(F1, 192, 16, 20000));

        Assert.Contains(new PlanTask(F1, 192, 6, 20000), plan.Tasks);
        Assert.Equal(10, Assert.Single(plan.Tasks, t => t.Path == F1 && t.Vcn == 198).Clusters);
        Assert.Equal(6L, plan.Stats["fixed_clusters"]);
        Assert.Equal(6 * Cb, plan.Stats["in_place_bytes"]);
        Assert.Equal(HotTotal * Cb, plan.Stats["hot_bytes"]);
        AssertDryRunMovesOnlyTheResidual(plan, HotTotal - 6);
    }

    [Fact]
    public void ACompletedPieceInTheMiddleOfATaskIsFixedExactly()
    {
        PlanFile cand = Candidate();
        Vol.Move(F1, 200, 20008, 4); // task 의 접두부가 아니라 가운데 4 개만 완료

        PlanFile plan = Build(cand, new PlanTask(F1, 192, 16, 20000));

        Assert.Contains(new PlanTask(F1, 200, 4, 20008), plan.Tasks);
        Assert.Equal(8, Assert.Single(plan.Tasks, t => t.Path == F1 && t.Vcn == 192).Clusters);
        Assert.Equal(4, Assert.Single(plan.Tasks, t => t.Path == F1 && t.Vcn == 204).Clusters);
        Assert.Equal(4L, plan.Stats["fixed_clusters"]);
        AssertDryRunMovesOnlyTheResidual(plan, HotTotal - 4);
    }

    [Fact]
    public void PiecesScatteredOverTwoOldZonesAreBothFixed()
    {
        PlanFile cand = Candidate();
        Vol.Move(F1, 192, 20000, 5); // 옛 존 A 에 일부
        Vol.Move(F2, 9, 30009, 7); // 옛 존 B 에 일부 (뒤쪽)

        PlanFile plan = Build(cand, new PlanTask(F1, 192, 16, 20000), new PlanTask(F2, 0, 16, 30000));

        Assert.Contains(new PlanTask(F1, 192, 5, 20000), plan.Tasks);
        Assert.Contains(new PlanTask(F2, 9, 7, 30009), plan.Tasks);
        Assert.Equal(12L, plan.Stats["fixed_clusters"]);
        AssertDryRunMovesOnlyTheResidual(plan, HotTotal - 12);
    }

    [Fact]
    public void ResidualTasksAreLaidContiguouslyInAccessOrderInTheFoundRun()
    {
        PlanFile cand = Candidate();
        Vol.Move(F1, 192, 20000, 16);
        long? asked = null;

        PlanFile plan = ResidualPlanner.Build(cand, [new PlanTask(F1, 192, 16, 20000)], p => Vol.Ext[p], n => { asked = n; return 5000; });

        Assert.Equal(HotTotal - 16, asked);
        Assert.Equal(5000, plan.Zone.StartLcn);
        long next = 5000;
        foreach (PlanTask t in Residual(plan))
        {
            Assert.Equal(next, t.TargetLcn);
            next += t.Clusters;
        }

        Assert.Equal(5000 + HotTotal - 16, next);
        Assert.Equal([F2, F1, F1, F2], Residual(plan).Select(t => t.Path)); // 접근 순서 (고정된 F1@192 는 자리 그대로)
    }

    [Fact]
    public void TwoResidualReplansInARowNeverMoveAnAlreadyFixedPieceAgain()
    {
        PlanFile cand = Candidate();
        var p0 = new PlanTask(F1, 192, 16, 20000);
        Vol.Move(F1, 192, 20000, 16);
        PlanFile plan1 = Build(cand, p0);
        PlanTask first = Residual(plan1).First();
        Vol.Move(first.Path, first.Vcn, first.TargetLcn, (uint)(first.Clusters / 2)); // 첫 잔여 task 가 절반에서 멈춤

        PlanFile plan2 = Build(cand, [p0, .. plan1.Tasks]);

        Assert.Contains(p0, plan2.Tasks);
        Assert.Contains(new PlanTask(first.Path, first.Vcn, first.Clusters / 2, first.TargetLcn), plan2.Tasks);
        Assert.Equal(16 + first.Clusters / 2, plan2.Stats["fixed_clusters"]);
        AssertDryRunMovesOnlyTheResidual(plan2, HotTotal - 16 - first.Clusters / 2);

        ApplyReport rep = Apply(plan2, new ApplyOptions(Execute: true));

        Assert.Empty(rep.Failed);
        Assert.Equal((HotTotal - 16 - first.Clusters / 2) * Cb, rep.MovedBytes);
        Assert.Equal(HotTotal - 16 - first.Clusters / 2, Vol.Moves.Sum(m => m.N));
        var fixedRanges = plan2.Tasks.Where(t => !Residual(plan2).Contains(t)).ToList();
        Assert.All(Vol.Moves, m => Assert.DoesNotContain(fixedRanges, f => f.Path == m.Path && m.Vcn < f.Vcn + f.Clusters && f.Vcn < m.Vcn + m.N));
        LayoutReport layout = Verify(plan2);
        Assert.True(layout.Contiguous);
        Assert.Equal(plan2.Stats["expected_runs"], layout.OrderedRuns);
    }

    [Fact]
    public void WhenNothingIsLeftTheFinderIsNotCalledAndTheZoneIsADeterministicNonNullValue()
    {
        PlanFile cand = Candidate();
        PlanTask[] prev = [new(F1, 192, 16, 20000), new(F2, 0, 16, 20016), new(F1, 0, 16, 20032), new(F1, 64, 16, 20048), new(F2, 128, 16, 20064)];
        foreach (PlanTask t in prev)
        {
            Vol.Move(t.Path, t.Vcn, t.TargetLcn, (uint)t.Clusters);
        }

        PlanFile plan = ResidualPlanner.Build(cand, prev, p => Vol.Ext[p], _ => throw new InvalidOperationException("finder must not be called"));

        Assert.Equal(prev, plan.Tasks);
        Assert.Equal(20000, plan.Zone.StartLcn); // 잔여가 없으면 첫 task 의 목표 (null 이면 ResolveZone 이 목표를 전부 밀어 버림)
        Assert.Equal(HotTotal, plan.Stats["fixed_clusters"]);
        Assert.Equal(HotTotal * Cb, plan.Stats["in_place_bytes"]);
        AssertDryRunMovesOnlyTheResidual(plan, 0);
        Assert.Equal(plan.Tasks, PlanGuards.ResolveZone(plan, Vol, p => Vol.Ext[p]).Tasks);
        Assert.True(Verify(plan).Contiguous);
    }

    [Fact]
    public void AFinderThatFindsNothingRejectsThePlan()
    {
        PlanFile cand = Candidate();

        PlanRejectedException e = Assert.Throws<PlanRejectedException>(() => ResidualPlanner.Build(cand, [], p => Vol.Ext[p], _ => null));

        Assert.Equal($"no free run of {HotTotal} clusters", e.Message);
    }

    [Fact]
    public void ClaimsThatTheFilesDoNotBackUpAreNotTrusted()
    {
        PlanFile cand = Candidate(); // 이전 목표만 있고 실제로는 하나도 안 옮겨짐 (done 표시가 있어도 extent 가 진실)

        PlanFile plan = Build(cand, new PlanTask(F1, 192, 16, 20000), new PlanTask(F2, 0, 16, 20016));

        Assert.Equal(0L, plan.Stats["fixed_clusters"]);
        Assert.Equal(0L, plan.Stats["in_place_bytes"]);
        AssertDryRunMovesOnlyTheResidual(plan, HotTotal);
    }

    [Fact]
    public void OrderFingerprintsAndOtherStatsAreKept()
    {
        PlanFile cand = Candidate();
        Vol.Move(F2, 128, 20000, 16);

        PlanFile plan = Build(cand, new PlanTask(F2, 128, 16, 20000));

        Assert.Equal(cand.Order, plan.Order);
        Assert.Equal(cand.Fingerprints, plan.Fingerprints);
        Assert.Equal((cand.OriginRoot, cand.Volume, cand.ClusterBytes, cand.Block), (plan.OriginRoot, plan.Volume, plan.ClusterBytes, plan.Block));
        Assert.Equal(cand.Stats["hot_blocks"], plan.Stats["hot_blocks"]);
        Assert.Equal(cand.Stats["units"], plan.Stats["units"]);
        Assert.NotEqual(cand.Sha(), plan.Sha());
    }

    [Fact]
    public void AFileWhoseExtentsCannotBeReadIsHeldBackFromOrderFingerprintsAndTasks()
    {
        PlanFile cand = Candidate();

        PlanFile plan = ResidualPlanner.Build(cand, [], p => p == F2 ? null : Vol.Ext[p], FindRun);

        Assert.All(plan.Order, o => Assert.Equal(F1, o.Path));
        Assert.Equal(3, plan.Order.Count);
        Assert.All(plan.Tasks, t => Assert.Equal(F1, t.Path));
        Assert.Equal([F1], plan.Fingerprints.Select(f => f.Path));
        Assert.Equal(1L, plan.Stats["skipped_files"]);
        Assert.Equal(3 * 16L, plan.Zone.Clusters);
    }

    [Fact]
    public void PreviousTargetsReadsPlannedRowsFromTheJournalAndOnlyZonedOldPlans()
    {
        PlanFile applied = Make();
        Vol.FailAfter = 2; // 두 번 옮기고 중단
        _ = Apply(applied, new ApplyOptions(Execute: true));
        Vol.FailAfter = null;
        PlanFile zoned = Make(30000);
        PlanFile relative = Make();

        IReadOnlyList<PlanTask> prev = ResidualPlanner.PreviousTargets(JournalPath, [zoned, zoned, relative]);

        Assert.Equal(2 * applied.Tasks.Count, prev.Count);
        Assert.True(prev.Take(applied.Tasks.Count).ToHashSet().SetEquals(applied.Tasks)); // 저널 먼저 (이 존은 0 에서 시작)
        Assert.Equal(zoned.Tasks, prev.Skip(applied.Tasks.Count).ToList());
    }

    [Fact]
    public void ResidualPlanFromAnInterruptedJournalFinishesWithoutMovingFixedPiecesAgain()
    {
        PlanFile applied = Make();
        Vol.FailAfter = 2;
        _ = Apply(applied, new ApplyOptions(Execute: true));
        Vol.FailAfter = null;
        long movedBefore = Vol.Moves.Sum(m => m.N);
        Assert.True(movedBefore > 0);
        IReadOnlyList<PlanTask> prev = ResidualPlanner.PreviousTargets(JournalPath, []);

        PlanFile plan = ResidualPlanner.Build(Candidate(), prev, p => Vol.Ext[p], FindRun);

        long fixedClusters = (long)plan.Stats["fixed_clusters"]!;
        Assert.Equal(movedBefore, fixedClusters);
        Vol.Moves.Clear();
        string newJournal = Path.Combine(Root, "j2.jsonl"); // 새 계획은 새 저널 (저널 전환은 다른 작업자 범위)
        ApplyReport rep = Applier.Apply(plan, Vol, [Root], newJournal, Hooks(), new ApplyOptions(Execute: true));
        Assert.Empty(rep.Failed);
        Assert.Equal((HotTotal - fixedClusters) * Cb, rep.MovedBytes);
        Assert.Equal(HotTotal - fixedClusters, Vol.Moves.Sum(m => m.N));
        Assert.True(Verify(plan).Contiguous);
    }

    [Fact]
    public void JournalRowsWithTheWrongShapeAreSkippedAndATruncatedLastRowIsIgnored()
    {
        string path = Path.Combine(Root, "hand.jsonl");
        File.WriteAllText(path, string.Join("\r\n",
            """{"type":"header","plan_sha":"x"}""",
            $$"""{"type":"step","idx":0,"state":"planned","path":{{PyJson.Dumps(F1)}},"vcn":0,"clusters":4,"target_lcn":900,"from":[]}""",
            """{"type":"step","idx":1,"state":"planned","path":5,"vcn":0,"clusters":4,"target_lcn":900,"from":[]}""",
            $$"""{"type":"step","idx":2,"state":"planned","path":{{PyJson.Dumps(F1)}},"vcn":4,"clusters":0,"target_lcn":900,"from":[]}""",
            $$"""{"type":"step","idx":3,"state":"done","path":{{PyJson.Dumps(F1)}},"vcn":8,"clusters":4,"target_lcn":912}""",
            """{"type":"step","idx":4,"state":"plan""") + "\r\n");

        IReadOnlyList<PlanTask> prev = ResidualPlanner.PreviousTargets(path, []);

        Assert.Equal([new PlanTask(F1, 0, 4, 900)], prev);
        Assert.Empty(ResidualPlanner.PreviousTargets(null, []));
        Assert.Empty(ResidualPlanner.PreviousTargets(Path.Combine(Root, "missing.jsonl"), []));
    }

    [Fact]
    public void AJournalThatCannotBeParsedIsNotSilentlyTreatedAsEmpty()
    {
        string path = Path.Combine(Root, "broken.jsonl");
        File.WriteAllText(path, "not json\r\n{\"type\":\"header\"}\r\n");

        _ = Assert.Throws<PyValueException>(() => ResidualPlanner.PreviousTargets(path, []));
    }
}
