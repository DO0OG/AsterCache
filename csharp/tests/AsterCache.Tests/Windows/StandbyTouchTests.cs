using System.Diagnostics;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Trace;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 경계 판정: 이 프로세스의 디스크 읽기 수 (하드 폴트) 의 증가분으로 (가짜 누계), 안전망은 읽기가 확인된 느린 조각의 시간 합. 파일은 임시 폴더의 작은 파일 (캐시에 있음).
public sealed class StandbyTouchTests : IDisposable
{
    private const int Mib = 1 << 20;
    private const uint Archive = 0x20;
    private const uint Sparse = FileGuards.SparseAttribute;
    private readonly TempDir _dir = new();
    private readonly string _path;

    public StandbyTouchTests()
    {
        _ = _dir.WriteRandom("t.bin", 2 * Mib);
        _path = _dir.Combine("t.bin");
    }

    public void Dispose() => _dir.Dispose();

    // 이 프로세스의 디스크 읽기 수 가짜 누계: 첫 호출 (Run 이 버리는 조회) 은 그대로, 그다음 호출마다 deltas 를 차례로 더해 돌려줌 (deltas[0] 은 기준값을 잡는 호출이라 보통 0, 다 쓰면 그대로).
    private static Func<uint?> Counter(params uint[] deltas)
    {
        uint value = 1_000_000;
        int calls = 0;
        return () =>
        {
            value += calls > 0 && calls - 1 < deltas.Length ? deltas[calls - 1] : 0;
            calls++;
            return value;
        };
    }

    // 시스템 페이지 읽기 가짜 누계: 호출마다 deltas 를 차례로 더함 (첫 호출은 기준값).
    private static Func<uint?> PageCounter(params uint[] deltas)
    {
        uint value = 1_000_000;
        int calls = 0;
        return () =>
        {
            value += calls < deltas.Length ? deltas[calls] : 0;
            calls++;
            return value;
        };
    }

    // 조각 하나를 재는 두 번의 호출 (만지기 전, 후) 사이에 msOf(조각 번호) 가 흐르는 시계 (Stopwatch 틱).
    private static Func<long> Clock(Func<int, double> msOf)
    {
        long ticks = 0;
        int calls = 0;
        return () => ++calls % 2 == 0 ? ticks += (long)(Stopwatch.Frequency * msOf((calls / 2) - 1) / 1000.0) : ticks;
    }

    private List<ReadOp> Repeat(int ranges) => [.. Enumerable.Repeat(new ReadOp(_path, 0, 2 * Mib), ranges)]; // 2 MiB 범위 n 개 (조각 2n 개)

    private static TouchResult Go(IReadOnlyList<ReadOp> ranges, Func<uint?> faults, Func<uint?>? pages = null, Func<long>? clock = null, Func<string, uint>? attributes = null, CancellationToken ct = default) =>
        StandbyTouch.Run(ranges, ct, faults, pages ?? (() => 1_000_000), attributes ?? (_ => Archive), clock ?? Clock(_ => 0));

    [Fact]
    public void RangesThatDoNotRaiseTheHardFaultCountAreInMemoryAndNeverStopNoMatterHowManyPagesOtherProcessesRead()
    {
        uint pages = 0;
        TouchResult r = Go(Repeat(40), Counter(), () => pages += 100_000); // 다른 프로세스가 시스템 페이지 읽기를 크게 올려도 이 프로세스의 읽기는 0

        Assert.Equal(TouchEnd.Completed, r.End);
        Assert.Equal(40L * 2 * Mib, r.Bytes);
        Assert.Equal((0L, 0L, StopReason.None), (r.DiskBytes, r.HardFaults, r.Reason));
    }

