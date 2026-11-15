using System.Globalization;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// QuickExitGuard 의 바깥 세계 (시험은 가짜로 바꿔 끼움). Create 는 진짜 연결.
public sealed record QuickExitEnv
{
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// 짧은 종료 기준 (초): 설정을 다시 읽어도 최신 값.
    public Func<int> QuickSeconds { get; init; } = () => AutoSettings.Default.QuickExitSeconds;

    /// 올라와 있는 커널 드라이버 (조회 실패는 null). 게임 시작과 두 번째 이후 짧은 종료 때만 부름.
    public Func<IReadOnlyList<KernelDriver>?> Drivers { get; init; } = () => null;

    /// 감시기 경로를 막지 않게 조회와 파일 쓰기를 예약합니다.
    public Func<Action, Task> Background { get; init; } = action => Task.Run(action);

    public Func<string?> ReadState { get; init; } = () => null;

    public Action<string> WriteState { get; init; } = _ => { };

    /// 트레이가 읽는 알림 (auto_status.json) 을 남기고 지우는 통로.
    public Action<string, QuickExitRecord> Notify { get; init; } = (_, _) => { };

    public Action<string> ClearNotice { get; init; } = _ => { };

    public static QuickExitEnv Create(string stateFile, Func<int> quickSeconds, AutoStatus? status) => new()
    {
        QuickSeconds = quickSeconds,
        Drivers = KernelDrivers.Snapshot,
        ReadState = () => File.Exists(stateFile) ? File.ReadAllText(stateFile) : null,
        WriteState = text =>
        {
            string tmp = stateFile + ".tmp";
            DurableFile.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(text));
            File.Move(tmp, stateFile, true);
        },
        Notify = (game, record) => status?.SetQuickExit(game, record),
        ClearNotice = game => status?.ClearQuickExit(game),
    };
}

/// 게임별 실행 직후 종료 연속 횟수와 한 번 물러남 (판정은 QuickExitRule). 세션은 첫 게임 프로세스 시작부터 모두 종료까지입니다.
/// 상태는 데이터 폴더의 quick_exit.json (열린 세션 제외): 서비스를 다시 시작해도 연속 횟수와 마지막 정상 실행의 드라이버 목록이 남습니다.
/// 게임·프로세스를 열지 않고, 드라이버는 읽기만 합니다. 모든 호출은 예외를 밖으로 내지 않습니다.
public sealed class QuickExitGuard
{
    private sealed record Session(Guid Id, DateTimeOffset Start, DateTimeOffset RunStarted, bool BackOff,
        IReadOnlyList<KernelDriver>? Drivers, string? Exe, int Pid);

    private sealed class GameState
    {
        public int Count { get; set; }

        public List<int> Runs { get; } = [];

        public DateTimeOffset? LastQuickAt { get; set; }

        public string? BackedOff { get; set; } // 이번 연속에서 물러난 실행의 결과 (BackedOffResult), 아직 물러난 적이 없으면 null

        public IReadOnlyList<KernelDriver>? NormalDrivers { get; set; }

        public Session? Open { get; set; }

        public long EffectVersion { get; set; }
    }

    private readonly QuickExitEnv _env;
    private readonly ILog _log;
    private readonly object _lock = new();
    private readonly object _saveLock = new();
    private readonly object _effectLock = new();
    private readonly Dictionary<string, GameState> _games = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, long> _appliedEffects = new(StringComparer.OrdinalIgnoreCase);
    private long _saveVersion;
    private long _writtenVersion;

    public QuickExitGuard(QuickExitEnv env, ILog log)
    {
        _env = env;
        _log = log;
        Guard("load", Load);
    }

