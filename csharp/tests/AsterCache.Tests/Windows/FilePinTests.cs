using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 상주 고정: 매핑만 들고 있어 다른 핸들의 읽기·쓰기를 막지 않고, Dispose 하면 모두 풀림.
public sealed class FilePinTests
{
    [Fact]
    public void PinnedFilesStayOpenableForReadAndWriteAndAreDeletableAfterDispose()
    {
        using var tmp = new TempDir();
        byte[] a = tmp.WriteRandom("a.bin", 200_000);
        tmp.WriteRandom("b.bin", 100_000);
        string[] paths = [tmp.Combine("a.bin"), tmp.Combine("b.bin")];

        using (FilePin pin = FilePin.Open(paths))
        {
            Assert.Equal((2, 0), (pin.Count, pin.Skipped));
            foreach (string p in paths)
            {
                using var f = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
                Assert.True(f.Length > 0);
            }

            using var rw = new FileStream(paths[0], FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            Assert.Equal(a[0], rw.ReadByte());
            rw.Position = 0;
            rw.WriteByte((byte)(a[0] ^ 0xFF)); // 고정이 쓰기를 막지 않음
        }

        File.Delete(paths[0]);
        File.Delete(paths[1]);
        Assert.False(File.Exists(paths[0]) || File.Exists(paths[1]));
    }

    [Fact]
    public void FilesThatCannotBeOpenedAreSkippedAndCounted()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("ok.bin", 50_000);
        tmp.WriteRandom("locked.bin", 50_000);
        File.WriteAllBytes(tmp.Combine("empty.bin"), []);
        using var exclusive = new FileStream(tmp.Combine("locked.bin"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        using FilePin pin = FilePin.Open([tmp.Combine("ok.bin"), tmp.Combine("locked.bin"), tmp.Combine("nope.bin"), tmp.Combine("empty.bin"), tmp.Combine("OK.bin")]);

        Assert.Equal((1, 3), (pin.Count, pin.Skipped)); // 같은 파일의 다른 철자는 한 번만 셈
    }

    [Fact]
    public void EmptyListIsAnEmptyPin()
    {
        using FilePin pin = FilePin.Open([]);

        Assert.Equal((0, 0), (pin.Count, pin.Skipped));
    }
}
