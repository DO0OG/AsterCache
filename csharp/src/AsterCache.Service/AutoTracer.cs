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
    private bool _stopCleanupFailed;
    private OrbitModel _orbitModel = new();
    private Run? _run;
    private readonly List<Run> _stopped = []; // 상한에 닿아 세션을 멈췄지만 게임이 끝나길 기다리는 기록 (저장 전)
    private readonly HashSet<string> _cheatLogged = new(StringComparer.OrdinalIgnoreCase);

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
    public static void DropChanged(GameSettings g, IReadOnlySet<string> paths)
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

        DateTime? planned = rewrites.Count > 0 ? PlannedAtUtc(g) : null; // 이 계획을 만든 뒤에 보관한 사본만 이번 업데이트의 것
        bool ThisUpdate(string copy) => planned is { } at && File.GetLastWriteTimeUtc(copy) >= at.AddSeconds(1); // planned_at 은 초 단위로 잘리므로 같은 초에 보관된 이전 사본은 이번 것으로 치지 않음
        foreach ((string file, List<TraceEvent> kept) in rewrites)
        {
            // 있는 .stale 이 이번 업데이트의 것이면 (중간에 멈췄다 다시 불림) 그대로 두고, 이전 업데이트의 것이면 덮지 않고 이번 원본을 .stale.update 로 따로 보관 (옛 저널·원본 보관 불변 조건). 계획 시각을 모르면 이번 업데이트의 사본으로 확인할 수 없으므로 .stale.update 를 덮어 이번 원본을 보관 (맨 처음 .stale 은 유지).
            string stale = file + AutoRelocator.StaleSuffix;
            string update = file + AutoRelocator.UpdateSuffix;
            string? target = !File.Exists(stale) ? stale : ThisUpdate(stale) ? null : !ThisUpdate(update) ? update : null;
            if (target is not null)
            {
                File.Copy(file, target, true);
                File.SetLastWriteTimeUtc(target, DateTime.UtcNow); // 복사는 원본의 수정 시각을 따라가므로 보관한 때로 맞춤
            }

            string temp = file + ".tmp";
            string text = PyJson.Dumps(kept.Select(e => e.ToJson()).ToList(), ensureAscii: false) + "\n";
            AsterCache.Core.DurableFile.WriteAllBytes(temp, PyJson.Utf8.GetBytes(PyJson.ToCrlf(text)));
            File.Move(temp, file, true);
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
            || status.Plays().TryGetValue(g.Name, out IReadOnlyList<DateTimeOffset>? plays) && plays.Count(at => at > last) >= RelearnAfterPlays;
    }

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

    /// 이 게임의 다음 플레이를 트레이스할지 (게임 시작 판정과 Start 가 같은 NextTrace 를 씀).
    public bool WantsRun(GameSettings g) => NextTrace(g, _settings().Auto, _env, _status, MinRunBytes).Wanted;

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

    public void Start(GameSettings g)
    {
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

            return;
        }

        string? refused;
        ReadAhead? learn = null;
        int done; // 이미 모은 유효 플레이 수
        bool firstRun; // 원시 트레이스가 아직 없는 첫 기록 (아니면 병합용 추가 기록: 상태·선행 읽기를 건드리지 않음). 유효 회차가 0 이어도 run1 이 있으면 추가 기록
        TraceNext next;
        lock (_lock)
        {
            if (_shutdown)
            {
                _shutdown = false;
                _orbitModel = new OrbitModel();
            }

            next = NextTrace(g, _settings().Auto, _env, _status, MinRunBytes);
            done = next.Valid;
            firstRun = !_env.FileExists(RawTracePath(g));
            if (_run is not null || _stopCleanupFailed || !next.Wanted || _stopped.Any(r => string.Equals(r.Game.Name, g.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            refused = _env.Capabilities(g.OriginRoot).PrewarmRefusal; // 시스템 SSD·SSD·종류를 모르는 드라이브는 건드리지 않음 (시스템 HDD 는 읽기 전용 트레이스만)
            if (refused is null)
            {
                try
                {
                    if (LastRunNumber(g, _env.FileExists) >= MaxRunFiles)
                    {
                        CompactRunsForRelearn(g);
                        done = RunCount(g, _env.FileExists, _env.RunReadBytes, MinRunBytes);
                        AutoSettings current = _settings().Auto;
                        int configuredLimit = Math.Max(current.TraceRuns, current.TraceRunsMax);
                        next = next with { Valid = done, Limit = Math.Max(configuredLimit, done + 1) };
                    }

                    LearnSettings ls = _settings().Auto.Learn;
                    learn = ls.Enabled && firstRun ? AutoLearn.Create(g, ls, _env.PhysicalRamBytes(), _learn) : null; // 시스템 SSD·SSD 는 위에서 이미 걸러짐
                    var exits = new GameExitTracker(_env.Now);
                    exits.ObserveStarted(g);
                    _run = new Run(g, _env.StartTrace(g.ExeNames, learn is null ? null : AutoLearn.Listener(learn)), _env.Now(), exits) { Learn = learn, Number = LastRunNumber(g, _env.FileExists) + 1, RawWrittenAtStart = _env.FileWriteTime(RawTracePath(g)) }; // 번호는 유효 회차 수가 아니라 있는 run 파일 기준 (덮어쓰기 금지)
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

                    return;
                }
            }
        }

        if (refused is not null)
        {
            if (firstRun && _status.Set(g.Name, g.Source, null, AutoStage.Skipped, refused))
            {
                _log.Info($"{g.Name}: auto trace and relocation skipped ({refused})"); // 같은 이유면 매번 남기지 않음
            }

            return;
        }

        if (!firstRun)
        {
            _log.Info($"{g.Name}: auto trace started (run {done + 1}/{next.Limit}" + (next.Coverage is { } cov && done >= _settings().Auto.TraceRuns ? $", coverage {cov}%" : string.Empty) + ")");
            return;
        }

        _status.Set(g.Name, g.Source, null, learn is null ? AutoStage.Tracing : AutoStage.Learning, learn is null ? null : AutoLearn.Detail(learn.Stats));
        _log.Info($"{g.Name}: auto trace started" + (learn is null ? string.Empty : " (learning: read ahead while the game runs)"));
    }

    /// 1 초마다 부릅니다. 끝내야 하면 세션을 멈추고 트레이스를 씁니다.
    public void Step()
    {
        FinishStopped(false);
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

        if (_settings().Games.FirstOrDefault(x => string.Equals(x.Name, run.Game.Name, StringComparison.OrdinalIgnoreCase)) is { } latest && TraceBlocker(latest) is { } cheat)
        {
            lock (_lock)
            {
                _run = null;
            }

            run.Session?.Dispose(); // 설정이 바뀌어 지금은 금지된 게임: 기록은 버리고 세션을 닫음
            run.Session = null;
            run.Learn?.Dispose();
            _log.Info($"{run.Game.Name}: trace stopped (anti-cheat: {cheat})");
            return;
        }

        AsterSettings all = _settings();
        AutoSettings a = all.Auto;
        IReadOnlyList<(int Pid, long ReadBytes)> procs = _env.GameProcesses(run.Game.ExeNames);
        foreach ((int pid, long bytes) in procs)
        {
            // 첫 표본의 프로세스는 지금 값이 기준, 그 뒤에 나타난 프로세스는 세션이 열린 뒤에 시작했으므로 0 이 기준
            long before = run.Last.TryGetValue(pid, out long last) ? last : run.Primed ? 0 : bytes;
            run.Read += Math.Max(0, bytes - before);
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
            Finish(run, GameExited); // 게임 디스크에 불필요한 I/O 를 하지 않도록 정상 종료 때만 바로 저장
            return;
        }

        if (run.Read >= (long)ReadCapMib(run, all) << 20)
        {
            Finish(run, "read cap", defer: true);
            return;
        }

        if (!run.InitialWindowEnded && elapsed >= TimeSpan.FromSeconds(a.TraceCapSeconds))
        {
            if (!TryStopWindow(run, endLearning: true))
            {
                return;
            }

            run.InitialWindowEnded = true;
            run.NextLateWindowAt = run.StartedAt + TimeSpan.FromSeconds(a.TraceCapSeconds) + TimeSpan.FromMinutes(a.TraceLateIntervalMin);
            _log.Info($"{run.Game.Name}: initial trace window ended at time cap; waiting for late window 1/{a.TraceLateWindows}");
            if (a.TraceLateWindows <= 0)
            {
                Finish(run, "time cap", defer: true);
            }

            return;
        }

        if (run.InitialWindowEnded)
        {
            if (run.Session is not null && run.LateWindowStartedAt is { } windowStarted
                && now - windowStarted >= TimeSpan.FromSeconds(a.TraceLateWindowSeconds))
            {
                if (!TryStopWindow(run))
                {
                    return;
                }

                run.LateWindowStartedAt = null;
                if (run.LateWindowsStarted >= a.TraceLateWindows)
                {
                    Finish(run, "time cap", defer: true);
                    return;
                }

                run.NextLateWindowAt = now + TimeSpan.FromMinutes(a.TraceLateIntervalMin);
            }

            if (run.Session is null)
            {
                if (run.LateWindowsStarted >= a.TraceLateWindows)
                {
                    Finish(run, "time cap", defer: true);
                    return;
                }

                if (now >= run.NextLateWindowAt)
                {
                    try
                    {
                        run.Session = _env.StartTrace(run.Game.ExeNames, null);
                        run.LateWindowsStarted++;
                        run.LateWindowStartedAt = now;
                        string detail = $"late window {run.LateWindowsStarted}/{a.TraceLateWindows}";
                        _status.Set(run.Game.Name, run.Game.Source, null, AutoStage.Tracing, detail);
                        _log.Info($"{run.Game.Name}: {detail} started");
                    }
                    catch (Exception e) when (e is not OperationCanceledException)
                    {
                        _log.Error($"{run.Game.Name}: late trace window could not start", e);
                        Finish(run, "trace session failed to restart", defer: true);
                    }
                }
            }
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
                lock (_lock)
                {
                    r.Saving = false;
                    if (!again)
                    {
                        _stopped.Remove(r);
                    }
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

    /// 그 게임이 시작되면 돌고 있는 그림자 계획을 취소합니다 (감시기 스레드에서 부름).
    public void CancelShadow(string game) => _shadow.CancelGame(game);

    public void SettingsReloaded(IReadOnlyCollection<GameSettings> removed)
    {
        lock (_lock)
        {
            foreach (GameSettings game in removed)
            {
                _cheatLogged.Remove(game.Name);
            }
        }

        OrbitModel.ForgetSnapshots(removed);
    }

    /// 서비스 종료: 돌고 있는 세션만 정리합니다 (기록은 버림).
    public void Shutdown()
    {
        Run? run;
        OrbitModel orbitModel;
        lock (_lock)
        {
            _shutdown = true;
            orbitModel = _orbitModel;
            (run, _run) = (_run, null);
        }

        _shadow.Shutdown();
        orbitModel.Dispose();
        run?.Session?.Dispose();
        if (run is not null)
        {
            run.Session = null;
        }
        run?.Learn?.Dispose();
        FinishStopped(true);
    }

    // 잠금이 바빠 기록을 멈춘 채 남겼으면 true (다음 Step 에서 다시 저장). lockWait 이 null 이면 기본 잠금 대기
    private bool Finish(Run run, string why, bool defer = false, TimeSpan? lockWait = null)
    {
        GameSettings g = run.Game;
        bool stoppedBefore = run.Held is not null; // 이미 멈춰 둔 기록의 저장: 세션은 멈출 때 정리했음
        try
        {
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

            (List<TraceEvent> events, _) = TraceBuilder.Build(records, g.ExeNames, g.OriginRoot);
            List<TraceEvent> canonical = _env.Canonicalize(events);
            if (canonical.Count == 0)
            {
                _log.Warn($"{g.Name}: auto trace ended ({why}) with no reads captured");
                if (run.Number == 1)
                {
                    _status.Set(g.Name, g.Source, null, AutoStage.Failed, "no reads captured");
                }

                return false;
            }

            if (run.Number > 1 && TooSmall(g, canonical) is { } small)
            {
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
                    TraceWriter.Write([canonical], RawTracePath(g));
                }
                else
                {
                    Merge(g, canonical, run.Number);
                }

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
                SaveVolatile(g, marks);
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
        }

        return false;
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
    private void Merge(GameSettings g, List<TraceEvent> canonical, int number)
    {
        string raw = RawTracePath(g);
        string first = TraceWriter.RunPath(raw, 1);
        bool legacy = number == 2 && !_env.FileExists(first); // run 파일 없이 원시 트레이스만 있던 게임: 그 트레이스가 run1 (호환: 크기와 무관하게 유효)
        if (legacy)
        {
            File.Copy(raw, first, true);
            SaveLegacyRun1Mark(g);
        }

        bool run1Valid = legacy || _env.FileExists(LegacyRun1Path(g));

        TraceWriter.WriteFile(TraceWriter.RunPath(raw, number), canonical);
        var previous = new List<IReadOnlyList<TraceEvent>>();
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
        SaveMergeMeta(g, number, (union.Count - TraceWriter.Union(previous).Count) * TraceWriter.Block >> 20);
        TracePhases.Recount(g, MinRunBytes); // 새 회차를 반영한 common 구분을 미리 남김 (예열 때 큰 run 파일을 읽지 않게)
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
        File.Delete(TraceWriter.RunPath(raw, oldest)); // 기존 원시 병합본이 이 회차의 블록을 보존
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
            }
        }

        _log.Info($"{g.Name}: rotated trace run files to keep the merged base and allow relearning");
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

    private bool TryStopWindow(Run run, bool endLearning = false)
    {
        try
        {
            _ = StopSession(run);
            if (endLearning)
            {
                EndLearn(run);
            }

            return true;
        }
        catch (Exception e)
        {
            bool cleanupUnconfirmed = run.Session is not null;
            try
            {
                run.Learn?.Dispose();
            }
            catch (Exception cleanupError)
            {
                cleanupUnconfirmed = true;
                _log.Warn($"{run.Game.Name}: learning cleanup failed after trace window failure ({cleanupError.GetType().Name})");
            }

            run.LearningEnded = true;
            lock (_lock)
            {
                _stopCleanupFailed |= cleanupUnconfirmed;
                _run = _run == run ? null : _run;
            }

            _log.Error($"{run.Game.Name}: auto trace window could not stop; run failed", e);
            if (cleanupUnconfirmed)
            {
                _log.Error($"{run.Game.Name}: trace cleanup could not be confirmed; new trace sessions are blocked");
            }

            if (run.Number == 1)
            {
                _status.Set(run.Game.Name, run.Game.Source, null, AutoStage.Failed, "trace window failed to stop");
            }

            return false;
        }
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

        public int LateWindowsStarted { get; set; }

        public DateTimeOffset NextLateWindowAt { get; set; }

        public DateTimeOffset? LateWindowStartedAt { get; set; }

        /// 이 플레이가 몇 번째 기록인가 (1 = 첫 기록).
        public int Number { get; init; } = 1;

        /// 시작할 때 원시 트레이스의 수정 시각 (없으면 null). 저장할 때 달라졌으면 그 사이 정리가 파일을 고친 것이라 이 회차 기록은 쓰지 않습니다.
        public DateTimeOffset? RawWrittenAtStart { get; init; }

        /// 잠금이 바빠 저장을 미룬 적이 있는지 (알림은 처음 한 번만).
        public bool LockWaitLogged { get; set; }

        public DateTimeOffset LastStatusAt { get; set; } = startedAt;

        /// 상한에 닿아 세션을 멈춘 뒤 아직 저장하지 않은 기록과 멈춘 사유 (게임이 끝난 뒤 저장).
        public IReadOnlyList<TraceRecord>? Held { get; set; }

        public string Why { get; set; } = string.Empty;

        /// 저장 중이면 true (다른 스레드가 같은 기록을 두 번 저장하지 않게).
        public bool Saving { get; set; }

        public bool Exited { get; set; }
    }
}
