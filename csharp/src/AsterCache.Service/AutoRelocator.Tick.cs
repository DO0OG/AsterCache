using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// AutoRelocator 의 재적용 틱: 한 틱에서 할 일을 고르고, 유휴·대기 상태를 따지고, 기록 없는 예열을 예약.
public sealed partial class AutoRelocator
{
    public void Tick(ReapplyGate gate, CancellationToken ct)
    {
        CleanupRemovedGameCaches();
        var warms = new List<(GameSettings Game, string Why)>(); // 틱 끝에서 요청 (재배치를 예열보다 먼저, 예열이 틱을 늦추지 않게)
        try
        {
            TickCore(gate, ct, warms);
            TierTick(gate, ct);
        }
        finally
        {
            warms.ForEach(w => QueueWarm(w.Game, w.Why));
        }
    }

    private void CleanupRemovedGameCaches()
    {
        IReadOnlyList<GameSettings> current = _settings().Games;
        foreach (string name in _removedGameCaches.Keys)
        {
            if (current.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                _ = _removedGameCaches.TryRemove(name, out _);
                continue;
            }

            lock (_warmLock)
            {
                if (_warming == name || _warmQueue.Any(q => string.Equals(q.Game.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }
            }

            _coldTried.TryRemove(name, out _);
            _staleChecked.Remove(name);
            _delayLogged.Remove(name);
            _prefetchTried.Remove(name);
            _supplementChecked.Remove(name);
            _volatileChecked.Remove(name);
            _seeded.Remove(name);
            _smrBudgetLogged.TryRemove(name, out _);
            _limitSeen.Remove(name);
            _extTried.Remove(name);
            _planFailedAt.TryRemove(name, out _);
            _transientHold.TryRemove(name, out _);
            _unreadableHold.TryRemove(name, out _);
            _traceWarmTried.TryRemove(name, out _);
            _progress.TryRemove(name, out _);
            _blockers.Clear(name);
            _ = _removedGameCaches.TryRemove(name, out _);
        }
    }

    // SSD 보조 저장: 재배치가 끝난 (또는 재배치가 꺼진) 게임을 유휴일 때 적용하고, 저널을 유지하고, 꺼졌으면 되돌립니다. 상태 열 표시도 새로 붙입니다.
    private void TierTick(ReapplyGate gate, CancellationToken ct)
    {
        if (_tier is null)
        {
            return;
        }

        AsterSettings s = _settings();
        _tier.Tick(s, s.Auto.Relocate && s.Auto.MaxRelocationsPerDay > 0, gate, ct, HeavyWorkAllowed);
        foreach (GameSettings g in s.Games)
        {
            lock (_noteLock)
            {
                Retag(g);
            }
        }
    }

    private void TickCore(ReapplyGate gate, CancellationToken ct, List<(GameSettings Game, string Why)> warms)
    {
        AsterSettings s = _settings();
        _env.CpuPercent(); // 틱마다 불러 두 표본 사이가 한 틱이 되게 함 (재배치 유휴 판정에는 쓰지 않음)
        if (!s.Auto.Enabled)
        {
            return;
        }

        _recovery.Poll();
        RecoverReplan(s);
        ReplanChanged(s);
        ReplanExtended(s);
        ReplanLegacyVolatile(s);
        ReplanProfileSupplement(s);
        PrefetchBootstrap(s);
        WarmCold(s, warms);
        bool relocateOn = s.Auto.Relocate && s.Auto.MaxRelocationsPerDay > 0;
        IReadOnlyDictionary<string, DateTimeOffset> played = _status.LastPlayed();
        List<GameSettings> work = s.Games.Where(g => GamePolicy.AllowsTracking(g) && !PostUpdateDelayed(g, s) && NeedsWork(g, relocateOn && GamePolicy.AllowsRelocate(g))) // 끄거나 일시 중지한 게임은 계획·예열·재배치 모두 건너뜀
            .OrderByDescending(g => played.GetValueOrDefault(g.Name, DateTimeOffset.MinValue)).ThenBy(g => g.Name, StringComparer.Ordinal).ToList();
        DateTimeOffset? held = relocateOn ? _status.RelocationHeldUntil() : null; // 상태 파일을 못 읽어 막는 중
        bool limitReached = relocateOn && (held is not null || _status.RelocationsWithin(DailyWindow) >= s.Auto.MaxRelocationsPerDay); // Note 보다 먼저 정해 같은 상태 로그가 오락가락하며 쌓이지 않게
        long used = _status.RelocationBytesWithin(DailyWindow);
        var ready = new List<GameSettings>();
        foreach (GameSettings g in work)
        {
            if (!HeavyWorkAllowed(g))
            {
                continue;
            }

            if (_status.Get(g.Name) is { Stage: AutoStage.Failed, Detail: { } failedDetail } && IsExtensionFailure(failedDetail))
            {
                _extensionRecovery[g.Name] = 0; // 아래 Note 가 실패 문구를 덮기 전에 표시: 재개 때 새 이동보다 남은 되돌리기와 보관부터
            }

            if (_recovery.IsRelocationHeld(DriveOf(g)))
            {
                WarmNewTrace(g, warms); // 예열은 막지 않음
                Note(g, AutoStage.WaitingIdle, WaitDetail(g, DriveRecovering));
                continue;
            }

            if (RecoveryHeldUntil(g) is { } heldUntil)
            {
                WarmNewTrace(g, warms); // 예열은 막지 않음
                Note(g, AutoStage.WaitingIdle, HoldText(heldUntil)); // 점유 충돌 보류: 재시작해도 풀리지 않음 (auto_status 에 저장)
                continue;
            }

            if (_transientHold.TryGetValue(g.Name, out (DateTimeOffset Until, string Text) transient))
            {
                if (transient.Until > _env.Now())
                {
                    WarmNewTrace(g, warms);
                    Note(g, AutoStage.WaitingIdle, transient.Text);
                    continue;
                }

                _ = _transientHold.TryRemove(g.Name, out _);
            }

            if (_unreadableHold.TryGetValue(g.Name, out (DateTimeOffset Until, string Text) unreadable))
            {
                if (unreadable.Until > _env.Now())
                {
                    WarmNewTrace(g, warms);
                    Note(g, AutoStage.WaitingIdle, unreadable.Text);
                    continue;
                }

                _ = _unreadableHold.TryRemove(g.Name, out _);
                if (!AnyGameRunning(g) && !RetryUnreadable(g))
                {
                    continue;
                }
            }

            if (!_env.FileExists(g.Plan) && (AnyGameRunning(g) || !PlanIfMissing(g, s)))
            {
                continue; // 게임이 도는 중이면 디스크를 건드리지 않고, 계획이 실패했으면 failed 로 기록됨
            }

            WarmNewTrace(g, warms); // 재배치를 못 하거나 기다리는 동안에도 트레이스가 있으면 바로 체감할 수 있게
            if (!_env.Capabilities(g.OriginRoot).CanRelocate)
            {
                Note(g, AutoStage.Skipped, DriveCapabilities.PrewarmOnly); // 시스템 HDD: 읽기 전용 예열만, 재배치 없음
                continue;
            }

            if (!relocateOn || !GamePolicy.AllowsRelocate(g)) // 재배치를 막은 게임은 자동 재배치를 끈 설정과 같은 길 (기존 보류·계획·새 트레이스 예열 그대로)
            {
                Note(g, AutoStage.Skipped, relocateOn ? $"game policy: {GamePolicy.ModeName(g.Mode)}" : RelocateOff);
                continue;
            }

            long budget = BudgetBytes(s, g); // 0 = 무제한
            bool budgetHeld = budget > 0 && used >= budget; // 1 바이트라도 남아야 함: 남은 만큼만 쓰도록 적용의 쓰기 상한을 줄이므로 첫 이동 예외는 없음
            lock (_noteLock)
            {
                Note(g, AutoStage.WaitingIdle, WaitDetail(g, held is { } until ? AutoStatus.HoldDetail(until) : limitReached ? DailyLimit : budgetHeld ? DailyBudget : null));
            }

            if (!budgetHeld)
            {
                ready.Add(g);
            }
        }

        if (ready.Count == 0 || limitReached)
        {
            _idle.Reset();
            ResetWait();
            return;
        }

        bool inHours = GamePolicy.AllowsRelocateAt(s.Auto, _env.Now().ToLocalTime().Hour); // 시간대 밖이면 새 재배치를 시작하지 않음 (이미 시작한 회차는 끝까지)
        if (inHours == _hoursHeld)
        {
            _hoursHeld = !inHours;
            _log.Info(inHours ? "auto relocation resumes: inside auto.relocate_hours" : "auto relocation waits: outside auto.relocate_hours"); // 상태가 바뀔 때만
        }

        if (!inHours)
        {
            ready = [.. ready.Where(g => _env.JournalRows(g) > 0)]; // 저널이 남은 미완료 재배치는 이미 시작한 회차라 시간대 밖에서도 이어 함 (새 회차의 시작만 제한)
            if (ready.Count == 0)
            {
                _idle.Reset();
                ResetWait();
                return;
            }
        }

        string? busy = BusyReason(s, ready);
        NoteWait(busy);
        if (!_idle.Sample(_env.Now(), busy is null, s.Auto.IdleWindow))
        {
            return;
        }

        GameSettings next = ready[0];
        if (IsWarming(next.Name))
        {
            return; // 그 게임의 예열이 읽는 중이면 이동은 다음 틱에 (유휴 조건은 그대로 이어짐)
        }

        gate.Run(ct, token =>
        {
            Relocate(next, s, token);
            return true;
        }, () => AnyGameRunning(next));
        _idle.Reset(); // 다음 재배치도 다시 유휴를 기다림
        Warm(next, "after relocation", warms); // 끝났든 보류·실패했든 트레이스가 있으면 그 범위로 바로 예열 (새 배치가 RAM 에 오르게)
    }

    // 부트스트랩 S2: 원시 트레이스도 boot 트레이스도 (S1: 공유 프로필이 만든 것 포함) 없는 등록 게임은 Windows 프리페치 기록 (.pf) 으로 boot 트레이스를 만듭니다.
    // 못 만들면 (프리페처 꺼짐, 맞는 기록 없음) 바로 아래 WarmCold (S3) 가 폴더 통째·엔진 프로필로 예열합니다. 재배치 대상 (HDD) 에서 게임이 돌지 않을 때만, 게임마다 발견 주기에 한 번 시도합니다.
    private void PrefetchBootstrap(AsterSettings s)
    {
        IReadOnlyDictionary<string, DateTimeOffset> played = _status.LastPlayed();
        foreach (GameSettings g in s.Games.OrderByDescending(g => played.GetValueOrDefault(g.Name, DateTimeOffset.MinValue)).ThenBy(g => g.Name, StringComparer.Ordinal))
        {
            if (!HeavyWorkAllowed(g) || !GamePolicy.AllowsTracking(g) || AutoTracer.PlanTracePath(g, _env.FileExists) is not null || _prefetchTried.TryGetValue(g.Name, out DateTimeOffset at) && _env.Now() - at < RetryEvery
                || !_env.Capabilities(g.OriginRoot).CanPrewarm || AnyGameRunning(g))
            {
                continue;
            }

            _prefetchTried[g.Name] = _env.Now();
            try
            {
                if (_env.PrefetchBoot(g) is > 0 and int n)
                {
                    _log.Info($"{g.Name}: boot trace made from the prefetch record ({n} ranges)");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: prefetch record could not be used ({e.GetType().Name})");
            }
        }
    }

    // 기록 (원시 트레이스·prefetch 기록) 이 없는 등록 게임은 첫 실행 없이 폴더 통째·엔진 프로필로 예열합니다 (게임당 서비스 실행 중 한 번: 일시적 이유로 건너뛰면 다음 틱에 다시).
    // 자동 모드의 재배치 대상 (HDD) 에서만 합니다. 서비스 시작·런처 트리거 때의 예열은 Prewarmer 가 직접 합니다.
    private void WarmCold(AsterSettings s, List<(GameSettings Game, string Why)> warms)
    {
        if (_prewarm is null || !_prewarm.BootSettled()) // 부팅 직후에는 디스크 경쟁을 피해 미룸
        {
            return;
        }

        (bool, int, bool) key = (s.Auto.FullPrewarm, s.Auto.FullPrewarmMaxMib, s.Auto.EngineProfiles);
        if (key != _coldKey)
        {
            _coldKey = key;
            _coldTried.Clear();
        }

        IReadOnlyDictionary<string, DateTimeOffset> played = _status.LastPlayed();
        foreach (GameSettings g in AutoMode.Recent(s.Games, played, s.Auto.PrewarmRecent)) // 플레이한 적 있는 게임만 (prewarm_recent 안, 미플레이는 첫 실행 때 트리거 예열만)
        {
            if (HeavyWorkAllowed(g) && GamePolicy.AllowsPrewarm(g) && (!_coldTried.TryGetValue(g.Name, out DateTimeOffset triedAt) || _env.Now() - triedAt >= RetryEvery) && !PostUpdateDelayed(g, s) && _prewarm.IsCold(g) && _prewarm.LastWarmed(g.Name) is null && _env.Capabilities(g.OriginRoot).CanPrewarm && !AnyGameRunning(g))
            {
                _coldTried[g.Name] = _env.Now();
                warms.Add((g, ColdWhy));
            }
        }
    }

    private bool NeedsWork(GameSettings g, bool relocateOn)
    {
        AutoGameEntry? e = _status.Get(g.Name);
        // 실패한 게임은 서비스 실행당 한 번 다시 시도하고 (저장된 실패 시각 e.At 은 트레이스가 먼저 쓰여 있으면 영영 못 넘으므로 쓰지 않음), 그 뒤로는 트레이스가 새로 쓰였을 때만 다시 시도합니다.
        // 이번 실행의 실패 시각은 Fail 이 남김 (같은 트레이스로는 되풀이하지 않음).
        DateTimeOffset failedAt = _planFailedAt.GetValueOrDefault(g.Name, DateTimeOffset.MinValue);
        bool reopened = e is { Stage: AutoStage.Failed, RelocatedAt: null }
            && (AutoTracer.PlanTracePath(g, _env.FileExists) is { } latest && _env.FileWriteTime(latest) is { } written && written > failedAt
                || IsRetriedHourly(e.Detail) && _env.Now() - failedAt >= StoppedRecheck); // 무결성 실패 (data check failed) 가 아닌 모든 실패는 새 트레이스가 없어도 (트레이스 금지 게임) StoppedRecheck 마다 다시 평가
        bool stageOpen = reopened || e is null || e.Stage is not (AutoStage.Done or AutoStage.Failed)
            && !(e.Stage == AutoStage.Skipped && !relocateOn)
            && e.RelocatedAt is null;
        if (!stageOpen || AutoTracer.PlanTracePath(g, _env.FileExists) is null)
        {
            if (stageOpen && ReplanKept(_status, g.Name)) // 트레이스를 유지한 채 재계획을 기다리다 트레이스가 사라진 경우 (삭제·격리): 보류가 영영 풀리지 않게 다음 플레이에서 다시 트레이스하는 상태로
            {
                _status.ResetForReplan(g.Name, ReplanDetail);
            }

            return false;
        }

        if (_env.Capabilities(g.OriginRoot) is { CanPrewarm: false } caps) // 시스템 SSD·SSD·종류를 모르는 드라이브는 건드리지 않음 (수동 등록 게임도 같음). 시스템 HDD 는 계획·예열까지만 (TickCore 가 재배치 앞에서 거름)
        {
            Note(g, AutoStage.Skipped, caps.Reason!);
            return false;
        }

        return true;
    }

    // 유휴가 아닌 이유 (유휴면 null): 재배치 대상 드라이브의 등록 게임이 돌거나, 배터리이거나, 대상 드라이브에 디스크 활동이 있음.
    // CPU 사용률은 보지 않습니다: HDD 이동은 Low I/O 우선순위로 대상 드라이브에만 영향을 주므로 백그라운드 작업이 있는 PC 도 재배치해야 합니다.
    private string? BusyReason(AsterSettings s, List<GameSettings> ready)
    {
        if (ready.Any(AnyGameRunning))
        {
            return "game running";
        }

        if (!_env.PowerOk())
        {
            return "on battery";
        }

        bool prewarming = _prewarm is { } warm && ready.Any(g => warm.StreamingOnDisk(g.Name)); // 같은 디스크의 예열 스트림과 재배치 쓰기가 겹치지 않게
        if (prewarming != _prewarmHeld)
        {
            _prewarmHeld = prewarming;
            if (prewarming)
            {
                _log.Info($"auto relocation waits: {StartupPrewarmBusy}"); // 한 번만: 읽는 동안 틱마다 남기지 않음
            }
        }

        if (prewarming)
        {
            return StartupPrewarmBusy;
        }

        bool diskBusy = ready.Select(g => PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant()).Distinct()
            .Any(letter => _env.DiskBytesPerSecond(letter) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond);
        return diskBusy ? "disk busy" : null;
    }

    // 유휴가 아닌 채로 6 시간을 넘게 기다리면 그때 거짓이던 이유를 한 번 남깁니다. 유휴가 되면 다시 셉니다.
    private void NoteWait(string? busy)
    {
        if (busy is null)
        {
            ResetWait();
            return;
        }

        DateTimeOffset now = _env.Now();
        _waitSince ??= now;
        if (!_waitNoted && now - _waitSince.Value >= LongWait)
        {
            _waitNoted = true;
            _log.Info($"auto waiting for idle for {(int)(now - _waitSince.Value).TotalHours} h ({busy})");
        }
    }

    private void ResetWait()
    {
        _waitSince = null;
        _waitNoted = false;
    }
}
