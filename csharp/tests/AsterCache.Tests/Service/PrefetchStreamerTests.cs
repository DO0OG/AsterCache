using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class PrefetchStreamerTests : IDisposable
{
    private const long Mib = 1L << 20;
    private const long BatchBytes = 64 * Mib;
    private readonly string _root = Directory.CreateTempSubdirectory("aster_prefetch_stream_").FullName;
    private readonly GameSettings _game;

    public PrefetchStreamerTests()
    {
        string gameRoot = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameRoot);
        _game = Games.Make("stream", "stream.exe") with
        {
            OriginRoot = gameRoot,
            AllowedRoots = [gameRoot],
            Trace = Path.Combine(_root, "stream.trace.json"),
            Plan = Path.Combine(_root, "stream.plan.json"),
        };
    }

    public void Dispose() => Directory.Delete(_root, true);

    private void WriteTrace(long mib)
    {
        string path = Path.Combine(_game.OriginRoot, "map.bin");
        using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            file.SetLength(mib * Mib);
        }

        _ = TraceWriter.Write([[new TraceEvent(path, 0, mib * Mib)]], _game.Trace);
    }

    [Fact]
    public void CoordinatedStartStreamDoesNotDoPostBatchRefresh()
    {
        WriteTrace(8);
        int checks = 0;

        (var stats, bool complete) = new PrefetchStreamer().StreamCoordinated(_game, CancellationToken.None, null, () =>
        {
            checks++;
            return true;
        });

        Assert.True(complete);
        Assert.Equal(8 * Mib, stats.Bytes);
        Assert.Equal(3, checks);
    }

    [Fact]
    public void CoordinatedStartStreamRechecksBeforeEachBatch()
    {
        WriteTrace(65);
        int checks = 0;

        (var stats, bool complete) = new PrefetchStreamer().StreamCoordinated(_game, CancellationToken.None, null, () => ++checks == 1);

        Assert.False(complete);
        Assert.True(stats.Aborted);
        Assert.Equal(BatchBytes, stats.Bytes);
        Assert.True(checks >= 2);
    }
}
