using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 프레임 간격 측정: 초 통 집계 (이벤트를 흉내 낸 입력), 플레이 기록에 더해지는 값, 세션을 열고 말고의 결정 (안티치트 게이트·설정·실패).
public sealed class FrameTimeTests
{
    private const long Freq = 10_000_000; // 시험은 초당 1천만 틱으로 가정 (Stopwatch 와 같은 단위)
    private const long Ms = Freq / 1000;

    // ---- 초 통 집계 ----

    [Fact]
    public void BucketsCountFramesLongestGapAndThresholdCrossingsPerSecond()
    {
        var buckets = new FrameBuckets(0, Freq);
        buckets.AddProcess(7);
        long t = 1000 * Ms;
        foreach (long gap in new long[] { 10, 10, 60, 10, 120, 10 }) // 초 1 안에서 60 ms 와 120 ms 간격
        {
            t += gap * Ms;
            Assert.True(buckets.Add(7, t));
        }

        FrameSecond second = Assert.Single(buckets.Take(5));
        Assert.Equal(1, second.Second);
        Assert.Equal(6, second.Frames);
        Assert.Equal(120, second.MaxMs, 3);
        Assert.Equal(2, second.Over50Ms); // 60 ms, 120 ms
        Assert.Equal(1, second.Over100Ms);
        Assert.Empty(buckets.Take(5)); // 한 번 가져간 초는 다시 나오지 않음
    }

    [Fact]
    public void OnlySettledSecondsAreTakenAndEventTimeNotArrivalTimePicksTheSecond()
    {
        var buckets = new FrameBuckets(0, Freq);
        buckets.AddProcess(7);
        Assert.True(buckets.Add(7, 1000 * Ms + 5 * Ms)); // 초 1
        Assert.True(buckets.Add(7, 2000 * Ms + 5 * Ms)); // 초 2 가 먼저 도착
        Assert.True(buckets.Add(7, 1000 * Ms + 30 * Ms)); // 초 1 이 늦게 도착: 제 초에 들어감

        FrameSecond one = Assert.Single(buckets.Take(1)); // 초 2 는 아직 확정 전
        Assert.Equal((1L, 2), (one.Second, one.Frames));
        Assert.Equal(2, Assert.Single(buckets.Take(2)).Second);
        Assert.Equal(0, buckets.Late);
    }

    [Fact]
    public void IntervalsFollowEventTimeOrderNotArrivalOrderAndTheReorderingIsCounted()
    {
        var buckets = new FrameBuckets(0, Freq);
        buckets.AddProcess(7);
        long baseTick = 3000 * Ms;
        Assert.True(buckets.Add(7, baseTick + 10 * Ms));
        Assert.True(buckets.Add(7, baseTick + 30 * Ms));
        Assert.True(buckets.Add(7, baseTick + 20 * Ms)); // 순서가 뒤바뀌어 도착
        Assert.True(buckets.Add(7, baseTick + 40 * Ms));

        FrameSecond second = Assert.Single(buckets.Take(10));
        Assert.Equal(4, second.Frames);
        Assert.Equal(10, second.MaxMs, 3); // 실제 시각 10, 20, 30, 40 ms: 도착 순서로 셌다면 20 ms 가 나옴
        Assert.Equal(1, buckets.OutOfOrder);
    }

    [Fact]
    public void AReorderedEventThatWouldInflateAThirtyFpsGapDoesNotCreateAnOver50Gap()
    {
        var buckets = new FrameBuckets(0, Freq);
        buckets.AddProcess(7);
        long baseTick = 3000 * Ms;
        foreach (long at in new long[] { 0, 66, 33, 99 }) // 30 fps (33 ms 간격), 한 칸이 뒤바뀌어 도착
        {
            Assert.True(buckets.Add(7, baseTick + at * Ms));
        }

        FrameSecond second = Assert.Single(buckets.Take(10));
        Assert.Equal(33, second.MaxMs, 3);
        Assert.Equal(0, second.Over50Ms);
    }

    [Fact]
    public void OtherProcessesAreDroppedAndALaterAddedProcessCountsFromThenOn()
    {
        var buckets = new FrameBuckets(0, Freq);
        buckets.AddProcess(7);
        Assert.False(buckets.Add(99, 1000 * Ms)); // 게임이 아닌 프로세스 (런처 화면, 오버레이)
        Assert.True(buckets.Add(7, 1010 * Ms));
        buckets.AddProcess(8); // 같은 게임으로 붙는 후속 실행 파일
        buckets.AddProcess(8);
        Assert.True(buckets.Add(8, 1020 * Ms));
        Assert.True(buckets.Add(8, 1040 * Ms));

        Assert.Equal(3, buckets.Accepted);
        FrameSecond second = Assert.Single(buckets.Take(5));
        Assert.Equal(3, second.Frames);
        Assert.Equal(20, second.MaxMs, 3); // 프로세스마다 직전 Present 와의 간격: 7 번은 첫 이벤트라 간격 없음, 8 번의 20 ms
    }

