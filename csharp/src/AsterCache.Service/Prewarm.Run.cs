using System.ComponentModel;
using System.Diagnostics;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;

namespace AsterCache.Service;

/// Prewarmer 의 핫 집합 한 게임 예열 실행: 스트림 읽기, 묶음 검사, 장치 보정.
public sealed partial class Prewarmer
{
    // 한 게임의 예외가 나머지 게임의 예열을 막지 않게 합니다.
    internal PrewarmResult Guarded(GameSettings game, string why, bool pin = true, bool ignoreCooldown = false, CancellationToken ct = default, long usedMib = 0, long? roomMib = null, bool trigger = false)
    {
        try
        {
            return Run(game, why, pin, ignoreCooldown, ct, usedMib, roomMib, trigger);
        }
        catch (Exception e)
        {
            _log.Error($"{game.Name}: prewarm failed ({why})", e);
            PrewarmResult failed = new(game.Name, false, "failed: " + e.GetType().Name, null, _now());
            TrackPendingWarm(game, failed, failed.At);
            return failed;
        }
    }

    /// 한 게임을 지금 예열 (조건을 만족할 때만). 동시에 하나만 돕니다.
    /// ct 는 스트림을 시작하기 전 (차례를 기다린 뒤) 과 핫 집합 스트림의 묶음 사이에서 확인합니다. 단 지금 읽는 그 게임이 시작되면 (NoteGameStart) 이 스트림도 다음 묶음 경계에서 멈춥니다 (keep_stream_on_game_start 가 켜졌을 때만 끝까지 읽음).
    /// usedMib: 같은 묶음에서 앞서 올린 핫 집합의 양 (기록 없는 예열의 예산에서 뺌). roomMib: 묶음에 남은 예산 (MiB), 핫 집합이 더 크면 들어가는 만큼만 읽음 (부분 예열).
    public PrewarmResult Run(GameSettings game, string why, bool pin = true, bool ignoreCooldown = false, CancellationToken ct = default, long usedMib = 0, long? roomMib = null, bool trigger = false)
    {
        if (!trigger)
        {
            return RunLocked(game, why, pin, ignoreCooldown, ct, usedMib, roomMib, trigger);
        }

        // 트리거 경로는 다른 예열이 락을 쥔 동안 취소할 수 있게 기다림 (게임이 시작돼 ct 가 취소되면 기다리지 않고 포기)
        if (!EnterRun(game.Name, ct, trigger: true))
        {
            using IDisposable work = TrackGameWork(game.Name);
            return CancelledBeforeRun(game, why, _now(), CancelDetailOf(game));
        }

        try
        {
            return RunLocked(game, why, pin, ignoreCooldown, ct, usedMib, roomMib, trigger);
        }
        finally
        {
            Monitor.Exit(_run);
        }
    }

    // 읽기 전에 취소된 예열의 사유: 그 게임이 이미 실행 중이면 게임 시작, 양보 등록 중이면 대여 거절, 아니면 호출자 취소.
    private string CancelDetailOf(GameSettings game) => _env.GameRunning(game) ? "game started"
        : _coordinator?.IsRegistered(game.Name) == true ? "disk read lease refused" : "caller cancelled";

    private PrewarmResult CancelledBeforeRun(GameSettings game, string why, DateTimeOffset now, string detail)
    {
        LogSkipped(game.Name, "prewarm cancelled " + detail, $"{game.Name}: prewarm cancelled ({why}) after 0 MiB ({detail})");
        PrewarmResult cancelled = new(game.Name, false, Cancelled, null, now) { CancelDetail = detail };
        TrackPendingWarm(game, cancelled, now);
        return cancelled;
    }

