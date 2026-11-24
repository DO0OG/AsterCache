namespace AsterCache.Service;

/// 한 플레이의 로딩 판정 입력 공급: 표본 루프가 1초마다 부르면 그 초의 표본 (게임 드라이브 읽기 증가분 - 서비스가 그 드라이브에서 직접 읽은 증가분, 그래픽 값, 실행 파일 시작) 을 만들어 판정 (LoadPhaseBoard) 에 넣고 상태를 돌려줍니다 (표본 루프 스레드 하나에서만 부름).
/// 드라이브 읽기는 표본 루프가 이미 읽는 디스크 성능 값의 앞뒤 차이이고, 서비스의 읽기는 서비스가 읽을 때마다 세는 누계 (ServiceDiskReads) 의 앞뒤 차이입니다: 새 조회가 없습니다.
/// 그래픽 값은 raw 에 있을 때만 씁니다: raw 는 프레임 세션이 열린 (안티치트 게이트를 지난) 플레이에서 초 단위 기록이 켜져 있을 때만 읽히므로, 그렇지 않은 게임에서는 디스크 규칙만 돕니다.
internal sealed class LoadFeed(string game, string root, LoadPhaseBoard board)
{
    private long _serviceRead = board.ServiceReads.Total(root); // 세션이 시작할 때의 누계가 기준

    /// 가장 최근 초에 서비스가 이 드라이브에서 직접 읽은 바이트 (초 단위 기록의 service_read_bytes).
    public long LastServiceBytes { get; private set; }

    public LoadState Next(IReadOnlyList<PlayDiskCounters> previous, IReadOnlyList<PlayDiskCounters> current, double seconds, PlaySeriesRaw? raw, bool exeStarted)
    {
        long service = board.ServiceReads.Total(root);
        long serviceDelta = Math.Max(0, service - _serviceRead);
        _serviceRead = service;
        LastServiceBytes = serviceDelta;
        long drive = PlaySeriesBuilder.Disk(previous, current)?.ReadBytes ?? 0;
        LoadState state = board.Add(game, new LoadingSample(seconds, Math.Max(0, drive - serviceDelta), raw?.GpuGameMib, exeStarted)).State;
        board.NoteQueue(game, current.Sum(c => (long)c.QueueDepth)); // 이미 읽은 디스크 성능 값의 대기열: 다시 붙들기가 디스크를 읽어도 되는지 봄
        return state;
    }
}
