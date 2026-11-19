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
    public void AfterTheRawFreezeOnlyReadModeNamesAndProcessTrackingContinue()
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

        Assert.True(c.Footprint.Names > names); // 읽기 방식 판정에 필요한 파일 이름은 계속 추적
        Assert.Equal(records, c.Footprint.Records); // 원시 기록은 더 자라지 않음
        Assert.DoesNotContain(raw, record => record.Pid == 11 || record.Kind == RecordKind.ProcessStop || record.Kind == RecordKind.Write);
        Assert.Single(raw, record => record.Kind == RecordKind.Read);
        Assert.Equal(1, Assert.Single(requests.Requests).Count);
        Assert.Equal(0, c.RawTraceZeroUs);
    }

    [Fact]
    public void RegisteredExeStartsAreRecordedInSessionTimeEvenAfterTheRawFreezeButNotDescendantsOrOnesAlreadyRunning()
    {
        var summary = new IoSummaryCollector(new DateTimeOffset(T0));
        var c = new KernelCollector(["game.exe", "late.exe"], new DevicePaths([]), ioSummary: summary);
        c.StartProcess(T0.AddSeconds(-1), 9, 1, "game.exe", alreadyRunning: true); // 관찰 전부터 떠 있던 프로세스
        c.StartProcess(T0.AddSeconds(2), 10, 9, "helper.exe"); // 대상의 자손이지만 등록된 실행 파일 이름이 아님
        c.Pause(new DateTimeOffset(T0.AddSeconds(2)));
        c.FreezeRawRecords();

        c.StartProcess(T0.AddSeconds(3), 11, 1, @"D:\Games\late.exe");
        c.StartProcess(T0.AddSeconds(5), 12, 1, "other.exe");
        IoRequestSnapshot requests = Assert.IsType<IoRequestSnapshot>(c.FinishIoRequests(new DateTimeOffset(T0.AddSeconds(6)), eventsLost: 0));

        Assert.Equal([3_000_000L], requests.TargetStartsUs);
    }

    [Fact]
    public void TheStartThatOpenedTheSessionCountsAsAStartAtZeroOnceAndNeedsASummary()
    {
        var summary = new IoSummaryCollector(new DateTimeOffset(T0));
        var c = new KernelCollector(["game.exe"], new DevicePaths([]), ioSummary: summary);
        c.StartProcess(T0, 9, 1, "game.exe", alreadyRunning: true); // 트레이스를 띄운 시작은 이미 떠 있는 프로세스로 들어옴
        c.NoteTriggerStart();
        c.NoteTriggerStart();
        c.StartProcess(T0.AddSeconds(4), 10, 1, "game.exe");

        Assert.Equal([0L, 4_000_000L], Assert.IsType<IoRequestSnapshot>(c.FinishIoRequests(new DateTimeOffset(T0.AddSeconds(5)), eventsLost: 0)).TargetStartsUs);
        new KernelCollector(["game.exe"], new DevicePaths([])).NoteTriggerStart(); // 집계가 없으면 아무것도 하지 않음
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

    [Fact]
    public void ReadModesUseFileRequestFlagsAndContinueAfterTheRawTraceIsFrozen()
    {
        var summary = new IoSummaryCollector(new DateTimeOffset(T0), readModeRoots: [@"D:\Games"]);
        var c = new KernelCollector(["game.exe", "late.exe"], new DevicePaths([]), ioSummary: summary);
        c.StartProcess(T0, 10, 1, "game.exe");
        c.NameKey(7, File1);
        c.AddRead(T0.AddSeconds(1), 10, 7, 70, 0, 4096, ioFlags: 0);
        c.AddRead(T0.AddSeconds(1), 10, 7, 70, 4096, 8192, ioFlags: 0x01);
        c.AddRead(T0.AddSeconds(1), 10, 7, 70, 12288, 4096, ioFlags: 0x02);
        c.AddRead(T0.AddSeconds(1), 10, 7, 70, 16384, 8192, ioFlags: 0x41);
        c.AddRead(T0.AddSeconds(1), 99, 7, 70, 0, 4096, ioFlags: 0x01); // 대상 트리 밖
        c.Pause(new DateTimeOffset(T0.AddSeconds(2)));
        c.FreezeRawRecords();

        c.StartProcess(T0.AddSeconds(3), 11, 10, "late.exe");
        c.AddRead(T0.AddSeconds(3), 11, 9, 90, 0, 2048, ioFlags: 0x40); // 원시 동결 뒤 이름 확인
        c.NameKey(9, @"D:\Games\late.bin");
        c.AddRead(T0.AddSeconds(3), 11, 99, 0, 0, 1024, ioFlags: 0x08); // 끝내 이름을 못 찾음
        c.AddRead(T0.AddSeconds(3), 11, 100, 0, 0, 1024, ioFlags: 0x10);
        c.NameKey(100, @"D:\Other\outside.bin"); // 게임 폴더 밖은 저장하지 않음

        IoRequestSnapshot requests = Assert.IsType<IoRequestSnapshot>(c.FinishIoRequests(new DateTimeOffset(T0.AddSeconds(4)), 0));
        IoSummary saved = new IoBlockMapper(requests).Build(0, 0, 0, 0, 0, false);

        Assert.Equal(2, saved.ReadModes!.Count);
        IoSummaryReadMode first = saved.ReadModes.Single(mode => mode.Path == File1);
        Assert.Equal((1L, 4096L, 1L, 8192L, 2L, 12288L),
            (first.CacheRequests, first.CacheBytes, first.NoCacheRequests, first.NoCacheBytes, first.PagingRequests, first.PagingBytes));
        IoSummaryReadMode late = saved.ReadModes.Single(mode => mode.Path.EndsWith("late.bin", StringComparison.Ordinal));
        Assert.Equal((0L, 0L, 0L, 0L, 1L, 2048L),
            (late.CacheRequests, late.CacheBytes, late.NoCacheRequests, late.NoCacheBytes, late.PagingRequests, late.PagingBytes));
        Assert.Equal(1, saved.Quality.ReadModeReadsUnnamed);
        Assert.Equal(0, saved.Quality.ReadModeFilesDropped);
        Assert.Equal(1, saved.Quality.ReadModeFlagCounts!.Single(flag => flag.Flags == 0x01).Reads);
        Assert.Equal(0, saved.Quality.ReadModeFlagCountOther);
        Assert.NotNull(saved.Quality.ReadModesObservedUntilUtc);
        Assert.Equal(4, c.Finish().Count(record => record.Kind == RecordKind.Read)); // 동결 뒤 원시 기록은 늘지 않음
    }

    [Fact]
    public void ReadModeFileLimitCountsEachDroppedPathOnce()
    {
        var summary = new IoSummaryCollector(new DateTimeOffset(T0), readModeRoots: [@"D:\Games"], maxReadModeFiles: 1);
        summary.AddProcessRead(@"D:\Games\keep.bin", 1, 4096, 0);
        summary.AddProcessRead(@"D:\Games\drop.bin", 1, 2048, 1);
        summary.AddProcessRead(@"D:\Games\drop.bin", 1, 2048, 1);

        IoRequestSnapshot requests = summary.Snapshot(new DateTimeOffset(T0.AddSeconds(1)), [], 0, null);
        IoSummary saved = new IoBlockMapper(requests).Build(0, 0, 0, 0, 0, false);

        Assert.Single(saved.ReadModes!);
        Assert.Equal(1, saved.Quality.ReadModeFilesDropped);
    }

    [Fact]
    public void ReadModeNameTableIsBoundedAfterFreezeAndStillUpdatesExistingKeys()
    {
        const int maxNames = 1 << 18;
        var summary = new IoSummaryCollector(new DateTimeOffset(T0), readModeRoots: [@"D:\Games"]);
        var c = new KernelCollector(["game.exe"], new DevicePaths([]), ioSummary: summary);
        c.StartProcess(T0, 10, 1, "game.exe");
        c.FreezeRawRecords();

        for (ulong key = 1; key <= maxNames; key++)
        {
            c.NameKey(key, $@"D:\Games\{key}.bin");
        }

        c.NameKey(1, @"D:\Games\reused.bin");
        c.NameKey(maxNames + 1, @"D:\Games\unnamed.bin");
        Assert.Equal(maxNames, c.Footprint.Names);

        c.AddRead(T0.AddSeconds(1), 10, 1, 0, 0, 4096, ioFlags: 0x01);
        c.AddRead(T0.AddSeconds(1), 10, maxNames + 1, 0, 0, 8192, ioFlags: 0x01);
        c.NameKey(maxNames + 1, @"D:\Games\unnamed.bin");
        IoSummaryReadMode readMode = Assert.Single(new IoBlockMapper(c.FinishIoRequests(new DateTimeOffset(T0.AddSeconds(2)), 0)!)
            .Build(0, 0, 0, 0, 0, false).ReadModes!);

        Assert.Equal(@"D:\Games\reused.bin", readMode.Path);
        Assert.Equal(1, readMode.NoCacheRequests);
        Assert.Equal(1, summary.Snapshot(new DateTimeOffset(T0.AddSeconds(2)), [], 0, null).ReadModeReadsUnnamed);
    }

    [Fact]
    public void FileNameLimitKeepsUpdatesAndAllowsClosedObjectIdentifiersToBeReused()
    {
        var names = new FileNames();
        names.NameKey(1, @"D:\Games\old.bin");
        names.LimitNewEntriesAfterFreeze(2);
        names.NameObject(7, @"D:\Games\first.bin");
        names.CloseObject(7);
        names.NameObject(7, @"D:\Games\second.bin");
        names.NameKey(1, @"D:\Games\updated.bin");
        names.NameObject(8, @"D:\Games\over-limit.bin");

        Assert.Equal(2, names.Count);
        Assert.Equal(@"D:\Games\updated.bin", names.AtRead(1, 7).Name);
        Assert.Equal(@"D:\Games\second.bin", names.AtRead(0, 7).Fallback);
        Assert.Null(names.AtRead(0, 8).Fallback);
    }

    [Fact]
    public void ClosingAndReopeningAReadWindowKeepsReadModeCountsWithoutAddingClosedReadsToRawRecords()
    {
        KernelCollector c = WithSummary(out _);
        c.NameKey(7, File1);
        c.AddRead(T0.AddSeconds(1), 10, 7, 70, 0, 4096, ioFlags: 0);
        c.Pause(new DateTimeOffset(T0.AddSeconds(2)));
        c.AddRead(T0.AddSeconds(3), 10, 7, 70, 4096, 8192, ioFlags: 0x01);
        c.Resume(new DateTimeOffset(T0.AddSeconds(4)));
        c.AddRead(T0.AddSeconds(5), 10, 7, 70, 12288, 4096, ioFlags: 0);

        IoRequestSnapshot requests = Assert.IsType<IoRequestSnapshot>(c.FinishIoRequests(new DateTimeOffset(T0.AddSeconds(6)), 0));
        IoSummaryReadMode mode = Assert.Single(new IoBlockMapper(requests).Build(0, 0, 0, 0, 0, false).ReadModes!);

        Assert.Equal((2L, 8192L, 1L, 8192L), (mode.CacheRequests, mode.CacheBytes, mode.NoCacheRequests, mode.NoCacheBytes));
        Assert.Equal(2, c.Finish().Count(record => record.Kind == RecordKind.Read));
    }

    [Fact]
    public void FinishReturnsTheSameRawRecordsBeforeAndAfterFreezing()
    {
        KernelCollector c = Collector();
        c.NameKey(7, File1);
        c.AddRead(T0.AddSeconds(1), 10, 7, 70, 0, 4096);

        List<TraceRecord> beforeFreeze = c.Finish();
        c.FreezeRawRecords();

        Assert.Equal(beforeFreeze, c.Finish());
    }
}
