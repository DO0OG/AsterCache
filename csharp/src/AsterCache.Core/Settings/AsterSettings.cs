namespace AsterCache.Core.Settings;

/// 게임 한 개의 서비스 설정. 경로는 모두 정규화된 절대 경로 (X:\...), 끝 역슬래시 없음.
public sealed record GameSettings(
    string Name, IReadOnlyList<string> ExeNames, string Trace, string Plan, string OriginRoot, IReadOnlyList<string> AllowedRoots,
    string VolumeLabel, int ReapplyMinutes)
{
    /// 사전 예열 설정 (config.json 의 prewarm 이 없으면 기본값).
    public PrewarmSettings Prewarm { get; init; } = PrewarmSettings.Default;

    /// 한 번의 스트림·예열이 읽는 바이트의 상한 (MiB). 예산은 계획의 존 크기이고 이 값이 위에서 자릅니다.
    public int MaxStreamMib { get; init; } = SettingsStore.DefaultMaxStreamMib;
}

/// 게임 시작 전에 핫 집합을 RAM (파일 캐시) 에 올려 두는 동작. 기본: 서비스 시작 때 한 번, 트리거 프로그램 없음, 가용 RAM 에 4096 MiB 여유, 쿨다운 30분.
public sealed record PrewarmSettings(bool OnServiceStart, IReadOnlyList<string> TriggerExes, int MinFreeRamMib, int CooldownMin)
{
    public static PrewarmSettings Default { get; } = new(true, [], 4096, 30);
}

/// config.json 한 벌 (검증을 통과한 값만 존재).
public sealed record AsterSettings(int PollMs, IReadOnlyList<GameSettings> Games, string Watcher = SettingsStore.DefaultWatcher, string Language = SettingsStore.DefaultLanguage);

/// 설정이 형식·경로 검증을 통과하지 못함. 메시지는 필드 이름만 담고 값(경로)은 담지 않습니다.
public sealed class SettingsException : Exception
{
    public SettingsException(string message) : base(message)
    {
    }
}
