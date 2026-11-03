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

    /// config 에 pin 키가 있었는지 (파싱 때만 설정). 없으면 물리 RAM 이 충분할 때 서비스가 자동으로 켭니다 (PinKeeper.Wanted).
    public bool PinSet { get; init; }
}

/// 첫 실행 학습·선행 읽기 설정 (auto.learn). max_total_mib 0 = 자동 (계산은 서비스가 함).
public sealed record LearnSettings(bool Enabled, int WindowMib, int MaxTotalMib)
{
    public const string StreamMode = "stream";
    public const string GapMode = "gap";
    public const string BoundedMode = "bounded";
    public const string AdaptiveMode = "adaptive";
    public const int DefaultSaturationMbps = 80;

    public static LearnSettings Default { get; } = new(false, 512, 0); // E7 재측정 전까지 기본 꺼짐

    /// "stream" (기본): 순차 판정된 파일은 틈을 기다리지 않고 앞쪽을 바로 읽음. "gap": 게임이 쉬는 틈에만 읽음.
    /// "bounded": stream 처럼 16 MiB 씩 읽되 총량은 게임 읽기 x 1.0 + 32 MiB 까지, 게임 읽기 알림 뒤 50 ms 는 새 요청을 내지 않음.
    /// "adaptive": bounded 규칙 위에, 최근 500 ms 게임 읽기 처리량이 saturation_mbps 이상이면 선행을 쉬고 우리 요청 지연이 2 배로 오르면 200 ms 쉼.
    public string Mode { get; init; } = StreamMode;

    /// adaptive 포화 기준 (MiB/s, 1..10000). 80 = HDD 순차 135~170 MB/s 의 약 50%.
    public int SaturationMbps { get; init; } = DefaultSaturationMbps;
}

/// 공유 프로필 묶음 내려받기 (auto.shared_profiles): 주 1 회 묶음 zip 하나만 받음. url 은 https 만.
public sealed record SharedProfilesSettings(bool Enabled, string Url)
{
    public const string DefaultUrl = "https://github.com/DO0OG/AsterCache-profiles/releases/latest/download/profiles.zip";

    public static SharedProfilesSettings Default { get; } = new(true, DefaultUrl);
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
    public static AutoSettings Default { get; } = new(true, true, true, true, 0, 3, 600, 6144, 4, []);

    /// 한 게임이 모을 트레이스 플레이 횟수 (1..32): 이보다 적게 모았으면 원시 트레이스가 있어도 다음 플레이를 다시 기록해 합집합으로 병합합니다. 1 = 첫 플레이만.
    public int TraceRuns { get; init; } = 3;

    /// 병합한 트레이스의 핫 집합이 지금 계획보다 이만큼 (MiB, 1..65536) 이상 늘었을 때만 존을 확장해 다시 계획합니다.
    public int TraceMergeMinMib { get; init; } = 64;

    /// 게임을 하는 동안 이 간격 (분) 으로 핫 집합을 다시 데움, 0 = 끔. 식었는지는 핫 집합 8 곳 (앞쪽 4 + 균등 4) 을 읽어 보고, 2 곳 이상이 2 ms 를 넘으면 식은 것으로 봅니다.
    public int RewarmMinutes { get; init; } = 10;

    /// 같은 게임의 시작 스트림 사이 최소 간격 (분, 0..1440), 0 = 끔. 게임이 이미 돌거나 그 디스크가 바쁠 때도 건너뜁니다 (같은 데이터를 디스크에서 또 읽어 게임이 끊기지 않게).
    public int RestreamCooldownMin { get; init; } = 10;

    /// 게임 종료를 감지하면 그 게임의 재예열 점검을 이만큼 (분, 0..1440) 뒤로 당김, 0 = 끔 (rewarm_minutes 가 0 이면 재예열 자체가 꺼져 있음). 점검은 식었을 때만 다시 읽습니다.
    public int RewarmAfterExitMin { get; init; } = 2;

    /// 게임 업데이트를 감지한 뒤 그 게임의 재계획·재배치·예열을 이만큼 (분, 0..1440) 미룸, 0 = 지연 없음. 업데이트 직후에는 드라이브가 파일을 흩어 쓰는 중일 수 있습니다.
    public int PostUpdateDelayMin { get; init; } = 15;

    /// probe 회복 대기의 상한 (분, 1..1440). post_update_delay_min 과 따로 두어, 그 값이 0 이어도 회복 대기는 꺼지지 않습니다. 기본 60 (예전 15 x 4).
    public int RecoveryMaxMin { get; init; } = 60;