    [Fact]
    public void ReadsUpToTheAllowanceInOneCheckPassAndMoreMarkTheBoundaryAndStopRightThere()
    {
        TouchResult within = Go(Repeat(40), Counter(0, StandbyTouch.BoundaryFaults), () => 1_000_000); // 64 MiB 에서 한 번, 끝에서 한 번 조회
        TouchResult over = Go(Repeat(40), Counter(0, StandbyTouch.BoundaryFaults + 1));

        Assert.Equal(TouchEnd.Completed, within.End);
        Assert.Equal(StandbyTouch.BoundaryFaults, within.HardFaults);
        Assert.Equal(TouchEnd.NeedsDiskReads, over.End);
        Assert.Equal(StopReason.HardFaults, over.Reason);
        Assert.Equal(StandbyTouch.FaultCheckBytes, over.Bytes); // 64 MiB 를 만진 뒤의 조회에서 멈춤
    }

    [Fact]
    public void TheAmountReadIsTheSmallerOfThePagesTheSystemReadAndTheFaultsTimesTheReadSizeAndIsZeroWithoutFaults()
    {
        var pages = PageCounter(0, 100); // 시스템 쪽 수 +100 쪽
        TouchResult r = Go(Repeat(40), Counter(0, 20), pages); // 읽기 20 회: 상한 20 x 64 KiB 보다 쪽 수 100 쪽 (400 KiB) 이 작음

        Assert.Equal(100L * 4096, r.DiskBytes);
        Assert.Equal(20, r.HardFaults);

        TouchResult capped = Go(Repeat(40), Counter(0, 2), PageCounter(0, 5000)); // 읽기 2 회, 시스템은 다른 프로세스 몫까지 크게 올랐음
        Assert.Equal(2 * StandbyTouch.FaultReadBytes, capped.DiskBytes);
    }

    [Fact]
    public void ASlowPieceBeforeTheNextRegularCheckBringsTheCheckForwardOnceEnoughWasTouched()
    {
        // 조각 4 번부터 느림 (디스크에서 읽는 구간, 30 ms): 앞의 조회 뒤 8 MiB 를 만졌고 만지는 데 100 ms 이상 걸린 조각 (7 번) 에서 바로 조회 → 읽기가 많아 멈춤
        TouchResult r = Go(Repeat(40), Counter(0, 500), clock: Clock(k => k >= 4 ? 30.0 : 0));

        Assert.Equal(TouchEnd.NeedsDiskReads, r.End);
        Assert.Equal(StopReason.HardFaults, r.Reason);
        Assert.Equal(8L * Mib, r.Bytes); // 64 MiB 까지 가지 않음
    }

    [Fact]
    public void TheBroughtForwardCheckWaitsUntilTheTouchTookAtLeastTheMinimumTimeSoBusyCpuDoesNotMakeThemFrequent()
    {
        // 모든 조각이 3 ms (의심 조각이지만 34 MiB 를 만져야 100 ms): 당겨진 조회가 34 MiB, 68 MiB 에서만 + 끝에서 한 번
        TouchResult slow6 = Go(Repeat(40), Counter(), clock: Clock(_ => 3.0));
        // 모든 조각이 30 ms: 8 MiB 에 240 ms 라 8 MiB 마다 조회
        TouchResult slow30 = Go(Repeat(40), Counter(), clock: Clock(_ => 30.0));

        Assert.Equal(3, slow6.Checks);
        Assert.InRange(slow30.Checks, 9, 12);
        Assert.Equal(TouchEnd.Completed, slow30.End);
    }

