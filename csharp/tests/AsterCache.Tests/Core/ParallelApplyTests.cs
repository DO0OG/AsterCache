using AsterCache.Core.Apply;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Trace;
using Xunit;

namespace AsterCache.Tests.Core;

/// Parallel > 1: 서로 다른 파일의 작업을 동시에 옮기되, 결과 배치·같은 파일 작업 순서·저널 재개와 되돌리기·쓰기 상한·연속 busy 규칙은 순차와 같아야 합니다.
public sealed class ParallelApplyTests : ApplyTestBase
{
    private const int ExtraFiles = 4;

    private readonly List<string> _paths;

    public ParallelApplyTests()
    {
        // 파일 6 개 (기본 2 + 추가 4): 서로 다른 파일을 동시에 옮길 여지를 만듦. 추가 파일은 겹치지 않는 LCN 에 흩어 놓음.
        _paths = [F1, F2];
        for (int k = 0; k < ExtraFiles; k++)
        {
            string p = Path.Combine(Root, $"x{k}.bin");
            File.WriteAllBytes(p, System.Security.Cryptography.RandomNumberGenerator.GetBytes(1 << 20));
            long baseLcn = 30000 + (k * 1000);
            List<Extent> ext = [.. Enumerable.Range(0, 32).Select(i => new Extent(i * 8, baseLcn + (i * 20), 8))]; // 8 클러스터씩 32 조각으로 흩어 작업 수를 늘림
            Files[p] = ext;
            Vol.Ext[p] = [.. ext];
            _paths.Add(p);
        }

        Sessions = [Enumerable.Range(0, 24).SelectMany(b => _paths.Select(f => new TraceEvent(f, b * 16384L, 16384, 8.0))).ToList()];
    }

    private static ApplyOptions Exec(int parallel, long writeCap = 2L << 30) => new(Execute: true, ChunkBytes: 4 * Cb, WriteCap: writeCap, Parallel: parallel);

    private PlanFile Plan() => PlanFile.Make(Sessions, Root, 8 << 20, Cb, p => Files[p], preserveBytes: 64 << 20).Resolve(60000);

    private FakeVolume Fresh() => new(Files.ToDictionary(kv => kv.Key, kv => kv.Value.ToList(), StringComparer.Ordinal), Letter);

    // 이동 한 건이 속한 계획 작업의 인덱스
    private static int TaskOf(PlanFile p, (string Path, long Vcn, long Lcn, long N) m) =>
        p.Tasks.ToList().FindIndex(t => t.Path == m.Path && m.Vcn >= t.Vcn && m.Vcn < t.Vcn + t.Clusters);

    [Fact]
    public void ParallelFourGivesTheSameLayoutAsSequential()
    {
        PlanFile p = Plan();
        FakeVolume seq = Fresh();
        ApplyReport r1 = Applier.Apply(p, seq, [Root], Path.Combine(Root, "seq.jsonl"), new ApplyHooks { Extents = x => seq.Ext[x], Digest = Hooks().Digest, SkipReason = _ => null, Sleep = Hooks().Sleep }, Exec(1));

        ApplyReport r4 = Apply(p, Exec(4));

        Assert.Empty(r1.Failed);
        Assert.Empty(r4.Failed);
        Assert.Null(r4.Stopped);
        Assert.Equal(r1.MovedBytes, r4.MovedBytes);
        Assert.True(SameExtents(seq.Ext, Vol.Ext));
        Assert.Equal(seq.Moves.Order().ToList(), Vol.Moves.Order().ToList());
        Assert.True(Verify(p).Contiguous);
        Assert.Empty(r4.ShaMismatch ?? []);
        Assert.Equal(r4.TasksTodo, r4.Timings.Tasks);
        Assert.True(r4.Timings.MoveWallS <= r4.Timings.MoveS + 1e-6);
    }

    [Fact]
    public void ParallelOneMovesInPlanOrder()
    {
        PlanFile p = Plan();

        Apply(p, Exec(1));

        int[] order = Vol.Moves.Select(m => TaskOf(p, m)).ToArray();
        Assert.Equal(order.Order().ToArray(), order);
        Assert.True(order.Distinct().Count() > 6);
    }

    [Fact]
    public void ParallelRunsDifferentFilesAtTheSameTimeButNeverTwoMovesOfOneFile()
    {
        PlanFile p = Plan();
        int inFlight = 0;
        int peak = 0;
        bool overlapped = false;
        var perFile = new Dictionary<string, int>(StringComparer.Ordinal);
        Vol.BeforeMove = path =>
        {
            int now = Interlocked.Increment(ref inFlight);
            lock (perFile)
            {
                perFile[path] = perFile.GetValueOrDefault(path) + 1;
                overlapped |= perFile[path] > 1;
                peak = Math.Max(peak, now);
            }

            Thread.Sleep(15);
            lock (perFile)
            {
                perFile[path]--;
            }

            _ = Interlocked.Decrement(ref inFlight);
        };

        ApplyReport rep = Apply(p, Exec(4));

        Assert.Empty(rep.Failed);
        Assert.False(overlapped);
        Assert.InRange(peak, 2, 4);
        Assert.True(rep.Timings.MoveWallS < rep.Timings.MoveS); // 겹쳤으니 벽시계가 합계보다 짧음
    }

