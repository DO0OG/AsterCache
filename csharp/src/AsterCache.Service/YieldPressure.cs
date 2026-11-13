using AsterCache.Windows;

namespace AsterCache.Service;

/// 배경 메모리 양보의 판정 논리: 프로세스·시계 없이 값만으로 결정합니다.
/// 여유분 = 여유 + 대기 목록 우선순위 0~4 (캐시보다 먼저 내줄 수 있는 메모리), 캐시 재사용량 = 우선순위 5~7 재사용 누계의 증가분.
public static class YieldPressure
{
    /// 시작 때 이만큼 여유분이 있으면 비우지 않아도 됩니다.
    public static long TargetHeadroomBytes(long physicalBytes) => Math.Max(BackgroundYield.HeadroomFloorBytes, physicalBytes / 4);

    /// 조회가 안 되면 동작을 잃지 않게 비웁니다.
    public static bool NeedsTrim(MemoryLists? lists, long physicalBytes) => lists is not { } l || l.HeadroomBytes < TargetHeadroomBytes(physicalBytes);

    /// 마지막 비우기 뒤 RetrimEvery 가 지났고 그사이 캐시가 충분히 재사용됐을 때만. 증가분을 모르면 (조회 실패·기준점 없음) 비우지 않습니다.
    public static bool ShouldRetrim(TimeSpan sinceLastTrim, long? cacheRepurposedBytes) =>
        sinceLastTrim >= BackgroundYield.RetrimEvery && cacheRepurposedBytes >= BackgroundYield.RetrimCacheBytes;

    /// 비우기 직전 작업 집합의 절반 이상을 되찾았으면 활발한 프로세스입니다.
    public static bool RegainedMost(long current, long before) => before > 0 && current * 2 >= before;
}
