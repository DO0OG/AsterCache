using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// 다른 프로세스의 경로·작업 집합·생성 시각 (FILETIME). 읽을 수 없으면 Describe 가 null.
public readonly record struct ProcessInfo(string Path, long WorkingSetBytes, long Created);

/// 배경 프로세스의 메모리 양보에 쓰는 프로세스 단위 호출. 다른 프로세스에 하는 일은 작업 집합 비우기와 메모리 우선순위 변경 두 가지뿐입니다.
/// 실패 (접근 거부, 이미 종료) 는 예외가 아니라 null/false 이며 핸들은 항상 닫습니다. 열기 권한은 호출마다 필요한 최소: 조회 0x1000, 비우기 0x1100, 우선순위 쓰기 0x0200.
public static unsafe class ProcessMemory
{
    public const int PriorityVeryLow = 1;

    /// 프로세스를 열지 않고 세션 id 를 묻습니다. 세션 0 은 서비스·시스템 프로세스.
    public static uint? SessionId(int pid) => NativeMethods.ProcessIdToSessionId((uint)pid, out uint session) ? session : null;

    public static ProcessInfo? Describe(int pid)
    {
        using SafeProcessHandle h = Open(pid, Win32.ProcessQueryLimitedInformation);
        if (h.IsInvalid)
        {
            return null;
        }

        char* buf = stackalloc char[1024];
        uint size = 1024;
        ProcessMemoryCounters counters = default;
        long created = 0;
        long unused = 0;
        if (!NativeMethods.QueryFullProcessImageNameW(h, 0, buf, ref size)
            || !NativeMethods.GetProcessMemoryInfo(h, &counters, (uint)sizeof(ProcessMemoryCounters))
            || !NativeMethods.GetProcessTimes(h, &created, &unused, &unused, &unused))
        {
            return null;
        }

        return new ProcessInfo(new string(buf, 0, (int)size), (long)Math.Min(counters.WorkingSetSize, long.MaxValue), created);
    }

    /// 작업 집합을 비웁니다 (페이지는 대기 목록으로 가고, 프로세스가 다시 만지면 소프트 폴트로 돌아옴).
    public static bool Trim(int pid)
    {
        using SafeProcessHandle h = Open(pid, Win32.ProcessQueryLimitedInformation | Win32.ProcessSetQuota);
        return !h.IsInvalid && NativeMethods.EmptyWorkingSet(h);
    }

    /// 메모리 우선순위 (0~7, 보통 5).
    public static int? MemoryPriority(int pid)
    {
        using SafeProcessHandle h = Open(pid, Win32.ProcessQueryLimitedInformation);
        if (h.IsInvalid)
        {
            return null;
        }

        uint value = 0;
        return NativeMethods.GetProcessInformation(h, Win32.ProcessMemoryPriorityClass, (byte*)&value, sizeof(uint)) ? (int)value : null;
    }

    public static bool SetMemoryPriority(int pid, int priority)
    {
        using SafeProcessHandle h = Open(pid, Win32.ProcessSetInformation);
        uint value = (uint)priority;
        return !h.IsInvalid && NativeMethods.SetProcessInformation(h, Win32.ProcessMemoryPriorityClass, (byte*)&value, sizeof(uint));
    }

    private static SafeProcessHandle Open(int pid, uint access) => NativeMethods.OpenProcess(access, false, (uint)pid);
}
