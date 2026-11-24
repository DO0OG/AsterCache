using AsterCache.Core.Logging;
using AsterCache.Core.Settings;
using AsterCache.Windows;
using WarmBudgetInfo = (System.Collections.Generic.HashSet<string> In, long CapMib, long UsedMib, System.Collections.Generic.Dictionary<string, long> AllocMib);

namespace AsterCache.Service;

/// Rewarmer 의 사다리 (Solar) 유지: 30초 틱마다 표식이 있는 게임 중 하나라도 시점이 되면 묶음 전체의 캐시를 디스크 읽기 없이 새로 고치고, 서비스를 다시 시작한 뒤 표식이 없는 게임은 서비스 시작 예열이 이미 warm 이라 건너뛴 직후나 재예열 점검이 warm 으로 판정할 때 첫 사다리를 만듭니다.
/// 게이트는 재예열 점검과 같은 함수 (ReadBlock) 를 쓰되 다른 프로세스의 드라이브 사용량은 보지 않습니다 (만지기는 디스크를 읽지 않으므로): 재배치 중이 아니고, 같은 디스크의 게임이 돌지 않고, 게임 종료 직후 유예가 지났고, 예산 안의 게임일 때만 만집니다.
/// 여기서는 재예열을 직접 부르지 않습니다: 디스크에서 다시 읽을지는 재예열 점검 (rewarm_minutes 주기, 표본으로 cold 확인 또는 사다리가 남긴 다시 채움 표시, 자체 게이트) 만 정하므로 HDD 를 다시 읽는 빈도는 늘지 않습니다.
/// 사다리를 새로 고치는 일 자체 (메모리 안에서만 만지기) 는 자동 모드·rewarm_minutes 와 무관하게 memory.standby_ladder 로만 켜고 끕니다.
public sealed partial class Rewarmer
{
    private readonly object _groupLock = new(); // 묶음 새로 고침 (유지 틱) 과 스트림의 보호 (스트림 스레드: 앞·도중·사다리 직전) 가 같은 게임을 동시에 만지지 않게, 기다리지 않고 못 잡으면 그 회는 건너뜀

    /// 유지 틱: 대기 목록을 한 번 읽어 (표식이 있을 때만) 표식이 있는 게임 중 하나라도 새로 고칠 때가 됐으면 묶음 전체를 순위가 낮은 것부터 (가장 최근에 예열한 게임이 마지막) 다시 만집니다.
    public void LadderTick(AutoSettings auto, CancellationToken ct = default)
    {
        Solar solar = _prewarm.Solar;
        if (!solar.Enabled || _prewarm.TriggerWaitPending()) // 이 틱은 예열 락 밖에서 도므로, 트리거가 기다리거나 읽는 중에는 같은 디스크를 만지지 않음
        {
            return;
        }

        using IDisposable scope = DiskReadLedger.Scope(ReadPurpose.Idle); // 유지 틱의 만지기와 표본은 계기 없는 읽기: 디스크로 읽힌 양을 하루 읽기 집계에 셈 (만지기는 메모리 안이라 상한으로 막지 않음)
        IReadOnlyList<GameSettings> group = DueGroup(Enumerable.Reverse(_prewarm.Warmed()), auto, ct); // Warmed 는 플레이 가능성이 높은 순 (예산 순위가 없으면 최근 예열 순)
        if (group.Count > 0)
        {
            _ = RefreshGroup(group, auto, ct, _prewarm.TriggerWaitPending);
            return;
        }

        // 평소의 상한 (IdleTouchShare) 과 때가 아님으로 쉬는 중에도 밀려날 위험이 가까우면 (재사용 누계가 앞에 있던 양의 절반) 더 높은 상한 (AtRiskTouchShare) 안에서 가장 중요한 범위부터 만짐
        IReadOnlyList<(GameSettings Game, long Limit)> atRisk = AtRiskGroup(Enumerable.Reverse(_prewarm.Warmed()), auto, ct);
        if (atRisk.Count > 0)
        {
            _ = RefreshGroup([.. atRisk.Select(a => a.Game)], auto, ct, _prewarm.TriggerWaitPending, limits: atRisk.ToDictionary(a => a.Game.Name, a => a.Limit, StringComparer.OrdinalIgnoreCase));
        }
    }

    /// order (순위가 낮은 것부터) 중 밀려날 위험이 가까워 지금 상한 안에서 만질 게임과 게임마다 만질 양 (Solar.AtRiskPlan): 디스크 읽기를 일으키지 않는 만지기라 다른 프로세스의 드라이브 사용량 (바쁨·상태 미확인) 에 막히지 않고, 게임 실행·종료 직후·예산 게이트는 DueGroup 과 같습니다.
    internal IReadOnlyList<(GameSettings Game, long Limit)> AtRiskGroup(IEnumerable<GameSettings> order, AutoSettings auto, CancellationToken ct)
    {
        DateTimeOffset now = _now();
        Lazy<WarmBudgetInfo?> budget = new(_prewarm.WarmBudget);
        return _prewarm.Solar.AtRiskPlan(order, now, g => Touchable(g, now, auto, budget.Value, ct));
    }

