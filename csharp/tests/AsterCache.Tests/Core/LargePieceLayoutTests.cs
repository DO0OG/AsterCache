using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

/// 큰 조각 배치: 통째 배치에 들지 않는 파일에서 핫 비율이 높은 구간을 한 조각으로 zone 에 놓는 분류 (Classify) 와 배정 (Allocate).
/// 구간 단위는 64 클러스터 (largePieceMinBytes = 64 클러스터, 16 의 배수라 그대로) 로 줄여서 씁니다.
public sealed class LargePieceLayoutTests
{
    private const long Cb = 4096;
    private const long Zone = 100000;
    private const long Unit = 64;

    private static PlanRange R(string path, long vcn, long clusters) => new(path, vcn, clusters);

    private static Dictionary<string, long> Sizes(params (string Path, long Clusters)[] files) => files.ToDictionary(f => f.Path, f => f.Clusters * Cb, StringComparer.Ordinal);

    private static List<FileDecision> Classify(IReadOnlyList<PlanRange> order, Dictionary<string, long> sizes, long capacityClusters = 100000, Func<string, bool>? sparse = null,
        Func<string, string?>? reason = null, long maxFileBytes = long.MaxValue, long minBytes = Unit * Cb) =>
        WholeFileLayout.Classify(order, sizes, Cb, capacityClusters, reason ?? (_ => null), maxFileBytes, largePieceMinBytes: minBytes, sparse: sparse);

    private static Dictionary<string, IReadOnlyList<Extent>?> Ext(params (string Path, Extent[] Extents)[] files) =>
        files.ToDictionary(f => f.Path, f => (IReadOnlyList<Extent>?)f.Extents, StringComparer.Ordinal);

    [Fact]
    public void TheDefaultMinimumIs80MiBAndTheHotRatioIsHalf()
    {
        Assert.Equal(80L << 20, WholeFileLayout.LargePieceMinBytes);
        Assert.Equal(0.5, WholeFileLayout.LargePieceHotRatio);
    }

    [Fact]
    public void ASegmentWithExactlyHalfHotIsChosenAndOneClusterLessIsNot()
    {
        // 256 클러스터 파일, 핫 63 이라 통째 조건 (크기 ≤ 핫 × 2) 에 들지 않음. 구간 0 은 핫 32 (딱 절반), 구간 1 은 31
        List<FileDecision> d = Classify([R("a", 0, 32), R("a", 64, 31)], Sizes(("a", 256)));

        FileDecision a = Assert.Single(d);
        Assert.Equal(Placement.LargePieces, a.Placement);
        Assert.Equal([new PieceDecision(0, Unit, 32, 0)], a.Pieces);
        Assert.Null(a.Reason);
    }

    [Fact]
    public void NeighbouringChosenSegmentsMergeIntoOnePieceAndAFarOneStaysSeparate()
    {
        // 5 구간 (320 클러스터), 핫 구간은 1·2·4: 1 과 2 는 이웃이라 하나 (VCN 64, 길이 128), 4 는 따로. 핫 합 96 이라 통째 조건에 들지 않음
        List<FileDecision> d = Classify([R("a", 64, 32), R("a", 128, 32), R("a", 256, 32)], Sizes(("a", 320)));

        Assert.Equal([new PieceDecision(64, 2 * Unit, 64, 0), new PieceDecision(256, Unit, 32, 2)], Assert.Single(d).Pieces);
    }

    [Fact]
    public void ARunOfSegmentsMergedThroughAHalfHotSegmentKeepsItsRankAtTheFirstAccess()
    {
        // 접근 순서: 구간 2 가 먼저, 구간 1 이 나중. 합친 조각의 Rank 는 먼저 접근한 쪽 (order 위치 0)
        List<FileDecision> d = Classify([R("a", 128, 32), R("a", 64, 32)], Sizes(("a", 320)));

        PieceDecision piece = Assert.Single(Assert.Single(d).Pieces!);
        Assert.Equal((64L, 2 * Unit, 0L), (piece.Vcn, piece.Clusters, piece.Rank));
    }

