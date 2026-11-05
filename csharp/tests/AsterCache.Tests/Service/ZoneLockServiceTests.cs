using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tests.Cli;
using AsterCache.Tests.Core;
using Xunit;

namespace AsterCache.Tests.Service;

/// 서비스의 적용 (정기 재적용, 자동 재배치 dry-run 과 실행) 이 CLI 와 같은 잠금 안에서 돌고, 잠금 안에서 활성 계획과 예약을 다시 읽는지.
/// DataLock.DefaultWait 를 바꾸므로 다른 시험과 동시에 돌지 않게 묶습니다.
[Collection(CliTests.VolumeSeam)]
public sealed class ZoneLockServiceTests : ApplyTestBase
{
    private string PlansDir => Directory.CreateDirectory(Path.Combine(Root, "plans")).FullName;

    private string JournalDir => Directory.CreateDirectory(Path.Combine(Root, "journals")).FullName;

    private string PlanPath => Path.Combine(PlansDir, "g.plan.json");

    private GameSettings Game => new("g", ["g.exe"], PlanPath + ".prefetch.json", PlanPath, Root, [Root], "GAMES", 1440);

    private IDisposable ShortWait()
    {
        TimeSpan previous = DataLock.DefaultWait;
        DataLock.DefaultWait = TimeSpan.Zero;
        return new Restore(() => DataLock.DefaultWait = previous);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }

    // CLI 가 잡는 것과 같은 잠금 (계획 폴더와 저널 폴더).
    private ForeignLock CliHolds() => new(PlanPath, Path.Combine(JournalDir, "g.jsonl"));

    [Fact]
    public void AutoRelocationDryRunAndExecuteReportBusyWhileAnotherHoldsTheLock()
    {
        PlanFile plan = Make();
        plan.Save(PlanPath);
        byte[] before = File.ReadAllBytes(PlanPath);
        AutoEnv env = AutoEnv.Create(JournalDir);
        using ForeignLock holder = CliHolds();
        using IDisposable wait = ShortWait();

        ApplyReport dry = env.Apply(Game, plan, new ApplyOptions(DryRun: true, SaveTo: PlanPath), default);
        ApplyReport run = env.Apply(Game, plan, new ApplyOptions(Execute: true, SaveTo: PlanPath), default);

        Assert.Equal(DataLock.BusyReason, dry.Stopped);
        Assert.Equal(DataLock.BusyReason, run.Stopped);
        Assert.Equal(before, File.ReadAllBytes(PlanPath)); // 잠금 밖에서는 계획을 다시 저장하지 않음
        Assert.False(File.Exists(Path.Combine(JournalDir, "g.jsonl")));
    }

    [Fact]
    public void ServiceWritesToTheActivePlanAndItsSiblingsAreRefusedWithoutTheLockToken()
    {
        PlanFile plan = Make().Resolve(20000);
        plan.Save(PlanPath);
        AutoEnv env = AutoEnv.Create(JournalDir);

        _ = Assert.Throws<InvalidOperationException>(() => env.SavePlan(plan, [], PlanPath));
        _ = Assert.Throws<InvalidOperationException>(() => env.MoveAside(PlanPath, PlanPath + ".stale"));
        _ = Assert.Throws<InvalidOperationException>(() => env.Touch(PlanPath));
        Assert.True(File.Exists(PlanPath));
        Assert.False(File.Exists(PlanPath + ".stale"));
    }

    [Fact]
    public void WithTheGameLockHeldTheWritesPassAndTheInnerLockedCallsReenterInsteadOfWaiting()
    {
        PlanFile plan = Make().Resolve(20000);
        plan.Save(PlanPath);
        AutoEnv env = AutoEnv.Create(JournalDir);
        using IDisposable wait = ShortWait(); // 안쪽 호출이 재진입하지 않으면 바로 busy

        using IDisposable? gate = env.Lock(Game);
        Assert.NotNull(gate);
        ApplyReport inner = ReapplyEnv.RunLocked(JournalDir, Game, plan, new ApplyOptions(DryRun: true), _ => new ApplyReport()); // 볼륨은 열지 않고 잠금만 확인
        Assert.Null(inner.Stopped); // busy 아님
        Assert.Null(env.ArchiveJournal(Game)); // 저널이 없음: 잠금은 재진입
        env.Touch(PlanPath);
        env.MoveAside(PlanPath, PlanPath + ".stale");

        Assert.True(File.Exists(PlanPath + ".stale"));
    }

