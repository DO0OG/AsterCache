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

    // 저널에서 줄 번호 fromRow 이후에 끝난 (done) 단계의 조각. 계획 행 (planned) 은 어느 줄에 있어도 됨.
    private static List<(string Path, long Vcn, long Clusters)> Moved(IReadOnlyList<JObject> rows, int fromRow)
    {
        var planned = new Dictionary<long, (string, long, long)>();
        var moved = new List<(string, long, long)>();
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
                planned[idx] = (path, vcn, clusters);
            }
            else if (Equals(st, "done") && i >= fromRow && planned.TryGetValue(idx, out (string, long, long) piece))
            {
                moved.Add(piece);
            }
        }

        return moved;
    }

    /// current 에서 fromRow 줄 뒤에 끝난 조각의 (앞선 저널 previous 와 겹치는 바이트, 전체 바이트).
    public static (long Repeated, long Moved) Measure(IReadOnlyList<JObject> current, int fromRow, IReadOnlyList<JObject> previous, long clusterBytes)
    {
        ILookup<string, (long Vcn, long End)> before = Moved(previous, 0).ToLookup(m => m.Path, m => (m.Vcn, End: m.Vcn + m.Clusters), StringComparer.Ordinal);
        long repeated = 0, total = 0;
        foreach ((string path, long vcn, long clusters) in Moved(current, fromRow))
        {
            total += clusters;
            long overlap = 0;
            foreach ((long from, long end) in before[path])
            {
                overlap += Math.Max(0, Math.Min(end, vcn + clusters) - Math.Max(from, vcn));
            }

            repeated += Math.Min(overlap, clusters); // 앞선 저널의 조각끼리 겹쳐도 이 조각의 길이를 넘지 않음
        }

        return (repeated * clusterBytes, total * clusterBytes);
    }

    /// 되돌려진 조각을 다시 옮기는 회차인가: 옮긴 양이 MinBytes 이상이고 그 가운데 MinBytes 이상, MinShare 이상이 앞선 저널과 같음.
    public static bool IsRepeat(long repeated, long moved) => moved >= MinBytes && repeated >= MinBytes && repeated >= moved * MinShare;
}
