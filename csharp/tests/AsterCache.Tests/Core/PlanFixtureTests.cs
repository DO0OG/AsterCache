using System.Text;
using System.Text.Json;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Trace;
using Xunit;

namespace AsterCache.Tests.Core;

/// Python 참조 구현이 만든 정답 픽스처와 바이트 단위로 대조합니다 (F1~F5, tools/make_fixtures.py).
public sealed class PlanFixtureTests
{
    private const string E3Sha = "7d64ea23fe801a163474da54bbb63ce1c5523dc107c4cccec5806319eff74424";
    private const string Root = @"D:\AsterBench";
    private const long ZoneE3 = 95135032;
    private const long Mib = 1L << 20;
    private const long Block = 65536;

    private sealed record Snapshot(
        JsonDocument Doc, Dictionary<string, IReadOnlyList<Extent>?> Extents, Dictionary<string, long> Sizes,
        Dictionary<string, Fingerprint> Fingerprints, long ClusterBytes) : IDisposable
    {
        public List<IReadOnlyList<TraceEvent>> Sessions => PlanFixtures.Sessions(Doc.RootElement.GetProperty("sessions"));

        public void Dispose() => Doc.Dispose();
    }

    private static Snapshot LoadSnapshot()
    {
        JsonDocument doc = PlanFixtures.Doc("fresh/snapshot.json");
        JsonElement files = doc.RootElement.GetProperty("files");
        var ext = new Dictionary<string, IReadOnlyList<Extent>?>(StringComparer.Ordinal);
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        var fps = new Dictionary<string, Fingerprint>(StringComparer.Ordinal);
        foreach (JsonProperty f in files.EnumerateObject())
        {
            ext[f.Name] = PlanFixtures.ExtentList(f.Value.GetProperty("extents"));
            sizes[f.Name] = f.Value.GetProperty("size").GetInt64();
            fps[f.Name] = new Fingerprint(f.Name, sizes[f.Name], f.Value.GetProperty("mtime_ns").GetInt64());
        }

        return new Snapshot(doc, ext, sizes, fps, doc.RootElement.GetProperty("cluster_bytes").GetInt64());
    }

