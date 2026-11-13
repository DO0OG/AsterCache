using AsterCache.Core.Logging;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// AutoRelocator 의 예열 요청 큐와 진행 표시: 예열 대기열, 단계 표시 갱신, 예열 완료·스트림 종료 알림.
public sealed partial class AutoRelocator
{
    /// 게임이 떴을 때 (감시기 스레드에서 바로): 돌고 있거나 기다리는 예열을 취소합니다. 핫 집합 스트림은 묶음 경계에서 멈춥니다 (그 게임을 읽던 스트림은 Prewarmer.NoteGameStart 가 끝까지 읽게 함).
    public void CancelWarm()
    {
        lock (_warmLock)
        {
            _warmQueue.ToList().ForEach(q => _ = _traceWarmTried.TryRemove(q.Game.Name, out _) | _coldTried.TryRemove(q.Game.Name, out _)); // 줄 서 있던 트레이스 예열은 게임이 끝난 뒤 다시 요청되게
            _warmQueue.Clear();
            _warmCts.Cancel();
            _warmCts = new CancellationTokenSource();
        }
    }

    /// 지금 이후 시작하는 예열 작업의 취소 신호 (CancelWarm 이 취소하고 새로 바꿈).
    public CancellationToken WarmToken
    {
        get
        {
            lock (_warmLock)
            {
                return _warmCts.Token;
            }
        }
    }

    /// 요청된 예열이 모두 끝나면 완료되는 작업 (시험용).
    public Task WaitWarmAsync()
    {
        lock (_warmLock)
        {
            return _warmTask;
        }
    }

    /// 예열 실측 MB/s (Prewarmer.WarmMeasured): 진단 로그용일 뿐 회복 판정에는 쓰이지 않음.
    public void NoteWarmMbps(GameSettings g, double mbps) => _recovery.NoteWarmMbps(DriveOf(g), mbps);

    /// 기록 없이 예열에 성공했을 때 (Prewarmer.ColdWarmed): auto_status 의 detail 을 "prewarmed (full)" 같은 표시로. 실패·건너뜀·재배치 중인 게임은 그대로 둡니다.
    public void NoteColdWarmed(GameSettings g, string label)
    {
        lock (_noteLock)
        {
            AutoGameEntry? e = _status.Get(g.Name);
            if (e is null ? g.Source != "auto" : e.Stage is AutoStage.Failed or AutoStage.Skipped or AutoStage.Relocating or AutoStage.Done)
            {
                return;
            }

            Note(g, e?.Stage ?? AutoStage.Discovered, WithPrewarmed(e?.Detail, PrewarmedBy(label)));
        }
    }

    /// 핫 집합 예열의 진행 (읽은 MiB, 총 MiB): 단계는 그대로 두고 detail 에 "prewarming x/y MiB" 를 붙입니다 (로그에는 남기지 않음).
    public void NoteProgress(GameSettings g, long doneMib, long totalMib)
    {
        lock (_noteLock)
        {
            _progress[g.Name] = $"prewarming {doneMib}/{totalMib} MiB";
            Retag(g);
        }
    }

    /// 핫 집합 스트림이 끝남: 진행 표시를 지우고, 끝까지 읽었으면 "prewarmed" 를 바로 붙입니다 (재배치 대기·완료 단계만, WarmNow 와 같음).
    public void NoteStreamEnded(GameSettings g, bool complete)
    {
        lock (_noteLock)
        {
            _progress.TryRemove(g.Name, out _);
            AutoGameEntry? e = _status.Get(g.Name);
            if (complete && e?.Stage is AutoStage.Done or AutoStage.WaitingIdle)
            {
                Note(g, e.Stage, WithPrewarmed(DoneDetail(e), PrewarmedTagOf(g)));
            }
            else
            {
                Retag(g);
            }
        }
    }

    // 진행 표시가 보일 수 있는 단계: 학습 중 (선행 읽기 양 표시)·트레이스·재배치·실패·건너뜀의 detail 은 건드리지 않음.
    private static bool ShowsProgress(string stage) => stage is AutoStage.Discovered or AutoStage.WaitingIdle or AutoStage.Done;

    private void Retag(GameSettings g)
    {
        if (_status.Get(g.Name) is { } e && ShowsProgress(e.Stage))
        {
            _status.Set(g.Name, g.Source, null, e.Stage, Tagged(g, e.Stage, e.Detail));
        }
    }

