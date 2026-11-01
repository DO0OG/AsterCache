using System.Security.Cryptography;
using Xunit;

namespace AsterCache.Tests.Core;

public sealed class FixtureIntegrityTests
{
    [Theory]
    [InlineData("e3/trace.json", "29bc2f6665e887c8d881fe936f27ffe45f913bd2e132d6b22e1dac854929504a")]
    [InlineData("e3/plan.json", "ce436dbe0da44552313a9052b7f4aca995e1e9531f84cd35398d1320a3fb87e6")]
    [InlineData("e3/journal.jsonl", "aff1a67582594095d2211d5eb88cd1e2a73a91095e1b90009d588857dd90689c")]
    public void FixtureBytesMatchSourceSha256(string relativePath, string expectedSha256)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", relativePath);

        string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

        Assert.Equal(expectedSha256, actual);
    }
}
