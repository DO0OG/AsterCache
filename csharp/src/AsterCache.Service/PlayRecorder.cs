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

internal readonly record struct PlayDiskCounters(uint DiskNumber, long ReadBytes, uint ReadCount, long ReadTime100ns, long IdleTime100ns, uint QueueDepth,
    long WriteBytes = 0, uint WriteCount = 0, long WriteTime100ns = 0);

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
    uint MaxQueueDepth,
    long FrameCount = 0,
    double MaxFrameMs = 0,
    double PeriodMaxFrameMs = 0, // 직전 검사점 뒤부터 이 검사점까지의 가장 긴 간격 (구간별 값을 누계의 차로 구할 수 없어서 따로 둠)
    int Over50Ms = 0,
    int Over100Ms = 0,
    int Pauses = 0, // 1 초 이상 걸린 간격 (최소화·로딩 멈춤 등): 끊김 수와 따로 셈
    double MaxPauseMs = 0, // 가장 긴 멈춤 (ms)
    double PeriodMaxPauseMs = 0); // 직전 검사점 뒤부터 이 검사점까지의 가장 긴 멈춤

/// 서비스 자신의 시스템 값 (프로세스를 열지 않는 값): 전체 CPU 사용률과 그래픽 메모리 전용 사용량 합계 (MiB). 못 읽은 값은 null.
internal readonly record struct PlaySystemSample(double? CpuPercent, int? GpuDedicatedMib);

internal interface IPlaySystemProbe : IDisposable
{
    PlaySystemSample Read();
}

/// 끊김이 있던 초 한 칸: 시작 뒤 초 T 의 프레임 값과 같은 초의 디스크 읽기 횟수·평균 읽기 시간, 그 초의 시스템 값. 가장 긴 간격이 50 ms 이상이거나 디스크 쪽 느린 초일 때만 담음.
/// DiskReads·DiskMeanReadMs 는 그 초에 디스크 표본이 없으면 null (읽기 0 건과 구별), Pauses 는 그 초에 끝난 1 초 이상 간격 수와 가장 긴 값 (ms).
internal sealed record PlayFrameSecond(int T, double MaxMs, int Over50Ms, int Over100Ms, int Frames, ulong? DiskReads, double? DiskMeanReadMs, double? CpuPercent, int? GpuDedicatedMib, int Pauses = 0, double PauseMaxMs = 0);

/// 같은 세션에서 이후에 뜬 실행 파일 하나의 시작 스트림 결과와 그 시작 기준 +30/+60/+120 초 창 (기존 창과 같은 요약 형식).
internal sealed class PlayStageStart
{
    public string Exe { get; set; } = string.Empty;
    public double OffsetSeconds { get; set; }
    public PlayStreamResult Stream { get; set; } = new("pending", null, null, null, null);
    public List<PlayWindowSummary> Windows { get; set; } = [];

    public PlayStageStart Copy() => new() { Exe = Exe, OffsetSeconds = OffsetSeconds, Stream = Stream, Windows = [.. Windows] };
}

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
    public long? MinAvailableRamMib { get; set; } // 시작 뒤 GameFootprint.WindowSeconds 안의 최소 가용 RAM (몫 학습용)
    public PlayWarmSnapshot? StartWarm { get; set; }
    public PlayStreamResult StartStream { get; set; } = new("pending", null, null, null, null);
    public List<PlayWindowSummary> Windows { get; set; } = [];
    public PlayWindowSummary? Total { get; set; }
    public List<PlayStageStart> StageStarts { get; set; } = [];
    public List<PlayPeriodSummary> Periods { get; set; } = [];
    public string? FrameStatus { get; set; } // 프레임을 재지 못했으면 이유 한 단어 (off, blocked, unavailable, no_events), 쟀으면 ok, 기능이 없던 옛 기록은 null
    public long? FrameEventsLost { get; set; } // 세션이 버퍼 부족으로 잃은 Present 이벤트 수
    public long? FrameEventsLate { get; set; } // 확정된 초에 늦게 도착해 버린 이벤트 수 (0 이 아니면 그 이벤트가 끝내는 간격이 빠짐)
    public long? FrameEventsOutOfOrder { get; set; } // 더 늦은 시각 이벤트보다 뒤에 도착한 이벤트 수 (간격은 이벤트 시각 순서로 셈)
    public double? FrameStoppedAtSeconds { get; set; } // 프레임 측정이 도중에 멈춘 시각 (시작 뒤 초): frameStatus 가 blocked 이면 이 시각 뒤가 blocked
    public List<PlayFrameSecond>? FrameSeconds { get; set; } // 끊김이 있던 초 목록 (상한 PlayRecorder.MaxFrameSeconds)
}

/// 게임 시작 때부터 물리 디스크의 누적 통계를 읽어 구간별 표본과 종료 요약을 남깁니다.
/// 카운터는 시스템 통계만 질의하며 게임 프로세스나 설치 파일을 열지 않습니다.
public sealed class PlayRecorder : IDisposable
{
    public const int MaxSamplesPerGame = 50;
    public static readonly int[] CheckpointSeconds = [30, 60, 120, 300, 600];
    public static readonly int[] StageCheckpointSeconds = [30, 60, 120];
    public const int MaxStageStarts = 20;
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
    private readonly int[] _stageCheckpoints;
    private readonly int _periodSeconds;
    private readonly PlaySamplesStore _store;
    private readonly ILog _log;
    private bool _disposed;
    private const string ExistingGameStreamReason = "game already running when observation began";
    public const int MaxFrameSeconds = 300;
    public const int MaxFrameSecondsPerPeriod = 50; // 10 분 구간마다의 상한: 앞 구간의 로딩 때문에 뒤 구간의 경기 중 끊김이 지워지지 않게 함
    public const int FrameSettleSeconds = 5; // ETW 전달 지연 (유휴 PC 에서 60 초 동안 최대 1.46 초 실측, FlushTrace 500 ms 주기) 의 3 배 넘는 여유: 게임이 CPU 를 쓰는 중에는 더 늦을 수 있어 늦게 온 이벤트는 따로 세어 기록함
    public const int NoEventsSeconds = 30;
    private const double FrameLogMinMs = 50;
    private readonly Func<int, long, IFrameSource>? _openFrames;
    private readonly Func<bool> _frameEnabled;
    private readonly Func<GameSettings, string?> _frameBlocker;
    private readonly Func<IPlaySystemProbe?>? _openSystem;
    private readonly Func<IPlaySeriesProbe?>? _openSeries;
    private readonly string? _seriesDirectory;
    private readonly Func<bool> _seriesEnabled;
    private readonly Func<GameSettings, bool>? _isTraceBlocked;
    private readonly Func<bool>? _blockedGameRunning;
    private readonly bool[] _frameSlots = new bool[FrameTimeSession.MaxSlots];

