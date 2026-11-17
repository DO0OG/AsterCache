using System.Security.Cryptography;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Volume;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Core;

/// 스파스 파일의 할당 구간 재배치: 계획·실행·완료 판정·할당 변화 감지·배치 검사·되돌리기를 가짜 볼륨과 실제 임시 파일로 확인합니다.
/// 시험 파일 s.bin 은 1 MiB (256 클러스터), 할당 구간은 VCN [0, 64) 와 [96, 160), 나머지는 구멍입니다.
public sealed class SparseRelocationTests : ApplyTestBase
{
    private static readonly AllocatedRange[] OriginalAllocation = [new(0, 64), new(96, 64)];
    private readonly string _s;

    public SparseRelocationTests()
    {
        _s = Path.Combine(Root, "s.bin");
        File.WriteAllBytes(_s, RandomNumberGenerator.GetBytes(1 << 20));
        SetExtents(_s, [new(0, 30000, 64), new(96, 40000, 64)]);
    }

    private void SetExtents(string path, List<Extent> extents)
    {
        Files[path] = [.. extents];
        Vol.Ext[path] = [.. extents];
    }

    private static ApplyOptions Exec(long chunkBytes = 4L << 20, long writeCap = 2L << 30) => new(Execute: true, ChunkBytes: chunkBytes, WriteCap: writeCap);

    private PlanFile MakeFor(string path, params long[] blocks) =>
        PlanFile.Make([Run(blocks.Select(b => (path, b)).ToArray())], Root, 2 << 20, Cb, p => Files[p], preserveBytes: 64 << 20, sparseFn: p => p == path).Resolve(20000);

    private PlanFile MakeSparse(params long[] blocks) => MakeFor(_s, blocks);

    private ApplyHooks HooksWithExtents(Func<string, IReadOnlyList<Extent>?> extents) => new()
    {
        Extents = extents,
        Digest = (p, size, extra) => SampleHasher.Compute(p, size, extra, null),
        SkipReason = _ => null,
        Sleep = _ => { },
    };

    private static IReadOnlyList<AllocatedRange> Allocation(FakeVolume vol, string path) => SparseAllocation.Normalize(vol.Ext[path]);

    // ---- 계획

    [Fact]
    public void PlanMakesTasksOnlyForAllocatedUnitsAndAssignsNoZoneSpaceToHoles()
    {
        PlanFile plan = MakeSparse(0, 4, 6, 10); // 블록 4 (VCN 64~80) 와 블록 10 (VCN 160~176) 은 구멍

        Assert.Equal([(0L, 16L), (96L, 16L)], plan.Order.Select(o => (o.Vcn, o.Clusters)));
        Assert.Equal([(0L, 16L), (96L, 16L)], plan.Tasks.Select(t => (t.Vcn, t.Clusters)));
        Assert.Equal(OriginalAllocation, plan.SparseAllocations[_s]);
        Assert.Equal(4L, plan.Stats["hot_blocks"]); // 기존 뜻 (추적의 핫 블록 수) 그대로
        Assert.Equal(4L, plan.Stats["units"]);
        Assert.Equal(2L, plan.Stats["supported_hot_blocks"]); // 지원 구간 기준 값은 새 키
        Assert.Equal(2L, plan.Stats["supported_units"]);
        Assert.Equal(32L * Cb, plan.Stats["moved_bytes"]);
        Assert.All(plan.Tasks, t => Assert.Equal(0, t.Vcn % 16)); // 시작 VCN 과 길이가 16클러스터 경계
        Assert.Equal(plan.Tasks[0].TargetLcn + 16, plan.Tasks[1].TargetLcn); // 구멍 몫 없이 이어 붙임
    }

    [Fact]
    public void PlanSkipsAFileWhoseAllocatedPartDoesNotFillAUnit()
    {
        SetExtents(_s, [new(0, 30000, 40)]); // 할당 [0, 40): 블록 2 (VCN 32~48) 와의 겹침 [32, 40) 은 한 단위를 못 채움

        PlanFile plan = MakeSparse(2);

        Assert.Empty(plan.Order);
        Assert.Empty(plan.Tasks);
        Assert.Empty(plan.SparseAllocations);
        Assert.Equal(1L, plan.Stats["skipped_files"]);
    }

