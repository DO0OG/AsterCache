using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Profiles;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Tracer;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 처음 보는 게임의 자동 트레이스. 등록 게임 exe 가 떴는데 원시 트레이스 (plans\<이름>.trace.json) 가 없으면 ETW 세션을 열어 읽기를 모으고,
/// 게임이 끝나거나 시간·읽기량 상한에 닿으면 멈춰 수동 수집 (aster trace --live) 과 같은 파일을 씁니다. 한 번에 하나만 돕니다.
public sealed class AutoTracer
{
    private readonly AutoEnv _env;
    private readonly AutoStatus _status;
    private readonly ILog _log;
    private readonly Func<AsterSettings> _settings;
    private readonly LearnEnv _learn;
    private readonly OrbitShadow _shadow;
    private readonly object _lock = new();
    private volatile bool _shutdown;
    private CancellationTokenSource _shutdownSource = new(); // 서비스 종료에서 끝난 뒤 변환 (파일 배치 조회) 을 멈춤, _lock 으로 보호
    private CancellationTokenSource? _ioScanSource; // 돌고 있는 변환의 취소 (게임 시작·시간 예산·서비스 종료), _lock 으로 보호
    private bool _ioScanGameStarted; // _ioScanSource 가 게임 시작으로 취소됐는가, _lock 으로 보호
    private bool _stopCleanupFailed;
    private OrbitModel _orbitModel = new();
    private Run? _run;
    private readonly List<Run> _stopped = []; // 상한에 닿아 세션을 멈췄지만 게임이 끝나길 기다리는 기록 (저장 전)
    private readonly List<Run> _ioDeferred = []; // 끝난 플레이 중 같은 디스크의 다른 게임이 떠 있어 I/O 집계를 미룬 것 (그 게임이 끝나면 만듦), _lock 으로 보호. 서비스가 다시 시작되면 사라짐 (원시 읽기 요청은 메모리에만 있음)
    private readonly HashSet<string> _cheatLogged = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _policyLogged = new(StringComparer.OrdinalIgnoreCase); // 게임 정책으로 추적을 시작하지 않은 것을 이미 로그한 게임 (다시 허용되면 지움), _lock 으로 보호
    private readonly Dictionary<string, int> _ioSummaryAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _ioSummaryPersistenceFailed = new(StringComparer.OrdinalIgnoreCase);

    /// 두 번째 이후 플레이가 이보다 적게 읽었거나 (바이트) 첫 플레이의 1 % 보다 이벤트가 적으면 (메뉴만 켠 플레이 등) 저장하지 않고 회차로 세지 않습니다.
    public const long MinExtraRunBytes = 64L << 20;

    public long MinRunBytes { get; init; } = MinExtraRunBytes;

    public AutoTracer(AutoEnv env, AutoStatus status, ILog log, Func<AsterSettings> settings, LearnEnv? learn = null, OrbitShadow? shadow = null)
    {
        _env = env;
        _status = status;
        _log = log;
        _settings = settings;
        _learn = learn ?? LearnEnv.Real;
        _shadow = shadow ?? new OrbitShadow(status, log, env.Now);
    }

    public bool Active
    {
        get
        {
            lock (_lock)
            {
                return _run is not null;
            }
        }
    }

    /// 재실행 분리에서 현재 실행의 추적이 끝날 때까지 기다립니다.
    public Task EndGameSessionAsync(GameSettings game, DateTimeOffset exitedAt)
    {
        Run? run;
        lock (_lock)
        {
            run = _run;
            if (run is null || !string.Equals(run.Game.Name, game.Name, StringComparison.OrdinalIgnoreCase) || exitedAt < run.StartedAt)
            {
                return Task.CompletedTask;
            }

            run.RequestEnd();
        }

        return run.Finished.Task;
    }

    public static string RawTracePath(GameSettings g) => Path.Combine(Path.GetDirectoryName(g.Plan) ?? string.Empty, g.Name + ".trace.json");

    /// 트레이스에서 알게 된 가변 파일 (경로 → 사유) 표시 파일: 계획에서 뺄 파일. 낡은 트레이스를 치울 때 같이 치웁니다.
    public static string VolatilePath(GameSettings g) => Path.Combine(Path.GetDirectoryName(g.Plan) ?? string.Empty, g.Name + ".trace.volatile.json");

