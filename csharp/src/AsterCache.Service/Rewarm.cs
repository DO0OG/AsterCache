using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 예열 유지: rewarm_minutes 마다, 예열한 게임과 같은 드라이브의 등록 게임이 하나도 안 돌고 PC 가 한가할 때 (CPU 와 그 게임 드라이브 처리량이 자동 재배치의 유휴 기준 아래), 예열한 적 있는 게임의 핫 집합 몇 곳을 읽어 보고 식었으면 (CacheProbe.Cold: 표본 8 곳 중 2 곳 이상이 CacheProbe.ColdThresholdMs 초과) 다시 예열합니다.
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

    public void Tick(AutoSettings auto, CancellationToken ct = default)
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
        IReadOnlyList<GameSettings> warmed = _prewarm.Warmed();
        if (AutoRelocator.SameDriveGames(_prewarm.Games, warmed).Any(_env.GameRunning))
        {
            _log.Info("rewarm check skipped: a game is running");
            return;
        }

        if (cpu is not < IdleTracker.CpuBusyPercent)
        {
            _log.Info("rewarm check skipped: the CPU is busy or its load is unknown");
            return;
        }

        foreach (GameSettings g in warmed)
        {
            if (ct.IsCancellationRequested)
            {
                _log.Info("rewarm check stopped: a game started");
                return;
            }

            if (_env.DiskBytesPerSecond(g) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond)
            {
                _log.Info($"{g.Name}: rewarm check skipped (the game drive is busy or its load is unknown)");
                continue;
            }

            Check(g, ct);
        }
    }

    private void Check(GameSettings g, CancellationToken ct)
    {
        ProbeResult? probe;
        try
        {
            probe = _prewarm.ColdSamples(g) is { } samples ? _env.ProbeSamples(samples) : _env.ProbeReadMs(g); // 기록 없이 예열한 게임은 그때 읽은 파일 목록에서 고른 표본
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            _log.Warn($"{g.Name}: rewarm check failed: {e.GetType().Name}");
            return;
        }

        if (probe is null)
        {
            _log.Info($"{g.Name}: rewarm check skipped (no readable samples)");
        }
        else if (!probe.Cold)
        {
            _log.Info($"{g.Name}: rewarm check: warm ({probe.Slow}/{probe.Samples} samples over {CacheProbe.ColdThresholdMs:F1} ms, average {probe.AverageMs:F2} ms)");
        }
        else
        {
            _log.Info($"{g.Name}: rewarm check: cold ({probe.Slow}/{probe.Samples} samples over {CacheProbe.ColdThresholdMs:F1} ms, average {probe.AverageMs:F2} ms), prewarming again (cooldown ignored)");
            _prewarm.RewarmGame(g, ct);
        }
    }
}
