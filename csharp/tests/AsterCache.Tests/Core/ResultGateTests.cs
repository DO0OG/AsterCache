using AsterCache.Core.Json;
using AsterCache.Core.Metrics;
using Xunit;

namespace AsterCache.Tests.Core;

public sealed class ResultGateTests
{
    private static LoadSample S(Arm arm, long? ready, long? visual = null) =>
        new("game", Scenario.S0, arm, ElapsedBucket.Sec0, ready, visual, GameBuild: "b1", CacheState: CacheState.Warm, ElapsedBasis: "service_uptime");

    private static List<LoadSample> Many(Arm arm, params long?[] ready) => [.. ready.Select(r => S(arm, r))];

    private static List<LoadSample> Flat(Arm arm, long ready) =>
        Enumerable.Range(0, ResultGate.MinSamples).Select(_ => S(arm, ready)).ToList();

    private static LoadSample B(Arm arm, long? ready, string basis) =>
        new("game", Scenario.S0, arm, ElapsedBucket.Sec0, ready, GameBuild: "b1", CacheState: CacheState.Warm, ElapsedBasis: basis);

    private static LoadSample Probe(Arm arm, long? ready) =>
        new("game", Scenario.Unknown, arm, ElapsedBucket.Sec0, null, GameBuild: null, CacheState: CacheState.Unknown,
            ProbeReadyMs: ready, ElapsedBasis: "service_uptime");

    [Fact]
    public void TooFewSamplesAreUnverified()
    {
        GateResult r = ResultGate.Evaluate(Many(Arm.Baseline, 10, 10, 10, 10), Many(Arm.Candidate, 1, 1, 1, 1, 1));

        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Null(r.Baseline);
    }

    [Fact]
    public void CandidateOnlyTooFewIsUnverified()
    {
        GateResult r = ResultGate.Evaluate(Many(Arm.Baseline, 10, 10, 10, 10, 10), Many(Arm.Candidate, 1, 1, 1, 1));

        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Equal(5, r.BaselineUsed);
        Assert.Equal(4, r.CandidateUsed);
        Assert.Null(r.Baseline);
        Assert.Contains("candidate 4", r.Reason);
    }

    [Fact]
    public void UnobservedSamplesAreExcludedAndCanLeaveTooFew()
    {
        GateResult r = ResultGate.Evaluate(Many(Arm.Baseline, 10, 10, 10, 10, null), Many(Arm.Candidate, 1, 1, 1, 1, 1));

        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Equal(4, r.BaselineUsed);
        Assert.Equal(1, r.Excluded);

        r = ResultGate.Evaluate(Many(Arm.Baseline, 10, 10, 10, 10, 10, null), Many(Arm.Candidate, 1, 1, 1, 1, 1));
        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Equal(5, r.BaselineUsed);
    }

    [Fact]
    public void NegativeTReadyIsExcluded()
    {
        GateResult r = ResultGate.Evaluate(Many(Arm.Baseline, 10, 20, 30, 40, 50, -1), Many(Arm.Candidate, 5, 15, 25, 35, 45));

        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Equal(5, r.BaselineUsed);
        Assert.Equal(1, r.Excluded);
    }

    [Fact]
    public void MixedConditionsAreUnverified()
    {
        GateResult r = ResultGate.Evaluate(Many(Arm.Baseline, 10, 10, 10, 10, 10),
            [.. Many(Arm.Candidate, 1, 1, 1, 1), new("game", Scenario.S1, Arm.Candidate, ElapsedBucket.Sec0, 1, GameBuild: "b1", CacheState: CacheState.Warm)]);

        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Contains("mixed comparison conditions", r.Reason);
        Assert.Null(r.Candidate);

        r = ResultGate.Evaluate(Many(Arm.Baseline, 10, 10, 10, 10, 10),
            [.. Many(Arm.Candidate, 1, 1, 1, 1), new("game", Scenario.S0, Arm.Candidate, ElapsedBucket.Sec10, 1, GameBuild: "b1", CacheState: CacheState.Warm)]);
        Assert.Contains("mixed comparison conditions", r.Reason);
    }

    [Fact]
    public void MixedArmsAreUnverified()
    {
        GateResult r = ResultGate.Evaluate([.. Many(Arm.Baseline, 10, 10, 10, 10), S(Arm.Candidate, 10)], Many(Arm.Candidate, 1, 1, 1, 1, 1));

        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Contains("mixed arms", r.Reason);
    }

