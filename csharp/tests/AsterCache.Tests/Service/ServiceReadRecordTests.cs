using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Tests.Windows;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 서비스가 게임 드라이브에서 일으킨 읽기를 초 단위 기록·스트림 종료 로그·플레이 종료 요약에 남기는 계측: 동작은 바꾸지 않고 값만 적습니다.
public sealed class ServiceReadRecordTests
{
    private const long Mib = 1L << 20;
    private const string Root = @"D:\Games\g";

    private static PlayDiskCounters Disk(long readBytes, uint count = 0, long readTime100ns = 0, uint queue = 0) => new(1, readBytes, count, readTime100ns, 0, queue);

    private static PlaySeriesPoint Point() => new(null, null, null, null, null, null, null, null, null, null, null, null, 0, string.Empty);

    private static string Cell(string line, string column) => line.Split(',')[Array.IndexOf(PlaySeriesFormat.Header.Split(','), column)];

    [Fact]
    public void TheNewColumnsAreAppendedAtTheEndAndTheOldColumnsKeepTheirPlace()
    {
        string[] names = PlaySeriesFormat.Header.Split(',');
        Assert.Equal(["yield_applied_n", "service_read_bytes", "service_stream_requested_bytes", "service_stream_accepted_bytes"], names[^4..]);

        string line = PlaySeriesFormat.Line(7, default, Point() with { Load = LoadState.Loading, ServiceReads = new ServiceReadSecond(48 * Mib, 64 * Mib, 60 * Mib) });
        Assert.Equal(names.Length, line.Split(',').Length);
        Assert.Equal(((48 * Mib).ToString(), (64 * Mib).ToString(), (60 * Mib).ToString()), (Cell(line, "service_read_bytes"), Cell(line, "service_stream_requested_bytes"), Cell(line, "service_stream_accepted_bytes")));
        Assert.Equal("loading", Cell(line, "load_phase")); // 앞 열은 그대로

        string old = string.Join(',', line.Split(',')[..^3]); // 이 열을 모르던 예전 파일의 줄: 앞 열은 같은 이름으로 읽힘
        Assert.Equal("loading", Cell(old, "load_phase"));
    }

    [Fact]
    public void ASecondWithoutServiceReadsLeavesTheThreeCellsEmptyAndASecondWithoutValuesKeepsTheCellCount()
    {
        string none = PlaySeriesFormat.Line(7, default, Point());
        Assert.Equal((string.Empty, string.Empty, string.Empty), (Cell(none, "service_read_bytes"), Cell(none, "service_stream_requested_bytes"), Cell(none, "service_stream_accepted_bytes")));

        string noValues = PlaySeriesFormat.Line(7, default, null);
        Assert.Equal(PlaySeriesFormat.Header.Split(',').Length, noValues.Split(',').Length);
    }

    [Fact]
    public void TheBuilderCarriesTheSecondsServiceReadsFromTheRawValues()
    {
        var builder = new PlaySeriesBuilder();
        var raw = new PlaySeriesRaw(null, null, [], 0, null, null, null, null, null, string.Empty, ServiceReads: new ServiceReadSecond(1, 2, 3));

        Assert.Equal(new ServiceReadSecond(1, 2, 3), builder.Next(raw, [], [], []).ServiceReads);
        Assert.Null(builder.Next(raw with { ServiceReads = null }, [], [], []).ServiceReads);
    }

    [Fact]
    public void TheFeedTurnsTheCountedTotalsIntoTheSecondsValuesAndStreamsAreNotSubtractedFromTheDriveReads()
    {
        var board = new LoadPhaseBoard(new ListLog());
        var feed = new LoadFeed("g", Root, board);

        board.ServiceReads.Add(Root, 5 * Mib, "g", ServiceReadPath.Keep);
        board.ServiceReads.Add(@"D:\Other", 2 * Mib, "g", ServiceReadPath.Hold);
        board.ServiceReads.CountsOf("g").AddStream(ServiceReadPath.StartStream, 64 * Mib, 0);
        board.ServiceReads.CountsOf("g").AddStream(ServiceReadPath.StartStream, 0, 60 * Mib);
        _ = feed.Next([Disk(0)], [Disk(200 * Mib)], 1, null, false);
        Assert.Equal(new ServiceReadSecond(7 * Mib, 64 * Mib, 60 * Mib), feed.Last);

        _ = feed.Next([Disk(200 * Mib)], [Disk(200 * Mib)], 2, null, false);
        Assert.Equal(new ServiceReadSecond(0, 0, 0), feed.Last); // 누계가 늘지 않은 초

        Assert.Equal(7 * Mib, board.ServiceReads.Total(Root)); // 로딩 판정이 빼는 누계는 스트림을 포함하지 않음 (예전 동작 그대로)
    }

