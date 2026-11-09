using System.Diagnostics;
using AsterCache.Core.Logging;
using AsterCache.Windows;

namespace AsterCache.Service;

/// VolumeDisks 가 알아낸 물리 디스크 단위로 배경 디스크 I/O 를 중재합니다.
public sealed class DiskReadCoordinator
{
    /// 판정 불가 재조회는 볼륨마다 5 초에 한 번만 (기다리지 않고 즉시 다시 조회). 그래도 모르면 기존대로 전체 양보.
    private const long RetryWindowMs = 5000;

    private readonly object _sync = new();
    private readonly List<DiskState> _states = [];
    private readonly VolumeDisks _volumes;
    private readonly ILog _log;
    private readonly Func<long> _nowMs;
    private readonly Dictionary<string, long> _lastRetryMs = new(StringComparer.OrdinalIgnoreCase); // 볼륨 루트별 마지막 재조회 시각 (_sync 안에서만 씀)
    private CancellationToken _serviceToken;

    public DiskReadCoordinator(VolumeDisks volumes, ILog log, Func<long>? nowMs = null)
    {
        _volumes = volumes;
        _log = log;
        _nowMs = nowMs ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
    }

    public void SetServiceToken(CancellationToken serviceToken)
    {
        lock (_sync)
        {
            _serviceToken = serviceToken;
        }
    }

    public sealed class Lease : IDisposable
    {
        private readonly DiskReadCoordinator _owner;
        internal readonly DiskState[] _states;
        private readonly CancellationTokenSource _stop;
        private readonly string? _startGameKey;
        private readonly bool _startStream;
        internal bool _disposed;

        internal Lease(DiskReadCoordinator owner, DiskState[] states, CancellationTokenSource stop, string purpose, string? startGameKey, bool startStream)
        {
            (_owner, _states, _stop, Purpose, _startGameKey, _startStream) = (owner, states, stop, purpose, startGameKey, startStream);
        }

        public string Purpose { get; }

        public CancellationToken Token => _stop.Token;

        public bool IsValid => _owner.IsValid(this);

        internal string? StartGameKey => _startGameKey;

        internal bool IsStartStream => _startStream;

        internal CancellationTokenSource StopSource => _stop;

        public void Dispose() => _owner.End(this);
    }

    internal sealed class DiskState(string volumeRoot)
    {
        public HashSet<string> VolumeRoots { get; } = new(StringComparer.OrdinalIgnoreCase) { volumeRoot };

        public Dictionary<string, string> RunningGames { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// 종료 유예 중인 게임 키 (RunningGames 의 부분집합): 양보는 유지하되 시작 스트림 허용 판단에서만 뺍니다.
        public HashSet<string> Exiting { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<Lease> Active { get; } = [];

        public HashSet<Lease> DrainLeases { get; } = [];

        public CancellationTokenSource Generation { get; set; } = new();

        public bool Yielded { get; set; }

        public long YieldAtMs { get; set; }

        public int LeasesAtYield { get; set; }

        public string PurposeAtYield { get; set; } = string.Empty;

        public bool DrainLogged { get; set; }
    }

    // 시작 스트림 허용: 게임 key 가 등록돼 있고 다른 등록 게임은 모두 종료 유예 중일 때만.
    private static bool StartAllowed(DiskState s, string key) => s.RunningGames.ContainsKey(key) && s.RunningGames.Keys.All(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase) || s.Exiting.Contains(k));

    /// 경로가 쓰는 볼륨 중 하나라도 양보 중이면 null.
    /// preserveForGameRoot: 이미 도는 스트림은 그 게임이 시작해도 유지합니다 (서비스 종료·다른 게임 시작에만 멈춤). allowWhileYielded: 새 게임 시작 스트림 전용.
    public Lease? TryBegin(string path, string purpose, CancellationToken serviceToken = default, string? preserveForGameRoot = null, bool allowWhileYielded = false, string? preserveGameKey = null)
    {
        if (serviceToken.IsCancellationRequested)
        {
            return null;
        }

        lock (_sync)
        {
            string volumeRoot = VolumeRoot(path);
            DiskState[] states = StatesFor(volumeRoot);
            string? startGameKey = preserveForGameRoot is null ? null : preserveGameKey ?? NormalizeRoot(preserveForGameRoot);
            bool startStream = startGameKey is not null;
            if (states.Any(s => s.Yielded && (!allowWhileYielded || startGameKey is null || !StartAllowed(s, startGameKey))))
            {
                return null;
            }

            CancellationToken[] tokens = startStream
                ? [_serviceToken.CanBeCanceled ? _serviceToken : CancellationToken.None] // 서비스 토큰이 없으면 호출자(예열) 토큰에 묶지 않음: 같은 게임 지속 예외가 예열 취소로 끊기지 않게
                : [.. states.Select(s => s.Generation.Token), serviceToken];
            var stop = CancellationTokenSource.CreateLinkedTokenSource(tokens);
            var lease = new Lease(this, states, stop, purpose, startGameKey, startStream);
            foreach (DiskState state in states)
            {
                _ = state.Active.Add(lease);
            }

            return lease;
        }
    }

