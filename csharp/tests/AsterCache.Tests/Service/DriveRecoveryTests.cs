using AsterCache.Core.Volume;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 쓰기 뒤 회복 판정 (DriveRecovery) 과 원시 읽기 표본의 순수 부분 (DiskProbe). 가짜 시계·가짜 표본만 쓰고 실제 볼륨은 읽지 않습니다.
public sealed class DriveRecoveryTests
{
    private const string Drive = @"E:\";
    private static readonly DriveBaseline Both = new(new ProbeSpot(10, 100), new ProbeSpot(20, 80));
    private readonly FakeWorld _w = new();
    private readonly ListLog _log = new();
    private readonly DriveRecovery _r;
    private readonly List<long> _probed = [];
    private double? _zone = 100;
    private double? _ref = 80;

    public DriveRecoveryTests()
    {
        _w.ProbeMbps = (_, lcn) =>
        {
            _probed.Add(lcn);
            return lcn == 10 ? _zone : _ref;
        };
        _r = new DriveRecovery(_w.Env(), _log);
    }

    private void Pass(int minutes)
    {
        _w.Now += TimeSpan.FromMinutes(minutes);
        _r.Poll();
    }

    [Fact]
    public void TwoConsecutiveGoodSamplesRecover()
    {
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        Assert.True(_r.IsRecovering(Drive));

        Pass(2);
        Assert.True(_r.IsRecovering(Drive));
        Pass(2);

        Assert.False(_r.IsRecovering(Drive));
        Assert.Contains(_log.Lines, l => l.Contains("drive E: recovered after 4 min", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(90, 72, true)]
    [InlineData(89, 72, false)]
    [InlineData(90, 71, false)]
    public void NinetyPercentOfEverySpotIsTheLine(double zone, double reference, bool recovers)
    {
        (_zone, _ref) = (zone, reference);
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));

        Pass(2);
        Pass(2);

        Assert.Equal(!recovers, _r.IsRecovering(Drive));
    }

    [Fact]
    public void ABadSampleBetweenGoodOnesRestartsTheStreak()
    {
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        Pass(2);
        _zone = 50;
        Pass(2);
        _zone = 100;
        Pass(2);
        Assert.True(_r.IsRecovering(Drive)); // 좋음, 나쁨, 좋음: 연속이 아님

        Pass(2);
        Assert.False(_r.IsRecovering(Drive));
    }

