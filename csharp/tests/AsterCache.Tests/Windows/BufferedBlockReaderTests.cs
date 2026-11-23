using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 스파스 블록 유지가 쓰는 버퍼드 읽기 도구: 실제 임시 파일을 읽고, 열 수 없는 파일은 null 이며, 들고 있는 동안에도 게임의 쓰기·삭제를 막지 않는지 확인합니다.
public sealed class BufferedBlockReaderTests
{
    [Fact]
    public void ReadsABlockAndAReadPastTheEndOfTheFileAndReturnsTheElapsedSeconds()
    {
        using var dir = new TempDir();
        _ = dir.WriteRandom("a.bin", 200_000);
        using var reader = new BufferedBlockReader();

        double? first = reader.Read(dir.Combine("a.bin"), 0, 65536);
        double? last = reader.Read(dir.Combine("a.bin"), 196608, 65536); // 파일 끝에서 잘림
        double? beyond = reader.Read(dir.Combine("a.bin"), 1 << 20, 65536); // 끝 밖: 0 바이트

        Assert.True(first >= 0);
        Assert.True(last >= 0);
        Assert.True(beyond >= 0);
    }

    [Fact]
    public void AFileThatCannotBeOpenedReturnsNullAndIsNotOpenedAgain()
    {
        using var dir = new TempDir();
        using var reader = new BufferedBlockReader();

        Assert.Null(reader.Read(dir.Combine("missing.bin"), 0, 65536));
        _ = dir.WriteRandom("missing.bin", 1000); // 그 사이 생겨도 이 도구가 닫히기 전까지는 다시 열지 않음
        Assert.Null(reader.Read(dir.Combine("missing.bin"), 0, 65536));
    }

    [Fact]
    public void HoldingTheFileOpenDoesNotStopTheGameFromWritingToItOrDeletingIt()
    {
        using var dir = new TempDir();
        _ = dir.WriteRandom("a.bin", 100_000);
        using var reader = new BufferedBlockReader();
        Assert.NotNull(reader.Read(dir.Combine("a.bin"), 0, 65536));

        File.AppendAllText(dir.Combine("a.bin"), "more"); // 쓰기 공유
        File.Delete(dir.Combine("a.bin")); // 삭제 공유 (오류 없이 끝나야 함)
    }

    [Fact]
    public void ALengthOutsideTheReuseBufferIsRejected()
    {
        using var reader = new BufferedBlockReader();

        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(@"C:\x.bin", 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.Read(@"C:\x.bin", 0, BufferedBlockReader.MaxReadBytes + 1));
    }
}
