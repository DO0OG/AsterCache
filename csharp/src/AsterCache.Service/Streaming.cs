using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 게임 한 개의 선읽기 스트림. 시험은 가짜로 바꿉니다.
public interface IGameStreamer
{
    StreamStats Stream(GameSettings game);

    /// 게임 시작 스트림: 서비스가 끝날 때 stop 으로 멈춥니다. 기존 스트리머는 시작 전에만 취소를 확인합니다.
    StreamStats Stream(GameSettings game, CancellationToken stop) => stop.IsCancellationRequested ? new StreamStats(0, 0, 0, 0, Aborted: true) : Stream(game);

    /// 묶음 사이에서 stop 으로 멈출 수 있는 스트림: (읽은 만큼의 통계, 끝까지 읽었는가). progress 는 묶음마다 (읽은 바이트, 총 바이트). 기본 구현은 멈추지 않고 한 번에 읽습니다.
    (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress) => (Stream(game), true);

    /// 예열 전용 스트림. maxBytes 는 메모리·스트림 예산 안에서 읽을 전체 바이트 상한입니다.
    (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes) => Stream(game, stop, progress);

    /// 예열이 실제로 읽을 원래 범위의 바이트 (틈 제외), 모르면 null. 최초 메모리 게이트가 존 크기 대신 씁니다.
    long? PrewarmReadBytes(GameSettings game) => null;

    /// 로딩 앞부분 (tier0) 만 stop 으로 멈출 수 있게 읽습니다. 묶음 예열이 모든 게임의 앞부분을 먼저 올릴 때 씁니다. 기본은 아무것도 하지 않음.
    void StreamHead(GameSettings game, CancellationToken stop)
    {
    }

    /// 묶음별 진행을 알리는 로딩 앞부분 스트림. 기존 스트리머는 진행 알림 없이 동작합니다.
    void StreamHead(GameSettings game, CancellationToken stop, Action<long, long>? progress) => StreamHead(game, stop);

    /// 로딩 앞부분의 바이트 (원시 트레이스에 t_ms 가 있어 경계를 계산했을 때), 아니면 null.
    long? LoadHeadBytes(GameSettings game) => null;
}

/// 게임의 .prefetch.json 을 Python prefetch.cli 기본값 (physical, 간격 0, 파일 30% 이상이면 통째) 으로 스케줄해 PrefetchVirtualMemory 로 읽습니다.
/// 예산은 계획의 존 크기 (계획이 없거나 존이 0 이면 추적의 총 바이트) 이고, 게임별 max_stream_mib 가 상한입니다. 고정 1 GiB 는 더 큰 핫 집합을 잘랐습니다.
public sealed class PrefetchStreamer : IGameStreamer
{
    /// 계획의 존 크기 (바이트). 계획 파일이 없거나 읽을 수 없으면 null, 존이 비어 있으면 0.
    /// 잔여 재계획은 Zone 이 새로 옮길 구간뿐이라 (전부 제자리면 0), 핫셋 전체인 task 합계를 돌려줍니다.
    public static long? ZoneBytes(GameSettings game) => ZoneBytes(game, true);

