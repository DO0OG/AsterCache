namespace AsterCache.Core.Plan.Orbit;

/// 외부 통계 패키지 없이 보정 구간을 계산합니다.
public static class OrbitStats
{
    private static readonly double[] LanczosCoefficients = [
        676.5203681218851,
        -1259.1392167224028,
        771.32342877765313,
        -176.61502916214059,
        12.507343278686905,
        -0.13857109526572012,
        9.9843695780195716e-6,
        1.5056327351493116e-7,
    ];

    public static (double Low, double High) ClopperPearson(int successes, int trials, double confidenceLevel)
    {
        if (trials <= 0 || successes < 0 || successes > trials)
        {
            throw new ArgumentOutOfRangeException(nameof(trials));
        }

        ValidateConfidence(confidenceLevel);
        double tail = (1 - confidenceLevel) / 2;
        double low = successes == 0 ? 0 : BetaQuantile(tail, successes, trials - successes + 1);
        double high = successes == trials ? 1 : BetaQuantile(1 - tail, successes + 1, trials - successes);
        return (low, high);
    }

    public static (double Low, double High) Garwood(long events, double confidenceLevel)
    {
        if (events < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(events));
        }

        ValidateConfidence(confidenceLevel);
        double tail = (1 - confidenceLevel) / 2;
        double low = events == 0 ? 0 : ChiSquareQuantile(tail, 2 * (double)events) / 2;
        double high = ChiSquareQuantile(1 - tail, 2 * ((double)events + 1)) / 2;
        return (low, high);
    }

    private static void ValidateConfidence(double confidenceLevel)
    {
        if (!double.IsFinite(confidenceLevel) || confidenceLevel <= 0 || confidenceLevel >= 1)
        {
            throw new ArgumentOutOfRangeException(nameof(confidenceLevel));
        }
    }

    private static double BetaQuantile(double probability, double a, double b)
    {
        double low = 0;
        double high = 1;
        for (int i = 0; i < 120; i++)
        {
            double mid = (low + high) / 2;
            if (RegularizedBeta(mid, a, b) < probability)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return (low + high) / 2;
    }

    private static double RegularizedBeta(double x, double a, double b)
    {
        if (x <= 0)
        {
            return 0;
        }

        if (x >= 1)
        {
            return 1;
        }

        double factor = Math.Exp(LogGamma(a + b) - LogGamma(a) - LogGamma(b) + a * Math.Log(x) + b * Math.Log(1 - x));
        if (x < (a + 1) / (a + b + 2))
        {
            return factor * BetaFraction(x, a, b) / a;
        }

        return 1 - factor * BetaFraction(1 - x, b, a) / b;
    }

    private static double BetaFraction(double x, double a, double b)
    {
        const int maxIterations = 10000;
        const double epsilon = 3e-14;
        const double minimum = 1e-300;
        double sum = a + b;
        double aPlus = a + 1;
        double aMinus = a - 1;
        double c = 1;
        double d = 1 - sum * x / aPlus;
        if (Math.Abs(d) < minimum)
        {
            d = minimum;
        }

        d = 1 / d;
        double fraction = d;
        for (int m = 1; m <= maxIterations; m++)
        {
            int twice = 2 * m;
            double step = m * (b - m) * x / ((aMinus + twice) * (a + twice));
            fraction = ApplyFractionStep(step, ref c, ref d, fraction, minimum, out double change);
            step = -(a + m) * (sum + m) * x / ((a + twice) * (aPlus + twice));
            fraction = ApplyFractionStep(step, ref c, ref d, fraction, minimum, out change);
            if (Math.Abs(change - 1) < epsilon)
            {
                break;
            }
        }

        return fraction;
    }

    private static double ApplyFractionStep(double step, ref double c, ref double d, double fraction, double minimum, out double change)
    {
        d = 1 + step * d;
        if (Math.Abs(d) < minimum)
        {
            d = minimum;
        }

        c = 1 + step / c;
        if (Math.Abs(c) < minimum)
        {
            c = minimum;
        }

        d = 1 / d;
        change = d * c;
        return fraction * change;
    }

    private static double ChiSquareQuantile(double probability, double degreesOfFreedom)
    {
        double shape = degreesOfFreedom / 2;
        double low = 0;
        double high = Math.Max(1, degreesOfFreedom);
        while (RegularizedGammaP(shape, high / 2) < probability)
        {
            high *= 2;
        }

        for (int i = 0; i < 120; i++)
        {
            double mid = (low + high) / 2;
            if (RegularizedGammaP(shape, mid / 2) < probability)
            {
                low = mid;
            }
            else
            {
                high = mid;
            }
        }

        return (low + high) / 2;
    }

    private static double RegularizedGammaP(double a, double x)
    {
        if (x <= 0)
        {
            return 0;
        }

        if (x < a + 1)
        {
            double term = 1 / a;
            double sum = term;
            double current = a;
            for (int i = 1; i <= 10000; i++)
            {
                current++;
                term *= x / current;
                sum += term;
                if (Math.Abs(term) <= Math.Abs(sum) * 3e-14)
                {
                    break;
                }
            }

            return sum * Math.Exp(-x + a * Math.Log(x) - LogGamma(a));
        }

        return 1 - RegularizedGammaQContinuedFraction(a, x);
    }

    private static double RegularizedGammaQContinuedFraction(double a, double x)
    {
        const double minimum = 1e-300;
        double b = x + 1 - a;
        double c = 1 / minimum;
        double d = 1 / Math.Max(Math.Abs(b), minimum) * Math.Sign(b == 0 ? 1 : b);
        double product = d;
        for (int i = 1; i <= 10000; i++)
        {
            double coefficient = -i * (i - a);
            b += 2;
            d = coefficient * d + b;
            if (Math.Abs(d) < minimum)
            {
                d = minimum;
            }

            c = b + coefficient / c;
            if (Math.Abs(c) < minimum)
            {
                c = minimum;
            }

            d = 1 / d;
            double delta = d * c;
            product *= delta;
            if (Math.Abs(delta - 1) < 3e-14)
            {
                break;
            }
        }

        return Math.Exp(-x + a * Math.Log(x) - LogGamma(a)) * product;
    }

    private static double LogGamma(double z)
    {
        if (z < 0.5)
        {
            return Math.Log(Math.PI) - Math.Log(Math.Sin(Math.PI * z)) - LogGamma(1 - z);
        }

        z -= 1;
        double sum = 0.99999999999980993;
        for (int i = 0; i < LanczosCoefficients.Length; i++)
        {
            sum += LanczosCoefficients[i] / (z + i + 1);
        }

        double t = z + LanczosCoefficients.Length - 0.5;
        return 0.5 * Math.Log(2 * Math.PI) + (z + 0.5) * Math.Log(t) - t + Math.Log(sum);
    }
}
