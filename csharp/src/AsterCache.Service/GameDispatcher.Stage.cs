using System.Diagnostics;
using System.Globalization;
using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;

namespace AsterCache.Service;

/// 시작 스트림의 단계 인지 부분: 같은 게임의 이후 실행 파일 시작마다 그 단계가 읽는 블록을 양보·메모리 게이트 아래에서 흘리고,
/// 첫 실행 파일의 스트림은 첫 단계만 흘린 뒤 다음 단계를 낮은 우선순위로 미리 흘립니다. 디스패처가 직렬이라 모두 Handle 스레드에서 돕니다.
public sealed partial class GameDispatcher
{
    private readonly Dictionary<string, DateTimeOffset> _lastStage = new(StringComparer.OrdinalIgnoreCase); // (게임, 실행 파일) -> 마지막 단계 스트림 시각

    /// 스트림 한 번의 구성. Ops 가 null 이면 시작 목록 전체 (IGameStreamer.StreamCoordinated), BoundaryPid 가 있으면 그 프로세스의 읽기만 경계 판정에 셉니다.
    // Planner: 목록 계획을 시계·표본·상한 취소 범위 안에서 만들게 넘김 (원본은 스트리머가 스트림 안에서 목록을 만들었음)
    private sealed record StreamSpec(IReadOnlyList<ReadOp>? Ops, int? BoundaryPid, bool StartLow, bool MemoryGate, Func<CancellationToken, (IReadOnlyList<ReadOp>? Ops, StageFirstPlan? Ahead)>? Planner = null);

    private sealed record AheadOutcome(string Exe, long Bytes, double Seconds, bool Complete, string? Reason);

    private sealed record StreamOutcome(StreamStats Stats, bool Complete, double Seconds, double MbPerSecond, bool BoundaryStopped, bool CapStopped, string? OtherStop, bool Cancelled,
        double YieldedSeconds, RuntimeLoadBoundary Boundary, AheadOutcome? Ahead);

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

    // 대기열 어디에든 다른 게임의 시작이 있는가 (그 시작을 먼저 처리하려고 미리 흘리기·증거 대기를 끝냄)
    private bool OtherGameQueued(GameSettings g) => _env.OtherGameQueued(g.Name);

    private static string MiB(long bytes) => (bytes / 1_048_576d).ToString("F0", CultureInfo.CurrentCulture);

