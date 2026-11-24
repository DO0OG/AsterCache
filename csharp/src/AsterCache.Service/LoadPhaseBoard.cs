using AsterCache.Core.Logging;

namespace AsterCache.Service;

/// 게임별 로딩 판정의 지금 상태: 표본 루프 (PlayRecorder) 가 1초마다 쓰고, 게임이 도는 동안 디스크를 읽는 서비스 경로 (실행 중 캐시 유지 회차, 붙든 범위의 점검) 가 읽습니다.
/// 상태가 바뀔 때만 한 줄을 로그에 남기므로 (시작 사유, 끝 사유와 길이, 그 사이에 미룬 일) 1초마다 찍히지 않습니다. 붙들기를 다시 붙드는 일도 StateOf 와 QueueOf 로 같은 상태를 읽습니다.
internal sealed class LoadPhaseBoard(ILog log)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _games = new(StringComparer.OrdinalIgnoreCase);
    private long _epoch; // 게임 상태가 새로 생길 때마다 늘어남 (디스크 표본 누계가 어느 상태의 것인지 가름)

    /// 서비스 자신의 게임 드라이브 읽기 누계: 읽는 쪽이 더하고 입력 공급 (LoadFeed) 이 드라이브 읽기에서 뺍니다.
    public ServiceDiskReads ServiceReads { get; } = new();

    private sealed class Entry
    {
        public LoadDetector Detector { get; } = new();
        public SortedSet<string> Deferred { get; } = []; // 이번 로딩 동안 미룬 일의 이름
        public long? Queue { get; set; } // 게임 드라이브의 디스크 대기열 (가장 최근 표본)
        public DiskTally Tally { get; set; } // 표본 루프가 이미 읽은 디스크 값의 누계 (스트림 종료 로그가 구간 차이를 구함)
    }

    /// 이 게임의 디스크 표본 누계 (표본이 아직 없으면 0).
    public DiskTally TallyOf(string game)
    {
        lock (_gate)
        {
            return _games.TryGetValue(game, out Entry? entry) ? entry.Tally : default;
        }
    }

    /// 표본 루프가 이 초의 디스크 표본 (대기열 합, 읽기 횟수, 읽기 시간 합 ms) 을 누계에 더합니다 (판정 입력이 아님, 새 조회 없음).
    public void NoteDiskSample(string game, long queue, ulong reads, double readMs)
    {
        lock (_gate)
        {
            if (_games.TryGetValue(game, out Entry? entry))
            {
                entry.Tally = entry.Tally.Add(queue, reads, readMs);
            }
        }
    }

    /// 이 게임의 지금 상태 (표본이 아직 없거나 표본 루프가 끝났으면 모름).
    public LoadState StateOf(string game)
    {
        lock (_gate)
        {
            return _games.TryGetValue(game, out Entry? entry) ? entry.Detector.State : LoadState.Unknown;
        }
    }

    /// 이 게임 드라이브의 가장 최근 디스크 대기열 (표본 루프가 1초마다 읽은 값, 표본이 없으면 모름): 다시 붙들기가 디스크를 읽어도 되는지 봅니다.
    public long? QueueOf(string game)
    {
        lock (_gate)
        {
            return _games.TryGetValue(game, out Entry? entry) ? entry.Queue : null;
        }
    }

    /// 표본 루프가 이 초의 디스크 대기열을 남깁니다 (판정 입력이 아님).
    public void NoteQueue(string game, long queue)
    {
        lock (_gate)
        {
            if (_games.TryGetValue(game, out Entry? entry))
            {
                entry.Queue = queue;
            }
        }
    }

    /// 지금 로딩이 읽기·그래픽 신호로만 잡힌 경기 안 로딩이면 참: 호출한 쪽은 이번 디스크 읽기를 줄이거나 건너뜁니다. 그 일의 이름은 로딩이 끝날 때 로그 한 줄에 함께 남습니다.
    /// 실행 파일 시작으로 시작된 로딩 (로딩 중에 실행 파일이 뜬 경우 포함) 에서는 거짓입니다: 실행 파일 시작 때의 동작은 로딩 판정이 없을 때와 같아야 합니다. 상태 자체 (StateOf) 와 로그·기록 열은 두 경우 모두 로딩입니다.
    public bool Defers(string game, string what)
    {
        lock (_gate)
        {
            if (!_games.TryGetValue(game, out Entry? entry) || !entry.Detector.SignalLoading)
            {
                return false;
            }

            _ = entry.Deferred.Add(what);
            return true;
        }
    }

    /// 표본 하나를 넣고 상태가 바뀌었으면 로그에 남깁니다.
    public LoadStep Add(string game, LoadingSample sample)
    {
        LoadStep step;
        string? line = null;
        lock (_gate)
        {
            if (!_games.TryGetValue(game, out Entry? entry))
            {
                _games[game] = entry = new Entry { Tally = new DiskTally(0, 0, 0, 0, ++_epoch) };
            }

            step = entry.Detector.Add(sample);
            if (step.Started is { } started)
            {
                entry.Deferred.Clear();
                line = $"{game}: loading phase started ({StartText(started, step)})";
            }
            else if (step.Ended is { } ended)
            {
                line = EndLine(game, EndText(ended), step.LoadingSeconds, entry.Deferred);
            }
        }

        if (line is not null)
        {
            log.Info(line);
        }

        return step;
    }

    /// 표본 루프가 끝났을 때 이 게임의 상태를 지웁니다 (로딩 중이었으면 그 끝을 남김): 표본이 없는데 로딩 상태가 남아 서비스의 일을 막지 않게.
    public void Forget(string game)
    {
        string? line = null;
        lock (_gate)
        {
            if (_games.Remove(game, out Entry? entry) && entry.Detector.LoadingSince is { } since)
            {
                line = EndLine(game, "play ended", entry.Detector.LastSeconds - since, entry.Deferred);
            }
        }

        if (line is not null)
        {
            log.Info(line);
        }
    }

    private static string StartText(LoadStart why, LoadStep step) => why switch
    {
        LoadStart.ExeStart => "executable started",
        LoadStart.Graphics => $"graphics memory +{step.GpuRiseMib} MiB within {LoadRules.GpuWindowSeconds:F0} s",
        _ => $"drive read {step.DriveBytes >> 20} MiB within {LoadRules.DriveWindowSeconds:F0} s",
    };

    private static string EndText(LoadEnd why) => why == LoadEnd.Quiet ? $"quiet for {LoadRules.QuietSeconds:F0} s" : $"{LoadRules.MaxLoadingSeconds:F0} s limit";

    private static string EndLine(string game, string why, double seconds, SortedSet<string> deferred) =>
        $"{game}: loading phase ended after {seconds:F0} s ({why}){(deferred.Count == 0 ? string.Empty : "; held back: " + string.Join(", ", deferred))}";
}

