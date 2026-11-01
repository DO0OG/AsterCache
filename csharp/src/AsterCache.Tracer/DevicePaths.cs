using System.Runtime.InteropServices;

namespace AsterCache.Tracer;

/// ETW 파일 이름 (`\Device\HarddiskVolume3\Games\a.bin`) → 드라이브 문자 경로 (`D:\Games\a.bin`).
/// 대응표는 QueryDosDeviceW("D:") 의 결과 (`\Device\HarddiskVolume3`) 로 만듭니다.
public sealed partial class DevicePaths
{
    private const int BufferChars = 32768;
    private readonly List<(string Device, string Drive)> _map;

    public DevicePaths(IEnumerable<KeyValuePair<string, string>> deviceToDrive)
    {
        _map = deviceToDrive.OrderByDescending(p => p.Key.Length).Select(p => (p.Key, p.Value)).ToList();
    }

    public IReadOnlyList<(string Device, string Drive)> Map => _map;

    /// 이 PC 의 A: 에서 Z: 까지. query 는 시험용 대체 (장치 이름 → 대상 문자열, 없으면 null).
    public static DevicePaths FromSystem(Func<string, string?>? query = null)
    {
        query ??= QueryDosDevice;
        var pairs = new List<KeyValuePair<string, string>>();
        for (char c = 'A'; c <= 'Z'; c++)
        {
            string drive = c + ":";
            string? target = query(drive);
            if (target is not null && target.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase))
            {
                pairs.Add(new KeyValuePair<string, string>(target, drive));
            }
        }

        return new DevicePaths(pairs);
    }

    /// 장치 접두가 맞으면 드라이브 문자로 바꾸고, 아니면 (`\Device\Mup`, 이미 드라이브 경로 등) 그대로 돌려줍니다.
    public string ToDosPath(string path)
    {
        foreach ((string device, string drive) in _map)
        {
            if (path.StartsWith(device, StringComparison.OrdinalIgnoreCase)
                && (path.Length == device.Length || path[device.Length] == '\\'))
            {
                return drive + path[device.Length..];
            }
        }

        return path;
    }

    /// QueryDosDeviceW 의 첫 문자열 (현재 대응). 장치가 없으면 null.
    public static string? QueryDosDevice(string deviceName)
    {
        char[] buffer = new char[BufferChars];
        uint n = QueryDosDeviceW(deviceName, buffer, (uint)buffer.Length);
        if (n == 0)
        {
            return null;
        }

        int end = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, end < 0 ? (int)n : end);
    }

    [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint QueryDosDeviceW(string deviceName, [Out] char[] targetPath, uint max);
}
