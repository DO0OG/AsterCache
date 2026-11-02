using AsterCache.Core.Model;
using AsterCache.Core.Prefetch;
using Xunit;

namespace AsterCache.Tests.Core;

/// tests/test_prefetch.py SchedulerTests 포팅.
public sealed class SchedulerTests
{
    private const int Cs = 4096;
    private const long Mib = Scheduler.Mib;

    // 가짜 파일: 파일 클러스터 0-9 는 디스크 1000.., 10-19 는 디스크 100..
    private static readonly Extent[] Ext = { new(0, 1000, 10), new(10, 100, 10) };

    private static readonly Dictionary<string, FileLayoutInfo> Lay = new()
    {
        ["A"] = new FileLayoutInfo(Ext, Cs),
        ["B"] = new FileLayoutInfo(new[] { new Extent(0, 500, 20) }, Cs),
    };

    private static ReadRange Rng(string path, long off, long n) => new(path, off, n);

    private static FileLayoutInfo? LayoutOf(string path) => Lay.GetValueOrDefault(path);

    private static List<ReadOp> Ops(params (string, long, long)[] ops) => ops.Select(o => new ReadOp(o.Item1, o.Item2, o.Item3)).ToList();

    [Fact]
    public void PhysicalKeyMapsOffsets()
    {
        Assert.Equal(1000L, FileLayoutInfo.PhysicalKey(Ext, 0, Cs));
        Assert.Equal(102L, FileLayoutInfo.PhysicalKey(Ext, (12 * Cs) + 5, Cs));
        Assert.Null(FileLayoutInfo.PhysicalKey(Ext, 99 * Cs, Cs));
        Assert.Null(FileLayoutInfo.PhysicalKey(null, 0, Cs));
    }

    [Fact]
    public void PhysicalSortsByDiskPositionAndSplitsAtExtent()
    {
        (List<ReadOp> ops, ScheduleInfo info) = Scheduler.Schedule(
            new[] { Rng("A", 0, Cs), Rng("B", 0, Cs), Rng("A", 15 * Cs, Cs) }, "physical", gapBytes: 0, layoutFn: LayoutOf);
        Assert.Equal(Ops(("A", 15 * Cs, Cs), ("B", 0, Cs), ("A", 0, Cs)), ops);
        Assert.False(info.Fallback);

        (ops, _) = Scheduler.Schedule(new[] { Rng("A", 8 * Cs, 4 * Cs) }, "physical", gapBytes: 0, layoutFn: LayoutOf);
        Assert.Equal(Ops(("A", 10 * Cs, 2 * Cs), ("A", 8 * Cs, 2 * Cs)), ops); // 디스크 100 이 1008 보다 앞
    }

    [Fact]
    public void MergeWithinGapAndNotBeyond()
    {
        ReadRange[] rs = { Rng("B", 0, Cs), Rng("B", 3 * Cs, Cs), Rng("B", 15 * Cs, Cs) };
        (List<ReadOp> ops, _) = Scheduler.Schedule(rs, "physical", gapBytes: 2 * Cs, layoutFn: LayoutOf);
        Assert.Equal(Ops(("B", 0, 4 * Cs), ("B", 15 * Cs, Cs)), ops);
        (ops, _) = Scheduler.Schedule(rs, "physical", gapBytes: 0, layoutFn: LayoutOf);
        Assert.Equal(3, ops.Count);
    }

    [Fact]
    public void OverlapMerges()
    {
        (List<ReadOp> ops, _) = Scheduler.Schedule(new[] { Rng("B", 0, 2 * Cs), Rng("B", Cs, 2 * Cs) }, "path", gapBytes: 0);
        Assert.Equal(Ops(("B", 0, 3 * Cs)), ops);
    }

    [Fact]
    public void UnknownLayoutSortsLastAndFlagsFallback()
    {
        (List<ReadOp> ops, ScheduleInfo info) = Scheduler.Schedule(
            new[] { Rng("Z", 0, Cs), Rng("B", 0, Cs) }, "physical", gapBytes: 0, layoutFn: LayoutOf);
        Assert.Equal(Ops(("B", 0, Cs), ("Z", 0, Cs)), ops);
        Assert.True(info.Fallback);
    }

    [Fact]
    public void PathAndNoneStrategies()
    {
        ReadRange[] rs = { Rng("b", 5, 10), Rng("a", 50, 10), Rng("a", 0, 10) };
        Assert.Equal(Ops(("b", 5, 10), ("a", 50, 10), ("a", 0, 10)), Scheduler.Schedule(rs, "none").Ops);
        Assert.Equal(Ops(("a", 0, 10), ("a", 50, 10), ("b", 5, 10)), Scheduler.Schedule(rs, "path", gapBytes: 0).Ops);
    }

    [Fact]
    public void BudgetDropsLowScoreThenTail()
    {
        ReadRange[] rs = { Rng("a", 0, 100) with { Score = 1 }, Rng("a", 1000, 100) with { Score = 9 }, Rng("a", 2000, 100) with { Score = 5 } };
        (List<ReadOp> ops, ScheduleInfo info) = Scheduler.Schedule(rs, "path", budgetBytes: 200, gapBytes: 0);
        Assert.Equal(Ops(("a", 1000, 100), ("a", 2000, 100)), ops);
        Assert.Equal(200, info.SelectedBytes);
        (ops, _) = Scheduler.Schedule(new[] { Rng("a", 0, 100), Rng("a", 1000, 100), Rng("a", 2000, 100) }, "none", budgetBytes: 250);
        Assert.Equal(Ops(("a", 0, 100), ("a", 1000, 100)), ops);
    }

