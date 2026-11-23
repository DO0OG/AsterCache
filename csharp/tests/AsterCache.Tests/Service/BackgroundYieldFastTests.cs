using System.Text.Json.Nodes;
using AsterCache.Core.Settings;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 배경 양보의 빠른 경로: 게임이 도는 동안 새로 뜬 프로세스를 번호 목록의 차이로 알아채 약 3 초 안에 기존 선택·낮추기를 적용합니다 (가짜 세계는 YieldWorld).
public sealed class BackgroundYieldFastTests
{
    private const long Mib = YieldWorld.Mib;
    private const long Second = 10_000_000; // FILETIME 한 단위는 100ns
    private readonly ListLog _log = new();
    private readonly YieldWorld _w = new();
    private AsterSettings _settings = new(1000, [Games.Make("cod", "cod.exe") with { Prewarm = PrewarmSettings.Default with { TriggerExes = ["codlauncher.exe"] } }]);

    private bool Logged(string text) => _log.Lines.Any(l => l.Contains(text, StringComparison.Ordinal));

    // 게임 (cod.exe) 이 떠 있는 채 세션이 시작된 배경 양보. 그 전에 쉬는 표본이 쌓여 있어 새로 뜨는 프로세스만 쓰는 중으로 판정됨 (표본에 없음).
    private BackgroundYield Running()
    {
        _ = _w.Add(10, "cod.exe");
        BackgroundYield y = _w.Yielder(() => _settings, _log);
        y.Begin("game started", settle: false);
        return y;
    }

    private void Check(BackgroundYield y, int times)
    {
        for (int i = 0; i < times; i++)
        {
            y.FastTick();
        }
    }

    [Fact]
    public void ANewProcessIsLoweredAtTheSecondCheckSoWithinThreeSecondsAndNeverTrimmed()
    {
        BackgroundYield y = Running();
        y.FastTick(); // 이미 떠 있던 프로세스는 기준
        _ = _w.Add(60, "browser.exe", wsMib: 300);

        y.FastTick();
        Assert.Empty(_w.Sets); // 처음 보인 점검: 번호만 기억하고 프로세스를 열지 않음
        Assert.DoesNotContain(60, _w.Touched);

        y.FastTick();
        Assert.Equal([(60, 4)], _w.Sets); // 새 프로세스는 아직 쓰는 중인지 모르니 보통보다 낮음
        Assert.Equal([(60, 1)], _w.IoSets);
        Assert.Empty(_w.Trimmed); // 작업 집합은 비우지 않음
        Assert.True(2 * BackgroundYield.FastEvery <= TimeSpan.FromSeconds(3));
        Assert.Contains("\"pid\": 60", _w.State, StringComparison.Ordinal); // 복원용 상태 기록은 기존 경로
        Assert.Equal(1, y.AppliedTotal);
    }

    [Fact]
    public void WithNoNewProcessTheFastCheckOnlyListsProcessNumbers()
    {
        BackgroundYield y = Running();
        int lists = _w.ProcessListCalls;

        Check(y, 6);

        Assert.Equal(lists, _w.ProcessListCalls); // 이름 목록 (전체 조회) 은 새 프로세스가 있을 때만
        Assert.Equal(6, _w.ProcessIdCalls);
        Assert.Empty(_w.Touched);
    }

    [Fact]
    public void TheSameExclusionRulesApplyAndProtectedProcessesAreNeverOpenedForWriting()
    {
        _w.SelfPid = 19;
        BackgroundYield y = Running();
        y.FastTick();
        _ = _w.Add(11, "codlauncher.exe"); // 트리거 프로그램
        _ = _w.Add(12, "cod.exe"); // 게임 실행 파일 하나 더
        _ = _w.Add(13, "steam.exe");
        _ = _w.Add(14, "vgtray.exe");
        _ = _w.Add(15, "EasyAntiCheat_EOS.exe");
        _ = _w.Add(16, "AsterCache.Tray.exe");
        _ = _w.Add(17, "service.exe", session: 0);
        _ = _w.Add(19, "self.exe");
        _ = _w.Add(30, "a.exe", path: @"C:\Windows\System32\a.exe"); // Windows 폴더: 경로만 조회
        _ = _w.Add(31, "b.exe", path: @"D:\Games\cod\bin\b.exe"); // 게임 폴더
        _ = _w.Add(32, "c.exe", path: @"C:\Program Files\EasyAntiCheat\c.exe");
        _ = _w.Add(20, "chat.exe");

        Check(y, 3);

        int[] protectedPids = [11, 12, 13, 14, 15, 16, 17, 19];
        Assert.DoesNotContain(_w.Touched, pid => protectedPids.Contains(pid)); // 이름·세션 단계에서 걸러져 어떤 권한으로도 열지 않음
        Assert.DoesNotContain(_w.Sets.Concat(_w.IoSets), s => s.Pid is 30 or 31 or 32); // 경로 규칙은 조회까지만
        Assert.Equal([(20, 4)], _w.Sets);
        Assert.Equal([(20, 1)], _w.IoSets);
        Assert.Equal([30, 31, 32, 20], _w.Described);
    }

