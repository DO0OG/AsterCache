using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 붙들 목록을 게임 드라이브가 한가한 틈에 채우는 이어 붙들기 (DiskIdleGate): 가짜 대기열과 가짜 시계로 묶음 크기·막힘·상한·게이트를 시험합니다 (가짜 환경은 KeepHoldTests 와 같음).
public sealed partial class KeepHoldTests
{
    private long _gateNow = 10_000; // 한가함 판정의 가짜 시계 (기다릴 때만 흐름)
    private long? _gateQueue = 0;
    private int _gateReads;

    private DiskIdleGate FakeGate() => new(_ =>
    {
        _gateReads++;
        return _gateQueue;
    }, () => _gateNow, ms => _gateNow += ms);

    // 첫 붙들기가 첫 경계 (파일 1, 디스크 40 MiB) 에서 상한을 넘어 끝나 남은 열 파일 (16 MiB 씩) 이 이어 붙들 후보가 되는 목록
    private static readonly ReadOp[] FillList = [Op(1, 0, 8), .. Enumerable.Range(0, 10).Select(i => Op(10 + i, 0, 16))];

    // 채우기를 시험하는 환경: 첫 확인 (초 1) 부터 파일 12 만 메모리에 없음 (채우는 묶음은 끝까지 읽고 그 양을 디스크 읽기로 셈), each 는 그 뒤 초마다 부름
    private Prewarmer MakeFill(Action<double>? each = null, LoadPhaseBoard? board = null, double seconds = 20)
    {
        _hold.Missing.Add(F(1));
        _hold.DiskPerMiss = 40 * Mib;
        Prewarmer p = Make(FillList);
        p.IdleGate = FakeGate();
        p.LoadPhases = board;
        RunFor(seconds, e =>
        {
            if (e == 1)
            {
                _hold.Missing.Clear();
                _hold.DiskPerMiss = 0;
                _ = _hold.Missing.Add(F(12));
            }

            each?.Invoke(e);
        });
        return p;
    }

    private int FirstPassBatches() => _hold.Fills.Count(f => !f);

    private int FilledBatches() => _hold.Fills.Count(f => f);

    [Fact]
    public void WhileTheDriveIsIdleTheContinuationFillsInSixteenMiBBatchesAndReadsTheColdFileToTheEndOnce()
    {
        Prewarmer p = MakeFill();

        p.TouchAtGameStart(Game("a"), "a.exe");

        List<(string Path, long OffsetMib)> flat = Flat(_hold.Batches.Zip(_hold.Fills).Where(x => x.Second).Select(x => x.First));
        Assert.Equal(flat.Count, flat.Distinct().Count()); // 채우는 동안 같은 범위를 두 번 요청하지 않음
        Assert.Equal(10, FilledBatches()); // 남은 열 파일 (16 MiB 씩) 을 묶음 하나씩
        Assert.All(_hold.Batches.Zip(_hold.Fills).Where(x => x.Second).Select(x => x.First), b => Assert.True(b.Sum(o => o.Length) <= 16 * Mib));
        Assert.Contains(_log.Lines, l => l.Contains("cache hold continuing after the first pass") && l.Contains("16 MiB read from disk (disk reads of the play 16 MiB of 5120 MiB)")); // 파일 12 는 디스크에서 끝까지 읽음
        string ended = Assert.Single(_log.Lines, l => l.Contains("cache hold ended"));
        Assert.Contains("filling while the drive was idle: 10 batches, 160 MiB; held back 0 times (queue 0, measurement gap 0, loading 0)", ended);
        Assert.Contains("ended: list finished", ended);
    }

    [Fact]
    public void OneCheckIssuesSeveralBatchesWhileTheDriveStaysIdle()
    {
        var perSecond = new List<int>();
        Prewarmer p = MakeFill(e => perSecond.Add(_hold.Batches.Count));

        p.TouchAtGameStart(Game("a"), "a.exe");

        int[] perCheck = [.. perSecond.Zip(perSecond.Skip(1), (a, b) => b - a).Where(d => d > 0)];
        Assert.True(perCheck[0] > 1, "one check issues more than one batch"); // 예전에는 디스크를 읽은 묶음 뒤에 그 확인을 끝냈음
    }

