using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows.Native;

/// Win32 상수. 값은 winioctl.h / fileapi.h / winnt.h 기준 (Python prefetch/layout.py, relayout/mover.py 와 같음).
internal static class Win32
{
    // CTL_CODE(FILE_DEVICE_FILE_SYSTEM = 9, function, method, FILE_ANY_ACCESS)
    // https://learn.microsoft.com/en-us/windows/win32/api/winioctl/nf-winioctl-ctl_code
    public const uint FsctlGetRetrievalPointers = 0x00090073; // function 28, METHOD_NEITHER
    public const uint FsctlGetNtfsVolumeData = 0x00090064;    // function 25, METHOD_BUFFERED
    public const uint FsctlGetVolumeBitmap = 0x0009006F;      // function 27, METHOD_NEITHER
    public const uint FsctlMoveFile = 0x00090074;             // function 29, METHOD_BUFFERED

    public const uint GenericRead = 0x80000000;
    public const uint FileReadAttributes = 0x80;
    public const uint ShareRead = 1;
    public const uint ShareAll = 7; // READ | WRITE | DELETE
    public const uint OpenExisting = 3;
    public const uint FileFlagNoBuffering = 0x20000000;
    public const int ErrorMoreData = 234;
    public const uint InvalidFileAttributes = 0xFFFFFFFF;
    public const uint PageReadonly = 2;
    public const uint FileMapRead = 4;
    public const int FileBasicInfoClass = 0; // FILE_INFO_BY_HANDLE_CLASS.FileBasicInfo
    public const int FileIoPriorityHintInfoClass = 12; // FILE_INFO_BY_HANDLE_CLASS.FileIoPriorityHintInfo
    public const int IoPriorityHintLow = 1; // PRIORITY_HINT: VeryLow = 0, Low = 1, Normal = 2
}

internal static unsafe partial class NativeMethods
{
    private const string Kernel32 = "kernel32.dll";

    [LibraryImport(Kernel32, EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeviceIoControl(SafeFileHandle device, uint code, byte* input, uint inputSize, byte* output, uint outputSize, out uint returned, nint overlapped);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, byte* info, uint size);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, byte* info, uint size);

    [LibraryImport(Kernel32, EntryPoint = "GetVolumeInformationW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumeInformationW(string rootPath, char* volumeName, uint volumeNameSize, uint* serial, uint* maxComponent, uint* flags, char* fsName, uint fsNameSize);

    [LibraryImport(Kernel32, EntryPoint = "GetFileAttributesW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial uint GetFileAttributesW(string path);

    [LibraryImport(Kernel32, EntryPoint = "GetVolumePathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetVolumePathNameW(string path, char* volumePath, uint bufferLength);

    [LibraryImport(Kernel32, EntryPoint = "GetDiskFreeSpaceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceW(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    [LibraryImport(Kernel32, EntryPoint = "CreateFileMappingW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial SafeFileHandle CreateFileMappingW(SafeFileHandle file, nint attributes, uint protect, uint maxSizeHigh, uint maxSizeLow, string? name);

    [LibraryImport(Kernel32, SetLastError = true)]
    internal static partial nint MapViewOfFile(SafeFileHandle mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnmapViewOfFile(nint baseAddress);

    [LibraryImport(Kernel32)]
    internal static partial nint GetCurrentProcess();

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PrefetchVirtualMemory(nint process, nuint numberOfEntries, Win32MemoryRangeEntry* entries, uint flags);

    // https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-rtlgetcompressionworkspacesize
    [LibraryImport("ntdll.dll")]
    internal static partial int RtlGetCompressionWorkSpaceSize(ushort format, out uint compressBufferWorkSpaceSize, out uint compressFragmentWorkSpaceSize);

    // https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-rtldecompressbufferex
    [LibraryImport("ntdll.dll")]
    internal static partial int RtlDecompressBufferEx(ushort format, byte* uncompressed, uint uncompressedSize, byte* compressed, uint compressedSize, out uint finalUncompressedSize, byte* workSpace);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsUserAnAdmin();
}
