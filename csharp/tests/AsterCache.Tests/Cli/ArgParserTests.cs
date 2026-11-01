using AsterCache.Cli;
using Xunit;

namespace AsterCache.Tests.Cli;

public sealed class ArgParserTests
{
    private static readonly ArgOption[] Spec =
    [
        new("trace", ArgKind.List, Required: true),
        new("zone-mib", Type: ArgType.Int, Default: "300"),
        new("zone-start-lcn", Default: "auto"),
        new("ratio", Type: ArgType.Double),
        new("allowed-root", ArgKind.Many),
        new("dry-run", ArgKind.Flag),
        new("strategy", Choices: ["physical", "path"]),
    ];

    [Fact]
    public void ParsesPythonStyleForms()
    {
        ParsedArgs a = ArgParser.Parse(Spec, ["--trace", "a.json", "b.json", "--zone-mib=-5", "--ratio", "0.5", "--allowed-root", "x", "--allowed-root", "y", "--dry-run"]);

        Assert.Equal(["a.json", "b.json"], a.List("trace"));
        Assert.Equal(-5L, a.Int("zone-mib"));
        Assert.Equal(0.5, a.Double("ratio"));
        Assert.Equal(["x", "y"], a.List("allowed-root"));
        Assert.True(a.Flag("dry-run"));
        Assert.Equal("auto", a.Text("zone-start-lcn"));
        Assert.False(a.Given("strategy"));
    }

    [Fact]
    public void UniquePrefixIsAcceptedAndAmbiguousIsNot()
    {
        Assert.Equal(7L, ArgParser.Parse(Spec, ["--trace", "t", "--zone-m", "7"]).Int("zone-mib"));
        Assert.Throws<ArgException>(() => ArgParser.Parse(Spec, ["--trace", "t", "--zone", "7"]));
    }

    [Theory]
    [InlineData("--zone-mib", "abc")]
    [InlineData("--ratio", "x")]
    [InlineData("--strategy", "zzz")]
    [InlineData("--zone-mib")]
    [InlineData("--dry-run=1")]
    [InlineData("--nope", "1")]
    [InlineData("--dry-run", "stray")]
    public void BadInputThrows(params string[] extra)
    {
        Assert.Throws<ArgException>(() => ArgParser.Parse(Spec, ["--trace", "t", .. extra]));
    }

    [Fact]
    public void MissingRequiredNamesTheOption()
    {
        ArgException e = Assert.Throws<ArgException>(() => ArgParser.Parse(Spec, ["--dry-run"]));

        Assert.Equal("the following arguments are required: --trace", e.Message);
    }

    [Fact]
    public void HelpIsRecognized() => Assert.Throws<HelpRequestedException>(() => ArgParser.Parse(Spec, ["--trace", "t", "-h"]));

    [Theory]
    [InlineData("D:/AsterBench/", @"D:\AsterBench")]
    [InlineData(@"D:\a\\b\.\c", @"D:\a\b\c")]
    [InlineData("D:", "D:")]
    [InlineData(@"D:\", @"D:\")]
    [InlineData("./sub/../plan.json", @"sub\..\plan.json")]
    [InlineData(@"\\host\share\x\", @"\\host\share\x")]
    [InlineData("", ".")]
    public void PathTextFollowsPathlib(string input, string expected) => Assert.Equal(expected, PathText.Str(input));

    [Theory]
    [InlineData("a", "'a'")]
    [InlineData("it's", "\"it's\"")]
    [InlineData(@"D:\x", @"'D:\\x'")]
    public void PyReprQuotesLikePython(string input, string expected) => Assert.Equal(expected, PyRepr.Str(input));
}
