using AsterCache.Windows.Native;

namespace AsterCache.Windows;

/// 볼륨 정보. Python mover.volume_label.
public static class VolumeInfo
{
    /// 'X:' 의 볼륨 라벨 (GetVolumeInformationW), 읽을 수 없으면 "".
    public static unsafe string Label(string letter)
    {
        char* name = stackalloc char[262];
        bool ok = NativeMethods.GetVolumeInformationW(letter.TrimEnd(':', '\\') + ":\\", name, 262, null, null, null, null, 0);
        return ok ? new string(name) : string.Empty;
    }

    /// 'X:' 의 파일 시스템 이름 ("NTFS" 등, GetVolumeInformationW), 읽을 수 없으면 "".
    public static unsafe string FileSystem(string letter)
    {
        char* fs = stackalloc char[262];
        bool ok = NativeMethods.GetVolumeInformationW(letter.TrimEnd(':', '\\') + ":\\", null, 0, null, null, null, fs, 262);
        return ok ? new string(fs) : string.Empty;
    }
}
