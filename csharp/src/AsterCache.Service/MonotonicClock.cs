using System.Diagnostics;

namespace AsterCache.Service;

/// 시작할 때 한 번 맞춘 벽시계 + Stopwatch 경과로 가는 시계. 실행 중 시스템 시계가 앞뒤로 튀어도 (사용자 조정, 시간 동기화) 경과 시간이 변하지 않아
/// 회복 대기·일일 창이 갑자기 끝나거나 늘어나지 않습니다. 저장된 시각은 다시 시작할 때 현재 시계에 맞춰 읽힙니다.
public sealed class MonotonicClock
{
    private readonly Func<long> _ticks;
    private readonly DateTimeOffset _anchor;
    private readonly long _anchorTicks;

    /// wall·ticks 는 시험이 바꿈 (ticks 는 Stopwatch 틱).
    public MonotonicClock(Func<DateTimeOffset>? wall = null, Func<long>? ticks = null)
    {
        _ticks = ticks ?? Stopwatch.GetTimestamp;
        _anchor = (wall ?? (() => DateTimeOffset.UtcNow))();
        _anchorTicks = _ticks();
    }

    public static MonotonicClock Shared { get; } = new();

    public DateTimeOffset Now() => _anchor + Stopwatch.GetElapsedTime(_anchorTicks, _ticks());
}
