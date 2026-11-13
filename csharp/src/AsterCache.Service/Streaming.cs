using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
    (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress) => (Stream(game, stop), true);

    /// 디스크 대여가 유효한 동안에만 스트림 묶음을 발행합니다.
    (StreamStats Stats, bool Complete) StreamCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue) =>
        canIssue() ? Stream(game, stop, progress) : (new StreamStats(0, 0, 0, 0, Aborted: true), false);

    (StreamStats Stats, bool Complete) StreamCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue) =>
        StreamCoordinated(game, stop, progress, canIssue);

    /// 예열 전용 스트림. maxBytes 는 메모리·스트림 예산 안에서 읽을 전체 바이트 상한입니다.
    (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes) => Stream(game, stop, progress);

    /// 끊긴 부분 예열을 이어 읽는 예열 스트림: 읽기 목록 앞쪽 skipBytes 는 이미 읽었으니 건너뜁니다 (통계 바이트는 새로 읽은 것만). 건너뛰기를 모르는 스트리머는 처음부터 읽습니다.
    (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes, long skipBytes) => StreamPrewarm(game, stop, progress, maxBytes);

    (StreamStats Stats, bool Complete) StreamPrewarmCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes, long skipBytes, Func<bool> canIssue) =>
        canIssue() ? StreamPrewarm(game, stop, progress, maxBytes, skipBytes) : (new StreamStats(0, 0, 0, 0, Aborted: true), false);

    (StreamStats Stats, bool Complete) StreamPrewarmCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes, long skipBytes, Func<bool> canIssue, bool allowReorder) =>
        StreamPrewarmCoordinated(game, stop, progress, maxBytes, skipBytes, canIssue);

    (StreamStats Stats, bool Complete) StreamPrewarmCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes, long skipBytes, Func<bool> canIssue, bool allowReorder, Action<string, long, bool>? noteIssue) =>
        StreamPrewarmCoordinated(game, stop, progress, maxBytes, skipBytes, canIssue, allowReorder);

    bool WasPrewarmReordered(GameSettings game) => false;

    /// 예열이 실제로 읽을 원래 범위의 바이트 (틈 제외), 모르면 null. 최초 메모리 게이트가 존 크기 대신 씁니다.
    long? PrewarmReadBytes(GameSettings game) => null;

    /// 부분 예열이면 실제 스트림과 같이 마지막 범위를 예산 경계에서 자른 크기입니다.
    long? PrewarmReadBytes(GameSettings game, bool partial) => PrewarmReadBytes(game);

    /// 로딩 앞부분 (tier0) 만 stop 으로 멈출 수 있게 읽습니다. 묶음 예열이 모든 게임의 앞부분을 먼저 올릴 때 씁니다. 기본은 아무것도 하지 않음.
    void StreamHead(GameSettings game, CancellationToken stop)
    {
    }

    /// 묶음별 진행을 알리는 로딩 앞부분 스트림. 기존 스트리머는 진행 알림 없이 동작합니다.
    void StreamHead(GameSettings game, CancellationToken stop, Action<long, long>? progress) => StreamHead(game, stop);

    void StreamHeadCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue)
    {
        if (canIssue())
        {
            StreamHead(game, stop, progress);
        }
    }

    void StreamHeadCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue) =>
        StreamHeadCoordinated(game, stop, progress, canIssue);

    /// 로딩 앞부분의 바이트 (원시 트레이스에 t_ms 가 있어 경계를 계산했을 때), 아니면 null.
    long? LoadHeadBytes(GameSettings game) => null;
}

/// 게임의 .prefetch.json 을 Python prefetch.cli 기본값 (physical, 간격 0, 파일 30% 이상이면 통째) 으로 스케줄해 PrefetchVirtualMemory 로 읽습니다.
/// 예산은 계획의 존 크기 (계획이 없거나 존이 0 이면 추적의 총 바이트) 이고, 게임별 max_stream_mib 가 상한입니다. 고정 1 GiB 는 더 큰 핫 집합을 잘랐습니다.
public sealed class PrefetchStreamer : IGameStreamer
{
    private sealed record TraceSourceCacheEntry(long Length, long LastWriteTicks, IReadOnlyList<TraceEvent> Rows);

    private static readonly object TraceSourceCacheLock = new();

    /// 시험용: 원시 트레이스를 실제로 파싱한 횟수 (경로별).
    internal static readonly ConcurrentDictionary<string, int> SourceTraceParses = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, TraceSourceCacheEntry> TraceSourceCache = new(StringComparer.OrdinalIgnoreCase);

    internal static void ForgetRemovedGames(IEnumerable<GameSettings> games)
    {
        foreach (GameSettings game in games)
        {
            string[] paths = [AutoTracer.RawTracePath(game), AutoTracer.BootTracePath(game)];
            lock (TraceSourceCacheLock)
            {
                foreach (string path in paths)
                {
                    _ = TraceSourceCache.Remove(path + "|" + game.OriginRoot);
                }
            }

            foreach (string path in paths)
            {
                _ = SourceTraceParses.TryRemove(path, out _);
            }
        }
    }

    private readonly ConcurrentDictionary<string, bool> _reordered = new(StringComparer.OrdinalIgnoreCase);

    /// 예열 목록을 정한 뒤 원래 순서를 바꿀 수 있습니다 (마지막 인자는 스트림 중지 신호). 기본은 기존 순서입니다.
    public Func<GameSettings, List<ReadOp>, CancellationToken, List<ReadOp>>? Reorder { get; set; }

    public bool WasPrewarmReordered(GameSettings game) => _reordered.GetValueOrDefault(game.Name);

    public bool WasPrewarmReordered(string game) => _reordered.GetValueOrDefault(game);

    /// 계획의 존 크기 (바이트). 계획 파일이 없거나 읽을 수 없으면 null, 존이 비어 있으면 0.
    /// 잔여 재계획은 Zone 이 새로 옮길 구간뿐이라 (전부 제자리면 0), 핫셋 전체인 task 합계를 돌려줍니다.
    public static long? ZoneBytes(GameSettings game) => ZoneBytes(game, true, default, ZoneView.Plan);

    /// 게임 간 메모리 예산 배정에 쓰는 예열 목록의 바이트. ZoneBytes 와 같되, 잔여 계획은 task 합계 대신 실제 예열 목록 바이트이고 존 밖 덧붙임 행도 셉니다 (목록이 비었거나 읽히지 않을 때만 task 합계).
    /// 고정 상한·트레이 측정은 계획 범위가 의미라 ZoneBytes 를 그대로 씁니다.
    public static long? WarmBytes(GameSettings game) => ZoneBytes(game, true, default, ZoneView.Warm);

    // 존 바이트를 무엇의 크기로 볼 것인가: Plan = 계획 범위 (덧붙임 행 제외, 고정 상한·트레이), Budget = 읽기 예산 (목록 전체를 담도록 덧붙임 행 포함),
    // Warm = 게임 간 예산 배정 (Budget + 잔여 계획은 task 합계 대신 목록 바이트).
    private enum ZoneView { Plan, Budget, Warm }

