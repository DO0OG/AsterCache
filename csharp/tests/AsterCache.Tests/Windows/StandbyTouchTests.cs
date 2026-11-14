using AsterCache.Core.Prefetch;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 디스크 읽기 감지: 뷰마다 페이지 읽기 누계의 증가분으로 판정 (가짜 누계), 누계가 없을 때의 시간 기준. 파일은 임시 폴더의 작은 파일 (캐시에 있음).
public sealed class StandbyTouchTests : IDisposable
{
    private const int Mib = 1 << 20;
    private readonly TempDir _dir = new();
    private readonly string _path;

    public StandbyTouchTests()
    {
        _ = _dir.WriteRandom("t.bin", 2 * Mib);
        _path = _dir.Combine("t.bin");
    }

    public void Dispose() => _dir.Dispose();

    // 범위마다 하나의 뷰: 2 MiB 범위 n 개. 누계는 뷰 전후 두 번 읽히고, 두 번째 읽기에 perView 쪽을 더함 (null 이면 조회 불가).
    private TouchResult Run(int ranges, uint? perView)
    {
        uint value = 1_000_000;
        int calls = 0;
        Func<uint?> counter = () =>
        {
            if (perView is null)
            {
                return null;
            }

            if (++calls % 2 == 0)
            {
                value += perView.Value;
            }

            return value;
        };
        return StandbyTouch.Run([.. Enumerable.Repeat(new ReadOp(_path, 0, 2 * Mib), ranges)], CancellationToken.None, counter);
    }

    [Fact]
    public void ViewsThatDoNotRaiseThePageReadCountAreInMemoryAndNeverStop()
    {
        TouchResult r = Run(40, 0);

        Assert.Equal(TouchEnd.Completed, r.End);
        Assert.Equal(40L * 2 * Mib, r.Bytes);
        Assert.Equal(0, r.DiskBytes);
    }

    [Fact]
    public void AFewPagesReadByOtherProcessesAreNotCountedAsDiskReads()
    {
        TouchResult r = Run(40, StandbyTouch.NoisePages); // 뷰마다 허용치 이하

        Assert.Equal(TouchEnd.Completed, r.End);
        Assert.Equal(0, r.DiskBytes);
    }

    [Fact]
    public void ViewsReadFromDiskStopOnceMoreThan32MiBAccumulated()
    {
        TouchResult r = Run(40, 512); // 2 MiB 뷰 전체가 디스크에서 읽힘

        Assert.Equal(TouchEnd.NeedsDiskReads, r.End);
        Assert.True(r.DiskBytes > StandbyTouch.DiskReadStopBytes);
        Assert.InRange(r.Bytes, 34L * Mib, 44L * Mib); // 허용치만큼 덜 세어도 몇 뷰 안에 멈춤
    }

    [Fact]
    public void ViewsThatAreOnlyPartlyReadFromDiskStillAddUpAndStop()
    {
        TouchResult r = Run(400, 256); // 뷰마다 절반만 디스크에서 읽힘: 뷰 단위 판정이면 놓치던 경우

        Assert.Equal(TouchEnd.NeedsDiskReads, r.End);
        Assert.True(r.DiskBytes > StandbyTouch.DiskReadStopBytes);
        Assert.True(r.Bytes < 400L * 2 * Mib);
    }

    [Fact]
    public void WithoutAPageReadCountTheViewTimeRuleIsUsedInstead()
    {
        TouchResult r = Run(20, null); // 캐시에 있는 뷰는 빠름

        Assert.Equal(TouchEnd.Completed, r.End);
        Assert.Equal(0, r.DiskBytes);
        Assert.False(StandbyTouch.ViewTooSlow(30, 64L << 20)); // 캐시: 64 MiB 에 약 30 ms
        Assert.True(StandbyTouch.ViewTooSlow(300, 64L << 20)); // HDD: 250 ms 이상
        Assert.False(StandbyTouch.ViewTooSlow(3, 128 << 10)); // 작은 뷰는 하한 4 ms
        Assert.True(StandbyTouch.ViewTooSlow(5, 128 << 10));
    }

    private const uint Archive = 0x20;
    private const uint Sparse = FileGuards.SparseAttribute;

    [Fact]
    public void ARangeOfASparseFileIsSkippedBeforeAnySectionIsOpenedAndCountedApartFromTouchedBytesAndErrors()
    {
        string notThere = _dir.Combine("sparse.bin"); // 열려고 하면 오류로 셈: 오류 0 이면 열지 않은 것
        var asked = new List<string>();

        TouchResult r = StandbyTouch.Run([new ReadOp(notThere, 0, 2 * Mib), new ReadOp(notThere, 4 * Mib, 3 * Mib)], CancellationToken.None, () => 1_000_000, path =>
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

        TouchResult r = StandbyTouch.Run([new ReadOp(sparse, 0, 2 * Mib), new ReadOp(_path, 0, 2 * Mib), new ReadOp(sparse, 0, Mib), new ReadOp(_path, 0, Mib)], CancellationToken.None, () => 1_000_000, path =>
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

        TouchResult r = StandbyTouch.Run([new ReadOp(_path, 0, 2 * Mib), new ReadOp(notThere, 0, Mib)], CancellationToken.None, () => 1_000_000, broken);

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

        TouchResult r = StandbyTouch.Run([.. paths.Select(p => new ReadOp(p, 0, Mib)), new ReadOp(paths[0], 0, Mib)], CancellationToken.None, () => 1_000_000, path =>
        {
            asked.Add(path);
            return Archive;
        });

        Assert.Equal(71, r.Errors);
        Assert.Equal(paths, asked); // 비운 뒤에 다시 만난 첫 파일도 다시 조회하지 않음
    }

    [Theory]
    [InlineData(0u, 1 << 20, 0.0, 0L)]
    [InlineData(2u, 1 << 20, 0.0, 0L)] // 허용치 이하
    [InlineData(3u, 1 << 20, 0.0, 4096L)]
    [InlineData(258u, 1 << 20, 0.0, 1L << 20)]
    [InlineData(10000u, 1 << 20, 0.0, 1L << 20)] // 뷰 크기를 넘지 않음
    [InlineData(2050u, 64 << 20, 1000.0, 0L)] // 1 초 동안의 허용치 2 + 2048 쪽
    [InlineData(2051u, 64 << 20, 1000.0, 4096L)]
    [InlineData(5u, 0, 0.0, 0L)]
    public void DiskBytesAreThePagesReadBeyondTheNoiseAllowanceCappedAtTheView(uint pagesRead, long viewBytes, double ms, long expected) =>
        Assert.Equal(expected, StandbyTouch.DiskBytesOf(pagesRead, viewBytes, ms));

    [Fact]
    public void ACounterThatWrapsAroundIsDifferencedAsUnsigned()
    {
        uint before = uint.MaxValue - 10;
        uint after = unchecked(before + 600); // 넘쳐서 작아짐

        Assert.Equal(2L * Mib, StandbyTouch.DiskBytesOf(unchecked(after - before), 2 * Mib, 0));
    }
}
