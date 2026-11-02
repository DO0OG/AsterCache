using AsterCache.Prefetch;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Prefetch;

/// 가짜 파일 접근: 크기 표와 읽기 기록. Broken 에 든 경로는 열기에서 실패합니다.
internal sealed class FakeReadIo : IReadAheadIo
{
    private readonly object _lock = new();
    private long _bytes;

    public Dictionary<string, long> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Broken { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<(string Path, long Offset, int Count)> Reads { get; } = [];

    public int Attempts { get; private set; }

    public bool Disposed { get; private set; }

    /// 읽기 한가운데서 부르는 갈고리 (시험이 "요청이 진행되는 동안 게임이 읽음" 을 흉내냄).
    public Action? DuringRead { get; set; }

    public int Read(string path, long offset, int count)
    {
        DuringRead?.Invoke();
        lock (_lock)
        {
            Attempts++;
            if (Broken.Contains(path))
            {
                throw new IOException("sharing violation");
            }

            Reads.Add((path, offset, count));
            int got = (int)Math.Max(0, Math.Min(count, Sizes[path] - offset));
            _bytes += got;
            return got;
        }
    }

    /// 실제로 읽어 준 바이트 합 (파일 끝에서는 요청보다 작음).
    public long ReadBytes
    {
        get
        {
            lock (_lock)
            {
                return _bytes;
            }
        }
    }

    public int ReadCount
    {
        get
        {
            lock (_lock)
            {
                return Reads.Count;
            }
        }
    }

    public void Dispose() => Disposed = true;
}

public sealed class ReadAheadTests
{
    private const long Mib = 1L << 20;
    private const string A = @"D:\Games\Foo\a.bin";
    private const string B = @"D:\Games\Foo\sub\b.bin";

    private readonly FakeReadIo _io = new();
    private long _availableMib = 1 << 20;
    private long _nowMs;

    // 기존 시험은 틈 모드의 규칙을 보므로 stream 은 기본 false.
    private ReadAhead Make(long windowMib = 4, long maxMib = 1024, long minFreeMib = 100, long gapMs = 100, int seqRuns = 6, bool stream = false, bool bounded = false, bool adaptive = false, int saturationMbps = 80) =>
        new(new ReadAheadOptions([@"D:\Games\Foo"], windowMib * Mib, maxMib * Mib, minFreeMib, () => Volatile.Read(ref _availableMib), () => Volatile.Read(ref _nowMs), gapMs, seqRuns, stream, bounded, adaptive, saturationMbps), _io);

    private static void Drain(ReadAhead r)
    {
        for (int i = 0; i < 100000 && r.Step(); i++)
        {
        }
    }

    /// 게임이 같은 파일을 length 씩 이어서 count 번 읽었다는 알림을 보냅니다 (읽기마다 everyMs 가 흐름). 다음 읽기 위치를 돌려줍니다.
    /// 기본 (1 MiB × 8) 은 순차 판정의 최소 패턴: 증가 읽기 연속 7 번 (≥ 6) 에 누적 8 MiB.
    private long Stream(ReadAhead r, string path, long from, long length = Mib, int count = 8, long everyMs = 10)
    {
        for (int i = 0; i < count; i++)
        {
            r.OnRead(path, from + (i * length), length);
            _nowMs += everyMs;
        }

        return from + (count * length);
    }

    /// 게임이 조용한 틈 (기본 설정의 gap_ms) 만큼 시간이 흐름.
    private void Quiet(long ms = ReadAhead.DefaultGapMs) => _nowMs += ms;

    [Fact]
    public void WindowStartsAfterTheJudgementAndSkipsWhatTheGameJustRead()
    {
        _io.Sizes[A] = 100 * Mib;
        ReadAhead r = Make(windowMib: 4);

        Stream(r, A, 0);
        Quiet();
        Drain(r);

        Assert.Equal(8 * Mib, _io.Reads[0].Offset);
        Assert.All(_io.Reads, x => Assert.True(x.Count <= Mib));
        Assert.Equal(3 * Mib, _io.Reads.Sum(x => (long)x.Count)); // 창 [7, 11) MiB 에서 게임이 읽은 [7, 8) 을 뺌
        Assert.Equal(_io.Reads.OrderBy(x => x.Offset), _io.Reads);
        Assert.Equal(3 * Mib, r.Stats.ReadAheadBytes);
        Assert.Equal(8 * Mib, r.Stats.GameReadBytes);
        Assert.Equal(1, r.Stats.SequentialFiles);
    }

    [Fact]
    public void LooseSequentialOfFourBlocksIsNeverJudged()
    {
        _io.Sizes[A] = 1000 * Mib;
        ReadAhead r = Make();

        for (int group = 0; group < 20; group++)
        {
            Stream(r, A, group * 100 * Mib, 2 * Mib, count: 4); // 4 블록 순차 (8 MiB) 뒤 큰 간격으로 건너뜀
            Quiet();
            Drain(r);
        }

        Assert.Empty(_io.Reads);
        Assert.Equal(0, r.Stats.SequentialFiles);
    }

