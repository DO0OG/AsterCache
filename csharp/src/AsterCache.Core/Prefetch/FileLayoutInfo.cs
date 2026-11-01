using AsterCache.Core.Model;

namespace AsterCache.Core.Prefetch;

/// 파일의 물리 배치 (extent 목록과 클러스터 바이트 수). Python prefetch.cli._layout 의 (extents, cluster_bytes) 튜플.
public sealed record FileLayoutInfo(IReadOnlyList<Extent> Extents, int ClusterBytes)
{
    /// 파일 바이트 offset 이 놓인 클러스터의 LCN. 모르거나 디스크 밖이면 null (Python physical_key).
    public static long? PhysicalKey(IReadOnlyList<Extent>? extents, long offset, int clusterBytes)
    {
        if (extents is null || extents.Count == 0 || clusterBytes == 0)
        {
            return null;
        }

        long vcn = offset / clusterBytes;
        foreach (Extent e in extents)
        {
            if (e.Vcn <= vcn && vcn < e.Vcn + e.Clusters)
            {
                return e.Lcn + (vcn - e.Vcn);
            }
        }

        return null;
    }
}

/// 읽을 범위 한 줄. Score 가 하나라도 있으면 점수 내림차순으로 예산을 채웁니다.
public readonly record struct ReadRange(string Path, long Offset, long Length, double? Score = null);

/// 스케줄 결과 한 줄.
public readonly record struct ReadOp(string Path, long Offset, long Length);

/// 스케줄 요약 (Python info 딕셔너리).
public sealed class ScheduleInfo
{
    public required string Strategy { get; init; }

    public long SelectedBytes { get; set; }

    public bool Fallback { get; set; }

    public int LayoutOkFiles { get; set; }

    public int LayoutFiles { get; set; }

    public long GapBytes { get; init; }

    public int FullFiles { get; set; }
}
