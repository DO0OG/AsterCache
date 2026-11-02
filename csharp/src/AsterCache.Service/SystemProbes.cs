using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Service;

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

/// 볼륨의 디스크 처리량 (LogicalDisk(X:)\Disk Bytes/sec, PDH 영어 카운터 이름이라 한국어 Windows 에서도 동작).
public static partial class DiskActivity
{
    /// 이보다 많이 읽고 쓰는 중이면 바쁜 것으로 봅니다 (바이트/초).
    public const double BusyBytesPerSecond = 4 << 20;
    private const uint FormatDouble = 0x200;

    [StructLayout(LayoutKind.Sequential)]
    private struct CounterValue
    {
        public uint Status;
        public double Value;
    }

    /// letter ("D:") 의 처리량을 sampleMs 동안 재서 바이트/초로. 읽지 못하면 null.
    public static double? BytesPerSecond(string letter, int sampleMs = 1000)
    {
        if (PdhOpenQueryW(null, 0, out nint query) != 0)
        {
            return null;
        }

        try
        {
            string path = $@"\LogicalDisk({letter.TrimEnd(':', '\\')}:)\Disk Bytes/sec";
            if (PdhAddEnglishCounterW(query, path, 0, out nint counter) != 0 || PdhCollectQueryData(query) != 0)
            {
                return null;
            }

            Thread.Sleep(sampleMs);
            if (PdhCollectQueryData(query) != 0 || PdhGetFormattedCounterValue(counter, FormatDouble, out _, out CounterValue v) != 0 || v.Status != 0)
            {
                return null;
            }

            return v.Value;
        }
        finally
        {
            PdhCloseQuery(query);
        }
    }

    /// 읽지 못하면 바쁜 것으로 간주 (보수적: 재적용을 미룸).
    public static bool IsBusy(string letter) => BytesPerSecond(letter) is not { } bps || bps > BusyBytesPerSecond;

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhGetFormattedCounterValue(nint counter, uint format, out uint type, out CounterValue value);

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
