using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임 실행 중 스파스 파일 블록 유지 (memory.keep_sparse): 가짜 환경과 가짜 읽기 도구 (IBlockReader) 로 읽는 순서·주기·디스크로 간 읽기 판정과 경계·회차와 플레이 상한·메모리 하한·열기 실패·스위치·곁 스레드 연결을 확인합니다.
/// 읽기 수단이 IBlockReader 뿐임 (매핑·붙들기·찾기·표본 대리자는 부르면 시험이 실패) 은 직접 도는 시험에서 고정합니다.
public sealed class SparseKeepTests
{
    private const long Block = 65536;
    private static readonly string FileA = @"D:\s\a.xsub";
    private static readonly string FileB = @"D:\s\b.xsub";

    private readonly ListLog _log = new();
    private readonly List<(string Path, long Offset, int Length)> _reads = [];
    private readonly List<TimeSpan> _sleeps = [];
    private readonly ManualResetEventSlim _firstRead = new();
    private volatile bool _gameUp = true;
    private volatile bool _enabled = true;
    private volatile bool _forbidMapping; // 켜면 매핑·붙들기·찾기·표본 대리자가 불리는 즉시 오류 (유지가 잡아 로그에 "stopped" 를 남기므로 시험이 실패)
    private long _avail = 20000;
    private int _opened;
    private List<TraceEvent>? _rows;
    private Func<int, string, double?> _seconds = (_, _) => 0.0001; // (읽기 번호, 경로) → 걸린 시간 (초), null 이면 열 수 없음
    private Action<TimeSpan, int>? _onSleep; // 쉴 때마다 (쉰 시간, 지금까지 쉰 횟수) 로 부름

    private static GameSettings Game() => Games.Make("a", "a.exe");

    private static List<TraceEvent> Rows(string path, int blocks, int firstBlock = 0) =>
        [.. Enumerable.Range(firstBlock, blocks).Select(i => new TraceEvent(path, i * Block, Block))];

