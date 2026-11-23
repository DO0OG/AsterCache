namespace AsterCache.Service;

/// 게임 안 로딩 판정의 상태: 표본이 아직 없으면 모름.
internal enum LoadState
{
    Unknown,
    Loading,
    NotLoading,
}

/// 로딩이 시작된 사유 (한 표본에서 여럿이면 앞의 것).
internal enum LoadStart
{
    ExeStart, // 실행 파일이 새로 뜸
    Graphics, // 게임의 그래픽 전용 메모리가 짧은 시간에 크게 늘어남
    DiskRead, // 게임 드라이브 읽기가 짧은 시간에 큼
}

/// 로딩이 끝난 사유.
internal enum LoadEnd
{
    Quiet, // 읽기와 그래픽 증가가 모두 작은 상태가 이어짐
    TimeLimit, // 시작 뒤 상한 시간이 지남
}

/// 1초 표본: 시작 뒤 초, 게임 드라이브 읽기 증가분에서 서비스 자신의 읽기를 뺀 값, 게임의 그래픽 전용 메모리 (MiB, 못 읽거나 이 게임에서 읽지 않으면 null), 이 초에 실행 파일이 새로 떴는가.
internal readonly record struct LoadingSample(double Seconds, long DriveReadBytes, int? GameGpuMib, bool ExeStarted);

/// 판정 한 번의 결과: 지금 상태와, 이 표본에서 바뀐 것 (시작·끝 사유, 끝났다면 로딩이었던 시간). DriveBytes 와 GpuRiseMib 는 이 표본에서 잰 창의 값 (로그용).
internal readonly record struct LoadStep(LoadState State, LoadStart? Started = null, LoadEnd? Ended = null, double LoadingSeconds = 0, long DriveBytes = 0, int? GpuRiseMib = null);

/// 로딩 판정의 임계값 (design_w82 2.3). 근거는 Call of Duty 네 플레이의 전환 12개 (그 표본으로 맞춘 값이라 다른 게임에서는 확인하지 않았음).
/// 판정 결과로 하는 일은 서비스가 하려던 디스크 읽기를 미루는 것뿐이고, MaxLoadingSeconds 가 잘못 켜진 판정이 서비스 작업을 계속 막지 못하게 합니다.
internal static class LoadRules
{
    public const double DriveWindowSeconds = 5;
    public const double GpuWindowSeconds = 10;
    public const long StartDriveBytes = 80L << 20; // 드라이브 읽기가 DriveWindowSeconds 에 이 이상이면 시작
    public const int StartGpuMib = 250; // 그래픽 전용 메모리가 GpuWindowSeconds 에 이 이상 늘면 시작
    public const long EndDriveBytes = 40L << 20; // 드라이브 읽기가 DriveWindowSeconds 에 이 미만이고
    public const int EndGpuMib = 64; // 그래픽 증가가 GpuWindowSeconds 에 이 미만인 상태가
    public const double QuietSeconds = 15; // 이 시간 이어지면 끝
    public const double MaxLoadingSeconds = 240; // 관찰된 전환 구간은 41~201 초
    public const double MaxTotalLoadingSeconds = 480; // 로딩 중 실행 파일이 다시 떠도 (런처가 본체를 띄우는 한 번의 이어짐) 이어진 로딩 전체의 상한: 240 초 상한의 두 배. 실행 파일이 계속 다시 떠도 서비스 작업을 이보다 오래 막지 못함
}

/// 로딩 판정: 입력 표본만으로 상태가 정해지는 순수한 논리입니다 (시계·시스템 호출 없음, 표본 루프 스레드 하나에서 부름).
/// 시작은 실행 파일 시작, 그래픽 전용 메모리 증가, 드라이브 읽기 중 하나이고, 끝은 조용한 상태가 QuietSeconds 이어지거나 시작 뒤 MaxLoadingSeconds 가 지난 때입니다.
/// 상한으로 끝난 뒤에는 시작 조건이 한 번 꺼질 때까지 읽기·그래픽 사유로 다시 시작하지 않습니다 (그렇지 않으면 상한이 아무것도 막지 못함). 실행 파일 시작은 언제나 새 로딩입니다.
internal sealed class LoadDetector
{
    private readonly List<LoadingSample> _window = []; // 그래픽 창 (GpuWindowSeconds) 의 기준이 되는 앞쪽 표본 하나를 포함한 최근 표본
    private double _loadingSince; // 현재 240 초 상한의 기준 (로딩 중 실행 파일이 뜨면 다시 셈)
    private double _chainSince; // 이어진 로딩 전체의 시작 (실행 파일이 다시 떠도 그대로)
    private double? _quietSince;
    private bool _rearm; // 상한으로 끝난 뒤 시작 조건이 꺼지기를 기다리는 중
    private bool _byExe; // 지금 로딩이 실행 파일 시작으로 시작됐거나 로딩 중에 실행 파일이 떴음 (그 뒤 읽기·그래픽 신호가 함께 참이어도 실행 파일 시작으로 봄)

