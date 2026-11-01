using AsterCache.Core.Model;
using AsterCache.Core.Prefetch;
using AsterCache.Prefetch;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Prefetch;

/// tests/test_prefetch.py IoTests (reader 부분) 포팅. 두 방식(Pvm 기본, Read 폴백) 모두.
public sealed class ZoneStreamerTests
{
    private const long Mib = 1 << 20;

    public static TheoryData<StreamMethod, int> Variants => new()
    {
        { StreamMethod.Pvm, 1 << 20 },
        { StreamMethod.Read, 1 << 20 },
        { StreamMethod.Read, 4096 },
        { StreamMethod.Read, 8 << 20 },
    };

    [Theory]
    [MemberData(nameof(Variants))]
    public void BytesEqualRequestSum(StreamMethod method, int chunk)
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", (int)(3 * Mib));
        string f = tmp.Combine("f.bin");
        ReadOp[] ops = { new(f, 0, 100000), new(f, Mib, 70000), new(f, (2 * Mib) + 5, 12345) };

        StreamStats r = ZoneStreamer.Run(ops, method, chunk);

        Assert.Equal(ops.Sum(o => o.Length), r.Bytes);
        Assert.Equal((3L, 0L), (r.Requests, r.Errors));
    }

    [Theory]
    [InlineData(StreamMethod.Pvm)]
    [InlineData(StreamMethod.Read)]
    public void LengthIsClampedToFileSizeAndMissingFileCountsError(StreamMethod method)
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", 10_000);

        StreamStats clamped = ZoneStreamer.Run(new[] { new ReadOp(tmp.Combine("f.bin"), 4000, 1_000_000) }, method);
        StreamStats missing = ZoneStreamer.Run(new[] { new ReadOp(tmp.Combine("nope"), 0, 10) }, method);

        Assert.Equal((6000L, 0L), (clamped.Bytes, clamped.Errors));
        Assert.Equal((0L, 1L), (missing.Bytes, missing.Errors));
    }

    [Theory]
    [InlineData(StreamMethod.Pvm)]
    [InlineData(StreamMethod.Read)]
    public void ReaderDoesNotModifyTheFile(StreamMethod method)
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", (int)(3 * Mib));
        string f = tmp.Combine("f.bin");
        byte[] before = File.ReadAllBytes(f);

        ZoneStreamer.Run(new[] { new ReadOp(f, 0, 3 * Mib) }, method);

        Assert.Equal(before, File.ReadAllBytes(f));
    }

    [Fact]
    public void ReadMethodKeepsGoingAfterAnErrorAndZeroOpsIsEmpty()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", 5000);
        ReadOp[] ops = { new(tmp.Combine("nope"), 0, 10), new(tmp.Combine("f.bin"), 0, 5000) };

        StreamStats r = ZoneStreamer.Run(ops, StreamMethod.Read);

        Assert.Equal((5000L, 1L, 1L), (r.Bytes, r.Requests, r.Errors));
        StreamStats empty = ZoneStreamer.Run(Array.Empty<ReadOp>());
        Assert.Equal((0L, 0L, 0L), (empty.Bytes, empty.Requests, empty.Errors));
    }

    [Fact]
    public void ColdOpsPicksOnlyExtentsOutsideTheHotLcnSpan()
    {
        const int cs = 4096;
        // 핫 작업은 LCN 1000..1009. a 의 두 번째 extent(LCN 5000..)와 b 전체(LCN 9000..)가 콜드 후보, a 의 첫 extent 는 제외.
        var layouts = new Dictionary<string, FileLayoutInfo?>
        {
            ["a"] = new FileLayoutInfo(new[] { new Extent(0, 1000, 10), new Extent(10, 5000, 600) }, cs),
            ["b"] = new FileLayoutInfo(new[] { new Extent(0, 9000, 300) }, cs),
            ["c"] = null,
        };
        ReadOp[] hot = { new("a", 0, 10 * cs), new("c", 0, 100) };

        List<ReadOp> cold = ColdMaker.ColdOps(hot, 3, p => layouts[p]);
        List<ReadOp> coldWithB = ColdMaker.ColdOps(new ReadOp[] { new("a", 0, 10 * cs), new("b", 0, cs) }, 3, p => layouts[p]);

        // 600 클러스터 * 4096 = 2.34 MiB: 1 MiB, 1 MiB, 0.34 MiB 조각, 예산 3 MiB 안.
        Assert.Equal(3, cold.Count);
        Assert.All(cold, o => Assert.Equal("a", o.Path));
        Assert.Equal(new long[] { 10 * cs, (10 * cs) + Mib, (10 * cs) + (2 * Mib) }, cold.Select(o => o.Offset).ToArray());
        Assert.Equal(600L * cs, cold.Sum(o => o.Length));
        // b 도 핫이면 LCN 범위가 1000..9000 이라 a 의 5000 extent 는 범위 안 -> 제외.
        Assert.Empty(coldWithB);
    }

    [Fact]
    public void ColdOpsStopsAtTheBudget()
    {
        const int cs = 4096;
        var layout = new FileLayoutInfo(new[] { new Extent(0, 100, 10), new Extent(10, 90_000, 2000) }, cs);

        List<ReadOp> cold = ColdMaker.ColdOps(new ReadOp[] { new("a", 0, cs) }, 2, _ => layout);

        Assert.Equal(2 * Mib, cold.Sum(o => o.Length));
    }

    [Fact]
    public void MakeColdReadsTheOpsUnbuffered()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", (int)(3 * Mib));
        string f = tmp.Combine("f.bin");
        ReadOp[] hot = { new(f, 0, 100_000), new(f, 2 * Mib, 5000) };
        ReadOp[] flush = { new(f, Mib, Mib) };

        ColdMaker.Make(hot, flush);

        Assert.Throws<System.ComponentModel.Win32Exception>(() => ColdMaker.ReadUnbuffered(new[] { new ReadOp(tmp.Combine("nope"), 0, 10) }));
    }
}
