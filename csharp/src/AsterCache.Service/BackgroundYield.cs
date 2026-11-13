using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 배경 메모리 양보가 바깥 세계에 닿는 곳 전부. 시험은 가짜로 바꿉니다.
public sealed class BackgroundYieldEnv
{
    public required Func<IReadOnlyList<(int Pid, string Exe)>> Processes { get; init; }

    /// 프로세스를 열지 않고 세션 id 를 묻습니다 (모르면 null).
    public required Func<int, uint?> SessionId { get; init; }

    /// 조회 권한만으로 경로·작업 집합·생성 시각을 읽습니다 (못 읽으면 null).
    public required Func<int, ProcessInfo?> Describe { get; init; }

    public required Func<int, bool> Trim { get; init; }

    public required Func<int, int?> MemoryPriority { get; init; }

    public required Func<int, int, bool> SetMemoryPriority { get; init; }

    public required Func<long> AvailableMib { get; init; }

    /// 대기 목록 조회 (프로세스를 열지 않는 시스템 전체 통계, 못 읽으면 null). 기본값은 조회 불가.
    public Func<MemoryLists?> Lists { get; init; } = () => null;

    /// 물리 RAM (바이트, 모르면 0). 비우기 목표 여유분을 계산합니다.
    public Func<long> PhysicalBytes { get; init; } = () => 0;

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// 가용 메모리가 가라앉기를 기다리는 대기 (취소되면 일찍 돌아옴).
    public Action<TimeSpan, CancellationToken> Delay { get; init; } = (t, ct) => ct.WaitHandle.WaitOne(t);

    /// 상태 파일 본문 (없으면 null)과 쓰기·지우기. 쓰기 실패는 IOException 등으로 알립니다.
    public Func<string?> ReadState { get; init; } = () => null;

    public Action<string> WriteState { get; init; } = _ => { };

    public Action DeleteState { get; init; } = () => { };

    /// 런처 탐색이 이미 메모리에 갖고 있는 게임 설치 폴더 (디스크를 새로 훑지 않음, 자동 모드가 꺼져 있으면 빈 목록).
    public Func<IReadOnlyList<string>> KnownGameFolders { get; init; } = () => [];

    public string WindowsDir { get; init; } = string.Empty;

    public int SelfPid { get; init; } = -1;

    public static BackgroundYieldEnv Create(string stateFile, Func<IReadOnlyList<string>>? knownGameFolders = null) => new()
    {
        Processes = ProcessSnapshot.Take,
        SessionId = ProcessMemory.SessionId,
        Describe = ProcessMemory.Describe,
        Trim = ProcessMemory.Trim,
        MemoryPriority = ProcessMemory.MemoryPriority,
        SetMemoryPriority = ProcessMemory.SetMemoryPriority,
        AvailableMib = SystemMemory.AvailableMib,
        Lists = MemoryListInfo.Query,
        PhysicalBytes = SystemMemory.TotalBytes,
        KnownGameFolders = knownGameFolders ?? (() => []),
        WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        SelfPid = Environment.ProcessId,
        ReadState = () => File.Exists(stateFile) ? File.ReadAllText(stateFile) : null,
        WriteState = text =>
        {
            string tmp = stateFile + ".tmp";
            DurableFile.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(text));
            File.Move(tmp, stateFile, true);
        },
        DeleteState = () => File.Delete(stateFile),
    };
}

