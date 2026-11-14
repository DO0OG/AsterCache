using System.Runtime.InteropServices;
using AsterCache.Prefetch;
using AsterCache.Tests.Windows;
using AsterCache.Windows;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AsterCache.Tests.Prefetch;

/// 실제 스파스 속성 임시 파일: 두 범위를 서로 다른 Run (묶음) 으로 차례로 읽은 뒤 첫 범위가 파일 캐시에 남아 있는지를 시스템 페이지 읽기 누계로 확인합니다.
/// 비버퍼로 써서 캐시에 없는 상태로 시작하므로 SSD 임시 폴더에서도 판정됩니다 (시간이 아니라 디스크에서 읽힌 쪽 수를 봄). 게임 파일은 건드리지 않습니다.
[Trait("Category", "Live")]
public sealed class ZoneStreamerSparseLiveTests
{
    private const long Mib = 1 << 20;
    private const long Page = 4096;
    private const long RangeBytes = 16 * Mib;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);

    // 임시 폴더의 시험 파일에만: 스파스 속성을 주고 FILE_FLAG_NO_BUFFERING 으로 써서 파일 캐시에 올리지 않음.
    private static string ColdSparseFile(TempDir dir, long length)
    {
        string path = dir.Combine("cold-sparse.bin");
        using (SafeFileHandle h = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(DeviceIoControl(h, 0x900C4, 0, 0, 0, 0, out _, 0));
        }

        byte[] pinned = GC.AllocateArray<byte>((int)Mib + (int)Page, pinned: true);
        int skew = (int)((Page - ((long)Marshal.UnsafeAddrOfPinnedArrayElement(pinned, 0) % Page)) % Page);
        Random.Shared.NextBytes(pinned);
        using SafeFileHandle w = File.OpenHandle(path, FileMode.Open, FileAccess.Write, FileShare.None, (FileOptions)0x20000000 | FileOptions.WriteThrough);
        for (long at = 0; at < length; at += Mib)
        {
            RandomAccess.Write(w, pinned.AsSpan(skew, (int)Mib), at);
        }

        return path;
    }

    private static uint PagesReadWhileReading(string path, long offset, long length)
    {
        uint before = SystemPageReads.Query()!.Value;
        using SafeFileHandle h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] buf = new byte[Mib];
        for (long at = offset; at < offset + length; at += Mib)
        {
            _ = RandomAccess.Read(h, buf, at);
        }

        return SystemPageReads.Query()!.Value - before;
    }

    [Fact]
    public void AnEarlierRangeOfARealSparseFileStaysInTheCacheAfterLaterRangesAreReadByOtherRuns()
    {
        using var dir = new TempDir();
        string path = ColdSparseFile(dir, 3 * RangeBytes);
        Assert.True(FileGuards.IsSparse(path));

        StreamStats r1 = ZoneStreamer.Run([new(path, 0, RangeBytes)], StreamMethod.Pvm);
        StreamStats r2 = ZoneStreamer.Run([new(path, RangeBytes, RangeBytes)], StreamMethod.Pvm);
        StreamStats r3 = ZoneStreamer.Run([new(path, 2 * RangeBytes, RangeBytes)], StreamMethod.Pvm);
        uint pages = PagesReadWhileReading(path, 0, RangeBytes); // 첫 범위: 캐시에 남았으면 디스크에서 거의 읽지 않음

        Assert.All(new[] { r1, r2, r3 }, r => Assert.Equal((RangeBytes, RangeBytes, 0L, 0L), (r.Bytes, r.BufferedBytes, r.HintAcceptedBytes, r.Errors)));
        Assert.True(pages < RangeBytes / Page / 2, $"first range was read again from the disk ({pages} pages of {RangeBytes / Page})");
    }
}
