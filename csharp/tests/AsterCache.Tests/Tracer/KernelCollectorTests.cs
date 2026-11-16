using AsterCache.Core.Trace;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Tracer;

/// 수집 구간 (창) 밖의 읽기를 거르고, 창 밖에서도 이름을 계속 익히는 동작. 이벤트 파서 없이 처리 메서드를 직접 부릅니다.
public sealed class KernelCollectorTests
{
    private static readonly DateTime T0 = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    private const string File1 = @"D:\Games\a.bin";

    private static KernelCollector Collector()
    {
        var collector = new KernelCollector(["game.exe"], new DevicePaths([]));
        collector.StartProcess(T0, 10, 1, "game.exe");
        return collector;
    }

    [Fact]
    public void ReadsOutsideTheWindowAreDroppedAndThoseInsideKeepTheirOwnTimes()
    {
        KernelCollector c = Collector();
        c.NameKey(7, File1);
        c.AddRead(T0.AddSeconds(1), 10, 7, 70, 0, 4096);
        c.Pause();
        c.AddRead(T0.AddSeconds(2), 10, 7, 70, 4096, 4096); // 창 밖
        c.Resume();
        c.AddRead(T0.AddSeconds(3), 10, 7, 70, 8192, 4096);

        WindowStats stats = c.TakeWindowStats();
        List<TraceRecord> reads = c.Finish().Where(r => r.Kind == RecordKind.Read).ToList();

        Assert.Equal(2, stats.Reads);
        Assert.Equal([0L, 8192L], reads.Select(r => r.Offset));
        Assert.Equal([T0.AddSeconds(1), T0.AddSeconds(3)], reads.Select(r => r.Time));
    }

    [Fact]
    public void ANameLearnedWhileClosedResolvesTheFirstReadOfTheNextWindow()
    {
        KernelCollector c = Collector();
        c.Pause();
        c.NameKey(7, File1); // 창 밖에서도 이름 이벤트는 계속 처리됨
        c.Resume();
        c.AddRead(T0.AddSeconds(5), 10, 7, 0, 0, 4096);

        List<TraceRecord> reads = c.Finish().Where(r => r.Kind == RecordKind.Read).ToList();

        Assert.Equal(0, c.Unresolved);
        Assert.Equal(File1, Assert.Single(reads).Path);
    }

    [Fact]
    public void AReadWithNoNameAtAllIsDroppedAndCountedAsUnresolved()
    {
        KernelCollector c = Collector();
        c.AddRead(T0.AddSeconds(1), 10, 9, 0, 0, 4096); // 이름 이벤트가 하나도 없는 파일: 새 세션이 창 전에 열린 파일을 만나는 경우

        Assert.DoesNotContain(c.Finish(), r => r.Kind == RecordKind.Read);
        Assert.Equal(1, c.Unresolved);
    }

    [Fact]
    public void WritesOutsideTheWindowAndStopsOfOtherProcessesAreNotRecorded()
    {
        KernelCollector c = Collector();
        c.NameKey(8, File1);
        c.Pause();
        c.AddWrite(T0.AddSeconds(1), 10, 7, 70); // 창 밖 쓰기
        c.StopProcess(T0.AddSeconds(2), 99); // 대상이 아닌 프로세스의 종료
        c.Resume();
        c.AddWrite(T0.AddSeconds(3), 10, 8, 80);

        List<TraceRecord> records = c.Finish();

        Assert.Single(records, r => r.Kind == RecordKind.Write);
        Assert.DoesNotContain(records, r => r.Kind == RecordKind.ProcessStop);
        c.StopProcess(T0.AddSeconds(4), 10);
        Assert.Contains(c.Finish(), r => r.Kind == RecordKind.ProcessStop && r.Pid == 10);
    }

    [Fact]
    public void ReadsOfAProcessOutsideTheTargetTreeAreIgnored()
    {
        KernelCollector c = Collector();
        c.NameKey(7, File1);
        c.AddRead(T0, 99, 7, 70, 0, 4096);

        Assert.Equal(0, c.TakeWindowStats().Reads);
        Assert.Equal(1, c.TakeWindowStats().Targets);
    }

    private static KernelCollector WithSummary(out IoSummaryCollector summary, IReadOnlySet<uint>? gameDisks = null)
    {
        summary = new IoSummaryCollector(new DateTimeOffset(T0), gameDisks);
        var collector = new KernelCollector(["game.exe", "late.exe"], new DevicePaths([]), ioSummary: summary);
        collector.StartProcess(T0, 10, 1, "game.exe");
        return collector;
    }

