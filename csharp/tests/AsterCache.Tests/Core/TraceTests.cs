using System.Text.Json;
using AsterCache.Core.Json;
using AsterCache.Core.Trace;
using Xunit;

namespace AsterCache.Tests.Core;

public sealed class TraceTests
{
    private const long Block = 65536;

    [Fact]
    public void TMsIsOptionalAndOnlyWrittenWhenPresent()
    {
        Assert.Equal("{\"path\": \"p\", \"offset\": 1, \"length\": 2, \"cost\": 8.0}", PyJson.Dumps(new TraceEvent("p", 1, 2).ToJson()));
        Assert.Equal("{\"path\": \"p\", \"offset\": 1, \"length\": 2, \"cost\": 8.0, \"t_ms\": 1500}", PyJson.Dumps(new TraceEvent("p", 1, 2, TMs: 1500).ToJson()));

        Assert.Null(TraceEvents.FromJson(JsonRead.Parse("{\"path\":\"p\",\"offset\":1,\"length\":2}")).TMs);
        Assert.Equal(1500L, TraceEvents.FromJson(JsonRead.Parse("{\"path\":\"p\",\"offset\":1,\"length\":2,\"t_ms\":1500}")).TMs);
        Assert.Null(TraceEvents.FromJson(JsonRead.Parse("{\"path\":\"p\",\"offset\":1,\"length\":2,\"t_ms\":null}")).TMs);
        foreach (string bad in new[] { "-1", "\"5\"", "1.5", "true" })
        {
            Assert.Throws<PyValueException>(() => TraceEvents.FromJson(JsonRead.Parse("{\"path\":\"p\",\"offset\":1,\"length\":2,\"t_ms\":" + bad + "}")));
        }
    }

    [Fact]
    public void ValidatedMapsRequestsToBlocks()
    {
        List<BlockEvent> got = TraceEvents.Validated(
            [new TraceEvent("p", 0, 1), new TraceEvent("p", Block - 1, 2, 3.0), new TraceEvent("q", 3 * Block, 2 * Block)], Block);

        Assert.Equal([new BlockEvent("p", 0, 0, 8.0), new BlockEvent("p", 0, 1, 3.0), new BlockEvent("q", 3, 4, 8.0)], got);
    }

    [Theory]
    [InlineData("", 0L, 1L, 8.0)]
    [InlineData("p", -1L, 1L, 8.0)]
    [InlineData("p", 0L, 0L, 8.0)]
    [InlineData("p", 0L, 67108865L, 8.0)]
    [InlineData("p", 0L, 1L, -1.0)]
    [InlineData("p", 0L, 1L, double.NaN)]
    [InlineData("p", 0L, 1L, double.PositiveInfinity)]
    public void ValidatedRejectsBadEvents(string path, long offset, long length, double cost) =>
        Assert.Throws<PyValueException>(() => TraceEvents.Validated([new TraceEvent(path, offset, length, cost)], Block));

    [Fact]
    public void ValidatedLimitsSessionAndRequestSize()
    {
        var tooMany = new List<TraceEvent>(Enumerable.Repeat(new TraceEvent("p", 0, 1), 500_001));

        Assert.Equal(500_000, TraceEvents.Validated(tooMany.GetRange(0, 500_000), Block).Count);
        Assert.Throws<PyValueException>(() => TraceEvents.Validated(tooMany, Block));
        Assert.Equal(new BlockEvent("p", 0, 1024, 8.0), Assert.Single(TraceEvents.Validated([new TraceEvent("p", Block - 1, TraceEvents.MaxLength)], Block)));
        Assert.Throws<PyValueException>(() => TraceEvents.Validated([new TraceEvent("p", 0, TraceEvents.MaxLength + 1)], Block));
    }

    [Fact]
    public void FromJsonDefaultsCostAndRejectsWrongTypes()
    {
        Assert.Equal(new TraceEvent("p", 1, 2, 8.0), TraceEvents.FromJson(JsonRead.Parse("{\"path\":\"p\",\"offset\":1,\"length\":2}")));
        Assert.Equal(new TraceEvent("p", 1, 2, 3.0), TraceEvents.FromJson(JsonRead.Parse("{\"path\":\"p\",\"offset\":1,\"length\":2,\"cost\":3}")));
        foreach (string bad in new[]
                 {
                     "[]", "{\"offset\":1,\"length\":2}", "{\"path\":1,\"offset\":1,\"length\":2}", "{\"path\":\"p\",\"offset\":1.0,\"length\":2}",
                     "{\"path\":\"p\",\"offset\":true,\"length\":2}", "{\"path\":\"p\",\"offset\":1,\"length\":2,\"cost\":\"x\"}",
                     "{\"path\":\"p\",\"offset\":1,\"length\":2,\"cost\":null}",
                 })
        {
            Assert.Throws<PyValueException>(() => TraceEvents.FromJson(JsonRead.Parse(bad)));
        }
    }

