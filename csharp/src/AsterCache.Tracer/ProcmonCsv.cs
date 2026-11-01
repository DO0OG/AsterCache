using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using AsterCache.Core.Trace;

namespace AsterCache.Tracer;

public readonly record struct ProcmonStats(int Rows, int ReadSuccessRows, int Parsed, int Ignored);

/// procmon_import.parse_procmon_csv + astercache_v07.load_events 포팅. 사용자가 내보낸 Process Monitor CSV 만 읽습니다
/// (Operation 이 ReadFile, Result 가 SUCCESS 이고 Detail 에 Offset·Length 가 있는 행).
/// 안티치트 위험 때문에 Procmon 실행 자체는 하지 않습니다.
public static partial class ProcmonCsv
{
    private static readonly string[] Required = ["Operation", "Path", "Result", "Detail"];

    [GeneratedRegex(@"\bOffset:\s*(0x[0-9A-Fa-f]+|[0-9,]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OffsetPattern();

    [GeneratedRegex(@"\bLength:\s*(0x[0-9A-Fa-f]+|[0-9,]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LengthPattern();

    public static (List<TraceEvent> Events, ProcmonStats Stats) Parse(string csvPath, string drivePrefix = "", int maxEvents = TraceEvents.MaxSessionEvents)
    {
        if (maxEvents < 1 || maxEvents > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(maxEvents), "max_events invalid");
        }

        string prefix = drivePrefix.Length > 0 ? NtPath.NormCase(NtPath.NormPath(drivePrefix)) : "";
        List<List<string>> table = ReadRows(csvPath);
        if (table.Count == 0 || !Required.All(table[0].Contains))
        {
            throw new FormatException("Expected Procmon CSV with Operation, Path, Result, Detail columns");
        }

        List<string> header = table[0];
        int[] col = Required.Select(c => header.LastIndexOf(c)).ToArray(); // 중복 열 이름은 DictReader 처럼 마지막이 이김

        int rows = 0;
        int readSuccess = 0;
        int ignored = 0;
        var events = new List<TraceEvent>();
        foreach (List<string> row in table.Skip(1))
        {
            if (row.Count == 0)
            {
                continue; // DictReader 는 빈 줄을 건너뜁니다.
            }

            rows++;
            string Cell(int k) => col[k] < row.Count ? row[col[k]] : "";
            if (!Cell(0).Equals("readfile", StringComparison.OrdinalIgnoreCase) || !Cell(2).Equals("success", StringComparison.OrdinalIgnoreCase))
            {
                ignored++;
                continue;
            }

            readSuccess++;
            string name = Cell(1);
            if (prefix.Length > 0 && !NtPath.IsUnder(NtPath.NormCase(NtPath.NormPath(name)), prefix))
            {
                ignored++;
                continue;
            }

            Match mo = OffsetPattern().Match(Cell(3));
            Match ml = LengthPattern().Match(Cell(3));
            if (!mo.Success || !ml.Success || !TryNum(mo.Groups[1].Value, out long off) || !TryNum(ml.Groups[1].Value, out long length)
                || off < 0 || length <= 0 || length > TraceBuilder.MaxLength)
            {
                ignored++;
                continue;
            }

            events.Add(new TraceEvent(name, off, length));
            if (events.Count >= maxEvents)
            {
                break;
            }
        }

        return (events, new ProcmonStats(rows, readSuccess, events.Count, ignored));
    }

    /// astercache_v07.load_events: Parse 한 뒤 경로마다 디스크의 실제 대소문자를 씁니다.
    public static (List<TraceEvent> Events, ProcmonStats Stats) LoadEvents(string csvPath, string prefix, Func<string, string?>? realPath = null)
    {
        (List<TraceEvent> events, ProcmonStats stats) = Parse(csvPath, prefix);
        return (TraceBuilder.CanonicalizeCase(events, realPath), stats);
    }

    // shortcut: 숫자가 int64 를 넘거나 쉼표뿐이면 (Python 은 큰 정수 허용 / ValueError) 해석 불가 행으로 무시, 실제 Offset·Length 는 이 범위 안.
    private static bool TryNum(string s, out long value)
    {
        s = s.Replace(",", "", StringComparison.Ordinal);
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return long.TryParse(s.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value) && value >= 0;
        }

        return long.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// Python csv (excel 방언): 따옴표 필드, "" 이스케이프, 따옴표 안 줄바꿈, \r\n | \n | \r 행 끝. BOM 은 utf-8-sig 처럼 제거.
    private static List<List<string>> ReadRows(string path)
    {
        string text = new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path));
        if (text.Length > 0 && text[0] == '﻿')
        {
            text = text[1..];
        }

        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        bool fieldStarted = false;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c != '"')
                {
                    field.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (c)
            {
                case '"' when !fieldStarted:
                    quoted = true;
                    fieldStarted = true;
                    break;
                case ',':
                    row.Add(field.ToString());
                    field.Clear();
                    fieldStarted = false;
                    break;
                case '\r' or '\n':
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }

                    EndRow(rows, row, field, fieldStarted);
                    row = [];
                    fieldStarted = false;
                    break;
                default:
                    field.Append(c);
                    fieldStarted = true;
                    break;
            }
        }

        if (fieldStarted || field.Length > 0 || row.Count > 0)
        {
            EndRow(rows, row, field, fieldStarted);
        }

        return rows;
    }

    private static void EndRow(List<List<string>> rows, List<string> row, StringBuilder field, bool fieldStarted)
    {
        if (row.Count > 0 || fieldStarted || field.Length > 0)
        {
            row.Add(field.ToString());
        }

        field.Clear();
        rows.Add(row);
    }
}
