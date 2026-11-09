using AsterCache.Core.Json;
using AsterCache.Core.Prefetch;

namespace AsterCache.Core.Plan.Orbit;

/// 오프라인 벤치 지표 (설계안 4.2·4.3). 블록은 64 KiB 단위이고, 블록 완료 시각은 그 블록을 덮는 op 들의 마지막 완료 시각입니다.
public static class OrbitBenchMetrics
{
    public const long BlockBytes = 65536;
    public const long LegacyOrbitVersion = 1;
    public const long OrbitVersion = 2;

    /// orbit.json 의 에피소드 한 개. Weight 는 p_e = runs_seen / runs_total.
    public sealed record BenchEpisode(IReadOnlyList<(string Path, long Block)> Blocks, double Weight);

    /// 에피소드 준비 시각. TReadyMs 는 덮인 블록이 하나도 없으면 null, MissingBlocks 는 목록에 없어 무시한 블록 수.
    public sealed record EpisodeTiming(double? TReadyMs, int MissingBlocks);

    /// 한 번 읽은 결과. ValueMeanMs 는 가치 가중 평균 준비 시각, CurveAreaMs 는 준비 곡선 면적 (ms).
    public sealed record BenchRunMetrics(double TotalMs, long Bytes, IReadOnlyList<EpisodeTiming> Episodes, double? ValueMeanMs, double? CurveAreaMs);

    /// summarize 가 쓰는 실행 한 건의 값 (run 출력 파일에서 읽음).
    public sealed record RunSummary(string Arm, double TotalMs, double? ValueMeanMs, IReadOnlyList<double?> EpisodeTReadyMs);

    /// 쌍 하나. ValueReduction 은 (A-B)/A (양수면 단축, 값이 없으면 null), TotalWorse 는 (B-A)/A (양수면 악화).
    public sealed record PairDelta(double? ValueReduction, double TotalWorse);

    /// 판정. Flag4 는 비교할 에피소드가 없으면 null. 플래그 5 는 이 계산의 범위가 아니므로 호출하는 쪽이 null 로 씁니다.
    public sealed record Verdict(
        IReadOnlyList<PairDelta> Pairs, double? ValueReductionMedian, int PairsReductionAtLeast10, double? SignP, double? TotalWorseMedian,
        double? EpisodeP95WorseMaxMs, bool Flag2, bool Flag3, bool? Flag4);

    /// orbit.json 을 읽습니다. 버전이 1 이 아니거나 어디라도 깨졌으면 에피소드 없음 (빈 목록).
    public static List<BenchEpisode> ParseEpisodes(string json)
    {
        if (ParseRoot(json) is not { } root || Field(root, "episodes") is not List<object?> list)
        {
            return [];
        }

        long version = (long)Field(root, "version")!;
        var files = new List<string>();
        if (version == OrbitVersion)
        {
            if (Field(root, "files") is not List<object?> fileRows)
            {
                return [];
            }

            var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (object? file in fileRows)
            {
                if (file is not string path || path.Length == 0 || !knownFiles.Add(path))
                {
                    return [];
                }

                files.Add(path);
            }
        }

        var episodes = new List<BenchEpisode>(list.Count);
        foreach (object? item in list)
        {
            if (item is not JObject e || Field(e, "runs_seen") is not long seen || Field(e, "runs_total") is not long total || total <= 0 || seen < 0 || seen > total
                || version == OrbitVersion && seen == 0)
            {
                return [];
            }

            var blocks = new List<(string Path, long Block)>();
            if (version == LegacyOrbitVersion)
            {
                if (Field(e, "blocks") is not List<object?> rows)
                {
                    return [];
                }

                foreach (object? row in rows)
                {
                    if (row is not List<object?> { Count: 2 } pair || pair[0] is not string path || pair[1] is not long block || block < 0)
                    {
                        return [];
                    }

                    blocks.Add((path, block));
                }
            }
            else
            {
                if (Field(e, "ranges") is not List<object?> ranges || ranges.Count == 0)
                {
                    return [];
                }

                foreach (object? range in ranges)
                {
                    if (range is not List<object?> { Count: 3 } parts || parts[0] is not long fileIndex || fileIndex < 0 || fileIndex >= files.Count
                        || parts[1] is not long start || start < 0 || parts[2] is not long count || count <= 0 || count > long.MaxValue - start)
                    {
                        return [];
                    }

                    string path = files[(int)fileIndex];
                    long end = start + count;
                    for (long block = start; block < end; block++)
                    {
                        blocks.Add((path, block));
                    }
                }
            }

            episodes.Add(new BenchEpisode(blocks, (double)seen / total));
        }

        return episodes;
    }