    // detail 에서 옛 진행 표시를 지우고, 지금 진행 중이면 새 표시를 붙입니다.
    private string? Tagged(GameSettings g, string stage, string? detail)
    {
        string game = g.Name;
        string? rest = detail is null ? null : TraceTag.Replace(TierTag.Replace(ProgressTag.Replace(detail, string.Empty), string.Empty), string.Empty).Trim(' ', ',');
        rest = string.IsNullOrEmpty(rest) ? null : rest;
        if (ShowsProgress(stage) && AutoTracer.NextTrace(g, _settings().Auto, _env, _status) is { Valid: > 0, Wanted: true } next) // 플레이를 더 모으는 중: "trace 2/3", 추가 수집이면 "trace 4/8 (coverage 22%)"
        {
            string traceTag = _env.TraceBlocker(g) is { } cheat
                ? $"trace unavailable ({cheat}), {next.Valid} earlier run(s) used" // 안티치트로 더 모을 수 없는 게임: 회차를 기다리는 것처럼 보이지 않게 (재배치 여부는 바꾸지 않음)
                : $"trace {next.Valid}/{next.Limit}" + (next.Valid >= _settings().Auto.TraceRuns && next.Coverage is { } cov ? $" (coverage {cov}%)" : string.Empty);
            rest = rest is null ? traceTag : rest + ", " + traceTag;
        }

        if (ShowsProgress(stage) && _tier?.Tag(game) is { } tier)
        {
            rest = rest is null ? tier : rest + ", " + tier;
        }

        return ShowsProgress(stage) && _progress.TryGetValue(game, out string? tag) ? (rest is null ? tag : rest + ", " + tag) : rest;
    }

    // 원시 트레이스가 아직 예열된 적 없거나 마지막 예열보다 새로우면 한 번 예열합니다 (같은 트레이스로는 한 번만 시도: 메모리가 모자라 건너뛰어도 30 초마다 다시 부르지 않음).
    private void WarmNewTrace(GameSettings g, List<(GameSettings Game, string Why)> warms)
    {
        if (_prewarm is null || !HeavyWorkAllowed(g) || !GamePolicy.AllowsPrewarm(g) || AutoTracer.PlanTracePath(g, _env.FileExists) is not { } tracePath || _env.FileWriteTime(tracePath) is not { } traced
            || _traceWarmTried.TryGetValue(g.Name, out DateTimeOffset tried) && tried >= traced
            || _prewarm.LastWarmed(g.Name) is { } last && last >= traced
            || ReplanKept(_status, g.Name) // 업데이트 재계획이 끝나기 전의 트레이스는 새 계획이 아님: 시도로 치지 않고 끝난 뒤 새 계획 파일로 예열
            || AnyGameRunning(g))
        {
            return;
        }

        _traceWarmTried[g.Name] = traced;
        Warm(g, "new trace", warms);
    }

    // g 와 같은 드라이브에 설치된 등록 게임 (g 포함) 이 도는 중인지: 다른 드라이브의 게임은 g 의 디스크 읽기와 겹치지 않으므로 보지 않습니다.
    private bool AnyGameRunning(GameSettings g)
    {
        List<GameSettings> games = [.. SameDriveGames(_settings().Games, [g], _env.SameDisk)];
        if (!games.Any(x => string.Equals(x.Name, g.Name, StringComparison.OrdinalIgnoreCase)))
        {
            games.Add(g);
        }

        string[] names = [.. games.SelectMany(x => x.ExeNames).Distinct(StringComparer.OrdinalIgnoreCase)];
        if (_env.AnyRunning(names))
        {
            _blockers.Note(g.Name, $"{g.Name}: auto skipped", games);
            return true;
        }

        _blockers.Clear(g.Name);
        return false;
    }

    // targets 와 같은 물리 디스크 (sameDisk 판정) 에 설치된 games.
    public static IEnumerable<GameSettings> SameDriveGames(IEnumerable<GameSettings> games, IEnumerable<GameSettings> targets, Func<string, string, bool> sameDisk)
    {
        List<string> roots = [.. targets.Select(t => t.OriginRoot)];
        return games.Where(x => roots.Any(r => sameDisk(r, x.OriginRoot)));
    }

    // 게임이 도는 중이면 디스크를 건드리지 않습니다. 쿨다운은 무시합니다 (트레이스나 배치가 새로 바뀐 뒤의 예열이므로).
    private void Warm(GameSettings g, string why, List<(GameSettings Game, string Why)> warms)
    {
        if (_prewarm is not null && HeavyWorkAllowed(g) && GamePolicy.AllowsPrewarm(g) && !AnyGameRunning(g))
        {
            warms.Add((g, why));
        }
    }

    private bool IsWarming(string game)
    {
        lock (_warmLock)
        {
            return _warming == game || _warmQueue.Any(q => q.Game.Name == game);
        }
    }

    // 같은 게임이 이미 줄에 있으면 다시 넣지 않음. 작업이 없으면 하나를 띄워 줄이 빌 때까지 차례로 돌립니다.
    private void QueueWarm(GameSettings g, string why)
    {
        lock (_warmLock)
        {
            if (_warmQueue.Any(q => q.Game.Name == g.Name))
            {
                return;
            }

            _warmQueue.Enqueue((g, why));
            if (!_warmRunning)
            {
                _warmRunning = true;
                _warmTask = Task.Run(DrainWarm, CancellationToken.None);
            }
        }
    }

    private void DrainWarm()
    {
        while (true)
        {
            GameSettings g;
            string why;
            CancellationToken token;
            lock (_warmLock)
            {
                if (!_warmQueue.TryDequeue(out (GameSettings Game, string Why) next))
                {
                    (_warmRunning, _warming) = (false, null);
                    return;
                }

                (g, why, _warming, token) = (next.Game, next.Why, next.Game.Name, _warmCts.Token);
            }

            try
            {
                WarmNow(g, why, token);
            }
            catch (Exception e)
            {
                _log.Error($"{g.Name}: auto prewarm failed", e);
            }
        }
    }

