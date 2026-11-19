using System.Globalization;
using AsterCache.Core.Json;

namespace AsterCache.Core.Trace;

/// 캐시를 거치지 않고만 읽힌다고 판정한 파일 (경로 → 판정할 때의 크기·수정 시각). 메모리로 예열해도 게임의 읽기가 쓰지 못하는 파일입니다.
public sealed record UnbufferedFileList(IReadOnlyDictionary<string, LoadRefillFileStat> Files, int Plays)
{
    public long Bytes => Files.Values.Sum(stat => stat.Size);

    /// 지금 파일의 크기·수정 시각 (없으면 null) 이 판정 때와 같은 파일만: 업데이트로 바뀐 파일의 판정은 버립니다.
    public HashSet<string> StillValid(Func<string, LoadRefillFileStat?> current)
    {
        var valid = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, LoadRefillFileStat saved) in Files)
        {
            if (current(path) is { } now && now == saved)
            {
                valid.Add(path);
            }
        }

        return valid;
    }
}

/// 저장된 플레이 집계들의 파일별 읽기 방식에서 "캐시를 거치지 않고만 읽힌 파일"을 고르는 순수 함수입니다.
public static class UnbufferedFileJudge
{
    /// 이 수 이상의 서로 다른 플레이에서 캐시 없이만 읽혀야 판정합니다 (한 번의 플레이만으로는 판정하지 않음).
    public const int MinPlays = 2;

    /// 판정 조건: 읽기 방식을 기록한 서로 다른 플레이 MinPlays 번 이상에서 캐시 없는 읽기만 있고 (캐시 읽기·페이징 읽기 0), 어느 플레이에서도 캐시를 거친 읽기가 없음.
    /// stat 은 지금 파일의 크기·수정 시각 (없거나 쓸 수 없으면 null → 판정 안 함). 파일이 근거 플레이의 관찰 시작보다 나중에 쓰였으면 (업데이트 등) 기록과 다른 파일이므로 판정하지 않습니다.
    /// 읽기 방식은 게임 프로세스 트리의 읽기만 담으므로 서비스 자신의 읽기는 처음부터 들어 있지 않습니다.
    public static UnbufferedFileList Collect(IEnumerable<IoSummary> plays, Func<string, LoadRefillFileStat?> stat)
    {
        var noCachePlays = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var latestObserved = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var cached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int counted = 0;
        foreach (IoSummary play in plays)
        {
            if (!play.HasReadModes || (play.SessionId.Length > 0 && !sessions.Add(play.SessionId)))
            {
                continue;
            }

            counted++;
            foreach (IoSummaryReadMode mode in play.ReadModes ?? [])
            {
                if (mode.CacheRequests > 0 || mode.PagingRequests > 0)
                {
                    cached.Add(mode.Path);
                }
                else if (mode.NoCacheRequests > 0)
                {
                    noCachePlays[mode.Path] = noCachePlays.GetValueOrDefault(mode.Path) + 1;
                    latestObserved[mode.Path] = Math.Max(latestObserved.GetValueOrDefault(mode.Path), play.ObservedAtUtc.UtcTicks);
                }
            }
        }

        var files = new Dictionary<string, LoadRefillFileStat>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, int count) in noCachePlays)
        {
            if (count >= MinPlays && !cached.Contains(path)
                && stat(path) is { Size: > 0 } now && now.MTimeTicks <= latestObserved[path])
            {
                files[path] = now;
            }
        }

        return new UnbufferedFileList(files, counted);
    }
}

/// 캐시 없이만 읽힌 파일 목록 파일 (계획 폴더, 원시 트레이스 옆). 임시 파일에 쓴 뒤 바꿔 끼웁니다.
public static class UnbufferedFileStore
{
    public const int Version = 1;
    public const string Suffix = ".unbuffered.json";

    public static string PathFor(string rawTracePath) => rawTracePath + Suffix;

    public static void Write(string path, UnbufferedFileList list, DateTimeOffset builtAtUtc)
    {
        var root = new JObject
        {
            { "version", (long)Version },
            { "built_at_utc", builtAtUtc.ToString("O", CultureInfo.InvariantCulture) },
            { "plays", (long)list.Plays },
            { "files", list.Files.OrderBy(file => file.Key, StringComparer.OrdinalIgnoreCase).Select(file => (object?)new JObject
                {
                    { "path", file.Key },
                    { "size", file.Value.Size },
                    { "mtime_ticks", file.Value.MTimeTicks },
                }).ToList() },
        };

        string temp = path + ".tmp";
        string text = PyJson.Dumps(root, ensureAscii: false) + "\n";
        DurableFile.WriteAllBytes(temp, PyJson.Utf8.GetBytes(PyJson.ToCrlf(text)));
        File.Move(temp, path, true);
    }

    /// 없거나 읽을 수 없거나 형식이 다르면 null (목록은 부가 정보라 없으면 지금 동작 그대로).
    public static UnbufferedFileList? Read(string path)
    {
        try
        {
            if (!File.Exists(path)
                || JsonRead.ReadFile(path) is not JObject root
                || !Long(root, "version", out long version) || version != Version
                || !Long(root, "plays", out long plays)
                || !root.TryGetValue("files", out object? filesValue) || filesValue is not List<object?> rows)
            {
                return null;
            }

            var files = new Dictionary<string, LoadRefillFileStat>(StringComparer.OrdinalIgnoreCase);
            foreach (object? row in rows)
            {
                if (row is not JObject file || !file.TryGetValue("path", out object? name) || name is not string text || text.Length == 0
                    || !Long(file, "size", out long size) || size < 0 || !Long(file, "mtime_ticks", out long ticks))
                {
                    return null;
                }

                files[text] = new LoadRefillFileStat(size, ticks);
            }

            return new UnbufferedFileList(files, (int)Math.Clamp(plays, 0, int.MaxValue));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or OverflowException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    private static bool Long(JObject o, string key, out long value)
    {
        value = 0;
        if (o.TryGetValue(key, out object? raw) && raw is long number)
        {
            value = number;
            return true;
        }

        return false;
    }
}
