using AsterCache.Core.Logging;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// Prewarmer 의 런처 트리거 선점: 실제로 읽을 트리거 예열이 오면 같은 디스크의 다른 게임의 배경 예열 읽기를 멈추고 (정지 신호 + 대기 표시),
/// 트리거 경로가 예열 락을 기다릴 때는 취소할 수 있게 합니다.
public sealed partial class Prewarmer
{
    /// 선점으로 멈춘 읽기의 사유 (취소 로그·PrewarmResult.CancelDetail). 보충 예열과 대기 예열 재시도는 이 사유를 디스크 대여 취소와 같은 급으로 다룹니다 (읽은 데서 이어 읽거나 기존 재시도에 올라감).
    internal const string PreemptedByTrigger = "preempted by trigger prewarm";

    // 락 대기 중 취소를 보는 간격과, 이 시간 이상 기다렸을 때만 남기는 대기 로그의 기준.
    private static readonly TimeSpan TriggerLockStep = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan TriggerLockLogAfter = TimeSpan.FromSeconds(1);

    private readonly List<string[]> _triggerWaits = []; // 읽을 트리거 예열이 끝나기를 기다리는 (또는 도는) 게임 이름들, 트리거마다 하나, _gate 로 보호
    private volatile bool _streamFromTrigger; // 지금 도는 스트림이 트리거 예열 것인가 (선점하지 않음)
    private volatile bool _streamPreempted; // 지금 도는 스트림을 트리거가 멈추게 했는가 (묶음 점검이 사유를 남김)

    /// 5 초: 보정 읽기 시간 상한 (트리거 밖의 예열). 시험이 줄여 쓸 수 있게 열어 둠.
    internal TimeSpan CalibrationLimit { get; set; } = TimeSpan.FromSeconds(5);

    internal IDisposable BeginTriggerWait(IReadOnlyList<GameSettings> games)
    {
        string[] names = [.. games.Select(g => g.Name)];
        lock (_gate)
        {
            _triggerWaits.Add(names);
        }

        return new TriggerWaitMark(this, names);
    }

    // 표시를 지움 (정상·취소·예외 모두): 겹친 트리거의 표시는 각자 따로 지워짐 (배열 참조로 찾음).
    private sealed class TriggerWaitMark(Prewarmer owner, string[] names) : IDisposable
    {
        private Prewarmer? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndTriggerWait(names);
    }

    private void EndTriggerWait(string[] names)
    {
        lock (_gate)
        {
            _triggerWaits.Remove(names);
        }
    }

    internal bool TriggerWaitPending()
    {
        lock (_gate)
        {
            return _triggerWaits.Count > 0;
        }
    }

    private string[] TriggerWaitNames()
    {
        lock (_gate)
        {
            return [.. _triggerWaits.SelectMany(n => n)];
        }
    }

    // 이 게임과 같은 디스크의 게임 (자신 포함) 을 읽을 트리거 예열이 기다리는 중인가: 트리거가 아닌 새 예열을 시작하지 않는 기준.
    private bool TriggerWaitCovers(string game) =>
        TriggerWaitNames().Any(n => string.Equals(n, game, StringComparison.OrdinalIgnoreCase) || SameDisk(n, game));

    // reading 을 읽는 중인 읽기를 트리거 대상 (targets) 이 멈추게 할 수 있는가: 대상 자신의 읽기는 그대로 (끝나기를 기다림), 같은 디스크의 대상이 있을 때만.
    private bool ShouldPreempt(string reading, IReadOnlyCollection<string> targets) =>
        targets.Count > 0 && !targets.Contains(reading, StringComparer.OrdinalIgnoreCase) && targets.Any(t => SameDisk(t, reading));

    // 묶음 점검 (CheckPrewarmBatch) 이 보는 선점 조건: 표시를 세우기 전에 시작해 정지 신호를 놓친 읽기를 다음 묶음 경계에서 멈춤.
    private bool PreemptedByTriggerWait(string game) => !_streamFromTrigger && ShouldPreempt(game, TriggerWaitNames());

    // 트리거가 읽기 시작 전에 부름: 돌고 있는 핫 집합 스트림 (보정 읽기·여러 게임 묶음의 앞부분 포함) 이 대상이 아닌 같은 디스크의 다른 게임 것이고
    // 트리거에서 시작한 것이 아니면 기존 정지 신호로 멈춥니다. 기록 없는 통째·프로필 읽기는 멈추지 않음: 멈추면 다시 시도할 곳이 없어 (기록 없는 게임은 대기 재시도 대상이 아님) 그 예열을 잃음.
    private void PreemptForTrigger(IReadOnlyList<GameSettings> targets)
    {
        string? reading;
        CancellationTokenSource? stop;
        PrewarmCancelDetail? detail;
        lock (_gate)
        {
            (reading, stop, detail) = (_streaming, _streamStop, _streamCancelDetail); // 읽기 표시 (TryMarkReading) 가 _gate 아래에서 쓰므로 한 번에 읽음
        }

        if (reading is null || stop is null || _streamFromTrigger || !ShouldPreempt(reading, [.. targets.Select(t => t.Name)]))
        {
            return;
        }

        _streamPreempted = true;
        detail?.Set(PreemptedByTrigger);
        try
        {
            stop.Cancel(); // 연결된 보정 읽기 토큰도 함께 취소됨
        }
        catch (ObjectDisposedException)
        {
            // 스트림이 방금 끝남
        }
    }

    // 배경 경로 (보충·대기 재시도·트리거 재시도·파일 예열) 의 락 시도: 트리거 예열이 기다리는 동안은 잡지 않고 다음 틱으로 넘김 (Monitor 는 공정하지 않아 멈춘 직후 먼저 가져갈 수 있음).
    internal bool TryEnterRunIdle()
    {
        if (TriggerWaitPending() || !Monitor.TryEnter(_run))
        {
            return false;
        }

        if (TriggerWaitPending())
        {
            Monitor.Exit(_run); // 확인과 획득 사이에 표시가 섬
            return false;
        }

        return true;
    }

    // 예열 락을 얻음 (호출자가 Monitor.Exit). 트리거 경로는 취소할 수 있게 짧은 간격으로 시도하며 기다리고, 취소되면 false (락 없음).
    private bool EnterRun(string game, CancellationToken ct, bool trigger)
    {
        if (!trigger)
        {
            Monitor.Enter(_run);
            return true;
        }

        DateTimeOffset started = _now();
        string? holder = null;
        while (!Monitor.TryEnter(_run, TriggerLockStep))
        {
            if (ct.IsCancellationRequested)
            {
                return false;
            }

            holder = _streaming ?? _coldReading ?? holder;
        }

        try
        {
            TimeSpan waited = _now() - started;
            if (waited >= TriggerLockLogAfter)
            {
                _log.Info($"{game}: trigger prewarm waited {waited.TotalSeconds:F0} s for {holder ?? "another read"}");
            }
        }
        catch
        {
            Monitor.Exit(_run); // 호출자는 false/예외 때 놓지 않으므로 로그 실패로 락이 남지 않게
            throw;
        }

        return true;
    }
}
