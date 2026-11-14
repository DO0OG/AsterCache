using System.Runtime.InteropServices;
using AsterCache.Core.Prefetch;
using AsterCache.Windows;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 실제 Windows 호출: 임시 파일을 읽어 캐시에 올린 뒤 범위를 만져 바이트 수와 끝난 사유를 확인합니다 (할당 단위에 맞지 않는 시작 위치 포함). 관리자 권한은 필요 없습니다.
[Trait("Category", "Live")]
public sealed class StandbyTouchLiveTests
{
    private const int Mib = 1 << 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);

    // FSCTL_SET_SPARSE: 시험 임시 폴더의 작은 파일에만 씀 (게임 파일의 속성은 건드리지 않음)
    private static void MarkSparse(string path)
    {
        using SafeFileHandle h = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(DeviceIoControl(h, 0x900C4, 0, 0, 0, 0, out _, 0));
    }

    [Fact]
    public void TouchesTwoRangesOfACachedFileAndReportsTheBytesAndCompletion()
    {
        using var dir = new TempDir();
        _ = dir.WriteRandom("t.bin", 6 * Mib);
        _ = File.ReadAllBytes(dir.Combine("t.bin")); // 캐시에 올림

        TouchResult r = StandbyTouch.Run([new ReadOp(dir.Combine("t.bin"), 3 * Mib + 4096, 2 * Mib + 7), new ReadOp(dir.Combine("t.bin"), 0, Mib)], CancellationToken.None);

        Assert.Equal(TouchEnd.Completed, r.End);
        Assert.Equal(3L * Mib + 7, r.Bytes);
        Assert.Equal(0, r.Errors);
        Assert.Equal(0, r.SlowPieces);
    }

    [Fact]
    public void AMissingFileCountsAsAnErrorAndTheRestStillRuns()
    {
        using var dir = new TempDir();
        _ = dir.WriteRandom("t.bin", Mib);

        TouchResult r = StandbyTouch.Run([new ReadOp(dir.Combine("none.bin"), 0, Mib), new ReadOp(dir.Combine("t.bin"), 0, 8 * Mib)], CancellationToken.None); // 길이는 파일 끝에서 잘림

        Assert.Equal(TouchEnd.Completed, r.End);
        Assert.Equal(1, r.Errors);
        Assert.Equal(Mib, r.Bytes);
    }

    [Fact]
    public void ARealSparseFileIsSkippedWithoutBeingTouchedWhileAnOrdinaryFileIsTouched()
    {
        using var dir = new TempDir();
        _ = dir.WriteRandom("sparse.bin", 4 * Mib);
        _ = dir.WriteRandom("t.bin", 2 * Mib);
        MarkSparse(dir.Combine("sparse.bin"));
        _ = File.ReadAllBytes(dir.Combine("t.bin"));

        TouchResult r = StandbyTouch.Run([new ReadOp(dir.Combine("sparse.bin"), 0, 4 * Mib), new ReadOp(dir.Combine("t.bin"), 0, 2 * Mib)], CancellationToken.None);

        Assert.True((FileGuards.FileAttributes(dir.Combine("sparse.bin")) & FileGuards.SparseAttribute) != 0);
        Assert.Equal(TouchEnd.Completed, r.End);
        Assert.Equal(2L * Mib, r.Bytes);
        Assert.Equal(4L * Mib, r.SkippedBytes);
        Assert.Equal(0, r.Errors);
    }

    [Fact]
    public void ACanceledTokenStopsBeforeTouchingAnything()
    {
        using var dir = new TempDir();
        _ = dir.WriteRandom("t.bin", Mib);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        TouchResult r = StandbyTouch.Run([new ReadOp(dir.Combine("t.bin"), 0, Mib)], cts.Token);

        Assert.Equal(TouchEnd.Canceled, r.End);
        Assert.Equal(0, r.Bytes);
    }

    [Fact]
    public void TheSystemPageReadCountCanBeReadAndOnlyGrows()
    {
        using var dir = new TempDir();
        _ = dir.WriteRandom("t.bin", 8 * Mib);

        uint? before = SystemPageReads.Query();
        _ = File.ReadAllBytes(dir.Combine("t.bin"));
        uint? after = SystemPageReads.Query();

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.True(unchecked(after.Value - before.Value) < 1u << 30); // 줄지 않음 (넘쳐도 부호 없는 차이는 작음)
    }
}
