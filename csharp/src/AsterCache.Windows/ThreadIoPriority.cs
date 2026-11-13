using AsterCache.Windows.Native;

namespace AsterCache.Windows;

/// 스레드가 내는 읽기의 I/O 우선순위 (IO_PRIORITY_HINT 값 그대로).
public enum IoPriority : uint
{
    VeryLow = 0,
    Low = 1,
    Normal = 2,
}

/// 현재 스레드의 I/O 우선순위를 바꿉니다 (NtSetInformationThread, ThreadIoPriority).
/// 스레드 백그라운드 모드 (THREAD_MODE_BACKGROUND_BEGIN) 와 달리 페이지 우선순위는 건드리지 않아, 올라온 페이지는 그대로 보통 파일 캐시 우선순위가 됩니다.
/// 같은 스레드가 낸 PrefetchVirtualMemory 읽기에도 적용됩니다. 바꾼 스레드가 읽는 스레드여야 하므로 호출한 스레드에만 영향을 줍니다 (스레드 풀 스레드는 끝나기 전에 보통으로 되돌릴 것).
public static unsafe class ThreadIoPriority
{
    private const int ThreadIoPriorityClass = 22;

    /// 현재 스레드의 I/O 우선순위를 바꿉니다. 실패하면 예외 없이 false.
    public static bool TrySet(IoPriority priority)
    {
        uint value = (uint)priority;
        return NativeMethods.NtSetInformationThread(NativeMethods.GetCurrentThread(), ThreadIoPriorityClass, &value, sizeof(uint)) >= 0;
    }

    /// 현재 스레드의 I/O 우선순위, 조회에 실패하면 null.
    public static IoPriority? Get()
    {
        uint value;
        return NativeMethods.NtQueryInformationThread(NativeMethods.GetCurrentThread(), ThreadIoPriorityClass, &value, sizeof(uint), out _) >= 0 ? (IoPriority)value : null;
    }
}
