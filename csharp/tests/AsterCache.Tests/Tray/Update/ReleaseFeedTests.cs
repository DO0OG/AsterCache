using System.Text;
using AsterCache.Tray.Update;
using Xunit;

namespace AsterCache.Tests.Tray;

public sealed class ReleaseFeedTests
{
    private static SemVer V(string text) => SemVer.TryParse(text, out SemVer v) ? v : throw new ArgumentException(text);

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public void SemVerOrderingFollowsTheSpec()
    {
        string[] ascending = ["0.9.0-beta.38", "0.9.0", "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "1.0.1", "1.10.0", "2.0.0"];
        for (int i = 0; i < ascending.Length - 1; i++)
        {
            Assert.True(V(ascending[i]) < V(ascending[i + 1]), $"{ascending[i]} < {ascending[i + 1]}");
            Assert.True(V(ascending[i + 1]) > V(ascending[i]));
        }

        Assert.True(V("1.0.0") > V("1.0.0-rc.1"));
        Assert.True(V("1.0.0-rc.1") > V("0.9.0-beta.38"));
        Assert.True(V("1.0.0+abc").Equals(V("1.0.0+xyz"))); // 빌드 메타데이터는 비교하지 않음
        Assert.Equal("1.0.0", V("1.0.0+abc").Core);
    }

    [Theory]
    [InlineData("v1.0.0")]
    [InlineData("1.0")]
    [InlineData("01.0.0")]
    [InlineData("1.0.0-")]
    [InlineData("1.0.0-01")]
    [InlineData("1.0.0 ")]
    [InlineData("1.0.0/../x")]
    [InlineData("99999999999.0.0")]
    [InlineData("")]
    public void SemVerRejectsAnythingButTheStrictGrammar(string text) => Assert.False(SemVer.TryParse(text, out _));

    [Fact]
    public void LatestObjectYieldsTheNewerStableRelease()
    {
        using var rig = new UpdateRig();
        ReleaseInfo? r = ReleaseFeed.Select(Bytes(rig.Release("1.0.0")), V("0.9.0-beta.38"), false);

        Assert.NotNull(r);
        Assert.Equal("1.0.0", r.Version.Core);
        Assert.Equal("AsterCache-v1.0.0-win-x64-setup.exe", r.InstallerName);
        Assert.Equal(rig.Sha, r.Sha256);
        Assert.Equal(new Uri("https://github.com/DO0OG/AsterCache/releases/tag/v1.0.0"), r.Page);
        Assert.Null(r.ChecksumsUrl);
    }

    [Fact]
    public void SameOrLowerVersionIsNotAnUpdate()
    {
        using var rig = new UpdateRig();
        Assert.Null(ReleaseFeed.Select(Bytes(rig.Release("1.0.0")), V("1.0.0"), false));
        Assert.Null(ReleaseFeed.Select(Bytes(rig.Release("1.0.0")), V("1.0.1"), false));
        Assert.Null(ReleaseFeed.Select(Bytes(rig.Release("1.0.0-rc.1")), V("1.0.0"), true)); // 다운그레이드 금지
        Assert.NotNull(ReleaseFeed.Select(Bytes(rig.Release("1.0.0")), V("1.0.0-rc.1"), false)); // 정식은 같은 번호 프리릴리스보다 높음
    }

    [Fact]
    public void ListPicksTheHighestAllowedRelease()
    {
        using var rig = new UpdateRig();
        string list = "[" + string.Join(",", rig.Release("1.1.0-rc.1", prerelease: true), rig.Release("1.0.1", draft: true), rig.Release("1.0.0"), rig.Release("0.9.5")) + "]";

        Assert.Equal("1.0.0", ReleaseFeed.Select(Bytes(list), V("0.9.0-beta.38"), false)!.Version.Core); // 프리릴리스와 초안 제외
        Assert.Equal("1.1.0-rc.1", ReleaseFeed.Select(Bytes(list), V("0.9.0-beta.38"), true)!.Version.Core); // 프리릴리스 포함이면 SemVer 가 가장 높은 것
        Assert.Null(ReleaseFeed.Select(Bytes(list), V("1.0.0"), false));
    }

    [Fact]
    public void PrereleaseFlagAloneKeepsAStableLookingTagOutWhenOff()
    {
        using var rig = new UpdateRig();
        Assert.Null(ReleaseFeed.Select(Bytes(rig.Release("1.0.0", prerelease: true)), V("0.9.0"), false));
    }

    [Theory]
    [InlineData("AsterCache-v1.0.0-win-x64-setup.exe.zip")]
    [InlineData("AsterCache-v1.0.1-win-x64-setup.exe")]
    [InlineData("asterCache-v1.0.0-win-x64-setup.exe")]
    [InlineData("../AsterCache-v1.0.0-win-x64-setup.exe")]
    [InlineData("AsterCache-1.0.0-win-x64.zip")]
    public void OnlyTheExactInstallerNameIsAccepted(string assetName)
    {
        using var rig = new UpdateRig();
        Assert.Null(ReleaseFeed.Select(Bytes(rig.Release("1.0.0", name: assetName)), V("0.9.0"), false));
    }

    [Fact]
    public void HigherReleaseWithoutTheInstallerFallsBackToTheNextOne()
    {
        using var rig = new UpdateRig();
        string list = "[" + rig.Release("1.1.0", name: "other.zip") + "," + rig.Release("1.0.0") + "]";
        Assert.Equal("1.0.0", ReleaseFeed.Select(Bytes(list), V("0.9.0"), false)!.Version.Core);
    }

    [Fact]
    public void AssetOnADisallowedHostOrWithoutSizeIsSkipped()
    {
        string evil = "{\"tag_name\":\"v1.0.0\",\"assets\":[{\"name\":\"AsterCache-v1.0.0-win-x64-setup.exe\",\"size\":10,\"browser_download_url\":\"https://evil.example/a.exe\"}]}";
        string http = "{\"tag_name\":\"v1.0.0\",\"assets\":[{\"name\":\"AsterCache-v1.0.0-win-x64-setup.exe\",\"size\":10,\"browser_download_url\":\"http://github.com/a.exe\"}]}";
        string nosize = "{\"tag_name\":\"v1.0.0\",\"assets\":[{\"name\":\"AsterCache-v1.0.0-win-x64-setup.exe\",\"browser_download_url\":\"https://github.com/a.exe\"}]}";
        Assert.Null(ReleaseFeed.Select(Bytes(evil), V("0.9.0"), false));
        Assert.Null(ReleaseFeed.Select(Bytes(http), V("0.9.0"), false));
        Assert.Null(ReleaseFeed.Select(Bytes(nosize), V("0.9.0"), false));
    }

    [Theory]
    [InlineData("https://github.com/Someone/Else/releases/download/v1.0.0/AsterCache-v1.0.0-win-x64-setup.exe")]
    [InlineData("https://github.com/DO0OG/AsterCache/releases/download/v0.9.9/AsterCache-v1.0.0-win-x64-setup.exe")]
    [InlineData("https://github.com/DO0OG/AsterCache/releases/download/v1.0.0/../v0.1.0/AsterCache-v1.0.0-win-x64-setup.exe")]
    [InlineData("https://objects.githubusercontent.com/AsterCache-v1.0.0-win-x64-setup.exe")]
    [InlineData("https://github.com/DO0OG/AsterCache/releases/download/v1.0.0/other.exe")]
    public void DownloadUrlMustBeThisReleasesFixedPath(string url)
    {
        string json = "{\"tag_name\":\"v1.0.0\",\"assets\":[{\"name\":\"AsterCache-v1.0.0-win-x64-setup.exe\",\"size\":10,\"browser_download_url\":\"" + url + "\"}]}";
        Assert.Null(ReleaseFeed.Select(Bytes(json), V("0.9.0"), false));
    }

    [Fact]
    public void ChecksumsAssetOnAnotherPathIsIgnored()
    {
        string json = "{\"tag_name\":\"v1.0.0\",\"assets\":[{\"name\":\"AsterCache-v1.0.0-win-x64-setup.exe\",\"size\":10,\"browser_download_url\":\""
            + UpdateRig.DownloadUrl("1.0.0", UpdateRig.InstallerName("1.0.0")) + "\"},{\"name\":\"SHA256SUMS.txt\",\"browser_download_url\":\"https://github.com/Someone/Else/releases/download/v1.0.0/SHA256SUMS.txt\"}]}";
        Assert.Null(ReleaseFeed.Select(Bytes(json), V("0.9.0"), false)!.ChecksumsUrl);
    }

    [Fact]
    public void ChecksumsAssetIsFoundWhenThereIsNoDigest()
    {
        using var rig = new UpdateRig();
        ReleaseInfo r = ReleaseFeed.Select(Bytes(rig.Release("1.0.0", digest: false, sums: true)), V("0.9.0"), false)!;
        Assert.Null(r.Sha256);
        Assert.Equal(new Uri(UpdateRig.DownloadUrl("1.0.0", "SHA256SUMS.txt")), r.ChecksumsUrl);
    }

    [Theory]
    [InlineData("sha1:" + "aa")]
    [InlineData("sha256:zz")]
    [InlineData("sha256:" + "ab")]
    public void MalformedDigestIsIgnored(string digest)
    {
        string json = "{\"tag_name\":\"v1.0.0\",\"assets\":[{\"name\":\"AsterCache-v1.0.0-win-x64-setup.exe\",\"size\":10,\"digest\":\"" + digest + "\",\"browser_download_url\":\"" + UpdateRig.DownloadUrl("1.0.0", UpdateRig.InstallerName("1.0.0")) + "\"}]}";
        Assert.Null(ReleaseFeed.Select(Bytes(json), V("0.9.0"), false)!.Sha256);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("\"text\"")]
    [InlineData("")]
    public void NonReleaseJsonIsAnUpdateError(string body) => Assert.Throws<UpdateException>(() => ReleaseFeed.Select(Bytes(body), V("0.9.0"), false));

    [Fact]
    public void ChecksumLineMatchesTheExactFileNameOnly()
    {
        string h1 = new('a', 64);
        string h2 = new('B', 64);
        byte[] sums = Bytes($"{h1}  AsterCache-1.0.0-win-x64.zip\r\n{h2} *AsterCache-v1.0.0-win-x64-setup.exe\r\n");

        Assert.Equal(new string('b', 64), ReleaseFeed.ChecksumFor(sums, "AsterCache-v1.0.0-win-x64-setup.exe"));
        Assert.Equal(h1, ReleaseFeed.ChecksumFor(sums, "AsterCache-1.0.0-win-x64.zip"));
        Assert.Null(ReleaseFeed.ChecksumFor(sums, "AsterCache-v1.0.0-win-x64-setup.ex"));
        Assert.Null(ReleaseFeed.ChecksumFor(Bytes("nothing useful"), "AsterCache-v1.0.0-win-x64-setup.exe"));
    }

    [Fact]
    public void SameFileListedTwiceWithDifferentHashesIsRefused()
    {
        string a = new('a', 64);
        string b = new('b', 64);
        Assert.Null(ReleaseFeed.ChecksumFor(Bytes($"{a}  f.exe\n{b}  f.exe\n"), "f.exe"));
        Assert.Equal(a, ReleaseFeed.ChecksumFor(Bytes($"{a}  f.exe\n{a}  f.exe\n"), "f.exe"));
    }

    [Fact]
    public void MetadataUrlsAreTheFixedRepository()
    {
        Assert.Equal("https://api.github.com/repos/DO0OG/AsterCache/releases/latest", ReleaseFeed.MetadataUrl(false).AbsoluteUri);
        Assert.Equal("https://api.github.com/repos/DO0OG/AsterCache/releases?per_page=20", ReleaseFeed.MetadataUrl(true).AbsoluteUri);
    }
}
