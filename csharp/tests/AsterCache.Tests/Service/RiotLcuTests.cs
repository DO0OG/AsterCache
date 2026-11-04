using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 자체 서명 인증서의 HTTPS 로컬 서버 (리그 클라이언트 흉내): 진짜 클라이언트·인터넷에는 접속하지 않고 127.0.0.1 의 시험이 고른 포트만 씁니다.
internal sealed class FakeLcuServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2 _cert;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeLcuServer()
    {
        using RSA rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=127.0.0.1", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using X509Certificate2 self = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        _cert = X509CertificateLoader.LoadPkcs12(self.Export(X509ContentType.Pfx), null);
        _listener.Start();
        _loop = Task.Run(Accept);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public ConcurrentQueue<(string Method, string Path, string? Auth)> Requests { get; } = new();

    /// 경로 -> (상태 코드, 본문, Location). 기본은 404.
    public Func<string, (int Status, string Body, string? Location)> Handler { get; set; } = _ => (404, string.Empty, null);

    public TimeSpan ResponseDelay { get; set; }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _loop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // 종료 중 취소
        }

        _cert.Dispose();
        _stop.Dispose();
    }

    private async Task Accept()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient c = await _listener.AcceptTcpClientAsync(_stop.Token);
                _ = Task.Run(() => Serve(c));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // 종료
        }
    }

    private async Task Serve(TcpClient c)
    {
        try
        {
            using (c)
            using (var ssl = new SslStream(c.GetStream(), false))
            {
                await ssl.AuthenticateAsServerAsync(_cert);
                var head = new StringBuilder();
                byte[] one = new byte[1];
                while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal) && await ssl.ReadAsync(one) > 0)
                {
                    head.Append((char)one[0]);
                }

                string[] lines = head.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                string[] first = lines[0].Split(' ');
                string? auth = lines.Skip(1).Select(l => l.Split(": ", 2)).Where(p => p.Length == 2 && p[0].Equals("Authorization", StringComparison.OrdinalIgnoreCase)).Select(p => p[1]).FirstOrDefault();
                Requests.Enqueue((first[0], first[1], auth));
                if (ResponseDelay > TimeSpan.Zero)
                {
                    await Task.Delay(ResponseDelay, _stop.Token);
                }

                (int status, string body, string? location) = Handler(first[1]);
                byte[] payload = Encoding.UTF8.GetBytes(body);
                string response = $"HTTP/1.1 {status} X\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n" + (location is null ? string.Empty : $"Location: {location}\r\n") + "\r\n";
                await ssl.WriteAsync(Encoding.ASCII.GetBytes(response));
                await ssl.WriteAsync(payload);
                await ssl.FlushAsync();
            }
        }
        catch (Exception e) when (e is IOException or AuthenticationException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // 클라이언트가 먼저 끊음 (인증서 거부·시간 초과)
        }
    }
}

