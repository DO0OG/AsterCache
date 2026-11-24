using System.Diagnostics;
using System.Reflection;
using AsterCache.Core.Model;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 붙들 목록을 게임 드라이브가 한가한 틈에 채우는 이어 붙들기 (DiskIdleGate): 채우기는 감시 루프와 다른 스레드에서 돌므로 가짜 게임은 시험이 정한 조건이 되면 끝납니다 (가짜 환경은 KeepHoldTests 와 같음).
public sealed partial class KeepHoldTests
{
    private long _gateNow = 10_000; // 한가함 판정의 가짜 시계 (판정이 기다릴 때만 흐름, 채우는 스레드만 씀)
    private long? _gateQueue = 0;
    private int _gateReads;
    private int _gateSleeps;
    private readonly List<long> _batchStartedAt = []; // 묶음 읽기를 시작한 가짜 시각

    private DiskIdleGate FakeGate() => new(_ =>
    {
        Interlocked.Increment(ref _gateReads);
        return _gateQueue;
    }, () => Interlocked.Read(ref _gateNow), ms =>
    {
        _ = Interlocked.Add(ref _gateNow, ms);
        _ = Interlocked.Increment(ref _gateSleeps);
    });

    // 첫 붙들기가 첫 경계 (파일 1, 디스크 40 MiB) 에서 상한을 넘어 끝나 남은 열 파일 (16 MiB 씩) 이 이어 붙들 후보가 되는 목록 (파일 10~19)
    private static readonly ReadOp[] FillList = [Op(1, 0, 8), .. Enumerable.Range(0, 10).Select(i => Op(10 + i, 0, 16))];

    // 채우기가 끝났거나 멈춘 줄이 남았는가
    private bool FillEnded() => _log.Lines.Any(l => l.Contains("cache hold continuing after the first pass") && (l.Contains("): finished") || l.Contains("): stopped")));

    // 파일 n 이 LCN lcnMib 부터 이어진 16 MiB 로 놓여 있다고 알려 줌
    private void Place(int file, long lcnMib) => _layouts[F(file)] = new FileLayoutInfo([new Extent(0, lcnMib * Mib / 4096, 16 * Mib / 4096)], 4096);

    // 채우기를 시험하는 환경: 첫 확인 (초 1) 부터 파일 12 만 메모리에 없음 (채우는 묶음은 끝까지 읽고 그 양을 디스크 읽기로 셈). 가짜 게임은 done 이 참이 되거나 시간이 다하면 끝남.
    private Prewarmer MakeFill(Func<bool>? done = null, Action<double>? each = null, LoadPhaseBoard? board = null, int timeoutMs = 15000)
    {
        _hold.Missing.Add(F(1));
        _hold.DiskPerMiss = 40 * Mib;
        Prewarmer p = Make(FillList);
        p.IdleGate = FakeGate();
        p.LoadPhases = board;
        _hold.OnHold = () => _batchStartedAt.Add(Interlocked.Read(ref _gateNow));
        var clock = Stopwatch.StartNew();
        RunFor(1e9, e =>
        {
            if (e == 1)
            {
                _hold.Missing.Clear();
                _hold.DiskPerMiss = 0;
                _ = _hold.Missing.Add(F(12));
            }

            each?.Invoke(e);
            Thread.Sleep(1); // 채우는 스레드가 도는 동안 감시 루프가 가짜 시간을 한꺼번에 흘리지 않게
            if (e >= 2 && ((done ?? FillEnded)() || clock.ElapsedMilliseconds > timeoutMs))
            {
                _ = _running.Remove("a");
            }
        });
        return p;
    }

    private int FirstPassBatches() => _hold.Fills.Count(f => !f);

    private List<ReadOp[]> FillBatches() => [.. _hold.Batches.Zip(_hold.Fills).Where(x => x.Second).Select(x => x.First.ToArray())];

    [Fact]
    public void WhileTheDriveIsIdleTheContinuationFillsInBatchesOfAtMostOneMiBWhenThePositionsAreUnknownAndReadsTheColdFileToTheEndOnce()
    {
        Prewarmer p = MakeFill();

        p.TouchAtGameStart(Game("a"), "a.exe");

        List<ReadOp[]> fills = FillBatches();
        Assert.Equal(160, fills.Count); // 160 MiB 를 위치를 모르는 범위의 상한 1 MiB 씩
        Assert.All(fills, b => Assert.True(b.Sum(o => o.Length) <= Mib));
        List<(string Path, long OffsetMib)> flat = [.. fills.SelectMany(b => b).Select(o => (o.Path, o.Offset / Mib))];
        Assert.Equal(flat.Count, flat.Distinct().Count()); // 같은 범위를 한 플레이에 두 번 요청하지 않음
        Assert.Contains(_log.Lines, l => l.Contains("cache hold continuing after the first pass") && l.Contains("16 MiB read from disk (disk reads of the play 16 MiB of 5120 MiB)")); // 파일 12 는 디스크에서 끝까지 읽음
        string ended = Assert.Single(_log.Lines, l => l.Contains("cache hold ended"));
        Assert.Contains("filling while the drive was idle: 160 batches, 160 MiB; held back 0 times (queue 0, measurement gap 0, loading 0)", ended);
        Assert.Contains("ended: list finished", ended);
    }

