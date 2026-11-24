using System.Globalization;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;

namespace AsterCache.Service;

/// AutoRelocator 의 통째 배치 (설정 auto.relocate_layout = whole_file): 계획을 파일 전체를 한 조각으로 zone 에 놓는 방식으로 만들고, 예전 범위 계획이 적용된 게임을 새 방식으로 이행합니다.
/// 설정이 ranges 이면 이 파일의 코드는 아무 일도 하지 않습니다 (기본은 whole_file). ranges 로 되돌려도 이미 있는 통째 계획은 그대로 둡니다 (검사는 계획의 표지로 판정): 설정을 바꾸는 순간 대량 이동이 일어나지 않게.
public sealed partial class AutoRelocator
{
    private const string NoWholeFiles = "no file qualifies for whole-file placement, the game is not relocated (prewarm only)";

    internal const string LayoutDetail = "layout rule changed, relocating whole files";

    private readonly HashSet<string> _layoutChecked = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 예전 범위 계획의 통째 배치 이행을 이미 따져 본 게임 (서비스 실행당 한 번)
    private readonly HashSet<string> _layoutSkipLogged = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 이행을 미루는 사유를 이미 로그로 남긴 게임
    private readonly Dictionary<string, DateTimeOffset> _layoutRetryAt = new(StringComparer.Ordinal); // 틱 스레드에서만 씀: 이행을 지금은 못 한 게임과 다시 시도할 시각

    // 이 게임의 재배치 방식이 통째 배치인가: 게임별 layout 이 전역 auto.relocate_layout 보다 우선 (GamePolicy.RelocateLayout). 방식을 읽는 곳은 모두 이 함수를 거침.
    private static bool WholeLayout(GameSettings g, AsterSettings s) => GamePolicy.RelocateLayout(g, s.Auto) == RelocateLayouts.WholeFile;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _wholeNone = new(StringComparer.Ordinal); // 통째로 놓을 파일이 없어 옮기지 않기로 한 게임 (이행에서 예전 범위 계획을 그대로 둔 것): 정기 재적용을 쉼, 서비스 실행마다 다시 따짐

    // 후보 계획을 만듦: 통째 방식이고 환경이 통째 계획 함수를 주면 통째 계획 (유효 존 상한까지, 하루 예산이 있으면 그보다 큰 파일은 제자리), 아니면 지금처럼 범위 계획.
    // 통째로 놓을 파일이 하나도 없으면 (핫 파일이 모두 크기가 핫의 2배를 넘는 등) 그대로 둠 (옮길 것이 없는 통째 계획, 예열만): 범위 계획으로 대신하면 예약 최적화가 되돌리는 배치가 그 게임에만 남음.
    private PlanFile MakeCandidate(GameSettings g, AsterSettings s, bool whole, IReadOnlyList<IReadOnlyList<TraceEvent>> sessions, long zoneMib, long? at, long used)
    {
        if (whole && _env.MakePlanWhole is { } make)
        {
            long budget = BudgetBytes(s, g);
            PlanFile built = make(sessions, g.OriginRoot, zoneMib, MaxZoneMib(s, g), budget > 0 ? budget : long.MaxValue, at, used);
            if (WholeFileLayout.WholeFiles(built) is not null && !WholeFileLayout.HasPlacements(built))
            {
                _log.Info($"{g.Name}: {NoWholeFiles}");
            }
            else
            {
                _ = _wholeNone.TryRemove(g.Name, out _);
            }

            return built;
        }

        return at is { } lcn ? _env.MakePlanAt!(sessions, g.OriginRoot, zoneMib, lcn, used) : _env.MakePlan(sessions, g.OriginRoot, zoneMib);
    }

