using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// 예열한 게임의 파일을 읽기 전용 매핑으로 들고 있는 곳 (게임 프로세스는 건드리지 않음). prewarm.pin 이 켜져 있고 물리 RAM 이 16 GiB 이상일 때만 고정합니다.
/// 런처가 떠 있는 동안은 고정하지 않습니다. 풀리는 때: 그 게임 exe 시작, 런처 시작, 가용 RAM 이 min_free_ram_mib 아래, 설정에서 pin 이 꺼짐·게임이 빠짐, 서비스 종료.
public sealed class PinKeeper
{
    public const long MinPhysicalRamBytes = 16L << 30;

    private readonly PrewarmEnv _env;
    private readonly ILog _log;
    private readonly object _lock = new();
    private readonly Dictionary<string, IDisposable> _held = new(StringComparer.OrdinalIgnoreCase);

    public PinKeeper(PrewarmEnv env, ILog log)
    {
        _env = env;
        _log = log;
    }

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

    /// 예열 성공 직후: 조건이 맞으면 그 게임의 존 파일을 고정합니다.
    public void TryPin(GameSettings game)
    {
        if (!game.Prewarm.Pin)
        {
            return;
        }

        long ram = _env.PhysicalRamBytes();
        if (ram < MinPhysicalRamBytes)
        {
            _log.Info($"{game.Name}: pin skipped (physical RAM {ram >> 30} GiB is below 16 GiB)");
            return;
        }

        if (_env.LauncherRunning())
        {
            _log.Info($"{game.Name}: pin held back (a launcher is running; its updates may change the files)");
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
            PinHandle handle = _env.OpenPin(_env.PinPaths(game));
            if (_env.GameRunning(game))
            {
                handle.Pin.Dispose(); // 고정하는 사이에 게임이 떴으면 바로 풂
                _log.Info($"{game.Name}: pin dropped (game started while pinning)");
                return;
            }

            lock (_lock)
            {
                _held[game.Name] = handle.Pin;
            }

            _log.Info($"{game.Name}: pinned {handle.Count} file(s), {handle.Skipped} skipped");
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
        }

        if (pin is not null)
        {
            pin.Dispose();
            _log.Info($"{game}: pin released ({why})");
        }
    }

    public void ReleaseAll(string why)
    {
        foreach (string name in Held)
        {
            Release(name, why);
        }
    }

    /// 설정을 다시 읽었을 때: pin 이 꺼졌거나 게임이 빠졌으면 풉니다.
    public void Reconcile(IReadOnlyList<GameSettings> games)
    {
        foreach (string name in Held.Where(n => !games.Any(g => string.Equals(g.Name, n, StringComparison.OrdinalIgnoreCase) && g.Prewarm.Pin)))
        {
            Release(name, "pin turned off or game removed in settings");
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
}
