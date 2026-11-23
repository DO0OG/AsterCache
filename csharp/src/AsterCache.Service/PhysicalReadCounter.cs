using System.Collections.Concurrent;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 드라이브가 놓인 물리 디스크가 읽은 누계 바이트 (IOCTL_DISK_PERFORMANCE 의 BytesRead, PlayRecorder 가 쓰는 DiskPerformanceMonitor). 드라이브마다 한 번 열어 두고 부를 때마다 한 번 조회합니다.
internal static class PhysicalReadCounter
{
    private static readonly ConcurrentDictionary<string, DiskPerformanceMonitor> Monitors = new(StringComparer.Ordinal);

    /// path 의 드라이브가 놓인 물리 디스크들의 읽은 누계 바이트. 열거나 조회하지 못하면 null (다음에 다시 시도).
    public static long? Total(string path)
    {
        try
        {
            string key = DiskReadLedger.DriveOf(path);
            if (!Monitors.TryGetValue(key, out DiskPerformanceMonitor? monitor))
            {
                monitor = DiskInfo.OpenPerformanceMonitor(key + "\\");
                if (monitor is null)
                {
                    return null;
                }

                if (!Monitors.TryAdd(key, monitor))
                {
                    monitor.Dispose();
                    monitor = Monitors[key];
                }
            }

            lock (monitor)
            {
                return monitor.Read()?.Sum(sample => sample.ReadBytes);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OverflowException or InvalidOperationException)
        {
            return null;
        }
    }
}
