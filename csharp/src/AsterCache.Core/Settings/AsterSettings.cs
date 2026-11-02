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

    /// 등록 출처: "manual" (사용자가 직접, 기본) 또는 "auto" (발견·감지로 자동 등록, 항목이 꽉 차면 밀려날 수 있음).
    public string Source { get; init; } = SettingsStore.DefaultSource;

    /// 설치 폴더에서 알아낸 안티치트: "eac" | "battleye" | "vanguard" | "ricochet" | "unknown", 모르면 null.
    public string? AntiCheat { get; init; }
}

/// 게임 시작 전에 핫 집합을 RAM (파일 캐시) 에 올려 두는 동작. 기본: 서비스 시작 때 한 번, 트리거 프로그램 없음, 가용 RAM 에 4096 MiB 여유, 쿨다운 30분.
public sealed record PrewarmSettings(bool OnServiceStart, IReadOnlyList<string> TriggerExes, int MinFreeRamMib, int CooldownMin)
{
    public static PrewarmSettings Default { get; } = new(true, [], 4096, 30);

    /// 예열한 파일을 읽기 전용 매핑으로 계속 들고 있어 파일 캐시에서 밀려나지 않게 함 (기본 꺼짐, 실측 전).
    public bool Pin { get; init; }
}

/// 자동 모드 (config.json 의 auto 가 없으면 기본값): 런처 발견, 처음 보는 게임의 트레이스, 재배치와 최근 게임 예열을 사용자가 손대지 않아도 하게 하는 설정.
public sealed record AutoSettings(
    bool Enabled, bool Discover, bool Trace, bool Relocate, int PrewarmRecent, int IdleMinutes, int TraceCapSeconds, int TraceCapMib,
    int MaxRelocationsPerDay, IReadOnlyList<string> IgnoreExes)
{
    /// 새 프로세스가 이 시간 안에 이 만큼 읽으면 게임 후보 (설정 키가 아니라 고정값).
    public const int HeuristicWindowSeconds = 20;
    public const int HeuristicReadMib = 256;

    public static AutoSettings Default { get; } = new(true, true, true, true, 1, 3, 600, 6144, 2, []);

    /// 게임을 하는 동안 이 간격 (분) 으로 핫 집합을 다시 데움, 0 = 끔.
    public int RewarmMinutes { get; init; } = 10;
}

/// config.json 한 벌 (검증을 통과한 값만 존재).
public sealed record AsterSettings(int PollMs, IReadOnlyList<GameSettings> Games, string Watcher = SettingsStore.DefaultWatcher, string Language = SettingsStore.DefaultLanguage)
{
    public AutoSettings Auto { get; init; } = AutoSettings.Default;

    /// 자동 재배치가 한 존에 쓰는 크기의 상한 (MiB).
    public int MaxZoneMib { get; init; } = SettingsStore.DefaultMaxZoneMib;

    /// max_zone_mib 가 "auto" 이면 true (RAM 에 맞춰 ZoneSize.AutoMaxMib 로 계산, MaxZoneMib 는 기본값 그대로).
    public bool MaxZoneAuto { get; init; }
}

/// 설정이 형식·경로 검증을 통과하지 못함. 메시지는 필드 이름만 담고 값(경로)은 담지 않습니다.
public sealed class SettingsException : Exception
{
    public SettingsException(string message) : base(message)
    {
    }
}
