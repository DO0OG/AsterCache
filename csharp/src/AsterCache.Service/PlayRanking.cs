using AsterCache.Core.Prefetch;
using AsterCache.Core.Trace;

namespace AsterCache.Service;

/// 플레이 전체 기록의 블록 순위 목록 (LoadRefillPlans.RankedRows) 을 예열 선택과 실행 중 유지가 쓰는 곳: 예산에 담을 행의 순서와, 유지가 만지는 범위의 순서.
/// 순위 목록이 없으면 호출하는 쪽이 이 클래스를 거치지 않으므로 지금 동작 그대로입니다.
internal static class PlayRanking
{
    /// 예산을 넘어 잘라야 할 때의 행 순서: 앞부분 예열 전용 (front) → tier0 (선읽기 행의 앞 tier0Bytes, 스케줄러가 접근 순서로 앞에 두는 만큼) → 순위 목록 → 나머지 선읽기 → 나머지 예열 전용 → 추측 행.
    /// 순위 행에서는 앞 단계와 겹치는 구간을 빼고, 뒤 단계 행에서는 순위 행과 겹치는 구간을 뺍니다 (같은 바이트가 예산을 두 번 쓰지 않게).
    /// 순위 행은 Rows[RankedFrom..RankedTo] 입니다.
    public static (List<TraceEvent> Rows, int RankedFrom, int RankedTo) Order(IReadOnlyList<TraceEvent> front, IReadOnlyList<TraceEvent> observed, IEnumerable<TraceEvent> warmOnly, IReadOnlyList<TraceEvent> guess,
        IReadOnlyList<TraceEvent> ranked, long tier0Bytes)
    {
        int head = 0;
        for (long taken = 0; head < observed.Count && taken < tier0Bytes; head++)
        {
            taken += observed[head].Length;
        }

        List<TraceEvent> first = [.. front, .. observed.Take(head)];
        var covered = new RangeIndex(first);
        List<TraceEvent> rankedNew = [.. ranked.SelectMany(covered.Subtract)];
        var rankedCover = new RangeIndex(rankedNew);
        List<TraceEvent> rows = [.. first, .. rankedNew, .. observed.Skip(head).Concat(warmOnly).Concat(guess).SelectMany(rankedCover.Subtract)];
        return (rows, first.Count, first.Count + rankedNew.Count);
    }

    /// 유지가 만지는 범위를 순위 순으로 다시 정렬합니다: 순위 목록에 든 64 KiB 블록은 순위 순 (이어진 블록은 한 범위로 합침), 나머지는 지금 순서로 그 뒤에.
    /// 읽기 범위 안의 블록만 다루므로 순위 목록이 예열하지 않은 구간을 끌어오지 않습니다. 순위 블록이 하나도 없으면 reads 를 그대로 돌려줍니다.
    public static IReadOnlyList<ReadOp> Reorder(IReadOnlyList<ReadOp> reads, IReadOnlyList<TraceEvent> ranked)
    {
        var rank = new Dictionary<string, Dictionary<long, int>>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < ranked.Count; i++)
        {
            if (!rank.TryGetValue(ranked[i].Path, out Dictionary<long, int>? blocks))
            {
                rank[ranked[i].Path] = blocks = [];
            }

            blocks[ranked[i].Offset / LoadBoundaries.Block] = i;
        }

        var hits = new List<(int Rank, ReadOp Piece)>();
        var rest = new List<ReadOp>();
        foreach (ReadOp op in reads)
        {
            rank.TryGetValue(op.Path, out Dictionary<long, int>? blocks);
            long end = op.Offset + op.Length;
            long restFrom = op.Offset; // 아직 rest 에 넣지 않은 순위 밖 구간의 시작
            for (long at = op.Offset; blocks is not null && at < end;)
            {
                long next = Math.Min(end, (at / LoadBoundaries.Block + 1) * LoadBoundaries.Block);
                if (blocks.TryGetValue(at / LoadBoundaries.Block, out int r))
                {
                    if (at > restFrom)
                    {
                        rest.Add(new ReadOp(op.Path, restFrom, at - restFrom));
                    }

                    hits.Add((r, new ReadOp(op.Path, at, next - at)));
                    restFrom = next;
                }

                at = next;
            }

            if (end > restFrom)
            {
                rest.Add(new ReadOp(op.Path, restFrom, end - restFrom));
            }
        }

        if (hits.Count == 0)
        {
            return reads;
        }

