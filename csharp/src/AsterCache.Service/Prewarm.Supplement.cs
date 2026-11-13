using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
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

    // 끊긴 보충을 이어 읽을 지점: 기억한 윈도를 그대로 쓰고 (읽을 목록이 같아야 앞 바이트를 건너뛸 수 있음: 윈도를 줄이면 접근 순서 뒤쪽의 물리 순서가 달라짐), 그 앞부분 표본이 따뜻할 때만 건너뜀.
    // 윈도가 지금 예산 (cap) 보다 커도 버리지 않음: 호출 쪽이 읽을 총량을 cap 까지로 막음. 식었거나 확인할 수 없거나 읽은 곳이 cap 이상이면 기억을 버리고 cap 으로 처음부터. 전체 핫 집합이 들어가는 경로 (cap 이 전체 크기) 에서도 더 작은 윈도의 기억을 그대로 이어 읽음 (윈도가 바뀌면 목록이 달라 앞 바이트를 건너뛸 수 없으므로 그 윈도를 먼저 끝냄).
    // refused: 디스크 대여가 거절·취소돼 표본을 읽지 못함 (기억은 그대로 두고 호출자가 조용히 멈춤).
    private (long WindowMib, long SkipBytes) ResumePoint(GameSettings game, long cap, CancellationToken ct, out bool refused)
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
        if (r.Bytes < MibBytes(cap) && _env.ProbeDone is { } probeDone)
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

        if (!TryEnterRunIdle())
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

            long? zoneBytes = _env.ZoneBytes(game);
            p = RaiseHotMib(game.Name, p, zoneBytes); // 기억한 목록 크기가 현재 목록보다 작으면 올림: 첫 관문·창 상한·표시가 현재 목록을 따름
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
            long? limitMib = null; // 건너뛴 앞부분을 포함해 읽을 총량의 상한 (이어 읽는 윈도가 지금 예산보다 클 때)
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

                if (cap > p.WarmedMib && !PrewarmPolicy.SupplementHasRoom(cap, p.WarmedMib, p.HotMib, MinPartialMib))
                {
                    LogSupplementWaitsForMemory(game.Name, cap, p.WarmedMib, p.HotMib); // 들어가는 범위가 읽은 양보다 의미 있게 크지 않음: 디스크를 읽지 않고 메모리가 날 때까지 기다림
                    return false;
                }

                (long resumed, skipBytes) = ResumePoint(game, cap, ct, out refused);
                if (refused)
                {
                    return false;
                }

                (windowMib, warm) = (resumed, game with { MaxStreamMib = (int)resumed, MaxStreamSet = true });
                limitMib = resumed > cap ? cap : null; // 이어 읽는 윈도가 지금 예산보다 크면 읽은 곳 다음부터 cap 까지만 읽음
            }
            else if (!HeavyWorkAllowed(game))
            {
                return false;
            }
            else if (ResumePoint(game, p.HotMib, ct, out refused) is var (resumedWindow, fullSkip) && fullSkip > 0)
            {
                // 전체가 들어가는 경로도 끊긴 위치에서 이어 읽음: 틈 메우기는 스케줄 앞쪽을 안정적으로 만들지 못하므로 이어 읽을 때는 틈 없이 같은 스케줄로 읽고, 끝나면 평소처럼 부분 상태를 지움.
                // 기억한 윈도가 전체보다 작으면 (창 마무리 뒤 메모리가 늘어남) 그 윈도의 목록을 끝낸 뒤 다음 회가 전체를 읽음.
                skipBytes = fullSkip;
                long resumeMib = Math.Min(resumedWindow, p.HotMib);
                if (resumeMib < p.HotMib)
                {
                    windowMib = resumeMib;
                }

                warm = game with { MaxStreamMib = (int)Math.Min(resumeMib, int.MaxValue), MaxStreamSet = true };
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
            long finalReadMib = 0; // 아래 전체 경로가 메모리로 멈췄을 때 창으로 줄이는 데도 씀
            if (finalReadBytes is > 0 and long finalReadSize)
            {
                finalReadMib = (finalReadSize + (1 << 20) - 1) >> 20;
                if (limitMib is { } limit)
                {
                    finalReadMib = Math.Min(finalReadMib, limit);
                }

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
                        if (skipBytes > 0 && windowMib is { } resumedWindow && resumedWindow > capMib)
                        {
                            (limitMib, changedWindow) = (capMib, false); // 이어 읽는 윈도는 그대로 두고 읽을 총량만 줄임 (건너뛴 앞부분을 다시 읽지 않음)
                        }
                        else
                        {
                            windowMib = capMib;
                            warm = game with { MaxStreamMib = (int)capMib, MaxStreamSet = true };
                        }

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
                    _supplementNothingNew.Remove(game.Name); // 메모리를 기다리는 상태가 아님
                }

                _log.Info($"{game.Name}: supplement prewarm of the rest of the hot set ({p.WarmedMib} of {p.HotMib} MiB read{(windowMib is { } w ? $", reading the first {w} MiB" : string.Empty)}{(skipBytes > 0 ? $", continuing after {skipBytes >> 20} MiB" : string.Empty)})");
                bool fellBack = false; // 전체 경로가 메모리로 멈추면 창으로 줄여 한 번만 다시 읽음
                _yieldedSeconds = 0; // StreamHot 이 끝날 때마다 더함 (done 줄에 한 번 보임)
                while (true)
                {
                    hot = StreamHot(warm, ct, zoneBytes, avail, windowMib is not null || skipBytes > 0, skipBytes: skipBytes, onTotal: total => tailTotal = total, settle: (stats, shortFinish) =>
                    {
                        if (shortFinish)
                        {
                            // 메모리 한도로 읽은 데까지만 읽고 마무리: 읽은 양을 기록하고, 틈 없는 읽기면 다음 보충이 그 위치부터 이어 읽음 (같은 윈도라 위치가 스케줄 앞부분과 같음).
                            // 틈을 메운 계획은 위치를 믿을 수 없어 이어 읽을 위치를 남기지 않고, 읽은 양도 틈 양을 뺀 만큼만 셈 (확실히 읽었다고 할 수 있는 만큼)
                            long keptBytes = skipBytes + Math.Max(0, stats.Bytes - stats.GapBytes);
                            NoteProgress(game, inputStamp, Math.Min(windowMib ?? p.HotMib, (keptBytes + (1 << 20) - 1) >> 20), p.HotMib, keptBytes);
                            lock (_gate)
                            {
                                if (stats.GapBytes == 0)
                                {
                                    _resume[game.Name] = (windowMib ?? p.HotMib, keptBytes);
                                }
                                else
                                {
                                    _resume.Remove(game.Name);
                                }

                                if (_partial.TryGetValue(game.Name, out (long WarmedMib, long HotMib) kept))
                                {
                                    _memoryPartial[game.Name] = kept; // 시작 상한으로 들어온 게임도 이제 메모리 한도로 줄어든 상태: 다음에 범위가 읽은 곳 이하면 표본도 읽지 않고 끝냄
                                }
                            }
                        }
                        else if (windowMib is { } window)
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
                    }, allowReorder: false, roomMib: allocatedMib ?? long.MaxValue, limitBytes: limitMib is { } limitBytesMib ? MibBytes(limitBytesMib) : long.MaxValue, allowFinish: true);
                    if (fellBack || windowMib is not null || hot is not { Complete: false, MemoryStop: true } || !HeavyWorkAllowed(game)
                        || MemoryFallbackCapMib(game, finalReadMib, budgetCap, ct) is not { } fallbackCap
                        || fallbackCap <= Math.Max(p.WarmedMib, ActualMib(hot.Value.Stats, p.HotMib, skipBytes)))
                    {
                        break; // 기억한 양이나 이번 회에 (이어 읽은 뒤) 읽은 양 이하로만 들어가면 다시 읽지 않고 아래 기존 취소 경로 (읽은 양·이어 읽을 위치 기억, 재시도 대기)
                    }

                    fellBack = true;
                    (windowMib, readCapMib, plannedCapMib) = (fallbackCap, fallbackCap, fallbackCap);
                    warm = game with { MaxStreamMib = (int)fallbackCap, MaxStreamSet = true };
                    tailTotal = null; // 두 번째 읽기가 목록 길이를 알리기 전에 끝나도 첫 읽기의 값이 남지 않게
                    skipBytes = 0; // 창이 바뀌면 이전 스케줄의 앞부분을 건너뛰지 않음 (사전 검사의 창 변경과 같음)
                    lock (_gate)
                    {
                        _resume.Remove(game.Name);
                    }

                    _log.Info($"{game.Name}: supplement prewarm stopped by memory before the full list was read; reading the first {fallbackCap} MiB of {p.HotMib} MiB instead");
                }
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
                    && r.CancelReason is not ("game started" or "another game started on the same disk" or "disk read lease revoked" or "disk read lease refused" or "caller cancelled" or PreemptedByTrigger))
                {
                    // 진전 없이 끊긴 보충도 유휴마다 같은 앞부분을 다시 읽지 않음 (게임 시작·디스크 양보·트리거 선점으로 끊긴 것은 제외: 게임이 끝나거나 트리거 예열이 끝나면 바로 이어야 함)
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

            if (r.MemoryStop)
            {
                long keptBytes = skipBytes + Math.Max(0, r.Stats.Bytes - r.Stats.GapBytes); // settle 이 기록한 (틈을 뺀) 읽은 양
                _log.Info($"{game.Name}: supplement prewarm read the first {MibText(keptBytes)} of {p.HotMib} MiB, the rest {r.CancelReason}");
                if (keptBytes <= progressBefore)
                {
                    DeferSupplement(game, plannedCapMib, progressBefore, inputStamp, r.Stats, planEnded: false, skipBytes + (tailTotal ?? r.Stats.RequestedBytes)); // 읽은 범위가 늘지 않음: 바로 다시 시작하지 않음
                }

                return true;
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
            _log.Info($"{game.Name}: supplement prewarm done: {MibText(r.Stats.Bytes)} accepted of {MibText(r.Stats.RequestedBytes)} requested, {r.Stats.GapBytes / 1048576.0:F2} MiB gaps filled in {r.Stats.Seconds:F2} s, {r.Stats.Errors} errors{(_yieldedSeconds > 0 ? $", yielded {_yieldedSeconds:F1} s" : string.Empty)}");
            return true;
        }
        finally
        {
            Monitor.Exit(_run);
        }
    }

    // 보충이 지금 더 읽을 수 있는 것이 없어 (들어가는 범위가 읽은 양 이하이거나 의미 있게 크지 않음) 메모리를 기다리는 중이고, 읽은 범위가 한도로 마무리한 틈 없는 창 읽기의 앞부분 (이어 읽을 위치) 이면
    // 그 범위를 정확히 나타내는 사다리 대상 (공통 블록 + 창 목록의 앞 위치 바이트), 아니면 null. 재예열 점검의 첫 사다리가 부름: 매번 한도로 끝나는 게임은 읽기를 끝내는 회가 없어 사다리를 받지 못함.
    // 창 크기가 다르면 다시 계획한 목록이 읽은 목록과 달라 읽지 않은 구간을 만질 수 있으므로, 읽은 창 (이어 읽기 기억의 창) 의 목록을 앞에서 자릅니다. 틈을 메운 계획이 마무리한 경우는 위치가 없어 null (건너뜀).
    internal IReadOnlyList<ReadOp>? WaitingSupplementLadderReads(GameSettings game)
    {
        (long WindowMib, long Bytes) resume;
        lock (_gate)
        {
            if (!_partial.ContainsKey(game.Name) || !_supplementNothingNew.ContainsKey(game.Name) || !_resume.TryGetValue(game.Name, out resume))
            {
                return null;
            }
        }

        try
        {
            GameSettings window = game with { MaxStreamMib = (int)Math.Min(resume.WindowMib, int.MaxValue), MaxStreamSet = true };
            if (_streamer.PlannedPrewarmReads(window) is not { Count: > 0 } planned || PrefetchStreamer.Head(planned, resume.Bytes) is not { Count: > 0 } read)
            {
                return null;
            }

            IReadOnlyList<ReadOp> common = _env.CommonFirstOps(window, 0, 0).Ops;
            return common.Count == 0 ? read : [.. common, .. read];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException)
        {
            return null;
        }
    }

    // 들어가는 범위가 읽은 양보다 의미 있게 크지 않아 보충을 시작하지 않는 상태: 읽은 양이 바뀔 때만 한 줄 (들어가는 범위는 가용 메모리에 따라 계속 흔들리므로 키에 넣지 않음).
    private void LogSupplementWaitsForMemory(string game, long fitMib, long warmedMib, long hotMib)
    {
        var state = (Reason: "supplement nothing new", WarmedMib: warmedMib, HotMib: hotMib); // 더 읽을 새 구간이 없는 같은 상태이므로 두 로그가 번갈아 나오지 않게 키를 같이 씀
        lock (_gate)
        {
            if (_supplementNothingNew.TryGetValue(game, out var previous) && previous == state)
            {
                return;
            }

            _supplementNothingNew[game] = state;
        }

        _log.Info($"{game}: supplement waits for memory (read {warmedMib} MiB, room for {fitMib} MiB)");
    }

    // 기억한 목록 크기 (HotMib) 를 현재 목록 (zoneBytes, MiB 올림) 이 더 크면 올려 돌려주고 _partial·_memoryPartial 도 같게 고침. 늘어날 때만: 줄이면 끝난 것으로 잘못 칠 수 있음.
    private (long WarmedMib, long HotMib) RaiseHotMib(string game, (long WarmedMib, long HotMib) p, long? zoneBytes)
    {
        long listMib = zoneBytes is > 0 ? (zoneBytes.Value + (1 << 20) - 1) >> 20 : 0;
        if (listMib <= p.HotMib)
        {
            return p;
        }

        lock (_gate)
        {
            if (_partial.TryGetValue(game, out (long WarmedMib, long HotMib) cur) && cur.HotMib < listMib)
            {
                _partial[game] = (cur.WarmedMib, listMib);
            }

            if (_memoryPartial.TryGetValue(game, out (long WarmedMib, long HotMib) mem) && mem.HotMib < listMib)
            {
                _memoryPartial[game] = (mem.WarmedMib, listMib);
            }
        }

        return (p.WarmedMib, listMib);
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