    /// gameRoot 가 닿는 물리 디스크마다 양보를 걸고 지금 세대의 배경 작업을 취소합니다. gameKey(게임 이름)별로 등록해 같은 설치 폴더의 두 게임도 따로 셉니다.
    public void Yield(string gameRoot, string reason, string? gameKey = null)
    {
        List<CancellationTokenSource> cancel = [];
        List<string> drained = [];
        lock (_sync)
        {
            string volumeRoot = VolumeRoot(gameRoot);
            string root = NormalizeRoot(gameRoot);
            string key = gameKey ?? root;
            DiskState[] states = StatesFor(volumeRoot);
            HashSet<DiskState> currentStates = [.. states];
            foreach (DiskState state in _states.Where(state => !currentStates.Contains(state)))
            {
                if (!state.RunningGames.Remove(key))
                {
                    continue;
                }

                _ = state.Exiting.Remove(key);
                cancel.AddRange(state.Active.Where(lease => string.Equals(lease.StartGameKey, key, StringComparison.OrdinalIgnoreCase)).Select(lease => lease.StopSource));
                ReleaseIfReady(state);
            }

            foreach (DiskState state in states)
            {
                _ = state.Exiting.Remove(key); // 다시 뜬 게임은 유예 해제
                if (state.RunningGames.TryGetValue(key, out string? registeredRoot) && string.Equals(registeredRoot, root, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                state.RunningGames[key] = root;

                if (!state.Yielded)
                {
                    state.Yielded = true;
                    state.YieldAtMs = _nowMs();
                    Lease[] drain = [.. state.Active.Where(x => !x.IsStartStream || !string.Equals(x.StartGameKey, key, StringComparison.OrdinalIgnoreCase))];
                    state.LeasesAtYield = drain.Length;
                    state.PurposeAtYield = string.Join(", ", drain.Select(x => x.Purpose).Distinct(StringComparer.Ordinal));
                    if (state.PurposeAtYield.Length == 0)
                    {
                        state.PurposeAtYield = reason;
                    }

                    state.DrainLeases.UnionWith(drain);
                    cancel.Add(state.Generation);
                    if (state.DrainLeases.Count == 0)
                    {
                        state.DrainLogged = true;
                        drained.Add(DrainLine(state));
                    }
                }

                foreach (Lease lease in state.Active)
                {
                    if (!lease.IsStartStream || !string.Equals(lease.StartGameKey, key, StringComparison.OrdinalIgnoreCase))
                    {
                        cancel.Add(lease.StopSource);
                    }
                }

                ReleaseIfReady(state);
            }
        }

        Cancel(cancel);
        foreach (string line in drained)
        {
            _log.Info(line);
        }

    }

    /// 등록된 게임이 모두 빠지고 남은 대여도 끝난 뒤에만 디스크를 다시 엽니다.
    public void Resume(string gameRoot, string? gameKey = null)
    {
        List<CancellationTokenSource> cancel = [];
        lock (_sync)
        {
            string key = gameKey ?? NormalizeRoot(gameRoot);
            foreach (DiskState state in _states)
            {
                if (!state.RunningGames.Remove(key))
                {
                    continue;
                }

                _ = state.Exiting.Remove(key);
                cancel.AddRange(state.Active.Where(lease => string.Equals(lease.StartGameKey, key, StringComparison.OrdinalIgnoreCase)).Select(lease => lease.StopSource));
                ReleaseIfReady(state);
            }
        }

        Cancel(cancel);
    }

    /// 종료 유예 중인 게임 키 전체를 통째로 갱신합니다 (빠진 키는 유예 해제). 양보는 그대로 두고 시작 스트림 허용 판단에서만 빠집니다.
    public void MarkExiting(IReadOnlySet<string> gameKeys)
    {
        lock (_sync)
        {
            foreach (DiskState state in _states)
            {
                state.Exiting.Clear();
                state.Exiting.UnionWith(state.RunningGames.Keys.Where(gameKeys.Contains));
            }
        }
    }

    /// gameKey 가 어느 디스크에든 양보 등록돼 있는가.
    public bool IsRegistered(string gameKey)
    {
        lock (_sync)
        {
            return _states.Any(state => state.RunningGames.ContainsKey(gameKey));
        }
    }

    public void ReconcileRunningGames(IReadOnlyDictionary<string, string> currentGameRoots)
    {
        List<CancellationTokenSource> cancel = [];
        List<string> released = [];
        lock (_sync)
        {
            foreach (DiskState state in _states)
            {
                foreach ((string key, string root) in state.RunningGames.ToArray())
                {
                    if (currentGameRoots.TryGetValue(key, out string? currentRoot)
                        && string.Equals(NormalizeRoot(currentRoot), root, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    _ = state.RunningGames.Remove(key);
                    _ = state.Exiting.Remove(key);
                    cancel.AddRange(state.Active.Where(lease => string.Equals(lease.StartGameKey, key, StringComparison.OrdinalIgnoreCase)).Select(lease => lease.StopSource));
                    released.Add(key);
                    ReleaseIfReady(state);
                }
            }
        }

        Cancel(cancel);
        foreach (string key in released.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            _log.Info($"disk yield: released stale game registration {key}");
        }
    }

    private bool IsValid(Lease lease)
    {
        lock (_sync)
        {
            return !lease._disposed && !lease.Token.IsCancellationRequested && lease._states.All(state => state.Active.Contains(lease)
                && (!state.Yielded || lease.IsStartStream && lease.StartGameKey is { } key && StartAllowed(state, key)));
        }
    }

    private void End(Lease lease)
    {
        List<string> lines = [];
        lock (_sync)
        {
            if (lease._disposed)
            {
                return;
            }

            lease._disposed = true;
            foreach (DiskState state in lease._states)
            {
                _ = state.Active.Remove(lease);
                if (state.DrainLeases.Remove(lease) && state.DrainLeases.Count == 0 && !state.DrainLogged)
                {
                    state.DrainLogged = true;
                    lines.Add(DrainLine(state));
                }

                ReleaseIfReady(state);
            }
        }

        lease.StopSource.Dispose();
        foreach (string line in lines)
        {
            _log.Info(line);
        }
    }

    private void ReleaseIfReady(DiskState state)
    {
        if (!state.Yielded || state.RunningGames.Count != 0 || state.Active.Count != 0)
        {
            return;
        }

        state.Generation.Dispose();
        state.Generation = new CancellationTokenSource();
        state.Yielded = false;
        state.YieldAtMs = 0;
        state.LeasesAtYield = 0;
        state.PurposeAtYield = string.Empty;
        state.DrainLogged = false;
    }

    private string DrainLine(DiskState state)
    {
        long elapsed = Math.Max(0, _nowMs() - state.YieldAtMs);
        string purpose = state.PurposeAtYield.Length == 0 ? "none" : state.PurposeAtYield;
        return $"disk yield: {state.LeasesAtYield} leases drained in {elapsed} ms ({purpose})";
    }

    private bool Classify(string volumeRoot, List<DiskState> matching)
    {
        bool unknown = false;
        foreach (DiskState state in _states)
        {
            if (state.VolumeRoots.Contains(volumeRoot))
            {
                matching.Add(state); // 같은 볼륨 문자는 매핑을 몰라도 같은 디스크
                continue;
            }

            foreach (string existing in state.VolumeRoots)
            {
                bool? same = _volumes.KnownAreSame(volumeRoot, existing);
                if (same is null)
                {
                    unknown = true;
                    break;
                }

                if (same == true)
                {
                    matching.Add(state);
                    break;
                }
            }
        }

        return unknown;
    }

    private DiskState[] StatesFor(string volumeRoot)
    {
        var matching = new List<DiskState>();
        bool unknown = Classify(volumeRoot, matching);
        long now = _nowMs();
        if (unknown && (!_lastRetryMs.TryGetValue(volumeRoot, out long last) || now - last >= RetryWindowMs))
        {
            _lastRetryMs[volumeRoot] = now;
            matching.Clear();
            unknown = Classify(volumeRoot, matching); // 실패는 VolumeDisks 가 캐시하지 않으므로 다시 조회됨, 성공하면 5 분 캐시
        }

        DiskState[] matches = unknown ? [.. _states] : [.. matching];
        if (matches.Length == 0)
        {
            var state = new DiskState(volumeRoot);
            _states.Add(state);
            return [state];
        }

        if (unknown)
        {
            if (matches.Any(state => state.VolumeRoots.Contains(volumeRoot)))
            {
                return matches;
            }

            var fresh = new DiskState(volumeRoot); // 모를 때도 이 볼륨의 상태를 만들어 함께 양보: 뒤에 매핑되면 실제 디스크로 다시 묶이되 양보 상태는 이어받음
            _states.Add(fresh);
            return [.. matches, fresh];
        }

        foreach (DiskState state in matches)
        {
            _ = state.VolumeRoots.Add(volumeRoot);
        }

        return matches;
    }

    private static string VolumeRoot(string path)
    {
        string? root = Path.GetPathRoot(path);
        if (!string.IsNullOrEmpty(root))
        {
            return root;
        }

        // 물리 매핑을 모르면 서로 독립이라고 말할 수 없으므로 보수적으로 같은 볼륨 묶음으로 취급합니다.
        return path.Length >= 2 && path[1] == ':' ? path[..2] + "\\" : path;
    }

    private static string NormalizeRoot(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            string volume = Path.GetPathRoot(full) ?? string.Empty;
            return full.Length == volume.Length ? full : full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private void Cancel(IEnumerable<CancellationTokenSource> sources)
    {
        foreach (CancellationTokenSource source in sources.Distinct())
        {
            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 양보하는 사이에 작업이 먼저 끝났음.
            }
            catch (AggregateException e)
            {
                _log.Error("disk yield cancellation callback failed", e);
            }
        }
    }
}