    [Fact]
    public void AnEventForAnAlreadyTakenSecondIsCountedLateAndDropped()
    {
        var buckets = new FrameBuckets(0, Freq);
        buckets.AddProcess(7);
        Assert.True(buckets.Add(7, 1000 * Ms));
        _ = buckets.Take(3);

        Assert.True(buckets.Add(7, 1500 * Ms)); // 초 1 은 이미 확정됨
        Assert.Equal(1, buckets.Late);
        Assert.Empty(buckets.Take(3));
    }

    [Fact]
    public void AGapOfOneSecondOrMoreIsAPauseCountedApartFromStutter()
    {
        var buckets = new FrameBuckets(0, Freq);
        buckets.AddProcess(7);
        Assert.True(buckets.Add(7, 1000 * Ms));
        Assert.True(buckets.Add(7, 1000 * Ms + 3 * Freq)); // 3 초 멈춤 (창을 가림, 로딩)
        Assert.True(buckets.Add(7, 1000 * Ms + 3 * Freq + 16 * Ms));
        Assert.True(buckets.Add(7, 1000 * Ms + 3 * Freq + 16 * Ms + 999 * Ms)); // 999 ms 는 멈춤이 아니라 끊김

        List<FrameSecond> taken = buckets.Take(20);
        FrameSecond paused = Assert.Single(taken, s => s.Second == 4);
        Assert.Equal((2, 1, 3000.0), (paused.Frames, paused.Pauses, paused.PauseMaxMs));
        Assert.Equal(16, paused.MaxMs, 3);
        Assert.Equal(0, paused.Over100Ms);
        FrameSecond after = Assert.Single(taken, s => s.Second == 5);
        Assert.Equal((999.0, 1, 0), (after.MaxMs, after.Over100Ms, after.Pauses));
    }

    [Fact]
    public void ABucketStartedLateDoesNotCountTheEarlierSecondsAsLateAndHasNoFirstInterval()
    {
        var buckets = new FrameBuckets(0, Freq, 100); // 시작한 지 100 초 뒤에 (다시) 연 세션
        buckets.AddProcess(7);
        Assert.True(buckets.Add(7, 100 * Freq + 2000000)); // 다시 연 직후의 첫 이벤트
        Assert.True(buckets.Add(7, 100 * Freq + 2000000 + 16 * Ms));
        Assert.True(buckets.Add(7, 50 * Freq)); // 닫혀 있던 때의 시각: 늦은 이벤트

        FrameSecond second = Assert.Single(buckets.Take(110));
        Assert.Equal((2, 16.0, 0), (second.Frames, second.MaxMs, second.Pauses)); // 닫혀 있던 시간이 간격·멈춤으로 세어지지 않음
        Assert.Equal(1, buckets.Late);
    }

    [Fact]
    public void AnIntervalAcrossATakeBoundaryIsStillCounted()
    {
        var buckets = new FrameBuckets(0, Freq);
        buckets.AddProcess(7);
        Assert.True(buckets.Add(7, 1900 * Ms));
        _ = buckets.Take(1);
        Assert.True(buckets.Add(7, 2100 * Ms)); // 직전 확정의 마지막 이벤트와 200 ms

        FrameSecond second = Assert.Single(buckets.Take(2));
        Assert.Equal((2L, 200.0, 1), (second.Second, second.MaxMs, second.Over100Ms));
    }

