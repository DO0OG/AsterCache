using AsterCache.Core.Logging;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 서비스 자동 모드의 묶음: 발견 등록, 휴리스틱, 자동 트레이스, 자동 계획·재배치, 최근 게임 예열 선택. 서비스가 관리자이고 폴더 권한이 안전할 때만 만듭니다 (쓰기·이동을 하므로).
/// 각 동작은 예외를 삼켜 로그로 남깁니다 (자동 동작이 서비스를 죽이지 않게). 설정은 다시 읽을 때마다 SetSettings 로 바꿉니다.
public sealed class AutoMode
{
    public static readonly TimeSpan FirstDiscoveryDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan DiscoveryEvery = TimeSpan.FromHours(6);
    public static readonly TimeSpan HeuristicEvery = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan TraceEvery = TimeSpan.FromSeconds(1);

    private readonly AutoEnv _env;
    private readonly ILog _log;
    private readonly AutoRegistry _registry;
    private readonly AutoTracer _tracer;
    private readonly AutoRelocator _relocator;
    private volatile AsterSettings _settings;

    public AutoMode(ConfigEditor editor, AutoEnv env, AutoStatus status, ILog log, AsterSettings settings)
    {
        _env = env;
        Status = status;
        _log = log;
        _settings = settings;
        _registry = new AutoRegistry(editor, env, status, log, () => _settings);
        _tracer = new AutoTracer(env, status, log, () => _settings);
        _relocator = new AutoRelocator(env, status, log, () => _settings);
    }

    public AutoStatus Status { get; }

    public AutoRegistry Registry => _registry;

    public AutoTracer Tracer => _tracer;

    public void SetSettings(AsterSettings s)
    {
        AsterSettings old = _settings;
        _settings = s;
        _registry.SettingsReloaded(s);
        foreach (string gone in old.Games.Select(g => g.Name).Except(s.Games.Select(g => g.Name)))
        {
            Status.Remove(gone); // 설정에서 빠진 게임의 상태는 버림 (같은 이름으로 다시 등록되면 새로 시작)
        }
    }

    /// 등록 게임 exe 가 떴을 때 (감시기 스레드 밖에서 부름): last_played 기록, 원시 트레이스가 없으면 자동 트레이스 시작.
    public void OnGameStart(GameSettings g)
    {
        Guard("game start", () =>
        {
            AutoSettings a = _settings.Auto;
            if (!a.Enabled)
            {
                return;
            }

            bool traced = _env.FileExists(AutoTracer.RawTracePath(g));
            Status.MarkPlayed(g.Name, g.Source, traced ? AutoStage.WaitingIdle : AutoStage.Discovered, _env.Now());
            if (a.Trace && !traced)
            {
                _tracer.Start(g);
            }
        });
    }

    /// 재적용 틱 (30 초) 마다. 호출한 스레드에서 이동까지 돌므로 재적용과 겹치지 않습니다.
    public void RelocateTick(ReapplyGate gate, CancellationToken ct) => Guard("relocation", () => _relocator.Tick(gate, ct));

    /// 서비스 시작 예열 뒤 추가로 예열할 게임: 최근에 한 순서로 prewarm_recent 개 (on_service_start 로 이미 도는 게임은 제외).
    public IReadOnlyList<GameSettings> StartupRecent()
    {
        AsterSettings s = _settings;
        return s.Auto.Enabled && s.Auto.PrewarmRecent > 0
            ? Recent(s.Games.Where(g => !g.Prewarm.OnServiceStart), Status.LastPlayed(), s.Auto.PrewarmRecent)
            : [];
    }

    /// 트리거 프로그램에 걸린 게임 중 예열할 것: 수동 게임은 전부, 자동 등록 게임은 최근 순 상위 prewarm_recent 개 (자동 모드가 꺼져 있으면 전부).
    public IEnumerable<GameSettings> NarrowTriggered(IReadOnlyList<GameSettings> hit)
    {
        AsterSettings s = _settings;
        return s.Auto.Enabled
            ? hit.Where(g => g.Source != "auto").Concat(Recent(hit.Where(g => g.Source == "auto"), Status.LastPlayed(), s.Auto.PrewarmRecent))
            : hit;
    }

    /// last_played 가 있는 게임을 최근 순으로 n 개.
    public static IReadOnlyList<GameSettings> Recent(IEnumerable<GameSettings> games, IReadOnlyDictionary<string, DateTimeOffset> lastPlayed, int n) =>
        [.. games.Where(g => lastPlayed.ContainsKey(g.Name)).OrderByDescending(g => lastPlayed[g.Name]).ThenBy(g => g.Name, StringComparer.Ordinal).Take(n)];

    /// 발견 (처음 1 분 뒤, 이후 6 시간마다), 휴리스틱 (2 초마다), 트레이스 감시 (1 초마다) 를 취소될 때까지 돌립니다.
    public Task RunAsync(CancellationToken ct) => Task.WhenAll(
        Loop(FirstDiscoveryDelay, DiscoveryEvery, "discovery", () => _registry.DiscoverOnce(), ct),
        Loop(HeuristicEvery, HeuristicEvery, "heuristic", _registry.HeuristicTick, ct),
        Loop(TraceEvery, TraceEvery, "trace", _tracer.Step, ct));

    /// 서비스 종료: 돌고 있는 ETW 세션을 정리합니다.
    public void Shutdown()
    {
        _tracer.Shutdown();
        _registry.Shutdown();
    }

    private async Task Loop(TimeSpan first, TimeSpan every, string what, Action work, CancellationToken ct)
    {
        try
        {
            await Task.Delay(first, ct);
            while (true)
            {
                Guard(what, work);
                await Task.Delay(every, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 종료
        }
    }

    private void Guard(string what, Action work)
    {
        try
        {
            work();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Error($"auto {what} failed", e);
        }
    }
}
