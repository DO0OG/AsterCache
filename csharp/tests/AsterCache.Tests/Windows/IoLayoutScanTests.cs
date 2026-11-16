using System.Buffers.Binary;
using System.Runtime.InteropServices;
using AsterCache.Core.Model;
using AsterCache.Core.Trace;
using AsterCache.Windows;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 끝난 뒤 변환의 Windows 쪽: 할당 구간 조회와 볼륨 위치 해석, 실제 임시 파일의 배치로 요청을 (파일, 블록) 에 잇기. 관리자 권한은 필요 없고 파일 데이터는 쓰기만 합니다.
/// NTFS 가 아니거나 볼륨 위치를 못 읽는 환경에서는 배치를 줄 수 없으므로 (이웃 FileHelpersTests 와 같이) 그 사실만 확인하고 끝냅니다.
public sealed class IoLayoutScanTests
{
    private const int Mib = 1 << 20;

    private static byte[] DiskExtentBuffer(params (uint Disk, long Start, long Length)[] extents)
    {
        byte[] buffer = new byte[8 + (extents.Length * 24)];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)extents.Length);
        for (int i = 0; i < extents.Length; i++)
        {
            Span<byte> item = buffer.AsSpan(8 + (i * 24));
            BinaryPrimitives.WriteUInt32LittleEndian(item, extents[i].Disk);
            BinaryPrimitives.WriteInt64LittleEndian(item[8..], extents[i].Start);
            BinaryPrimitives.WriteInt64LittleEndian(item[16..], extents[i].Length);
        }

        return buffer;
    }

    private static string WriteFlushed(TempDir dir, string name, int length)
    {
        dir.WriteRandom(name, length);
        string path = dir.Combine(name);
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Flush(true); // 방금 쓴 파일은 아직 할당 전일 수 있어 한 번 밀어 냄
        }

        return path;
    }

    private static IoRequestSnapshot Snapshot(DateTimeOffset observed, params IoRequest[] requests) => new(
        observed, observed.AddSeconds(5), [], 0, 0, new IoClassTotals(requests.Sum(r => r.Count), requests.Sum(r => r.Bytes), requests.Sum(r => r.LatencySumUs)),
        0, default, default, default, 0, 0, requests);

    [Fact]
    public void DiskExtentsParseTheDiskStartAndLengthOfEveryExtent()
    {
        IReadOnlyList<DiskExtent>? extents = VolumeDisks.ParseExtents(DiskExtentBuffer((2, 16_777_216, 1L << 40), (5, 4096, 8192)));

        Assert.Equal([new DiskExtent(2, 16_777_216, 1L << 40), new DiskExtent(5, 4096, 8192)], extents);
        Assert.Null(VolumeDisks.ParseExtents(DiskExtentBuffer()));
        Assert.Null(VolumeDisks.ParseExtents(DiskExtentBuffer((1, 0, 1), (2, 0, 1))[..40])); // 잘림
        Assert.Equal(new HashSet<uint> { 2, 5 }, VolumeDisks.Parse(DiskExtentBuffer((2, 0, 1), (5, 0, 1))));
    }

    [Theory]
    [InlineData("NTFS", true)]
    [InlineData("ntfs", true)]
    [InlineData("exFAT", false)]
    [InlineData("FAT32", false)]
    [InlineData("ReFS", false)]
    [InlineData("", false)] // 이름을 읽지 못함
    public void OnlyNtfsVolumesAreConverted(string name, bool converts) => Assert.Equal(converts, IoLayoutScan.ConvertsFileSystem(name));

    [Fact]
    public void TheFileSystemNameOfARealVolumeIsReadAndAnUnknownDriveGivesAnEmptyName()
    {
        string drive = Path.GetPathRoot(Path.GetTempPath())![..2];

        Assert.NotEmpty(VolumeInfo.FileSystem(drive));
        Assert.Empty(VolumeInfo.FileSystem(@"?:"));
    }

    // 임시 폴더에 정션을 만듭니다 (관리자 불필요). 만들 수 없는 환경이면 false.
    private static bool TryMakeJunction(string link, string target)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
            process!.WaitForExit();
            return process.ExitCode == 0 && Directory.Exists(link);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    [Fact]
    public void ARootOrAnAncestorThatIsAJunctionIsNotConvertedAndCountsAsASkippedVolume()
    {
        using var dir = new TempDir();
        string real = dir.Combine("real");
        Directory.CreateDirectory(Path.Combine(real, "game"));
        WriteFlushed(dir, @"real\game\f.bin", Mib);
        string link = dir.Combine("link");
        if (!TryMakeJunction(link, real))
        {
            return; // 정션을 만들 수 없는 환경
        }

        try
        {
            Assert.False(IoLayoutScan.HasReparsePoint(real));
            Assert.True(IoLayoutScan.HasReparsePoint(link));
            Assert.True(IoLayoutScan.HasReparsePoint(Path.Combine(link, "game"))); // 조상이 정션

            var request = new IoRequest(0, 0, 4096, 1, 100, 100, 0, 0);
            IoSummary viaJunction = IoLayoutScan.Build(Snapshot(DateTimeOffset.UtcNow.AddMinutes(1), request), [Path.Combine(link, "game")], CancellationToken.None);

            Assert.Equal(1, viaJunction.Quality.VolumesSkipped);
            Assert.Equal(0, viaJunction.Quality.FilesScanned);
        }
        finally
        {
            Directory.Delete(link, false); // 정션만 지움 (TempDir 의 재귀 삭제가 정션을 못 지워 실패하지 않게)
        }
    }

    [Fact]
    public void AllocatedExtentsEqualTheOrdinaryExtentsOfAFileWithoutHolesAndNeverThrow()
    {
        using var dir = new TempDir();
        string path = WriteFlushed(dir, "f.bin", 3 * Mib);

        IReadOnlyList<Extent>? all = FileLayout.AllocatedExtents(path);
        IReadOnlyList<Extent>? ordinary = FileLayout.Extents(path);

        Assert.Equal(ordinary, all); // 구멍이 없으면 두 조회가 같음 (NTFS 가 아니면 둘 다 null)
        Assert.Null(FileLayout.AllocatedExtents(dir.Combine("missing.bin")));
        Assert.Null(FileLayout.AllocatedExtents(string.Empty));
    }

    [Fact]
    public void ARequestInsideARealFileIsLinkedToItsBlocksAndNothingIsMarkedChanged()
    {
        using var dir = new TempDir();
        string path = WriteFlushed(dir, "g.bin", 3 * Mib);
        IReadOnlyList<Extent>? extents = FileLayout.AllocatedExtents(path);
        IReadOnlyList<DiskExtent>? disks = VolumeDisks.DiskExtentsOf(path);
        int? cluster = FileLayout.ClusterBytes(path);
        if (extents is not { Count: > 0 } || disks is not { Count: 1 } || cluster is not > 0)
        {
            return; // NTFS 가 아니거나 볼륨 위치를 읽을 수 없는 환경
        }

        long bytes = Math.Min(extents[0].Clusters * cluster.Value, 128 * 1024);
        long diskOffset = disks[0].StartingOffset + (extents[0].Lcn * cluster.Value);
        long fileOffset = extents[0].Vcn * cluster.Value;
        DateTimeOffset observed = DateTimeOffset.UtcNow.AddMinutes(1); // 파일은 관찰 시작 전에 쓰임
        var request = new IoRequest(disks[0].Disk, diskOffset, bytes, 1, 800, 800, 123, 1);

        IoSummary summary = IoLayoutScan.Build(Snapshot(observed, request), [dir.Path], CancellationToken.None);

        Assert.Equal(new IoClassTotals(1, bytes, 800), summary.Quality.Linked);
        IoSummaryFile file = Assert.Single(summary.Files);
        Assert.Equal(path, file.Path, StringComparer.OrdinalIgnoreCase);
        Assert.False(file.ChangedDuringSession);
        Assert.Equal(bytes, summary.Rows.Sum(row => row.Bytes));
        Assert.Equal(fileOffset / IoBlockMapper.BlockSizeBytes, summary.Rows.Min(row => row.BlockNumber));
        Assert.Equal(1, summary.Quality.FilesScanned);
        Assert.Equal(0, summary.Quality.VolumesSkipped);
    }

    [Fact]
    public void AFileWrittenAfterTheObservationStartedIsMarkedChangedAndStillConverted()
    {
        using var dir = new TempDir();
        string path = WriteFlushed(dir, "h.bin", Mib);
        IReadOnlyList<Extent>? extents = FileLayout.AllocatedExtents(path);
        IReadOnlyList<DiskExtent>? disks = VolumeDisks.DiskExtentsOf(path);
        int? cluster = FileLayout.ClusterBytes(path);
        if (extents is not { Count: > 0 } || disks is not { Count: 1 } || cluster is not > 0)
        {
            return;
        }

        var request = new IoRequest(disks[0].Disk, disks[0].StartingOffset + (extents[0].Lcn * cluster.Value), cluster.Value, 1, 100, 100, 0, 0);
        DateTimeOffset observed = DateTimeOffset.UtcNow.AddMinutes(-5); // 관찰이 시작된 뒤에 쓰인 파일

        IoSummary summary = IoLayoutScan.Build(Snapshot(observed, request), [dir.Path], CancellationToken.None);

        Assert.True(Assert.Single(summary.Files).ChangedDuringSession);
        Assert.Equal(1, summary.Quality.FilesChanged);
        Assert.Equal(cluster.Value, summary.Quality.Linked.Bytes);
    }

    [Fact]
    public void ARequestOutsideEveryFileOfTheRootIsCountedAsNoFileAndACancelledScanThrows()
    {
        using var dir = new TempDir();
        string path = WriteFlushed(dir, "i.bin", Mib);
        IReadOnlyList<DiskExtent>? disks = VolumeDisks.DiskExtentsOf(path);
        if (disks is not { Count: 1 })
        {
            return;
        }

        var request = new IoRequest(disks[0].Disk, disks[0].StartingOffset + 1, 4096, 1, 100, 100, 0, 0); // 볼륨 맨 앞 (부트 영역): 어떤 파일에도 안 듦
        DateTimeOffset observed = DateTimeOffset.UtcNow.AddMinutes(1);

        IoSummary summary = IoLayoutScan.Build(Snapshot(observed, request), [dir.Path], CancellationToken.None);

        Assert.Equal(new IoClassTotals(1, 4096, 100), summary.Quality.NoFile);
        Assert.Empty(summary.Rows);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => IoLayoutScan.Build(Snapshot(observed, request), [dir.Path], cancelled.Token));
    }
}