    [Fact]
    public void PlanKeepsTheLastShortPieceAtTheEndOfTheFileAndDoesNotMergeItWithTheUnitBeforeIt()
    {
        string t = Path.Combine(Root, "t.bin");
        File.WriteAllBytes(t, RandomNumberGenerator.GetBytes(100 * (int)Cb + 1234)); // 101 클러스터, 마지막 조각 5클러스터
        SetExtents(t, [new(0, 50000, 64), new(64, 60000, 48)]); // 할당은 파일 끝 뒤 (VCN 112) 까지

        PlanFile plan = MakeFor(t, 5, 6);

        Assert.Equal([(80L, 16L), (96L, 5L)], plan.Order.Select(o => (o.Vcn, o.Clusters)));
        Assert.Equal([(80L, 16L), (96L, 5L)], plan.Tasks.Select(x => (x.Vcn, x.Clusters)));
    }

    [Fact]
    public void PlanForFilesWithoutTheSparseHookIsIdenticalToThePlanBefore()
    {
        PlanFile without = PlanFile.Make(Sessions, Root, 2 << 20, Cb, p => Files[p], preserveBytes: 64 << 20);
        PlanFile hooked = PlanFile.Make(Sessions, Root, 2 << 20, Cb, p => Files[p], preserveBytes: 64 << 20, sparseFn: _ => false);

        Assert.Equal(without.Dumps(), hooked.Dumps());
        Assert.Empty(without.SparseAllocations);
        Assert.DoesNotContain("sparse_allocations", without.Dumps(), StringComparison.Ordinal);
    }

    // ---- 계획 파일 형식

    [Fact]
    public void PlanRoundTripKeepsTheAllocationAndAnOldFormatPlanLoadsWithoutSparseFiles()
    {
        PlanFile plan = MakeSparse(0, 6);

        PlanFile again = PlanFile.FromJson(JsonRead.Parse(plan.Dumps()));
        Assert.Equal(OriginalAllocation, again.SparseAllocations[_s]);
        Assert.Equal(plan.Sha(), again.Sha());

        JObject old = plan.ToJson();
        _ = old.Remove("sparse_allocations");
        PlanFile loaded = PlanFile.FromJson(JsonRead.Parse(PyJson.Dumps(old)));
        Assert.Empty(loaded.SparseAllocations);
        Assert.Equal(plan.Tasks, loaded.Tasks);
    }

    [Theory]
    [InlineData("[[0,64],[32,16]]")] // 겹침
    [InlineData("[[0,0]]")] // 길이 0
    [InlineData("[[0]]")] // 모양이 틀림
    public void MalformedSparseAllocationIsRejected(string ranges)
    {
        JObject json = MakeSparse(0, 6).ToJson();
        json["sparse_allocations"] = new JObject { { _s, JsonRead.Parse(ranges) } };

        Assert.Throws<PyValueException>(() => PlanFile.FromJson(JsonRead.Parse(PyJson.Dumps(json))));
    }

    // ---- 실행

    [Fact]
    public void ApplyMovesOnlyAllocatedUnitsOnTheBoundaryAndKeepsTheAllocation()
    {
        PlanFile plan = MakeSparse(0, 4, 6, 10);

        ApplyReport rep = Apply(plan, Exec(8 * Cb)); // 청크가 한 단위보다 작아도 한 단위씩

        Assert.Empty(rep.Failed);
        Assert.Null(rep.Stopped);
        Assert.Equal([(0L, 16L), (96L, 16L)], Vol.Moves.Select(m => (m.Vcn, m.N)));
        Assert.Equal(32L * Cb, rep.SparseMovedBytes);
        Assert.Equal(rep.MovedBytes, rep.SparseMovedBytes);
        Assert.Equal(OriginalAllocation, Allocation(Vol, _s)); // 구멍은 그대로 구멍
        Assert.True(Verify(plan).Contiguous);
        Assert.Contains(new Journal(JournalPath).Rows, r => Equals(r["type"], "header") && r.ContainsKey("sparse_allocations"));
    }

    [Fact]
    public void ApplyMovesTheShortLastPieceAsOneCall()
    {
        string t = Path.Combine(Root, "t.bin");
        File.WriteAllBytes(t, RandomNumberGenerator.GetBytes(100 * (int)Cb + 1234));
        SetExtents(t, [new(0, 50000, 64), new(64, 60000, 48)]);
        PlanFile plan = MakeFor(t, 5, 6);

        ApplyReport rep = Apply(plan, Exec());

        Assert.Empty(rep.Failed);
        Assert.Equal([(80L, 16L), (96L, 5L)], Vol.Moves.Select(m => (m.Vcn, m.N)));
        Assert.Equal(21L * Cb, rep.SparseMovedBytes);
        Assert.True(Verify(plan).Contiguous);
    }

