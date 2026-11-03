using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Trace;
using Xunit;

namespace AsterCache.Tests.Core;

/// tests/test_relayout.py 의 계획 관련 시험 (HotSet, Consensus, Allocate, PlanFile) 포팅.
public sealed class PlanPortTests
{
    private const long Blk = 65536;
    private const long Cb = 4096;
    private const long Per = Blk / Cb;

    private static TraceEvent Ev(string path, long block, long n = 1) => new(path, block * Blk, n * Blk, 8.0);

    private static List<TraceEvent> Run(params (string Path, long Block)[] keys) => keys.Select(k => Ev(k.Path, k.Block)).ToList();

    private static IReadOnlyList<IReadOnlyList<TraceEvent>> Sessions(params List<TraceEvent>[] runs) => runs;

    private static IReadOnlyList<IReadOnlyList<TraceEvent>> HotSessions() => Sessions(
        Run(("f", 0), ("f", 1), ("f", 2), ("f", 9)), Run(("f", 0), ("f", 1), ("f", 2)), Run(("f", 0), ("f", 2), ("f", 1), ("f", 5)));

    private static BlockKey K(string p, long b) => new(p, b);

    [Fact]
    public void HotSet_CoreBeforeTailAndBudget()
    {
        (List<BlockKey> blocks, _) = HotSet.Select(HotSessions(), 3 * Blk);
        (List<BlockKey> blocks4, _) = HotSet.Select(HotSessions(), 4 * Blk);

        Assert.Equal(new HashSet<BlockKey> { K("f", 0), K("f", 1), K("f", 2) }, blocks.ToHashSet());
        Assert.Equal(K("f", 5), blocks4[3]); // 가장 최근 tail 블록 (f 0.39) 이 가장 오래된 것 (f 0.28) 보다 앞
        Assert.Empty(HotSet.Select(HotSessions(), 0).Blocks);
    }

    [Fact]
    public void HotSet_StableOrderScoresHigherAndContiguousIsDemoted()
    {
        Dictionary<BlockKey, BlockStat> st = HotSet.Select(HotSessions(), 10 * Blk).Stats;
        Dictionary<BlockKey, BlockStat> st2 = HotSet.Select(HotSessions(), 10 * Blk, contiguous: new HashSet<BlockKey> { K("f", 0) }).Stats;

        Assert.True(st[K("f", 0)].Score > st[K("f", 1)].Score);
        Assert.Equal(st[K("f", 0)].Score * 0.2, st2[K("f", 0)].Score, 12);
    }

    [Fact]
    public void HotSet_SingleRunKeepsAccessOrder()
    {
        (List<BlockKey> blocks, _) = HotSet.Select(Sessions(Run(("b", 3), ("a", 7), ("b", 1))), 10 * Blk);

        Assert.Equal([K("b", 3), K("a", 7), K("b", 1)], blocks);
    }

    [Fact]
    public void HotSet_MinSessionsDropsSingleRunBlocks()
    {
        Assert.Equal(new HashSet<BlockKey> { K("f", 0), K("f", 1), K("f", 2), K("f", 5), K("f", 9) }, HotSet.Select(HotSessions(), 10 * Blk).Blocks.ToHashSet());
        (List<BlockKey> blocks, Dictionary<BlockKey, BlockStat> st) = HotSet.Select(HotSessions(), 10 * Blk, minSessions: 2);

        Assert.Equal(new HashSet<BlockKey> { K("f", 0), K("f", 1), K("f", 2) }, blocks.ToHashSet());
        Assert.DoesNotContain(K("f", 9), st.Keys);
        Assert.Equal(3, HotSet.Select(HotSessions(), 10 * Blk, minSessions: 3).Blocks.Count);
        Assert.Equal([K("b", 3), K("a", 7)], HotSet.Select(Sessions(Run(("b", 3), ("a", 7))), 10 * Blk, minSessions: 1).Blocks);
    }

