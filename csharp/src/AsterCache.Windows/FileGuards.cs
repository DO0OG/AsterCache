using System.ComponentModel;
using System.Runtime.InteropServices;
using AsterCache.Windows.Native;

namespace AsterCache.Windows;

/// 옮기면 안 되는 파일 판정. Python mover.file_attrs / skip_reason.
public static class FileGuards
{
    // 디렉터리, 오프라인, 스파스, 재해석 지점, 압축, 암호화: v1 은 옮기지 않습니다. 판정 순서는 Python dict 순서.
    // https://learn.microsoft.com/en-us/windows/win32/fileio/file-attribute-constants
    private static readonly (uint Bit, string Name)[] SkipAttrs =
    {
        (0x10, "directory"), (0x1000, "offline"), (0x200, "sparse"), (0x400, "reparse"), (0x800, "compressed"), (0x4000, "encrypted"),
    };

    public static uint FileAttributes(string path)
    {
        uint a = NativeMethods.GetFileAttributesW(path);
        if (a == Win32.InvalidFileAttributes)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return a;
    }

    /// 옮기면 안 되는 이유 (압축·스파스·암호화 등), 없으면 null. attrFn 으로 속성 조회를 바꿀 수 있습니다 (시험용).
    public static string? SkipReason(string path, Func<string, uint>? attrFn = null)
    {
        uint a;
        try
        {
            a = (attrFn ?? FileAttributes)(path);
        }
        catch (Exception e) when (e is IOException or Win32Exception or UnauthorizedAccessException)
        {
            return "attrs_unreadable: " + e.Message;
        }

        foreach ((uint bit, string name) in SkipAttrs)
        {
            if ((a & bit) != 0)
            {
                return name;
            }
        }

        return null;
    }
}
