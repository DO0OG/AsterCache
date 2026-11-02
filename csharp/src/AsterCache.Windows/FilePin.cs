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

    /// 열거나 매핑하지 못해 건너뛴 파일 수 (배타적으로 열린 파일, 크기 0, 권한 등).
    public int Skipped { get; private set; }

    public static FilePin Open(IEnumerable<string> paths)
    {
        var pin = new FilePin();
        foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
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
