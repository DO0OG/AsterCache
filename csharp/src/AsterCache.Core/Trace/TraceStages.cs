using AsterCache.Core.Json;

namespace AsterCache.Core.Trace;

/// run 기록에 덧붙이는 등록 프로세스의 첫 시작 시각.
public sealed record StageMark(string Exe, long TMs);

/// 단계에 배정된 첫 읽기 순 블록 범위.
public sealed record TraceStage(string Exe, long TMs, IReadOnlyList<TraceEvent> Ranges);

/// 병합 추적의 단계 색인과 곁 파일 입출력.
public sealed record TraceStageIndex(IReadOnlyList<TraceStage> Stages)
{
    public const string Suffix = ".stages.json";
    private const long Block = 65536;

    public static string RunMarksPath(string runPath) => runPath + Suffix;

    public static string IndexPath(string rawTracePath) => rawTracePath + Suffix;

    /// 최신 run 우선으로 병합하면서 먼저 나온 블록의 단계를 보존합니다.
    public static TraceStageIndex? Build(IReadOnlyList<(IReadOnlyList<TraceEvent> Events, IReadOnlyList<StageMark> Marks)> runsNewestFirst)
    {
        var markedRuns = new List<IReadOnlyList<StageMark>>();
        foreach (var (_, marks) in runsNewestFirst)
        {
            List<StageMark> valid = NormalizeMarks(marks);
            if (valid.Count > 0)
            {
                markedRuns.Add(valid);
            }
        }

        if (markedRuns.Count == 0)
        {
            return null;
        }

        var stageMarks = new List<StageMark>();
        var stageByExe = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (StageMark mark in markedRuns[0])
        {
            AddStage(mark);
        }

        // 옛 run 의 첫 표지는 그 run 의 첫 단계(0)로만 쓰고 새 단계로 만들지 않음 (첫 exe 의 시작 기록이 run 마다 있거나 없을 수 있어, 별도 단계가 되면 첫 구간 블록이 첫 스트림에서 빠짐)
        foreach (StageMark mark in markedRuns.Skip(1).SelectMany(x => x.Skip(1)).OrderBy(x => x.TMs))
        {
            AddStage(mark);
        }

        void AddStage(StageMark mark)
        {
            if (stageByExe.ContainsKey(mark.Exe))
            {
                return;
            }

            stageByExe.Add(mark.Exe, stageMarks.Count);
            stageMarks.Add(mark);
        }

        var ranges = Enumerable.Range(0, stageMarks.Count).Select(_ => new List<TraceEvent>()).ToArray();
        var seen = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        for (int runIndex = 0; runIndex < runsNewestFirst.Count; runIndex++)
        {
            List<StageMark> marks = NormalizeMarks(runsNewestFirst[runIndex].Marks);
            if (marks.Count == 0)
            {
                continue;
            }

            IEnumerable<(TraceEvent Event, int Order)> ordered = runsNewestFirst[runIndex].Events
                .Select((e, i) => (Event: e, Order: i))
                .Where(x => x.Event.TMs is not null)
                .OrderBy(x => x.Event.TMs)
                .ThenBy(x => x.Order);
            foreach (var (e, _) in ordered)
            {
                if (string.IsNullOrEmpty(e.Path) || e.Offset < 0 || e.Length <= 0 || e.Offset > long.MaxValue - e.Length)
                {
                    continue;
                }

                long first = e.Offset / Block;
                long last = (e.Offset + e.Length - 1) / Block;
                int stageIndex = StageAt(marks, e.TMs!.Value, stageByExe);
                if (!seen.TryGetValue(e.Path, out HashSet<long>? pathBlocks))
                {
                    pathBlocks = [];
                    seen.Add(e.Path, pathBlocks);
                }

                for (long block = first; block <= last; block++)
                {
                    if (!pathBlocks.Add(block))
                    {
                        continue;
                    }

                    AddRange(ranges[stageIndex], e.Path, block * Block);
                }
            }
        }

        return new TraceStageIndex(stageMarks.Select((mark, i) => new TraceStage(mark.Exe, mark.TMs, ranges[i])).ToArray());
    }

