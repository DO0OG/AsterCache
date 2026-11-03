using AsterCache.Core.Tier;

namespace AsterCache.Tests.Core;

/// 메모리 파일 시스템 가짜: 링크 권한 없이 SSD 보조 저장의 논리를 확인합니다. 경로는 Path.GetFullPath 로 맞추고 대소문자를 구분하지 않습니다.
internal sealed class MemTierFs : ITierFs
{
    private long _clock = 1_000_000;

    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, long> Mtimes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Links { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// 링크·정션이라고 답할 경로 (폴더 포함).
    public HashSet<string> Reparse { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long Free { get; set; } = 100L << 30;

    public List<string> Drives { get; } = [];

    public bool SymlinkDenied { get; set; }

    /// 파일 시스템을 바꾸는 호출 수 (아무것도 건드리지 않았음을 확인하는 데 씀).
    public int Mutations { get; private set; }

    public Action<string, string>? AfterCopy { get; set; }

    public static string N(string p) => Path.GetFullPath(p);

    public void Put(string path, byte[] data, long? mtime = null)
    {
        Files[N(path)] = data;
        Mtimes[N(path)] = mtime ?? ++_clock;
    }

    private string Resolve(string path)
    {
        string p = N(path);
        return Links.TryGetValue(p, out string? t) ? N(t) : p;
    }

    public bool Exists(string path) => Files.ContainsKey(N(path)) || Links.ContainsKey(N(path));

    public bool IsReparsePoint(string path) => Links.ContainsKey(N(path)) || Reparse.Contains(N(path));

    public string? LinkTarget(string path) => Links.GetValueOrDefault(N(path));

    public FileStamp? Stat(string path) => Files.TryGetValue(Resolve(path), out byte[]? d) ? new FileStamp(d.Length, Mtimes[Resolve(path)]) : null;

    public byte[] ReadRange(string path, long offset, long length)
    {
        byte[] d = Files[Resolve(path)];
        int n = (int)Math.Max(0, Math.Min(length, d.Length - offset));
        return d.AsSpan((int)offset, n).ToArray();
    }

    public void Copy(string source, string destination)
    {
        Mutations++;
        string src = Resolve(source);
        Files[N(destination)] = (byte[])Files[src].Clone();
        Mtimes[N(destination)] = Mtimes[src];
        AfterCopy?.Invoke(source, destination);
    }

    public void Move(string from, string to, bool overwrite)
    {
        Mutations++;
        string f = N(from), t = N(to);
        if (!Files.ContainsKey(f))
        {
            throw new FileNotFoundException(from);
        }

        if (!overwrite && Exists(t))
        {
            throw new IOException("exists");
        }

        Files[t] = Files[f];
        Mtimes[t] = Mtimes[f];
        Files.Remove(f);
        Mtimes.Remove(f);
    }

    public void CreateSymlink(string link, string target)
    {
        Mutations++;
        if (SymlinkDenied)
        {
            throw new UnauthorizedAccessException("A required privilege is not held by the client");
        }

        if (Exists(link))
        {
            throw new IOException("exists");
        }

        Links[N(link)] = target;
    }

    public void DeleteLink(string link)
    {
        Mutations++;
        if (!Links.Remove(N(link)))
        {
            throw new IOException("not a link, nothing deleted");
        }
    }

    public void DeleteFile(string path)
    {
        Mutations++;
        Files.Remove(N(path));
        Mtimes.Remove(N(path));
    }

    public long FreeBytes(string path) => Free;

    public IReadOnlyList<string> FixedDrives() => Drives;

    public void EnsureRoot(string root)
    {
    }

    public void RemoveEmptyDirs(string root)
    {
    }
}
