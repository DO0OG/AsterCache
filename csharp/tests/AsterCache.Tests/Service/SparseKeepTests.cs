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
/// 읽는 대상 (유지 목록의 스파스 구간, 중요한 순서)·주기·디스크로 간 읽기 판정·디스크 읽기 조건 (로딩·대기열·회차 상한·플레이 합계 상한)·메모리 하한·열기 실패·스위치·서비스 자신의 읽기 기록·곁 스레드 연결을 확인합니다.
/// 읽기 수단이 IBlockReader 뿐임 (매핑·붙들기·찾기·표본 대리자는 부르면 시험이 실패) 은 직접 도는 시험에서 고정합니다.
public sealed class SparseKeepTests
{
    private const long Block = 65536;
    private const long Mib = 1L << 20;
    private static readonly string FileA = @"D:\s\a.xsub";
    private static readonly string FileB = @"D:\s\b.xsub";
    private static readonly string Dense = @"D:\s\dense.bin";
    private static readonly double Slow = 0.01;
    private static readonly double Fast = 0.0001;

    private readonly ListLog _log = new();
    private readonly List<(string Path, long Offset, int Length)> _reads = [];
    private readonly List<TimeSpan> _sleeps = []; // 1 초 이상의 쉼 (회차 주기)
    private readonly List<TimeSpan> _pauses = []; // 묶음 사이의 짧은 쉼
    private readonly List<bool> _ioLow = [];
    private readonly List<ThreadPriority> _priorities = [];
    private readonly HashSet<(string, long)> _inMemory = []; // 메모리에 올라온 블록 (읽으면 올라옴)
    private readonly HashSet<string> _sparse = new(StringComparer.OrdinalIgnoreCase) { FileA, FileB, @"D:\s\ricochet_driver.sys" };
    private readonly ManualResetEventSlim _firstRead = new();
    private volatile bool _gameUp = true;
    private volatile bool _enabled = true;
    private volatile bool _forbidMapping; // 켜면 매핑·붙들기·찾기·표본 대리자가 불리는 즉시 오류 (유지가 잡아 로그에 "stopped" 를 남기므로 시험이 실패)
    private long _avail = 20000;
    private int _opened;
    private int _readsUnderLock;
    private bool _allWarm; // 켜면 모든 블록이 메모리에 있음 (읽기가 모두 빠름)
    private Prewarmer? _prewarmer;
    private List<TraceEvent>? _rows; // 블록 순위 목록 (순위 순, 없으면 null: 순위 목록이 없는 게임)
    private List<ReadOp> _planned = [new ReadOp(FileA, 0, Mib), new ReadOp(FileB, 0, Mib), new ReadOp(Dense, 0, 8 * Mib)]; // 예열이 읽은 범위 (유지 목록의 원천)
    private Func<int, string, double?>? _seconds; // 지정하면 (읽기 번호, 경로) → 걸린 시간 (초, null 이면 열 수 없음), 없으면 메모리 모델
    private Action<TimeSpan, int>? _onSleep; // 회차 주기로 쉴 때마다 (쉰 시간, 지금까지 쉰 횟수) 로 부름

    private static GameSettings Game() => Games.Make("a", "a.exe");

    private static List<TraceEvent> Rows(string path, int blocks, int firstBlock = 0) =>
        [.. Enumerable.Range(firstBlock, blocks).Select(i => new TraceEvent(path, i * Block, Block))];