    // 통째 계획 요약 한 줄: zone 에 놓는 파일 수·바이트, 제자리 파일 수·핫 바이트 (사유별), zone 길이, 한도 때문에 제자리로 돌린 파일 수, 큰 조각 수·바이트.
    private void LogWholePlan(GameSettings g, PlanFile plan, long capacityMib)
    {
        if (WholeFileLayout.WholeFiles(plan) is not { } files)
        {
            return;
        }

        var placed = files.ToHashSet(StringComparer.Ordinal);
        long wholeBytes = plan.Fingerprints.Where(f => placed.Contains(f.Path)).Sum(f => f.Size);
        JObject reasons = plan.Stats.TryGetValue(WholeFileLayout.InPlaceFilesKey, out object? raw) && raw is JObject r ? r : new JObject();
        long inPlace = reasons.Sum(kv => kv.Value is long n ? n : 0);
        long full = reasons.TryGetValue(WholeFileLayout.ZoneFullReason, out object? z) && z is long zn ? zn : 0;
        long hotInPlace = plan.Stats.TryGetValue("overflow_bytes", out object? o) && o is long ob ? ob : 0;
        _log.Info($"{g.Name}: whole-file plan, {files.Count} file(s) {wholeBytes >> 20} MiB placed in the zone, {inPlace} file(s) {hotInPlace >> 20} MiB hot left in place ({Reasons(reasons)}), "
            + $"zone {(plan.Stats.TryGetValue("zone_used_clusters", out object? used) && used is long u ? u : 0) * plan.ClusterBytes >> 20} MiB of {capacityMib} MiB, {full} file(s) left in place because of the zone limit, {LargePieceNote(plan)}");
    }

    // 큰 조각 배치로 zone 에 놓은 구간의 수와 바이트 (로그 한 토막).
    private static string LargePieceNote(PlanFile plan)
    {
        IReadOnlyList<(string Path, long Vcn, long Clusters, long? Offset)> pieces = WholeFileLayout.LargePieces(plan);
        return $"{pieces.Count} large piece(s) {pieces.Sum(p => p.Clusters) * plan.ClusterBytes >> 20} MiB placed in the zone";
    }

    private static string Reasons(JObject reasons) => reasons.Count == 0 ? "none" : string.Join(", ", reasons.Select(kv => $"{kv.Key} {kv.Value}"));

