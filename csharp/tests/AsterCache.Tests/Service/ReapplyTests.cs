using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tests.Core;
using Xunit;

namespace AsterCache.Tests.Service;

/// 재적용 판단 표: 적중률, 게임 실행, 디스크 바쁨, 지문 변경, 권한의 조합.
public sealed class ReapplyTests
{
    private const double Hit = 1.0;
    private const double Miss = 0.5;

    private static ReapplyVerdict Decide(bool applied, bool changed, double? ratio, bool game, bool disk, bool can) =>
        ReapplyPolicy.Decide(new ReapplyInputs(applied, changed, ratio, () => game, () => disk, can));

    public static TheoryData<bool, bool, double?, bool, bool, bool, ReapplyVerdict> Table => new()
    {
        // applied, changed, ratio, game, disk, canApply, expected
        { false, false, Miss, false, false, true, ReapplyVerdict.NotApplied },
        { false, true, Miss, false, false, true, ReapplyVerdict.NotApplied },
        { true, true, Miss, false, false, true, ReapplyVerdict.PlanChanged },
        { true, true, Hit, false, false, true, ReapplyVerdict.PlanChanged },
        { true, false, Hit, true, true, false, ReapplyVerdict.Healthy },
        { true, false, 0.9, true, true, false, ReapplyVerdict.Healthy },
        { true, false, null, true, true, false, ReapplyVerdict.Healthy },
        { true, false, 0.8999, false, false, true, ReapplyVerdict.Reapply },
        { true, false, Miss, true, false, true, ReapplyVerdict.GameRunning },
        { true, false, Miss, true, true, true, ReapplyVerdict.GameRunning },
        { true, false, Miss, true, false, false, ReapplyVerdict.GameRunning },
        { true, false, Miss, false, true, true, ReapplyVerdict.DiskBusy },
        { true, false, Miss, false, true, false, ReapplyVerdict.DiskBusy },
        { true, false, Miss, false, false, false, ReapplyVerdict.ApplyDisabled },
        { true, false, 0.0, false, false, true, ReapplyVerdict.Reapply },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void DecisionTable(bool applied, bool changed, double? ratio, bool game, bool disk, bool can, ReapplyVerdict expected)
    {
        Assert.Equal(expected, Decide(applied, changed, ratio, game, disk, can));
    }

    [Fact]
    public void GameAndDiskAreNotProbedWhenNothingNeedsReapplying()
    {
        static bool Boom() => throw new InvalidOperationException("probed");

        Assert.Equal(ReapplyVerdict.Healthy, ReapplyPolicy.Decide(new ReapplyInputs(true, false, 0.95, Boom, Boom, true)));
        Assert.Equal(ReapplyVerdict.PlanChanged, ReapplyPolicy.Decide(new ReapplyInputs(true, true, 0.1, Boom, Boom, true)));
        Assert.Equal(ReapplyVerdict.GameRunning, ReapplyPolicy.Decide(new ReapplyInputs(true, false, 0.1, () => true, Boom, true)));
    }

    // ---- 실제 계획(e3 픽스처: 1048 구간, 존 시작 있음)으로 검사기 전체를 돌립니다 ----

    private static readonly GameSettings Bench = new("bench", ["bench.exe"], @"C:\p\t.json", @"C:\p\plan.json", @"D:\AsterBench", [@"D:\AsterBench"], "BENCH", 60);

    internal static ReapplyEnv NoIoEnv() => new()
    {
        CanApply = false,
        GameRunning = _ => throw new InvalidOperationException(),
        DiskBusy = _ => throw new InvalidOperationException(),
        LoadPlan = _ => throw new InvalidOperationException(),
        Extents = _ => throw new InvalidOperationException(),
        Stat = _ => throw new InvalidOperationException(),
        RealPath = Path.GetFullPath,
        Apply = (_, _, _) => throw new InvalidOperationException(),
    };

    private sealed class Rig
    {
        public Rig(string planFixture = "e3/plan.json")
        {
            Plan = PlanFile.Load(PlanFixtures.PathOf(planFixture));
        }

        public PlanFile Plan { get; }

        public bool Moved { get; set; } = true;

        public bool SourceChanged { get; set; }

        public bool SourceMissing { get; set; }

        public bool GameRunning { get; set; }

        public bool DiskBusy { get; set; }

        public bool CanApply { get; set; } = true;

        public Exception? ApplyThrows { get; set; }

        public bool ApplyCancelled { get; set; }

        public bool PlanMissing { get; set; }

        public CancellationToken LastToken { get; private set; }

        public int ApplyCalls { get; private set; }

        public int ExtentCalls { get; private set; }

        public ListLog Log { get; } = new();

        private ReapplyChecker? _checker;

        public ReapplyVerdict Check(GameSettings game, CancellationToken ct = default)
        {
            ILookup<string, PlanTask> byPath = Plan.Tasks.ToLookup(t => t.Path, StringComparer.Ordinal);
            Dictionary<string, Fingerprint> fps = Plan.Fingerprints.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var env = new ReapplyEnv
            {
                CanApply = CanApply,
                GameRunning = _ => GameRunning,
                DiskBusy = _ => DiskBusy,
                LoadPlan = _ => PlanMissing ? throw new FileNotFoundException() : Plan,
                Extents = p =>
                {
                    ExtentCalls++;
                    return Moved
                        ? byPath[p].OrderBy(t => t.Vcn).Select(t => new Extent(t.Vcn, t.TargetLcn, t.Clusters)).ToList()
                        : [new Extent(0, 1000, 1_000_000)];
                },
                Stat = p => SourceMissing ? throw new FileNotFoundException() : SourceChanged ? fps[p] with { Size = fps[p].Size + 1 } : fps[p],
                RealPath = Path.GetFullPath,
                Apply = (_, _, token) =>
                {
                    ApplyCalls++;
                    LastToken = token;
                    return ApplyThrows is null ? new ApplyReport { MovedBytes = 4096, Stopped = ApplyCancelled ? "cancelled" : null } : throw ApplyThrows;
                },
            };
            return (_checker ??= new ReapplyChecker(env, Log)).Check(game, ct);
        }
    }

    [Fact]
    public void MovedLayoutIsHealthyAndNothingIsApplied()
    {
        var rig = new Rig();

        Assert.Equal(ReapplyVerdict.Healthy, rig.Check(Bench));
        Assert.Equal(0, rig.ApplyCalls);
        Assert.Contains(rig.Log.Lines, l => l.Contains("zone hit 100.0%", StringComparison.Ordinal));
    }

    [Fact]
    public void UnmovedLayoutWithIdleDiskAndNoGameIsReappliedAndOptimizationIsOnlyLogged()
    {
        var rig = new Rig { Moved = false };

        Assert.Equal(ReapplyVerdict.Reapply, rig.Check(Bench));
        Assert.Equal(1, rig.ApplyCalls);
        Assert.Contains(rig.Log.Lines, l => l.Contains("optimization", StringComparison.Ordinal) && l.StartsWith("Warn", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false, true, ReapplyVerdict.GameRunning)]
    [InlineData(false, true, true, ReapplyVerdict.DiskBusy)]
    [InlineData(true, true, true, ReapplyVerdict.GameRunning)]
    [InlineData(false, false, false, ReapplyVerdict.ApplyDisabled)]
    public void DegradedLayoutIsNotAppliedWhenGameBusyDiskOrNoPermission(bool game, bool disk, bool can, ReapplyVerdict expected)
    {
        var rig = new Rig { Moved = false, GameRunning = game, DiskBusy = disk, CanApply = can };

        Assert.Equal(expected, rig.Check(Bench));
        Assert.Equal(0, rig.ApplyCalls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedOrMissingSourceMeansPlanChangedWithoutReadingLayoutOrApplying(bool missing)
    {
        var rig = new Rig { Moved = false, SourceChanged = !missing, SourceMissing = missing };

        Assert.Equal(ReapplyVerdict.PlanChanged, rig.Check(Bench));
        Assert.Equal((0, 0), (rig.ApplyCalls, rig.ExtentCalls));
    }

    [Fact]
    public void MissingPlanFileIsWaitingNotAnError()
    {
        var rig = new Rig { PlanMissing = true };

        Assert.Equal(ReapplyVerdict.NotApplied, rig.Check(Bench));
        Assert.Equal((0, 0), (rig.ApplyCalls, rig.ExtentCalls));
        Assert.DoesNotContain(rig.Log.Lines, l => l.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanThatWasNeverAppliedIsLeftToTheUser()
    {
        var rig = new Rig("fresh/plan.json") { Moved = false };

        Assert.Equal(ReapplyVerdict.NotApplied, rig.Check(Bench));
        Assert.Equal((0, 0), (rig.ApplyCalls, rig.ExtentCalls));
    }

    [Fact]
    public void PlanPathOutsideAllowedRootsFailsBeforeAnyIo()
    {
        var rig = new Rig { Moved = false };
        GameSettings narrow = Bench with { AllowedRoots = [@"D:\Elsewhere"] };

        Assert.Equal(ReapplyVerdict.Failed, rig.Check(narrow));
        Assert.Equal((0, 0), (rig.ApplyCalls, rig.ExtentCalls));
        Assert.Contains(rig.Log.Lines, l => l.StartsWith("Error: bench", StringComparison.Ordinal));
    }

    [Fact]
    public void ShaMismatchAfterApplyIsLoggedAsFailure()
    {
        var rig = new Rig { Moved = false, ApplyThrows = new ShaMismatchException(["D:\\AsterBench\\data\\f000.bin"]) };

        Assert.Equal(ReapplyVerdict.Failed, rig.Check(Bench));
        Assert.Contains(rig.Log.Lines, l => l.Contains("DATA CHECK FAILED", StringComparison.Ordinal));
    }

    [Fact]
    public void ApplyCancelledByGameStartIsAResumableStateNotAnError()
    {
        var rig = new Rig { Moved = false, ApplyCancelled = true };
        using var cts = new CancellationTokenSource();

        Assert.Equal(ReapplyVerdict.Cancelled, rig.Check(Bench, cts.Token));
        Assert.Equal(cts.Token, rig.LastToken);
        Assert.DoesNotContain(rig.Log.Lines, l => l.StartsWith("Error", StringComparison.Ordinal));

        rig.ApplyCancelled = false; // 게임이 끝난 뒤 다음 검사: 같은 계획·저널로 이어서 적용
        Assert.Equal(ReapplyVerdict.Reapply, rig.Check(Bench));
        Assert.Equal(2, rig.ApplyCalls);
        Assert.Contains(rig.Log.Lines, l => l.Contains("partially applied", StringComparison.Ordinal));
        Assert.Single(rig.Log.Lines, l => l.Contains("optimization", StringComparison.Ordinal)); // 첫 검사의 경고뿐, 재개 검사는 경고 아님
    }

    [Fact]
    public async Task GateCancelsTheRunningReapplyOnlyAndIgnoresStrayCancels()
    {
        var gate = new ReapplyGate();
        gate.Cancel(); // 돌고 있는 재적용이 없으면 무시
        using var started = new ManualResetEventSlim();
        Task<bool> running = Task.Run(() => gate.Run(CancellationToken.None, token =>
        {
            started.Set();
            return token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10));
        }));
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)));

        gate.Cancel();

        Assert.True(await running);
        gate.Cancel(); // 끝난 뒤에도 안전
        Assert.False(gate.Run(CancellationToken.None, token => token.IsCancellationRequested)); // 다음 재적용은 취소 상태로 시작하지 않음
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.True(gate.Run(stop.Token, token => token.IsCancellationRequested)); // 서비스 종료도 같은 토큰으로 전해짐
    }

    [Fact]
    public void PlanRejectedByApplierIsLoggedAsFailure()
    {
        var rig = new Rig { Moved = false, ApplyThrows = new PlanRejectedException("journal belongs to a different plan") };

        Assert.Equal(ReapplyVerdict.Failed, rig.Check(Bench));
        Assert.Equal(1, rig.ApplyCalls);
    }
}