    [Fact]
    public void AProcessIsNotLoweredTwiceAndAShortLivedOneIsNeverOpenedNorWrittenToTheStateFile()
    {
        BackgroundYield y = Running();
        y.FastTick();
        _ = _w.Add(60, "short.exe");
        _ = _w.Add(61, "stays.exe");
        y.FastTick();
        _ = _w.Procs.Remove(60); // 한 점검만 보이고 끝남

        Check(y, 5);

        Assert.DoesNotContain(60, _w.Touched);
        Assert.Equal([(61, 4)], _w.Sets);
        Assert.Equal([(61, 1)], _w.IoSets);
        Assert.DoesNotContain("\"pid\": 60", _w.State, StringComparison.Ordinal);
        Assert.Equal(1, y.AppliedTotal);
    }

    [Fact]
    public void AReusedProcessNumberIsLoweredAgainAsANewProcessAndTheOldOneIsNotMistakenForIt()
    {
        BackgroundYield y = Running();
        y.FastTick();
        _ = _w.Add(60, "first.exe");
        Check(y, 2);
        Assert.Equal([(60, 4)], _w.Sets);

        _ = _w.Procs.Remove(60);
        y.FastTick(); // 끝난 번호는 잊음
        YieldWorld.Proc reused = _w.Add(60, "second.exe");
        reused.Created = 5000; // 같은 번호를 다른 프로세스가 이어받음
        Check(y, 2);

        Assert.Equal([(60, 4), (60, 4)], _w.Sets);
        Assert.Equal(2, y.AppliedTotal);
    }

    [Fact]
    public void WhenTheGameEndsEverythingTheFastPathLoweredIsRestoredAndLaterProcessesAreLeftAlone()
    {
        BackgroundYield y = Running();
        y.FastTick();
        _ = _w.Add(60, "browser.exe");
        Check(y, 2);
        Assert.Equal(4, _w.Procs[60].Priority);
        Assert.Equal(1, _w.Procs[60].Io);

        y.End();

        Assert.Equal((5, 2), (_w.Procs[60].Priority, _w.Procs[60].Io));
        Assert.Null(_w.State);
        _ = _w.Add(61, "late.exe");
        Check(y, 3);
        Assert.DoesNotContain(61, _w.Touched);
    }

    [Fact]
    public void ARestartedServiceRestoresWhatTheFastPathLoweredFromTheStateFile()
    {
        BackgroundYield y = Running();
        y.FastTick();
        _ = _w.Add(60, "browser.exe");
        Check(y, 2);
        Assert.Equal(4, _w.Procs[60].Priority);

        _w.Yielder(() => _settings, _log).Recover(); // 서비스가 비정상 종료한 뒤의 시작

        Assert.Equal((5, 2), (_w.Procs[60].Priority, _w.Procs[60].Io));
        Assert.Null(_w.State);
    }

    [Fact]
    public void TurningOffTheFastSettingOrTheBackgroundYieldStopsTheFastPath()
    {
        BackgroundYield y = Running();
        y.FastTick();
        _settings = _settings with { Memory = _settings.Memory with { BackgroundYieldFast = false } };
        _ = _w.Add(60, "browser.exe");

        Check(y, 3);

        Assert.DoesNotContain(60, _w.Touched);
        Assert.Equal(1, _w.ProcessIdCalls); // 꺼져 있으면 번호 목록도 읽지 않음 (처음 기준 점검의 한 번만)

        _settings = _settings with { Memory = _settings.Memory with { BackgroundYieldFast = true, BackgroundYield = false } };
        Check(y, 3);

        Assert.DoesNotContain(60, _w.Touched);
    }

