using AsterCache.Core.Prefetch;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

/// 중요한 순서로 메모리에 있는 범위를 찾는 곳 (ResidentFinder): 가짜 조각 읽기 (메모리에 없는 구간을 읽으면 오래 걸리고 하드 폴트가 늘고 그 뒤 메모리에 있음) 로
/// 빠진 조각을 싸게 알아채는지, 건너뛰는 폭의 제한·확대·되돌림, 건너뛴 구간만 빠진 것으로 기억하는지, 디스크 읽기 상한을 확인합니다. 실제 파일 한 개로 실제 읽기 경로도 확인합니다.
public sealed class ResidentFinderTests
{
    private const long Mib = 1L << 20;

    // 가짜 메모리: 파일마다 길이와 메모리에 없는 구간. 없는 구간을 읽으면 12 ms 가 걸리고 64 KiB 블록마다 하드 폴트 3 회·페이지 읽기 16 쪽이 늘며 그 구간은 메모리에 올라옴.
    private sealed class FakeMemory : IPieceSource
    {
        public readonly Dictionary<string, long> Lengths = [];
        public readonly Dictionary<string, List<(long From, long To)>> Cold = [];
        public readonly HashSet<string> Sparse = [];
        public readonly List<(string Path, long Offset, long Length)> Reads = [];
        public uint Faults = 1000;
        public uint Pages = 1000;
        public bool Slow = true; // 없는 구간을 읽을 때 오래 걸리는가 (끄면 하드 폴트만 늘고 시간은 그대로: 빠짐을 시간으로 가를 수 없는 디스크)
        public long SlowWithoutFaults; // 이 파일 위치 이상은 하드 폴트 없이 느리기만 함 (CPU 경합 흉내): 0 이면 쓰지 않음
        public uint NoiseFaults; // 그 느린 조각을 읽을 때 서비스의 다른 스레드가 낸 폴트 (잡음)

        public int Maps => 0;

        public long Open(string path) => Sparse.Contains(path) ? IPieceSource.Sparse : Lengths.TryGetValue(path, out long length) ? length : IPieceSource.OpenFailed;

        public bool Read(string path, long offset, long length, out double ms)
        {
            Reads.Add((path, offset, length));
            ms = 0.3;
            long cold = 0;
            if (Cold.TryGetValue(path, out List<(long From, long To)>? ranges))
            {
                List<(long, long)> remaining = [];
                foreach ((long rf, long rt) in ranges)
                {
                    long from = Math.Max(offset, rf);
                    long to = Math.Min(offset + length, rt);
                    if (to <= from)
                    {
                        remaining.Add((rf, rt));
                        continue;
                    }

                    cold += to - from; // 읽어 온 구간은 메모리에 올라옴
                    if (rf < from)
                    {
                        remaining.Add((rf, from));
                    }

                    if (to < rt)
                    {
                        remaining.Add((to, rt));
                    }
                }

                ranges.Clear();
                ranges.AddRange(remaining);
            }

            if (cold > 0)
            {
                long blocks = (cold + 65535) / 65536;
                Faults += (uint)(blocks * 3);
                Pages += (uint)(blocks * 16);
                ms = Slow ? 12 : 0.3;
            }
            else if (SlowWithoutFaults > 0 && offset >= SlowWithoutFaults)
            {
                ms = 12; // 메모리에 있어도 느림 (CPU 경합): 하드 폴트는 늘지 않음 (잡음 폴트만)
                Faults += NoiseFaults;
            }

            return true;
        }

        public void Dispose()
        {
        }
    }

    private double _clock;

    private double _clockStep = 25;

    private double Clock() => _clock += _clockStep; // 기본은 조회 사이 최소 간격 (20 ms) 을 항상 채움

    private FindResult Find(FakeMemory memory, IReadOnlyList<ReadOp> ordered, long cap = 1L << 40, Action<long, double>? onGroup = null) =>
        ResidentFinder.Find(ordered, cap, CancellationToken.None, onGroup, () => memory.Faults, () => memory.Pages, memory, Clock);

    private static long Sum(IEnumerable<ReadOp> ops) => ops.Sum(o => o.Length);

    [Fact]
    public void EverythingInMemoryIsCollectedInOrderWithoutBoundaries()
    {
        var memory = new FakeMemory();
        memory.Lengths["a"] = 20 * Mib;
        memory.Lengths["b"] = 10 * Mib;

        FindResult r = Find(memory, [new ReadOp("b", 0, 10 * Mib), new ReadOp("a", 0, 20 * Mib)]);

        Assert.Equal([new ReadOp("b", 0, 10 * Mib), new ReadOp("a", 0, 20 * Mib)], r.Resident); // 이어진 조각은 합쳐지고 순서는 그대로
        Assert.Empty(r.Missing);
        Assert.Equal(0, r.Boundaries);
        Assert.Equal(0, r.DiskBytes);
        Assert.Equal(30 * Mib, r.Bytes);
        Assert.False(r.CapReached);
    }

