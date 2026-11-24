using AsterCache.Core.Model;
using AsterCache.Core.Prefetch;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 붙들 목록을 채우는 묶음: 물리 위치 (LCN) 순으로 놓고 맨 앞의 물리적으로 이어진 한 구간만 상한 안에서 떼는 규칙을 시험합니다.
public sealed class HoldFillBatchesTests
{
    private const long Mib = 1L << 20;
    private const int Cluster = 4096;

    private static ReadOp Op(string file, long offsetMib, long lengthMib) => new(file, offsetMib * Mib, lengthMib * Mib);

    // 파일 전체가 LCN lcnMib 부터 이어진 extent 하나인 배치
    private static FileLayoutInfo Whole(long sizeMib, long lcnMib) => new([new Extent(0, lcnMib * Mib / Cluster, sizeMib * Mib / Cluster)], Cluster);

    [Fact]
    public void RangesAreOrderedByPhysicalPositionAndRangesWithoutALayoutComeLastInTheirOwnOrder()
    {
        var layouts = new Dictionary<string, FileLayoutInfo> { ["a"] = Whole(16, 500), ["b"] = Whole(16, 100) };

        List<HoldFillBatches.Piece> ordered = HoldFillBatches.Order([Op("a", 0, 4), Op("x", 0, 4), Op("b", 0, 4), Op("y", 0, 2)], p => layouts.GetValueOrDefault(p));

        Assert.Equal(["b", "a", "x", "y"], ordered.Select(p => p.Op.Path));
        Assert.Equal([100 * Mib, 500 * Mib], ordered.Take(2).Select(p => p.Lcn!.Value));
        Assert.All(ordered.Skip(2), p => Assert.Null(p.Lcn));
    }

    [Fact]
    public void ARangeIsSplitAtTheExtentBoundariesAndAHoleBecomesAnUnknownPiece()
    {
        // 파일: 0~4 MiB 는 LCN 200 MiB, 4~8 MiB 는 구멍, 8~12 MiB 는 LCN 100 MiB
        var layout = new FileLayoutInfo([new Extent(0, 200 * Mib / Cluster, 4 * Mib / Cluster), new Extent(8 * Mib / Cluster, 100 * Mib / Cluster, 4 * Mib / Cluster)], Cluster);

        List<HoldFillBatches.Piece> ordered = HoldFillBatches.Order([Op("f", 0, 12)], _ => layout);

        Assert.Equal([(8L, 4L), (0L, 4L), (4L, 4L)], ordered.Select(p => (p.Op.Offset / Mib, p.Op.Length / Mib))); // LCN 100 → 200 → 구멍 (위치 모름)
        Assert.Null(ordered[2].Lcn);
    }

    [Fact]
    public void AContiguousRunLongerThanTheCapIsCutAtTheCapAndTheRestStaysAtTheFront()
    {
        // 파일 a (16 MiB) 바로 뒤에 파일 b 가 물리적으로 이어짐
        var layouts = new Dictionary<string, FileLayoutInfo> { ["a"] = Whole(16, 100), ["b"] = Whole(16, 116) };
        List<HoldFillBatches.Piece> ordered = HoldFillBatches.Order([Op("a", 0, 16), Op("b", 0, 16)], p => layouts[p]);

        List<ReadOp> run = HoldFillBatches.TakeRun(ordered, 8 * Mib);

        Assert.Equal([Op("a", 0, 8)], run);
        Assert.Equal(Op("a", 8, 8), ordered[0].Op); // 나머지가 맨 앞에 남고 위치도 그만큼 앞으로
        Assert.Equal((108 * Mib), ordered[0].Lcn);
        Assert.Equal([Op("a", 8, 8), Op("b", 0, 8)], HoldFillBatches.TakeRun(ordered, 16 * Mib).Take(2)); // 다음 묶음은 a 의 나머지와 이어진 b 의 앞부분
    }

    [Fact]
    public void AContiguousRunShorterThanTheCapStopsWhereThePhysicalPositionJumps()
    {
        var layouts = new Dictionary<string, FileLayoutInfo> { ["a"] = Whole(2, 100), ["b"] = Whole(2, 102), ["c"] = Whole(2, 900) };
        List<HoldFillBatches.Piece> ordered = HoldFillBatches.Order([Op("c", 0, 2), Op("a", 0, 2), Op("b", 0, 2)], p => layouts[p]);

        List<ReadOp> run = HoldFillBatches.TakeRun(ordered, 8 * Mib);

        Assert.Equal([Op("a", 0, 2), Op("b", 0, 2)], run); // a 와 b 는 이어지고 c 는 떨어져 있음
        Assert.Equal([Op("c", 0, 2)], HoldFillBatches.TakeRun(ordered, 8 * Mib));
        Assert.Empty(ordered);
    }

    [Fact]
    public void ScatteredRangesMakeOneRunEachNeverMixingDistantPositions()
    {
        var layouts = new Dictionary<string, FileLayoutInfo> { ["a"] = Whole(4, 100), ["b"] = Whole(4, 300), ["c"] = Whole(4, 500) };
        List<HoldFillBatches.Piece> ordered = HoldFillBatches.Order([Op("c", 0, 4), Op("a", 0, 4), Op("b", 0, 4)], p => layouts[p]);

        List<List<ReadOp>> runs = [HoldFillBatches.TakeRun(ordered, 8 * Mib), HoldFillBatches.TakeRun(ordered, 8 * Mib), HoldFillBatches.TakeRun(ordered, 8 * Mib)];

        Assert.Equal(["a", "b", "c"], runs.Select(r => Assert.Single(r).Path));
    }

    [Fact]
    public void ARangeWhosePositionIsUnknownIsCutAtOneMiBEvenWhenTheCapIsLarger()
    {
        List<HoldFillBatches.Piece> ordered = HoldFillBatches.Order([Op("x", 0, 4)], _ => null);

        List<ReadOp> run = HoldFillBatches.TakeRun(ordered, 8 * Mib);

        Assert.Equal([Op("x", 0, 1)], run);
        Assert.Equal(Op("x", 1, 3), ordered[0].Op);
        Assert.Equal([Op("x", 1, 1)], HoldFillBatches.TakeRun(ordered, Mib / 2 + (Mib / 2))); // 상한이 1 MiB 이하면 그 상한
    }

    [Fact]
    public void ARunNeverPassesTheGivenCapAndAZeroCapTakesNothing()
    {
        var layouts = new Dictionary<string, FileLayoutInfo> { ["a"] = Whole(16, 100) };
        List<HoldFillBatches.Piece> ordered = HoldFillBatches.Order([Op("a", 0, 16)], p => layouts[p]);

        Assert.Empty(HoldFillBatches.TakeRun(ordered, 0));
        Assert.Equal(Mib, HoldFillBatches.TakeRun(ordered, Mib).Sum(o => o.Length));
        Assert.Equal(10 * Mib, HoldFillBatches.TakeRun(ordered, 10 * Mib).Sum(o => o.Length)); // 예산이 4~15 MiB 남았을 때 그만큼만
        Assert.Equal(5 * Mib, ordered.Sum(p => p.Op.Length));
    }

    [Fact]
    public void TheBatchLimitsAreEightMiBDuringAnExecutableStartLoadingAndOneMiBOtherwise()
    {
        Assert.Equal(8 * Mib, HoldFillBatches.LoadingBatchBytes);
        Assert.Equal(1 * Mib, HoldFillBatches.PlayBatchBytes);
    }
}
