using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using AsterCache.Core;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

internal sealed record PlayWarmSnapshot(string Mode, string Result, string? Reason, long? ReadMib, long? TargetMib, double? SecondsAgo);

public sealed record PlayStreamResult(string Status, string? Reason, long? ReadBytes, double? Seconds, double? MegabytesPerSecond);

internal readonly record struct PlayDiskCounters(uint DiskNumber, long ReadBytes, uint ReadCount, long ReadTime100ns, long IdleTime100ns, uint QueueDepth);

internal interface IPlayDiskCounter : IDisposable
{
    string? PhysicalDisk { get; }
    IReadOnlyList<PlayDiskCounters> Initial { get; }
    IReadOnlyList<PlayDiskCounters>? Read();
}

internal sealed record PlayWindowSummary(
    int? Seconds,
    double MeasuredAtSeconds,
    long ReadBytes,
    ulong ReadCount,
    double ReadSeconds,
    int SlowSamples,
    int LongestSlowSamples,
    int SampleCount,
    int MissingSampleCount,
    bool Delayed,
    bool CounterReset,
    double IdleSeconds,
    uint MaxQueueDepth);

internal sealed class PlaySampleRecord
{
    public string SessionId { get; set; } = string.Empty;
    public string Game { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public double DurationSeconds { get; set; }
    public string Status { get; set; } = "recording";
    public string? EndReason { get; set; }
    public bool ObservedAfterGameStart { get; set; }
    public string? PhysicalDisk { get; set; }
    public long AvailableRamMib { get; set; }
    public PlayWarmSnapshot? StartWarm { get; set; }
    public PlayStreamResult StartStream { get; set; } = new("pending", null, null, null, null);
    public List<PlayWindowSummary> Windows { get; set; } = [];
    public PlayWindowSummary? Total { get; set; }
}

/// 게임 시작 때부터 물리 디스크의 누적 통계를 읽어 구간별 표본과 종료 요약을 남깁니다.
/// 카운터는 시스템 통계만 질의하며 게임 프로세스나 설치 파일을 열지 않습니다.
public sealed class PlayRecorder : IDisposable
{
    public const int MaxSamplesPerGame = 50;
    public static readonly int[] CheckpointSeconds = [30, 60, 120, 300, 600];
    private static readonly TimeSpan LateStreamRetention = TimeSpan.FromMinutes(10);

    private readonly object _gate = new();
    private readonly Dictionary<string, Session> _active = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ProcessKey, Session> _processSessions = [];
    private readonly Func<string, IPlayDiskCounter?> _openCounter;
    private readonly Func<long> _availableRamMib;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<long> _timestamp;
    private readonly TimeSpan _sampleInterval;
    private readonly int[] _checkpoints;
    private readonly PlaySamplesStore _store;
    private readonly ILog _log;
    private bool _disposed;
    private const string ExistingGameStreamReason = "game already running when observation began";

    internal PlayRecorder(string? path, ILog log, Func<string, IPlayDiskCounter?>? openCounter = null, Func<long>? availableRamMib = null,
        TimeSpan? sampleInterval = null, Func<DateTimeOffset>? now = null, Func<long>? timestamp = null, IReadOnlyList<int>? checkpoints = null)
    {
        _log = log;
        _store = new PlaySamplesStore(path, log);
        _openCounter = openCounter ?? WindowsPlayDiskCounter.Open;
        _availableRamMib = availableRamMib ?? SystemMemory.AvailableMib;
        _sampleInterval = sampleInterval ?? TimeSpan.FromSeconds(1);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _checkpoints = checkpoints is null ? [.. CheckpointSeconds] : [.. checkpoints.Order()];
    }

