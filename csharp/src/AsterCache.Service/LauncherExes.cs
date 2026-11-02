using AsterCache.Windows;

namespace AsterCache.Service;

/// 런처 프로그램 이름을 한 곳에: 휴리스틱이 게임으로 보지 않는 목록이자, 상주 고정을 푸는 신호 (런처 업데이트가 파일을 고칠 수 있게).
public static class LauncherExes
{
    public static readonly IReadOnlyList<string> All = LauncherScan.LauncherPrograms;

    public static bool Is(string exe) => All.Contains(exe, StringComparer.OrdinalIgnoreCase);

    /// 발견 등록 때 쓸 트리거: Ubisoft 는 두 프로그램, 나머지는 발견 결과가 알려 준 하나.
    public static IReadOnlyList<string> ForLauncher(string launcher, string? discovered) =>
        launcher == "ubisoft" ? ["upc.exe", "UbisoftConnect.exe"] : discovered is null ? [] : [discovered];

    /// 휴리스틱 등록 게임의 트리거: Steam 라이브러리나 Epic 기본 폴더 아래면 그 런처, 아니면 없음.
    public static IReadOnlyList<string> ForOrigin(string originRoot)
    {
        string p = originRoot.TrimEnd('\\') + "\\";
        return p.Contains(@"\steamapps\common\", StringComparison.OrdinalIgnoreCase) ? ["steam.exe"]
            : p.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase) ? ["EpicGamesLauncher.exe"]
            : [];
    }
}