    [Fact]
    public void FewerThanSixRunsOrUnderEightMibIsNotEnough()
    {
        _io.Sizes[A] = 1000 * Mib;
        _io.Sizes[B] = 1000 * Mib;
        ReadAhead r = Make();

        long next = Stream(r, A, 0, Mib, count: 7); // 연속 6 번이지만 누적 7 MiB
        Quiet();
        Drain(r);
        Assert.Empty(_io.Reads);
        Stream(r, B, 0, 4 * Mib, count: 6); // 누적 24 MiB 지만 연속 5 번
        Quiet();
        Drain(r);
        Assert.Empty(_io.Reads);
        Assert.Equal(0, r.Stats.SequentialFiles);

        r.OnRead(A, next, Mib); // 누적 8 MiB: 판정
        Quiet();
        Drain(r);
        Assert.NotEmpty(_io.Reads);
        Assert.Equal(1, r.Stats.SequentialFiles);

        r.OnRead(B, 24 * Mib, 4 * Mib); // 연속 6 번: 판정
        Assert.Equal(2, r.Stats.SequentialFiles);
    }

    [Fact]
    public void SeqRunsOptionChangesTheRunsNeeded()
    {
        _io.Sizes[A] = 100 * Mib;
        ReadAhead r = Make(windowMib: 8, seqRuns: 2);

        Stream(r, A, 0, 4 * Mib, count: 3); // 연속 2 번, 누적 12 MiB
        Quiet();
        Drain(r);

        Assert.Equal(1, r.Stats.SequentialFiles);
        Assert.NotEmpty(_io.Reads);
    }

    [Fact]
    public void OverlappingWindowsNeverReadTheSameBytesTwice()
    {
        _io.Sizes[A] = 100 * Mib;
        ReadAhead r = Make(windowMib: 4);

        long next = Stream(r, A, 0);
        Quiet();
        Drain(r); // 창 [7, 11) → 선읽기 [8, 11)
        r.OnRead(A, next, Mib); // 이미 선읽기한 안쪽을 읽음: 새 창 [8, 12) 중 새로운 곳 [11, 12) 만 큐에 들어감
        Quiet();
        Drain(r);

        Assert.Equal(4 * Mib, _io.Reads.Sum(x => (long)x.Count));
        Assert.Equal(_io.Reads.Count, _io.Reads.Select(x => x.Offset).Distinct().Count());
        Assert.Equal(11 * Mib, _io.Reads[^1].Offset);
        Assert.Equal(Mib, r.Stats.HitBytes);
    }

    [Fact]
    public void OpenedFilesAreCountedButNeverReadWhole()
    {
        _io.Sizes[A] = 3 * Mib;
        _io.Sizes[B] = 3 * Mib;
        ReadAhead r = Make();

        r.OnOpen(A);
        r.OnOpen(B);
        r.OnOpen(@"D:\Other\x.bin"); // 허용 루트 밖
        Quiet();
        Drain(r);

        Assert.Empty(_io.Reads);
        Assert.Equal(0, _io.Attempts);
        Assert.Equal(2, r.Stats.Events);
    }

    [Fact]
    public void OneBackwardOrBigGapReadClearsTheQueueButTwoInARowReleaseTheJudgement()
    {
        _io.Sizes[A] = 1000 * Mib;
        ReadAhead r = Make(windowMib: 4);
        Stream(r, A, 0); // 판정됨, 큐 [8, 11)

        r.OnRead(A, 500 * Mib, Mib); // 큰 간격 1 번: 큐를 비움
        Quiet();
        Drain(r);
        Assert.Empty(_io.Reads);

        r.OnRead(A, 501 * Mib, Mib); // 이어 읽으면 1 번 끊긴 것은 용서: 창이 다시 큐에 들어감
        Quiet();
        Drain(r);
        Assert.Equal(502 * Mib, _io.Reads[0].Offset);
        int before = _io.ReadCount;

        r.OnRead(A, 0, Mib); // 역행 1 번
        r.OnRead(A, 100 * Mib, Mib); // 큰 간격 2 번 연속: 판정이 풀림
        Quiet();
        Drain(r);
        Assert.Equal(before, _io.ReadCount);

        for (int i = 1; i <= 5; i++)
        {
            r.OnRead(A, (100 + i) * Mib, Mib); // 증가 읽기 연속 5 번: 아직 부족
        }

        Quiet();
        Drain(r);
        Assert.Equal(before, _io.ReadCount);

        r.OnRead(A, 106 * Mib, Mib); // 연속 6 번: 다시 판정
        Quiet();
        Drain(r);
        Assert.True(_io.ReadCount > before);
        Assert.Equal(107 * Mib, _io.Reads[before].Offset);
        Assert.Equal(1, r.Stats.SequentialFiles); // 판정된 적이 있는 파일 수는 그대로
    }

