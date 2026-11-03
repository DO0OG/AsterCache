using AsterCache.Core.Discovery;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Profiles;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Tracer;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 처음 보는 게임의 자동 트레이스. 등록 게임 exe 가 떴는데 원시 트레이스 (plans\<이름>.trace.json) 가 없으면 ETW 세션을 열어 읽기를 모으고,
/// 게임이 끝나거나 시간·읽기량 상한에 닿으면 멈춰 수동 수집 (aster trace --live) 과 같은 파일을 씁니다. 한 번에 하나만 돕니다.
public sealed class AutoTracer
{
    private readonly AutoEnv _env;
    private readonly AutoStatus _status;
    private readonly ILog _log;
    private readonly Func<AsterSettings> _settings;
    private readonly LearnEnv _learn;
    private readonly object _lock = new();
    private Run? _run;
    private readonly Dictionary<string, string?> _cheat = new(StringComparer.OrdinalIgnoreCase); // 게임별 트레이스 금지 판정 (없음 포함), _lock 으로 보호
    private readonly HashSet<string> _cheatLogged = new(StringComparer.OrdinalIgnoreCase);

    public AutoTracer(AutoEnv env, AutoStatus status, ILog log, Func<AsterSettings> settings, LearnEnv? learn = null)
    {
        _env = env;
        _status = status;
        _log = log;
        _settings = settings;
        _learn = learn ?? LearnEnv.Real;
    }

    public bool Active
    {
        get
        {
            lock (_lock)
            {
                return _run is not null;
            }
        }
    }

    public static string RawTracePath(GameSettings g) => Path.Combine(Path.GetDirectoryName(g.Plan) ?? string.Empty, g.Name + ".trace.json");

    /// 지금까지 모은 플레이 수: 원시 트레이스가 있을 때 trace.run1.json, run2.json ... 이 이어서 있는 개수, 그런 파일 없이 원시 트레이스만 있으면 1, 원시 트레이스가 없으면 0. 따로 저장하는 상태 없음.
    public static int RunCount(GameSettings g, Func<string, bool> exists)
    {
        string raw = RawTracePath(g);
        if (!exists(raw))
        {
            return 0;
        }

        int n = 0;
        while (n < 32 && exists(TraceWriter.RunPath(raw, n + 1)))
        {
            n++;
        }

        return Math.Max(1, n);
    }

    /// 가져온 프로필·합성 트레이스 (plans\<이름>.boot.trace.json). 원시 트레이스가 없을 때만 계획·예열의 입력이 되고, 첫 실행 기록은 막지 않습니다.
    public static string BootTracePath(GameSettings g) => Path.Combine(Path.GetDirectoryName(g.Plan) ?? string.Empty, g.Name + GameProfile.BootTraceSuffix);

    /// 계획·예열이 읽을 트레이스: 원시가 있으면 원시, 없으면 boot, 둘 다 없으면 null.
    public static string? PlanTracePath(GameSettings g, Func<string, bool> exists) =>
        exists(RawTracePath(g)) ? RawTracePath(g) : exists(BootTracePath(g)) ? BootTracePath(g) : null;