    // withWarmOnly: 예열 전용 옆 파일 (가변 파일 범위) 까지 센 예열량. 시작 스트림의 예산은 false (계획 범위만).
    private static long? ZoneBytes(GameSettings game, bool withWarmOnly, CancellationToken cancellationToken, ZoneView view = ZoneView.Budget)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlanFile plan = PlanFile.Load(game.Plan);
            long planBytes = (plan.IsResidual ? plan.Tasks.Sum(t => t.Clusters) : plan.Zone.Clusters) * plan.ClusterBytes;
            long plannedWarmBytes = 0;
            long volatileBytes = 0;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (File.Exists(game.Trace))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    IReadOnlyDictionary<string, string> marked = AutoTracer.ReadVolatile(AutoTracer.VolatilePath(game));
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (TraceEvent row in LoadRows(game, withWarmOnly, cancellationToken, allowSourceRead: false)) // 길이 합만 필요: 출처 복원을 위해 원시 트레이스를 읽지 않음
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (IsVolatilePath(row.Path, marked))
                        {
                            volatileBytes += row.Length;
                        }
                        else if (view != ZoneView.Plan || !row.Overflow)
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

            bool listIsTotal = view == ZoneView.Warm && plan.IsResidual && plannedWarmBytes + volatileBytes > 0;
            return Math.Max(listIsTotal ? 0 : planBytes, plannedWarmBytes) + volatileBytes;
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
    public static long HeadBytesOf(GameSettings game) => HeadBytesOf(game, default);

    private static long HeadBytesOf(GameSettings game, CancellationToken cancellationToken) =>
        LoadTiers.For(game, cancellationToken)?.Tier0Bytes is > 0 and long tier0 ? tier0 : HeadBytes;

    public long? LoadHeadBytes(GameSettings game) => LoadTiers.For(game)?.Tier0Bytes is > 0 and long tier0 ? tier0 : null;

    public long? PrewarmReadBytes(GameSettings game) => PrewarmReadBytes(game, partial: false);

    public long? PrewarmReadBytes(GameSettings game, bool partial) => ScheduleOps(game, true, trimLast: partial).Ops.Sum(o => o.Length);

    /// 관찰 출처가 있는 예열 범위의 바이트. 구형 목록에 표식이 없으면 전체 예열 예산을 관찰된 것으로 취급합니다.
    public static long? ObservedBytes(GameSettings game) => ObservedBytes(game, allowSourceRead: true);

    internal static long? ObservedBytes(GameSettings game, bool allowSourceRead)
    {
        try
        {
            List<TraceEvent> rows = LoadRows(game, true, out int warmOnlyStart, default, allowSourceRead);
            long? zoneBytes = ZoneBytes(game, true, default);
            long total = rows.Sum(r => r.Length);
            long budget = Budget(game, zoneBytes, total);
            if (!rows.Any(r => !r.Observed))
            {
                return budget;
            }

            List<TraceEvent> selected = budget < total ? TrimToBudget(game, rows, warmOnlyStart, budget, default, allowSourceRead).Rows : rows;
            return selected.Where(r => r.Observed).Sum(r => r.Length);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or PlanRejectedException)
        {
            return null;
        }
    }

    /// 접근 순서 앞쪽 HeadBytesOf 만큼만 읽습니다 (스케줄러가 앞에 둔 부분과 같음).
    public void StreamHead(GameSettings game, CancellationToken stop) => StreamHead(game, stop, null);

    public void StreamHead(GameSettings game, CancellationToken stop, Action<long, long>? progress) => _ = Batches(Head(Ops(game, stop), HeadBytesOf(game, stop)), stop, progress, null);

