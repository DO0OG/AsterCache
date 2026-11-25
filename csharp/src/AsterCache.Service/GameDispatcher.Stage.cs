using System.Diagnostics;
using System.Globalization;
using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;

namespace AsterCache.Service;

/// 시작 스트림의 단계 인지 부분: 같은 게임의 이후 실행 파일 시작마다 그 단계가 읽는 블록을 양보·메모리 게이트 아래에서 흘리고,
/// 첫 실행 파일의 스트림은 단계 정보가 있으면 앞부분과 첫 단계만 흘립니다 (다음 단계는 그 실행 파일이 뜰 때 이후 실행 파일 경로가 흘림). 디스패처가 직렬이라 모두 Handle 스레드에서 돕니다.
public sealed partial class GameDispatcher
{
    private readonly Dictionary<string, DateTimeOffset> _lastStage = new(StringComparer.OrdinalIgnoreCase); // (게임, 실행 파일) -> 마지막 단계 스트림 시각

    /// 스트림이 낸 묶음의 요청·받아들인 바이트를 세고 (초 단위 기록의 service_stream_* 열), 끝날 때 그 구간의 디스크 표본을 한 줄로 남기는 판정판 (플레이 기록과 같은 것, 없으면 아무것도 하지 않음). 스트림의 동작에는 쓰지 않습니다.
    internal LoadPhaseBoard? LoadPhases { get; set; }

    /// 스트림 한 번의 구성. Ops 가 null 이면 시작 목록 전체 (IGameStreamer.StreamCoordinated), BoundaryPid 가 있으면 그 프로세스의 읽기만 경계 판정에 셉니다.
    // Planner: 목록 계획을 시계·표본·상한 취소 범위 안에서 만들게 넘김 (원본은 스트리머가 스트림 안에서 목록을 만들었음)
    private sealed record StreamSpec(IReadOnlyList<ReadOp>? Ops, int? BoundaryPid, bool StartLow, bool MemoryGate, Func<CancellationToken, IReadOnlyList<ReadOp>?>? Planner = null, bool IgnoreBoundary = false, bool YieldToQueuedGame = false, ServiceReadPath ReadPath = ServiceReadPath.StartStream);

    private sealed record StreamOutcome(StreamStats Stats, double Seconds, double MbPerSecond, bool BoundaryStopped, bool CapStopped, string? OtherStop, bool Cancelled,
        double YieldedSeconds, RuntimeLoadBoundary Boundary);

    private bool IsLaterStart(GameSettings g, ProcessStart start) =>
        _env.StageStreamEnabled() && !LauncherExes.Is(start.ExeName) && (_env.OtherRunning(g, start) || StartedEarlierThan(g, start));

    // 이 시작보다 Settle 넘게 먼저 관측된 (또는 관측 기록이 없는 = 서비스 시작 전부터 돌던) 같은 게임의 등록 이름 프로세스가 있는가.
    // 처리 시점의 나이가 아니라 시작 시각 차이로 봐서, 같은 시작 무리는 첫 스트림이 길어져도 이후 실행 파일이 되지 않음 (기존 쿨다운이 조용히 건너뜀).
    private bool StartedEarlierThan(GameSettings g, ProcessStart start)
    {
        TimeSpan startAge = _env.Age(start);
        foreach ((int pid, string exe) in _env.OtherProcesses(g, start))
        {
            if (!_seen.TryGetValue(pid, out ProcessStart? seen) || !seen.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase) || _env.Age(seen) - startAge > DispatchEnv.Settle)
            {
                return true;
            }
        }

