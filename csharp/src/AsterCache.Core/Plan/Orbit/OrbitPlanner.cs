using System.Numerics;
using System.Text;

namespace AsterCache.Core.Plan.Orbit;

public sealed record PlanningBlock(string Key, long Size, double Position, double Frequency);

public sealed record PredictedEpisode(
    IReadOnlyList<string> Requires,
    double Predicted,
    double PredictedPartial);

public sealed record PlanningWorld(
    IReadOnlyList<PlanningBlock> Blocks,
    IReadOnlyList<PredictedEpisode> Episodes,
    BigInteger Budget,
    double Timebox,
    double TransferUnitsPerMs,
    double SeekMs,
    double SeekGap,
    double InterruptionHazard,
    double ModelConfidence,
    bool DiskIdle = true,
    bool DiskKnown = true,
    bool OtherGameActive = false,
    bool BaselineReadAllowed = true,
    string Signature = "synthetic");

public sealed record Uncertainty(
    double ValueLow,
    double ValueHigh,
    double PartialError,
    string CalibrationVersion);

public sealed record DeviceEnvelope(
    double SeekMin,
    double SeekMax,
    double ThroughputMin,
    double ThroughputMax,
    string CalibrationVersion);

public sealed record OrbitPlan(
    IReadOnlyList<string> Order,
    double ElapsedMs,
    long BytesUnits,
    double PredictedProxy,
    string Source);

public sealed record OrbitDecision(
    OrbitPlan Plan,
    string Selected,
    string Reason,
    double? MinimumAdvantage,
    double PredictionAdvantage,
    int Candidates = 0,
    int Eligible = 0);

public static class OrbitPlanner
{
    public const int MaxBlocks = 24;
    public const int MaxEpisodes = 96;
    public const int MaxDependencies = 384;
    public const int MaxWidth = 8;
    public const int MaxExpansions = 480;
    public const int MaxDevicePartitionK = 8;

    private const long MaxBlockSize = 1_000_000_000_000L;
    private const double TimeboxToleranceMs = 1e-8;
    private const double MaxSafePredictedTotal = 1e300;
    private const double ScoreTolerance = 1e-10;

    private sealed record State(int[] Order, long Mask, long Used, double Elapsed, double Score, double? Head);

    private sealed record Ranked(double Lower, double Improvement, OrbitPlan Plan);

    private static OrbitPlan RejectedPlan() => new(new string[0], 0, 0, 0, "ORBIT_R_rejected");

    public static OrbitDecision Choose(
        PlanningWorld? world,
        Uncertainty? uncertainty = null,
        DeviceEnvelope? device = null,
        int width = 8,
        int maxExpansions = 360,
        int partitionK = 4,
        bool candidateSet = true,
        CancellationToken cancellation = default)
    {
        if (!ValidWorld(world))
        {
            return Decision(RejectedPlan(), "none", "invalid_or_unsafe_input", null, 0);
        }

        if (width is < 1 or > MaxWidth || maxExpansions is < 1 or > MaxExpansions ||
            partitionK is < 1 or > MaxDevicePartitionK)
        {
            return Decision(RejectedPlan(), "none", "invalid_complexity_budget", null, 0);
        }

        try
        {
            PlanningWorld validWorld = world!;
            Dictionary<string, int> blockIndex = BuildIndex(validWorld);
            List<OrbitPlan> baselines =
            [
                Greedy(validWorld, blockIndex, "LFU"),
                Greedy(validWorld, blockIndex, "physical"),
                Greedy(validWorld, blockIndex, "marginal"),
                BundleGreedy(validWorld, blockIndex)
            ];
            OrbitPlan baseline = BestBaseline(baselines);
            CheckPlan(validWorld, baseline, blockIndex);

            if (!ValidUncertainty(uncertainty))
            {
                return Decision(baseline, "baseline", "uncalibrated_model", null, 0);
            }

            if (!ValidDevice(device))
            {
                return Decision(baseline, "baseline", "uncalibrated_device", null, 0);
            }

            if (!ValidDerivedDeviceCosts(validWorld, device!))
            {
                return Decision(RejectedPlan(), "none", "invalid_or_unsafe_input", null, 0);
            }

            if (validWorld.ModelConfidence < 0.55)
            {
                return Decision(baseline, "baseline", "low_model_confidence", null, 0);
            }

            IEnumerable<OrbitPlan> candidates = candidateSet
                ? BeamCandidates(validWorld, blockIndex, width, maxExpansions, cancellation).Concat(baselines)
                : [BeamPlan(validWorld, blockIndex, width, maxExpansions, cancellation)];

            var ranked = new List<Ranked>();
            foreach (OrbitPlan candidate in candidates)
            {
                cancellation.ThrowIfCancellationRequested();
                CheckPlan(validWorld, candidate, blockIndex);
                double improvement = candidate.PredictedProxy - baseline.PredictedProxy;
                double lower = ConservativeAdvantage(validWorld, candidate, baseline, uncertainty!, device!, partitionK);
                ranked.Add(new Ranked(lower, improvement, candidate));
            }

            List<Ranked> eligible = ranked.Where(item => item.Lower > 0 && item.Improvement > 0).ToList();
            if (eligible.Count > 0)
            {
                Ranked best = BestRanked(eligible);
                return Decision(best.Plan, "orbit", "bounded_model_advantage", best.Lower, best.Improvement, ranked.Count, eligible.Count);
            }

            if (ranked.Count > 0)
            {
                Ranked best = BestRanked(ranked);
                return Decision(baseline, "baseline", "not_robustly_better", best.Lower, best.Improvement, ranked.Count);
            }

            return Decision(baseline, "baseline", "not_robustly_better", null, 0);
        }
        catch (Exception error) when (error is ArithmeticException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return Decision(RejectedPlan(), "none", "invalid_or_unsafe_input", null, 0);
        }
    }