    // 줄에 넣은 뒤 설정이 다시 읽혔을 수 있어 최신 항목으로 봅니다.
    private bool CurrentAllowsPrewarm(GameSettings g) =>
        GamePolicy.AllowsPrewarm(_settings().Games.FirstOrDefault(x => string.Equals(x.Name, g.Name, StringComparison.OrdinalIgnoreCase)) ?? g);

    // 게임이 돌면 디스크를 건드리지 않습니다. 쿨다운은 무시합니다 (트레이스나 배치가 새로 바뀐 뒤의 예열이므로). 취소·중단된 트레이스 예열은 다음 틱에 다시 시도합니다.
    private void WarmNow(GameSettings g, string why, CancellationToken token)
    {
        if (token.IsCancellationRequested || !HeavyWorkAllowed(g) || !CurrentAllowsPrewarm(g) || AnyGameRunning(g)) // 줄에서 기다리는 동안 정책이 예열을 막았으면 읽지 않고 시도 표시를 풀어 다시 허용될 때 요청되게
        {
            _traceWarmTried.TryRemove(g.Name, out _);
            _coldTried.TryRemove(g.Name, out _);
            return;
        }

        PrewarmResult r = _prewarm!.Guarded(g, "auto " + why, pin: true, ignoreCooldown: true, token);
        if (why == ColdWhy)
        {
            // 일시적인 이유 (메모리 부족, 게임 실행 중, 취소) 로 못 했으면 다음 틱에 다시, 디스크 바쁨은 BusyRetry 뒤에. 폴더가 크고 맞는 엔진이 없는 것 같은 결과는 다시 열거하지 않음.
            if (!r.Ran && r.SkipReason == "drive busy")
            {
                _coldTried[g.Name] = _env.Now() - RetryEvery + BusyRetry;
            }
            else if (!r.Ran && r.SkipReason is { } why2 && (why2 == Prewarmer.Cancelled || why2 == "game is running" || why2.StartsWith("low memory", StringComparison.Ordinal)
                || why2.StartsWith("cooldown", StringComparison.Ordinal) || why2.StartsWith("failed", StringComparison.Ordinal)))
            {
                _coldTried.TryRemove(g.Name, out _);
            }
        }
        else if (r.SkipReason == Prewarmer.Cancelled)
        {
            _traceWarmTried.TryRemove(g.Name, out _);
        }
        else if (r.Ran)
        {
            lock (_noteLock)
            {
                AutoGameEntry? e = _status.Get(g.Name);
                if (e?.Stage is AutoStage.Done or AutoStage.WaitingIdle)
                {
                    Note(g, e.Stage, WithPrewarmed(DoneDetail(e), PrewarmedTagOf(g)));
                }
            }
        }
    }

    private string? WaitDetail(GameSettings g, string? d)
    {
        return _prewarm?.LastWarmed(g.Name) is null ? d : WithPrewarmed(d, PrewarmedTagOf(g));
    }

    // 메모리가 모자라 앞부분만 읽은 게임은 "prewarmed (partial N/M MiB)" 로 완료와 구분합니다.
    private string PrewarmedTagOf(GameSettings g) => _prewarm?.PartialInfo(g.Name) is { } p ? $"{Prewarmed} (partial {p.WarmedMib}/{p.HotMib} MiB)" : Prewarmed;

    // 이미 있는 "prewarmed" 표시 (기록 없는 예열의 "(full)" 포함) 는 새 표시로 바꿉니다.
    private static string WithPrewarmed(string? detail, string tag = Prewarmed)
    {
        string rest = detail is null ? string.Empty : PrewarmedTag.Replace(detail, string.Empty).Trim(' ', ',');
        return rest.Length == 0 ? tag : rest + ", " + tag;
    }

    // 상태가 바뀌었을 때만 로그에 남깁니다 (30 초마다 같은 줄이 쌓이지 않게).
    private void Note(GameSettings g, string stage, string? detail)
    {
        detail = Tagged(g, stage, detail);
        if (stage is AutoStage.WaitingIdle or AutoStage.Relocating && !_env.FileExists(g.Plan) && _status.Get(g.Name)?.Detail is { } held && ReplanKept(_status, g.Name) && detail?.StartsWith(ReplanDetail, StringComparison.Ordinal) != true)
        {
            detail = (held.StartsWith(KeptDetail, StringComparison.Ordinal) ? KeptDetail : UnchangedKeptDetail) + (detail is null ? string.Empty : "; " + detail); // 새 계획이 나올 때까지 보류 이유가 바뀌어도 재계획 대기 표시 (예열 보류 근거) 를 유지
        }

        if (_status.Set(g.Name, g.Source, null, stage, detail))
        {
            _log.Info($"{g.Name}: auto {stage}" + (detail is null ? string.Empty : $" ({detail})"));
        }
    }
}
