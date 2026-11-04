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
    private readonly object _lock = new();
    private Run? _run;
    private readonly HashSet<string> _cheatLogged = new(StringComparer.OrdinalIgnoreCase);

    /// 두 번째 이후 플레이가 이보다 적게 읽었거나 (바이트) 첫 플레이의 1 % 보다 이벤트가 적으면 (메뉴만 켠 플레이 등) 저장하지 않고 회차로 세지 않습니다.
    public const long MinExtraRunBytes = 64L << 20;

    public long MinRunBytes { get; init; } = MinExtraRunBytes;

    public AutoTracer(AutoEnv env, AutoStatus status, ILog log, Func<AsterSettings> settings, LearnEnv? learn = null)
    {
        _env = env;
        _status = status;
        _log = log;
        _settings = settings;
        _learn = learn ?? LearnEnv.Real;
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

    /// 병합 직후 남기는 작은 메타 (이번 병합이 더한 새 고유 블록): plans\<이름>.trace.merge.json. run 번호가 마지막 run 파일과 같을 때만 믿습니다.
    public static string MergeMetaPath(GameSettings g) => Path.Combine(Path.GetDirectoryName(g.Plan) ?? string.Empty, g.Name + ".trace.merge.json");

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
            if (readBytes?.Invoke(TraceWriter.RunPath(raw, n)) is not { } bytes || bytes >= minBytes)
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

    /// 유효 회차가 trace_runs 에 못 미치면 기존 정책대로 한 번 더. 채운 뒤에는 (a) 마지막 세션 커버리지가 trace_target_coverage 미만이고 (b) 직전 병합이 새 고유 블록을
    /// trace_merge_min_mib 이상 더했고 (c) 유효 회차가 trace_runs_max 미만일 때만 한 번 더. 값이 없으면 기존 정책 (거절). 안티치트 판정은 호출한 쪽 (Start) 이 먼저 합니다.
    public static TraceNext NextTrace(GameSettings g, AutoSettings a, AutoEnv env, AutoStatus status, long minBytes = MinExtraRunBytes)
    {
        int valid = RunCount(g, env.FileExists, env.RunReadBytes, minBytes);
        int last = LastRunNumber(g, env.FileExists);
        bool room = last < MaxRunFiles;
        if (valid < a.TraceRuns)
        {
            return new TraceNext(valid, a.TraceRuns, room, null);
        }

        int limit = Math.Max(a.TraceRuns, a.TraceRunsMax);
        int? coverage = status.Get(g.Name)?.CoveragePercent;
        bool more = room && valid < limit && coverage is { } c && c < a.TraceTargetCoverage
            && ReadMergeGainMib(g, last) is { } gain && gain >= a.TraceMergeMinMib;
        return new TraceNext(valid, limit, more, coverage);
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
            next = NextTrace(g, _settings().Auto, _env, _status, MinRunBytes);
            done = next.Valid;
            firstRun = !_env.FileExists(RawTracePath(g));
            if (_run is not null || !next.Wanted)
            {
                return;
            }

            refused = _env.Capabilities(g.OriginRoot).PrewarmRefusal; // 시스템 SSD·SSD·종류를 모르는 드라이브는 건드리지 않음 (시스템 HDD 는 읽기 전용 트레이스만)
            if (refused is null)
            {
                try
                {
                    LearnSettings ls = _settings().Auto.Learn;
                    learn = ls.Enabled && firstRun ? AutoLearn.Create(g, ls, _env.PhysicalRamBytes(), _learn) : null; // 시스템 SSD·SSD 는 위에서 이미 걸러짐
                    _run = new Run(g, _env.StartTrace(g.ExeNames, learn is null ? null : AutoLearn.Listener(learn)), _env.Now()) { Learn = learn, Number = LastRunNumber(g, _env.FileExists) + 1 }; // 번호는 유효 회차 수가 아니라 있는 run 파일 기준 (덮어쓰기 금지)
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

            run.Session.Dispose(); // 설정이 바뀌어 지금은 금지된 게임: 기록은 버리고 세션을 닫음
            run.Learn?.Dispose();
            _log.Info($"{run.Game.Name}: trace stopped (anti-cheat: {cheat})");
            return;
        }

        AsterSettings all = _settings();
        AutoSettings a = all.Auto;
        IReadOnlyList<(int Pid, long ReadBytes)> procs = _env.GameProcesses(run.Game.ExeNames);
        IReadOnlyList<int> rooted = _env.RootPids(run.Game); // 게임 폴더 아래의 실제 게임 프로세스 (등록 exe 의 자식인 경우도 포함)
        procs = [.. procs, .. rooted.Where(p => procs.All(x => x.Pid != p)).Select(p => (Pid: p, Bytes: _env.ReadBytes(p))).Where(x => x.Bytes is not null).Select(x => (x.Pid, x.Bytes!.Value))];
        foreach ((int pid, long bytes) in procs)
        {
            // 첫 표본의 프로세스는 지금 값이 기준, 그 뒤에 나타난 프로세스는 세션이 열린 뒤에 시작했으므로 0 이 기준
            long before = run.Last.TryGetValue(pid, out long last) ? last : run.Primed ? 0 : bytes;
            run.Read += Math.Max(0, bytes - before);
            run.Last[pid] = bytes;
        }

        run.Primed = true;
        if (run.Learn is { } learn && _env.Now() - run.LastStatusAt >= AutoLearn.StatusEvery)
        {
            run.LastStatusAt = _env.Now();
            _status.Set(run.Game.Name, run.Game.Source, null, AutoStage.Learning, AutoLearn.Detail(learn.Stats));
        }

        TimeSpan elapsed = _env.Now() - run.StartedAt;
        string? why = !_env.AnyRunning(run.Game.ExeNames) && rooted.Count == 0 ? "game exited" // 읽기량을 못 연 (보호된) 게임 프로세스도 실행 중으로 셈, 등록 exe 가 먼저 끝나도 폴더 아래 프로세스가 남았으면 실행 중
            : elapsed >= TimeSpan.FromSeconds(a.TraceCapSeconds) ? "time cap"
            : run.Read >= (long)ReadCapMib(run, all) << 20 ? "read cap"
            : null;
        if (why is not null)
        {
            Finish(run, why);
        }
    }

    // 읽기 캡 (MiB): 명시한 trace_cap_mib 는 그대로, 기본값이면 max(6144, 유효 존 상한 x 2) 를 플레이당 한 번만 계산함 (매초 디스크 종류를 묻지 않게).
    private int ReadCapMib(Run run, AsterSettings all) =>
        all.Auto.TraceCapMib != AutoSettings.DefaultTraceCapMib ? all.Auto.TraceCapMib : run.AutoCapMib ??= all.Auto.EffectiveTraceCapMib(AutoRelocator.EffectiveZoneMib(all, run.Game, _env));

    /// 서비스 종료: 돌고 있는 세션만 정리합니다 (기록은 버림).
    public void Shutdown()
    {
        Run? run;
        lock (_lock)
        {
            (run, _run) = (_run, null);
        }

        run?.Session.Dispose();
        run?.Learn?.Dispose();
    }

    private void Finish(Run run, string why)
    {
        GameSettings g = run.Game;
        try
        {
            IReadOnlyList<TraceRecord> records = run.Session.Stop();
            EndLearn(run);
            (List<TraceEvent> events, _) = TraceBuilder.Build(records, g.ExeNames, g.OriginRoot);
            List<TraceEvent> canonical = _env.Canonicalize(events);
            if (canonical.Count == 0)
            {
                _log.Warn($"{g.Name}: auto trace ended ({why}) with no reads captured");
                if (run.Number == 1)
                {
                    _status.Set(g.Name, g.Source, null, AutoStage.Failed, "no reads captured");
                }

                return;
            }

            if (run.Number > 1 && TooSmall(g, canonical) is { } small)
            {
                _log.Info($"{g.Name}: auto trace ended ({why}), run {run.Number} too small ({small}), not saved and not counted");
                return;
            }

            Dictionary<string, string> marks = VolatileOfRun(run, records, canonical); // 이전 플레이 파일을 덮기 전에 (그 수정 시각이 기준)
            // 기록·병합은 게임과 디스크를 다투지 않게 낮은 우선순위 스레드에서 돌림 (끝날 때까지 기다림)
            RunBelowNormal(() =>
            {
                if (run.Number == 1)
                {
                    TraceWriter.Write([canonical], RawTracePath(g));
                }
                else
                {
                    Merge(g, canonical, run.Number);
                }
            });

            SaveVolatile(g, marks);
            _log.Info($"{g.Name}: auto trace ended ({why}), {canonical.Count} read events saved" + (run.Number == 1 ? string.Empty : $" (run {run.Number}, merged)"));
            if (run.Number == 1)
            {
                _status.Set(g.Name, g.Source, null, AutoStage.WaitingIdle, null);
            }
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
            run.Session.Dispose();
            run.Learn?.Dispose();
            lock (_lock)
            {
                _run = null;
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

    // 두 번째 이후 플레이: runN.json 으로 저장하고, 유효한 run 들의 블록 합집합 (최신 회차의 접근 순서가 앞, 옛 회차에서만 본 블록은 뒤) 을 임시 파일을 거쳐 원시 트레이스로 한 번에 바꿔 씁니다.
    // 읽기량이 MinRunBytes 미만인 옛 run (옛 형식에서 옮긴 run1 제외) 은 합집합에 넣지 않습니다. 이번 병합이 더한 새 고유 블록은 작은 메타로 남김.
    private void Merge(GameSettings g, List<TraceEvent> canonical, int number)
    {
        string raw = RawTracePath(g);
        string first = TraceWriter.RunPath(raw, 1);
        bool legacy = number == 2 && !_env.FileExists(first); // run 파일 없이 원시 트레이스만 있던 게임: 그 트레이스가 run1 (호환: 크기와 무관하게 유효)
        if (legacy)
        {
            File.Copy(raw, first, true);
        }

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
            if (legacy && i == 1 || events.Sum(e => e.Length) >= MinRunBytes)
            {
                previous.Add(events);
            }
        }

        List<TraceEvent> union = TraceWriter.Union([canonical, .. previous]);
        string temp = raw + ".tmp";
        TraceWriter.WriteFile(temp, union);
        File.Move(temp, raw, true);
        SaveMergeMeta(g, number, (union.Count - TraceWriter.Union(previous).Count) * TraceWriter.Block >> 20);
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
    private void EndLearn(Run run)
    {
        if (run.Learn is not { } learn)
        {
            return;
        }

        learn.Dispose();
        _log.Info($"{run.Game.Name}: learn ended, {AutoLearn.Summary(learn.Stats)}");
    }

    private sealed class Run(GameSettings game, IAutoTrace session, DateTimeOffset startedAt)
    {
        public GameSettings Game { get; } = game;

        public IAutoTrace Session { get; } = session;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public Dictionary<int, long> Last { get; } = [];

        public bool Primed { get; set; }

        public long Read { get; set; }

        /// 기본 읽기 캡의 계산값 (MiB), 아직 안 구했으면 null.
        public int? AutoCapMib { get; set; }

        /// 첫 실행 선행 읽기 (auto.learn 이 켜졌을 때).
        public ReadAhead? Learn { get; init; }

        /// 이 플레이가 몇 번째 기록인가 (1 = 첫 기록).
        public int Number { get; init; } = 1;

        public DateTimeOffset LastStatusAt { get; set; } = startedAt;
    }
}
