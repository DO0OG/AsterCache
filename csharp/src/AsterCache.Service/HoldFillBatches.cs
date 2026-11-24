using AsterCache.Core.Model;
using AsterCache.Core.Prefetch;

namespace AsterCache.Service;

/// 붙들 목록을 디스크에서 채울 때 한 번에 읽는 묶음을 만드는 순수 함수: 대상 집합 (중요도 순서에서 앞에서부터 예산만큼) 을 물리 위치 (LCN) 순으로 놓고, 맨 앞에서부터 물리적으로 이어진 한 구간만 떼어 냅니다.
/// 한 묶음은 탐색 한 번에 순차 전송이므로 게임의 읽기가 기다릴 최악의 시간이 그 묶음의 크기로 정해집니다 (아래 상수).
internal static class HoldFillBatches
{
    /// 실행 파일 시작으로 잡힌 로딩 중 한 묶음의 최대: 8 MiB. 근거: 하드디스크 순차 약 75 MiB/s (2026-11-24 이 PC 의 중앙값 78.9 MB/s) 에서 전송 약 110 ms 에 탐색 한 번을 더한 값이고, 이때는 게임이 디스크를 거의 읽지 않는 시작 직후입니다. 실측으로 고른 값이 아닙니다.
    public const long LoadingBatchBytes = 8L << 20;

    /// 그 밖 (로딩이 아니라 게임이 도는 중) 한 묶음의 최대, 그리고 물리 위치를 알 수 없는 범위를 자르는 크기: 1 MiB. 근거: 같은 속도에서 전송 약 15 ms 에 탐색 한 번을 더한 값. 실측으로 고른 값이 아닙니다.
    public const long PlayBatchBytes = 1L << 20;

    /// 대상 범위의 한 조각: 한 파일 안에서 물리적으로 이어진 부분. Lcn 은 디스크 위 바이트 위치 (모르면 null).
    public readonly record struct Piece(ReadOp Op, long? Lcn);

    /// targets 를 extent 경계에서 조각으로 나누고 물리 위치 순으로 놓습니다 (위치를 아는 조각이 앞, 모르는 조각은 원래 순서로 뒤). layout 은 파일의 물리 배치 (모르면 null).
    public static List<Piece> Order(IReadOnlyList<ReadOp> targets, Func<string, FileLayoutInfo?> layout)
    {
        var known = new List<(long Lcn, int Index, Piece Piece)>();
        var unknown = new List<Piece>();
        int index = 0;
        foreach (ReadOp op in targets.Where(o => o.Length > 0))
        {
            FileLayoutInfo? info = layout(op.Path);
            if (info is null || info.Extents.Count == 0 || info.ClusterBytes <= 0)
            {
                unknown.Add(new Piece(op, null));
                continue;
            }

            long cluster = info.ClusterBytes;
            long cursor = op.Offset;
            long end = op.Offset + op.Length;
            int at = FirstExtentEndingAfter(info.Extents, cursor / cluster);
            while (cursor < end)
            {
                if (at < info.Extents.Count && info.Extents[at].Vcn * cluster <= cursor && cursor < (info.Extents[at].Vcn + info.Extents[at].Clusters) * cluster)
                {
                    Extent e = info.Extents[at];
                    long take = Math.Min(end, (e.Vcn + e.Clusters) * cluster) - cursor;
                    known.Add(((e.Lcn * cluster) + (cursor - (e.Vcn * cluster)), index++, new Piece(new ReadOp(op.Path, cursor, take), (e.Lcn * cluster) + (cursor - (e.Vcn * cluster)))));
                    cursor += take;
                    at++;
                    continue;
                }

                // extent 밖 (스파스 구멍 등): 다음 extent 의 시작까지를 위치 모르는 조각으로 둠
                long next = at < info.Extents.Count && info.Extents[at].Vcn * cluster > cursor ? Math.Min(end, info.Extents[at].Vcn * cluster) : end;
                unknown.Add(new Piece(new ReadOp(op.Path, cursor, next - cursor), null));
                cursor = next;
            }
        }

        known.Sort((a, b) => a.Lcn != b.Lcn ? a.Lcn.CompareTo(b.Lcn) : a.Index.CompareTo(b.Index));
        return [.. known.Select(k => k.Piece), .. unknown];
    }

    /// ordered 맨 앞에서 물리적으로 이어진 한 구간을 maxBytes 까지 떼어 돌려주고 ordered 에서 그만큼 뺍니다. 위치를 모르는 조각은 PlayBatchBytes 로 자릅니다.
    public static List<ReadOp> TakeRun(List<Piece> ordered, long maxBytes)
    {
        List<ReadOp> run = [];
        if (ordered.Count == 0 || maxBytes <= 0)
        {
            return run;
        }

        if (ordered[0].Lcn is null)
        {
            Piece first = ordered[0];
            long take = Math.Min(first.Op.Length, Math.Min(maxBytes, PlayBatchBytes));
            run.Add(new ReadOp(first.Op.Path, first.Op.Offset, take));
            Advance(ordered, 0, take);
            return run;
        }

        long total = 0;
        long? nextLcn = null;
        while (ordered.Count > 0 && total < maxBytes)
        {
            Piece piece = ordered[0];
            if (piece.Lcn is not { } lcn || (nextLcn is { } expected && lcn != expected))
            {
                break; // 위치를 모르거나 물리적으로 이어지지 않음
            }

            long take = Math.Min(piece.Op.Length, maxBytes - total);
            run.Add(new ReadOp(piece.Op.Path, piece.Op.Offset, take));
            total += take;
            nextLcn = lcn + take;
            Advance(ordered, 0, take);
        }

        return run;
    }

    // ordered[i] 의 앞 take 바이트를 뺍니다 (남으면 나머지를 같은 자리에 남김).
    private static void Advance(List<Piece> ordered, int i, long take)
    {
        Piece p = ordered[i];
        if (take >= p.Op.Length)
        {
            ordered.RemoveAt(i);
            return;
        }

        ordered[i] = new Piece(new ReadOp(p.Op.Path, p.Op.Offset + take, p.Op.Length - take), p.Lcn is { } l ? l + take : null);
    }

    // VCN 순 extent 목록에서 vcn 을 덮거나 그 뒤에 처음 오는 extent 의 위치 (이진 탐색).
    private static int FirstExtentEndingAfter(IReadOnlyList<Extent> extents, long vcn)
    {
        int lo = 0;
        int hi = extents.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >> 1;
            if (extents[mid].Vcn + extents[mid].Clusters <= vcn)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid;
            }
        }

        return lo;
    }
}
