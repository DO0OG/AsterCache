using AsterCache.Core;
using AsterCache.Core.Tier;
using AsterCache.Service;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 상태 파일 쓰기와 시계: 임시 파일을 디스크까지 내린 뒤 교체, 실행 중 시스템 시계가 튀어도 경과 시간 유지, 전원 조회 실패는 보수적으로 거절.
public sealed class ClockAndStateFileTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void DurableFileWritesTheBytesAndOverwrites()
    {
        string path = _dir.Combine("x.tmp");

        DurableFile.WriteAllBytes(path, [1, 2, 3]);
        DurableFile.WriteAllBytes(path, [9]);

        Assert.Equal(new byte[] { 9 }, File.ReadAllBytes(path));
    }

    [Fact]
    public void TierJournalSaveLeavesNoTempFileAndDoesNotShareTempNames()
    {
        string path = Path.Combine(_dir.Path, "j", "Foo.json");
        var j = new TierJournal { Game = "Foo", Root = @"C:\AsterCache\tier" };

        j.Save(path);
        j.Save(path);

        Assert.Equal(["Foo.json"], Directory.GetFiles(Path.Combine(_dir.Path, "j")).Select(Path.GetFileName).ToList());
        Assert.Equal("Foo", TierJournal.Load(path).Game);
    }

    [Fact]
    public void MonotonicClockIgnoresASystemClockJump()
    {
        DateTimeOffset wall = Start;
        long ticks = 0;
        var clock = new MonotonicClock(() => wall, () => ticks);

        ticks += System.Diagnostics.Stopwatch.Frequency * 60; // 1 분 경과
        wall = wall.AddHours(2); // 시스템 시계가 2 시간 앞으로 튐

        Assert.Equal(Start.AddMinutes(1), clock.Now());
    }

    [Fact]
    public void RecoveryWaitSurvivesAClockJumpDuringTheRun()
    {
        DateTimeOffset wall = Start;
        long ticks = 0;
        var clock = new MonotonicClock(() => wall, () => ticks);
        var world = new FakeWorld { Clock = clock.Now };
        var recovery = new DriveRecovery(world.Env(), new ListLog());
        recovery.Begin(@"E:\", null, TimeSpan.FromMinutes(30));

        ticks += System.Diagnostics.Stopwatch.Frequency * 60;
        wall = wall.AddHours(2); // 시계가 2 시간 튀어도 회복 대기는 1 분만 지남
        Assert.True(recovery.IsRecovering(@"E:\"));

        ticks += System.Diagnostics.Stopwatch.Frequency * 60 * 30;
        Assert.False(recovery.IsRecovering(@"E:\"));
    }

    [Theory]
    [InlineData(false, 0, 0, 0, 50, false)] // 조회 실패
    [InlineData(true, 1, 0, 10, 50, true)] // AC
    [InlineData(true, 0, 128, 10, 50, true)] // 배터리 없음
    [InlineData(true, 0, 0, 80, 50, true)]
    [InlineData(true, 0, 0, 20, 50, false)]
    [InlineData(true, 0, 0, 255, 50, false)] // 배터리 비율 모름
    public void PowerDecisionIsConservativeWhenTheQueryFails(bool queried, byte ac, byte flag, byte percent, int min, bool expected)
    {
        Assert.Equal(expected, PowerProbe.Decide(queried, ac, flag, percent, min));
    }
}
