using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Prefetch;
using Xunit;

namespace AsterCache.Tests.Plan.Orbit;

public sealed class OrbitSegmentsTests
{
    private const long MiB = 1L << 20;

    private static List<ReadOp> Many(int n, long len = MiB)
    {
        var list = new List<ReadOp>();
        for (int i = 0; i < n; i++)
        {
            list.Add(new ReadOp("f" + (i % 7), i * len, len));
        }

        return list;
    }

    [Fact]
    public void Reorder_InvalidOrNullOrIdentity_ReturnsSameReference()
    {
        var ops = Many(40);
        var set = OrbitSegments.Build(ops, 4 * MiB, 8 * MiB);
        Assert.True(set.Segments.Count >= 3);
        int k = set.Segments.Count;
        var identity = Enumerable.Range(0, k).ToArray();
        var dup = Enumerable.Range(0, k).ToArray();
        dup[1] = 0;
        var outOfRange = Enumerable.Range(0, k).Reverse().ToArray();
        outOfRange[0] = k;
        var other = OrbitSegments.Build(Many(40), 4 * MiB, 8 * MiB);

        Assert.Same(ops, OrbitApply.Reorder(ops, set, null));
        Assert.Same(ops, OrbitApply.Reorder(ops, set, identity));
        Assert.Same(ops, OrbitApply.Reorder(ops, set, identity.Take(k - 1).ToArray()));
        Assert.Same(ops, OrbitApply.Reorder(ops, set, dup));
        Assert.Same(ops, OrbitApply.Reorder(ops, set, outOfRange));
        Assert.Same(ops, OrbitApply.Reorder(ops, other, identity.Reverse().ToArray()));
        Assert.Equal(Many(40), ops);
    }

    [Fact]
    public void Reorder_Permutation_KeepsHeadAndMultiset()
    {
        var ops = Many(40);
        var snapshot = ops.ToList();
        var set = OrbitSegments.Build(ops, 4 * MiB, 8 * MiB);
        var order = Enumerable.Range(0, set.Segments.Count).Reverse().ToArray();

        var result = OrbitApply.Reorder(ops, set, order);

        Assert.NotSame(ops, result);
        Assert.Equal(snapshot, ops);
        Assert.Equal(snapshot.Count, result.Count);
        Assert.Equal(snapshot.OrderBy(o => o.Offset), result.OrderBy(o => o.Offset));
        Assert.Equal(snapshot.Take(set.HeadCount), result.Take(set.HeadCount));
        int pos = set.HeadCount;
        foreach (var index in order)
        {
            var seg = set.Segments[index];
            Assert.Equal(snapshot.Skip(seg.Start).Take(seg.Count), result.Skip(pos).Take(seg.Count));
            pos += seg.Count;
        }
    }

    [Fact]
    public void Build_HugeList_CapsSegmentsAndCoversTail()
    {
        var ops = Many(5000);
        var set = OrbitSegments.Build(ops, 64 * MiB);

        Assert.True(set.Segments.Count <= 23);
        Assert.Equal(64, set.HeadCount);
        int next = set.HeadCount;
        long bytes = 0;
        foreach (var seg in set.Segments)
        {
            Assert.Equal(next, seg.Start);
            next += seg.Count;
            bytes += seg.Bytes;
        }

        Assert.Equal(ops.Count, next);
        Assert.Equal(5000 * MiB - set.HeadBytes, bytes);
    }

    [Fact]
    public void Build_HeadCoversAll_NoSegments()
    {
        var ops = Many(5);
        var set = OrbitSegments.Build(ops, 100 * MiB);
        Assert.Empty(set.Segments);
        Assert.Equal(5, set.HeadCount);
        Assert.Same(ops, OrbitApply.Reorder(ops, set, Array.Empty<int>()));

        var empty = new List<ReadOp>();
        var emptySet = OrbitSegments.Build(empty, 10);
        Assert.Empty(emptySet.Segments);
        Assert.Same(empty, OrbitApply.Reorder(empty, emptySet, new[] { 0 }));
    }