    [Fact]
    public void TheFillGoesInPhysicalOrderAndEachBatchIsOneContiguousRunNotPassingEightMiBDuringAnExecutableStartLoading()
    {
        var board = new LoadPhaseBoard(_log);
        _ = board.Add("a", new LoadingSample(0, 0, null, true)); // 실행 파일 시작으로 잡힌 로딩
        for (int n = 10; n < 20; n++)
        {
            Place(n, 1000 - ((n - 10) * 32)); // 파일 19 가 가장 앞 (LCN 이 가장 낮음), 파일 사이는 16 MiB 떨어져 이어지지 않음
        }

        Prewarmer p = MakeFill(null, null, board);

        p.TouchAtGameStart(Game("a"), "a.exe");

        List<ReadOp[]> fills = FillBatches();
        Assert.Equal(F(19), fills[0][0].Path); // 중요도 순서가 아니라 물리 위치 순으로 채움
        Assert.All(fills, b => Assert.True(b.Sum(o => o.Length) <= 8 * Mib));
        Assert.All(fills, b => Assert.Single(b.Select(o => o.Path).Distinct())); // 서로 떨어진 파일이 한 묶음에 섞이지 않음
        Assert.Equal(20, fills.Count); // 16 MiB 파일 열 개를 8 MiB 씩
        Assert.DoesNotContain(_log.Lines, l => l.Contains("deferred: loading phase"));
    }

    [Fact]
    public void ABatchIsOnlyIssuedAfterANewIdleHoldTimeFollowingTheEndOfTheBatchBefore()
    {
        Prewarmer p = MakeFill();

        p.TouchAtGameStart(Game("a"), "a.exe");

        long[] starts = [.. _batchStartedAt.Skip(FirstPassBatches())];
        Assert.True(starts.Length > 100);
        Assert.All(starts.Zip(starts.Skip(1), (a, b) => b - a), gap => Assert.True(gap >= DiskIdleGate.IdleHoldMs, $"gap {gap}")); // 묶음마다 처음부터 0.3 초를 다시 잼
    }