    [Theory]
    [InlineData(@"D:\AsterBench\data\a.bin", @"d:\asterbench", true)]
    [InlineData(@"D:\AsterBench", @"D:\AsterBench\", true)]
    [InlineData(@"D:\AsterBench2\a.bin", @"D:\AsterBench", false)]
    [InlineData(@"E:\AsterBench\a.bin", @"D:\AsterBench", false)]
    [InlineData("D:/AsterBench/a.bin", @"D:\AsterBench", true)]
    public void IsRelativeToIgnoresCaseAndMatchesComponents(string path, string root, bool expected) =>
        Assert.Equal(expected, TraceLoader.IsRelativeTo(path, root));

    [Fact]
    public void LoadRangesDropsOutsideMissingAndClampsToFileSize()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string root = Path.Combine(dir, "root");
            string other = Path.Combine(dir, "other");
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(other);
            string inside = Path.Combine(root, "a.bin");
            string outside = Path.Combine(other, "b.bin");
            File.WriteAllBytes(inside, new byte[1000]);
            File.WriteAllBytes(outside, new byte[1000]);
            string missing = Path.Combine(root, "gone.bin");
            string trace = Path.Combine(dir, "trace.json");
            File.WriteAllText(trace, JsonSerializer.Serialize(new object[]
            {
                new { path = inside, offset = 0, length = 5000, cost = 2.5 },
                new { path = inside, offset = 990, length = 100 },
                new { path = inside, offset = 1000, length = 10 },
                new { path = outside, offset = 0, length = 10 },
                new { path = missing, offset = 0, length = 10 },
            }));

            List<TraceEvent> got = TraceLoader.LoadRanges(trace, root);

            Assert.Equal([new TraceEvent(inside, 0, 1000, 2.5), new TraceEvent(inside, 990, 10, 8.0)], got);

            string wrapped = Path.Combine(dir, "plan.json");
            File.WriteAllText(wrapped, JsonSerializer.Serialize(new { ranges = new[] { new { path = inside, offset = 10, length = 20 } } }));
            Assert.Equal([new TraceEvent(inside, 10, 20, 8.0)], TraceLoader.LoadRanges(wrapped, root));
            Assert.DoesNotContain(TraceLoader.LoadRanges(trace, other), e => e.Path == inside);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadRangesRejectsBadRowsAndMissingRoot()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string trace = Path.Combine(dir, "t.json");
            File.WriteAllText(trace, "[{\"path\":\"x\",\"offset\":-1,\"length\":5}]");
            Assert.Throws<PyValueException>(() => TraceLoader.LoadRanges(trace, dir));
            File.WriteAllText(trace, "[{\"path\":\"x\",\"offset\":0,\"length\":0}]");
            Assert.Throws<PyValueException>(() => TraceLoader.LoadRanges(trace, dir));
            File.WriteAllText(trace, "{\"x\": []}");
            Assert.Throws<PyValueException>(() => TraceLoader.LoadRanges(trace, dir));
            File.WriteAllText(trace, "[]");
            Assert.Throws<DirectoryNotFoundException>(() => TraceLoader.LoadRanges(trace, Path.Combine(dir, "nope")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadRangesOnRecordedTraceMatchesPython()
    {
        using JsonDocument snap = PlanFixtures.Doc("fresh/snapshot.json");
        JsonElement files = snap.RootElement.GetProperty("files");
        string root = snap.RootElement.GetProperty("origin_root").GetString()!;

        List<TraceEvent> got = TraceLoader.LoadRanges(PlanFixtures.PathOf("e3/trace.json"), root,
            p => p == root || files.TryGetProperty(p, out _) ? p : null,
            p => files.TryGetProperty(p, out JsonElement f) ? f.GetProperty("size").GetInt64() : null);

        Assert.Equal(4000, got.Count);
        Assert.Equal(PlanFixtures.Session(snap.RootElement.GetProperty("sessions")[0]), got);
    }
}
