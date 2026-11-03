using System.Collections.Concurrent;
using AsterCache.Core;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 서비스 자동 모드의 묶음: 발견 등록, 휴리스틱, 자동 트레이스, 자동 계획·재배치, 최근 게임 예열 선택. 서비스가 관리자이고 폴더 권한이 안전할 때만 만듭니다 (쓰기·이동을 하므로).
/// 각 동작은 예외를 삼켜 로그로 남깁니다 (자동 동작이 서비스를 죽이지 않게). 설정은 다시 읽을 때마다 SetSettings 로 바꿉니다.
public sealed class AutoMode
{
    public static readonly TimeSpan FirstDiscoveryDelay = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan DiscoveryEvery = TimeSpan.FromHours(1);
    public static readonly TimeSpan HeuristicEvery = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan TraceEvery = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan SummaryEvery = TimeSpan.FromHours(1);

    private readonly AutoEnv _env;
    private readonly ILog _log;
    private readonly AutoRegistry _registry;
    private readonly AutoTracer _tracer;
    private readonly AutoRelocator _relocator;
    private readonly Prewarmer? _prewarm;
    private readonly ProfileFeed _feed;
    private volatile AsterSettings _settings;
    private DateTimeOffset? _summaryNext; // 재적용 틱 스레드에서만 씀
    private readonly SemaphoreSlim _tickRequest = new(0, 1); // 발견이 새 게임을 등록했을 때 30 초 틱을 기다리지 않게 깨움 (요청은 하나로 합쳐짐)

    public AutoMode(ConfigEditor editor, AutoEnv env, AutoStatus status, ILog log, AsterSettings settings, Prewarmer? prewarm = null, IFeedHttp? feedHttp = null)
    {
        _env = env;
        _prewarm = prewarm;
        Status = status;
        _log = log;
        _settings = settings;
        env.GameRunningOnDisk ??= drive => env.AnyRunning([.. _settings.Games.Where(g => env.SameDisk(drive, g.OriginRoot)).SelectMany(g => g.ExeNames).Distinct(StringComparer.OrdinalIgnoreCase)]); // 회복 표본은 같은 물리 디스크에서 게임이 도는 동안 시작하지 않음
        _registry = new AutoRegistry(editor, env, status, log, () => _settings);
        _feed = new ProfileFeed(Path.GetDirectoryName(editor.ConfigPath) ?? string.Empty, feedHttp ?? new HttpFeedClient(), log, env.Now, env.ExeFiles);
        _tracer = new AutoTracer(env, status, log, () => _settings);
        _relocator = new AutoRelocator(env, status, log, () => _settings, prewarm);
        if (prewarm is not null)
        {
            prewarm.Recovery = _relocator.Recovery; // 재배치 직후 예열을 드라이브 회복까지 고정
            prewarm.WarmMeasured += (g, mbps) => Guard("warm mbps note", () => _relocator.NoteWarmMbps(g, mbps));
            prewarm.WarmMeasured += (g, _) => Guard("hit percent", () => RecordHitAfterWarm(prewarm, g)); // 예열 직후 (게임 시작 전) 에만 잼
            prewarm.ColdWarmed += (g, label) => Guard("cold prewarm note", () => _relocator.NoteColdWarmed(g, label)); // 서비스 시작·트리거·틱 어디서 예열하든 같은 표시
            prewarm.Progress += (g, done, total) => Guard("prewarm progress", () => _relocator.NoteProgress(g, done, total));
            prewarm.StreamEnded += (g, complete) => Guard("prewarm end", () => _relocator.NoteStreamEnded(g, complete));
        }
    }

    private readonly CancellationTokenSource _stop = new(); // 서비스 종료: 게임 시작 때 도는 로딩 경계 감시를 멈춤
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _boundaryWatch = new(StringComparer.OrdinalIgnoreCase); // 게임별 로딩 경계 감시 (게임 종료 때 취소)

    /// 시험용 seam: 로딩 경계 감시 본체 (비우면 LoadBoundaryWatch.Run).
    public Action<GameSettings, CancellationToken>? BoundaryRunner { get; set; }

    // 예열이 방금 끝났으면 핫 집합 표본 몇 곳의 적중률을 잼. 같은 물리 디스크에서 게임이 도는 중이면 게임 디스크를 건드리지 않으려고 건너뜀.
    private void RecordHitAfterWarm(Prewarmer prewarm, GameSettings g)
    {
        if (!_settings.Auto.Enabled || _env.GameRunningOnDisk?.Invoke(PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant()) == true)
        {
            return;
        }

        if (prewarm.HitPercent(g) is { } hit)
        {
            Status.RecordHit(g.Name, hit, _env.Now());
        }
    }

