using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using AsterCache.Core.Plan.Orbit;
using Xunit;
using Xunit.Abstractions;

namespace AsterCache.Tests.Plan.Orbit;

public sealed class OrbitPlannerParityTests(ITestOutputHelper output)
{
    private static JsonDocument Fixture() => JsonDocument.Parse(File.ReadAllBytes(
        Path.Combine(AppContext.BaseDirectory, "fixtures", "orbit", "expected.json")));

    [Fact]
    public void ChooseMatchesPythonReferenceForFixedCases()
    {
        using JsonDocument document = Fixture();
        JsonElement cases = document.RootElement.GetProperty("cases");
        Assert.True(cases.GetArrayLength() >= 40);
        Assert.True(cases.EnumerateArray().Count(item => item.GetProperty("expected").GetProperty("selected").GetString() == "orbit") >= 10);
        int nonBitExactValues = 0;

        foreach (JsonElement item in cases.EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            OrbitDecision actual = Choose(item);
            JsonElement expected = item.GetProperty("expected");
            JsonElement expectedPlan = expected.GetProperty("plan");

            Assert.True(expected.GetProperty("selected").GetString() == actual.Selected, name + " selected");
            Assert.True(expected.GetProperty("reason").GetString() == actual.Reason, name + " reason");
            Assert.True(expectedPlan.GetProperty("source").GetString() == actual.Plan.Source, name + " source");
            Assert.True(expectedPlan.GetProperty("order").EnumerateArray().Select(value => value.GetString())
                .SequenceEqual(actual.Plan.Order), name + " plan order");
            Assert.True(expectedPlan.GetProperty("bytes_units").GetInt64() == actual.Plan.BytesUnits, name + " bytes");
            nonBitExactValues += AssertClose(expectedPlan.GetProperty("elapsed_ms"), actual.Plan.ElapsedMs, name + " elapsed") ? 1 : 0;
            nonBitExactValues += AssertClose(expectedPlan.GetProperty("predicted_proxy"), actual.Plan.PredictedProxy, name + " proxy") ? 1 : 0;
            nonBitExactValues += AssertNullableClose(expected.GetProperty("minimum_advantage"), actual.MinimumAdvantage, name + " minimum advantage") ? 1 : 0;
            nonBitExactValues += AssertClose(expected.GetProperty("prediction_advantage"), actual.PredictionAdvantage, name + " prediction advantage") ? 1 : 0;
        }

        string parity = $"ORBIT numeric fields differing by bits: {nonBitExactValues}";
        output.WriteLine(parity);
        Console.WriteLine(parity);
    }

