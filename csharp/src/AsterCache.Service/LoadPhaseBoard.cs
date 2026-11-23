using AsterCache.Core.Logging;

namespace AsterCache.Service;

/// 게임별 로딩 판정의 지금 상태: 표본 루프 (PlayRecorder) 가 1초마다 쓰고, 게임이 도는 동안 디스크를 읽는 서비스 경로 (실행 중 캐시 유지 회차, 붙든 범위의 점검) 가 읽습니다.
/// 상태가 바뀔 때만 한 줄을 로그에 남기므로 (시작 사유, 끝 사유와 길이, 그 사이에 미룬 일) 1초마다 찍히지 않습니다. 붙들기를 다시 붙드는 일 (다음 작업) 도 StateOf 로 같은 상태를 읽습니다.
internal sealed class LoadPhaseBoard(ILog log)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _games = new(StringComparer.OrdinalIgnoreCase);

    /// 서비스 자신의 게임 드라이브 읽기 누계: 읽는 쪽이 더하고 입력 공급 (LoadFeed) 이 드라이브 읽기에서 뺍니다.
    public ServiceDiskReads ServiceReads { get; } = new();

    private sealed class Entry
    {
        public LoadDetector Detector { get; } = new();
        public SortedSet<string> Deferred { get; } = []; // 이번 로딩 동안 미룬 일의 이름
    }

    /// 이 게임의 지금 상태 (표본이 아직 없거나 표본 루프가 끝났으면 모름).
    public LoadState StateOf(string game)
    {
        lock (_gate)
        {
            return _games.TryGetValue(game, out Entry? entry) ? entry.Detector.State : LoadState.Unknown;
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
                _games[game] = entry = new Entry();
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
