namespace AsterCache.Core.Plan;

/// 계획의 존 크기 정하기: 탐색용으로 큰 존을 잡아 핫 바이트를 먼저 재고 (ProbeMib), 그 크기에 맞춰 다시 계획합니다.
public static class ZoneSize
{
    public const long ProbeMib = 16384;
    private const long Mib = 1L << 20;
    private const long RoundMib = 16;

    /// 핫 바이트 전체를 담는 존 크기 (MiB): 1.1 배를 16 MiB 단위로 올림, 최소 16.
    public static long ZoneMib(long hotBytes)
    {
        long padded = ((hotBytes * 11) + 9) / 10;
        long mib = (padded + Mib - 1) / Mib;
        return Math.Max(RoundMib, (mib + RoundMib - 1) / RoundMib * RoundMib);
    }

    /// 자동 존 크기 상한 (MiB): 물리 RAM 의 1/4, 2048 이상 8192 이하.
    public static long AutoMaxMib(long physicalRamBytes) => Math.Max(2048, Math.Min(physicalRamBytes / Mib / 4, 8192));
}
