using System.Security.Cryptography;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

/// 스파스 파일의 할당 기준값이 없는 이전 형식 계획: 기준값을 더한 후보 (SparseAllocation.WithMissingBaselines) 와 그 후보로 한 잔여 재계획 (ResidualReplan).
/// 시험 파일 s.bin 은 1 MiB (256 클러스터), 할당 구간은 VCN [0, 64) 와 [96, 160), 나머지는 구멍입니다.
public sealed class LegacySparseReplanTests : ApplyTestBase
{
    private static readonly AllocatedRange[] OriginalAllocation = [new(0, 64), new(96, 64)];
    private static readonly DateTime Now = new(2026, 11, 18, 3, 0, 0, DateTimeKind.Utc);
    private readonly string _s;

    public LegacySparseReplanTests()
    {
        _s = Path.Combine(Root, "s.bin");
        File.WriteAllBytes(_s, RandomNumberGenerator.GetBytes(1 << 20));
        Files[_s] = [new(0, 30000, 64), new(96, 40000, 64)];
        Vol.Ext[_s] = [.. Files[_s]];
    }

    private string PlanPath => Path.Combine(Root, "g.plan.json");

    private static ApplyOptions Exec(long writeCap = 2L << 30) => new(Execute: true, ChunkBytes: 4L << 20, WriteCap: writeCap, Verify: VerifyMode.All);

    // 서비스가 쓰는 규칙과 같음: s.bin 은 스파스 속성이라 기준값이 없는 계획에서는 건너뜀.
    private ApplyHooks LegacyHooks() => new()
    {
        Extents = p => Vol.Ext[p],
        Digest = (p, size, extra) => SampleHasher.Compute(p, size, extra, null),
        EdgeDigest = (p, size, extra) => SampleHasher.Compute(p, size, extra, null, null, 0),
        SkipReason = _ => null,
        IsSparse = p => p == _s,
        Sleep = _ => { },
    };

    // 기준값을 모르는 이전 형식 계획 (존 20000): 스파스 파일도 일반 파일처럼 Order·작업에 듦.
    private PlanFile LegacyPlan() =>
        PlanFile.Make([Run((F1, 0), (F1, 4), (_s, 0), (_s, 6))], Root, 2 << 20, Cb, p => Files[p], preserveBytes: 64 << 20).Resolve(20000);

    private PlanFile? Candidate(PlanFile plan) => SparseAllocation.WithMissingBaselines(plan, p => p == _s, p => Vol.Ext[p]);

    // 이전 형식 계획을 적용한 상태: 일반 파일은 존으로 옮겨지고 스파스 파일은 건너뜀.
    private PlanFile AppliedLegacy()
    {
        PlanFile legacy = LegacyPlan();
        legacy.Save(PlanPath);
        ApplyReport rep = Applier.Apply(legacy, Vol, [Root], JournalPath, LegacyHooks(), Exec());
        Assert.Contains(rep.SkippedFiles, s => s.Key == _s);
        return legacy;
    }

    // ---- 1: 후보 함수

    [Fact]
    public void OnlySparseFilesWithoutABaselineGainOneAndEverythingElseStaysTheSame()
    {
        PlanFile legacy = LegacyPlan();

        PlanFile candidate = Candidate(legacy)!;

        Assert.Equal(OriginalAllocation, Assert.Single(candidate.SparseAllocations).Value);
        Assert.Equal(_s, Assert.Single(candidate.SparseAllocations).Key);
        Assert.Equal(SparseAllocation.RuleVersion, candidate.Stats[SparseAllocation.RuleKey]);
        Assert.Equal(legacy.Tasks, candidate.Tasks);
        Assert.Equal(legacy.Order, candidate.Order);
        Assert.Equal(legacy.Fingerprints, candidate.Fingerprints);
        Assert.Equal(legacy.Zone, candidate.Zone);
        Assert.False(legacy.Stats.ContainsKey(SparseAllocation.RuleKey)); // 활성 계획 사본은 바뀌지 않음
        Assert.Empty(legacy.SparseAllocations);
    }

