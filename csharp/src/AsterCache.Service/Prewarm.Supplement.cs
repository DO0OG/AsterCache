using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 의 보충 예열: 끊기거나 부분만 된 핫 집합을 이어 읽고, 무진전이면 재시도를 미룸.
public sealed partial class Prewarmer
{
    /// 시작 예열이 상한으로 일부만 읽은 게임이 남아 있는가 (재예열 점검이 "표본이 따뜻함" 으로 건너뛰지 않게).
    public bool SupplementPending(string game)
    {
        lock (_gate)
        {
            return _partial.ContainsKey(game);
        }
    }

    private void NotePartial(GameSettings game, long warmedMib, long hotMib, long bytes)
    {
        string? inputs = PrewarmRun.InputStamp(game);
        lock (_gate)
        {
            _supplementNothingNew.Remove(game.Name);
            if (_memoryPartial.ContainsKey(game.Name))
            {
                return; // 메모리 상한이 더 작게 읽어 이미 실제 읽은 양으로 기록함
            }

            _partial[game.Name] = (warmedMib, hotMib);
            _resume.Remove(game.Name); // 부분 상태를 새로 정함: 옛 이어 읽기 위치는 낡음
            _supplementReadProgress[game.Name] = new(bytes, inputs, game);
            _supplementRetry.Remove(game.Name);
        }

        _log.Info($"{game.Name}: startup prewarm read {warmedMib} of {hotMib} MiB, the rest is read when the drive is idle");
    }

    // 나머지까지 읽어 부분 예열이 끝남: 메모리 상태와 상태 파일의 부분 표지를 함께 지웁니다.
    private void ClearPartial(string game, long hotMib)
    {
        lock (_gate)
        {
            _partial.Remove(game);
            _memoryPartial.Remove(game);
            _memoryRunning.Remove(game);
            _resume.Remove(game);
            _supplementNothingNew.Remove(game);
            _supplementReadProgress.Remove(game);
            _supplementRetry.Remove(game);
            _memoryStopAt.Remove(game);
        }

        PatchStatus(game, hotMib, hotMib);
    }

    private void CompletePendingWarmSupplement(GameSettings game, StreamStats stats)
    {
        DateTimeOffset at = _now();
        lock (_gate)
        {
            if (!_pendingWarm.ContainsKey(game.Name))
            {
                return;
            }

            _last[game.Name] = at;
        }

        double mbps = stats.Seconds > 0 ? stats.Bytes / 1048576.0 / stats.Seconds : 0;
        Record(game, at, new PrewarmResult(game.Name, true, null, stats, at), mbps);
    }

    private long SupplementProgressBytes(GameSettings game, (long WarmedMib, long HotMib) partial, string? inputs)
    {
        lock (_gate)
        {
            if (_supplementReadProgress.TryGetValue(game.Name, out SupplementReadProgress? progress)
                && progress.Settings == game && string.Equals(progress.Inputs, inputs, StringComparison.Ordinal))
            {
                return progress.Bytes;
            }

            _supplementReadProgress.Remove(game.Name);
            _supplementRetry.Remove(game.Name);
            long bytes = MibBytes(partial.WarmedMib);
            _supplementReadProgress[game.Name] = new(bytes, inputs, game);
            return bytes;
        }
    }

    private bool SupplementDeferred(GameSettings game, long capMib, long hotMib, long progressBytes, string? inputs)
    {
        DateTimeOffset now = _now();
        lock (_gate)
        {
            if (!_supplementRetry.TryGetValue(game.Name, out SupplementRetry? retry))
            {
                return false;
            }

            if (retry.Settings != game || retry.ProgressBytes != progressBytes || !string.Equals(retry.Inputs, inputs, StringComparison.Ordinal))
            {
                _supplementRetry.Remove(game.Name);
                return false;
            }

            if (now < retry.RetryAt)
            {
                bool memoryGrew = capMib >= retry.CapMib + MinPartialMib;
                bool fullPathBecameAvailable = retry.CapMib < hotMib && capMib >= hotMib;
                if (retry.EarlyRetryUsed || (!memoryGrew && !fullPathBecameAvailable))
                {
                    return true;
                }

                _supplementRetry[game.Name] = retry with { CapMib = capMib, EarlyRetryUsed = true };
                return false;
            }

            _supplementRetry[game.Name] = retry with { RetryAt = now + PendingWarmRetryDelay(), CapMib = capMib, EarlyRetryUsed = false };
            return false;
        }
    }

