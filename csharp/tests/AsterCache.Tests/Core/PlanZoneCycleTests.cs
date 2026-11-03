using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

/// 존 확장 실패 원인 후보: 존 안에 이미 있는 핫 파일이 접근 순서와 반대로 놓이면 계획이 순환으로 거절됩니다 (PyValueException).
public sealed class PlanZoneCycleTests : ApplyTestBase
{
    [Fact]
    public void HotFilesSittingInsideTheZoneInReverseAccessOrderMakeThePlanFailWithACycleMessage()
    {
        long clusters = (1 << 20) / Cb;
        Files = new Dictionary<string, List<Extent>>(StringComparer.Ordinal)
        {
            [F1] = [new(0, 1000, clusters)],
            [F2] = [new(0, 1000 + clusters, clusters)],
        };
        Sessions = [Run([.. Enumerable.Range(0, 16).Select(i => (F2, (long)i)), .. Enumerable.Range(0, 16).Select(i => (F1, (long)i))])];

        PyValueException e = Assert.Throws<PyValueException>(() => Make(1000));

        Assert.Equal("zone overlaps hot data in a cycle; choose another zone", e.Message);
    }
}
