using System.Diagnostics;
using System.Runtime.InteropServices;
using AsterCache.Core;
using AsterCache.Core.Volume;
using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// 볼륨 원시 핸들로 지정 LCN 에서 비버퍼드 순차 읽기 속도를 재는 표본 (서비스 SYSTEM 전용, 쓰기 뒤 디스크 회복 확인용).
/// 1 MiB x 64 를 3 회 읽은 중앙값 MB/s (decimal). 비어 있는 영역은 SMR 이 0 을 즉시 돌려 과대하게 나오므로 호출하는 쪽이 할당된 곳만 고릅니다.
public static class DiskProbe
{
    public const int BlockMib = 1;
    public const int Blocks = 64;
    public const int Repeats = 3;
    public const int ReferenceOffsetGib = 4; // 존에서 이만큼 뒤의 할당된 영역을 쓰지 않을 기준으로 삼음
    private const uint FileFlagSequentialScan = 0x08000000;
    private const int Sector = 4096;
    private const long Mib = 1L << 20;

    /// letter ("E:") 볼륨의 lcn 에서 읽은 속도 (MB/s), 열거나 읽지 못하면 null.
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
            void* buffer = NativeMemory.AlignedAlloc((nuint)block, Sector);
            try
            {
                var rates = new double[Repeats];
                for (int i = 0; i < Repeats; i++)
                {
                    long offset = lcn * cluster;
                    long start = Stopwatch.GetTimestamp();
                    for (int b = 0; b < Blocks; b++)
                    {
                        if (RandomAccess.Read(h, new Span<byte>(buffer, block), offset + b * (long)block) != block)
                        {
                            return null; // 볼륨 끝을 넘음
                        }
                    }

                    rates[i] = Rate(Blocks * (long)block, Stopwatch.GetElapsedTime(start));
                }

                return Median(rates);
            }
            finally
            {
                NativeMemory.AlignedFree(buffer);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// 존 시작 lcn 의 앞쪽 표본 구간이 이미 할당돼 있는지와, 존에서 4 GiB 뒤에 가장 가까운 할당된 기준 구간의 LCN. 비트맵을 못 읽으면 (관리자 아님 포함) null.
    public static (bool ZoneAllocated, long? ReferenceLcn)? Locate(string letter, long zoneLcn)
    {
        try
        {
            using NtfsVolume v = NtfsVolume.Open(letter);
            return Locate(v.Bitmap(), v.ClusterBytes, zoneLcn);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotAdminException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    internal static (bool ZoneAllocated, long? ReferenceLcn) Locate(VolumeBitmap bitmap, int clusterBytes, long zoneLcn)
    {
        long need = (Blocks * BlockMib * Mib + clusterBytes - 1) / clusterBytes;
        bool zoneAllocated = true;
        for (long i = 0; i < need && zoneAllocated; i++)
        {
            zoneAllocated = bitmap.Used(zoneLcn + i);
        }

        return (zoneAllocated, FindReference(bitmap, zoneLcn + ReferenceOffsetGib * (1L << 30) / clusterBytes, need));
    }

    // 모두 할당된 need 클러스터 이상의 구간 중 target 에 가장 가까운 시작 LCN, 없으면 null (할당 구간 = 빈 구간 사이).
    internal static long? FindReference(VolumeBitmap bitmap, long target, long need)
    {
        long? best = null;
        long prevEnd = bitmap.Start;
        foreach ((long lcn, long length) in bitmap.FreeRuns().Append((bitmap.Start + bitmap.NBits, 0)))
        {
            if (lcn - prevEnd >= need)
            {
                long at = Math.Clamp(target, prevEnd, lcn - need);
                best = best is { } b && Math.Abs(b - target) <= Math.Abs(at - target) ? best : at;
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