    [Fact]
    public void ABusyQueueIssuesNothingNewAndTheFillResumesWhenTheQueueIsZeroAgain()
    {
        int atEndOfBusy = -1;
        Prewarmer p = MakeFill(null, e =>
        {
            if (e == 1)
            {
                _gateQueue = 2; // 게임이 읽는 중
            }

            if (e == 40)
            {
                atEndOfBusy = _hold.Batches.Count;
                _gateQueue = 0;
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(FirstPassBatches(), atEndOfBusy); // 막힌 동안 새 요청 0 (첫 패스의 묶음만)
        _ = Assert.Single(_log.Lines, l => l.Contains("cache hold continuing after the first pass") && l.Contains("deferred: game drive queue is not 0")); // 같은 사유는 한 줄
        Assert.Equal(160, FillBatches().Count); // 0 으로 돌아오면 이어서 채움
        Assert.Contains("held back 1 times (queue 1, measurement gap 0, loading 0)", Assert.Single(_log.Lines, l => l.Contains("cache hold ended")));
    }

    [Fact]
    public void AMeasurementGapIsNeverIdleSoNothingIsFilledAndTheReasonIsLogged()
    {
        Prewarmer p = MakeFill(() => _log.Lines.Any(l => l.Contains("deferred: disk activity could not be measured")), e =>
        {
            if (e == 1)
            {
                _gateQueue = null; // 대기열을 읽지 못함
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Empty(FillBatches());
        Assert.Contains("measurement gap 1", Assert.Single(_log.Lines, l => l.Contains("cache hold ended")));
    }

    [Fact]
    public void ALoadingFoundByTheReadSignalHoldsBackTheFillEvenWhileTheDriveIsIdle()
    {
        var board = new LoadPhaseBoard(_log);
        int atEndOfBlock = -1;
        Prewarmer p = MakeFill(null, e =>
        {
            _ = board.Add("a", new LoadingSample(e, e < 30 ? 100 * Mib : 0, null, false)); // 읽기 신호로 잡힌 로딩 (읽기가 조용해진 뒤 15 초 더 이어짐)
            if (e == 29)
            {
                atEndOfBlock = _hold.Batches.Count;
            }
        }, board);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(FirstPassBatches(), atEndOfBlock); // 신호 로딩 동안 새 요청 0
        _ = Assert.Single(_log.Lines, l => l.Contains("cache hold continuing after the first pass") && l.Contains("deferred: loading phase"));
        Assert.Contains("loading 1)", Assert.Single(_log.Lines, l => l.Contains("cache hold ended")));
        Assert.Equal(160, FillBatches().Count); // 로딩이 끝나면 채움
    }

    [Theory]
    [InlineData(10, 8)]
    [InlineData(6, 6)]
    [InlineData(4, 4)]
    [InlineData(3, 0)]
    public void AFillNeverRequestsMoreThanTheRemainingPlayBudgetEvenWhenItIsLessThanOneBatch(long leftMib, long expectedMib)
    {
        var board = new LoadPhaseBoard(_log);
        _ = board.Add("a", new LoadingSample(0, 0, null, true));
        for (int n = 10; n < 20; n++)
        {
            Place(n, 1000 + ((n - 10) * 32));
        }

        Prewarmer? p = null;
        p = MakeFill(null, e =>
        {
            if (e == 1)
            {
                for (int n = 10; n < 20; n++)
                {
                    _ = _hold.Missing.Add(F(n)); // 모두 메모리에 없어 읽은 만큼 예산이 줄어듦
                }

                SpendHoldFillDisk(p!, Prewarmer.HoldFillDiskCapBytes - (leftMib * Mib)); // 상한까지 leftMib 남음
            }
        }, board);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(expectedMib * Mib, FillBatches().Sum(b => b.Sum(o => o.Length))); // 남은 예산이 한 묶음보다 적으면 그만큼만 (10 MiB 남음: 8 MiB 뒤 2 MiB 가 남아 멈춤), 4 MiB 아래면 새로 읽지 않음
        Assert.Equal(5L << 30, Prewarmer.HoldFillDiskCapBytes);
        Assert.Equal(1L << 30, Prewarmer.HoldAgainDiskCapBytes); // 내놓았다가 다시 붙드는 읽기는 따로 1 GiB
    }

    [Fact]
    public void WhatTheFillReadFromTheDiskIsNotReadAgainWhenTheListIsRebuiltAfterEverythingWasReleased()
    {
        Prewarmer p = Make(Reads);
        object memory = typeof(Prewarmer).GetMethod("KeepMemoryOf", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(p, ["a"])!;
        Type t = memory.GetType();

        _ = t.GetMethod("NoteDiskFilled")!.Invoke(memory, [new[] { Op(2, 0, 4), Op(3, 2, 2) }]);
        var left = (List<ReadOp>)t.GetMethod("WithoutDiskFilled")!.Invoke(memory, [new[] { Op(1, 0, 8), Op(2, 0, 8), Op(3, 0, 8) }])!;

        Assert.Equal([Op(1, 0, 8), Op(2, 4, 4), Op(3, 0, 2), Op(3, 4, 4)], left);
    }

    [Fact]
    public void AGameThatCannotBeHeldNeverConsultsTheGateAndKeepsItsOldWay()
    {
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(FillList, cheat);
        p.IdleGate = FakeGate();
        RunFor(3);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Equal(0, _opened);
        Assert.Empty(_hold.Batches);
        Assert.Equal(0, _gateReads);
        string[] paths = [.. _touched.SelectMany(b => b).Select(o => o.Path).Distinct()];
        Assert.Equal(FillList.Select(o => o.Path).Distinct().Order(), paths.Order()); // 시작 만지기가 전체 목록을 만짐
    }

    [Fact]
    public void WithoutAGateTheContinuationKeepsTheFourMiBRequestsAndNeverAsksTheHoldToFill()
    {
        _hold.Missing.Add(F(1));
        _hold.DiskPerMiss = 40 * Mib;
        Prewarmer p = Make(FillList);
        RunFor(20, e =>
        {
            if (e == 1)
            {
                _hold.DiskPerMiss = 0;
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.DoesNotContain(true, _hold.Fills);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("filling while the drive"));
    }

    [Fact]
    public void TheMemoryFloorReleasesWithinASecondWhileAFillBatchIsStillReading()
    {
        bool released = false;
        _hold.OnRelease = () => released = true;
        bool sawReleaseDuringRead = false;
        bool armed = false;
        Prewarmer p = MakeFill(() => armed && _log.Lines.Any(l => l.Contains("cache hold released")), null);
        _hold.OnHold = () =>
        {
            if (_hold.Fills.Count(f => f) == 5 && !armed) // 다섯째 채우기 묶음이 느리게 읽히는 동안
            {
                armed = true;
                _avail = 1500; // 가용 메모리가 하한 (2048) 아래로 내려감
                sawReleaseDuringRead = SpinWait.SpinUntil(() => released, 1000); // 감시 루프가 1 초 안에 내놓는가 (읽기는 아직 끝나지 않음)
            }
        };

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.True(sawReleaseDuringRead, "the floor release did not wait for the fill read");
        Assert.NotEmpty(_hold.Released);
    }

    [Fact]
    public void TheFillStopsForThePlayWhenTheDiskReadsOfABatchCannotBeMeasured()
    {
        Prewarmer p = MakeFill(() => _log.Lines.Any(l => l.Contains("cache hold stops reading from the disk for this play")), e =>
        {
            if (e == 1)
            {
                _hold.Measured = false; // 하드 폴트 수를 읽을 수 없는 환경
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        _ = Assert.Single(FillBatches()); // 계측하지 못한 묶음 하나로 끝: 이 플레이에서는 더 읽지 않음
        _ = Assert.Single(_log.Lines, l => l.Contains("cache hold stops reading from the disk for this play"));
    }
}
