using AsterCache.Core.Prefetch;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 게임의 사다리를 만든 직후의 표식: 시각, 그때의 캐시 재사용 누계 (대기 목록을 못 읽었으면 null), 우리보다 앞에 있는 캐시 양 A, 만진 바이트, 다시 만질 범위 (이미 역순으로 만든 것).
internal sealed record LadderMark(DateTimeOffset At, long? RepurposedBytes, long AheadBytes, long TouchedBytes, IReadOnlyList<ReadOp> Ladder);

/// 사다리를 언제 다시 만들지의 판정 (순수 함수). 대기 목록은 먼저 들어간 페이지부터 재사용되므로, 우리 페이지 앞에 있던 캐시 A 의 일부가 재사용되면 우리 페이지가 목록 앞에 가까워진 것입니다.
internal static class SolarPolicy
{
    /// 재사용 누계가 A 의 이 비율만큼 늘면 새로 고칩니다. 실측으로는 0.61~0.81 배에서 첫 파일이 밀려났고, 앞에 있던 페이지 일부가 다시 쓰여 예측보다 일찍 오므로 여유를 둡니다.
    public const double AheadFraction = 0.4;

    /// A 가 작아 판정이 흔들릴 때를 위한 하한: 증가분이 이보다 작으면 새로 고치지 않습니다.
    public const long MinRepurposedBytes = 512L << 20;

    /// 대기 목록을 읽을 수 없을 때 (권한 없음 등) 시간으로 대신하는 주기.
    public static readonly TimeSpan FallbackEvery = TimeSpan.FromMinutes(10);

    /// 우리보다 앞에 있는 양: 사다리를 만든 직후 캐시 대기 바이트에서 그 게임이 만진 바이트를 뺀 값 (0 미만이면 0).
    public static long Ahead(long cacheStandbyBytes, long touchedBytes) => Math.Max(0, cacheStandbyBytes - touchedBytes);

    /// 사다리를 만든 뒤의 캐시 재사용 누계 증가분 (표식이나 지금 값이 없으면 null).
    public static long? Repurposed(LadderMark mark, MemoryLists? lists) =>
        lists is { } l && mark.RepurposedBytes is { } baseline ? Math.Max(0, l.CacheRepurposedBytes - baseline) : null;

    /// 지금 새로 고칠 때인가: 증가분이 max(하한, 0.4 x A) 이상이면 예. 대기 목록을 못 읽으면 (표식을 만들 때나 지금) 마지막 사다리 뒤 FallbackEvery.
    public static bool RefreshDue(LadderMark mark, MemoryLists? lists, DateTimeOffset now) =>
        Repurposed(mark, lists) is { } grown
            ? grown >= Math.Max(MinRepurposedBytes, (long)(AheadFraction * mark.AheadBytes))
            : now - mark.At >= FallbackEvery;
}
