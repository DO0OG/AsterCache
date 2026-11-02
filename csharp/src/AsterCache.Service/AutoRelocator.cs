using AsterCache.Core.Apply;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;

namespace AsterCache.Service;

/// 유휴 판정: 모든 조건이 필요한 시간 동안 끊김 없이 참이어야 유휴입니다. 하나라도 거짓이면 처음부터 다시 잽니다.
public sealed class IdleTracker
{
    /// 이 이상 CPU 를 쓰는 중이면 다른 일을 하는 PC 로 봄 (백그라운드 작업 정도는 허용).
    public const double CpuBusyPercent = 20;

    private DateTimeOffset? _since;

    public bool Sample(DateTimeOffset now, bool quiet, TimeSpan needed)
    {
        if (!quiet)
        {
            _since = null;
            return false;
        }

        _since ??= now;
        return now - _since.Value >= needed;
    }

    public void Reset() => _since = null;
}

/// 자동 계획·첫 재배치. 30 초 재적용 틱 안에서 (같은 스레드라 이동이 겹치지 않음) 원시 트레이스가 있고 아직 재배치하지 않은 게임을
/// 최근에 한 순서로 하나씩: 계획 (Tray 의 계획 만들기와 같은 2 단계) → 유휴·일일 상한 확인 → 존 시작 정하기 → 이동 → 확인.
/// 안전장치는 재적용과 같습니다: 저널, SHA 표본 검증, 게임 시작 때 취소 (ReapplyGate), 볼륨 레이블 확인, 허용 루트 검사.
public sealed class AutoRelocator
{
    public static readonly TimeSpan DailyWindow = TimeSpan.FromHours(24);
    public const string RelocateOff = "auto relocate off";

    private readonly AutoEnv _env;
    private readonly AutoStatus _status;
    private readonly ILog _log;
    private readonly Func<AsterSettings> _settings;
    private readonly IdleTracker _idle = new();

    public AutoRelocator(AutoEnv env, AutoStatus status, ILog log, Func<AsterSettings> settings)
    {
        _env = env;
        _status = status;
        _log = log;
        _settings = settings;
    }

    public void Tick(ReapplyGate gate, CancellationToken ct)
    {
        AsterSettings s = _settings();
        double? cpu = _env.CpuPercent(); // 틱마다 불러 두 표본 사이가 한 틱이 되게 함
        if (!s.Auto.Enabled)
        {
            return;
        }

        bool relocateOn = s.Auto.Relocate && s.Auto.MaxRelocationsPerDay > 0;
        IReadOnlyDictionary<string, DateTimeOffset> played = _status.LastPlayed();
        List<GameSettings> work = s.Games.Where(g => NeedsWork(g, relocateOn))
            .OrderByDescending(g => played.GetValueOrDefault(g.Name, DateTimeOffset.MinValue)).ThenBy(g => g.Name, StringComparer.Ordinal).ToList();
        bool limitReached = relocateOn && _status.RelocationsWithin(DailyWindow) >= s.Auto.MaxRelocationsPerDay; // Note 보다 먼저 정해 같은 상태 로그가 오락가락하며 쌓이지 않게
        var ready = new List<GameSettings>();
        foreach (GameSettings g in work)
        {
            if (!_env.FileExists(g.Plan) && (_env.AnyRunning(g.ExeNames) || !Plan(g, s)))
            {
                continue; // 게임이 도는 중이면 디스크를 건드리지 않고, 계획이 실패했으면 failed 로 기록됨
            }

            if (!relocateOn)
            {
                Note(g, AutoStage.Skipped, RelocateOff);
                continue;
            }

            Note(g, AutoStage.WaitingIdle, limitReached ? "daily limit reached" : null);
            ready.Add(g);
        }

        if (ready.Count == 0 || limitReached)
        {
            _idle.Reset();
            return;
        }

        if (!_idle.Sample(_env.Now(), Quiet(s, ready, cpu), TimeSpan.FromMinutes(s.Auto.IdleMinutes)))
        {
            return;
        }

        gate.Run(ct, token =>
        {
            Relocate(ready[0], s, token);
            return true;
        });
        _idle.Reset(); // 다음 재배치도 다시 유휴를 기다림
    }

