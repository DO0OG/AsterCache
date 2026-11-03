using System.Security.AccessControl;
using System.Security.Principal;
using AsterCache.Core.Tier;

namespace AsterCache.Windows;

/// SSD 보조 저장의 실제 파일 시스템. 사본 복사는 1 MiB 버퍼와 낮은 I/O 우선순위 힌트로 합니다 (hint 가 null 이면 보통 우선순위).
/// 심볼릭 링크는 권한이 있어야 만들어집니다 (SYSTEM 서비스, 관리자, 또는 개발자 모드).
public sealed class TierFs(IoPriorityHint? hint = null) : ITierFs
{
    public const int BufferBytes = 1 << 20;

    public bool Exists(string path) => IsReparsePoint(path) || File.Exists(path) || Directory.Exists(path);

    public bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public string? LinkTarget(string path) => new FileInfo(path).LinkTarget;

    public FileStamp? Stat(string path)
    {
        var info = new FileInfo(path);
        FileSystemInfo target = info.LinkTarget is null ? info : info.ResolveLinkTarget(true) ?? info;
        return target is FileInfo { Exists: true } f ? new FileStamp(f.Length, f.LastWriteTimeUtc.Ticks) : null;
    }

    public byte[] ReadRange(string path, long offset, long length)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        f.Seek(offset, SeekOrigin.Begin);
        byte[] buf = new byte[length];
        int got = f.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        return got == buf.Length ? buf : buf[..got];
    }

    public void Copy(string source, string destination, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
            using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1, FileOptions.SequentialScan))
            {
                hint?.SetLow(input.SafeFileHandle);
                hint?.SetLow(output.SafeFileHandle);
                byte[] buf = new byte[BufferBytes];
                int n;
                while ((n = input.Read(buf, 0, buf.Length)) > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    output.Write(buf, 0, n);
                }

                ct.ThrowIfCancellationRequested();
                output.Flush(true);
            }
        }
        catch (OperationCanceledException)
        {
            File.Delete(destination); // 만들던 .part 를 남기지 않음
            throw;
        }

        // 시각을 먼저 맞추고 속성은 마지막에 (읽기 전용이 되면 시각을 못 고침): 원본이 읽기 전용이면 사본도 읽기 전용
        var info = new FileInfo(source);
        File.SetLastWriteTimeUtc(destination, info.LastWriteTimeUtc);
        File.SetCreationTimeUtc(destination, info.CreationTimeUtc);
        File.SetAttributes(destination, (info.Attributes & FileAttributes.ReadOnly) | FileAttributes.Archive);
    }

    public void Move(string from, string to, bool overwrite)
    {
        if (overwrite && File.Exists(to))
        {
            ClearReadOnly(to); // 읽기 전용 사본도 새 사본으로 바꿀 수 있게
        }

        File.Move(from, to, overwrite);
    }

    public void CreateSymlink(string link, string target) => File.CreateSymbolicLink(link, target);

    public void DeleteLink(string link)
    {
        if (!IsReparsePoint(link))
        {
            throw new IOException("not a link, nothing deleted");
        }

        File.Delete(link);
    }

    public void DeleteFile(string path)
    {
        ClearReadOnly(path);
        File.Delete(path);
    }

    private static void ClearReadOnly(string path)
    {
        if (File.Exists(path) && File.GetAttributes(path) is var a && (a & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
        }
    }

    public long FreeBytes(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).AvailableFreeSpace;

    public long TotalBytes(string path) => new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).TotalSize;

    public IReadOnlyList<string> FixedDrives() =>
        [.. DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady).Select(d => d.Name[..2].ToUpperInvariant())];

    // 새로 만드는 tier 폴더 (와 새로 만드는 바로 위 폴더, 예: C:\AsterCache) 만 관리자·SYSTEM 쓰기, 일반 사용자 읽기·실행 (게임은 사용자 계정으로 사본을 읽음) 권한으로 만듭니다.
    // 일반 사용자가 폴더를 바꿔치거나 정션을 심지 못하게 하려는 것입니다. 이미 있는 폴더의 권한은 건드리지 않습니다.
    public void EnsureRoot(string root)
    {
        if (Directory.Exists(root))
        {
            return;
        }

        string? parent = Path.GetDirectoryName(root);
        bool newParent = parent is not null && !Directory.Exists(parent) && Path.GetDirectoryName(parent) is not null;
        Protect(Directory.CreateDirectory(root));
        if (newParent)
        {
            Protect(new DirectoryInfo(parent!));
        }
    }

    private static void Protect(DirectoryInfo dir)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach ((WellKnownSidType sid, FileSystemRights rights) in new[]
        {
            (WellKnownSidType.LocalSystemSid, FileSystemRights.FullControl),
            (WellKnownSidType.BuiltinAdministratorsSid, FileSystemRights.FullControl),
            (WellKnownSidType.BuiltinUsersSid, FileSystemRights.ReadAndExecute),
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), rights, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }

        dir.SetAccessControl(security);
    }

    public void RemoveEmptyDirs(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string d in Directory.EnumerateDirectories(root).ToList())
        {
            if (IsReparsePoint(d))
            {
                continue;
            }

            RemoveEmptyDirs(d);
            if (!Directory.EnumerateFileSystemEntries(d).Any())
            {
                Directory.Delete(d);
            }
        }
    }
}
