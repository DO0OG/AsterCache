namespace AsterCache.Service;

/// 게임 드라이브가 한가한가를 판정하는 한 곳: 서비스가 게임 드라이브에서 물리 읽기를 일으키는 붙들기 채우기가 씁니다.
/// 기준: 디스크 대기열의 순간값이 0 이고 IdleHoldMs 동안 0 이 이어졌으며 계측이 비지 않았을 것. 계측을 읽지 못하면 한가하지 않음으로 봅니다.
/// 판정과 발행을 물리 디스크 (드라이브 문자) 마다 한 흐름으로 묶습니다: TryBegin 이 새로 IdleHoldMs 를 확인해 순번 (Turn) 을 주고, 호출한 쪽이 묶음 하나를 읽고 순번을 놓으면 다음 판정은 처음부터 다시 잽니다.
/// 자기 읽기가 끝난 직후의 0 을 이어진 한가함으로 치지 않으므로, 그 사이 게임이나 다른 프로그램의 읽기가 끼어들었다 끝나도 새 묶음을 곧바로 내지 않습니다.
/// 지연 (읽기 한 건의 평균 시간) 은 기준에 넣지 않았습니다: 서비스 자신이 방금 낸 읽기가 지연에 섞여 게임의 읽기와 가릴 수 없기 때문입니다.
internal sealed class DiskIdleGate : IDisposable
{
    /// 대기열 0 이 이만큼 이어져야 한가함: 게임의 읽기 한 건이 대기열에 머무는 시간 (하드디스크 수 ms~수십 ms) 보다 길고 한 번의 판정이 채우기를 너무 늦추지 않는 값입니다 (설계 범위 200~500 ms). 실측으로 고른 값이 아닙니다.
    public const long IdleHoldMs = 300;

    /// 한가함을 기다리는 동안 대기열을 다시 읽는 간격 (채우기가 도는 동안만 읽음).
    public const long PollMs = 100;

    public const string QueueBusy = "queue";
    public const string MeasureGap = "gap";
    public const string LoadingPhase = "loading";
    public const string Cancelled = "cancelled";

    private readonly object _sync = new();
    private readonly Dictionary<string, SemaphoreSlim> _turns = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, long?> _queueOf;
    private readonly Func<long> _nowMs;
    private readonly Action<int> _sleep;
    private readonly IDisposable? _owner;

    /// queueOf: 드라이브 ("D:") → 그 물리 디스크의 지금 대기열 (읽지 못하면 null). nowMs·sleep: 시험이 시계를 흉내 냅니다.
    public DiskIdleGate(Func<string, long?> queueOf, Func<long>? nowMs = null, Action<int>? sleep = null, IDisposable? owner = null)
    {
        _queueOf = queueOf;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _sleep = sleep ?? (ms => Thread.Sleep(ms));
        _owner = owner;
    }

