namespace AsterCache.Core.Discovery;

public enum CapabilityFeature
{
    BaselineRead,
    NewPrefetch,
    Trace,
    Pin,
    Relocate,
    Tier,
    LobbyHint,
    ShadowPlan,
}

public enum CapabilityOutcome
{
    Allow,
    Block,
    Unknown,
}

public sealed record CapabilityDecision(CapabilityOutcome Outcome, string Reason);

public static class Capabilities
{
    public static CapabilityDecision Decide(CapabilityFeature feature, string? antiCheatKind) => feature switch
    {
        CapabilityFeature.BaselineRead => Allow("existing read-only path"),
        CapabilityFeature.NewPrefetch => NewPrefetch(antiCheatKind),
        CapabilityFeature.Trace => Trace(antiCheatKind),
        CapabilityFeature.Pin => Pin(antiCheatKind),
        CapabilityFeature.Relocate => Relocate(antiCheatKind),
        CapabilityFeature.Tier => Tier(antiCheatKind),
        CapabilityFeature.LobbyHint => Allow("existing lobby hint path"),
        CapabilityFeature.ShadowPlan => Allow("planning performs no file I/O"),
        _ => Unknown("unsupported capability"),
    };

    /// 아직 배선되지 않은 정책 기본값 (시험에서만 확인).
    private static CapabilityDecision NewPrefetch(string? kind)
    {
        if (kind is null)
        {
            return Allow("no anti-cheat detected");
        }

        // 설계 R-03: Vanguard 기준선만 새 예열을 허용합니다. 대소문자가 다른 값과 미지 값도 막습니다.
        return kind switch
        {
            "vanguard" => Allow("Vanguard baseline; read-only prefetch"),
            _ => Block("new prefetch is allowed only for Vanguard or no anti-cheat"),
        };
    }

    private static CapabilityDecision Trace(string? kind) => ForbidsTrace(kind)
        ? Block("current trace policy forbids this anti-cheat kind")
        : Allow("current trace policy allows this anti-cheat kind");

    private static CapabilityDecision Pin(string? kind) => ForbidsPin(kind)
        ? Block("current pin policy forbids this anti-cheat kind")
        : Allow("current pin policy allows this anti-cheat kind");

    /// 아직 배선되지 않은 정책 기본값 (시험에서만 확인).
    // AutoRelocator keeps the supplemental profile replan and confirmation marker checks. 설계에 따라 unknown(대소문자 무관)과 빈 문자열만 막습니다.
    private static CapabilityDecision Relocate(string? kind) => kind == "" || string.Equals(kind, "unknown", StringComparison.OrdinalIgnoreCase)
        ? Block("unknown or empty anti-cheat blocks relocation")
        : Allow("existing relocation path; only unknown or empty detection is gated");

    /// 아직 배선되지 않은 정책 기본값 (시험에서만 확인).
    private static CapabilityDecision Tier(string? kind) => kind is null
        ? Allow("no anti-cheat is present")
        : Block("tiering is refused when any anti-cheat kind is present");

    private static bool ForbidsTrace(string? kind) => kind is "eac" or "battleye" or "vanguard" or "kernel" or "unknown";

    private static bool ForbidsPin(string? kind) => ForbidsTrace(kind) || kind == "ricochet";

    private static CapabilityDecision Allow(string reason) => new(CapabilityOutcome.Allow, reason);

    private static CapabilityDecision Block(string reason) => new(CapabilityOutcome.Block, reason);

    private static CapabilityDecision Unknown(string reason) => new(CapabilityOutcome.Unknown, reason);
}
