using AsterCache.Core.Plan.Orbit;
using Xunit;

namespace AsterCache.Tests.Plan.Orbit;

public sealed class OrbitStatsTests
{
    [Fact]
    public void ClopperPearsonMatchesReferenceBounds()
    {
        (int successes, int trials, double confidence, double low, double high)[] cases =
        [
            (0, 8, 0.95, 0, 0.3694166476),
            (4, 8, 0.95, 0.1570127705, 0.8429872295),
            (8, 8, 0.95, 0.6305833524, 1),
            (3, 3, 0.95, 0.2924017738, 1),
            (0, 8, 0.99, 0, 0.4843307311),
            (4, 8, 0.99, 0.0998672276, 0.9001327724),
            (8, 8, 0.99, 0.5156692689, 1),
            (3, 3, 0.99, 0.1709975947, 1),
        ];

        foreach ((int successes, int trials, double confidence, double expectedLow, double expectedHigh) in cases)
        {
            (double low, double high) = OrbitStats.ClopperPearson(successes, trials, confidence);
            Assert.InRange(Math.Abs(low - expectedLow), 0, 1e-4);
            Assert.InRange(Math.Abs(high - expectedHigh), 0, 1e-4);
        }
    }

    [Fact]
    public void GarwoodMatchesReferenceBounds()
    {
        (long events, double low, double high)[] cases =
        [
            (0, 0, 3.6888794541),
            (1, 0.0253178080, 5.5716433909),
            (5, 1.6234863901, 11.6683320793),
        ];

        foreach ((long events, double expectedLow, double expectedHigh) in cases)
        {
            (double low, double high) = OrbitStats.Garwood(events, 0.95);
            Assert.InRange(Math.Abs(low - expectedLow), 0, 1e-4);
            Assert.InRange(Math.Abs(high - expectedHigh), 0, 1e-4);
        }
    }
}