    private static object TouchSerial(Prewarmer p) => typeof(Prewarmer).GetField("_touchSerial", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(p)!;

    private static object KeepMemory(Prewarmer p) => typeof(Prewarmer).GetMethod("KeepMemoryOf", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(p, ["a"])!;

    private static long PlayDiskBytes(Prewarmer p) => (long)KeepMemory(p).GetType().GetProperty("HoldAgainDiskBytes")!.GetValue(KeepMemory(p))!;

    private static void SpendPlayDisk(Prewarmer p, long bytes) => _ = KeepMemory(p).GetType().GetMethod("AddHoldAgainDisk")!.Invoke(KeepMemory(p), [bytes]);

    // 게임 드라이브의 상태: 로딩 판정이 로딩 (실행 파일 시작) 이거나 조용함, 디스크 대기열 (null 이면 모름)
    private static void Feed(LoadPhaseBoard board, bool loading, long? queue)
    {
        _ = board.Add("a", new LoadingSample(0, loading ? 100 * Mib : 0, null, loading));
        if (queue is { } q)
        {
            board.NoteQueue("a", q);
        }
    }

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

    private sealed class Streamer(SparseKeepTests t) : IGameStreamer
    {
        public StreamStats Stream(GameSettings game) => new(30L << 20, 1, 0, 0.01);

        public IReadOnlyList<ReadOp>? PlannedPrewarmReads(GameSettings game) => t._planned;
    }

    private IOException Forbidden(string what) => new($"{what} must not be used by the sparse keep");

    private Prewarmer Make()
    {
        GameSettings a = Game();
        var p = new Prewarmer([a], new Streamer(this), new PrewarmEnv
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
            IsSparse = path => _sparse.Contains(path),
            RankedRows = _ => _rows,
            TouchRanges = (ranges, ct) => _forbidMapping ? throw Forbidden("touch") : new TouchResult(ranges.Sum(o => o.Length), 0.1, 0, 0, TouchEnd.Completed),
            FindResident = (ops, cap, ct, onGroup) => throw Forbidden("find"),
            KeepProbeSamples = (_, _) => throw Forbidden("probe"),
            OpenHold = () => throw Forbidden("hold"),
            KeepSparseEnabled = () => _enabled,
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

    // 디스크 읽기가 허용되는 조건 (로딩이 아니고 대기열 0) 의 판정판을 달아 줍니다.
    private LoadPhaseBoard Calm(Prewarmer p)
    {
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        Feed(board, loading: false, queue: 0);
        return board;
    }

    [Fact]
    public void ARoundReadsOnlyTheSparsePartOfThePrewarmedListBlockByBlockInImportanceOrderWithBufferedReadsOnlyAndLogsTheStartAndTheEnd()
    {
        _rows = [.. Rows(FileB, 16), .. Rows(FileA, 16)]; // 순위: 파일 B 의 블록이 먼저
        _allWarm = true;
        Prewarmer p = Direct(1);

        Run(p);

        Assert.Equal([.. _rows.Select(r => (r.Path, r.Offset, (int)r.Length))], _reads); // 일반 파일 (Dense) 은 읽지 않고, 스파스 구간만 중요한 순서로 블록 하나씩 (수단은 IBlockReader 뿐: 다른 대리자는 불리면 실패)
        Assert.Equal(1, _opened);
        Assert.Equal(0, _readsUnderLock); // 직렬화 잠금 (_touchSerial) 은 잡지 않음: 버퍼드 읽기는 하드 폴트 수에 안 잡힘
        Assert.Contains(_log.Lines, l => l.Contains("a: sparse blocks kept while playing: 2 MiB in 2 files", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("sparse blocks kept while playing ended after 1 rounds (a.exe): reached 2 of 2 MiB of the list, 0 MiB read from disk", StringComparison.Ordinal));
        NoStoppedLine(_log);
    }

    [Fact]
    public void ThePrewarmedSparseRangesThatThePlanReadsWithoutARankAreKeptToo()
    {
        _rows = Rows(Dense, 4); // 순위 목록에는 스파스 블록이 없음 (순위 목록을 스파스 포함으로 다시 만들기 전의 설치본): 예열이 읽은 범위가 곧 유지 대상
        _allWarm = true;
        Prewarmer p = Direct(1);

        Run(p);

        Assert.Equal(32, _reads.Count);
        Assert.All(_reads, r => Assert.NotEqual(Dense, r.Path));
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
        Assert.Equal(12 * 32, _reads.Count); // 쉬기 전의 첫 회차 + 쉰 뒤 회차 11 번 (열둘째 쉼에서 게임이 끝나 마지막 회차는 없음), 회차마다 스파스 구간 2 MiB
        Assert.Equal(12, _opened);
    }

    [Fact]
    public void ABlockCountsAsReadFromDiskOnlyAboveTwoMillisecondsPlusOneMillisecondPerMiB()
    {
        Assert.Equal(0.002 + (0.0625 * 0.001), Prewarmer.SparseDiskReadSeconds(Block), 9);
        Assert.Equal(0.003, Prewarmer.SparseDiskReadSeconds(1 << 20), 9);
        _planned = [new ReadOp(FileA, 0, 40 * Block)];
        _rows = Rows(FileA, 40);
        double threshold = Prewarmer.SparseDiskReadSeconds(Block);
        _seconds = (n, _) => n < 20 ? threshold : threshold + 0.0001; // 기준 그대로는 메모리, 조금이라도 넘으면 디스크
        Prewarmer p = Direct(1);
        _ = Calm(p);

        Run(p);

        Assert.Contains(_log.Lines, l => l.Contains("reached 2 of 2 MiB of the list, 1 MiB read from disk", StringComparison.Ordinal)); // 디스크로 센 블록 20 개 (1.25 MiB), 목록은 끝까지 읽음
    }

    [Fact]
    public void ABlockThatIsAlreadyInMemoryAddsNothingToTheDiskCountersAndBlocksReadFromDiskAreCountedAsTheServicesOwnReadsAndInThePlayTotal()
    {
        _planned = [new ReadOp(FileA, 0, 100 * Block)];
        _rows = Rows(FileA, 100);
        Prewarmer p = Direct(2);
        LoadPhaseBoard board = Calm(p);
        string root = Game().OriginRoot;
        SelfReadObservation observation = SelfReadLog.Begin();
        for (int i = 0; i < 40; i++)
        {
            _ = _inMemory.Add((FileA, i * Block)); // 앞 40 블록은 이미 메모리에 있음
        }

        Run(p);

        (IReadOnlyList<SelfReadBatch> batches, _) = observation.End();
        List<ReadOp> mine = [.. batches.SelectMany(b => b.Ops).Where(o => o.Path == FileA)];
        Assert.Equal(60, mine.Count); // 첫 회차에 디스크로 판정한 60 블록만 (둘째 회차는 모두 메모리라 기록할 것이 없음)
        Assert.Equal(Enumerable.Range(40, 60).Select(i => i * Block), mine.Select(o => o.Offset).Order());
        Assert.Equal(60 * Block, board.ServiceReads.Total(root)); // 로딩 판정이 게임의 읽기에서 뺄 서비스 자신의 읽기
        Assert.Equal(60 * Block, PlayDiskBytes(p)); // 다시 붙들기와 합쳐 세는 플레이 합계
    }

    [Fact]
    public void WhenARoundReachesItsDiskReadCapOnlyThatRoundEndsAndTheNextOneGoesFurtherFromTheStartOfTheList()
    {
        _planned = [new ReadOp(FileA, 0, 1000 * Block)];
        _rows = Rows(FileA, 1000); // 62.5 MiB: 아직 메모리에 없는 블록이 대부분
        Prewarmer p = Direct(2);
        _ = Calm(p);

        Run(p);

        // 1 회차: 회차 상한 32 MiB = 디스크로 간 블록 512 개에서 끝남. 둘째 회차는 앞 512 블록이 메모리에 있어 빠르고 새 블록 488 개로 목록 끝까지.
        Assert.Equal((FileA, 511L * Block, (int)Block), _reads[511]);
        Assert.Equal((FileA, 0L, (int)Block), _reads[512]); // 둘째 회차는 다시 맨 앞부터
        Assert.Equal(512 + 1000, _reads.Count);
        Assert.Equal(1000 * Block, PlayDiskBytes(p));
        Assert.Contains(_log.Lines, l => l.Contains("ended after 2 rounds (a.exe): reached 62 of 62 MiB of the list, 62 MiB read from disk", StringComparison.Ordinal));
        Assert.Contains("0 rounds with disk reads held back", _log.Lines.Last(l => l.Contains("ended after", StringComparison.Ordinal)), StringComparison.Ordinal); // 조건이 맞아 상한까지 읽은 회차는 막힌 회차가 아님
    }

    [Fact]
    public void TheDiskReadsOfThePlayShareTheHoldAgainTotalAndWhenItIsSpentARoundStopsAtTheFirstBlockFromDisk()
    {
        _planned = [new ReadOp(FileA, 0, 1000 * Block)];
        _rows = Rows(FileA, 1000);
        Prewarmer p = Direct(2);
        _ = Calm(p);
        SpendPlayDisk(p, Prewarmer.HoldAgainDiskCapBytes - (10 * Mib)); // 다시 붙들기가 이미 쓴 양: 플레이 합계의 남은 양은 10 MiB

        Run(p);

        // 1 회차: 남은 10 MiB = 블록 160 개에서 끝남. 2 회차: 합계를 다 써서 앞 160 블록 (메모리) 과 디스크로 간 새 블록 하나에서 끝남.
        Assert.Equal(160 + 161, _reads.Count);
        Assert.Equal(Prewarmer.HoldAgainDiskCapBytes + Block, PlayDiskBytes(p)); // 상한을 넘는 것은 회차마다 디스크로 간 블록 하나 (64 KiB) 까지
        Assert.Contains("1 rounds with disk reads held back", _log.Lines.Last(l => l.Contains("ended after", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    public enum Blocker
    {
        Loading,
        BusyQueue,
        UnknownQueue,
    }

    [Theory]
    [InlineData(Blocker.Loading)]
    [InlineData(Blocker.BusyQueue)]
    [InlineData(Blocker.UnknownQueue)]
    public void WhileTheLoadingPhaseOrTheDriveQueueForbidsDiskReadsTheResidentPartIsStillReadAndEachRoundStopsAtTheFirstBlockFromDisk(Blocker why)
    {
        _planned = [new ReadOp(FileA, 0, 40 * Block)];
        _rows = Rows(FileA, 40);
        for (int i = 0; i < 5; i++)
        {
            _ = _inMemory.Add((FileA, i * Block)); // 앞 5 블록은 메모리에 있음
        }

        Prewarmer p = Direct(2);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        Feed(board, loading: why == Blocker.Loading, queue: why == Blocker.BusyQueue ? 2L : why == Blocker.UnknownQueue ? null : 0L);

        Run(p);

        // 1 회차: 메모리의 5 블록 + 디스크로 간 블록 하나에서 끝남 (6 읽기). 2 회차: 앞 6 블록이 메모리라 빠르고 다음 디스크 블록 하나에서 끝남 (7 읽기).
        Assert.Equal(6 + 7, _reads.Count);
        Assert.Equal(2 * Block, PlayDiskBytes(p));
        Assert.Contains("2 rounds with disk reads held back", _log.Lines.Last(l => l.Contains("ended after", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void ALoadingThatStartsInTheMiddleOfARoundIsSeenAtTheNextBatchAndTheRoundStopsAtTheFirstBlockFromDisk()
    {
        _planned = [new ReadOp(FileA, 0, 1000 * Block)];
        _rows = Rows(FileA, 1000);
        Prewarmer p = Direct(1);
        LoadPhaseBoard board = Calm(p);
        _seconds = (n, _) =>
        {
            if (n == 10)
            {
                Feed(board, loading: true, queue: 0); // 첫 묶음을 읽는 중에 로딩이 시작됨
            }

            return Slow; // 모두 디스크로 간 읽기
        };

        Run(p);

        // 첫 묶음 (16 MiB = 256 블록) 은 조건을 보고 시작했으므로 끝까지 읽고 (16 MiB < 회차 상한 32 MiB), 둘째 묶음은 로딩을 보고 첫 디스크 블록에서 끝남.
        Assert.Equal(256 + 1, _reads.Count);
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

        Assert.Equal(32, _reads.Count); // 첫 회차와 둘째 회차는 건너뛰고 셋째 회차만 읽음 (셋째 쉼에서 게임이 끝나므로 읽은 회차는 하나)
        Assert.Equal(1, _opened);
    }

    [Fact]
    public void AFileThatCannotBeOpenedIsSkippedInTheRoundAndTheOthersAreStillRead()
    {
        _rows = [.. Rows(FileB, 16), .. Rows(FileA, 16)];
        _seconds = (_, path) => path == FileB ? null : Fast;
        Prewarmer p = Direct(1);

        Run(p);

        Assert.Equal(32, _reads.Count);
        Assert.Contains(_log.Lines, l => l.Contains("reached 2 of 2 MiB of the list", StringComparison.Ordinal)); // 열지 못한 파일도 목록에서 빼지 않음 (다음 회차에 다시 시도)
    }

    [Fact]
    public void WithoutARankedListNothingIsOpenedReadOrLogged()
    {
        _rows = null; // 순위 목록이 없는 게임 (트레이스를 금지하는 안티치트 게임 등)
        Prewarmer p = Direct(1);

        Run(p);

        Assert.Empty(_reads);
        Assert.Equal(0, _opened);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("sparse blocks", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenThePrewarmedListHasNoSparseRangeNothingIsReadOrLogged()
    {
        _planned = [new ReadOp(Dense, 0, 8 * Mib)];
        _rows = Rows(Dense, 4);
        Prewarmer p = Direct(1);

        Run(p);

        Assert.Empty(_reads);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("sparse blocks", StringComparison.Ordinal));
    }

    [Fact]
    public void TurningTheSwitchOffDuringThePlayEndsTheKeepAndAntiCheatFilesAreNotRead()
    {
        _planned = [new ReadOp(@"D:\s\ricochet_driver.sys", 0, Mib), new ReadOp(FileA, 0, Mib)];
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
        Assert.Contains(_log.Lines, l => l.Contains("a: sparse blocks kept while playing: 1 MiB in 1 files", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.EndsWith("(turned off in the settings)", StringComparison.Ordinal));
    }

    [Fact]
    public void OnlyTheIoPriorityIsLoweredInsideARoundAndTheReadingThreadKeepsItsNormalCpuPriority()
    {
        _rows = Rows(FileA, 8);
        _allWarm = true;
        Prewarmer p = Direct(2);

        Run(p);

        Assert.Contains(true, _ioLow);
        Assert.Equal(_ioLow.Count(l => l), _ioLow.Count(l => !l)); // 회차마다 낮췄다 되돌림 (읽기 구간 밖의 목록 계산도 낮은 우선순위 읽기로 한 번씩 낮췄다 되돌림)
        Assert.All(_priorities, pr => Assert.Equal(ThreadPriority.Normal, pr)); // 낮은 CPU 우선순위는 복사를 늘려 디스크 읽기로 잘못 셈
    }

    [Fact]
    public void ReadsPauseBetweenBatches()
    {
        _planned = [new ReadOp(FileA, 0, 1200 * Block)]; // 75 MiB = 16 MiB 묶음 다섯 (16·16·16·16·11)
        _rows = Rows(FileA, 1200);
        _allWarm = true;
        Prewarmer p = Direct(1);

        Run(p);

        Assert.Equal(1200, _reads.Count);
        Assert.Equal(4, _pauses.Count); // 묶음 사이 네 번
        Assert.All(_pauses, t => Assert.Equal(Prewarmer.SparseBatchPause, t));
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
        Assert.Contains(_log.Lines, l => l.Contains("a: sparse blocks kept while playing: 2 MiB in 2 files", StringComparison.Ordinal));
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
        var p = new Prewarmer([Game()], new Streamer(this), new PrewarmEnv
        {
            AvailableRamMib = () => 20000,
            GameRunning = _ => _gameUp,
            ZoneBytes = g => 8L << 20,
            TraceExists = _ => true,
            StandbyLadderEnabled = () => false,
            KeepSparseEnabled = () => true,
            RankedRows = _ => _rows,
            OpenBlockReader = () => new FakeReader(this),
        }, _log, null, () => new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero));

        p.TouchAtGameStart(Game(), "a.exe");

        Assert.Empty(_reads);
    }
}
