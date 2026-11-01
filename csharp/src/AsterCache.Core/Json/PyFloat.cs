using System.Globalization;
using System.Numerics;

namespace AsterCache.Core.Json;

/// Python float 의 repr 과 round(x, 2) 를 그대로 재현합니다.
public static class PyFloat
{
    /// repr(float): 최단 왕복 자릿수, 정수값이면 ".0", 지수 형식은 decpt 가 -4 이하이거나 16 초과일 때 "1e-05" / "1e+16".
    public static string Repr(double value)
    {
        if (!double.IsFinite(value))
        {
            throw new PyValueException("Out of range float values are not JSON compliant");
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        string r = value.ToString("R", CultureInfo.InvariantCulture);
        bool negative = r[0] == '-';
        if (negative)
        {
            r = r[1..];
        }

        int exponent = 0;
        int e = r.IndexOf('E');
        if (e >= 0)
        {
            exponent = int.Parse(r[(e + 1)..], CultureInfo.InvariantCulture);
            r = r[..e];
        }

        int dot = r.IndexOf('.');
        string digits = dot < 0 ? r : r.Remove(dot, 1);
        int decpt = (dot < 0 ? r.Length : dot) + exponent;
        int lead = 0;
        while (digits[lead] == '0')
        {
            lead++;
        }

        digits = digits[lead..].TrimEnd('0');
        decpt -= lead;
        return (negative ? "-" : string.Empty) + Layout(digits, decpt);
    }

    /// Python round(x, 2): x 의 정확한 이진 값을 소수 둘째 자리로 반올림 (정확히 중간이면 짝수 쪽) 한 뒤 가장 가까운 double.
    public static double Round2(double x)
    {
        if (!double.IsFinite(x))
        {
            return x;
        }

        long bits = BitConverter.DoubleToInt64Bits(x);
        int biased = (int)((bits >> 52) & 0x7FF);
        long fraction = bits & 0xFFFFFFFFFFFFFL;
        BigInteger mantissa = biased == 0 ? fraction : fraction | (1L << 52);
        int exponent = (biased == 0 ? 1 : biased) - 1075;
        BigInteger scaled = mantissa * 100;
        BigInteger q;
        if (exponent >= 0)
        {
            q = scaled << exponent;
        }
        else
        {
            int shift = -exponent;
            q = scaled >> shift;
            int c = (scaled - (q << shift)).CompareTo(BigInteger.One << (shift - 1));
            if (c > 0 || (c == 0 && !q.IsEven))
            {
                q += 1;
            }
        }

        double r = double.Parse(q.ToString(CultureInfo.InvariantCulture) + "e-2", NumberStyles.Float, CultureInfo.InvariantCulture);
        return bits < 0 ? -r : r;
    }

    private static string Layout(string digits, int decpt)
    {
        if (decpt <= -4 || decpt > 16)
        {
            int exp = decpt - 1;
            string mantissa = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
            return mantissa + "e" + (exp < 0 ? "-" : "+") + Math.Abs(exp).ToString("00", CultureInfo.InvariantCulture);
        }

        if (decpt <= 0)
        {
            return "0." + new string('0', -decpt) + digits;
        }

        if (decpt >= digits.Length)
        {
            return digits + new string('0', decpt - digits.Length) + ".0";
        }

        return digits[..decpt] + "." + digits[decpt..];
    }
}
