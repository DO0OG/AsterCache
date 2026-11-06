namespace AsterCache.Core.Plan;

/// 게임이 플레이 중 다시 쓰는 파일 (캐시·설정·로그·세이브): 계획에서 빼고 지문 비교에서도 무시합니다. 이름만으로 정하는 규칙 (트레이스에서 알게 된 것은 AutoTracer 의 표시 파일).
public static class VolatileFiles
{
    /// 계획 stats 에 제외한 파일과 사유를 남기는 키.
    public const string StatsKey = "excluded_files";

    public const string NameRule = "volatile file name";

    public const string BrowserCacheRule = "embedded browser cache";

    /// 이 규칙 버전보다 낮은 (또는 표지가 없는) 계획이 새 규칙에 걸리는 파일을 들고 있으면 한 번 다시 계획합니다 (계획 stats 의 표지 키).
    public const string RuleKey = "volatile_rule";

    public const long RuleVersion = 2;

    // 내장 브라우저 (Chromium 계열) 의 캐시·프로필 폴더 이름: 경로 조각이 이 이름이면 그 아래 전부가 가변 파일입니다. 게임 데이터 폴더 이름 (셰이더 캐시 등) 은 넣지 않습니다.
    private static readonly HashSet<string> BrowserFolders = new(StringComparer.OrdinalIgnoreCase) { "webcache", "gpucache", "code cache" };

    // 폴더 이름과 상관없이 내장 브라우저의 프로필 파일로만 쓰이는 이름.
    private static readonly HashSet<string> BrowserFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Network Persistent State", "TransportSecurity", "Visited Links", "the-real-index", "Cookies", "Cookies-journal", "Reporting and NEL", "Reporting and NEL-journal",
    };

    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase) { ".log", ".yaml", ".yml", ".ini", ".json", ".txt", ".cfg", ".sav" };

    /// 이름 규칙에 걸리면 사유, 아니면 null: cache* / settings* 로 시작하거나 확장자가 log yaml ini json txt cfg sav, 또는 내장 브라우저 캐시 (폴더 조각이 webcache·gpucache·code cache, 또는 브라우저 프로필 파일 이름).
    public static string? NameReason(string path)
    {
        string name = Path.GetFileName(path);
        if (BrowserFiles.Contains(name) || (Path.GetDirectoryName(path) ?? string.Empty).Split('\\', '/').Any(BrowserFolders.Contains))
        {
            return BrowserCacheRule;
        }

        return name.StartsWith("cache", StringComparison.OrdinalIgnoreCase) || name.StartsWith("settings", StringComparison.OrdinalIgnoreCase) || Extensions.Contains(Path.GetExtension(name))
            ? NameRule : null;
    }

    public static bool Ignored(string path) => NameReason(path) is not null;
}
