using AsterCache.Core.Prefetch;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 표본 확인 (기록용): 게임 시작 때의 만지기나 실행 중 유지 회차가 목록 끝 전에 경계를 찾았을 때, 목록 전체에서 고르게 고른 곳이 메모리에 있는지를 로그 한 줄에 남깁니다.
/// 앞부분만 빠졌는지, 목록 전체가 빠졌는지, 흩어져 빠졌는지를 가르는 데 쓰며 판정·순서·시점에는 쓰지 않습니다. 디스크에서 읽는 양은 Samples × StandbyTouch.ProbeBytes (1 MiB) 안입니다.
internal static class KeepProbe
{
    /// 표본 수: 목록을 이 수로 나눈 칸의 한가운데를 한 곳씩 (각 StandbyTouch.ProbeBytes).
    public const int Samples = 16;

    /// 한 표본을 만지는 동안 이 프로세스의 디스크 읽기 수가 이만큼 이상 늘면 메모리에 없던 것으로 봅니다: 메모리에 있는 범위도 조회 사이에 +1 쯤의 잡음이 있고, 캐시에 없는 64 KiB 블록 하나는 2~3 회 (StandbyTouch 의 실측).
    public const int MissFaults = 2;

    /// 목록 (읽을 범위의 중요도 순서) 의 누적 바이트를 count 등분한 칸의 한가운데에 놓인 64 KiB 범위들과 그 목록 위치 (바이트). 범위 하나의 안에서 잘라 쓰므로 목록의 범위 경계를 넘지 않습니다.
    public static IReadOnlyList<(ReadOp Op, long Offset)> Pick(IReadOnlyList<ReadOp> reads, int count = Samples)
    {
        long total = reads.Sum(r => Math.Max(0, r.Length));
        if (total <= 0 || count <= 0)
        {
            return [];
        }

        List<(ReadOp, long)> picks = [];
        long before = 0; // 지금 범위 앞까지의 누적 바이트
        int index = 0;
        for (int i = 0; i < count; i++)
        {
            long target = (long)((i + 0.5) * total / count);
            while (before + Math.Max(0, reads[index].Length) <= target)
            {
                before += Math.Max(0, reads[index].Length);
                index++;
            }

            ReadOp range = reads[index];
            long start = Math.Max(range.Offset, (range.Offset + (target - before)) & ~((long)StandbyTouch.ProbeBytes - 1));
            picks.Add((new ReadOp(range.Path, start, Math.Min(StandbyTouch.ProbeBytes, range.Offset + range.Length - start)), target));
        }

        return picks;
    }

    /// 로그 꼬리: `probe 5/16 in memory (positions 0 211 422 MiB of 3386 MiB)` (메모리에 있던 표본의 목록 위치). 열지 못한 표본이 있으면 `, 2 not probed` 를 붙입니다.
    public static string Describe(IReadOnlyList<(ReadOp Op, long Offset)> picks, int[] faults, long totalBytes)
    {
        List<long> inMemory = [];
        int probed = 0;
        for (int i = 0; i < picks.Count && i < faults.Length; i++)
        {
            if (faults[i] < 0)
            {
                continue;
            }

            probed++;
            if (faults[i] < MissFaults)
            {
                inMemory.Add(picks[i].Offset >> 20);
            }
        }

        string positions = inMemory.Count == 0 ? $"none of {totalBytes >> 20} MiB" : $"positions {string.Join(' ', inMemory)} MiB of {totalBytes >> 20} MiB";
        return $"probe {inMemory.Count}/{probed} in memory ({positions}){(probed < picks.Count ? $", {picks.Count - probed} not probed" : string.Empty)}";
    }
}
