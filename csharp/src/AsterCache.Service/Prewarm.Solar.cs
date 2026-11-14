using System.Diagnostics;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 가 Solar (예열한 캐시를 디스크 읽기 없이 다시 만져 순서를 정하고 유지) 와 만나는 곳.
public sealed partial class Prewarmer
{
    private readonly Solar _solar;

    internal Solar Solar => _solar;

    private RequeueTally _requeued; // 핫 집합 스트림이 읽는 묶음을 다시 만든 양 (Run·보충이 예열 한 회 동안 모아 done 줄에 씀), _run 아래에서만 씀

    private readonly HashSet<string> _protectedNames = new(StringComparer.OrdinalIgnoreCase); // 이번 예열 한 회에서 보호한 다른 게임 (done 줄의 서로 다른 게임 수), 첫 보호 때 비움, _run 아래에서만 씀

    // 핫 집합 스트림 한 번이 읽는 묶음마다 그 범위를 곧바로 다시 만지는 도우미 (StreamHot 이 만듦). 꺼져 있거나 keep_stream_on_game_start (게임이 떠도 스트림이 계속 읽음, 만지기도 디스크 읽기가 될 수 있음) 면 null.
    // 만지기는 스트림과 같은 스레드·같은 대여·같은 중지 토큰 아래에서만 돌고, 게임 시작·트리거 선점 표시가 서면 건너뜁니다 (묶음 점검이 그 사유로 스트림을 멈추기 전의 틈도 막음).
    private StreamRequeue? BeginRequeue(GameSettings game, Action<double>? onTouched = null) =>
        _solar.Enabled && !_auto.KeepStreamOnGameStart ? new StreamRequeue(_env.TouchRanges, _log, game.Name, () => _streamGameStarted || _streamPreempted, onTouched) : null;