    // 첫 실행 파일 (또는 같은 시작 무리) 의 시작 스트림. 단계 정보가 있으면 첫 단계만 흘린 뒤 다음 단계를 미리 흘립니다.
    private bool StreamFirst(GameSettings g, ProcessStart start, DiskReadCoordinator.Lease? lease, CancellationToken stop)
    {
        bool stage = _env.StageStreamEnabled();
        StageFirstPlan? plan = null;
        (IReadOnlyList<ReadOp>? Ops, StageFirstPlan? Ahead) Plan(CancellationToken token)
        {
            plan = _streamer.PlanFirstStage(g, token);
            IReadOnlyList<ReadOp>? ops = plan?.Ops ?? _streamer.StartOps(g, token); // 메모리 게이트가 걸리면 단계 정보가 없어도 같은 잘린 목록으로 흘림
            return (ops, plan is { AheadOps.Count: > 0 } ? plan : null);
        }

        StreamOutcome o = RunStream(g, start, lease, stop, new StreamSpec(null, null, false, stage, stage ? Plan : null));
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

        string tail = string.Empty;
        if (o.Ahead is { } ahead)
        {
            tail = $"; ahead stage {ahead.Exe} {MiB(ahead.Bytes)} MiB in {ahead.Seconds:F1} s ({(ahead.Complete ? "completed" : "stopped, " + ahead.Reason)}), yielded {o.YieldedSeconds:F1} s";
        }

        if (plan is { VolatileSkippedBytes: > 0 })
        {
            tail += $"; skipped volatile {MiB(plan.VolatileSkippedBytes)} MiB";
        }

        _log.Info($"{g.Name}: streamed {o.Stats.Bytes} bytes in {o.Stats.Requests} requests, {o.Stats.Errors} errors, {o.Seconds:F3} s ({mbPerSecond:F1} MB/s){tail}");
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
            StageLaterPlan plan = _streamer.PlanLaterStage(g, exe, stop);
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

            using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(g.OriginRoot, "stage stream", stop, g.OriginRoot, allowWhileYielded: true, preserveGameKey: g.Name);
            if (_coordinator is not null && lease is null)
            {
                Skip("disk yielded", "disk yielded");
                return false;
            }

            _lastStage[key] = now;
            StreamOutcome o = RunStream(g, start, lease, stop, new StreamSpec(plan.Ops, start.Pid, true, true, null));
            string? reason = o.BoundaryStopped ? "load boundary" : o.CapStopped ? "cap" : o.OtherStop ?? (o.Cancelled ? "cancelled" : null);
            string skipped = plan.VolatileSkippedBytes > 0 ? $"; skipped volatile {MiB(plan.VolatileSkippedBytes)} MiB" : string.Empty;
            _log.Info($"{g.Name}: stage stream {exe} {MiB(o.Stats.Bytes)} MiB in {o.Seconds:F1} s ({o.MbPerSecond:F1} MB/s, {(reason is null ? "completed" : "stopped, " + reason)}), yielded {o.YieldedSeconds:F1} s{skipped}");
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

    // 시작 스트림·단계 스트림 공통 본체: 읽기 카운터를 1 초마다 표본해 경계·상한·양보·다음 단계 시작을 판정하고, 묶음마다 메모리 게이트와 우선순위를 적용합니다.
    private StreamOutcome RunStream(GameSettings g, ProcessStart start, DiskReadCoordinator.Lease? lease, CancellationToken stop, StreamSpec spec)
    {
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
        int aheadActive = 0;
        int nextStageSeen = 0;
        int wantLow = spec.StartLow ? 1 : 0;
        bool low = spec.StartLow;
        DateTimeOffset? quietSince = null;
        IReadOnlyList<ReadOp>? streamOps = spec.Ops is null ? null : StageStreamPlans.Split(spec.Ops); // 한 묶음이 64 MiB 를 크게 넘지 않게 잘라, 우선순위 전환과 메모리 게이트가 묶음마다 듣게 함
        long remainingBytes = streamOps is null ? long.MaxValue : streamOps.Sum(o => o.Length);
        bool needNames = spec.BoundaryPid is not null; // 첫 스트림은 미리 흘리기를 시작할 때에야 이름 있는 카운터로 바꿈 (그 전에는 원본과 같은 카운터 조회)
        string? aheadExe = null;
        HashSet<int> initialPids = [];
        StageFirstPlan? aheadPlan = null;
        ReadYield? readYield = spec.StartLow ? new ReadYield(_env.SetReadIoLow, true) : null;
        object sampleLock = new();
        using var issueStop = CancellationTokenSource.CreateLinkedTokenSource(streamStop);

        IReadOnlyList<(int Pid, string Exe, long ReadBytes)> Counters() => needNames
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
                        foreach ((int pid, string exe, long bytes) in Counters())
                        {
                            long delta = previousReads.TryGetValue(pid, out long previous) ? (bytes >= previous ? bytes - previous : bytes) : bytes;
                            gameDelta = checked(gameDelta + Math.Max(0, delta));
                            if (spec.BoundaryPid is null || spec.BoundaryPid == pid)
                            {
                                processReadBytes = checked(processReadBytes + Math.Max(0, delta));
                            }

                            if (aheadExe is not null && !initialPids.Contains(pid) && string.Equals(exe, aheadExe, StringComparison.OrdinalIgnoreCase))
                            {
                                Volatile.Write(ref nextStageSeen, 1); // 다음 단계 프로세스가 떴음: 미리 흘리기는 거기서 멈추고 그 시작을 처리
                            }

                            previousReads[pid] = bytes;
                        }

                        if (Volatile.Read(ref aheadActive) == 0 && boundary.Update((long)elapsed.TotalMilliseconds, processReadBytes))
                        {
                            Volatile.Write(ref boundaryStopped, 1);
                            shouldCancel = true;
                        }

                        if (readYield is not null)
                        {
                            (low, quietSince) = StageStreamRule.NextPriority(low, quietSince, StageStreamRule.WantLow(gameDelta, Stopwatch.GetElapsedTime(lastSampleAt, timestamp).TotalSeconds), _now());
                            Volatile.Write(ref wantLow, low ? 1 : 0);
                        }

                        if (Volatile.Read(ref aheadActive) != 0)
                        {
                            if (Volatile.Read(ref nextStageSeen) != 0)
                            {
                                StopFor("next stage started");
                                shouldCancel = true;
                            }
                            else if (OtherGameQueued(g))
                            {
                                StopFor("another game started");
                                shouldCancel = true;
                            }
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

        bool CanIssue()
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

            _ = readYield?.Apply(Volatile.Read(ref wantLow) != 0, _now()); // 읽는 스레드에서만 부름
            return true;
        }

        void Noted(string stage, long bytes, bool isData) => lease?.NoteReadIssue(stage, bytes, isData);

        CancellationTokenSource? sampleStop = null;
        Task? sampleTask = null;

        void StartSampler()
        {
            sampleStop = new CancellationTokenSource();
            CancellationToken token = sampleStop.Token;
            sampleTask = Task.Run(() => SampleLoop(token));
        }

        void StopSampler()
        {
            if (sampleStop is null)
            {
                return;
            }

            sampleStop.Cancel();
            try
            {
                sampleTask!.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // 표본 추출은 스트림과 함께 멈췄습니다.
            }

            sampleStop.Dispose();
            sampleStop = null;
        }

        lease?.NoteReadIssue("start stream planning", 0, false);
        StartSampler();
        StreamStats r;
        bool complete;
        long progressBytes = 0;
        AheadOutcome? ahead = null;
        double elapsedSeconds;
        bool stoppedA;
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
                        (IReadOnlyList<ReadOp>? planned, aheadPlan) = planner(issueStop.Token); // 원본처럼 시계·표본·상한 취소가 돌기 시작한 뒤에 목록을 만듦
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
                StopSampler(); // 원본처럼 표본 작업을 멈추고 합류한 뒤에 플래그를 읽어 종료 사유를 정함 (진행 중이던 표본이 경계·상한을 세울 수 있음)
            }

            elapsedSeconds = Stopwatch.GetElapsedTime(startedAt, _env.Timestamp()).TotalSeconds;
            (boundaryA, capA, otherA) = (Volatile.Read(ref boundaryStopped) != 0, Volatile.Read(ref capStopped) != 0, Volatile.Read(ref otherStop)); // 경계·상한 플래그를 먼저 읽고 취소를 판정 (미리 흘리기가 첫 스트림의 결과를 바꾸지도 않음)
            if (!boundaryA && !capA && otherA is null)
            {
                cancelled = !complete || r.Aborted || streamStop.IsCancellationRequested || !CanIssue();
                otherA = Volatile.Read(ref otherStop); // 마지막 확인 중 메모리 게이트가 닫혔으면 사유만 덧붙임
            }

            stoppedA = boundaryA || capA || otherA is not null;
            if (!stoppedA && !cancelled && aheadPlan is { } plan)
            {
                StartSampler(); // 미리 흘리는 동안에도 상한·다음 단계·대기열을 표본으로 봄
                try
                {
                    ahead = StreamAhead(plan);
                }
                finally
                {
                    StopSampler();
                }
            }
        }
        finally
        {
            _ = readYield?.Finish(_now()); // 낮음이 남지 않게 보통으로 되돌림 (읽는 스레드)
        }