    [Fact]
    public void AChildThatInheritedBelowNormalFromAnInUseParentIsRecordedAndRestoredAtTheEnd()
    {
        _ = _w.Add(50, "busy.exe", wsMib: 300);
        _ = _w.Add(10, "cod.exe");
        BackgroundYield y = _w.Yielder(() => _settings, _log);
        _w.Procs[50].CpuTicks = 60 * Second / 10; // 한 코어의 10%: 쓰는 중
        y.Begin("game started", settle: false);
        Assert.Equal(4, _w.Procs[50].Priority);
        y.FastTick();

        YieldWorld.Proc child = _w.Add(70, "worker.exe", priority: 4); // 부모가 낮춘 뒤에 뜬 자식: 4 와 I/O 낮음을 물려받음
        (child.Io, child.Parent, child.Created) = (1, 50, _w.Start.ToFileTime() + (20 * Second));
        Check(y, 2);
        Assert.DoesNotContain(_w.Sets, s => s.Pid == 70); // 이미 낮은 값이라 새로 쓰지 않음
        Assert.Contains("\"pid\": 70", _w.State, StringComparison.Ordinal);

        y.End();

        Assert.Equal((5, 2), (_w.Procs[70].Priority, _w.Procs[70].Io)); // 부모의 원래 값으로
        Assert.Equal(5, _w.Procs[50].Priority);
    }

    [Fact]
    public void TheMaintenanceLogLineCarriesTheFastPathCountsAndFailuresOnlyWhenThereAreValues()
    {
        BackgroundYield y = Running();
        _w.Now = _w.Start + TimeSpan.FromMinutes(2);
        y.Tick(_w.Snapshot());
        Assert.False(Logged("background yield maintenance")); // 값이 없으면 줄을 남기지 않음

        _ = _w.Add(60, "browser.exe");
        _ = _w.Add(61, "unreadable.exe");
        _ = _w.Add(62, "svc.exe", session: 0);
        _ = _w.DescribeFails.Add(61); // 경로를 읽지 못함
        _ = _w.SetFails.Add(60); // 메모리 우선순위 쓰기 실패
        y.FastTick();
        Check(y, 2);
        _w.Now = _w.Start + TimeSpan.FromMinutes(4);
        y.Tick(_w.Snapshot());

        Assert.True(Logged("background yield maintenance in 120 s: fast path saw 3 new processes (2 not eligible), memory priority lowered for 0, set below normal for 0 in-use, switched for 0, I/O priority lowered for 1, CPU priority lowered for 0, 2 failed"), string.Join(" | ", _log.Lines));
    }

    private static MemorySettings ParseMemory(string memory)
    {
        var root = new JsonObject
        {
            ["games"] = new JsonArray(new JsonObject
            {
                ["name"] = "mw4",
                ["exe_names"] = new JsonArray("game.exe"),
                ["trace"] = @"C:\ProgramData\AsterCache\plans\mw4.prefetch.json",
                ["plan"] = @"C:\ProgramData\AsterCache\plans\mw4.plan.json",
                ["origin_root"] = @"D:\Games\MW4",
                ["allowed_roots"] = new JsonArray(@"D:\Games"),
                ["volume_label"] = "GAMES",
            }),
            ["memory"] = JsonNode.Parse(memory),
        };
        return SettingsStore.Parse(root.ToJsonString(), @"C:\ProgramData\AsterCache\plans").Memory;
    }

    [Fact]
    public void TheTwoSwitchesDefaultToOnAreReadIndependentlyAndRejectBadValues()
    {
        MemorySettings defaults = ParseMemory("{}");
        Assert.True(defaults.BackgroundYieldFast);
        Assert.True(defaults.BackgroundInUseMemoryPriority);
        Assert.False(ParseMemory("""{"background_yield_fast":false}""").BackgroundYieldFast);
        Assert.True(ParseMemory("""{"background_yield_fast":false}""").BackgroundInUseMemoryPriority);
        Assert.False(ParseMemory("""{"background_in_use_memory_priority":false}""").BackgroundInUseMemoryPriority);
        Assert.True(ParseMemory("""{"background_in_use_memory_priority":false}""").BackgroundYieldFast);
        Assert.Throws<SettingsException>(() => ParseMemory("""{"background_yield_fast":"yes"}"""));
        Assert.Throws<SettingsException>(() => ParseMemory("""{"background_in_use_memory_priority":1}"""));
    }

