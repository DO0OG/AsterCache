using System.Globalization;
using System.Text;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Trace;

namespace AsterCache.Core.Profiles;

/// 프로필에 담은 파일 하나: origin_root 기준 상대 경로 ('/' 구분), 크기, SampleHasher 표본 SHA-256.
public sealed record ProfileFile(string Path, long Size, string SampleSha256);

/// 가져오기 때 맞지 않은 파일. Reason: "missing" | "size" | "sample" | "outside_root" (파일 경로나 중간 폴더가 정션·심볼릭 링크 같은 재해석 지점). ActualSize 는 파일이 없으면 null.
public sealed record ProfileMismatch(string Path, string Reason, long ExpectedSize, long? ActualSize);

/// 게임 하나의 trace 를 다른 PC 와 주고받는 파일. 경로는 모두 origin_root 기준 상대 경로이고 (절대 경로·사용자 이름·드라이브 문자 없음),
/// 파일마다 크기와 표본 SHA-256 을 함께 담아 가져올 때 같은 파일인지 확인합니다. 계획은 담지 않습니다 (그 PC 의 볼륨에서 다시 만듦).
/// 받은 파일은 신뢰 경계 밖이라 알려진 키만, 정확한 형으로만 받고 모든 경로를 검사합니다.
public sealed class GameProfile
{
    public const string FormatName = "astercache-profile";
    public const int FormatVersion = 1;

    /// 가져온 프로필과 합성 트레이스가 plans 폴더에 쓰이는 이름의 끝 (<이름>.boot.trace.json). 실제로 수집한 원시 트레이스 (<이름>.trace.json) 와 달라서 첫 실행 기록을 막지 않습니다.
    public const string BootTraceSuffix = ".boot.trace.json";

    public const long MaxBytes = 64L << 20;
    public const int MaxFiles = 100_000;
    public const int MaxEvents = TraceEvents.MaxSessionEvents;

    private static readonly string[] Keys = ["format", "version", "created", "game", "exe_names", "files", "raw_trace", "prefetch_trace"];
    private static readonly string[] Reserved =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    private GameProfile(string created, string game, IReadOnlyList<string> exeNames, IReadOnlyList<ProfileFile> files, IReadOnlyList<TraceEvent> raw, IReadOnlyList<TraceEvent> prefetch)
    {
        Created = created;
        Game = game;
        ExeNames = exeNames;
        Files = files;
        RawTrace = raw;
        PrefetchTrace = prefetch;
    }

    public string Created { get; }

    public string Game { get; }

    public IReadOnlyList<string> ExeNames { get; }

    public IReadOnlyList<ProfileFile> Files { get; }

    /// 이벤트 경로는 상대 경로 ('/' 구분).
    public IReadOnlyList<TraceEvent> RawTrace { get; }

    public IReadOnlyList<TraceEvent> PrefetchTrace { get; }