    public const string RecoveryProbe = "probe";
    public const string RecoveryFixed = "fixed";
    public const string RecoveryOff = "off";

    /// 재배치(쓰기) 뒤 같은 드라이브의 다음 작업을 미루는 방식: "probe" (기본, 읽기 속도를 재서 회복을 확인, 최대 post_update_delay_min x4 분), "fixed" (post_update_delay_min 분 고정) 또는 "off" (기다리지 않음).
    public string PostRelocateRecovery { get; init; } = RecoveryProbe;

    /// HDD 게임 드라이브가 디스크 끄기 시간 뒤 잠들지 않게, 런처가 떠 있거나 지금이 플레이하던 시간대면 10 분마다 4 KiB 를 읽음 (기본 켜짐, 전원 설정은 바꾸지 않음).
    public bool KeepAwake { get; init; } = true;

    /// 게임이 시작될 때 그 게임을 읽던 예열 스트림을 끝까지 읽게 할지 (기본 꺼짐, 실험용): 꺼져 있으면 다음 64 MiB 묶음 경계에서 멈춰 게임에 디스크를 양보함.
    public bool KeepStreamOnGameStart { get; init; }

    /// 게임 시작 때 로딩 경계 (tier0) 가 RAM 에 들어온 시각을 재는 감시 (기본 꺼짐): 디스크를 읽으므로 게임이 끝나면 바로 멈춤.
    public bool LoadBoundary { get; init; }

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

    /// 통째 예열의 폴더 크기 상한 (MiB, 0..1048576). 0 = 기본 1024 MiB (RAM 예산, 즉 물리 RAM 의 1/4 과 가용 RAM - 여유 중 작은 값 안에서).
    public int FullPrewarmMaxMib { get; init; }

    /// 기록이 없고 폴더가 상한보다 크면: 엔진 (Unity·Unreal·Source) 을 감지해 로딩 초반 파일 집합만 예열.
    public bool EngineProfiles { get; init; } = true;

    /// 첫 실행 학습·선행 읽기 설정.
    public LearnSettings Learn { get; init; } = LearnSettings.Default;

    /// 공유 프로필 (다른 PC 에서 만든 기록) 묶음 내려받기.
    public SharedProfilesSettings SharedProfiles { get; init; } = SharedProfilesSettings.Default;

    /// SSD 보조 저장 (선택 기능, 기본 꺼짐).
    public SsdTierSettings SsdTier { get; init; } = SsdTierSettings.Default;

    public const int DefaultRelocateChunkMib = 4;
    public const int DefaultRelocateParallel = 8;
    public const int SlowBusRelocateParallel = 2;
    public const string IoLow = "low";
    public const string IoNormal = "normal";

    /// 자동 재배치가 한 번에 옮기는 조각 크기 (MiB, 1..256).
    public int RelocateChunkMib { get; init; } = DefaultRelocateChunkMib;

    /// 자동 재배치가 동시에 옮기는 파일 수 (1..8, 1 = 순차). 같은 파일의 작업은 계획 순서대로 하나씩. 명시하지 않으면 ParallelFor 가 디스크에 맞춰 정함.
    public int RelocateParallel { get; init; } = DefaultRelocateParallel;

    /// config 에 relocate_parallel 이 명시돼 있는가 (명시값은 그대로, 아니면 자동 규칙).
    public bool RelocateParallelSet { get; init; }

    /// 실제 동시 수: 명시값, 아니면 대상 디스크 종류를 모르거나 USB 버스면 2, 그 밖에는 8.
    public int ParallelFor(bool slowBus) => RelocateParallelSet ? RelocateParallel : slowBus ? SlowBusRelocateParallel : DefaultRelocateParallel;

    /// 자동 재배치의 I/O 우선순위: "low" (기본, 낮은 I/O 힌트) 또는 "normal" (힌트 없음).
    public string RelocateIoPriority { get; init; } = IoLow;

    /// 자동 재배치의 이동 전후 내용 비교: "sample" (기본, 각 파일의 첫 작업과 4 번째마다), "all" (모든 작업) 또는 "off" (표본을 읽지 않음).
    public string RelocateVerify { get; init; } = Apply.VerifyMode.Sample;

    /// 자동 재배치의 실행 순서: "auto" (기본, target 과 같음), "target" 또는 "source" (실험용).
    public string RelocateOrder { get; init; } = Apply.RelocateOrder.Auto;

    /// 하루 (24 시간) 에 자동 재배치가 쓸 수 있는 총량 (GiB, 0..1048576). 0 = 무제한.
    public int MaxRelocateGibPerDay { get; init; } = 8;

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
