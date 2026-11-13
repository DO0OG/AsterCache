using System.Diagnostics;
using System.Text.Json.Nodes;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tests.Windows;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 가짜 세계: 프로세스 표와 호출 기록, 시계, 가용 메모리, 상태 파일. 조회·쓰기 권한 호출이 어느 pid 로 갔는지 남깁니다.
internal sealed class YieldWorld
{
    public const long Mib = 1L << 20;

    public sealed class Proc
    {
        public string Exe = string.Empty;
        public uint Session = 1;
        public string Path = @"C:\Users\u\AppData\Local\Programs\app\app.exe";
        public long Ws = 10 * Mib;
        public long Created = 1000;
        public int Priority = 5;
    }

    public Dictionary<int, Proc> Procs { get; } = [];

    public List<int> Described { get; } = [];

    public List<int> Trimmed { get; } = [];

    public List<(int Pid, int Priority)> Sets { get; } = [];

    public List<int> PriorityReads { get; } = [];

    public int ProcessListCalls;

    public DateTimeOffset Start { get; } = DateTimeOffset.UnixEpoch.AddDays(1);

    public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch.AddDays(1);

    public Func<long> Avail { get; set; } = () => 10000;

    /// 대기 목록 조회 결과 (null 이면 조회 실패).
    public MemoryLists? ListsNow { get; set; }

    /// 비우기 한 번에 걸리는 시간 (가짜 시계가 그만큼 흐름).
    public TimeSpan TrimCost { get; set; }

    public long PhysicalRam { get; set; } = 32L * 1024 * Mib;

    public string? State { get; set; }

    /// 우선순위 쓰기가 실패하는 pid (같은 프로세스인데 쓰기 실패를 흉내).
    public HashSet<int> SetFails { get; } = [];

    public bool WriteFails { get; set; }

    public IReadOnlyList<string> KnownFolders { get; set; } = [];

    public int SelfPid { get; set; } = -1;

    public Proc Add(int pid, string exe, uint session = 1, string? path = null, long wsMib = 10, int priority = 5) =>
        Procs[pid] = new Proc { Exe = exe, Session = session, Path = path ?? @"C:\Users\u\AppData\Local\Programs\app\" + exe, Ws = wsMib * Mib, Priority = priority };

    /// 조회·쓰기·우선순위 읽기가 닿은 모든 pid (제외 대상은 여기에 한 번도 없어야 함).
    public IEnumerable<int> Touched => Described.Concat(Trimmed).Concat(Sets.Select(s => s.Pid)).Concat(PriorityReads).Distinct();

    public IReadOnlyList<(int Pid, string Exe)> Snapshot() => [.. Procs.Select(p => (p.Key, p.Value.Exe))];

    public BackgroundYieldEnv Env() => new()
    {
        Processes = () =>
        {
            ProcessListCalls++;
            return Snapshot();
        },
        SessionId = pid => Procs.TryGetValue(pid, out Proc? p) ? p.Session : null,
        Describe = pid =>
        {
            Described.Add(pid);
            return Procs.TryGetValue(pid, out Proc? p) ? new ProcessInfo(p.Path, p.Ws, p.Created) : null;
        },
        Trim = pid =>
        {
            Trimmed.Add(pid);
            if (!Procs.TryGetValue(pid, out Proc? p))
            {
                return false;
            }

            p.Ws = Math.Min(p.Ws, 20 * Mib); // 비우면 작업 집합이 줄어듦
            Now += TrimCost;
            return true;
        },
        MemoryPriority = pid =>
        {
            PriorityReads.Add(pid);
            return Procs.TryGetValue(pid, out Proc? p) ? p.Priority : null;
        },
        SetMemoryPriority = (pid, value) =>
        {
            Sets.Add((pid, value));
            if (SetFails.Contains(pid) || !Procs.TryGetValue(pid, out Proc? p))
            {
                return false;
            }

            p.Priority = value;
            return true;
        },
        AvailableMib = () => Avail(),
        Lists = () => ListsNow,
        PhysicalBytes = () => PhysicalRam,
        Now = () => Now,
        Delay = (t, _) => Now += t,
        ReadState = () => State,
        WriteState = text => State = WriteFails ? throw new IOException("disk full") : text,
        DeleteState = () => State = null,
        KnownGameFolders = () => KnownFolders,
        WindowsDir = @"C:\Windows",
        SelfPid = SelfPid,
    };
}

/// 배경 메모리 양보: 대상 선택, 세션 수명, 상태 파일, 설정, 서비스 연결.
public sealed class BackgroundYieldTests
{
    private const long Mib = YieldWorld.Mib;
    private readonly ListLog _log = new();
    private readonly YieldWorld _w = new();
    private AsterSettings _settings = new(1000, [Cod()]);

    private static GameSettings Cod() => Games.Make("cod", "cod.exe") with { Prewarm = PrewarmSettings.Default with { TriggerExes = ["codlauncher.exe"] } };

