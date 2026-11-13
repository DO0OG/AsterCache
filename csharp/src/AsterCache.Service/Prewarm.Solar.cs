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

    // 핫 집합 스트림이 끝까지 읽은 직후 (StreamHot, 디스크 대여 안): 그 게임이 지금까지 캐시에 올린 전체의 역순으로 캐시를 만져 중요한 것이 마지막에 밀려나게 합니다.
    // game 은 스트림이 읽은 범위를 나타내는 사본 (메모리·상한으로 줄인 것 포함, 보충이면 이어 읽은 창) 이라 앞에서 읽은 부분과 보충분이 모두 대상입니다. 방금 읽었으므로 표본 점검은 생략합니다.
    // 게임이 이미 도는 디스크이거나 keep_stream_on_game_start (게임이 떠도 스트림을 끊지 않는 실험 설정) 가 켜져 있으면 만지지 않습니다: 만지기도 캐시에 없으면 디스크 읽기라 게임을 느리게 할 수 있습니다.
    private void LadderAfterStream(GameSettings game, CancellationToken stop)
    {
        if (!_solar.Enabled || _auto.KeepStreamOnGameStart || _streamGameStarted || stop.IsCancellationRequested || _streamPreempted || PreemptedByTriggerWait(game.Name) || DriveHasRunningGame(game)
            || LadderReads(game) is not { Count: > 0 } reads)
        {
            return;
        }

        try
        {
            _ = _solar.Order(game, reads, stop);
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

    /// 지금까지 읽은 범위를 나타내는 게임: 부분 예열 (메모리·시작 상한) 이면 읽은 앞부분까지로 줄인 사본, 아니면 그대로.
    internal GameSettings CoveredPart(GameSettings g)
    {
        lock (_gate)
        {
            return _partial.TryGetValue(g.Name, out (long WarmedMib, long HotMib) part) ? g with { MaxStreamMib = (int)Math.Min(part.WarmedMib, int.MaxValue), MaxStreamSet = true } : g;
        }
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
