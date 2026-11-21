using System.Reflection;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임 실행 중 스파스 파일 블록 유지 (memory.keep_sparse): 가짜 환경과 가짜 읽기 도구 (IBlockReader, 메모리에 올라온 블록은 빠르고 아닌 블록은 느림) 로
/// 읽는 순서·주기·디스크로 간 읽기 판정·회차 상한 (그 회차만 끝나고 다음 회차가 이어 읽음)·플레이 상한·메모리 하한·열기 실패·스위치·서비스 자신의 읽기 기록·직렬화 잠금·곁 스레드 연결을 확인합니다.
/// 읽기 수단이 IBlockReader 뿐임 (매핑·붙들기·찾기·표본 대리자는 부르면 시험이 실패) 은 직접 도는 시험에서 고정합니다.
public sealed class SparseKeepTests
{
    private const long Block = 65536;
    private static readonly string FileA = @"D:\s\a.xsub";
    private static readonly string FileB = @"D:\s\b.xsub";
    private static readonly double Slow = 0.01;
    private static readonly double Fast = 0.0001;

    private readonly ListLog _log = new();
    private readonly List<(string Path, long Offset, int Length)> _reads = [];
    private readonly List<TimeSpan> _sleeps = []; // 1 초 이상의 쉼 (회차 주기)
    private readonly List<TimeSpan> _pauses = []; // 묶음 사이의 짧은 쉼
    private readonly List<bool> _ioLow = [];
    private readonly List<ThreadPriority> _priorities = [];
    private readonly HashSet<(string, long)> _inMemory = []; // 메모리에 올라온 블록 (읽으면 올라옴)
    private readonly ManualResetEventSlim _firstRead = new();
    private volatile bool _gameUp = true;
    private volatile bool _enabled = true;
    private volatile bool _forbidMapping; // 켜면 매핑·붙들기·찾기·표본 대리자가 불리는 즉시 오류 (유지가 잡아 로그에 "stopped" 를 남기므로 시험이 실패)
    private long _avail = 20000;
    private int _opened;
    private int _readsUnderLock;
    private int _pausesUnderLock;
    private bool _allWarm; // 켜면 모든 블록이 메모리에 있음 (읽기가 모두 빠름)
    private Prewarmer? _prewarmer;
    private List<TraceEvent>? _rows;
    private Func<int, string, double?>? _seconds; // 지정하면 (읽기 번호, 경로) → 걸린 시간 (초, null 이면 열 수 없음), 없으면 메모리 모델
    private Action<TimeSpan, int>? _onSleep; // 회차 주기로 쉴 때마다 (쉰 시간, 지금까지 쉰 횟수) 로 부름

    private static GameSettings Game() => Games.Make("a", "a.exe");

    private static List<TraceEvent> Rows(string path, int blocks, int firstBlock = 0) =>
        [.. Enumerable.Range(firstBlock, blocks).Select(i => new TraceEvent(path, i * Block, Block))];

