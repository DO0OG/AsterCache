using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using Xunit;

namespace AsterCache.Tests.Core;

/// 통째 배치의 후속 (서비스 연결 전 검토에서 나온 고칠 것): 이미 놓인 파일을 잔여 재계획이 다시 옮기지 않음 (F1), 자기 소스와 목표가 겹치는 파일 (F2),
/// 큰 파일을 한 회에 끝까지 옮기는 규칙과 하루 예산·병렬 실행 (F3), 옮길 수 없는 파일, 예전 범위 계획의 이행 (Rebuild).
/// 파일 둘은 각각 256 클러스터 (1 MiB), 핫 범위는 둘 다 160 클러스터라 통째 대상입니다.
public sealed class WholeFileLayoutFollowUpTests : ApplyTestBase
{
    private const long Capacity = 4L << 20;
    private const long Mib = 1L << 20;
    private const long Zone = 100000;

    public WholeFileLayoutFollowUpTests() => Sessions = [Run(Enumerable.Range(0, 10).Select(b => (F1, (long)b)).Concat(Enumerable.Range(0, 10).Select(b => (F2, (long)b))).ToArray())];

    private PlanFile Whole(long? start = null, long capacity = Capacity, Func<string, bool>? unmovable = null, long maxFileBytes = long.MaxValue) =>
        PlanFile.Make(Sessions, Root, 2 << 20, Cb, p => Files[p], zoneStartLcn: start, capacityBytes: capacity, unmovableFn: unmovable, maxFileBytes: maxFileBytes);

    private static ApplyOptions Exec(long writeCap = 2L << 30, long raiseLimit = long.MaxValue, int parallel = 1, long chunkBytes = 4L << 20) =>
        new(Execute: true, WriteCap: writeCap, Parallel: parallel, ChunkBytes: chunkBytes) { RaiseLimit = raiseLimit };

    private static PlanFile Residual(PlanFile plan, Func<string, IReadOnlyList<Extent>?> extents, FakeVolume vol) =>
        ResidualPlanner.Build(plan, ResidualPlanner.PreviousTargets(null, [plan]), extents, n => vol.Bitmap().FindFreeRun(n, true, 0, PlanGuards.ZoneAlign));

    // ---- F1: 이미 목표에 놓인 파일은 잔여 재계획에서 다시 옮기지 않음 ----

    [Fact]
    public void TheStatsKeepEachWholeFilesOffsetInTheOrderOfTheWholeFiles()
    {
        PlanFile plan = Whole(Zone);

        Assert.Equal([(F1, 0L), (F2, 256L)], WholeFileLayout.WholeOffsets(plan));
        Assert.Equal(new List<object?> { 0L, 256L }, plan.Stats[WholeFileLayout.WholeOffsetsKey]);
    }

    [Fact]
    public void AResidualPlanKeepsAWholeFileThatNeededNoTaskBecauseItWasAlreadyAtItsTarget()
    {
        Files[F1] = [new Extent(0, Zone, 256)]; // 계획을 만들 때 이미 목표 자리에 한 조각: 작업이 없음
        Vol.Ext[F1] = [new Extent(0, Zone, 256)];
        PlanFile plan = Whole(Zone);
        Assert.DoesNotContain(plan.Tasks, t => t.Path == F1);

        PlanFile residual = Residual(plan, p => Vol.Ext[p], Vol);

        Assert.Equal(new PlanTask(F1, 0, 256, Zone), Assert.Single(residual.Tasks, t => t.Path == F1)); // 고정 작업: 현재 자리 = 목표
        Assert.Equal(256L, residual.Stats["fixed_clusters"]);
        Assert.Equal(256, Assert.Single(residual.Tasks, t => t.Path == F2).Clusters);
    }

    [Fact]
    public void AFileWhoseOnePieceWasAlreadyInPlaceIsNotMovedAgainAfterItsOtherTasksRan()
    {
        Files[F1] = [new Extent(0, 1000, 64), new Extent(64, Zone + 64, 64), new Extent(128, 5000, 128)]; // 가운데 조각이 이미 목표 자리: 작업이 둘로 갈림
        Vol.Ext[F1] = [new Extent(0, 1000, 64), new Extent(64, Zone + 64, 64), new Extent(128, 5000, 128)];
        PlanFile plan = Whole(Zone);
        Assert.Equal(2, plan.Tasks.Count(t => t.Path == F1));
        Assert.Empty(Apply(plan, Exec()).Failed);

        PlanFile residual = Residual(plan, p => Vol.Ext[p], Vol);

        Assert.Equal(512L, residual.Stats["fixed_clusters"]); // 두 파일 모두 목표에 한 조각: 아무것도 다시 옮기지 않음
        Assert.Equal(0L, residual.Stats["moved_bytes"]);
        Assert.True(Verify(residual).Contiguous);
    }