    private void DeferSupplement(GameSettings game, long capMib, long progressBytes, string? inputs, StreamStats stats, bool planEnded, long planBytes, string? reason = null)
    {
        DateTimeOffset now = _now();
        DateTimeOffset retryAt = now + PendingWarmRetryDelay();
        bool earlyRetryUsed = false;
        bool log;
        lock (_gate)
        {
            if (_supplementReadProgress.TryGetValue(game.Name, out SupplementReadProgress? progress)
                && progress.Settings == game && string.Equals(progress.Inputs, inputs, StringComparison.Ordinal))
            {
                progressBytes = Math.Max(progressBytes, progress.Bytes);
            }
            else if (_partial.TryGetValue(game.Name, out (long WarmedMib, long HotMib) partial))
            {
                progressBytes = Math.Max(progressBytes, MibBytes(partial.WarmedMib));
            }

            bool sameProgress = _supplementRetry.TryGetValue(game.Name, out SupplementRetry? previous)
                && previous.Settings == game && previous.ProgressBytes == progressBytes
                && string.Equals(previous.Inputs, inputs, StringComparison.Ordinal);
            bool same = sameProgress && previous!.PlanExhausted == planEnded;
            if (sameProgress && previous is { } current && now < current.RetryAt)
            {
                retryAt = current.RetryAt;
                earlyRetryUsed = current.EarlyRetryUsed;
            }

            log = !same || previous?.Logged != true;
            _supplementRetry[game.Name] = new(retryAt, capMib, progressBytes, inputs, game, planEnded, earlyRetryUsed, true);
        }

        if (!log)
        {
            return;
        }

        if (reason is not null)
        {
            _log.Info($"{game.Name}: supplement prewarm deferred ({reason}; retry after {retryAt:O})");
        }
        else if (planEnded)
        {
            _log.Info($"{game.Name}: supplement prewarm deferred (read list ended at {MibText(planBytes)} of {capMib} MiB planned, {stats.Errors} errors; retry after {retryAt:O})");
        }
        else
        {
            _log.Info($"{game.Name}: supplement prewarm deferred (no progress: {MibText(stats.Bytes)} accepted of {MibText(stats.RequestedBytes)} requested, {stats.Errors} errors; retry after {retryAt:O})");
        }
    }

    private static bool SupplementPlanEnded(long? listBytes, StreamStats stats, long skipBytes, long capMib)
    {
        if (stats.Errors != 0 || listBytes is null && stats.RequestedBytes <= 0)
        {
            return false;
        }

        long bytes = listBytes ?? stats.RequestedBytes;
        long total = bytes > long.MaxValue - skipBytes ? long.MaxValue : skipBytes + bytes;
        return total < MibBytes(capMib);
    }

    private static bool SupplementFullyCovered(StreamStats stats, long skipBytes, long capMib, long? listBytes = null, bool fullReadPath = false)
    {
        if (stats.Errors != 0)
        {
            return false;
        }

        if (fullReadPath)
        {
            long fullList = listBytes ?? stats.RequestedBytes;
            if (fullList <= 0)
            {
                fullList = stats.Bytes;
            }

            return fullList > 0;
        }

        long requestedTail = listBytes ?? stats.RequestedBytes;
        if (listBytes is null && stats.RequestedBytes <= 0)
        {
            requestedTail = stats.Bytes; // 구형 스트리머 통계에 요청량이 없으면 수락량으로만 완료를 확인합니다.
        }

        long requested = requestedTail > long.MaxValue - skipBytes ? long.MaxValue : skipBytes + requestedTail;
        long accepted = stats.Bytes > long.MaxValue - skipBytes ? long.MaxValue : skipBytes + stats.Bytes;
        long target = MibBytes(capMib);
        return requested >= target && accepted >= target;
    }

    private static long MibBytes(long mib) => mib <= 0 ? 0 : mib > long.MaxValue >> 20 ? long.MaxValue : mib << 20;

    private static string MibText(long bytes) => $"{bytes / 1048576.0:F2} MiB";