    [Fact]
    public void HotSet_BadInput()
    {
        Assert.Throws<PyValueException>(() => HotSet.Select(Sessions(), Blk));
        Assert.Throws<PyValueException>(() => HotSet.Select(Sessions(Run(("f", 0))), -1));
        Assert.Throws<PyValueException>(() => HotSet.Select(Sessions(Run(("f", 0))), Blk, 1000));
        Assert.Throws<PyValueException>(() => HotSet.Select(Sessions(Run(("f", 0))), Blk, 3 * 4096));
        Assert.Throws<PyValueException>(() => HotSet.Select(Enumerable.Repeat(Run(("f", 0)), 33).Cast<IReadOnlyList<TraceEvent>>().ToList(), Blk));
    }

    [Fact]
    public void Consensus_MajorityOrderWins()
    {
        BlockKey a = K("p1", 0);
        BlockKey b = K("p2", 0);
        List<TraceEvent> ab = Run(("p1", 0), ("p2", 0));
        List<TraceEvent> ba = Run(("p2", 0), ("p1", 0));

        Assert.Equal(["p1", "p2"], Consensus.Order(Sessions(ab, ab, ba), [a, b]).Select(u => u.Path));
        Assert.Equal(["p2", "p1"], Consensus.Order(Sessions(ba, ab, ba), [a, b]).Select(u => u.Path));
    }

    [Fact]
    public void Consensus_SequentialBlocksFormOneUnit()
    {
        List<TraceEvent> run = Run(("f", 0), ("f", 1), ("f", 2), ("g", 0));
        BlockKey[] hot = [K("g", 0), K("f", 2), K("f", 0), K("f", 1)];

        Assert.Equal([new Unit("f", 0, 3), new Unit("g", 0, 1)], Consensus.Order(Sessions(run, run, run), hot));
    }

    [Fact]
    public void Consensus_EveryHotBlockAppearsOnce()
    {
        var rng = new Random(1);
        var keys = Enumerable.Range(0, 200).Select(_ => ("f" + rng.Next(4), (long)rng.Next(40))).ToList();
        List<List<TraceEvent>> runs = Enumerable.Range(0, 4)
            .Select(_ => keys.OrderBy(_ => rng.Next()).Take(120).Select(k => Ev(k.Item1, k.Item2)).ToList()).ToList();
        IReadOnlyList<IReadOnlyList<TraceEvent>> sessions = runs;

        (List<BlockKey> hot, _) = HotSet.Select(sessions, 1_000_000_000L);
        List<Unit> units = Consensus.Order(sessions, hot);

        List<BlockKey> got = units.SelectMany(u => Enumerable.Range(0, (int)u.Count).Select(i => K(u.Path, u.Start + i))).Order().ToList();
        Assert.Equal(hot.Order().ToList(), got);
    }

    private static IReadOnlyList<Extent> Ext(params (long V, long L, long N)[] e) => e.Select(x => new Extent(x.V, x.L, x.N)).ToList();

    private static AllocResult Alloc(IReadOnlyList<Unit> units, Dictionary<string, IReadOnlyList<Extent>?> files, long? start = 5000, long clusters = 1000,
        Dictionary<string, long>? sizes = null, long preserve = 8L << 20) =>
        ZoneAllocator.Allocate(units, files, start, clusters, Cb, Blk, sizes, preserve);

    [Fact]
    public void Allocate_PlacesInAccessOrderAndMergesAdjacent()
    {
        var files = new Dictionary<string, IReadOnlyList<Extent>?> { ["A"] = Ext((0, 100, 4 * Per)), ["B"] = Ext((0, 900, Per)) };

        AllocResult r = Alloc([new Unit("A", 0, 1), new Unit("A", 1, 1), new Unit("B", 0, 1)], files);

        Assert.Equal([new PlanTask("A", 0, 2 * Per, 5000), new PlanTask("B", 0, Per, 5000 + 2 * Per)], r.Tasks);
        Assert.Equal((2L, 3 * Blk, 1L, 2L), (r.Stats.Tasks, r.Stats.MovedBytes, r.Stats.ExpectedRuns, r.Stats.CurrentRuns));
    }

    [Fact]
    public void Allocate_FragmentedSourceIsOneTaskAndContiguousTarget()
    {
        var files = new Dictionary<string, IReadOnlyList<Extent>?> { ["A"] = Ext((0, 100, Per), (Per, 700, Per)) };

        AllocResult r = Alloc([new Unit("A", 0, 2)], files);

        Assert.Equal([new PlanTask("A", 0, 2 * Per, 5000)], r.Tasks);
        Assert.Equal((2L, 1L), (r.Stats.CurrentRuns, r.Stats.ExpectedRuns));
    }

