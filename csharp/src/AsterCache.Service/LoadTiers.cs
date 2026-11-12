using System.Collections.Concurrent;
using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;

namespace AsterCache.Service;

/// 게임의 로딩 경계 (원시 트레이스 plans\<이름>.trace.json 의 t_ms 로 계산). 트레이스가 바뀔 때만 (쓴 시각·크기) 다시 계산해 메모리에 둡니다:
/// 서비스가 쓰고 읽는 파일이라 따로 저장하지 않고, 읽을 때 TraceLoader 가 형식을 검증합니다. t_ms 가 없는 옛 트레이스·파일 없음·읽기 실패는 null.
public static class LoadTiers
{
    private static readonly ConcurrentDictionary<string, (DateTime Written, long Length, LoadBoundary? Boundary)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static LoadBoundary? For(GameSettings game) => For(game, default);

    // allowParse 가 거짓이면 이미 캐시에 있는 경계만 돌려줍니다 (없으면 null, 원시 트레이스를 파싱하지 않음).
    internal static LoadBoundary? For(GameSettings game, CancellationToken cancellationToken, bool allowParse = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string raw = AutoTracer.PlanTracePath(game, path =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return File.Exists(path);
        }) ?? AutoTracer.RawTracePath(game); // 원시가 없으면 가져온 boot 트레이스
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(raw);
            cancellationToken.ThrowIfCancellationRequested();
            if (!info.Exists)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            DateTime written = info.LastWriteTimeUtc;
            cancellationToken.ThrowIfCancellationRequested();
            long length = info.Length;
            if (Cache.TryGetValue(raw, out var hit) && hit.Written == written && hit.Length == length)
            {
                return hit.Boundary;
            }

            if (!allowParse)
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            LoadBoundary? b = LoadBoundaries.Find(TraceLoader.LoadRanges(raw, game.OriginRoot, RealPaths.Resolve, p =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(p))
                {
                    return null;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new FileInfo(p).Length;
            }, cancellationToken));
            Cache[raw] = (written, length, b);
            return b;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
    }
}