    /// orbit.json 의 run 지문 (inputs). 버전이 1 이 아니거나 깨졌거나 값이 없으면 null.
    public static string? Fingerprint(string json) => ParseRoot(json) is { } root && Field(root, "inputs") is string s ? s : null;

    private static JObject? ParseRoot(string json)
    {
        object? parsed;
        try
        {
            parsed = JsonRead.Parse(json);
        }
        catch (PyValueException)
        {
            return null;
        }

        return parsed is JObject root && Field(root, "version") is long version && version is LegacyOrbitVersion or OrbitVersion ? root : null;
    }

    /// JObject 의 값 (키가 없으면 null). 인덱서는 없는 키에서 예외를 내므로 읽을 때 이 함수를 씁니다.
    public static object? Field(JObject o, string key) => o.TryGetValue(key, out object? v) ? v : null;

    /// 한 번 읽은 결과로 지표를 냅니다. done 은 op 와 그 완료 시각 (스트림 시작 기준 ms) 입니다.
    public static BenchRunMetrics Evaluate(IReadOnlyList<(ReadOp Op, double DoneMs)> done, IReadOnlyList<BenchEpisode> episodes)
    {
        double total = done.Count == 0 ? 0 : done.Max(d => d.DoneMs);

        var blockDone = new Dictionary<(string, long), double>();
        foreach ((ReadOp op, double t) in done)
        {
            if (op.Length <= 0)
            {
                continue;
            }

            string key = Key(op.Path);
            for (long b = op.Offset / BlockBytes; b <= (op.Offset + op.Length - 1) / BlockBytes; b++)
            {
                blockDone[(key, b)] = blockDone.TryGetValue((key, b), out double prev) ? Math.Max(prev, t) : t;
            }
        }

        var timings = new List<EpisodeTiming>(episodes.Count);
        foreach (BenchEpisode e in episodes)
        {
            double? ready = null;
            int missing = 0;
            foreach ((string path, long block) in e.Blocks)
            {
                if (blockDone.TryGetValue((Key(path), block), out double bt))
                {
                    ready = ready is null ? bt : Math.Max(ready.Value, bt);
                }
                else
                {
                    missing++;
                }
            }

            timings.Add(new EpisodeTiming(ready, missing));
        }

        // 준비 시각이 있는 에피소드만 가치 합에 넣습니다 (없는 에피소드는 가중치 분모에도 넣지 않음)
        double weightSum = 0;
        double weighted = 0;
        for (int i = 0; i < episodes.Count; i++)
        {
            if (timings[i].TReadyMs is { } t)
            {
                weightSum += episodes[i].Weight;
                weighted += episodes[i].Weight * t;
            }
        }

        double? mean = null;
        double? area = null;
        if (weightSum > 0)
        {
            double a = 0;
            for (int i = 0; i < episodes.Count; i++)
            {
                if (timings[i].TReadyMs is { } t)
                {
                    a += episodes[i].Weight / weightSum * (total - t); // 준비 곡선: 시각 T 이후 준비된 가치 비율의 적분
                }
            }

            mean = weighted / weightSum;
            area = a;
        }

        return new BenchRunMetrics(total, done.Sum(d => d.Op.Length), timings, mean, area);
    }

    /// ABBA 묶음 (A,B,B,A) 을 쌍으로 짓습니다: 각 묶음의 (0,1) 과 (2,3) 에서 A 와 B 를 짝지음. 개수가 4의 배수가 아니거나 순서가 다르면 오류.
    public static List<(RunSummary A, RunSummary B)> PairAbba(IReadOnlyList<RunSummary> runs)
    {
        if (runs.Count == 0 || runs.Count % 4 != 0)
        {
            throw new ArgumentException($"run count must be a positive multiple of 4 (ABBA), got {runs.Count}");
        }

        var pairs = new List<(RunSummary A, RunSummary B)>(runs.Count / 2);
        for (int g = 0; g < runs.Count; g += 4)
        {
            if (runs[g].Arm != "A" || runs[g + 1].Arm != "B" || runs[g + 2].Arm != "B" || runs[g + 3].Arm != "A")
            {
                throw new ArgumentException($"runs {g + 1}-{g + 4} are not in ABBA order");
            }

            pairs.Add((runs[g], runs[g + 1]));
            pairs.Add((runs[g + 3], runs[g + 2]));
        }

        return pairs;
    }

