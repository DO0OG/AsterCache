using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

/// 통째 배치의 분류 (Classify) 와 zone 배정 (Allocate): 디스크 없이 가짜 크기·extent 로.
public sealed class WholeFileLayoutTests
{
    private const long Cb = 4096;
    private const long Zone = 100000;

    private static PlanRange R(string path, long vcn, long clusters) => new(path, vcn, clusters);

    private static Dictionary<string, long> Sizes(params (string Path, long Clusters)[] files) => files.ToDictionary(f => f.Path, f => f.Clusters * Cb, StringComparer.Ordinal);

    private static List<FileDecision> Classify(IReadOnlyList<PlanRange> order, Dictionary<string, long> sizes, long capacityClusters = 1000,
        Func<string, string?>? reason = null, long maxFileBytes = long.MaxValue) =>
        WholeFileLayout.Classify(order, sizes, Cb, capacityClusters, reason ?? (_ => null), maxFileBytes);

    private static Dictionary<string, IReadOnlyList<Extent>?> Ext(params (string Path, Extent[] Extents)[] files) =>
        files.ToDictionary(f => f.Path, f => (IReadOnlyList<Extent>?)f.Extents, StringComparer.Ordinal);

    [Fact]
    public void AFileAtTwiceTheHotSizeIsWholeAndOneClusterMoreIsMostlyCold()
    {
        List<FileDecision> d = Classify([R("a", 0, 10), R("b", 0, 10)], Sizes(("a", 20), ("b", 21)));

        Assert.Equal(Placement.WholeFile, d[0].Placement);
        Assert.Equal(Placement.InPlace, d[1].Placement);
        Assert.Equal(WholeFileLayout.MostlyColdReason, d[1].Reason);
    }

    [Fact]
    public void ASizeThatIsNotAClusterMultipleCountsRoundedUp()
    {
        Dictionary<string, long> sizes = new() { ["a"] = 20 * Cb + 1 }; // 21 클러스터

        Assert.Equal(WholeFileLayout.MostlyColdReason, Classify([R("a", 0, 10)], sizes)[0].Reason);
    }

    [Fact]
    public void FilesAreOrderedByTheirFirstAccessAndSumTheirHotRanges()
    {
        List<FileDecision> d = Classify([R("b", 0, 4), R("a", 0, 6), R("b", 8, 4)], Sizes(("a", 6), ("b", 16)));

        Assert.Equal(["b", "a"], d.Select(x => x.Path));
        Assert.Equal([0, 1], d.Select(x => x.FirstAccess));
        Assert.Equal(8, d[0].HotClusters);
        Assert.Equal(16, d[0].FileClusters);
        Assert.All(d, x => Assert.Equal(Placement.WholeFile, x.Placement));
    }

    [Fact]
    public void ACallerReasonKeepsTheFileInPlaceAndDoesNotUseTheCapacity()
    {
        List<FileDecision> d = Classify([R("a", 0, 10), R("b", 0, 10)], Sizes(("a", 10), ("b", 10)), capacityClusters: 10,
            p => p == "a" ? WholeFileLayout.SparseHolesReason : null);

        Assert.Equal((Placement.InPlace, WholeFileLayout.SparseHolesReason), (d[0].Placement, d[0].Reason));
        Assert.Equal(Placement.WholeFile, d[1].Placement); // a 가 자리를 쓰지 않아 b 가 들어감
    }

    [Fact]
    public void WhenTheZoneIsFullTheFirstFileThatDoesNotFitAndEverythingAfterItStayInPlace()
    {
        List<FileDecision> d = Classify([R("a", 0, 10), R("b", 0, 10), R("c", 0, 2)], Sizes(("a", 10), ("b", 10), ("c", 2)), capacityClusters: 15);

        Assert.Equal(Placement.WholeFile, d[0].Placement);
        Assert.Equal((Placement.InPlace, WholeFileLayout.ZoneFullReason), (d[1].Placement, d[1].Reason));
        Assert.Equal((Placement.InPlace, WholeFileLayout.ZoneFullReason), (d[2].Placement, d[2].Reason)); // 뒤의 작은 파일도 제자리
    }

    [Fact]
    public void AFileLargerThanTheDailyLimitStaysInPlace()
    {
        List<FileDecision> d = Classify([R("a", 0, 10), R("b", 0, 10)], Sizes(("a", 10), ("b", 12)), maxFileBytes: 10 * Cb);

        Assert.Equal(Placement.WholeFile, d[0].Placement);
        Assert.Equal(WholeFileLayout.OverDailyLimitReason, d[1].Reason);
    }