    [Fact]
    public void RandomReadsOverManyFilesNeverReadAhead()
    {
        for (int i = 0; i < 64; i++)
        {
            _io.Sizes[$@"D:\Games\Foo\f{i}.bin"] = 100 * Mib;
        }

        ReadAhead r = Make();
        var rng = new Random(7);
        var steps = new int[64];
        for (int n = 0; n < 4000; n++)
        {
            int f = rng.Next(64);
            long offset = (steps[f]++ * 11 % 97) * Mib; // 파일마다 +11 MiB 또는 되돌아감: 간격이 4 MiB 보다 크거나 역행
            r.OnRead($@"D:\Games\Foo\f{f}.bin", offset, 4096 * rng.Next(1, 16));
            Quiet(); // 틈이 있어도
            Drain(r);
        }

        Assert.Empty(_io.Reads);
        Assert.Equal(0, r.Stats.ReadAheadBytes);
        Assert.Equal(0, r.Stats.SequentialFiles);
    }

    [Fact]
    public void LateReadsDriveTheJudgementButNeverCountAsGameReadsOrHits()
    {
        _io.Sizes[A] = 100 * Mib;
        ReadAhead r = Make(windowMib: 4);

        for (int i = 0; i < 8; i++)
        {
            r.OnLateRead(A, i * Mib, Mib);
        }

        Quiet();
        Drain(r);
        r.OnLateRead(A, 8 * Mib, Mib); // 우리가 이미 읽은 곳이라도 늦은 전달은 적중이 아님

        ReadAheadStats st = r.Stats;
        Assert.Equal(9, st.LateReads);
        Assert.Equal(0, st.ResolvedReads);
        Assert.Equal(0, st.GameReadBytes);
        Assert.Equal(0, st.HitBytes);
        Assert.True(st.ReadAheadBytes > 0);
        Assert.Equal(1, st.SequentialFiles);
    }

    [Fact]
    public void MostRecentlyTouchedFileGoesFirst()
    {
        _io.Sizes[A] = 100 * Mib;
        _io.Sizes[B] = 100 * Mib;
        ReadAhead r = Make();

        long a = Stream(r, A, 0);
        Stream(r, B, 0);
        Quiet();
        Assert.True(r.Step());
        Assert.Equal(B, _io.Reads[^1].Path);

        r.OnRead(A, a, 4096); // A 가 다시 가장 최근 (이어서 읽으므로 순차 유지)
        Quiet();
        Assert.True(r.Step());
        Assert.Equal(A, _io.Reads[^1].Path);
        Assert.Equal(a + 4096, _io.Reads[^1].Offset); // A 안에서는 가장 앞 오프셋
    }

    [Fact]
    public void WithinAFileTheLowestOffsetGoesFirst()
    {
        _io.Sizes[A] = 200 * Mib;
        ReadAhead r = Make(windowMib: 4);

        long next = Stream(r, A, 10 * Mib);
        r.OnRead(A, next + (2 * Mib), 4096); // 간격 2 MiB 는 4 MiB 이하라 순차 유지, 큐에는 [next, next + 2 MiB) 와 그 뒤가 남음
        Quiet();
        Assert.True(r.Step());

        Assert.Equal(next, _io.Reads[0].Offset);
    }

    [Fact]
    public void TotalCapStopsReadingExactly()
    {
        _io.Sizes[A] = 1000 * Mib;
        ReadAhead r = Make(windowMib: 128, maxMib: 3);

        Stream(r, A, 0);
        Quiet();
        Drain(r);

        Assert.Equal(3 * Mib, r.Stats.ReadAheadBytes);
        Assert.False(r.Step());
    }

    [Fact]
    public void ReadAheadTotalIsCappedByOnePointFiveTimesTheGameReadsPlus64Mib()
    {
        _io.Sizes[A] = 2000 * Mib;
        ReadAhead r = Make(windowMib: 512, maxMib: 1024);

        long next = Stream(r, A, 0); // 게임 읽기 8 MiB
        Quiet();
        Drain(r);
        Assert.Equal((8 * Mib * 3 / 2) + (64 * Mib), r.Stats.ReadAheadBytes); // 76 MiB
        Assert.False(r.Step());

        r.OnRead(A, next, 10 * Mib); // 게임 읽기 18 MiB: 상한이 91 MiB 로 늘어남 (이미 읽은 곳이라 새로 큐에 넣지 않음)
        Quiet();
        Drain(r);
        Assert.Equal((18 * Mib * 3 / 2) + (64 * Mib), r.Stats.ReadAheadBytes);
    }

    [Fact]
    public void LowAvailableRamPausesUntilItRecovers()
    {
        _io.Sizes[A] = 100 * Mib;
        ReadAhead r = Make(windowMib: 4, minFreeMib: 1000);
        _availableMib = 1000; // 1 MiB 를 읽으면 999 로 내려감
        Stream(r, A, 0);
        Quiet();

        Assert.False(r.Step());
        Assert.Empty(_io.Reads);

        _availableMib = 5000;
        Drain(r);
        Assert.Equal(3 * Mib, r.Stats.ReadAheadBytes);
    }