        return false;
    }

    // 대기열 어디에든 다른 게임의 시작이 있는가 (그 시작을 먼저 처리하려고 증거 대기를 끝냄)
    private bool OtherGameQueued(GameSettings g) => _env.OtherGameQueued(g.Name);

    private static string MiB(long bytes) => (bytes / 1_048_576d).ToString("F0", CultureInfo.CurrentCulture);

    // 첫 실행 파일 (또는 같은 시작 무리) 의 시작 스트림. 단계 정보가 있으면 앞부분과 첫 단계만 흘립니다.
    private bool StreamFirst(GameSettings g, ProcessStart start, DiskReadCoordinator.Lease? lease, CancellationToken stop)
    {
        bool stage = _env.StageStreamEnabled();
        IReadOnlyList<ReadOp>? Plan(CancellationToken token) =>
            _streamer.PlanFirstStage(g, token) ?? _streamer.StartOps(g, token); // 메모리 게이트가 걸리면 단계 정보가 없어도 같은 잘린 목록으로 흘림

        StreamOutcome o = RunStream(g, lease, stop, new StreamSpec(null, null, false, stage, stage ? Plan : null));
        double mbPerSecond = o.MbPerSecond;
        if (o.BoundaryStopped)
        {
            _log.Info($"{g.Name}: start stream stopped at load boundary after {o.Stats.Bytes} bytes, {o.Seconds:F1} s ({mbPerSecond:F1} MB/s); boundary at {o.Boundary.BoundaryMs} ms, peak {o.Boundary.PeakBytes >> 20} MiB per 10 s, last window {o.Boundary.LastWindowBytes >> 20} MiB, observed {o.Boundary.ObservedMs / 1000d:F1} s");
            ReportStream(start, "stopped", "load boundary", o.Stats.Bytes, o.Seconds, mbPerSecond);
            return false;
        }

        if (o.CapStopped)
        {
            _log.Info($"{g.Name}: start stream stopped at cap after {o.Stats.Bytes} bytes, {o.Seconds:F1} s ({mbPerSecond:F1} MB/s)");
            ReportStream(start, "stopped", "cap", o.Stats.Bytes, o.Seconds, mbPerSecond);
            return false;
        }

        if (o.OtherStop is not null)
        {
            _log.Info($"{g.Name}: start stream stopped at {o.OtherStop} after {o.Stats.Bytes} bytes, {o.Seconds:F1} s ({mbPerSecond:F1} MB/s)");
            ReportStream(start, "stopped", o.OtherStop, o.Stats.Bytes, o.Seconds, mbPerSecond);
            return false;
        }

        if (o.Cancelled)
        {
            _log.Info($"{g.Name}: start stream cancelled after {o.Stats.Bytes} bytes");
            ReportStream(start, "cancelled", "cancelled", o.Stats.Bytes, o.Seconds, mbPerSecond);
            return false;
        }

        _log.Info($"{g.Name}: streamed {o.Stats.Bytes} bytes in {o.Stats.Requests} requests, {o.Stats.Errors} errors, {o.Seconds:F3} s ({mbPerSecond:F1} MB/s)");
        ReportStream(start, "completed", null, o.Stats.Bytes, o.Seconds, mbPerSecond);
        return true;
    }

    // 같은 게임의 이후 실행 파일 시작: 그 단계 (모르면 시작 목록 전체, 읽기 증거가 보인 뒤에만) 의 블록을 낮은 우선순위에서 흘립니다.
    private bool HandleLater(GameSettings g, ProcessStart start, DateTimeOffset now, CancellationToken stop)
    {
        string exe = start.ExeName;
        string key = g.Name + "|" + exe;
        void Skip(string why, string reason)
        {
            _log.Info($"{g.Name}: stage stream skipped ({exe}: {why})");
            ReportStream(start, "skipped", reason, null, null, null);
        }

        if (StageStreamRule.LaterSkip(now, _lastStage.TryGetValue(key, out DateTimeOffset prev) ? prev : null) is { } cooldown)
        {
            Skip(cooldown, "stage stream cooldown");
            return false;
        }

        try
        {
            LoadRefillPlan? refill = PlanRefill(g, start, stop);
            StageLaterPlan plan = refill is null ? _streamer.PlanLaterStage(g, exe, stop) : new StageLaterPlan(refill.Ops, true, 0); // 목록의 증거 확인은 짧음 (아래)
            if (plan.Ops.Count == 0)
            {
                Skip("no blocks in the start list", "no stage blocks");
                return false;
            }

            if (!plan.HasStage && WaitForLoadEvidence(g, start, stop) is { } missing)
            {
                Skip(missing, missing.StartsWith("another", StringComparison.Ordinal) ? "another game started" : "no load evidence");
                return false;
            }

            if (refill is not null && ConfirmRefillStart(g, start, stop) is { } unconfirmed)
            {
                Skip(unconfirmed, unconfirmed.StartsWith("another", StringComparison.Ordinal) ? "another game started" : "no load evidence");
                return false;
            }

            using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(g.OriginRoot, "stage stream", stop, g.OriginRoot, allowWhileYielded: true, preserveGameKey: g.Name);
            if (_coordinator is not null && lease is null)
            {
                Skip("disk yielded", "disk yielded");
                return false;
            }

            _lastStage[key] = now;
            StreamOutcome o = RunStream(g, lease, stop, new StreamSpec(plan.Ops, start.Pid, true, true, null, refill is not null, refill is not null, ServiceReadPath.StageStream));
            if (refill is not null)
            {
                _lastStage[key] = _now(); // 흘리는 동안 겹친 같은 로드의 시작이 곧바로 다시 흘리지 않게 끝난 시각부터 대기
            }

            string? reason = o.BoundaryStopped ? "load boundary" : o.CapStopped ? "cap" : o.OtherStop ?? (o.Cancelled ? "cancelled" : null);
            string skipped = plan.VolatileSkippedBytes > 0 ? $"; skipped volatile {MiB(plan.VolatileSkippedBytes)} MiB" : string.Empty;
            string source = refill is not null ? $"; source load history ({refill.Plays} plays, {refill.Loads} loads, {MiB(plan.Ops.Sum(op => op.Length))} MiB planned)" : plan.HasStage ? "; source trace stage" : "; source start list";
            _log.Info($"{g.Name}: stage stream {exe} {MiB(o.Stats.Bytes)} MiB in {o.Seconds:F1} s ({o.MbPerSecond:F1} MB/s, {(reason is null ? "completed" : "stopped, " + reason)}), yielded {o.YieldedSeconds:F1} s{skipped}{source}");
            ReportStream(start, reason is null ? "completed" : reason == "cancelled" ? "cancelled" : "stopped", reason, o.Stats.Bytes, o.Seconds, o.MbPerSecond);
            return reason is null;
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            Skip("cancelled while planning", "planning cancelled");
            return false;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            ReportStream(start, "cancelled", "service stopping", null, null, null);
            throw;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            Skip("no trace file", "no trace file");
            return false;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            _log.Error($"{g.Name}: stage stream failed ({exe})", e); // 새 경로의 실패가 디스패처 루프를 멈추지 않게 함
            ReportStream(start, "failed", e.GetType().Name, null, null, null);
            return false;
        }
    }

    // 같은 게임의 앞선 플레이에서 모은 로드 구간 블록 목록 (메모리 게이트가 허용하는 양 이하). 목록이 없거나 시작이 이미 오래됐으면 null (지금 동작 그대로).
    private LoadRefillPlan? PlanRefill(GameSettings g, ProcessStart start, CancellationToken stop)
    {
        if (_env.Age(start) >= StageStreamRule.RefillMaxStartAge)
        {
            return null;
        }

        long available;
        try
        {
            available = _env.AvailableMib();
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            available = long.MaxValue / (1L << 20); // 가용 메모리를 읽지 못하면 고정 상한만 (스트림의 게이트도 같은 경우 게이트 없음)
        }

        return _streamer.PlanLoadRefill(g, StageStreamRule.RefillCapBytes(available, StageStreamRule.ReserveMib(g)), stop) is { Ops.Count: > 0 } plan ? plan : null;
    }

    // 로드 구간 목록은 단계 증거 (64 MiB) 를 기다리지 않는 대신, 같은 이름의 보조 시작 (오류 처리기·짧은 재시작) 을 거르는 짧은 확인을 합니다:
    // 새 프로세스가 살아 있고 RefillEvidenceWindow 안에 RefillEvidenceBytes 이상 읽었으면 통과 (읽자마자 바로). 흘리지 않을 이유를 돌려주고, 흘려도 되면 null.
    private string? ConfirmRefillStart(GameSettings g, ProcessStart start, CancellationToken stop)
    {
        long? ReadOf() => _env.ReadCountersWithExeNames(g.ExeNames).Where(c => c.Pid == start.Pid).Select(c => (long?)c.ReadBytes).FirstOrDefault();
        int waited = 0;
        while (true)
        {
            stop.ThrowIfCancellationRequested();
            if (ReadOf() is not { } read)
            {
                return "no load evidence, the process is gone";
            }

            if (read >= StageStreamRule.RefillEvidenceBytes)
            {
                return null;
            }

            if (waited >= (int)StageStreamRule.RefillEvidenceWindow.TotalSeconds)
            {
                return $"no load evidence, {read >> 10} KiB in {waited} s";
            }

            if (OtherGameQueued(g))
            {
                return "another game started";
            }

            _env.Sleep(TimeSpan.FromSeconds(1), stop);
            waited++;
        }
    }

    // 단계를 모르는 실행 파일은 새 프로세스의 읽기가 대기 시작 뒤 EvidenceWindow 안에 EvidenceBytes 만큼 늘 때만 흘립니다 (오류 처리기·보조 프로세스 제외).
    // 이 시작이 이미 창보다 오래됐으면 누적 읽기가 커도 흘리지 않음. 흘리지 않을 이유를 돌려주고, 흘려도 되면 null.
    private string? WaitForLoadEvidence(GameSettings g, ProcessStart start, CancellationToken stop)
    {
        TimeSpan age = _env.Age(start);
        if (age >= StageStreamRule.EvidenceWindow)
        {
            return $"no load evidence, the start is already {(int)age.TotalSeconds} s old";
        }

        long ReadOf() => _env.ReadCountersWithExeNames(g.ExeNames).Where(c => c.Pid == start.Pid).Select(c => c.ReadBytes).DefaultIfEmpty(0).Max();
        long baseline = ReadOf();
        while (true)
        {
            stop.ThrowIfCancellationRequested();
            long read = Math.Max(0, ReadOf() - baseline);
            if (_env.Age(start) >= StageStreamRule.EvidenceWindow) // 읽은 뒤에 확인: 창이 만료된 뒤 얻은 카운터는 증거로 쓰지 않음 (늦게 깬 잠 포함)
            {
                return $"no load evidence, {MiB(read)} MiB in {(int)StageStreamRule.EvidenceWindow.TotalSeconds} s";
            }

            if (StageStreamRule.LoadEvidence(read))
            {
                return null;
            }

            if (OtherGameQueued(g))
            {
                return "another game started";
            }

            _env.Sleep(TimeSpan.FromSeconds(1), stop);
        }
    }

    // 스트림 종료 때의 한 줄: 낸 묶음 수, 요청·받아들인 바이트, 걸린 시간, 낮은 I/O 우선순위로 보낸 시간, 그 구간에 게임 드라이브에 대기열이 있던 표본의 비율과 읽기 한 번의 평균 지연 (플레이 기록이 이미 1초마다 읽은 디스크 값의 집계),
    // 메모리 여유가 돌아올 때까지 기다린 묶음 수와 그 시간, 스트림 동안의 최소 여유 (조회한 적이 없으면 none).
    internal static string StreamReadsLine(string game, ServiceReadPath path, long batches, long requested, long accepted, long sparseBuffered, double seconds, double lowSeconds, DiskTally disk, StreamHeadroom? headroom = null) =>
        FormattableString.Invariant($"{game}: {(path == ServiceReadPath.StageStream ? "stage" : "start")} stream reads: {batches} batches, requested {requested / 1_048_576d:F1} MiB, accepted {accepted / 1_048_576d:F1} MiB, sparse buffered {sparseBuffered / 1_048_576d:F1} MiB in {seconds:F1} s, low I/O priority {lowSeconds:F1} s; {disk.Text()}; memory headroom held {headroom?.HeldBatches ?? 0} batches {headroom?.HeldSeconds ?? 0:F1} s, min {(headroom?.MinBytes is { } min ? $"{min / 1_048_576d:F0} MiB" : "none")}");

    // 시작 스트림·단계 스트림 공통 본체: 읽기 카운터를 1 초마다 표본해 경계·상한·양보를 판정하고, 묶음마다 메모리 게이트와 우선순위를 적용합니다.
    private StreamOutcome RunStream(GameSettings g, DiskReadCoordinator.Lease? lease, CancellationToken stop, StreamSpec spec)
    {
        using var activity = ServiceActivity.Begin(ServiceOp.Stream); // 초 단위 기록의 동작 표시
        CancellationToken streamStop = lease?.Token ?? stop;
        int capSeconds = Math.Max(0, _env.StartStreamCapSeconds());
        long startedAt = _env.Timestamp();
        long lastSampleAt = startedAt;
        long processReadBytes = 0;
        var previousReads = new Dictionary<int, long>();
        var boundary = new RuntimeLoadBoundary();
        int boundaryStopped = 0;
        int capStopped = 0;
        string? otherStop = null;
        int wantLow = spec.StartLow ? 1 : 0;
        bool low = spec.StartLow;
        DateTimeOffset? quietSince = null;
        IReadOnlyList<ReadOp>? streamOps = spec.Ops is null ? null : StageStreamPlans.Split(spec.Ops); // 한 묶음이 64 MiB 를 크게 넘지 않게 잘라, 우선순위 전환과 메모리 게이트가 묶음마다 듣게 함
        long remainingBytes = streamOps is null ? long.MaxValue : streamOps.Sum(o => o.Length);
        ReadYield? readYield = spec.StartLow ? new ReadYield(_env.SetReadIoLow, true) : null;
        object sampleLock = new();
        ServiceReadCounts? counts = LoadPhases?.ServiceReads.CountsOf(g.Name); // 시작 때 한 번 얻음: 묶음·조각마다는 원자적 더하기만
        DiskTally tallyBefore = LoadPhases?.TallyOf(g.Name) ?? default;
        long batches = 0;
        long requestedBytes = 0;
        long acceptedBytes = 0;
        long sparseBytes = 0;
        long fragmentBase = 0; // 지금 조각 호출에서 앞 조각까지 받아들여진 바이트
        var headroom = new StreamHeadroom(_env.Lists, _env.Timestamp);
        bool lookupFailedLogged = false;
        using var issueStop = CancellationTokenSource.CreateLinkedTokenSource(streamStop);

        IReadOnlyList<(int Pid, string Exe, long ReadBytes)> Counters() => spec.BoundaryPid is not null
            ? _env.ReadCountersWithExeNames(g.ExeNames)
            : [.. _env.ReadCounters(g.ExeNames).Select(c => (c.Pid, string.Empty, c.ReadBytes))];

        void StopFor(string reason)
        {
            _ = Interlocked.CompareExchange(ref otherStop, reason, null);
        }

        try
        {
            foreach ((int pid, _, long bytes) in Counters())
            {
                previousReads[pid] = bytes;
            }
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            _log.Warn($"{g.Name}: start stream read counters unavailable ({e.GetType().Name})");
        }

        _ = boundary.Update(0, 0);

        void SampleReadCounters()
        {
            bool shouldCancel = false;
            bool sampleFailed = false;
            lock (sampleLock)
            {
                if (issueStop.IsCancellationRequested)
                {
                    return;
                }

                long timestamp = _env.Timestamp();
                TimeSpan elapsed = Stopwatch.GetElapsedTime(startedAt, timestamp);
                if (capSeconds > 0 && elapsed >= TimeSpan.FromSeconds(capSeconds))
                {
                    Volatile.Write(ref capStopped, 1);
                    shouldCancel = true;
                }
                else if (Stopwatch.GetElapsedTime(lastSampleAt, timestamp) >= TimeSpan.FromSeconds(1))
                {
                    try
                    {
                        long gameDelta = 0;
                        foreach ((int pid, _, long bytes) in Counters())
                        {
                            long delta = previousReads.TryGetValue(pid, out long previous) ? (bytes >= previous ? bytes - previous : bytes) : bytes;
                            gameDelta = checked(gameDelta + Math.Max(0, delta));
                            if (spec.BoundaryPid is null || spec.BoundaryPid == pid)
                            {
                                processReadBytes = checked(processReadBytes + Math.Max(0, delta));
                            }

                            previousReads[pid] = bytes;
                        }

                        if (boundary.Update((long)elapsed.TotalMilliseconds, processReadBytes) && !spec.IgnoreBoundary) // 로드 구간 목록은 첫 휴지에서 멈추지 않음 (목록 끝·시간 상한에서 멈춤)
                        {
                            Volatile.Write(ref boundaryStopped, 1);
                            shouldCancel = true;
                        }

                        if (readYield is not null)
                        {
                            (low, quietSince) = StageStreamRule.NextPriority(low, quietSince, StageStreamRule.WantLow(gameDelta, Stopwatch.GetElapsedTime(lastSampleAt, timestamp).TotalSeconds), _now());
                            Volatile.Write(ref wantLow, low ? 1 : 0);
                        }
                    }
                    catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
                    {
                        sampleFailed = true;
                    }

                    lastSampleAt = timestamp;
                }
            }

            if (sampleFailed)
            {
                _log.Warn($"{g.Name}: start stream read counter sample failed");
            }

            if (shouldCancel)
            {
                issueStop.Cancel();
            }
        }

        async Task SampleLoop(CancellationToken token)
        {
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                SampleReadCounters();
            }
        }

        bool LeaseValid() => !issueStop.IsCancellationRequested && (lease?.IsValid ?? !stop.IsCancellationRequested);

        bool MemoryShort()
        {
            if (!spec.MemoryGate)
            {
                return false;
            }

            try
            {
                if (!StageStreamRule.MemoryStop(_env.AvailableMib(), StageStreamRule.ReserveMib(g), Math.Min(StageStreamPlans.GateBatchBytes, Interlocked.Read(ref remainingBytes))))
                {
                    return false;
                }
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
            {
                return false; // 가용 메모리를 읽지 못하면 게이트 없음 (예열의 다른 경로와 같음)
            }

            StopFor("memory reserve");
            return true;
        }

        // 다음 묶음이 메모리에 있는 페이지를 밀어내지 않고 들어올 여유가 없는가 (조회 실패도 여유 없음). 로그는 스트림 하나에 한 번.
        bool HeadroomShort()
        {
            long next = Math.Min(StageStreamPlans.GateBatchBytes, Interlocked.Read(ref remainingBytes));
            if (!spec.MemoryGate || next <= 0) // 다 읽은 뒤의 마지막 확인은 기다리지 않음
            {
                return false;
            }

            bool held = headroom.Short(next);
            if (held && headroom.LookupFailed && !lookupFailedLogged)
            {
                lookupFailedLogged = true;
                _log.Warn($"{g.Name}: {(spec.ReadPath == ServiceReadPath.StageStream ? "stage" : "start")} stream memory lists unavailable, no batch is issued until they can be read");
            }

            return held;
        }

        // 묶음을 내도 되는가. 메모리 여유가 모자라면 그 묶음은 내지 않고 1 초마다 (표본 주기) 다시 보며, 시간 상한·로딩 경계·양보로 끝날 때까지 기다립니다.
        bool CanIssue()
        {
            long heldAt = 0;
            bool held = false;
            try
            {
                while (true)
                {
                    if (issueStop.IsCancellationRequested || !(lease?.IsValid ?? !stop.IsCancellationRequested))
                    {
                        return false;
                    }

                    SampleReadCounters();
                    if (!LeaseValid() || MemoryShort())
                    {
                        return false;
                    }

                    if (spec.YieldToQueuedGame && OtherGameQueued(g)) // 긴 목록이 디스패처를 붙잡고 다른 게임의 시작을 막지 않게
                    {
                        StopFor("another game started");
                        return false;
                    }

                    if (!HeadroomShort())
                    {
                        break;
                    }

                    if (!held)
                    {
                        (held, heldAt) = (true, _env.Timestamp());
                    }

                    _env.Sleep(TimeSpan.FromSeconds(1), issueStop.Token);
                }
            }
            finally
            {
                if (held)
                {
                    headroom.NoteHeld(Stopwatch.GetElapsedTime(heldAt, _env.Timestamp()).TotalSeconds);
                }
            }

            _ = readYield?.Apply(Volatile.Read(ref wantLow) != 0, _now()); // 읽는 스레드에서만 부름
            return true;
        }

        void Noted(string stage, long bytes, bool isData)
        {
            lease?.NoteReadIssue(stage, bytes, isData);
            if (counts is null)
            {
                return;
            }

            if (stage == PrefetchStreamer.BatchStage)
            {
                _ = Interlocked.Increment(ref batches); // 요청은 묶음을 내는 때에 셈
                _ = Interlocked.Add(ref requestedBytes, bytes);
                counts.AddStream(spec.ReadPath, bytes, 0);
            }
            else if (stage == ZoneStreamer.SparseBufferedStage)
            {
                _ = Interlocked.Add(ref sparseBytes, bytes); // 스파스 파일의 버퍼드 읽기는 선읽기가 받아들인 양이 아니라 디스크에서 읽은 것으로 셈
                counts.AddDisk(spec.ReadPath, bytes);
            }
            else if (ZoneStreamer.TryFragmentProgress(stage, out bool completed, out long progress))
            {
                if (completed && progress > fragmentBase)
                {
                    _ = Interlocked.Add(ref acceptedBytes, progress - fragmentBase); // 받아들여진 양은 조각이 끝나는 때에 셈 (거절된 조각은 progress 가 늘지 않아 셈하지 않음)
                    counts.AddStream(spec.ReadPath, 0, progress - fragmentBase);
                }

                fragmentBase = progress;
            }
        }

        lease?.NoteReadIssue("start stream planning", 0, false);
        using var sampleStop = new CancellationTokenSource();
        Task sampleTask = Task.Run(() => SampleLoop(sampleStop.Token));
        StreamStats r;
        bool complete;
        long progressBytes = 0;
        double elapsedSeconds;
        bool cancelled = false;
        bool boundaryA;
        bool capA;
        string? otherA;
        try
        {
            try
            {
                try
                {
                    void Progress(long done, long total)
                    {
                        Interlocked.Exchange(ref progressBytes, done);
                        Interlocked.Exchange(ref remainingBytes, Math.Max(0, total - done));
                    }

                    if (spec.Planner is { } planner)
                    {
                        IReadOnlyList<ReadOp>? planned = planner(issueStop.Token); // 원본처럼 시계·표본·상한 취소가 돌기 시작한 뒤에 목록을 만듦
                        streamOps = planned is null ? null : StageStreamPlans.Split(planned);
                        Interlocked.Exchange(ref remainingBytes, streamOps is null ? long.MaxValue : streamOps.Sum(o => o.Length));
                    }

                    (r, complete) = streamOps is null
                        ? _streamer.StreamCoordinated(g, issueStop.Token, Progress, CanIssue, Noted)
                        : _streamer.StreamOpsCoordinated(g, streamOps, issueStop.Token, Progress, CanIssue, Noted);
                }
                catch (OperationCanceledException) when (Volatile.Read(ref boundaryStopped) != 0 || Volatile.Read(ref capStopped) != 0 || Volatile.Read(ref otherStop) is not null)
                {
                    r = new StreamStats(Interlocked.Read(ref progressBytes), 0, 0, Stopwatch.GetElapsedTime(startedAt, _env.Timestamp()).TotalSeconds, Aborted: true);
                    complete = false;
                }
            }
            finally
            {
                sampleStop.Cancel(); // 원본처럼 표본 작업을 멈추고 합류한 뒤에 플래그를 읽어 종료 사유를 정함 (진행 중이던 표본이 경계·상한을 세울 수 있음)
                try
                {
                    sampleTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    // 표본 추출은 스트림과 함께 멈췄습니다.
                }
            }

            elapsedSeconds = Stopwatch.GetElapsedTime(startedAt, _env.Timestamp()).TotalSeconds;
            (boundaryA, capA, otherA) = (Volatile.Read(ref boundaryStopped) != 0, Volatile.Read(ref capStopped) != 0, Volatile.Read(ref otherStop)); // 경계·상한 플래그를 먼저 읽고 취소를 판정
            if (!boundaryA && !capA && otherA is null)
            {
                cancelled = !complete || r.Aborted || streamStop.IsCancellationRequested || !CanIssue();
                otherA = Volatile.Read(ref otherStop); // 마지막 확인 중 메모리 게이트가 닫혔으면 사유만 덧붙임
            }
        }
        finally
        {
            _ = readYield?.Finish(_now()); // 낮음이 남지 않게 보통으로 되돌림 (읽는 스레드)
        }

        double mbPerSecond = elapsedSeconds > 0 ? r.Bytes / (1_000_000d * elapsedSeconds) : 0;
        if (LoadPhases is { } board)
        {
            _log.Info(StreamReadsLine(g.Name, spec.ReadPath, Interlocked.Read(ref batches), Interlocked.Read(ref requestedBytes), Interlocked.Read(ref acceptedBytes), Interlocked.Read(ref sparseBytes), elapsedSeconds, readYield?.Seconds ?? 0, board.TallyOf(g.Name).Since(tallyBefore), headroom));
        }

        return new StreamOutcome(r, elapsedSeconds, mbPerSecond, boundaryA, capA, otherA, cancelled, readYield?.Seconds ?? 0, boundary);
    }
}