    [Fact]
    public void AFileWhoseAllocationCannotBeReadOrHasNoDiskRangesIsNotGivenABaseline()
    {
        PlanFile legacy = LegacyPlan();

        Assert.Null(SparseAllocation.WithMissingBaselines(legacy, p => p == _s, _ => null));
        Assert.Null(SparseAllocation.WithMissingBaselines(legacy, p => p == _s, _ => []));
        Assert.Null(SparseAllocation.WithMissingBaselines(legacy, p => p == _s, p => p == _s ? null : Vol.Ext[p]));
    }

    [Fact]
    public void ThereIsNothingToAddWhenNoFileIsSparseOrEveryOneAlreadyHasABaseline()
    {
        PlanFile legacy = LegacyPlan();
        PlanFile candidate = Candidate(legacy)!;

        Assert.Null(SparseAllocation.WithMissingBaselines(legacy, _ => false, p => Vol.Ext[p]));
        Assert.Null(SparseAllocation.WithMissingBaselines(candidate, p => p == _s, p => Vol.Ext[p]));
    }

    [Fact]
    public void AnExistingBaselineIsLeftAloneEvenWhenTheAllocationLooksDifferentNow()
    {
        PlanFile candidate = Candidate(LegacyPlan())!;
        Vol.Ext[_s] = [new(0, 30000, 32)];

        Assert.Null(SparseAllocation.WithMissingBaselines(candidate, p => p == _s, p => Vol.Ext[p]));
        Assert.Equal(OriginalAllocation, candidate.SparseAllocations[_s]);
    }

    // ---- 2·3: 교체 결과와 이어지는 적용

    [Fact]
    public void ReplacementKeepsPlacedPiecesFixedAndPutsTheUnplacedSparsePiecesInOneNewRunThatAvoidsOccupiedAndReservedSpace()
    {
        PlanFile legacy = AppliedLegacy();
        List<PlanTask> oldSparse = [.. legacy.Tasks.Where(t => t.Path == _s)];
        Vol.Ext[Path.Combine(Root, "other.bin")] = [.. oldSparse.Select(t => new Extent(t.Vcn, t.TargetLcn, t.Clusters))]; // 옛 스파스 목표 자리는 이미 다른 데이터가 차지함
        int movesBefore = Vol.Moves.Count;

        PlanFile plan = ResidualReplan.Run(Candidate(PlanFile.Load(PlanPath))!, PlanPath, JournalPath, Vol, [(0, 5000)], LegacyHooks(), () => Now, null, []).Plan;

        Assert.Equal(movesBefore, Vol.Moves.Count); // 교체는 파일을 옮기지 않음
        Assert.Equal(legacy.Tasks.Where(t => t.Path == F1).Sum(t => t.Clusters), (long)plan.Stats["fixed_clusters"]!); // 이미 놓인 일반 조각은 모두 고정
        Assert.Equal(1L, plan.Stats["residual"]);
        List<PlanTask> sparse = [.. plan.Tasks.Where(t => t.Path == _s).OrderBy(t => t.Vcn)];
        Assert.Equal([(0L, 16L), (96L, 16L)], sparse.Select(t => (t.Vcn, t.Clusters)));
        Assert.Equal(32L, plan.Zone.Clusters);
        Assert.Equal(plan.Zone.StartLcn, sparse[0].TargetLcn);
        Assert.Equal(sparse[0].TargetLcn + 16, sparse[1].TargetLcn); // 접근 순서대로 한 연속 구간
        long start = plan.Zone.StartLcn!.Value;
        Assert.True(start >= 5000); // 다른 게임 계획의 예약을 피함
        Assert.True(Vol.Bitmap().RangeFree(start, 32)); // 옛 목표처럼 점유된 자리가 아님
        Assert.Equal(OriginalAllocation, plan.SparseAllocations[_s]);
        Assert.Equal(1L, plan.Stats[SparseAllocation.RuleKey]);
    }