    [Fact]
    public void AFileWithoutASizeStaysInPlace() =>
        Assert.Equal(WholeFileLayout.UnknownSizeReason, Classify([R("a", 0, 10)], [])[0].Reason);

    private static WholePlan Allocate(IReadOnlyList<PlanRange> order, Dictionary<string, long> sizes, Dictionary<string, IReadOnlyList<Extent>?> ext,
        long? zoneStart = Zone, long fixedClusters = 0, IReadOnlySet<string>? sparse = null, long capacityClusters = 100000)
    {
        List<FileDecision> d = WholeFileLayout.Classify(order, sizes, Cb, capacityClusters, _ => null);
        return WholeFileLayout.Allocate(order, d, ext, zoneStart, Cb, fixedClusters, sparse);
    }

    [Fact]
    public void AFileAlreadyOneRunAtItsTargetNeedsNoTask()
    {
        WholePlan p = Allocate([R("a", 0, 10)], Sizes(("a", 10)), Ext(("a", [new Extent(0, Zone, 10)])));

        Assert.Empty(p.Alloc.Tasks);
        Assert.Equal(10 * Cb, p.Alloc.Stats.InPlaceBytes);
        Assert.Equal(10, p.Alloc.Stats.ZoneUsedClusters);
    }

    [Fact]
    public void AOneRunFileOutsideTheZoneGetsOneTaskForTheWholeFile()
    {
        WholePlan p = Allocate([R("a", 0, 5)], Sizes(("a", 10)), Ext(("a", [new Extent(0, 500, 10)])));

        Assert.Equal([new PlanTask("a", 0, 10, Zone)], p.Alloc.Tasks);
        Assert.Equal(10 * Cb, p.Alloc.Stats.MovedBytes);
    }

    [Fact]
    public void AFileInSeveralPiecesGetsOneTaskWithSeveralSources()
    {
        Dictionary<string, IReadOnlyList<Extent>?> ext = Ext(("a", [new Extent(0, 500, 4), new Extent(4, 900, 3), new Extent(7, 300, 3)]));

        WholePlan p = Allocate([R("a", 0, 5)], Sizes(("a", 10)), ext);

        Assert.Equal([new PlanTask("a", 0, 10, Zone)], p.Alloc.Tasks);
        Assert.Equal((2, 1), (p.Alloc.Stats.CurrentRuns, p.Alloc.Stats.ExpectedRuns)); // 핫 범위는 두 조각에 걸쳐 있고, 통째로 놓으면 파일 하나
    }

    [Fact]
    public void APieceAlreadyAtItsTargetIsLeftOutAndTheRestStaysSeparateTasks()
    {
        // 목표 [Zone, Zone + 10): 가운데 조각 (VCN 4..7) 이 이미 제자리
        Dictionary<string, IReadOnlyList<Extent>?> ext = Ext(("a", [new Extent(0, 500, 4), new Extent(4, Zone + 4, 3), new Extent(7, 300, 3)]));

        WholePlan p = Allocate([R("a", 0, 5)], Sizes(("a", 10)), ext);

        Assert.Equal([new PlanTask("a", 0, 4, Zone), new PlanTask("a", 7, 3, Zone + 7)], p.Alloc.Tasks);
        Assert.Equal(3 * Cb, p.Alloc.Stats.InPlaceBytes);
    }

    [Fact]
    public void FilesAreAppendedInFirstAccessOrderAndTheOrderIsPassedThrough()
    {
        PlanRange[] order = [R("b", 0, 5), R("a", 0, 3), R("b", 5, 5)];
        Dictionary<string, IReadOnlyList<Extent>?> ext = Ext(("a", [new Extent(0, 700, 6)]), ("b", [new Extent(0, 500, 10)]));

        WholePlan p = Allocate(order, Sizes(("a", 6), ("b", 10)), ext);

        Assert.Equal([new PlanTask("b", 0, 10, Zone), new PlanTask("a", 0, 6, Zone + 10)], p.Alloc.Tasks);
        Assert.Equal(order, p.Alloc.Order);
        Assert.Equal(16, p.Alloc.Stats.ZoneUsedClusters);
        Assert.Equal(2, p.Alloc.Stats.ExpectedRuns);
        Assert.Equal(13 * Cb, p.Alloc.Stats.HotBytes); // 핫 범위 5 + 3 + 5 클러스터
    }

