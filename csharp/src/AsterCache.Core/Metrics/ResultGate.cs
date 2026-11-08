namespace AsterCache.Core.Metrics;

public sealed record Percentiles(double P50, double P95);

/// 게이트 결과. 신뢰구간(ci)은 항상 null 로 기록합니다: 실측 표본이 쌓인 뒤에야 의미가 있어 계산하지 않고 사유만 남깁니다.
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
    string ReasonCode = "")
{
    public const string CiNotComputed = "신뢰구간은 실측 표본 확보 전이라 계산하지 않음";
}

/// 로딩 성과 채택 게이트 (V2-09, RV-05). 입력은 표본뿐이며 "개선됨" 값을 밖에서 넘길 길이 없습니다.
public static class ResultGate
{
    public const string Unverified = "performance_unverified";
    public const string Adopt = "adopt";
    public const string Reject = "reject";

    /// 쪽마다 필요한 최소 표본 수. p95 가 최댓값 하나에 거의 좌우되지 않도록 잡은 보수적인 값 (실측 후 조정).
    public const int MinSamples = 5;

    // p95 는 표본 5개면 최댓값이라 1~2 ms 잡음에도 흔들리므로, 기준 p95 의 5% 또는 2 ms 중 큰 값까지는 악화로 보지 않습니다.
    // p50 에는 이 여유가 없습니다. 비대칭은 의도입니다: p95 는 표본 5개에서 사실상 최댓값이라 잡음에 흔들립니다.
    private const double P95TolerancePct = 0.05;
    private const double P95SlackMs = 2;

    public static GateResult Evaluate(IReadOnlyList<LoadSample> baseline, IReadOnlyList<LoadSample> candidate)
    {
        // T_ready 가 없거나 음수 (잘못된 수동 입력) 인 표본은 판정에서 제외
        List<long> b = Usable(baseline);
        List<long> c = Usable(candidate);
        int excluded = baseline.Count + candidate.Count - b.Count - c.Count;
        int visualCount = baseline.Concat(candidate).Count(s => s.TReadyMs is >= 0 && s.TVisualMs is >= 0);
        string visual = visualCount > 0 ? "observed" : "unobserved";

        (string Code, string Text)? mixed = MixedReason(baseline, candidate);
        if (mixed is { } m)
        {
            return new GateResult(Unverified, m.Text, b.Count, c.Count, excluded, null, null, visual, null, visualCount, m.Code);
        }

        if (b.Count < MinSamples || c.Count < MinSamples)
        {
            return new GateResult(Unverified,
                $"표본 부족: baseline {b.Count}, candidate {c.Count} (각 {MinSamples} 이상 필요, 제외 {excluded})",
                b.Count, c.Count, excluded, null, null, visual, null, visualCount, "too_few_samples");
        }

        var pb = new Percentiles(Percentile(b, 0.50), Percentile(b, 0.95));
        var pc = new Percentiles(Percentile(c, 0.50), Percentile(c, 0.95));
        bool p50Worse = pc.P50 > pb.P50; // 동률은 나쁘지 않음으로 봅니다
        bool p95Worse = pc.P95 > pb.P95 + Math.Max(pb.P95 * P95TolerancePct, P95SlackMs);
        // 신뢰구간이 없어 지금은 adopt 에 도달하지 않습니다. 나중에 신뢰구간 판정을 넣으면 여기서 Adopt 를 고릅니다.
        // 악화 증거는 신뢰구간 없이도 채택 거부에 충분하므로 reject 는 그대로 냅니다.
        bool worse = p50Worse || p95Worse;
        string verdict = worse ? Reject : Unverified;
        string reason = worse
            ? $"candidate 가 baseline 보다 나쁨:{(p50Worse ? " p50" : string.Empty)}{(p95Worse ? " p95" : string.Empty)}"
            : "candidate 가 p50·p95 모두 baseline 보다 나쁘지 않음 (신뢰구간 없어 채택 보류)";
        string code = !worse ? "not_worse_no_ci" : p50Worse && p95Worse ? "worse_p50_p95" : p50Worse ? "worse_p50" : "worse_p95";
        return new GateResult(verdict, reason, b.Count, c.Count, excluded, pb, pc, visual, c.Max() - b.Max(), visualCount, code);
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
    private static (string Code, string Text)? MixedReason(IReadOnlyList<LoadSample> baseline, IReadOnlyList<LoadSample> candidate)
    {
        List<LoadSample> all = [.. baseline, .. candidate];
        // 빌드를 모르거나 캐시 상태·시나리오·경과 기준이 unknown 이면 비교 조건을 보장할 수 없어 미검증으로 돌립니다.
        if (all.Any(s => s.GameBuild is null || s.CacheState == CacheState.Unknown || s.Scenario == Scenario.Unknown || s.ElapsedBasis == "unknown"))
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

    private static List<long> Usable(IReadOnlyList<LoadSample> samples) =>
        [.. samples.Where(s => s.TReadyMs is >= 0).Select(s => s.TReadyMs!.Value)];
}
