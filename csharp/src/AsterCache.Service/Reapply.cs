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

    /// 재적용 도중 게임이 시작돼 멈춤 (저널에 재개 지점이 남음). 오류가 아니고, 게임이 끝난 뒤 다음 검사에서 이어갑니다.
    Cancelled,
    Failed,
}

/// 재적용 판단 입력. GameRunning·DiskBusy 는 비싸므로 필요할 때만 부르는 지연 값입니다.
/// Contiguous 는 LayoutVerifier 와 같은 판정 (null = 알 수 없음, 건강으로 봄). 존 안 비율이 아니라 이 판정이 기준인 이유: 계획이 이미 연속인 구간을 제자리에 보존하면
/// 존 안 비율이 낮아도 배치는 연속입니다 (실제 게임 계획에서 10.5% 인데 verify 는 contiguous 였음).
public sealed record ReapplyInputs(bool PlanApplied, bool FingerprintsChanged, bool? Contiguous, Func<bool> GameRunning, Func<bool> DiskBusy, bool CanApply);

public static class ReapplyPolicy
{
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

        if (i.Contiguous is null or true)
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

    public required Func<GameSettings, PlanFile, CancellationToken, ApplyReport> Apply { get; init; }

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
        Apply = (g, plan, ct) => Run(journalDir, g, plan, new ApplyOptions(Execute: true, SaveTo: g.Plan), ct), // 존을 다시 정하면 계획에 남김
    };

    /// 레이블 확인 → 볼륨 열기 → Applier. 재적용과 자동 모드의 첫 재배치가 같은 길을 씁니다 (DryRun 은 존 시작만 계획에 저장).
    public static ApplyReport Run(string journalDir, GameSettings g, PlanFile plan, ApplyOptions options, CancellationToken ct)
    {
        using NtfsVolume vol = OpenChecked(g);
        Directory.CreateDirectory(journalDir);
        return Applier.Apply(plan, vol, g.AllowedRoots, JournalPath(journalDir, g), Hooks(), options, ct);
    }

    /// 저널대로 되돌립니다 (skipRows 줄 뒤부터만, 게임이 뜨면 조각 사이에서 멈춤). 저널이 없으면 (아무것도 옮기지 않았음) null.
    public static RevertReport? Revert(string journalDir, GameSettings g, int skipRows = 0, CancellationToken ct = default)
    {
        string journal = JournalPath(journalDir, g);
        if (!File.Exists(journal))
        {
            return null;
        }

        using NtfsVolume vol = OpenChecked(g);
        return Reverter.Revert(journal, vol, g.AllowedRoots, Hooks(), new ApplyOptions(Execute: true), ct, skipRows);
    }

    /// 저널의 현재 줄 수 (없으면 0).
    public static int JournalRows(string journalDir, GameSettings g)
    {
        string journal = JournalPath(journalDir, g);
        if (!File.Exists(journal))
        {
            return 0;
        }

        using var jr = new Journal(journal);
        return jr.Rows.Count;
    }

    private static string JournalPath(string journalDir, GameSettings g) => Path.Combine(journalDir, g.Name + ".jsonl");

    private static ApplyHooks Hooks() => new()
    {
        Extents = FileLayout.Extents,
        Digest = (p, size, extra) => SampleDigest.Compute(p, size, extra),
        SkipReason = p => FileGuards.SkipReason(p),
        RealPath = RealPaths.Real,
    };

    private static NtfsVolume OpenChecked(GameSettings g)
    {
        string letter = PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant();
        if (!string.Equals(VolumeInfo.Label(letter), g.VolumeLabel, StringComparison.OrdinalIgnoreCase))
        {
            throw new PlanRejectedException("volume label mismatch");
        }

        return NtfsVolume.Open(letter);
    }
}

/// 재적용 하나를 감싸는 취소 창. 게임 시작 알림이 Cancel 을 부르면 지금 도는 재적용만 멈추고 (한 조각을 끝낸 뒤), 돌고 있지 않으면 아무 일도 없습니다.
public sealed class ReapplyGate
{
    private readonly object _lock = new();
    private CancellationTokenSource? _current;

    public T Run<T>(CancellationToken stop, Func<CancellationToken, T> work)
    {
        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(stop);
        lock (_lock)
        {
            _current = cts;
        }

        try
        {
            return work(cts.Token);
        }
        finally
        {
            lock (_lock)
            {
                _current = null; // Cancel 이 잠금 안에서만 _current 를 건드리므로 이 뒤에 Dispose 해도 안전
            }

            cts.Dispose();
        }
    }

    public void Cancel()
    {
        lock (_lock)
        {
            _current?.Cancel();
        }
    }
}

/// 게임 하나의 주기 검사: 지문 비교 → verify_layout (연속 여부, 존 적중률은 참고 로그) → (게임 미실행·디스크 유휴·권한 OK 이면) Applier 재적용.
public sealed class ReapplyChecker
{
    private readonly ReapplyEnv _env;
    private readonly ILog _log;
    private readonly HashSet<string> _cancelled = new(StringComparer.Ordinal); // 취소로 반쯤 적용된 게임

