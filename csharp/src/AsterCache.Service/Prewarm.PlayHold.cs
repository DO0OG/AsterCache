using System.Collections.Concurrent;
using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 가 게임이 실행되는 동안 유지 목록 (중요도 순서) 을 서비스의 매핑 뷰로 붙들어 두는 곳 (설정 memory.keep_hold).
/// 대기 목록을 주기마다 다시 만지는 방식 (Prewarm.PlayTouch) 은 만진 페이지가 대기 목록에 있는 동안 게임의 읽기나 다른 프로그램의 메모리 사용에 밀려나므로, 작업 집합에 올려 두고 뷰를 닫지 않아 대기 목록에서 빼 둡니다 (ViewHold).
/// 붙드는 때: 시작 만지기가 끝난 직후. 가변 파일·스파스 파일·순위 목록을 만든 뒤 바뀐 파일은 붙들지 않고, 안티치트 게이트가 고정을 막는 게임과 가변 파일 정보가 없는 게임 (원시 트레이스가 없음) 에서는 하지 않습니다 (그 게임은 지금의 다시 만지기로 남음).
/// 붙드는 동안: 몇 초마다 가용 메모리가 하한 아래인지 봐서 덜 중요한 쪽부터 내놓고, 2 분마다 붙든 범위를 다시 읽어 메모리 관리자가 줄여 간 조각을 하드 폴트로 확인합니다. 게임이 끝나면 모두 내놓고 닫습니다.
public sealed partial class Prewarmer
{
    /// 붙드는 양의 상한: 5 GiB. 근거: 2026-11-21 Call of Duty 플레이의 블록 단위 디스크 읽기 기록에서 순위 앞 2·3·4·5·6 GiB 가 경기 중 캐시 경유 읽기 지연의 32·39·42·47·49% 를 덮었고 5 GiB 를 넘기면 거의 늘지 않았습니다 (한 플레이의 근사 값).
    public const long HoldTargetCapBytes = 5L << 30;

    /// 붙든 뒤에도 가용 메모리가 남아 있어야 하는 하한의 최솟값 (MiB): 하한은 max(이 값, prewarm.min_free_ram_mib).
    public const long HoldMinFloorMib = 2048;

    /// 가용 메모리가 하한 아래인지 보는 간격 (GlobalMemoryStatusEx 한 번, 비용은 거의 없음).
    public static readonly TimeSpan HoldWatchEvery = TimeSpan.FromSeconds(5);

    /// 붙들기와 같은 일을 한 번에 하는 단위: 이만큼 읽을 때마다 하드 폴트를 조회하고 (경계), 가용 메모리가 하한에 닿았는지 봅니다.
    public const long HoldBatchBytes = 16L << 20;

    /// 한 번의 붙들기 (또는 점검의 다시 채우기, 빠진 조각을 건너뛰는 다시 만지기) 에서 디스크에서 읽는 양의 상한: 32 MiB (StandbyTouch.DiskReadStopBytes 와 같은 값). 넘으면 그 회차는 거기서 끝납니다.
    public const long KeepDiskReadCapBytes = 32L << 20;

    /// 하한 아래일 때 부족한 양에 더해 내놓는 여유 (MiB): 내놓은 뒤 바로 다시 하한 아래가 되지 않게.
    public const long HoldReleaseMarginMib = 256;

    /// 붙들 목표가 이보다 작으면 붙들지 않습니다.
    public const long HoldMinTargetBytes = 64L << 20;

    private readonly ConcurrentDictionary<string, long> _holdHeld = new(StringComparer.OrdinalIgnoreCase); // 게임별로 지금 붙든 바이트
    private readonly object _touchSerial = new(); // 이 프로세스의 하드 폴트 누계로 경계를 판정하는 일 (시작 만지기, 붙들기, 점검) 이 겹치지 않게

    /// 지금 붙든 양 (MiB): 플레이 기록의 가용 메모리에 더해 기록합니다 (붙들어서 줄어 보인 가용 메모리가 게임의 몫으로 배워져 다음 예열 예산이 줄지 않게).
    internal long KeepHeldMib => _holdHeld.Values.Sum() >> 20;

    private enum HoldOutcome
    {
        Retry, // 일시적인 사유로 붙들지 못함: 다음 회차에 다시 시도
        No, // 붙들지 않음 (설정·안티치트·정보 없음·메모리에 없음): 지금의 다시 만지기로 남음
        Done, // 붙들고 게임이 끝날 때까지 돌았음
    }

