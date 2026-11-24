namespace AsterCache.Core.Settings;

/// 게임 하나에 서비스가 하는 일의 범위: Inherit (전부), PrewarmOnly (재배치 계열 제외), RelocateOnly (예열 계열 제외), Off (아무것도 안 함).
public enum GameMode
{
    Inherit,
    PrewarmOnly,
    RelocateOnly,
    Off,
}

/// 자동 업데이트 방식: Auto (받아서 설치), Notify (알림만), Off (확인하지 않음).
public enum UpdateMode
{
    Auto,
    Notify,
    Off,
}

/// 업데이트 설정 (config.json 의 update 가 없으면 기본값).
public sealed record UpdateSettings(UpdateMode Mode = UpdateMode.Auto, bool Prerelease = false)
{
    public static UpdateSettings Default { get; } = new();
}

/// 게임 한 개의 서비스 설정. 경로는 모두 정규화된 절대 경로 (X:\...), 끝 역슬래시 없음.
public sealed record GameSettings(
    string Name, IReadOnlyList<string> ExeNames, string Trace, string Plan, string OriginRoot, IReadOnlyList<string> AllowedRoots,
    string VolumeLabel, int ReapplyMinutes)
{
    /// 사전 예열 설정 (config.json 의 prewarm 이 없으면 기본값).
    public PrewarmSettings Prewarm { get; init; } = PrewarmSettings.Default;

    /// 한 번의 스트림·예열이 읽는 바이트의 상한 (MiB). 예산은 계획의 존 크기이고 이 값이 위에서 자릅니다.
    public int MaxStreamMib { get; init; } = SettingsStore.DefaultMaxStreamMib;

    /// config 에 max_stream_mib 가 명시됐는가 (파싱 때만 설정). 명시하지 않으면 상한은 StreamCapBytes 의 자동 규칙입니다.
    public bool MaxStreamSet { get; init; }

    /// 이번에 읽을 바이트의 상한: 명시값은 그대로, 명시하지 않았으면 max(기본 4096 MiB, 존 크기). 존은 계획 때 이미 존 상한 (max_zone_mib, auto 는 RAM 규칙) 안으로 잘려 있어 존 전체를 읽습니다.
    public long StreamCapBytes(long zoneBytes) => MaxStreamSet ? (long)MaxStreamMib << 20 : Math.Max((long)MaxStreamMib << 20, zoneBytes);

    /// 서비스 실행 중에만 쓰는 표시 (config 에는 쓰지 않음): 예열 상한이 모자라 스파스 파일 범위를 메모리 예열 목록·표본·고정 목록에서 뺀 게임의 사본에 붙습니다 (Prewarmer 가 게임별로 정함).
    public bool SkipSparse { get; init; }

    /// 등록 출처: "manual" (사용자가 직접, 기본) 또는 "auto" (발견·감지로 자동 등록, 항목이 꽉 차면 밀려날 수 있음).
    public string Source { get; init; } = SettingsStore.DefaultSource;

    /// 설치 폴더에서 알아낸 안티치트: "eac" | "battleye" | "vanguard" | "kernel" | "ricochet" | "unknown", 모르면 null.
    public string? AntiCheat { get; init; }

    /// 이 게임에 서비스가 하는 일의 범위 (config 의 mode, 없으면 Inherit).
    public GameMode Mode { get; init; } = GameMode.Inherit;

    /// 참이면 Mode 와 무관하게 이 게임의 새 작업을 모두 멈춤 (config 의 paused, 없으면 false).
    public bool Paused { get; init; }

    /// 이 게임의 재배치 방식 (config 의 layout: "whole_file" 또는 "ranges"). 없으면 null = 전역 auto.relocate_layout 을 따름. 읽을 때는 GamePolicy.RelocateLayout 으로.
    public string? Layout { get; init; }

    /// 런처가 준 표시 이름 (선택, 화면에만 씀). 내부 키 Name 은 계획·저널·상태 파일명이라 바꾸지 않습니다.
    public string? DisplayName { get; init; }

    /// 언어별 표시 이름 (선택, "ko"/"en" → 이름). 있으면 DisplayName 보다 먼저 씁니다.
    public IReadOnlyDictionary<string, string>? DisplayNames { get; init; }

    /// 화면에 보일 이름: 언어별 이름 → 설정의 표시 이름 → 런처가 상태 파일에 남긴 이름 (launcher) → 내부 키의 밑줄을 공백으로.
    public string DisplayFor(string lang, string? launcher = null) =>
        DisplayNames is { } byLang && byLang.TryGetValue(lang, out string? local) ? local : DisplayName ?? launcher ?? Name.Replace('_', ' ');
}