    private sealed class FakeReader(SparseKeepTests t) : IBlockReader
    {
        public double? Read(string path, long offset, int length)
        {
            int n;
            lock (t._reads)
            {
                n = t._reads.Count;
                t._reads.Add((path, offset, length));
            }

            t._firstRead.Set();
            return t._seconds(n, path);
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
            OpenBlockReader = () =>
            {
                _ = Interlocked.Increment(ref _opened);
                return new FakeReader(this);
            },
            Sleep = (t, ct) =>
            {
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

        return p;
    }

    // 직접 도는 시험: 쉼이 sleepsUntilEnd 번째에서 게임이 끝남 (첫 회차는 쉬기 전에 돎).
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
        Prewarmer p = Direct(1);

        Run(p);

        Assert.Equal([.. _rows.Select(r => (r.Path, r.Offset, (int)r.Length))], _reads); // 순위 순서 그대로, 블록 하나씩 (수단은 IBlockReader 뿐: 다른 대리자는 불리면 실패)
        Assert.Equal(1, _opened);
        Assert.Contains(_log.Lines, l => l.Contains($"a: sparse blocks kept while playing: 2 MiB in 2 files (target {LoadRefillPlanner.SparseRankedCapBytes >> 20} MiB)", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("a: sparse blocks kept while playing ended after 1 rounds (a.exe): 2 MiB left in the list, 0 MiB read from disk", StringComparison.Ordinal));
        NoStoppedLine(_log);
    }

    [Fact]
    public void TheFirstRoundRunsAtOnceThenEveryTwentySecondsForTheFirstThreeMinutesAndEveryThirtyAfter()
    {
        _rows = Rows(FileA, 8);
        Prewarmer p = Direct(12);

        Run(p);

        Assert.Equal(12, _sleeps.Count);
        Assert.All(_sleeps.Take(9), t => Assert.Equal(TimeSpan.FromSeconds(20), t)); // 쉰 시간 합 180 초가 될 때까지
        Assert.All(_sleeps.Skip(9), t => Assert.Equal(TimeSpan.FromSeconds(30), t));
        Assert.Equal(12 * 8, _reads.Count); // 쉬기 전의 첫 회차 + 쉰 뒤 회차 11 번 (열둘째 쉼에서 게임이 끝나 마지막 회차는 없음)
        Assert.Equal(12, _opened);
    }

    [Fact]
    public void TwoDiskBlocksInARowAreABoundaryTheRestIsLeftOutOfThePlayAndASingleSlowBlockIsNot()
    {
        _rows = Rows(FileA, 160); // 10 MiB
        _seconds = (n, _) => n == 20 || n == 80 || n == 81 ? 0.01 : 0.0001; // 20 번째는 혼자 느림, 80·81 번째는 이어서 느림 (첫 회차)
        Prewarmer p = Direct(2);

        Run(p);

        Assert.Equal(82 + 80, _reads.Count); // 첫 회차는 둘째 느린 블록에서 멈추고, 다음 회차는 앞 80 블록만
        Assert.Equal(80, _reads.Skip(82).Select(r => r.Offset).Distinct().Count());
        Assert.Contains(_log.Lines, l => l.Contains("a: sparse blocks kept while playing: boundary after 5 MiB, 5 MiB left out for the rest of this play", StringComparison.Ordinal));
        Assert.Single(_log.Lines, l => l.Contains("boundary", StringComparison.Ordinal)); // 혼자 느린 블록은 경계가 아님
        Assert.Contains(_log.Lines, l => l.Contains("5 MiB left in the list, 0 MiB read from disk", StringComparison.Ordinal));
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

        Assert.Equal(22, _reads.Count); // 20 번째와 21 번째가 이어서 디스크: 거기서 멈춤
        Assert.Contains(_log.Lines, l => l.Contains("a: sparse blocks kept while playing: boundary after 1 MiB, 1 MiB left out for the rest of this play", StringComparison.Ordinal));
    }

    [Fact]
    public void ADiskReadCapOfFourMiBInOneRoundEndsTheSparseKeepOfThePlayEvenWithoutTwoInARow()
    {
        _rows = Rows(FileA, 140);
        _seconds = (n, _) => n % 2 == 0 ? 0.01 : 0.0001; // 한 블록 걸러 디스크 (경계 판정에는 걸리지 않음)
        Prewarmer p = Direct(5);

        Run(p);

        Assert.Equal(129, _reads.Count); // 65 번째 디스크 블록 (4 MiB 초과) 에서 멈춤
        Assert.Empty(_sleeps); // 이 플레이의 유지를 끝냈으므로 다시 쉬지 않음
        Assert.Contains(_log.Lines, l => l.Contains("ended after 1 rounds", StringComparison.Ordinal) && l.EndsWith("(disk read cap reached)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDiskReadsOfThePlayAddUpAndPassingThirtyTwoMiBEndsTheSparseKeep()
    {
        _rows = Rows(FileA, 100);
        _seconds = (n, _) => (n % 100) % 2 == 0 ? 0.01 : 0.0001; // 회차마다 50 블록 (3.125 MiB) 이 디스크로 감: 회차 상한 아래
        Prewarmer p = Direct(50);

        Run(p);

        Assert.Contains(_log.Lines, l => l.Contains("ended after 11 rounds", StringComparison.Ordinal) && l.EndsWith("(disk read cap reached)", StringComparison.Ordinal)); // 10 회차까지 31.25 MiB, 11 회차에서 32 MiB 초과
        Assert.Equal((10 * 100) + 25, _reads.Count); // 11 회차는 열세째 디스크 블록 (25 번째 읽기) 에서 32 MiB 를 넘어 멈춤
    }

    [Fact]
    public void BelowTheHoldFloorOfAvailableMemoryARoundIsSkippedAndAReaderIsNotEvenOpened()
    {
        _rows = Rows(FileA, 8);
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
    public void AFileThatCannotBeOpenedIsSkippedInTheRoundAndTheOthersAreStillReadWithoutCuttingTheList()
    {
        _rows = [.. Rows(FileB, 8), .. Rows(FileA, 8)];
        _seconds = (_, path) => path == FileB ? null : 0.0001;
        Prewarmer p = Direct(1);

        Run(p);

        Assert.Equal(16, _reads.Count);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("boundary", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("1 MiB left in the list", StringComparison.Ordinal)); // 열지 못한 파일도 목록에서 빼지 않음 (다음 회차에 다시 시도)
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
    public void TheKeepRunsInASideThreadOfTheGameKeepingStartsRightAfterTheStartTouchAndEndsWithTheGame()
    {
        _rows = Rows(FileA, 32);
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