    private GameSettings? GameOf(string name) => _games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    private static long HoldFloorMib(GameSettings game) => Math.Max(HoldMinFloorMib, game.Prewarm.MinFreeRamMib);

    /// 붙들 목표 (바이트): 기존 예산 (가용 메모리의 PlayTouchAvailableFraction), 붙든 뒤에도 가용 메모리가 하한 위에 남는 양, HoldTargetCapBytes 가운데 가장 작은 것.
    internal static long HoldTargetBytes(long availMib, long floorMib) => Math.Min(HoldTargetCapBytes, Math.Min((long)(availMib * PlayTouchAvailableFraction) << 20, Math.Max(0, availMib - floorMib) << 20));

    // 읽는 스레드의 I/O 우선순위와 CPU 우선순위를 이 범위 안에서만 낮춥니다 (게임의 읽기와 CPU 를 다투지 않게). 스레드 배경 모드는 메모리 우선순위까지 낮추므로 쓰지 않습니다.
    private sealed class LowPriorityReader : IDisposable
    {
        private readonly PrewarmEnv _env;
        private readonly Thread _thread = Thread.CurrentThread;
        private readonly ThreadPriority _before;

        public LowPriorityReader(PrewarmEnv env)
        {
            _env = env;
            _before = _thread.Priority;
            _thread.Priority = ThreadPriority.BelowNormal;
            _ = env.SetReadIoLow(true);
        }

        public void Dispose()
        {
            _ = _env.SetReadIoLow(false);
            _thread.Priority = _before;
        }
    }

    private void NoteHoldSkip(GameSettings game, string exe, string detail, ref string? last)
    {
        if (detail != last)
        {
            _log.Info($"{game.Name}: cache hold while playing skipped ({exe}: {detail})");
            last = detail;
        }
    }

    // ---- 목록을 배치 단위로 걸으며 빠진 조각을 건너뜀 (붙들기와 지금의 다시 만지기가 함께 씀) ----

    /// 한 배치를 처리한 결과: 시작한 범위 수, 만진 바이트, 디스크에서 읽힌 바이트, 디스크 읽기가 섞여 경계로 보고 멈췄는가.
    private readonly record struct WalkStep(int Processed, long Consumed, long Disk, bool Boundary);

    private sealed record WalkResult(List<ReadOp> Kept, long Disk, int Boundaries, long SkippedBytes, long LeftBytes, bool CapReached, bool Stopped);

    // 목록을 중요한 쪽부터 HoldBatchBytes 씩 처리합니다. 경계를 만나면 그 파일의 이어지는 범위 (목록에서 바로 뒤따르는 같은 파일의 범위) 를 건너뛰고 다음 범위로 계속하며,
    // 디스크에서 읽은 양이 diskCap 을 넘으면 거기서 끝냅니다. mayContinue 가 거짓이 되면 (가용 메모리가 하한에 닿음) 멈춥니다.
    private static WalkResult Walk(IReadOnlyList<ReadOp> reads, long diskCap, CancellationToken ct, Func<bool> mayContinue, Func<IReadOnlyList<ReadOp>, WalkStep> step)
    {
        List<ReadOp> rest = [.. reads.Where(r => r.Length > 0)];
        List<ReadOp> kept = [];
        int next = 0;
        long disk = 0;
        long skipped = 0;
        int boundaries = 0;
        string? skipPath = null;
        bool capReached = false;
        bool stopped = false;
        while (next < rest.Count)
        {
            if (ct.IsCancellationRequested || !mayContinue())
            {
                stopped = true;
                break;
            }

            if (skipPath is not null)
            {
                for (; next < rest.Count && string.Equals(rest[next].Path, skipPath, StringComparison.OrdinalIgnoreCase); next++)
                {
                    skipped += rest[next].Length;
                }

                skipPath = null;
                continue;
            }

            List<ReadOp> batch = TakeBatch(rest, ref next);
            WalkStep s = step(batch);
            disk += s.Disk;
            if (s.Boundary)
            {
                boundaries++;
                kept.AddRange(PrefetchStreamer.Head(batch, s.Consumed));
                int started = Math.Clamp(s.Processed, 1, batch.Count);
                List<ReadOp> tail = batch.GetRange(started, batch.Count - started);
                skipped += Math.Max(0, batch.Sum(o => o.Length) - s.Consumed - tail.Sum(o => o.Length)); // 경계를 만난 범위의 읽지 않은 나머지
                rest.InsertRange(next, tail);
                skipPath = batch[started - 1].Path;
            }
            else
            {
                kept.AddRange(batch);
            }

            if (disk > diskCap)
            {
                capReached = true;
                break;
            }
        }

        return new WalkResult(kept, disk, boundaries, skipped, rest.Skip(next).Sum(o => o.Length), capReached, stopped);
    }

