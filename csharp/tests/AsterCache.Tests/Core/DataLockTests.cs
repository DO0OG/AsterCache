using AsterCache.Core.Apply;
using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

/// 프로세스 간 데이터 잠금 (DataLock): 보유 중 다른 쪽은 busy, 닫으면 풀림, 잠금 안의 계획 전환은 직렬화.
public sealed class DataLockTests : ApplyTestBase
{
    private static readonly DateTime Now = new(2026, 11, 5, 3, 0, 0, DateTimeKind.Utc);

    private string PlanPath => Path.Combine(Root, "g.plan.json");

    [Fact]
    public void WhileHeldAnotherAcquireIsBusyAndAfterDisposeItSucceeds()
    {
        DataLock? first = DataLock.TryAcquire(PlanPath);
        Assert.NotNull(first);

        Assert.Null(DataLock.TryAcquire(PlanPath, TimeSpan.Zero)); // 예외가 아니라 null (busy)
        Assert.Null(DataLock.TryAcquire(Path.Combine(Root, "other.plan.json"), TimeSpan.Zero)); // 같은 폴더의 다른 게임도 같은 잠금

        first!.Dispose();
        using DataLock? second = DataLock.TryAcquire(PlanPath, TimeSpan.Zero);
        Assert.NotNull(second);
    }

    [Fact]
    public void CallsInDifferentPlanFoldersThatShareAJournalFolderAreSerialized()
    {
        string journal = Path.Combine(Root, "journals", "g.jsonl");
        string planA = Path.Combine(Root, "a", "g.plan.json");
        string planB = Path.Combine(Root, "b", "g.plan.json");

        DataLock? first = DataLock.TryAcquire(planA, TimeSpan.Zero, journalPath: journal);
        Assert.NotNull(first);
        Assert.Null(DataLock.TryAcquire(planB, TimeSpan.Zero, journalPath: journal)); // 저널 폴더 잠금이 겹침
        Assert.Null(DataLock.TryAcquire(Path.Combine(Root, "journals", "x.plan.json"), TimeSpan.Zero)); // 저널 폴더에서 계획만 잡아도 같은 파일
        using DataLock? unrelated = DataLock.TryAcquire(Path.Combine(Root, "c", "g.plan.json"), TimeSpan.Zero, journalPath: Path.Combine(Root, "c", "g.jsonl"));
        Assert.NotNull(unrelated); // 겹치지 않는 호출은 막지 않음

        first!.Dispose();
        using DataLock? second = DataLock.TryAcquire(planB, TimeSpan.Zero, journalPath: journal);
        Assert.NotNull(second);
        Assert.Null(DataLock.TryAcquire(Path.Combine(Root, "b", "y.plan.json"), TimeSpan.Zero)); // 둘째가 계획 폴더 쪽도 잡고 있음
    }

    [Fact]
    public void AWaitingAcquireGetsTheLockOnceTheHolderReleases()
    {
        DataLock holder = DataLock.TryAcquire(PlanPath)!;
        int sleeps = 0;

        using DataLock? got = DataLock.TryAcquire(PlanPath, TimeSpan.FromSeconds(5), sleep: _ =>
        {
            if (++sleeps == 2)
            {
                holder.Dispose();
            }
        });

        Assert.NotNull(got);
        Assert.Equal(2, sleeps);
    }

    [Fact]
    public void AnAcquireThatCannotGetTheLockInTimeGivesUpWithoutThrowing()
    {
        using DataLock holder = DataLock.TryAcquire(PlanPath)!;
        DateTime clock = DateTime.UtcNow;

        DataLock? got = DataLock.TryAcquire(PlanPath, TimeSpan.FromSeconds(1), sleep: t => clock += t + t, now: () => clock);

        Assert.Null(got);
    }

    [Fact]
    public async Task TwoPlanSwitchesInsideTheLockNeverOverlapAndBothFinish()
    {
        PlanFile a = Make().Resolve(20000);
        Assert.Equal("write_cap", Apply(a, new ApplyOptions(Execute: true, ChunkBytes: 8 * Cb, WriteCap: 64 * Cb)).Stopped);
        a.Save(PlanPath);
        int inside = 0;
        int overlap = 0;
        void Step(string point)
        {
            if (point == "verified")
            {
                if (Interlocked.Increment(ref inside) > 1)
                {
                    _ = Interlocked.Increment(ref overlap);
                }
            }
            else if (point == "prepared")
            {
                Thread.Sleep(150); // 잠금이 없으면 이 사이에 다른 호출이 같은 .next 와 기록을 덮음
            }
            else if (point == "next_removed")
            {
                _ = Interlocked.Decrement(ref inside);
            }
        }

        PlanFile Switch(long zone)
        {
            using DataLock gate = DataLock.TryAcquire(PlanPath, TimeSpan.FromSeconds(30)) ?? throw new TimeoutException("lock");
            PlanFile candidate = Make().Resolve(zone);
            _ = PlanSwitch.Commit(candidate, PlanPath, JournalPath, () => Now, hooks: Hooks(), checkpoint: Step);
            return candidate;
        }

        Task<PlanFile> x = Task.Run(() => Switch(30000));
        Task<PlanFile> y = Task.Run(() => Switch(40000));
        PlanFile[] done = await Task.WhenAll(x, y);

        Assert.Equal(0, overlap);
        string active = PlanFile.Load(PlanPath).Sha();
        Assert.Contains(active, done.Select(p => p.Sha()));
        Assert.False(File.Exists(PlanPath + PlanSwitch.RecordSuffix));
        Assert.False(File.Exists(PlanPath + PlanSwitch.NextSuffix));
        _ = Assert.Single(Directory.GetFiles(Root), f => Path.GetFileName(f).StartsWith("j.jsonl.", StringComparison.Ordinal)); // 첫 전환이 저널을 보관, 둘째는 보관할 저널 없음
    }
}
