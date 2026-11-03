using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AsterCache.Core.Model;
using AsterCache.Core.Volume;
using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// \\.\X: 핸들 (관리자 전용). Python relayout/mover.py Volume. 시험은 이 클래스를 상속해 Ioctl 을 바꿉니다.
public class NtfsVolume : IVolume
{
    private readonly SafeFileHandle? _handle;
    private readonly ConcurrentBag<SafeFileHandle> _spare = [];
    private readonly List<SafeFileHandle> _spareAll = [];
    private int _mainBusy;
    private IoPriorityHint? _hint;

    // 이 스레드의 Move 가 쓸 볼륨 핸들 (Ioctl 이 _handle 대신 봄).
    [ThreadStatic]
    private static SafeFileHandle? t_device;

    protected NtfsVolume(SafeFileHandle? handle, string letter)
    {
        _handle = handle;
        Letter = letter;
    }

    public string Letter { get; }

    private string? _drive;

    public int ClusterBytes { get; private set; }

    public long TotalClusters { get; private set; }

    public long FreeClusters { get; private set; }

    public (long Start, long End) MftZone { get; private set; }

    /// 관리자가 아니면 볼륨을 열기 전에 NotAdminException. isAdmin 은 시험용 대체 판정입니다.
    public static NtfsVolume Open(string letter, Func<bool>? isAdmin = null)
    {
        Elevation.RequireAdmin(isAdmin);
        string drive = letter.TrimEnd(':', '\\');
        SafeFileHandle h = NativeMethods.CreateFileW(@"\\.\" + drive + ":", Win32.GenericRead, Win32.ShareAll, 0, Win32.OpenExisting, 0, 0);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw new Win32Exception(err);
        }

        var v = new NtfsVolume(h, drive.ToUpperInvariant() + ":") { _drive = drive };
        try
        {
            v.Refresh();
        }
        catch
        {
            v.Dispose();
            throw;
        }

        return v;
    }

    /// FSCTL_GET_NTFS_VOLUME_DATA 로 클러스터 크기·전체/빈 클러스터·MFT 존을 다시 읽습니다.
    public void Refresh()
    {
        (bool ok, int err, byte[] output) = Ioctl(Win32.FsctlGetNtfsVolumeData, default, Unsafe.SizeOf<NtfsVolumeDataBuffer>() + 64);
        if (!ok)
        {
            throw new Win32Exception(err);
        }

        NtfsVolumeDataBuffer d = MemoryMarshal.Read<NtfsVolumeDataBuffer>(output);
        ClusterBytes = (int)d.BytesPerCluster;
        TotalClusters = d.TotalClusters;
        FreeClusters = d.FreeClusters;
        MftZone = (d.MftZoneStart, d.MftZoneEnd);
    }

    /// 볼륨 전체 비트맵을 chunkBytes 조각으로 받습니다 (시작 LCN 은 항상 8 의 배수).
    /// VOLUME_BITMAP_BUFFER (https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-volume_bitmap_buffer):
    /// { LARGE_INTEGER StartingLcn; LARGE_INTEGER BitmapSize; BYTE Buffer[]; } 바이트 j 의 비트 i = LCN StartingLcn + 8j + i, 1 = 사용 중.
    /// 입력 STARTING_LCN_INPUT_BUFFER (https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-starting_lcn_input_buffer): LARGE_INTEGER StartingLcn.
    public VolumeBitmap Bitmap() => Bitmap(1 << 20);

    public VolumeBitmap Bitmap(int chunkBytes)
    {
        long start = 0;
        long? total = null;
        var data = new List<byte>();
        Span<byte> input = stackalloc byte[8];
        while (true)
        {
            BinaryPrimitives.WriteInt64LittleEndian(input, start);
            (bool ok, int err, byte[] output) = Ioctl(Win32.FsctlGetVolumeBitmap, input, 16 + chunkBytes);
            if (!ok && err != Win32.ErrorMoreData)
            {
                throw new Win32Exception(err);
            }

            if (output.Length < 16)
            {
                throw new IOException("unexpected bitmap chunk");
            }

            long gotStart = BinaryPrimitives.ReadInt64LittleEndian(output);
            long size = BinaryPrimitives.ReadInt64LittleEndian(output.AsSpan(8));
            ReadOnlySpan<byte> payload = output.AsSpan(16);
            if (gotStart != start || (!ok && payload.IsEmpty))
            {
                throw new IOException("unexpected bitmap chunk");
            }

            total ??= start + size;
            if (ok)
            {
                payload = payload[..(int)Math.Min(payload.Length, (size + 7) / 8)];
            }

            data.AddRange(payload);
            start += payload.Length * 8L;
            if (ok || start >= total)
            {
                return new VolumeBitmap(0, total.Value, data.ToArray());
            }
        }
    }