    private BackgroundYield Make() => new(() => _settings, _w.Env(), _log);

    private bool Logged(string text) => _log.Lines.Any(l => l.Contains(text, StringComparison.Ordinal));

    [Fact]
    public void GameLauncherHelperAntiCheatSessionZeroAndSelfAreNeverOpened()
    {
        _w.SelfPid = 19;
        _ = _w.Add(4, "System");
        _ = _w.Add(10, "cod.exe");
        _ = _w.Add(11, "codlauncher.exe"); // 트리거 exe
        _ = _w.Add(12, "steam.exe");
        _ = _w.Add(13, "steamwebhelper.exe");
        _ = _w.Add(14, "vgtray.exe");
        _ = _w.Add(15, "EasyAntiCheat_EOS.exe");
        _ = _w.Add(16, "RiotClientUx.exe");
        _ = _w.Add(17, "AsterCache.Tray.exe");
        _ = _w.Add(18, "service.exe", session: 0);
        _ = _w.Add(19, "self.exe");
        _ = _w.Add(20, "chat.exe", wsMib: 500);

        Make().Begin("test", settle: false);

        Assert.Equal([20], _w.Touched);
        Assert.Equal([20], _w.Trimmed);
        Assert.Equal([(20, 1)], _w.Sets);
    }

    [Fact]
    public void WindowsFolderGameFoldersAndLauncherLibraryFoldersAreOnlyDescribedNeverWritten()
    {
        _ = _w.Add(30, "a.exe", path: @"C:\Windows\System32\a.exe", wsMib: 300);
        _ = _w.Add(31, "b.exe", path: @"D:\Games\cod\bin\b.exe", wsMib: 300);
        _ = _w.Add(32, "c.exe", path: @"D:\Games\other\c.exe", wsMib: 300); // 허용 루트 아래
        _ = _w.Add(33, "d.exe", path: @"C:\Program Files (x86)\Steam\bin\d.exe", wsMib: 300);
        _ = _w.Add(34, "e.exe", path: @"E:\SteamLibrary\steamapps\common\x\e.exe", wsMib: 300);
        _ = _w.Add(35, "f.exe", path: @"C:\Program Files\Riot Vanguard\f.exe", wsMib: 300);
        _ = _w.Add(36, "g.exe", path: @"C:\Program Files\EasyAntiCheat\g.exe", wsMib: 300);
        _ = _w.Add(40, "ok.exe", path: @"C:\Users\u\ok.exe", wsMib: 300);

        Make().Begin("test", settle: false);

        Assert.True(Enumerable.Range(30, 7).All(_w.Described.Contains)); // 경로 확인 (조회 권한) 까지만
        Assert.Equal([40], _w.Trimmed);
        Assert.Equal([40], _w.Sets.Select(s => s.Pid));
        Assert.Equal([40], _w.PriorityReads);
    }

    [Fact]
    public void BeginLowersPriorityAndRemembersItTrimsOnlyLargeProcessesAndIgnoresARepeatWithinSixtySeconds()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _ = _w.Add(51, "small.exe", wsMib: 10);
        _ = _w.Add(52, "low.exe", wsMib: 400, priority: 1); // 이미 가장 낮음: 건드리지 않음
        BackgroundYield y = Make();

        y.Begin("trigger", settle: false);

        Assert.Equal([(50, 1), (51, 1)], _w.Sets);
        Assert.Equal([50, 52], _w.Trimmed); // 32 MiB 이상만
        Assert.True(Logged("background yield (trigger): trimmed 2 processes (900 MiB working set), memory priority lowered for 2, available 10000 MiB"));

        _w.Now += TimeSpan.FromSeconds(30);
        _ = _w.Add(53, "late.exe", wsMib: 300);
        y.Begin("game started", settle: false);
        Assert.Equal(2, _w.Sets.Count); // 60초 안의 두 번째 호출은 무시
        Assert.Equal(2, _w.Trimmed.Count);

        _w.Now += TimeSpan.FromSeconds(31);
        y.Begin("game started", settle: false);
        Assert.Equal([(50, 1), (51, 1), (53, 1)], _w.Sets); // 이미 낮춘 것은 다시 안 함
    }

    [Fact]
    public void SettleWaitEndsEarlyWhenAvailableMemoryCalmsDown()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        var readings = new Queue<long>([1000, 1000, 3000, 3010, 3020]);
        long last = 0;
        _w.Avail = () => last = readings.Count > 0 ? readings.Dequeue() : last;

        Make().Begin("trigger", settle: true);

