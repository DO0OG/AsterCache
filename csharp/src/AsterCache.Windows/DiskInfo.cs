using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// 볼륨이 놓인 디스크의 모델과 탐색 지연 유무. IncursSeekPenalty 가 true 면 HDD, false 면 SSD, 알 수 없으면 null. Trim 은 TRIM 지원 (알 수 없으면 null).
public sealed record DiskDetails(string Model, bool? IncursSeekPenalty, bool? Trim = null, uint BusType = 0);

/// IOCTL_STORAGE_QUERY_PROPERTY (StorageDeviceProperty, StorageDeviceSeekPenaltyProperty, StorageDeviceTrimProperty). 접근 권한 0 으로 볼륨을 열므로 관리자가 필요 없습니다.
public static unsafe class DiskInfo
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint StorageDeviceProperty = 0;
    private const uint StorageDeviceSeekPenaltyProperty = 7;
    private const uint StorageDeviceTrimProperty = 8;

    /// 게임 설치 경로가 있는 물리 디스크의 누적 읽기 통계 수집기. 게임 파일을 열거나 읽지 않습니다.
    public static DiskPerformanceMonitor? OpenPerformanceMonitor(string path) => DiskPerformanceMonitor.Open(path);

    /// 'X:' 의 디스크. 열지 못하거나 질의가 실패한 항목은 빈 모델과 null.
    public static DiskDetails Query(string letter)
    {
        using SafeFileHandle h = NativeMethods.CreateFileW(@"\\.\" + letter.TrimEnd(':', '\\') + ":", 0, Win32.ShareAll, 0, Win32.OpenExisting, 0, 0);
        if (h.IsInvalid)
        {
            return new DiskDetails(string.Empty, null);
        }

        byte[]? device = Property(h, StorageDeviceProperty, 1024);
        byte[]? seek = Property(h, StorageDeviceSeekPenaltyProperty, 12);
        byte[]? trim = Property(h, StorageDeviceTrimProperty, 12);
        return new DiskDetails(device is null ? string.Empty : ParseModel(device), ParseSeekPenalty(seek), ParseTrim(trim), device is { Length: >= 32 } ? BinaryPrimitives.ReadUInt32LittleEndian(device.AsSpan(28)) : 0);
    }

    /// STORAGE_BUS_TYPE 의 USB.
    public const uint BusTypeUsb = 7;

    /// 디스크 종류 문구 (로그·status 용). HDD 가 TRIM 을 받으면 SMR 로 보는 것은 추정일 뿐입니다.
    public static string Kind(bool? seekPenalty, bool? trim) => seekPenalty switch
    {
        false => "SSD",
        true => trim switch { true => "HDD (looks like SMR)", false => "HDD (probably CMR)", _ => "HDD" },
        _ => "unknown",
    };

    /// STORAGE_DEVICE_DESCRIPTOR: VendorIdOffset (12) 와 ProductIdOffset (16) 가 가리키는 NUL 끝 ASCII 문자열을 이어 붙입니다.
    internal static string ParseModel(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < 20)
        {
            return string.Empty;
        }

        string vendor = AsciiAt(descriptor, BinaryPrimitives.ReadUInt32LittleEndian(descriptor[12..]));
        string product = AsciiAt(descriptor, BinaryPrimitives.ReadUInt32LittleEndian(descriptor[16..]));
        return (vendor + " " + product).Trim();
    }

    /// DEVICE_SEEK_PENALTY_DESCRIPTOR: 8 바이트 머리 뒤의 BOOLEAN IncursSeekPenalty.
    internal static bool? ParseSeekPenalty(ReadOnlySpan<byte> descriptor) => descriptor.Length >= 9 ? descriptor[8] != 0 : null;

    /// DEVICE_TRIM_DESCRIPTOR: 8 바이트 머리 (Version, Size) 뒤의 BOOLEAN TrimEnabled.
    internal static bool? ParseTrim(ReadOnlySpan<byte> descriptor) => descriptor.Length >= 9 ? descriptor[8] != 0 : null;

    private static string AsciiAt(ReadOnlySpan<byte> buffer, uint offset)
    {
        if (offset == 0 || offset >= buffer.Length)
        {
            return string.Empty;
        }

        ReadOnlySpan<byte> rest = buffer[(int)offset..];
        int end = rest.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? rest : rest[..end]).Trim();
    }

    // STORAGE_PROPERTY_QUERY { PropertyId, QueryType = PropertyStandardQuery (0), AdditionalParameters[1] } 는 12 바이트.
    private static byte[]? Property(SafeFileHandle volume, uint propertyId, int outputSize)
    {
        byte[] query = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(query, propertyId);
        byte[] output = new byte[outputSize];
        fixed (byte* q = query)
        fixed (byte* o = output)
        {
            return NativeMethods.DeviceIoControl(volume, IoctlStorageQueryProperty, q, (uint)query.Length, o, (uint)output.Length, out uint returned, 0)
                ? output[..(int)returned]
                : null;
        }
    }
}

