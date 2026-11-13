using AsterCache.Core.Logging;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// Prewarmer 의 게임 설정 교체, 정책 판정, 틱, 진행 중 작업 추적.
public sealed partial class Prewarmer
{
    private IDisposable TrackGameWork(string game)
    {
        lock (_gate)
        {
            _activeWork[game] = _activeWork.GetValueOrDefault(game) + 1;
        }

        return new GameWorkLease(this, game);
    }

    private void EndGameWork(string game)
    {
        lock (_gate)
        {
            if (_activeWork.TryGetValue(game, out int count) && count > 1)
            {
                _activeWork[game] = count - 1;
            }
            else
            {
                _activeWork.Remove(game);
                PruneRemovedGameLocked(game);
            }
        }
    }

    private void PruneRemovedGameLocked(string game)
    {
        if (!_removedGames.Contains(game) || _activeWork.ContainsKey(game)
            || _relocStarted.Contains(game)
            || string.Equals(_streaming, game, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_coldReading, game, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _removedGames.Remove(game);
        _last.Remove(game);
        _pendingWarm.Remove(game);
        _idleSince.Remove(game);
        _profileMiss.Remove(game);
        _residencyProbes.Remove(game);
        _refusedLogged.Remove(game);
        _policyLogged.Remove(game);
        _delayLogged.Remove(game);
        _relocHoldLogged.Remove(game);
        _supplementNothingNew.Remove(game);
        _memoryStopAt.Remove(game);
        _pendingWarmBudgetSkipped.Remove(game);
        _readSample.Remove(game);
        _gameIdleSince.Remove(game);
        _coldMib.Remove(game);
        _coldSamples.Remove(game);
        _resume.Remove(game);
        _supplementReadProgress.Remove(game);
        _supplementRetry.Remove(game);
        foreach (string key in _skipLogged.Keys.Where(k => k.StartsWith(game + "|", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _skipLogged.Remove(key);
        }

        if (_triggered is { } trigger && string.Equals(trigger.Game, game, StringComparison.OrdinalIgnoreCase))
        {
            _triggered = null;
        }

        HashSet<string> liveTriggers = new(_games.SelectMany(g => g.Prewarm.TriggerExes), StringComparer.OrdinalIgnoreCase);
        foreach (string triggerExe in _triggerSeen.Keys.Where(k => !liveTriggers.Contains(k)).ToList())
        {
            _triggerSeen.Remove(triggerExe);
        }
    }

    /// 자동 모드 설정 (full_prewarm 등)을 바꿉니다. 시작 때와 설정을 다시 읽을 때 부름.
    public void SetAuto(AutoSettings auto) => _auto = auto;

    // 호출자가 쥔 옛 사본이 아니라 설정을 다시 읽은 최신 항목으로 정책을 봅니다 (목록에 없으면 받은 값).
    private GameSettings CurrentOf(GameSettings game) => _games.FirstOrDefault(x => string.Equals(x.Name, game.Name, StringComparison.OrdinalIgnoreCase)) ?? game;

    /// 게임별 정책이 예열 계열 (예열·재예열·보충·시작 스트림·고정) 을 막는가. 전역 스위치·안티치트 게이트와는 따로 AND 로 봅니다.
    internal bool PolicyBlocksPrewarm(GameSettings game) => !GamePolicy.AllowsPrewarm(CurrentOf(game));

    // 막혔으면 true. 정책이 바뀔 때까지 같은 게임의 사유는 한 번만 로그합니다.
    private bool PolicyBlocked(GameSettings game, string what)
    {
        GameSettings current = CurrentOf(game);
        if (GamePolicy.AllowsPrewarm(current))
        {
            return false;
        }

        bool first;
        lock (_gate)
        {
            first = _policyLogged.Add(game.Name);
        }

        if (first)
        {
            _log.Info($"{game.Name}: {what} skipped (game policy: {PolicyText(current)})");
        }

        return true;
    }

    private static string PolicyText(GameSettings g) => g.Paused ? "paused" : GamePolicy.ModeName(g.Mode);

    /// 이 게임이 기록 없는 예열 대상인가: 기능이 켜져 있고 (자동 모드, full_prewarm 이나 engine_profiles) 원시 트레이스도 prefetch 기록도 없음.
    public bool IsCold(GameSettings game)
    {
        AutoSettings a = _auto;
        return _env.ColdPlanner is not null && a.Enabled && (a.FullPrewarm || a.EngineProfiles) && !_env.TraceExists(game) && !_env.RawTraceExists(game);
    }

    /// 재배치 직후 예열의 회복 기간 고정이 쓰는 드라이브 회복 판정. null 이면 그 고정은 꺼진 것과 같습니다.
    public IDriveRecovery? Recovery
    {
        get => _pins.Recovery;
        set => _pins.Recovery = value;
    }

    /// 지금 고정을 들고 있는 게임 이름.
    public IReadOnlyCollection<string> Pinned => _pins.Held;

    internal IReadOnlyList<GameSettings> Games => _games;

    /// 설정을 다시 읽었을 때 게임 목록만 바꿉니다 (쿨다운·상태는 유지). pin 이 꺼졌거나 빠진 게임의 고정은 풉니다.
    public void SetGames(IReadOnlyList<GameSettings> games)
    {
        IReadOnlyList<GameSettings> before = _games;
        _games = games;
        HashSet<string> live = new(games.Select(g => g.Name), StringComparer.OrdinalIgnoreCase);
        lock (_gate)
        {
            foreach (GameSettings gone in before.Where(g => !live.Contains(g.Name)))
            {
                _removedGames.Add(gone.Name);
            }

            _removedGames.RemoveWhere(live.Contains);
            foreach (string stale in _resume.Keys.Concat(_supplementRetry.Keys).Concat(_supplementReadProgress.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
            {
                GameSettings? now = games.FirstOrDefault(g => string.Equals(g.Name, stale, StringComparison.OrdinalIgnoreCase));
                GameSettings? was = before.FirstOrDefault(g => string.Equals(g.Name, stale, StringComparison.OrdinalIgnoreCase));
                bool changed = now is not null && was != now;
                bool removed = now is null;
                if (changed || removed && !_activeWork.ContainsKey(stale) && !_relocStarted.Contains(stale))
                {
                    _resume.Remove(stale); // 빠진 게임, 또는 설정 (읽을 목록) 이 바뀐 게임의 이어 읽기 위치는 낡음
                    _supplementRetry.Remove(stale);
                    _supplementReadProgress.Remove(stale);
                }
            }

            foreach (string gone in _pendingWarm.Keys.Where(k => !live.Contains(k) && !_activeWork.ContainsKey(k)).ToList())
            {
                _pendingWarm.Remove(gone);
                _idleSince.Remove(gone);
                _pendingWarmBudgetSkipped.Remove(gone);
            }

            foreach (GameSettings g in games)
            {
                if (GamePolicy.AllowsPrewarm(g))
                {
                    _policyLogged.Remove(g.Name); // 다시 허용되면 부분 상태·마지막 예열 이력은 그대로라 다음 점검부터 평소처럼 이어짐
                    continue;
                }

                // 예열이 금지된 게임의 대기 상태 (대기 예열, 보충 재시도, 이어 읽기 위치) 는 낡은 채 엉뚱한 읽기를 하지 않게 정리. 이력 (_last·_partial·상태 파일) 은 보존.
                _pendingWarm.Remove(g.Name);
                _idleSince.Remove(g.Name);
                _pendingWarmBudgetSkipped.Remove(g.Name);
                _resume.Remove(g.Name);
                _supplementRetry.Remove(g.Name);
                _supplementReadProgress.Remove(g.Name);
                _supplementNothingNew.Remove(g.Name);
                if (_policyLogged.Add(g.Name))
                {
                    _log.Info($"{g.Name}: prewarm, supplement and pin stopped (game policy: {PolicyText(g)})");
                }
            }

            foreach (string gone in _removedGames.ToList())
            {
                PruneRemovedGameLocked(gone);
            }
        }

        _rewarm.SetGames(games);
        _pins.Reconcile(games);
        bool budgetWaiting;
        lock (_gate)
        {
            budgetWaiting = _pendingWarmBudgetSkipped.Count > 0;
        }

        if (budgetWaiting)
        {
            WarmBudget(); // 목록·예산이 바뀌었으면 예산으로 미뤄 둔 대기 게임의 배정 회복을 바로 판정
        }
    }

    /// 한 게임의 고정을 풉니다 (그 게임 exe 가 뜰 때, 스트림보다 먼저).
    public void ReleasePin(string game, string why) => _pins.Release(game, why);

    /// 모든 고정을 풉니다 (런처가 뜰 때). 회복 기간 고정은 남깁니다 (런처 업데이트는 파일 변경 해제로 막음).
    public void ReleasePins(string why) => _pins.ReleaseAll(why, keepRecovery: true);

    public void Shutdown() => _pins.ReleaseAll("service stopping");

    /// 주기 점검 (재적용 틱마다): 가용 RAM 이 모자라면 고정 해제, 예열 유지 주기가 되면 식은 게임을 다시 예열. ct 가 취소되면 (게임 시작) 시작하지 않은 예열은 하지 않습니다.
    public void Tick(AutoSettings auto, CancellationToken ct = default)
    {
        _pins.CheckMemory(_games);
        _pins.CheckRecovery();
        SampleDriveGames();
        SupplementTick(ct);
        _rewarm.Tick(auto, ct);
    }
}