        Assert.True(Logged("available 1000 -> 3020 MiB"));
        Assert.Equal(TimeSpan.FromMilliseconds(1500), _w.Now - _w.Start); // 연속 두 번 64 MiB 미만 증가
    }

    [Fact]
    public void SettleWaitEndsAtThreeSecondsWhenAvailableMemoryKeepsGrowing()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        long grow = 0;
        _w.Avail = () => grow += 1000;

        Make().Begin("trigger", settle: true);

        Assert.Equal(TimeSpan.FromSeconds(3), _w.Now - _w.Start);
        Assert.True(Logged("available 1000 -> 8000 MiB"));
    }

    [Fact]
    public void TickEndsTheSessionTenMinutesAfterTheLastGameActivityAndRestoresPriority()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        BackgroundYield y = Make();
        y.Begin("trigger", settle: false);

        _w.Now += TimeSpan.FromMinutes(9);
        y.Tick([(50, "big.exe")]);
        Assert.Single(_w.Sets);

        _w.Now += TimeSpan.FromMinutes(1);
        y.Tick([(50, "big.exe")]);
        Assert.Equal([(50, 1), (50, 5)], _w.Sets);
        Assert.Null(_w.State);
        Assert.True(Logged("background yield ended: memory priority restored for 1 processes"));
    }

    [Fact]
    public void WhileAGameRunsTickKeepsTheSessionAndLowersNewProcessesEveryTwoMinutes()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        BackgroundYield y = Make();
        y.Begin("trigger", settle: false);
        _ = _w.Add(10, "cod.exe");

        for (int minute = 1; minute <= 12; minute++)
        {
            _w.Now += TimeSpan.FromMinutes(1);
            if (minute == 1)
            {
                _ = _w.Add(60, "newcomer.exe");
            }

            y.Tick(_w.Snapshot());
            Assert.Equal(minute >= 2 ? 2 : 1, _w.Sets.Count); // 2분 유지 주기에 새로 뜬 프로세스만 낮춤
        }

        Assert.DoesNotContain(_w.Sets, s => s.Priority == 5); // 10분이 지나도 게임이 있어 끝나지 않음
        _ = _w.Procs.Remove(10);
        int ended = -1;
        for (int minute = 13; minute <= 24 && ended < 0; minute++)
        {
            _w.Now += TimeSpan.FromMinutes(1);
            y.Tick(_w.Snapshot());
            ended = _w.Sets.Any(s => s.Priority == 5) ? minute : -1;
        }

        Assert.Equal(22, ended); // 마지막 게임 관측 (12분) 뒤 10분
    }

    private static MemoryLists Lists(long headroomMib, long repurposedMib = 0) => new(headroomMib * Mib, repurposedMib * Mib);

    // 가용 게임 실행 중인 세션: big.exe (500 MiB) 가 시작에서 비워진 상태. 조회 결과는 _w.ListsNow.
    private BackgroundYield RunningSession(MemoryLists? lists)
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _ = _w.Add(10, "cod.exe");
        _w.ListsNow = lists;
        BackgroundYield y = Make();
        y.Begin("trigger", settle: false);
        return y;
    }

    private void TickAt(BackgroundYield y, int minute, long wsMib, MemoryLists? lists)
    {
        _w.Now = _w.Start + TimeSpan.FromMinutes(minute);
        _w.Procs[50].Ws = wsMib * Mib;
        _w.ListsNow = lists;
        y.Tick(_w.Snapshot());
    }

    [Fact]
    public void StartDoesNotTrimWhenHeadroomReachesTheTargetButStillLowersPriorityAndSkipsTheSettleWait()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _w.ListsNow = Lists(8192); // 물리 RAM 32 GiB 의 1/4 = 8 GiB

        Make().Begin("trigger", settle: true);

        Assert.Empty(_w.Trimmed);
        Assert.Equal([(50, 1)], _w.Sets);
        Assert.Equal(_w.Start, _w.Now); // 가라앉기 대기 없음
        Assert.True(Logged("background yield (trigger): memory priority lowered for 1, no trim needed (headroom 8192 MiB, available 10000 MiB)"));
    }

    [Fact]
    public void StartTrimsWhenHeadroomIsBelowTheTargetAndTheFloorIsFourGibOnSmallMachines()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _w.ListsNow = Lists(8191);
        Make().Begin("trigger", settle: false);
        Assert.Equal([50], _w.Trimmed);
        Assert.True(Logged("trimmed 1 processes (500 MiB working set), memory priority lowered for 1, available 10000 MiB, headroom 8191 MiB"));

        _w.Trimmed.Clear();
        _w.Procs[50].Ws = 500 * Mib;
        _w.Now += TimeSpan.FromMinutes(5);
        (_w.PhysicalRam, _w.ListsNow) = (8L * 1024 * Mib, Lists(4096)); // RAM/4 = 2 GiB 라 하한 4 GiB 가 목표
        Make().Begin("trigger", settle: false);
        Assert.Empty(_w.Trimmed);
        Assert.True(Logged("no trim needed (headroom 4096 MiB"));
    }

    [Fact]
    public void ASecondBeginRightAfterAStartWithoutATrimDoesNotWaitToSettleButOneAfterATrimDoes()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _w.ListsNow = Lists(8192);
        BackgroundYield y = Make();
        y.Begin("trigger", settle: false);
        _w.Now += TimeSpan.FromSeconds(1);

        y.Begin("game started", settle: true);

        Assert.Equal(_w.Start + TimeSpan.FromSeconds(1), _w.Now); // 비운 결과가 없으니 가라앉기를 기다리지 않음

        _w.Now += TimeSpan.FromMinutes(5);
        _w.ListsNow = Lists(1000);
        y.End();
        y.Begin("trigger", settle: false);
        DateTimeOffset afterTrim = _w.Now += TimeSpan.FromSeconds(1);

        y.Begin("game started", settle: true);

        Assert.True(_w.Now > afterTrim); // 비운 직후라면 같이 기다림
    }

    [Fact]
    public void StartTrimsWhenTheMemoryListsCannotBeReadAndWarnsOnlyOncePerSession()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _ = _w.Add(10, "cod.exe");
        BackgroundYield y = Make();

        y.Begin("trigger", settle: false);

        Assert.Equal([50], _w.Trimmed);
        Assert.True(Logged("trimmed 1 processes (500 MiB working set), memory priority lowered for 1, available 10000 MiB"));
        TickAt(y, 2, 500, null);
        TickAt(y, 4, 500, null);
        Assert.Single(_w.Trimmed); // 조회가 안 되면 다시 비우지 않음
        Assert.Single(_log.Lines, l => l.Contains("memory lists unavailable, trimming once per session", StringComparison.Ordinal));
    }

    [Fact]
    public void WorkingSetIsTrimmedAgainOnlyWhenTheCacheWasRepurposedByAtLeast512MibSinceTheLastMaintenance()
    {
        BackgroundYield y = RunningSession(Lists(1000, 10_000));
        Assert.Single(_w.Trimmed);

        TickAt(y, 2, 100, Lists(1000, 10_000 + 511)); // 작업 집합이 어떻게 늘어도 캐시가 재사용되지 않으면 안 비움
        TickAt(y, 4, 1500, Lists(1000, 10_000 + 511 + 511));
        Assert.Single(_w.Trimmed);

        TickAt(y, 6, 100, Lists(1000, 10_000 + 1022 + 512));
        Assert.Equal([50, 50], _w.Trimmed);
        Assert.True(Logged("background yield: trimmed again 1 processes (100 MiB working set), cache repurposed 512 MiB in 120 s, 0 active processes skipped"));
    }

    [Fact]
    public void TheNextMaintenanceCanTrimAgainEvenWhenTheTrimItselfTookTime()
    {
        BackgroundYield y = RunningSession(Lists(1000, 0));
        _w.TrimCost = TimeSpan.FromSeconds(5);

        TickAt(y, 2, 100, Lists(1000, 600));
        Assert.Equal([50, 50], _w.Trimmed);

        TickAt(y, 4, 40, Lists(1000, 1200)); // 정확히 2분 뒤: 비우는 동안 흐른 시간이 간격 판정을 모자라게 하지 않음
        Assert.Equal([50, 50, 50], _w.Trimmed);
    }

    [Fact]
    public void AProcessThatLeftTheSessionLosesItsTrimMemory()
    {
        BackgroundYield y = RunningSession(Lists(1000, 0));
        YieldWorld.Proc gone = _w.Procs[50];
        _ = _w.Procs.Remove(50);

        _w.Now = _w.Start + TimeSpan.FromMinutes(2); // 50 이 목록에 없는 유지 주기
        y.Tick(_w.Snapshot());
        _w.Procs[50] = gone; // 같은 생성 시각으로 다시 나타남
        TickAt(y, 4, 400, Lists(1000, 600)); // 기억이 남았다면 절반 이상 되찾은 것으로 보고 건너뜀

        Assert.Equal([50, 50], _w.Trimmed);
    }

    [Fact]
    public void AProcessThatRegainedHalfOfItsWorkingSetIsNotTrimmedAgainThisSession()
    {
        BackgroundYield y = RunningSession(Lists(1000, 0));

        TickAt(y, 2, 250, Lists(1000, 600)); // 500 MiB 의 절반을 되찾음
        Assert.Single(_w.Trimmed);
        Assert.True(Logged("trimmed again 0 processes (0 MiB working set), cache repurposed 600 MiB in 120 s, 1 active processes skipped"));

        TickAt(y, 4, 40, Lists(1000, 1200)); // 다시 줄었어도 이 세션에서는 대상이 아님
        Assert.Single(_w.Trimmed);

        _w.Now += TimeSpan.FromMinutes(11); // 게임 없이 10분이 지나 세션이 끝나면 기억도 지워짐
        y.Tick([]);
        _w.Procs[50].Ws = 500 * Mib;
        _w.Now += TimeSpan.FromMinutes(2);
        y.Begin("game started", settle: false);
        Assert.Equal([50, 50], _w.Trimmed);
    }

    [Fact]
    public void ProcessesThatStayedSmallAfterTheTrimAreTrimmedAgain()
    {
        BackgroundYield y = RunningSession(Lists(1000, 0));
        _ = _w.Add(51, "other.exe", wsMib: 300);

        TickAt(y, 2, 100, Lists(1000, 0)); // 51 은 이번 세션에 처음 보임 (비운 적 없음)
        TickAt(y, 4, 100, Lists(1000, 600));

        Assert.Equal([50, 50, 51], _w.Trimmed);
        Assert.True(Logged("trimmed again 2 processes (400 MiB working set)"));
    }

    [Fact]
    public void PressureDecisionsAreMadeFromValuesAlone()
    {
        Assert.Equal(4096L << 20, YieldPressure.TargetHeadroomBytes(8L << 30));
        Assert.Equal(8L << 30, YieldPressure.TargetHeadroomBytes(32L << 30));
        Assert.True(YieldPressure.NeedsTrim(null, 32L << 30));
        Assert.True(YieldPressure.NeedsTrim(Lists(8191), 32L << 30));
        Assert.False(YieldPressure.NeedsTrim(Lists(8192), 32L << 30));
        Assert.False(YieldPressure.NeedsTrim(Lists(4096), 8L << 30)); // 하한 4 GiB 가 목표
        Assert.True(YieldPressure.NeedsTrim(Lists(4095), 8L << 30));
        Assert.False(YieldPressure.ShouldRetrim(TimeSpan.FromSeconds(119), 600L << 20));
        Assert.False(YieldPressure.ShouldRetrim(TimeSpan.FromMinutes(2), (512L << 20) - 1));
        Assert.False(YieldPressure.ShouldRetrim(TimeSpan.FromMinutes(2), null));
        Assert.True(YieldPressure.ShouldRetrim(TimeSpan.FromMinutes(2), 512L << 20));
        Assert.True(YieldPressure.RegainedMost(250, 500));
        Assert.False(YieldPressure.RegainedMost(249, 500));
        Assert.False(YieldPressure.RegainedMost(100, 0));
    }

    [Fact]
    public void MemoryListSummaryAddsFreeAndLowPriorityStandbyAsHeadroomAndSumsCacheReuse()
    {
        var v = new nuint[22];
        (v[0], v[1]) = (10, 20); // zero, free
        for (int i = 0; i < 8; i++)
        {
            (v[5 + i], v[13 + i]) = ((nuint)(100 * (i + 1)), (nuint)(1000 * (i + 1)));
        }

        MemoryLists lists = MemoryListInfo.Summarize(v, 4096);

        Assert.Equal((10 + 20 + 100 + 200 + 300 + 400 + 500) * 4096L, lists.HeadroomBytes); // 우선순위 0~4 만
        Assert.Equal((6000 + 7000 + 8000) * 4096L, lists.CacheRepurposedBytes); // 우선순위 5~7 만
    }

    [Fact]
    public void EndDoesNotRestoreAProcessWhoseCreationTimeChanged()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        BackgroundYield y = Make();
        y.Begin("trigger", settle: false);
        _w.Procs[50].Created = 999; // 같은 pid 를 다른 프로세스가 재사용

        y.End();

        Assert.Equal([(50, 1)], _w.Sets);
        Assert.True(Logged("memory priority restored for 0 processes"));
    }

    [Fact]
    public void StateFileKeepsTheLoweredProcessesAndTheNextRunRestoresThemThenDeletesIt()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        Make().Begin("trigger", settle: false);
        Assert.NotNull(_w.State);
        Assert.Contains("\"pid\": 50", _w.State, StringComparison.Ordinal);
        Assert.Contains("\"priority\": 5", _w.State, StringComparison.Ordinal);
        Assert.Equal(1, _w.Procs[50].Priority);

        Make().Recover(); // 서비스가 비정상 종료한 뒤의 새 인스턴스

        Assert.Equal(5, _w.Procs[50].Priority);
        Assert.Null(_w.State);
        Assert.True(Logged("background yield: restored memory priority for 1 processes left by the previous run"));
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("{\"lowered\":[{\"pid\":50}]}")]
    [InlineData("{\"lowered\":[{\"pid\":50,\"created\":1000,\"priority\":9}]}")]
    public void ACorruptStateFileIsDeletedAndNothingIsTouched(string text)
    {
        _ = _w.Add(50, "big.exe");
        _w.State = text;

        Make().Recover();

        Assert.Null(_w.State);
        Assert.Empty(_w.Sets);
    }

    [Fact]
    public void ALeftoverStateEntryForAReusedPidIsNotRestored()
    {
        _ = _w.Add(50, "other.exe");
        _w.State = "{\"lowered\":[{\"pid\":50,\"created\":424242,\"priority\":5}]}";

        Make().Recover();

        Assert.Empty(_w.Sets);
        Assert.Null(_w.State);
    }

    [Fact]
    public void SettingDefaultsToOnIsReadAndRejectsBadShapesAndUnknownKeys()
    {
        Assert.True(ParseMemory(null).BackgroundYield);
        Assert.True(ParseMemory(new JsonObject()).BackgroundYield);
        Assert.False(ParseMemory(new JsonObject { ["background_yield"] = false }).BackgroundYield);
        Assert.True(ParseMemory(new JsonObject { ["background_yield"] = true }).BackgroundYield);
        Assert.Throws<SettingsException>(() => ParseMemory(new JsonObject { ["background_yield"] = "yes" }));
        Assert.Throws<SettingsException>(() => ParseMemory(new JsonObject { ["unknown"] = 1 }));
        Assert.Throws<SettingsException>(() => ParseMemory(JsonValue.Create(true)));
    }

    private static MemorySettings ParseMemory(JsonNode? memory)
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
        };
        if (memory is not null)
        {
            root["memory"] = memory;
        }

        return SettingsStore.Parse(root.ToJsonString(), @"C:\ProgramData\AsterCache\plans").Memory;
    }

    [Fact]
    public void ConfigEditorKeepsTheMemorySectionWhenItSavesOtherSettings()
    {
        using var dir = new TempDir();
        var editor = new ConfigEditor(dir.Path);
        editor.Add(new GameInput("a", ["a.exe"], dir.Combine(@"plans\a.prefetch.json"), dir.Combine(@"plans\a.plan.json"), dir.Combine(@"Games\a"), [dir.Combine("Games")], "label"));
        string text = File.ReadAllText(editor.ConfigPath);
        File.WriteAllText(editor.ConfigPath, text.Insert(text.IndexOf('{') + 1, "\"memory\":{\"background_yield\":false},"));

        editor.SetAuto(AutoSettings.Default);
        editor.UpdateAuto(a => a with { Relocate = false });
        editor.SetUpdateMode(UpdateMode.Notify);

        Assert.False(editor.Load()!.Memory.BackgroundYield);
    }

    [Fact]
    public void DisabledSettingMakesBeginDoNothingAndTurningItOffDuringASessionRestoresOnTheNextTick()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _settings = _settings with { Memory = new MemorySettings(false) };
        BackgroundYield y = Make();
        y.Begin("trigger", settle: false);
        Assert.Equal(0, _w.ProcessListCalls);
        Assert.Empty(_w.Sets);

        _settings = _settings with { Memory = new MemorySettings(true) };
        y.Begin("trigger", settle: false);
        Assert.Single(_w.Sets);

        _settings = _settings with { Memory = new MemorySettings(false) };
        y.Tick([(50, "big.exe")]);
        Assert.Equal([(50, 1), (50, 5)], _w.Sets);
    }

    [Fact]
    public void TriggerPrewarmYieldsBeforeTheFirstAvailableMemoryReadAndOnlyWhenAWarmupWillRun()
    {
        var events = new List<string>();
        var prewarm = new Prewarmer([Cod()], new FakeStreamer(), new PrewarmEnv
        {
            AvailableRamMib = () =>
            {
                events.Add("avail");
                return 16000;
            },
            GameRunning = _ => false,
            ZoneBytes = _ => 1610L << 20,
            TraceExists = _ => true,
            YieldBackground = (why, _) => events.Add("yield:" + why),
        }, _log, null);

        Assert.Empty(prewarm.OnProcessStart(new ProcessStart(1, "notepad.exe", 0)));
        Assert.DoesNotContain("yield:trigger", events); // 트리거가 아니면 예열이 시작되지 않으므로 양보도 없음
        events.Clear();

        _ = prewarm.OnProcessStart(new ProcessStart(2, "codlauncher.exe", 0, "poll"));

        Assert.Equal("yield:trigger", events[0]);
        Assert.Contains("avail", events);
    }

    [Fact]
    public async Task GameExeStartBeginsAndTheGameExitEndsTheSession()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        GameSettings game = Cod();
        _settings = new AsterSettings(1000, [game]);
        var watcher = new CapturingWatcher();
        var worker = new Worker(_settings, _log, watcher, null, new FakeStreamer(), new ReapplyChecker(ReapplyTests.NoIoEnv(), _log), backgroundYield: Make());
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => watcher.OnStart is not null);

            watcher.OnStart!(new ProcessStart(1, "cod.exe", Stopwatch.GetTimestamp()));
            await WaitUntil(() => _w.Sets.Contains((50, 1)));

            worker.CompleteDetectedExit(game, unseen: false);
            await WaitUntil(() => _w.Sets.Contains((50, 5)));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public void ProcessThatBecomesARegisteredGameDuringTheSessionIsRestoredAndNeverWrittenAgain()
    {
        _ = _w.Add(50, "newgame.exe", wsMib: 500);
        _ = _w.Add(10, "cod.exe");
        BackgroundYield y = Make();
        y.Begin("trigger", settle: false);
        Assert.Equal([(50, 1)], _w.Sets);

        _settings = new AsterSettings(1000, [Cod(), Games.Make("new", "newgame.exe")]); // 자동 등록이 세션 중에 새 게임을 등록
        _w.Procs[50].Ws = 1500 * Mib;
        _w.Now += TimeSpan.FromMinutes(2);
        y.Tick(_w.Snapshot());

        Assert.Equal([(50, 1), (50, 5)], _w.Sets); // 그 자리에서 원래 값으로
        int trimmedBefore = _w.Trimmed.Count;
        _w.Described.Clear();
        _w.Procs[50].Ws = 3000 * Mib;
        _w.Now += TimeSpan.FromMinutes(4);
        y.Tick(_w.Snapshot()); // 작업 집합이 커져도 대상이 아님
        _w.Now += TimeSpan.FromMinutes(1);
        y.Begin("game started", settle: false); // 새 세션에서도
        Assert.Equal(trimmedBefore, _w.Trimmed.Count);
        Assert.DoesNotContain(50, _w.Described);
        Assert.Equal(2, _w.Sets.Count);
    }

    [Fact]
    public void MaintenanceNeverOpensNewlyStartedGamesLaunchersAntiCheatOrSessionZeroProcesses()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _ = _w.Add(10, "cod.exe");
        BackgroundYield y = Make();
        y.Begin("trigger", settle: false);
        _w.Described.Clear();
        _w.PriorityReads.Clear();
        _ = _w.Add(11, "codlauncher.exe");
        _ = _w.Add(12, "vgtray.exe");
        _ = _w.Add(13, "service.exe", session: 0);
        _ = _w.Add(14, "steam.exe");
        _ = _w.Add(15, "loader.exe", path: @"C:\Windows\loader.exe");
        _ = _w.Add(70, "newcomer.exe");

        _w.Now += TimeSpan.FromMinutes(2);
        y.Tick(_w.Snapshot());

        Assert.Contains(70, _w.Touched);
        Assert.Contains(15, _w.Described); // 경로만 확인
        Assert.DoesNotContain(_w.Touched, pid => pid is 11 or 12 or 13 or 14);
        Assert.DoesNotContain(_w.Sets, s => s.Pid is 11 or 12 or 13 or 14 or 15);
        Assert.Equal([(50, 1), (70, 1)], _w.Sets);
    }

    [Fact]
    public void FailedRestoreOfTheSameProcessIsKeptInMemoryAndFileUntilTheNextEndSucceeds()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        BackgroundYield y = Make();
        y.Begin("trigger", settle: false);
        _ = _w.SetFails.Add(50);

        y.End();

        Assert.True(Logged("memory priority restored for 0 processes"));
        Assert.NotNull(_w.State); // 못 되돌린 항목은 파일에 남음
        Assert.Contains("\"pid\": 50", _w.State, StringComparison.Ordinal);
        Assert.Equal(1, _w.Procs[50].Priority);

        _ = _w.SetFails.Remove(50);
        y.End();

        Assert.Equal(5, _w.Procs[50].Priority);
        Assert.Null(_w.State);
    }

    [Fact]
    public void RecoverKeepsAnEntryWhoseRestoreFailedForTheNextRun()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        Make().Begin("trigger", settle: false);
        _ = _w.SetFails.Add(50);

        Make().Recover();

        Assert.NotNull(_w.State);
        Assert.Equal(1, _w.Procs[50].Priority);
        _ = _w.SetFails.Remove(50);

        Make().Recover();

        Assert.Equal(5, _w.Procs[50].Priority);
        Assert.Null(_w.State);
    }

    [Fact]
    public void WhenTheStateFileCannotBeWrittenPriorityIsNotLoweredButTrimmingContinues()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _w.WriteFails = true;

        Make().Begin("trigger", settle: false);

        Assert.Empty(_w.Sets);
        Assert.Equal(5, _w.Procs[50].Priority);
        Assert.Equal([50], _w.Trimmed);
        Assert.True(Logged("background yield state file not written: IOException"));
        Assert.True(Logged("memory priority lowered for 0"));
    }

    [Fact]
    public void StateEntriesWhosePidIsNowSessionZeroAntiCheatRenamedOrGoneAreNeverOpened()
    {
        _ = _w.Add(51, "service.exe", session: 0);
        _ = _w.Add(52, "EasyAntiCheat.exe");
        _ = _w.Add(53, "other.exe");
        _w.State = "{\"lowered\":[{\"pid\":50,\"exe\":\"big.exe\",\"created\":1000,\"priority\":5},{\"pid\":51,\"exe\":\"service.exe\",\"created\":1000,\"priority\":5},"
            + "{\"pid\":52,\"created\":1000,\"priority\":5},{\"pid\":53,\"exe\":\"big.exe\",\"created\":1000,\"priority\":5}]}"; // 50 은 없음, 51 은 세션 0, 52 는 옛 항목이 안티치트 이름, 53 은 이름이 다름

        Make().Recover();

        Assert.Empty(_w.Touched);
        Assert.Null(_w.State);
    }

    [Fact]
    public void BeginAfterShutdownDoesNothingAndShutdownRestoresTheSession()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        BackgroundYield y = Make();
        y.Begin("trigger", settle: false);

        y.Shutdown();
        Assert.Equal([(50, 1), (50, 5)], _w.Sets);

        _w.Now += TimeSpan.FromMinutes(5);
        int listed = _w.ProcessListCalls;
        y.Begin("game started", settle: false); // 종료 뒤 늦게 도착한 호출

        Assert.Equal(listed, _w.ProcessListCalls);
        Assert.Equal(2, _w.Sets.Count);
    }

    [Fact]
    public void GameInstallFolderKnownToTheLauncherScanIsOnlyDescribedNeverWritten()
    {
        _ = _w.Add(80, "Overwatch.exe", path: @"C:\Program Files (x86)\Overwatch\Overwatch.exe", wsMib: 500);

        Make().Begin("trigger", settle: false);
        Assert.Equal([80], _w.Trimmed); // 목록이 비어 있으면 (자동 모드 꺼짐) 지금까지와 같음

        _w.Trimmed.Clear();
        _w.Sets.Clear();
        _w.Described.Clear();
        (_w.Procs[80].Priority, _w.Procs[80].Ws) = (5, 500 * Mib); // 첫 단계가 낮추고 비운 것을 되돌려 두 번째 단계의 단언이 규칙을 실제로 확인하게
        _w.Now += TimeSpan.FromMinutes(5);
        _w.KnownFolders = [@"C:\Program Files (x86)\Overwatch"];
        var second = new BackgroundYield(() => _settings, _w.Env(), _log);
        second.Begin("trigger", settle: false);

        Assert.Empty(_w.Trimmed);
        Assert.Empty(_w.Sets);
        Assert.Contains(80, _w.Described);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5);
        }

        Assert.True(condition(), "condition was not met before the timeout");
    }

    private sealed class CapturingWatcher : IProcessWatcher
    {
        public volatile Action<ProcessStart>? OnStart;

        public async Task RunAsync(Action<ProcessStart> onStart, CancellationToken cancellationToken)
        {
            OnStart = onStart;
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 종료
            }
        }
    }
}

