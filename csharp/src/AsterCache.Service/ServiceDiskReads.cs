using System.Collections.Concurrent;

namespace AsterCache.Service;

/// 서비스가 게임이 도는 동안 게임 드라이브에서 직접 읽은 바이트의 누계 (드라이브별): 유지 회차·붙든 범위의 점검·붙들기 시작의 결과에 이미 있는 "디스크에서 읽은 바이트" 를 더합니다.
/// 로딩 판정이 드라이브 읽기에서 이 앞뒤 차이를 빼 서비스 자신의 읽기가 게임의 로딩으로 보이지 않게 합니다. 서비스의 읽기는 매핑 뷰의 페이지 폴트라 프로세스의 읽기 전송 바이트 (ReadFile 류) 에는 들지 않아 그 값으로는 뺄 수 없습니다.
/// 한 번의 만지기·점검이 끝날 때 한꺼번에 더하므로 그 읽기는 끝난 초 한 곳에서 빠집니다 (그 초의 드라이브 읽기보다 크면 0 으로 자름).
internal sealed class ServiceDiskReads
{
    private readonly ConcurrentDictionary<string, long> _bytes = new(StringComparer.OrdinalIgnoreCase);

    /// root (게임 설치 루트나 그 안의 경로) 가 있는 드라이브에서 읽은 바이트를 더합니다.
    public void Add(string root, long bytes)
    {
        if (bytes > 0)
        {
            _ = _bytes.AddOrUpdate(DriveOf(root), bytes, (_, old) => old + bytes);
        }
    }

    /// root 의 드라이브에서 서비스가 읽은 누계.
    public long Total(string root) => _bytes.TryGetValue(DriveOf(root), out long total) ? total : 0;

    private static string DriveOf(string path) => Path.GetPathRoot(path) ?? path;
}