    [Fact]
    public void AnotherThreadHoldingTheGameLockMakesTheServiceLockBusyNotAnException()
    {
        AutoEnv env = AutoEnv.Create(JournalDir);
        using ForeignLock holder = CliHolds();
        using IDisposable wait = ShortWait();

        Assert.Null(env.Lock(Game));
    }

    [Fact]
    public void PeriodicReapplyReportsBusyWhileAnotherHoldsTheLock()
    {
        PlanFile plan = Make().Resolve(20000);
        plan.Save(PlanPath);
        ReapplyEnv env = ReapplyEnv.Create(canApply: true, JournalDir);
        using ForeignLock holder = CliHolds();
        using IDisposable wait = ShortWait();

        ApplyReport rep = env.Apply(Game, plan, null, default);

        Assert.Equal(DataLock.BusyReason, rep.Stopped);
    }

    [Fact]
    public void ServiceAndCliLockTheSameFilesEvenWhenThePlanFolderDiffers()
    {
        string otherPlan = Path.Combine(Root, "elsewhere", "x.plan.json");
        string journal = Path.Combine(JournalDir, "g.jsonl");
        using var cli = new ForeignLock(otherPlan, journal); // 다른 --out 폴더에서 같은 저널을 쓰는 호출
        Make().Resolve(20000).Save(PlanPath);
        using IDisposable wait = ShortWait();

        ApplyReport rep = ReapplyEnv.RunLocked(JournalDir, Game, Make().Resolve(20000), new ApplyOptions(DryRun: true), _ => throw new InvalidOperationException("must not run"));

        Assert.Equal(DataLock.BusyReason, rep.Stopped);
    }

    [Fact]
    public async Task ThePlanChangedWhileWaitingIsRejectedAndTheNewPlanIsKept()
    {
        PlanFile oldPlan = Make();
        PlanFile newPlan = Make().Resolve(30000);
        oldPlan.Save(PlanPath);
        ForeignLock holder = CliHolds();
        bool ran = false;

        Task<ApplyReport> waiting = Task.Run(() => ReapplyEnv.RunLocked(JournalDir, Game, oldPlan, new ApplyOptions(DryRun: true, SaveTo: PlanPath), _ =>
        {
            ran = true;
            return new ApplyReport();
        }));
        await Task.Delay(300);
        newPlan.Save(PlanPath); // 다른 쪽이 새 계획을 승격하고 잠금을 풂
        holder.Dispose();

        PlanRejectedException e = await Assert.ThrowsAsync<PlanRejectedException>(() => waiting);
        Assert.Equal(DataLock.PlanChangedMessage, e.Message);
        Assert.False(ran);
        Assert.Equal(newPlan.Sha(), PlanFile.Load(PlanPath).Sha());
    }

    [Fact]
    public async Task ReservationsAreReadAgainInsideTheLock()
    {
        PlanFile plan = Make();
        plan.Save(PlanPath);
        ForeignLock holder = CliHolds();
        IReadOnlyList<(long Lcn, long Clusters)>? seen = null;
        var stale = new ApplyOptions(DryRun: true, Reserved: [(1L, 1L)]); // 잠금 전에 읽은 옛 스냅샷

        Task<ApplyReport> waiting = Task.Run(() => ReapplyEnv.RunLocked(JournalDir, Game, plan, stale, o =>
        {
            seen = o.Reserved;
            return new ApplyReport();
        }));
        await Task.Delay(300);
        Make().Resolve(60000).Save(Path.Combine(PlansDir, "other.plan.json")); // 기다리는 동안 다른 게임이 존을 예약
        holder.Dispose();
        _ = await waiting;

        Assert.NotNull(seen);
        Assert.Contains(seen!, r => r.Lcn == 60000);
        Assert.DoesNotContain(seen!, r => r.Lcn == 1);
    }
}
