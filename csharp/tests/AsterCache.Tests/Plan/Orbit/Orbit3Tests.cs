using System.Diagnostics;
using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Prefetch;
using Xunit;

namespace AsterCache.Tests.Plan.Orbit;

public sealed class Orbit3Tests
{
    [Fact]
    public void LambdaIntervalChecksAnInteriorMinimum()
    {
        (double Coefficient, double Candidate, double Incumbent)[] terms =
        [
            (4.348919746898806, 6.782645821812131, 1.0402842521326883),
            (3.061140225481307, 0.4170940441863724, 8.98594583810125)
        ];

        double At(double lambda) => terms.Sum(term => term.Coefficient
            * (Math.Exp(-lambda * term.Candidate) - Math.Exp(-lambda * term.Incumbent)));
        double endpointMinimum = Math.Min(At(0.001), At(10));
        double interior = At(0.08);
        double closedLower = terms.Sum(term => term.Coefficient
            * Orbit3.MinimumExponentialDifference(0.001, 10, term.Candidate, term.Incumbent));

        Assert.True(endpointMinimum > 0);
        Assert.True(interior < 0);
        Assert.True(closedLower <= 0);
    }

    [Fact]
    public void DecideKeepsTheIncumbentWhenTheClosedBoundHasAnInteriorMinimum()
    {
        Orbit3Input input = new(
            SegmentSet([10, 1], headBytes: 1),
            [
                new Orbit3Episode([(0, 10)], 0.01, 0.01, 0.01),
                new Orbit3Episode([(1, 1)], 0.7, 0.7, 0.7)
            ],
            0.001,
            0.5,
            1,
            new Orbit3Device(0, 1, 1, 1, 1, 1),
            0.9);

        double At(double lambda) => 0.01 * (Math.Exp(-lambda * 12) - Math.Exp(-lambda))
            + 0.7 * (Math.Exp(-lambda * 2) - Math.Exp(-lambda * 11));
        Orbit3Decision decision = Orbit3.Decide(input);

        Assert.True(At(input.LambdaLow) > 0);
        Assert.True(At(input.LambdaHigh) > 0);
        Assert.Null(decision.Order);
        Assert.Equal("no_safe_gain", decision.Reason);
        Assert.True(decision.DLow <= 0);
    }

    [Fact]
    public void ZeroHazardKeepsTheOriginalOrder()
    {
        Orbit3Decision decision = Orbit3.Decide(GainInput(lambdaLow: 0, lambdaHat: 0, lambdaHigh: 0));

        Assert.Null(decision.Order);
        Assert.Equal("no_safe_gain", decision.Reason);
        Assert.True(decision.DLow.HasValue);
        Assert.True(decision.DLow.Value <= 0);
    }

    [Fact]
    public void FixedEnvelopeMatchesTheHandCalculatedBound()
    {
        Orbit3Decision decision = Orbit3.Decide(GainInput());
        double expected = Math.Exp(-0.5) - Math.Exp(-5)
            + 0.01 * (Math.Exp(-5.5) - 1);

        Assert.Equal(new[] { 1, 0 }, decision.Order);
        Assert.True(decision.DLow.HasValue);
        Assert.InRange(Math.Abs(decision.DLow!.Value - expected), 0, 1e-9);
    }

    [Fact]
    public void IncreasingReadTimesDoesNotIncreaseTheLowerBound()
    {
        Orbit3Decision faster = Orbit3.Decide(GainInput(bytesPerMs: 100));
        Orbit3Decision slower = Orbit3.Decide(GainInput(bytesPerMs: 50));
        Orbit3Decision slowest = Orbit3.Decide(GainInput(bytesPerMs: 25));

        Assert.True(faster.DLow.HasValue);
        Assert.True(slower.DLow.HasValue);
        Assert.True(slowest.DLow.HasValue);
        Assert.True(faster.DLow!.Value >= slower.DLow!.Value);
        Assert.True(slower.DLow.Value >= slowest.DLow!.Value);
    }