    [Fact]
    public void AResidualPlanStillMovesAWholeFileThatIsNotWhereTheOffsetsPutIt()
    {
        PlanFile plan = Whole(Zone);

        PlanFile residual = Residual(plan, p => Vol.Ext[p], Vol); // 아무것도 옮기지 않은 상태: 둘 다 이전 목표에 없음

        Assert.Equal(0L, residual.Stats["fixed_clusters"]);
        Assert.Equal(512 * Cb, residual.Stats["moved_bytes"]);
    }

    [Fact]
    public void ResidualPlansDoNotCarryTheOffsetsBecauseTheirZoneMoved()
    {
        PlanFile residual = Residual(Whole(Zone), p => Vol.Ext[p], Vol);

        Assert.False(residual.Stats.ContainsKey(WholeFileLayout.WholeOffsetsKey));
        Assert.Empty(WholeFileLayout.WholeOffsets(residual));
    }

    // ---- F2: 자기 소스와 목표가 겹치는 파일 ----

    [Fact]
    public void AOneRunFileAheadOfItsTargetAndOverlappingItIsKeptWhereItIsInsteadOfFailingWithACycle()
    {
        Files[F2] = [new Extent(0, Zone + 100, 256)]; // 목표 [Zone, Zone + 256) 와 자기 자신이 겹침
        Vol.Ext[F2] = [new Extent(0, Zone + 100, 256)];
        Sessions = [Run(Enumerable.Range(0, 10).Select(b => (F2, (long)b)).ToArray())];

        PlanFile plan = Whole(Zone);

        Assert.Empty(plan.Tasks);
        Assert.Equal([(F2, 100L)], WholeFileLayout.WholeOffsets(plan));
        Assert.Equal(356L, plan.Stats["zone_used_clusters"]); // 빈 자리 100 + 파일 256
        Assert.True(Verify(plan).Contiguous);
    }

    [Fact]
    public void AFileBehindItsTargetThatOverlapsItIsMovedPastItsOwnClustersAndTheOrderResolvesTheRest()
    {
        Files[F2] = [new Extent(0, Zone + 100, 256)]; // 앞 파일 F1 이 [Zone, Zone + 256) 를 차지하면 F2 의 목표 [Zone + 256, Zone + 512) 는 자기 소스 끝 (Zone + 356) 과 겹침
        Vol.Ext[F2] = [new Extent(0, Zone + 100, 256)];

        PlanFile plan = Whole(Zone);

        Assert.Equal([F2, F1], plan.Tasks.Select(t => t.Path)); // F1 의 목표가 F2 의 소스와 겹쳐 F2 가 먼저
        Assert.Equal(new PlanTask(F2, 0, 256, Zone + 356), plan.Tasks[0]);
        Assert.Equal([(F1, 0L), (F2, 356L)], WholeFileLayout.WholeOffsets(plan));
        Assert.Empty(Apply(plan, Exec()).Failed);
        Assert.True(Verify(plan).Contiguous);
        Assert.True(Vol.Ext.Values.All(e => e.Count == 1));
    }

    // ---- 통째로 놓을 파일이 하나도 없는 통째 계획 ----

    [Fact]
    public void AWholePlanWithNoWholeFileHasAZeroLengthZoneAndStillAppliesAndVerifies()
    {
        PlanFile plan = Whole(capacity: Cb); // 한도가 1 클러스터라 어떤 파일도 들어가지 않음
        Assert.Empty(WholeFileLayout.WholeFiles(plan)!);
        Assert.Equal(0L, plan.Stats["zone_used_clusters"]);
        string path = Path.Combine(Root, "empty.plan.json");

        ApplyReport rep = Apply(plan, Exec() with { SaveTo = path });

        Assert.Empty(rep.Failed);
        Assert.Equal(0, rep.TasksTodo);
        Assert.NotNull(PlanFile.Load(path).Zone.StartLcn);
        Assert.True(Verify(PlanFile.Load(path)).Contiguous);
        Assert.Equal(Files[F1], Vol.Ext[F1]);
    }

    // ---- 범위 잔여 계획의 예열 예산은 통째 계획 도입 뒤에도 작업 합 그대로 ----

