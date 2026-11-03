using System.Globalization;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;

namespace AsterCache.Service;

/// 자동 모드의 게임별 단계 이름 (auto_status.json 의 stage).
public static class AutoStage
{
    public const string Discovered = "discovered";
    public const string Tracing = "tracing";
    public const string WaitingIdle = "waiting_idle";
    public const string Relocating = "relocating";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Learning = "learning";
}

public sealed record AutoGameEntry(
    string Source, string? Launcher, string Stage, string? Detail, DateTimeOffset At, DateTimeOffset? LastPlayed, DateTimeOffset? RelocatedAt, int? HitPercent = null, DateTimeOffset? HitAt = null, AutoBootProbe? BootProbe = null, IReadOnlyList<DateTimeOffset>? Plays = null, int? LoadBoundaryMs = null, DateTimeOffset? LoadBoundaryAt = null);

/// 부팅 뒤 첫 서비스 시작에서 잰 메타데이터 콜드 비용 (auto_status.json 의 boot_probe): 측정 시각, 연 파일 수, 파일당 평균 열기 시간 (ms).
public sealed record AutoBootProbe(DateTimeOffset At, int Files, double OpenMsPerFile);

/// 쓰기 뒤 회복 추적 한 건 (auto_status.json 의 drive_recovery): 드라이브, 시작·상한 시각, 쓰기 직전 기준 (없으면 고정 대기), 마지막 판정 ("pass"/"unknown"/"bad", 아직 없으면 null).
public sealed record DriveRecoveryRecord(string Drive, DateTimeOffset Started, DateTimeOffset Until, ProbeSpot? Zone, ProbeSpot? Ref, string? Verdict);

public sealed record AutoEvent(DateTimeOffset At, string Game, string Kind);

/// 자동 모드 상태 (<데이터 폴더>\auto_status.json). 서비스만 쓰고 트레이가 읽습니다. 파일이 없거나 깨졌으면 빈 상태로 시작합니다.
/// path 가 null 이면 (데이터 폴더를 믿을 수 없을 때) 메모리에만 둡니다. 쓰기는 임시 파일 → 이동으로 원자적입니다.
public sealed class AutoStatus
{
    public const int MaxEvents = 20;
    public static readonly TimeSpan RelocationKeep = TimeSpan.FromHours(48);
    public const int MaxPlays = 30;
    public static readonly TimeSpan RelocationHold = TimeSpan.FromHours(24);
    public const int MoveRetries = 5;
    public static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly object _lock = new();
    private readonly string? _path;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<long> _ticks;
    private readonly Action<TimeSpan> _sleep;
    private readonly Dictionary<string, AutoGameEntry> _games = new(StringComparer.Ordinal);
    private readonly List<(DateTimeOffset At, long Bytes, long? Ticks)> _relocations = []; // Ticks: 이번 실행에서 시작한 기록의 Stopwatch 시작값 (파일에서 읽은 기록은 null)
    private DateTimeOffset? _holdUntil; // 상태 파일을 못 읽었을 때 재배치를 막는 벽시계 시각 (relocation_hold_until)
    private readonly List<AutoEvent> _events = [];
    private readonly Dictionary<string, DriveRecoveryRecord> _driveRecovery = new(StringComparer.Ordinal);

    /// sleep 은 파일 교체를 다시 시도하기 전의 대기 (시험이 바꿈, 기본은 MoveRetryDelay 만큼 실제로 잠). ticks 는 세션 안 경과 판정용 Stopwatch 틱 (시험이 바꿈).
    public AutoStatus(string? path, ILog log, Func<DateTimeOffset>? now = null, Action<TimeSpan>? sleep = null, Func<long>? ticks = null)
    {
        _path = path;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _ticks = ticks ?? System.Diagnostics.Stopwatch.GetTimestamp;
        _sleep = sleep ?? Thread.Sleep;
        Load();
    }

    public AutoGameEntry? Get(string name)
    {
        lock (_lock)
        {
            return _games.GetValueOrDefault(name);
        }
    }

    public IReadOnlyList<AutoEvent> Events
    {
        get
        {
            lock (_lock)
            {
                return [.. _events];
            }
        }
    }

