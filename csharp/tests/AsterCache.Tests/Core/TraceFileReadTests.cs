using System.Text;
using AsterCache.Core.Json;
using AsterCache.Core.Trace;
using Xunit;

namespace AsterCache.Tests.Core;

/// 파일을 조각으로 읽는 새 방식(JsonRead.ReadFile, TraceFile.Read)이 옛 방식(파일 전체를 문자열로 해석한 뒤 JsonDocument)과 같은 결과·같은 오류를 내는지 대조합니다.
public sealed class TraceFileReadTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory().FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private string Write(byte[] bytes)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] B(string text) => Encoding.UTF8.GetBytes(text);

    /// 옛 ReadFile 과 같은 동작 (BOM 허용, 엄격한 UTF-8, JsonRead.Parse).
    private static object? OldRead(byte[] bytes)
    {
        int skip = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        string text;
        try
        {
            text = new UTF8Encoding(false, true).GetString(bytes, skip, bytes.Length - skip);
        }
        catch (DecoderFallbackException e)
        {
            throw new PyValueException("invalid UTF-8: " + e.Message);
        }

        return JsonRead.Parse(text);
    }

    /// 옛 방식으로 트레이스를 읽은 결과: 행 목록(목록이 아니면 null)과, 행을 바꾸다 처음 던진 오류.
    private static (List<TraceEvent>? Rows, string? RowError) OldTrace(byte[] bytes)
    {
        object? data = OldRead(bytes);
        object? rows = data is JObject obj ? (obj.TryGetValue("ranges", out object? r) ? r : null) : data;
        if (rows is not List<object?> list)
        {
            return (null, null);
        }

        var events = new List<TraceEvent>();
        foreach (object? row in list)
        {
            try
            {
                events.Add(TraceEvents.FromJson(row));
            }
            catch (PyValueException e)
            {
                return (events, e.Message);
            }
        }

        return (events, null);
    }

    /// 던진 예외는 종류와 (PyValueException 이면) 첫 ':' 앞 문구까지만 비교합니다. 뒤의 상세 문구는 해석기마다 다릅니다.
    private static string Kind(Exception e) => e is PyValueException ? e.Message.Split(':')[0] : e.GetType().Name;

    private static string Same(object? a, object? b)
    {
        switch (a)
        {
            case JObject x when b is JObject y:
                if (x.Count != y.Count)
                {
                    return "count";
                }

                foreach ((KeyValuePair<string, object?> p, KeyValuePair<string, object?> q) in x.Zip(y))
                {
                    if (p.Key != q.Key || Same(p.Value, q.Value).Length > 0)
                    {
                        return p.Key + "/" + q.Key;
                    }
                }

                return string.Empty;
            case List<object?> l when b is List<object?> m:
                if (l.Count != m.Count)
                {
                    return "count";
                }

                for (int i = 0; i < l.Count; i++)
                {
                    if (Same(l[i], m[i]) is { Length: > 0 } diff)
                    {
                        return "[" + i + "]" + diff;
                    }
                }

                return string.Empty;
            case double d when b is double e:
                return BitConverter.DoubleToInt64Bits(d) == BitConverter.DoubleToInt64Bits(e) ? string.Empty : "double";
            case null:
                return b is null ? string.Empty : "null";
            default:
                return a.GetType() == b?.GetType() && a.Equals(b) ? string.Empty : "value";
        }
    }

    private void AssertReadSame(byte[] bytes, string label)
    {
        object? expected = null, actual = null;
        string? expectedError = null, actualError = null;
        try
        {
            expected = OldRead(bytes);
        }
        catch (Exception e)
        {
            expectedError = Kind(e);
        }

        string path = Write(bytes);
        try
        {
            actual = JsonRead.ReadFile(path);
        }
        catch (Exception e)
        {
            actualError = Kind(e);
        }

        Assert.True(expectedError == actualError, $"{label}: old={expectedError ?? "ok"} new={actualError ?? "ok"}");
        if (expectedError is null)
        {
            Assert.True(Same(expected, actual).Length == 0, $"{label}: different value ({Same(expected, actual)})");
        }

        File.Delete(path);
    }

    private void AssertTraceSame(byte[] bytes, string label)
    {
        (List<TraceEvent>? Rows, string? RowError) expected = default;
        (List<TraceEvent>? Rows, string? RowError) actual = default;
        string? expectedError = null, actualError = null;
        try
        {
            expected = OldTrace(bytes);
        }
        catch (Exception e)
        {
            expectedError = Kind(e);
        }

        string path = Write(bytes);
        try
        {
            TraceFileRows read = TraceFile.Read(path);
            actual = (read.Rows, read.RowError?.Message);
        }
        catch (Exception e)
        {
            actualError = Kind(e);
        }

        Assert.True(expectedError == actualError, $"{label}: old={expectedError ?? "ok"} new={actualError ?? "ok"}");
        if (expectedError is null)
        {
            Assert.True((expected.Rows is null) == (actual.Rows is null), $"{label}: list or not");
            Assert.True(expected.RowError == actual.RowError, $"{label}: row error old={expected.RowError} new={actual.RowError}");
            if (expected.Rows is not null)
            {
                Assert.True(expected.Rows.SequenceEqual(actual.Rows!), $"{label}: rows differ");
            }
        }

        File.Delete(path);
    }

    public static TheoryData<string> Documents() =>
    [
        "null", "true", "5", "-0", "1.0", "1E5", "1e999", "-1e999", "99999999999999999999", "-9223372036854775808", "9223372036854775808",
        "\"text\"", "\"\\u00e9\\n\\\"\"", "\"\\ud800\"", "\"\\udc00x\"", "\"\\ud83d\\ude00\"", "[]", "{}", "[1,2,3]", "{\"a\":1,\"a\":2,\"b\":[null,true,false]}",
        "{\"a\":{\"b\":{\"c\":[1,[2,[3]]]}}}", "[1,]", "{\"a\":1,}", "[1 2]", "[", "{", "{\"a\"}", "", " ", "\n\t ", "nul", "NaN", "Infinity", "[1] x", "[1][2]",
        "/* c */ [1]", "// c\n[1]", "{'a':1}", "[01]", "[1.]", "[.5]", "[+1]", "[1e]", "\"\u0001\"", "\"tab\there\"", "[\"a\"]\n\n", "\u00a0[1]",
        "[1e400, 99999999999999999999]", "[99999999999999999999, [", "{\"x\":99999999999999999999,\"y\":\"\\ud800\"}", "[\"\\ud800\", 1e999]",
    ];

    [Theory]
    [MemberData(nameof(Documents))]
    public void ReadFileMatchesOldReadOnDocuments(string text)
    {
        AssertReadSame(B(text), text);
        AssertReadSame([0xEF, 0xBB, 0xBF, .. B(text)], "bom " + text);
        AssertTraceSame(B(text), text);
    }

    [Fact]
    public void ReadFileMatchesOldReadOnDamagedBytes()
    {
        byte[][] cases =
        [
            [], [0xEF, 0xBB, 0xBF], [0xEF, 0xBB], [0xEF, 0xBB, 0xBF, 0xEF, 0xBB, 0xBF, (byte)'1'],
            [(byte)'"', 0xC3, (byte)'"'], [(byte)'"', 0xC3, 0x28, (byte)'"'], [(byte)'"', 0xC0, 0xAF, (byte)'"'], [(byte)'"', 0xED, 0xA0, 0x80, (byte)'"'],
            [(byte)'"', 0xF4, 0x90, 0x80, 0x80, (byte)'"'], [(byte)'"', 0xE2, 0x82], [(byte)'"', 0x80, (byte)'"'], [(byte)'[', 0xFF, (byte)']'],
            [(byte)'1', 0xC3], [(byte)'{', (byte)'"', (byte)'a', (byte)'"', (byte)':', (byte)'1', (byte)'}', (byte)'x', 0xC3],
            [(byte)'[', (byte)'1', (byte)',', (byte)']', 0xFF],
        ];
        foreach (byte[] bytes in cases)
        {
            AssertReadSame(bytes, BitConverter.ToString(bytes));
            AssertTraceSame(bytes, BitConverter.ToString(bytes));
        }
    }

    [Fact]
    public void ReadFileMatchesOldReadAcrossChunkBoundaries()
    {
        // 한 조각(64 KiB)을 넘는 글: 여러 바이트 문자가 조각 경계에 걸치는 모든 위치, 조각보다 긴 문자열·숫자
        string unit = "가나다\\u00e9\\n😀x";
        for (int pad = 0; pad < 12; pad++)
        {
            var text = new StringBuilder("[\"").Append('a', pad);
            while (text.Length < 65536 * 2 + 40)
            {
                text.Append(unit);
            }

            text.Append("\",{\"키\":[1,2.5,\"값\"]}]");
            AssertReadSame(B(text.ToString()), "pad " + pad);
        }

        AssertReadSame(B("[\"" + new string('b', 300_000) + "\"]"), "long string");
        AssertReadSame(B("[" + new string('1', 200_000) + "]"), "long integer");
        AssertReadSame(B("[" + new string('1', 70_000) + ".5]"), "long real");
        AssertReadSame(B(string.Concat(Enumerable.Repeat("[", 512)) + string.Concat(Enumerable.Repeat("]", 512))), "depth 512");
        AssertReadSame(B(string.Concat(Enumerable.Repeat("[", 513)) + string.Concat(Enumerable.Repeat("]", 513))), "depth 513");
        AssertReadSame([.. B("[\"" + new string('c', 70_000) + "\"]"), 0xC3], "invalid byte at the end of a long file");
        AssertReadSame([.. B("[\"" + new string('c', 70_000) + "\" x]")], "syntax error after a chunk");
        AssertReadSame([.. B("[99999999999999999999,\"" + new string('c', 70_000) + "\" x]")], "number error then syntax error");
        AssertReadSame([.. B("[x,\"" + new string('c', 70_000) + "\""), 0xC3, .. B("]")], "syntax error then invalid UTF-8 in a later chunk");
    }

    [Fact]
    public void ReadFileKeepsErrorPriority()
    {
        string path = Write(B("[99999999999999999999, \"\\ud800\", ]"));
        Assert.StartsWith("invalid JSON", Assert.Throws<PyValueException>(() => JsonRead.ReadFile(path)).Message);
        path = Write(B("[99999999999999999999, \"\\ud800\"]"));
        Assert.StartsWith("integer out of int64 range", Assert.Throws<PyValueException>(() => JsonRead.ReadFile(path)).Message);
        path = Write([.. B("[1, ]"), 0xFF]);
        Assert.StartsWith("invalid UTF-8", Assert.Throws<PyValueException>(() => JsonRead.ReadFile(path)).Message);
    }

    private static string Pick(Random rng, params string[] options) => options[rng.Next(options.Length)];

    private static string RandomRow(Random rng)
    {
        if (rng.Next(12) == 0)
        {
            return Pick(rng, "5", "[]", "null", "\"x\"", "true", "[{\"path\":\"a\"}]");
        }

        var parts = new List<string>();
        int count = rng.Next(3, 11);
        for (int i = 0; i < count; i++)
        {
            parts.Add(rng.Next(8) switch
            {
                0 => "\"path\":" + Pick(rng, "\"a.bin\"", "\"D:\\\\g\\\\b.bin\"", "\"p\\u00e9\"", "\"한글\"", "5", "null", "[]", "true", "\"\"", "\"a.bin\"", "\"a.bin\"", "\"a.bin\""),
                1 => "\"offset\":" + Pick(rng, "0", "65536", "-1", "1.5", "1e3", "null", "\"5\"", "99999999999999999999", "9223372036854775807", "true", "0", "65536"),
                2 => "\"length\":" + Pick(rng, "0", "65536", "-1", "1.5", "null", "\"5\"", "9223372036854775807", "65536", "65536", "131072"),
                3 => "\"cost\":" + Pick(rng, "8.0", "3", "null", "\"x\"", "1e400", "-2", "[]", "0.5", "8.0"),
                4 => "\"t_ms\":" + Pick(rng, "0", "1500", "-1", "null", "1.5", "\"x\"", "true", "471"),
                5 => "\"" + Pick(rng, "observed", "overflow") + "\":" + Pick(rng, "true", "false", "null", "1", "\"true\""),
                6 => "\"range_group\":" + Pick(rng, "0", "3", "-1", "null", "true", "1.0"),
                _ => "\"" + Pick(rng, "extra", "pa\\u0074h", "x") + "\":" + Pick(rng, "{\"a\":[1,2,{\"b\":null}]}", "1e999", "\"\\ud800\"", "[1.5,2]", "99999999999999999999", "null", "\"s\""),
            });
        }

        if (rng.Next(3) != 0)
        {
            // 필수 키를 대개 갖춰 정상 행이 많게 함 (뒤에 오면 앞의 같은 키를 덮음)
            parts.Add("\"path\":\"a.bin\"");
            parts.Add("\"offset\":" + rng.Next(100));
            parts.Add("\"length\":" + rng.Next(1, 100));
        }

        return "{" + string.Join(",", parts.OrderBy(_ => rng.Next())) + "}";
    }

    [Fact]
    public void TraceFileReadMatchesOldReadOnRandomRows()
    {
        var rng = new Random(20261121);
        for (int n = 0; n < 4000; n++)
        {
            string rows = string.Join(",", Enumerable.Range(0, rng.Next(0, 5)).Select(_ => RandomRow(rng)));
            string text = rng.Next(10) switch
            {
                0 => "{\"ranges\":[" + rows + "]}",
                1 => "{\"x\":1,\"ranges\":[" + rows + "],\"y\":[2]}",
                2 => "{\"ranges\":[" + rows + "],\"ranges\":" + Pick(rng, "5", "[]", "null") + "}",
                3 => "{\"ranges\":5,\"ranges\":[" + rows + "]}",
                4 => "{\"other\":[" + rows + "]}",
                5 => "[" + rows + "] " + Pick(rng, "x", "1", "", ""),
                6 => "[" + rows + (rng.Next(2) == 0 ? "," : string.Empty),
                _ => "[" + rows + "]",
            };
            byte[] bytes = B(text);
            if (rng.Next(25) == 0)
            {
                bytes[rng.Next(bytes.Length)] = 0xFF;
            }

            AssertTraceSame(bytes, text);
            AssertReadSame(bytes, text);
        }
    }

    [Fact]
    public void TraceFileReadMatchesOldReadOnLargeFileAndSharesPaths()
    {
        var rng = new Random(7);
        var text = new StringBuilder("[");
        string[] files = [.. Enumerable.Range(0, 40).Select(i => "D:\\\\게임\\\\폴더" + i + "\\\\f" + (i * 7919) + ".bin")];
        for (int i = 0; i < 40_000; i++)
        {
            text.Append(i == 0 ? "" : ", ").Append("{\"path\": \"").Append(files[rng.Next(100) < 70 ? i / 1000 : rng.Next(files.Length)])
                .Append("\", \"offset\": ").Append(i * 65536L).Append(", \"length\": 65536, \"cost\": 8.0, \"t_ms\": ").Append(i / 3).Append('}');
        }

        text.Append(']');
        byte[] bytes = B(text.ToString());
        Assert.True(bytes.Length > 3 * 1024 * 1024);
        AssertTraceSame(bytes, "large");
        AssertReadSame(bytes, "large");

        string path = Write(bytes);
        List<TraceEvent> rows = TraceFile.Read(path).Rows!;
        Assert.Equal(40_000, rows.Count);
        var byValue = new Dictionary<string, string>();
        foreach (TraceEvent e in rows)
        {
            if (byValue.TryGetValue(e.Path, out string? first))
            {
                Assert.Same(first, e.Path);
            }
            else
            {
                byValue[e.Path] = e.Path;
            }
        }

        Assert.True(byValue.Count <= files.Length);
    }

    [Fact]
    public void LoadRangesHandlesRowsBeforeABadRowThenFails()
    {
        string root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(root);
        string a = Path.Combine(root, "a.bin");
        string b = Path.Combine(root, "b.bin");
        File.WriteAllBytes(a, new byte[100]);
        File.WriteAllBytes(b, new byte[100]);
        string trace = Write(B(System.Text.Json.JsonSerializer.Serialize(new object[]
        {
            new { path = a, offset = 0, length = 10 },
            new { path = b, offset = 0, length = 10, cost = "x" },
            new { path = a, offset = 0, length = 10 },
        })));

        var seen = new List<string>();
        string? Resolve(string p)
        {
            seen.Add(p);
            return p;
        }

        Assert.Equal("bad cost", Assert.Throws<PyValueException>(() => TraceLoader.LoadRanges(trace, root, Resolve)).Message);
        Assert.Equal([root, a], seen); // 잘못된 행 앞의 행만 처리하고, 잘못된 행의 경로는 풀지 않음
        Assert.Throws<DirectoryNotFoundException>(() => TraceLoader.LoadRanges(trace, Path.Combine(_dir, "nope")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => TraceLoader.LoadRanges(trace, root, cancellationToken: cancelled.Token));
    }
}
