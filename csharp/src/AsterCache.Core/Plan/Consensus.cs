using AsterCache.Core.Json;
using AsterCache.Core.Trace;

namespace AsterCache.Core.Plan;

/// 연속한 블록 묶음: Path 의 블록 [Start, Start + Count).
public readonly record struct Unit(string Path, long Start, long Count);

/// relayout/plan.py consensus_order 와 _make_units.
public static class Consensus
{
    /// b 바로 뒤에 b+1 이 b 가중치의 thetaCore 이상으로 이어지면 한 단위로 묶습니다.
    internal static List<Unit> MakeUnits(IReadOnlyCollection<BlockKey> hot, List<List<BlockKey>> runs, double[] w, double thetaCore)
    {
        var adj = new Dictionary<BlockKey, double>();
        var pres = new Dictionary<BlockKey, double>();
        var hotSet = new HashSet<BlockKey>(hot);
        for (int k = 0; k < runs.Count; k++)
        {
            List<BlockKey> r = runs[k];
            for (int i = 0; i < r.Count; i++)
            {
                BlockKey key = r[i];
                if (!hotSet.Contains(key))
                {
                    continue;
                }

                pres[key] = pres.GetValueOrDefault(key) + w[k];
                if (i + 1 < r.Count && r[i + 1] == new BlockKey(key.Path, key.Block + 1) && hotSet.Contains(r[i + 1]))
                {
                    adj[key] = adj.GetValueOrDefault(key) + w[k];
                }
            }
        }

        var sorted = hotSet.ToList();
        sorted.Sort();
        var units = new List<Unit>();
        foreach (BlockKey key in sorted)
        {
            var prev = new BlockKey(key.Path, key.Block - 1);
            if (units.Count > 0 && units[^1].Path == key.Path && units[^1].Start + units[^1].Count == key.Block
                && Ratio(adj.GetValueOrDefault(prev), pres.GetValueOrDefault(prev)) >= thetaCore)
            {
                units[^1] = units[^1] with { Count = units[^1].Count + 1 };
            }
            else
            {
                units.Add(new Unit(key.Path, key.Block, 1));
            }
        }

        return units;
    }

    private static double Ratio(double num, double den) =>
        den == 0 ? throw new DivideByZeroException("float division by zero") : num / den;

    /// 핫 블록의 물리 배치 순서 하나. 단위 만들기, 중앙 위치 정렬, 가중 다수가 원하면 이웃 맞바꾸기 (최대 maxPasses 회).
    public static List<Unit> Order(IReadOnlyList<IReadOnlyList<TraceEvent>> sessions, IReadOnlyCollection<BlockKey> hotBlocks,
        long block = HotSet.Block, double decay = HotSet.DefaultDecay, double thetaCore = HotSet.ThetaCore, int maxPasses = 64)
    {
        List<List<BlockKey>> runs = HotSet.Runs(sessions, block);
        List<Dictionary<BlockKey, double>> pos = HotSet.Positions(runs);
        double[] w = HotSet.Weights(runs.Count, decay);
        List<Unit> units = MakeUnits(hotBlocks, runs, w, thetaCore);

        double Median(Unit u)
        {
            var key = new BlockKey(u.Path, u.Start);
            var pairs = new List<(double Value, double Weight)>();
            for (int i = 0; i < pos.Count; i++)
            {
                if (pos[i].TryGetValue(key, out double p))
                {
                    pairs.Add((p, w[i]));
                }
            }

            return pairs.Count == 0 ? throw new IndexOutOfRangeException("hot block never seen") : HotSet.WMedian(pairs);
        }

        // x 가 y 보다 먼저 읽힌 실행들의 가중치 합
        double Pref(Unit x, Unit y)
        {
            var kx = new BlockKey(x.Path, x.Start);
            var ky = new BlockKey(y.Path, y.Start);
            double sum = 0;
            for (int i = 0; i < pos.Count; i++)
            {
                if (pos[i].TryGetValue(kx, out double px) && pos[i].TryGetValue(ky, out double py) && px < py)
                {
                    sum += w[i];
                }
            }

            return sum;
        }

        var keyed = units.Select(u => (Unit: u, M: Median(u))).ToList();
        keyed.Sort((a, b) =>
        {
            int c = a.M.CompareTo(b.M);
            if (c != 0)
            {
                return c;
            }

            c = CodePointComparer.Compare(a.Unit.Path, b.Unit.Path);
            if (c != 0)
            {
                return c;
            }

            c = a.Unit.Start.CompareTo(b.Unit.Start);
            return c != 0 ? c : a.Unit.Count.CompareTo(b.Unit.Count);
        });
        units = keyed.Select(k => k.Unit).ToList();
        for (int pass = 0; pass < maxPasses; pass++)
        {
            bool swapped = false;
            for (int i = 0; i < units.Count - 1; i++)
            {
                Unit x = units[i];
                Unit y = units[i + 1];
                if (Pref(y, x) > Pref(x, y))
                {
                    units[i] = y;
                    units[i + 1] = x;
                    swapped = true;
                }
            }

            if (!swapped)
            {
                break;
            }
        }

        return units;
    }
}
