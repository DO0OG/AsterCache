using System.Runtime.InteropServices;
using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// 파일 핸들에 낮은 I/O 우선순위 힌트 (FILE_IO_PRIORITY_HINT_INFO, IoPriorityHintLow) 를 붙입니다.
/// 힌트일 뿐이라 드라이버가 무시할 수 있고, 실패해도 동작은 계속합니다. 첫 실패의 Win32 오류만 onFailure 로 한 번 알립니다.
/// 스레드의 백그라운드 모드 (SetThreadPriority) 는 쓰지 않습니다: 서비스의 다른 일까지 느려지기 때문입니다.
public sealed unsafe class IoPriorityHint(Action<int> onFailure)
{
    private int _reported;

    public bool SetLow(SafeFileHandle handle)
    {
        int hint = Win32.IoPriorityHintLow;
        if (NativeMethods.SetFileInformationByHandle(handle, Win32.FileIoPriorityHintInfoClass, (byte*)&hint, sizeof(int)))
        {
            return true;
        }

        int error = Marshal.GetLastPInvokeError();
        if (Interlocked.Exchange(ref _reported, 1) == 0)
        {
            onFailure(error);
        }

        return false;
    }
}