    /// 게임별 로딩 판정의 지금 상태: 표본 루프가 쓰고 서비스의 디스크 읽기 경로가 읽습니다 (게임이 도는 동안 로딩이면 그 경로의 디스크 읽기를 줄임).
    internal LoadPhaseBoard LoadPhases { get; }

    internal PlayRecorder(string? path, ILog log, Func<string, IPlayDiskCounter?>? openCounter = null, Func<long>? availableRamMib = null,
        TimeSpan? sampleInterval = null, Func<DateTimeOffset>? now = null, Func<long>? timestamp = null, IReadOnlyList<int>? checkpoints = null,
        IReadOnlyList<int>? stageCheckpoints = null, int periodSeconds = PlayPeriods.DefaultSeconds,
        Func<int, long, IFrameSource>? openFrames = null, Func<bool>? frameEnabled = null, Func<GameSettings, string?>? frameBlocker = null, Func<IPlaySystemProbe?>? openSystem = null,
        Func<GameSettings, bool>? isTraceBlocked = null, Func<bool>? blockedGameRunning = null,
        Func<IPlaySeriesProbe?>? openSeries = null, string? seriesDirectory = null, Func<bool>? seriesEnabled = null)
    {
        _openSeries = openSeries; // 플레이의 초 단위 기록: 프레임 세션이 열린 (안티치트 게이트를 지난) 플레이에서만 남김
        _seriesDirectory = seriesDirectory;
        _seriesEnabled = seriesEnabled ?? (() => true);
        _isTraceBlocked = isTraceBlocked; // 안티치트 게이트가 막는 게임인가 (다른 게임의 시작에 열려 있는 세션을 닫는 데 씀)
        _blockedGameRunning = blockedGameRunning; // 게이트가 막는 등록 게임의 프로세스가 지금 실행 중인가 (실행 중이면 세션을 열지 않음)
        _openFrames = openFrames;
        _frameEnabled = frameEnabled ?? (() => true);
        _frameBlocker = frameBlocker ?? (_ => "unknown"); // 게이트 판정이 없으면 막힌 것으로 (안티치트 게이트를 거치지 않고는 세션을 열지 않음)
        _openSystem = openSystem;
        _log = log;
        LoadPhases = new LoadPhaseBoard(log);
        _store = new PlaySamplesStore(path, log);
        _openCounter = openCounter ?? WindowsPlayDiskCounter.Open;
        _availableRamMib = availableRamMib ?? SystemMemory.AvailableMib;
        _sampleInterval = sampleInterval ?? TimeSpan.FromSeconds(1);
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _timestamp = timestamp ?? Stopwatch.GetTimestamp;
        _checkpoints = checkpoints is null ? [.. CheckpointSeconds] : [.. checkpoints.Order()];
        _stageCheckpoints = stageCheckpoints is null ? [.. StageCheckpointSeconds] : [.. stageCheckpoints.Order()];
        _periodSeconds = Math.Max(1, periodSeconds);
    }

    /// 시작 이벤트 시각으로 세션을 만듭니다. 같은 게임의 후속 exe 는 현재 세션에 연결합니다.
    internal bool ObserveStart(GameSettings game, ProcessStart start, PlayWarmSnapshot? warm)
    {
        StopFramesIfBlocked(game);
        ProcessKey key = new(start.Pid, start.Timestamp);
        Session? reopen = null;
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
                session = new Session(game, _now(), startedTick, ram, warm, _checkpoints, _sampleInterval.TotalSeconds, observedAfterGameStart: false, stageCheckpoints: _stageCheckpoints, periodSeconds: _periodSeconds);
                _active.Add(game.Name, session);
                session.AddProcess(start.Pid);
                session.NoteExeStart();
                session.SamplingTask = Task.Run(() => SampleSessionAsync(session));
            }
            else
            {
                reopen = session.NoteReturn(_now()) ? session : null; // 유예 중에 같은 게임의 다른 exe 가 뜸: 종료가 아님 (종료 때 닫은 프레임 세션은 다시 엶)
                session.AddStageStart(start.ExeName, key, start.Timestamp > 0 ? start.Timestamp : _timestamp());
                session.AddProcess(start.Pid);
                session.NoteExeStart();
            }

