using AsterCache.Windows;

namespace AsterCache.Service;

/// 한 플레이의 로딩 판정 입력 공급: 표본 루프가 1초마다 부르면 그 초의 표본 (게임 드라이브 읽기 증가분 - 서비스 자신의 읽기 증가분, 그래픽 값, 실행 파일 시작) 을 만들어 판정 (LoadPhaseBoard) 에 넣고 상태를 돌려줍니다 (표본 루프 스레드 하나에서만 부름).
/// 드라이브 읽기는 표본 루프가 이미 읽는 디스크 성능 값의 앞뒤 차이이고, 서비스 자신의 읽기는 초 단위 기록이 이미 받아 온 프로세스 목록 (raw) 의 서비스 항목, 기록이 꺼진 플레이에서는 같은 조회 (프로세스를 열지 않음) 로 얻습니다.
/// 그래픽 값은 raw 에 있을 때만 씁니다: raw 는 프레임 세션이 열린 (안티치트 게이트를 지난) 플레이에서 초 단위 기록이 켜져 있을 때만 읽히므로, 그렇지 않은 게임에서는 디스크 규칙만 돕니다.
internal sealed class LoadFeed(string game, LoadPhaseBoard board)
{
    private long? _serviceRead;

    public LoadState Next(IReadOnlyList<PlayDiskCounters> previous, IReadOnlyList<PlayDiskCounters> current, double seconds, PlaySeriesRaw? raw, bool exeStarted)
    {
        long? service = raw?.Processes.Where(p => p.Pid == raw.ServicePid).Select(p => (long?)p.ReadBytes).FirstOrDefault() ?? ProcessFaults.OwnReadBytes();
        long serviceDelta = _serviceRead is { } before && service is { } now ? Math.Max(0, now - before) : 0; // 첫 초는 기준만 잡음
        _serviceRead = service;
        long drive = PlaySeriesBuilder.Disk(previous, current)?.ReadBytes ?? 0;
        return board.Add(game, new LoadingSample(seconds, Math.Max(0, drive - serviceDelta), raw?.GpuGameMib, exeStarted)).State;
    }
}
