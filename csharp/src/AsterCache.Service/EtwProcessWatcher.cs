using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using AsterCache.Windows;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Session;

namespace AsterCache.Service;

/// ETW Microsoft-Windows-Kernel-Process 의 ProcessStart (이벤트 1) 실시간 구독. 관리자/SYSTEM 전용 (비관리자는 NotAdminException).
/// NT Kernel Logger 는 쓰지 않으므로 추적기(Tracer)의 커널 세션과 겹치지 않습니다. 세션 이름은 서비스 전용 하나.
/// 같은 이벤트를 두 길로 받습니다: TDH 로 푼 필드 (RegisteredTraceEventParser) 와 원시 페이로드 (AllEvents, <see cref="ProcessStartPayload"/>).
/// 앞쪽이 어떤 이유로 풀지 못해도 감지가 끊기지 않게 하려는 것이고, 두 길이 같은 시작을 알리면 한 번만 넘깁니다.
/// ETW 실시간 세션은 버퍼를 약 1초마다 내려 보내므로 폴링 (250 ms) 보다 감지가 늦을 수 있습니다.
/// silenceTimeout 동안 (감시 대상이 아니어도) 시작 이벤트가 하나도 안 오면 예외로 끝내 Worker 가 폴링으로 내려가게 합니다.
public sealed class EtwProcessWatcher : IProcessWatcher
{
    public const string SessionName = "AsterCache-ProcessWatch";
    public static readonly TimeSpan DefaultSilenceTimeout = TimeSpan.FromSeconds(60);

    private static readonly Guid KernelProcess = new("22FB2CD6-0E7B-422B-A0C7-2FAD1FD0E716");
    private const ulong ProcessKeyword = 0x10; // WINEVENT_KEYWORD_PROCESS
    private const int ProcessStartId = 1;
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(2);

    private readonly HashSet<string> _names;
    private readonly Func<bool> _isAdmin;
    private readonly EtwWatchDiagnostics? _diag;
    private readonly TimeSpan _silenceTimeout;
    private int _seen;

    public EtwProcessWatcher(IEnumerable<string> exeNames, Func<bool>? isAdmin = null, EtwWatchDiagnostics? diagnostics = null, TimeSpan? silenceTimeout = null)
    {
        _names = new HashSet<string>(exeNames, StringComparer.OrdinalIgnoreCase);
        _isAdmin = isAdmin ?? Elevation.IsAdmin;
        _diag = diagnostics;
        _silenceTimeout = silenceTimeout ?? DefaultSilenceTimeout;
    }

