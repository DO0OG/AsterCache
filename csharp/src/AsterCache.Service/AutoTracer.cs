using AsterCache.Core.Logging;
using AsterCache.Core.Profiles;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Tracer;

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

    /// 가져온 프로필·합성 트레이스 (plans\<이름>.boot.trace.json). 원시 트레이스가 없을 때만 계획·예열의 입력이 되고, 첫 실행 기록은 막지 않습니다.
    public static string BootTracePath(GameSettings g) => Path.Combine(Path.GetDirectoryName(g.Plan) ?? string.Empty, g.Name + GameProfile.BootTraceSuffix);

    /// 계획·예열이 읽을 트레이스: 원시가 있으면 원시, 없으면 boot, 둘 다 없으면 null.
    public static string? PlanTracePath(GameSettings g, Func<string, bool> exists) =>
        exists(RawTracePath(g)) ? RawTracePath(g) : exists(BootTracePath(g)) ? BootTracePath(g) : null;

    public void Start(GameSettings g)
    {
        string? refused;
        ReadAhead? learn = null;
        lock (_lock)
        {
            if (_run is not null || _env.FileExists(RawTracePath(g)))
            {
                return;
            }

            refused = _env.DriveRefusal(g.OriginRoot); // 시스템 볼륨·SSD·종류를 모르는 드라이브는 건드리지 않음
            if (refused is null)
            {
                try
                {
                    LearnSettings ls = _settings().Auto.Learn;
                    learn = ls.Enabled ? AutoLearn.Create(g, ls, _env.PhysicalRamBytes(), _learn) : null; // 시스템 볼륨·SSD 는 위에서 이미 걸러짐
                    _run = new Run(g, _env.StartTrace(g.ExeNames, learn is null ? null : AutoLearn.Listener(learn)), _env.Now()) { Learn = learn };
                    learn?.Start();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    learn?.Dispose();
                    _log.Error($"{g.Name}: auto trace could not start", e);
                    _status.Set(g.Name, g.Source, null, AutoStage.Failed, "trace session failed to start");
                    return;
                }
            }
        }

        if (refused is not null)
        {
            if (_status.Set(g.Name, g.Source, null, AutoStage.Skipped, refused))
            {
                _log.Info($"{g.Name}: auto trace and relocation skipped ({refused})"); // 같은 이유면 매번 남기지 않음
            }

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
        string? why = !_env.AnyRunning(run.Game.ExeNames) ? "game exited" // 읽기량을 못 연 (보호된) 게임 프로세스도 실행 중으로 셈
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
                _status.Set(g.Name, g.Source, null, AutoStage.Failed, "no reads captured");
                return;
            }

            TraceWriter.Write([canonical], RawTracePath(g));
            _log.Info($"{g.Name}: auto trace ended ({why}), {canonical.Count} read events saved");
            _status.Set(g.Name, g.Source, null, AutoStage.WaitingIdle, null);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Error($"{g.Name}: auto trace failed", e);
            _status.Set(g.Name, g.Source, null, AutoStage.Failed, "trace failed: " + e.GetType().Name);
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

        public DateTimeOffset LastStatusAt { get; set; } = startedAt;
    }
}