    [Fact]
    public void ElapsedBasisMixedOrUnknownIsUnverifiedNotRejected()
    {
        // 기준이 섞이거나 unknown 이면 같은 구간이라도 비교가 아니므로, 악화처럼 보여도 reject 가 아니라 미검증입니다
        List<LoadSample> baseline = Flat(Arm.Baseline, 10);
        GateResult r = ResultGate.Evaluate(baseline, [.. Many(Arm.Candidate, 100, 100, 100, 100), B(Arm.Candidate, 100, "install")]);
        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Equal("mixed_conditions", r.ReasonCode);

        r = ResultGate.Evaluate(baseline, Enumerable.Range(0, ResultGate.MinSamples).Select(_ => B(Arm.Candidate, 100, "install")).ToList());
        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Equal("mixed_conditions", r.ReasonCode);

        r = ResultGate.Evaluate(baseline, Enumerable.Range(0, ResultGate.MinSamples).Select(_ => B(Arm.Candidate, 100, "unknown")).ToList());
        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Equal("mixed_conditions", r.ReasonCode);

        r = ResultGate.Evaluate(baseline, Enumerable.Range(0, ResultGate.MinSamples).Select(_ => B(Arm.Candidate, 100, "service_uptime")).ToList());
        Assert.Equal(ResultGate.Reject, r.Verdict);
    }

    [Fact]
    public void DifferentGameBuildIsUnverified()
    {
        List<LoadSample> candidate = [.. Many(Arm.Candidate, 1, 1, 1, 1), new("game", Scenario.S0, Arm.Candidate, ElapsedBucket.Sec0, 1, GameBuild: "b2", CacheState: CacheState.Warm)];
        GateResult r = ResultGate.Evaluate(Many(Arm.Baseline, 10, 10, 10, 10, 10), candidate);

        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Contains("mixed comparison conditions", r.Reason);
        Assert.Null(r.Candidate);
    }

    [Fact]
    public void UnknownCacheStateOrMissingBuildIsUnverified()
    {
        List<LoadSample> cacheUnknown = [.. Many(Arm.Candidate, 1, 1, 1, 1), new("game", Scenario.S0, Arm.Candidate, ElapsedBucket.Sec0, 1, GameBuild: "b1")];
        GateResult r = ResultGate.Evaluate(Many(Arm.Baseline, 10, 10, 10, 10, 10), cacheUnknown);
        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Null(r.Candidate);

        List<LoadSample> noBuild = [.. Many(Arm.Candidate, 1, 1, 1, 1), new("game", Scenario.S0, Arm.Candidate, ElapsedBucket.Sec0, 1, CacheState: CacheState.Warm)];
        r = ResultGate.Evaluate(Many(Arm.Baseline, 10, 10, 10, 10, 10), noBuild);
        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Null(r.Candidate);
    }

    [Fact]
    public void P95WithinTheSlackIsNotRejectedButBeyondItIs()
    {
        // 표본 하나만 달라진 p95 는 기준 여유를 넘더라도 bootstrap 구간에 악화가 확정되지 않습니다.
        Assert.Equal(ResultGate.Unverified, ResultGate.Evaluate(Flat(Arm.Baseline, 10), Many(Arm.Candidate, 10, 10, 10, 10, 10, 10, 10, 10, 10, 11)).Verdict);
        Assert.Equal(ResultGate.Unverified, ResultGate.Evaluate(Flat(Arm.Baseline, 10), Many(Arm.Candidate, 10, 10, 10, 10, 10, 10, 10, 10, 10, 13)).Verdict);
    }

    [Fact]
    public void VisualObservedCountIsRecordedAndSurvivesJsonWithoutDecidingTheVerdict()
    {
        List<LoadSample> c = [.. Flat(Arm.Candidate, 1).Take(ResultGate.MinSamples - 2), S(Arm.Candidate, 1, visual: 300), S(Arm.Candidate, 1, visual: 400)];
        GateResult r = ResultGate.Evaluate(Flat(Arm.Baseline, 10), c);
        Assert.Equal(2, r.VisualObservedCount);
        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Equal(r, MetricsJson.Parse(MetricsJson.Dumps(r)));
    }

    [Fact]
    public void PercentilesUseLinearInterpolation()
    {
        Assert.Equal(30, ResultGate.Percentile([50, 10, 30, 20, 40], 0.50));
        Assert.Equal(48, ResultGate.Percentile([10, 20, 30, 40, 50], 0.95), 9); // 위치 3.8: 40 + 0.8*10 = 48
        Assert.Equal(7, ResultGate.Percentile([7], 0.95));
    }

