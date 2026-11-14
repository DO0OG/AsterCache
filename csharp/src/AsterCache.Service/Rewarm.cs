using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Windows;
using WarmBudgetInfo = (System.Collections.Generic.HashSet<string> In, long CapMib, long UsedMib, System.Collections.Generic.Dictionary<string, long> AllocMib);

namespace AsterCache.Service;

/// 예열 유지: rewarm_minutes 마다, 예열한 게임 자신이 안 돌고 같은 디스크의 다른 등록 게임이 안 돌거나 돌아도 한가하고 (Prewarmer.BusyOthers) 그 게임 드라이브 처리량이 자동 재배치의 유휴 기준 아래일 때 (CPU 부하는 보지 않음), 예열한 적 있는 게임의 핫 집합 몇 곳을 읽어 보고 식었으면 (CacheProbe.Cold: 표본 8 곳 중 2 곳 이상이 CacheProbe.ColdThresholdMs 초과) 다시 예열합니다. 사다리 (Solar) 가 목록의 일부가 메모리에 없는 것을 찾은 게임은 표본이 warm 이어도 다시 예열합니다 (게임마다 마지막 예열 뒤 rewarm_minutes 가 지난 뒤에만).
public sealed partial class Rewarmer
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
    private readonly Dictionary<string, DateTimeOffset> _missingTriedAt = new(StringComparer.OrdinalIgnoreCase); // 게임별로 사다리의 다시 채움 표시로 재예열을 마지막 시도한 시각 (성공·실패·건너뜀 모두), _checkLock 으로 보호, 메모리에만 둠

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

            foreach (string gone in _missingTriedAt.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _missingTriedAt.Remove(gone);
            }
        }

        _prewarm.Solar.Prune(live);
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

            if (ReadBlock(g, now, auto, budget) is { } block)
            {
                if (block.Due is { } gateDue && (nextGateDue is null || gateDue < nextGateDue))
                {
                    nextGateDue = gateDue;
                }

                NoteCheck(g, block.State, block.Message);
                continue;
            }

            Check(g, ct, RoomOf(g, budget), auto, now);
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

    // 점검과 사다리가 이 게임 디스크를 지금 읽어도 되는가: 막히면 (상태 키, 로그 문구, 게이트가 풀릴 시각 (모르면 null)) 를, 괜찮으면 null 을 돌려줍니다.
    // ignoreDriveLoad: 디스크를 읽지 않는 만지기 (사다리 새로 고침) 는 다른 프로세스의 드라이브 사용량에 막히지 않음. 게임 실행·종료 직후·예산 게이트는 그대로.
    private (string State, string Message, DateTimeOffset? Due)? ReadBlock(GameSettings g, DateTimeOffset now, AutoSettings auto, WarmBudgetInfo? budget, bool ignoreDriveLoad = false)
    {
        if (_exitTracker?.HeavyWorkNotBefore(g, _prewarm.Games, _env.SameDisk, auto.RewarmAfterExitMin) is { } heavyWorkDue && now < heavyWorkDue)
        {
            return ("post-exit", $"{g.Name}: rewarm check deferred until {heavyWorkDue:O} after game exit", heavyWorkDue);
        }

        if (budget is { } b && !b.In.Contains(g.Name))
        {
            return ("budget", $"{g.Name}: rewarm check skipped ({Prewarmer.WarmBudgetSkipDetail(b.CapMib, b.UsedMib)})", null);
        }

        if (_env.GameRunning(g))
        {
            string blockers = Blockers(g);
            return ("game-running|" + blockers, $"{g.Name}: rewarm check skipped: a game is running" + blockers, null); // 실행 중인 게임 자신은 건드리지 않음 (같은 사유·같은 프로세스면 점검 주기마다 반복하지 않음)
        }

        if (_prewarm.BusyOthers(g) is { Count: > 0 } busy)
        {
            return ("drive-game-running|" + string.Join(",", busy.Select(x => x.Name)), $"{g.Name}: rewarm check skipped: a game is running on the same drive and is not idle" + Blockers(busy.ToArray()), null); // 한가한 다른 게임은 막지 않음
        }

        if (!ignoreDriveLoad && DriveBusy(g))
        {
            return ("busy", $"{g.Name}: rewarm check skipped (the game drive is busy or its load is unknown)", null);
        }

        return null;
    }

    // 게임 드라이브가 바쁘거나 처리량을 모르는가.
    private bool DriveBusy(GameSettings g) => _env.DiskBytesPerSecond(g) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond;

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

    // 핫 집합 표본 점검 (재예열 점검과 사다리 새로 고침이 같이 씀): 읽지 못하면 null, 예외로 실패하면 failed (경고 한 줄).
    private ProbeResult? Probe(GameSettings g, CancellationToken ct, out bool failed)
    {
        failed = false;
        try
        {
            return _prewarm.ColdSamples(g) is { } samples ? _prewarm.ProbeSamplesForGame(g, samples, "rewarm sample", ct) : _prewarm.ProbeFront(g, ct); // 기록 없이 예열한 게임은 그때 읽은 파일 목록에서 고른 표본
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            _log.Warn($"{g.Name}: rewarm check failed: {e.GetType().Name}");
            failed = true;
            return null;
        }
    }

    private void Check(GameSettings g, CancellationToken ct, long? roomMib, AutoSettings auto, DateTimeOffset now)
    {
        if (_prewarm.RewarmAlreadyCovered(g))
        {
            NoteCheck(g, "recent-warm", $"{g.Name}: rewarm check skipped (prewarm is running or recently completed)");
            return;
        }

        // 사다리가 목록의 일부가 메모리에 없는 것을 찾았으면 표본이 warm 이어도 식은 것으로 봅니다 (표본은 8 곳뿐). 보충 대기 게임은 보충이 사다리를 다시 만들고, 트리거가 기다리는 동안은 표시를 둔 채 미룸.
        Solar solar = _prewarm.Solar;
        if (solar.HasMissing(g.Name) && !_prewarm.SupplementPending(g.Name) && !_prewarm.TriggerWaitPending())
        {
            // 점검이 당겨져도 (게임 종료·유예) 표시로 인한 재예열은 게임마다 마지막 예열과 마지막 시도 중 늦은 쪽 뒤 rewarm_minutes 가 지나야 함:
            // 재예열 뒤 사다리가 곧 표시를 다시 세우거나 예열이 실패·건너뛰어도 HDD 를 주기보다 잦게 읽지 않음
            DateTimeOffset? last = _prewarm.LastWarmed(g.Name);
            lock (_checkLock)
            {
                if (_missingTriedAt.TryGetValue(g.Name, out DateTimeOffset tried) && (last is not { } l || tried > l))
                {
                    last = tried;
                }
            }

            if (last is { } since && now >= since && now - since < TimeSpan.FromMinutes(auto.RewarmMinutes))
            {
                NoteCheck(g, "ladder-missing-wait", $"{g.Name}: rewarm check: part of the list is not in memory (found by the ladder), waiting until {auto.RewarmMinutes} min after the last prewarm attempt");
                return;
            }

            _ = solar.TakeMissing(g.Name);
            lock (_checkLock)
            {
                _checkState[g.Name] = "ladder-missing"; // 로그는 매번 남기고, 채운 뒤 다음 점검의 warm 도 새 상태로 기록되게 함
                _missingTriedAt[g.Name] = now;
            }

            _log.Info($"{g.Name}: rewarm check: part of the list is not in memory (found by the ladder), prewarming again (cooldown ignored)");
            PrewarmResult result = _prewarm.RewarmGame(g, ct, roomMib);
            if (!result.Ran && result.Stats is not { Bytes: > 0 })
            {
                solar.RestoreMissing(g.Name); // 읽기 전에 건너뜀 (메모리·지연·대여 거절·재배치 등): 다음 점검에서 다시 시도
            }

            return;
        }

        ProbeResult? probe = Probe(g, ct, out bool failed);
        if (failed)
        {
            return;
        }

        if (probe is null)
        {
            NoteCheck(g, "nosamples", $"{g.Name}: rewarm check skipped (no readable samples)");
        }
        else if (_prewarm.SupplementPending(g.Name))
        {
            NoteCheck(g, "supplement", $"{g.Name}: rewarm check: the rest of the hot set is not read yet, supplementing once the drive has stayed idle"); // 부분 예열 뒤에는 표본이 cold 든 warm 이든 전체 재예열로 가지 않고, 보충은 SupplementTick 과 같은 연속 유휴 판정을 거침
            bool supplemented = _prewarm.SupplementWhenIdle(g, ct);
            if (!supplemented && !probe.Cold)
            {
                LadderWaitingSupplement(g, ct); // 보충이 메모리를 기다리는 중이면 (곧 더 읽을 수 있는 상태가 아니면) 읽은 범위의 첫 사다리를 만듦
            }
            else if (!supplemented && probe.Cold && _prewarm.MemoryPartialPending(g.Name))
            {
                NoteCheck(g, "partial-cold", $"{g.Name}: rewarm check: the part read into memory went cold ({probe.Slow}/{probe.Samples} samples over {CacheProbe.ColdThresholdMs:F1} ms, available {_env.AvailableRamMib()} MiB), reading it again (cooldown ignored)"); // 메모리가 모자라 앞부분만 읽은 게임: 읽은 구간이 밀려났을 때만 다시 (남은 구간은 표본에 넣지 않음)
                _prewarm.RewarmGame(g, ct, roomMib);
            }
        }
        else if (!probe.Cold)
        {
            NoteCheck(g, "warm", $"{g.Name}: rewarm check: warm ({probe.Slow}/{probe.Samples} samples over {CacheProbe.ColdThresholdMs:F1} ms, average {probe.AverageMs:F2} ms)");
            solar.ClearOverlooked(g.Name);
            LadderUnmarked(g, ct); // 서비스를 다시 시작한 뒤 표식이 없는 게임은 warm 일 때 첫 사다리를 만듦
        }
        else
        {
            NoteCheck(g, "cold", $"{g.Name}: rewarm check: cold ({probe.Slow}/{probe.Samples} samples over {CacheProbe.ColdThresholdMs:F1} ms, average {probe.AverageMs:F2} ms, available {_env.AvailableRamMib()} MiB), prewarming again (cooldown ignored){solar.SinceLadder(g.Name)}");
            _prewarm.RewarmGame(g, ct, roomMib);
        }
    }
}