    [Fact]
    public void FilesOutsideTheAllowedRootAreIgnored()
    {
        _io.Sizes[A] = Mib;
        ReadAhead r = Make();

        r.OnRead(@"E:\Other\x.bin", 0, 4096);
        r.OnRead(@"D:\Games\Foo2\x.bin", 0, 4096); // 접두만 같고 폴더 경계가 다름
        r.OnOpen(@"D:\Games\Foo");
        r.OnRead(@"d:\games\foo\A.BIN", 0, 4096); // 대소문자 무시
        Quiet();
        Drain(r);

        Assert.Equal(1, r.Stats.Events);
        Assert.Equal(4096, r.Stats.GameReadBytes);
    }

    [Fact]
    public void FileThatFailsToOpenIsTriedOnlyOnceAndOthersKeepWorking()
    {
        _io.Sizes[A] = 100 * Mib;
        _io.Sizes[B] = 100 * Mib;
        _io.Broken.Add(A);
        ReadAhead r = Make();

        long next = Stream(r, A, 0);
        Quiet();
        Drain(r);
        Stream(r, A, next);
        Stream(r, B, 0);
        Quiet();
        Drain(r);

        Assert.Equal(1, _io.Attempts - _io.ReadCount); // A 읽기 시도 1 번
        Assert.Equal(1, r.Stats.FailedFiles);
        Assert.All(_io.Reads, x => Assert.Equal(B, x.Path));
        Assert.True(r.Stats.ReadAheadBytes > 0);
    }

    [Fact]
    public void HitEstimateCountsOnlyBytesWeFinishedBeforeTheGameRead()
    {
        _io.Sizes[A] = 100 * Mib;
        ReadAhead r = Make(windowMib: 8);

        Stream(r, A, 0); // 판정: 창 [7, 15) 중 [8, 15) 선읽기
        Quiet();
        Drain(r);
        r.OnRead(A, 8 * Mib, Mib); // 적중 1 MiB
        r.OnRead(A, 20 * Mib, Mib); // 아직 안 읽은 곳 (간격이 커서 무작위): 적중 아님
        r.OnRead(A, 21 * Mib, Mib); // 선행 읽기 대상이 아님: 적중 아님
        r.OnRead(A, 14 * Mib, 2 * Mib); // [14, 15) 만 적중

        ReadAheadStats st = r.Stats;
        Assert.Equal(13 * Mib, st.GameReadBytes);
        Assert.Equal(2 * Mib, st.HitBytes);
        Assert.Equal(2.0 / 13, st.HitRatio, 6);
        Assert.Equal(12, st.ResolvedReads);
    }

    [Fact]
    public void ReadsPastTheEndOfFileStopTheQueueForThatFile()
    {
        _io.Sizes[A] = (19 * Mib) / 2; // 9.5 MiB
        ReadAhead r = Make(windowMib: 16);

        Stream(r, A, 0);
        Quiet();
        Drain(r);

        Assert.Equal((3 * Mib) / 2, r.Stats.ReadAheadBytes); // [8, 9.5)
        Assert.Equal(2, _io.ReadCount);
    }

    [Fact]
    public void ContinuousReadsWithoutGapsNeverReadAhead()
    {
        _io.Sizes[A] = 1000 * Mib;
        ReadAhead r = Make(windowMib: 64);

        for (int i = 0; i < 200; i++)
        {
            r.OnRead(A, i * Mib, Mib);
            _nowMs += 99; // 이벤트 간격이 gap_ms (100) 보다 작음
            Drain(r);
        }

        ReadAheadStats st = r.Stats;
        Assert.Equal(1, st.SequentialFiles); // 순차 판정은 됐지만
        Assert.Empty(_io.Reads); // 틈이 없어 선행 읽기 0
        Assert.Equal(0, st.ReadAheadBytes);
        Assert.Equal((0L, 0L, 0L), (st.GapCount, st.YieldedRequests, st.ReadAheadWhileIdleBytes));
    }

    [Fact]
    public void GapOfExactlyGapMsAllowsReadAheadButOneMillisecondLessDoesNot()
    {
        _io.Sizes[A] = 100 * Mib;
        ReadAhead r = Make();

        Stream(r, A, 0, everyMs: 0);
        _nowMs += 99;
        Assert.False(r.Step());

        _nowMs += 1;
        Assert.True(r.Step());
    }

    [Fact]
    public void ReadsSeparatedByGapsReadAheadInEveryGapAndAGameReadStopsIt()
    {
        _io.Sizes[A] = 1000 * Mib;
        ReadAhead r = Make(windowMib: 128);
        long next = Stream(r, A, 0);

        for (int gap = 1; gap <= 3; gap++)
        {
            int before = _io.ReadCount;
            _nowMs += 150; // 읽기 사이 150 ms 틈
            Assert.True(r.Step());
            Assert.True(r.Step());
            Assert.True(_io.ReadCount >= before + 2); // 틈마다 선행 읽기가 일어남
            r.OnRead(A, next, Mib); // 게임 읽기: 멈춤
            next += Mib;
            _nowMs += 10;
            Assert.False(r.Step());
            Assert.Equal(before + 2, _io.ReadCount);
            Assert.Equal(gap, r.Stats.GapCount);
            Assert.Equal(gap, r.Stats.YieldedRequests);
        }

        ReadAheadStats st = r.Stats;
        Assert.Equal(6 * Mib, st.ReadAheadBytes);
        Assert.Equal(6 * Mib, st.ReadAheadWhileIdleBytes);
    }

