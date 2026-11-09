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

    [Fact]
    public void CancelledTokenReadsNothingInReadMethod()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", (int)(20 * Mib));
        ReadOp[] ops = { new(tmp.Combine("f.bin"), 0, 20 * Mib) };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        StreamStats cut = ZoneStreamer.Run(ops, StreamMethod.Read, ct: cts.Token);
        StreamStats full = ZoneStreamer.Run(ops, StreamMethod.Read);

        Assert.Equal((0L, 0L, true), (cut.Bytes, cut.Requests, cut.Aborted));
        Assert.Equal((20 * Mib, false, 20 * Mib), (full.Bytes, full.Aborted, full.RequestedBytes));
    }

    [Fact]
    public void CancelledTokenReadsNothingFromASingleSmallFile()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", (int)Mib);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        StreamStats cut = ZoneStreamer.Run([new(tmp.Combine("f.bin"), 0, Mib)], StreamMethod.Read, ct: cts.Token);

        Assert.Equal((0L, true), (cut.Bytes, cut.Aborted));
    }

    [Theory]
    [InlineData(0, 0)] // 첫 파일 시작 시점에 취소
    [InlineData(1, 1)] // 둘째 파일 시작 시점에 취소: 첫 파일만 읽음
    [InlineData(3, 3)] // 마지막 묶음 시작 시점에 취소
    public void ReadStopsBeforeTheNextFileWhenCancelledDuringOpEnumeration(int cancelAtIndex, int expectedMib)
    {
        using var tmp = new TempDir();
        var ops = new List<ReadOp>();
        for (int i = 0; i < 4; i++)
        {
            tmp.WriteRandom($"f{i}.bin", (int)Mib);
            ops.Add(new ReadOp(tmp.Combine($"f{i}.bin"), 0, Mib));
        }

        using var cts = new CancellationTokenSource();
        StreamStats r = ZoneStreamer.Run(new CancelAt(ops, cancelAtIndex, cts), StreamMethod.Read, ct: cts.Token);

        Assert.Equal((expectedMib * Mib, expectedMib, true), (r.Bytes, r.Requests, r.Aborted));
    }

    [Fact]
    public void PvmStopsMappingAndCallsNothingWhenCancelledDuringOpEnumeration()
    {
        using var tmp = new TempDir();
        var ops = new List<ReadOp>();
        for (int i = 0; i < 6; i++)
        {
            tmp.WriteRandom($"f{i}.bin", 8192);
            ops.Add(new ReadOp(tmp.Combine($"f{i}.bin"), 0, 8192));
        }

        using var cts = new CancellationTokenSource();
        int calls = 0;
        (long bytes, _, _) = ZoneStreamer.RunPvm(new CancelAt(ops, 2, cts), cts.Token, out bool aborted, out _, _ => { calls++; return true; });

        Assert.Equal((0, 0L, true), (calls, bytes, aborted));
    }

    // index 번째 op 를 꺼내는 순간 취소하는 목록 (op 열거 도중 게임이 뜨는 상황).
    private sealed class CancelAt(List<ReadOp> ops, int index, CancellationTokenSource cts) : IReadOnlyList<ReadOp>
    {
        public int Count => ops.Count;

        public ReadOp this[int i] => ops[i];

        public IEnumerator<ReadOp> GetEnumerator()
        {
            for (int i = 0; i < ops.Count; i++)
            {
                if (i == index)
                {
                    cts.Cancel();
                }

                yield return ops[i];
            }
        }

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    [Fact]
    public void PvmCancelledBeforeTheFirstCallMakesNoCall()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", (int)(20 * Mib));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        StreamStats cut = ZoneStreamer.Run([new(tmp.Combine("f.bin"), 0, 20 * Mib)], StreamMethod.Pvm, ct: cts.Token);
        int calls = 0;
        _ = ZoneStreamer.RunPvm([new(tmp.Combine("f.bin"), 0, 20 * Mib)], cts.Token, out bool aborted, out long hint, _ => { calls++; return true; });

        Assert.Equal((0L, 0L, true, 20 * Mib), (cut.Bytes, cut.HintAcceptedBytes, cut.Aborted, cut.RequestedBytes));
        Assert.Equal((0, true, 0L), (calls, aborted, hint));
    }

    [Theory]
    [InlineData(1, 1, 8)] // 첫 호출 직후 취소 → 둘째 조각 전에 멈춤
    [InlineData(2, 2, 16)] // 둘째 호출 직후 취소 → 마지막 조각 전에 멈춤
    [InlineData(3, 3, 20)] // 마지막 호출 직후 취소는 다 끝난 것 (중단 아님)
    public void PvmStopsBetweenSlicesWhenCancelledAfterACall(int cancelAfter, int expectedCalls, int expectedMib)
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", (int)(20 * Mib)); // 8 + 8 + 4 MiB 세 조각
        using var cts = new CancellationTokenSource();
        int calls = 0;

        (long bytes, _, _) = ZoneStreamer.RunPvm([new(tmp.Combine("f.bin"), 0, 20 * Mib)], cts.Token, out bool aborted, out long hint, _ =>
        {
            if (++calls == cancelAfter)
            {
                cts.Cancel();
            }

            return true;
        });

        Assert.Equal((expectedCalls, expectedMib * Mib, expectedMib * Mib), (calls, bytes, hint));
        Assert.Equal(cancelAfter < 3, aborted);
    }

    [Fact]
    public void PvmReportsRequestSizeAndProgressBeforeAndAfterEachFragment()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", (int)(20 * Mib));
        var issues = new List<(string Stage, long Bytes, bool IsData)>();

        _ = ZoneStreamer.RunPvm([new(tmp.Combine("f.bin"), 0, 20 * Mib)], CancellationToken.None,
            out _, out _, _ => true, (stage, bytes, isData) => issues.Add((stage, bytes, isData)));

        Assert.Equal(new long[] { 8 * Mib, 8 * Mib, 8 * Mib, 8 * Mib, 4 * Mib, 4 * Mib }, issues.Select(issue => issue.Bytes));
        Assert.All(issues, issue => Assert.True(issue.IsData));
        Assert.Contains(issues, issue => issue.Stage == "pvm fragment 1/3 started, progress 0/20971520 bytes");
        Assert.Contains(issues, issue => issue.Stage == "pvm fragment 3/3 completed, progress 20971520/20971520 bytes");
    }

    [Fact]
    public void PvmHintBytesAreSeparatedFromRequestedBytes()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", 10_000);

        StreamStats r = ZoneStreamer.Run([new(tmp.Combine("f.bin"), 4000, 1_000_000)], StreamMethod.Pvm);
        StreamStats read = ZoneStreamer.Run([new(tmp.Combine("f.bin"), 4000, 1_000_000)], StreamMethod.Read);

        Assert.Equal((6000L, 6000L, 1_000_000L, false), (r.Bytes, r.HintAcceptedBytes, r.RequestedBytes, r.Aborted));
        Assert.Equal((6000L, 0L), (read.Bytes, read.HintAcceptedBytes)); // 실제 읽기 방식은 힌트 바이트가 없음
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
    public void PvmSkipsOnlyTheFileThatCannotBeMappedAndKeepsTheRest()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("a.bin", 100_000);
        tmp.WriteRandom("locked.bin", 100_000);
        tmp.WriteRandom("c.bin", 100_000);
        using var exclusive = new FileStream(tmp.Combine("locked.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); // 게임이 배타적으로 연 파일처럼
        ReadOp[] ops =
        {
            new(tmp.Combine("a.bin"), 0, 40_000), new(tmp.Combine("locked.bin"), 0, 5000), new(tmp.Combine("c.bin"), 0, 30_000),
            new(tmp.Combine("locked.bin"), 8192, 5000), new(tmp.Combine("nope.bin"), 0, 10),
        };

        StreamStats r = ZoneStreamer.Run(ops, StreamMethod.Pvm);

        Assert.Equal((70_000L, 2L, 2L), (r.Bytes, r.Requests, r.Errors)); // 잠긴 파일과 없는 파일이 각각 오류 1
    }

    [Fact]
    public void ReadMethodAlsoSkipsTheLockedFileOnly()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("a.bin", 100_000);
        tmp.WriteRandom("locked.bin", 100_000);
        using var exclusive = new FileStream(tmp.Combine("locked.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        ReadOp[] ops = { new(tmp.Combine("locked.bin"), 0, 5000), new(tmp.Combine("a.bin"), 0, 40_000) };

        StreamStats r = ZoneStreamer.Run(ops, StreamMethod.Read);

        Assert.Equal((40_000L, 1L, 1L), (r.Bytes, r.Requests, r.Errors));
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
