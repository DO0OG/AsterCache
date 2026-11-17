namespace AsterCache.Core.Plan;

/// <summary>
/// 선정의 단위입니다. 한 블록 안에서 조각이 끊긴 구간들은 함께 옮겨야 하므로 한 단위로 묶고,
/// 묶음 안의 이음 이득은 단위를 고르면 항상 실현되는 SelfGain 으로 둡니다.
/// </summary>
internal sealed class LinkUnit
{
    public long Bytes { get; set; }

    public bool Selectable { get; set; }

    public double SelfGain { get; set; }

    /// <summary>앞 단위와 바로 이어 읽힙니다.</summary>
    public bool LinkedFromPrevious { get; set; }

    /// <summary>앞 단위와의 이음이 실현될 때(두 단위를 모두 고를 때)의 이득입니다.</summary>
    public double LinkGain { get; set; }
}

/// <param name="Selected">고른 단위.</param>
/// <param name="Lambda">고른 집합을 낸 λ(예산 안에 드는 쪽 끝). 후보 전체를 고른 경우 0.</param>
/// <param name="LowerLambda">예산을 넘는 쪽 끝. 탐색을 하지 않았으면 0.</param>
/// <param name="InitialUpper">탐색을 시작한 위 끝(1 + 이득 합). 탐색을 하지 않았으면 0.</param>
/// <param name="Searched">λ 이분 탐색을 했는가(후보 전체가 예산을 넘을 때만 true).</param>
internal sealed record LinkGainSolution(bool[] Selected, double Lambda, double LowerLambda, double InitialUpper, bool Searched);

/// <summary>
/// 이음 이득 선정의 동적 계획법입니다. 바이트당 값 λ 에 대해 "실현되는 이음 이득의 합 - λ × 고른 바이트" 를 가장 크게 하는 집합을
/// 앞에서 한 번 훑어 정확히 구하고, λ 를 고정 횟수로 이분 탐색해 고른 바이트가 예산 이하인 가장 큰 집합을 냅니다.
/// </summary>
internal static class LinkGainSolver
{
    public const int Iterations = 48;

    public static LinkGainSolution Solve(IReadOnlyList<LinkUnit> units, long budgetBytes)
    {
        long selectableBytes = 0;
        foreach (LinkUnit unit in units.Where(unit => unit.Selectable))
        {
            selectableBytes = checked(selectableBytes + unit.Bytes);
        }

        // 예산이 후보 전체보다 크면 후보 전체를 고릅니다.
        if (selectableBytes <= budgetBytes)
        {
            return new LinkGainSolution([.. units.Select(unit => unit.Selectable)], 0, 0, 0, false);
        }

        double lo = 0;
        double hi = 1 + units.Sum(unit => unit.SelfGain + (unit.LinkedFromPrevious ? unit.LinkGain : 0));
        double initialUpper = hi;
        bool[] best = new bool[units.Count];
        double bestLambda = hi;
        for (int step = 0; step < Iterations; step++)
        {
            double mid = lo + (hi - lo) / 2;
            bool[] set = Best(units, mid);
            if (Bytes(units, set) <= budgetBytes)
            {
                hi = mid;
                best = set;
                bestLambda = mid;
            }
            else
            {
                lo = mid;
            }
        }

        return new LinkGainSolution(best, bestLambda, lo, initialUpper, true);
    }

    public static long Bytes(IReadOnlyList<LinkUnit> units, bool[] selected)
    {
        long bytes = 0;
        for (int i = 0; i < units.Count; i++)
        {
            if (selected[i])
            {
                bytes = checked(bytes + units[i].Bytes);
            }
        }

        return bytes;
    }

    /// <summary>고른 집합에서 실현되는 이음 이득의 합입니다.</summary>
    public static double Gain(IReadOnlyList<LinkUnit> units, bool[] selected)
    {
        double gain = 0;
        for (int i = 0; i < units.Count; i++)
        {
            if (!selected[i])
            {
                continue;
            }

            gain += units[i].SelfGain;
            if (i > 0 && selected[i - 1] && units[i].LinkedFromPrevious)
            {
                gain += units[i].LinkGain;
            }
        }

        return gain;
    }

