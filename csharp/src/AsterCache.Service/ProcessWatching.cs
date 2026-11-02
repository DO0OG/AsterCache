using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AsterCache.Service;

/// 감시 대상 프로세스가 새로 떴다는 알림. Timestamp 는 감지 시각 (Stopwatch.GetTimestamp).
public sealed record ProcessStart(int Pid, string ExeName, long Timestamp, string? Route = null);

/// 프로세스 시작 감지. RunAsync 는 취소될 때까지 onStart 를 부르고, 취소되면 정상 반환합니다.
public interface IProcessWatcher
{
    Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken);
}

/// 프로세스 시작 폴링 (기본 감시기). 기본 경로는 PID 목록만 (K32EnumProcesses, 한 번에 약 0.05 ms) 받아 새 PID 의 이미지 이름만 조회합니다.
/// 전체 이름 스냅샷 (Toolhelp) 은 한 번에 약 10 ms 라 50 ms 간격이면 한 코어의 25% 였습니다. snapshot 을 주면 (시험용) 그 스냅샷으로 비교합니다.
/// 처음 목록은 기준선이라 이미 떠 있던 프로세스는 알리지 않습니다. 스냅샷 경로는 같은 PID 의 이름이 바뀌면 새 프로세스로 봅니다.
/// PID 경로의 한계: 두 틱 사이에 떴다가 끝난 프로세스와, 틱 사이에 재사용된 PID 는 보지 못합니다 (게임 시작 감지에는 영향 없음).
public sealed class PollingProcessWatcher : IProcessWatcher
{
    private readonly HashSet<string> _names;
    private readonly TimeSpan _interval;
    private readonly Func<IReadOnlyList<(int Pid, string Exe)>>? _snapshot;

    public PollingProcessWatcher(IEnumerable<string> exeNames, TimeSpan interval, Func<IReadOnlyList<(int Pid, string Exe)>>? snapshot = null)
    {
        _names = new HashSet<string>(exeNames, StringComparer.OrdinalIgnoreCase);
        _interval = interval;
        _snapshot = snapshot;
    }

    public async Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
    {
        if (_snapshot is null)
        {
            await RunPidsAsync(onStart, cancellationToken);
            return;
        }

        var known = new Dictionary<int, string>();
        bool first = true;
        using var timer = new PeriodicTimer(_interval);
        try
        {
            do
            {
                Dictionary<int, string> now = Diff(_snapshot(), known, first ? null : onStart);
                known = now;
                first = false;
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
            // 취소는 정상 종료
        }
    }

    private async Task RunPidsAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
    {
        HashSet<int> known = [.. ProcessSnapshot.Pids()];
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                IReadOnlyList<int> now = ProcessSnapshot.Pids();
                foreach (int pid in now)
                {
                    if (!known.Contains(pid) && ProcessSnapshot.ImageName(pid) is { } exe && _names.Contains(exe))
                    {
                        onStart(new ProcessStart(pid, exe, Stopwatch.GetTimestamp(), "poll"));
                    }
                }

                known = [.. now];
            }
        }
        catch (OperationCanceledException)
        {
            // 취소는 정상 종료
        }
    }

    private Dictionary<int, string> Diff(IReadOnlyList<(int Pid, string Exe)> procs, Dictionary<int, string> known, Action<ProcessStart>? onStart)
    {
        var now = new Dictionary<int, string>(procs.Count);
        foreach ((int pid, string exe) in procs)
        {
            now[pid] = exe;
            if (onStart is not null && _names.Contains(exe) && !(known.TryGetValue(pid, out string? old) && string.Equals(old, exe, StringComparison.OrdinalIgnoreCase)))
            {
                onStart(new ProcessStart(pid, exe, Stopwatch.GetTimestamp(), "poll"));
            }
        }

        return now;
    }
}

/// 실행 중인 프로세스 (PID, 이미지 이름). Toolhelp32 스냅샷.
public static unsafe partial class ProcessSnapshot
{
    private const uint SnapProcess = 2;

    public static IReadOnlyList<(int Pid, string Exe)> Take()
    {
        nint snap = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snap == -1)
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
        }

        var list = new List<(int, string)>(512);
        try
        {
            ProcessEntry e = default;
            e.Size = (uint)sizeof(ProcessEntry);
            for (bool ok = Process32FirstW(snap, &e); ok; ok = Process32NextW(snap, &e))
            {
                list.Add(((int)e.ProcessId, new string(e.ExeFile)));
            }
        }
        finally
        {
            CloseHandle(snap);
        }

        return list;
    }

    /// 실행 중인 PID 목록만 (이름 없음). 전체 스냅샷보다 200배 가볍습니다.
    public static IReadOnlyList<int> Pids()
    {
        uint[] ids = new uint[2048];
        while (true)
        {
            uint needed;
            fixed (uint* p = ids)
            {
                if (!EnumProcesses(p, (uint)(ids.Length * sizeof(uint)), out needed))
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
                }
            }

            if (needed < ids.Length * sizeof(uint))
            {
                return ids.Take((int)(needed / sizeof(uint))).Select(x => (int)x).ToList();
            }

            ids = new uint[ids.Length * 2]; // 꽉 찼으면 더 있을 수 있음
        }
    }

    /// 프로세스 이미지의 파일 이름 (예: cmd.exe). 열 수 없거나 이미 끝났으면 null.
    public static string? ImageName(int pid)
    {
        nint h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
        if (h == 0)
        {
            return null;
        }

        try
        {
            char* buf = stackalloc char[1024];
            uint size = 1024;
            if (!QueryFullProcessImageNameW(h, 0, buf, ref size))
            {
                return null;
            }

            string full = new(buf, 0, (int)size);
            return full[(full.LastIndexOf('\\') + 1)..];
        }
        finally
        {
            CloseHandle(h);
        }
    }

    public static bool IsRunning(IEnumerable<string> exeNames)
    {
        var names = new HashSet<string>(exeNames, StringComparer.OrdinalIgnoreCase);
        return Take().Any(p => names.Contains(p.Exe));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed char ExeFile[260];
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32FirstW(nint snapshot, ProcessEntry* entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Process32NextW(nint snapshot, ProcessEntry* entry);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [LibraryImport("kernel32.dll", EntryPoint = "K32EnumProcesses", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EnumProcesses(uint* processIds, uint cb, out uint bytesReturned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageNameW(nint process, uint flags, char* exeName, ref uint size);
}
