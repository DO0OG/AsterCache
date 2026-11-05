using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Plan;
using AsterCache.Core.Volume;
using Xunit;

namespace AsterCache.Tests.Core;

/// 다른 게임의 미완료 존 제외 (ZoneReservations, VolumeBitmap 제외 범위, ApplyOptions.Reserved).
public sealed class ZoneReservationsTests : ApplyTestBase
{
    private string PlansDir => Directory.CreateDirectory(Path.Combine(Root, "plans")).FullName;

    private string PlanPath(string name) => Path.Combine(PlansDir, name + ".plan.json");

    private static long End(IReadOnlyList<(long Lcn, long Clusters)> r) => r.Max(x => x.Lcn + x.Clusters);

    [Fact]
    public void ActivePlansOfOtherGamesAreReservedAndTheCallersOwnIsNot()
    {
        PlanFile a = Make().Resolve(20000);
        PlanFile b = Make().Resolve(60000);
        a.Save(PlanPath("a"));
        b.Save(PlanPath("b"));

        IReadOnlyList<(long Lcn, long Clusters)> forB = ZoneReservations.Collect(PlansDir, Letter, PlanPath("b"));
        IReadOnlyList<(long Lcn, long Clusters)> forA = ZoneReservations.Collect(PlansDir, Letter, PlanPath("a"));

        Assert.Equal(20000, forB[0].Lcn);
        Assert.InRange(End(forB), 20001, 59999);
        Assert.Equal(60000, forA[0].Lcn);
    }

    [Fact]
    public void AnotherVolumeIsIgnored()
    {
        Make().Resolve(20000).Save(PlanPath("a"));

        Assert.Empty(ZoneReservations.Collect(PlansDir, "Q:", PlanPath("b")));
    }

    [Fact]
    public void StaleBootAndArchivedNamesDoNotReserve()
    {
        PlanFile a = Make().Resolve(20000);
        foreach (string suffix in new[] { ".stale", ".boot", ".extend", ".bad", ".prefetch.json", ".switch.tmp" })
        {
            a.Save(PlanPath("a") + suffix);
        }

        File.WriteAllText(Path.Combine(PlansDir, "a.jsonl.20261105T030000Z"), "x");

        Assert.Empty(ZoneReservations.Collect(PlansDir, Letter, PlanPath("b")));
    }

    [Fact]
    public void UnreadableOrAutoZonePlansAreSkippedWithoutError()
    {
        File.WriteAllText(PlanPath("broken"), "{ not json");
        Make().Save(PlanPath("auto")); // 존 시작이 아직 없는 계획

        Assert.Empty(ZoneReservations.Collect(PlansDir, Letter, PlanPath("b")));
        Assert.Empty(ZoneReservations.Collect(Path.Combine(Root, "missing"), Letter, PlanPath("b")));
    }

    [Fact]
    public void ACandidateOfAnInterruptedSwitchIsReservedUntilTheSwitchFinishes()
    {
        Make().Resolve(20000).Save(PlanPath("a"));
        string journal = Path.Combine(PlansDir, "a.jsonl");
        PlanFile candidate = Make().Resolve(70000);
        Assert.Throws<InvalidOperationException>(() => PlanSwitch.Commit(candidate, PlanPath("a"), journal, () => DateTime.UtcNow, hooks: Hooks(),
            checkpoint: p =>
            {
                if (p == "recorded")
                {
                    throw new InvalidOperationException(p);
                }
            }));

        IReadOnlyList<(long Lcn, long Clusters)> during = ZoneReservations.Collect(PlansDir, Letter, PlanPath("b"));

        Assert.Contains(during, r => r.Lcn <= 70000 && 70000 < r.Lcn + r.Clusters); // 후보 (.next)
        Assert.Contains(during, r => r.Lcn <= 20000 && 20000 < r.Lcn + r.Clusters); // 아직 활성인 이전 계획
        Assert.Empty(ZoneReservations.Collect(PlansDir, Letter, PlanPath("a"))); // 교체하는 게임 자신은 뺌

        _ = PlanSwitch.Resume(PlanPath("a"), journal);
        IReadOnlyList<(long Lcn, long Clusters)> after = ZoneReservations.Collect(PlansDir, Letter, PlanPath("b"));
        Assert.Single(after); // 후보가 활성 계획이 되어 이전 범위는 풀림
        Assert.Equal(70000, after[0].Lcn);
    }

    [Fact]
    public void ReleasedReservationsDisappearWhenThePlanIsGone()
    {
        PlanFile a = Make().Resolve(20000);
        a.Save(PlanPath("a"));
        Assert.NotEmpty(ZoneReservations.Collect(PlansDir, Letter, PlanPath("b")));

        File.Move(PlanPath("a"), PlanPath("a") + ".stale");

        Assert.Empty(ZoneReservations.Collect(PlansDir, Letter, PlanPath("b")));
    }

