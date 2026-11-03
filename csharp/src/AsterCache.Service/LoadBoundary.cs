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

    /// 경계 도달 시간 (ms), 상한 안에 못 닿았거나 잴 수 없으면 null. 시계·대기·경계·표본 읽기는 시험이 바꿉니다 (기본은 실제 값, 대기는 ct 취소 시 바로 깨어남).
    public static int? Run(GameSettings game, AutoStatus status, ILog log, CancellationToken ct = default, Func<DateTimeOffset>? now = null, Action<TimeSpan>? sleep = null,
        Func<GameSettings, LoadBoundary?>? boundary = null, Func<IReadOnlyList<(string Path, long Offset, int Bytes)>, ProbeResult?>? probe = null)
    {
        now ??= () => DateTimeOffset.UtcNow;
        sleep ??= d => ct.WaitHandle.WaitOne(d);
        probe ??= CacheProbe.Probe;
        if ((boundary ?? LoadTiers.For)(game) is not { Tier0.Count: > 0 } b)
        {
            return null;
        }

        var ops = b.Tier0.TakeLast(SampleCount).Select(e => new ReadOp(e.Path, e.Offset, e.Length)).ToList(); // 시간상 끝 쪽
        var samples = CacheProbe.PickSamples(ops, SampleCount, 0);
        DateTimeOffset start = now();
        while (!ct.IsCancellationRequested)
        {
            TimeSpan elapsed = now() - start;
            if (probe(samples) is { Samples: > 0, Slow: 0 })
            {
                int ms = (int)Math.Min(int.MaxValue, (now() - start).TotalMilliseconds);
                status.RecordLoadBoundary(game.Name, ms, now());
                log.Info($"{game.Name}: load boundary reached in {ms} ms");
                return ms;
            }

            if (elapsed >= Limit)
            {
                log.Info($"{game.Name}: load boundary not reached within {(int)Limit.TotalSeconds} s");
                return null;
            }

            sleep(Interval);
        }

        return null;
    }
}