    [Fact]
    public void WithBothSwitchesOffTheBehaviorIsTheOneBeforeBusyProcessesGetNothingInMemoryAndNewOnesWaitForTheMaintenance()
    {
        _settings = _settings with { Memory = _settings.Memory with { BackgroundYieldFast = false, BackgroundInUseMemoryPriority = false } };
        BackgroundYield y = Running();
        y.FastTick();
        _ = _w.Add(60, "browser.exe");

        Check(y, 3);
        Assert.Empty(_w.Touched);
        _w.Procs[60].CpuTicks = 60 * Second / 10; // 한 코어의 10%: 쓰는 중
        _w.Now = _w.Start + TimeSpan.FromMinutes(2);
        y.Tick(_w.Snapshot()); // 2 분 유지 회차에 비로소 선택됨: 쓰는 중이라 메모리 우선순위는 그대로 (I/O 만 낮춤)

        Assert.Empty(_w.Sets);
        Assert.Equal([(60, 1)], _w.IoSets);
    }

    [Fact]
    public void TheFastPathLogIsFlushedWhenTheSessionEnds()
    {
        BackgroundYield y = Running();
        y.FastTick();
        _ = _w.Add(60, "browser.exe");
        Check(y, 2);

        y.End();

        Assert.True(Logged("background yield maintenance: fast path saw 1 new processes (0 not eligible)"));
    }

    // 쓰고 있는 프로세스 (50, 한 코어의 10%) 가 있는 세션: 시작에서 보통보다 낮음 (4) 을 받음.
    private BackgroundYield RunningWithBusyProcess()
    {
        _ = _w.Add(50, "busy.exe", wsMib: 300);
        _ = _w.Add(10, "cod.exe");
        BackgroundYield y = _w.Yielder(() => _settings, _log);
        _w.Procs[50].CpuTicks = 60 * Second / 10;
        y.Begin("game started", settle: false);
        Assert.Equal(4, _w.Procs[50].Priority);
        return y;
    }

    [Fact]
    public void ASameNamedProcessThatTookOverTheNumberIsNeverWrittenByTheMaintenanceSwitch()
    {
        BackgroundYield y = RunningWithBusyProcess();
        YieldWorld.Proc taker = _w.Add(50, "busy.exe"); // 낮춘 프로세스가 끝나고 같은 이름의 다른 프로세스가 같은 번호를 이어받음 (경로로만 보호되는 보조 프로세스일 수도 있음)
        taker.Created = 5000;
        int sets = _w.Sets.Count;
        int described = _w.Described.Count;

        _w.Now = _w.Start + TimeSpan.FromMinutes(2);
        y.Tick(_w.Snapshot()); // 이어받은 프로세스는 쉼: 전환 대상이지만 쓰기 전에 생성 시각이 다름을 확인

        Assert.Equal(sets, _w.Sets.Count); // 쓰기 호출 0
        Assert.Equal(5, taker.Priority);
        Assert.True(_w.Described.Count > described); // 확인은 조회 권한으로만
        Assert.DoesNotContain("\"pid\": 50", _w.State ?? string.Empty, StringComparison.Ordinal); // 그 항목은 버림
    }

    [Fact]
    public void ADeferredProcessWhoseNumberWasTakenOverIsNotWrittenEither()
    {
        _settings = _settings with { Memory = _settings.Memory with { BackgroundInUseMemoryPriority = false } };
        BackgroundYield y = Running();
        y.FastTick();
        _ = _w.Add(60, "browser.exe");
        Check(y, 2);
        Assert.Empty(_w.Sets); // 새 프로세스는 쓰는 중으로 보여 메모리 쪽을 건너뜀
        _w.Add(60, "browser.exe").Created = 5000; // 같은 이름의 다른 프로세스가 번호를 이어받음

        _w.Now = _w.Start + TimeSpan.FromMinutes(2);
        y.Tick(_w.Snapshot());

        Assert.Empty(_w.Sets);
        Assert.Equal(5, _w.Procs[60].Priority);
    }

    [Fact]
    public void WithTheInUseSettingOffANewProcessThatTurnsOutIdleGetsTheLowestPriorityAtTheNextMaintenanceLikeBefore()
    {
        _settings = _settings with { Memory = _settings.Memory with { BackgroundInUseMemoryPriority = false } };
        BackgroundYield y = Running();
        y.FastTick();
        _ = _w.Add(60, "browser.exe");
        Check(y, 2);
        Assert.Empty(_w.Sets); // 처음엔 쓰는 중으로 보여 메모리 쪽은 건너뛰고 I/O 만 낮춤
        Assert.Equal([(60, 1)], _w.IoSets);

        _w.Now = _w.Start + TimeSpan.FromMinutes(2);
        y.Tick(_w.Snapshot()); // 쉬는 것으로 판정됨

        Assert.Equal([(60, 1)], _w.Sets);
        Assert.Contains("\"priority\": 5", _w.State, StringComparison.Ordinal);
        y.End();
        Assert.Equal(5, _w.Procs[60].Priority);
    }

