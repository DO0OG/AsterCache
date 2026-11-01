using AsterCache.Windows;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace AsterCache.Tracer;

/// 관리자 전용 실시간 커널 ETW 세션 (Process + FileIO + FileIOInit 키워드). 읽기 이벤트는 FileIOInit, 파일 이름은 FileIO 키워드가 켭니다.
///
/// 세션 이름: TraceEvent 문서 (TraceEventSession.EnableKernelProvider) 는 "Windows 8 전에는 세션 이름이 NT Kernel Logger 여야 한다" 고 적습니다.
/// NT Kernel Logger 는 시스템에 하나뿐이라 wpr·xperf·Procmon 등과 겹치면 서로를 끕니다. Windows 8 이상에서는 다른 이름의 세션에서도
/// 커널 이벤트를 켤 수 있으므로 (시스템 로거 세션, 최대 8개) 기본 이름은 고유한 <see cref="DefaultSessionName"/> 입니다.
/// 같은 이름의 세션이 이미 있으면 기본은 실패합니다 (NoRestartOnCreate: 남의 세션을 끄지 않음). 이전 실행이 남긴 고아 세션이면
/// restartExisting 으로 다시 만듭니다. 이 코드는 관리자로 실행해 보지 않았습니다 (사용자 스크립트 S3 에서 확인).
///
/// 알려진 한계: 세션을 켠 뒤 처리 스레드가 붙기까지의 짧은 틈과, 세션 시작 전에 열린 파일의 이름 (Stop 때 정리 이벤트로 옴) 은
/// 실제 실행으로 확인이 필요합니다. 이름을 못 푼 읽기는 <see cref="Unresolved"/> 로 셉니다.
public sealed class LiveSession : IDisposable
{
    public const string DefaultSessionName = "AsterCacheTrace";

    private readonly TraceEventSession _session;
    private readonly KernelCollector _collector;
    private readonly Task _pump;

    private LiveSession(TraceEventSession session, KernelCollector collector, Task pump)
    {
        _session = session;
        _collector = collector;
        _pump = pump;
    }

    public int Unresolved => _collector.Unresolved;

    /// 비관리자면 NotAdminException (세션을 만들기 전에 검사).
    public static LiveSession Start(IReadOnlyCollection<string> exeNames, string sessionName = DefaultSessionName, bool restartExisting = false,
        Func<bool>? isAdmin = null, DevicePaths? devices = null)
    {
        Elevation.RequireAdmin(isAdmin);
        var session = new TraceEventSession(sessionName,
            restartExisting ? TraceEventSessionOptions.Create : TraceEventSessionOptions.NoRestartOnCreate);
        try
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit);
            var collector = new KernelCollector(session.Source.Kernel, exeNames, devices ?? DevicePaths.FromSystem());
            Task pump = Task.Run(() => session.Source.Process());
            return new LiveSession(session, collector, pump);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    /// 세션을 멈추고 남은 이벤트까지 처리한 뒤 모은 기록을 돌려줍니다.
    public List<TraceRecord> Stop()
    {
        _session.Stop();
        _pump.Wait();
        return _collector.Finish();
    }

    public void Dispose() => _session.Dispose();
}