    [Fact]
    public void ALotOfSlowPiecesWithOnlyANoiseSizedFaultIncreasePerCheckNeverReachTheSafetyNet()
    {
        // 모든 조각이 30 ms (CPU 선점), 조회마다 읽기가 1 회 (실측 잡음) 씩 늘어남: 시간 합이 2 초를 넘어도 안전망에 더하지 않음
        TouchResult noise = Go(Repeat(200), Counter([0, .. Enumerable.Repeat(1u, 200)]), clock: Clock(_ => 30.0));
        // 같은 조건에서 증가가 하한 (SafetyNetMinFaults) 이면 안전망으로 멈춤
        TouchResult reads = Go(Repeat(200), Counter([0, .. Enumerable.Repeat(StandbyTouch.SafetyNetMinFaults, 200)]), clock: Clock(_ => 30.0));

        Assert.Equal(TouchEnd.Completed, noise.End);
        Assert.Equal(400L * Mib, noise.Bytes);
        Assert.Equal(TouchEnd.NeedsDiskReads, reads.End);
        Assert.Equal(StopReason.SlowTime, reads.Reason);
    }

    [Fact]
    public void SettleChecksWhatWasTouchedAfterTheLastCheckOfARoundAndReportsABoundaryThere()
    {
        using (StandbyTouch.Round())
        {
            TouchResult run = Go(Repeat(1), Counter(0, 40)); // 2 MiB 만 만져 끝 조회를 미룸
            TouchResult tail = StandbyTouch.Settle(); // 그 구간의 읽기 40 회 (허용치 32 초과)

            Assert.Equal(TouchEnd.Completed, run.End);
            Assert.Equal(TouchEnd.NeedsDiskReads, tail.End);
            Assert.Equal((40L, StopReason.HardFaults, 1), (tail.HardFaults, tail.Reason, tail.Checks));
            Assert.Equal(TouchEnd.Completed, StandbyTouch.Settle().End); // 남은 구간이 없으면 빈 결과
        }

        Assert.Equal(TouchEnd.Completed, StandbyTouch.Settle().End); // 회차 밖
    }

    [Fact]
    public void SlowPiecesFromCpuContentionAloneNeverStopEvenWhenTheirTimeAddsUpPastTheBudget()
    {
        // 모든 조각이 30 ms (CPU 선점) 지만 읽기 수는 늘지 않음: 시간 합 2.4 초 > 2 초여도 계속
        TouchResult r = Go(Repeat(40), Counter(), clock: Clock(_ => 30));

        Assert.Equal(TouchEnd.Completed, r.End);
        Assert.Equal(80L * Mib, r.Bytes);
        Assert.True(r.SlowPieces > 0);
        Assert.Equal(0, r.HardFaults);
    }

    [Fact]
    public void SlowPiecesThatCoincideWithConfirmedDiskReadsAreTheLastResortAndStopByTime()
    {
        // 조각마다 30 ms 이고 조회마다 (허용치 이하의) 읽기 5 회가 확인됨: 시간 합이 2 초를 넘으면 안전망으로 멈춤
        TouchResult r = Go(Repeat(200), Counter([0, .. Enumerable.Repeat(5u, 200)]), clock: Clock(_ => 30));

        Assert.Equal(TouchEnd.NeedsDiskReads, r.End);
        Assert.Equal(StopReason.SlowTime, r.Reason);
        Assert.InRange(r.Bytes, 60L * Mib, 100L * Mib);
    }

    [Fact]
    public void WithoutAReadableFaultCountTheSlowPieceTimeIsTheOnlySafetyNetAndFastPiecesNeverStop()
    {
        TouchResult fast = Go(Repeat(40), () => null);
        TouchResult slow = Go(Repeat(100), () => null, clock: Clock(_ => 30));

        Assert.Equal(TouchEnd.Completed, fast.End);
        Assert.Equal(TouchEnd.NeedsDiskReads, slow.End);
        Assert.Equal(StopReason.SlowTime, slow.Reason);
        Assert.InRange(slow.Bytes, 66L * Mib, 70L * Mib); // 30 ms x 67 조각 > 2 초
    }

    [Fact]
    public void AnUnopenableFileIsAnErrorWhileTheOtherRangesAreStillTouched()
    {
        string notThere = _dir.Combine("none.bin");

        TouchResult r = Go([new ReadOp(_path, 0, 2 * Mib), new ReadOp(notThere, 0, Mib), new ReadOp(_path, 0, Mib)], Counter());

        Assert.Equal(TouchEnd.Completed, r.End);
        Assert.Equal(3L * Mib, r.Bytes);
        Assert.Equal(1, r.Errors);
    }

