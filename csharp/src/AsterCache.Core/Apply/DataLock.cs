namespace AsterCache.Core.Apply;

/// 서비스와 CLI 가 활성 계획·저널·존 예약을 함께 만지는 구간 (예약 조회 → 계획 전환 → 적용) 을 직렬화하는 프로세스 간 잠금.
/// 계획 폴더의 zones.lock 을 FileShare.None 으로 열어 잡고, 닫으면 풀립니다 (프로세스가 죽으면 OS 가 풀어 줌). 같은 폴더의 모든 게임이 한 잠금을 쓰므로 같은 볼륨의 존 예약도 함께 보호됩니다.
/// 저널 경로를 함께 주면 저널 폴더의 zones.lock 도 (다르면) 같이 잡습니다: 서로 다른 폴더의 계획이 같은 저널을 쓰는 호출도 직렬화됩니다. 여러 파일은 이름 순으로 잡아 교착이 없습니다.
/// 재진입: 같은 스레드가 이미 잡은 파일은 다시 열지 않고 (참조 횟수만 올림) 통과합니다. 다른 스레드와 다른 프로세스는 계속 배제됩니다. 해제는 잡은 스레드에서 하고, 바깥 구간이 끝나야 파일이 풀립니다.
/// 사용: <code>using DataLock? gate = DataLock.TryAcquire(planPath); if (gate is null) { /* busy: 나중에 다시 */ return; }</code>
public sealed class DataLock : IDisposable
{
    public const string FileName = "zones.lock";

    /// 잠금을 못 잡아 구간을 건너뛴 이유 (ApplyReport.Stopped 에 넣는 값).
    public const string BusyReason = "zone_lock_busy";

    /// 잠금을 기다리는 동안 활성 계획이 호출자가 넘긴 계획과 달라졌을 때 PlanRejectedException 의 문구 (호출자는 다음에 새 계획으로 다시 시도).
    public const string PlanChangedMessage = "plan changed while waiting for the zone lock";

    /// 서비스 쪽 재계획이 잠금을 못 잡았을 때 PlanRejectedException 의 문구 (기존 보류 처리를 그대로 탐).
    public const string BusyMessage = "zone lock busy: another apply or plan switch is running";

    /// 잠금을 기다리는 기본 시간 (시험이 바꿈).
    public static TimeSpan DefaultWait { get; set; } = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan RetryEvery = TimeSpan.FromMilliseconds(100);

    // 이 스레드가 잡고 있는 잠금 파일 경로 → 참조 횟수 (재진입 판정).
    [ThreadStatic]
    private static Dictionary<string, int>? s_held;

    private readonly FileStream[] _files;
    private readonly string[] _paths;
    private readonly Dictionary<string, int> _registry;
    private bool _disposed;

    private DataLock(FileStream[] files, string[] paths, Dictionary<string, int> registry)
    {
        _files = files;
        _paths = paths;
        _registry = registry;
    }

    /// 이 스레드가 planPath 의 폴더 잠금을 잡고 있는가 (재진입 포함).
    public static bool IsHeld(string planPath) => s_held?.ContainsKey(PathFor(planPath)) == true;

    /// 계획·저널 파일을 바꾸기 전에 부르는 증표 검사: 이 스레드가 path 폴더의 잠금을 잡고 있지 않으면 예외 (잠금 밖 변경을 막음).
    public static void RequireHeld(string path)
    {
        if (!IsHeld(path))
        {
            throw new InvalidOperationException("zone lock is not held by this thread: " + path);
        }
    }

    /// planPath 가 있는 폴더의 잠금 파일 경로 (CLI 와 서비스가 같은 계획 경로로 같은 파일을 가리킴).
    public static string PathFor(string planPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(planPath)) ?? ".", FileName);

    /// 잠금을 잡습니다. 다른 곳이 잡고 있으면 wait (기본 15 초) 까지 100 ms 간격으로 다시 시도하고, 끝내 못 잡으면 null (busy, 예외 아님).
    /// journalPath 가 있으면 저널 폴더의 잠금도 함께 잡습니다. sleep 과 now 는 시험이 바꿉니다.
    public static DataLock? TryAcquire(string planPath, TimeSpan? wait = null, Action<TimeSpan>? sleep = null, Func<DateTime>? now = null, string? journalPath = null)
    {
        string[] paths = new[] { PathFor(planPath), journalPath is null ? null : PathFor(journalPath) }
            .OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string path in paths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        }

        Dictionary<string, int> mine = s_held ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        string[] missing = [.. paths.Where(p => !mine.ContainsKey(p))];
        FileStream[]? files = missing.Length == 0 ? [] : Open(missing, wait ?? DefaultWait, sleep ?? Thread.Sleep, now ?? (() => DateTime.UtcNow));
        if (files is null)
        {
            return null;
        }

        foreach (string path in paths)
        {
            mine[path] = mine.GetValueOrDefault(path) + 1;
        }

        return new DataLock(files, paths, mine);
    }

    // 파일을 이름 순으로 모두 열어 둡니다. 공유 위반 (IOException) 만 wait 까지 재시도하고 (이미 연 핸들은 놓음), 그 밖의 예외는 이미 연 핸들을 놓은 뒤 다시 던집니다.
    private static FileStream[]? Open(string[] paths, TimeSpan wait, Action<TimeSpan> sleep, Func<DateTime> now)
    {
        DateTime until = now() + wait;
        while (true)
        {
            var held = new List<FileStream>();
            try
            {
                foreach (string path in paths)
                {
                    held.Add(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
                }

                return [.. held];
            }
            catch (IOException)
            {
                held.ForEach(f => f.Dispose());
                if (now() >= until)
                {
                    return null;
                }

                sleep(RetryEvery);
            }
            catch
            {
                held.ForEach(f => f.Dispose());
                throw;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (string path in _paths)
        {
            if (_registry.GetValueOrDefault(path) <= 1)
            {
                _ = _registry.Remove(path);
            }
            else
            {
                _registry[path]--;
            }
        }

        foreach (FileStream f in _files)
        {
            f.Dispose();
        }
    }
}