    /// 단계를 바꿉니다 (launcher 가 null 이면 기존 값 유지). 바뀐 것이 있으면 true. 단계가 같으면 at 은 그대로입니다.
    public bool Set(string name, string source, string? launcher, string stage, string? detail)
    {
        lock (_lock)
        {
            DateTimeOffset now = _now();
            AutoGameEntry next;
            if (_games.TryGetValue(name, out AutoGameEntry? old))
            {
                next = old with { Source = source, Launcher = launcher ?? old.Launcher, Stage = stage, Detail = detail, At = stage == old.Stage ? old.At : now };
                if (next == old)
                {
                    return false;
                }
            }
            else
            {
                next = new AutoGameEntry(source, launcher, stage, detail, now, null, null);
            }

            _games[name] = next;
            Save();
            return true;
        }
    }

    /// 게임 exe 가 떴음: last_played 를 기록합니다. 항목이 없으면 initialStage 로 만듭니다.
    public void MarkPlayed(string name, string source, string initialStage, DateTimeOffset at)
    {
        lock (_lock)
        {
            _games[name] = _games.TryGetValue(name, out AutoGameEntry? old)
                ? old with { LastPlayed = at, Plays = [.. (old.Plays ?? (old.LastPlayed is { } l ? [l] : [])).Append(at).TakeLast(MaxPlays)] }
                : new AutoGameEntry(source, null, initialStage, null, at, at, null, Plays: [at]);
            Save();
        }
    }

    /// 자동 적용을 시작함 (이어하기 포함): 일일 상한 목록 (relocations) 에 시작 시각과 이동 예정 바이트 (relocation_bytes) 를 남깁니다. 끝났을 때 다시 남기지 않습니다.
    public void BeginRelocation(DateTimeOffset at, long bytes = 0)
    {
        lock (_lock)
        {
            _relocations.Add((at, bytes, _ticks() - (long)(Math.Max(0, (_now() - at).Ticks) * (decimal)System.Diagnostics.Stopwatch.Frequency / TimeSpan.TicksPerSecond))); // 시작값은 at 이 지금보다 앞이면 그만큼 거슬러 잡음
            Save();
        }
    }

    /// 자동 재배치 끝: done, relocated_at 기록. counted 면 events 에도 남깁니다 (이미 제자리인 게임은 세지 않음). 일일 상한은 시작 때 센 BeginRelocation 이 맡습니다.
    public void Complete(string name, string source, DateTimeOffset at, bool counted, string? detail)
    {
        lock (_lock)
        {
            AutoGameEntry old = _games.GetValueOrDefault(name) ?? new AutoGameEntry(source, null, AutoStage.Done, null, at, null, null);
            _games[name] = old with { Source = source, Stage = AutoStage.Done, Detail = detail, At = old.Stage == AutoStage.Done ? old.At : at, RelocatedAt = at };
            if (counted)
            {
                AddEventLocked(at, name, "relocated");
            }

            Save();
        }
    }

    /// 게임 시작 때 잰 핫 집합의 RAM 상주율 (%)과 측정 시각. 항목이 없으면 남기지 않습니다.
    public void RecordHit(string name, int percent, DateTimeOffset at)
    {
        lock (_lock)
        {
            if (_games.TryGetValue(name, out AutoGameEntry? old))
            {
                _games[name] = old with { HitPercent = Math.Clamp(percent, 0, 100), HitAt = at };
                Save();
            }
        }
    }

    /// 게임 시작 때 잰 로딩 경계 도달 시간 (ms)과 측정 시각. 항목이 없으면 남기지 않습니다.
    public void RecordLoadBoundary(string name, int ms, DateTimeOffset at)
    {
        lock (_lock)
        {
            if (_games.TryGetValue(name, out AutoGameEntry? old))
            {
                _games[name] = old with { LoadBoundaryMs = Math.Max(0, ms), LoadBoundaryAt = at };
                Save();
            }
        }
    }

    /// 부팅 뒤 콜드 비용 진단 결과를 남깁니다 (덮어씀). 항목이 없으면 남기지 않습니다.
    public void RecordBootProbe(string name, AutoBootProbe probe)
    {
        lock (_lock)
        {
            if (_games.TryGetValue(name, out AutoGameEntry? old))
            {
                _games[name] = old with { BootProbe = probe };
                Save();
            }
        }
    }

    /// 드라이브별 회복 추적 저장본 (서비스 재시작 뒤 복원용).
    public IReadOnlyList<DriveRecoveryRecord> DriveRecoveries()
    {
        lock (_lock)
        {
            return [.. _driveRecovery.Values];
        }
    }