    // 상태가 바뀌었을 때만 로그에 남깁니다 (30 초마다 같은 줄이 쌓이지 않게).
    private void Note(GameSettings g, string stage, string? detail)
    {
        if (_status.Set(g.Name, g.Source, null, stage, detail))
        {
            _log.Info($"{g.Name}: auto {stage}" + (detail is null ? string.Empty : $" ({detail})"));
        }
    }

    private bool NeedsWork(GameSettings g, bool relocateOn)
    {
        AutoGameEntry? e = _status.Get(g.Name);
        bool stageOpen = e is null || e.Stage is not (AutoStage.Done or AutoStage.Failed)
            && !(e.Stage == AutoStage.Skipped && !relocateOn)
            && e.RelocatedAt is null;
        if (!stageOpen || !_env.FileExists(AutoTracer.RawTracePath(g)))
        {
            return false;
        }

        if (_env.DriveRefusal(g.OriginRoot) is { } why) // 시스템 볼륨·SSD·종류를 모르는 드라이브는 옮기지 않음 (수동 등록 게임도 같음)
        {
            Note(g, AutoStage.Skipped, why);
            return false;
        }

        return true;
    }

    // 유휴: 게임이 하나도 안 돌고, 게임 드라이브 처리량이 낮고, CPU 가 한가하고, AC 전원이거나 배터리가 충분함.
    private bool Quiet(AsterSettings s, List<GameSettings> ready, double? cpu)
    {
        if (_env.AnyRunning([.. s.Games.SelectMany(g => g.ExeNames)]) || cpu is not < IdleTracker.CpuBusyPercent || !_env.PowerOk())
        {
            return false;
        }

        return ready.Select(g => PlanFile.SplitDrive(g.OriginRoot).ToUpperInvariant()).Distinct()
            .All(letter => _env.DiskBytesPerSecond(letter) is { } bps && bps < DiskActivity.BusyBytesPerSecond);
    }

    // 계획 만들기: 탐색용 큰 존으로 핫 바이트를 재고, 그 크기에 맞는 존으로 다시 계획해 계획과 선읽기 파일을 저장합니다.
    private bool Plan(GameSettings g, AsterSettings s)
    {
        Note(g, AutoStage.Relocating, "planning");
        try
        {
            IReadOnlyList<IReadOnlyList<TraceEvent>> sessions = [_env.LoadTrace(AutoTracer.RawTracePath(g), g.OriginRoot)];
            PlanFile probe = _env.MakePlan(sessions, g.OriginRoot, ZoneSize.ProbeMib);
            long hot = probe.Stats.TryGetValue("hot_bytes", out object? h) && h is long bytes ? bytes : 0;
            if (hot == 0)
            {
                Fail(g, "no hot data", null);
                return false;
            }

            long zoneMib = Math.Min(ZoneSize.ZoneMib(hot), s.MaxZoneAuto ? ZoneSize.AutoMaxMib(_env.PhysicalRamBytes()) : s.MaxZoneMib);
            PlanFile plan = _env.MakePlan(sessions, g.OriginRoot, zoneMib);
            _env.SavePlan(plan, sessions, g.Plan);
            _log.Info($"{g.Name}: auto plan made, hot {hot >> 20} MiB, zone {zoneMib} MiB");
            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Fail(g, "plan failed: " + e.GetType().Name, e);
            return false;
        }
    }

