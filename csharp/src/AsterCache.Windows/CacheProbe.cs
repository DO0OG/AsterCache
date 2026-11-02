using System.Diagnostics;
using AsterCache.Core.Prefetch;

namespace AsterCache.Windows;

/// 핫 집합이 파일 캐시에 있는지 몇 군데를 읽어 재 봅니다 (일반 버퍼드 읽기, 읽기 전용).
public static class CacheProbe
{
    public const int SampleBytes = 1 << 20;

    /// 1 MiB 평균 읽기 시간이 이 값 이상이면 캐시에서 빠진 것으로 봅니다: 파일 캐시 적중은 1 MiB 에 1 ms 미만, HDD 는 10 ms 이상이라 그 사이로 잡음.
    public const double ColdThresholdMs = 2.0;

    /// 순서 목록에서 고르게 count 곳 (목록이 더 짧으면 전부) 의 시작 위치.
    public static List<(string Path, long Offset)> PickSamples(IReadOnlyList<ReadOp> ordered, int count = 8)
    {
        var picked = new List<(string, long)>();
        int n = ordered.Count;
        for (int i = 0; i < Math.Min(count, n); i++)
        {
            ReadOp op = ordered[(int)((long)i * n / Math.Min(count, n))];
            picked.Add((op.Path, op.Offset));
        }

        return picked;
    }

    /// 표본마다 bytes 를 읽어 걸린 시간 (ms) 의 평균. 열 수 없거나 읽을 것이 없는 표본은 뺍니다. 모두 빠지면 null.
    public static double? AverageReadMs(IReadOnlyList<(string Path, long Offset)> samples, int bytes = SampleBytes)
    {
        byte[] buf = new byte[bytes];
        double total = 0;
        int ok = 0;
        foreach ((string path, long offset) in samples)
        {
            try
            {
                using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.None);
                f.Seek(offset, SeekOrigin.Begin);
                var watch = Stopwatch.StartNew();
                int got = f.Read(buf, 0, bytes);
                watch.Stop();
                if (got > 0)
                {
                    total += watch.Elapsed.TotalMilliseconds;
                    ok++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 게임이 배타적으로 연 파일 등은 표본에서 제외
            }
        }

        return ok == 0 ? null : total / ok;
    }
}
