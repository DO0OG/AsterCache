using AsterCache.Core.Apply;

namespace AsterCache.Tests;

/// 다른 스레드가 잡고 있는 DataLock (같은 스레드의 재진입과 구별하려는 busy 시험용). Dispose 하면 그 스레드가 놓습니다.
internal sealed class ForeignLock : IDisposable
{
    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _thread;

    public ForeignLock(string planPath, string? journalPath = null)
    {
        using var ready = new ManualResetEventSlim();
        bool held = false;
        _thread = new Thread(() =>
        {
            using DataLock? gate = DataLock.TryAcquire(planPath, journalPath: journalPath);
            held = gate is not null;
            ready.Set();
            if (held)
            {
                _release.Wait();
            }
        })
        { IsBackground = true };
        _thread.Start();
        ready.Wait();
        if (!held)
        {
            throw new InvalidOperationException("foreign lock could not be taken");
        }
    }

    public void Dispose()
    {
        _release.Set();
        _thread.Join();
    }
}