    [Fact]
    public void ThePlaySummaryDividesEachPathsReadsByTheLoadJudgmentOfTheSecondTheyWereCountedIn()
    {
        var board = new LoadPhaseBoard(new ListLog());
        var feed = new LoadFeed("g", Root, board);

        board.ServiceReads.Add(Root, 3 * Mib, "g", ServiceReadPath.Hold);
        Assert.NotEqual(LoadState.Loading, feed.Next([Disk(0)], [Disk(Mib)], 1, null, false)); // 로딩 밖의 초
        board.ServiceReads.Add(Root, 5 * Mib, "g", ServiceReadPath.Keep);
        board.ServiceReads.CountsOf("g").AddStream(ServiceReadPath.StartStream, 64 * Mib, 60 * Mib);
        Assert.Equal(LoadState.Loading, feed.Next([Disk(Mib)], [Disk(300 * Mib)], 2, null, false)); // 로딩 안의 초

        string summary = feed.Summary();

        Assert.StartsWith("g: service reads on the game drive in play (counted MiB, not measured), loading / outside loading: start stream 60.0/0.0, stage stream 0.0/0.0, hold 0.0/3.0, keep rounds 5.0/0.0, check 0.0/0.0, sparse keep 0.0/0.0, start touch 0.0/0.0", summary);
        Assert.DoesNotContain("other", summary, StringComparison.Ordinal); // 센 것이 없는 other 는 적지 않음
    }

    [Fact]
    public void TheDiskSamplesTheLoopAlreadyReadsAreTalliedForTheStreamEndLine()
    {
        var board = new LoadPhaseBoard(new ListLog());
        var feed = new LoadFeed("g", Root, board);
        Assert.Equal(default, board.TallyOf("g"));

        _ = feed.Next([Disk(0)], [Disk(Mib, 10, 500_000, 2)], 1, null, false); // 읽기 10 회, 합 50 ms, 대기열 2
        DiskTally before = board.TallyOf("g");
        _ = feed.Next([Disk(Mib, 10, 500_000)], [Disk(2 * Mib, 20, 1_500_000, 3)], 2, null, false); // 읽기 10 회, 합 100 ms, 대기열 3
        _ = feed.Next([Disk(2 * Mib, 20, 1_500_000, 3)], [Disk(2 * Mib, 20, 1_500_000, 0)], 3, null, false); // 읽기 없음, 대기열 0
        DiskTally during = board.TallyOf("g").Since(before);

        Assert.Equal((2L, 1L, 10UL), (during.Samples, during.Queued, during.Reads));
        Assert.Equal("disk queue present in 50% of 2 samples, mean read latency 10.0 ms", during.Text());
        Assert.Equal("disk samples none", default(DiskTally).Text());
        Assert.Equal("disk queue present in 100% of 1 samples, mean read latency n/a", new DiskTally(1, 1, 0, 0).Text());

        board.Forget("g");
        Assert.Equal(default, board.TallyOf("g").Since(before)); // 상태가 지워졌다 다시 생겨 누계가 줄었으면 모름
    }

