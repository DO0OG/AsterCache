using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Discovery;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 예열한 게임의 파일을 읽기 전용 매핑으로 들고 있는 곳 (게임 프로세스는 건드리지 않음). prewarm.pin 이 켜져 있고 물리 RAM 이 16 GiB 이상일 때만 고정합니다.
/// 런처가 떠 있는 동안은 고정하지 않습니다. 풀리는 때: 그 게임 exe 시작, 런처 시작, 가용 RAM 이 min_free_ram_mib 아래, 설정에서 pin 이 꺼짐·게임이 빠짐, 서비스 종료.
/// 예외: 재배치 직후 예열의 "회복 기간 고정" 은 RAM 하한·런처 보류 없이 드라이브가 회복될 때까지 (최대 RecoveryPinMax) 들고 있고, 런처 시작으로는 풀리지 않으며 고정한 파일이 바뀌면 풀립니다.
public sealed class PinKeeper
{
    public const long MinPhysicalRamBytes = 16L << 30;

    /// pin 을 설정에 적지 않았을 때 자동으로 켜는 물리 RAM 하한.
    public const long AutoPinRamBytes = 24L << 30;

    /// 이 게임을 고정할 것인가: pin 이 명시되면 그 값, 아니면 물리 RAM 이 AutoPinRamBytes 이상일 때.
    public static bool Wanted(PrewarmSettings p, long physicalRamBytes) => p.PinSet ? p.Pin : physicalRamBytes >= AutoPinRamBytes;

    /// 파일 사이 게임 실행 재확인 간격: 파일 수만큼 프로세스 스냅샷을 찍으면 수 초 늦어짐 (Streaming.cs 의 IsGameRunning 이 스냅샷을 찍음).
    private static readonly TimeSpan GameCheckInterval = TimeSpan.FromMilliseconds(50);

    /// 회복 기간 고정을 들고 있는 최대 시간.
    public static readonly TimeSpan RecoveryPinMax = TimeSpan.FromMinutes(60);

    private sealed record RecoveryPin(string Drive, DateTimeOffset At, IReadOnlyList<Fingerprint> Files);

