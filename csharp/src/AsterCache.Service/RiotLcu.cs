using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace AsterCache.Service;

/// 리그 오브 레전드 클라이언트 lockfile 의 접속 정보 (이름:PID:포트:비밀번호:https). 비밀번호는 메모리에만 두고 ToString 에 찍지 않습니다.
public sealed record Lockfile(int Pid, int Port, string Password)
{
    private const int MaxPasswordChars = 256;

    public override string ToString() => $"Lockfile {{ Pid = {Pid}, Port = {Port} }}";

    /// 필드가 정확히 5 개이고 PID·포트가 범위 안이며 비밀번호가 비어 있지 않고 프로토콜이 https 일 때만. 아니면 null.
    public static Lockfile? Parse(string? text)
    {
        string[] f = (text ?? string.Empty).Trim().Split(':');
        if (f.Length != 5 || f[0].Length == 0 || f[3].Length is 0 or > MaxPasswordChars || f[3].Any(char.IsControl) || f[4] != "https"
            || !int.TryParse(f[1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0
            || !int.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out int port) || port is <= 0 or > 65535)
        {
            return null;
        }

        return new Lockfile(pid, port, f[3]);
    }
}

/// 챔피언 선택 세션에서 읽은 챔피언 id: 잠금 (type=pick 이 completed) 이 먼저, 그다음 호버 (myTeam·theirTeam 의 championId 중 잠금이 아닌 것). 0 (미선택) 은 제외, 순서는 처음 나온 대로 중복 없이.
public sealed record ChampSelectState(IReadOnlyList<int> Locked, IReadOnlyList<int> Hovered)
{
    public static ChampSelectState Parse(JsonElement session)
    {
        var locked = new List<int>();
        var hovered = new List<int>();
        if (session.TryGetProperty("actions", out JsonElement actions) && actions.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement group in actions.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Array))
            {
                foreach (JsonElement a in group.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object))
                {
                    if (a.TryGetProperty("type", out JsonElement t) && t.ValueKind == JsonValueKind.String && t.GetString() == "pick"
                        && a.TryGetProperty("completed", out JsonElement done) && done.ValueKind == JsonValueKind.True && Id(a) is { } id && !locked.Contains(id))
                    {
                        locked.Add(id);
                    }
                }
            }
        }

        foreach (string team in new[] { "myTeam", "theirTeam" })
        {
            if (session.TryGetProperty(team, out JsonElement members) && members.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement m in members.EnumerateArray().Where(m => m.ValueKind == JsonValueKind.Object))
                {
                    if (Id(m) is { } id && !locked.Contains(id) && !hovered.Contains(id))
                    {
                        hovered.Add(id);
                    }
                }
            }
        }

        return new ChampSelectState(locked, hovered);
    }

    // championId 가 0 보다 큰 정수이면 그 값, 아니면 null.
    private static int? Id(JsonElement o) =>
        o.TryGetProperty("championId", out JsonElement c) && c.ValueKind == JsonValueKind.Number && c.TryGetInt32(out int id) && id > 0 ? id : null;
}

/// 리그 오브 레전드 클라이언트 로컬 API (GET 만). 실패 (연결·인증·형식·시간 초과) 는 예외로, 챔피언 선택이 아니면 (404) null.
public interface ILcuClient : IDisposable
{
    /// 게임 흐름 단계 ("None", "Lobby", "ChampSelect", "GameStart", "InProgress" ...).
    Task<string?> GetPhaseAsync(CancellationToken ct);

    /// 지금 흐름의 맵 id, 없으면 null.
    Task<int?> GetMapIdAsync(CancellationToken ct);

    /// 챔피언 선택 상태, 챔피언 선택 중이 아니면 null.
    Task<ChampSelectState?> GetChampSelectAsync(CancellationToken ct);

    /// 챔피언 id -> 영숫자 alias (데이터 파일 이름). alias 가 영숫자가 아니면 그 항목은 뺍니다.
    Task<IReadOnlyDictionary<int, string>> GetChampionAliasesAsync(CancellationToken ct);
}

