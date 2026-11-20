using AsterCache.Core.Json;

namespace AsterCache.Core.Trace;

public static class TraceLoader
{
    /// Python prefetch.cli.load_ranges. 계획 ({"ranges": [...]}) 이나 추적 목록 ([...]) 을 읽고,
    /// 루트 밖이거나 없는 파일의 행은 버리고, 파일 크기로 자릅니다.
    /// resolve: 경로를 실제 경로로 풀어 줍니다 (링크·정션 해석, 없으면 null). 기본은 Path.GetFullPath + 존재 확인.
    /// fileSize: 풀린 경로가 파일이면 크기, 아니면 null. 기본은 FileInfo.
    public static List<TraceEvent> LoadRanges(string tracePath, string originRoot, Func<string, string?>? resolve = null,
        Func<string, long?>? fileSize = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        resolve ??= DefaultResolve;
        fileSize ??= DefaultFileSize;
        TraceFileRows file = TraceFile.Read(tracePath);
        cancellationToken.ThrowIfCancellationRequested();
        List<TraceEvent> rows = file.Rows ?? throw new PyValueException("bad trace file");
        cancellationToken.ThrowIfCancellationRequested();
        string root = resolve(originRoot) ?? throw new DirectoryNotFoundException(originRoot);
        cancellationToken.ThrowIfCancellationRequested();
        var sizes = new Dictionary<string, long?>(StringComparer.Ordinal);
        int kept = 0; // 읽은 목록 안에서 그대로 추려 두 번째 목록을 만들지 않음
        for (int i = 0; i < rows.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TraceEvent e = rows[i];
            if (e.Offset < 0 || e.Length <= 0)
            {
                throw new PyValueException("bad row");
            }

            if (!sizes.TryGetValue(e.Path, out long? size))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? rp = resolve(e.Path);
                cancellationToken.ThrowIfCancellationRequested();
                size = rp is not null && IsRelativeTo(rp, root) ? fileSize(rp) : null;
                cancellationToken.ThrowIfCancellationRequested();
                sizes[e.Path] = size;
            }

            if (size is null || e.Offset >= size)
            {
                continue;
            }

            rows[kept++] = e with { Length = Math.Min(e.Length, size.Value - e.Offset) };
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (file.RowError is not null)
        {
            throw file.RowError; // 읽을 수 없던 행은 앞의 행을 다 처리한 뒤에 오류가 됨 (옛 방식과 같은 순서)
        }

        rows.RemoveRange(kept, rows.Count - kept);
        rows.TrimExcess();
        return rows;
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
