using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 드라이브별 자동 모드 허용 범위: 시스템 HDD 는 발견·예열만, 시스템 SSD 는 전부 거절, 시스템 밖 HDD 는 전부 허용.
public sealed class DriveCapabilitiesTests
{
    private static DriveCapabilities Of(string root, string system, bool? hdd) => DriveCapabilities.Of(root, system, _ => hdd);

    [Fact]
    public void SystemHddAllowsDiscoveryAndPrewarmOnly()
    {
        DriveCapabilities c = Of(@"C:\Games\G", "C:", true);

        Assert.Equal((true, true, false, false, true, true), (c.CanDiscover, c.CanPrewarm, c.CanRelocate, c.CanTier, c.IsSystem, c.IsRotational));
        Assert.Equal(DriveCapabilities.PrewarmOnly, c.Reason);
        Assert.Null(c.PrewarmRefusal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void SystemSsdOrUnknownRefusesEverything(bool? hdd)
    {
        DriveCapabilities c = Of(@"C:\Games\G", "c:", hdd);

        Assert.Equal((false, false, false, false, true), (c.CanDiscover, c.CanPrewarm, c.CanRelocate, c.CanTier, c.IsSystem));
        Assert.Equal("system volume", c.PrewarmRefusal);
    }

    [Fact]
    public void HddOutsideTheSystemVolumeAllowsEverything()
    {
        DriveCapabilities c = Of(@"D:\Games\G", "C:", true);

        Assert.Equal((true, true, true, true, false, true), (c.CanDiscover, c.CanPrewarm, c.CanRelocate, c.CanTier, c.IsSystem, c.IsRotational));
        Assert.Null(c.Reason);
    }

    [Theory]
    [InlineData(false, "not an HDD")]
    [InlineData(null, "drive type unknown")]
    public void SsdOrUnknownOutsideTheSystemVolumeIsRefused(bool? hdd, string reason)
    {
        DriveCapabilities c = Of(@"D:\Games\G", "C:", hdd);

        Assert.False(c.CanPrewarm || c.CanRelocate || c.CanTier || c.CanDiscover);
        Assert.Equal(reason, c.PrewarmRefusal);
    }
}
