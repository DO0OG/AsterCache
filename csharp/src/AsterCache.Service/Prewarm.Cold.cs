using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Discovery;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 의 기록 없는 (프로필·통째) 예열과 그 상주 표본, 프로필 없음 기억.
public sealed partial class Prewarmer
{
    /// 기록 없이 예열한 게임의 재예열 점검 표본 (그 게임이 지금도 기록 없는 예열 대상이고 예열한 적이 있을 때만), 아니면 null.
    internal IReadOnlyList<(string Path, long Offset, int Bytes)>? ColdSamples(GameSettings game)
    {
        lock (_gate)
        {
            return IsCold(game) && _coldSamples.TryGetValue(game.Name, out IReadOnlyList<(string, long, int)>? s) ? s : null;
        }
    }

    /// 게임 시작 때 핫 집합이 RAM 에 있는 비율 (%): 표본 몇 곳 (CacheProbe.PickSamples 규모) 을 읽어 느리지 않은 곳의 비율. 잴 표본이 없으면 null.
    public int? HitPercent(GameSettings game)
    {
        ProbeResult? p = ColdSamples(game) is { } samples ? ProbeSamplesForGame(game, samples, "hit percent") : ProbeReadMsForGame(game, "hit percent");
        return p is { Samples: > 0 } ? (p.Samples - p.Slow) * 100 / p.Samples : null;
    }