    /// boot 트레이스 파일의 "source" 표시 (프리페치 기록으로 만든 것이면 "prefetch", 가져온 프로필 등 표시가 없으면 null). 읽을 수 없으면 null.
    public static string? TraceSourceOf(string path)
    {
        try
        {
            return JsonRead.ReadFile(path) is JObject o && o.TryGetValue("source", out object? v) ? v as string : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
    }

    /// 부트스트랩 S2: 기록 없는 게임의 boot 트레이스를 Windows 프리페치 기록 (.pf) 으로 만듭니다. 만든 범위 수, 못 만들면 0.
    /// 프리페처가 꺼져 있거나 맞는 기록이 없거나, 원시·boot 트레이스가 이미 있으면 (공유 프로필 S1 이 먼저) 0. 쓸 때도 이미 있으면 덮지 않습니다.
    public static int BootFromPrefetch(GameSettings g, Func<bool>? prefetcherOn = null, string? prefetchDir = null, IReadOnlyDictionary<uint, string>? letters = null)
    {
        string boot = BootTracePath(g);
        if (File.Exists(RawTracePath(g)) || File.Exists(boot) || !(prefetcherOn ?? SystemProbes.PrefetcherOn)()
            || PrefetchFiles.BootTrace(g.ExeNames, g.OriginRoot, prefetchDir, letters) is not { Count: > 0 } events)
        {
            return 0;
        }

        string temp = $"{boot}.{Guid.NewGuid():N}.tmp"; // 고유 이름: 동시에 만드는 쪽끼리 임시 파일을 공유하지 않음
        try
        {
            File.WriteAllBytes(temp, PrefetchFiles.BootTraceBytes(events));
            File.Move(temp, boot, false); // 그 사이 다른 쪽이 boot 를 만들었으면 IOException
            return events.Count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception d) when (d is IOException or UnauthorizedAccessException)
            {
                // 정리도 못 하면 남겨 둠
            }

            return 0;
        }
    }

    // 안티치트 게임은 ETW 커널 트레이스를 걸지 않음 (보호 드라이버가 세션을 보고 클라이언트를 끊음). 등록 정보, Riot 규칙 (Vanguard 는 게임 폴더 밖), 폴더 이름 순으로 보고 (폴더 열거는 한 번만) 탐지 실패도 막음.
    private string? TraceBlocker(GameSettings g)
    {
        lock (_lock)
        {
            if (_cheat.TryGetValue(g.Name, out string? known))
            {
                return known;
            }
        }

        string? cheat = AntiCheat.ForbidsTrace(g.AntiCheat) ? g.AntiCheat
            : AntiCheat.RiotVanguard(g.OriginRoot, g.ExeNames, _env.VanguardInstalled());
        if (cheat is null)
        {
            string? found = _env.AntiCheatOf(g.OriginRoot);
            cheat = AntiCheat.ForbidsTrace(found) ? found : null;
            if (found == AutoEnv.AntiCheatUnknown)
            {
                return cheat; // 탐지 실패는 캐시하지 않음 (다음 시작에 다시 봄)
            }
        }

        lock (_lock)
        {
            _cheat[g.Name] = cheat;
        }

        return cheat;
    }

    public void Start(GameSettings g)
    {
        if (TraceBlocker(g) is { } cheat)
        {
            bool first;
            lock (_lock)
            {
                first = _cheatLogged.Add(g.Name);
            }

            if (first)
            {
                _log.Info($"{g.Name}: trace skipped (anti-cheat: {cheat})");
            }

            return;
        }

        string? refused;
        ReadAhead? learn = null;
        int done; // 이미 모은 플레이 수: 0 이면 첫 기록, 1 이상이면 병합용 추가 기록 (상태·선행 읽기를 건드리지 않음)
        lock (_lock)
        {
            done = RunCount(g, _env.FileExists);
            if (_run is not null || done >= _settings().Auto.TraceRuns)
            {
                return;
            }

            refused = _env.Capabilities(g.OriginRoot).PrewarmRefusal; // 시스템 SSD·SSD·종류를 모르는 드라이브는 건드리지 않음 (시스템 HDD 는 읽기 전용 트레이스만)
            if (refused is null)
            {
                try
                {
                    LearnSettings ls = _settings().Auto.Learn;
                    learn = ls.Enabled && done == 0 ? AutoLearn.Create(g, ls, _env.PhysicalRamBytes(), _learn) : null; // 시스템 SSD·SSD 는 위에서 이미 걸러짐
                    _run = new Run(g, _env.StartTrace(g.ExeNames, learn is null ? null : AutoLearn.Listener(learn)), _env.Now()) { Learn = learn, Number = done + 1 };
                    learn?.Start();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    learn?.Dispose();
                    _log.Error($"{g.Name}: auto trace could not start", e);
                    if (done == 0)
                    {
                        _status.Set(g.Name, g.Source, null, AutoStage.Failed, "trace session failed to start");
                    }

                    return;
                }
            }
        }

        if (refused is not null)
        {
            if (done == 0 && _status.Set(g.Name, g.Source, null, AutoStage.Skipped, refused))
            {
                _log.Info($"{g.Name}: auto trace and relocation skipped ({refused})"); // 같은 이유면 매번 남기지 않음
            }

            return;
        }

        if (done > 0)
        {
            _log.Info($"{g.Name}: auto trace started (run {done + 1}/{_settings().Auto.TraceRuns})");
            return;
        }

        _status.Set(g.Name, g.Source, null, learn is null ? AutoStage.Tracing : AutoStage.Learning, learn is null ? null : AutoLearn.Detail(learn.Stats));
        _log.Info($"{g.Name}: auto trace started" + (learn is null ? string.Empty : " (learning: read ahead while the game runs)"));
    }

