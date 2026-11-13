using System.Runtime.InteropServices;
using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// 대기 목록에서 읽은 두 값: 여유분 (여유 + 우선순위 0~4 대기) 바이트와 캐시 (우선순위 5~7) 재사용 누계 바이트.
public readonly record struct MemoryLists(long HeadroomBytes, long CacheRepurposedBytes);

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
        _ = Privilege.Value; // 못 켜도 이미 켜져 있을 수 있어 조회는 시도
        Span<nuint> v = stackalloc nuint[Count];
        fixed (nuint* p = v)
        {
            return NativeMethods.NtQuerySystemInformation(SystemMemoryListInformation, p, (uint)(Count * sizeof(nuint)), out _) >= 0
                ? Summarize(v, Environment.SystemPageSize)
                : null;
        }
    }

    /// 조회 결과 (페이지 수 배열) 를 두 값으로 줄입니다.
    public static MemoryLists Summarize(ReadOnlySpan<nuint> v, long pageSize)
    {
        long headroom = (long)v[Zero] + (long)v[Free];
        for (int i = 0; i < CachePriority; i++)
        {
            headroom += (long)v[ByPriority + i];
        }

        long cache = 0;
        for (int i = CachePriority; i < 8; i++)
        {
            cache += (long)v[Repurposed + i];
        }

        return new MemoryLists(headroom * pageSize, cache * pageSize);
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
