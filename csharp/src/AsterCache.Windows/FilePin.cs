using System.ComponentModel;

namespace AsterCache.Windows;

/// 파일을 읽기 전용으로 매핑만 해 두는 묶음 (페이지는 건드리지 않음). 다른 프로세스의 읽기·쓰기·삭제 요청은 막지 않고, Dispose 하면 모두 풉니다.
public sealed class FilePin : IDisposable
{
    private readonly List<FileMapping> _maps = [];

    private FilePin()
    {
    }

    /// 매핑을 들고 있는 파일 수.
    public int Count => _maps.Count;

    /// 열거나 매핑하지 못해 건너뛴 파일 수 (배타적으로 열린 파일, 크기 0, 권한 등) 와 스파스 속성이라 매핑하지 않은 파일 수.
    public int Skipped { get; private set; }

    /// attributes: 파일 속성 조회 (스파스 판정, 시험은 가짜로 바꿈). 스파스 파일은 섹션을 만드는 순간 그 파일의 캐시가 버려지므로 매핑하지 않습니다.
    public static FilePin Open(IEnumerable<string> paths, Func<string, uint>? attributes = null)
    {
        var pin = new FilePin();
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (FileGuards.IsSparse(path, attributes))
            {
                pin.Skipped++;
                continue;
            }

            try
            {
                pin._maps.Add(FileMapping.Open(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
            {
                pin.Skipped++;
            }
        }

        return pin;
    }

    public void Dispose()
    {
        foreach (FileMapping m in _maps)
        {
            m.Dispose();
        }

        _maps.Clear();
    }
}
