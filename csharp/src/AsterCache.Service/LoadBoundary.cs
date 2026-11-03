using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 게임 시작 때 로딩 경계 (tier0) 의 끝 쪽 범위가 모두 RAM 에 들어온 시각을 재서 auto_status 에 남깁니다 (실게임 로딩 측정의 1 차 지표).
/// 오래 걸리므로 감시 스레드가 아닌 Task.Run 안에서 부릅니다. 경계가 없는 게임 (t_ms 없는 옛 트레이스·트레이스 없음) 은 아무것도 하지 않습니다.
public static class LoadBoundaryWatch
{
    public const int SampleCount = 8;
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan Limit = TimeSpan.FromSeconds(120);
    public const int SampleBytes = 4096;

    /// 4 KiB 표본이 이 시간을 넘으면 느린 것으로 봅니다 (1 MiB 용 CacheProbe.ColdThresholdMs 는 이 크기에 너무 느슨함: 4 KiB 캐시 적중은 마이크로초, HDD 는 수 ms).
    public const double SlowMs = 0.5;
    public const long MinGap = 1L << 20; // 캐시 미리 읽기가 이웃 위치를 올리지 않도록 표본끼리·이전 회차와 이만큼 띄움

    /// tier0 (시간순) 의 끝 쪽 범위부터 거슬러 올라가며, used 와 서로 MinGap 이상 떨어진 새 위치를 count 곳까지 고릅니다 (고른 위치는 used 에 더함). 모자라면 있는 만큼만 돌려줍니다.
    public static List<(string Path, long Offset, int Bytes)> PickFresh(IReadOnlyList<ReadOp> tier0, List<(string Path, long Offset)> used, int count)
    {
        var picked = new List<(string Path, long Offset, int Bytes)>();
        for (int i = tier0.Count - 1; i >= 0 && picked.Count < count; i--)
        {
            ReadOp op = tier0[i];
            for (long pos = op.Offset; pos + SampleBytes <= op.Offset + op.Length && picked.Count < count; pos += MinGap)
            {
                if (!used.Exists(u => u.Path == op.Path && Math.Abs(u.Offset - pos) < MinGap))
                {
                    used.Add((op.Path, pos));
                    picked.Add((op.Path, pos, SampleBytes));
                }
            }
        }

        return picked;
    }

    /// 경계 도달 시간 (ms), 상한 안에 못 닿았거나 잴 수 없으면 null. 시계·대기·경계·표본 읽기는 시험이 바꿉니다 (기본은 실제 값, 대기는 ct 취소 시 바로 깨어남).
    public static int? Run(GameSettings game, AutoStatus status, ILog log, CancellationToken ct = default, Func<DateTimeOffset>? now = null, Action<TimeSpan>? sleep = null,
        Func<GameSettings, LoadBoundary?>? boundary = null, Func<IReadOnlyList<(string Path, long Offset, int Bytes)>, ProbeResult?>? probe = null)
    {
        now ??= () => DateTimeOffset.UtcNow;
        sleep ??= d => ct.WaitHandle.WaitOne(d);
        probe ??= s => CacheProbe.Probe(s, SlowMs);
        if ((boundary ?? LoadTiers.For)(game) is not { Tier0.Count: > 0 } b)
        {
            return null;
        }

        var ops = b.Tier0.Select(e => new ReadOp(e.Path, e.Offset, e.Length)).ToList();
        var used = new List<(string Path, long Offset)>();
        DateTimeOffset start = now();
        while (!ct.IsCancellationRequested)
        {
            TimeSpan elapsed = now() - start;
            var samples = PickFresh(ops, used, SampleCount); // 회차마다 새 위치: 앞 회차 읽기가 캐시를 채워 도달로 오판하지 않게
            if (samples.Count < SampleCount)
            {
                log.Info($"{game.Name}: tier0 probe not measurable (no unread sample positions left)");
                return null;
            }

            if (probe(samples) is { Slow: 0 } r && r.Samples == SampleCount) // 읽지 못한 표본이 있으면 도달로 보지 않음
            {
                int ms = (int)Math.Min(int.MaxValue, (now() - start).TotalMilliseconds);
                status.RecordLoadBoundary(game.Name, ms, now());
                log.Info($"{game.Name}: tier0 probe ready in {ms} ms");
                return ms;
            }

            if (elapsed >= Limit)
            {
                log.Info($"{game.Name}: tier0 probe not ready within {(int)Limit.TotalSeconds} s");
                return null;
            }

            sleep(Interval);
        }

        return null;
    }
}
