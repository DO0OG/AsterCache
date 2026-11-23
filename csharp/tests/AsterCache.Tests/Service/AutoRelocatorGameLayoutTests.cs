using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임별 layout (config 의 게임 항목) 이 전역 auto.relocate_layout 보다 우선하는가: 계획 만들기·이행, 그리고 재배치가 막힌 게임에는 통째 계획을 남기지 않음.
public sealed partial class AutoRelocatorTests
{
    private void SetLayout(string? layout)
    {
        _game = _game with { Layout = layout };
        _settings = _settings with { Games = [_game] };
        _relocator = null;
    }

    [Fact]
    public void AGameLayoutOfWholeFileMakesAWholePlanEvenWhenTheGlobalSettingIsRanges()
    {
        UseWholeFile(whole: false);
        SetLayout(RelocateLayouts.WholeFile);

        Tick();

        Assert.Equal([F1, F2], WholeFileLayout.WholeFiles(PlanFile.Load(_game.Plan)));
        Assert.Single(_wholeCalls);
    }

    [Fact]
    public void AGameLayoutOfRangesMakesARangePlanEvenWhenTheGlobalSettingIsWholeFile()
    {
        UseWholeFile();
        SetLayout(RelocateLayouts.Ranges);

        Tick();

        Assert.Null(WholeFileLayout.WholeFiles(PlanFile.Load(_game.Plan)));
        Assert.Empty(_wholeCalls);
    }

    [Fact]
    public void TheMigrationFollowsTheGameLayoutNotTheGlobalSetting()
    {
        UseWholeFile();
        LegacyRangeDone();
        SetLayout(RelocateLayouts.Ranges);
        Tick();
        Assert.Empty(_switched); // 전역은 whole_file 이지만 이 게임은 ranges: 이행하지 않음

        UseWholeFile(whole: false);
        SetLayout(RelocateLayouts.WholeFile);
        Tick();
        Assert.Equal(["bench"], _switched); // 전역은 ranges 이지만 이 게임은 whole_file: 이행함
        Assert.NotNull(WholeFileLayout.WholeFiles(PlanFile.Load(_game.Plan)));
    }

    [Fact]
    public void ARangeGameDoesNotBlockTheMigrationOfAnotherGameThatInheritsTheGlobalSetting()
    {
        string plan2 = Path.Combine(Root, "plans", "other.plan.json");
        GameSettings other = new("other", ["other.exe"], plan2 + ".prefetch.json", plan2, Root, [Root], "GAMES", 1440) { Source = "auto" };
        _settings = _settings with { Games = [_game with { Layout = RelocateLayouts.Ranges }, other] };
        UseWholeFile();
        LegacyRangeDone(_settings.Games[0]);
        LegacyRangeDone(other);

        Tick();

        Assert.Equal(["other"], _switched);
    }

    [Theory]
    [InlineData(GameMode.PrewarmOnly, false)]
    [InlineData(GameMode.Inherit, true)]
    public void AGameWhosePolicyBlocksRelocationGetsNoWholePlanSoReleasingThePolicyDoesNotMoveItWhole(GameMode mode, bool wholePlan)
    {
        UseWholeFile();
        _game = _game with { Mode = mode };
        _settings = _settings with { Games = [_game] };

        Tick();

        Assert.Equal(wholePlan, WholeFileLayout.WholeFiles(PlanFile.Load(_game.Plan)) is not null);
        Assert.Equal(wholePlan ? 1 : 0, _wholeCalls.Count);
    }

    [Fact]
    public void ARangesGameKeepsItsOwnRangePlanWhenTheUpdateReplanMakesAFreshOne()
    {
        UseWholeFile();
        SetLayout(RelocateLayouts.Ranges);
        Tick();
        File.Delete(_game.Plan); // 업데이트 재계획처럼 계획이 없어져 새로 만듦

        Tick();

        Assert.Null(WholeFileLayout.WholeFiles(PlanFile.Load(_game.Plan)));
        Assert.Empty(_wholeCalls);
    }
}
