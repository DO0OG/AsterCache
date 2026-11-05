using AsterCache.Core.Json;
using AsterCache.Core.Trace;

namespace AsterCache.Core.Plan;

/// 계획 파일과 그 옆의 선읽기 입력 (<계획>.prefetch.json) 을 같은 바이트로 씁니다 (aster plan 과 서비스 자동 모드가 공유).
public static class PlanOutput
{
    public const string PrefetchSuffix = ".prefetch.json";

    /// 가변 파일 범위의 예열 전용 목록 (선읽기 파일 옆). 게임 시작 스트림·표본·고정은 읽지 않고 시작 전 예열만 합쳐 읽습니다.
    public const string WarmOnlySuffix = ".warmonly.json";

    public static string WarmOnlyPath(string prefetchPath) => prefetchPath + WarmOnlySuffix;

    /// 쓴 선읽기 파일의 경로를 돌려줍니다.
    public static string Save(PlanFile plan, IReadOnlyList<IReadOnlyList<TraceEvent>> sessions, string outPath)
    {
        plan.Save(outPath);
        string prefetchPath = outPath + PrefetchSuffix;
        SavePrefetch(prefetchPath, plan.PrefetchTrace(sessions));
        return prefetchPath;
    }

    /// 계획 파일은 그대로 두고 읽기 전용 예열 목록만 씁니다. warmOnly 는 옆 파일에 따로 쓰고, 비어 있으면 옛 옆 파일을 지웁니다.
    public static void SavePrefetch(string prefetchPath, IReadOnlyList<TraceEvent> rows, IReadOnlyList<TraceEvent>? warmOnly = null)
    {
        WriteRows(prefetchPath, rows);
        if (warmOnly is { Count: > 0 })
        {
            WriteRows(WarmOnlyPath(prefetchPath), warmOnly);
        }
        else
        {
            File.Delete(WarmOnlyPath(prefetchPath));
        }
    }

    private static void WriteRows(string path, IReadOnlyList<TraceEvent> rows)
    {
        string json = PyJson.Dumps(rows.Select(e => e.ToJson()).ToList(), ensureAscii: false) + "\n";
        string tmp = path + ".tmp";
        DurableFile.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(PyJson.ToCrlf(json)));
        File.Move(tmp, path, true);
    }
}
