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
}
