namespace AsterCache.Core.Settings;

/// 게임별 정책 판정 (순수 함수). 전역 스위치·안티치트 게이트는 호출하는 쪽이 따로 AND 로 봅니다.
public static class GamePolicy
{
    /// 예열 계열 (예열·재예열·보충·시작 스트림·고정) 을 해도 되는가.
    public static bool AllowsPrewarm(GameSettings g) => !g.Paused && g.Mode is GameMode.Inherit or GameMode.PrewarmOnly;

    /// 재배치 계열 (자동 재배치·주기 재적용) 을 해도 되는가.
    public static bool AllowsRelocate(GameSettings g) => !g.Paused && g.Mode is GameMode.Inherit or GameMode.RelocateOnly;

    /// 추적 (트레이스 기록) 을 해도 되는가: 끄거나 일시 중지한 게임만 불허.
    public static bool AllowsTracking(GameSettings g) => !g.Paused && g.Mode != GameMode.Off;

    /// relocate_hours 가 비어 있거나 hour (로컬 시각의 시) 가 들어 있으면 지금 재배치를 시작해도 됩니다.
    public static bool AllowsRelocateAt(AutoSettings a, int hour) => a.RelocateHours.Count == 0 || a.RelocateHours.Contains(hour);

    /// config 에 쓰는 mode 문자열 (inherit / prewarm_only / relocate_only / off).
    public static string ModeName(GameMode mode) => mode switch
    {
        GameMode.Inherit => "inherit",
        GameMode.PrewarmOnly => "prewarm_only",
        GameMode.RelocateOnly => "relocate_only",
        GameMode.Off => "off",
        _ => throw new SettingsException("games.mode: unknown mode"),
    };

    /// config 에 쓰는 update.mode 문자열 (auto / notify / off).
    public static string UpdateModeName(UpdateMode mode) => mode switch
    {
        UpdateMode.Auto => "auto",
        UpdateMode.Notify => "notify",
        UpdateMode.Off => "off",
        _ => throw new SettingsException("update.mode: unknown mode"),
    };
}
