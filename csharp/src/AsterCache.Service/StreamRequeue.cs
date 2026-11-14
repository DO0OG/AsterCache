using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 한 예열 스트림이 만지는 데 쓴 양: 만진 바이트, 걸린 시간 (초), 디스크 읽기가 감지돼 만지기가 멈췄는가, 스파스 파일이라 만지지 않은 바이트.
/// Protected*: 스트림이 디스크를 읽기 전과 읽는 도중에 표식이 있는 다른 게임을 만진 양 (서로 다른 게임 수, 바이트, 걸린 시간, 보호한 횟수): 묶음을 다시 만지는 것과 따로 셈.
internal readonly record struct RequeueTally(long Bytes, double Seconds, bool DiskStopped, long SkippedBytes = 0, int ProtectedGames = 0, long ProtectedBytes = 0, double ProtectedSeconds = 0, int ProtectedTimes = 0)
{
    public static RequeueTally operator +(RequeueTally a, RequeueTally b) => new(a.Bytes + b.Bytes, a.Seconds + b.Seconds, a.DiskStopped || b.DiskStopped, a.SkippedBytes + b.SkippedBytes,
        a.ProtectedGames + b.ProtectedGames, a.ProtectedBytes + b.ProtectedBytes, a.ProtectedSeconds + b.ProtectedSeconds, a.ProtectedTimes + b.ProtectedTimes);

    /// 예열 완료 로그 줄 끝에 붙이는 말 (만진 것도 멈춘 것도 건너뛴 것도 지킨 것도 없으면 빈 문자열, MiB 로 0 인 스파스·보호 꼬리는 붙이지 않음, 두 번 이상 보호했으면 횟수를 덧붙임).
    public string Note() => RequeuedNote() + (ProtectedGames > 0 && ProtectedBytes >> 20 > 0 ? $", protected {ProtectedGames} games ({ProtectedBytes >> 20} MiB in {ProtectedSeconds:F1} s){(ProtectedTimes > 1 ? $", {ProtectedTimes} times" : string.Empty)}" : string.Empty);

    private string RequeuedNote() => Bytes >> 20 > 0 || DiskStopped || SkippedBytes >> 20 > 0 ? $", requeued {Bytes >> 20} MiB in {Seconds:F1} s{(SkippedBytes >> 20 > 0 ? $" ({SkippedBytes >> 20} MiB in sparse files not touched)" : string.Empty)}{(DiskStopped ? ", requeue stopped by disk reads" : string.Empty)}" : string.Empty;
}

/// 예열 스트림이 묶음을 읽을 때마다 그 범위를 곧바로 다시 만집니다 (SOLAR 의 만지기, 디스크 읽기 없음이 전제).
/// PrefetchVirtualMemory 는 이미 캐시에 있는 페이지의 순서를 바꾸지 않으므로, 빠진 부분만 읽는 재예열은 그 게임의 나머지 페이지를 이전 예열 때의 오래된 순서로 남깁니다.
/// 읽은 범위를 바로 만지면 건너뛴 페이지도 대기 목록의 뒤로 가서, 스트림이 끝났을 때 그 게임의 모든 페이지가 이번 스트림 동안의 것이 됩니다.
/// 만지기의 실패·취소·디스크 읽기 감지는 스트림의 결과를 바꾸지 않습니다 (스트림의 일은 읽는 것): 예외는 삼키고 한 번만 경고합니다.
/// 한 묶음의 만지기가 디스크 읽기를 만나 멈추면 그 스트림의 남은 묶음은 만지지 않습니다 (묶음마다 새 허용치로 디스크 읽기를 되풀이하지 않게). 이어 읽기의 건너뛴 앞부분 만지기는 이 판정과 따로입니다.
/// onTouched: 만지는 데 쓴 시간 (초) 을 알림 (양보 시간 계산에서 빼는 데 씀).
internal sealed class StreamRequeue(Func<IReadOnlyList<ReadOp>, CancellationToken, TouchResult> touch, ILog log, string game, Func<bool> halted, Action<double>? onTouched = null)
{
    private bool _warned;
    private bool _batchesStopped;

    public long Bytes { get; private set; }

    public double Seconds { get; private set; }

    public bool DiskStopped { get; private set; }

    public long SkippedBytes { get; private set; }

    public RequeueTally Tally => new(Bytes, Seconds, DiskStopped, SkippedBytes);

    /// 방금 읽은 범위를 읽은 순서대로 만집니다. 스트림이 멈췄거나 (stop) 게임이 시작했거나 트리거가 멈추게 했으면 (halted), 또는 앞 묶음의 만지기가 디스크 읽기로 멈췄으면 만지지 않습니다.
    public void Touch(IReadOnlyList<ReadOp> ranges, CancellationToken stop) => Run(ranges, stop, skipped: false);

    /// 이어 읽기가 건너뛴 앞부분 (이전 회에 읽은, 가장 중요한 부분) 을 꼬리를 읽기 전에 한 번 만집니다. 디스크 읽기로 멈춰도 이 만지기만 그만두고, 꼬리 묶음의 만지기는 따로 판정합니다.
    public void TouchSkipped(IReadOnlyList<ReadOp> ranges, CancellationToken stop) => Run(ranges, stop, skipped: true);

    private void Run(IReadOnlyList<ReadOp> ranges, CancellationToken stop, bool skipped)
    {
        if (ranges.Count == 0 || stop.IsCancellationRequested || halted() || (!skipped && _batchesStopped))
        {
            return;
        }

        try
        {
            TouchResult r = touch(ranges, stop);
            Bytes += r.Bytes;
            Seconds += r.Seconds;
            SkippedBytes += r.SkippedBytes;
            onTouched?.Invoke(r.Seconds);
            if (r.End == TouchEnd.NeedsDiskReads)
            {
                DiskStopped = true;
                _batchesStopped |= !skipped;
            }
        }
        catch (OperationCanceledException)
        {
            // 스트림이 멈춤: 아무것도 하지 않음
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            if (!_warned)
            {
                _warned = true;
                log.Warn($"{game}: requeue of the read batches skipped: {e.GetType().Name}"); // 예열 결과에는 영향을 주지 않음
            }
        }
    }
}
