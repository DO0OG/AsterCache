using System.Text;
using System.Text.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Trace;
using Xunit.Sdk;

namespace AsterCache.Tests.Core;

/// 픽스처 읽기 도우미 (tests/fixtures 는 빌드 출력의 fixtures 폴더로 복사됩니다).
internal static class PlanFixtures
{
    public static string PathOf(string relative) => Path.Combine(AppContext.BaseDirectory, "fixtures", relative);

    public static byte[] Bytes(string relative) => File.ReadAllBytes(PathOf(relative));

    public static JsonDocument Doc(string relative) => JsonDocument.Parse(Bytes(relative));

    /// 비교 전 CRLF 를 LF 로 맞춥니다.
    public static string Lf(byte[] bytes) => Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);

    public static double FromBits(string hex) => BitConverter.Int64BitsToDouble(Convert.ToInt64(hex, 16));

    public static List<TraceEvent> Session(JsonElement events) =>
        events.EnumerateArray().Select(e => new TraceEvent(e.GetProperty("path").GetString()!, e.GetProperty("offset").GetInt64(),
            e.GetProperty("length").GetInt64(), e.GetProperty("cost").GetDouble())).ToList();

    public static List<IReadOnlyList<TraceEvent>> Sessions(JsonElement sessions) =>
        sessions.EnumerateArray().Select(s => (IReadOnlyList<TraceEvent>)Session(s)).ToList();

    public static List<Extent> ExtentList(JsonElement list) =>
        list.EnumerateArray().Select(e => new Extent(e[0].GetInt64(), e[1].GetInt64(), e[2].GetInt64())).ToList();

    public static Dictionary<string, IReadOnlyList<Extent>?> Extents(JsonElement map) =>
        map.EnumerateObject().ToDictionary(p => p.Name,
            p => p.Value.ValueKind == JsonValueKind.Null ? null : (IReadOnlyList<Extent>)ExtentList(p.Value), StringComparer.Ordinal);

    public static Dictionary<string, long> Sizes(JsonElement map) =>
        map.ValueKind == JsonValueKind.Null
            ? []
            : map.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt64(), StringComparer.Ordinal);

    public static long? NullableLong(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetInt64();

    public static List<Unit> Units(JsonElement list) =>
        list.EnumerateArray().Select(u => new Unit(u[0].GetString()!, u[1].GetInt64(), u[2].GetInt64())).ToList();

    public static List<BlockKey> Keys(JsonElement list) =>
        list.EnumerateArray().Select(k => new BlockKey(k[0].GetString()!, k[1].GetInt64())).ToList();

    /// 두 텍스트가 같음을 확인하고, 다르면 처음 다른 위치 둘레만 보여 줍니다.
    public static void SameText(string expected, string actual)
    {
        if (expected == actual)
        {
            return;
        }

        int i = 0;
        while (i < expected.Length && i < actual.Length && expected[i] == actual[i])
        {
            i++;
        }

        string Around(string s) => s.Substring(Math.Max(0, i - 60), Math.Min(120, s.Length - Math.Max(0, i - 60)));
        throw new XunitException($"texts differ at {i} (lengths {expected.Length} / {actual.Length}): expected ...{Around(expected)}... actual ...{Around(actual)}...");
    }

    /// 실패 메시지에 사례 이름을 붙입니다.
    public static void With(string context, Action check)
    {
        try
        {
            check();
        }
        catch (XunitException e)
        {
            throw new XunitException($"[{context}] {e.Message}");
        }
    }
}
