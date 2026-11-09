using AsterCache.Core.Plan.Orbit;
using Xunit;

namespace AsterCache.Tests.Plan.Orbit;

public sealed class OrbitEnvelopeTests
{
    // 앞쪽 절반은 모두 10, 뒤쪽 절반은 10 * e^(r) (부호를 섞음). 명목값 10, 잔차 |ln(y/10)| = |r| 를 그대로 만듭니다.
    private static List<double> Series(int fit, params double[] calibrationLogs) =>
        [.. Enumerable.Repeat(10.0, fit), .. calibrationLogs.Select(r => 10 * Math.Exp(r))];

    private static double[] Logs(int n) => [.. Enumerable.Range(1, n).Select(k => (k % 2 == 0 ? 1 : -1) * 0.01 * k)];

    [Fact]
    public void Eighteen_calibration_samples_give_no_envelope_and_nineteen_do()
    {
        Assert.Null(OrbitEnvelope.Conformal(Series(18, Logs(18))));
        Assert.NotNull(OrbitEnvelope.Conformal(Series(19, Logs(19))));
        Assert.NotNull(OrbitEnvelope.Conformal(Series(18, Logs(19)))); // 37 개면 보정 창이 19 개
    }

    [Fact]
    public void Quantile_uses_the_ceil_n_plus_1_times_1_minus_alpha_rank()
    {
        // n=19: ceil(20 * 0.95) = 19 번째 = 가장 큰 잔차 0.19
        (double hat, double q, int n) = OrbitEnvelope.Conformal(Series(19, Logs(19)))!.Value;
        Assert.Equal(10.0, hat, 12);
        Assert.Equal(0.19, q, 12);
        Assert.Equal(19, n);

        // n=39: ceil(40 * 0.95) = 38 번째 = 두 번째로 큰 잔차 0.38
        (_, double q39, int n39) = OrbitEnvelope.Conformal(Series(39, Logs(39)))!.Value;
        Assert.Equal(0.38, q39, 12);
        Assert.Equal(39, n39);
    }

    [Fact]
    public void Nominal_value_is_the_median_of_the_fit_half()
    {
        // 홀수 개 (19): 정렬한 10 번째 = 4
        List<double> odd = [.. Enumerable.Repeat(1.0, 9), 4.0, .. Enumerable.Repeat(100.0, 9), .. Enumerable.Repeat(4.0, 19)];
        Assert.Equal(4.0, OrbitEnvelope.Conformal(odd)!.Value.Hat, 12);

        // 짝수 개 (20): 가운데 둘 (3, 5) 의 평균 = 4
        List<double> even = [.. Enumerable.Repeat(3.0, 10), .. Enumerable.Repeat(5.0, 10), .. Enumerable.Repeat(4.0, 20)];
        (double hat, double q, int n) = OrbitEnvelope.Conformal(even)!.Value;
        Assert.Equal((4.0, 0.0, 20), (hat, q, n));
    }

    [Fact]
    public void Ratio_above_the_limit_gives_no_envelope()
    {
        double[] logs = [.. Logs(18), Math.Log(10.5)]; // 가장 큰 잔차의 e^q = 10.5 > 10
        Assert.Null(OrbitEnvelope.Conformal(Series(19, logs)));
        double[] ok = [.. Logs(18), Math.Log(9.5)];
        Assert.NotNull(OrbitEnvelope.Conformal(Series(19, ok)));
    }

    [Fact]
    public void Non_positive_or_non_finite_values_give_no_envelope()
    {
        List<double> zero = Series(19, Logs(19));
        zero[3] = 0;
        Assert.Null(OrbitEnvelope.Conformal(zero));
        List<double> nan = Series(19, Logs(19));
        nan[30] = double.NaN;
        Assert.Null(OrbitEnvelope.Conformal(nan));
    }

    [Fact]
    public void Fit_needs_both_series_and_exposes_planner_envelope()
    {
        List<double> seek = Series(19, Logs(19));
        Assert.Null(OrbitEnvelope.Fit(seek, Series(5, Logs(5))));
        Assert.Null(OrbitEnvelope.Fit(Series(5, Logs(5)), seek));

        DeviceFit fit = OrbitEnvelope.Fit(seek, Series(19, Logs(19)))!;
        Assert.Equal(Math.Exp(-0.19), fit.Envelope.SeekMin, 12);
        Assert.Equal(Math.Exp(0.19), fit.Envelope.SeekMax, 12);
        Assert.Equal(Math.Exp(-0.19), fit.Envelope.ThroughputMin, 12);
        Assert.Equal(Math.Exp(0.19), fit.Envelope.ThroughputMax, 12);
        Assert.Equal(10.0, fit.SeekHatMs, 12);
        Assert.Equal((19, 19), (fit.SeekN, fit.ThroughputN));
        Assert.True(OrbitEnvelope.SeekInside(fit, 10 * Math.Exp(0.18)));
        Assert.False(OrbitEnvelope.SeekInside(fit, 10 * Math.Exp(0.2)));
        Assert.False(OrbitEnvelope.ThroughputInside(fit, 10 * Math.Exp(-0.2)));
    }

    [Fact]
    public void Binomial_upper_tail_matches_hand_calculation()
    {
        // n=19, p=0.05: pmf 0..4 = 0.377354, 0.377354, 0.178746, 0.053310, 0.011224
        Assert.Equal(0.013236, OrbitEnvelope.BinomialUpperTail(19, 4, 0.05), 5);
        Assert.Equal(0.002012, OrbitEnvelope.BinomialUpperTail(19, 5, 0.05), 5);
        Assert.True(OrbitEnvelope.BinomialUpperTail(19, 4, 0.05) >= Orbit3Constants.MonitorPValue);
        Assert.True(OrbitEnvelope.BinomialUpperTail(19, 5, 0.05) < Orbit3Constants.MonitorPValue);
        Assert.Equal(1.0, OrbitEnvelope.BinomialUpperTail(19, 0, 0.05));
        Assert.Equal(0.0, OrbitEnvelope.BinomialUpperTail(19, 20, 0.05));
        Assert.Equal(Math.Pow(0.05, 19), OrbitEnvelope.BinomialUpperTail(19, 19, 0.05), 12);
    }
}
