using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;

namespace AsterCache.Service;

/// AutoRelocator 의 존 확장과 가변 파일·프로필 보충 재계획, 존 크기와 예산 계산.
public sealed partial class AutoRelocator
{
    private const string WriteLimitDetail = "write limit reached, will continue";

    // 확장 중인가: 확장 재계획이 이전 계획을 .extend 로 치운 뒤 끝나거나 막히기 전까지 (파일이라 서비스를 다시 시작해도 이어짐).
    private bool IsExtending(GameSettings g) => _env.FileExists(g.Plan + ExtendSuffix);

    // 프로필 보충 재계획인가: 이전 계획 (.extend) 에 표지가 있음. 보충은 이미 옮긴 존을 지키려는 것이라 뒤 공간이 쓰이면 새 존으로 가지 않고 보류합니다.
    private bool IsSupplementExtension(GameSettings g)
    {
        try
        {
            return IsExtending(g) && _env.LoadPlan(g.Plan + ExtendSuffix).Stats.TryGetValue(SupplementReplanKey, out object? marked) && marked is 1L;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return false;
        }
    }

    // 존 확장 재계획: 배치까지 끝낸 게임의 병합 트레이스 (플레이를 더 모아 합집합이 된 trace.json) 가 계획보다 새롭고, 핫 집합이 TraceMergeMinMib 이상 커졌으면
    // 이전 계획을 .extend 로 치우고 처음 단계로 되돌립니다. 이후는 일반 흐름 (계획 → 재배치): 같은 존 시작에 맞춰 계획하므로 이미 존에 있는 블록은 제자리이고 새 블록만 존 뒤에 붙으며,
    // 유휴·일일 상한·바이트 예산·회복·전원·같은 디스크 게임 같은 안전장치를 그대로 지납니다. 존 뒤가 비어 있지 않은 등 확장할 수 없으면 BlockExtension 이 이전 계획을 되살립니다.
    private void ReplanExtended(AsterSettings s)
    {
        foreach (GameSettings g in s.Games)
        {
            if (!HeavyWorkAllowed(g) || !GamePolicy.AllowsRelocate(g)) // 확장 재계획은 다시 재배치로 이어지므로 재배치를 막은 게임은 시작하지 않음
            {
                continue;
            }

            string raw = AutoTracer.RawTracePath(g);
            bool relocated = _status.Get(g.Name)?.RelocatedAt is not null;
            if (!_env.FileExists(g.Plan) || IsExtending(g) || AnyGameRunning(g)
                || _env.FileWriteTime(raw) is not { } traced || _env.FileWriteTime(g.Plan) is not { } planned)
            {
                continue;
            }

            if (_warmRestorePending.Contains(g.Name))
            {
                RetryWarmRestore(g); // 계획이 트레이스보다 새로워져 아래 점검이 조기 종료해도 목록 복원은 다시 시도
            }

            // 새 트레이스: 계획보다 새롭고 아직 따져 보지 않았고 두 번째 이후 플레이가 병합된 것. 존 상한 설정이 바뀌었거나 처음 보는 게임도 한 번 따져 봄 (상한을 올려도 트레이스는 안 바뀌므로).
            bool newTrace = traced > planned && !(_extTried.TryGetValue(g.Name, out DateTimeOffset seen) && seen >= traced) && AutoTracer.RunCount(g, _env.FileExists) >= 2;
            (bool, int) limitKey = (s.MaxZoneAuto, s.MaxZoneMib);
            bool limitDue = !_limitSeen.TryGetValue(g.Name, out (bool, int) seenKey) || seenKey != limitKey;
            if (!newTrace && !limitDue)
            {
                continue;
            }

            _limitSeen[g.Name] = limitKey;
            if (newTrace)
            {
                _extTried[g.Name] = traced;
            }

            try
            {
                PlanFile plan = _env.LoadPlan(g.Plan);
                if (plan.Zone.StartLcn is null || IsBootPlan(plan) || !plan.Stats.TryGetValue("hot_blocks", out object? hb) || hb is not long hotBlocks || !_env.DirectoryExists(plan.OriginRoot))
                {
                    continue; // boot 계획은 첫 기록 재계획 (ReplanChanged) 의 몫
                }

                long limit = MaxZoneMib(s, g);
                bool metaSame = plan.Stats.TryGetValue(EffectiveZoneKey, out object? ez) && ez is long zoneMib && zoneMib == limit
                    && plan.Stats.TryGetValue(MergePolicyKey, out object? mp) && mp as string == AutoTracer.MergePolicy;
                if (!newTrace && metaSame)
                {
                    continue; // 상한·병합 정책이 계획을 만들 때와 같음: 반복하지 않음
                }

                string? stored = plan.Stats.TryGetValue(FingerprintKey, out object? fo) ? fo as string : null;
                string? storedStable = plan.Stats.TryGetValue(StableFingerprintKey, out object? sf) ? sf as string : null;
                string? rawFingerprint = _env.TraceFingerprint(raw);
                IReadOnlyList<TraceEvent> mergedTrace = _env.LoadTrace(raw, g.OriginRoot);
                (IReadOnlyList<TraceEvent> stableTrace, JObject excluded, IReadOnlyList<TraceEvent> warmOnly) = DropVolatile(g, mergedTrace);
                string fingerprint = PlanFile.TraceFingerprint(stableTrace);
                long cap = limit << 20;
                long merged = Math.Min(DistinctBlocks(stableTrace) * HotSet.Block, cap);
                long grew = merged - hotBlocks * HotSet.Block;
                bool volatileHistory = excluded.Count > 0 || plan.Stats.ContainsKey(VolatileFiles.StatsKey);
                bool full = hotBlocks * HotSet.Block >= cap; // 존 상한까지 찬 계획: 새 트레이스는 순서만 바꿀 뿐 더 담지 못하고, 확장하면 이미 옮긴 배치를 깨뜨림 (예열 목록은 아래 refreshWarm 이 계속 갱신)
                bool reordered = newTrace && !full && (volatileHistory && storedStable is not null
                    ? fingerprint != storedStable // 가변 파일은 안정 지문 비교에서 제외
                    : stored is not null && rawFingerprint is not null && rawFingerprint != stored); // 안정 지문이 없는 옛 계획은 원시 지문으로 비교한 뒤 새 지문을 기록
                bool shrink = hotBlocks * HotSet.Block > cap; // 상한을 낮춰 계획의 핫셋이 새 상한을 넘음: 메타만 적지 않고 같은 존 시작으로 줄여 다시 계획
                bool replan = grew >= (long)s.Auto.TraceMergeMinMib << 20 || reordered || shrink;
                bool stamp = !metaSame || stored is null && rawFingerprint is not null || relocated && storedStable is null;
                bool refreshWarm = relocated && newTrace && !replan;

                if (!relocated)
                {
                    using IDisposable? warmGate = _env.Lock(g);
                    if (warmGate is null || !PlanUnchanged(g, plan))
                    {
                        _ = _extTried.Remove(g.Name);
                        _ = _limitSeen.Remove(g.Name);
                        continue;
                    }

                    long refreshedMib = SavePartialWarm(g, limit, stableTrace, warmOnly);
                    _log.Info($"{g.Name}: partial plan prewarm trace refreshed ({refreshedMib} MiB hot set, zone limit {limit} MiB); relocation plan and journal kept");
                    continue;
                }

                using IDisposable? gate = replan || stamp || refreshWarm ? _env.Lock(g) : null; // 계획을 치우거나 다시 쓰는 구간
                if ((replan || stamp || refreshWarm) && (gate is null || !PlanUnchanged(g, plan)))
                {
                    _ = _extTried.Remove(g.Name); // 못 잡았거나 기다리는 동안 계획이 바뀜: 아무것도 바꾸지 않고 다음 틱에 다시 따짐
                    _ = _limitSeen.Remove(g.Name);
                    continue;
                }

                if (replan)
                {
                    _tier?.RevertGame(g.Name, "trace grew"); // 링크는 새 재배치 전에 되돌림
                    _ = _env.ArchiveJournal(g); // 새 계획은 다른 저널 header 를 쓰므로 끝난 저널은 옆으로 보관
                    _env.MoveAside(g.Plan, g.Plan + ExtendSuffix);
                    _status.ResetForReplan(g.Name, ExtendDetail);
                    _log.Info(shrink
                        ? $"{g.Name}: plan holds {hotBlocks * HotSet.Block >> 20} MiB, above the zone limit {limit} MiB, plan set aside as {ExtendSuffix}, will plan again within the limit (same zone)"
                        : grew >= (long)s.Auto.TraceMergeMinMib << 20
                        ? $"{g.Name}: merged trace holds {grew >> 20} MiB more than the plan (run {AutoTracer.RunCount(g, _env.FileExists)}, zone limit {limit} MiB), plan set aside as {ExtendSuffix}, will extend the zone"
                        : $"{g.Name}: merged trace blocks or order changed since the plan (run {AutoTracer.RunCount(g, _env.FileExists)}), plan set aside as {ExtendSuffix}, will extend the zone");
                    continue;
                }

                // 다시 계획할 일은 아님: 옛 계획 (메타 없음) 이거나 상한이 바뀌었으면 지금 값을 계획에 적어 둠 (지문은 없을 때만: 옛 계획은 처음 한 번 기록만 하고 재계획하지 않음)
                if (stamp)
                {
                    plan.Stats[EffectiveZoneKey] = limit;
                    plan.Stats[MergePolicyKey] = AutoTracer.MergePolicy;
                    if (stored is null && rawFingerprint is not null)
                    {
                        plan.Stats[FingerprintKey] = rawFingerprint;
                    }

                    if (storedStable is null)
                    {
                        plan.Stats[StableFingerprintKey] = fingerprint;
                    }

                    plan.Save(g.Plan);
                    _log.Info($"{g.Name}: plan notes recorded (zone limit {limit} MiB, merge {AutoTracer.MergePolicy}), no replan needed");
                }

                if (refreshWarm)
                {
                    try
                    {
                        List<TraceEvent> hot = plan.PrefetchTrace([stableTrace]);
                        PlanOutput.SavePrefetch(g.Trace, hot, full ? WithZoneOverflow(g, hot, stableTrace, warmOnly) : warmOnly);
                        _log.Info($"{g.Name}: prewarm trace refreshed from the merged trace; relocation plan and journal kept");
                    }
                    catch (Exception e) when (IsTransient(e))
                    {
                        // 이 트레이스는 이미 따져 본 것으로 기록됐으므로, 복원 대기에 넣어 다음 틱부터 같은 병합 트레이스로 다시 씀
                        _ = _warmRestorePending.Add(g.Name);
                        _log.Warn($"{g.Name}: prewarm trace not refreshed ({e.GetType().Name}), will retry");
                    }
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                if (!relocated)
                {
                    _ = _extTried.Remove(g.Name);
                    _ = _limitSeen.Remove(g.Name);
                }

                _log.Warn($"{g.Name}: zone extension check failed, will retry on the next trace ({e.GetType().Name})");
            }
        }
    }

    // 존이 상한까지 찬 계획은 새 블록을 담지 못하므로, 존 밖 블록을 예열 전용 목록에 더함 (목록 전체가 캐시 예산 안일 때만, 재배치·계획 파일은 그대로).
    private IReadOnlyList<TraceEvent> WithZoneOverflow(GameSettings g, IReadOnlyList<TraceEvent> hot, IReadOnlyList<TraceEvent> stable, IReadOnlyList<TraceEvent> warmOnly)
    {
        long capMib = Prewarmer.CacheCapMib(_env.PhysicalRamBytes(), _settings().Games);
        long capBytes = capMib >= long.MaxValue >> 20 ? long.MaxValue : capMib << 20; // 물리 RAM 을 모르면 제한 없음
        List<TraceEvent> extra = WarmOverflow.Select(hot, stable, hot.Sum(r => r.Length) + warmOnly.Sum(r => r.Length), capBytes);
        if (extra.Count == 0)
        {
            return warmOnly;
        }

        _log.Info($"{g.Name}: prewarm list gained {extra.Sum(r => r.Length) / (double)(1 << 20):F1} MiB of blocks outside the full zone");
        return [.. warmOnly, .. extra];
    }

    // 지금 규칙으로 가변 파일인 파일이 핫 순서에 든 계획 (현재 규칙 버전 표지가 없는 것만: 규칙을 넓히기 전에 만든 계획을 한 번 정리).
    private static bool HoldsVolatile(PlanFile plan) =>
        !(plan.Stats.TryGetValue(VolatileFiles.RuleKey, out object? v) && v is long version && version >= VolatileFiles.RuleVersion) && plan.Order.Any(o => VolatileFiles.Ignored(o.Path));

    // 가변 파일을 빼기 전에 만든 옛 계획 (규칙 버전 표지가 없고 핫 순서에 가변 파일이 듦): 게임이 그 파일을 다시 써서 존이 흐트러져 보이므로
    // 서비스 실행당 한 번 가변 파일을 뺀 계획으로 다시 만듭니다. 확장 재계획과 같은 길 (.extend): 같은 존 시작에 맞추고, 유휴·일일 상한·바이트 예산 같은 재배치 게이트를 그대로 지납니다.
    private void ReplanLegacyVolatile(AsterSettings s)
    {
        foreach (GameSettings g in s.Games)
        {
            if (!HeavyWorkAllowed(g) || !GamePolicy.AllowsRelocate(g) || _status.Get(g.Name)?.RelocatedAt is null || !_env.FileExists(g.Plan) || IsExtending(g) || AnyGameRunning(g)
                || AutoTracer.PlanTracePath(g, _env.FileExists) is null || !_volatileChecked.Add(g.Name))
            {
                continue;
            }

            try
            {
                PlanFile plan = _env.LoadPlan(g.Plan);
                if (plan.Zone.StartLcn is null || !HoldsVolatile(plan))
                {
                    continue;
                }

                using IDisposable? gate = _env.Lock(g);
                if (gate is null || !PlanUnchanged(g, plan))
                {
                    _ = _volatileChecked.Remove(g.Name); // 못 잡았거나 기다리는 동안 계획이 바뀜: 아무것도 바꾸지 않고 다음 틱에 다시
                    continue;
                }

                _tier?.RevertGame(g.Name, "volatile files left out"); // 링크는 새 재배치 전에 되돌림
                _ = _env.ArchiveJournal(g);
                _env.MoveAside(g.Plan, g.Plan + ExtendSuffix);
                _status.ResetForReplan(g.Name, ExtendDetail);
                _log.Info($"{g.Name}: plan holds files the game rewrites by itself, plan set aside as {ExtendSuffix}, will plan again without them (same zone)");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: volatile file check failed ({e.GetType().Name})");
            }
        }
    }

    // 트레이스 금지 게임은 트레이스를 다시 만들 수 없어, 프로필 보충 (Plan) 이 들어가기 전에 만든 계획은 스스로 다시 계획될 계기가 없습니다. 서비스 실행당 한 번 따져,
    // 계획에 보충 표지가 없고 트레이스가 덮지 못한 프로필 구간이 trace_merge_min_mib 이상이면 존 확장과 같은 길 (.extend) 로 다시 계획합니다: 같은 존 시작에 맞추고 이미 옮긴 블록은 제자리,
    // 유휴·일일 상한·바이트 예산 같은 재배치 게이트를 그대로 지납니다. 더 작으면 계획에 표지만 남깁니다. 표지는 재계획 직전에 이전 계획에도 적어, 확장이 막혀 되살려도 되풀이하지 않습니다.
    private void ReplanProfileSupplement(AsterSettings s)
    {
        if (!(s.Auto.Relocate && s.Auto.MaxRelocationsPerDay > 0))
        {
            return;
        }

        foreach (GameSettings g in s.Games)
        {
            if (!HeavyWorkAllowed(g) || !GamePolicy.AllowsRelocate(g) || _status.Get(g.Name) is not { Stage: AutoStage.Done, RelocatedAt: not null } || !_env.FileExists(g.Plan) || IsExtending(g) || AnyGameRunning(g)
                || !_supplementChecked.Add(g.Name))
            {
                continue;
            }

            try
            {
                if (_env.TraceBlocker(g) is not { } cheat || cheat == AutoEnv.AntiCheatUnknown || AutoTracer.PlanTracePath(g, _env.FileExists) is not { } tracePath)
                {
                    continue; // 탐지 실패는 확정이 아님: 이 실행에서는 건드리지 않음
                }

                PlanFile plan = _env.LoadPlan(g.Plan);
                if (plan.Zone.StartLcn is null || plan.Stats.ContainsKey(SupplementKey) || plan.Stats.ContainsKey(SupplementCheckedKey) || !_env.DirectoryExists(plan.OriginRoot))
                {
                    continue;
                }

                using IDisposable? gate = _env.Lock(g); // 표지를 쓰거나 계획을 치우는 구간 (아래 plan 을 수정하기 전에 확인)
                if (gate is null || !PlanUnchanged(g, plan))
                {
                    _ = _supplementChecked.Remove(g.Name); // 못 잡았거나 기다리는 동안 계획이 바뀜: 아무것도 바꾸지 않고 다음 틱에 다시
                    continue;
                }

                (IReadOnlyList<TraceEvent> trace, _, _) = DropVolatile(g, _env.LoadTrace(tracePath, g.OriginRoot));
                (_, long bytes) = ProfileSupplement.Union(trace, MaxZoneMib(s, g) << 20, budget => _env.ProfileEvents(g, budget));
                plan.Stats[SupplementCheckedKey] = 1L;
                bool replan = bytes >= (long)s.Auto.TraceMergeMinMib << 20;
                if (replan)
                {
                    plan.Stats[SupplementReplanKey] = 1L; // 확장이 막혀 되살리면 NoteBlocked 가 0 으로 돌림
                }

                plan.Save(g.Plan);
                if (!replan)
                {
                    continue;
                }

                _tier?.RevertGame(g.Name, "engine profile added"); // 링크는 새 재배치 전에 되돌림
                _ = _env.ArchiveJournal(g);
                _env.MoveAside(g.Plan, g.Plan + ExtendSuffix);
                _status.ResetForReplan(g.Name, ExtendDetail);
                _log.Info($"{g.Name}: plan lacks {(bytes + (1 << 20) - 1) >> 20} MiB of the engine profile (tracing is not allowed for this game), plan set aside as {ExtendSuffix}, will plan again with it (same zone)");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: engine profile supplement check failed ({e.GetType().Name})");
            }
        }
    }

    // 하루 쓰기 예산 (바이트, 0 = 무제한): 명시하지 않았으면 SMR 로 보이는 디스크는 더 낮은 기본값.
    private long BudgetBytes(AsterSettings s, GameSettings g)
    {
        if (s.Auto is { MaxRelocateGibPerDaySet: true } a && a.MaxRelocateGibPerDay > AutoSettings.SmrMaxRelocateGibPerDay && IsSmr(g) && _smrBudgetLogged.TryAdd(g.Name, 0))
        {
            _log.Info($"{g.Name}: max_relocate_gib_per_day {a.MaxRelocateGibPerDay} is above the SMR default {AutoSettings.SmrMaxRelocateGibPerDay}; using the explicit value");
        }

        return (long)(s.Auto.MaxRelocateGibPerDaySet ? s.Auto.MaxRelocateGibPerDay : s.Auto.MaxRelocateGibFor(IsSmr(g))) << 30;
    }

    private bool IsSmr(GameSettings g)
    {
        try { return _env.IsSmr(DriveOf(g)); }
        catch (Exception) { return false; } // 질의 실패는 SMR 아님으로 본다
    }

    private long MaxZoneMib(AsterSettings s, GameSettings g) => EffectiveZoneMib(s, g, _env);

    /// 유효 존 상한 (MiB): max_zone_mib 가 auto 면 RAM·디스크 종류에 맞춘 값, 아니면 설정값. 트레이스 읽기 캡의 기본값도 이 값을 따릅니다.
    public static long EffectiveZoneMib(AsterSettings s, GameSettings g, AutoEnv env) => s.MaxZoneAuto ? ZoneSize.AutoMaxMib(env.PhysicalRamBytes(), SlowBus(g, env)) : s.MaxZoneMib;

    private static bool SlowBus(GameSettings g, AutoEnv env)
    {
        try { return env.IsSlowBus(DriveOf(g)); }
        catch (Exception) { return true; } // 질의 실패는 종류 미확인으로 본다
    }

    // 트레이스의 고유 (경로, 64 KiB 블록) 수.
    private static long DistinctBlocks(IEnumerable<TraceEvent> events)
    {
        var seen = new HashSet<(string, long)>();
        foreach (TraceEvent e in events)
        {
            for (long b = e.Offset / HotSet.Block; b <= (e.Offset + e.Length - 1) / HotSet.Block; b++)
            {
                seen.Add((e.Path, b));
            }
        }

        return seen.Count;
    }

    // 확장할 수 없음 (존 뒤가 비어 있지 않음, 기존 존이 제자리가 아님, 계획 실패 등): 전체 재배치로 떨어지지 않고 이전 계획을 되살려 끝난 상태로 둡니다.
    // 같은 트레이스로는 다시 시도하지 않음 (_extTried). 저널은 확장을 시작할 때 이미 옆으로 보관됐으므로 되살린 계획은 새 저널로 이어집니다.
    private void BlockExtension(GameSettings g, string reason)
    {
        _env.MoveAside(g.Plan + ExtendSuffix, g.Plan); // 새 계획이 이미 만들어졌으면 덮어씀
        NoteBlocked(g);
        _env.Touch(g.Plan); // 옛 수정 시각이면 재시작마다 트레이스가 계획보다 새롭다고 보고 확장을 되풀이함
        _ = _seeded.Remove(g.Name);
        _status.Complete(g.Name, g.Source, _env.Now(), counted: false, null);
        _log.Info($"{g.Name}: zone extension blocked: {reason}");
    }

    // 막힌 확장이 서비스를 다시 시작할 때마다 되풀이되지 않게, 되살린 계획에 지금의 존 상한·병합 정책을 적어 둡니다 (새 트레이스가 쌓이면 다시 따져 봄). 적지 못해도 막힘 처리는 그대로.
    private void NoteBlocked(GameSettings g)
    {
        try
        {
            PlanFile plan = _env.LoadPlan(g.Plan);
            plan.Stats[EffectiveZoneKey] = MaxZoneMib(_settings(), g);
            plan.Stats[MergePolicyKey] = AutoTracer.MergePolicy;
            if (plan.Stats.ContainsKey(SupplementReplanKey))
            {
                plan.Stats[SupplementReplanKey] = 0L; // 되살린 계획이 이후 일반 확장에서 보충 재계획으로 오인되지 않게
            }

            plan.Save(g.Plan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            // 계획 파일을 읽거나 쓸 수 없음: 다음 시작에 한 번 더 따져 볼 뿐
        }
    }

    // 확장 중 재배치가 실패함 (이동 실패·데이터 검사 실패·예외): 실패로 두지 않고 쓴 줄을 되돌린 뒤 이전 계획을 되살립니다. 확장 중이 아니면 거짓.
    // newRound: 실패한 회차가 아니라 나중에 복구 재개로 부름 (되돌리기 쓰기를 새 집계 기록으로 셈).
    private bool BlockFailedExtension(GameSettings g, string detail, Exception? e, int mark, CancellationToken token, bool newRound = false)
    {
        if (!IsExtending(g))
        {
            return false;
        }

        _log.Warn($"{g.Name}: zone extension failed ({detail}{(e is null ? string.Empty : ": " + e.GetType().Name)})");
        if (!Undo(g, mark, token, newRound))
        {
            Fail(g, $"{ExtensionRevertDetail} ({detail})", e); // 되돌리기를 끝내지 못했으니 저널을 두고 실패로: 다음 실행이 되돌리기부터 다시 함
            return true;
        }

        try
        {
            _ = _env.ArchiveJournal(g); // 확장 계획의 저널이 남으면 복원한 이전 계획의 다음 적용이 다른 계획의 저널이라며 거부하므로 옆으로 보관
        }
        catch (Exception a) when (a is IOException or UnauthorizedAccessException)
        {
            Fail(g, $"{ExtensionArchiveDetail} ({a.GetType().Name}; {detail})", a); // 보관 못 한 채 이전 계획을 되살리면 다음 적용이 거절되므로 실패로 두고 다시 시도
            return true;
        }

        BlockExtension(g, detail);
        return true;
    }

    // 확장 재배치가 끝남: 이전 계획을 일반 .stale 자리로 옮겨 확장 중 표시를 지우고, 늘어난 크기를 남깁니다.
    private void FinishExtension(GameSettings g, PlanFile plan)
    {
        string old = g.Plan + ExtendSuffix;
        long Used(PlanFile p) => p.Stats.TryGetValue("zone_used_clusters", out object? u) && u is long clusters ? clusters : 0;
        long grew = 0;
        try
        {
            grew = (Used(plan) - Used(_env.LoadPlan(old))) * plan.ClusterBytes >> 20;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Warn($"{g.Name}: previous plan unreadable, zone growth not logged ({e.GetType().Name})");
        }

        _env.MoveAside(old, g.Plan + StaleSuffix);
        _log.Info($"{g.Name}: zone extended by {grew} MiB (run {AutoTracer.RunCount(g, _env.FileExists)})");
    }
}
