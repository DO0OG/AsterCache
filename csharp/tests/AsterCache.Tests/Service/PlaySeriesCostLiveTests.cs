using System.Diagnostics;
using System.Globalization;
using AsterCache.Service;
using AsterCache.Tests.Windows;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 초 단위 기록의 비용 측정: 실제 조회 (WindowsPlaySeriesProbe) 와 줄 만들기·파일 쓰기를 돌려 회당 CPU 시간과 메모리를 잽니다.
/// 두 가지로 잽니다. (1) 1 초 간격으로 ROUNDS 회 (기본 300) 돌며 프로세스 전체 CPU 시간 차이를 한 코어 대비 비율로 구함, (2) 쉬지 않고 TIGHT 회 (기본 2000) 돌며 구성 요소별 한 번의 시간을 구함 (CPU 에 묶인 일이라 시계 시간이 CPU 시간과 같음).
/// 환경 변수 ASTER_SERIES_COST_OUT (결과를 적을 파일) 이 있을 때만 돌고 없으면 바로 끝납니다 (ASTER_SERIES_COST_ROUNDS, ASTER_SERIES_COST_TIGHT 로 회수 조정). 게임이 실행 중이 아닐 때 돌리십시오.
[Trait("Category", "Live")]
public sealed class PlaySeriesCostLiveTests
{
    private static double ProcessCpuMs()
    {
        using Process p = Process.GetCurrentProcess();
        return p.TotalProcessorTime.TotalMilliseconds;
    }

    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out int n) ? n : fallback;

    [Fact]
    public async Task MeasureCostPerSecondOfTheSeriesRecording()
    {
        string? output = Environment.GetEnvironmentVariable("ASTER_SERIES_COST_OUT");
        if (string.IsNullOrEmpty(output))
        {
            return;
        }

        int rounds = Env("ASTER_SERIES_COST_ROUNDS", 300);
        int tight = Env("ASTER_SERIES_COST_TIGHT", 2000);
        using var dir = new TempDir();
        int[] gamePids = [.. Process.GetProcessesByName("dwm").Select(p => p.Id).Take(1)]; // 그래픽 메모리 카운터에 있는 프로세스 (게임 대신)
        using IPlaySeriesProbe probe = WindowsPlaySeriesProbe.Open();
        var builder = new PlaySeriesBuilder();
        var file = new PlaySeriesFile(dir.Combine("play_series"), "cost", DateTimeOffset.UtcNow, new ListLog(), () => DateTimeOffset.UtcNow);
        using var recorder = new PlaySeriesRecorder(probe, file);
        IReadOnlyList<PlayDiskCounters> disk = [new(1, 0, 0, 0, 0, 0)];
        long firstTick = Stopwatch.GetTimestamp();

        // 쉬지 않는 반복: 구성 요소별 한 번의 시간
        double readMs = 0;
        double buildMs = 0;
        double listsMs = 0;
        double tightCpuBefore = ProcessCpuMs();
        for (int i = 0; i < tight; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            PlaySeriesRaw raw = probe.Read(gamePids);
            long t1 = Stopwatch.GetTimestamp();
            PlaySeriesPoint point = builder.Next(raw, gamePids, disk, disk);
            file.AddRow(PlaySeriesFormat.Line(i, new FrameSecond(i, 60, 16.7, 0, 0), point));
            long t2 = Stopwatch.GetTimestamp();
            recorder.SampleLists(i * 30.0); // 호출마다 30 초가 지난 것으로 (목록 조회를 매번 잼)
            long t3 = Stopwatch.GetTimestamp();
            (readMs, buildMs, listsMs) = (readMs + Stopwatch.GetElapsedTime(t0, t1).TotalMilliseconds, buildMs + Stopwatch.GetElapsedTime(t1, t2).TotalMilliseconds, listsMs + Stopwatch.GetElapsedTime(t2, t3).TotalMilliseconds);
        }

        double tightCpu = ProcessCpuMs() - tightCpuBefore;

        // 조회별 한 번의 시간 (probe.Read 를 이루는 호출들)
        List<ProcessUsage> processes = [];
        using DiskActivity.GpuDedicatedMemory? gpu = DiskActivity.GpuDedicatedMemory.Open();
        using DiskActivity.GpuProcessMemory? gpuProcesses = DiskActivity.GpuProcessMemory.Open();
        string parts = string.Join("\n", new (string Name, Action Call)[]
        {
            ("CpuLoad.Times", () => _ = CpuLoad.Times(out _, out _)),
            ("ProcessFaults.Snapshot", () => _ = ProcessFaults.Snapshot(processes)),
            ("SystemMemory.AvailableMib", () => _ = SystemMemory.AvailableMib()),
            ("MemoryListInfo.Query", () => _ = MemoryListInfo.Query()),
            ("SystemPageReads.Query", () => _ = SystemPageReads.Query()),
            ("GpuDedicatedMemory.Mib (adapters)", () => _ = gpu?.Mib()),
            ("GpuProcessMemory.Mib (game processes)", () => _ = gpuProcesses?.Mib(gamePids)),
        }.Select(part =>
        {
            part.Call();
            long t = Stopwatch.GetTimestamp();
            for (int i = 0; i < 1000; i++)
            {
                part.Call();
            }

            return $"  {part.Name}: {Stopwatch.GetElapsedTime(t).TotalMilliseconds / 1000:F3} ms";
        }));

        // 기준선: 같은 1 초 간격으로 아무것도 읽지 않고 기다리기만 하며 프로세스 전체 CPU 를 잼 (시험 호스트·타이머·측정 자체의 몫)
        int baselineRounds = Math.Max(10, rounds / 3);
        double baseCpuBefore = ProcessCpuMs();
        long baseWallBefore = Stopwatch.GetTimestamp();
        for (int i = 0; i < baselineRounds; i++)
        {
            await Task.Delay(1000);
        }

        double baseCpuMs = ProcessCpuMs() - baseCpuBefore;
        double baseWallSeconds = Stopwatch.GetElapsedTime(baseWallBefore).TotalSeconds;

        // 1 초 간격 반복: 프로세스 전체 CPU
        long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
        long workingBefore = Process.GetCurrentProcess().WorkingSet64;
        double cpuBefore = ProcessCpuMs();
        long wallBefore = Stopwatch.GetTimestamp();
        for (int i = 0; i < rounds; i++)
        {
            await Task.Delay(1000);
            PlaySeriesRaw raw = probe.Read(gamePids);
            PlaySeriesPoint point = builder.Next(raw, gamePids, disk, disk);
            file.AddRow(PlaySeriesFormat.Line(i, new FrameSecond(i, 60, 16.7, 0, 0), point));
            recorder.SampleLists(Stopwatch.GetElapsedTime(firstTick).TotalSeconds + (tight * 30.0));
            recorder.FlushIfDue();
        }

        double wallSeconds = Stopwatch.GetElapsedTime(wallBefore).TotalSeconds;
        double cpuMs = ProcessCpuMs() - cpuBefore;
        long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        long workingAfter = Process.GetCurrentProcess().WorkingSet64;
        recorder.Dispose();
        long fileBytes = new DirectoryInfo(dir.Combine("play_series")).GetFiles().Sum(f => f.Length);
        string text = string.Create(CultureInfo.InvariantCulture,
            $"per call, 1000 calls each:\n{parts}\n"
            + $"tight loop {tight} rounds: probe.Read {readMs / tight:F3} ms, builder + line + AddRow {buildMs / tight:F3} ms, lists read {listsMs / tight:F3} ms per call; whole process CPU {tightCpu / tight:F3} ms per round\n"
            + $"baseline, waiting only, {baselineRounds} rounds, wall {baseWallSeconds:F1} s: whole process CPU {baseCpuMs:F1} ms = {baseCpuMs / (baseWallSeconds * 1000) * 100:F3} % of one core\n"
            + $"1 s interval {rounds} rounds, wall {wallSeconds:F1} s: whole process CPU {cpuMs:F1} ms = {cpuMs / (wallSeconds * 1000) * 100:F3} % of one core (test host idle included), {cpuMs / rounds:F3} ms per second; minus the baseline {(cpuMs / (wallSeconds * 1000) - (baseCpuMs / (baseWallSeconds * 1000))) * 100:F3} % of one core\n"
            + $"managed memory {memoryBefore / 1024} KiB -> {memoryAfter / 1024} KiB, working set {workingBefore / 1024} KiB -> {workingAfter / 1024} KiB\n"
            + $"file bytes {fileBytes}\n");
        File.WriteAllText(output, text);
    }
}