    /// 세션이 시작됨 (게임 시작 이벤트에서, 그 게임이 실행 중이 아니었을 때만). 이번 실행에서 서비스가 물러나야 하면 true.
    public bool BeginSession(string game, string? exe = null, int pid = 0, DateTimeOffset? startedAt = null)
    {
        Session session;
        string? warning = null;
        lock (_lock)
        {
            GameState state = State(game);
            DateTimeOffset now = startedAt ?? _env.Now();
            bool backOff = QuickExitRule.ShouldBackOff(state.LastQuickAt, state.BackedOff, now);
            session = new Session(Guid.NewGuid(), now, now, backOff, null, exe, pid);
            state.Open = session;
            if (backOff)
            {
                int lastSeconds = state.Runs.Count > 0 ? state.Runs[^1] : 0;
                int minutes = (int)(now - state.LastQuickAt!.Value).TotalMinutes;
                warning = $"{game}: backing off for this run (quick exit {state.Count} in a row, last {lastSeconds}s, {minutes} min ago): no start stream, memory yield or trace";
            }
        }

        if (warning is not null)
        {
            SafeLog(LogLevel.Warn, warning);
        }

        QueueBackground(() => AttachDrivers(game, session.Id));
        return session.BackOff;
    }

    /// 다시 뜬 프로그램 앞의 실행이 5초 이상이었는지 확인합니다. 실제 분리는 StartedAgain 에서만 확정합니다.
    public bool WouldSplitSession(string game, string exe, int pid, DateTimeOffset restartedAt)
    {
        lock (_lock)
        {
            return _games.TryGetValue(game, out GameState? state)
                && state.Open is { Pid: > 0 } open
                && open.Pid != pid
                && string.Equals(open.Exe, exe, StringComparison.OrdinalIgnoreCase)
                && QuickExitRule.SplitsSession(restartedAt - open.RunStarted);
        }
    }

