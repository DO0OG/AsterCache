using AsterCache.Core.Json;

namespace AsterCache.Core.Trace;

public static class TraceLoader
{
    /// Python prefetch.cli.load_ranges. 계획 ({"ranges": [...]}) 이나 추적 목록 ([...]) 을 읽고,
    /// 루트 밖이거나 없는 파일의 행은 버리고, 파일 크기로 자릅니다.
    /// resolve: 경로를 실제 경로로 풀어 줍니다 (링크·정션 해석, 없으면 null). 기본은 Path.GetFullPath + 존재 확인.
    /// fileSize: 풀린 경로가 파일이면 크기, 아니면 null. 기본은 FileInfo.
    public static List<TraceEvent> LoadRanges(string tracePath, string originRoot, Func<string, string?>? resolve = null,
        Func<string, long?>? fileSize = null)
    {
        resolve ??= DefaultResolve;
        fileSize ??= DefaultFileSize;
        object? data = JsonRead.ReadFile(tracePath);
        object? rows = data is JObject obj ? (obj.TryGetValue("ranges", out object? r) ? r : null) : data;
        if (rows is not List<object?> list)
        {
            throw new PyValueException("bad trace file");
        }

        string root = resolve(originRoot) ?? throw new DirectoryNotFoundException(originRoot);
        var sizes = new Dictionary<string, long?>(StringComparer.Ordinal);
        var result = new List<TraceEvent>(list.Count);
        foreach (object? row in list)
        {
            TraceEvent e = TraceEvents.FromJson(row);
            if (e.Offset < 0 || e.Length <= 0)
            {
                throw new PyValueException("bad row");
            }

            if (!sizes.TryGetValue(e.Path, out long? size))
            {
                string? rp = resolve(e.Path);
                size = rp is not null && IsRelativeTo(rp, root) ? fileSize(rp) : null;
                sizes[e.Path] = size;
            }

            if (size is null || e.Offset >= size)
            {
                continue;
            }

            result.Add(e with { Length = Math.Min(e.Length, size.Value - e.Offset) });
        }

        return result;
    }

    /// pathlib.PureWindowsPath.is_relative_to: 대소문자를 무시하고 구성 요소 단위로 접두를 비교합니다.
    public static bool IsRelativeTo(string path, string root)
    {
        string p = Normalize(path);
        string r = Normalize(root);
        return p == r || p.StartsWith(r + "\\", StringComparison.Ordinal);
    }

    private static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    private static string? DefaultResolve(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return File.Exists(full) || Directory.Exists(full) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    private static long? DefaultFileSize(string path) => File.Exists(path) ? new FileInfo(path).Length : null;
}
