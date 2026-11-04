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
    private readonly HashSet<string> _holdLogged = new(StringComparer.OrdinalIgnoreCase); // 런처 보류 사유는 하나뿐이라 게임당 한 번만 남김

    public PinKeeper(PrewarmEnv env, ILog log, Func<DateTimeOffset>? now = null)
    {
        _env = env;
        _log = log;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

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
            IReadOnlyList<string> paths = _env.PinPaths(game);
            Fingerprint[] files = recovering ? [.. paths.Select(_env.FileStamp)] : []; // 고정 전에 재야 그 사이의 변경도 잡음
            PinHandle handle = _env.OpenPin(paths);
            if (_env.GameRunning(game))
            {
                handle.Pin.Dispose(); // 고정하는 사이에 게임이 떴으면 바로 풂
                _log.Info($"{game.Name}: pin dropped (game started while pinning)");
                return;
            }

            lock (_lock)
            {
                _held[game.Name] = handle.Pin;
                if (recovering)
                {
                    _recovery[game.Name] = new RecoveryPin(drive, _now(), files);
                }
            }

            _log.Info(recovering ? $"{game.Name}: pinned until drive recovers ({handle.Count} file(s))" : $"{game.Name}: pinned {handle.Count} file(s), {handle.Skipped} skipped");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            _log.Warn($"{game.Name}: pin failed: {e.GetType().Name}");
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

    /// 설정을 다시 읽었을 때: pin 이 꺼졌거나 게임이 빠졌으면 풉니다.
    public void Reconcile(IReadOnlyList<GameSettings> games)
    {
        long ram = _env.PhysicalRamBytes();
        foreach (string name in Held.Where(n => !games.Any(g => string.Equals(g.Name, n, StringComparison.OrdinalIgnoreCase) && !AntiCheat.ForbidsPin(g.AntiCheat) && (Wanted(g.Prewarm, ram) || (IsRecoveryPin(n) && !(g.Prewarm.PinSet && !g.Prewarm.Pin))))))
        {
            Release(name, games.Any(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase) && AntiCheat.ForbidsPin(g.AntiCheat)) ? "anti-cheat set in settings" : "pin turned off or game removed in settings");
        }
    }

    /// 재적용 틱마다: 가용 RAM 이 그 게임의 min_free_ram_mib 아래면 풉니다.
    public void CheckMemory(IReadOnlyList<GameSettings> games)
    {
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
            else if (pin.Files.FirstOrDefault(f => ReapplyChecker.Changed(f, _env.FileStamp)) is { } changed)
            {
                Release(name, $"file changed: {Path.GetFileName(changed.Path)}");
            }
        }
    }
}
