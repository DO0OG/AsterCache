using System.Text.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Prefetch;
using Xunit;

namespace AsterCache.Tests.Core;

/// F7: fresh/snapshot.json (trace 의 첫 세션 + 파일 배치 스냅샷) 으로 만든 Python schedule 결과(fresh/schedule.json 12조합)와
/// ops 목록·info 가 같아야 합니다. 픽스처가 없으면 읽기에서 실패합니다 (건너뛰지 않음).
public sealed class SchedulerParityTests
{
    private static JsonDocument Fixture(string relative) =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", relative)));

    [Fact]
    public void ScheduleMatchesPythonForEveryFixtureCase()
    {
        using JsonDocument snapDoc = Fixture("fresh/snapshot.json");
        using JsonDocument schedDoc = Fixture("fresh/schedule.json");
        JsonElement snap = snapDoc.RootElement;
        int clusterBytes = snap.GetProperty("cluster_bytes").GetInt32();
        var layouts = new Dictionary<string, FileLayoutInfo>(StringComparer.Ordinal);
        var sizes = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (JsonProperty f in snap.GetProperty("files").EnumerateObject())
        {
            sizes[f.Name] = f.Value.GetProperty("size").GetInt64();
            layouts[f.Name] = new FileLayoutInfo(
                f.Value.GetProperty("extents").EnumerateArray().Select(e => new Extent(e[0].GetInt64(), e[1].GetInt64(), e[2].GetInt64())).ToList(),
                clusterBytes);
        }

        List<ReadRange> ranges = snap.GetProperty("sessions")[0].EnumerateArray()
            .Select(r => new ReadRange(r.GetProperty("path").GetString()!, r.GetProperty("offset").GetInt64(), r.GetProperty("length").GetInt64()))
            .ToList();
        string[] names = schedDoc.RootElement.GetProperty("files").EnumerateArray().Select(n => n.GetString()!).ToArray();

        int checkedCases = 0;
        foreach (JsonElement c in schedDoc.RootElement.GetProperty("cases").EnumerateArray())
        {
            string label = $"{c.GetProperty("strategy").GetString()} gap={c.GetProperty("gap_bytes").GetInt64()} ratio={c.GetProperty("full_file_ratio").GetDouble()} budget={c.GetProperty("budget_bytes").GetInt64()}";

            (List<ReadOp> ops, ScheduleInfo info) = Scheduler.Schedule(
                ranges,
                c.GetProperty("strategy").GetString()!,
                c.GetProperty("budget_bytes").GetInt64(),
                c.GetProperty("gap_bytes").GetInt64(),
                p => layouts.GetValueOrDefault(p),
                p => sizes[p],
                c.GetProperty("full_file_ratio").GetDouble());

            JsonElement expectedInfo = c.GetProperty("info");
            Assert.True(expectedInfo.GetProperty("strategy").GetString() == info.Strategy, label + " info.strategy");
            Assert.True(expectedInfo.GetProperty("selected_bytes").GetInt64() == info.SelectedBytes, label + " info.selected_bytes");
            Assert.True(expectedInfo.GetProperty("fallback").GetBoolean() == info.Fallback, label + " info.fallback");
            Assert.True(expectedInfo.GetProperty("layout_ok_files").GetInt32() == info.LayoutOkFiles, label + " info.layout_ok_files");
            Assert.True(expectedInfo.GetProperty("layout_files").GetInt32() == info.LayoutFiles, label + " info.layout_files");
            Assert.True(expectedInfo.GetProperty("gap_bytes").GetInt64() == info.GapBytes, label + " info.gap_bytes");
            Assert.True(expectedInfo.GetProperty("full_files").GetInt32() == info.FullFiles, label + " info.full_files");

            List<ReadOp> expected = c.GetProperty("ops").EnumerateArray()
                .Select(o => new ReadOp(names[o[0].GetInt32()], o[1].GetInt64(), o[2].GetInt64()))
                .ToList();
            Assert.True(expected.SequenceEqual(ops), label + " ops differ (expected " + expected.Count + ", got " + ops.Count + ")");
            checkedCases++;
        }

        Assert.Equal(12, checkedCases);
    }
}
