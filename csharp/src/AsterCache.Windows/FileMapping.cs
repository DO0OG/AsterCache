using System.ComponentModel;
using System.Runtime.InteropServices;
using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// 파일 전체를 읽기 전용으로 매핑한 뷰. 뷰가 섹션을 붙잡으므로 핸들은 매핑 직후 닫습니다 (Python reader._map).
public sealed class FileMapping : IDisposable
{
    private nint _base;

    private FileMapping(nint baseAddress)
    {
        _base = baseAddress;
    }

    public nint BaseAddress => _base;

    /// 크기 0 파일은 매핑할 수 없어 Win32Exception 입니다.
    public static FileMapping Open(string path)
    {
        using SafeFileHandle file = NativeMethods.CreateFileW(path, Win32.GenericRead, Win32.ShareAll, 0, Win32.OpenExisting, 0, 0);
        if (file.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        using SafeFileHandle mapping = NativeMethods.CreateFileMappingW(file, 0, Win32.PageReadonly, 0, 0, null);
        if (mapping.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        nint view = NativeMethods.MapViewOfFile(mapping, Win32.FileMapRead, 0, 0, 0);
        if (view == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return new FileMapping(view);
    }

    public void Dispose()
    {
        if (_base != 0)
        {
            NativeMethods.UnmapViewOfFile(_base);
            _base = 0;
        }
    }
}

/// 매핑된 범위를 파일 캐시로 미리 가져오기 (Windows 8 이상, 관리자 불필요).
public static class MemoryPrefetch
{
    /// PrefetchVirtualMemory 를 현재 프로세스에 대해 한 번 부릅니다 (호출 순서가 곧 요청 순서). 실패하면 false.
    public static unsafe bool Prefetch(ReadOnlySpan<Win32MemoryRangeEntry> ranges)
    {
        if (ranges.IsEmpty)
        {
            return true;
        }

        fixed (Win32MemoryRangeEntry* p = ranges)
        {
            return NativeMethods.PrefetchVirtualMemory(NativeMethods.GetCurrentProcess(), (nuint)ranges.Length, p, 0);
        }
    }
}
