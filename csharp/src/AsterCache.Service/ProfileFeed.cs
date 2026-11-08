using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Text.RegularExpressions;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Profiles;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 묶음 zip 하나를 받는 HTTP 한 번의 답. 304 면 Body 없음, 200 이면 Body 가 본문 스트림 (닫으면 연결도 닫힘).
public sealed class FeedReply(int status, string? etag = null, Stream? body = null, long? length = null, IDisposable? owner = null, CancellationToken token = default) : IDisposable
{
    public int Status { get; } = status;

    public string? ETag { get; } = etag;

    public Stream? Body { get; } = body;

    /// 본문을 읽는 동안 지켜야 할 취소 토큰 (전체 시간 상한).
    public CancellationToken Token { get; } = token;

    /// 헤더가 알린 본문 길이 (모르면 null).
    public long? Length { get; } = length;

    public void Dispose()
    {
        Body?.Dispose();
        owner?.Dispose();
    }
}

/// 묶음 zip 을 받는 곳. 시험은 파일 기반 가짜로 바꿉니다. 실패는 HttpRequestException·IOException·TaskCanceledException 으로 알립니다.
public interface IFeedHttp
{
    /// etag 가 있으면 If-None-Match 로 보냅니다. stop 은 서비스 종료 신호로, 헤더 대기와 본문 읽기 모두를 끊습니다.
    FeedReply Get(string url, string? etag, CancellationToken stop = default);
}

/// 실제 HTTP (HttpClient): 헤더는 60 초 안에, 본문까지 전체는 5 분 안에 받아야 합니다. 리디렉션은 따르되 https 에서 http 로는 가지 않습니다 (HttpClient 기본).
public sealed class HttpFeedClient : IFeedHttp
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5); // 전체 다운로드 상한

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) }; // 헤더 대기. 본문 포함 전체 상한은 요청마다 취소 토큰으로

    public FeedReply Get(string url, string? etag, CancellationToken stop = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        cts.CancelAfter(Timeout);
        HttpResponseMessage? response = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("AsterCache-profile-feed");
            if (etag is not null)
            {
                request.Headers.TryAddWithoutValidation("If-None-Match", etag);
            }

            response = Client.Send(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            Stream? body = response.StatusCode == HttpStatusCode.OK ? response.Content.ReadAsStream(cts.Token) : null;
            return new FeedReply((int)response.StatusCode, response.Headers.ETag?.ToString(), body, response.Content.Headers.ContentLength, new Owner(response, cts), cts.Token);
        }
        catch
        {
            response?.Dispose();
            cts.Dispose();
            throw;
        }
    }

    private sealed class Owner(HttpResponseMessage response, CancellationTokenSource cts) : IDisposable
    {
        public void Dispose()
        {
            response.Dispose();
            cts.Dispose();
        }
    }
}

