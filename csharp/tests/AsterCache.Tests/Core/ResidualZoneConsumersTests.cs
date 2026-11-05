using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tests.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Core;

/// 잔여 재계획의 Zone 은 새로 옮길 구간뿐 (전부 제자리면 길이 0) 이지만 핫셋은 task 전체입니다. 예열·스트림 예산, 존 없음 판정, 재예열, 보충, 존 소속 판정, 예약이 그 핫셋을 따르는가.
/// 두 형태: A = 일부만 제자리라 존이 새 구간 일부 (74 클러스터), B = 전부 제자리라 존 길이 0. 핫셋은 80 클러스터.
public sealed class ResidualZoneConsumersTests : ApplyTestBase
{
    private const long HotClusters = 80;
    private const long Mib = 1L << 20;
    private readonly FakeStreamer _streamer = new();
    private readonly ListLog _log = new();
    private DateTimeOffset _now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private PlanFile Candidate() => PlanFile.Make(Sessions, Root, 2 << 20, Cb, p => Vol.Ext[p], preserveBytes: 64 << 20);

    private long? FindRun(long n) => Vol.Bitmap().FindFreeRun(n, true, 0, PlanGuards.ZoneAlign);

    // 형태 A: 첫 task 의 앞 6 클러스터만 옮겨져 있음. 형태 B: 다섯 task 전부 옮겨져 있음.
    private PlanFile Residual(bool allInPlace)
    {
        PlanTask[] prev = [new(F1, 192, 16, 20000), new(F2, 0, 16, 20016), new(F1, 0, 16, 20032), new(F1, 64, 16, 20048), new(F2, 128, 16, 20064)];
        foreach (PlanTask t in allInPlace ? prev : prev.Take(1))
        {
            Vol.Move(t.Path, t.Vcn, t.TargetLcn, (uint)(allInPlace ? t.Clusters : 6));
        }

        PlanFile plan = ResidualPlanner.Build(Candidate(), allInPlace ? prev : prev.Take(1).ToList(), p => Vol.Ext[p], FindRun);
        Assert.True(plan.IsResidual);
        Assert.Equal(allInPlace ? 0 : HotClusters - 6, plan.Zone.Clusters);
        return plan;
    }

    // 계획 파일과 선읽기 추적을 디스크에 둔 게임. scaledMib 가 있으면 클러스터 크기를 키워 핫셋을 MiB 단위로 만듦 (상한 시험용).
    private GameSettings GameOf(PlanFile plan, long clusterBytes = Cb)
    {
        PlanFile scaled = plan with { ClusterBytes = clusterBytes };
        string planPath = Path.Combine(Directory.CreateDirectory(Path.Combine(Root, "plans")).FullName, "a.plan.json");
        scaled.Save(planPath);
        string trace = planPath + ".prefetch.json";
        File.WriteAllText(trace, PyJson.Dumps(plan.PrefetchTrace(Sessions).Select(e => e.ToJson()).ToList(), ensureAscii: false) + "\n");
        return Games.Make("a", "a.exe") with { Plan = planPath, Trace = trace, OriginRoot = Root, AllowedRoots = [Root], Prewarm = PrewarmSettings.Default with { Pin = false, PinSet = true } };
    }

    private Prewarmer Prewarmer(GameSettings g, ProbeResult? probe = null)
    {
        var p = new Prewarmer([g], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = _ => false,
            ZoneBytes = PrewarmEnv.Create().ZoneBytes, // 실제 계획 파일을 읽는 판정
            TraceExists = _ => true,
            CpuPercent = () => 1,
            DiskBytesPerSecond = _ => 0,
            ProbeReadMs = _ => probe,
        }, _log, null, () => _now);
        return p;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BudgetsAndTheNoZoneJudgementFollowTheWholeHotSetOfAResidualPlan(bool allInPlace)
    {
        GameSettings g = GameOf(Residual(allInPlace));

        long? zone = PrefetchStreamer.ZoneBytes(g);

        Assert.Equal(HotClusters * Cb, zone);
        Assert.Equal(zone, PrewarmEnv.Create().ZoneBytes(g));
        Assert.Null(PrewarmPolicy.SkipReason(zone, true, false, 16000, 0, null, TimeSpan.Zero)); // 길이 0 이어도 "no zone" 이 아님
        Assert.Equal(HotClusters * Cb, PrefetchStreamer.Budget(g, zone, 0));
        Assert.Equal(HotClusters * Cb, PrefetchStreamer.PinCapBytes(zone, 64L << 30, 100000, 0)); // 고정 상한도 핫셋 크기
    }

