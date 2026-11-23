using System.Globalization;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;

namespace AsterCache.Service;

/// 서비스가 게임 드라이브에서 읽은 양의 목적: Trigger 는 게임·런처 시작, 게임 종료 뒤 한 번의 다시 채우기, 서비스 시작 예열, 재배치 뒤 예열, 사용자가 시킨 명령처럼 계기가 있는 읽기 (상한 없음),
/// Idle 은 계기 없이 주기로 도는 재예열·보충 예열·사다리와 재배치 뒤 예열 (하루 상한 대상), Recovery 는 재배치 뒤 드라이브 회복 표본 (따로 횟수·바이트 상한).
/// RecoveryCut 은 끝까지 읽지 못하고 끊긴 (또는 실패한) 회복 표본의 바이트: 읽은 만큼 바이트 상한에는 들지만 하루 표본 횟수에는 세지 않습니다.
internal enum ReadPurpose
{
    Trigger,
    Idle,
    Recovery,
    RecoveryCut,
}

/// 드라이브별·목적별로 읽은 바이트를 24시간 이동 창으로 모읍니다. 상한 판정과 하루 한 번의 로그가 이 값을 쓰고, 서비스를 다시 시작해도 이어지게 예열 상태 파일 (prewarm_status.json 의 disk_reads) 에 함께 남습니다.
/// 목적은 읽는 스레드가 정하지 않고 호출한 쪽이 Scope 로 정합니다 (재예열 점검·보충 틱·사다리 틱은 Idle, 그 밖은 Trigger): 같은 스트림 함수를 계기 있는 예열과 주기 예열이 함께 쓰기 때문입니다.
internal sealed class DiskReadLedger(Func<DateTimeOffset> now)
{
    /// 집계 창 (이동 창).
    public static readonly TimeSpan Window = TimeSpan.FromHours(24);

    // 같은 드라이브·목적의 읽기가 이 간격 안에 이어지면 한 항목으로 합쳐 상태 파일이 커지지 않게 함 (회복 표본은 표본 하나가 한 항목이어야 횟수를 셀 수 있어 합치지 않음).
    private static readonly TimeSpan MergeWithin = TimeSpan.FromMinutes(1);

    // 이만큼 이상 읽은 항목은 바로 상태 파일에 씀 (그보다 작은 것은 다음 상태 파일 쓰기에 실려 나가고, 그 사이에 서비스가 끊기면 이만큼보다 작은 양만 잃음).
    private const long PersistBytes = 64L << 20;

    private static readonly AsyncLocal<ReadPurpose?> Scoped = new();

    private readonly object _lock = new();
    private readonly Dictionary<(string Drive, ReadPurpose Purpose, string Game), List<(DateTimeOffset At, long Bytes)>> _entries = []; // Game: 게임별로 세는 읽기의 게임 이름, 아니면 빈 문자열
    private DateTimeOffset? _loggedAt; // 하루 한 번 로그를 마지막으로 남긴 시각, _lock 으로 보호

    /// 큰 읽기를 더한 뒤 상태 파일을 쓰는 호출 (Prewarmer 가 넣음, 없으면 메모리에만 둠).
    public Action? Persist { get; set; }

    /// 지금 읽기의 목적: Scope 안이면 그 값, 아니면 Trigger.
    public static ReadPurpose Current => Scoped.Value ?? ReadPurpose.Trigger;

    /// 이 스레드 (와 거기서 이어지는 작업) 의 읽기를 purpose 로 셉니다. Dispose 하면 이전 목적으로 돌아갑니다.
    public static IDisposable Scope(ReadPurpose purpose)
    {
        ReadPurpose? previous = Scoped.Value;
        Scoped.Value = purpose;
        return new Restore(previous);
    }

    private sealed class Restore(ReadPurpose? previous) : IDisposable
    {
        public void Dispose() => Scoped.Value = previous;
    }

    /// path (게임 설치 루트나 드라이브 키 "D:") 가 놓인 드라이브의 키 ("D:").
    public static string DriveOf(string path) => PlanFile.SplitDrive(path) is { Length: > 0 } drive ? drive.ToUpperInvariant() : path.ToUpperInvariant();

