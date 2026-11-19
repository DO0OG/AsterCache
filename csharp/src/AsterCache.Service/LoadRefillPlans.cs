using AsterCache.Core.Apply;
using AsterCache.Core.Plan;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Tracer;

namespace AsterCache.Service;

/// 저장된 플레이 집계에서 로드 구간 블록 목록을 만들어 저장하고, 단계 스트림이 쓸 때 읽기 목록으로 바꿉니다 (순수 계산은 LoadRefillPlanner).
internal static class LoadRefillPlans
{
    public static string PathOf(GameSettings g) => LoadRefillStore.PathFor(AutoTracer.RawTracePath(g));

    /// 지금 있고 허용 루트 안이며 가변 파일이 아닌 파일의 크기·수정 시각 (아니면 null).
    public static Func<string, LoadRefillFileStat?> StatOf(GameSettings g)
    {
        IReadOnlyDictionary<string, string> marked = AutoTracer.ReadVolatile(AutoTracer.VolatilePath(g));
        return path =>
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists && VolatileFiles.Reason(path, marked) is null && PlanGuards.Inside(path, g.AllowedRoots, RealPaths.Real)
                    ? new LoadRefillFileStat(info.Length, info.LastWriteTimeUtc.Ticks)
                    : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        };
    }

    /// 원시 트레이스 옆에 남은 플레이 집계 (직접·단독·회차별, 지문이 맞는 것만, 같은 세션은 한 번)와 각 집계의 실행 파일 시작 시각.
    /// 시작 시각은 집계가 직접 기록한 것을 쓰고, 옛 집계에는 없으므로 회차 파일의 단계 표지 (첫 시작 하나씩, 첫 번째 표지 제외) 로 대신합니다.
    public static List<LoadRefillSource> Sources(string raw)
    {
        var sources = new List<LoadRefillSource>();
        var sessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(IoSummary? summary, string? runPath)
        {
            if (summary is null || !sessions.Add(summary.SessionId))
            {
                return;
            }

            IReadOnlyList<long> starts = summary.TargetStartsUs is { Count: > 0 } own ? own : MarkStarts(summary, runPath);
            sources.Add(new LoadRefillSource(summary, starts));
        }

        if (File.Exists(IoSummaryStore.RunPath(raw)))
        {
            Add(IoSummaryStore.ReadForRun(raw), null);
        }

        for (int number = 1; number <= AutoTracer.MaxRunFiles; number++)
        {
            string run = TraceWriter.RunPath(raw, number);
            if (File.Exists(run) && File.Exists(IoSummaryStore.RunPath(run)))
            {
                Add(IoSummaryStore.ReadForRun(run), run);
            }
        }

        foreach (string standalone in IoSummaryStore.StandalonePaths(raw))
        {
            if (File.Exists(standalone))
            {
                Add(IoSummaryStore.ReadStandaloneForRaw(standalone, raw), null);
            }
        }

        return sources;
    }

    private static IReadOnlyList<long> MarkStarts(IoSummary summary, string? runPath)
    {
        if (runPath is null || summary.RawTraceZeroUs is not { } zeroUs || TraceStageIndex.ReadMarks(runPath) is not { } marks)
        {
            return [];
        }

        return [.. marks.Where(m => m.Exe.Length > 0 && m.TMs > 0).Select(m => zeroUs + m.TMs * 1000)];
    }

    /// 목록을 다시 만들어 저장합니다 (집계가 없거나 블록이 하나도 없으면 낡은 파일을 지움). 로그 한 줄을 돌려줍니다.
    public static string Rebuild(GameSettings g, DateTimeOffset now)
    {
        string path = PathOf(g);
        LoadRefillList list = LoadRefillPlanner.Collect(Sources(AutoTracer.RawTracePath(g)), StatOf(g), LoadRefillPlanner.Window, LoadRefillPlanner.CapBytes);
        if (list.Blocks.Count == 0)
        {
            File.Delete(path);
        }
        else
        {
            LoadRefillStore.Write(path, list, now);
        }

        return $"load refill list built from {list.Plays} plays ({list.PlaysWithoutStarts} without start times), {list.Loads} loads: {list.Blocks.Count} blocks/{Mib(list.Bytes)} MiB, "
            + $"{Mib(list.SkippedNoCacheBlocks * LoadBoundaries.Block)} MiB left out as read only without the cache, {Mib(list.DroppedByCapBlocks * LoadBoundaries.Block)} MiB over the {LoadRefillPlanner.CapBytes >> 20} MiB cap";
    }

    /// 저장된 목록을 지금 파일에 맞춰 읽기 목록으로 바꿉니다 (목록이 없거나 쓸 블록이 없으면 null → 지금 동작 그대로).
    /// capBytes 는 메모리 게이트가 허용하는 양과 고정 상한 중 작은 값입니다.
    public static LoadRefillPlan? Plan(GameSettings g, long capBytes, Func<string, FileLayoutInfo?>? layout, CancellationToken stop)
    {
        if (LoadRefillStore.Read(PathOf(g)) is not { Blocks.Count: > 0 } list)
        {
            return null;
        }

        List<ReadOp> ops = LoadRefillPlanner.ToOps(list, StatOf(g), Math.Min(capBytes, LoadRefillPlanner.CapBytes), layout, stop);
        return ops.Count == 0 ? null : new LoadRefillPlan(ops, list.Plays, list.Loads);
    }

    private static string Mib(long bytes) => (bytes / 1_048_576d).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
}

/// 단계 스트림이 흘릴 로드 구간 목록 (디스크 위치 순)과 그 출처 (플레이·로드 수).
public sealed record LoadRefillPlan(IReadOnlyList<ReadOp> Ops, int Plays, int Loads);