    private PrewarmResult RunLocked(GameSettings game, string why, bool pin, bool ignoreCooldown, CancellationToken ct, long usedMib, long? roomMib, bool trigger)
    {
        lock (_run)
        {
            using IDisposable work = TrackGameWork(game.Name);
            DateTimeOffset now = _now();
            if (ct.IsCancellationRequested)
            {
                return CancelledBeforeRun(game, why, now, CancelDetailOf(game));
            }

            if (!trigger && !IsCold(game) && TriggerWaitCovers(game.Name))
            {
                return CancelledBeforeRun(game, why, now, PreemptedByTrigger); // 트리거 예열이 기다리는 동안 같은 디스크의 새 배경 예열은 읽지 않고 기존 재시도에 맡김 (멈춘 직후 락을 다시 잡지 않게)
            }

            if (!_games.Any(x => string.Equals(x.Name, game.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return new PrewarmResult(game.Name, false, "game removed", null, now); // 대기 중에 등록이 해제됨: 읽지 않음
            }

            if (IsRetryReason(why) && MemoryRetryTooSoon(game.Name))
            {
                LogSkipped(game.Name, "memory retry floor", $"{game.Name}: prewarm skipped (stopped by memory less than {MemoryRetryFloor.TotalMinutes:F0} min ago) [{why}]");
                return new PrewarmResult(game.Name, false, "memory retry floor", null, now); // 대기 기한은 그대로 둠
            }

            if (PolicyBlocked(game, $"prewarm [{why}]"))
            {
                PrewarmResult blocked = new(game.Name, false, GamePolicySkip, null, now); // 상태 파일 이력은 건드리지 않음
                TrackPendingWarm(game, blocked, now);
                return blocked;
            }

            if (string.Equals(why, "rewarm", StringComparison.Ordinal) && RewarmAlreadyCovered(game))
            {
                PrewarmResult covered = new(game.Name, false, "prewarm already running or recently completed", null, now);
                TrackPendingWarm(game, covered, now);
                return covered;
            }

            if (Deferred(game))
            {
                PrewarmResult deferred = new(game.Name, false, PostUpdateDelay, null, now);
                TrackPendingWarm(game, deferred, now);
                return deferred;
            }

            if (IsCold(game) && !(_env.ColdRefusal(game) is null && _env.PrefetchBoot(game))) // S2 가 먼저: boot 트레이스가 생기면 더는 기록 없는 게임이 아님
            {
                return RunCold(game, why, now, ct, usedMib, trigger);
            }

            TimeSpan? since = ignoreCooldown ? null : CooldownSince(game, now, why);
            long? zoneBytes = _env.ZoneBytes(game);
            long availableRamMib = _env.AvailableRamMib();
            string? Skip(long? bytes) => PrewarmPolicy.SkipReason(bytes, _env.TraceExists(game), DriveHasRunningGame(game), availableRamMib,
                game.Prewarm.MinFreeRamMib, since, TimeSpan.FromMinutes(game.Prewarm.CooldownMin));
            string? skip = Skip(zoneBytes);
            long? hotBytes = zoneBytes; // 읽을 핫 집합의 크기 (실제 읽을 집합을 알면 그것)
            if (skip?.StartsWith("low memory", StringComparison.Ordinal) == true && ReadSetBytes(game) is { } readBytes && readBytes < zoneBytes)
            {
                skip = Skip(readBytes); // 존 크기가 아니라 실제 읽을 집합 + 예비가 들어가면 진행
                hotBytes = readBytes;
            }

            GameSettings warm = game;
            (long WarmedMib, long HotMib)? memoryPartial = null;
            long originalHotMib = 0;
            if (hotBytes is > 0 and long hotRead)
            {
                long hotMib = (hotRead + (1 << 20) - 1) >> 20;
                long originalMib = hotMib;
                lock (_gate)
                {
                    if (_startupTrim.TryGetValue(game.Name, out (long WarmedMib, long HotMib) trim))
                    {
                        originalMib = trim.HotMib;
                    }
                }

                originalHotMib = originalMib;
                bool lowMemory = skip?.StartsWith("low memory", StringComparison.Ordinal) == true;
                bool overBudget = skip is null && roomMib is { } room && hotMib > room;
                if (lowMemory || overBudget)
                {
                    if (PartialCapMib(game, hotMib, WindowAvailableMib(availableRamMib), PhysCapMib(), roomMib ?? long.MaxValue) is { } capMib)
                    {
                        // 전체가 들어가지 않음: 앞쪽부터 들어가는 만큼만 읽음 (max_stream_mib 를 줄인 사본). 틈 메우기는 생략.
                        // 전체 크기는 원래 값을 유지: 서비스 시작 상한으로 이미 줄인 사본이면 그 원래 크기.
                        memoryPartial = (capMib, originalMib);
                        warm = game with { MaxStreamMib = (int)capMib, MaxStreamSet = true };
                        skip = null;
                    }
                    else if (overBudget)
                    {
                        skip = "batch memory budget";
                    }
                }
            }

            if (skip is null)
            {
                using PrewarmRun run = PrewarmRun.Begin(); // 이 예열 한 회 동안만 계획을 재사용하고 단계 시간을 잼
                long? finalReadBytes = ReadSetBytes(warm, partial: memoryPartial is not null);
                availableRamMib = _env.AvailableRamMib();
                long finalReadMib = 0; // 아래 전체 경로가 메모리로 멈췄을 때 창으로 줄이는 데도 씀
                if (finalReadBytes is > 0 and long finalReadSize)
                {
                    finalReadMib = (finalReadSize + (1 << 20) - 1) >> 20;
                    long reserveMib = Math.Max(game.Prewarm.MinFreeRamMib, GapFillReserveMib);
                    long allowedMib = Math.Min(Math.Min(PhysCapMib(), roomMib ?? long.MaxValue), WindowAvailableMib(availableRamMib) - reserveMib); // 전체가 들어가는지도 창과 같은 여유를 둠: 경계에서 0 MiB 정지가 되풀이되지 않게
                    if (finalReadMib > allowedMib)
                    {
                        if (PartialCapMib(game, finalReadMib, WindowAvailableMib(availableRamMib), PhysCapMib(), roomMib ?? long.MaxValue) is { } capMib)
                        {
                            originalHotMib = Math.Max(originalHotMib, finalReadMib);
                            memoryPartial = (capMib, originalHotMib);
                            warm = game with { MaxStreamMib = (int)capMib, MaxStreamSet = true };
                            _log.Info($"{game.Name}: prewarm memory budget fell after planning; reading the first {capMib} MiB of {originalHotMib} MiB");
                        }
                        else
                        {
                            string reason = $"low memory (available {availableRamMib} MiB, reserve {reserveMib} MiB, final set {finalReadMib} MiB; less than {MinPartialMib} MiB fits)";
                            LogSkipped(game.Name, reason, $"{game.Name}: prewarm skipped ({reason}) [{why}]");
                            return Record(game, now, new PrewarmResult(game.Name, false, reason, null, now), 0);
                        }
                    }
                }

                if (warm.MaxStreamSet && !DriveHasRunningGame(game))
                {
                    _ = _env.ObservedBytes(game); // 줄여 읽기 전에 출처 정보를 캐시에 올림 (스케줄 단계는 캐시만 씀)
                }

                _log.Info($"{game.Name}: prewarm triggered by {TriggerCause(why)}");
                if (memoryPartial is { } running)
                {
                    lock (_gate)
                    {
                        _memoryRunning[game.Name] = running;
                    }
                }

                // 스트림이 끝났다는 알림이 나가기 전에 부분 예열 상태를 확정합니다 (자동 상태가 옛 상태를 보고 표시를 만들지 않게). 읽은 양은 설정한 상한이 아니라 실제 스트림 통계.
                void Settle(StreamStats stats)
                {
                    string? inputs = PrewarmRun.InputStamp(game);
                    lock (_gate)
                    {
                        _memoryRunning.Remove(game.Name);
                        _memoryStopAt.Remove(game.Name);
                        if (stats.Bytes <= 0)
                        {
                            return; // 아무것도 읽지 못함: 성공이 아니므로 부분 상태도 만들지 않음
                        }

                        _supplementNothingNew.Remove(game.Name);
                        _resume.Remove(game.Name); // 일반 예열이 부분 상태를 교체함: 보충의 옛 이어 읽기 위치는 낡음

                        if (memoryPartial is { } planned)
                        {
                            SetMemoryPartialLocked(game, stats, planned.WarmedMib, planned.HotMib, inputs);
                        }
                        else
                        {
                            _memoryPartial.Remove(game.Name); // 메모리 때문에 줄이지 않은 예열이 끝남
                            if (_games.FirstOrDefault(x => string.Equals(x.Name, game.Name, StringComparison.OrdinalIgnoreCase))?.MaxStreamMib == game.MaxStreamMib)
                            {
                                _partial.Remove(game.Name); // 설정 상한 그대로 읽었으면 남은 구간이 없음 (서비스 시작 상한으로 줄인 사본은 다름)
                                _supplementReadProgress.Remove(game.Name);
                                _supplementRetry.Remove(game.Name);
                            }
                        }
                    }
                }

                try
                {
                    (StreamStats Stats, bool Complete, string? CancelReason, bool MemoryStop)? streamed;
                    bool fellBack = false; // 전체 경로가 메모리로 멈추면 창으로 줄여 한 번만 다시 읽음
                    _yieldedSeconds = 0; // StreamHot 이 끝날 때마다 더함 (done 줄에 한 번 보임)
                    while (true)
                    {
                        streamed = StreamHot(warm, ct, zoneBytes, availableRamMib, memoryPartial is not null, Settle,
                            allowReorder: memoryPartial is null && !HasPartialState(game.Name), roomMib: roomMib ?? long.MaxValue, allowWhileExiting: trigger, why: why);
                        if (fellBack || memoryPartial is not null || streamed is not { Complete: false, MemoryStop: true }
                            || MemoryFallbackCapMib(game, finalReadMib, roomMib ?? long.MaxValue, ct) is not { } fallbackCap)
                        {
                            break;
                        }

                        fellBack = true;
                        originalHotMib = Math.Max(originalHotMib, finalReadMib);
                        memoryPartial = (fallbackCap, originalHotMib);
                        warm = game with { MaxStreamMib = (int)fallbackCap, MaxStreamSet = true };
                        _ = _env.ObservedBytes(game); // 줄여 읽기 전에 출처 정보를 캐시에 올림 (위 사전 검사 경로와 같음)
                        lock (_gate)
                        {
                            _memoryRunning[game.Name] = memoryPartial.Value;
                        }

                        _log.Info($"{game.Name}: prewarm stopped by memory before the full list was read; reading the first {fallbackCap} MiB of {originalHotMib} MiB instead");
                    }

                    if (streamed is not { } hot)
                    {
                        LogSkipped(game.Name, RelocationRunning, $"{game.Name}: prewarm skipped ({RelocationRunning}) [{why}]");
                        return Record(game, now, new PrewarmResult(game.Name, false, RelocationRunning, null, now), 0);
                    }

                    (StreamStats s, bool complete, string? cancelReason, bool memoryStop) = hot;
                    if (!complete)
                    {
                        string reason = cancelReason ?? "cancelled externally";
                        long? keptMib = memoryStop ? NoteMemoryStop(game, s, memoryPartial) : null; // Record 가 상태 파일에 쓰기 전에 읽은 데까지를 부분 상태로 확정
                        LogSkipped(game.Name, "prewarm cancelled " + reason, () => $"{game.Name}: prewarm cancelled ({why}) after {s.Bytes >> 20} MiB ({reason})");
                        if (keptMib is { } kept)
                        {
                            _log.Info($"{game.Name}: prewarm stopped by memory; kept the {kept} of {memoryPartial?.HotMib} MiB read as partial");
                        }

                        PrewarmResult stopped = Record(game, now, new PrewarmResult(game.Name, false, Cancelled, s, now) { CancelDetail = reason }, 0);
                        if (memoryStop && s.Bytes > 0)
                        {
                            ExpediteMemoryRetry(game.Name); // 읽은 게 없는 정지 (0 MiB) 는 기존 재시도 주기를 유지
                        }

                        return stopped;
                    }

                    if (s.Bytes <= 0)
                    {
                        _log.Info($"{game.Name}: prewarm read nothing ({why}), not recorded as done");
                        return Record(game, now, new PrewarmResult(game.Name, false, "nothing read", s, now), 0);
                    }

                    DateTimeOffset warmedAt = _now();
                    lock (_gate)
                    {
                        _last[game.Name] = warmedAt;
                        _coldMib.Remove(game.Name); // 기록이 생겨 핫 집합으로 바뀜
                        _coldSamples.Remove(game.Name);
                    }

                    double mbps = s.Seconds > 0 ? s.Bytes / 1048576.0 / s.Seconds : 0;
                    string kind = memoryPartial is { } part ? $"partial {ActualMib(s, part.WarmedMib)}/{part.HotMib} MiB, the rest does not fit in memory: " : string.Empty;
                    _log.Info($"{game.Name}: prewarm done ({why}): {kind}{s.Bytes >> 20} MiB requested, {s.GapBytes / 1048576.0:F2} MiB gaps filled in {s.Seconds:F2} s = {mbps:F0} hint MB/s, {s.Requests} requests, {s.Errors} errors, room {(roomMib is { } roomLimit ? roomLimit + " MiB" : "unlimited")}, available {_env.AvailableRamMib()} MiB{(_yieldedSeconds > 0 ? $", yielded {_yieldedSeconds:F1} s" : string.Empty)}");
                    _log.Info($"{game.Name}: prewarm phases ({why}): {run.Summary()}");
                    PrewarmResult done = Record(game, warmedAt, new PrewarmResult(game.Name, true, null, s, warmedAt), mbps);
                    WarmMeasured?.Invoke(game, mbps);
                    if (pin && !ct.IsCancellationRequested && _games.Any(x => string.Equals(x.Name, game.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        _pins.TryPin(game, why == AfterRelocation);
                    }

                    return done;
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    skip = "no trace file";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
                {
                    _log.Error($"{game.Name}: prewarm failed ({why})", e);
                    return Record(game, now, new PrewarmResult(game.Name, false, "failed: " + e.GetType().Name, null, now), 0);
                }
                finally
                {
                    lock (_gate)
                    {
                        _memoryRunning.Remove(game.Name);
                    }
                }
            }

            LogSkipped(game.Name, skip, () => $"{game.Name}: prewarm skipped ({skip}) [{why}]{(skip == "game is running" ? BlockerNote(game) : string.Empty)}");
            return Record(game, now, new PrewarmResult(game.Name, false, skip, null, now), 0);
        }
    }

    private void CheckPrewarmBatch(GameSettings game, CancellationTokenSource stop, long done, long total, bool reserveFinalSet = false,
        PrewarmCancelDetail? detail = null, CancellationToken callerToken = default, CancellationToken leaseToken = default, ReadYield? yield = null, bool preemptible = false)
    {
        if (stop.IsCancellationRequested)
        {
            string cancellation = leaseToken.IsCancellationRequested ? "disk read lease revoked"
                : callerToken.IsCancellationRequested ? "caller cancelled"
                : _streamGameStarted ? "game started on the same disk"
                : _streamPreempted ? PreemptedByTrigger
                : "cancelled externally";
            detail?.Set(cancellation);
            return;
        }

        long remaining = Math.Max(0, total - done);
        long remainingMib = (remaining + ((1 << 20) - 1)) >> 20;
        long availableRamMib = _env.AvailableRamMib();
        long finalSetMib = (total + ((1 << 20) - 1)) >> 20;
        bool finalSetExceedsReserve = reserveFinalSet &&
            (availableRamMib < GapFillReserveMib || finalSetMib > availableRamMib - GapFillReserveMib);
        // keep_stream_on_game_start 가 켜졌으면 읽던 게임 자신의 실행만으로는 취소하지 않음 (같은 디스크의 다른 게임 실행은 항상 봄). 메모리 검사는 그대로.
        string? reason = null;
        bool memoryReason = false;
        Func<GameSettings, bool>? snapshot = null;
        bool running(GameSettings g) => (snapshot ??= _env.GameRunningSnapshot?.Invoke() ?? _env.GameRunning)(g); // 처음 필요할 때 한 번만 찍어 묶음 안의 판정이 나눠 씀
        if (RunningBlocksRead(game, running))
        {
            reason = running(game) ? "game started" : "another game is running on the same disk";
        }
        else if (PolicyBlocksPrewarm(game))
        {
            reason = "game policy"; // 읽는 도중 정책이 바뀌면 다음 묶음 경계에서 멈춤
        }
        else if (preemptible && PreemptedByTriggerWait(game.Name))
        {
            reason = PreemptedByTrigger; // 트리거가 표시를 세우기 전에 시작해 정지 신호를 놓친 읽기도 다음 묶음 경계에서 멈춤
            _streamPreempted = true;
        }
        else if (remaining > 0 && (yield is null ? ExternalLoadWhilePaused(game, stop.Token, running) : ObserveReadYield(game, stop.Token, running, yield)))
        {
            reason = "disk busy after pause";
        }
        else if (remaining > 0 && _env.DiskSample?.Invoke(game) is { } sample
            && sample.State is DiskActivity.SampleState.Warming or DiskActivity.SampleState.Stale)
        {
            reason = "disk sample " + sample.State.ToString().ToLowerInvariant();
        }
        else if (finalSetExceedsReserve)
        {
            reason = $"memory reserve: available {availableRamMib} MiB, final set {finalSetMib} MiB + reserve {GapFillReserveMib} MiB";
            memoryReason = true;
        }
        else if (!PrewarmPolicy.HasMemoryBudget(remainingMib, availableRamMib, game.Prewarm.MinFreeRamMib))
        {
            reason = $"memory budget: available {availableRamMib} MiB, remaining {remainingMib} MiB + reserve {game.Prewarm.MinFreeRamMib} MiB";
            memoryReason = true;
        }

        if (reason is not null)
        {
            detail?.Set(reason, memoryReason);
            stop.Cancel();
        }
    }

    // 핫 집합 스트림 한 번. 게임이 시작되면 (NoteGameStart) 다음 묶음 경계에서 멈춥니다. 단 keep_stream_on_game_start 가 켜졌고 그 게임의 스트림이면 ct 가 취소돼도 끝까지 읽습니다.
    // 같은 디스크에서 재배치가 시작했으면 읽지 않고 null.
    // settle: 끝까지 읽었을 때 StreamEnded 알림보다 먼저 부름 (읽은 통계를 받아 부분 예열 상태를 확정하거나 지움).
    // skipBytes: 읽기 목록 앞쪽 이만큼은 이미 읽었으니 건너뜀 (끊긴 보충을 이어 읽음, 틈 메우기 없는 경로에서만). onTotal: 이번에 읽을 목록의 길이 (건너뛴 앞부분 제외).
    private (StreamStats Stats, bool Complete, string? CancelReason, bool MemoryStop)? StreamHot(GameSettings game, CancellationToken ct, long? zoneBytes, long availableRamMib, bool partial = false, Action<StreamStats>? settle = null, long skipBytes = 0, Action<long>? onTotal = null, bool allowReorder = true, long roomMib = long.MaxValue, bool allowWhileExiting = false, string? why = null)
    {
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "prewarm stream", ct, preserveForGameRoot: _auto.KeepStreamOnGameStart ? game.OriginRoot : null, preserveGameKey: game.Name, allowWhileExiting: allowWhileExiting);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            return (new StreamStats(0, 0, 0, 0, Aborted: true), false, "disk read lease refused", false);
        }

        using var stop = new CancellationTokenSource();
        using var calibration = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token);
        var cancelDetail = new PrewarmCancelDetail();
        if (!TryMarkReading(game.Name, () =>
        {
            _streamGameStarted = false;
            (_streamFromTrigger, _streamPreempted) = (allowWhileExiting, false); // allowWhileExiting 은 Run 이 트리거 예열일 때만 켬
            _calibrationStop = calibration;
            (_keepStream, _streaming, _streamStop, _streamCancelDetail) = (false, game.Name, stop, cancelDetail);
            _yield = null;
            _driveObservedAt = _now();
        }))
        {
            return null;
        }

        InvalidateResidencyProbe(game.Name);
        using PrewarmRun run = PrewarmRun.Begin(); // 보충 등 바깥 회가 없는 경로도 이 스트림 한 번 동안은 계획을 재사용
        DateTimeOffset? last = null;
        bool complete = false;
        StreamStats? finalStats = null;
        long startedAt = Stopwatch.GetTimestamp();
        double? streamSeconds = null; // 사다리 시간이 노출 시간에 들어가지 않게 스트림이 끝난 때의 값
        // 읽기는 이 스레드 (묶음 점검 콜백도 같은 스레드) 에서 내므로 I/O 우선순위도 여기서 바꾸고 아래 finally 에서 되돌립니다.
        var readYield = new ReadYield(_env.SetReadIoLow, PrewarmYield.StartsYielding(why, _env.UptimeMs()));
        try
        {
            using CancellationTokenRegistration leaseReg = lease?.Token.Register(() =>
            {
                cancelDetail.Set("disk read lease revoked");
                stop.Cancel();
            }) ?? default;
            using CancellationTokenRegistration callerReg = ct.Register(() =>
            {
                if (!_keepStream)
                {
                    cancelDetail.Set("caller cancelled");
                    stop.Cancel();
                }
            });
            lease?.NoteReadIssue("prewarm planning", 0, false);
            long maxBytes = partial ? 0 : GapFillBudgetBytes(game, zoneBytes, _env.EffectiveZoneBytes(game), _env.PhysicalRamBytes(), availableRamMib); // 부분 예열은 틈 메우기 예산 0
            if (!partial)
            {
                long allocationMib = Math.Min(PhysCapMib(), roomMib);
                long allocationBytes = allocationMib > (long.MaxValue >> 20) ? long.MaxValue : allocationMib << 20;
                maxBytes = Math.Min(maxBytes, allocationBytes);
            }
            // 트리거 예열은 보정 읽기를 하지 않음 (ORBIT 결정은 이미 쌓인 장치 구간만 쓰고 이번 스트림의 표본을 요구하지 않음)
            if (!allowWhileExiting && _env.DeviceCalibration is not null && (_env.DeviceCalibrationEnabled?.Invoke() ?? true)) // 훅이 없거나 off 면 기준선과 같은 호출만 남김
            {
                using var calibrationTimer = PrewarmRun.Time(PrewarmPhase.Calibration);
                IReadOnlyList<ReadOp> calibrationOps = _streamer is PrefetchStreamer ? PrefetchStreamer.IncumbentPrewarmOps(game, maxBytes, calibration.Token) : Array.Empty<ReadOp>();
                calibration.Token.ThrowIfCancellationRequested();
                lease?.NoteReadIssue("device calibration", calibrationOps.Sum(op => op.Length), true);
                CalibrateDevice(game, calibrationOps, _env.AntiCheatOf(game), lease, calibration.Token);
            }

            calibration.Token.ThrowIfCancellationRequested();
            using (PrewarmRun.Time(PrewarmPhase.Common))
            {
                WarmCommonFirst(game, stop, maxBytes, skipBytes, lease, cancelDetail, ct, readYield);
            }

            calibration.Token.ThrowIfCancellationRequested();
            (StreamStats, bool) r = _streamer.StreamPrewarmCoordinated(game, stop.Token, (done, total) =>
            {
                onTotal?.Invoke(total);
                CheckPrewarmBatch(game, stop, done, total, reserveFinalSet: true, detail: cancelDetail, callerToken: ct, leaseToken: lease?.Token ?? default, yield: readYield, preemptible: true);
                if (done == 0)
                {
                    return;
                }

                DateTimeOffset at = _now();
                if (last is null || at - last >= ProgressEvery)
                {
                    last = at;
                    Progress?.Invoke(game, done >> 20, (total + (1 << 20) - 1) >> 20);
                }
            }, maxBytes, skipBytes, () => !stop.IsCancellationRequested && (lease?.IsValid ?? true), allowReorder,
                (stage, bytes, isData) => lease?.NoteReadIssue(stage, bytes, isData));
            complete = r.Item2 && !stop.IsCancellationRequested;
            finalStats = r.Item1;
            LogYield(game.Name, r.Item1.Bytes, lease);
            streamSeconds = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
            if (complete && r.Item1.Bytes > 0)
            {
                LadderAfterStream(game, stop.Token); // 같은 디스크 대여 안에서: 게임 시작·대여 취소가 stop 으로 바로 멈춤
            }

            return (r.Item1, complete, cancelDetail.First, cancelDetail.Memory);
        }
        catch (OperationCanceledException) when (calibration.IsCancellationRequested)
        {
            StreamStats cancelled = new(0, 0, 0, 0, Aborted: true);
            finalStats = cancelled;
            LogYield(game.Name, 0, lease);
            return (cancelled, false, cancelDetail.First ?? (lease?.Token.IsCancellationRequested == true ? "disk read lease revoked" : ct.IsCancellationRequested ? "caller cancelled" : "cancelled externally"), cancelDetail.Memory);
        }
        finally
        {
            FinishYield(readYield, game.Name); // 스레드 풀 스레드에 낮은 우선순위가 남지 않게 (정상·취소·예외 모두)
            _yieldedSeconds += readYield.Seconds;
            bool interrupted = !complete && _streamGameStarted;
            _calibrationStop = null;
            (_streaming, _streamStop, _streamCancelDetail) = (null, null, null);
            // shortcut: 스트림 PDH 는 순차 탐침과 비교할 수 없어 교차 확인을 배선하지 않음, 실측 이탈이 확인되면 끝까지 읽은 스트림의 실제 읽기 바이트/시간으로 다시 연결
            try
            {
                _env.OrbitExposure?.Invoke(game, streamSeconds ?? Stopwatch.GetElapsedTime(startedAt).TotalSeconds, interrupted);
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
            {
                _log.Warn($"{game.Name}: orbit exposure update skipped: {e.GetType().Name}");
            }

            if (complete && finalStats is not null)
            {
                settle?.Invoke(finalStats);
            }

            StreamEnded?.Invoke(game, complete && (finalStats is { Bytes: > 0 } || skipBytes > 0)); // 아무것도 읽지 못했으면 예열 완료로 알리지 않음 (이어 읽기의 0 바이트는 앞에서 이미 읽은 것)
        }
    }

    // 읽기가 끝나면 낮은 I/O 우선순위를 보통으로 되돌림. 되돌리지 못하면 (거의 없음) 스레드 풀 스레드에 낮음이 남을 수 있어 흔적을 남깁니다.
    private void FinishYield(ReadYield readYield, string game)
    {
        if (!readYield.Finish(_now()))
        {
            _log.Warn($"{game}: could not restore normal I/O priority after prewarm");
        }
    }

    // 장치 표본 훅: 실패해도 스트림은 그대로 진행합니다 (취소는 아래 스트림이 판단). 읽기 시간이 CalibrationLimit 을 넘으면 그 표본은 훅이 버리고 (보정 자료에 넣지 않음) 예열을 계속합니다.
    private void CalibrateDevice(GameSettings game, IReadOnlyList<ReadOp> ops, string? antiCheat, DiskReadCoordinator.Lease? lease, CancellationToken ct)
    {
        using var limit = new CancellationTokenSource(CalibrationLimit);
        try
        {
            _env.DeviceCalibration?.Invoke(game, ops, antiCheat, lease, ct, limit.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 취소 뒤에는 다음 읽기를 발행하지 않습니다.
        }
        catch (Exception e)
        {
            _log.Warn($"{game.Name}: device calibration skipped: {e.GetType().Name}");
        }

        if (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            PrewarmRun.NoteCalibrationCapped();
            _log.Info($"{game.Name}: device calibration stopped at the {CalibrationLimit.TotalSeconds:F0} s limit, sample discarded");
        }
    }

    // 모드·맵을 모르는 시작 예열: 여러 회차에서 반복해 읽힌 common 을 핫 집합 스트림보다 먼저 올립니다 (common 이 없으면 아무것도 안 해 기존 동작 그대로).
    // 이번 스트림이 읽을 범위의 부분집합만 읽어 RAM 예산은 그대로이고, map_initial (일부 회차에서만 읽힌 나머지) 은 기존 스트림이 이어 읽어 이득이 줄지 않습니다 (common 은 이미 올라와 있어 다시 읽어도 빠름).
    // 디스크 대여·중단 신호는 StreamHot 의 것을 그대로 씁니다. 실패하면 건너뛰고 스트림이 같은 오류를 판단합니다.
    // 최종 예약 검사는 common 을 읽기 전에 이번 스트림 전체 양으로 합니다 (거절이면 common 도 읽지 않음).
    private void WarmCommonFirst(GameSettings game, CancellationTokenSource stop, long maxBytes, long skipBytes, DiskReadCoordinator.Lease? lease,
        PrewarmCancelDetail cancelDetail, CancellationToken callerToken, ReadYield? readYield = null)
    {
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            (IReadOnlyList<ReadOp> ops, long streamTotal) = _env.CommonFirstOpsWithCancellation is { } checkedOps
                ? checkedOps(game, maxBytes, skipBytes, stop.Token)
                : _env.CommonFirstOps(game, maxBytes, skipBytes);
            if (stop.IsCancellationRequested || ops.Count == 0)
            {
                return;
            }

            CheckPrewarmBatch(game, stop, 0, streamTotal, reserveFinalSet: true, detail: cancelDetail, callerToken: callerToken, leaseToken: lease?.Token ?? default, yield: readYield, preemptible: true);
            if (stop.IsCancellationRequested || lease is { IsValid: false })
            {
                if (lease is { IsValid: false })
                {
                    cancelDetail.Set("disk read lease revoked");
                    stop.Cancel();
                }

                return;
            }

            long total = ops.Sum(o => o.Length);
            Action<long, long> progress = (done, planned) =>
            {
                CheckPrewarmBatch(game, stop, done, planned, detail: cancelDetail, callerToken: callerToken, leaseToken: lease?.Token ?? default, yield: readYield, preemptible: true);
                if (lease is { IsValid: false })
                {
                    cancelDetail.Set("disk read lease revoked");
                    stop.Cancel();
                }
            };
            (StreamStats Stats, bool Complete) read;
            if (_env.ColdReadWithIssue is { } withIssue)
            {
                read = withIssue(ops, stop.Token, progress, (stage, bytes, isData) => lease?.NoteReadIssue(stage, bytes, isData));
            }
            else if (_env.ColdReadWithProgress is { } withProgress)
            {
                lease?.NoteReadIssue("common-first batch", total, true);
                read = withProgress(ops, stop.Token, progress);
            }
            else
            {
                lease?.NoteReadIssue("common-first read", total, true);
                read = _env.ColdRead(ops, stop.Token);
            }
            _log.Info($"{game.Name}: common-first prewarm {read.Stats.Bytes >> 20}/{total >> 20} MiB" + (read.Complete ? string.Empty : " (stopped)"));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            // 스트림 취소는 공통 구간 계산·읽기를 끝내고 호출부가 핫 스트림을 취소 처리하게 둡니다.
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            _log.Warn($"{game.Name}: common-first prewarm skipped: {e.GetType().Name}");
        }
    }
}