    /// path 의 드라이브에서 bytes 를 읽었다고 더합니다 (0 이하는 무시). game 을 주면 그 게임이 읽은 몫으로도 셉니다 (드라이브 합계에는 늘 들어감).
    public void Add(string path, ReadPurpose purpose, long bytes, string? game = null)
    {
        if (bytes <= 0)
        {
            return;
        }

        DateTimeOffset at = now();
        lock (_lock)
        {
            List<(DateTimeOffset At, long Bytes)> list = ListOf(path, purpose, game ?? string.Empty);
            Fresh(list, at);
            if (purpose is not (ReadPurpose.Recovery or ReadPurpose.RecoveryCut) && list.Count > 0 && at - list[^1].At < MergeWithin)
            {
                list[^1] = (list[^1].At, list[^1].Bytes + bytes);
            }
            else
            {
                list.Add((at, bytes));
            }
        }

        if (bytes >= PersistBytes)
        {
            Persist?.Invoke();
        }
    }

    /// 최근 24시간 안에 path 의 드라이브에서 purpose (와 also) 로 읽은 바이트와 항목 수 (회복 표본은 항목 하나가 표본 하나). game 을 주면 그 게임이 읽은 몫만.
    public (long Bytes, int Count) Used(string path, ReadPurpose purpose, string? game = null, ReadPurpose? also = null)
    {
        DateTimeOffset at = now();
        lock (_lock)
        {
            List<(DateTimeOffset At, long Bytes)> all = Gather(path, purpose, game, at, also);
            return (all.Sum(e => e.Bytes), all.Count);
        }
    }

    /// 가장 오래된 항목부터 빼 가며 within 이 처음 참이 되는 때 (그 항목이 24시간 창에서 빠지는 시각). 지금 이미 참이면 지금.
    public DateTimeOffset FreeAt(string path, ReadPurpose purpose, Func<long, int, bool> within, string? game = null, ReadPurpose? also = null)
    {
        DateTimeOffset at = now();
        lock (_lock)
        {
            List<(DateTimeOffset At, long Bytes)> list = Gather(path, purpose, game, at, also);
            list.Sort((a, b) => a.At.CompareTo(b.At));
            long bytes = list.Sum(e => e.Bytes);
            int count = list.Count;
            if (within(bytes, count))
            {
                return at;
            }

            foreach ((DateTimeOffset entryAt, long entryBytes) in list)
            {
                (bytes, count) = (bytes - entryBytes, count - 1);
                if (within(bytes, count))
                {
                    return entryAt + Window;
                }
            }

            return at;
        }
    }