    [Fact]
    public void AfterTheReplacementTheLayoutNeedsAReapplyWhichMovesOnlyTheSparsePiecesAndCompletesIt()
    {
        _ = AppliedLegacy();
        PlanFile plan = ResidualReplan.Run(Candidate(PlanFile.Load(PlanPath))!, PlanPath, JournalPath, Vol, null, LegacyHooks(), () => Now, null, []).Plan;
        plan.Save(PlanPath);
        // 교체 뒤에는 스파스 조각이 목표에 없어 배치 판정이 연속이 아니고 (정기 재적용이 Reapply 로 판정), 적용이 그 조각만 옮김.
        Assert.False(LayoutVerifier.Verify(plan, p => Vol.Ext[p], null, p => p == _s, holdChangedSparse: true).Contiguous);
        Vol.Moves.Clear();

        ApplyReport rep = Applier.Apply(PlanFile.Load(PlanPath), Vol, [Root], JournalPath, LegacyHooks(), Exec());

        Assert.Empty(rep.Failed);
        Assert.Equal(32 * Cb, rep.MovedBytes);
        Assert.All(Vol.Moves, m => Assert.Equal(_s, m.Path));
        Assert.All(Vol.Moves, m => Assert.True(m.Vcn % 16 == 0 && m.N % 16 == 0));
        Assert.Equal(OriginalAllocation, SparseAllocation.Normalize(Vol.Ext[_s])); // 할당 구간(구멍)은 그대로
        Assert.True(LayoutVerifier.Verify(plan, p => Vol.Ext[p], null, p => p == _s, holdChangedSparse: true).Contiguous);
        Vol.Moves.Clear();
        Assert.Equal(0, Applier.Apply(PlanFile.Load(PlanPath), Vol, [Root], JournalPath, LegacyHooks(), Exec()).MovedBytes); // 다시 돌려도 더 옮길 것이 없음
    }

    // ---- 8: 미검증 표본이 남은 저널

    [Fact]
    public void UnverifiedSamplesAreCheckedBeforeTheReplacementAndASourceChangedFileIsHeldNotRejected()
    {
        PlanFile legacy = LegacyPlan();
        legacy.Save(PlanPath);
        using var cts = new CancellationTokenSource();
        Vol.AfterMove = n =>
        {
            if (n == 1)
            {
                cts.Cancel();
            }
        };
        Assert.Equal("cancelled", Applier.Apply(legacy, Vol, [Root], JournalPath, LegacyHooks(), Exec(), cts.Token).Stopped);
        Vol.AfterMove = null;
        string moved = Vol.Moves.Select(m => m.Path).First();
        Assert.Equal(F1, moved);
        Assert.NotEmpty(Applier.PendingBefore(new Journal(JournalPath).Rows));
        File.WriteAllBytes(F1, RandomNumberGenerator.GetBytes(1 << 20));
        File.SetLastWriteTimeUtc(F1, DateTime.UtcNow.AddDays(1)); // 계획 뒤 원본이 바뀜

        PlanFile plan = ResidualReplan.Run(Candidate(PlanFile.Load(PlanPath))!, PlanPath, JournalPath, Vol, null, LegacyHooks(), () => Now, null, []).Plan;

        Assert.Equal(Applier.SourceChangedReason, ((JObject)plan.Stats[ResidualPlanner.HeldKey]!)[F1]);
        Assert.DoesNotContain(plan.Order, o => o.Path == F1);
        Assert.Equal(2, plan.Tasks.Count(t => t.Path == _s)); // 스파스 조각은 그대로 계획됨
        Assert.Empty(Applier.PendingBefore(new Journal(Directory.GetFiles(Root, "j.jsonl.*").Single()).Rows)); // 보관된 저널의 검증 표본은 교체 전에 처리됨
        Assert.False(File.Exists(JournalPath));
    }
}