    public ReapplyChecker(ReapplyEnv env, ILog log)
    {
        _env = env;
        _log = log;
    }

    public ReapplyVerdict Check(GameSettings game, CancellationToken cancellationToken = default)
    {
        try
        {
            PlanFile plan;
            try
            {
                plan = _env.LoadPlan(game.Plan);
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                _log.Info($"{game.Name}: no plan file yet, nothing to check"); // 아직 계획을 만들지 않은 게임은 오류가 아님
                return ReapplyVerdict.NotApplied;
            }

            CheckPaths(plan, game, _env.RealPath);
            bool applied = plan.Zone.StartLcn is not null;
            bool changed = applied && plan.Fingerprints.Any(Changed);
            LayoutReport? layout = applied && !changed ? LayoutVerifier.Verify(plan, _env.Extents) : null;
            // 파일 배치를 못 읽은 항목이 있으면 (희소 파일 등) 판단할 수 없으므로 건강으로 봅니다: 같은 이유로 매 주기 재적용하며 0 바이트를 옮기는 일을 피함.
            bool? contiguous = layout is null || layout.HotClusters == 0 || layout.MissingFiles > 0 ? null : layout.Contiguous;
            ReapplyVerdict v = ReapplyPolicy.Decide(new ReapplyInputs(applied, changed, contiguous, () => _env.GameRunning(game), () => _env.DiskBusy(game), _env.CanApply));
            _log.Info($"{game.Name}: layout check {v}" + (layout is null ? string.Empty : $" ({LayoutState(layout)}{ZoneHit(layout)})"));
            if (contiguous == false && _cancelled.Contains(game.Name))
            {
                _log.Info($"{game.Name}: partially applied (an earlier reapply was cancelled by a game start); resuming");
            }
            else if (contiguous == false)
            {
                // 설계 2.3-6: 원본이 그대로인데 배치가 풀렸으면 정기 드라이브 최적화가 되돌렸을 수 있음. 자동으로 해제하지 않고 알리기만 함.
                _log.Warn($"{game.Name}: layout degraded while source files are unchanged; scheduled drive optimization may be undoing it (not changed automatically)");
            }

            if (v == ReapplyVerdict.Reapply)
            {
                ApplyReport rep = _env.Apply(game, plan, cancellationToken);
                if (rep.ZoneRetargeted)
                {
                    _log.Info($"{game.Name}: previous zone was taken by another file, zone moved" + (rep.JournalArchivedTo is { } a ? $", earlier journal kept as {a}" : string.Empty));
                }

                _log.Info($"{game.Name}: reapplied, moved {rep.MovedBytes} bytes, {rep.Failed.Count} failed" + (rep.Stopped is { } s ? $", stopped: {s}" : string.Empty));
                if (rep.Stopped == "cancelled")
                {
                    _cancelled.Add(game.Name);
                    return ReapplyVerdict.Cancelled;
                }

                _cancelled.Remove(game.Name);
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

    /// 계획 안의 모든 경로가 (링크를 푼 뒤) 허용 루트 안인가. 서비스는 SYSTEM 이라 계획 파일을 그대로 믿지 않습니다.
    public static void CheckPaths(PlanFile plan, GameSettings game, Func<string, string> realPath)
    {
        foreach (string p in plan.Tasks.Select(t => t.Path).Concat(plan.Order.Select(o => o.Path)).Distinct(StringComparer.Ordinal))
        {
            if (!PlanGuards.Inside(p, game.AllowedRoots, realPath))
            {
                throw new PlanRejectedException("outside_allowed_root in plan");
            }
        }
    }

    private bool Changed(Fingerprint fp) => Changed(fp, _env.Stat);

    /// 계획 뒤 파일이 바뀌었거나 (크기·수정 시각) 읽을 수 없게 됐는가. 자동 재배치도 같은 기준으로 계획을 다시 만들지 정합니다.
    public static bool Changed(Fingerprint fp, Func<string, Fingerprint> stat)
    {
        try
        {
            Fingerprint now = stat(fp.Path);
            return now.Size != fp.Size || now.MtimeNs != fp.MtimeNs;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static string LayoutState(LayoutReport r) =>
        r.MissingFiles > 0 ? "layout unreadable for some files" : r.Contiguous ? "contiguous" : r.ResumeNeeded ? "resume-needed" : "non-contiguous";

    // 참고용: 핫 데이터 중 존 안 클러스터 비율 (보존 구간이 있는 계획에서는 낮아도 정상).
    private static string ZoneHit(LayoutReport r) =>
        r.HotClusters > 0 && r.InZoneClusters is { } z ? $", zone hit {100.0 * z / r.HotClusters:F1}%" : string.Empty;
}
