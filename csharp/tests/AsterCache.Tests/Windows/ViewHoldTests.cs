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

        Assert.True(QueryWorkingSetEx(Process.GetCurrentProcess().Handle, info, pages * Marshal.SizeOf<WorkingSetEx>()));
        return (info.Count(e => (e.Attributes & 1) != 0), pages);
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
        using var hold = new ViewHold(Counter(0), Counter(0));
        hold.Rebase();

        HoldBatchResult r = hold.Hold([new ReadOp(a, 0, 2L * Mib), new ReadOp(b, 0, 2L * Mib), new ReadOp(a, 0, 2L * Mib), new ReadOp(a, Mib, 2L * Mib)], CancellationToken.None);

        Assert.Equal(5L * Mib, hold.HeldBytes); // 같은 구간은 두 번 붙들지 않음
        Assert.Equal(5L * Mib, r.Bytes);
        Assert.Equal(-1, r.BoundaryIndex);
        Assert.Equal(2, hold.Files);
        Assert.Equal(2, hold.Views); // 파일마다 뷰 하나
        foreach ((nint address, long length) in hold.LiveRanges())
        {
            (int valid, int total) = Resident(address, length);
            Assert.Equal(total, valid); // 뷰를 닫지 않아 읽은 쪽이 모두 작업 집합에 있음
        }
    }

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EmptyWorkingSet(nint process);

    [Fact]
    public void TheCheckLooksUpTheWorkingSetWithoutReadingAndRefillsNothingWhileEverythingIsResident()
    {
        string a = Make("a.bin", 8);
        using var hold = new ViewHold(Counter(0), Counter(0)); // 가짜 누계: 다른 시험의 읽기가 판정에 섞이지 않게
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
        using var hold = new ViewHold(Counter(0), Counter(0));
        hold.Rebase();
        _ = hold.Hold([new ReadOp(a, 0, 8L * Mib)], CancellationToken.None);
        Assert.True(EmptyWorkingSet(Process.GetCurrentProcess().Handle)); // 메모리 관리자가 작업 집합을 줄이는 것을 흉내 냄: 붙든 쪽이 Valid 에서 빠짐 (페이지는 대기 목록에 있어 디스크 읽기는 없음)

        HoldCheckResult r = hold.Recheck(Prewarm32Mib, CancellationToken.None);

        Assert.True(r.TrimmedBytes >= 4L * Mib, $"trimmed {r.TrimmedBytes}"); // 대부분이 빠짐
        Assert.Equal(8L * Mib, hold.HeldBytes); // 상한 안: 목록에서 빼지 않음
        (nint address, long length) = hold.LiveRanges().Single();
        (int valid, int total) = Resident(address, length);
        Assert.True(valid >= total * 9 / 10, $"valid {valid} of {total}"); // 빠진 쪽을 다시 읽어 작업 집합에 올림
    }

    private const long Prewarm32Mib = 32L << 20;

    [Fact]
    public void ReleaseTakesTheLeastImportantFirstAndTakesThePagesOutOfTheWorkingSet()
    {
        string a = Make("a.bin", 4);
        string b = Make("b.bin", 4);
        using var hold = new ViewHold();
        hold.Rebase();
        _ = hold.Hold([new ReadOp(a, 0, 4L * Mib), new ReadOp(b, 0, 4L * Mib)], CancellationToken.None);
        IReadOnlyList<(nint Address, long Length)> before = hold.LiveRanges();

        long released = hold.Release(2L * Mib);

        Assert.Equal(2L * Mib, released);
        Assert.Equal(6L * Mib, hold.HeldBytes);
        IReadOnlyList<(nint Address, long Length)> after = hold.LiveRanges();
        Assert.Equal(before[0], after[0]); // 먼저 붙든 파일 a 는 그대로
        Assert.Equal(2L * Mib, after[1].Length); // 나중에 붙든 b 의 뒤쪽이 줄어듦
        (int validA, int totalA) = Resident(after[0].Address, after[0].Length);
        Assert.Equal(totalA, validA);
        (int validTail, int totalTail) = Resident(before[1].Address + (2 * Mib), 2L * Mib);
        Assert.Equal(0, validTail); // 내놓은 쪽은 작업 집합에서 빠짐 (VirtualUnlock 이 FALSE 와 오류 158 을 돌려줘도)
        Assert.Equal(512, totalTail);
        Assert.Equal(4L * Mib + 2L * Mib, hold.HeldBytes);
    }

    [Fact]
    public void WhileHeldAnotherHandleCanOverwriteTheSameSizeGrowAndDeleteButShrinkingFailsWithErrorOf1224()
    {
        string a = Make("a.bin", 4);
        string c = Make("c.bin", 4);
        using var hold = new ViewHold();
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
        using var hold = new ViewHold();
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

    [Fact]
    public void DisposeReleasesEverythingAndLeavesNoSectionOrViewSoShrinkingWorks()
    {
        string a = Make("a.bin", 4);
        var hold = new ViewHold();
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
        using var hold = new ViewHold(Counter(100), Counter(1000));
        hold.Rebase();

        HoldBatchResult r = hold.Hold([new ReadOp(a, 0, 4L * Mib), new ReadOp(b, 0, 4L * Mib)], CancellationToken.None);

        Assert.Equal(1, r.BoundaryIndex); // 배치 끝의 조회에서 경계로 봄
        Assert.True(r.DiskBytes > 0);
        Assert.Equal(100, r.HardFaults);
        Assert.Equal(8L * Mib, hold.HeldBytes); // 읽은 것은 붙듦 (디스크에서 읽은 것도 이제 메모리에 있음)
    }

    [Fact]
    public void ASparseFileIsNotHeldAndAMissingFileCountsAsAnErrorWhileTheRestIsHeld()
    {
        string a = Make("a.bin", 2);
        string s = Make("s.bin", 2);
        using var hold = new ViewHold(attributes: p => p == s ? FileGuards.SparseAttribute : 0);
        hold.Rebase();

        HoldBatchResult r = hold.Hold([new ReadOp(s, 0, 2L * Mib), new ReadOp(_dir.Combine("none.bin"), 0, Mib), new ReadOp(a, 0, 8L * Mib)], CancellationToken.None);

        Assert.Equal(2L * Mib, r.SkippedBytes);
        Assert.Equal(1, r.Errors);
        Assert.Equal(2L * Mib, hold.HeldBytes); // 길이는 파일 끝에서 잘림
        Assert.Equal(1, hold.Files);
    }

    [Fact]
    public void ACanceledTokenHoldsNothing()
    {
        string a = Make("a.bin", 2);
        using var hold = new ViewHold();
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
        using var hold = new ViewHold(Counter(0), Counter(0)); // 가짜 누계: 다른 시험이 내는 읽기가 경계로 보이지 않게 (조회 비용은 실제 호출 시간에 들어 있음)
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