        hits.Sort((a, b) => a.Rank.CompareTo(b.Rank));
        var result = new List<ReadOp>(hits.Count + rest.Count);
        foreach ((_, ReadOp piece) in hits)
        {
            if (result.Count > 0 && result[^1] is { } last && string.Equals(last.Path, piece.Path, StringComparison.OrdinalIgnoreCase) && last.Offset + last.Length == piece.Offset)
            {
                result[^1] = last with { Length = last.Length + piece.Length };
            }
            else
            {
                result.Add(piece);
            }
        }

        result.AddRange(rest);
        return result;
    }

    /// 유지 회차·붙들기의 중요도 순서: Reorder 와 같되, 읽기 범위 (예열한 범위) 안에 든 유효한 블록 가운데 앞 frontBytes (스파스 파일 블록은 세지 않음) 를 경기 가중 점수 순으로 먼저 놓고 나머지는 순위 순입니다.
    /// 예산은 파일 대조를 통과한 행 (play 의 행은 이미 대조됨) 가운데 읽기 범위에 온전히 든 것에만 적용하므로, 바뀐 파일이나 예열하지 않은 블록이 예산을 쓰지 않습니다. 같은 블록은 한 번만 나옵니다. 정렬은 하지 않음 (점수 순서는 저장된 것).
    public static IReadOnlyList<ReadOp> PlayReorder(IReadOnlyList<ReadOp> reads, PlayRankedRows play, long frontBytes = LoadRefillPlanner.PlayFrontBytes)
    {
        var covered = new RangeIndex(reads.Select(r => new TraceEvent(r.Path, r.Offset, r.Length)));
        long left = Math.Max(0, frontBytes) / LoadBoundaries.Block;
        var front = new List<TraceEvent>();
        var taken = new HashSet<(string Path, long Block)>(KeyComparer.Instance);
        foreach (TraceEvent row in play.ByScore)
        {
            if (left <= 0)
            {
                break;
            }

            if (covered.Subtract(row).Any())
            {
                continue; // 예열한 읽기 범위 밖 (또는 일부만 안)
            }

            front.Add(row);
            taken.Add((row.Path, row.Offset / LoadBoundaries.Block));
            left -= play.Sparse.Contains(row.Path) ? 0 : 1;
        }

        return Reorder(reads, [.. front, .. play.Ranked.Where(r => !taken.Contains((r.Path, r.Offset / LoadBoundaries.Block)))]);
    }

    private sealed class KeyComparer : IEqualityComparer<(string Path, long Block)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string Path, long Block) a, (string Path, long Block) b) => a.Block == b.Block && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Path, long Block) k) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Path), k.Block);
    }

    /// 경로별로 정렬·병합한 구간: 행에서 이미 담은 구간을 빼는 데 씁니다.
    private sealed class RangeIndex
    {
        private readonly Dictionary<string, List<(long Start, long End)>> _spans = new(StringComparer.OrdinalIgnoreCase);

        public RangeIndex(IEnumerable<TraceEvent> rows)
        {
            foreach (IGrouping<string, TraceEvent> file in rows.Where(r => r.Length > 0).GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
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

                _spans[file.Key] = merged;
            }
        }

        /// 행에서 이 구간들과 겹치는 부분을 뺀 나머지 조각 (겹치지 않으면 행 그대로).
        public IEnumerable<TraceEvent> Subtract(TraceEvent row)
        {
            if (!_spans.TryGetValue(row.Path, out List<(long Start, long End)>? spans))
            {
                yield return row;
                yield break;
            }

            long at = row.Offset;
            long end = row.Offset + row.Length;
            int low = 0;
            for (int high = spans.Count; low < high;)
            {
                int mid = (low + high) / 2;
                if (spans[mid].End > at)
                {
                    high = mid;
                }
                else
                {
                    low = mid + 1;
                }
            }

            for (int i = low; i < spans.Count && spans[i].Start < end; i++)
            {
                if (spans[i].Start > at)
                {
                    yield return row with { Offset = at, Length = spans[i].Start - at };
                }

                at = Math.Max(at, spans[i].End);
            }

            if (at < end)
            {
                yield return row with { Offset = at, Length = end - at };
            }
        }
    }
}

/// 유지 회차·붙들기가 쓰는 순위 행 (Ranked: 지금 순위 순), 경기 가중 점수 순의 행 (ByScore), 스파스 파일 (Sparse). 모두 순위 파일에 저장된 것에서 만들며 파일 세대 대조를 거쳤습니다.
public sealed record PlayRankedRows(List<TraceEvent> Ranked, List<TraceEvent> ByScore, IReadOnlySet<string> Sparse);
