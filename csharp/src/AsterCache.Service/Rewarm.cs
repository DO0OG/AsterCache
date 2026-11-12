using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 예열 유지: rewarm_minutes 마다, 예열한 게임 자신이 안 돌고 같은 디스크의 다른 등록 게임이 안 돌거나 돌아도 한가하고 (Prewarmer.BusyOthers) 그 게임 드라이브 처리량이 자동 재배치의 유휴 기준 아래일 때 (CPU 부하는 보지 않음), 예열한 적 있는 게임의 핫 집합 몇 곳을 읽어 보고 식었으면 (CacheProbe.Cold: 표본 8 곳 중 2 곳 이상이 CacheProbe.ColdThresholdMs 초과) 다시 예열합니다.
public sealed class Rewarmer
{
    private readonly Prewarmer _prewarm;
    private readonly PrewarmEnv _env;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _now;
    private GameExitTracker? _exitTracker;
    private readonly object _nextLock = new(); // Tick (유지 점검 스레드) 와 PullIn (재적용 틱 스레드) 이 같이 쓰는 _next 보호
    private DateTimeOffset? _next;
    private readonly object _checkLock = new();
    private readonly Dictionary<string, string> _checkState = new(StringComparer.OrdinalIgnoreCase); // 게임별 마지막 재확인 결과 (로그를 바뀔 때만 남김)

    public Rewarmer(Prewarmer prewarm, PrewarmEnv env, ILog log, Func<DateTimeOffset> now)
    {
        _prewarm = prewarm;
        _env = env;
        _log = log;
        _now = now;
    }

    public void SetGameExitTracker(GameExitTracker tracker) => _exitTracker = tracker;

