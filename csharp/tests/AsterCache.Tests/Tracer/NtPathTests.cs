using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Tracer;

/// 기대값은 Python 3.11 ntpath.normpath / normcase 실행 결과.
public sealed class NtPathTests
{
    [Theory]
    [InlineData(@"D:\Games", @"D:\Games", @"d:\games")]
    [InlineData(@"D:\Games\", @"D:\Games", @"d:\games")]
    [InlineData("D:/Games/./x/../y", @"D:\Games\y", @"d:\games\y")]
    [InlineData(@"d:\\games\\\\x", @"d:\games\x", @"d:\games\x")]
    [InlineData("", ".", ".")]
    [InlineData(".", ".", ".")]
    [InlineData(@"\\srv\share\a\..\b", @"\\srv\share\b", @"\\srv\share\b")]
    [InlineData(@"\\srv\share", @"\\srv\share", @"\\srv\share")]
    [InlineData("D:", "D:", "d:")]
    [InlineData(@"D:\\", @"D:\", @"d:\")]
    [InlineData(@"D:..\x", @"D:..\x", @"d:..\x")]
    [InlineData(@"..\a", @"..\a", @"..\a")]
    [InlineData(@"\a\b\..\..\..\c", @"\c", @"\c")]
    [InlineData(@"\\?\C:\x\..\y", @"\\?\C:\y", @"\\?\c:\y")]
    [InlineData(@"C:\Windows\x.dll", @"C:\Windows\x.dll", @"c:\windows\x.dll")]
    public void NormPathAndNormCaseMatchPython(string input, string normpath, string normcased)
    {
        Assert.Equal(normpath, NtPath.NormPath(input));
        Assert.Equal(normcased, NtPath.NormCase(NtPath.NormPath(input)));
    }

    [Fact]
    public void DriveRootPrefixMatchesNothingLikePython()
    {
        // Python: canon == prefix or canon.startswith(prefix + '\\') 이므로 d:\ 접두는 d:\x 도 못 맞춥니다 (그대로 포팅).
        Assert.False(NtPath.IsUnder(@"d:\x", NtPath.NormCase(NtPath.NormPath(@"D:\"))));
        Assert.True(NtPath.IsUnder(@"d:\games\x", @"d:\games"));
        Assert.False(NtPath.IsUnder(@"d:\gamesother\x", @"d:\games"));
    }
}