    [Fact]
    public void GameReadDuringARequestLetsThatRequestFinishThenStops()
    {
        _io.Sizes[A] = 1000 * Mib;
        ReadAhead r = Make(windowMib: 128);
        long next = Stream(r, A, 0);
        bool once = false;
        _io.DuringRead = () =>
        {
            if (!once)
            {
                once = true;
                r.OnRead(A, next, Mib); // 요청이 진행되는 동안 게임이 읽음
            }
        };

        Quiet();
        Assert.True(r.Step()); // 진행 중이던 요청 하나는 끝까지 읽음
        _nowMs += 10;
        Assert.False(r.Step()); // 그다음은 읽지 않음
        Assert.Equal(1, _io.ReadCount);

        ReadAheadStats st = r.Stats;
        Assert.Equal(Mib, st.ReadAheadBytes);
        Assert.Equal(0, st.ReadAheadWhileIdleBytes); // 게임 읽기와 겹친 요청은 틈 읽기로 세지 않음
        Assert.Equal((1L, 1L), (st.GapCount, st.YieldedRequests));
    }

    [Fact]
    public void StreamModeReadsAheadWithoutWaitingForAGapInSixteenMibRequests()
    {
        _io.Sizes[A] = 1000 * Mib;
        ReadAhead r = Make(windowMib: 64, stream: true);

        Stream(r, A, 0, everyMs: 1); // 읽기 사이에 틈이 전혀 없음 (마지막 읽기 바로 뒤)
        Drain(r);

        Assert.Equal([8 * Mib, 24 * Mib, 40 * Mib, 56 * Mib], _io.Reads.Select(x => (long)x.Offset)); // 창 [7, 71) MiB 에서 게임이 읽은 [7, 8) 을 뺌
        Assert.Equal([16 * Mib, 16 * Mib, 16 * Mib, 15 * Mib], _io.Reads.Select(x => (long)x.Count));
        ReadAheadStats st = r.Stats;
        Assert.Equal(63 * Mib, st.ReadAheadBytes);
        Assert.Equal((0L, 0L), (st.GapCount, st.YieldedRequests)); // 틈이라는 개념이 없음
    }

    [Fact]
    public void StreamModeNeverStopsForGameReadsAndKeepsTheWholeQueue()
    {
        _io.Sizes[A] = 1000 * Mib;
        ReadAhead r = Make(windowMib: 64, stream: true);
        long next = Stream(r, A, 0);
        bool once = false;
        _io.DuringRead = () =>
        {
            if (!once)
            {
                once = true;
                r.OnRead(A, next, Mib); // 요청이 진행되는 동안 게임이 읽음
            }
        };

        Assert.True(r.Step());
        Assert.True(r.Step()); // 게임 읽기가 왔어도 다음 요청을 바로 냄
        Assert.True(r.Step());
        Assert.Equal(3, _io.ReadCount);
        Assert.Equal((0L, 0L), (r.Stats.GapCount, r.Stats.YieldedRequests));
        Assert.Equal(32 * Mib, r.Stats.ReadAheadWhileIdleBytes); // 첫 요청만 게임 읽기와 겹침
    }

    [Fact]
    public void StreamModeIsStillBoundedByTheCapAndTheWindow()
    {
        _io.Sizes[A] = 2000 * Mib;
        ReadAhead r = Make(windowMib: 512, maxMib: 1024, stream: true);

        Stream(r, A, 0); // 게임 읽기 8 MiB
        Drain(r);

        Assert.Equal((8 * Mib * 3 / 2) + (64 * Mib), r.Stats.ReadAheadBytes); // 76 MiB: 게임 읽기 × 1.5 + 64 MiB
        Assert.All(_io.Reads, x => Assert.True(x.Offset >= 8 * Mib && x.Offset + x.Count <= (7 + 512) * Mib)); // 현재 위치 앞쪽 창 안
        Assert.False(r.Step());

        ReadAhead small = new(new ReadAheadOptions([@"D:\Games\Foo"], 4 * Mib, 1024 * Mib, 100, () => Volatile.Read(ref _availableMib), () => Volatile.Read(ref _nowMs)), _io); // Stream 기본값 = true
        _io.Reads.Clear();
        Stream(small, B, 0);
        _io.Sizes[B] = 1000 * Mib;
        Drain(small);
        Assert.Equal(3 * Mib, _io.Reads.Sum(x => (long)x.Count)); // 창 4 MiB: [8, 11)
    }