    [Fact]
    public void TheStreamEndLineNamesTheKindAndTheCountedValues()
    {
        string line = GameDispatcher.StreamReadsLine("g", ServiceReadPath.StageStream, 4, 250 * Mib, 240 * Mib, 6 * Mib, 122.4, 120.0, new DiskTally(113, 113, 100, 4000));

        Assert.Equal("g: stage stream reads: 4 batches, requested 250.0 MiB, accepted 240.0 MiB, sparse buffered 6.0 MiB in 122.4 s, low I/O priority 120.0 s; disk queue present in 100% of 113 samples, mean read latency 40.0 ms; memory headroom held 0 batches 0.0 s, min none", line);
        Assert.StartsWith("g: start stream reads: ", GameDispatcher.StreamReadsLine("g", ServiceReadPath.StartStream, 0, 0, 0, 0, 0, 0, default), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStreamEndLineAddsTheHeldBatchesAndTheMinimumHeadroom()
    {
        var headroom = new StreamHeadroom(() => new MemoryLists(500 * Mib, 0), () => 0);
        Assert.True(headroom.Short(300 * Mib)); // 500 < 300 + 256: 내지 못함
        headroom.NoteHeld(2.5);

        string line = GameDispatcher.StreamReadsLine("g", ServiceReadPath.StartStream, 0, 0, 0, 0, 0, 0, default, headroom);

        Assert.EndsWith("; disk samples none; memory headroom held 1 batches 2.5 s, min 500 MiB", line, StringComparison.Ordinal);
    }

    [Fact]
    public void AStartStreamCountsEachBatchRequestAndAcceptanceAndLogsOneEndLineWithoutChangingWhatItReads()
    {
        GameSettings game = Games.Make("game", "game.exe");
        var log = new ListLog();
        var board = new LoadPhaseBoard(log);
        var streamer = new BatchStreamer();
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), log);
        var dispatcher = new GameDispatcher([game], streamer, log, coordinator: coordinator) { LoadPhases = board };

        Assert.True(dispatcher.Handle(new ProcessStart(42, "game.exe", 0)));

        Assert.Equal(3, streamer.Batches); // 읽는 일은 그대로
        ServiceReadCounts counts = board.ServiceReads.CountsOf("game");
        Assert.Equal((192 * Mib, 192 * Mib, 0L, 4 * Mib), (counts.Requested(ServiceReadPath.StartStream), counts.Accepted(ServiceReadPath.StartStream), counts.Requested(ServiceReadPath.StageStream), counts.Disk(ServiceReadPath.StartStream)));
        Assert.Equal(0, board.ServiceReads.Total(game.OriginRoot)); // 로딩 판정이 빼는 드라이브 누계에는 들지 않음
        Assert.Single(log.Lines, l => l.StartsWith("Info: game: start stream reads: 3 batches, requested 192.0 MiB, accepted 192.0 MiB, sparse buffered 4.0 MiB in ", StringComparison.Ordinal) && l.Contains("; disk samples none; memory headroom held 0 batches 0.0 s, min ", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutALoadBoardTheStreamCountsAndLogsNothingNew()
    {
        GameSettings game = Games.Make("game", "game.exe");
        var log = new ListLog();
        var streamer = new BatchStreamer();
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), log);
        var dispatcher = new GameDispatcher([game], streamer, log, coordinator: coordinator);

        Assert.True(dispatcher.Handle(new ProcessStart(42, "game.exe", 0)));

        Assert.Equal(3, streamer.Batches);
        Assert.DoesNotContain(log.Lines, l => l.Contains("stream reads", StringComparison.Ordinal));
    }

    [Fact]
    public void TwoGamesOnTheSameVolumeKeepTheirOwnCountsWhileTheDriveTotalStaysOneNumber()
    {
        var board = new LoadPhaseBoard(new ListLog());
        var feed1 = new LoadFeed("g1", Root, board);
        var feed2 = new LoadFeed("g2", Root, board);

        board.ServiceReads.Add(Root, 5 * Mib, "g1", ServiceReadPath.Keep);
        board.ServiceReads.Add(Root, 3 * Mib, "g2", ServiceReadPath.Hold);
        board.ServiceReads.CountsOf("g2").AddStream(ServiceReadPath.StageStream, 64 * Mib, 60 * Mib);
        _ = feed1.Next([Disk(0)], [Disk(Mib)], 1, null, false);
        _ = feed2.Next([Disk(0)], [Disk(Mib)], 1, null, false);

        Assert.Equal(new ServiceReadSecond(5 * Mib, 0, 0), feed1.Last);
        Assert.Equal(new ServiceReadSecond(3 * Mib, 64 * Mib, 60 * Mib), feed2.Last);
        Assert.Contains("keep rounds 0.0/5.0", feed1.Summary(), StringComparison.Ordinal);
        Assert.DoesNotContain("hold 0.0/3.0", feed1.Summary(), StringComparison.Ordinal);
        Assert.Equal(8 * Mib, board.ServiceReads.Total(Root)); // 로딩 판정이 빼는 값은 드라이브의 단일 누계
    }

    [Fact]
    public void TheCountsAreReadAtTheDiskSampleBoundaryAndALaterReadBelongsToTheNextSecond()
    {
        var board = new LoadPhaseBoard(new ListLog());
        var feed = new LoadFeed("g", Root, board);

        board.ServiceReads.Add(Root, 2 * Mib, "g", ServiceReadPath.Keep);
        feed.Mark(); // 디스크를 읽은 직후
        board.ServiceReads.Add(Root, 6 * Mib, "g", ServiceReadPath.Keep); // 그 뒤에 생긴 읽기
        _ = feed.Next([Disk(0)], [Disk(Mib)], 1, null, false);
        Assert.Equal(2 * Mib, feed.Last!.Value.ServiceBytes);

        _ = feed.Next([Disk(Mib)], [Disk(Mib)], 2, null, false);
        Assert.Equal(6 * Mib, feed.Last!.Value.ServiceBytes);
    }

    [Fact]
    public void AStreamBatchThatCrossesSecondsPutsTheRequestInTheSecondItWasIssuedAndEachFragmentsAcceptanceInTheSecondItEnded()
    {
        GameSettings game = Games.Make("game", "game.exe");
        var log = new ListLog();
        var board = new LoadPhaseBoard(log);
        var feed = new LoadFeed("game", game.OriginRoot, board);
        var seconds = new List<ServiceReadSecond?>();
        int second = 0;
        var streamer = new FragmentStreamer(() =>
        {
            feed.Mark();
            _ = feed.Next([Disk(second * Mib)], [Disk(++second * Mib)], second, null, false);
            seconds.Add(feed.Last);
        });
        var dispatcher = new GameDispatcher([game], streamer, log, coordinator: new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), log)) { LoadPhases = board };