    public void StreamHeadCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue) =>
        StreamHeadCoordinated(game, stop, progress, canIssue, null);

    public void StreamHeadCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue)
    {
        noteIssue?.Invoke("head schedule", 0, false);
        _ = Batches(Head(Ops(game, stop), HeadBytesOf(game, stop)), stop, progress, canIssue, noteIssue);
    }

    /// 예열이 끝난 뒤 버퍼드로 한 번 더 읽는 앞쪽 양: 스탠바이 목록에서 접근 순서 앞부분이 먼저 밀려나지 않게 최신으로 만듭니다.
    public const long RefreshBytes = 64L << 20;

    /// 게임 시작과 함께 도는 동시 스트림: 멈추지 않고 끝까지 (묶음 경로는 예열과 같음).
    public StreamStats Stream(GameSettings game) => Batches(Ops(game), CancellationToken.None, null, null).Stats;

    /// 게임 시작 스트림은 시작 예열과 겹치지 않게 갱신 읽기를 생략하고, 서비스 취소를 묶음 읽기에 전달합니다.
    public StreamStats Stream(GameSettings game, CancellationToken stop) => Batches(Ops(game, stop), stop, null, null).Stats;

    /// 예열용: 64 MiB 묶음 (FolderWarm.Read) 마다 stop 을 보고 진행을 알립니다. 끝까지 읽었으면 앞쪽 RefreshBytes 를 버퍼드로 한 번 더 읽습니다.
    public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
    {
        List<ReadOp> ops = Ops(game, stop);
        (StreamStats Stats, bool Complete) r = Batches(ops, stop, progress, null);
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

    public (StreamStats Stats, bool Complete) StreamCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue)
    {
        return StreamCoordinated(game, stop, progress, canIssue, null);
    }

    public (StreamStats Stats, bool Complete) StreamCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue)
    {
        noteIssue?.Invoke("stream schedule", 0, false);
        List<ReadOp> ops = Ops(game, stop);
        (StreamStats Stats, bool Complete) r = Batches(ops, stop, progress, canIssue, noteIssue);
        return r.Complete && !canIssue() ? (r.Stats with { Aborted = true }, false) : r;
    }

    /// 예열 전용 범위에 작은 논리 틈을 예산 안에서 더해 읽습니다. 시작 스트림·표본·고정은 Ops 의 원래 범위를 씁니다.
    public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes) =>
        StreamPrewarm(game, stop, progress, maxBytes, 0);

    public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes, long skipBytes)
        => StreamPrewarmCore(game, stop, progress, maxBytes, skipBytes, null, true);

    public (StreamStats Stats, bool Complete) StreamPrewarmCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes, long skipBytes, Func<bool> canIssue)
        => StreamPrewarmCore(game, stop, progress, maxBytes, skipBytes, canIssue, true);

    public (StreamStats Stats, bool Complete) StreamPrewarmCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes, long skipBytes, Func<bool> canIssue, bool allowReorder)
        => StreamPrewarmCore(game, stop, progress, maxBytes, skipBytes, canIssue, allowReorder, null);

    public (StreamStats Stats, bool Complete) StreamPrewarmCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes, long skipBytes, Func<bool> canIssue, bool allowReorder, Action<string, long, bool>? noteIssue)
        => StreamPrewarmCore(game, stop, progress, maxBytes, skipBytes, canIssue, allowReorder, noteIssue);

    private (StreamStats Stats, bool Complete) StreamPrewarmCore(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes, long skipBytes, Func<bool>? canIssue, bool allowReorder, Action<string, long, bool>? noteIssue = null)
    {
        List<ReadOp> all;
        long gapBytes;
        bool fitsBudget;
        try
        {
            (all, gapBytes, fitsBudget) = PrewarmOps(game, maxBytes, stop);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return (new StreamStats(0, 0, 0, 0, Aborted: true), false);
        }
        _reordered[game.Name] = false;
        if (!fitsBudget || stop.IsCancellationRequested)
        {
            return (new StreamStats(0, 0, 0, 0, Aborted: true), false);
        }

        List<ReadOp> stream = all;
        if (allowReorder && maxBytes > 0 && skipBytes == 0 && !game.MaxStreamSet && Reorder is { } reorder)
        {
            try
            {
                using var orbitTimer = PrewarmRun.Time(PrewarmPhase.Orbit);
                List<ReadOp> candidate = reorder(game, all, stop);
                if (candidate is not null && !ReferenceEquals(candidate, all))
                {
                    stream = candidate;
                    _reordered[game.Name] = true;
                }
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
            {
                stream = all;
            }

            if (stop.IsCancellationRequested)
            {
                return (new StreamStats(0, 0, 0, 0, Aborted: true), false); // 결정 중 게임이 시작되면 읽기를 내지 않음
            }
        }

        List<ReadOp> ops = skipBytes > 0 ? Tail(stream, skipBytes) : stream; // 갱신 읽기 (Refresh) 는 원래 목록 앞쪽을 씁니다

        progress?.Invoke(0, ops.Sum(op => op.Length));
        if (stop.IsCancellationRequested)
        {
            return (new StreamStats(0, 0, 0, 0, Aborted: true), false);
        }

        using var streamTimer = PrewarmRun.Time(PrewarmPhase.Stream);
        (StreamStats Stats, bool Complete) r = Batches(ops, stop, progress, canIssue, noteIssue);
        if (r.Complete)
        {
            if (stop.IsCancellationRequested || (canIssue is not null && !canIssue()))
            {
                return (r.Stats with { Aborted = true, GapBytes = gapBytes }, false);
            }

            StreamStats refresh = Refresh(all, stop, noteIssue);
            if (stop.IsCancellationRequested || refresh.Aborted || (canIssue is not null && !canIssue()))
            {
                return (r.Stats with { Aborted = true, GapBytes = gapBytes }, false);
            }
        }

        bool complete = r.Complete && !stop.IsCancellationRequested;
        return (r.Stats with { Aborted = r.Stats.Aborted || !complete, GapBytes = gapBytes }, complete);
    }

    internal static StreamStats Refresh(IReadOnlyList<ReadOp> ops, CancellationToken stop, Action<string, long, bool>? noteIssue = null)
    {
        if (stop.IsCancellationRequested)
        {
            return new StreamStats(0, 0, 0, 0, Aborted: true);
        }

        List<ReadOp> refresh = Head(ops, RefreshBytes);
        noteIssue?.Invoke("refresh", refresh.Sum(o => o.Length), true);
        return ZoneStreamer.Run(refresh, StreamMethod.Read, ct: stop);
    }

    private static (StreamStats Stats, bool Complete) Batches(List<ReadOp> ops, CancellationToken stop, Action<long, long>? progress, Func<bool>? canIssue, Action<string, long, bool>? noteIssue = null)
    {
        long total = ops.Sum(o => o.Length);
        long done = 0;
        (StreamStats Stats, bool Complete) result = FolderWarm.Read(ops, stop, batch =>
        {
            if (stop.IsCancellationRequested || (canIssue is not null && !canIssue()))
            {
                return new StreamStats(0, 0, 0, 0, Aborted: true, RequestedBytes: batch.Sum(op => op.Length));
            }

            noteIssue?.Invoke("stream batch", batch.Sum(op => op.Length), true);
            StreamStats s = ZoneStreamer.Run(batch, StreamMethod.Pvm, ct: stop, noteIssue: noteIssue);
            done += s.Bytes;
            progress?.Invoke(done, total);
            return s;
        });
        return stop.IsCancellationRequested || (canIssue is not null && !canIssue())
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

    /// 순서 목록에서 앞의 bytes 를 뺀 나머지 (걸친 op 는 잘라서).
    public static List<ReadOp> Tail(IReadOnlyList<ReadOp> ops, long bytes)
    {
        var tail = new List<ReadOp>();
        long skip = Math.Max(0, bytes);
        foreach (ReadOp op in ops)
        {
            if (skip >= op.Length)
            {
                skip -= op.Length;
                continue;
            }

            tail.Add(skip > 0 ? new ReadOp(op.Path, op.Offset + skip, op.Length - skip) : op);
            skip = 0;
        }

        return tail;
    }

    /// 게임의 선읽기 순서 목록 (허용 루트 검사 포함). 스트림·식음 확인·고정이 같은 목록을 씁니다.
    public static List<ReadOp> Ops(GameSettings game, CancellationToken cancellationToken = default)
    {
        return ScheduleOps(game, false, cancellationToken: cancellationToken).Ops;
    }

    /// 제품 예열이 읽을 원래 순서 목록입니다.
    public static List<ReadOp> IncumbentPrewarmOps(GameSettings game, long maxBytes, CancellationToken cancellationToken = default) => PrewarmOps(game, maxBytes, cancellationToken).Ops;

    /// 예열 스트림에만 틈을 더하고, 남은 예산이 없으면 원래 범위만 유지합니다.
    /// 원래 범위는 존 예산으로 이미 골라졌고, totalBudgetBytes (유효 존 상한·스트림 상한·가용 RAM 중 작은 값) 에서 그 범위를 뺀 나머지만 틈에 씁니다.
    /// 예열 한 회 (PrewarmRun) 안에서는 같은 (게임, 예산) 의 목록을 한 번만 계산합니다.
    internal static (List<ReadOp> Ops, long GapBytes, bool FitsBudget) PrewarmOps(GameSettings game, long totalBudgetBytes, CancellationToken cancellationToken = default) =>
        PrewarmRun.Plan(game, totalBudgetBytes, cancellationToken, () => BuildPrewarmOps(game, totalBudgetBytes, cancellationToken));

    private static (List<ReadOp> Ops, long GapBytes, bool FitsBudget) BuildPrewarmOps(GameSettings game, long totalBudgetBytes, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (List<ReadOp> original, _, long frontBytes) = ScheduleOps(game, true, trimLast: totalBudgetBytes == 0, cancellationToken: cancellationToken);
        (List<ReadOp> expanded, long gapBytes, bool fitsBudget) = FillPrewarmGaps(original, Math.Max(0, totalBudgetBytes), p =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(p))
            {
                return 0;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new FileInfo(p).Length;
        }, cancellationToken);
        if (!fitsBudget || gapBytes == 0)
        {
            return (expanded, gapBytes, fitsBudget);
        }

        (List<ReadOp> ordered, _) = Scheduler.Schedule(expanded.Select(o => new ReadRange(o.Path, o.Offset, o.Length)), "physical", null, 0,
            FileLayout.Get, p =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!File.Exists(p))
                {
                    return 0;
                }

                cancellationToken.ThrowIfCancellationRequested();
                return new FileInfo(p).Length;
            }, fullFileRatio: 2, headBytes: HeadBytesOf(game, cancellationToken) + frontBytes, cancellationToken: cancellationToken);
        return (ordered, gapBytes, true);
    }

    /// 작은 논리 틈을 모두 더했을 때 예산을 넘으면 256 KiB 미만만 다시 시도하고, 그것도 넘으면 원래 범위를 반환합니다.
    internal static (List<ReadOp> Ops, long GapBytes, bool FitsBudget) FillPrewarmGaps(
        IReadOnlyList<ReadOp> ops, long totalBudgetBytes, Func<string, long> fileSize, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        List<ReadOp> original = [.. ops];
        long baseBytes = 0;
        foreach (ReadOp op in original)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
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
            cancellationToken.ThrowIfCancellationRequested();
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
    internal static List<TraceEvent> LoadRows(GameSettings game, bool withWarmOnly, CancellationToken cancellationToken = default, bool allowSourceRead = true) =>
        LoadRows(game, withWarmOnly, out _, cancellationToken, allowSourceRead);

    // warmOnlyStart: 반환 목록에서 예열 전용 행이 시작하는 위치 (없으면 행 수).
    private static List<TraceEvent> LoadRows(GameSettings game, bool withWarmOnly, out int warmOnlyStart, CancellationToken cancellationToken, bool allowSourceRead)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Func<string, long?> size = p =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(p))
            {
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return new FileInfo(p).Length;
        };
        List<TraceEvent> rows = TraceLoader.LoadRanges(game.Trace, game.OriginRoot, RealPaths.Resolve, size, cancellationToken);
        IReadOnlyList<TraceEvent>? sourceRows = null;
        if (NeedsSourceRestore(rows))
        {
            sourceRows = LoadSourceTraceRows(game, size, cancellationToken, allowSourceRead);
            if (sourceRows is not null)
            {
                rows = RestoreObservedSources(rows, sourceRows);
            }
        }

        warmOnlyStart = rows.Count;
        string side = PlanOutput.WarmOnlyPath(game.Trace);
        cancellationToken.ThrowIfCancellationRequested();
        if (withWarmOnly && File.Exists(side))
        {
            List<TraceEvent> warmOnly = TraceLoader.LoadRanges(side, game.OriginRoot, RealPaths.Resolve, size, cancellationToken);
            if (NeedsSourceRestore(warmOnly))
            {
                sourceRows ??= LoadSourceTraceRows(game, size, cancellationToken, allowSourceRead);
                if (sourceRows is not null)
                {
                    warmOnly = RestoreObservedSources(warmOnly, sourceRows);
                }
            }

            rows.AddRange(warmOnly);
        }

        return rows;
    }

    private static bool NeedsSourceRestore(IReadOnlyList<TraceEvent> rows) => !rows.Any(r => !r.Observed || r.RangeGroup is not null);

    private static IReadOnlyList<TraceEvent>? LoadSourceTraceRows(GameSettings game, Func<string, long?> size, CancellationToken cancellationToken, bool allowSourceRead)
    {
        if (!allowSourceRead)
        {
            lock (TraceSourceCacheLock) // 파일 접근 없이 캐시에서만 찾음 (게임 실행 중·시작 직후 경로)
            {
                foreach (string candidate in new[] { AutoTracer.RawTracePath(game), AutoTracer.BootTracePath(game) })
                {
                    if (TraceSourceCache.TryGetValue(candidate + "|" + game.OriginRoot, out TraceSourceCacheEntry? hit))
                    {
                        return hit.Rows;
                    }
                }
            }

            return null;
        }

        string? path = AutoTracer.PlanTracePath(game, File.Exists);
        if (path is null)
        {
            return null;
        }

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            long length = info.Length;
            long written = info.LastWriteTimeUtc.Ticks;
            string key = path + "|" + game.OriginRoot;
            lock (TraceSourceCacheLock)
            {
                if (TraceSourceCache.TryGetValue(key, out TraceSourceCacheEntry? cached)
                    && cached.Length == length && cached.LastWriteTicks == written)
                {
                    return cached.Rows;
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            SourceTraceParses.AddOrUpdate(path, 1, (_, n) => n + 1);
            List<TraceEvent> rows = TraceLoader.LoadRanges(path, game.OriginRoot, RealPaths.Resolve, size, cancellationToken);
            lock (TraceSourceCacheLock)
            {
                TraceSourceCache[key] = new TraceSourceCacheEntry(length, written, rows);
            }

            return rows;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or PlanRejectedException)
        {
            return null;
        }
    }

    private static List<TraceEvent> RestoreObservedSources(IReadOnlyList<TraceEvent> rows, IReadOnlyList<TraceEvent> sourceRows)
    {
        var source = new Dictionary<string, List<(long Start, long End)>>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, TraceEvent> pathRows in sourceRows.Where(r => r.Length > 0).GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase))
        {
            var merged = new List<(long Start, long End)>();
            foreach (TraceEvent row in pathRows.OrderBy(r => r.Offset))
            {
                long end = row.Offset + row.Length;
                if (merged.Count > 0 && row.Offset <= merged[^1].End)
                {
                    merged[^1] = (merged[^1].Start, Math.Max(merged[^1].End, end));
                }
                else
                {
                    merged.Add((row.Offset, end));
                }
            }

            source[pathRows.Key] = merged;
        }

        var restored = new List<TraceEvent>(rows.Count);
        long group = 0;
        foreach (TraceEvent row in rows)
        {
            long end = row.Offset + row.Length;
            var pieces = new List<TraceEvent>();
            long at = row.Offset;
            if (source.TryGetValue(row.Path, out List<(long Start, long End)>? ranges))
            {
                foreach ((long start, long rangeEnd) in ranges)
                {
                    if (rangeEnd <= at)
                    {
                        continue;
                    }

                    if (start >= end)
                    {
                        break;
                    }

                    if (start > at)
                    {
                        long gapEnd = Math.Min(end, start);
                        pieces.Add(row with { Offset = at, Length = gapEnd - at, Observed = false });
                        at = gapEnd;
                    }

                    long observedEnd = Math.Min(end, rangeEnd);
                    if (observedEnd > at)
                    {
                        pieces.Add(row with { Offset = at, Length = observedEnd - at, Observed = true });
                        at = observedEnd;
                    }

                    if (at >= end)
                    {
                        break;
                    }
                }
            }

            if (at < end)
            {
                pieces.Add(row with { Offset = at, Length = end - at, Observed = false });
            }

            if (pieces.Count == 1)
            {
                restored.Add(pieces[0]);
            }
            else if (pieces.Count > 1)
            {
                long rangeGroup = group++;
                restored.AddRange(pieces.Select(piece => piece with { RangeGroup = rangeGroup }));
            }
        }

        return restored;
    }

    // 전체가 들어가면 원래 순서를 유지하고, 잘라야 하면 아래 우선순위로 예산에 남깁니다. 마지막 범위는 경계에서 자릅니다.
    // 우선순위 = 내보내는 순서: (가) 게임이 쓰는 파일 (예열 전용, 덧붙임 아님) 의 실행 앞부분 (frontMs 전에 처음 읽힌) 관찰 행, (나) 선읽기 관찰 행, (다) 나머지 예열 전용 관찰 행 (t_ms 순, t_ms 없는 옛 행은 맨 뒤), (라) 추측 행.
    // 덧붙임 행 (Overflow) 은 t_ms 가 앞부분이어도 (다) 에만 들어, 메모리가 남을 때만 읽힙니다. rows[warmOnlyStart..] 가 예열 전용 행입니다.
    // frontBytes: 남긴 (가) 의 바이트 (스케줄러가 접근 순서로 먼저 읽는 앞부분에 더해야 하는 양). 전체가 들어가면 순서를 바꾸지 않으므로 0.
    internal static List<TraceEvent> SelectToBudget(IReadOnlyList<TraceEvent> rows, long budget, int warmOnlyStart, long frontMs, out long frontBytes)
    {
        frontBytes = 0;
        if (rows.Sum(r => r.Length) <= budget)
        {
            return [.. rows];
        }

        List<TraceEvent> front = [];
        List<TraceEvent> observed = [];
        List<TraceEvent> warmOnly = [];
        List<TraceEvent> guess = [];
        for (int i = 0; i < rows.Count; i++)
        {
            TraceEvent r = rows[i];
            List<TraceEvent> tier = !r.Observed ? guess : i < warmOnlyStart ? observed : !r.Overflow && r.TMs < frontMs ? front : warmOnly;
            tier.Add(r);
        }

        var kept = new List<TraceEvent>();
        long used = 0;
        foreach (TraceEvent r in front.Concat(observed).Concat(warmOnly.OrderBy(w => w.TMs ?? long.MaxValue)).Concat(guess))
        {
            long take = Math.Min(r.Length, budget - used);
            if (take <= 0)
            {
                break;
            }

            kept.Add(take == r.Length ? r : r with { Length = take });
            used += take;
            if (take < r.Length)
            {
                break;
            }
        }

        frontBytes = Math.Min(front.Sum(r => r.Length), used);
        return kept;
    }

    // 실행 앞부분의 끝 (ms): 로딩 경계를 알면 그 시각, 모르면 120 초. allowParse 가 거짓이면 이미 캐시에 있는 경계만 봅니다 (게임 실행 중 경로에서 원시 트레이스를 파싱하지 않음).
    private static long FrontMs(GameSettings game, CancellationToken cancellationToken, bool allowParse) =>
        LoadTiers.For(game, cancellationToken, allowParse)?.BoundaryMs is > 0 and long ms ? ms : DefaultFrontMs;

    private const long DefaultFrontMs = 120_000;

    private static (List<TraceEvent> Rows, long FrontBytes) TrimToBudget(GameSettings game, IReadOnlyList<TraceEvent> rows, int warmOnlyStart, long budget, CancellationToken cancellationToken, bool allowParse)
    {
        List<TraceEvent> kept = SelectToBudget(rows, budget, warmOnlyStart, warmOnlyStart < rows.Count ? FrontMs(game, cancellationToken, allowParse) : 0, out long frontBytes);
        return (kept, frontBytes);
    }

    internal static List<TraceEvent> CoalesceRangeGroups(IReadOnlyList<TraceEvent> rows)
    {
        var result = new List<TraceEvent>(rows.Count);
        foreach (TraceEvent row in rows)
        {
            if (row.RangeGroup is { } group && result.Count > 0)
            {
                TraceEvent previous = result[^1];
                if (previous.RangeGroup == group
                    && string.Equals(previous.Path, row.Path, StringComparison.OrdinalIgnoreCase)
                    && previous.Offset + previous.Length == row.Offset)
                {
                    result[^1] = previous with { Length = previous.Length + row.Length };
                    continue;
                }
            }

            result.Add(row);
        }

        return result;
    }

    private static (List<ReadOp> Ops, long BudgetBytes, long FrontBytes) ScheduleOps(GameSettings game, bool withWarmOnly, bool trimLast = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PlanGuards.Inside(game.OriginRoot, game.AllowedRoots, RealPaths.Real))
        {
            throw new PlanRejectedException("origin_root resolves outside allowed roots");
        }

        List<TraceEvent> rows = LoadRows(game, withWarmOnly, out int warmOnlyStart, cancellationToken, allowSourceRead: false); // 게임 시작 경로에서도 불리므로 원시 트레이스는 읽지 않고 캐시만 씀 (캐시는 예열 쪽 ObservedBytes 가 채움)
        cancellationToken.ThrowIfCancellationRequested();
        long total = rows.Sum(r => r.Length);
        // 예열 목록의 예산은 배정·표시가 쓰는 목록 기준 (Warm): 잔여 계획의 task 합계를 예산으로 쓰면 스케줄러의 파일 전체 승격이 배정량을 넘어 목록을 다 읽고도 부분 예열로 남음
        long budget = Budget(game, ZoneBytes(game, withWarmOnly, cancellationToken, withWarmOnly ? ZoneView.Warm : ZoneView.Budget), total);
        long frontBytes = 0;
        if (trimLast && budget < total)
        {
            (rows, frontBytes) = TrimToBudget(game, rows, warmOnlyStart, budget, cancellationToken, allowParse: true);
        }

        IEnumerable<ReadRange> ranges = CoalesceRangeGroups(rows).Select(r => new ReadRange(r.Path, r.Offset, r.Length));
        (List<ReadOp> ops, _) = Scheduler.Schedule(ranges, "physical", budget, 0,
            FileLayout.Get, p =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return new FileInfo(p).Length;
            }, headBytes: HeadBytesOf(game, cancellationToken) + frontBytes, cancellationToken: cancellationToken); // 앞부분 가변 범위 (목록 맨 앞) 도 접근 순서로 먼저 읽음
        return (ops, budget, frontBytes);
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

    /// 부분 예열이 읽은 목록 (예열 전용 범위 포함, 상한에 맞춰 마지막 범위를 자른 것): 재예열 표본은 여기서만 고릅니다.
    internal static List<(string Path, long Offset, int Bytes)> PartialProbeSamples(GameSettings game, int round = 0) =>
        CacheProbe.PickSamples(ScheduleOps(game, true, trimLast: true).Ops, round: round);

    public static ProbeResult? ProbePartialReadMs(GameSettings game, Func<bool>? canIssue = null) => CacheProbe.Probe(PartialProbeSamples(game, CacheProbe.NextRound(game.Name)), canIssue: canIssue);

    /// 끊긴 부분 예열 (game 의 MaxStreamMib 로 잘린 목록) 이 스케줄 순서로 앞에서 bytes 만큼 읽은 구간의 표본을 읽어 잰 결과: 이어 읽기 전에 그 구간이 아직 따뜻한지 봅니다.
    public static ProbeResult? ProbeScheduledHead(GameSettings game, long bytes, Func<bool>? canIssue = null) =>
        CacheProbe.Probe(CacheProbe.PickSamples(Head(ScheduleOps(game, true, trimLast: true).Ops, bytes), round: CacheProbe.NextRound(game.Name)), canIssue: canIssue);

    /// 선읽기 목록의 앞쪽과 고르게 고른 몇 곳을 읽어 잰 결과 (CacheProbe).
    public static ProbeResult? ProbeReadMs(GameSettings game, Func<bool>? canIssue = null) => CacheProbe.Probe(CacheProbe.PickSamples(Ops(game), round: CacheProbe.NextRound(game.Name)), canIssue: canIssue); // 점검마다 다른 위치 (앞 점검의 읽기가 캐시를 데우지 않게)
}

