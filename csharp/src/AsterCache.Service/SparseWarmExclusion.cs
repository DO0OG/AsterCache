using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 예열 상한이 모자라 메모리 예열에서 스파스 파일 범위를 뺀 게임 (GameSettings.SkipSparse) 의 읽기 목록 처리: 사다리는 스파스 범위를 만지지 못해 그 페이지가 가장 먼저 밀려나므로, 상한 안에서는 일반 파일 범위에 양을 씁니다.
/// 표시는 게임 사본에 실려 다니므로 다른 게임·다른 스레드의 계획에는 영향이 없습니다. 예열·표본·고정 목록에만 적용하고 시작 스트림·재배치 계획은 표시 없는 원래 게임을 씁니다.
internal static class SparseWarmExclusion
{
    /// 표시가 있는 게임이면 읽기 목록에서 스파스 파일의 구간을 뺍니다. keep (스파스 유지 블록 순위 행) 이 있으면 그 블록과 겹치는 구간만 남깁니다:
    /// 이 블록은 실행 중 스파스 유지가 버퍼드 읽기로 계속 새로 쓰므로 사다리가 만지지 못해 먼저 밀려난다는 제외 이유가 해당하지 않습니다.
    public static List<ReadOp> Without(GameSettings game, List<ReadOp> ops, IReadOnlyList<TraceEvent>? keep = null)
    {
        if (!game.SkipSparse)
        {
            return ops;
        }

        var sparse = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, List<(long Start, long End)>> spans = KeepSpans(keep);
        List<ReadOp> result = [];
        foreach (ReadOp op in ops)
        {
            if (!(sparse.TryGetValue(op.Path, out bool s) ? s : sparse[op.Path] = FileGuards.IsSparse(op.Path)))
            {
                result.Add(op);
            }
            else if (spans.TryGetValue(op.Path, out List<(long Start, long End)>? file))
            {
                foreach ((long start, long end) in file)
                {
                    long from = Math.Max(op.Offset, start);
                    long to = Math.Min(op.Offset + op.Length, end);
                    if (from < to)
                    {
                        result.Add(new ReadOp(op.Path, from, to - from));
                    }
                }
            }
        }

        return result;
    }

    // 파일별로 정렬해 이어진 구간을 합친 유지 블록 구간.
    private static Dictionary<string, List<(long Start, long End)>> KeepSpans(IReadOnlyList<TraceEvent>? keep)
    {
        var spans = new Dictionary<string, List<(long Start, long End)>>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, TraceEvent> file in (keep ?? []).Where(r => r.Length > 0).GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            var merged = new List<(long Start, long End)>();
            foreach (TraceEvent r in file.OrderBy(r => r.Offset))
            {
                if (merged.Count > 0 && r.Offset <= merged[^1].End)
                {
                    merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, r.Offset + r.Length));
                }
                else
                {
                    merged.Add((r.Offset, r.Offset + r.Length));
                }
            }

            spans[file.Key] = merged;
        }

        return spans;
    }
}
