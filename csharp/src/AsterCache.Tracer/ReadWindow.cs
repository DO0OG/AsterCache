namespace AsterCache.Tracer;

/// 읽기를 기록하는 구간의 열림 여부와 읽기 수 집계. 이벤트 스레드가 세고 서비스 스레드가 여닫고 읽으므로 잠금 없이 Interlocked 로 다룹니다.
internal sealed class ReadWindow
{
    private volatile bool _open = true;
    private long _reads;

    public bool IsOpen => _open;

    public void Close() => _open = false;

    public void Reopen() => _open = true;

    /// 기록한 읽기 하나를 셉니다.
    public void Count() => _ = Interlocked.Increment(ref _reads);

    /// 지난 호출 뒤의 읽기 수를 읽고 비웁니다.
    public long Take() => Interlocked.Exchange(ref _reads, 0);
}
