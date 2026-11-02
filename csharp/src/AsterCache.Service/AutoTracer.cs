using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
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
    private readonly object _lock = new();
    private Run? _run;

    public AutoTracer(AutoEnv env, AutoStatus status, ILog log, Func<AsterSettings> settings)
    {
        _env = env;
        _status = status;
        _log = log;
        _settings = settings;
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

    public void Start(GameSettings g)
    {
        string? refused;
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
                    _run = new Run(g, _env.StartTrace(g.ExeNames), _env.Now());
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
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

        _status.Set(g.Name, g.Source, null, AutoStage.Tracing, null);
        _log.Info($"{g.Name}: auto trace started");
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
    }

    private void Finish(Run run, string why)
    {
        GameSettings g = run.Game;
        try
        {
            IReadOnlyList<TraceRecord> records = run.Session.Stop();
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
            lock (_lock)
            {
                _run = null;
            }
        }
    }

    private sealed class Run(GameSettings game, IAutoTrace session, DateTimeOffset startedAt)
    {
        public GameSettings Game { get; } = game;

        public IAutoTrace Session { get; } = session;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public Dictionary<int, long> Last { get; } = [];

        public bool Primed { get; set; }

        public long Read { get; set; }
    }
}