    [Fact]
    public void ANormalPlanStillUsesItsZoneSize()
    {
        PlanFile plan = Make().Resolve(20000);
        GameSettings g = GameOf(plan);

        Assert.False(plan.IsResidual);
        Assert.Equal(plan.Zone.Clusters * Cb, PrefetchStreamer.ZoneBytes(g));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheStreamCapStillCutsTheWholeHotSetOfAResidualPlan(bool allInPlace)
    {
        GameSettings g = GameOf(Residual(allInPlace), Mib) with { MaxStreamMib = 32, MaxStreamSet = true }; // 핫셋 80 MiB

        Assert.Equal(32 * Mib, PrewarmEnv.Create().ZoneBytes(g));
        Assert.Equal(32 * Mib, PrefetchStreamer.Budget(g, PrefetchStreamer.ZoneBytes(g), 0));
        Assert.Equal(80 * Mib, PrefetchStreamer.Budget(g with { MaxStreamSet = false, MaxStreamMib = 4096 }, PrefetchStreamer.ZoneBytes(g), 0));
    }

    [Fact]
    public void TheReadListOfAPartlyInPlaceResidualPlanIsNotCutAtTheNewZoneSize()
    {
        GameSettings g = GameOf(Residual(false));

        long read = PrefetchStreamer.Ops(g).Sum(o => o.Length);

        Assert.Equal(HotClusters * Cb, read); // 새 구간 (74 클러스터) 크기로 자르면 핫셋 끝이 빠짐
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ServiceStartPrewarmsAResidualPlanInsteadOfSkippingItAsNoZone(bool allInPlace)
    {
        Prewarmer p = Prewarmer(GameOf(Residual(allInPlace)));

        PrewarmResult r = Assert.Single(p.RunServiceStart());

        Assert.True(r.Ran, r.SkipReason);
        Assert.Single(_streamer.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AColdSampleRewarmsAResidualPlan(bool allInPlace)
    {
        GameSettings g = GameOf(Residual(allInPlace));
        Prewarmer p = Prewarmer(g, new ProbeResult(8, 8, 5.0));
        Assert.True(p.Run(g, "service start").Ran);
        AutoSettings auto = AutoSettings.Default with { RewarmMinutes = 10 };
        p.Tick(auto);
        _now += TimeSpan.FromMinutes(10);

        p.Tick(auto);

        Assert.Equal(2, _streamer.Calls.Count);
        Assert.Contains(_log.Lines, l => l.Contains("a: prewarm done (rewarm)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheStartupCapTrimsAgainstTheWholeHotSetAndTheSupplementReadsTheRest()
    {
        GameSettings g = GameOf(Residual(false), Mib); // 핫셋 80 MiB, 새 구간 74 MiB
        string status = Path.Combine(Root, "prewarm_status.json");
        var p = new Prewarmer([g], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = _ => false,
            ZoneBytes = PrewarmEnv.Create().ZoneBytes,
            TraceExists = _ => true,
            DiskBytesPerSecond = _ => 0,
        }, _log, status, () => _now);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 50 });

        Assert.True(Assert.Single(p.RunServiceStart()).Ran);

        var s = (JObject)((JObject)((JObject)JsonRead.ReadFile(status)!)["games"]!)["a"]!;
        Assert.Equal((50L, 80L), ((long)s["warmed_mib"]!, (long)s["hot_mib"]!)); // 새 구간 크기 74 가 아니라 핫셋 80
        Assert.True(p.SupplementPending("a"));
        Assert.False(p.SupplementWhenIdle(g, default));
        _now += TimeSpan.FromSeconds(61);
        Assert.True(p.SupplementWhenIdle(g, default));
        Assert.False(p.SupplementPending("a"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZoneHitCountsEveryPieceOnItsTargetAndAPartialStateNeedsAResume(bool allInPlace)
    {
        PlanFile plan = Residual(allInPlace);
        LayoutReport before = Verify(plan);

        Assert.Equal(allInPlace ? HotClusters : 6, before.InZoneClusters);
        Assert.Equal(!allInPlace, before.ResumeNeeded);

        Apply(plan, new ApplyOptions(Execute: true));
        LayoutReport after = Verify(plan);

        Assert.Equal(HotClusters, after.InZoneClusters); // 존 길이가 0 이든 새 구간뿐이든 100 %
        Assert.Equal(after.HotClusters, after.InZoneClusters);
        Assert.True(after.Contiguous);
    }

    [Fact]
    public void ZoneHitOfANormalPlanIsStillTheZoneOverlap()
    {
        PlanFile plan = Make().Resolve(20000);

        Assert.Equal(0, Verify(plan).InZoneClusters);
        Apply(plan, new ApplyOptions(Execute: true));
        Assert.Equal(HotClusters, Verify(plan).InZoneClusters);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReservationsCoverTheFixedPiecesAndTheNewRunEvenWhenTheZoneLengthIsZero(bool allInPlace)
    {
        PlanFile plan = Residual(allInPlace);
        string plans = Directory.CreateDirectory(Path.Combine(Root, "plans")).FullName;
        plan.Save(Path.Combine(plans, "a.plan.json"));

        IReadOnlyList<(long Lcn, long Clusters)> reserved = ZoneReservations.Collect(plans, Letter, Path.Combine(plans, "b.plan.json"));

        foreach (PlanTask t in plan.Tasks)
        {
            Assert.Contains(reserved, r => r.Lcn <= t.TargetLcn && t.TargetLcn + t.Clusters <= r.Lcn + r.Clusters);
        }

        Assert.Equal(HotClusters, reserved.Sum(r => r.Clusters));
    }

    [Fact]
    public void VolatileWarmRangesAreBudgetedAndClipToShrunkFileSize()
    {
        PlanFile plan = Make().Resolve(20000);
        GameSettings game = GameOf(plan);
        long planBytes = PrefetchStreamer.ZoneBytes(game)!.Value;
        string cache = Path.Combine(Root, "cache_1_2.xpakdata");
        int fileBytes = (1 << 20) + 100;
        int tracedBytes = fileBytes + 100;
        File.WriteAllBytes(cache, new byte[tracedBytes]);
        PlanOutput.SavePrefetch(game.Trace, [.. plan.PrefetchTrace(Sessions), new TraceEvent(cache, 0, tracedBytes), new TraceEvent(cache, tracedBytes, 10)]);
        using (FileStream shrinking = new(cache, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            shrinking.SetLength(fileBytes);
        }

        long? warmBytes = PrewarmEnv.Create().ZoneBytes(game);
        int available = (int)((planBytes + Mib - 1) / Mib);
        Assert.Equal(Math.Max(planBytes, plan.PrefetchTrace(Sessions).Sum(r => r.Length)) + fileBytes, warmBytes);
        Assert.Null(PrewarmPolicy.SkipReason(planBytes, true, false, available, 0, null, TimeSpan.Zero));
        Assert.StartsWith("low memory", PrewarmPolicy.SkipReason(warmBytes, true, false, available, 0, null, TimeSpan.Zero));

        using FileStream shared = new(cache, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        List<TraceEvent> loaded = TraceLoader.LoadRanges(game.Trace, Root);
        Assert.Contains(loaded, row => row.Path == cache && row.Offset == 0 && row.Length == fileBytes);
        Assert.DoesNotContain(loaded, row => row.Path == cache && row.Offset >= fileBytes);
    }
}