    [Fact]
    public void TasksOfOneFileKeepPlanOrder()
    {
        PlanFile p = Plan();
        Vol.BeforeMove = _ => Thread.Sleep(2);

        Apply(p, Exec(4));

        foreach (IGrouping<string, (string Path, long Vcn, long Lcn, long N)> g in Vol.Moves.GroupBy(m => m.Path))
        {
            int[] order = g.Select(m => TaskOf(p, m)).ToArray();
            Assert.Equal(order.Order().ToArray(), order);
        }
    }

    [Fact]
    public void InterleavedJournalResumesAndRevertsAfterAStopWithANonPrefixDoneSet()
    {
        PlanFile p = Plan();
        using var cts = new CancellationTokenSource();
        Vol.BeforeMove = path => Thread.Sleep(path == F1 ? 40 : 1); // F1 이 느려서 앞 인덱스가 늦게 끝남 → 완료 집합이 접두가 아님
        Vol.AfterMove = n =>
        {
            if (n == 30)
            {
                cts.Cancel();
            }
        };

        ApplyReport r1 = Applier.Apply(p, Vol, [Root], JournalPath, Hooks(), Exec(4), cts.Token);
        Vol.AfterMove = null;
        Vol.BeforeMove = null;

        Assert.Equal("cancelled", r1.Stopped);
        HashSet<long> done = new Journal(JournalPath).Rows.Where(r => Equals(r["type"], "step") && r.TryGetValue("state", out object? st) && Equals(st, "done")).Select(r => (long)r["idx"]!).ToHashSet();
        Assert.NotEmpty(done);
        Assert.Contains(Enumerable.Range(0, (int)done.Max()).Select(i => (long)i), i => !done.Contains(i)); // 접두가 아님: 더 큰 인덱스가 끝났는데 작은 인덱스가 남음

        int movesBefore = Vol.Moves.Count;
        ApplyReport r2 = Apply(p, Exec(4));

        Assert.Null(r2.Stopped);
        Assert.Empty(r2.Failed);
        Assert.Empty(r2.ShaMismatch ?? []);
        Assert.Equal(Vol.Moves.Count, Vol.Moves.Select(m => (m.Path, m.Vcn, m.Lcn)).Distinct().Count()); // 이미 옮긴 조각은 다시 옮기지 않음
        Assert.True(Vol.Moves.Count > movesBefore);
        Assert.True(Verify(p).Contiguous);

        RevertReport rv = Reverter.Revert(JournalPath, Vol, [Root], Hooks(), new ApplyOptions(Execute: true));
        Assert.Empty(rv.Skipped);
        Assert.True(SameExtents(Vol.Ext, Files));
    }

    [Fact]
    public void WriteCapHoldsAcrossWorkers()
    {
        PlanFile p = Plan();
        long cap = 20 * 4 * Cb;
        Vol.BeforeMove = _ => Thread.Sleep(1);

        ApplyReport rep = Apply(p, Exec(4, cap));

        Assert.Equal("write_cap", rep.Stopped);
        Assert.True(rep.MovedBytes <= cap);
        Assert.Equal(Vol.Moves.Sum(m => m.N) * Cb, rep.MovedBytes);
    }

    [Fact]
    public void ConcurrentBusyFailuresAreSummedAndStopTheRun()
    {
        PlanFile p = Plan();
        Vol.FailNext = int.MaxValue; // 모든 이동이 액세스 거부 (대상 점유)

        ApplyReport rep = Apply(p, Exec(4));

        Assert.Equal(Applier.ZoneOccupied, rep.Stopped);
        Assert.InRange(rep.Failed.Count, Applier.MaxBusyInARow + 1, Applier.MaxBusyInARow + 4); // 21 번째에서 멈추고 진행 중이던 작업만 더 끝남
        Assert.All(rep.Failed, f => Assert.Equal(Applier.TargetBusy, f.Reason));
        Assert.Equal(0, rep.MovedBytes);
        Assert.Empty(Vol.Moves);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void AllBusyWithManyTasksAndEightWorkersIsRaceFree(int round)
    {
        Sessions = [Enumerable.Range(0, 64).SelectMany(b => _paths.Select(f => new TraceEvent(f, b * 16384L, 16384, 8.0))).ToList()];
        PlanFile p = Plan();
        Assert.True(p.Tasks.Count >= 64, $"round {round} tasks {p.Tasks.Count}");
        Vol.FailNext = int.MaxValue;

        ApplyReport rep = Apply(p, Exec(8));

        Assert.Equal(Applier.ZoneOccupied, rep.Stopped);
        Assert.InRange(rep.Failed.Count, Applier.MaxBusyInARow + 1, Applier.MaxBusyInARow + 8);
        Assert.Equal(rep.Failed.Select(f => f.Idx).Order().ToArray(), rep.Failed.Select(f => f.Idx).ToArray()); // 작업 인덱스 순
        Assert.Equal(rep.Failed.Count, rep.Failed.Select(f => f.Idx).Distinct().Count());
        Assert.Equal(rep.Failed.Count * 2, Sleeps.Count); // 실패한 작업마다 재시도 사이 대기 2번
        Assert.Equal(rep.Failed.Count, rep.Timings.Tasks);
        Assert.Equal(0, rep.MovedBytes);
    }

    [Fact]
    public void AWorkerExceptionStopsTheOthersAndSurfaces()
    {
        PlanFile p = Plan();
        Vol.CrashAt = 5;

        _ = Assert.Throws<InvalidOperationException>(() => Apply(p, Exec(4)));

        Assert.True(Vol.Moves.Count < 30);
        Assert.Contains(new Journal(JournalPath).Rows, r => Equals(r["type"], "end")); // 예외에도 end 행은 남음
    }
}