        double mbPerSecond = elapsedSeconds > 0 ? r.Bytes / (1_000_000d * elapsedSeconds) : 0;
        return new StreamOutcome(r, complete, elapsedSeconds, mbPerSecond, boundaryA, capA, otherA, cancelled, readYield?.Seconds ?? 0, boundary, ahead);

        // 첫 단계를 다 흘린 뒤: 경계 판정 없이 다음 단계를 낮은 우선순위에서 이어 흘림 (같은 대여, 같은 상한 시계). 다음 단계 프로세스가 보이거나 다른 게임의 시작이 대기열에 들어오면 멈춤.
        AheadOutcome StreamAhead(StageFirstPlan plan)
        {
            lock (sampleLock)
            {
                IReadOnlyList<(int Pid, string Exe, long ReadBytes)> named;
                try
                {
                    named = _env.ReadCountersWithExeNames(g.ExeNames); // 이름 있는 카운터로 바꾸며 기준을 다시 잡음
                }
                catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
                {
                    return new AheadOutcome(plan.AheadExe, 0, 0, false, "counters unavailable");
                }

                if (named.Any(c => string.Equals(c.Exe, plan.AheadExe, StringComparison.OrdinalIgnoreCase)))
                {
                    return new AheadOutcome(plan.AheadExe, 0, 0, false, "next stage started"); // 첫 단계를 흘리는 동안 이미 떴음
                }

                initialPids = [.. named.Select(c => c.Pid)];
                aheadExe = plan.AheadExe;
                needNames = true;
                (low, quietSince) = (true, null); // 낮음으로 시작
                Volatile.Write(ref wantLow, 1);
                Interlocked.Exchange(ref remainingBytes, plan.AheadOps.Sum(o => o.Length));
                readYield = new ReadYield(_env.SetReadIoLow, true);
                Volatile.Write(ref aheadActive, 1);
            }

            long aheadStart = _env.Timestamp();
            long aheadProgress = 0;
            StreamStats ar;
            bool acomplete;

            try
            {
                (ar, acomplete) = _streamer.StreamOpsCoordinated(g, StageStreamPlans.Split(plan.AheadOps), issueStop.Token, (done, total) =>
                {
                    Interlocked.Exchange(ref aheadProgress, done);
                    Interlocked.Exchange(ref remainingBytes, Math.Max(0, total - done));
                }, CanIssue, Noted);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref capStopped) != 0 || Volatile.Read(ref otherStop) is not null)
            {
                (ar, acomplete) = (new StreamStats(Interlocked.Read(ref aheadProgress), 0, 0, 0, Aborted: true), false);
            }

            double seconds = Stopwatch.GetElapsedTime(aheadStart, _env.Timestamp()).TotalSeconds;
            if (acomplete && !ar.Aborted && Volatile.Read(ref capStopped) == 0 && Volatile.Read(ref otherStop) is null)
            {
                return new AheadOutcome(plan.AheadExe, ar.Bytes, seconds, true, null);
            }

            string reason = Volatile.Read(ref capStopped) != 0 ? "cap" : Volatile.Read(ref otherStop) ?? "cancelled";
            return new AheadOutcome(plan.AheadExe, ar.Bytes, seconds, false, reason);
        }
    }
}