    [Fact]
    public void ARangeResidualPlanKeepsCountingItsTasksForThePrewarmBudget()
    {
        PlanFile ranges = Make(Zone);
        PlanFile residual = ResidualPlanner.Build(ranges, [], p => Vol.Ext[p], n => Vol.Bitmap().FindFreeRun(n, true, 0, PlanGuards.ZoneAlign));
        string planPath = Path.Combine(Directory.CreateDirectory(Path.Combine(Root, "plans")).FullName, "r.plan.json");
        residual.Save(planPath);
        GameSettings game = new("r", ["r.exe"], planPath + ".prefetch.json", planPath, Root, [Root], "GAMES", 60);

        Assert.True(residual.IsResidual);
        Assert.False(residual.Stats.ContainsKey(WholeFileLayout.RuleKey));
        Assert.Equal(residual.Tasks.Sum(t => t.Clusters) * Cb, AsterCache.Service.PrefetchStreamer.ZoneBytes(game));
    }

    // ---- 옮길 수 없는 파일 (FileGuards.RelocationUnmovable 연결) ----

    [Fact]
    public void AnUnmovableFileStaysInPlaceAndDoesNotTakeZoneSpace()
    {
        PlanFile plan = Whole(Zone, unmovable: p => p == F1);

        Assert.Equal([F2], WholeFileLayout.WholeFiles(plan));
        Assert.Equal(1L, ((JObject)plan.Stats[WholeFileLayout.InPlaceFilesKey]!)[WholeFileLayout.UnmovableReason]);
        Assert.Equal(256L, plan.Stats["zone_used_clusters"]);
        Assert.DoesNotContain(plan.Tasks, t => t.Path == F1);
    }

    // ---- F3: 한 회에 끝까지 옮기는 규칙과 쓰기 예산 ----

    private string AddSmallFile()
    {
        string path = Path.Combine(Root, "f3.bin");
        File.WriteAllBytes(path, System.Security.Cryptography.RandomNumberGenerator.GetBytes(16 * (int)Cb)); // 16 클러스터
        Files[path] = [new Extent(0, 12000, 16)];
        Vol.Ext[path] = [new Extent(0, 12000, 16)];
        Sessions = [Run((F1, 0), (F1, 1), (F1, 2), (F1, 3), (F1, 4), (F1, 5), (F1, 6), (F1, 7), (F1, 8), (F1, 9),
            (F2, 0), (F2, 1), (F2, 2), (F2, 3), (F2, 4), (F2, 5), (F2, 6), (F2, 7), (F2, 8), (F2, 9), (path, 0))];
        return path;
    }

    [Fact]
    public void AFileLargerThanTheDailyRemainderIsDeferredAndTheSmallerFilesAfterItStillMove()
    {
        string small = AddSmallFile();
        PlanFile plan = Whole(Zone);

        ApplyReport rep = Apply(plan, Exec(writeCap: Mib / 2, raiseLimit: Mib / 2));

        Assert.Equal(2, rep.DeferredFiles);
        Assert.Equal(16 * Cb, rep.MovedBytes); // 작은 파일만
        Assert.Equal("write_cap", rep.Stopped); // 미룬 파일이 남았으므로 끝난 것이 아님
        Assert.Equal(Files[F1], Vol.Ext[F1]);
        Assert.Equal(Files[F2], Vol.Ext[F2]);
        Assert.Single(Vol.Ext[small]);
        Assert.Equal(Zone + 512, Vol.Ext[small][0].Lcn);

        ApplyReport next = Apply(plan, Exec()); // 다음 날: 예산이 다시 충분함

        Assert.Equal(0, next.DeferredFiles);
        Assert.Null(next.Stopped);
        Assert.True(Verify(plan).Contiguous);
    }

    [Fact]
    public void TheFirstFileIsRaisedOnlyUpToTheRaiseLimit()
    {
        PlanFile plan = Whole(Zone);

        ApplyReport raised = Apply(plan, Exec(writeCap: Mib / 2, raiseLimit: Mib));

        Assert.Equal((Mib, 0L, "write_cap"), (raised.MovedBytes, raised.DeferredFiles, raised.Stopped)); // 상한을 1 MiB 까지 올려 첫 파일을 끝까지, 둘째는 예산이 없어 시작하지 않음
        Assert.Single(Vol.Ext[F1]);
        Assert.Equal(Files[F2], Vol.Ext[F2]);
    }

