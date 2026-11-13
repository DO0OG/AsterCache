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
    public const uint IoctlDiskPerformance = 0x00070020;       // CTL_CODE(IOCTL_DISK_BASE = 7, 8, METHOD_BUFFERED, FILE_ANY_ACCESS)
    public const uint IoctlDiskPerformanceOff = 0x00070060;    // CTL_CODE(IOCTL_DISK_BASE = 7, 0x18, METHOD_BUFFERED, FILE_ANY_ACCESS)

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
    public const uint ProcessSetInformation = 0x0200;
    public const uint ProcessSetQuota = 0x0100;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const int ProcessMemoryPriorityClass = 0; // PROCESS_INFORMATION_CLASS.ProcessMemoryPriority
    public const uint TokenAdjustPrivileges = 0x0020;
    public const uint TokenQuery = 0x0008;
    public const uint PrivilegeEnabled = 2; // SE_PRIVILEGE_ENABLED
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

    // 매핑한 뷰를 읽을 때 디스크 오류를 예외 (in-page error) 가 아닌 반환값으로 받으려고 씁니다 (현재 프로세스 자신만).
    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ReadProcessMemory(nint process, nint baseAddress, byte* buffer, nuint size, out nuint read);

    [LibraryImport(Kernel32)]
    internal static partial nint GetCurrentProcess();

    [LibraryImport(Kernel32)]
    internal static partial nint GetCurrentThread();

    // https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntsetinformationthread (ThreadIoPriority = 22, 값은 IO_PRIORITY_HINT)
    [LibraryImport("ntdll.dll")]
    internal static partial int NtSetInformationThread(nint thread, int infoClass, uint* info, uint size);

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQueryInformationThread(nint thread, int infoClass, uint* info, uint size, out uint returned);

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

    [LibraryImport(Kernel32, SetLastError = true)]
    internal static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport(Kernel32, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, char* exeName, ref uint size);

    [LibraryImport(Kernel32, EntryPoint = "K32GetProcessMemoryInfo", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessMemoryInfo(SafeProcessHandle process, ProcessMemoryCounters* counters, uint size);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(SafeProcessHandle process, long* creation, long* exit, long* kernel, long* user);

    [LibraryImport(Kernel32, EntryPoint = "K32EmptyWorkingSet", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EmptyWorkingSet(SafeProcessHandle process);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessInformation(SafeProcessHandle process, int infoClass, byte* info, uint size);

    [LibraryImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessInformation(SafeProcessHandle process, int infoClass, byte* info, uint size);

    // https://learn.microsoft.com/en-us/windows/win32/api/winternl/nf-winternl-ntquerysysteminformation
    [LibraryImport("ntdll.dll")]
    internal static partial int NtQuerySystemInformation(int infoClass, nuint* info, uint size, out uint returned);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LookupPrivilegeValueW(string? systemName, string name, out Luid luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustTokenPrivileges(SafeAccessTokenHandle token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, TokenPrivileges* state, uint length, nint previous, nint returnLength);
}
