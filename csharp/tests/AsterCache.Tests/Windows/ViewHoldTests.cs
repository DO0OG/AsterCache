using System.Diagnostics;
using System.Runtime.InteropServices;
using AsterCache.Core.Prefetch;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 붙들기 (ViewHold): 실제 Windows 호출로 임시 폴더의 작은 파일을 붙든 뒤 작업 집합 (QueryWorkingSetEx), 하드 폴트, 다른 핸들의 파일 조작, 내놓기 순서, 닫기를 확인합니다 (게임 파일·큰 메모리는 쓰지 않음).
public sealed class ViewHoldTests : IDisposable
{
    private const int Mib = 1 << 20;
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct WorkingSetEx
    {
        public nint VirtualAddress;
        public nuint Attributes;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool QueryWorkingSetEx(nint process, [In, Out] WorkingSetEx[] info, int size);

    // 주소 범위의 쪽 가운데 이 프로세스의 작업 집합에 있는 (Valid) 쪽 수와 전체 쪽 수
    private static (int Valid, int Total) Resident(nint address, long length)
    {
        int pages = (int)(length / 4096);
        var info = new WorkingSetEx[pages];
        for (int i = 0; i < pages; i++)
        {
            info[i].VirtualAddress = address + (i * 4096);
        }

        // Process.GetCurrentProcess() 가 임시 객체라 .Handle 만 쓰면 호출 도중 객체가 수거되며 핸들이 닫혀 간헐적으로 실패할 수 있으므로 객체를 호출이 끝날 때까지 들고 있고, 실패하면 오류 번호를 담아 세 번까지 다시 부름
        using Process me = Process.GetCurrentProcess();
        int error = 0;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (QueryWorkingSetEx(me.Handle, info, pages * Marshal.SizeOf<WorkingSetEx>()))
            {
                return (info.Count(e => (e.Attributes & 1) != 0), pages);
            }

            error = Marshal.GetLastWin32Error();
            Thread.Sleep(10);
        }

        throw new Xunit.Sdk.XunitException($"QueryWorkingSetEx failed 3 times, last error {error}");
    }

    // 붙든 쪽이 작업 집합에 있어야 하는 판정: 메모리가 빠듯한 환경에서는 운영체제가 잠그지 않은 쪽을 줄여 갈 수 있으므로, 만족하지 못하면 그 범위를 다시 만진 직후 조회하기를 최대 세 번 시도합니다.
    private static (int Valid, int Total) ResidentAfterTouch(nint address, long length, Func<int, int> minValid)
    {
        (int valid, int total) = Resident(address, length);
        for (int attempt = 1; attempt < 3 && valid < minValid(total); attempt++)
        {
            long sum = 0;
            for (long at = 0; at < length; at += 4096)
            {
                sum += Marshal.ReadByte(address + (nint)at); // 다시 만짐 (읽기만 함)
            }

            GC.KeepAlive(sum);
            (valid, total) = Resident(address, length);
        }

        return (valid, total);
    }

    private string Make(string name, int mib)
    {
        _ = _dir.WriteRandom(name, mib * Mib);
        _ = File.ReadAllBytes(_dir.Combine(name)); // 캐시에 올림
        return _dir.Combine(name);
    }

    private static Func<uint?> Counter(uint step)
    {
        uint value = 5_000_000;
        return () => value += step;
    }

