using AsterCache.Core.Json;
using AsterCache.Core.Trace;

namespace AsterCache.Core.Plan;

/// (경로, 블록 번호). 정렬은 Python 튜플 비교 (경로는 코드 포인트 순서).
public readonly record struct BlockKey(string Path, long Block) : IComparable<BlockKey>
{
    public int CompareTo(BlockKey other)
    {
        int c = CodePointComparer.Compare(Path, other.Path);
        return c != 0 ? c : Block.CompareTo(other.Block);
    }
}

/// hot_set 통계: f 가중 빈도, m 가중 중앙 위치, s 순서 안정도, score 점수.
public readonly record struct BlockStat(double F, double M, double S, double Score);

/// relayout/plan.py hot_set 과 그 보조 함수. 실수 합은 Python 3.11 처럼 왼쪽부터 순서대로 더합니다.
public static class HotSet
{
    public const long Block = 65536;
    public const double DefaultDecay = 0.85;
    public const double ThetaCore = 0.5;
    public const double ThetaTail = 0.2;

    /// 세션마다 처음 접근한 순서의 고유 블록 목록.
    internal static List<List<BlockKey>> Runs(IReadOnlyList<IReadOnlyList<TraceEvent>> sessions, long block)
    {
        if (sessions.Count == 0 || sessions.Count > 32)
        {
            throw new PyValueException("expect 1..32 sessions");
        }

        var runs = new List<List<BlockKey>>(sessions.Count);
        foreach (IReadOnlyList<TraceEvent> session in sessions)
        {
            var seen = new HashSet<BlockKey>();
            var order = new List<BlockKey>();
            foreach (BlockEvent e in TraceEvents.Validated(session, block))
            {
                for (long b = e.First; b <= e.Last; b++)
                {
                    var key = new BlockKey(e.Path, b);
                    if (seen.Add(key))
                    {
                        order.Add(key);
                    }
                }
            }

            runs.Add(order);
        }

        return runs;
    }

    internal static double[] Weights(int k, double decay)
    {
        var w = new double[k];
        for (int i = 0; i < k; i++)
        {
            w[i] = Math.Pow(decay, k - 1 - i);
        }

        return w;
    }

    /// 가중 중앙값, 동률이면 아래쪽. 입력은 (값, 가중치) 쌍.
    internal static double WMedian(List<(double Value, double Weight)> pairs)
    {
        pairs.Sort((a, b) =>
        {
            int c = a.Value.CompareTo(b.Value);
            return c != 0 ? c : a.Weight.CompareTo(b.Weight);
        });
        double total = 0;
        foreach ((double _, double w) in pairs)
        {
            total += w;
        }

        double half = total / 2.0;
        double acc = 0;
        foreach ((double v, double w) in pairs)
        {
            acc += w;
            if (acc >= half)
            {
                return v;
            }
        }

        return pairs[^1].Value;
    }

    /// 세션마다 블록 -> 정규화한 첫 접근 위치 [0, 1).
    internal static List<Dictionary<BlockKey, double>> Positions(List<List<BlockKey>> runs)
    {
        var result = new List<Dictionary<BlockKey, double>>(runs.Count);
        foreach (List<BlockKey> r in runs)
        {
            var d = new Dictionary<BlockKey, double>(r.Count);
            for (int i = 0; i < r.Count; i++)
            {
                d[r[i]] = (double)i / r.Count;
            }

            result.Add(d);
        }

        return result;
    }

    /// 핫 블록 선택. 반환: 선택 순서 (core 를 점수순, 이어서 tail) 의 블록과 모든 후보의 통계.
    public static (List<BlockKey> Blocks, Dictionary<BlockKey, BlockStat> Stats) Select(
        IReadOnlyList<IReadOnlyList<TraceEvent>> sessions, long budgetBytes, long block = Block, double decay = DefaultDecay,
        double thetaCore = ThetaCore, double thetaTail = ThetaTail, ISet<BlockKey>? contiguous = null, int minSessions = 1)
    {
        if (budgetBytes < 0 || block < 4096 || (block & (block - 1)) != 0)
        {
            throw new PyValueException("invalid budget/block");
        }

        List<List<BlockKey>> runs = Runs(sessions, block);
        List<Dictionary<BlockKey, double>> pos = Positions(runs);
        double[] w = Weights(runs.Count, decay);
        double totalW = 0;
        foreach (double x in w)
        {
            totalW += x;
        }

        var seenIn = new Dictionary<BlockKey, int>();
        foreach (List<BlockKey> r in runs)
        {
            foreach (BlockKey key in r)
            {
                seenIn[key] = seenIn.GetValueOrDefault(key) + 1;
            }
        }

        var stats = new Dictionary<BlockKey, BlockStat>();
        foreach ((BlockKey key, int n) in seenIn)
        {
            if (n < minSessions)
            {
                continue;
            }

            var obs = new List<(double Value, double Weight)>();
            for (int i = 0; i < pos.Count; i++)
            {
                if (pos[i].TryGetValue(key, out double p))
                {
                    obs.Add((p, w[i]));
                }
            }

            double fsum = 0;
            foreach ((double _, double wk) in obs)
            {
                fsum += wk;
            }

            double f = fsum / totalW;
            double m = WMedian([.. obs]);
            double mad = WMedian(obs.Select(o => (Math.Abs(o.Value - m), o.Weight)).ToList());
            double s = 1.0 - Math.Min(1.0, 2.0 * mad);
            double score = f * (0.5 + 0.5 * s) * (contiguous is not null && contiguous.Contains(key) ? 0.2 : 1.0);
            stats[key] = new BlockStat(f, m, s, score);
        }

        int Rank(BlockKey a, BlockKey b)
        {
            BlockStat x = stats[a];
            BlockStat y = stats[b];
            int c = (-x.Score).CompareTo(-y.Score);
            if (c != 0)
            {
                return c;
            }

            c = x.M.CompareTo(y.M);
            return c != 0 ? c : a.CompareTo(b);
        }

        var core = stats.Keys.Where(k => stats[k].F >= thetaCore).ToList();
        var tail = stats.Keys.Where(k => stats[k].F >= thetaTail && stats[k].F < thetaCore).ToList();
        core.Sort(Rank);
        tail.Sort(Rank);
        core.AddRange(tail);
        int take = (int)Math.Min(core.Count, budgetBytes / block);
        return (core.GetRange(0, take), stats);
    }
}
