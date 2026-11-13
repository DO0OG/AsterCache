using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Tracer;

namespace AsterCache.Service;

/// AutoRelocator 의 다시 계획: 게임 업데이트 뒤 바뀐 범위를 가려내 계획을 다시 세우는 흐름.
public sealed partial class AutoRelocator
{
    // 재배치 도중 목표 존이 막혀 멈춘 경우: 업데이트 재계획과 같은 방식으로 저널·계획을 옆으로 치우고 (트레이스는 그대로) 처음 단계로 되돌립니다.
    private void SetAsideForReplan(GameSettings g)
    {
        if (_env.ReplanResidual is null || _env.JournalRows(g) == 0)
        {
            _ = _env.ArchiveJournal(g); // 이미 옮긴 기록이 없거나 잔여 재계획이 없으면 처음부터 다시 계획
        }
        // 저널에 행이 있으면 그대로 둠: 다음 Plan 이 저널과 .stale 로 잔여 재계획하고 교체 때 저널을 보관 (보관부터 하면 이미 옮긴 데이터를 다시 옮김)
        _env.MoveAside(g.Plan, g.Plan + StaleSuffix);
        _status.ResetForReplan(g.Name, BusyDetail);
        _log.Info($"{g.Name}: zone region appears occupied, plan set aside as {StaleSuffix}, will plan again");
    }

    // 잔여 재계획이 이미 옮긴 조각 바로 뒤에 배치했는지 서비스 로그에 남김 (인접 공간이 없으면 새 빈 구간).
    private void LogResidualPlacement(GameSettings g, PlanFile plan)
    {
        if (plan.Stats.TryGetValue(ResidualPlanner.AdjacentKey, out object? adjacent))
        {
            _log.Info($"{g.Name}: " + (adjacent is 1L ? "remainder placed right after the moved part" : "no free space after the moved part, using a new place"));
        }
    }

    // 활성 계획이 없을 때만 계획을 만듭니다: 잠금을 잡은 뒤 다시 확인해 (기다리는 동안 CLI 가 계획을 만들었으면 덮지 않음) 못 잡으면 보류합니다 (거짓, 실패로 기록하지 않음).
    private bool PlanIfMissing(GameSettings g, AsterSettings s)
    {
        using IDisposable? gate = _env.Lock(g);
        if (gate is null)
        {
            Note(g, AutoStage.WaitingIdle, ZoneLockBusyDetail);
            return false;
        }

        return _env.FileExists(g.Plan) || Plan(g, s);
    }

    // 잠금 안에서 디스크의 활성 계획이 판단할 때 읽은 계획과 같은가 (기다리는 동안 CLI 가 바꿨으면 옛 계획을 저장·이동·되돌리지 않음).
    private bool PlanUnchanged(GameSettings g, PlanFile read) => _env.FileExists(g.Plan) && _env.LoadPlan(g.Plan).Sha() == read.Sha();

