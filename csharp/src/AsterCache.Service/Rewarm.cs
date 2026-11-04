using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 예열 유지: rewarm_minutes 마다, 예열한 게임과 같은 드라이브의 등록 게임이 하나도 안 돌고 그 게임 드라이브 처리량이 자동 재배치의 유휴 기준 아래일 때 (CPU 부하는 보지 않음), 예열한 적 있는 게임의 핫 집합 몇 곳을 읽어 보고 식었으면 (CacheProbe.Cold: 표본 8 곳 중 2 곳 이상이 CacheProbe.ColdThresholdMs 초과) 다시 예열합니다.
public sealed class Rewarmer
{
    private readonly Prewarmer _prewarm;
    private readonly PrewarmEnv _env;
    private readonly ILog _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _nextLock = new(); // Tick (유지 점검 스레드) 와 PullIn (재적용 틱 스레드) 이 같이 쓰는 _next 보호
    private DateTimeOffset? _next;
    private readonly Dictionary<string, string> _checkState = new(StringComparer.OrdinalIgnoreCase); // 게임별 마지막 재확인 결과 (로그를 바뀔 때만 남김)

    public Rewarmer(Prewarmer prewarm, PrewarmEnv env, ILog log, Func<DateTimeOffset> now)
    {
        _prewarm = prewarm;
        _env = env;
        _log = log;
        _now = now;
    }

    /// 게임이 끝났을 때: 다음 점검을 auto.rewarm_after_exit_min 뒤로 당깁니다 (이미 더 이르면 그대로). 게이트 (게임 실행 중·디스크 바쁨) 와 식었을 때만 읽는 점검은 Tick 이 그대로 적용합니다.
    public void PullIn(AutoSettings auto)
    {
        if (!auto.Enabled || auto.RewarmMinutes <= 0 || auto.RewarmAfterExitMin <= 0)
        {
            return;
        }

        DateTimeOffset due = _now() + TimeSpan.FromMinutes(auto.RewarmAfterExitMin);
        lock (_nextLock)
        {
            if (_next is not { } next || due < next)
            {
                _next = due;
            }
        }
    }

    public void Tick(AutoSettings auto, CancellationToken ct = default)
    {
        if (!auto.Enabled || auto.RewarmMinutes <= 0)
        {
            lock (_nextLock)
            {
                _next = null;
            }

            return;
        }

        _env.CpuPercent(); // 틱마다 불러 두 표본 사이가 한 틱이 되게 함 (재예열 판정에는 쓰지 않음)

        DateTimeOffset now = _now();
        lock (_nextLock)
        {
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
        }

        IReadOnlyList<GameSettings> warmed = _prewarm.Warmed();
        if (AutoRelocator.SameDriveGames(_prewarm.Games, warmed, _env.SameDisk).Any(_env.GameRunning))
        {
            _log.Info("rewarm check skipped: a game is running");
            return;
        }

        if (_prewarm.Games.Any(g => _prewarm.Relocating(g.Name)))
        {
            _log.Info("rewarm check skipped: a relocation is running");
            return;
        }

        foreach (GameSettings g in warmed)
        {
            if (ct.IsCancellationRequested)
            {
                _log.Info("rewarm check stopped: a game started");
                return;
            }

            if (_prewarm.Games.Any(x => _prewarm.Relocating(x.Name)))
            {
                _log.Info("rewarm check stopped: a relocation started"); // 점검 주기는 이미 넘어가 있어 다음 주기에 다시 봄
                return;
            }

            if (_env.DiskBytesPerSecond(g) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond)
            {
                NoteCheck(g, "busy", $"{g.Name}: rewarm check skipped (the game drive is busy or its load is unknown)");
                continue;
            }

            Check(g, ct);
        }
    }

    /// 결과(상태)가 바뀔 때만 남김: 틱마다 같은 결과를 반복해 쓰지 않음 (예열 동작은 그대로).
    private void NoteCheck(GameSettings g, string state, string message)
    {
        if (!_checkState.TryGetValue(g.Name, out string? last) || last != state)
        {
            _checkState[g.Name] = state;
            _log.Info(message);
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
            NoteCheck(g, "nosamples", $"{g.Name}: rewarm check skipped (no readable samples)");
        }
        else if (!probe.Cold && _prewarm.SupplementPending(g.Name))
        {
            NoteCheck(g, "supplement", $"{g.Name}: rewarm check: samples are warm but the rest of the hot set is not read yet, supplementing once the drive has stayed idle"); // 부분 예열 뒤에는 표본이 따뜻해도 건너뛰지 않음, 보충은 SupplementTick 과 같은 연속 유휴 판정을 거침
            _prewarm.SupplementWhenIdle(g, ct);
        }
        else if (!probe.Cold)
        {
            NoteCheck(g, "warm", $"{g.Name}: rewarm check: warm ({probe.Slow}/{probe.Samples} samples over {CacheProbe.ColdThresholdMs:F1} ms, average {probe.AverageMs:F2} ms)");
        }
        else
        {
            NoteCheck(g, "cold", $"{g.Name}: rewarm check: cold ({probe.Slow}/{probe.Samples} samples over {CacheProbe.ColdThresholdMs:F1} ms, average {probe.AverageMs:F2} ms), prewarming again (cooldown ignored)");
            _prewarm.RewarmGame(g, ct);
        }
    }
}