    // 보충이 끊기거나 앞부분만 읽고 끝났을 때 읽은 양을 기억합니다 (줄어들지 않음): 상태 표시와 다음 보충의 시작점. 보충 대기 중인 게임만.
    private void NoteProgress(GameSettings game, string? inputs, long warmedMib, long hotMib, long bytes)
    {
        bool changed = false;
        lock (_gate)
        {
            if (!_partial.TryGetValue(game.Name, out (long WarmedMib, long HotMib) cur))
            {
                return;
            }

            long previousBytes = _supplementReadProgress.TryGetValue(game.Name, out SupplementReadProgress? previous)
                && previous.Settings == game && string.Equals(previous.Inputs, inputs, StringComparison.Ordinal)
                ? previous.Bytes
                : MibBytes(cur.WarmedMib);
            if (bytes <= previousBytes)
            {
                return;
            }

            _supplementReadProgress[game.Name] = new(bytes, inputs, game);
            _supplementRetry.Remove(game.Name);
            _supplementNothingNew.Remove(game.Name);
            if (warmedMib > cur.WarmedMib)
            {
                _partial[game.Name] = (warmedMib, hotMib);
                if (_memoryPartial.ContainsKey(game.Name))
                {
                    _memoryPartial[game.Name] = (warmedMib, hotMib); // 재예열 점검은 읽은 앞부분에서만 표본을 고름
                }

                changed = true;
            }
        }

        if (!changed)
        {
            return;
        }

        lock (_run)
        {
            lock (_gate)
            {
                if (_status.TryGetValue(game.Name, out object? entry) && entry is JObject o)
                {
                    o["warmed_mib"] = warmedMib;
                    o["hot_mib"] = hotMib;
                }
            }

            FlushStatus();
        }
    }

    // 이미 읽은 앞부분 (warmedMib) 의 표본이 식었는가: 식었으면 보충이 앞부분부터 다시 읽음. 읽지 못하면 식지 않은 것으로 봄.
    private bool FrontCold(GameSettings game, long warmedMib, CancellationToken ct, out bool refused)
    {
        refused = false;
        try
        {
            GameSettings front = game with { MaxStreamMib = (int)Math.Min(warmedMib, int.MaxValue), MaxStreamSet = true };
            return GuardedProbe(game, "supplement front sample", ct, () => (_env.ProbePartialReadMs ?? _env.ProbeReadMs)(front),
                (_env.ProbePartialReadMsChecked ?? _env.ProbeReadMsChecked) is { } checkedProbe ? canIssue => checkedProbe(front, canIssue) : null, out refused) is { Cold: true };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            return false;
        }
    }

    // 끊긴 보충을 이어 읽을 지점: 기억한 윈도가 지금 예산 (cap) 안이면 그 윈도를 끝내고 (읽을 목록이 같아야 앞 바이트를 건너뛸 수 있음), 그 앞부분 표본이 따뜻할 때만 건너뜀.
    // 식었거나 확인할 수 없거나 윈도가 예산을 넘으면 기억을 버리고 cap 으로 처음부터. full: 전체 핫 집합이 들어가는 경로라 cap (전체 크기) 와 같은 윈도의 기억만 씀.
    // refused: 디스크 대여가 거절·취소돼 표본을 읽지 못함 (기억은 그대로 두고 호출자가 조용히 멈춤).
    private (long WindowMib, long SkipBytes) ResumePoint(GameSettings game, long cap, CancellationToken ct, out bool refused, bool full = false)
    {
        refused = false;
        (long WindowMib, long Bytes) r;
        lock (_gate)
        {
            if (!_resume.TryGetValue(game.Name, out r))
            {
                return (cap, 0);
            }
        }

        ProbeResult? probe = null;
        if ((full ? r.WindowMib == cap : r.WindowMib <= cap) && _env.ProbeDone is { } probeDone)
        {
            try
            {
                GameSettings window = game with { MaxStreamMib = (int)r.WindowMib, MaxStreamSet = true };
                probe = GuardedProbe(game, "supplement resume sample", ct, () => probeDone(window, r.Bytes),
                    _env.ProbeDoneChecked is { } checkedDone ? canIssue => checkedDone(window, r.Bytes, canIssue) : null, out refused);
                if (refused)
                {
                    return (cap, 0);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
            {
                // 읽지 못하면 이어 읽지 않고 처음부터
            }
        }

        if (probe is { Samples: > 0, Cold: false })
        {
            return r;
        }

        lock (_gate)
        {
            _resume.Remove(game.Name);
        }

        return (cap, 0);
    }

    // 보충 예열 대기 중인 게임이 IdleNeeded 동안 한가했으면 남은 구간을 읽습니다 (게임 미실행·재배치 아님·같은 디스크 게임 미실행·디스크 처리량 낮음).
    private void SupplementTick(CancellationToken ct)
    {
        string[] pending;
        lock (_gate)
        {
            pending = [.. _partial.Keys];
        }

        HashSet<string> pendingPartial = new(StringComparer.OrdinalIgnoreCase);

        foreach (string name in pending)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            if (_games.FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)) is not { } g)
            {
                lock (_gate)
                {
                    _partial.Remove(name);
                    _idleSince.Remove(name);
                    _resume.Remove(name);
                    _supplementNothingNew.Remove(name);
                    _supplementReadProgress.Remove(name);
                    _supplementRetry.Remove(name);
                }

                continue;
            }

            if (!HeavyWorkAllowed(g) || PolicyBlocksPrewarm(g))
            {
                continue;
            }

            if (HasPendingWarm(name))
            {
                pendingPartial.Add(name);
            }

            SupplementWhenIdle(g, ct);
        }

