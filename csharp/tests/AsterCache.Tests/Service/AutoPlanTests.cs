using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Tests.Core;
using AsterCache.Tray;
using Xunit;

namespace AsterCache.Tests.Service;

/// 자동 계획이 수동 흐름과 같은 존 크기·같은 파일 바이트를 만드는지.
public sealed class AutoPlanTests : ApplyTestBase
{
    private const long Mib = 1L << 20;

    [Theory]
    [InlineData(0L, 16)]
    [InlineData(1L, 16)]
    [InlineData(14 * Mib, 16)] // 1.1 배 = 15.4 MiB
    [InlineData(15 * Mib, 32)] // 16.5 MiB 는 16 MiB 단위로 올림
    [InlineData(1000 * Mib, 1104)]
    [InlineData(2048 * Mib, 2256)]
    public void ZoneMibIsTenPercentMoreRoundedUpTo16Mib(long hotBytes, long expected)
    {
        Assert.Equal(expected, ZoneSize.ZoneMib(hotBytes));
    }

    [Fact]
    public void ZoneMibMatchesTheTrayManualFlow()
    {
        for (long hot = 0; hot < 5000 * Mib; hot += (37 * Mib) + 12345)
        {
            Assert.Equal(ManageViewModel.ZoneMib(hot), ZoneSize.ZoneMib(hot));
        }
    }

    [Fact]
    public void SavedPlanAndPrefetchFilesHaveTheBytesTheCliPlanCommandAlwaysWrote()
    {
        PlanFile plan = Make();
        string path = Path.Combine(Root, "p.plan.json");

        string prefetch = PlanOutput.Save(plan, Sessions, path);

        Assert.Equal(path + ".prefetch.json", prefetch);
        Assert.Equal(PyJson.Utf8.GetBytes(PyJson.ToCrlf(plan.Dumps())), File.ReadAllBytes(path));
        string rows = PyJson.Dumps(plan.PrefetchTrace(Sessions).Select(e => e.ToJson()).ToList(), ensureAscii: false) + "\n";
        Assert.Equal(PyJson.Utf8.GetBytes(PyJson.ToCrlf(rows)), File.ReadAllBytes(prefetch));
        Assert.Contains((byte)'\r', File.ReadAllBytes(prefetch));
    }
}
