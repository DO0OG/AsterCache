using AsterCache.Core.Discovery;
using Xunit;

namespace AsterCache.Tests.Core;

public sealed class CapabilityDecisionTests
{
    private static readonly string?[] AntiCheatKinds =
    [
        null, "eac", "battleye", "vanguard", "kernel", "ricochet", "unknown", "other-anticheat",
        "EAC", "Battleye", "Vanguard", "Kernel", "Ricochet", "UNKNOWN", "",
    ];

    public static TheoryData<CapabilityFeature, string?, CapabilityOutcome> DecisionCases { get; } = CreateDecisionCases();

    [Theory]
    [MemberData(nameof(DecisionCases))]
    public void DecideReturnsTheFixedDecisionForEveryFeatureAndAntiCheatKind(CapabilityFeature feature, string? antiCheatKind, CapabilityOutcome expected)
    {
        CapabilityDecision decision = Capabilities.Decide(feature, antiCheatKind);

        Assert.Equal(expected, decision.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
    }

    [Theory]
    [MemberData(nameof(AllAntiCheatKinds))]
    public void TraceAndPinDecisionsMatchTheLegacyGates(string? antiCheatKind)
    {
        Assert.Equal(AntiCheat.ForbidsTrace(antiCheatKind), Capabilities.Decide(CapabilityFeature.Trace, antiCheatKind).Outcome == CapabilityOutcome.Block);
        Assert.Equal(AntiCheat.ForbidsPin(antiCheatKind), Capabilities.Decide(CapabilityFeature.Pin, antiCheatKind).Outcome == CapabilityOutcome.Block);
    }

    [Fact]
    public void VanguardRetainsBaselineReadAndLobbyHint()
    {
        Assert.Equal(CapabilityOutcome.Allow, Capabilities.Decide(CapabilityFeature.BaselineRead, "vanguard").Outcome);
        Assert.Equal(CapabilityOutcome.Allow, Capabilities.Decide(CapabilityFeature.LobbyHint, "vanguard").Outcome);
    }

    [Fact]
    public void UnknownDetectionBlocksNewPrefetchTracePinAndTier()
    {
        CapabilityFeature[] blocked =
        [CapabilityFeature.NewPrefetch, CapabilityFeature.Trace, CapabilityFeature.Pin, CapabilityFeature.Tier];

        foreach (CapabilityFeature feature in blocked)
        {
            Assert.Equal(CapabilityOutcome.Block, Capabilities.Decide(feature, "unknown").Outcome);
        }
    }

    [Theory]
    [MemberData(nameof(AllAntiCheatKinds))]
    public void TierDecisionMatchesTheExistingNullOnlyGate(string? antiCheatKind)
    {
        bool existingGateAllows = antiCheatKind is null;

        Assert.Equal(existingGateAllows, Capabilities.Decide(CapabilityFeature.Tier, antiCheatKind).Outcome == CapabilityOutcome.Allow);
    }

    public static TheoryData<string?> AllAntiCheatKinds
    {
        get
        {
            var data = new TheoryData<string?>();
            foreach (string? kind in AntiCheatKinds)
            {
                data.Add(kind);
            }

            return data;
        }
    }

    private static TheoryData<CapabilityFeature, string?, CapabilityOutcome> CreateDecisionCases()
    {
        var data = new TheoryData<CapabilityFeature, string?, CapabilityOutcome>();
        AddCases(data, CapabilityFeature.BaselineRead, All(CapabilityOutcome.Allow));
        AddCases(data, CapabilityFeature.NewPrefetch,
        [
            CapabilityOutcome.Allow, CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Allow, CapabilityOutcome.Block,
            CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block,
            CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block,
        ]);
        AddCases(data, CapabilityFeature.Trace,
        [
            CapabilityOutcome.Allow, CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block,
            CapabilityOutcome.Allow, CapabilityOutcome.Block, CapabilityOutcome.Allow, CapabilityOutcome.Allow, CapabilityOutcome.Allow,
            CapabilityOutcome.Allow, CapabilityOutcome.Allow, CapabilityOutcome.Allow, CapabilityOutcome.Allow, CapabilityOutcome.Allow,
        ]);
        AddCases(data, CapabilityFeature.Pin,
        [
            CapabilityOutcome.Allow, CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block,
            CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Allow, CapabilityOutcome.Allow, CapabilityOutcome.Allow,
            CapabilityOutcome.Allow, CapabilityOutcome.Allow, CapabilityOutcome.Allow, CapabilityOutcome.Allow, CapabilityOutcome.Allow,
        ]);
        AddCases(data, CapabilityFeature.Relocate,
        [
            CapabilityOutcome.Allow, CapabilityOutcome.Allow, CapabilityOutcome.Allow, CapabilityOutcome.Allow, CapabilityOutcome.Allow,
            CapabilityOutcome.Allow, CapabilityOutcome.Block, CapabilityOutcome.Allow, CapabilityOutcome.Allow, CapabilityOutcome.Allow,
            CapabilityOutcome.Allow, CapabilityOutcome.Allow, CapabilityOutcome.Allow, CapabilityOutcome.Block, CapabilityOutcome.Block,
        ]);
        AddCases(data, CapabilityFeature.Tier,
        [
            CapabilityOutcome.Allow, CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block,
            CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block,
            CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block, CapabilityOutcome.Block,
        ]);
        AddCases(data, CapabilityFeature.LobbyHint, All(CapabilityOutcome.Allow));
        AddCases(data, CapabilityFeature.ShadowPlan, All(CapabilityOutcome.Allow));
        return data;
    }

    private static CapabilityOutcome[] All(CapabilityOutcome outcome) => Enumerable.Repeat(outcome, AntiCheatKinds.Length).ToArray();

    private static void AddCases(TheoryData<CapabilityFeature, string?, CapabilityOutcome> data, CapabilityFeature feature, CapabilityOutcome[] expected)
    {
        Assert.Equal(AntiCheatKinds.Length, expected.Length);
        for (int i = 0; i < AntiCheatKinds.Length; i++)
        {
            data.Add(feature, AntiCheatKinds[i], expected[i]);
        }
    }
}