    public AutoStatus Status { get; }

    public AutoRegistry Registry => _registry;

    public ProfileFeed Feed => _feed;

    public AutoTracer Tracer => _tracer;

    public void SetSettings(AsterSettings s)
    {
        AsterSettings old = _settings;
        _settings = s;
        _registry.SettingsReloaded(s);
        if (s.Games.Any(g => g.Source == "auto" && old.Games.All(o => o.Name != g.Name)))
        {
            RequestTick(); // 새로 등록된 자동 게임 (설정에 들어온 뒤라야 틱이 봄): 부트스트랩·콜드 예열을 다음 30 초 틱까지 미루지 않음
        }
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
            if (a.Trace && AutoTracer.RunCount(g, _env.FileExists) < a.TraceRuns)
            {
                _tracer.Start(g);
            }

            if (a.LoadBoundary)
            {
                // 적중률은 여기서 재지 않음 (게임 디스크 읽기): 예열 직후 값이 auto_status 에 남아 있음. 게임이 끝나면 NoteGameExit 가 멈춤
                var watch = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                _boundaryWatch[g.Name] = watch;
                try
                {
                    // 등록 전에 게임이 끝났으면 NoteGameExit 가 놓치므로 등록 직후 확인 (등록 뒤의 종료는 NoteGameExit 가 취소)
                    if (!_env.AnyRunning(g.ExeNames))
                    {
                        watch.Cancel();
                    }

                    if (BoundaryRunner is { } run)
                    {
                        run(g, watch.Token);
                    }
                    else
                    {
                        LoadBoundaryWatch.Run(g, Status, _log, watch.Token, _env.Now);
                    }
                }
                finally
                {
                    _ = _boundaryWatch.TryRemove(new KeyValuePair<string, CancellationTokenSource>(g.Name, watch));
                }
            }
        });
    }

    /// 게임 종료 때 (Worker 의 종료 감지에서 부름): 그 게임의 로딩 경계 감시를 멈춥니다.
    public void NoteGameExit(GameSettings g)
    {
        if (_boundaryWatch.TryRemove(g.Name, out CancellationTokenSource? watch))
        {
            watch.Cancel();
        }
    }

    /// 정기 재적용이 지나야 하는 자동 모드와 같은 게이트 (재적용 루프 스레드에서 부름). 게이트 자체가 실패하면 보수적으로 미룸.
    public ReapplyVerdict? ReapplyBlock(GameSettings g)
    {
        try
        {
            return _relocator.ReapplyBlock(g);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Error("auto reapply gate failed", e);
            return ReapplyVerdict.NotIdle;
        }
    }

    /// 정기 재적용이 쓴 바이트를 일일 예산·회복 추적에 알림 (재적용 루프 스레드에서 부름).
    public void ReapplySpent(GameSettings g, long movedBytes) => Guard("reapply note", () => _relocator.NoteReapplied(g, movedBytes));

    /// 재적용 틱 (30 초) 마다. 호출한 스레드에서 이동까지 돌므로 재적용과 겹치지 않습니다 (예열은 따로 돌아 틱을 늦추지 않음).
    public void RelocateTick(ReapplyGate gate, CancellationToken ct)
    {
        Guard("relocation", () => _relocator.Tick(gate, ct));
        Guard("summary", LogSummaryIfDue);
    }

    /// 재적용 루프가 다음 틱을 지금 한 번 돌게 요청합니다. 틱은 재적용 루프 한 곳에서만 돌아 (재배치기는 한 번에 한 틱) 겹쳐 실행되지 않습니다.
    public void RequestTick()
    {
        try
        {
            _tickRequest.Release();
        }
        catch (SemaphoreFullException)
        {
            // 이미 요청됨
        }
    }

    /// 다음 틱까지 기다립니다: delay 가 지나면 false, 그 전에 RequestTick 이 불리면 true.
    public Task<bool> WaitForTickAsync(TimeSpan delay, CancellationToken ct) => _tickRequest.WaitAsync(delay, ct);

    /// 게임 exe 가 떴을 때 (감시기 스레드에서 바로): 자동 모드의 예열 작업을 취소합니다.
    public void CancelWarm() => _relocator.CancelWarm();

    /// 예열 유지 같은 다른 예열 작업이 함께 쓰는 취소 신호.
    public CancellationToken WarmToken => _relocator.WarmToken;

    // 서비스 시작 뒤 첫 틱과 매 1 시간: 자동 모드의 게임 수를 한 줄로 (자동 모드가 꺼져 있으면 남기지 않음).
    private void LogSummaryIfDue()
    {
        AsterSettings s = _settings;
        DateTimeOffset now = _env.Now();
        if (!s.Auto.Enabled || now < _summaryNext)
        {
            return;
        }

        _summaryNext = now + SummaryEvery;
        IReadOnlyList<GameSettings> games = s.Games;
        int Count(string stage) => games.Count(g => Status.Get(g.Name)?.Stage == stage);
        int auto = games.Count(g => g.Source == "auto");
        int prewarmed = _prewarm is null ? 0 : games.Count(g => _prewarm.LastWarmed(g.Name) is not null);
        _log.Info($"auto summary: registered {games.Count} (auto {auto}, manual {games.Count - auto}), learning {Count(AutoStage.Learning)}, waiting_idle {Count(AutoStage.WaitingIdle)}, "
            + $"relocating {Count(AutoStage.Relocating)}, done {Count(AutoStage.Done)}, prewarmed {prewarmed}, failed {Count(AutoStage.Failed)}, skipped {Count(AutoStage.Skipped)}");
    }

    /// 서비스 시작 예열 뒤 추가로 예열할 게임: 최근에 한 순서로 prewarm_recent 개, 0 이면 전부, -1 이면 없음 (on_service_start 로 이미 도는 게임은 제외). 실제로 올리는 양은 예열 묶음의 RAM 예산이 정합니다.
    public IReadOnlyList<GameSettings> StartupRecent()
    {
        AsterSettings s = _settings;
        return s.Auto.Enabled
            ? Recent(s.Games.Where(g => !g.Prewarm.OnServiceStart), Status.LastPlayed(), s.Auto.PrewarmRecent, Status.Plays(), _env.Now().ToLocalTime())
            : [];
    }

    /// 트리거 프로그램에 걸린 게임 중 예열할 것: 수동 게임은 전부, 자동 등록 게임은 최근 순 상위 prewarm_recent 개 (0 이면 전부, -1 이면 자동 등록 게임은 없음, 자동 모드가 꺼져 있으면 전부).
    public IEnumerable<GameSettings> NarrowTriggered(IReadOnlyList<GameSettings> hit)
    {
        AsterSettings s = _settings;
        return s.Auto.Enabled
            ? hit.Where(g => g.Source != "auto").Concat(Recent(hit.Where(g => g.Source == "auto"), Status.LastPlayed(), s.Auto.PrewarmRecent, Status.Plays(), _env.Now().ToLocalTime()))
            : hit;
    }

    /// last_played 가 있는 게임을 예열 점수 (최근성 + 시간대 습관) 순으로 n 개 (n 이 0 이면 전부, 음수면 없음), 동점이면 최근순. plays 를 안 주면 최근순입니다.
    public static IReadOnlyList<GameSettings> Recent(IEnumerable<GameSettings> games, IReadOnlyDictionary<string, DateTimeOffset> lastPlayed, int n,
        IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>>? plays = null, DateTimeOffset now = default)
    {
        double Score(GameSettings g) => plays is not null && plays.TryGetValue(g.Name, out IReadOnlyList<DateTimeOffset>? p) ? PrewarmScore.Compute(p, now) : 0;
        IEnumerable<GameSettings> ordered = games.Where(g => lastPlayed.ContainsKey(g.Name)).OrderByDescending(Score).ThenByDescending(g => lastPlayed[g.Name]).ThenBy(g => g.Name, StringComparer.Ordinal);
        return [.. n < 0 ? [] : n > 0 ? ordered.Take(n) : ordered];
    }

    /// 발견 한 번 + 공유 프로필 맞추기 (묶음은 첫 주기와 7 일마다 받음). 둘은 서로 독립이라 앞이 실패해도 뒤를 돌립니다.
    public void DiscoverAndFeed()
    {
        Guard("discovery", () => _registry.DiscoverOnce());
        Guard("shared profiles", () => _feed.Run(_settings, _registry.LastScan));
    }

    /// 발견 (처음 10 초 뒤, 이후 1 시간마다), 휴리스틱 (2 초마다), 트레이스 감시 (1 초마다) 를 취소될 때까지 돌립니다.
    public Task RunAsync(CancellationToken ct)
    {
        _ = ct.Register(_stop.Cancel);
        return RunLoops(ct);
    }

    private Task RunLoops(CancellationToken ct) => Task.WhenAll(
        Loop(FirstDiscoveryDelay, DiscoveryEvery, "discovery", DiscoverAndFeed, ct),
        Loop(HeuristicEvery, HeuristicEvery, "heuristic", _registry.HeuristicTick, ct),
        Loop(TraceEvery, TraceEvery, "trace", _tracer.Step, ct));

    /// 서비스 종료: 돌고 있는 ETW 세션을 정리합니다.
    public void Shutdown()
    {
        _stop.Cancel();
        _tracer.Shutdown();
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