    // 업데이트 뒤 재계획: 배치까지 끝낸 게임의 원본이 계획 뒤 바뀌었으면 (ReapplyChecker 의 PlanChanged 와 같은 기준) 낡은 트레이스·계획을 .stale 로 치우고
    // 처음 단계로 되돌립니다. 다음 실행에서 다시 트레이스·학습하고 새 계획으로 다시 배치합니다 (이전 존 시작을 먼저 시도). 일일 상한과 다른 안전장치는 그대로.
    // 마지막에 옮기는 계획이 남아 있어야 중간에 실패해도 다음 검사가 이어서 처리합니다.
    private void ReplanChanged(AsterSettings s)
    {
        foreach (GameSettings g in s.Games)
        {
            if (!HeavyWorkAllowed(g) || !GamePolicy.AllowsTracking(g) || _status.Get(g.Name)?.RelocatedAt is null || !_env.FileExists(g.Plan) || AnyGameRunning(g)
                || _staleChecked.TryGetValue(g.Name, out DateTimeOffset at) && _env.Now() - at < StaleCheckEvery)
            {
                continue;
            }

            _staleChecked[g.Name] = _env.Now();
            try
            {
                PlanFile plan = _env.LoadPlan(g.Plan);
                if (plan.Zone.StartLcn is null || !_env.DirectoryExists(plan.OriginRoot))
                {
                    continue;
                }

                // 새 트레이스로도 계획이 실패해 낡은 계획을 되살린 게임: 같은 트레이스로는 같은 실패라 그 뒤 트레이스가 새로 쓰였을 때만 다시 계획합니다 (그 트레이스는 새 버전의 것이라 치우지 않음).
                bool retry = false;
                bool failedRetry = false;
                if (_planFailedAt.TryGetValue(g.Name, out DateTimeOffset failed))
                {
                    if (_env.FileWriteTime(AutoTracer.RawTracePath(g)) is not { } written || written <= failed)
                    {
                        continue;
                    }

                    retry = true;
                    failedRetry = true; // 표시는 잠금을 잡은 뒤에 지움 (못 잡으면 다음 틱에 다시 이 경로)
                }
                else if (_status.Get(g.Name)?.Detail?.StartsWith(StalePlanDetail, StringComparison.Ordinal) == true)
                {
                    retry = true; // 되살린 낡은 계획을 재시작 뒤 보면 같은 취급: 트레이스를 치우지 않고 그 트레이스로 다시 계획 (실패하면 다시 되살리고 실패 시각이 남음)
                }

                // boot 트레이스로 배치한 게임이 첫 실행을 기록했으면 (원시 트레이스가 생김) 그 기록으로 계획을 바꿉니다: 원시 트레이스는 치우지 않음.
                bool recorded = retry || IsBootPlan(plan) && _env.FileExists(AutoTracer.RawTracePath(g));
                if (!recorded && !ReplanStale(g, plan))
                {
                    continue;
                }

                using IDisposable? gate = _env.Lock(g); // 저널 보관과 계획·트레이스를 치우는 구간
                if (gate is null || !PlanUnchanged(g, plan))
                {
                    _ = _staleChecked.Remove(g.Name); // 못 잡았거나 기다리는 동안 계획이 바뀜: 아무것도 바꾸지 않고 다음 틱에 다시 따짐
                    continue;
                }

                if (failedRetry)
                {
                    _ = _planFailedAt.TryRemove(g.Name, out _);
                }

                _tier?.RevertGame(g.Name, "game updated"); // 링크는 새 재배치 전에 되돌림 (심볼릭 링크가 낀 계획은 재적용·재배치가 거부함)
                _ = _env.ArchiveJournal(g); // 새 계획은 다른 저널 header 를 쓰므로 끝난 저널은 옆으로 보관
                string trace = AutoTracer.RawTracePath(g);
                bool keep = !recorded && _env.TraceBlocker(g) is not null && TraceHasUsableBlocks(trace, g); // 다시 트레이스할 수 없는 게임은 옛 트레이스를 유지 (없어진 파일·줄어든 범위는 읽을 때 걸러짐)
                bool dropFailed = false;
                HashSet<string>? changed = !recorded && !keep && _env.TraceBlocker(g) is null && _env.FileExists(trace) ? ChangedIfSmall(g, plan, traceShare: true) : null; // 일부 파일만 바뀐 업데이트: 그 파일의 범위만 빼고 나머지 학습은 유지 (트레이스 금지 게임의 트레이스 파일은 건드리지 않음)
                if (changed is not null)
                {
                    try
                    {
                        _env.DropChanged(g, changed); // 계획은 마지막에 옮기므로 여기서 멈춰도 다음 검사가 같은 판정으로 이어감 (이미 뺀 파일은 그대로)
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
                    {
                        _log.Warn($"{g.Name}: changed ranges could not be removed from the trace ({e.GetType().Name}), setting the whole trace aside instead");
                        changed = null; // 읽을 수 없는 트레이스 파일이 있어도 업데이트 처리가 멈추지 않게 기존 전체 치우기로
                        dropFailed = true;
                    }
                }

                if (changed is null && !recorded && !keep && _env.FileExists(trace))
                {
                    string Aside(string path) => !_env.FileExists(path + StaleSuffix) ? path + StaleSuffix : dropFailed ? path + StaleSuffix + PartialSuffix : path + UpdateSuffix; // 이미 있는 원본 .stale (이전 업데이트의 것, 또는 일부 정리가 실패하며 남긴 것) 은 덮지 않음
                    for (int run = AutoTracer.LastRunNumber(g, _env.FileExists); run >= 1; run--)
                    {
                        string runFile = TraceWriter.RunPath(trace, run); // 플레이 수는 run 파일로 세므로 옛 버전의 run 파일이 남지 않게 함
                        if (_env.FileExists(runFile))
                        {
                            _env.MoveAside(runFile, Aside(runFile));
                        }
                    }

                    _env.MoveAside(trace, Aside(trace));
                    string marks = AutoTracer.VolatilePath(g); // 옛 트레이스에서 알게 된 가변 파일 표시도 같이 치움
                    if (_env.FileExists(marks))
                    {
                        _env.MoveAside(marks, Aside(marks));
                    }
                }

                _env.MoveAside(g.Plan, g.Plan + StaleSuffix);
                _status.ResetForReplan(g.Name, recorded ? FirstRunDetail : keep ? KeptDetail : changed is not null ? UnchangedKeptDetail : ReplanDetail);
                _log.Info(retry
                    ? $"{g.Name}: trace updated after a failed replan, stale plan set aside as {StaleSuffix}, will plan again from it"
                    : recorded
                        ? $"{g.Name}: first run recorded, profile plan set aside as {StaleSuffix}, will plan again from the recording (same zone first)"
                        : keep
                            ? $"{g.Name}: game updated; trace kept (tracing is not allowed for this game), re-planning"
                            : changed is not null
                                ? $"{g.Name}: source files changed since the plan (game update), only the {changed.Count} changed file(s) removed from the trace and prewarm list (originals kept as {StaleSuffix}, or {UpdateSuffix} where an older {StaleSuffix} already existed), re-planning from the rest"
                                : $"{g.Name}: source files changed since the plan (game update), trace and plan set aside as {StaleSuffix}, will trace and plan again on the next run");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: update check failed, will retry ({e.GetType().Name})");
            }
        }
    }

    // 계획 지문 중 바뀐 파일의 경로 (없어짐 포함, 읽을 수 없어 모르는 파일도 바뀜으로 친다: 불확실하면 학습을 재사용하지 않음. 가변 파일은 업데이트 증거가 아니므로 제외).
    // 바뀐 파일이 계획 핫 바이트의 PartialMaxChangedShare 이상이면 null: 큰 패치는 남은 트레이스가 새 버전의 읽기 순서·양을 대표하지 못하므로 전체를 치우고 다시 학습.
    // 비율의 분자·분모는 계획 (Order) 기준. 트레이스가 존보다 커서 계획 밖에 있는 파일 (지문 없음) 도 빼야 하므로, 트레이스에 나오는 지문 없는 경로는 현재 수정 시각이 옛 계획 파일 (또는 병합 트레이스) 보다 늦거나
    // 읽을 수 없으면 바뀐 것으로 쳐서 행을 빼는 대상에만 더합니다 (안 빼면 비어 있는 존 자리를 채우는 다음 순위 블록이 그 파일의 옛 오프셋을 새 계획과 예열 목록에 넣음). 트레이스를 읽을 수 없으면 null.
    private HashSet<string>? ChangedIfSmall(GameSettings g, PlanFile plan, bool traceShare)
    {
        var changed = plan.Fingerprints.Where(f => !VolatileFiles.Ignored(f.Path) && ReapplyChecker.Changed(f, _env.Stat)).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        long total = plan.Order.Sum(o => o.Clusters);
        long lost = plan.Order.Where(o => changed.Contains(o.Path)).Sum(o => o.Clusters);
        if (changed.Count == 0 || total <= 0 || lost >= total * PartialMaxChangedShare)
        {
            return null;
        }

        string raw = AutoTracer.RawTracePath(g);
        // 기준: 계획을 만든 시각 (stats.planned_at, 이후 다시 저장·Touch·병합해도 그대로). 계획 이후 학습한 파일 중 정상적으로 바뀐 것까지 빼지만 빼기만 하므로 안전한 쪽.
        // 옛 계획은 계획 파일과 병합 트레이스의 수정 시각 중 이른 쪽으로 대신함.
        DateTimeOffset?[] times = [_env.FileWriteTime(g.Plan), _env.FileWriteTime(raw)];
        DateTimeOffset? since = plan.Stats.TryGetValue(PlannedAtKey, out object? planned) && planned is long unix ? DateTimeOffset.FromUnixTimeSeconds(unix) : times.Where(t => t is not null).Min();
        try
        {
            var known = plan.Fingerprints.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string path in _env.TracePaths(raw).Where(p => !known.Contains(p) && !changed.Contains(p) && !VolatileFiles.Ignored(p) && ModifiedSince(p, since)))
            {
                _ = changed.Add(path);
            }

            // 계획 기준만 보면 계획 밖 파일이 대량으로 바뀐 큰 패치를 부분 패치로 오판하므로, 트레이스 바이트 기준 비율도 보고 큰 쪽을 씀 (둘 중 하나라도 넘으면 전체를 치움).
            // 원본 행의 원래 길이로 셈: LoadTrace 는 없어진 파일의 행을 빼고 줄어든 파일은 잘라 큰 패치를 작게 보이게 하고, 길이 0 행은 예외를 냄. 읽을 수 없으면 아래 catch 로 전체를 치움.
            // 바이트 판정의 null 은 전체 치움 (재계획 경로) 일 때만 안전: 부분 적용 계획에서는 null 이 "아무것도 빼지 않음" 이라 큰 패치일수록 옛 오프셋이 남으므로 끔 (traceShare=false).
            IReadOnlyList<TraceEvent> events = traceShare ? _env.TraceRows(raw) : [];
            long traced = events.Sum(e => Math.Max(e.Length, 0));
            if (traced > 0 && events.Where(e => changed.Contains(e.Path)).Sum(e => Math.Max(e.Length, 0)) >= traced * PartialMaxChangedShare)
            {
                return null;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            return null;
        }

        return changed;
    }

    // 경로의 수정 시각 또는 ChangeTime 이 since 보다 늦은가. 기준 시각이 없거나 파일이 없거나 읽을 수 없으면 참 (불확실하면 바뀐 것으로).
    // 런처가 수정 시각을 매니페스트 시각으로 되돌려도 ChangeTime 은 되돌릴 수 없어 잡힘 (트레이스 행에는 파일 크기가 없어 크기 비교는 불가: 내용·크기 변경은 모두 ChangeTime 을 올림).
    private bool ModifiedSince(string path, DateTimeOffset? since)
    {
        try
        {
            return since is not { } at || new DateTimeOffset(Fingerprint.UnixEpochTicks + _env.Stat(path).MtimeNs / 100, TimeSpan.Zero) > at || _env.ChangeTime(path) is not { } ctime || ctime > at;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    // 아직 끝나지 않은 (부분 적용된) 계획의 업데이트: 일부 파일만 바뀌었으면 그 파일의 범위만 트레이스·예열 목록에서 빼고 남은 학습으로 잔여 재계획합니다 (이미 옮긴 조각은 잔여 재계획이 지킴).
    // 다시 트레이스할 수 없는 게임·boot 계획·실패는 기존대로 트레이스를 그대로 둠. 호출하는 쪽이 잠금 안에 있음.
    private void DropChangedRanges(GameSettings g, PlanFile plan)
    {
        if (IsBootPlan(plan) || !_env.FileExists(AutoTracer.RawTracePath(g)) || _env.TraceBlocker(g) is not null || ChangedIfSmall(g, plan, traceShare: false) is not { } changed)
        {
            return;
        }

        try
        {
            _env.DropChanged(g, changed);
            _log.Info($"{g.Name}: only the {changed.Count} changed file(s) removed from the trace and prewarm list (originals kept as {StaleSuffix}, or {UpdateSuffix} where an older {StaleSuffix} already existed)");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            _log.Warn($"{g.Name}: changed ranges could not be removed from the trace, planning with it as it is ({e.GetType().Name})");
        }
    }

    // 트레이스를 읽었을 때 (없는 파일·범위 밖 행은 걸러지고 줄어든 파일은 새 크기로 잘림) 남는 블록이 있는가. 읽을 수 없어도 거짓: 유지하지 않고 기존대로 치움.
    private bool TraceHasUsableBlocks(string trace, GameSettings g)
    {
        try
        {
            return _env.FileExists(trace) && _env.LoadTrace(trace, g.OriginRoot).Count > 0;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    // 서비스 시작 때 한 번: 트레이스 금지 게임인데 업데이트 재계획이 트레이스를 .stale 로 치워 사장시켰으면 (다시 트레이스할 수 없어 영영 재계획 불가) 되살립니다.
    // 그 사이 만들어진 boot 계획 (프리페치·프로필) 은 옆으로 치워 이미 옮긴 존 자리에 맞춰 다시 계획하게 합니다: 이전 .stale 계획이 있으면 그 존 시작을 지키려고 .boot 로, 없으면 .stale 로.
    // 순서: 계획 → 상태 → 트레이스. 마지막 단계 (원시 트레이스 복원) 전에 꺼져도 조건이 그대로라 다음 시작에 이어집니다.
    // 반환: 트레이스를 되살려 다시 계획하게 둔 게임 이름 (호출자가 같은 회차의 이전 계획 복원에서 뺌).
    private HashSet<string> RestoreStaleTraces(AsterSettings s)
    {
        var restored = new HashSet<string>(StringComparer.Ordinal);
        string? Kept(string path) => _env.FileExists(path + UpdateSuffix) ? path + UpdateSuffix : _env.FileExists(path + StaleSuffix) ? path + StaleSuffix : null; // 두 번째 전체 치움부터 최신 원본은 .stale.update: 있으면 그것을, 없으면 .stale 을 되살림 (다른 접미사 파일은 보관 그대로)
        foreach (GameSettings g in s.Games)
        {
            if (!HeavyWorkAllowed(g))
            {
                _recoverBusy = true;
                continue;
            }

            string raw = AutoTracer.RawTracePath(g);
            if (_env.FileExists(raw) || Kept(raw) is not { } keptRaw || _env.TraceBlocker(g) is null)
            {
                continue;
            }

            try
            {
                if (!TraceHasUsableBlocks(keptRaw, g))
                {
                    continue; // 파일이 다 없어졌거나 읽을 수 없는 옛 트레이스는 되살려도 계획할 수 없음
                }

                using IDisposable? gate = _env.Lock(g);
                if (gate is null)
                {
                    _recoverBusy = true; // 시작 복구를 다음 틱에 다시 (이 게임의 이전 계획 복원은 그동안 건너뜀)
                    _ = restored.Add(g.Name);
                    continue;
                }

                if (_env.FileExists(raw) || Kept(raw) is not { } kept || kept != keptRaw)
                {
                    continue; // 기다리는 동안 상태가 바뀜
                }

                if (_env.FileExists(g.Plan))
                {
                    if (!IsBootPlan(_env.LoadPlan(g.Plan)))
                    {
                        continue; // 이미 트레이스로 만든 계획이 있음
                    }

                    _tier?.RevertGame(g.Name, "trace restored");
                    _ = _env.ArchiveJournal(g);
                    _env.MoveAside(g.Plan, g.Plan + (_env.FileExists(g.Plan + StaleSuffix) ? BootPlanSuffix : StaleSuffix));
                }

                _status.ResetForReplan(g.Name, FirstRunDetail); // 계획이 없던 게임도 다시 계획 상태로 (이전 계획 복원으로 done 이 되지 않게)
                string sfx = kept[raw.Length..]; // 원시 트레이스에서 고른 접미사 하나로 run·marks 도 맞춤: 버전이 섞이면 run 수가 부풀고 옛 오프셋이 다시 들어감 (다른 접미사 파일은 그대로)
                for (int run = 1; run <= 32; run++)
                {
                    string runFile = TraceWriter.RunPath(raw, run);
                    if (_env.FileExists(runFile + sfx))
                    {
                        _env.MoveAside(runFile + sfx, runFile);
                    }
                }

                string marks = AutoTracer.VolatilePath(g);
                if (_env.FileExists(marks + sfx) && !_env.FileExists(marks))
                {
                    _env.MoveAside(marks + sfx, marks);
                }

                _env.MoveAside(kept, raw);
                _ = restored.Add(g.Name);
                _log.Info($"{g.Name}: trace set aside by an earlier update restored (tracing is not allowed for this game), re-planning");
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: stale trace could not be restored ({e.GetType().Name})");
            }
        }

        return restored;
    }

    // 드라이브가 분리됐거나 (폴더 없음) 파일을 읽을 수 없으면 (잠김 등) "변경" 이 아니라 건너뜀: 다음 검사에 다시 봅니다. 대표 파일 (게임 exe, 가장 큰 파일 몇 개) 이
    // 바뀐 경우에만 전체 지문을 비교합니다 (shortcut: 대표가 아닌 파일만 바뀐 업데이트는 대표가 바뀔 때까지 못 잡음, 재배치 직전 검사가 잡음).
    private bool ReplanStale(GameSettings g, PlanFile plan)
    {
        List<Fingerprint> prints = [.. plan.Fingerprints.Where(f => !VolatileFiles.Ignored(f.Path))]; // 게임이 스스로 다시 쓰는 파일 (캐시·설정·로그·세이브) 은 업데이트 증거가 아님
        IEnumerable<Fingerprint> sample = prints.Where(f => g.ExeNames.Contains(Path.GetFileName(f.Path), StringComparer.OrdinalIgnoreCase))
            .Concat(prints.OrderByDescending(f => f.Size).Take(LargestSampled));
        return sample.Any(Differs) && prints.Any(Differs);
    }

    // 지문과 다른가. 파일이 없어짐 (업데이트가 지움) 은 바뀜, 그 밖의 읽기 실패 (잠김·권한) 는 판단 불가라 재계획하지 않음 (재적용 검사와 같은 기준).
    private bool Differs(Fingerprint fp) => ReapplyChecker.Compare(fp, _env.Stat) == true;
}
