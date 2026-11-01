using AsterCache.Cli;
using AsterCache.Core.Json;
using AsterCache.Core.Prefetch;
using AsterCache.Prefetch;
using AsterCache.Prefetch.Modes;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Prefetch;

/// 오버랩 읽기와 stream-bench: 모든 방식이 같은 바이트를 읽는지 (재생 crc 동일), 순서·오류 처리.
public sealed class ModesTests
{
    private const long Mib = 1 << 20;

    [Theory]
    [InlineData(1, 4096)]
    [InlineData(2, 65536)]
    [InlineData(3, 1 << 20)]
    [InlineData(4, 1 << 20)]
    [InlineData(64, 8192)]
    public void OverlappedPiecesEqualFileBytesAndArriveInOrder(int depth, int chunk)
    {
        using var tmp = new TempDir();
        byte[] data = tmp.WriteRandom("f.bin", (int)(3 * Mib));
        string f = tmp.Combine("f.bin");
        ReadOp[] ops = { new(f, 0, 100_000), new(f, Mib, 70_000), new(f, (2 * Mib) + 5, 12_345), new(f, 3 * Mib - 100, 5000) };
        var seen = new List<(long Offset, int Length)>();

        (long bytes, long requests, long errors) = OverlappedReader.Run(ops, depth, chunk, (path, offset, piece) =>
        {
            Assert.Equal(f, path);
            Assert.Equal(data.AsSpan((int)offset, piece.Length).ToArray(), piece.ToArray());
            seen.Add((offset, piece.Length));
        });

        // 마지막 op 는 파일 끝으로 잘려 100 바이트.
        Assert.Equal(100_000L + 70_000 + 12_345 + 100, bytes);
        Assert.Equal(0L, errors);
        Assert.Equal(requests, seen.Count);
        Assert.Equal(seen.OrderBy(s => s.Offset).ToList(), seen); // 완료 처리는 발행(존) 순서
    }

    [Fact]
    public void OverlappedCountsMissingFileAsErrorAndKeepsGoing()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", 5000);
        ReadOp[] ops = { new(tmp.Combine("nope"), 0, 10), new(tmp.Combine("f.bin"), 0, 5000) };

        (long bytes, long requests, long errors) = OverlappedReader.Run(ops, 2, 4096);

        Assert.Equal((5000L, 2L, 1L), (bytes, requests, errors)); // 5000 바이트 = 4096 + 904 두 조각
        Assert.Equal((0L, 0L, 0L), OverlappedReader.Run(Array.Empty<ReadOp>(), 3, 4096));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void StreamerOverlappedMethodReadsEveryByte(int depth)
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", (int)(3 * Mib));
        string f = tmp.Combine("f.bin");
        ReadOp[] ops = { new(f, 0, 100000), new(f, Mib, 70000), new(f, (2 * Mib) + 5, 12345) };

        StreamStats r = ZoneStreamer.Run(ops, StreamMethod.Overlapped, 1 << 20, depth);

