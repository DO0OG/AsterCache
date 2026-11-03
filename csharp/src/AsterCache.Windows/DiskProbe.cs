using System.Diagnostics;
using System.Runtime.InteropServices;
using AsterCache.Core;
using AsterCache.Core.Volume;
using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// 볼륨 원시 핸들로 지정 LCN 에서 비버퍼드 순차 읽기 속도를 재는 표본 (서비스 전용, 쓰기 뒤 디스크 회복 확인용).
/// 서로 떨어진 64 MiB 구간 3 곳을 1 MiB x 64 씩 한 번씩 읽은 중앙값 MB/s (decimal): 같은 곳을 되풀이해 읽으면 2·3 회는 디스크 캐시라서. 총 192 MiB, 벽시계 WallLimit 안에 못 끝내면 null.
/// 비어 있는 영역은 SMR 이 0 을 즉시 돌려 과대하게 나오므로 호출하는 쪽이 할당된 곳만 고릅니다.
public static class DiskProbe
{
    public const int BlockMib = 1;
    public const int Blocks = 64;
    public const int Spots = 3; // 한 표본이 읽는 서로 다른 64 MiB 구간 수
    public const int SpotStrideWindows = 4; // 구간 사이 시작점 간격 (64 MiB 칸): 256 MiB, 구간 사이가 192 MiB 비어 있어 연속이 아님
    public const int RotationSteps = 4; // 표본마다 64 MiB 씩 옮기는 횟수: 4 번 만에 12 칸 (768 MiB) 이 모두 한 번씩 쓰여 HDD 자체 캐시 (256 MB) 에 같은 곳이 다시 맞지 않음
    public const int RotationWindows = SpotStrideWindows * Spots; // 존·기준 구간이 할당·기록돼 있어야 하는 64 MiB 칸 수
    public const int ReferenceOffsetGib = 4; // 존에서 이만큼 뒤의 할당된 영역을 쓰지 않을 기준으로 삼음
    public static readonly TimeSpan WallLimit = TimeSpan.FromSeconds(10);
    private const uint FileFlagSequentialScan = 0x08000000;
    private const int Sector = 4096;
    private const long Mib = 1L << 20;

