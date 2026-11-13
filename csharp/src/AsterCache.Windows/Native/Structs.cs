using System.Runtime.InteropServices;

namespace AsterCache.Windows.Native;

/// FSCTL_MOVE_FILE 입력 (x64 에서 32바이트).
/// https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-move_file_data
[StructLayout(LayoutKind.Sequential)]
internal struct MoveFileData
{
    public nint FileHandle;
    public long StartingVcn;
    public long StartingLcn;
    public uint ClusterCount;
}

/// FSCTL_GET_NTFS_VOLUME_DATA 출력 (96바이트).
/// https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-ntfs_volume_data_buffer
[StructLayout(LayoutKind.Sequential)]
internal struct NtfsVolumeDataBuffer
{
    public long VolumeSerialNumber;
    public long NumberSectors;
    public long TotalClusters;
    public long FreeClusters;
    public long TotalReserved;
    public uint BytesPerSector;
    public uint BytesPerCluster;
    public uint BytesPerFileRecordSegment;
    public uint ClustersPerFileRecordSegment;
    public long MftValidDataLength;
    public long MftStartLcn;
    public long Mft2StartLcn;
    public long MftZoneStart;
    public long MftZoneEnd;
}

/// IOCTL_DISK_PERFORMANCE 출력 (DISK_PERFORMANCE, x64/x86 에서 88 바이트).
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DiskPerformanceBuffer
{
    public long BytesRead;
    public long BytesWritten;
    public long ReadTime;
    public long WriteTime;
    public long IdleTime;
    public uint ReadCount;
    public uint WriteCount;
    public uint QueueDepth;
    public uint SplitCount;
    public long QueryTime;
    public uint StorageDeviceNumber;
    public fixed char StorageManagerName[8];
}

/// GetFileInformationByHandleEx(FileBasicInfo) 출력 (40바이트, 시각은 FILETIME 100ns 단위).
/// https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_basic_info
[StructLayout(LayoutKind.Sequential)]
internal struct FileBasicInfo
{
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public long ChangeTime;
    public uint FileAttributes;
}

/// PrefetchVirtualMemory 입력 한 줄 (x64 에서 16바이트).
/// https://learn.microsoft.com/en-us/windows/win32/api/memoryapi/ns-memoryapi-win32_memory_range_entry
[StructLayout(LayoutKind.Sequential)]
public struct Win32MemoryRangeEntry
{
    public nint VirtualAddress;
    public nuint NumberOfBytes;
}

/// LUID (권한 식별자, 8바이트).
[StructLayout(LayoutKind.Sequential)]
internal struct Luid
{
    public uint LowPart;
    public int HighPart;
}

/// TOKEN_PRIVILEGES 권한 하나짜리 (16바이트).
/// https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-token_privileges
[StructLayout(LayoutKind.Sequential)]
internal struct TokenPrivileges
{
    public uint Count;
    public Luid Luid;
    public uint Attributes;
}

/// GetProcessMemoryInfo 출력 (PROCESS_MEMORY_COUNTERS, x64 에서 72바이트).
/// https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessMemoryCounters
{
    public uint Size;
    public uint PageFaultCount;
    public nuint PeakWorkingSetSize;
    public nuint WorkingSetSize;
    public nuint QuotaPeakPagedPoolUsage;
    public nuint QuotaPagedPoolUsage;
    public nuint QuotaPeakNonPagedPoolUsage;
    public nuint QuotaNonPagedPoolUsage;
    public nuint PagefileUsage;
    public nuint PeakPagefileUsage;
}
