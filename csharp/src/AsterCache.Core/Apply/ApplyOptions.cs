using AsterCache.Core.Model;
using AsterCache.Core.Plan;

namespace AsterCache.Core.Apply;

/// 실행 옵션. Execute 가 false 면 아무것도 옮기지 않고 저널도 쓰지 않습니다. DryRun 은 자동 존을 정해 SaveTo 에 저장만 합니다.
/// Parallel > 1 이면 서로 다른 파일의 작업을 최대 그 수만큼 동시에 옮깁니다 (같은 파일의 작업은 계획 순서대로 한 번에 하나). 기본 1 = 순차.
/// LowIoPriority 는 서비스의 백그라운드 경로 (ReapplyEnv.Run) 가 읽고, false 면 낮은 I/O 우선순위 힌트를 붙이지 않습니다.
/// Reserved: 같은 볼륨의 다른 게임 계획이 아직 쓸 존 범위 (ZoneReservations.Collect). 자동 존은 이 범위를 피해 고르고, 정한 존의 남은 목표가 겹치면 zone_not_free 로 거절합니다.
public sealed record ApplyOptions(
    bool Execute = false, long ChunkBytes = 4L << 20, long WriteCap = 2L << 30, string? SaveTo = null, bool DryRun = false, bool LowIoPriority = true, int PlanAhead = Applier.PlanAhead,
    string Verify = VerifyMode.Sample, string Order = RelocateOrder.Target, int Parallel = 1, IReadOnlyList<(long Lcn, long Clusters)>? Reserved = null);

/// 재배치 작업의 실행 순서. target: 계획의 대상(존) 순서, source: 현재 원본 첫 extent 의 LCN 오름차순 (결과 배치는 같음, 읽기 탐색이 줄 수 있으나 존 쓰기가 흩어짐).
/// source 는 실험용: 실측 (SMR HDD) 에서 존을 무작위 순서로 읽게 돼 target 보다 느렸음. 설정 auto.relocate_order 는 여기에 "auto" 를 더하며 항상 target 으로 해석.
public static class RelocateOrder
{
    public const string Target = "target";
    public const string Source = "source";
    public const string Auto = "auto";

    public static bool IsValid(string? v) => v is Target or Source;

    public static bool IsValidSetting(string? v) => v is Auto || IsValid(v);

    /// 설정값에서 실제 순서를 정함: auto 는 target.
    public static string Resolve(string setting) => setting is Auto ? Target : setting;
}

/// 이동 전후 내용 비교의 범위. all: 모든 작업, sample: 각 파일의 첫 작업과 작업 인덱스가 4 의 배수인 작업만 (나머지는 이동 결과와 extent 일치만 확인), off: 표본을 읽지 않음.
public static class VerifyMode
{
    public const string All = "all";
    public const string Sample = "sample";
    public const string Off = "off";

    public static bool IsValid(string? v) => v is All or Sample or Off;
}

/// 바깥 세계 접근 (extent 조회, 해시, 속성, realpath, 지문, sleep) 을 전부 주입하는 자리. 시험은 가짜로 바꿉니다.
public sealed class ApplyHooks
{
    private const long UnixEpochTicks = 621355968000000000L;

    /// 파일의 현재 extent, 스파스 등으로 모르면 null (Python file_extents).
    public required Func<string, IReadOnlyList<Extent>?> Extents { get; init; }

    /// (경로, 파일 크기, 옮긴 범위) → SHA-256 표본. apply 에 필요 (Windows SampleDigest.Compute 또는 <see cref="SampleHasher"/>).
    public Func<string, long, IReadOnlyList<(long Offset, long Length)>, string>? Digest { get; init; }

    /// 새 저널 (digest_rule edge64k-v1) 의 이동 전후 비교용: 처음·가운데·끝 표본 없이 주어진 범위 + 크기만 해시합니다 (Windows SampleDigest.Compute 의 span: 0).
    /// 없으면 Digest 를 그대로 씁니다. 프로필 지문 같은 호환 규칙과는 무관합니다.
    public Func<string, long, IReadOnlyList<(long Offset, long Length)>, string>? EdgeDigest { get; init; }

    /// 건너뛸 이유 (압축·스파스·암호화 …), 옮겨도 되면 null. apply 에 필요 (Windows FileGuards.SkipReason).
    public Func<string, string?>? SkipReason { get; init; }

    public Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;

    /// 링크·정션을 푼 경로. 기본은 Path.GetFullPath (링크를 풀지 않으므로 실제 실행은 GetFinalPathNameByHandle 기반 구현을 주입).
    public Func<string, string> RealPath { get; init; } = Path.GetFullPath;

    /// 계획 뒤 파일이 바뀌었는지 보는 [size, mtime_ns].
    public Func<string, Fingerprint> Stat { get; init; } = PlanFile.FingerprintOf;

    public Func<string, long> FileSize { get; init; } = p => new FileInfo(p).Length;

    /// 저널 header 의 started (Unix 초).
    public Func<double> Clock { get; init; } = () => (DateTime.UtcNow.Ticks - UnixEpochTicks) / 1e7;
}