/// 게임 시작 전에 핫 집합을 RAM (파일 캐시) 에 올려 두는 동작. 기본: 서비스 시작 때 한 번, 트리거 프로그램 없음, 가용 RAM 에 4096 MiB 여유, 쿨다운 30분.
public sealed record PrewarmSettings(bool OnServiceStart, IReadOnlyList<string> TriggerExes, int MinFreeRamMib, int CooldownMin)
{
    public static PrewarmSettings Default { get; } = new(true, [], 4096, 30);

    /// 예열한 파일을 읽기 전용 매핑으로 계속 들고 있어 파일 캐시에서 밀려나지 않게 함 (기본 꺼짐, 실측 전).
    public bool Pin { get; init; }

    /// 예열이 파일 캐시에 올려 둘 합계의 상한 (MiB). 0 = 자동 (물리 RAM 의 1/2), 양수 = 그 값 (물리 RAM 을 넘지 못함). 게임들이 공유하는 예산이라 게임마다 적혀 있으면 가장 큰 값을 씁니다.
    public int MaxCacheMib { get; init; }

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

    /// trace_cap_mib 기본값: 이 값이면 명시하지 않은 것으로 보고 존 상한에 맞춰 자동 해석 (EffectiveTraceCapMib).
    public const int DefaultTraceCapMib = 6144;

    public const int DefaultStartStreamCapSeconds = 120;

    /// 기본 prewarm_recent 0 = RAM 예산 안에서 최근 순으로 가능한 만큼 (1 이상이면 그 개수 상한, -1 = 최근순 예열 끔).
    public static AutoSettings Default { get; } = new(true, true, true, true, 0, 3, 1800, DefaultTraceCapMib, 4, []);

    /// 거짓이면 게임 시작 때의 시작 스트림을 하지 않음 (기본 true).
    public bool StartStreamEnabled { get; init; } = true;

    /// 거짓이면 같은 게임의 이후 실행 파일 시작에 단계 스트림을 하지 않고 첫 스트림도 단계를 구분하지 않고 지금처럼 시작 목록 전체를 흘림 (기본 true).
    public bool StageStreamEnabled { get; init; } = true;

    /// 거짓이면 시작·단계 스트림이 게임 드라이브가 한가한 때만 읽지 않고 예전처럼 낮은 I/O 우선순위로 게임의 읽기 사이에 끼어듭니다 (기본 true: 한가할 때만 묶음 하나씩).
    public bool StreamIdleOnly { get; init; } = true;

    /// 거짓이면 자동 트레이스에 디스크 읽기 완료 집계와 그 관찰 수명을 더하지 않습니다 (기본 true).
    public bool IoSummaryEnabled { get; init; } = true;

    /// 자동 재배치·주기 재적용을 시작해도 되는 로컬 시각 (시, 0..23, 중복 없음, 최대 24개). 비어 있으면 시간 제한 없음.
    public IReadOnlyList<int> RelocateHours { get; init; } = [];

    /// 시작 스트림 시간 상한 (초, 0..7200), 0 = 시간 상한 없음.
    public int StartStreamCapSeconds { get; init; } = DefaultStartStreamCapSeconds;

    /// 첫 트레이스 창이 끝난 뒤 다음 창까지 기다리는 시간 (분, 1..1440).
    public int TraceLateIntervalMin { get; init; } = 10;

    /// 추가 트레이스 창 하나의 수집 시간 (초, 1..7200).
    public int TraceLateWindowSeconds { get; init; } = 90;

    /// 첫 창 뒤 추가로 열 트레이스 창 수 (0..32), 0 = 추가 수집 끔.
    public int TraceLateWindows { get; init; } = 6;

    /// 한 게임이 모을 트레이스 플레이 횟수 (1..32): 이보다 적게 모았으면 원시 트레이스가 있어도 다음 플레이를 다시 기록해 합집합으로 병합합니다. 1 = 첫 플레이만.
    public int TraceRuns { get; init; } = 3;

