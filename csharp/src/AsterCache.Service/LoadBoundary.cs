using AsterCache.Core.Logging;
using AsterCache.Core.Metrics;
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

    /// 게임 시작 때 경계 감시 결과를 남기는 기록기 (Program 이 canApply 일 때 넣음). 비어 있으면 아무것도 쓰지 않습니다.
    public static BenchmarkRecorder? Recorder;

    public static Func<string, bool> OrbitApplied { get; set; } = _ => false;

    /// 로딩 경계 감시가 회차마다 얻는 대여: 그 게임의 시작 스트림과 같은 예외만 (다른 등록 게임이 같은 디스크에서 돌면 거절, 도는 중 다른 게임이 시작하면 대여가 취소됨).
    public static Func<GameSettings, DiskReadCoordinator.Lease?> LeaseFrom(DiskReadCoordinator coordinator) =>
        g => coordinator.TryBegin(g.OriginRoot, "load boundary", preserveForGameRoot: g.OriginRoot, allowWhileYielded: true, preserveGameKey: g.Name);

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

    /// probeWithStop: 대여 토큰 (다른 게임이 시작하면 취소됨) 을 받는 표본 읽기, 비우면 probe 를 쓰고 probe 도 없으면 표본 사이에서 토큰을 확인하며 읽음.
    /// lease: 회차마다 읽기 전에 얻는 중재 대여 (null 을 돌려주면 이 회차는 읽지 않고 측정 없이 끝냄, 인자를 안 주면 대여를 요구하지 않는 옛 동작).
    /// 경계 도달 시간 (ms), 상한 안에 못 닿았거나 잴 수 없으면 null. 시계·대기·경계·표본 읽기는 시험이 바꿉니다 (기본은 실제 값, 대기는 ct 취소 시 바로 깨어남).
    public static int? Run(GameSettings game, AutoStatus status, ILog log, CancellationToken ct = default, Func<DateTimeOffset>? now = null, Action<TimeSpan>? sleep = null,
        Func<GameSettings, LoadBoundary?>? boundary = null, Func<IReadOnlyList<(string Path, long Offset, int Bytes)>, ProbeResult?>? probe = null, BenchmarkRecorder? recorder = null,
        Func<DiskReadCoordinator.Lease?>? lease = null, Func<IReadOnlyList<(string Path, long Offset, int Bytes)>, CancellationToken, ProbeResult?>? probeWithStop = null)
    {
        now ??= () => DateTimeOffset.UtcNow;
        sleep ??= d => ct.WaitHandle.WaitOne(d);
        probeWithStop ??= probe is null ? (s, stop) => CacheProbe.Probe(s, SlowMs, stop) : (s, _) => probe(s);
        if ((boundary ?? LoadTiers.For)(game) is not { Tier0.Count: > 0 } b)
        {
            return null;
        }

        var ops = b.Tier0.Select(e => new ReadOp(e.Path, e.Offset, e.Length)).ToList();
        var used = new List<(string Path, long Offset)>();
        DateTimeOffset start = now();
        BenchmarkRecorder? rec = recorder ?? Recorder;
        Arm arm = OrbitApplied(game.Name) ? Arm.Candidate : Arm.Baseline;
        int? Done(int? ms) // 끝날 때마다 한 건 기록 (도달은 ProbeReadyMs, 미도달은 null)
        {
            rec?.Record(game.Name, start, ms, arm);
            return ms;
        }

        while (!ct.IsCancellationRequested)
        {
            TimeSpan elapsed = now() - start;
            var samples = PickFresh(ops, used, SampleCount); // 회차마다 새 위치: 앞 회차 읽기가 캐시를 채워 도달로 오판하지 않게
            if (samples.Count < SampleCount)
            {
                log.Info($"{game.Name}: tier0 probe not measurable (no unread sample positions left)");
                return Done(null);
            }

            ProbeResult? result;
            if (lease is null)
            {
                result = probeWithStop(samples, default);
            }
            else
            {
                using DiskReadCoordinator.Lease? held = lease();
                if (held is null)
                {
                    log.Info($"{game.Name}: tier0 probe skipped (disk read not granted)");
                    return null; // 읽지 않았으므로 측정 기록도 남기지 않음
                }

                result = probeWithStop(samples, held.Token);
            }

            if (result is { Slow: 0 } r && r.Samples == SampleCount) // 읽지 못한 표본이 있으면 도달로 보지 않음
            {
                int ms = (int)Math.Min(int.MaxValue, (now() - start).TotalMilliseconds);
                status.RecordLoadBoundary(game.Name, ms, now());
                log.Info($"{game.Name}: tier0 probe ready in {ms} ms");
                return Done(ms);
            }

            if (elapsed >= Limit)
            {
                log.Info($"{game.Name}: tier0 probe not ready within {(int)Limit.TotalSeconds} s");
                return Done(null);
            }

            sleep(Interval);
        }

        return null; // 루프는 취소(게임 종료·서비스 정지)로만 빠져나오며, 취소된 회차는 기록하지 않습니다
    }
}
