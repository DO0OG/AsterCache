using System.ComponentModel;
using AsterCache.Core;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

public enum ReapplyVerdict
{
    /// 계획이 한 번도 적용되지 않음 (존 시작 미정). 첫 적용은 사용자가 CLI 로 합니다.
    NotApplied,

    /// 계획 뒤 원본 파일이 바뀜 (지문 불일치). 새 계획이 필요하고 재적용은 거부됩니다.
    PlanChanged,
    Healthy,
    GameRunning,
    DiskBusy,

    /// 관리자가 아니거나 설정·계획 폴더 권한이 안전하지 않음: 상태만 기록.
    ApplyDisabled,
    Reapply,
    Failed,
}

/// 재적용 판단 입력. GameRunning·DiskBusy 는 비싸므로 필요할 때만 부르는 지연 값입니다.
public sealed record ReapplyInputs(bool PlanApplied, bool FingerprintsChanged, double? ZoneHitRatio, Func<bool> GameRunning, Func<bool> DiskBusy, bool CanApply);

public static class ReapplyPolicy
{
    /// 핫 데이터 중 존 안에 있는 비율이 이보다 낮아야 재적용합니다.
    public const double MinZoneHitRatio = 0.9;

    public static ReapplyVerdict Decide(ReapplyInputs i)
    {
        if (!i.PlanApplied)
        {
            return ReapplyVerdict.NotApplied;
        }

        if (i.FingerprintsChanged)
        {
            return ReapplyVerdict.PlanChanged;
        }

        if (i.ZoneHitRatio is null or >= MinZoneHitRatio)
        {
            return ReapplyVerdict.Healthy;
        }

        if (i.GameRunning())
        {
            return ReapplyVerdict.GameRunning;
        }

        if (i.DiskBusy())
        {
            return ReapplyVerdict.DiskBusy;
        }

        return i.CanApply ? ReapplyVerdict.Reapply : ReapplyVerdict.ApplyDisabled;
    }
}

/// 재적용 검사가 바깥 세계에 닿는 곳 전부. 시험은 가짜로 바꿉니다.
public sealed class ReapplyEnv
{
    public required bool CanApply { get; init; }

    public required Func<GameSettings, bool> GameRunning { get; init; }

    public required Func<GameSettings, bool> DiskBusy { get; init; }

    public required Func<string, PlanFile> LoadPlan { get; init; }

    public required Func<string, IReadOnlyList<Extent>?> Extents { get; init; }

    public required Func<string, Fingerprint> Stat { get; init; }

    public required Func<string, string> RealPath { get; init; }

    public required Func<GameSettings, PlanFile, ApplyReport> Apply { get; init; }

    /// 실제 구현. 재적용은 관리자이고 폴더 권한이 안전할 때만 (canApply). 저널은 게임마다 journalDir\이름.jsonl 하나를 계속 씁니다 (같은 저널 규칙: 이어하기·되돌리기).
    public static ReapplyEnv Create(bool canApply, string journalDir) => new()
    {
        CanApply = canApply,
        GameRunning = g => ProcessSnapshot.IsRunning(g.ExeNames),
        DiskBusy = g => DiskActivity.IsBusy(PlanFile.SplitDrive(g.OriginRoot)),
        LoadPlan = PlanFile.Load,
        Extents = FileLayout.Extents,
        Stat = PlanFile.FingerprintOf,
        RealPath = RealPaths.Real,
        Apply = (g, plan) =>
        {
            string letter = PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant();
            if (!string.Equals(VolumeInfo.Label(letter), g.VolumeLabel, StringComparison.OrdinalIgnoreCase))
            {
                throw new PlanRejectedException("volume label mismatch");
            }

            Directory.CreateDirectory(journalDir);
            var hooks = new ApplyHooks
            {
                Extents = FileLayout.Extents,
                Digest = (p, size, extra) => SampleDigest.Compute(p, size, extra),
                SkipReason = p => FileGuards.SkipReason(p),
                RealPath = RealPaths.Real,
            };
            using NtfsVolume vol = NtfsVolume.Open(letter);
            return Applier.Apply(plan, vol, g.AllowedRoots, Path.Combine(journalDir, g.Name + ".jsonl"), hooks, new ApplyOptions(Execute: true));
        },
    };
}

/// 게임 하나의 주기 검사: 지문 비교 → verify_layout → 존 적중률 → (게임 미실행·디스크 유휴·권한 OK 이면) Applier 재적용.
public sealed class ReapplyChecker
{
    private readonly ReapplyEnv _env;
    private readonly ILog _log;

    public ReapplyChecker(ReapplyEnv env, ILog log)
    {
        _env = env;
        _log = log;
    }

    public ReapplyVerdict Check(GameSettings game)
    {
        try
        {
            PlanFile plan = _env.LoadPlan(game.Plan);
            CheckPaths(plan, game);
            bool applied = plan.Zone.StartLcn is not null;
            bool changed = applied && plan.Fingerprints.Any(Changed);
            double? ratio = applied && !changed ? HitRatio(plan) : null;
            ReapplyVerdict v = ReapplyPolicy.Decide(new ReapplyInputs(applied, changed, ratio, () => _env.GameRunning(game), () => _env.DiskBusy(game), _env.CanApply));
            _log.Info($"{game.Name}: layout check {v}" + (ratio is { } r ? $" (zone hit {r * 100:F1}%)" : string.Empty));
            if (ratio < ReapplyPolicy.MinZoneHitRatio)
            {
                // 설계 2.3-6: 원본이 그대로인데 배치가 풀렸으면 정기 드라이브 최적화가 되돌렸을 수 있음. 자동으로 해제하지 않고 알리기만 함.
                _log.Warn($"{game.Name}: layout degraded while source files are unchanged; scheduled drive optimization may be undoing it (not changed automatically)");
            }

            if (v == ReapplyVerdict.Reapply)
            {
                ApplyReport rep = _env.Apply(game, plan);
                _log.Info($"{game.Name}: reapplied, moved {rep.MovedBytes} bytes, {rep.Failed.Count} failed" + (rep.Stopped is { } s ? $", stopped: {s}" : string.Empty));
            }

            return v;
        }
        catch (ShaMismatchException e)
        {
            _log.Error($"{game.Name}: DATA CHECK FAILED after move, journal kept for revert", e);
            return ReapplyVerdict.Failed;
        }
        catch (Exception e) when (e is PlanRejectedException or NotAdminException or PyValueException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            _log.Error($"{game.Name}: layout check failed", e);
            return ReapplyVerdict.Failed;
        }
    }

    // 계획 안의 모든 경로가 (링크를 푼 뒤) 허용 루트 안인가. 서비스는 SYSTEM 이라 계획 파일을 그대로 믿지 않습니다.
    private void CheckPaths(PlanFile plan, GameSettings game)
    {
        foreach (string p in plan.Tasks.Select(t => t.Path).Concat(plan.Order.Select(o => o.Path)).Distinct(StringComparer.Ordinal))
        {
            if (!PlanGuards.Inside(p, game.AllowedRoots, _env.RealPath))
            {
                throw new PlanRejectedException("outside_allowed_root in plan");
            }
        }
    }

    private bool Changed(Fingerprint fp)
    {
        try
        {
            Fingerprint now = _env.Stat(fp.Path);
            return now.Size != fp.Size || now.MtimeNs != fp.MtimeNs;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private double? HitRatio(PlanFile plan)
    {
        LayoutReport r = LayoutVerifier.Verify(plan, _env.Extents);
        return r.HotClusters == 0 || r.InZoneClusters is not { } z ? null : (double)z / r.HotClusters;
    }
}
