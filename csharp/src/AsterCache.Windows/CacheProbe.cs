using System.Diagnostics;
using AsterCache.Core.Prefetch;

namespace AsterCache.Windows;

/// 표본 몇 곳의 읽기 결과: 읽은 표본 수, 느린 (ColdThresholdMs 초과) 표본 수, 평균 시간 (ms, 로그용).
/// 식었는가: 느린 표본이 ColdSlowSamples 곳 이상 (표본이 그보다 적으면 전부).
public sealed record ProbeResult(int Samples, int Slow, double AverageMs)
{
    public bool Cold => Samples > 0 && Slow >= Math.Min(CacheProbe.ColdSlowSamples, Samples);
}

/// 핫 집합이 파일 캐시에 있는지 몇 군데를 읽어 재 봅니다 (일반 버퍼드 읽기, 읽기 전용).
public static class CacheProbe
{
    public const int SampleBytes = 1 << 20;

    /// 한 표본이 이 값을 넘으면 느린 것으로 봅니다: 파일 캐시 적중은 1 MiB 에 1 ms 미만, HDD 는 10 ms 이상이라 그 사이로 잡음.
    public const double ColdThresholdMs = 2.0;

    /// 느린 표본이 이만큼 (표본 8 곳 중) 이상이면 캐시에서 빠진 것으로 봅니다: 한두 곳의 일시적 지연으로 다시 예열하지 않게.
    public const int ColdSlowSamples = 2;

    /// 접근 순서 앞쪽 (게임 시작에 먼저 필요한 부분) 에서 고정으로 고르는 표본 수와, 나머지에서 고르게 고르는 표본 수.
    public const int HeadSamples = 4;
    public const int SpreadSamples = 4;

    /// 순서 목록에서 앞쪽 head 곳 (고정) + 나머지에서 고르게 spread 곳 (목록이 더 짧으면 전부) 의 시작 위치와 읽을 길이 (그 op 길이, 최대 SampleBytes: 짧은 op 는 이웃 데이터를 읽지 않게).
    public static List<(string Path, long Offset, int Bytes)> PickSamples(IReadOnlyList<ReadOp> ordered, int head = HeadSamples, int spread = SpreadSamples)
    {
        var picked = new List<(string, long, int)>();
        int h = Math.Min(head, ordered.Count);
        int rest = ordered.Count - h;
        int k = Math.Min(spread, rest);
        IEnumerable<int> idx = Enumerable.Range(0, h).Concat(Enumerable.Range(0, k).Select(i => h + (int)((long)i * rest / k)));
        foreach (int i in idx)
        {
            ReadOp op = ordered[i];
            picked.Add((op.Path, op.Offset, (int)Math.Min(op.Length, SampleBytes)));
        }

        return picked;
    }

    /// 표본마다 읽어 걸린 시간 (ms) 을 재고 판정합니다. 열 수 없거나 읽을 것이 없는 표본은 뺍니다. 모두 빠지면 null.
    public static ProbeResult? Probe(IReadOnlyList<(string Path, long Offset, int Bytes)> samples)
    {
        byte[] buf = new byte[SampleBytes];
        double total = 0;
        int ok = 0;
        int slow = 0;
        foreach ((string path, long offset, int bytes) in samples)
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
                    double ms = watch.Elapsed.TotalMilliseconds;
                    total += ms;
                    ok++;
                    slow += ms > ColdThresholdMs ? 1 : 0;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 게임이 배타적으로 연 파일 등은 표본에서 제외
            }
        }

        return ok == 0 ? null : new ProbeResult(ok, slow, total / ok);
    }
}