    public Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
    {
        Elevation.RequireAdmin(_isAdmin);
        return Task.Run(() =>
        {
            try
            {
                Watch(onStart, cancellationToken);
            }
            catch (Exception e) when (_diag is not null)
            {
                _diag.Write($"RunAsync exception: {e}");
                throw;
            }
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

    private void Watch(Action<ProcessStart> onStart, CancellationToken cancellationToken)
    {
        using var session = new TraceEventSession(SessionName);
        using CancellationTokenRegistration stop = cancellationToken.Register(() => session.Stop());
        session.EnableProvider(KernelProcess, TraceEventLevel.Informational, ProcessKeyword);
        var recent = new ConcurrentDictionary<int, long>();

        void Report(int pid, string? exe, string route)
        {
            _diag?.Count("start via " + route);
            if (exe is null || !_names.Contains(exe))
            {
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (recent.Count > 4096)
            {
                recent.Clear();
            }

            if (recent.TryGetValue(pid, out long before) && Stopwatch.GetElapsedTime(before, now) < DuplicateWindow)
            {
                return; // 다른 길로 이미 알림
            }

            recent[pid] = now;
            onStart(new ProcessStart(pid, exe, now));
        }

        session.Source.AllEvents += data =>
        {
            if (data.ProviderGuid != KernelProcess)
            {
                return;
            }

            _diag?.Count($"raw event id={(int)data.ID}");
            if ((int)data.ID != ProcessStartId)
            {
                return;
            }

            Interlocked.Increment(ref _seen);
            byte[] payload = data.EventData();
            bool ok = ProcessStartPayload.TryParse(payload, out int pid, out string? image);
            _diag?.Once("raw", () => $"first raw ProcessStart: payload {payload.Length} bytes, parsed={ok}, pid={pid}, image={image}");
            if (ok)
            {
                Report(pid, ExeNameOf(image), "raw");
            }
        };

        var parser = new RegisteredTraceEventParser(session.Source);
        parser.All += data =>
        {
            if (data.ProviderGuid != KernelProcess)
            {
                return;
            }

            _diag?.Count($"decoded event id={(int)data.ID}");
            if ((int)data.ID != ProcessStartId)
            {
                return;
            }

            _diag?.Once("decoded", () => $"first decoded ProcessStart: PayloadNames=[{string.Join(", ", data.PayloadNames)}], ImageName={data.PayloadByName("ImageName")}, ProcessID={data.PayloadByName("ProcessID")}");
            if (ExeNameOf(data.PayloadByName("ImageName") as string) is { } exe && data.PayloadByName("ProcessID") is { } pid)
            {
                Report(Convert.ToInt32(pid), exe, "decoded");
            }
        };

        bool silent = false;
        using var watchdog = new Timer(_ =>
        {
            if (Volatile.Read(ref _seen) == 0)
            {
                silent = true;
                session.Stop();
            }
        }, null, _silenceTimeout, Timeout.InfiniteTimeSpan);
        session.Source.Process();
        if (silent)
        {
            throw new InvalidOperationException($"no Kernel-Process start event arrived in {_silenceTimeout.TotalSeconds:F0} s");
        }
    }
}

/// Kernel-Process 이벤트 1 (ProcessStart) 의 원시 페이로드에서 pid 와 이미지 경로를 꺼냅니다. TDH 없이 동작: ProcessID 는 모든 버전에서 첫 4바이트이고,
/// ImageName 은 SID 뒤의 UTF-16 문자열이라 ".exe" 로 끝나는 첫 경로를 찾습니다 (뒤의 패키지 이름 문자열은 쓰지 않음).
public static class ProcessStartPayload
{
    private const int MaxChars = 1024;

    public static bool TryParse(ReadOnlySpan<byte> payload, out int pid, out string? image)
    {
        pid = 0;
        image = null;
        if (payload.Length < 8)
        {
            return false;
        }

        pid = (int)BinaryPrimitives.ReadUInt32LittleEndian(payload);
        image = FirstExePath(payload[4..]);
        return image is not null;
    }

    private static string? FirstExePath(ReadOnlySpan<byte> bytes)
    {
        for (int parity = 0; parity < 2; parity++)
        {
            var run = new System.Text.StringBuilder();
            for (int i = parity; i + 1 < bytes.Length; i += 2)
            {
                char c = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes[i..]);
                if (c >= ' ' && run.Length < MaxChars)
                {
                    run.Append(c);
                    continue;
                }

                string s = run.ToString();
                if (s.Contains('\\', StringComparison.Ordinal) && s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return s;
                }

                run.Clear();
            }
        }

        return null;
    }
}

/// 감시 진단 (--watch-test): 세션으로 들어온 이벤트를 길·ID 별로 세고, 첫 이벤트의 모양을 한 번씩 찍습니다.
public sealed class EtwWatchDiagnostics(Action<string> write)
{
    private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, bool> _once = new(StringComparer.Ordinal);

    public IReadOnlyDictionary<string, long> Counts => _counts;

    public void Write(string line) => write(line);

    public void Count(string key) => _counts.AddOrUpdate(key, 1, (_, v) => v + 1);

    public void Once(string key, Func<string> line)
    {
        if (_once.TryAdd(key, true))
        {
            write(line());
        }
    }
}
