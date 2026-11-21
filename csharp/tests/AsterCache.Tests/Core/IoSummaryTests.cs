using System.Text.Json.Nodes;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Trace;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Core;

public sealed class IoSummaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_io_" + Guid.NewGuid().ToString("N"));
    private static readonly DateTimeOffset Started = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    public IoSummaryTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static IoRequestSnapshot Snapshot(params IoRequest[] requests) => new(
        Started, Started.AddSeconds(2), [new IoSummaryWindow(0, 1000)], 0, 3,
        new IoClassTotals(requests.Sum(r => r.Count), requests.Sum(r => r.Bytes * r.Count), requests.Sum(r => r.LatencySumUs)),
        requests.Select(r => r.LatencyMaxUs).DefaultIfEmpty().Max(), default, default, default, 0, 0, requests);

    private static IoRequest Request(uint disk, long offset, long bytes, long latencyUs, long count = 1, long first = 100, long paging = 0) =>
        new(disk, offset, bytes, count, latencyUs, latencyUs, first, paging);

    [Fact]
    public void ARequestCrossingExtentBlockAndFileBoundariesKeepsBytesAndLatencyTotals()
    {
        // 요청 20,000 B (지연 1,000 us): 파일 A 의 끝 6,000 B (파일 위치 64,000.., 64 KiB 블록 경계를 넘음) + 파일 B 의 앞 14,000 B
        var mapper = new IoBlockMapper(Snapshot(Request(4, 100_000, 20_000, 1000)));
        mapper.AddFile(@"D:\Games\a.bin", false, true, 4, [new IoFileExtent(96_000, 60_000, 10_000)]);
        mapper.AddFile(@"D:\Games\b.bin", false, true, 4, [new IoFileExtent(106_000, 0, 14_000)]);

        IoSummary summary = mapper.Build(2, 0, 0, 0, 5, false);

        Assert.Equal([new IoSummaryFile(@"D:\Games\a.bin"), new IoSummaryFile(@"D:\Games\b.bin")], summary.Files);
        Assert.Collection(summary.Rows,
            row => Assert.Equal(new IoSummaryRow(0, 0, 1, 1536, 76, 1000, 100, 0), row),
            row => Assert.Equal(new IoSummaryRow(0, 1, 1, 4464, 224, 1000, 100, 0), row),
            row => Assert.Equal(new IoSummaryRow(1, 0, 1, 14_000, 700, 1000, 100, 0), row));
        Assert.Equal(20_000, summary.Rows.Sum(row => row.Bytes));
        Assert.Equal(1000, summary.Rows.Sum(row => row.LatencySumUs));
        Assert.Equal(new IoClassTotals(1, 20_000, 1000), summary.Quality.Linked);
        Assert.Equal(summary.Quality.Total, summary.Quality.Linked);
    }

    [Fact]
    public void AFragmentedFileMapsEachExtentToItsOwnFileOffsetAndTheGapBetweenThemIsNotAttributed()
    {
        // 디스크 위치 순으로 파일의 뒤쪽 조각이 먼저 놓인 조각난 파일 + 두 조각 사이에 다른 것이 낀 경우
        var mapper = new IoBlockMapper(Snapshot(Request(4, 1000, 3000, 300)));
        mapper.AddFile(@"D:\Games\frag.bin", false, true, 4,
            [new IoFileExtent(1000, 500_000, 1000), new IoFileExtent(3000, 0, 1000)]);

        IoSummary summary = mapper.Build(1, 0, 0, 0, 1, false);

        Assert.Equal([0L, 7L], summary.Rows.Select(row => row.BlockNumber)); // 파일 위치 500,000 = 블록 7
        Assert.All(summary.Rows, row => Assert.Equal(1000, row.Bytes));
        Assert.Equal(new IoClassTotals(1, 2000, 200), summary.Quality.Linked); // 가장 큰 몫이 파일 구간이라 요청 건수는 그쪽에 셈
        Assert.Equal(new IoClassTotals(0, 1000, 100), summary.Quality.NoFile);
    }

    [Fact]
    public void EveryCompletionLandsInExactlyOneClassAndTheClassesAddUpToTheTotal()
    {
        var collector = new IoSummaryCollector(Started, new HashSet<uint> { 4, 5 }, maxRequests: 5);
        collector.AddDiskRead(4, 0, 4096, 100, 10, paging: true); // 파일에 듦
        collector.AddDiskRead(4, 1_000_000, 4096, 50, 20, paging: false); // 같은 디스크, 어떤 파일 구간에도 안 듦
        collector.AddDiskRead(9, 0, 8192, 30, 30, paging: false); // 게임 디스크가 아님
        collector.AddDiskRead(5, 0, 2048, 40, 40, paging: false); // 변환 안 하는 디스크
        collector.AddDiskRead(4, 2_000_000, 512, 60, 50, paging: false); // 압축 파일
        collector.AddDiskRead(4, -1, 512, 5, 60, paging: false); // 위치를 모름

        var mapper = new IoBlockMapper(collector.Snapshot(Started.AddSeconds(1), [], 0, null));
        mapper.SkipDisk(5);
        mapper.AddFile(@"D:\Games\a.bin", false, true, 4, [new IoFileExtent(0, 0, 4096)]);
        mapper.AddFile(@"D:\Games\packed.bin", false, false, 4, [new IoFileExtent(2_000_000, 0, 4096)]);
        IoSummary summary = mapper.Build(2, 0, 0, 1, 1, false);
        IoSummaryQuality q = summary.Quality;

        Assert.Equal(new IoClassTotals(6, 4096 + 4096 + 8192 + 2048 + 512 + 512, 285), q.Total);
        Assert.Equal(new IoClassTotals(1, 4096, 100), q.Linked);
        Assert.Equal(new IoClassTotals(1, 8192, 30), q.OtherDisk);
        Assert.Equal(new IoClassTotals(2, 4096 + 512, 55), q.NoFile);
        Assert.Equal(new IoClassTotals(2, 2048 + 512, 100), q.NotConverted);
        Assert.Equal(q.Total.Reads, q.Linked.Reads + q.OtherDisk.Reads + q.NoFile.Reads + q.NotConverted.Reads + q.Dropped.Reads);
        Assert.Equal(q.Total.Bytes, q.Linked.Bytes + q.OtherDisk.Bytes + q.NoFile.Bytes + q.NotConverted.Bytes + q.Dropped.Bytes);
        Assert.Equal(q.Total.LatencySumUs, q.Linked.LatencySumUs + q.OtherDisk.LatencySumUs + q.NoFile.LatencySumUs + q.NotConverted.LatencySumUs + q.Dropped.LatencySumUs);
        Assert.Equal(1, q.PagingReads);
        Assert.Equal(4096, q.PagingBytes);
        Assert.Single(summary.Files); // 변환하지 않는 파일은 파일 표에 올리지 않음
    }

    [Fact]
    public void RepeatedRequestsMergeIntoOneEntryWithTheFirstTimeAndPagingCount()
    {
        var collector = new IoSummaryCollector(Started);
        collector.AddDiskRead(1, 4096, 4096, 100, 900, paging: true);
        collector.AddDiskRead(1, 4096, 4096, 300, 200, paging: false);
        collector.AddDiskRead(1, 4096, 4096, 50, 400, paging: true);

        IoRequest request = Assert.Single(collector.Snapshot(Started.AddSeconds(1), [], 0, null).Requests);

        Assert.Equal(new IoRequest(1, 4096, 4096, 3, 450, 300, 200, 2), request);
    }

    [Fact]
    public void RequestTableLimitDropsNewEntriesButKeepsMergingKnownOnesAndMarksTruncation()
    {
        var collector = new IoSummaryCollector(Started, maxRequests: 1);
        collector.AddDiskRead(1, 0, 4096, 10, 1, paging: false);
        collector.AddDiskRead(1, 4096, 4096, 20, 2, paging: false);
        collector.AddDiskRead(1, 0, 4096, 30, 3, paging: false);
        collector.AddDiskRead(1, 8192, 512, 40, 4, paging: false);

        IoRequestSnapshot snapshot = collector.Snapshot(Started.AddSeconds(1), [], 0, null);
        IoSummary summary = new IoBlockMapper(snapshot).Build(0, 0, 0, 0, 0, false);

        Assert.Equal(new IoRequest(1, 0, 4096, 2, 40, 30, 1, 0), Assert.Single(snapshot.Requests));
        Assert.Equal(new IoClassTotals(2, 4096 + 512, 60), summary.Quality.Dropped);
        Assert.Equal(new IoClassTotals(4, 4096 * 3 + 512, 100), summary.Quality.Total);
        Assert.True(summary.Quality.Truncated);
    }

    [Fact]
    public void FileReadFlagCountsKeepThirtyTwoValuesAndAggregateTheRest()
    {
        var collector = new IoSummaryCollector(Started);
        for (uint flags = 0; flags < IoSummaryCollector.MaxReadModeFlags + 1; flags++)
        {
            collector.AddProcessRead(@"D:\Games\a.bin", 1, 4096, flags);
        }

        IoSummary summary = new IoBlockMapper(collector.Snapshot(Started.AddSeconds(1), [], 0, null)).Build(0, 0, 0, 0, 0, false);

        Assert.Equal(IoSummaryCollector.MaxReadModeFlags, summary.Quality.ReadModeFlagCounts!.Count);
        Assert.Equal(1, summary.Quality.ReadModeFlagCountOther);
    }

    [Fact]
    public void ReadModeTotalsDistinguishCacheOnlyUncachedOnlyMixedAndPagingFiles()
    {
        var collector = new IoSummaryCollector(Started);
        collector.AddProcessRead(@"D:\Games\cache.bin", 2, 2L << 20, 0);
        collector.AddProcessRead(@"D:\Games\nocache.bin", 1, 3L << 20, 0x01);
        collector.AddProcessRead(@"D:\Games\mixed.bin", 1, 1L << 20, 0);
        collector.AddProcessRead(@"D:\Games\mixed.bin", 1, 1L << 20, 0x01);
        collector.AddProcessRead(@"D:\Games\paging.bin", 1, 1L << 20, 0x02);
        collector.AddProcessRead(@"D:\Games\paging.bin", 1, 1L << 20, 0x41);

        IoSummary summary = new IoBlockMapper(collector.Snapshot(Started.AddSeconds(1), [], 0, null)).Build(0, 0, 0, 0, 0, false);

        IoSummaryReadMode cache = Assert.Single(summary.ReadModes!, mode => mode.Path == @"D:\Games\cache.bin");
        Assert.Equal((2L, 2L << 20), (cache.CacheRequests, cache.CacheBytes));
        IoSummaryReadMode noCache = Assert.Single(summary.ReadModes!, mode => mode.Path.EndsWith("nocache.bin", StringComparison.Ordinal));
        Assert.Equal((1L, 3L << 20), (noCache.NoCacheRequests, noCache.NoCacheBytes));
        IoSummaryReadMode mixed = Assert.Single(summary.ReadModes!, mode => mode.Path.EndsWith("mixed.bin", StringComparison.Ordinal));
        Assert.Equal((1L, 1L << 20, 1L, 1L << 20), (mixed.CacheRequests, mixed.CacheBytes, mixed.NoCacheRequests, mixed.NoCacheBytes));
        IoSummaryReadMode paging = Assert.Single(summary.ReadModes!, mode => mode.Path.EndsWith("paging.bin", StringComparison.Ordinal));
        Assert.Equal((2L, 2L << 20), (paging.PagingRequests, paging.PagingBytes));
        Assert.Contains("1 files read only without the cache (3.0 MiB)", summary.FormatReadModesLogLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void DroppedReadModePathTrackingStopsAtItsLimitAndCountsUntrackedRequests()
    {
        const int maxTrackedPaths = 1 << 16;
        var collector = new IoSummaryCollector(Started, readModeRoots: [@"D:\Games"], maxReadModeFiles: 1);
        collector.AddProcessRead(@"D:\Games\keep.bin", 1, 4096, 0);
        for (int i = 0; i <= maxTrackedPaths; i++)
        {
            collector.AddProcessRead($@"D:\Games\drop{i}.bin", 1, 4096, 0x01);
        }

        collector.AddProcessRead($@"D:\Games\drop{maxTrackedPaths}.bin", 1, 4096, 0x01);
        IoSummary summary = new IoBlockMapper(collector.Snapshot(Started.AddSeconds(1), [], 0, null)).Build(0, 0, 0, 0, 0, false);
        string path = Path.Combine(_dir, "dropped.trace.run1.json.io.json");
        IoSummaryStore.Write(path, summary);
        JsonObject quality = JsonNode.Parse(File.ReadAllText(path))!["quality"]!.AsObject();

        Assert.Equal(maxTrackedPaths, (int)quality["read_modes_files_dropped"]!);
        Assert.Equal(2L, (long)quality["read_modes_dropped_requests"]!);
        Assert.False((bool)quality["read_modes_files_dropped_exact"]!);
        Assert.True(summary.Quality.Truncated);
        IoSummary restored = Assert.IsType<IoSummary>(IoSummaryStore.Read(path));
        Assert.Equal(2, restored.Quality.ReadModeDroppedRequests);
        Assert.False(restored.Quality.ReadModeFilesDroppedExact);
    }

    [Fact]
    public void BlockTableLimitCountsDroppedBlocksInsteadOfTreatingThemAsZeroReads()
    {
        var mapper = new IoBlockMapper(Snapshot(Request(1, 0, 3 * IoBlockMapper.BlockSizeBytes, 300)), maxBlocks: 2);
        mapper.AddFile(@"D:\Games\a.bin", false, true, 1, [new IoFileExtent(0, 0, 3 * IoBlockMapper.BlockSizeBytes)]);

        IoSummary summary = mapper.Build(1, 0, 0, 0, 0, false);

        Assert.Equal(2, summary.Rows.Count);
        Assert.Equal(1, summary.Quality.BlockTableDropped);
        Assert.True(summary.Quality.Truncated);
    }

    [Fact]
    public void FirstReadTimeAndPagingCountReachEveryBlockTheRequestTouched()
    {
        var mapper = new IoBlockMapper(Snapshot(
            Request(1, 0, 2 * IoBlockMapper.BlockSizeBytes, 200, count: 3, first: 500, paging: 2),
            Request(1, IoBlockMapper.BlockSizeBytes, IoBlockMapper.BlockSizeBytes, 100, first: 90)));
        mapper.AddFile(@"D:\Games\a.bin", false, true, 1, [new IoFileExtent(0, 0, 2 * IoBlockMapper.BlockSizeBytes)]);

        IoSummary summary = mapper.Build(1, 0, 0, 0, 0, false);

        Assert.Equal(500, summary.Rows[0].FirstSessionUs);
        Assert.Equal(90, summary.Rows[1].FirstSessionUs); // 블록 1 에 닿은 요청 가운데 가장 이른 시각
        Assert.Equal(2, summary.Rows[0].PagingRequests);
        Assert.Equal(2, summary.Rows[1].PagingRequests);
        Assert.Equal(3, summary.Rows[0].Requests);
        Assert.Equal(4, summary.Rows[1].Requests);
        Assert.Equal(7L * IoBlockMapper.BlockSizeBytes, summary.Rows.Sum(row => row.Bytes)); // 세 번 읽은 두 블록 + 한 번 읽은 블록 1
    }

    [Fact]
    public void FilesWithoutAnyRequestInTheirExtentsAreNeitherKeptNorListed()
    {
        var mapper = new IoBlockMapper(Snapshot(Request(1, 1000, 100, 10)));
        mapper.AddFile(@"D:\Games\far.bin", false, true, 1, [new IoFileExtent(5000, 0, 100)]);
        mapper.AddFile(@"D:\Games\other-disk.bin", false, true, 2, [new IoFileExtent(1000, 0, 100)]);
        mapper.AddFile(@"D:\Games\near.bin", true, true, 1, [new IoFileExtent(900, 0, 300)]);

        IoSummary summary = mapper.Build(3, 1, 0, 0, 0, false);

        Assert.Equal([new IoSummaryFile(@"D:\Games\near.bin", ChangedDuringSession: true)], summary.Files);
        Assert.Equal(3, summary.Quality.FilesScanned);
        Assert.Equal(1, summary.Quality.FilesChanged);
    }

    [Fact]
    public void StoreRoundTripsWithANegativeRawZeroAndRejectsVersionMismatchOrDamagedJson()
    {
        IoRequestSnapshot requests = Snapshot(Request(4, 100_000, 20_000, 1000)) with
        {
            RawTraceZeroUs = -2000,
            EventsLost = 3,
            ReadModes = [new IoSummaryReadMode(@"D:\Games\a.bin", 1, 4096, 2, 8192, 3, 12288, IoSummaryCollector.OpenSequential | IoSummaryCollector.OpenRandomAccess)],
            ReadModeFilesDropped = 2,
            ReadModeReadsUnnamed = 4,
            ReadModeFlags = [new IoFlagCount(0x01, 2), new IoFlagCount(0x42, 1)],
            ReadModeFlagsOther = 5,
        };
        var mapper = new IoBlockMapper(requests);
        mapper.AddFile(@"D:\Games\a.bin", true, true, 4, [new IoFileExtent(96_000, 60_000, 30_000)]);
        IoSummary expected = mapper.Build(7, 1, 2, 1, 42, false);
        string path = Path.Combine(_dir, "foo.trace.run1.json.io.json");

        IoSummaryStore.Write(path, expected);

        IoSummary actual = Assert.IsType<IoSummary>(IoSummaryStore.Read(path));
        Assert.Equal(expected.ObservedAtUtc, actual.ObservedAtUtc);
        Assert.Equal(expected.EndedAtUtc, actual.EndedAtUtc);
        Assert.Equal(expected.RawWindows, actual.RawWindows);
        Assert.Equal(expected.Quality with { ReadModeFlagCounts = null }, actual.Quality with { ReadModeFlagCounts = null });
        Assert.Equal(expected.Files, actual.Files);
        Assert.Equal(expected.Rows, actual.Rows);
        Assert.Equal(expected.ReadModes, actual.ReadModes);
        Assert.Equal(IoSummaryCollector.OpenSequential | IoSummaryCollector.OpenRandomAccess, Assert.Single(actual.ReadModes!).OpenOptions); // 파일을 열 때 준 옵션도 그대로
        Assert.EndsWith("opened with sequential 1, random access 1, no buffering 0 files", actual.FormatReadModesLogLine());
        Assert.Equal(expected.SessionId, actual.SessionId);
        Assert.Equal(expected.Quality.ReadModeFilesDropped, actual.Quality.ReadModeFilesDropped);
        Assert.Equal(expected.Quality.ReadModeReadsUnnamed, actual.Quality.ReadModeReadsUnnamed);
        Assert.Equal(expected.Quality.ReadModeFlagCounts, actual.Quality.ReadModeFlagCounts);
        Assert.Equal(expected.Quality.ReadModeFlagCountOther, actual.Quality.ReadModeFlagCountOther);
        Assert.Equal(requests.EndedAtUtc, actual.Quality.ReadModesObservedUntilUtc);
        Assert.Equal(-2000, actual.RawTraceZeroUs);
        Assert.Equal(Started.AddTicks(-2000 * TimeSpan.TicksPerMicrosecond), actual.RawTraceZeroAtUtc);
        Assert.Contains("not pure service time", File.ReadAllText(path)); // 지연의 뜻이 곁 파일에도 적힘
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"version\": 4", "\"version\": 2"));
        Assert.Null(IoSummaryStore.Read(path));
        File.WriteAllText(path, "not json");
        Assert.Null(IoSummaryStore.Read(path));
    }

    [Fact]
    public void VersionThreeSummariesRemainReadableAndHaveUnknownReadModes()
    {
        string path = Path.Combine(_dir, "legacy.trace.run1.json.io.json");
        IoSummaryStore.Write(path, SomeSummary());
        JsonObject root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["version"] = 3;
        root.Remove("read_modes");
        JsonObject quality = root["quality"]!.AsObject();
        quality.Remove("read_modes_files_dropped");
        quality.Remove("read_modes_dropped_requests");
        quality.Remove("read_modes_files_dropped_exact");
        quality.Remove("read_modes_reads_unnamed");
        quality.Remove("read_modes_flag_counts");
        quality.Remove("read_modes_flag_count_other");
        quality.Remove("read_modes_observed_until_utc");
        File.WriteAllText(path, root.ToJsonString());

        IoSummary summary = Assert.IsType<IoSummary>(IoSummaryStore.Read(path));

        Assert.False(summary.HasReadModes);
        Assert.Empty(summary.ReadModes!);
        Assert.Null(summary.Quality.ReadModesObservedUntilUtc);
    }

    private IoSummary SomeSummary() => new IoBlockMapper(Snapshot()).Build(0, 0, 0, 0, 0, false);

    [Fact]
    public void AFailedReplacementKeepsTheInvalidMarkOfTheOldSummary()
    {
        string path = Path.Combine(_dir, "a.trace.run1.json.io.json");
        IoSummaryStore.Write(path, SomeSummary());
        IoSummaryStore.MarkInvalid(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) // 잠겨 교체가 실패함
        {
            Assert.ThrowsAny<Exception>(() => IoSummaryStore.Write(path, SomeSummary())); // 잠긴 파일 교체는 IOException 또는 UnauthorizedAccessException
        }

        Assert.True(File.Exists(IoSummaryStore.InvalidPath(path))); // 낡은 집계가 표지 없이 다시 유효해지지 않음
        Assert.Null(IoSummaryStore.Read(path));

        IoSummaryStore.Write(path, SomeSummary()); // 교체가 성공하면 표지도 사라짐
        Assert.False(File.Exists(IoSummaryStore.InvalidPath(path)));
        Assert.NotNull(IoSummaryStore.Read(path));
    }

    [Fact]
    public void ClearSidecarsKeepsTheMarkOfASummaryItCouldNotDeleteAndRemovesOrphanMarks()
    {
        string raw = Path.Combine(_dir, "b.trace.json");
        File.WriteAllText(raw, "[]");
        string locked = IoSummaryStore.RunPath(TraceWriter.RunPath(raw, 2));
        IoSummaryStore.Write(locked, SomeSummary());
        IoSummaryStore.MarkInvalid(locked);
        string orphan = IoSummaryStore.RunPath(TraceWriter.RunPath(raw, 3)); // 집계는 없고 표지만
        File.WriteAllText(IoSummaryStore.InvalidPath(orphan), "1");

        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            TraceStageIndex.ClearSidecars(raw);
        }

        Assert.True(File.Exists(locked));
        Assert.True(File.Exists(IoSummaryStore.InvalidPath(locked))); // 집계를 못 지웠으니 표지는 남김
        Assert.Null(IoSummaryStore.Read(locked));
        Assert.False(File.Exists(IoSummaryStore.InvalidPath(orphan)));

        TraceStageIndex.ClearSidecars(raw); // 잠금이 풀린 뒤에는 둘 다 지움

        Assert.False(File.Exists(locked));
        Assert.False(File.Exists(IoSummaryStore.InvalidPath(locked)));
    }

    [Fact]
    public void MovingSummariesCarriesTheInvalidMarkAndClearsAStaleMarkAtTheDestination()
    {
        string run1 = Path.Combine(_dir, "c.trace.run1.json.io.json");
        string run2 = Path.Combine(_dir, "c.trace.run2.json.io.json");
        string run3 = Path.Combine(_dir, "c.trace.run3.json.io.json");
        foreach (string file in new[] { run1, run2, run3 })
        {
            IoSummaryStore.Write(file, SomeSummary());
        }

        IoSummaryStore.MarkInvalid(run2); // run2 의 집계는 무효, run1 자리에는 옛 정상 집계, run3 은 정상
        IoSummaryStore.MarkInvalid(run1); // 옮겨 갈 자리에 남은 옛 표지
        IoSummaryStore.Write(run1, SomeSummary()); // 정상으로 다시 써 표지를 치운 뒤
        IoSummaryStore.MarkInvalid(run1); // 다시 옛 표지만 남은 상황을 만듦

        static void Move(string source, string destination) => File.Move(source, destination, true);
        IoSummaryStore.MoveSummary(run2, run1, Move); // 무효 집계를 당김

        Assert.Null(IoSummaryStore.Read(run1)); // 옮긴 자리에서도 무효
        Assert.False(File.Exists(IoSummaryStore.InvalidPath(run2))); // 원본 자리에는 표지가 남지 않음

        IoSummaryStore.MoveSummary(run3, run2, Move); // 뒤따라 정상 집계를 당김

        Assert.NotNull(IoSummaryStore.Read(run2)); // 비워진 자리에서 정상으로 읽힘
        Assert.Null(IoSummaryStore.Read(run1)); // 앞의 무효 집계는 그대로 무효
        Assert.False(File.Exists(run3));

        IoSummaryStore.MarkInvalid(run3); // 정상 집계가 옛 표지가 남은 자리로 옮겨 가는 경우
        IoSummaryStore.Write(run3, SomeSummary());
        IoSummaryStore.MarkInvalid(run3);
        IoSummaryStore.MoveSummary(run2, run3, Move);
        Assert.NotNull(IoSummaryStore.Read(run3)); // 정상 집계는 옛 표지가 치워져 유효
    }

    [Fact]
    public void RunSummaryIsAcceptedOnlyForTheRawFileItFingerprinted()
    {
        string raw = Path.Combine(_dir, "run.trace.json");
        File.WriteAllText(raw, "[]\n");
        IoSummaryStore.WriteForRun(raw, new IoBlockMapper(Snapshot()).Build(0, 0, 0, 0, 0, false));

        IoSummary saved = Assert.IsType<IoSummary>(IoSummaryStore.ReadForRun(raw));
        Assert.Equal(IoSummaryStore.RawFingerprint(raw), saved.RawFingerprint);
        Assert.False(string.IsNullOrWhiteSpace(saved.SessionId));

        File.WriteAllText(raw, "[{}]\n");
        Assert.Null(IoSummaryStore.ReadForRun(raw));
    }

    [Fact]
    public void SummaryOnlyRunsUseUniqueSessionsAndStayWithinTheFileCap()
    {
        string raw = Path.Combine(_dir, "run.trace.json");
        File.WriteAllText(raw, "[]\n");
        for (int i = 0; i < IoSummaryStore.MaxStandaloneFiles + 2; i++)
        {
            IoSummaryStore.WriteStandalone(raw, new IoBlockMapper(Snapshot()).Build(0, 0, 0, 0, 0, false));
        }

        string[] existing = IoSummaryStore.StandalonePaths(raw).Where(File.Exists).ToArray();
        Assert.Equal(IoSummaryStore.MaxStandaloneFiles, existing.Length);
        Assert.Equal(IoSummaryStore.MaxStandaloneFiles, existing.Select(path => IoSummaryStore.ReadStandaloneForRaw(path, raw)!.SessionId).Distinct().Count());
    }

    [Fact]
    public void OwnReadsOfTheServiceAreLeftOutUnlessTheGameReadTheSameRangeOrTheRequestWasNotAPagingReadOnce()
    {
        const string File = @"D:\Games.bin";
        var batch = new SelfReadBatch(Started.AddSeconds(4), Started.AddSeconds(6),
            [new ReadOp(File, 0, 4096), new ReadOp(File, 65_536, 4096), new ReadOp(File, 131_072, 4096), new ReadOp(File, 196_608, 4096), new ReadOp(File, 262_144, 4096)]);
        var game = new GameRead(File, Started.AddSeconds(5.5), 65_536, 4096); // 서비스와 같은 시간대에 게임도 같은 구간을 읽음
        IoRequestSnapshot requests = Snapshot(
            Request(4, 0, 4096, 100, first: 5_000_000, paging: 1), // 서비스만 읽음: 뺌
            Request(4, 65_536, 4096, 100, first: 5_000_000, paging: 1), // 게임도 읽음: 남김
            Request(4, 131_072, 4096, 100, first: 5_000_000, paging: 0), // 페이징이 아닌 읽기: 남김
            Request(4, 196_608, 4096, 100, first: 50_000_000, paging: 1), // 서비스 읽기가 끝난 지 한참 뒤: 남김
            Request(4, 262_144, 4096, 100, count: 2, first: 5_000_000, paging: 2)); // 두 번 읽힘: 남김
        requests = requests with { RawWindows = [new IoSummaryWindow(0, 60_000_000)], SelfReads = new SelfReadFilter(Started, [batch], [game]) };
        var mapper = new IoBlockMapper(requests);
        mapper.AddFile(File, false, true, 4, [new IoFileExtent(0, 0, 400_000)]);

        IoSummary summary = mapper.Build(1, 0, 0, 0, 1, false);

        Assert.Equal([1L, 2L, 3L, 4L], summary.Rows.Select(row => row.BlockNumber));
        Assert.Equal(new IoClassTotals(1, 4096, 100), summary.Quality.SelfRead);
        Assert.Equal(new IoClassTotals(5, 4096 * 5, 400), summary.Quality.Linked);
        Assert.Equal(summary.Quality.Total.Bytes, summary.Quality.Linked.Bytes + summary.Quality.SelfRead.Bytes);

        string path = Path.Combine(_dir, "own.trace.run1.json.io.json");
        IoSummaryStore.Write(path, summary);
        Assert.Equal(summary.Quality.SelfRead, IoSummaryStore.Read(path)!.Quality.SelfRead);
    }

    [Fact]
    public void WithoutAFilterNothingIsLeftOut()
    {
        var mapper = new IoBlockMapper(Snapshot(Request(4, 0, 4096, 100, first: 5_000_000, paging: 1)));
        mapper.AddFile(@"D:\Games.bin", false, true, 4, [new IoFileExtent(0, 0, 8192)]);

        IoSummary summary = mapper.Build(1, 0, 0, 0, 1, false);

        Assert.Single(summary.Rows);
        Assert.Equal(default, summary.Quality.SelfRead);
    }

    [Fact]
    public void OnlyReadsInsideARawWindowCanBeLeftOutBecauseOutsideItTheGameReadsAreNotRecorded()
    {
        const string File = @"D:\Games.bin";
        var batch = new SelfReadBatch(Started.AddSeconds(4), Started.AddSeconds(6), [new ReadOp(File, 0, 4096), new ReadOp(File, 65_536, 4096)]);
        IoRequestSnapshot requests = Snapshot(
            Request(4, 0, 4096, 100, first: 5_000_000, paging: 1), // 창 안
            Request(4, 65_536, 4096, 100, first: 5_500_000, paging: 1)); // 창 밖: 같은 시간대여도 게임 읽기를 확인할 수 없어 남김
        requests = requests with { RawWindows = [new IoSummaryWindow(0, 5_200_000)], SelfReads = new SelfReadFilter(Started, [batch], []) };
        var mapper = new IoBlockMapper(requests);
        mapper.AddFile(File, false, true, 4, [new IoFileExtent(0, 0, 200_000)]);

        IoSummary summary = mapper.Build(1, 0, 0, 0, 1, false);

        Assert.Equal([1L], summary.Rows.Select(row => row.BlockNumber));
        Assert.Equal(new IoClassTotals(1, 4096, 100), summary.Quality.SelfRead);
    }

    [Fact]
    public void ARequestOnlyPartlyCoveredByTheServiceRangeIsKeptAndAdjacentRangesCountAsOne()
    {
        const string File = @"D:\Games.bin";
        var batch = new SelfReadBatch(Started.AddSeconds(4), Started.AddSeconds(6),
            [new ReadOp(File, 0, 4096), new ReadOp(File, 4096, 4096), new ReadOp(File, 131_072, 4096)]);
        IoRequestSnapshot requests = Snapshot(
            Request(4, 0, 8192, 100, first: 5_000_000, paging: 1), // 이웃한 두 서비스 구간이 전체를 덮음: 뺌
            Request(4, 131_072, 8192, 100, first: 5_000_000, paging: 1)); // 앞 절반만 덮임: 남김
        requests = requests with { RawWindows = [new IoSummaryWindow(0, 60_000_000)], SelfReads = new SelfReadFilter(Started, [batch], []) };
        var mapper = new IoBlockMapper(requests);
        mapper.AddFile(File, false, true, 4, [new IoFileExtent(0, 0, 400_000)]);

        IoSummary summary = mapper.Build(1, 0, 0, 0, 1, false);

        Assert.Equal([2L], summary.Rows.Select(row => row.BlockNumber));
        Assert.Equal(new IoClassTotals(1, 8192, 100), summary.Quality.SelfRead);
        Assert.Equal(new IoClassTotals(1, 8192, 100), summary.Quality.Linked);
    }

    [Fact]
    public void AnObservationKeepsTheEarliestBatchesPastTheLimitAndIgnoresOtherObservations()
    {
        var ops = new ReadOp[] { new(@"D:\Games\log-test.bin", 0, 4096) };
        var collector = new IoSummaryCollector(Started); // 관찰 시작
        SelfReadObservation other = SelfReadLog.Begin();
        _ = other.End(); // 끝난 관찰은 더 받지 않음
        SelfReadLog.Note(ops, Started, Started.AddSeconds(1));
        SelfReadLog.Note(new ReadOp[SelfReadLog.MaxOps], Started, Started.AddSeconds(2)); // 상한을 넘김: 버림

        IoRequestSnapshot snapshot = collector.Snapshot(Started.AddSeconds(3), [], 0, null);

        Assert.Contains(snapshot.SelfReadBatches!, b => b.Ops.Length == 1 && b.Ops[0].Path == ops[0].Path);
        Assert.DoesNotContain(snapshot.SelfReadBatches!, b => b.Ops.Length == SelfReadLog.MaxOps);
        Assert.True(snapshot.SelfReadDroppedOps >= SelfReadLog.MaxOps);
        Assert.Empty(other.End().Batches);
    }

    [Fact]
    public void SummaryLineIncludesTheClassesLatencyQualityAndScanCost()
    {
        var quality = new IoSummaryQuality(
            new IoClassTotals(4, 4L << 20, 2500), 1600, new IoClassTotals(1, 1L << 20, 500), new IoClassTotals(1, 1L << 20, 500),
            new IoClassTotals(1, 1L << 20, 500), new IoClassTotals(1, 1L << 20, 1000), default, 0, 0, 3, BlockTableDropped: 0, FilesScanned: 12, FilesChanged: 2, ScanMs: 34);
        var summary = new IoSummary(Started, Started.AddSeconds(2), [], quality, [], []);

        Assert.Equal("I/O summary: observed 2.0s, 4 disk reads/4.0 MiB, block linked 1 reads/1.0 MiB (25.0% of bytes), other disk 1 reads/1.0 MiB, no file 1 reads/1.0 MiB, not converted 1 reads/1.0 MiB, "
            + "dropped 0 reads/0.0 MiB, latency 2.5 ms sum/1.6 ms max, 3 events lost, truncated False (blocks 0), 12 files scanned in 34 ms, 2 changed during session, 0 reads/0.0 MiB left out as the service's own reads",
            summary.FormatLogLine());
    }

    // ---- 쓰기 기록 ----

    private static IoSummary BuildWrites(IoSummaryCollector collector) => new IoBlockMapper(collector.Snapshot(Started.AddSeconds(1), [], 0, null)).Build(0, 0, 0, 0, 0, false);

    [Fact]
    public void WritesAreKeptPerFileWithMergedRangesNoCacheCountsAndTimesAndPagingWritesAreNotNoCache()
    {
        var collector = new IoSummaryCollector(Started, readModeRoots: [@"D:\Games"]);
        collector.AddProcessWrite(@"D:\Games\x.xpakdata", 0, 1000, 0x01, 5); // 캐시 없이 쓴 처음 쓰기
        collector.AddProcessWrite(@"D:\Games\x.xpakdata", 1000, 1000, 0x01, 7); // 이어 붙음: 한 구간으로 합침
        collector.AddProcessWrite(@"D:\Games\x.xpakdata", 2000, 500, 0, 9); // 캐시 경유
        collector.AddProcessWrite(@"D:\Games\x.xpakdata", 10_000, 100, 0x03, 11); // 페이징 (캐시 관리자의 쓰기): 캐시 없음으로 세지 않음
        collector.AddProcessWrite(@"D:\Games\x.xpakdata", 0, 0, 0, 12); // 길이 0 은 무시
        collector.AddProcessWrite(@"D:\Other\y.bin", 0, 10, 0, 1); // 집계 경로 밖
        collector.AddProcessWrite(null, 0, 10, 0, 1); // 이름 모름

        IoSummaryWriteFile file = Assert.Single(BuildWrites(collector).WriteFiles!);

        Assert.Equal(@"D:\Games\x.xpakdata", file.Path);
        Assert.Equal((4L, 2600L, 2L, 2000L, 5L, 11L), (file.Writes, file.Bytes, file.NoCacheWrites, file.NoCacheBytes, file.FirstUs, file.LastUs));
        Assert.Equal([new IoWriteRange(0, 2500), new IoWriteRange(10_000, 100)], file.Ranges);
        Assert.False(file.RangesTruncated);
    }

    [Fact]
    public void SavedWriteFilesAreCappedKeepingTheFilesWithTheMostBytes()
    {
        var collector = new IoSummaryCollector(Started);
        for (int i = 0; i < IoSummaryCollector.MaxWriteFilesSaved + 10; i++)
        {
            collector.AddProcessWrite($@"D:\Games\f{i}.bin", 0, 100 + i, 0, 1);
        }

        IReadOnlyList<IoSummaryWriteFile> files = BuildWrites(collector).WriteFiles!;

        Assert.Equal(IoSummaryCollector.MaxWriteFilesSaved, files.Count);
        Assert.Equal($@"D:\Games\f{IoSummaryCollector.MaxWriteFilesSaved + 9}.bin", files[0].Path); // 쓴 바이트가 많은 순
        Assert.DoesNotContain(files, f => f.Path == @"D:\Games\f0.bin");
    }

    [Fact]
    public void AFileWithMoreScatteredWritesThanTheRangeCapKeepsTheCapAndMarksTruncation()
    {
        var collector = new IoSummaryCollector(Started);
        for (int i = 0; i < IoSummaryCollector.MaxWriteRangesPerFile + 3; i++)
        {
            collector.AddProcessWrite(@"D:\Games\scattered.bin", i * 1000L, 10, 0, i); // 군데군데 덮어씀: 이어지지 않는 구간
        }

        IoSummaryWriteFile file = Assert.Single(BuildWrites(collector).WriteFiles!);

        Assert.Equal(IoSummaryCollector.MaxWriteRangesPerFile, file.Ranges.Count);
        Assert.True(file.RangesTruncated);
        Assert.Equal(IoSummaryCollector.MaxWriteRangesPerFile + 3, file.Writes); // 쓰기 수·바이트는 구간 상한과 무관하게 셈
    }

    [Fact]
    public void WriteRecordsSurviveTheStoreAndSummariesWithoutThemStillRead()
    {
        var collector = new IoSummaryCollector(Started);
        collector.AddProcessWrite(@"D:\Games\x.xpakdata", 4096, 8192, 0x01, 1234);
        IoSummary summary = BuildWrites(collector);
        string path = Path.Combine(_dir, "w.trace.run1.json.io.json");

        IoSummaryStore.Write(path, summary);
        IoSummary back = Assert.IsType<IoSummary>(IoSummaryStore.Read(path));

        IoSummaryWriteFile saved = Assert.Single(summary.WriteFiles!);
        IoSummaryWriteFile read = Assert.Single(back.WriteFiles!);
        Assert.Equal((saved.Path, saved.Writes, saved.Bytes, saved.NoCacheWrites, saved.NoCacheBytes, saved.FirstUs, saved.LastUs, saved.RangesTruncated),
            (read.Path, read.Writes, read.Bytes, read.NoCacheWrites, read.NoCacheBytes, read.FirstUs, read.LastUs, read.RangesTruncated));
        Assert.Equal([new IoWriteRange(4096, 8192)], read.Ranges);

        string text = File.ReadAllText(path);
        File.WriteAllText(path, text.Replace("\"write_files\"", "\"x_write_files\"", StringComparison.Ordinal)); // 옛 집계처럼 항목이 없음
        Assert.Null(Assert.IsType<IoSummary>(IoSummaryStore.Read(path)).WriteFiles);

        File.WriteAllText(path, text.Replace("\"nocache_writes\": 1", "\"nocache_writes\": \"x\"", StringComparison.Ordinal)); // 모양이 다르면 집계 전체를 읽지 않음
        Assert.Null(IoSummaryStore.Read(path));

        IoSummaryStore.Write(path, SomeSummary());
        Assert.DoesNotContain("write_files", File.ReadAllText(path), StringComparison.Ordinal); // 쓰기가 없으면 항목도 만들지 않음
    }
}