    // 이어 읽기 (skipBytes > 0) 는 이전 회가 읽은 앞부분 (공통 블록은 목록의 부분집합이라 그 안에 있음) 을 읽지 않으므로, 그대로 두면 그 부분이 이 게임에서 가장 오래된 페이지로 남습니다.
    // 꼬리를 읽기 전에 건너뛴 앞부분을 목록 순서대로 한 번 만집니다. 이어 읽기는 틈 메우기 없는 창 읽기라 계획한 목록 (PlannedPrewarmReads) 이 스트림의 목록과 같습니다. 계획할 수 없으면 건너뜁니다.
    private void RequeueSkipped(GameSettings game, long skipBytes, StreamRequeue? requeue, CancellationToken stop)
    {
        if (requeue is null || skipBytes <= 0 || stop.IsCancellationRequested || DriveHasRunningGame(game))
        {
            return;
        }

        try
        {
            if (_streamer.PlannedPrewarmReads(game) is { Count: > 0 } planned)
            {
                requeue.TouchSkipped(PrefetchStreamer.Head(planned, skipBytes), stop);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException)
        {
            // 계획할 수 없음: 건너뛴 앞부분은 스트림 끝의 사다리가 처리
        }
    }

    // 핫 집합 스트림이 디스크를 읽기 시작하기 직전 (StreamHot, 디스크 대여 안): 표식이 있는 다른 게임들을 순위가 낮은 것부터 한 번 만집니다 (유지 틱의 묶음 새로 고침과 같은 함수, 대상에서 이 게임만 뺌).
    // 그러면 이 스트림이 새로 읽는 페이지는 다른 게임이 아니라 캐시 (와 이 게임 자신의 오래된 페이지) 부터 밀어냅니다. 표본 점검과 게이트는 새로 고침과 같습니다.
    // 트리거 예열 (allowWhileExiting) 은 게임 시작이 임박한 경로라 늦추지 않으려고 하지 않고, 스트림이 끝난 뒤 사다리가 다른 게임의 A 만 줄입니다 (Solar.Order).
    // 만지는 데 쓴 시간은 스트림의 노출 시간·양보 시간에서 빼도록 돌려줍니다. 실패·취소돼도 스트림 결과는 같습니다.
    private RequeueTally ProtectOthers(GameSettings game, CancellationToken stop, bool trigger, ReadYield readYield) =>
        trigger || !CanProtectOthers(game, stop) ? default : TouchOthers(game, stop, readYield, () => _solar.Marked(OtherGames(game)));

    // 핫 집합 스트림이 도는 동안 묶음 사이 (StreamHot 의 묶음 다시 만지기 직후): 표식이 있는 다른 게임이 새로 고칠 때가 됐으면 (유지 틱과 같은 판정 Rewarmer.DueGroup) 스트림 앞의 보호와 같은 함수로 다시 만집니다.
    // 판정에는 대기 목록 통계 조회가 한 번 들므로 직전 판정 뒤 SolarPolicy.ProtectCheckEvery 가 지났을 때만 봅니다 (checkedAt). 조건과 계약 (같은 스레드·대여·중지 토큰, 실패가 결과를 바꾸지 않음) 은 스트림 앞의 보호와 같습니다.
    // streamBytes: 이 스트림이 그때까지 만진 양 (공통 블록·건너뛴 앞부분·묶음): 새 표식의 앞에 있는 양에서 끝의 사다리가 줄여 주지 않을 만큼을 뺍니다.
    private RequeueTally ProtectBetweenBatches(GameSettings game, CancellationToken stop, bool trigger, ReadYield readYield, long streamBytes, ref DateTimeOffset checkedAt)
    {
        DateTimeOffset now = _now();
        if (trigger || !CanProtectOthers(game, stop) || (now >= checkedAt && now - checkedAt < SolarPolicy.ProtectCheckEvery))
        {
            return default;
        }

        checkedAt = now;
        return TouchOthers(game, stop, readYield, () => _rewarm.DueGroup(OtherGames(game), _auto, stop), SolarPolicy.StreamAheadLess(streamBytes, _solar.PriorTouchedBytes(game.Name)));
    }

    // 보호할 수 있는 상태인가: SOLAR 가 켜져 있고, 게임이 떠도 스트림이 계속 읽는 설정이 아니고, 스트림이 멈추지 않았고, 게임 시작·트리거 선점 표시가 서 있지 않음.
    private bool CanProtectOthers(GameSettings game, CancellationToken stop) => _solar.Enabled && !_auto.KeepStreamOnGameStart && !stop.IsCancellationRequested && !ProtectionHalted(game);

    private bool ProtectionHalted(GameSettings game) => _streamGameStarted || _streamPreempted || PreemptedByTriggerWait(game.Name);

    // 스트림의 게임을 뺀 예열한 게임, 순위가 낮은 것부터 (Warmed 는 최근 순).
    private IEnumerable<GameSettings> OtherGames(GameSettings game) => Enumerable.Reverse(Warmed()).Where(g => !string.Equals(g.Name, game.Name, StringComparison.OrdinalIgnoreCase));

    // pick 이 고른 다른 게임들을 만지고 (Rewarmer.RefreshGroup) 쓴 시간을 스트림의 양보 시간에서 빼도록 알립니다. 고른 게임이 없으면 아무것도 하지 않습니다.
    private RequeueTally TouchOthers(GameSettings game, CancellationToken stop, ReadYield readYield, Func<IReadOnlyList<GameSettings>> pick, long aheadLess = 0)
    {
        long startedAt = Stopwatch.GetTimestamp();
        (int Games, long Bytes, IReadOnlyList<string> Names) touched = (0, 0, []);
        try
        {
            if (pick() is not { Count: > 0 } others)
            {
                return default;
            }

            touched = _rewarm.RefreshGroup(others, _auto, stop, () => ProtectionHalted(game), aheadLess);
        }
        catch (OperationCanceledException)
        {
            // 스트림이 멈춤: 아무것도 하지 않음
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            _log.Warn($"{game.Name}: protecting the other games skipped: {e.GetType().Name}"); // 예열 결과에는 영향을 주지 않음
        }

        double seconds = Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
        readYield.Exclude(seconds);
        if (_requeued.ProtectedTimes == 0)
        {
            _protectedNames.Clear(); // 이번 예열 한 회의 첫 보호
        }

        return new RequeueTally(0, 0, false, 0, touched.Names.Count(_protectedNames.Add), touched.Bytes, seconds, touched.Games > 0 ? 1 : 0);
    }

    // 핫 집합 스트림이 끝까지 읽은 직후 (StreamHot, 디스크 대여 안): 그 게임이 지금까지 캐시에 올린 전체의 역순으로 캐시를 만져 중요한 것이 마지막에 밀려나게 합니다.
    // game 은 스트림이 읽은 범위를 나타내는 사본 (메모리·상한으로 줄인 것 포함, 보충이면 이어 읽은 창) 이라 앞에서 읽은 부분과 보충분이 모두 대상입니다. 방금 읽었으므로 표본 점검은 생략합니다.
    // reads: 메모리 한도로 마무리한 스트림은 읽은 창의 앞부분 (WindowLadderReads), 끝까지 읽었으면 null (읽은 범위 전체).
    // 게임이 이미 도는 디스크이거나 keep_stream_on_game_start (게임이 떠도 스트림을 끊지 않는 실험 설정) 가 켜져 있으면 만지지 않습니다: 만지기도 캐시에 없으면 디스크 읽기라 게임을 느리게 할 수 있습니다.
    private void LadderAfterStream(GameSettings game, CancellationToken stop, Func<IReadOnlyList<ReadOp>?>? reads = null)
    {
        if (!_solar.Enabled || _auto.KeepStreamOnGameStart || _streamGameStarted || stop.IsCancellationRequested || _streamPreempted || PreemptedByTriggerWait(game.Name) || DriveHasRunningGame(game)
            || (reads is null ? LadderReads(game) : reads()) is not { Count: > 0 } ladder)
        {
            return;
        }

        try
        {
            _ = _solar.Order(game, ladder, stop);
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _log.Warn($"{game.Name}: ladder skipped: {e.GetType().Name}"); // 예열 결과에는 영향을 주지 않음
        }
    }

    // 사다리 대상 (읽은 순서): 먼저 읽은 가장 중요한 공통 블록 + 읽은 범위 전체. 역순으로 만지면 보충분의 뒤쪽 → … → 스트림 앞부분 → 공통 블록 순서가 됩니다.
    // covered: 읽은 범위를 나타내는 게임 (부분 예열 뒤 표본을 고르는 기준과 같게 MaxStreamMib 로 잘린 사본). 계획할 수 없으면 null.
    internal IReadOnlyList<ReadOp>? LadderReads(GameSettings covered)
    {
        try
        {
            if (_streamer.PlannedPrewarmReads(covered) is not { Count: > 0 } planned)
            {
                return null;
            }

            IReadOnlyList<ReadOp> common = _env.CommonFirstOps(covered, 0, 0).Ops; // 예산 0: 틈 메우기 없이 계획한 목록 안의 common 범위
            return common.Count == 0 ? planned : [.. common, .. planned];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException)
        {
            return null;
        }
    }

    // 틈 없는 창 읽기가 읽은 범위의 사다리 대상 (공통 블록 + 창 목록의 앞 bytes 바이트): 창 (windowMib) 은 스트림이 읽은 것과 같은 크기로 계획하므로 읽지 않은 구간을 만지지 않습니다. 계획할 수 없으면 null.
    private IReadOnlyList<ReadOp>? WindowLadderReads(GameSettings game, long windowMib, long bytes)
    {
        try
        {
            GameSettings window = game with { MaxStreamMib = (int)Math.Min(windowMib, int.MaxValue), MaxStreamSet = true };
            if (_streamer.PlannedPrewarmReads(window) is not { Count: > 0 } planned || PrefetchStreamer.Head(planned, bytes) is not { Count: > 0 } read)
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

    /// 지금까지 읽은 범위를 나타내는 게임: 부분 예열 (메모리·시작 상한) 이면 읽은 앞부분까지로 줄인 사본, 아니면 그대로.
    internal GameSettings CoveredPart(GameSettings g)
    {
        lock (_gate)
        {
            return _partial.TryGetValue(g.Name, out (long WarmedMib, long HotMib) part) ? g with { MaxStreamMib = (int)Math.Min(part.WarmedMib, int.MaxValue), MaxStreamSet = true } : g;
        }
    }

    // 이 게임 디스크의 읽기 대여를 지금 얻을 수 있는가 (얻으면 바로 놓음): 새로 고침이 대여 거절을 "표본 없음" 으로 보고 표식을 지우지 않게 먼저 물어봅니다.
    internal bool DiskReadAllowed(GameSettings game, CancellationToken ct)
    {
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "cache refresh gate", ct);
        return _coordinator is null || lease is { IsValid: true };
    }

    // 디스크 대여 안에서 work 를 돌립니다 (거절·무효면 null): 게임이 시작돼 대여가 취소되면 work 의 토큰이 취소됩니다.
    internal TouchEnd? UnderLease(GameSettings game, string purpose, CancellationToken ct, Func<CancellationToken, TouchEnd?> work)
    {
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, purpose, ct);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            return null;
        }

        lease?.NoteReadIssue(purpose, 0, false); // 양보 진단 로그에 이 단계가 보이게
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lease?.Token ?? default);
        return work(linked.Token);
    }
}
