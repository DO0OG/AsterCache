namespace AsterCache.Core.Discovery;

/// 게임이 아니고 한 번 띄우면 상주하며 다시 큰 읽기를 하지 않는 데스크톱 유틸리티의 고정 목록 (자동 등록 제외용, 설정과 별개).
/// appid 는 Steam 상점 페이지의 제품 이름으로 확인한 것만, 실행 파일 이름은 그 제품 고유의 것만 넣습니다 (흔한 이름은 실제 게임을 제외시킴).
public static class ResidentUtilities
{
    private static readonly (string AppId, string Product, string[] Exes)[] Table =
    [
        ("431960", "Wallpaper Engine", ["wallpaper32.exe", "wallpaper64.exe", "wallpaperui.exe"]),
        ("1283970", "YoloMouse", ["YoloMouse.exe"]),
        ("993090", "Lossless Scaling", ["LosslessScaling.exe"]),
        ("388080", "Borderless Gaming", []),
        ("629520", "Soundpad", []),
        ("1366800", "Crosshair X", []),
        ("1027220", "DeskScapes", []),
        ("1905180", "OBS Studio", ["obs64.exe"]),
        ("250820", "SteamVR", ["vrserver.exe", "vrmonitor.exe", "vrcompositor.exe"]),
        ("908520", "fpsVR", []),
        ("1068820", "OVR Toolkit", []),
        ("1173510", "XSOverlay", []),
        ("1325860", "VTube Studio", []),
        ("3301060", "Desktop Mate", []),
        ("3419430", "Bongo Cat", []),
    ];

    private static readonly HashSet<string> ExeNames = new(Table.SelectMany(t => t.Exes), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> AppIds { get; } = [.. Table.Select(t => t.AppId)];

    public static bool IsAppId(string appId) => AppIds.Contains(appId);

    public static bool IsExe(string exe) => ExeNames.Contains(exe);

    /// appmanifest 본문의 installdir 이 dirName (대소문자 무시) 이면 true. 형식이 틀리면 false.
    public static bool OwnsInstallDir(string manifestText, string dirName)
    {
        try
        {
            return LauncherManifests.ParseKeyValues(manifestText).GetValueOrDefault("AppState") is Dictionary<string, object> s
                && s.GetValueOrDefault("installdir") is string dir && string.Equals(dir, dirName, StringComparison.OrdinalIgnoreCase);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
