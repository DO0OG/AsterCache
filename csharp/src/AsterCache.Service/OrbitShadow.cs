using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using AsterCache.Core.Discovery;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Trace;

namespace AsterCache.Service;

/// 상태 파일 (auto_status.json 의 게임 항목 shadow_orbit) 한 건: 선택 라벨, 사유, 계획한 블록 수, 계획 순서 길이, 계산 시간 (ms), 기록 시각,
/// 보정 없음 여부, 최악 손실 (ms, 플래너가 주지 않으면 null), 후보 선택 비율 (보정이 없어 후보가 없으면 null), 계산 스레드 할당 바이트, 지문 (입력을 확보할 때의 재계획 세대 번호).
public sealed record OrbitShadowRecord(string Selected, string Reason, int Blocks, int Order, double Ms, DateTimeOffset At,
    bool Uncalibrated = false, double? WorstLossMs = null, double? CandidateRatio = null, long AllocatedBytes = 0, string? Fingerprint = null);

/// ORBIT-2R-R 그림자 계획. 트레이스가 끝난 뒤 메모리의 이번 회차 읽기로 핫 블록을 고르고 OrbitPlanner.Choose 를 한 번 돌려 로그와 상태에만 남깁니다.
/// 파일 읽기·쓰기·후킹이 없고 (상태 파일 기록 제외), 결과는 예열·재배치·스트림 순서 어디에도 넘기지 않습니다.
public sealed class OrbitShadow
{
    /// 계산 시간 상한. 넘긴 결과는 버리고 경고만 남깁니다 (게임 종료 뒤 긴 계산이 서비스를 붙잡지 않게).
    public static readonly TimeSpan MaxCompute = TimeSpan.FromSeconds(2);

    /// 블록 묶기 규칙: 핫 블록은 점수 순으로 최대 OrbitPlanner.MaxBlocks 개만 (예산 = 24 x 64 KiB). 플래너 입력 상한과 같은 수입니다.
    private const long BudgetBytes = OrbitPlanner.MaxBlocks * HotSet.Block;

    internal const int MaxInputEvents = 200_000; // 큰 트레이스가 그림자 계산을 오래 붙잡지 않도록 이벤트 순회를 제한합니다.

    // shortcut: 보정 전 자리표시 시간·신뢰 값 (입력이 플래너 검사를 통과하게만 둠), 실측 보정 envelope 이 생기면 채움.
    // 보정이 없으면 Choose 결과는 baseline 이라 이 값으로 고른 계획은 쓰이지 않습니다.
    private const double PlaceholderTimebox = 1000;
    private const double PlaceholderTransferUnitsPerMs = HotSet.Block; // 블록 1 개 = 1 ms: 시간 상한 1000 ms 안에 24 블록이 들어가게

    private readonly AutoStatus _status;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly CancellationTokenSource _stop = new(); // 서비스 종료
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _running = new(); // 게임별 돌고 있는 계산 (그 게임이 다시 뜨거나 새 계산이 시작되면 취소)
    private readonly ConcurrentQueue<(string Game, OrbitShadowRecord Record)> _done = new(); // 계산 스레드는 상태 파일을 쓰지 않고 여기에 넣음 (Flush 가 트레이스 루프에서 옮김)

    public OrbitShadow(AutoStatus status, ILog log, Func<DateTimeOffset> now)
    {
        _status = status;
        _log = log;
        _now = now;
    }

    /// 트레이스 회차가 끝난 뒤 한 번 부릅니다. 게임 이벤트 스레드에서 부르지 않습니다 (계산은 기록·병합과 같은 BelowNormal 스레드에서 돕니다).
    public void Schedule(string game, IReadOnlyList<TraceEvent> events, string? antiCheatKind, string fingerprint)
    {
        if (Capabilities.Decide(CapabilityFeature.ShadowPlan, antiCheatKind).Outcome != CapabilityOutcome.Allow)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _running.AddOrUpdate(game, cts, (_, old) =>
        {
            Cancel(old);
            return cts;
        });
        var thread = new Thread(() =>
        {
            try
            {
                Evaluate(game, events, MaxCompute, fingerprint, cancel: cts.Token);
            }
            finally
            {
                _ = _running.TryRemove(new KeyValuePair<string, CancellationTokenSource>(game, cts));
                cts.Dispose();
            }
        })
        { Priority = ThreadPriority.BelowNormal, IsBackground = true };
        thread.Start(); // 끝까지 기다리지 않음 (트레이스 루프를 붙잡지 않게)
    }

    /// 그 게임이 시작되면 (감시기 스레드에서 부름) 돌고 있는 그림자 계산을 취소합니다: 게임과 CPU 를 다투지 않게.
    public void CancelGame(string game)
    {
        if (_running.TryRemove(game, out CancellationTokenSource? cts))
        {
            Cancel(cts);
        }
    }

    /// 서비스 종료: 모든 계산을 취소합니다.
    public void Shutdown() => Cancel(_stop);

