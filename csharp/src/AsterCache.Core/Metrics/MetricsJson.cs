using System.Globalization;
using AsterCache.Core.Json;

namespace AsterCache.Core.Metrics;

/// 게이트 결과의 기록 형식. 쓰기는 PyJson, 읽기는 JsonRead (System.Text.Json 기반) 를 거치는 기존 관례를 따릅니다. 파일 입출력은 호출부 몫입니다.
public static class MetricsJson
{
    public static string Dumps(GateResult r)
    {
        var o = new JObject
        {
            ["verdict"] = r.Verdict,
            ["reason"] = r.Reason,
            ["reason_code"] = r.ReasonCode,
            ["baseline_used"] = r.BaselineUsed,
            ["candidate_used"] = r.CandidateUsed,
            ["excluded"] = r.Excluded,
            ["baseline"] = Pair(r.Baseline),
            ["candidate"] = Pair(r.Candidate),
            ["visual"] = r.Visual,
            ["visual_observed_count"] = r.VisualObservedCount,
            ["worst_loss_ms"] = r.WorstLossMs,
            ["metric_source"] = r.MetricSource,
            ["ci"] = Ci(r.Ci),
            ["ci_not_computed"] = r.Ci is null ? GateResult.CiNotComputed : null,
        };
        return PyJson.Dumps(o, ensureAscii: false);
    }

    public static GateResult Parse(string text)
    {
        JObject o = JsonRead.Parse(text) as JObject ?? throw new PyValueException("gate result is not an object");
        string verdict = Str(o, "verdict");
        if (verdict is not (ResultGate.Unverified or ResultGate.Adopt or ResultGate.Reject))
        {
            throw new PyValueException("unknown verdict: " + verdict);
        }

        string visual = Str(o, "visual");
        if (visual is not ("observed" or "unobserved"))
        {
            throw new PyValueException("unknown visual: " + visual);
        }

        return new GateResult(verdict, Str(o, "reason"), Int(o, "baseline_used"), Int(o, "candidate_used"), Int(o, "excluded"),
            ReadPair(o, "baseline"), ReadPair(o, "candidate"), visual, NullableLong(o, "worst_loss_ms"),
            o.TryGetValue("visual_observed_count", out _) ? Int(o, "visual_observed_count") : 0,
            o.TryGetValue("reason_code", out _) ? Str(o, "reason_code") : string.Empty,
            ReadCi(o), o.TryGetValue("metric_source", out _) ? Str(o, "metric_source") : "t_ready_ms"); // 옛 결과에는 CI 와 관측 개수 키가 없습니다
    }

    /// 로딩 표본 기록 파일 이름 (데이터 폴더). 서비스가 쓰고 트레이가 읽습니다.
    public const string SamplesFile = "benchmark_samples.json";

    public static string DumpSamples(IEnumerable<LoadSample> samples)
    {
        var list = new List<object?>();
        foreach (LoadSample s in samples)
        {
            list.Add(new JObject
            {
                ["game_key"] = s.GameKey,
                ["scenario"] = s.Scenario.ToString(),
                ["arm"] = s.Arm.ToString(),
                ["elapsed"] = s.Elapsed.ToString(),
                ["elapsed_basis"] = s.ElapsedBasis,
                ["t_ready_ms"] = s.TReadyMs,
                ["t_visual_ms"] = s.TVisualMs,
                ["physical_hdd_read_bytes"] = s.PhysicalHddReadBytes,
                ["contended"] = s.Contended,
                ["t_total_ms"] = s.TTotalMs,
                ["game_build"] = s.GameBuild,
                ["cache_state"] = s.CacheState.ToString(),
                ["probe_ready_ms"] = s.ProbeReadyMs,
            });
        }

        return PyJson.Dumps(new JObject { ["samples"] = list }, indent: 2, ensureAscii: false) + "\n";
    }

    /// 표본 기록 파일을 읽습니다. 파일 전체가 JSON 이 아니거나 목록이 없으면 PyValueException (호출부가 파일을 처리합니다). 항목 하나가 깨졌으면 그 항목만 건너뜁니다.
    public static List<LoadSample> ParseSamples(string text)
    {
        JObject o = JsonRead.Parse(text) as JObject ?? throw new PyValueException("sample file is not an object");
        if (!o.TryGetValue("samples", out object? v) || v is not List<object?> items)
        {
            throw new PyValueException("samples must be a list");
        }

        var list = new List<LoadSample>();
        foreach (object? item in items)
        {
            try
            {
                list.Add(ParseSample(item as JObject ?? throw new PyValueException("sample is not an object")));
            }
            catch (PyValueException)
            {
                // 깨진 항목만 빼고 나머지 기록은 유지합니다
            }
        }

        return list;
    }

