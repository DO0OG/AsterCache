using System.Collections.Concurrent;
using AsterCache.Tracer;
using AsterCache.Windows;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace AsterCache.Service;

/// 휴리스틱의 읽기량 대체 공급자: 프로세스를 열 수 없을 때 (보호된 프로세스) 커널 ETW 가 센 값을 돌려줍니다. 만들면 세션이 켜지고 Dispose 하면 꺼집니다.
public interface IReadWatch : IDisposable
{
    /// 세션을 켠 뒤 그 프로세스가 읽은 누적 바이트.
    long ReadBytes(int pid);

    /// 세션이 본 프로세스 이미지 전체 경로 (드라이브 문자 경로), 모르면 null.
    string? ImagePath(int pid);
}

/// 커널 세션 "AsterCacheHeuristic" (Process + FileIOInit): FileIO Read 이벤트의 IoSize 를 프로세스 ID 별로 더하고, Process Start/DCStart 의 이미지 경로를 적습니다.
/// 게임 프로세스를 열지 않고 (핸들 불필요) 읽기만 셉니다. 실제 세션은 관리자로 실행해 보지 않았습니다.
public sealed class EtwReadWatch : IReadWatch
{
    public const string SessionName = "AsterCacheHeuristic";
    private static readonly TimeSpan PumpWait = TimeSpan.FromSeconds(5);

    private readonly TraceEventSession _session;
    private readonly Task _pump;
    private readonly ConcurrentDictionary<int, long> _reads = new();
    private readonly ConcurrentDictionary<int, string> _images = new();
    private readonly DevicePaths _devices = DevicePaths.FromSystem();

    private EtwReadWatch(TraceEventSession session)
    {
        _session = session;
        KernelTraceEventParser kernel = session.Source.Kernel;
        kernel.ProcessStart += d => _images[d.ProcessID] = ImageOf(d.KernelImageFileName, d.ImageFileName);
        kernel.ProcessDCStart += d => _images[d.ProcessID] = ImageOf(d.KernelImageFileName, d.ImageFileName);
        kernel.ProcessStop += d =>
        {
            _images.TryRemove(d.ProcessID, out _);
            _reads.TryRemove(d.ProcessID, out _);
        };
        kernel.FileIORead += d => _reads.AddOrUpdate(d.ProcessID, d.IoSize, (_, sum) => sum + d.IoSize);
        _pump = Task.Run(() => session.Source.Process());
    }

    /// 비관리자면 NotAdminException. 같은 이름의 남은 세션이 있으면 (이전 실행의 고아) 다시 만듭니다.
    public static EtwReadWatch Start(Func<bool>? isAdmin = null)
    {
        Elevation.RequireAdmin(isAdmin);
        var session = new TraceEventSession(SessionName, TraceEventSessionOptions.Create);
        try
        {
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.Process | KernelTraceEventParser.Keywords.FileIOInit);
            return new EtwReadWatch(session);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public long ReadBytes(int pid) => _reads.GetValueOrDefault(pid);

    public string? ImagePath(int pid) => _images.TryGetValue(pid, out string? p) && p.Length > 2 && p[1] == ':' ? p : null;

    public void Dispose()
    {
        _session.Dispose(); // 세션을 멈추면 처리 스레드가 끝남
        _pump.Wait(PumpWait);
    }

    // 전체 경로(장치 경로)가 있으면 그것을 드라이브 문자 경로로, 없으면 짧은 이름 (경로가 아니라 ImagePath 에서 걸러짐).
    private string ImageOf(string? kernelImage, string? shortName) =>
        !string.IsNullOrEmpty(kernelImage) ? _devices.ToDosPath(kernelImage) : shortName ?? string.Empty;
}
