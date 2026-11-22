using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tests.Windows;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 플레이의 초 단위 기록 (열 만들기, 프로세스별 CPU 차이, 파일 쓰기·보관), 서비스 동작 표시, 표본 확인의 위치 고르기. 서비스 연결은 가짜 프레임 세션·가짜 조회로 확인합니다.
public sealed class PlaySeriesTests
{
    private const long Mib = 1L << 20;
    private static readonly DateTimeOffset Start = new(2026, 11, 21, 20, 1, 34, TimeSpan.Zero);

    // ---- 초 단위 값 (원시 값의 앞뒤 차이) ----

    private static PlaySeriesRaw Raw(long idle, long total, IReadOnlyList<ProcessUsage> processes, long? repurposed = null, uint? pageReads = null, string ops = "") =>
        new(idle, total, processes, 200, 7000, repurposed, pageReads, 6500, 3000, ops);

    private static PlayDiskCounters Disk(uint number, long readBytes, uint reads, long readTime, long writeBytes, uint writes, long writeTime, uint queue = 0) =>
        new(number, readBytes, reads, readTime, 0, queue, writeBytes, writes, writeTime);

    [Fact]
    public void CpuIsTheShareOfAllLogicalProcessorsPerGameServiceAndTheTwoBusiestOtherNamesAndSameNamesAreAdded()
    {
        var builder = new PlaySeriesBuilder();
        List<ProcessUsage> first =
        [
            new(0, "Idle", 5000, 0), new(100, "game.exe", 1000, 0), new(101, "gamehelper.exe", 0, 0), new(200, "AsterCache.Service.exe", 0, 0),
            new(300, "chrome.exe", 100, 0), new(301, "chrome.exe", 100, 0), new(400, "obs.exe", 0, 0), new(500, "tiny.exe", 0, 0),
        ];
        List<ProcessUsage> second =
        [
            new(0, "Idle", 5500, 0), new(100, "game.exe", 1400, 5000), new(101, "gamehelper.exe", 100, 0), new(200, "AsterCache.Service.exe", 10, 0),
            new(300, "chrome.exe", 400, 0), new(301, "chrome.exe", 400, 0), new(400, "obs.exe", 100, 9000), new(500, "tiny.exe", 5, 0), new(600, "new.exe", 20, 0),
        ];

        PlaySeriesPoint start = builder.Next(Raw(1000, 10_000, first), [100, 101], [], []);
        PlaySeriesPoint point = builder.Next(Raw(1500, 11_000, second, ops: "keep"), [100, 101], [], []); // 시스템 전체 1000 틱 중 유휴 500

        Assert.Null(start.CpuSystemPct); // 앞의 값이 없는 첫 초는 차이 값이 없음
        Assert.Null(start.CpuGamePct);
        Assert.Equal(50.0, point.CpuSystemPct);
        Assert.Equal(50.0, point.CpuGamePct); // 게임 프로세스 둘의 합 (400 + 100)
        Assert.Equal(1.0, point.CpuServicePct);
        Assert.Equal(("chrome.exe", 60.0), point.Other1); // 같은 이름은 합쳐서 셈 (300 + 300)
        Assert.Equal(("obs.exe", 10.0), point.Other2);
        Assert.Equal(("obs.exe", 9000L), (point.WriteName, point.WriteBytes)); // 쓰기는 모든 프로세스 중 가장 큰 이름
        Assert.Equal("keep", point.Ops);
        Assert.Equal(7000, point.AvailableMib);
        Assert.Equal((6500, 3000), (point.GpuMib, point.GpuGameMib));
    }

    [Fact]
    public void AProcessWhoseTimeWentBackwardsCountsAsZeroAndAnEmptyProcessListLeavesOnlyTheSystemValues()
    {
        var builder = new PlaySeriesBuilder();
        _ = builder.Next(Raw(0, 1000, [new(100, "game.exe", 900, 50), new(300, "a.exe", 800, 0)]), [100], [], []);
        PlaySeriesPoint point = builder.Next(Raw(100, 2000, [new(100, "game.exe", 100, 10), new(300, "a.exe", 900, 0)]), [100], [], []); // 번호가 다시 쓰여 값이 줄어듦

        Assert.Equal(0.0, point.CpuGamePct);
        Assert.Equal(("a.exe", 10.0), point.Other1);
        Assert.Null(point.WriteName);

        PlaySeriesPoint noProcesses = builder.Next(Raw(200, 3000, []), [100], [], []);
        Assert.Equal(90.0, noProcesses.CpuSystemPct);
        Assert.Null(noProcesses.CpuGamePct);
        Assert.Null(noProcesses.Other1);
    }

    [Fact]
    public void ReusedStandbyBytesAndSystemPageReadsAreDifferencesWithThe32BitCounterWrapping()
    {
        var builder = new PlaySeriesBuilder();
        _ = builder.Next(Raw(0, 1000, [], repurposed: 5000 * Mib, pageReads: uint.MaxValue - 1), [], [], []);
        PlaySeriesPoint point = builder.Next(Raw(0, 2000, [], repurposed: 5512 * Mib, pageReads: 5), [], [], []);

        Assert.Equal(512.0, point.ReusedMib);
        Assert.Equal(7, point.PageReads);
    }

