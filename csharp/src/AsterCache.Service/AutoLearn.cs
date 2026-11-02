using System.Globalization;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Tracer;

namespace AsterCache.Service;

/// 선행 읽기가 닿는 바깥 세계 (파일 읽기, 가용 RAM). 시험은 가짜로 바꿉니다.
public sealed class LearnEnv
{
    public static LearnEnv Real { get; } = new();

    public Func<IReadAheadIo> OpenIo { get; init; } = () => new FileReadAheadIo();

    public Func<long> AvailableRamMib { get; init; } = SystemMemory.AvailableMib;
}

/// 첫 실행 학습 (auto.learn): 자동 트레이스 세션의 읽기·열기 알림을 선행 읽기 엔진에 이어 줍니다. 세션은 트레이스와 하나를 같이 씁니다.
public static class AutoLearn
{
    /// 진행 상태 (auto_status) 를 갱신하는 최소 간격.
    public static readonly TimeSpan StatusEvery = TimeSpan.FromSeconds(10);

    /// 창·상한은 설정 (max_total_mib 0 = 자동), 허용 루트는 게임의 원점 루트, RAM 여유는 게임의 prewarm.min_free_ram_mib.
    public static ReadAhead Create(GameSettings g, LearnSettings s, long physicalRamBytes, LearnEnv env) => new(
        new ReadAheadOptions([g.OriginRoot], Math.Max(1, (long)s.WindowMib) << 20, ReadAhead.ResolveMaxBytes(s.MaxTotalMib, physicalRamBytes), g.Prewarm.MinFreeRamMib, env.AvailableRamMib, Stream: s.Mode != LearnSettings.GapMode, Bounded: s.Mode is LearnSettings.BoundedMode or LearnSettings.AdaptiveMode, Adaptive: s.Mode == LearnSettings.AdaptiveMode, SaturationMbps: s.SaturationMbps),
        env.OpenIo());

    public static LiveListener Listener(ReadAhead engine) => new(engine.OnRead, engine.OnOpen, engine.OnLateRead);

    public static string Detail(ReadAheadStats st) => $"{st.ReadAheadBytes >> 20} MiB read ahead";

    public static string Summary(ReadAheadStats st) => string.Create(
        CultureInfo.InvariantCulture,
        $"{st.ReadAheadBytes >> 20} MiB read ahead, hit estimate {st.HitRatio * 100:F0}% of {st.GameReadBytes >> 20} MiB game reads, {st.ResolvedReads} resolved reads, {st.LateReads} late reads, {st.SequentialFiles} sequential files, {st.LowPriorityFailures} low I/O hint failures, gap_count {st.GapCount}, yielded_requests {st.YieldedRequests}, read_ahead_while_idle_bytes {st.ReadAheadWhileIdleBytes}, saturated_ms {st.SaturatedMs}, paused_requests {st.PausedRequests}");
}