    [Fact]
    public void ASegmentCutShortByTheEndOfTheFileIsNotChosen()
    {
        // 300 클러스터 = 구간 4 개 + 끝의 44. 핫이 끝 44 에만 있어 (구간 단위가 안 됨) 고르지 않고, 파일이 구간 하나보다 작아도 마찬가지
        List<FileDecision> d = Classify([R("a", 256, 44), R("b", 0, 10)], Sizes(("a", 300), ("b", 50)));

        Assert.All(d, x => Assert.Equal((Placement.InPlace, WholeFileLayout.MostlyColdReason), (x.Placement, x.Reason)));
    }

    [Fact]
    public void AFileWithHotDataInAShortTailAndAChosenSegmentKeepsOnlyTheSegment()
    {
        List<FileDecision> d = Classify([R("a", 0, 64), R("a", 256, 44)], Sizes(("a", 300)));

        Assert.Equal([new PieceDecision(0, Unit, 64, 0)], Assert.Single(d).Pieces);
    }

    [Fact]
    public void AWholeFileCandidateIsNotSplitIntoPieces()
    {
        List<FileDecision> d = Classify([R("a", 0, 64)], Sizes(("a", 128))); // 크기 ≤ 핫 × 2: 통째

        Assert.Equal(Placement.WholeFile, Assert.Single(d).Placement);
        Assert.Null(d[0].Pieces);
    }

    [Fact]
    public void SparseVolatileOrUnmovableFilesAndPiecesOverTheDailyLimitAreNeverPieces()
    {
        PlanRange[] order = [R("sparse", 0, 64), R("volatile", 0, 64), R("big", 0, 64)];
        Dictionary<string, long> sizes = Sizes(("sparse", 256), ("volatile", 256), ("big", 256));

        List<FileDecision> d = Classify(order, sizes, sparse: p => p == "sparse", reason: p => p == "volatile" ? WholeFileLayout.VolatileReason : null, maxFileBytes: 63 * Cb);

        Assert.Equal([WholeFileLayout.MostlyColdReason, WholeFileLayout.VolatileReason, WholeFileLayout.MostlyColdReason], d.Select(x => x.Reason));
        Assert.All(d, x => Assert.Equal(Placement.InPlace, x.Placement));
    }

    [Fact]
    public void TheSegmentLengthIsRoundedUpToASixteenClusterMultiple()
    {
        List<FileDecision> d = Classify([R("a", 0, 48)], Sizes(("a", 192)), minBytes: 40 * Cb); // 40 클러스터 → 48

        Assert.Equal([new PieceDecision(0, 48, 48, 0)], Assert.Single(d).Pieces);
    }

    [Fact]
    public void ADisabledMinimumNeverMakesPieces()
    {
        List<FileDecision> d = Classify([R("a", 0, 64)], Sizes(("a", 256)), minBytes: 0);

        Assert.Equal(WholeFileLayout.MostlyColdReason, Assert.Single(d).Reason);
    }

    [Fact]
    public void WholeFilesAndPiecesShareTheZoneByFirstAccessAndTheLimitCutsTheLateOnes()
    {
        // 접근 순서: w (20), p 의 구간 (64), x (10). 한도 90: w + p = 84, x 는 94 라 제자리
        PlanRange[] order = [R("w", 0, 20), R("p", 64, 64), R("x", 0, 10)];
        Dictionary<string, long> sizes = Sizes(("w", 20), ("p", 256), ("x", 10));

        List<FileDecision> d = Classify(order, sizes, capacityClusters: 90);

        Assert.Equal([Placement.WholeFile, Placement.LargePieces, Placement.InPlace], d.Select(x => x.Placement));
        Assert.Equal(WholeFileLayout.ZoneFullReason, d[2].Reason);

        d = Classify(order, sizes, capacityClusters: 70); // 구간이 안 들어가면 뒤의 x 도 제자리 (가득 참 규칙)
        Assert.Equal([Placement.WholeFile, Placement.InPlace, Placement.InPlace], d.Select(x => x.Placement));
        Assert.Equal([null, WholeFileLayout.ZoneFullReason, WholeFileLayout.ZoneFullReason], d.Select(x => x.Reason));
        Assert.Null(d[1].Pieces);
    }