/// IOCTL_DISK_PERFORMANCE 의 누적 통계. 시간 값은 100 ns 단위입니다.
public sealed record DiskPerformanceSample(uint DiskNumber, long ReadBytes, uint ReadCount, long ReadTime100ns, long IdleTime100ns, uint QueueDepth);

/// 물리 디스크별 성능 카운터를 읽습니다. 각 성공한 IOCTL_PERFORMANCE 에 대응해 IOCTL_PERFORMANCE_OFF 를 호출해 드라이버 참조 수를 균형 있게 유지합니다.
public sealed unsafe class DiskPerformanceMonitor : IDisposable
{
    private sealed class Device(uint number, SafeFileHandle handle)
    {
        public uint Number { get; } = number;
        public SafeFileHandle Handle { get; } = handle;
        public int References { get; set; }
        public DiskPerformanceSample Initial { get; set; } = new(number, 0, 0, 0, 0, 0);
    }

    private readonly List<Device> _devices;
    private bool _disposed;

    private DiskPerformanceMonitor(List<Device> devices, string physicalKey)
    {
        _devices = devices;
        PhysicalKey = physicalKey;
    }

    /// 시작 시 각 물리 디스크에서 읽은 기준값입니다.
    public IReadOnlyList<DiskPerformanceSample> Initial => _devices.Select(device => device.Initial).ToArray();
    public string PhysicalKey { get; }

    public static DiskPerformanceMonitor? Open(string path)
    {
        IReadOnlySet<uint>? numbers = VolumeDisks.DiskNumbers(path);
        if (numbers is not { Count: > 0 })
        {
            return null;
        }

        List<Device> devices = [];
        try
        {
            foreach (uint number in numbers.Order())
            {
                SafeFileHandle handle = NativeMethods.CreateFileW($@"\\.\PhysicalDrive{number}", 0, Win32.ShareAll, 0, Win32.OpenExisting, 0, 0);
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return DisposeAndFail(devices);
                }

                Device device = new(number, handle);
                devices.Add(device);
                if (QueryAndDisable(device) is not { } sample || !Valid(sample))
                {
                    return DisposeAndFail(devices);
                }

                device.Initial = sample;
            }

            string physicalKey = "disk" + string.Join('+', numbers.Order());
            return new DiskPerformanceMonitor(devices, physicalKey);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OverflowException)
        {
            _ = DisposeAndFail(devices);
            return null;
        }
    }

    /// 마지막 기준값 이후의 누적 통계. 조회가 실패하면 null 입니다.
    public IReadOnlyList<DiskPerformanceSample>? Read()
    {
        if (_disposed)
        {
            return null;
        }

        List<DiskPerformanceSample> samples = [];
        foreach (Device device in _devices)
        {
            if (QueryAndDisable(device) is not { } sample || !Valid(sample))
            {
                return null;
            }

            samples.Add(sample);
        }

        return samples;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (Device device in _devices)
        {
            while (device.References > 0 && DisableOne(device))
            {
            }

            device.Handle.Dispose();
        }
    }

    private static DiskPerformanceMonitor? DisposeAndFail(List<Device> devices)
    {
        foreach (Device device in devices)
        {
            while (device.References > 0 && DisableOne(device))
            {
            }

            device.Handle.Dispose();
        }

        return null;
    }

    private static DiskPerformanceSample? Query(Device device)
    {
        byte[] output = new byte[Marshal.SizeOf<DiskPerformanceBuffer>()];
        fixed (byte* buffer = output)
        {
            if (!NativeMethods.DeviceIoControl(device.Handle, Win32.IoctlDiskPerformance, null, 0, buffer, (uint)output.Length, out uint returned, 0))
            {
                return null;
            }

            device.References++;
            if (returned < (uint)output.Length)
            {
                return null;
            }

            DiskPerformanceBuffer data = MemoryMarshal.Read<DiskPerformanceBuffer>(output);
            return new DiskPerformanceSample(device.Number, data.BytesRead, data.ReadCount, data.ReadTime, data.IdleTime, data.QueueDepth);
        }
    }

    private static DiskPerformanceSample? QueryAndDisable(Device device)
    {
        DiskPerformanceSample? sample = null;
        try
        {
            sample = Query(device);
        }
        finally
        {
            if (!DisableOne(device))
            {
                sample = null;
            }
        }

        return sample;
    }

    private static bool Valid(DiskPerformanceSample sample) => sample.ReadBytes >= 0 && sample.ReadTime100ns >= 0 && sample.IdleTime100ns >= 0;

    private static bool DisableOne(Device device)
    {
        if (device.References <= 0)
        {
            return true;
        }

        bool disabled = NativeMethods.DeviceIoControl(device.Handle, Win32.IoctlDiskPerformanceOff, null, 0, null, 0, out _, 0);
        if (disabled)
        {
            device.References--;
        }

        return disabled;
    }
}
