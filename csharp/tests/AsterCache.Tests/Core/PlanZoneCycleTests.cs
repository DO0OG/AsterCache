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

    [Fact]
    public void WithTheUsedZoneFixedTheSameSwappedFilesPlanWithoutMovesAndOnlyNewBlocksGoAfterThem()
    {
        long clusters = (1 << 20) / Cb;
        string f3 = Path.Combine(Root, "f3.bin");
        File.WriteAllBytes(f3, new byte[1 << 20]);
        Files = new Dictionary<string, List<Extent>>(StringComparer.Ordinal)
        {
            [F1] = [new(0, 1000, clusters)],
            [F2] = [new(0, 1000 + clusters, clusters)],
            [f3] = [new(0, 5000, clusters)],
        };
        Sessions = [Run([.. Enumerable.Range(0, 16).Select(i => (F2, (long)i)), .. Enumerable.Range(0, 16).Select(i => (F1, (long)i)), .. Enumerable.Range(0, 16).Select(i => (f3, (long)i))])];

        PlanFile plan = PlanFile.Make(Sessions, Root, 4 << 20, Cb, p => Files[p], zoneStartLcn: 1000, fixedClusters: 2 * clusters);

        PlanTask task = Assert.Single(plan.Tasks); // 존 안의 두 파일은 이동 없음, f3 만 존 뒤로
        Assert.Equal((f3, 1000 + 2 * clusters), (task.Path, task.TargetLcn));
        Assert.Equal(2 * clusters * Cb, plan.Stats["in_place_bytes"]);
        Assert.Equal(3 * clusters, plan.Stats["zone_used_clusters"]);
    }

    [Fact]
    public void WithoutAFixedZoneAndInRelativeModeNothingIsFixed()
    {
        long clusters = (1 << 20) / Cb;
        Files = new Dictionary<string, List<Extent>>(StringComparer.Ordinal)
        {
            [F1] = [new(0, 1000, clusters)],
            [F2] = [new(0, 1000 + clusters, clusters)],
        };
        Sessions = [Run([.. Enumerable.Range(0, 16).Select(i => (F1, (long)i)), .. Enumerable.Range(0, 16).Select(i => (F2, (long)i))])];

        PlanFile fixedPlan = PlanFile.Make(Sessions, Root, 4 << 20, Cb, p => Files[p], zoneStartLcn: 1000, fixedClusters: 0);
        PlanFile rel = PlanFile.Make(Sessions, Root, 4 << 20, Cb, p => Files[p], fixedClusters: 2 * clusters);

        Assert.Empty(fixedPlan.Tasks); // 이미 접근 순서대로 제자리
        Assert.Equal(2 * clusters, rel.Tasks.Sum(t => t.Clusters)); // 존 시작을 모르면 고정할 것이 없음
    }
}
