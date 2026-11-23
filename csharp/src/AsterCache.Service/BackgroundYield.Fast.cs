using AsterCache.Core.Logging;

namespace AsterCache.Service;

/// 우선순위를 적용한 수의 집계 (로그 한 줄과 판단용 기록을 만드는 데만 씁니다).
internal sealed class YieldTally
{
    public int Seen; // 빠른 경로가 확인한 새 프로세스
    public int Excluded; // 그 가운데 제외 규칙이나 조회 실패로 대상이 되지 않은 수
    public int Memory; // 메모리 우선순위를 매우 낮음으로 낮춘 수
    public int InUse; // 쓰고 있는 프로세스의 메모리 우선순위를 보통보다 낮음으로 낮춘 수
    public int Io;
    public int Cpu;
    public int Switched; // 쓰고 있다가 쉬게 됐거나 그 반대라 메모리 우선순위 값을 바꾼 수
    public int Failed; // 쓰기에 실패한 우선순위 수 (그 사이 끝났거나 접근 거부)

    public bool Any => Seen + Excluded + Memory + InUse + Io + Cpu + Switched + Failed > 0;

    public void Add(YieldTally other)
    {
        Seen += other.Seen;
        Excluded += other.Excluded;
        Memory += other.Memory;
        InUse += other.InUse;
        Io += other.Io;
        Cpu += other.Cpu;
        Switched += other.Switched;
        Failed += other.Failed;
    }

    public void Clear() => (Seen, Excluded, Memory, InUse, Io, Cpu, Switched, Failed) = (0, 0, 0, 0, 0, 0, 0, 0);
}

/// BackgroundYield 의 빠른 경로: 등록 게임이 도는 동안 새로 뜬 프로세스를 약 3 초 안에 알아채 기존 Select·Lower 를 그 프로세스들에만 돌립니다 (설정 memory.background_yield_fast).
/// 새 프로세스는 프로세스 번호 목록 (열지 않음, 한 번에 약 0.05 ms) 의 차이로만 알아채고, 연속 두 번 보인 번호만 (1.5 초 간격, 최대 3 초) 이름·경로를 확인합니다: 한 번만 보이고 다음 점검 전에 끝난 프로세스는 열지도 상태 파일에 쓰지도 않습니다.
/// 대상에서 빼는 규칙과 판정 순서, 열기 권한은 2 분 주기의 유지와 같습니다 (같은 Select·Lower). 작업 집합 비우기는 하지 않고, 복원은 기존 상태 파일 경로를 그대로 씁니다.
/// shortcut: 두 점검 사이에 끝났다가 같은 번호를 다른 프로세스가 이어받으면 알아채지 못합니다 (쓰기 전의 같은 프로세스 확인이 거르며, 2 분 유지가 이름이 다를 때만 다시 고름), 필요하면 번호 대신 생성 시각을 비교하도록 올림.
public sealed partial class BackgroundYield
{
    public static readonly TimeSpan FastEvery = TimeSpan.FromMilliseconds(1500);

    /// 실제 점검 간격 (시험이 짧게 바꿈).
    internal TimeSpan FastInterval { get; set; } = FastEvery;

    private HashSet<int> _seen = []; // 이미 처리했거나 세션 시작 때 떠 있던 프로세스 번호
    private HashSet<int> _pending = []; // 직전 점검에서 처음 보인 번호 (이번에도 있으면 처리)
    private readonly YieldTally _fastTally = new(); // 다음 유지 로그에 실을 빠른 경로의 집계
    private bool _gameRunning; // 마지막으로 본 등록 게임 실행 여부 (세션 시작과 30 초 틱에서 갱신)
    private volatile bool _fastOn;
    private volatile TaskCompletionSource _fastSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _fastWarned;
    private readonly Dictionary<int, long> _deferred = []; // 빠른 경로가 골랐지만 그때 쓰고 있는 것으로 보여 메모리 쪽을 건너뛴 프로세스 (번호와 생성 시각), 2분 유지에서 쉬는 것으로 판정되면 낮춤
    private long _appliedTotal;

    /// 서비스가 시작된 뒤 새로 우선순위를 적용한 프로세스 수의 누계 (초 단위 기록이 초마다 읽어 차이를 남김, 다른 스레드에서 읽음).
    public long AppliedTotal => Interlocked.Read(ref _appliedTotal);

    private bool FastEnabled => Enabled && settings().Memory.BackgroundYieldFast;

