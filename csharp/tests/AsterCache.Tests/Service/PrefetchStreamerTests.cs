using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
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

    [Fact]
    public void PrewarmStreamHandsEachReadBatchToTheCallerRightAfterItsProgressNotice()
    {
        string[] files = [Path.Combine(_game.OriginRoot, "a.bin"), Path.Combine(_game.OriginRoot, "b.bin")];
        foreach (string file in files)
        {
            using var stream = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength(65 * Mib); // 묶음 크기 (64 MiB) 보다 큰 파일 둘: 묶음이 여러 개 나옴
        }

        _ = TraceWriter.Write([[new TraceEvent(files[0], 0, 65 * Mib), new TraceEvent(files[1], 0, 65 * Mib)]], _game.Trace);
        List<string> events = [];
        List<ReadOp[]> batches = [];

        (StreamStats stats, bool complete) = new PrefetchStreamer().StreamPrewarmCoordinated(_game, CancellationToken.None, (done, _) => events.Add("progress " + (done >> 20)), 200 * Mib, 0,
            () => true, allowReorder: false, null, batch =>
            {
                events.Add("batch");
                batches.Add([.. batch]);
            });

        Assert.True(complete);
        Assert.True(batches.Count >= 2);
        Assert.Equal(stats.Bytes, batches.Sum(b => b.Sum(o => o.Length))); // 묶음들이 스트림이 읽은 범위 전부
        Assert.Equal(files.Order(), batches.SelectMany(b => b).Select(o => o.Path).Distinct().Order());
        Assert.Equal("progress 0", events[0]);
        Assert.Equal(Enumerable.Repeat(new[] { "progress", "batch" }, batches.Count).SelectMany(x => x), events.Skip(1).Select(e => e.Split(' ')[0])); // 묶음마다 진행 알림 뒤에 곧바로 묶음
    }

    [Fact]
    public void CancelledPrewarmPlanningDoesNotPublishAReadRequest()
    {
        WriteTrace(8);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        int issued = 0;

        (StreamStats stats, bool complete) = new PrefetchStreamer().StreamPrewarmCoordinated(_game, stop.Token, null, 8 * Mib, 0,
            () => true, allowReorder: false, (_, _, _) => issued++);

        Assert.False(complete);
        Assert.True(stats.Aborted);
        Assert.Equal(0, issued);
    }
}
