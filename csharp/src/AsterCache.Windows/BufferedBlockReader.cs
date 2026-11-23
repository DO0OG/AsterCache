using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// 파일 블록을 버퍼드 읽기로만 읽고 걸린 시간을 돌려주는 읽기 도구 (스파스 파일 블록 유지가 씀). 시험은 가짜로 바꿉니다.
public interface IBlockReader : IDisposable
{
    /// path 의 offset 부터 length 바이트 (파일 끝이면 그보다 적게) 를 읽고 걸린 시간 (초) 을 돌려줍니다. 열 수 없거나 읽기가 실패하면 null (그 파일은 이 도구가 닫히기 전까지 다시 열지 않음).
    double? Read(string path, long offset, int length);
}

/// 스파스 속성 파일은 섹션 (매핑) 을 만들면 그 파일의 캐시가 파일 전체에서 버려지므로 (2026-11-14 실험), 이 도구는 매핑·뷰·PrefetchVirtualMemory 를 부르지 않고 일반 버퍼드 ReadFile 만 씁니다.
/// FILE_FLAG_NO_BUFFERING 도 쓰지 않습니다: 캐시에 있으면 메모리 복사 (64 KiB 에 수 µs~수십 µs), 없으면 디스크 읽기라 걸린 시간으로 디스크로 갔는지 알 수 있습니다.
/// 캐시 관리자의 미리 읽기는 끕니다 (FileOptions.RandomAccess): 켜 두면 이어진 콜드 읽기를 미리 읽기가 앞서 가져가 블록마다는 메모리처럼 빨라 보이므로 (2026-11-23 하드디스크 시험: 이어진 64 KiB 읽기의 75% 가 0.15 ms 이내), 읽은 양이 요청한 양에 가깝고 시간으로 가르기 쉽습니다.
/// 파일은 읽기 전용으로, 읽기·쓰기·삭제 공유를 모두 허용해 열어 (게임이 쓰거나 지우는 것을 막지 않음) 이 도구가 닫힐 때까지만 들고 있습니다. 읽은 내용은 재사용하는 버퍼로 받아 버립니다.
public sealed class BufferedBlockReader : IBlockReader
{
    /// 한 번에 읽을 수 있는 최대 길이: 1 MiB (재사용 버퍼의 크기).
    public const int MaxReadBytes = 1 << 20;

    private readonly byte[] _buffer = new byte[MaxReadBytes];
    private readonly Dictionary<string, SafeFileHandle?> _handles = new(StringComparer.OrdinalIgnoreCase); // null: 열 수 없거나 읽기가 실패한 파일

    public double? Read(string path, long offset, int length)
    {
        if (length <= 0 || length > MaxReadBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(length));
        }

        if (!_handles.TryGetValue(path, out SafeFileHandle? handle))
        {
            _handles[path] = handle = Open(path);
        }

        if (handle is null)
        {
            return null;
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            _ = RandomAccess.Read(handle, _buffer.AsSpan(0, length), offset);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            handle.Dispose();
            _handles[path] = null;
            return null;
        }

        return System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalSeconds;
    }

    private static SafeFileHandle? Open(string path)
    {
        try
        {
            return File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.RandomAccess);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null; // 게임이 배타적으로 열었거나 파일이 없어짐
        }
    }

    public void Dispose()
    {
        foreach (SafeFileHandle? handle in _handles.Values)
        {
            handle?.Dispose();
        }

        _handles.Clear();
    }
}