    [Fact]
    public void ThePiecesOfOneFileAreCutIndependentlyByTheLimit()
    {
        // 구간 둘 (64 + 64) 이 접근 순서로 사이에 w 를 끼고 있음: 한도 100 에서 앞 구간 + w 는 들어가고 뒤 구간은 제자리
        PlanRange[] order = [R("p", 0, 64), R("w", 0, 30), R("p", 192, 64)];

        List<FileDecision> d = Classify(order, Sizes(("p", 320), ("w", 30)), capacityClusters: 100);

        Assert.Equal([new PieceDecision(0, Unit, 64, 0)], d[0].Pieces);
        Assert.Equal(Placement.WholeFile, d[1].Placement);
    }

    [Fact]
    public void AllocatePlacesAPieceInTheMiddleOfAFileBetweenOtherFilesInFirstAccessOrder()
    {
        PlanRange[] order = [R("w", 0, 20), R("p", 64, 64), R("x", 0, 10)];
        Dictionary<string, IReadOnlyList<Extent>?> ext = Ext(("w", [new Extent(0, 500, 20)]), ("p", [new Extent(0, 1000, 256)]), ("x", [new Extent(0, 900, 10)]));
        List<FileDecision> d = Classify(order, Sizes(("w", 20), ("p", 256), ("x", 10)));

        WholePlan p = WholeFileLayout.Allocate(order, d, ext, Zone, Cb);

        Assert.Equal([new PlanTask("w", 0, 20, Zone), new PlanTask("p", 64, 64, Zone + 20), new PlanTask("x", 0, 10, Zone + 84)], p.Alloc.Tasks);
        Assert.Equal(94, p.Alloc.Stats.ZoneUsedClusters);
        Assert.Equal(3, p.Alloc.Stats.ExpectedRuns);
        Assert.Equal(0L, p.Offsets["w"]);
        Assert.Equal(new KeyValuePair<(string, long), long>(("p", 64), 20), Assert.Single(p.PieceOffsets!));
        Assert.Equal(order, p.Alloc.Order); // 예열 목록과 hot_bytes 를 쓰는 곳이 바뀌지 않게 입력 그대로
    }

    [Fact]
    public void APieceComesBeforeAWholeFileThatIsAccessedLater()
    {
        PlanRange[] order = [R("p", 64, 64), R("w", 0, 20)];
        Dictionary<string, IReadOnlyList<Extent>?> ext = Ext(("p", [new Extent(0, 1000, 256)]), ("w", [new Extent(0, 500, 20)]));
        List<FileDecision> d = Classify(order, Sizes(("p", 256), ("w", 20)));

        WholePlan p = WholeFileLayout.Allocate(order, d, ext, Zone, Cb);

        Assert.Equal([new PlanTask("p", 64, 64, Zone), new PlanTask("w", 0, 20, Zone + 64)], p.Alloc.Tasks);
    }

    [Fact]
    public void APieceAlreadyAtItsTargetNeedsNoTaskAndAHotByteOutsideThePieceCountsAsLeftInPlace()
    {
        PlanRange[] order = [R("p", 0, 64), R("p", 200, 5)];
        Dictionary<string, IReadOnlyList<Extent>?> ext = Ext(("p", [new Extent(0, Zone, 64), new Extent(64, 5000, 192)]));
        List<FileDecision> d = Classify(order, Sizes(("p", 256)));

        WholePlan p = WholeFileLayout.Allocate(order, d, ext, Zone, Cb);

        Assert.Empty(p.Alloc.Tasks);
        Assert.Equal(64 * Cb, p.Alloc.Stats.InPlaceBytes);
        Assert.Equal(5 * Cb, p.Alloc.Stats.OverflowBytes);
    }