    [Fact]
    public void DiskValuesSumTheMatchingDisksSkipResetOnesAndWrapTheCounts()
    {
        IReadOnlyList<PlayDiskCounters> before = [Disk(1, 1000, uint.MaxValue - 1, 100_000, 5000, 10, 20_000), Disk(2, 500, 1, 0, 0, 0, 0), Disk(3, 100, 1, 1, 1, 1, 1)];
        IReadOnlyList<PlayDiskCounters> after = [Disk(1, 5096, 3, 140_000, 9000, 14, 60_000, queue: 2), Disk(2, 100, 2, 0, 0, 0, 0), Disk(4, 1, 1, 1, 1, 1, 1)]; // 디스크 2 는 누계가 줄어 뺌, 3 은 사라짐, 4 는 짝이 없음

        PlaySeriesDisk disk = Assert.IsType<PlaySeriesDisk>(PlaySeriesBuilder.Disk(before, after));

        Assert.Equal((5UL, 4096L, 40_000 / 10_000d / 5), (disk.ReadCount, disk.ReadBytes, disk.ReadMeanMs)); // 32비트 순환: 3 - (2^32 - 2) = 5
        Assert.Equal((4UL, 4000L, 40_000 / 10_000d / 4, 2U), (disk.WriteCount, disk.WriteBytes, disk.WriteMeanMs, disk.Queue));
        Assert.Null(PlaySeriesBuilder.Disk(before, [Disk(9, 0, 0, 0, 0, 0, 0)]));
    }

    [Fact]
    public void AfterAResetTheFirstSecondHasNoDifferenceValuesSoSeveralSecondsAreNeverWrittenAsOne()
    {
        var builder = new PlaySeriesBuilder();
        _ = builder.Next(Raw(0, 1000, [new(100, "game.exe", 0, 0)], repurposed: 0, pageReads: 0), [100], [], []);
        builder.Reset(); // 값을 못 읽은 몇 초
        PlaySeriesPoint point = builder.Next(Raw(500, 9000, [new(100, "game.exe", 4000, 9000)], repurposed: 900 * Mib, pageReads: 5000), [100], [], []);

        Assert.Equal((null, null, null, null, null), (point.CpuSystemPct, point.CpuGamePct, point.ReusedMib, point.PageReads, point.WriteName));
        Assert.Equal(7000, point.AvailableMib); // 절대값 칸은 그대로
        PlaySeriesPoint next = builder.Next(Raw(1000, 10_000, [new(100, "game.exe", 4500, 9000)], repurposed: 901 * Mib, pageReads: 5010), [100], [], []);
        Assert.Equal((50.0, 1.0, 10L), (next.CpuGamePct, next.ReusedMib, next.PageReads)); // 기준이 새로 잡힌 뒤에는 다시 차이가 나옴
    }

    // ---- 줄 만들기 ----

    private static string Cell(string line, string column) => line.Split(',')[Array.IndexOf(PlaySeriesFormat.Header.Split(','), column)];

    [Fact]
    public void EveryLineHasTheSameNumberOfCellsAsTheHeaderAndTheValuesSitInTheirColumns()
    {
        int columns = PlaySeriesFormat.Header.Split(',').Length;
        var point = new PlaySeriesPoint(86.04, 55.5, 0.26, ("chrome,exe", 12.34), null, 5738, 512.26, 1234, 6852, 3000, new PlaySeriesDisk(17, 1_048_576, 2.5, 3, 4096, 0.126, 4), "backup.exe", 987654, "keep+stream");

        string full = PlaySeriesFormat.Line(61, new FrameSecond(61, 58, 133.4, 2, 1, 1, 1500), point);
        string noDisk = PlaySeriesFormat.Line(62, default, point with { Disk = null, WriteName = null });
        string noPoint = PlaySeriesFormat.Line(63, new FrameSecond(63, 60, 16.7, 0, 0), null);

        Assert.All([full, noDisk, noPoint], line => Assert.Equal(columns, line.Split(',').Length));
        Assert.Equal(("61", "58", "133.4", "2", "1", "1", "1500.0"), (Cell(full, "t_s"), Cell(full, "frames"), Cell(full, "max_frame_ms"), Cell(full, "over50_n"), Cell(full, "over100_n"), Cell(full, "pauses_n"), Cell(full, "pause_max_ms")));
        Assert.Equal(("86.0", "55.5", "0.3", "chrome_exe", "12.3", string.Empty), (Cell(full, "cpu_sys_pct"), Cell(full, "cpu_game_pct"), Cell(full, "cpu_service_pct"), Cell(full, "cpu_other1_name"), Cell(full, "cpu_other1_pct"), Cell(full, "cpu_other2_name"))); // 이름의 쉼표는 밑줄로
        Assert.Equal(("5738", "512.3", "1234", "6852", "3000"), (Cell(full, "avail_mib"), Cell(full, "standby57_reused_mib"), Cell(full, "page_reads_n"), Cell(full, "gpu_mib"), Cell(full, "gpu_game_mib")));
        Assert.Equal(("17", "1048576", "2.50", "3", "4096", "0.13", "4"), (Cell(full, "disk_read_n"), Cell(full, "disk_read_bytes"), Cell(full, "disk_read_mean_ms"), Cell(full, "disk_write_n"), Cell(full, "disk_write_bytes"), Cell(full, "disk_write_mean_ms"), Cell(full, "disk_queue")));
        Assert.Equal(("backup.exe", "987654", "keep+stream"), (Cell(full, "write_anyvol_top_name"), Cell(full, "write_anyvol_top_bytes"), Cell(full, "service_ops")));
        Assert.Equal(string.Empty, Cell(noDisk, "disk_read_n"));
        Assert.Equal((string.Empty, string.Empty), (Cell(noDisk, "write_anyvol_top_name"), Cell(noDisk, "write_anyvol_top_bytes")));
        Assert.Equal(("63", "60", "16.7", string.Empty, string.Empty), (Cell(noPoint, "t_s"), Cell(noPoint, "frames"), Cell(noPoint, "max_frame_ms"), Cell(noPoint, "cpu_sys_pct"), Cell(noPoint, "service_ops")));
    }

