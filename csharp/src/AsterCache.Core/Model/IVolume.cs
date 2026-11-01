using AsterCache.Core.Volume;

namespace AsterCache.Core.Model;

/// 재배치 대상 볼륨. 테스트는 가짜 구현으로 바꿉니다.
public interface IVolume : IDisposable
{
    string Letter { get; }
    int ClusterBytes { get; }
    long TotalClusters { get; }
    long FreeClusters { get; }
    (long Start, long End) MftZone { get; }

    VolumeBitmap Bitmap();

    /// 실패하면 System.ComponentModel.Win32Exception 을 던집니다.
    void Move(string path, long vcn, long lcn, uint clusters);
}
