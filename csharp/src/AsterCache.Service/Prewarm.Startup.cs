using System.ComponentModel;
using System.Diagnostics;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// Prewarmer 의 서비스 시작 예열과 묶음 예열: 시작 게이트, 재예열, 머리 부분 예열.
public sealed partial class Prewarmer
{
    // roomMib: WarmBudget 가 이 게임에 배정한 양 (핫 집합이 더 크면 앞쪽 이만큼만 읽음: 일반 부분 예열 경로).
    internal PrewarmResult RewarmGame(GameSettings game, CancellationToken ct = default, long? roomMib = null) => Guarded(game, "rewarm", pin: true, ignoreCooldown: true, ct, roomMib: roomMib);

    internal bool RewarmAlreadyCovered(GameSettings game)
    {
        if (string.Equals(_streaming, game.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_coldReading, game.Name, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        DateTimeOffset now = _now();
        return LastWarmed(game.Name) is { } warmedAt && now >= warmedAt && now - warmedAt < RecentWarmupWindow;
    }

    /// 서비스 시작 뒤: on_service_start 인 게임을 차례로 (started 를 주면 그 조건을 만족하는 게임만: 자동 모드의 플레이한 게임).
    /// recent (최근순) 를 주면 한 묶음으로 합칩니다: on_service_start 게임 먼저, 그다음 최근순 (중복 제거) — RAM 예산을 한 번만 씁니다.
    /// 부팅 PrewarmBootDelay 뒤 + 게임 드라이브 PrewarmIdleNeeded 유휴까지 기다린 다음 (그동안 ct 가 취소되면 아무것도 하지 않음), 핫 집합이 이미 파일 캐시에 있는 게임은 읽지 않고 건너뜁니다.
    /// lastPlayed (게임 이름 → 마지막 플레이) 를 주면 (자동 모드가 켜진 경우) 대상을 최근 <see cref="StartupPlayedWithin"/> 안에 플레이한 게임으로 좁혀 마지막 플레이 순 상위 auto.startup_warm_games 개만 남기고
    /// (on_service_start 게임도 같은 순위), 합계 읽기량이 auto.startup_warm_max_mib 를 넘는 부분은 읽지 않습니다 (넘는 게임은 앞부분만, 이미 다 쓴 뒤의 게임은 건너뜀). 합계 상한은 기록이 없는 게임 (통째 예열) 도 포함해 항상 적용합니다.
    public IReadOnlyList<PrewarmResult> RunServiceStart(IEnumerable<GameSettings>? recent = null, CancellationToken ct = default, Func<GameSettings, bool>? started = null,
        Func<string, DateTimeOffset?>? lastPlayed = null)
    {
        AutoSettings a = _auto;
        GameSettings[] first = _games.Where(g => g.Prewarm.OnServiceStart && (started?.Invoke(g) ?? true)).ToArray();
        IEnumerable<GameSettings> merged = first.Concat((recent ?? []).Where(g => !first.Any(f => string.Equals(f.Name, g.Name, StringComparison.OrdinalIgnoreCase))));
        merged = [.. merged.Where(g => !PolicyBlocked(g, "service-start prewarm"))]; // 정책이 예열을 막은 게임은 시작 예열 순위·개수·상한을 쓰지 않음
        if (lastPlayed is not null)
        {
            DateTimeOffset cutoff = _now() - StartupPlayedWithin;
            merged = merged.Select(g => (Game: g, At: lastPlayed(g.Name))).Where(x => x.At is { } at && at >= cutoff)
                .OrderByDescending(x => x.At).ThenBy(x => x.Game.Name, StringComparer.Ordinal).Take(a.StartupWarmGames).Select(x => x.Game);
        }

        GameSettings[] list = [.. merged];
        if (list.Length == 0 || !WaitStartupGate(list, ct, PrewarmBootDelay, PrewarmIdleNeeded))
        {
            return [];
        }

        var skipped = new List<PrewarmResult>();
        var warmSkipped = new List<GameSettings>(); // 이미 warm 이라 건너뛴 게임 (표본 판정을 그대로 써서 곧바로 첫 사다리)
        var cold = new List<GameSettings>();
        var trimmed = new Dictionary<string, (long WarmedMib, long HotMib)>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<GameSettings>();
        long availNow = _env.AvailableRamMib();
        foreach (GameSettings g in list)
        {
            if (AlreadyWarm(g) is true)
            {
                LogSkipped(g.Name, "already warm", $"{g.Name}: prewarm skipped (already warm) [service start]");
                skipped.Add(new PrewarmResult(g.Name, false, "already warm", null, _now()));
                warmSkipped.Add(g);
            }
            else if (_env.ZoneBytes(g) is not null && !IsCold(g) && WillSkipReason(g, availNow) is { } why)
            {
                skipped.Add(SkipAtServiceStart(g, why)); // 쿨다운 같은 사유로 어차피 건너뜀: 시작 상한을 배정하지 않고, 다른 게임을 읽는 사이 사유가 풀려도 상한 없이 읽지 않게 여기서 결과를 확정
            }
            else
            {
                candidates.Add(g);
            }
        }

        _rewarm.LadderAfterStartSkip(warmSkipped, _auto, ct); // 서비스를 다시 시작하면 표식이 없어 첫 재예열 점검까지 새로 고침이 없던 공백을 메움

        // 시작 상한도 출처별 두 단계로 나눕니다: 모든 게임의 관찰분을 먼저 (입력 순서), 남은 상한을 보충분에 씁니다. 기록 없는 게임은 전부 첫 단계에서 순서대로 셉니다.
        var totalMib = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var observedMib = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (GameSettings g in candidates)
        {
            long? zone = _env.ZoneBytes(g);
            long mib = ((zone ?? (long)DefaultFullPrewarmMib << 20) + (1 << 20) - 1) >> 20; // 기록이 없으면 통째 예열의 기본 상한만큼으로 셈
            totalMib[g.Name] = mib;
            observedMib[g.Name] = zone is null || IsCold(g) ? mib : ObservedMib(g, mib, allowSourceRead: !DriveHasRunningGame(g));
        }

        var startupAlloc = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long plannedMib = 0;
        foreach (bool observedPhase in new[] { true, false })
        {
            foreach (GameSettings g in candidates)
            {
                long portion = observedPhase ? observedMib[g.Name] : totalMib[g.Name] - observedMib[g.Name];
                long remaining = Math.Max(0, a.StartupWarmMaxMib - plannedMib);
                long take = _env.ZoneBytes(g) is null && portion > remaining ? 0 : Math.Min(portion, remaining); // 기록 없는 게임은 통째로 들어갈 때만 (건너뛰면서 상한을 쓰지 않음)
                startupAlloc[g.Name] = startupAlloc.GetValueOrDefault(g.Name) + take;
                plannedMib += take;
            }
        }

        foreach (GameSettings g in candidates)
        {
            long? zone = _env.ZoneBytes(g);
            long mib = totalMib[g.Name];
            long allocation = startupAlloc[g.Name];
            if (allocation <= 0 && (mib > 0 || plannedMib >= a.StartupWarmMaxMib) || (zone is null && allocation < mib))
            {
                LogSkipped(g.Name, "startup byte cap", $"{g.Name}: prewarm skipped (the service-start cap of {a.StartupWarmMaxMib} MiB is used up) [service start]");
                PrewarmResult result = new(g.Name, false, "startup byte cap", null, _now());
                TrackPendingWarm(g, result, result.At);
                skipped.Add(result);
            }
            else if (zone is not null && allocation < mib)
            {
                // 상한을 넘는 만큼은 읽지 않음: 핫 집합의 앞부분만 (max_stream_mib 를 남은 양으로 줄인 사본)
                trimmed[g.Name] = (allocation, mib);
                cold.Add(g with { MaxStreamMib = (int)Math.Min(allocation, int.MaxValue), MaxStreamSet = true });
            }
            else
            {
                cold.Add(g);
            }
        }

        IReadOnlyList<PrewarmResult> ran;
        lock (_gate)
        {
            foreach (KeyValuePair<string, (long, long)> t in trimmed)
            {
                _startupTrim[t.Key] = t.Value;
            }
        }

        try
        {
            ran = RunBatch(cold, "service start", ct: ct, yieldToRelocation: true);
        }
        finally
        {
            lock (_gate)
            {
                _startupTrim.Clear();
            }
        }

        foreach (PrewarmResult r in ran.Where(r => r is { Ran: true, Mode: null }))
        {
            if (trimmed.TryGetValue(r.Game, out (long WarmedMib, long HotMib) t))
            {
                if (list.FirstOrDefault(g => string.Equals(g.Name, r.Game, StringComparison.OrdinalIgnoreCase)) is { } game)
                {
                    long warmedMib = ActualMib(r.Stats, t.WarmedMib);
                    NotePartial(game, warmedMib, t.HotMib, r.Stats?.Bytes ?? MibBytes(warmedMib)); // 상한으로 앞부분만 읽음 (실제 읽은 양): 유휴 때 나머지를 보충 (상태 파일에는 Record 가 이미 남김)
                }
            }
        }

        return [.. skipped, .. ran];
    }

    /// 서비스 시작 예열 대상이 되는 마지막 플레이의 최대 경과.
    public static readonly TimeSpan StartupPlayedWithin = TimeSpan.FromDays(14);

    /// 부팅 BootDelay 가 지났는가: 자동 재배치·새 트레이스 예열과 대기 예열 재시도가 보는 조건 (서비스 시작 예열 게이트는 더 짧은 PrewarmBootDelay).
    public bool BootSettled() => _env.UptimeMs() >= BootDelay.TotalMilliseconds;

    /// 서비스 시작 예열과 같은 조건 (부팅 PrewarmBootDelay 뒤 + 게임 드라이브 PrewarmIdleNeeded 유휴) 을 기다립니다. 취소되면 false.
    public bool WaitServiceStartGate(IReadOnlyList<GameSettings> games, CancellationToken ct) => games.Count > 0 && WaitStartupGate(games, ct, PrewarmBootDelay, PrewarmIdleNeeded);

    /// 부팅 뒤 경과 시간 (ms, PrewarmEnv.UptimeMs). 부팅 조용 구간 판정이 같은 시계를 씁니다.
    public long UptimeMs() => _env.UptimeMs();

    // 부팅 bootDelay 경과 + 게임 드라이브 idleNeeded 유휴를 기다립니다. 취소되면 false. 대상 드라이브 중 하나라도 처리량을 못 읽으면 (null) 유휴를 확인할 수 없으므로 읽지 않고 보류합니다.
    // bootDelay·idleNeeded: 서비스 시작 예열은 PrewarmBootDelay·PrewarmIdleNeeded, 게임 사이 재배치 대기 (WaitOutRelocation) 는 기존 BootDelay·IdleNeeded.
    private bool WaitStartupGate(IReadOnlyList<GameSettings> games, CancellationToken ct, TimeSpan bootDelay, TimeSpan idleNeeded)
    {
        TimeSpan idle = TimeSpan.Zero;
        bool logged = false;
        bool heldLogged = false;
        while (!ct.IsCancellationRequested)
        {
            if (RelocationHeld())
            {
                idle = TimeSpan.Zero; // 재배치가 끝난 뒤 유휴를 처음부터 다시 셉니다
                _env.Sleep(GateStep, ct);
                continue;
            }

            double?[] rates = [.. games.Select(g => _env.DiskBytesPerSecond(g))];
            bool settled = _env.UptimeMs() >= bootDelay.TotalMilliseconds;
            if (settled && rates.Any(r => r is null))
            {
                // 디스크 유휴를 확인할 수 없음: 읽지 않고 계측값이 생길 때까지 (또는 취소될 때까지) 보류
                idle = TimeSpan.Zero;
                if (!heldLogged)
                {
                    heldLogged = true;
                    _log.Info("service start prewarm held: disk activity cannot be measured");
                }

                _env.Sleep(GateStep, ct);
                continue;
            }

            bool busy = !settled || rates.Any(r => r >= DiskActivity.BusyBytesPerSecond);
            idle = busy ? TimeSpan.Zero : idle + GateStep;
            if (idle >= idleNeeded)
            {
                return true;
            }

            if (!logged)
            {
                logged = true;
                _log.Info($"service start prewarm waits for {bootDelay.TotalSeconds:F0} s after boot and {idleNeeded.TotalSeconds:F0} s of idle disk");
            }

            _env.Sleep(GateStep, ct);
        }

        return false;
    }

    // 한 묶음의 예산 = min(예열 예산, 가용 RAM - 여유). 존 크기 합이 예산을 넘는 게임만 건너뛰고, 뒤 게임은 남은 예산으로 따로 판단합니다 (앞쪽 = 최근 순서가 우선).
    // 서비스 시작·런처 시작이 같은 규칙입니다. 첫 게임의 가용 RAM 쪽 판단은 Run 의 자체 판단에 맡깁니다.
    // yieldToRelocation: 게임 사이마다 재배치가 시작됐는지 다시 보고, 돌고 있으면 끝날 때까지 기다린 뒤 이어서 읽습니다 (서비스 시작 예열: 게이트를 지난 직후나 예열 도중 시작된 재배치와 겹치지 않게).
    // sampled: 호출 쪽이 이미 쓴 쿨다운·콜드 판정 표본 읽기 시간 (트리거 경로), 읽기 전 준비 시간 로그에만 씀.
    private IReadOnlyList<PrewarmResult> RunBatch(IEnumerable<GameSettings> games, string why, bool pin = true, CancellationToken ct = default, bool yieldToRelocation = false, bool trigger = false, TimeSpan sampled = default)
    {
        long lapAt = Stopwatch.GetTimestamp();
        double Lap() // 직전 구간 이후 경과 초
        {
            double seconds = Stopwatch.GetElapsedTime(lapAt).TotalSeconds;
            lapAt = Stopwatch.GetTimestamp();
            return seconds;
        }

        var results = new List<PrewarmResult>();
        IReadOnlyList<GameSettings> list = games as IReadOnlyList<GameSettings> ?? [.. games];
        IReadOnlyList<GameSettings> given = list; // 결과는 받은 순서로 돌려줌 (읽는 순서만 예산에 따라 바뀔 수 있음)
        if (list.Any(g => PolicyBlocksPrewarm(g)))
        {
            // 게임 정책이 예열을 막은 게임은 예산 계산과 읽기에서 빼고 결과만 남김
            foreach (GameSettings denied in list.Where(g => PolicyBlocked(g, $"prewarm [{why}]")))
            {
                results.Add(new PrewarmResult(denied.Name, false, GamePolicySkip, null, _now()));
            }

            list = [.. list.Where(g => !PolicyBlocksPrewarm(g))];
        }

        long cold = list.Count > 1 ? ColdMib(null) : 0; // 기록 없는 예열 양: 순위 모의 계산과 앞부분 예열이 한 번만 읽어 같이 씀
        if (list.Count > 2)
        {
            long availNow = _env.AvailableRamMib();
            long physNow = PhysCapMib();
            list = OrderByValuePerByte(list, g => SkipsInBatch(g, availNow) ? 0 : ((_env.ZoneBytes(g) ?? 0) + (1 << 20) - 1) >> 20, (g, used) => BatchRoomMib(physNow, availNow, g.Prewarm.MinFreeRamMib, cold + used));
        }

        double ordered = Lap();
        long avail = _env.AvailableRamMib();
        long physCap = PhysCapMib(); // MiB, 예열 예산
        (long[] observed, long[] later) = ObservedSplit(list, avail);
        double planned = Lap();
        var heads = new HeadTimes();
        try
        {
            WarmHeads(list, ct, yieldToRelocation, cold, observed, later, trigger, heads);
        }
        finally
        {
            LogPrepared(why, sampled.TotalSeconds, ordered, planned, heads, Lap()); // 앞부분 도중 취소·예외로 나가도 준비에 쓴 시간은 남김
        }

        long used = 0; // 핫 집합 예열의 MiB (기록 없는 예열은 _coldMib 가 셈)
        for (int i = 0; i < list.Count; i++)
        {
            GameSettings g = list[i];
            if (yieldToRelocation && !WaitOutRelocation(g, ct))
            {
                PrewarmResult cancelled = new(g.Name, false, Cancelled, null, _now());
                TrackPendingWarm(g, cancelled, cancelled.At); // 기다리는 동안 취소됨 (읽지 않았으므로 상태 파일에는 기록하지 않음)
                results.Add(cancelled);
                continue;
            }

            if (Deferred(g))
            {
                results.Add(new PrewarmResult(g.Name, false, PostUpdateDelay, null, _now()));
                continue;
            }

            long zoneMib = ZoneMib(g);
            long held = used + ColdMib(null); // 이미 올린 양 (기록 없는 예열 포함): 기록 없는 예열이 먼저 돌아도 새 사용량이 바로 셈에 들어감
            long room = RoomKeepingLaterObserved(BatchRoomMib(physCap, avail, g.Prewarm.MinFreeRamMib, held), observed[i], later[i]); // 묶음에 남은 예산: 전체가 들어가지 않으면 들어가는 만큼만 읽음 (부분 예열)
            bool fits = zoneMib <= room;
            if (!fits && room < MinPartialMib)
            {
                LogSkipped(g.Name, "batch memory budget", $"{g.Name}: prewarm skipped (batch zones {held + zoneMib} MiB exceed the budget: min(cache budget {physCap} MiB, available {avail} MiB - reserve {g.Prewarm.MinFreeRamMib} MiB)) [{why}]");
                PrewarmResult skipped = new(g.Name, false, "batch memory budget", null, _now());
                TrackPendingWarm(g, skipped, skipped.At);
                results.Add(skipped);
                continue;
            }

            PrewarmResult r = Guarded(g, why, pin, ct: ct, usedMib: used, roomMib: fits ? null : room, trigger: trigger);
            results.Add(r);
            used += r.Ran && r.Mode is null ? r.Stats is { Bytes: > 0 } st ? (st.Bytes + (1 << 20) - 1) >> 20 : zoneMib : 0; // 예산에서는 설정한 상한이 아니라 실제로 읽은 양을 뺌 (통계가 없으면 존 크기)
        }

        if (ReferenceEquals(given, list))
        {
            return results;
        }

        var rank = new Dictionary<string, int>(StringComparer.Ordinal); // 받은 순서 (같은 이름이면 처음 것)
        for (int i = 0; i < given.Count; i++)
        {
            rank.TryAdd(given[i].Name, i);
        }

        return [.. results.OrderBy(r => rank.GetValueOrDefault(r.Game, -1))];
    }

    // 읽기 전 준비 (표본·순서 계산·목록 읽기와 배치 조회·앞부분 읽기) 가 PrepareLogAbove 를 넘으면 단계별 시간을 한 줄로: 취소된 실행은 prewarm phases 줄이 없어 어디서 시간이 갔는지 보이지 않음.
    internal TimeSpan PrepareLogAbove = TimeSpan.FromSeconds(5); // 시험이 낮춤

    /// 앞부분 읽기 준비에 쓴 시간: 목록 계산·배치 조회 (첫 읽기를 내기 전)와 실제 읽기. 게임마다 StreamHead 가 더함.
    internal sealed class HeadTimes
    {
        public double Planning;
        public double Reading;
    }

    // headsTotal: WarmHeads 전체 시간. 계획·읽기를 뺀 나머지는 락 대기·게임별 판정 (other waits).
    internal void LogPrepared(string why, double sampled, double ordered, double planned, HeadTimes heads, double headsTotal)
    {
        double total = sampled + ordered + planned + headsTotal;
        if (total >= PrepareLogAbove.TotalSeconds)
        {
            double other = Math.Max(0, headsTotal - heads.Planning - heads.Reading);
            _log.Info($"prewarm prepared in {total:F1} s before the per-game reads [{why}]: cooldown and cold samples {sampled:F1} s, order {ordered:F1} s, lists and layout lookups {planned:F1} s, load head plans and layout lookups {heads.Planning:F1} s, load head reads {heads.Reading:F1} s, other waits in load heads {other:F1} s");
        }
    }

    // 모의 계산에서 실제로 건너뛸 게임은 예산을 차지하지 않음: 업데이트 지연·기록 없는 예열·그 밖의 건너뜀 사유 (메모리 부족은 부분 예열이 읽으니 제외).
    private bool SkipsInBatch(GameSettings g, long avail) => WillSkipReason(g, avail) is not null || IsCold(g);

    // Run 이 메모리 부족 외의 사유로 건너뛸 게임의 사유 (없으면 null): 업데이트 지연 포함.
    private string? WillSkipReason(GameSettings g, long avail)
    {
        TimeSpan? since = CooldownSince(g, _now());
        string? skip = PrewarmPolicy.SkipReason(_env.ZoneBytes(g), _env.TraceExists(g), DriveHasRunningGame(g), avail, g.Prewarm.MinFreeRamMib, since, TimeSpan.FromMinutes(g.Prewarm.CooldownMin));
        if (skip is not null && !skip.StartsWith("low memory", StringComparison.Ordinal))
        {
            return skip;
        }

        return Deferred(g) ? PostUpdateDelay : null;
    }

    // 서비스 시작 분류에서 건너뛰기로 한 게임의 결과를 Run 과 같은 방식으로 남김 (업데이트 지연은 기록 없이 대기만).
    private PrewarmResult SkipAtServiceStart(GameSettings g, string reason)
    {
        DateTimeOffset now = _now();
        PrewarmResult result = new(g.Name, false, reason, null, now);
        if (reason == PostUpdateDelay)
        {
            TrackPendingWarm(g, result, now);
            return result;
        }

        LogSkipped(g.Name, reason, () => $"{g.Name}: prewarm skipped ({reason}) [service start]{(reason == "game is running" ? BlockerNote(g) : string.Empty)}");
        return Record(g, now, result, 0);
    }

    // 묶음에 게임이 둘 이상이면 각 게임의 로딩 앞부분 (tier0) 을 최근 순서로 먼저 모두 올리고, 그 뒤 게임별 전체 (tier1 포함) 를 읽습니다 (앞부분은 이미 올라와 있어 다시 읽어도 빠름).
    // 올릴 게임은 RunBatch 와 같은 예산과 Run 과 같은 건너뜀 조건으로 고릅니다. 취소는 앞부분 묶음 사이에서 보고, 읽던 게임이 시작되면 끝까지 읽습니다 (StreamHot 과 같음).
    private void WarmHeads(IReadOnlyList<GameSettings> games, CancellationToken ct, bool yieldToRelocation, long planned, long[] observed, long[] later, bool allowWhileExiting, HeadTimes times)
    {
        if (games.Count < 2)
        {
            return;
        }

        long avail = _env.AvailableRamMib();
        long physCap = PhysCapMib();
        void Skipped(GameSettings g, string reason) => LogSkipped(g.Name, "head skipped " + reason, $"{g.Name}: load head prewarm skipped ({reason})"); // 앞부분을 읽지 않은 게임도 이유를 남김 (읽은 게임은 StreamHead 가 시작·끝을 남김)
        for (int i = 0; i < games.Count; i++)
        {
            GameSettings g = games[i];
            if (ct.IsCancellationRequested || (yieldToRelocation && !WaitOutRelocation(g, ct)))
            {
                Skipped(g, "cancelled before reading");
                return;
            }

            if (Deferred(g))
            {
                Skipped(g, PostUpdateDelay);
                continue;
            }

            long zoneMib = ZoneMib(g);
            long room = RoomKeepingLaterObserved(BatchRoomMib(physCap, avail, g.Prewarm.MinFreeRamMib, planned), observed[i], later[i]); // RunBatch 와 같은 남은 예산
            long? hotBytes = _env.ZoneBytes(g);
            TimeSpan? since = CooldownSince(g, _now());
            string? skip = PrewarmPolicy.SkipReason(hotBytes, _env.TraceExists(g), DriveHasRunningGame(g), avail, g.Prewarm.MinFreeRamMib, since, TimeSpan.FromMinutes(g.Prewarm.CooldownMin));
            if (skip?.StartsWith("low memory", StringComparison.Ordinal) == true && ReadSetBytes(g) is { } readBytes && readBytes < hotBytes)
            {
                skip = PrewarmPolicy.SkipReason(readBytes, _env.TraceExists(g), DriveHasRunningGame(g), avail, g.Prewarm.MinFreeRamMib, since, TimeSpan.FromMinutes(g.Prewarm.CooldownMin)); // Run 과 같은 재판단: 읽을 집합이 들어가면 진행
                hotBytes = readBytes;
            }

            bool lowMemory = skip?.StartsWith("low memory", StringComparison.Ordinal) == true;
            long hotMib = ((hotBytes ?? 0) + (1 << 20) - 1) >> 20; // 실제로 읽을 양 (RunBatch 의 Run 과 같은 기준)
            long take = hotMib; // 이 게임이 예산에서 가져갈 양 (RunBatch 가 실제로 읽을 양과 같게)
            GameSettings head = g;
            if (lowMemory || hotMib > room)
            {
                // 전체가 안 들어가도 Run 의 부분 예열이 읽으면 앞부분도 올림 (RunBatch 와 같은 판단, 상한은 Run 과 같게 전체 입장 때는 남은 예산을 씀)
                if (PartialCapMib(g, hotMib, WindowAvailableMib(avail), physCap, zoneMib <= room ? long.MaxValue : room, FootprintOf(g)?.Mib) is not { } cap)
                {
                    Skipped(g, "batch memory budget");
                    continue; // 이 게임만 건너뜀: 뒤 게임은 남은 예산으로 RunBatch 와 같이 따로 판단
                }

                take = cap;
                head = g with { MaxStreamMib = (int)cap, MaxStreamSet = true }; // 앞부분 읽기도 상한을 지킴 (tier0 가 cap 보다 커도)
                skip = lowMemory ? null : skip;
            }

            if (IsCold(g) || skip is not null)
            {
                Skipped(g, skip ?? "no record yet");
                continue;
            }

            planned += take;
            if (!EnterRun(g.Name, ct, allowWhileExiting))
            {
                Skipped(g, "cancelled while waiting for the run lock");
                return; // 트리거 경로가 락을 기다리다 취소됨: 앞부분도 읽지 않음
            }

            try
            {
                if (!allowWhileExiting && TriggerWaitCovers(g.Name))
                {
                    Skipped(g, PreemptedByTrigger);
                    continue; // 트리거 예열이 기다리는 동안 같은 디스크의 다른 예열은 앞부분도 읽지 않음 (뒤의 게임별 예열이 같은 이유로 기존 재시도에 맡김), 다른 디스크의 뒤 게임은 계속
                }

                if (_games.Any(x => string.Equals(x.Name, g.Name, StringComparison.OrdinalIgnoreCase) && GamePolicy.AllowsPrewarm(x)))
                {
                    StreamHead(head, ct, allowWhileExiting, times); // 대기 중 등록이 해제됐거나 정책이 예열을 막은 게임은 앞부분도 읽지 않음
                }
                else
                {
                    Skipped(g, "game removed or policy");
                }
            }
            finally
            {
                Monitor.Exit(_run);
            }
        }
    }

    private void StreamHead(GameSettings game, CancellationToken ct, bool allowWhileExiting, HeadTimes times)
    {
        long startedAt = Stopwatch.GetTimestamp();
        long? readAt = null; // 첫 읽기를 낸 시각: 그 앞은 목록 계산·배치 조회
        using IDisposable work = TrackGameWork(game.Name);
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "warm heads", ct, preserveForGameRoot: _auto.KeepStreamOnGameStart ? game.OriginRoot : null, preserveGameKey: game.Name, allowWhileExiting: allowWhileExiting);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            LogSkipped(game.Name, "head skipped disk read lease refused", $"{game.Name}: load head prewarm skipped (disk read lease refused)");
            return;
        }

        using var stop = new CancellationTokenSource();
        if (!TryMarkReading(game.Name, () =>
        {
            (_keepStream, _streaming, _streamStop) = (false, game.Name, stop);
            (_streamFromTrigger, _streamPreempted) = (allowWhileExiting, false);
            _yield = null;
            _driveObservedAt = _now();
        }))
        {
            LogSkipped(game.Name, "head skipped " + RelocationRunning, $"{game.Name}: load head prewarm skipped ({RelocationRunning})");
            return; // 같은 디스크에서 재배치가 시작함: 앞부분은 읽지 않음 (전체 예열 Run 이 같은 이유로 건너뜀을 남김)
        }

        InvalidateResidencyProbe(game.Name, keepCooldown: true);
        var readYield = new ReadYield(_env.SetReadIoLow, PrewarmYield.StartsYielding(allowWhileExiting ? "trigger" : null, _env.UptimeMs())); // 읽기와 묶음 점검이 같은 스레드: 양보는 StreamHot 과 같은 방식
        long headDone = 0; // 취소돼도 그때까지 읽은 양을 로그에 남기려고 묶음 점검마다 받아 둠
        bool ended = false; // 끝·취소·실패 줄 중 하나를 남겼는가 (호출자 취소·예외로 나갈 때 finally 가 남김)
        string Took() => $"{headDone >> 20} MiB in {Stopwatch.GetElapsedTime(startedAt).TotalSeconds:F1} s{readYield.Mix.Note()}";
        try
        {
            using CancellationTokenRegistration leaseReg = lease?.Token.Register(stop.Cancel) ?? default;
            using CancellationTokenRegistration callerReg = ct.Register(() =>
            {
                if (!_keepStream)
                {
                    stop.Cancel();
                }
            });
            lease?.NoteReadIssue("head schedule", 0, false);
            var cancelDetail = new PrewarmCancelDetail();
            _log.Info($"{game.Name}: load head prewarm started");
            _streamer.StreamHeadCoordinated(game, stop.Token, (done, total) =>
                {
                    headDone = done;
                    CheckPrewarmBatch(game, stop, done, total, detail: cancelDetail, callerToken: ct, leaseToken: lease?.Token ?? default, yield: readYield, preemptible: true);
                },
                () => !stop.IsCancellationRequested && (lease?.IsValid ?? true),
                (stage, bytes, isData) =>
                {
                    if (isData && readAt is null)
                    {
                        readAt = Stopwatch.GetTimestamp();
                        readYield.NoteRead(0, _now()); // 이 스트리머는 읽기 전에 진행 0 을 알리지 않으므로 첫 묶음의 양을 셀 기준점
                    }

                    lease?.NoteReadIssue(stage, bytes, isData);
                });
            ended = true;
            if (stop.IsCancellationRequested)
            {
                LogSkipped(game.Name, "head cancelled " + (cancelDetail.First ?? "cancelled externally"),
                    $"{game.Name}: load head prewarm cancelled ({cancelDetail.First ?? "cancelled externally"}) after {Took()}");
            }
            else
            {
                _log.Info($"{game.Name}: load head prewarm done: {Took()}");
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ended = true;
            _log.Info($"{game.Name}: load head prewarm cancelled (planning) after {Took()}"); // 계획 도중 대여가 취소됨: 서비스 종료가 아니면 RunBatch 를 계속함
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            ended = true;
            _log.Warn($"{game.Name}: load head prewarm failed: {e.GetType().Name}"); // 전체 예열(Run)이 같은 오류를 판단해 기록함
        }
        finally
        {
            long endedAt = Stopwatch.GetTimestamp();
            times.Planning += Stopwatch.GetElapsedTime(startedAt, readAt ?? endedAt).TotalSeconds;
            times.Reading += readAt is { } readStart ? Stopwatch.GetElapsedTime(readStart, endedAt).TotalSeconds : 0;
            if (!ended)
            {
                _log.Info($"{game.Name}: load head prewarm interrupted ({(ct.IsCancellationRequested ? "caller cancelled" : "exception")}) after {Took()}"); // 서비스 종료 등 호출자 취소는 예외로 빠져나가므로 여기서 남김
            }

            FinishYield(readYield, game.Name);
            (_streaming, _streamStop) = (null, null);
            LogYield(game.Name, 0, lease);
        }
    }
}
