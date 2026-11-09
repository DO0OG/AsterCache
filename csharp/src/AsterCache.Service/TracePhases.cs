using System.Collections.Concurrent;
using System.Text;
using AsterCache.Core.Json;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Tracer;

namespace AsterCache.Service;

/// 트레이스 범위의 common / map_initial 구분. 모드·맵 정보 없이 회차별 트레이스 (run 파일) 만으로 가립니다:
/// 유효한 모든 회차에서 반복해 읽힌 64 KiB 블록 = common (검증됨), 일부 회차에서만 읽힌 블록 = map_initial (모드·맵 의존 가능, 따로 저장하지 않고 나머지로 봄).
/// 유효 회차가 MinCommonRuns 보다 적으면 아무것도 common 으로 올리지 않습니다 (보수적). 게임 프로세스에는 접근하지 않고 이미 저장된 파일만 읽습니다.
/// 결과는 plans\<이름>.trace.common.json (계획·저널·트레이스·프로필 형식과 무관한 별도 파일) 에 입력 지문과 함께 남기고, 지문이 같으면 다시 계산하지 않습니다.
public static class TracePhases
{
    /// 최소 회차: 같은 모드·맵을 두 번 한 우연의 일치로 맵 자산이 common 이 되지 않도록 세 번 이상. shortcut: 세 번 모두 같은 맵이면 그 맵도 common 이 됨 (순서만 앞설 뿐 읽는 양은 그대로), 맵 정보를 얻는 방법이 생기면 개선.
    public const int MinCommonRuns = 3;

    private const int FormatVersion = 1;
    private static readonly ConcurrentDictionary<string, (string Inputs, IReadOnlyList<TraceEvent> Common)> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string CommonPath(GameSettings g) => Path.Combine(Path.GetDirectoryName(g.Plan) ?? string.Empty, g.Name + ".trace.common.json");

    /// 예열 경로: 저장된 common 범위 (경로 순, 서로 겹치지 않음) 만 읽고 run 파일은 읽지 않습니다. 저장본이 없거나 지문이 안 맞거나 깨졌거나 common 이 없으면 null (호출부는 기존 동작).
    public static IReadOnlyList<TraceEvent>? CommonOf(GameSettings g, long minRunBytes = AutoTracer.MinExtraRunBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = CommonPath(g);
        string inputs = Inputs(g, minRunBytes, cancellationToken);
        if (inputs.Length == 0)
        {
            return null;
        }

        if (!Cache.TryGetValue(path, out var hit) || hit.Inputs != inputs)
        {
            List<TraceEvent>? loaded = Load(path, inputs, cancellationToken);
            if (loaded is null)
            {
                return null;
            }

            Cache[path] = hit = (inputs, loaded);
        }

        return hit.Common.Count > 0 ? hit.Common : null;
    }

    /// Merge 끝과 업데이트로 run 을 다시 쓴 뒤에만 부릅니다: run 파일을 읽어 common 을 계산해 저장합니다. 읽을 수 없으면 저장하지 않고 예열은 기존 동작.
    public static void Recount(GameSettings g, long minRunBytes = AutoTracer.MinExtraRunBytes)
    {
        string inputs = Inputs(g, minRunBytes);
        if (inputs.Length == 0)
        {
            return;
        }

        try
        {
            Compute(g, minRunBytes, CommonPath(g), inputs);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            // 읽을 수 없는 run 파일: 옛 저장본은 지문이 달라 쓰이지 않음
        }
    }

    /// ops (이번 예열이 실제로 읽을 범위, 읽는 순서) 중 common 과 겹치는 부분만 그 순서대로: 이번에 읽을 범위의 부분집합이라 읽는 양의 상한이 그대로입니다.
    public static List<ReadOp> Within(IReadOnlyList<TraceEvent> common, IReadOnlyList<ReadOp> ops, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Dictionary<string, (long Start, long End)[]> byPath = common.GroupBy(c => c.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(c => c.Offset).Select(c => (c.Offset, c.Offset + c.Length)).ToArray(), StringComparer.OrdinalIgnoreCase);
        var result = new List<ReadOp>();
        foreach (ReadOp op in ops)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byPath.TryGetValue(op.Path, out (long Start, long End)[]? ranges))
            {
                continue;
            }

            long end = op.Offset + op.Length;
            int lo = 0, hi = ranges.Length;
            while (lo < hi) // 끝이 op 시작보다 뒤인 첫 범위
            {
                int mid = (lo + hi) / 2;
                if (ranges[mid].End <= op.Offset)
                {
                    lo = mid + 1;
                }
                else
                {
                    hi = mid;
                }
            }