    [Fact]
    public void AMissingPieceIsFoundAtOnePieceOfDiskReadNotAtEightMiB()
    {
        var memory = new FakeMemory();
        memory.Lengths["a"] = 64 * Mib;
        memory.Cold["a"] = [(20 * Mib, 64 * Mib)]; // 앞 20 MiB 만 메모리에 있음

        FindResult r = Find(memory, [new ReadOp("a", 0, 64 * Mib)], cap: Mib); // 첫 경계에서 상한에 닿음

        Assert.Equal(1, r.Boundaries);
        Assert.True(r.DiskBytes <= Mib, $"disk {r.DiskBytes}"); // 빠진 조각 하나 (1 MiB 이하) 만 읽고 알아챔
        Assert.Equal(21 * Mib, Sum(r.Resident)); // 앞 20 MiB + 디스크에서 읽어 온 조각 하나
        Assert.True(r.CapReached);
        Assert.True(r.LeftBytes >= 0);
    }

    [Fact]
    public void ASlowPieceWithoutAHardFaultIncreaseIsNotAMissingPiece()
    {
        var memory = new FakeMemory { SlowWithoutFaults = 10 * Mib }; // 10 MiB 뒤는 CPU 경합으로 느리기만 함
        memory.Lengths["a"] = 40 * Mib;

        FindResult r = Find(memory, [new ReadOp("a", 0, 40 * Mib)]);

        Assert.Equal(0, r.Boundaries);
        Assert.Empty(r.Missing);
        Assert.Equal(40 * Mib, Sum(r.Resident));
        Assert.True(r.Checks >= 20, $"checks {r.Checks}"); // 느린 조각마다 바로 조회했지만 증가분이 없어 빠짐으로 치지 않음
    }

    [Fact]
    public void WhenTheNextPointIsAlsoMissingItIsABoundaryTheWalkStopsAndTheRestIsLeftUnconfirmed()
    {
        var memory = new FakeMemory();
        memory.Lengths["a"] = 400 * Mib;
        memory.Cold["a"] = [(0, 400 * Mib)]; // 전부 빠져 있음

        FindResult r = Find(memory, [new ReadOp("a", 0, 400 * Mib)]);

        // 0 MiB 에서 빠짐을 확인 → 8 MiB 건너뜀 → 9 MiB 에서 또 빠짐 (이어진 두 지점) → 경계: 거기서 멈춤
        Assert.True(r.BoundaryHit);
        Assert.Equal([new ReadOp("a", 1 * Mib, 8 * Mib)], r.Missing); // 구멍으로 남는 것은 건너뛴 구간뿐
        Assert.Equal(2, r.Boundaries);
        Assert.InRange(r.LeftBytes, 380 * Mib, 392 * Mib); // 경계 뒤 (덜 중요한 쪽) 는 확인하지 않고 남김
        Assert.False(r.CapReached);
    }

    [Fact]
    public void ThePointAfterASkipIsCheckedEvenWhenTheClockBarelyMovesSoTwoMissesInARowStillMakeABoundary()
    {
        _clockStep = 2; // 빠진 조각을 읽는 시간이 조회 간격 (20 ms) 에 못 미치는 실제 HDD: 앞 조회 뒤 경과 시간이 20 ms 가 안 됨
        var memory = new FakeMemory();
        memory.Lengths["a"] = 400 * Mib;
        memory.Cold["a"] = [(0, 400 * Mib)];

        FindResult r = Find(memory, [new ReadOp("a", 0, 400 * Mib)]);

        Assert.True(r.BoundaryHit); // 건너뛴 다음 지점은 시간과 무관하게 조회: 또 빠짐이라 경계
        Assert.Equal([new ReadOp("a", 1 * Mib, 8 * Mib)], r.Missing);
        Assert.InRange(r.LeftBytes, 380 * Mib, 392 * Mib);
    }

    [Fact]
    public void AHoleFollowedByAPointInMemoryIsNotABoundaryAndTheWalkContinuesUntilTwoInARowAreMissing()
    {
        var memory = new FakeMemory();
        memory.Lengths["a"] = 100 * Mib;
        memory.Cold["a"] = [(10 * Mib, 12 * Mib), (50 * Mib, 100 * Mib)]; // 10~12 MiB 는 작은 구멍, 50 MiB 부터는 끝까지 빠짐

        FindResult r = Find(memory, [new ReadOp("a", 0, 100 * Mib)]);

        Assert.Equal(new ReadOp("a", 11 * Mib, 8 * Mib), r.Missing[0]); // 첫 빠짐 (10 MiB) 뒤 8 MiB 를 건너뜀
        Assert.Equal(new ReadOp("a", 51 * Mib, 8 * Mib), r.Missing[1]); // 구멍 뒤 지점이 메모리에 있어 계속 걸었고 두 번째 빠짐 뒤에도 같은 폭
        Assert.True(r.BoundaryHit); // 50 MiB 부터는 이어진 두 지점이 빠져 경계
        Assert.Equal(2, r.Missing.Count);
    }