    [Fact]
    public void AMoveThatSucceedsWithoutChangingTheLayoutIsAFailure()
    {
        PlanFile plan = MakeSparse(0, 6);
        using var silent = new SilentVolume(Vol);

        ApplyReport rep = Applier.Apply(plan, silent, [Root], JournalPath, Hooks(), Exec());

        Assert.Equal(2, rep.Failed.Count);
        Assert.All(rep.Failed, f => Assert.Equal("extent_mismatch", f.Reason));
        Assert.Equal(0L, rep.SparseMovedBytes); // 배치가 바뀌지 않은 호출은 옮긴 양과 쓰기 예산에 세지 않음
        Assert.Equal(0L, rep.MovedBytes);
        Assert.False(Verify(plan).Contiguous);
    }

    [Fact]
    public void AnEarlierChunkThatSilentlyDidNotMoveDoesNotHideTheLaterChunkThatDid()
    {
        PlanFile plan = MakeSparse(0, 1); // VCN 0~32 한 작업
        using var firstSilent = new FirstMoveSilentVolume(Vol);

        ApplyReport rep = Applier.Apply(plan, firstSilent, [Root], JournalPath, Hooks(), Exec(chunkBytes: 16 * Cb));

        Assert.Equal(2, firstSilent.Calls);
        Assert.Single(Vol.Moves); // 두 번째 청크만 실제로 옮겨짐
        Assert.Equal("extent_mismatch", Assert.Single(rep.Failed).Reason);
        Assert.Equal(16 * Cb, rep.MovedBytes); // 앞에서부터 이어진 양 (0) 이 아니라 실제로 놓인 양
        Assert.Equal(16 * Cb, rep.SparseMovedBytes);
    }

    [Fact]
    public void WriteBudgetSmallerThanOneUnitMovesNothingAndStops()
    {
        PlanFile plan = MakeSparse(0, 6);

        ApplyReport rep = Apply(plan, Exec(writeCap: 8 * Cb));

        Assert.Equal("write_cap", rep.Stopped);
        Assert.Empty(Vol.Moves);
        Assert.Equal(0L, rep.SparseMovedBytes);
    }

    [Fact]
    public void ResumeAfterABudgetStopFinishesTheRemainingUnits()
    {
        PlanFile plan = MakeSparse(0, 6);

        ApplyReport first = Apply(plan, Exec(writeCap: 20 * Cb)); // 한 단위 (16) 만 들어가고 남은 4 는 한 단위가 못 됨
        Assert.Equal("write_cap", first.Stopped);
        Assert.Single(Vol.Moves);

        ApplyReport second = Apply(plan, Exec());
        Assert.Null(second.Stopped);
        Assert.Empty(second.Failed);
        Assert.Equal(2, Vol.Moves.Count);
        Assert.True(Verify(plan).Contiguous);
        Assert.Equal(OriginalAllocation, Allocation(Vol, _s));
    }

    // ---- 할당 변화 감지

    [Theory]
    [InlineData("filled")]
    [InlineData("released")]
    [InlineData("lookup-failed")]
    [InlineData("modified")]
    public void AFileWhoseAllocationChangedSincePlanningIsHeldAndNotMoved(string change)
    {
        PlanFile plan = MakeSparse(0, 6);
        Func<string, IReadOnlyList<Extent>?> extents = p => Vol.Ext[p];
        switch (change)
        {
            case "filled":
                Vol.Ext[_s] = [.. Vol.Ext[_s], new Extent(64, 50000, 16)]; // 구멍에 데이터가 써짐
                break;
            case "released":
                Vol.Ext[_s] = [new(0, 30000, 64)]; // 할당 구간이 해제됨
                break;
            case "lookup-failed":
                extents = p => p == _s ? null : Vol.Ext[p];
                break;
            default:
                File.SetLastWriteTimeUtc(_s, DateTime.UtcNow.AddHours(-3)); // 지문 (수정 시각) 이 달라짐
                break;
        }

        ApplyReport rep = Apply(plan, Exec(), HooksWithExtents(extents));

        Assert.Empty(Vol.Moves);
        Assert.Equal(1L, rep.SparseAllocationChangedFiles);
        Assert.Equal([new KeyValuePair<string, string>(_s, "sparse allocation changed")], rep.SkippedFiles);
        Assert.Equal(1L, rep.ToJson()["sparse_allocation_changed_files"]);
        Assert.Contains("1 sparse file(s) held", rep.SparseSummary, StringComparison.Ordinal);
    }

