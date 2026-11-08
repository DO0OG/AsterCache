using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Trace;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// ORBIT 그림자 계획: 블록 상한, 보정 없음 → baseline, 상태 파일에 추가 필드만, 시간 상한과 예외 시 서비스 유지.
public sealed class OrbitShadowTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_orbit_shadow_" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    public OrbitShadowTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string StatusPath => Path.Combine(_dir, "auto_status.json");

    private static TraceEvent[] Reads(int blocks) =>
        Enumerable.Range(0, blocks).Select(i => new TraceEvent(@"C:\Game\data.pak", i * HotSet.Block, HotSet.Block, 8.0, i)).ToArray();

    [Fact]
    public void HotBlocksAreCappedAtThePlannerLimit()
    {
        PlanningWorld? world = OrbitShadow.BuildWorld(Reads(40), OrbitPlanner.MaxBlocks * HotSet.Block);

        Assert.NotNull(world);
        Assert.Equal(OrbitPlanner.MaxBlocks, world!.Blocks.Count);
    }

    [Fact]
    public void MoreThanTheEventLimitIsRecordedWithoutBuildingAWorld()
    {
        var status = new AutoStatus(StatusPath, _log, () => _now, _ => { }, () => 0);
        status.Set("mw4", "auto", null, AutoStage.Done, null);
        var shadow = new OrbitShadow(status, _log, () => _now);
        var events = new TraceEvent[OrbitShadow.MaxInputEvents + 1];

        Assert.Null(OrbitShadow.BuildWorld(events, 1L << 40));
        OrbitShadowRecord? record = shadow.Evaluate("mw4", events, OrbitShadow.MaxCompute, status.ReplanGeneration("mw4"));
        shadow.Flush();

        Assert.NotNull(record);
        Assert.Equal(("none", "too_many_events", 0, 0), (record!.Selected, record.Reason, record.Blocks, record.Order));
        Assert.Equal("too_many_events", status.Get("mw4")!.OrbitShadow!.Reason);
    }

    [Fact]
    public void MoreThan24BlocksGivesAnEmptyPlan()
    {
        PlanningWorld? world = OrbitShadow.BuildWorld(Reads(40), 1L << 40);

        Assert.NotNull(world);
        OrbitDecision decision = OrbitPlanner.Choose(world);
        Assert.Empty(decision.Plan.Order);
        Assert.Equal("invalid_or_unsafe_input", decision.Reason);
    }

    [Fact]
    public void NoCalibrationRecordsBaselineAndAddsOnlyTheNewField()
    {
        var status = new AutoStatus(StatusPath, _log, () => _now, _ => { }, () => 0);
        status.Set("mw4", "auto", null, AutoStage.Done, null);
        var shadow = new OrbitShadow(status, _log, () => _now);

        OrbitShadowRecord? record = shadow.Evaluate("mw4", Reads(3), OrbitShadow.MaxCompute, status.ReplanGeneration("mw4"));
        shadow.Flush();

        Assert.NotNull(record);
        Assert.Equal(("baseline", "uncalibrated_model", 3), (record!.Selected, record.Reason, record.Blocks));
        Assert.True(record.Order > 0); // 기준 순서가 비면 기록이 무의미함
        var game = (JObject)((JObject)JsonRead.ReadFile(StatusPath)!)["games"]!;
        var entry = (JObject)game["mw4"]!;
        Assert.Equal(AutoStage.Done, entry["stage"]);
        Assert.False(entry.ContainsKey("orbit_shadow"));
        var os = (JObject)entry["shadow_orbit"]!;
        Assert.Equal("baseline", os["selected"]);
        Assert.Equal(false, os["orbit_selected"]);
        Assert.Equal("uncalibrated_model", os["reason"]); // 플래너 사유 문자열 유지, 보정 없음은 별도 필드
        Assert.Equal(true, os["uncalibrated"]);
        Assert.Null(os["worst_loss_ms"]);
        Assert.Null(os["candidate_ratio"]);
        Assert.Equal(3L, os["blocks"]);
        Assert.True((long)os["order"]! > 0);
        Assert.True((long)os["alloc_bytes"]! > 0);
        Assert.Equal("0", os["fingerprint"]); // 재계획이 없었으므로 세대 번호 0
        Assert.Contains("orbit shadow plan", string.Join("\n", _log.Lines));
    }

    [Fact]
    public void TimeCapDiscardsTheResultWithoutRecording()
    {
        var status = new AutoStatus(StatusPath, _log, () => _now, _ => { }, () => 0);
        status.Set("mw4", "auto", null, AutoStage.Done, null);
        var shadow = new OrbitShadow(status, _log, () => _now);

        Assert.Null(shadow.Evaluate("mw4", Reads(3), TimeSpan.Zero, status.ReplanGeneration("mw4")));
        shadow.Flush();

        Assert.Null(status.Get("mw4")!.OrbitShadow);
        Assert.Contains(_log.Lines, line => line.Contains("discarded"));
    }

    [Fact]
    public void BrokenTraceLogsAWarningAndDoesNotThrow()
    {
        var status = new AutoStatus(null, _log, () => _now, _ => { }, () => 0);
        var shadow = new OrbitShadow(status, _log, () => _now);

        Assert.Null(shadow.Evaluate("mw4", [new TraceEvent("x", -1, -1)], OrbitShadow.MaxCompute, status.ReplanGeneration("mw4")));
        Assert.Contains(_log.Lines, line => line.StartsWith("Warn", StringComparison.Ordinal) && line.Contains("failed"));
    }

    [Fact]
    public void MoreThan24BlocksRecordsAReducedPlanInTheStatus()
    {
        var status = new AutoStatus(StatusPath, _log, () => _now, _ => { }, () => 0);
        status.Set("mw4", "auto", null, AutoStage.Done, null);
        var shadow = new OrbitShadow(status, _log, () => _now);

        OrbitShadowRecord? record = shadow.Evaluate("mw4", Reads(40), OrbitShadow.MaxCompute, status.ReplanGeneration("mw4"), 1L << 40); // 예산을 넓혀 25블록 이상 입력을 만듦 (실제 예산은 24블록이라 넘지 않음)
        shadow.Flush();

        Assert.NotNull(record);
        Assert.Equal(("baseline", "uncalibrated_model;reduced(top24_of=40)", 24), (record!.Selected, record.Reason, record.Blocks));
        Assert.True(record.Order > 0);
        var game = (JObject)((JObject)JsonRead.ReadFile(StatusPath)!)["games"]!;
        var entry = (JObject)game["mw4"]!;
        var os = (JObject)entry["shadow_orbit"]!;
        Assert.Equal("baseline", os["selected"]);
        Assert.Equal(24L, os["blocks"]);
    }

    [Fact]
    public void ACancelledTokenDiscardsTheResultAndRecordsNothing()
    {
        var status = new AutoStatus(StatusPath, _log, () => _now, _ => { }, () => 0);
        status.Set("mw4", "auto", null, AutoStage.Done, null);
        var shadow = new OrbitShadow(status, _log, () => _now);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Null(shadow.Evaluate("mw4", Reads(3), OrbitShadow.MaxCompute, status.ReplanGeneration("mw4"), cancel: cancel.Token));
        shadow.Flush();

        Assert.Null(status.Get("mw4")!.OrbitShadow);
        Assert.Contains(_log.Lines, line => line.Contains("discarded") && line.Contains("cancelled"));
    }

    [Fact]
    public void AfterShutdownAScheduledPlanIsCancelledAndRecordsNothing()
    {
        var status = new AutoStatus(StatusPath, _log, () => _now, _ => { }, () => 0);
        status.Set("mw4", "auto", null, AutoStage.Done, null);
        var shadow = new OrbitShadow(status, _log, () => _now);

        shadow.CancelGame("mw4"); // 돌고 있는 계산이 없어도 예외 없음
        shadow.Shutdown();
        shadow.Shutdown(); // 두 번 불러도 안전
        shadow.Schedule("mw4", Reads(3), null, status.ReplanGeneration("mw4"));
        for (int i = 0; i < 500 && !_log.Lines.Any(line => line.Contains("cancelled")); i++)
        {
            Thread.Sleep(10); // 계산은 BelowNormal 스레드에서 돎
        }

        shadow.Flush();
        Assert.Contains(_log.Lines, line => line.Contains("discarded") && line.Contains("cancelled"));
        Assert.Null(status.Get("mw4")!.OrbitShadow);
    }

    [Fact]
    public void CompleteKeepsTheShadowResultAndAnUpdateReplanDropsIt()
    {
        var status = new AutoStatus(StatusPath, _log, () => _now, _ => { }, () => 0);
        status.Complete("mw4", "auto", _now, true, null);
        var shadow = new OrbitShadow(status, _log, () => _now);

        shadow.Evaluate("mw4", Reads(3), OrbitShadow.MaxCompute, status.ReplanGeneration("mw4"));
        shadow.Flush();
        Assert.NotNull(status.Get("mw4")!.OrbitShadow);

        status.Complete("mw4", "auto", _now.AddMinutes(5), true, null); // 완료 (재계획 아님): 결과 유지
        Assert.NotNull(status.Get("mw4")!.OrbitShadow);

        status.ResetForReplan("mw4", "game updated, will trace again on next run"); // 업데이트로 다시 계획: 옛 결과 폐기
        Assert.Null(status.Get("mw4")!.OrbitShadow);
    }

    [Fact]
    public void AReplanOfAnotherGameKeepsThisGamesResult()
    {
        var status = new AutoStatus(StatusPath, _log, () => _now, _ => { }, () => 0);
        status.Complete("mw4", "auto", _now, true, null);
        var shadow = new OrbitShadow(status, _log, () => _now);

        shadow.Evaluate("mw4", Reads(3), OrbitShadow.MaxCompute, status.ReplanGeneration("mw4"));
        status.ResetForReplan("other", "game updated, will trace again on next run"); // 다른 게임의 재계획은 이 게임 결과를 버리지 않음
        shadow.Flush();
        Assert.NotNull(status.Get("mw4")!.OrbitShadow);
    }

    [Fact]
    public void AReplanDuringTheComputationKeepsTheOldResultOut()
    {
        var status = new AutoStatus(StatusPath, _log, () => _now, _ => { }, () => 0);
        status.Complete("mw4", "auto", _now, true, null);
        var shadow = new OrbitShadow(status, _log, () => _now);

        string fingerprint = status.ReplanGeneration("mw4"); // 입력과 함께 확보한 세대를 계산 스레드로 넘김
        OrbitShadowRecord? record = shadow.Evaluate("mw4", Reads(3), OrbitShadow.MaxCompute, fingerprint);
        status.ResetForReplan("mw4", "game updated, will trace again on next run"); // 계산이 끝난 뒤 기록 전에 재계획
        shadow.Flush();
        Assert.Null(status.Get("mw4")!.OrbitShadow);

        status.RecordOrbitShadow("mw4", record!); // 옛 세대로 늦게 도착한 결과도 남기지 않음
        Assert.Null(status.Get("mw4")!.OrbitShadow);
    }

    [Fact]
    public void AReplanAfterInputCaptureDiscardsTheCapturedGeneration()
    {
        var status = new AutoStatus(StatusPath, _log, () => _now, _ => { }, () => 0);
        status.Complete("mw4", "auto", _now, true, null);
        var shadow = new OrbitShadow(status, _log, () => _now);
        string fingerprint = status.ReplanGeneration("mw4"); // 호출자가 이벤트 입력과 함께 확보한 세대
        status.ResetForReplan("mw4", "game updated, will trace again on next run");

        OrbitShadowRecord? record = shadow.Evaluate("mw4", Reads(3), OrbitShadow.MaxCompute, fingerprint);
        shadow.Flush();

        Assert.NotNull(record);
        Assert.Equal("0", record!.Fingerprint);
        Assert.Null(status.Get("mw4")!.OrbitShadow);
    }
}
