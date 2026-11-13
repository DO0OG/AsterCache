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
