using System.ComponentModel;
using System.Runtime.InteropServices;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 속성 필터, 볼륨 라벨, ChangeTime, 파일 매핑, 배치 조회 (tests/test_relayout.py SafetyHelperTests, tests/test_prefetch.py IoTests 일부).
public sealed class FileHelpersTests
{
    [Fact]
    public void IsSparseReadsOnlyTheSparseBitAndTreatsUnreadableAttributesAsNotSparse()
    {
        Assert.True(FileGuards.IsSparse("x", _ => 0x20 | FileGuards.SparseAttribute));
        Assert.False(FileGuards.IsSparse("x", _ => 0x20 | 0x800 | 0x400));
        Assert.False(FileGuards.IsSparse("x", _ => throw new IOException("nope")));
        Assert.False(FileGuards.IsSparse(@"C:\no\such\file"));
    }

    [Fact]
    public void SkipReasonFlags()
    {
        Assert.Null(FileGuards.SkipReason("x", _ => 0x20));
        Assert.Equal("encrypted", FileGuards.SkipReason("x", _ => 0x20 | 0x4000));
        Assert.Equal("sparse", FileGuards.SkipReason("x", _ => 0x200));
        Assert.Equal("directory", FileGuards.SkipReason("x", _ => 0x10 | 0x800)); // Python 딕셔너리 순서가 우선순위
        Assert.StartsWith("attrs_unreadable", FileGuards.SkipReason("x", _ => throw new IOException("nope")));
    }

    [Fact]
    public void FileAttributesReadsRealFilesAndThrowsForMissing()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", 10);

        Assert.Null(FileGuards.SkipReason(tmp.Combine("f.bin")));
        Assert.Equal("directory", FileGuards.SkipReason(tmp.Path));
        Assert.Equal(2, Assert.Throws<Win32Exception>(() => FileGuards.FileAttributes(tmp.Combine("missing"))).NativeErrorCode);
        Assert.StartsWith("attrs_unreadable", FileGuards.SkipReason(tmp.Combine("missing")));
    }

    [Fact]
    public void VolumeLabelMatchesDriveInfo()
    {
        string root = Path.GetPathRoot(Environment.SystemDirectory)!;
        Assert.Equal(new DriveInfo(root).VolumeLabel, VolumeInfo.Label(root[..2]));
    }

    [Fact]
    public void ChangeTimeIsPlausibleAndMovesWithWrites()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", 100);
        string p = tmp.Combine("f.bin");
        long first;
        using (Microsoft.Win32.SafeHandles.SafeFileHandle h = File.OpenHandle(p))
        {
            first = FileTimes.ChangeTime(h);
        }

        Assert.True(Math.Abs(first - DateTime.UtcNow.ToFileTimeUtc()) < TimeSpan.FromDays(1).Ticks);

        long second = first;
        for (int attempt = 0; attempt < 50 && second == first; attempt++)
        {
            Thread.Sleep(20);
            File.AppendAllText(p, "x");
            using Microsoft.Win32.SafeHandles.SafeFileHandle h = File.OpenHandle(p);
            second = FileTimes.ChangeTime(h);
        }

        Assert.True(second > first);
    }

    [Fact]
    public void FileMappingExposesTheFileBytes()
    {
        using var tmp = new TempDir();
        byte[] data = tmp.WriteRandom("f.bin", 100_000);

        using FileMapping map = FileMapping.Open(tmp.Combine("f.bin"));
        byte[] seen = new byte[data.Length];
        Marshal.Copy(map.BaseAddress, seen, 0, seen.Length);

        Assert.Equal(data, seen);
        Assert.Throws<Win32Exception>(() => FileMapping.Open(tmp.Combine("missing.bin")));
    }

    [Fact]
    public void FileExtentsReturnsListOrNullAndNeverThrows()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", 3 << 20);
        string p = tmp.Combine("f.bin");

        var ext = FileLayout.Extents(p);

        Assert.True(ext is null || ext.All(e => e.Clusters > 0));
        if (ext is { Count: > 0 })
        {
            Assert.True(FileLayout.ClusterBytes(p) > 0);
            Assert.NotNull(FileLayout.Get(p));
        }

        Assert.Null(FileLayout.Extents(tmp.Combine("missing.bin")));
        Assert.Null(FileLayout.Get(tmp.Combine("missing.bin")));
        Assert.Null(FileLayout.Extents(string.Empty));
    }

    [Fact]
    public void FileExtentsCoverTheWholeFileInVcnOrder()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", 3 << 20);
        string p = tmp.Combine("f.bin");
        // 방금 쓴 파일은 아직 할당 전일 수 있어 한 번 밀어 냅니다.
        using (var fs = new FileStream(p, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Flush(true);
        }

        var lay = FileLayout.Get(p);

        if (!string.Equals(new DriveInfo(Path.GetPathRoot(p)!).DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Null(lay); // NTFS 가 아니면 배치를 줄 수 없음
            return;
        }

        Assert.NotNull(lay);

        long vcn = 0;
        foreach (var e in lay!.Extents)
        {
            Assert.Equal(vcn, e.Vcn);
            vcn += e.Clusters;
        }

        Assert.Equal((3L << 20) / lay.ClusterBytes, vcn);
    }
}
