namespace AsterCache.Core.Apply;

/// 서비스와 CLI 가 활성 계획·저널·존 예약을 함께 만지는 구간 (예약 조회 → 계획 전환 → 적용) 을 직렬화하는 프로세스 간 잠금.
/// 계획 폴더의 zones.lock 을 FileShare.None 으로 열어 잡고, 닫으면 풀립니다 (프로세스가 죽으면 OS 가 풀어 줌). 같은 폴더의 모든 게임이 한 잠금을 쓰므로 같은 볼륨의 존 예약도 함께 보호됩니다.
/// 재진입은 되지 않습니다: 이미 잡은 구간 안에서 다시 잡으려 하면 시간 초과로 null 입니다.
/// 사용: <code>using DataLock? gate = DataLock.TryAcquire(planPath); if (gate is null) { /* busy: 나중에 다시 */ return; }</code>
public sealed class DataLock : IDisposable
{
    public const string FileName = "zones.lock";

    /// 잠금을 못 잡아 구간을 건너뛴 이유 (ApplyReport.Stopped 에 넣는 값).
    public const string BusyReason = "zone_lock_busy";

    /// 서비스 쪽 재계획이 잠금을 못 잡았을 때 PlanRejectedException 의 문구 (기존 보류 처리를 그대로 탐).
    public const string BusyMessage = "zone lock busy: another apply or plan switch is running";

    /// 잠금을 기다리는 기본 시간 (시험이 바꿈).
    public static TimeSpan DefaultWait { get; set; } = TimeSpan.FromSeconds(15);

    private static readonly TimeSpan RetryEvery = TimeSpan.FromMilliseconds(100);

    private readonly FileStream _file;

    private DataLock(FileStream file) => _file = file;

    /// planPath 가 있는 폴더의 잠금 파일 경로 (CLI 와 서비스가 같은 계획 경로로 같은 파일을 가리킴).
    public static string PathFor(string planPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(planPath)) ?? ".", FileName);

    /// 잠금을 잡습니다. 다른 곳이 잡고 있으면 wait (기본 15 초) 까지 100 ms 간격으로 다시 시도하고, 끝내 못 잡으면 null (busy, 예외 아님).
    /// sleep 과 now 는 시험이 바꿉니다.
    public static DataLock? TryAcquire(string planPath, TimeSpan? wait = null, Action<TimeSpan>? sleep = null, Func<DateTime>? now = null)
    {
        string path = PathFor(planPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        sleep ??= Thread.Sleep;
        now ??= () => DateTime.UtcNow;
        DateTime until = now() + (wait ?? DefaultWait);
        while (true)
        {
            try
            {
                return new DataLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException)
            {
                if (now() >= until)
                {
                    return null;
                }

                sleep(RetryEvery);
            }
        }
    }

    public void Dispose() => _file.Dispose();
}