/// 구멍이 있는 스파스 파일 (실제 NTFS 가 필요해 `--filter "Category=Live"` 로 따로 돌립니다): 할당 구간 조회는 구멍을 건너뛰고, 기존 Extents 는 구멍이 있으면 null 입니다.
[Trait("Category", "Live")]
public sealed class IoLayoutScanLiveTests
{
    private const int Mib = 1 << 20;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, nint input, uint inputSize, nint output, uint outputSize, out uint returned, nint overlapped);

    [Fact]
    public void ASparseFileWithAHoleReturnsOnlyItsAllocatedExtentsAndTheOrdinaryQueryReturnsNull()
    {
        using var dir = new TempDir();
        string path = dir.Combine("sparse.bin");
        using (SafeFileHandle h = File.OpenHandle(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(DeviceIoControl(h, 0x900C4, 0, 0, 0, 0, out _, 0)); // FSCTL_SET_SPARSE: 임시 폴더의 시험 파일에만
            byte[] data = new byte[Mib];
            Random.Shared.NextBytes(data);
            RandomAccess.SetLength(h, 16L * Mib);
            RandomAccess.Write(h, data, 0);
            RandomAccess.Write(h, data, 15L * Mib);
            RandomAccess.FlushToDisk(h);
        }

        IReadOnlyList<Extent>? allocated = FileLayout.AllocatedExtents(path);
        int? cluster = FileLayout.ClusterBytes(path);

        Assert.NotNull(allocated);
        Assert.True(cluster > 0);
        long allocatedBytes = allocated.Sum(e => e.Clusters) * cluster!.Value;
        Assert.InRange(allocatedBytes, 2L * Mib, 4L * Mib); // 16 MiB 중 앞뒤 1 MiB 만 할당됨
        Assert.True(allocated[^1].Vcn * cluster.Value >= 15L * Mib); // 뒤쪽 조각은 파일 안 15 MiB 부터
        Assert.Null(FileLayout.Extents(path)); // 기존 조회의 의미는 그대로: 구멍이 있으면 null
    }
}
