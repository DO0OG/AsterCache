namespace AsterCache.Core.Metrics;

public sealed record Percentiles(double P50, double P95);

public sealed record BootstrapInterval(double Lower, double Upper);

public sealed record BootstrapIntervals(BootstrapInterval P50DeltaMs, BootstrapInterval P95DeltaMs);

/// 게이트 결과. ci 는 baseline 과 candidate 의 차이에 대한 고정 시드 bootstrap 95% 구간입니다.
/// WorstLossMs 는 candidate 최악값 - baseline 최악값이며, 비교를 하지 못했으면 null 입니다.
/// VisualObservedCount 는 T_visual 이 잡힌 표본 수입니다. 기록만 하고 판정에는 쓰지 않습니다.
/// ReasonCode 는 트레이가 ko/en 문구로 바꾸는 짧은 영문 키 (예: too_few_samples), Reason 은 로그·JSON 용 한국어 설명입니다. 예전 기록에는 없어 빈 값입니다.
public sealed record GateResult(
    string Verdict,
    string Reason,
    int BaselineUsed,
    int CandidateUsed,
    int Excluded,
    Percentiles? Baseline,
    Percentiles? Candidate,
    string Visual,
    long? WorstLossMs,
    int VisualObservedCount,
    string ReasonCode = "",
    BootstrapIntervals? Ci = null,
    string MetricSource = "t_ready_ms")
{
    public const string CiNotComputed = "표본이 부족하거나 비교 조건이 맞지 않아 신뢰구간을 계산하지 않음";
}

/// 로딩 성과 강등 게이트 (V2-09, RV-05). 통계적으로 유의한 악화만 reject 하며 adopt 는 내지 않습니다.
public static class ResultGate
{
    public const string Unverified = "performance_unverified";
    public const string Adopt = "adopt";
    public const string Reject = "reject";

    /// 쪽마다 필요한 최소 표본 수. p95 가 최댓값 하나에 거의 좌우되지 않도록 잡은 보수적인 값 (실측 후 조정).
    public const int MinSamples = 10;

    public const int BootstrapIterations = 1000;
    private const int BootstrapSeed = 0x4F524249;

    // p95 는 표본 10개면 최댓값이라 1~2 ms 잡음에도 흔들리므로, 기준 p95 의 5% 또는 2 ms 중 큰 값까지는 악화로 보지 않습니다.
    // p50 에는 이 여유가 없습니다. 비대칭은 의도입니다: p95 는 표본 10개에서 사실상 최댓값이라 잡음에 흔들립니다.
    private const double P95TolerancePct = 0.05;
    private const double P95SlackMs = 2;

    public static GateResult Evaluate(IReadOnlyList<LoadSample> baseline, IReadOnlyList<LoadSample> candidate)
    {
        List<long> baselineReady = Usable(baseline, sample => sample.TReadyMs);
        List<long> candidateReady = Usable(candidate, sample => sample.TReadyMs);
        List<long> baselineProbe = Usable(baseline, sample => sample.ProbeReadyMs);
        List<long> candidateProbe = Usable(candidate, sample => sample.ProbeReadyMs);
        bool readyEnough = baselineReady.Count >= MinSamples && candidateReady.Count >= MinSamples;
        bool probeEnough = baselineProbe.Count >= MinSamples && candidateProbe.Count >= MinSamples;
        bool useProbe = !readyEnough && (probeEnough || (baselineReady.Count == 0 && candidateReady.Count == 0
            && (baselineProbe.Count > 0 || candidateProbe.Count > 0)));
        List<long> b = useProbe ? baselineProbe : baselineReady;
        List<long> c = useProbe ? candidateProbe : candidateReady;
        string metricSource = useProbe ? "probe_ready_ms" : "t_ready_ms";
        int excluded = baseline.Count + candidate.Count - b.Count - c.Count;
        int visualCount = baseline.Concat(candidate).Count(s => s.TReadyMs is >= 0 && s.TVisualMs is >= 0);
        string visual = visualCount > 0 ? "observed" : "unobserved";

        LoadSample[] baselineCompared = MetricSamples(baseline, useProbe);
        LoadSample[] candidateCompared = MetricSamples(candidate, useProbe);
        (string Code, string Text)? mixed = MixedReason(baselineCompared, candidateCompared, allowUnknown: useProbe);
        if (mixed is { } m)
        {
            return new GateResult(Unverified, m.Text, b.Count, c.Count, excluded, null, null, visual, null, visualCount,
                m.Code, MetricSource: metricSource);
        }

        if (b.Count < MinSamples || c.Count < MinSamples)
        {
            return new GateResult(Unverified,
                $"표본 부족: baseline {b.Count}, candidate {c.Count} (각 {MinSamples} 이상 필요, 제외 {excluded})",
                b.Count, c.Count, excluded, null, null, visual, null, visualCount, "too_few_samples", MetricSource: metricSource);
        }

        var pb = new Percentiles(Percentile(b, 0.50), Percentile(b, 0.95));
        var pc = new Percentiles(Percentile(c, 0.50), Percentile(c, 0.95));
        BootstrapIntervals ci = Bootstrap(b, c);
        bool p50Worse = ci.P50DeltaMs.Lower > 0;
        bool p95Worse = ci.P95DeltaMs.Lower > Math.Max(pb.P95 * P95TolerancePct, P95SlackMs);
        bool worse = p50Worse || p95Worse;
        string verdict = worse ? Reject : Unverified;
        string reason = worse
            ? $"bootstrap 95% 신뢰구간에서 candidate 악화가 유의함:{(p50Worse ? " p50" : string.Empty)}{(p95Worse ? " p95" : string.Empty)}"
            : "bootstrap 95% 신뢰구간에서 유의한 악화가 확인되지 않음 (자동 채택은 하지 않음)";
        string code = !worse ? "not_worse_no_ci" : p50Worse && p95Worse ? "worse_p50_p95" : p50Worse ? "worse_p50" : "worse_p95";
        return new GateResult(verdict, reason, b.Count, c.Count, excluded, pb, pc, visual, c.Max() - b.Max(), visualCount,
            code, ci, metricSource);
    }

