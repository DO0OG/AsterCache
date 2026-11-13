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

        IReadOnlyList<PrewarmResult> results = RunBatch(chosen, $"trigger {start.ExeName}", pin: !LauncherExes.Is(start.ExeName), ct: ct, trigger: true); // 런처가 뜰 때는 고정하지 않음 (런처 업데이트가 파일을 고칠 수 있게)
        WaitForBlockingGames(results, start.ExeName);
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
        if (!Monitor.TryEnter(_run))
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
