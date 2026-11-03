namespace AsterCache.Core.Plan;

/// 게임이 플레이 중 다시 쓰는 파일 (캐시·설정·로그·세이브): 계획에서 빼고 지문 비교에서도 무시합니다. 이름만으로 정하는 규칙 (트레이스에서 알게 된 것은 AutoTracer 의 표시 파일).
public static class VolatileFiles
{
    /// 계획 stats 에 제외한 파일과 사유를 남기는 키.
    public const string StatsKey = "excluded_files";

    public const string NameRule = "volatile file name";

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".log", ".yaml", ".yml", ".ini", ".json", ".txt", ".cfg", ".sav" };

    /// 이름 규칙에 걸리면 사유, 아니면 null: cache* / settings* 로 시작하거나 확장자가 log yaml ini json txt cfg sav.
    public static string? NameReason(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("cache", StringComparison.OrdinalIgnoreCase) || name.StartsWith("settings", StringComparison.OrdinalIgnoreCase) || Extensions.Contains(Path.GetExtension(name))
            ? NameRule : null;
    }

    public static bool Ignored(string path) => NameReason(path) is not null;
}