    [Fact]
    public void ARoundKeepsTheCountersBaselineAcrossRunsSoTheyAreQueriedLessOften()
    {
        int calls = 0;
        Func<uint?> counter = () =>
        {
            calls++;
            return 1_000_000;
        };
        TouchResult[] alone = [Go(Repeat(1), counter), Go(Repeat(1), counter)];
        int aloneCalls = calls;
        calls = 0;

        using (StandbyTouch.Round())
        {
            _ = Go(Repeat(1), counter);
            _ = Go(Repeat(1), counter);
        }

        Assert.Equal(6, aloneCalls); // 따로 부르면 Run 마다 버리는 조회 + 기준 + 끝 조회
        Assert.Equal(2, calls); // 한 회차 안에서는 처음 한 번의 버리는 조회 + 기준뿐 (두 Run 이 합쳐 8 MiB 미만이라 끝 조회도 미룸)
        Assert.All(alone, r => Assert.Equal(TouchEnd.Completed, r.End));
    }

    [Fact]
    public void TheRangesOfSuspiciousPiecesAreNotedAsTheServicesOwnReadsOnlyWhenDiskReadsWereConfirmed()
    {
        SelfReadObservation observation = SelfReadLog.Begin();

        _ = Go(Repeat(8), Counter(0, 4), clock: Clock(k => k == 3 ? 10.0 : 0)); // 의심 조각이 있고 읽기 4 회가 확인됨
        _ = Go([new ReadOp(_path, 0, 2 * Mib)], Counter(0, 0), clock: Clock(_ => 10.0)); // 의심 조각이지만 읽기 0 회 (CPU 선점)

        (IReadOnlyList<SelfReadBatch> batches, _) = observation.End();
        ReadOp[] noted = [.. batches.SelectMany(b => b.Ops).Where(o => o.Path == _path)];
        Assert.Equal([new ReadOp(_path, Mib, Mib)], noted); // 조각 3 번 (두 번째 범위의 뒤쪽 1 MiB)
    }

    [Fact]
    public void RangesOfTheSameFileThatFollowEachOtherAreMappedAsOneViewInAnyAddressOrder()
    {
        ReadOp[] ranges = [.. Enumerable.Range(0, 10).Select(i => new ReadOp(_path, i * 128L * 1024, 64 * 1024))];

        TouchResult forward = Go(ranges, Counter());
        TouchResult backwards = Go([.. ranges.Reverse()], Counter());

        Assert.Equal(1, forward.Maps);
        Assert.Equal(1, backwards.Maps);
        Assert.Equal(10L * 64 * 1024, forward.Bytes);
    }

    [Fact]
    public void RangesOfTheSameFileFartherApartThanOneViewAndALongRangeAreMappedSeparately()
    {
        _ = _dir.WriteRandom("far.bin", 150 * Mib);
        string far = _dir.Combine("far.bin");

        TouchResult r = Go([new ReadOp(far, 0, Mib), new ReadOp(far, 70L * Mib, Mib), new ReadOp(far, 72L * Mib, Mib), new ReadOp(far, 140L * Mib, Mib)], Counter());
        TouchResult longRange = Go([new ReadOp(far, 0, 70 * Mib)], Counter());

        Assert.Equal(4L * Mib, r.Bytes);
        Assert.Equal(3, r.Maps); // 0 / 70~73 MiB (한 뷰) / 140 MiB
        Assert.Equal(70L * Mib, longRange.Bytes);
        Assert.Equal(2, longRange.Maps); // 64 MiB + 6 MiB
    }