    /// order (순위가 낮은 것부터) 중 지금 새로 고칠 묶음 (Solar.DueGroup, 만질 수 있는 게임 하나라도 때가 됐고 직전 묶음 새로 고침 뒤 60초가 지났을 때): 유지 틱과 스트림 도중의 보호가 같은 판정을 씁니다.
    internal IReadOnlyList<GameSettings> DueGroup(IEnumerable<GameSettings> order, AutoSettings auto, CancellationToken ct, bool capped = true)
    {
        DateTimeOffset now = _now();
        Lazy<WarmBudgetInfo?> budget = new(_prewarm.WarmBudget); // 표식이 있고 때가 된 게임이 있을 때만 계산
        return _prewarm.Solar.DueGroup(order, now, g => Touchable(g, now, auto, budget.Value, ct), capped); // capped: 게임이 없을 때의 만지기 시간 상한 (유지 틱만 받음, 스트림 도중의 보호는 받지 않음)
    }

    // 지금 이 게임을 만져도 되는가 (재예열 점검과 같은 게이트, 단 드라이브 사용량은 제외): 재배치 중이 아니고, 방금 예열하지 않았고, ReadBlock 에 막히지 않고, 읽기 대여를 얻을 수 있음.
    // 대여는 게임 실행으로 양보 중일 때만 거절되고 (DiskReadCoordinator) 다른 프로세스의 드라이브 사용은 보지 않습니다. 대여 거절은 표본을 읽지 못한 것이 아니라 지금은 만질 수 없다는 뜻이라, 표식을 지우지 않고 건너뜁니다 (다음 틱에 다시).
    private bool Touchable(GameSettings g, DateTimeOffset now, AutoSettings auto, WarmBudgetInfo? budget, CancellationToken ct) =>
        !_prewarm.Games.Any(x => _prewarm.Relocating(x.Name)) && !_prewarm.RewarmAlreadyCovered(g) && ReadBlock(g, now, auto, budget, ignoreDriveLoad: true) is null && _prewarm.DiskReadAllowed(g, ct);

    /// order (순위가 낮은 것부터) 의 표식이 있는 게임을 차례로 새로 고치고 (표본 점검 → warm 이면 만지기), 만진 게임들의 표식을 같은 기준으로 다시 씁니다 (Solar.Regroup).
    /// 유지 틱의 묶음 새로 고침과 스트림의 보호 (앞·도중·사다리 직전) 네 호출부가 같은 함수를 씁니다. 게이트에 막힌 게임은 건너뛰고 나머지는 계속하며, halted 가 참이거나 취소·재배치가 시작되면 거기서 멈춥니다.
    /// aheadLess: 스트림 도중의 보호가 넘기는, 진행 중인 스트림의 몫 (Solar.Regroup 참고), 그 밖에는 0.
    /// 돌려주는 값: 끝까지 만진 게임 수와 만진 바이트와 그 게임들 (만진 순서, 이름과 바이트).
    /// limits: 주면 (밀려날 위험이 가까울 때) 거기 든 게임만, 게임마다 그 양까지 가장 중요한 쪽부터 만짐.
    internal (int Games, long Bytes, IReadOnlyList<(string Game, long Bytes)> Touched) RefreshGroup(IReadOnlyList<GameSettings> order, AutoSettings auto, CancellationToken ct, Func<bool> halted, long aheadLess = 0, IReadOnlyDictionary<string, long>? limits = null)
    {
        if (!Monitor.TryEnter(_groupLock))
        {
            return (0, 0, []);
        }

        List<(string Game, long Bytes, LadderMark? Previous)> touched = [];
        try
        {
            DateTimeOffset now = _now();
            WarmBudgetInfo? budget = _prewarm.WarmBudget();
            foreach (GameSettings g in order)
            {
                if (ct.IsCancellationRequested || halted() || _prewarm.Games.Any(x => _prewarm.Relocating(x.Name)))
                {
                    break;
                }

                long limit = long.MaxValue;
                if (limits is not null && !limits.TryGetValue(g.Name, out limit))
                {
                    continue; // 상한 안에 들지 못한 덜 중요한 게임은 이번에 만지지 않음
                }

                if (Touchable(g, now, auto, budget, ct) && Refresh(g, ct, (previous, bytes) => touched.Add((g.Name, bytes, previous)), limit)
                    && auto.RewarmAfterExitMin > 0 && _prewarm.FirstPlayPriority() is { } first && string.Equals(first.Name, g.Name, StringComparison.OrdinalIgnoreCase))
                {
                    PullIn(auto, now); // 플레이 1순위 게임의 표본이 식었음: 다음 재예열 점검을 앞당겨 (기존 경로) 곧 다시 읽게 함. 사용자가 앞당김을 끈 (rewarm_after_exit_min <= 0) 설정은 따름
                }
            }
        }
        finally
        {
            _prewarm.Solar.Regroup(touched, aheadLess); // 중간에 멈췄어도 만진 것은 이미 뒤로 갔으므로 그만큼을 반영
            Monitor.Exit(_groupLock);
        }

        return (touched.Count, touched.Sum(t => t.Bytes), [.. touched.Select(t => (t.Game, t.Bytes))]);
    }

