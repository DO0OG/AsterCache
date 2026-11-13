using AsterCache.Core.Logging;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// Prewarmer 의 성공하지 못한 일시 예열 (pending warm) 추적과 한가할 때의 재시도.
public sealed partial class Prewarmer
{
    internal DateTimeOffset? PendingRetryAt(string game)
    {
        lock (_gate)
        {
            return _pendingWarm.TryGetValue(game, out PendingWarm? pending) ? pending.RetryAt : null;
        }
    }

    internal bool HasPendingWarm(string game)
    {
        lock (_gate)
        {
            return _pendingWarm.ContainsKey(game);
        }
    }

    private TimeSpan PendingWarmRetryDelay() => _auto.RewarmMinutes > 0
        ? TimeSpan.FromMinutes(_auto.RewarmMinutes)
        : AutoRelocator.RetryEvery;

    private static bool IsPermanentWarmRefusal(string? reason) => reason is "no plan file" or "no zone" or "no trace file" or "outside allowed roots" or GamePolicySkip
        || reason?.StartsWith("failed:", StringComparison.Ordinal) == true;

    private static bool IsRetryableWarmOutcome(string? reason) => reason is Cancelled or RelocationRunning or "startup byte cap"
        or "batch memory budget" or "game is running" or "drive busy" or "nothing read" or PostUpdateDelay
        || reason?.StartsWith("low memory", StringComparison.Ordinal) == true;

    private void TrackPendingWarm(GameSettings game, PrewarmResult result, DateTimeOffset at)
    {
        bool alreadyPending;
        lock (_gate)
        {
            alreadyPending = _pendingWarm.ContainsKey(game.Name);
        }

        if (result.Ran)
        {
            lock (_gate)
            {
                _pendingWarm.Remove(game.Name);
                _idleSince.Remove(game.Name);
                _pendingWarmBudgetSkipped.Remove(game.Name);
            }

            return;
        }

        string? reason = result.SkipReason;
        if (IsCold(game) || IsPermanentWarmRefusal(reason)) // 기록 없는 게임은 대기 재시도 대상이 아니고 런처 트리거·서비스 시작 때만 예열함
        {
            lock (_gate)
            {
                _pendingWarm.Remove(game.Name);
                _idleSince.Remove(game.Name);
                _pendingWarmBudgetSkipped.Remove(game.Name);
            }

            return;
        }

        if (!alreadyPending && (result.CancelDetail == "game started"
            || reason == "game is running" && _env.GameRunning(game)
            || reason == Cancelled && (_env.GameRunning(game) || _coordinator?.IsRegistered(game.Name) == true)
            || result.CancelDetail == "disk read lease refused" && _coordinator?.IsRegistered(game.Name) == true))
        {
            return; // 그 게임 자신이 실행 중일 때의 거절은 게임 종료 경로에 맡김
        }

        if (!alreadyPending && !IsRetryableWarmOutcome(reason))
        {
            return;
        }

        lock (_gate)
        {
            if (_pendingWarm.TryGetValue(game.Name, out PendingWarm? current) && (current.RetryAt > at || current.TriggerExe is not null))
            {
                return; // 재시도 기한 전에 다른 경로가 실패해도 기한을 뒤로 밀지 않음, 당겨 둔 트리거 재시도 표시도 지우지 않음
            }

            _pendingWarm[game.Name] = new(at + PendingWarmRetryDelay(), reason ?? "unsuccessful warm");
            _pendingWarmBudgetSkipped.Remove(game.Name); // 다른 사유로 새로 잡힌 기한은 예산 회복으로 앞당기지 않음
        }
    }

    /// 게임이 끝났을 때 (Worker 가 부름): 재예열 점검을 앞당깁니다.
    public void NoteGameExit(DateTimeOffset? heavyWorkNotBefore = null) => _rewarm.PullIn(_auto, heavyWorkNotBefore);

    private void DropPendingWarm(string game)
    {
        lock (_gate)
        {
            if (_pendingWarm.TryGetValue(game, out PendingWarm? pending) && pending.TriggerExe is not null)
            {
                return; // 당겨 둔 트리거 재시도 표시는 보충 경로가 지우지 않음 (RetryPendingWarmTick 이 먼저 처리)
            }

            _pendingWarm.Remove(game);
            _idleSince.Remove(game);
            _pendingWarmBudgetSkipped.Remove(game);
        }
    }