    [Fact]
    public void AMissingFileOrARangeOutsideTheFileIsAnErrorAndACanceledTouchEndsAsCanceled()
    {
        TouchResult r = Go([new ReadOp(_dir.Combine("none.bin"), 0, Mib), new ReadOp(_path, 5 * Mib, Mib), new ReadOp(_path, 0, Mib)], Counter());
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        TouchResult canceled = Go(Repeat(2), Counter(), ct: cts.Token);

        Assert.Equal((2, (long)Mib), (r.Errors, r.Bytes));
        Assert.Equal(TouchEnd.Canceled, canceled.End);
        Assert.Equal(0, canceled.Bytes);
    }

    [Fact]
    public void ARangeOfASparseFileIsSkippedBeforeAnySectionIsOpenedAndCountedApartFromTouchedBytesAndErrors()
    {
        string notThere = _dir.Combine("sparse.bin"); // 열려고 하면 오류로 셈: 오류 0 이면 열지 않은 것
        var asked = new List<string>();

        TouchResult r = Go([new ReadOp(notThere, 0, 2 * Mib), new ReadOp(notThere, 4 * Mib, 3 * Mib)], Counter(), attributes: path =>
        {
            asked.Add(path);
            return Archive | Sparse;
        });

        Assert.Equal(TouchEnd.Completed, r.End);
        Assert.Equal(5L * Mib, r.SkippedBytes);
        Assert.Equal((0L, 0, 0L, 0), (r.Bytes, r.Errors, r.DiskBytes, r.SlowPieces));
        Assert.Equal([notThere], asked); // 파일마다 한 번만 조회
    }

    [Fact]
    public void SparseAndOrdinaryFilesMixedTouchOnlyTheOrdinaryOnesAndEachFilesAttributesAreReadOnce()
    {
        string sparse = _dir.Combine("s.bin");
        _ = _dir.WriteRandom("s.bin", 2 * Mib);
        var asked = new List<string>();

        TouchResult r = Go([new ReadOp(sparse, 0, 2 * Mib), new ReadOp(_path, 0, 2 * Mib), new ReadOp(sparse, 0, Mib), new ReadOp(_path, 0, Mib)], Counter(), attributes: path =>
        {
            asked.Add(path);
            return path == sparse ? Archive | Sparse : Archive;
        });

        Assert.Equal(TouchEnd.Completed, r.End);
        Assert.Equal(3L * Mib, r.Bytes);
        Assert.Equal(3L * Mib, r.SkippedBytes);
        Assert.Equal(0, r.Errors);
        Assert.Equal([sparse, _path], asked);
    }

    [Fact]
    public void AnAttributeReadThatThrowsIsNotTakenForSparseSoTheFileIsOpenedAsBefore()
    {
        string notThere = _dir.Combine("none.bin");
        Func<string, uint> broken = _ => throw new IOException("attributes unreadable");

        TouchResult r = Go([new ReadOp(_path, 0, 2 * Mib), new ReadOp(notThere, 0, Mib)], Counter(), attributes: broken);

        Assert.Equal(TouchEnd.Completed, r.End);
        Assert.Equal(2L * Mib, r.Bytes); // 열 수 있는 파일은 만지고
        Assert.Equal(1, r.Errors); // 없는 파일은 기존대로 오류
        Assert.Equal(0, r.SkippedBytes);
    }

    [Fact]
    public void AFilesAttributesAreNotReadAgainAfterTheOpenSectionsAreClearedForTooManyFiles()
    {
        string[] paths = [.. Enumerable.Range(0, 70).Select(i => _dir.Combine($"none{i}.bin"))]; // 65번째부터 열린 섹션을 모두 닫고 비움 (없는 파일은 오류로 세고 기억됨)
        var asked = new List<string>();

        TouchResult r = Go([.. paths.Select(p => new ReadOp(p, 0, Mib)), new ReadOp(paths[0], 0, Mib)], Counter(), attributes: path =>
        {
            asked.Add(path);
            return Archive;
        });

        Assert.Equal(71, r.Errors);
        Assert.Equal(paths, asked); // 비운 뒤에 다시 만난 첫 파일도 다시 조회하지 않음
    }