    [Fact]
    public void AFilePartlyOnAnotherTasksTargetIsOrderedAfterThatTasksMove()
    {
        // a 의 목표 [Zone, Zone + 10) 를 지금 b 가 차지함: b 가 먼저 옮겨져야 함
        Dictionary<string, IReadOnlyList<Extent>?> ext = Ext(("a", [new Extent(0, 500, 10)]), ("b", [new Extent(0, Zone, 10)]));

        WholePlan p = Allocate([R("a", 0, 10), R("b", 0, 10)], Sizes(("a", 10), ("b", 10)), ext);

        Assert.Equal(["b", "a"], p.Alloc.Tasks.Select(t => t.Path));
    }

    [Fact]
    public void ARelativeZoneGivesTargetsAsOffsets()
    {
        WholePlan p = Allocate([R("a", 0, 10), R("b", 0, 10)], Sizes(("a", 10), ("b", 10)),
            Ext(("a", [new Extent(0, 500, 10)]), ("b", [new Extent(0, 900, 10)])), zoneStart: null);

        Assert.Equal([0L, 10L], p.Alloc.Tasks.Select(t => t.TargetLcn));
    }

    [Fact]
    public void InAnExtendedZoneAOneRunFileInsideTheKeptPartStaysAndNewFilesGoAfterIt()
    {
        Dictionary<string, IReadOnlyList<Extent>?> ext = Ext(("a", [new Extent(0, Zone + 5, 10)]), ("b", [new Extent(0, 900, 6)]));

        WholePlan p = Allocate([R("a", 0, 10), R("b", 0, 6)], Sizes(("a", 10), ("b", 6)), ext, fixedClusters: 20);

        Assert.Equal([new PlanTask("b", 0, 6, Zone + 20)], p.Alloc.Tasks);
        Assert.Equal(26, p.Alloc.Stats.ZoneUsedClusters);
    }

    [Fact]
    public void AFileWithAHoleIsDowngradedToInPlace()
    {
        Dictionary<string, IReadOnlyList<Extent>?> ext = Ext(("a", [new Extent(0, 500, 4), new Extent(6, 600, 4)])); // VCN 4..6 구멍

        WholePlan p = Allocate([R("a", 0, 10)], Sizes(("a", 10)), ext);

        Assert.Empty(p.Alloc.Tasks);
        Assert.Equal((Placement.InPlace, WholeFileLayout.SparseHolesReason), (p.Decisions[0].Placement, p.Decisions[0].Reason));
        Assert.Equal(0, p.Alloc.Stats.ZoneUsedClusters);
    }

    [Fact]
    public void ASparseFileWhoseLengthIsNotASixteenMultipleIsSplitAtTheTail()
    {
        Dictionary<string, IReadOnlyList<Extent>?> ext = Ext(("s", [new Extent(0, 500, 40)]));
        HashSet<string> sparse = ["s"];

        WholePlan p = Allocate([R("s", 0, 32)], Sizes(("s", 40)), ext, sparse: sparse);

        Assert.Equal([new PlanTask("s", 0, 32, Zone), new PlanTask("s", 32, 8, Zone + 32)], p.Alloc.Tasks);
        Assert.All(p.Alloc.Tasks, t => Assert.True(SparseAllocation.SupportsMove(t.Vcn, t.Clusters, 40)));
    }

    [Fact]
    public void TheStatsMarkWholeFilesInFirstAccessOrderAndCountInPlaceReasons()
    {
        List<FileDecision> d = Classify([R("b", 0, 10), R("a", 0, 1), R("c", 0, 10)], Sizes(("a", 50), ("b", 10), ("c", 10)), capacityClusters: 10);
        var stats = new AsterCache.Core.Json.JObject();

        WholeFileLayout.AddStats(stats, d);

        Assert.Equal(WholeFileLayout.RuleVersion, stats[WholeFileLayout.RuleKey]);
        Assert.Equal(new List<object?> { "b" }, stats[WholeFileLayout.WholeFilesKey]);
        var reasons = Assert.IsType<AsterCache.Core.Json.JObject>(stats[WholeFileLayout.InPlaceFilesKey]);
        Assert.Equal(1L, reasons[WholeFileLayout.MostlyColdReason]);
        Assert.Equal(1L, reasons[WholeFileLayout.ZoneFullReason]);
    }

    [Theory]
    [InlineData("whole_file", true)]
    [InlineData("ranges", true)]
    [InlineData("WHOLE_FILE", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheTwoLayoutNamesAreValid(string? value, bool valid) => Assert.Equal(valid, RelocateLayouts.IsValid(value));
}