        Assert.True(dispatcher.Handle(new ProcessStart(42, "game.exe", 0)));

        Assert.Equal([new ServiceReadSecond(0, 24 * Mib, 8 * Mib), new ServiceReadSecond(0, 0, 8 * Mib), new ServiceReadSecond(0, 0, 8 * Mib)], seconds); // 요청은 낸 초에 한꺼번에, 받아들인 양은 조각마다
    }

    [Fact]
    public void TheFragmentNoteParserAgreesWithTheRealPvmNotesAndARejectedFragmentAddsNothing()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", (int)(20 * Mib));
        var notes = new List<string>();
        int calls = 0;

        _ = ZoneStreamer.RunPvm([new(tmp.Combine("f.bin"), 0, 20 * Mib)], CancellationToken.None, out _, out long hint, _ => ++calls != 2, (stage, _, _) => notes.Add(stage)); // 두 번째 조각 (8 MiB) 은 거절됨

        long baseline = 0;
        long accepted = 0;
        foreach (string note in notes)
        {
            Assert.True(ZoneStreamer.TryFragmentProgress(note, out bool completed, out long progress));
            accepted += completed ? progress - baseline : 0;
            baseline = progress;
        }

        Assert.Equal(12 * Mib, hint);
        Assert.Equal(hint, accepted);
        Assert.False(ZoneStreamer.TryFragmentProgress(PrefetchStreamer.BatchStage, out _, out _));
        Assert.False(ZoneStreamer.TryFragmentProgress(ZoneStreamer.SparseBufferedStage, out _, out _));
    }

    [Fact]
    public void ThePlaySummaryStopsAtTheSecondTheGameProcessWasFirstGoneAndResumesIfItCameBack()
    {
        var board = new LoadPhaseBoard(new ListLog());
        var feed = new LoadFeed("g", Root, board);

        board.ServiceReads.Add(Root, 2 * Mib, "g", ServiceReadPath.Keep);
        _ = feed.Next([Disk(0)], [Disk(Mib)], 1, null, false);
        feed.GoneSeconds = 1.5; // 사라진 시각 (유예 중)
        board.ServiceReads.Add(Root, 7 * Mib, "g", ServiceReadPath.Keep);
        _ = feed.Next([Disk(Mib)], [Disk(Mib)], 2, null, false); // 사라진 시각 뒤의 초
        Assert.Contains("keep rounds 0.0/2.0", feed.Summary(), StringComparison.Ordinal);

        feed.GoneSeconds = null; // 유예에서 돌아옴
        _ = feed.Next([Disk(Mib)], [Disk(Mib)], 3, null, false);
        Assert.Contains("keep rounds 0.0/9.0", feed.Summary(), StringComparison.Ordinal);
    }

    [Fact]
    public void ADiskTallyFromAnEarlierGameStateIsDroppedWhenTheStateWasReplaced()
    {
        var board = new LoadPhaseBoard(new ListLog());
        var feed = new LoadFeed("g", Root, board);
        _ = feed.Next([Disk(0)], [Disk(Mib, 1, 1000, 1)], 1, null, false);
        DiskTally before = board.TallyOf("g");

        board.Forget("g");
        var replaced = new LoadFeed("g", Root, board);
        _ = replaced.Next([Disk(0)], [Disk(Mib, 1, 1000, 1)], 1, null, false);
        _ = replaced.Next([Disk(Mib, 1, 1000)], [Disk(2 * Mib, 2, 2000, 1)], 2, null, false); // 누계가 앞의 값보다 커져도 다른 상태의 값

        Assert.Equal(default, board.TallyOf("g").Since(before));
    }

    // 묶음 하나 (24 MiB) 를 8 MiB 조각 셋으로 읽고, 조각이 끝날 때마다 한 초가 지나게 하는 스트리머
    private sealed class FragmentStreamer(Action onFragmentEnd) : IGameStreamer
    {
        public StreamStats Stream(GameSettings game) => throw new InvalidOperationException();

        public StreamStats Stream(GameSettings game, CancellationToken stop) => throw new InvalidOperationException();

        public (StreamStats Stats, bool Complete) StreamCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue)
        {
            if (!canIssue())
            {
                return (new StreamStats(0, 0, 0, 0, Aborted: true), false);
            }

            noteIssue?.Invoke(PrefetchStreamer.BatchStage, 24 * Mib, true);
            for (int i = 1; i <= 3; i++)
            {
                noteIssue?.Invoke($"pvm fragment {i}/3 started, progress {(i - 1) * 8 * Mib}/{24 * Mib} bytes", 8 * Mib, true);
                noteIssue?.Invoke($"pvm fragment {i}/3 completed, progress {i * 8 * Mib}/{24 * Mib} bytes", 8 * Mib, true);
                onFragmentEnd();
            }

            progress?.Invoke(24 * Mib, 24 * Mib);
            return (new StreamStats(24 * Mib, 3, 0, 0), true);
        }
    }

    // 묶음 3 개 (64 MiB 씩) 를 요청 (noteIssue) 하고 모두 받아들이는 스트리머
    private sealed class BatchStreamer : IGameStreamer
    {
        public int Batches { get; private set; }

        public StreamStats Stream(GameSettings game) => throw new InvalidOperationException();

        public StreamStats Stream(GameSettings game, CancellationToken stop) => Stream(game, stop, null, () => true, null).Stats;

        public (StreamStats Stats, bool Complete) StreamCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue) =>
            Stream(game, stop, progress, canIssue, noteIssue);

        private (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue)
        {
            while (Batches < 3)
            {
                if (stop.IsCancellationRequested || !canIssue())
                {
                    return (new StreamStats(Batches * 64 * Mib, Batches, 0, 0, Aborted: true), false);
                }

                noteIssue?.Invoke(PrefetchStreamer.BatchStage, 64 * Mib, true);
                Batches++;
                if (Batches == 1)
                {
                    noteIssue?.Invoke(ZoneStreamer.SparseBufferedStage, 4 * Mib, true); // 스파스 구간은 선읽기가 아니라 버퍼드로 읽음
                }

                noteIssue?.Invoke($"pvm fragment 1/1 started, progress 0/{64 * Mib} bytes", 64 * Mib, true);
                noteIssue?.Invoke($"pvm fragment 1/1 completed, progress {64 * Mib}/{64 * Mib} bytes", 64 * Mib, true);
                progress?.Invoke(Batches * 64 * Mib, 3 * 64 * Mib);
            }

            return (new StreamStats(Batches * 64 * Mib, Batches, 0, 0), true);
        }
    }
}
