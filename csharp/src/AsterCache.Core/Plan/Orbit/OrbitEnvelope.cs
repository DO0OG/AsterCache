namespace AsterCache.Core.Plan.Orbit;

/// 장치 표본에서 만든 보정 결과: 명목값 (탐색 지연 ms, 전송률 MB/s), 보정 창 크기, 플래너에 넘기는 구간.
public sealed record DeviceFit(double SeekHatMs, double ThroughputHatMBps, int SeekN, int ThroughputN, DeviceEnvelope Envelope);

/// 분할 conformal 구간 (순수 계산): 앞쪽 절반으로 명목값 (중앙값), 뒤쪽 절반의 |ln(y / 명목값)| 분위수로 배수 구간을 만듭니다.
public static class OrbitEnvelope
{
    public const string Version = "split-conformal-v1";

    /// 시간순 표본 series 의 (명목값, 분위수 q, 보정 창 크기 n). 보정할 수 없으면 null: n 이 모자라거나 값이 유한 양수가 아니거나 e^q 가 상한을 넘음.
    public static (double Hat, double Q, int N)? Conformal(IReadOnlyList<double> series)
    {
        int fitCount = series.Count / 2;
        int n = series.Count - fitCount;
        if (n < Orbit3Constants.MinCalibrationSamples || series.Any(v => !double.IsFinite(v) || v <= 0))
        {
            return null;
        }

        double[] fit = [.. series.Take(fitCount).Order()];
        double hat = fitCount % 2 == 1 ? fit[fitCount / 2] : (fit[(fitCount / 2) - 1] + fit[fitCount / 2]) / 2;
        // 부동소수 오차로 정수 경계가 한 칸 올라가지 않게 작은 값을 뺌
        int rank = (int)Math.Ceiling(((n + 1) * (1 - Orbit3Constants.Alpha)) - 1e-9);
        if (rank > n)
        {
            return null;
        }

        double q = series.Skip(fitCount).Select(y => Math.Abs(Math.Log(y / hat))).Order().ElementAt(rank - 1);
        return Math.Exp(q) > Orbit3Constants.MaxEnvelopeRatio ? null : (hat, q, n);
    }

    /// 단측 이항 상측 확률 P(X ≥ k), X ~ Bin(n, p). 점화식으로 합산 (n 이 작아 충분).
    public static double BinomialUpperTail(int n, int k, double p)
    {
        if (k <= 0)
        {
            return 1;
        }

        if (k > n)
        {
            return 0;
        }

        double pmf = Math.Pow(1 - p, n);
        double below = 0; // P(X < k)
        for (int i = 0; i < k; i++)
        {
            below += pmf;
            pmf *= (double)(n - i) / (i + 1) * (p / (1 - p));
        }

        return Math.Max(0, 1 - below);
    }

    /// 탐색 지연 (ms) 과 전송률 (MB/s) 시계열 둘 다 보정될 때만 값, 하나라도 안 되면 null.
    public static DeviceFit? Fit(IReadOnlyList<double> seekMs, IReadOnlyList<double> throughputMBps)
    {
        if (Conformal(seekMs) is not { } s || Conformal(throughputMBps) is not { } b)
        {
            return null;
        }

        var envelope = new DeviceEnvelope(Math.Exp(-s.Q), Math.Exp(s.Q), Math.Exp(-b.Q), Math.Exp(b.Q), Version);
        return new DeviceFit(s.Hat, b.Hat, s.N, b.N, envelope);
    }

    public static bool SeekInside(DeviceFit fit, double ms) => ms / fit.SeekHatMs >= fit.Envelope.SeekMin && ms / fit.SeekHatMs <= fit.Envelope.SeekMax;

    public static bool ThroughputInside(DeviceFit fit, double mbps) => mbps / fit.ThroughputHatMBps >= fit.Envelope.ThroughputMin && mbps / fit.ThroughputHatMBps <= fit.Envelope.ThroughputMax;
}