internal enum PrewarmPhase { Plan, Calibration, Common, Orbit, Stream }

/// 예열 한 회 동안만 사는 계획 재사용과 단계별 경과 시간. 같은 스레드에서 도는 보정·common·본 스트림이 같은 (게임, 예산) 의 목록을 한 번만 계산하게 하고, 회가 끝나면 사라집니다.
internal sealed class PrewarmRun : IDisposable
{
    [ThreadStatic]
    private static PrewarmRun? _current;

    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly long[] _ticks = new long[Enum.GetValues<PrewarmPhase>().Length];
    private (GameSettings Game, long Budget, List<ReadOp> Ops, long GapBytes, bool Fits, string? Stamp)? _plan;
    private int _depth;
    private bool _calibrationCapped;

    /// 이 회의 보정 읽기가 시간 상한에 걸렸음을 알림 (회 밖이면 무시): Summary 가 표시함.
    internal static void NoteCalibrationCapped()
    {
        if (_current is { } run)
        {
            run._calibrationCapped = true;
        }
    }

    // 목록의 입력 (계획·트레이스·예열 전용 옆 파일·가변 표지) 의 (존재, 길이, 수정 시각). 메타데이터 조회뿐이며 읽지 못하면 null (재사용 안 함).
    internal static string? InputStamp(GameSettings game)
    {
        try
        {
            return string.Join('|', new[] { game.Plan, game.Trace, PlanOutput.WarmOnlyPath(game.Trace), AutoTracer.VolatilePath(game) }.Select(path =>
            {
                var info = new FileInfo(path);
                return info.Exists ? $"{info.Length}:{info.LastWriteTimeUtc.Ticks}" : "-";
            }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static PrewarmRun Begin()
    {
        PrewarmRun run = _current ??= new PrewarmRun();
        run._depth++;
        return run;
    }

    public void Dispose()
    {
        if (--_depth == 0)
        {
            _current = null;
        }
    }

    public static PhaseTimer Time(PrewarmPhase phase) =>
        _current is { } run ? new PhaseTimer(run, phase, Stopwatch.GetTimestamp(), run._ticks[(int)PrewarmPhase.Plan]) : default;

    /// 이 회에 계산한 목록이 같은 (게임, 예산) 이면 복사본을 돌려주고, 아니면 build 로 계산합니다 (회 밖에서는 항상 build). 취소 검사는 그대로 합니다.
    public static (List<ReadOp> Ops, long GapBytes, bool FitsBudget) Plan(GameSettings game, long budget, CancellationToken cancellationToken, Func<(List<ReadOp> Ops, long GapBytes, bool FitsBudget)> build)
    {
        if (_current is not { } run)
        {
            return build();
        }

        cancellationToken.ThrowIfCancellationRequested();
        string? stamp = InputStamp(game); // 입력 파일이 회 도중 바뀌었으면 (업데이트 정리 등) 재사용하지 않음. 계산 전에 떠서 계산 중 바뀐 것도 걸림
        if (run._plan is { } p && ReferenceEquals(p.Game, game) && p.Budget == budget && stamp is not null && p.Stamp == stamp)
        {
            return ([.. p.Ops], p.GapBytes, p.Fits);
        }

        long start = Stopwatch.GetTimestamp();
        try
        {
            (List<ReadOp> ops, long gap, bool fits) = build();
            run._plan = (game, budget, [.. ops], gap, fits, stamp);
            return (ops, gap, fits);
        }
        finally
        {
            run._ticks[(int)PrewarmPhase.Plan] += Stopwatch.GetTimestamp() - start;
        }
    }

    /// 각 단계 경과 초와 총 경과 (Begin 부터). Other 는 나머지라 합이 항상 Total 과 같습니다.
    public (double Plan, double Calibration, double Common, double Orbit, double Stream, double Other, double Total) Elapsed()
    {
        double Sec(PrewarmPhase p) => Stopwatch.GetElapsedTime(0, _ticks[(int)p]).TotalSeconds;
        double total = Stopwatch.GetElapsedTime(_started).TotalSeconds;
        double known = _ticks.Sum(t => Stopwatch.GetElapsedTime(0, t).TotalSeconds);
        return (Sec(PrewarmPhase.Plan), Sec(PrewarmPhase.Calibration), Sec(PrewarmPhase.Common), Sec(PrewarmPhase.Orbit), Sec(PrewarmPhase.Stream), total - known, total);
    }

    public string Summary()
    {
        var e = Elapsed();
        return $"plan {e.Plan:F2} s, calibration {e.Calibration:F2} s{(_calibrationCapped ? " (capped)" : string.Empty)}, common {e.Common:F2} s, orbit {e.Orbit:F2} s, stream {e.Stream:F2} s, other {e.Other:F2} s = {e.Total:F2} s";
    }

    /// 단계 시간 (그 안에서 쓴 계획 계산 시간은 Plan 단계로만 셉니다).
    internal readonly struct PhaseTimer(PrewarmRun? run, PrewarmPhase phase, long start, long planStart) : IDisposable
    {
        public void Dispose()
        {
            if (run is not null)
            {
                run._ticks[(int)phase] += Math.Max(0, Stopwatch.GetTimestamp() - start - (run._ticks[(int)PrewarmPhase.Plan] - planStart));
            }
        }
    }
}

/// 게임 시작 스트림을 건너뛸 조건의 입력 (기본은 모두 건너뛰지 않음, 시험용). Create 는 진짜 판정.
public sealed record DispatchEnv
{
    /// 새 프로세스가 이 시간보다 늦게 시작했으면 같은 시작 무리로 보고 (게임 exe 와 보조 exe 가 동시에 뜸) 이미 돌던 것으로 세지 않습니다.
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(10);

    /// 이 프로세스가 시작되기 전에 이미 돌던 이 게임의 다른 프로세스가 있는가.
    public Func<GameSettings, ProcessStart, bool> OtherRunning { get; init; } = (_, _) => false;

    /// 지금 떠 있는 이 게임의 등록 이름 프로세스 (시작한 pid 와 서비스 자신은 뺌). 나이는 GameDispatcher 가 관측한 시작 시각으로 따집니다.
    public Func<GameSettings, ProcessStart, IReadOnlyList<(int Pid, string Exe)>> OtherProcesses { get; init; } = (_, _) => [];

    /// 그 게임의 물리 디스크가 바쁜가 (읽지 못하면 바쁘지 않은 것으로 봄: 지표 없이 첫 시작 스트림까지 막지 않게).
    public Func<GameSettings, bool> DiskBusy { get; init; } = _ => false;

    /// 이 프로세스가 감지된 지 얼마나 됐는가 (Settle 안이면 막 시작한 게임: 그 게임 자신의 읽기를 디스크 바쁨으로 세지 않음).
    public Func<ProcessStart, TimeSpan> Age { get; init; } = s => System.Diagnostics.Stopwatch.GetElapsedTime(s.Timestamp);

    /// 그 게임의 핫 집합이 이미 파일 캐시에 있는가 (Prewarmer.AlreadyWarm). null 은 표본을 얻지 못함 (최근 실제 표본으로 보완).
    public Func<GameSettings, bool?> IsWarm { get; init; } = _ => null;

    /// 현재 표본을 얻지 못했을 때 쓸, 같은 계획·trace 세대의 최근 warm 표본 시각.
    public Func<GameSettings, DateTimeOffset?> RecentWarmProbeAt { get; init; } = _ => null;

    /// 최근 표본의 시작 스트림 보완 유효 기간 (분). 0 이면 보완하지 않습니다.
    public Func<int> RecentWarmProbeMinutes { get; init; } = () => AutoSettings.Default.RewarmMinutes;

    /// 같은 게임의 시작 스트림 사이 최소 간격 (분), 0 = 끔.
    public Func<int> RestreamCooldownMin { get; init; } = () => 0;

    public Func<IReadOnlyCollection<string>, IReadOnlyList<(int Pid, long ReadBytes)>> ReadCounters { get; init; } = ProcessSnapshot.ReadCounters;

    public Func<int> StartStreamCapSeconds { get; init; } = () => AutoSettings.Default.StartStreamCapSeconds;

    /// auto.start_stream_enabled: 거짓이면 게임 시작 스트림을 하지 않습니다 (다른 예열은 그대로).
    public Func<bool> StartStreamEnabled { get; init; } = () => AutoSettings.Default.StartStreamEnabled;

    public static DispatchEnv Create() => new()
    {
        OtherProcesses = (g, start) => RegisteredOthers(g, ProcessSnapshot.Take(), start.Pid),
        DiskBusy = g => DiskActivity.BusyUnlessBroken(PlanFile.SplitDrive(g.OriginRoot), keepStalled: true), // 표본이 없거나 낡으면 바쁨 (정체가 60 초를 넘어도 게임 보호는 유지), 카운터 고장은 기존대로 바쁘지 않음
    };

    /// 등록 이름 프로세스만 (설치 폴더 아래 미등록 프로세스는 제외, 시작한 pid 와 서비스 자신도 제외).
    internal static IReadOnlyList<(int Pid, string Exe)> RegisteredOthers(GameSettings g, IReadOnlyList<(int Pid, string Exe)> processes, int startPid) =>
        processes.Where(p => p.Pid != startPid && p.Pid != Environment.ProcessId && g.ExeNames.Contains(p.Exe, StringComparer.OrdinalIgnoreCase)).ToList();

    /// 이미지 경로가 게임 폴더 아래인가 (다른 프로세스 판정과 종료 판정이 함께 씁니다).
    internal static bool UnderRoot(GameSettings g, string? imagePath)
    {
        string root = g.OriginRoot.TrimEnd('\\', '/') + "\\";
        return imagePath?.StartsWith(root, StringComparison.OrdinalIgnoreCase) == true;
    }

    internal static bool IsGameRunning(GameSettings g) => IsGameRunning(g, ProcessSnapshot.Take());

    // shortcut: 등록 이름만 봄. 미등록 본체는 자동 등록 게임이면 많이 읽을 때 이름이 학습되고, 수동 등록 게임은 사용자가 실행 파일을 추가해야 실행으로 셈 (설치 폴더 아래 미등록 상주 프로그램이 모든 게임을 막지 않게).
    internal static bool IsGameRunning(GameSettings g, IReadOnlyList<(int Pid, string Exe)> processes) =>
        processes.Any(p => p.Pid != Environment.ProcessId && g.ExeNames.Contains(p.Exe, StringComparer.OrdinalIgnoreCase));

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

            if (!g.ExeNames.Contains(exe, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string? path = SafeImagePath(pid, imagePath);
            found.Add(string.IsNullOrEmpty(path) ? $"{exe}({pid})" : $"{exe}({pid}) {path}");
            if (found.Count == 3)
            {
                break;
            }
        }

        return found.Count == 0 ? null : string.Join("; ", found);
    }

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
    private readonly DiskReadCoordinator? _coordinator;
    private readonly Action<ProcessStart, PlayStreamResult>? _streamResult;
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, ProcessStart> _seen = []; // pid -> 서비스가 처음 관측한 시작 (Process.StartTime 을 읽지 않고 나이를 셈)

    public GameDispatcher(IReadOnlyList<GameSettings> games, IGameStreamer streamer, ILog log, Func<DateTimeOffset>? now = null, DispatchEnv? env = null,
        DiskReadCoordinator? coordinator = null, Action<ProcessStart, PlayStreamResult>? streamResult = null)
    {
        _env = env ?? new DispatchEnv();
        _games = games;
        _streamer = streamer;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _coordinator = coordinator;
        _streamResult = streamResult;
    }

    /// 설정을 다시 읽었을 때 게임 목록만 바꿉니다 (쿨다운은 유지).
    public void SetGames(IReadOnlyList<GameSettings> games)
    {
        IReadOnlyList<GameSettings> before = _games;
        _games = games;
        PrefetchStreamer.ForgetRemovedGames(before.Where(old => !games.Any(game => string.Equals(game.Name, old.Name, StringComparison.OrdinalIgnoreCase))));
    }

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

        if (RunningOther(g, start) is { } who)
        {
            return $"game already running: {who}";
        }

        if (_env.Age(start) < DispatchEnv.Settle)
        {
            bool? warm = _env.IsWarm(g);
            if (warm is true)
            {
                return "already warm";
            }

            int recentWarmMinutes = _env.RecentWarmProbeMinutes();
            if (warm is null && recentWarmMinutes > 0 && _env.RecentWarmProbeAt(g) is { } sampledAt)
            {
                TimeSpan age = now - sampledAt;
                if (age >= TimeSpan.Zero && age <= TimeSpan.FromMinutes(recentWarmMinutes))
                {
                    return $"recent probe warm {(long)age.TotalSeconds}s ago";
                }
            }

            return null; // 막 시작한 게임: 바쁨 검사 대신 캐시 점검, 실제 cold 또는 근거 없는 unknown 은 스트림
        }

        return _env.DiskBusy(g) ? "drive busy" : null;
    }

    // Settle 보다 오래전에 관측된 (또는 관측 기록이 없는 = 서비스 시작 전부터 돌던) 등록 이름 프로세스를 "이름(pid)" 로, 없으면 null. 끝난 pid 의 기록은 여기서 정리.
    private string? RunningOther(GameSettings g, ProcessStart start)
    {
        IReadOnlyList<(int Pid, string Exe)> others = _env.OtherProcesses(g, start);
        foreach (int gone in _seen.Where(kv => kv.Key != start.Pid && g.ExeNames.Contains(kv.Value.ExeName, StringComparer.OrdinalIgnoreCase) && !others.Any(o => o.Pid == kv.Key)).Select(kv => kv.Key).ToList())
        {
            _ = _seen.Remove(gone);
        }

        foreach ((int pid, string exe) in others)
        {
            if (!_seen.TryGetValue(pid, out ProcessStart? seen) || !seen.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase) || _env.Age(seen) > DispatchEnv.Settle)
            {
                return $"{exe}({pid})";
            }
        }

        return null;
    }

    /// 스트림을 불렀으면 true. 스트림 실패는 로그만 남기고 서비스를 멈추지 않습니다.
    public bool Handle(ProcessStart start, CancellationToken stop = default)
    {
        GameSettings? g = _games.FirstOrDefault(x => x.ExeNames.Contains(start.ExeName, StringComparer.OrdinalIgnoreCase));
        if (g is null)
        {
            return false;
        }

        if (!_seen.TryGetValue(start.Pid, out ProcessStart? first) || !first.ExeName.Equals(start.ExeName, StringComparison.OrdinalIgnoreCase))
        {
            _seen[start.Pid] = start; // 같은 pid 를 다시 감지해도 처음 관측 시각을 지킴 (pid 가 다른 exe 로 재사용되면 새로)
        }

        string queueWait = start.QueueTimestamp > 0
            ? Math.Max(0, Stopwatch.GetElapsedTime(start.QueueTimestamp).TotalMilliseconds).ToString("F1", CultureInfo.InvariantCulture)
            : "unknown";
        _log.Info($"{g.Name}: {start.ExeName} started (pid {start.Pid})" + (start.Route is { } route ? $" via {route}" : string.Empty)
            + $" [detected_at={start.DetectedAt:O}, queue_wait_ms={queueWait}]"); // 경로 표시 (cs_service.ps1 이 "via X" 를 파싱) 뒤에 감지 시각·큐 대기를 분리 기록
        if (!GamePolicy.AllowsPrewarm(g))
        {
            string policy = g.Paused ? "paused" : GamePolicy.ModeName(g.Mode);
            _log.Info($"{g.Name}: start stream skipped (game policy: {policy})");
            ReportStream(start, "skipped", "game policy", null, null, null);
            return false;
        }

        if (!_env.StartStreamEnabled())
        {
            _log.Info($"{g.Name}: start stream skipped (auto.start_stream_enabled is false)");
            ReportStream(start, "skipped", "start stream disabled", null, null, null);
            return false;
        }

        DateTimeOffset now = _now();
        if (_last.TryGetValue(g.Name, out DateTimeOffset prev) && now - prev < Cooldown)
        {
            ReportStream(start, "skipped", "start stream cooldown", null, null, null);
            return false;
        }

        if (!LauncherExes.Is(start.ExeName) && RestreamSkip(g, start, now) is { } why)
        {
            _log.Info($"{g.Name}: start stream skipped ({why})");
            ReportStream(start, "skipped", why, null, null, null);
            return false;
        }

        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(g.OriginRoot, "start stream", stop, g.OriginRoot, allowWhileYielded: true, preserveGameKey: g.Name);
        if (_coordinator is not null && lease is null)
        {
            _log.Info($"{g.Name}: start stream skipped (disk yielded)");
            ReportStream(start, "skipped", "disk yielded", null, null, null);
            return false;
        }

        _last[g.Name] = now;
        try
        {
            CancellationToken streamStop = lease?.Token ?? stop;
            int capSeconds = Math.Max(0, _env.StartStreamCapSeconds());
            long startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            long lastSampleAt = startedAt;
            long processReadBytes = 0;
            var previousReads = new Dictionary<int, long>();
            var boundary = new RuntimeLoadBoundary();
            int boundaryStopped = 0;
            int capStopped = 0;
            object sampleLock = new();
            using var issueStop = CancellationTokenSource.CreateLinkedTokenSource(streamStop);
            try
            {
                foreach ((int pid, long bytes) in _env.ReadCounters(g.ExeNames))
                {
                    previousReads[pid] = bytes;
                }
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
            {
                _log.Warn($"{g.Name}: start stream read counters unavailable ({e.GetType().Name})");
            }

            _ = boundary.Update(0, 0);

            void SampleReadCounters()
            {
                bool shouldCancel = false;
                bool sampleFailed = false;
                lock (sampleLock)
                {
                    if (issueStop.IsCancellationRequested)
                    {
                        return;
                    }

                    long timestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                    TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt, timestamp);
                    if (capSeconds > 0 && elapsed >= TimeSpan.FromSeconds(capSeconds))
                    {
                        Volatile.Write(ref capStopped, 1);
                        shouldCancel = true;
                    }
                    else if (System.Diagnostics.Stopwatch.GetElapsedTime(lastSampleAt, timestamp) >= TimeSpan.FromSeconds(1))
                    {
                        try
                        {
                            foreach ((int pid, long bytes) in _env.ReadCounters(g.ExeNames))
                            {
                                long delta = previousReads.TryGetValue(pid, out long previous) ? (bytes >= previous ? bytes - previous : bytes) : bytes;
                                processReadBytes = checked(processReadBytes + Math.Max(0, delta));
                                previousReads[pid] = bytes;
                            }

                            if (boundary.Update((long)elapsed.TotalMilliseconds, processReadBytes))
                            {
                                Volatile.Write(ref boundaryStopped, 1);
                                shouldCancel = true;
                            }
                        }
                        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
                        {
                            sampleFailed = true;
                        }

                        lastSampleAt = timestamp;
                    }
                }

                if (sampleFailed)
                {
                    _log.Warn($"{g.Name}: start stream read counter sample failed");
                }

                if (shouldCancel)
                {
                    issueStop.Cancel();
                }
            }

            async Task SampleLoop(CancellationToken token)
            {
                while (true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                    SampleReadCounters();
                }
            }

            bool CanIssue()
            {
                if (issueStop.IsCancellationRequested || !(lease?.IsValid ?? !stop.IsCancellationRequested))
                {
                    return false;
                }

                SampleReadCounters();
                return !issueStop.IsCancellationRequested && (lease?.IsValid ?? !stop.IsCancellationRequested);
            }

            lease?.NoteReadIssue("start stream planning", 0, false);
            using var sampleStop = new CancellationTokenSource();
            Task sampleTask = Task.Run(() => SampleLoop(sampleStop.Token));
            StreamStats r;
            bool complete;
            long progressBytes = 0;
            try
            {
                (r, complete) = _streamer.StreamCoordinated(g, issueStop.Token, (done, _) => Interlocked.Exchange(ref progressBytes, done), CanIssue,
                    (stage, bytes, isData) => lease?.NoteReadIssue(stage, bytes, isData));
            }
            catch (OperationCanceledException) when (Volatile.Read(ref boundaryStopped) != 0 || Volatile.Read(ref capStopped) != 0)
            {
                r = new StreamStats(Interlocked.Read(ref progressBytes), 0, 0, System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalSeconds, Aborted: true);
                complete = false;
            }
            finally
            {
                sampleStop.Cancel();
                try
                {
                    sampleTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    // 표본 추출은 스트림과 함께 멈췄습니다.
                }
            }

            double elapsedSeconds = System.Diagnostics.Stopwatch.GetElapsedTime(startedAt).TotalSeconds;
            double mbPerSecond = elapsedSeconds > 0 ? r.Bytes / (1_000_000d * elapsedSeconds) : 0;
            if (Volatile.Read(ref boundaryStopped) != 0)
            {
                _log.Info($"{g.Name}: start stream stopped at load boundary after {r.Bytes} bytes, {elapsedSeconds:F1} s ({mbPerSecond:F1} MB/s); boundary at {boundary.BoundaryMs} ms, peak {boundary.PeakBytes >> 20} MiB per 10 s, last window {boundary.LastWindowBytes >> 20} MiB, observed {boundary.ObservedMs / 1000d:F1} s");
                ReportStream(start, "stopped", "load boundary", r.Bytes, elapsedSeconds, mbPerSecond);
                return false;
            }

            if (Volatile.Read(ref capStopped) != 0)
            {
                _log.Info($"{g.Name}: start stream stopped at cap after {r.Bytes} bytes, {elapsedSeconds:F1} s ({mbPerSecond:F1} MB/s)");
                ReportStream(start, "stopped", "cap", r.Bytes, elapsedSeconds, mbPerSecond);
                return false;
            }

            if (!complete || r.Aborted || streamStop.IsCancellationRequested || !CanIssue())
            {
                _log.Info($"{g.Name}: start stream cancelled after {r.Bytes} bytes");
                ReportStream(start, "cancelled", "cancelled", r.Bytes, elapsedSeconds, mbPerSecond);
                return false;
            }

            _log.Info($"{g.Name}: streamed {r.Bytes} bytes in {r.Requests} requests, {r.Errors} errors, {elapsedSeconds:F3} s ({mbPerSecond:F1} MB/s)");
            ReportStream(start, "completed", null, r.Bytes, elapsedSeconds, mbPerSecond);
            return true;
        }
        catch (OperationCanceledException) when (!stop.IsCancellationRequested)
        {
            _log.Info($"{g.Name}: start stream cancelled (planning)"); // 계획 도중 대여가 취소됨: 서비스 종료가 아니면 다음 시작 스트림을 막지 않음
            ReportStream(start, "cancelled", "planning cancelled", null, null, null);
            return false;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            ReportStream(start, "cancelled", "service stopping", null, null, null);
            throw;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            _log.Info($"{g.Name}: no trace file yet, nothing to stream"); // 아직 추적을 만들지 않은 게임은 오류가 아님
            ReportStream(start, "skipped", "no trace file", null, null, null);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            _log.Error($"{g.Name}: stream failed", e);
            ReportStream(start, "failed", e.GetType().Name, null, null, null);
            return false;
        }
        catch (Exception e)
        {
            ReportStream(start, "failed", e.GetType().Name, null, null, null);
            throw;
        }
    }

    private void ReportStream(ProcessStart start, string status, string? reason, long? bytes, double? seconds, double? mbps)
    {
        try
        {
            _streamResult?.Invoke(start, new PlayStreamResult(status, reason, bytes, seconds, mbps));
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            // 기록 실패가 게임 시작 스트림에 영향을 주지 않게 합니다.
        }
    }
}
