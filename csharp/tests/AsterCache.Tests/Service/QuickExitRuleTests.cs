using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 실행 직후 종료 판정 규칙: 길이 분류, 물러남 조건, 새 드라이버 비교 (순수 함수).
public sealed class QuickExitRuleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private static KernelDriver Driver(string name) => new(name, @"C:\Drivers\" + name);

    [Theory]
    [InlineData(52, QuickExitKind.Quick)]
    [InlineData(89, QuickExitKind.Quick)]
    [InlineData(90, QuickExitKind.Between)]
    [InlineData(299, QuickExitKind.Between)]
    [InlineData(300, QuickExitKind.Normal)]
    [InlineData(7200, QuickExitKind.Normal)]
    public void SessionLengthIsQuickBelowTheLimitNormalFromFiveMinutesAndNeitherInBetween(int seconds, QuickExitKind expected)
    {
        Assert.Equal(expected, QuickExitRule.Classify(TimeSpan.FromSeconds(seconds), 90));
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(4, false)]
    [InlineData(5, true)]
    [InlineData(20, true)]
    public void SessionSplitsOnlyWhenThePreviousRunWasAtLeastFiveSeconds(int seconds, bool expected)
    {
        Assert.Equal(expected, QuickExitRule.SplitsSession(TimeSpan.FromSeconds(seconds)));
    }

    [Fact]
    public void TheQuickLimitFollowsTheSetting()
    {
        Assert.Equal(QuickExitKind.Between, QuickExitRule.Classify(TimeSpan.FromSeconds(52), 30));
        Assert.Equal(QuickExitKind.Quick, QuickExitRule.Classify(TimeSpan.FromSeconds(400), 600));
    }

    [Theory]
    [InlineData(2, null, true)]
    [InlineData(10, null, true)]
    [InlineData(11, null, false)]
    [InlineData(2, "exited", false)]
    [InlineData(2, "launched", false)]
    public void BackOffOnlyWithinTenMinutesAndOncePerStreak(int minutesLater, string? backedOff, bool expected)
    {
        Assert.Equal(expected, QuickExitRule.ShouldBackOff(T0, backedOff, T0.AddMinutes(minutesLater)));
        Assert.False(QuickExitRule.ShouldBackOff(null, null, T0));
    }

    [Fact]
    public void OnlyDriversMissingFromTheLastNormalListAreNew()
    {
        KernelDriver[] normal = [Driver("a.sys"), Driver("b.sys")];
        KernelDriver[] now = [Driver("a.sys"), Driver("c.sys"), new KernelDriver("B.SYS", @"c:\drivers\B.SYS"), Driver("c.sys")];

        IReadOnlyList<KernelDriver>? added = QuickExitRule.NewDrivers(normal, now);

        Assert.Equal(["c.sys"], added!.Select(d => d.Name)); // 경로 대소문자는 무시, 같은 경로는 한 번
        Assert.Empty(QuickExitRule.NewDrivers(normal, normal)!);
    }

    [Fact]
    public void MissingListsGiveNoClueInsteadOfAnEmptyAnswer()
    {
        Assert.Null(QuickExitRule.NewDrivers(null, [Driver("a.sys")]));
        Assert.Null(QuickExitRule.NewDrivers([Driver("a.sys")], null));
    }
}