    [Fact]
    public void TheGameReadBytesWorkingSetAndLoadPhaseColumnsComeLastSoTheOtherColumnsStayInPlaceAndAFirstSecondLeavesTheDifferenceEmpty()
    {
        var builder = new PlaySeriesBuilder();
        List<ProcessUsage> first = [new(100, "game.exe", 0, 0, 10 * Mib, 2000 * Mib), new(101, "helper.exe", 0, 0, 1 * Mib, 100 * Mib), new(300, "chrome.exe", 0, 0, 500 * Mib, 900 * Mib)];
        List<ProcessUsage> second = [new(100, "game.exe", 0, 0, 40 * Mib, 2500 * Mib), new(101, "helper.exe", 0, 0, 1 * Mib, 150 * Mib), new(102, "new.exe", 0, 0, 900 * Mib, 50 * Mib), new(300, "chrome.exe", 0, 0, 900 * Mib, 900 * Mib)];

        PlaySeriesPoint start = builder.Next(Raw(0, 1000, first), [100, 101, 102], [], [], LoadState.Loading);
        PlaySeriesPoint point = builder.Next(Raw(0, 2000, second), [100, 101, 102], [], [], LoadState.NotLoading);

        Assert.Equal((null, null), (start.GameReadBytes, start.GameWorkingSetMib)); // 앞의 값이 없는 첫 초
        Assert.Equal((30L * Mib, 2700L), (point.GameReadBytes, point.GameWorkingSetMib)); // 게임 프로세스만: 읽기 증가분 (새로 보인 프로세스는 셈하지 않음), 작업 집합 (2500 + 150 + 50)
        string[] names = PlaySeriesFormat.Header.Split(',');
        Assert.Equal(["service_ops", "game_read_bytes", "game_ws_mib", "load_phase"], names[^4..]);
        string line = PlaySeriesFormat.Line(5, default, point);
        Assert.Equal(((30L * Mib).ToString(CultureInfo.InvariantCulture), "2700", "not_loading"), (Cell(line, "game_read_bytes"), Cell(line, "game_ws_mib"), Cell(line, "load_phase")));
        string unknown = PlaySeriesFormat.Line(5, default, point with { Load = LoadState.Unknown, GameReadBytes = null });
        Assert.Equal((string.Empty, "2700", string.Empty), (Cell(unknown, "game_read_bytes"), Cell(unknown, "game_ws_mib"), Cell(unknown, "load_phase")));
        Assert.Equal("loading", Cell(PlaySeriesFormat.Line(4, default, start with { Load = LoadState.Loading }), "load_phase"));
    }

