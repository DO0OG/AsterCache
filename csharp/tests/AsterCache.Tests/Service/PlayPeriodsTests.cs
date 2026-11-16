using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 600 초 이후 10 분 단위 요약: 누계 검사점의 차이로 구간을 셉니다.
public sealed class PlayPeriodsTests
{
    private const long Mib = 1L << 20;

    private static PlayWindowSummary Cumulative(int? seconds, double measuredAt, long mib, ulong reads, double readSeconds, int slow) =>
        new(seconds, measuredAt, mib * Mib, reads, readSeconds, slow, slow, (int)measuredAt, 0, false, false, 0, 1);

    [Fact]
    public void PeriodsAreDifferencesOfNeighborCheckpointsAndTheTailUpToTheEnd()
    {
        PlayWindowSummary[] cumulative =
        [
            Cumulative(600, 600, 100, 1000, 10, 2),
            Cumulative(1200, 1200, 300, 4000, 50, 9),
            Cumulative(1800, 1800, 350, 4500, 55, 9),
        ];
        PlayWindowSummary end = Cumulative(null, 1950.4, 420, 5000, 60, 11);

        List<PlayPeriodSummary> periods = PlayPeriods.Build(cumulative, end);

        Assert.Equal([(600, 1200, 200 * Mib, 3000UL, 40.0, 7), (1200, 1800, 50 * Mib, 500UL, 5.0, 0), (1800, 1951, 70 * Mib, 500UL, 5.0, 2)],
            periods.Select(p => (p.FromSeconds, p.ToSeconds, p.ReadBytes, p.ReadCount, p.ReadSeconds, p.SlowSamples)));
        Assert.Equal("10-20 min 200 MiB/3000 reads/40.0 s read time/7 slow samples; 20-30 min 50 MiB/500 reads/5.0 s read time/0 slow samples; 30-33 min 70 MiB/500 reads/5.0 s read time/2 slow samples", PlayPeriods.Format(periods));
    }

    [Fact]
    public void SessionsShorterThanTheFirstCheckpointHaveNoPeriodsAndAnUnfinishedSessionHasNoTail()
    {
        Assert.Empty(PlayPeriods.Build([], Cumulative(null, 300, 50, 10, 1, 0)));
        Assert.Empty(PlayPeriods.Build([Cumulative(600, 600, 100, 1000, 10, 2)], null));
        Assert.Empty(PlayPeriods.Build([Cumulative(600, 600, 100, 1000, 10, 2)], Cumulative(null, 600, 100, 1000, 10, 2))); // 검사점 뒤 읽은 시간이 없으면 구간을 만들지 않음
    }

    [Fact]
    public void CheckpointsAreMultiplesOfThePeriodAndCapped()
    {
        int[] checkpoints = PlayPeriods.Checkpoints(600);

        Assert.Equal(600, checkpoints[0]);
        Assert.Equal(1200, checkpoints[1]);
        Assert.Equal(PlayPeriods.MaxCheckpoints, checkpoints.Length);
    }
}
