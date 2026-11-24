using System.Collections.Concurrent;
using AsterCache.Core.Apply;
using AsterCache.Core.Plan;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Tracer;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 저장된 플레이 집계에서 로드 구간 블록 목록을 만들어 저장하고, 단계 스트림이 쓸 때 읽기 목록으로 바꿉니다 (순수 계산은 LoadRefillPlanner).
internal static class LoadRefillPlans
{
    public static string PathOf(GameSettings g) => LoadRefillStore.PathFor(AutoTracer.RawTracePath(g));

    /// 지금 있고 허용 루트 안이며 가변 파일이 아닌 파일의 크기·수정 시각 (아니면 null). includeWritten 이면 게임이 플레이 중에 스스로 쓰는 파일 (트레이스가 가변으로 표시한 것, VolatileFiles.IsGameWritten) 도 돌려줍니다:
    /// 순위 목록이 그 파일의 블록도 담을 때 쓰며, 저장한 크기·수정 시각과 지금 값이 다르면 (게임이 다시 썼으면) 그 파일의 블록은 읽을 때 빠집니다.
    public static Func<string, LoadRefillFileStat?> StatOf(GameSettings g, bool includeWritten = false)
    {
        IReadOnlyDictionary<string, string> marked = AutoTracer.ReadVolatile(AutoTracer.VolatilePath(g));
        return path =>
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists && (VolatileFiles.Reason(path, marked) is null || (includeWritten && VolatileFiles.IsGameWritten(path, marked))) && PlanGuards.Inside(path, g.AllowedRoots, RealPaths.Real)
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

    /// 플레이마다 남긴 디스크 전용 요약 (게임마다 최근 IoSummaryStore.MaxPlayDiskSummaries 개) 과 각 요약의 실행 파일 시작 시각.
    /// 순위 목록의 경기 가중 점수에만 쓰고 Sources 에는 들어가지 않습니다: 초기 로드 목록·예열 순위·캐시 없이 읽는 파일의 입력은 이 요약이 있어도 같습니다.
    public static List<LoadRefillSource> ScoreSources(string raw)
    {
        var sources = new List<LoadRefillSource>();
        foreach (string path in IoSummaryStore.PlayDiskPaths(raw))
        {
            if (File.Exists(path) && IoSummaryStore.ReadPlayDisk(path) is { } summary)
            {
                sources.Add(new LoadRefillSource(summary, summary.TargetStartsUs ?? []));
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

    /// 목록 파일이 없거나 이 게임의 가장 새 집계보다 오래됐는가 (집계가 하나도 없으면 만들 것이 없으므로 false). writeTime 은 파일의 수정 시각 (없으면 null).
    public static bool IsStale(GameSettings g, Func<string, DateTimeOffset?> writeTime) => IsStale(g, writeTime, PathOf(g));

    /// 집계에서 만든 다른 목록 파일 (listPath) 이 없거나 가장 새 집계보다 오래됐는가.
    public static bool IsStale(GameSettings g, Func<string, DateTimeOffset?> writeTime, string listPath)
    {
        DateTimeOffset? newest = SummaryPaths(AutoTracer.RawTracePath(g)).Select(writeTime).Max();
        return newest is { } n && (writeTime(listPath) is not { } built || built < n);
    }

    private static IEnumerable<string> SummaryPaths(string raw) =>
        new[] { IoSummaryStore.RunPath(raw) }
            .Concat(Enumerable.Range(1, AutoTracer.MaxRunFiles).Select(n => IoSummaryStore.RunPath(TraceWriter.RunPath(raw, n))))
            .Concat(IoSummaryStore.StandalonePaths(raw));

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

    /// 플레이 전체 기록의 블록 순위 목록 파일 (로드 목록 옆, 따로 둠).
    public static string RankedPathOf(GameSettings g) => LoadRefillStore.RankedPathFor(AutoTracer.RawTracePath(g));

    /// 순위 목록 파일이 없거나 이 게임의 가장 새 집계보다 오래됐는가 (서비스 시작 때 다시 만들지 정함, 집계가 없으면 false).
    public static bool RankedIsStale(GameSettings g, Func<string, DateTimeOffset?> writeTime) => IsStale(g, writeTime, RankedPathOf(g)); // 디스크 전용 요약은 입력으로 보지 않음: 그 점수는 요약을 저장할 때 RescoreRanked 가 갱신하고, 서비스 시작 때 순위를 다시 만드는 조건은 그대로

    /// 순위 목록을 다시 만들어 저장합니다: 집계 전체 구간 (실행·로드·경기 중) 의 블록을 나온 플레이 수, 지연 합 순으로 줄 세웁니다 (집계가 없으면 낡은 파일을 지움). 로그 한 줄을 돌려줍니다.
    /// 캐시 없이만 읽힌 블록·가변 파일·허용 루트 밖은 로드 목록과 같게 빼고, 예열 목록에서 어차피 빠지는 캐시 없이 읽는 파일 (UnbufferedFiles) 과 게임 드라이브 밖 파일도 뺍니다 (순위 자리와 예산을 쓰지 않게).
    /// 스파스 파일의 블록도 같은 점수로 이 한 목록에 섞습니다: 예열은 스파스 파일을 매핑하지 않고 버퍼드 읽기로 읽으므로 (ZoneStreamer.RunPvmOrBuffered) 순위 자리와 예산을 일반 파일 블록과 같은 기준으로 다툽니다.
    /// 스파스 블록은 붙들기·다시 만지기·사다리가 어차피 건너뛰므로 (Prewarm.PlayHold 의 sparse 사유, StandbyTouch·ResidentFinder) 그쪽에 영향이 없고, 게임 중에는 Prewarm.PlaySparse 가 버퍼드 읽기로 유지합니다.
    /// includeWritten 이면 (memory.keep_hold_written_files) 게임이 플레이 중에 쓰는 파일의 블록도 같은 점수로 줄 세웁니다 (두 플레이 이상 반복해 읽힌 블록만, LoadRefillPlanner.WrittenMinPlays).
    /// includeSparse 가 거짓이면 (memory.keep_sparse 가 꺼져 있거나 memory.standby_ladder 가 꺼져 있어 스파스 블록을 유지하지 않음) 스파스 파일의 블록을 이 변경 전처럼 목록에서 뺍니다: 예열 예산을 쓰고도 게임 중에 지켜지지 않는 일이 없게.
    public static string RebuildRanked(GameSettings g, DateTimeOffset now, bool includeWritten = false, IReadOnlyList<string>? recentPlays = null, bool includeSparse = true) => RebuildRanked(g, now, path => FileGuards.IsSparse(path), includeWritten, recentPlays, includeSparse);

    internal static string RebuildRanked(GameSettings g, DateTimeOffset now, Func<string, bool> isSparse, bool includeWritten = false, IReadOnlyList<string>? recentPlays = null, bool includeSparse = true)
    {
        string path = RankedPathOf(g);
        Func<string, LoadRefillFileStat?> inner = StatOf(g, includeWritten);
        IReadOnlyDictionary<string, string> marked = includeWritten ? AutoTracer.ReadVolatile(AutoTracer.VolatilePath(g)) : new Dictionary<string, string>();
        WrittenFileHistory history = includeWritten ? WrittenFileHistory.Read(WrittenFileHistory.PathFor(AutoTracer.RawTracePath(g))) : new WrittenFileHistory();
        HashSet<string> unbuffered = UnbufferedFiles.Excluded(g);
        string drive = Path.GetPathRoot(g.OriginRoot) ?? string.Empty;
        int sparseLeftOut = 0;
        LoadRefillFileStat? Stat(string file)
        {
            if (!string.Equals(Path.GetPathRoot(file), drive, StringComparison.OrdinalIgnoreCase) || unbuffered.Contains(file) || inner(file) is not { } stat)
            {
                return null;
            }

            if (!includeSparse && isSparse(file))
            {
                sparseLeftOut++; // Collect 가 파일마다 한 번만 물으므로 파일 수
                return null;
            }

            return stat;
        }

        LoadRefillList list = LoadRefillPlanner.Collect(Sources(AutoTracer.RawTracePath(g)), Stat, null, LoadRefillPlanner.RankedCapBytes, includeWritten ? file => VolatileFiles.IsGameWritten(file, marked) : null,
            includeWritten ? file => VolatileFiles.IsGameWritten(file, marked) && inner(file) is { } now && history.IsAppendOnly(file, now.Size, recentPlays) : null, // 크기 기록이 충분하고 줄어든 적이 없는 파일은 수정된 플레이의 읽기도 셈
            ScoreSources(AutoTracer.RawTracePath(g))); // 디스크 전용 요약은 경기 가중 점수에만 더해지고 목록의 블록과 순서는 바꾸지 않음
        list = LoadRefillPlanner.WithPlayOrder(list, isSparse); // 경기 가중 점수 순서와 스파스 표지는 여기서 (게임이 꺼져 있을 때) 한 번 정해 파일에 저장: 게임 중에는 다시 계산하지 않음
        long writtenBlocks = includeWritten ? list.Blocks.Count(b => VolatileFiles.IsGameWritten(b.Path, marked)) : 0;
        HashSet<string> sparseFiles = list.SparseFiles is { } known ? [.. known] : [.. list.Files.Keys.Where(isSparse)]; // 점수 순서를 정할 때 이미 물은 표지를 다시 쓰고, 점수가 없는 목록만 지금 물음 (파일마다 한 번)
        long sparseBlocks = list.Blocks.Count(b => sparseFiles.Contains(b.Path));
        if (list.Plays == 0 && !SummaryPaths(AutoTracer.RawTracePath(g)).Any(File.Exists))
        {
            File.Delete(path); // 집계가 하나도 없음: 낡은 파일을 지움
        }
        else
        {
            LoadRefillStore.Write(path, list, now); // 쓸 집계가 없거나 블록이 없어도 다시 만들었음을 남김 (서비스 시작 때마다 다시 만들지 않게)
        }

        return $"ranked block list built from {list.Plays} plays: {list.Blocks.Count} blocks/{Mib(list.Bytes)} MiB, "
            + $"{Mib(list.SkippedNoCacheBlocks * LoadBoundaries.Block)} MiB left out as read only without the cache, {(includeSparse ? $"{Mib(sparseBlocks * LoadBoundaries.Block)} MiB in {sparseFiles.Count} sparse files" : $"{sparseLeftOut} sparse files left out")}, "
            + $"{Mib(list.DroppedByCapBlocks * LoadBoundaries.Block)} MiB over the {LoadRefillPlanner.RankedCapBytes >> 20} MiB cap"
            + (includeWritten ? $", {Mib(writtenBlocks * LoadBoundaries.Block)} MiB in game-written files" : string.Empty);
    }

    /// 디스크 전용 요약을 저장한 뒤 순위 목록의 경기 가중 점수만 갱신합니다: 저장된 순위의 블록·순서·플레이 수·지연 합과 파일의 크기·수정 시각 (목록을 만든 때의 세대) 을 그대로 두므로
    /// 그 사이 게임이 파일을 고쳐도 지금 파일의 크기·수정 시각이 옛 블록에 새로 붙지 않고, 예열 순위 (R0) 는 요약이 없을 때와 같습니다. 파일 속성을 조회하지 않고 순위 파일 하나와 요약 슬롯을 읽을 뿐입니다.
    /// 점수가 저장되지 않은 순위 (이 변경 앞의 예전 파일이거나 순위가 없음) 는 건드리지 않고 그렇다고 알립니다. 취소되면 OperationCanceledException (순위 파일은 그대로, 다음 요약이 다시 셈).
    public static string RescoreRanked(GameSettings g, DateTimeOffset now, CancellationToken ct = default)
    {
        string path = RankedPathOf(g);
        if (LoadRefillStore.Read(path) is not { PlayOrder: not null } list)
        {
            return "ranked scores not updated, no ranked list with scores yet";
        }

        List<LoadRefillSource> sources = ScoreSources(AutoTracer.RawTracePath(g));
        ct.ThrowIfCancellationRequested();
        LoadRefillList rescored = LoadRefillPlanner.Rescore(list, sources, ct);
        LoadRefillStore.Write(path, rescored, now);
        return $"ranked scores updated from {sources.Count} play summaries, blocks and file generations kept: {rescored.Blocks.Count(b => b.Extra > 0)} of {rescored.Blocks.Count} blocks got points from them";
    }

    // 마지막 예열이 읽은 순위 목록 (순위 파일 경로 → 그 파일의 길이·수정 시각과 읽은 내용).
    // 순위 파일은 게임이 끝날 때마다 새로 쓰이지만 메모리의 캐시는 마지막 예열이 읽은 선택 그대로이므로, 사다리·시작 만지기·유지 목록은 새 예열이 읽기 전에는 이 선택을 따라야 합니다.
    private sealed record RankedPin(long Length, long Ticks, LoadRefillList List);

    private static readonly ConcurrentDictionary<string, RankedPin> RankedPins = new(StringComparer.OrdinalIgnoreCase);

    // 예열 한 회가 새로 읽었지만 아직 실제로 읽지 않은 선택 (null 값 = 순위 파일이 없어짐). 스트림이 바이트를 읽으면 CommitRanked 가 기억으로 확정하고, 그 전에 건너뛰거나 취소된 회는 이전 기억이 그대로 남습니다.
    private static readonly ConcurrentDictionary<string, RankedPin?> RankedPending = new(StringComparer.OrdinalIgnoreCase);

    /// 저장된 순위 목록을 지금 파일에 맞춰 순위 순 행 (64 KiB 블록 하나씩) 으로 바꿉니다 (목록이 없거나 쓸 블록이 없으면 null → 지금 동작 그대로).
    /// 게임이 끝난 뒤 만들어 둔 파일만 읽고 .io.json 은 읽지 않습니다.
    /// 이 게임의 예열 한 회 (PrewarmRun) 안이면 파일이 바뀌었을 때 새로 읽어 이 회의 선택으로 삼고 (스트림이 읽으면 확정), 그 밖 (사다리·시작 만지기·유지·표본, 다른 게임의 예열 회 안) 이면 기억한 목록을 그대로 씁니다.
    /// 기억한 것이 없으면 (서비스를 막 시작한 때) 지금 파일을 읽어 기억합니다: 그 파일이 마지막 예열 뒤에 바뀌었다면 목록이 어긋날 수 있고, 유지는 그 경우를 스스로 알아차려 순위 없는 순서로 다시 시도합니다.
    internal static List<TraceEvent>? RankedRows(GameSettings g, CancellationToken cancellationToken = default) =>
        WithRankedPin(g, pin => ToRowsOf(g, pin, cancellationToken));

    /// 유지 회차·붙들기 목록이 쓰는 중요도 순서의 행: 순위 행 (RankedRows 와 같음) 과 경기 가중 점수 순의 행과 스파스 파일 표지 (PlayRanking.PlayReorder 가 앞 2 GiB 를 정함).
    /// 순위 파일에 점수 순서가 없으면 (구간을 모르는 예전 목록) null → 지금 순서 그대로. 점수 순서와 스파스 표지는 파일에 저장된 것을 읽을 뿐이라 정렬도 파일 속성 조회도 하지 않습니다 (파일 대조는 RankedRows 와 같은 크기·수정 시각 조회 한 번).
    internal static PlayRankedRows? PlayRankedRows(GameSettings g, CancellationToken cancellationToken = default) =>
        WithRankedPin(g, pin => PlayRowsOf(g, pin, cancellationToken));

    // 저장된 순위 목록의 기억 (RankedPins·RankedPending 규칙은 RankedRows 의 설명 그대로) 을 구해 make 로 행을 만듭니다.
    private static T? WithRankedPin<T>(GameSettings g, Func<RankedPin, T?> make)
        where T : class
    {
        string path = RankedPathOf(g);
        RankedPins.TryGetValue(path, out RankedPin? pin);
        bool inRun = PrewarmRun.ActiveFor(g.Name);
        if (pin is null || inRun)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    if (inRun)
                    {
                        RankedPending[path] = null;
                    }

                    return inRun ? null : pin is null ? null : make(pin);
                }

                RankedPin? current = inRun && RankedPending.TryGetValue(path, out RankedPin? pending) ? pending : pin;
                if (current is null || current.Length != info.Length || current.Ticks != info.LastWriteTimeUtc.Ticks)
                {
                    current = LoadRefillStore.Read(path) is { } read ? new RankedPin(info.Length, info.LastWriteTimeUtc.Ticks, read) : null;
                    if (inRun)
                    {
                        RankedPending[path] = current;
                    }
                    else if (current is not null)
                    {
                        RankedPins[path] = current; // 기억이 없던 때만 여기 옴: 지금 파일을 기억
                    }
                }

                pin = current;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        return pin is null ? null : make(pin);
    }

    private static PlayRankedRows? PlayRowsOf(GameSettings g, RankedPin pin, CancellationToken cancellationToken)
    {
        if (pin.List is not { PlayOrder: { } order, SparseFiles: { } sparseFiles } list || list.Blocks.Count == 0)
        {
            return null;
        }

        Func<string, LoadRefillFileStat?> current = StatOf(g, includeWritten: true);
        var valid = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase); // 두 순서가 파일 대조를 나눠 씀
        List<TraceEvent> ranked = LoadRefillPlanner.ToRows(list, current, cancellationToken, valid);
        List<TraceEvent> byScore = LoadRefillPlanner.ToRows(list, order.Select(i => list.Blocks[i]), current, cancellationToken, valid);
        if (g.SkipSparse)
        {
            ranked = [.. ranked.Where(r => !sparseFiles.Contains(r.Path))]; // 스파스 구간을 메모리 예열에서 뺀 게임: ToRowsOf 와 같은 제외 (저장된 표지로)
            byScore = [.. byScore.Where(r => !sparseFiles.Contains(r.Path))];
        }

        return ranked.Count == 0 ? null : new PlayRankedRows(ranked, byScore, sparseFiles);
    }

    private static List<TraceEvent>? ToRowsOf(GameSettings g, RankedPin pin, CancellationToken cancellationToken)
    {
        if (pin.List.Blocks.Count == 0)
        {
            return null;
        }

        List<TraceEvent> rows = LoadRefillPlanner.ToRows(pin.List, StatOf(g, includeWritten: true), cancellationToken); // 순위 목록이 담은 파일만 나옴 (게임이 쓰는 파일은 memory.keep_hold_written_files 가 켜진 때 만든 목록에만 있음)
        if (g.SkipSparse)
        {
            // 스파스 구간을 메모리 예열에서 뺀 게임 (SparseWarmExclusion): 어차피 빠질 스파스 블록이 예산을 쓰지 않게 순위 행에서도 뺌
            var sparse = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            rows = [.. rows.Where(r => !(sparse.TryGetValue(r.Path, out bool s) ? s : sparse[r.Path] = FileGuards.IsSparse(r.Path)))];
        }

        return rows.Count == 0 ? null : rows;
    }

    /// 예열 스트림이 실제로 바이트를 읽었음: 이 회가 새로 읽은 순위 목록을 마지막 예열이 읽은 선택으로 확정합니다 (새로 읽은 것이 없으면 아무것도 안 함).
    internal static void CommitRanked(GameSettings g)
    {
        string path = RankedPathOf(g);
        if (RankedPending.TryRemove(path, out RankedPin? pending))
        {
            if (pending is null)
            {
                _ = RankedPins.TryRemove(path, out _);
            }
            else
            {
                RankedPins[path] = pending;
            }
        }
    }

    /// 설정에서 빠진 게임의 기억한 순위 목록을 버립니다.
    internal static void ForgetRanked(GameSettings g)
    {
        _ = RankedPins.TryRemove(RankedPathOf(g), out _);
        _ = RankedPending.TryRemove(RankedPathOf(g), out _);
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
