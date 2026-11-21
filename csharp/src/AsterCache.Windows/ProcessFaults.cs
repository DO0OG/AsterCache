using System.Runtime.InteropServices;
using AsterCache.Windows.Native;

namespace AsterCache.Windows;

/// 프로세스 하나의 누계 값 (ProcessFaults.Snapshot): CpuTicks 는 커널 + 사용자 시간 (100 ns), WriteBytes 는 쓰기 전송 바이트 (디스크 구분 없음).
public readonly record struct ProcessUsage(int Pid, string Name, long CpuTicks, long WriteBytes);

/// 이 프로세스가 디스크에서 읽은 횟수의 누계 (하드 폴트 수): NtQuerySystemInformation(SystemProcessInformation) 의 자기 프로세스 항목 (HardFaultCount) 입니다. 프로세스를 열지 않고 읽기만 합니다.
/// 시스템 전체 페이지 읽기 누계 (SystemPageReads) 와 달리 다른 프로세스의 읽기가 섞이지 않습니다 (실측: 다른 프로세스가 같은 디스크를 읽고 모든 코어를 쓰는 동안 메모리에 있는 범위를 만지면 +0).
/// 조회 한 번이 모든 프로세스의 정보를 받아 오므로 약 4 ms 의 CPU 가 듭니다 (실측): 조각마다 부르지 말고 수십 MiB 만진 뒤에 부르십시오.
public static unsafe class ProcessFaults
{
    private const int SystemProcessInformation = 5;
    private const int HardFaultCountOffset = 16; // SYSTEM_PROCESS_INFORMATION.HardFaultCount (x64)
    private const int UserTimeOffset = 40; // SYSTEM_PROCESS_INFORMATION.UserTime (100 ns, x64)
    private const int KernelTimeOffset = 48; // SYSTEM_PROCESS_INFORMATION.KernelTime (100 ns, x64)
    private const int ImageNameLengthOffset = 56; // ImageName (UNICODE_STRING): Length (바이트), 8 바이트 뒤가 Buffer (x64)
    private const int ImageNameBufferOffset = 64;
    private const int ProcessIdOffset = 80; // SYSTEM_PROCESS_INFORMATION.UniqueProcessId (x64)
    private const int WriteTransferCountOffset = 240; // SYSTEM_PROCESS_INFORMATION.WriteTransferCount (x64): 디스크·네트워크·파이프를 가리지 않는 쓰기 바이트 누계
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);
    private static readonly object Gate = new();
    private static nuint* _buffer;
    private static uint _size;

    /// 지금의 누계, 읽을 수 없으면 (조회 실패, 자기 항목이 없음, 64 비트가 아님) null.
    public static uint? Query()
    {
        if (sizeof(nint) != 8)
        {
            return null;
        }

        try
        {
            return QueryLocked();
        }
        catch (OutOfMemoryException)
        {
            return null; // 조회 버퍼를 못 잡음: 읽을 수 없는 것으로 (호출한 쪽은 시간 안전망으로)
        }
    }

    private static uint? QueryLocked()
    {
        lock (Gate)
        {
            return FillLocked() ? Find((byte*)_buffer, Environment.ProcessId) : null;
        }
    }

    // 버퍼에 모든 프로세스의 정보를 채웁니다 (Gate 를 쥔 채 부름). 채우지 못하면 false.
    private static bool FillLocked()
    {
        if (_buffer == null)
        {
            _buffer = (nuint*)NativeMemory.Alloc(2 << 20);
            _size = 2 << 20;
        }

        for (int attempt = 0; attempt < 4; attempt++)
        {
            int status = NativeMethods.NtQuerySystemInformation(SystemProcessInformation, _buffer, _size, out uint needed);
            if (status == StatusInfoLengthMismatch)
            {
                uint size = needed + (512 << 10); // 그 사이 프로세스가 늘 수 있어 여유를 둠
                _buffer = (nuint*)NativeMemory.Realloc(_buffer, size); // 실패해 예외가 나도 _size 는 옛 값 그대로 (버퍼보다 큰 크기를 커널에 주지 않게)
                _size = size;
                continue;
            }

            return status >= 0;
        }

        return false;
    }

    /// 모든 프로세스의 CPU 시간 (커널 + 사용자, 100 ns 누계) 과 쓰기 바이트 누계를 into 에 담습니다 (프로세스를 열지 않고, 위 조회 한 번으로 얻음). 읽을 수 없으면 (조회 실패, 64 비트가 아님) false.
    /// 이름은 이미지 이름 (경로 없음) 이고, 시스템 유휴 프로세스 (번호 0) 는 CPU 시간이 유휴 시간이라 그대로 담으니 쓰는 쪽이 빼야 합니다.
    public static bool Snapshot(List<ProcessUsage> into)
    {
        into.Clear();
        if (sizeof(nint) != 8)
        {
            return false;
        }

        try
        {
            lock (Gate)
            {
                if (!FillLocked())
                {
                    return false;
                }

                for (byte* entry = (byte*)_buffer; ; entry += *(uint*)entry)
                {
                    int length = *(ushort*)(entry + ImageNameLengthOffset) / 2;
                    char* name = *(char**)(entry + ImageNameBufferOffset);
                    into.Add(new ProcessUsage((int)*(long*)(entry + ProcessIdOffset), name == null ? string.Empty : new string(name, 0, length),
                        *(long*)(entry + UserTimeOffset) + *(long*)(entry + KernelTimeOffset), (long)*(ulong*)(entry + WriteTransferCountOffset)));
                    if (*(uint*)entry == 0) // NextEntryOffset 0 = 마지막 항목
                    {
                        return true;
                    }
                }
            }
        }
        catch (OutOfMemoryException)
        {
            into.Clear();
            return false;
        }
    }

    private static uint? Find(byte* start, int pid)
    {
        for (byte* entry = start; ; entry += *(uint*)entry)
        {
            if (*(long*)(entry + ProcessIdOffset) == pid)
            {
                return *(uint*)(entry + HardFaultCountOffset);
            }

            if (*(uint*)entry == 0) // NextEntryOffset 0 = 마지막 항목
            {
                return null;
            }
        }
    }
}
