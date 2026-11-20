namespace AsterCache.Service;

/// 게임이 실행 뒤 스스로 잡는 메모리 (몫) 의 학습: 플레이 기록의 시작 가용 RAM 과 시작 뒤 10 분 안의 최소 가용 RAM 의 차이.
/// 순수 계산만 합니다 (기록 읽기·저장은 PlayRecorder). 기록이 없으면 null: 예열 상한은 지금 동작 그대로입니다.
internal static class GameFootprint
{
    /// 몫 위에 더 남길 여유 (MiB): 예열 상한의 예비 = max(min_free_ram_mib, 몫 + 이 값).
    public const int MarginMib = 1024;

    /// 몫을 셀 최근 플레이 수.
    public const int RecentPlays = 3;

    /// 최소 가용 RAM 을 재는 시작 뒤 구간 (초): 플레이가 이보다 짧으면 몫을 덜 잰 것이라 쓰지 않습니다.
    public const int WindowSeconds = 600;

    /// 기록이 모자랄 때 몫 예비를 예전 예비 쪽으로 끌어오는 기준 (MiB): 예전 고정 예비 (4096) - MarginMib.
    private const long OldReserveBaseMib = 4096 - MarginMib;

    /// 한 플레이의 몫 (MiB), 쓸 수 없는 기록이면 null. 시작 가용이 몫보다 충분히 크지 않아 (최소 가용이 MarginMib 아래) 메모리가 바닥난 플레이는 몫이 잘렸을 수 있어 뺍니다. 몫은 그 플레이의 시작 가용을 넘지 않습니다.
    public static long? Of(PlaySampleRecord r) =>
        !r.ObservedAfterGameStart && r.DurationSeconds >= WindowSeconds && r.AvailableRamMib > 0 && r.MinAvailableRamMib is { } min && min >= MarginMib && r.AvailableRamMib > min
            ? Math.Min(r.AvailableRamMib - min, r.AvailableRamMib)
            : null;

    /// 쓸 수 있는 최근 기록으로 정한 몫 (MiB) 과 쓴 플레이 수, 없으면 null.
    /// 1 개: 한 번의 플레이는 다른 프로그램이 메모리를 잡았거나 게임이 겹친 것일 수 있어 예전 예비와 몫 예비의 중간값이 되는 값을 씀. 2 개: 작은 쪽. 3 개 이상: 최근 3 개의 중앙값.
    public static (long Mib, int Plays)? Median(IEnumerable<PlaySampleRecord> records)
    {
        long[] values = [.. records.Where(r => Of(r) is not null).OrderByDescending(r => r.StartedAt).Take(RecentPlays).Select(r => Of(r)!.Value).Order()];
        return values.Length switch
        {
            0 => null,
            1 => ((values[0] + OldReserveBaseMib) / 2, 1),
            2 => (values[0], 2),
            _ => (values[values.Length / 2], values.Length),
        };
    }

    /// 몫을 아는 게임의 예열 예비 (MiB): max(min_free_ram_mib, 몫 + MarginMib).
    public static long ReserveMib(int minFreeRamMib, long footprintMib) => Math.Max(minFreeRamMib, footprintMib + MarginMib);
}
