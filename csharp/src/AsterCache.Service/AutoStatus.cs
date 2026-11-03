using System.Globalization;
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
    string Source, string? Launcher, string Stage, string? Detail, DateTimeOffset At, DateTimeOffset? LastPlayed, DateTimeOffset? RelocatedAt, int? HitPercent = null, DateTimeOffset? HitAt = null);

public sealed record AutoEvent(DateTimeOffset At, string Game, string Kind);

/// 자동 모드 상태 (<데이터 폴더>\auto_status.json). 서비스만 쓰고 트레이가 읽습니다. 파일이 없거나 깨졌으면 빈 상태로 시작합니다.
/// path 가 null 이면 (데이터 폴더를 믿을 수 없을 때) 메모리에만 둡니다. 쓰기는 임시 파일 → 이동으로 원자적입니다.
public sealed class AutoStatus
{
    public const int MaxEvents = 20;
    public static readonly TimeSpan RelocationKeep = TimeSpan.FromHours(48);
    public const int MoveRetries = 5;
    public static readonly TimeSpan MoveRetryDelay = TimeSpan.FromMilliseconds(100);

    private readonly object _lock = new();
    private readonly string? _path;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly Action<TimeSpan> _sleep;
    private readonly Dictionary<string, AutoGameEntry> _games = new(StringComparer.Ordinal);
    private readonly List<DateTimeOffset> _relocations = [];
    private readonly List<AutoEvent> _events = [];

    /// sleep 은 파일 교체를 다시 시도하기 전의 대기 (시험이 바꿈, 기본은 MoveRetryDelay 만큼 실제로 잠).
    public AutoStatus(string? path, ILog log, Func<DateTimeOffset>? now = null, Action<TimeSpan>? sleep = null)
    {
        _path = path;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
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
                ? old with { LastPlayed = at }
                : new AutoGameEntry(source, null, initialStage, null, at, at, null);
            Save();
        }
    }

    /// 자동 적용을 시작함 (이어하기 포함): 일일 상한 목록 (relocations) 에 시작 시각을 남깁니다. 끝났을 때 다시 남기지 않습니다.
    public void BeginRelocation(DateTimeOffset at)
    {
        lock (_lock)
        {
            _relocations.Add(at);
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

    /// 최근 window 안에 시작한 자동 적용 수.
    public int RelocationsWithin(TimeSpan window)
    {
        lock (_lock)
        {
            DateTimeOffset since = _now() - window;
            return _relocations.Count(t => t > since);
        }
    }

    public IReadOnlyDictionary<string, DateTimeOffset> LastPlayed()
    {
        lock (_lock)
        {
            return _games.Where(kv => kv.Value.LastPlayed is not null).ToDictionary(kv => kv.Key, kv => kv.Value.LastPlayed!.Value, StringComparer.Ordinal);
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
        _relocations.RemoveAll(t => now - t > RelocationKeep);
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
            if (e.HitPercent is { } hp)
            {
                game.Add("hit_percent", (long)hp); // 재지 않은 게임의 항목 모양은 그대로
                game.Add("hit_at", e.HitAt is { } ha ? Iso(ha) : null);
            }

            games.Add(name, game);
        }

        var root = new JObject
        {
            { "updated", Iso(now) },
            { "relocations", _relocations.Select(t => (object?)Iso(t)).ToList() },
            { "games", games },
            { "events", _events.Select(e => (object?)new JObject { { "at", Iso(e.At) }, { "game", e.Game }, { "kind", e.Kind } }).ToList() },
        };
        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(PyJson.Dumps(root, indent: 2, ensureAscii: false) + "\n"));
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
                _relocations.AddRange(times.Select(Time).OfType<DateTimeOffset>());
            }

            if (root.TryGetValue("games", out object? g) && g is JObject games)
            {
                foreach ((string name, object? value) in games)
                {
                    if (value is JObject o && Str(o, "stage") is { } stage)
                    {
                        _games[name] = new AutoGameEntry(Str(o, "source") ?? "manual", Str(o, "launcher"), stage, Str(o, "detail"), Time(o, "at") ?? _now(), Time(o, "last_played"), Time(o, "relocated_at"), o.TryGetValue("hit_percent", out object? hp) && hp is long h ? (int)Math.Clamp(h, 0, 100) : null, Time(o, "hit_at"));
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
            _log.Warn("auto status file unreadable, starting empty: " + e.GetType().Name);
            _games.Clear();
            _relocations.Clear();
            _events.Clear();
        }
    }

    private static string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? Str(JObject o, string key) => o.TryGetValue(key, out object? v) ? v as string : null;

    private static DateTimeOffset? Time(JObject o, string key) => o.TryGetValue(key, out object? v) ? Time(v) : null;

    private static DateTimeOffset? Time(object? v) =>
        v is string s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset t) ? t : null;
}
