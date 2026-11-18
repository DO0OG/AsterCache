using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Volume;
using AsterCache.Windows;
using Xunit;
using Xunit.Abstractions;

namespace AsterCache.Tests.Windows;

/// 실제 이전 형식 계획 사본으로 기준값 후보와 잔여 계획을 메모리에서만 만들어 결과를 출력합니다 (디스크·계획 폴더에 아무것도 쓰지 않음).
/// 결과를 검증하는 시험이 아니라 출력용 점검입니다: 단언 없이 출력만 보고 판단하며, 서비스 결과와 다를 수 있습니다 (저널의 planned 행을 반영하지 않음).
/// 계획 사본은 환경 변수 ASTER_LEGACY_PLAN 에 경로를 주어야 하고 (없으면 바로 끝남), 다른 게임의 예약은 ASTER_PLANS_DIR (기본: C:\ProgramData\AsterCache\plans) 에서 읽기만 합니다.
/// 승격된 (관리자) 프로세스에서는 실제 볼륨 비트맵으로 새 구간을 고르고, 아니면 빈 구간 조회를 가짜로 바꿔 기준값 수와 옮길 클러스터 수만 냅니다.
[Trait("Category", "Live")]
public sealed class LegacySparsePlanCheckLiveTests(ITestOutputHelper output)
{
    private const string DefaultPlansDir = @"C:\ProgramData\AsterCache\plans";

    [Fact]
    public void RealLegacyPlanBaselineCandidateAndResidualPlacementPreview()
    {
        if (Environment.GetEnvironmentVariable("ASTER_LEGACY_PLAN") is not { Length: > 0 } planPath)
        {
            return; // 계획 사본을 주지 않으면 할 일 없음
        }

        string plansDir = Environment.GetEnvironmentVariable("ASTER_PLANS_DIR") ?? DefaultPlansDir;
        Assert.True(File.Exists(planPath), $"plan copy not found: {planPath}");
        PlanFile plan = PlanFile.Load(planPath);
        string exceptPlan = Path.Combine(plansDir, Path.GetFileName(planPath));
        IReadOnlyList<(long Lcn, long Clusters)> reserved = ZoneReservations.Collect(plansDir, plan.Volume, exceptPlan);

        PlanFile? candidate = SparseAllocation.WithMissingBaselines(plan, p => FileGuards.IsSparse(p), FileLayout.RelocationExtents);
        output.WriteLine($"plan {planPath}: order {plan.Order.Count} ranges, tasks {plan.Tasks.Count}, zone start {plan.Zone.StartLcn}, existing baselines {plan.SparseAllocations.Count}");
        if (candidate is null)
        {
            output.WriteLine("candidate: none (no sparse file without a baseline, or its allocation could not be read)");
            return;
        }

        output.WriteLine($"candidate: {candidate.SparseAllocations.Count - plan.SparseAllocations.Count} sparse file(s) gained a baseline");
        using NtfsVolume? vol = Elevation.IsAdmin() ? NtfsVolume.Open(plan.Volume[..2]) : null;
        VolumeBitmap? bitmap = vol?.Bitmap();
        long fakeRun = 0;
        long? asked = null;
        PlanFile built = ResidualPlanner.Build(candidate, ResidualPlanner.PreviousTargets(null, [plan]), FileLayout.RelocationExtents,
            need =>
            {
                asked = need;
                return bitmap is null ? fakeRun : bitmap.FindFreeRun(need, preferLowLcn: true, align: PlanGuards.ZoneAlign, excluded: reserved);
            },
            (need, ends) => bitmap is null ? null : ends.Where(e => bitmap.RangeFree(e, need) && !reserved.Any(r => r.Lcn < e + need && e < r.Lcn + r.Clusters)).Select(e => (long?)e).FirstOrDefault());

        long sparseMoving = built.Tasks.Where(t => built.SparseAllocations.ContainsKey(t.Path) && built.Zone.Clusters > 0 && t.TargetLcn >= built.Zone.StartLcn && t.TargetLcn < built.Zone.StartLcn + built.Zone.Clusters).Sum(t => t.Clusters);
        output.WriteLine($"residual: {built.Zone.Clusters} cluster(s) to move ({built.Zone.Clusters * plan.ClusterBytes / (1 << 20)} MiB), sparse part {sparseMoving}, fixed {built.Stats["fixed_clusters"]}, "
            + $"held files {(built.Stats.TryGetValue(ResidualPlanner.HeldKey, out object? held) && held is JObject h ? h.Count : 0)}, sparse tasks {built.Tasks.Count(t => built.SparseAllocations.ContainsKey(t.Path))}, asked free run {asked}");
        output.WriteLine(bitmap is null
            ? "volume bitmap not read (not elevated): the new run start below is a placeholder"
            : $"new run: start LCN {built.Zone.StartLcn}, length {built.Zone.Clusters}, adjacent to moved part {(built.Stats.TryGetValue(ResidualPlanner.AdjacentKey, out object? adj) ? adj : "n/a")}, reserved ranges {reserved.Count}");
        LayoutReport layout = LayoutVerifier.Verify(built, FileLayout.RelocationExtents, FileGuards.RelocationUnmovable, p => FileGuards.IsSparse(p), holdChangedSparse: true);
        output.WriteLine($"layout check on the replaced plan: contiguous {layout.Contiguous}, current runs {layout.OrderedRuns}, promised runs {layout.ExpectedRuns}, missing files {layout.MissingFiles}, held sparse {layout.SparseHeldFiles}");
    }
}