/// 실제 Windows 호출: 시험이 직접 띄운 자식 프로세스에 조회·비우기·메모리 우선순위 변경이 되는지. 관리자 권한은 필요 없습니다 (같은 사용자의 프로세스).
[Trait("Category", "Live")]
public sealed class ProcessMemoryLiveTests
{
    [Fact]
    public void WrapperDescribesTrimsAndLowersThenRestoresTheMemoryPriorityOfAChildProcess()
    {
        using Process child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        int pid = child.Id;
        try
        {
            ProcessInfo? info = ProcessMemory.Describe(pid);
            Assert.NotNull(info);
            Assert.EndsWith("ping.exe", info.Value.Path, StringComparison.OrdinalIgnoreCase);
            Assert.True(info.Value.WorkingSetBytes > 0);
            Assert.True(info.Value.Created > 0);
            Assert.Equal((uint)Process.GetCurrentProcess().SessionId, ProcessMemory.SessionId(pid));

            int? original = ProcessMemory.MemoryPriority(pid);
            Assert.NotNull(original);
            Assert.True(ProcessMemory.SetMemoryPriority(pid, ProcessMemory.PriorityVeryLow));
            Assert.Equal(ProcessMemory.PriorityVeryLow, ProcessMemory.MemoryPriority(pid));
            Assert.True(ProcessMemory.SetMemoryPriority(pid, original.Value));
            Assert.Equal(original, ProcessMemory.MemoryPriority(pid));
            Assert.True(ProcessMemory.Trim(pid));
        }
        finally
        {
            child.Kill();
        }

        child.WaitForExit();
        Assert.Null(ProcessMemory.Describe(pid)); // 끝난 프로세스는 예외 없이 null
        Assert.False(ProcessMemory.Trim(pid));
    }
}

/// 실제 Windows 호출: 대기 목록 조회가 값을 돌려주고 합이 물리 RAM 을 넘지 않는지. 권한을 켤 수 있는 (관리자) 토큰이 필요합니다.
[Trait("Category", "Live")]
public sealed class MemoryListLiveTests
{
    [Fact]
    public void QueryReturnsHeadroomAndCacheReuseWithinPhysicalMemory()
    {
        MemoryLists? lists = MemoryListInfo.Query();

        Assert.NotNull(lists);
        Assert.True(lists.Value.HeadroomBytes > 0);
        Assert.True(lists.Value.HeadroomBytes <= SystemMemory.TotalBytes());
        Assert.True(lists.Value.CacheRepurposedBytes >= 0);
    }
}