    [Fact]
    public void BoundedModeWaitsFiftyMillisecondsAfterAGameReadThenReadsInSixteenMibRequestsUpToGameReadsPlus32Mib()
    {
        _io.Sizes[A] = 1000 * Mib;
        ReadAhead r = Make(windowMib: 64, stream: true, bounded: true);

        Stream(r, A, 0, everyMs: 1); // 마지막 읽기 1 ms 뒤
        Assert.False(r.Step());
        _nowMs += 48; // 마지막 읽기 49 ms 뒤: 아직 쉼
        Assert.False(r.Step());
        Assert.Equal(0, _io.ReadCount);
        _nowMs += 1; // 정확히 50 ms
        Drain(r);

        Assert.Equal([8 * Mib, 24 * Mib, 40 * Mib], _io.Reads.Select(x => (long)x.Offset));
        Assert.Equal([16 * Mib, 16 * Mib, 8 * Mib], _io.Reads.Select(x => (long)x.Count));
        ReadAheadStats st = r.Stats;
        Assert.Equal((8 * Mib) + (32 * Mib), st.ReadAheadBytes); // 게임 읽기 x 1.0 + 32 MiB (스트림 모드는 76 MiB)
        Assert.Equal((1L, 0L), (st.GapCount, st.YieldedRequests));
        Assert.False(r.Step());
    }

    [Fact]
    public void BoundedModeCapGrowsWithGameReadsAndStaysWithinMaxTotal()
    {
        _io.Sizes[A] = 1000 * Mib;
        ReadAhead r = Make(windowMib: 512, maxMib: 50, stream: true, bounded: true);
        long next = Stream(r, A, 0);
        Quiet(ReadAhead.BoundedQuietMs);
        Drain(r);
        Assert.Equal(40 * Mib, r.Stats.ReadAheadBytes);

        Stream(r, A, next + (64 * Mib), 1 * Mib, count: 4); // 같은 파일에서 큰 간격을 건너뛰어 읽음: 순차가 풀림 (게임 읽기 +4 MiB)
        Quiet(ReadAhead.BoundedQuietMs);
        Drain(r);

        Assert.Equal(44 * Mib, r.Stats.ReadAheadBytes); // 게임 읽기 12 MiB + 32 MiB (max_total 50 MiB 이내)
    }

    [Fact]
    public void BoundedModeLetsTheRequestInFlightFinishAndThenWaitsAgain()
    {
        _io.Sizes[A] = 1000 * Mib;
        ReadAhead r = Make(windowMib: 64, stream: true, bounded: true);
        long next = Stream(r, A, 0, everyMs: 1);
        _nowMs += ReadAhead.BoundedQuietMs;
        bool once = false;
        _io.DuringRead = () =>
        {
            if (!once)
            {
                once = true;
                r.OnRead(A, next, Mib); // 요청이 진행되는 동안 게임이 읽음
                _nowMs += 1;
            }
        };

        Assert.True(r.Step()); // 진행 중이던 요청은 끝남
        Assert.Equal(1, _io.ReadCount);
        Assert.False(r.Step()); // 새 요청은 게임 읽기 뒤 50 ms 동안 내지 않음
        Assert.Equal(1, _io.ReadCount);
        _nowMs += ReadAhead.BoundedQuietMs - 2; // 게임 읽기 뒤 49 ms
        Assert.False(r.Step());
        _nowMs += 1; // 50 ms
        Assert.True(r.Step());
        Assert.Equal(2, _io.ReadCount);
    }

    [Fact]
    public void BoundedModeReadsAheadOnlyInFilesJudgedSequential()
    {
        _io.Sizes[A] = 1000 * Mib;
        ReadAhead r = Make(windowMib: 64, stream: true, bounded: true);
        for (int i = 0; i < 12; i++)
        {
            r.OnRead(A, i * 100 * Mib % (900 * Mib), Mib); // 큰 간격으로 건너뛰는 읽기
            _nowMs += 10;
        }

        Quiet(ReadAhead.BoundedQuietMs);
        Drain(r);

        Assert.Equal(0, _io.ReadCount);
        Assert.Equal(0, r.Stats.SequentialFiles);
    }

    [Fact]
    public void AdaptiveModeIssuesNothingWhileTheGameReadsAt100MibPerSecondAndCountsTheRest()
    {
        _io.Sizes[A] = 2000 * Mib;
        ReadAhead r = Make(windowMib: 512, stream: true, adaptive: true);
        for (int i = 0; i < 12; i++)
        {
            r.OnRead(A, i * 8 * Mib, 8 * Mib); // 8 MiB 마다 60 ms = 133 MiB/s 연속
            _nowMs += 60; // 게임 읽기 뒤 50 ms 규칙은 넘김: 막는 것은 포화뿐
            Assert.False(r.Step());
        }

        Assert.Equal(0, _io.ReadCount);
        Assert.Equal(300, r.Stats.SaturatedMs); // 순차 판정 뒤 첫 확인 (420 ms) 부터 720 ms 까지
        Assert.Equal(0, r.Stats.PausedRequests);
    }