    [Fact]
    public void FirstNotRecoveredSampleLogsOnceWithValuesAndBaselines()
    {
        _zone = 61;
        _ref = 70;
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));

        Pass(2);
        Pass(2);

        Assert.Single(_log.Lines, l => l.Contains("drive E: recovering after relocation (zone 61 MB/s / 100 baseline, ref 70/80)", StringComparison.Ordinal));
    }

    [Fact]
    public void LimitEndsTheWaitEvenIfNeverRecovered()
    {
        _zone = 10;
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(10));
        Pass(8);
        Assert.True(_r.IsRecovering(Drive));

        _w.Now += TimeSpan.FromMinutes(2);
        Assert.False(_r.IsRecovering(Drive)); // 상한 시각이면 Poll 전에도 끝
        _r.Poll();
        Assert.Contains(_log.Lines, l => l.Contains("recovery wait ended at the 10 min limit", StringComparison.Ordinal));
        Assert.False(_r.IsRecovering(Drive));
    }

    [Fact]
    public void NoBaselineIsAFixedWaitWithoutAnySample()
    {
        _r.Begin(Drive, null, TimeSpan.FromMinutes(15));

        Pass(14);
        Assert.True(_r.IsRecovering(Drive));
        Pass(1);

        Assert.False(_r.IsRecovering(Drive));
        Assert.Empty(_probed);
    }

    [Fact]
    public void ZeroLimitDoesNotWait()
    {
        _r.Begin(Drive, Both, TimeSpan.Zero);

        Assert.False(_r.IsRecovering(Drive));
    }

    [Fact]
    public void WithoutAZoneBaselineOnlyTheReferenceIsSampledAndJudged()
    {
        _zone = 1; // 존 값은 무시됨
        _r.Begin(Drive, new DriveBaseline(null, new ProbeSpot(20, 80)), TimeSpan.FromMinutes(60));

        Pass(2);
        Pass(2);

        Assert.False(_r.IsRecovering(Drive));
        Assert.All(_probed, lcn => Assert.Equal(20, lcn));
    }

    [Theory]
    [InlineData(4.0 * (1 << 20))]
    [InlineData(100.0 * (1 << 20))]
    [InlineData(null)]
    public void BusyOrUnreadableDiskSkipsTheSampleAndKeepsTheStreak(double? busy)
    {
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));
        Pass(2); // 좋음 1 회
        _w.Disk = busy;
        Pass(2);
        Pass(2);
        Assert.Equal(2, _probed.Count); // 바쁜 회차에는 재지 않음 (첫 회차의 두 곳뿐)

        _w.Disk = 0;
        Pass(2);

        Assert.False(_r.IsRecovering(Drive)); // 건너뛴 회차는 연속을 끊지 않음
    }

    [Fact]
    public void FailedProbeIsSkippedLikeABusyDisk()
    {
        _ref = null;
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(60));

        Pass(2);
        Pass(2);
        Assert.True(_r.IsRecovering(Drive));

        _ref = 80;
        Pass(2);
        Pass(2);
        Assert.False(_r.IsRecovering(Drive));
    }

    [Fact]
    public void DriveKeyIgnoresCaseAndTrailingSeparator()
    {
        _r.Begin("e:", Both, TimeSpan.FromMinutes(60));

        Assert.True(_r.IsRecovering(@"E:\"));
        Assert.True(_r.IsRecovering("e:"));
        Assert.False(_r.IsRecovering(@"D:\"));
    }

    [Fact]
    public void UpdateWatchNeedsABaselineThenFollowsTheSameJudgement()
    {
        TimeSpan limit = TimeSpan.FromMinutes(60);
        Assert.Null(_r.WatchUpdate(Drive, _w.Now, limit)); // 쓰기 전 기준이 없으면 호출하는 쪽의 고정 분

        _r.Begin(Drive, Both, limit);
        Pass(2);
        Pass(2); // 회복 (Begin 의 추적)
        Assert.False(_r.IsRecovering(Drive));

        _w.Now += TimeSpan.FromMinutes(1);
        DateTimeOffset updatedAt = _w.Now;
        _zone = 50; // 업데이트가 흩어 쓴 뒤 느림
        Assert.True(_r.WatchUpdate(Drive, updatedAt, limit));
        _zone = 100;
        Pass(2);
        Pass(2);

        Assert.False(_r.WatchUpdate(Drive, updatedAt, limit)); // 회복 뒤 같은 업데이트로 다시 시작하지 않음
    }

    [Fact]
    public void UpdateWatchOlderThanTheLimitIsNotRecovering()
    {
        _r.Begin(Drive, Both, TimeSpan.FromMinutes(1));
        Pass(2);

        Assert.False(_r.WatchUpdate(Drive, _w.Now - TimeSpan.FromMinutes(90), TimeSpan.FromMinutes(60)));
    }

    // ---- DiskProbe 의 순수 부분 (실제 볼륨을 열지 않음) ----

    private static VolumeBitmap Bits(params (bool Used, int Length)[] runs)
    {
        var bits = new List<bool>();
        foreach ((bool used, int length) in runs)
        {
            bits.AddRange(Enumerable.Repeat(used, length));
        }

        byte[] data = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++)
        {
            if (bits[i])
            {
                data[i >> 3] |= (byte)(1 << (i & 7));
            }
        }

        return new VolumeBitmap(0, bits.Count, data);
    }

    // 1 MiB 클러스터: 표본 64 클러스터, 존에서 4 GiB = 4096 클러스터 뒤.
    private static readonly VolumeBitmap Map = Bits((true, 500), (false, 2500), (true, 200), (false, 1800), (true, 5000));

    [Fact]
    public void ReferenceIsTheAllocatedRunNearestToFourGibAfterTheZone()
    {
        (bool zoneAllocated, long? reference) = DiskProbe.Locate(Map, 1 << 20, 0);

        Assert.True(zoneAllocated);
        Assert.Equal(5000, reference); // 3000..3200 은 4096 에서 960 칸, 5000.. 은 904 칸
    }

    [Fact]
    public void ZoneThatIsStillFreeIsNotAllocated()
    {
        (bool zoneAllocated, long? reference) = DiskProbe.Locate(Map, 1 << 20, 1000);

        Assert.False(zoneAllocated);
        Assert.Equal(5096, reference); // 목표 1000 + 4096 이 5000.. 구간 안
    }

    [Fact]
    public void ReferenceIsNullWhenNoRunIsLongEnough()
    {
        Assert.Null(DiskProbe.FindReference(Bits((true, 60), (false, 100), (true, 63)), 0, 64));
    }

    [Fact]
    public void ReferenceInsideARunStaysAtTheTargetAndNeverCrossesItsEnd()
    {
        VolumeBitmap map = Bits((false, 100), (true, 1000), (false, 100));

        Assert.Equal(600, DiskProbe.FindReference(map, 600, 64));
        Assert.Equal(1036, DiskProbe.FindReference(map, 5000, 64)); // 1100 - 64
        Assert.Equal(100, DiskProbe.FindReference(map, 0, 64));
    }

    [Fact]
    public void MedianAndRate()
    {
        Assert.Equal(2, DiskProbe.Median([3, 1, 2]));
        Assert.Equal(100, DiskProbe.Rate(100_000_000, TimeSpan.FromSeconds(1)), 6);
    }
}