    [Fact]
    public void AllocationThatChangesBetweenTasksStopsTheFileAtTheNextTask()
    {
        PlanFile plan = MakeSparse(0, 6);
        Vol.AfterMove = n =>
        {
            if (n == 1)
            {
                Vol.Ext[_s] = [.. Vol.Ext[_s], new Extent(64, 50000, 16)]; // 첫 이동 직후 구멍이 채워짐
            }
        };

        ApplyReport rep = Apply(plan, Exec());

        Assert.Single(Vol.Moves);
        Assert.Equal(1L, rep.SparseAllocationChangedFiles);
        Assert.Contains(rep.Failed, f => f.Reason == "sparse allocation changed");
    }

    [Fact]
    public void ChangedFileDoesNotStopOtherFilesFromMoving()
    {
        PlanFile plan = PlanFile.Make([Run((_s, 0), (F1, 0))], Root, 2 << 20, Cb, p => Files[p], preserveBytes: 64 << 20, sparseFn: p => p == _s).Resolve(20000);
        Vol.Ext[_s] = [.. Vol.Ext[_s], new Extent(64, 50000, 16)];

        ApplyReport rep = Apply(plan, Exec());

        Assert.Equal(1L, rep.SparseAllocationChangedFiles);
        Assert.NotEmpty(Vol.Moves);
        Assert.All(Vol.Moves, m => Assert.Equal(F1, m.Path));
    }

    // ---- 배치 검사

    [Fact]
    public void LayoutCheckDoesNotCountAReleasedOrFilledAllocationAsDone()
    {
        PlanFile plan = MakeSparse(0, 6);
        Apply(plan, Exec());
        Assert.True(Verify(plan).Contiguous);
        List<Extent> done = [.. Vol.Ext[_s]];

        Vol.Ext[_s] = [.. done.Where(e => e.Vcn >= 96)]; // [0, 64) 가 해제됨
        LayoutReport released = Verify(plan);
        Assert.False(released.Contiguous);
        Assert.Equal(1L, released.SparseAllocationChangedFiles);
        Assert.True(released.MissingFiles > 0);
        Assert.False(released.ResumeNeeded);

        Vol.Ext[_s] = [.. done, new Extent(64, 50000, 16)]; // 구멍이 채워짐
        LayoutReport filled = Verify(plan);
        Assert.False(filled.Contiguous);
        Assert.Equal(1L, filled.SparseAllocationChangedFiles);
        Assert.False(filled.ResumeNeeded);

        LayoutReport failed = LayoutVerifier.Verify(plan, p => p == _s ? null : Vol.Ext[p]); // 조회 실패도 보류
        Assert.False(failed.Contiguous);
        Assert.Equal(1L, failed.SparseAllocationChangedFiles);
    }

    [Fact]
    public void LayoutCheckThatHoldsChangedSparseFilesLeavesThemOutInsteadOfCountingThemChanged()
    {
        PlanFile plan = MakeSparse(0, 6);
        Apply(plan, Exec());
        List<Extent> done = [.. Vol.Ext[_s]];
        Vol.Ext[_s] = [.. done, new Extent(64, 50000, 16)]; // 구멍이 채워짐

        LayoutReport held = LayoutVerifier.Verify(plan, p => Vol.Ext[p], holdChangedSparse: true);
        Assert.True(held.Contiguous);
        Assert.Equal(1L, held.SparseHeldFiles);
        Assert.Equal(0L, held.SparseAllocationChangedFiles);
        Assert.Equal(0L, held.MissingFiles);

        LayoutReport failed = LayoutVerifier.Verify(plan, p => p == _s ? null : Vol.Ext[p], holdChangedSparse: true); // 조회 실패도 보류
        Assert.True(failed.Contiguous);
        Assert.Equal(1L, failed.SparseHeldFiles);

        Assert.False(Verify(plan).Contiguous); // 옵션을 주지 않으면 기존대로 변화로 셈 (CLI 검사, 자동 재배치)
    }

