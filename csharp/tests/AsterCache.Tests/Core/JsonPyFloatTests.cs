using System.Text.Json;
using AsterCache.Core.Json;
using Xunit;

namespace AsterCache.Tests.Core;

public sealed class JsonPyFloatTests
{
    [Fact]
    public void ReprAndRound2MatchPythonFor2000Values()
    {
        using JsonDocument doc = PlanFixtures.Doc("floats/repr.json");
        JsonElement rows = doc.RootElement.GetProperty("rows");
        Assert.Equal(2000, rows.GetArrayLength());
        foreach (JsonElement row in rows.EnumerateArray())
        {
            double x = PlanFixtures.FromBits(row[0].GetString()!);
            PlanFixtures.With(row[1].GetString()!, () =>
            {
                Assert.Equal(row[1].GetString(), PyFloat.Repr(x));
                double r = PyFloat.Round2(x);
                Assert.Equal(row[2].GetString(), PyFloat.Repr(r));
                Assert.Equal(row[3].GetString(), BitConverter.DoubleToInt64Bits(r).ToString("x16", System.Globalization.CultureInfo.InvariantCulture));
            });
        }
    }

    [Theory]
    [InlineData(57.0, "57.0")]
    [InlineData(1e-5, "1e-05")]
    [InlineData(1e-4, "0.0001")]
    [InlineData(1e15, "1000000000000000.0")]
    [InlineData(1e16, "1e+16")]
    [InlineData(-0.0, "-0.0")]
    [InlineData(0.1, "0.1")]
    [InlineData(1.5e300, "1.5e+300")]
    public void ReprKnownValues(double x, string expected) => Assert.Equal(expected, PyFloat.Repr(x));

    [Theory]
    [InlineData(2.675, 2.67)]
    [InlineData(0.125, 0.12)]
    [InlineData(0.375, 0.38)]
    [InlineData(56.725, 56.73)]
    [InlineData(1.005, 1.0)]
    public void Round2IsExactNotScaled(double x, double expected) => Assert.Equal(expected, PyFloat.Round2(x));

    [Fact]
    public void NonFiniteIsRefused()
    {
        Assert.Throws<PyValueException>(() => PyFloat.Repr(double.NaN));
        Assert.Throws<PyValueException>(() => PyFloat.Repr(double.PositiveInfinity));
    }

    [Fact]
    public void DecayPowersMatchPython()
    {
        using JsonDocument doc = PlanFixtures.Doc("floats/repr.json");
        JsonElement bits = doc.RootElement.GetProperty("pow").GetProperty("bits");
        Assert.Equal(32, bits.GetArrayLength());
        for (int n = 0; n < 32; n++)
        {
            Assert.Equal(PlanFixtures.FromBits(bits[n].GetString()!), Math.Pow(0.85, n));
        }
    }
}
