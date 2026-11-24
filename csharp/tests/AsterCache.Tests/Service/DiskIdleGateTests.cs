using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임 드라이브의 한가함 판정: 가짜 시계와 가짜 대기열로 0 이 이어진 시간, 끊김, 계측 공백, 묶음마다 새로 재는 것, 같은 디스크의 순번 직렬화를 시험합니다.
public sealed class DiskIdleGateTests
{
    private const string Key = "D:";
    private long _now = 1000;
    private long? _queue = 0;
    private readonly List<long?> _script = [];
    private int _reads;

    private DiskIdleGate Gate() => new(_ =>
    {
        _reads++;
        return _script.Count > 0 ? _script[Math.Min(_reads - 1, _script.Count - 1)] : _queue;
    }, () => _now, ms => _now += ms);

    [Fact]
    public void AQueueOfZeroIsIdleOnlyAfterItStayedZeroForTheHoldTimeAndTheCallerWaitsForThatTime()
    {
        DiskIdleGate gate = Gate();
        long start = _now;

        using DiskIdleGate.Turn? turn = gate.TryBegin(Key, CancellationToken.None, out string? why);

        Assert.NotNull(turn);
        Assert.Null(why);
        Assert.Equal(DiskIdleGate.IdleHoldMs, _now - start); // 0 이 이어진 시간을 채울 때까지 지켜봄
    }

    [Fact]
    public void ANonZeroQueueIsBusyAndLeavesTheTurnFree()
    {
        _queue = 2;
        DiskIdleGate gate = Gate();

        DiskIdleGate.Turn? busy = gate.TryBegin(Key, CancellationToken.None, out string? why);
        _queue = 0;
        using DiskIdleGate.Turn? next = gate.TryBegin(Key, CancellationToken.None, out _);

        Assert.Null(busy);
        Assert.Equal(DiskIdleGate.QueueBusy, why);
        Assert.NotNull(next); // 막혀 끝난 판정이 순번을 쥐고 있지 않음
    }

    [Fact]
    public void AQueueThatBecomesBusyWhileWaitingEndsTheCheckAsBusy()
    {
        _script.AddRange([0, 0, 3]);
        DiskIdleGate gate = Gate();

        DiskIdleGate.Turn? turn = gate.TryBegin(Key, CancellationToken.None, out string? why);

        Assert.Null(turn);
        Assert.Equal(DiskIdleGate.QueueBusy, why);
    }

    [Fact]
    public void AMissingMeasurementIsAGapAndNeverIdle()
    {
        _queue = null;
        DiskIdleGate gate = Gate();

        Assert.Null(gate.TryBegin(Key, CancellationToken.None, out string? why));
        Assert.Equal(DiskIdleGate.MeasureGap, why);
    }

    [Fact]
    public void EveryTurnStartsTheZeroRunOverSoAZeroRightAfterTheOwnReadIsNotCountedAsContinuedIdleness()
    {
        DiskIdleGate gate = Gate();
        DiskIdleGate.Turn? first = gate.TryBegin(Key, CancellationToken.None, out _);
        first!.Dispose(); // 서비스 자신의 읽기가 끝남
        long start = _now;

        using DiskIdleGate.Turn? second = gate.TryBegin(Key, CancellationToken.None, out _);

        Assert.NotNull(second);
        Assert.Equal(DiskIdleGate.IdleHoldMs, _now - start); // 다시 처음부터 한가함을 잼
    }

    [Fact]
    public async Task AnotherJudgmentOnTheSameDiskWaitsForTheTurnAndThenMeasuresFromScratch()
    {
        DiskIdleGate gate = Gate();
        DiskIdleGate.Turn? held = gate.TryBegin(Key, CancellationToken.None, out _);
        Task<(bool Got, long Elapsed)> other = Task.Run(() =>
        {
            long before = Interlocked.Read(ref _now);
            using DiskIdleGate.Turn? turn = gate.TryBegin("d:", CancellationToken.None, out _); // 같은 디스크 (대소문자 무관)
            return (turn is not null, Interlocked.Read(ref _now) - before);
        });

        await Task.Delay(150);
        Assert.False(other.IsCompleted); // 쥐여 있는 동안은 새 판정을 시작하지 않음
        held!.Dispose();
        (bool got, long elapsed) = await other;

        Assert.True(got);
        Assert.True(elapsed >= DiskIdleGate.IdleHoldMs, $"elapsed {elapsed}");
    }

    [Fact]
    public void ADifferentDiskHasItsOwnTurn()
    {
        DiskIdleGate gate = Gate();
        using DiskIdleGate.Turn? c = gate.TryBegin("C:", CancellationToken.None, out _);

        using DiskIdleGate.Turn? d = gate.TryBegin("D:", CancellationToken.None, out _);

        Assert.NotNull(d);
    }

    [Fact]
    public void ACancelledCallerDoesNotGetATurn()
    {
        DiskIdleGate gate = Gate();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Null(gate.TryBegin(Key, cts.Token, out string? why));
        Assert.Equal(DiskIdleGate.Cancelled, why);
    }

    [Fact]
    public void TheTallyCountsABlockOncePerReasonWhenItStartsAndTheTimeItLasted()
    {
        var tally = new IdleTally();

        tally.NoteVerdict(DiskIdleGate.QueueBusy, 1000);
        tally.NoteVerdict(DiskIdleGate.QueueBusy, 1100); // 같은 막힘이 이어짐: 다시 세지 않음
        tally.NoteVerdict(null, 1500);
        tally.NoteVerdict(DiskIdleGate.MeasureGap, 2000);
        tally.Close(2200);
        tally.NoteIssued(1L << 20);
        tally.EndedBy = "list finished";

        Assert.Equal(1, tally.BlockedCount(DiskIdleGate.QueueBusy));
        Assert.Equal(1, tally.BlockedCount(DiskIdleGate.MeasureGap));
        Assert.Equal(700, tally.BlockedMs);
        Assert.Equal("1 batches, 1 MiB; held back 2 times (queue 1, measurement gap 1, loading 0) for 0.7 s, ended: list finished", tally.Describe());
    }
}
