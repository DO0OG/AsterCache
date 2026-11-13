using AsterCache.Core.Discovery;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 배경 메모리 양보가 건드리지 않을 프로세스를 가르는 순수 판정. 이미 있는 런처·안티치트 분류의 확장이며 개별 게임 이름은 넣지 않습니다.
/// 이름 단계는 프로세스를 열기 전에, 경로 단계는 조회 권한 (0x1000) 으로 경로만 얻은 뒤에 부릅니다. 제외 대상은 쓰기 권한으로 열지 않습니다.
public static class BackgroundProcessRules
{
    /// 등록 게임 전체의 ExeNames·TriggerExes 합집합 (대소문자 무시).
    public static HashSet<string> GameExes(IEnumerable<GameSettings> games) =>
        new(games.SelectMany(g => g.ExeNames.Concat(g.Prewarm.TriggerExes)), StringComparer.OrdinalIgnoreCase);

    /// 이름만 보고 제외: 시스템 pid, 서비스 자신, 등록 게임·트리거, 런처와 런처 보조, 안티치트·Riot 구성요소, AsterCache 프로세스.
    public static bool ExcludedByName(string exe, int pid, int selfPid, IReadOnlySet<string> gameExes) =>
        pid <= 4 || pid == selfPid || gameExes.Contains(exe) || LauncherExes.Is(exe) || AutoRegistry.NotGames.Contains(exe)
        || AntiCheat.IsProcessName(exe) || AntiCheat.IsRiotExe(exe) || exe.StartsWith("AsterCache.", StringComparison.OrdinalIgnoreCase);

    /// 이미지 경로로 제외: Windows 폴더, 등록 게임의 설치·허용 폴더, 알려진 게임 라이브러리·런처·안티치트 폴더, 런처 탐색이 이미 알고 있는 게임 설치 폴더 아래.
    public static bool ExcludedByPath(string imagePath, string windowsDir, IEnumerable<GameSettings> games, IEnumerable<string>? knownGameFolders = null) =>
        Under(imagePath, windowsDir) || LauncherExes.InKnownFolder(imagePath)
        || games.Any(g => Under(imagePath, g.OriginRoot) || g.AllowedRoots.Any(r => Under(imagePath, r)))
        || (knownGameFolders?.Any(f => Under(imagePath, f)) ?? false);

    // 루트가 "D:\" 처럼 끝에 \ 가 있어도 같게 비교 (그러면 그 드라이브 전체가 제외).
    private static bool Under(string path, string root) =>
        root.Length > 0 && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
