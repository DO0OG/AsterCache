using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 디스패처 대기열의 게임별 시작 수: 맨 앞이 아니라 전체에서 다른 게임의 시작을 가려냅니다.
public sealed class QueuedStartsTests
{
    [Fact]
    public void AnotherGameBehindOtherEntriesIsFoundAnywhereInTheQueue()
    {
        var queued = new QueuedStarts();
        queued.Add("cod");
        queued.Add("cod");
        queued.Add("COD");
        Assert.False(queued.AnyOther("cod")); // 같은 게임의 시작은 (대소문자 무시) 세지 않음: 다음 단계 프로세스는 표본 루프가 pid 로 가려냄

        queued.Add("other"); // 맨 앞이 아닌 뒤쪽의 다른 게임
        Assert.True(queued.AnyOther("cod"));
    }

    [Fact]
    public void RemovingTheDequeuedStartClearsItAndNeverGoesBelowZero()
    {
        var queued = new QueuedStarts();
        queued.Add("other");
        queued.Remove("other");
        Assert.False(queued.AnyOther("cod"));

        queued.Remove("other"); // 설정이 바뀌어 짝이 안 맞아도 음수가 되지 않음
        queued.Add("other");
        Assert.True(queued.AnyOther("cod"));
    }
}