    [Fact]
    public void AFileWithAHoleOutsideThePieceIsDowngradedToInPlace()
    {
        PlanRange[] order = [R("p", 0, 64)];
        Dictionary<string, IReadOnlyList<Extent>?> ext = Ext(("p", [new Extent(0, 1000, 100), new Extent(130, 2000, 126)])); // VCN 100..130 구멍
        List<FileDecision> d = Classify(order, Sizes(("p", 256)));

        WholePlan p = WholeFileLayout.Allocate(order, d, ext, Zone, Cb);

        Assert.Empty(p.Alloc.Tasks);
        Assert.Equal((Placement.InPlace, WholeFileLayout.SparseHolesReason), (p.Decisions[0].Placement, p.Decisions[0].Reason));
    }

    [Fact]
    public void ThePieceStaysOneRunWhenItsTargetOverlapsItsOwnCurrentPlace()
    {
        // 구간 [64, 128) 이 지금 Zone - 10 부터 한 조각: 목표 Zone 과 겹침 → 자기 자리를 목표로 인정 (다시 쓰지 않음)
        PlanRange[] order = [R("p", 64, 64)];
        Dictionary<string, IReadOnlyList<Extent>?> ext = Ext(("p", [new Extent(0, 5000, 64), new Extent(64, Zone + 10, 64), new Extent(128, 6000, 128)]));
        List<FileDecision> d = Classify(order, Sizes(("p", 256)));

        WholePlan p = WholeFileLayout.Allocate(order, d, ext, Zone, Cb);

        Assert.Empty(p.Alloc.Tasks);
        Assert.Equal(10L, p.PieceOffsets![("p", 64)]);
    }

    [Fact]
    public void TheStatsListThePiecesInZoneOrderWithTheirOffsetsAndOnlyWhenThereAreAny()
    {
        PlanRange[] order = [R("w", 0, 20), R("p", 64, 64)];
        Dictionary<string, IReadOnlyList<Extent>?> ext = Ext(("w", [new Extent(0, 500, 20)]), ("p", [new Extent(0, 1000, 256)]));
        WholePlan p = WholeFileLayout.Allocate(order, Classify(order, Sizes(("w", 20), ("p", 256))), ext, Zone, Cb);
        var stats = new JObject();

        WholeFileLayout.AddStats(stats, p.Decisions, p.Offsets, p.PieceOffsets);

        Assert.Equal(new List<object?> { "w" }, stats[WholeFileLayout.WholeFilesKey]);
        JObject row = Assert.IsType<JObject>(Assert.Single((List<object?>)stats[WholeFileLayout.LargePiecesKey]!));
        Assert.Equal(("p", 64L, 64L), (row["path"], row["vcn"], row["clusters"]));
        Assert.Equal(new List<object?> { 20L }, stats[WholeFileLayout.LargeOffsetsKey]);

        var none = new JObject();
        WholeFileLayout.AddStats(none, Classify([R("a", 0, 10)], Sizes(("a", 10))));
        Assert.False(none.ContainsKey(WholeFileLayout.LargePiecesKey)); // 큰 조각이 없는 계획은 키가 늘지 않음
    }
}

/// 큰 조각 배치를 계획·적용·검사까지 (진짜 임시 파일 1 MiB 둘과 가짜 볼륨): F1 은 핫이 앞 64 클러스터뿐이라 큰 조각 (구간 64 클러스터), F2 는 통째.
public sealed class LargePieceLayoutApplyTests : ApplyTestBase
{
    private const long Zone = 100000;
    private const long Min = 64 * Cb;

    public LargePieceLayoutApplyTests() =>
        Sessions = [Run(Enumerable.Range(0, 4).Select(b => (F1, (long)b)).Concat(Enumerable.Range(0, 10).Select(b => (F2, (long)b))).ToArray())];

    private PlanFile Whole(long? start = null) =>
        PlanFile.Make(Sessions, Root, 2 << 20, Cb, p => Files[p], zoneStartLcn: start, capacityBytes: 4L << 20, largePieceMinBytes: Min);

    private static ApplyOptions Exec(long writeCap = 2L << 30, long chunkBytes = 4L << 20) => new(Execute: true, WriteCap: writeCap, ChunkBytes: chunkBytes);

    private Extent PieceExtent() => Vol.Ext[F1].First();

