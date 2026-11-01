using System.Globalization;
using System.Text;

namespace AsterCache.Cli;

/// pathlib.PureWindowsPath 의 str(): 슬래시를 역슬래시로, 겹친 구분자·"." 와 끝 구분자를 없앱니다 (절대 경로로 만들지는 않음).
internal static class PathText
{
    public static string Str(string p)
    {
        string s = p.Replace('/', '\\');
        string drive = "";
        string rest = s;
        if (s.StartsWith(@"\\", StringComparison.Ordinal) && !s.StartsWith(@"\\\", StringComparison.Ordinal))
        {
            string[] parts = s[2..].Split('\\', 3);
            if (parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0)
            {
                drive = @"\\" + parts[0] + "\\" + parts[1];
                rest = parts.Length == 3 ? "\\" + parts[2] : "\\";
            }
        }
        else if (s.Length >= 2 && s[1] == ':')
        {
            drive = s[..2];
            rest = s[2..];
        }

        string root = rest.StartsWith('\\') ? "\\" : "";
        string body = string.Join('\\', rest.Split('\\', StringSplitOptions.RemoveEmptyEntries).Where(x => x != "."));
        string text = drive + root + body;
        return text.Length == 0 ? "." : text;
    }
}

/// Python repr(str) 와 같은 따옴표·이스케이프 (오류 문구용).
internal static class PyRepr
{
    public static string Str(string s)
    {
        char quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder().Append(quote);
        foreach (char c in s)
        {
            if (c == quote || c == '\\')
            {
                sb.Append('\\').Append(c);
            }
            else if (c == '\n')
            {
                sb.Append("\\n");
            }
            else if (c == '\r')
            {
                sb.Append("\\r");
            }
            else if (c == '\t')
            {
                sb.Append("\\t");
            }
            else if (c < 0x20 || c == 0x7F)
            {
                sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.Append(quote).ToString();
    }
}
