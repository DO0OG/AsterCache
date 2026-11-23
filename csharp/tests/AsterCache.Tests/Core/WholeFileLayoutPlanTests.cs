using AsterCache.Core.Apply;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tests.Service;
using Xunit;

namespace AsterCache.Tests.Core;

/// 통째 배치 계획: 계획 만들기 (capacityBytes), 적용, 배치 검사, 회당 상한, 잔여 재계획, 예열 예산. 두 시험 파일은 각각 256 클러스터 (1 MiB) 이고 핫 범위는 둘 다 160 클러스터라 통째 대상입니다.
public sealed class WholeFileLayoutPlanTests : ApplyTestBase
{
    private const long Capacity = 4L << 20;
    private const long Mib = 1L << 20;

    public WholeFileLayoutPlanTests() => Sessions = [Run(Enumerable.Range(0, 10).Select(b => (F1, (long)b)).Concat(Enumerable.Range(0, 10).Select(b => (F2, (long)b))).ToArray())];

    private PlanFile Whole(long? start = null, long capacity = Capacity) =>
        PlanFile.Make(Sessions, Root, 2 << 20, Cb, p => Files[p], zoneStartLcn: start, capacityBytes: capacity);

    private static ApplyOptions Exec(long writeCap = 2L << 30) => new(Execute: true, WriteCap: writeCap);

    private static bool OneRun(IReadOnlyList<Extent> e) => e.Count == 1;

    [Fact]
    public void WithoutACapacityThePlanIsTheRangePlanAndCarriesNoLayoutMarker()
    {
        PlanFile ranges = Make();

        Assert.Equal(ranges.Dumps(), PlanFile.Make(Sessions, Root, 2 << 20, Cb, p => Files[p], preserveBytes: 64 << 20, capacityBytes: 0).Dumps());
        Assert.False(ranges.Stats.ContainsKey(WholeFileLayout.RuleKey));
        Assert.Null(WholeFileLayout.WholeFiles(ranges));
    }

    [Fact]
    public void AWholePlanKeepsTheHotBudgetZoneLengthAndPassesTheHotRangesThrough()
    {
        PlanFile ranges = PlanFile.Make(Sessions, Root, 2 << 20, Cb, p => Files[p]);

        PlanFile whole = Whole();

        Assert.Equal(512, whole.Zone.Clusters); // 핫 예산 기준 길이 그대로 (예열 예산의 근거)
        Assert.Equal(ranges.Zone.Clusters, whole.Zone.Clusters);
        Assert.Equal(ranges.Order, whole.Order);
        Assert.Equal(ranges.Stats["hot_bytes"], whole.Stats["hot_bytes"]);
        Assert.Equal(512L, whole.Stats["zone_used_clusters"]); // 실제로 쓴 길이는 따로
        Assert.Equal(WholeFileLayout.RuleVersion, whole.Stats[WholeFileLayout.RuleKey]);
        Assert.Equal([F1, F2], WholeFileLayout.WholeFiles(whole));
        Assert.Equal(PlanFile.CurrentVersion, whole.Version);
    }

    [Fact]
    public void AWholePlanHasOneTaskPerFileAndSurvivesASaveAndLoad()
    {
        PlanFile whole = Whole();

        Assert.Equal([new PlanTask(F1, 0, 256, 0), new PlanTask(F2, 0, 256, 256)], whole.Tasks); // 존을 아직 못 정함: 목표는 시작 기준 오프셋
        string path = Path.Combine(Root, "whole.plan.json");
        whole.Save(path);
        PlanFile loaded = PlanFile.Load(path);

        Assert.Equal(whole.Dumps(), loaded.Dumps());
        Assert.Equal([F1, F2], WholeFileLayout.WholeFiles(loaded));
    }

    [Fact]
    public void AnOldRangePlanStillLoadsAndIsJudgedWithTheRangeRule()
    {
        PlanFile old = PlanFile.Load(PlanFixtures.PathOf("e3/plan.json"));

        Assert.Null(WholeFileLayout.WholeFiles(old));
        Assert.False(old.Stats.ContainsKey(WholeFileLayout.RuleKey));
    }

