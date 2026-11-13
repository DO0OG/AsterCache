namespace AsterCache.Core.Prefetch;

/// 예열이 읽은 목록에서 캐시를 다시 만질 순서를 만듭니다 (판정 논리라 순수 함수).
public static class LadderOrder
{
    /// 예열 순서의 역순: 예열은 중요한 것 (공통 블록, 로딩 앞부분) 을 먼저 읽으므로 가장 중요한 것이 가장 나중에 만져져 가장 나중에 밀려납니다.
    /// 역순에서 바로 이어지는 두 범위가 같은 파일에서 주소가 이어지면 (앞 범위 끝 = 다음 범위 시작) 하나로 합칩니다: 한 범위 안에서는 주소 순서로 만지므로 합쳐도 앞 범위가 먼저, 다음 범위가 나중이라는 순서가 그대로입니다.
    /// 길이가 0 이하인 범위는 버립니다.
    public static List<ReadOp> Reverse(IReadOnlyList<ReadOp> reads)
    {
        var ladder = new List<ReadOp>();
        for (int i = reads.Count - 1; i >= 0; i--)
        {
            ReadOp op = reads[i];
            if (op.Length <= 0)
            {
                continue;
            }

            if (ladder.Count > 0 && Continues(ladder[^1], op))
            {
                ladder[^1] = ladder[^1] with { Length = ladder[^1].Length + op.Length };
            }
            else
            {
                ladder.Add(op);
            }
        }

        return ladder;
    }

    private static bool Continues(ReadOp last, ReadOp next) =>
        string.Equals(last.Path, next.Path, StringComparison.OrdinalIgnoreCase) && last.Offset + last.Length == next.Offset;
}