    [Theory]
    [InlineData(0.01, 1, 5)]
    [InlineData(0, 0.5, 3)]
    [InlineData(0.2, 2, 1)]
    public void IncreasingCandidateTimeDoesNotIncreaseTheExponentialLowerBound(
        double lambdaLow,
        double lambdaHigh,
        double incumbentTime)
    {
        double[] bounds = new[] { 1d, 2d, 4d, 8d }
            .Select(candidateTime => Orbit3.MinimumExponentialDifference(
                lambdaLow, lambdaHigh, candidateTime, incumbentTime))
            .ToArray();

        for (int index = 1; index < bounds.Length; index++)
        {
            Assert.True(bounds[index] <= bounds[index - 1]);
        }
    }

    [Fact]
    public void HighValueLaterSegmentMovesToTheFrontOnlyWithASafeGain()
    {
        Orbit3Decision decision = Orbit3.Decide(GainInput());

        Assert.Equal(new[] { 1, 0 }, decision.Order);
        Assert.True(decision.DLow > 0);
        Assert.True(decision.NominalGain > 0);
    }

    [Fact]
    public void DeviceEnvelopeUsesSeekAndThroughputCorners()
    {
        Orbit3Input pointInput = GainInput() with
        {
            Device = new Orbit3Device(2, 100, 1, 1, 1, 1)
        };
        Orbit3Input envelopeInput = pointInput with
        {
            Device = new Orbit3Device(2, 100, 0.5, 2, 0.5, 2)
        };

        Orbit3Decision point = Orbit3.Decide(pointInput);
        Orbit3Decision envelope = Orbit3.Decide(envelopeInput);

        Assert.True(point.DLow.HasValue);
        Assert.True(envelope.DLow.HasValue);
        Assert.True(envelope.DLow.Value < point.DLow.Value);
    }

    [Fact]
    public void EpisodesWithoutMappedPartsAreSkipped()
    {
        Orbit3Input valid = GainInput();
        Orbit3Input withEmpty = valid with
        {
            Episodes = valid.Episodes.Prepend(new Orbit3Episode(
                Array.Empty<(int Segment, long Bytes)>(), 0, 0, 1)).ToArray()
        };

        Orbit3Decision decision = Orbit3.Decide(withEmpty);
        Orbit3Decision expected = Orbit3.Decide(valid);

        Assert.Equal(expected.Order, decision.Order);
        Assert.Equal(expected.DLow, decision.DLow);
    }

    [Fact]
    public void EmptyMappedEpisodesKeepTheIncumbent()
    {
        Orbit3Input input = GainInput() with
        {
            Episodes = [new Orbit3Episode(Array.Empty<(int Segment, long Bytes)>(), 0, 0, 0)]
        };

        Orbit3Decision decision = Orbit3.Decide(input);

        Assert.Null(decision.Order);
        Assert.Equal("no_episodes", decision.Reason);
    }

    [Fact]
    public void EpisodeAfterTheCandidateLimitCanRejectTheCandidate()
    {
        Orbit3Episode[] episodes = Enumerable.Repeat(
                new Orbit3Episode([(1, 100_000)], 0.001, 0.001, 0.001), 96)
            .Append(new Orbit3Episode([(0, 1)], 1, 1, 1))
            .ToArray();
        Orbit3Input input = new(
            SegmentSet([1_000_000, 100_000]),
            episodes,
            0.5,
            0.5,
            0.5,
            new Orbit3Device(0, 100_000, 1, 1, 1, 1),
            0.9);

        Orbit3Decision decision = Orbit3.Decide(input);

        Assert.Null(decision.Order);
        Assert.Equal("no_safe_gain", decision.Reason);
        Assert.True(decision.DLow <= 0);
    }

    [Fact]
    public void EpisodeCountAboveTheSafetyLimitKeepsTheIncumbent()
    {
        Orbit3Episode episode = GainInput().Episodes[0];
        Orbit3Input input = GainInput() with
        {
            Episodes = Enumerable.Repeat(episode, 4097).ToArray()
        };

        Orbit3Decision decision = Orbit3.Decide(input);

        Assert.Null(decision.Order);
        Assert.Equal("too_many_episodes", decision.Reason);
    }

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void InvalidInputsKeepTheIncumbent(Orbit3Input input)
    {
        Orbit3Decision decision = Orbit3.Decide(input);

        Assert.Null(decision.Order);
        Assert.Equal("invalid_input", decision.Reason);
    }