    [Fact]
    public void HoldsTheRangesInOrderKeepsThemInTheWorkingSetAndSkipsWhatIsAlreadyHeld()
    {
        string a = Make("a.bin", 4);
        string b = Make("b.bin", 4);
        using var hold = new ViewHold(Counter(0), Counter(0), minFileBytes: 0);
        hold.Rebase();

        HoldBatchResult r = hold.Hold([new ReadOp(a, 0, 2L * Mib), new ReadOp(b, 0, 2L * Mib), new ReadOp(a, 0, 2L * Mib), new ReadOp(a, Mib, 2L * Mib)], CancellationToken.None);

        Assert.Equal(5L * Mib, hold.HeldBytes); // 같은 구간은 두 번 붙들지 않음
        Assert.Equal(5L * Mib, r.Bytes);
        Assert.Equal(-1, r.BoundaryIndex);
        Assert.Equal(2, hold.Files);
        Assert.Equal(2, hold.Views); // 파일마다 뷰 하나
        foreach ((nint address, long length) in hold.LiveRanges())
        {
            (int valid, int total) = ResidentAfterTouch(address, length, t => t);
            Assert.Equal(total, valid); // 뷰를 닫지 않아 읽은 쪽이 모두 작업 집합에 있음
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(nint process);

    [Fact]
    public void TheCheckLooksUpTheWorkingSetWithoutReadingAndRefillsNothingWhileEverythingIsResident()
    {
        string a = Make("a.bin", 8);
        using var hold = new ViewHold(Counter(0), Counter(0), minFileBytes: 0); // 가짜 누계: 다른 시험의 읽기가 판정에 섞이지 않게
        hold.Rebase();
        _ = hold.Hold([new ReadOp(a, 0, 8L * Mib)], CancellationToken.None);

        HoldCheckResult r = hold.Recheck(Prewarm32Mib, CancellationToken.None);

        Assert.Equal(8L * Mib, r.CheckedBytes); // 쪽마다 작업 집합에 있는지 물음
        Assert.True(r.TrimmedBytes <= Mib, $"trimmed {r.TrimmedBytes}"); // 줄여 간 쪽이 (거의) 없음: 읽지 않음
        Assert.Equal(0, r.RefilledBytes);
        Assert.Equal(0, r.Checks); // 읽은 것이 없어 하드 폴트 조회도 없음
        Assert.False(r.CapReached);
        Assert.Equal(8L * Mib, hold.HeldBytes);
    }

    [Fact]
    public void WhenTheMemoryManagerTrimsTheWorkingSetTheCheckFindsItAndReadsOnlyThoseBackIn()
    {
        string a = Make("a.bin", 8);
        using var hold = new ViewHold(Counter(0), Counter(0), minFileBytes: 0);
        hold.Rebase();
        _ = hold.Hold([new ReadOp(a, 0, 8L * Mib)], CancellationToken.None);
        using Process me = Process.GetCurrentProcess();
        HoldCheckResult r = default!;
        for (int attempt = 0; attempt < 5; attempt++) // 한 번 비운 것이 일부만 빠지는 때가 있어 몇 번 다시 시도 (실제 메모리 관리자의 동작이라 양이 일정하지 않음)
        {
            Assert.True(EmptyWorkingSet(me.Handle)); // 메모리 관리자가 작업 집합을 줄이는 것을 흉내 냄: 붙든 쪽이 Valid 에서 빠짐 (페이지는 대기 목록에 있어 디스크 읽기는 없음)
            r = hold.Recheck(Prewarm32Mib, CancellationToken.None);
            if (r.TrimmedBytes >= 4L * Mib)
            {
                break;
            }
        }

        Assert.True(r.TrimmedBytes >= 4L * Mib, $"trimmed {r.TrimmedBytes}"); // 대부분이 빠짐
        Assert.Equal(8L * Mib, hold.HeldBytes); // 상한 안: 목록에서 빼지 않음
        (nint address, long length) = hold.LiveRanges().Single();
        (int valid, int total) = ResidentAfterTouch(address, length, t => t * 9 / 10);
        Assert.True(valid >= total * 9 / 10, $"valid {valid} of {total}"); // 빠진 쪽을 다시 읽어 작업 집합에 올림
    }

    private const long Prewarm32Mib = 32L << 20;

    [Fact]
    public void ReleaseTakesTheLeastImportantFirstAndTakesThePagesOutOfTheWorkingSet()
    {
        string a = Make("a.bin", 4);
        string b = Make("b.bin", 4);
        using var hold = new ViewHold(minFileBytes: 0);
        hold.Rebase();
        _ = hold.Hold([new ReadOp(a, 0, 4L * Mib), new ReadOp(b, 0, 4L * Mib)], CancellationToken.None);
        IReadOnlyList<(nint Address, long Length)> before = hold.LiveRanges();

        long released = hold.Release(2L * Mib);

        Assert.Equal(2L * Mib, released);
        Assert.Equal(6L * Mib, hold.HeldBytes);
        IReadOnlyList<(nint Address, long Length)> after = hold.LiveRanges();
        Assert.Equal(before[0], after[0]); // 먼저 붙든 파일 a 는 그대로
        Assert.Equal(2L * Mib, after[1].Length); // 나중에 붙든 b 의 뒤쪽이 줄어듦
        (int validA, int totalA) = ResidentAfterTouch(after[0].Address, after[0].Length, t => t);
        Assert.Equal(totalA, validA);
        (int validTail, int totalTail) = Resident(before[1].Address + (2 * Mib), 2L * Mib);
        Assert.Equal(0, validTail); // 내놓은 쪽은 작업 집합에서 빠짐 (VirtualUnlock 이 FALSE 와 오류 158 을 돌려줘도)
        Assert.Equal(512, totalTail);
        Assert.Equal(4L * Mib + 2L * Mib, hold.HeldBytes);
    }

    [Fact]
    public void WhatWasReleasedIsReportedMostImportantFirstAndHeldAgainByHoldAndThenNoLongerReported()
    {
        string a = Make("a.bin", 4);
        string b = Make("b.bin", 4);
        using var hold = new ViewHold(Counter(0), Counter(0), minFileBytes: 0);
        hold.Rebase();
        _ = hold.Hold([new ReadOp(a, 0, 4L * Mib), new ReadOp(b, 0, 4L * Mib)], CancellationToken.None);
        Assert.Empty(hold.ReleasedRanges(long.MaxValue)); // 내놓은 것이 없음
        _ = hold.Release(6L * Mib); // 나중에 붙든 b 전체와 a 의 뒤쪽 2 MiB

        IReadOnlyList<ReadOp> released = hold.ReleasedRanges(long.MaxValue);

        Assert.Equal([new ReadOp(a, 2L * Mib, 2L * Mib), new ReadOp(b, 0, 4L * Mib)], released); // 덜 중요해서 먼저 내놓은 b 가 뒤
        Assert.Equal([new ReadOp(a, 2L * Mib, Mib)], hold.ReleasedRanges(Mib)); // 상한까지만
        HoldBatchResult r = hold.Hold(released, CancellationToken.None);
        Assert.Equal(6L * Mib, r.Bytes);
        Assert.Equal(8L * Mib, hold.HeldBytes);
        Assert.Empty(hold.ReleasedRanges(long.MaxValue)); // 다시 붙들었으면 더 나오지 않음
    }

    [Fact]
    public void HeldRangesListsWhatIsHeldNowAndDropsTheReleasedPart()
    {
        string a = Make("a.bin", 4);
        string b = Make("b.bin", 4);
        using var hold = new ViewHold(Counter(0), Counter(0), minFileBytes: 0);
        hold.Rebase();
        _ = hold.Hold([new ReadOp(a, 0, 4L * Mib), new ReadOp(b, 0, 4L * Mib)], CancellationToken.None);
        Assert.Equal([new ReadOp(a, 0, 4L * Mib), new ReadOp(b, 0, 4L * Mib)], hold.HeldRanges());

        _ = hold.Release(6L * Mib); // 나중에 붙든 b 전체와 a 의 뒤쪽 2 MiB

        Assert.Equal([new ReadOp(a, 0, 2L * Mib)], hold.HeldRanges());
    }

    [Fact]
    public void AGivenUpRangeAndAFileThatChangedAfterTheReleaseAreNotReportedAgain()
    {
        string a = Make("a.bin", 4);
        string b = Make("b.bin", 4);
        string c = Make("c.bin", 4);
        using var hold = new ViewHold(Counter(0), Counter(0), minFileBytes: 0);
        hold.Rebase();
        _ = hold.Hold([new ReadOp(a, 0, 4L * Mib), new ReadOp(b, 0, 4L * Mib), new ReadOp(c, 0, 4L * Mib)], CancellationToken.None);
        _ = hold.Release(12L * Mib);
        hold.GiveUp([new ReadOp(c, 0, 4L * Mib)]); // 메모리에 없어 다시 붙들지 않기로 한 범위
        File.SetLastWriteTimeUtc(b, DateTime.UtcNow.AddMinutes(5)); // 내놓은 뒤 바뀐 파일

        IReadOnlyList<ReadOp> released = hold.ReleasedRanges(long.MaxValue);

        Assert.Equal([new ReadOp(a, 0, 4L * Mib)], released);
        Assert.Equal(0, hold.HeldBytes);
    }

    [Fact]
    public void WhileHeldAnotherHandleCanOverwriteTheSameSizeGrowAndDeleteButShrinkingFailsWithErrorOf1224()
    {
        string a = Make("a.bin", 4);
        string c = Make("c.bin", 4);
        using var hold = new ViewHold(minFileBytes: 0);
        hold.Rebase();
        _ = hold.Hold([new ReadOp(a, 0, 4L * Mib), new ReadOp(c, 0, 4L * Mib)], CancellationToken.None);

        using (var fs = new FileStream(a, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.Write(new byte[Mib]); // 같은 크기 덮어쓰기
            fs.SetLength(5L * Mib); // 늘리기
            IOException shrink = Assert.Throws<IOException>(() => fs.SetLength(2L * Mib));
            Assert.Equal(1224, shrink.HResult & 0xFFFF); // ERROR_USER_MAPPED_FILE
        }

        File.Delete(c); // 파일 핸들을 닫은 섹션·뷰는 삭제를 막지 않음
        Assert.False(File.Exists(c));
    }

    [Fact]
    public void AFileWhoseSizeOrTimeChangedIsReleasedAtTheCheckAndTheOtherFilesStayHeld()
    {
        string a = Make("a.bin", 4);
        string b = Make("b.bin", 4);
        using var hold = new ViewHold(minFileBytes: 0);
        hold.Rebase();
        _ = hold.Hold([new ReadOp(a, 0, 4L * Mib), new ReadOp(b, 0, 4L * Mib)], CancellationToken.None);
        File.SetLastWriteTimeUtc(a, DateTime.UtcNow.AddMinutes(5));
        hold.Rebase();

        HoldCheckResult r = hold.Recheck(Prewarm32Mib, CancellationToken.None);

        Assert.Equal([a], r.ChangedFiles);
        Assert.Equal(4L * Mib, hold.HeldBytes);
        Assert.Equal(1, hold.Files);
        using (var fs = new FileStream(a, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.SetLength(Mib); // 뷰를 닫았으니 줄이기가 됨
        }
    }

    // ---- 게임이 쓰는 파일: 5 초 확인 ----

    [Fact]
    public void AppendingToAHeldWrittenFileThroughAnotherHandleReleasesThatFileAtTheNextCheckAndNotTheOthers()
    {
        string written = Make("w.bin", 4);
        string unchanged = Make("u.bin", 4);
        string plain = Make("p.bin", 4);
        using var hold = new ViewHold(minFileBytes: 0);
        hold.MarkWritten([written, unchanged]);
        hold.Rebase();
        _ = hold.Hold([new ReadOp(written, 0, 4L * Mib), new ReadOp(unchanged, 0, 4L * Mib), new ReadOp(plain, 0, 4L * Mib)], CancellationToken.None);
        Assert.Equal(8L * Mib, hold.WrittenHeldBytes);
        Assert.Equal(2, hold.WrittenFiles);
        Assert.Empty(hold.ReleaseChangedWritten()); // 바뀐 것이 없으면 아무것도 닫지 않음

        using (var fs = new FileStream(written, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.Seek(0, SeekOrigin.End);
            fs.Write(new byte[Mib]); // 덧붙이기는 뷰가 있어도 성공
        }

        File.SetLastWriteTimeUtc(plain, DateTime.UtcNow.AddMinutes(5)); // 게임이 쓰는 파일이 아니면 이 확인은 보지 않음 (2 분 점검 몫)
        IReadOnlyList<WrittenRelease> released = hold.ReleaseChangedWritten();

        Assert.Equal([new WrittenRelease(written, 4L * Mib)], released);
        Assert.Equal(8L * Mib, hold.HeldBytes); // 쓰는 파일 하나만 내놓음
        Assert.Equal(4L * Mib, hold.WrittenHeldBytes);
        Assert.Equal(1, hold.WrittenFiles);
        Assert.Empty(hold.ReleaseChangedWritten()); // 같은 파일을 다시 내놓지 않음
        using (var fs = new FileStream(written, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
        {
            fs.SetLength(Mib); // 뷰를 닫았으니 크기 줄이기도 됨
        }
    }

    [Fact]
    public void AModifiedTimeChangeAloneAndAReplacementByRenamingATempFileReleaseTheWrittenFile()
    {
        string touched = Make("t.bin", 4);
        string swapped = Make("s.bin", 4);
        using var hold = new ViewHold(minFileBytes: 0);
        hold.MarkWritten([touched, swapped]);
        hold.Rebase();
        _ = hold.Hold([new ReadOp(touched, 0, 4L * Mib), new ReadOp(swapped, 0, 4L * Mib)], CancellationToken.None);

        File.SetLastWriteTimeUtc(touched, DateTime.UtcNow.AddMinutes(5)); // 크기가 같아도 수정 시각이 바뀜
        string temp = swapped + ".tmp";
        File.WriteAllBytes(temp, new byte[4 * Mib]); // 같은 크기의 새 파일로 바꿔 끼움 (임시 파일 + 이름 바꾸기)
        File.Move(temp, swapped, true);
        IReadOnlyList<WrittenRelease> released = hold.ReleaseChangedWritten();

        Assert.Equal(new[] { touched, swapped }.Order(StringComparer.OrdinalIgnoreCase), released.Select(r => r.Path).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(0, hold.HeldBytes);
    }

    [Fact]
    public void AReleasedWrittenFileIsNotHeldAgainByALaterHoldInTheSamePlay()
    {
        string written = Make("w.bin", 4);
        using var hold = new ViewHold(minFileBytes: 0);
        hold.MarkWritten([written]);
        hold.Rebase();
        _ = hold.Hold([new ReadOp(written, 0, 2L * Mib)], CancellationToken.None);
        File.SetLastWriteTimeUtc(written, DateTime.UtcNow.AddMinutes(5));
        _ = hold.ReleaseChangedWritten();

        _ = hold.Hold([new ReadOp(written, 2L * Mib, 2L * Mib)], CancellationToken.None);

        Assert.Equal(0, hold.HeldBytes);
        Assert.Equal(0, hold.WrittenFiles);
    }

    [Fact]
    public void DisposeReleasesEverythingAndLeavesNoSectionOrViewSoShrinkingWorks()
    {
        string a = Make("a.bin", 4);
        var hold = new ViewHold(minFileBytes: 0);
        hold.Rebase();
        _ = hold.Hold([new ReadOp(a, 0, 4L * Mib)], CancellationToken.None);

        hold.Dispose();
        hold.Dispose(); // 두 번 불러도 됨

        Assert.Equal(0, hold.HeldBytes);
        Assert.Equal(0, hold.Views);
        Assert.Equal(0, hold.Files);
        using var fs = new FileStream(a, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        fs.SetLength(Mib);
    }

    [Fact]
    public void AnIncreaseOfHardFaultsOverTheToleranceStopsTheBatchAtAFoundBoundary()
    {
        string a = Make("a.bin", 4);
        string b = Make("b.bin", 4);
        using var hold = new ViewHold(Counter(100), Counter(1000), minFileBytes: 0);
        hold.Rebase();

        HoldBatchResult r = hold.Hold([new ReadOp(a, 0, 4L * Mib), new ReadOp(b, 0, 4L * Mib)], CancellationToken.None);

        Assert.Equal(1, r.BoundaryIndex); // 배치 끝의 조회에서 경계로 봄
        Assert.True(r.DiskBytes > 0);
        Assert.Equal(100, r.HardFaults);
        Assert.Equal(8L * Mib, hold.HeldBytes); // 읽은 것은 붙듦 (디스크에서 읽은 것도 이제 메모리에 있음)
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void AFailedHardFaultReadAtTheStartOrTheEndOfABatchMakesTheWholeBatchUnmeasured(bool startReads, bool endReads)
    {
        string a = Make("a.bin", 4);
        int calls = 0;
        uint value = 5_000_000;
        // 첫 두 번은 Rebase (버리는 조회와 기준값), 셋째부터는 묶음 안의 조회: 시작 쪽 실패는 기준값 조회가 null, 끝 쪽 실패는 묶음 끝의 조회가 null
        Func<uint?> faults = () => ++calls == 2 && !startReads || calls >= 3 && !endReads ? null : value;
        using var hold = new ViewHold(faults, Counter(0), minFileBytes: 0);
        hold.Rebase();

        HoldBatchResult r = hold.Hold([new ReadOp(a, 0, 4L * Mib)], CancellationToken.None);

        Assert.Equal(startReads && endReads, r.Measured); // 시작이나 끝 중 한 번이라도 조회가 실패하면 계측하지 못한 묶음
    }

    [Fact]
    public void WhenTheLastSegmentIsRefilledExactlyToTheCapTheCheckKeepsItHeld()
    {
        string a = Make("a.bin", 3);
        uint step = 0;
        uint faults = 5_000_000;
        uint pages = 5_000_000;
        using var hold = new ViewHold(() => faults += step, () => pages += step * 10, minFileBytes: 0);
        hold.Rebase();
        _ = hold.Hold([new ReadOp(a, 0, 3L * Mib)], CancellationToken.None);
        step = 100; // 조회 한 번이 4,096,000 바이트 (쪽 수 1,000 개 x 4 KiB) 를 디스크에서 읽은 것으로 보임
        using Process me = Process.GetCurrentProcess();
        HoldCheckResult r = default!;
        for (int attempt = 0; attempt < 5 && r.TrimmedBytes == 0; attempt++)
        {
            Assert.True(EmptyWorkingSet(me.Handle));
            r = hold.Recheck(4_096_000, CancellationToken.None);
        }

        Assert.True(r.TrimmedBytes > 0);
        Assert.False(r.CapReached); // 상한을 넘은 것이 아니라 정확히 닿았으므로 되찾은 구간을 그대로 붙듦 (넘어야 내놓음)
        Assert.Equal(0, r.DroppedBytes);
        Assert.Equal(3L * Mib, hold.HeldBytes);
    }

    [Theory]
    [InlineData(2)] // 게임이 로딩 중일 때의 점검 상한
    [InlineData(8)]
    public void TheCheckStopsReadingAtTheCapInsteadOfAtTheNextReadCountLookUp(int capMib)
    {
        string a = Make("a.bin", 16);
        uint step = 0;
        uint faults = 5_000_000;
        uint pages = 5_000_000;
        using var hold = new ViewHold(() => faults += step, () => pages += step * 10, minFileBytes: 0);
        hold.Rebase();
        _ = hold.Hold([new ReadOp(a, 0, 16L * Mib)], CancellationToken.None);
        step = 1000; // 조회 한 번이 40 MB 넘는 디스크 읽기로 보임 (정기 조회는 64 MiB 마다라 조회만으로는 상한 안에서 멈출 수 없음)
        using Process me = Process.GetCurrentProcess();
        HoldCheckResult r = default!;
        long read = long.MaxValue;
        for (int attempt = 0; attempt < 5 && !r.CapReached; attempt++)
        {
            Assert.True(EmptyWorkingSet(me.Handle));
            (nint address, long length) = hold.LiveRanges().Single();
            int before = Resident(address, length).Valid;
            r = hold.Recheck(capMib * (long)Mib, CancellationToken.None, dropOnCap: false);
            read = (Resident(address, length).Valid - before) * 4096L;
        }

        Assert.True(r.CapReached, $"trimmed {r.TrimmedBytes}");
        Assert.True(read <= (capMib * (long)Mib) + (64 * 1024), $"read {read}"); // 조회 간격 (64 MiB) 까지 읽으면 줄어든 구간 전체 (16 MiB 가까이) 를 읽게 됨
        Assert.Equal(16L * Mib, hold.HeldBytes); // 로딩 중 점검처럼 상한에 닿아도 빼지 않는 경우
    }

    [Theory]
    [InlineData(-4096, true)] // 상한 직전: 마지막 쪽 하나를 읽지 못함
    [InlineData(0, false)] // 정확히 상한: 끝까지 되찾았으므로 유지
    [InlineData(4096, false)] // 상한 초과 여유
    public void WithoutAHardFaultCountTheCheckCountsWhatItReadsAsDiskReadsAndStopsExactlyAtTheCap(int offset, bool capReached)
    {
        string a = Make("a.bin", 8);
        HoldCheckResult r = default!;
        long held = 0;
        for (int attempt = 0; attempt < 5; attempt++) // 같은 프로세스의 다른 시험이 작업 집합을 비우면 줄어든 양이 달라져 다시 시도
        {
            using var hold = new ViewHold(() => null, () => null, minFileBytes: 0); // 읽기 수를 읽을 수 없음
            hold.Rebase();
            _ = hold.Hold([new ReadOp(a, 0, 8L * Mib)], CancellationToken.None);
            (nint address, long length) = hold.LiveRanges().Single();
            using Process me = Process.GetCurrentProcess();
            Assert.True(EmptyWorkingSet(me.Handle));
            (int valid, int total) = Resident(address, length);
            long trimmed = (total - valid) * 4096L;
            r = hold.Recheck(trimmed + offset, CancellationToken.None);
            held = hold.HeldBytes;
            if (r.TrimmedBytes == trimmed && trimmed >= 4L * Mib)
            {
                break;
            }
        }

        Assert.True(r.TrimmedBytes >= 4L * Mib, $"trimmed {r.TrimmedBytes}");
        Assert.Equal(capReached, r.CapReached);
        Assert.Equal(capReached, r.DroppedBytes > 0);
        Assert.Equal(capReached ? 0 : 8L * Mib, held);
        Assert.Equal(0, r.RefilledBytes); // 세어 둔 양은 읽은 양으로 보고하지 않음
    }

    [Fact]
    public void AnEmptyBatchIsMeasuredEvenWhenTheHardFaultCountCannotBeRead()
    {
        using var hold = new ViewHold(() => null, () => null, minFileBytes: 0);
        hold.Rebase();

        HoldBatchResult r = hold.Hold([], CancellationToken.None);

        Assert.True(r.Measured); // 읽은 것이 없으므로 잴 것도 없음
    }

    [Fact]
    public void ASparseFileIsNotHeldAndAMissingFileCountsAsAnErrorWhileTheRestIsHeld()
    {
        string a = Make("a.bin", 2);
        string s = Make("s.bin", 2);
        using var hold = new ViewHold(attributes: p => p == s ? FileGuards.SparseAttribute : 0, minFileBytes: 0);
        hold.Rebase();

        HoldBatchResult r = hold.Hold([new ReadOp(s, 0, 2L * Mib), new ReadOp(_dir.Combine("none.bin"), 0, Mib), new ReadOp(a, 0, 8L * Mib)], CancellationToken.None);

        Assert.Equal(2L * Mib, r.SkippedBytes);
        Assert.Equal(1, r.Errors);
        Assert.Equal(2L * Mib, hold.HeldBytes); // 길이는 파일 끝에서 잘림
        Assert.Equal(1, hold.Files);
    }

    [Fact]
    public void AFileWhoseFirstTwoBytesAreMZIsNotHeldWhateverItsNameAndIsCounted()
    {
        string exe = _dir.Combine("tool.bin"); // 확장자와 상관없이 내용으로 판정
        byte[] data = new byte[3 * Mib];
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        File.WriteAllBytes(exe, data);
        string plain = Make("p.bin", 3);
        using var hold = new ViewHold(Counter(0), Counter(0), minFileBytes: 0);
        hold.Rebase();

        HoldBatchResult r = hold.Hold([new ReadOp(exe, 0, 3L * Mib), new ReadOp(plain, 0, 3L * Mib)], CancellationToken.None);

        Assert.Equal(3L * Mib, r.ImageBytes);
        Assert.Equal(3L * Mib, hold.HeldBytes); // 일반 파일만 붙듦
        Assert.Equal(1, hold.Files);
    }

    [Fact]
    public void AFileThatCannotBeReadForTheMZCheckIsNotHeldAndCountsAsAnError()
    {
        using var hold = new ViewHold(Counter(0), Counter(0), minFileBytes: 0);
        hold.Rebase();

        HoldBatchResult r = hold.Hold([new ReadOp(_dir.Combine("none.bin"), 0, Mib)], CancellationToken.None);

        Assert.Equal(0, hold.HeldBytes);
        Assert.Equal(1, r.Errors);
    }

    [Fact]
    public void FilesSmallerThanTheThresholdAreNotHeldAndTheirBytesAreCounted()
    {
        string small = Make("small.bin", 2);
        string big = Make("big.bin", 6);
        using var hold = new ViewHold(Counter(0), Counter(0), minFileBytes: 4L * Mib);
        hold.Rebase();

        HoldBatchResult r = hold.Hold([new ReadOp(small, 0, 2L * Mib), new ReadOp(big, 0, 6L * Mib)], CancellationToken.None);

        Assert.Equal(2L * Mib, r.SmallBytes);
        Assert.Equal(6L * Mib, hold.HeldBytes);
        Assert.Equal(16L << 20, ViewHold.MinFileBytes);
    }

    [Fact]
    public void WithoutRefillTheCheckDropsTheSegmentsTheMemoryManagerReducedInsteadOfReadingThemBack()
    {
        string a = Make("a.bin", 8);
        using var hold = new ViewHold(Counter(0), Counter(0), minFileBytes: 0);
        hold.Rebase();
        _ = hold.Hold([new ReadOp(a, 0, 8L * Mib)], CancellationToken.None);
        using Process me = Process.GetCurrentProcess();
        Assert.True(EmptyWorkingSet(me.Handle));

        HoldCheckResult r = hold.Recheck(Prewarm32Mib, CancellationToken.None, refill: false);

        Assert.True(r.DroppedBytes >= 4L * Mib, $"dropped {r.DroppedBytes}");
        Assert.Equal(0, r.RefilledBytes);
        Assert.True(hold.HeldBytes <= 4L * Mib);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WhenTheRefillCapIsHitTheSegmentsAreTakenOffTheHoldOnlyIfDroppingOnTheCapIsAsked(bool dropOnCap)
    {
        string a = Make("a.bin", 8);
        uint step = 0;
        uint faults = 5_000_000;
        uint pages = 5_000_000;
        using var hold = new ViewHold(() => faults += step, () => pages += step * 10, minFileBytes: 0);
        hold.Rebase();
        _ = hold.Hold([new ReadOp(a, 0, 8L * Mib)], CancellationToken.None);
        step = 100; // 이후의 조회마다 디스크에서 읽힌 것으로 보임: 다시 읽기가 상한에 닿음
        using Process me = Process.GetCurrentProcess();
        HoldCheckResult r = default!;
        for (int attempt = 0; attempt < 5 && !r.CapReached; attempt++)
        {
            Assert.True(EmptyWorkingSet(me.Handle));
            r = hold.Recheck(Mib, CancellationToken.None, dropOnCap: dropOnCap);
        }

        Assert.True(r.CapReached, $"trimmed {r.TrimmedBytes}, disk {r.RefilledBytes}");
        Assert.Equal(dropOnCap, r.DroppedBytes > 0);
        Assert.Equal(dropOnCap, hold.HeldBytes < 8L * Mib); // 거짓이면 상한에 닿아도 구간은 그대로 붙들고 있음
    }

    [Fact]
    public void ACanceledTokenHoldsNothing()
    {
        string a = Make("a.bin", 2);
        using var hold = new ViewHold(minFileBytes: 0);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _ = hold.Hold([new ReadOp(a, 0, 2L * Mib)], cts.Token);

        Assert.Equal(0, hold.HeldBytes);
    }

    [Fact]
    public void HoldingCheckingAndReleasingSomeTensOfMiBFinishesQuickly()
    {
        string a = Make("a.bin", 32);
        string b = Make("b.bin", 32);
        using var hold = new ViewHold(Counter(0), Counter(0), minFileBytes: 0); // 가짜 누계: 다른 시험이 내는 읽기가 경계로 보이지 않게 (조회 비용은 실제 호출 시간에 들어 있음)
        hold.Rebase();
        long t0 = Stopwatch.GetTimestamp();
        HoldBatchResult held = hold.Hold([new ReadOp(a, 0, 32L * Mib), new ReadOp(b, 0, 32L * Mib)], CancellationToken.None);
        TimeSpan holdTime = Stopwatch.GetElapsedTime(t0);
        hold.Rebase();
        HoldCheckResult check = hold.Recheck(Prewarm32Mib, CancellationToken.None);
        long t1 = Stopwatch.GetTimestamp();
        long released = hold.Release(64L * Mib);
        TimeSpan releaseTime = Stopwatch.GetElapsedTime(t1);

        Assert.Equal(64L * Mib, held.Bytes);
        Assert.Equal(64L * Mib, released);
        Assert.True(holdTime.TotalSeconds < 30 && check.Seconds < 30 && releaseTime.TotalSeconds < 30); // 값은 보고서용으로 따로 재고, 여기서는 터무니없이 느리지 않은지만 봄
    }
}
