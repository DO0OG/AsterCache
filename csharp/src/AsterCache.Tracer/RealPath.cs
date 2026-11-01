using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Tracer;

internal static partial class RealPath
{
    private const int BufferChars = 32768;

    /// GetFinalPathNameByHandle 로 푼 실제 경로 (디스크에 저장된 대소문자, 링크 해석). 열 수 없으면 null.
    public static string? Resolve(string path)
    {
        try
        {
            using SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            char[] buffer = new char[BufferChars];
            uint n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (n == 0 || n >= buffer.Length)
            {
                return null;
            }

            string s = new(buffer, 0, (int)n);
            if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            {
                return @"\\" + s[8..];
            }

            return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] filePath, uint filePathSize, uint flags);
}
