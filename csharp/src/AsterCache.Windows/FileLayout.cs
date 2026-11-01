using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using AsterCache.Core.Model;
using AsterCache.Core.Prefetch;
using AsterCache.Windows.Native;

namespace AsterCache.Windows;

/// NTFS 파일의 물리 배치 (FSCTL_GET_RETRIEVAL_POINTERS, 관리자 불필요). Python prefetch/layout.py 포팅.
///
/// 출력 버퍼 (RETRIEVAL_POINTERS_BUFFER, https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-retrieval_pointers_buffer):
///   DWORD ExtentCount; (4바이트 패딩) LARGE_INTEGER StartingVcn;
///   struct { LARGE_INTEGER NextVcn; LARGE_INTEGER Lcn; } Extents[ExtentCount];
/// extent i 는 VCN [앞 NextVcn (처음은 StartingVcn), NextVcn) 을 덮고, Lcn -1 은 스파스 구멍.
/// 입력 (STARTING_VCN_INPUT_BUFFER, https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-starting_vcn_input_buffer): LARGE_INTEGER StartingVcn.
public static class FileLayout
{
    private const int Header = 16; // ExtentCount + 패딩 + StartingVcn
    private const int MaxChunkExtents = 4096;
    private const int MaxIterations = 100000; // ERROR_MORE_DATA 반복 상한

    /// 파일이 놓인 볼륨의 클러스터 바이트 수, 실패하면 null.
    public static unsafe int? ClusterBytes(string path)
    {
        try
        {
            char* root = stackalloc char[1024];
            if (!NativeMethods.GetVolumePathNameW(Path.GetFullPath(path), root, 1024))
            {
                return null;
            }

            if (!NativeMethods.GetDiskFreeSpaceW(new string(root), out uint spc, out uint bps, out _, out _))
            {
                return null;
            }

            return checked((int)(spc * bps));
        }
        catch (Exception e) when (IsExpected(e))
        {
            return null;
        }
    }

    /// VCN 순 extent 목록. 실패하거나 스파스 구멍이 있으면 null, 빈 목록은 디스크 extent 없음 (MFT 상주·빈 파일).
    public static IReadOnlyList<Extent>? Extents(string path)
    {
        try
        {
            using Microsoft.Win32.SafeHandles.SafeFileHandle h = NativeMethods.CreateFileW(
                Path.GetFullPath(path), Win32.FileReadAttributes, Win32.ShareAll, 0, Win32.OpenExisting, 0, 0);
            return h.IsInvalid ? null : ReadExtents(h);
        }
        catch (Exception e) when (IsExpected(e))
        {
            return null;
        }
    }

    /// Python prefetch.cli._layout: extent 도 클러스터 크기도 알 때만 값을 돌려줍니다.
    public static FileLayoutInfo? Get(string path)
    {
        IReadOnlyList<Extent>? ext = Extents(path);
        int? cs = ext is null ? null : ClusterBytes(path);
        return ext is not null && cs is > 0 ? new FileLayoutInfo(ext, cs.Value) : null;
    }

    private static unsafe List<Extent>? ReadExtents(Microsoft.Win32.SafeHandles.SafeFileHandle h)
    {
        var output = new List<Extent>();
        long start = 0;
        byte[] buf = new byte[Header + 16 * MaxChunkExtents];
        Span<byte> input = stackalloc byte[8];
        for (int iteration = 0; iteration < MaxIterations; iteration++)
        {
            BinaryPrimitives.WriteInt64LittleEndian(input, start);
            bool ok;
            int err = 0;
            fixed (byte* pin = input)
            fixed (byte* pout = buf)
            {
                ok = NativeMethods.DeviceIoControl(h, Win32.FsctlGetRetrievalPointers, pin, 8, pout, (uint)buf.Length, out _, 0);
                if (!ok)
                {
                    err = Marshal.GetLastPInvokeError();
                }
            }

            if (!ok && err != Win32.ErrorMoreData)
            {
                return null;
            }

            uint count = BinaryPrimitives.ReadUInt32LittleEndian(buf);
            long vcn = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(8));
            if (count > MaxChunkExtents)
            {
                return null;
            }

            for (int i = 0; i < count; i++)
            {
                long next = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(Header + 16 * i));
                long lcn = BinaryPrimitives.ReadInt64LittleEndian(buf.AsSpan(Header + 16 * i + 8));
                if (lcn < 0)
                {
                    return null; // 스파스·압축 구멍: 쓸 수 있는 물리 순서 없음
                }

                output.Add(new Extent(vcn, lcn, next - vcn));
                vcn = next;
            }

            if (ok)
            {
                return output;
            }

            if (count == 0)
            {
                return null;
            }

            start = vcn;
        }

        return null;
    }

    private static bool IsExpected(Exception e) =>
        e is IOException or ArgumentException or NotSupportedException or UnauthorizedAccessException or Win32Exception or OverflowException;
}
