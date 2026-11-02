using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Cli;

/// Python Path.resolve / os.path.realpath: 링크·정션을 풀고 디스크에 저장된 대소문자로 돌려줍니다 (GetFinalPathNameByHandle).
internal static partial class RealPaths
{
    private const uint FileReadAttributes = 0x80;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint BackupSemantics = 0x02000000; // 디렉터리도 열기
    private const int BufferChars = 32768;

    /// 없거나 열 수 없으면 null (resolve(strict=True) 가 실패하는 경우).
    public static string? Resolve(string path)
    {
        path = DriveRoot(path);
        try
        {
            using SafeFileHandle handle = CreateFileW(Path.GetFullPath(path), FileReadAttributes, ShareAll, 0, OpenExisting, BackupSemantics, 0);
            if (handle.IsInvalid)
            {
                return null;
            }

            char[] buffer = new char[BufferChars];
            uint n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (n == 0 || n >= buffer.Length)
            {
                return null;
            }

            string s = new(buffer, 0, (int)n);
            return s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + s[8..] : s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// os.path.realpath (엄격하지 않음): 없는 경로는 절대 경로로만 바꿉니다.
    public static string Real(string path)
    {
        path = DriveRoot(path);
        string? resolved = Resolve(path);
        if (resolved is not null)
        {
            return resolved;
        }

        string full = Path.GetFullPath(path);
        return full.Length > 3 ? full.TrimEnd('\\') : full;
    }

    /// 구분자 없는 "D:" 는 GetFullPath 가 D: 의 현재 디렉터리로 풀어 버리므로 루트 "D:\" 로 바꿉니다.
    private static string DriveRoot(string path) => path.Length == 2 && path[1] == ':' ? path + '\\' : path;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] filePath, uint filePathSize, uint flags);
}
