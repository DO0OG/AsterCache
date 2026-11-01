using System.Diagnostics;
using AsterCache.Windows;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace AsterCache.Service;

/// ETW Microsoft-Windows-Kernel-Process 의 ProcessStart (이벤트 1) 실시간 구독. 관리자/SYSTEM 전용 (비관리자는 NotAdminException).
/// NT Kernel Logger 는 쓰지 않으므로 추적기(Tracer)의 커널 세션과 겹치지 않습니다. 세션 이름은 서비스 전용 하나.
public sealed class EtwProcessWatcher : IProcessWatcher
{
    public const string SessionName = "AsterCache-ProcessWatch";

    private static readonly Guid KernelProcess = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
    private const ulong ProcessKeyword = 0x10; // WINEVENT_KEYWORD_PROCESS
    private const int ProcessStartId = 1;

    private readonly HashSet<string> _names;
    private readonly Func<bool> _isAdmin;

    public EtwProcessWatcher(IEnumerable<string> exeNames, Func<bool>? isAdmin = null)
    {
        _names = new HashSet<string>(exeNames, StringComparer.OrdinalIgnoreCase);
        _isAdmin = isAdmin ?? Elevation.IsAdmin;
    }

    public Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
    {
        Elevation.RequireAdmin(_isAdmin);
        return Task.Run(() =>
        {
            using var session = new TraceEventSession(SessionName);
            using CancellationTokenRegistration stop = cancellationToken.Register(() => session.Stop());
            session.EnableProvider(KernelProcess, TraceEventLevel.Informational, ProcessKeyword);
            var parser = new RegisteredTraceEventParser(session.Source);
            parser.All += data =>
            {
                if (data.ProviderGuid == KernelProcess && (int)data.ID == ProcessStartId
                    && ExeNameOf(data.PayloadByName("ImageName") as string) is { } exe && _names.Contains(exe)
                    && data.PayloadByName("ProcessID") is { } pid)
                {
                    onStart(new ProcessStart(Convert.ToInt32(pid), exe, Stopwatch.GetTimestamp()));
                }
            };
            session.Source.Process();
        }, CancellationToken.None);
    }

    /// ImageName ("\Device\HarddiskVolume3\Windows\System32\cmd.exe") 의 파일 이름. 비었으면 null.
    public static string? ExeNameOf(string? imageName)
    {
        if (string.IsNullOrEmpty(imageName))
        {
            return null;
        }

        string name = imageName[(imageName.LastIndexOf('\\') + 1)..];
        return name.Length == 0 ? null : name;
    }
}