    [Fact]
    public void Allocate_PieceAlreadyInPlaceIsNotMoved()
    {
        var files = new Dictionary<string, IReadOnlyList<Extent>?> { ["A"] = Ext((0, 5000, Per)), ["B"] = Ext((0, 900, Per)) };

        AllocResult r = Alloc([new Unit("A", 0, 1), new Unit("B", 0, 1)], files);

        Assert.Equal([new PlanTask("B", 0, Per, 5000 + Per)], r.Tasks);
        Assert.Equal(Blk, r.Stats.InPlaceBytes);
    }

    [Fact]
    public void Allocate_LargeContiguousRunIsPreserved()
    {
        const long big = 3 * 256;
        var files = new Dictionary<string, IReadOnlyList<Extent>?>
        {
            ["A"] = Ext((0, 100, big)),
            ["B"] = Ext((0, 100 + big, big)),
            ["C"] = Ext((0, 9000, Per)),
        };

        AllocResult r = Alloc([new Unit("A", 0, 48), new Unit("B", 0, 48), new Unit("C", 0, 1)], files, preserve: 4L << 20);

        Assert.Equal((1L, 6L << 20), (r.Stats.PreservedSegments, r.Stats.PreservedBytes));
        Assert.Equal(["C"], r.Tasks.Select(t => t.Path));
        Assert.Equal(2L, r.Stats.ExpectedRuns);
    }

    [Fact]
    public void Allocate_ZeroPreserveMovesEveryHotRange()
    {
        const long big = 3 * 256;
        var files = new Dictionary<string, IReadOnlyList<Extent>?>
        {
            ["A"] = Ext((0, 100, big)),
            ["B"] = Ext((0, 100 + big, big)),
            ["C"] = Ext((0, 9000, Per)),
        };
        Unit[] units = [new Unit("A", 0, 48), new Unit("B", 0, 48), new Unit("C", 0, 1)];

        AllocResult r = Alloc(units, files, clusters: 5000, preserve: 0);

        Assert.Equal((0L, 0L), (r.Stats.PreservedSegments, r.Stats.PreservedBytes));
        Assert.Equal(["A", "B", "C"], r.Tasks.Select(t => t.Path).Distinct());
        Assert.Equal(1L, r.Stats.ExpectedRuns);
        // 기본값도 보존 없음
        AllocResult byDefault = ZoneAllocator.Allocate(units, files, 5000, 5000, Cb, Blk);
        Assert.Equal(0L, byDefault.Stats.PreservedSegments);
        Assert.Equal(r.Tasks, byDefault.Tasks);
    }

    [Fact]
    public void PlanDefaultsPreserveNothing()
    {
        PlanFile viaDefault = PlanFile.Make(PlanSessions(), @"D:oot", 2L << 20, Cb, p => PlanFiles[p], p => PlanSizes[p], p => new Fingerprint(p, PlanSizes[p], 123));

        Assert.Equal(0L, viaDefault.Stats["preserved_segments"]);
        Assert.Equal(viaDefault.Stats["hot_bytes"], viaDefault.Stats["moved_bytes"]);
        Assert.Equal("0", AsterCache.Cli.Commands.PlanCommand.Options.Single(o => o.Name == "preserve-mib").Default);
    }

    [Fact]
    public void Allocate_OverflowKeepsPrefix()
    {
        var files = new Dictionary<string, IReadOnlyList<Extent>?> { ["A"] = Ext((0, 100, 3 * Per)) };

        AllocResult r = Alloc([new Unit("A", 0, 1), new Unit("A", 2, 1), new Unit("A", 1, 1)], files, clusters: 2 * Per);

        Assert.Equal([0L, 2 * Per], r.Tasks.Select(t => t.Vcn));
        Assert.Equal(Blk, r.Stats.OverflowBytes);
    }

