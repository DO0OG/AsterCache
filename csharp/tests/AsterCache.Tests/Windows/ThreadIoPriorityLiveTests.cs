using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 실제 Windows 호출: 스레드의 I/O 우선순위를 낮음으로 바꾸고 조회해 확인한 뒤 보통으로 되돌립니다. 관리자 권한은 필요 없습니다.
/// 시험 스레드 풀에 낮은 우선순위가 남지 않게 전용 스레드에서 돌립니다.
[Trait("Category", "Live")]
public sealed class ThreadIoPriorityLiveTests
{
    [Fact]
    public void LowCanBeSetReadBackAndRestoredToNormalOnTheCurrentThread()
    {
        (IoPriority? Before, bool Lowered, IoPriority? Low, bool Restored, IoPriority? After) result = default;
        var thread = new Thread(() =>
        {
            IoPriority? before = ThreadIoPriority.Get();
            bool lowered = ThreadIoPriority.TrySet(IoPriority.Low);
            IoPriority? low = ThreadIoPriority.Get();
            bool restored = ThreadIoPriority.TrySet(IoPriority.Normal);
            result = (before, lowered, low, restored, ThreadIoPriority.Get());
        });
        thread.Start();
        thread.Join();

        Assert.Equal(IoPriority.Normal, result.Before);
        Assert.True(result.Lowered);
        Assert.Equal(IoPriority.Low, result.Low);
        Assert.True(result.Restored);
        Assert.Equal(IoPriority.Normal, result.After);
    }
}
