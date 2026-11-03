using AsterCache.Core.Model;
using AsterCache.Core.Plan;

namespace AsterCache.Core.Apply;

/// 실행 옵션. Execute 가 false 면 아무것도 옮기지 않고 저널도 쓰지 않습니다. DryRun 은 자동 존을 정해 SaveTo 에 저장만 합니다.
/// LowIoPriority 는 서비스의 백그라운드 경로 (ReapplyEnv.Run) 가 읽고, false 면 낮은 I/O 우선순위 힌트를 붙이지 않습니다.
public sealed record ApplyOptions(
    bool Execute = false, long ChunkBytes = 4L << 20, long WriteCap = 2L << 30, string? SaveTo = null, bool DryRun = false, bool LowIoPriority = true);

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