    private static OrbitDecision Decision(OrbitPlan plan, string selected, string reason, double? minimumAdvantage, double predictionAdvantage, int candidates = 0, int eligible = 0) =>
        new(plan, selected, reason, minimumAdvantage, predictionAdvantage, candidates, eligible);

    /// Choose 앞단 사전 축약 (Python 참조에는 없고 Choose 는 그대로): 1e12 를 넘는 블록을 빼고, 남은 블록이 24개를 넘으면 앞에서 24개만 남깁니다 (블록 순서가 순위).
    /// 뺀 블록을 요구하던 에피소드는 남은 블록만 요구하게 줄이지 않고 통째로 버립니다 (가치 부풀림 방지). 줄였으면 사유 접미를 함께 돌려줍니다 (줄이지 않았으면 빈 문자열).
    public static (PlanningWorld? World, string Suffix) Reduce(PlanningWorld? world)
    {
        if (world?.Blocks is null || world.Episodes is null)
        {
            return (world, string.Empty);
        }

        List<PlanningBlock> kept = world.Blocks.Where(block => block is null || block.Size <= MaxBlockSize).ToList();
        int oversized = world.Blocks.Count - kept.Count;
        int before = kept.Count;
        if (oversized == 0 && before <= MaxBlocks)
        {
            return (world, string.Empty);
        }

        if (before > MaxBlocks)
        {
            kept.RemoveRange(MaxBlocks, before - MaxBlocks);
        }

        HashSet<string> keys = kept.Where(block => block is not null).Select(block => block.Key).ToHashSet(StringComparer.Ordinal);
        var episodes = new List<PredictedEpisode>(world.Episodes.Count);
        foreach (PredictedEpisode episode in world.Episodes)
        {
            if (episode?.Requires is null)
            {
                episodes.Add(episode!); // Choose 가 거부함
                continue;
            }

            bool intact = episode.Requires.Count > 0 && episode.Requires.All(key => key is null || keys.Contains(key)); // 뺀 블록을 요구하면 버림
            if (intact)
            {
                episodes.Add(episode);
            }
        }

        var parts = new List<string>();
        if (oversized > 0)
        {
            parts.Add($"oversized={oversized}");
        }

        if (before > MaxBlocks)
        {
            parts.Add($"top{MaxBlocks}_of={before}");
        }

        return (world with { Blocks = kept, Episodes = episodes }, ";reduced(" + string.Join(',', parts) + ")");
    }