/// 게임 드라이브의 1초 표본 누계: 표본 수, 대기열이 있던 표본 수, 읽기 횟수와 읽기 시간 합 (ms). 스트림 하나가 도는 동안의 값은 끝의 누계에서 시작의 누계를 빼 구합니다.
internal readonly record struct DiskTally(long Samples, long Queued, ulong Reads, double ReadMs, long Epoch = 0)
{
    public DiskTally Add(long queue, ulong reads, double readMs) => new(Samples + 1, Queued + (queue > 0 ? 1 : 0), Reads + reads, ReadMs + readMs, Epoch);

    /// before 이후의 값 (게임 상태가 지워졌다 다시 생겼으면 — 상태 번호가 다르거나 누계가 줄었으면 — 모름: 표본 없음).
    public DiskTally Since(DiskTally before) => (before.Epoch != 0 && Epoch != before.Epoch) || Samples < before.Samples || Reads < before.Reads ? default : new(Samples - before.Samples, Queued - before.Queued, Reads - before.Reads, ReadMs - before.ReadMs, Epoch);

    /// 로그 한 줄에 붙일 말: 대기열이 있던 표본의 비율과 읽기 한 번의 평균 시간 (1초 표본의 평균이라 순간 값이 아님).
    public string Text() => Samples == 0
        ? "disk samples none"
        : FormattableString.Invariant($"disk queue present in {100d * Queued / Samples:F0}% of {Samples} samples, mean read latency {(Reads == 0 ? "n/a" : $"{ReadMs / Reads:F1} ms")}");
}
