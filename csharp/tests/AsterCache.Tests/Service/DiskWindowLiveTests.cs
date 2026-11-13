using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 실제 PDH 호출: 호출자 스레드에서 멈춘 구간만 직접 재어 값과 상태가 나오는지. 관리자 권한은 필요 없습니다.
[Trait("Category", "Live")]
public sealed class DiskWindowLiveTests
{
    [Fact]
    public void MeasureWindowWaitsForSettleThenWindowAndReturnsAnOkSampleOfTheSystemDrive()
    {
        var order = new List<string>();
        string drive = Path.GetPathRoot(Environment.SystemDirectory)![..2];

        (double? bps, DiskActivity.SampleState state) = DiskActivity.MeasureWindow(drive, () => order.Add("settle"), () =>
        {
            order.Add("window");
            Thread.Sleep(300);
        });

        Assert.Equal(["settle", "window"], order);
        Assert.Equal(DiskActivity.SampleState.Ok, state);
        Assert.True(bps >= 0);
    }
}
