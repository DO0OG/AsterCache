using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// 다른 프로세스의 경로·작업 집합·생성 시각 (FILETIME)·부모 pid (모르면 0). 읽을 수 없으면 Describe 가 null.
public readonly record struct ProcessInfo(string Path, long WorkingSetBytes, long Created, int Parent = 0);

/// 배경 프로세스의 메모리 양보에 쓰는 프로세스 단위 호출. 다른 프로세스에 하는 일은 작업 집합 비우기와 메모리·I/O·CPU 우선순위 변경 네 가지뿐입니다.
/// 실패 (접근 거부, 이미 종료) 는 예외가 아니라 null/false 이며 핸들은 항상 닫습니다. 열기 권한은 호출마다 필요한 최소: 조회 0x1000, 비우기 0x1100, 우선순위 쓰기 0x0200.
public static unsafe class ProcessMemory
{
    public const int PriorityVeryLow = 1;

    /// 프로세스 I/O 우선순위 (IO_PRIORITY_HINT 값 그대로): 낮음 1, 보통 2. 매우 낮음 (0) 은 다른 작업이 거의 멈춰 쓰지 않습니다.
    public const int IoPriorityLow = 1;

    public const int IoPriorityNormal = 2;

    /// CPU 우선순위 등급 (GetPriorityClass 값 그대로): 보통 0x20, 보통보다 낮음 0x4000. 낮음 쪽은 Windows 가 부모에게서 자식으로 물려줍니다.
    public const int CpuClassNormal = 0x20;

    public const int CpuClassBelowNormal = 0x4000;

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

        // PROCESS_BASIC_INFORMATION 은 포인터 크기 칸 여섯 개이고 마지막이 부모 pid 입니다 (부모가 끝나도 남음). 못 읽으면 0.
        nuint* basic = stackalloc nuint[6];
        int parent = NativeMethods.NtQueryInformationProcess(h, Win32.ProcessBasicInformation, basic, (uint)(6 * sizeof(nuint)), out _) >= 0 ? (int)basic[5] : 0;
        return new ProcessInfo(new string(buf, 0, (int)size), (long)Math.Min(counters.WorkingSetSize, long.MaxValue), created, parent);
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

    /// 프로세스 I/O 우선순위 (0~3, 보통 2).
    public static int? IoPriority(int pid)
    {
        using SafeProcessHandle h = Open(pid, Win32.ProcessQueryLimitedInformation);
        if (h.IsInvalid)
        {
            return null;
        }

        uint value = 0;
        return NativeMethods.NtQueryInformationProcess(h, Win32.ProcessIoPriorityClass, (nuint*)&value, sizeof(uint), out _) >= 0 ? (int)value : null;
    }

    public static bool SetIoPriority(int pid, int priority)
    {
        using SafeProcessHandle h = Open(pid, Win32.ProcessSetInformation);
        uint value = (uint)priority;
        return !h.IsInvalid && NativeMethods.NtSetInformationProcess(h, Win32.ProcessIoPriorityClass, &value, sizeof(uint)) >= 0;
    }

    /// CPU 우선순위 등급 (GetPriorityClass 값 그대로, 못 읽으면 null).
    public static int? CpuPriorityClass(int pid)
    {
        using SafeProcessHandle h = Open(pid, Win32.ProcessQueryLimitedInformation);
        if (h.IsInvalid)
        {
            return null;
        }

        uint value = NativeMethods.GetPriorityClass(h);
        return value == 0 ? null : (int)value;
    }

    public static bool SetCpuPriorityClass(int pid, int priorityClass)
    {
        using SafeProcessHandle h = Open(pid, Win32.ProcessSetInformation);
        return !h.IsInvalid && NativeMethods.SetPriorityClass(h, (uint)priorityClass);
    }

    private static SafeProcessHandle Open(int pid, uint access) => NativeMethods.OpenProcess(access, false, (uint)pid);
}
