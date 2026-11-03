using System.Diagnostics;
using System.Globalization;
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

    /// 이전 존이 다른 파일에 막혀 새 존으로 계획을 다시 정했는가, 그때 옮겨 보관한 끝난 저널 (없으면 null).
    public bool ZoneRetargeted { get; set; }

    public string? JournalArchivedTo { get; set; }

    /// order=source 를 요청했을 때만 채움: 실제로 쓴 순서와, target 으로 바꿨다면 그 이유.
    public string? OrderUsed { get; set; }

    public string? OrderFallbackReason { get; set; }

    public long? ShaChecked { get; set; }

    public List<string>? ShaMismatch { get; set; }

    /// 실행했을 때만 JSON 에 나오는 단계별 누적 시간.
    public ApplyTimings Timings { get; } = new();

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
        if (ZoneRetargeted)
        {
            o.Add("zone_retargeted", true);
            o.Add("journal_archived_to", JournalArchivedTo);
        }

        if (OrderUsed is not null)
        {
            o.Add("order_used", OrderUsed);
            if (OrderFallbackReason is not null)
            {
                o.Add("order_fallback_reason", OrderFallbackReason);
            }
        }

        if (ShaChecked is not null)
        {
            o.Add("sha_checked", ShaChecked);
            o.Add("sha_mismatch", ShaMismatch!.Cast<object?>().ToList());
        }

        if (Executed)
        {
            o.Add("timings", Timings.ToJson());
        }

        return o;
    }
}

/// 실행 경로의 단계별 누적 시간 (Stopwatch 틱). open = 파일 열기·extent 조회, move = FSCTL_MOVE_FILE 호출 합, other = total 에서 나머지를 뺀 값.
public sealed class ApplyTimings
{
    private readonly long _start = Stopwatch.GetTimestamp();
    private long _totalTicks;

    internal long OpenTicks;
    internal long DigestBeforeTicks;
    internal long MoveTicks;
    internal long MoveWallTicks;
    private readonly object _moveGate = new();
    private int _moving;
    private long _movingSince;
    internal long DigestAfterTicks;
    internal long JournalTicks;

    public long Tasks { get; internal set; }

    public long Files { get; internal set; }

    /// 저널을 디스크까지 내린 횟수 (진단용, JSON 에는 넣지 않음).
    public int JournalFlushes { get; internal set; }

    public double OpenS => Seconds(OpenTicks);

    public double DigestBeforeS => Seconds(DigestBeforeTicks);

    /// 모든 작업자의 이동 호출 시간을 더한 값 (동시 실행이면 벽시계보다 큼).
    public double MoveS => Seconds(MoveTicks);

    /// 이동 호출이 하나라도 진행 중이던 벽시계 시간 (순차면 MoveS 와 같음).
    public double MoveWallS => Seconds(MoveWallTicks);

    public double DigestAfterS => Seconds(DigestAfterTicks);

    public double JournalS => Seconds(JournalTicks);

    public double TotalS => Seconds(_totalTicks);

    public double OtherS => Seconds(_totalTicks - OpenTicks - DigestBeforeTicks - MoveTicks - DigestAfterTicks - JournalTicks);

    /// 사람이 읽는 한 줄 꼬리: " (open X s, digest Y s, move Z s)".
    public string Summary => string.Create(CultureInfo.InvariantCulture, $" (open {OpenS:0.0} s, digest {DigestBeforeS + DigestAfterS:0.0} s, move {MoveS:0.0} s)");

    internal void MoveBegin()
    {
        lock (_moveGate)
        {
            if (_moving++ == 0)
            {
                _movingSince = Stopwatch.GetTimestamp();
            }
        }
    }

    internal void MoveEnd(long startTicks)
    {
        long now = Stopwatch.GetTimestamp();
        _ = Interlocked.Add(ref MoveTicks, now - startTicks);
        lock (_moveGate)
        {
            if (--_moving == 0)
            {
                MoveWallTicks += now - _movingSince;
            }
        }
    }

    internal void Stop() => _totalTicks = Stopwatch.GetTimestamp() - _start;

    internal static T Time<T>(ref long ticks, Func<T> f)
    {
        long s = Stopwatch.GetTimestamp();
        try
        {
            return f();
        }
        finally
        {
            _ = Interlocked.Add(ref ticks, Stopwatch.GetTimestamp() - s);
        }
    }

    internal static void Time(ref long ticks, Action f) => Time(ref ticks, () =>
    {
        f();
        return 0;
    });

    public JObject ToJson() => new()
    {
        { "open_s", Math.Round(OpenS, 3) },
        { "digest_before_s", Math.Round(DigestBeforeS, 3) },
        { "move_s", Math.Round(MoveS, 3) },
        { "move_wall_s", Math.Round(MoveWallS, 3) },
        { "digest_after_s", Math.Round(DigestAfterS, 3) },
        { "journal_s", Math.Round(JournalS, 3) },
        { "other_s", Math.Round(OtherS, 3) },
        { "total_s", Math.Round(TotalS, 3) },
        { "tasks", Tasks },
        { "files", Files },
    };

    private static double Seconds(long ticks) => (double)ticks / Stopwatch.Frequency;
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
