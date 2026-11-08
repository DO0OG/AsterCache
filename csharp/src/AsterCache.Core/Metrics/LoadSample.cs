namespace AsterCache.Core.Metrics;

/// 실측 시나리오: S0/S1/S2 (ZC-10 의 설치 직후 상태 구분). Unknown 은 모름 (게이트는 비교 불가로 봅니다).
public enum Scenario { S0, S1, S2, Unknown }

/// 비교 쪽 라벨.
public enum Arm { Baseline, Candidate }

/// 경과 구간 (0s/10s/30s/60s/5m). 기준은 두 가지입니다: 서비스 가동 시간 (기록기가 쓰는 기준, elapsed_basis 가 service_uptime) 과 설치 후 경과 (아직 쓰지 않음). 기준이 다르면 같은 구간도 뜻이 다릅니다.
public enum ElapsedBucket { Sec0, Sec10, Sec30, Sec60, Min5 }

/// 캐시 상태: Cold 설치 직후 빈 캐시, Warm 데워진 캐시, Unknown 모름 (게이트는 Unknown 을 비교 불가로 봅니다).
public enum CacheState { Cold, Warm, Unknown }

/// 로딩 표본 한 건. null 은 unobserved (측정 안 함) 이며 0 으로 대신하지 않습니다.
/// TVisualMs 는 동의 기반 외부 관찰·수동 입력만 받는 칸입니다 (화면 캡처 코드 없음, 판정에도 쓰지 않음).
/// TTotalMs 는 준비 포함 총 시간 칸입니다 (null 은 unobserved). 판정에는 아직 쓰지 않습니다.
/// GameBuild 는 게임 빌드 식별자 (null 이면 모름). 같은 판정 안에서는 빌드·캐시 상태가 모두 같아야 합니다.
/// ProbeReadyMs 는 로딩 경계의 캐시 표본 도달 시각 (대리 지표, 실제 준비 완료 시각이 아님). 판정에는 쓰지 않습니다.
/// ElapsedBasis 는 Elapsed 의 기준 (service_uptime 또는 unknown). 기록 파일에 없으면 unknown 이며, install 로 간주하지 않습니다.
public sealed record LoadSample(
    string GameKey,
    Scenario Scenario,
    Arm Arm,
    ElapsedBucket Elapsed,
    long? TReadyMs,
    long? TVisualMs = null,
    long? PhysicalHddReadBytes = null,
    bool? Contended = null,
    long? TTotalMs = null,
    string? GameBuild = null,
    CacheState CacheState = CacheState.Unknown,
    long? ProbeReadyMs = null,
    string ElapsedBasis = "unknown");