    public void SetDriveRecovery(DriveRecoveryRecord record)
    {
        lock (_lock)
        {
            if (!_driveRecovery.TryGetValue(record.Drive, out DriveRecoveryRecord? old) || old != record)
            {
                _driveRecovery[record.Drive] = record;
                Save();
            }
        }
    }

    public void ClearDriveRecovery(string drive)
    {
        lock (_lock)
        {
            if (_driveRecovery.Remove(drive))
            {
                Save();
            }
        }
    }

    public void AddEvent(string game, string kind)
    {
        lock (_lock)
        {
            AddEventLocked(_now(), game, kind);
            Save();
        }
    }

    /// 업데이트로 계획이 낡음: relocated_at 을 지우고 discovered 로 되돌려 다음 실행의 트레이스 → 계획 → 재배치를 다시 타게 합니다. 이미 센 일일 상한 기록은 그대로입니다.
    public void ResetForReplan(string name, string detail)
    {
        lock (_lock)
        {
            if (_games.TryGetValue(name, out AutoGameEntry? old))
            {
                _games[name] = old with { Stage = AutoStage.Discovered, Detail = detail, At = _now(), RelocatedAt = null };
                Save();
            }
        }
    }

    // 창 안인가: 기록의 벽시계 시각과 이번 실행의 Stopwatch 경과 중 더 늦게 끝나는 쪽을 따릅니다 (실행 중 시계가 앞으로 튀어도 창이 일찍 끝나지 않음).
    private bool InWindow((DateTimeOffset At, long Bytes, long? Ticks) r, TimeSpan window, DateTimeOffset since) =>
        r.At > since || (r.Ticks is { } t && System.Diagnostics.Stopwatch.GetElapsedTime(t, _ticks()) < window);

    /// 최근 window 안에 시작한 자동 적용 수.
    public int RelocationsWithin(TimeSpan window)
    {
        lock (_lock)
        {
            DateTimeOffset since = _now() - window;
            return _relocations.Count(r => InWindow(r, window, since));
        }
    }

    /// 상태 파일을 못 읽어 재배치를 막고 있으면 그 해제 시각 (벽시계), 아니면 null.
    public DateTimeOffset? RelocationHeldUntil()
    {
        lock (_lock)
        {
            return _holdUntil is { } h && h > _now() ? h : null;
        }
    }

    /// 상태 화면·로그에 보이는 막힘 문구.
    public static string HoldDetail(DateTimeOffset until) => $"status file was unreadable; relocation held until {until.ToUniversalTime():u}";

    /// 최근 window 안에 시작한 자동 적용의 이동 예정 바이트 합.
    public long RelocationBytesWithin(TimeSpan window)
    {
        lock (_lock)
        {
            DateTimeOffset since = _now() - window;
            return _relocations.Where(r => InWindow(r, window, since)).Sum(r => r.Bytes);
        }
    }

    public IReadOnlyDictionary<string, DateTimeOffset> LastPlayed()
    {
        lock (_lock)
        {
            return _games.Where(kv => kv.Value.LastPlayed is not null).ToDictionary(kv => kv.Key, kv => kv.Value.LastPlayed!.Value, StringComparer.Ordinal);
        }
    }

    /// 게임별 플레이 이력 (오래된 순, 최대 MaxPlays 개). 이력이 없고 last_played 만 있는 옛 항목은 그 하나가 이력입니다.
    public IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> Plays()
    {
        lock (_lock)
        {
            return _games.Where(kv => kv.Value.LastPlayed is not null)
                .ToDictionary(kv => kv.Key, kv => kv.Value.Plays is { Count: > 0 } p ? p : [kv.Value.LastPlayed!.Value], StringComparer.Ordinal);
        }
    }

    public void Remove(string name)
    {
        lock (_lock)
        {
            if (_games.Remove(name))
            {
                Save();
            }
        }
    }

    private void AddEventLocked(DateTimeOffset at, string game, string kind)
    {
        _events.Add(new AutoEvent(at, game, kind));
        if (_events.Count > MaxEvents)
        {
            _events.RemoveRange(0, _events.Count - MaxEvents);
        }
    }

