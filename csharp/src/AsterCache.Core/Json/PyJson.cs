using System.Collections;
using System.Globalization;
using System.Text;

namespace AsterCache.Core.Json;

/// Python json.dumps 와 바이트가 같은 쓰기. 값은 null, bool, int/long, double, string, JObject, 나열 가능한 목록.
/// 줄바꿈은 항상 LF 입니다. Windows 텍스트 모드 파일은 <see cref="ToCrlf"/> 를 거칩니다.
public static class PyJson
{
    public static readonly UTF8Encoding Utf8 = new(false);

    public static string Dumps(object? value, int? indent = null, bool sortKeys = false, bool ensureAscii = true, bool compact = false)
    {
        var sb = new StringBuilder();
        Write(sb, value, new Options(indent, sortKeys, ensureAscii, compact), 0);
        return sb.ToString();
    }

    /// Windows 텍스트 모드 쓰기와 같은 줄바꿈 변환. JSON 문자열 안에는 날 줄바꿈이 없으므로 안전합니다.
    public static string ToCrlf(string text) => text.Replace("\n", "\r\n", StringComparison.Ordinal);

    private sealed record Options(int? Indent, bool SortKeys, bool EnsureAscii, bool Compact);

    private static void Write(StringBuilder sb, object? value, Options o, int level)
    {
        switch (value)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case int i:
                sb.Append(i.ToString(CultureInfo.InvariantCulture));
                break;
            case long l:
                sb.Append(l.ToString(CultureInfo.InvariantCulture));
                break;
            case double d:
                sb.Append(PyFloat.Repr(d));
                break;
            case string s:
                WriteString(sb, s, o.EnsureAscii);
                break;
            case JObject obj:
                WriteObject(sb, obj, o, level);
                break;
            case IEnumerable list:
                WriteArray(sb, list.Cast<object?>().ToList(), o, level);
                break;
            default:
                throw new PyValueException("Object of type " + value.GetType().Name + " is not JSON serializable");
        }
    }

    private static void WriteArray(StringBuilder sb, List<object?> items, Options o, int level)
    {
        if (items.Count == 0)
        {
            sb.Append("[]");
            return;
        }

        string itemSep = ItemSeparator(o);
        sb.Append('[');
        if (o.Indent is not null)
        {
            string nl = "\n" + new string(' ', o.Indent.Value * (level + 1));
            sb.Append(nl);
            itemSep += nl;
        }

        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(itemSep);
            }

            Write(sb, items[i], o, level + 1);
        }

        CloseContainer(sb, o, level, ']');
    }

    private static void WriteObject(StringBuilder sb, JObject obj, Options o, int level)
    {
        if (obj.Count == 0)
        {
            sb.Append("{}");
            return;
        }

        var pairs = obj.ToList();
        if (o.SortKeys)
        {
            pairs.Sort((a, b) => CodePointComparer.Compare(a.Key, b.Key));
        }

        string itemSep = ItemSeparator(o);
        string keySep = o.Compact ? ":" : ": ";
        sb.Append('{');
        if (o.Indent is not null)
        {
            string nl = "\n" + new string(' ', o.Indent.Value * (level + 1));
            sb.Append(nl);
            itemSep += nl;
        }

        for (int i = 0; i < pairs.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(itemSep);
            }

            WriteString(sb, pairs[i].Key, o.EnsureAscii);
            sb.Append(keySep);
            Write(sb, pairs[i].Value, o, level + 1);
        }

        CloseContainer(sb, o, level, '}');
    }

    private static string ItemSeparator(Options o) => o.Compact || o.Indent is not null ? "," : ", ";

    private static void CloseContainer(StringBuilder sb, Options o, int level, char close)
    {
        if (o.Indent is not null)
        {
            sb.Append('\n').Append(' ', o.Indent.Value * level);
        }

        sb.Append(close);
    }

    private static void WriteString(StringBuilder sb, string s, bool ensureAscii)
    {
        sb.Append('"');
        foreach (char c in s)
        {
            switch (c)
            {
                case '"':
                    sb.Append("\\\"");
                    break;
                case '\\':
                    sb.Append("\\\\");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                default:
                    if (c < 0x20 || (ensureAscii && c > 0x7E))
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }
}