    /// 정렬 후 선형 보간 (위치 = p*(n-1), numpy 기본값과 같음). 빈 목록, 범위 밖이거나 NaN 인 p, 음수 값은 ArgumentException.
    public static double Percentile(IReadOnlyList<long> values, double p)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("빈 목록의 백분위는 구할 수 없습니다", nameof(values));
        }

        if (double.IsNaN(p) || p < 0 || p > 1)
        {
            throw new ArgumentException("p 는 0 이상 1 이하여야 합니다", nameof(p));
        }

        if (values.Any(v => v < 0))
        {
            throw new ArgumentException("음수 시간은 백분위 입력이 될 수 없습니다", nameof(values));
        }

        long[] sorted = [.. values.Order()];
        double pos = p * (sorted.Length - 1);
        int lo = (int)Math.Floor(pos);
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + ((sorted[hi] - sorted[lo]) * (pos - lo));
    }

    /// 두 목록이 같은 비교 조건이고 쪽 라벨이 맞는지 봅니다. 어긋나면 사유, 맞으면 null.
    private static (string Code, string Text)? MixedReason(IReadOnlyList<LoadSample> baseline, IReadOnlyList<LoadSample> candidate, bool allowUnknown)
    {
        List<LoadSample> all = [.. baseline, .. candidate];
        // T_ready 비교는 기존처럼 모든 조건을 알아야 합니다. 대리 지표는 같은 서비스 실행 안의 값끼리만 비교합니다.
        if (!allowUnknown && all.Any(s => s.GameBuild is null || s.CacheState == CacheState.Unknown || s.Scenario == Scenario.Unknown || s.ElapsedBasis == "unknown"))
        {
            return ("mixed_conditions", "mixed comparison conditions: 게임 빌드·캐시 상태·시나리오·경과 기준 중 알 수 없는 값이 있음");
        }

        if (all.Any(s => s.GameKey != all[0].GameKey || s.Scenario != all[0].Scenario || s.Elapsed != all[0].Elapsed || s.ElapsedBasis != all[0].ElapsedBasis
            || s.GameBuild != all[0].GameBuild || s.CacheState != all[0].CacheState))
        {
            return ("mixed_conditions", "mixed comparison conditions: 게임·시나리오·경과 구간·경과 기준·빌드·캐시 상태가 두 목록에서 다름");
        }

        if (baseline.Any(s => s.Arm != Arm.Baseline) || candidate.Any(s => s.Arm != Arm.Candidate))
        {
            return ("mixed_arms", "mixed arms: baseline 목록에 Candidate 표본이 있거나 candidate 목록에 Baseline 표본이 있음");
        }

        return null;
    }

    private static List<long> Usable(IReadOnlyList<LoadSample> samples, Func<LoadSample, long?> value) =>
        [.. samples.Select(value).Where(sample => sample is >= 0).Select(sample => sample!.Value)];

    private static LoadSample[] MetricSamples(IReadOnlyList<LoadSample> samples, bool useProbe) =>
        [.. samples.Where(sample => (useProbe ? sample.ProbeReadyMs : sample.TReadyMs) is >= 0)];

    private static BootstrapIntervals Bootstrap(IReadOnlyList<long> baseline, IReadOnlyList<long> candidate)
    {
        var random = new Random(BootstrapSeed);
        var baselineSample = new long[baseline.Count];
        var candidateSample = new long[candidate.Count];
        var p50Deltas = new double[BootstrapIterations];
        var p95Deltas = new double[BootstrapIterations];
        for (int iteration = 0; iteration < BootstrapIterations; iteration++)
        {
            for (int index = 0; index < baselineSample.Length; index++)
            {
                baselineSample[index] = baseline[random.Next(baseline.Count)];
            }

            for (int index = 0; index < candidateSample.Length; index++)
            {
                candidateSample[index] = candidate[random.Next(candidate.Count)];
            }

            Array.Sort(baselineSample);
            Array.Sort(candidateSample);
            p50Deltas[iteration] = SortedPercentile(candidateSample, 0.50) - SortedPercentile(baselineSample, 0.50);
            p95Deltas[iteration] = SortedPercentile(candidateSample, 0.95) - SortedPercentile(baselineSample, 0.95);
        }

        Array.Sort(p50Deltas);
        Array.Sort(p95Deltas);
        return new BootstrapIntervals(
            new BootstrapInterval(SortedPercentile(p50Deltas, 0.025), SortedPercentile(p50Deltas, 0.975)),
            new BootstrapInterval(SortedPercentile(p95Deltas, 0.025), SortedPercentile(p95Deltas, 0.975)));
    }

    private static double SortedPercentile(IReadOnlyList<long> values, double p)
    {
        double position = p * (values.Count - 1);
        int low = (int)Math.Floor(position);
        int high = Math.Min(low + 1, values.Count - 1);
        return values[low] + ((values[high] - values[low]) * (position - low));
    }

    private static double SortedPercentile(IReadOnlyList<double> values, double p)
    {
        double position = p * (values.Count - 1);
        int low = (int)Math.Floor(position);
        int high = Math.Min(low + 1, values.Count - 1);
        return values[low] + ((values[high] - values[low]) * (position - low));
    }
}
