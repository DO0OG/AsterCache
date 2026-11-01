using AsterCache.Core.Json;

namespace AsterCache.Core.Apply;

public sealed record FailedStep(long Idx, string Path, string Reason);

/// SHA 표본이 이동 전과 달라진 파일이 있음 (Python RuntimeError('sha_mismatch: ...')). 저널에는 end 행이 이미 남았습니다.
public sealed class ShaMismatchException : Exception
{
    public ShaMismatchException(IReadOnlyList<string> paths)
        : base("sha_mismatch: [" + string.Join(", ", paths.Select(p => "'" + p + "'")) + "]")
    {
        Paths = paths;
    }

    public IReadOnlyList<string> Paths { get; }
}

/// apply 결과 (Python rep 사전). <see cref="ToJson"/> 의 키 순서는 Python 과 같습니다. sha_* 는 실행해서 끝까지 갔을 때만 있습니다.
public sealed class ApplyReport
{
    public long? ZoneStartLcn { get; set; }

    public long TasksTotal { get; set; }

    public long TasksTodo { get; set; }

    public long BytesTodo { get; set; }

    /// 경로 → 이유, 경로 순.
    public List<KeyValuePair<string, string>> SkippedFiles { get; } = [];

    public bool Executed { get; set; }

    public long MovedBytes { get; set; }

    public List<FailedStep> Failed { get; } = [];

    public string? Stopped { get; set; }

    public bool DryRun { get; set; }

    public bool PlanSaved { get; set; }

    public long? ShaChecked { get; set; }

    public List<string>? ShaMismatch { get; set; }

    public JObject ToJson()
    {
        var skipped = new JObject();
        foreach ((string path, string why) in SkippedFiles)
        {
            skipped.Add(path, why);
        }

        var o = new JObject
        {
            { "zone_start_lcn", ZoneStartLcn },
            { "tasks_total", TasksTotal },
            { "tasks_todo", TasksTodo },
            { "bytes_todo", BytesTodo },
            { "skipped_files", skipped },
            { "executed", Executed },
            { "moved_bytes", MovedBytes },
            { "failed", Failed.Select(f => (object?)new JObject { { "idx", f.Idx }, { "path", f.Path }, { "reason", f.Reason } }).ToList() },
            { "stopped", Stopped },
            { "dry_run", DryRun },
            { "plan_saved", PlanSaved },
        };
        if (ShaChecked is not null)
        {
            o.Add("sha_checked", ShaChecked);
            o.Add("sha_mismatch", ShaMismatch!.Cast<object?>().ToList());
        }

        return o;
    }
}

public sealed record SkippedStep(long Idx, string Reason);

/// revert 결과. stopped 는 쓰기 상한에 걸렸을 때만 JSON 에 나옵니다.
public sealed class RevertReport
{
    public bool Executed { get; set; }

    public long Restored { get; set; }

    public List<SkippedStep> Skipped { get; } = [];

    public long MovedBytes { get; set; }

    public string? Stopped { get; set; }

    public JObject ToJson()
    {
        var o = new JObject
        {
            { "executed", Executed },
            { "restored", Restored },
            { "skipped", Skipped.Select(s => (object?)new JObject { { "idx", s.Idx }, { "reason", s.Reason } }).ToList() },
            { "moved_bytes", MovedBytes },
        };
        if (Stopped is not null)
        {
            o.Add("stopped", Stopped);
        }

        return o;
    }
}
