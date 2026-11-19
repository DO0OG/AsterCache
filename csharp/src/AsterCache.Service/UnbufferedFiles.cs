using System.Globalization;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;

namespace AsterCache.Service;

/// 캐시를 거치지 않고만 읽히는 파일 목록의 저장·적용: 저장된 플레이 집계에서 판정해 파일로 남기고 (순수 판정은 UnbufferedFileJudge),
/// 예열·시작 스트림·단계 스트림·고정이 쓰는 읽기 목록에서 그 파일의 구간을 뺍니다. 재배치 계획은 이 목록을 보지 않습니다 (배치로만 빨라지는 파일).
internal static class UnbufferedFiles
{
    public static string PathOf(GameSettings g) => UnbufferedFileStore.PathFor(AutoTracer.RawTracePath(g));

    /// 목록 파일이 없거나 이 게임의 가장 새 집계보다 오래됐는가 (서비스 시작 때 다시 만들지 정함, 집계가 없으면 false).
    public static bool IsStale(GameSettings g, Func<string, DateTimeOffset?> writeTime) => LoadRefillPlans.IsStale(g, writeTime, PathOf(g));

    /// 목록을 다시 만들어 저장합니다 (판정된 파일이 없어도 빈 목록을 남겨 다시 만들었음을 표시). 로그 한 줄을 돌려줍니다.
    public static string Rebuild(GameSettings g, DateTimeOffset now)
    {
        string path = PathOf(g);
        UnbufferedFileList list = UnbufferedFileJudge.Collect(LoadRefillPlans.Sources(AutoTracer.RawTracePath(g)).Select(source => source.Summary), LoadRefillPlans.StatOf(g));
        UnbufferedFileStore.Write(path, list, now);

        return $"unbuffered file list built from {list.Plays} plays: {list.Files.Count} files/{(list.Bytes / 1_048_576d).ToString("F1", CultureInfo.InvariantCulture)} MiB read only without the cache";
    }

    /// 저장된 목록 가운데 지금 파일이 판정 때와 같은 파일의 경로 (목록이 없으면 빈 집합 → 지금 동작 그대로).
    public static HashSet<string> Excluded(GameSettings g)
    {
        if (UnbufferedFileStore.Read(PathOf(g)) is not { Files.Count: > 0 } list)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return list.StillValid(path =>
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new LoadRefillFileStat(info.Length, info.LastWriteTimeUtc.Ticks) : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        });
    }

    /// 읽기 목록에서 캐시 없이만 읽히는 파일의 구간을 뺍니다.
    public static List<ReadOp> Without(GameSettings g, List<ReadOp> ops)
    {
        HashSet<string> excluded = Excluded(g);
        return excluded.Count == 0 ? ops : [.. ops.Where(op => !excluded.Contains(op.Path))];
    }
}