    /// 한가하다고 판정된 뒤 묶음 하나를 읽는 동안 쥐는 순번: 놓으면 같은 디스크의 다음 판정이 열립니다.
    public sealed class Turn(SemaphoreSlim turns) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0)
            {
                _ = turns.Release();
            }
        }
    }

    /// 새로 IdleHoldMs 동안 대기열이 0 인지 보고 한가하면 순번을 돌려줍니다 (why 는 null). 한가하지 않거나 취소되면 null 과 사유 (QueueBusy·MeasureGap·Cancelled).
    /// 대기열이 0 이면 최대 IdleHoldMs 동안 PollMs 간격으로 지켜보므로 호출한 스레드가 그만큼 기다립니다. 같은 디스크의 다른 순번이 쥐여 있으면 그것이 끝날 때까지 기다립니다.
    public Turn? TryBegin(string key, CancellationToken ct, out string? why)
    {
        SemaphoreSlim turns = TurnsOf(key);
        try
        {
            turns.Wait(ct);
        }
        catch (OperationCanceledException)
        {
            why = Cancelled;
            return null;
        }

        long zeroSince = -1;
        while (true)
        {
            long now = _nowMs();
            if (_queueOf(key) is not { } queue)
            {
                _ = turns.Release();
                why = MeasureGap;
                return null;
            }

            if (queue != 0)
            {
                _ = turns.Release();
                why = QueueBusy;
                return null;
            }

            if (zeroSince < 0)
            {
                zeroSince = now;
            }

            if (now - zeroSince >= IdleHoldMs)
            {
                why = null;
                return new Turn(turns);
            }

            if (ct.IsCancellationRequested)
            {
                _ = turns.Release();
                why = Cancelled;
                return null;
            }

            _sleep((int)PollMs);
        }
    }

    public void Dispose() => _owner?.Dispose();

    private SemaphoreSlim TurnsOf(string key)
    {
        lock (_sync)
        {
            if (!_turns.TryGetValue(key, out SemaphoreSlim? turns))
            {
                _turns[key] = turns = new SemaphoreSlim(1, 1);
            }

            return turns;
        }
    }

    /// 실제 환경: 서비스가 이미 쓰는 성능 카운터 (PhysicalDisk 의 Current Disk Queue Length, 인스턴스 이름의 드라이브 문자로 물리 디스크를 찾음) 에서 순간 대기열을 읽습니다.
    /// 질의 개체는 하나를 열어 두고 재사용하며 (조회마다 수집을 켜고 끄지 않음) Dispose 때 닫습니다. 읽지 못하면 닫고 5 초 뒤에 다시 엽니다 (그동안은 계측 공백).
    public static DiskIdleGate Windows()
    {
        var reader = new WindowsQueue();
        return new DiskIdleGate(drive => reader.Read(drive), owner: reader);
    }

    private sealed class WindowsQueue : IDisposable
    {
        private const long RetryMs = 5000;
        private readonly object _lock = new();
        private DiskActivity.DiskQueueLength? _query;
        private long _retryAt;
        private bool _disposed;

        public long? Read(string drive)
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return null;
                }

                long now = Environment.TickCount64;
                if (_query is null)
                {
                    if (now < _retryAt)
                    {
                        return null;
                    }

                    _query = DiskActivity.DiskQueueLength.Open();
                    _retryAt = now + RetryMs;
                }

                if (_query?.Read(drive) is { } queue)
                {
                    return queue;
                }

                _query?.Dispose(); // 읽지 못함: 닫고 다음에 다시 엶 (드라이브 구성이 바뀌었을 수 있음)
                _query = null;
                return null;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                _disposed = true;
                _query?.Dispose();
                _query = null;
            }
        }
    }
}

/// 한 번의 채우기가 한가함 판정으로 한 일: 낸 묶음과 바이트, 판정에 막힌 횟수 (막히기 시작할 때마다 한 번, 사유별)와 막혀 있던 시간, 끝난 사유. 한 스레드에서만 씁니다.
internal sealed class IdleTally
{
    private readonly Dictionary<string, int> _blocked = [];
    private string? _blockedNow;
    private long _blockedSince;

    public int Batches { get; private set; }

    public long Bytes { get; private set; }

    public long BlockedMs { get; private set; }

    public string? EndedBy { get; set; }

    public void NoteIssued(long bytes)
    {
        Batches++;
        Bytes += Math.Max(0, bytes);
    }

    /// 판정 한 번의 결과를 알립니다 (nowMs: 시계). 막히기 시작할 때만 사유별 횟수를 늘리고, 풀리면 막혀 있던 시간을 더합니다.
    public void NoteVerdict(string? why, long nowMs)
    {
        if (why is not null && _blockedNow is null)
        {
            _blockedSince = nowMs;
            _blocked[why] = _blocked.GetValueOrDefault(why) + 1;
        }
        else if (why is null && _blockedNow is not null)
        {
            BlockedMs += Math.Max(0, nowMs - _blockedSince);
        }

        _blockedNow = why;
    }

    /// 끝날 때 아직 막혀 있으면 그 시간을 더합니다.
    public void Close(long nowMs)
    {
        if (_blockedNow is not null)
        {
            BlockedMs += Math.Max(0, nowMs - _blockedSince);
            _blockedNow = null;
        }
    }

    public int BlockedCount(string why) => _blocked.GetValueOrDefault(why);

    public int BlockedTotal => _blocked.Values.Sum();

    /// 로그 문구: "N batches, X MiB; held back M times (queue a, gap b, loading c) for S s".
    public string Describe() =>
        $"{Batches} batches, {Bytes >> 20} MiB; held back {BlockedTotal} times (queue {BlockedCount(DiskIdleGate.QueueBusy)}, measurement gap {BlockedCount(DiskIdleGate.MeasureGap)}, loading {BlockedCount(DiskIdleGate.LoadingPhase)}) for {BlockedMs / 1000d:F1} s{(EndedBy is null ? string.Empty : ", ended: " + EndedBy)}";
}