    public LoadState State { get; private set; }

    /// 지금 로딩이면 이어진 로딩이 시작된 초, 아니면 null.
    public double? LoadingSince => State == LoadState.Loading ? _chainSince : null;

    /// 지금 로딩이 실행 파일 시작 없이 읽기·그래픽 신호만으로 잡힌 것인가 (경기 안 로딩). 서비스가 디스크 읽기를 줄이는 것은 이때뿐이고, 실행 파일 시작 때의 로딩은 줄이지 않습니다.
    public bool SignalLoading => State == LoadState.Loading && !_byExe;

    /// 마지막으로 받은 표본의 초.
    public double LastSeconds { get; private set; }

    public LoadStep Add(LoadingSample s)
    {
        LastSeconds = s.Seconds;
        _window.Add(s);
        while (_window.Count > 1 && _window[1].Seconds <= s.Seconds - LoadRules.GpuWindowSeconds)
        {
            _window.RemoveAt(0); // 창보다 앞선 표본은 가장 가까운 하나만 기준으로 남김
        }

        long drive = 0;
        foreach (LoadingSample w in _window)
        {
            if (s.Seconds - w.Seconds < LoadRules.DriveWindowSeconds)
            {
                drive += w.DriveReadBytes;
            }
        }

        int? rise = GpuRise(s);
        bool hot = drive >= LoadRules.StartDriveBytes || rise >= LoadRules.StartGpuMib;
        _rearm &= hot;
        if (State != LoadState.Loading)
        {
            LoadStart? why = s.ExeStarted ? LoadStart.ExeStart : _rearm || !hot ? null : rise >= LoadRules.StartGpuMib ? LoadStart.Graphics : LoadStart.DiskRead;
            if (why is null)
            {
                State = LoadState.NotLoading;
                return new LoadStep(State, DriveBytes: drive, GpuRiseMib: rise);
            }

            (State, _loadingSince, _chainSince, _quietSince, _byExe) = (LoadState.Loading, s.Seconds, s.Seconds, null, why == LoadStart.ExeStart);
            return new LoadStep(State, Started: why, DriveBytes: drive, GpuRiseMib: rise);
        }

        if (s.ExeStarted)
        {
            (_loadingSince, _quietSince, _byExe) = (s.Seconds, null, true); // 로딩 중에 실행 파일이 또 뜸: 240 초 상한은 다시 세되 이어진 로딩 전체의 상한 (MaxTotalLoadingSeconds) 은 그대로
            return new LoadStep(State, DriveBytes: drive, GpuRiseMib: rise);
        }

        bool quiet = drive < LoadRules.EndDriveBytes && !(rise >= LoadRules.EndGpuMib);
        _quietSince = quiet ? _quietSince ?? s.Seconds : null;
        LoadEnd? end = s.Seconds - _quietSince >= LoadRules.QuietSeconds ? LoadEnd.Quiet : s.Seconds - _loadingSince >= LoadRules.MaxLoadingSeconds || s.Seconds - _chainSince >= LoadRules.MaxTotalLoadingSeconds ? LoadEnd.TimeLimit : null;
        if (end is null)
        {
            return new LoadStep(State, DriveBytes: drive, GpuRiseMib: rise);
        }

        double length = s.Seconds - _chainSince;
        (State, _quietSince, _rearm) = (LoadState.NotLoading, null, end == LoadEnd.TimeLimit && hot);
        return new LoadStep(State, Ended: end, LoadingSeconds: length, DriveBytes: drive, GpuRiseMib: rise);
    }

    // 그래픽 전용 메모리의 증가: 지금 값 - 창 앞 기준 표본의 값. 둘 중 하나라도 못 읽었거나 0 (카운터가 아직 값을 주지 않음) 이면 null.
    private int? GpuRise(LoadingSample s) =>
        s.GameGpuMib is > 0 && _window[0].GameGpuMib is > 0 && _window[0].Seconds < s.Seconds ? s.GameGpuMib - _window[0].GameGpuMib : null;
}
