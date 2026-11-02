using AsterCache.Windows;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AsterCache.Tests.Windows;

public sealed class IoPriorityHintTests
{
    [Fact]
    public void LowHintIsAcceptedOnARegularFileHandle()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", 4096);
        using SafeFileHandle h = File.OpenHandle(tmp.Combine("f.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var errors = new List<int>();

        Assert.True(new IoPriorityHint(errors.Add).SetLow(h));
        Assert.Empty(errors);
    }

    [Fact]
    public void FailureIsReportedOnceAndNeverThrows()
    {
        var errors = new List<int>();
        var hint = new IoPriorityHint(errors.Add);
        using var invalid = new SafeFileHandle(nint.Zero, false);

        Assert.False(hint.SetLow(invalid));
        Assert.False(hint.SetLow(invalid));

        Assert.Single(errors);
        Assert.NotEqual(0, errors[0]);
    }

    [Fact]
    public void SampleDigestIsTheSameWithTheHintOnBothReadPaths()
    {
        using var tmp = new TempDir();
        byte[] data = tmp.WriteRandom("f.bin", (3 * (1 << 20)) + 777);
        string p = tmp.Combine("f.bin");
        var errors = new List<int>();
        var hint = new IoPriorityHint(errors.Add);

        string plain = SampleDigest.Compute(p, data.Length);

        Assert.Equal(plain, SampleDigest.Compute(p, data.Length, hint: hint)); // 언버퍼드 읽기
        Assert.Equal(plain, SampleDigest.Compute(p, data.Length, unbuffered: false, hint: hint)); // 버퍼드 읽기
        Assert.Empty(errors);
    }

    [Fact]
    public void VolumeAndMovedFileHandlesBothGetTheHintWithoutChangingTheRequest()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", 4096);
        var errors = new List<int>();
        using SafeFileHandle stand = File.OpenHandle(tmp.Combine("f.bin"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); // 볼륨 핸들 대역
        using var v = new FakeVolume(stand, "X:");

        v.UseLowIoPriority(new IoPriorityHint(errors.Add));
        v.Move(tmp.Combine("f.bin"), 1, 2, 3);

        Assert.Empty(errors);
        Assert.Equal(1, v.Calls);
    }

    [Fact]
    public void VolumeWithoutAHandleIsSkippedQuietly()
    {
        var errors = new List<int>();
        using var v = new FakeVolume(new SafeFileHandle(nint.Zero, false), "X:");

        v.UseLowIoPriority(new IoPriorityHint(errors.Add));

        Assert.Empty(errors);
    }

    private sealed class FakeVolume(SafeFileHandle handle, string letter) : NtfsVolume(handle, letter)
    {
        public int Calls { get; private set; }

        protected override (bool Ok, int Error, byte[] Output) Ioctl(uint code, ReadOnlySpan<byte> input, int outputLength)
        {
            Calls++;
            return (true, 0, []);
        }
    }
}