    /// 같은 실행 파일이 다시 떴는가. previousEndAt: 앞 실행의 끝 (분리 판정용), startedAt: 새 pid 의 시작 (저장용).
    /// 앞 실행이 5초 미만이면 세션을 잇고 개별 pid 실행 시각만 새 시작으로 갱신합니다.
    public bool StartedAgain(string game, string exe, int pid, DateTimeOffset previousEndAt, DateTimeOffset startedAt, bool allowSplit, out bool continued)
    {
        continued = false;
        lock (_lock)
        {
            if (!_games.TryGetValue(game, out GameState? state) || state.Open is not { Pid: > 0 } open
                || open.Pid == pid || !string.Equals(open.Exe, exe, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            continued = true;
            if (allowSplit && QuickExitRule.SplitsSession(previousEndAt - open.RunStarted))
            {
                continued = false;
                return true;
            }

            state.Open = open with { Pid = pid, RunStarted = startedAt };
            return false;
        }
    }

    /// 지금 열린 세션이 물러난 실행인가 (디스패처가 시작 스트림을 건너뛸 때 물음).
    public bool IsBackingOff(string game)
    {
        lock (_lock)
        {
            return _games.TryGetValue(game, out GameState? state) && state.Open?.BackOff == true;
        }
    }

    /// 지금 시작하면 (또는 지금 열린 세션이) 물러날 상태인가: 세션을 열지 않고 "연속당 한 번" 도 소비하지 않는 읽기 전용 질문 (트리거 경로가 물러날 게임을 거를 때).
    public bool WouldBackOff(string game)
    {
        lock (_lock)
        {
            return _games.TryGetValue(game, out GameState? state)
                && (state.Open is { } open ? open.BackOff : QuickExitRule.ShouldBackOff(state.LastQuickAt, state.BackedOff, _env.Now()));
        }
    }

    /// 세션이 끝남 (exitedAt: 프로세스가 사라진 것을 처음 본 시각, 없으면 지금). 짧은 종료로 셌으면 true.
    public bool EndSession(string game, DateTimeOffset? exitedAt = null)
    {
        bool quick = false;
        string? stateText = null;
        long stateVersion = 0;
        long effectVersion = 0;
        QuickExitEffect? effect = null;
        lock (_lock)
        {
            if (!_games.TryGetValue(game, out GameState? state) || state.Open is not { } session)
            {
                return false;
            }

            DateTimeOffset end = exitedAt ?? _env.Now();
            if (end < session.Start)
            {
                return false; // 이전 실행의 늦은 종료가 새 세션을 닫지 않음
            }

            state.Open = null;
            TimeSpan length = end > session.Start ? end - session.Start : TimeSpan.Zero;
            int seconds = (int)Math.Round(length.TotalSeconds);
            QuickExitKind kind = QuickExitRule.Classify(length, _env.QuickSeconds());
            switch (kind)
            {
                case QuickExitKind.Quick:
                    quick = true;
                    state.Count++;
                    state.Runs.Add(seconds);
                    if (state.Runs.Count > QuickExitRule.MaxRuns)
                    {
                        state.Runs.RemoveAt(0);
                    }

                    state.LastQuickAt = end;
                    if (session.BackOff)
                    {
                        state.BackedOff = BackedOffResult.Exited;
                    }

                    effectVersion = ++state.EffectVersion;
                    effect = state.Count < 2
                        ? new QuickExitEffect(game, effectVersion, end, state.Count, [.. state.Runs], state.BackedOff,
                            state.NormalDrivers, FirstQuick: true, Clear: true)
                        : new QuickExitEffect(game, effectVersion, end, state.Count, [.. state.Runs], state.BackedOff,
                            state.NormalDrivers, FirstQuick: false, Clear: false, SecondQuick: true);
                    break;
                case QuickExitKind.Between:
                    if (session.BackOff)
                    {
                        effectVersion = ++state.EffectVersion;
                        effect = BackedOffLaunchEffect(game, state, session, end, seconds, effectVersion);
                    }

                    break;
                default:
                    if (session.BackOff)
                    {
                        effectVersion = ++state.EffectVersion;
                        effect = BackedOffLaunchEffect(game, state, session, end, seconds, effectVersion);
                    }
                    else
                    {
                        effectVersion = ++state.EffectVersion;
                        effect = new QuickExitEffect(game, effectVersion, end, state.Count, [.. state.Runs], null,
                            state.NormalDrivers, FirstQuick: false, Clear: true);
                    }

                    state.Count = 0;
                    state.Runs.Clear();
                    state.LastQuickAt = null;
                    state.BackedOff = null;
                    state.NormalDrivers = session.Drivers ?? state.NormalDrivers; // 시작 조회가 늦었거나 실패했으면 이전 기준을 보존
                    break;
            }

            try
            {
                stateText = SerializeLocked();
                stateVersion = ++_saveVersion;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                SafeLog(LogLevel.Error, "quick exit guard state serialization failed", e);
            }
        }

        if (stateText is not null)
        {
            long version = stateVersion;
            string text = stateText;
            QueueBackground(() => WriteState(version, text));
        }

        if (effect is { } pending)
        {
            QueueBackground(() => ApplyEffect(pending));
        }

        return quick;
    }

    private QuickExitEffect BackedOffLaunchEffect(string game, GameState state, Session session, DateTimeOffset end, int seconds, long version)
    {
        IReadOnlyList<KernelDriver>? added = QuickExitRule.NewDrivers(state.NormalDrivers, session.Drivers);
        return new QuickExitEffect(game, version, end, state.Count, [.. state.Runs], BackedOffResult.Launched,
            state.NormalDrivers, FirstQuick: false, Clear: false, LaunchSeconds: seconds, Added: added);
    }

    private void ApplyEffect(QuickExitEffect effect)
    {
        if (effect.LaunchSeconds is { } seconds)
        {
            IReadOnlyList<KernelDriver>? added = effect.Added;
            string result = added is { Count: > 0 } ? string.Join(", ", added.Select(d => $"{d.Name} ({d.Path})"))
                : added is not null ? "none"
                : effect.NormalDrivers is null ? "unknown (no normal run recorded)" : "unknown";
            string message = $"{effect.Game}: launched after the service backed off ({seconds}s); the earlier quick exit(s) may involve the service, or the program may restart itself";
            ApplyNotice(effect, () =>
            {
                SafeLog(LogLevel.Warn, message);
                _env.Notify(effect.Game, new QuickExitRecord(effect.Count, effect.Runs, effect.At, BackedOffResult.Launched,
                    added is null ? null : [.. added.Select(d => d.Name)]));
            });
            return;
        }

        if (effect.FirstQuick)
        {
            ApplyNotice(effect, () =>
            {
                if (effect.Clear)
                {
                    _env.ClearNotice(effect.Game);
                }

                SafeLog(LogLevel.Info, $"{effect.Game}: exited quickly ({effect.Runs[^1]}s)");
            });
            return;
        }

        if (!effect.SecondQuick)
        {
            if (effect.Clear)
            {
                ApplyNotice(effect, () => _env.ClearNotice(effect.Game));
            }

            return;
        }

        IReadOnlyList<KernelDriver>? current = SafeDrivers(); // 종료 조회 실패는 알 수 없음이며 시작 목록으로 대체하지 않음
        IReadOnlyList<KernelDriver>? newDrivers = QuickExitRule.NewDrivers(effect.NormalDrivers, current);
        string resultText = effect.BackedOff switch
        {
            null or BackedOffResult.None => "not backed off",
            BackedOffResult.Exited => "exited too",
            _ => effect.BackedOff,
        };
        string warning = $"{effect.Game}: quick exit {effect.Count} in a row (runs {string.Join(", ", effect.Runs.Select(r => r + "s"))}); backed-off run: {resultText}"
            + "; new kernel drivers since the last normal run: " + DriverText(newDrivers, effect.NormalDrivers is null, current is null);
        ApplyNotice(effect, () =>
        {
            SafeLog(LogLevel.Warn, warning);
            _env.Notify(effect.Game, new QuickExitRecord(effect.Count, effect.Runs, effect.At, effect.BackedOff ?? BackedOffResult.None,
                newDrivers is null ? null : [.. newDrivers.Select(d => d.Name)]));
        });
    }

    private void ApplyNotice(QuickExitEffect effect, Action action)
    {
        lock (_effectLock)
        {
            if (_appliedEffects.TryGetValue(effect.Game, out long previous) && effect.Version <= previous)
            {
                return;
            }

            _appliedEffects[effect.Game] = effect.Version;
            try
            {
                action();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                SafeLog(LogLevel.Error, "quick exit notice failed", e);
            }
        }
    }

    private void AttachDrivers(string game, Guid sessionId)
    {
        IReadOnlyList<KernelDriver>? drivers = SafeDrivers();
        lock (_lock)
        {
            if (_games.TryGetValue(game, out GameState? state) && state.Open is { } session && session.Id == sessionId)
            {
                state.Open = session with { Drivers = drivers };
            }
        }
    }

    private void QueueBackground(Action action)
    {
        try
        {
            Task task = _env.Background(() => Guard("background", action));
            _ = task.ContinueWith(t => SafeLog(LogLevel.Error, "quick exit guard background task failed", t.Exception),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SafeLog(LogLevel.Error, "quick exit guard background task could not start", e);
        }
    }

    private void WriteState(long version, string text)
    {
        lock (_saveLock)
        {
            if (version <= _writtenVersion)
            {
                return;
            }

            try
            {
                _env.WriteState(text);
                _writtenVersion = version;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                SafeLog(LogLevel.Warn, "quick exit state file not written: " + e.GetType().Name);
            }
        }
    }

    private static string DriverText(IReadOnlyList<KernelDriver>? added, bool noNormalRun, bool unavailable) =>
        added is { Count: > 0 } ? string.Join(", ", added.Select(d => $"{d.Name} ({d.Path})"))
        : added is not null ? "none"
        : noNormalRun ? "unknown (no normal run recorded)"
        : unavailable ? "unavailable" : "unknown";

    private IReadOnlyList<KernelDriver>? SafeDrivers()
    {
        try
        {
            return _env.Drivers();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SafeLog(LogLevel.Warn, "kernel driver list unavailable: " + e.GetType().Name);
            return null;
        }
    }

    private void SafeLog(LogLevel level, string message, Exception? exception = null)
    {
        try
        {
            _log.Write(level, message, exception);
        }
        catch (Exception)
        {
            // 기록 실패가 게임 시작·종료 처리를 막지 않게 합니다.
        }
    }

    // 호출하는 쪽이 잠금을 잡고 있어야 합니다 (게임 상태는 잠금 아래에서만 바뀜).
    private GameState State(string game)
    {
        if (!_games.TryGetValue(game, out GameState? state))
        {
            _games[game] = state = new GameState();
        }

        return state;
    }

    private void Guard(string operation, Action run)
    {
        try
        {
            run();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            SafeLog(LogLevel.Error, $"quick exit guard {operation} failed", e);
        }
    }

    // 호출하는 쪽이 잠금을 잡고 있어야 합니다.
    private string SerializeLocked()
    {
        var games = new JObject();
        foreach ((string name, GameState s) in _games.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (s.Count == 0 && s.NormalDrivers is null)
            {
                continue;
            }

            var o = new JObject { { "count", (long)s.Count }, { "run_seconds", s.Runs.Select(r => (object?)(long)r).ToList() } };
            if (s.LastQuickAt is { } at)
            {
                o.Add("last_quick_at", at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            }

            if (s.BackedOff is not null)
            {
                o.Add("backed_off", s.BackedOff);
            }

            if (s.NormalDrivers is { } drivers)
            {
                o.Add("normal_drivers", drivers.Select(d => (object?)d.Path).ToList());
            }

            games.Add(name, o);
        }

        return PyJson.Dumps(new JObject { { "games", games } }, indent: 2, ensureAscii: false) + "\n";
    }

    // 없거나 깨진 파일은 빈 상태로 시작합니다. 알 수 없는 키는 무시.
    private void Load()
    {
        string? text;
        try
        {
            text = _env.ReadState();
            if (text is null || JsonRead.Parse(text) is not JObject root || !root.TryGetValue("games", out object? g) || g is not JObject games)
            {
                return;
            }

            foreach ((string name, object? value) in games)
            {
                if (value is not JObject o)
                {
                    continue;
                }

                GameState state = State(name);
                state.Count = o.TryGetValue("count", out object? c) && c is long count ? (int)Math.Clamp(count, 0, int.MaxValue) : 0;
                if (o.TryGetValue("run_seconds", out object? rs) && rs is List<object?> runs)
                {
                    state.Runs.AddRange(runs.OfType<long>().Select(r => (int)Math.Clamp(r, 0, int.MaxValue)).TakeLast(QuickExitRule.MaxRuns));
                }

                state.LastQuickAt = o.TryGetValue("last_quick_at", out object? at) && at is string t && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed) ? parsed : null;
                state.BackedOff = o.TryGetValue("backed_off", out object? b) && b is BackedOffResult.Exited or BackedOffResult.Launched ? (string)b : null;
                if (o.TryGetValue("normal_drivers", out object? nd) && nd is List<object?> paths)
                {
                    state.NormalDrivers = [.. paths.OfType<string>().Select(p => new KernelDriver(Path.GetFileName(p), p))];
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            SafeLog(LogLevel.Warn, "quick exit state file unreadable, starting empty: " + e.GetType().Name);
            _games.Clear();
        }
    }

    private sealed record QuickExitEffect(string Game, long Version, DateTimeOffset At, int Count, IReadOnlyList<int> Runs,
        string? BackedOff, IReadOnlyList<KernelDriver>? NormalDrivers, bool FirstQuick, bool Clear,
        int? LaunchSeconds = null, IReadOnlyList<KernelDriver>? Added = null, bool SecondQuick = false);
}