/// 게임 세션 동안 게임과 무관한 배경 프로세스가 메모리를 양보하게 합니다 (설정 memory.background_yield).
/// 비우기: 여유분 (여유 + 우선순위 0~4 대기) 이 모자랄 때만, 그리고 세션 중에는 캐시가 실제로 재사용될 때만 작업 집합을 비워 그 메모리가 예열 캐시보다 먼저 재사용되게 합니다. 우선순위 낮추기: 세션 동안 메모리 우선순위를 매우 낮음으로 내려 그들의 읽기가 캐시를 밀어내지 못하게 하고, 세션이 끝나면 되돌립니다.
/// 다른 프로세스에 하는 일은 이 두 가지뿐이며, 게임·런처·안티치트·세션 0 프로세스는 이름·세션 단계에서 걸러 어떤 권한으로도 열지 않습니다 (BackgroundProcessRules).
public sealed partial class BackgroundYield(Func<AsterSettings> settings, BackgroundYieldEnv env, ILog log)
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan IdleEnd = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaintainEvery = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan RetrimEvery = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan SettleStep = TimeSpan.FromMilliseconds(500);
    public static readonly TimeSpan SettleMax = TimeSpan.FromSeconds(3);
    public const long TrimMinBytes = 32L << 20;
    public const long HeadroomFloorBytes = 4096L << 20; // 시작 때 이만큼 여유분이 있으면 비우지 않음 (물리 RAM/4 와 큰 쪽)
    public const long RetrimCacheBytes = 512L << 20; // 유지 주기 사이에 캐시 (우선순위 5~7) 가 이만큼 재사용돼야 다시 비움
    public const long SettleCalmMib = 64;

    private readonly object _lock = new(); // 상태는 모두 이 락 아래 (Begin 의 가용 메모리 대기만 락 밖)
    private readonly Dictionary<int, YieldTarget> _targets = []; // 이번 세션의 대상 (경로 검사까지 통과한 프로세스)
    private readonly Dictionary<int, LoweredProcess> _lowered = []; // 우선순위를 낮춘 프로세스와 원래 값 (되돌리기에 실패한 것도 남아 다음에 다시 시도)
    private readonly HashSet<int> _skipped = []; // 경로 검사에서 빠졌거나 못 연 pid: 유지 때 다시 열지 않음
    private bool _active;
    private bool _shutdown;
    private DateTimeOffset? _lastBegin;
    private DateTimeOffset _lastActivity;
    private DateTimeOffset _lastMaintain;
    private DateTimeOffset _lastTrim; // 마지막 비우기 (비우지 않았다면 세션 시작)
    private long? _lastRepurposed; // 직전 조회의 캐시 재사용 누계 (조회가 안 됐으면 null)
    private bool _beginTrimmed; // 마지막 세션 시작이 비웠는지
    private bool _listsWarned;
    private readonly Dictionary<int, TrimMemo> _trimMemo = []; // 비운 대상의 직전 작업 집합 (되찾았는지 보려고), 세션이 끝나면 지움

    private bool Enabled => settings().Memory.BackgroundYield;

    /// 세션을 시작합니다: 대상 선택 → 우선순위 낮추기 → 비우기. 설정이 꺼져 있거나 서비스 종료 뒤이거나 60초 안에 다시 불리면 아무것도 안 함. settle 이면 (예열이 가용 메모리를 읽기 전) 비운 페이지가 가용으로 넘어와 가라앉을 때까지 최대 3초 기다립니다.
    public void Begin(string reason, bool settle, CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            return;
        }

        try
        {
            BeginCore(reason, settle, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Error("background yield failed", e);
        }
    }

    private void BeginCore(string reason, bool settle, CancellationToken ct)
    {
        YieldStart? started = null;
        bool justStarted = false;
        lock (_lock)
        {
            if (_shutdown || !Enabled)
            {
                return;
            }

            DateTimeOffset now = env.Now();
            if (_lastBegin is { } last && now - last < MinInterval)
            {
                justStarted = _beginTrimmed && now - last < SettleMax;
            }
            else
            {
                started = StartSession(now);
            }
        }

        if (started is null)
        {
            if (settle && justStarted)
            {
                _ = WaitForSettle(ct); // 같은 순간 다른 경로 (게임 시작) 가 먼저 비웠으면 그 결과가 가라앉기를 같이 기다림
            }

            return;
        }

        if (!started.DidTrim)
        {
            log.Info($"background yield ({reason}): memory priority lowered for {started.Lowered}, no trim needed (headroom {started.HeadroomMib} MiB, available {started.Before} MiB)");
            return;
        }

        string available = settle ? $"{started.Before} -> {WaitForSettle(ct)}" : started.Before.ToString();
        string headroom = started.HeadroomMib is { } h ? $", headroom {h} MiB" : string.Empty;
        log.Info($"background yield ({reason}): trimmed {started.Trimmed} processes ({started.TrimmedBytes >> 20} MiB working set), memory priority lowered for {started.Lowered}, available {available} MiB{headroom}");
    }

    /// 30초 틱에서: 세션이 활성일 때만 일합니다. 이미 뜬 프로세스 스냅샷을 받아 쓰며 틱마다 프로세스를 열지 않습니다 (유지 주기에만).
    /// 등록 게임이 실행 중이면 마지막 활동을 갱신하고, 없고 10분이 지났거나 설정이 꺼졌으면 끝냅니다.
    public void Tick(IReadOnlyList<(int Pid, string Exe)> processes)
    {
        if (!Monitor.TryEnter(_lock))
        {
            return; // Begin 이 비우는 중: 다음 틱에
        }

        try
        {
            if (!_active)
            {
                return;
            }

            if (!Enabled)
            {
                EndLocked();
                return;
            }

            DateTimeOffset now = env.Now();
            var names = new HashSet<string>(processes.Select(p => p.Exe), StringComparer.OrdinalIgnoreCase);
            bool running = settings().Games.Any(g => g.ExeNames.Any(names.Contains));
            if (running)
            {
                _lastActivity = now;
            }
            else if (now - _lastActivity >= IdleEnd)
            {
                EndLocked();
                return;
            }

            if (running && now - _lastMaintain >= MaintainEvery)
            {
                Maintain(processes, now);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log.Error("background yield failed", e);
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    /// 세션을 끝내고 낮춘 우선순위를 되돌립니다 (게임 종료). 활성이 아니고 남은 것도 없으면 아무것도 안 함.
    public void End()
    {
        lock (_lock)
        {
            try
            {
                EndLocked();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.Error("background yield failed", e);
            }
        }
    }

    /// 서비스 종료: 종료 표시를 세우고 (이후 도착한 Begin 은 무시) 세션을 끝냅니다.
    public void Shutdown()
    {
        lock (_lock)
        {
            _shutdown = true;
        }

        End();
    }

    private void EndLocked()
    {
        if (!_active && _lowered.Count == 0)
        {
            return;
        }

        var kept = new List<LoweredProcess>();
        int restored = Restore([.. _lowered.Values], env.Processes(), kept);
        (_active, _lastBegin) = (false, null); // 끝난 세션은 60초 제한에 걸리지 않음
        _lowered.Clear();
        foreach (LoweredProcess k in kept)
        {
            _lowered[k.Pid] = k;
        }

        _targets.Clear();
        _skipped.Clear();
        _trimMemo.Clear();
        (_lastRepurposed, _listsWarned) = (null, false);
        _ = SaveState();
        log.Info($"background yield ended: memory priority restored for {restored} processes");
    }

    // 비운 페이지는 변경 목록을 거쳐 대기 목록으로 가는 중이라 (2초 +2.1 GiB, 5초 +3.0, 10초 +3.7) 가용 메모리가 가라앉을 때까지 500ms 간격으로 잽니다:
    // 연속 두 번 증가분이 64 MiB 미만이거나 3초가 지나면 끝. 마지막으로 읽은 가용 메모리를 돌려줍니다.
    private long WaitForSettle(CancellationToken ct)
    {
        DateTimeOffset start = env.Now();
        long previous = env.AvailableMib();
        int calm = 0;
        while (env.Now() - start < SettleMax && !ct.IsCancellationRequested)
        {
            env.Delay(SettleStep, ct);
            long current = env.AvailableMib();
            calm = current - previous < SettleCalmMib ? calm + 1 : 0;
            previous = current;
            if (calm >= 2)
            {
                break;
            }
        }

        return previous;
    }

    private sealed record YieldStart(long Before, int Trimmed, long TrimmedBytes, int Lowered, bool DidTrim, long? HeadroomMib);
}

/// 비운 대상이 비우기 직전에 쓰던 작업 집합. Busy 는 되찾은 것으로 판정돼 이 세션에서 다시 비우지 않는 대상 (pid 재사용은 생성 시각으로 거름).
internal readonly record struct TrimMemo(long Created, long Before, bool Busy);

/// 세션 대상 프로세스: pid 는 재사용되므로 생성 시각 (FILETIME) 과 함께 같은 프로세스인지 봅니다. 이름과 경로는 규칙을 다시 돌릴 때 프로세스를 다시 열지 않으려고 기억합니다.
internal readonly record struct YieldTarget(int Pid, string Exe, string Path, long Created, long WorkingSetBytes);

/// 메모리 우선순위를 낮춘 프로세스와 원래 값. 상태 파일에서 읽은 옛 항목은 이름이 없을 수 있고, 경로는 파일에 적지 않습니다.
internal readonly record struct LoweredProcess(int Pid, string? Exe, string? Path, long Created, int Priority);
