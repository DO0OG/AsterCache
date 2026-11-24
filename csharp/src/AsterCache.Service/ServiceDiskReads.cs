using System.Collections.Concurrent;

namespace AsterCache.Service;

/// 서비스가 게임 드라이브에서 읽은 것으로 계상하는 경로 (초 단위 기록의 service_* 열과 플레이 종료 요약이 경로별로 나눠 보이는 데 씀).
internal enum ServiceReadPath
{
    StartStream,
    StageStream,
    Hold,
    Keep,
    Check,
    Sparse,
    Touch,
    Other,
}

/// 한 게임의 경로별 계상값: 디스크에서 읽은 것으로 센 바이트 (Disk), 스트림이 요청한 바이트 (Requested), 받아들여진 바이트 (Accepted).
/// 더하는 쪽은 원자적 더하기만 쓰므로 (잠금 없음) 스트림은 시작 때 한 번 얻은 이 객체에 묶음·조각마다 더합니다. 계상값이며 물리 디스크 읽기의 실측이 아닙니다.
internal sealed class ServiceReadCounts
{
    internal static readonly ServiceReadPath[] Paths = Enum.GetValues<ServiceReadPath>();

    private readonly long[] _disk = new long[Paths.Length];
    private readonly long[] _requested = new long[Paths.Length];
    private readonly long[] _accepted = new long[Paths.Length];

    public void AddDisk(ServiceReadPath path, long bytes)
    {
        if (bytes > 0)
        {
            _ = Interlocked.Add(ref _disk[(int)path], bytes);
        }
    }

    public void AddStream(ServiceReadPath path, long requested, long accepted)
    {
        if (requested > 0)
        {
            _ = Interlocked.Add(ref _requested[(int)path], requested);
        }

        if (accepted > 0)
        {
            _ = Interlocked.Add(ref _accepted[(int)path], accepted);
        }
    }

    public long Disk(ServiceReadPath path) => Volatile.Read(ref _disk[(int)path]);

    public long Requested(ServiceReadPath path) => Volatile.Read(ref _requested[(int)path]);

    public long Accepted(ServiceReadPath path) => Volatile.Read(ref _accepted[(int)path]);
}

/// 서비스가 게임이 도는 동안 게임 드라이브에서 직접 읽은 바이트의 누계 (드라이브별): 유지 회차·붙든 범위의 점검·붙들기 시작의 결과에 이미 있는 "디스크에서 읽은 바이트" 를 더합니다.
/// 로딩 판정이 드라이브 읽기에서 이 앞뒤 차이를 빼 서비스 자신의 읽기가 게임의 로딩으로 보이지 않게 합니다. 서비스의 읽기는 매핑 뷰의 페이지 폴트라 프로세스의 읽기 전송 바이트 (ReadFile 류) 에는 들지 않아 그 값으로는 뺄 수 없습니다.
/// 한 번의 만지기·점검이 끝날 때 한꺼번에 더하므로 그 읽기는 끝난 초 한 곳에서 빠집니다 (그 초의 드라이브 읽기보다 크면 0 으로 자름).
/// 경로별·게임별 값 (CountsOf) 은 기록용 계상값이며 물리 디스크 읽기의 실측이 아닙니다. 스트림의 요청·받아들인 바이트는 로딩 판정이 빼는 누계에 들지 않습니다 (예전 동작 그대로).
internal sealed class ServiceDiskReads
{
    private readonly ConcurrentDictionary<string, long> _bytes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ServiceReadCounts> _games = new(StringComparer.OrdinalIgnoreCase);

    /// root (게임 설치 루트나 그 안의 경로) 가 있는 드라이브에서 읽은 바이트를 더합니다.
    public void Add(string root, long bytes)
    {
        if (bytes > 0)
        {
            _ = _bytes.AddOrUpdate(DriveOf(root), bytes, (_, old) => old + bytes);
        }
    }

    /// Add(root, bytes) 에 더해 같은 읽기를 그 게임의 경로별 계상 (CountsOf) 에도 더합니다. 로딩 판정은 드라이브별 누계 (Total) 만 보고, 기록은 게임별 계상을 봅니다 (같은 볼륨의 두 게임이 서로의 읽기를 섞지 않게).
    public void Add(string root, long bytes, string game, ServiceReadPath path)
    {
        Add(root, bytes);
        CountsOf(game).AddDisk(path, bytes);
    }

    /// 이 게임의 경로별 계상값 (처음 부를 때 만들고 그 뒤로는 같은 객체: 스트림은 시작 때 한 번 얻어 씀).
    public ServiceReadCounts CountsOf(string game) => _games.GetOrAdd(game, static _ => new ServiceReadCounts());

    /// root 의 드라이브에서 서비스가 읽은 누계.
    public long Total(string root) => _bytes.TryGetValue(DriveOf(root), out long total) ? total : 0;

    private static string DriveOf(string path) => Path.GetPathRoot(path) ?? path;
}