/// lockfile 파싱, 챔피언 선택 JSON, 파일 이름 규칙, 그리고 가짜 HTTPS 서버에 대한 전용 클라이언트의 동작.
public sealed class RiotLcuTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData("LeagueClient:1234:51234:s3cret:https", true)]
    [InlineData("LeagueClient:1234:51234:s3cret:https\n", true)]
    [InlineData("LeagueClient:1234:51234:s3cret", false)] // 필드 4 개
    [InlineData("LeagueClient:1234:51234:s3cret:https:extra", false)] // 필드 6 개
    [InlineData("LeagueClient:1234:51234:s3cret:http", false)]
    [InlineData("LeagueClient:0:51234:s3cret:https", false)] // PID 없음
    [InlineData("LeagueClient:abc:51234:s3cret:https", false)]
    [InlineData("LeagueClient:1234:0:s3cret:https", false)]
    [InlineData("LeagueClient:1234:70000:s3cret:https", false)]
    [InlineData("LeagueClient:1234:51234::https", false)] // 비밀번호 없음
    [InlineData("", false)]
    [InlineData(null, false)]
    public void LockfileParsesOnlyTheFiveFieldHttpsForm(string? text, bool ok)
    {
        Lockfile? l = Lockfile.Parse(text);

        Assert.Equal(ok, l is not null);
        if (ok)
        {
            Assert.Equal((1234, 51234, "s3cret"), (l!.Pid, l.Port, l.Password));
        }
    }

    [Fact]
    public void LockfileToStringDoesNotShowThePassword()
    {
        string text = Lockfile.Parse("LeagueClient:1234:51234:s3cret:https")!.ToString();

        Assert.DoesNotContain("s3cret", text, StringComparison.Ordinal);
        Assert.Contains("51234", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ChampSelectStateListsLockedPicksFirstThenHoversWithoutZerosOrBans()
    {
        const string Json = """
        {
          "actions": [
            [ {"type":"ban","completed":true,"championId":99}, {"type":"ban","completed":true,"championId":0} ],
            [ {"type":"pick","completed":true,"championId":11}, {"type":"pick","completed":false,"championId":12}, {"type":"pick","completed":true,"championId":0} ],
            [ {"type":"pick","completed":true,"championId":21}, {"type":"pick","completed":true,"championId":11} ]
          ],
          "myTeam":    [ {"championId":11}, {"championId":12}, {"championId":0} ],
          "theirTeam": [ {"championId":21}, {"championId":30}, {"championId":12} ]
        }
        """;
        using JsonDocument doc = JsonDocument.Parse(Json);

        ChampSelectState s = ChampSelectState.Parse(doc.RootElement);

        Assert.Equal([11, 21], s.Locked);
        Assert.Equal([12, 30], s.Hovered); // 잠금이 아닌 것만, 중복 없이, 처음 나온 순서
    }

    [Fact]
    public void ChampSelectStateToleratesMissingOrWrongShapes()
    {
        using JsonDocument doc = JsonDocument.Parse("""{"actions":"x","myTeam":[1,{"championId":"7"},{"championId":5}],"theirTeam":null}""");

        ChampSelectState s = ChampSelectState.Parse(doc.RootElement);

        Assert.Empty(s.Locked);
        Assert.Equal([5], s.Hovered);
    }

    [Theory]
    [InlineData("Aatrox", true)]
    [InlineData("MonkeyKing", true)]
    [InlineData("Zed2", true)]
    [InlineData("", false)]
    [InlineData("../Aatrox", false)]
    [InlineData("a\\b", false)]
    [InlineData("a b", false)]
    [InlineData("Aatrox.wad", false)]
    [InlineData("Ünal", false)]
    public void AliasMustBeAlphanumeric(string alias, bool ok) => Assert.Equal(ok, LcuHttpClient.IsAlias(alias));

    [Fact]
    public void FileNamesFollowTheInstallLayoutAndLocaleSuffixes()
    {
        string final = @"C:\Riot Games\League of Legends\Game\DATA\FINAL";

        Assert.Equal([final + @"\Champions\Ahri.wad.client", final + @"\Champions\Ahri.ko_KR.wad.client"], RiotPhaseWatcher.ChampionFiles(final, "Ahri", ["ko_KR"]));
        Assert.Equal([final + @"\Maps\Shipping\Map11.wad.client"], RiotPhaseWatcher.MapFiles(final, 11, []));
        Assert.Empty(RiotPhaseWatcher.ChampionFiles(final, @"..\..\evil", ["ko_KR"]));
        Assert.Empty(RiotPhaseWatcher.MapFiles(final, 0, []));
    }

    [Fact]
    public void LocalesComeFromTheGlobalWadSuffixesThatActuallyExist()
    {
        string dir = Path.Combine(Path.GetTempPath(), "aster_loc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (string n in new[] { "Global.wad.client", "Global.ko_KR.wad.client", "Global.en_US.wad.client", "Global.bad name.wad.client", "Global.x.wad.client" })
            {
                File.WriteAllText(Path.Combine(dir, n), "x");
            }

            Assert.Equal(["en_US", "ko_KR"], RiotPhaseWatcher.DetectLocales(dir));
            Assert.Empty(RiotPhaseWatcher.DetectLocales(Path.Combine(dir, "missing")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void CertificateIsAcceptedOnlyWhenValidOrTheTargetIsLoopback()
    {
        Assert.True(LcuHttpClient.AcceptsCertificate(null, SslPolicyErrors.None));
        Assert.False(LcuHttpClient.AcceptsCertificate(null, SslPolicyErrors.RemoteCertificateChainErrors));
        Assert.False(LcuHttpClient.AcceptsCertificate(new object(), SslPolicyErrors.RemoteCertificateNameMismatch));
    }

    private static string Basic(string password) => "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("riot:" + password));

    [Fact]
    public async Task ClientTalksToTheSelfSignedServerWithBasicAuthAndGetOnly()
    {
        using var server = new FakeLcuServer();
        server.Handler = path => path switch
        {
            LcuHttpClient.PhasePath => (200, "\"ChampSelect\"", null),
            LcuHttpClient.FlowSessionPath => (200, """{"map":{"id":12,"name":"x"}}""", null),
            LcuHttpClient.ChampSelectPath => (200, """{"actions":[[{"type":"pick","completed":true,"championId":103}]],"myTeam":[{"championId":103}],"theirTeam":[{"championId":1}]}""", null),
            LcuHttpClient.ChampionSummaryPath => (200, """[{"id":-1,"alias":"None"},{"id":103,"alias":"Ahri"},{"id":1,"alias":"Annie"},{"id":5,"alias":"../x"},{"id":6,"alias":"Zed"}]""", null),
            _ => (404, string.Empty, null),
        };
        using var client = new LcuHttpClient(new Lockfile(1, server.Port, "pw"), TimeSpan.FromSeconds(5));
        using var cts = new CancellationTokenSource(Bound);

        Assert.Equal("ChampSelect", await client.GetPhaseAsync(cts.Token));
        Assert.Equal(12, await client.GetMapIdAsync(cts.Token));
        ChampSelectState? state = await client.GetChampSelectAsync(cts.Token);
        Assert.Equal([103], state!.Locked);
        Assert.Equal([1], state.Hovered);
        IReadOnlyDictionary<int, string> aliases = await client.GetChampionAliasesAsync(cts.Token);
        Assert.Equal(new Dictionary<int, string> { [103] = "Ahri", [1] = "Annie", [6] = "Zed" }, aliases); // -1 과 경로를 닮은 alias 는 뺌

        Assert.Equal(4, server.Requests.Count);
        Assert.All(server.Requests, r => Assert.Equal(("GET", Basic("pw")), (r.Method, r.Auth)));
        Assert.Equal([LcuHttpClient.PhasePath, LcuHttpClient.FlowSessionPath, LcuHttpClient.ChampSelectPath, LcuHttpClient.ChampionSummaryPath], server.Requests.Select(r => r.Path));
    }

    [Fact]
    public async Task NotFoundIsNullAndOtherStatusesAndRedirectsFail()
    {
        using var server = new FakeLcuServer();
        server.Handler = path => path switch
        {
            LcuHttpClient.ChampSelectPath => (404, string.Empty, null),
            LcuHttpClient.PhasePath => (500, "x", null),
            LcuHttpClient.FlowSessionPath => (302, string.Empty, "https://127.0.0.1:1/elsewhere"),
            _ => (200, "not json", null),
        };
        using var client = new LcuHttpClient(new Lockfile(1, server.Port, "pw"), TimeSpan.FromSeconds(5));

        Assert.Null(await client.GetChampSelectAsync(default));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetPhaseAsync(default));
        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetMapIdAsync(default)); // 리디렉션은 따라가지 않음
        await Assert.ThrowsAnyAsync<JsonException>(() => client.GetChampionAliasesAsync(default));
        Assert.DoesNotContain(server.Requests, r => r.Path.Contains("elsewhere", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SlowResponseTimesOutAndExternalCancellationPropagates()
    {
        using var server = new FakeLcuServer { ResponseDelay = TimeSpan.FromSeconds(5) };
        server.Handler = _ => (200, "\"None\"", null);
        using var client = new LcuHttpClient(new Lockfile(1, server.Port, "pw"), TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetPhaseAsync(default)); // 시간 초과 (호출자 토큰은 살아 있음)

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetPhaseAsync(cts.Token));
    }
}