    /// <summary>주어진 λ 에서 "이득 - λ × 바이트" 가 가장 큰 집합입니다. 값이 같으면 고르지 않는 쪽을 택해 결과가 항상 같습니다.</summary>
    internal static bool[] Best(IReadOnlyList<LinkUnit> units, double lambda)
    {
        int n = units.Count;
        double[] inValue = new double[n];
        double[] outValue = new double[n];
        bool[] inFromIn = new bool[n];
        bool[] outFromIn = new bool[n];
        for (int i = 0; i < n; i++)
        {
            double previousOut = i == 0 ? 0 : outValue[i - 1];
            double previousIn = i == 0 ? double.NegativeInfinity : inValue[i - 1];
            outFromIn[i] = previousIn > previousOut;
            outValue[i] = Math.Max(previousOut, previousIn);
            if (!units[i].Selectable)
            {
                inValue[i] = double.NegativeInfinity;
                continue;
            }

            double viaIn = previousIn + (units[i].LinkedFromPrevious ? units[i].LinkGain : 0);
            inFromIn[i] = viaIn > previousOut;
            inValue[i] = Math.Max(previousOut, viaIn) + units[i].SelfGain - lambda * units[i].Bytes;
        }

        bool[] selected = new bool[n];
        bool state = n > 0 && inValue[n - 1] > outValue[n - 1];
        for (int i = n - 1; i >= 0; i--)
        {
            selected[i] = state;
            state = state ? inFromIn[i] : outFromIn[i];
        }

        return selected;
    }
}

public static partial class RelocationEstimate
{
    /// <summary>세 기준이 같이 쓰는 후보 조건: 이동 가능하고 정책상 제외가 아니며 점유 길이가 있는 블록입니다.</summary>
    private static bool IsSelectable(RelocationEstimateBlock block) =>
        block.Classification == EstimateBlockClass.Movable && block.PolicyCandidate != false && block.OccupiedBytes > 0;

    private static RelocationEstimateSelection SelectByLinkGain(
        IReadOnlyList<RelocationEstimateBlock> blocks,
        IReadOnlyList<RelocationEstimateBlock> line,
        RelocationEstimateTotals total,
        long zoneMib,
        IReadOnlyDictionary<string, RelocationEstimateZoneStart> starts,
        RelocationEstimateModel model,
        long diskBytes,
        RelocationEstimateModelMetrics currentModel,
        Dictionary<(long Zone, string Criterion), Dictionary<BlockKey, RelocationEstimatePhysicalExtent>>? placements)
    {
        long zoneBytes = checked(zoneMib * Mib);
        var selected = new HashSet<BlockKey>(new BlockKeyComparer());
        double realized = 0;
        List<ReadSegment> segments = RelocationEstimateSegments.Build(line, IsSelectable);
        foreach (IGrouping<string, ReadSegment> volume in segments.GroupBy(segment => segment.VolumeKey, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            List<(LinkUnit Unit, List<ReadSegment> Segments)> units = BuildUnits([.. volume], model, diskBytes);
            LinkUnit[] plain = [.. units.Select(item => item.Unit)];
            LinkGainSolution solution = LinkGainSolver.Solve(plain, zoneBytes);
            realized += LinkGainSolver.Gain(plain, solution.Selected);
            for (int i = 0; i < units.Count; i++)
            {
                if (!solution.Selected[i])
                {
                    continue;
                }

                foreach (RelocationEstimateBlock block in units[i].Segments.SelectMany(segment => segment.Blocks))
                {
                    selected.Add(new BlockKey(block.Path, block.BlockNumber));
                }
            }
        }

        return Describe(blocks, line, total, zoneMib, "link_gain", selected, starts, model, diskBytes, currentModel, placements, realized);
    }

    private static List<(LinkUnit Unit, List<ReadSegment> Segments)> BuildUnits(
        IReadOnlyList<ReadSegment> segments, RelocationEstimateModel model, long diskBytes)
    {
        var units = new List<(LinkUnit Unit, List<ReadSegment> Segments)>();
        for (int k = 0; k < segments.Count; k++)
        {
            ReadSegment segment = segments[k];
            double gain = 0;
            if (segment.LinkedFromPrevious)
            {
                ReadSegment previous = segments[k - 1];
                long distance = RelocationEstimateSegments.LinkDistance(previous, segment);
                // 조각이 셋 이상 끊긴 블록의 가운데 조각들은 센 블록이 없으므로 그 블록의 가중치를 씁니다.
                int counted = previous.BlockCount + segment.BlockCount;
                double weight = counted > 0 ? (previous.WeightSum + segment.WeightSum) / counted : segment.Blocks[0].Weight;
                gain = weight * model.PositionMs(distance, diskBytes);
            }

            if (segment.LinkedFromPrevious && segment.BreakInsideBlock && units.Count > 0)
            {
                (LinkUnit unit, List<ReadSegment> members) = units[^1];
                unit.Bytes = checked(unit.Bytes + segment.Bytes);
                unit.SelfGain += gain;
                unit.Selectable &= segment.Selectable;
                members.Add(segment);
                continue;
            }

            units.Add((new LinkUnit
            {
                Bytes = segment.Bytes,
                Selectable = segment.Selectable,
                LinkedFromPrevious = segment.LinkedFromPrevious,
                LinkGain = gain,
            }, [segment]));
        }

        return units;
    }
}