    [Fact]
    public void Allocate_EndOfFileAndSizeClamp()
    {
        var files = new Dictionary<string, IReadOnlyList<Extent>?> { ["A"] = Ext((0, 100, 20)) }; // 할당 20 클러스터, 파일은 18 클러스터 + 1 바이트 -> 19 사용

        AllocResult r = Alloc([new Unit("A", 0, 2)], files, sizes: new Dictionary<string, long> { ["A"] = 18 * Cb + 1 });

        Assert.Equal([new PlanRange("A", 0, 19)], r.Order);
        Assert.Equal(19L, r.Tasks[0].Clusters);
    }

    [Fact]
    public void Allocate_DuplicateAndMissingFiles()
    {
        var files = new Dictionary<string, IReadOnlyList<Extent>?> { ["A"] = Ext((0, 100, Per)), ["S"] = null };

        AllocResult r = Alloc([new Unit("A", 0, 1), new Unit("A", 0, 1), new Unit("S", 0, 1), new Unit("Z", 0, 1)], files);

        Assert.Equal((1, 2L), (r.Order.Count, r.Stats.SkippedFiles));
    }

    [Fact]
    public void Allocate_SourcesInsideZoneGetDependencyOrder()
    {
        // B 의 목표가 A 가 있는 자리라 A 가 먼저 떠나야 합니다.
        var files = new Dictionary<string, IReadOnlyList<Extent>?> { ["B"] = Ext((0, 900, Per)), ["A"] = Ext((0, 5000, Per)) };

        AllocResult r = Alloc([new Unit("B", 0, 1), new Unit("A", 0, 1)], files);

        Assert.Equal(["A", "B"], r.Tasks.Select(t => t.Path));
    }

    [Fact]
    public void Allocate_CycleIsRefused()
    {
        var files = new Dictionary<string, IReadOnlyList<Extent>?> { ["X"] = Ext((0, 5000 + Per, Per)), ["A"] = Ext((0, 5000, Per)) };

        Assert.Throws<PyValueException>(() => Alloc([new Unit("X", 0, 1), new Unit("A", 0, 1)], files));
    }

    [Fact]
    public void Allocate_MisalignedPiecesCounted()
    {
        var files = new Dictionary<string, IReadOnlyList<Extent>?> { ["A"] = Ext((0, 101, Per)) };

        Assert.Equal(1L, Alloc([new Unit("A", 0, 1)], files).Stats.MisalignedPieces);
    }

    [Fact]
    public void Allocate_NoTasksGivesIntegerZeroAverage()
    {
        var files = new Dictionary<string, IReadOnlyList<Extent>?> { ["A"] = Ext((0, 5000, Per)) };

        AllocResult r = Alloc([new Unit("A", 0, 1)], files);

        Assert.Empty(r.Tasks);
        Assert.Equal(0L, r.Stats.ToJson()["avg_clusters_per_task"]);
    }

    // ---- 계획 파일

    private static readonly Dictionary<string, IReadOnlyList<Extent>?> PlanFiles = new()
    {
        ["f1"] = Ext((0, 1000, 64), (64, 3000, 64), (128, 2000, 64), (192, 5000, 64)),
        ["f2"] = Ext((0, 7000, 128), (128, 9000, 128)),
    };

    private static readonly Dictionary<string, long> PlanSizes = new() { ["f1"] = 1 << 20, ["f2"] = 1 << 20 };

    private static IReadOnlyList<IReadOnlyList<TraceEvent>> PlanSessions() =>
        Sessions(Run(("f1", 12), ("f2", 0), ("f1", 0), ("f1", 4), ("f2", 8)));

    private static PlanFile MakePlan(IReadOnlyList<IReadOnlyList<TraceEvent>> sessions, long? start = null) => PlanFile.Make(
        sessions, @"D:\root", 2L << 20, Cb, p => PlanFiles[p], p => PlanSizes[p], p => new Fingerprint(p, PlanSizes[p], 123), start,
        preserveBytes: 64L << 20);

