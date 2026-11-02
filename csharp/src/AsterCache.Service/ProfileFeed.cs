using System.Globalization;
using System.IO.Compression;
using System.Net;
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
    /// etag 가 있으면 If-None-Match 로 보냅니다.
    FeedReply Get(string url, string? etag);
}

/// 실제 HTTP (HttpClient): 헤더는 60 초 안에, 본문까지 전체는 5 분 안에 받아야 합니다. 리디렉션은 따르되 https 에서 http 로는 가지 않습니다 (HttpClient 기본).
public sealed class HttpFeedClient : IFeedHttp
{
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5); // 전체 다운로드 상한

    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(60) }; // 헤더 대기. 본문 포함 전체 상한은 요청마다 취소 토큰으로

    public FeedReply Get(string url, string? etag)
    {
        var cts = new CancellationTokenSource(Timeout);
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
    public static readonly TimeSpan Every = TimeSpan.FromDays(7);
    public static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(1);
    public static readonly TimeSpan RetryAfterNoMatch = TimeSpan.FromDays(1);

    private readonly string _dir;
    private readonly IFeedHttp _http;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, IReadOnlyList<(string Path, long Size)>> _exeFiles;
    private readonly Dictionary<string, DateTimeOffset> _nextTry = new(StringComparer.OrdinalIgnoreCase); // 게임 이름 → 다시 맞춰 볼 시각
    private DateTimeOffset _nextFetch = DateTimeOffset.MinValue;
    private string _stamp = string.Empty;

    public ProfileFeed(string dataDir, IFeedHttp http, ILog log, Func<DateTimeOffset> now, Func<string, IReadOnlyList<(string Path, long Size)>> exeFiles)
    {
        _dir = Path.Combine(dataDir, "profiles");
        _http = http;
        _log = log;
        _now = now;
        _exeFiles = exeFiles;
    }

    public string ZipPath => Path.Combine(_dir, "feed.zip");

    public string EtagPath => Path.Combine(_dir, "feed.etag");

    /// 받을 때가 되었으면 받고, 캐시를 아직 맞춰 보지 않은 게임에 맞춰 저장합니다. 저장한 부트 트레이스 수를 돌려줍니다. 한 번에 한 줄만 로그를 남깁니다 (할 일이 없으면 남기지 않음).
    public int Run(AsterSettings s, IReadOnlyList<DiscoveredGame> scan)
    {
        SharedProfilesSettings cfg = s.Auto.SharedProfiles;
        if (!s.Auto.Enabled || !cfg.Enabled)
        {
            return 0;
        }

        var parts = new List<string>();
        DateTimeOffset now = _now();
        if (now >= _nextFetch)
        {
            parts.Add("download " + Fetch(cfg.Url, now));
        }

        int saved = Apply(s, scan, now, parts);
        if (parts.Count > 0)
        {
            _log.Info("shared profiles: " + string.Join(", ", parts));
        }

        return saved;
    }

    // 묶음을 받아 검증이 끝난 뒤에만 캐시를 바꿉니다. 결과 문구를 돌려줍니다 (예외는 삼킴).
    private string Fetch(string url, DateTimeOffset now)
    {
        _nextFetch = now + Every;
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return "skipped (not an https url)";
        }

        string tmp = ZipPath + ".tmp";
        try
        {
            Directory.CreateDirectory(_dir);
            string? etag = File.Exists(ZipPath) && File.Exists(EtagPath) ? File.ReadAllText(EtagPath).Trim() : null;
            using FeedReply reply = _http.Get(url, etag is { Length: > 0 } ? etag : null);
            if (reply.Status == 304 && File.Exists(ZipPath))
            {
                return "not modified (cache used)";
            }

            if (reply.Status != 200 || reply.Body is null)
            {
                return "unavailable (HTTP " + reply.Status.ToString(CultureInfo.InvariantCulture) + ")";
            }

            long size = Download(reply, tmp);
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

            return "ok (" + (size >> 10).ToString(CultureInfo.InvariantCulture) + " KiB)";
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException or PyValueException or OperationCanceledException)
        {
            _nextFetch = now + RetryAfterFailure;
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

    // 본문을 임시 파일로 받음. 헤더가 큰 길이를 말하거나 읽는 중 상한을 넘으면 곧바로 멈춤.
    private static long Download(FeedReply reply, string tmp)
    {
        if (reply.Length > MaxDownloadBytes)
        {
            throw new InvalidDataException("download too large");
        }

        long total = 0;
        byte[] buffer = new byte[81920];
        using var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None);
        int n;
        while ((n = reply.Body!.ReadAsync(buffer, reply.Token).AsTask().GetAwaiter().GetResult()) > 0)
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
