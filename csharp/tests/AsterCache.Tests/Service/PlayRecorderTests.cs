using System.Diagnostics;
using System.Text.Json;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tests.Windows;
using AsterCache.Windows.Native;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class PlayRecorderTests
{
    [Fact]
    public void CumulativeWindowsUseReadCountAndAverageLatencyForSlowSamples()
    {
        Assert.Equal(new[] { 30, 60, 120, 300, 600 }, PlayRecorder.CheckpointSeconds);
        var accumulator = new PlayRecorder.PlayRecordAccumulator([1, 2, 3, 4, 5], 1);
        PlayDiskCounters[] previous = [new(1, 0, 0, 0, 0, 0)];
        PlayDiskCounters[] samples =
        [
            new(1, 100, 5, 5_000_000, 0, 1),       // 5 reads at exactly 100 ms
            new(1, 150, 9, 9_000_000, 0, 0),       // 4 reads: below the count threshold
            new(1, 200, 14, 13_999_999, 0, 2),     // 5 reads just below 100 ms
            new(1, 250, 19, 18_999_999, 0, 3),     // a long-read sample interval
            new(1, 300, 24, 23_999_999, 0, 1),     // continues the longest streak
        ];

        IReadOnlyList<PlayWindowSummary> lastCheckpoint = [];
        double previousAt = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            lastCheckpoint = accumulator.Add(previous, [samples[i]], previousAt, i + 1);
            previous = [samples[i]];
            previousAt = i + 1;
        }

        PlayWindowSummary checkpoint = Assert.Single(lastCheckpoint);
        PlayWindowSummary total = accumulator.Total();
        Assert.Equal(5, checkpoint.Seconds);
        Assert.Equal(300, total.ReadBytes);
        Assert.Equal((ulong)24, total.ReadCount);
        Assert.Equal(3, total.SlowSamples);
        Assert.Equal(2, total.LongestSlowSamples);
        Assert.Equal(5, total.SampleCount);
        Assert.Equal(0, total.MissingSampleCount);
        Assert.Equal(2.3999999, total.ReadSeconds, 6);
        Assert.Equal((uint)3, total.MaxQueueDepth);
    }

    [Fact]
    public void DiskPerformanceIoctlsMatchCtlCodeValues()
    {
        static uint CtlCode(uint function) => (7u << 16) | (function << 2); // METHOD_BUFFERED, FILE_ANY_ACCESS

        Assert.Equal(Win32.IoctlDiskPerformance, CtlCode(0x08));
        Assert.Equal(0x00070020u, Win32.IoctlDiskPerformance);
        Assert.Equal(Win32.IoctlDiskPerformanceOff, CtlCode(0x18));
        Assert.Equal(0x00070060u, Win32.IoctlDiskPerformanceOff);
    }

    [Fact]
    public void PerDiskDeltasHandleReadCountWrapAndSkipResetIntervals()
    {
        var accumulator = new PlayRecorder.PlayRecordAccumulator([], 1);
        PlayDiskCounters[] previous =
        [
            new(0, 100, uint.MaxValue - 2, 5_000_000, 1_000_000, 1),
            new(1, 200, 7, 5_000_000, 1_000_000, 1),
        ];
        PlayDiskCounters[] reset =
        [
            new(0, 150, 2, 6_000_000, 2_000_000, 2),
            new(1, 10, 9, 100, 1_000_001, 1),
        ];

        _ = accumulator.Add(previous, reset, 0, 1);

        PlayWindowSummary afterReset = accumulator.Total();
        Assert.Equal(50, afterReset.ReadBytes); // 디스크 1 의 초기화 구간은 합산하지 않음
        Assert.Equal((ulong)5, afterReset.ReadCount); // 디스크 0 의 32비트 순환
        Assert.Equal(1, afterReset.MissingSampleCount);
        Assert.True(afterReset.CounterReset);

        PlayDiskCounters[] recovered =
        [
            new(0, 170, 4, 8_000_000, 3_000_000, 1),
            new(1, 15, 12, 200, 1_000_101, 2),
        ];
        _ = accumulator.Add(reset, recovered, 1, 2);

        PlayWindowSummary total = accumulator.Total();
        Assert.Equal(75, total.ReadBytes);
        Assert.Equal((ulong)10, total.ReadCount);
        Assert.Equal(2, total.SampleCount);
        Assert.Equal(1, total.MissingSampleCount);
    }

    [Fact]
    public void CheckpointsCrossedByALateSampleCarryTheMeasurementTimeAndDelay()
    {
        var accumulator = new PlayRecorder.PlayRecordAccumulator([30, 60], 1);
        PlayDiskCounters[] previous = [new(0, 0, 0, 0, 0, 0)];
        PlayDiskCounters[] current = [new(0, 900, 9, 9_000_000, 0, 1)];

        IReadOnlyList<PlayWindowSummary> windows = accumulator.Add(previous, current, 29, 61);

        Assert.Equal([30, 60], windows.Select(window => window.Seconds));
        Assert.All(windows, window =>
        {
            Assert.Equal(61, window.MeasuredAtSeconds);
            Assert.True(window.Delayed);
            Assert.Equal(31, window.MissingSampleCount);
        });
    }

    [Fact]
    public void MissingSampleGapBreaksConsecutiveSlowSampleStreaks()
    {
        var accumulator = new PlayRecorder.PlayRecordAccumulator([1, 4], 1);
        PlayDiskCounters[] first = [new(0, 50, 5, 5_000_000, 0, 1)];
        PlayDiskCounters[] second = [new(0, 100, 10, 10_000_000, 0, 1)];

        _ = accumulator.Add([new PlayDiskCounters(0, 0, 0, 0, 0, 0)], first, 0, 1);
        _ = accumulator.Add(first, second, 1, 4);

        PlayWindowSummary total = accumulator.Total();
        Assert.Equal(2, total.SlowSamples);
        Assert.Equal(1, total.LongestSlowSamples);
        Assert.Equal(2, total.MissingSampleCount);
    }

    [Fact]
    public void OlderStoreUpdateKeepsFinishedSummaryAndMergesLateStreamResult()
    {
        using var dir = new TempDir();
        var store = new PlayRecorder.PlaySamplesStore(dir.Combine("play_samples.json"), new ListLog());
        DateTimeOffset endedAt = DateTimeOffset.UnixEpoch.AddMinutes(5);
        store.Upsert(new PlaySampleRecord
        {
            SessionId = "session",
            Game = "game",
            StartedAt = DateTimeOffset.UnixEpoch,
            EndedAt = endedAt,
            DurationSeconds = 300,
            Status = "complete",
            StartStream = new PlayStreamResult("pending", null, null, null, null),
            Total = new PlayWindowSummary(null, 300, 4096, 7, 0.8, 1, 1, 2, 0, false, false, 2, 1),
        });
        store.Upsert(new PlaySampleRecord
        {
            SessionId = "session",
            Game = "game",
            StartedAt = DateTimeOffset.UnixEpoch,
            DurationSeconds = 120,
            Status = "recording",
            StartStream = new PlayStreamResult("completed", null, 1024, 1, 1),
        });

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        JsonElement sample = Assert.Single(json.RootElement.GetProperty("games").GetProperty("game").EnumerateArray());
        Assert.Equal(300, sample.GetProperty("durationSeconds").GetDouble());
        Assert.Equal("complete", sample.GetProperty("status").GetString());
        Assert.Equal(4096, sample.GetProperty("total").GetProperty("readBytes").GetInt64());
        Assert.Equal("completed", sample.GetProperty("startStream").GetProperty("status").GetString());
    }

    [Fact]
    public async Task SameGameProcessStartsShareOneSessionAndExitWritesStreamAndWarmState()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        int opened = 0;
        using var counter = new FakeCounter();
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), log, _ =>
        {
            Interlocked.Increment(ref opened);
            return counter;
        }, () => 8192, TimeSpan.FromMilliseconds(10), checkpoints: [1]);
        GameSettings game = Games.Make("Full game name", "launcher-game.exe", "main-game.exe");
        ProcessStart first = new(21, game.ExeNames[0], Stopwatch.GetTimestamp());
        ProcessStart second = new(22, game.ExeNames[1], Stopwatch.GetTimestamp());
        var warm = new PlayWarmSnapshot("profile Unity", "ok", null, 8173, 10218, 38 * 60);

        Assert.True(recorder.ObserveStart(game, first, warm));
        Assert.True(recorder.ObserveStart(game, second, null));
        await WaitUntil(() => Volatile.Read(ref opened) == 1 && counter.Reads > 0);
        recorder.RecordStartStream(first, new PlayStreamResult("completed", null, 2_254L << 20, 61.6, 36.6));
        recorder.EndSession(game.Name);

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        JsonElement sample = Assert.Single(json.RootElement.GetProperty("games").GetProperty(game.Name).EnumerateArray());
        Assert.Equal("Full game name", sample.GetProperty("game").GetString());
        Assert.Equal("complete", sample.GetProperty("status").GetString());
        Assert.Equal(8192, sample.GetProperty("availableRamMib").GetInt64());
        Assert.Equal("profile Unity", sample.GetProperty("startWarm").GetProperty("mode").GetString());
        Assert.Equal("completed", sample.GetProperty("startStream").GetProperty("status").GetString());
        Assert.NotNull(sample.GetProperty("endedAt").GetString());
        Assert.Contains(log.Lines, line => line.Contains("Full game name: play record:", StringComparison.Ordinal));
        Assert.Contains(log.Lines, line => line.Contains("start RAM 8192 MiB", StringComparison.Ordinal));
        Assert.Contains(log.Lines, line => line.Contains("slow samples (longest", StringComparison.Ordinal)
            && line.Contains("samples, ", StringComparison.Ordinal) && line.Contains(" missing", StringComparison.Ordinal));
        Assert.Equal(1, opened);
        Assert.True(counter.Disposed);
    }

    [Fact]
    public async Task SessionStopsCountingAtTheFirstTimeTheProcessWasGone()
    {
        using var dir = new TempDir();
        using var counter = new FakeCounter();
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), new ListLog(), _ => counter, () => 8192, TimeSpan.FromMilliseconds(10));
        GameSettings game = Games.Make("gone", "gone.exe");
        var clock = Stopwatch.StartNew();
        Assert.True(recorder.ObserveStart(game, new ProcessStart(41, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => counter.Reads > 3);
        DateTimeOffset goneAt = DateTimeOffset.UtcNow;

        recorder.NoteGone(new Dictionary<string, DateTimeOffset> { [game.Name] = goneAt });
        double goneSeconds = clock.Elapsed.TotalSeconds;
        int readsAtGone = counter.Reads;
        await WaitUntil(() => counter.Reads >= readsAtGone + 20); // 종료 확정 전 유예 동안 표본이 더 쌓임
        recorder.EndSession(game.Name);
        double endSeconds = clock.Elapsed.TotalSeconds;

        JsonElement sample = ReadLastSample(dir, game.Name);
        Assert.Equal(goneAt, sample.GetProperty("endedAt").GetDateTimeOffset()); // 종료 확정 시각이 아니라 소멸 최초 관측 시각
        double duration = sample.GetProperty("durationSeconds").GetDouble();
        Assert.InRange(duration, 0, goneSeconds + 0.01);
        Assert.True(duration < endSeconds - 0.15, $"duration {duration} must not include the grace until {endSeconds}");
        JsonElement total = sample.GetProperty("total");
        int samples = total.GetProperty("sampleCount").GetInt32();
        Assert.InRange(samples, 1, readsAtGone); // 유예 동안의 표본(20개 이상)은 세지 않음
        Assert.Equal(samples * 0.1, total.GetProperty("idleSeconds").GetDouble(), 3); // 표본마다 유휴 0.1 초
    }

    [Fact]
    public async Task ReturnFromTheGraceCancelsTheGoneMarkAndAStaleMarkIsNotRevived()
    {
        using var dir = new TempDir();
        using var counter = new FakeCounter();
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), new ListLog(), _ => counter, () => 8192, TimeSpan.FromMilliseconds(10));
        GameSettings game = Games.Make("back", "back.exe");
        Assert.True(recorder.ObserveStart(game, new ProcessStart(42, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        int baseline = counter.Reads;
        await WaitUntil(() => counter.Reads > baseline + 2);
        DateTimeOffset goneAt = DateTimeOffset.UtcNow;
        Dictionary<string, DateTimeOffset> gone = new() { [game.Name] = goneAt };

        recorder.NoteGone(gone);
        recorder.ObserveExisting(game); // 유예 중 같은 게임의 프로세스가 다시 보임
        recorder.NoteGone(gone); // 틱 스레드가 읽어 둔 옛 값이 뒤늦게 도착
        recorder.EndSession(game.Name);

        Assert.True(ReadLastSample(dir, game.Name).GetProperty("endedAt").GetDateTimeOffset() > goneAt);
    }

    private static JsonElement ReadLastSample(TempDir dir, string game)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        return json.RootElement.GetProperty("games").GetProperty(game).EnumerateArray().Last().Clone();
    }

    [Fact]
    public async Task CounterOpenFailureIsPersistedAsUnavailableWithoutThrowing()
    {
        using var dir = new TempDir();
        var log = new ListLog();
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), log, _ => null, sampleInterval: TimeSpan.FromMilliseconds(5));
        GameSettings game = Games.Make("unavailable", "unavailable.exe");

        Assert.True(recorder.ObserveStart(game, new ProcessStart(31, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => File.Exists(dir.Combine("play_samples.json")));
        recorder.EndSession(game.Name);

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        JsonElement sample = Assert.Single(json.RootElement.GetProperty("games").GetProperty(game.Name).EnumerateArray());
        Assert.Equal("unavailable", sample.GetProperty("status").GetString());
        Assert.Empty(sample.GetProperty("windows").EnumerateArray());
        Assert.Contains(log.Lines, line => line.Contains("counter unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CounterReadFailureIsPersistedAsUnavailable()
    {
        using var dir = new TempDir();
        var counter = new FailingCounter();
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), new ListLog(), _ => counter, sampleInterval: TimeSpan.FromMilliseconds(5));
        GameSettings game = Games.Make("read failure", "read-failure.exe");

        Assert.True(recorder.ObserveStart(game, new ProcessStart(32, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => File.Exists(dir.Combine("play_samples.json")));
        recorder.EndSession(game.Name);

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        JsonElement sample = Assert.Single(json.RootElement.GetProperty("games").GetProperty(game.Name).EnumerateArray());
        Assert.Equal("unavailable", sample.GetProperty("status").GetString());
        Assert.True(counter.Disposed);
    }

    [Fact]
    public async Task SettingsReloadStopsTheSamplingLoopAndStoresAnInterruptedSession()
    {
        using var dir = new TempDir();
        var counter = new FakeCounter();
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), new ListLog(), _ => counter, sampleInterval: TimeSpan.FromMilliseconds(5));
        GameSettings game = Games.Make("reload", "reload.exe");

        Assert.True(recorder.ObserveStart(game, new ProcessStart(33, game.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => counter.Reads > 0);
        recorder.StopAll("settings_reloaded");

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        JsonElement sample = Assert.Single(json.RootElement.GetProperty("games").GetProperty(game.Name).EnumerateArray());
        Assert.Equal("interrupted", sample.GetProperty("status").GetString());
        Assert.Equal("settings_reloaded", sample.GetProperty("endReason").GetString());
        Assert.True(counter.Disposed);
    }

    [Fact]
    public async Task ExistingGameStartsAtObservationTimeAndUnchangedSessionSurvivesReload()
    {
        using var dir = new TempDir();
        int opened = 0;
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), new ListLog(), _ =>
        {
            Interlocked.Increment(ref opened);
            return new FakeCounter();
        }, sampleInterval: TimeSpan.FromMilliseconds(5));
        GameSettings existing = Games.Make("already running", "already-running.exe");
        GameSettings removed = Games.Make("removed", "removed.exe");

        Assert.True(recorder.ObserveExisting(existing));
        Assert.True(recorder.ObserveStart(removed, new ProcessStart(52, removed.ExeNames[0], Stopwatch.GetTimestamp()), null));
        await WaitUntil(() => Volatile.Read(ref opened) == 2);
        recorder.StopGamesExcept([existing.Name], "settings_reloaded");
        recorder.EndSession(existing.Name);

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        JsonElement existingRecord = Assert.Single(json.RootElement.GetProperty("games").GetProperty(existing.Name).EnumerateArray());
        JsonElement removedRecord = Assert.Single(json.RootElement.GetProperty("games").GetProperty(removed.Name).EnumerateArray());
        Assert.True(existingRecord.GetProperty("observedAfterGameStart").GetBoolean());
        Assert.Equal("skipped", existingRecord.GetProperty("startStream").GetProperty("status").GetString());
        Assert.Equal("complete", existingRecord.GetProperty("status").GetString());
        Assert.Equal("game_exit", existingRecord.GetProperty("endReason").GetString());
        Assert.Equal("interrupted", removedRecord.GetProperty("status").GetString());
        Assert.Equal("settings_reloaded", removedRecord.GetProperty("endReason").GetString());
    }

    [Fact]
    public void LaterProcessStartCanRecordAStreamAfterLateObservation()
    {
        using var dir = new TempDir();
        var recorder = new PlayRecorder(dir.Combine("play_samples.json"), new ListLog(), _ => null);
        GameSettings game = Games.Make("observed game", "first.exe", "second.exe");
        ProcessStart later = new(54, game.ExeNames[1], Stopwatch.GetTimestamp());

        Assert.True(recorder.ObserveExisting(game));
        Assert.True(recorder.ObserveStart(game, later, null));
        recorder.RecordStartStream(later, new PlayStreamResult("completed", null, 4096, 1.2, 3.2));
        recorder.EndSession(game.Name);

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        JsonElement sample = Assert.Single(json.RootElement.GetProperty("games").GetProperty(game.Name).EnumerateArray());
        Assert.Equal("completed", sample.GetProperty("startStream").GetProperty("status").GetString());
    }

    [Fact]
    public void StoreKeepsOnlyTheMostRecentFiftySamplesPerGame()
    {
        using var dir = new TempDir();
        var store = new PlayRecorder.PlaySamplesStore(dir.Combine("play_samples.json"), new ListLog());
        for (int i = 0; i < 51; i++)
        {
            store.Upsert(new PlaySampleRecord
            {
                SessionId = i.ToString(),
                Game = "Full game name",
                StartedAt = DateTimeOffset.UnixEpoch.AddSeconds(i),
                Status = "complete",
            });
        }

        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(dir.Combine("play_samples.json")));
        JsonElement.ArrayEnumerator samples = json.RootElement.GetProperty("games").GetProperty("Full game name").EnumerateArray();
        JsonElement[] records = samples.ToArray();
        Assert.Equal(50, records.Length);
        Assert.Equal("1", records[0].GetProperty("sessionId").GetString());
        Assert.Equal("50", records[^1].GetProperty("sessionId").GetString());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(5);
        }

        Assert.True(condition(), "condition was not met before the timeout");
    }

    private sealed class FakeCounter : IPlayDiskCounter
    {
        public string? PhysicalDisk => "disk1";
        public IReadOnlyList<PlayDiskCounters> Initial { get; } = [new(1, 0, 0, 0, 0, 0)];
        public int Reads { get; private set; }
        public bool Disposed { get; private set; }

        public IReadOnlyList<PlayDiskCounters>? Read()
        {
            Reads++;
            return [new PlayDiskCounters(1, Reads * 1024, (uint)(Reads * 5), Reads * 5_000_000L, Reads * 1_000_000L, (uint)(Reads % 4))];
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FailingCounter : IPlayDiskCounter
    {
        public string? PhysicalDisk => "disk1";
        public IReadOnlyList<PlayDiskCounters> Initial { get; } = [new(1, 0, 0, 0, 0, 0)];
        public bool Disposed { get; private set; }
        public IReadOnlyList<PlayDiskCounters>? Read() => null;
        public void Dispose() => Disposed = true;
    }
}