    /// letter ("E:") 볼륨의 lcn 에서 읽은 속도 (MB/s), 열거나 읽지 못하거나 WallLimit 을 넘기면 null.
    public static unsafe double? Mbps(string letter, long lcn)
    {
        try
        {
            int cluster = FileLayout.ClusterBytes(letter.TrimEnd(':', '\\') + @":\") ?? 0;
            if (cluster <= 0 || lcn < 0)
            {
                return null;
            }

            using SafeFileHandle h = NativeMethods.CreateFileW(@"\\.\" + letter.TrimEnd(':', '\\') + ":", Win32.GenericRead, Win32.ShareAll, 0, Win32.OpenExisting, Win32.FileFlagNoBuffering | FileFlagSequentialScan, 0);
            if (h.IsInvalid)
            {
                return null;
            }

            int block = checked((int)(BlockMib * Mib));
            nint buffer = (nint)NativeMemory.AlignedAlloc((nuint)block, Sector);
            try
            {
                return Run(lcn * cluster, offset => RandomAccess.Read(h, new Span<byte>((void*)buffer, block), offset) == block, Stopwatch.GetTimestamp, WallLimit); // 읽기 실패 = 볼륨 끝을 넘음
            }
            finally
            {
                NativeMemory.AlignedFree((void*)buffer);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// 한 표본이 읽는 3 곳의 시작 바이트 오프셋 (서로 SpotStrideWindows x 64 MiB 떨어짐).
    internal static long[] SpotOffsets(long startOffset) => [.. Enumerable.Range(0, Spots).Select(i => startOffset + i * SpotStrideWindows * Blocks * BlockMib * Mib)];

    // readBlock(offset): 1 MiB 한 블록을 읽었는가. 벽시계 (now 는 Stopwatch 타임스탬프) 가 limit 을 넘으면 null.
    internal static double? Run(long startOffset, Func<long, bool> readBlock, Func<long> now, TimeSpan limit)
    {
        long begin = now();
        var rates = new double[Spots];
        long[] offsets = SpotOffsets(startOffset);
        for (int i = 0; i < offsets.Length; i++)
        {
            long spotStart = now();
            for (int b = 0; b < Blocks; b++)
            {
                if (!readBlock(offsets[i] + b * (BlockMib * Mib)) || Stopwatch.GetElapsedTime(begin, now()) > limit)
                {
                    return null;
                }
            }

            rates[i] = Rate(Blocks * BlockMib * Mib, Stopwatch.GetElapsedTime(spotStart, now()));
        }

        return Median(rates);
    }

    /// 표본 한 칸 (64 MiB) 의 LCN 수 (회전 간격), 클러스터 크기를 모르면 0 (회전 없음).
    public static long StepLcn(string letter) => FileLayout.ClusterBytes(letter.TrimEnd(':', '\\') + @":\") is > 0 and int c ? BlockMib * Blocks * Mib / c : 0;

    /// 존 시작 lcn 의 앞쪽 표본 구간 (512 MiB) 이 할당되고 기록돼 있는지와, 존에서 4 GiB 뒤에 가장 가까운 같은 조건의 기준 구간의 LCN. 비트맵을 못 읽으면 (관리자 아님 포함) null.
    /// 기록됨 = 각 64 MiB 칸의 첫 4 KiB 가 전부 0 이 아님 (할당만 되고 안 쓴 영역은 SMR 이 0 을 즉시 돌려 과대).
    public static (bool ZoneAllocated, long? ReferenceLcn)? Locate(string letter, long zoneLcn)
    {
        try
        {
            using NtfsVolume v = NtfsVolume.Open(letter);
            using SafeFileHandle h = NativeMethods.CreateFileW(@"\\.\" + letter.TrimEnd(':', '\\') + ":", Win32.GenericRead, Win32.ShareAll, 0, Win32.OpenExisting, Win32.FileFlagNoBuffering, 0);
            return h.IsInvalid ? null : Locate(v.Bitmap(), v.ClusterBytes, zoneLcn, lcn => HasData(h, lcn * v.ClusterBytes));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotAdminException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // written: LCN 의 첫 4 KiB 가 0 이 아닌가 (null 이면 확인 안 함).
    internal static (bool ZoneAllocated, long? ReferenceLcn) Locate(VolumeBitmap bitmap, int clusterBytes, long zoneLcn, Func<long, bool>? written = null)
    {
        long step = (BlockMib * Blocks * Mib + clusterBytes - 1) / clusterBytes;
        long need = step * RotationWindows;
        bool zoneAllocated = true;
        for (long i = 0; i < need && zoneAllocated; i++)
        {
            zoneAllocated = bitmap.Used(zoneLcn + i);
        }

        zoneAllocated = zoneAllocated && Written(written, zoneLcn, step);
        return (zoneAllocated, FindReference(bitmap, zoneLcn + ReferenceOffsetGib * (1L << 30) / clusterBytes, need, lcn => Written(written, lcn, step)));
    }

    private static bool Written(Func<long, bool>? written, long lcn, long step)
    {
        for (int w = 0; w < RotationWindows; w++)
        {
            if (written is not null && !written(lcn + w * step))
            {
                return false;
            }
        }

        return true;
    }

    private static unsafe bool HasData(SafeFileHandle h, long offset)
    {
        void* buffer = NativeMemory.AlignedAlloc(Sector, Sector);
        try
        {
            var span = new Span<byte>(buffer, Sector);
            return RandomAccess.Read(h, span, offset) == Sector && span.IndexOfAnyExcept((byte)0) >= 0;
        }
        finally
        {
            NativeMemory.AlignedFree(buffer);
        }
    }

    // 모두 할당된 need 클러스터 이상의 구간 중 target 에 가장 가까운 (ok 인) 시작 LCN, 없으면 null (할당 구간 = 빈 구간 사이).
    internal static long? FindReference(VolumeBitmap bitmap, long target, long need, Func<long, bool>? ok = null)
    {
        long? best = null;
        long prevEnd = bitmap.Start;
        foreach ((long lcn, long length) in bitmap.FreeRuns().Append((bitmap.Start + bitmap.NBits, 0)))
        {
            if (lcn - prevEnd >= need)
            {
                long at = Math.Clamp(target, prevEnd, lcn - need);
                if (ok is null || ok(at))
                {
                    best = best is { } b && Math.Abs(b - target) <= Math.Abs(at - target) ? best : at;
                }
            }

            prevEnd = lcn + length;
        }

        return best;
    }

    internal static double Rate(long bytes, TimeSpan elapsed) => bytes / 1e6 / Math.Max(elapsed.TotalSeconds, 1e-9);

    internal static double Median(double[] values)
    {
        double[] sorted = [.. values.Order()];
        return sorted[sorted.Length / 2];
    }
}
