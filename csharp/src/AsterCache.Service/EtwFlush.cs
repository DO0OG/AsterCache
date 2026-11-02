namespace AsterCache.Service;

/// 실시간 ETW 세션의 버퍼를 바로 내려 보냅니다 (FlushTraceW, evntrace.h: "request that all active buffers of an event trace session be flushed").
/// 실시간 세션은 버퍼가 차거나 플러시 타이머 (EVENT_TRACE_PROPERTIES.FlushTimer, 단위 초, 최소 1) 가 돌 때만 소비자에게 가므로, 프로세스 시작 같은
/// 드문 이벤트는 약 1.65 초 늦게 보였습니다 (S4 실측). 밀리초 플러시 플래그는 공개 SDK 헤더에 없어서, 문서화된 FlushTrace 를 짧은 간격으로 부릅니다.
/// 구현은 추적기 (CLI 의 aster learn 도 씀) 의 <see cref="AsterCache.Tracer.SessionFlush"/> 에 있습니다.
public static class EtwFlush
{
    /// 세션 이름으로 플러시. 0 이면 성공, 아니면 Win32 오류 코드 (세션이 없으면 4201).
    public static uint Flush(string sessionName) => AsterCache.Tracer.SessionFlush.Flush(sessionName);
}