    // 기록 (원시 트레이스·prefetch 기록) 이 없는 게임: 설치 폴더가 예산 이하면 통째로, 크면 엔진 프로필로. 쿨다운은 항상 지킵니다 (같은 폴더를 연달아 두 번 읽지 않게): 재예열도 쿨다운 안이면 다음 점검으로 미룸.
    // 순서: 드라이브 거부 (시스템 SSD·SSD·판정 불가) → 게임 실행 중 → 쿨다운 → 디스크 유휴 → RAM 예산 → 폴더 열거 (비싼 일은 마지막).
    private PrewarmResult RunCold(GameSettings game, string why, DateTimeOffset now, CancellationToken ct, long usedMib, bool trigger)
    {
        AutoSettings a = _auto;
        TimeSpan cooldown = TimeSpan.FromMinutes(game.Prewarm.CooldownMin);
        if (_env.ColdRefusal(game) is { } refused)
        {
            bool first;
            lock (_gate)
            {
                first = _refusedLogged.Add(game.Name);
            }

            if (first)
            {
                _log.Info($"{game.Name}: prewarm without a record skipped ({refused}) [{why}]");
            }

            return new PrewarmResult(game.Name, false, refused, null, now); // 상태 파일에도 쓰지 않음: 기록 없는 예열 대상이 아닌 드라이브
        }

        string? skip = null;
        if (DriveHasRunningGame(game))
        {
            skip = "game is running";
        }
        else if (LastWarmed(game.Name) is { } prev && now - prev < cooldown)
        {
            skip = $"cooldown ({Math.Ceiling((cooldown - (now - prev)).TotalMinutes)} min left)";
        }
        else if (_env.DiskBytesPerSecond(game) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond)
        {
            skip = "drive busy";
        }

        long held = usedMib + ColdMib(game.Name);
        long avail = _env.AvailableRamMib();
        long availBudget = avail - game.Prewarm.MinFreeRamMib - held; // MiB
        long ram = Math.Min(availBudget, PhysCapMib() - held); // min(예열 예산, 가용 RAM - 여유) - 이미 올린 양
        long fullMib = !a.FullPrewarm ? 0 : Math.Min(a.FullPrewarmMaxMib > 0 ? a.FullPrewarmMaxMib : DefaultFullPrewarmMib, availBudget);
        long profileMib = !a.EngineProfiles ? 0 : a.FullPrewarm && a.FullPrewarmMaxMib > 0 ? Math.Min(ram, fullMib) : ram;
        if (skip is null && Math.Max(fullMib, profileMib) <= 0)
        {
            skip = $"low memory (available {avail} MiB, reserve {game.Prewarm.MinFreeRamMib} MiB, already held {held} MiB)";
        }

        StreamStats? stats = null;
        string? label = null;
        string? cancelReason = null;
        List<(string Path, long Offset, int Bytes)>? samples = null;
        if (skip is null)
        {
            try
            {
                string key = Directory.GetLastWriteTimeUtc(game.OriginRoot).Ticks.ToString(); // shortcut: 설치 폴더 자신의 mtime 만 봄 (하위 변경은 놓칠 수 있음), 폴더 mtime 이 바뀌면 다시 열거
                ColdPlan? plan = ProfileMissed(game.Name, key) ? null : _env.ColdPlanner!(game, Math.Max(0, fullMib) << 20, Math.Max(0, profileMib) << 20, ct);
                if (plan is null)
                {
                    skip = ProfileSkip(game);
                }
                else if (plan.Skip == FolderWarm.NoEngineProfile)
                {
                    RememberProfileMiss(game.Name, key);
                    skip = ProfileSkip(game);
                }
                else if (plan.Skip is not null)
                {
                    skip = plan.Skip;
                }
                else
                {
                    ForgetProfileMiss(game.Name);
                    _log.Info($"{game.Name}: prewarm triggered by {TriggerCause(why)}");
                    if (ColdReadMarked(game, plan.Ops, ct, trigger) is not { } cold)
                    {
                        skip = RelocationRunning;
                    }
                    else
                    {
                        (stats, label, skip, cancelReason) = (cold.Stats, plan.Label, cold.Complete && !ct.IsCancellationRequested ? null : Cancelled, cold.CancelReason);
                        samples = CacheProbe.PickSamples(plan.Ops);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                skip = Cancelled;
                cancelReason = ct.IsCancellationRequested ? "caller cancelled" : "planning cancelled";
            }
            catch (DirectoryNotFoundException)
            {
                skip = "install folder not found";
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
            {
                _log.Error($"{game.Name}: prewarm failed ({why})", e);
                return Record(game, now, new PrewarmResult(game.Name, false, "failed: " + e.GetType().Name, null, now), 0);
            }
        }

        if (skip is not null || stats is null)
        {
            if (skip == Cancelled)
            {
                string reason = cancelReason ?? "cancelled externally";
                long afterMib = stats is { } cancelledStats ? cancelledStats.Bytes >> 20 : 0;
                LogSkipped(game.Name, "prewarm cancelled " + reason, $"{game.Name}: prewarm cancelled ({why}) after {afterMib} MiB ({reason})");
            }
            else
            {
                _log.Info($"{game.Name}: prewarm skipped ({skip}) [{why}]{(skip == "game is running" ? BlockerNote(game) : string.Empty)}");
            }

            return Record(game, now, new PrewarmResult(game.Name, false, skip, stats, now), 0);
        }

        DateTimeOffset warmedAt = _now();
        lock (_gate)
        {
            _last[game.Name] = warmedAt;
            _coldMib[game.Name] = (stats.Bytes + (1 << 20) - 1) >> 20;
            _coldSamples[game.Name] = samples!;
        }

        double mbps = stats.Seconds > 0 ? stats.Bytes / 1048576.0 / stats.Seconds : 0;
        _log.Info($"{game.Name}: prewarm done ({why}, {label}): {stats.Bytes >> 20} MiB requested in {stats.Seconds:F2} s = {mbps:F0} hint MB/s, {stats.Requests} requests, {stats.Errors} errors");
        PrewarmResult done = Record(game, warmedAt, new PrewarmResult(game.Name, true, null, stats, warmedAt) { Mode = label }, mbps);
        WarmMeasured?.Invoke(game, mbps);
        ColdWarmed?.Invoke(game, label!);
        return done;
    }

    // 기록 없는 읽기 한 번: 읽는 동안 _coldReading 표시를 세우고 (예외·취소로 끝나도 내림), 같은 디스크에서 재배치가 시작했으면 읽지 않고 null.
    private (StreamStats Stats, bool Complete, string? CancelReason)? ColdReadMarked(GameSettings game, IReadOnlyList<ReadOp> ops, CancellationToken ct, bool allowWhileExiting)
    {
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "cold prewarm", ct, allowWhileExiting: allowWhileExiting);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            return (new StreamStats(0, 0, 0, 0, Aborted: true), false, "disk read lease refused");
        }

        if (!TryMarkReading(game.Name, () => _coldReading = game.Name))
        {
            return null;
        }

        InvalidateResidencyProbe(game.Name);
        try
        {
            var cancelDetail = new PrewarmCancelDetail();
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(lease?.Token ?? ct);
            long total = ops.Sum(o => o.Length);
            using CancellationTokenRegistration leaseReg = lease?.Token.Register(() => cancelDetail.Set("disk read lease revoked")) ?? default;
            using CancellationTokenRegistration callerReg = ct.Register(() => cancelDetail.Set("caller cancelled"));
            Action<long, long> progress = (done, planned) => CheckPrewarmBatch(game, stop, done, planned,
                detail: cancelDetail, callerToken: ct, leaseToken: lease?.Token ?? default);
            (StreamStats Stats, bool Complete) read = _env.ColdReadWithProgress is { } withProgress
                ? withProgress(ops, stop.Token, progress)
                : _env.ColdRead(ops, stop.Token);
            if (_env.ColdReadWithProgress is null)
            {
                progress(read.Stats.Bytes, total);
            }

            return stop.IsCancellationRequested
                ? (read.Stats with { Aborted = true }, false, cancelDetail.First ?? "cancelled externally")
                : (read.Stats, read.Complete, cancelDetail.First);
        }
        finally
        {
            _coldReading = null;
        }
    }

    private bool ProfileMissed(string game, string key)
    {
        lock (_gate)
        {
            return _profileMiss.TryGetValue(game, out string? known) && known == key;
        }
    }

    private void RememberProfileMiss(string game, string key)
    {
        lock (_gate)
        {
            _profileMiss[game] = key;
        }
    }

    private void ForgetProfileMiss(string game)
    {
        lock (_gate)
        {
            _profileMiss.Remove(game);
        }
    }

    // 안티치트 게임은 기록 (트레이스) 을 만들지 않으므로 "trace needed" 라고 하지 않습니다.
    private string ProfileSkip(GameSettings game) => AntiCheat.ForbidsTrace(game.AntiCheat) || AntiCheat.ForbidsTrace(_env.AntiCheatOf(game)) ? "no profile" : "no profile; trace needed";
}