    [Fact]
    public void AddingEventsAllocatesNothing()
    {
        var buckets = new FrameBuckets(0, Freq);
        buckets.AddProcess(7);
        for (int i = 0; i < 1000; i++)
        {
            _ = buckets.Add(7, i * Ms);
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1000; i < 21_000; i++) // 초 통 고리 (64 초) 안에 머무는 20 초
        {
            _ = buckets.Add(7, i * Ms);
            _ = buckets.Add(99, i * Ms);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---- 누계와 구간 ----

    [Fact]
    public void CheckpointsCarryCumulativeFramesAndEachPeriodKeepsItsOwnLongestGap()
    {
        var accumulator = new PlayRecorder.PlayRecordAccumulator([1, 2], 1);
        PlayDiskCounters[] previous = [new(1, 0, 0, 0, 0, 0)];
        PlayDiskCounters[] one = [new(1, 10, 1, 10, 0, 0)];
        PlayDiskCounters[] two = [new(1, 20, 2, 20, 0, 0)];

        IReadOnlyList<PlayWindowSummary> first = accumulator.Add(previous, one, 0, 1, [new FrameSecond(0, 100, 90, 2, 0)]);
        IReadOnlyList<PlayWindowSummary> second = accumulator.Add(one, two, 1, 2, [new FrameSecond(1, 120, 40, 0, 0)]);

        PlayWindowSummary a = Assert.Single(first);
        PlayWindowSummary b = Assert.Single(second);
        Assert.Equal((100L, 90.0, 90.0, 2), (a.FrameCount, a.MaxFrameMs, a.PeriodMaxFrameMs, a.Over50Ms));
        Assert.Equal((220L, 90.0, 40.0, 2), (b.FrameCount, b.MaxFrameMs, b.PeriodMaxFrameMs, b.Over50Ms)); // 둘째 구간의 가장 긴 간격은 40 ms
    }

    [Fact]
    public void PeriodsTakeFrameCountsFromDifferencesAndTheLongestGapFromTheirOwnWindow()
    {
        PlayWindowSummary Cumulative(int? seconds, double measuredAt, long frames, double max, double periodMax, int over50, int over100, int pauses, double maxPause, double periodMaxPause) =>
            new(seconds, measuredAt, 0, 0, 0, 0, 0, (int)measuredAt, 0, false, false, 0, 0, frames, max, periodMax, over50, over100, pauses, maxPause, periodMaxPause);

        List<PlayPeriodSummary> periods = PlayPeriods.Build(
            [Cumulative(600, 600, 60_000, 90, 90, 10, 2, 1, 1500, 1500), Cumulative(1200, 1200, 130_000, 90, 45, 14, 2, 4, 2500, 2500)],
            Cumulative(null, 1500, 160_000, 90, 70, 20, 3, 5, 2500, 1100));

        Assert.Equal([(70_000L, 45.0, 4, 0, 3, 2500.0), (30_000L, 70.0, 6, 1, 1, 1100.0)], periods.Select(p => (p.FrameCount, p.MaxFrameMs, p.Over50Ms, p.Over100Ms, p.Pauses, p.MaxPauseMs)));
        Assert.EndsWith(", frames 70000, longest 45 ms, over 50 ms 4, over 100 ms 0, pauses 3 (longest 2500 ms); 20-25 min 0 MiB/0 reads/0.0 s read time/0 slow samples, frames 30000, longest 70 ms, over 50 ms 6, over 100 ms 1, pauses 1 (longest 1100 ms)",
            PlayPeriods.Format(periods, "ok"), StringComparison.Ordinal);
        Assert.EndsWith("slow samples, frames blocked; 20-25 min 0 MiB/0 reads/0.0 s read time/0 slow samples, frames blocked", PlayPeriods.Format(periods, "blocked"), StringComparison.Ordinal);
        Assert.Contains("slow samples, frames 70000, longest 45 ms", PlayPeriods.Format(periods, "blocked", 1250), StringComparison.Ordinal); // 도중에 멈췄으면 그 안에 끝난 구간은 잰 값
        Assert.EndsWith("slow samples, frames blocked", PlayPeriods.Format(periods, "blocked", 1250), StringComparison.Ordinal);
        Assert.DoesNotContain("frames", PlayPeriods.Format(periods), StringComparison.Ordinal);
    }

    // ---- 플레이 기록에 얹힌 모습 ----

    [Fact]
    public async Task RecordKeepsFrameTotalsStutterSecondsWithDiskValuesAndTheLostEventCount()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        using var counter = new DiskCounter();
        var source = new FakeFrames { Lost = 7, Late = 4, OutOfOrder = 2 };
        source.Seconds.Add(new FrameSecond(0, 100, 120, 3, 1));
        source.Seconds.Add(new FrameSecond(7, 10, 80, 1, 0)); // 그 초에는 디스크 표본이 없음
        source.Seconds.Add(new FrameSecond(9, 5, 0, 0, 0, 2, 3000)); // 멈춤만 있던 초
        source.Seconds.Add(new FrameSecond(1, 140, 20, 0, 0)); // 끊김 아님: 목록에 들지 않음 (디스크도 그 초는 표본 없음)
        var recorder = Recorder(dir, log, counter, source, checkpoints: [1]);
        GameSettings game = Games.Make("frames", "frames.exe", "second.exe");
        ProcessStart first = new(21, game.ExeNames[0], Stopwatch.GetTimestamp());

        Assert.True(recorder.ObserveStart(game, first, null));
        Assert.True(recorder.ObserveStart(game, new ProcessStart(22, game.ExeNames[1], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => counter.Reads > 3);
        recorder.EndSession(game.Name);

        JsonElement sample = ReadLastSample(dir, game.Name);
        Assert.Equal("ok", sample.GetProperty("frameStatus").GetString());
        Assert.Equal(7, sample.GetProperty("frameEventsLost").GetInt64());
        Assert.Equal([21, 22], source.Processes);
        JsonElement total = sample.GetProperty("total");
        Assert.Equal(255, total.GetProperty("frameCount").GetInt64());
        Assert.Equal(2, total.GetProperty("pauses").GetInt32());
        Assert.Equal(4, sample.GetProperty("frameEventsLate").GetInt64());
        Assert.Equal(2, sample.GetProperty("frameEventsOutOfOrder").GetInt64());
        JsonElement[] listed = [.. sample.GetProperty("frameSeconds").EnumerateArray()];
        Assert.Equal(JsonValueKind.Null, listed.Single(e => e.GetProperty("t").GetInt32() == 7).GetProperty("diskReads").ValueKind); // 디스크 표본이 없던 초는 읽기 0 건과 구별
        JsonElement paused = listed.Single(e => e.GetProperty("t").GetInt32() == 9);
        Assert.Equal((2, 3000.0), (paused.GetProperty("pauses").GetInt32(), paused.GetProperty("pauseMaxMs").GetDouble()));
        Assert.Equal(120, total.GetProperty("maxFrameMs").GetDouble());
        Assert.Equal(4, total.GetProperty("over50Ms").GetInt32());
        JsonElement stutter = Assert.Single(sample.GetProperty("frameSeconds").EnumerateArray(), e => e.GetProperty("t").GetInt32() == 0); // 느린 디스크 초는 프레임이 없어도 담김
        Assert.Equal(0, stutter.GetProperty("t").GetInt32());
        Assert.Equal(120, stutter.GetProperty("maxMs").GetDouble());
        Assert.Equal(100, stutter.GetProperty("frames").GetInt32());
        Assert.Equal(5, stutter.GetProperty("diskReads").GetInt32()); // 같은 초의 디스크 읽기 (표본마다 5 건, 100 ms)
        Assert.Equal(100, stutter.GetProperty("diskMeanReadMs").GetDouble(), 3);
        Assert.Equal(12.5, stutter.GetProperty("cpuPercent").GetDouble());
        Assert.Equal(6128, stutter.GetProperty("gpuDedicatedMib").GetInt32());
        Assert.Contains(log.Lines, line => line.Contains("play record:", StringComparison.Ordinal)
            && line.Contains("frames 255, longest 120 ms, over 50 ms 4, over 100 ms 1, pauses 2", StringComparison.Ordinal) && line.Contains("frame events lost 7, late 4, out of order 2", StringComparison.Ordinal));
        Assert.True(source.Disposed);
    }

    [Fact]
    public async Task OnlyAFlaggedSecondOrADiskSlowSecondEntersTheListAndTheListKeepsTheLongestGaps()
    {
        using var dir = new TempDir();
        using var counter = new DiskCounter { Slow = false };
        var source = new FakeFrames();
        for (int i = 0; i < 400; i++)
        {
            source.Seconds.Add(new FrameSecond(i, 100, 50 + i, 1, 0)); // 가장 긴 간격이 50..449 ms
        }

        source.Seconds.Add(new FrameSecond(500, 100, 49.9, 0, 0)); // 50 ms 미만이고 디스크도 느리지 않음
        var recorder = Recorder(dir, new ListLog(), counter, source);
        GameSettings game = Games.Make("cap", "cap.exe");

        Assert.True(recorder.ObserveStart(game, new ProcessStart(31, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => counter.Reads > 2);
        recorder.EndSession(game.Name);

        JsonElement[] list = [.. ReadLastSample(dir, game.Name).GetProperty("frameSeconds").EnumerateArray()];
        Assert.Equal(PlayRecorder.MaxFrameSecondsPerPeriod, list.Length); // 한 10 분 구간 안에서는 구간 상한
        Assert.Equal(400, list.Min(e => e.GetProperty("maxMs").GetDouble())); // 가장 긴 간격이 작은 것부터 빠짐
        Assert.Equal(449, list.Max(e => e.GetProperty("maxMs").GetDouble()));
        Assert.DoesNotContain(list, e => e.GetProperty("t").GetInt32() == 500);
    }

    [Fact]
    public async Task ALongPauseInPlayIsInTheTotalsTheLogAndSurvivesTheListLimit()
    {
        using var dir = new TempDir();
        using var counter = new DiskCounter { Slow = false };
        var log = new ListLog();
        var source = new FakeFrames();
        for (int i = 0; i < 60; i++)
        {
            source.Seconds.Add(new FrameSecond(i, 100, 60 + i, 1, 0)); // 구간 상한 (50) 을 넘기는 끊김 60 칸
        }

        source.Seconds.Add(new FrameSecond(100, 100, 0, 0, 0, 1, 1500)); // 경기 중 1.5 초 멈춤 (1 초 미만 간격은 없음)
        var recorder = Recorder(dir, log, counter, source);
        GameSettings game = Games.Make("longpause", "longpause.exe");
        Assert.True(recorder.ObserveStart(game, new ProcessStart(34, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => counter.Reads > 2);
        recorder.EndSession(game.Name);

        JsonElement sample = ReadLastSample(dir, game.Name);
        JsonElement[] list = [.. sample.GetProperty("frameSeconds").EnumerateArray()];
        Assert.Equal(PlayRecorder.MaxFrameSecondsPerPeriod, list.Length);
        Assert.Contains(list, e => e.GetProperty("t").GetInt32() == 100 && e.GetProperty("pauseMaxMs").GetDouble() == 1500); // 가장 긴 간격 0 이어도 길이 기준으로 남음
        JsonElement total = sample.GetProperty("total");
        Assert.Equal((1, 1500.0), (total.GetProperty("pauses").GetInt32(), total.GetProperty("maxPauseMs").GetDouble()));
        Assert.Contains(log.Lines, line => line.Contains("play record:", StringComparison.Ordinal) && line.Contains("pauses 1 (longest 1500 ms)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReopenThatLosesTheRaceWithTheEndOfTheSessionClosesTheNewEtwSessionAtOnce()
    {
        using var dir = new TempDir();
        using var counter = new DiskCounter();
        using var inOpen = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var source = new FakeFrames();
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), new ListLog(), _ => counter, () => 8192, TimeSpan.FromMilliseconds(10),
            openFrames: (_, _) =>
            {
                inOpen.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
                return source;
            }, frameBlocker: _ => null);
        GameSettings game = Games.Make("race", "race.exe");
        Assert.True(recorder.ObserveStart(game, new ProcessStart(97, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        Assert.True(inOpen.Wait(TimeSpan.FromSeconds(10))); // 표본 작업이 프레임 세션을 여는 중
        Task end = Task.Run(() => recorder.EndSession(game.Name)); // 그 사이 세션 끝
        await Task.Delay(100);
        release.Set(); // 연 세션이 끝난 세션에 붙으려 함
        await end;

        Assert.True(source.Disposed); // 붙지 못하고 바로 닫힘: 남는 세션 없음
    }

    [Fact]
    public async Task EachTenMinutePeriodHasItsOwnLimitSoALoadingBurstCannotEraseLaterStutter()
    {
        using var dir = new TempDir();
        using var counter = new DiskCounter { Slow = false };
        var source = new FakeFrames();
        for (int i = 0; i < 100; i++)
        {
            source.Seconds.Add(new FrameSecond(i, 100, 100 + i, 1, 1)); // 앞 구간 (0..99 초): 100 칸이 몰림
        }

        for (int i = 0; i < 5; i++)
        {
            source.Seconds.Add(new FrameSecond(100 + i, 100, 60, 1, 0)); // 뒤 구간 (100.. 초): 경기 중 60 ms 끊김
        }

        var recorder = Recorder(dir, new ListLog(), counter, source, periodSeconds: 100);
        GameSettings game = Games.Make("periods", "periods.exe");
        Assert.True(recorder.ObserveStart(game, new ProcessStart(32, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => counter.Reads > 2);
        recorder.EndSession(game.Name);

        int[] seconds = [.. ReadLastSample(dir, game.Name).GetProperty("frameSeconds").EnumerateArray().Select(e => e.GetProperty("t").GetInt32())];
        Assert.Equal(PlayRecorder.MaxFrameSecondsPerPeriod, seconds.Count(t => t < 100));
        Assert.Equal([100, 101, 102, 103, 104], seconds.Where(t => t >= 100)); // 뒤 구간의 60 ms 는 하나도 지워지지 않음
    }

    [Fact]
    public async Task TheOverallLimitTakesFromTheMostCrowdedPeriodsFirst()
    {
        using var dir = new TempDir();
        using var counter = new DiskCounter { Slow = false };
        var source = new FakeFrames();
        for (int period = 0; period < 8; period++)
        {
            for (int i = 0; i < 60; i++)
            {
                source.Seconds.Add(new FrameSecond(period * 100 + i, 100, 50 + i, 1, 0));
            }
        }

        var recorder = Recorder(dir, new ListLog(), counter, source, periodSeconds: 100);
        GameSettings game = Games.Make("overall", "overall.exe");
        Assert.True(recorder.ObserveStart(game, new ProcessStart(33, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => counter.Reads > 2);
        recorder.EndSession(game.Name);

        int[] perPeriod = [.. ReadLastSample(dir, game.Name).GetProperty("frameSeconds").EnumerateArray().GroupBy(e => e.GetProperty("t").GetInt32() / 100).OrderBy(g => g.Key).Select(g => g.Count())];
        Assert.Equal(8, perPeriod.Length);
        Assert.Equal(PlayRecorder.MaxFrameSeconds, perPeriod.Sum());
        Assert.All(perPeriod, n => Assert.InRange(n, 37, 38));
    }

    [Fact]
    public async Task NoEventsForThirtySecondsIsRecordedApartFromZeroFramesAndOpenFailureLeavesTheDiskRecord()
    {
        using var dir = new TempDir();
        long skew = 0;
        using var counter = new DiskCounter();
        var silent = new FakeFrames { Accepted = 0 };
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), new ListLog(), _ => counter, () => 8192, TimeSpan.FromMilliseconds(10),
            timestamp: () => Stopwatch.GetTimestamp() + Interlocked.Read(ref skew),
            openFrames: (_, _) => silent, frameBlocker: _ => null);
        GameSettings game = Games.Make("silent", "silent.exe");
        Assert.True(recorder.ObserveStart(game, new ProcessStart(41, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => counter.Reads > 3);
        Interlocked.Add(ref skew, 40 * Stopwatch.Frequency);
        recorder.EndSession(game.Name);

        Assert.Equal("no_events", ReadLastSample(dir, game.Name).GetProperty("frameStatus").GetString());

        using var dir2 = new TempDir();
        using var counter2 = new DiskCounter();
        var log = new ListLog();
        var failing = new PlayRecorder(dir2.Combine("play_samples.json"), log, _ => counter2, () => 8192, TimeSpan.FromMilliseconds(10),
            openFrames: (_, _) => throw new InvalidOperationException("access denied"), frameBlocker: _ => null);
        GameSettings other = Games.Make("fails", "fails.exe");
        Assert.True(failing.ObserveStart(other, new ProcessStart(42, other.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => counter2.Reads > 3);
        failing.EndSession(other.Name);

        JsonElement sample = ReadLastSample(dir2, other.Name);
        Assert.Equal("unavailable", sample.GetProperty("frameStatus").GetString());
        Assert.Equal("complete", sample.GetProperty("status").GetString()); // 디스크 기록은 그대로
        Assert.True(sample.GetProperty("total").GetProperty("sampleCount").GetInt32() > 0);
        Assert.Contains(log.Lines, line => line.Contains("frame times unavailable", StringComparison.Ordinal));
        Assert.Contains(log.Lines, line => line.Contains("play record:", StringComparison.Ordinal) && line.Contains("frames unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABlockingGateOrADisabledSettingNeverOpensASessionAndNoGateMeansBlocked()
    {
        int opened = 0;
        async Task<(string? Status, string Line)> Run(Func<bool>? enabled, Func<GameSettings, string?>? blocker, string name)
        {
            using var dir = new TempDir();
            using var counter = new DiskCounter();
            var log = new ListLog();
            var recorder = new PlayRecorder(dir.Combine("play_samples.json"), log, _ => counter, () => 8192, TimeSpan.FromMilliseconds(10),
                openFrames: (_, _) =>
                {
                    Interlocked.Increment(ref opened);
                    return new FakeFrames();
                }, frameEnabled: enabled, frameBlocker: blocker);
            GameSettings game = Games.Make(name, name + ".exe");
            Assert.True(recorder.ObserveStart(game, new ProcessStart(51, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
            await WaitUntil(() => counter.Reads > 2);
            recorder.EndSession(game.Name);
            return (ReadLastSample(dir, game.Name).GetProperty("frameStatus").GetString(), log.Lines.First(l => l.Contains("play record:", StringComparison.Ordinal)));
        }

        (string? blocked, string blockedLine) = await Run(null, g => "vanguard", "blocked");
        Assert.Equal("blocked", blocked);
        Assert.Contains("frames blocked", blockedLine, StringComparison.Ordinal);
        Assert.Equal("off", (await Run(() => false, _ => null, "off")).Status);
        Assert.Equal("blocked", (await Run(null, null, "nogate")).Status); // 게이트 판정을 받지 못하면 열지 않음
        Assert.Equal(0, opened);
        Assert.Equal("ok", (await Run(null, _ => null, "allowed")).Status);
        Assert.Equal(1, opened);
    }

    [Fact]
    public async Task ARecorderWithoutFrameSupportLeavesTheRecordAsBefore()
    {
        using var dir = new TempDir();
        using var counter = new DiskCounter();
        var log = new ListLog();
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), log, _ => counter, () => 8192, TimeSpan.FromMilliseconds(10));
        GameSettings game = Games.Make("plain", "plain.exe");
        Assert.True(recorder.ObserveStart(game, new ProcessStart(61, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => counter.Reads > 2);
        recorder.EndSession(game.Name);

        JsonElement sample = ReadLastSample(dir, game.Name);
        Assert.Equal(JsonValueKind.Null, sample.GetProperty("frameStatus").ValueKind);
        Assert.Equal(JsonValueKind.Null, sample.GetProperty("frameSeconds").ValueKind);
        Assert.DoesNotContain(log.Lines, line => line.Contains("frames", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EachSessionFreesItsSlotAndAlreadyRunningGamesPassTheirProcessNumbers()
    {
        using var dir = new TempDir();
        using var counter = new DiskCounter();
        var slots = new List<int>();
        var sources = new List<FakeFrames>();
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), new ListLog(), _ => counter, () => 8192, TimeSpan.FromMilliseconds(10),
            openFrames: (slot, _) =>
            {
                slots.Add(slot);
                var source = new FakeFrames();
                sources.Add(source);
                return source;
            }, frameBlocker: _ => null);
        GameSettings game = Games.Make("slots", "slots.exe", "child.exe");

        Assert.True(recorder.ObserveExisting(game, [301]));
        await WaitUntil(() => sources.Count == 1);
        Assert.False(recorder.ObserveExisting(game, [301, 302])); // 뒤늦게 뜬 등록 실행 파일
        Assert.Equal([301, 302], sources[0].Processes);
        recorder.EndSession(game.Name);
        Assert.True(recorder.ObserveExisting(game, [303]));
        await WaitUntil(() => sources.Count == 2);
        recorder.EndSession(game.Name);

        Assert.Equal([0, 0], slots);
        Assert.All(sources, s => Assert.True(s.Disposed));
    }

    [Fact]
    public async Task ASessionThatEndsWithoutAnyEventIsNoEventsEvenBeforeThirtySeconds()
    {
        using var dir = new TempDir();
        using var counter = new DiskCounter();
        var recorder = Recorder(dir, new ListLog(), counter, new FakeFrames { Accepted = 0 });
        GameSettings game = Games.Make("short", "short.exe");
        Assert.True(recorder.ObserveStart(game, new ProcessStart(71, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => counter.Reads > 2);
        recorder.EndSession(game.Name);

        Assert.Equal("no_events", ReadLastSample(dir, game.Name).GetProperty("frameStatus").GetString());
    }

    [Fact]
    public async Task NoSessionIsOpenedWhileAGateBlockedGameIsRunning()
    {
        using var dir = new TempDir();
        using var counter = new DiskCounter();
        int opened = 0;
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), new ListLog(), _ => counter, () => 8192, TimeSpan.FromMilliseconds(10),
            openFrames: (_, _) =>
            {
                Interlocked.Increment(ref opened);
                return new FakeFrames();
            }, frameBlocker: _ => null, isTraceBlocked: _ => false, blockedGameRunning: () => true);
        GameSettings game = Games.Make("beside", "beside.exe");
        Assert.True(recorder.ObserveStart(game, new ProcessStart(81, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => counter.Reads > 2);
        recorder.EndSession(game.Name);

        Assert.Equal(0, opened);
        Assert.Equal("blocked", ReadLastSample(dir, game.Name).GetProperty("frameStatus").GetString());
    }

    [Fact]
    public async Task StartingAGateBlockedGameClosesTheOpenSessionAtOnceAndMarksTheRestBlocked()
    {
        using var dir = new TempDir();
        using var counter = new DiskCounter();
        var log = new ListLog();
        var sources = new List<FakeFrames>();
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), log, _ => counter, () => 8192, TimeSpan.FromMilliseconds(10),
            openFrames: (_, _) =>
            {
                var source = new FakeFrames();
                source.Seconds.Add(new FrameSecond(0, 90, 20, 0, 0));
                sources.Add(source);
                return source;
            }, frameBlocker: g => g.Name == "guarded" ? "vanguard" : null, isTraceBlocked: g => g.Name == "guarded");
        GameSettings allowed = Games.Make("allowed", "allowed.exe");
        GameSettings guarded = Games.Make("guarded", "guarded.exe");
        Assert.True(recorder.ObserveStart(allowed, new ProcessStart(91, allowed.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => sources.Count == 1);
        await Task.Delay(50);

        Assert.True(recorder.ObserveStart(guarded, new ProcessStart(92, guarded.ExeNames[0], Stopwatch.GetTimestamp()), null)); // 시작 이벤트가 오자마자 닫힘
        Assert.True(sources[0].Disposed);
        Assert.Single(sources); // 막힌 게임 자신의 세션은 열리지 않음
        recorder.EndSession(allowed.Name);
        recorder.EndSession(guarded.Name);

        JsonElement sample = ReadLastSample(dir, allowed.Name);
        Assert.Equal("blocked", sample.GetProperty("frameStatus").GetString());
        Assert.True(sample.GetProperty("frameStoppedAtSeconds").GetDouble() > 0);
        Assert.Equal(90, sample.GetProperty("total").GetProperty("frameCount").GetInt64()); // 멈추기 전의 값은 그대로
        Assert.Contains(log.Lines, line => line.Contains("allowed: frame times stopped (anti-cheat game started)", StringComparison.Ordinal));
        Assert.Contains(log.Lines, line => line.Contains("allowed: play record:", StringComparison.Ordinal) && line.Contains("frame times stopped at", StringComparison.Ordinal) && line.Contains("frames 90", StringComparison.Ordinal));
        Assert.Equal("blocked", ReadLastSample(dir, guarded.Name).GetProperty("frameStatus").GetString());
    }

    [Fact]
    public async Task TheSessionClosesWhenTheGameProcessIsGoneWithoutWaitingForTheGraceAndReopensIfItReturns()
    {
        using var dir = new TempDir();
        using var counter = new DiskCounter();
        var sources = new List<FakeFrames>();
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), new ListLog(), _ => counter, () => 8192, TimeSpan.FromMilliseconds(10),
            openFrames: (_, _) =>
            {
                var source = new FakeFrames();
                sources.Add(source);
                return source;
            }, frameBlocker: _ => null);
        GameSettings game = Games.Make("exits", "exits.exe");
        Assert.True(recorder.ObserveStart(game, new ProcessStart(95, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => sources.Count == 1);

        sources[0].Lost = 3;
        recorder.NoteGone(new Dictionary<string, DateTimeOffset> { [game.Name] = DateTimeOffset.UtcNow }); // 종료 확정 (EndSession) 전
        Assert.True(sources[0].Disposed);
        recorder.NoteGone(new Dictionary<string, DateTimeOffset> { [game.Name] = DateTimeOffset.UtcNow }); // 틱마다 불려도 한 번만 닫음

        Assert.False(recorder.ObserveExisting(game, [96])); // 유예 중 게임이 돌아옴
        await WaitUntil(() => sources.Count == 2);
        Assert.Equal([95, 96], sources[1].Processes);
        sources[1].Lost = 4;
        recorder.EndSession(game.Name);
        Assert.True(sources[1].Disposed);
        Assert.Equal(7, ReadLastSample(dir, game.Name).GetProperty("frameEventsLost").GetInt64()); // 닫았다 다시 열어도 앞의 유실 수를 잃지 않음
    }

    [Fact]
    public void TheDiagnosticSessionNeverSharesTheServiceSessionNames()
    {
        Assert.DoesNotContain(Enumerable.Range(0, FrameTimeSession.MaxSlots).Select(FrameTimeSession.NameOf), name => name == FrameTimeSession.TestSessionName);
    }

    // ---- 설정 ----

    [Fact]
    public void PlayFrameTimesDefaultsToOnIsReadAndRejectsBadValues()
    {
        Assert.True(ParsePlay(null).FrameTimes);
        Assert.True(ParsePlay("{}").FrameTimes);
        Assert.False(ParsePlay("""{"frame_times":false}""").FrameTimes);
        Assert.Throws<SettingsException>(() => ParsePlay("""{"frame_times":"yes"}"""));
        Assert.Throws<SettingsException>(() => ParsePlay("""{"frame_time":true}"""));
    }

    private static PlaySettings ParsePlay(string? play)
    {
        var root = new JsonObject
        {
            ["games"] = new JsonArray(),
        };
        if (play is not null)
        {
            root["play"] = JsonNode.Parse(play);
        }

        return SettingsStore.Parse(root.ToJsonString(), @"C:\ProgramData\AsterCache\plans").Play;
    }

    // ---- 도구 ----

    private static PlayRecorder Recorder(TempDir dir, ListLog log, DiskCounter counter, FakeFrames source, IReadOnlyList<int>? checkpoints = null, int periodSeconds = PlayPeriods.DefaultSeconds) =>
        new(dir.Combine("play_samples.json"), log, _ => counter, () => 8192, TimeSpan.FromMilliseconds(10), checkpoints: checkpoints, periodSeconds: periodSeconds,
            openFrames: (_, _) => source, frameBlocker: _ => null, openSystem: () => new FakeSystem());

    private static JsonElement ReadLastSample(TempDir dir, string game)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        return json.RootElement.GetProperty("games").GetProperty(game).EnumerateArray().Last().Clone();
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(5);
        }

        Assert.True(condition(), "condition was not met before the timeout");
    }

    private sealed class FakeFrames : IFrameSource
    {
        private readonly object _gate = new();

        public List<FrameSecond> Seconds { get; } = [];
        public List<int> Processes { get; } = [];
        public long Accepted { get; set; } = 1;
        public long Lost { get; set; }
        public long Late { get; set; }
        public long OutOfOrder { get; set; }
        public bool Disposed { get; private set; }
        public long AcceptedEvents => Accepted;
        public long EventsLost => Lost;
        public long EventsLate => Late;
        public long EventsOutOfOrder => OutOfOrder;

        public void AddProcess(int pid)
        {
            lock (_gate)
            {
                Processes.Add(pid);
            }
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

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeSystem : IPlaySystemProbe
    {
        public PlaySystemSample Read() => new(12.5, 6128);

        public void Dispose()
        {
        }
    }

    private sealed class DiskCounter : IPlayDiskCounter
    {
        public string? PhysicalDisk => "disk1";
        public IReadOnlyList<PlayDiskCounters> Initial { get; } = [new(1, 0, 0, 0, 0, 0)];
        public int Reads { get; private set; }
        public bool Slow { get; set; } = true;

        public IReadOnlyList<PlayDiskCounters>? Read()
        {
            Reads++;
            return [new PlayDiskCounters(1, Reads * 1024, (uint)(Reads * 5), Reads * (Slow ? 5_000_000L : 5_000L), 0, 0)]; // 표본마다 5 건, 읽기 하나 100 ms (Slow) 또는 0.1 ms
        }

        public void Dispose()
        {
        }
    }
}
