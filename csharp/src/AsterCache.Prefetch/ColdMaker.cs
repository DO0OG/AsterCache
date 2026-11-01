using AsterCache.Core.Model;
using AsterCache.Core.Prefetch;
using AsterCache.Windows;

namespace AsterCache.Prefetch;

/// 비관리자 콜드 조건 (측정용). 스탠바이 목록 비우기 없이: 핫 범위를 언버퍼드로 읽어 캐시에서 내보내고,
/// 존 밖 데이터를 언버퍼드로 읽어 드라이브 캐시를 밀어냅니다. Python bench/stream_tune.py make_cold / cold_ops / read_unbuffered.
public static class ColdMaker
{
    private const long Mib = 1L << 20;
    private const long MaxPiece = 16 * Mib; // shortcut: 아주 큰 작업 하나가 버퍼를 통째로 잡지 않게 잘라 읽음 (Python 은 작업 단위 그대로)

    /// 핫 범위를 퇴출하고 flush 를 읽습니다. 읽기 실패는 그대로 던집니다.
    public static void Make(IReadOnlyList<ReadOp> ops, IReadOnlyList<ReadOp> flush)
    {
        ReadUnbuffered(ops);
        ReadUnbuffered(flush);
    }

    public static void ReadUnbuffered(IEnumerable<ReadOp> ops)
    {
        using var reader = new UnbufferedReader();
        foreach (ReadOp op in ops)
        {
            for (long done = 0; done < op.Length; done += MaxPiece)
            {
                reader.Read(op.Path, op.Offset + done, Math.Min(MaxPiece, op.Length - done));
            }
        }
    }

    /// ops 의 파일들에서 핫 작업들의 LCN 범위 밖 extent 만 골라 최대 mib MiB 를 1 MiB 단위로 (경로 순).
    public static List<ReadOp> ColdOps(IReadOnlyList<ReadOp> ops, int mib, Func<string, FileLayoutInfo?> layoutFn)
    {
        var lays = new SortedDictionary<string, FileLayoutInfo?>(AsterCache.Core.Json.CodePointComparer.Instance);
        foreach (ReadOp op in ops)
        {
            if (!lays.ContainsKey(op.Path))
            {
                lays[op.Path] = layoutFn(op.Path);
            }
        }

        var keys = new List<long>();
        foreach (ReadOp op in ops)
        {
            FileLayoutInfo? lay = lays[op.Path];
            if (lay is not null && FileLayoutInfo.PhysicalKey(lay.Extents, op.Offset, lay.ClusterBytes) is long k)
            {
                keys.Add(k);
            }
        }

        var output = new List<ReadOp>();
        if (keys.Count == 0)
        {
            return output;
        }

        long lo = keys.Min();
        long hi = keys.Max();
        long left = mib * Mib;
        foreach ((string path, FileLayoutInfo? lay) in lays)
        {
            if (lay is null)
            {
                continue; // Python 은 여기서 TypeError 로 죽음: 배치를 모르는 파일은 건너뜀
            }

            foreach (Extent e in lay.Extents)
            {
                if (e.Lcn <= hi && e.Lcn + e.Clusters >= lo)
                {
                    continue;
                }

                long total = e.Clusters * lay.ClusterBytes;
                for (long o = 0; o < total; o += Mib)
                {
                    long m = Math.Min(Math.Min(Mib, total - o), left);
                    if (m > 0)
                    {
                        output.Add(new ReadOp(path, (e.Vcn * lay.ClusterBytes) + o, m));
                        left -= m;
                    }
                }
            }
        }

        return output;
    }
}