    /// 1 초마다 부릅니다. 끝내야 하면 세션을 멈추고 트레이스를 씁니다.
    public void Step()
    {
        Run? run;
        lock (_lock)
        {
            run = _run;
        }

        if (run is null)
        {
            return;
        }

        AutoSettings a = _settings().Auto;
        IReadOnlyList<(int Pid, long ReadBytes)> procs = _env.GameProcesses(run.Game.ExeNames);
        IReadOnlyList<int> rooted = _env.RootPids(run.Game); // 게임 폴더 아래의 실제 게임 프로세스 (등록 exe 의 자식인 경우도 포함)
        procs = [.. procs, .. rooted.Where(p => procs.All(x => x.Pid != p)).Select(p => (Pid: p, Bytes: _env.ReadBytes(p))).Where(x => x.Bytes is not null).Select(x => (x.Pid, x.Bytes!.Value))];
        foreach ((int pid, long bytes) in procs)
        {
            // 첫 표본의 프로세스는 지금 값이 기준, 그 뒤에 나타난 프로세스는 세션이 열린 뒤에 시작했으므로 0 이 기준
            long before = run.Last.TryGetValue(pid, out long last) ? last : run.Primed ? 0 : bytes;
            run.Read += Math.Max(0, bytes - before);
            run.Last[pid] = bytes;
        }

        run.Primed = true;
        if (run.Learn is { } learn && _env.Now() - run.LastStatusAt >= AutoLearn.StatusEvery)
        {
            run.LastStatusAt = _env.Now();
            _status.Set(run.Game.Name, run.Game.Source, null, AutoStage.Learning, AutoLearn.Detail(learn.Stats));
        }

        TimeSpan elapsed = _env.Now() - run.StartedAt;
        string? why = !_env.AnyRunning(run.Game.ExeNames) && rooted.Count == 0 ? "game exited" // 읽기량을 못 연 (보호된) 게임 프로세스도 실행 중으로 셈, 등록 exe 가 먼저 끝나도 폴더 아래 프로세스가 남았으면 실행 중
            : elapsed >= TimeSpan.FromSeconds(a.TraceCapSeconds) ? "time cap"
            : run.Read >= (long)a.TraceCapMib << 20 ? "read cap"
            : null;
        if (why is not null)
        {
            Finish(run, why);
        }
    }

    /// 서비스 종료: 돌고 있는 세션만 정리합니다 (기록은 버림).
    public void Shutdown()
    {
        Run? run;
        lock (_lock)
        {
            (run, _run) = (_run, null);
        }

        run?.Session.Dispose();
        run?.Learn?.Dispose();
    }