    // 호출하는 쪽이 잠금을 잡고 있어야 합니다.
    private void Save()
    {
        DateTimeOffset now = _now();
        _relocations.RemoveAll(r => now - r.At > RelocationKeep);
        if (_path is null)
        {
            return;
        }

        var games = new JObject();
        foreach ((string name, AutoGameEntry e) in _games.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var game = new JObject
            {
                { "source", e.Source },
                { "launcher", e.Launcher },
                { "stage", e.Stage },
                { "detail", e.Detail },
                { "at", Iso(e.At) },
                { "last_played", e.LastPlayed is { } p ? Iso(p) : null },
                { "relocated_at", e.RelocatedAt is { } r ? Iso(r) : null },
            };
            if (e.Plays is { Count: > 0 } plays)
            {
                game.Add("plays", plays.Select(t => (object?)Iso(t)).ToList());
            }

            if (e.HitPercent is { } hp)
            {
                game.Add("hit_percent", (long)hp); // 재지 않은 게임의 항목 모양은 그대로
                game.Add("hit_at", e.HitAt is { } ha ? Iso(ha) : null);
            }

            if (e.BootProbe is { } bp)
            {
                game.Add("boot_probe", new JObject { { "at", Iso(bp.At) }, { "files", (long)bp.Files }, { "open_ms_per_file", bp.OpenMsPerFile } });
            }

            if (e.LoadBoundaryMs is { } lb)
            {
                game.Add("tier0_probe_ready_ms", (long)lb); // 재지 않은 게임의 항목 모양은 그대로
                game.Add("tier0_probe_ready_at", e.LoadBoundaryAt is { } la ? Iso(la) : null);
            }

            games.Add(name, game);
        }

        var root = new JObject
        {
            { "updated", Iso(now) },
            { "relocations", _relocations.Select(r => (object?)Iso(r.At)).ToList() },
            { "relocation_bytes", _relocations.Select(r => (object?)r.Bytes).ToList() },
            { "games", games },
            { "events", _events.Select(e => (object?)new JObject { { "at", Iso(e.At) }, { "game", e.Game }, { "kind", e.Kind } }).ToList() },
        };
        if (_holdUntil is { } hold && hold > now)
        {
            root.Add("relocation_hold_until", Iso(hold)); // 막힘이 없으면 키를 만들지 않음 (기존 파일 모양 유지)
        }

        if (_driveRecovery.Count > 0)
        {
            var recovery = new JObject();
            foreach ((string drive, DriveRecoveryRecord r) in _driveRecovery.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var o = new JObject { { "started", Iso(r.Started) }, { "until", Iso(r.Until) }, { "verdict", r.Verdict } };
                if (r.Zone is { } z)
                {
                    o.Add("zone", Spot(z));
                }

                if (r.Ref is { } f)
                {
                    o.Add("ref", Spot(f));
                }

                recovery.Add(drive, o);
            }

            root.Add("drive_recovery", recovery); // 추적이 없으면 키를 만들지 않음 (기존 파일 모양 유지)
        }

        try
        {
            string tmp = _path + ".tmp";
            DurableFile.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(PyJson.Dumps(root, indent: 2, ensureAscii: false) + "\n"));
            MoveOver(tmp, _path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn("auto status file not written: " + e.GetType().Name);
        }
    }

    // 트레이나 백신이 파일을 잠깐 열고 있으면 덮어쓰기가 공유 위반 (IOException) 이나 접근 거부 (UnauthorizedAccessException) 로 실패하므로 짧게 다시 시도합니다.
    private void MoveOver(string tmp, string path)
    {
        for (int retry = 0; ; retry++)
        {
            try
            {
                File.Move(tmp, path, true);
                return;
            }
            catch (Exception e) when ((e is IOException or UnauthorizedAccessException) && retry < MoveRetries)
            {
                _sleep(MoveRetryDelay);
            }
        }
    }