    [Fact]
    public void WithoutADailyBudgetTheFirstFileIsRaisedToItsSizeWhateverTheWriteCap()
    {
        PlanFile plan = Whole(Zone);

        ApplyReport rep = Apply(plan, Exec(writeCap: Mib / 4)); // RaiseLimit 기본 = 제한 없음

        Assert.Equal(Mib, rep.MovedBytes);
        Assert.Equal("write_cap", rep.Stopped);
        Assert.Single(Vol.Ext[F1]);
    }

    [Fact]
    public void WithParallelWorkersAFileIsNeverStartedWithoutRoomForItsWholeRemainder()
    {
        PlanFile plan = Whole(Zone);

        ApplyReport rep = Apply(plan, Exec(writeCap: Mib + Mib / 2, parallel: 2, chunkBytes: 16 * Cb));

        Assert.Equal((Mib, "write_cap"), (rep.MovedBytes, rep.Stopped)); // 둘째 파일은 예산 0.5 MiB 로는 끝낼 수 없어 시작하지 않음 (병렬이어도 도중에 멈춘 파일이 없음)
        Assert.Single(Vol.Ext[F1]);
        Assert.Equal(Files[F2], Vol.Ext[F2]);
    }

    [Fact]
    public void WithParallelWorkersBothFilesMoveWhenTheBudgetCoversThem()
    {
        PlanFile plan = Whole(Zone);

        ApplyReport rep = Apply(plan, Exec(writeCap: 2 * Mib, parallel: 2, chunkBytes: 16 * Cb));

        Assert.Equal((2 * Mib, (string?)null), (rep.MovedBytes, rep.Stopped));
        Assert.True(Verify(plan).Contiguous);
    }

    [Fact]
    public void AFileIsNotSkippedWhenALaterTaskTargetsItsCurrentClusters()
    {
        Files[F1] = [new Extent(0, Zone, 256)];
        Vol.Ext[F1] = [new Extent(0, Zone, 256)];
        Sessions = [Run(Enumerable.Range(0, 10).Select(b => (F2, (long)b)).Concat(Enumerable.Range(0, 10).Select(b => (F1, (long)b))).ToArray())]; // F2 가 먼저라 목표가 F1 의 지금 자리 [Zone, +256)
        PlanFile plan = Whole(Zone);
        Assert.Equal([F1, F2], plan.Tasks.Select(t => t.Path)); // F1 은 [Zone + 256, ...) 로 먼저 비켜야 함

        ApplyReport rep = Apply(plan, Exec(writeCap: Mib, raiseLimit: Mib / 2));

        Assert.Equal(0, rep.MovedBytes);
        Assert.Equal(Files[F2], Vol.Ext[F2]); // F2 는 F1 이 비키지 않아 목표를 쓸 수 없으니 시작하지 않음 (실패로 세지 않음)
        Assert.Empty(rep.Failed);
        Assert.Equal("write_cap", rep.Stopped);
    }

    [Fact]
    public void ARangePlanIsNotAffectedByTheRaiseLimit()
    {
        PlanFile plan = Make().Resolve(20000);

        ApplyReport rep = Apply(plan, Exec(writeCap: 8 * Cb, raiseLimit: 0));

        Assert.Equal((8 * Cb, "write_cap", 0L), (rep.MovedBytes, rep.Stopped, rep.DeferredFiles));
    }

    [Fact]
    public void TheWriteEstimateOfAWholePlanFollowsTheLargestFileButNotTheRaiseLimit()
    {
        PlanFile whole = Whole(Zone);
        PlanFile ranges = Make().Resolve(20000);

        Assert.Equal(Mib, Applier.EstimateWrite(whole, 2 * Mib, Mib / 2, long.MaxValue)); // 큰 파일이 상한을 넘으면 그 파일 크기까지
        Assert.Equal(Mib / 2, Applier.EstimateWrite(whole, 2 * Mib, Mib / 2, Mib / 2)); // 하루 남은 양을 넘지 않음
        Assert.Equal(Mib / 4, Applier.EstimateWrite(whole, Mib / 4, Mib / 2, long.MaxValue)); // 옮길 양이 더 적으면 그 양
        Assert.Equal(Mib / 2, Applier.EstimateWrite(ranges, 2 * Mib, Mib / 2, long.MaxValue)); // 범위 계획은 회당 상한까지
    }

    [Fact]
    public void AFileLargerThanTheDailyBudgetIsLeftInPlaceWhenThePlanIsMade()
    {
        PlanFile plan = Whole(Zone, maxFileBytes: Mib / 2);

        Assert.Empty(WholeFileLayout.WholeFiles(plan)!);
        Assert.Equal(2L, ((JObject)plan.Stats[WholeFileLayout.InPlaceFilesKey]!)[WholeFileLayout.OverDailyLimitReason]);
    }

