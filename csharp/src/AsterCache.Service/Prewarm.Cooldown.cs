using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

/// Prewarmer 의 쿨다운 판정: 쿨다운 안이어도 표본으로 파일 캐시가 식었음이 확인되면 적용하지 않습니다 (재부팅으로 캐시가 비었는데 부팅 전 예열이 쿨다운에 세지 않게).
public sealed partial class Prewarmer
{
    // 같은 흐름 (서비스 시작 분류 → 묶음 순위 → 앞부분 → 본 예열) 이 표본을 다시 읽지 않고 쓰는 시간.
    private static readonly TimeSpan CooldownProbeReuse = TimeSpan.FromMinutes(5);

    // Prev, Generation: 표본을 읽기 전의 마지막 예열 시각과 계획·trace 세대. 그 사이 예열이 끝났거나 계획이 바뀌면 옛 표본은 쓰지 않습니다.
    private sealed record CooldownProbe(bool? Warm, DateTimeOffset At, DateTimeOffset? Prev, ProbeGeneration Generation);

    private readonly Dictionary<string, CooldownProbe> _cooldownProbes = new(StringComparer.OrdinalIgnoreCase); // _gate 로 보호

    /// 쿨다운 판정에 쓸 마지막 예열 뒤 경과 시간 (예열한 적이 없으면 null: 쿨다운 없음).
    /// 쿨다운 안일 때만 AlreadyWarm 표본 (대여 안에서만 읽음) 으로 확인해, 식었음 (false) 이면 null 을 돌려줍니다. 따뜻하거나 확인 불가 (null) 면 경과 시간 그대로라 쿨다운이 적용됩니다.
    /// logWhy 를 주면 쿨다운을 풀 때 한 줄 남김 (실제로 예열하는 호출만).
    private TimeSpan? CooldownSince(GameSettings g, DateTimeOffset now, string? logWhy = null)
    {
        if (LastWarmed(g.Name) is not { } prev)
        {
            return null;
        }

        TimeSpan since = now - prev;
        if (since >= TimeSpan.FromMinutes(g.Prewarm.CooldownMin) || IsCold(g) || _env.ZoneBytes(g) is not > 0 || !_env.TraceExists(g) || DriveHasRunningGame(g))
        {
            return since; // 쿨다운 밖, 기록 없는 게임 (표본 없음), 어차피 다른 사유로 건너뜀, 게임 실행 중 (같은 디스크 읽기 금지): 표본을 읽지 않음
        }

        if (!TryGetCooldownProbe(g, prev, now, out bool? warm))
        {
            // 메모리가 모자라 앞부분만 읽은 게임은 읽은 앞부분에서만 표본을 고름 (전체 목록에서 고르면 읽지 않은 뒤쪽 때문에 늘 식음). 재예열 점검 (ProbeFront) 과 같은 기준.
            warm = MemoryPartialPending(g.Name) ? SampleAndNote(g, () => SampleFront(g)) : AlreadyWarm(g); // AlreadyWarm 은 결과를 같은 표에 기록
        }

        if (warm is not false)
        {
            return since;
        }

        if (logWhy is not null)
        {
            _log.Info($"{g.Name}: cooldown ignored, the cache is cold [{logWhy}]");
        }

        return null;
    }

    private bool TryGetCooldownProbe(GameSettings g, DateTimeOffset prev, DateTimeOffset now, out bool? warm)
    {
        CooldownProbe? probe;
        lock (_gate)
        {
            _cooldownProbes.TryGetValue(g.Name, out probe);
        }

        TimeSpan age = probe is null ? TimeSpan.Zero : now - probe.At;
        if (probe is not null && probe.Prev == prev && age >= TimeSpan.Zero && age < CooldownProbeReuse && ProbeGenerationOf(g) == probe.Generation)
        {
            warm = probe.Warm;
            return true;
        }

        warm = null;
        return false;
    }

    // 표본을 읽고 쿨다운 판정용으로 기록합니다. 마지막 예열 시각과 세대는 읽기 전에 잡고, 읽는 사이 예열이 끝났으면 기록하지 않음.
    private bool? SampleAndNote(GameSettings g, Func<bool?> sample)
    {
        DateTimeOffset? prev = LastWarmed(g.Name);
        ProbeGeneration? generation = ProbeGenerationOf(g);
        bool? warm = sample();
        if (generation is not null && LastWarmed(g.Name) == prev)
        {
            lock (_gate)
            {
                _cooldownProbes[g.Name] = new CooldownProbe(warm, _now(), prev, generation);
            }
        }

        return warm;
    }

    // 부분 예열 (메모리 부족) 뒤의 표본: 읽은 앞부분에서만. 읽을 수 없으면 null.
    private bool? SampleFront(GameSettings g)
    {
        try
        {
            return ProbeFront(g, purpose: "already warm") is { Samples: > 0 } p ? !p.Cold : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlanRejectedException or PyValueException or Win32Exception)
        {
            return null;
        }
    }
}