    /// 트레이스 읽기 캡 (MiB): 기본값 (6144) 이면 max(6144, 유효 존 상한 x 2) (최대 65536), 다른 값이면 명시한 값 그대로.
    public int EffectiveTraceCapMib(long zoneMib) =>
        TraceCapMib != DefaultTraceCapMib ? TraceCapMib : (int)Math.Min(65536, Math.Max(DefaultTraceCapMib, zoneMib * 2));

    /// 추가 트레이스 판단에 더 이상 쓰지 않는 호환용 키. 추가 수집은 직전 병합 증가량(trace_merge_min_mib)과 trace_runs_max 로 정함. 커버리지는 표시 전용.
    public int TraceTargetCoverage { get; init; } = 70;

    /// 추가 수집을 포함한 유효 트레이스 회차 상한 (1..32).
    public int TraceRunsMax { get; init; } = 8;

    /// 병합한 트레이스의 핫 집합이 지금 계획보다 이만큼 (MiB, 1..65536) 이상 늘었을 때만 존을 확장해 다시 계획합니다.
    public int TraceMergeMinMib { get; init; } = 64;

    /// 게임을 하는 동안 이 간격 (분) 으로 핫 집합을 다시 데움, 0 = 끔. 식었는지는 핫 집합 8 곳 (앞쪽 4 + 균등 4) 을 읽어 보고, 2 곳 이상이 2 ms 를 넘으면 식은 것으로 봅니다.
    public int RewarmMinutes { get; init; } = 10;

    /// 같은 게임의 시작 스트림 사이 최소 간격 (분, 0..1440), 0 = 끔. 게임이 이미 돌거나 그 디스크가 바쁠 때도 건너뜁니다 (같은 데이터를 디스크에서 또 읽어 게임이 끊기지 않게).
    public int RestreamCooldownMin { get; init; } = 10;

    /// 게임 세션이 이 시간 (초, 10..600) 보다 짧게 끝나면 실행 직후 종료로 셈 (그다음 실행에서 서비스가 한 번 물러남).
    public int QuickExitSeconds { get; init; } = 90;

    /// 게임 종료를 감지하면 그 게임의 재예열 점검을 이만큼 (분, 0..1440) 뒤로 당김, 0 = 끔 (rewarm_minutes 가 0 이면 재예열 자체가 꺼져 있음). 점검은 식었을 때만 다시 읽습니다.
    public int RewarmAfterExitMin { get; init; } = 2;

    /// 서비스 시작 (부팅) 예열 대상: 최근 14 일 안에 플레이한 게임 중 마지막 플레이 순 상위 이 개수 (0..32), 0 = 서비스 시작 예열 끔. 플레이 기록이 없는 (자동 모드 꺼짐) 설정에는 적용하지 않음.
    public int StartupWarmGames { get; init; } = 2;

    /// 서비스 시작 예열이 한 번에 읽는 양의 합계 상한 (MiB, 1..1048576). 넘는 게임은 건너뜀.
    public int StartupWarmMaxMib { get; init; } = 1024;

    /// 게임 업데이트를 감지한 뒤 그 게임의 재계획·재배치·예열을 이만큼 (분, 0..1440) 미룸, 0 = 지연 없음. 업데이트 직후에는 드라이브가 파일을 흩어 쓰는 중일 수 있습니다.
    public int PostUpdateDelayMin { get; init; } = 15;

    /// probe 회복 대기의 상한 (분, 1..1440). post_update_delay_min 과 따로 두어, 그 값이 0 이어도 회복 대기는 꺼지지 않습니다. 기본 60 (예전 15 x 4).
    public int RecoveryMaxMin { get; init; } = 60;

    /// probe 회복이 recovery_max_min 에 이르러도 회복을 확인하지 못했을 때 (판정은 unknown) 추가 재배치를 계속 보류하는 절대 상한 (분, 1..1440). recovery_max_min 보다 작으면 그 값을 씀. 기본 180. 예열에는 영향 없음.
    public int RecoveryHardMaxMin { get; init; } = 180;

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

