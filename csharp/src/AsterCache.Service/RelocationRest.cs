using AsterCache.Core.Logging;

namespace AsterCache.Service;

/// 재배치가 쓰기 조각 사이마다 다른 프로그램의 디스크 활동을 보고 쉬는 규칙 (ApplyOptions.CanIssue 에 연결, 재배치 한 번에 하나).
/// 활동이 보이면 조각을 내지 않고 쉬다가 (저널이 재개 지점이라 어느 조각 사이에서 멈춰도 안전) 한가해지면 이어 갑니다.
/// 너무 오래 (MaxRest) 쉬었으면 그 회차를 끝내고 (취소와 같은 중단) 다음 기회에 이어 감: 시작 조건이 한가함을 다시 봅니다.
/// 서비스 자신의 쓰기는 판정에서 빠집니다: 관측 때 이 스레드는 쓰지 않고 (남은 쓰기가 빠져나갈 시간을 둔 뒤) 그 멈춘 구간만 재기 때문입니다.
/// optimizing: 예약 드라이브 최적화가 도는 중인가 (DriveOptimization.Blocking). 값이 싸서 관측 간격과 무관하게 조각마다 보고, 도는 동안은 디스크 관측 없이 쉽니다.
internal sealed class RelocationRest(string game, ILog log, Func<(double? Bps, DiskActivity.SampleState State)> observe, Action<TimeSpan, CancellationToken> sleep, Func<DateTimeOffset> now, Func<bool>? optimizing = null)
{
    // 한가한 동안 관측하는 간격: 관측은 멈춤 한 번 (ObserveDrain + ObserveWindow, 약 1 초) 이라 이 간격이면 한가할 때 재배치 속도를 3% 남짓만 줄임 (예열의 한가함 연장 간격과 같음).
    public static readonly TimeSpan ObserveEvery = TimeSpan.FromSeconds(30);

    // 쉬는 동안 다시 관측하기 전의 대기.
    public static readonly TimeSpan RestPoll = TimeSpan.FromSeconds(4);

    // 이만큼 쉬어도 한가해지지 않으면 이번 회차를 끝냄.
    public static readonly TimeSpan MaxRest = TimeSpan.FromMinutes(10);

    // 관측 멈춤의 앞부분: 직전 조각의 쓰기 꼬리가 빠져나가게 비우는 시간 (예열의 양보 관측과 같음), 그 뒤 ObserveWindow 만 잼.
    public static readonly TimeSpan ObserveDrain = TimeSpan.FromMilliseconds(200);

    public static readonly TimeSpan ObserveWindow = TimeSpan.FromMilliseconds(800);

    private readonly object _lock = new();
    private DateTimeOffset _observedAt = now(); // 시작 조건이 방금 한가함을 봤으므로 첫 관측은 한 간격 뒤

    /// 다음 조각을 내도 되는가: 취소됐거나 MaxRest 를 넘겨 쉬었으면 false. 동시 실행의 다른 스레드는 관측·쉼이 끝날 때까지 여기서 기다림.
    public bool MayIssue(CancellationToken stop)
    {
        lock (_lock)
        {
            if (stop.IsCancellationRequested)
            {
                return false;
            }

            bool optimizingNow = optimizing?.Invoke() == true;
            if (!optimizingNow && now() - _observedAt < ObserveEvery)
            {
                return true;
            }

            if (!optimizingNow && !Busy())
            {
                _observedAt = now();
                return true;
            }

            log.Info(optimizingNow
                ? $"{game}: relocation yields to the drive optimization (resting between pieces)"
                : $"{game}: relocation yields to other disk activity (resting between pieces)");
            DateTimeOffset since = now();
            while (!stop.IsCancellationRequested)
            {
                if (now() - since >= MaxRest)
                {
                    log.Info($"{game}: relocation stops this round after {(now() - since).TotalSeconds:F0} s of rest, other disk activity continues (resumes when the drive is idle)");
                    return false;
                }

                sleep(RestPoll, stop);
                if (!stop.IsCancellationRequested && optimizing?.Invoke() != true && !Busy())
                {
                    _observedAt = now();
                    log.Info($"{game}: relocation resumes after {(now() - since).TotalSeconds:F1} s of rest");
                    return true;
                }
            }

            return false;
        }
    }

    // 바쁨은 값을 읽었고 BusyBytesPerSecond 이상일 때만: 못 읽거나 아직 모르는 표본 때문에 재배치를 막지 않음 (시작 조건이 이미 한가함을 봤고, 고장으로 영구히 막지 않는 기존 규칙).
    private bool Busy() => observe() is (double bps, DiskActivity.SampleState.Ok) && bps >= DiskActivity.BusyBytesPerSecond;
}
