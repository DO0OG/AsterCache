using AsterCache.Core.Logging;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// Prewarmer 의 트리거 프로그램 시작 처리: 중복 트리거 걸러내기와 막던 게임이 사라진 뒤의 재시도.
public sealed partial class Prewarmer
{
    /// 이 프로세스가 어느 게임의 trigger_exes 에 들어 있는가 (대소문자 무시).
    public bool IsTrigger(string exeName) => _games.Any(g => IsTrigger(g, exeName));

    public static bool IsTrigger(GameSettings g, string exeName) => g.Prewarm.TriggerExes.Contains(exeName, StringComparer.OrdinalIgnoreCase);

    /// 로그용 예열 원인: "trigger X.exe" → "X.exe", "auto cold start" → "cold start", 그 밖은 앞의 "auto " 만 뗌.
    public static string TriggerCause(string why) => why.StartsWith("trigger ", StringComparison.Ordinal) ? why["trigger ".Length..] : why.StartsWith("auto ", StringComparison.Ordinal) ? why["auto ".Length..] : why;

    /// 트리거 프로그램이 떴을 때: 그 프로그램을 trigger_exes 에 둔 게임을 차례로. narrow 가 있으면 그 목록에서 예열할 게임만 고릅니다.
    public IReadOnlyList<PrewarmResult> OnProcessStart(ProcessStart start, Func<IReadOnlyList<GameSettings>, IEnumerable<GameSettings>>? narrow = null, CancellationToken ct = default)
    {
        if (DuplicateTrigger(start.ExeName))
        {
            return [];
        }

        IReadOnlyList<GameSettings> hit = _games.Where(g => IsTrigger(g, start.ExeName) && !PolicyBlocked(g, $"trigger prewarm ({start.ExeName})")).ToList();
        IReadOnlyList<GameSettings> chosen = [.. narrow?.Invoke(hit) ?? hit];
        if (chosen.Count > 0)
        {
            GameSettings first = WarmRank?.Invoke(chosen).FirstOrDefault() ?? chosen[0]; // 같은 트리거를 여러 게임이 쓰면 기존 순위로 한 게임을 고름
            lock (_gate)
            {
                _triggered = (first.Name, _now());
            }
        }

        IReadOnlyList<PrewarmResult> results;
        IDisposable? waiting = null; // 읽을 게임이 있으면 RunBatch 가 끝날 때까지 서 있는 트리거 대기 표시 (배경 예열이 끼어들지 않게, 정상·취소·예외 모두 지움)
        try
        {
            IReadOnlyList<string?> skips = chosen.Count > 0 ? TriggerSkipReasons(chosen) : [];
            IReadOnlyList<GameSettings> warming = [.. chosen.Where((_, i) => skips[i] is null)]; // 실제로 읽을 게임만: 표시·선점의 "대상" (건너뛸 게임의 배경 스트림을 보호하지 않고, 건너뛸 게임만 있으면 아무것도 멈추지 않음)
            if (warming.Count > 0)
            {
                waiting = BeginTriggerWait(warming);
                PreemptForTrigger(warming); // 같은 디스크의 다른 게임의 배경 읽기는 이 트리거가 먼저: 멈추고 그 게임은 이어 읽기·재시도에 맡김
                _env.YieldBackground("trigger", ct); // 예열이 가용 메모리를 처음 읽기 (RunBatch) 전에 배경 프로세스의 메모리를 양보시킴 (모두 건너뛸 것이면 비우지 않음)
            }

            string why = $"trigger {start.ExeName}";
            results = chosen.Count > 0 && warming.Count == 0
                ? SkipTriggerNow(chosen, skips, why) // 읽을 게임이 없으면 다른 예열이 락을 놓기를 기다리지 않고 바로 건너뜀을 남김
                : RunBatch(chosen, why, pin: !LauncherExes.Is(start.ExeName), ct: ct, trigger: true); // 런처가 뜰 때는 고정하지 않음 (런처 업데이트가 파일을 고칠 수 있게)
        }
        finally
        {
            waiting?.Dispose();
        }

        WaitForBlockingGames(results, start.ExeName);
        return results;
    }

    // 게임마다 트리거 예열이 건너뛸 사유, 실제로 읽을 게임이면 null (RunBatch 와 같은 건너뜀 판정, 게임마다 한 번씩만 판정해 표본 읽기를 겹치지 않음). 기록 없는 게임은 RunCold 의 확정 건너뜀 (드라이브 거부·게임 실행 중·쿨다운) 만 봄: 디스크 유휴는 양보 뒤에 바뀔 수 있어 진행으로 셈.
    // 가용 메모리는 읽지 않음: 양보가 끝난 뒤 처음 읽어야 하고, 메모리 부족은 부분 예열이 읽으니 어차피 진행으로 셈.
    private IReadOnlyList<string?> TriggerSkipReasons(IReadOnlyList<GameSettings> games)
    {
        DateTimeOffset now = _now();
        return [.. games.Select(g => IsCold(g) ? _env.ColdRefusal(g) ?? ColdFixedSkip(g, now) : WillSkipReason(g, long.MaxValue))];
    }