    /// 읽은 기록이 있는 드라이브 키 (정렬).
    public IReadOnlyList<string> Drives()
    {
        DateTimeOffset at = now();
        lock (_lock)
        {
            foreach (List<(DateTimeOffset At, long Bytes)> list in _entries.Values)
            {
                Fresh(list, at);
            }

            return [.. _entries.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key.Drive).Distinct().Order(StringComparer.Ordinal)];
        }
    }

    /// 하루 한 번 로그를 남길 때인가 (기록이 있고 마지막 로그가 24시간 넘게 전이거나 시계가 뒤로 갔을 때): 참이면 시각을 남겨 다음 24시간 동안은 거짓입니다 (서비스를 다시 시작해도 이어짐).
    public bool TakeDailyLog()
    {
        DateTimeOffset at = now();
        lock (_lock)
        {
            foreach (List<(DateTimeOffset At, long Bytes)> list in _entries.Values)
            {
                Fresh(list, at);
            }

            if (_entries.Values.All(l => l.Count == 0) || (_loggedAt is { } last && last <= at && at - last < Window))
            {
                return false;
            }

            _loggedAt = at;
            return true;
        }
    }

    /// 상태 파일에 쓸 값: drives 아래 드라이브마다 목적별 "시각=바이트;..." 문자열.
    public JObject ToJson()
    {
        DateTimeOffset at = now();
        lock (_lock)
        {
            JObject drives = new();
            foreach (IGrouping<string, KeyValuePair<(string Drive, ReadPurpose Purpose, string Game), List<(DateTimeOffset At, long Bytes)>>> group in _entries.GroupBy(kv => kv.Key.Drive).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                JObject purposes = new();
                foreach (KeyValuePair<(string Drive, ReadPurpose Purpose, string Game), List<(DateTimeOffset At, long Bytes)>> kv in group.OrderBy(kv => kv.Key.Purpose).ThenBy(kv => kv.Key.Game, StringComparer.Ordinal))
                {
                    Fresh(kv.Value, at);
                    if (kv.Value.Count > 0)
                    {
                        purposes[kv.Key.Purpose.ToString().ToLowerInvariant() + (kv.Key.Game.Length > 0 ? "/" + kv.Key.Game : string.Empty)] = string.Join(';', kv.Value.Select(e => e.At.ToString("O", CultureInfo.InvariantCulture) + "=" + e.Bytes.ToString(CultureInfo.InvariantCulture)));
                    }
                }

                if (purposes.Count > 0)
                {
                    drives[group.Key] = purposes;
                }
            }

            JObject root = new() { { "drives", drives } };
            if (_loggedAt is { } logged)
            {
                root["logged_at"] = logged.ToString("O", CultureInfo.InvariantCulture);
            }

            return root;
        }
    }

    /// 상태 파일에서 되살립니다 (깨진 항목은 건너뜀). 24시간이 지난 항목과 미래 시각은 Fresh 가 다음 조회에서 정리합니다.
    public void Load(JObject root)
    {
        lock (_lock)
        {
            if (root.TryGetValue("logged_at", out object? logged) && logged is string loggedText
                && DateTimeOffset.TryParse(loggedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset loggedAt))
            {
                _loggedAt = loggedAt;
            }

            if (!root.TryGetValue("drives", out object? drives) || drives is not JObject all)
            {
                return;
            }

            foreach ((string drive, object? value) in all)
            {
                if (value is not JObject purposes)
                {
                    continue;
                }

                foreach ((string name, object? text) in purposes)
                {
                    string[] nameParts = name.Split('/', 2);
                    if (!Enum.TryParse(nameParts[0], ignoreCase: true, out ReadPurpose purpose) || text is not string entries)
                    {
                        continue;
                    }

                    List<(DateTimeOffset At, long Bytes)> list = ListOf(drive, purpose, nameParts.Length > 1 ? nameParts[1] : string.Empty);
                    foreach (string part in entries.Split(';', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string[] pair = part.Split('=');
                        if (pair.Length == 2 && DateTimeOffset.TryParse(pair[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset at)
                            && long.TryParse(pair[1], NumberStyles.None, CultureInfo.InvariantCulture, out long bytes) && bytes > 0)
                        {
                            list.Add((at, bytes));
                        }
                    }

                    list.Sort((a, b) => a.At.CompareTo(b.At));
                }
            }
        }
    }

    // 드라이브·목적의 항목 (game 이 null 이면 모든 게임의 항목을 합침, 아니면 그 게임 것만), 24시간 지난 것은 지운 뒤 (_lock 안에서 부름).
    private List<(DateTimeOffset At, long Bytes)> Gather(string path, ReadPurpose purpose, string? game, DateTimeOffset at, ReadPurpose? also = null)
    {
        string drive = DriveOf(path);
        List<(DateTimeOffset At, long Bytes)> found = [];
        foreach (KeyValuePair<(string Drive, ReadPurpose Purpose, string Game), List<(DateTimeOffset At, long Bytes)>> kv in _entries)
        {
            if (kv.Key.Drive == drive && (kv.Key.Purpose == purpose || kv.Key.Purpose == also) && (game is null || kv.Key.Game == game))
            {
                Fresh(kv.Value, at);
                found.AddRange(kv.Value);
            }
        }

        return found;
    }

    private List<(DateTimeOffset At, long Bytes)> ListOf(string path, ReadPurpose purpose, string game)
    {
        (string, ReadPurpose, string) key = (DriveOf(path), purpose, game);
        if (!_entries.TryGetValue(key, out List<(DateTimeOffset At, long Bytes)>? list))
        {
            _entries[key] = list = [];
        }

        return list;
    }

    // 24시간 지난 항목을 지우고, 시계가 뒤로 가서 지금보다 뒤인 시각은 지금으로 당겨 상한이 풀리지 않게 합니다 (_lock 안에서 부름).
    private static void Fresh(List<(DateTimeOffset At, long Bytes)> list, DateTimeOffset at)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].At > at)
            {
                list[i] = (at, list[i].Bytes);
            }
        }

        _ = list.RemoveAll(e => at - e.At >= Window);
    }
}
