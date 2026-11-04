using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 존 확장 재계획: 기존 존 바로 뒤가 다른 파일에 쓰이고 있어 덧붙일 수 없을 때 확장을 포기하지 않고 새 존으로 전체를 옮김.
public sealed partial class AutoRelocatorTests
{
    private const string RegionInUse = "the region after the zone is in use, looking for a new one";

    private int _archiveCalls;

    // 병합 트레이스가 계획보다 새로운 상태 + 존 바로 뒤 (덧붙일 구간 안) 에 다른 파일이 있음. 반환: (옛 존 시작, 다른 파일 extent).
    private long DoneMergedWithAFileAfterTheZone(out List<Extent> other)
    {
        long start = DoneThenMergedTrace(Merged(RestOfTheBlocks()));
        other = [new(0, start + 100, 8)];
        Vol.Ext[Path.Combine(Root, "other.bin")] = other;
        Func<GameSettings, string?> inner = _w.ArchiveJournal;
        _w.ArchiveJournal = g =>
        {
            _archiveCalls++;
            return inner(g);
        };
        return start;
    }

    private bool HasExtendMark() => File.Exists(_game.Plan + AutoRelocator.ExtendSuffix) || _w.Files.Contains(_game.Plan + AutoRelocator.ExtendSuffix);

    private void AssertSwitchedToANewZone(long start, List<Extent> other, PlanFile before)
    {
        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotNull(Entry.RelocatedAt);
        Assert.False(HasExtendMark()); // 확장 중이 아님
        Assert.Equal(before.Sha(), PlanFile.Load(_game.Plan + AutoRelocator.StaleSuffix).Sha()); // 옛 계획은 일반 .stale 자리로
        PlanFile fresh = PlanFile.Load(_game.Plan);
        Assert.NotEqual(start, fresh.Zone.StartLcn); // 새 존
        Assert.Equal(32L, fresh.Stats["hot_blocks"]);
        Assert.True(Verify(fresh).Contiguous);
        Assert.Equal(other, Vol.Ext[Path.Combine(Root, "other.bin")]); // 다른 파일은 그대로
        Assert.Single(_log.Lines, l => l.Contains(RegionInUse, StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("zone extension blocked", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("zone extended by", StringComparison.Ordinal));
        Assert.Equal([start], _w.PlanStarts); // 이전 존 시작에 맞춘 계획은 한 번뿐 (확장 → 새 존 → 확장 되풀이 없음)
        Assert.Equal(2, _archiveCalls); // 확장 시작 때 한 번, 새 존으로 바꿀 때 한 번
    }

    [Fact]
    public void ExtensionWhoseRegionAfterTheZoneIsInUseGoesToANewZoneAtPlanTime()
    {
        long start = DoneMergedWithAFileAfterTheZone(out List<Extent> other);
        PlanFile before = PlanFile.Load(_game.Plan);
        _w.Bitmap = _ => Vol.Bitmap();

        Tick();

        Assert.False(HasExtendMark()); // 계획 시점에 이미 새 존으로 바뀜
        Assert.Contains(_log.Lines, l => l.Contains(RegionInUse, StringComparison.Ordinal));
        Assert.Null(PlanFile.Load(_game.Plan).Zone.StartLcn); // 새 존은 적용 때 정함

        RunUntilDone();

        AssertSwitchedToANewZone(start, other, before);
    }

    [Fact]
    public void ExtensionRejectedAsZoneNotFreeAtApplyTimeGoesToANewZone()
    {
        long start = DoneMergedWithAFileAfterTheZone(out List<Extent> other);
        PlanFile before = PlanFile.Load(_game.Plan);
        // 기본: 비트맵을 읽지 못함 → 계획 시점에는 모르고 확장 계획을 만듦

        Tick();
        Assert.True(HasExtendMark());

        RunUntilDone();

        AssertSwitchedToANewZone(start, other, before);
    }

    [Fact]
    public void BitmapReadFailureAtPlanTimeStillExtendsAndTheApplyTimeRejectionTakesTheNewZone()
    {
        long start = DoneMergedWithAFileAfterTheZone(out List<Extent> other);
        PlanFile before = PlanFile.Load(_game.Plan);
        _w.Bitmap = _ => throw new IOException("bitmap");

        Tick();
        Assert.True(HasExtendMark()); // 확인 불가면 확장 시도

        RunUntilDone();

        AssertSwitchedToANewZone(start, other, before);
    }

    [Fact]
    public void ExtensionWithAFreeRegionAfterTheZoneIsNotSwitchedEvenWhenTheBitmapIsReadable()
    {
        long start = DoneThenMergedTrace(Merged(RestOfTheBlocks()));
        long usedBefore = (long)PlanFile.Load(_game.Plan).Stats["zone_used_clusters"]!;
        _w.Bitmap = _ => Vol.Bitmap();

        Tick();

        Assert.True(HasExtendMark());
        Assert.Equal(start, PlanFile.Load(_game.Plan).Zone.StartLcn);

        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.All(Vol.Moves, m => Assert.True(m.Lcn >= start + usedBefore));
        Assert.Contains(_log.Lines, l => l.Contains("zone extended by 1 MiB (run 2)", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains(RegionInUse, StringComparison.Ordinal));
    }

    [Fact]
    public void AfterTheSwitchToANewZoneTheSameTraceNeverStartsAnotherExtension()
    {
        long start = DoneMergedWithAFileAfterTheZone(out List<Extent> other);
        PlanFile before = PlanFile.Load(_game.Plan);
        _w.Bitmap = _ => Vol.Bitmap();
        Tick();
        RunUntilDone();
        AssertSwitchedToANewZone(start, other, before);

        for (int i = 0; i < 4; i++)
        {
            _w.Now += AutoRelocator.StaleCheckEvery;
            Tick();
        }

        _w.FileTimes[AutoTracer.RawTracePath(_game)] = _w.Now; // 다음 플레이가 쌓여 트레이스가 바뀌어도 새 계획에 이미 다 들어 있음
        for (int i = 0; i < 4; i++)
        {
            _w.Now += AutoRelocator.StaleCheckEvery;
            Tick();
        }

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.False(HasExtendMark());
        Assert.Equal([start], _w.PlanStarts);
        Assert.Equal(2, _archiveCalls);
    }

    [Fact]
    public void FailingToPlanTheNewZoneRestoresThePreviousPlanAndKeepsDone()
    {
        DoneMergedWithAFileAfterTheZone(out _);
        PlanFile before = PlanFile.Load(_game.Plan);
        _w.Bitmap = _ => Vol.Bitmap();
        Func<IReadOnlyList<IReadOnlyList<TraceEvent>>, string, long, PlanFile> inner = _w.MakePlan;
        _w.MakePlan = (sessions, origin, zoneMib) => zoneMib == ZoneSize.ProbeMib ? inner(sessions, origin, zoneMib) : throw new PyValueException("no zone\nsecond line");

        Tick();
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.NotNull(Entry.RelocatedAt);
        Assert.Empty(Vol.Moves);
        Assert.False(HasExtendMark());
        Assert.Equal(before.Sha(), PlanFile.Load(_game.Plan).Sha()); // 이전 계획이 되살아남
        Assert.Single(_log.Lines, l => l.Contains("zone extension blocked: plan failed: PyValueException: no zone", StringComparison.Ordinal));
    }
}