    // 모두 건너뛸 트리거: Run 이 락 안에서 남기는 것과 같은 건너뜀 줄·기록을 락 없이 남김 (읽지 않으므로 락이 필요 없고, 다른 예열이 몇 분 읽는 동안 스레드 풀 스레드를 붙잡지 않음).
    // 같은 디스크에서 게임이 실행 중인 건너뜀은 호출자의 WaitForBlockingGames 가 기존 재시도 경로에 올림.
    private IReadOnlyList<PrewarmResult> SkipTriggerNow(IReadOnlyList<GameSettings> games, IReadOnlyList<string?> skips, string why)
    {
        List<PrewarmResult> results = [];
        for (int i = 0; i < games.Count; i++)
        {
            (GameSettings game, string skip, DateTimeOffset now) = (games[i], skips[i]!, _now());
            if (IsCold(game))
            {
                if (_env.ColdRefusal(game) is not null)
                {
                    bool first;
                    lock (_gate)
                    {
                        first = _refusedLogged.Add(game.Name);
                    }

                    if (first)
                    {
                        _log.Info($"{game.Name}: prewarm without a record skipped ({skip}) [{why}]");
                    }

                    results.Add(new PrewarmResult(game.Name, false, skip, null, now)); // 상태 파일에도 쓰지 않음 (RunCold 와 같음)
                    continue;
                }

                _log.Info($"{game.Name}: prewarm skipped ({skip}) [{why}]{(skip == "game is running" ? BlockerNote(game) : string.Empty)}");
                results.Add(Record(game, now, new PrewarmResult(game.Name, false, skip, null, now), 0));
                continue;
            }

            PrewarmResult result = new(game.Name, false, skip, null, now);
            if (skip == PostUpdateDelay)
            {
                TrackPendingWarm(game, result, now); // 업데이트 지연은 기록 없이 대기만 (Run 과 같음)
                results.Add(result);
                continue;
            }

            LogSkipped(game.Name, skip, () => $"{game.Name}: prewarm skipped ({skip}) [{why}]{(skip == "game is running" ? BlockerNote(game) : string.Empty)}");
            results.Add(Record(game, now, result, 0));
        }

        return results;
    }

    // 같은 디스크의 다른 게임이 실행 중이라 거절된 트리거 예열은, 그 게임들이 사라진 것을 처음 본 때 한 번 다시 시도하도록 적어 둡니다 (NoteGamesGone).
    private void WaitForBlockingGames(IReadOnlyList<PrewarmResult> results, string triggerExe)
    {
        foreach (PrewarmResult r in results.Where(r => !r.Ran && (r.SkipReason == "game is running" || r.CancelDetail == "disk read lease refused")))
        {
            if (_games.FirstOrDefault(g => string.Equals(g.Name, r.Game, StringComparison.OrdinalIgnoreCase)) is { } game && !_env.GameRunning(game))
            {
                _triggerRetries.Wait(game.Name, triggerExe, GamesActiveOnDisk(game).Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase), _now());
            }
        }
    }

    /// 종료 유예에 들어간 (프로세스가 사라진 것을 처음 본) 게임 이름들 (Worker 가 틱마다 부름): 그 게임 때문에 거절됐고 트리거 프로세스가 아직 살아 있는 예열의 재시도를 지금으로 당깁니다.
    public void NoteGamesGone(IReadOnlySet<string> gone)
    {
        foreach ((string game, string exe) in _triggerRetries.NoteGone(gone, BlockerActive, _env.ProcessRunning, _now()))
        {
            lock (_gate)
            {
                if (_pendingWarm.TryGetValue(game, out PendingWarm? pending))
                {
                    _pendingWarm[game] = pending with { RetryAt = _now(), TriggerExe = exe };
                }
            }
        }
    }

    // 막던 게임이 아직 실행 중이거나 양보 등록돼 있는가 (설정에서 빠진 게임은 아님).
    private bool BlockerActive(string name) =>
        _games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is { } game && (_env.GameRunning(game) || _coordinator?.IsRegistered(name) == true);

    // 당겨진 트리거 재시도를 트리거 경로와 같은 조건 (유휴·종료 유예 게이트 없음) 으로 한 번 돌립니다. 다른 예열이 돌고 있으면 표시를 남겨 다음 틱으로 미룸.
    private bool TryRunTriggerRetry(GameSettings game, CancellationToken ct)
    {
        if (!TryEnterRunIdle())
        {
            return false;
        }

        try
        {
            if (TakeTriggerRetry(game.Name) is not { } exe)
            {
                return false;
            }

            _log.Info($"{game.Name}: retrying the refused trigger prewarm ({exe}) now that the blocking game is gone");
            _ = RunBatch([game], $"trigger {exe}", pin: !LauncherExes.Is(exe), ct: ct, trigger: true);
            return true;
        }
        finally
        {
            Monitor.Exit(_run);
        }
    }

    // 당겨진 트리거 재시도를 한 번만 꺼냅니다 (표시를 지우므로 다시 거절돼도 되풀이하지 않음).
    private string? TakeTriggerRetry(string game)
    {
        lock (_gate)
        {
            if (!_pendingWarm.TryGetValue(game, out PendingWarm? pending) || pending.TriggerExe is not { } exe)
            {
                return null;
            }

            _pendingWarm[game] = pending with { TriggerExe = null };
            return exe;
        }
    }

    // 같은 exe 의 트리거는 SkipLogEvery 안에 한 번만 처리하고 나머지는 세어 두었다가, 다음에 처리하는 때 한 줄로 남깁니다.
    private bool DuplicateTrigger(string exe)
    {
        int suppressed;
        lock (_gate)
        {
            DateTimeOffset now = _now();
            if (_triggerSeen.TryGetValue(exe, out (DateTimeOffset At, int Count) last) && now - last.At < SkipLogEvery)
            {
                _triggerSeen[exe] = last with { Count = last.Count + 1 };
                return true;
            }

            suppressed = last.Count;
            _triggerSeen[exe] = (now, 0);
        }

        if (suppressed > 0)
        {
            _log.Info($"{exe}: trigger handled again ({suppressed} duplicate triggers suppressed)");
        }

        return false;
    }

    /// 주어진 게임을 차례로 (한 게임의 실패가 나머지를 막지 않음).
    public IReadOnlyList<PrewarmResult> RunAll(IEnumerable<GameSettings> games, string why) => RunBatch(games, why);
}
