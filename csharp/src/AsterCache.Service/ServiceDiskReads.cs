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

/// 서비스가 게임이 도는 동안 게임 드라이브에서 직접 읽은 바이트의 누계 (드라이브별): 유지 회차·붙든 범위의 점검·붙들기 시작의 결과에 이미 있는 "디스크에서 읽은 바이트" 를 더합니다.
/// 로딩 판정이 드라이브 읽기에서 이 앞뒤 차이를 빼 서비스 자신의 읽기가 게임의 로딩으로 보이지 않게 합니다. 서비스의 읽기는 매핑 뷰의 페이지 폴트라 프로세스의 읽기 전송 바이트 (ReadFile 류) 에는 들지 않아 그 값으로는 뺄 수 없습니다.
/// 한 번의 만지기·점검이 끝날 때 한꺼번에 더하므로 그 읽기는 끝난 초 한 곳에서 빠집니다 (그 초의 드라이브 읽기보다 크면 0 으로 자름).
/// 경로별 값은 계상값이며 물리 디스크 읽기의 실측이 아닙니다. 시작·단계 스트림의 요청·받아들인 바이트 (AddStream) 는 로딩 판정이 빼는 누계 (Total) 에 들지 않습니다 (예전 동작 그대로).
internal sealed class ServiceDiskReads
{
    private static readonly int Paths = Enum.GetValues<ServiceReadPath>().Length;

    private sealed class Drive
    {
        public readonly long[] Disk = new long[Paths];
        public readonly long[] Requested = new long[Paths];
        public readonly long[] Accepted = new long[Paths];
    }

    private readonly ConcurrentDictionary<string, Drive> _drives = new(StringComparer.OrdinalIgnoreCase);

    /// root (게임 설치 루트나 그 안의 경로) 가 있는 드라이브에서 읽은 바이트를 더합니다.
    public void Add(string root, long bytes, ServiceReadPath path = ServiceReadPath.Other)
    {
        if (bytes > 0)
        {
            _ = Interlocked.Add(ref DriveOf(root).Disk[(int)path], bytes);
        }
    }

    /// 스트림 묶음 하나가 요청한 바이트와 받아들여진 바이트를 더합니다 (원자적 더하기만: 읽는 경로에 잠금이 없음).
    public void AddStream(string root, ServiceReadPath path, long requested, long accepted)
    {
        Drive drive = DriveOf(root);
        if (requested > 0)
        {
            _ = Interlocked.Add(ref drive.Requested[(int)path], requested);
        }

        if (accepted > 0)
        {
            _ = Interlocked.Add(ref drive.Accepted[(int)path], accepted);
        }
    }

    /// root 의 드라이브에서 서비스가 읽은 누계 (스트림 제외).
    public long Total(string root)
    {
        long total = 0;
        if (Of(root) is { } drive)
        {
            for (int i = 0; i < Paths; i++)
            {
                total += Volatile.Read(ref drive.Disk[i]);
            }
        }

        return total;
    }

    /// 경로별 누계: 디스크에서 읽은 것으로 계상한 바이트 (Disk), 스트림이 요청한 바이트 (Requested), 받아들여진 바이트 (Accepted).
    public long Disk(string root, ServiceReadPath path) => Of(root)?.Disk is { } a ? Volatile.Read(ref a[(int)path]) : 0;

    public long Requested(string root, ServiceReadPath path) => Of(root)?.Requested is { } a ? Volatile.Read(ref a[(int)path]) : 0;

    public long Accepted(string root, ServiceReadPath path) => Of(root)?.Accepted is { } a ? Volatile.Read(ref a[(int)path]) : 0;

    private Drive? Of(string root) => _drives.TryGetValue(KeyOf(root), out Drive? drive) ? drive : null;

    private Drive DriveOf(string root) => _drives.GetOrAdd(KeyOf(root), static _ => new Drive());

    private static string KeyOf(string path) => Path.GetPathRoot(path) ?? path;
}