    [Fact]
    public void LayoutCheckOfAPlanWithoutSparseFilesIsUnchanged()
    {
        PlanFile plan = Make().Resolve(20000);

        Assert.Equal(0L, Verify(plan).SparseAllocationChangedFiles);
        Assert.DoesNotContain("sparse_allocation_changed_files", Verify(plan).ToJson().Select(kv => kv.Key));
    }

    // ---- 되돌리기

    [Fact]
    public void RevertRestoresTheOriginalPlacesOnTheSameBoundaries()
    {
        PlanFile plan = MakeSparse(0, 6);
        Apply(plan, Exec());
        int forward = Vol.Moves.Count;

        RevertReport rep = Reverter.Revert(JournalPath, Vol, [Root], Hooks(), new ApplyOptions(Execute: true));

        Assert.Empty(rep.Skipped);
        Assert.Equal(new List<Extent> { new(0, 30000, 64), new(96, 40000, 64) }, Vol.Ext[_s]);
        Assert.All(Vol.Moves.Skip(forward), m => Assert.True(m.Vcn % 16 == 0 && m.N % 16 == 0));
        Assert.Equal(OriginalAllocation, Allocation(Vol, _s));
    }

    [Fact]
    public void RevertWithASmallChunkAndWriteBudgetStillMovesWholeUnits()
    {
        PlanFile plan = MakeSparse(0, 6);
        Apply(plan, Exec());
        int forward = Vol.Moves.Count;

        RevertReport rep = Reverter.Revert(JournalPath, Vol, [Root], Hooks(), new ApplyOptions(Execute: true, ChunkBytes: 4 * Cb, WriteCap: 8 * Cb));

        Assert.Equal("write_cap", rep.Stopped);
        Assert.Equal(forward, Vol.Moves.Count); // 한 단위보다 작은 예산으로는 아무것도 옮기지 않음
    }

    [Fact]
    public void RevertSkipsAFileWhoseAllocationChangedAndRecordsIt()
    {
        PlanFile plan = MakeSparse(0, 6);
        Apply(plan, Exec());
        int forward = Vol.Moves.Count;
        Vol.Ext[_s] = [.. Vol.Ext[_s], new Extent(64, 50000, 16)];

        RevertReport rep = Reverter.Revert(JournalPath, Vol, [Root], Hooks(), new ApplyOptions(Execute: true));

        Assert.Equal(forward, Vol.Moves.Count);
        Assert.Equal(2, rep.Skipped.Count);
        Assert.All(rep.Skipped, s => Assert.Equal("sparse allocation changed", s.Reason));
    }

    [Fact]
    public void RevertSkipsOriginalPiecesThatAreOffTheBoundary()
    {
        SetExtents(_s, [new(0, 30000, 8), new(8, 31000, 8), new(96, 40000, 64)]); // 한 단위가 물리 조각 둘로 나뉘어 있음
        PlanFile plan = MakeSparse(0, 6);
        Apply(plan, Exec()); // 정방향은 한 단위로 한 번에 옮김
        Assert.Equal(16L, Vol.Moves[0].N);
        int forward = Vol.Moves.Count;

        RevertReport rep = Reverter.Revert(JournalPath, Vol, [Root], Hooks(), new ApplyOptions(Execute: true));

        Assert.Equal(2, rep.Skipped.Count(s => s.Reason == "unsupported sparse range"));
        Assert.Equal(forward + 1, Vol.Moves.Count); // 경계에 맞는 두 번째 단위만 되돌림
        Assert.Equal(96L, Vol.Moves[^1].Vcn);
        Assert.Equal([new AllocatedRange(0, 16), new AllocatedRange(96, 64)], Allocation(Vol, _s)); // 할당 구간 자체는 그대로
    }

    // ---- 스파스가 아닌 파일

    [Fact]
    public void NonSparseFilesAreStillPlannedAndMovedWithTheirOriginalRules()
    {
        PlanFile plan = Make().Resolve(20000); // 스파스 훅 없음: 모든 파일이 일반 파일
        ApplyReport rep = Apply(plan, Exec(8 * Cb));

        Assert.Empty(rep.Failed);
        Assert.Equal(0L, rep.SparseMovedBytes);
        Assert.DoesNotContain("sparse_moved_bytes", rep.ToJson().Select(kv => kv.Key));
        Assert.True(Verify(plan).Contiguous);
    }

