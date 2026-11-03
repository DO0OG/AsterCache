using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Service;

/// Windows 프리페처가 켜져 있는가 (읽기만 함). 프리페치 기록 (.pf) 으로 첫 실행 없이 boot 트레이스를 만들려면 프리페처가 응용 프로그램을 기록하고 있어야 합니다.
public static class SystemProbes
{
    private const string PrefetchKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";
    private const int ApplicationBit = 1; // EnablePrefetcher: 1 = 응용 프로그램, 2 = 부팅, 3 = 둘 다

    public static bool PrefetcherOn() => PrefetcherOn(SysMainRunning, EnablePrefetcher);

    /// SysMain 서비스가 돌고 있고 EnablePrefetcher 의 응용 프로그램 비트가 켜져 있을 때만 참 (값을 읽지 못하면 거짓).
    public static bool PrefetcherOn(Func<bool> sysMainRunning, Func<int?> enablePrefetcher) => enablePrefetcher() is { } v && (v & ApplicationBit) != 0 && sysMainRunning();

    private static bool SysMainRunning()
    {
        try
        {
            using var sc = new ServiceController("SysMain");
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false; // 서비스가 없거나 읽을 수 없음
        }
    }

    private static int? EnablePrefetcher()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(PrefetchKey);
            return key?.GetValue("EnablePrefetcher") as int?;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// 링크·정션을 푼 실제 경로 (GetFinalPathNameByHandle). 열 수 없으면 null.
public static partial class RealPaths
{
    private const uint BackupSemantics = 0x02000000; // 디렉터리도 열기
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const int BufferChars = 32768;

    public static string? Resolve(string path)
    {
        using SafeFileHandle h = CreateFileW(path, 0, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (h.IsInvalid)
        {
            return null;
        }

        char[] buf = new char[BufferChars];
        uint n = GetFinalPathNameByHandleW(h, buf, (uint)buf.Length, 0);
        if (n == 0 || n >= buf.Length)
        {
            return null;
        }

        string s = new(buf, 0, (int)n);
        return s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + s[8..] : s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    /// ApplyHooks.RealPath 용: 풀 수 없으면 (아직 없는 경로 등) 정규화만.
    public static string Real(string path) => Resolve(path) ?? Path.GetFullPath(path);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] filePath, uint filePathSize, uint flags);
}

/// 볼륨이 놓인 물리 디스크의 처리량 (PhysicalDisk(N 문자들)\Disk Bytes/sec, PDH 영어 카운터 이름이라 한국어 Windows 에서도 동작).
/// 같은 물리 디스크의 다른 볼륨 활동도 보도록 인스턴스 이름의 "N C: D:" 에서 볼륨 문자가 속한 디스크를 찾아 합칩니다.
public static partial class DiskActivity
{
    /// 이보다 많이 읽고 쓰는 중이면 바쁜 것으로 봅니다 (바이트/초).
    public const double BusyBytesPerSecond = 4 << 20;
    private const uint FormatDouble = 0x200;
    private const uint MoreData = 0x800007D2;
    private const int SampleMs = 1000;
    private const long CacheMs = 2000;
    private static readonly PhysicalSampler Shared = new(SampleAll, () => Environment.TickCount64);

    // PDH_FMT_COUNTERVALUE_ITEM_W: 이름 포인터, 값 (상태 4 바이트, 정렬 4 바이트 더, double).
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public nint Name;
        public uint Status;
        public double Value;
    }

    /// letter ("D:") 가 놓인 물리 디스크(들)의 처리량 합 (바이트/초). 디스크를 찾지 못하거나 읽지 못하면 null.
    public static double? BytesPerSecond(string letter) => Shared.BytesPerSecond(letter);

    /// 읽지 못하면 바쁜 것으로 간주 (보수적: 재적용을 미룸).
    public static bool IsBusy(string letter) => BytesPerSecond(letter) is not { } bps || bps > BusyBytesPerSecond;

    /// 인스턴스 이름 ("0", "0 C: D:") → 값. letter 가 이름에 들어 있는 인스턴스의 합, 하나도 없으면 null.
    public static double? Sum(string letter, IReadOnlyDictionary<string, double> instances)
    {
        string key = letter.TrimEnd(':', '\\').ToUpperInvariant() + ":";
        double sum = 0;
        bool found = false;
        foreach ((string name, double value) in instances)
        {
            string[] parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1 && uint.TryParse(parts[0], out _) && parts.Skip(1).Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                sum += value;
                found = true;
            }
        }

