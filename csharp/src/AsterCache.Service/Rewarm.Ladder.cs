using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Windows;
using WarmBudgetInfo = (System.Collections.Generic.HashSet<string> In, long CapMib, long UsedMib, System.Collections.Generic.Dictionary<string, long> AllocMib);

namespace AsterCache.Service;

/// Rewarmer 의 사다리 (Solar) 유지: 30초 틱마다 시점이 된 게임의 캐시를 디스크 읽기 없이 새로 고치고, 서비스를 다시 시작한 뒤 표식이 없는 게임은 재예열 점검이 warm 으로 판정할 때 첫 사다리를 만듭니다.
/// 게이트는 재예열 점검과 같은 함수 (ReadBlock) 를 씁니다: 재배치 중이 아니고, 같은 디스크의 게임이 돌지 않고, 게임 종료 직후 유예가 지났고, 예산 안의 게임이고, 디스크가 바쁘지 않을 때만 만집니다.
/// 여기서는 재예열을 직접 부르지 않습니다: 디스크에서 다시 읽을지는 재예열 점검 (rewarm_minutes 주기, 표본으로 cold 확인 또는 사다리가 남긴 다시 채움 표시, 자체 게이트) 만 정하므로 HDD 를 다시 읽는 빈도는 늘지 않습니다.
/// 사다리를 새로 고치는 일 자체 (메모리 안에서만 만지기) 는 자동 모드·rewarm_minutes 와 무관하게 memory.standby_ladder 로만 켜고 끕니다.
public sealed partial class Rewarmer
{
    /// 유지 틱: 대기 목록을 한 번 읽어 (표식이 있을 때만) 새로 고칠 때가 된 게임을 순위가 낮은 것부터 (가장 최근에 예열한 게임이 마지막) 다시 만집니다.
    public void LadderTick(AutoSettings auto, CancellationToken ct = default)
    {
        Solar solar = _prewarm.Solar;
        if (!solar.Enabled || _prewarm.TriggerWaitPending()) // 이 틱은 예열 락 밖에서 도므로, 트리거가 기다리거나 읽는 중에는 같은 디스크를 만지지 않음
        {
            return;
        }

        DateTimeOffset now = _now();
        IReadOnlyList<GameSettings> due = solar.Due(Enumerable.Reverse(_prewarm.Warmed()), now); // Warmed 는 최근 순
        if (due.Count == 0)
        {
            return;
        }

        WarmBudgetInfo? budget = _prewarm.WarmBudget();
        foreach (GameSettings g in due)
        {
            if (ct.IsCancellationRequested || _prewarm.Games.Any(x => _prewarm.Relocating(x.Name)))
            {
                return;
            }

            if (_prewarm.RewarmAlreadyCovered(g) || ReadBlock(g, now, auto, budget) is not null)
            {
                continue;
            }

            Refresh(g, ct);
        }
    }

    // 새로 고치기 전에 기존 재예열 점검의 표본 (warm/cold 판정) 을 한 번 거칩니다: warm 일 때만 만집니다.
    // 시간 감시만으로는 CPU 선점을 디스크 읽기로 오판할 수 있어서이고, 이미 식었다면 표식을 지우고 재예열 점검에 맡깁니다.
    private void Refresh(GameSettings g, CancellationToken ct)
    {
        Solar solar = _prewarm.Solar;
        ProbeResult? probe = Probe(g, ct, out bool failed);
        if (ct.IsCancellationRequested)
        {
            return; // 게임 시작 등으로 취소됨: 표식은 그대로
        }

        if (probe is null || probe.Cold)
        {
            // 식었거나 표본을 읽을 수 없으면 (읽지 못한 것은 식은 것과 같이) 표식을 지우고 재예열 점검에 맡깁니다: 30 초마다 표본 읽기를 되풀이하거나 경고를 반복하지 않음.
            solar.Forget(g.Name);
            string state = probe is null ? (failed ? "the samples could not be read" : "there are no readable samples") : $"the samples are cold ({probe.Slow}/{probe.Samples} over {CacheProbe.ColdThresholdMs:F1} ms)";
            _log.Info($"{g.Name}: cache refresh skipped, {state}, left to the rewarm check");
            return;
        }

        Run(g, ct, token => solar.Refresh(g, token), "cache refresh");
    }

    // 재예열 점검이 warm 으로 판정한 게임에 표식이 없으면 (서비스를 다시 시작한 뒤) 읽은 범위 전체의 역순으로 첫 사다리를 만듭니다 (표본은 방금 점검에서 읽음).
    private void LadderUnmarked(GameSettings g, CancellationToken ct)
    {
        Solar solar = _prewarm.Solar;
        if (solar.Enabled && !solar.HasMark(g.Name) && !solar.HasMissing(g.Name) && !_prewarm.TriggerWaitPending() && _prewarm.LadderReads(_prewarm.CoveredPart(g)) is { Count: > 0 } reads)
        {
            Run(g, ct, token => solar.Order(g, reads, token), "cache ladder");
        }
    }

    // 디스크 대여 안에서 만집니다 (거절되면 만지지 않음). 실패해도 점검·틱은 계속합니다.
    private void Run(GameSettings g, CancellationToken ct, Func<CancellationToken, TouchEnd?> work, string purpose)
    {
        try
        {
            _ = _prewarm.UnderLease(g, purpose, ct, work);
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _log.Warn($"{g.Name}: {purpose} failed: {e.GetType().Name}");
        }
    }

    // WarmBudget 가 이 게임에 배정한 양 (MiB), 없으면 null: 재예열은 이만큼까지만 읽어야 계상과 실제 읽기가 같습니다.
    private static long? RoomOf(GameSettings g, WarmBudgetInfo? budget) =>
        budget is { } b && b.AllocMib.GetValueOrDefault(g.Name) is > 0 and long room ? room : null;
}
