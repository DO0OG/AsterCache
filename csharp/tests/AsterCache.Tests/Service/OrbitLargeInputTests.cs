using System.Diagnostics;
using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;
using Xunit.Abstractions;

namespace AsterCache.Tests.Service;

public sealed class OrbitLargeInputTests(ITestOutputHelper output) : IDisposable
{
    private const long BlockBytes = 64L << 10;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_orbit_large_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void DecisionPathFinishesForTwentySixEpisodesAndThreeHundredThousandBlocks()
    {
        Directory.CreateDirectory(_dir);
        string[] paths = Enumerable.Range(0, 602).Select(index => $@"D:\Games\Synthetic\asset-{index:D3}.bin").ToArray();
        var ops = new List<ReadOp>(14_206);
        for (int index = 0; index < 14_206; index++)
        {
            long offset = (long)(index / paths.Length) * 16 * BlockBytes;
            ops.Add(new ReadOp(paths[index % paths.Length], offset, 16 * BlockBytes));
        }

        var episodes = new List<OrbitEpisode>(26);
        for (int episodeIndex = 0; episodeIndex < 26; episodeIndex++)
        {
            int blockCount = episodeIndex == 0 ? 58_987 : 9_904 + (episodeIndex <= 8 ? 1 : 0);
            var ranges = new List<OrbitBlockRange>((blockCount + 7) / 8);
            for (int index = 0; index < blockCount;)
            {
                int range = index / 8;
                string path = paths[range % paths.Length];
                long startBlock = (long)(range / paths.Length) * 16;
                int count = Math.Min(8, blockCount - index);
                ranges.Add(new OrbitBlockRange(path, startBlock, count));
                index += count;
            }

            episodes.Add(new OrbitEpisode([], 0.4, 0.6, 0.8, 100) { Ranges = ranges });
        }

        long blockTotal = episodes.Sum(episode => episode.Ranges.Sum(range => range.Count));
        Assert.Equal(26, episodes.Count);
        Assert.Equal(306_595, blockTotal);
        Assert.Equal(14_206, ops.Count);

        var snapshot = new OrbitModelSnapshot(episodes, 0.00001, 0.00003, 0.00006, 4, 0);
        var game = new GameSettings("synthetic", ["game.exe"], Path.Combine(_dir, "synthetic.prefetch.json"),
            Path.Combine(_dir, "synthetic.plan.json"), @"D:\Games\Synthetic", [@"D:\Games\Synthetic"], "GAMES", 60);
        var runtime = new OrbitRuntime(_dir, null, null, new ListLog(), device: _ =>
            new DeviceFit(10, 100, 19, 19, new DeviceEnvelope(0.5, 2, 0.5, 2, OrbitEnvelope.Version)),
            headBytes: _ => 256L << 20, loadModel: (_, _) => snapshot);

        var clock = Stopwatch.StartNew();
        OrbitRuntime.ForcedCandidate result = runtime.ForceCandidate(game, ops, 2.0);
        double elapsedMs = clock.Elapsed.TotalMilliseconds;
        output.WriteLine($"episodes={episodes.Count} blocks={blockTotal} ops={ops.Count} candidate_ms={elapsedMs:F1} reason={result.Reason}");

        Assert.Equal("forced_candidate", result.Reason);
        Assert.NotNull(result.Ops);
        Assert.True(elapsedMs < Orbit3Constants.DecisionBudgetMs, $"Candidate path took {elapsedMs:F1} ms");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }
}