/// 공유 프로필 묶음 (auto.shared_profiles): 다른 PC 에서 만든 기록을 받아, 설치 파일이 같다고 확인된 게임에만 첫 실행 없이 쓸 수 있는 부트 트레이스 (plans\<이름>.boot.trace.json) 로 둡니다.
/// 서비스 시작 뒤 첫 발견 주기와 그 뒤 7 일마다 묶음 zip 하나만 받고 (개별 조회 없음, ETag 로 304 면 캐시 사용), 매 발견 주기마다 캐시를 새 게임에 맞춰 봅니다.
/// 묶음은 신뢰 경계 밖입니다: 다운로드 상한 64 MiB (읽는 중 넘으면 즉시 중단), zip 항목 수·해제 크기 상한, 안전하지 않은 항목 이름이 하나라도 있으면 묶음 전체 거부,
/// 프로필은 GameProfile 의 형식 검사와 Verify (파일별 크기·표본 SHA) 를 모두 통과해야 저장합니다. 서비스만 캐시를 씁니다.
/// index.json: {"format": "astercache-profile-index", "version": 1, "entries": [{"steam": {"appid": "...", "depots": {"<depot>": "<manifest gid>"}} | "epic": {"app_name": "...", "version": "..."} | "exe": {"size": N, "sample_sha256": "..."}, "profile": "<zip 안 프로필 .json 경로>"}]}.
public sealed class ProfileFeed
{
    public const string IndexName = "index.json";
    public const string IndexFormat = "astercache-profile-index";
    public const long MaxDownloadBytes = 64L << 20;
    public const int MaxZipEntries = 4096;
    public const long MaxZipBytes = 256L << 20; // zip 이 말하는 해제 크기의 합, 그리고 한 번에 풀어 읽는 프로필 바이트의 합
    public const long MaxIndexBytes = 4L << 20;
    public const int MaxMatchesTried = 3;
    public const string StateFormat = "astercache-profile-feed-state";
    public static readonly TimeSpan Every = TimeSpan.FromDays(7);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1); // 일시 오류 백오프의 시작: 연속 실패마다 2 배
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(24); // 백오프 상한: 하루 한 번보다 잦지 않고, 복구된 서버를 하루 안에 다시 만남
    public static readonly TimeSpan CorruptRetryCap = TimeSpan.FromDays(7); // 같은 표지의 손상 묶음을 이 기간이 지나면 한 번 더 받아 봄
    public static readonly TimeSpan RetryAfterNoMatch = TimeSpan.FromDays(1);
    private static readonly Regex LeftoverTemp = new(@"^feed\.(zip|state\.json)\.[0-9a-f]{32}\.tmp\z", RegexOptions.None, TimeSpan.FromSeconds(1)); // Fetch 가 만드는 GUID 임시 파일 (zip·상태 파일)

    private readonly string _dir;
    private readonly IFeedHttp _http;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, IReadOnlyList<(string Path, long Size)>> _exeFiles;
    private readonly Dictionary<string, DateTimeOffset> _nextTry = new(StringComparer.OrdinalIgnoreCase); // 게임 이름 → 다시 맞춰 볼 시각
    private readonly object _gate = new(); // Run 직렬화: 도는 중에 또 불리면 건너뜀
    private DateTimeOffset _nextFetch = DateTimeOffset.MinValue;
    private bool _persistFetch = true; // https 주소가 아니라 받지 않은 경우의 _nextFetch 는 파일에 남기지 않음 (주소를 고치면 재시작으로 바로 받게)
    private string _stamp = string.Empty;
    private int _failures; // 연속 실패 횟수 (성공·304 면 0)
    private string? _error; // 마지막 오류 분류: transient (네트워크·시간 초과·입출력) / corrupt (묶음 내용 손상) / http (200·304 가 아닌 응답)
    private string? _badMarker; // 손상으로 거부한 묶음의 표지 (ETag, 없으면 헤더 길이)
    private DateTimeOffset _badSince = DateTimeOffset.MinValue;
    private string? _savedState; // 마지막으로 파일과 맞춘 상태 문장 (같으면 다시 쓰지 않음)

    public ProfileFeed(string dataDir, IFeedHttp http, ILog log, Func<DateTimeOffset> now, Func<string, IReadOnlyList<(string Path, long Size)>> exeFiles)
    {
        _dir = Path.Combine(dataDir, "profiles");
        _http = http;
        _log = log;
        _now = now;
        _exeFiles = exeFiles;
        LoadState();
    }

    public string ZipPath => Path.Combine(_dir, "feed.zip");

    public string EtagPath => Path.Combine(_dir, "feed.etag");

    /// 받기 백오프와 게임별 재시도 시각 (config.json 이 아니라 여기에 둠). 없거나 깨졌으면 무시합니다.
    public string StatePath => Path.Combine(_dir, "feed.state.json");

    /// 받을 때가 되었으면 받고, 캐시를 아직 맞춰 보지 않은 게임에 맞춰 저장합니다. 저장한 부트 트레이스 수를 돌려줍니다. 한 번에 한 줄만 로그를 남깁니다 (할 일이 없으면 남기지 않음).
    /// stop 은 서비스 종료 신호 (호출자가 주지 않으면 none): 종료로 끊긴 받기는 실패로 세지 않고, 시간 초과만 일시 오류로 셉니다.
    public int Run(AsterSettings s, IReadOnlyList<DiscoveredGame> scan, CancellationToken stop = default)
    {
        if (!Monitor.TryEnter(_gate))
        {
            return 0; // 이미 도는 중: 같은 요청을 겹쳐 보내지 않음
        }

        try
        {
            return RunLocked(s, scan, stop);
        }
        finally
        {
            Monitor.Exit(_gate);
        }
    }

    private int RunLocked(AsterSettings s, IReadOnlyList<DiscoveredGame> scan, CancellationToken stop)
    {
        SharedProfilesSettings cfg = s.Auto.SharedProfiles;
        if (!s.Auto.Enabled || !cfg.Enabled)
        {
            return 0;
        }

        var parts = new List<string>();
        DateTimeOffset now = _now();
        ClampToNow(now); // 실행 중 시계가 되감겨도 분류별 최대 대기를 넘기지 않음
        if (now >= _nextFetch)
        {
            parts.Add("download " + Fetch(cfg.Url, now, stop));
            SaveState(); // 적용 도중 끝나도 받기 결과 (다음 요청 시각) 는 남김
        }

        foreach (string gone in _nextTry.Keys.Where(k => !s.Games.Any(g => string.Equals(g.Name, k, StringComparison.OrdinalIgnoreCase))).ToList())
        {
            _nextTry.Remove(gone); // 설정에서 빠진 게임
        }

        int saved = Apply(s, scan, now, parts);
        SaveState();
        if (parts.Count > 0)
        {
            _log.Info("shared profiles: " + string.Join(", ", parts));
        }

        return saved;
    }

    // 묶음을 받아 검증이 끝난 뒤에만 캐시를 바꿉니다. 결과 문구를 돌려줍니다 (예외는 삼킴).
    private string Fetch(string url, DateTimeOffset now, CancellationToken stop)
    {
        DeleteLeftoverTemps(); // 요청 전: 이전 실행이 받는 도중 죽어 남긴 임시 파일 정리
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            _nextFetch = now + Every;
            _persistFetch = false;
            return "skipped (not an https url)";
        }

        _persistFetch = true;
        (int Failures, string? Error, DateTimeOffset Next) before = (_failures, _error, _nextFetch);
        Failed("transient", now, before.Failures); // 요청 도중 프로세스가 죽어도 백오프가 남도록, 이번 시도가 실패한 것으로 가정한 다음 시각을 요청 전에 저장. 끝나면 실제 결과로 바꿈
        if (!SaveState())
        {
            (_failures, _error, _nextFetch) = before; // 요청 전 기록을 남기지 못하면 요청하지 않음: 다음 Run 에서 다시 시도
            return "skipped (state not saved)";
        }

        string tmp = ZipPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string? marker = null;
        try
        {
            Directory.CreateDirectory(_dir);
            string? etag = File.Exists(ZipPath) && File.Exists(EtagPath) ? File.ReadAllText(EtagPath).Trim() : null;
            using FeedReply reply = _http.Get(url, etag is { Length: > 0 } ? etag : null, stop);
            if (reply.Status == 304 && File.Exists(ZipPath))
            {
                Succeeded(now);
                return "not modified (cache used)";
            }

            if (reply.Status != 200 || reply.Body is null)
            {
                // 200·304 가 아닌 HTTP 응답은 서버가 답을 한 것이므로 상태 코드와 무관하게 Every 뒤에 다시 (요청 빈도를 늘리지 않음). 1 시간 백오프는 응답이 아예 없는 예외에만
                Failed("http", now, before.Failures);
                return "unavailable (HTTP " + reply.Status.ToString(CultureInfo.InvariantCulture) + ")";
            }

            marker = reply.ETag is { Length: > 0 } etagMark ? etagMark : reply.Length is { } len ? "len:" + len.ToString(CultureInfo.InvariantCulture) : null;
            if (marker is not null && marker == _badMarker && now < (_badSince < now ? _badSince : now) + CorruptRetryCap)
            {
                Failed("corrupt", now, before.Failures); // 이미 거부한 그 묶음: 본문은 읽지 않고 표지가 바뀌거나 상한이 지나길 기다림
                return "skipped (same bundle as the rejected one)";
            }

            long size = Download(reply, tmp, stop);
            CheckZip(tmp);
            File.Move(tmp, ZipPath, true);
            if (reply.ETag is { Length: > 0 } tag)
            {
                File.WriteAllText(EtagPath, tag);
            }
            else if (File.Exists(EtagPath))
            {
                File.Delete(EtagPath);
            }

            Succeeded(now);
            return "ok (" + (size >> 10).ToString(CultureInfo.InvariantCulture) + " KiB)";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or PyValueException or OperationCanceledException)
        {
            if (stop.IsCancellationRequested) // 종료 신호가 켜졌으면 예외 종류와 무관하게 종료 (IOException·HttpRequestException 으로 감싸져 올라오는 경우 포함)
            {
                (_failures, _error) = (before.Failures, before.Error); // 서비스 종료로 끊김: 실패 횟수는 되돌리되, 재시작 반복으로 요청이 잦아지지 않게 다음 시각은 앞당기지 않음
                _nextFetch = before.Next > now + RetryAfterFailure ? before.Next : now + RetryAfterFailure;
                return "cancelled (service stopping)";
            }

            bool corrupt = e is InvalidDataException or PyValueException; // 내용 손상. 그 밖은 일시 오류
            if (corrupt)
            {
                _badMarker = marker;
                _badSince = now;
            }

            Failed(corrupt ? "corrupt" : "transient", now, before.Failures); // 이전 묶음 (ZipPath) 은 그대로 둠: 새 묶음은 검증을 마친 뒤에만 옮김
            return "failed (" + (e is InvalidDataException or PyValueException ? e.Message : e.GetType().Name) + ")";
        }
        finally
        {
            try
            {
                File.Delete(tmp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 다음 받기가 덮어씀
            }
        }
    }

    // 이전 실행이 받는 도중 죽어 남긴 임시 파일 (zip·상태 파일의 GUID 임시본)만 지움. 패턴이 정확히 맞는 것만, 지우기 실패는 무시.
    private void DeleteLeftoverTemps()
    {
        if (!Directory.Exists(_dir))
        {
            return;
        }

        foreach (string path in Directory.GetFiles(_dir, "*.tmp"))
        {
            if (!LeftoverTemp.IsMatch(Path.GetFileName(path)))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 지우지 못하면 다음 받기 때 다시 시도
            }
        }
    }

    private void Succeeded(DateTimeOffset now)
    {
        _failures = 0;
        _error = null;
        _badMarker = null;
        _nextFetch = now + Every;
    }

    // 이번 시도까지 실패로 세고 (prior = 시도 전 연속 실패 수) 다음 받기 시각을 정함: http 는 Every 뒤, corrupt 는 손상 판정 시각에서 CorruptRetryCap 뒤, 나머지는 1 시간에서 시작해 연속 실패마다 2 배 (상한 MaxBackoff).
    private void Failed(string kind, DateTimeOffset now, int prior)
    {
        _failures = prior < int.MaxValue ? prior + 1 : prior;
        _error = kind;
        if (kind == "http")
        {
            _nextFetch = now + Every;
            return;
        }

        if (kind == "corrupt")
        {
            _nextFetch = (_badMarker is null || _badSince > now ? now : _badSince) + CorruptRetryCap; // 그 사이에는 표지 확인용 요청도 보내지 않음. 표지가 없으면 지금부터
            return;
        }

        TimeSpan delay = RetryAfterFailure;
        for (int i = 1; i < _failures && delay < MaxBackoff; i++)
        {
            delay *= 2;
        }

        _nextFetch = now + (delay < MaxBackoff ? delay : MaxBackoff);
    }

    // 시작할 때 상태 파일을 읽음. 없거나 형식이 어긋나면 통째로 무시 (기본 동작: 바로 받아 볼 수 있음).
    private void LoadState()
    {
        try
        {
            if (!File.Exists(StatePath) || JsonRead.ReadFile(StatePath) is not JObject o)
            {
                return;
            }

            string? Str(string key) => o.TryGetValue(key, out object? v) ? v as string : null;
            DateTimeOffset? Time(string key) => DateTimeOffset.TryParse(Str(key), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset t) ? t : null;

            if (Str("format") != StateFormat || !o.TryGetValue("version", out object? version) || version as long? != 1)
            {
                return;
            }

            DateTimeOffset now = _now();
            bool clamped = false;
            if (Time("next_fetch") is { } at)
            {
                string? error = Str("error");
                _error = error is "transient" or "corrupt" or "http" ? error : null;
                _nextFetch = at;
                if (o.TryGetValue("failures", out object? n) && n is long count and > 0 and < 100000)
                {
                    _failures = (int)count;
                }
            }

            // bad_since 는 지금 기준 [-CorruptRetryCap, 0] 안일 때만 믿음 (밖이면 표지째 무시: 그 묶음을 한 번 더 받아 봄)
            if (Time("bad_since") is { } since && since <= now && since >= now - CorruptRetryCap)
            {
                _badMarker = Str("bad_marker");
                _badSince = since;
            }

            _stamp = Str("stamp") ?? string.Empty;
            if (o.TryGetValue("next_try", out object? tries) && tries is JObject map)
            {
                foreach ((string name, object? value) in map)
                {
                    if (value is string text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
                    {
                        if (parsed > now + RetryAfterNoMatch)
                        {
                            parsed = now + RetryAfterNoMatch; // 게임별 상한 (MaxValue 와 같은 값도 여기서 줄임)
                            clamped = true;
                        }

                        _nextTry[name] = parsed;
                    }
                }
            }

            _savedState = StateText();
            if (ClampToNow(now) || clamped)
            {
                _savedState = null; // 상한으로 보정한 값은 파일에도 다시 씀
                SaveState();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            // 깨진 파일: 무시. 다음 저장이 덮어씀
        }
    }

    // 분류별 상한 (시계가 되감겼거나 값이 비정상이어도 그 분류의 최대 대기를 넘지 않게) 과 게임별 상한을 적용. 바뀌었으면 true. 끝난 게임 (MaxValue) 은 건드리지 않음.
    private bool ClampToNow(DateTimeOffset now)
    {
        bool changed = false;
        TimeSpan cap = _error switch { "transient" => MaxBackoff, "corrupt" => CorruptRetryCap, _ => Every };
        if (_nextFetch > now + cap)
        {
            _nextFetch = now + cap;
            changed = true;
        }

        foreach (string name in _nextTry.Where(p => p.Value != DateTimeOffset.MaxValue && p.Value > now + RetryAfterNoMatch).Select(p => p.Key).ToList())
        {
            _nextTry[name] = now + RetryAfterNoMatch;
            changed = true;
        }

        return changed;
    }

    // 저장할 상태 문장. 기록할 것이 없으면 null (파일을 두지 않음). 끝난 게임 (MaxValue) 은 재시작하면 다시 맞춰 보므로 저장하지 않음.
    private string? StateText()
    {
        var tries = new JObject();
        foreach ((string name, DateTimeOffset at) in _nextTry.Where(p => p.Value != DateTimeOffset.MaxValue).OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            tries.Add(name, at.ToString("O", CultureInfo.InvariantCulture));
        }

        bool hasFetch = _persistFetch && _nextFetch != DateTimeOffset.MinValue; // 성공·304 뒤의 다음 정기 요청 시각도 남김: 재시작마다 받지 않게
        if (!hasFetch && _failures == 0 && tries.Count == 0)
        {
            return null;
        }

        var root = new JObject { { "format", StateFormat }, { "version", 1L }, { "failures", (long)_failures } };
        if (hasFetch)
        {
            if (_error is not null)
            {
                root.Add("error", _error);
            }

            root.Add("next_fetch", _nextFetch.ToString("O", CultureInfo.InvariantCulture));
        }

        if (_badMarker is not null)
        {
            root.Add("bad_marker", _badMarker);
            root.Add("bad_since", _badSince.ToString("O", CultureInfo.InvariantCulture));
        }

        root.Add("stamp", _stamp);
        root.Add("next_try", tries);
        return PyJson.Dumps(root, indent: 2, ensureAscii: false) + "\n";
    }

    // 상태 파일을 임시 파일 → 이동으로 원자적으로 씀. 실패해도 메모리 상태로 계속 (다음 저장 때 다시). 맞춰진 상태면 true, 쓰지 못했으면 false.
    private bool SaveState()
    {
        string? text = StateText();
        if (text == _savedState)
        {
            return true;
        }

        try
        {
            if (text is null)
            {
                File.Delete(StatePath);
            }
            else
            {
                Directory.CreateDirectory(_dir);
                string tmp = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    DurableFile.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(text));
                    File.Move(tmp, StatePath, true);
                }
                catch
                {
                    try
                    {
                        File.Delete(tmp);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // 임시 파일을 못 지워도 원래 예외를 가리지 않음
                    }

                    throw;
                }
            }

            _savedState = text;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false; // 상태는 보조 정보: 쓰지 못하면 다음 주기에 다시 시도
        }
    }

    // 본문을 임시 파일로 받음. 헤더가 큰 길이를 말하거나 읽는 중 상한을 넘으면 곧바로 멈춤.
    private static long Download(FeedReply reply, string tmp, CancellationToken stop)
    {
        if (reply.Length > MaxDownloadBytes)
        {
            throw new InvalidDataException("download too large");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(reply.Token, stop);
        long total = 0;
        byte[] buffer = new byte[81920];
        using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
        int n;
        while ((n = reply.Body!.ReadAsync(buffer, linked.Token).AsTask().GetAwaiter().GetResult()) > 0)
        {
            total += n;
            if (total > MaxDownloadBytes)
            {
                throw new InvalidDataException("download too large");
            }

            fs.Write(buffer, 0, n);
        }

        return total;
    }

    private static void CheckZip(string path)
    {
        using var fs = File.OpenRead(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        _ = ReadIndex(zip, new Budget());
    }

    // 캐시를 아직 맞춰 보지 않은 게임 (원시 트레이스가 없는 것) 을 맞춰 부트 트레이스를 저장합니다.
    private int Apply(AsterSettings s, IReadOnlyList<DiscoveredGame> scan, DateTimeOffset now, List<string> parts)
    {
        if (!File.Exists(ZipPath))
        {
            return 0;
        }

        string stamp;
        try
        {
            var info = new FileInfo(ZipPath);
            stamp = info.Length.ToString(CultureInfo.InvariantCulture) + ":" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        if (stamp != _stamp)
        {
            _stamp = stamp;
            _nextTry.Clear();
        }

        List<GameSettings> todo = s.Games.Where(g => !(_nextTry.TryGetValue(g.Name, out DateTimeOffset at) && now < at) && !File.Exists(AutoTracer.RawTracePath(g))).ToList();
        if (todo.Count == 0)
        {
            return 0;
        }

        int saved = 0;
        try
        {
            using var fs = File.OpenRead(ZipPath);
            using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
            Budget budget = new();
            IReadOnlyList<IndexEntry> index = ReadIndex(zip, budget);
            foreach (GameSettings g in todo)
            {
                DiscoveredGame? found = scan.FirstOrDefault(d => SamePath(d.InstallDir, g.OriginRoot));
                bool? written = TryApply(g, found, index, zip, budget);
                _nextTry[g.Name] = written is null ? now + RetryAfterNoMatch : DateTimeOffset.MaxValue; // 맞는 프로필이 있으면 이 묶음으로는 끝
                if (written == true)
                {
                    saved++;
                    parts.Add("saved boot trace for " + g.Name);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or PyValueException)
        {
            foreach (GameSettings g in todo)
            {
                _nextTry.TryAdd(g.Name, now + RetryAfterNoMatch);
            }

            parts.Add("cache unusable (" + (e is InvalidDataException or PyValueException ? e.Message : e.GetType().Name) + ")");
        }

        return saved;
    }

    // 맞는 프로필을 지문 순서 (steam → epic → exe) 로 찾아 검증이 되면 저장. 맞는 프로필이 있어 저장 대상이 되면 (이미 같은 것이 있어 쓰지 않았어도) 쓴 여부를, 없으면 null.
    private bool? TryApply(GameSettings g, DiscoveredGame? found, IReadOnlyList<IndexEntry> index, ZipArchive zip, Budget budget)
    {
        int tried = 0;
        foreach (IndexEntry entry in Matches(g, found, index))
        {
            if (++tried > MaxMatchesTried)
            {
                break;
            }

            try
            {
                GameProfile profile = GameProfile.ParseBytes(ReadEntry(zip, entry.Profile, GameProfile.MaxBytes, budget));
                if (profile.RawTrace.Count == 0 || profile.Verify(g.OriginRoot).Count > 0)
                {
                    continue; // 위조·다른 빌드·내용 없음: 다음 후보
                }

                return Save(g, GameProfile.TraceBytes(profile.RawTrace, g.OriginRoot, GameProfile.BootSourceShared));
            }
            catch (Exception e) when (e is PyValueException or InvalidDataException or IOException or UnauthorizedAccessException or FormatException or ArgumentException)
            {
                if (e is InvalidDataException && budget.Bytes > MaxZipBytes)
                {
                    throw; // 해제 총량 상한: 묶음 전체를 멈춤
                }
            }
        }

        return null;
    }

    private IEnumerable<IndexEntry> Matches(GameSettings g, DiscoveredGame? found, IReadOnlyList<IndexEntry> index)
    {
        if (found?.Steam is { } steam)
        {
            foreach (IndexEntry e in index.Where(x => x.Steam is { } k && k.AppId == steam.AppId && k.Depots.All(d => steam.InstalledDepots.TryGetValue(d.Key, out string? gid) && gid == d.Value)))
            {
                yield return e;
            }
        }

        if (found?.Epic is { AppVersionString.Length: > 0 } epic)
        {
            foreach (IndexEntry e in index.Where(x => x.Epic is { } k && k.AppName == epic.AppName && k.Version == epic.AppVersionString))
            {
                yield return e;
            }
        }

        if (index.Any(x => x.Exe is not null) && ExeFingerprint(g) is { } fp)
        {
            foreach (IndexEntry e in index.Where(x => x.Exe is { } k && k.Size == fp.Size && k.Sha == fp.Sha))
            {
                yield return e;
            }
        }
    }

    // 게임의 첫 exe (설정 이름 순서대로 폴더 아래에서 찾은 것, 같은 이름이 여럿이면 가장 큰 것) 의 크기와 표본 SHA-256.
    private (long Size, string Sha)? ExeFingerprint(GameSettings g)
    {
        IReadOnlyList<(string Path, long Size)> files = _exeFiles(g.OriginRoot);
        foreach (string name in g.ExeNames)
        {
            (string Path, long Size) best = files.Where(f => System.IO.Path.GetFileName(f.Path).Equals(name, StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.Size).FirstOrDefault();
            if (best.Path is not null)
            {
                try
                {
                    return (best.Size, SampleHasher.Compute(best.Path, best.Size, null, null));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    return null;
                }
            }
        }

        return null;
    }

    // 이미 같은 내용이 있거나 수동 가져오기 boot 가 있으면 쓰지 않음 (false), 썼으면 true. 임시 파일 → 이동으로 원자적.
    // 덮어쓰는 것은 source 가 "prefetch"·"shared" 인 boot 뿐: "import" 와 source 없는 옛 목록 형식 (beta.4 가져오기일 수 있음) 은 보호합니다.
    private bool Save(GameSettings g, byte[] bytes)
    {
        string path = AutoTracer.BootTracePath(g);
        if (File.Exists(path) && AutoTracer.TraceSourceOf(path) is not (PrefetchFiles.BootSource or GameProfile.BootSourceShared))
        {
            _log.Info($"shared profiles: {g.Name} keeps its imported boot trace");
            return false;
        }

        if (File.Exists(path) && new FileInfo(path).Length == bytes.Length && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes))
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, true);
        }
        catch
        {
            try
            {
                File.Delete(tmp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 남으면 사람이 지움
            }

            throw;
        }

        return true;
    }

    private static bool SamePath(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    // zip 의 구조 검사 (항목 수, 해제 크기 합, 이름) 뒤 index.json 을 읽어 항목 목록으로. 어긋나면 InvalidDataException·PyValueException.
    private static List<IndexEntry> ReadIndex(ZipArchive zip, Budget budget)
    {
        if (zip.Entries.Count > MaxZipEntries)
        {
            throw new InvalidDataException("zip: too many entries");
        }

        long declared = 0;
        foreach (ZipArchiveEntry e in zip.Entries)
        {
            declared += e.Length;
            if (declared > MaxZipBytes)
            {
                throw new InvalidDataException("zip: too large when unpacked");
            }

            if (e.FullName.Contains('\\', StringComparison.Ordinal) || !GameProfile.IsSafeRelative(e.FullName.TrimEnd('/')))
            {
                throw new InvalidDataException("zip: unsafe entry name");
            }
        }

        if (JsonRead.Parse(PyJson.Utf8.GetString(ReadEntry(zip, IndexName, MaxIndexBytes, budget))) is not JObject root
            || root.TryGetValue("format", out object? format) is false || format as string != IndexFormat
            || !root.TryGetValue("version", out object? version) || version as long? != 1
            || !root.TryGetValue("entries", out object? list) || list is not List<object?> rows || rows.Count > MaxZipEntries)
        {
            throw new PyValueException("index: not an AsterCache profile index");
        }

        var names = new HashSet<string>(zip.Entries.Select(e => e.FullName), StringComparer.Ordinal);
        var result = new List<IndexEntry>(rows.Count);
        foreach (object? row in rows)
        {
            IndexEntry entry = ParseEntry(row);
            if (!names.Contains(entry.Profile))
            {
                throw new PyValueException("index: profile missing from the zip");
            }

            result.Add(entry);
        }

        return result;
    }

    private static IndexEntry ParseEntry(object? row)
    {
        if (row is not JObject o || !o.TryGetValue("profile", out object? p) || p is not string profile || !profile.EndsWith(".json", StringComparison.Ordinal)
            || profile.Contains('\\', StringComparison.Ordinal) || !GameProfile.IsSafeRelative(profile))
        {
            throw new PyValueException("index: entry needs a safe profile path");
        }

        SteamKey? steam = null;
        EpicKey? epic = null;
        ExeKey? exe = null;
        if (o.TryGetValue("steam", out object? sv))
        {
            if (sv is not JObject s || !s.TryGetValue("appid", out object? id) || id is not string { Length: > 0 } appId)
            {
                throw new PyValueException("index: steam.appid expected");
            }

            var depots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (s.TryGetValue("depots", out object? dv))
            {
                if (dv is not JObject { Count: > 0 and <= 1024 } d)
                {
                    throw new PyValueException("index: steam.depots expected");
                }

                foreach ((string depot, object? gid) in d)
                {
                    depots[depot] = gid as string is { Length: > 0 } g ? g : throw new PyValueException("index: depot manifest gid expected");
                }
            }

            steam = new SteamKey(appId, depots);
        }

        if (o.TryGetValue("epic", out object? ev))
        {
            epic = ev is JObject e && e.TryGetValue("app_name", out object? an) && an is string { Length: > 0 } appName && e.TryGetValue("version", out object? vv) && vv is string { Length: > 0 } version
                ? new EpicKey(appName, version)
                : throw new PyValueException("index: epic.app_name and epic.version expected");
        }

        if (o.TryGetValue("exe", out object? xv))
        {
            exe = xv is JObject x && x.TryGetValue("size", out object? sz) && sz is long size and >= 0 && x.TryGetValue("sample_sha256", out object? h) && h is string { Length: 64 } sha
                && sha.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'))
                ? new ExeKey(size, sha)
                : throw new PyValueException("index: exe.size and exe.sample_sha256 expected");
        }

        return (steam is null ? 0 : 1) + (epic is null ? 0 : 1) + (exe is null ? 0 : 1) == 1
            ? new IndexEntry(profile, steam, epic, exe)
            : throw new PyValueException("index: exactly one of steam, epic, exe expected");
    }

    // 항목 하나를 풀어 읽음. 이름이 정확히 같은 것만, 말한 크기와 실제로 푼 크기 모두 limit 이하여야 하고, 푼 바이트는 budget 에 더해져 묶음 전체 상한 (MaxZipBytes) 을 넘으면 중단.
    private static byte[] ReadEntry(ZipArchive zip, string name, long limit, Budget budget)
    {
        ZipArchiveEntry entry = zip.Entries.FirstOrDefault(e => e.FullName == name) ?? throw new InvalidDataException("zip: entry missing");
        if (entry.Length > limit)
        {
            throw new InvalidDataException("zip: entry too large");
        }

        bool first = budget.Seen.Add(name); // 같은 항목을 여러 게임이 읽어도 묶음 예산에는 한 번만 셈
        using Stream s = entry.Open();
        using var ms = new MemoryStream();
        byte[] buf = new byte[81920];
        int n;
        while ((n = s.Read(buf)) > 0)
        {
            ms.Write(buf, 0, n);
            if (first)
            {
                budget.Bytes += n;
            }

            if (ms.Length > limit || budget.Bytes > MaxZipBytes)
            {
                throw new InvalidDataException(budget.Bytes > MaxZipBytes ? "zip: too large when unpacked" : "zip: entry too large");
            }
        }

        return ms.ToArray();
    }

    private sealed class Budget
    {
        public long Bytes;
        public HashSet<string> Seen { get; } = new(StringComparer.Ordinal);
    }

    private sealed record SteamKey(string AppId, IReadOnlyDictionary<string, string> Depots);

    private sealed record EpicKey(string AppName, string Version);

    private sealed record ExeKey(long Size, string Sha);

    private sealed record IndexEntry(string Profile, SteamKey? Steam, EpicKey? Epic, ExeKey? Exe);
}