/// https://127.0.0.1:포트 의 로컬 클라이언트 API. 전용 HttpClient: 프록시·자동 리디렉션·쿠키 없음, GET 만, 고정 경로만, 연결은 127.0.0.1 의 lockfile 포트로만,
/// 인증서 검증 예외 (자체 서명) 는 대상 호스트가 127.0.0.1 일 때만, 요청마다 시간 초과. 인증 정보는 이 객체 안에만 있고 로그에 찍지 않습니다.
public sealed class LcuHttpClient : ILcuClient
{
    public const string Host = "127.0.0.1";
    public const string PhasePath = "/lol-gameflow/v1/gameflow-phase";
    public const string FlowSessionPath = "/lol-gameflow/v1/session";
    public const string ChampSelectPath = "/lol-champ-select/v1/session";
    public const string ChampionSummaryPath = "/lol-game-data/assets/v1/champion-summary.json";
    private const int MaxBodyBytes = 8 << 20;

    private readonly HttpClient _http;
    private readonly TimeSpan _timeout;

    public LcuHttpClient(Lockfile creds, TimeSpan timeout)
    {
        _timeout = timeout;
        int port = creds.Port;
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (sender, _, _, errors) => AcceptsCertificate(sender, errors) },
            ConnectCallback = async (context, ct) =>
            {
                if (context.DnsEndPoint.Host != Host || context.DnsEndPoint.Port != port)
                {
                    throw new HttpRequestException("blocked: only the lockfile port on 127.0.0.1 is allowed");
                }

                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            },
        };
        _http = new HttpClient(handler, disposeHandler: true) { BaseAddress = new Uri($"https://{Host}:{port}/"), Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("riot:" + creds.Password)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// 인증서 검증이 이미 통과했거나, 대상 호스트가 127.0.0.1 (클라이언트의 자체 서명 인증서) 일 때만 받아들입니다.
    public static bool AcceptsCertificate(object? sender, SslPolicyErrors errors) =>
        errors == SslPolicyErrors.None || sender is SslStream { TargetHostName: Host };

    public async Task<string?> GetPhaseAsync(CancellationToken ct)
    {
        using JsonDocument? doc = await GetAsync(PhasePath, ct);
        return doc?.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : null;
    }

    public async Task<int?> GetMapIdAsync(CancellationToken ct)
    {
        using JsonDocument? doc = await GetAsync(FlowSessionPath, ct);
        return doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("map", out JsonElement map) && map.ValueKind == JsonValueKind.Object
            && map.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out int v) && v > 0 ? v : null;
    }

    public async Task<ChampSelectState?> GetChampSelectAsync(CancellationToken ct)
    {
        using JsonDocument? doc = await GetAsync(ChampSelectPath, ct);
        return doc is null || doc.RootElement.ValueKind != JsonValueKind.Object ? null : ChampSelectState.Parse(doc.RootElement);
    }

    public async Task<IReadOnlyDictionary<int, string>> GetChampionAliasesAsync(CancellationToken ct)
    {
        using JsonDocument? doc = await GetAsync(ChampionSummaryPath, ct);
        var map = new Dictionary<int, string>();
        if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return map;
        }

        foreach (JsonElement e in doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object))
        {
            if (e.TryGetProperty("id", out JsonElement id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out int v) && v > 0
                && e.TryGetProperty("alias", out JsonElement alias) && alias.ValueKind == JsonValueKind.String && IsAlias(alias.GetString()))
            {
                map[v] = alias.GetString()!;
            }
        }

        return map;
    }

    /// 파일 이름에 그대로 쓰이므로 영숫자 1..64 자만 (경로 구분자·".." 주입 차단).
    public static bool IsAlias(string? s) => s is { Length: > 0 and <= 64 } && s.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9'));

    public void Dispose() => _http.Dispose();

    // 고정 경로 GET 한 번 (본문 상한 MaxBodyBytes). 404 는 null, 그 밖의 비성공·시간 초과·형식 오류는 예외.
    private async Task<JsonDocument?> GetAsync(string path, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_timeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        using HttpResponseMessage response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxBodyBytes)
        {
            throw new InvalidDataException("response too large");
        }

        await using Stream body = await response.Content.ReadAsStreamAsync(limit.Token);
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[16 * 1024];
        int n;
        while ((n = await body.ReadAsync(chunk, limit.Token)) > 0)
        {
            if (buffer.Length + n > MaxBodyBytes)
            {
                throw new InvalidDataException("response too large");
            }

            buffer.Write(chunk, 0, n);
        }

        buffer.Position = 0;
        return await JsonDocument.ParseAsync(buffer, cancellationToken: limit.Token);
    }
}