    private void Finish(Run run, string why)
    {
        GameSettings g = run.Game;
        try
        {
            IReadOnlyList<TraceRecord> records = run.Session.Stop();
            EndLearn(run);
            (List<TraceEvent> events, _) = TraceBuilder.Build(records, g.ExeNames, g.OriginRoot);
            List<TraceEvent> canonical = _env.Canonicalize(events);
            if (canonical.Count == 0)
            {
                _log.Warn($"{g.Name}: auto trace ended ({why}) with no reads captured");
                if (run.Number == 1)
                {
                    _status.Set(g.Name, g.Source, null, AutoStage.Failed, "no reads captured");
                }

                return;
            }

            if (run.Number == 1)
            {
                TraceWriter.Write([canonical], RawTracePath(g));
            }
            else
            {
                Merge(g, canonical, run.Number);
            }

            _log.Info($"{g.Name}: auto trace ended ({why}), {canonical.Count} read events saved" + (run.Number == 1 ? string.Empty : $" (run {run.Number}, merged)"));
            if (run.Number == 1)
            {
                _status.Set(g.Name, g.Source, null, AutoStage.WaitingIdle, null);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Error($"{g.Name}: auto trace failed", e);
            if (run.Number == 1)
            {
                _status.Set(g.Name, g.Source, null, AutoStage.Failed, "trace failed: " + e.GetType().Name); // 추가 기록이 실패해도 이미 있는 트레이스와 상태는 그대로
            }
        }
        finally
        {
            run.Session.Dispose();
            run.Learn?.Dispose();
            lock (_lock)
            {
                _run = null;
            }
        }
    }

    // 두 번째 이후 플레이: runN.json 으로 저장하고, run1..N 의 블록 합집합 (run1 의 접근 순서 뒤에 새 run 에서 처음 본 블록) 을 임시 파일을 거쳐 원시 트레이스로 한 번에 바꿔 씁니다.
    private void Merge(GameSettings g, List<TraceEvent> canonical, int number)
    {
        string raw = RawTracePath(g);
        string first = TraceWriter.RunPath(raw, 1);
        if (number == 2 && !_env.FileExists(first))
        {
            File.Copy(raw, first, true); // run 파일 없이 원시 트레이스만 있던 게임: 그 트레이스가 run1
        }

        TraceWriter.WriteFile(TraceWriter.RunPath(raw, number), canonical);
        var sessions = new List<IReadOnlyList<TraceEvent>>();
        for (int i = 1; i < number; i++)
        {
            sessions.Add(JsonRead.ReadFile(TraceWriter.RunPath(raw, i)) is List<object?> rows ? [.. rows.Select(TraceEvents.FromJson)] : throw new PyValueException("bad trace file"));
        }

        sessions.Add(canonical);
        string temp = raw + ".tmp";
        TraceWriter.WriteFile(temp, TraceWriter.Union(sessions));
        File.Move(temp, raw, true);
    }

    // 세션이 멈춘 뒤 선행 읽기를 끝내고 결과를 남깁니다 (Dispose 는 작업자 스레드를 기다림).
    private void EndLearn(Run run)
    {
        if (run.Learn is not { } learn)
        {
            return;
        }

        learn.Dispose();
        _log.Info($"{run.Game.Name}: learn ended, {AutoLearn.Summary(learn.Stats)}");
    }

    private sealed class Run(GameSettings game, IAutoTrace session, DateTimeOffset startedAt)
    {
        public GameSettings Game { get; } = game;

        public IAutoTrace Session { get; } = session;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public Dictionary<int, long> Last { get; } = [];

        public bool Primed { get; set; }

        public long Read { get; set; }

        /// 첫 실행 선행 읽기 (auto.learn 이 켜졌을 때).
        public ReadAhead? Learn { get; init; }

        /// 이 플레이가 몇 번째 기록인가 (1 = 첫 기록).
        public int Number { get; init; } = 1;

        public DateTimeOffset LastStatusAt { get; set; } = startedAt;
    }
}
