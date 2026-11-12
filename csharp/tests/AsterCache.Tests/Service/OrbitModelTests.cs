using AsterCache.Core.Json;
using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class OrbitModelTests : IDisposable
{
    private const long Block = 65536;
    private const string Common = @"D:\Games\Synthetic\shared.bin";
    private const string Map = @"D:\Games\Synthetic\map.bin";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_orbit_" + Guid.NewGuid().ToString("N"));
    private readonly GameSettings _game;
    private readonly OrbitModel _model = new();

    public OrbitModelTests()
    {
        Directory.CreateDirectory(_dir);
        string plan = Path.Combine(_dir, "foo.plan.json");
        _game = new GameSettings("foo", ["foo.exe"], plan + ".prefetch.json", plan, @"D:\Games\Synthetic", [@"D:\Games\Synthetic"], "GAMES", 1440);
    }

    public void Dispose()
    {
        _model.Dispose();
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void BuildsRepeatedEpisodesExcludesCommonAndSplitsAtTheGapBoundary()
    {
        WriteRuns();
        TracePhases.Recount(_game, 0);

        Assert.True(OrbitModel.Build(_game, 0, TimeSpan.FromSeconds(5), CancellationToken.None));

        JObject root = (JObject)JsonRead.ReadFile(OrbitModel.PathFor(_game))!;
        List<object?> episodes = (List<object?>)root["episodes"]!;
        Assert.Equal(0.0, Number(root["unmatched"]));
        Assert.Equal(4, episodes.Count);
        var first = (JObject)episodes[0]!;
        var second = (JObject)episodes[1]!;
        Assert.Equal(2L, first["runs_seen"]);
        Assert.Equal(3L, first["runs_total"]);
        Assert.Equal(2250.0, Number(first["d_ms"]));
        Assert.Equal(4450.0, Number(second["d_ms"]));
        OrbitModelSnapshot loaded = OrbitModel.Load(_dir, "foo", 0);
        Assert.Equal(3, loaded.Runs);
        Assert.Equal(4, loaded.Episodes.Count);
        Assert.Equal(new long[] { 10, 11, 12, 13 }, EpisodeBlocks(loaded.Episodes[0]).Select(block => block.Block));
        Assert.Equal(new long[] { 20, 21, 22, 23 }, EpisodeBlocks(loaded.Episodes[1]).Select(block => block.Block));
        Assert.DoesNotContain(loaded.Episodes.SelectMany(EpisodeBlocks), block => block.Block == 0);
        Assert.Equal(2250.0, loaded.Episodes[0].DMs);
        Assert.Equal(4450.0, loaded.Episodes[1].DMs);
        Assert.Equal((double?)0, loaded.UnmatchedRatio);
    }

    [Fact]
    public void BuildWritesCompactVersionTwoRangesAndRoundTripsThem()
    {
        WriteRuns();
        TracePhases.Recount(_game, 0);

        Assert.True(OrbitModel.Build(_game, 0, TimeSpan.FromSeconds(5), CancellationToken.None));

        JObject root = (JObject)JsonRead.ReadFile(OrbitModel.PathFor(_game))!;
        Assert.Equal(2L, root["version"]);
        Assert.NotEmpty((List<object?>)root["files"]!);
        List<object?> rows = (List<object?>)root["episodes"]!;
        Assert.All(rows, row =>
        {
            JObject episode = (JObject)row!;
            Assert.False(episode.ContainsKey("blocks"));
            Assert.NotEmpty((List<object?>)episode["ranges"]!);
        });
        List<object?> firstRanges = (List<object?>)((JObject)rows[0]!)["ranges"]!;
        List<object?> firstRange = (List<object?>)Assert.Single(firstRanges)!;
        Assert.Equal(10L, firstRange[1]);
        Assert.Equal(4L, firstRange[2]);

        OrbitModelSnapshot loaded = OrbitModel.Load(_dir, "foo", 0);
        Assert.Equal(new long[] { 10, 11, 12, 13 }, EpisodeBlocks(loaded.Episodes[0]).Select(block => block.Block));
        Assert.Equal(new long[] { 20, 21, 22, 23 }, EpisodeBlocks(loaded.Episodes[1]).Select(block => block.Block));
        Assert.Same(loaded, OrbitModel.Load(_dir, "foo", 0));

        OrbitModel.ForgetSnapshots([_game]);

        Assert.NotSame(loaded, OrbitModel.Load(_dir, "foo", 0));
    }

    [Fact]
    public void LegacyVersionOneRangesLoadAndUpgradeWithoutLosingEpisodes()
    {
        WriteRuns();
        TracePhases.Recount(_game, 0);
        Assert.True(OrbitModel.Build(_game, 0, TimeSpan.FromSeconds(5), CancellationToken.None));
        string path = OrbitModel.PathFor(_game);
        JObject root = (JObject)JsonRead.ReadFile(path)!;
        if (root.ContainsKey("files"))
        {
            List<object?> files = (List<object?>)root["files"]!;
            var legacyEpisodes = new List<object?>();
            foreach (JObject episode in ((List<object?>)root["episodes"]!).Cast<JObject>())
            {
                var blocks = new List<object?>();
                foreach (object? item in (List<object?>)episode["ranges"]!)
                {
                    List<object?> range = (List<object?>)item!;
                    string file = (string)files[checked((int)(long)range[0]!)]!;
                    long start = (long)range[1]!;
                    long count = (long)range[2]!;
                    for (long block = start; block < start + count; block++)
                    {
                        blocks.Add(new List<object?> { file, block });
                    }
                }

                legacyEpisodes.Add(new JObject
                {
                    { "blocks", blocks },
                    { "runs_seen", episode["runs_seen"] },
                    { "runs_total", episode["runs_total"] },
                    { "d_ms", episode["d_ms"] },
                });
            }

            var legacy = new JObject
            {
                { "version", 1L },
                { "inputs", root["inputs"] },
                { "episodes", legacyEpisodes },
            };
            if (root.TryGetValue("unmatched", out object? unmatched))
            {
                legacy.Add("unmatched", unmatched);
            }

            if (root.TryGetValue("hazard", out object? hazard))
            {
                legacy.Add("hazard", hazard);
            }

            if (root.TryGetValue("decision", out object? decision))
            {
                legacy.Add("decision", decision);
            }

            root = legacy;
        }

        File.WriteAllText(path, PyJson.Dumps(root, indent: 2) + "\n", PyJson.Utf8);

        OrbitModelSnapshot loaded = OrbitModel.Load(_dir, "foo", 0);
        Assert.Equal(new long[] { 10, 11, 12, 13 }, EpisodeBlocks(loaded.Episodes[0]).Select(block => block.Block));

        OrbitModel.SetDecision(_game, "legacy-preserved", DateTimeOffset.UnixEpoch, "shadow");

        JObject upgraded = (JObject)JsonRead.ReadFile(path)!;
        Assert.Equal(2L, upgraded["version"]);
        Assert.Equal("legacy-preserved", ((JObject)upgraded["decision"]!)["id"]);
        Assert.Equal(new long[] { 10, 11, 12, 13 }, EpisodeBlocks(OrbitModel.Load(_dir, "foo", 0).Episodes[0]).Select(block => block.Block));
    }

    [Fact]
    public void GapJustUnderThresholdKeepsReadsTogether()
    {
        for (int run = 1; run <= 3; run++)
        {
            WriteRun(run, [Read(Map, 10, 0), Read(Map, 11, 1999)]);
        }

        Assert.True(OrbitModel.Build(_game, 0, TimeSpan.FromSeconds(5), CancellationToken.None));

        OrbitModelSnapshot loaded = OrbitModel.Load(_dir, "foo", 0);
        OrbitEpisode episode = Assert.Single(loaded.Episodes);
        Assert.Equal(2, EpisodeBlocks(episode).Count());
    }

    [Fact]
    public void InvalidRangeRowDoesNotDiscardItsRun()
    {
        WriteRun(1, [Read(Common, 0, 0), Read(Map, 10, 100), new TraceEvent(Map, -Block, Block, TMs: 150)]);
        WriteRun(2, [Read(Common, 0, 0), Read(Map, 11, 100)]);
        WriteRun(3, [Read(Common, 0, 0), Read(Map, 12, 100)]);
        TracePhases.Recount(_game, 0);

        Assert.True(OrbitModel.Build(_game, 0, TimeSpan.FromSeconds(5), CancellationToken.None));

        OrbitModelSnapshot loaded = OrbitModel.Load(_dir, "foo", 0);
        Assert.Equal(3, loaded.Runs);
        Assert.Equal(3, loaded.Episodes.Count);
        Assert.DoesNotContain(loaded.Episodes.SelectMany(EpisodeBlocks), block => block.Block < 0);
    }

    [Fact]
    public void LoadUsesStoredModelWhileRunFilesAreLocked()
    {
        WriteRuns();
        TracePhases.Recount(_game, 0);
        Assert.True(OrbitModel.Build(_game, 0, TimeSpan.FromSeconds(5), CancellationToken.None));

        using var locked = new FileStream(TraceWriter.RunPath(AutoTracer.RawTracePath(_game), 1), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        OrbitModelSnapshot loaded = OrbitModel.Load(_dir, "foo", 0);

        Assert.Equal(3, loaded.Runs);
        Assert.Equal(4, loaded.Episodes.Count);
        Assert.Equal((double?)0, loaded.UnmatchedRatio);
    }

    [Fact]
    public async Task StopWaitsForCanceledWriteToRemoveTheTemporaryFile()
    {
        WriteRuns();
        using var tempReady = new ManualResetEventSlim();
        using var canceled = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        string model = OrbitModel.PathFor(_game);
        string temp = model + ".tmp";

        _model.Schedule(_game, new ListLog(), 0, token =>
        {
            tempReady.Set();
            Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)));
            canceled.Set();
            release.Wait();
        });

        Assert.True(tempReady.Wait(TimeSpan.FromSeconds(5)));
        Task dispose = Task.Run(_model.Dispose);
        try
        {
            Assert.True(canceled.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(dispose.IsCompleted);
        }
        finally
        {
            release.Set();
        }

        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(temp));
        Assert.False(File.Exists(model));

        using var rejected = new ManualResetEventSlim();
        _model.Schedule(_game, new ListLog(), 0, _ => rejected.Set());
        Assert.False(rejected.Wait(TimeSpan.FromMilliseconds(400)));

        Directory.Delete(_dir, true);
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void ScheduledBuildCanBeCanceledBeforeWriting()
    {
        WriteRuns();
        _model.Schedule(_game, new ListLog(), 0);
        _model.CancelGame(_game.Name);
        Thread.Sleep(400);

        Assert.False(File.Exists(OrbitModel.PathFor(_game)));
    }

    [Fact]
    public void FewerThanThreeValidRunsAndBrokenRunsProduceNoEpisodeModel()
    {
        WriteRun(1, [Read(Common, 0, 0), Read(Map, 10, 100)]);
        WriteRun(2, [Read(Common, 0, 0), Read(Map, 10, 200)]);
        TracePhases.Recount(_game, 0);
        Assert.True(OrbitModel.Build(_game, 0, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Empty((List<object?>)((JObject)JsonRead.ReadFile(OrbitModel.PathFor(_game))!)["episodes"]!);
        Assert.Empty(OrbitModel.Load(_dir, "foo", 0).Episodes);

        WriteRuns();
        File.WriteAllText(TraceWriter.RunPath(AutoTracer.RawTracePath(_game), 4), "{ broken");
        TracePhases.Recount(_game, 0);
        Assert.True(OrbitModel.Build(_game, 0, TimeSpan.FromSeconds(5), CancellationToken.None));
        OrbitModelSnapshot loaded = OrbitModel.Load(_dir, "foo", 0);
        Assert.Equal(3, loaded.Runs);
        Assert.Equal(5, loaded.Episodes.Count);
    }

    [Fact]
    public void CorruptModelLoadsNoEpisodesAndARebuildReplacesIt()
    {
        WriteRuns();
        TracePhases.Recount(_game, 0);
        Assert.True(OrbitModel.Build(_game, 0, TimeSpan.FromSeconds(5), CancellationToken.None));

        File.WriteAllText(OrbitModel.PathFor(_game), "{ broken");
        Assert.Empty(OrbitModel.Load(_dir, "foo", 0).Episodes);

        Assert.True(OrbitModel.Build(_game, 0, TimeSpan.FromSeconds(5), CancellationToken.None));
        Assert.Equal(4, OrbitModel.Load(_dir, "foo", 0).Episodes.Count);
    }

    [Fact]
    public void ExposureAndDecisionSurviveModelRebuilds()
    {
        WriteRuns();
        TracePhases.Recount(_game, 0);
        Assert.True(OrbitModel.Build(_game, 0, TimeSpan.FromSeconds(5), CancellationToken.None));
        OrbitModel.NoteExposure(_game, 10, interrupted: true);
        OrbitModel.NoteExposure(_game, 3, interrupted: false);
        DateTimeOffset at = DateTimeOffset.Parse("2026-11-09T03:04:05+00:00");
        OrbitModel.SetDecision(_game, "decision-1", at, "active");
        Assert.Equal((double?)0, OrbitModel.Load(_dir, "foo", 0).UnmatchedRatio);

        WriteRun(4, [Read(Common, 0, 0), Read(Map, 10, 3500), Read(Map, 11, 3600), Read(Map, 20, 5600)]);
        TracePhases.Recount(_game, 0);
        Assert.True(OrbitModel.Build(_game, 0, TimeSpan.FromSeconds(5), CancellationToken.None));

        JObject root = (JObject)JsonRead.ReadFile(OrbitModel.PathFor(_game))!;
        JObject hazard = (JObject)root["hazard"]!;
        JObject decision = (JObject)root["decision"]!;
        Assert.Equal(1L, hazard["events"]);
        Assert.Equal(13.0, Number(hazard["exposure_s"]));
        Assert.True(root.ContainsKey("unmatched"));
        Assert.Equal("decision-1", decision["id"]);
        Assert.Equal("active", decision["mode_applied"]);
        Assert.Equal(at.ToUniversalTime().ToString("O"), decision["at"]);

        OrbitModelSnapshot loaded = OrbitModel.Load(_dir, "foo", 0);
        Assert.NotNull(loaded.LambdaHat);
        Assert.InRange(Math.Abs(loaded.LambdaHat!.Value - 1.0 / 13000), 0, 1e-12);
        Assert.NotNull(loaded.LambdaLow);
        Assert.NotNull(loaded.LambdaHigh);
    }

    [Fact]
    public void CancellationAndExpiredBudgetDoNotWriteTheModelFile()
    {
        WriteRuns();
        TracePhases.Recount(_game, 0);
        Assert.False(OrbitModel.Build(_game, 0, TimeSpan.Zero, CancellationToken.None));

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.False(OrbitModel.Build(_game, 0, TimeSpan.FromSeconds(5), stop.Token));
        Assert.False(File.Exists(OrbitModel.PathFor(_game)));
    }

    private void WriteRuns()
    {
        WriteRun(1, [Read(Common, 0, 0), Read(Map, 10, 2000), Read(Map, 11, 2100), Read(Map, 12, 2200), Read(Map, 20, 4200), Read(Map, 21, 4300), Read(Map, 22, 4400)]);
        WriteRun(2, [Read(Common, 0, 0), Read(Map, 10, 2500), Read(Map, 11, 2600), Read(Map, 13, 2700), Read(Map, 20, 4700), Read(Map, 21, 4800), Read(Map, 23, 4900)]);
        WriteRun(3, [Read(Common, 0, 0), Read(Map, 12, 3000), Read(Map, 13, 3100), Read(Map, 14, 3200), Read(Map, 22, 5200), Read(Map, 23, 5300), Read(Map, 24, 5400)]);
    }

    private void WriteRun(int number, IReadOnlyList<TraceEvent> events) =>
        TraceWriter.WriteFile(TraceWriter.RunPath(AutoTracer.RawTracePath(_game), number), events);

    private static TraceEvent Read(string path, long block, long time) => new(path, block * Block, Block, TMs: time);

    private static IEnumerable<OrbitBlock> EpisodeBlocks(OrbitEpisode episode)
    {
        foreach (OrbitBlock block in episode.Blocks)
        {
            yield return block;
        }

        foreach (OrbitBlockRange range in episode.Ranges)
        {
            for (long block = range.StartBlock; block < range.StartBlock + range.Count; block++)
            {
                yield return new OrbitBlock(range.Path, block);
            }
        }
    }

    private static double Number(object? value) => value switch
    {
        long integer => integer,
        double number => number,
        _ => throw new InvalidOperationException("not a number"),
    };
}