        RetryPendingWarmTick(ct, pendingPartial);
    }

    /// 보충 예열 대기 게임이 IdleNeeded 동안 연속으로 한가했을 때만 Supplement 를 부릅니다 (SupplementTick 과 재예열 점검이 같은 판정을 씀). 유휴가 막 시작됐거나 아직 모자라면 읽지 않고 false.
    public bool SupplementWhenIdle(GameSettings g, CancellationToken ct)
    {
        if (!HeavyWorkAllowed(g) || PolicyBlocksPrewarm(g))
        {
            return false;
        }

        long? allocatedMib = null;
        if (WarmBudget() is { } budget)
        {
            if (!budget.In.Contains(g.Name))
            {
                LogSkipped(g.Name, "warm budget", $"{g.Name}: supplement prewarm skipped ({WarmBudgetSkipDetail(budget.CapMib, budget.UsedMib)})");
                return false;
            }

            allocatedMib = budget.AllocMib.GetValueOrDefault(g.Name) is > 0 and long mib ? mib : null;
        }

        bool idle = !DriveHasRunningGame(g) && !_games.Any(x => Relocating(x.Name)) && _env.DiskBytesPerSecond(g) is { } bps && bps < DiskActivity.BusyBytesPerSecond;
        DateTimeOffset now = _now();
        DateTimeOffset since;
        lock (_gate)
        {
            if (!idle)
            {
                _idleSince.Remove(g.Name);
                return false;
            }

            if (!_idleSince.TryGetValue(g.Name, out since))
            {
                _idleSince[g.Name] = since = now;
            }
        }

        if (now - since < IdleNeeded)
        {
            return false;
        }

        bool done = Supplement(g, ct, allocatedMib);
        lock (_gate)
        {
            _idleSince.Remove(g.Name); // 취소돼 남았으면 다음에 다시 IdleNeeded 를 셈
        }

        return done;
    }

    /// 남은 핫 집합 구간을 읽는 보충 예열 한 번 (한 번 끝나면 다시 하지 않음). 다른 예열이 돌고 있거나, 게임·재배치가 있거나, RAM 예산 (예열 예산, 가용 RAM - 여유) 을 넘으면 읽지 않고 false.
    /// 읽는 동안 게임 시작·재배치 시작은 StreamHot 의 기존 규칙으로 멈춥니다 (멈추면 남겨 두고 다음 유휴 때 다시).
    internal bool Supplement(GameSettings game, CancellationToken ct, long? allocatedMib = null)
    {
        (long WarmedMib, long HotMib) p;
        lock (_gate)
        {
            if (!_partial.TryGetValue(game.Name, out p))
            {
                return false;
            }
        }

        if (!Monitor.TryEnter(_run))
        {
            return false;
        }

        try
        {
            using IDisposable work = TrackGameWork(game.Name);
            if (ct.IsCancellationRequested || !HeavyWorkAllowed(game) || PolicyBlocksPrewarm(game)
                || DriveHasRunningGame(game) || _games.Any(x => Relocating(x.Name)) || Deferred(game) || MemoryRetryTooSoon(game.Name))
            {
                return false;
            }

            long avail = _env.AvailableRamMib();
            long physCap = PhysCapMib();
            long budgetCap = allocatedMib is { } allocated ? Math.Min(p.HotMib, allocated) : p.HotMib;
            bool needsPartial = budgetCap < p.HotMib || p.HotMib > physCap || p.HotMib + game.Prewarm.MinFreeRamMib > avail;
            long? partialCap = null;
            if (needsPartial)
            {
                partialCap = PartialCapMib(game, p.HotMib, WindowAvailableMib(avail), physCap, budgetCap);
                if (partialCap is not { } cap)
                {
                    LogSkipped(game.Name, "supplement memory budget", $"{game.Name}: supplement prewarm skipped (hot set {p.HotMib} MiB exceeds the budget: min(cache budget {physCap} MiB, available {avail} MiB - reserve {game.Prewarm.MinFreeRamMib} MiB), and less than {MinPartialMib} MiB fits)");
                    return false;
                }
            }

            long plannedCapMib = partialCap ?? p.HotMib;
            string? inputStamp = PrewarmRun.InputStamp(game);
            long progressBefore = SupplementProgressBytes(game, p, inputStamp);
            if (SupplementDeferred(game, plannedCapMib, p.HotMib, progressBefore, inputStamp))
            {
                return false;
            }

            GameSettings warm = game;
            long? windowMib = null; // 전체가 예산에 안 들어갈 때 읽을 앞부분 (일반 예열의 부분 예산과 같은 식)
            long skipBytes = 0; // 끊긴 보충이 그 윈도에서 이미 읽은 바이트 (ResumePoint)
            bool refused = false; // 앞부분·이어 읽을 지점 표본이 디스크 대여 거절로 읽히지 않음: 이유를 남기지 않고 다음 유휴에 다시 (기억은 그대로)
            if (needsPartial)
            {
                long cap = partialCap.GetValueOrDefault();

                if (SupplementCoveredByCap(game, p, cap, ct, out refused))
                {
                    return false;
                }

                if (!HeavyWorkAllowed(game))
                {
                    return false;
                }

                (long resumed, skipBytes) = ResumePoint(game, cap, ct, out refused);
                if (refused)
                {
                    return false;
                }

                (windowMib, warm) = (resumed, game with { MaxStreamMib = (int)resumed, MaxStreamSet = true });
            }
            else if (!HeavyWorkAllowed(game))
            {
                return false;
            }
            else if (ResumePoint(game, p.HotMib, ct, out refused, full: true).SkipBytes is > 0 and long fullSkip)
            {
                // 전체가 들어가는 경로도 끊긴 위치에서 이어 읽음: 틈 메우기는 스케줄 앞쪽을 안정적으로 만들지 못하므로 이어 읽을 때는 틈 없이 같은 스케줄로 읽고, 끝나면 평소처럼 부분 상태를 지움.
                (skipBytes, warm) = (fullSkip, game with { MaxStreamMib = (int)Math.Min(p.HotMib, int.MaxValue), MaxStreamSet = true });
            }

            if (refused)
            {
                return false;
            }

            if (!HeavyWorkAllowed(game))
            {
                return false;
            }

            using PrewarmRun run = PrewarmRun.Begin();
            long? finalReadBytes = ReadSetBytes(warm, partial: windowMib is not null || skipBytes > 0);
            avail = _env.AvailableRamMib();
            if (finalReadBytes is > 0 and long finalReadSize)
            {
                long finalReadMib = (finalReadSize + (1 << 20) - 1) >> 20;
                long reserveMib = Math.Max(game.Prewarm.MinFreeRamMib, GapFillReserveMib);
                long allowedMib = Math.Min(Math.Min(physCap, allocatedMib ?? long.MaxValue), WindowAvailableMib(avail) - reserveMib);
                if (finalReadMib > allowedMib)
                {
                    if (PartialCapMib(game, finalReadMib, WindowAvailableMib(avail), physCap, budgetCap) is { } capMib)
                    {
                        if (SupplementCoveredByCap(game, p, capMib, ct, out _))
                        {
                            return false; // 줄인 뒤의 상한이 이미 읽은 양 이하: 앞부분을 다시 읽지 않고 앞쪽 "더 읽을 것 없음" 과 같은 경로로 돌아감
                        }

                        bool changedWindow = windowMib is null || windowMib.Value != capMib;
                        windowMib = capMib;
                        warm = game with { MaxStreamMib = (int)capMib, MaxStreamSet = true };
                        if (changedWindow)
                        {
                            skipBytes = 0; // 창이나 틈 메우기 방식이 바뀌면 이전 스케줄의 앞부분을 건너뛰지 않음
                            lock (_gate)
                            {
                                _resume.Remove(game.Name);
                            }
                        }

                        _log.Info($"{game.Name}: supplement memory budget fell after planning; reading the first {capMib} MiB of {p.HotMib} MiB");
                    }
                    else
                    {
                        string reason = $"low memory (available {avail} MiB, reserve {reserveMib} MiB, final set {finalReadMib} MiB; less than {MinPartialMib} MiB fits)";
                        LogSkipped(game.Name, "supplement " + reason, $"{game.Name}: supplement prewarm skipped ({reason})"); // 일반 예열의 low memory 와 다른 억제 키
                        DeferSupplement(game, plannedCapMib, progressBefore, inputStamp, new StreamStats(0, 0, 0, 0), planEnded: false, 0, reason); // 표본 읽기 뒤의 건너뜀이 유휴마다 되풀이되지 않게 재시도 기한을 둠
                        return false;
                    }
                }
            }

            long readCapMib = windowMib ?? p.HotMib;
            long? zoneBytes = _env.ZoneBytes(game);
            (StreamStats Stats, bool Complete, string? CancelReason, bool MemoryStop)? hot;
            long? tailTotal = null; // 이번에 읽을 목록의 길이 (건너뛴 앞부분 제외): 이어 읽을 위치를 스케줄 길이로 제한하고 끝까지 읽었는지 앎
            try
            {
                if (!HeavyWorkAllowed(game))
                {
                    return false;
                }

                lock (_gate)
                {
                    _memoryStopAt.Remove(game.Name); // 다시 읽기 시작함: 이번에 메모리로 멈추면 그때 다시 기억
                }

                _log.Info($"{game.Name}: supplement prewarm of the rest of the hot set ({p.WarmedMib} of {p.HotMib} MiB read{(windowMib is { } w ? $", reading the first {w} MiB" : string.Empty)}{(skipBytes > 0 ? $", continuing after {skipBytes >> 20} MiB" : string.Empty)})");
                hot = StreamHot(warm, ct, zoneBytes, avail, windowMib is not null || skipBytes > 0, skipBytes: skipBytes, onTotal: total => tailTotal = total, settle: stats =>
                {
                    if (windowMib is { } window)
                    {
                        NoteProgress(game, inputStamp, ActualMib(stats, window, skipBytes), p.HotMib, skipBytes + stats.Bytes); // 앞부분만 읽음: 나머지는 다음 보충 때
                        lock (_gate)
                        {
                            _resume.Remove(game.Name); // 윈도를 끝까지 읽음
                        }
                    }
                    else if (SupplementFullyCovered(stats, skipBytes, p.HotMib, tailTotal, fullReadPath: windowMib is null))
                    {
                        ClearPartial(game.Name, p.HotMib); // 완료 알림보다 먼저 부분 상태를 지움
                    }
                    else
                    {
                        NoteProgress(game, inputStamp, ActualMib(stats, p.HotMib, skipBytes), p.HotMib, skipBytes + stats.Bytes);
                    }
                }, allowReorder: false, roomMib: allocatedMib ?? long.MaxValue);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
            {
                _log.Warn($"{game.Name}: supplement prewarm failed: {e.GetType().Name}");
                lock (_gate)
                {
                    _partial.Remove(game.Name); // 실패한 읽기는 되풀이하지 않음 (유휴마다 같은 실패를 반복하지 않게)
                    _resume.Remove(game.Name);
                    _supplementNothingNew.Remove(game.Name);
                    _supplementReadProgress.Remove(game.Name);
                    _supplementRetry.Remove(game.Name);
                }

                _log.Warn($"{game.Name}: supplement given up after the failure");
                return false;
            }

            if (hot is not { } r)
            {
                LogSkipped(game.Name, RelocationRunning, $"{game.Name}: supplement prewarm skipped ({RelocationRunning})");
                return false;
            }

            if (!r.Complete)
            {
                long progressBytes = skipBytes + r.Stats.Bytes;
                NoteProgress(game, inputStamp, ActualMib(r.Stats, windowMib ?? p.HotMib, skipBytes), p.HotMib, progressBytes); // 끊겨도 읽은 양은 버리지 않음: 다음 보충이 이어서 읽음
                bool reordered = _streamer.WasPrewarmReordered(game);
                // 위치는 틈을 뺀 스케줄 기준 (틈 메운 순서와 달라도 모자라게 셈: 덜 건너뛰는 쪽). 오류가 있으면 앞이 다 읽혔다고 못 하니 버리고, 목록 끝까지 읽은 뒤의 취소 (갱신 읽기 도중) 는 창 완료.
                long pos = skipBytes + Math.Max(0, r.Stats.Bytes - r.Stats.GapBytes);
                bool listAtEnd = r.Stats.Errors == 0 && tailTotal is { } t && pos >= skipBytes + t - r.Stats.GapBytes;
                bool planEndedAtEnd = listAtEnd && windowMib is not null && SupplementPlanEnded(tailTotal, r.Stats, skipBytes, readCapMib);
                bool atEnd = listAtEnd && !planEndedAtEnd && (windowMib is not null || SupplementFullyCovered(r.Stats, skipBytes, p.HotMib, tailTotal, fullReadPath: true));
                lock (_gate)
                {
                    if (r.Stats.Errors > 0 || atEnd || planEndedAtEnd || r.Stats.GapBytes > 0 || reordered) // 순서를 바꿨거나 틈 메우기가 섞이면 위치를 믿을 수 없어 다음에 처음부터
                    {
                        _resume.Remove(game.Name);
                    }
                    else if (pos > 0)
                    {
                        _resume[game.Name] = (windowMib ?? p.HotMib, pos); // 같은 윈도 (전체 경로면 전체 크기) 의 이 위치부터 이어 읽음
                    }
                }

                if (atEnd && windowMib is null)
                {
                    ClearPartial(game.Name, p.HotMib); // 전체를 끝까지 읽음
                    CompletePendingWarmSupplement(game, r.Stats);
                }
                else if (planEndedAtEnd)
                {
                    DeferSupplement(game, plannedCapMib, progressBytes, inputStamp, r.Stats, planEnded: true, skipBytes + (tailTotal ?? r.Stats.RequestedBytes));
                }
                else if (progressBytes <= progressBefore
                    && r.CancelReason is not ("game started" or "another game started on the same disk" or "disk read lease revoked" or "disk read lease refused" or "caller cancelled"))
                {
                    // 진전 없이 끊긴 보충도 유휴마다 같은 앞부분을 다시 읽지 않음 (게임 시작·디스크 양보로 끊긴 것은 제외: 게임이 끝나면 바로 이어야 함)
                    DeferSupplement(game, plannedCapMib, progressBefore, inputStamp, r.Stats, planEnded: false, skipBytes + (tailTotal ?? r.Stats.RequestedBytes));
                }

                string reason = r.CancelReason ?? "cancelled externally";
                if (r.MemoryStop)
                {
                    lock (_gate)
                    {
                        _memoryStopAt[game.Name] = _now(); // 읽은 데까지는 NoteProgress 가 이미 남김
                    }
                }

                LogSkipped(game.Name, "supplement cancelled " + reason, () => $"{game.Name}: supplement prewarm cancelled after {r.Stats.Bytes >> 20} MiB ({reason})");
                return false;
            }

            if (r.Stats.Bytes <= 0 && skipBytes == 0
                && (windowMib is not null || !SupplementFullyCovered(r.Stats, skipBytes, p.HotMib, tailTotal, fullReadPath: true)))
            {
                bool planEnded = SupplementPlanEnded(tailTotal, r.Stats, skipBytes, readCapMib);
                long listBytes = tailTotal ?? r.Stats.RequestedBytes;
                DeferSupplement(game, plannedCapMib, progressBefore, inputStamp, r.Stats, planEnded, skipBytes + listBytes);
                _log.Info($"{game.Name}: supplement prewarm read nothing, kept as partial");
                return false;
            }

            long progressAfter = skipBytes + r.Stats.Bytes;
            bool madeProgress = progressAfter > progressBefore;
            bool requestListEnded = windowMib is not null && SupplementPlanEnded(tailTotal, r.Stats, skipBytes, readCapMib);
            long plannedListBytes = tailTotal ?? r.Stats.RequestedBytes;
            if (requestListEnded)
            {
                DeferSupplement(game, plannedCapMib, Math.Max(progressBefore, progressAfter), inputStamp, r.Stats, planEnded: true, skipBytes + plannedListBytes);
                _log.Info($"{game.Name}: supplement prewarm read the first {MibText(progressAfter)} of {p.HotMib} MiB in {r.Stats.Seconds:F2} s ({MibText(r.Stats.Bytes)} accepted of {MibText(r.Stats.RequestedBytes)} requested, {r.Stats.Errors} errors), the plan lists {MibText(skipBytes + plannedListBytes)} within {readCapMib} MiB");
                return true;
            }

            if (windowMib is not null)
            {
                if (!madeProgress)
                {
                    DeferSupplement(game, plannedCapMib, progressBefore, inputStamp, r.Stats, planEnded: false, skipBytes + plannedListBytes);
                }

                _log.Info($"{game.Name}: supplement prewarm read the first {MibText(progressAfter)} of {p.HotMib} MiB in {r.Stats.Seconds:F2} s ({MibText(r.Stats.Bytes)} accepted of {MibText(r.Stats.RequestedBytes)} requested, {r.Stats.Errors} errors), the partial window remains pending");
                return true;
            }

            if (!SupplementFullyCovered(r.Stats, skipBytes, p.HotMib, tailTotal, fullReadPath: true))
            {
                if (!madeProgress)
                {
                    DeferSupplement(game, plannedCapMib, progressBefore, inputStamp, r.Stats, planEnded: false, skipBytes + plannedListBytes);
                }

                _log.Info($"{game.Name}: supplement prewarm read the first {MibText(progressAfter)} of {p.HotMib} MiB in {r.Stats.Seconds:F2} s ({MibText(r.Stats.Bytes)} accepted of {MibText(r.Stats.RequestedBytes)} requested, {r.Stats.Errors} errors), the hot set remains partial");
                return true;
            }

            ClearPartial(game.Name, p.HotMib);
            CompletePendingWarmSupplement(game, r.Stats);
            _log.Info($"{game.Name}: supplement prewarm done: {MibText(r.Stats.Bytes)} accepted of {MibText(r.Stats.RequestedBytes)} requested, {r.Stats.GapBytes / 1048576.0:F2} MiB gaps filled in {r.Stats.Seconds:F2} s, {r.Stats.Errors} errors");
            return true;
        }
        finally
        {
            Monitor.Exit(_run);
        }
    }

    // cap 이 이미 읽은 양 (p.WarmedMib) 이하이면 더 읽을 것이 없는 경우를 처리하고 true: 메모리 부분 예열은 바로 대기에서 빼고, 아니면 앞부분 표본이 식지 않았을 때만 같은 처리 (식은 앞부분은 재예열 점검이 다시 읽음).
    // refused: 디스크 대여가 거절·취소돼 표본을 읽지 못함 (기억은 그대로 두고 호출자가 조용히 멈춤).
    private bool SupplementCoveredByCap(GameSettings game, (long WarmedMib, long HotMib) p, long cap, CancellationToken ct, out bool refused)
    {
        refused = false;
        if (cap > p.WarmedMib)
        {
            return false;
        }

        if (MemoryPartialPending(game.Name))
        {
            LogSupplementNothingNew(game.Name, cap, p.WarmedMib, p.HotMib);
            if (!HasMemoryStop(game.Name))
            {
                DropPendingWarm(game.Name); // 메모리로 멈춘 게임은 대기를 남겨 둠: 가용 메모리가 읽은 양 + 여유 + 예비량으로 회복되면 다음 유휴 때 창 계산이 이어 읽음
            }

            return true;
        }

        if (HeavyWorkAllowed(game) && FrontCold(game, p.WarmedMib, ct, out refused))
        {
            return false;
        }

        if (!refused && HeavyWorkAllowed(game))
        {
            LogSupplementNothingNew(game.Name, cap, p.WarmedMib, p.HotMib);
            DropPendingWarm(game.Name); // 들어가는 만큼은 이미 읽었으니 더 읽을 것이 없고, 남기면 회복 보류만 계속 걸림
        }

        return true;
    }
}