    [Fact]
    public void F1_RecordedPlanReadsAndWritesBackIdentically()
    {
        byte[] original = PlanFixtures.Bytes("e3/plan.json");
        PlanFile plan = PlanFile.Load(PlanFixtures.PathOf("e3/plan.json"));
        string tmp = Path.Combine(Directory.CreateTempSubdirectory().FullName, "plan.json");
        try
        {
            plan.Save(tmp);
            byte[] written = File.ReadAllBytes(tmp);

            PlanFixtures.SameText(PlanFixtures.Lf(original), PlanFixtures.Lf(written));
            Assert.Contains("\r\n", Encoding.UTF8.GetString(written), StringComparison.Ordinal);
            Assert.DoesNotContain("\n", Encoding.UTF8.GetString(written).Replace("\r\n", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
            Assert.NotEqual((byte)'\n', written[^1]);
            Assert.Equal(1048, plan.Tasks.Count);
            Assert.Equal(64, plan.Fingerprints.Count);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(tmp)!, true);
        }
    }

    [Fact]
    public void F2_PlanShaMatchesJournalHeader()
    {
        PlanFile plan = PlanFile.Load(PlanFixtures.PathOf("e3/plan.json"));
        string header = PlanFixtures.Lf(PlanFixtures.Bytes("e3/journal.jsonl")).Split('\n')[0];

        Assert.Equal(E3Sha, plan.Sha());
        Assert.Contains(E3Sha, header, StringComparison.Ordinal);
    }

    [Fact]
    public void F3_RecordedPlanIsReproducedFromTraceAndPreRelocationLayout()
    {
        using Snapshot snap = LoadSnapshot();
        using JsonDocument pre = PlanFixtures.Doc("fresh/e3_pre_extents.json");
        Dictionary<string, IReadOnlyList<Extent>?> ext = PlanFixtures.Extents(pre.RootElement);
        PlanFile recorded = PlanFile.Load(PlanFixtures.PathOf("e3/plan.json"));
        Dictionary<string, Fingerprint> fps = recorded.Fingerprints.ToDictionary(f => f.Path, StringComparer.Ordinal);
        List<TraceEvent> trace = TraceLoader.LoadRanges(PlanFixtures.PathOf("e3/trace.json"), Root,
            p => p == Root || snap.Sizes.ContainsKey(p) ? p : null, p => snap.Sizes.TryGetValue(p, out long s) ? s : null);

        PlanFile made = PlanFile.Make([trace], Root, 300 * Mib, snap.ClusterBytes, p => ext[p], p => fps[p].Size, p => fps[p],
            zoneStartLcn: null, preserveBytes: 8 * Mib, minSessions: 1).Resolve(ZoneE3);

        PlanFixtures.SameText(PlanFixtures.Lf(PlanFixtures.Bytes("e3/plan.json")), made.Dumps());
        Assert.Equal(E3Sha, made.Sha());
    }

    [Theory]
    [InlineData("plan", null, 8L)]
    [InlineData("plan_nopreserve", null, 1L << 30)]
    [InlineData("plan_inplace", ZoneE3, 1L << 30)]
    [InlineData("plan_zone_preserve", ZoneE3, 8L)]
    public void F4_CurrentLayoutSnapshotPlansAreByteIdentical(string name, long? zoneStart, long preserveMib)
    {
        using Snapshot snap = LoadSnapshot();
        using JsonDocument shas = PlanFixtures.Doc("fresh/shas.json");
        List<IReadOnlyList<TraceEvent>> sessions = snap.Sessions;

        PlanFile plan = PlanFile.Make(sessions, Root, 300 * Mib, snap.ClusterBytes, p => snap.Extents[p], p => snap.Sizes[p],
            p => snap.Fingerprints[p], zoneStart, Block, 1, preserveMib * Mib);

        PlanFixtures.SameText(Encoding.UTF8.GetString(PlanFixtures.Bytes($"fresh/{name}.json")), PyJson.ToCrlf(plan.Dumps()));
        Assert.Equal(shas.RootElement.GetProperty(name).GetString(), plan.Sha());
        if (name == "plan")
        {
            string rows = PyJson.Dumps(plan.PrefetchTrace(sessions).Select(r => r.ToJson()).ToList(), ensureAscii: false) + "\n";
            PlanFixtures.SameText(Encoding.UTF8.GetString(PlanFixtures.Bytes("fresh/plan.prefetch.json")), PyJson.ToCrlf(rows));
        }
        else if (name == "plan_inplace")
        {
            Assert.Empty(plan.Tasks);
            Assert.Equal(plan.Stats["hot_bytes"], plan.Stats["in_place_bytes"]);
        }
        else if (name == "plan_nopreserve")
        {
            Assert.Equal(1048, plan.Tasks.Count);
        }
    }

    [Fact]
    public void F5_HotSetStatsAndConsensusUnitsMatchPython()
    {
        using JsonDocument doc = PlanFixtures.Doc("synthetic/cases.json");
        foreach (JsonElement b in doc.RootElement.GetProperty("bases").EnumerateArray())
        {
            string name = b.GetProperty("name").GetString()!;
            List<IReadOnlyList<TraceEvent>> sessions = PlanFixtures.Sessions(b.GetProperty("sessions"));
            foreach (JsonElement v in b.GetProperty("hot_variants").EnumerateArray())
            {
                PlanFixtures.With(name, () =>
                {
                    (List<BlockKey> hot, Dictionary<BlockKey, BlockStat> stats) = HotSet.Select(sessions, v.GetProperty("budget_bytes").GetInt64(),
                        Block, contiguous: PlanFixtures.Keys(v.GetProperty("contiguous")).ToHashSet(),
                        minSessions: v.GetProperty("min_sessions").GetInt32());

                    Assert.Equal(PlanFixtures.Keys(v.GetProperty("hot")), hot);
                    Assert.Equal(v.GetProperty("stats").GetArrayLength(), stats.Count);
                    foreach (JsonElement row in v.GetProperty("stats").EnumerateArray())
                    {
                        BlockStat s = stats[new BlockKey(row[0].GetString()!, row[1].GetInt64())];
                        Assert.Equal([row[2].GetString(), row[3].GetString(), row[4].GetString(), row[5].GetString()],
                            new[] { s.F, s.M, s.S, s.Score }.Select(d => BitConverter.DoubleToInt64Bits(d).ToString("x16", System.Globalization.CultureInfo.InvariantCulture)));
                    }
                });
            }

            PlanFixtures.With(name, () =>
            {
                List<BlockKey> hot = PlanFixtures.Keys(b.GetProperty("hot_variants")[0].GetProperty("hot"));
                Assert.Equal(PlanFixtures.Units(b.GetProperty("units")), Consensus.Order(sessions, hot, Block));
            });
        }
    }

    [Fact]
    public void F5_ZoneAllocationMatchesPythonIncludingErrors()
    {
        using JsonDocument doc = PlanFixtures.Doc("synthetic/cases.json");
        int errors = 0;
        int ordered = 0;
        foreach (JsonElement b in doc.RootElement.GetProperty("bases").EnumerateArray())
        {
            List<Unit> units = PlanFixtures.Units(b.GetProperty("units"));
            Dictionary<string, IReadOnlyList<Extent>?> ext = PlanFixtures.Extents(b.GetProperty("extents"));
            Dictionary<string, long> sizes = PlanFixtures.Sizes(b.GetProperty("sizes"));
            foreach (JsonElement a in b.GetProperty("allocs").EnumerateArray())
            {
                string name = b.GetProperty("name").GetString() + "/" + a.GetProperty("name").GetString();
                PlanFixtures.With(name, () => CheckAlloc(a.GetProperty("expected"), () => ZoneAllocator.Allocate(units, ext,
                    PlanFixtures.NullableLong(a.GetProperty("zone_start")), a.GetProperty("zone_clusters").GetInt64(), 4096, Block, sizes,
                    a.GetProperty("preserve_bytes").GetInt64())));
                errors += a.GetProperty("expected").TryGetProperty("error", out _) ? 1 : 0;
                ordered += a.GetProperty("expected").TryGetProperty("tasks", out _) ? 1 : 0;
            }
        }

        foreach (JsonElement h in doc.RootElement.GetProperty("handmade").EnumerateArray())
        {
            string name = "handmade/" + h.GetProperty("name").GetString();
            PlanFixtures.With(name, () => CheckAlloc(h.GetProperty("expected"), () => ZoneAllocator.Allocate(
                PlanFixtures.Units(h.GetProperty("units")), PlanFixtures.Extents(h.GetProperty("extents")),
                PlanFixtures.NullableLong(h.GetProperty("zone_start")), h.GetProperty("zone_clusters").GetInt64(), 4096, Block,
                PlanFixtures.Sizes(h.GetProperty("sizes")), h.GetProperty("preserve_bytes").GetInt64(), h.GetProperty("align_clusters").GetInt64())));
            errors += h.GetProperty("expected").TryGetProperty("error", out _) ? 1 : 0;
        }

        Assert.True(errors >= 4 && ordered >= 20, $"fixture should hold error and success cases ({errors}/{ordered})");
    }

    [Fact]
    public void F5_WholePlansAreByteIdenticalWithSameSha()
    {
        using JsonDocument doc = PlanFixtures.Doc("synthetic/cases.json");
        foreach (JsonElement b in doc.RootElement.GetProperty("bases").EnumerateArray())
        {
            List<IReadOnlyList<TraceEvent>> sessions = PlanFixtures.Sessions(b.GetProperty("sessions"));
            Dictionary<string, IReadOnlyList<Extent>?> ext = PlanFixtures.Extents(b.GetProperty("extents"));
            Dictionary<string, long> sizes = PlanFixtures.Sizes(b.GetProperty("sizes"));
            Dictionary<string, Fingerprint> fps = b.GetProperty("fingerprints").EnumerateObject().ToDictionary(p => p.Name,
                p => new Fingerprint(p.Name, p.Value[0].GetInt64(), p.Value[1].GetInt64()), StringComparer.Ordinal);
            foreach (JsonElement p in b.GetProperty("plans").EnumerateArray())
            {
                string name = b.GetProperty("name").GetString() + "/" + p.GetProperty("name").GetString();
                PlanFixtures.With(name, () =>
                {
                    PlanFile plan = PlanFile.Make(sessions, p.GetProperty("origin_root").GetString()!, p.GetProperty("zone_bytes").GetInt64(), 4096,
                        path => ext.GetValueOrDefault(path), path => sizes.GetValueOrDefault(path), path => fps[path],
                        PlanFixtures.NullableLong(p.GetProperty("zone_start")), Block, p.GetProperty("min_sessions").GetInt32(),
                        p.GetProperty("preserve_bytes").GetInt64());

                    PlanFixtures.SameText(p.GetProperty("plan_text").GetString()!, PyJson.ToCrlf(plan.Dumps()));
                    Assert.Equal(p.GetProperty("sha").GetString(), plan.Sha());
                    PlanFile again = PlanFile.FromJson(JsonRead.Parse(plan.Dumps()));
                    Assert.Equal(plan.Dumps(), again.Dumps());
                });
            }
        }
    }

    private static void CheckAlloc(JsonElement expected, Func<AllocResult> run)
    {
        if (expected.TryGetProperty("error", out _))
        {
            Assert.Throws<PyValueException>(run);
            return;
        }

        AllocResult got = run();
        Assert.Equal(expected.GetProperty("tasks").EnumerateArray().Select(t => new PlanTask(t[0].GetString()!, t[1].GetInt64(), t[2].GetInt64(), t[3].GetInt64())),
            got.Tasks);
        Assert.Equal(expected.GetProperty("order").EnumerateArray().Select(o => new PlanRange(o[0].GetString()!, o[1].GetInt64(), o[2].GetInt64())),
            got.Order);
        Assert.Equal(expected.GetProperty("stats_json").GetString(), PyJson.Dumps(got.Stats.ToJson()));
    }
}