    [Fact]
    public void WithTheInUseSettingOffANewProcessThatStaysBusyKeepsItsMemoryPriority()
    {
        _settings = _settings with { Memory = _settings.Memory with { BackgroundInUseMemoryPriority = false } };
        BackgroundYield y = Running();
        y.FastTick();
        _ = _w.Add(60, "browser.exe");
        Check(y, 2);
        _w.Procs[60].CpuTicks = 60 * Second / 10;

        _w.Now = _w.Start + TimeSpan.FromMinutes(2);
        y.Tick(_w.Snapshot());

        Assert.Empty(_w.Sets);
    }

    [Fact]
    public void TheStateFileRecordsTheSetValueOnlyWhenItIsNotOneAndARestartFindsAChildThatInheritedBelowNormal()
    {
        _ = RunningWithBusyProcess();
        Assert.Contains("\"applied\": 4", _w.State, StringComparison.Ordinal);
        YieldWorld.Proc child = _w.Add(70, "worker.exe", priority: 4); // 마지막 기록 뒤에 뜬 자식: 부모의 4 와 I/O 낮음을 물려받음
        (child.Io, child.Parent, child.Created) = (1, 50, _w.Start.ToFileTime() + (20 * Second));

        _w.Yielder(() => _settings, _log).Recover(); // 비정상 종료 뒤의 시작

        Assert.Equal((5, 2), (_w.Procs[70].Priority, _w.Procs[70].Io));
        Assert.Equal(5, _w.Procs[50].Priority);
        Assert.Null(_w.State);
    }

    [Fact]
    public void AnIdleEntryHasNoAppliedKeySoTheFileStaysTheSameAsBeforeForTheLowestPriority()
    {
        _ = _w.Add(10, "cod.exe");
        _ = _w.Add(60, "idle.exe");
        BackgroundYield y = _w.Yielder(() => _settings, _log);

        y.Begin("game started", settle: false);

        Assert.Equal(1, _w.Procs[60].Priority);
        Assert.DoesNotContain("applied", _w.State, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFastLoopKeepsWorkingWhenTheSessionEndsTwiceBecauseAnEntryCouldNotBeRestored()
    {
        _ = _w.Add(60, "idle.exe");
        _ = _w.Add(10, "cod.exe");
        BackgroundYield y = _w.Yielder(() => _settings, _log);
        y.FastInterval = TimeSpan.FromMilliseconds(10);
        using var stop = new CancellationTokenSource();
        Task loop = y.RunFastAsync(stop.Token);
        try
        {
            y.Begin("game started", settle: false);
            _ = _w.SetFails.Add(60); // 되돌리기에 실패해 항목이 남음
            y.End();
            await Task.Delay(100);
            y.End(); // 세션 밖에서 다시 끝남: 루프가 기다리는 신호를 바꾸면 안 됨
            await Task.Delay(100);
            _ = _w.SetFails.Remove(60);
            int before = _w.ProcessIdCalls;

            y.Begin("game started again", settle: false);

            DateTime until = DateTime.UtcNow.AddSeconds(10);
            while (_w.ProcessIdCalls < before + 3 && DateTime.UtcNow < until)
            {
                await Task.Delay(20);
            }

            Assert.True(_w.ProcessIdCalls >= before + 3, "다음 세션에서 루프가 점검을 다시 불러야 함");
        }
        finally
        {
            await stop.CancelAsync();
            await loop;
        }
    }

    [Fact]
    public void RecordsOfEndedProcessesAreDroppedAtTheMaintenanceAndTheStateFileIsWrittenOnlyForNewEntries()
    {
        BackgroundYield y = Running();
        y.FastTick();
        _ = _w.Add(60, "short-lived.exe");
        Check(y, 2);
        Assert.Contains("\"pid\": 60", _w.State, StringComparison.Ordinal);
        _ = _w.Procs.Remove(60);
        int writes = _w.StateWrites;

        Check(y, 5); // 새 프로세스가 없으면 쓰지 않음
        _w.Now = _w.Start + TimeSpan.FromMinutes(2);
        y.Tick(_w.Snapshot()); // 끝난 항목을 치움 (치우기만으로는 쓰지 않음)
        Assert.Equal(writes, _w.StateWrites);

        _ = _w.Add(61, "next.exe");
        Check(y, 2);

        Assert.Equal(writes + 1, _w.StateWrites);
        Assert.Contains("\"pid\": 61", _w.State, StringComparison.Ordinal);
        Assert.DoesNotContain("\"pid\": 60", _w.State, StringComparison.Ordinal);
    }
}