    /// 실제 동시 수: 명시값, 아니면 SMR 로 보이는 HDD 면 1 (순차 쓰기가 느려짐), 종류를 모르거나 USB 버스면 2, 그 밖에는 8.
    public int ParallelFor(bool slowBus, bool smr = false) => RelocateParallelSet ? RelocateParallel : smr ? 1 : slowBus ? SlowBusRelocateParallel : DefaultRelocateParallel;

    /// 자동 재배치의 I/O 우선순위: "low" (기본, 낮은 I/O 힌트) 또는 "normal" (힌트 없음).
    public string RelocateIoPriority { get; init; } = IoLow;

    /// 자동 재배치의 이동 전후 내용 비교: "sample" (기본, 각 파일의 첫 작업과 4 번째마다), "all" (모든 작업) 또는 "off" (표본을 읽지 않음).
    public string RelocateVerify { get; init; } = Apply.VerifyMode.Sample;

    /// 자동 재배치의 실행 순서: "auto" (기본, target 과 같음), "target" 또는 "source" (실험용).
    public string RelocateOrder { get; init; } = Apply.RelocateOrder.Auto;

    /// 자동 재배치의 배치 방식: "whole_file" (기본, 고른 파일을 한 조각으로 zone 에) 또는 "ranges" (핫 범위를 접근 순서로 이어 붙임).
    public string RelocateLayout { get; init; } = Plan.RelocateLayouts.WholeFile;

    /// 하루 (24 시간) 에 자동 재배치가 쓸 수 있는 총량 (GiB, 0..1048576). 0 = 무제한.
    public int MaxRelocateGibPerDay { get; init; } = 8;

    /// max_relocate_gib_per_day 가 설정에 명시됐는가 (아니면 SMR 디스크는 더 낮은 기본값을 씀).
    public bool MaxRelocateGibPerDaySet { get; init; }

    public const int SmrMaxRelocateGibPerDay = 2;

    /// 게임 실행 계기 없이 주기로 도는 재예열·보충 예열·사다리가 드라이브마다 하루 (24 시간) 에 읽을 수 있는 총량 (GiB, 0..1048576). 0 = 기본 규칙 (그 드라이브 게임들의 예열 목록 합의 6 배와 24 GiB 가운데 큰 값).
    public int IdleReadGibPerDay { get; init; }

    /// 실제 일일 상한 (GiB): 명시값, 아니면 SMR 로 보이는 HDD 면 2, 그 밖에는 8 (순차 쓰기가 느린 SMR 은 하루에 덜 씀).
    public int MaxRelocateGibFor(bool smr) => MaxRelocateGibPerDaySet || !smr ? MaxRelocateGibPerDay : SmrMaxRelocateGibPerDay;

    /// 한 번의 적용이 쓰는 상한 (바이트): SMR 로 보이는 HDD 는 1 GiB, 그 밖에는 2 GiB.
    public static long WriteCapFor(bool smr) => smr ? 1L << 30 : 2L << 30;

    /// 유휴 대기 시간: idle_seconds 가 명시됐거나 둘 다 없으면 IdleSeconds 초, idle_minutes 만 명시됐으면 IdleMinutes 분.
    public TimeSpan IdleWindow => IdleSecondsSet || !IdleMinutesSet ? TimeSpan.FromSeconds(IdleSeconds) : TimeSpan.FromMinutes(IdleMinutes);
}

/// 리그 오브 레전드 챔피언 선택 선읽기 (config 의 riot 객체): 기본 꺼짐. 켜면 챔피언 선택 중 잠긴 챔피언·맵의 데이터 파일을 예산 (MiB) 안에서 미리 읽음.
public sealed record RiotSettings(bool ChampSelectPrewarm = false, int ChampSelectBudgetMib = RiotSettings.DefaultBudgetMib)
{
    public const int DefaultBudgetMib = 2048;

    public static RiotSettings Default { get; } = new();
}

