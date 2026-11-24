using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 성능 카운터에서 물리 디스크의 순간 대기열을 읽는 질의: 이 PC 에 카운터가 있으면 같은 질의 개체를 되풀이해 읽을 수 있어야 하고, 없으면 열지 못하고 끝납니다.
public sealed class DiskQueueLengthTests
{
    [Fact]
    public void AnOpenQueryReadsTheInstantQueueOfTheSystemDriveRepeatedlyAndIsNotNegative()
    {
        using DiskActivity.DiskQueueLength? query = DiskActivity.DiskQueueLength.Open();
        if (query is null)
        {
            return; // 카운터가 없는 환경
        }

        string drive = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd(Path.DirectorySeparatorChar);
        for (int i = 0; i < 3; i++)
        {
            long? queue = query.Read(drive);
            Assert.True(queue is null or >= 0);
            Assert.NotNull(queue); // 시스템 드라이브는 물리 디스크 인스턴스 이름에 문자가 붙어 있음
        }
    }

    [Fact]
    public void AnUnknownDriveHasNoQueueValue()
    {
        using DiskActivity.DiskQueueLength? query = DiskActivity.DiskQueueLength.Open();

        Assert.Null(query?.Read("Q:"));
    }
}