    // ---- 이전 형식 계획, 구역 확인, 지원하지 못하는 조각

    private ApplyHooks SparseAwareHooks(string sparsePath, Func<string, string?>? skip = null) => new()
    {
        Extents = p => Vol.Ext[p],
        Digest = (p, size, extra) => SampleHasher.Compute(p, size, extra, null),
        SkipReason = skip ?? (_ => null),
        IsSparse = p => p == sparsePath,
        Sleep = _ => { },
    };

    [Fact]
    public void AFullyAllocatedSparseFileInAPlanWithoutTheBaselineIsSkippedAndLeftOutOfTheLayoutCheckAsBefore()
    {
        SetExtents(_s, [new(0, 30000, 256)]);
        PlanFile plan = PlanFile.Make([Run((_s, 0), (_s, 6))], Root, 2 << 20, Cb, p => Files[p], preserveBytes: 64 << 20).Resolve(20000); // 이전 형식 계획: 할당 기준값 없음
        Assert.Empty(plan.SparseAllocations);

        ApplyReport rep = Apply(plan, Exec(), SparseAwareHooks(_s));

        Assert.Empty(Vol.Moves);
        Assert.Equal([new KeyValuePair<string, string>(_s, "sparse")], rep.SkippedFiles);
        LayoutReport before = LayoutVerifier.Verify(plan, p => Vol.Ext[p], p => p == _s); // 기준 커밋의 판정: 스파스는 옮기지 않는 파일
        Assert.Equal(before, LayoutVerifier.Verify(plan, p => Vol.Ext[p], _ => false, p => p == _s));
    }

    [Fact]
    public void RevertLeavesASparseFileAloneThatThePlanHasNoBaselineFor()
    {
        SetExtents(_s, [new(0, 30000, 256)]);
        PlanFile plan = PlanFile.Make([Run((_s, 0))], Root, 2 << 20, Cb, p => Files[p], preserveBytes: 64 << 20).Resolve(20000);
        Apply(plan, Exec()); // 속성을 모르는 훅: 일반 파일처럼 옮겨 저널을 남김
        int forward = Vol.Moves.Count;
        Assert.True(forward > 0);

        RevertReport rep = Reverter.Revert(JournalPath, Vol, [Root], SparseAwareHooks(_s), new ApplyOptions(Execute: true));

        Assert.Equal(forward, Vol.Moves.Count);
        Assert.NotEmpty(rep.Skipped);
        Assert.All(rep.Skipped, s => Assert.Equal("sparse", s.Reason));
    }

    [Fact]
    public void SkippedNonSparseFilesStillTakePartInTheZoneCheck()
    {
        PlanFile plan = Make().Resolve(7000); // 존이 F2 의 현재 자리 (LCN 7000~) 와 겹침
        ApplyHooks hooks = SparseAwareHooks("none", p => p == F2 ? "encrypted" : null);

        ApplyReport? rep = null;
        Exception? thrown = Record.Exception(() => rep = Apply(plan, Exec(), hooks));

        Assert.Null(thrown); // 기준 커밋과 같이 건너뛴 파일의 자리도 계획 자신의 파일로 봄: zone_not_free 로 거절하지 않음
        Assert.Contains(new KeyValuePair<string, string>(F2, "encrypted"), rep!.SkippedFiles);
    }

    [Fact]
    public void ASparseFileWhoseTaskWouldBeOffTheBoundaryIsLeftOutAndTheOtherFilesStillMove()
    {
        SetExtents(_s, [new(0, 50000, 20), new(20, 51000, 12)]); // 물리 조각 경계가 16클러스터 경계 밖
        // 존이 50000 부터라 첫 조각은 제자리: 남은 조각 (VCN 20, 길이 12) 만 작업이 되어 경계에 맞지 않음
        PlanFile plan = PlanFile.Make([Run((_s, 0), (_s, 1), (F1, 0))], Root, 2 << 20, Cb, p => Files[p], zoneStartLcn: 50000, preserveBytes: 64 << 20, sparseFn: p => p == _s);

        Assert.NotEmpty(plan.Tasks);
        Assert.All(plan.Tasks, t => Assert.Equal(F1, t.Path));
        Assert.Empty(plan.SparseAllocations);
        Assert.Equal(1L, plan.Stats["skipped_files"]);
        Assert.Equal(1L, plan.Stats["unsupported_sparse_files"]); // 서비스가 이 값을 로그로 남겨 실제 게임에서 얼마나 생기는지 봄
        Assert.Null(Record.Exception(() => PlanGuards.CheckPlan(plan, [Root], Letter, Hooks())));
    }