    public static IEnumerable<object[]> InvalidInputs()
    {
        Orbit3Input valid = GainInput();
        yield return [valid with { Device = null }];
        yield return [valid with { LambdaHat = double.NaN }];
        yield return [valid with { LambdaLow = -1 }];
        yield return [valid with { LambdaLow = 2, LambdaHat = 1, LambdaHigh = 1 }];
        yield return [valid with { Episodes = Array.Empty<Orbit3Episode>() }];
        yield return [valid with { Episodes = [new Orbit3Episode([(0, 1000)], -0.1, 0.2, 0.3)] }];
        yield return [valid with { Episodes = [new Orbit3Episode([(0, 1000)], 0.3, 0.2, 0.4)] }];
        yield return [valid with { Episodes = [new Orbit3Episode([(0, 1000)], 0.2, 0.4, 1.01)] }];
        yield return [valid with { Device = valid.Device! with { SeekMax = 11 } }];
        yield return [valid with { Device = valid.Device! with { SeekMin = 1.1 } }];
        yield return [valid with { Device = valid.Device! with { ThroughputMin = 0 } }];
        yield return [valid with { Device = valid.Device! with { ThroughputMax = 11 } }];
        yield return [valid with { Device = valid.Device! with { SeekMs = double.NaN } }];
        yield return [valid with { Device = valid.Device! with { BytesPerMs = double.NaN } }];
        yield return [valid with { ModelConfidence = 0.5 }];
        yield return [valid with { Set = SegmentSet(1) }];
    }

    [Fact]
    public void CancelledTokenKeepsTheIncumbent()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Orbit3Decision decision = Orbit3.Decide(GainInput(), cancellation.Token);