    [Fact]
    public void FSumMatchesPythonMathFsumBitForBitForFixedInputs()
    {
        using JsonDocument document = Fixture();
        JsonElement cases = document.RootElement.GetProperty("fsum_cases");
        Assert.True(cases.GetArrayLength() >= 256);

        foreach (JsonElement item in cases.EnumerateArray())
        {
            string name = item.GetProperty("name").GetString()!;
            double[] values = item.GetProperty("values").EnumerateArray().Select(value => value.GetDouble()).ToArray();
            double expected = item.GetProperty("expected").GetDouble();
            double actual = OrbitPlanner.FSum(values);

            Assert.True(BitConverter.DoubleToInt64Bits(expected) == BitConverter.DoubleToInt64Bits(actual),
                $"{name}: expected {expected:R} ({BitConverter.DoubleToInt64Bits(expected):X16}), " +
                $"actual {actual:R} ({BitConverter.DoubleToInt64Bits(actual):X16})");
        }
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void UpperBoundInputPlansWithinTheLooseTimeLimit()
    {
        using JsonDocument document = Fixture();
        JsonElement item = document.RootElement.GetProperty("cases").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "upper-bounds");
        Stopwatch stopwatch = Stopwatch.StartNew();
        OrbitDecision decision = Choose(item);
        stopwatch.Stop();

        Assert.NotNull(decision);
        output.WriteLine($"ORBIT maximum-bound Choose: {stopwatch.Elapsed.TotalMilliseconds:F3} ms");
        Console.WriteLine($"ORBIT maximum-bound Choose: {stopwatch.Elapsed.TotalMilliseconds:F3} ms");
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"Planning took {stopwatch.Elapsed.TotalMilliseconds:F3} ms."); // 2 초 상한은 그림자 계획의 취소 토큰이 지킴 (이 단언은 머신 부하에 흔들리지 않게 느슨하게)
    }

    [Fact]
    public void ACancelledTokenStopsTheBeamSearchAndTheDefaultTokenChangesNothing()
    {
        using JsonDocument document = Fixture();
        JsonElement item = document.RootElement.GetProperty("cases").EnumerateArray()
            .Single(value => value.GetProperty("name").GetString() == "upper-bounds");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => Choose(item, cancelled.Token));
        Assert.Equal(Choose(item).Plan.Order, Choose(item, CancellationToken.None).Plan.Order);
    }

    [Fact]
    public void ReduceDropsOversizedBlocksAndKeepsTheTopRankedTwentyFour()
    {
        PlanningBlock[] blocks = Enumerable.Range(0, 30).Select(i => new PlanningBlock("b" + i, i == 2 ? 1_000_000_000_001L : 1, i, 1)).ToArray();
        PredictedEpisode[] episodes =
        [
            new(["b0", "b2"], 1, 0), // 뺀 블록 b2 를 요구하므로 통째로 버림
            new(["b2"], 1, 0), // 뺀 블록을 요구하므로 버림
            new(["b29"], 1, 0), // 상위 24개 밖이라 버림
            new(["b1", "b3"], 1, 0)
        ];
        var world = new PlanningWorld(blocks, episodes, new BigInteger(100), 1000, 1, 0, 0, 0, 0.9);

        (PlanningWorld? reduced, string suffix) = OrbitPlanner.Reduce(world);

        Assert.Equal("reduced(oversized=1,top24_of=29)", suffix.TrimStart(';'));
        Assert.Equal(24, reduced!.Blocks.Count);
        Assert.Equal("b0", reduced.Blocks[0].Key);
        Assert.DoesNotContain(reduced.Blocks, block => block.Key is "b2" or "b29");
        Assert.Single(reduced.Episodes);
        Assert.Equal(["b1", "b3"], reduced.Episodes[0].Requires);
        Assert.Equal("baseline", OrbitPlanner.Choose(reduced).Selected);
    }

    [Fact]
    public void ReduceLeavesAWorldWithinTheLimitsUntouched()
    {
        PlanningBlock[] blocks = Enumerable.Range(0, 24).Select(i => new PlanningBlock("b" + i, 1, i, 1)).ToArray();
        var world = new PlanningWorld(blocks, [new PredictedEpisode(["b0"], 1, 0)], new BigInteger(24), 1000, 1, 0, 0, 0, 0.9);

        (PlanningWorld? reduced, string suffix) = OrbitPlanner.Reduce(world);

        Assert.Same(world, reduced);
        Assert.Equal(string.Empty, suffix);
    }

    private static OrbitDecision Choose(JsonElement item, CancellationToken cancellation = default)
    {
        JsonElement rawWorld = item.GetProperty("world");
        PlanningWorld? world = rawWorld.ValueKind == JsonValueKind.Null ? null : ReadWorld(rawWorld);
        JsonElement rawUncertainty = item.GetProperty("uncertainty");
        Uncertainty? uncertainty = rawUncertainty.ValueKind == JsonValueKind.Null ? null : new Uncertainty(
            Number(rawUncertainty.GetProperty("value_low")), Number(rawUncertainty.GetProperty("value_high")),
            Number(rawUncertainty.GetProperty("partial_error")), rawUncertainty.GetProperty("calibration_version").GetString()!);
        JsonElement rawDevice = item.GetProperty("device");
        DeviceEnvelope? device = rawDevice.ValueKind == JsonValueKind.Null ? null : new DeviceEnvelope(
            Number(rawDevice.GetProperty("seek_min")), Number(rawDevice.GetProperty("seek_max")),
            Number(rawDevice.GetProperty("throughput_min")), Number(rawDevice.GetProperty("throughput_max")),
            rawDevice.GetProperty("calibration_version").GetString()!);

        return OrbitPlanner.Choose(world, uncertainty, device,
            item.GetProperty("width").GetInt32(),
            item.GetProperty("max_expansions").GetInt32(),
            item.GetProperty("partition_k").GetInt32(),
            item.GetProperty("candidate_set").GetBoolean(),
            cancellation);
    }

    private static PlanningWorld ReadWorld(JsonElement item)
    {
        PlanningBlock[] blocks = item.GetProperty("blocks").EnumerateArray().Select(block => new PlanningBlock(
            block.GetProperty("key").GetString()!,
            block.GetProperty("size").GetInt64(),
            Number(block.GetProperty("position")),
            Number(block.GetProperty("frequency")))).ToArray();
        PredictedEpisode[] episodes = item.GetProperty("episodes").EnumerateArray().Select(episode => new PredictedEpisode(
            episode.GetProperty("requires").EnumerateArray().Select(key => key.GetString()!).ToArray(),
            Number(episode.GetProperty("predicted")),
            Number(episode.GetProperty("predicted_partial")))).ToArray();

        return new PlanningWorld(
            blocks,
            episodes,
            BigInteger.Parse(item.GetProperty("budget").GetRawText(), CultureInfo.InvariantCulture),
            Number(item.GetProperty("timebox")),
            Number(item.GetProperty("transfer_units_per_ms")),
            Number(item.GetProperty("seek_ms")),
            Number(item.GetProperty("seek_gap")),
            Number(item.GetProperty("interruption_hazard")),
            Number(item.GetProperty("model_confidence")),
            item.GetProperty("disk_idle").GetBoolean(),
            item.GetProperty("disk_known").GetBoolean(),
            item.GetProperty("other_game_active").GetBoolean(),
            item.GetProperty("baseline_read_allowed").GetBoolean(),
            item.GetProperty("signature").GetString()!);
    }

    private static double Number(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Number)
        {
            return value.GetDouble();
        }

        return value.GetString() switch
        {
            "NaN" => double.NaN,
            "Infinity" => double.PositiveInfinity,
            "-Infinity" => double.NegativeInfinity,
            string text => double.Parse(text, CultureInfo.InvariantCulture),
            _ => throw new FormatException("Expected a numeric fixture value.")
        };
    }

    private static bool AssertNullableClose(JsonElement expected, double? actual, string context)
    {
        if (expected.ValueKind == JsonValueKind.Null)
        {
            Assert.Null(actual);
            return false;
        }

        Assert.NotNull(actual);
        return AssertClose(expected, actual!.Value, context);
    }

    private static bool AssertClose(JsonElement expected, double actual, string context)
    {
        double value = expected.GetDouble();
        if (BitConverter.DoubleToInt64Bits(value) == BitConverter.DoubleToInt64Bits(actual))
        {
            return false;
        }

        double tolerance = 1e-9 * Math.Max(1, Math.Abs(value));
        Assert.True(Math.Abs(value - actual) <= tolerance,
            $"{context}: expected {value:R}, actual {actual:R}, tolerance {tolerance:R}");
        return true;
    }
}
