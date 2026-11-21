namespace AsterCache.Service;

/// 배경 메모리 양보의 판정 논리: 프로세스·시계 없이 값만으로 결정합니다.
/// 비우기는 가용 메모리 전체에서 게임이 앞으로 가져갈 것으로 보이는 양을 뺀 값이 하한 아래일 때만, 모자라는 만큼만 합니다. 캐시 재사용량 = 우선순위 5~7 재사용 누계의 증가분.
public static class YieldPressure
{
    /// 비운 뒤에도 남아 있어야 하는 가용 메모리의 하한 (게임이 가져간 뒤 기준).
    public static long TargetHeadroomBytes(long physicalBytes) => Math.Max(BackgroundYield.HeadroomFloorBytes, physicalBytes / 4);

    /// 하한 + 게임이 앞으로 가져갈 양 - 지금 가용 메모리: 이만큼을 비워 만들어야 합니다 (0 이면 모자라지 않음).
    public static long ShortfallBytes(long availableBytes, long gameBytesLeft, long floorBytes) => Math.Max(0, floorBytes + gameBytesLeft - availableBytes);

    /// 마지막 비우기 뒤 RetrimEvery 가 지났고 그사이 캐시가 충분히 재사용됐을 때만. 증가분을 모르면 (조회 실패·기준점 없음) 비우지 않습니다.
    public static bool ShouldRetrim(TimeSpan sinceLastTrim, long? cacheRepurposedBytes) =>
        sinceLastTrim >= BackgroundYield.RetrimEvery && cacheRepurposedBytes >= BackgroundYield.RetrimCacheBytes;

    /// 비우기 직전 작업 집합의 절반 이상을 되찾았으면 활발한 프로세스입니다.
    public static bool RegainedMost(long current, long before) => before > 0 && current * 2 >= before;
}
