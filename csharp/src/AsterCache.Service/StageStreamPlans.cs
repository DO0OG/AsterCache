using AsterCache.Core.Prefetch;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;

namespace AsterCache.Service;

/// 첫 실행 파일의 단계 스트림 계획: Ops = 앞부분 + 첫 단계 블록, AheadOps = 첫 단계가 끝난 뒤 낮은 우선순위로 미리 흘릴 다음 단계 블록 (AheadExe 가 뜨면 멈춤).
public sealed record StageFirstPlan(IReadOnlyList<ReadOp> Ops, string AheadExe, IReadOnlyList<ReadOp> AheadOps, long VolatileSkippedBytes);

/// 이후 실행 파일의 단계 스트림 계획: HasStage 가 거짓이면 단계를 모르는 실행 파일이라 시작 목록 전체이고, 흘리기 전에 읽기 증거를 요구합니다.
public sealed record StageLaterPlan(IReadOnlyList<ReadOp> Ops, bool HasStage, long VolatileSkippedBytes);

/// 시작 목록과 단계 색인으로 단계 스트림이 읽을 목록을 고르는 순수 함수입니다. 읽는 블록은 늘 시작 목록의 부분집합입니다.
internal static class StageStreamPlans
{
    /// 읽기 목록을 이 크기 이하 조각으로 자릅니다: FolderWarm 은 묶음을 64 MiB 이상이 모일 때까지 op 단위로 모으므로, 조각이 이 크기면 묶음은 GateBatchBytes 보다 작습니다.
    public const long PieceBytes = FolderWarm.BatchBytes / 2;

    /// 메모리 게이트가 다음 묶음으로 보는 크기 (묶음이 나올 수 있는 최대).
    public const long GateBatchBytes = FolderWarm.BatchBytes + PieceBytes;

    public static List<ReadOp> Split(IReadOnlyList<ReadOp> ops)
    {
        var pieces = new List<ReadOp>(ops.Count);
        foreach (ReadOp op in ops)
        {
            long done = 0;
            while (op.Length - done > PieceBytes)
            {
                pieces.Add(new ReadOp(op.Path, op.Offset + done, PieceBytes));
                done += PieceBytes;
            }

            pieces.Add(done == 0 ? op : new ReadOp(op.Path, op.Offset + done, op.Length - done));
        }

        return pieces;
    }

    public static StageFirstPlan? First(IReadOnlyList<ReadOp> ops, long headBytes, TraceStageIndex? index, IReadOnlySet<string> volatilePaths)
    {
        if (index is null || index.Stages.Count < 2 || StageStreamRule.FirstStageOps(ops, headBytes, index) is not { } first)
        {
            return null;
        }

        List<ReadOp> ahead = StageStreamRule.StageOps(ops, index.Stages[1], volatilePaths);
        return new StageFirstPlan(first, index.Stages[1].Exe, ahead, StageVolatileBytes(ops, index.Stages[1], volatilePaths));
    }

    public static StageLaterPlan Later(IReadOnlyList<ReadOp> ops, TraceStageIndex? index, string exe, IReadOnlySet<string> volatilePaths)
    {
        if (index is { Stages.Count: >= 2 })
        {
            // 첫 단계 이름과 같은 실행 파일은 첫 단계 (StageOf 는 단계 1 이상만 찾음), 이름이 빈 첫 단계는 이름 대응이 안 되어 증거 경로로 감
            int? stage = index.StageOf(exe) ?? (index.Stages[0].Exe.Length > 0 && string.Equals(index.Stages[0].Exe, exe, StringComparison.OrdinalIgnoreCase) ? 0 : null);
            if (stage is { } k)
            {
                return new StageLaterPlan(StageStreamRule.StageOps(ops, index.Stages[k], volatilePaths), true, StageVolatileBytes(ops, index.Stages[k], volatilePaths));
            }
        }

        return new StageLaterPlan([.. ops.Where(op => !volatilePaths.Contains(op.Path))], false, StageStreamRule.SkippedVolatileBytes(ops, volatilePaths));
    }

    // 그 단계의 블록 중 가변 파일이라 건너뛴 양 (단계와 무관한 가변 파일은 세지 않음)
    private static long StageVolatileBytes(IReadOnlyList<ReadOp> ops, TraceStage stage, IReadOnlySet<string> volatilePaths) =>
        StageStreamRule.SkippedVolatileBytes(StageStreamRule.StageOps(ops, stage), volatilePaths);
}
