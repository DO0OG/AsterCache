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

    /// 게임이 없을 때 메모리 안 캐시를 다시 만지는 데 쓰는 시간이 벽시계 시간에서 차지할 수 있는 몫의 상한 (2%): 한 번에 7초를 썼으면 그 뒤 350초가 지나야 다음 묶음 새로 고침을 합니다 (GroupEvery 는 그대로 하한).
    /// 근거: 설치본 실측에서 다른 프로그램이 캐시를 계속 쓰는 동안 60초마다 약 16.8 GiB 를 4~7초씩 만져 서비스 CPU 가 90초에 5~21초까지 올랐고, 이 몫이면 그 때도 CPU 를 코어 하나의 2% 안에 둠. 값 자체는 실험으로 고른 것이 아니라 "배경 일이 눈에 띄지 않을 크기" 로 정한 것입니다.
    public const double IdleTouchShare = 0.02;

    /// 만지는 데 touchSeconds 초를 썼을 때 다음 묶음 새로 고침까지 기다릴 시간 (IdleTouchShare 로 나눈 값).
    public static TimeSpan IdleTouchWait(double touchSeconds) => TimeSpan.FromSeconds(touchSeconds / IdleTouchShare);

    /// 재사용 누계가 앞에 있던 양 A 의 이 비율에 닿으면 밀려날 위험이 가깝다고 봅니다 (RefreshDue 의 0.4 배보다 늦고, 실험에서 첫 파일이 밀려난 0.61~0.81 배보다 이른 값).
    /// 2026-11-24 의 Call of Duty 는 2% 상한 때문에 10분 넘게 쉬는 동안 재사용 누계가 A 의 1.4 배에 닿아 목록 전체가 밀려났음 (12:28 에 6,866 MiB 대 4,973 MiB).
    public const double AtRiskFraction = 0.5;

    /// 위험할 때 새로 고치는 데 쓰는 시간이 벽시계에서 차지할 수 있는 몫의 상한 (5%, 평소의 IdleTouchShare 와 따로 셈): 쓴 시간을 이 몫으로 나눈 만큼 지나야 같은 몫이 돌아옵니다.
    public const double AtRiskTouchShare = 0.05;

    /// 위험할 때 쉬는 동안 쌓이는 쓸 수 있는 시간의 최대 (초): 6,687 MiB 를 처음 만질 때 10.5 초가 걸렸음 (11-24 12:16:26).
    public const double AtRiskBurstSeconds = 12;

    /// 만지는 데 touchSeconds 초를 썼을 때 위험 상한 (AtRiskTouchShare) 으로 갚는 데 걸리는 시간.
    public static TimeSpan AtRiskTouchWait(double touchSeconds) => TimeSpan.FromSeconds(touchSeconds / AtRiskTouchShare);

    /// 밀려날 위험이 가까운가: 사다리를 만든 뒤 재사용 누계 증가분이 max(하한, AtRiskFraction x A) 이상 (대기 목록을 못 읽으면 판정하지 않음).
    public static bool AtRisk(LadderMark mark, MemoryLists? lists) =>
        Repurposed(mark, lists) is { } grown && grown >= Math.Max(MinRepurposedBytes, (long)(AtRiskFraction * mark.AheadBytes));

    /// 스트림이 도는 동안 다른 게임의 새로 고칠 때를 보는 간격: 판정마다 대기 목록 통계를 한 번 읽으므로 묶음마다 (약 1초) 보지 않습니다.
    public static readonly TimeSpan ProtectCheckEvery = TimeSpan.FromSeconds(10);

    /// 대기 목록을 읽을 수 없을 때 (권한 없음 등) 시간으로 대신하는 주기.
    public static readonly TimeSpan FallbackEvery = TimeSpan.FromMinutes(10);

    /// 우리보다 앞에 있는 양: 사다리를 만든 직후 캐시 대기 바이트에서 그 게임이 만진 바이트를 뺀 값 (0 미만이면 0).
    public static long Ahead(long cacheStandbyBytes, long touchedBytes) => Math.Max(0, cacheStandbyBytes - touchedBytes);

    /// 다른 게임들이 방금 만진 뒤 우리 앞에 있는 양: 그 게임 X 의 이전 표식이 우리 표식보다 먼저 만들어졌을 때만 (그때만 X 의 페이지가 우리 앞에 있었음) min(X 의 이전 표식이 만진 바이트, X 가 이번에 만진 바이트) 만큼 줄어듦 (0 미만이면 0).
    /// X 에 이전 표식이 없었거나 (새로 예열된 게임) 우리 표식보다 나중이면 줄이지 않습니다. 스트림이 디스크에서 새로 읽은 양은 재사용 누계에 이미 잡히므로 이 규칙에서 자연히 빠지고, 스파스로 건너뛴 양은 만진 바이트에 없습니다.
    /// 묶음 새로 고침·스트림 뒤 사다리·스트림 앞의 보호가 같은 규칙을 씁니다 (묶음에서 새 표식을 받는 게임은 GroupAhead 로 다시 계산하므로 대상이 아님).
    /// withoutPreviousAll: 이전 표식이 없는 X 가 만진 바이트 전체를 줄입니다 (스트림 끝의 사다리용): 재예열·보충 스트림의 게임은 표식이 이미 지워져 있어 위 규칙대로면 0 이 되는데, 실제로는 그 게임이 캐시에 가진 페이지가 우리 앞에서 뒤로 가기 때문.
    /// 얼마가 그런 페이지인지 알 수 없어 만진 전체를 줄입니다 (과하면 새로 고침이 한 번 일찍 올 뿐이고 덜 줄이면 캐시를 잃음).
    public static long AheadAfterOthersTouched(LadderMark mine, IEnumerable<(LadderMark? Previous, long TouchedBytes)> others, bool withoutPreviousAll = false) =>
        Ahead(mine.AheadBytes, others.Sum(o => o.Previous is { } previous ? (previous.At < mine.At ? Math.Min(previous.TouchedBytes, o.TouchedBytes) : 0) : withoutPreviousAll ? o.TouchedBytes : 0));

    /// 스트림 도중의 보호가 새 표식의 A 를 계산할 때 캐시 대기 바이트에서 뺄 양: 스트림이 그때까지 읽은 양은 끝의 사다리에서 다시 만져져 보호한 게임들의 뒤로 가지만,
    /// 끝의 사다리는 스트림 게임의 이전 표식이 있으면 min(이전 표식이 만진 양, 이번 양) 만 줄이므로 (AheadAfterOthersTouched) 그 몫을 뺀 나머지를 미리 뺍니다.
    /// 이전 표식이 없으면 (priorTouchedBytes 0) 끝의 사다리가 만진 전체를 줄이므로 (withoutPreviousAll) 여기서는 빼지 않습니다: 빼면 끝에서 건너뛴 게임이 두 번 줄고, 사다리 없이 끝나도 스트림 페이지는 그 게임 앞에 그대로 있어 안 빼는 쪽이 실제와 맞습니다.
    public static long StreamAheadLess(long streamReadBytes, long priorTouchedBytes) => priorTouchedBytes > 0 ? Math.Max(0, streamReadBytes - priorTouchedBytes) : 0;

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

    /// 표식이 있던 게임이 식었다고 볼 때 로그 줄 끝에 붙이는 말: 사다리를 만든 뒤 지난 분과 (재사용 누계를 알면) 그동안의 재사용 양과 지금 앞에 있는 양.
    public static string SinceLadder(DateTimeOffset at, DateTimeOffset now, long? repurposedBytes, long aheadBytes) =>
        $", since the ladder {Math.Max(0, (long)(now - at).TotalMinutes)} min ago{(repurposedBytes is { } grown ? $": {grown >> 20} MiB repurposed of {aheadBytes >> 20} MiB ahead" : string.Empty)}";

    /// 지금 새로 고칠 때인가: 증가분이 max(하한, 0.4 x A) 이상이거나 남은 여유 (A - 증가분) 가 MinHeadroomBytes 미만이면 예.
    /// 남은 여유 기준은 표식을 만들 때 A 가 2 x MinHeadroomBytes 이상이었던 게임에만: 캐시가 원래 작은 게임은 만질 때마다 곧바로 다시 때가 되기 때문.
    /// 대기 목록을 못 읽으면 (표식을 만들 때나 지금) 마지막 사다리 뒤 FallbackEvery.
    public static bool RefreshDue(LadderMark mark, MemoryLists? lists, DateTimeOffset now) =>
        Repurposed(mark, lists) is { } grown
            ? grown >= Math.Max(MinRepurposedBytes, (long)(AheadFraction * mark.AheadBytes))
                || (mark.MarkedAheadBytes >= 2 * MinHeadroomBytes && mark.AheadBytes - grown < MinHeadroomBytes)
            : now - mark.At >= FallbackEvery;

    /// 앞으로 readBytes 를 더 읽으면 (그만큼 캐시 재사용이 늘면) 이 표식의 게임이 새로 고칠 때가 되는가: 읽을 양이 그 게임의 남은 여유보다 충분히 작으면 (RefreshDue 가 아니면) 거짓이라 미리 지킬 필요가 없습니다.
    /// 대기 목록을 모르거나 표식에 기준이 없으면 참 (지금처럼 지킴).
    public static bool ReadReaches(LadderMark mark, MemoryLists? lists, long readBytes, DateTimeOffset now) =>
        lists is not { } l || mark.RepurposedBytes is null
        || RefreshDue(mark, l with { CacheRepurposedBytes = l.CacheRepurposedBytes > long.MaxValue - readBytes ? long.MaxValue : l.CacheRepurposedBytes + readBytes }, now);
}