    // ---- 잔여 계획

    private PlanFile Residual(PlanFile candidate, params PlanTask[] previous) =>
        ResidualPlanner.Build(candidate, previous, p => Vol.Ext[p], n => Vol.Bitmap().FindFreeRun(n, true, 0, PlanGuards.ZoneAlign));

    private PlanFile Candidate(string path, params long[] blocks) =>
        PlanFile.Make([Run(blocks.Select(b => (path, b)).ToArray())], Root, 2 << 20, Cb, p => Vol.Ext[p], preserveBytes: 64 << 20, sparseFn: p => p == path);

    [Fact]
    public void ResidualPlanMakesOnlyAllocatedUnitTasks()
    {
        PlanFile plan = Residual(Candidate(_s, 0, 4, 6, 10));

        Assert.Equal([(0L, 16L), (96L, 16L)], plan.Tasks.Select(t => (t.Vcn, t.Clusters)));
        Assert.Equal(OriginalAllocation, plan.SparseAllocations[_s]);
        Assert.Equal(32L, plan.Zone.Clusters);
        Assert.Equal(1L, plan.Stats["residual"]);
    }

    [Fact]
    public void ResidualPlanKeepsAUnitThatIsAlreadyInPlaceFixed()
    {
        PlanFile candidate = Candidate(_s, 0, 6);
        Vol.Move(_s, 0, 20000, 16); // 첫 단위는 이미 이전 목표에 놓임

        PlanFile plan = Residual(candidate, new PlanTask(_s, 0, 16, 20000));

        Assert.Contains(new PlanTask(_s, 0, 16, 20000), plan.Tasks);
        Assert.Equal(16L, plan.Stats["fixed_clusters"]);
        Assert.Equal(16L, plan.Zone.Clusters); // 옮길 것은 두 번째 단위뿐
        Assert.Equal(96L, Assert.Single(plan.Tasks, t => t.Vcn >= 96).Vcn);
    }

    [Fact]
    public void ResidualPlanDoesNotMergeTheShortLastPieceWithTheUnitBeforeIt()
    {
        string t = Path.Combine(Root, "t.bin");
        File.WriteAllBytes(t, RandomNumberGenerator.GetBytes(100 * (int)Cb + 1234));
        SetExtents(t, [new(0, 50000, 64), new(64, 60000, 48)]);

        PlanFile plan = Residual(Candidate(t, 5, 6));

        Assert.Equal([(80L, 16L), (96L, 5L)], plan.Tasks.Select(x => (x.Vcn, x.Clusters)));
    }

    [Fact]
    public void ResidualPlanHoldsAFileThatChangesInTheMiddleWithoutAnyTaskForItsEarlierPieces()
    {
        PlanFile candidate = Candidate(_s, 0, 6);
        candidate = candidate with { Order = [new PlanRange(_s, 0, 16), new PlanRange(_s, 64, 16)] }; // 두 번째 범위는 지금 구멍

        PlanFile plan = Residual(candidate);

        Assert.Empty(plan.Tasks);
        Assert.Contains(_s, ((JObject)plan.Stats[ResidualPlanner.HeldKey]!).Select(kv => kv.Key));
    }

    [Fact]
    public void ResidualReplanAfterAPartialApplyMovesOnlyTheRemainingUnit()
    {
        PlanFile first = MakeSparse(0, 6);
        Assert.Equal("write_cap", Apply(first, Exec(writeCap: 20 * Cb)).Stopped); // 한 단위만 옮김
        string planPath = Path.Combine(Root, "g.plan.json");
        first.Save(planPath);
        Vol.Moves.Clear();

        (PlanFile plan, _) = ResidualReplan.Run(Candidate(_s, 0, 6), planPath, JournalPath, Vol, null, Hooks(), () => new DateTime(2026, 11, 5, 3, 0, 0, DateTimeKind.Utc), null, []);

        Assert.Equal(16L, plan.Stats["fixed_clusters"]);
        Assert.Equal(OriginalAllocation, plan.SparseAllocations[_s]);
        ApplyReport rep = Apply(plan, Exec());
        Assert.Empty(rep.Failed);
        Assert.Equal(16 * Cb, rep.MovedBytes);
        Assert.All(Vol.Moves, m => Assert.True(m.Vcn % 16 == 0 && m.N % 16 == 0));
        Assert.Equal(OriginalAllocation, Allocation(Vol, _s));
    }