    [Fact]
    public void Build_SmallExample_PositionAndGaps()
    {
        var ops = new List<ReadOp>
        {
            new("a", 0, 10), // 접두
            new("a", 10, 10),
            new("a", 20, 10),
            new("a", 100, 10), // 간격
            new("b", 0, 10), // 파일 바뀜
        };
        var set = OrbitSegments.Build(ops, 10, 20);

        Assert.Equal(1, set.HeadCount);
        Assert.Equal(10, set.HeadBytes);
        Assert.Equal(new[] { new OrbitSegment(1, 2, 20, 10, 0), new OrbitSegment(3, 2, 20, 30, 1) }, set.Segments);
    }

    [Fact]
    public void Build_InnerOffsetGap_CountsGapInsideSegment()
    {
        var ops = new List<ReadOp>
        {
            new("a", 0, 10), // 접두
            new("a", 10, 10),
            new("a", 30, 10), // 간격
            new("a", 40, 10),
        };
        var set = OrbitSegments.Build(ops, 10, 100);

        Assert.Equal(new[] { new OrbitSegment(1, 3, 30, 10, 1) }, set.Segments);
    }

    [Fact]
    public void Build_ZeroAndNegativeLengths_KeepPositionAndMultiset()
    {
        var ops = new List<ReadOp>
        {
            new("a", 0, 10), // 접두
            new("a", 10, 10),
            new("a", 20, 0),
            new("b", 0, -5),
            new("a", 20, 10),
            new("a", 30, 10),
            new("a", 40, 0),
            new("a", 50, 10),
            new("a", 60, 10),
            new("a", 70, 0),
            new("a", 80, 0), // 끝의 영 길이 꼬리
        };
        var snapshot = ops.ToList();
        var set = OrbitSegments.Build(ops, 10, 20);

        Assert.Equal(new[]
        {
            new OrbitSegment(1, 4, 20, 10, 2),
            new OrbitSegment(5, 3, 20, 30, 1),
            new OrbitSegment(8, 3, 10, 50, 1),
        }, set.Segments);

        var result = OrbitApply.Reorder(ops, set, new[] { 2, 0, 1 });
        var expected = snapshot.Take(1)
            .Concat(snapshot.Skip(8).Take(3))
            .Concat(snapshot.Skip(1).Take(4))
            .Concat(snapshot.Skip(5).Take(3))
            .ToList();

        Assert.NotSame(ops, result);
        Assert.Equal(snapshot, ops);
        Assert.Equal(expected, result);
        Assert.Equal(
            snapshot.OrderBy(o => o.Path).ThenBy(o => o.Offset).ThenBy(o => o.Length),
            result.OrderBy(o => o.Path).ThenBy(o => o.Offset).ThenBy(o => o.Length));
    }

    [Fact]
    public void Reorder_ListShrunkAfterBuild_ReturnsSameReference()
    {
        var ops = Many(40);
        var set = OrbitSegments.Build(ops, 4 * MiB, 8 * MiB);
        var order = Enumerable.Range(0, set.Segments.Count).Reverse().ToArray();
        ops.RemoveAt(ops.Count - 1);

        Assert.Same(ops, OrbitApply.Reorder(ops, set, order));
    }

    [Fact]
    public void Build_HeadBoundaryInsideOp_StraddlingOpGoesToHead()
    {
        var ops = new List<ReadOp>
        {
            new("a", 0, 10),
            new("a", 10, 10), // 접두 경계를 걸침
            new("a", 20, 10),
            new("a", 30, 10),
        };
        var set = OrbitSegments.Build(ops, 15, 20);

        Assert.Equal(2, set.HeadCount);
        Assert.Equal(20, set.HeadBytes);
        Assert.Equal(new[] { new OrbitSegment(2, 2, 20, 20, 0) }, set.Segments);
        Assert.Same(ops, OrbitApply.Reorder(ops, set, new[] { 0 }));
    }
}