    private static void Cancel(CancellationTokenSource cts)
    {
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 계산 스레드가 이미 치웠음
        }
    }

    /// 트레이스 루프 (1 초 틱) 에서 부릅니다: 끝난 계산 결과만 상태 파일에 옮깁니다. 게임 종료 뒤 계산이 늦게 끝나도 저장은 루프에서만 일어납니다.
    public void Flush()
    {
        while (_done.TryDequeue(out (string Game, OrbitShadowRecord Record) item))
        {
            _status.RecordOrbitShadow(item.Game, item.Record);
        }
    }

    /// 계산과 기록. 예외는 잡아 경고만 남기고 (서비스는 계속), 시간 상한 (플래너 협조적 취소) 을 넘기거나 취소되면 결과를 버립니다.
    internal OrbitShadowRecord? Evaluate(string game, IReadOnlyList<TraceEvent> events, TimeSpan limit, string fingerprint, long budgetBytes = BudgetBytes, CancellationToken cancel = default)
    {
        try
        {
            using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            timer.CancelAfter(limit);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var clock = Stopwatch.StartNew();
            bool tooManyEvents = events.Count > MaxInputEvents;
            PlanningWorld? world = tooManyEvents ? null : BuildWorld(events, budgetBytes);
            PlanningWorld? planned = null;
            string reduced = string.Empty;
            if (!tooManyEvents)
            {
                (planned, reduced) = OrbitPlanner.Reduce(world); // 25블록 이상·1e12 초과는 줄여서 계획 (사유에 접미)
            }

            OrbitDecision? decision = planned is null ? null : OrbitPlanner.Choose(planned, uncertainty: null, device: null, cancellation: timer.Token); // 실측 보정 없음: 플래너가 baseline 과 사유를 돌려줌
            clock.Stop();
            timer.Token.ThrowIfCancellationRequested(); // 플래너 밖 단계 (핫 블록 선택·기준 계획) 에서 시간 상한·취소가 지나갔어도 버림
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            double ms = clock.Elapsed.TotalMilliseconds;
            if (clock.Elapsed >= limit)
            {
                _log.Warn($"{game}: orbit shadow plan discarded ({ms.ToString("F1", CultureInfo.InvariantCulture)} ms, limit {limit.TotalSeconds} s)");
                return null;
            }

            string reason = tooManyEvents ? "too_many_events" : (decision?.Reason ?? "no_hot_blocks") + reduced;
            // 보정 자료를 넘기지 않으므로 항상 보정 전 (uncalibrated)
            var record = new OrbitShadowRecord(decision?.Selected ?? "none", reason, planned?.Blocks.Count ?? 0, decision?.Plan.Order.Count ?? 0, Math.Round(ms, 1), _now(),
                true, null, decision is { Candidates: > 0 } d ? (double)d.Eligible / d.Candidates : null, allocated, fingerprint);
            _done.Enqueue((game, record));
            _log.Info($"{game}: orbit shadow plan, blocks {record.Blocks}, order {record.Order}, selected {record.Selected}, reason {record.Reason}, {record.Ms.ToString("F1", CultureInfo.InvariantCulture)} ms, alloc {record.AllocatedBytes} bytes");
            return record;
        }
        catch (OperationCanceledException)
        {
            _log.Warn($"{game}: orbit shadow plan discarded ({(cancel.IsCancellationRequested ? "cancelled" : "time limit " + limit.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s")})");
            return null;
        }
        catch (Exception e) // 그림자 계산은 별도 스레드라 어떤 예외도 밖으로 내보내지 않음 (서비스 종료 방지)
        {
            _log.Warn($"{game}: orbit shadow plan failed ({e.GetType().Name})");
            return null;
        }
    }

    /// 이번 회차 읽기에서 핫 블록을 골라 플래너 입력을 만듭니다. 블록이 없으면 null.
    internal static PlanningWorld? BuildWorld(IReadOnlyList<TraceEvent> events, long budgetBytes)
    {
        if (events.Count == 0 || events.Count > MaxInputEvents)
        {
            return null;
        }

        (List<BlockKey> blocks, Dictionary<BlockKey, BlockStat> stats) = HotSet.Select([events], budgetBytes);
        if (blocks.Count == 0)
        {
            return null;
        }

        // 키는 경로 대신 순번 (플래너 키 길이 상한 512 자를 경로가 넘을 수 있음)
        var planning = new List<PlanningBlock>(blocks.Count);
        var episodes = new List<PredictedEpisode>(blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            BlockStat stat = stats[blocks[i]];
            string key = "b" + i.ToString(CultureInfo.InvariantCulture);
            planning.Add(new PlanningBlock(key, HotSet.Block, stat.M, stat.F));
            // shortcut: 블록마다 그 블록만 요구하는 에피소드를 빈도 가치로 둠 (에피소드가 없으면 기준 묶음 계획이 항상 빈 순서가 됨), 실측 예측 모델이 생기면 교체.
            episodes.Add(new PredictedEpisode([key], stat.F, 0));
        }

        return new PlanningWorld(planning, episodes, new BigInteger(blocks.Count * HotSet.Block), PlaceholderTimebox, PlaceholderTransferUnitsPerMs,
            0, 0, 0, 0, Signature: "shadow");
    }
}