    private static object TouchSerial(Prewarmer p) => typeof(Prewarmer).GetField("_touchSerial", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(p)!;

    private sealed class FakeReader(SparseKeepTests t) : IBlockReader
    {
        public double? Read(string path, long offset, int length)
        {
            int n;
            lock (t._reads)
            {
                n = t._reads.Count;
                t._reads.Add((path, offset, length));
                if (t._prewarmer is { } p && Monitor.IsEntered(TouchSerial(p)))
                {
                    t._readsUnderLock++;
                }

                t._priorities.Add(Thread.CurrentThread.Priority);
            }

            t._firstRead.Set();
            if (t._seconds is { } custom)
            {
                return custom(n, path);
            }

            lock (t._inMemory)
            {
                bool warm = t._allWarm || !t._inMemory.Add((path, offset));
                return warm ? Fast : Slow;
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class Streamer : IGameStreamer
    {
        public StreamStats Stream(GameSettings game) => new(30L << 20, 1, 0, 0.01);

        public IReadOnlyList<ReadOp>? PlannedPrewarmReads(GameSettings game) => [.. Enumerable.Range(1, 6).Select(i => new ReadOp(@"D:\a\" + i + ".bin", 0, 8L << 20))];
    }

    private IOException Forbidden(string what) => new($"{what} must not be used by the sparse keep");

    private Prewarmer Make()
    {
        GameSettings a = Game();
        var p = new Prewarmer([a], new Streamer(), new PrewarmEnv
        {
            AvailableRamMib = () => Interlocked.Read(ref _avail),
            GameRunning = _ => _gameUp,
            ZoneBytes = g => Math.Min(30, g.StreamCapBytes(30L << 20) >> 20) << 20,
            TraceExists = _ => true,
            PhysicalRamBytes = () => 64L << 30,
            ProbeReadMs = _ => new ProbeResult(8, 0, 0.2),
            DiskBytesPerSecond = _ => 0,
            CommonFirstOps = (_, _, _) => ([], 0),
            ColdRead = (ops, _) => (new StreamStats(ops.Sum(o => o.Length), 1, 0, 0.01), true),
            StandbyLadderEnabled = () => true,
            ListStamp = _ => "s1",
            TouchRanges = (ranges, ct) => _forbidMapping ? throw Forbidden("touch") : new TouchResult(ranges.Sum(o => o.Length), 0.1, 0, 0, TouchEnd.Completed),
            FindResident = (ops, cap, ct, onGroup) => throw Forbidden("find"),
            KeepProbeSamples = (_, _) => throw Forbidden("probe"),
            OpenHold = () => throw Forbidden("hold"),
            KeepSparseEnabled = () => _enabled,
            SparseRankedRows = _ => _rows,
            SetReadIoLow = low =>
            {
                lock (_ioLow)
                {
                    _ioLow.Add(low);
                }

                return true;
            },
            OpenBlockReader = () =>
            {
                _ = Interlocked.Increment(ref _opened);
                return new FakeReader(this);
            },
            Sleep = (t, ct) =>
            {
                if (t < TimeSpan.FromSeconds(1))
                {
                    lock (_pauses)
                    {
                        _pauses.Add(t);
                        if (_prewarmer is { } owner && Monitor.IsEntered(TouchSerial(owner)))
                        {
                            _pausesUnderLock++;
                        }
                    }

                    return;
                }

                int count;
                lock (_sleeps)
                {
                    _sleeps.Add(t);
                    count = _sleeps.Count;
                }

                _onSleep?.Invoke(t, count);
            },
        }, _log, null, () => new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));
        _gameUp = false; // 게임이 실행 중이면 시작 예열을 하지 않음
        Assert.True(p.Run(a, "service start").Ran); // 메모리에 둔 목록이 있는 게임 (유지 회차의 게이트)
        _gameUp = true;
        lock (_sleeps)
        {
            _sleeps.Clear(); // 시작 예열이 쉰 것은 세지 않음
        }

        lock (_pauses)
        {
            _pauses.Clear();
        }

        lock (_ioLow)
        {
            _ioLow.Clear();
        }

        _prewarmer = p;
        return p;
    }

    // 직접 도는 시험: 회차 주기의 쉼이 sleepsUntilEnd 번째에서 게임이 끝남 (첫 회차는 쉬기 전에 돎).
    private Prewarmer Direct(int sleepsUntilEnd)
    {
        Prewarmer p = Make();
        _forbidMapping = true;
        _onSleep = (_, n) =>
        {
            if (n >= sleepsUntilEnd)
            {
                _gameUp = false;
            }
        };
        return p;
    }

    private void Run(Prewarmer p) => p.KeepSparseWhilePlaying(Game(), "a.exe", CancellationToken.None);

    private static void NoStoppedLine(ListLog log) => Assert.DoesNotContain(log.Lines, l => l.Contains("stopped", StringComparison.Ordinal));

    [Fact]
    public void ARoundReadsTheRankedBlocksOneByOneInRankOrderWithBufferedReadsOnlyAndLogsTheStartAndTheEnd()
    {
        _rows = [.. Rows(FileA, 16), .. Rows(FileB, 16, firstBlock: 5)];
        _allWarm = true;
        Prewarmer p = Direct(1);

        Run(p);

        Assert.Equal([.. _rows.Select(r => (r.Path, r.Offset, (int)r.Length))], _reads); // 순위 순서 그대로, 블록 하나씩 (수단은 IBlockReader 뿐: 다른 대리자는 불리면 실패)
        Assert.Equal(1, _opened);
        Assert.Contains(_log.Lines, l => l.Contains($"a: sparse blocks kept while playing: 2 MiB in 2 files (target {LoadRefillPlanner.SparseRankedCapBytes >> 20} MiB)", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("sparse blocks kept while playing ended after 1 rounds (a.exe): reached 2 of 2 MiB of the list, 0 MiB read from disk", StringComparison.Ordinal));
        NoStoppedLine(_log);
    }

    [Fact]
    public void TheFirstRoundRunsAtOnceThenEveryTwentySecondsForTheFirstThreeMinutesAndEveryThirtyAfter()
    {
        _rows = Rows(FileA, 8);
        _allWarm = true;
        Prewarmer p = Direct(12);

        Run(p);

        Assert.Equal(12, _sleeps.Count);
        Assert.All(_sleeps.Take(9), t => Assert.Equal(TimeSpan.FromSeconds(20), t)); // 쉰 시간 합 180 초가 될 때까지
        Assert.All(_sleeps.Skip(9), t => Assert.Equal(TimeSpan.FromSeconds(30), t));
        Assert.Equal(12 * 8, _reads.Count); // 쉬기 전의 첫 회차 + 쉰 뒤 회차 11 번 (열둘째 쉼에서 게임이 끝나 마지막 회차는 없음)
        Assert.Equal(12, _opened);
    }

    [Fact]
    public void ABlockCountsAsReadFromDiskOnlyAboveTwoMillisecondsPlusOneMillisecondPerMiB()
    {
        Assert.Equal(0.002 + (0.0625 * 0.001), Prewarmer.SparseDiskReadSeconds(Block), 9);
        Assert.Equal(0.003, Prewarmer.SparseDiskReadSeconds(1 << 20), 9);
        _rows = Rows(FileA, 40);
        double threshold = Prewarmer.SparseDiskReadSeconds(Block);
        _seconds = (n, _) => n < 20 ? threshold : threshold + 0.0001; // 기준 그대로는 메모리, 조금이라도 넘으면 디스크
        Prewarmer p = Direct(1);

        Run(p);

        Assert.Contains(_log.Lines, l => l.Contains("reached 2 of 2 MiB of the list, 1 MiB read from disk", StringComparison.Ordinal)); // 디스크로 센 블록 20 개 (1.25 MiB), 목록은 끝까지 읽음
    }

    [Fact]
    public void SlowBlocksInARowDoNotDropTheRestOfTheListThePlayKeepsReadingIt()
    {
        _rows = Rows(FileA, 160); // 10 MiB
        _seconds = (n, _) => n >= 20 && n < 30 ? Slow : Fast; // 이어진 느린 블록 열 개 (경계로 보아 뒤를 플레이 내내 빼지 않음)
        Prewarmer p = Direct(2);

        Run(p);

        Assert.Equal(2 * 160, _reads.Count); // 매 회차 목록 전체를 읽음
        Assert.DoesNotContain(_log.Lines, l => l.Contains("boundary", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("ended after 2 rounds (a.exe): reached 10 of 10 MiB of the list", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenARoundReachesItsDiskReadCapOnlyThatRoundEndsAndTheNextOneGoesFurtherFromTheStartOfTheList()
    {
        _rows = Rows(FileA, 1200); // 75 MiB: 아직 메모리에 없는 블록이 대부분인 첫 회차
        Prewarmer p = Direct(4);

        Run(p);

        // 1 회차: 첫 회차 상한 64 MiB = 디스크로 간 블록 1,024 개에서 끝남. 이후 회차는 앞 1,024 블록이 메모리에 있어 빠르고 새 블록 64 개 (4 MiB) 씩 더 나아감.
        int round1 = 1024;
        int round2 = 1024 + 64;
        int round3 = 1088 + 64;
        Assert.Equal((FileA, 1023L * Block, (int)Block), _reads[round1 - 1]);
        Assert.Equal((FileA, 0L, (int)Block), _reads[round1]); // 둘째 회차는 다시 맨 앞부터
        Assert.Equal((FileA, 1087L * Block, (int)Block), _reads[round1 + round2 - 1]); // 둘째 회차는 새 블록 64 개 (1,024~1,087) 에서 끝남
        Assert.Equal((FileA, 1151L * Block, (int)Block), _reads[round1 + round2 + round3 - 1]); // 셋째 회차는 1,088~1,151
        Assert.Contains(_log.Lines, l => l.Contains("ended after 4 rounds (a.exe): reached 75 of 75 MiB of the list, 75 MiB read from disk", StringComparison.Ordinal)); // 넷째 회차가 남은 48 블록을 읽어 목록 끝까지
        Assert.DoesNotContain(_log.Lines, l => l.Contains("cap", StringComparison.Ordinal)); // 회차 상한은 플레이를 끝내지 않음
    }

    [Fact]
    public void TheDiskReadsOfThePlayAddUpAndReachingOneHundredTwentyEightMiBEndsTheSparseKeepWithTheReason()
    {
        _rows = Rows(FileA, 3000); // 187.5 MiB: 모두 메모리에 없음
        Prewarmer p = Direct(100);

        Run(p);

        // 첫 회차 64 MiB, 이후 회차 4 MiB 씩: 64 + 16 × 4 = 128 MiB 에서 열일곱째 회차가 끝남
        Assert.Contains(_log.Lines, l => l.Contains("ended after 17 rounds (a.exe): reached", StringComparison.Ordinal) && l.Contains("128 MiB read from disk", StringComparison.Ordinal) && l.EndsWith("(disk read cap of the play reached)", StringComparison.Ordinal));
        Assert.Equal(16, _sleeps.Count);
    }

    [Fact]
    public void BelowTheHoldFloorOfAvailableMemoryARoundIsSkippedAndAReaderIsNotEvenOpened()
    {
        _rows = Rows(FileA, 8);
        _allWarm = true;
        Prewarmer p = Direct(3);
        _avail = Prewarmer.HoldMinFloorMib - 1;
        Action<TimeSpan, int> end = _onSleep!;
        _onSleep = (t, n) =>
        {
            if (n == 2)
            {
                Interlocked.Exchange(ref _avail, 20000); // 둘째 쉼 뒤부터 여유가 생김
            }

            end(t, n);
        };

        Run(p);

        Assert.Equal(8, _reads.Count); // 첫 회차와 둘째 회차는 건너뛰고 셋째 회차만 읽음 (셋째 쉼에서 게임이 끝나므로 읽은 회차는 하나)
        Assert.Equal(1, _opened);
    }

    [Fact]
    public void AFileThatCannotBeOpenedIsSkippedInTheRoundAndTheOthersAreStillRead()
    {
        _rows = [.. Rows(FileB, 8), .. Rows(FileA, 8)];
        _seconds = (_, path) => path == FileB ? null : Fast;
        Prewarmer p = Direct(1);

        Run(p);

        Assert.Equal(16, _reads.Count);
        Assert.Contains(_log.Lines, l => l.Contains("reached 1 of 1 MiB of the list", StringComparison.Ordinal)); // 열지 못한 파일도 목록에서 빼지 않음 (다음 회차에 다시 시도)
    }

    [Fact]
    public void WithoutARankedSparseListNothingIsOpenedReadOrLogged()
    {
        _rows = null;
        Prewarmer p = Direct(1);

        Run(p);

        Assert.Empty(_reads);
        Assert.Equal(0, _opened);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("sparse blocks", StringComparison.Ordinal));
    }

    [Fact]
    public void TurningTheSwitchOffDuringThePlayEndsTheKeepAndAntiCheatFilesAreNotRead()
    {
        _rows = [.. Rows(@"D:\s\ricochet_driver.sys", 16), .. Rows(FileA, 16)];
        _allWarm = true;
        Prewarmer p = Direct(5);
        Action<TimeSpan, int> end = _onSleep!;
        _onSleep = (t, n) =>
        {
            _enabled = false; // 첫 쉼 뒤에 끔
            end(t, n);
        };

        Run(p);

        Assert.All(_reads, r => Assert.Equal(FileA, r.Path)); // 안티치트 구성요소 파일은 읽지 않음
        Assert.Equal(16, _reads.Count); // 끈 뒤에는 회차가 돌지 않음
        Assert.Contains(_log.Lines, l => l.Contains("a: sparse blocks kept while playing: 1 MiB in 1 files (target 512 MiB)", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.EndsWith("(turned off in the settings)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheBlocksJudgedAsDiskReadsAreNotedAsTheServicesOwnReadsSoTheNextSummaryDoesNotCountThemForTheGame()
    {
        _rows = Rows(FileA, 100);
        SelfReadObservation observation = SelfReadLog.Begin();
        Prewarmer p = Direct(2);

        Run(p);

        (IReadOnlyList<SelfReadBatch> batches, _) = observation.End();
        List<ReadOp> mine = [.. batches.SelectMany(b => b.Ops).Where(o => o.Path == FileA)];
        Assert.Equal(100, mine.Count); // 첫 회차에 디스크로 판정한 블록 100 개만 (둘째 회차는 모두 메모리라 기록할 것이 없음)
        Assert.Equal(Enumerable.Range(0, 100).Select(i => i * Block), mine.Select(o => o.Offset).Order());
    }

    [Fact]
    public void ReadsRunUnderTheSerializationLockBatchByBatchAndTheLockIsReleasedBetweenBatchesWhereTheThreadPauses()
    {
        _rows = Rows(FileA, 1200); // 75 MiB = 16 MiB 묶음 다섯 (16·16·16·16·11)
        _allWarm = true;
        Prewarmer p = Direct(1);

        Run(p);

        Assert.Equal(1200, _reads.Count);
        Assert.Equal(1200, _readsUnderLock); // 모든 읽기가 잠금 안
        Assert.Equal(4, _pauses.Count); // 묶음 사이 네 번
        Assert.All(_pauses, t => Assert.Equal(Prewarmer.SparseBatchPause, t));
        Assert.Equal(0, _pausesUnderLock); // 쉴 때는 잠금을 놓음
    }

    [Fact]
    public void OnlyTheIoPriorityIsLoweredInsideARoundAndTheReadingThreadKeepsItsNormalCpuPriority()
    {
        _rows = Rows(FileA, 8);
        _allWarm = true;
        Prewarmer p = Direct(2);

        Run(p);

        Assert.Equal([true, false, true, false], _ioLow); // 회차마다 낮췄다 되돌림
        Assert.All(_priorities, pr => Assert.Equal(ThreadPriority.Normal, pr)); // 낮은 CPU 우선순위는 복사를 늘려 디스크 읽기로 잘못 셈
    }

    [Fact]
    public void TheKeepRunsInASideThreadOfTheGameKeepingStartsRightAfterTheStartTouchAndEndsWithTheGame()
    {
        _rows = Rows(FileA, 32);
        _allWarm = true;
        Prewarmer p = Make();
        _onSleep = (t, n) =>
        {
            if (t == Prewarmer.KeepPollEvery)
            {
                Assert.True(_firstRead.Wait(TimeSpan.FromSeconds(10)), "the side thread reads its first round"); // 다시 만지기 루프는 곁 스레드의 첫 회차가 끝난 뒤에 게임을 끝냄
                _gameUp = false;
            }
            else
            {
                Thread.Sleep(1); // 곁 스레드의 쉼
            }
        };

        p.TouchAtGameStart(Game(), "a.exe");

        Assert.NotEmpty(_reads);
        Assert.Equal((FileA, 0L, (int)Block), _reads[0]);
        Assert.True(_opened >= 1);
        Assert.Contains(_log.Lines, l => l.Contains("a: sparse blocks kept while playing: 2 MiB in 1 files", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("a: sparse blocks kept while playing ended after", StringComparison.Ordinal));
        NoStoppedLine(_log);
    }

    [Fact]
    public void WithTheSwitchOffTheGameKeepingNeverOpensAReaderOrStartsTheSideThread()
    {
        _rows = Rows(FileA, 32);
        _enabled = false;
        Prewarmer p = Make();
        _onSleep = (t, n) => _gameUp = false;

        p.TouchAtGameStart(Game(), "a.exe");

        Assert.Equal(0, _opened);
        Assert.Empty(_reads);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("sparse blocks", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutTheStandbyLadderTheGameKeepingAndSoTheSparseKeepDoNotRun()
    {
        _rows = Rows(FileA, 32);
        var p = new Prewarmer([Game()], new Streamer(), new PrewarmEnv
        {
            AvailableRamMib = () => 20000,
            GameRunning = _ => _gameUp,
            ZoneBytes = g => 8L << 20,
            TraceExists = _ => true,
            StandbyLadderEnabled = () => false,
            KeepSparseEnabled = () => true,
            SparseRankedRows = _ => _rows,
            OpenBlockReader = () => new FakeReader(this),
        }, _log, null, () => new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));

        p.TouchAtGameStart(Game(), "a.exe");

        Assert.Empty(_reads);
    }
}