    /// 첫 이동 호출만 성공하고 옮기지 않는 볼륨 (나머지는 그대로 전달).
    private sealed class FirstMoveSilentVolume(FakeVolume inner) : IVolume
    {
        public int Calls { get; private set; }

        public string Letter => inner.Letter;

        public int ClusterBytes => inner.ClusterBytes;

        public long TotalClusters => inner.TotalClusters;

        public long FreeClusters => inner.FreeClusters;

        public (long Start, long End) MftZone => inner.MftZone;

        public VolumeBitmap Bitmap() => inner.Bitmap();

        public void Move(string path, long vcn, long lcn, uint clusters)
        {
            if (Calls++ > 0)
            {
                inner.Move(path, vcn, lcn, clusters);
            }
        }

        public void Dispose()
        {
        }
    }

    /// 호출은 성공하지만 아무것도 옮기지 않는 볼륨.
    private sealed class SilentVolume(FakeVolume inner) : IVolume
    {
        public string Letter => inner.Letter;

        public int ClusterBytes => inner.ClusterBytes;

        public long TotalClusters => inner.TotalClusters;

        public long FreeClusters => inner.FreeClusters;

        public (long Start, long End) MftZone => inner.MftZone;

        public VolumeBitmap Bitmap() => inner.Bitmap();

        public void Move(string path, long vcn, long lcn, uint clusters)
        {
        }

        public void Dispose()
        {
        }
    }
}

/// 재배치 경로에서만 스파스 속성 단독 파일을 허용하는 판정.
public sealed class SparseGuardTests
{
    private const uint Sparse = 0x200;

    [Fact]
    public void SparseAttributeAloneIsAllowedOnlyOnTheRelocationPath()
    {
        Assert.Null(FileGuards.RelocationSkipReason("x", _ => Sparse | 0x20));
        Assert.False(FileGuards.RelocationUnmovable("x", _ => Sparse));
        Assert.Equal("sparse", FileGuards.SkipReason("x", _ => Sparse | 0x20)); // 다른 호출부의 동작은 그대로
        Assert.True(FileGuards.Unmovable("x", _ => Sparse));
    }

    [Theory]
    [InlineData(0x800u)] // 압축
    [InlineData(0x4000u)] // 암호화
    [InlineData(0x400u)] // 재해석 지점
    [InlineData(0x1000u)] // 오프라인
    [InlineData(0x10u)] // 디렉터리
    public void SparseTogetherWithAnotherExcludedAttributeIsStillExcluded(uint other)
    {
        Assert.NotNull(FileGuards.RelocationSkipReason("x", _ => Sparse | other));
        Assert.Equal(FileGuards.SkipReason("x", _ => Sparse | other), FileGuards.RelocationSkipReason("x", _ => Sparse | other));
        Assert.True(FileGuards.RelocationUnmovable("x", _ => Sparse | other));
    }

    [Fact]
    public void ProductionApplyHooksOfTheCliPassTheSparseAttributeHook()
    {
        Assert.NotNull(AsterCache.Cli.Commands.VolumeCommands.Hooks().IsSparse); // 재배치용 SkipReason 을 쓰는 호출부는 속성 훅도 함께 넘겨야 함
    }

    [Fact]
    public void OtherFilesAreJudgedTheSameWayOnBothPaths()
    {
        foreach (uint a in new uint[] { 0x20, 0x800, 0x4000, 0x400, 0x1000, 0x10 })
        {
            Assert.Equal(FileGuards.SkipReason("x", _ => a), FileGuards.RelocationSkipReason("x", _ => a));
        }

        Assert.StartsWith("attrs_unreadable", FileGuards.RelocationSkipReason("x", _ => throw new IOException("not ready")), StringComparison.Ordinal);
        Assert.False(FileGuards.RelocationUnmovable("x", _ => throw new IOException("not ready")));
    }
}