    private readonly PrewarmEnv _env;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _lock = new();
    private readonly Dictionary<string, IDisposable> _held = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, RecoveryPin> _recovery = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string Root, string? Cheat)> _cheat = new(StringComparer.OrdinalIgnoreCase); // 게임별 고정 금지 판정 (없음 포함, 탐지 실패는 넣지 않음): 게임 루트가 바뀌면 무효
    private readonly HashSet<string> _cheatLogged = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<GameSettings> _games = []; // Reconcile·CheckMemory 가 마지막으로 받은 등록 게임 (다른 스레드가 읽으므로 Volatile)
    private readonly Dictionary<string, bool> _deferred = new(StringComparer.OrdinalIgnoreCase); // 게임이 돌아서 미룬 고정 (afterRelocation 값): 게임이 끝난 뒤 다음 Reconcile·CheckMemory 에서 다시 시도
    private readonly HashSet<string> _holdLogged = new(StringComparer.OrdinalIgnoreCase); // 런처 보류 사유는 하나뿐이라 게임당 한 번만 남김
    private readonly HashSet<string> _yieldLogged = new(StringComparer.OrdinalIgnoreCase); // 디스크 양보 사유도 재시도 틱마다 반복하지 않게 게임당 한 번만 남김

    public PinKeeper(PrewarmEnv env, ILog log, Func<DateTimeOffset>? now = null)
    {
        _env = env;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// 디스크 읽기 중재: 있으면 양보 중인 디스크의 고정 (경로 해석·파일 열기) 은 미룹니다. 시험 기본값은 없음.
    public DiskReadCoordinator? Coordinator { get; set; }

    /// 드라이브 회복 판정. null 이면 회복 기간 고정은 꺼진 것과 같습니다.
    public IDriveRecovery? Recovery { get; set; }

    public IReadOnlyCollection<string> Held
    {
        get
        {
            lock (_lock)
            {
                return [.. _held.Keys];
            }
        }
    }

    /// 예열 성공 직후: 조건이 맞으면 그 게임의 존 파일을 고정합니다. afterRelocation 이고 그 드라이브가 회복 중이면 평소 RAM 하한·런처 보류 없이 회복 기간 고정을 합니다 (pin 을 false 로 명시한 게임 제외).
    public void TryPin(GameSettings game, bool afterRelocation = false)
    {
        if (DiskHasRunningGame(game))
        {
            Defer(game, afterRelocation);
            _log.Info($"{game.Name}: pin deferred (a game is running on the same disk; retried once it ends)"); // 경로·extent·파일 열기를 게임 디스크에서 하기 전에 멈춤
            return;
        }

        string drive = Path.GetPathRoot(game.OriginRoot) ?? string.Empty;
        bool recovering = afterRelocation && !(game.Prewarm.PinSet && !game.Prewarm.Pin) && Recovery?.IsRecovering(drive) == true;
        long ram = _env.PhysicalRamBytes();
        if (recovering)
        {
            long avail = _env.AvailableRamMib();
            if (avail < game.Prewarm.MinFreeRamMib)
            {
                _log.Info($"{game.Name}: recovery pin skipped (available {avail} MiB < reserve {game.Prewarm.MinFreeRamMib} MiB)");
                return;
            }
        }
        else
        {
            if (!Wanted(game.Prewarm, ram))
            {
                return;
            }

            if (ram < MinPhysicalRamBytes)
            {
                _log.Info($"{game.Name}: pin skipped (physical RAM {ram >> 30} GiB is below 16 GiB)");
                return;
            }

            if (_env.LauncherRunning())
            {
                if (_holdLogged.Add(game.Name))
                {
                    _log.Info($"{game.Name}: pin held back (a launcher is running; its updates may change the files)");
                }
                return;
            }
        }

        if (PinBlocker(game) is { } cheat)
        {
            bool first;
            lock (_lock)
            {
                first = _cheatLogged.Add(game.Name);
            }

            if (first)
            {
                _log.Info($"{game.Name}: pin skipped (anti-cheat: {cheat})");
            }

            return;
        }

        lock (_lock)
        {
            if (_held.ContainsKey(game.Name))
            {
                return;
            }
        }

        try
        {
            using DiskReadCoordinator.Lease? lease = Coordinator?.TryBegin(game.OriginRoot, "pin");
            bool Yielded() => Coordinator is not null && lease is not { IsValid: true }; // 시작 이벤트를 놓쳐도 양보 중인 디스크는 열지 않음
            if (Yielded())
            {
                Defer(game, afterRelocation);
                if (_yieldLogged.Add(game.Name))
                {
                    _log.Info($"{game.Name}: pin deferred (disk reads are yielded to a running game; retried once it ends)");
                }
                return;
            }

            IReadOnlyList<string> paths = _env.PinPaths(game);
            Fingerprint[] files = recovering ? [.. paths.Select(_env.FileStamp)] : []; // 고정 전에 재야 그 사이의 변경도 잡음
            if (DiskHasRunningGame(game) || Yielded()) // 경로 탐색 중에 같은 디스크 게임이 떴으면 파일을 열지 않음
            {
                Defer(game, afterRelocation);
                _log.Info($"{game.Name}: pin deferred (a game started on the same disk while resolving paths; retried once it ends)");
                return;
            }

            bool gameRunning = false;
            DateTimeOffset? checkedAt = null;
            Func<bool> stop = () => // 파일마다 프로세스 스냅샷을 새로 찍지 않도록 짧은 간격 안의 재확인은 이전 결과를 씀
            {
                DateTimeOffset at = _now();
                if (checkedAt is null || at - checkedAt.Value >= GameCheckInterval)
                {
                    gameRunning = DiskHasRunningGame(game);
                    checkedAt = at;
                }

                return gameRunning || Yielded();
            };
            PinHandle handle = OpenPin(paths, stop, out bool stopped);
            if (stopped || DiskHasRunningGame(game) || Yielded()) // 열기 뒤 확인은 재사용하지 않음 (직후에 같은 디스크 게임이 떴으면 바로 풂)
            {
                handle.Pin.Dispose(); // 고정하는 사이에 같은 디스크 게임이 떴으면 바로 풀고 끝난 뒤 다시 시도
                Defer(game, afterRelocation);
                _log.Info($"{game.Name}: pin deferred (a game started while pinning; retried once it ends)");
                return;
            }

            bool duplicate;
            lock (_lock)
            {
                duplicate = _held.ContainsKey(game.Name); // 다른 호출이 먼저 고정했으면 핸들을 덮어쓰지 않음
                if (!duplicate)
                {
                    _held[game.Name] = handle.Pin;
                    if (recovering)
                    {
                        _recovery[game.Name] = new RecoveryPin(drive, _now(), files);
                    }
                }
            }

            if (duplicate)
            {
                handle.Pin.Dispose();
                return;
            }

            _log.Info(recovering ? $"{game.Name}: pinned until drive recovers ({handle.Count} file(s))" : $"{game.Name}: pinned {handle.Count} file(s), {handle.Skipped} skipped");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            _log.Warn($"{game.Name}: pin failed: {e.GetType().Name}");
        }
    }

    // 환경 API 를 유지하면서도 파일 사이에 중단 여부를 확인합니다. 한 번의 OpenPin 호출은 한 파일만 열므로 실제 FilePin.Open 도 파일 단위로 진행됩니다.
    private PinHandle OpenPin(IReadOnlyList<string> paths, Func<bool> stop, out bool stopped)
    {
        var pins = new List<IDisposable>();
        int count = 0;
        int skipped = 0;

        try
        {
            foreach (string path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (stop())
                {
                    stopped = true;
                    DisposeOpened();
                    return new PinHandle(new PinCollection([]), 0, 0);
                }

                PinHandle opened = _env.OpenPin([path]);
                pins.Add(opened.Pin);
                count += opened.Count;
                skipped += opened.Skipped;
            }
        }
        catch
        {
            DisposeOpened();
            throw;
        }

        stopped = false;
        return new PinHandle(new PinCollection(pins), count, skipped);

        void DisposeOpened()
        {
            foreach (IDisposable pin in pins)
            {
                try
                {
                    pin.Dispose();
                }
                catch (Exception)
                {
                    // 한 핸들의 해제 실패가 나머지 고정을 풀지 못하게 막음 (실패한 핸들은 다시 풀 방법이 없음)
                }
            }

            pins.Clear();
        }
    }

    private sealed class PinCollection(List<IDisposable> pins) : IDisposable
    {
        private List<IDisposable>? _pins = pins;

        public void Dispose()
        {
            List<IDisposable>? pins = Interlocked.Exchange(ref _pins, null);
            if (pins is null)
            {
                return;
            }

            foreach (IDisposable pin in pins)
            {
                try
                {
                    pin.Dispose();
                }
                catch (Exception)
                {
                    // 한 핸들의 해제 실패가 나머지 고정을 풀지 못하게 막음 (실패한 핸들은 다시 풀 방법이 없음)
                }
            }
        }
    }

    // 게임이 끝난 뒤 다음 Reconcile·CheckMemory 가 다시 시도하도록 기록.
    private void Defer(GameSettings game, bool afterRelocation)
    {
        lock (_lock)
        {
            _deferred[game.Name] = afterRelocation;
        }
    }

    // 이 게임이나 같은 물리 디스크의 알려진 등록 게임이 도는가. 조회가 실패하면 보호 중으로 봄. 다른 게임 목록은 Reconcile·CheckMemory 가 받은 것 (서비스는 시작할 때 Worker 가 SetGames 로 먼저 줌).
    private bool DiskHasRunningGame(GameSettings game)
    {
        try
        {
            IReadOnlyList<GameSettings> games = Volatile.Read(ref _games);
            return _env.GameRunning(game) || games.Any(o => !string.Equals(o.Name, game.Name, StringComparison.OrdinalIgnoreCase) && _env.SameDisk(o.OriginRoot, game.OriginRoot) && _env.GameRunning(o));
        }
        catch (Exception)
        {
            return true;
        }
    }

    public void Release(string game, string why)
    {
        IDisposable? pin;
        lock (_lock)
        {
            _held.Remove(game, out pin);
            _recovery.Remove(game);
        }

        if (pin is not null)
        {
            pin.Dispose();
            _log.Info($"{game}: pin released ({why})");
        }
    }

    /// keepRecovery: 회복 기간 고정은 남깁니다 (런처 시작 때).
    public void ReleaseAll(string why, bool keepRecovery = false)
    {
        foreach (string name in Held)
        {
            if (!keepRecovery || !IsRecoveryPin(name))
            {
                Release(name, why);
            }
        }
    }

    private bool IsRecoveryPin(string game)
    {
        lock (_lock)
        {
            return _recovery.ContainsKey(game);
        }
    }

    // 안티치트 게임은 파일을 고정하지 않음 (보호 드라이버가 열린 핸들을 문제 삼을 수 있음): 등록 값 (설정을 읽을 때 Riot 규칙으로 채워짐), 없으면 폴더 이름 (한 번만 봄), 탐지 실패도 막음.
    private string? PinBlocker(GameSettings game)
    {
        if (AntiCheat.ForbidsPin(game.AntiCheat))
        {
            return game.AntiCheat;
        }

        lock (_lock)
        {
            if (_cheat.TryGetValue(game.Name, out var known) && known.Root == game.OriginRoot)
            {
                return known.Cheat;
            }
        }

        string? found = _env.AntiCheatOf(game);
        string? cheat = AntiCheat.ForbidsPin(found) ? found : null;
        if (found != AutoEnv.AntiCheatUnknown)
        {
            lock (_lock)
            {
                _cheat[game.Name] = (game.OriginRoot, cheat);
            }
        }

        return cheat;
    }

    // 게임이 돌아 미룬 고정을 게임이 끝났으면 다시 시도합니다 (설정에서 빠진 게임은 버림). 아직 돌면 그대로 둠.
    private void RetryDeferred(IReadOnlyList<GameSettings> games)
    {
        KeyValuePair<string, bool>[] pending;
        lock (_lock)
        {
            pending = [.. _deferred];
        }

        foreach ((string name, bool afterRelocation) in pending)
        {
            GameSettings? game = games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
            if (game is not null && DiskHasRunningGame(game))
            {
                continue;
            }

            bool claimed;
            lock (_lock)
            {
                claimed = _deferred.Remove(name); // Reconcile·CheckMemory 가 함께 돌아도 지운 호출 하나만 다시 시도
            }

            if (claimed && game is not null)
            {
                TryPin(game, afterRelocation);
            }
        }
    }

    /// 설정을 다시 읽었을 때: pin 이 꺼졌거나 게임이 빠졌으면 풉니다.
    public void Reconcile(IReadOnlyList<GameSettings> games)
    {
        Volatile.Write(ref _games, games);
        RetryDeferred(games);
        long ram = _env.PhysicalRamBytes();
        foreach (string name in Held.Where(n => !games.Any(g => string.Equals(g.Name, n, StringComparison.OrdinalIgnoreCase) && !AntiCheat.ForbidsPin(g.AntiCheat) && (Wanted(g.Prewarm, ram) || (IsRecoveryPin(n) && !(g.Prewarm.PinSet && !g.Prewarm.Pin))))))
        {
            Release(name, games.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase) && AntiCheat.ForbidsPin(g.AntiCheat)) ? "anti-cheat set in settings" : "pin turned off or game removed in settings");
        }
    }

    /// 재적용 틱마다: 가용 RAM 이 그 게임의 min_free_ram_mib 아래면 풉니다.
    public void CheckMemory(IReadOnlyList<GameSettings> games)
    {
        Volatile.Write(ref _games, games);
        RetryDeferred(games);
        IReadOnlyCollection<string> held = Held;
        if (held.Count == 0)
        {
            return;
        }

        long avail = _env.AvailableRamMib();
        foreach (GameSettings g in games.Where(g => held.Contains(g.Name, StringComparer.OrdinalIgnoreCase) && avail < g.Prewarm.MinFreeRamMib))
        {
            Release(g.Name, $"low memory: available {avail} MiB < reserve {g.Prewarm.MinFreeRamMib} MiB");
        }
    }

    /// 재적용 틱마다: 회복 기간 고정을 풉니다 - 드라이브가 회복됐거나, 60 분이 지났거나, 고정한 파일의 크기·수정 시각이 바뀜.
    public void CheckRecovery()
    {
        List<KeyValuePair<string, RecoveryPin>> pins;
        lock (_lock)
        {
            pins = [.. _recovery];
        }

        DateTimeOffset now = _now();
        IReadOnlyList<GameSettings> games = Volatile.Read(ref _games);
        foreach ((string name, RecoveryPin pin) in pins)
        {
            if (Recovery?.IsRecovering(pin.Drive) != true)
            {
                Release(name, "drive recovered");
            }
            else if (now - pin.At >= RecoveryPinMax)
            {
                Release(name, "recovery pin expired");
            }
            else if (!(games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) is { } owner && DiskHasRunningGame(owner)) // 게임이 도는 동안은 고정 파일을 stat 하지 않음 (변경 점검은 게임이 끝난 뒤 다음 틱에)
                && pin.Files.FirstOrDefault(f => ReapplyChecker.Changed(f, _env.FileStamp)) is { } changed)
            {
                Release(name, $"file changed: {Path.GetFileName(changed.Path)}");
            }
        }
    }
}