        return found ? sum : null;
    }

    /// 모든 물리 디스크 인스턴스의 표본을 2 초 캐시해 같은 디스크를 묻는 여러 호출이 한 번만 잽니다.
    public sealed class PhysicalSampler(Func<IReadOnlyDictionary<string, double>?> sample, Func<long> nowMs)
    {
        private readonly object _gate = new();
        private long _at = long.MinValue;
        private IReadOnlyDictionary<string, double>? _last;

        public double? BytesPerSecond(string letter)
        {
            IReadOnlyDictionary<string, double>? instances;
            lock (_gate)
            {
                long now = nowMs();
                if (_at == long.MinValue || now - _at >= CacheMs)
                {
                    _last = sample();
                    _at = nowMs();
                }

                instances = _last;
            }

            return instances is null ? null : Sum(letter, instances);
        }
    }

    private static unsafe Dictionary<string, double>? SampleAll()
    {
        if (PdhOpenQueryW(null, 0, out nint query) != 0)
        {
            return null;
        }

        try
        {
            if (PdhAddEnglishCounterW(query, @"\PhysicalDisk(*)\Disk Bytes/sec", 0, out nint counter) != 0 || PdhCollectQueryData(query) != 0)
            {
                return null;
            }

            Thread.Sleep(SampleMs);
            uint size = 0;
            if (PdhCollectQueryData(query) != 0 || PdhGetFormattedCounterArrayW(counter, FormatDouble, ref size, out _, 0) != MoreData)
            {
                return null;
            }

            nint buffer = Marshal.AllocHGlobal((nint)size);
            try
            {
                if (PdhGetFormattedCounterArrayW(counter, FormatDouble, ref size, out uint count, buffer) != 0)
                {
                    return null;
                }

                var result = new Dictionary<string, double>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    CounterItem item = *((CounterItem*)buffer + i);
                    if (item.Status != 0)
                    {
                        return null; // 한 인스턴스라도 읽지 못하면 전체를 믿지 않음 (보수적)
                    }

                    result[Marshal.PtrToStringUni(item.Name) ?? string.Empty] = item.Value;
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            PdhCloseQuery(query);
        }
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint buffer);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}

/// 설정·계획 폴더가 관리자/SYSTEM 전용인지 (설계 2.1: 서비스는 SYSTEM 이라 일반 사용자가 쓸 수 있는 폴더의 계획을 믿으면 안 됨).
public static class AclCheck
{
    private static readonly SecurityIdentifier[] Trusted =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.CreatorOwnerSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // TrustedInstaller
    ];

    private const FileSystemRights WriteMask = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// 폴더를 바꾸거나 권한을 바꿀 수 있는 신뢰 밖 주체의 SID 목록 (비어 있으면 안전).
    public static IReadOnlyList<string> UntrustedWriters(string directory) => UntrustedWriters(new DirectoryInfo(directory).GetAccessControl());

    /// 파일(config.json) 쪽 같은 검사: 소유자와 쓰기 권한.
    public static IReadOnlyList<string> UntrustedFileWriters(string file) => UntrustedWriters(new FileInfo(file).GetAccessControl());

    public static IReadOnlyList<string> UntrustedWriters(FileSystemSecurity security)
    {
        var bad = new SortedSet<string>(StringComparer.Ordinal);
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !Trusted.Contains(owner))
        {
            bad.Add("owner:" + owner.Value);
        }

        foreach (FileSystemAccessRule r in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & WriteMask) != 0
                && r.IdentityReference is SecurityIdentifier sid && !Trusted.Contains(sid))
            {
                bad.Add(sid.Value);
            }
        }

        return bad.ToList();
    }
}

/// 전체 CPU 사용률 (GetSystemTimes): 호출 사이의 사용 시간 비율.
public sealed partial class CpuLoad
{
    private long _idle;
    private long _total;
    private bool _primed;

    /// 직전 호출 뒤의 CPU 사용률 (0..100). 처음 부르거나 읽지 못하면 null.
    public double? Percent()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user))
        {
            return null;
        }

        long total = kernel + user; // kernel 에는 idle 이 포함됨
        double? result = null;
        if (_primed && total > _total)
        {
            result = 100.0 * (1.0 - ((double)(idle - _idle) / (total - _total)));
        }

        (_idle, _total, _primed) = (idle, total, true);
        return result;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);
}

/// 전원 상태 (GetSystemPowerStatus).
public static partial class PowerProbe
{
    private const byte AcOnline = 1;
    private const byte NoBattery = 128;
    private const byte Unknown = 255;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    /// AC 연결, 배터리 없음, 알 수 없음은 true (방해될 일이 없음). 배터리면 남은 비율이 minBatteryPercent 이상일 때만 true.
    public static bool PowerOk(int minBatteryPercent)
    {
        if (!GetSystemPowerStatus(out PowerStatus s) || s.AcLineStatus is AcOnline or Unknown || (s.BatteryFlag & NoBattery) != 0)
        {
            return true;
        }

        return s.BatteryLifePercent != Unknown && s.BatteryLifePercent >= minBatteryPercent;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out PowerStatus status);
}