    [Fact]
    public void AFewStrayFaultsFromOtherThreadsAreNotAMissingPiece()
    {
        var memory = new FakeMemory { SlowWithoutFaults = 10 * Mib, NoiseFaults = 5 }; // 느린 조각마다 다른 스레드 폴트 5 회: 문턱 8 보다 낮음
        memory.Lengths["a"] = 40 * Mib;

        FindResult r = Find(memory, [new ReadOp("a", 0, 40 * Mib)]);

        Assert.Equal(0, r.Boundaries);
        Assert.False(r.BoundaryHit);
        Assert.Empty(r.Missing);
        Assert.Equal(40 * Mib, Sum(r.Resident));
    }

    [Fact]
    public void SkippingContinuesAcrossTheNextRangesOfTheListAndNothingSkippedIsOpenedOrRead()
    {
        var memory = new FakeMemory();
        foreach (string f in new[] { "a", "b", "c", "d" })
        {
            memory.Lengths[f] = 4 * Mib;
        }

        memory.Cold["a"] = [(0, 4 * Mib)];
        memory.Cold["b"] = [(0, 4 * Mib)];
        memory.Cold["c"] = [(0, 4 * Mib)];
        memory.Cold["d"] = [(0, 4 * Mib)];

        FindResult r = Find(memory, [new ReadOp("a", 0, 4 * Mib), new ReadOp("b", 0, 4 * Mib), new ReadOp("c", 0, 4 * Mib), new ReadOp("d", 0, 4 * Mib)]);

        Assert.Contains(r.Missing, m => m.Path == "b"); // 여러 파일에 걸친 빈 곳도 목록 순서로 건너뜀
        Assert.DoesNotContain(memory.Reads, rd => rd.Path == "b"); // 건너뛴 파일은 읽지 않음
        Assert.True(Sum(r.Missing) >= 8 * Mib);
    }

    [Fact]
    public void TheDiskReadCapStopsTheFindAndLeavesTheRestUnconfirmed()
    {
        var memory = new FakeMemory();
        for (int i = 0; i < 20; i++)
        {
            memory.Lengths["f" + i] = 2 * Mib;
            memory.Cold["f" + i] = [(0, 2 * Mib)];
        }

        FindResult r = Find(memory, [.. Enumerable.Range(0, 20).Select(i => new ReadOp("f" + i, 0, 2 * Mib))], cap: Mib);

        Assert.True(r.CapReached);
        Assert.True(r.DiskBytes >= Mib && r.DiskBytes <= 2 * Mib, $"disk {r.DiskBytes}");
        Assert.True(r.LeftBytes > 0); // 확인하지 못하고 남긴 범위는 이번에 만지지 않음
    }

    [Fact]
    public void SparseAndUnopenableFilesAreSkippedAndCounted()
    {
        var memory = new FakeMemory();
        memory.Lengths["a"] = 4 * Mib;
        memory.Lengths["sp"] = 4 * Mib;
        memory.Sparse.Add("sp");

        FindResult r = Find(memory, [new ReadOp("sp", 0, 4 * Mib), new ReadOp("none", 0, Mib), new ReadOp("a", 0, 8 * Mib)]);

        Assert.Equal(4 * Mib, r.SparseBytes);
        Assert.Equal(1, r.Errors);
        Assert.Equal(4 * Mib, Sum(r.Resident)); // 길이는 파일 끝에서 잘림
    }

    [Fact]
    public void TheGroupCallbackIsCalledEveryHundredAndTwentyEightMiBAndTheBaselineIsTakenAgain()
    {
        var memory = new FakeMemory();
        memory.Lengths["a"] = 300 * Mib;
        List<long> groups = [];
        uint faultsAtGroup = 0;

        FindResult r = Find(memory, [new ReadOp("a", 0, 300 * Mib)], onGroup: (bytes, seconds) =>
        {
            groups.Add(bytes);
            faultsAtGroup = memory.Faults;
            memory.Faults += 500; // 잠금을 놓은 사이 다른 스레드가 낸 읽기: 기준값을 다시 잡으므로 이번 판정에 섞이지 않음
        });

        Assert.Equal([128 * Mib, 128 * Mib], groups);
        Assert.Equal(0, r.Boundaries);
        Assert.True(faultsAtGroup > 0);
    }

    [Fact]
    public void ARealFileIsReadThroughTheRealMappingAndEverythingIsCollected()
    {
        using var dir = new TempDir();
        _ = dir.WriteRandom("t.bin", 4 * (int)Mib);
        _ = File.ReadAllBytes(dir.Combine("t.bin")); // 캐시에 올림
        uint faults = 1000;

        FindResult r = ResidentFinder.Find([new ReadOp(dir.Combine("t.bin"), 0, 4 * Mib), new ReadOp(dir.Combine("none.bin"), 0, Mib)], 8 * Mib, CancellationToken.None, null, () => faults, () => faults, p => 0);

        Assert.Equal(4 * Mib, Sum(r.Resident));
        Assert.Equal(1, r.Errors); // 없는 파일
        Assert.Equal(0, r.Boundaries);
        Assert.True(r.Maps >= 1);
    }
}
