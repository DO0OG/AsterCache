using AsterCache.Windows;

namespace AsterCache.Service;

/// 게임 드라이브가 한가한가를 판정하는 한 곳: 서비스가 게임 드라이브에서 물리 읽기를 일으킬 수 있는 발행 지점 (붙들기 채우기, 단계·시작 스트림) 이 같은 기준을 씁니다.
/// 기준: 디스크 대기열이 0 이고 IdleHoldMs 동안 0 이 이어졌으며 계측이 비지 않았을 것. 계측을 읽지 못하면 한가하지 않음으로 봅니다.
/// 대기열은 디스크 성능 값 (IOCTL_DISK_PERFORMANCE) 을 바로 읽습니다: 플레이 기록의 1 초 표본은 간격이 길어 짧은 한가함을 놓치고, 호출 한 번이 IOCTL 두 번이라 비용이 거의 없습니다.
/// 지연 (읽기 한 건의 평균 시간) 은 기준에 넣지 않았습니다: 서비스 자신이 방금 낸 읽기가 지연에 섞여 게임의 읽기와 가릴 수 없기 때문입니다.
internal sealed class DiskIdleGate : IDisposable
{
    /// 대기열 0 이 이만큼 이어져야 한가함: 게임의 읽기 한 건이 대기열에 머무는 시간 (하드디스크 수 ms~수십 ms) 보다 길고, 채우기 한 묶음이 끝난 뒤 곧 다시 판정할 수 있는 짧은 값입니다 (설계 범위 200~500 ms). 실측으로 고른 값이 아닙니다.
    public const long IdleHoldMs = 300;

    /// 한가함을 기다리는 동안 대기열을 다시 읽는 간격.
    public const long PollMs = 50;

    /// 이보다 오래 판정하지 않았으면 그 사이 게임이 읽었는지 알 수 없어 0 이 이어진 시간을 처음부터 다시 잽니다 (자기 읽기 중에는 Own 이 이 시각을 이어 줌).
    public const long ContinuityMs = 250;

    public const string QueueBusy = "queue";
    public const string MeasureGap = "gap";
    public const string LoadingPhase = "loading";

    private sealed class Track
    {
        public long ZeroSince = -1;
        public long LastAt;
    }

    private readonly object _sync = new();
    private readonly Dictionary<string, Track> _tracks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Func<string, long?> _queueOf;
    private readonly Func<long> _nowMs;
    private readonly Action<int> _sleep;
    private readonly IDisposable? _owner;

    /// queueOf: 게임 설치 경로 → 그 디스크의 지금 대기열 (읽지 못하면 null). nowMs·sleep: 시험이 시계를 흉내 냅니다.
    public DiskIdleGate(Func<string, long?> queueOf, Func<long>? nowMs = null, Action<int>? sleep = null, IDisposable? owner = null)
    {
        _queueOf = queueOf;
        _nowMs = nowMs ?? (() => Environment.TickCount64);
        _sleep = sleep ?? (ms => Thread.Sleep(ms));
        _owner = owner;
    }

    /// 판정 결과: 한가하면 Why 가 null, 아니면 QueueBusy·MeasureGap.
    public readonly record struct Verdict(string? Why)
    {
        public bool Idle => Why is null;
    }

    /// 지금 한가한가. 대기열이 0 이어도 IdleHoldMs 가 아직 안 찼으면 그만큼 (최대 IdleHoldMs) 짧게 지켜보다 판정합니다 (호출한 스레드가 잠깐 기다림).
    public Verdict Check(string key)
    {
        Track track = TrackOf(key);
        while (true)
        {
            long now = _nowMs();
            if (_queueOf(key) is not { } queue)
            {
                Reset(track);
                return new Verdict(MeasureGap);
            }

            lock (_sync)
            {
                if (queue != 0)
                {
                    (track.ZeroSince, track.LastAt) = (-1, now);
                    return new Verdict(QueueBusy);
                }

                if (track.ZeroSince < 0 || now - track.LastAt > ContinuityMs)
                {
                    track.ZeroSince = now; // 앞 판정과 이어지는지 알 수 없음: 지금부터 다시 잼
                }

                track.LastAt = now;
                if (now - track.ZeroSince >= IdleHoldMs)
                {
                    return new Verdict(null);
                }
            }

            _sleep((int)PollMs);
        }
    }

    /// 서비스가 이 디스크를 직접 읽는 동안 감싸는 범위: 그 사이에는 대기열이 자기 읽기로 차 있으므로 0 이 이어진 시간을 끊지 않고, 끝나면 이어서 판정합니다 (그 사이 게임이 낸 읽기는 대기열에 남아 있으면 다음 판정이 잡음).
    public IDisposable Own(string key) => new OwnScope(this, TrackOf(key));

    public void Dispose() => _owner?.Dispose();

    private Track TrackOf(string key)
    {
        lock (_sync)
        {
            if (!_tracks.TryGetValue(key, out Track? track))
            {
                _tracks[key] = track = new Track();
            }

            return track;
        }
    }

    private void Reset(Track track)
    {
        lock (_sync)
        {
            track.ZeroSince = -1;
        }
    }

    private sealed class OwnScope(DiskIdleGate gate, Track track) : IDisposable
    {
        public void Dispose()
        {
            lock (gate._sync)
            {
                if (track.ZeroSince >= 0)
                {
                    track.LastAt = gate._nowMs();
                }
            }
        }
    }

    /// 실제 환경: 게임 설치 경로가 놓인 물리 디스크의 대기열을 디스크 성능 값에서 읽습니다. 디스크마다 모니터를 한 번 열고 닫지 않으며, 열지 못하면 5 초 뒤에 다시 시도합니다 (그동안은 계측 공백).
    public static DiskIdleGate Windows()
    {
        var reader = new WindowsQueue();
        return new DiskIdleGate(reader.Read, owner: reader);
    }

    private sealed class WindowsQueue : IDisposable
    {
        private const long RetryMs = 5000;
        private readonly object _lock = new();
        private readonly Dictionary<string, (DiskPerformanceMonitor? Monitor, long RetryAt)> _monitors = new(StringComparer.OrdinalIgnoreCase);

        public long? Read(string path)
        {
            lock (_lock)
            {
                long now = Environment.TickCount64;
                if (!_monitors.TryGetValue(path, out (DiskPerformanceMonitor? Monitor, long RetryAt) entry) || (entry.Monitor is null && now >= entry.RetryAt))
                {
                    _monitors[path] = entry = (DiskInfo.OpenPerformanceMonitor(path), now + RetryMs);
                }

                if (entry.Monitor?.Read() is not { } samples)
                {
                    return null;
                }

                long queue = 0;
                foreach (DiskPerformanceSample sample in samples)
                {
                    queue += sample.QueueDepth;
                }

                return queue;
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                foreach ((DiskPerformanceMonitor? monitor, _) in _monitors.Values)
                {
                    monitor?.Dispose();
                }

                _monitors.Clear();
            }
        }
    }
}

/// 한 번의 채우기나 스트림이 한가함 판정으로 한 일: 낸 묶음과 바이트, 판정에 막힌 횟수 (막히기 시작할 때마다 한 번, 사유별)와 막혀 있던 시간, 끝난 사유. 한 스레드에서만 씁니다.
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