    /// 이 볼륨 핸들과 이동하는 파일 핸들에 낮은 I/O 우선순위 힌트를 붙입니다 (서비스의 백그라운드 이동용, 수동 적용은 쓰지 않음).
    /// FSCTL_MOVE_FILE 이 커널 안에서 내는 복사 I/O 에 힌트가 적용되는지는 문서가 보장하지 않습니다.
    public void UseLowIoPriority(IoPriorityHint hint)
    {
        _hint = hint;
        if (_handle is { IsInvalid: false })
        {
            hint.SetLow(_handle);
        }
    }

    /// FSCTL_MOVE_FILE: path 의 clusters 개 클러스터(VCN vcn 부터)를 물리 lcn 으로 다시 매핑합니다. 실패하면 Win32Exception.
    public void Move(string path, long vcn, long lcn, uint clusters)
    {
        // 동기 핸들 (FILE_FLAG_OVERLAPPED 없음) 의 DeviceIoControl 은 파일 객체 잠금으로 직렬화되므로, 같은 볼륨 핸들을 여러 스레드가 쓰면 호출이 겹치지 않음.
        // 그래서 동시에 부르는 스레드마다 볼륨 핸들을 따로 씁니다: 기본 핸들이 비어 있으면 그것을 (순차 실행은 항상 이 길), 아니면 여분 핸들을 꺼내거나 새로 엽니다.
        // OVERLAPPED 로 한 핸들을 공유하는 길은 입출력 완료 대기와 핸들 우선순위 힌트가 복잡해져 택하지 않음.
        bool main = Interlocked.CompareExchange(ref _mainBusy, 1, 0) == 0;
        SafeFileHandle? spare = main ? null : RentSpare();
        t_device = spare;
        try
        {
            MoveOn(path, vcn, lcn, clusters);
        }
        finally
        {
            t_device = null;
            if (spare is not null)
            {
                _spare.Add(spare);
            }

            if (main)
            {
                _ = Interlocked.Exchange(ref _mainBusy, 0);
            }
        }
    }

    // 쓰던 여분 핸들이 있으면 그것을, 없으면 새로 엽니다. 열 수 없거나 시험용 볼륨 (핸들 없음) 이면 null = 기본 핸들을 공유 (이 경우 호출은 직렬화될 뿐 틀리지 않음).
    private SafeFileHandle? RentSpare()
    {
        if (_spare.TryTake(out SafeFileHandle? h))
        {
            return h;
        }

        if (_drive is null)
        {
            return null;
        }

        h = NativeMethods.CreateFileW(@"\\.\" + _drive + ":", Win32.GenericRead, Win32.ShareAll, 0, Win32.OpenExisting, 0, 0);
        if (h.IsInvalid)
        {
            h.Dispose();
            return null;
        }

        if (_hint is { } hint)
        {
            hint.SetLow(h);
        }

        lock (_spareAll)
        {
            _spareAll.Add(h);
        }

        return h;
    }

    private void MoveOn(string path, long vcn, long lcn, uint clusters)
    {
        using SafeFileHandle h = NativeMethods.CreateFileW(path, Win32.FileReadAttributes, Win32.ShareAll, 0, Win32.OpenExisting, 0, 0);
        if (h.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        _hint?.SetLow(h);
        var req = new MoveFileData { FileHandle = h.DangerousGetHandle(), StartingVcn = vcn, StartingLcn = lcn, ClusterCount = clusters };
        (bool ok, int err, _) = Ioctl(Win32.FsctlMoveFile, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref req, 1)), 0);
        if (!ok)
        {
            throw new Win32Exception(err);
        }
    }

    public void Dispose()
    {
        _handle?.Dispose();
        lock (_spareAll)
        {
            foreach (SafeFileHandle h in _spareAll)
            {
                h.Dispose();
            }
        }

        GC.SuppressFinalize(this);
    }

    /// DeviceIoControl 한 번. (성공 여부, 실패 시 Win32 오류, 출력 바이트). 시험이 대체하는 지점입니다.
    protected virtual unsafe (bool Ok, int Error, byte[] Output) Ioctl(uint code, ReadOnlySpan<byte> input, int outputLength)
    {
        byte[] output = new byte[outputLength];
        bool ok;
        int err = 0;
        uint got;
        fixed (byte* pin = input)
        fixed (byte* pout = output)
        {
            ok = NativeMethods.DeviceIoControl(t_device ?? _handle!, code, input.IsEmpty ? null : pin, (uint)input.Length, outputLength == 0 ? null : pout, (uint)outputLength, out got, 0);
            if (!ok)
            {
                err = Marshal.GetLastPInvokeError();
            }
        }

        return (ok, err, output[..(int)got]);
    }
}