    /// 시작 이벤트 시각으로 세션을 만듭니다. 같은 게임의 후속 exe 는 현재 세션에 연결합니다.
    internal bool ObserveStart(GameSettings game, ProcessStart start, PlayWarmSnapshot? warm)
    {
        ProcessKey key = new(start.Pid, start.Timestamp);
        lock (_gate)
        {
            if (_disposed || _processSessions.ContainsKey(key))
            {
                return false;
            }

            PruneLateStreamsLocked();
            if (!_active.TryGetValue(game.Name, out Session? session))
            {
                long startedTick = start.Timestamp > 0 ? start.Timestamp : _timestamp();
                long ram = SafeAvailableRam();
                session = new Session(game, _now(), startedTick, ram, warm, _checkpoints, _sampleInterval.TotalSeconds, observedAfterGameStart: false);
                _active.Add(game.Name, session);
                session.SamplingTask = Task.Run(() => SampleSessionAsync(session));
            }
            else
            {
                session.NoteReturn(_now()); // 유예 중에 같은 게임의 다른 exe 가 뜸: 종료가 아님
            }

            _processSessions.Add(key, session);
            return true;
        }
    }

    /// 서비스 시작이나 설정 재적재 때 이미 실행 중인 게임의 관측을 시작합니다.
    internal bool ObserveExisting(GameSettings game)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_active.TryGetValue(game.Name, out Session? current))
            {
                current.NoteReturn(_now()); // 매 틱 실행 중인 게임마다 불림: 유예에서 돌아온 세션의 소멸 표시를 지움
                return false;
            }

