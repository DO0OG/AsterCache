using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Tracer;

public sealed class DevicePathsTests
{
    private static readonly DevicePaths Fake = new(
    [
        new KeyValuePair<string, string>(@"\Device\HarddiskVolume3", "D:"),
        new KeyValuePair<string, string>(@"\Device\HarddiskVolume31", "E:"),
        new KeyValuePair<string, string>(@"\Device\HarddiskVolume2", "C:"),
    ]);

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3\Games\a.bin", @"D:\Games\a.bin")]
    [InlineData(@"\Device\HarddiskVolume31\x.bin", @"E:\x.bin")]       // Volume3 접두가 Volume31 을 가로채면 안 됨
    [InlineData(@"\device\harddiskvolume2\Windows", @"C:\Windows")]    // 대소문자 무시
    [InlineData(@"\Device\HarddiskVolume3", "D:")]                      // 장치 이름만
    [InlineData(@"\Device\HarddiskVolume4\x", @"\Device\HarddiskVolume4\x")] // 모르는 볼륨은 그대로
    [InlineData(@"\Device\HarddiskVolume30\x", @"\Device\HarddiskVolume30\x")]
    [InlineData(@"\Device\Mup\srv\share\a", @"\Device\Mup\srv\share\a")]
    [InlineData(@"D:\Games\a.bin", @"D:\Games\a.bin")]
    public void ToDosPathReplacesOnlyWholeDeviceComponent(string nt, string expected)
    {
        Assert.Equal(expected, Fake.ToDosPath(nt));
    }

    [Fact]
    public void FromSystemKeepsOnlyDeviceTargets()
    {
        string? Query(string name) => name switch
        {
            "C:" => @"\Device\HarddiskVolume2",
            "S:" => @"\??\C:\subst",   // subst 드라이브는 장치 경로가 아니므로 제외
            _ => null,
        };

        var map = DevicePaths.FromSystem(Query);

        Assert.Equal([(@"\Device\HarddiskVolume2", "C:")], map.Map);
    }

    /// 이 PC 의 실제 QueryDosDeviceW 결과 (D:\AsterBench 가 있는 PC 에서만 의미가 있어 Live).
    [Trait("Category", "Live")]
    [Fact]
    public void SystemMapCoversRealCAndDDrives()
    {
        var map = DevicePaths.FromSystem();

        foreach (string drive in new[] { "C:", "D:" })
        {
            string device = Assert.Single(map.Map, m => m.Drive == drive).Device;
            Assert.StartsWith(@"\Device\", device);
            Assert.Equal(drive + @"\", map.ToDosPath(device + @"\"));
            Assert.Equal(drive + @"\Windows", map.ToDosPath(device + @"\Windows"));
        }

        foreach (DriveInfo di in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            Assert.Contains(map.Map, m => m.Drive.Equals(di.Name[..2], StringComparison.OrdinalIgnoreCase));
        }
    }
}