            for (int i = lo; i < ranges.Length && ranges[i].Start < end; i++)
            {
                long from = Math.Max(op.Offset, ranges[i].Start);
                result.Add(new ReadOp(op.Path, from, Math.Min(end, ranges[i].End) - from));
            }
        }

        return result;
    }

    // run 파일들의 (번호, 크기, 수정 시각), 기준 바이트, 옛 형식 표지. run 파일이 하나도 없으면 빈 문자열 (구분 자료 없음).
    private static string Inputs(GameSettings g, long minRunBytes, CancellationToken cancellationToken = default)
    {
        string raw = AutoTracer.RawTracePath(g);
        var sb = new StringBuilder();
        for (int n = 1; n <= AutoTracer.MaxRunFiles; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(TraceWriter.RunPath(raw, n));
            if (info.Exists)
            {
                sb.Append(n).Append(':').Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks).Append(';');
            }
        }

        if (sb.Length == 0)
        {
            return string.Empty;
        }

        sb.Append("min:").Append(minRunBytes).Append(';');
        return File.Exists(AutoTracer.LegacyRun1Path(g)) ? sb.Append("legacy;").ToString() : sb.ToString();
    }

    private static List<TraceEvent>? Load(string path, string inputs, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            object? loaded = File.Exists(path) ? JsonRead.ReadFile(path) : null;
            cancellationToken.ThrowIfCancellationRequested();
            if (loaded is JObject o && o.TryGetValue("version", out object? v) && v is long ver && ver == FormatVersion
                && o.TryGetValue("inputs", out object? i) && i as string == inputs && o.TryGetValue("ranges", out object? r) && r is List<object?> rows)
            {
                // 형식 오류는 FromJson 이 PyValueException 으로 던짐. 음수·0 길이·범위 넘침 행은 예열 범위로 쓸 수 없어 전체를 버림
                var ranges = new List<TraceEvent>(rows.Count);
                foreach (object? row in rows)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ranges.Add(TraceEvents.FromJson(row));
                }

                return ranges.All(e => e.Path.Length > 0 && e.Offset >= 0 && e.Length > 0 && e.Length <= long.MaxValue - e.Offset) ? ranges : null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            // 못 읽거나 낡은 파일: 다시 계산
        }

        return null;
    }

    private static List<TraceEvent> Compute(GameSettings g, long minRunBytes, string path, string inputs, CancellationToken cancellationToken = default)
    {
        string raw = AutoTracer.RawTracePath(g);
        bool legacy = File.Exists(AutoTracer.LegacyRun1Path(g));
        var counts = new Dictionary<string, Dictionary<long, int>>(StringComparer.OrdinalIgnoreCase);
        int valid = 0;
        for (int n = 1; n <= AutoTracer.MaxRunFiles; n++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string file = TraceWriter.RunPath(raw, n);
            if (!File.Exists(file))
            {
                continue;
            }

            cancellationToken.ThrowIfCancellationRequested();
            object? loaded = JsonRead.ReadFile(file);
            if (loaded is not List<object?> sourceRows)
            {
                throw new PyValueException("bad trace file");
            }

            var rows = new List<TraceEvent>(sourceRows.Count);
            foreach (object? row in sourceRows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                rows.Add(TraceEvents.FromJson(row));
            }
            if (!(n == 1 && legacy) && rows.Sum(e => e.Length) < minRunBytes)
            {
                continue; // 병합과 같은 기준: 읽기량이 모자란 회차는 유효하지 않음
            }

            valid++;
            var seen = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
            foreach (TraceEvent e in rows.Where(e => e.Offset >= 0 && e.Length > 0))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!seen.TryGetValue(e.Path, out HashSet<long>? blocks))
                {
                    seen[e.Path] = blocks = [];
                }

                for (long blk = e.Offset / TraceWriter.Block; blk <= (e.Offset + e.Length - 1) / TraceWriter.Block; blk++)
                {
                    blocks.Add(blk);
                }
            }

            foreach ((string p, HashSet<long> blocks) in seen)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!counts.TryGetValue(p, out Dictionary<long, int>? per))
                {
                    counts[p] = per = [];
                }

                foreach (long blk in blocks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    per[blk] = per.GetValueOrDefault(blk) + 1;
                }
            }
        }

        var common = new List<TraceEvent>();
        if (valid >= MinCommonRuns)
        {
            foreach ((string p, Dictionary<long, int> per) in counts.OrderBy(c => c.Key, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                long start = -1, next = -1;
                foreach (long blk in per.Where(b => b.Value == valid).Select(b => b.Key).Order())
                {
                    if (blk != next)
                    {
                        AddRange(common, p, start, next);
                        start = blk;
                    }

                    next = blk + 1;
                }

                AddRange(common, p, start, next);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        Save(path, inputs, valid, common);
        return common;
    }

    private static void AddRange(List<TraceEvent> common, string path, long start, long next)
    {
        if (start >= 0)
        {
            common.Add(new TraceEvent(path, start * TraceWriter.Block, (next - start) * TraceWriter.Block));
        }
    }

    // 힌트일 뿐: 못 써도 이번 프로세스는 메모리 값을 쓰고, 다음에 다시 계산합니다.
    private static void Save(string path, string inputs, int runs, List<TraceEvent> common)
    {
        try
        {
            var o = new JObject { { "version", (long)FormatVersion }, { "runs", (long)runs }, { "inputs", inputs }, { "ranges", common.Select(e => (object?)e.ToJson()).ToList() } };
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, PyJson.Utf8.GetBytes(PyJson.ToCrlf(PyJson.Dumps(o, ensureAscii: false) + "\n")));
            File.Move(temp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 저장하지 못해도 구분 결과는 그대로
        }
    }
}