    [Fact]
    public void ABusyQueueIssuesNothingNewAndTheFillResumesWhenTheQueueIsZeroAgain()
    {
        int atEndOfBusy = -1;
        Prewarmer p = MakeFill(e =>
        {
            if (e == 1)
            {
                _gateQueue = 2; // 게임이 읽는 중
            }

            if (e == 7)
            {
                atEndOfBusy = _hold.Batches.Count;
            }

            if (e == 8)
            {
                _gateQueue = 0;
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(FirstPassBatches(), atEndOfBusy); // 막힌 동안 새 요청 0 (첫 패스의 묶음만)
        _ = Assert.Single(_log.Lines, l => l.Contains("cache hold continuing after the first pass") && l.Contains("deferred: game drive queue is not 0")); // 같은 사유는 한 줄
        Assert.Equal(10, FilledBatches()); // 0 으로 돌아오면 이어서 채움
        Assert.Contains("held back 1 times (queue 1, measurement gap 0, loading 0)", Assert.Single(_log.Lines, l => l.Contains("cache hold ended")));
    }

    [Fact]
    public void AMeasurementGapIsNeverIdleSoNothingIsFilledAndTheReasonIsLogged()
    {
        Prewarmer p = MakeFill(e =>
        {
            if (e == 1)
            {
                _gateQueue = null; // 대기열을 읽지 못함
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(0, FilledBatches());
        Assert.Contains(_log.Lines, l => l.Contains("cache hold continuing after the first pass") && l.Contains("deferred: disk activity could not be measured"));
        Assert.Contains("measurement gap 1", Assert.Single(_log.Lines, l => l.Contains("cache hold ended")));
    }

    [Fact]
    public void WhileFramesAreShownTheBatchIsFourMiB()
    {
        var board = new LoadPhaseBoard(_log);
        Prewarmer p = MakeFill(e => _ = board.Add("a", new LoadingSample(e, 0, null, false)), board); // 읽기가 조용함: 로딩이 아님 (프레임이 나오는 중)

        p.TouchAtGameStart(Game("a"), "a.exe");

        long[] sizes = [.. _hold.Batches.Zip(_hold.Fills).Where(x => x.Second).Select(x => x.First.Sum(o => o.Length))];
        Assert.NotEmpty(sizes);
        Assert.All(sizes, bytes => Assert.True(bytes <= 4 * Mib));
        Assert.Equal(40, sizes.Length); // 160 MiB 를 4 MiB 씩
    }

    [Fact]
    public void ALoadingThatStartedWithAnExecutableStartDoesNotHoldBackTheFillAndUsesSixteenMiBBatches()
    {
        var board = new LoadPhaseBoard(_log);
        _ = board.Add("a", new LoadingSample(0, 0, null, true)); // 실행 파일 시작으로 잡힌 로딩
        Prewarmer p = MakeFill(null, board);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(10, FilledBatches());
        Assert.DoesNotContain(_log.Lines, l => l.Contains("deferred: loading phase"));
        Assert.Contains(_hold.Batches.Zip(_hold.Fills), x => x.Second && x.First.Sum(o => o.Length) == 16 * Mib); // 로딩 중에는 16 MiB 묶음
    }

    [Fact]
    public void ALoadingFoundByTheReadSignalHoldsBackTheFillEvenWhileTheDriveIsIdle()
    {
        var board = new LoadPhaseBoard(_log);
        int atEndOfBlock = -1;
        Prewarmer p = MakeFill(e =>
        {
            _ = board.Add("a", new LoadingSample(e, e < 6 ? 100 * Mib : 0, null, false)); // 읽기 신호로 잡힌 로딩 (읽기가 조용해진 뒤 15 초 더 이어져 그만큼 늦게 풀림)
            if (e == 5)
            {
                atEndOfBlock = _hold.Batches.Count;
            }
        }, board, 45);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(FirstPassBatches(), atEndOfBlock); // 신호 로딩 동안 새 요청 0
        _ = Assert.Single(_log.Lines, l => l.Contains("cache hold continuing after the first pass") && l.Contains("deferred: loading phase"));
        Assert.Contains("loading 1)", Assert.Single(_log.Lines, l => l.Contains("cache hold ended")));
        Assert.True(FilledBatches() >= 10, "the fill goes on after the loading ended"); // 로딩이 끝나면 프레임이 나오는 중이라 4 MiB 묶음으로 채움
    }

    [Fact]
    public void TheFillStopsAtTheRemainingPlayBudgetWithOneLineAndTheLimitsAreAsDocumented()
    {
        Prewarmer? p = null;
        p = MakeFill(e =>
        {
            if (e == 1)
            {
                SpendHoldFillDisk(p!, Prewarmer.HoldFillDiskCapBytes - (2 * Mib)); // 상한까지 2 MiB: 한 번 요청분 (4 MiB) 보다 적음
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(0, FilledBatches()); // 더 채우지 않음
        _ = Assert.Single(_log.Lines, l => l.Contains("cache hold continuing after the first pass") && l.Contains("stopped: the disk read cap of the play is used"));
        Assert.Equal(5L << 30, Prewarmer.HoldFillDiskCapBytes);
        Assert.Equal(1L << 30, Prewarmer.HoldAgainDiskCapBytes); // 내놓았다가 다시 붙드는 읽기는 따로 1 GiB
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
    public void TheMemoryFloorStillReleasesWhenTheMemoryDropsInTheMiddleOfAFill()
    {
        Prewarmer p = MakeFill();
        _hold.OnHold = () =>
        {
            if (_hold.Batches.Count == 6)
            {
                _avail = 1500; // 가용 메모리가 하한 (2048) 아래로 내려감
            }
        };

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.NotEmpty(_hold.Released); // 내놓기는 채우기와 무관하게 바로 일어남
        Assert.True(FilledBatches() < 10, "the fill stops when the memory is short instead of going on to the end");
        Assert.Contains(_log.Lines, l => l.Contains("cache hold released") && l.Contains("for memory"));
    }

    [Fact]
    public void WhenTheDiskReadsOfAFillBatchCannotBeMeasuredTheFillStopsForThePlay()
    {
        Prewarmer p = MakeFill(e =>
        {
            if (e == 1)
            {
                _hold.Measured = false; // 하드 폴트 수를 읽을 수 없는 환경
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(1, FilledBatches()); // 계측하지 못한 묶음 하나로 끝: 이 플레이에서는 더 읽지 않음
        _ = Assert.Single(_log.Lines, l => l.Contains("cache hold stops reading from the disk for this play"));
    }
}
