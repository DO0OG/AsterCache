namespace AsterCache.Core.Model;

/// 파일 안 위치 (VCN), 볼륨 위치 (LCN), 클러스터 수.
public readonly record struct Extent(long Vcn, long Lcn, long Clusters);