    private void Relocate(GameSettings g, AsterSettings s, CancellationToken token)
    {
        if (_env.AnyRunning(g.ExeNames))
        {
            Note(g, AutoStage.WaitingIdle, "game is running");
            return;
        }

        Note(g, AutoStage.Relocating, null);
        _log.Info($"{g.Name}: auto relocation starting");
        int mark = int.MaxValue; // 이번 적용 전의 저널 줄 수 (모르면 아무것도 되돌리지 않음)
        try
        {
            PlanFile plan = _env.LoadPlan(g.Plan);
            ReapplyChecker.CheckPaths(plan, g, _env.RealPath);
            if (plan.Fingerprints.Any(f => ReapplyChecker.Changed(f, _env.Stat)))
            {
                _log.Info($"{g.Name}: source files changed since the plan, planning again"); // 없어진 파일은 새 계획에서 빠짐
                if (!Plan(g, s))
                {
                    return;
                }

                plan = _env.LoadPlan(g.Plan);
                ReapplyChecker.CheckPaths(plan, g, _env.RealPath);
            }

            ApplyReport dry = _env.Apply(g, plan, new ApplyOptions(DryRun: true, SaveTo: g.Plan), token); // 존 시작을 정해 계획에 저장 (이어하기를 위해 남김)
            if (dry.ZoneRetargeted)
            {
                _log.Info($"{g.Name}: previous zone was taken by another file, zone moved" + (dry.JournalArchivedTo is { } a ? $", earlier journal kept as {a}" : string.Empty));
            }

            mark = _env.JournalRows(g); // 저널을 옮겨 보관했을 수 있으므로 계획을 저장한 뒤의 줄 수
            if (dry.TasksTodo == 0)
            {
                _status.Complete(g.Name, g.Source, _env.Now(), counted: false, "already in place");
                _log.Info($"{g.Name}: auto relocation not needed, layout already in place");
                return;
            }

            plan = _env.LoadPlan(g.Plan);
            ReapplyChecker.CheckPaths(plan, g, _env.RealPath);
            _status.BeginRelocation(_env.Now()); // 하루 상한은 끝이 아니라 시작 (이어하기 포함) 으로 셈
            ApplyReport rep = _env.Apply(g, plan, new ApplyOptions(Execute: true), token);
            _log.Info($"{g.Name}: auto relocation moved {rep.MovedBytes} bytes, {rep.Failed.Count} failed" + (rep.Stopped is { } why ? $", stopped: {why}" : string.Empty));
            if (rep.Stopped == "cancelled")
            {
                Note(g, AutoStage.WaitingIdle, "cancelled by game start"); // 저널이 재개 지점이고 이어하기는 기존 재적용 검사가 합니다
            }
            else if (rep.Stopped is not null)
            {
                Note(g, AutoStage.WaitingIdle, "write limit reached, will continue");
            }
            else if (rep.Failed.Count > 0)
            {
                Fail(g, $"{rep.Failed.Count} step(s) failed", null); // 데이터는 그대로이고 남은 이동은 기존 재적용 검사가 이어감
            }
            else
            {
                LayoutReport layout = LayoutVerifier.Verify(_env.LoadPlan(g.Plan), _env.Extents);
                _log.Info($"{g.Name}: auto relocation verified: " + (layout.Contiguous ? "contiguous" : layout.ResumeNeeded ? "resume-needed" : "non-contiguous"));
                _status.Complete(g.Name, g.Source, _env.Now(), counted: true, null);
                _log.Info($"{g.Name}: auto relocation done");
            }
        }
        catch (ShaMismatchException e)
        {
            Fail(g, "data check failed", e);
            Undo(g, mark, token);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Fail(g, "relocation failed: " + e.GetType().Name, e);
            Undo(g, mark, token);
        }
    }

    // 게임이 안 돌 때만, 이번 자동 적용이 저널에 쓴 줄 (mark 줄 뒤) 만 되돌립니다 (최선 노력). 앞선 적용의 줄은 이어하기·수동 되돌리기의 몫이라 건드리지 않음.
    private void Undo(GameSettings g, int mark, CancellationToken token)
    {
        if (_env.AnyRunning(g.ExeNames))
        {
            _log.Warn($"{g.Name}: game is running, revert skipped (journal kept)");
            return;
        }

        try
        {
            RevertReport? r = _env.JournalRows(g) <= mark ? null : _env.Revert(g, mark, token);
            _log.Info(r is null ? $"{g.Name}: nothing to revert" : $"{g.Name}: reverted {r.Restored} piece(s), {r.Skipped.Count} skipped" + (r.Stopped is { } why ? $", stopped: {why}, journal kept" : string.Empty));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _log.Error($"{g.Name}: revert failed, journal kept", e);
        }
    }

    private void Fail(GameSettings g, string detail, Exception? e)
    {
        _status.Set(g.Name, g.Source, null, AutoStage.Failed, detail);
        _log.Error($"{g.Name}: auto relocation stopped: {detail}", e);
    }
}