    [Fact]
    public void DiskReadsKeepCountingAfterTheRawWindowClosesWithoutGrowingTheRawRecords()
    {
        KernelCollector c = WithSummary(out _);
        c.NameKey(7, File1);
        c.AddRead(T0.AddSeconds(1), 10, 7, 70, 0, 4096);
        c.Pause(new DateTimeOffset(T0.AddSeconds(2)));
        c.AddDiskRead(3, 8192, 4096, 2.5, T0.AddSeconds(3), paging: true);

        IoRequestSnapshot requests = Assert.IsType<IoRequestSnapshot>(c.FinishIoRequests(new DateTimeOffset(T0.AddSeconds(4)), eventsLost: 3));

        Assert.Equal(new IoRequest(3, 8192, 4096, 1, 2500, 2500, 3_000_000, 1), Assert.Single(requests.Requests));
        Assert.Equal(3, requests.EventsLost);
        Assert.Equal([new IoSummaryWindow(0, 2_000_000)], requests.RawWindows);
        Assert.Single(c.Finish(), record => record.Kind == RecordKind.Read);
    }

    [Fact]
    public void AfterTheRawFreezeEveryEventExceptDiskCompletionsIsDroppedAtOnce()
    {
        KernelCollector c = WithSummary(out _);
        c.NameKey(7, File1);
        c.AddRead(T0.AddSeconds(1), 10, 7, 70, 0, 4096);
        c.Pause(new DateTimeOffset(T0.AddSeconds(2)));
        c.FreezeRawRecords();
        (int names, int records) = c.Footprint;

        c.NameKey(8, @"D:\Other\noise.bin");
        c.OpenFile(10, 80, @"D:\Games\late.bin");
        c.StartProcess(T0.AddSeconds(3), 11, 1, "late.exe");
        c.StopProcess(T0.AddSeconds(3), 10);
        c.CloseObject(70);
        c.AddRead(T0.AddSeconds(3), 10, 7, 70, 4096, 4096);
        c.AddWrite(T0.AddSeconds(3), 10, 8, 80);
        c.AddDiskRead(3, 0, 4096, 1.0, T0.AddSeconds(3), paging: false);

        List<TraceRecord> raw = c.Finish();
        IoRequestSnapshot requests = Assert.IsType<IoRequestSnapshot>(c.FinishIoRequests(new DateTimeOffset(T0.AddSeconds(4)), eventsLost: 0));

        Assert.Equal((names, records), c.Footprint); // 이름표·기록이 더 자라지 않음
        Assert.DoesNotContain(raw, record => record.Pid == 11 || record.Kind == RecordKind.ProcessStop || record.Kind == RecordKind.Write);
        Assert.Single(raw, record => record.Kind == RecordKind.Read);
        Assert.Equal(1, Assert.Single(requests.Requests).Count);
        Assert.Equal(0, c.RawTraceZeroUs);
    }

    [Fact]
    public void ARawRecordBeforeTheObservationOriginKeepsASignedZeroOffsetAndIsNeverClippedToZero()
    {
        // 관찰 시작 (공급자를 켠 뒤) 보다 2 ms 앞선 첫 기록: 원시 0점은 -2,000 us, 그 뒤 1 ms 에 온 디스크 읽기는 +1,000 us
        var origin = new DateTimeOffset(T0);
        var summary = new IoSummaryCollector(origin);
        var c = new KernelCollector(["game.exe"], new DevicePaths([]), ioSummary: summary);
        c.StartProcess(T0.AddMilliseconds(-2), 10, 1, "game.exe");
        c.AddDiskRead(3, 0, 4096, 1.0, T0.AddMilliseconds(1), paging: false);
        c.Pause(origin.AddSeconds(1));
        c.FreezeRawRecords();

        IoRequestSnapshot requests = Assert.IsType<IoRequestSnapshot>(c.FinishIoRequests(origin.AddSeconds(2), eventsLost: 0));

        Assert.Equal(-2000, requests.RawTraceZeroUs);
        Assert.Equal(1000, Assert.Single(requests.Requests).FirstSessionUs);
    }

    [Fact]
    public void DiskReadsOfDisksOtherThanTheGameDiskGoToTheOtherDiskTotalsOnly()
    {
        KernelCollector c = WithSummary(out _, new HashSet<uint> { 4 });
        c.AddDiskRead(4, 0, 4096, 1.0, T0.AddSeconds(1), paging: false);
        c.AddDiskRead(1, 0, 8192, 2.0, T0.AddSeconds(1), paging: false);

        IoRequestSnapshot requests = Assert.IsType<IoRequestSnapshot>(c.FinishIoRequests(new DateTimeOffset(T0.AddSeconds(2)), eventsLost: 0));

        Assert.Equal(4u, Assert.Single(requests.Requests).Disk);
        Assert.Equal(new IoClassTotals(1, 8192, 2000), requests.OtherDisk);
        Assert.Equal(new IoClassTotals(2, 12288, 3000), requests.Total);
    }
}