        Assert.Null(decision.Order);
        Assert.Equal("cancelled", decision.Reason);
    }

    [Fact]
    public void DecisionBudgetKeepsTheIncumbent()
    {
        Orbit3Input input = GainInput() with { Episodes = new SlowEpisodeList(GainInput().Episodes[0]) };

        Orbit3Decision decision = Orbit3.Decide(input);

        Assert.Null(decision.Order);
        Assert.Equal("timeout", decision.Reason);
    }

    [Fact]
    public void BlockMappingSplitsAtOperationAndHeadBoundaries()
    {
        ReadOp[] ops =
        [
            new("a", 0, 32 * 1024),
            new("a", 32 * 1024, 16 * 1024),
            new("a", 48 * 1024, 48 * 1024)
        ];
        OrbitSegmentSet set = OrbitSegments.Build(ops, 32 * 1024, 64 * 1024);

        var mapped = Orbit3.MapBlocks(set, [("a", 0), ("a", 1), ("missing", 0)]);

        Assert.Equal(32 * 1024, mapped.HeadBytes);
        Assert.Equal(1, mapped.Unmatched);
        Assert.Equal(new[] { (0, 64L * 1024) }, mapped.Parts);
    }

    [Fact]
    public void CompressedBlockRangesMatchTheExpandedBlockMapping()
    {
        ReadOp[] ops =
        [
            new("a", 0, 32 * 1024),
            new("a", 32 * 1024, 16 * 1024),
            new("a", 48 * 1024, 48 * 1024)
        ];
        OrbitSegmentSet set = OrbitSegments.Build(ops, 32 * 1024, 64 * 1024);
        var expanded = Orbit3.MapBlocks(set, [("a", 0), ("a", 1), ("a", 0), ("missing", 0)]);
        Orbit3BlockMapping compressed = Assert.Single(Orbit3.MapBlockRanges(set,
        [
            [new OrbitBlockRange("a", 0, 2), new OrbitBlockRange("missing", 0, 1)]
        ]));

        Assert.Equal(expanded.Parts, compressed.Parts);
        Assert.Equal(expanded.HeadBytes, compressed.HeadBytes);
        Assert.Equal(expanded.Unmatched, compressed.Unmatched);
        Assert.Equal(3, compressed.Blocks);
    }

    [Fact]
    public void BlockWithoutAHeadOrSegmentIsUnmatched()
    {
        OrbitSegmentSet set = new(
            [new ReadOp("a", 0, 64 * 1024)],
            0,
            0,
            Array.Empty<OrbitSegment>());

        var mapped = Orbit3.MapBlocks(set, [("a", 0)]);

        Assert.Empty(mapped.Parts);
        Assert.Equal(1, mapped.Unmatched);
    }

    [Fact]
    public void BlockWithAnUnassignedOperationIsUnmatched()
    {
        OrbitSegmentSet set = new(
            [new ReadOp("a", 0, 32 * 1024), new ReadOp("a", 32 * 1024, 32 * 1024)],
            0,
            0,
            [new OrbitSegment(0, 1, 32 * 1024, 0, 0)]);

        var mapped = Orbit3.MapBlocks(set, [("a", 0)]);

        Assert.Equal(new[] { (0, 32L * 1024) }, mapped.Parts);
        Assert.Equal(1, mapped.Unmatched);
    }

    [Fact]
    public void CancelledBlockMappingReturnsEveryBlockAsUnmatched()
    {
        ReadOp[] ops = [new("a", 0, 128 * 1024)];
        OrbitSegmentSet set = OrbitSegments.Build(ops, 0, 128 * 1024);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var mapped = Orbit3.MapBlocks(set, [("a", 0), ("a", 0), ("a", 1)], cancellation.Token);

        Assert.Empty(mapped.Parts);
        Assert.Equal(0, mapped.HeadBytes);
        Assert.Equal(3, mapped.Unmatched);
    }

    [Fact]
    public void LargeBlockMappingUsesTheOffsetIndex()
    {
        const int operationCount = 5000;
        const int blockCount = 200_000;
        const long blockBytes = 64L * 1024;
        ReadOp[] ops = Enumerable.Range(0, operationCount)
            .Select(index => new ReadOp("large", index * blockBytes, blockBytes))
            .ToArray();
        OrbitSegmentSet set = new(
            ops,
            0,
            0,
            [new OrbitSegment(0, operationCount, operationCount * blockBytes, 0, 0)]);
        (string Path, long Block)[] blocks = Enumerable.Range(0, blockCount)
            .Select(index => ("large", (long)index))
            .ToArray();
        var timer = Stopwatch.StartNew();

        var mapped = Orbit3.MapBlocks(set, blocks);

        timer.Stop();
        Assert.Single(mapped.Parts);
        Assert.Equal(operationCount * blockBytes, mapped.Parts[0].Bytes);
        Assert.Equal(blockCount - operationCount, mapped.Unmatched);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1), $"매핑 소요 시간: {timer.Elapsed}.");
    }

    private static Orbit3Input GainInput(double bytesPerMs = 100, double lambdaLow = 0.5, double lambdaHat = 0.5, double lambdaHigh = 0.5)
    {
        return new Orbit3Input(
            SegmentSet(2),
            [
                new Orbit3Episode([(0, 1000)], 0.01, 0.01, 0.01),
                new Orbit3Episode([(1, 100)], 1, 1, 1)
            ],
            lambdaLow,
            lambdaHat,
            lambdaHigh,
            new Orbit3Device(0, bytesPerMs, 1, 1, 1, 1),
            0.9);
    }

    private static OrbitSegmentSet SegmentSet(int count)
    {
        return SegmentSet(count == 1 ? [1000L] : [1000L, 100L]);
    }

    private static OrbitSegmentSet SegmentSet(long[] sizes, long headBytes = 0)
    {
        ReadOp[] ops = sizes.Select((bytes, index) => new ReadOp("file", index == 0 ? 0 : sizes[0], bytes)).ToArray();
        var segments = sizes.Select((bytes, index) => new OrbitSegment(index, 1, bytes, sizes.Take(index).Sum(), 0)).ToArray();
        return new OrbitSegmentSet(ops, 0, headBytes, segments);
    }

    private sealed class SlowEpisodeList(Orbit3Episode episode) : IReadOnlyList<Orbit3Episode>
    {
        private bool _delayed;

        public int Count => 1;

        public Orbit3Episode this[int index]
        {
            get
            {
                if (index != 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(index));
                }

                if (!_delayed)
                {
                    _delayed = true;
                    Thread.Sleep(600);
                }

                return episode;
            }
        }

        public IEnumerator<Orbit3Episode> GetEnumerator() => new[] { episode }.AsEnumerable().GetEnumerator();

        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
