using System.Text;
using System.Text.Json;

namespace AsterCache.Core.Json;

/// Python json.loads 에 가까운 읽기. 결과: null, bool, long, double, string, JObject, List&lt;object?&gt;.
/// 정수는 Python 의 type(x) is int 처럼 엄격합니다: 소수점·지수가 있는 숫자는 double, int64 범위 밖 정수는 거부합니다.
public static class JsonRead
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonDocumentOptions Options = new() { MaxDepth = 512 };

    public static object? Parse(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text, Options);
            return Convert(doc.RootElement);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            throw new PyValueException("invalid JSON: " + e.Message);
        }
    }

    /// UTF-8 (BOM 허용, Python utf-8-sig) 파일을 읽습니다.
    public static object? ReadFile(string path)
    {
        byte[] bytes;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            bytes = new byte[fs.Length];
            fs.ReadExactly(bytes);
        }

        int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes, skip, bytes.Length - skip);
        }
        catch (DecoderFallbackException e)
        {
            throw new PyValueException("invalid UTF-8: " + e.Message);
        }

        return Parse(text);
    }

    private static object? Convert(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var obj = new JObject();
                foreach (JsonProperty p in e.EnumerateObject())
                {
                    obj.Add(p.Name, Convert(p.Value));
                }

                return obj;
            case JsonValueKind.Array:
                var list = new List<object?>();
                foreach (JsonElement item in e.EnumerateArray())
                {
                    list.Add(Convert(item));
                }

                return list;
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Number:
                string raw = e.GetRawText();
                if (raw.AsSpan().IndexOfAny('.', 'e', 'E') >= 0)
                {
                    return e.GetDouble();
                }

                if (e.TryGetInt64(out long l))
                {
                    return l;
                }

                throw new PyValueException("integer out of int64 range: " + raw);
            default:
                return null;
        }
    }
}