    /// 상대 경로로 쓸 수 있는가: 비어 있지 않은 구성 요소들이 '/' 나 '\' 로 이어지고, ".."·"."·드라이브 문자·UNC·절대 경로·ADS·잘못된 이름이 없음.
    public static bool IsSafeRelative(string path)
    {
        if (path.Length == 0 || path.Length > 1024)
        {
            return false;
        }

        foreach (string part in path.Split('/', '\\'))
        {
            if (part.Length == 0 || part is "." or ".." || part[^1] is '.' or ' '
                || part.Any(c => c < 0x20 || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')
                || Reserved.Contains(part.Split('.')[0].ToUpperInvariant()))
            {
                return false;
            }
        }

        return true;
    }

    /// origin_root 밖이면 null. 대소문자를 무시하고 구성 요소 단위로 비교합니다.
    public static string? Relative(string path, string originRoot)
    {
        string root = Path.GetFullPath(originRoot).TrimEnd('\\');
        string full = Path.GetFullPath(path);
        if (full.Length <= root.Length + 1 || !TraceLoader.IsRelativeTo(full, root))
        {
            return null;
        }

        string rel = full[(root.Length + 1)..].Replace('\\', '/');
        return IsSafeRelative(rel) ? rel : null;
    }

    /// origin_root 아래 상대 경로의 어느 구성 요소 (파일 자신과 중간 폴더, origin_root 자체는 제외) 든 재해석 지점 (정션·심볼릭 링크) 인가. 없는 경로는 false.
    public static bool HasReparsePoint(string originRoot, string relative)
    {
        string cur = Path.GetFullPath(originRoot).TrimEnd('\\');
        foreach (string part in relative.Split('/', '\\'))
        {
            cur = Path.Combine(cur, part);
            try
            {
                if ((File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
            }
            catch (UnauthorizedAccessException)
            {
                return true; // 속성을 못 읽는 구성 요소는 믿지 않음
            }
            catch (IOException)
            {
                return false; // 없는 경로 (존재 여부는 호출자가 봄)
            }
        }

        return false;
    }

    public static string Absolute(string relative, string originRoot) => Path.Combine(originRoot, relative.Replace('/', '\\'));

    /// 트레이스 (절대 경로) 에서 프로필을 만듭니다. origin_root 밖이거나 없는 파일의 행은 뺍니다. 파일은 한 번만, 처음 본 대소문자로 담습니다.
    public static GameProfile Build(string game, IReadOnlyList<string> exeNames, string originRoot, IReadOnlyList<TraceEvent> raw, IReadOnlyList<TraceEvent> prefetch, DateTimeOffset now,
        Func<string, string, bool>? isReparse = null)
    {
        isReparse ??= HasReparsePoint;
        var files = new Dictionary<string, ProfileFile>(StringComparer.OrdinalIgnoreCase);
        List<TraceEvent> Rebase(IReadOnlyList<TraceEvent> events)
        {
            var rows = new List<TraceEvent>(events.Count);
            foreach (TraceEvent e in events)
            {
                if (Relative(e.Path, originRoot) is not { } rel)
                {
                    continue;
                }

                if (!files.TryGetValue(rel, out ProfileFile? f))
                {
                    string disk = Absolute(rel, originRoot);
                    if (!File.Exists(disk) || isReparse(originRoot, rel)) // 재해석 지점은 따라가지 않음 (대상이 origin_root 밖일 수 있음)
                    {
                        continue;
                    }

                    long size = new FileInfo(disk).Length;
                    files[rel] = f = new ProfileFile(rel, size, SampleHasher.Compute(disk, size, null, null));
                }

                rows.Add(e with { Path = f.Path });
            }

            return rows;
        }

        List<TraceEvent> rawRows = Rebase(raw);
        List<TraceEvent> preRows = Rebase(prefetch);
        string created = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return new GameProfile(created, game, exeNames, files.Values.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(), rawRows, preRows);
    }

    public JObject ToJson() => new()
    {
        { "format", FormatName },
        { "version", (long)FormatVersion },
        { "created", Created },
        { "game", Game },
        { "exe_names", ExeNames.Cast<object?>().ToList() },
        { "files", Files.Select(f => (object?)new JObject { { "path", f.Path }, { "size", f.Size }, { "sample_sha256", f.SampleSha256 } }).ToList() },
        { "raw_trace", RawTrace.Select(e => (object?)e.ToJson()).ToList() },
        { "prefetch_trace", PrefetchTrace.Select(e => (object?)e.ToJson()).ToList() },
    };

    public byte[] ToBytes() => PyJson.Utf8.GetBytes(PyJson.Dumps(ToJson(), ensureAscii: false, compact: true) + "\n");

    public static GameProfile ParseBytes(byte[] bytes)
    {
        if (bytes.Length > MaxBytes)
        {
            throw new PyValueException("profile too large");
        }

        int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return Parse(JsonRead.Parse(new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip)));
    }

    /// 형식·경로·값 범위를 모두 검사합니다 (잘못되면 PyValueException). 이벤트는 파일 목록에 있는 경로만 받습니다.
    public static GameProfile Parse(object? json)
    {
        JObject o = json as JObject ?? throw new PyValueException("profile: object expected");
        foreach (string k in o.Select(kv => kv.Key))
        {
            if (!Keys.Contains(k))
            {
                throw new PyValueException($"profile: unknown key {k}");
            }
        }

        if (!o.TryGetValue("format", out object? fmt) || fmt as string != FormatName)
        {
            throw new PyValueException("profile: not an AsterCache profile");
        }

        if (!o.TryGetValue("version", out object? ver) || ver as long? != FormatVersion)
        {
            throw new PyValueException("profile: unsupported version");
        }

        string created = o.TryGetValue("created", out object? c) && c is string cs && cs.Length <= 40 ? cs : throw new PyValueException("profile.created: string expected");
        string game = o.TryGetValue("game", out object? g) && g is string gs && gs.Length is >= 1 and <= 64 ? gs : throw new PyValueException("profile.game: 1..64 chars expected");
        if (!o.TryGetValue("exe_names", out object? x) || x is not List<object?> exes || exes.Count > 16
            || exes.Any(e => e is not string s || s.Length is 0 or > 260 || s.IndexOfAny(['/', '\\', ':']) >= 0))
        {
            throw new PyValueException("profile.exe_names: list of 0..16 names expected");
        }

        if (!o.TryGetValue("files", out object? fl) || fl is not List<object?> fileRows || fileRows.Count > MaxFiles)
        {
            throw new PyValueException($"profile.files: list of 0..{MaxFiles} expected");
        }

        var files = new List<ProfileFile>(fileRows.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (object? row in fileRows)
        {
            if (row is not JObject r || r.Count != 3 || !r.TryGetValue("path", out object? p) || p is not string path
                || !r.TryGetValue("size", out object? sz) || sz is not long size || size < 0
                || !r.TryGetValue("sample_sha256", out object? h) || h is not string sha)
            {
                throw new PyValueException("profile.files: {path, size, sample_sha256} expected");
            }

            if (!IsSafeRelative(path) || path.Contains('\\', StringComparison.Ordinal))
            {
                throw new PyValueException("profile.files: unsafe path");
            }

            if (sha.Length != 64 || !sha.All(ch => ch is (>= '0' and <= '9') or (>= 'a' and <= 'f')) || !seen.Add(path))
            {
                throw new PyValueException("profile.files: bad sample_sha256 or duplicate path");
            }

            files.Add(new ProfileFile(path, size, sha));
        }

        return new GameProfile(created, game, exes.Cast<string>().ToList(), files, Events(o, "raw_trace", seen), Events(o, "prefetch_trace", seen));
    }

    /// 가져온 파일마다 크기·표본이 맞는지 확인합니다. 맞지 않은 파일의 목록을 돌려줍니다 (비어 있으면 모두 일치).
    public List<ProfileMismatch> Verify(string originRoot, Func<string, string, bool>? isReparse = null)
    {
        isReparse ??= HasReparsePoint;
        string root = Path.GetFullPath(originRoot).TrimEnd('\\');
        var bad = new List<ProfileMismatch>();
        foreach (ProfileFile f in Files)
        {
            string disk = Path.GetFullPath(Absolute(f.Path, root));
            if (!TraceLoader.IsRelativeTo(disk, root) || !File.Exists(disk))
            {
                bad.Add(new ProfileMismatch(f.Path, "missing", f.Size, null));
                continue;
            }

            if (isReparse(root, f.Path))
            {
                bad.Add(new ProfileMismatch(f.Path, "outside_root", f.Size, null)); // shortcut: 대상이 안쪽이어도 재해석 지점이면 거부 (대상 해석 없이 단순하게)
                continue;
            }

            long actual = new FileInfo(disk).Length;
            if (actual != f.Size)
            {
                bad.Add(new ProfileMismatch(f.Path, "size", f.Size, actual));
            }
            else if (SampleHasher.Compute(disk, actual, null, null) != f.SampleSha256)
            {
                bad.Add(new ProfileMismatch(f.Path, "sample", f.Size, actual));
            }
        }

        return bad;
    }

    /// 상대 경로 이벤트를 origin_root 아래 절대 경로 이벤트로 바꾸고 trace 파일 본문 (CRLF) 으로 직렬화합니다.
    public static byte[] TraceBytes(IReadOnlyList<TraceEvent> events, string originRoot)
    {
        string text = PyJson.Dumps(events.Select(e => (object?)(e with { Path = Absolute(e.Path, originRoot) }).ToJson()).ToList(), ensureAscii: false) + "\n";
        return PyJson.Utf8.GetBytes(PyJson.ToCrlf(text));
    }

    private static List<TraceEvent> Events(JObject o, string key, HashSet<string> files)
    {
        if (!o.TryGetValue(key, out object? v))
        {
            return [];
        }

        if (v is not List<object?> rows || rows.Count > MaxEvents)
        {
            throw new PyValueException($"profile.{key}: list of 0..{MaxEvents} expected");
        }

        var events = new List<TraceEvent>(rows.Count);
        foreach (object? row in rows)
        {
            TraceEvent e = TraceEvents.FromJson(row);
            if (!files.Contains(e.Path) || e.Path.Contains('\\', StringComparison.Ordinal) || e.Offset < 0 || e.Length <= 0 || e.Offset > long.MaxValue - e.Length
                || !double.IsFinite(e.Cost) || e.Cost < 0)
            {
                throw new PyValueException($"profile.{key}: bad event");
            }

            events.Add(e);
        }

        return events;
    }
}