    /// ABBA 로 모은 실행에서 쌍별 지표와 판정을 냅니다 (플래그 2~4; 5 는 호출하는 쪽이 null).
    public static Verdict Summarize(IReadOnlyList<RunSummary> runs)
    {
        List<(RunSummary A, RunSummary B)> pairs = PairAbba(runs);
        int episodeCount = runs[0].EpisodeTReadyMs.Count;
        if (runs.Any(r => r.EpisodeTReadyMs.Count != episodeCount))
        {
            throw new ArgumentException("runs have different episode counts");
        }

        var deltas = new List<PairDelta>(pairs.Count);
        foreach ((RunSummary a, RunSummary b) in pairs)
        {
            if (a.TotalMs <= 0)
            {
                throw new ArgumentException("A run total time must be positive");
            }

            double? reduction = a.ValueMeanMs is { } va && b.ValueMeanMs is { } vb && va > 0 ? (va - vb) / va : null;
            deltas.Add(new PairDelta(reduction, (b.TotalMs - a.TotalMs) / a.TotalMs));
        }

        // 부호 검정: 값이 같은 쌍 (0 단축) 은 뺍니다
        double[] reds = deltas.Where(d => d.ValueReduction is { } r && r != 0).Select(d => d.ValueReduction!.Value).ToArray();
        int better = reds.Count(r => r > 0);
        double? signP = reds.Length == 0 ? null : SignTestP(reds.Length, better);
        double? reductionMedian = Median(deltas.Where(d => d.ValueReduction is not null).Select(d => d.ValueReduction!.Value));
        double? totalWorseMedian = Median(deltas.Select(d => d.TotalWorse));

        // 에피소드별: 쌍마다 (B-A) 의 p95 를 보고, 기준 max(5% 기준 A 중앙값, 200 ms) 넘는 에피소드가 있는지 봄
        double? p95Max = null;
        bool? flag4 = null;
        for (int e = 0; e < episodeCount; e++)
        {
            var diffs = new List<double>(pairs.Count);
            var baseA = new List<double>(pairs.Count);
            bool complete = true;
            foreach ((RunSummary a, RunSummary b) in pairs)
            {
                double? ta = a.EpisodeTReadyMs[e];
                double? tb = b.EpisodeTReadyMs[e];
                if (ta is null || tb is null)
                {
                    complete = false;
                    break;
                }

                diffs.Add(tb.Value - ta.Value);
                baseA.Add(ta.Value);
            }

            if (!complete)
            {
                continue;
            }

            double p95 = Percentile95(diffs);
            double limit = Math.Max(0.05 * (Median(baseA) ?? 0), 200);
            p95Max = p95Max is null ? p95 : Math.Max(p95Max.Value, p95);
            flag4 = (flag4 ?? true) && p95 <= limit;
        }

        // 제외된 쌍이 있으면 n 이 줄어 검정 대상이 달라지므로, 요청한 쌍이 모두 유효할 때만 판정합니다
        bool allPairsValid = deltas.All(d => d.ValueReduction is not null);
        bool flag2 = allPairsValid && reductionMedian is { } rm && rm >= 0.10 && signP is { } sp && sp < 0.05;
        bool flag3 = totalWorseMedian is { } tw && tw <= 0.05;
        return new Verdict(deltas, reductionMedian, deltas.Count(d => d.ValueReduction >= 0.10), signP, totalWorseMedian, p95Max, flag2, flag3, flag4);
    }

    /// 부호 검정 단측 p: P(X >= k), X ~ Binomial(n, 0.5). 정확값 (이항 계수 합).
    public static double SignTestP(int n, int k)
    {
        double sum = 0;
        double c = 1; // C(n, j), j = 0 부터
        for (int j = 0; j <= n; j++)
        {
            if (j >= k)
            {
                sum += c;
            }

            c = c * (n - j) / (j + 1);
        }

        return sum / Math.Pow(2, n);
    }

    private static string Key(string path) => path.ToUpperInvariant();

    private static double? Median(IEnumerable<double> values)
    {
        double[] v = values.Order().ToArray();
        if (v.Length == 0)
        {
            return null;
        }

        int m = v.Length / 2;
        return v.Length % 2 == 1 ? v[m] : (v[m - 1] + v[m]) / 2;
    }

    // 최근접 순위 (nearest rank): ceil(0.95 n) 번째
    private static double Percentile95(List<double> values)
    {
        double[] v = values.Order().ToArray();
        return v[(int)Math.Ceiling(0.95 * v.Length) - 1];
    }
}
