using AsterCache.Core.Apply;
using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

/// 프로세스 간 데이터 잠금 (DataLock): 보유 중 다른 쪽은 busy, 닫으면 풀림, 잠금 안의 계획 전환은 직렬화.
public sealed class DataLockTests : ApplyTestBase
{
    private static readonly DateTime Now = new(2026, 11, 5, 3, 0, 0, DateTimeKind.Utc);

    private string PlanPath => Path.Combine(Root, "g.plan.json");

    // 잠금은 스레드에 묶이므로 (재진입 판정) 보유 중에는 await 하지 않고, 다른 스레드는 풀 스레드가 아닌 전용 스레드로 돌립니다 (풀이 이 스레드로 일을 되돌려 재진입으로 보이는 것을 막음).
    private static T OnOtherThread<T>(Func<T> body)
    {
        T result = default!;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception e)
            {
                error = e;
            }
        });
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }

        return result;
    }

    // 같은 스레드의 두 번째 획득은 재진입이라 busy 가 아님: busy 시험은 다른 스레드에서 잡아 봅니다 (잡으면 바로 놓고 잡혔는지만 돌려줌).
    private static bool OtherThreadCanAcquire(string planPath, string? journalPath = null) =>
        OnOtherThread(() =>
        {
            using DataLock? gate = DataLock.TryAcquire(planPath, TimeSpan.Zero, journalPath: journalPath);
            return gate is not null;
        });

    [Fact]
    public void WhileHeldAnotherThreadIsBusyAndAfterDisposeItSucceeds()
    {
        DataLock? first = DataLock.TryAcquire(PlanPath);
        Assert.NotNull(first);

        Assert.False(OtherThreadCanAcquire(PlanPath)); // 예외가 아니라 null (busy)
        Assert.False(OtherThreadCanAcquire(Path.Combine(Root, "other.plan.json"))); // 같은 폴더의 다른 게임도 같은 잠금

        first!.Dispose();
        Assert.True(OtherThreadCanAcquire(PlanPath));
    }

    [Fact]
    public void CallsInDifferentPlanFoldersThatShareAJournalFolderAreSerialized()
    {
        string journal = Path.Combine(Root, "journals", "g.jsonl");
        string planA = Path.Combine(Root, "a", "g.plan.json");
        string planB = Path.Combine(Root, "b", "g.plan.json");

        DataLock? first = DataLock.TryAcquire(planA, TimeSpan.Zero, journalPath: journal);
        Assert.NotNull(first);
        Assert.False(OtherThreadCanAcquire(planB, journal)); // 저널 폴더 잠금이 겹침
        Assert.False(OtherThreadCanAcquire(Path.Combine(Root, "journals", "x.plan.json"))); // 저널 폴더에서 계획만 잡아도 같은 파일
        Assert.True(OtherThreadCanAcquire(Path.Combine(Root, "c", "g.plan.json"), Path.Combine(Root, "c", "g.jsonl"))); // 겹치지 않는 호출은 막지 않음

        first!.Dispose();
        using DataLock? second = DataLock.TryAcquire(planB, TimeSpan.Zero, journalPath: journal);
        Assert.NotNull(second);
        Assert.False(OtherThreadCanAcquire(Path.Combine(Root, "b", "y.plan.json"))); // 둘째가 계획 폴더 쪽도 잡고 있음
    }

    [Fact]
    public void AWaitingAcquireGetsTheLockOnceTheHolderReleases()
    {
        DataLock holder = DataLock.TryAcquire(PlanPath)!;
        using var slept = new SemaphoreSlim(0);
        int sleeps = 0;
        bool got = false;
        var waiter = new Thread(() =>
        {
            using DataLock? gate = DataLock.TryAcquire(PlanPath, TimeSpan.FromSeconds(10), sleep: delay =>
            {
                if (++sleeps == 2)
                {
                    _ = slept.Release();
                }

                Thread.Sleep(50);
            });
            got = gate is not null;
        });
        waiter.Start();
        Assert.True(slept.Wait(TimeSpan.FromSeconds(10)));
        holder.Dispose();
        waiter.Join();

        Assert.True(got);
        Assert.True(sleeps >= 2);
    }

    [Fact]
    public void AnAcquireThatCannotGetTheLockInTimeGivesUpWithoutThrowing()
    {
        using DataLock holder = DataLock.TryAcquire(PlanPath)!;
        DateTime clock = DateTime.UtcNow;

        DataLock? got = OnOtherThread(() => DataLock.TryAcquire(PlanPath, TimeSpan.FromSeconds(1), sleep: t => clock += t + t, now: () => clock));

        Assert.Null(got);
    }

    [Fact]
    public void TheSameThreadReentersWithoutWaitingAndTheFilesStayHeldUntilTheOutermostEnds()
    {
        string journal = Path.Combine(Root, "journals", "g.jsonl");
        DataLock outer = DataLock.TryAcquire(PlanPath, TimeSpan.Zero, journalPath: journal)!;
        Assert.True(DataLock.IsHeld(PlanPath));
        Assert.True(DataLock.IsHeld(journal));

        DataLock? inner = DataLock.TryAcquire(PlanPath, TimeSpan.Zero, journalPath: journal); // 안쪽 구간: 다시 열지 않고 통과
        Assert.NotNull(inner);
        using (DataLock? journalOnly = DataLock.TryAcquire(journal, TimeSpan.Zero)) // 이미 잡은 파일의 부분집합도 통과
        {
            Assert.NotNull(journalOnly);
        }

        inner!.Dispose();
        Assert.True(DataLock.IsHeld(PlanPath)); // 안쪽이 끝나도 바깥 구간은 그대로
        Assert.False(OtherThreadCanAcquire(PlanPath, journal));

        outer.Dispose();
        Assert.False(DataLock.IsHeld(PlanPath));
        Assert.True(OtherThreadCanAcquire(PlanPath, journal));
    }

    [Fact]
    public void ReentryOnlyOpensTheFilesTheThreadDoesNotYetHold()
    {
        string planA = Path.Combine(Root, "a", "g.plan.json");
        string planB = Path.Combine(Root, "b", "g.plan.json");
        using DataLock outer = DataLock.TryAcquire(planA, TimeSpan.Zero)!;

        using (DataLock? wider = DataLock.TryAcquire(planA, TimeSpan.Zero, journalPath: Path.Combine(Root, "b", "g.jsonl")))
        {
            Assert.NotNull(wider);
            Assert.False(OtherThreadCanAcquire(planB)); // 새로 필요한 폴더도 잡힘
        }

        Assert.True(OtherThreadCanAcquire(planB)); // 안쪽이 끝나면 새로 연 파일만 풀림
        Assert.False(OtherThreadCanAcquire(planA));
    }

    [Fact]
    public void AnotherThreadIsStillExcludedWhileThisThreadReenters()
    {
        using DataLock outer = DataLock.TryAcquire(PlanPath)!;
        using DataLock inner = DataLock.TryAcquire(PlanPath, TimeSpan.Zero)!;

        Assert.False(OnOtherThread(() => DataLock.IsHeld(PlanPath))); // 다른 스레드는 잡은 것으로 치지 않음
        Assert.False(OtherThreadCanAcquire(PlanPath));
        _ = Assert.Throws<InvalidOperationException>(() => OnOtherThread(() =>
        {
            DataLock.RequireHeld(PlanPath);
            return true;
        }));
    }

    [Fact]
    public void RequireHeldThrowsOutsideTheLockAndPassesInside()
    {
        _ = Assert.Throws<InvalidOperationException>(() => DataLock.RequireHeld(PlanPath));

        using DataLock gate = DataLock.TryAcquire(PlanPath)!;
        DataLock.RequireHeld(PlanPath);
        DataLock.RequireHeld(Path.Combine(Root, "other.plan.json")); // 같은 폴더
    }

    [Fact]
    public void ANonSharingFailureOnTheSecondFileReleasesTheFirstAndIsRethrown()
    {
        string planA = Path.Combine(Root, "a", "g.plan.json"); // 이름 순으로 a 가 먼저, b 가 둘째
        string journalB = Path.Combine(Root, "b", "g.jsonl");
        string second = DataLock.PathFor(journalB);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(second)!);
        File.WriteAllText(second, string.Empty);
        File.SetAttributes(second, FileAttributes.ReadOnly); // 쓰기 모드로 열면 UnauthorizedAccessException (IOException 이 아님)
        try
        {
            _ = Assert.Throws<UnauthorizedAccessException>(() => DataLock.TryAcquire(planA, TimeSpan.Zero, journalPath: journalB));

            using var first = new FileStream(DataLock.PathFor(planA), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); // 첫 핸들이 새지 않아 바로 열림
            Assert.False(DataLock.IsHeld(planA)); // 참조 횟수도 남지 않음
        }
        finally
        {
            File.SetAttributes(second, FileAttributes.Normal);
        }
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
