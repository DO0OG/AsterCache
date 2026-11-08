using System.ComponentModel;
using System.Runtime.InteropServices;
using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// 파일 시각. Python sqlite_cache._fingerprint 의 ChangeTime 항목.
public static class FileTimes
{
    /// 열린 핸들의 ChangeTime (FILETIME, 1601 년 기준 100ns 단위). 실패하면 Win32Exception.
    public static unsafe long ChangeTime(SafeFileHandle handle)
    {
        FileBasicInfo info = default;
        if (!NativeMethods.GetFileInformationByHandleEx(handle, Win32.FileBasicInfoClass, (byte*)&info, (uint)sizeof(FileBasicInfo)))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return info.ChangeTime;
    }

    /// 경로 파일의 ChangeTime (UTC). 속성 읽기 권한만 열고 (공유 전부 허용, 파일 내용은 읽지 않음) 열 수 없으면 null.
    public static DateTimeOffset? ChangeTimeOf(string path)
    {
        using SafeFileHandle h = NativeMethods.CreateFileW(Path.GetFullPath(path), Win32.FileReadAttributes, Win32.ShareAll, 0, Win32.OpenExisting, 0, 0);
        if (h.IsInvalid)
        {
            return null;
        }

        try
        {
            return new DateTimeOffset(DateTime.FromFileTimeUtc(ChangeTime(h)), TimeSpan.Zero);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