    private void Load()
    {
        if (_path is null || !File.Exists(_path))
        {
            return;
        }

        try
        {
            if (JsonRead.ReadFile(_path) is not JObject root)
            {
                throw new PyValueException("object expected");
            }

            if (root.TryGetValue("relocations", out object? rel) && rel is List<object?> times)
            {
                List<object?> sizes = root.TryGetValue("relocation_bytes", out object? rb) && rb is List<object?> bl ? bl : []; // 옛 파일은 없음: 빠진 항목은 0
                _relocations.AddRange(times.Select((v, i) => (At: Time(v), Bytes: i < sizes.Count && sizes[i] is long n && n > 0 ? n : 0L)).Where(r => r.At is not null).Select(r => (r.At!.Value, r.Bytes, (long?)null)));
                _holdUntil = Time(root, "relocation_hold_until");
            }

            if (root.TryGetValue("games", out object? g) && g is JObject games)
            {
                foreach ((string name, object? value) in games)
                {
                    if (value is JObject o && Str(o, "stage") is { } stage)
                    {
                        _games[name] = new AutoGameEntry(Str(o, "source") ?? "manual", Str(o, "launcher"), stage, Str(o, "detail"), Time(o, "at") ?? _now(), Time(o, "last_played"), Time(o, "relocated_at"), o.TryGetValue("hit_percent", out object? hp) && hp is long h ? (int)Math.Clamp(h, 0, 100) : null, Time(o, "hit_at"), ReadBootProbe(o), ReadPlays(o), TryGetEither(o, "tier0_probe_ready_ms", "load_boundary_ms", out object? lb) && lb is long l ? (int)Math.Clamp(l, 0, int.MaxValue) : null, Time(o, "tier0_probe_ready_at") ?? Time(o, "load_boundary_at"));
                    }
                }
            }

            if (root.TryGetValue("drive_recovery", out object? dr) && dr is JObject recoveries)
            {
                foreach ((string drive, object? value) in recoveries)
                {
                    if (value is JObject o && Time(o, "started") is { } started && Time(o, "until") is { } until)
                    {
                        _driveRecovery[drive] = new DriveRecoveryRecord(drive, started, until, ReadSpot(o, "zone"), ReadSpot(o, "ref"), Str(o, "verdict"));
                    }
                }
            }

            if (root.TryGetValue("events", out object? ev) && ev is List<object?> list)
            {
                foreach (JObject o in list.OfType<JObject>())
                {
                    if (Time(o, "at") is { } at && Str(o, "game") is { } game && Str(o, "kind") is { } kind)
                    {
                        _events.Add(new AutoEvent(at, game, kind));
                    }
                }

                _events.RemoveRange(0, Math.Max(0, _events.Count - MaxEvents));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            _games.Clear();
            _relocations.Clear();
            _events.Clear();
            _driveRecovery.Clear();
            KeepBad();
            // 일일 상한 기록이 사라졌으므로 24 시간 동안 재배치를 막음 (보수). 이동 바이트 집계는 건드리지 않음.
            _holdUntil = _now() + RelocationHold;
            _log.Warn($"auto status file unreadable ({e.GetType().Name}), starting empty; {HoldDetail(_holdUntil.Value)}");
            Save(); // 다시 시작해도 24 시간은 유지되도록 바로 남김
        }
    }

    // 읽을 수 없는 파일은 .bad 로 보관 (조사용), 못 옮기면 그대로 둠: 다음 저장이 덮어씀
    private void KeepBad()
    {
        try
        {
            File.Move(_path!, _path + ".bad", true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn("auto status file not kept as .bad: " + e.GetType().Name);
        }
    }

    private static JObject Spot(ProbeSpot s) => new() { { "lcn", s.Lcn }, { "mbps", s.Mbps } };

    private static ProbeSpot? ReadSpot(JObject o, string key) =>
        o.TryGetValue(key, out object? v) && v is JObject b && b.TryGetValue("lcn", out object? l) && l is long lcn
            && b.TryGetValue("mbps", out object? m) && (m as double? ?? m as long?) is { } mbps ? new ProbeSpot(lcn, mbps) : null;

    private static AutoBootProbe? ReadBootProbe(JObject o) =>
        o.TryGetValue("boot_probe", out object? v) && v is JObject b && Time(b, "at") is { } at && b.TryGetValue("files", out object? f) && f is long files
            && b.TryGetValue("open_ms_per_file", out object? ms) && (ms as double? ?? ms as long?) is { } openMs
            ? new AutoBootProbe(at, (int)Math.Clamp(files, 0, int.MaxValue), openMs) : null;

    private static List<DateTimeOffset>? ReadPlays(JObject o) =>
        o.TryGetValue("plays", out object? v) && v is List<object?> list ? [.. list.Select(Time).OfType<DateTimeOffset>().TakeLast(MaxPlays)] : null;

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? Str(JObject o, string key) => o.TryGetValue(key, out object? v) ? v as string : null;

    private static DateTimeOffset? Time(JObject o, string key) => o.TryGetValue(key, out object? v) ? Time(v) : null;

    // 새 키를 먼저 보고, 없으면 옛 키 (tier0_probe_ready 이름 바꾸기 전 파일)를 봅니다.
    private static bool TryGetEither(JObject o, string key, string oldKey, out object? value) => o.TryGetValue(key, out value) || o.TryGetValue(oldKey, out value);

    private static DateTimeOffset? Time(object? v) =>
        v is string s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset t) ? t : null;
}