    [Fact]
    public void ACounterThatWrapsAroundIsDifferencedAsUnsigned()
    {
        uint value = uint.MaxValue - 10;
        int calls = 0;
        Func<uint?> wrapping = () =>
        {
            value = calls++ == 0 ? value : unchecked(value + 20); // 기준 뒤 20 회 늘며 넘침
            return value;
        };

        TouchResult r = Go(Repeat(2), wrapping);

        Assert.Equal(20, r.HardFaults);
        Assert.Equal(TouchEnd.Completed, r.End);
    }

    [Fact]
    public void TheLiveProcessCounterIsReadableAndALiveTouchOfAFileInMemoryGoesToTheEnd()
    {
        uint? first = ProcessFaults.Query();
        TouchResult r = StandbyTouch.Run(Repeat(20), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(40L * Mib, r.Bytes); // 메모리에 있는 파일 (방금 쓴 임시 파일): 읽기 수는 같은 프로세스의 다른 스레드가 섞일 수 있어 값은 확인하지 않음
    }

    // ---- 오류의 종류 (기록용) ----

    [Fact]
    public void ErrorsAreCountedByKindWithTheWin32CodeAndTheWorstFile()
    {
        string empty = _dir.Combine("empty.bin");
        File.WriteAllBytes(empty, []);
        string missing = _dir.Combine("none.bin");
        ReadOp[] ranges = [new(missing, 0, Mib), new(_path, 5 * Mib, Mib), new(empty, 0, Mib), new(missing, Mib, Mib), new(_path, 0, Mib)]; // 열기 실패 2 (같은 파일), 파일 끝 밖 1, 크기 0 1, 정상 1

        TouchResult r = Go(ranges, Counter());

        Assert.Equal(4, r.Errors);
        TouchErrors kinds = Assert.IsType<TouchErrors>(r.ErrorKinds);
        Assert.Equal(4, kinds.Total);
        Assert.Equal(3, kinds.Count(TouchErrorKind.Open));
        Assert.Equal(1, kinds.Count(TouchErrorKind.BeyondEnd));
        Assert.Equal([(2, 2), (TouchErrors.EmptyFile, 1)], kinds.Codes(TouchErrorKind.Open)); // 2 = ERROR_FILE_NOT_FOUND
        Assert.Equal((missing, 2), kinds.Worst);
        Assert.Equal($"open 3 (win32 2 ×2, empty file ×1), beyond end 1; most failures in {missing} ×2", kinds.Describe());
    }

    [Fact]
    public void ARunWithoutErrorsCarriesNoErrorKinds() => Assert.Null(Go(Repeat(2), Counter()).ErrorKinds);

    [Fact]
    public void ErrorKindsAddUpAcrossPartsAndKeepTheWorstFile()
    {
        var a = new TouchErrors();
        a.Add(TouchErrorKind.Map, 2, @"D:\x.bin", 8);
        var b = new TouchErrors();
        b.Add(TouchErrorKind.Map, 1, @"D:\x.bin", 8);
        b.Add(TouchErrorKind.Read, 4, @"D:\y.bin", 23);

        TouchErrors sum = Assert.IsType<TouchErrors>(TouchErrors.Combine(a, b));

        Assert.Equal((3, 4), (sum.Count(TouchErrorKind.Map), sum.Count(TouchErrorKind.Read)));
        Assert.Equal((@"D:\y.bin", 4), sum.Worst);
        Assert.Equal(@"map 3 (win32 8 ×3), read 4 (win32 23 ×4); most failures in D:\y.bin ×4", sum.Describe());
        Assert.Same(b, TouchErrors.Combine(null, b));
        Assert.Null(TouchErrors.Combine(null, null));
        Assert.Equal(string.Empty, new TouchErrors().Describe());
    }

    // ---- 표본 확인 (기록용) ----

    [Fact]
    public void ProbeReturnsTheHardFaultIncreaseOfEachSampleAndMinusOneForSparseMissingOrOutOfFileOnes()
    {
        string sparse = _dir.Combine("sparse.bin");
        _ = _dir.WriteRandom("sparse.bin", Mib);
        ReadOp[] samples = [new(_path, 0, StandbyTouch.ProbeBytes), new(_path, Mib, StandbyTouch.ProbeBytes), new(sparse, 0, StandbyTouch.ProbeBytes), new(_dir.Combine("none.bin"), 0, 4096), new(_path, 9 * Mib, 4096), new(_path, Mib + 4096, StandbyTouch.ProbeBytes)];

        int[]? faults = StandbyTouch.Probe(samples, CancellationToken.None, Counter(0, 5, 1, 3), p => p == sparse ? Sparse : Archive); // 앞 두 번 (버림·기준) 뒤 열린 표본마다 한 번씩 조회: 5, 1, 3

        Assert.Equal([5, 1, -1, -1, -1, 3], Assert.IsType<int[]>(faults));
    }

    [Fact]
    public void ProbeIsNullWhenTheHardFaultCountCannotBeRead() => Assert.Null(StandbyTouch.Probe([new ReadOp(_path, 0, 4096)], CancellationToken.None, () => null, _ => Archive));

    [Fact]
    public void ProbeStopsWhenCanceled()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Equal([-1, -1], Assert.IsType<int[]>(StandbyTouch.Probe([new ReadOp(_path, 0, 4096), new ReadOp(_path, Mib, 4096)], cancel.Token, Counter(), _ => Archive)));
    }

