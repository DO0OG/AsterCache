using AsterCache.Core.Plan;
using Xunit;

namespace AsterCache.Tests.Core;

/// 존 크기 상한의 자동 계산: 물리 RAM 의 1/4 을 2048..8192 MiB 로 자름.
public sealed class ZoneSizeAutoTests
{
    private const long Gib = 1L << 30;

    [Theory]
    [InlineData(0L, 2048L)]
    [InlineData(4L, 2048L)] // 1 GiB/4 = 1 GiB 라 하한
    [InlineData(8L, 2048L)] // 8 GiB/4 = 2048
    [InlineData(16L, 4096L)]
    [InlineData(24L, 6144L)]
    [InlineData(32L, 8192L)]
    [InlineData(64L, 8192L)] // 상한
    public void AutoMaxMibIsAQuarterOfRamWithinTheLimits(long ramGib, long expectedMib) => Assert.Equal(expectedMib, ZoneSize.AutoMaxMib(ramGib * Gib));

    [Fact]
    public void AutoMaxMibRoundsDownInsideTheRange() => Assert.Equal(3000L, ZoneSize.AutoMaxMib((3000L * 4 * (1L << 20)) + 12345));

    [Theory]
    [InlineData(32L, null, 2048L)] // SMR·USB·종류 미확인
    [InlineData(32L, 15L, 2048L)] // SMR 아님이지만 여유가 4096 MiB 존의 4 배 (16 GiB) 미만
    [InlineData(32L, 16L, 4096L)]
    [InlineData(32L, 500L, 4096L)]
    [InlineData(16L, 500L, 4096L)] // RAM 규칙과 같음
    [InlineData(8L, 500L, 2048L)] // RAM 규칙이 더 작음
    public void AutoMaxMibWithDiskTakesTheSmallerOfRamAndDiskRule(long ramGib, long? freeGib, long expectedMib) =>
        Assert.Equal(expectedMib, ZoneSize.AutoMaxMib(ramGib * Gib, freeGib * Gib));
}
