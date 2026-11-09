namespace AsterCache.Core.Plan.Orbit;

/// ORBIT-3 제안값 모음. config.json 키로 노출하지 않습니다 (이전 판 되돌리기 보호).
public static class Orbit3Constants
{
    /// 세그먼트 목표 크기: 예열 읽기 묶음(64 MiB)과 같은 단위라 세그먼트 경계가 묶음 경계와 크게 어긋나지 않습니다.
    public const long SegmentBytes = 64L << 20;

    /// 순열 대상 세그먼트 상한: 플래너의 64비트 마스크 블록 상한 24 에서 고정 접두(head) 1개를 뺀 값.
    public const int MaxTailSegments = 23;

    /// 회차 안 에피소드 경계로 보는 읽기 쉼 간격.
    public const long EpisodeGapMs = 2000;

    /// 회차 사이 같은 에피소드 클래스로 묶는 블록 집합 Jaccard 하한.
    public const double EpisodeJaccard = 0.5;

    /// 에피소드 요구 세그먼트: 에피소드 바이트의 이 비율 이상을 가진 세그먼트만, 최대 MaxRequiredSegments 개.
    public const double RequiredSegmentShare = 1.0 / 16;
    public const int MaxRequiredSegments = 8;

    /// 보정 오차 수준. 장치 구간은 분할 conformal (1-α), 보상 구간은 Clopper–Pearson (1-α/E).
    public const double Alpha = 0.05;

    /// 보정 창 표본 최소 수: α=0.05 에서 ⌈(n+1)(1-α)⌉ ≤ n 이 되는 가장 작은 n.
    public const int MinCalibrationSamples = 19;

    /// 장치 구간 배수 상한 (플래너 ValidDevice 와 같은 값).
    public const double MaxEnvelopeRatio = 10;

    /// 온라인 커버리지 감시: 지표별 최근 MonitorWindow 개 스트림 가운데 이탈 수 X 에 대해 단측 이항 검정 P(X ≥ 관측 | n, α) 가 MonitorPValue 미만이면 포락선 무효.
    /// 고정 여유 (2α+0.05) 는 Bin(50, 0.1) ≥ 8 이 약 12% 라 오경보가 잦아 검정으로 바꿈.
    public const int MonitorWindow = 50;
    public const double MonitorPValue = 0.01;

    /// PDH 처리량이 하한보다 낮은 스트림이 이만큼 연속이면 포락선 무효.
    public const int PdhAlarmStreak = 2;

    /// 스트림마다 장치 표본: 4 KiB 비버퍼 읽기 12회(탐색 지연) + 4 MiB 연속 비버퍼 읽기 3회(전송률). 합계 12 MiB + 48 KiB.
    public const int SeekProbeReads = 12;
    public const int SeekProbeBytes = 4 << 10;
    public const int ThroughputProbeReads = 3;
    public const int ThroughputProbeBytes = 4 << 20;
    public const long MaxProbeBytesPerStream = (long)SeekProbeReads * SeekProbeBytes + (long)ThroughputProbeReads * ThroughputProbeBytes;

    /// 표본 읽기 사이 최소 거리: 같은 파일에서 최근 읽은 위치와 이만큼 떨어진 곳만 씁니다. 하드디스크의 캐시 (DRAM·SMR 미디어 캐시, 실측 드라이브 256 MiB 급) 에 이미 올라온 영역이나
    /// 선읽기 영역을 다시 읽으면 플래터 탐색이 아니라 캐시 적중 (0.3 ms 탐색·460 MB/s 전송) 이 재어지기 때문입니다. 캐시의 1/4 이라 선읽기 폭을 넉넉히 넘습니다.
    public const long MinProbeGapBytes = 64L << 20;

    /// 거리 규칙에 쓰려고 디스크마다 기억하는 최근 스트림 수: 스트림 하나가 읽는 약 12 MiB 의 16 배 (약 192 MiB) 가 캐시 256 MiB 안에 드는 범위.
    public const int RecentProbeStreams = 16;

    /// disk_model.json 에 디스크마다 남기는 최근 표본 수.
    public const int DiskModelMaxSamples = 200;

    /// 보상 보정에 쓰는 회차 수: 3 미만이면 보정 없음, 최근 8 회차까지만.
    public const int MinValueRuns = 3;
    public const int MaxValueRuns = 8;

    /// 모형 신뢰도 하한 (플래너 기본과 같은 값).
    public const double MinModelConfidence = 0.55;

    /// 예열 스레드 결정 시간 상한, 배경 모형 구축 시간 상한.
    public const int DecisionBudgetMs = 500;
    public const int ModelBuildBudgetMs = 2000;

    /// 후보 순서가 한 에피소드의 준비 시각을 늦출 수 있는 기본 한도입니다 (플래그 4 기준과 동일).
    public const double EpisodeRegressionFraction = 0.05;
    public const double EpisodeRegressionSlackMs = 200;

    /// 연속 예외가 이만큼이면 모드를 shadow 로 강등.
    public const int ExceptionDemoteStreak = 2;
}