    [Fact]
    public void TwoAutoZonePlansDoNotPickTheSameFreeRun()
    {
        string pathA = PlanPath("a");
        string pathB = PlanPath("b");
        Make().Save(pathA);
        Make().Save(pathB);

        ApplyReport ra = Applier.Apply(PlanFile.Load(pathA), Vol, [Root], Path.Combine(Root, "ja.jsonl"), Hooks(),
            new ApplyOptions(DryRun: true, SaveTo: pathA, Reserved: ZoneReservations.Collect(PlansDir, Letter, pathA)));
        ApplyReport rb = Applier.Apply(PlanFile.Load(pathB), Vol, [Root], Path.Combine(Root, "jb.jsonl"), Hooks(),
            new ApplyOptions(DryRun: true, SaveTo: pathB, Reserved: ZoneReservations.Collect(PlansDir, Letter, pathB)));
        ApplyReport rbNoReserve = Applier.Apply(Make(), Vol, [Root], Path.Combine(Root, "jc.jsonl"), Hooks(), new ApplyOptions(DryRun: true));

        PlanFile planA = PlanFile.Load(pathA);
        PlanFile planB = PlanFile.Load(pathB);
        Assert.Equal(ra.ZoneStartLcn, rbNoReserve.ZoneStartLcn); // 예약이 없으면 같은 빈 구간을 고름 (문제의 원인)
        Assert.DoesNotContain(planB.Tasks, tb => planA.Tasks.Any(ta => ta.TargetLcn < tb.TargetLcn + tb.Clusters && tb.TargetLcn < ta.TargetLcn + ta.Clusters));
        Assert.Equal(rb.ZoneStartLcn, planB.Zone.StartLcn);
    }

    [Fact]
    public void ExplicitZoneOverlappingAReservationIsRejectedLikeAnOccupiedZone()
    {
        PlanFile plan = Make().Resolve(20000);
        (long, long)[] reserved = [(20000, 10000)];

        PlanRejectedException e = Assert.Throws<PlanRejectedException>(() => PlanGuards.CheckZoneFree(plan, Vol.Bitmap(), p => Vol.Ext[p], reserved));
        PlanRejectedException e2 = Assert.Throws<PlanRejectedException>(() => PlanGuards.ResolveZone(plan, Vol, p => Vol.Ext[p], null, out _, reserved));

        Assert.StartsWith("zone_not_free", e.Message);
        Assert.StartsWith("zone_not_free", e2.Message);
    }

    [Fact]
    public void ReservedZoneOfAFreshPlanIsRezonedToAFreeRunAwayFromTheReservation()
    {
        // 점유와 같은 취급: 저널에 끝나지 않은 단계가 없고 제자리 조각이 없으면 예약을 피해 새 빈 구간으로 옮김
        PlanFile plan = Make().Resolve(20000);

        ApplyReport rep = Apply(plan, new ApplyOptions(Execute: true, Reserved: [(20000, 10000)]));

        Assert.True(rep.ZoneRetargeted);
        Assert.NotEqual(20000, rep.ZoneStartLcn);
        Assert.All(Vol.Moves, m => Assert.False(m.Lcn < 30000 && m.Lcn + m.N > 20000));
    }

    [Fact]
    public void AReservationOutsideTheRemainingTargetIsHarmless()
    {
        PlanFile plan = Make().Resolve(20000);

        ApplyReport rep = Apply(plan, new ApplyOptions(Execute: true, Reserved: [(90000, 100)]));

        Assert.Empty(rep.Failed);
        Assert.NotEmpty(Vol.Moves);
    }

    [Fact]
    public void ReservationOnlyCoversWhatIsLeftToMove()
    {
        PlanFile plan = Make().Resolve(20000);
        Apply(plan, new ApplyOptions(Execute: true)); // 전부 옮김: 남은 목표가 없음
        Vol.Moves.Clear();

        ApplyReport rep = Apply(plan, new ApplyOptions(Execute: true, Reserved: [(20000, 100)]));

        Assert.Empty(rep.Failed);
        Assert.Empty(Vol.Moves);
    }

    [Fact]
    public void FindFreeRunSkipsExcludedRanges()
    {
        var bm = new VolumeBitmap(0, 1000, new byte[125]);

        Assert.Equal(0, bm.FindFreeRun(100));
        Assert.Equal(200, bm.FindFreeRun(100, excluded: [(0, 100), (150, 50)]));
        Assert.Equal(100, bm.FindFreeRun(40, excluded: [(0, 100), (150, 50)]));
        Assert.Equal(100, bm.FindFreeRun(40, excluded: [(150, 50), (0, 100), (10, 20)])); // 순서·겹침 무관
        Assert.Equal(104, bm.FindFreeRun(40, align: 4, excluded: [(0, 101), (150, 50)]));
        Assert.Null(bm.FindFreeRun(100, excluded: [(0, 950)]));
        Assert.Equal(600, bm.FindFreeRun(50, preferLowLcn: false, excluded: [(0, 10), (500, 100)]));
    }
}
