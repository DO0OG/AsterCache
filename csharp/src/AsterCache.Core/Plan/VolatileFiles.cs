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

    /// 계획 stats 에 계획을 만들 때 반영한 트레이스 가변 표시 수를 남기는 키: 표시가 이보다 늘면 계획이 새 표시 파일을 들고 있는지 다시 봅니다.
    public const string MarksKey = "volatile_marks";

    /// 게임이 플레이 중에 스스로 쓰는 파일인가: 트레이스가 가변으로 표시한 파일 (쓰기로 열었거나 플레이 사이에 수정됨, 내장 브라우저 캐시는 제외).
    /// "opened for write" 만으로 가르지 않는 까닭: 게임이 한 플레이에서 쓰기를 기록 구간 밖에서 하거나 그 플레이에서 쓰지 않은 파일은 쓰기 표시가 아니라 "changed between runs" 로 남습니다
    /// (2026-11-21 Call of Duty 의 xpak_cache 데이터 파일 8 개 중 6 개). 업데이트로 바뀐 파일도 같은 표시라, 붙들기는 이 판정 말고도 크기 기록·업데이트 판정·변경 확인을 따로 요구합니다.
    public static bool IsGameWritten(string path, IReadOnlyDictionary<string, string> marked) => marked.ContainsKey(path) && NameReason(path) != BrowserCacheRule;

    /// 이름 규칙 또는 트레이스 가변 표시 (경로 → 사유) 에 걸리면 사유, 아니면 null: 계획 입력에서 빼는 판정과 업데이트 판정이 같은 기준을 쓰게 하는 하나의 함수.
    public static string? Reason(string path, IReadOnlyDictionary<string, string> marked) => NameReason(path) ?? (marked.TryGetValue(path, out string? why) ? why : null);
}
