using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임 드라이브의 한가함 판정: 가짜 시계와 가짜 대기열로 0 이 이어진 시간, 끊김, 계측 공백, 자기 읽기 범위를 시험합니다.
public sealed class DiskIdleGateTests
{
    private const string Key = @"D:\Games\a";
    private long _now = 1000;
    private long? _queue = 0;
    private readonly List<long?> _queueScript = [];
    private int _reads;

    private DiskIdleGate Gate() => new(_ =>
    {
        _reads++;
        return _queueScript.Count > 0 ? _queueScript[Math.Min(_reads - 1, _queueScript.Count - 1)] : _queue;
    }, () => _now, ms => _now += ms);

    [Fact]
    public void AQueueOfZeroIsIdleOnlyAfterItStayedZeroForTheHoldTimeAndTheCallerWaitsForThatTime()
    {
        DiskIdleGate gate = Gate();
        long start = _now;

        DiskIdleGate.Verdict v = gate.Check(Key);

        Assert.True(v.Idle);
        Assert.Equal(DiskIdleGate.IdleHoldMs, _now - start); // 0 이 이어진 시간을 채울 때까지 짧게 지켜봄
    }

    [Fact]
    public void ASecondCheckRightAfterTheFirstIdleOneNeedsNoWaitingBecauseTheZeroRunContinues()
    {
        DiskIdleGate gate = Gate();
        _ = gate.Check(Key);
        long after = _now;
        _now += 100; // 연속으로 본 것으로 치는 시간 (ContinuityMs) 안

        DiskIdleGate.Verdict v = gate.Check(Key);

        Assert.True(v.Idle);
        Assert.Equal(after + 100, _now);
    }

    [Fact]
    public void ANonZeroQueueIsBusyAndRestartsTheZeroRun()
    {
        DiskIdleGate gate = Gate();
        _ = gate.Check(Key);
        _queue = 2;
        _now += 50;

        DiskIdleGate.Verdict busy = gate.Check(Key);
        _queue = 0;
        long start = _now;
        DiskIdleGate.Verdict again = gate.Check(Key);

        Assert.Equal(DiskIdleGate.QueueBusy, busy.Why);
        Assert.True(again.Idle);
        Assert.Equal(DiskIdleGate.IdleHoldMs, _now - start); // 0 이 이어진 시간을 처음부터 다시 잼
    }

    [Fact]
    public void AQueueThatBecomesBusyWhileWaitingEndsTheCheckAsBusy()
    {
        _queueScript.AddRange([0, 0, 3]);
        DiskIdleGate gate = Gate();

        DiskIdleGate.Verdict v = gate.Check(Key);

        Assert.Equal(DiskIdleGate.QueueBusy, v.Why);
    }

    [Fact]
    public void AMissingMeasurementIsAGapAndNeverIdle()
    {
        _queue = null;
        DiskIdleGate gate = Gate();

        Assert.Equal(DiskIdleGate.MeasureGap, gate.Check(Key).Why);
        _queue = 0;
        _ = gate.Check(Key);
        _queue = null;
        _now += 10;
        Assert.Equal(DiskIdleGate.MeasureGap, gate.Check(Key).Why);
    }

    [Fact]
    public void AGapBetweenChecksLongerThanTheContinuityTimeRestartsTheZeroRun()
    {
        DiskIdleGate gate = Gate();
        _ = gate.Check(Key);
        _now += DiskIdleGate.ContinuityMs + 1; // 그 사이 게임이 읽었는지 알 수 없음
        long start = _now;

        DiskIdleGate.Verdict v = gate.Check(Key);

        Assert.True(v.Idle);
        Assert.Equal(DiskIdleGate.IdleHoldMs, _now - start);
    }

    [Fact]
    public void TheServicesOwnReadDoesNotBreakTheZeroRunEvenWhenItLastsLongerThanTheContinuityTime()
    {
        DiskIdleGate gate = Gate();
        _ = gate.Check(Key);
        using (gate.Own(Key))
        {
            _now += 400; // 서비스가 읽는 동안
        }

        long after = _now;
        DiskIdleGate.Verdict v = gate.Check(Key);

        Assert.True(v.Idle);
        Assert.Equal(after, _now); // 기다리지 않고 바로 이어서 판정
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
        tally.NoteIssued(16L << 20);
        tally.EndedBy = "list finished";

        Assert.Equal(1, tally.BlockedCount(DiskIdleGate.QueueBusy));
        Assert.Equal(1, tally.BlockedCount(DiskIdleGate.MeasureGap));
        Assert.Equal(700, tally.BlockedMs);
        Assert.Equal("1 batches, 16 MiB; held back 2 times (queue 1, measurement gap 1, loading 0) for 0.7 s, ended: list finished", tally.Describe());
    }
}
