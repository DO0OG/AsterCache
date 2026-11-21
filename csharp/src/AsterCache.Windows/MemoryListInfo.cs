using System.Runtime.InteropServices;
using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// 대기 목록에서 읽은 값: 여유분 (여유 + 우선순위 0~4 대기) 바이트, 캐시 (우선순위 5~7) 재사용 누계 바이트, 캐시 (우선순위 5~7) 대기 바이트, 우선순위 5 대기 바이트 (만진 페이지가 들어가는 목록, 모르면 -1).
public readonly record struct MemoryLists(long HeadroomBytes, long CacheRepurposedBytes, long CacheStandbyBytes = 0, long Priority5StandbyBytes = -1);

/// 우선순위 0~7 별 대기 목록의 크기와 재사용 누계 (바이트, 배열 길이 8), 여유 + 0 으로 채운 목록 바이트 (플레이 기록용).
public readonly record struct MemoryPriorityLists(long[] StandbyBytes, long[] RepurposedBytes, long FreeBytes);

/// 메모리 목록 조회 (NtQuerySystemInformation SystemMemoryListInformation): 프로세스를 열지 않는 시스템 전체 통계입니다.
/// 필요한 SeProfileSingleProcessPrivilege 는 처음 한 번 켭니다 (서비스는 LocalSystem). 실패하면 null.
public static unsafe class MemoryListInfo
{
    private const int SystemMemoryListInformation = 80;
    private const int Zero = 0;
    private const int Free = 1;
    private const int ByPriority = 5; // 8개
    private const int Repurposed = 13; // 8개
    private const int Count = 22; // SIZE_T 22개 (뒤쪽 ModifiedPageCountPageFile 까지)
    private const int CachePriority = 5; // 보통 파일 캐시 (예열 캐시 포함) 가 있는 우선순위

    private static readonly Lazy<bool> Privilege = new(() => EnablePrivilege("SeProfileSingleProcessPrivilege"));

    public static MemoryLists? Query()
    {
        Span<nuint> v = stackalloc nuint[Count];
        return Read(v) ? Summarize(v, Environment.SystemPageSize) : null;
    }

    /// 같은 조회에서 우선순위별 값을 그대로 꺼냅니다 (플레이 기록이 30 초마다 남김). 조회가 안 되면 null.
    public static MemoryPriorityLists? QueryPriorities()
    {
        Span<nuint> v = stackalloc nuint[Count];
        return Read(v) ? Priorities(v, Environment.SystemPageSize) : null;
    }

    private static bool Read(Span<nuint> v)
    {
        _ = Privilege.Value; // 못 켜도 이미 켜져 있을 수 있어 조회는 시도
        fixed (nuint* p = v)
        {
            return NativeMethods.NtQuerySystemInformation(SystemMemoryListInformation, p, (uint)(Count * sizeof(nuint)), out _) >= 0;
        }
    }

    /// 조회 결과 (페이지 수 배열) 를 우선순위별 바이트로 풉니다.
    public static MemoryPriorityLists Priorities(ReadOnlySpan<nuint> v, long pageSize)
    {
        var standby = new long[8];
        var repurposed = new long[8];
        for (int i = 0; i < 8; i++)
        {
            standby[i] = (long)v[ByPriority + i] * pageSize;
            repurposed[i] = (long)v[Repurposed + i] * pageSize;
        }

        return new MemoryPriorityLists(standby, repurposed, ((long)v[Zero] + (long)v[Free]) * pageSize);
    }

    /// 조회 결과 (페이지 수 배열) 를 세 값으로 줄입니다.
    public static MemoryLists Summarize(ReadOnlySpan<nuint> v, long pageSize)
    {
        long headroom = (long)v[Zero] + (long)v[Free];
        for (int i = 0; i < CachePriority; i++)
        {
            headroom += (long)v[ByPriority + i];
        }

        long cache = 0;
        long standby = 0;
        for (int i = CachePriority; i < 8; i++)
        {
            cache += (long)v[Repurposed + i];
            standby += (long)v[ByPriority + i];
        }

        return new MemoryLists(headroom * pageSize, cache * pageSize, standby * pageSize, (long)v[ByPriority + CachePriority] * pageSize);
    }

    private static bool EnablePrivilege(string name)
    {
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), Win32.TokenAdjustPrivileges | Win32.TokenQuery, out SafeAccessTokenHandle token))
        {
            return false;
        }

        using (token)
        {
            if (!NativeMethods.LookupPrivilegeValueW(null, name, out Luid luid))
            {
                return false;
            }

            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = Win32.PrivilegeEnabled };
            // 일부만 부여돼도 TRUE 를 돌려주므로 마지막 오류가 0 인지 봅니다 (ERROR_NOT_ALL_ASSIGNED = 1300).
            return NativeMethods.AdjustTokenPrivileges(token, false, &privileges, (uint)sizeof(TokenPrivileges), 0, 0) && Marshal.GetLastPInvokeError() == 0;
        }
    }
}