    [Fact]
    public void ThePlanHoldsThePieceAndTheWholeFileInFirstAccessOrderAndSurvivesSaveAndLoad()
    {
        PlanFile plan = Whole(Zone);

        Assert.Equal([F2], WholeFileLayout.WholeFiles(plan));
        Assert.Equal([(F1, 0L, 64L, (long?)0L)], WholeFileLayout.LargePieces(plan));
        Assert.Equal([(F2, 64L)], WholeFileLayout.WholeOffsets(plan)); // 구간이 먼저 접근되어 파일은 그 뒤
        Assert.Equal([(F1, 0L, 64L, Zone), (F2, 0L, 256L, Zone + 64)], plan.Tasks.Select(t => (t.Path, t.Vcn, t.Clusters, t.TargetLcn)));
        Assert.Equal(320L, plan.Stats["zone_used_clusters"]);

        string path = Path.Combine(Root, "large.plan.json");
        plan.Save(path);
        PlanFile loaded = PlanFile.Load(path);
        Assert.Equal(WholeFileLayout.LargePieces(plan), WholeFileLayout.LargePieces(loaded));
        Assert.True(WholeFileLayout.HasPlacements(loaded));
    }

    [Fact]
    public void ARangePlanAndAPlanWithoutPiecesCarryNoLargePieceKeys()
    {
        Assert.False(Make().Stats.ContainsKey(WholeFileLayout.LargePiecesKey));
        Assert.False(PlanFile.Make(Sessions, Root, 2 << 20, Cb, p => Files[p], capacityBytes: 4L << 20).Stats.ContainsKey(WholeFileLayout.LargePiecesKey)); // 기본 80 MiB 보다 작은 파일
    }

    [Fact]
    public void ApplyMovesOnlyTheChosenPieceAndTheCheckJudgesItOnItsOwn()
    {
        PlanFile plan = Whole(Zone);
        List<Extent> restOfF1 = Vol.Ext[F1].Skip(1).ToList();

        ApplyReport rep = Apply(plan, Exec());

        Assert.Null(rep.Stopped);
        Assert.Equal(new Extent(0, Zone, 64), PieceExtent());
        Assert.Equal(restOfF1, Vol.Ext[F1].Skip(1)); // 파일의 나머지는 제자리 (흩어져 있어도 판정에 들지 않음)
        LayoutReport ok = Verify(plan);
        Assert.True(ok.Contiguous);
        Assert.Equal(2, ok.ExpectedRuns); // 구간 하나 + 통째 파일 하나
    }

    [Fact]
    public void ASplitPieceOrAPieceOutsideTheZoneIsNotContiguous()
    {
        PlanFile plan = Whole(Zone);
        _ = Apply(plan, Exec());
        List<Extent> rest = Vol.Ext[F1].Skip(1).ToList();

        Vol.Ext[F1] = [new Extent(0, Zone, 32), new Extent(32, 60000, 32), .. rest]; // 구간이 둘로 쪼개짐
        Assert.False(Verify(plan).Contiguous);

        Vol.Ext[F1] = [new Extent(0, 60000, 64), .. rest]; // 한 조각이지만 zone 밖
        Assert.False(Verify(plan).Contiguous);

        Vol.Ext[F1] = [new Extent(0, Zone, 64), .. rest];
        Assert.True(Verify(plan).Contiguous);
    }

    [Fact]
    public void APieceLargerThanTheWriteCapIsMovedInOneRunAndTheNextFileDoesNotStart()
    {
        PlanFile plan = Whole(Zone);

        ApplyReport rep = Apply(plan, Exec(writeCap: 16 * Cb, chunkBytes: 16 * Cb));

        Assert.Equal((64 * Cb, "write_cap"), (rep.MovedBytes, rep.Stopped)); // 구간 전체 (64 클러스터) 를 한 회에, 쓰기 상한은 그 구간 크기까지 올림
        Assert.Equal(new Extent(0, Zone, 64), PieceExtent());
        Assert.Equal(Files[F2], Vol.Ext[F2]); // 다음 파일은 예산이 없어 시작하지 않음

        ApplyReport next = Apply(plan, Exec());
        Assert.Null(next.Stopped);
        Assert.True(Verify(plan).Contiguous);
    }