            long tick = _timestamp();
            var skippedStream = new PlayStreamResult("skipped", ExistingGameStreamReason, null, null, null);
            var session = new Session(game, _now(), tick, SafeAvailableRam(), null, _checkpoints, _sampleInterval.TotalSeconds,
                observedAfterGameStart: true, initialStartStream: skippedStream);
            _active.Add(game.Name, session);
            session.SamplingTask = Task.Run(() => SampleSessionAsync(session));
            return true;
        }
    }

    /// 종료 유예 중인 게임의 소멸 최초 관측 시각을 세션에 표시합니다. 종료가 확정되면 기록은 그 시각까지만 셉니다 (유예 60~90 초는 게임 시간이 아님).
    internal void NoteGone(IReadOnlyDictionary<string, DateTimeOffset> goneSince)
    {
        if (goneSince.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            long tick = _timestamp();
            DateTimeOffset now = _now();
            foreach ((string game, DateTimeOffset at) in goneSince)
            {
                if (_active.TryGetValue(game, out Session? session))
                {
                    long lag = Math.Max(0, (long)((now - at).TotalSeconds * Stopwatch.Frequency));
                    session.NoteGone(at, tick - lag); // 시각과 틱을 같은 순간 (첫 관측) 에 맞춤
                }
            }
        }
    }

    /// 시작 스트림 결과는 해당 시작 이벤트의 세션에 붙입니다. 늦게 끝난 스트림도 종료 기록을 갱신합니다.
    internal void RecordStartStream(ProcessStart start, PlayStreamResult result)
    {
        Session? session;
        lock (_gate)
        {
            if (!_processSessions.Remove(new ProcessKey(start.Pid, start.Timestamp), out session))
            {
                return;
            }
        }

        if (session.SetStartStream(result, _timestamp()) is { } record)
        {
            _store.Upsert(record);
        }
    }

    /// 게임 종료 유예가 끝났을 때 세션을 끝냅니다.
    internal void EndSession(string game, string reason = "game_exit")
    {
        Session? session;
        lock (_gate)
        {
            if (!_active.Remove(game, out session))
            {
                return;
            }

            session.RequestEnd(reason);
        }

        session.StopSampling();
        PlaySampleRecord record = session.Finish(reason, _now(), _timestamp());
        _store.Upsert(record);
        _log.Info(Format(record));
    }

    /// 설정 재적재나 서비스 정지 때 실행 중인 표본 루프를 끝냅니다.
    internal void StopAll(string reason)
    {
        string[] games;
        lock (_gate)
        {
            games = [.. _active.Keys];
        }

        foreach (string game in games)
        {
            EndSession(game, reason);
        }
    }

    /// 설정에서 사라진 게임만 끝내고 남아 있는 게임 세션은 그대로 둡니다.
    internal void StopGamesExcept(IEnumerable<string> games, string reason)
    {
        HashSet<string> retained = new(games, StringComparer.OrdinalIgnoreCase);
        string[] removed;
        lock (_gate)
        {
            removed = [.. _active.Keys.Where(game => !retained.Contains(game))];
        }

        foreach (string game in removed)
        {
            EndSession(game, reason);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        StopAll("service_stop");
        lock (_gate)
        {
            _processSessions.Clear();
        }
    }

    private async Task SampleSessionAsync(Session session)
    {
        IPlayDiskCounter? counter = null;
        IReadOnlyList<PlayDiskCounters> previous = [];
        long previousTick = 0;
        bool hasBaseline = false;
        try
        {
            counter = _openCounter(session.Game.OriginRoot);
            if (counter is null || counter.Initial.Count == 0)
            {
                Persist(session.MarkUnavailable(_timestamp()));
                return;
            }

            previous = counter.Initial;
            previousTick = _timestamp();
            hasBaseline = true;
            session.SetPhysicalDisk(counter.PhysicalDisk);

            while (true)
            {
                await Task.Delay(_sampleInterval, session.Stop.Token).ConfigureAwait(false);
                long tick = _timestamp();
                IReadOnlyList<PlayDiskCounters>? current = counter.Read();
                if (current is null)
                {
                    Persist(session.MarkUnavailable(_timestamp()));
                    return;
                }

                foreach (PlayWindowSummary checkpoint in session.AddSample(previous, current, previousTick, tick))
                {
                    PlaySampleRecord record = session.Snapshot(tick);
                    _store.Upsert(record);
                    if (checkpoint.Seconds == 600)
                    {
                        _log.Info(Format(record));
                    }
                }

                previous = current;
                previousTick = tick;
            }
        }
        catch (OperationCanceledException) when (session.Stop.IsCancellationRequested)
        {
            // 종료 시 마지막 통계를 읽고 정리합니다.
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            Persist(session.MarkUnavailable(_timestamp()));
        }
        finally
        {
            if (counter is not null && hasBaseline && !session.IsUnavailable)
            {
                try
                {
                    IReadOnlyList<PlayDiskCounters>? final = counter.Read();
                    if (final is { Count: > 0 })
                    {
                        long tick = _timestamp();
                        foreach (PlayWindowSummary checkpoint in session.AddSample(previous, final, previousTick, tick))
                        {
                            PlaySampleRecord record = session.Snapshot(tick);
                            _store.Upsert(record);
                            if (checkpoint.Seconds == 600)
                            {
                                _log.Info(Format(record));
                            }
                        }
                    }
                    else if (!session.IsUnavailable)
                    {
                        Persist(session.MarkUnavailable(_timestamp()));
                    }
                }
                catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
                {
                    Persist(session.MarkUnavailable(_timestamp()));
                }
            }

            try
            {
                counter?.Dispose();
            }
            catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
            {
                // 성능 카운터 정리 실패는 세션이나 서비스를 중단시키지 않습니다.
            }
        }
    }

    private void Persist(PlaySampleRecord record) => _store.Upsert(record);

    private long SafeAvailableRam()
    {
        try
        {
            return Math.Max(0, _availableRamMib());
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            return 0;
        }
    }

    private void PruneLateStreamsLocked()
    {
        DateTimeOffset cutoff = _now() - LateStreamRetention;
        foreach (ProcessKey key in _processSessions.Where(pair => pair.Value.EndedAt is { } ended && ended < cutoff).Select(pair => pair.Key).ToList())
        {
            _processSessions.Remove(key);
        }
    }

    private static string Format(PlaySampleRecord record)
    {
        List<string> parts = [];
        if (record.ObservedAfterGameStart)
        {
            parts.Add("observation started after game start");
        }

        parts.Add(FormatWarm(record.StartWarm));
        parts.Add($"start RAM {record.AvailableRamMib} MiB");
        parts.Add(FormatStream(record.StartStream));
        foreach (PlayWindowSummary window in record.Windows)
        {
            parts.Add(FormatWindow(window, $"{window.Seconds}s"));
        }

        PlayWindowSummary? total = record.Total ?? record.Windows.LastOrDefault(w => w.Seconds == 600);
        string duration = $"total {(int)Math.Round(record.DurationSeconds / 60d, MidpointRounding.AwayFromZero)} min";
        if (total is not null)
        {
            duration += ", " + FormatWindow(total, "disk read");
        }

        parts.Add(duration);
        if (record.Status == "unavailable")
        {
            parts.Add("counter unavailable");
        }
        else if (record.EndReason is not null && record.EndReason != "game_exit")
        {
            parts.Add(record.EndReason.Replace('_', ' '));
        }

        return $"{record.Game}: play record: {string.Join("; ", parts)}";
    }

    private static string FormatWarm(PlayWarmSnapshot? warm)
    {
        if (warm is null)
        {
            return "start warm unavailable";
        }

        string age = warm.SecondsAgo is { } seconds ? $" {(long)(seconds / 60)} min ago" : string.Empty;
        string read = warm.ReadMib is { } readMib ? readMib.ToString(CultureInfo.InvariantCulture) : "?";
        string target = warm.TargetMib is { } targetMib ? targetMib.ToString(CultureInfo.InvariantCulture) : "?";
        string result = warm.Result == "ok" ? "complete" : warm.Reason == Prewarmer.Cancelled ? "cancelled" : warm.Result;
        return $"start warm {warm.Mode} {read}/{target} MiB{age} ({result})";
    }

    private static string FormatStream(PlayStreamResult stream)
    {
        if (stream.Status == "pending")
        {
            return "start stream pending";
        }

        if (stream.Status == "skipped")
        {
            return $"start stream skipped ({stream.Reason ?? "unknown"})";
        }

        if (stream.ReadBytes is { } bytes && stream.Seconds is { } seconds && stream.MegabytesPerSecond is { } mbps)
        {
            return $"start stream {bytes / 1_048_576d:F0} MiB in {seconds:F1} s ({mbps:F1} MB/s, {stream.Status}{(stream.Reason is null ? string.Empty : ", " + stream.Reason)})";
        }

        return $"start stream {stream.Status}{(stream.Reason is null ? string.Empty : $" ({stream.Reason})")}";
    }

    private static string FormatWindow(PlayWindowSummary window, string label)
    {
        string measured = window.Seconds is { } seconds
            ? $"{label} measured at {window.MeasuredAtSeconds:F1}s{(window.Delayed ? $" (late {window.MeasuredAtSeconds - seconds:F1}s, boundary missed)" : string.Empty)}"
            : $"{label} through {window.MeasuredAtSeconds:F1}s";
        string reset = window.CounterReset ? ", counter reset interval missing" : string.Empty;
        return $"{measured} {window.ReadBytes / 1_048_576d:F0} MiB/{window.ReadCount} reads/{window.ReadSeconds:F1} s read time/{window.SlowSamples} slow samples (longest {window.LongestSlowSamples}, {window.SampleCount} samples, {window.MissingSampleCount} missing{reset}, queue max {window.MaxQueueDepth}, idle {window.IdleSeconds:F1} s)";
    }

    private readonly record struct ProcessKey(int Pid, long Timestamp);

    private sealed class Session(GameSettings game, DateTimeOffset startedAt, long startedTick, long availableRamMib, PlayWarmSnapshot? warm,
        int[] checkpoints, double sampleIntervalSeconds, bool observedAfterGameStart, PlayStreamResult? initialStartStream = null)
    {
        private readonly object _gate = new();
        private readonly string _sessionId = Guid.NewGuid().ToString("N");
        private readonly PlayRecordAccumulator _accumulator = new(checkpoints, sampleIntervalSeconds);
        private readonly List<PlayWindowSummary> _windows = [];
        private string? _requestedEndReason;
        private string _status = "recording";
        private string? _physicalDisk;
        private PlayStreamResult _startStream = initialStartStream ?? new("pending", null, null, null, null);
        private DateTimeOffset? _endedAt;
        private double _finalDurationSeconds;
        private DateTimeOffset? _goneAt; // 프로세스가 사라진 것을 처음 본 시각 (종료 유예 중에만 있음)
        private long _goneTick;
        private PlayWindowSummary? _goneTotal; // 그 시각까지의 누계
        private int _goneWindows;
        private DateTimeOffset _returnedAt = DateTimeOffset.MinValue; // 유예에서 돌아온 마지막 시각

        public GameSettings Game { get; } = game;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public long StartedTick { get; } = startedTick;
        public long AvailableRamMib { get; } = availableRamMib;
        public PlayWarmSnapshot? Warm { get; } = warm;
        public bool ObservedAfterGameStart { get; } = observedAfterGameStart;
        public CancellationTokenSource Stop { get; } = new();
        public Task? SamplingTask { get; set; }
        public DateTimeOffset? EndedAt { get { lock (_gate) { return _endedAt; } } }
        public bool IsUnavailable { get { lock (_gate) { return _status == "unavailable"; } } }

        public void SetPhysicalDisk(string? physicalDisk)
        {
            lock (_gate)
            {
                _physicalDisk ??= physicalDisk;
            }
        }

        public PlaySampleRecord? SetStartStream(PlayStreamResult result, long tick)
        {
            lock (_gate)
            {
                if (_startStream.Status != "pending" && !(_startStream.Status == "skipped" && _startStream.Reason == ExistingGameStreamReason))
                {
                    return null;
                }

                _startStream = result;
                return SnapshotLocked(tick);
            }
        }

        public IReadOnlyList<PlayWindowSummary> AddSample(IReadOnlyList<PlayDiskCounters> previous, IReadOnlyList<PlayDiskCounters> current,
            long previousTick, long tick)
        {
            lock (_gate)
            {
                double previousElapsed = Math.Max(0, Stopwatch.GetElapsedTime(StartedTick, previousTick).TotalSeconds);
                double elapsed = Stopwatch.GetElapsedTime(StartedTick, tick).TotalSeconds;
                IReadOnlyList<PlayWindowSummary> added = _accumulator.Add(previous, current, previousElapsed, elapsed);
                foreach (PlayWindowSummary window in added)
                {
                    _windows.Add(window);
                }

                return added;
            }
        }

        public PlaySampleRecord MarkUnavailable(long tick)
        {
            lock (_gate)
            {
                _status = "unavailable";
                _requestedEndReason ??= "counter_unavailable";
                return SnapshotLocked(tick);
            }
        }

        public void NoteGone(DateTimeOffset at, long tick)
        {
            lock (_gate)
            {
                if (_goneAt is not null || at <= _returnedAt)
                {
                    return; // 처음 본 시각을 유지, 돌아오기 전의 옛 소멸 표시는 되살리지 않음 (틱 스레드와 시작 이벤트 스레드의 경쟁)
                }

                (_goneAt, _goneTick, _goneTotal, _goneWindows) = (at, tick, _accumulator.Total(), _windows.Count);
            }
        }

        public void NoteReturn(DateTimeOffset at)
        {
            lock (_gate)
            {
                (_goneAt, _goneTotal, _returnedAt) = (null, null, at);
            }
        }

        public void RequestEnd(string reason)
        {
            lock (_gate)
            {
                _requestedEndReason = reason;
            }
        }

        public void StopSampling()
        {
            Stop.Cancel();
            try
            {
                SamplingTask?.GetAwaiter().GetResult();
            }
            catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
            {
                // SampleSessionAsync already records provider failures as unavailable.
            }
        }

        public PlaySampleRecord Finish(string reason, DateTimeOffset now, long tick)
        {
            lock (_gate)
            {
                _endedAt = _goneAt ?? now;
                _finalDurationSeconds = Math.Max(0, Stopwatch.GetElapsedTime(StartedTick, _goneAt is null ? tick : _goneTick).TotalSeconds);
                _requestedEndReason = reason;
                if (_status != "unavailable")
                {
                    _status = reason == "game_exit" ? "complete" : "interrupted";
                }

                return SnapshotLocked(tick, final: true);
            }
        }

        public PlaySampleRecord Snapshot(long tick)
        {
            lock (_gate)
            {
                return SnapshotLocked(tick, final: _endedAt is not null);
            }
        }

        private PlaySampleRecord SnapshotLocked(long tick, bool final = false)
        {
            double duration = final ? _finalDurationSeconds : Math.Max(0, Stopwatch.GetElapsedTime(StartedTick, tick).TotalSeconds);
            return new PlaySampleRecord
            {
                SessionId = _sessionId,
                Game = Game.Name,
                StartedAt = StartedAt,
                EndedAt = _endedAt,
                DurationSeconds = duration,
                Status = _status,
                EndReason = _requestedEndReason,
                ObservedAfterGameStart = ObservedAfterGameStart,
                PhysicalDisk = _physicalDisk,
                AvailableRamMib = AvailableRamMib,
                StartWarm = Warm,
                StartStream = _startStream,
                Windows = [.. final && _goneAt is not null ? _windows.Take(_goneWindows) : _windows],
                Total = final ? _goneTotal ?? _accumulator.Total() : null,
            };
        }
    }

    internal sealed class PlayRecordAccumulator
    {
        private const long LongRead100ns = 1_000_000; // 100 ms
        private readonly int[] _checkpoints;
        private readonly double _sampleIntervalSeconds;
        private readonly HashSet<int> _recorded = [];
        private long _readBytes;
        private ulong _readCount;
        private long _readTime100ns;
        private long _idleTime100ns;
        private int _slowSamples;
        private int _slowStreak;
        private int _longestSlowStreak;
        private int _sampleCount;
        private int _missingSampleCount;
        private double _measuredAtSeconds;
        private bool _lastCounterReset;
        private uint _maxQueueDepth;

        public PlayRecordAccumulator(int[] checkpoints, double sampleIntervalSeconds)
        {
            _checkpoints = checkpoints;
            _sampleIntervalSeconds = Math.Max(0.001, sampleIntervalSeconds);
        }

        public IReadOnlyList<PlayWindowSummary> Add(IReadOnlyList<PlayDiskCounters> previous, IReadOnlyList<PlayDiskCounters> current,
            double previousElapsedSeconds, double elapsedSeconds)
        {
            _sampleCount++;
            _measuredAtSeconds = Math.Max(0, elapsedSeconds);
            int missingExpected = MissingExpectedSamples(previousElapsedSeconds, elapsedSeconds);
            _missingSampleCount = checked(_missingSampleCount + missingExpected);

            Dictionary<uint, PlayDiskCounters> previousByDisk = [];
            bool reset = false;
            foreach (PlayDiskCounters sample in previous)
            {
                if (!previousByDisk.TryAdd(sample.DiskNumber, sample))
                {
                    reset = true;
                }
            }

            long bytes = 0;
            ulong reads = 0;
            long readTime = 0;
            long idleTime = 0;
            uint queueDepth = 0;
            HashSet<uint> seen = [];
            foreach (PlayDiskCounters sample in current)
            {
                if (!seen.Add(sample.DiskNumber) || !previousByDisk.TryGetValue(sample.DiskNumber, out PlayDiskCounters before)
                    || !TryDelta(before, sample, out long diskBytes, out ulong diskReads, out long diskReadTime, out long diskIdleTime))
                {
                    reset = true;
                    continue;
                }

                bytes = checked(bytes + diskBytes);
                reads = checked(reads + diskReads);
                readTime = checked(readTime + diskReadTime);
                idleTime = checked(idleTime + diskIdleTime);
                queueDepth = checked(queueDepth + sample.QueueDepth);
            }

            if (seen.Count != previousByDisk.Count)
            {
                reset = true;
            }

            _readBytes = checked(_readBytes + bytes);
            _readCount = checked(_readCount + reads);
            _readTime100ns = checked(_readTime100ns + readTime);
            _idleTime100ns = checked(_idleTime100ns + idleTime);
            _maxQueueDepth = Math.Max(_maxQueueDepth, queueDepth);

            if (reset)
            {
                _missingSampleCount = checked(_missingSampleCount + 1);
                _slowStreak = 0;
            }
            else if (reads >= 5 && (double)readTime / reads >= LongRead100ns)
            {
                _slowSamples++;
                _slowStreak = missingExpected > 0 ? 1 : _slowStreak + 1;
                _longestSlowStreak = Math.Max(_longestSlowStreak, _slowStreak);
            }
            else
            {
                _slowStreak = 0;
            }

            _lastCounterReset = reset;

            List<PlayWindowSummary> added = [];
            foreach (int checkpoint in _checkpoints)
            {
                if (elapsedSeconds >= checkpoint && _recorded.Add(checkpoint))
                {
                    bool delayed = elapsedSeconds - checkpoint > _sampleIntervalSeconds / 2;
                    added.Add(Snapshot(checkpoint, elapsedSeconds, delayed, reset));
                }
            }

            return added;
        }

        public PlayWindowSummary Total() => Snapshot(null, _measuredAtSeconds, delayed: false, counterReset: _lastCounterReset);

        private PlayWindowSummary Snapshot(int? seconds, double measuredAtSeconds, bool delayed, bool counterReset) =>
            new(seconds, measuredAtSeconds, _readBytes, _readCount, _readTime100ns / 10_000_000d, _slowSamples, _longestSlowStreak,
                _sampleCount, _missingSampleCount, delayed, counterReset, _idleTime100ns / 10_000_000d, _maxQueueDepth);

        private int MissingExpectedSamples(double previousElapsedSeconds, double elapsedSeconds)
        {
            double interval = elapsedSeconds - previousElapsedSeconds;
            if (interval <= _sampleIntervalSeconds * 1.5)
            {
                return 0;
            }

            double expected = Math.Round(interval / _sampleIntervalSeconds, MidpointRounding.AwayFromZero);
            return expected >= int.MaxValue ? int.MaxValue : Math.Max(0, (int)expected - 1);
        }

        private static bool TryDelta(PlayDiskCounters previous, PlayDiskCounters current, out long bytes, out ulong reads, out long readTime, out long idleTime)
        {
            bytes = 0;
            reads = 0;
            readTime = 0;
            idleTime = 0;
            if (previous.ReadBytes < 0 || current.ReadBytes < 0 || current.ReadBytes < previous.ReadBytes
                || previous.ReadTime100ns < 0 || current.ReadTime100ns < previous.ReadTime100ns
                || previous.IdleTime100ns < 0 || current.IdleTime100ns < previous.IdleTime100ns)
            {
                return false;
            }

            bytes = current.ReadBytes - previous.ReadBytes;
            reads = current.ReadCount >= previous.ReadCount
                ? current.ReadCount - previous.ReadCount
                : (ulong)uint.MaxValue - previous.ReadCount + current.ReadCount + 1;
            readTime = current.ReadTime100ns - previous.ReadTime100ns;
            idleTime = current.IdleTime100ns - previous.IdleTime100ns;
            return true;
        }
    }

    private sealed class WindowsPlayDiskCounter(DiskPerformanceMonitor monitor) : IPlayDiskCounter
    {
        public string? PhysicalDisk => monitor.PhysicalKey;
        public IReadOnlyList<PlayDiskCounters> Initial => monitor.Initial.Select(Convert).ToArray();
        public IReadOnlyList<PlayDiskCounters>? Read() => monitor.Read()?.Select(Convert).ToArray();
        public void Dispose() => monitor.Dispose();
        private static PlayDiskCounters Convert(DiskPerformanceSample sample) =>
            new(sample.DiskNumber, sample.ReadBytes, sample.ReadCount, sample.ReadTime100ns, sample.IdleTime100ns, sample.QueueDepth);

        public static IPlayDiskCounter? Open(string path) => DiskInfo.OpenPerformanceMonitor(path) is { } monitor ? new WindowsPlayDiskCounter(monitor) : null;
    }

    internal sealed class PlaySamplesStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
        };

        private sealed class Document
        {
            public Dictionary<string, List<PlaySampleRecord>> Games { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private readonly object _gate = new();
        private readonly string? _path;
        private readonly ILog _log;
        private readonly Document _document;
        private readonly bool _persist;

        public PlaySamplesStore(string? path, ILog log)
        {
            _path = path;
            _log = log;
            (_document, _persist) = Load(path, log);
        }

        public void Upsert(PlaySampleRecord record)
        {
            lock (_gate)
            {
                if (!_document.Games.TryGetValue(record.Game, out List<PlaySampleRecord>? samples))
                {
                    samples = [];
                    _document.Games.Add(record.Game, samples);
                }

                int index = samples.FindIndex(s => s.SessionId == record.SessionId);
                if (index >= 0)
                {
                    PlaySampleRecord existing = samples[index];
                    bool replace = IsNewer(record, existing);
                    PlaySampleRecord latest = replace ? record : existing;
                    PlaySampleRecord other = replace ? existing : record;
                    if (latest.StartStream.Status == "pending" && other.StartStream.Status != "pending")
                    {
                        latest.StartStream = other.StartStream;
                    }

                    if (replace)
                    {
                        samples[index] = latest;
                    }
                }
                else
                {
                    samples.Add(record);
                }

                if (samples.Count > MaxSamplesPerGame)
                {
                    samples.RemoveRange(0, samples.Count - MaxSamplesPerGame);
                }

                FlushLocked();
            }
        }

        private static bool IsNewer(PlaySampleRecord incoming, PlaySampleRecord existing)
        {
            if (existing.EndedAt is { } existingEnd)
            {
                return incoming.EndedAt is { } incomingEnd && incomingEnd >= existingEnd;
            }

            return incoming.EndedAt is not null || incoming.DurationSeconds >= existing.DurationSeconds;
        }

        private void FlushLocked()
        {
            if (!_persist || _path is null)
            {
                return;
            }

            try
            {
                string tmp = _path + ".tmp";
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(_document, JsonOptions);
                DurableFile.WriteAllBytes(tmp, bytes);
                File.Move(tmp, _path, true);
            }
            catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
            {
                _log.Warn("play samples not written: " + e.GetType().Name);
            }
        }

        private static (Document Document, bool Persist) Load(string? path, ILog log)
        {
            if (path is null || !File.Exists(path))
            {
                return (new Document(), true);
            }

            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                Document document = JsonSerializer.Deserialize<Document>(stream, JsonOptions) ?? new Document();
                if (document.Games is null || document.Games.Any(pair => pair.Value is null || pair.Value.Any(sample => sample is null)))
                {
                    throw new JsonException("play samples document has invalid games");
                }

                Dictionary<string, List<PlaySampleRecord>> games = new(StringComparer.OrdinalIgnoreCase);
                foreach ((string name, List<PlaySampleRecord> samples) in document.Games)
                {
                    games[name] = samples.TakeLast(MaxSamplesPerGame).ToList();
                }

                document.Games = games;
                return (document, true);
            }
            catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
            {
                log.Warn("play samples not read, not overwritten: " + e.GetType().Name);
                return (new Document(), false);
            }
        }
    }
}
