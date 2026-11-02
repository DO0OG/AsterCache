using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 예열 유지: rewarm_minutes 마다, 등록 게임이 하나도 안 돌고 PC 가 한가할 때 (CPU 와 그 게임 드라이브 처리량이 자동 재배치의 유휴 기준 아래), 예열한 적 있는 게임의 핫 집합 몇 곳을 읽어 보고 식었으면 (평균 > CacheProbe.ColdThresholdMs) 다시 예열합니다.
public sealed class Rewarmer
{
    private readonly Prewarmer _prewarm;
    private readonly PrewarmEnv _env;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _now;
    private DateTimeOffset? _next;

    public Rewarmer(Prewarmer prewarm, PrewarmEnv env, ILog log, Func<DateTimeOffset> now)
    {
        _prewarm = prewarm;
        _env = env;
        _log = log;
        _now = now;
    }

    public void Tick(AutoSettings auto)
    {
        if (!auto.Enabled || auto.RewarmMinutes <= 0)
        {
            _next = null;
            return;
        }

        double? cpu = _env.CpuPercent(); // 틱마다 불러 두 표본 사이가 한 틱이 되게 함

        DateTimeOffset now = _now();
        if (_next is null)
        {
            _next = now + TimeSpan.FromMinutes(auto.RewarmMinutes); // 켜진 때부터 한 주기 뒤에 처음 점검
            return;
        }

        if (now < _next)
        {
            return;
        }

        _next = now + TimeSpan.FromMinutes(auto.RewarmMinutes);
        if (_prewarm.Games.Any(_env.GameRunning))
        {
            _log.Info("rewarm check skipped: a game is running");
            return;
        }

        if (cpu is not < IdleTracker.CpuBusyPercent)
        {
            _log.Info("rewarm check skipped: the CPU is busy or its load is unknown");
            return;
        }

        foreach (GameSettings g in _prewarm.Warmed())
        {
            if (_env.DiskBytesPerSecond(g) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond)
            {
                _log.Info($"{g.Name}: rewarm check skipped (the game drive is busy or its load is unknown)");
                continue;
            }

            Check(g);
        }
    }

    private void Check(GameSettings g)
    {
        double? ms;
        try
        {
            ms = _env.ProbeReadMs(g);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            _log.Warn($"{g.Name}: rewarm check failed: {e.GetType().Name}");
            return;
        }

        if (ms is not { } avg)
        {
            _log.Info($"{g.Name}: rewarm check skipped (no readable samples)");
        }
        else if (avg <= CacheProbe.ColdThresholdMs)
        {
            _log.Info($"{g.Name}: rewarm check: warm ({avg:F2} ms per MiB)");
        }
        else
        {
            _log.Info($"{g.Name}: rewarm check: cold ({avg:F2} ms per MiB > {CacheProbe.ColdThresholdMs:F1}), prewarming again (cooldown ignored)");
            _prewarm.RewarmGame(g);
        }
    }
}
