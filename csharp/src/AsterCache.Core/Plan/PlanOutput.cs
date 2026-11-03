using AsterCache.Core.Json;
using AsterCache.Core.Trace;

namespace AsterCache.Core.Plan;

/// 계획 파일과 그 옆의 선읽기 입력 (<계획>.prefetch.json) 을 같은 바이트로 씁니다 (aster plan 과 서비스 자동 모드가 공유).
public static class PlanOutput
{
    public const string PrefetchSuffix = ".prefetch.json";

    /// 쓴 선읽기 파일의 경로를 돌려줍니다.
    public static string Save(PlanFile plan, IReadOnlyList<IReadOnlyList<TraceEvent>> sessions, string outPath)
    {
        plan.Save(outPath);
        string prefetchPath = outPath + PrefetchSuffix;
        string rows = PyJson.Dumps(plan.PrefetchTrace(sessions).Select(e => e.ToJson()).ToList(), ensureAscii: false) + "\n";
        string tmp = prefetchPath + ".tmp";
        DurableFile.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(PyJson.ToCrlf(rows)));
        File.Move(tmp, prefetchPath, true);
        return prefetchPath;
    }
}