    // ---- 예전 범위 계획의 이행 (Rebuild) ----

    private PlanFile OldRangePlan()
    {
        PlanFile old = Make(20000);
        old.Stats["source"] = "boot";
        old.Stats["residual"] = 1L;
        old.Stats[ResidualPlanner.HeldKey] = new JObject { { "x", "unreadable" } };
        old.Stats[ResidualPlanner.GenerationKey] = 3L;
        return old;
    }

    [Fact]
    public void AnOldRangePlanIsRebuiltAsAWholePlanInANewZoneKeepingTheOrderAndTheFingerprints()
    {
        PlanFile old = OldRangePlan();

        PlanFile? built = WholeFileLayout.Rebuild(old, p => Vol.Ext[p], _ => false, null, Capacity);

        Assert.NotNull(built);
        Assert.Null(built.Zone.StartLcn); // 새 zone: 적용 때 빈 구간을 고름
        Assert.Equal(old.Order, built.Order);
        Assert.Equal(old.Fingerprints, built.Fingerprints);
        Assert.Equal([F1, F2], WholeFileLayout.WholeFiles(built));
        Assert.Equal([new PlanTask(F1, 0, 256, 0), new PlanTask(F2, 0, 256, 256)], built.Tasks); // 파일마다 작업 하나, 목표는 zone 시작 기준 오프셋
        Assert.Equal("boot", built.Stats["source"]); // 예전 계획의 표지는 물려받음
        Assert.Equal(WholeFileLayout.RuleVersion, built.Stats[WholeFileLayout.RuleKey]);
        Assert.False(built.Stats.ContainsKey("residual")); // 잔여·보류 표지는 이번 판정으로 다시 정함
        Assert.False(built.Stats.ContainsKey(ResidualPlanner.HeldKey));
        Assert.False(built.Stats.ContainsKey(ResidualPlanner.GenerationKey));
        Assert.Equal(512L, built.Stats["zone_used_clusters"]);
    }

    [Fact]
    public void ARebuiltPlanIsAppliedIntoOneRunPerFileInTheNewZone()
    {
        PlanFile built = WholeFileLayout.Rebuild(OldRangePlan(), p => Vol.Ext[p], _ => false, null, Capacity)!;
        string path = Path.Combine(Root, "rebuilt.plan.json");

        Assert.Empty(Apply(built, Exec() with { SaveTo = path }).Failed);

        PlanFile applied = PlanFile.Load(path); // 적용이 정한 zone 시작이 저장됨
        Assert.NotNull(applied.Zone.StartLcn);
        Assert.True(Vol.Ext.Values.All(e => e.Count == 1));
        Assert.True(Verify(applied).Contiguous);
    }

    [Fact]
    public void RebuildGivesUpWhenAFileCannotBeReadAndDropsFilesWithoutDiskExtents()
    {
        PlanFile old = Make(20000);

        Assert.Null(WholeFileLayout.Rebuild(old, p => p == F2 ? null : Vol.Ext[p], _ => false, null, Capacity));

        PlanFile? built = WholeFileLayout.Rebuild(old, p => p == F2 ? [] : Vol.Ext[p], _ => false, null, Capacity);

        Assert.NotNull(built);
        Assert.Equal([F1], WholeFileLayout.WholeFiles(built));
        Assert.DoesNotContain(built.Order, o => o.Path == F2);
        Assert.DoesNotContain(built.Fingerprints, f => f.Path == F2);
        Assert.Equal(1L, built.Stats["skipped_files"]);
    }

    [Fact]
    public void RebuildLeavesUnmovableAndLateFilesInPlaceAndCountsWhy()
    {
        PlanFile old = Make(20000);

        PlanFile unmovable = WholeFileLayout.Rebuild(old, p => Vol.Ext[p], _ => false, p => p == F1, Capacity)!;
        PlanFile tight = WholeFileLayout.Rebuild(old, p => Vol.Ext[p], _ => false, null, 300 * Cb)!;

        Assert.Equal([F2], WholeFileLayout.WholeFiles(unmovable));
        Assert.Equal(1L, ((JObject)unmovable.Stats[WholeFileLayout.InPlaceFilesKey]!)[WholeFileLayout.UnmovableReason]);
        Assert.Equal([F1], WholeFileLayout.WholeFiles(tight));
        Assert.Equal(1L, ((JObject)tight.Stats[WholeFileLayout.InPlaceFilesKey]!)[WholeFileLayout.ZoneFullReason]);
    }
}