/// 메모리 설정 (config 의 memory 객체): background_yield 는 게임 세션 동안 게임과 무관한 배경 프로세스가 메모리를 양보하게 함 (기본 켜짐).
/// standby_ladder 는 예열한 캐시를 디스크 읽기 없이 다시 만져 중요한 것이 마지막에 밀려나게 하고 밀려나기 전에 새로 고침 (기본 켜짐).
/// background_io_priority 는 배경 양보 동안 그 프로세스들의 디스크 I/O 우선순위도 낮춤 (기본 켜짐, background_yield 가 꺼져 있으면 하지 않음).
/// background_cpu_priority 는 같은 프로세스 가운데 CPU 우선순위 등급이 보통인 것을 보통보다 낮음으로 낮춤 (기본 꺼짐: 실제 플레이의 초 단위 기록에서 끊긴 초와 아닌 초의 다른 프로세스 CPU 에 차이가 없었음, background_yield 가 꺼져 있으면 하지 않음).
/// keep_hold 는 게임이 실행되는 동안 유지 목록을 서비스의 매핑 뷰로 붙들어 대기 목록에서 밀려나지 않게 함 (기본 켜짐, 꺼지면 대기 목록을 다시 만지는 지금의 동작, standby_ladder 가 꺼져 있으면 하지 않음).
/// keep_hold_written_files 는 붙들 때 게임이 플레이 중에 쓰는 파일 (트레이스가 가변으로 표시한 xpak_cache 같은 것) 도 조건이 맞으면 붙듦 (기본 켜짐, keep_hold 가 꺼져 있으면 하지 않음): 크기가 줄어든 적이 없고 업데이트 직후가 아니며 이번 플레이에서 바뀌지 않은 파일만, 바뀌면 5 초 안에 그 파일의 뷰를 닫음. 끄면 그런 파일은 예전처럼 붙들지 않고 순위 목록에도 넣지 않음.
/// keep_sparse 는 게임이 실행되는 동안 예열에 든 스파스 속성 파일의 블록을 버퍼드 읽기로만 주기마다 다시 읽어 메모리에 남김 (기본 켜짐, 스파스 파일은 매핑하면 캐시가 버려져 붙들지 못함, standby_ladder 가 꺼져 있으면 하지 않음).
/// background_yield_fast 는 게임이 실행되는 동안 새로 뜬 프로세스를 약 3 초 안에 알아채 기존 배경 양보를 적용함 (기본 켜짐, 프로세스 번호 목록만 비교하고 작업 집합은 비우지 않음, background_yield 가 꺼져 있으면 하지 않음).
/// background_in_use_memory_priority 는 쓰고 있는 배경 프로세스의 메모리 우선순위를 보통보다 낮음 (4) 으로 낮춤 (기본 켜짐, 작업 집합은 비우지 않음, background_yield 가 꺼져 있으면 하지 않음).
public sealed record MemorySettings(bool BackgroundYield = true, bool StandbyLadder = true, bool BackgroundIoPriority = true, bool BackgroundCpuPriority = false, bool KeepHold = true, bool KeepHoldWrittenFiles = true, bool KeepSparse = true, bool BackgroundYieldFast = true, bool BackgroundInUseMemoryPriority = true)
{
    public static MemorySettings Default { get; } = new();
}

/// 플레이 기록 설정 (config 의 play 객체): frame_times 는 게임 프로세스의 프레임 간격 (끊김) 을 ETW Present 이벤트로 재서 플레이 기록에 남김 (기본 켜짐, 자동 트레이스를 막는 안티치트 게임에서는 켜져 있어도 동작하지 않음).
/// series 는 프레임 간격을 재는 플레이에서 매초 한 줄 (프레임 값과 CPU·메모리·그래픽 메모리·디스크 값) 을 플레이마다 파일 하나 (play_series 폴더) 에 남김 (기본 켜짐, frame_times 가 동작하는 플레이에서만).
public sealed record PlaySettings(bool FrameTimes = true, bool Series = true)
{
    public static PlaySettings Default { get; } = new();
}

/// config.json 한 벌 (검증을 통과한 값만 존재).
public sealed record AsterSettings(int PollMs, IReadOnlyList<GameSettings> Games, string Watcher = SettingsStore.DefaultWatcher, string Language = SettingsStore.DefaultLanguage)
{
    public AutoSettings Auto { get; init; } = AutoSettings.Default;

    /// 자동 업데이트 설정.
    public UpdateSettings Update { get; init; } = UpdateSettings.Default;

    /// 리그 오브 레전드 챔피언 선택 선읽기 설정.
    public RiotSettings Riot { get; init; } = RiotSettings.Default;

    /// 배경 메모리 양보 설정.
    public MemorySettings Memory { get; init; } = MemorySettings.Default;

    /// 플레이 기록 설정.
    public PlaySettings Play { get; init; } = PlaySettings.Default;

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