        Assert.Equal(ops.Sum(o => o.Length), r.Bytes);
        Assert.Equal((3L, 0L), (r.Requests, r.Errors));
    }

    [Fact]
    public void ReplayCrcMatchesZlibKnownValue()
    {
        using var tmp = new TempDir();
        File.WriteAllText(tmp.Combine("c.txt"), "123456789");

        ReplayResult r = StreamBench.Replay(new[] { new ReplayEvent(tmp.Combine("c.txt"), 0, 9) });

        Assert.Equal(0xCBF43926u, r.Crc32);
        Assert.Equal((1, 9L), (r.Requests, r.Bytes));
    }

    [Fact]
    public void AllModesGiveTheSameReplayCrcAndBytes()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("a.bin", (int)(2 * Mib));
        tmp.WriteRandom("b.bin", (int)Mib);
        string a = tmp.Combine("a.bin");
        string b = tmp.Combine("b.bin");
        ReadOp[] ops = { new(a, 0, Mib), new(b, 4096, 300_000), new(a, Mib + 100, 50_000) };
        ReplayEvent[] events = { new(a, 10, 20_000), new(b, 5000, 100_000), new(a, Mib, 60_000) };
        string[] names = { "qd1", "pvm", "ovl2", "ovl3", "ovl4" };

        List<BenchRow> rows = StreamBench.Run(ops, events, new[] { new ReadOp(a, Mib, 4096) }, names.Select(StreamBench.ParseMode).ToList(), 2);

        Assert.Equal(10, rows.Count);
        Assert.Equal(names.Order(), rows.Select(r => r.Mode).Distinct().Order());
        Assert.Single(rows.Select(r => r.Replay.Crc32).Distinct());
        Assert.All(rows, r => Assert.Equal((ops.Sum(o => o.Length), 0L, 180_000L), (r.Bytes, r.Errors, r.Replay.Bytes)));
    }

    [Fact]
    public void ParseModeRejectsUnknownNames()
    {
        Assert.Throws<ArgumentException>(() => StreamBench.ParseMode("ovl0"));
        Assert.Throws<ArgumentException>(() => StreamBench.ParseMode("ovl65"));
        Assert.Throws<ArgumentException>(() => StreamBench.ParseMode("xyz"));
        Assert.Equal("ovl3", StreamBench.ParseMode("ovl3").Name);
    }

    private static (string Root, string Trace) MakeTree(TempDir dir)
    {
        string root = dir.Combine("root");
        Directory.CreateDirectory(root);
        string a = Path.Combine(root, "a.bin");
        string b = Path.Combine(root, "b.bin");
        var rnd = new Random(7);
        foreach (string p in new[] { a, b })
        {
            byte[] d = new byte[3 << 20];
            rnd.NextBytes(d);
            File.WriteAllBytes(p, d);
        }

        var rows = new List<object?>
        {
            new JObject { { "path", a }, { "offset", 0L }, { "length", 131072L }, { "cost", 8.0 } },
            new JObject { { "path", a }, { "offset", 1L << 20 }, { "length", 65536L }, { "cost", 8.0 } },
            new JObject { { "path", b }, { "offset", 65536L }, { "length", 65536L }, { "cost", 8.0 } },
        };
        string trace = Path.Combine(root, "trace.json");
        File.WriteAllText(trace, PyJson.Dumps(rows));
        return (root, trace);
    }

    [Fact]
    public void PrefetchCommandRunsOverlappedMethods()
    {
        using var dir = new TempDir();
        (string root, string trace) = MakeTree(dir);

        var o = new StringWriter();
        int code = AsterCli.Run(new[] { "prefetch", "--trace", trace, "--origin-root", root, "--execute", "--method", "ovl3" }, o, new StringWriter());

        Assert.Equal(0, code);
        var json = (JObject)JsonRead.Parse(o.ToString())!;
        Assert.Equal((262144L, 0L, "ovl3"), (json["bytes"], json["errors"], json["method"]));
    }

    [Fact]
    public void StreamBenchCommandWritesTableAndFile()
    {
        using var dir = new TempDir();
        (string root, _) = MakeTree(dir);
        string outFile = dir.Combine("out.json");

        var o = new StringWriter();
        var e = new StringWriter();
        int code = AsterCli.Run(new[] { "stream-bench", "--root", root, "--modes", "qd1,pvm,ovl2", "--repeat", "2", "--flush-mib", "1", "--out", outFile }, o, e);

        Assert.True(code == 0, o + e.ToString());
        var json = (JObject)JsonRead.Parse(File.ReadAllText(outFile))!;
        Assert.Equal(true, json["crc_ok"]);
        var table = (List<object?>)json["table"]!;
        Assert.Equal(3, table.Count);
        Assert.All(table, t => Assert.Equal(0L, ((JObject)t!)["errors"]));
        Assert.Equal(6, ((List<object?>)json["rows"]!).Count);
    }
}
