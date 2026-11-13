using AsterCache.Windows;

namespace AsterCache.Service;

/// 런처 프로그램 이름을 한 곳에: 휴리스틱이 게임으로 보지 않는 목록이자, 상주 고정을 푸는 신호 (런처 업데이트가 파일을 고칠 수 있게).
public static class LauncherExes
{
    public static readonly IReadOnlyList<string> All = LauncherScan.LauncherPrograms;

    public static bool Is(string exe) => All.Contains(exe, StringComparer.OrdinalIgnoreCase);

    public const string SteamCommon = @"\steamapps\common\";
    public const string EpicGames = @"\Epic Games\";

    /// 게임 라이브러리·런처·안티치트가 깔리는 폴더 표지 (경로 조각). 배경 프로세스 판정이 이 아래 프로세스를 건드리지 않는 근거이며, 아래 ForOrigin 의 Steam·Epic 표지도 같은 상수를 씁니다.
    public static readonly IReadOnlyList<string> FolderMarkers =
    [
        @"\steamapps\", @"\Steam\", EpicGames, @"\Riot Games\", @"\Riot Vanguard\", @"\Battle.net\", @"\XboxGames\", @"\GOG Galaxy\", @"\EA Games\", @"\Origin Games\", @"\Ubisoft\", @"\EasyAntiCheat", @"\BattlEye",
    ];

    /// 경로가 위 표지 중 하나를 품고 있는가 (끝에 \ 를 붙여 비교해 폴더 이름 끝에서도 일치).
    public static bool InKnownFolder(string path)
    {
        string p = path.TrimEnd('\\') + "\\";
        return FolderMarkers.Any(m => p.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    /// 발견 등록 때 쓸 트리거: Ubisoft 는 두 프로그램, 나머지는 발견 결과가 알려 준 하나.
    public static IReadOnlyList<string> ForLauncher(string launcher, string? discovered) =>
        launcher == "ubisoft" ? ["upc.exe", "UbisoftConnect.exe"] : discovered is null ? [] : [discovered];

    /// 휴리스틱 등록 게임의 트리거: Steam 라이브러리나 Epic 기본 폴더 아래면 그 런처, 아니면 없음.
    public static IReadOnlyList<string> ForOrigin(string originRoot)
    {
        string p = originRoot.TrimEnd('\\') + "\\";
        return p.Contains(SteamCommon, StringComparison.OrdinalIgnoreCase) ? ["steam.exe"]
            : p.Contains(EpicGames, StringComparison.OrdinalIgnoreCase) ? ["EpicGamesLauncher.exe"]
            : [];
    }
}