    // 예전 범위 계획이 적용된 게임을 통째 계획으로 서비스 실행당 한 번 바꿉니다 (가장 최근에 플레이한 게임부터 따짐). 새 zone 에 세우고 (예전 zone 의 조각은 고정하지 않음), 옮기는 일은 이후 자동 재배치가
    // 최근 플레이 순서로, 유휴·회복·하루 한도·회당 상한을 지키며 합니다. 제자리로 분류된 파일의 예전 zone 조각은 옮기지 않고 그대로 둠 (예약 드라이브 최적화가 모으도록).
    // 게임이 실행 중이거나 회복·확장 중이면 건드리지 않고 다음 틱에 다시 따짐. 예열은 계획의 order 와 지금 위치로 하므로 이행 중에도 평소처럼 동작합니다.
    private void ReplanLegacyLayout(AsterSettings s)
    {
        if (!(s.Auto.Relocate && s.Auto.MaxRelocationsPerDay > 0) || _env.SwitchPlan is null || !s.Games.Any(g => WholeLayout(g, s)))
        {
            return;
        }

        IReadOnlyDictionary<string, DateTimeOffset> played = _status.LastPlayed();
        foreach (GameSettings g in s.Games.OrderByDescending(g => played.GetValueOrDefault(g.Name, DateTimeOffset.MinValue)).ThenBy(g => g.Name, StringComparer.Ordinal))
        {
            if (!WholeLayout(g, s) || !HeavyWorkAllowed(g) || !GamePolicy.AllowsRelocate(g) || _status.Get(g.Name) is not { Stage: AutoStage.Done, RelocatedAt: not null } entry
                || !_env.FileExists(g.Plan) || LayoutBlocker(g, entry) is { } blocker && SkipLayoutOnce(g, blocker)
                || _tier?.IsActive(g.Name) == true || _recovery.IsRelocationHeld(DriveOf(g)) || RecoveryHeldUntil(g) is not null || AnyGameRunning(g)
                || _layoutRetryAt.TryGetValue(g.Name, out DateTimeOffset retryAt) && _env.Now() < retryAt || !_layoutChecked.Add(g.Name))
            {
                continue;
            }

            try
            {
                PlanFile plan = _env.LoadPlan(g.Plan);
                if (plan.Zone.StartLcn is null && WholeFileLayout.WholeFiles(plan) is not null)
                {
                    // 계획 교체 뒤 상태를 다시 계획 단계로 돌리기 전에 서비스가 꺼진 흔적: 이대로면 이 게임은 옮겨지지도 검사되지도 않으므로 이어서 상태만 돌림
                    _status.ResetForReplan(g.Name, LayoutDetail);
                    _log.Info($"{g.Name}: whole-file placement was interrupted before the game was queued again, continuing");
                    continue;
                }

                if (plan.Zone.StartLcn is null || WholeFileLayout.WholeFiles(plan) is not null)
                {
                    continue; // 존이 아직 없는 계획이거나 이미 통째 계획
                }

                if (!_env.DirectoryExists(plan.OriginRoot))
                {
                    RetryLayoutLater(g, TransientRetry); // 원본 폴더가 없음 (드라이브가 빠짐 등)
                    continue;
                }

                List<bool?> stamps = [.. plan.Fingerprints.Where(f => !VolatileFiles.Ignored(f.Path)).Select(f => ReapplyChecker.Compare(f, _env.Stat))];
                if (stamps.Contains(true))
                {
                    continue; // 원본이 바뀜: 업데이트 다시 계획이 맡음
                }

                if (stamps.Contains(null))
                {
                    RetryLayoutLater(g, TransientRetry); // 읽지 못해 모름
                    continue;
                }

                using IDisposable? gate = _env.Lock(g);
                if (gate is null || !PlanUnchanged(g, plan))
                {
                    _ = _layoutChecked.Remove(g.Name); // 못 잡았거나 기다리는 동안 계획이 바뀜: 아무것도 바꾸지 않고 다음 틱에 다시
                    continue;
                }

                if (BuildWholeFrom(g, s, plan) is { } built)
                {
                    _tier?.RevertGame(g.Name, "layout rule changed"); // 링크는 새 배치 전에 되돌림 (심볼릭 링크가 낀 계획은 재배치가 거부함)
                    _env.SwitchPlan(g, built, [plan]); // 저널은 되돌리기 기록으로 보관되고 새 계획의 저널이 따로 시작됨
                    _ = _seeded.Remove(g.Name);
                    _ = _wholeNone.TryRemove(g.Name, out _);
                    _status.ResetForReplan(g.Name, LayoutDetail); // 다음 틱부터 최근 플레이 순서로 기존 재배치 흐름 (dry-run → 예산·상한 → 적용 → 확인) 을 지남
                    LogRebuilt(g, built);
                }
            }
            catch (PlanRejectedException e) when (IsTemporaryRejection(e))
            {
                _ = _layoutChecked.Remove(g.Name); // 잠금 busy·계획 변경 등 일시 사유: 아무것도 바꾸지 않았음, 다시 시도
                _layoutRetryAt[g.Name] = _env.Now() + (IsLockBusy(e) || e.Message.StartsWith(PlanChangedWhileWaiting, StringComparison.Ordinal) ? TimeSpan.Zero : ResourceRetry);
            }
            catch (Exception e) when (IsTransient(e))
            {
                RetryLayoutLater(g, TransientRetry); // 아무것도 바꾸지 않은 읽기·잠금 실패
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Warn($"{g.Name}: whole-file migration failed ({e.GetType().Name}: {FirstLine(e.Message)})");
            }
        }
    }