    /// 등록 exe 이름으로 단계 위치를 찾습니다. 첫 단계는 첫 실행 경로에서 별도로 다룹니다.
    public int? StageOf(string exe)
    {
        for (int i = 1; i < Stages.Count; i++)
        {
            if (string.Equals(Stages[i].Exe, exe, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return null;
    }

    public static IReadOnlyList<StageMark>? ReadMarks(string runPath)
    {
        string path = RunMarksPath(runPath);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (JsonRead.ReadFile(path) is not List<object?> rows)
            {
                return null;
            }

            var marks = new List<StageMark>(rows.Count);
            foreach (object? row in rows)
            {
                if (row is not JObject o || !o.TryGetValue("exe", out object? name) || name is not string exe
                    || !o.TryGetValue("t_ms", out object? time) || time is not long tMs || tMs < 0)
                {
                    return null;
                }

                marks.Add(new StageMark(exe, tMs));
            }

            return marks;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
    }

    public static void WriteMarks(string runPath, IReadOnlyList<StageMark> marks)
    {
        var rows = marks.Select(m => new JObject { { "exe", m.Exe }, { "t_ms", m.TMs } }).ToList();
        WriteJson(RunMarksPath(runPath), rows);
    }

    public static TraceStageIndex? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            if (JsonRead.ReadFile(path) is not JObject root
                || !root.TryGetValue("version", out object? version) || version is not long v || v != 1
                || !root.TryGetValue("stages", out object? value) || value is not List<object?> rows)
            {
                return null;
            }

            var stages = new List<TraceStage>(rows.Count);
            foreach (object? row in rows)
            {
                if (row is not JObject o || !o.TryGetValue("exe", out object? name) || name is not string exe
                    || !o.TryGetValue("t_ms", out object? time) || time is not long tMs || tMs < 0
                    || !o.TryGetValue("ranges", out object? rangesValue) || rangesValue is not List<object?> rangeRows)
                {
                    return null;
                }

                var ranges = new List<TraceEvent>(rangeRows.Count);
                foreach (object? range in rangeRows)
                {
                    TraceEvent e = TraceEvents.FromJson(range);
                    if (string.IsNullOrEmpty(e.Path) || e.Offset < 0 || e.Length <= 0 || e.Offset > long.MaxValue - e.Length)
                    {
                        return null;
                    }

                    ranges.Add(e with { TMs = null });
                }

                stages.Add(new TraceStage(exe, tMs, ranges));
            }

            return new TraceStageIndex(stages);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or OverflowException)
        {
            return null;
        }
    }

    public static void Write(string path, TraceStageIndex index)
    {
        var stages = index.Stages.Select(stage => (object?)new JObject
        {
            { "exe", stage.Exe },
            { "t_ms", stage.TMs },
            { "ranges", stage.Ranges.Select(range => (object?)new JObject
                {
                    { "path", range.Path },
                    { "offset", range.Offset },
                    { "length", range.Length },
                }).ToList() },
        }).ToList();
        WriteJson(path, new JObject { { "version", 1L }, { "stages", stages } });
    }

    private static List<StageMark> NormalizeMarks(IReadOnlyList<StageMark> marks)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return marks.Select((mark, index) => (mark, index))
            .Where(x => x.mark.Exe is not null && x.mark.TMs >= 0)
            .OrderBy(x => x.mark.TMs)
            .ThenBy(x => x.index)
            .Where(x => seen.Add(x.mark.Exe))
            .Select(x => x.mark)
            .ToList();
    }

    private static int StageAt(IReadOnlyList<StageMark> marks, long tMs, IReadOnlyDictionary<string, int> stageByExe)
    {
        StageMark mark = marks[0];
        foreach (StageMark candidate in marks)
        {
            if (candidate.TMs > tMs)
            {
                break;
            }

            mark = candidate;
        }

        return mark == marks[0] ? 0 : stageByExe[mark.Exe];
    }

    private static void AddRange(List<TraceEvent> ranges, string path, long offset)
    {
        if (ranges.Count > 0 && string.Equals(ranges[^1].Path, path, StringComparison.OrdinalIgnoreCase)
            && ranges[^1].Offset + ranges[^1].Length == offset)
        {
            ranges[^1] = ranges[^1] with { Length = ranges[^1].Length + Block };
        }
        else
        {
            ranges.Add(new TraceEvent(path, offset, Block));
        }
    }

    private static void WriteJson(string path, object? value)
    {
        string temp = path + ".tmp";
        string text = PyJson.Dumps(value, indent: 1, ensureAscii: false) + "\n";
        File.WriteAllBytes(temp, PyJson.Utf8.GetBytes(PyJson.ToCrlf(text)));
        File.Move(temp, path, true);
    }
}
