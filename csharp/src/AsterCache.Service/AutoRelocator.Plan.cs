using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;

namespace AsterCache.Service;

/// AutoRelocator 의 계획 만들기: 트레이스에서 계획 파일을 만들고 보류·복원·범위 계산을 다룸.
public sealed partial class AutoRelocator
{
    // 계획 만들기: 탐색용 큰 존으로 핫 바이트를 재고, 그 크기에 맞는 존으로 다시 계획해 계획과 선읽기 파일을 저장합니다.
    // reuseZone: 업데이트 전 계획 (.stale) 의 존 시작이 있으면 그 자리에 맞춰 계획합니다 (비어 있지 않으면 Relocate 가 reuseZone 없이 다시 부름).
    private bool Plan(GameSettings g, AsterSettings s, bool reuseZone = true)
    {
        Note(g, AutoStage.Relocating, "planning");
        try
        {
            string tracePath = AutoTracer.PlanTracePath(g, _env.FileExists) ?? AutoTracer.RawTracePath(g);
            bool boot = tracePath == AutoTracer.BootTracePath(g);
            bool prefetch = boot && _env.TraceSource(tracePath) == PlanSourcePrefetch;
            (IReadOnlyList<TraceEvent> trace, JObject excluded, IReadOnlyList<TraceEvent> warmOnly) = DropVolatile(g, _env.LoadTrace(tracePath, g.OriginRoot));
            IReadOnlyList<TraceEvent> fingerprintTrace = trace;
            long supplementBytes = 0;
            string? cheat = _env.TraceBlocker(g);
            if (cheat is not null)
            {
                (trace, supplementBytes) = ProfileSupplement.Union(trace, MaxZoneMib(s, g) << 20, budget => _env.ProfileEvents(g, budget)); // 트레이스를 만들 수 없는 게임: 트레이스가 덮지 못한 엔진 프로필 구간을 존 상한 안에서 보충
            }

            IReadOnlyList<IReadOnlyList<TraceEvent>> sessions = [prefetch ? RangesByLcn(trace) : trace];
            PlanFile probe = _env.MakePlan(sessions, g.OriginRoot, ZoneSize.ProbeMib);
            long hot = probe.Stats.TryGetValue("hot_bytes", out object? h) && h is long bytes ? bytes : 0;
            if (hot == 0)
            {
                PlanFailed(g, "no hot data", null);
                return false;
            }

            long zoneMib = Math.Min(ZoneSize.ZoneMib(hot), MaxZoneMib(s, g));
            bool extending = IsExtending(g);
            if (extending && hot > zoneMib << 20)
            {
                _log.Info($"{g.Name}: zone extension overflow {(hot >> 20) - zoneMib} MiB dropped (max zone {zoneMib} MiB), the latest blocks of the merged trace are left out");
            }

            bool followResidual = _env.ReplanResidual is not null && PreviousIsResidual(g); // 이전 계획 (.extend 또는 .stale) 이 잔여 계획이면 단일 존 접두부로 가지 않고 모든 이전 task 를 승계
            (long? start, long usedClusters) = reuseZone && !followResidual && _env.MakePlanAt is not null ? PreviousZone(g) : (null, 0);
            if (extending && start is null && reuseZone && !followResidual)
            {
                BlockExtension(g, "previous zone start unknown");
                return false;
            }

            PlanFile plan;
            try
            {
                plan = start is { } lcn ? _env.MakePlanAt!(sessions, g.OriginRoot, zoneMib, lcn, usedClusters) : _env.MakePlan(sessions, g.OriginRoot, zoneMib);
            }
            catch (PyValueException e) when (start is not null && !extending)
            {
                // 존 안의 핫 파일이 접근 순서와 반대로 놓이면 (zone overlaps hot data in a cycle) 그 자리에서는 계획할 수 없음: 확장이 아니면 새 존을 찾음
                _log.Info($"{g.Name}: previous zone cannot be planned in place ({FirstLine(e.Message)}), looking for a new one");
                (start, plan) = (null, _env.MakePlan(sessions, g.OriginRoot, zoneMib));
            }

            if (start is not null && ZoneBlockedByOwnData(plan, extending ? usedClusters : 0))
            {
                if (extending)
                {
                    BlockExtension(g, "the region after the zone holds data the plan does not move");
                    return false;
                }

                _log.Info($"{g.Name}: previous zone holds data this plan does not move, looking for a new one");
                (start, plan) = (null, _env.MakePlan(sessions, g.OriginRoot, zoneMib));
            }

            if (start is not null && ZoneReserved(g, plan, extending ? usedClusters : 0))
            {
                if (extending)
                {
                    BlockExtension(g, "the region after the zone is reserved by another game plan");
                    return false;
                }

                _log.Info($"{g.Name}: previous zone overlaps another game plan, looking for a new one");
                (start, plan) = (null, _env.MakePlan(sessions, g.OriginRoot, zoneMib));
            }

            if (extending && start is not null && ExtensionRegionInUse(g, plan))
            {
                if (IsSupplementExtension(g))
                {
                    BlockExtension(g, "the region after the zone is in use, engine profile not added"); // 이미 옮긴 블록을 다시 옮기는 전체 재배치로 가지 않음
                    return false;
                }

                _log.Info($"{g.Name}: the region after the zone is in use, looking for a new one");
                (start, plan) = (null, _env.MakePlan(sessions, g.OriginRoot, zoneMib));
            }

            _ = start is null ? _seeded.Remove(g.Name) : _seeded.Add(g.Name);
            if (boot)
            {
                plan.Stats.Add("source", prefetch ? PlanSourcePrefetch : PlanSourceBoot);
            }

            if (!boot)
            {
                plan.Stats[EffectiveZoneKey] = MaxZoneMib(s, g);
                plan.Stats[MergePolicyKey] = AutoTracer.MergePolicy;
                if (_env.TraceFingerprint(tracePath) is { } fp)
                {
                    plan.Stats[FingerprintKey] = fp;
                }

                plan.Stats[StableFingerprintKey] = PlanFile.TraceFingerprint(fingerprintTrace);
            }

            if (supplementBytes > 0)
            {
                long supplementMib = (supplementBytes + (1 << 20) - 1) >> 20;
                plan.Stats[SupplementKey] = supplementMib;
                _log.Info($"{g.Name}: engine profile added {supplementMib} MiB the trace did not cover");
            }
            else if (cheat is not null && cheat != AutoEnv.AntiCheatUnknown)
            {
                plan.Stats[SupplementCheckedKey] = 1L; // 보충할 것이 없었음: 나중에 다시 따지지 않게 표지
            }

            plan.Stats[PlannedAtKey] = _env.Now().ToUnixTimeSeconds();
            plan.Stats[VolatileFiles.RuleKey] = VolatileFiles.RuleVersion;
            if (excluded.Count > 0)
            {
                plan.Stats.Add(VolatileFiles.StatsKey, excluded);
                _log.Info($"{g.Name}: {excluded.Count} volatile file(s) left out of the plan");
            }

            if (extending && start is null && !followResidual)
            {
                _ = _env.ArchiveJournal(g); // 새 존 계획은 확장 계획의 저널과 맞지 않음
            }

            if ((followResidual || !extending && _env.JournalRows(g) > 0) && _env.ReplanResidual is { } residual)
            {
                // 저널에 행이 있음 (부분 적용) 이거나 이전 계획이 잔여 계획인 확장: 활성 계획을 새 계획으로 바로 덮지 않고, 이미 옮긴 조각을 지키는 잔여 재계획으로 저널 보관과 함께 바꿈
                plan = InheritHeld(g, plan); // 아직 읽지 못하는 보류 파일은 후보에서 빠졌어도 계속 보류로 남김
                bool hadJournal = _env.JournalRows(g) > 0;
                try
                {
                    plan = residual(g, plan, ResidualOlds(g), sessions);
                    LogResidualPlacement(g, plan);
                }
                catch (PlanRejectedException e) when (IsTemporaryRejection(e))
                {
                    HoldTemporary(g, e); // 아무것도 바꾸지 않았음: 일시 보류 뒤 같은 입력으로 다시
                    return false;
                }
                catch (PlanRejectedException e) when (followResidual && extending)
                {
                    BlockExtension(g, "residual replan rejected: " + FirstLine(e.Message)); // 이미 옮긴 조각을 다시 옮기는 계획으로 가지 않고 이전 계획을 되살림
                    return false;
                }
                catch (PlanRejectedException e)
                {
                    HoldRecovery(g, e.Message);
                    return false;
                }
                catch (Exception e) when (IsTransient(e))
                {
                    HoldTransient(g, e); // 저널 잠금·읽기 실패: 아무것도 바꾸지 않았음, 곧 다시
                    return false;
                }

                _ = _seeded.Remove(g.Name);
                bool archived = hadJournal && _env.JournalRows(g) == 0; // 같은 계획이면 저널을 보관하지 않고 그대로 이어감
                _log.Info($"{g.Name}: partially applied plan replanned, the parts already moved are kept" + (!hadJournal ? " (no earlier journal)" : archived ? " (earlier journal archived)" : " (same plan, journal kept)"));
            }
            else
            {
                _env.SavePlan(plan, sessions, g.Plan);
            }

            PlanOutput.SavePrefetch(g.Trace, plan.PrefetchTrace(sessions), warmOnly);

            if (extending && start is null)
            {
                _env.MoveAside(g.Plan + ExtendSuffix, g.Plan + StaleSuffix); // 마지막에 치움: 앞에서 실패하면 확장 중 표시가 남아 BlockExtension 이 이전 계획을 되살림
            }

            _log.Info($"{g.Name}: auto plan made, hot {hot >> 20} MiB, zone {zoneMib} MiB");
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            PlanFailed(g, "plan failed: " + e.GetType().Name, e);
            return false;
        }
    }