    [Fact]
    public void PlanFile_MakeAndJsonRoundTrip()
    {
        PlanFile p = MakePlan(PlanSessions());
        string tmp = Path.Combine(Directory.CreateTempSubdirectory().FullName, "plan.json");
        try
        {
            p.Save(tmp);
            PlanFile back = PlanFile.Load(tmp);
            Assert.False(File.Exists(tmp + ".tmp"));

            Assert.Equal(p.Dumps(), back.Dumps());
            Assert.Equal(p.Sha(), back.Sha());
            Assert.Equal((5L, 1L), (p.Stats["hot_blocks"], p.Stats["expected_runs"]));
            Assert.Null(p.Zone.StartLcn);
            Assert.True((long)p.Stats["current_runs"]! > 1);
            Assert.Equal("D:", p.Volume);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(tmp)!, true);
        }
    }

    [Fact]
    public void PlanFile_PrefetchTraceIsExactlyTheZoneInZoneOrder()
    {
        IReadOnlyList<IReadOnlyList<TraceEvent>> sessions = Sessions(
            Run(("f1", 12), ("f2", 0), ("f1", 0), ("f1", 4), ("f2", 8)), Run(("f1", 12), ("f2", 0), ("f1", 0), ("f1", 4)), Run(("f2", 3)));
        PlanFile p = MakePlan(sessions);

        List<TraceEvent> rows = p.PrefetchTrace(sessions);

        Assert.Equal((long)p.Stats["hot_bytes"]!, p.Order.Sum(o => o.Clusters) * Cb);
        Assert.Equal(p.Stats["hot_bytes"], rows.Sum(r => r.Length));
        Assert.Equal(p.Order.Select(o => (o.Path, o.Vcn, o.Clusters)), rows.Select(r => (r.Path, r.Offset / Cb, r.Length / Cb)));
        Assert.All(rows, r => Assert.Equal(8.0, r.Cost));
    }

    [Fact]
    public void PlanFile_ResolveShiftsTargets()
    {
        PlanFile p = MakePlan(PlanSessions());

        PlanFile r = p.Resolve(777);

        Assert.Equal(777L, r.Zone.StartLcn);
        Assert.Equal(p.Tasks.Select(t => t.TargetLcn), r.Tasks.Select(t => t.TargetLcn - 777));
        Assert.Same(r, r.Resolve(5));
    }

    [Fact]
    public void PlanFile_MalformedPlanIsRejected()
    {
        string good = MakePlan(PlanSessions()).Dumps();
        Assert.Equal(good, PlanFile.FromJson(JsonRead.Parse(good)).Dumps());

        JObject Mutated(Action<JObject> edit)
        {
            var o = (JObject)JsonRead.Parse(good)!;
            edit(o);
            return o;
        }

        JObject[] bad =
        [
            Mutated(o => o["version"] = 9L),
            Mutated(o => o["tasks"] = new List<object?> { new JObject { { "path", 1L } } }),
            Mutated(o => o["tasks"] = new List<object?> { new JObject { { "path", "a" }, { "vcn", 0L }, { "clusters", 0L }, { "target_lcn", 0L } } }),
            Mutated(o => o["zone"] = new JObject { { "start_lcn", -3L }, { "clusters", 10L } }),
            Mutated(o => o["zone"] = new JObject { { "start_lcn", 5.0 }, { "clusters", 10L } }),
            Mutated(o => o["cluster_bytes"] = 0L),
            Mutated(o => o["stats"] = new List<object?>()),
            Mutated(o => o["fingerprints"] = new JObject { { "a", new List<object?> { 1L } } }),
            Mutated(o => o["volume"] = 5L),
        ];
        foreach (JObject b in bad)
        {
            Assert.Throws<PyValueException>(() => PlanFile.FromJson(b));
        }

        var noZone = (JObject)JsonRead.Parse(good)!;
        var copy = new JObject();
        foreach ((string k, object? v) in noZone.Where(kv => kv.Key != "zone"))
        {
            copy.Add(k, v);
        }

        Assert.Throws<PyValueException>(() => PlanFile.FromJson(copy));
        Assert.Throws<PyValueException>(() => PlanFile.FromJson(new List<object?>()));
    }

    [Theory]
    [InlineData(@"D:\AsterBench", "D:")]
    [InlineData("d:/Data/", "d:")]
    [InlineData(@"\\srv\share\Data", @"\\srv\share")]
    [InlineData(@"\\srv", "")]
    [InlineData(@"\\\x", "")]
    [InlineData("rel/path", "")]
    public void SplitDriveMatchesNtpath(string path, string expected) => Assert.Equal(expected, PlanFile.SplitDrive(path));
}