    public void SetGames(IReadOnlyList<GameSettings> games)
    {
        HashSet<string> live = new(games.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
        lock (_checkLock)
        {
            foreach (string gone in _checkState.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _checkState.Remove(gone);
            }
        }
    }

    /// 게임이 끝났을 때: 다음 점검을 auto.rewarm_after_exit_min 뒤로 당깁니다 (이미 더 이르면 그대로). 게이트 (게임 실행 중·디스크 바쁨) 와 식었을 때만 읽는 점검은 Tick 이 그대로 적용합니다.
    public void PullIn(AutoSettings auto, DateTimeOffset? heavyWorkNotBefore = null)
    {
        if (!auto.Enabled || auto.RewarmMinutes <= 0 || heavyWorkNotBefore is null && auto.RewarmAfterExitMin <= 0)
        {
            return;
        }

        DateTimeOffset due = heavyWorkNotBefore ?? _now() + TimeSpan.FromMinutes(auto.RewarmAfterExitMin);
        lock (_nextLock)
        {
            if (_next is not { } next || due < next)
            {
                _next = due;
            }
        }
    }

    public void Tick(AutoSettings auto, CancellationToken ct = default)
    {
        if (!auto.Enabled || auto.RewarmMinutes <= 0)
        {
            lock (_nextLock)
            {
                _next = null;
            }

            return;
        }

        _env.CpuPercent(); // 틱마다 불러 두 표본 사이가 한 틱이 되게 함 (재예열 판정에는 쓰지 않음)

        DateTimeOffset now = _now();
        lock (_nextLock)
        {
            if (_next is null)
            {
                _next = now + TimeSpan.FromMinutes(auto.RewarmMinutes); // 켜진 때부터 한 주기 뒤에 처음 점검
                return;
            }

            if (now < _next)
            {
                return;
            }

            _next = now + TimeSpan.FromMinutes(auto.RewarmMinutes);
        }

        IReadOnlyList<GameSettings> warmed = _prewarm.Warmed();

        if (_prewarm.Games.Any(g => _prewarm.Relocating(g.Name)))
        {
            _log.Info("rewarm check skipped: a relocation is running");
            return;
        }

        var budget = _prewarm.WarmBudget(); // 순위가 있으면 예산 안에 드는 게임만 점검 (순위가 바뀌면 다음 점검에서 대상도 바뀜)
        DateTimeOffset? nextGateDue = null;
        foreach (GameSettings g in warmed)
        {
            if (ct.IsCancellationRequested)
            {
                _log.Info("rewarm check stopped: a game started");
                return;
            }

            if (_prewarm.Games.Any(x => _prewarm.Relocating(x.Name)))
            {
                _log.Info("rewarm check stopped: a relocation started"); // 점검 주기는 이미 넘어가 있어 다음 주기에 다시 봄
                return;
            }

            if (_exitTracker?.HeavyWorkNotBefore(g, _prewarm.Games, _env.SameDisk, auto.RewarmAfterExitMin) is { } heavyWorkDue && now < heavyWorkDue)
            {
                if (nextGateDue is null || heavyWorkDue < nextGateDue)
                {
                    nextGateDue = heavyWorkDue;
                }

                NoteCheck(g, "post-exit", $"{g.Name}: rewarm check deferred until {heavyWorkDue:O} after game exit");
                continue;
            }

            if (budget is { } b && !b.In.Contains(g.Name))
            {
                NoteCheck(g, "budget", $"{g.Name}: rewarm check skipped ({Prewarmer.WarmBudgetSkipDetail(b.CapMib, b.UsedMib)})");
                continue;
            }

            if (_env.GameRunning(g))
            {
                string blockers = Blockers(g);
                NoteCheck(g, "game-running|" + blockers, $"{g.Name}: rewarm check skipped: a game is running" + blockers); // 실행 중인 게임 자신은 건드리지 않음 (같은 사유·같은 프로세스면 점검 주기마다 반복하지 않음)
                continue;
            }

            if (_prewarm.BusyOthers(g) is { Count: > 0 } busy)
            {
                NoteCheck(g, "drive-game-running|" + string.Join(",", busy.Select(x => x.Name)), $"{g.Name}: rewarm check skipped: a game is running on the same drive and is not idle" + Blockers(busy.ToArray())); // 한가한 다른 게임은 막지 않음
                continue;
            }

            if (_env.DiskBytesPerSecond(g) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond)
            {
                NoteCheck(g, "busy", $"{g.Name}: rewarm check skipped (the game drive is busy or its load is unknown)");
                continue;
            }

            Check(g, ct, budget is { } bg && bg.AllocMib.GetValueOrDefault(g.Name) is > 0 and long room ? room : null);
        }

        if (nextGateDue is { } due)
        {
            lock (_nextLock)
            {
                if (_next is not { } next || due < next)
                {
                    _next = due;
                }
            }
        }
    }

    /// 결과(상태)가 바뀔 때만 남김: 틱마다 같은 결과를 반복해 쓰지 않음 (예열 동작은 그대로).
    private void NoteCheck(GameSettings g, string state, string message)
    {
        bool changed;
        lock (_checkLock)
        {
            if (!_prewarm.Games.Any(game => string.Equals(game.Name, g.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            changed = !_checkState.TryGetValue(g.Name, out string? last) || last != state;
            if (changed)
            {
                _checkState[g.Name] = state;
            }
        }

        if (changed)
        {
            _log.Info(message);
        }
    }

    // 건너뛴 사유에 붙일 차단 프로세스 설명 (없으면 빈 문자열).
    private string Blockers(params GameSettings[] games)
    {
        string blockers = string.Join("; ", games.Select(_env.DescribeBlockers).Where(b => b is not null));
        return blockers.Length == 0 ? string.Empty : $" (blocked by {blockers})";
    }

    private void Check(GameSettings g, CancellationToken ct, long? roomMib)
    {
        if (_prewarm.RewarmAlreadyCovered(g))
        {
            NoteCheck(g, "recent-warm", $"{g.Name}: rewarm check skipped (prewarm is running or recently completed)");
            return;
        }

        ProbeResult? probe;
        try
        {
            probe = _prewarm.ColdSamples(g) is { } samples ? _prewarm.ProbeSamplesForGame(g, samples, "rewarm sample", ct) : _prewarm.ProbeFront(g, ct); // 기록 없이 예열한 게임은 그때 읽은 파일 목록에서 고른 표본
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            _log.Warn($"{g.Name}: rewarm check failed: {e.GetType().Name}");
            return;
        }

        if (probe is null)
        {
            NoteCheck(g, "nosamples", $"{g.Name}: rewarm check skipped (no readable samples)");
        }
        else if (_prewarm.SupplementPending(g.Name))
        {
            NoteCheck(g, "supplement", $"{g.Name}: rewarm check: the rest of the hot set is not read yet, supplementing once the drive has stayed idle"); // 부분 예열 뒤에는 표본이 cold 든 warm 이든 전체 재예열로 가지 않고, 보충은 SupplementTick 과 같은 연속 유휴 판정을 거침
            if (!_prewarm.SupplementWhenIdle(g, ct) && probe.Cold && _prewarm.MemoryPartialPending(g.Name))
            {
                NoteCheck(g, "partial-cold", $"{g.Name}: rewarm check: the part read into memory went cold ({probe.Slow}/{probe.Samples} samples over {CacheProbe.ColdThresholdMs:F1} ms), reading it again (cooldown ignored)"); // 메모리가 모자라 앞부분만 읽은 게임: 읽은 구간이 밀려났을 때만 다시 (남은 구간은 표본에 넣지 않음)
                _prewarm.RewarmGame(g, ct, roomMib);
            }
        }
        else if (!probe.Cold)
        {
            NoteCheck(g, "warm", $"{g.Name}: rewarm check: warm ({probe.Slow}/{probe.Samples} samples over {CacheProbe.ColdThresholdMs:F1} ms, average {probe.AverageMs:F2} ms)");
        }
        else
        {
            NoteCheck(g, "cold", $"{g.Name}: rewarm check: cold ({probe.Slow}/{probe.Samples} samples over {CacheProbe.ColdThresholdMs:F1} ms, average {probe.AverageMs:F2} ms), prewarming again (cooldown ignored)");
            _prewarm.RewarmGame(g, ct, roomMib);
        }
    }
}
