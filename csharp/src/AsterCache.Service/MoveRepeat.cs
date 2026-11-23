using AsterCache.Core.Json;

namespace AsterCache.Service;

/// 이번 회차가 옮긴 조각 가운데 앞선 저널에서 이미 같은 파일의 같은 범위 (VCN·길이) 를 옮겼던 양을 셉니다.
/// 드라이브 최적화 같은 다른 도구가 옮긴 조각을 되돌리면 서비스가 같은 조각을 다시 옮기는 일이 되풀이되는데, 그것을 알아보는 근거입니다.
public static class MoveRepeat
{
    /// 되풀이로 볼 회차의 최소 바이트와 비율 (옮긴 양 대비), 이 회차가 이어지는 횟수.
    public const long MinBytes = 64L << 20;
    public const double MinShare = 0.5;
    public const int RoundsToRest = 2;

    private sealed record Piece(string Path, long Vcn, long Clusters, long? Target, List<(long Vcn, long Lcn, long N)>? From);

    // 저널에서 줄 번호 fromRow 이후에 끝난 (done) 단계의 조각. 계획 행 (planned) 은 어느 줄에 있어도 됨.
    private static List<Piece> Moved(IReadOnlyList<JObject> rows, int fromRow)
    {
        var planned = new Dictionary<long, Piece>();
        var moved = new List<Piece>();
        for (int i = 0; i < rows.Count; i++)
        {
            JObject r = rows[i];
            if (!r.TryGetValue("type", out object? t) || !Equals(t, "step") || !r.TryGetValue("idx", out object? x) || x is not long idx || !r.TryGetValue("state", out object? st))
            {
                continue;
            }

            if (Equals(st, "planned") && r.TryGetValue("path", out object? p) && p is string path && r.TryGetValue("vcn", out object? v) && v is long vcn
                && r.TryGetValue("clusters", out object? c) && c is long clusters && clusters > 0)
            {
                planned[idx] = new Piece(path, vcn, clusters, r.TryGetValue("target_lcn", out object? tl) && tl is long target ? target : null, Segments(r));
            }
            else if (Equals(st, "done") && i >= fromRow && planned.TryGetValue(idx, out Piece? piece))
            {
                moved.Add(piece);
            }
        }

        return moved;
    }

    // planned 행의 from (출발 구간 [vcn, lcn, 길이] 목록), 없거나 모양이 다르면 null.
    private static List<(long, long, long)>? Segments(JObject row)
    {
        if (!row.TryGetValue("from", out object? f) || f is not List<object?> list)
        {
            return null;
        }

        var result = new List<(long, long, long)>();
        foreach (object? o in list)
        {
            if (o is not List<object?> { Count: 3 } s || s[0] is not long v || s[1] is not long l || s[2] is not long n)
            {
                return null;
            }

            result.Add((v, l, n));
        }

        return result;
    }

    // 이 조각의 클러스터 가운데 앞선 저널의 조각 q 가 놓았던 바로 그 자리에서 떠난 것이 아닌 것 (겹치는 구간만): 서비스가 놓은 자리에서 새 zone 으로 일부러 옮기는 경우 (업데이트·트레이스 증가로 다시 계획) 는 세지 않고,
    // 다른 도구가 옮겨 놓은 것을 다시 옮기는 경우만 셈. 출발 자리나 앞선 목표가 저널에 없으면 알 수 없으므로 겹치는 만큼 셈.
    private static long RepeatedClusters(Piece cur, Piece q)
    {
        long a = Math.Max(cur.Vcn, q.Vcn), b = Math.Min(cur.Vcn + cur.Clusters, q.Vcn + q.Clusters);
        if (b <= a)
        {
            return 0;
        }

        if (cur.From is null || q.Target is not { } target)
        {
            return b - a;
        }

        long count = 0;
        foreach ((long sv, long sl, long sn) in cur.From)
        {
            long from = Math.Max(a, sv), to = Math.Min(b, sv + sn);
            if (to > from && sl + (from - sv) != target + (from - q.Vcn))
            {
                count += to - from; // 겹치는 구간의 출발 자리가 앞선 목표와 다름 (같은 구간은 선형이라 첫 클러스터만 비교하면 됨)
            }
        }

        return count;
    }

    /// current 에서 fromRow 줄 뒤에 끝난 조각의 (앞선 저널 previous 가 이미 옮겼던 범위를 그 저널이 놓은 자리가 아닌 곳에서 다시 옮긴 바이트, 전체 바이트).
    public static (long Repeated, long Moved) Measure(IReadOnlyList<JObject> current, int fromRow, IReadOnlyList<JObject> previous, long clusterBytes)
    {
        ILookup<string, Piece> before = Moved(previous, 0).ToLookup(m => m.Path, StringComparer.Ordinal);
        long repeated = 0, total = 0;
        foreach (Piece piece in Moved(current, fromRow))
        {
            total += piece.Clusters;
            repeated += Math.Min(before[piece.Path].Sum(q => RepeatedClusters(piece, q)), piece.Clusters); // 앞선 저널의 조각끼리 겹쳐도 이 조각의 길이를 넘지 않음
        }

        return (repeated * clusterBytes, total * clusterBytes);
    }

    /// 되돌려진 조각을 다시 옮기는 회차인가: 옮긴 양이 MinBytes 이상이고 그 가운데 MinBytes 이상, MinShare 이상이 앞선 저널과 같음.
    public static bool IsRepeat(long repeated, long moved) => moved >= MinBytes && repeated >= MinBytes && repeated >= moved * MinShare;
}
