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
/// 버퍼: WPR 로 받은 .etl 에서 이벤트 수백만 건의 유실이 관찰돼 (TraceEvent 기본 64 MB 에서의 유실은 확인하지 못함) <see cref="DefaultBufferMB"/> 로 키웁니다.
/// 그 크기로 세션을 못 열면 <see cref="FallbackBufferMB"/> 로 한 번 다시 시도합니다. 유실 수는 <see cref="EventsLost"/> 로 남깁니다.
///
/// 알려진 한계: 세션을 켠 뒤 처리 스레드가 붙기까지의 짧은 틈과, 세션 시작 전에 열린 파일의 이름 (Stop 때 정리 이벤트로 옴) 은
/// 실제 실행으로 확인이 필요합니다. 이름을 못 푼 읽기는 <see cref="Unresolved"/> 로 셉니다.
public sealed class LiveSession : IDisposable
{
    public const string DefaultSessionName = "AsterCacheTrace";

    /// 세션 버퍼 총량 (MB). WPR 수집 .etl 에서 수백만 건의 유실이 관찰돼 키움.
    public const int DefaultBufferMB = 256;

    /// DefaultBufferMB 로 세션을 열지 못할 때 쓰는 TraceEvent 기본 크기.
    public const int FallbackBufferMB = 64;

    private readonly TraceEventSession _session;
    private readonly KernelCollector _collector;
    private readonly Task _pump;
    private readonly Timer? _flusher;

    private LiveSession(TraceEventSession session, KernelCollector collector, Task pump, Timer? flusher)
    {
        _session = session;
        _collector = collector;
        _pump = pump;
        _flusher = flusher;
    }

    /// 대상 프로세스 트리가 지금 하나라도 살아 있는가 / 세션이 한 번이라도 본 적이 있는가.
    public bool TargetsActive => _collector.TargetsActive;

    public bool SawTarget => _collector.SawTarget;

    public int Unresolved => _collector.Unresolved;

    /// 읽기 이름 해석 집계 (Stop 뒤에 읽음; 리스너 없이 시작했으면 Resolved·Late 는 0).
    public ReadNameStats NameStats => new(_collector.ResolvedReads, _collector.LateReads, _collector.Unresolved);

    /// 세션이 버퍼 부족으로 잃은 이벤트 수 (Stop 직전 값). 0 이 아니면 이 기록은 불완전합니다.
    public long EventsLost { get; private set; }

    /// 비관리자면 NotAdminException (세션을 만들기 전에 검사).
    public static LiveSession Start(IReadOnlyCollection<string> exeNames, string sessionName = DefaultSessionName, bool restartExisting = false,
        Func<bool>? isAdmin = null, DevicePaths? devices = null, int bufferMB = DefaultBufferMB, LiveListener? listener = null, TimeSpan? flushInterval = null)
    {
        Elevation.RequireAdmin(isAdmin);
        try
        {
            return Open(exeNames, sessionName, restartExisting, devices, bufferMB, listener, flushInterval);
        }
        catch (Exception) when (bufferMB > FallbackBufferMB)
        {
            return Open(exeNames, sessionName, restartExisting, devices, FallbackBufferMB, listener, flushInterval);
        }
    }

    private static LiveSession Open(
        IReadOnlyCollection<string> exeNames, string sessionName, bool restartExisting, DevicePaths? devices, int bufferMB, LiveListener? listener, TimeSpan? flushInterval)
    {
        var session = new TraceEventSession(sessionName,
            restartExisting ? TraceEventSessionOptions.Create : TraceEventSessionOptions.NoRestartOnCreate);
        try
        {
            session.BufferSizeMB = bufferMB; // 제공자를 켜기 전에만 설정 가능
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.FileIO | KernelTraceEventParser.Keywords.FileIOInit);
            var collector = new KernelCollector(session.Source.Kernel, exeNames, devices ?? DevicePaths.FromSystem(), listener);
            Task pump = Task.Run(() => session.Source.Process());
            // 실시간 알림을 받는 쪽은 버퍼가 차기를 기다리지 않도록 짧은 간격으로 내려 보냄 (SessionFlush)
            Timer? flusher = flushInterval is { } every ? new Timer(_ => SessionFlush.Flush(sessionName), null, every, every) : null;
            return new LiveSession(session, collector, pump, flusher);
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
        _flusher?.Dispose();
        EventsLost = _session.EventsLost;
        _session.Stop();
        _pump.Wait();
        return _collector.Finish();
    }

    public void Dispose()
    {
        _flusher?.Dispose();
        _session.Dispose();
    }
}
