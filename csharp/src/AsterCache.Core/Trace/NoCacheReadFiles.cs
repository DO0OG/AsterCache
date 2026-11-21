using System.Globalization;
using AsterCache.Core.Json;

namespace AsterCache.Core.Trace;

/// 게임이 주로 캐시를 거치지 않고 읽은 파일 (플레이 집계의 읽기 방식을 기록한 플레이 전체에서 캐시 없는 읽기 요청 수가 캐시 경유·페이징 읽기 요청 수보다 많은 파일) 과 그 판정에 쓴 플레이 수.
/// 붙들기가 이 파일을 뺍니다: 파일에 매핑 뷰가 하나라도 있으면 그 파일의 캐시 없는 읽기 전체가 느려지고 (밤샘 재생 측정에서 붙든 파일 2.08배, 붙들지 않은 파일 0.96배), 붙들어도 캐시 없는 읽기는 얻는 것이 없습니다.
/// "한 건이라도" 가 아니라 "더 많은 파일" 로 고른 근거: 이 PC 의 Call of Duty 기록 (읽기 방식을 기록한 4 플레이) 에서 한 건이라도 있던 파일은 909 개로 순위 목록 7.62 GiB 의 7.1 GiB (93%) 를 덮어 붙들기를 거의 없애지만,
/// 더 많은 파일은 146 개·308 MiB (4%, 앞 5 GiB 안 123 MiB) 로 측정에서 손해를 낸 파일 (8 개, 붙든 목록의 2.1%) 과 같은 규모입니다.
public sealed record NoCacheReadFileList(IReadOnlySet<string> Files, int Plays);

/// 저장된 플레이 집계들의 파일별 읽기 방식에서 "주로 캐시 없이 읽은 파일" 을 고르는 순수 함수입니다.
/// 읽기 방식을 기록하지 않은 집계 (옛 형식) 는 모름이라 판정에 쓰지 않습니다: 그런 집계뿐인 게임은 목록이 비어 지금처럼 붙듭니다 (모르는 것을 빼면 붙들기를 이유 없이 줄이므로).
public static class NoCacheReadJudge
{
    public static NoCacheReadFileList Collect(IEnumerable<IoSummary> plays)
    {
        var noCache = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var cached = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
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
                // 읽기 방식 집계는 페이징 요청을 먼저 가르므로 NoCacheRequests 는 캐시 없음이면서 페이징이 아닌 요청입니다 (페이징 읽기는 캐시 없는 읽기로 세지 않음).
                noCache[mode.Path] = noCache.GetValueOrDefault(mode.Path) + mode.NoCacheRequests;
                cached[mode.Path] = cached.GetValueOrDefault(mode.Path) + mode.CacheRequests + mode.PagingRequests;
            }
        }

        var files = new HashSet<string>(noCache.Where(pair => pair.Value > cached.GetValueOrDefault(pair.Key)).Select(pair => pair.Key), StringComparer.OrdinalIgnoreCase);
        return new NoCacheReadFileList(files, counted);
    }
}

/// 주로 캐시 없이 읽은 파일 목록 파일 (계획 폴더, 원시 트레이스 옆). 임시 파일에 쓴 뒤 바꿔 끼웁니다.
public static class NoCacheReadFileStore
{
    public const int Version = 1;
    public const string Suffix = ".nocache-read.json";

    public static string PathFor(string rawTracePath) => rawTracePath + Suffix;

    public static void Write(string path, NoCacheReadFileList list, DateTimeOffset builtAtUtc)
    {
        var root = new JObject
        {
            { "version", (long)Version },
            { "built_at_utc", builtAtUtc.ToString("O", CultureInfo.InvariantCulture) },
            { "plays", (long)list.Plays },
            { "files", list.Files.Order(StringComparer.OrdinalIgnoreCase).Select(file => (object?)file).ToList() },
        };

        string temp = path + ".tmp";
        string text = PyJson.Dumps(root, ensureAscii: false) + "\n";
        DurableFile.WriteAllBytes(temp, PyJson.Utf8.GetBytes(PyJson.ToCrlf(text)));
        File.Move(temp, path, true);
    }

    /// 없거나 읽을 수 없거나 형식이 다르면 null (목록은 부가 정보라 없으면 지금 동작 그대로).
    public static NoCacheReadFileList? Read(string path)
    {
        try
        {
            if (!File.Exists(path)
                || JsonRead.ReadFile(path) is not JObject root
                || !root.TryGetValue("version", out object? version) || version is not long v || v != Version
                || !root.TryGetValue("plays", out object? playsValue) || playsValue is not long plays
                || !root.TryGetValue("files", out object? filesValue) || filesValue is not List<object?> rows)
            {
                return null;
            }

            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object? row in rows)
            {
                if (row is not string name || name.Length == 0)
                {
                    return null;
                }

                _ = files.Add(name);
            }

            return new NoCacheReadFileList(files, (int)Math.Clamp(plays, 0, int.MaxValue));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or OverflowException or FormatException or ArgumentException)
        {
            return null;
        }
    }
}