    // rest[next..] 에서 HoldBatchBytes 만큼 떼어 냅니다 (걸친 범위는 잘라 나머지를 남김).
    private static List<ReadOp> TakeBatch(List<ReadOp> rest, ref int next)
    {
        List<ReadOp> batch = [];
        long room = HoldBatchBytes;
        while (next < rest.Count && room > 0)
        {
            ReadOp op = rest[next];
            if (op.Length <= room)
            {
                batch.Add(op);
                room -= op.Length;
                next++;
            }
            else
            {
                batch.Add(new ReadOp(op.Path, op.Offset, room));
                rest[next] = new ReadOp(op.Path, op.Offset + room, op.Length - room);
                room = 0;
            }
        }

        return batch;
    }

    // 만진 바이트가 consumed 일 때 시작한 범위 수 (경계가 있던 범위까지).
    private static int RangesStarted(IReadOnlyList<ReadOp> batch, long consumed)
    {
        long before = 0;
        for (int i = 0; i < batch.Count; i++)
        {
            if (before >= consumed)
            {
                return Math.Max(1, i);
            }

            before += batch[i].Length;
        }

        return batch.Count;
    }

    // 지금의 다시 만지기 (대기 목록) 가 쓰는 걷기: 앞부분 하나의 경계에서 끝나지 않고 빠진 조각을 건너뛰며 목록 끝까지 (디스크 읽기 상한 안에서) 중요한 순서로 만지고, 메모리에 있던 범위를 돌려줍니다. memory.keep_hold 가 켜졌을 때만 씁니다.
    private (TouchResult Find, IReadOnlyList<ReadOp> Kept, string Note) KeepWalk(IReadOnlyList<ReadOp> reads, CancellationToken ct)
    {
        TouchResult total = new(0, 0, 0, 0, TouchEnd.Completed);
        WalkResult w = Walk(reads, KeepDiskReadCapBytes, ct, () => true, batch =>
        {
            TouchResult r = ThrottledTouch(batch, ct);
            total = new TouchResult(total.Bytes + r.Bytes, total.Seconds + r.Seconds, total.SlowPieces + r.SlowPieces, total.Errors + r.Errors, total.End == TouchEnd.Canceled ? total.End : r.End == TouchEnd.Canceled ? r.End : total.End,
                total.DiskBytes + r.DiskBytes, total.SkippedBytes + r.SkippedBytes, total.CpuSeconds + r.CpuSeconds, total.Maps + r.Maps, total.HardFaults + r.HardFaults, total.Reason == StopReason.None ? r.Reason : total.Reason,
                total.Checks + r.Checks, TouchErrors.Combine(total.ErrorKinds, r.ErrorKinds));
            bool boundary = r.End == TouchEnd.NeedsDiskReads;
            long consumed = boundary ? r.Bytes + r.SkippedBytes : batch.Sum(o => o.Length);
            return new WalkStep(boundary ? RangesStarted(batch, consumed) : batch.Count, consumed, r.DiskBytes, boundary);
        });
        TouchEnd end = total.End == TouchEnd.Canceled || ct.IsCancellationRequested ? TouchEnd.Canceled : w.Boundaries > 0 ? TouchEnd.NeedsDiskReads : TouchEnd.Completed;
        string note = w.Boundaries > 0 ? $", {w.Boundaries} missing stretches skipped ({w.SkippedBytes >> 20} MiB not touched){(w.CapReached ? ", disk read cap reached" : string.Empty)}" : string.Empty;
        return (total with { End = end }, w.Kept, note);
    }

    // ---- 붙들기 ----

    // 붙들기를 시작하고 게임이 끝날 때까지 돕니다. 붙들지 못하면 사유별로 Retry (다음 회차에 다시) 나 No (지금의 다시 만지기로 남음).
    // 붙들기 중의 예외는 이 기능만 끝내고 (붙든 것은 닫음) 서비스의 다른 동작과 플레이 기록은 멈추지 않습니다.
    private HoldOutcome HoldWhilePlaying(GameSettings started, string exe, KeepList keep, ref string? lastSkip)
    {
        IKeepHold? hold = null;
        try
        {
            hold = StartHold(started, exe, keep, ref lastSkip, out HoldOutcome outcome, out long floorMib, out long targetBytes);
            if (hold is null)
            {
                return outcome;
            }

            return KeepHeld(hold, started.Name, exe, floorMib, targetBytes);
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _log.Warn($"{started.Name}: cache hold while playing stopped: {e.GetType().Name}");
            return HoldOutcome.No; // 아래 finally 가 붙든 것을 닫음
        }
        finally
        {
            hold?.Dispose();
            _ = _holdHeld.TryRemove(started.Name, out _);
        }
    }

