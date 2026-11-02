using System.Runtime.InteropServices;

namespace AsterCache.Tracer;

/// 실시간 ETW 세션의 버퍼를 바로 내려 보냅니다 (FlushTraceW, evntrace.h: "request that all active buffers of an event trace session be flushed").
/// 실시간 세션은 버퍼가 차거나 플러시 타이머 (EVENT_TRACE_PROPERTIES.FlushTimer, 단위 초, 최소 1) 가 돌 때만 소비자에게 가므로, 프로세스 시작 같은
/// 드문 이벤트는 약 1.65 초 늦게 보였습니다 (S4 실측). 밀리초 플러시 플래그는 공개 SDK 헤더에 없어서, 문서화된 FlushTrace 를 짧은 간격으로 부릅니다.
public static unsafe partial class SessionFlush
{
    // EVENT_TRACE_PROPERTIES (x64): WNODE_HEADER 48 바이트 + 본문 72 바이트 = 120. 뒤에 로그 파일 이름·세션 이름 자리.
    private const int PropertiesSize = 120;
    private const int NameChars = 1024;
    private const int WnodeFlagTracedGuid = 0x00020000;
    private const int LoggerNameOffsetAt = 116;
    private const int LogFileNameOffsetAt = 112;

    /// 세션 이름으로 플러시. 0 이면 성공, 아니면 Win32 오류 코드 (세션이 없으면 4201).
    public static uint Flush(string sessionName)
    {
        int total = PropertiesSize + (2 * NameChars * sizeof(char));
        byte[] buffer = new byte[total];
        fixed (byte* p = buffer)
        {
            *(int*)p = total; // Wnode.BufferSize
            *(int*)(p + 44) = WnodeFlagTracedGuid; // Wnode.Flags
            *(int*)(p + LogFileNameOffsetAt) = PropertiesSize;
            *(int*)(p + LoggerNameOffsetAt) = PropertiesSize + (NameChars * sizeof(char));
            return FlushTraceW(0, sessionName, p);
        }
    }

    [LibraryImport("advapi32.dll", EntryPoint = "FlushTraceW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint FlushTraceW(ulong traceId, string instanceName, byte* properties);
}
