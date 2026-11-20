using System.Text.Json;

namespace AsterCache.Core.Json;

/// Python json.loads 에 가까운 읽기. 결과: null, bool, long, double, string, JObject, List&lt;object?&gt;.
/// 정수는 Python 의 type(x) is int 처럼 엄격합니다: 소수점·지수가 있는 숫자는 double, int64 범위 밖 정수는 거부합니다.
public static class JsonRead
{
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

    /// UTF-8 (BOM 허용, Python utf-8-sig) 파일을 읽습니다. 파일을 조각으로 읽어 바로 값으로 만듭니다 (<see cref="JsonFileReader"/>).
    public static object? ReadFile(string path)
    {
        var s = new JsonFileReader(path);
        try
        {
            object? value = null;
            JsonException? syntaxError = null;
            try
            {
                s.ReadNext();
                value = ReadValue(ref s);
                s.ReadNext(); // 끝 뒤에 다른 내용이 없는지 확인
            }
            catch (JsonException e)
            {
                syntaxError = e;
            }

            s.Finish(syntaxError);
            return value;
        }
        finally
        {
            s.Dispose();
        }
    }

    /// 지금 토큰에서 시작하는 값 하나를 읽어 <see cref="Convert"/> 와 같은 모양으로 만들고, 그 값의 마지막 토큰에서 멈춥니다.
    internal static object? ReadValue(ref JsonFileReader s)
    {
        switch (s.Reader.TokenType)
        {
            case JsonTokenType.StartObject:
                var obj = new JObject();
                while (s.ReadNext() && s.Reader.TokenType == JsonTokenType.PropertyName)
                {
                    string name = s.GetString();
                    s.ReadNext();
                    obj.Add(name, ReadValue(ref s));
                }

                return obj;
            case JsonTokenType.StartArray:
                var list = new List<object?>();
                while (s.ReadNext() && s.Reader.TokenType != JsonTokenType.EndArray)
                {
                    list.Add(ReadValue(ref s));
                }

                return list;
            case JsonTokenType.String:
                return s.GetString();
            case JsonTokenType.True:
                return true;
            case JsonTokenType.False:
                return false;
            case JsonTokenType.Number:
                if (!s.ReadNumber(out bool isDouble, out long integer, out double real))
                {
                    return null;
                }

                return isDouble ? (object)real : integer; // 두 갈래의 형을 같게 두면 long 이 double 로 바뀜
            default:
                return null;
        }
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