    [Fact]
    public void NumbersUseAPointWhateverTheCurrentCulture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        try
        {
            var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone(); // 전역화 불변 모드에서도 쓸 수 있게 소수점이 쉼표인 문화권을 만듦
            comma.NumberFormat.NumberDecimalSeparator = ",";
            CultureInfo.CurrentCulture = comma;
            Assert.Equal("12,3", 12.34.ToString("F1", CultureInfo.CurrentCulture)); // 현재 문화권이 실제로 쉼표를 쓰는지 먼저 확인
            Assert.Equal("12.3", Cell(PlaySeriesFormat.Line(1, default, new PlaySeriesPoint(12.34, null, null, null, null, null, null, null, null, null, null, null, 0, string.Empty)), "cpu_sys_pct"));
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }
    }

    [Fact]
    public void TheListsLineHasFreeStandbyAndReusedMiBPerPriorityInTheHeaderOrder()
    {
        var lists = new MemoryPriorityLists([.. Enumerable.Range(0, 8).Select(i => (100L + i) * Mib)], [.. Enumerable.Range(0, 8).Select(i => (1000L + i) * Mib)], 30 * Mib);

        string[] cells = PlaySeriesFormat.ListsLine(30, lists).Split(',');

        Assert.Equal(PlaySeriesFormat.ListsHeader.Split(',').Length, cells.Length);
        Assert.Equal(["30", "30", "100", "101", "102", "103", "104", "105", "106", "107", "1000", "1001", "1002", "1003", "1004", "1005", "1006", "1007"], cells);
    }

    // ---- 파일 ----

    private static string[] Lines(string path) => File.ReadAllLines(path);

    [Fact]
    public void LinesAreAppendedOnlyWhenTheMinuteIsUpWithOneHeaderAndTheNameIsLoggedOnce()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        DateTimeOffset now = Start;
        var file = new PlaySeriesFile(dir.Combine("play_series"), "Call of Duty", Start, log, () => now);

        file.AddRow("1,a");
        file.FlushIfDue();
        Assert.False(Directory.Exists(dir.Combine("play_series"))); // 아직 한 번도 쓰지 않음 (1 분 전)

        now = Start.AddSeconds(61);
        file.AddRow("2,b");
        file.FlushIfDue();
        string path = Path.Combine(dir.Combine("play_series"), file.FileName);
        Assert.Equal([PlaySeriesFormat.Header, "1,a", "2,b"], Lines(path));

        file.AddRow("3,c");
        file.FlushIfDue(); // 직전 쓰기 뒤 1 분이 안 됨
        Assert.Equal(3, Lines(path).Length);

        file.AddListsRow("0,1");
        file.Close();
        file.AddRow("4,d"); // 닫은 뒤의 줄은 버림
        file.Close();

        Assert.Equal([PlaySeriesFormat.Header, "1,a", "2,b", "3,c"], Lines(path));
        Assert.Equal([PlaySeriesFormat.ListsHeader, "0,1"], Lines(Path.Combine(dir.Combine("play_series"), file.ListsFileName)));
        Assert.Matches(@"^Call_of_Duty_\d{8}-\d{6}\.csv$", file.FileName);
        Assert.Single(log.Lines, l => l.Contains("play record series:", StringComparison.Ordinal));
        Assert.Contains(file.FileName, log.Lines.Single(l => l.Contains("play record series:", StringComparison.Ordinal)));
    }

    [Fact]
    public void OnlyTheNewestEightPlaysOfAGameKeepTheirFilesTogetherWithTheListsFileAndOtherGamesAreUntouched()
    {
        using var dir = new TempDir();
        string folder = dir.Combine("play_series");
        _ = Directory.CreateDirectory(folder);
        for (int day = 1; day <= 10; day++)
        {
            string stamp = $"202611{day:00}-120000";
            File.WriteAllText(Path.Combine(folder, $"Game_{stamp}.csv"), "x");
            File.WriteAllText(Path.Combine(folder, $"Game_{stamp}.lists.csv"), "x");
        }

        File.WriteAllText(Path.Combine(folder, "Game_Two_20260101-000000.csv"), "x"); // 다른 게임 (키가 앞부분만 같음)
        File.WriteAllText(Path.Combine(folder, "Game_20261101-120000.txt"), "x");
        var file = new PlaySeriesFile(folder, "Game", new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 11, 21))), new ListLog(), () => Start);

        file.AddRow("1,a");
        file.Close();

        string[] kept = [.. new DirectoryInfo(folder).GetFiles().Select(f => f.Name).Order()];
        Assert.Contains("Game_Two_20260101-000000.csv", kept);
        Assert.Contains("Game_20261101-120000.txt", kept);
        string[] game = [.. kept.Where(n => n.StartsWith("Game_2026", StringComparison.Ordinal) && n.EndsWith(".csv", StringComparison.Ordinal))];
        Assert.Equal(8, game.Select(n => n.Split('.')[0]).Distinct().Count()); // 새 것 하나와 옛것 일곱
        Assert.DoesNotContain("Game_20261101-120000.csv", game);
        Assert.DoesNotContain("Game_20261103-120000.lists.csv", game); // 목록 파일도 같이 지워짐
        Assert.Contains("Game_20261104-120000.csv", game);
        Assert.Contains("Game_20261104-120000.lists.csv", game);
        Assert.Contains(file.FileName, game);
    }

    [Fact]
    public void AFolderThatCannotBeWrittenIsReportedOnceAndTheRowsAreDropped()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine("play_series"), "this is a file, not a folder"); // 폴더를 만들 수 없음
        var log = new ListLog();
        var file = new PlaySeriesFile(dir.Combine("play_series"), "g", Start, log, () => Start);

        file.AddRow("1,a");
        file.Close();
        file.Close();

        Assert.Single(log.Lines, l => l.Contains("play series not written", StringComparison.Ordinal));
    }

    // ---- 서비스 동작 표시 ----

    [Fact]
    public void ActivityShowsWhatRanSinceTheLastReadOnceAndWhatIsStillRunningEveryTime()
    {
        var activity = new ServiceActivity();

        Assert.Equal(string.Empty, activity.Collect());
        using (activity.Enter(ServiceOp.Keep))
        {
            using (activity.Enter(ServiceOp.Keep)) // 같은 종류가 겹쳐 돌아도 하나로 표시하고 횟수를 셈
            {
            }

            using (activity.Enter(ServiceOp.StartTouch))
            {
            }

            Assert.Equal("touch+keep", activity.Collect()); // 끝난 touch 도 이 초에 돌았음
            Assert.Equal("keep", activity.Collect()); // 아직 돌고 있는 것만
        }

        Assert.Equal(string.Empty, activity.Collect()); // 끝났고 지난 호출이 이미 보여 줌
        using (activity.Enter(ServiceOp.Trim))
        {
        }

        using (activity.Enter(ServiceOp.Supplement))
        {
        }

        Assert.Equal("trim+supp", activity.Collect());
        Assert.Equal(string.Empty, activity.Collect());
    }

    // ---- 그래픽 메모리의 프로세스별 값 ----

    [Fact]
    public void GameProcessGraphicsMemoryIsTheSumOfTheCounterInstancesWhosePidIsAGamePid()
    {
        Dictionary<string, double> values = new()
        {
            ["pid_100_luid_0x00000000_0x0000fa06_phys_0"] = 2.0 * Mib,
            ["pid_101_luid_0x00000000_0x0000fa06_phys_0"] = 512.0 * Mib,
            ["pid_1000_luid_0x00000000_0x0000fa06_phys_0"] = 900.0 * Mib, // 번호가 앞부분만 같은 다른 프로세스
            ["pid_x_luid"] = 5.0 * Mib,
            ["other"] = 7.0 * Mib,
        };

        Assert.Equal(514, DiskActivity.GpuProcessMemory.SumMib(values, [100, 101, 7]));
        Assert.Equal(0, DiskActivity.GpuProcessMemory.SumMib(values, []));
    }

    // ---- 표본 확인의 위치 고르기 ----

    private static readonly ReadOp[] ProbeList = [new(@"D:\a\1.bin", 0, 10 * Mib), new(@"D:\a\2.bin", 0, 10 * Mib), new(@"D:\a\1.bin", 50 * Mib + 4096, 10 * Mib)];

    [Fact]
    public void SixteenSamplesSitInTheMiddleOfEqualSlicesOfTheWholeListAndStayInsideOneRange()
    {
        IReadOnlyList<(ReadOp Op, long Offset)> picks = KeepProbe.Pick(ProbeList);

        Assert.Equal(16, picks.Count);
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (long)((i + 0.5) * 30 * Mib / 16)), picks.Select(p => p.Offset)); // 목록 위치: 칸의 한가운데
        Assert.All(picks, p => Assert.InRange(p.Op.Length, 1, StandbyTouch.ProbeBytes));
        Assert.All(picks, p => Assert.Contains(ProbeList, r => r.Path == p.Op.Path && p.Op.Offset >= r.Offset && p.Op.Offset + p.Op.Length <= r.Offset + r.Length));
        Assert.Equal((5, 6, 5), (picks.Count(p => p.Op.Path == ProbeList[0].Path && p.Op.Offset < 10 * Mib), picks.Count(p => p.Op.Path == ProbeList[1].Path), picks.Count(p => p.Op.Offset >= 50 * Mib))); // 세 범위에 5, 6, 5 곳
        Assert.Equal(0, picks[0].Op.Offset % StandbyTouch.ProbeBytes); // 범위 시작이 64 KiB 배수이면 표본도 64 KiB 배수
    }

    [Fact]
    public void ASampleInsideAShortRangeIsCutToTheRangeAndAnEmptyListHasNoSamples()
    {
        IReadOnlyList<(ReadOp Op, long Offset)> picks = KeepProbe.Pick([new ReadOp(@"D:\a\s.bin", 100, 5000)], 2);

        Assert.Equal([new ReadOp(@"D:\a\s.bin", 100, 5000), new ReadOp(@"D:\a\s.bin", 100, 5000)], picks.Select(p => p.Op));
        Assert.Empty(KeepProbe.Pick([]));
        Assert.Empty(KeepProbe.Pick([new ReadOp(@"D:\a\z.bin", 0, 0)]));
    }

    [Fact]
    public void TheDescriptionCountsSamplesBelowTheMissThresholdAsInMemoryAndSaysHowManyWereNotProbed()
    {
        (ReadOp Op, long Offset)[] picks = [(ProbeList[0], 1 * Mib), (ProbeList[0], 211 * Mib), (ProbeList[0], 422 * Mib), (ProbeList[0], 600 * Mib), (ProbeList[0], 700 * Mib)];

        Assert.Equal("probe 2/4 in memory (positions 1 422 MiB of 3386 MiB), 1 not probed", KeepProbe.Describe(picks, [0, KeepProbe.MissFaults, 1, 7, -1], 3386 * Mib)); // 잡음 +1 은 메모리에 있음, 2 이상은 디스크
        Assert.Equal("probe 0/2 in memory (none of 100 MiB)", KeepProbe.Describe(picks[..2], [2, 3], 100 * Mib));
    }

    // ---- 설정 ----

    [Fact]
    public void PlaySeriesDefaultsToOnIsReadAndRejectsBadValues()
    {
        Assert.True(ParsePlay(null).Series);
        Assert.True(ParsePlay("{}").Series);
        Assert.False(ParsePlay("""{"series":false}""").Series);
        Assert.True(ParsePlay("""{"series":true,"frame_times":false}""").Series);
        Assert.Throws<SettingsException>(() => ParsePlay("""{"series":"yes"}"""));
    }

    private static PlaySettings ParsePlay(string? play)
    {
        var root = new JsonObject { ["games"] = new JsonArray() };
        if (play is not null)
        {
            root["play"] = JsonNode.Parse(play);
        }

        return SettingsStore.Parse(root.ToJsonString(), @"C:\ProgramData\AsterCache\plans").Play;
    }

    // ---- 서비스 연결 (가짜 프레임 세션·가짜 조회) ----

    private static PlayRecorder Recorder(TempDir dir, ListLog log, DiskCounter counter, FakeFrames frames, Func<IPlaySeriesProbe?> openSeries, Func<bool>? enabled = null, Func<GameSettings, string?>? blocker = null, Func<IPlaySystemProbe?>? openSystem = null) =>
        new(dir.Combine("play_samples.json"), log, _ => counter, () => 8192, TimeSpan.FromMilliseconds(10), openFrames: (_, _) => frames, frameBlocker: blocker ?? (_ => null), openSystem: openSystem,
            openSeries: openSeries, seriesDirectory: dir.Combine("play_series"), seriesEnabled: enabled);

    private static async Task<GameSettings> Play(PlayRecorder recorder, DiskCounter counter, string name, int pid)
    {
        GameSettings game = Games.Make(name, name + ".exe");
        Assert.True(recorder.ObserveStart(game, new ProcessStart(pid, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        for (int i = 0; i < 400 && counter.Reads <= 4; i++)
        {
            await Task.Delay(5);
        }

        Assert.True(counter.Reads > 4, "the sampling loop did not run");
        recorder.EndSession(game.Name);
        return game;
    }

    private static string[] SeriesFiles(TempDir dir, string suffix) => Directory.Exists(dir.Combine("play_series")) ? Directory.GetFiles(dir.Combine("play_series"), "*" + suffix) : [];

    [Fact]
    public async Task EverySecondOfAPlayGetsOneLineWithTheFrameSecondTheSystemValuesAndTheServiceActivityAndTheOldRecordIsUnchanged()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        using var counter = new DiskCounter();
        var frames = new FakeFrames();
        frames.Seconds.Add(new FrameSecond(0, 100, 62.5, 1, 1));
        frames.Seconds.Add(new FrameSecond(3, 55, 16.7, 0, 0)); // 표본이 없던 초 (시험은 1 초도 안 돎): 프레임 값만 있는 줄
        var probe = new FakeSeries();
        PlayRecorder recorder = Recorder(dir, log, counter, frames, () => probe, openSystem: () => new FakeSystem());

        GameSettings game = await Play(recorder, counter, "Series Game", 34);

        string[] files = SeriesFiles(dir, ".csv");
        string main = Assert.Single(files, f => !f.EndsWith(".lists.csv", StringComparison.Ordinal));
        string lists = Assert.Single(files, f => f.EndsWith(".lists.csv", StringComparison.Ordinal));
        string[] lines = Lines(main);
        Assert.Equal(PlaySeriesFormat.Header, lines[0]);
        string second0 = Assert.Single(lines, l => l.StartsWith("0,", StringComparison.Ordinal));
        Assert.Equal(("100", "62.5", "1", "1"), (Cell(second0, "frames"), Cell(second0, "max_frame_ms"), Cell(second0, "over50_n"), Cell(second0, "over100_n")));
        Assert.Equal(("50.0", "20.0", "1.0", "obs.exe", "10.0"), (Cell(second0, "cpu_sys_pct"), Cell(second0, "cpu_game_pct"), Cell(second0, "cpu_service_pct"), Cell(second0, "cpu_other1_name"), Cell(second0, "cpu_other1_pct")));
        Assert.Equal(("7000", "6128", "3000", "keep"), (Cell(second0, "avail_mib"), Cell(second0, "gpu_mib"), Cell(second0, "gpu_game_mib"), Cell(second0, "service_ops"))); // 어댑터 전체는 이미 읽는 시스템 값 (6128), 게임 몫은 조회 값
        Assert.Equal(("5", "1024", "0.10", "3", "2048", "0.10"), (Cell(second0, "disk_read_n"), Cell(second0, "disk_read_bytes"), Cell(second0, "disk_read_mean_ms"), Cell(second0, "disk_write_n"), Cell(second0, "disk_write_bytes"), Cell(second0, "disk_write_mean_ms")));
        Assert.Equal(("obs.exe", "4096"), (Cell(second0, "write_anyvol_top_name"), Cell(second0, "write_anyvol_top_bytes")));
        string second3 = Assert.Single(lines, l => l.StartsWith("3,", StringComparison.Ordinal));
        Assert.Equal(("55", "16.7", string.Empty), (Cell(second3, "frames"), Cell(second3, "max_frame_ms"), Cell(second3, "cpu_sys_pct")));
        Assert.Equal(PlaySeriesFormat.ListsHeader, Lines(lists)[0]);
        Assert.StartsWith("0,", Lines(lists)[1]); // 처음 부를 때 한 줄
        Assert.Contains(34, probe.Pids); // 서비스가 이미 아는 게임 프로세스 번호만 넘김
        Assert.True(probe.Disposed);
        Assert.Single(log.Lines, l => l.Contains("play record series: Series_Game_", StringComparison.Ordinal) && l.EndsWith(".csv", StringComparison.Ordinal));

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        JsonElement sample = json.RootElement.GetProperty("games").GetProperty(game.Name).EnumerateArray().Last();
        Assert.Equal(62.5, sample.GetProperty("frameSeconds").EnumerateArray().First().GetProperty("maxMs").GetDouble()); // 끊긴 초 목록은 그대로
        Assert.Equal("complete", sample.GetProperty("status").GetString());
        Assert.False(sample.TryGetProperty("seriesFile", out _)); // 기존 기록의 내용은 늘리지 않음
    }

    [Fact]
    public async Task AnUnreadableSystemValueOnlyEmptiesItsCellsAndTheSessionIsStillRecorded()
    {
        using var dir = new TempDir();
        using var counter = new DiskCounter();
        var frames = new FakeFrames();
        frames.Seconds.Add(new FrameSecond(0, 100, 62.5, 1, 1));
        var probe = new FakeSeries { Throw = true };
        PlayRecorder recorder = Recorder(dir, new ListLog(), counter, frames, () => probe);

        GameSettings game = await Play(recorder, counter, "throws", 35);

        string second0 = Assert.Single(Lines(Assert.Single(SeriesFiles(dir, ".csv"), f => !f.EndsWith(".lists.csv", StringComparison.Ordinal))), l => l.StartsWith("0,", StringComparison.Ordinal));
        Assert.Equal(("100", string.Empty, string.Empty), (Cell(second0, "frames"), Cell(second0, "cpu_sys_pct"), Cell(second0, "disk_read_n")));
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        Assert.Equal("complete", json.RootElement.GetProperty("games").GetProperty(game.Name).EnumerateArray().Last().GetProperty("status").GetString());
    }

    [Fact]
    public async Task AFailureWhileBuildingSecondsStopsOnlyTheSeriesAndTheRestOfThePlayRecordIsKept()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        using var counter = new DiskCounter();
        var frames = new FakeFrames();
        frames.Seconds.Add(new FrameSecond(0, 100, 62.5, 1, 1));
        var probe = new FakeSeries { BadRaw = true }; // 프로세스 목록이 null 이라 Point 가 예외를 던짐
        PlayRecorder recorder = Recorder(dir, log, counter, frames, () => probe);

        GameSettings game = await Play(recorder, counter, "badraw", 38);

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        JsonElement sample = json.RootElement.GetProperty("games").GetProperty(game.Name).EnumerateArray().Last();
        Assert.Equal("complete", sample.GetProperty("status").GetString()); // MarkUnavailable 로 끊기지 않음
        Assert.Equal(62.5, sample.GetProperty("frameSeconds").EnumerateArray().First().GetProperty("maxMs").GetDouble());
        Assert.Single(log.Lines, l => l.Contains("play series stopped (NullReferenceException)", StringComparison.Ordinal)); // 한 번만 알림
        Assert.True(counter.Reads > 4);
    }

    [Fact]
    public async Task ASwitchedOffSeriesOrAGateThatBlocksFrameTimesOpensNothingAndWritesNothing()
    {
        using var off = new TempDir();
        using var offCounter = new DiskCounter();
        int opened = 0;
        PlayRecorder disabled = Recorder(off, new ListLog(), offCounter, new FakeFrames(), () =>
        {
            opened++;
            return new FakeSeries();
        }, enabled: () => false);
        _ = await Play(disabled, offCounter, "off", 36);

        using var blocked = new TempDir();
        using var blockedCounter = new DiskCounter();
        PlayRecorder gated = Recorder(blocked, new ListLog(), blockedCounter, new FakeFrames(), () =>
        {
            opened++;
            return new FakeSeries();
        }, blocker: _ => "anti-cheat"); // 프레임 세션이 안티치트 게이트에 막힘: 초 단위 기록도 열지 않음
        _ = await Play(gated, blockedCounter, "gated", 37);

        Assert.Equal(0, opened);
        Assert.False(Directory.Exists(off.Combine("play_series")));
        Assert.False(Directory.Exists(blocked.Combine("play_series")));
    }

    [Fact]
    public async Task ThePlayStartsAsLoadingAtTheExecutableStartTheSecondsLineSaysSoAndTheStateIsForgottenWhenTheSessionEnds()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        using var counter = new DiskCounter();
        var frames = new FakeFrames();
        frames.Seconds.Add(new FrameSecond(0, 100, 62.5, 1, 1));
        PlayRecorder recorder = Recorder(dir, log, counter, frames, () => new FakeSeries());

        GameSettings game = await Play(recorder, counter, "Load Game", 40);

        string second0 = Assert.Single(Lines(Assert.Single(SeriesFiles(dir, ".csv"), f => !f.EndsWith(".lists.csv", StringComparison.Ordinal))), l => l.StartsWith("0,", StringComparison.Ordinal));
        Assert.Equal("loading", Cell(second0, "load_phase")); // 시험은 1 초도 안 돌아 조용한 15 초가 차지 않음
        Assert.Single(log.Lines, l => l.Contains("Load Game: loading phase started (executable started)", StringComparison.Ordinal));
        Assert.Contains(log.Lines, l => l.Contains("Load Game: loading phase ended after", StringComparison.Ordinal) && l.Contains("(play ended)", StringComparison.Ordinal));
        Assert.Equal(LoadState.Unknown, recorder.LoadPhases.StateOf(game.Name));
    }

    [Fact]
    public async Task TheLoadingJudgmentStillRunsWithTheSeriesSwitchedOffOrTheGateBlockingFrameTimesUsingOnlyTheDiskRule()
    {
        using var off = new TempDir();
        var offLog = new ListLog();
        using var offCounter = new DiskCounter();
        PlayRecorder disabled = Recorder(off, offLog, offCounter, new FakeFrames(), () => new FakeSeries(), enabled: () => false);
        _ = await Play(disabled, offCounter, "Off Game", 41);

        using var blocked = new TempDir();
        var blockedLog = new ListLog();
        using var blockedCounter = new DiskCounter();
        PlayRecorder gated = Recorder(blocked, blockedLog, blockedCounter, new FakeFrames(), () => new FakeSeries(), blocker: _ => "anti-cheat");
        _ = await Play(gated, blockedCounter, "Gated Game", 42);

        Assert.Single(offLog.Lines, l => l.Contains("Off Game: loading phase started (executable started)", StringComparison.Ordinal));
        Assert.Single(blockedLog.Lines, l => l.Contains("Gated Game: loading phase started (executable started)", StringComparison.Ordinal));
        Assert.False(Directory.Exists(off.Combine("play_series"))); // 기록은 없어도 판정은 돎
        Assert.False(Directory.Exists(blocked.Combine("play_series")));
    }

    // ---- 가짜 ----

    private sealed class FakeSeries : IPlaySeriesProbe
    {
        private int _reads;

        public bool Throw { get; init; }
        public bool BadRaw { get; init; }
        public bool Disposed { get; private set; }
        public List<int> Pids { get; } = [];

        // 읽을 때마다 같은 양씩 늘어 (시스템 틱 10000 중 유휴 5000, 게임 2000, 서비스 100, obs.exe 1000, obs.exe 쓰기 4096) 초마다 값이 같음
        public PlaySeriesRaw Read(IReadOnlyCollection<int> gamePids)
        {
            lock (Pids)
            {
                Pids.AddRange(gamePids);
            }

            if (Throw)
            {
                throw new InvalidOperationException("probe failed");
            }

            long n = Interlocked.Increment(ref _reads);
            if (BadRaw)
            {
                return new PlaySeriesRaw(n, n * 2, null!, Environment.ProcessId, 1, null, null, null, null, string.Empty);
            }

            return new PlaySeriesRaw(n * 5000, n * 10_000,
                [new(0, "Idle", n * 5000, 0), new(34, "Series Game.exe", n * 2000, 0), new(Environment.ProcessId, "testhost.exe", n * 100, 0), new(999, "obs.exe", n * 1000, n * 4096)],
                Environment.ProcessId, 7000, n * Mib, (uint)(n * 10), 6500, 3000, "keep");
        }

        public MemoryPriorityLists? ReadLists() => new([.. Enumerable.Range(0, 8).Select(i => (100L + i) * Mib)], [.. Enumerable.Range(0, 8).Select(i => (1000L + i) * Mib)], 30 * Mib);

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeSystem : IPlaySystemProbe
    {
        public PlaySystemSample Read() => new(12.5, 6128);

        public void Dispose()
        {
        }
    }

    private sealed class FakeFrames : IFrameSource
    {
        private readonly object _gate = new();

        public List<FrameSecond> Seconds { get; } = [];

        public long AcceptedEvents => 1;
        public long EventsLost => 0;
        public long EventsLate => 0;
        public long EventsOutOfOrder => 0;

        public void AddProcess(int pid)
        {
        }

        public IReadOnlyList<FrameSecond> TakeSettled(long throughSecond)
        {
            lock (_gate)
            {
                List<FrameSecond> taken = [.. Seconds.Where(s => s.Second <= throughSecond)];
                _ = Seconds.RemoveAll(s => s.Second <= throughSecond);
                return taken;
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class DiskCounter : IPlayDiskCounter
    {
        public string? PhysicalDisk => "disk1";
        public IReadOnlyList<PlayDiskCounters> Initial { get; } = [new(1, 0, 0, 0, 0, 0)];
        public int Reads { get; private set; }

        // 표본마다 읽기 5 건 (0.1 ms), 1024 바이트, 쓰기 3 건 (0.1 ms), 2048 바이트
        public IReadOnlyList<PlayDiskCounters>? Read()
        {
            Reads++;
            return [new PlayDiskCounters(1, Reads * 1024, (uint)(Reads * 5), Reads * 5_000L, 0, 0, Reads * 2048L, (uint)(Reads * 3), Reads * 3_000L)];
        }

        public void Dispose()
        {
        }
    }
}
