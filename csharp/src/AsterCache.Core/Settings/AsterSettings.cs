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

/// 첫 실행 학습·선행 읽기 설정 (auto.learn). max_total_mib 0 = 자동 (계산은 서비스가 함).
public sealed record LearnSettings(bool Enabled, int WindowMib, int MaxTotalMib)
{
    public const string StreamMode = "stream";
    public const string GapMode = "gap";

    public static LearnSettings Default { get; } = new(false, 512, 0); // E7 재측정 전까지 기본 꺼짐

    /// "stream" (기본): 순차 판정된 파일은 틈을 기다리지 않고 앞쪽을 바로 읽음. "gap": 게임이 쉬는 틈에만 읽음.
    public string Mode { get; init; } = StreamMode;
}

/// 자동 모드 (config.json 의 auto 가 없으면 기본값): 런처 발견, 처음 보는 게임의 트레이스, 재배치와 최근 게임 예열을 사용자가 손대지 않아도 하게 하는 설정.
public sealed record AutoSettings(
    bool Enabled, bool Discover, bool Trace, bool Relocate, int PrewarmRecent, int IdleMinutes, int TraceCapSeconds, int TraceCapMib,
    int MaxRelocationsPerDay, IReadOnlyList<string> IgnoreExes)
{
    /// 새 프로세스가 이 시간 안에 이 만큼 읽으면 게임 후보 (설정 키가 아니라 고정값).
    public const int HeuristicWindowSeconds = 20;
    public const int HeuristicReadMib = 256;

    /// 기본 prewarm_recent 0 = RAM 예산 안에서 최근 순으로 가능한 만큼 (1 이상이면 그 개수 상한, -1 = 최근순 예열 끔).
    public static AutoSettings Default { get; } = new(true, true, true, true, 0, 3, 600, 6144, 2, []);

    /// 게임을 하는 동안 이 간격 (분) 으로 핫 집합을 다시 데움, 0 = 끔. 식었는지는 핫 집합 8 곳 (앞쪽 4 + 균등 4) 을 읽어 보고, 2 곳 이상이 2 ms 를 넘으면 식은 것으로 봅니다.
    public int RewarmMinutes { get; init; } = 10;

    /// beta.3 형식 설정 (learn 키 없음, prewarm_recent 1 + idle_minutes 3) 을 새 기본으로 해석했는지 (파싱 때만 설정, 서비스가 시작 때 로그 한 줄).
    public bool Migrated { get; init; }

    /// 유휴 대기 (초, 10..86400). idle_seconds 가 있으면 (또는 둘 다 없으면) 이 값이 idle_minutes 보다 우선.
    public int IdleSeconds { get; init; } = 30;

    /// config 에 idle_seconds 키가 있었는지 (파싱 때만 설정, IdleWindow 판정용).
    public bool IdleSecondsSet { get; init; }

    /// config 에 idle_minutes 키가 있었는지 (파싱 때만 설정, IdleWindow 판정용).
    public bool IdleMinutesSet { get; init; }

    /// 기록 (trace) 이 없는 게임: 설치 폴더 합이 상한 이하면 폴더 전체를 예열.
    public bool FullPrewarm { get; init; } = true;

    /// 통째 예열의 폴더 크기 상한 (MiB, 0..1048576). 0 = RAM 예산 (물리 RAM 의 1/4 과 가용 RAM - 여유 중 작은 값).
    public int FullPrewarmMaxMib { get; init; }

    /// 기록이 없고 폴더가 상한보다 크면: 엔진 (Unity·Unreal·Source) 을 감지해 로딩 초반 파일 집합만 예열.
    public bool EngineProfiles { get; init; } = true;

    /// 첫 실행 학습·선행 읽기 설정.
    public LearnSettings Learn { get; init; } = LearnSettings.Default;

    /// 유휴 대기 시간: idle_seconds 가 명시됐거나 둘 다 없으면 IdleSeconds 초, idle_minutes 만 명시됐으면 IdleMinutes 분.
    public TimeSpan IdleWindow => IdleSecondsSet || !IdleMinutesSet ? TimeSpan.FromSeconds(IdleSeconds) : TimeSpan.FromMinutes(IdleMinutes);
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
