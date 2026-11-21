using System.Diagnostics;
using AsterCache.Windows;

namespace AsterCache.Service;

/// `AsterCache.Service --console --frame-test <초>`: 프레임 세션을 모든 프로세스 대상으로 그 시간 동안 열어, Present 시작 이벤트가 프로세스 번호와 함께 들어오는지,
/// 초당 이벤트 수와 이 프로세스의 CPU 사용률, 이벤트 번호 필터가 42 만 남기는지, 이벤트 시각이 Stopwatch 틱과 같은 시계인지를 출력합니다 (게임 없이 하는 확인).
/// 프로세스 번호를 더 주면 (`--frame-test <초> <pid>`) 모든 프로세스 집계 대신 그 프로세스 하나를 서비스가 쓰는 경로 (초 통, 확정 대기) 로 초마다 출력합니다.
/// 관리자 권한이 필요합니다. 예외는 삼키지 않고 그대로 출력합니다.
public static class FrameTest
{
    public static async Task<int> RunAsync(int seconds, int? pid, TextWriter o)
    {
        if (pid is { } one)
        {
            return await RunOneProcessAsync(seconds, one, o);
        }

        o.WriteLine($"frame-test: {seconds} s, administrator: {Elevation.IsAdmin()}, Stopwatch.Frequency {Stopwatch.Frequency}");
        using var process = Process.GetCurrentProcess();
        TimeSpan cpu0 = process.TotalProcessorTime;
        long allocated0 = GC.GetTotalAllocatedBytes(false);
        long t0 = Stopwatch.GetTimestamp();
        using FrameTimeSession session = FrameTimeSession.OpenDiagnostic(t0);
        o.WriteLine($"session opened; FlushTrace returned {EtwFlush.Flush(FrameTimeSession.TestSessionName)} (0 = success)");
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        process.Refresh();
        double wall = Stopwatch.GetElapsedTime(t0).TotalSeconds;
        double cpuPercent = (process.TotalProcessorTime - cpu0).TotalSeconds / wall * 100;
        long total = 0;
        foreach (KeyValuePair<int, long> kv in session.PerProcess.OrderByDescending(k => k.Value))
        {
            total += kv.Value;
            o.WriteLine($"  pid {kv.Key} ({NameOf(kv.Key)}): {kv.Value} Present events, {kv.Value / wall:F1} per second");
        }

        o.WriteLine($"  event ids seen (DXGI): [{string.Join(", ", session.PerEventId.Select(k => $"{k.Key}={k.Value}"))}]");
        double first = session.FirstDeltaTicks == long.MinValue ? double.NaN : session.FirstDeltaTicks * 1000.0 / Stopwatch.Frequency;
        double last = session.LastDeltaTicks == long.MinValue ? double.NaN : session.LastDeltaTicks * 1000.0 / Stopwatch.Frequency;
        o.WriteLine($"  event time minus Stopwatch.GetTimestamp at arrival: first {first:F1} ms, last {last:F1} ms (same clock: both small and similar); largest delivery lag {session.MaxLagTicks * 1000.0 / Stopwatch.Frequency:F0} ms");
        o.WriteLine($"  events lost: {session.EventsLost}; total {total} events in {wall:F1} s = {total / wall:F1} per second; this process CPU {cpuPercent:F2} % of one core (includes the session pump and flush timer); process allocated {(GC.GetTotalAllocatedBytes(false) - allocated0) / 1024} KiB in total (all threads)");
        return 0;
    }

    private static async Task<int> RunOneProcessAsync(int seconds, int pid, TextWriter o)
    {
        long t0 = Stopwatch.GetTimestamp();
        using var gpu = DiskActivity.GpuDedicatedMemory.Open();
        o.WriteLine($"graphics memory dedicated usage (all adapters): {(gpu?.Mib() is { } mib ? mib + " MiB" : "not readable")}");
        using FrameTimeSession session = FrameTimeSession.OpenTest(t0);
        session.AddProcess(pid);
        o.WriteLine($"frame-test pid {pid} ({NameOf(pid)}): second, frames, longest ms, over 50 ms, over 100 ms (settled {PlayRecorder.FrameSettleSeconds} s after the second ends; pauses = intervals of 1 s or more)");
        long taken = 0;
        for (int s = 0; s < seconds + PlayRecorder.FrameSettleSeconds + 1; s++)
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            foreach (FrameSecond f in session.TakeSettled((long)Math.Floor(Stopwatch.GetElapsedTime(t0).TotalSeconds) - PlayRecorder.FrameSettleSeconds))
            {
                taken += f.Frames;
                o.WriteLine($"  {f.Second}: {f.Frames}, {f.MaxMs:F1}, {f.Over50Ms}, {f.Over100Ms}, pauses {f.Pauses}");
            }
        }

        o.WriteLine($"  frames taken {taken}, accepted events {session.AcceptedEvents}, lost {session.EventsLost}, late {session.EventsLate}, out of order {session.EventsOutOfOrder}");
        return 0;
    }

    private static string NameOf(int pid)
    {
        try
        {
            using Process p = Process.GetProcessById(pid);
            return p.ProcessName;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return "exited";
        }
    }
}