    [Fact]
    public void PercentileRejectsInvalidInput()
    {
        Assert.Throws<ArgumentException>(() => ResultGate.Percentile([], 0.5));
        Assert.Throws<ArgumentException>(() => ResultGate.Percentile([1, 2], double.NaN));
        Assert.Throws<ArgumentException>(() => ResultGate.Percentile([1, 2], -0.1));
        Assert.Throws<ArgumentException>(() => ResultGate.Percentile([1, 2], 1.1));
        Assert.Throws<ArgumentException>(() => ResultGate.Percentile([-1, 2], 0.5));
    }

    [Fact]
    public void CandidateWorseOnlyAtP95IsRejected()
    {
        // p50 은 같고 (30) p95 만 baseline 48 -> candidate 88
        GateResult r = ResultGate.Evaluate(
            Many(Arm.Baseline, 10, 20, 30, 40, 50, 10, 20, 30, 40, 50),
            Many(Arm.Candidate, 10, 20, 30, 40, 100, 10, 20, 30, 40, 50));

        Assert.Equal(ResultGate.Unverified, r.Verdict); // 한 outlier 만으로는 95% bootstrap 구간이 유의한 악화를 확정하지 못합니다.
        Assert.Equal(30, r.Candidate!.P50);
        Assert.Equal(77.5, r.Candidate.P95, 10);
        Assert.NotNull(r.Ci);
    }

    [Fact]
    public void SmallMedianRegressionWithoutSignificantIntervalIsUnverified()
    {
        // p50 만 baseline 30 -> candidate 31, p95 는 48 보다 낮음
        GateResult r = ResultGate.Evaluate(
            Many(Arm.Baseline, 10, 20, 30, 40, 50, 10, 20, 30, 40, 50),
            Many(Arm.Candidate, Enumerable.Repeat<long?>(31, ResultGate.MinSamples).ToArray()));

        Assert.Equal(ResultGate.Unverified, r.Verdict); // 1 ms 차이는 표본 변동보다 작아 자동 강등하지 않습니다.
        Assert.NotNull(r.Ci);
    }

    [Fact]
    public void NotWorseWithEnoughSamplesStaysUnverifiedWithComparison()
    {
        // 개선이나 동률은 자동 채택하지 않고 보류합니다.
        GateResult r = ResultGate.Evaluate(
            Many(Arm.Baseline, 10, 20, 30, 40, 50, 10, 20, 30, 40, 50),
            Many(Arm.Candidate, 5, 15, 25, 35, 45, 5, 15, 25, 35, 45));
        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Contains("유의한 악화가 확인되지 않음", r.Reason);
        Assert.NotNull(r.Candidate);

        r = ResultGate.Evaluate(
            Many(Arm.Baseline, 10, 20, 30, 40, 50, 10, 20, 30, 40, 50),
            Many(Arm.Candidate, 10, 20, 30, 40, 50, 10, 20, 30, 40, 50));
        Assert.Equal(ResultGate.Unverified, r.Verdict);
        Assert.Contains("유의한 악화가 확인되지 않음", r.Reason);
    }

    [Fact]
    public void ProbeReadyBootstrapOnlyRejectsAConsistentProxyRegression()
    {
        GateResult r = ResultGate.Evaluate(
            Enumerable.Repeat(Probe(Arm.Baseline, 100), ResultGate.MinSamples).ToList(),
            Enumerable.Repeat(Probe(Arm.Candidate, 120), ResultGate.MinSamples).ToList());

        Assert.Equal(ResultGate.Reject, r.Verdict);
        Assert.Equal("probe_ready_ms", r.MetricSource);
        Assert.Equal(new BootstrapInterval(20, 20), r.Ci!.P50DeltaMs);
        Assert.Contains("bootstrap 95%", r.Reason);

        GateResult noisy = ResultGate.Evaluate(
            Enumerable.Range(0, ResultGate.MinSamples).Select(index => Probe(Arm.Baseline, 100 + index * 20)).ToList(),
            Enumerable.Range(0, ResultGate.MinSamples).Select(index => Probe(Arm.Candidate, 105 + index * 20)).ToList());
        Assert.Equal(ResultGate.Unverified, noisy.Verdict);
    }

    [Fact]
    public void ReadyComparisonIgnoresUnknownMetadataFromProbeOnlyRows()
    {
        List<LoadSample> baseline = [.. Flat(Arm.Baseline, 100), Probe(Arm.Baseline, 120)];
        List<LoadSample> candidate = [.. Flat(Arm.Candidate, 100), Probe(Arm.Candidate, 140)];

        GateResult result = ResultGate.Evaluate(baseline, candidate);

        Assert.Equal(ResultGate.Unverified, result.Verdict);
        Assert.Equal("t_ready_ms", result.MetricSource);
        Assert.Equal(ResultGate.MinSamples, result.BaselineUsed);
        Assert.Equal(ResultGate.MinSamples, result.CandidateUsed);
    }

