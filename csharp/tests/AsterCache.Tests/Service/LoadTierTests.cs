using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class LoadTierTests : IDisposable
{
    private const long Mib = 1 << 20;
    private readonly string _dir = Directory.CreateTempSubdirectory("astercache-tier-").FullName;
    private readonly GameSettings _game;

    public LoadTierTests()
    {
        string root = Path.Combine(_dir, "game");
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "a.bin"), new byte[8 * Mib]);
        _game = Games.Make("x", "x.exe") with { Plan = Path.Combine(_dir, "x.plan.json"), Trace = Path.Combine(_dir, "x.prefetch.json"), OriginRoot = root, AllowedRoots = [root] };
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private TraceEvent Read(long sec, long atMib, bool timed = true) => new(Path.Combine(_game.OriginRoot, "a.bin"), atMib * Mib, Mib, TMs: timed ? sec * 1000 : null);

    private void WriteRaw(params TraceEvent[] events) => TraceWriter.Write([events], AutoTracer.RawTracePath(_game));

    [Fact]
    public void Tier0BytesReplaceTheFixedHead()
    {
        WriteRaw([.. Enumerable.Range(0, 5).Select(s => Read(s, s)), Read(40, 5)]); // 0..4 초 1 MiB 씩, 40 초에 하나 더

        Assert.Equal(5 * Mib, PrefetchStreamer.HeadBytesOf(_game));
        Assert.Equal(5 * Mib, new PrefetchStreamer().LoadHeadBytes(_game));
        Assert.Equal(5_000, LoadTiers.For(_game)!.BoundaryMs);
    }

    [Fact]
    public void RawTraceWithoutTimesOrMissingKeepsTheOld256MibHead()
    {
        Assert.Equal(PrefetchStreamer.HeadBytes, PrefetchStreamer.HeadBytesOf(_game)); // 원시 트레이스 없음
        Assert.Null(new PrefetchStreamer().LoadHeadBytes(_game));

        WriteRaw([.. Enumerable.Range(0, 5).Select(s => Read(s, s, timed: false))]); // 옛 파일: t_ms 없음

        Assert.Null(LoadTiers.For(_game));
        Assert.Equal(PrefetchStreamer.HeadBytes, PrefetchStreamer.HeadBytesOf(_game));
        Assert.Equal(256 * Mib, PrefetchStreamer.HeadBytes);
    }

    [Fact]
    public void RewrittenRawTraceIsRecomputed()
    {
        WriteRaw(Read(0, 0));
        Assert.Equal(Mib, PrefetchStreamer.HeadBytesOf(_game));

        File.Delete(AutoTracer.RawTracePath(_game));
        WriteRaw(Read(0, 0), Read(1, 1), Read(2, 2));

        Assert.Equal(3 * Mib, PrefetchStreamer.HeadBytesOf(_game));
    }

    [Fact]
    public void RawTraceKeepsTimesThroughWriteAndLoadAndOldFilesStillLoad()
    {
        WriteRaw(Read(0, 0), Read(3, 1));

        List<TraceEvent> loaded = TraceLoader.LoadRanges(AutoTracer.RawTracePath(_game), _game.OriginRoot);
        Assert.Equal(32, loaded.Count); // 합집합은 64 KiB 블록마다 한 줄, 블록을 처음 읽은 시각을 가짐
        Assert.Equal(16, loaded.Count(e => e.TMs == 0));
        Assert.Equal(16, loaded.Count(e => e.TMs == 3000));

        string old = Path.Combine(_dir, "old.json"); // t_ms 가 없는 옛 형식 (Python 이 쓴 모양)
        File.WriteAllText(old, "[{\"path\": " + PyJson.Dumps(Path.Combine(_game.OriginRoot, "a.bin")) + ", \"offset\": 0, \"length\": 4096, \"cost\": 8.0}]\r\n");
        Assert.Null(Assert.Single(TraceLoader.LoadRanges(old, _game.OriginRoot)).TMs);
    }
}