    [Fact]
    public void AppliedWholeFilesEndUpAsOneRunInsideTheZoneAndVerifyAsContiguous()
    {
        PlanFile plan = Whole(20000);

        ApplyReport rep = Apply(plan, Exec());

        Assert.Empty(rep.Failed);
        Assert.All(Vol.Ext.Values, e => Assert.True(OneRun(e)));
        LayoutReport report = Verify(plan);
        Assert.True(report.Contiguous);
        Assert.Equal(512, report.InZoneClusters);
        Assert.Equal(512, report.HotClusters);
        Assert.False(report.ResumeNeeded);
    }

    [Fact]
    public void AFileSplitIntoTwoPiecesFailsTheCheck()
    {
        PlanFile plan = Whole(20000);
        Apply(plan, Exec());

        Vol.Move(F1, 0, 150000, 8);

        LayoutReport report = Verify(plan);
        Assert.False(report.Contiguous);
        Assert.Equal(1, report.OrderedRuns);
    }

    [Fact]
    public void AOneRunFileOutsideTheZoneFailsTheCheckAndTheRestIsAPartialApplication()
    {
        PlanFile plan = Whole(20000);
        Apply(plan, Exec());

        Vol.Move(F2, 0, 160000, 256);

        LayoutReport report = Verify(plan);
        Assert.False(report.Contiguous);
        Assert.Equal(256, report.InZoneClusters);
        Assert.True(report.ResumeNeeded);
    }

    [Fact]
    public void FilesLeftInPlaceAreNotJudgedEvenWhenTheyAreScattered()
    {
        Sessions = [Run(Enumerable.Range(0, 10).Select(b => (F1, (long)b)).Append((F2, 0L)).ToArray())]; // F2 는 핫이 16 클러스터뿐이라 mostly_cold
        PlanFile plan = Whole(20000);
        Assert.Equal([F1], WholeFileLayout.WholeFiles(plan));
        Assert.Equal(1L, ((AsterCache.Core.Json.JObject)plan.Stats[WholeFileLayout.InPlaceFilesKey]!)[WholeFileLayout.MostlyColdReason]);
        Apply(plan, Exec());

        Vol.Move(F2, 0, 150000, 8);
        Vol.Move(F2, 100, 151000, 8);

        Assert.True(Verify(plan).Contiguous);
        Assert.Equal(Files[F2].Sum(e => e.Clusters), Vol.Ext[F2].Sum(e => e.Clusters));
    }

    [Fact]
    public void ThePlanLeavesTheLastFilesInPlaceWhenTheCapacityIsTooSmall()
    {
        PlanFile plan = Whole(capacity: 300 * Cb);

        Assert.Equal([F1], WholeFileLayout.WholeFiles(plan));
        Assert.Equal(1L, ((AsterCache.Core.Json.JObject)plan.Stats[WholeFileLayout.InPlaceFilesKey]!)[WholeFileLayout.ZoneFullReason]);
        Assert.Equal(256L, plan.Stats["zone_used_clusters"]);
        Assert.Equal(160 * Cb, plan.Stats["overflow_bytes"]);
    }

    [Fact]
    public void AFirstFileLargerThanTheWriteCapIsMovedToTheEndInsteadOfStoppingHalfWay()
    {
        PlanFile plan = Whole(20000);

        ApplyReport rep = Apply(plan, Exec(writeCap: Mib / 2));

        Assert.Equal(Mib, rep.MovedBytes);
        Assert.Equal("write_cap", rep.Stopped); // 둘째 파일은 시작하지 않음
        Assert.True(OneRun(Vol.Ext[F1]));
        Assert.Equal(Files[F2], Vol.Ext[F2]);
    }