    [Fact]
    public void BreakevenGap()
    {
        Assert.Equal(1_200_000, Scheduler.BreakevenGap());
        Assert.Equal(2_000_000, Scheduler.BreakevenGap(200, 10));
        (List<ReadOp> ops, ScheduleInfo info) = Scheduler.Schedule(new[] { Rng("a", 0, 10), Rng("a", 1_000_000, 10) }, "path"); // 기본 간격이 1MB 구멍을 합침
        Assert.Equal(Ops(("a", 0, 1_000_010)), ops);
        Assert.Equal(1_200_000, info.GapBytes);
    }

    [Fact]
    public void FullFileWhenDenseEnoughAndBudgetAllows()
    {
        ReadRange[] rs = { Rng("a", 0, 400), Rng("a", 600, 100), Rng("b", 0, 100) }; // a: 1000 중 50%, b: 10%
        (List<ReadOp> ops, ScheduleInfo info) = Scheduler.Schedule(rs, "path", gapBytes: 0, sizeFn: _ => 1000);
        Assert.Equal(Ops(("a", 0, 1000), ("b", 0, 100)), ops);
        Assert.Equal((1, 1100L), (info.FullFiles, info.SelectedBytes));

        (ops, info) = Scheduler.Schedule(rs, "path", budgetBytes: 700, gapBytes: 0, sizeFn: _ => 1000); // 자리 없음
        Assert.Equal((0, 3), (info.FullFiles, ops.Count));

        (ops, _) = Scheduler.Schedule(rs, "path", gapBytes: 0, sizeFn: _ => 1000, fullFileRatio: 2); // 꺼짐
        Assert.Equal(3, ops.Count);
    }

    [Fact]
    public void BadStrategy()
    {
        Assert.Throws<ArgumentException>(() => Scheduler.Schedule(Array.Empty<ReadRange>(), "bogus"));
    }

    [Fact]
    public void MibConstant()
    {
        Assert.Equal(1 << 20, Mib);
    }

    [Fact]
    public void HeadBytesPutsTheAccessOrderPrefixFirstThenPhysicalOrder()
    {
        // 접근 순서: A@0(디스크 1000대), B@0(500대), A@15(100대). 물리 순서만이면 A15, B0, A0.
        ReadRange[] rs = { Rng("A", 0, Cs), Rng("B", 0, Cs), Rng("A", 15 * Cs, Cs) };

        (List<ReadOp> ops, ScheduleInfo info) = Scheduler.Schedule(rs, "physical", gapBytes: 0, layoutFn: LayoutOf, headBytes: Cs);

        Assert.Equal(Ops(("A", 0, Cs), ("A", 15 * Cs, Cs), ("B", 0, Cs)), ops); // 앞 4 KiB 는 접근 순서, 나머지는 물리 순서
        Assert.Equal(3L * Cs, info.SelectedBytes);
    }

    [Fact]
    public void HeadBytesTakesWholeRangesUntilTheThresholdIsReached()
    {
        ReadRange[] rs = { Rng("B", 10 * Cs, Cs), Rng("A", 0, Cs), Rng("B", 0, Cs) };

        (List<ReadOp> ops, _) = Scheduler.Schedule(rs, "physical", gapBytes: 0, layoutFn: LayoutOf, headBytes: (2 * Cs) - 1);

        Assert.Equal(Ops(("B", 10 * Cs, Cs), ("A", 0, Cs), ("B", 0, Cs)), ops); // 두 범위로 기준을 넘기고 세 번째가 나머지
    }

    [Fact]
    public void HeadBytesBiggerThanEverythingKeepsAccessOrderAndZeroChangesNothing()
    {
        ReadRange[] rs = { Rng("A", 0, Cs), Rng("B", 0, Cs), Rng("A", 15 * Cs, Cs) };
        (List<ReadOp> plain, _) = Scheduler.Schedule(rs, "physical", gapBytes: 0, layoutFn: LayoutOf);

        (List<ReadOp> all, _) = Scheduler.Schedule(rs, "physical", gapBytes: 0, layoutFn: LayoutOf, headBytes: 256 * Mib);
        (List<ReadOp> zero, _) = Scheduler.Schedule(rs, "physical", gapBytes: 0, layoutFn: LayoutOf, headBytes: 0);

        Assert.Equal(Ops(("A", 0, Cs), ("B", 0, Cs), ("A", 15 * Cs, Cs)), all);
        Assert.Equal(plain, zero);
    }

    [Fact]
    public void PhysicalOrderIsNotAccessOrderByDefault()
    {
        (List<ReadOp> ops, _) = Scheduler.Schedule(
            new[] { Rng("A", 0, Cs), Rng("A", 15 * Cs, Cs) }, "physical", gapBytes: 0, layoutFn: LayoutOf);

        Assert.Equal(Ops(("A", 15 * Cs, Cs), ("A", 0, Cs)), ops);
    }
}
