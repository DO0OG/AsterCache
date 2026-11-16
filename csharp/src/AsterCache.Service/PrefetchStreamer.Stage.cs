using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;

namespace AsterCache.Service;

/// 단계 스트림이 읽을 목록: 현재 시작 목록 (Ops) 과 병합 추적의 단계 색인을 교집합합니다 (읽는 집합은 늘 시작 목록의 부분집합, 가변 파일 제외).
public sealed partial class PrefetchStreamer
{
    private sealed record StageIndexCacheEntry(long Length, long LastWriteTicks, long RawLength, long RawLastWriteTicks, TraceStageIndex? Index);

    private static readonly object StageIndexLock = new();
    private static readonly Dictionary<string, StageIndexCacheEntry> StageIndexCache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ReadOp>? PlanFirstStage(GameSettings game, CancellationToken stop)
    {
        if (StageIndexOf(game) is not { Stages.Count: >= 2 } index)
        {
            return null;
        }

        return StageStreamPlans.First(Ops(game, stop), HeadBytesOf(game, stop), index);
    }

    public IReadOnlyList<ReadOp>? StartOps(GameSettings game, CancellationToken stop) => Ops(game, stop);

    public StageLaterPlan PlanLaterStage(GameSettings game, string exe, CancellationToken stop)
    {
        List<ReadOp> ops = Ops(game, stop);
        return StageStreamPlans.Later(ops, StageIndexOf(game), exe, VolatilePaths(game, ops));
    }

    public (StreamStats Stats, bool Complete) StreamOpsCoordinated(GameSettings game, IReadOnlyList<ReadOp> ops, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue)
    {
        noteIssue?.Invoke("stream schedule", 0, false);
        return Batches([.. ops], stop, progress, canIssue, noteIssue); // 지금 스트림과 같은 버퍼드 묶음 경로 (스파스 파일에 섹션을 만들지 않음)
    }

    // 추적에서 가변으로 표시된 파일 (이름 규칙 포함): 게임이 쓰거나 줄이는 파일이라 실행 중에 읽지 않음.
    private static HashSet<string> VolatilePaths(GameSettings game, IReadOnlyList<ReadOp> ops)
    {
        IReadOnlyDictionary<string, string> marked = AutoTracer.ReadVolatile(AutoTracer.VolatilePath(game));
        return new HashSet<string>(ops.Select(op => op.Path).Where(path => IsVolatilePath(path, marked)), StringComparer.OrdinalIgnoreCase);
    }

    // 색인 파일과 원시 추적의 길이·수정 시각이 같으면 파싱한 색인을 재사용 (원시 추적이 바뀌면 색인에 적힌 표식 검증을 다시 거침). 파일이 없거나 읽을 수 없으면 null (단계 정보 없음).
    private static TraceStageIndex? StageIndexOf(GameSettings game)
    {
        string path = TraceStageIndex.IndexPath(AutoTracer.RawTracePath(game));
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            var raw = new FileInfo(AutoTracer.RawTracePath(game));
            long rawLength = raw.Exists ? raw.Length : -1;
            long rawTicks = raw.Exists ? raw.LastWriteTimeUtc.Ticks : -1;

            lock (StageIndexLock)
            {
                if (StageIndexCache.TryGetValue(path, out StageIndexCacheEntry? hit) && hit.Length == info.Length && hit.LastWriteTicks == info.LastWriteTimeUtc.Ticks && hit.RawLength == rawLength && hit.RawLastWriteTicks == rawTicks)
                {
                    return hit.Index;
                }
            }

            TraceStageIndex? index = TraceStageIndex.Read(path);
            lock (StageIndexLock)
            {
                StageIndexCache[path] = new StageIndexCacheEntry(info.Length, info.LastWriteTimeUtc.Ticks, rawLength, rawTicks, index);
            }

            return index;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