    /// 서비스가 도는 동안 1.5 초마다 FastTick 을 부릅니다. 세션이 없으면 신호를 기다리며 깨지 않습니다.
    public async Task RunFastAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                if (_fastOn)
                {
                    await Task.Delay(FastInterval, ct).ConfigureAwait(false);
                    FastTick();
                }
                else
                {
                    await _fastSignal.Task.WaitAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 서비스 종료
        }
    }

    private void StartFast(IReadOnlyList<(int Pid, string Exe)> processes)
    {
        _seen = [.. processes.Select(p => p.Pid)];
        _pending = [];
        _deferred.Clear();
        _gameRunning = GameRunning(processes);
        _fastOn = true;
        _ = _fastSignal.TrySetResult();
    }

    // 세션 끝: 모아 둔 집계를 로그에 남기고, 시작 신호가 완료된 상태일 때만 새 신호로 바꿔 다음 세션까지 기다리게 함 (EndLocked 가 부름).
    // 세션 밖에서 EndLocked 가 다시 불려도 (되돌리기에 실패한 항목이 남은 경우) 루프가 기다리는 신호를 바꾸지 않아야 다음 시작이 루프를 깨웁니다.
    private void EndFast()
    {
        _fastOn = false;
        if (_fastSignal.Task.IsCompleted)
        {
            _fastSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        (_seen, _pending) = ([], []);
        _deferred.Clear();
        LogTally(new YieldTally(), null);
    }

    /// 새로 확인된 프로세스에 우선순위를 적용합니다. 락을 잡지 못하면 (세션 시작·끝이나 유지가 도는 중) 이번은 건너뜁니다.
    internal void FastTick()
    {
        if (!Monitor.TryEnter(_lock))
        {
            return;
        }

        try
        {
            if (!_active || !_gameRunning || !FastEnabled)
            {
                return;
            }

            HashSet<int> now = [.. env.ProcessIds()];
            foreach (int gone in _seen.Where(pid => !now.Contains(pid)).ToList())
            {
                _ = _targets.Remove(gone); // 끝난 번호는 잊음: 같은 번호를 다른 프로세스가 이어받으면 새 프로세스로 다시 고름
                _ = _skipped.Remove(gone);
            }

            HashSet<int> fresh = [.. now.Where(pid => !_seen.Contains(pid))];
            HashSet<int> ready = [.. fresh.Where(_pending.Contains)];
            _pending = [.. fresh.Where(pid => !ready.Contains(pid))];
            _seen = [.. _seen.Where(now.Contains), .. ready];
            if (ready.Count == 0)
            {
                return;
            }

            IReadOnlyList<(int Pid, string Exe)> processes = env.Processes();
            List<(int Pid, string Exe)> arrivals = [.. processes.Where(p => ready.Contains(p.Pid))];
            int known = arrivals.Count(p => _targets.ContainsKey(p.Pid) || _skipped.Contains(p.Pid)); // 그 사이 2분 유지가 먼저 고른 것
            List<YieldTarget> found = Select(arrivals, pid => _targets.ContainsKey(pid) || _skipped.Contains(pid));
            foreach (YieldTarget t in found)
            {
                _targets[t.Pid] = t;
            }

            _fastTally.Seen += ready.Count;
            _fastTally.Excluded += ready.Count - known - found.Count;
            _ = Lower(found, processes, tally: _fastTally);
            foreach (YieldTarget t in found.Where(t => !t.InheritOnly && !(_lowered.TryGetValue(t.Pid, out LoweredProcess entry) && entry.Created == t.Created && entry.Priority != NotLowered)))
            {
                _deferred[t.Pid] = t.Created; // 메모리 쪽을 아직 안 낮춘 것 (쓰고 있는 것으로 보임): 2분 유지에서 다시 판정
            }

            _fastWarned = false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (!_fastWarned)
            {
                _fastWarned = true; // 같은 실패가 1.5 초마다 로그를 채우지 않게 처음 한 번만
                log.Error("background yield fast path failed", e);
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    // 값이 있을 때만 한 줄 (빠른 경로가 모아 둔 집계와 이번 회차의 집계를 합침).
    private void LogTally(YieldTally round, TimeSpan? span)
    {
        round.Add(_fastTally);
        _fastTally.Clear();
        if (round.Any)
        {
            string within = span is { } s ? $" in {(int)s.TotalSeconds} s" : string.Empty;
            log.Info($"background yield maintenance{within}: fast path saw {round.Seen} new processes ({round.Excluded} not eligible), memory priority lowered for {round.Memory}, set below normal for {round.InUse} in-use, switched for {round.Switched}, I/O priority lowered for {round.Io}, CPU priority lowered for {round.Cpu}, {round.Failed} failed");
        }
    }
}
