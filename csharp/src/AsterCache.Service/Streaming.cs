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
}

/// 게임의 .prefetch.json 을 Python prefetch.cli 기본값 (physical, 간격 0, 파일 30% 이상이면 통째) 으로 스케줄해 PrefetchVirtualMemory 로 읽습니다.
/// 예산은 계획의 존 크기 (계획이 없거나 존이 0 이면 추적의 총 바이트) 이고, 게임별 max_stream_mib 가 상한입니다. 고정 1 GiB 는 더 큰 핫 집합을 잘랐습니다.
public sealed class PrefetchStreamer : IGameStreamer
{
    /// 계획의 존 크기 (바이트). 계획 파일이 없거나 읽을 수 없으면 null, 존이 비어 있으면 0.
    public static long? ZoneBytes(GameSettings game)
    {
        try
        {
            PlanFile plan = PlanFile.Load(game.Plan);
            return plan.Zone.Clusters * plan.ClusterBytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
    }

    /// 이번에 읽을 바이트의 예산: 존 크기 (없거나 0 이면 추적 총 바이트) 를 max_stream_mib 로 자른 값.
    public static long Budget(GameSettings game, long? zoneBytes, long traceBytes) =>
        Math.Min((long)game.MaxStreamMib << 20, zoneBytes is > 0 ? zoneBytes.Value : traceBytes);

    /// 접근 순서 앞쪽 이만큼 (게임 시작에 먼저 필요한 부분) 을 먼저 읽고 나머지는 물리 순서로.
    public const long HeadBytes = 256L << 20;

    public StreamStats Stream(GameSettings game) => ZoneStreamer.Run(Ops(game), StreamMethod.Pvm);

    /// 게임의 선읽기 순서 목록 (허용 루트 검사 포함). 스트림·식음 확인·고정이 같은 목록을 씁니다.
    public static List<ReadOp> Ops(GameSettings game)
    {
        if (!PlanGuards.Inside(game.OriginRoot, game.AllowedRoots, RealPaths.Real))
        {
            throw new PlanRejectedException("origin_root resolves outside allowed roots");
        }

        List<TraceEvent> rows = TraceLoader.LoadRanges(game.Trace, game.OriginRoot, RealPaths.Resolve,
            p => File.Exists(p) ? new FileInfo(p).Length : null);
        long budget = Budget(game, ZoneBytes(game), rows.Sum(r => r.Length));
        (List<ReadOp> ops, _) = Scheduler.Schedule(rows.Select(r => new ReadRange(r.Path, r.Offset, r.Length)), "physical", budget, 0,
            FileLayout.Get, p => new FileInfo(p).Length, headBytes: HeadBytes);
        return ops;
    }

    /// 고정할 파일: 선읽기 목록의 파일 중 허용 루트 안인 것 (읽기 전용으로만 쓰임). 매핑 총합이 capBytes 를 넘기 전의 앞쪽 파일만.
    public static IReadOnlyList<string> PinPaths(GameSettings game, long capBytes = long.MaxValue) =>
        WithinCap(Ops(game).Select(o => o.Path).Distinct(StringComparer.OrdinalIgnoreCase).Where(p => PlanGuards.Inside(p, game.AllowedRoots, RealPaths.Real)),
            capBytes, p => File.Exists(p) ? new FileInfo(p).Length : 0);

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

    /// 선읽기 목록에서 고르게 고른 몇 곳을 읽어 잰 1 MiB 평균 읽기 시간 (ms).
    public static double? ProbeReadMs(GameSettings game) => CacheProbe.AverageReadMs(CacheProbe.PickSamples(Ops(game)));
}

/// 감지된 프로세스를 게임에 대응시켜 스트림을 부릅니다. 같은 게임은 Cooldown 안에 한 번만 (런처·보조 프로세스가 연달아 떠도 중복 읽기 없음).
public sealed class GameDispatcher
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    private volatile IReadOnlyList<GameSettings> _games;
    private readonly IGameStreamer _streamer;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Dictionary<string, DateTimeOffset> _last = new(StringComparer.OrdinalIgnoreCase);

    public GameDispatcher(IReadOnlyList<GameSettings> games, IGameStreamer streamer, ILog log, Func<DateTimeOffset>? now = null)
    {
        _games = games;
        _streamer = streamer;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// 설정을 다시 읽었을 때 게임 목록만 바꿉니다 (쿨다운은 유지).
    public void SetGames(IReadOnlyList<GameSettings> games) => _games = games;

    /// 스트림을 불렀으면 true. 스트림 실패는 로그만 남기고 서비스를 멈추지 않습니다.
    public bool Handle(ProcessStart start)
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

        _last[g.Name] = now;
        try
        {
            StreamStats r = _streamer.Stream(g);
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