    [Fact]
    public void TheResidualPlanKeepsAPieceThatIsAlreadyAtItsTargetAndUsesItAsAPreviousTarget()
    {
        Files[F1] = [new Extent(0, Zone, 64), new Extent(64, 3000, 64), new Extent(128, 2000, 128)];
        Vol.Ext[F1] = [new Extent(0, Zone, 64), new Extent(64, 3000, 64), new Extent(128, 2000, 128)];
        PlanFile plan = Whole(Zone);
        Assert.DoesNotContain(plan.Tasks, t => t.Path == F1); // 구간이 이미 목표 자리: 작업이 없음
        Assert.Contains(new PlanTask(F1, 0, 64, Zone), ResidualPlanner.PreviousTargets(null, [plan]));

        PlanFile residual = ResidualPlanner.Build(plan, ResidualPlanner.PreviousTargets(null, [plan]), p => Vol.Ext[p], n => Vol.Bitmap().FindFreeRun(n, true, 0, PlanGuards.ZoneAlign));

        Assert.Equal(new PlanTask(F1, 0, 64, Zone), Assert.Single(residual.Tasks, t => t.Path == F1)); // 고정 작업: 현재 자리 = 목표
        Assert.Equal(64L, residual.Stats["fixed_clusters"]);
        Assert.Equal(256, Assert.Single(residual.Tasks, t => t.Path == F2).Clusters);
        Assert.False(residual.Stats.ContainsKey(WholeFileLayout.LargeOffsetsKey)); // 옮겨진 zone 기준이라 더는 맞지 않음
        Assert.Equal([(F1, 0L, 64L, (long?)null)], WholeFileLayout.LargePieces(residual));
    }

    [Fact]
    public void AWholePlanResidualReplanLeavesASparseFileThatIsNotInTheWholeListInPlaceButARangePlanStillMovesIt()
    {
        PlanFile whole = PlanFile.Make(Sessions, Root, 2 << 20, Cb, p => Files[p], zoneStartLcn: Zone, capacityBytes: 4L << 20, largePieceMinBytes: Min, sparseFn: p => p == F1);
        Assert.Equal([F2], WholeFileLayout.WholeFiles(whole)); // F1 은 스파스라 큰 조각도 아니고 제자리
        PlanFile ranges = PlanFile.Make(Sessions, Root, 2 << 20, Cb, p => Files[p], zoneStartLcn: Zone, sparseFn: p => p == F1);

        PlanFile wholeResidual = ResidualPlanner.Build(whole, ResidualPlanner.PreviousTargets(null, [whole]), p => Vol.Ext[p], n => Vol.Bitmap().FindFreeRun(n, true, 0, PlanGuards.ZoneAlign));
        PlanFile rangesResidual = ResidualPlanner.Build(ranges, ResidualPlanner.PreviousTargets(null, [ranges]), p => Vol.Ext[p], n => Vol.Bitmap().FindFreeRun(n, true, 0, PlanGuards.ZoneAlign));

        Assert.DoesNotContain(wholeResidual.Tasks, t => t.Path == F1); // 제자리 스파스 파일이 범위 조각으로 zone 에 놓이지 않음
        Assert.Contains(rangesResidual.Tasks, t => t.Path == F1); // 범위 계획의 잔여 재계획은 그대로
    }

    [Fact]
    public void AResidualPlanMovesAPieceThatIsNotAtItsTargetAsOneWholePiece()
    {
        PlanFile plan = Whole(Zone);

        PlanFile residual = ResidualPlanner.Build(plan, ResidualPlanner.PreviousTargets(null, [plan]), p => Vol.Ext[p], n => Vol.Bitmap().FindFreeRun(n, true, 0, PlanGuards.ZoneAlign));

        PlanTask piece = Assert.Single(residual.Tasks, t => t.Path == F1);
        Assert.Equal((0L, 64L), (piece.Vcn, piece.Clusters)); // 구간 밖 (고르지 않은 범위) 은 작업이 없음
    }
}
