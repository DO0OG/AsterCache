using AsterCache.Windows.Native;

namespace AsterCache.Windows;

/// 시스템 전체가 디스크에서 읽어 들인 쪽 수의 누계 (NtQuerySystemInformation SystemPerformanceInformation 의 PageReadCount): 프로세스를 열지 않는 시스템 통계이고 권한이 필요 없습니다.
/// 만진 범위가 디스크에서 읽혔는지를 CPU 부하와 무관하게 알려 줍니다. ULONG 이라 넘칠 수 있으니 차이는 부호 없는 뺄셈으로 구하세요.
public static unsafe class SystemPageReads
{
    private const int SystemPerformanceInformation = 2;
    private const int PageReadCountOffset = 80; // 앞쪽 배치: 8 바이트 4 개 (32) + ULONG 12 개 (48) 뒤의 PageReadCount, 바로 뒤 84 가 PageReadIoCount
    private const int BufferBytes = 1024; // 구조체는 이 PC 에서 376 바이트

    /// 지금의 누계, 읽을 수 없으면 (실패하거나 구조체가 짧음) null.
    public static uint? Query()
    {
        nuint* buffer = stackalloc nuint[BufferBytes / sizeof(nuint)];
        if (NativeMethods.NtQuerySystemInformation(SystemPerformanceInformation, buffer, BufferBytes, out uint returned) < 0 || returned < PageReadCountOffset + sizeof(uint))
        {
            return null;
        }

        return *(uint*)((byte*)buffer + PageReadCountOffset);
    }
}
