using System.Buffers.Binary;
using System.Text;
using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// 볼륨이 놓인 디스크의 모델과 탐색 지연 유무. IncursSeekPenalty 가 true 면 HDD, false 면 SSD, 알 수 없으면 null. Trim 은 TRIM 지원 (알 수 없으면 null).
public sealed record DiskDetails(string Model, bool? IncursSeekPenalty, bool? Trim = null);

/// IOCTL_STORAGE_QUERY_PROPERTY (StorageDeviceProperty, StorageDeviceSeekPenaltyProperty, StorageDeviceTrimProperty). 접근 권한 0 으로 볼륨을 열므로 관리자가 필요 없습니다.
public static unsafe class DiskInfo
{
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const uint StorageDeviceProperty = 0;
    private const uint StorageDeviceSeekPenaltyProperty = 7;
    private const uint StorageDeviceTrimProperty = 8;

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
        return new DiskDetails(device is null ? string.Empty : ParseModel(device), ParseSeekPenalty(seek), ParseTrim(trim));
    }

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