    /// 표시 파일을 읽음. 없거나 읽을 수 없으면 빈 목록 (표시는 힌트일 뿐).
    public static IReadOnlyDictionary<string, string> ReadVolatile(string path)
    {
        var marks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path) && JsonRead.ReadFile(path) is JObject o)
            {
                foreach ((string file, object? why) in o)
                {
                    marks[file] = why as string ?? string.Empty;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            marks.Clear();
        }

        return marks;
    }

    /// 병합 정책 표지 (계획 메타 merge_policy): 최신 유효 회차의 블록이 앞, 옛 회차는 뒤. 바뀌면 계획을 한 번 다시 봅니다.
    public const string MergePolicy = "newest-first-v1";

    /// run 파일 번호의 상한 (run1..run32).
    public const int MaxRunFiles = 32;

    // 병합 증가가 작아도 플레이 5회 또는 7일마다 새 콘텐츠를 다시 배울 기회를 줍니다.
    private const int RelearnAfterPlays = 5;
    private const int RelearnAfterDays = 7;

    /// 병합 직후 남기는 작은 메타 (이번 병합이 더한 새 고유 블록): plans\<이름>.trace.merge.json. run 번호가 마지막 run 파일과 같을 때만 믿습니다.
    public static string MergeMetaPath(GameSettings g) => Path.Combine(Path.GetDirectoryName(g.Plan) ?? string.Empty, g.Name + ".trace.merge.json");

    /// run 파일 한도 때문에 가장 오래된 회차를 지운 적이 있다는 표지: plans\<이름>.trace.rotated. 있으면 병합이 기존 병합본의 블록도 합집합에 넣어 지운 회차의 블록을 지킵니다.
    public static string RotatedMarkPath(GameSettings g) => Path.Combine(Path.GetDirectoryName(g.Plan) ?? string.Empty, g.Name + ".trace.rotated");

    /// 옛 형식에서 옮긴 run1 이 크기와 무관하게 유효하다는 표지: plans\<이름>.trace.legacy-run1.
    public static string LegacyRun1Path(GameSettings g) => Path.Combine(Path.GetDirectoryName(g.Plan) ?? string.Empty, g.Name + ".trace.legacy-run1");

    /// 업데이트로 바뀐 파일의 범위만 트레이스에서 뺐다는 표지: plans\<이름>.trace.updated. 이 표지보다 새 유효 플레이가 생길 때까지 다음 플레이를 다시 학습합니다 (RelearnDue).
    public static string UpdatedMarkPath(GameSettings g) => Path.Combine(Path.GetDirectoryName(g.Plan) ?? string.Empty, g.Name + ".trace.updated");

    /// 트레이스 파일에 나오는 경로 (중복 없이). 읽을 수 없으면 예외.
    public static IReadOnlyCollection<string> TracePaths(string path) =>
        JsonRead.ReadFile(path) is List<object?> rows ? [.. rows.Select(r => TraceEvents.FromJson(r).Path).Distinct(StringComparer.OrdinalIgnoreCase)] : throw new PyValueException("bad trace file");

    /// 트레이스 파일의 원본 행 (TracePaths 와 같은 읽기, 걸러내기·잘라내기 없음). 읽을 수 없으면 예외.
    public static IReadOnlyList<TraceEvent> TraceRows(string path) =>
        JsonRead.ReadFile(path) is List<object?> rows ? [.. rows.Select(TraceEvents.FromJson)] : throw new PyValueException("bad trace file");

    // 이 계획을 만든 시각 (stats.planned_at). 계획이 없거나 읽을 수 없거나 표지가 없으면 null.
    private static DateTime? PlannedAtUtc(GameSettings g)
    {
        try
        {
            return PlanFile.Load(g.Plan).Stats.TryGetValue(AutoRelocator.PlannedAtKey, out object? v) && v is long unix ? DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
    }

    /// 업데이트로 바뀐 파일의 행만 트레이스 병합본·run 파일·예열 목록 (선읽기 파일과 예열 전용 옆 파일) 에서 빼고 표지를 남깁니다. 호출하는 쪽이 계획 잠금 안에 있어야 합니다.
    /// 먼저 모든 파일을 읽어 뺄 행을 정하고 (읽을 수 없는 파일이 있으면 아무것도 바꾸기 전에 예외), 무언가 빠지는 파일만 원본을 .stale 로 복사해 두고 다시 씁니다 (이미 뺀 파일은 건드리지 않아 중간에 멈췄다 다시 불려도 .stale 은 원본 그대로).
    /// 다시 쓴 run 이 MinRunBytes 아래로 줄어도 다음 병합이 정리된 병합본을 기준으로 유지하도록 rotated 표지를 남깁니다. 다시 학습 표지는 파일을 실제로 다시 썼거나 표지가 없을 때만 (되풀이 호출이 재학습을 다시 켜지 않게), 마지막에: 다시 쓴 파일보다 새로워야 함.
    public static void DropChanged(GameSettings g, IReadOnlySet<string> paths, Action<string>? warn = null)
    {
        AsterCache.Core.Apply.DataLock.RequireHeld(g.Plan);
        string raw = RawTracePath(g);
        var files = new List<string> { raw, g.Trace, PlanOutput.WarmOnlyPath(g.Trace) };
        for (int run = 1; run <= MaxRunFiles; run++)
        {
            files.Add(TraceWriter.RunPath(raw, run));
        }
        var rewrites = new List<(string File, List<TraceEvent> Kept)>();
        foreach (string file in files.Where(File.Exists))
        {
            List<TraceEvent> rows = JsonRead.ReadFile(file) is List<object?> list ? [.. list.Select(TraceEvents.FromJson)] : throw new PyValueException("bad trace file");
            List<TraceEvent> kept = [.. rows.Where(e => !paths.Contains(e.Path))];
            if (kept.Count != rows.Count)
            {
                rewrites.Add((file, kept));
            }
        }

        // 집계 곁 파일은 원시 회차와 따로 따짐: 짝 (지문) 이 안 맞으면 무효, 바뀐 파일이 파일 표에 있으면 낡은 집계 (치움), 원시 회차만 다시 쓰이면 지문만 새 원시 파일에 맞춰 갱신
        var rewrittenPaths = rewrites.Select(rewrite => rewrite.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var changedPaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        var invalidatedIoSidecars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var staleIoSidecars = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ioSidecarPairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rebindIoSidecars = new List<(string Sidecar, string PairedTrace, IoSummary Summary)>();
        var rawAsideSuffixes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        void CheckPairedSummary(string sidecar, string pairedTrace, bool standalone)
        {
            if (!File.Exists(sidecar))
            {
                return;
            }

            IoSummary? summary = standalone ? IoSummaryStore.ReadStandaloneForRaw(sidecar, pairedTrace) : IoSummaryStore.ReadForRun(pairedTrace);
            if (summary is null)
            {
                invalidatedIoSidecars.Add(sidecar);
            }
            else if (summary.Files.Any(file => changedPaths.Contains(file.Path))
                || (summary.ReadModes ?? []).Any(mode => changedPaths.Contains(mode.Path)))
            {
                staleIoSidecars.Add(sidecar);
                ioSidecarPairs[sidecar] = pairedTrace;
            }
            else if (rewrittenPaths.Contains(pairedTrace))
            {
                rebindIoSidecars.Add((sidecar, pairedTrace, summary));
            }
        }

        try
        {
            CheckPairedSummary(IoSummaryStore.RunPath(raw), raw, standalone: false);
            for (int run = 1; run <= MaxRunFiles; run++)
            {
                string runPath = TraceWriter.RunPath(raw, run);
                CheckPairedSummary(IoSummaryStore.RunPath(runPath), runPath, standalone: false);
            }

            foreach (string standalone in IoSummaryStore.StandalonePaths(raw))
            {
                CheckPairedSummary(standalone, raw, standalone: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"{g.Name}: I/O summary files could not be checked for the game update ({e.GetType().Name}); stale summaries stay invalid by their raw fingerprint");
        }

        bool anyInvalidated = invalidatedIoSidecars.Count > 0 || staleIoSidecars.Count > 0;
        DateTime? planned = rewrites.Count > 0 || anyInvalidated ? PlannedAtUtc(g) : null; // 이 계획을 만든 뒤에 보관한 사본만 이번 업데이트의 것
        bool ThisUpdate(string copy) => planned is { } at && File.GetLastWriteTimeUtc(copy) >= at.AddSeconds(1); // planned_at 은 초 단위로 잘리므로 같은 초에 보관된 이전 사본은 이번 것으로 치지 않음
        string? Aside(string path)
        {
            string stalePath = path + AutoRelocator.StaleSuffix;
            string updatePath = path + AutoRelocator.UpdateSuffix;
            return !File.Exists(stalePath) ? stalePath : ThisUpdate(stalePath) ? null : !ThisUpdate(updatePath) ? updatePath : null;
        }

        if (rewrites.Count > 0 && File.Exists(TraceStageIndex.IndexPath(raw)))
        {
            File.Delete(TraceStageIndex.IndexPath(raw)); // 원시 범위가 바뀌면 이전 색인을 쓰지 않음
        }

        foreach ((string file, List<TraceEvent> kept) in rewrites)
        {
            // 있는 .stale 이 이번 업데이트의 것이면 (중간에 멈췄다 다시 불림) 그대로 두고, 이전 업데이트의 것이면 덮지 않고 이번 원본을 .stale.update 로 따로 보관 (옛 저널·원본 보관 불변 조건). 계획 시각을 모르면 이번 업데이트의 사본으로 확인할 수 없으므로 .stale.update 를 덮어 이번 원본을 보관 (맨 처음 .stale 은 유지).
            string? target = Aside(file);
            if (target is not null)
            {
                File.Copy(file, target, true);
                File.SetLastWriteTimeUtc(target, DateTime.UtcNow); // 복사는 원본의 수정 시각을 따라가므로 보관한 때로 맞춤
            }

            rawAsideSuffixes[file] = target is not null
                ? target[file.Length..]
                : new[] { AutoRelocator.StaleSuffix, AutoRelocator.UpdateSuffix }
                    .FirstOrDefault(suffix => File.Exists(file + suffix) && ThisUpdate(file + suffix));

            string temp = file + ".tmp";
            string text = PyJson.Dumps(kept.Select(e => e.ToJson()).ToList(), ensureAscii: false) + "\n";
            AsterCache.Core.DurableFile.WriteAllBytes(temp, PyJson.Utf8.GetBytes(PyJson.ToCrlf(text)));
            File.Move(temp, file, true);
        }

        try
        {
            foreach (string sidecar in staleIoSidecars)
            {
                string? suffix = ioSidecarPairs.TryGetValue(sidecar, out string? pairedRaw)
                    ? rawAsideSuffixes.GetValueOrDefault(pairedRaw)
                    : null;
                string? target = suffix is not null ? sidecar + suffix : Aside(sidecar);
                try
                {
                    if (target is not null)
                    {
                        File.Copy(sidecar, target, true);
                        File.SetLastWriteTimeUtc(target, DateTime.UtcNow);
                    }

                    File.Delete(sidecar);
                    File.Delete(IoSummaryStore.InvalidPath(sidecar));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    MarkInvalid(sidecar, g, warn, e);
                }
            }

            foreach (string sidecar in invalidatedIoSidecars)
            {
                try
                {
                    File.Delete(sidecar);
                    File.Delete(IoSummaryStore.InvalidPath(sidecar));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    MarkInvalid(sidecar, g, warn, e);
                }
            }

            foreach ((string sidecar, string pairedTrace, IoSummary summary) in rebindIoSidecars)
            {
                IoSummaryStore.Write(sidecar, summary with { RawFingerprint = IoSummaryStore.RawFingerprint(pairedTrace) });
            }

            if (anyInvalidated)
            {
                File.Delete(IoSummaryStore.TriedPath(raw)); // 업데이트로 무효화된 집계는 새 추적으로 다시 채울 수 있음
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"{g.Name}: I/O summary files could not be cleaned for the game update ({e.GetType().Name}); stale summaries stay invalid by their raw fingerprint"); // 집계는 무효로 끝나고 원시 추적 정리는 계속
        }

        if (rewrites.Count > 0)
        {
            File.WriteAllText(RotatedMarkPath(g), "1");
            TracePhases.Recount(g); // 다시 쓴 run 을 반영한 common 구분을 미리 남김
        }

        if (rewrites.Count > 0 || !File.Exists(UpdatedMarkPath(g)))
        {
            File.WriteAllText(UpdatedMarkPath(g), "1");
        }
    }

    // 낡은 집계를 지우지 못했을 때 (잠김): 원시 추적이 그대로라 지문이 다시 맞을 수 있으므로 곁에 무효 표지를 남겨 읽기가 거부하게 함 (다음 정리가 집계와 표지를 함께 지움)
    private static void MarkInvalid(string sidecar, GameSettings g, Action<string>? warn, Exception cause)
    {
        try
        {
            IoSummaryStore.MarkInvalid(sidecar);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            warn?.Invoke($"{g.Name}: stale I/O summary not removed ({cause.GetType().Name}) and its invalid mark not written ({e.GetType().Name})");
        }
    }

    /// 지금까지 모은 유효 플레이 수: 원시 트레이스가 없으면 0, run 파일 없는 원시 트레이스 하나 (옛 형식) 는 1 로 셈하고 (호환),
    /// 이어서 있는 run1, run2 ... 는 읽기량이 minBytes 이상인 것만 셈 (읽기량을 못 얻으면 셈). readBytes 가 없으면 이어서 있는 run 파일을 모두 셈. 따로 저장하는 상태 없음.
    public static int RunCount(GameSettings g, Func<string, bool> exists, Func<string, long?>? readBytes = null, long minBytes = MinExtraRunBytes)
    {
        string raw = RawTracePath(g);
        if (!exists(raw))
        {
            return 0;
        }

        if (!exists(TraceWriter.RunPath(raw, 1)))
        {
            return 1;
        }

        int valid = 0;
        for (int n = 1; n <= MaxRunFiles && exists(TraceWriter.RunPath(raw, n)); n++)
        {
            if (n == 1 && exists(LegacyRun1Path(g)) || readBytes?.Invoke(TraceWriter.RunPath(raw, n)) is not { } bytes || bytes >= minBytes)
            {
                valid++;
            }
        }

        return valid;
    }

    /// 있는 run 파일 중 가장 큰 번호 (끊겨 있어도 가장 큰 것), run 파일 없이 원시 트레이스만 있으면 1, 원시 트레이스가 없으면 0. 다음 저장 번호는 이 값 + 1 (유효 회차 수가 아님: 덮어쓰기 금지).
    public static int LastRunNumber(GameSettings g, Func<string, bool> exists)
    {
        string raw = RawTracePath(g);
        if (!exists(raw))
        {
            return 0;
        }

        for (int n = MaxRunFiles; n >= 1; n--)
        {
            if (exists(TraceWriter.RunPath(raw, n)))
            {
                return n;
            }
        }

        return 1;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Length, long Ticks, long? Bytes)> RunBytesCache = new(StringComparer.OrdinalIgnoreCase);

    /// run 파일 하나가 읽은 양 (행 길이 합). 파일 크기·수정 시각이 같으면 캐시 값을 써서 큰 JSON 을 매번 읽지 않습니다. 없거나 읽을 수 없으면 null.
    public static long? RunReadBytes(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return null;
            }

            long ticks = info.LastWriteTimeUtc.Ticks;
            if (RunBytesCache.TryGetValue(path, out var hit) && hit.Length == info.Length && hit.Ticks == ticks)
            {
                return hit.Bytes;
            }

            long? bytes = JsonRead.ReadFile(path) is List<object?> rows ? rows.Sum(r => TraceEvents.FromJson(r).Length) : null;
            RunBytesCache[path] = (info.Length, ticks, bytes);
            return bytes;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
    }

    /// 다음 플레이를 트레이스할지의 공통 판정 (게임 시작 두 곳이 같이 씀). Valid = 유효 회차 수, Limit = 지금 단계의 회차 상한.
    public readonly record struct TraceNext(int Valid, int Limit, bool Wanted, int? Coverage);

    /// 유효 회차가 trace_runs 에 못 미치면 기존 정책대로 한 번 더. 채운 뒤에는 병합 증가가 충분하거나 주기·업데이트 재학습이 필요할 때 한 번 더.
    /// 커버리지 (핫셋 ÷ 프로세스 누적 읽기량) 는 세션이 짧으면 100% 로 쏠려 판단 근거가 못 되므로 표시에만 씁니다. 안티치트 판정은 호출한 쪽 (Start) 이 먼저 합니다.
    public static TraceNext NextTrace(GameSettings g, AutoSettings a, AutoEnv env, AutoStatus status, long minBytes = MinExtraRunBytes)
    {
        int valid = RunCount(g, env.FileExists, env.RunReadBytes, minBytes);
        int last = LastRunNumber(g, env.FileExists);
        bool room = last < MaxRunFiles;
        bool relearn = RelearnDue(g, env, status, minBytes);
        if (valid < a.TraceRuns)
        {
            return new TraceNext(valid, a.TraceRuns, room || relearn, null);
        }

        int limit = Math.Max(Math.Max(a.TraceRuns, a.TraceRunsMax), relearn ? valid + 1 : 0);
        int? coverage = status.Get(g.Name)?.CoveragePercent;
        bool more = relearn || room && valid < limit && ReadMergeGainMib(g, last) is { } gain && gain >= a.TraceMergeMinMib; // 커버리지는 표시용: 짧은 재실행이 100% 로 덮어써도 멈추지 않게 병합 증가량만 봄
        return new TraceNext(valid, limit, more, coverage);
    }

    private static bool RelearnDue(GameSettings g, AutoEnv env, AutoStatus status, long minBytes)
    {
        AutoGameEntry? entry = status.Get(g.Name);
        DateTimeOffset? lastTrace = LastValidRunTime(g, env, minBytes);
        if (entry?.Detail?.StartsWith(AutoRelocator.ReplanDetail, StringComparison.Ordinal) == true && (lastTrace is null || lastTrace.Value <= entry.At))
        {
            return true;
        }

        if (env.FileWriteTime(UpdatedMarkPath(g)) is { } updated && (lastTrace is null || lastTrace.Value <= updated))
        {
            return true; // 업데이트로 바뀐 파일의 범위를 뺐고 그 뒤 새 플레이가 아직 없음: 다음 플레이로 바뀐 파일을 다시 학습
        }

        if (lastTrace is not { } last)
        {
            return false;
        }

        return env.Now() - last >= TimeSpan.FromDays(RelearnAfterDays)
            || PlaysAfter(status, g.Name, last) >= RelearnAfterPlays;
    }

    private static int PlaysAfter(AutoStatus status, string game, DateTimeOffset last) =>
        status.Plays().TryGetValue(game, out IReadOnlyList<DateTimeOffset>? plays) ? plays.Count(at => at > last) : 0;

    private static DateTimeOffset? LastValidRunTime(GameSettings g, AutoEnv env, long minBytes)
    {
        string raw = RawTracePath(g);
        if (!env.FileExists(raw))
        {
            return null;
        }

        int last = LastRunNumber(g, env.FileExists);
        if (!env.FileExists(TraceWriter.RunPath(raw, 1)))
        {
            return env.FileWriteTime(raw); // 옛 형식은 원시 트레이스 자체가 첫 유효 회차
        }

        for (int number = last; number >= 1; number--)
        {
            string path = TraceWriter.RunPath(raw, number);
            if (!env.FileExists(path))
            {
                continue;
            }

            if (number == 1 && env.FileExists(LegacyRun1Path(g)) || env.RunReadBytes(path) is not { } bytes || bytes >= minBytes)
            {
                return env.FileWriteTime(path);
            }
        }

        return null;
    }

    /// 병합 결과의 지문: 행 순서대로 (경로, 오프셋, 길이) 의 SHA-256 (소문자 hex). 블록 집합이나 순서가 바뀌면 달라지고 t_ms·cost 는 보지 않습니다. 읽을 수 없으면 null.
    public static string? TraceFingerprint(string path)
    {
        try
        {
            if (JsonRead.ReadFile(path) is not List<object?> rows)
            {
                return null;
            }

            using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            foreach (object? row in rows)
            {
                TraceEvent e = TraceEvents.FromJson(row);
                sha.AppendData(System.Text.Encoding.UTF8.GetBytes($"{e.Path}\n{e.Offset}\n{e.Length}\n"));
            }

            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
    }

    /// 단계 색인이 없는 게임에 그것을 얻으려고 한 번 더 추적하는 최대 횟수 (저장되지 않은 run 도 시도로 셈).
    public const int MaxStageIndexTries = 2;

    /// 단계 색인을 얻으려는 추가 추적의 차례 (1..MaxStageIndexTries), 해당 없으면 0. 학습 진행 표시 (NextTrace) 에는 들어가지 않습니다. 추적이 꺼졌거나 금지된 게임, run 파일 여유가 없는 게임은 0.
    public static int StageTraceAttempt(GameSettings g, AutoSettings a, AutoEnv env)
    {
        string raw = RawTracePath(g);
        if (!a.Trace || LastRunNumber(g, env.FileExists) >= MaxRunFiles || !env.FileExists(raw) || env.FileExists(TraceStageIndex.IndexPath(raw)) || env.TraceBlocker(g) is not null)
        {
            return 0;
        }

        int tried = env.StageIndexTries(raw);
        return tried < MaxStageIndexTries ? tried + 1 : 0;
    }

    public const int MaxIoSummaryTries = IoSummaryStore.MaxTried;

    /// 게임의 가장 최근 플레이에서 로딩이었던 구간 (로딩 판정이 쌓은 값, 서비스를 조립하는 쪽이 채움): 플레이마다 남기는 디스크 완료 요약에 구간 경계로 들어갑니다. 없으면 구간 없이 남김.
    public Func<string, IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)>>? LoadPhasesOf { get; set; }

    /// 이 게임의 지금 플레이에 디스크 완료 요약만 남기는 가벼운 세션을 열 차례인가: 이미 원시 트레이스가 있는 (학습이 끝난) 게임에서 트레이스를 허용하는 설정이고 안티치트가 막지 않을 때.
    /// 원시 트레이스를 뜨는 플레이는 그 세션이 같은 요약을 남기므로 여기서 거르지 않고 부르는 쪽 (Start) 이 따집니다. 정책 (끔·일시 중지) 은 Start 가 먼저 거릅니다.
    public static bool PlaySummaryWanted(GameSettings g, AutoSettings a, AutoEnv env) =>
        a.Trace && a.IoSummaryEnabled && g.AllowedRoots.Count > 0 && env.FileExists(RawTracePath(g)) && env.TraceBlocker(g) is null;

    /// 읽기 방식 집계가 있는 서로 다른 플레이 두 번을 모으기 위한 추가 추적 차례 (1..MaxIoSummaryTries), 해당 없으면 0.
    public static int IoSummaryAttempt(GameSettings g, AutoSettings a, AutoEnv env)
    {
        string raw = RawTracePath(g);
        if (!a.Trace || !a.IoSummaryEnabled || !env.FileExists(raw) || env.TraceBlocker(g) is not null)
        {
            return 0;
        }

        if (ReadModeSessionCount(g, env) >= 2)
        {
            return 0;
        }

        int tried = env.IoSummaryTries(raw) ?? 0;
        return tried < MaxIoSummaryTries ? tried + 1 : 0;
    }

    /// 읽기 방식이 담긴 I/O 집계가 있는 서로 다른 플레이 수 (같은 세션의 재저장은 한 번).
    private static int ReadModeSessionCount(GameSettings g, AutoEnv env)
    {
        string raw = RawTracePath(g);
        var sessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddReadModeSession(string sidecar, string pairedTrace, bool standalone)
        {
            IoSummary? summary = env.ReadIoSummary(sidecar, pairedTrace, standalone);
            if (summary is { HasReadModes: true, SessionId.Length: > 0 })
            {
                sessions.Add(summary.SessionId);
            }
        }

        string directSidecar = IoSummaryStore.RunPath(raw);
        if (env.FileExists(directSidecar))
        {
            AddReadModeSession(directSidecar, raw, standalone: false);
        }

        foreach (string standalone in IoSummaryStore.StandalonePaths(raw))
        {
            if (env.FileExists(standalone))
            {
                AddReadModeSession(standalone, raw, standalone: true);
            }
        }

        for (int number = 1; number <= MaxRunFiles; number++)
        {
            string run = TraceWriter.RunPath(raw, number);
            string sidecar = IoSummaryStore.RunPath(run);
            if (env.FileExists(run) && env.FileExists(sidecar))
            {
                AddReadModeSession(sidecar, run, standalone: false);
            }
        }

        return sessions.Count;
    }

    /// 로드 구간 목록이 이만큼의 시작 시각 있는 플레이로 만들어져야 로드 하나의 블록이 두 번 이상 겹쳐 점수가 매겨집니다 (한 플레이만으로는 점수가 전부 같음).
    public const int MinTimedPlays = 2;

    /// 목록 보강 추가 추적의 최대 횟수: 부족한 플레이는 MinTimedPlays 개이고, 한 번은 시작 시각이 기록되지 못한 플레이를 위한 여유입니다.
    public const int MaxLoadListTries = MinTimedPlays + 1;

    /// 로드 구간 목록이 만들어진 시작 시각 있는 플레이 수 (저장된 목록 파일의 값, 목록이 없으면 0). 게임 시작 경로라 큰 .io.json 을 다시 읽지 않습니다.
    private static int TimedPlays(GameSettings g, AutoEnv env) => env.LoadListTimedPlays(RawTracePath(g));

    /// 시작 시각이 있는 플레이가 MinTimedPlays 번 미만이라 로드 구간 목록이 빈약할 때 한 번 더 추적하는 차례 (1..MaxLoadListTries), 해당 없으면 0. I/O 집계를 남기지 않는 설정이면 집계가 안 생기므로 0.
    public static int LoadListAttempt(GameSettings g, AutoSettings a, AutoEnv env)
    {
        string raw = RawTracePath(g);
        if (!a.Trace || !a.IoSummaryEnabled || !env.FileExists(raw) || env.TraceBlocker(g) is not null || TimedPlays(g, env) >= MinTimedPlays)
        {
            return 0;
        }

        int tried = env.LoadListTries(raw);
        return tried < MaxLoadListTries ? tried + 1 : 0;
    }

    /// 게임 시작 때 트레이스하지 않기로 한 이유를 한 줄로 (판정에 쓴 값들).
    public string NotNeededLine(GameSettings g)
    {
        AutoSettings auto = _settings().Auto;
        TraceNext next = NextTrace(g, auto, _env, _status, MinRunBytes);
        DateTimeOffset? last = LastValidRunTime(g, _env, MinRunBytes);
        int plays = last is { } at ? PlaysAfter(_status, g.Name, at) : 0;
        string raw = RawTracePath(g);
        string relearn = RelearnDue(g, _env, _status, MinRunBytes) ? "yes" : "no";
        string stage = _env.FileExists(TraceStageIndex.IndexPath(raw)) ? "yes" : "no";
        return $"auto trace not needed (valid {next.Valid}/limit {next.Limit}, relearn {relearn} (plays {plays}/{RelearnAfterPlays}), "
            + $"stage index {stage}, io summaries {ReadModeSessionCount(g, _env)}, load list from {TimedPlays(g, _env)} timed plays)";
    }

    private int IoSummaryAttemptForService(GameSettings g, AutoSettings a)
    {
        string raw = RawTracePath(g);
        lock (_lock)
        {
            if (_ioSummaryPersistenceFailed.Contains(raw))
            {
                return 0; // 시도 횟수를 디스크에 못 남긴 게임은 서비스가 도는 동안 더 추적하지 않음
            }

            // 업데이트·회전이 디스크의 시도 횟수 파일을 지웠으면 (새 세대) 메모리 횟수도 같이 초기화
            int attempted = _ioSummaryAttempts.GetValueOrDefault(raw);
            if (_env.IoSummaryTries(raw) is { } onDisk)
            {
                attempted = Math.Min(attempted, onDisk); // 읽기에 실패했을 때 (null) 는 메모리 횟수를 낮추지 않음
            }

            _ioSummaryAttempts[raw] = attempted;
            if (attempted >= MaxIoSummaryTries)
            {
                return 0;
            }

            int next = IoSummaryAttempt(g, a, _env);
            return next == 0 ? 0 : Math.Max(next, attempted + 1);
        }
    }

    /// 끝난 뒤 변환 (게임 폴더 파일 배치 조회) 의 시간 예산: 넘으면 집계만 버리고 원시 기록 저장은 계속합니다.
    public TimeSpan IoScanBudget { get; init; } = TimeSpan.FromSeconds(30);

    /// 이 게임의 다음 플레이를 트레이스할지 (게임 시작 판정과 Start 가 같은 NextTrace 를 씀).
    public bool WantsRun(GameSettings g) => WantsRawTrace(g) || PlaySummaryWanted(g, _settings().Auto, _env);

    /// 이 게임의 다음 플레이에 원시 트레이스를 뜰 필요가 있는가 (필요한 회차, 단계 색인, 읽기 방식 집계, 로드 목록 보강 중 하나). 디스크 완료 요약만 남기는 플레이는 여기에 들지 않습니다.
    public bool WantsRawTrace(GameSettings g)
    {
        AutoSettings auto = _settings().Auto;
        return NextTrace(g, auto, _env, _status, MinRunBytes).Wanted
            || StageTraceAttempt(g, auto, _env) > 0
            || IoSummaryAttemptForService(g, auto) > 0
            || LoadListAttempt(g, auto, _env) > 0;
    }

    // 직전 병합 (run lastRun) 이 더한 새 고유 블록 (MiB). 메타가 없거나 다른 병합의 것이거나 읽을 수 없으면 null.
    private static long? ReadMergeGainMib(GameSettings g, int lastRun)
    {
        try
        {
            string path = MergeMetaPath(g);
            return File.Exists(path) && JsonRead.ReadFile(path) is JObject o && o.TryGetValue("run", out object? r) && r is long run && run == lastRun
                && o.TryGetValue("new_mib", out object? n) && n is long mib ? mib : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
    }

    /// 가져온 프로필·합성 트레이스 (plans\<이름>.boot.trace.json). 원시 트레이스가 없을 때만 계획·예열의 입력이 되고, 첫 실행 기록은 막지 않습니다.
    public static string BootTracePath(GameSettings g) => Path.Combine(Path.GetDirectoryName(g.Plan) ?? string.Empty, g.Name + GameProfile.BootTraceSuffix);

    /// 계획·예열이 읽을 트레이스: 원시가 있으면 원시, 없으면 boot, 둘 다 없으면 null.
    public static string? PlanTracePath(GameSettings g, Func<string, bool> exists) =>
        exists(RawTracePath(g)) ? RawTracePath(g) : exists(BootTracePath(g)) ? BootTracePath(g) : null;

    /// boot 트레이스 파일의 "source" 표시 (프리페치 기록으로 만든 것이면 "prefetch", 가져온 프로필 등 표시가 없으면 null). 읽을 수 없으면 null.
    public static string? TraceSourceOf(string path)
    {
        try
        {
            return JsonRead.ReadFile(path) is JObject o && o.TryGetValue("source", out object? v) ? v as string : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
    }

    /// 부트스트랩 S2: 기록 없는 게임의 boot 트레이스를 Windows 프리페치 기록 (.pf) 으로 만듭니다. 만든 범위 수, 못 만들면 0.
    /// 프리페처가 꺼져 있거나 맞는 기록이 없거나, 원시·boot 트레이스가 이미 있으면 (공유 프로필 S1 이 먼저) 0. 쓸 때도 이미 있으면 덮지 않습니다.
    public static int BootFromPrefetch(GameSettings g, Func<bool>? prefetcherOn = null, string? prefetchDir = null, IReadOnlyDictionary<uint, string>? letters = null)
    {
        string boot = BootTracePath(g);
        if (File.Exists(RawTracePath(g)) || File.Exists(boot) || !(prefetcherOn ?? SystemProbes.PrefetcherOn)()
            || PrefetchFiles.BootTrace(g.ExeNames, g.OriginRoot, prefetchDir, letters) is not { Count: > 0 } events)
        {
            return 0;
        }

        string temp = $"{boot}.{Guid.NewGuid():N}.tmp"; // 고유 이름: 동시에 만드는 쪽끼리 임시 파일을 공유하지 않음
        try
        {
            File.WriteAllBytes(temp, PrefetchFiles.BootTraceBytes(events));
            File.Move(temp, boot, false); // 그 사이 다른 쪽이 boot 를 만들었으면 IOException
            return events.Count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception d) when (d is IOException or UnauthorizedAccessException)
            {
                // 정리도 못 하면 남겨 둠
            }

            return 0;
        }
    }

    // 안티치트 게임은 ETW 커널 트레이스를 걸지 않음 (보호 드라이버가 세션을 보고 클라이언트를 끊음). 등록 정보 (설정을 읽을 때 Riot 규칙으로 채워짐), 폴더 이름 순으로 보고 (폴더 열거는 한 번만) 탐지 실패도 막음.
    private string? TraceBlocker(GameSettings g) => _env.TraceBlocker(g);

    // 게임별 정책이 추적 (ETW 세션·학습) 을 막는가: 끄거나 일시 중지한 게임. 설정을 다시 읽은 최신 항목으로 봅니다.
    private bool PolicyBlocksTracking(GameSettings g, out GameSettings latest)
    {
        latest = _settings().Games.FirstOrDefault(x => string.Equals(x.Name, g.Name, StringComparison.OrdinalIgnoreCase)) ?? g;
        return !GamePolicy.AllowsTracking(latest);
    }

    public void Start(GameSettings g) => _ = Start(g, out _);

    // 저장 마무리 중인 실행만 완료 신호를 돌려줘 시작 작업이 한 번 재시도하게 함.
    // wantedAtGameStart: 게임 시작 때 이미 "트레이스할 차례"로 판정했고 직전 저장을 기다린 재시도이면 true (그 사이 저장으로 NextTrace 가 바뀌어도 다시 거르지 않음).
    public bool Start(GameSettings g, out Task? previousFinish, bool wantedAtGameStart = false)
    {
        previousFinish = null;
        if (PolicyBlocksTracking(g, out GameSettings latest))
        {
            bool first;
            lock (_lock)
            {
                first = _policyLogged.Add(g.Name);
            }

            if (first)
            {
                _log.Info($"{g.Name}: trace skipped (game policy: {(latest.Paused ? "paused" : GamePolicy.ModeName(latest.Mode))})");
            }

            return false;
        }

        lock (_lock)
        {
            _ = _policyLogged.Remove(g.Name);
        }

        if (TraceBlocker(g) is { } cheat)
        {
            bool first;
            lock (_lock)
            {
                first = _cheatLogged.Add(g.Name);
            }

            if (first)
            {
                _log.Info($"{g.Name}: trace skipped (anti-cheat: {cheat})");
            }

            return false;
        }

        string? refused;
        ReadAhead? learn = null;
        int done; // 이미 모은 유효 플레이 수
        bool firstRun; // 원시 트레이스가 아직 없는 첫 기록 (아니면 병합용 추가 기록: 상태·선행 읽기를 건드리지 않음). 유효 회차가 0 이어도 run1 이 있으면 추가 기록
        TraceNext next;
        int stageAttempt; // 단계 색인만을 위한 추가 추적의 차례 (아니면 0)
        int ioSummaryAttempt; // 물리 읽기 집계를 위한 추가 추적의 차례 (아니면 0)
        int loadListAttempt; // 로드 구간 목록 보강을 위한 추가 추적의 차례 (아니면 0)
        bool summaryOnly; // 원시 트레이스 없이 디스크 완료 요약만 남기는 플레이 (위 어느 목적도 아닐 때)
        int runNumber = 0;
        lock (_lock)
        {
            if (_shutdown)
            {
                _shutdown = false;
                _shutdownSource = new CancellationTokenSource();
                _orbitModel = new OrbitModel();
            }

            AutoSettings auto = _settings().Auto;
            next = NextTrace(g, auto, _env, _status, MinRunBytes);
            done = next.Valid;
            stageAttempt = next.Wanted ? 0 : StageTraceAttempt(g, auto, _env);
            ioSummaryAttempt = next.Wanted ? 0 : IoSummaryAttemptForService(g, auto);
            loadListAttempt = next.Wanted ? 0 : LoadListAttempt(g, auto, _env);
            summaryOnly = !next.Wanted && stageAttempt == 0 && ioSummaryAttempt == 0 && loadListAttempt == 0 && PlaySummaryWanted(g, auto, _env);
            firstRun = !_env.FileExists(RawTracePath(g));
            if (_run is { } running)
            {
                if (running.Finishing || running.EndRequested)
                {
                    previousFinish = running.Finished.Task;
                }

                return false;
            }

            if (_stopCleanupFailed || !wantedAtGameStart && !next.Wanted && stageAttempt == 0 && ioSummaryAttempt == 0 && loadListAttempt == 0 && !summaryOnly)
            {
                return false;
            }

            Run? stopped = _stopped.FirstOrDefault(r => string.Equals(r.Game.Name, g.Name, StringComparison.OrdinalIgnoreCase));
            if (stopped is not null)
            {
                if (stopped.Saving)
                {
                    previousFinish = stopped.SaveFinished.Task;
                }

                return false;
            }

            refused = _env.Capabilities(g.OriginRoot).PrewarmRefusal; // 시스템 SSD·SSD·종류를 모르는 드라이브는 건드리지 않음 (시스템 HDD 는 읽기 전용 트레이스만)
            if (refused is null)
            {
                try
                {
                    if (!summaryOnly && LastRunNumber(g, _env.FileExists) >= MaxRunFiles)
                    {
                        CompactRunsForRelearn(g);
                        done = RunCount(g, _env.FileExists, _env.RunReadBytes, MinRunBytes);
                        AutoSettings current = _settings().Auto;
                        int configuredLimit = Math.Max(current.TraceRuns, current.TraceRunsMax);
                        next = next with { Valid = done, Limit = Math.Max(configuredLimit, done + 1) };
                    }

                    LearnSettings ls = _settings().Auto.Learn;
                    learn = ls.Enabled && firstRun && GamePolicy.AllowsPrewarm(latest) ? AutoLearn.Create(g, ls, _env.PhysicalRamBytes(), _learn) : null; // 시스템 SSD·SSD 는 위에서 이미 걸러짐
                    var exits = new GameExitTracker(_env.Now);
                    exits.ObserveStarted(g);
                    _run = new Run(g, summaryOnly ? OpenSummarySession(g) : _env.StartTrace(g.ExeNames, learn is null ? null : AutoLearn.Listener(learn), auto.IoSummaryEnabled ? g.AllowedRoots : null), _env.Now(), exits)
                    {
                        Learn = learn,
                        Number = LastRunNumber(g, _env.FileExists) + 1,
                        RawWrittenAtStart = _env.FileWriteTime(RawTracePath(g)),
                        IoSummaryEnabled = auto.IoSummaryEnabled,
                        SummaryOnly = summaryOnly,
                        RawEnded = summaryOnly, // 원시 기록은 뜨지 않음: Step 은 게임이 끝나길 기다릴 뿐
                        InitialWindowEnded = summaryOnly,
                    }; // 번호는 유효 회차 수가 아니라 있는 run 파일 기준 (덮어쓰기 금지)
                    runNumber = _run.Number;
                    _run.Session?.NoteTriggerStart(); // Start 는 등록 실행 파일의 시작을 감지한 직후에만 불림 (게임 시작·재실행 대기 뒤 재시도)
                    learn?.Start();
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    learn?.Dispose();
                    _log.Error($"{g.Name}: auto trace could not start", e);
                    if (firstRun)
                    {
                        _status.Set(g.Name, g.Source, null, AutoStage.Failed, "trace session failed to start");
                    }

                    return false;
                }
            }
        }

        if (refused is not null)
        {
            if (firstRun && _status.Set(g.Name, g.Source, null, AutoStage.Skipped, refused))
            {
                _log.Info($"{g.Name}: auto trace and relocation skipped ({refused})"); // 같은 이유면 매번 남기지 않음
            }

            return false;
        }

        if (stageAttempt > 0 || ioSummaryAttempt > 0 || loadListAttempt > 0)
        {
            if (stageAttempt > 0)
            {
                try
                {
                    TraceStageIndex.WriteTried(RawTracePath(g), stageAttempt); // 저장되지 않는 run 도 시도로 셈 (서비스를 다시 시작해도 유지)
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _log.Warn($"{g.Name}: stage index attempt not recorded ({e.GetType().Name})");
                }
            }

            if (ioSummaryAttempt > 0)
            {
                try
                {
                    _env.WriteIoSummaryTries(RawTracePath(g), ioSummaryAttempt);
                    lock (_lock)
                    {
                        string raw = RawTracePath(g);
                        _ioSummaryAttempts[raw] = Math.Max(_ioSummaryAttempts.GetValueOrDefault(raw), ioSummaryAttempt);
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    lock (_lock)
                    {
                        string raw = RawTracePath(g);
                        _ioSummaryAttempts[raw] = Math.Max(_ioSummaryAttempts.GetValueOrDefault(raw), ioSummaryAttempt);
                        _ioSummaryPersistenceFailed.Add(raw);
                    }

                    _log.Warn($"{g.Name}: I/O summary attempt not recorded ({e.GetType().Name})");
                }
            }

            if (loadListAttempt > 0)
            {
                try
                {
                    LoadRefillStore.WriteTried(RawTracePath(g), loadListAttempt); // 저장되지 않는 run 도 시도로 셈 (서비스를 다시 시작해도 유지)
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _log.Warn($"{g.Name}: load list attempt not recorded ({e.GetType().Name})");
                }
            }

            var purposes = new List<string>();
            if (stageAttempt > 0)
            {
                purposes.Add($"stage index, attempt {stageAttempt}/{MaxStageIndexTries}");
            }

            if (ioSummaryAttempt > 0)
            {
                purposes.Add($"I/O summary, attempt {ioSummaryAttempt}/{MaxIoSummaryTries}");
            }

            if (loadListAttempt > 0)
            {
                purposes.Add($"load list, attempt {loadListAttempt}/{MaxLoadListTries}");
            }

            string purpose = string.Join(" and ", purposes);
            _log.Info($"{g.Name}: auto trace started (run {runNumber} for {purpose})");
            return true;
        }

        if (summaryOnly)
        {
            _log.Info($"{g.Name}: play summary started (disk completions only, no raw trace)");
            return true;
        }

        if (!firstRun)
        {
            _log.Info($"{g.Name}: auto trace started (run {done + 1}/{next.Limit}" + (next.Coverage is { } cov && done >= _settings().Auto.TraceRuns ? $", coverage {cov}%" : string.Empty) + ")");
            return true;
        }

        _status.Set(g.Name, g.Source, null, learn is null ? AutoStage.Tracing : AutoStage.Learning, learn is null ? null : AutoLearn.Detail(learn.Stats));
        _log.Info($"{g.Name}: auto trace started" + (learn is null ? string.Empty : " (learning: read ahead while the game runs)"));
        return true;
    }

    /// 1 초마다 부릅니다. 끝내야 하면 세션을 멈추고 트레이스를 씁니다.
    public void Step()
    {
        FinishStopped(false);
        BuildDeferredIoSummaries();
        _shadow.Flush();
        Run? run;
        lock (_lock)
        {
            run = _run;
        }

        if (run is null)
        {
            return;
        }

        if (run.EndRequested)
        {
            Finish(run, run.RawWhy ?? GameExited);
            return;
        }

        if (_settings().Games.FirstOrDefault(x => string.Equals(x.Name, run.Game.Name, StringComparison.OrdinalIgnoreCase)) is { } latest && TraceBlocker(latest) is { } cheat)
        {
            lock (_lock)
            {
                _run = null;
            }

            run.Session?.Dispose(); // 설정이 바뀌어 지금은 금지된 게임: 기록은 버리고 세션을 닫음
            run.Session = null;
            run.Learn?.Dispose();
            run.Finished.TrySetResult(true);
            _log.Info($"{run.Game.Name}: trace stopped (anti-cheat: {cheat})");
            return;
        }

        if (PolicyBlocksTracking(run.Game, out GameSettings paused))
        {
            _log.Info($"{run.Game.Name}: trace stopped (game policy: {(paused.Paused ? "paused" : GamePolicy.ModeName(paused.Mode))})");
            Finish(run, "game policy", defer: true); // 읽은 만큼은 게임이 끝난 뒤 저장 (기존 시간·읽기량 상한과 같은 안전한 중단 지점)
            return;
        }

        AsterSettings all = _settings();
        AutoSettings a = all.Auto;
        IReadOnlyList<(int Pid, long ReadBytes)> procs = _env.GameProcesses(run.Game.ExeNames);
        foreach ((int pid, long bytes) in procs)
        {
            // 첫 표본의 프로세스는 지금 값이 기준, 그 뒤에 나타난 프로세스는 세션이 열린 뒤에 시작했으므로 0 이 기준
            long before = run.Last.TryGetValue(pid, out long last) ? last : run.Primed ? 0 : bytes;
            if (run.Collecting)
            {
                run.Read += Math.Max(0, bytes - before); // 수집하지 않는 구간의 읽기는 상한에 세지 않음
            }

            run.Last[pid] = bytes;
        }

        run.Primed = true;
        if (run.Learn is { } learn && !run.LearningEnded && _env.Now() - run.LastStatusAt >= AutoLearn.StatusEvery)
        {
            run.LastStatusAt = _env.Now();
            _status.Set(run.Game.Name, run.Game.Source, null, AutoStage.Learning, AutoLearn.Detail(learn.Stats));
        }

        DateTimeOffset now = _env.Now();
        bool named = _env.AnyRunning(run.Game.ExeNames);
        bool exited = ExitGraceElapsed(run, named);
        TimeSpan elapsed = now - run.StartedAt;
        if (exited)
        {
            Finish(run, run.RawWhy ?? GameExited); // 게임 디스크에 불필요한 I/O 를 하지 않도록 정상 종료 때만 바로 저장
            return;
        }

        if (run.RawEnded)
        {
            return;
        }

        if (run.Read >= (long)ReadCapMib(run, all) << 20)
        {
            EndRawCapture(run, "read cap");
            return;
        }

        if (!run.InitialWindowEnded && elapsed >= TimeSpan.FromSeconds(a.TraceCapSeconds))
        {
            run.InitialWindowEnded = true;
            if (!EndWindow(run, endLearning: true))
            {
                return;
            }

            run.NextLateWindowAt = run.StartedAt + TimeSpan.FromSeconds(a.TraceCapSeconds); // 첫 추가 창은 첫 창 바로 뒤에 엽니다 (간격은 추가 창 사이에만)
            _log.Info($"{run.Game.Name}: initial trace window ended at time cap; late window 1/{a.TraceLateWindows} follows");
            if (a.TraceLateWindows <= 0)
            {
                EndRawCapture(run, "time cap");
            }

            return;
        }

        if (run.InitialWindowEnded)
        {
            StepLateWindows(run, a, now);
        }
    }

    // 후반 창: 세션은 첫 창부터 게임 끝까지 두고 창 안에서만 읽기를 모읍니다 (창마다 새 세션이면 창 전에 열린 파일의 이름을 못 풀어 읽기가 버려짐).
    // 등록 실행 파일이 새로 시작했으면 (NoteTargetStart) 남은 창 하나를 다음 예약 시각을 기다리지 않고 그 시각에 엽니다. 창이 이미 열려 있으면 아무것도 하지 않습니다.
    private void StepLateWindows(Run run, AutoSettings a, DateTimeOffset now)
    {
        string? startedExe = run.TakeTargetStart();
        if (run.LateWindowStartedAt is { } started)
        {
            if (now - started < TimeSpan.FromSeconds(a.TraceLateWindowSeconds))
            {
                return;
            }

            run.LateWindowStartedAt = null;
            if (!EndWindow(run))
            {
                return;
            }

            run.NextLateWindowAt = now + TimeSpan.FromMinutes(a.TraceLateIntervalMin);
        }

        if (run.LateWindowsStarted >= a.TraceLateWindows)
        {
            EndRawCapture(run, "time cap");
            return;
        }

        if (now >= run.NextLateWindowAt || startedExe is not null)
        {
            StartLateWindow(run, a, now, startedExe);
        }
    }

    /// 트레이스 세션이 후반 창 단계에 있는 같은 게임의 등록 실행 파일이 새로 시작했음을 알립니다 (감시기 시작 처리에서 부름). 창은 트레이스 루프가 다음 틱에 엽니다.
    public void NoteTargetStart(string game, string exe)
    {
        lock (_lock)
        {
            if (_run is { InitialWindowEnded: true, RawEnded: false, Finishing: false } run && string.Equals(run.Game.Name, game, StringComparison.OrdinalIgnoreCase))
            {
                run.NoteTargetStart(exe);
            }
        }
    }

    private void StartLateWindow(Run run, AutoSettings a, DateTimeOffset now, string? targetStartExe = null)
    {
        try
        {
            run.Session?.ResumeWindow();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warn($"{run.Game.Name}: trace session ended early, no more late windows ({e.GetType().Name})");
            Finish(run, "trace session ended early", defer: true); // 남은 창은 열지 않고 지금까지 모은 것을 저장
            return;
        }

        run.LateWindowsStarted++;
        run.LateWindowStartedAt = now;
        string detail = $"late window {run.LateWindowsStarted}/{a.TraceLateWindows}";
        if (run.Number == 1)
        {
            _status.Set(run.Game.Name, run.Game.Source, null, AutoStage.Tracing, detail); // 추가 기록(2회차 이후)은 상태를 건드리지 않음: 단계를 덮으면 At 이 바뀌어 재학습 판정이 흔들림
        }

        _log.Info($"{run.Game.Name}: {detail} started" + (targetStartExe is null ? string.Empty : $" at a target start ({targetStartExe})"));
    }

    // 수집 구간을 닫고 그 구간의 읽기 집계를 남깁니다. 닫지 못하면 (세션이 이미 죽음 등) 지금까지 모은 기록을 저장하도록 끝내고 false.
    private bool EndWindow(Run run, bool endLearning = false)
    {
        try
        {
            if (run.Session?.PauseWindow() is { } w)
            {
                LogWindow(run, w);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Error($"{run.Game.Name}: trace window could not end; stopping the trace", e);
            Finish(run, "trace window failed", defer: true); // 학습도 Finish 가 끝냄
            return false;
        }

        if (endLearning)
        {
            EndLearn(run);
        }

        return true;
    }

    private void LogWindow(Run run, WindowStats w) =>
        _log.Info($"{run.Game.Name}: trace window ended, {w.Reads} reads, {w.Targets} target processes, {w.EventsLost} events lost so far");

    // 열린 채 끝나는 후반 창의 집계를 로그만 남기고 (Finish 가 곧 세션을 멈추므로 실패해도 무시)
    private void LogOpenWindow(Run run)
    {
        try
        {
            if (run.Session?.PauseWindow() is { } w)
            {
                LogWindow(run, w);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warn($"{run.Game.Name}: last trace window summary unavailable ({e.GetType().Name})");
        }
    }

    private void EndRawCapture(Run run, string why)
    {
        if (run.RawEnded)
        {
            return;
        }

        if (run.Collecting && !EndWindow(run, endLearning: true))
        {
            return;
        }

        EndLearn(run);
        run.InitialWindowEnded = true;
        run.LateWindowStartedAt = null;
        run.RawEnded = true;
        run.RawWhy = why;
        if (run.IoSummaryEnabled)
        {
            try
            {
                run.Session?.FreezeRawRecords();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{run.Game.Name}: raw trace could not be frozen ({e.GetType().Name}); stopping observation");
                Finish(run, $"{why}, raw freeze failed", defer: true); // 이웃 실패 경로와 같이 게임이 도는 동안 저장·변환하지 않음
                return;
            }

            _log.Info($"{run.Game.Name}: trace recording stopped ({why}); I/O observation continues until the game exits");
        }
        else
        {
            Finish(run, why, defer: true);
        }
    }

    private const string GameExited = "game exited";

    // 멈춘 기록을 다시 저장할 때의 잠금 대기: 0 이면 바쁠 때 다음 Step 으로 미뤄 Step 스레드를 막지 않음
    private static readonly TimeSpan RetryLockWait = TimeSpan.Zero;

    // 멈춰 둔 기록 중 게임 (같은 물리 디스크의 등록 게임 포함) 이 끝난 것을 저장합니다. force 는 서비스 종료: 기다리지 않고 바로 저장해 잃지 않게.
    private void FinishStopped(bool force)
    {
        Run[] held;
        lock (_lock)
        {
            held = [.. _stopped];
        }

        foreach (Run r in held.Where(r => force || ExitGraceElapsed(r) && !StillRunning(r.Game)))
        {
            bool mine;
            lock (_lock)
            {
                mine = !r.Saving && _stopped.Contains(r);
                if (mine)
                {
                    r.Saving = true; // 선점한 호출만 표시 (이미 저장 중이면 다시 쓰지 않음). 저장이 끝날 때까지 _stopped 에 남겨 같은 게임의 Start 가 같은 회차 번호를 받지 못하게 함
                }
            }

            if (!mine)
            {
                continue;
            }

            bool again = false;
            try
            {
                again = Finish(r, r.Why, lockWait: force ? null : RetryLockWait); // 멈춘 기록을 다시 저장할 때는 잠금을 기다리지 않음
                if (again && force)
                {
                    _log.Warn($"{r.Game.Name}: auto trace not saved at shutdown (zone lock busy)");
                }
            }
            finally
            {
                bool saved = false;
                lock (_lock)
                {
                    r.Saving = false;
                    if (!again)
                    {
                        saved = _stopped.Remove(r);
                    }
                }

                if (saved)
                {
                    r.SaveFinished.TrySetResult(true); // 보류 목록에서 뺀 뒤에만 저장 완료를 알림
                }
            }
        }
    }

    private bool StillRunning(GameSettings g) =>
        _env.AnyRunning(g.ExeNames) || _env.GameRunningOnDisk?.Invoke(PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant()) == true;

    private bool ExitGraceElapsed(Run run)
    {
        bool named = _env.AnyRunning(run.Game.ExeNames);
        return ExitGraceElapsed(run, named);
    }

    private static bool ExitGraceElapsed(Run run, bool named)
    {
        if (named)
        {
            run.Exited = false;
        }
        else if (run.Exited)
        {
            return true;
        }

        IReadOnlySet<string> names = named ? new HashSet<string>(run.Game.ExeNames, StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        run.Exited |= run.Exits.Poll([run.Game], names).Count > 0;
        return run.Exited;
    }

    // 읽기 캡 (MiB): 명시한 trace_cap_mib 는 그대로, 기본값이면 max(6144, 유효 존 상한 x 2) 를 플레이당 한 번만 계산함 (매초 디스크 종류를 묻지 않게).
    private int ReadCapMib(Run run, AsterSettings all) =>
        all.Auto.TraceCapMib != AutoSettings.DefaultTraceCapMib ? all.Auto.TraceCapMib : run.AutoCapMib ??= all.Auto.EffectiveTraceCapMib(AutoRelocator.EffectiveZoneMib(all, run.Game, _env));

    /// 그 게임이 시작되면 돌고 있는 그림자 계획과 끝난 뒤 변환 (게임 폴더 메타데이터 조회) 을 취소합니다 (감시기 스레드에서 부름). 변환은 어느 게임이 시작되든 취소: 같은 디스크를 다툼.
    public void CancelShadow(string game)
    {
        _shadow.CancelGame(game);
        lock (_lock)
        {
            if (_ioScanSource is { } scan)
            {
                _ioScanGameStarted = true;
                scan.Cancel();
            }
        }
    }

    public void SettingsReloaded(IReadOnlyCollection<GameSettings> removed)
    {
        lock (_lock)
        {
            foreach (GameSettings game in removed)
            {
                _cheatLogged.Remove(game.Name);
                _policyLogged.Remove(game.Name);
            }
        }

        OrbitModel.ForgetSnapshots(removed);
    }

    /// 서비스 종료: 돌고 있는 세션만 정리합니다 (기록은 버림).
    public void Shutdown()
    {
        Run? run;
        OrbitModel orbitModel;
        bool finishing;
        lock (_lock)
        {
            _shutdown = true;
            _shutdownSource.Cancel();
            orbitModel = _orbitModel;
            (run, _run) = (_run, null);
            finishing = run?.Finishing == true;
        }

        _shadow.Shutdown();
        orbitModel.Dispose();
        if (!finishing && run is { RawEnded: true, IoSummaryEnabled: true, SummaryOnly: false, Session: not null }) // 이미 Finish 가 세션을 멈추는 중이면 그쪽이 저장함. 디스크 완료 요약만 남기는 플레이는 저장할 원시 기록이 없고 요약은 종료 중에는 만들지 않음
        {
            // 원시 기록이 이미 확정된 채 관찰만 이어 가던 중이었음: 이 변경 전에는 상한에서 세션을 멈추고 보류해 두던 기록이라 같은 저장 경로로 보냄 (집계는 변환을 건너뛰고 버림)
            try
            {
                HoldForSave(run, StopSession(run), run.RawWhy ?? "service shutdown");
                _log.Info($"{run.Game.Name}: auto trace stopped (service shutdown after {run.RawWhy}), saving the raw trace");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{run.Game.Name}: raw trace not kept at shutdown ({e.GetType().Name})");
            }
        }

        run?.Session?.Dispose();
        if (run is not null)
        {
            run.Session = null;
        }
        run?.Learn?.Dispose();
        run?.Finished.TrySetResult(true);
        FinishStopped(true);
    }

    // 잠금이 바빠 기록을 멈춘 채 남겼으면 true (다음 Step 에서 다시 저장). lockWait 이 null 이면 기본 잠금 대기
    private bool Finish(Run run, string why, bool defer = false, TimeSpan? lockWait = null)
    {
        GameSettings g = run.Game;
        bool stoppedBefore = run.Held is not null; // 이미 멈춰 둔 기록의 저장: 세션은 멈출 때 정리했음
        try
        {
            if (!stoppedBefore)
            {
                lock (_lock)
                {
                    run.Finishing = true; // 저장 경로 진입을 알려 시작 재시도가 완료 신호를 기다리게 함
                }
            }

            if (!stoppedBefore && run.Collecting)
            {
                LogOpenWindow(run); // 후반 창이 열린 채 끝나면 그 창의 집계도 남김
            }

            IReadOnlyList<TraceRecord> records = run.Held ?? StopSession(run);
            if (!stoppedBefore)
            {
                EndLearn(run);
            }

            if (defer)
            {
                run.Held = records;
                run.Why = why;
                lock (_lock)
                {
                    _stopped.Add(run);
                }

                _log.Info($"{g.Name}: auto trace stopped ({why}), saving after the game exits");
                return false;
            }

            ResolveIoSummary(run);
            if (run.SummaryOnly)
            {
                return FinishPlaySummary(run, records, why, lockWait);
            }

            (List<TraceEvent> events, _) = TraceBuilder.Build(records, g.ExeNames, g.OriginRoot);
            List<TraceEvent> canonical = _env.Canonicalize(events);
            IReadOnlyList<StageMark> stageMarks = TraceStageMarks.FromRecords(records, g.ExeNames);
            if (canonical.Count == 0)
            {
                if (!SaveIoSummaryOnly(run, lockWait, out bool summaryChanged))
                {
                    HoldForSave(run, records, why);
                    return true;
                }

                if (!summaryChanged)
                {
                    LogIoSummary(run);
                }

                _log.Warn($"{g.Name}: auto trace ended ({why}) with no reads captured");
                if (run.Number == 1)
                {
                    _status.Set(g.Name, g.Source, null, AutoStage.Failed, "no reads captured");
                }

                return false;
            }

            if (run.Number > 1 && TooSmall(g, canonical) is { } small)
            {
                if (!SaveIoSummaryOnly(run, lockWait, out bool summaryChanged))
                {
                    HoldForSave(run, records, why);
                    return true;
                }

                if (!summaryChanged)
                {
                    LogIoSummary(run);
                }

                _log.Info($"{g.Name}: auto trace ended ({why}), run {run.Number} too small ({small}), not saved and not counted");
                return false;
            }

            if (run.Number == 1 && !ClearLegacyRun1Mark(g)) // 업데이트로 옛 트레이스를 치운 뒤의 새 첫 기록: 남은 옛 표지가 새 run1 을 유효로 만들면 안 되므로 표지를 먼저 지우고, 못 지우면 저장하지 않음
            {
                _status.Set(g.Name, g.Source, null, AutoStage.Failed, "trace failed: legacy mark");
                return false;
            }

            Dictionary<string, string> marks = VolatileOfRun(run, records, canonical); // 이전 플레이 파일을 덮기 전에 (그 수정 시각이 기준)
            // 기록·병합은 게임과 디스크를 다투지 않게 낮은 우선순위 스레드에서 돌림 (끝날 때까지 기다림)
            bool locked = false;
            bool changed = false;
            string? replanGeneration = null;
            RunBelowNormal(() =>
            {
                // 업데이트 정리 (DropChanged) 가 같은 파일을 읽고-고쳐-쓰므로 같은 폴더 잠금 안에서 씀 (이 스레드가 새로 잡음, 잠금 순서: 이 안에서 AutoStatus 잠금을 잡으며 (ReplanGeneration) 방향은 폴더 잠금 → AutoStatus 한쪽뿐이라 안전). 못 잡으면 버리지 않고 멈춘 채 남김
                using AsterCache.Core.Apply.DataLock? gate = AsterCache.Core.Apply.DataLock.TryAcquire(g.Plan, lockWait);
                if (gate is null)
                {
                    return;
                }

                locked = true;
                // 잠금을 기다리는 사이 정리가 원시 트레이스를 고쳤거나 치웠으면 이 기록은 쓰지 않음: 기록의 오프셋이 옛 파일 기준일 수 있고, 병합하면 정리가 뺀 행이 되살아남. 파일 시각으로 비교 (원본을 옮기면 시각이 그대로라 존재 여부도 봄)
                if (_env.FileWriteTime(RawTracePath(g)) != run.RawWrittenAtStart)
                {
                    changed = true;
                    return;
                }

                if (run.Number == 1)
                {
                    string rawPath = RawTracePath(g);
                    TraceStageIndex.ClearSidecars(rawPath);
                    TraceWriter.Write([canonical], rawPath);
                    SaveStageMarks(g, run.Number, stageMarks);
                    SaveStageIndex(g, [(canonical, stageMarks)]);
                }
                else
                {
                    Merge(g, canonical, run.Number, stageMarks);
                }

                WriteIoSummaryLocked(run);

                replanGeneration = _status.ReplanGeneration(g.Name); // 잠금 안에서 병합한 이벤트와 같은 재계획 세대를 넘김
            });

            if (!locked)
            {
                // 잠금이 바쁨 (정리·병합 중): 처음 한 번만 알리고 멈춘 채 남김. 다음 Step 에서 게임이 끝났으면 다시 저장
                run.Held = records;
                run.Why = why;
                if (!stoppedBefore)
                {
                    lock (_lock)
                    {
                        _stopped.Add(run);
                    }
                }

                if (!run.LockWaitLogged)
                {
                    run.LockWaitLogged = true;
                    _log.Info($"{g.Name}: auto trace ({why}) waits for the zone lock, saved once it is free");
                }

                return true;
            }

            if (changed)
            {
                _log.Info($"{g.Name}: auto trace ({why}) not saved, the trace changed while it waited for the zone lock (game update cleanup)");
            }
            else
            {
                LogIoSummary(run);
                SaveVolatile(g, marks);
                run.RawWrittenAtStart = _env.FileWriteTime(RawTracePath(g)); // 이 저장이 바꾼 원시 트레이스 시각: 미룬 집계가 그 사이 정리가 바꾸지 않았는지 보는 기준
                if (run.Number > 1 && TraceBlocker(g) is null)
                {
                    lock (_lock)
                    {
                        if (!_shutdown)
                        {
                            _orbitModel.Schedule(g, _log, MinRunBytes); // 새 회차 병합 뒤 에피소드를 낮은 우선순위로 갱신
                        }
                    }
                }

                if (replanGeneration is { } generation)
                {
                    _shadow.Schedule(g.Name, canonical, TraceBlocker(g), generation); // 병합 뒤 메모리의 이번 회차 읽기로 그림자 계획 (트레이스 루프 스레드, 계산은 낮은 우선순위 별도 스레드)
                }
                _log.Info($"{g.Name}: auto trace ended ({why}), {canonical.Count} read events saved" + (run.Number == 1 ? string.Empty : $" (run {run.Number}, merged)"));
            }

            if (run.Number == 1)
            {
                _status.Set(g.Name, g.Source, null, AutoStage.WaitingIdle, null);
            }

            return false;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Error($"{g.Name}: auto trace failed", e);
            if (run.Session is not null)
            {
                lock (_lock)
                {
                    _stopCleanupFailed = true; // 세션이 멈췄는지 확인되지 않음: 새 세션을 막음
                }

                _log.Error($"{g.Name}: trace cleanup could not be confirmed; new trace sessions are blocked");
            }

            if (run.Number == 1)
            {
                _status.Set(g.Name, g.Source, null, AutoStage.Failed, "trace failed: " + e.GetType().Name); // 추가 기록이 실패해도 이미 있는 트레이스와 상태는 그대로
            }
        }
        finally
        {
            if (!stoppedBefore)
            {
                run.Session?.Dispose();
                run.Session = null;
            }

            run.Learn?.Dispose();
            lock (_lock)
            {
                _run = _run == run ? null : _run;
            }

            run.Finished.TrySetResult(true);
        }

        return false;
    }

    // 게임이 끝난 뒤 디스크 읽기 요청을 게임 폴더 파일 배치 (속성 조회) 로 (파일, 블록) 에 이읍니다. 낮은 우선순위 스레드에서 돌리고 서비스 종료에서 멈춥니다.
    // 순서 보장: 재배치·재계획·확장·SSD 보조 저장은 모두 게임 종료 뒤 rewarm_after_exit_min 이 지나야 시작하므로 (GameExitTracker.HeavyWorkAllowed) 변환은 보통 그 전에 끝납니다.
    // 그래도 재배치가 끼어들면 역변환이 틀리므로 변환 앞뒤의 계획·저널 상태가 다르면 집계를 버립니다.
    private void ResolveIoSummary(Run run)
    {
        if (run.IoSummaryResolved || run.IoRequests is not { } requests)
        {
            return;
        }

        run.IoSummaryResolved = true;
        GameSettings g = run.Game;
        CancellationTokenSource scan;
        lock (_lock)
        {
            if (_shutdownSource.IsCancellationRequested)
            {
                _log.Info($"{g.Name}: I/O summary not built (service shutdown)");
                return;
            }

            // 같은 디스크의 게임이 이미 도는 중이면 그 로딩과 메타데이터 조회를 다투지 않도록 지금은 만들지 않고, 그 게임이 끝난 뒤 만듦 (기록 저장은 계속, BuildDeferredIoSummaries)
            if (_env.GameRunningOnDisk?.Invoke(PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant()) == true)
            {
                if (DeferIoSummary(run))
                {
                    _log.Info($"{g.Name}: I/O summary deferred (a game is running on the disk), built after it exits");
                }

                return;
            }

            scan = _ioScanSource = CancellationTokenSource.CreateLinkedTokenSource(_shutdownSource.Token);
            _ioScanGameStarted = false;
        }

        scan.CancelAfter(IoScanBudget);
        if (requests.SelfReadDroppedOps > 0)
        {
            _log.Info($"{g.Name}: {requests.SelfReadDroppedOps} service read ranges were not noted (limit {SelfReadLog.MaxOps}); their disk reads stay in the I/O summary");
        }

        IoSummary? summary = null;
        try
        {
            string before = LayoutStamp(g);
            RunBelowNormal(() => summary = _env.BuildIoSummary(requests with { SelfReads = OwnReadFilter(requests, run.Records, scan.Token) }, g.AllowedRoots, scan.Token));
            if (summary is not null && before != LayoutStamp(g))
            {
                _log.Warn($"{g.Name}: I/O summary dropped, the plan or journal changed while file layouts were read");
                return;
            }
        }
        catch (OperationCanceledException)
        {
            string reason;
            lock (_lock)
            {
                reason = _shutdownSource.IsCancellationRequested ? "service shutdown" : _ioScanGameStarted ? "a game started" : "time budget";
                if (reason == "a game started")
                {
                    _ = DeferIoSummary(run); // 조회가 게임 시작으로 멈췄으므로 그 게임이 끝난 뒤 다시 만듦
                }
            }

            _log.Info($"{g.Name}: I/O summary not built ({reason})" + (reason == "a game started" ? ", deferred until it exits" : string.Empty));
            return;
        }
        catch (Exception e)
        {
            _log.Warn($"{g.Name}: I/O summary not built ({e.GetType().Name})"); // 집계는 부가 정보: 전후 상태 조회가 실패해도 원시 기록 저장은 계속
            return;
        }
        finally
        {
            lock (_lock)
            {
                _ioScanSource = null;
            }

            scan.Dispose();
        }

        run.IoSummary = run.SummaryOnly && summary is not null ? WithPlayInfo(run, summary) : summary;
    }

    // 원시 트레이스 없이 디스크 완료 요약만 남기는 플레이의 저장: 요약을 단독 집계로 저장하고 (로드 구간·캐시 없이 읽는 파일·순위 목록은 WriteIoSummaryLocked 가 다시 만듦) 끝냅니다. 잠금이 바쁘면 true (멈춘 채 남겨 다음 Step 에서 다시).
    // 요약이 아직 없으면 (같은 디스크의 다른 게임이 떠 있어 미룸) 저장할 것이 없고, 그 게임이 끝난 뒤 BuildDeferredIoSummaries 가 만들어 저장합니다.
    private bool FinishPlaySummary(Run run, IReadOnlyList<TraceRecord> records, string why, TimeSpan? lockWait)
    {
        if (!SaveIoSummaryOnly(run, lockWait, out bool traceChanged))
        {
            HoldForSave(run, records, why);
            return true;
        }

        if (!traceChanged)
        {
            LogIoSummary(run);
        }

        _log.Info($"{run.Game.Name}: play summary ended ({why})" + (run.IoSummary is null ? ", not built yet" : string.Empty));
        return false;
    }

    // 플레이마다 남기는 요약의 부가 정보: 로딩 판정 구간 (관찰 시작을 0 으로 한 마이크로초), 계측이 빈 구간 (플레이가 관찰보다 먼저 시작했으면 그 앞 구간을 음수로), 파일의 크기·수정 시각 (세대 대조).
    private IoSummary WithPlayInfo(Run run, IoSummary summary)
    {
        DateTimeOffset from = summary.ObservedAtUtc;
        long Us(DateTimeOffset at) => (long)(at - from).TotalMicroseconds;
        var phases = new List<IoSummaryWindow>();
        foreach ((DateTimeOffset start, DateTimeOffset end) in LoadPhasesOf?.Invoke(run.Game.Name) ?? [])
        {
            if (end > from && start < summary.EndedAtUtc)
            {
                phases.Add(new IoSummaryWindow(Math.Max(0, Us(start)), Math.Max(0, Us(end))));
            }
        }

        var gaps = new List<IoSummaryWindow>();
        foreach (SessionSpan play in _env.PlaySessions(run.Game.Name))
        {
            DateTimeOffset start = new(DateTime.SpecifyKind(play.StartUtc, DateTimeKind.Utc));
            if (start < from && play.EndUtc >= from.UtcDateTime)
            {
                gaps.Add(new IoSummaryWindow(Us(start), 0));
            }
        }

        var files = new List<IoSummaryFile>(summary.Files.Count);
        foreach (IoSummaryFile file in summary.Files)
        {
            try
            {
                Fingerprint now = _env.Stat(file.Path);
                files.Add(file with { Size = now.Size, MTimeTicks = Fingerprint.UnixEpochTicks + now.MtimeNs / 100 });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                files.Add(file); // 크기를 못 잼 (없어졌거나 읽을 수 없음): 세대 대조 없이 둠
            }
        }

        return summary with { Files = files, Play = new IoSummaryPlay(DiskOnly: true, phases, gaps) };
    }

    private IAutoTrace OpenSummarySession(GameSettings g) =>
        _env.StartSummaryTrace is { } open ? open(g.ExeNames, g.AllowedRoots) : _env.StartTrace(g.ExeNames, null, g.AllowedRoots);

    // 서비스가 스스로 읽은 구간 (시작 스트림·예열) 을 디스크 읽기 집계에서 빼기 위한 표: 게임이 같은 시간대에 같은 구간을 읽었으면 남깁니다.
    private static SelfReadFilter OwnReadFilter(IoRequestSnapshot requests, IEnumerable<TraceRecord> records, CancellationToken ct) =>
        new(requests.ObservedAtUtc, requests.SelfReadBatches ?? [],
            records.Where(r => r.Kind == RecordKind.Read).Select(r => new GameRead(r.Path, new DateTimeOffset(r.Time), r.Offset, r.Length)), ct);

    private string LayoutStamp(GameSettings g) =>
        $"{_env.FileWriteTime(g.Plan):O}|{_env.JournalRows(g)}|{_env.JournalPlanSha(g)}|{_status.Get(g.Name)?.RelocatedAt:O}|{_status.ReplanGeneration(g.Name)}";

    private bool SaveIoSummaryOnly(Run run, TimeSpan? lockWait, out bool traceChanged)
    {
        traceChanged = false;
        if (run.IoSummary is null)
        {
            return true;
        }

        bool locked = false;
        bool changedDuringSave = false;
        RunBelowNormal(() =>
        {
            using AsterCache.Core.Apply.DataLock? gate = AsterCache.Core.Apply.DataLock.TryAcquire(run.Game.Plan, lockWait);
            if (gate is null)
            {
                return;
            }

            if (_env.FileWriteTime(RawTracePath(run.Game)) != run.RawWrittenAtStart)
            {
                changedDuringSave = true;
                return;
            }

            locked = true;
            WriteIoSummaryLocked(run, standalone: true);
        });

        if (changedDuringSave)
        {
            traceChanged = true;
            _log.Info($"{run.Game.Name}: I/O summary not saved, the trace changed while it waited for the zone lock (game update cleanup)");
            return true;
        }

        return locked;
    }

    private void WriteIoSummaryLocked(Run run, bool standalone = false)
    {
        if (run.IoSummary is not { } summary)
        {
            return;
        }

        try
        {
            string raw = RawTracePath(run.Game);
            if (standalone)
            {
                string target = IoSummaryStore.WriteStandalone(raw, summary);
                if (run.SummaryOnly)
                {
                    foreach (string removed in IoSummaryStore.RememberPlaySummary(raw, target))
                    {
                        _log.Info($"{run.Game.Name}: older play summary removed ({Path.GetFileName(removed)}), the latest {IoSummaryStore.MaxPlaySummaries} are kept");
                    }
                }
            }
            else
            {
                string runPath = TraceWriter.RunPath(raw, run.Number);
                IoSummaryStore.WriteForRun(runPath, summary);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{run.Game.Name}: I/O summary not saved ({e.GetType().Name})");
            return;
        }

        RebuildLoadRefill(run.Game);
        RebuildUnbufferedFiles(run.Game);
        RebuildRanked(run.Game);
    }

    /// 미룬 I/O 집계의 상한: 게임당 가장 최근 플레이 하나. 다른 게임이 오래 떠 있는 동안 플레이마다 원시 읽기 기록이 메모리에 쌓이지 않게 하고, 더 새 플레이가 있으면 오래된 것을 버립니다 (그 크기 기록은 이미 남음).
    /// 이 run 을 미룬 목록에 새로 넣었으면 true. 호출하는 쪽이 _lock 을 쥐고 부릅니다.
    private bool DeferIoSummary(Run run)
    {
        run.IoSummaryResolved = false; // 나중에 다시 만들 수 있게
        Run? other = _ioDeferred.FirstOrDefault(r => r != run && string.Equals(r.Game.Name, run.Game.Name, StringComparison.OrdinalIgnoreCase));
        if (other is not null)
        {
            Run older = other.StartedAt <= run.StartedAt ? other : run;
            _ = _ioDeferred.Remove(older);
            _log.Info($"{run.Game.Name}: an older deferred I/O summary was dropped, a newer play of the game ended");
            if (older == run)
            {
                return false;
            }
        }

        if (_ioDeferred.Contains(run))
        {
            return false;
        }

        _ioDeferred.Add(run);
        return true;
    }

    /// 플레이가 끝날 때 한 번 게임이 쓰는 파일의 크기를 남깁니다: 트레이스를 뜨지 않은 플레이 (학습이 끝난 게임은 몇 플레이에 한 번만 뜸) 도 이력이 쌓여야 붙들기가 동작합니다.
    /// 대상 파일은 이전 트레이스가 남긴 가변 표시 (volatile.json) 에서 얻고, 하는 일은 파일 속성 조회와 작은 이력 파일 읽기·쓰기뿐이라 같은 디스크의 다른 게임이 떠 있어도 합니다.
    /// 게임 종료 처리를 붙들지 않게 낮은 우선순위 스레드에서 폴더 잠금 안에 돌고, 크기 기록은 이 한 곳에서만 남깁니다 (트레이스 저장 쪽은 남기지 않음): 저장은 플레이 기록이 끝 시각을 쓰기 전에 돌 수 있어 그 플레이의 식별자를 모르므로, 두 곳이 남기면 한 플레이가 두 번 세집니다.
    /// 트레이스를 뜬 플레이의 새 가변 표시는 다음 플레이 끝부터 반영됩니다. 플레이 기록에 방금 (PlayEndWindow 안에) 끝난 플레이가 없으면 (보이지 않은 짧은 실행 등) 남기지 않습니다: 직전 플레이의 키를 덮어쓰지 않게.
    // 방금 끝난 플레이로 보는 기준: 플레이 기록의 끝 시각이 이 안에 있어야 함 (종료 감시 틱은 30 초 간격).
    private static readonly TimeSpan PlayEndWindow = TimeSpan.FromMinutes(10);

    internal void RecordWrittenSizesAtPlayEnd(GameSettings g)
    {
        DateTime recent = _env.Now().UtcDateTime - PlayEndWindow;
        if (TraceBlocker(g) is not null || !_env.FileExists(VolatilePath(g)) || _env.PlaySessions(g.Name).Where(s => s.EndUtc >= recent).Select(s => s.Session).LastOrDefault() is not { Length: > 0 } session)
        {
            return; // 트레이스를 막는 게임·가변 표시를 만든 적이 없는 게임·방금 끝난 플레이 기록이 없음
        }

        bool traced;
        lock (_lock)
        {
            traced = _run is { } run && string.Equals(run.Game.Name, g.Name, StringComparison.OrdinalIgnoreCase);
        }

        var thread = new Thread(() =>
        {
            try
            {
                using AsterCache.Core.Apply.DataLock? gate = AsterCache.Core.Apply.DataLock.TryAcquire(g.Plan, TimeSpan.FromSeconds(10));
                if (gate is null)
                {
                    _log.Info($"{g.Name}: written file sizes not recorded at the end of the play (zone lock busy)");
                    return;
                }

                int files = RecordWrittenSizes(g, session);
                _log.Info($"{g.Name}: written file sizes recorded at the end of the play ({files} files, {(traced ? "trace of this play pending" : "no trace in this play")})");
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
            {
                _log.Warn($"{g.Name}: written file sizes not recorded ({e.GetType().Name})");
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "written-sizes",
        };
        thread.Start();
    }

    // 미룬 I/O 집계를 만듭니다: 같은 디스크의 게임이 더는 떠 있지 않을 때만 (조회가 그 게임과 디스크를 다투지 않게). 만든 집계는 단독 집계로 저장해 목록 (로드 재채움·캐시 없이 읽는 파일·순위) 을 갱신합니다.
    private void BuildDeferredIoSummaries()
    {
        Run[] pending;
        lock (_lock)
        {
            if (_ioDeferred.Count == 0 || _shutdownSource.IsCancellationRequested)
            {
                return;
            }

            pending = [.. _ioDeferred];
        }

        foreach (Run run in pending)
        {
            GameSettings g = run.Game;
            if (_env.GameRunningOnDisk?.Invoke(PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant()) == true)
            {
                continue;
            }

            lock (_lock)
            {
                _ = _ioDeferred.Remove(run);
            }

            if (run.IoSummaryResolved)
            {
                continue; // 그 사이 다른 경로 (잠금 대기로 보류했다가 다시 저장) 가 이미 집계를 만들어 저장했음: 같은 플레이를 두 번 세지 않음
            }

            ResolveIoSummary(run);
            if (run.IoSummary is null)
            {
                continue; // 다시 막혔거나 (다시 미룸) 만들지 못함 (로그는 ResolveIoSummary 가 남김)
            }

            run.IoSummary = run.IoSummary with { SessionId = run.PlaySession }; // 미룬 집계의 식별자 (같은 플레이의 다른 집계와 구분)
            if (!SaveIoSummaryOnly(run, RetryLockWait, out bool traceChanged))
            {
                lock (_lock)
                {
                    _ = DeferIoSummary(run); // 폴더 잠금이 바쁨: 다음 Step 에서 다시
                }

                continue;
            }

            if (!traceChanged)
            {
                LogIoSummary(run);
                _log.Info($"{g.Name}: deferred I/O summary built and saved (the game on the disk exited)");
            }
        }
    }

    private readonly object _journalGate = new(); // 변경 저널 읽기는 한 번에 하나

    // 변경 저널 (NTFS USN) 로 플레이마다 게임이 쓰는 파일이 잘라내어지거나 교체됐는지 판정해 크기 기록에 더합니다: 세션 끝의 크기만 보면 줄였다 다시 늘린 것을 놓칩니다.
    // 읽기는 게임 영역 잠금 밖의 낮은 우선순위 스레드에서 돌고 (최대 30 초), 결과를 이력에 반영할 때만 잠급니다. 등록된 게임 중 어느 것이든 실행 중이면 읽지 않고 다음 플레이 끝으로 미룹니다.
    // 구간은 플레이 기록 (play_samples.json) 에 남은 끝난 플레이 전부 (과거 플레이 포함, 트레이스를 켜지 않은 플레이도) 이고, 이미 판정한 플레이는 건너뜁니다. 저널을 못 읽으면 (권한·저널 없음·상한 초과·일부만 읽음) 그 플레이는 저널로 판정되지 않습니다.
    private void QueueJournalFill(GameSettings g, IReadOnlyList<string> paths)
    {
        var thread = new Thread(() =>
        {
            try
            {
                FillFromJournal(g, paths);
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
            {
                _log.Warn($"{g.Name}: change journal judgment failed ({e.GetType().Name})"); // 부가 기능: 실패해도 아무것도 건드리지 않음
            }
        })
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "journal-fill",
        };
        thread.Start();
    }

    internal void FillFromJournal(GameSettings g, IReadOnlyList<string> paths)
    {
        if (!Monitor.TryEnter(_journalGate))
        {
            return;
        }

        try
        {
            bool GameRunning() => _settings().Games.Any(x => _env.AnyRunning(x.ExeNames));
            if (GameRunning())
            {
                _log.Info($"{g.Name}: change journal not read, a game is running (next time)");
                return;
            }

            using var cancel = new CancellationTokenSource();
            using var watch = new Timer(_ =>
            {
                if (GameRunning())
                {
                    cancel.Cancel(); // 읽는 도중 게임이 시작됨: 읽기를 멈춤
                }
            }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

            string file = WrittenFileHistory.PathFor(RawTracePath(g));
            var tracked = WrittenFileHistory.Read(file).Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var reads = new List<(string Volume, (string Path, ulong? Frn)[] Files, UsnRead? Read)>();
            foreach (IGrouping<string, string> volume in paths.Where(tracked.Contains).Distinct(StringComparer.OrdinalIgnoreCase).GroupBy(p => (Path.GetPathRoot(p) ?? string.Empty).TrimEnd('\\'), StringComparer.OrdinalIgnoreCase))
            {
                (string Path, ulong? Frn)[] files = [.. volume.Select(p => (p, _env.FileReference(p)))];
                HashSet<ulong> frns = [.. files.Where(f => f.Frn is not null).Select(f => f.Frn!.Value)];
                if (frns.Count > 0)
                {
                    reads.Add((volume.Key, files, _env.ReadUsn(volume.Key, frns, cancel.Token)));
                }
            }

            if (reads.Count == 0)
            {
                return;
            }

            if (cancel.IsCancellationRequested || GameRunning())
            {
                _log.Info($"{g.Name}: change journal result dropped, a game started while it was read (next time)"); // 플레이 중에는 이력도 순위 목록도 바꾸지 않음 ("순위 목록을 만든 뒤 바뀐 파일" 기준 시각이 밀리지 않게)
                return;
            }

            using AsterCache.Core.Apply.DataLock? gate = AsterCache.Core.Apply.DataLock.TryAcquire(g.Plan, TimeSpan.FromSeconds(10));
            if (gate is null)
            {
                _log.Info($"{g.Name}: change journal result not saved (zone lock busy)");
                return;
            }

            if (GameRunning())
            {
                return; // 잠금을 기다리는 사이 게임이 시작됨
            }

            IReadOnlyList<SessionSpan> sessions = _env.PlaySessions(g.Name);
            WrittenFileHistory history = WrittenFileHistory.Read(file); // 잠금을 잡은 뒤 다시 읽음: 읽는 동안 플레이 끝의 기록이 바뀌었을 수 있음
            int added = 0;
            foreach ((string volume, (string Path, ulong? Frn)[] files, UsnRead? read) in reads)
            {
                int n = history.RecordJournal(read, files, sessions);
                added += n;
                _log.Info($"{g.Name}: change journal of {volume}: " + (read is null
                    ? "not readable, those plays are not judged by it"
                    : $"{files.Length} written files, {read.Events.Count} records since {read.OldestUtc:u}, {n} play judgments added, {history.Files.Count(f => f.Shrank)} files shrank in all"));
            }

            if (added > 0)
            {
                history.Write(file);
                RebuildRanked(g, " (after the change journal)"); // 덧붙이기로 보는 파일이 달라졌을 수 있음
            }
        }
        finally
        {
            Monitor.Exit(_journalGate);
        }
    }

    // 플레이가 끝났을 때 게임이 쓰는 파일 (트레이스가 가변으로 표시한 것) 의 크기를 남깁니다: 붙들기가 크기가 줄어든 적이 없는 파일만 고르는 근거 (WrittenFileHistory). 부가 정보라 실패해도 기록 저장은 계속.
    private int RecordWrittenSizes(GameSettings g, string session)
    {
        try
        {
            IReadOnlyDictionary<string, string> marked = ReadVolatile(VolatilePath(g));
            var seen = new List<(string Path, long Size, long MTimeTicks)>();
            foreach (string path in marked.Keys.Where(p => VolatileFiles.IsGameWritten(p, marked)))
            {
                try
                {
                    Fingerprint now = _env.Stat(path);
                    seen.Add((path, now.Size, Fingerprint.UnixEpochTicks + (now.MtimeNs / 100)));
                }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    seen.Add((path, 0, 0)); // 없어짐: 기록한 적이 있는 파일이면 줄어든 것으로 셈
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // 읽을 수 없는 파일은 이번 플레이에 기록하지 않음
                }
            }

            string file = WrittenFileHistory.PathFor(RawTracePath(g));
            WrittenFileHistory history = WrittenFileHistory.Read(file);
            history.Record(session, seen, ViewHold.MinFileBytes);
            if (_env.GameRunningOnDisk?.Invoke(PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant()) != true)
            {
                history.GameState = PrewarmEnv.GameStateMark(g); // 이 플레이가 끝났을 때의 게임 상태: 다음 플레이가 업데이트 직후인지 가리는 기준. 계획의 모든 파일 속성을 조회하므로 같은 디스크의 다른 게임이 도는 동안은 하지 않음 (기존 표지 유지: 게임이 바뀌었으면 다음 판정이 업데이트로 보아 붙들지 않는 쪽)
            }
            history.Write(file);
            QueueJournalFill(g, [.. seen.Select(x => x.Path)]);
            return seen.Count;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{g.Name}: written file size history not saved ({e.GetType().Name})");
            return 0;
        }
    }

    /// 서비스 시작 때: 로드 구간 목록·캐시 없이만 읽히는 파일 목록·블록 순위 목록 (각각) 이 없거나 그 게임의 가장 새 집계보다 오래된 게임의 목록을 저장된 집계로 만듭니다 (실행 중인 게임은 건너뜀, 폴더 잠금 안, 낮은 우선순위).
    /// 부팅 뒤 대기·디스크 유휴는 부르는 쪽이 기다린 뒤 백그라운드로 부릅니다.
    public void RebuildStaleLoadRefills(CancellationToken ct)
    {
        foreach (GameSettings g in _settings().Games)
        {
            if (ct.IsCancellationRequested)
            {
                return;
            }

            PruneTraceArchives(g);
            bool refillStale = LoadRefillPlans.IsStale(g, _env.FileWriteTime);
            bool unbufferedStale = UnbufferedFiles.IsStale(g, _env.FileWriteTime);
            bool rankedStale = LoadRefillPlans.RankedIsStale(g, _env.FileWriteTime);
            if (_env.AnyRunning(g.ExeNames) || !(refillStale || unbufferedStale || rankedStale))
            {
                continue;
            }

            RunBelowNormal(() =>
            {
                using AsterCache.Core.Apply.DataLock? gate = AsterCache.Core.Apply.DataLock.TryAcquire(g.Plan);
                if (gate is null)
                {
                    _log.Info($"{g.Name}: load refill list not built at service start (zone lock busy)");
                }
                else if (!_env.AnyRunning(g.ExeNames)) // 잠금을 기다리는 사이 게임이 떴으면 만들지 않음
                {
                    if (refillStale)
                    {
                        RebuildLoadRefill(g, " (service start)");
                    }

                    if (unbufferedStale)
                    {
                        RebuildUnbufferedFiles(g, " (service start)");
                    }

                    if (rankedStale)
                    {
                        RebuildRanked(g, " (service start)");
                    }
                }
            });
        }
    }

    // 트레이스를 허용하는 게임의 낡은 업데이트 보관본 (.stale, .stale.update) 을 지웁니다: 읽는 곳이 없음 (TraceArchives). 게임이 떠 있거나 원시 트레이스가 없거나 트레이스가 금지된 게임은 건드리지 않음.
    private void PruneTraceArchives(GameSettings g)
    {
        if (_env.AnyRunning(g.ExeNames) || !_env.FileExists(RawTracePath(g)) || _env.TraceBlocker(g) is not null)
        {
            return;
        }

        try
        {
            RunBelowNormal(() =>
            {
                using AsterCache.Core.Apply.DataLock? gate = AsterCache.Core.Apply.DataLock.TryAcquire(g.Plan, TimeSpan.Zero); // 업데이트 정리가 같은 파일들을 쓰므로 계획 잠금 안에서만 (바쁘면 다음 시작에)
                if (gate is null)
                {
                    return;
                }

                (int files, long bytes) = TraceArchives.Prune(RawTracePath(g), _env.Now().UtcDateTime.AddDays(-TraceArchives.KeepDays));
                if (files > 0)
                {
                    _log.Info($"{g.Name}: {files} update archive copies of the trace older than {TraceArchives.KeepDays} days removed ({bytes >> 20} MiB), nothing reads them for a game that can be traced");
                }
            });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{g.Name}: update archive copies not removed ({e.GetType().Name})");
        }
    }

    // 집계가 바뀔 때마다 다음 로드에 다시 채울 블록 목록을 새로 만듦 (게임이 끝난 뒤 낮은 우선순위 스레드, 폴더 잠금 안). 부가 정보라 실패해도 기록 저장은 계속.
    private void RebuildLoadRefill(GameSettings g, string logSuffix = "")
    {
        try
        {
            _log.Info($"{g.Name}: {LoadRefillPlans.Rebuild(g, _env.Now())}{logSuffix}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{g.Name}: load refill list not saved ({e.GetType().Name})");
        }
    }

    // 집계가 바뀔 때마다 캐시 없이만 읽히는 파일 목록도 새로 만듦 (부가 정보라 실패해도 기록 저장은 계속).
    private void RebuildUnbufferedFiles(GameSettings g, string logSuffix = "")
    {
        try
        {
            _log.Info($"{g.Name}: {UnbufferedFiles.Rebuild(g, _env.Now())}{logSuffix}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{g.Name}: unbuffered file list not saved ({e.GetType().Name})");
        }
    }

    // 집계가 바뀔 때마다 플레이 전체 기록의 블록 순위 목록도 새로 만듦 (부가 정보라 실패해도 기록 저장은 계속).
    private void RebuildRanked(GameSettings g, string logSuffix = "")
    {
        try
        {
            _log.Info($"{g.Name}: {LoadRefillPlans.RebuildRanked(g, _env.Now(), _settings().Memory is { KeepHold: true, KeepHoldWrittenFiles: true }, [.. _env.PlaySessions(g.Name).Select(x => x.Session)], _settings().Memory is { KeepSparse: true, StandbyLadder: true })}{logSuffix}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{g.Name}: ranked block list not saved ({e.GetType().Name})");
        }
    }

    private void LogIoSummary(Run run)
    {
        if (run.IoSummary is not { } summary || run.IoSummaryLogged)
        {
            return;
        }

        _log.Info($"{run.Game.Name}: {summary.FormatLogLine()}");
        if (summary.HasReadModes)
        {
            _log.Info($"{run.Game.Name}: {summary.FormatReadModesLogLine()}");
        }

        run.IoSummaryLogged = true;
    }

    private void HoldForSave(Run run, IReadOnlyList<TraceRecord> records, string why)
    {
        run.Held = records;
        run.Why = why;
        lock (_lock)
        {
            if (!_stopped.Contains(run))
            {
                _stopped.Add(run);
            }
        }
    }

    // 첫 플레이보다 훨씬 작은 (이벤트 수가 1 % 미만) 또는 읽은 양이 MinRunBytes 미만인 플레이의 사유, 아니면 null. 첫 플레이 크기를 못 읽으면 바이트 기준만 봄.
    private string? TooSmall(GameSettings g, List<TraceEvent> canonical)
    {
        long bytes = canonical.Sum(e => e.Length);
        if (bytes < MinRunBytes)
        {
            return $"{canonical.Count} events, {bytes >> 20} MiB read";
        }

        string raw = RawTracePath(g);
        string first = _env.FileExists(TraceWriter.RunPath(raw, 1)) ? TraceWriter.RunPath(raw, 1) : raw;
        try
        {
            return JsonRead.ReadFile(first) is List<object?> rows && canonical.Count * 100L < rows.Count ? $"{canonical.Count} events against {rows.Count} in run 1" : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }
    }

    // 이 플레이에서 가변 파일로 볼 것: 쓰기로 열린 파일, 그리고 (두 번째 이후) 이전 플레이를 저장한 뒤 수정된 파일. 읽을 수 없는 파일은 건너뜀.
    private Dictionary<string, string> VolatileOfRun(Run run, IReadOnlyList<TraceRecord> records, List<TraceEvent> canonical)
    {
        GameSettings g = run.Game;
        var marks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> written = TraceBuilder.WrittenPaths(records, g.ExeNames, g.OriginRoot);
        string raw = RawTracePath(g);
        DateTimeOffset? since = run.Number > 1 ? _env.FileWriteTime(TraceWriter.RunPath(raw, run.Number - 1)) ?? _env.FileWriteTime(raw) : null;
        foreach (string path in canonical.Select(e => e.Path).Distinct(StringComparer.Ordinal))
        {
            if (written.Contains(path))
            {
                marks[path] = "opened for write";
            }
            else if (since is { } at && ModifiedAfter(path, at))
            {
                marks[path] = "changed between runs";
            }
        }

        return marks;
    }

    private bool ModifiedAfter(string path, DateTimeOffset at)
    {
        try
        {
            return new DateTimeOffset(Fingerprint.UnixEpochTicks + _env.Stat(path).MtimeNs / 100, TimeSpan.Zero) > at;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // 표시 파일에 합칩니다 (임시 파일을 거쳐 한 번에 바꿔 씀). 실패해도 이미 저장한 트레이스는 그대로: 표시는 힌트일 뿐.
    private void SaveVolatile(GameSettings g, Dictionary<string, string> marks)
    {
        if (marks.Count == 0)
        {
            return;
        }

        string path = VolatilePath(g);
        try
        {
            var all = new Dictionary<string, string>(ReadVolatile(path), StringComparer.OrdinalIgnoreCase);
            foreach ((string file, string why) in marks)
            {
                all[file] = why;
            }

            var o = new JObject();
            foreach ((string file, string why) in all.OrderBy(k => k.Key, StringComparer.Ordinal))
            {
                o.Add(file, why);
            }

            string temp = path + ".tmp";
            File.WriteAllBytes(temp, PyJson.Utf8.GetBytes(PyJson.Dumps(o, indent: 1)));
            File.Move(temp, path, true);
            _log.Info($"{g.Name}: {marks.Count} volatile file(s) marked from the trace ({all.Count} in total)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{g.Name}: volatile file marks not saved ({e.GetType().Name})");
        }
    }

    // 작업을 BelowNormal 스레드에서 돌리고 끝날 때까지 기다립니다. 예외는 호출한 쪽으로 다시 던집니다.
    private static void RunBelowNormal(Action work)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                work();
            }
            catch (Exception e)
            {
                error = e;
            }
        })
        { Priority = ThreadPriority.BelowNormal, IsBackground = true };
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        }
    }

    // 두 번째 이후 플레이: runN.json 으로 저장하고, 유효한 run 과 기존 원시 병합본의 블록 합집합 (최신 회차가 앞) 을 임시 파일로 바꿔 씁니다.
    // 읽기량이 MinRunBytes 미만인 옛 run (옛 형식에서 옮긴 run1 제외) 은 합집합에 넣지 않습니다. 이번 병합이 더한 새 고유 블록은 작은 메타로 남김.
    private void Merge(GameSettings g, List<TraceEvent> canonical, int number, IReadOnlyList<StageMark> stageMarks)
    {
        string raw = RawTracePath(g);
        var standaloneSummaries = new List<(string Path, IoSummary Summary)>();
        foreach (string standalone in IoSummaryStore.StandalonePaths(raw))
        {
            if (!File.Exists(standalone))
            {
                continue;
            }

            if (IoSummaryStore.ReadStandaloneForRaw(standalone, raw) is { } summary)
            {
                standaloneSummaries.Add((standalone, summary));
            }
            else
            {
                DeleteIoSidecar(standalone);
            }
        }

        InvalidateStageIndex(g);
        DeleteIoSidecar(IoSummaryStore.RunPath(raw));
        string first = TraceWriter.RunPath(raw, 1);
        bool legacy = number == 2 && !_env.FileExists(first); // run 파일 없이 원시 트레이스만 있던 게임: 그 트레이스가 run1 (호환: 크기와 무관하게 유효)
        if (legacy)
        {
            File.Copy(raw, first, true);
            SaveLegacyRun1Mark(g);
        }

        bool run1Valid = legacy || _env.FileExists(LegacyRun1Path(g));

        string currentRun = TraceWriter.RunPath(raw, number);
        DeleteIoSidecar(IoSummaryStore.RunPath(currentRun));
        TraceWriter.WriteFile(currentRun, canonical);
        SaveStageMarks(g, number, stageMarks);
        var previous = new List<IReadOnlyList<TraceEvent>>();
        var stageRuns = new List<(IReadOnlyList<TraceEvent> Events, IReadOnlyList<StageMark> Marks)> { (canonical, stageMarks) };
        for (int i = number - 1; i >= 1; i--)
        {
            string file = TraceWriter.RunPath(raw, i);
            if (!_env.FileExists(file))
            {
                continue; // 끊긴 번호
            }

            List<TraceEvent> events = JsonRead.ReadFile(file) is List<object?> rows ? [.. rows.Select(TraceEvents.FromJson)] : throw new PyValueException("bad trace file");
            if (run1Valid && i == 1 || events.Sum(e => e.Length) >= MinRunBytes)
            {
                previous.Add(events);
                if (TraceStageIndex.ReadMarks(file) is { } marks)
                {
                    stageRuns.Add((events, marks));
                }
            }
        }

        if (_env.FileExists(raw) && _env.FileExists(RotatedMarkPath(g))) // 지운 회차가 있을 때만 병합본을 기준으로 유지 (아니면 옛 버전의 블록·짧은 무효 회차가 병합본에서 빠질 수 있게 run 들로만 만듦)
        {
            List<TraceEvent> merged = JsonRead.ReadFile(raw) is List<object?> rows ? [.. rows.Select(TraceEvents.FromJson)] : throw new PyValueException("bad trace file");
            previous.Add(merged);
        }

        List<TraceEvent> union = TraceWriter.Union([canonical, .. previous]);
        string temp = raw + ".tmp";
        TraceWriter.WriteFile(temp, union);
        File.Move(temp, raw, true);
        RebindStandaloneSummaries(raw, standaloneSummaries);
        SaveStageIndex(g, stageRuns);
        SaveMergeMeta(g, number, (union.Count - TraceWriter.Union(previous).Count) * TraceWriter.Block >> 20);
        TracePhases.Recount(g, MinRunBytes); // 새 회차를 반영한 common 구분을 미리 남김 (예열 때 큰 run 파일을 읽지 않게)
    }

    // 단독 집계의 지문을 새 병합본에 맞춰 다시 씁니다. 부가 파일이라 다시 쓰지 못해도 (다른 프로그램이 열어 둔 경우 등) 경고만 남기고 병합의 뒤 단계를 계속합니다.
    // 다시 쓰지 못한 집계는 옛 지문으로 남아 유효한 집계로 읽히지 않습니다.
    private void RebindStandaloneSummaries(string raw, List<(string Path, IoSummary Summary)> summaries)
    {
        if (summaries.Count == 0)
        {
            return;
        }

        string rawFingerprint;
        try
        {
            rawFingerprint = IoSummaryStore.RawFingerprint(raw);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"I/O summary files not rewritten ({e.GetType().Name}): the merged trace could not be read");
            return;
        }

        foreach ((string path, IoSummary summary) in summaries)
        {
            try
            {
                IoSummaryStore.Write(path, summary with { RawFingerprint = rawFingerprint });
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log.Warn($"I/O summary file not rewritten ({e.GetType().Name}): {Path.GetFileName(path)}");
            }
        }
    }

    // run 파일 한도에 닿으면 병합본에 이미 든 가장 오래된 회차를 지우고 나머지 번호를 당깁니다.
    private void CompactRunsForRelearn(GameSettings g)
    {
        string raw = RawTracePath(g);
        if (TraceFingerprint(raw) is null)
        {
            throw new PyValueException("bad merged trace file");
        }

        if (!ClearLegacyRun1Mark(g))
        {
            throw new IOException("could not clear legacy trace marker before run rotation");
        }

        int oldest = Enumerable.Range(1, MaxRunFiles).FirstOrDefault(number => _env.FileExists(TraceWriter.RunPath(raw, number)));
        if (oldest == 0)
        {
            throw new IOException("no run file to replace");
        }

        File.WriteAllText(RotatedMarkPath(g), "1"); // 지우기 전에 표지부터: 못 쓰면 예외로 지우지 않음
        string oldestRun = TraceWriter.RunPath(raw, oldest);
        File.Delete(oldestRun); // 기존 원시 병합본이 이 회차의 블록을 보존
        DeleteIfExists(TraceStageIndex.RunMarksPath(oldestRun));
        DeleteIoSidecar(IoSummaryStore.RunPath(oldestRun));
        DeleteIoSidecar(IoSummaryStore.TriedPath(raw)); // 지운 회차의 집계가 마지막 것이었으면 다시 추가 추적할 수 있음
        int target = 1;
        for (int number = 1; number <= MaxRunFiles; number++)
        {
            string source = TraceWriter.RunPath(raw, number);
            if (!_env.FileExists(source))
            {
                continue;
            }

            string destination = TraceWriter.RunPath(raw, target++);
            if (!string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            {
                File.Move(source, destination, true);
                string sourceMarks = TraceStageIndex.RunMarksPath(source);
                string destinationMarks = TraceStageIndex.RunMarksPath(destination);
                if (File.Exists(sourceMarks))
                {
                    File.Move(sourceMarks, destinationMarks, true);
                }
                else
                {
                    DeleteIfExists(destinationMarks);
                }

                MoveIoSidecar(IoSummaryStore.RunPath(source), IoSummaryStore.RunPath(destination));
            }
        }

        _log.Info($"{g.Name}: rotated trace run files to keep the merged base and allow relearning");
    }

    private void InvalidateStageIndex(GameSettings g)
    {
        try
        {
            DeleteIfExists(TraceStageIndex.IndexPath(RawTracePath(g)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{g.Name}: old stage index not removed ({e.GetType().Name})"); // 부가 파일 때문에 본 기록을 잃지 않음: 낡은 색인은 원시 추적이 바뀌면 읽을 때 무시됨
        }
    }

    private void SaveStageMarks(GameSettings g, int number, IReadOnlyList<StageMark> marks)
    {
        try
        {
            TraceStageIndex.WriteMarks(TraceWriter.RunPath(RawTracePath(g), number), marks);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{g.Name}: stage marks not saved ({e.GetType().Name})");
        }
    }

    private void SaveStageIndex(GameSettings g, IReadOnlyList<(IReadOnlyList<TraceEvent> Events, IReadOnlyList<StageMark> Marks)> runsNewestFirst)
    {
        try
        {
            if (TraceStageIndex.Build(runsNewestFirst) is { } index)
            {
                TraceStageIndex.Write(TraceStageIndex.IndexPath(RawTracePath(g)), index);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{g.Name}: stage index not saved ({e.GetType().Name})");
        }
    }

    // 집계 곁 파일은 부가 파일입니다: 지우거나 옮기지 못해도 경고만 남기고 원시 추적의 저장·병합·회전은 계속합니다 (낡은 곁 파일은 원시 지문이 달라 읽을 때 무효).
    private void DeleteIoSidecar(string path)
    {
        try
        {
            DeleteIfExists(path);
            DeleteIfExists(IoSummaryStore.InvalidPath(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"I/O summary file not removed ({e.GetType().Name}): {Path.GetFileName(path)}");
        }
    }

    private void MoveIoSidecar(string source, string destination)
    {
        try
        {
            if (File.Exists(source))
            {
                IoSummaryStore.MoveSummary(source, destination, (moved, to) => File.Move(moved, to, true));
            }
            else
            {
                DeleteIfExists(destination);
                DeleteIfExists(IoSummaryStore.InvalidPath(destination));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"I/O summary file not moved ({e.GetType().Name}): {Path.GetFileName(source)}");
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private bool ClearLegacyRun1Mark(GameSettings g)
    {
        try
        {
            foreach (string path in new[] { LegacyRun1Path(g), RotatedMarkPath(g) }) // 새 첫 기록은 회차 교체 표지도 함께 지움 (교체 쪽은 지운 뒤 다시 씀)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{g.Name}: legacy run1 mark not removed ({e.GetType().Name}), first trace not saved");
            return false;
        }
    }

    // 못 쓰면 이번 병합만 run1 을 유효로 보고, 이후에는 기존 동작 (64 MiB 미만이면 무효) 으로 돌아감.
    private void SaveLegacyRun1Mark(GameSettings g)
    {
        try
        {
            string path = LegacyRun1Path(g);
            string temp = path + ".tmp";
            File.WriteAllText(temp, "1");
            File.Move(temp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{g.Name}: legacy run1 mark not saved ({e.GetType().Name})");
        }
    }

    // 힌트일 뿐: 못 써도 병합은 그대로 (메타가 없으면 추가 수집은 기존 정책으로 돌아감).
    private void SaveMergeMeta(GameSettings g, int number, long newMib)
    {
        try
        {
            var o = new JObject { { "run", (long)number }, { "new_mib", newMib } };
            string path = MergeMetaPath(g);
            string temp = path + ".tmp";
            File.WriteAllBytes(temp, PyJson.Utf8.GetBytes(PyJson.Dumps(o, indent: 1)));
            File.Move(temp, path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn($"{g.Name}: merge note not saved ({e.GetType().Name})");
        }
    }

    // 세션이 멈춘 뒤 선행 읽기를 끝내고 결과를 남깁니다 (Dispose 는 작업자 스레드를 기다림).
    private static IReadOnlyList<TraceRecord> StopSession(Run run)
    {
        if (run.Session is not { } session)
        {
            return run.Records;
        }

        IReadOnlyList<TraceRecord> records;
        try
        {
            records = session.Stop();
            run.IoRequests = session.IoRequests;
        }
        catch
        {
            try
            {
                session.Dispose();
                run.Session = null;
            }
            catch
            {
                // 정리가 확인되지 않으면 참조를 남겨 다음 트레이스 창을 막습니다.
            }

            throw;
        }

        session.Dispose();
        run.Session = null;
        run.Records.AddRange(records);
        return run.Records;
    }

    private void EndLearn(Run run)
    {
        if (run.LearningEnded)
        {
            return;
        }

        run.LearningEnded = true;
        if (run.Learn is not { } learn)
        {
            return;
        }

        learn.Dispose();
        _log.Info($"{run.Game.Name}: learn ended, {AutoLearn.Summary(learn.Stats)}");
    }

    private sealed class Run(GameSettings game, IAutoTrace session, DateTimeOffset startedAt, GameExitTracker exits)
    {
        private int _endRequested;
        private string? _targetStart;

        public GameSettings Game { get; } = game;

        public IAutoTrace? Session { get; set; } = session;

        public List<TraceRecord> Records { get; } = [];

        public DateTimeOffset StartedAt { get; } = startedAt;

        public GameExitTracker Exits { get; } = exits;

        public Dictionary<int, long> Last { get; } = [];

        public bool Primed { get; set; }

        public long Read { get; set; }

        /// 기본 읽기 캡의 계산값 (MiB), 아직 안 구했으면 null.
        public int? AutoCapMib { get; set; }

        /// 첫 실행 선행 읽기 (auto.learn 이 켜졌을 때).
        public ReadAhead? Learn { get; init; }

        public bool LearningEnded { get; set; }

        public bool InitialWindowEnded { get; set; }

        public bool IoSummaryEnabled { get; init; }

        /// 원시 트레이스 없이 디스크 완료 요약만 남기는 플레이 (가벼운 세션).
        public bool SummaryOnly { get; init; }

        public bool RawEnded { get; set; }

        public string? RawWhy { get; set; }

        /// 세션을 멈춘 뒤의 디스크 읽기 요청 표, 그리고 끝난 뒤 파일 배치로 바꾼 집계 (바꾸기를 이미 해 봤는지도).
        public IoRequestSnapshot? IoRequests { get; set; }

        public IoSummary? IoSummary { get; set; }

        public bool IoSummaryResolved { get; set; }

        /// Finish 가 세션을 멈추기 시작했음 (_lock 으로 보호).
        public bool Finishing { get; set; }

        public bool IoSummaryLogged { get; set; }

        /// 이 플레이의 식별자 (미룬 I/O 집계를 만들 때 집계의 식별자로 씀).
        public string PlaySession { get; } = Guid.NewGuid().ToString("N");

        public int LateWindowsStarted { get; set; }

        public DateTimeOffset NextLateWindowAt { get; set; }

        public DateTimeOffset? LateWindowStartedAt { get; set; }

        /// 읽기를 모으는 구간 안인가: 첫 창, 또는 열려 있는 후반 창.
        public bool Collecting => !InitialWindowEnded || LateWindowStartedAt is not null;

        /// 이 플레이가 몇 번째 기록인가 (1 = 첫 기록).
        public int Number { get; init; } = 1;

        /// 시작할 때 원시 트레이스의 수정 시각 (없으면 null). 저장할 때 달라졌으면 그 사이 정리가 파일을 고친 것이라 이 회차 기록은 쓰지 않습니다.
        public DateTimeOffset? RawWrittenAtStart { get; set; }

        /// 잠금이 바빠 저장을 미룬 적이 있는지 (알림은 처음 한 번만).
        public bool LockWaitLogged { get; set; }

        public DateTimeOffset LastStatusAt { get; set; } = startedAt;

        /// 상한에 닿아 세션을 멈춘 뒤 아직 저장하지 않은 기록과 멈춘 사유 (게임이 끝난 뒤 저장).
        public IReadOnlyList<TraceRecord>? Held { get; set; }

        public string Why { get; set; } = string.Empty;

        /// 저장 중이면 true (다른 스레드가 같은 기록을 두 번 저장하지 않게).
        public bool Saving { get; set; }

        /// 보류 기록의 실제 저장이 끝나 목록에서 빠질 때 알림.
        public TaskCompletionSource<bool> SaveFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Exited { get; set; }

        public TaskCompletionSource<bool> Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool EndRequested => Volatile.Read(ref _endRequested) != 0;

        public void RequestEnd() => Interlocked.Exchange(ref _endRequested, 1);

        /// 등록 실행 파일이 새로 시작함 (트레이스 루프가 TakeTargetStart 로 가져가 후반 창을 엽니다).
        public void NoteTargetStart(string exe) => Volatile.Write(ref _targetStart, exe);

        public string? TakeTargetStart() => Interlocked.Exchange(ref _targetStart, null);
    }
}