    private void RetryPendingWarmTick(CancellationToken ct, IReadOnlySet<string>? pendingPartial = null)
    {
        string[] pending;
        DateTimeOffset now = _now();
        lock (_gate)
        {
            pending = [.. _pendingWarm.Where(pair => pair.Value.RetryAt <= now).Select(pair => pair.Key)];
        }

        foreach (string name in pending)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            GameSettings? game = _games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            if (game is null || IsCold(game) || !GamePolicy.AllowsPrewarm(game)) // 기록이 없어진 게임, 정책이 예열을 막은 게임은 대기에서 뺌 (기록 없는 게임은 대기 재시도 대상이 아님)
            {
                lock (_gate)
                {
                    _pendingWarm.Remove(name);
                    _idleSince.Remove(name);
                    _pendingWarmBudgetSkipped.Remove(name);
                }

                continue;
            }

            if (TryRunTriggerRetry(game, ct))
            {
                continue; // 막던 게임이 사라지면 부분 상태 게임도 보충 유휴를 기다리지 않고 트리거 재시도를 먼저 함 (게임 실행·디스크 대여 판정은 Run 이 그대로 함)
            }

            if (pendingPartial?.Contains(name) == true)
            {
                continue; // 이 틱에 보충 경로가 맡은 게임은 부분 읽기와 재시도를 겹치지 않음
            }

            RetryPendingWarmWhenIdle(game, ct);
        }
    }

    private bool RetryPendingWarmWhenIdle(GameSettings game, CancellationToken ct)
    {
        if (!PendingWarmCanRead(game, ct))
        {
            ResetIdleSinceForGame(game.Name);
            return false;
        }

        DateTimeOffset now = _now();
        if (!PendingWarmIdleElapsed(game, now))
        {
            return false;
        }

        if (PendingWarmAllocation(game) is not > 0)
        {
            return false;
        }

        if (!Monitor.TryEnter(_run))
        {
            return false;
        }

        try
        {
            DateTimeOffset enteredAt = _now();
            lock (_gate)
            {
                if (!_pendingWarm.TryGetValue(game.Name, out PendingWarm? pending) || pending.RetryAt > enteredAt)
                {
                    return false;
                }
            }

            if (!PendingWarmCanRead(game, ct))
            {
                ResetIdleSinceForGame(game.Name);
                return false;
            }

            if (!PendingWarmIdleElapsed(game, enteredAt))
            {
                return false;
            }

            long? allocatedMib = PendingWarmAllocation(game);
            if (allocatedMib is not > 0)
            {
                return false;
            }

            PrewarmResult result = Guarded(game, "pending warm retry", pin: false, ignoreCooldown: true, ct, roomMib: allocatedMib);
            return result.Ran;
        }
        finally
        {
            Monitor.Exit(_run);
        }
    }

    private IEnumerable<GameSettings> GamesActiveOnDisk(GameSettings game) => AutoRelocator.SameDriveGames(_games, [game], _env.SameDisk)
        .Where(g => _env.GameRunning(g) || _coordinator?.IsRegistered(g.Name) == true);

    private bool AnyGameActiveOnDisk(GameSettings game) => GamesActiveOnDisk(game).Any();

    private bool PendingWarmCanRead(GameSettings game, CancellationToken ct) => !ct.IsCancellationRequested && BootSettled()
        && HeavyWorkAllowed(game) && !Deferred(game)
        && !AnyGameActiveOnDisk(game) && !_games.Any(g => Relocating(g.Name))
        && _env.DiskBytesPerSecond(game) is { } bps && bps < DiskActivity.BusyBytesPerSecond;

    private bool PendingWarmIdleElapsed(GameSettings game, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (!_idleSince.TryGetValue(game.Name, out DateTimeOffset since))
            {
                _idleSince[game.Name] = since = now;
            }

            return now - since >= IdleNeeded;
        }
    }

    private void ResetIdleSinceForGame(string game)
    {
        lock (_gate)
        {
            _idleSince.Remove(game);
        }
    }

    private long? PendingWarmAllocation(GameSettings game)
    {
        if (WarmBudget() is not { } budget)
        {
            return null;
        }

        long allocatedMib = budget.AllocMib.GetValueOrDefault(game.Name);
        if (!budget.In.Contains(game.Name) || allocatedMib <= 0)
        {
            DeferPendingWarmForBudget(game.Name);
            LogPendingWarmBudgetSkipped(game.Name, budget.CapMib, budget.UsedMib, allocatedMib);
            return 0;
        }

        lock (_gate)
        {
            _pendingWarmBudgetSkipped.Remove(game.Name);
        }

        return allocatedMib;
    }

    private void DeferPendingWarmForBudget(string game)
    {
        DateTimeOffset retryAt = _now() + PendingWarmRetryDelay();
        lock (_gate)
        {
            if (_pendingWarm.TryGetValue(game, out PendingWarm? pending))
            {
                _pendingWarm[game] = pending with { RetryAt = retryAt };
            }
        }
    }

    private void LogPendingWarmBudgetSkipped(string game, long capMib, long usedMib, long allocatedMib)
    {
        var state = (Reason: "cache budget", CapMib: capMib, UsedMib: usedMib, AllocMib: allocatedMib);
        lock (_gate)
        {
            if (_pendingWarmBudgetSkipped.TryGetValue(game, out var previous) && previous == state)
            {
                return;
            }

            _pendingWarmBudgetSkipped[game] = state;
        }

        _log.Info($"{game}: pending prewarm skipped ({WarmBudgetSkipDetail(capMib, usedMib)})");
    }
}
