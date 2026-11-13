namespace AsterCache.Service;

/// 예열 판단 (순수 함수): 건너뛸 이유, 없으면 null. 순서: 계획·추적 파일 → 쿨다운 → 게임 실행 중 → 가용 RAM (존 크기 + 여유).
public static class PrewarmPolicy
{
    public static string? SkipReason(long? zoneBytes, bool traceExists, bool gameRunning, long availableRamMib, int minFreeRamMib, TimeSpan? sinceLast, TimeSpan cooldown)
    {
        if (zoneBytes is null)
        {
            return "no plan file";
        }

        if (zoneBytes == 0)
        {
            return "no zone";
        }

        if (!traceExists)
        {
            return "no trace file";
        }

        if (sinceLast is { } since && since < cooldown)
        {
            return $"cooldown ({Math.Ceiling((cooldown - since).TotalMinutes)} min left)";
        }

        if (gameRunning)
        {
            return "game is running";
        }

        long zoneMib = (zoneBytes.Value + (1 << 20) - 1) >> 20;
        return !HasMemoryBudget(zoneMib, availableRamMib, minFreeRamMib)
            ? $"low memory (available {availableRamMib} MiB < zone {zoneMib} MiB + reserve {minFreeRamMib} MiB)"
            : null;
    }

    internal static bool HasMemoryBudget(long zoneMib, long availableRamMib, int minFreeRamMib) =>
        availableRamMib >= zoneMib + minFreeRamMib;
}
