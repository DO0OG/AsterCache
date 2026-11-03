using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// 드라이브 문자 → 그 볼륨이 놓인 물리 디스크 번호들 (IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS, 접근 권한 0 이라 관리자 불필요).
/// 같은 물리 디스크는 SameDisk(a, b) = 두 집합이 겹침. 조회에 실패한 (모르는) 쪽은 보수적으로 같은 디스크로 칩니다.
/// 결과는 5 분 캐시하고, 조회 실패(예외 포함)는 캐시하지 않고 그 드라이브 항목을 지웁니다 (분리·재연결·문자 재할당 반영).
public sealed class VolumeDisks(Func<string, IReadOnlySet<uint>?> query, Func<long> nowMs)
{
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000; // CTL_CODE(IOCTL_VOLUME_BASE = 0x56, 0, METHOD_BUFFERED, FILE_ANY_ACCESS)
    private const int MaxExtents = 16;
    private static readonly long CacheMs = (long)TimeSpan.FromMinutes(5).TotalMilliseconds;
    private readonly ConcurrentDictionary<string, (long At, IReadOnlySet<uint>? Disks)> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);

    /// 실제 IOCTL 을 쓰는 서비스 공용 인스턴스.
    public static VolumeDisks Shared { get; } = new(QueryDisks, () => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);

    /// 'X:' (또는 그 아래 경로) 두 곳이 같은 물리 디스크인가. 드라이브 문자를 알 수 없거나 조회에 실패하면 참.
    public static bool SameDisk(string a, string b) => Shared.AreSame(a, b);

    public bool AreSame(string a, string b) => Overlap(DisksOf(a), DisksOf(b));

    /// 둘 중 하나라도 모르면 (null) 같은 디스크, 아니면 번호가 겹치는가.
    internal static bool Overlap(IReadOnlySet<uint>? a, IReadOnlySet<uint>? b) => a is null || b is null || a.Overlaps(b);

    /// VOLUME_DISK_EXTENTS: NumberOfDiskExtents (4 바이트, 정렬 4 바이트 더) 뒤에 DISK_EXTENT (DiskNumber 4, 정렬 4, StartingOffset 8, ExtentLength 8 = 24 바이트) 배열.
    internal static IReadOnlySet<uint>? Parse(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < 8)
        {
            return null;
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        if (count == 0 || buffer.Length < 8 + ((long)count * 24))
        {
            return null;
        }

        var disks = new HashSet<uint>();
        for (int i = 0; i < count; i++)
        {
            disks.Add(BinaryPrimitives.ReadUInt32LittleEndian(buffer[(8 + (i * 24))..]));
        }

        return disks;
    }

    private IReadOnlySet<uint>? DisksOf(string path)
    {
        if (path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0]))
        {
            return null;
        }

        string letter = char.ToUpperInvariant(path[0]) + ":";
        long now = nowMs();
        if (_cache.TryGetValue(letter, out (long At, IReadOnlySet<uint>? Disks) hit) && now - hit.At < CacheMs)
        {
            return hit.Disks;
        }

        IReadOnlySet<uint>? disks;
        try
        {
            disks = query(letter);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            disks = null;
        }

        if (disks is null)
        {
            _cache.TryRemove(letter, out _);
            if (_reported.TryAdd(letter, 0))
            {
                Trace.WriteLine($"VolumeDisks: {letter} 물리 디스크를 판정하지 못해 같은 디스크로 봅니다.");
            }

            return null;
        }

        _cache[letter] = (now, disks);
        return disks;
    }

    private static unsafe IReadOnlySet<uint>? QueryDisks(string letter)
    {
        using SafeFileHandle h = NativeMethods.CreateFileW(@"\\.\" + letter, 0, Win32.ShareAll, 0, Win32.OpenExisting, 0, 0);
        if (h.IsInvalid)
        {
            return null;
        }

        byte[] output = new byte[8 + (MaxExtents * 24)];
        fixed (byte* o = output)
        {
            return NativeMethods.DeviceIoControl(h, IoctlVolumeGetVolumeDiskExtents, null, 0, o, (uint)output.Length, out uint returned, 0)
                ? Parse(output.AsSpan(0, (int)returned))
                : null;
        }
    }
}
