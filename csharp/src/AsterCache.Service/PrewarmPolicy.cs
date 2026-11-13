namespace AsterCache.Service;

/// 읽는 도중 메모리 한도에 걸렸을 때의 처리: 이 자리에서 마무리 (이미 읽은 양이 들어가는 범위 이상), 줄인 범위까지 계속, 읽은 것이 없어 취소.
public enum MemoryLimitAction
{
    Continue,
    Finish,
    Cancel,
}

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

    /// 지금 가용 메모리에 들어가는 범위 (MiB) = 가용 - 예비량 (0 밑으로 내려가지 않음). 예비량은 낮추지 않고 그대로 뺍니다.
    internal static long MemoryFitMib(long availableRamMib, long reserveMib) => Math.Max(0, availableRamMib - reserveMib);

    /// 읽는 도중 최종 집합이 들어가는 범위 (fitMib) 를 넘었을 때: 아무것도 읽지 못했으면 들어가는 범위가 minUsableMib 이상일 때만 계속 (아니면 취소),
    /// 이미 끝까지 읽었으면 그대로 두고, 읽은 양이 들어가는 범위 이상이면 여기서 마무리, 미만이면 그 범위까지 계속 읽습니다 (취소하지 않음).
    internal static MemoryLimitAction AtMemoryLimit(long doneBytes, long totalBytes, long fitMib, long minUsableMib)
    {
        if (doneBytes <= 0)
        {
            return fitMib >= minUsableMib ? MemoryLimitAction.Continue : MemoryLimitAction.Cancel;
        }

        if (doneBytes >= totalBytes)
        {
            return MemoryLimitAction.Continue;
        }

        return fitMib <= doneBytes >> 20 ? MemoryLimitAction.Finish : MemoryLimitAction.Continue;
    }

    /// 보충 예열을 시작할 만큼 들어가는 범위 (fitMib) 가 이미 읽은 양 (readMib) 보다 의미 있게 큰가: fit - read >= max(minMib, read 의 5%).
    /// 남은 구간 자체가 그보다 작으면 (hot - read) 그 남은 구간 전부가 들어갈 때 시작합니다 (마지막 조각이 영영 안 읽히지 않게).
    internal static bool SupplementHasRoom(long fitMib, long readMib, long hotMib, long minMib)
    {
        long need = Math.Min(Math.Max(minMib, readMib / 20), hotMib - readMib);
        return need > 0 && fitMib - readMib >= need;
    }
}
