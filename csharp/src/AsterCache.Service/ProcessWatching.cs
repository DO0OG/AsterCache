using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AsterCache.Service;

/// 감시 대상 프로세스가 새로 떴다는 알림. Timestamp 는 감지 시각 (Stopwatch.GetTimestamp).
public sealed record ProcessStart(int Pid, string ExeName, long Timestamp);

/// 프로세스 시작 감지. RunAsync 는 취소될 때까지 onStart 를 부르고, 취소되면 정상 반환합니다.
public interface IProcessWatcher
{
    Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken);
}

/// Toolhelp 스냅샷 폴링 (비관리자 기본, 250 ms: p95 266 ms, 한 코어의 5.6%. 50 ms 는 p95 70 ms 지만 27%라 쓰지 않음).
/// 처음 스냅샷은 기준선이라 이미 떠 있던 프로세스는 알리지 않습니다. 같은 PID 의 이름이 바뀌면 새 프로세스로 봅니다.
public sealed class PollingProcessWatcher : IProcessWatcher
{
    private readonly HashSet<string> _names;
    private readonly TimeSpan _interval;
    private readonly Func<IReadOnlyList<(int Pid, string Exe)>> _snapshot;

    public PollingProcessWatcher(IEnumerable<string> exeNames, TimeSpan interval, Func<IReadOnlyList<(int Pid, string Exe)>>? snapshot = null)
    {
        _names = new HashSet<string>(exeNames, StringComparer.OrdinalIgnoreCase);
        _interval = interval;
        _snapshot = snapshot ?? ProcessSnapshot.Take;
    }

    public async Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
    {
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

    private Dictionary<int, string> Diff(IReadOnlyList<(int Pid, string Exe)> procs, Dictionary<int, string> known, Action<ProcessStart>? onStart)
    {
        var now = new Dictionary<int, string>(procs.Count);
        foreach ((int pid, string exe) in procs)
        {
            now[pid] = exe;
            if (onStart is not null && _names.Contains(exe) && !(known.TryGetValue(pid, out string? old) && string.Equals(old, exe, StringComparison.OrdinalIgnoreCase)))
            {
                onStart(new ProcessStart(pid, exe, Stopwatch.GetTimestamp()));
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
}
