using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 트레이스 금지 게임 (등록값 없이 폴더 탐지로만 보임) 의 계획이 프로필 보충 없이 만들어졌으면 한 번 다시 계획함.
public sealed partial class AutoRelocatorTests
{
    // 보충 없이 만든 계획으로 재배치를 끝낸 게임이 서비스를 다시 시작한 상태 (탐지 캐시도 새로): 이제 폴더 이름으로 kind 안티치트가 보임. 보충 후보 (엔진 프로필) 호출 수를 셈.
    private long DoneWithoutSupplementThenRestart(string? kind, Func<GameSettings, long, IReadOnlyList<TraceEvent>> profile, int minMib = 1)
    {
        ReportFullMoves();
        long start = DoneAndReadyToReplan(out _);
        SetAuto(a => a with { TraceMergeMinMib = minMib });
        DetectAntiCheat(kind);
        _w.ProfileEvents = (g, budget) =>
        {
            _profileCalls++;
            return profile(g, budget);
        };
        _relocator = null;
        Vol.Moves.Clear();
        return start;
    }

    // 프로필 보충 재계획인데 존 바로 뒤 (덧붙일 구간) 를 다른 파일이 쓰고 있음: 새 존으로 전체를 옮기지 않고 이전 계획을 되살려 보충을 보류.
    private void AssertSupplementDeferredAndNeverRepeated(long start)
    {
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotNull(Entry.RelocatedAt);
        Assert.False(HasExtendMark());
        PlanFile restored = PlanFile.Load(_game.Plan);
        Assert.Equal(start, restored.Zone.StartLcn); // 새 존이 아님
        Assert.Equal(5L, restored.Stats["hot_blocks"]); // 이전 계획이 되살아남
        Assert.False(restored.Stats.ContainsKey("profile_supplement_mib"));
        Assert.True(restored.Stats.ContainsKey("profile_supplement_checked")); // 되풀이하지 않게 표지는 남음
        Assert.NotEqual(1L, restored.Stats.TryGetValue("profile_supplement_replan", out object? mark) ? mark : null); // 이후 일반 확장이 보충 재계획으로 오인되지 않음
        Assert.Empty(Vol.Moves);
        Assert.Single(_log.Lines, l => l.Contains("zone extension blocked: ", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("looking for a new one", StringComparison.Ordinal));

        int calls = _profileCalls;
        Run(TimeSpan.FromHours(1));
        _relocator = null; // 서비스 재시작
        Tick();
        Run(TimeSpan.FromHours(1));

        Assert.Equal(1, ExtendLogs());
        Assert.Single(_log.Lines, l => l.Contains("zone extension blocked: ", StringComparison.Ordinal));
        Assert.Equal(calls, _profileCalls); // 보충을 다시 따지지 않음
        Assert.Empty(Vol.Moves);
        Assert.Equal(start, PlanFile.Load(_game.Plan).Zone.StartLcn);
    }

    [Fact]
    public void ProfileSupplementWhoseRegionAfterTheZoneIsInUseIsDeferredAtPlanTimeInsteadOfTakingANewZone()
    {
        long start = DoneWithoutSupplementThenRestart("kernel", (_, _) => WholeFiles());
        List<Extent> other = [new(0, start + 100, 8)];
        Vol.Ext[Path.Combine(Root, "other.bin")] = other;
        _w.Bitmap = _ => Vol.Bitmap();

        Tick();
        RunUntilDone();

        AssertSupplementDeferredAndNeverRepeated(start);
        Assert.Equal(other, Vol.Ext[Path.Combine(Root, "other.bin")]);
    }

    [Fact]
    public void ProfileSupplementRejectedAsZoneNotFreeAtApplyTimeIsDeferredInsteadOfTakingANewZone()
    {
        long start = DoneWithoutSupplementThenRestart("kernel", (_, _) => WholeFiles());
        List<Extent> other = [new(0, start + 100, 8)];
        Vol.Ext[Path.Combine(Root, "other.bin")] = other; // 비트맵을 읽지 못함 → 계획 시점에는 모르고 확장 계획을 만들고 적용 때 거부됨

        Tick();
        Assert.True(HasExtendMark());
        RunUntilDone();

        AssertSupplementDeferredAndNeverRepeated(start);
        Assert.Equal(other, Vol.Ext[Path.Combine(Root, "other.bin")]);
    }

    [Fact]
    public void RegisteredGameWithDetectedKernelAntiCheatPlansAgainOnceWithTheProfileIntoTheSameZone()
    {
        long start = DoneWithoutSupplementThenRestart("kernel", (_, _) => WholeFiles());
        PlanFile before = PlanFile.Load(_game.Plan);
        long usedBefore = (long)before.Stats["zone_used_clusters"]!;

        Tick();

        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
        Assert.True(HasExtendMark());
        Assert.True(PlanFile.Load(_game.Plan + AutoRelocator.ExtendSuffix).Stats.ContainsKey("profile_supplement_checked")); // 확장이 막혀 되살려도 되풀이하지 않게 이전 계획에도 표지
        PlanFile planned = PlanFile.Load(_game.Plan);
        Assert.Equal(start, planned.Zone.StartLcn);
        Assert.Equal(5L, before.Stats["hot_blocks"]);
        Assert.Equal(32L, planned.Stats["hot_blocks"]);
        Assert.True(planned.Stats.ContainsKey("profile_supplement_mib"));

        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotEmpty(Vol.Moves);
        Assert.All(Vol.Moves, m => Assert.True(m.Lcn >= start + usedBefore)); // 이미 옮긴 블록은 다시 옮기지 않음
        Assert.False(HasExtendMark());
        Assert.Equal([start], _w.PlanStarts);
        Assert.Equal(1, ExtendLogs());

        Run(TimeSpan.FromHours(1)); // 이후 틱: 되풀이 없음
        int calls = _profileCalls;
        _relocator = null; // 서비스 재시작
        Tick();

        Assert.Equal(1, ExtendLogs());
        Assert.False(HasExtendMark());
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(calls, _profileCalls); // 보충한 계획은 다시 계산하지 않음
    }

    [Fact]
    public void BootPlanOfAGameWithDetectedKernelAntiCheatPlansAgainWithTheProfile()
    {
        ReportFullMoves();
        string raw = AutoTracer.RawTracePath(_game);
        _w.Files.Remove(raw); // 프리페치 기록으로 만든 boot 트레이스만 있음 (트레이스 금지 게임은 원시 트레이스가 없음)
        _w.Files.Add(AutoTracer.BootTracePath(_game));
        long start = DoneAndReadyToReplan(out _);
        Assert.Equal(AutoRelocator.PlanSourceBoot, PlanFile.Load(_game.Plan).Stats["source"]);
        SetAuto(a => a with { TraceMergeMinMib = 1 });
        DetectAntiCheat("kernel");
        _w.ProfileEvents = (_, _) => WholeFiles();
        _relocator = null;
        Vol.Moves.Clear();

        Tick();
        RunUntilDone();

        PlanFile after = PlanFile.Load(_game.Plan);
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal(start, after.Zone.StartLcn);
        Assert.Equal(AutoRelocator.PlanSourceBoot, after.Stats["source"]);
        Assert.True(after.Stats.ContainsKey("profile_supplement_mib"));
        Assert.Equal(1, ExtendLogs());
    }

    [Fact]
    public void SupplementBelowTheMinimumOnlyMarksThePlan()
    {
        DoneWithoutSupplementThenRestart("kernel", (_, _) => [new TraceEvent(F2, 3 * Blk, Blk)]); // 트레이스가 덮지 못한 64 KiB, 기준 1 MiB

        Tick();
        Run(TimeSpan.FromMinutes(10));

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotNull(Entry.RelocatedAt);
        Assert.False(HasExtendMark());
        var stats = PlanFile.Load(_game.Plan).Stats;
        Assert.True(stats.ContainsKey("profile_supplement_checked"));
        Assert.False(stats.ContainsKey("profile_supplement_mib"));
        Assert.Equal(0, ExtendLogs());
        Assert.Equal(1, _profileCalls);

        _relocator = null; // 서비스 재시작
        Tick();

        Assert.Equal(1, _profileCalls); // 표지가 있는 계획은 다시 계산하지 않음
        Assert.False(HasExtendMark());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ricochet")] // 리코셰는 트레이스를 허용
    public void TraceAllowedGameIsNotReplanned(string? kind)
    {
        DoneWithoutSupplementThenRestart(kind, (_, _) => WholeFiles());

        Tick();
        Run(TimeSpan.FromMinutes(10));

        Assert.Equal(0, _profileCalls);
        Assert.False(HasExtendMark());
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.False(PlanFile.Load(_game.Plan).Stats.ContainsKey("profile_supplement_checked"));
    }

    [Fact]
    public void UnknownDetectionIsNotAReasonToReplan()
    {
        DoneWithoutSupplementThenRestart(AutoEnv.AntiCheatUnknown, (_, _) => WholeFiles());

        Tick();

        Assert.Equal(0, _profileCalls);
        Assert.False(HasExtendMark());
        Assert.Equal(AutoStage.Done, Entry.Stage);
    }

    [Fact]
    public void RunningGameIsSkippedAndTheCheckHappensAfterItStops()
    {
        DoneWithoutSupplementThenRestart("kernel", (_, _) => WholeFiles());
        _w.Running = true;

        Tick();

        Assert.Equal(0, _profileCalls);
        Assert.False(HasExtendMark());
        Assert.Equal(AutoStage.Done, Entry.Stage);

        _w.Running = false;
        Tick();

        Assert.True(HasExtendMark());
        Assert.Equal(1, ExtendLogs());
    }

    [Fact]
    public void RelocationTurnedOffSkipsTheCheck()
    {
        DoneWithoutSupplementThenRestart("kernel", (_, _) => WholeFiles());
        SetAuto(a => a with { Relocate = false });

        Tick();

        Assert.Equal(0, _profileCalls);
        Assert.False(HasExtendMark());
        Assert.NotNull(Entry.RelocatedAt);
    }
}