    // 이행을 미루는, 시간이 지나도 저절로 풀리지 않는 사유 (없으면 null). 계획 보관 파일 (.stale) 은 다시 세우기가 끝난 뒤에도 남으므로 사유가 아님:
    // 다시 세우는 중이면 활성 계획이 없거나 (치워 둠) 상태가 완료가 아니거나 확장 표시가 있음. 이행은 활성 계획을 제자리에서 바꾸고 보관 파일은 건드리지 않음.
    private string? LayoutBlocker(GameSettings g, AutoGameEntry entry) =>
        entry.Detail?.StartsWith(StalePlanDetail, StringComparison.Ordinal) == true || _planFailedAt.ContainsKey(g.Name) ? "the plan was restored after a failed replan"
        : IsExtending(g) ? "the zone is being extended"
        : _env.FileExists(g.Plan + PlanSwitch.RecordSuffix) ? "a plan switch is unfinished"
        : AutoTracer.PlanTracePath(g, _env.FileExists) is null ? "the game has no trace" // 트레이스가 없으면 자동 재배치가 이 게임을 집지 못해 이행이 끝나지 않음
        : null;

    // 이행을 미루는 사유를 게임당 서비스 실행에 한 번만 로그로 남김 (이미 통째 계획인 게임은 대상이 아니므로 남기지 않음). 항상 true: 호출한 쪽은 이 게임을 건너뜀.
    private bool SkipLayoutOnce(GameSettings g, string why)
    {
        if (_layoutSkipLogged.Add(g.Name))
        {
            try
            {
                if (WholeFileLayout.WholeFiles(_env.LoadPlan(g.Plan)) is null)
                {
                    _log.Info($"{g.Name}: whole-file placement waits, {why}");
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // 읽지 못하면 사유 로그만 생략
            }
        }

        return true;
    }

    private void RetryLayoutLater(GameSettings g, TimeSpan after)
    {
        _ = _layoutChecked.Remove(g.Name);
        _layoutRetryAt[g.Name] = _env.Now() + after;
    }

    // 예전 범위 계획 old 로 통째 계획을 만듦. 파일을 읽지 못해 지금 만들 수 없거나 통째로 놓을 파일이 없으면 null (이유는 로그).
    // 볼륨의 가장 긴 빈 구간이 통째 합보다 짧으면 한도를 그 길이로 줄여 늦게 접근하는 파일부터 제자리로 돌려 다시 만듦 (적용 때 "no free run" 으로 막히지 않게).
    private PlanFile? BuildWholeFrom(GameSettings g, AsterSettings s, PlanFile old)
    {
        var cache = new Dictionary<string, IReadOnlyList<Extent>?>(StringComparer.Ordinal);
        IReadOnlyList<Extent>? Ext(string path) => cache.TryGetValue(path, out IReadOnlyList<Extent>? e) ? e : cache[path] = _env.Extents(path);
        long limitMib = old.Stats.TryGetValue(EffectiveZoneKey, out object? effective) && effective is long eff ? eff : MaxZoneMib(s, g); // boot 계획에는 기록이 없어 지금 설정으로 셈
        long budget = BudgetBytes(s, g);
        long maxFile = budget > 0 ? budget : long.MaxValue;
        PlanFile? built = WholeFileLayout.Rebuild(old, Ext, _env.IsSparse, _env.Unmovable, limitMib << 20, maxFile);
        if (built is null)
        {
            RetryLayoutLater(g, TransientRetry);
            _log.Info($"{g.Name}: whole-file placement not planned yet, a file of the plan cannot be read; will try again");
            return null;
        }

        long used = built.Stats.TryGetValue("zone_used_clusters", out object? u) && u is long n ? n : 0;
        if (used > 0 && _env.Bitmap(DriveOf(g)) is { } bitmap && bitmap.FindZoneRun(used, old.ClusterBytes, PlanGuards.ZoneAlign, _env.Reserved(g)) is null)
        {
            long lo = 0, hi = used - 1; // 들어가는 가장 긴 길이 (이분 탐색)
            while (lo < hi)
            {
                long mid = (lo + hi + 1) / 2;
                if (bitmap.FindZoneRun(mid, old.ClusterBytes, PlanGuards.ZoneAlign, _env.Reserved(g)) is null)
                {
                    hi = mid - 1;
                }
                else
                {
                    lo = mid;
                }
            }

            _log.Info($"{g.Name}: free space is shorter than the whole-file zone ({used * old.ClusterBytes >> 20} MiB), limit lowered to {lo * old.ClusterBytes >> 20} MiB, later files stay in place");
            built = lo > 0 ? WholeFileLayout.Rebuild(old, Ext, _env.IsSparse, _env.Unmovable, lo * old.ClusterBytes, maxFile) : null;
            if (built is null)
            {
                _log.Info($"{g.Name}: whole-file placement skipped, no free run is long enough; plan unchanged");
                return null;
            }
        }

        if (!WholeFileLayout.HasPlacements(built))
        {
            _log.Info($"{g.Name}: {NoWholeFiles}; the plan already applied is left as it is");
            _wholeNone[g.Name] = 0; // 계획을 바꾸지도 되돌리지도 않고, 재적용·잔여 재계획만 하지 않음
            return null;
        }

        return built;
    }

    // 이행 시작 한 줄: 통째로 놓는 파일 수·크기, 제자리 파일 수 (사유별), 옮길 양.
    private void LogRebuilt(GameSettings g, PlanFile built)
    {
        IReadOnlyList<string> files = WholeFileLayout.WholeFiles(built) ?? [];
        var placed = files.ToHashSet(StringComparer.Ordinal);
        long wholeBytes = built.Fingerprints.Where(f => placed.Contains(f.Path)).Sum(f => f.Size);
        JObject reasons = built.Stats.TryGetValue(WholeFileLayout.InPlaceFilesKey, out object? raw) && raw is JObject r ? r : new JObject();
        long moveBytes = built.Tasks.Sum(t => t.Clusters) * built.ClusterBytes;
        _log.Info($"{g.Name}: plan rebuilt for whole-file placement, {files.Count} file(s) {wholeBytes >> 20} MiB whole, {reasons.Sum(kv => kv.Value is long n ? n : 0)} left in place ({Reasons(reasons)}), "
            + $"{moveBytes >> 20} MiB to move; old zone pieces of in-place files stay where they are, {LargePieceNote(built)}");
    }

    // 통째 계획의 이동 진행 한 줄 (회당 상한·하루 예산으로 멈췄을 때): 한 조각으로 놓인 파일 수, 남은 이동량.
    private void LogWholeProgress(GameSettings g, ApplyReport dry, ApplyReport rep)
    {
        try
        {
            LayoutReport layout = LayoutVerifier.Verify(_env.LoadPlan(g.Plan), _env.Extents, _env.Unmovable, _env.IsSparse);
            long left = Math.Max(0, dry.BytesTodo - rep.MovedBytes);
            _log.Info($"{g.Name}: whole-file placement in progress, {layout.ExpectedRuns - layout.OrderedRuns} of {layout.ExpectedRuns} file(s) in one piece, {PercentInZone(layout)} of their clusters inside the zone, {left >> 20} MiB left to move"
                + (rep.DeferredFiles > 0 ? $", {rep.DeferredFiles} file(s) larger than the remaining daily write budget wait for the next day" : string.Empty));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException)
        {
            // 진단 로그일 뿐: 읽지 못하면 남기지 않음
        }
    }

    private static string PercentInZone(LayoutReport layout) =>
        layout.InZoneClusters is { } inZone && layout.HotClusters > 0 ? (inZone * 100.0 / layout.HotClusters).ToString("F1", CultureInfo.InvariantCulture) + "%" : "n/a";
}