    // 새로 고치기 전에 기존 재예열 점검의 표본 (warm/cold 판정) 을 한 번 거칩니다: warm 일 때만 만집니다.
    // 시간 감시만으로는 CPU 선점을 디스크 읽기로 오판할 수 있어서이고, 이미 식었다면 표식을 지우고 재예열 점검에 맡깁니다. 돌려주는 값: 표본이 식어서 재예열 점검에 맡겼는가 (표본을 못 읽은 경우는 거짓).
    private bool Refresh(GameSettings g, CancellationToken ct, Action<LadderMark, long> touched, long byteLimit = long.MaxValue)
    {
        Solar solar = _prewarm.Solar;
        ProbeResult? probe = Probe(g, ct, out bool failed);
        if (ct.IsCancellationRequested)
        {
            return false; // 게임 시작 등으로 취소됨: 표식은 그대로
        }

        if (probe is { Cold: true } && DriveBusy(g) && solar.OverlookBusyCold(g.Name))
        {
            return false; // 다른 프로세스가 드라이브를 쓰는 중의 느린 표본은 식었다는 증거가 못 됨: 이 표식에서 처음 한 번만 표식을 지우지 않고 그 회를 건너뜀 (둘째부터는 아래처럼 재예열 점검에 맡겨 표본을 되풀이해 읽지 않음)
        }

        if (probe is { Cold: false })
        {
            solar.ClearOverlooked(g.Name);
        }

        if (probe is null || probe.Cold)
        {
            // 식었거나 표본을 읽을 수 없으면 (읽지 못한 것은 식은 것과 같이) 표식을 지우고 재예열 점검에 맡깁니다: 30 초마다 표본 읽기를 되풀이하거나 경고를 반복하지 않음.
            string since = solar.ForgetCold(g.Name);
            string state = probe is null ? (failed ? "the samples could not be read" : "there are no readable samples") : $"the samples are cold ({probe.Slow}/{probe.Samples} over {CacheProbe.ColdThresholdMs:F1} ms)";
            _log.Info($"{g.Name}: cache refresh skipped, {state}, left to the rewarm check{since}");
            return probe is { Cold: true };
        }

        Run(g, ct, token => solar.Refresh(g, token, touched, byteLimit), "cache refresh");
        return false;
    }

    // 재예열 점검이 warm 으로 판정한 게임에 표식이 없으면 (서비스를 다시 시작한 뒤) 읽은 범위 전체의 역순으로 첫 사다리를 만듭니다 (표본은 방금 점검에서 읽음).
    // auto: 주면 (재예열 점검의 첫 사다리) 새 사다리가 플레이 1순위 게임의 뒤에 서지 않게 그 게임을 한 번 더 만짐. 묶음으로 정리하는 호출 (made 가 있음) 은 주지 않음.
    private void LadderUnmarked(GameSettings g, CancellationToken ct, Action<long>? made = null, AutoSettings? auto = null)
    {
        Solar solar = _prewarm.Solar;
        if (solar.Enabled && !solar.HasMark(g.Name) && !solar.HasMissing(g.Name) && !solar.HasNothingToTouch(g.Name) && !_prewarm.TriggerWaitPending() && _prewarm.LadderReads(_prewarm.CoveredPart(g)) is { Count: > 0 } reads)
        {
            Run(g, ct, token => solar.Order(g, reads, token, made), "cache ladder");
            RetouchFirstPriority(g, auto, ct);
        }
    }

