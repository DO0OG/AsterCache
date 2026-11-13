using AsterCache.Prefetch;

namespace AsterCache.Service;

/// 열어 둔 고정 (Dispose 하면 풂)과 고정한 파일 수, 건너뛴 파일 수.
public sealed record PinHandle(IDisposable Pin, int Count, int Skipped);

internal sealed class Nothing : IDisposable
{
    public void Dispose()
    {
    }
}

public sealed record PrewarmResult(string Game, bool Ran, string? SkipReason, StreamStats? Stats, DateTimeOffset At)
{
    /// 기록 없이 예열했을 때의 방식 이름표 ("full" 또는 "profile Unity"), 핫 집합 예열이면 null.
    public string? Mode { get; init; }

    internal string? CancelDetail { get; init; }
}
