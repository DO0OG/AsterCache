using System.Net;
using AsterCache.Tray.Update;
using Xunit;

namespace AsterCache.Tests.Tray;

public sealed class UpdateHttpTests
{
    private const string Api = "https://api.github.com/repos/DO0OG/AsterCache/releases/latest";
    private const string Asset = "https://github.com/DO0OG/AsterCache/releases/download/v1.0.0/a.exe";
    private const string Cdn = "https://release-assets.githubusercontent.com/github-production-release-asset/1/a.exe?sig=x";

    private static UpdateHttp Client(FakeHandler h, UpdateLimits? limits = null) => new("1.2.3", limits ?? new UpdateLimits(), h);

    [Theory]
    [InlineData("https://api.github.com/x", true)]
    [InlineData("https://GitHub.com/x", true)]
    [InlineData("https://objects.githubusercontent.com/x", true)]
    [InlineData("https://release-assets.githubusercontent.com/x", true)]
    [InlineData("http://github.com/x", false)]
    [InlineData("https://github.com:8443/x", false)]
    [InlineData("https://user@github.com/x", false)]
    [InlineData("https://github.com.evil.example/x", false)]
    [InlineData("https://evilgithub.com/x", false)]
    [InlineData("https://raw.githubusercontent.com/x", false)]
    [InlineData("ftp://github.com/x", false)]
    public void OnlyHttpsOnAllowedHostsPasses(string url, bool allowed) => Assert.Equal(allowed, UpdateHttp.IsAllowed(new Uri(url)));

    [Fact]
    public async Task RequestsCarryOnlyAProductUserAgent()
    {
        var h = new FakeHandler();
        h.Serve(Api, "{}");

        await Client(h).GetBytesAsync(new Uri(Api), CancellationToken.None);

        HttpRequestMessage req = Assert.Single(h.Requests);
        Assert.Equal("AsterCache/1.2.3", req.Headers.UserAgent.ToString());
        Assert.Equal(["User-Agent"], req.Headers.Select(x => x.Key)); // 사용자·PC 정보나 인증 헤더 없음
    }

    [Fact]
    public async Task PlainHttpIsRefusedWithoutConnecting()
    {
        var h = new FakeHandler();
        await Assert.ThrowsAsync<UpdateException>(() => Client(h).GetBytesAsync(new Uri("http://api.github.com/x"), CancellationToken.None));
        Assert.Empty(h.Requests);
    }

    [Fact]
    public async Task RedirectToAnAllowedHostIsFollowed()
    {
        var h = new FakeHandler();
        h.Redirect(Asset, Cdn);
        h.Serve(Cdn, [1, 2, 3]);
        using var ms = new MemoryStream();

        await Client(h).DownloadAsync(new Uri(Asset), ms, null, CancellationToken.None);

        Assert.Equal([1, 2, 3], ms.ToArray());
        Assert.Equal([Asset, Cdn], h.Urls);
    }

    [Theory]
    [InlineData("https://evil.example/a.exe")]
    [InlineData("http://github.com/a.exe")]
    [InlineData("https://github.com.evil.example/a.exe")]
    public async Task RedirectToADisallowedAddressIsRefusedWithoutConnecting(string target)
    {
        var h = new FakeHandler();
        h.Redirect(Asset, target);
        h.Serve(target, [9]);
        using var ms = new MemoryStream();

        await Assert.ThrowsAsync<UpdateException>(() => Client(h).DownloadAsync(new Uri(Asset), ms, null, CancellationToken.None));

        Assert.Equal([Asset], h.Urls); // 허용되지 않은 주소에는 요청하지 않음
        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public async Task RedirectLoopsStop()
    {
        var h = new FakeHandler();
        h.Redirect(Asset, Asset);
        await Assert.ThrowsAsync<UpdateException>(() => Client(h).GetBytesAsync(new Uri(Asset), CancellationToken.None));
        Assert.Equal(UpdateHttp.MaxRedirects + 1, h.Requests.Count);
    }

    [Fact]
    public async Task ErrorStatusIsAnUpdateError()
    {
        var h = new FakeHandler();
        h.Fail(Api, HttpStatusCode.Forbidden);
        UpdateException e = await Assert.ThrowsAsync<UpdateException>(() => Client(h).GetBytesAsync(new Uri(Api), CancellationToken.None));
        Assert.Contains("403", e.Message);
    }

    [Fact]
    public async Task DeclaredSizeOverTheLimitIsRefusedBeforeReading()
    {
        var h = new FakeHandler();
        h.Serve(Asset, new byte[2048]);
        using var ms = new MemoryStream();

        await Assert.ThrowsAsync<UpdateException>(() => Client(h, new UpdateLimits(InstallerBytes: 1024)).DownloadAsync(new Uri(Asset), ms, null, CancellationToken.None));

        Assert.Equal(0, ms.Length);
    }

    [Fact]
    public async Task UndeclaredSizeOverTheLimitStopsWhileReading()
    {
        var h = new FakeHandler();
        h.Routes[Asset] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Unseekable(new byte[300_000])) };
        using var ms = new MemoryStream();

        await Assert.ThrowsAsync<UpdateException>(() => Client(h, new UpdateLimits(InstallerBytes: 100_000)).DownloadAsync(new Uri(Asset), ms, null, CancellationToken.None));

        Assert.True(ms.Length <= 100_000);
    }

    [Fact]
    public async Task MetadataOverTheLimitIsRefused()
    {
        var h = new FakeHandler();
        h.Serve(Api, new byte[5000]);
        await Assert.ThrowsAsync<UpdateException>(() => Client(h, new UpdateLimits(MetadataBytes: 1000)).GetBytesAsync(new Uri(Api), CancellationToken.None));
    }

    [Fact]
    public async Task NetworkErrorIsAnUpdateErrorButCancellationIsNot()
    {
        var h = new FakeHandler();
        h.Routes[Api] = () => throw new HttpRequestException("boom");
        await Assert.ThrowsAsync<UpdateException>(() => Client(h).GetBytesAsync(new Uri(Api), CancellationToken.None));

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(h).GetBytesAsync(new Uri(Api), cts.Token));
    }

    [Fact]
    public async Task ConnectionDroppedWhileReadingTheBodyIsAnUpdateError()
    {
        var h = new FakeHandler();
        h.Routes[Api] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Dropping()) };

        await Assert.ThrowsAsync<UpdateException>(() => Client(h).GetBytesAsync(new Uri(Api), CancellationToken.None));
    }

    [Fact]
    public async Task ARequestThatNeverAnswersTimesOut()
    {
        using var http = new UpdateHttp("1.2.3", new UpdateLimits { MetadataTimeout = TimeSpan.FromMilliseconds(100) }, new Hanging());
        UpdateException e = await Assert.ThrowsAsync<UpdateException>(() => http.GetBytesAsync(new Uri(Api), CancellationToken.None));
        Assert.Contains("timed out", e.Message);
    }

    private sealed class Hanging : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new HttpResponseMessage();
        }
    }

    private sealed class Dropping : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("connection reset");

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class Unseekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;

        public override long Length => throw new NotSupportedException();
    }
}
