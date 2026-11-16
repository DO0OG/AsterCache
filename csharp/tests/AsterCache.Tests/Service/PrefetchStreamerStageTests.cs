using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Service;

/// 단계 색인 읽기 캐시: 원시 추적이 다시 쓰이면 색인 파일이 그대로여도 표식 검증을 다시 거쳐 낡은 단계 배정을 쓰지 않음.
public sealed class PrefetchStreamerStageTests : IDisposable
{
    private const long Mib = 1L << 20;
    private readonly string _root = Directory.CreateTempSubdirectory("aster_stage_index_").FullName;
    private readonly GameSettings _game;
    private readonly string _map;

    public PrefetchStreamerStageTests()
    {
        string gameRoot = Path.Combine(_root, "game");
        Directory.CreateDirectory(gameRoot);
        _game = Games.Make("stage", "stage.exe", "main.exe") with
        {
            OriginRoot = gameRoot,
            AllowedRoots = [gameRoot],
            Trace = Path.Combine(_root, "stage.trace.json"),
            Plan = Path.Combine(_root, "stage.plan.json"),
        };
        _map = Path.Combine(gameRoot, "map.bin");
        using (var file = new FileStream(_map, FileMode.Create, FileAccess.Write))
        {
            file.SetLength(8 * Mib);
        }
    }

    public void Dispose() => Directory.Delete(_root, true);

    private void WriteRaw() => _ = TraceWriter.Write([[new TraceEvent(_map, 0, 8 * Mib)]], _game.Trace);

    [Fact]
    public void CachedStageIndexIsRevalidatedWhenTheRawTraceIsRewrittenEvenIfTheIndexFileStays()
    {
        WriteRaw();
        string raw = AutoTracer.RawTracePath(_game);
        Assert.Equal(_game.Trace, raw);
        TraceStageIndex.Write(TraceStageIndex.IndexPath(raw), new TraceStageIndex(
        [
            new TraceStage("stage.exe", 0, [new TraceEvent(_map, 0, 4 * Mib)]),
            new TraceStage("main.exe", 5000, [new TraceEvent(_map, 4 * Mib, 4 * Mib)]),
        ]));
        var streamer = new PrefetchStreamer();

        StageLaterPlan before = streamer.PlanLaterStage(_game, "main.exe", CancellationToken.None); // 캐시를 채움
        Assert.True(before.HasStage);
        Assert.Equal([new ReadOp(_map, 4 * Mib, 4 * Mib)], before.Ops);

        WriteRaw(); // 색인 삭제·교체가 실패한 채 원시 추적만 다시 쓰임: 색인 파일은 그대로
        File.SetLastWriteTimeUtc(raw, File.GetLastWriteTimeUtc(raw).AddSeconds(30));

        StageLaterPlan after = streamer.PlanLaterStage(_game, "main.exe", CancellationToken.None);
        Assert.False(after.HasStage); // 낡은 색인은 단계 정보 없음으로
        Assert.Equal(8 * Mib, after.Ops.Sum(op => op.Length)); // 시작 목록 전체 (앞부분 조각으로 나뉠 수 있음)
    }
}
