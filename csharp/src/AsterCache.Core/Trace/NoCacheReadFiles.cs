using System.Globalization;
using AsterCache.Core.Json;

namespace AsterCache.Core.Trace;

/// 붙들면 손해가 이득보다 큰 파일 (게임이 캐시 없이 읽는 몫이 커서 매핑 뷰가 그 읽기를 느리게 하는 손해가 붙들어서 줄이는 캐시 경유 디스크 지연보다 큰 파일) 과 그 판정에 쓴 플레이 수.
/// 붙들기가 이 파일을 뺍니다: 파일에 매핑 뷰가 하나라도 있으면 그 파일의 캐시 없는 읽기 전체가 느려지고 (밤샘 재생 측정에서 붙든 파일 2.08배, 붙들지 않은 파일 0.96배), 붙든 쪽의 캐시 없는 읽기는 얻는 것이 없습니다.
/// "한 건이라도" 로 고르면 이 PC 의 Call of Duty 기록 (읽기 방식을 기록한 4 플레이) 에서 순위 목록 7.62 GiB 의 93% 를 덮어 붙들기를 없애고, "캐시 없는 요청이 더 많은 파일" (다수결) 은 두 방식이 섞인 파일의 손해를 남깁니다
/// (손해 1,415 → 1,181 로 17% 만 줄고 남는 손해의 53% 는 캐시 경유 읽기가 더 많은 두 파일). 그래서 파일마다 손해 추정과 이득 추정을 견줍니다 (NoCacheReadJudge).
public sealed record NoCacheReadFileList(IReadOnlySet<string> Files, int Plays);

/// 저장된 플레이 집계들의 파일별 읽기 방식과 행의 디스크 지연에서 "붙들면 손해가 큰 파일" 을 고르는 순수 함수입니다.
/// 읽기 방식을 기록하지 않은 집계 (옛 형식) 는 모름이라 판정에 쓰지 않습니다: 그런 집계뿐인 게임은 목록이 비어 지금처럼 붙듭니다 (모르는 것을 빼면 붙들기를 이유 없이 줄이므로).
public static class NoCacheReadJudge
{
    /// 파일에 뷰가 있을 때 그 파일의 캐시 없는 읽기가 느려지는 배수: 2.08. 근거: 밤샘 재생 측정에서 다른 디스크 활동이 없던 회의 붙든 파일 값 (붙드는 동안 7~18배, 뷰가 남은 동안 1.5~4배는 더 큼).
    /// 캐시 경유와 섞인 파일의 배수는 측정하지 못해 같은 값을 씁니다.
    public const double SlowdownFactor = 2.08;

    /// 붙든 양 가운데 실제로 남아 이득이 되는 몫의 보수적 추정: 0.4. 근거: 같은 측정에서 붙들기가 줄인 캐시 경유 지연은 덮던 지연의 0.28~0.53 (회 4 와 회 1).
    public const double RealizedGainShare = 0.4;

    private sealed class FileTotals
    {
        public long NoCacheRequests;
        public long CachedRequests;
        public double NoCacheLatencyUs; // 캐시 없는 읽기의 디스크 지연 추정 (L_nc)
        public double CachedLatencyUs; // 순위 블록이 덮는 캐시 경유 디스크 지연 추정 (G_c)
        public bool HasLatency;
    }

    /// 파일 f 를 붙들기에서 뺄 조건: L_nc(f) × (SlowdownFactor − 1) > RealizedGainShare × G_c(f).
    /// 읽기 방식별 지연은 집계에 없어, 파일의 행 (플레이마다) 의 디스크 지연 합에 캐시 없는 바이트 몫 s = min(1, 캐시 없는 읽기 바이트 / 행의 바이트 합) 를 곱해 L_nc = 지연 × s 로 근사합니다 (캐시 없는 읽기는 모두 디스크로 감).
    /// G_c 는 순위 블록과 같은 기준 (캐시를 거쳐 읽힌 요청이 있는 블록, 관찰 중 바뀐 파일 제외; 덧붙이기만 하는 파일의 예외는 넣지 않아 그런 파일은 G_c 가 작게 잡힘) 의 행의 지연 합에 (1 − s) 를 곱한 값입니다. 행에 지연이 없는 파일 (지연 기록 없음) 은 캐시 없는 읽기 요청 수가
    /// 캐시 경유·페이징 요청 수보다 많은지로 판정합니다 (다수결, 값이 없어 식을 못 쓰므로).
    public static NoCacheReadFileList Collect(IEnumerable<IoSummary> plays)
    {
        var totals = new Dictionary<string, FileTotals>(StringComparer.OrdinalIgnoreCase);
        var sessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int counted = 0;
        foreach (IoSummary play in plays)
        {
            if (!play.HasReadModes || (play.SessionId.Length > 0 && !sessions.Add(play.SessionId)))
            {
                continue;
            }

            counted++;
            var rowBytes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var rowLatency = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var pagedLatency = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (IoSummaryRow row in play.Rows)
            {
                if (row.FileNumber < 0 || row.FileNumber >= play.Files.Count)
                {
                    continue;
                }

                IoSummaryFile file = play.Files[row.FileNumber];
                rowBytes[file.Path] = rowBytes.GetValueOrDefault(file.Path) + row.Bytes;
                rowLatency[file.Path] = rowLatency.GetValueOrDefault(file.Path) + row.LatencySumUs;
                if (row.PagingRequests > 0 && !file.ChangedDuringSession)
                {
                    pagedLatency[file.Path] = pagedLatency.GetValueOrDefault(file.Path) + row.LatencySumUs;
                }
            }

            foreach (IoSummaryReadMode mode in play.ReadModes ?? [])
            {
                if (!totals.TryGetValue(mode.Path, out FileTotals? t))
                {
                    totals[mode.Path] = t = new FileTotals();
                }

                // 읽기 방식 집계는 페이징 요청을 먼저 가르므로 NoCacheRequests 는 캐시 없음이면서 페이징이 아닌 요청입니다 (페이징 읽기는 캐시 없는 읽기로 세지 않음).
                t.NoCacheRequests += mode.NoCacheRequests;
                t.CachedRequests += mode.CacheRequests + mode.PagingRequests;
                if (rowBytes.GetValueOrDefault(mode.Path) > 0 && rowLatency.GetValueOrDefault(mode.Path) > 0)
                {
                    double share = Math.Min(1.0, mode.NoCacheBytes / (double)rowBytes[mode.Path]);
                    t.NoCacheLatencyUs += rowLatency[mode.Path] * share;
                    t.CachedLatencyUs += pagedLatency.GetValueOrDefault(mode.Path) * (1 - share);
                    t.HasLatency = true;
                }
            }
        }

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, FileTotals t) in totals)
        {
            bool loses = t.HasLatency
                ? t.NoCacheLatencyUs * (SlowdownFactor - 1) > RealizedGainShare * t.CachedLatencyUs
                : t.NoCacheRequests > t.CachedRequests;
            if (loses)
            {
                files.Add(path);
            }
        }

        return new NoCacheReadFileList(files, counted);
    }
}

/// 붙들면 손해가 큰 파일 목록 파일 (계획 폴더, 원시 트레이스 옆). 임시 파일에 쓴 뒤 바꿔 끼웁니다.
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
