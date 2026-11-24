using System.Diagnostics;
using System.IO.Enumeration;
using AsterCache.Core.Model;
using AsterCache.Core.Trace;

namespace AsterCache.Windows;

/// 게임이 끝난 뒤 집계 루트 아래 파일의 배치를 속성 조회로 읽어 디스크 읽기 요청을 (파일, 블록) 으로 바꿉니다 (IoBlockMapper 에 배치를 먹이는 쪽).
/// 하는 일은 파일 열기 (FILE_READ_ATTRIBUTES, 데이터는 읽지 않음) + FSCTL_GET_RETRIEVAL_POINTERS + 볼륨 속성 조회뿐이라 관리자 권한과 게임·런처 프로세스 접근이 필요 없습니다.
/// 심볼릭 링크·정션·재해석 지점 파일은 건너뜁니다 (밖으로 새거나 배치가 다른 저장소의 것일 수 있음): 그 위치의 읽기는 "파일 구간 밖" 으로 셉니다.
public static class IoLayoutScan
{
    /// 한 번에 훑는 파일 수 상한. 넘으면 훑기를 멈추고 품질 수치에 미완료로 남깁니다 (상한 값은 재지 않은 안전 한도).
    public const int MaxFiles = 1_000_000;

    public static IoSummary Build(IoRequestSnapshot snapshot, IEnumerable<string> roots, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var mapper = new IoBlockMapper(snapshot);
        var volumes = new Dictionary<char, Volume?>();
        long scanned = 0;
        long changed = 0;
        long unreadable = 0;
        long skipped = 0;
        bool incomplete = false;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (string root in roots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            if (HasReparsePoint(root))
            {
                skipped++; // 정션·심볼릭 링크·마운트 지점 아래는 경로의 드라이브 문자와 실제 파일의 볼륨이 다를 수 있어 틀리게 붙음
                continue;
            }

            foreach ((string path, FileAttributes attributes, DateTime writtenUtc, long length) in Files(root, options))
            {
                ct.ThrowIfCancellationRequested();
                if (scanned >= MaxFiles)
                {
                    incomplete = true;
                    break;
                }

                if (path.Length < 2 || path[1] != ':')
                {
                    continue; // 드라이브 문자가 없는 경로 (UNC·마운트 지점) 는 볼륨 위치를 모름
                }

                char letter = char.ToUpperInvariant(path[0]);
                if (!volumes.TryGetValue(letter, out Volume? volume))
                {
                    volumes[letter] = volume = OpenVolume(path, mapper, ref skipped);
                }

                if (volume is null)
                {
                    continue;
                }

                scanned++;
                IReadOnlyList<Extent>? extents = FileLayout.AllocatedExtents(path);
                if (extents is null)
                {
                    unreadable++;
                    continue;
                }

                bool changedDuringSession = writtenUtc > snapshot.ObservedAtUtc.UtcDateTime;
                changed += changedDuringSession ? 1 : 0;
                bool convertible = (attributes & (FileAttributes.Compressed | FileAttributes.Encrypted)) == 0;
                mapper.AddFile(path, changedDuringSession, convertible, volume.Disk, extents.Select(e => volume.ToBytes(e)),
                    snapshot.DiskOnly ? length : null, snapshot.DiskOnly ? writtenUtc.Ticks : null); // 디스크 전용 요약만 파일 크기·수정 시각을 남김 (목록을 훑으며 이미 얻은 값이라 따로 조회하지 않음)
            }
        }

        return mapper.Build(scanned, changed, unreadable, skipped, clock.ElapsedMilliseconds, incomplete);
    }

    private static FileSystemEnumerable<(string Path, FileAttributes Attributes, DateTime WrittenUtc, long Length)> Files(string root, EnumerationOptions options) =>
        new(root, (ref FileSystemEntry e) => (e.ToFullPath(), e.Attributes, e.LastWriteTimeUtc.UtcDateTime, e.Length), options)
        {
            ShouldIncludePredicate = (ref FileSystemEntry e) => !e.IsDirectory,
        };

    /// 폴더 자신이나 그 조상 중에 재해석 지점이 있는가 (조회에 실패하면 안전하게 있다고 봄).
    public static bool HasReparsePoint(string directory)
    {
        try
        {
            for (string? dir = Path.GetFullPath(directory); dir is not null; dir = Path.GetDirectoryName(dir))
            {
                if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return true;
        }
    }

    /// LCN 0 이 볼륨 시작인 NTFS 만 변환합니다: FAT·exFAT 는 LCN 기준이 데이터 영역 시작이고 ReFS 는 확인하지 않아 틀리게 붙이느니 붙이지 않음. 이름을 못 읽어도 ("") 건너뜀.
    public static bool ConvertsFileSystem(string name) => string.Equals(name, "NTFS", StringComparison.OrdinalIgnoreCase);

    // 볼륨이 디스크 하나에 놓인 NTFS 일 때만 변환합니다 (여러 디스크 볼륨은 그 디스크의 파일 구간 밖 읽기를 "변환 안 함" 으로 세도록 표시하고 건너뜀).
    private static Volume? OpenVolume(string path, IoBlockMapper mapper, ref long skipped)
    {
        IReadOnlyList<DiskExtent>? disks = VolumeDisks.DiskExtentsOf(path);
        int? cluster = FileLayout.ClusterBytes(path);
        if (disks is { Count: 1 } && cluster is > 0 && ConvertsFileSystem(VolumeInfo.FileSystem(path[..2])))
        {
            return new Volume(disks[0].Disk, disks[0].StartingOffset, cluster.Value);
        }

        skipped++;
        foreach (DiskExtent disk in disks ?? [])
        {
            mapper.SkipDisk(disk.Disk);
        }

        return null;
    }

    private sealed record Volume(uint Disk, long Start, long ClusterBytes)
    {
        public IoFileExtent ToBytes(Extent e) => new(Start + (e.Lcn * ClusterBytes), e.Vcn * ClusterBytes, e.Clusters * ClusterBytes);
    }
}
