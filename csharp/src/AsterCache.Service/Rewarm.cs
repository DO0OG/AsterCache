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
    private DateTimeOffset? _afterExitAt; // 게임 종료로 점검을 당긴 시각: 그 뒤 첫 점검은 게임 종료라는 계기가 있는 읽기라 하루 읽기 상한을 받지 않음, 게임마다 그 점검을 마치면 지우고 (_afterExitDone) 모든 게임이 마치거나 AfterExitSpan 이 지나면 끝, _nextLock 으로 보호
    private long _afterExitSeq; // 게임 종료 표시를 세운 횟수 (점검이 시작할 때의 번호와 같은 종료에만 완료를 반영), _nextLock 으로 보호
    private readonly HashSet<string> _afterExitDone = new(StringComparer.OrdinalIgnoreCase); // 게임 종료 뒤 점검을 이미 마친 게임, _nextLock 으로 보호
    private static readonly TimeSpan AfterExitSpan = TimeSpan.FromHours(24); // 게임 종료 표시를 이어 두는 최대 시간 (막힌 게임이 영영 표시를 붙들지 않게)
    private readonly object _checkLock = new();
    private readonly Dictionary<string, string> _checkState = new(StringComparer.OrdinalIgnoreCase); // 게임별 마지막 재확인 결과 (로그를 바뀔 때만 남김)
    private readonly Dictionary<string, (bool Reread, int Streak)> _coldRuns = new(StringComparer.OrdinalIgnoreCase); // 게임별: 직전 점검이 식은 캐시를 다시 읽었는가, 다시 읽은 뒤 또 식은 횟수의 연속 (그 게임이 식지 않은 점검이 있으면 지움), _nextLock 으로 보호, 메모리에만 둠
    private readonly Dictionary<string, DateTimeOffset> _gameDue = new(StringComparer.OrdinalIgnoreCase); // 간격을 늘린 게임의 다음 점검 시각 (그전의 점검 틱은 그 게임을 건너뜀, 게임 종료로 당긴 점검은 지움), _nextLock 으로 보호
    private const int MaxCheckIntervalMinutes = 60; // 늘린 점검 간격의 상한 (rewarm_minutes 가 이보다 길면 그 값)
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

        lock (_nextLock)
        {
            foreach (string gone in _coldRuns.Keys.Where(k => !live.Contains(k)).ToList())
            {
                _ = _coldRuns.Remove(gone);
                _ = _gameDue.Remove(gone);
            }
        }

        _prewarm.Solar.Prune(live);
    }

    /// 게임이 끝났을 때: 다음 점검을 auto.rewarm_after_exit_min 뒤로 당깁니다 (이미 더 이르면 그대로). 게이트 (게임 실행 중·디스크 바쁨) 와 식었을 때만 읽는 점검은 Tick 이 그대로 적용합니다.
    public void PullIn(AutoSettings auto, DateTimeOffset? heavyWorkNotBefore = null, bool afterExit = false)
    {
        if (!auto.Enabled || auto.RewarmMinutes <= 0 || heavyWorkNotBefore is null && auto.RewarmAfterExitMin <= 0)
        {
            return;
        }

        DateTimeOffset due = heavyWorkNotBefore ?? _now() + TimeSpan.FromMinutes(auto.RewarmAfterExitMin);
        lock (_nextLock)
        {
            if (afterExit)
            {
                _afterExitAt = _now();
                _afterExitSeq++;
                _afterExitDone.Clear(); // 새 종료마다 모든 게임의 종료 뒤 점검을 다시 한 번씩
            }

            if (_next is not { } next || due < next)
            {
                _next = due;
                _gameDue.Clear(); // 당겨진 점검은 늘린 간격 때문에 건너뛰지 않음
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
                _afterExitAt = null;
                _afterExitDone.Clear();
                _coldRuns.Clear();
                _gameDue.Clear();
            }

            return;
        }

        _env.CpuPercent(); // 틱마다 불러 두 표본 사이가 한 틱이 되게 함 (재예열 판정에는 쓰지 않음)

        DateTimeOffset now = _now();
        bool afterExit;
        long exitSeq;
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
            afterExit = _afterExitAt is { } exitAt && now >= exitAt && now - exitAt < AfterExitSpan; // 점검을 마친 게임은 아래에서 하나씩 지움
            exitSeq = _afterExitSeq; // 이 점검이 시작할 때의 종료: 점검 도중 새 종료가 들어오면 그 종료의 표시는 이 점검이 지우지 않음
        }

        using IDisposable scope = DiskReadLedger.Scope(ReadPurpose.Idle); // 주기 점검의 읽기는 계기 없는 읽기 (하루 상한 대상), 게임 종료 뒤 아직 점검하지 않은 게임만 아래에서 계기 있는 읽기로 바꿈

        IReadOnlyList<GameSettings> warmed = _prewarm.Warmed();

        if (_prewarm.Games.Any(g => _prewarm.Relocating(g.Name)))
        {
            _log.Info("rewarm check skipped: a relocation is running");
            return;
        }

        var budget = _prewarm.WarmBudget(); // 순위가 있으면 예산 안에 드는 게임만 점검 (순위가 바뀌면 다음 점검에서 대상도 바뀜)
        DateTimeOffset? nextGateDue = null;
        foreach (GameSettings g in warmed.OrderByDescending(g => _prewarm.PlayedAt(g.Name))) // 하루 읽기 예산은 가장 최근에 플레이한 게임부터 씀
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

            if (GameCheckDeferred(g.Name, now))
            {
                continue; // 식은 것을 다시 읽는 일이 되풀이돼 이 게임의 점검 간격을 늘린 중
            }

            if (ReadBlock(g, now, auto, budget) is { } block)
            {
                if (block.Due is { } gateDue && (nextGateDue is null || gateDue < nextGateDue))
                {
                    nextGateDue = gateDue;
                }

                NoteCheck(g, block.State, block.Message);
                if (block.State == "busy")
                {
                    LadderWhileBusy(g, auto, now, budget, ct);
                }

                continue;
            }

            bool exitCheck;
            lock (_nextLock)
            {
                exitCheck = afterExit && !_afterExitDone.Contains(g.Name); // 이 게임의 게임 종료 뒤 점검은 계기가 있는 읽기
            }

            using IDisposable gameScope = DiskReadLedger.Scope(exitCheck ? ReadPurpose.Trigger : ReadPurpose.Idle);
            if (!exitCheck && _prewarm.IdleReadsExhausted(g))
            {
                continue; // 이 드라이브의 계기 없는 읽기가 하루 상한에 닿음 (로그는 쉬기 시작할 때 한 번): 표본도 읽지 않고 다음 점검에 다시 봄
            }

            bool? checkedResult = Check(g, ct, exitCheck ? RoomOf(g, budget) : _prewarm.IdleReadRoom(g, RoomOf(g, budget)), auto, now);
            if (exitCheck && checkedResult is not null)
            {
                lock (_nextLock)
                {
                    if (exitSeq == _afterExitSeq) // 점검을 실제로 마친 게임만, 점검이 시작할 때의 종료에만 완료 처리: 막히거나 건너뛴 게임과 도중에 들어온 새 종료의 표시는 다음 점검까지 유지
                    {
                        _ = _afterExitDone.Add(g.Name);
                    }
                }
            }

            NoteColdRun(g.Name, checkedResult, auto, now);
        }

        if (afterExit)
        {
            lock (_nextLock)
            {
                if (exitSeq == _afterExitSeq && warmed.All(g => _afterExitDone.Contains(g.Name)))
                {
                    _afterExitAt = null;
                    _afterExitDone.Clear();
                }
            }
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

    // 게임별 점검 간격 (분): 다시 읽은 캐시가 또 식는 일이 되풀이되는 동안 (다른 프로그램이 캐시를 계속 쓰는 중) 첫 번째는 rewarm_minutes 그대로, 되풀이마다 두 배씩 (10 → 20 → 40) MaxCheckIntervalMinutes 까지 늘립니다.
    // 게임·런처가 시작해 거는 예열, 서비스 시작 예열, 재배치 뒤 예열은 이 점검을 거치지 않고, 게임 종료로 당긴 점검 (PullIn) 은 늘린 간격을 지우고 제때 돕니다.
    private int GameIntervalMinutes(string game, AutoSettings auto)
    {
        int streak;
        lock (_nextLock)
        {
            streak = _coldRuns.TryGetValue(game, out (bool Reread, int Streak) run) ? run.Streak : 0;
        }

        return Math.Max(auto.RewarmMinutes, (int)Math.Min(MaxCheckIntervalMinutes, (long)auto.RewarmMinutes << streak));
    }

    private bool GameCheckDeferred(string game, DateTimeOffset now)
    {
        lock (_nextLock)
        {
            return _gameDue.TryGetValue(game, out DateTimeOffset due) && now < due;
        }
    }

    // 점검 한 번의 결과를 그 게임의 연속 횟수에 반영합니다 (Check 의 반환값: true 는 식은 캐시를 실제로 다시 읽음, false 는 식지 않음, null 은 그 밖이라 그대로).
    // 직전 점검도 다시 읽었는데 또 식었으면 횟수를 늘리고 (첫 식음은 늘리지 않음), 식지 않은 점검이 있으면 이 게임의 횟수를 지웁니다 (다른 게임의 결과는 무관). 간격이 바뀔 때만 로그 한 줄.
    private void NoteColdRun(string game, bool? result, AutoSettings auto, DateTimeOffset now)
    {
        if (result is null)
        {
            return;
        }

        int before = GameIntervalMinutes(game, auto);
        lock (_nextLock)
        {
            (bool reread, int streak) = _coldRuns.TryGetValue(game, out (bool Reread, int Streak) run) ? run : (false, 0);
            if (result == true)
            {
                _coldRuns[game] = (true, reread ? Math.Min(streak + 1, 8) : streak); // 8 이면 어떤 rewarm_minutes 도 상한에 닿음 (시프트가 넘치지 않게)
            }
            else
            {
                _ = _coldRuns.Remove(game);
            }
        }

        int minutes = GameIntervalMinutes(game, auto);
        lock (_nextLock)
        {
            if (minutes > auto.RewarmMinutes)
            {
                _gameDue[game] = now + TimeSpan.FromMinutes(minutes);
            }
            else
            {
                _ = _gameDue.Remove(game);
            }
        }

        if (minutes != before)
        {
            _log.Info(minutes > before
                ? $"{game}: rewarm check interval raised to {minutes} min: the cache went cold again right after it was read"
                : $"{game}: rewarm check interval back to {minutes} min: the cache stayed warm for a whole check period");
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
            ProbeResult? probe = _prewarm.SupplementPending(g.Name)
                ? _prewarm.ProbeFront(g, ct)
                : _prewarm.ColdSamples(g) is { } samples ? _prewarm.ProbeSamplesForGame(g, samples, "rewarm sample", ct) : _prewarm.ProbeFront(g, ct); // 기록 없이 예열한 게임은 그때 읽은 파일 목록에서 고른 표본
            return probe;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            _log.Warn($"{g.Name}: rewarm check failed: {e.GetType().Name}");
            failed = true;
            return null;
        }
    }

    // 돌려주는 값: 식은 캐시를 실제로 다시 읽었으면 true, 식지 않은 것을 확인했으면 false, 그 밖 (건너뜀·읽기 전에 건너뛴 재예열·보충·표본 없음·실패) 은 null.
    private bool? Check(GameSettings g, CancellationToken ct, long? roomMib, AutoSettings auto, DateTimeOffset now)
    {
        if (_prewarm.RewarmAlreadyCovered(g))
        {
            NoteCheck(g, "recent-warm", $"{g.Name}: rewarm check skipped (prewarm is running or recently completed)");
            return null;
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

            int waitMinutes = GameIntervalMinutes(g.Name, auto); // 되풀이되면 점검과 같은 규칙으로 늘어난 간격
            if (last is { } since && now >= since && now - since < TimeSpan.FromMinutes(waitMinutes))
            {
                NoteCheck(g, "ladder-missing-wait", $"{g.Name}: rewarm check: part of the list is not in memory (found by the ladder), waiting until {waitMinutes} min after the last prewarm attempt");
                return null;
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

            return Read(result);
        }

        // 식은 앞부분 다시 읽기를 쉬는 게임은 표본도 읽지 않습니다 (표본도 식은 구간의 디스크 읽기). 보충은 그대로 진행.
        if (_prewarm.SupplementPending(g.Name) && _prewarm.PartialRewarmPause(g.Name) is { } pause)
        {
            NoteCheck(g, pause.Resting ? "partial-rewarm-rest" : "partial-rewarm-limit", PartialRewarmRestMessage(g, pause.Until,
                pause.Resting ? "it went cold again soon after the last read" : $"it was already read again {Prewarmer.MaxPartialRewarmsPerDay} times in the last 24 h"));
            _ = _prewarm.SupplementWhenIdle(g, ct);
            return null;
        }

        ProbeResult? probe = Probe(g, ct, out bool failed);
        if (failed)
        {
            return null;
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
                LadderWaitingSupplement(g, ct, auto: auto); // 보충이 메모리를 기다리는 중이면 (곧 더 읽을 수 있는 상태가 아니면) 읽은 범위의 첫 사다리를 만듦
                return false;
            }

            if (!supplemented && probe.Cold && _prewarm.PartialRewarmReadable(g.Name))
            {
                if (_prewarm.NotePartialColdAgain(g.Name, TimeSpan.FromMinutes(2L * auto.RewarmMinutes)) is { } restUntil)
                {
                    NoteCheck(g, "partial-rewarm-rest", PartialRewarmRestMessage(g, restUntil, $"it went cold again within {2L * auto.RewarmMinutes} min of the last read"));
                    return null;
                }

                NoteCheck(g, "partial-cold", $"{g.Name}: rewarm check: the part read into memory went cold ({probe.Slow}/{probe.Samples} samples over {CacheProbe.ColdThresholdMs:F1} ms, available {_env.AvailableRamMib()} MiB), reading it again (cooldown ignored)"); // 부분 예열에서 실제로 읽은 범위만 표본과 재예열에 씁니다.
                return Read(_prewarm.RewarmPartialGame(g, ct, roomMib));
            }
        }
        else if (!probe.Cold)
        {
            NoteCheck(g, "warm", $"{g.Name}: rewarm check: warm ({probe.Slow}/{probe.Samples} samples over {CacheProbe.ColdThresholdMs:F1} ms, average {probe.AverageMs:F2} ms)");
            solar.ClearOverlooked(g.Name);
            LadderUnmarked(g, ct, auto: auto); // 서비스를 다시 시작한 뒤 표식이 없는 게임은 warm 일 때 첫 사다리를 만듦
            return false;
        }
        else
        {
            NoteCheck(g, "cold", $"{g.Name}: rewarm check: cold ({probe.Slow}/{probe.Samples} samples over {CacheProbe.ColdThresholdMs:F1} ms, average {probe.AverageMs:F2} ms, available {_env.AvailableRamMib()} MiB), prewarming again (cooldown ignored){solar.SinceLadder(g.Name)}");
            return Read(_prewarm.RewarmGame(g, ct, roomMib));
        }

        return null;
    }

    private static string PartialRewarmRestMessage(GameSettings g, DateTimeOffset until, string why) =>
        $"{g.Name}: rewarm check: not reading the part read into memory again until {until:O} ({why})";

    // 재예열이 실제로 읽었으면 true (메모리·지연·대여 거절 등으로 읽기 전에 건너뛰었으면 null: 간격 늘리기에 세지 않음).
    private static bool? Read(PrewarmResult result) => result.Ran || result.Stats is { Bytes: > 0 } ? true : null;

}