    private static OrbitPlan BestBaseline(IReadOnlyList<OrbitPlan> plans)
    {
        OrbitPlan best = plans[0];
        for (int i = 1; i < plans.Count; i++)
        {
            OrbitPlan candidate = plans[i];
            int comparison = candidate.PredictedProxy.CompareTo(best.PredictedProxy);
            if (comparison == 0)
            {
                comparison = (-candidate.ElapsedMs).CompareTo(-best.ElapsedMs);
            }

            if (comparison == 0)
            {
                comparison = string.Compare(candidate.Source, best.Source, StringComparison.Ordinal);
            }

            if (comparison > 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static Ranked BestRanked(IReadOnlyList<Ranked> items)
    {
        Ranked best = items[0];
        for (int i = 1; i < items.Count; i++)
        {
            Ranked candidate = items[i];
            int comparison = candidate.Lower.CompareTo(best.Lower);
            if (comparison == 0)
            {
                comparison = candidate.Improvement.CompareTo(best.Improvement);
            }

            if (comparison == 0)
            {
                comparison = (-candidate.Plan.ElapsedMs).CompareTo(-best.Plan.ElapsedMs);
            }

            if (comparison == 0)
            {
                comparison = string.Compare(candidate.Plan.Source, best.Plan.Source, StringComparison.Ordinal);
            }

            if (comparison == 0)
            {
                comparison = CompareStringOrders(candidate.Plan.Order, best.Plan.Order);
            }

            if (comparison > 0)
            {
                best = candidate;
            }
        }

        return best;
    }

    private static int CompareStringOrders(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        int count = Math.Min(left.Count, right.Count);
        for (int i = 0; i < count; i++)
        {
            var leftRunes = left[i].EnumerateRunes().GetEnumerator();
            var rightRunes = right[i].EnumerateRunes().GetEnumerator();
            while (true)
            {
                bool leftHasRune = leftRunes.MoveNext();
                bool rightHasRune = rightRunes.MoveNext();
                if (!leftHasRune || !rightHasRune)
                {
                    if (leftHasRune != rightHasRune)
                    {
                        return leftHasRune.CompareTo(rightHasRune);
                    }

                    break;
                }

                int comparison = leftRunes.Current.Value.CompareTo(rightRunes.Current.Value);
                if (comparison != 0)
                {
                    return comparison;
                }
            }
        }

        return left.Count.CompareTo(right.Count);
    }

    private static bool ValidWorld(PlanningWorld? world)
    {
        try
        {
            if (world is null || world.Blocks is null || world.Episodes is null ||
                world.Blocks.Count is < 1 or > MaxBlocks || world.Episodes.Count > MaxEpisodes)
            {
                return false;
            }

            if (!world.DiskIdle || !world.DiskKnown || world.OtherGameActive || !world.BaselineReadAllowed)
            {
                return false;
            }

            if (!AllFinite(world.Timebox, world.TransferUnitsPerMs, world.SeekMs, world.SeekGap,
                    world.InterruptionHazard, world.ModelConfidence) ||
                world.Budget <= BigInteger.Zero || world.Timebox <= 0 || world.TransferUnitsPerMs <= 0 ||
                world.SeekMs < 0 || world.SeekGap < 0 || world.InterruptionHazard < 0 ||
                world.ModelConfidence is < 0 or > 1)
            {
                return false;
            }

            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (PlanningBlock? block in world.Blocks)
            {
                if (block is null || block.Key is null || PythonLength(block.Key) is < 1 or > 512 ||
                    !keys.Add(block.Key) || block.Size is <= 0 or > 1_000_000_000_000L ||
                    !AllFinite(block.Position, block.Frequency) || block.Frequency < 0)
                {
                    return false;
                }
            }

            int dependencyCount = 0;
            foreach (PredictedEpisode? episode in world.Episodes)
            {
                if (episode is null || episode.Requires is null)
                {
                    return false;
                }

                dependencyCount = checked(dependencyCount + episode.Requires.Count);
                if (dependencyCount > MaxDependencies || episode.Requires.Count is < 1 or > 8 ||
                    !AllFinite(episode.Predicted, episode.PredictedPartial) || episode.Predicted < 0 ||
                    episode.PredictedPartial is < 0 or > 1)
                {
                    return false;
                }

                var required = new HashSet<string>(StringComparer.Ordinal);
                foreach (string? key in episode.Requires)
                {
                    if (key is null || !required.Add(key) || !keys.Contains(key))
                    {
                        return false;
                    }
                }
            }

            return LegacySafe(world);
        }
        catch (Exception error) when (error is ArithmeticException or ArgumentException or InvalidOperationException or NullReferenceException)
        {
            return false;
        }
    }

    private static bool LegacySafe(PlanningWorld world)
    {
        try
        {
            double predictedTotal = FSum(world.Episodes.Select(episode => episode.Predicted));
            var possibleCosts = new List<double>(world.Blocks.Count);
            foreach (PlanningBlock block in world.Blocks)
            {
                possibleCosts.Add(Cost(world, null, block));
            }

            return double.IsFinite(predictedTotal) && predictedTotal <= MaxSafePredictedTotal &&
                possibleCosts.All(double.IsFinite) && double.IsFinite(FSum(possibleCosts));
        }
        catch (Exception error) when (error is ArithmeticException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool ValidUncertainty(Uncertainty? uncertainty) => uncertainty is not null &&
        HasNonWhitespace(uncertainty.CalibrationVersion) &&
        AllFinite(uncertainty.ValueLow, uncertainty.ValueHigh, uncertainty.PartialError) &&
        uncertainty.ValueLow is >= 0 and <= 1 && uncertainty.ValueHigh is >= 1 and <= 10 &&
        uncertainty.PartialError is >= 0 and <= 1;

    private static bool ValidDevice(DeviceEnvelope? device) => device is not null &&
        HasNonWhitespace(device.CalibrationVersion) &&
        AllFinite(device.SeekMin, device.SeekMax, device.ThroughputMin, device.ThroughputMax) &&
        device.SeekMin is >= 0 and <= 1 && device.SeekMax is >= 1 and <= 10 &&
        device.ThroughputMin is > 0 and <= 1 && device.ThroughputMax is >= 1 and <= 10;

    private static bool ValidDerivedDeviceCosts(PlanningWorld world, DeviceEnvelope device)
    {
        try
        {
            (double Seek, double Throughput)[] corners =
            [
                (device.SeekMin, device.ThroughputMin),
                (device.SeekMax, device.ThroughputMin),
                (device.SeekMin, device.ThroughputMax),
                (device.SeekMax, device.ThroughputMax)
            ];

            foreach ((double seekFactor, double throughputFactor) in corners)
            {
                double transferDenominator = world.TransferUnitsPerMs * throughputFactor;
                if (!double.IsFinite(transferDenominator) || transferDenominator <= 0)
                {
                    return false;
                }

                var costs = new List<double>(world.Blocks.Count);
                foreach (PlanningBlock block in world.Blocks)
                {
                    double seek = world.SeekMs * seekFactor;
                    double transfer = block.Size / transferDenominator;
                    double delay = seek + transfer;
                    if (!double.IsFinite(delay))
                    {
                        return false;
                    }

                    costs.Add(delay);
                }

                if (!double.IsFinite(FSum(costs)))
                {
                    return false;
                }
            }

            return true;
        }
        catch (Exception error) when (error is ArithmeticException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static OrbitPlan Greedy(PlanningWorld world, IReadOnlyDictionary<string, int> blockIndex, string label)
    {
        var selected = new List<int>();
        long mask = 0;
        BigInteger left = world.Budget;
        double? head = null;
        double elapsed = 0;

        while (true)
        {
            (double First, double Second, int Third, int Index, double Delay)? best = null;
            for (int i = 0; i < world.Blocks.Count; i++)
            {
                PlanningBlock block = world.Blocks[i];
                if ((mask & (1L << i)) != 0 || block.Size > left)
                {
                    continue;
                }

                double delay = Cost(world, head, block);
                if (!TimeboxComplete(elapsed + delay, world.Timebox, conservative: false))
                {
                    continue;
                }

                double delta = Reward(world, blockIndex, mask | (1L << i)) - Reward(world, blockIndex, mask);
                (double First, double Second, int Third) ranking = label switch
                {
                    "LFU" => (-block.Frequency / block.Size, 0, i),
                    "physical" => (block.Position, 0, i),
                    _ => (delay == 0 ? -delta : -delta / delay, -delta, i)
                };
                var current = (ranking.First, ranking.Second, ranking.Third, i, delay);
                if (best is null || CompareGreedy(current, best.Value) < 0)
                {
                    best = current;
                }
            }

            if (best is null)
            {
                break;
            }

            int index = best.Value.Index;
            mask |= 1L << index;
            left -= world.Blocks[index].Size;
            elapsed += best.Value.Delay;
            head = world.Blocks[index].Position;
            selected.Add(index);
        }

        return MakePlan(world, blockIndex, selected, label);
    }

    private static int CompareGreedy(
        (double First, double Second, int Third, int Index, double Delay) left,
        (double First, double Second, int Third, int Index, double Delay) right)
    {
        int result = left.First.CompareTo(right.First);
        if (result == 0)
        {
            result = left.Second.CompareTo(right.Second);
        }

        return result == 0 ? left.Third.CompareTo(right.Third) : result;
    }

    private static OrbitPlan BundleGreedy(PlanningWorld world, IReadOnlyDictionary<string, int> blockIndex)
    {
        var state = new State([], 0, 0, 0, 0, null);
        while (true)
        {
            (double First, double Second, int[] Order, State State)? best = null;
            foreach (PredictedEpisode episode in world.Episodes)
            {
                int[] rest = episode.Requires.Select(key => blockIndex[key]).Where(i => (state.Mask & (1L << i)) == 0).ToArray();
                if (rest.Length == 0)
                {
                    continue;
                }

                State? next = AppendAction(world, blockIndex, state, OrderNear(world, state, rest));
                if (next is null || next.Score <= state.Score)
                {
                    continue;
                }

                double rate = -(next.Score - state.Score) / Math.Max(0.001, next.Elapsed - state.Elapsed);
                var current = (rate, -next.Score, next.Order, next);
                if (best is null || CompareBundle(current, best.Value) < 0)
                {
                    best = current;
                }
            }

            if (best is null)
            {
                break;
            }

            state = best.Value.State;
        }

        return MakePlan(world, blockIndex, state.Order, "bundle_roi");
    }

    private static int CompareBundle(
        (double First, double Second, int[] Order, State State) left,
        (double First, double Second, int[] Order, State State) right)
    {
        int result = left.First.CompareTo(right.First);
        if (result == 0)
        {
            result = left.Second.CompareTo(right.Second);
        }

        return result == 0 ? CompareOrders(left.Order, right.Order) : result;
    }

    private static IReadOnlyList<OrbitPlan> BeamCandidates(
        PlanningWorld world,
        IReadOnlyDictionary<string, int> blockIndex,
        int width,
        int maxExpansions,
        CancellationToken cancellation) =>
        BeamSearch(world, blockIndex, width, maxExpansions, cancellation).Candidates;

    private static OrbitPlan BeamPlan(
        PlanningWorld world,
        IReadOnlyDictionary<string, int> blockIndex,
        int width,
        int maxExpansions,
        CancellationToken cancellation) =>
        BeamSearch(world, blockIndex, width, maxExpansions, cancellation).Best;

    private static (OrbitPlan Best, IReadOnlyList<OrbitPlan> Candidates) BeamSearch(
        PlanningWorld world,
        IReadOnlyDictionary<string, int> blockIndex,
        int width,
        int maxExpansions,
        CancellationToken cancellation)
    {
        var beam = new List<State> { new([], 0, 0, 0, 0, null) };
        State best = beam[0];
        int expansions = 0;

        while (beam.Count > 0 && expansions < maxExpansions)
        {
            var found = new Dictionary<(long Mask, int Endpoint), State>();
            foreach (State state in beam)
            {
                cancellation.ThrowIfCancellationRequested(); // 협조적 취소 지점: 그림자 계획의 시간 제한·게임 시작
                var actions = new List<int[]>();
                var seenActions = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < world.Blocks.Count; i++)
                {
                    if ((state.Mask & (1L << i)) == 0)
                    {
                        AddAction(actions, seenActions, [i]);
                    }
                }

                foreach (PredictedEpisode episode in world.Episodes)
                {
                    int[] rest = episode.Requires.Select(key => blockIndex[key]).Where(i => (state.Mask & (1L << i)) == 0).ToArray();
                    if (rest.Length > 1)
                    {
                        AddAction(actions, seenActions, OrderNear(world, state, rest));
                    }
                }

                actions.Sort(CompareOrders);
                foreach (int[] action in actions)
                {
                    if (expansions >= maxExpansions)
                    {
                        break;
                    }

                    expansions++;
                    State? next = AppendAction(world, blockIndex, state, action);
                    if (next is null)
                    {
                        continue;
                    }

                    if (next.Score > best.Score + ScoreTolerance ||
                        Math.Abs(next.Score - best.Score) <= ScoreTolerance && CompareOrders(next.Order, best.Order) < 0)
                    {
                        best = next;
                    }

                    var key = (next.Mask, next.Order[^1]);
                    if (!found.TryGetValue(key, out State? old) || next.Score > old.Score + ScoreTolerance ||
                        Math.Abs(next.Score - old.Score) <= ScoreTolerance && next.Elapsed < old.Elapsed)
                    {
                        found[key] = next;
                    }
                }
            }

            beam = found.Values.ToList();
            beam.Sort(CompareStates);
            if (beam.Count > width)
            {
                beam.RemoveRange(width, beam.Count - width);
            }
        }

        OrbitPlan bestPlan = MakePlan(world, blockIndex, best.Order, "ORBIT2_beam");
        var finalStates = new List<State> { best };
        finalStates.AddRange(beam);
        var seenOrders = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<OrbitPlan>();
        foreach (State state in finalStates)
        {
            string key = string.Join("\u001f", state.Order);
            if (seenOrders.Add(key))
            {
                candidates.Add(MakePlan(world, blockIndex, state.Order, "ORBIT2_beam_candidate"));
            }
        }

        return (bestPlan, candidates);
    }

    private static void AddAction(List<int[]> actions, HashSet<string> seen, int[] action)
    {
        string key = string.Join(",", action);
        if (seen.Add(key))
        {
            actions.Add(action);
        }
    }

    private static int CompareStates(State left, State right)
    {
        int result = (-left.Score).CompareTo(-right.Score);
        if (result == 0)
        {
            result = left.Elapsed.CompareTo(right.Elapsed);
        }

        return result == 0 ? CompareOrders(left.Order, right.Order) : result;
    }

    private static State? AppendAction(
        PlanningWorld world,
        IReadOnlyDictionary<string, int> blockIndex,
        State state,
        IReadOnlyList<int> indices)
    {
        long mask = state.Mask;
        long used = state.Used;
        double elapsed = state.Elapsed;
        double score = state.Score;
        double? head = state.Head;
        var order = new List<int>(state.Order);

        foreach (int i in indices)
        {
            if ((mask & (1L << i)) != 0)
            {
                continue;
            }

            PlanningBlock block = world.Blocks[i];
            double delay = Cost(world, head, block);
            if (new BigInteger(used) + block.Size > world.Budget ||
                !TimeboxComplete(elapsed + delay, world.Timebox, conservative: false))
            {
                return null;
            }

            long newMask = mask | (1L << i);
            double delta = Reward(world, blockIndex, newMask) - Reward(world, blockIndex, mask);
            elapsed += delay;
            score += delta * Math.Exp(-world.InterruptionHazard * elapsed);
            used += block.Size;
            mask = newMask;
            head = block.Position;
            order.Add(i);
        }

        return new State(order.ToArray(), mask, used, elapsed, score, head);
    }

    private static int[] OrderNear(PlanningWorld world, State state, IEnumerable<int> candidates)
    {
        var remaining = new List<int>(candidates);
        var order = new List<int>(remaining.Count);
        double? last = state.Head;
        while (remaining.Count > 0)
        {
            int bestIndex = 0;
            for (int i = 1; i < remaining.Count; i++)
            {
                double current = Cost(world, last, world.Blocks[remaining[i]]);
                double best = Cost(world, last, world.Blocks[remaining[bestIndex]]);
                if (current < best || current == best && remaining[i] < remaining[bestIndex])
                {
                    bestIndex = i;
                }
            }

            int selected = remaining[bestIndex];
            order.Add(selected);
            remaining.RemoveAt(bestIndex);
            last = world.Blocks[selected].Position;
        }

        return order.ToArray();
    }

    private static Dictionary<string, int> BuildIndex(PlanningWorld world)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < world.Blocks.Count; i++)
        {
            index.Add(world.Blocks[i].Key, i);
        }

        return index;
    }

    private static OrbitPlan MakePlan(
        PlanningWorld world,
        IReadOnlyDictionary<string, int> blockIndex,
        IReadOnlyList<int> order,
        string source)
    {
        string[] names = order.Select(i => world.Blocks[i].Key).ToArray();
        (double proxy, double elapsed, long used) = Evaluate(world, blockIndex, names);
        return new OrbitPlan(Array.AsReadOnly(names), elapsed, used, proxy, source);
    }

    private static void CheckPlan(PlanningWorld world, OrbitPlan plan, IReadOnlyDictionary<string, int> blockIndex)
    {
        (double proxy, double elapsed, long used) = Evaluate(world, blockIndex, plan.Order);
        if (!AllFinite(proxy, elapsed, plan.ElapsedMs, plan.PredictedProxy) || used != plan.BytesUnits ||
            Math.Abs(elapsed - plan.ElapsedMs) >= 1e-6 || Math.Abs(proxy - plan.PredictedProxy) >= 1e-6)
        {
            throw new ArgumentException("invalid plan accounting");
        }
    }

    private static (double Proxy, double Elapsed, long Used) Evaluate(
        PlanningWorld world,
        IReadOnlyDictionary<string, int> blockIndex,
        IReadOnlyList<string> order)
    {
        long mask = 0;
        double? head = null;
        double elapsed = 0;
        long used = 0;
        double utility = 0;

        foreach (string key in order)
        {
            if (!blockIndex.TryGetValue(key, out int i) || (mask & (1L << i)) != 0)
            {
                throw new ArgumentException("unknown/duplicate step");
            }

            PlanningBlock block = world.Blocks[i];
            double nextTime = elapsed + Cost(world, head, block);
            if (new BigInteger(used) + block.Size > world.Budget || !TimeboxComplete(nextTime, world.Timebox, conservative: false))
            {
                throw new ArgumentException("budget/timebox exceeded");
            }

            long newMask = mask | (1L << i);
            double gain = Math.Max(0, Reward(world, blockIndex, newMask) - Reward(world, blockIndex, mask));
            utility += gain * Math.Exp(-world.InterruptionHazard * nextTime);
            mask = newMask;
            elapsed = nextTime;
            used += block.Size;
            head = block.Position;
        }

        return (utility, elapsed, used);
    }

    private static double Reward(PlanningWorld world, IReadOnlyDictionary<string, int> blockIndex, long mask)
    {
        double total = 0;
        for (int e = 0; e < world.Episodes.Count; e++)
        {
            PredictedEpisode episode = world.Episodes[e];
            int present = 0;
            foreach (string key in episode.Requires)
            {
                int index = blockIndex[key];
                if ((mask & (1L << index)) != 0)
                {
                    present++;
                }
            }

            double fraction = episode.PredictedPartial;
            double worth = episode.Predicted;
            total += worth * (fraction * present / episode.Requires.Count + (1.0 - fraction) * (present == episode.Requires.Count ? 1 : 0));
        }

        return total;
    }

    private static double Cost(PlanningWorld world, double? last, PlanningBlock block)
    {
        double seek = last is null || Math.Abs(last.Value - block.Position) > world.SeekGap ? world.SeekMs : 0;
        return seek + block.Size / world.TransferUnitsPerMs;
    }

    private static bool TimeboxComplete(double elapsed, double timebox, bool conservative)
    {
        if (!double.IsFinite(elapsed) || !double.IsFinite(timebox))
        {
            return false;
        }

        return conservative ? elapsed < timebox - TimeboxToleranceMs : elapsed <= timebox + TimeboxToleranceMs;
    }

    private static double ConservativeAdvantage(
        PlanningWorld world,
        OrbitPlan candidate,
        OrbitPlan baseline,
        Uncertainty uncertainty,
        DeviceEnvelope device,
        int partitionK)
    {
        double[] seekLowerEdges = PartitionEdges(device.SeekMin, device.SeekMax, partitionK, roundUp: false);
        double[] seekUpperEdges = PartitionEdges(device.SeekMin, device.SeekMax, partitionK, roundUp: true);
        double[] throughputLowerEdges = PartitionEdges(device.ThroughputMin, device.ThroughputMax, partitionK, roundUp: false);
        double[] throughputUpperEdges = PartitionEdges(device.ThroughputMin, device.ThroughputMax, partitionK, roundUp: true);
        var bounds = new List<double>(partitionK * partitionK);

        for (int seekIndex = 0; seekIndex < partitionK; seekIndex++)
        {
            for (int throughputIndex = 0; throughputIndex < partitionK; throughputIndex++)
            {
                Dictionary<int, (double Partial, double Complete)> candidateProfile = Profile(
                    world, candidate, seekUpperEdges[seekIndex + 1], throughputLowerEdges[throughputIndex], conservative: true);
                Dictionary<int, (double Partial, double Complete)> baselineProfile = Profile(
                    world, baseline, seekLowerEdges[seekIndex], throughputUpperEdges[throughputIndex + 1], conservative: false);
                var terms = new List<double>(world.Episodes.Count);

                for (int i = 0; i < world.Episodes.Count; i++)
                {
                    PredictedEpisode episode = world.Episodes[i];
                    (double candidatePartial, double candidateComplete) = candidateProfile[i];
                    (double baselinePartial, double baselineComplete) = baselineProfile[i];
                    double[] partialValues =
                    [
                        Math.Max(0, episode.PredictedPartial - uncertainty.PartialError),
                        Math.Min(1, episode.PredictedPartial + uncertainty.PartialError)
                    ];
                    double[] values = [episode.Predicted * uncertainty.ValueLow, episode.Predicted * uncertainty.ValueHigh];
                    double term = double.PositiveInfinity;
                    foreach (double value in values)
                    {
                        foreach (double rho in partialValues)
                        {
                            double current = value * (rho * (candidatePartial - baselinePartial) +
                                (1.0 - rho) * (candidateComplete - baselineComplete));
                            term = Math.Min(term, current);
                        }
                    }

                    if (!double.IsFinite(term))
                    {
                        throw new ArithmeticException("non-finite advantage term");
                    }

                    terms.Add(term);
                }

                double lower = FSum(terms);
                if (!double.IsFinite(lower))
                {
                    throw new ArithmeticException("non-finite advantage sum");
                }

                bounds.Add(lower);
            }
        }

        return bounds.Min();
    }

    private static Dictionary<int, (double Partial, double Complete)> Profile(
        PlanningWorld world,
        OrbitPlan plan,
        double seekFactor,
        double throughputFactor,
        bool conservative)
    {
        Dictionary<string, PlanningBlock> index = world.Blocks.ToDictionary(block => block.Key, StringComparer.Ordinal);
        double elapsed = 0;
        double? previous = null;
        var timeOf = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (string name in plan.Order)
        {
            PlanningBlock block = index[name];
            double delay = previous is null || Math.Abs(previous.Value - block.Position) > world.SeekGap
                ? world.SeekMs * seekFactor
                : 0;
            delay += block.Size / (world.TransferUnitsPerMs * throughputFactor);
            double nextTime = elapsed + delay;
            if (!double.IsFinite(nextTime))
            {
                throw new ArithmeticException("non-finite profile timing");
            }

            if (!TimeboxComplete(nextTime, world.Timebox, conservative))
            {
                break;
            }

            elapsed = nextTime;
            timeOf[name] = elapsed;
            previous = block.Position;
        }

        var discounts = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string key, double time) in timeOf)
        {
            discounts[key] = Math.Exp(-world.InterruptionHazard * time);
        }

        if (discounts.Values.Any(value => !double.IsFinite(value)))
        {
            throw new ArithmeticException("non-finite profile discount");
        }

        var profiles = new Dictionary<int, (double Partial, double Complete)>();
        for (int i = 0; i < world.Episodes.Count; i++)
        {
            PredictedEpisode episode = world.Episodes[i];
            var present = new List<double>();
            foreach (string key in episode.Requires)
            {
                if (discounts.TryGetValue(key, out double discount))
                {
                    present.Add(discount);
                }
            }

            double partial = 0;
            foreach (double value in present)
            {
                partial += value;
            }

            partial /= episode.Requires.Count;
            bool full = present.Count == episode.Requires.Count;
            double complete = full ? present.Min() : 0;
            profiles[i] = (partial, complete);
        }

        return profiles;
    }

    private static double[] PartitionEdges(double low, double high, int partitionK, bool roundUp)
    {
        if (low == high)
        {
            return Enumerable.Repeat(low, partitionK + 1).ToArray();
        }

        double direction = roundUp ? double.PositiveInfinity : double.NegativeInfinity;
        double span = NextAfter(high - low, direction);
        var edges = new double[partitionK + 1];
        edges[0] = low;
        for (int index = 1; index < partitionK; index++)
        {
            double offset = NextAfter(span * index, direction);
            offset = NextAfter(offset / partitionK, direction);
            edges[index] = NextAfter(low + offset, direction);
        }

        edges[partitionK] = high;
        return edges;
    }

    private static double NextAfter(double value, double direction) =>
        direction > value ? Math.BitIncrement(value) : direction < value ? Math.BitDecrement(value) : value;

    private static int CompareOrders(IReadOnlyList<int> left, IReadOnlyList<int> right)
    {
        int count = Math.Min(left.Count, right.Count);
        for (int i = 0; i < count; i++)
        {
            int comparison = left[i].CompareTo(right[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Count.CompareTo(right.Count);
    }

    private static int PythonLength(string value) => value.EnumerateRunes().Count();

    private static bool HasNonWhitespace(string? value)
    {
        if (value is null)
        {
            return false;
        }

        foreach (Rune rune in value.EnumerateRunes())
        {
            if (!Rune.IsWhiteSpace(rune) && rune.Value is not (>= 0x1c and <= 0x1f))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AllFinite(params double[] values) => values.All(double.IsFinite);

    /// 이 합산은 CPython math.fsum(Shewchuk, "Adaptive Precision Floating-Point Arithmetic and Fast Robust Geometric Predicates", 1997)의 알고리즘을 같은 구조로 옮긴 것이며, 출처 고지는 THIRD_PARTY_NOTICES.md 에 있습니다.
    internal static double FSum(IEnumerable<double> values)
    {
        List<double> partials = [];
        foreach (double value in values)
        {
            double x = value;
            var next = new List<double>(partials.Count + 1);
            for (int i = 0; i < partials.Count; i++)
            {
                double y = partials[i];
                if (Math.Abs(x) < Math.Abs(y))
                {
                    (x, y) = (y, x);
                }

                double high = x + y;
                if (!double.IsFinite(high))
                {
                    throw new ArithmeticException("math.fsum overflow");
                }

                double rounded = high - x;
                double low = y - rounded;
                if (low != 0)
                {
                    next.Add(low);
                }

                x = high;
            }

            if (x != 0 || next.Count == 0)
            {
                next.Add(x);
            }

            partials = next;
        }

        double total = 0;
        for (int i = partials.Count - 1; i >= 0; i--)
        {
            double x = partials[i];
            double roundedTotal = total + x;
            if (!double.IsFinite(roundedTotal))
            {
                throw new ArithmeticException("math.fsum overflow");
            }

            double rounded = roundedTotal - total;
            double low = x - rounded;
            total = roundedTotal;
            if (low != 0)
            {
                if (i > 0 && (low < 0 && partials[i - 1] < 0 || low > 0 && partials[i - 1] > 0))
                {
                    double y = low * 2;
                    double corrected = total + y;
                    double recovered = corrected - total;
                    if (y == recovered)
                    {
                        total = corrected;
                    }
                }

                break;
            }
        }

        return total;
    }
}