    // withWarmOnly: 예열 전용 옆 파일 (가변 파일 범위) 까지 센 예열량. 시작 스트림의 예산은 false (계획 범위만).
    private static long? ZoneBytes(GameSettings game, bool withWarmOnly)
    {
        try
        {
            PlanFile plan = PlanFile.Load(game.Plan);
            long planBytes = (plan.IsResidual ? plan.Tasks.Sum(t => t.Clusters) : plan.Zone.Clusters) * plan.ClusterBytes;
            long plannedWarmBytes = 0;
            long volatileBytes = 0;
            try
            {
                if (File.Exists(game.Trace))
                {
                    IReadOnlyDictionary<string, string> marked = AutoTracer.ReadVolatile(AutoTracer.VolatilePath(game));
                    foreach (TraceEvent row in LoadRows(game, withWarmOnly))
                    {
                        if (IsVolatilePath(row.Path, marked))
                        {
                            volatileBytes += row.Length;
                        }
                        else
                        {
                            plannedWarmBytes += row.Length;
                        }
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
            {
                // 현재 예열 목록을 읽지 못하면 계획의 존 예산을 유지합니다.
            }

            return Math.Max(planBytes, plannedWarmBytes) + volatileBytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
    }

    /// 이번에 읽을 바이트의 예산: 존 크기 (없거나 0 이면 추적 총 바이트) 를 max_stream_mib 로 자른 값. max_stream_mib 를 명시하지 않았으면 존 전체 (GameSettings.StreamCapBytes).
    public static long Budget(GameSettings game, long? zoneBytes, long traceBytes) =>
        zoneBytes is > 0 ? Math.Min(game.StreamCapBytes(zoneBytes.Value), zoneBytes.Value) : Math.Min((long)game.MaxStreamMib << 20, traceBytes);

    /// 접근 순서 앞쪽 이만큼 (게임 시작에 먼저 필요한 부분) 을 먼저 읽고 나머지는 물리 순서로. 원시 트레이스에 t_ms 가 없을 때의 값 (있으면 로딩 경계 전 tier0 바이트).
    public const long HeadBytes = 256L << 20;

    /// 이 게임의 앞부분 바이트: 로딩 경계의 tier0 (0 이면 옛 값), 경계를 모르면 HeadBytes.
    public static long HeadBytesOf(GameSettings game) => LoadTiers.For(game)?.Tier0Bytes is > 0 and long tier0 ? tier0 : HeadBytes;

    public long? LoadHeadBytes(GameSettings game) => LoadTiers.For(game)?.Tier0Bytes is > 0 and long tier0 ? tier0 : null;

    public long? PrewarmReadBytes(GameSettings game) => ScheduleOps(game, true).Ops.Sum(o => o.Length);

    /// 접근 순서 앞쪽 HeadBytesOf 만큼만 읽습니다 (스케줄러가 앞에 둔 부분과 같음).
    public void StreamHead(GameSettings game, CancellationToken stop) => StreamHead(game, stop, null);

    public void StreamHead(GameSettings game, CancellationToken stop, Action<long, long>? progress) => Batches(Head(Ops(game), HeadBytesOf(game)), stop, progress);

    /// 예열이 끝난 뒤 버퍼드로 한 번 더 읽는 앞쪽 양: 스탠바이 목록에서 접근 순서 앞부분이 먼저 밀려나지 않게 최신으로 만듭니다.
    public const long RefreshBytes = 64L << 20;

    /// 게임 시작과 함께 도는 동시 스트림: 멈추지 않고 끝까지 (묶음 경로는 예열과 같음).
    public StreamStats Stream(GameSettings game) => Batches(Ops(game), CancellationToken.None, null).Stats;

    /// 게임 시작 스트림은 시작 예열과 겹치지 않게 갱신 읽기를 생략하고, 서비스 취소를 묶음 읽기에 전달합니다.
    public StreamStats Stream(GameSettings game, CancellationToken stop) => Batches(Ops(game), stop, null).Stats;

    /// 예열용: 64 MiB 묶음 (FolderWarm.Read) 마다 stop 을 보고 진행을 알립니다. 끝까지 읽었으면 앞쪽 RefreshBytes 를 버퍼드로 한 번 더 읽습니다.
    public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
    {
        List<ReadOp> ops = Ops(game);
        (StreamStats Stats, bool Complete) r = Batches(ops, stop, progress);
        if (r.Complete)
        {
            StreamStats refresh = Refresh(ops, stop);
            if (stop.IsCancellationRequested || refresh.Aborted)
            {
                return (r.Stats, false);
            }
        }

        return r;
    }

    /// 예열 전용 범위에 작은 논리 틈을 예산 안에서 더해 읽습니다. 시작 스트림·표본·고정은 Ops 의 원래 범위를 씁니다.
    public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes)
    {
        (List<ReadOp> ops, long gapBytes, bool fitsBudget) = PrewarmOps(game, maxBytes);
        if (!fitsBudget || stop.IsCancellationRequested)
        {
            return (new StreamStats(0, 0, 0, 0, Aborted: true), false);
        }

        progress?.Invoke(0, ops.Sum(op => op.Length));
        if (stop.IsCancellationRequested)
        {
            return (new StreamStats(0, 0, 0, 0, Aborted: true), false);
        }

        (StreamStats Stats, bool Complete) r = Batches(ops, stop, progress);
        if (r.Complete)
        {
            StreamStats refresh = Refresh(ops, stop);
            if (stop.IsCancellationRequested || refresh.Aborted)
            {
                return (r.Stats with { Aborted = true, GapBytes = gapBytes }, false);
            }
        }

        bool complete = r.Complete && !stop.IsCancellationRequested;
        return (r.Stats with { Aborted = r.Stats.Aborted || !complete, GapBytes = gapBytes }, complete);
    }

    internal static StreamStats Refresh(IReadOnlyList<ReadOp> ops, CancellationToken stop) =>
        ZoneStreamer.Run(Head(ops, RefreshBytes), StreamMethod.Read, ct: stop);

    private static (StreamStats Stats, bool Complete) Batches(List<ReadOp> ops, CancellationToken stop, Action<long, long>? progress)
    {
        long total = ops.Sum(o => o.Length);
        long done = 0;
        (StreamStats Stats, bool Complete) result = FolderWarm.Read(ops, stop, batch =>
        {
            StreamStats s = ZoneStreamer.Run(batch, StreamMethod.Pvm, ct: stop);
            done += s.Bytes;
            progress?.Invoke(done, total);
            return s;
        });
        return stop.IsCancellationRequested
            ? (result.Stats with { Aborted = true }, false)
            : result;
    }

    /// 순서 목록의 앞에서 bytes 만큼 (마지막 op 는 잘라서).
    public static List<ReadOp> Head(IReadOnlyList<ReadOp> ops, long bytes)
    {
        var head = new List<ReadOp>();
        long left = bytes;
        foreach (ReadOp op in ops)
        {
            if (left <= 0)
            {
                break;
            }

            head.Add(op.Length <= left ? op : new ReadOp(op.Path, op.Offset, left));
            left -= op.Length;
        }

        return head;
    }

    /// 게임의 선읽기 순서 목록 (허용 루트 검사 포함). 스트림·식음 확인·고정이 같은 목록을 씁니다.
    public static List<ReadOp> Ops(GameSettings game)
    {
        return ScheduleOps(game, false).Ops;
    }

    /// 예열 스트림에만 틈을 더하고, 남은 예산이 없으면 원래 범위만 유지합니다.
    /// 원래 범위는 존 예산으로 이미 골라졌고, totalBudgetBytes (유효 존 상한·스트림 상한·가용 RAM 중 작은 값) 에서 그 범위를 뺀 나머지만 틈에 씁니다.
    internal static (List<ReadOp> Ops, long GapBytes, bool FitsBudget) PrewarmOps(GameSettings game, long totalBudgetBytes)
    {
        (List<ReadOp> original, _) = ScheduleOps(game, true);
        (List<ReadOp> expanded, long gapBytes, bool fitsBudget) = FillPrewarmGaps(original, Math.Max(0, totalBudgetBytes),
            p => File.Exists(p) ? new FileInfo(p).Length : 0);
        if (!fitsBudget || gapBytes == 0)
        {
            return (expanded, gapBytes, fitsBudget);
        }

        (List<ReadOp> ordered, _) = Scheduler.Schedule(expanded.Select(o => new ReadRange(o.Path, o.Offset, o.Length)), "physical", null, 0,
            FileLayout.Get, p => File.Exists(p) ? new FileInfo(p).Length : 0, fullFileRatio: 2, headBytes: HeadBytesOf(game));
        return (ordered, gapBytes, true);
    }

    /// 작은 논리 틈을 모두 더했을 때 예산을 넘으면 256 KiB 미만만 다시 시도하고, 그것도 넘으면 원래 범위를 반환합니다.
    internal static (List<ReadOp> Ops, long GapBytes, bool FitsBudget) FillPrewarmGaps(
        IReadOnlyList<ReadOp> ops, long totalBudgetBytes, Func<string, long> fileSize)
    {
        List<ReadOp> original = [.. ops];
        long baseBytes = 0;
        foreach (ReadOp op in original)
        {
            if (op.Length > long.MaxValue - baseBytes)
            {
                return (original, 0, false);
            }

            baseBytes += op.Length;
        }

        if (totalBudgetBytes < baseBytes)
        {
            return (original, 0, true);
        }

        var candidates = new List<(int Index, long Bytes)>();
        foreach (var group in original.Select((op, index) => (op, index)).GroupBy(x => x.op.Path, StringComparer.OrdinalIgnoreCase))
        {
            long size;
            try
            {
                size = fileSize(group.Key);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }

            var sorted = group.OrderBy(x => x.op.Offset).ToList();
            if (size <= 0 || sorted.Any(x => x.op.Offset < 0 || x.op.Length <= 0 || x.op.Offset > size - x.op.Length))
            {
                continue;
            }

            long end = sorted[0].op.Offset + sorted[0].op.Length;
            for (int i = 1; i < sorted.Count; i++)
            {
                (ReadOp next, int index) = sorted[i];
                long nextEnd = next.Offset + next.Length;
                if (next.Offset > end)
                {
                    long gap = next.Offset - end;
                    if (gap < Scheduler.Mib && end <= size && nextEnd <= size)
                    {
                        candidates.Add((index, gap));
                    }
                }

                end = Math.Max(end, nextEnd);
            }
        }

        long remaining = totalBudgetBytes - baseBytes;
        List<(int Index, long Bytes)> selected = Fits(candidates, remaining)
            ? candidates
            : candidates.Where(x => x.Bytes < Scheduler.Mib / 4).ToList();
        if (!Fits(selected, remaining))
        {
            selected = [];
        }

        if (selected.Count == 0)
        {
            return (original, 0, true);
        }

        HashSet<int> bridge = selected.Select(x => x.Index).ToHashSet();
        var expanded = new List<(int Index, ReadOp Op)>();
        foreach (var group in original.Select((op, index) => (op, index)).GroupBy(x => x.op.Path, StringComparer.OrdinalIgnoreCase))
        {
            var sorted = group.OrderBy(x => x.op.Offset).ToList();
            ReadOp current = sorted[0].op;
            int firstIndex = sorted[0].index;
            long end = current.Offset + current.Length;
            foreach ((ReadOp next, int index) in sorted.Skip(1))
            {
                long nextEnd = next.Offset + next.Length;
                long gap = next.Offset - end;
                if (gap < 0)
                {
                    end = Math.Max(end, nextEnd);
                }
                else if (gap > 0 && bridge.Contains(index))
                {
                    end = Math.Max(end, nextEnd);
                }
                else
                {
                    expanded.Add((firstIndex, new ReadOp(current.Path, current.Offset, end - current.Offset)));
                    current = next;
                    firstIndex = index;
                    end = nextEnd;
                }
            }

            expanded.Add((firstIndex, new ReadOp(current.Path, current.Offset, end - current.Offset)));
        }

        return (expanded.OrderBy(x => x.Index).Select(x => x.Op).ToList(), selected.Sum(x => x.Bytes), true);
    }

    private static bool Fits(IReadOnlyList<(int Index, long Bytes)> gaps, long budget)
    {
        long total = 0;
        foreach ((_, long bytes) in gaps)
        {
            if (bytes > budget - total)
            {
                return false;
            }

            total += bytes;
        }

        return true;
    }

    // 선읽기 목록의 범위. withWarmOnly 이면 예열 전용 옆 파일 (가변 파일 범위) 도 합칩니다.
    private static List<TraceEvent> LoadRows(GameSettings game, bool withWarmOnly)
    {
        Func<string, long?> size = p => File.Exists(p) ? new FileInfo(p).Length : null;
        List<TraceEvent> rows = TraceLoader.LoadRanges(game.Trace, game.OriginRoot, RealPaths.Resolve, size);
        string side = PlanOutput.WarmOnlyPath(game.Trace);
        if (withWarmOnly && File.Exists(side))
        {
            rows.AddRange(TraceLoader.LoadRanges(side, game.OriginRoot, RealPaths.Resolve, size));
        }

        return rows;
    }

    private static (List<ReadOp> Ops, long BudgetBytes) ScheduleOps(GameSettings game, bool withWarmOnly)
    {
        if (!PlanGuards.Inside(game.OriginRoot, game.AllowedRoots, RealPaths.Real))
        {
            throw new PlanRejectedException("origin_root resolves outside allowed roots");
        }

        List<TraceEvent> rows = LoadRows(game, withWarmOnly);
        long budget = Budget(game, ZoneBytes(game, withWarmOnly), rows.Sum(r => r.Length));
        (List<ReadOp> ops, _) = Scheduler.Schedule(rows.Select(r => new ReadRange(r.Path, r.Offset, r.Length)), "physical", budget, 0,
            FileLayout.Get, p => new FileInfo(p).Length, headBytes: HeadBytesOf(game));
        return (ops, budget);
    }

    /// 고정할 파일: 선읽기 목록의 파일 중 허용 루트 안인 것 (읽기 전용으로만 쓰임). 매핑 총합이 capBytes 를 넘기 전의 앞쪽 파일만.
    public static IReadOnlyList<string> PinPaths(GameSettings game, long capBytes = long.MaxValue)
    {
        IReadOnlyDictionary<string, string> marked = AutoTracer.ReadVolatile(AutoTracer.VolatilePath(game));
        return WithinCap(Ops(game).Select(o => o.Path).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(p => !IsVolatilePath(p, marked) && PlanGuards.Inside(p, game.AllowedRoots, RealPaths.Real)),
            capBytes, p => File.Exists(p) ? new FileInfo(p).Length : 0);
    }

    private static bool IsVolatilePath(string path, IReadOnlyDictionary<string, string> marked) =>
        VolatileFiles.NameReason(path) is not null || marked.ContainsKey(path);

    /// 총 매핑 상한: 존 크기 (알면), 물리 RAM 의 1/4, 가용 RAM 에서 min_free_ram_mib 를 뺀 값 중 가장 작은 것.
    public static long PinCapBytes(long? zoneBytes, long physicalRamBytes, long availableRamMib, int minFreeRamMib) =>
        Math.Min(Math.Min(zoneBytes is > 0 ? zoneBytes.Value : long.MaxValue, physicalRamBytes / 4), Math.Max(0, availableRamMib - minFreeRamMib) << 20);

    /// 앞에서부터 크기를 더하다가 다음 파일이 상한을 넘기면 거기서 멈춥니다.
    public static IReadOnlyList<string> WithinCap(IEnumerable<string> paths, long capBytes, Func<string, long> size)
    {
        var kept = new List<string>();
        long total = 0;
        foreach (string p in paths)
        {
            long len = size(p);
            if (total + len > capBytes)
            {
                break;
            }

            total += len;
            kept.Add(p);
        }

        return kept;
    }

    /// 선읽기 목록의 앞쪽과 고르게 고른 몇 곳을 읽어 잰 결과 (CacheProbe).
    public static ProbeResult? ProbeReadMs(GameSettings game) => CacheProbe.Probe(CacheProbe.PickSamples(Ops(game), round: CacheProbe.NextRound(game.Name))); // 점검마다 다른 위치 (앞 점검의 읽기가 캐시를 데우지 않게)
}

/// 게임 시작 스트림을 건너뛸 조건의 입력 (기본은 모두 건너뛰지 않음, 시험용). Create 는 진짜 판정.
public sealed record DispatchEnv
{
    /// 새 프로세스가 이 시간보다 늦게 시작했으면 같은 시작 무리로 보고 (게임 exe 와 보조 exe 가 동시에 뜸) 이미 돌던 것으로 세지 않습니다.
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    /// 이 프로세스가 시작되기 전에 이미 돌던 이 게임의 다른 프로세스가 있는가.
    public Func<GameSettings, ProcessStart, bool> OtherRunning { get; init; } = (_, _) => false;

    /// 그 게임의 물리 디스크가 바쁜가 (읽지 못하면 바쁘지 않은 것으로 봄: 지표 없이 첫 시작 스트림까지 막지 않게).
    public Func<GameSettings, bool> DiskBusy { get; init; } = _ => false;

    /// 이 프로세스가 감지된 지 얼마나 됐는가 (Settle 안이면 막 시작한 게임: 그 게임 자신의 읽기를 디스크 바쁨으로 세지 않음).
    public Func<ProcessStart, TimeSpan> Age { get; init; } = s => System.Diagnostics.Stopwatch.GetElapsedTime(s.Timestamp);

    /// 그 게임의 핫 집합이 이미 파일 캐시에 있는가 (Prewarmer.AlreadyWarm). 기본은 모른다 (스트림).
    public Func<GameSettings, bool> IsWarm { get; init; } = _ => false;

    /// 같은 게임의 시작 스트림 사이 최소 간격 (분), 0 = 끔.
    public Func<int> RestreamCooldownMin { get; init; } = () => 0;

    public static DispatchEnv Create() => new()
    {
        OtherRunning = IsOtherRunning,
        DiskBusy = g => DiskActivity.BytesPerSecond(PlanFile.SplitDrive(g.OriginRoot)) is { } bps && bps >= DiskActivity.BusyBytesPerSecond,
    };

    /// 이미지 경로가 게임 폴더 아래인가 (다른 프로세스 판정과 종료 판정이 함께 씁니다).
    internal static bool UnderRoot(GameSettings g, string? imagePath)
    {
        string root = g.OriginRoot.TrimEnd('\\', '/') + "\\";
        return imagePath?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static bool IsGameRunning(GameSettings g) => IsGameRunning(g, ProcessSnapshot.Take(), ProcessSnapshot.ImagePath);

    internal static bool IsGameRunning(GameSettings g, IReadOnlyList<(int Pid, string Exe)> processes, Func<int, string?> imagePath)
    {
        foreach ((int pid, string exe) in processes)
        {
            if (pid == Environment.ProcessId)
            {
                continue;
            }

            bool named = g.ExeNames.Contains(exe, StringComparer.OrdinalIgnoreCase);
            string? path = named ? null : SafeImagePath(pid, imagePath);
            if (IsGameProcess(g, exe, path))
            {
                return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<int> RootPids(GameSettings g, IReadOnlyList<(int Pid, string Exe)> processes, Func<int, string?> imagePath)
    {
        List<int> rooted = [];
        foreach ((int pid, _) in processes)
        {
            if (pid != Environment.ProcessId && UnderRoot(g, SafeImagePath(pid, imagePath)))
            {
                rooted.Add(pid);
            }
        }

        return rooted;
    }

    /// 게임 실행 중 판정의 근거가 된 프로세스를 "이름(pid) 경로" 로 최대 3개 돌려줍니다 (없으면 null, 로그용).
    internal static string? DescribeBlockers(GameSettings g) => DescribeBlockers(g, ProcessSnapshot.Take(), ProcessSnapshot.ImagePath);

    internal static string? DescribeBlockers(GameSettings g, IReadOnlyList<(int Pid, string Exe)> processes, Func<int, string?> imagePath)
    {
        List<string> found = [];
        foreach ((int pid, string exe) in processes)
        {
            if (pid == Environment.ProcessId)
            {
                continue;
            }

            string? path = SafeImagePath(pid, imagePath);
            if (!IsGameProcess(g, exe, path))
            {
                continue;
            }

            found.Add(string.IsNullOrEmpty(path) ? $"{exe}({pid})" : $"{exe}({pid}) {path}");
            if (found.Count == 3)
            {
                break;
            }
        }

        return found.Count == 0 ? null : string.Join("; ", found);
    }

    private static bool IsGameProcess(GameSettings g, string exeName, string? imagePath) =>
        g.ExeNames.Contains(exeName, StringComparer.OrdinalIgnoreCase) || UnderRoot(g, imagePath);

    private static string? SafeImagePath(int pid, Func<int, string?> imagePath)
    {
        try
        {
            return imagePath(pid);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or UnauthorizedAccessException or Win32Exception)
        {
            return null;
        }
    }

    private static bool IsOtherRunning(GameSettings g, ProcessStart start)
    {
        foreach ((int pid, string exe) in ProcessSnapshot.Take())
        {
            if (pid == start.Pid || pid == Environment.ProcessId)
            {
                continue;
            }

            string? path = g.ExeNames.Contains(exe, StringComparer.OrdinalIgnoreCase) ? null : SafeImagePath(pid, ProcessSnapshot.ImagePath);
            if (IsGameProcess(g, exe, path) && StartedBeforeSettle(pid))
            {
                return true;
            }
        }

        return false;
    }

    private static bool StartedBeforeSettle(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return DateTime.Now - p.StartTime > Settle;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return false; // 그 사이 끝났거나 시작 시각을 읽을 수 없음
        }
    }
}

/// 감지된 프로세스를 게임에 대응시켜 스트림을 부릅니다. 같은 게임은 Cooldown 안에 한 번만 (런처·보조 프로세스가 연달아 떠도 중복 읽기 없음).
/// 게임 exe 가 다시 떴을 때 (게임이 이미 도는 중, 직전 스트림이 restream 간격 안, 그 디스크가 바쁨) 는 같은 데이터를 디스크에서 또 읽지 않도록 건너뜁니다. 런처 시작은 예외.
public sealed class GameDispatcher
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    private volatile IReadOnlyList<GameSettings> _games;
    private readonly IGameStreamer _streamer;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly DispatchEnv _env;
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.OrdinalIgnoreCase);

    public GameDispatcher(IReadOnlyList<GameSettings> games, IGameStreamer streamer, ILog log, Func<DateTimeOffset>? now = null, DispatchEnv? env = null)
    {
        _env = env ?? new DispatchEnv();
        _games = games;
        _streamer = streamer;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// 설정을 다시 읽었을 때 게임 목록만 바꿉니다 (쿨다운은 유지).
    public void SetGames(IReadOnlyList<GameSettings> games) => _games = games;

    // 건너뛸 이유 (로그 문구), 없으면 null. 싼 판정부터.
    private string? RestreamSkip(GameSettings g, ProcessStart start, DateTimeOffset now)
    {
        int min = _env.RestreamCooldownMin();
        if (min > 0 && _last.TryGetValue(g.Name, out DateTimeOffset prev) && now - prev < TimeSpan.FromMinutes(min))
        {
            return $"last stream {(int)(now - prev).TotalMinutes} min ago, within restream cooldown of {min} min";
        }

        if (_env.OtherRunning(g, start))
        {
            return "game already running";
        }

        if (_env.Age(start) < DispatchEnv.Settle)
        {
            return _env.IsWarm(g) ? "already warm" : null; // 막 시작한 게임: 바쁨 검사 대신 캐시 점검, 차가우면 스트림
        }

        return _env.DiskBusy(g) ? "drive busy" : null;
    }

    /// 스트림을 불렀으면 true. 스트림 실패는 로그만 남기고 서비스를 멈추지 않습니다.
    public bool Handle(ProcessStart start, CancellationToken stop = default)
    {
        GameSettings? g = _games.FirstOrDefault(x => x.ExeNames.Contains(start.ExeName, StringComparer.OrdinalIgnoreCase));
        if (g is null)
        {
            return false;
        }

        _log.Info($"{g.Name}: {start.ExeName} started (pid {start.Pid})" + (start.Route is { } route ? $" via {route}" : string.Empty)); // 감지 지연 측정 (cs_service.ps1 test) 이 쿨다운과 무관하게 매번 이 줄을 봅니다
        DateTimeOffset now = _now();
        if (_last.TryGetValue(g.Name, out DateTimeOffset prev) && now - prev < Cooldown)
        {
            return false;
        }

        if (!LauncherExes.Is(start.ExeName) && RestreamSkip(g, start, now) is { } why)
        {
            _log.Info($"{g.Name}: start stream skipped ({why})");
            return false;
        }

        _last[g.Name] = now;
        try
        {
            StreamStats r = _streamer.Stream(g, stop);
            if (r.Aborted || stop.IsCancellationRequested)
            {
                _log.Info($"{g.Name}: start stream cancelled after {r.Bytes} bytes");
                return false;
            }

            _log.Info($"{g.Name}: streamed {r.Bytes} bytes in {r.Requests} requests, {r.Errors} errors, {r.Seconds:F3} s");
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            _log.Info($"{g.Name}: no trace file yet, nothing to stream"); // 아직 추적을 만들지 않은 게임은 오류가 아님
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            _log.Error($"{g.Name}: stream failed", e);
            return false;
        }
    }
}
