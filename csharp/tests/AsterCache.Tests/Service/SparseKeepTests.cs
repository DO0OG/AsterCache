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
    private static readonly double Fast = 0.00002; // 캐시 적중의 64 KiB 복사 (기준 0.15 ms 의 아래, 16 블록의 합 0.32 ms 도 묶음 기준 0.85 ms 의 아래)

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
    private volatile bool _ladder = true; // memory.standby_ladder
    private volatile bool _forbidMapping; // 켜면 매핑·붙들기·찾기·표본 대리자가 불리는 즉시 오류 (유지가 잡아 로그에 "stopped" 를 남기므로 시험이 실패)
    private long _avail = 20000;
    private int _opened;
    private int _readsUnderLock;
    private string? _antiCheat; // 게임 설정의 안티치트 종류 (붙들기 게이트)
    private bool _allWarm; // 켜면 모든 블록이 메모리에 있음 (읽기가 모두 빠름)
    private Prewarmer? _prewarmer;
    private List<TraceEvent>? _rows; // 블록 순위 목록 (순위 순, 없으면 null: 순위 목록이 없는 게임)
    private PlayRankedRows? _playRows; // 유지 목록이 쓰는 경기 가중 순서 (없으면 null: 순위 순서 그대로)
    private List<ReadOp> _planned = [new ReadOp(FileA, 0, Mib), new ReadOp(FileB, 0, Mib), new ReadOp(Dense, 0, 8 * Mib)]; // 예열이 읽은 범위 (유지 목록의 원천)
    private Func<int, string, double?>? _seconds; // 지정하면 (읽기 번호, 경로) → 걸린 시간 (초, null 이면 열 수 없음), 없으면 메모리 모델
    private Action<TimeSpan, int>? _onSleep; // 회차 주기로 쉴 때마다 (쉰 시간, 지금까지 쉰 횟수) 로 부름

    private static GameSettings Game() => Games.Make("a", "a.exe");

    private static List<TraceEvent> Rows(string path, int blocks, int firstBlock = 0) =>
        [.. Enumerable.Range(firstBlock, blocks).Select(i => new TraceEvent(path, i * Block, Block))];

    private static object TouchSerial(Prewarmer p) => typeof(Prewarmer).GetField("_touchSerial", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(p)!;

    private static object KeepMemory(Prewarmer p) => typeof(Prewarmer).GetMethod("KeepMemoryOf", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(p, ["a"])!;

    // 다시 붙들기가 이 플레이에서 디스크로 읽은 양의 합 (스파스 유지는 더하지 않음)
    private static long HoldAgainDiskBytes(Prewarmer p) => (long)KeepMemory(p).GetType().GetProperty("HoldAgainDiskBytes")!.GetValue(KeepMemory(p))!;

    // 스파스 유지가 디스크로 센 양의 합 (서비스 자신의 읽기로 로딩 판정판에 남긴 값)
    private static long SparseDisk(Prewarmer p) => p.LoadPhases!.ServiceReads.Total(Game().OriginRoot);

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
        GameSettings a = Game() with { AntiCheat = _antiCheat };
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
            StandbyLadderEnabled = () => _ladder,
            ListStamp = _ => "s1",
            IsSparse = path => _sparse.Contains(path),
            RankedRows = _ => _rows,
            PlayRankedRows = _ => _playRows,
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

    // 사유 줄 (같은 사유는 게임당 한 줄) 과 끝 줄
    private List<string> SkipLines() => [.. _log.Lines.Where(l => l.Contains("sparse blocks kept while playing skipped (", StringComparison.Ordinal))];

    private string EndLine() => _log.Lines.Last(l => l.Contains("sparse blocks kept while playing ended after", StringComparison.Ordinal));

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
    public void TheKeepListTakesItsOrderFromThePlayRowsWhenThereAreAnyAndFromTheRankedRowsOtherwise()
    {
        _rows = [.. Rows(FileB, 16), .. Rows(FileA, 16)]; // 순위: 파일 B 의 블록이 먼저
        _playRows = new PlayRankedRows(_rows, [.. Rows(FileA, 16), .. Rows(FileB, 16)], new HashSet<string>(_sparse)); // 경기 가중 점수 순: 파일 A 의 블록이 먼저
        _allWarm = true;

        Run(Direct(1));

        Assert.Equal([.. Rows(FileA, 16).Concat(Rows(FileB, 16)).Select(r => (r.Path, r.Offset, (int)r.Length))], _reads); // 유지 목록 (스파스 유지가 읽는 순서) 이 점수 순서를 받음
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
    public void ASlowBlockCountsAloneAndAGroupWhoseSumPassesTheGroupLimitWithoutASlowBlockCountsWhole()
    {
        Assert.Equal(0.00005 + (0.0625 * 0.0016), Prewarmer.SparseDiskReadSeconds(Block), 9); // 64 KiB 에 0.15 ms
        Assert.Equal(0.00005 + 0.0016, Prewarmer.SparseDiskReadSeconds(1 << 20), 9);
        Assert.Equal(0.00005 + 0.0008, Prewarmer.SparseGroupSeconds(1 << 20), 9); // 묶음 (1 MiB) 의 누적 기준 0.85 ms
        _planned = [new ReadOp(FileA, 0, 160 * Block)]; // 묶음 10 개
        _rows = Rows(FileA, 160);
        double limit = Prewarmer.SparseDiskReadSeconds(Block);
        _seconds = (n, _) => n == 5 ? limit + 0.00001 : n is >= 32 and < 48 ? 0.00006 : Fast; // 묶음 0: 블록 하나가 기준을 넘음, 묶음 2: 블록마다 기준 안이지만 합 0.96 ms 가 묶음 기준 0.85 ms 를 넘음, 나머지는 메모리
        Prewarmer p = Direct(1);
        _ = Calm(p);

        Run(p);

        Assert.Equal((16 + 1) * Block, SparseDisk(p)); // 센 것은 누적 기준을 넘은 묶음 하나 (1 MiB) 전체와, 블록 기준을 넘은 블록 하나 (64 KiB) 만
        Assert.Contains(_log.Lines, l => l.Contains("reached 10 of 10 MiB of the list, 1 MiB read from disk", StringComparison.Ordinal));
    }

    [Fact]
    public void ABlockThatIsAlreadyInMemoryAddsNothingToTheDiskCountersAndOnlyTheSlowBlocksAreCountedWhileTheirGroupsAreNotedAsTheServicesOwnReads()
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
        // 서비스 자신의 읽기 기록에는 디스크로 센 묶음 전체를 남김: 묶음 2 (32~47) 는 일부만 디스크였어도 묶음 전체, 나머지 (48~99) 도 디스크라 블록 32~99 의 68 개. 둘째 회차는 모두 메모리라 기록할 것이 없음.
        Assert.Equal(68, mine.Count);
        Assert.Equal(Enumerable.Range(32, 68).Select(i => i * Block), mine.Select(o => o.Offset).Order());
        Assert.Equal(68 * Block, board.ServiceReads.Total(root)); // 센 양 (로딩 판정이 게임의 읽기에서 뺄 값): 묶음 2 는 느린 블록 8 개의 시간 합이 묶음 기준을 넘어 묶음 전체, 나머지는 느린 블록 모두
        Assert.Equal(0, HoldAgainDiskBytes(p)); // 다시 붙들기의 합계에는 더하지 않음
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
        Assert.Equal(1000 * Block, SparseDisk(p));
        Assert.Contains(_log.Lines, l => l.Contains("ended after 2 rounds (a.exe): reached 62 of 62 MiB of the list, 62 MiB read from disk", StringComparison.Ordinal));
        Assert.Contains("0 rounds with disk reads held back", _log.Lines.Last(l => l.Contains("ended after", StringComparison.Ordinal)), StringComparison.Ordinal); // 조건이 맞아 상한까지 읽은 회차는 막힌 회차가 아님
    }

    [Fact]
    public void TheSparseKeepHasItsOwnPlayCapOfFiveHundredTwelveMiBAndWhenItIsSpentARoundReadsOnlyTheBlocksKnownToBeInMemory()
    {
        Assert.Equal(512L << 20, Prewarmer.SparsePlayDiskCapBytes);
        _planned = [new ReadOp(FileA, 0, 12_000 * Block)]; // 750 MiB: 모두 메모리에 없음
        _rows = Rows(FileA, 12_000);
        Prewarmer p = Direct(18);
        _ = Calm(p);

        Run(p);

        // 회차 상한 32 MiB 씩 16 회로 512 MiB 를 다 쓴 뒤 (17·18 회차) 에는 앞 회차가 읽은 블록만 읽어 합계를 넘지 않음.
        Assert.Equal(Prewarmer.SparsePlayDiskCapBytes, SparseDisk(p));
        Assert.Equal(0, HoldAgainDiskBytes(p)); // 다시 붙들기의 합계와 따로 셈
        Assert.Contains("2 rounds with disk reads held back", _log.Lines.Last(l => l.Contains("ended after", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void WhenTheHoldAgainTotalIsSpentTheSparseKeepStillReadsFromDiskWithinItsOwnCap()
    {
        _planned = [new ReadOp(FileA, 0, 1000 * Block)];
        _rows = Rows(FileA, 1000);
        Prewarmer p = Direct(1);
        _ = Calm(p);
        _ = KeepMemory(p).GetType().GetMethod("AddHoldAgainDisk")!.Invoke(KeepMemory(p), [Prewarmer.HoldAgainDiskCapBytes]); // 다시 붙들기가 합계를 다 씀

        Run(p);

        Assert.Equal(512 * Block, SparseDisk(p)); // 회차 상한 32 MiB 까지 읽음
        Assert.Equal(Prewarmer.HoldAgainDiskCapBytes, HoldAgainDiskBytes(p)); // 그 합계는 그대로
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
    public void WhileTheLoadingPhaseOrTheDriveQueueForbidsDiskReadsOnlyTheBlocksKnownToBeInMemoryAreReadAndEachRoundStopsAtTheFirstOneThatWentToDisk(Blocker why)
    {
        _planned = [new ReadOp(FileA, 0, 40 * Block)];
        _rows = Rows(FileA, 40);
        Prewarmer p = Direct(3);
        _ = Calm(p);
        long total1 = 0;
        Action<TimeSpan, int> end = _onSleep!;
        _onSleep = (t, n) =>
        {
            if (n == 1)
            {
                lock (_inMemory)
                {
                    for (int i = 10; i < 40; i++)
                    {
                        _ = _inMemory.Remove((FileA, i * Block)); // 첫 회차 뒤에 블록 10~39 가 메모리에서 밀려남
                    }
                }

                total1 = SparseDisk(p); // 첫 회차까지 센 양 (판정판을 바꾸면 누계가 새로 시작함)
                var board = new LoadPhaseBoard(_log);
                p.LoadPhases = board;
                Feed(board, loading: why == Blocker.Loading, queue: why == Blocker.BusyQueue ? 2L : why == Blocker.UnknownQueue ? null : 0L); // 이제 디스크 읽기가 허용되지 않음
            }

            end(t, n);
        };

        Run(p);

        // 1 회차: 40 블록 모두 읽음 (디스크로 센 묶음 셋 = 40 블록). 2 회차: 블록 0~9 는 메모리, 블록 10 은 밀려나 디스크로 간 첫 블록에서 끝남 (11 읽기). 3 회차: 블록 10 은 다시 메모리, 11 에서 끝남 (12 읽기).
        Assert.Equal(40 + 11 + 12, _reads.Count);
        Assert.Equal(42 * Block, SparseDisk(p) + total1);
        Assert.Contains("2 rounds with disk reads held back", _log.Lines.Last(l => l.Contains("ended after", StringComparison.Ordinal)), StringComparison.Ordinal);
    }

    [Fact]
    public void WhenDiskReadsAreForbiddenInTheFirstRoundNothingIsKnownToBeInMemoryAndNothingIsRead()
    {
        _planned = [new ReadOp(FileA, 0, 40 * Block)];
        _rows = Rows(FileA, 40);
        Prewarmer p = Direct(1);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        Feed(board, loading: true, queue: 0L); // 게임 시작 로딩

        Run(p);

        Assert.Empty(_reads);
        Assert.Equal(0, SparseDisk(p));
        Assert.Contains("a.exe: nothing read: loading phase", Assert.Single(SkipLines()), StringComparison.Ordinal);
        Assert.Contains("skipped rounds: nothing read: loading phase x1", EndLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void ALoadingThatStartsInTheMiddleOfARoundIsSeenAtTheNextGroupAndNothingUnknownIsReadAfterIt()
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

        // 첫 묶음 (16 블록) 은 조건을 보고 시작했으므로 끝까지 읽고, 다음 묶음부터는 로딩이라 확인된 블록만 읽는데 (첫 묶음의 16 블록은 이미 읽어 확인됨) 그 뒤 묶음의 블록은 확인된 것이 없어 읽지 않음.
        Assert.Equal(16, _reads.Count);
        Assert.Equal(16 * Block, SparseDisk(p));
    }

    [Fact]
    public void TheMemoryFloorIsWatchedAtEveryGroupAndAFallBelowItInTheMiddleOfARoundEndsTheRound()
    {
        _planned = [new ReadOp(FileA, 0, 1000 * Block)];
        _rows = Rows(FileA, 1000);
        _allWarm = true;
        Prewarmer p = Direct(1);
        _seconds = (n, _) =>
        {
            if (n == 10)
            {
                Interlocked.Exchange(ref _avail, Prewarmer.HoldMinFloorMib - 1); // 읽는 도중 가용 메모리가 하한 아래로 내려감
            }

            return Fast;
        };

        Run(p);

        Assert.Equal(16, _reads.Count); // 읽던 묶음 하나만 끝까지 읽고 끝남
    }

    [Fact]
    public void ForAGameWhoseAntiCheatForbidsTheHoldNothingIsOpenedReadOrKeptEvenWithARankedListLeftOver()
    {
        _antiCheat = "vanguard";
        _rows = Rows(FileA, 16);
        _allWarm = true;
        Prewarmer p = Direct(1);

        Run(p);

        Assert.Empty(_reads);
        Assert.Equal(0, _opened);
        Assert.Contains(_log.Lines, l => l.Contains("a: sparse blocks kept while playing skipped (a.exe: anti-cheat: vanguard)", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("kept while playing:", StringComparison.Ordinal));
    }

    [Fact]
    public void ForARicochetGameWhichTheHoldGateAllowsTheKeepRuns()
    {
        _antiCheat = "ricochet";
        _rows = Rows(FileA, 16);
        _allWarm = true;
        Prewarmer p = Direct(1);

        Run(p);

        Assert.NotEmpty(_reads);
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
        string line = Assert.Single(SkipLines()); // 같은 사유가 이어진 두 회차는 한 줄
        Assert.Contains("a: sparse blocks kept while playing skipped (a.exe: low memory, available 2047 MiB < floor 2048 MiB)", line, StringComparison.Ordinal);
        Assert.Contains("skipped rounds: low memory x2", EndLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void AReasonThatComesBackAfterARoundThatReadIsLoggedAgain()
    {
        _rows = Rows(FileA, 8);
        _allWarm = true;
        Prewarmer p = Direct(4);
        _avail = Prewarmer.HoldMinFloorMib - 1;
        Action<TimeSpan, int> end = _onSleep!;
        _onSleep = (t, n) =>
        {
            Interlocked.Exchange(ref _avail, n == 1 ? 20000 : n == 2 ? Prewarmer.HoldMinFloorMib - 1 : _avail); // 첫 회차 건너뜀, 둘째 회차 읽음, 셋째 회차 건너뜀
            end(t, n);
        };

        Run(p);

        Assert.Equal(2, SkipLines().Count); // 읽은 회차가 끼면 같은 사유도 다시 한 줄
        Assert.Contains("skipped rounds: low memory x3", EndLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void WithTheStandbyLadderOffEveryRoundIsSkippedWithOneReasonLine()
    {
        _rows = Rows(FileA, 8);
        Prewarmer p = Direct(3);
        _ladder = false;

        Run(p);

        Assert.Empty(_reads);
        Assert.Contains("a.exe: standby ladder is off", Assert.Single(SkipLines()), StringComparison.Ordinal);
        Assert.Contains("skipped rounds: standby ladder is off x3", EndLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void WhenThePrewarmLeftTheSparseRangesOutOfTheMemoryPrewarmThatIsTheReasonAndNotThatTheListHasNone()
    {
        _planned = [new ReadOp(Dense, 0, 8 * Mib)];
        _rows = Rows(Dense, 4);
        Prewarmer p = Direct(2);
        typeof(Prewarmer).GetMethod("SetSparseOut", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(p, ["a", true]); // 예열 판정이 스파스 범위를 뺐음 (표식)

        Run(p);

        Assert.Empty(_reads);
        Assert.Contains("a.exe: sparse files left out of the memory prewarm, nothing to keep, the prewarm budget did not fit the whole list; ", Assert.Single(SkipLines()), StringComparison.Ordinal);
        Assert.Contains("skipped rounds: sparse files left out of the memory prewarm, nothing to keep x2", EndLine(), StringComparison.Ordinal);
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
        Assert.Contains(", 16 block reads failed", EndLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void ABlockThatCouldNotBeReadIsNotAKeepTargetSoOnlyTheBlocksThatWereReadAreReadWhileDiskReadsAreHeldBack()
    {
        _rows = [.. Rows(FileB, 16), .. Rows(FileA, 16)];
        _seconds = (_, path) => path == FileB ? null : Fast;
        Prewarmer p = Direct(2);
        LoadPhaseBoard board = Calm(p);
        Action<TimeSpan, int> end = _onSleep!;
        _onSleep = (t, n) =>
        {
            if (n == 1)
            {
                Feed(board, loading: true, queue: 0); // 둘째 회차는 로딩 중: 앞 회차가 읽어 확인한 블록만 읽음
            }

            end(t, n);
        };

        Run(p);

        Assert.Equal(16, _reads.Count(r => r.Path == FileB)); // 읽지 못한 B 의 블록은 둘째 회차에 읽지 않음
        Assert.Equal(32, _reads.Count(r => r.Path == FileA));
        Assert.Contains(", 16 block reads failed", EndLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void ARoundInWhichNoBlockCouldBeReadIsReportedAsNothingRead()
    {
        _rows = Rows(FileA, 16);
        _seconds = (_, _) => null;
        Prewarmer p = Direct(2);

        Run(p);

        Assert.Contains("a.exe: nothing read: block reads failed, 32 reads failed (a file could not be opened or read)", Assert.Single(SkipLines()), StringComparison.Ordinal); // 계획의 파일 둘 (블록 32 개) 이 모두 실패
        Assert.Contains("skipped rounds: nothing read: block reads failed x2", EndLine(), StringComparison.Ordinal);
        Assert.Contains(", 64 block reads failed", EndLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutARankedListNothingIsOpenedReadOrLogged()
    {
        _rows = null; // 순위 목록이 없는 게임 (트레이스를 금지하는 안티치트 게임 등)
        Prewarmer p = Direct(1);

        Run(p);

        Assert.Empty(_reads);
        Assert.Equal(0, _opened);
        Assert.Contains("a: sparse blocks kept while playing skipped (a.exe: no ranked block list", Assert.Single(_log.Lines, l => l.Contains("sparse blocks", StringComparison.Ordinal)), StringComparison.Ordinal); // 끝내는 사유라 그 한 줄뿐 (끝 줄 없음)
    }

    [Fact]
    public void WhenThePrewarmedListHasNoSparseRangeNothingIsReadAndEachRoundIsCountedUnderOneReasonLine()
    {
        _planned = [new ReadOp(Dense, 0, 8 * Mib)];
        _rows = Rows(Dense, 4);
        Prewarmer p = Direct(3);

        Run(p);

        Assert.Empty(_reads);
        Assert.Equal(0, _opened);
        Assert.Contains("a.exe: no sparse range in the keep list, ", Assert.Single(SkipLines()), StringComparison.Ordinal);
        Assert.Contains(" MiB in the keep list", Assert.Single(SkipLines()), StringComparison.Ordinal);
        Assert.Contains("skipped rounds: no sparse range in the keep list x3", EndLine(), StringComparison.Ordinal);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("kept while playing:", StringComparison.Ordinal)); // 시작 줄은 읽을 블록이 있을 때만
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