    // 방금 만든 사다리가 지킬 게임 (플레이 1순위) 의 뒤에 서면 그 게임이 먼저 밀려나므로, 1순위가 이 게임이 아니고 표식이 있으면 한 번 더 만져 가장 늦게 밀려나게 합니다 (예열 스트림 끝의 사다리와 같은 규칙: Prewarmer.LadderAfterStream).
    private void RetouchFirstPriority(GameSettings g, AutoSettings? auto, CancellationToken ct)
    {
        if (auto is null || !_prewarm.Solar.HasMark(g.Name) || _prewarm.FirstPlayPriority() is not { } first
            || string.Equals(first.Name, g.Name, StringComparison.OrdinalIgnoreCase) || !_prewarm.Solar.HasMark(first.Name))
        {
            return;
        }

        _ = RefreshGroup([first], auto, ct, _prewarm.TriggerWaitPending);
        _log.Info($"{g.Name}: ladder ordered, {first.Name} touched again after it as the first play priority");
    }

    // 드라이브가 바쁘거나 처리량을 몰라서 (Tick 의 게이트) 점검을 건너뛰는 동안에도 표식이 없는 게임의 첫 사다리는 만듦: 이 만지기는 메모리 안에서만 하고 캐시에 없는 곳에서 멈추므로 다른 프로세스의 드라이브 사용량은 보지 않습니다 (재참조가 상태 미확인 때문에 쉬지 않게).
    // 다른 게이트 (재배치·게임 실행·예산·대여) 는 사다리 새로 고침과 같이 Touchable 이 봅니다. 표본을 읽거나 디스크에서 다시 읽는 일은 하지 않음.
    private void LadderWhileBusy(GameSettings g, AutoSettings auto, DateTimeOffset now, WarmBudgetInfo? budget, CancellationToken ct)
    {
        if (!_prewarm.Solar.Enabled || !Touchable(g, now, auto, budget, ct))
        {
            return;
        }

        if (_prewarm.SupplementPending(g.Name))
        {
            LadderWaitingSupplement(g, ct, auto: auto);
        }
        else
        {
            LadderUnmarked(g, ct, auto: auto);
        }
    }

    // 보충이 메모리를 기다리는 중인 게임 (매번 한도로 끝나 읽기를 끝내는 회가 없음) 의 첫 사다리: 읽은 범위 (이어 읽을 위치까지) 의 역순으로 만듭니다. 대상 판정은 Prewarmer 가 함 (WaitingSupplementLadderReads).
    private void LadderWaitingSupplement(GameSettings g, CancellationToken ct, Action<long>? made = null, AutoSettings? auto = null)
    {
        Solar solar = _prewarm.Solar;
        if (solar.Enabled && !solar.HasMark(g.Name) && !solar.HasMissing(g.Name) && !solar.HasNothingToTouch(g.Name) && !_prewarm.TriggerWaitPending() && _prewarm.WaitingSupplementLadderReads(g) is { Count: > 0 } reads)
        {
            Run(g, ct, token => solar.Order(g, reads, token, made), "cache ladder");
            RetouchFirstPriority(g, auto, ct);
        }
    }

    /// 서비스 시작 예열이 이미 warm 이라 건너뛴 게임들 (순위가 높은 것부터) 의 첫 사다리: 표본은 그 판정을 그대로 쓰고 (다시 읽지 않음), 재예열 점검의 첫 사다리와 같은 조건·함수로 순위가 낮은 게임부터 만든 뒤 묶음 규칙으로 표식을 정리합니다.
    /// 시작 예열이 서비스를 다시 시작한 뒤 첫 재예열 점검 (rewarm_minutes 뒤) 까지 표식이 없던 공백을 메웁니다. 유지 틱의 묶음 새로 고침과 겹치면 (_groupLock) 건너뛰고 재예열 점검이 받침.
    internal void LadderAfterStartSkip(IReadOnlyList<GameSettings> skipped, AutoSettings auto, CancellationToken ct)
    {
        Solar solar = _prewarm.Solar;
        if (skipped.Count == 0 || !solar.Enabled || !Monitor.TryEnter(_groupLock))
        {
            return;
        }

        List<(string Game, long Bytes, LadderMark? Previous)> made = [];
        try
        {
            DateTimeOffset now = _now();
            WarmBudgetInfo? budget = _prewarm.WarmBudget();
            foreach (GameSettings g in Enumerable.Reverse(skipped))
            {
                if (ct.IsCancellationRequested || _prewarm.Games.Any(x => _prewarm.Relocating(x.Name)))
                {
                    break;
                }

                if (ReadBlock(g, now, auto, budget, ignoreDriveLoad: true) is not null)
                {
                    continue;
                }

                void Made(long bytes) => made.Add((g.Name, bytes, null));
                if (_prewarm.SupplementPending(g.Name))
                {
                    LadderWaitingSupplement(g, ct, Made);
                }
                else
                {
                    LadderUnmarked(g, ct, Made);
                }
            }
        }
        finally
        {
            solar.Regroup(made);
            Monitor.Exit(_groupLock);
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
