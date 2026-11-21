using System.Diagnostics;
using AsterCache.Core.Trace;
using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// NTFS 변경 저널 (USN) 읽기: 문서화된 FSCTL_QUERY_USN_JOURNAL·FSCTL_READ_USN_JOURNAL 로 볼륨의 저널을 처음부터 읽어 찾는 파일 (파일 참조 번호) 의 기록만 모읍니다.
/// 볼륨을 열려면 SYSTEM 이나 관리자 권한이 필요합니다 (서비스는 SYSTEM). 읽기 전용이며 게임·런처 프로세스를 열지 않습니다. 한 번 읽을 때 시간과 읽는 양에 상한이 있고, 넘으면 일부만 읽은 것이므로 null (못 읽음) 입니다.
public static unsafe class UsnJournal
{
    /// 한 번 읽기의 시간 상한과 읽는 바이트 상한: 이 PC 의 D: 저널 32 MB 가 수 초 안에 읽히는 정도를 넉넉히 덮고, 훨씬 큰 저널은 일부만 읽은 판정을 하지 않고 포기합니다.
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(30);

    public const long DefaultMaxBytes = 512L << 20;

    private const uint FsctlQueryUsnJournal = 0x000900F4;
    private const uint FsctlReadUsnJournal = 0x000900BB;
    private const uint GenericRead = 0x80000000;
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const uint FileReadAttributes = 0x80;
    private const uint BackupSemantics = 0x02000000;
    private const int FileIdInfoClass = 18;
    private const int BufferBytes = 1 << 20;
    private const long FileTimeEpochMin = 1;
    private const long MaxFileTime = 2650467743999999999; // DateTime.MaxValue 의 FILETIME

    /// 파일의 64 비트 파일 참조 번호 (NTFS: 레코드 번호와 순번). 속성만 읽는 열기라 게임의 공유 모드와 부딪치지 않습니다. 열지 못하면 null (그 파일은 판정 불가).
    public static ulong? FileReference(string path)
    {
        using SafeFileHandle handle = NativeMethods.CreateFileW(path, FileReadAttributes, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        byte* info = stackalloc byte[24]; // FILE_ID_INFO: 볼륨 일련번호 8 + FILE_ID_128 16 (NTFS 는 앞 8 바이트가 참조 번호)
        return NativeMethods.GetFileInformationByHandleEx(handle, FileIdInfoClass, info, 24) ? *(ulong*)(info + 8) : null;
    }

    /// volume (예: "D:") 의 변경 저널을 읽어 frns 에 든 파일의 기록과 저널의 가장 오래된 기록 시각을 돌려줍니다. 저널이 없거나 볼륨을 열 수 없거나 상한을 넘기거나 읽기에 실패하면 null.
    public static UsnRead? Read(string volume, IReadOnlySet<ulong> frns, TimeSpan budget, long maxBytes, CancellationToken ct = default)
    {
        string device = @"\\.\" + volume.TrimEnd('\\');
        using SafeFileHandle handle = NativeMethods.CreateFileW(device, GenericRead, ShareAll, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        byte* query = stackalloc byte[56];
        if (!NativeMethods.DeviceIoControl(handle, FsctlQueryUsnJournal, null, 0, query, 56, out _, 0))
        {
            return null;
        }

        ulong journalId = *(ulong*)query;
        long next = *(long*)(query + 8); // FirstUsn
        long end = *(long*)(query + 16); // NextUsn
        byte* buffer = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc(BufferBytes);
        try
        {
            var events = new List<UsnEvent>();
            DateTime? oldest = null;
            long total = 0;
            long started = Stopwatch.GetTimestamp();
            byte* request = stackalloc byte[40];
            while (next < end)
            {
                if (ct.IsCancellationRequested || total > maxBytes || Stopwatch.GetElapsedTime(started) > budget)
                {
                    return null;
                }

                *(long*)request = next; // StartUsn
                *(uint*)(request + 8) = 0xFFFFFFFF; // ReasonMask: 모든 사유
                *(uint*)(request + 12) = 0; // ReturnOnlyOnClose
                *(ulong*)(request + 16) = 0; // Timeout
                *(ulong*)(request + 24) = 0; // BytesToWaitFor: 기다리지 않음
                *(ulong*)(request + 32) = journalId;
                if (!NativeMethods.DeviceIoControl(handle, FsctlReadUsnJournal, request, 40, buffer, BufferBytes, out uint returned, 0) || returned < 8)
                {
                    return null;
                }

                long after = *(long*)buffer;
                total += returned;
                if (!ParseRecords(new ReadOnlySpan<byte>(buffer + 8, (int)returned - 8), frns, events, ref oldest) || after <= next)
                {
                    return null; // 해석하지 못한 기록이 있거나 읽기가 나아가지 않음: 일부만 읽은 결과로 판정하지 않음
                }

                next = after;
            }

            return new UsnRead(oldest ?? DateTime.UtcNow, events);
        }
        finally
        {
            System.Runtime.InteropServices.NativeMemory.Free(buffer);
        }
    }

    /// USN_RECORD_V2 들을 걸으며 처음 본 기록의 시각을 oldest 에 남기고, 찾는 번호의 기록만 events 에 더합니다. 끝까지 해석했으면 true, 모양이 맞지 않는 기록 (v2 가 아님, 길이 이상, 시각 이상, 남는 바이트) 을 만나면 거기서 멈추고 false 입니다 (일부만 읽은 결과).
    internal static bool ParseRecords(ReadOnlySpan<byte> data, IReadOnlySet<ulong> frns, List<UsnEvent> events, ref DateTime? oldest)
    {
        int at = 0;
        while (at < data.Length)
        {
            if (at + 60 > data.Length)
            {
                return false;
            }

            int length = BitConverter.ToInt32(data.Slice(at, 4));
            ushort major = BitConverter.ToUInt16(data.Slice(at + 4, 2));
            if (length < 60 || at + length > data.Length || major != 2)
            {
                return false;
            }

            ulong frn = BitConverter.ToUInt64(data.Slice(at + 8, 8));
            long fileTime = BitConverter.ToInt64(data.Slice(at + 32, 8));
            uint reason = BitConverter.ToUInt32(data.Slice(at + 40, 4));
            if (fileTime < FileTimeEpochMin || fileTime > MaxFileTime)
            {
                return false; // 시각을 읽을 수 없는 기록: 처음 기록이면 덮는 범위를 알 수 없음
            }

            DateTime time = DateTime.FromFileTimeUtc(fileTime);
            oldest ??= time; // 저널은 USN 순이므로 처음 본 기록이 가장 오래됨 (이후 시계가 되돌려진 기록이 덮는 범위를 넓히지 않게 처음 값만 씀)

            if (frns.Contains(frn))
            {
                events.Add(new UsnEvent(frn, time, reason));
            }

            at += length;
        }

        return true;
    }
}
