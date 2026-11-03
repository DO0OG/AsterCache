using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 물리 디스크 활동 판정의 순수 부분: 인스턴스 이름 매핑·합산·캐시. 실제 PDH 는 부르지 않습니다.
public sealed class DiskActivityTests
{
    private const double Mib = 1 << 20;

    private static readonly Dictionary<string, double> Instances = new()
    {
        ["0 C: D:"] = 80 * Mib,
        ["1 E:"] = 0,
        ["_Total"] = 80 * Mib,
    };

    private static bool Busy(string letter, IReadOnlyDictionary<string, double> instances) =>
        DiskActivity.Sum(letter, instances) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond;

    [Fact]
    public void OtherVolumeActivityOnTheSameDiskHoldsBackTheIdleVolume()
    {
        Assert.True(Busy("D:", Instances)); // C: 가 읽는 중이면 같은 디스크의 D: 도 바쁨
        Assert.True(Busy("C:", Instances));
        Assert.False(Busy("E:", Instances)); // 다른 디스크는 진행
    }

    [Fact]
    public void UnmappedVolumeCountsAsBusy()
    {
        Assert.Null(DiskActivity.Sum("Z:", Instances));
        Assert.True(Busy("Z:", Instances));
        Assert.True(Busy("D:", new Dictionary<string, double> { ["0"] = 0, ["_Total"] = 0 })); // 문자 없는 인스턴스만 있으면 모름
    }

    [Fact]
    public void SpannedVolumeSumsEveryDisk()
    {
        var spanned = new Dictionary<string, double> { ["1 F:"] = 3 * Mib, ["2 F:"] = 3 * Mib, ["3 G:"] = 0 };
        Assert.Equal(6 * Mib, DiskActivity.Sum("f:", spanned));
        Assert.True(Busy("F:", spanned)); // 각각 4 MiB/s 미만이어도 합이 넘으면 바쁨
        Assert.False(Busy("G:", spanned));
    }

    [Fact]
    public void SampleIsCachedForTwoSecondsThenRetaken()
    {
        long now = 0;
        int taken = 0;
        var sampler = new DiskActivity.PhysicalSampler(() => { taken++; return Instances; }, () => now);
        Assert.Equal(80 * Mib, sampler.BytesPerSecond("C:"));
        Assert.Equal(0, sampler.BytesPerSecond("E:"));
        now = 1999;
        sampler.BytesPerSecond("D:");
        Assert.Equal(1, taken);
        now = 2000;
        sampler.BytesPerSecond("D:");
        Assert.Equal(2, taken);
    }

    [Fact]
    public void FailedSampleMeansUnknown()
    {
        var sampler = new DiskActivity.PhysicalSampler(() => null, () => 0);
        Assert.Null(sampler.BytesPerSecond("D:"));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1.0)]
    public void InvalidValueMakesTheVolumeUnknownAndSaysSo(double value)
    {
        var bad = new Dictionary<string, double> { ["0 D:"] = value, ["1 E:"] = 0 };
        Assert.Null(DiskActivity.Sum("D:", bad, out string? reason));
        Assert.Equal(DiskActivity.ValueInvalid, reason);
        Assert.True(Busy("D:", bad));
    }

    [Fact]
    public void OnlyAnUnnumberedInstanceIsUnmapped()
    {
        Assert.Null(DiskActivity.Sum("D:", new Dictionary<string, double> { ["0"] = 0 }, out string? reason));
        Assert.Equal(DiskActivity.InstanceUnmapped, reason);
    }
}
