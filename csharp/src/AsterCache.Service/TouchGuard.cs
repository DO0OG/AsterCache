namespace AsterCache.Service;

/// 재참조 한 번 (묶음 전체) 의 실행 기한과 양보: 기한이 지나거나 halted (대기 중인 트리거·게임 시작 등) 가 참이 되면 Token 을 취소해 만지기가 다음 조각 (1 MiB) 에서 멈추게 합니다.
/// 기한은 처리 속도를 어림해 정하는 것이 아니라 타이머 (단조 시계) 로 실행 중에 지키고, halted 는 PollEvery 마다 확인합니다. 바깥 토큰 (게임 시작 등) 도 그대로 이어받습니다.
internal sealed class TouchGuard : IDisposable
{
    internal static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(50);

    private readonly CancellationTokenSource _cts;
    private readonly CancellationToken _outer;
    private readonly Timer _poll;
    private volatile bool _yielded;

    public TouchGuard(CancellationToken outer, TimeSpan deadline, Func<bool> halted)
    {
        _outer = outer;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(outer);
        _cts.CancelAfter(deadline);
        _poll = new Timer(_ =>
        {
            try
            {
                if (halted())
                {
                    _yielded = true;
                    _cts.Cancel();
                }
            }
            catch (Exception e) when (e is ObjectDisposedException or InvalidOperationException)
            {
                // 끝난 뒤 늦게 불린 타이머: 아무것도 하지 않음
            }
        }, null, PollEvery, PollEvery);
    }

    public CancellationToken Token => _cts.Token;

    /// 기한이 지나서 멈췄는가 (양보나 바깥 취소가 아님).
    public bool DeadlineHit => _cts.IsCancellationRequested && !_yielded && !_outer.IsCancellationRequested;

    /// 대기 중인 계기가 있어 양보하며 멈췄는가.
    public bool Yielded => _yielded;

    public void Dispose()
    {
        _poll.Dispose();
        _cts.Dispose();
    }
}

/// Rewarmer.RefreshGroup 가 한 번의 재참조에서 만진 양과 멈춘 사유를 알리는 상자 (로그용).
internal sealed class RefreshReport
{
    private bool _byDisk;
    private string _by = string.Empty;

    /// 완전히 또는 일부만 만진 바이트 (끝까지 못 만지고 멈춘 것 포함).
    public long Bytes { get; private set; }

    /// 로그 줄 끝에 붙일 말 (멈춘 사유가 있을 때만 앞에 쉼표).
    public string Note => (_byDisk ? ", stopped at data missing from memory (the drive is busy or its load is unknown)" : string.Empty) + _by;

    public void Add(long bytes) => Bytes += bytes;

    public void StopByDisk() => _byDisk = true;

    public void Stopped(TouchGuard? guard) => _by = guard is { DeadlineHit: true } ? ", stopped at the time limit" : guard is { Yielded: true } ? ", stopped for a waiting trigger" : string.Empty;
}