    [Fact]
    public void AdaptiveModeReadsAheadBelowTheThresholdKeepsTheBoundedCapAndResumesWhenTheStreamCalmsDown()
    {
        _io.Sizes[A] = 2000 * Mib;
        ReadAhead r = Make(windowMib: 512, stream: true, adaptive: true);
        for (int i = 0; i < 12; i++)
        {
            r.OnRead(A, i * 3 * Mib, 3 * Mib); // 3 MiB 마다 60 ms = 50 MiB/s
            _nowMs += 60;
            r.Step();
        }

        Assert.True(_io.ReadCount > 0);
        Assert.Equal(0, r.Stats.SaturatedMs);
        Drain(r);
        Assert.Equal((36 * Mib) + (32 * Mib), r.Stats.ReadAheadBytes); // bounded 규칙: 게임 읽기 x 1.0 + 32 MiB

        _nowMs = 0; // 새 엔진과 새 시간축
        ReadAhead busy = Make(windowMib: 512, stream: true, adaptive: true);
        _io.Sizes[B] = 2000 * Mib;
        _io.Reads.Clear();
        for (int i = 0; i < 12; i++)
        {
            busy.OnRead(B, i * 8 * Mib, 8 * Mib); // 133 MiB/s
            _nowMs += 60;
        }

        Assert.False(busy.Step());
        _nowMs += 919 - 720; // 지금 919 ms: 창 (419, 919] 에 읽기 다섯 개 = 40 MiB = 정확히 80 MiB/s
        Assert.False(busy.Step());
        _nowMs += 1; // 920 ms: 가장 오래된 읽기 (420 ms) 가 창 밖으로 나가 32 MiB = 64 MiB/s: 재개
        Assert.True(busy.Step());
        Assert.Equal(1, _io.ReadCount);
    }

    [Theory]
    [InlineData(0, true)] // 정확히 80 MiB/s (500 ms 에 40 MiB): 기준 이상이라 쉼
    [InlineData(1, true)]
    [InlineData(-1, false)] // 1 바이트 모자람: 선행 함
    public void AdaptiveModeThresholdBoundaryIsInclusive(int deltaBytes, bool saturated)
    {
        _io.Sizes[A] = 2000 * Mib;
        ReadAhead r = Make(windowMib: 512, stream: true, adaptive: true);
        Stream(r, A, 0, everyMs: 1); // 8 MiB, 순차 판정 (t = 0..7 ms)
        r.OnRead(@"D:\Games\Other\x.bin", 0, (32 * Mib) + deltaBytes); // 루트 밖 읽기도 디스크 처리량에 듦: 합 40 MiB 정확히 (deltaBytes = 0)
        _nowMs += ReadAhead.BoundedQuietMs; // 게임 읽기 뒤 50 ms

        Assert.Equal(!saturated, r.Step());
    }

    [Fact]
    public void AdaptiveModePausesTwoHundredMillisecondsWhenOurRequestLatencyDoublesTheLastWindowAverage()
    {
        _io.Sizes[A] = 2000 * Mib;
        ReadAhead r = Make(windowMib: 512, stream: true, adaptive: true, saturationMbps: 10000);
        Stream(r, A, 0, count: 32, everyMs: 1); // 게임 읽기 32 MiB: 상한 64 MiB = 16 MiB 요청 넷
        _nowMs += ReadAhead.BoundedQuietMs;
        long latency = 100;
        _io.DuringRead = () => _nowMs += latency;

        Assert.True(r.Step()); // 지연 100 ms: 비교할 직전 창이 없음
        Assert.True(r.Step()); // 100 ms: 평균 100 의 2 배 미만
        latency = 199;
        Assert.True(r.Step()); // 199 ms < 200: 쉬지 않음
        Assert.Equal(0, r.Stats.PausedRequests);
        Assert.True(r.Step()); // 199 ms, 평균 (100 + 100 + 199) / 3 = 133 의 2 배 (266) 미만
        Assert.Equal(0, r.Stats.PausedRequests);

        // 같은 흐름을 새 엔진으로 다시: 마지막 요청만 평균의 2 배 이상
        ReadAhead s = Make(windowMib: 512, stream: true, adaptive: true, saturationMbps: 10000);
        _io.Sizes[B] = 2000 * Mib;
        Stream(s, B, 0, count: 32, everyMs: 1);
        _nowMs += ReadAhead.BoundedQuietMs;
        latency = 100;
        Assert.True(s.Step());
        Assert.True(s.Step());
        latency = 200; // 정확히 2 배
        Assert.True(s.Step());
        Assert.Equal(1, s.Stats.PausedRequests);
        _io.DuringRead = null;
        Assert.False(s.Step()); // 200 ms 쉼
        _nowMs += ReadAhead.ContentionPauseMs - 1;
        Assert.False(s.Step());
        _nowMs += 1;
        Assert.True(s.Step());
        Assert.Equal(1, s.Stats.PausedRequests);
    }

