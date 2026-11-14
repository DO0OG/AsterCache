using AsterCache.Core.Prefetch;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 게임의 사다리를 만든 직후의 표식: 시각, 그때의 캐시 재사용 누계 (대기 목록을 못 읽었으면 null), 우리보다 앞에 있는 캐시 양 A (다른 게임이 만지면 줄어듦), 만진 바이트, 다시 만질 범위 (이미 역순으로 만든 것).
/// MarkedAheadBytes: 표식을 만들 때의 A (줄어들지 않음), 남은 여유 기준을 적용할 게임인지 가리는 데 씀.
internal sealed record LadderMark(DateTimeOffset At, long? RepurposedBytes, long AheadBytes, long TouchedBytes, IReadOnlyList<ReadOp> Ladder)
{
    public long MarkedAheadBytes { get; init; } = AheadBytes;
}

/// 사다리를 언제 다시 만들지의 판정 (순수 함수). 대기 목록은 먼저 들어간 페이지부터 재사용되므로, 우리 페이지 앞에 있던 캐시 A 의 일부가 재사용되면 우리 페이지가 목록 앞에 가까워진 것입니다.
internal static class SolarPolicy
{
    /// 재사용 누계가 A 의 이 비율만큼 늘면 새로 고칩니다. 실측으로는 0.61~0.81 배에서 첫 파일이 밀려났고, 앞에 있던 페이지 일부가 다시 쓰여 예측보다 일찍 오므로 여유를 둡니다.
    public const double AheadFraction = 0.4;

    /// A 가 작아 판정이 흔들릴 때를 위한 하한: 증가분이 이보다 작으면 새로 고치지 않습니다.
    public const long MinRepurposedBytes = 512L << 20;

    /// 남은 여유 (A - 재사용 증가분) 가 이보다 작으면 비율에 못 미쳐도 새로 고칩니다. 근거: 30초에 약 1.9 GiB 의 재사용이 몰리는 것이 두 번 관측됨 (한 틱 폭주만큼의 여유).
    public const long MinHeadroomBytes = 2L << 30;

    /// 묶음 새로 고침은 직전 묶음 새로 고침 뒤 이 시간 안에는 다시 하지 않습니다 (몰림이 30초 단위라 틱마다 되풀이하지 않게).
    public static readonly TimeSpan GroupEvery = TimeSpan.FromSeconds(60);

    /// 대기 목록을 읽을 수 없을 때 (권한 없음 등) 시간으로 대신하는 주기.
    public static readonly TimeSpan FallbackEvery = TimeSpan.FromMinutes(10);

    /// 우리보다 앞에 있는 양: 사다리를 만든 직후 캐시 대기 바이트에서 그 게임이 만진 바이트를 뺀 값 (0 미만이면 0).
    public static long Ahead(long cacheStandbyBytes, long touchedBytes) => Math.Max(0, cacheStandbyBytes - touchedBytes);

    /// 다른 게임들이 방금 만진 뒤 우리 앞에 있는 양: 그 게임 X 의 이전 표식이 우리 표식보다 먼저 만들어졌을 때만 (그때만 X 의 페이지가 우리 앞에 있었음) min(X 의 이전 표식이 만진 바이트, X 가 이번에 만진 바이트) 만큼 줄어듦 (0 미만이면 0).
    /// X 에 이전 표식이 없었거나 (새로 예열된 게임) 우리 표식보다 나중이면 줄이지 않습니다. 스트림이 디스크에서 새로 읽은 양은 재사용 누계에 이미 잡히므로 이 규칙에서 자연히 빠지고, 스파스로 건너뛴 양은 만진 바이트에 없습니다.
    /// 묶음 새로 고침·스트림 뒤 사다리·스트림 앞의 보호가 같은 규칙을 씁니다 (묶음에서 새 표식을 받는 게임은 GroupAhead 로 다시 계산하므로 대상이 아님).
    public static long AheadAfterOthersTouched(LadderMark mine, IEnumerable<(LadderMark? Previous, long TouchedBytes)> others) =>
        Ahead(mine.AheadBytes, others.Sum(o => o.Previous is { } previous && previous.At < mine.At ? Math.Min(previous.TouchedBytes, o.TouchedBytes) : 0));

    /// 묶음을 만진 순서 (가장 오래된 것이 먼저) 대로 각 게임의 앞에 있는 양: g_i 의 A = max(0, C - (g_i 와 그 뒤에 만진 게임들이 만진 합)), C 는 만진 직후 캐시 대기 바이트.
    public static long[] GroupAhead(long cacheStandbyBytes, IReadOnlyList<long> touchedBytes)
    {
        long[] ahead = new long[touchedBytes.Count];
        long fromHere = 0;
        for (int i = touchedBytes.Count - 1; i >= 0; i--)
        {
            fromHere += touchedBytes[i];
            ahead[i] = Ahead(cacheStandbyBytes, fromHere);
        }

        return ahead;
    }

    /// 사다리를 만든 뒤의 캐시 재사용 누계 증가분 (표식이나 지금 값이 없으면 null).
    public static long? Repurposed(LadderMark mark, MemoryLists? lists) =>
        lists is { } l && mark.RepurposedBytes is { } baseline ? Math.Max(0, l.CacheRepurposedBytes - baseline) : null;

    /// 지금 새로 고칠 때인가: 증가분이 max(하한, 0.4 x A) 이상이거나 남은 여유 (A - 증가분) 가 MinHeadroomBytes 미만이면 예.
    /// 남은 여유 기준은 표식을 만들 때 A 가 2 x MinHeadroomBytes 이상이었던 게임에만: 캐시가 원래 작은 게임은 만질 때마다 곧바로 다시 때가 되기 때문.
    /// 대기 목록을 못 읽으면 (표식을 만들 때나 지금) 마지막 사다리 뒤 FallbackEvery.
    public static bool RefreshDue(LadderMark mark, MemoryLists? lists, DateTimeOffset now) =>
        Repurposed(mark, lists) is { } grown
            ? grown >= Math.Max(MinRepurposedBytes, (long)(AheadFraction * mark.AheadBytes))
                || (mark.MarkedAheadBytes >= 2 * MinHeadroomBytes && mark.AheadBytes - grown < MinHeadroomBytes)
            : now - mark.At >= FallbackEvery;
}