    [Fact]
    public void ProcessSnapshotListsThisProcessWithItsCpuTimeAndWrittenBytes()
    {
        List<ProcessUsage> before = [];
        Assert.True(ProcessFaults.Snapshot(before));
        ProcessUsage mine = Assert.Single(before, p => p.Pid == Environment.ProcessId);
        Assert.False(string.IsNullOrEmpty(mine.Name));
        Assert.True(mine.CpuTicks > 0);
        Assert.Contains(before, p => p.Pid == 0); // 시스템 유휴 프로세스도 담김 (쓰는 쪽이 뺌)

        _ = _dir.WriteRandom("w.bin", 4 * Mib);
        long spin = Stopwatch.GetTimestamp() + (Stopwatch.Frequency / 20);
        while (Stopwatch.GetTimestamp() < spin)
        {
        }

        List<ProcessUsage> after = [];
        Assert.True(ProcessFaults.Snapshot(after));
        ProcessUsage later = Assert.Single(after, p => p.Pid == Environment.ProcessId);
        Assert.True(later.CpuTicks > mine.CpuTicks);
        Assert.True(later.WriteBytes >= mine.WriteBytes + (4 * Mib));
    }

    [Fact]
    public void MemoryListPrioritiesAreReadPerPriorityInBytes()
    {
        nuint[] v = new nuint[22];
        v[0] = 10; // 0 으로 채운 목록
        v[1] = 20; // 여유 목록
        for (int i = 0; i < 8; i++)
        {
            v[5 + i] = (nuint)(100 + i); // 우선순위별 대기 쪽 수
            v[13 + i] = (nuint)(1000 + i); // 우선순위별 재사용 누계
        }

        MemoryPriorityLists lists = MemoryListInfo.Priorities(v, 4096);

        Assert.Equal(30 * 4096L, lists.FreeBytes);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (100 + i) * 4096L), lists.StandbyBytes);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (1000 + i) * 4096L), lists.RepurposedBytes);
        Assert.Equal(MemoryListInfo.Summarize(v, 4096).CacheRepurposedBytes, lists.RepurposedBytes[5..].Sum()); // 기존 요약 (우선순위 5~7) 과 같은 값
    }
}
