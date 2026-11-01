namespace AsterCache.Tracer;

/// Python ntpath.normpath / normcase 와 같은 결과 (프로세스 모니터 경로의 루트 접두 비교용).
public static class NtPath
{
    public static string NormCase(string path) => path.Replace('/', '\\').ToLowerInvariant();

    public static string NormPath(string path)
    {
        path = path.Replace('/', '\\');
        (string prefix, string rest) = SplitDrive(path);
        if (prefix.Length == 0)
        {
            int n = rest.Length - rest.TrimStart('\\').Length;
            prefix = rest[..n];
            rest = rest[n..];
        }
        else if (rest.StartsWith('\\'))
        {
            prefix += "\\";
            rest = rest.TrimStart('\\');
        }

        var comps = new List<string>(rest.Split('\\'));
        int i = 0;
        while (i < comps.Count)
        {
            if (comps[i].Length == 0 || comps[i] == ".")
            {
                comps.RemoveAt(i);
            }
            else if (comps[i] == "..")
            {
                if (i > 0 && comps[i - 1] != "..")
                {
                    comps.RemoveRange(i - 1, 2);
                    i--;
                }
                else if (i == 0 && prefix.EndsWith('\\'))
                {
                    comps.RemoveAt(i);
                }
                else
                {
                    i++;
                }
            }
            else
            {
                i++;
            }
        }

        if (prefix.Length == 0 && comps.Count == 0)
        {
            comps.Add(".");
        }

        return prefix + string.Join('\\', comps);
    }

    /// ntpath.splitdrive: 드라이브 문자 또는 \\서버\공유.
    private static (string Drive, string Tail) SplitDrive(string p)
    {
        if (p.Length >= 2 && p[0] == '\\' && p[1] == '\\' && (p.Length == 2 || p[2] != '\\'))
        {
            int index = p.IndexOf('\\', 2);
            if (index == -1)
            {
                return ("", p);
            }

            int index2 = p.IndexOf('\\', index + 1);
            if (index2 == index + 1)
            {
                return ("", p);
            }

            if (index2 == -1)
            {
                index2 = p.Length;
            }

            return (p[..index2], p[index2..]);
        }

        return p.Length >= 2 && p[1] == ':' ? (p[..2], p[2..]) : ("", p);
    }

    /// Python: canon == prefix or canon.startswith(prefix + '\\'). 둘 다 NormCase(NormPath(...)) 를 거친 값.
    /// 드라이브 루트 접두 (D:\) 는 Python 과 똑같이 아무것도 맞추지 못합니다 (접두 + 역슬래시 = D:\\).
    public static bool IsUnder(string canonPath, string canonPrefix) =>
        canonPath == canonPrefix || canonPath.StartsWith(canonPrefix + "\\", StringComparison.Ordinal);
}
