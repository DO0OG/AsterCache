using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 예열 상한이 모자라 메모리 예열에서 스파스 파일 범위를 뺀 게임 (GameSettings.SkipSparse) 의 읽기 목록 처리: 사다리는 스파스 범위를 만지지 못해 그 페이지가 가장 먼저 밀려나므로, 상한 안에서는 일반 파일 범위에 양을 씁니다.
/// 표시는 게임 사본에 실려 다니므로 다른 게임·다른 스레드의 계획에는 영향이 없습니다. 예열·표본·고정 목록에만 적용하고 시작 스트림·재배치 계획은 표시 없는 원래 게임을 씁니다.
internal static class SparseWarmExclusion
{
    /// 표시가 있는 게임이면 읽기 목록에서 스파스 파일의 구간을 뺍니다.
    public static List<ReadOp> Without(GameSettings game, List<ReadOp> ops)
    {
        if (!game.SkipSparse)
        {
            return ops;
        }

        var sparse = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        return [.. ops.Where(op => !(sparse.TryGetValue(op.Path, out bool s) ? s : sparse[op.Path] = FileGuards.IsSparse(op.Path)))];
    }
}
