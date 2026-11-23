using AsterCache.Core.Plan;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 자동 재배치기가 통째 배치 계획 (파일 전체를 한 조각으로 zone 에) 을 옮기고 확인하는 흐름. 서비스는 아직 통째 계획을 스스로 만들지 않으므로 계획 함수를 대신 건 시험입니다.
public sealed partial class AutoRelocatorTests
{
    private void PlanWholeFiles()
    {
        Sessions = [Run(Enumerable.Range(0, 10).Select(b => (F1, (long)b)).Concat(Enumerable.Range(0, 10).Select(b => (F2, (long)b))).ToArray())];
        _w.MakePlan = (sessions, origin, zoneMib) => PlanFile.Make(sessions, origin, zoneMib << 20, Cb, p => Files[p], capacityBytes: 4L << 20);
    }

    [Fact]
    public void AWholeFilePlanIsMovedIntoOneRunPerFileAndVerifiedAsContiguous()
    {
        PlanWholeFiles();

        Tick();
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.Equal([F1, F2], WholeFileLayout.WholeFiles(plan));
        Assert.Single(Vol.Ext[F1]);
        Assert.Single(Vol.Ext[F2]);
        Assert.Contains(_log.Lines, l => l.Contains("auto relocation verified: contiguous", StringComparison.Ordinal));
    }

    [Fact]
    public void TheZoneIsBlockedByOwnDataOnlyForFilesTheWholePlanDoesNotPlace()
    {
        PlanWholeFiles();
        Tick();
        RunUntilDone();
        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.Equal(AutoStage.Done, Entry.Stage);

        Assert.False(Relocator.ZoneBlockedByOwnData(plan, 0)); // 통째 파일은 차가운 부분까지 이 계획의 데이터

        var stats = new AsterCache.Core.Json.JObject();
        foreach ((string key, object? value) in plan.Stats)
        {
            stats.Add(key, value);
        }

        stats[WholeFileLayout.WholeFilesKey] = new List<object?> { F1 }; // F2 는 이 계획이 옮기지 않는 자기 파일 데이터
        Assert.True(Relocator.ZoneBlockedByOwnData(plan with { Stats = stats }, 0));
    }
}