    [Fact]
    public void AFileThatDoesNotFitTheRemainingCapIsNotStartedAndTheNextRunContinuesWithIt()
    {
        PlanFile plan = Whole(20000);

        ApplyReport first = Apply(plan, Exec(writeCap: Mib + Mib / 2));

        Assert.Equal((Mib, "write_cap"), (first.MovedBytes, first.Stopped));
        Assert.Equal(Files[F2], Vol.Ext[F2]); // 일부만 옮겨진 파일이 없음

        ApplyReport second = Apply(plan, Exec(writeCap: Mib + Mib / 2));

        Assert.Equal(Mib, second.MovedBytes);
        Assert.Null(second.Stopped);
        Assert.True(Verify(plan).Contiguous);
    }

    [Fact]
    public void ARangePlanStillStopsInTheMiddleOfATaskAtTheWriteCap()
    {
        PlanFile plan = Make().Resolve(20000);

        ApplyReport rep = Apply(plan, Exec(writeCap: 8 * Cb));

        Assert.Equal((8 * Cb, "write_cap"), (rep.MovedBytes, rep.Stopped));
    }

    [Fact]
    public void AResidualPlanFixesAWholeFileOnlyWhenItIsAlreadyOneRunAtItsPreviousTarget()
    {
        PlanFile plan = Whole(20000);
        Vol.Move(F1, 0, 20000, 256); // F1 은 목표에 통째로, F2 는 앞 100 클러스터만 옮겨진 채 멈춤
        Vol.Move(F2, 0, 20256, 100);

        PlanFile residual = ResidualPlanner.Build(plan, plan.Tasks, p => Vol.Ext[p], n => Vol.Bitmap().FindFreeRun(n, true, 0, PlanGuards.ZoneAlign));

        Assert.True(residual.IsResidual);
        Assert.Equal(new PlanTask(F1, 0, 256, 20000), Assert.Single(residual.Tasks, t => t.Path == F1));
        Assert.Equal(256, Assert.Single(residual.Tasks, t => t.Path == F2).Clusters); // 일부가 맞아도 파일 전체를 잔여로
        Assert.Equal(256L, residual.Stats["fixed_clusters"]);
        Assert.Equal(WholeFileLayout.RuleVersion, residual.Stats[WholeFileLayout.RuleKey]);
        Assert.Equal([F1, F2], WholeFileLayout.WholeFiles(residual));
        Assert.Equal(plan.Order, residual.Order);

        ApplyReport rep = Apply(residual, Exec());

        Assert.Empty(rep.Failed);
        Assert.True(Verify(residual).Contiguous); // 잔여 계획: 파일별 작업 목표 범위 기준
        Assert.True(OneRun(Vol.Ext[F1]) && OneRun(Vol.Ext[F2]));
    }

    private GameSettings GameOf(PlanFile plan)
    {
        string planPath = Path.Combine(Directory.CreateDirectory(Path.Combine(Root, "plans")).FullName, "a.plan.json");
        plan.Save(planPath);
        return Games.Make("a", "a.exe") with { Plan = planPath, OriginRoot = Root, AllowedRoots = [Root] };
    }

    [Fact]
    public void ThePrewarmBudgetOfAWholePlanIsTheHotRangeSumNotTheZoneLength()
    {
        PlanFile ranges = PlanFile.Make(Sessions, Root, 2 << 20, Cb, p => Files[p]);
        PlanFile whole = Whole(20000);

        Assert.Equal(ranges.Zone.Clusters * Cb, PrefetchStreamer.ZoneBytes(GameOf(ranges)));
        Assert.Equal(320 * Cb, PrefetchStreamer.ZoneBytes(GameOf(whole))); // 핫 범위 (160 + 160) 합. zone.clusters (512) 가 아님
        Assert.Equal(512, whole.Zone.Clusters);
    }

    [Fact]
    public void AWholeResidualPlanCountsTheHotRangesNotTheColdBytesInItsTasks()
    {
        PlanFile plan = Whole(20000);
        PlanFile residual = ResidualPlanner.Build(plan, [], p => Vol.Ext[p], n => Vol.Bitmap().FindFreeRun(n, true, 0, PlanGuards.ZoneAlign));

        Assert.True(residual.IsResidual);
        Assert.Equal(512, residual.Tasks.Sum(t => t.Clusters));
        Assert.Equal(320 * Cb, PrefetchStreamer.ZoneBytes(GameOf(residual)));
    }
}