    private IKeepHold? StartHold(GameSettings started, string exe, KeepList keep, ref string? lastSkip, out HoldOutcome outcome, out long floorMib, out long targetBytes)
    {
        (outcome, floorMib, targetBytes) = (HoldOutcome.Retry, 0, 0);
        if (_env.OpenHold is not { } open)
        {
            outcome = HoldOutcome.No;
            return null;
        }

        if (!_solar.Enabled || Relocating(started.Name) || _solar.HasNothingToTouch(started.Name))
        {
            return null;
        }

        lock (_gate)
        {
            if (!(_last.ContainsKey(started.Name) || _warmAtServiceStart.Contains(started.Name) || _solar.HasMark(started.Name))) // 메모리에 둔 목록이 없음
            {
                return null;
            }
        }

        GameSettings game = GameOf(started.Name) ?? started;
        if (_pins.PinBlocker(game) is { } cheat)
        {
            NoteHoldSkip(game, exe, $"anti-cheat: {cheat}", ref lastSkip); // 고정이 건너뛰는 게임: 열린 뷰를 게임 실행 내내 들고 있는 일도 하지 않음
            outcome = HoldOutcome.No;
            return null;
        }

        if (_env.HoldExclusion(game) is not { } excluded)
        {
            NoteHoldSkip(game, exe, "no volatile file information yet, no trace was recorded", ref lastSkip);
            outcome = HoldOutcome.No;
            return null;
        }

        floorMib = HoldFloorMib(game);
        long availMib = _env.AvailableRamMib();
        targetBytes = HoldTargetBytes(availMib, floorMib);
        if (targetBytes < HoldMinTargetBytes)
        {
            NoteHoldSkip(game, exe, $"available {availMib} MiB leaves no room above the floor {floorMib} MiB", ref lastSkip);
            return null;
        }

        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "play hold", default, game.OriginRoot, allowWhileYielded: true, preserveGameKey: game.Name);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            NoteHoldSkip(game, exe, "disk yielded", ref lastSkip);
            return null;
        }

        lease?.NoteReadIssue("play hold", 0, false); // 양보 진단 로그에 이 단계가 보이게
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lease?.Token ?? default, _playStop.Token);
        using var low = new LowPriorityReader(_env);
        if (!RefreshKeepList(game, keep))
        {
            return null;
        }

        // 붙들 수 없는 파일을 뺍니다: 스파스 (붙들면 캐시에 있어도 디스크에서 다시 읽힘), 가변·순위 목록 뒤 바뀐 파일 (붙든 뷰가 있으면 크기 줄이기가 오류 1224 로 실패하고 캐시 없는 쓰기와 섞이면 낡은 내용이 남을 수 있음)
        var reasons = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        long sparseBytes = 0;
        long excludedBytes = 0;
        List<ReadOp> eligible = [];
        foreach (ReadOp op in keep.Ordered)
        {
            if (!reasons.TryGetValue(op.Path, out string? reason))
            {
                reasons[op.Path] = reason = _env.IsSparse(op.Path) ? "sparse" : excluded(op.Path);
            }

            if (reason is null)
            {
                eligible.Add(op);
            }
            else if (reason == "sparse")
            {
                sparseBytes += op.Length;
            }
            else
            {
                excludedBytes += op.Length;
            }
        }

        IReadOnlyList<ReadOp> reads = PrefetchStreamer.Head(eligible, targetBytes);
        long total = reads.Sum(o => o.Length);
        IKeepHold hold = open();
        try
        {
            using var activity = ServiceActivity.Begin(ServiceOp.Hold); // 초 단위 기록의 동작 표시
            long seconds0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double cpu = 0;
            int checks = 0;
            int errors = 0;
            long hardFaults = 0;
            long diskOnly = 0;
            long floor = floorMib;
            WalkResult walk;
            lock (_touchSerial)
            {
                hold.Rebase();
                walk = Walk(reads, KeepDiskReadCapBytes, linked.Token, () => _env.AvailableRamMib() - (HoldBatchBytes >> 20) >= floor, batch =>
                {
                    HoldBatchResult r = hold.Hold(batch, linked.Token);
                    (cpu, checks, errors, hardFaults, diskOnly) = (cpu + r.CpuSeconds, checks + r.Checks, errors + r.Errors, hardFaults + r.HardFaults, diskOnly + r.DiskBytes);
                    return new WalkStep(r.BoundaryIndex + 1, r.Bytes + r.SkippedBytes, r.DiskBytes, r.BoundaryIndex >= 0);
                });
            }

            double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(seconds0).TotalSeconds;
            if (linked.IsCancellationRequested)
            {
                hold.Dispose();
                outcome = HoldOutcome.Retry;
                return null; // 게임이 끝났거나 서비스가 멈춤: 호출한 쪽 루프가 곧 끝남
            }

            if (hold.HeldBytes <= 0)
            {
                _log.Info($"{game.Name}: cache hold while playing ({exe}): nothing of the {total >> 20} MiB list was in memory ({walk.Disk >> 20} MiB read from disk{ErrorsText(errors)}), left to the cache touching{HoldProbe(reads, total, linked.Token)}");
                hold.Dispose();
                outcome = HoldOutcome.No;
                return null;
            }

            string stopped = walk.Stopped ? $", stopped at the memory floor ({floorMib} MiB, available {_env.AvailableRamMib()} MiB)" : walk.CapReached ? ", disk read cap reached" : string.Empty;
            string missing = walk.Boundaries > 0 ? $", {walk.Boundaries} missing stretches skipped ({walk.SkippedBytes >> 20} MiB not held)" : string.Empty;
            string excludedText = sparseBytes + excludedBytes > 0 ? $", {excludedBytes >> 20} MiB in volatile or changed files and {sparseBytes >> 20} MiB in sparse files not held" : string.Empty;
            _log.Info($"{game.Name}: cache held while playing ({exe}): {hold.HeldBytes >> 20} MiB of {targetBytes >> 20} MiB target ({total >> 20} MiB list) in {hold.Files} files, {hold.Views} views, in {elapsed:F1} s (cpu {cpu:F1} s, {checks} checks), {walk.Disk >> 20} MiB read from disk ({hardFaults} hard faults){ErrorsText(errors)}{missing}{excludedText}{stopped}{(walk.Boundaries > 0 ? HoldProbe(reads, total, linked.Token) : string.Empty)}");
            _holdHeld[game.Name] = hold.HeldBytes;
            outcome = HoldOutcome.Done;
            return hold;
        }
        catch
        {
            hold.Dispose();
            throw;
        }
    }

    private static string ErrorsText(int errors) => errors > 0 ? $", {errors} errors" : string.Empty;

    // 표본 확인 (기록용): 메모리에 없는 조각이 있었을 때 목록 전체에서 고른 16 곳이 메모리에 있는지를 로그 꼬리 (앞에 쉼표) 로 남깁니다. 실패해도 빈 문자열.
    private string HoldProbe(IReadOnlyList<ReadOp> reads, long total, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<(ReadOp Op, long Offset)> picks = KeepProbe.Pick(reads);
            return picks.Count > 0 && _env.KeepProbeSamples([.. picks.Select(p => p.Op)], ct) is { } faults ? ", " + KeepProbe.Describe(picks, faults, total) : string.Empty;
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            return string.Empty;
        }
    }

    // ---- 붙든 동안 ----

    // 붙든 뒤 게임이 끝날 때까지: HoldWatchEvery 마다 가용 메모리가 하한 아래면 내놓고, PlayTouchEvery 마다 붙든 범위를 점검합니다. 끝나면 (호출한 쪽 finally 가) 붙든 것을 모두 내놓고 닫습니다.
    private HoldOutcome KeepHeld(IKeepHold hold, string name, string exe, long floorMib, long targetBytes)
    {
        CancellationToken stop = _playStop.Token;
        int perCheck = (int)Math.Max(1, PlayTouchEvery.Ticks / HoldWatchEvery.Ticks);
        long peak = hold.HeldBytes;
        long releasedTotal = 0;
        long droppedTotal = 0;
        long refilledTotal = 0;
        int checks = 0;
        string? lastSkip = null;
        string ended = string.Empty;
        bool off = false;
        try
        {
            for (int tick = 1; ; tick++)
            {
                _env.Sleep(HoldWatchEvery, stop);
                if (stop.IsCancellationRequested || GameOf(name) is not { } game || !_env.GameRunning(game))
                {
                    break; // 서비스가 멈추거나 설정에서 빠지거나 등록 실행 파일이 모두 끝남
                }

                if (!_env.KeepHoldEnabled())
                {
                    (ended, off) = (" (turned off in the settings)", true);
                    break;
                }

                long availMib = _env.AvailableRamMib();
                if (availMib < floorMib)
                {
                    long released = ReleaseBelowFloor(hold, name, exe, floorMib, availMib);
                    releasedTotal += released;
                    _holdHeld[name] = hold.HeldBytes;
                    if (hold.HeldBytes <= 0)
                    {
                        ended = " (everything released for memory)";
                        break;
                    }
                }

                if (tick % perCheck == 0)
                {
                    (long refilled, long dropped) = CheckHeld(hold, game, exe, ref lastSkip);
                    (refilledTotal, droppedTotal, checks) = (refilledTotal + refilled, droppedTotal + dropped, checks + 1);
                    _holdHeld[name] = hold.HeldBytes;
                    if (hold.HeldBytes <= 0)
                    {
                        ended = " (nothing left held)";
                        break;
                    }
                }
            }
        }
        finally
        {
            long held = hold.HeldBytes;
            hold.Dispose(); // 덜 중요한 것부터 내놓고 뷰를 닫음
            _log.Info($"{name}: cache hold ended after {checks} checks ({exe}): {held >> 20} MiB released at the end, peak {peak >> 20} MiB of {targetBytes >> 20} MiB target, {releasedTotal >> 20} MiB released for memory, {refilledTotal >> 20} MiB read again from disk, {droppedTotal >> 20} MiB taken off the hold{ended}");
        }

        return off ? HoldOutcome.No : HoldOutcome.Done; // 설정이 꺼졌으면 남은 시간은 지금의 다시 만지기가 이어받음
    }

    // 가용 메모리가 하한 아래: 부족한 양 (과 약간의 여유) 만큼 덜 중요한 쪽부터 내놓습니다. 내놓은 양을 돌려줍니다.
    private long ReleaseBelowFloor(IKeepHold hold, string name, string exe, long floorMib, long availMib)
    {
        using var activity = ServiceActivity.Begin(ServiceOp.Release);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long released = hold.Release((floorMib - availMib + HoldReleaseMarginMib) << 20);
        _log.Info($"{name}: cache hold released {released >> 20} MiB for memory ({exe}): available {availMib} MiB < floor {floorMib} MiB, {hold.HeldBytes >> 20} MiB still held, in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F2} s");
        return released;
    }

    // 붙든 범위를 다시 읽어 점검합니다. 디스크에서 다시 읽은 양과 목록에서 뺀 양을 돌려줍니다.
    private (long Refilled, long Dropped) CheckHeld(IKeepHold hold, GameSettings game, string exe, ref string? lastSkip)
    {
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "play hold check", default, game.OriginRoot, allowWhileYielded: true, preserveGameKey: game.Name);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            NoteHoldSkip(game, exe, "check: disk yielded", ref lastSkip);
            return (0, 0);
        }

        lease?.NoteReadIssue("play hold check", 0, false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lease?.Token ?? default, _playStop.Token);
        using var low = new LowPriorityReader(_env);
        using var activity = ServiceActivity.Begin(ServiceOp.Check);
        HoldCheckResult r;
        lock (_touchSerial)
        {
            hold.Rebase();
            r = hold.Recheck(KeepDiskReadCapBytes, linked.Token);
        }

        string cap = r.CapReached ? $", refill cap reached, {r.DroppedBytes >> 20} MiB taken off the hold" : r.DroppedBytes > 0 ? $", {r.DroppedBytes >> 20} MiB taken off the hold (unreadable)" : string.Empty;
        string changed = r.ChangedFiles.Count > 0 ? $", {r.ChangedFiles.Count} changed file(s) released ({r.ChangedFiles[0]})" : string.Empty;
        _log.Info($"{game.Name}: cache hold checked ({exe}): {r.CheckedBytes >> 20} MiB read again in {r.Seconds:F1} s (cpu {r.CpuSeconds:F1} s, {r.Checks} checks), {r.RefilledBytes >> 20} MiB reduced by the memory manager and read from disk ({r.HardFaults} hard faults){cap}{changed}, {hold.HeldBytes >> 20} MiB held");
        return (r.RefilledBytes, r.DroppedBytes);
    }
}