    private static LoadSample ParseSample(JObject o) => new(Str(o, "game_key"), Named<Scenario>(o, "scenario"), Named<Arm>(o, "arm"), Named<ElapsedBucket>(o, "elapsed"),
        NullableLong(o, "t_ready_ms"), NullableLong(o, "t_visual_ms"), NullableLong(o, "physical_hdd_read_bytes"), NullableBool(o, "contended"),
        NullableLong(o, "t_total_ms"), NullableStr(o, "game_build"), Named<CacheState>(o, "cache_state"), NullableLong(o, "probe_ready_ms"),
        NullableStr(o, "elapsed_basis") ?? "unknown"); // 기준 칸이 없는 옛 기록은 설치 후 경과로 짐작하지 않습니다

    private static T Named<T>(JObject o, string key) where T : struct, Enum =>
        Enum.TryParse(Str(o, key), out T v) && Enum.IsDefined(v) ? v : throw new PyValueException(key + " is unknown");

    private static bool? NullableBool(JObject o, string key)
    {
        if (!o.TryGetValue(key, out object? v) || v is null)
        {
            return null;
        }

        return v is bool b ? b : throw new PyValueException(key + " must be a bool or null");
    }

    private static string? NullableStr(JObject o, string key)
    {
        if (!o.TryGetValue(key, out object? v) || v is null)
        {
            return null;
        }

        return v as string ?? throw new PyValueException(key + " must be a string or null");
    }

    private static JObject? Pair(Percentiles? p) => p is null ? null : new JObject { ["p50"] = p.P50, ["p95"] = p.P95 };

    private static JObject? Ci(BootstrapIntervals? ci) => ci is null ? null : new JObject
    {
        ["method"] = "bootstrap",
        ["level"] = 0.95,
        ["iterations"] = (long)ResultGate.BootstrapIterations,
        ["p50_delta_ms"] = new JObject { ["lower"] = ci.P50DeltaMs.Lower, ["upper"] = ci.P50DeltaMs.Upper },
        ["p95_delta_ms"] = new JObject { ["lower"] = ci.P95DeltaMs.Lower, ["upper"] = ci.P95DeltaMs.Upper },
    };

    private static BootstrapIntervals? ReadCi(JObject o)
    {
        if (!o.TryGetValue("ci", out object? value) || value is null)
        {
            return null;
        }

        if (value is not JObject ci || !ci.TryGetValue("method", out object? method) || method is not "bootstrap")
        {
            throw new PyValueException("ci is not a supported bootstrap interval");
        }

        BootstrapInterval ReadInterval(string key)
        {
            if (!ci.TryGetValue(key, out object? intervalValue) || intervalValue is not JObject interval)
            {
                throw new PyValueException(key + " is not an interval");
            }

            double lower = Num(interval, "lower");
            double upper = Num(interval, "upper");
            if (!double.IsFinite(lower) || !double.IsFinite(upper) || lower > upper)
            {
                throw new PyValueException(key + " is invalid");
            }

            return new BootstrapInterval(lower, upper);
        }

        return new BootstrapIntervals(ReadInterval("p50_delta_ms"), ReadInterval("p95_delta_ms"));
    }

    private static Percentiles? ReadPair(JObject o, string key)
    {
        if (!o.TryGetValue(key, out object? v) || v is null)
        {
            return null;
        }

        JObject p = v as JObject ?? throw new PyValueException(key + " is not an object");
        return new Percentiles(Num(p, "p50"), Num(p, "p95"));
    }

    private static string Str(JObject o, string key) =>
        o.TryGetValue(key, out object? v) && v is string s ? s : throw new PyValueException(key + " must be a string");

    private static int Int(JObject o, string key)
    {
        if (!o.TryGetValue(key, out object? v) || v is not long l)
        {
            throw new PyValueException(key + " must be an integer");
        }

        // 개수는 0 이상이고 int 범위 안이어야 합니다 (checked 캐스트의 OverflowException 대신 같은 예외 형식으로 거부)
        if (l < 0 || l > int.MaxValue)
        {
            throw new PyValueException(key + " must be a non-negative int range value");
        }

        return (int)l;
    }

    private static long? NullableLong(JObject o, string key)
    {
        if (!o.TryGetValue(key, out object? v) || v is null)
        {
            return null;
        }

        return v is long l ? l : throw new PyValueException(key + " must be an integer or null");
    }

    private static double Num(JObject o, string key) =>
        o.TryGetValue(key, out object? v) && v is long or double
            ? Convert.ToDouble(v, CultureInfo.InvariantCulture)
            : throw new PyValueException(key + " must be a number");
}