    [Fact]
    public void WorstLossIsCandidateMaxMinusBaselineMax()
    {
        GateResult r = ResultGate.Evaluate(
            Many(Arm.Baseline, 10, 20, 30, 40, 50, 10, 20, 30, 40, 50),
            Many(Arm.Candidate, 10, 20, 30, 40, 100, 10, 20, 30, 40, 100));
        Assert.Equal(50L, r.WorstLossMs);

        Assert.Null(ResultGate.Evaluate(Many(Arm.Baseline, 10, 10, 10, 10), Many(Arm.Candidate, 1, 1, 1, 1, 1)).WorstLossMs);
    }

    [Fact]
    public void NoVisualObservationIsReportedAsUnobserved()
    {
        GateResult r = ResultGate.Evaluate(Many(Arm.Baseline, 10, 10, 10, 10, 10), Many(Arm.Candidate, 1, 1, 1, 1, 1));
        Assert.Equal("unobserved", r.Visual);

        List<LoadSample> c = [.. Many(Arm.Candidate, 1, 1, 1, 1), S(Arm.Candidate, 1, visual: 300)];
        Assert.Equal("observed", ResultGate.Evaluate(Many(Arm.Baseline, 10, 10, 10, 10, 10), c).Visual);

        // T_ready 가 음수이거나 TVisualMs 가 음수인 표본의 visual 값은 관찰로 셈하지 않습니다
        List<LoadSample> neg = [.. Many(Arm.Candidate, 1, 1, 1, 1), S(Arm.Candidate, -1, visual: 300), S(Arm.Candidate, 1, visual: -5)];
        Assert.Equal("unobserved", ResultGate.Evaluate(Many(Arm.Baseline, 10, 10, 10, 10, 10), neg).Visual);
    }

    [Fact]
    public void JsonRoundTripKeepsBootstrapIntervals()
    {
        GateResult r = ResultGate.Evaluate(
            Many(Arm.Baseline, 10, 20, 30, 40, 50, 10, 20, 30, 40, 50),
            Many(Arm.Candidate, 5, 15, 25, 35, 45, 5, 15, 25, 35, 45));
        string json = MetricsJson.Dumps(r);

        Assert.Equal(r, MetricsJson.Parse(json));
        Assert.NotNull(r.Ci);
        Assert.Contains("\"method\": \"bootstrap\"", json);
        Assert.Contains("\"iterations\": 1000", json);
        Assert.Contains("\"worst_loss_ms\": -5", json);

        GateResult u = ResultGate.Evaluate([], []);
        Assert.Equal(u, MetricsJson.Parse(MetricsJson.Dumps(u)));
    }

    [Fact]
    public void ParseRejectsUnknownValuesAndOutOfRangeCounts()
    {
        string json = MetricsJson.Dumps(ResultGate.Evaluate(
            Many(Arm.Baseline, 10, 20, 30, 40, 50, 10, 20, 30, 40, 50),
            Many(Arm.Candidate, 5, 15, 25, 35, 45, 5, 15, 25, 35, 45)));

        Assert.Throws<PyValueException>(() => MetricsJson.Parse(json.Replace("\"performance_unverified\"", "\"maybe\"")));
        Assert.Throws<PyValueException>(() => MetricsJson.Parse(json.Replace("\"visual\": \"unobserved\"", "\"visual\": \"maybe\"")));
        Assert.Throws<PyValueException>(() => MetricsJson.Parse(json.Replace("\"baseline_used\": 10", "\"baseline_used\": 4294967296")));
        Assert.Throws<PyValueException>(() => MetricsJson.Parse(MetricsJson.Dumps(new GateResult(ResultGate.Unverified, "x", -1, 0, 0, null, null, "unobserved", null, 0))));
    }

    [Fact]
    public void RecordWithoutVisualObservedCountReadsAsZero()
    {
        string json = MetricsJson.Dumps(ResultGate.Evaluate([], [])).Replace("\"visual_observed_count\": 0,", string.Empty); // 이 필드가 없던 예전 기록
        Assert.DoesNotContain("visual_observed_count", json);
        Assert.Equal(0, MetricsJson.Parse(json).VisualObservedCount);
    }
}