    [Fact]
    public void NonAdaptiveModesNeverCountSaturationOrPauses()
    {
        _io.Sizes[A] = 2000 * Mib;
        ReadAhead r = Make(windowMib: 512, stream: true, bounded: true);
        for (int i = 0; i < 12; i++)
        {
            r.OnRead(A, i * 8 * Mib, 8 * Mib);
            _nowMs += 60;
        }

        Drain(r);
        Assert.True(_io.ReadCount > 0);
        Assert.Equal((0L, 0L), (r.Stats.SaturatedMs, r.Stats.PausedRequests));
    }

    [Fact]
    public void StreamModeStopsAtTheEndOfTheFileAndAtLowRam()
    {
        _io.Sizes[A] = 20 * Mib;
        ReadAhead r = Make(windowMib: 512, stream: true);
        Stream(r, A, 0);
        Drain(r);
        Assert.Equal(12 * Mib, r.Stats.ReadAheadBytes); // [8, 20)
        Assert.Equal(1, _io.ReadCount);

        _io.Sizes[B] = 1000 * Mib;
        Volatile.Write(ref _availableMib, 100 + 15); // 16 MiB 요청에는 모자람
        Stream(r, B, 0);
        Assert.False(r.Step());
    }

    [Fact]
    public void ReadsOutsideTheRootStillCountAsGameActivity()
    {
        _io.Sizes[A] = 100 * Mib;
        ReadAhead r = Make();
        Stream(r, A, 0);
        Quiet();

        r.OnRead(@"E:\Other\x.bin", 0, 4096);

        Assert.False(r.Step());
        Assert.Equal(8, r.Stats.Events); // 루트 밖 읽기는 세지 않음
    }

    [Fact]
    public void ResolveMaxBytesUsesAQuarterOfRamCappedAt4GiBWhenAuto()
    {
        Assert.Equal(2048 * Mib, ReadAhead.ResolveMaxBytes(0, 8L << 30));
        Assert.Equal(4096 * Mib, ReadAhead.ResolveMaxBytes(0, 64L << 30));
        Assert.Equal(500 * Mib, ReadAhead.ResolveMaxBytes(500, 8L << 30));
    }

    [Fact]
    public void WorkerThreadReadsInTheBackgroundAfterAGapAndDisposeStopsIt()
    {
        _io.Sizes[A] = 10 * Mib;
        ReadAhead r = Make(windowMib: 4);
        r.Start();

        Stream(r, A, 0);
        DateTime until = DateTime.UtcNow.AddSeconds(10);
        while (r.Stats.ReadAheadBytes < 2 * Mib && DateTime.UtcNow < until)
        {
            Interlocked.Add(ref _nowMs, 50); // 주입 시계를 흘려 틈을 만듦
            Thread.Sleep(10);
        }

        r.Dispose();
        Assert.Equal(2 * Mib, r.Stats.ReadAheadBytes); // [8, 10) MiB (파일 끝)
        Assert.True(_io.Disposed);
        Assert.Null(r.Fault);
    }

    [Fact]
    public void RealFileReadingFollowsASequentialReaderFromStartToEnd()
    {
        using var dir = new TempDir();
        string path = dir.Combine("blob.bin");
        File.WriteAllBytes(path, new byte[24 * (int)Mib]);
        var engine = new ReadAhead(new ReadAheadOptions([dir.Path], 8 * Mib, 1024 * Mib, 0, () => 1 << 20, GapMs: 0, Stream: false), new FileReadAheadIo()); // 틈 규칙은 다른 시험에서 봄

        for (long off = 0; off < 24 * Mib; off += Mib)
        {
            engine.OnRead(path, off, Mib); // "게임" 이 1 MiB 씩 순차로 읽음 (읽기 사이에 선행 읽기가 앞서도록 한 걸음씩 돌림)
            for (int i = 0; i < 3; i++)
            {
                engine.Step();
            }
        }

        engine.Dispose();
        ReadAheadStats st = engine.Stats;
        Assert.True(st.ReadAheadBytes > 0);
        Assert.Equal(24 * Mib, st.GameReadBytes);
        Assert.True(st.HitRatio > 0.5, $"hit ratio {st.HitRatio}");
        Assert.Equal(0, st.FailedFiles);
        Assert.Equal(0, st.LowPriorityFailures); // 읽기 전용 일반 파일 핸들에는 낮은 우선순위 힌트가 붙음
    }

    [Fact]
    public void IntervalSetMergesRemovesAndFindsGaps()
    {
        var s = new IntervalSet();
        s.Add(10, 20);
        s.Add(20, 30); // 맞닿으면 합침
        s.Add(50, 60);
        s.Add(5, 12);

        Assert.Equal((5L, 30L), s.First);
        Assert.Equal(2, s.Count);
        Assert.Equal([(0L, 5L), (30L, 50L), (60L, 70L)], s.Gaps(0, 70));
        Assert.Equal(15, s.Overlap(0, 20));
        s.Remove(15, 55);
        Assert.Equal((5L, 15L), s.First);
        Assert.Equal(2, s.Count);
        Assert.Equal(15, s.Overlap(0, 100));
    }
}
