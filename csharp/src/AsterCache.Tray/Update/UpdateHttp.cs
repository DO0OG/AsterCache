using System.Net;
using System.Net.Http.Headers;

namespace AsterCache.Tray.Update;

/// 업데이트 전용 HTTP. HTTPS 와 허용 호스트만 허용하고, 리디렉션은 직접 한 단계씩 따라가며 각 주소를 먼저 검사합니다 (허용되지 않은 호스트에는 접속하지 않음).
/// 인증서 검증은 기본값 그대로 두고, 요청에는 제품 이름과 버전이 든 User-Agent 만 넣습니다 (사용자·PC 정보 없음).
public sealed class UpdateHttp : IDisposable
{
    public const int MaxRedirects = 5;

    private static readonly string[] AllowedHosts = ["api.github.com", "github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    private readonly HttpClient _client;
    private readonly UpdateLimits _limits;

    /// handler 를 주면 그것을 씁니다 (시험용 가짜). 없으면 리디렉션 자동 추적을 끈 기본 핸들러.
    public UpdateHttp(string productVersion, UpdateLimits limits, HttpMessageHandler? handler = null)
    {
        _limits = limits;
        _client = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = Timeout.InfiniteTimeSpan };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AsterCache", productVersion));
    }

    public static bool IsAllowed(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.UserInfo.Length == 0
        && AllowedHosts.Contains(uri.IdnHost, StringComparer.OrdinalIgnoreCase);

    /// 응답 본문 전체 (metadata 상한 이내).
    public async Task<byte[]> GetBytesAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await ReadAsync(uri, buffer, _limits.MetadataBytes, _limits.MetadataTimeout, null, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    /// destination 에 본문을 씁니다 (installer 상한 이내). progress 는 (받은 바이트, 전체 바이트 또는 null).
    public Task DownloadAsync(Uri uri, Stream destination, Action<long, long?>? progress, CancellationToken cancellationToken) =>
        ReadAsync(uri, destination, _limits.InstallerBytes, _limits.DownloadTimeout, progress, cancellationToken);

    private async Task ReadAsync(Uri uri, Stream destination, long maxBytes, TimeSpan timeout, Action<long, long?>? progress, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        try
        {
            using HttpResponseMessage response = await FollowAsync(uri, cts.Token).ConfigureAwait(false);
            long? total = response.Content.Headers.ContentLength;
            if (total > maxBytes)
            {
                throw new UpdateException("response is larger than the limit");
            }

            using Stream body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            byte[] chunk = new byte[81920];
            long got = 0;
            int n;
            while ((n = await body.ReadAsync(chunk, cts.Token).ConfigureAwait(false)) > 0)
            {
                got += n;
                if (got > maxBytes)
                {
                    throw new UpdateException("response is larger than the limit");
                }

                await destination.WriteAsync(chunk.AsMemory(0, n), cts.Token).ConfigureAwait(false);
                progress?.Invoke(got, total);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpdateException("request timed out");
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException("request failed: " + e.GetType().Name + (e.StatusCode is { } s ? " " + (int)s : string.Empty));
        }
        catch (IOException e)
        {
            // 본문을 읽는 중 연결이 끊기면 HttpIOException (IOException 계열) 이 나고, 쓰기 실패도 같은 길로 옴
            throw new UpdateException("reading the response failed: " + e.GetType().Name);
        }
    }

    private async Task<HttpResponseMessage> FollowAsync(Uri start, CancellationToken cancellationToken)
    {
        Uri current = start;
        for (int hop = 0; hop <= MaxRedirects; hop++)
        {
            if (!IsAllowed(current))
            {
                throw new UpdateException("address is not allowed (https and the GitHub hosts only)");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            HttpResponseMessage response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                Uri? location = response.Headers.Location;
                response.Dispose();
                current = location is null ? throw new UpdateException("redirect without a location") : location.IsAbsoluteUri ? location : new Uri(current, location);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                HttpStatusCode code = response.StatusCode;
                response.Dispose();
                throw new UpdateException("server answered " + (int)code);
            }

            return response;
        }

        throw new UpdateException("too many redirects");
    }

    public void Dispose() => _client.Dispose();
}