            _processSessions.Add(key, session);
        }

        if (reopen is not null)
        {
            OpenFrames(reopen);
        }

        return true;
    }

    /// 게임이 실행 뒤 잡는 메모리 (몫, MiB) 와 그 값을 낸 플레이 수: 최근 플레이 기록의 중앙값, 쓸 기록이 없으면 null.
    internal (long Mib, int Plays)? Footprint(string game) => GameFootprint.Median(_store.Samples(game));

    /// 서비스 시작이나 설정 재적재 때 이미 실행 중인 게임의 관측을 시작합니다.
    internal bool ObserveExisting(GameSettings game, IReadOnlyList<int>? pids = null)
    {
        StopFramesIfBlocked(game); // 설정을 다시 읽어 막히게 바뀐 게임도 여기서 잡힘
        Session? reopen = null;
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            if (_active.TryGetValue(game.Name, out Session? current))
            {
                reopen = current.NoteReturn(_now()) ? current : null; // 매 틱 실행 중인 게임마다 불림: 유예에서 돌아온 세션의 소멸 표시를 지움
                foreach (int pid in pids ?? [])
                {
                    current.AddProcess(pid); // 뒤늦게 뜬 등록 실행 파일도 프레임 집계 대상으로 (이미 있으면 무시)
                }
            }
        }

        if (reopen is not null)
        {
            OpenFrames(reopen);
        }

        lock (_gate)
        {
            if (_disposed || _active.ContainsKey(game.Name))
            {
                return false;
            }

            long tick = _timestamp();
            var skippedStream = new PlayStreamResult("skipped", ExistingGameStreamReason, null, null, null);
            var session = new Session(game, _now(), tick, SafeAvailableRam(), null, _checkpoints, _sampleInterval.TotalSeconds,
                observedAfterGameStart: true, initialStartStream: skippedStream, stageCheckpoints: _stageCheckpoints, periodSeconds: _periodSeconds);
            _active.Add(game.Name, session);
            foreach (int pid in pids ?? [])
            {
                session.AddProcess(pid);
            }

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

        List<Session> gone = [];
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
                    gone.Add(session);
                }
            }
        }

        foreach (Session session in gone)
        {
            CloseFramesNow(session, blocked: false, atExit: true); // 게임 프로세스가 끝났으면 종료 유예 (60~90 초) 를 기다리지 않고 바로 닫음
        }
    }

    /// 안티치트 게이트가 막는 게임이 시작했거나 실행 중이면 열려 있는 프레임 세션을 모두 닫고 그 뒤를 blocked 로 기록합니다 (게이트 판정은 기존 함수 그대로).
    private void StopFramesIfBlocked(GameSettings game)
    {
        if (_openFrames is null)
        {
            return;
        }

        Session[] open;
        lock (_gate)
        {
            open = [.. _active.Values.Where(s => s.HasFrames)];
        }

        if (open.Length == 0 || _isTraceBlocked?.Invoke(game) != true)
        {
            return; // 열린 세션이 없으면 판정을 부르지 않음 (매 틱 불리므로)
        }

        foreach (Session session in open)
        {
            CloseFramesNow(session, blocked: true, atExit: false);
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

        if (session.SetStartStream(new ProcessKey(start.Pid, start.Timestamp), result, _timestamp()) is { } record)
        {
            _store.Upsert(record);
        }
    }

    /// 게임 종료 유예가 끝났을 때 세션을 끝냅니다.
    internal void EndSession(string game, string reason = "game_exit", DateTimeOffset? exitedAt = null)
    {
        Session? session;
        lock (_gate)
        {
            if (!_active.TryGetValue(game, out session) || exitedAt is { } at && at < session.StartedAt)
            {
                return;
            }

            _active.Remove(game);
            session.RequestEnd(reason);
        }

        session.StopSampling();
        PlaySampleRecord record = session.Finish(reason, _now(), _timestamp(), exitedAt);
        _store.Upsert(record);
        _log.Info(Format(record));
        if (record.Periods.Count > 0)
        {
            _log.Info($"{record.Game}: play record periods: {PlayPeriods.Format(record.Periods, record.FrameStatus, record.FrameStoppedAtSeconds)}");
        }
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
        IPlaySystemProbe? system = null;
        PlaySeriesRecorder? series = null;
        bool seriesTried = false;
        IReadOnlyList<PlayDiskCounters> previous = [];
        long previousTick = 0;
        bool hasBaseline = false;
        var loadFeed = new LoadFeed(session.Game.Name, session.Game.OriginRoot, LoadPhases);
        LoadState load = LoadState.Unknown;
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
            OpenFrames(session);
            system = session.HasFrames ? OpenSystem() : null;

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

                session.NoteAvailable(SafeAvailableRam(), tick);
                if (!seriesTried && session.HasFrames)
                {
                    (seriesTried, series) = (true, OpenSeries(session));
                }

                PlaySeriesRaw? raw = session.HasFrames && session.HasSeries ? ReadSeries(series, session) : null;
                load = NoteLoad(loadFeed, session, previous, current, tick, raw);
                foreach (PlayWindowSummary checkpoint in session.AddSample(previous, current, previousTick, tick, ReadSystem(system), raw, load, loadFeed.LastServiceBytes))
                {
                    PlaySampleRecord record = session.Snapshot(tick);
                    _store.Upsert(record);
                    if (checkpoint.Seconds == 600)
                    {
                        _log.Info(Format(record));
                    }
                }

                PersistStage(session, tick);
                if (session.HasFrames && session.HasSeries)
                {
                    TickSeries(series, Stopwatch.GetElapsedTime(session.StartedTick, tick).TotalSeconds);
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
            LoadPhases.Forget(session.Game.Name); // 표본이 끊긴 뒤에는 로딩 상태를 남기지 않음 (서비스의 일을 계속 막지 않게)
            if (counter is not null && hasBaseline && !session.IsUnavailable)
            {
                try
                {
                    IReadOnlyList<PlayDiskCounters>? final = counter.Read();
                    if (final is { Count: > 0 })
                    {
                        long tick = _timestamp();
                        foreach (PlayWindowSummary checkpoint in session.AddSample(previous, final, previousTick, tick, ReadSystem(system), session.HasFrames && session.HasSeries ? ReadSeries(series, session) : null, load))
                        {
                            PlaySampleRecord record = session.Snapshot(tick);
                            _store.Upsert(record);
                            if (checkpoint.Seconds == 600)
                            {
                                _log.Info(Format(record));
                            }
                        }

                        PersistStage(session, tick);
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
                system?.Dispose();
            }
            catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
            {
                // 성능 카운터 정리 실패는 세션이나 서비스를 중단시키지 않습니다.
            }

            CloseFrames(session);
            CloseSeries(series);
        }
    }

    /// 플레이의 초 단위 기록을 엽니다: 설정이 꺼졌거나 쓸 폴더·조회가 없으면 null. 어떤 실패도 디스크 기록을 막지 않습니다.
    private PlaySeriesRecorder? OpenSeries(Session session)
    {
        if (_openSeries is null || _seriesDirectory is null || !_seriesEnabled())
        {
            return null;
        }

        IPlaySeriesProbe? probe = null;
        try
        {
            probe = _openSeries();
            if (probe is null)
            {
                return null;
            }

            var recorder = new PlaySeriesRecorder(probe, new PlaySeriesFile(_seriesDirectory, session.Game.Name, session.StartedAt, _log, _now));
            session.AttachSeries(recorder, e => _log.Warn($"{session.Game.Name}: play series stopped ({e.GetType().Name})")); // 초 단위 기록의 실패는 그 플레이의 초 단위 기록만 멈춤
            return recorder;
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            probe?.Dispose();
            return null;
        }
    }

    // 이 초의 로딩 판정: 입력을 못 만들면 (조회 실패) 모름으로 두고 표본 루프는 계속 돎
    private static LoadState NoteLoad(LoadFeed feed, Session session, IReadOnlyList<PlayDiskCounters> previous, IReadOnlyList<PlayDiskCounters> current, long tick, PlaySeriesRaw? raw)
    {
        try
        {
            return feed.Next(previous, current, Stopwatch.GetElapsedTime(session.StartedTick, tick).TotalSeconds, raw, session.TakeExeStart());
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            return LoadState.Unknown;
        }
    }

    private static PlaySeriesRaw? ReadSeries(PlaySeriesRecorder? series, Session session)
    {
        try
        {
            return series?.Read(session.PidsSnapshot());
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            return null; // 이 초의 시스템 값만 비움
        }
    }

    // 우선순위별 대기 목록 (30 초마다) 과 쌓인 줄 쓰기 (분당 한 번): 실패해도 표본 루프를 멈추지 않음
    private static void TickSeries(PlaySeriesRecorder? series, double elapsedSeconds)
    {
        try
        {
            series?.SampleLists(elapsedSeconds);
            series?.FlushIfDue();
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            // 기록 실패는 세션이나 서비스를 중단시키지 않습니다.
        }
    }

    private static void CloseSeries(PlaySeriesRecorder? series)
    {
        try
        {
            series?.Dispose();
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            // 기록 실패는 세션이나 서비스를 중단시키지 않습니다.
        }
    }

    /// 프레임 세션을 열지 말지 정합니다: 설정이 꺼졌으면 off, 자동 트레이스를 막는 안티치트 판정이면 blocked (열지 않음), 열기에 실패하면 unavailable. 어느 쪽이든 디스크 기록은 그대로 갑니다.
    private void OpenFrames(Session session)
    {
        if (_openFrames is null)
        {
            return;
        }

        int slot = -1;
        try
        {
            if (!_frameEnabled())
            {
                session.SetFrameStatus("off");
                return;
            }

            if (_frameBlocker(session.Game) is not null || _blockedGameRunning?.Invoke() == true)
            {
                session.SetFrameStatus("blocked"); // 게이트가 막는 게임이거나, 막는 등록 게임이 지금 실행 중이면 열지 않음
                return;
            }

            lock (_gate)
            {
                slot = Array.IndexOf(_frameSlots, false);
                if (slot >= 0)
                {
                    _frameSlots[slot] = true;
                }
            }

            if (slot < 0)
            {
                throw new InvalidOperationException("no free frame session slot");
            }

            IFrameSource opened = _openFrames(slot, session.StartedTick);
            if (!session.AttachFrames(opened, slot, _timestamp()))
            {
                opened.Dispose(); // 그 사이 세션이 끝남: 남는 ETW 세션이 없게 바로 닫고 칸을 돌려줌
                ReleaseFrameSlot(slot);
                return;
            }

            if (_blockedGameRunning?.Invoke() == true)
            {
                CloseFramesNow(session, blocked: true, atExit: false); // 검사와 붙이기 사이에 막는 게임이 시작했으면 바로 닫음
            }
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            ReleaseFrameSlot(slot);
            session.SetFrameStatus("unavailable");
            _log.Warn($"{session.Game.Name}: frame times unavailable ({e.GetType().Name})");
        }
    }

    private void CloseFrames(Session session) => CloseFramesNow(session, blocked: false, atExit: false);

    /// 프레임 세션을 닫습니다 (이미 닫혔으면 아무것도 안 함). blocked 면 그 뒤를 blocked 로 기록하고, atExit 면 게임이 다시 뜰 때 다시 열 수 있게 표시합니다.
    private void CloseFramesNow(Session session, bool blocked, bool atExit)
    {
        IFrameSource? source = null;
        int slot = -1;
        try
        {
            session.FlushFrames(); // 남은 초도 확정 (더 기다리지 않음)
            (source, slot) = session.DetachFrames(_timestamp());
            source?.Dispose();
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            // 프레임 세션 정리 실패는 세션이나 서비스를 중단시키지 않습니다.
        }

        ReleaseFrameSlot(slot);
        if (source is null)
        {
            return;
        }

        if (blocked)
        {
            session.MarkFramesBlocked(_timestamp());
            _log.Info($"{session.Game.Name}: frame times stopped (anti-cheat game started)");
        }
        else if (atExit)
        {
            session.MarkFramesClosedAtExit();
        }
    }

    private void ReleaseFrameSlot(int slot)
    {
        if (slot >= 0)
        {
            lock (_gate)
            {
                _frameSlots[slot] = false;
            }
        }
    }

    private IPlaySystemProbe? OpenSystem()
    {
        try
        {
            return _openSystem?.Invoke();
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            return null;
        }
    }

    private static PlaySystemSample ReadSystem(IPlaySystemProbe? probe)
    {
        try
        {
            return probe?.Read() ?? default;
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            return default;
        }
    }

    private void Persist(PlaySampleRecord record) => _store.Upsert(record);

    // 이후 실행 파일의 창이 새로 채워졌으면 기록하고, +120 초 창까지 채운 시작은 한 줄로 남김
    private void PersistStage(Session session, long tick)
    {
        if (session.TakeStageUpdate(out List<(string Game, PlayStageStart Start)> finished))
        {
            _store.Upsert(session.Snapshot(tick));
        }

        foreach ((string game, PlayStageStart stage) in finished)
        {
            _log.Info(FormatStage(game, stage));
        }
    }

    private static string FormatStage(string game, PlayStageStart stage) =>
        $"{game}: play record stage {stage.Exe}: {string.Concat("stage", FormatStream(stage.Stream).AsSpan(5))}; {string.Join("; ", stage.Windows.Select(w => FormatWindow(w, $"{w.Seconds}s")))}";

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
        parts.Add($"start RAM {record.AvailableRamMib} MiB" + (record.MinAvailableRamMib is { } min ? $", min RAM {min} MiB" : string.Empty));
        parts.Add(FormatStream(record.StartStream));
        foreach (PlayWindowSummary window in record.Windows)
        {
            parts.Add(FormatWindow(window, $"{window.Seconds}s", record.FrameStatus == "blocked" && window.MeasuredAtSeconds <= record.FrameStoppedAtSeconds ? "ok" : record.FrameStatus));
        }

        PlayWindowSummary? total = record.Total ?? record.Windows.LastOrDefault(w => w.Seconds == 600);
        string duration = $"total {(int)Math.Round(record.DurationSeconds / 60d, MidpointRounding.AwayFromZero)} min";
        if (total is not null)
        {
            duration += ", " + FormatWindow(total, "disk read", record.FrameStatus == "blocked" && record.FrameStoppedAtSeconds is not null ? "ok" : record.FrameStatus); // 도중에 멈췄으면 합계는 멈춘 시각까지의 값
        }

        parts.Add(duration);
        List<string> events = [];
        if (record.FrameEventsLost is > 0)
        {
            events.Add($"lost {record.FrameEventsLost}");
        }

        if (record.FrameEventsLate is > 0)
        {
            events.Add($"late {record.FrameEventsLate}"); // 확정 뒤에 도착해 버린 이벤트: 0 이 아니면 그 이벤트가 끝내는 간격이 빠졌을 수 있음
        }

        if (record.FrameEventsOutOfOrder is > 0)
        {
            events.Add($"out of order {record.FrameEventsOutOfOrder}");
        }

        if (events.Count > 0)
        {
            parts.Add("frame events " + string.Join(", ", events));
        }

        if (record.FrameStoppedAtSeconds is { } stopped)
        {
            parts.Add($"frame times stopped at {stopped:F0} s (anti-cheat game started)");
        }

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

    private static string FormatWindow(PlayWindowSummary window, string label, string? frameStatus = null)
    {
        string measured = window.Seconds is { } seconds
            ? $"{label} measured at {window.MeasuredAtSeconds:F1}s{(window.Delayed ? $" (late {window.MeasuredAtSeconds - seconds:F1}s, boundary missed)" : string.Empty)}"
            : $"{label} through {window.MeasuredAtSeconds:F1}s";
        string reset = window.CounterReset ? ", counter reset interval missing" : string.Empty;
        return $"{measured} {window.ReadBytes / 1_048_576d:F0} MiB/{window.ReadCount} reads/{window.ReadSeconds:F1} s read time/{window.SlowSamples} slow samples (longest {window.LongestSlowSamples}, {window.SampleCount} samples, {window.MissingSampleCount} missing{reset}, queue max {window.MaxQueueDepth}, idle {window.IdleSeconds:F1} s{(frameStatus is null ? string.Empty : ", " + PlayPeriods.FrameText(frameStatus, window.FrameCount, window.MaxFrameMs, window.Over50Ms, window.Over100Ms, window.Pauses, window.MaxPauseMs))})";
    }

    private readonly record struct ProcessKey(int Pid, long Timestamp);

    private sealed class Session(GameSettings game, DateTimeOffset startedAt, long startedTick, long availableRamMib, PlayWarmSnapshot? warm,
        int[] checkpoints, double sampleIntervalSeconds, bool observedAfterGameStart, PlayStreamResult? initialStartStream = null, int[]? stageCheckpoints = null, int periodSeconds = PlayPeriods.DefaultSeconds)
    {
        private readonly object _gate = new();
        private readonly string _sessionId = Guid.NewGuid().ToString("N");
        private readonly PlayRecordAccumulator _accumulator = new(checkpoints, sampleIntervalSeconds);
        private readonly List<PlayWindowSummary> _windows = [];
        private readonly List<LaterStart> _laterStarts = [];
        private readonly PlayRecordAccumulator _periodAccumulator = new(PlayPeriods.Checkpoints(periodSeconds), sampleIntervalSeconds);
        private readonly List<PlayWindowSummary> _periodWindows = [];
        private bool _stageChanged;
        private int _gonePeriodWindows;
        private PlayWindowSummary? _gonePeriodTotal;
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
        private readonly List<int> _pids = [];
        private readonly Dictionary<long, PendingSecond> _pending = []; // 프레임이 확정되기를 기다리는 초의 디스크·시스템 값
        private readonly List<PlayFrameSecond> _frameSeconds = [];
        private IFrameSource? _frames;
        private int _frameSlot = -1;
        private long _framesAttachedTick;
        private string? _frameStatus; // 프레임 세션이 없을 때의 이유 (닫은 뒤에는 마지막 상태)
        private long _frameLost;
        private long _frameLate;
        private long _frameOutOfOrder;
        private double? _frameStoppedAt;
        private bool _framesClosedAtExit;

        private PlaySeriesRecorder? _series; // 초 단위 기록 (없거나 실패해 멈췄으면 null)
        private Action<Exception>? _seriesFailed;
        private bool _exeStarted; // 실행 파일이 새로 떴고 표본 루프가 아직 로딩 판정에 알리지 않음

        private readonly record struct PendingSecond(ulong Reads, double MeanReadMs, bool Slow, PlaySystemSample System, PlaySeriesPoint? Series = null);

        public GameSettings Game { get; } = game;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public long StartedTick { get; } = startedTick;
        public long AvailableRamMib { get; } = availableRamMib;
        private long? _minAvailableMib;

        /// 시작 뒤 GameFootprint.WindowSeconds 안의 가용 RAM 표본 중 최소값을 남깁니다 (0 은 못 읽은 값이라 뺌).
        public void NoteAvailable(long mib, long tick)
        {
            if (mib <= 0 || Stopwatch.GetElapsedTime(StartedTick, tick).TotalSeconds > GameFootprint.WindowSeconds)
            {
                return;
            }

            lock (_gate)
            {
                _minAvailableMib = _minAvailableMib is { } current ? Math.Min(current, mib) : mib;
            }
        }
        public PlayWarmSnapshot? Warm { get; } = warm;
        public bool ObservedAfterGameStart { get; } = observedAfterGameStart;
        public CancellationTokenSource Stop { get; } = new();
        public Task? SamplingTask { get; set; }
        public DateTimeOffset? EndedAt { get { lock (_gate) { return _endedAt; } } }
        public bool IsUnavailable { get { lock (_gate) { return _status == "unavailable"; } } }
        public bool HasFrames { get { lock (_gate) { return _frames is not null; } } }

        /// 게임 프로세스 번호를 더합니다 (서비스가 이미 아는 값). 프레임 세션이 열려 있으면 거기에도.
        public void AddProcess(int pid)
        {
            lock (_gate)
            {
                if (!_pids.Contains(pid))
                {
                    _pids.Add(pid);
                    _frames?.AddProcess(pid);
                }
            }
        }

        /// 실행 파일이 새로 떴음을 알립니다 (다음 표본의 로딩 판정이 로딩 시작으로 봄).
        public void NoteExeStart()
        {
            lock (_gate)
            {
                _exeStarted = true;
            }
        }

        /// 알리지 않은 실행 파일 시작이 있었는지 돌려주고 지웁니다.
        public bool TakeExeStart()
        {
            lock (_gate)
            {
                (bool started, _exeStarted) = (_exeStarted, false);
                return started;
            }
        }

        /// 초 단위 기록을 붙입니다 (표본 루프가 프레임 세션이 열린 뒤 한 번).
        public void AttachSeries(PlaySeriesRecorder series, Action<Exception> onFailed)
        {
            lock (_gate)
            {
                (_series, _seriesFailed) = (series, onFailed);
            }
        }

        public bool HasSeries
        {
            get
            {
                lock (_gate)
                {
                    return _series is not null;
                }
            }
        }

        // 초 단위 기록의 계산·줄 만들기가 실패하면 그 플레이의 초 단위 기록만 멈추고 한 번 알림 (플레이 기록·끊긴 초 목록·디스크 표본은 이어 감). _gate 를 쥔 채 부름.
        private void StopSeries(Exception e)
        {
            Action<Exception>? notify = _seriesFailed;
            (_series, _seriesFailed) = (null, null);
            notify?.Invoke(e);
        }

        /// 지금까지 알려진 게임 프로세스 번호의 사본.
        public int[] PidsSnapshot()
        {
            lock (_gate)
            {
                return [.. _pids];
            }
        }

        public void SetFrameStatus(string status)
        {
            lock (_gate)
            {
                _frameStatus = status;
            }
        }

        /// 프레임 세션을 붙입니다. 이미 끝났거나 끝내라는 요청을 받은 세션에는 붙이지 않고 false (호출자가 방금 연 세션을 바로 닫음): 닫아 줄 표본 루프가 이미 없을 수 있기 때문.
        public bool AttachFrames(IFrameSource source, int slot, long tick)
        {
            lock (_gate)
            {
                if (_requestedEndReason is not null || _endedAt is not null || Stop.IsCancellationRequested)
                {
                    return false;
                }

                (_frames, _frameSlot, _framesAttachedTick) = (source, slot, tick);
                foreach (int pid in _pids)
                {
                    source.AddProcess(pid);
                }

                return true;
            }
        }

        /// 남은 확정 대기 초를 모두 확정합니다 (세션이 끝날 때).
        public void FlushFrames()
        {
            lock (_gate)
            {
                FlushFramesLocked();
            }
        }

        private void FlushFramesLocked()
        {
            if (_frames is { } frames)
            {
                IReadOnlyList<FrameSecond> rest = frames.TakeSettled(long.MaxValue);
                _accumulator.AddFrames(rest);
                _periodAccumulator.AddFrames(rest);
                ApplyFrameSeconds(rest, long.MaxValue);
            }
        }

        /// 종료 때 닫은 프레임 세션을 게임이 다시 뜰 때 다시 열 수 있게 표시합니다.
        public void MarkFramesClosedAtExit()
        {
            lock (_gate)
            {
                _framesClosedAtExit = true;
            }
        }

        /// 안티치트 게임이 시작해 프레임 측정을 멈췄습니다: 그 뒤는 blocked, 앞의 값은 그대로 유효합니다.
        public void MarkFramesBlocked(long tick)
        {
            lock (_gate)
            {
                (_frameStatus, _frameStoppedAt, _framesClosedAtExit) = ("blocked", Math.Max(0, Stopwatch.GetElapsedTime(StartedTick, tick).TotalSeconds), false);
            }
        }

        /// 프레임 세션을 떼어 돌려줍니다 (닫는 것은 호출자). 마지막 상태와 잃은 이벤트 수는 기록에 남깁니다.
        public (IFrameSource? Source, int Slot) DetachFrames(long tick)
        {
            lock (_gate)
            {
                IFrameSource? source = _frames;
                int slot = _frameSlot;
                if (source is not null)
                {
                    _frameStatus = source.AcceptedEvents == 0 ? "no_events" : "ok"; // 끝날 때 이벤트가 하나도 없었으면 30 초가 안 됐어도 0 프레임 정상과 구분함
                    (_frameLost, _frameLate, _frameOutOfOrder) = (_frameLost + source.EventsLost, _frameLate + source.EventsLate, _frameOutOfOrder + source.EventsOutOfOrder); // 닫았다 다시 열어도 앞의 수를 잃지 않음
                    (_frames, _frameSlot) = (null, -1);
                }

                return (source, slot);
            }
        }

        private string? FrameStatusLocked(long tick) => _frames is null ? _frameStatus
            : _frames.AcceptedEvents == 0 && Stopwatch.GetElapsedTime(_framesAttachedTick, tick).TotalSeconds >= NoEventsSeconds ? "no_events" : "ok";

        // 목록이 넘칠 때 버리는 순서의 기준: 1 초 미만 간격의 최대와 1 초 이상 멈춤의 길이 중 큰 쪽 (경기 중 긴 멈춤이 먼저 버려지지 않게)
        private static double Severity(PlayFrameSecond e) => Math.Max(e.MaxMs, e.PauseMaxMs);

        /// 확정된 초마다 끊김이 있던 초 목록에 담을지 정합니다: 프레임의 가장 긴 간격이 50 ms 이상이거나 같은 초가 디스크 쪽 느린 초일 때.
        private void ApplyFrameSeconds(IReadOnlyList<FrameSecond> frames, long through)
        {
            Dictionary<long, FrameSecond> byT = frames.ToDictionary(f => f.Second);
            SortedSet<long> seconds = [.. _pending.Keys.Where(t => t <= through), .. byT.Keys];
            foreach (long t in seconds)
            {
                byT.TryGetValue(t, out FrameSecond frame);
                bool hasDisk = _pending.Remove(t, out PendingSecond disk);
                try
                {
                    _series?.AddSecond(t, frame, disk.Series); // 모든 초를 초 단위 기록에 (끊긴 초 목록과 따로)
                }
                catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
                {
                    StopSeries(e);
                }

                if (frame.MaxMs < FrameLogMinMs && !disk.Slow && frame.Pauses == 0)
                {
                    continue;
                }

                _frameSeconds.Add(new PlayFrameSecond((int)t, frame.MaxMs, frame.Over50Ms, frame.Over100Ms, frame.Frames, hasDisk ? disk.Reads : null, hasDisk ? disk.MeanReadMs : null,
                    disk.System.CpuPercent, disk.System.GpuDedicatedMib, frame.Pauses, frame.PauseMaxMs));
                int period = (int)(t / Math.Max(1, periodSeconds));
                int PeriodOf(PlayFrameSecond e) => e.T / Math.Max(1, periodSeconds);
                if (_frameSeconds.Count(e => PeriodOf(e) == period) > MaxFrameSecondsPerPeriod)
                {
                    _frameSeconds.Remove(_frameSeconds.Where(e => PeriodOf(e) == period).MinBy(Severity)!); // 구간 안에서는 가장 긴 간격·멈춤이 작은 것부터 버림 (같으면 먼저 온 것)
                }

                if (_frameSeconds.Count > MaxFrameSeconds)
                {
                    int crowded = _frameSeconds.GroupBy(PeriodOf).MaxBy(g => g.Count())!.Key; // 전체 상한은 칸이 가장 많은 구간에서 덜어냄
                    _frameSeconds.Remove(_frameSeconds.Where(e => PeriodOf(e) == crowded).MinBy(Severity)!);
                }
            }
        }

        public void SetPhysicalDisk(string? physicalDisk)
        {
            lock (_gate)
            {
                _physicalDisk ??= physicalDisk;
            }
        }

        private sealed class LaterStart(ProcessKey key, long startTick, PlayStageStart record, PlayRecordAccumulator? accumulator)
        {
            public ProcessKey Key { get; } = key;
            public long StartTick { get; } = startTick;
            public PlayStageStart Record { get; } = record;
            public PlayRecordAccumulator? Accumulator { get; set; } = accumulator;
            public bool Logged { get; set; }
        }

        /// 같은 세션에 뜬 이후 실행 파일: 그 시작 기준 창을 따로 셈 (기존 세션 기록은 그대로).
        public void AddStageStart(string exe, ProcessKey key, long startTick)
        {
            lock (_gate)
            {
                if (_laterStarts.Count >= MaxStageStarts || _laterStarts.Any(l => l.Key == key))
                {
                    return;
                }

                var record = new PlayStageStart { Exe = exe, OffsetSeconds = Math.Max(0, Stopwatch.GetElapsedTime(StartedTick, startTick).TotalSeconds) };
                _laterStarts.Add(new LaterStart(key, startTick, record, new PlayRecordAccumulator(stageCheckpoints ?? [.. StageCheckpointSeconds], sampleIntervalSeconds)));
            }
        }

        /// 새로 채워진 이후 시작 창이 있으면 true, 마지막 창까지 채운 시작은 finished 에 (한 번만).
        public bool TakeStageUpdate(out List<(string Game, PlayStageStart Start)> finished)
        {
            lock (_gate)
            {
                finished = [.. _laterStarts.Where(l => !l.Logged && l.Accumulator is null).Select(l =>
                {
                    l.Logged = true;
                    return (Game.Name, l.Record.Copy());
                })];
                bool changed = _stageChanged;
                _stageChanged = false;
                return changed;
            }
        }

        public PlaySampleRecord? SetStartStream(ProcessKey key, PlayStreamResult result, long tick)
        {
            lock (_gate)
            {
                if (_startStream.Status != "pending" && !(_startStream.Status == "skipped" && _startStream.Reason == ExistingGameStreamReason))
                {
                    LaterStart? later = _laterStarts.Find(l => l.Key == key); // 첫 결과를 이미 받은 세션: 이후 시작의 결과는 그 시작 항목에 붙임 (전에는 버려짐)
                    if (later is null || later.Record.Stream.Status != "pending")
                    {
                        return null;
                    }

                    later.Record.Stream = result;
                    return SnapshotLocked(tick);
                }

                _startStream = result;
                return SnapshotLocked(tick);
            }
        }

        public IReadOnlyList<PlayWindowSummary> AddSample(IReadOnlyList<PlayDiskCounters> previous, IReadOnlyList<PlayDiskCounters> current,
            long previousTick, long tick, PlaySystemSample system = default, PlaySeriesRaw? series = null, LoadState load = LoadState.Unknown, long serviceRead = 0)
        {
            lock (_gate)
            {
                double previousElapsed = Math.Max(0, Stopwatch.GetElapsedTime(StartedTick, previousTick).TotalSeconds);
                double elapsed = Stopwatch.GetElapsedTime(StartedTick, tick).TotalSeconds;
                long through = (long)Math.Floor(elapsed) - FrameSettleSeconds;
                IReadOnlyList<FrameSecond>? frames = _frames?.TakeSettled(through);
                IReadOnlyList<PlayWindowSummary> added = _accumulator.Add(previous, current, previousElapsed, elapsed, frames);
                foreach (PlayWindowSummary window in added)
                {
                    _windows.Add(window);
                }

                _periodWindows.AddRange(_periodAccumulator.Add(previous, current, previousElapsed, elapsed, frames));
                if (frames is not null)
                {
                    (ulong reads, double meanReadMs, bool slow) = _accumulator.LastSecond;
                    PlaySeriesPoint? point = null;
                    try
                    {
                        if (_series is not null && series is not null)
                        {
                            point = _series.Point(series with { GpuMib = system.GpuDedicatedMib ?? series.GpuMib, ServiceReadBytes = serviceRead }, _pids, previous, current, load); // 그래픽 메모리는 이미 읽은 시스템 값을 씀
                        }
                        else
                        {
                            _series?.Reset(); // 값을 못 읽은 초: 다음 초가 여러 초의 누계 차이를 한 초 값으로 적지 않게 앞 값을 버림
                        }
                    }
                    catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
                    {
                        StopSeries(e);
                    }

                    _pending[(long)Math.Floor((previousElapsed + elapsed) / 2)] = new PendingSecond(reads, meanReadMs, slow, system, point);
                    ApplyFrameSeconds(frames, through);
                }
                else
                {
                    _series?.Reset(); // 프레임 세션이 닫힌 동안: 다시 열린 뒤 첫 초가 그 동안의 누계 차이를 한 초 값으로 적지 않게
                }

                foreach (LaterStart later in _laterStarts.Where(l => l.Accumulator is not null && tick > l.StartTick))
                {
                    IReadOnlyList<PlayWindowSummary> laterWindows = later.Accumulator!.Add(previous, current, Math.Max(0, Stopwatch.GetElapsedTime(later.StartTick, previousTick).TotalSeconds), Stopwatch.GetElapsedTime(later.StartTick, tick).TotalSeconds);
                    later.Record.Windows.AddRange(laterWindows);
                    _stageChanged |= laterWindows.Count > 0;
                    if (later.Record.Windows.Count >= (stageCheckpoints ?? StageCheckpointSeconds).Length)
                    {
                        later.Accumulator = null; // 마지막 창까지 채움: 더 셈하지 않음
                    }
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

                FlushFramesLocked(); // 사라진 시각까지의 프레임을 모두 확정한 뒤 누계를 잡음
                (_goneAt, _goneTick, _goneTotal, _goneWindows) = (at, tick, _accumulator.Total(), _windows.Count);
                (_gonePeriodTotal, _gonePeriodWindows) = (_periodAccumulator.Total(), _periodWindows.Count);
            }
        }

        /// 유예에서 돌아온 표시. 종료 때 닫은 프레임 세션을 다시 열어야 하면 true (한 번만).
        public bool NoteReturn(DateTimeOffset at)
        {
            lock (_gate)
            {
                (_goneAt, _goneTotal, _returnedAt) = (null, null, at);
                bool reopen = _framesClosedAtExit;
                _framesClosedAtExit = false;
                return reopen;
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
                // SampleSessionAsync 가 제공자 실패를 이미 "사용 불가" 로 기록합니다.
            }
        }

        public PlaySampleRecord Finish(string reason, DateTimeOffset now, long tick, DateTimeOffset? exitedAt = null)
        {
            lock (_gate)
            {
                _endedAt = _goneAt ?? exitedAt ?? now;
                long endTick = _goneAt is not null ? _goneTick
                    : tick - Math.Max(0, (long)((now - _endedAt.Value).TotalSeconds * Stopwatch.Frequency));
                _finalDurationSeconds = Math.Max(0, Stopwatch.GetElapsedTime(StartedTick, endTick).TotalSeconds);
                _requestedEndReason = reason;
                if (_status != "unavailable")
                {
                    _status = reason is "game_exit" or "quick_exit" ? "complete" : "interrupted"; // 짧은 종료도 끝까지 기록한 세션 (꼬리표로만 구분)
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
                MinAvailableRamMib = _minAvailableMib,
                StartWarm = Warm,
                StartStream = _startStream,
                Windows = [.. final && _goneAt is not null ? _windows.Take(_goneWindows) : _windows],
                Total = final ? _goneTotal ?? _accumulator.Total() : null,
                StageStarts = [.. _laterStarts.Select(l => l.Record.Copy())],
                Periods = PlayPeriods.Build(final && _goneAt is not null ? [.. _periodWindows.Take(_gonePeriodWindows)] : _periodWindows, final ? _goneAt is not null ? _gonePeriodTotal : _periodAccumulator.Total() : null),
                FrameStatus = FrameStatusLocked(tick),
                FrameEventsLost = FrameStatusLocked(tick) is null ? null : (_frames?.EventsLost ?? 0) + _frameLost,
                FrameEventsLate = FrameStatusLocked(tick) is null ? null : (_frames?.EventsLate ?? 0) + _frameLate,
                FrameEventsOutOfOrder = FrameStatusLocked(tick) is null ? null : (_frames?.EventsOutOfOrder ?? 0) + _frameOutOfOrder,
                FrameStoppedAtSeconds = _frameStoppedAt,
                FrameSeconds = FrameStatusLocked(tick) is null ? null : [.. _frameSeconds.Where(f => !final || _goneAt is null || f.T < (int)Math.Floor(Stopwatch.GetElapsedTime(StartedTick, _goneTick).TotalSeconds))],
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
        private long _frameCount;
        private double _maxFrameMs;
        private double _sinceCheckpointMaxMs;
        private int _over50Ms;
        private int _over100Ms;
        private int _pauses;
        private double _maxPauseMs;
        private double _sinceCheckpointMaxPauseMs;

        /// 가장 최근 표본 한 초의 디스크 읽기 횟수, 평균 읽기 시간 (ms), 느린 초 여부 (위 느린 표본과 같은 판정).
        public (ulong Reads, double MeanReadMs, bool Slow) LastSecond { get; private set; }

        public PlayRecordAccumulator(int[] checkpoints, double sampleIntervalSeconds)
        {
            _checkpoints = checkpoints;
            _sampleIntervalSeconds = Math.Max(0.001, sampleIntervalSeconds);
        }

        /// 확정된 프레임 초를 누계에 더합니다 (검사점 판정보다 먼저 불러 그 검사점에 들어가게 함).
        public void AddFrames(IReadOnlyList<FrameSecond>? frames)
        {
            foreach (FrameSecond frame in frames ?? [])
            {
                _frameCount += frame.Frames;
                _maxFrameMs = Math.Max(_maxFrameMs, frame.MaxMs);
                _sinceCheckpointMaxMs = Math.Max(_sinceCheckpointMaxMs, frame.MaxMs);
                _over50Ms += frame.Over50Ms;
                _over100Ms += frame.Over100Ms;
                _pauses += frame.Pauses;
                _maxPauseMs = Math.Max(_maxPauseMs, frame.PauseMaxMs);
                _sinceCheckpointMaxPauseMs = Math.Max(_sinceCheckpointMaxPauseMs, frame.PauseMaxMs);
            }
        }

        public IReadOnlyList<PlayWindowSummary> Add(IReadOnlyList<PlayDiskCounters> previous, IReadOnlyList<PlayDiskCounters> current,
            double previousElapsedSeconds, double elapsedSeconds, IReadOnlyList<FrameSecond>? frames = null)
        {
            AddFrames(frames);
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
            LastSecond = (reads, reads == 0 ? 0 : readTime / 10_000d / reads, !reset && reads >= 5 && (double)readTime / reads >= LongRead100ns);

            List<PlayWindowSummary> added = [];
            foreach (int checkpoint in _checkpoints)
            {
                if (elapsedSeconds >= checkpoint && _recorded.Add(checkpoint))
                {
                    bool delayed = elapsedSeconds - checkpoint > _sampleIntervalSeconds / 2;
                    added.Add(Snapshot(checkpoint, elapsedSeconds, delayed, reset));
                    (_sinceCheckpointMaxMs, _sinceCheckpointMaxPauseMs) = (0, 0); // 구간별 가장 긴 간격·멈춤은 검사점마다 다시 셈
                }
            }

            return added;
        }

        public PlayWindowSummary Total() => Snapshot(null, _measuredAtSeconds, delayed: false, counterReset: _lastCounterReset);

        private PlayWindowSummary Snapshot(int? seconds, double measuredAtSeconds, bool delayed, bool counterReset) =>
            new(seconds, measuredAtSeconds, _readBytes, _readCount, _readTime100ns / 10_000_000d, _slowSamples, _longestSlowStreak,
                _sampleCount, _missingSampleCount, delayed, counterReset, _idleTime100ns / 10_000_000d, _maxQueueDepth,
                _frameCount, _maxFrameMs, _sinceCheckpointMaxMs, _over50Ms, _over100Ms, _pauses, _maxPauseMs, _sinceCheckpointMaxPauseMs);

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
            new(sample.DiskNumber, sample.ReadBytes, sample.ReadCount, sample.ReadTime100ns, sample.IdleTime100ns, sample.QueueDepth, sample.WriteBytes, sample.WriteCount, sample.WriteTime100ns);

        public static IPlayDiskCounter? Open(string path) => DiskInfo.OpenPerformanceMonitor(path) is { } monitor ? new WindowsPlayDiskCounter(monitor) : null;
    }

    /// 전체 CPU 사용률 (GetSystemTimes) 과 그래픽 메모리 전용 사용량 (성능 카운터): 둘 다 프로세스를 열지 않습니다.
    internal sealed class WindowsPlaySystemProbe : IPlaySystemProbe
    {
        private readonly CpuLoad _cpu = new();
        private readonly DiskActivity.GpuDedicatedMemory? _gpu = OpenGpu();

        public WindowsPlaySystemProbe() => _ = _cpu.Percent(); // 첫 호출은 기준만 잡음

        public PlaySystemSample Read() => new(_cpu.Percent(), _gpu?.Mib());

        public void Dispose() => _gpu?.Dispose();

        public static IPlaySystemProbe Open() => new WindowsPlaySystemProbe();

        private static DiskActivity.GpuDedicatedMemory? OpenGpu()
        {
            try
            {
                return DiskActivity.GpuDedicatedMemory.Open();
            }
            catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
            {
                return null; // 그래픽 메모리 값만 없이 CPU 값은 계속 남김
            }
        }
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

        public IReadOnlyList<PlaySampleRecord> Samples(string game)
        {
            lock (_gate)
            {
                return _document.Games.TryGetValue(game, out List<PlaySampleRecord>? samples) ? [.. samples] : [];
            }
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