    // 가변 파일 (이름 규칙, 트레이스에서 run 사이에 바뀌었거나 쓰기로 열렸다고 표시된 파일) 을 계획 입력에서 뺍니다. 뺀 파일과 사유는 계획 stats 에 남김.
    private (IReadOnlyList<TraceEvent> Kept, JObject Excluded, IReadOnlyList<TraceEvent> WarmOnly) DropVolatile(GameSettings g, IReadOnlyList<TraceEvent> trace)
    {
        IReadOnlyDictionary<string, string> marked = _env.LoadVolatile(AutoTracer.VolatilePath(g));
        var excluded = new JObject();
        foreach (string path in trace.Select(t => t.Path).Distinct(StringComparer.Ordinal))
        {
            if ((VolatileFiles.NameReason(path) ?? (marked.TryGetValue(path, out string? why) ? why : null)) is { } reason)
            {
                excluded.Add(path, reason);
            }
        }

        return excluded.Count == 0
            ? (trace, excluded, [])
            : ([.. trace.Where(t => !excluded.ContainsKey(t.Path))], excluded, [.. trace.Where(t => excluded.ContainsKey(t.Path))]);
    }

    // 이전 계획들 (옛 계획과 읽히는 활성 계획) 에 보류로 남은 파일 중 지금도 extent 를 읽지 못하는 파일의 핫 범위와 지문을 새 후보에 승계합니다:
    // 후보 생성은 읽지 못하는 파일을 Order 에서 빼므로, 그대로 두면 잔여 재계획이 그 파일을 보지 못해 held_files·held_targets (이미 옮긴 조각의 이전 목표) 가 사라짐.
    // 조회가 성공한 파일은 승계하지 않음 (후보에 없으면 트레이스가 더는 원하지 않는 파일, 있으면 보류가 아니라 정상 처리).
    private PlanFile InheritHeld(GameSettings g, PlanFile candidate)
    {
        List<PlanFile> previous = ResidualOlds(g);
        try
        {
            if (_env.FileExists(g.Plan))
            {
                previous.Add(_env.LoadPlan(g.Plan));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // 읽히지 않는 활성 계획은 승계 근거에서만 빠짐
        }

        var have = candidate.Order.Select(o => o.Path).ToHashSet(StringComparer.Ordinal);
        var order = candidate.Order.ToList();
        var prints = candidate.Fingerprints.ToList();
        foreach (PlanFile old in previous.Where(p => p.Stats.TryGetValue(ResidualPlanner.HeldKey, out object? h) && h is JObject))
        {
            foreach (string path in ((JObject)old.Stats[ResidualPlanner.HeldKey]!).Select(kv => kv.Key).Where(p => !have.Contains(p) && _env.Extents(p) is null))
            {
                order.AddRange(old.Order.Where(o => o.Path == path));
                prints.AddRange(old.Fingerprints.Where(f => f.Path == path));
                _ = have.Add(path);
            }
        }

        return order.Count == candidate.Order.Count ? candidate : candidate with { Order = order, Fingerprints = prints };
    }

    // 확장 중에 계획이 실패하면 실패 상태로 두지 않고 이전 계획을 되살립니다. 확장이 아니어도 낡은 계획 (.stale) 이 있으면 같은 규칙: 되살리고 done (stale plan) 을 유지합니다.
    private void PlanFailed(GameSettings g, string detail, Exception? e)
    {
        if (IsExtending(g))
        {
            BlockExtension(g, e is null ? detail : $"{detail}: {FirstLine(e.Message)}"); // 예외 형식만으로는 원인을 알 수 없음
        }
        else if (!RestoreStalePlan(g, e is null ? detail : $"{detail}: {FirstLine(e.Message)}"))
        {
            Fail(g, detail, e);
        }
    }

    // 활성 계획을 읽어 보류 파일이 있으면 그 계획, 없거나 읽지 못하면 null.
    private PlanFile? LoadHeldPlan(GameSettings g)
    {
        try
        {
            PlanFile plan = _env.LoadPlan(g.Plan);
            return HeldFiles(plan) > 0 ? plan : null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private bool RestoreStalePlan(GameSettings g, string why)
    {
        string stale = g.Plan + StaleSuffix;
        if (_env.FileExists(g.Plan) || !_env.FileExists(stale))
        {
            return false;
        }

        try
        {
            _env.MoveAside(stale, g.Plan);
        }
        catch (Exception m) when (m is IOException or UnauthorizedAccessException)
        {
            return false; // 되살리지 못하면 기존처럼 실패로 기록
        }

        _planFailedAt[g.Name] = _env.Now();
        _ = _seeded.Remove(g.Name);
        if (LoadHeldPlan(g) is { } held && HoldUnreadable(g, held))
        {
            _log.Warn($"{g.Name}: replan failed ({why}), previous plan restored with held files, not marked done");
            return true; // 읽지 못한 파일이 보류로 남은 미완료 계획은 완료로 복원하지 않음: UnreadableRetry 뒤 다시 계획
        }

        _status.Complete(g.Name, g.Source, _env.Now(), counted: false, StalePlanDetail);
        _log.Warn($"{g.Name}: replan failed ({why}), previous plan restored, state stays done ({StalePlanDetail})");
        return true;
    }

    private static string FirstLine(string message) => message.Split('\n', 2)[0].Trim();

    // boot 트레이스 (가져온 프로필 또는 프리페치 기록) 로 만든 계획인가.
    private static bool IsBootPlan(PlanFile plan) => plan.Stats.TryGetValue("source", out object? v) && v is PlanSourceBoot or PlanSourcePrefetch;

    // 재배치를 마친 게임의 표시: 프리페치 기록이면 "relocated (prefetch)", 프로필이면 "relocated (profile)", 원시 트레이스면 없음.
    private static string? RelocatedTag(PlanFile plan) =>
        !IsBootPlan(plan) ? null : plan.Stats["source"] is PlanSourcePrefetch ? RelocatedPrefetch : RelocatedProfile;

    // 완료 단계의 detail 은 예열 표시를 새로 붙일 때 비우되, 프로필·프리페치 기록으로 재배치했다는 표시는 남깁니다.
    private static string? DoneDetail(AutoGameEntry e) => e.Stage != AutoStage.Done ? e.Detail
        : e.Detail?.Contains(RelocatedPrefetch, StringComparison.Ordinal) == true ? RelocatedPrefetch
        : e.Detail?.Contains(RelocatedProfile, StringComparison.Ordinal) == true ? RelocatedProfile : null;

    // 프리페치 계획의 입력: 트레이스가 참조한 범위의 앞뒤로 PrefetchPadBytes 를 더해 (겹치면 합치고 파일 크기로 자름), 파일의 물리 순서 (첫 extent 의 LCN, 오름차순) 와 오프셋 순으로.
    // shortcut: 프리페치 순서가 실제 접근 순서와 얼마나 맞는지 아직 재지 않아서 (τ 검증 전) 접근 순서를 따르지 않고 물리 순서로 보수적으로 정렬함, 검증되면 프리페치 순서로 바꿈.
    private List<TraceEvent> RangesByLcn(IReadOnlyList<TraceEvent> trace)
    {
        var rows = new List<(string Path, long Lcn, long Start, long End)>();
        foreach (IGrouping<string, TraceEvent> file in trace.GroupBy(e => e.Path, StringComparer.OrdinalIgnoreCase))
        {
            long size = _env.Stat(file.Key).Size;
            if (size <= 0 || _env.Extents(file.Key) is not { Count: > 0 } extents)
            {
                continue;
            }

            long lcn = extents.Min(x => x.Lcn);
            (long Start, long End)? open = null;
            foreach ((long Start, long End) r in file.Select(e => (Start: Math.Max(0, e.Offset - PrefetchPadBytes), End: Math.Min(size, e.Offset + e.Length + PrefetchPadBytes))).Where(r => r.Start < r.End).OrderBy(r => r.Start))
            {
                if (open is { } o && r.Start <= o.End)
                {
                    open = (o.Start, Math.Max(o.End, r.End));
                    continue;
                }

                if (open is { } done)
                {
                    rows.Add((file.Key, lcn, done.Start, done.End));
                }

                open = r;
            }

            if (open is { } last)
            {
                rows.Add((file.Key, lcn, last.Start, last.End));
            }
        }

        return rows.OrderBy(r => r.Lcn).ThenBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Start).SelectMany(r => Chunks(r.Path, r.Start, r.End)).ToList();
    }

    // 트레이스 이벤트 한 개의 길이는 TraceEvents.MaxLength 이하여야 하므로 큰 범위는 그 길이로 잘라 이어 붙입니다.
    private static IEnumerable<TraceEvent> Chunks(string path, long start, long end)
    {
        for (long at = start; at < end; at += TraceEvents.MaxLength)
        {
            yield return new TraceEvent(path, at, Math.Min(TraceEvents.MaxLength, end - at));
        }
    }

    // 확장 계획이 덧붙일 구간 (옮길 목표) 이 다른 파일에 쓰이고 있는가: 적용 때의 zone_not_free 검사를 미리 합니다.
    // 비트맵을 못 읽거나 판정에 실패하면 거짓: 확장을 시도하고, 적용 때 zone_not_free 로 거부되면 Relocate 가 새 존으로 다시 계획합니다.
    private bool ExtensionRegionInUse(GameSettings g, PlanFile plan)
    {
        try
        {
            if (_env.Bitmap(DriveOf(g)) is { } bitmap)
            {
                PlanGuards.CheckZoneFree(plan, bitmap, _env.Extents);
            }

            return false;
        }
        catch (PlanRejectedException e) when (IsZoneNotFree(e))
        {
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }
}
