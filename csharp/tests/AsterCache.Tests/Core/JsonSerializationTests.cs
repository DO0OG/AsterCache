using System.Text;
using System.Text.Json;
using AsterCache.Core.Json;
using Xunit;

namespace AsterCache.Tests.Core;

public sealed class JsonSerializationTests
{
    [Fact]
    public void IndentOneMatchesPython()
    {
        var value = new JObject
        {
            { "a", new List<object?> { 1L, 2.5, new JObject { { "b", null }, { "c", new List<object?>() } } } },
            { "d", new JObject() },
            { "e", "x\u00e9" },
        };
        const string expected = "{\n \"a\": [\n  1,\n  2.5,\n  {\n   \"b\": null,\n   \"c\": []\n  }\n ],\n \"d\": {},\n \"e\": \"x\\u00e9\"\n}";

        Assert.Equal(expected, PyJson.Dumps(value, indent: 1));
        Assert.Equal("[\n [],\n {}\n]", PyJson.Dumps(new List<object?> { new List<object?>(), new JObject() }, indent: 1));
        Assert.Equal("[]", PyJson.Dumps(new List<object?>()));
        Assert.Equal("{}", PyJson.Dumps(new JObject()));
    }

    [Fact]
    public void DefaultCompactAndSortedSeparators()
    {
        var value = new JObject
        {
            { "b", 1L },
            { "a", new JObject { { "z", 1L }, { "y", new List<object?> { 1.0, 1e-05 } } } },
            { "\u00e9", true },
        };

        Assert.Equal("{\"b\": 1, \"a\": {\"z\": 1, \"y\": [1.0, 1e-05]}, \"\\u00e9\": true}", PyJson.Dumps(value));
        Assert.Equal("{\"a\": {\"y\": [1.0, 1e-05], \"z\": 1}, \"b\": 1, \"\\u00e9\": true}", PyJson.Dumps(value, sortKeys: true));
        Assert.Equal("{\"k\":[1,{\"x\":2.0}]}",
            PyJson.Dumps(new JObject { { "k", new List<object?> { 1L, new JObject { { "x", 2.0 } } } } }, compact: true));
    }

    [Fact]
    public void StringEscapesAreLowercaseUtf16()
    {
        const string s = "a\"b\\c\n\r\t\b\f\u0001\u007f\u00e9\uffff\U0001F600";

        Assert.Equal("\"a\\\"b\\\\c\\n\\r\\t\\b\\f\\u0001\\u007f\\u00e9\\uffff\\ud83d\\ude00\"", PyJson.Dumps(s));
        Assert.Equal("\"a\\\"b\\\\c\\n\\r\\t\\b\\f\\u0001\u007f\u00e9\uffff\U0001F600\"", PyJson.Dumps(s, ensureAscii: false));
    }

    [Fact]
    public void CodePointOrderPutsSupplementaryAfterBmpPrivateUse()
    {
        string bmp = "d\uffff";
        string emoji = "d\U0001F600";

        Assert.True(CodePointComparer.Compare(bmp, emoji) < 0);
        Assert.True(string.CompareOrdinal(bmp, emoji) > 0);
        Assert.Equal("{\"d\\uffff\": 1, \"d\\ud83d\\ude00\": 2}",
            PyJson.Dumps(new JObject { { emoji, 2L }, { bmp, 1L } }, sortKeys: true));
    }

    [Fact]
    public void ToCrlfAndUnsupportedTypes()
    {
        Assert.Equal("a\r\nb", PyJson.ToCrlf("a\nb"));
        Assert.Throws<PyValueException>(() => PyJson.Dumps(new object()));
        Assert.Throws<PyValueException>(() => PyJson.Dumps(double.NaN));
    }

    [Fact]
    public void ReadingIsStrictAboutIntegers()
    {
        var obj = Assert.IsType<JObject>(JsonRead.Parse("{\"i\": 5, \"f\": 5.0, \"e\": 1e2, \"n\": -0, \"s\": \"\\u00e9\", \"b\": true, \"z\": null}"));

        Assert.Equal(5L, obj["i"]);
        Assert.Equal(5.0, obj["f"]);
        Assert.Equal(100.0, obj["e"]);
        Assert.Equal(0L, obj["n"]);
        Assert.Equal("\u00e9", obj["s"]);
        Assert.Equal(true, obj["b"]);
        Assert.Null(obj["z"]);
        Assert.Throws<PyValueException>(() => JsonRead.Parse("9223372036854775808"));
        Assert.Throws<PyValueException>(() => JsonRead.Parse("[1,]"));
        Assert.Throws<PyValueException>(() => JsonRead.Parse("{} x"));
    }

    [Fact]
    public void DuplicateKeysKeepFirstPositionAndLastValue()
    {
        var obj = Assert.IsType<JObject>(JsonRead.Parse("{\"a\": 1, \"b\": 2, \"a\": 3}"));

        Assert.Equal("{\"a\": 3, \"b\": 2}", PyJson.Dumps(obj));
    }

    [Fact]
    public void ReadFileAcceptsBomAndRejectsBadUtf8()
    {
        string dir = Directory.CreateTempSubdirectory().FullName;
        try
        {
            string bom = Path.Combine(dir, "bom.json");
            File.WriteAllBytes(bom, [0xEF, 0xBB, 0xBF, .. "[1]"u8.ToArray()]);
            string bad = Path.Combine(dir, "bad.json");
            File.WriteAllBytes(bad, [(byte)'"', 0xFF, (byte)'"']);

            Assert.Equal(1L, Assert.Single(Assert.IsType<List<object?>>(JsonRead.ReadFile(bom))));
            Assert.Throws<PyValueException>(() => JsonRead.ReadFile(bad));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void JournalRowsSerializeLikePython()
    {
        using JsonDocument doc = PlanFixtures.Doc("synthetic/journal_rows.json");
        foreach (JsonElement item in doc.RootElement.EnumerateArray())
        {
            object? row = JsonRead.Parse(item.GetProperty("row").GetRawText());
            string line = item.GetProperty("line").GetString()!;

            Assert.Equal(line, PyJson.Dumps(row));
            Assert.Equal(PyJson.Dumps(row), PyJson.Dumps(JsonRead.Parse(line)));
        }
    }

    [Fact]
    public void RecordedJournalLinesRoundTripByteForByte()
    {
        string[] lines = PlanFixtures.Lf(PlanFixtures.Bytes("e3/journal.jsonl")).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2162, lines.Length);
        foreach (string line in lines)
        {
            Assert.Equal(line, PyJson.Dumps(JsonRead.Parse(line)));
        }
    }

    [Fact]
    public void TruncatedJournalCopyHasExactlyOneBrokenLastLine()
    {
        byte[] bytes = PlanFixtures.Bytes("synthetic/journal_truncated.jsonl");
        string[] lines = Encoding.UTF8.GetString(bytes).Split("\r\n");

        Assert.Equal(51, lines.Length);
        for (int i = 0; i < 50; i++)
        {
            Assert.IsType<JObject>(JsonRead.Parse(lines[i]));
        }

        Assert.Throws<PyValueException>(() => JsonRead.Parse(lines[50]));
    }
}
