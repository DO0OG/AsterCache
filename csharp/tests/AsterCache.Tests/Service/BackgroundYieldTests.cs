using System.Diagnostics;
using System.Text.Json.Nodes;
using AsterCache.Core.Logging;
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
        public int Io = 2; // I/O 우선순위 (보통)
        public int Cpu = 0x20; // CPU 우선순위 등급 (보통)
        public int Parent;
        public long CpuTicks; // CPU 시간 누계 (100 ns)
        public long IoBytes; // 읽기 + 쓰기 전송 바이트 누계
    }

    public Dictionary<int, Proc> Procs { get; } = [];

    public List<int> Described { get; } = [];

    public List<int> Trimmed { get; } = [];

    public List<(int Pid, int Priority)> Sets { get; } = [];

    public List<int> PriorityReads { get; } = [];

    public List<(int Pid, int Priority)> IoSets { get; } = [];

    public List<int> IoReads { get; } = [];

    public List<(int Pid, int Class)> CpuSets { get; } = [];

    public List<int> CpuReads { get; } = [];

    /// CPU 등급 쓰기가 실패하는 pid.
    public HashSet<int> CpuSetFails { get; } = [];

    /// I/O 우선순위 쓰기가 실패하는 pid.
    public HashSet<int> IoSetFails { get; } = [];

    /// 조회 권한으로 경로를 읽지 못하는 pid.
    public HashSet<int> DescribeFails { get; } = [];

    public DateTimeOffset Boot { get; set; } = DateTimeOffset.UnixEpoch;

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

    /// 프로세스별 CPU·I/O 누계 조회가 실패함.
    public bool UsageFails { get; set; }

    public IReadOnlyList<string> KnownFolders { get; set; } = [];

    public int SelfPid { get; set; } = -1;

    public Proc Add(int pid, string exe, uint session = 1, string? path = null, long wsMib = 10, int priority = 5) =>
        Procs[pid] = new Proc { Exe = exe, Session = session, Path = path ?? @"C:\Users\u\AppData\Local\Programs\app\" + exe, Ws = wsMib * Mib, Priority = priority };

    /// 조회·쓰기·우선순위 읽기가 닿은 모든 pid (제외 대상은 여기에 한 번도 없어야 함).
    public IEnumerable<int> Touched => Described.Concat(Trimmed).Concat(Sets.Select(s => s.Pid)).Concat(PriorityReads).Concat(IoSets.Select(s => s.Pid)).Concat(IoReads).Concat(CpuSets.Select(s => s.Pid)).Concat(CpuReads).Distinct();

    public IReadOnlyList<(int Pid, string Exe)> Snapshot() => [.. Procs.Select(p => (p.Key, p.Value.Exe))];

    /// 서비스가 한동안 돌아 1분 전의 쉬는 상태 표본이 쌓여 있는 배경 양보 (그 뒤 CpuTicks·IoBytes 를 늘리면 쓰고 있는 프로세스).
    public BackgroundYield Yielder(Func<AsterSettings> settings, ILog log)
    {
        var yielder = new BackgroundYield(settings, Env(), log);
        yielder.SampleUsage(Now - TimeSpan.FromMinutes(1));
        return yielder;
    }

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
            return !DescribeFails.Contains(pid) && Procs.TryGetValue(pid, out Proc? p) ? new ProcessInfo(p.Path, p.Ws, p.Created, p.Parent) : null;
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
        IoPriority = pid =>
        {
            IoReads.Add(pid);
            return Procs.TryGetValue(pid, out Proc? p) ? p.Io : null;
        },
        SetIoPriority = (pid, value) =>
        {
            IoSets.Add((pid, value));
            if (IoSetFails.Contains(pid) || !Procs.TryGetValue(pid, out Proc? p))
            {
                return false;
            }

            p.Io = value;
            return true;
        },
        CpuClass = pid =>
        {
            CpuReads.Add(pid);
            return Procs.TryGetValue(pid, out Proc? p) ? p.Cpu : null;
        },
        SetCpuClass = (pid, value) =>
        {
            CpuSets.Add((pid, value));
            if (CpuSetFails.Contains(pid) || !Procs.TryGetValue(pid, out Proc? p))
            {
                return false;
            }

            p.Cpu = value;
            return true;
        },
        AvailableMib = () => Avail(),
        Lists = () => ListsNow,
        PhysicalBytes = () => PhysicalRam,
        Usage = list =>
        {
            list.Clear();
            if (UsageFails)
            {
                return false;
            }

            list.AddRange(Procs.Select(p => new ProcessUsage(p.Key, p.Value.Exe, p.Value.CpuTicks, 0, p.Value.IoBytes)));
            return true;
        },
        Now = () => Now,
        BootTime = () => Boot,
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
    private AsterSettings _settings = new AsterSettings(1000, [Cod()]) with { Memory = new MemorySettings(BackgroundCpuPriority: true) }; // CPU 등급 낮추기는 기본이 꺼짐이라 그쪽을 시험하는 쪽에서 켬

    private static GameSettings Cod() => Games.Make("cod", "cod.exe") with { Prewarm = PrewarmSettings.Default with { TriggerExes = ["codlauncher.exe"] } };

    private BackgroundYield Make() => _w.Yielder(() => _settings, _log);

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
        Assert.Equal([(20, 1)], _w.IoSets);
        Assert.Equal([20], _w.IoReads);
        Assert.Equal([(20, 0x4000)], _w.CpuSets);
        Assert.Equal([20], _w.CpuReads);
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
        Assert.True(Logged("background yield (trigger): trimmed 2 processes (900 MiB working set, shortfall 6384 MiB), memory priority lowered for 2, I/O priority lowered for 3, CPU priority lowered for 0, 0 in-use processes spared, available 10000 MiB, expected game use 8192 MiB, floor 8192 MiB"));

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
    public void StartDoesNotTrimWhenAvailableMemoryCoversTheGameAndTheFloorButStillLowersPriorityAndSkipsTheSettleWait()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _w.Avail = () => 16384; // 하한 8 GiB (물리 RAM 32 GiB 의 1/4) + 게임 예상 8 GiB

        Make().Begin("game started", settle: true);

        Assert.Empty(_w.Trimmed);
        Assert.Equal([(50, 1)], _w.Sets);
        Assert.Equal(_w.Start, _w.Now); // 가라앉기 대기 없음
        Assert.True(Logged("background yield (game started): memory priority lowered for 1, I/O priority lowered for 1, CPU priority lowered for 0, 0 in-use processes spared, no trim needed (available 16384 MiB, expected game use 8192 MiB, floor 8192 MiB)"));
    }

    [Fact]
    public void StartTrimsOnlyTheShortfallAndOnlyWhenTheFloorIsMissedTheFloorIsFourGibOnSmallMachines()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _w.Avail = () => 16383;
        Make().Begin("game started", settle: false);
        Assert.Equal([50], _w.Trimmed);
        Assert.True(Logged("trimmed 1 processes (500 MiB working set, shortfall 1 MiB), memory priority lowered for 1, I/O priority lowered for 1, CPU priority lowered for 0, 0 in-use processes spared, available 16383 MiB, expected game use 8192 MiB, floor 8192 MiB"));

        _w.Trimmed.Clear();
        _w.Procs[50].Ws = 500 * Mib;
        _w.Now += TimeSpan.FromMinutes(5);
        (_w.PhysicalRam, _w.Avail) = (8L * 1024 * Mib, () => 12288); // RAM/4 = 2 GiB 라 하한 4 GiB: 4 GiB + 게임 8 GiB
        Make().Begin("game started", settle: false);
        Assert.Empty(_w.Trimmed);
        Assert.True(Logged("no trim needed (available 12288 MiB, expected game use 8192 MiB, floor 4096 MiB)"));
    }

    [Fact]
    public void StartTrimsOnlyAsMuchAsIsMissingAndTheLargestIdleProcessesFirst()
    {
        _ = _w.Add(50, "mid.exe", wsMib: 300);
        _ = _w.Add(51, "big.exe", wsMib: 500);
        _ = _w.Add(52, "small.exe", wsMib: 200);
        _w.Avail = () => 16384 - 600; // 600 MiB 모자람

        Make().Begin("game started", settle: false);

        Assert.Equal([51, 50], _w.Trimmed); // 500 + 300 >= 600 에서 멈춤: 작은 것은 비우지 않음
        Assert.True(Logged("shortfall 600 MiB"));
    }

    [Fact]
    public void ThePlayRecordFootprintReplacesTheDefaultGameEstimate()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _ = _w.Add(10, "cod.exe");
        _w.Avail = () => 16384 - 3000; // 기본 추정 (8 GiB) 이면 3000 MiB 모자라지만 기록 (3 GiB) 이면 충분
        BackgroundYield y = Make();
        y.Footprint = name => name == "cod" ? ((long Mib, int Plays)?)(3072, 3) : null;

        y.Begin("game started", settle: false);

        Assert.Empty(_w.Trimmed);
        Assert.True(Logged("no trim needed (available 13384 MiB, expected game use 3072 MiB, floor 8192 MiB)"));
    }

    [Fact]
    public void ASecondBeginRightAfterAStartWithoutATrimDoesNotWaitToSettleButOneAfterATrimDoes()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _w.Avail = () => 16384;
        BackgroundYield y = Make();
        y.Begin("trigger", settle: false);
        _w.Now += TimeSpan.FromSeconds(1);

        y.Begin("game started", settle: true);

        Assert.Equal(_w.Start + TimeSpan.FromSeconds(1), _w.Now); // 비운 결과가 없으니 가라앉기를 기다리지 않음

        _w.Now += TimeSpan.FromMinutes(5);
        _w.Avail = () => 1000;
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
        Assert.True(Logged("trimmed 1 processes (500 MiB working set, shortfall 6384 MiB), memory priority lowered for 1, I/O priority lowered for 1, CPU priority lowered for 1, 0 in-use processes spared, available 10000 MiB"));
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
        Assert.True(Logged("background yield: trimmed again 1 processes (100 MiB working set, shortfall 6384 MiB), cache repurposed 512 MiB in 120 s, 0 active processes skipped, 0 in-use processes spared, available 10000 MiB, expected game use 8192 MiB, floor 8192 MiB"));
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
        Assert.True(Logged("trimmed again 0 processes (0 MiB working set, shortfall 6384 MiB), cache repurposed 600 MiB in 120 s, 1 active processes skipped"));

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

        Assert.Equal([50, 51, 50], _w.Trimmed); // 다시 비우기는 큰 것부터 (51 이 300 MiB, 50 이 100 MiB)
        Assert.True(Logged("trimmed again 2 processes (400 MiB working set"));
    }

    [Fact]
    public void PressureDecisionsAreMadeFromValuesAlone()
    {
        Assert.Equal(4096L << 20, YieldPressure.TargetHeadroomBytes(8L << 30));
        Assert.Equal(8L << 30, YieldPressure.TargetHeadroomBytes(32L << 30));
        Assert.Equal(0, YieldPressure.ShortfallBytes(16L << 30, 8L << 30, 8L << 30)); // 가용 16 GiB - 게임 8 GiB = 하한: 모자라지 않음
        Assert.Equal(1L << 30, YieldPressure.ShortfallBytes(15L << 30, 8L << 30, 8L << 30)); // 모자라는 만큼만
        Assert.Equal(0, YieldPressure.ShortfallBytes(40L << 30, 8L << 30, 8L << 30)); // 남아도 음수가 되지 않음
        Assert.Equal(4L << 30, YieldPressure.ShortfallBytes(8L << 30, 0, 12L << 30)); // 게임이 이미 가져갔으면 하한만 봄
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
        Assert.Equal((600 + 700 + 800) * 4096L, lists.CacheStandbyBytes); // 우선순위 5~7 만
    }

    private const long Second = 10_000_000; // FILETIME 한 단위는 100ns

    // 첫 낮추기 (가짜 시계의 시작) 뒤 seconds 초에 parent 의 자식으로 떠서 낮은 값을 물려받은 프로세스. 낮추기 전 (seconds 가 음수) 이면 원래부터 낮은 값으로 뜬 것.
    private YieldWorld.Proc Spawn(int pid, string exe, int parent, int seconds)
    {
        YieldWorld.Proc p = _w.Add(pid, exe, priority: 1);
        (p.Io, p.Cpu) = (1, 0x4000); // 낮춘 부모에게서 물려받은 값
        (p.Parent, p.Created) = (parent, _w.Start.ToFileTime() + (seconds * Second));
        return p;
    }

    private BackgroundYield RunningYield()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _ = _w.Add(10, "cod.exe");
        BackgroundYield y = Make();
        y.Begin("trigger", settle: false);
        return y;
    }

    private void MaintainAt(BackgroundYield y, int minute)
    {
        _w.Now = _w.Start + TimeSpan.FromMinutes(minute);
        y.Tick(_w.Snapshot());
    }

    [Fact]
    public void WindowsFolderChildrenAndTheirDescendantsOfALoweredProcessAreRestoredAtTheEndButNeverLoweredOrTrimmed()
    {
        BackgroundYield y = RunningYield();
        YieldWorld.Proc shell = Spawn(60, "cmd.exe", 50, 20);
        (shell.Path, shell.Ws) = (@"C:\Windows\System32\cmd.exe", 500 * Mib);
        _ = Spawn(61, "git.exe", 60, 40); // Windows 폴더의 자식이 띄운 손자
        _w.Procs[62] = new YieldWorld.Proc { Exe = "conhost.exe", Path = @"C:\Windows\System32\conhost.exe", Priority = 1, Io = 1, Cpu = 0x4000, Parent = 61, Created = _w.Start.ToFileTime() + (60 * Second) };

        MaintainAt(y, 2);

        Assert.Equal([(50, 1)], _w.Sets); // Windows 폴더의 자식도 새로 낮추지는 않음
        Assert.Equal([(50, 1)], _w.IoSets);
        Assert.DoesNotContain(60, _w.Trimmed);
        Assert.Contains("\"pid\": 62", _w.State, StringComparison.Ordinal);
        _ = _w.Procs.Remove(10);

        y.End();

        Assert.Equal([5, 5, 5, 5], new[] { 50, 60, 61, 62 }.Select(pid => _w.Procs[pid].Priority));
        Assert.Equal([2, 2, 2, 2], new[] { 50, 60, 61, 62 }.Select(pid => _w.Procs[pid].Io));
        Assert.Null(_w.State);
    }

    [Fact]
    public void AWindowsFolderProcessThatIsNotAnInheritingChildOfALoweredProcessIsNeverWritten()
    {
        BackgroundYield y = RunningYield();
        _ = _w.Add(64, "svchost.exe", path: @"C:\Windows\System32\svchost.exe", wsMib: 500); // 부모가 기록에 없음
        _ = Spawn(65, "robocopy.exe", 4000, 20).Path = @"C:\Windows\System32\robocopy.exe"; // 모르는 부모
        YieldWorld.Proc own = _w.Add(66, "tool.exe", path: @"C:\Windows\System32\tool.exe");
        (own.Parent, own.Created) = (50, _w.Start.ToFileTime() + (20 * Second)); // 낮춘 부모의 자식이지만 스스로 값이 보통 (물려받지 않음)

        MaintainAt(y, 2);
        _ = _w.Procs.Remove(10);
        y.End();

        Assert.DoesNotContain(_w.Sets, s => s.Pid is 64 or 65 or 66);
        Assert.DoesNotContain(_w.IoSets, s => s.Pid is 64 or 65 or 66);
        Assert.DoesNotContain(64, _w.Trimmed);
        Assert.Equal([5, 1, 5], new[] { 64, 65, 66 }.Select(pid => _w.Procs[pid].Priority));
    }

    [Fact]
    public void GameLauncherAntiCheatSessionZeroAndGameFolderChildrenOfALoweredProcessAreNeverReadOrWritten()
    {
        BackgroundYield y = RunningYield();
        _ = Spawn(66, "cod.exe", 50, 20); // 등록 게임
        _ = Spawn(67, "steam.exe", 50, 20); // 런처
        _ = Spawn(68, "vgtray.exe", 50, 20); // 안티치트
        Spawn(69, "svc.exe", 50, 20).Session = 0; // 세션 0
        _ = Spawn(70, "x.exe", 50, 20).Path = @"D:\Games\cod\bin\x.exe"; // 게임 폴더 아래
        _ = Spawn(71, "y.exe", 50, 20).Path = @"C:\Program Files\EasyAntiCheat\y.exe"; // 안티치트 폴더 아래

        MaintainAt(y, 2);
        _ = _w.Procs.Remove(10);
        y.End();

        int[] kids = [66, 67, 68, 69, 70, 71];
        Assert.DoesNotContain(_w.Touched, pid => pid is >= 66 and <= 69);
        Assert.DoesNotContain(_w.Sets.Concat(_w.IoSets), s => kids.Contains(s.Pid));
        Assert.DoesNotContain(_w.PriorityReads.Concat(_w.IoReads), pid => kids.Contains(pid));
        Assert.Equal([1, 1, 1, 1, 1, 1], kids.Select(pid => _w.Procs[pid].Priority));
    }

    [Fact]
    public void WhenTheStateFileWriteFailsAChildFoundThatMomentIsStillRestoredAtTheEnd()
    {
        BackgroundYield y = RunningYield();
        _ = Spawn(60, "kid.exe", 50, 20);
        _ = _w.Add(70, "late.exe", wsMib: 500); // 새로 낮추려던 것은 기록 없이 낮추지 않음
        _w.WriteFails = true;

        MaintainAt(y, 2);

        Assert.Equal([5, 2], new[] { _w.Procs[70].Priority, _w.Procs[70].Io });
        _w.WriteFails = false;
        _ = _w.Procs.Remove(10);
        y.End();

        Assert.Equal([5, 2], new[] { _w.Procs[60].Priority, _w.Procs[60].Io });
        Assert.Null(_w.State);
    }

    [Fact]
    public void ARestartedServiceAlsoRestoresChildrenStartedAfterTheLastRecordButNotOnesCreatedBeforeTheParentWasLowered()
    {
        _ = RunningYield();
        Assert.Contains("lowered_at", _w.State, StringComparison.Ordinal);
        _ = Spawn(60, "kid.exe", 50, 150); // 마지막 기록 뒤 (서비스가 내려가 있던 동안 포함)
        _ = Spawn(61, "grandkid.exe", 60, 160);
        _ = Spawn(62, "old.exe", 50, -30); // 부모가 낮춰지기 전부터 낮았음

        Make().Recover();

        Assert.Equal([5, 5, 5], new[] { 50, 60, 61 }.Select(pid => _w.Procs[pid].Priority));
        Assert.Equal([2, 2, 2], new[] { 50, 60, 61 }.Select(pid => _w.Procs[pid].Io));
        Assert.Equal([1, 1], new[] { _w.Procs[62].Priority, _w.Procs[62].Io });
        Assert.Null(_w.State);
        Assert.True(Logged("restored memory priority for 3 processes left by the previous run, I/O priority for 3"));
    }

    [Fact]
    public void AChildThatCombinesItsOwnLoweringWithAnInheritedSideKeepsTheEarlierTimeSoItsOwnChildIsFound()
    {
        BackgroundYield y = RunningYield();
        YieldWorld.Proc kid = Spawn(60, "kid.exe", 50, 20);
        kid.Priority = 5; // 메모리는 스스로 올려 직접 낮추고 I/O 는 물려받음
        _ = Spawn(61, "grandkid.exe", 60, 40).Priority = 5;

        MaintainAt(y, 2);
        _ = _w.Procs.Remove(10);
        y.End();

        Assert.Equal(2, _w.Procs[61].Io);
        Assert.Equal(2, _w.Procs[60].Io);
    }

    [Fact]
    public void WhenOnlyTheIoSettingIsOffAndTheRestoreKeepsFailingItIsTriedOnceAndTheEndRetries()
    {
        BackgroundYield y = RunningYield();
        _ = _w.IoSetFails.Add(50);
        _settings = _settings with { Memory = new MemorySettings(true, true, false, true) };

        MaintainAt(y, 1);
        MaintainAt(y, 2);
        MaintainAt(y, 3);

        Assert.Equal(1, _log.Lines.Count(l => l.Contains("(setting turned off)", StringComparison.Ordinal)));
        Assert.Equal(1, _w.IoSets.Count(s => s.Priority == 2));

        _ = _w.IoSetFails.Remove(50);
        _ = _w.Procs.Remove(10);
        y.End();

        Assert.Equal(2, _w.Procs[50].Io);
        Assert.Null(_w.State);
    }

    // 낮춘 부모 (pid 50) 가 끝나고 같은 pid 를 대상이 아닌 프로세스가 다시 씀. 그 프로세스가 띄운 낮은 값의 자식은 Windows 폴더 하나, 일반 폴더 하나.
    private BackgroundYield ReusedParentPid(string exe, uint session)
    {
        BackgroundYield y = RunningYield();
        _ = _w.Procs.Remove(50);
        _w.Add(50, exe, session: session).Created = _w.Start.ToFileTime() + (60 * Second);
        _ = Spawn(60, "cmd.exe", 50, 200).Path = @"C:\Windows\System32\cmd.exe";
        _ = Spawn(61, "tool.exe", 50, 200);
        return y;
    }

    [Theory]
    [InlineData("svchost.exe", 0u)]
    [InlineData("cod.exe", 1u)]
    [InlineData("steam.exe", 1u)]
    public void ChildrenOfAProcessThatReusedTheEndedParentsPidAreNotTakenAsInheritedAtTheEnd(string exe, uint session)
    {
        BackgroundYield y = ReusedParentPid(exe, session);

        MaintainAt(y, 2);
        _ = _w.Procs.Remove(10);
        y.End();

        Assert.Equal([1, 1, 1, 1], new[] { 60, 61 }.SelectMany(pid => new[] { _w.Procs[pid].Priority, _w.Procs[pid].Io }));
        Assert.DoesNotContain(_w.Sets.Concat(_w.IoSets), s => s.Pid is 60 or 61);
    }

    [Theory]
    [InlineData("svchost.exe", 0u)]
    [InlineData("cod.exe", 1u)]
    public void ChildrenOfAProcessThatReusedTheEndedParentsPidAreNotTakenAsInheritedByRecover(string exe, uint session)
    {
        _ = ReusedParentPid(exe, session);

        Make().Recover();

        Assert.Equal([1, 1, 1, 1], new[] { 60, 61 }.SelectMany(pid => new[] { _w.Procs[pid].Priority, _w.Procs[pid].Io }));
        Assert.DoesNotContain(_w.Sets.Concat(_w.IoSets), s => s.Pid is 60 or 61);
    }

    [Fact]
    public void RecoverAfterARebootDoesNotUseTheFilesPidsAsParents()
    {
        _ = RunningYield();
        _ = Spawn(60, "kid.exe", 50, 200);
        _w.Boot = _w.Start + TimeSpan.FromDays(1); // 파일은 재부팅 앞에 적힘

        Make().Recover();

        Assert.Equal([1, 1], new[] { _w.Procs[60].Priority, _w.Procs[60].Io });
        Assert.Equal(5, _w.Procs[50].Priority); // 같은 프로세스 (만든 시각 같음) 면 그 항목 자체는 되돌림
    }

    [Fact]
    public void AChildThatEnteredBackgroundModeItselfIsNotTakenAsInheritedForMemoryEither()
    {
        BackgroundYield y = RunningYield();
        _w.Procs[61] = new YieldWorld.Proc { Exe = "bg.exe", Priority = 1, Io = 0, Parent = 50, Created = _w.Start.ToFileTime() + (20 * Second) };

        MaintainAt(y, 2);
        _ = _w.Procs.Remove(10);
        y.End();

        Assert.Equal([1, 0], new[] { _w.Procs[61].Priority, _w.Procs[61].Io });
    }

    [Fact]
    public void AWindowsFolderChildThatAlsoHitsAnotherPathRuleOrWhosePathCannotBeReadIsNeverReadOrWritten()
    {
        _w.KnownFolders = [@"C:\Windows\Temp\MyGame"];
        BackgroundYield y = RunningYield();
        _ = Spawn(60, "g.exe", 50, 20).Path = @"C:\Windows\Temp\MyGame\g.exe"; // Windows 폴더 아래이면서 알려진 게임 폴더
        _ = Spawn(61, "cmd.exe", 50, 20).Path = @"C:\Windows\System32\cmd.exe";
        _ = _w.DescribeFails.Add(61); // 경로를 못 읽음

        MaintainAt(y, 2);
        _ = _w.Procs.Remove(10);
        y.End();

        Assert.DoesNotContain(_w.PriorityReads.Concat(_w.IoReads), pid => pid is 60 or 61);
        Assert.DoesNotContain(_w.Sets.Concat(_w.IoSets), s => s.Pid is 60 or 61);
        Assert.Equal([1, 1, 1, 1], new[] { 60, 61 }.SelectMany(pid => new[] { _w.Procs[pid].Priority, _w.Procs[pid].Io }));
    }

    [Fact]
    public void TheMaintenanceRetrimDoesNotOpenWindowsFolderProgramsAgain()
    {
        BackgroundYield y = RunningSession(Lists(1000, 10_000));
        _ = _w.Add(70, "svchost.exe", path: @"C:\Windows\System32\svchost.exe", wsMib: 500);

        TickAt(y, 2, 100, Lists(1000, 10_000));
        TickAt(y, 4, 100, Lists(1000, 10_000 + 600));
        TickAt(y, 6, 100, Lists(1000, 10_000 + 1300));

        Assert.True(_w.Trimmed.Count > 1); // 다시 비우기가 일어남
        Assert.DoesNotContain(70, _w.Trimmed);
        Assert.Equal(1, _w.Described.Count(pid => pid == 70));
    }

    [Fact]
    public void OnlyNormalIoPriorityIsLoweredToLowAndRestoredAtTheEndWhileOtherValuesAreLeftAlone()
    {
        _ = _w.Add(50, "normal.exe");
        _w.Add(51, "low.exe").Io = 1;
        _w.Add(52, "high.exe").Io = 3;
        _w.Add(53, "verylow.exe").Io = 0;
        BackgroundYield y = Make();

        y.Begin("trigger", settle: false);

        Assert.Equal([(50, 1)], _w.IoSets);
        Assert.Equal(1, _w.Procs[50].Io);
        Assert.Equal([1, 3, 0], new[] { 51, 52, 53 }.Select(pid => _w.Procs[pid].Io));
        Assert.Contains("\"io_priority\": 2", _w.State, StringComparison.Ordinal);
        Assert.True(Logged("memory priority lowered for 4, I/O priority lowered for 1"));

        y.End();

        Assert.Equal([(50, 1), (50, 2)], _w.IoSets);
        Assert.Equal([2, 1, 3, 0], new[] { 50, 51, 52, 53 }.Select(pid => _w.Procs[pid].Io));
        Assert.Null(_w.State);
        Assert.True(Logged("memory priority restored for 4 processes (0 inherited), I/O priority restored for 1 processes"));
    }

    [Fact]
    public void ChildrenAndGrandchildrenOfAProcessWithLoweredIoPriorityAreRestoredWithItAndAnIoOnlyEntryIsKept()
    {
        BackgroundYield y = RunningYield();
        _ = Spawn(61, "grandkid.exe", 60, 40);
        _ = Spawn(60, "kid.exe", 50, 20);

        MaintainAt(y, 2);

        Assert.Equal([(50, 1)], _w.IoSets); // 물려받은 것은 이미 낮아서 쓰지 않음
        Assert.Equal(1, _w.Procs[61].Io);
        _ = _w.Procs.Remove(10);
        y.End();

        Assert.Equal([2, 2, 2], new[] { 50, 60, 61 }.Select(pid => _w.Procs[pid].Io));
        Assert.True(Logged("I/O priority restored for 3 processes"));
        Assert.Null(_w.State);
    }

    [Fact]
    public void AChildWhoseMemoryPriorityWasSetByItselfStillInheritsTheIoPriorityOnlyOnce()
    {
        BackgroundYield y = RunningYield();
        YieldWorld.Proc kid = Spawn(60, "kid.exe", 50, 20);
        kid.Priority = 5; // 스스로 올림: 메모리는 직접 낮추고 I/O 는 물려받음

        MaintainAt(y, 2);

        Assert.Equal([(50, 1), (60, 1)], _w.Sets);
        Assert.Equal([(50, 1)], _w.IoSets);
        _ = _w.Procs.Remove(10);
        y.End();

        Assert.Equal([5, 2], new[] { _w.Procs[60].Priority, _w.Procs[60].Io });
        Assert.Null(_w.State);
    }

    [Fact]
    public void ARestartedServiceRestoresTheIoPriorityOfTheLoweredAndInheritedProcessesFromTheStateFile()
    {
        BackgroundYield y = RunningYield();
        _ = Spawn(60, "kid.exe", 50, 20);
        MaintainAt(y, 2);
        Assert.Equal([1, 1], new[] { 50, 60 }.Select(pid => _w.Procs[pid].Io));

        Make().Recover();

        Assert.Equal([2, 2], new[] { 50, 60 }.Select(pid => _w.Procs[pid].Io));
        Assert.Null(_w.State);
        Assert.True(Logged("restored memory priority for 2 processes left by the previous run, I/O priority for 2"));
    }

    [Fact]
    public void AStateFileWrittenBeforeIoPriorityExistedStillRestoresMemoryAndAnIoOnlyEntryRestoresIo()
    {
        _ = _w.Add(50, "big.exe");
        _ = _w.Add(51, "other.exe");
        _w.Procs[50].Priority = 1;
        _w.Procs[51].Io = 1;
        _w.State = "{\"lowered\":[{\"pid\":50,\"exe\":\"big.exe\",\"created\":1000,\"priority\":5}," // 이전 형식: io_priority 없음
            + "{\"pid\":51,\"exe\":\"other.exe\",\"created\":1000,\"io_priority\":2}]}";

        Make().Recover();

        Assert.Equal([(50, 5)], _w.Sets);
        Assert.Equal([(51, 2)], _w.IoSets);
        Assert.Equal([5, 2], new[] { _w.Procs[50].Priority, _w.Procs[51].Io });
        Assert.Null(_w.State);
    }

    [Theory]
    [InlineData("{\"lowered\":[{\"pid\":50,\"created\":1000,\"io_priority\":0}]}")]
    [InlineData("{\"lowered\":[{\"pid\":50,\"created\":1000,\"io_priority\":\"2\"}]}")]
    [InlineData("{\"lowered\":[{\"pid\":50,\"created\":1000,\"priority\":5,\"io_priority\":9}]}")]
    public void AStateEntryWithAnInvalidIoPriorityIsDroppedAndNothingIsTouched(string text)
    {
        _ = _w.Add(50, "big.exe");
        _w.State = text;

        Make().Recover();

        Assert.Null(_w.State);
        Assert.Empty(_w.Sets);
        Assert.Empty(_w.IoSets);
    }

    [Fact]
    public void WithTheIoSettingOffOnlyMemoryPriorityIsLoweredAndNothingIoIsReadOrWritten()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _settings = _settings with { Memory = new MemorySettings(true, true, false, true) };

        Make().Begin("trigger", settle: false);

        Assert.Equal([(50, 1)], _w.Sets);
        Assert.Empty(_w.IoReads);
        Assert.Empty(_w.IoSets);
        Assert.DoesNotContain("io_priority", _w.State, StringComparison.Ordinal);
        Assert.True(Logged("memory priority lowered for 1, I/O priority lowered for 0"));
    }

    [Fact]
    public void TurningOnlyTheIoSettingOffDuringASessionRestoresIoAtTheNextTickAndKeepsMemoryLowered()
    {
        BackgroundYield y = RunningYield();
        _ = Spawn(60, "kid.exe", 50, 20); // 마지막 점검 뒤에 뜬 자식도 함께 되돌림
        _settings = _settings with { Memory = new MemorySettings(true, true, false, true) };

        MaintainAt(y, 1);

        Assert.Equal([2, 2], new[] { 50, 60 }.Select(pid => _w.Procs[pid].Io));
        Assert.Equal(1, _w.Procs[50].Priority);
        Assert.DoesNotContain("io_priority", _w.State, StringComparison.Ordinal);
        Assert.Contains("\"priority\": 5", _w.State, StringComparison.Ordinal);
        Assert.True(Logged("I/O priority restored for 2 processes (setting turned off)"));

        _ = _w.Procs.Remove(10);
        y.End();

        Assert.Equal(5, _w.Procs[50].Priority);
        Assert.Null(_w.State);
    }

    [Fact]
    public void AFailureOnOneSideLeavesTheOtherSideLoweredAndRecordedAndEachFailedRestoreIsRetried()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _ = _w.IoSetFails.Add(50);
        BackgroundYield y = Make();

        y.Begin("trigger", settle: false);

        Assert.Equal(1, _w.Procs[50].Priority);
        Assert.Equal(2, _w.Procs[50].Io);
        Assert.DoesNotContain("io_priority", _w.State, StringComparison.Ordinal); // 못 낮춘 쪽은 기록하지 않음
        Assert.True(Logged("memory priority lowered for 1, I/O priority lowered for 0"));

        _ = _w.IoSetFails.Remove(50);
        y.End();
        Assert.Equal(5, _w.Procs[50].Priority);
        Assert.Null(_w.State);

        _w.Now += TimeSpan.FromMinutes(5);
        y.Begin("trigger", settle: false); // 다음 세션: 둘 다 낮춤
        Assert.Equal([1, 1], new[] { _w.Procs[50].Priority, _w.Procs[50].Io });
        _ = _w.SetFails.Add(50);
        y.End(); // 메모리 되돌리기만 실패

        Assert.Equal(2, _w.Procs[50].Io);
        Assert.Equal(1, _w.Procs[50].Priority);
        Assert.Contains("\"priority\": 5", _w.State, StringComparison.Ordinal);
        Assert.DoesNotContain("io_priority", _w.State, StringComparison.Ordinal);

        _ = _w.SetFails.Remove(50);
        Make().Recover();
        Assert.Equal(5, _w.Procs[50].Priority);
        Assert.Null(_w.State);

        _w.Now += TimeSpan.FromMinutes(5);
        BackgroundYield next = Make();
        next.Begin("trigger", settle: false);
        _ = _w.IoSetFails.Add(50);
        next.End();
        Assert.Contains("\"io_priority\": 2", _w.State, StringComparison.Ordinal); // I/O 되돌리기 실패는 파일에 남음
        _ = _w.IoSetFails.Remove(50);
        Make().Recover();
        Assert.Equal(2, _w.Procs[50].Io);
        Assert.Null(_w.State);
    }

    [Fact]
    public void WhenTheStateFileCannotBeWrittenIoPriorityIsNotLoweredEither()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _ = _w.Add(10, "cod.exe");
        _w.WriteFails = true;

        Make().Begin("trigger", settle: false);

        Assert.Empty(_w.IoSets);
        Assert.Equal(2, _w.Procs[50].Io);
        Assert.True(Logged("I/O priority lowered for 0"));
        Assert.Empty(_w.CpuSets);
        Assert.Equal(0x20, _w.Procs[50].Cpu);
        Assert.True(Logged("CPU priority lowered for 0"));
    }

    [Fact]
    public void OnlyNormalCpuClassIsLoweredToBelowNormalAndRestoredAtTheEndWhileOtherClassesAreLeftAlone()
    {
        _ = _w.Add(10, "cod.exe");
        _ = _w.Add(50, "normal.exe");
        int[] others = [0x80, 0x100, 0x8000, 0x4000, 0x40]; // 높음, 실시간, 보통보다 높음, 보통보다 낮음, 유휴
        for (int i = 0; i < others.Length; i++)
        {
            _w.Add(51 + i, "other" + i + ".exe").Cpu = others[i];
        }

        BackgroundYield y = Make();
        y.Begin("trigger", settle: false);

        Assert.Equal([(50, 0x4000)], _w.CpuSets);
        Assert.Equal(others, Enumerable.Range(51, others.Length).Select(pid => _w.Procs[pid].Cpu));
        Assert.Contains("\"cpu_priority\": 32", _w.State, StringComparison.Ordinal);
        Assert.True(Logged("CPU priority lowered for 1"));

        y.End();

        Assert.Equal([(50, 0x4000), (50, 0x20)], _w.CpuSets);
        Assert.Equal(0x20, _w.Procs[50].Cpu);
        Assert.Equal(others, Enumerable.Range(51, others.Length).Select(pid => _w.Procs[pid].Cpu));
        Assert.Null(_w.State);
        Assert.True(Logged("CPU priority restored for 1 processes"));
    }

    [Fact]
    public void AStartFromATriggerAloneLeavesTheCpuClassAndItIsLoweredOnceTheGameShowsUp()
    {
        _ = _w.Add(50, "normal.exe");
        BackgroundYield y = Make();

        y.Begin("trigger", settle: false);

        Assert.Empty(_w.CpuReads); // 게임이 아직 없음: 낮춘 부모가 게임을 띄워 게임이 낮은 등급을 물려받는 일을 막음
        Assert.Empty(_w.CpuSets);
        Assert.Equal([(50, 1)], _w.Sets);
        Assert.Equal(0x20, _w.Procs[50].Cpu);

        _ = _w.Add(10, "cod.exe");
        y.Tick(_w.Snapshot());

        Assert.Equal([(50, 0x4000)], _w.CpuSets);
        Assert.Equal(0x4000, _w.Procs[50].Cpu);
        Assert.Contains("\"cpu_priority\": 32", _w.State, StringComparison.Ordinal);
        Assert.Contains("\"priority\": 5", _w.State, StringComparison.Ordinal); // 메모리 기록은 그대로

        y.Tick(_w.Snapshot());
        Assert.Single(_w.CpuSets); // 한 번만

        y.End();

        Assert.Equal(0x20, _w.Procs[50].Cpu);
        Assert.Equal(5, _w.Procs[50].Priority);
        Assert.Null(_w.State);
    }

    [Fact]
    public void TurningOnlyTheCpuSwitchOffDuringASessionRestoresTheCpuClassAtOnceAndTurningItOnLowersItAgain()
    {
        BackgroundYield y = RunningYield();
        Assert.Equal(0x4000, _w.Procs[50].Cpu);

        _settings = _settings with { Memory = new MemorySettings(true, true, true, false) };
        y.Tick(_w.Snapshot());

        Assert.Equal(0x20, _w.Procs[50].Cpu);
        Assert.Equal(1, _w.Procs[50].Priority); // 메모리·I/O 는 그대로
        Assert.Equal(1, _w.Procs[50].Io);
        Assert.DoesNotContain("cpu_priority", _w.State, StringComparison.Ordinal);
        Assert.True(Logged("CPU priority restored for 1 processes (setting turned off)"));

        y.Tick(_w.Snapshot());
        Assert.Equal(2, _w.CpuSets.Count); // 되풀이하지 않음

        _settings = _settings with { Memory = new MemorySettings(true, true, true, true) };
        y.Tick(_w.Snapshot());

        Assert.Equal(0x4000, _w.Procs[50].Cpu);
        y.End();
        Assert.Equal([0x20, 5, 2], new[] { _w.Procs[50].Cpu, _w.Procs[50].Priority, _w.Procs[50].Io });
        Assert.Null(_w.State);
    }

    [Fact]
    public void ACpuClassChangedByTheUserOrTheProgramDuringTheSessionIsNotTouchedAtTheEnd()
    {
        _ = _w.Add(10, "cod.exe");
        _ = _w.Add(50, "normal.exe");
        BackgroundYield y = Make();
        y.Begin("trigger", settle: false);
        _w.Procs[50].Cpu = 0x80; // 그 사이 높음으로 올림

        y.End();

        Assert.Equal([(50, 0x4000)], _w.CpuSets);
        Assert.Equal(0x80, _w.Procs[50].Cpu);
        Assert.Null(_w.State);
        Assert.True(Logged("CPU priority restored for 0 processes"));
    }

    [Fact]
    public void ACpuClassThatCannotBeSetIsNotRecordedAsLoweredAndTheOtherPrioritiesStillGo()
    {
        _ = _w.Add(10, "cod.exe");
        _ = _w.Add(50, "normal.exe");
        _w.CpuSetFails.Add(50);

        BackgroundYield y = Make();
        y.Begin("trigger", settle: false);

        Assert.Equal(1, _w.Procs[50].Io);
        Assert.True(Logged("CPU priority lowered for 0"));
        y.End();
        Assert.Equal([(50, 0x4000)], _w.CpuSets); // 못 낮춘 것은 되돌리기도 없음
        Assert.Null(_w.State);
    }

    [Fact]
    public void WithTheCpuSwitchOffNoCpuClassIsReadOrSetButMemoryAndIoStillAre()
    {
        _ = _w.Add(10, "cod.exe");
        _ = _w.Add(50, "normal.exe");
        _settings = _settings with { Memory = new MemorySettings(true, true, true, false) };

        Make().Begin("trigger", settle: false);

        Assert.Empty(_w.CpuReads);
        Assert.Empty(_w.CpuSets);
        Assert.Equal([(50, 1)], _w.Sets);
        Assert.Equal([(50, 1)], _w.IoSets);
    }

    [Fact]
    public void ChildrenAndGrandchildrenOfAProcessWithLoweredCpuClassAreRestoredWithIt()
    {
        BackgroundYield y = RunningYield();
        _ = Spawn(61, "grandkid.exe", 60, 40);
        _ = Spawn(60, "kid.exe", 50, 20);

        MaintainAt(y, 2);

        Assert.Equal([(50, 0x4000)], _w.CpuSets); // 물려받은 것은 이미 낮아서 쓰지 않음
        _ = _w.Procs.Remove(10);
        y.End();

        Assert.Equal([0x20, 0x20, 0x20], new[] { 50, 60, 61 }.Select(pid => _w.Procs[pid].Cpu));
        Assert.True(Logged("CPU priority restored for 3 processes"));
        Assert.Null(_w.State);
    }

    [Fact]
    public void AChildWhoseCpuClassWasNotInheritedAsLowIsNotRecordedForCpu()
    {
        BackgroundYield y = RunningYield();
        _ = Spawn(60, "kid.exe", 50, 20).Cpu = 0x80; // 스스로 높음으로 올린 자식

        MaintainAt(y, 2);
        _ = _w.Procs.Remove(10);
        y.End();

        Assert.Equal(0x80, _w.Procs[60].Cpu);
        Assert.DoesNotContain(_w.CpuSets, s => s.Pid == 60);
    }

    [Fact]
    public void ARestartedServiceRestoresTheCpuClassOfTheLoweredAndInheritedProcessesFromTheStateFile()
    {
        BackgroundYield y = RunningYield();
        _ = Spawn(60, "kid.exe", 50, 20);
        MaintainAt(y, 2);
        Assert.Equal([0x4000, 0x4000], new[] { 50, 60 }.Select(pid => _w.Procs[pid].Cpu));

        Make().Recover();

        Assert.Equal([0x20, 0x20], new[] { 50, 60 }.Select(pid => _w.Procs[pid].Cpu));
        Assert.Null(_w.State);
        Assert.True(Logged("CPU priority for 2"));
    }

    [Fact]
    public void ACpuOnlyStateEntryRestoresTheClassAndAnInvalidCpuValueIsDropped()
    {
        _ = _w.Add(50, "big.exe").Cpu = 0x4000;
        _ = _w.Add(51, "other.exe").Cpu = 0x4000;
        _w.State = "{\"lowered\":[{\"pid\":50,\"exe\":\"big.exe\",\"created\":1000,\"cpu_priority\":32},"
            + "{\"pid\":51,\"exe\":\"other.exe\",\"created\":1000,\"cpu_priority\":64}]}"; // 64 (유휴) 는 우리가 적은 값이 아님

        Make().Recover();

        Assert.Equal([(50, 0x20)], _w.CpuSets);
        Assert.Equal([0x20, 0x4000], new[] { 50, 51 }.Select(pid => _w.Procs[pid].Cpu));
        Assert.Null(_w.State);
    }

    [Fact]
    public void ChildrenAndGrandchildrenOfALoweredProcessAreRecordedWithTheAncestorsOriginalAndRestoredAtTheEnd()
    {
        BackgroundYield y = RunningYield();
        _ = Spawn(61, "grandkid.exe", 60, 40); // 손자가 목록에서 먼저 나와도 이어짐
        _ = Spawn(60, "kid.exe", 50, 20);

        MaintainAt(y, 2);

        Assert.Equal([(50, 1)], _w.Sets); // 물려받은 것은 이미 낮아서 쓰지 않음
        Assert.Contains("\"pid\": 60", _w.State, StringComparison.Ordinal);
        Assert.Contains("\"pid\": 61", _w.State, StringComparison.Ordinal);
        _ = _w.Procs.Remove(10);

        y.End();

        Assert.Equal([5, 5, 5], new[] { 50, 60, 61 }.Select(pid => _w.Procs[pid].Priority));
        Assert.True(Logged("memory priority restored for 3 processes (2 inherited)"));
        Assert.Null(_w.State);
    }

    [Fact]
    public void ALowProcessCreatedBeforeItsParentWasLoweredOrWithAnUnrecordedParentIsNeverTouched()
    {
        BackgroundYield y = RunningYield();
        _ = _w.Add(52, "low.exe", priority: 1); // 원래부터 낮은 프로세스 (기록 안 됨)
        _ = Spawn(61, "old.exe", 50, -30); // 부모가 낮춰지기 전에 뜸
        _ = Spawn(62, "child.exe", 52, 20); // 부모가 기록에 없음
        _ = Spawn(63, "orphan.exe", 4000, 20); // 모르는 부모

        MaintainAt(y, 2);
        _ = _w.Procs.Remove(10);
        y.End();

        Assert.Equal([(50, 1), (50, 5)], _w.Sets);
        Assert.Equal([1, 1, 1, 1], new[] { 52, 61, 62, 63 }.Select(pid => _w.Procs[pid].Priority));
        Assert.True(Logged("memory priority restored for 1 processes (0 inherited)"));
    }

    [Fact]
    public void AChildStartedAfterTheLastCheckIsRestoredByTheFinalLookupAtTheEnd()
    {
        BackgroundYield y = RunningYield();
        MaintainAt(y, 2);
        _ = Spawn(60, "late.exe", 50, 150); // 마지막 유지 (2분) 뒤
        _w.Now = _w.Start + TimeSpan.FromMinutes(3);

        y.End();

        Assert.Equal(5, _w.Procs[60].Priority);
        Assert.Equal(5, _w.Procs[50].Priority);
        Assert.True(Logged("memory priority restored for 2 processes (1 inherited)"));
    }

    [Fact]
    public void AChildWhoseParentAlreadyEndedIsStillRecordedAndRestored()
    {
        BackgroundYield y = RunningYield();
        _ = Spawn(60, "kid.exe", 50, 20);
        _ = _w.Procs.Remove(50); // 부모가 먼저 끝남 (기록은 남음)

        MaintainAt(y, 2);

        Assert.Contains("\"pid\": 60", _w.State, StringComparison.Ordinal);
        _ = _w.Procs.Remove(10);
        y.End();

        Assert.Equal(5, _w.Procs[60].Priority);
        Assert.True(Logged("memory priority restored for 1 processes (1 inherited)"));
    }

    [Fact]
    public void ARestartedServiceRestoresTheInheritedEntriesFromTheStateFileToo()
    {
        BackgroundYield y = RunningYield();
        _ = Spawn(60, "kid.exe", 50, 20);
        MaintainAt(y, 2);
        Assert.Equal(1, _w.Procs[60].Priority);

        Make().Recover(); // 서비스가 비정상 종료한 뒤의 새 인스턴스

        Assert.Equal([5, 5], new[] { 50, 60 }.Select(pid => _w.Procs[pid].Priority));
        Assert.Null(_w.State);
        Assert.True(Logged("restored memory priority for 2 processes left by the previous run"));
    }

    [Fact]
    public void ALoweredEntryWhosePidWasReusedByAnotherProcessIsNotTakenAsTheParent()
    {
        BackgroundYield y = RunningYield();
        _ = _w.Procs.Remove(50);
        MaintainAt(y, 2); // 대상에서 빠짐
        YieldWorld.Proc reused = _w.Add(50, "reuse.exe", priority: 1); // 같은 pid 를 다른 프로세스가 재사용, 원래부터 낮음
        reused.Created = _w.Start.ToFileTime() + (60 * Second);
        _ = Spawn(60, "kid.exe", 50, 180);

        MaintainAt(y, 4);
        _ = _w.Procs.Remove(10);
        y.End();

        Assert.Equal([(50, 1)], _w.Sets);
        Assert.Equal(1, _w.Procs[60].Priority);
        Assert.Equal(1, _w.Procs[50].Priority);
    }

    [Fact]
    public void AnExcludedChildOfALoweredProcessIsNeitherReadNorWritten()
    {
        BackgroundYield y = RunningYield();
        _ = Spawn(60, "vgtray.exe", 50, 20); // 안티치트 이름
        _ = Spawn(61, "service.exe", 50, 20);
        _w.Procs[61].Session = 0;

        MaintainAt(y, 2);
        _ = _w.Procs.Remove(10);
        y.End();

        Assert.DoesNotContain(60, _w.PriorityReads);
        Assert.DoesNotContain(61, _w.Touched);
        Assert.DoesNotContain(_w.Sets, s => s.Pid is 60 or 61);
        Assert.Equal([1, 1], new[] { 60, 61 }.Select(pid => _w.Procs[pid].Priority));
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
    public void SettingDefaultsAreReadAndBadShapesAndUnknownKeysAreRejected()
    {
        Assert.True(ParseMemory(null).BackgroundYield);
        Assert.True(ParseMemory(new JsonObject()).BackgroundYield);
        Assert.False(ParseMemory(new JsonObject { ["background_yield"] = false }).BackgroundYield);
        Assert.True(ParseMemory(new JsonObject { ["background_yield"] = true }).BackgroundYield);
        Assert.Throws<SettingsException>(() => ParseMemory(new JsonObject { ["background_yield"] = "yes" }));
        Assert.True(ParseMemory(null).BackgroundIoPriority);
        Assert.False(ParseMemory(new JsonObject { ["background_io_priority"] = false }).BackgroundIoPriority);
        Assert.True(ParseMemory(new JsonObject { ["background_io_priority"] = false }).BackgroundYield);
        Assert.Throws<SettingsException>(() => ParseMemory(new JsonObject { ["background_io_priority"] = 1 }));
        Assert.False(ParseMemory(null).BackgroundCpuPriority); // 기본 꺼짐
        Assert.True(ParseMemory(new JsonObject { ["background_cpu_priority"] = true }).BackgroundCpuPriority);
        Assert.False(ParseMemory(new JsonObject { ["background_cpu_priority"] = false }).BackgroundCpuPriority);
        Assert.True(ParseMemory(new JsonObject { ["background_cpu_priority"] = false }).BackgroundIoPriority);
        Assert.Throws<SettingsException>(() => ParseMemory(new JsonObject { ["background_cpu_priority"] = 1 }));
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
        Assert.Empty(_w.IoReads);
        Assert.Empty(_w.IoSets);
        Assert.Empty(_w.CpuReads);
        Assert.Empty(_w.CpuSets);

        _settings = _settings with { Memory = new MemorySettings(true) };
        y.Begin("trigger", settle: false);
        Assert.Single(_w.Sets);

        _settings = _settings with { Memory = new MemorySettings(false) };
        y.Tick([(50, "big.exe")]);
        Assert.Equal([(50, 1), (50, 5)], _w.Sets);
    }

    [Fact]
    public async Task ALauncherStartDoesNotBeginTheYieldAndTheGameExeStartDoes()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _settings = new AsterSettings(1000, [Cod()]);
        var watcher = new CapturingWatcher();
        var worker = new Worker(_settings, _log, watcher, null, new FakeStreamer(), new ReapplyChecker(ReapplyTests.NoIoEnv(), _log), backgroundYield: Make());
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await WaitUntil(() => watcher.OnStart is not null);

            watcher.OnStart!(new ProcessStart(1, "codlauncher.exe", Stopwatch.GetTimestamp())); // 트리거 프로그램 (게임 실행 파일이 아님)
            await Task.Delay(300); // 비동기로 도는 양보가 불리지 않았음을 확인할 틈
            Assert.Empty(_w.Sets);
            Assert.Empty(_w.Trimmed);
            Assert.Equal(0, _w.ProcessListCalls);
            Assert.Null(_w.State);

            watcher.OnStart!(new ProcessStart(2, "cod.exe", Stopwatch.GetTimestamp()));
            await WaitUntil(() => _w.Sets.Contains((50, 1)));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
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
        BackgroundYield second = _w.Yielder(() => _settings, _log);
        second.Begin("trigger", settle: false);

        Assert.Empty(_w.Trimmed);
        Assert.Empty(_w.Sets);
        Assert.Contains(80, _w.Described);
    }

    // 쓰고 있는 프로세스 판정: CPU 와 읽기·쓰기 전송 누계를 1분 전 표본 (Make) 과 비교합니다. 만든 세 프로세스: 50 은 CPU, 51 은 I/O, 52 는 쉼.
    private BackgroundYield SessionWithBusyProcesses()
    {
        _ = _w.Add(50, "cpu.exe", wsMib: 500);
        _ = _w.Add(51, "io.exe", wsMib: 400);
        _ = _w.Add(52, "idle.exe", wsMib: 300);
        _ = _w.Add(10, "cod.exe");
        BackgroundYield y = Make();
        _w.Procs[50].CpuTicks = 60 * Second / 10; // 한 코어의 10%
        _w.Procs[51].IoBytes = 60L * 64 * 1024; // 초당 64 KiB
        return y;
    }

    [Fact]
    public void ProcessesInUseAreNeitherTrimmedNorGivenALowerMemoryPriorityButTheirIoPriorityIsStillLowered()
    {
        BackgroundYield y = SessionWithBusyProcesses();

        y.Begin("game started", settle: false);

        Assert.Equal([52], _w.Trimmed); // 처음 비우기 전에도 가림
        Assert.Equal([(52, 1)], _w.Sets);
        Assert.Equal([5, 5, 1], new[] { 50, 51, 52 }.Select(pid => _w.Procs[pid].Priority));
        Assert.Equal([50, 51, 52], _w.IoSets.Select(s => s.Pid).Order()); // I/O 우선순위 낮추기는 그대로
        Assert.True(Logged("memory priority lowered for 1, I/O priority lowered for 3, CPU priority lowered for 3, 2 in-use processes spared"));
    }

    [Fact]
    public void ABusyProcessStartedAfterTheSessionIsAlsoSparedAtTheNextMaintenanceAndTheRetrimSkipsIt()
    {
        BackgroundYield y = RunningSession(Lists(1000, 0));
        _ = _w.Add(60, "late-busy.exe", wsMib: 400);
        _ = _w.Add(61, "late-idle.exe", wsMib: 300);
        _w.Now = _w.Start + TimeSpan.FromMinutes(1);
        y.Tick(_w.Snapshot()); // 표본: 둘 다 쉼
        _w.Procs[60].CpuTicks = 60 * Second / 10;

        TickAt(y, 2, 100, Lists(1000, 600));

        Assert.DoesNotContain(_w.Sets, s => s.Pid == 60);
        Assert.Contains(_w.Sets, s => s.Pid == 61);
        Assert.DoesNotContain(60, _w.Trimmed);
        Assert.Contains(61, _w.Trimmed);
        Assert.True(Logged("1 in-use processes spared"));
    }

    [Fact]
    public void WithoutAUsageReadingNothingIsTrimmedOrLoweredInMemoryPriority()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _w.UsageFails = true;

        Make().Begin("game started", settle: false);

        Assert.Empty(_w.Trimmed);
        Assert.Empty(_w.Sets);
        Assert.Equal([(50, 1)], _w.IoSets); // I/O 우선순위는 사용량과 무관하게 그대로
    }

    [Fact]
    public void WithoutEarlierSamplesEveryProcessCountsAsInUseUntilTicksHaveBuiltUpTheHistory()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        var y = new BackgroundYield(() => _settings, _w.Env(), _log); // 서비스가 막 시작해 표본이 없음

        y.Begin("game started", settle: false);

        Assert.Empty(_w.Trimmed);
        Assert.Empty(_w.Sets);
        Assert.True(Logged("1 in-use processes spared"));
    }

    [Fact]
    public void TicksKeepSamplingWithoutASessionSoTheFirstTrimAlreadyKnowsWhoIsBusy()
    {
        _ = _w.Add(50, "busy.exe", wsMib: 500);
        _ = _w.Add(51, "idle.exe", wsMib: 400);
        var y = new BackgroundYield(() => _settings, _w.Env(), _log);
        y.Tick([]);
        _w.Now += TimeSpan.FromSeconds(30);
        _w.Procs[50].CpuTicks += 30 * Second / 10;
        y.Tick([]);
        _w.Now += TimeSpan.FromSeconds(30);
        _w.Procs[50].CpuTicks += 30 * Second / 10;

        y.Begin("game started", settle: false);

        Assert.Equal([51], _w.Trimmed);
        Assert.Equal([(51, 1)], _w.Sets);
    }

    [Fact]
    public void TheRetrimIsSkippedWhileAvailableMemoryStillCoversTheGameAndTheFloor()
    {
        BackgroundYield y = RunningSession(Lists(1000, 0));
        _w.Avail = () => 16384;
        Assert.Single(_w.Trimmed);

        TickAt(y, 2, 100, Lists(1000, 600)); // 캐시는 재사용됐지만 가용 메모리가 충분

        Assert.Single(_w.Trimmed);
        Assert.True(Logged("background yield: not trimmed again, memory is sufficient (available 16384 MiB, expected game use 8192 MiB, floor 8192 MiB), cache repurposed 600 MiB in 120 s"));
    }

    [Fact]
    public void TheRetrimCountsTheMemoryTheGameAlreadyTookOutOfWhatItStillNeeds()
    {
        _ = _w.Add(50, "big.exe", wsMib: 500);
        _ = _w.Add(10, "cod.exe");
        _w.Avail = () => 16384;
        _w.ListsNow = Lists(1000, 0);
        BackgroundYield y = Make();
        y.Begin("game started", settle: false);
        Assert.Empty(_w.Trimmed);
        _w.Avail = () => 16384 - 8192 + 100; // 게임이 8092 MiB 를 이미 가져가 남은 예상은 100 MiB: 가용 8292 MiB 가 하한 8192 MiB + 100 MiB 를 채움

        _w.Now = _w.Start + TimeSpan.FromMinutes(2);
        _w.Procs[50].Ws = 500 * Mib;
        _w.ListsNow = Lists(1000, 600);
        y.Tick(_w.Snapshot());

        Assert.Empty(_w.Trimmed);
        Assert.True(Logged("not trimmed again, memory is sufficient (available 8292 MiB"));
    }

    [Fact]
    public void CpuPriorityIsOffByDefaultAndNeitherReadNorWrittenThen()
    {
        _settings = _settings with { Memory = MemorySettings.Default };
        _ = _w.Add(50, "normal.exe");
        _ = _w.Add(10, "cod.exe");

        Make().Begin("game started", settle: false);

        Assert.Empty(_w.CpuReads);
        Assert.Empty(_w.CpuSets);
        Assert.Equal([(50, 1)], _w.Sets);
        Assert.Equal([(50, 1)], _w.IoSets);
    }

    [Fact]
    public void ARestartInsideASessionThatAlreadyLoweredProcessesStillLowersTheirCpuClassOnceTheSettingIsOnAtTheNextTick()
    {
        _settings = _settings with { Memory = new MemorySettings(true, true, true, false) };
        _ = _w.Add(50, "normal.exe");
        _ = _w.Add(10, "cod.exe");
        BackgroundYield y = Make();
        y.Begin("game started", settle: false);
        Assert.Empty(_w.CpuSets);
        _settings = _settings with { Memory = new MemorySettings(true, true, true, true) };
        _w.Now += TimeSpan.FromSeconds(61);

        y.Begin("game started", settle: false); // 같은 세션 안의 두 번째 시작: 이미 낮춘 대상은 다시 보지 않음
        y.Tick(_w.Snapshot());

        Assert.Equal([(50, 0x4000)], _w.CpuSets);
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

/// 쓰고 있는 프로세스 판정의 값 규칙 (프로세스·시계 없이).
public sealed class UsageWindowTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UnixEpoch;

    private static ProcessUsage U(int pid, long cpuTicks, long ioBytes = 0) => new(pid, "p.exe", cpuTicks, 0, ioBytes);

    private static UsageWindow Two(ProcessUsage before, ProcessUsage after, int seconds = 60)
    {
        var w = new UsageWindow();
        w.Add(T0, [before]);
        w.Add(T0 + TimeSpan.FromSeconds(seconds), [after]);
        return w;
    }

    [Fact]
    public void WithoutTwoSamplesFarEnoughApartItCannotJudgeAndCountsAsInUse()
    {
        var w = new UsageWindow();
        Assert.True(w.IsInUse(1));
        w.Add(T0, [U(1, 0)]);
        Assert.True(w.IsInUse(1));
        w.Add(T0 + TimeSpan.FromSeconds(9), [U(1, 0)]);
        Assert.True(w.IsInUse(1)); // 간격 10초 미만
        w.Add(T0 + TimeSpan.FromSeconds(10), [U(1, 0)]);
        Assert.False(w.IsInUse(1));
    }

    [Fact]
    public void CpuAtHalfAPercentOfACoreOrMoreIsInUse()
    {
        Assert.True(Two(U(1, 0), U(1, 3_000_000)).IsInUse(1)); // 60초에 0.3초 = 0.5%
        Assert.False(Two(U(1, 0), U(1, 2_999_999)).IsInUse(1));
    }

    [Fact]
    public void ReadPlusWriteTransferAt32KibPerSecondOrMoreIsInUse()
    {
        Assert.True(Two(U(1, 0, 0), U(1, 0, 60L * 32 * 1024)).IsInUse(1));
        Assert.False(Two(U(1, 0, 0), U(1, 0, (60L * 32 * 1024) - 1)).IsInUse(1));
    }

    [Fact]
    public void AProcessThatIsNotInTheBaselineIsJudgedByItsWholeTotalAndOneMissingFromTheLatestSampleIsInUse()
    {
        var w = new UsageWindow();
        w.Add(T0, [U(9, 0)]);
        w.Add(T0 + TimeSpan.FromSeconds(60), [U(1, 3_000_000), U(2, 100)]);
        Assert.True(w.IsInUse(1)); // 구간 안에 떠서 누계 전체가 사용량
        Assert.False(w.IsInUse(2));
        Assert.True(w.IsInUse(3)); // 최근 표본에 없음: 모름
    }

    [Fact]
    public void AReusedPidWhoseTotalsAreSmallerThanTheBaselineIsJudgedByItsOwnTotals()
    {
        Assert.False(Two(U(1, 900_000_000, 900_000_000), U(1, 100, 100)).IsInUse(1));
    }

    [Fact]
    public void TheBaselineIsTheNewestSampleAtLeastOneWindowOldSoAnOldBurstDropsOut()
    {
        var w = new UsageWindow();
        w.Add(T0, [U(1, 0)]);
        w.Add(T0 + TimeSpan.FromSeconds(30), [U(1, 100_000_000)]); // 10초 분량의 CPU 를 쓴 뒤
        w.Add(T0 + TimeSpan.FromSeconds(60), [U(1, 100_000_000)]);
        Assert.True(w.IsInUse(1)); // 기준은 0초 표본: 구간 안에 있음
        w.Add(T0 + TimeSpan.FromSeconds(90), [U(1, 100_000_000)]);
        Assert.False(w.IsInUse(1)); // 기준이 30초 표본으로 옮겨 가 그 뒤로는 쉼
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

/// 실제 Windows 호출: 시험이 직접 띄운 자식 프로세스와 시험 프로세스 자신의 CPU 우선순위 등급만 바꿉니다 (끝나면 원래 값으로). 관리자 권한은 필요 없습니다.
[Trait("Category", "Live")]
public sealed class ProcessCpuClassLiveTests
{
    [Fact]
    public void ANormalChildIsLoweredToBelowNormalAndRestoredAndALoweredParentPassesItToANewChild()
    {
        using Process child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        int self = Environment.ProcessId;
        int? selfOriginal = ProcessMemory.CpuPriorityClass(self);
        try
        {
            Assert.Equal(ProcessMemory.CpuClassNormal, ProcessMemory.CpuPriorityClass(child.Id));
            Assert.True(ProcessMemory.SetCpuPriorityClass(child.Id, ProcessMemory.CpuClassBelowNormal));
            Assert.Equal(ProcessMemory.CpuClassBelowNormal, ProcessMemory.CpuPriorityClass(child.Id));
            Assert.True(ProcessMemory.SetCpuPriorityClass(child.Id, ProcessMemory.CpuClassNormal));
            Assert.Equal(ProcessMemory.CpuClassNormal, ProcessMemory.CpuPriorityClass(child.Id));

            Assert.Equal(ProcessMemory.CpuClassNormal, selfOriginal);
            Assert.True(ProcessMemory.SetCpuPriorityClass(self, ProcessMemory.CpuClassBelowNormal));
            using Process grandchild = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            try
            {
                Assert.Equal(ProcessMemory.CpuClassBelowNormal, ProcessMemory.CpuPriorityClass(grandchild.Id)); // 낮춘 부모가 띄운 자식은 낮은 등급으로 시작
            }
            finally
            {
                grandchild.Kill();
            }
        }
        finally
        {
            _ = ProcessMemory.SetCpuPriorityClass(self, selfOriginal ?? ProcessMemory.CpuClassNormal);
            child.Kill();
        }

        Assert.Equal(selfOriginal, ProcessMemory.CpuPriorityClass(self));
        child.WaitForExit();
        Assert.Null(ProcessMemory.CpuPriorityClass(1 << 30)); // 없는 프로세스는 예외 없이 null/false
        Assert.False(ProcessMemory.SetCpuPriorityClass(1 << 30, ProcessMemory.CpuClassBelowNormal));
    }

    /// 실제 호출로 배경 양보 전체 경로를 돕니다: 처리 대상 목록만 시험이 띄운 자식 프로세스로 좁히고 (다른 프로그램은 건드리지 않음), 나머지는 실제 Windows 호출입니다.
    [Fact]
    public void TheBackgroundYieldLowersOnlyNormalChildrenAndRestoresThemWhileAChildAtHighIsLeftAlone()
    {
        string dir = Path.Combine(Path.GetTempPath(), "cpuyield_live_" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(dir);
        string exe = Path.Combine(dir, "cpuyield_probe.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        var children = new List<Process>();
        string? state = null;
        try
        {
            for (int i = 0; i < 3; i++)
            {
                children.Add(Process.Start(new ProcessStartInfo(exe, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!);
            }

            int[] pids = [.. children.Select(c => c.Id)];
            Assert.True(ProcessMemory.SetCpuPriorityClass(pids[2], 0x80)); // 셋째는 사용자가 높음으로 정한 프로세스
            var env = new BackgroundYieldEnv
            {
                Processes = () => [.. ProcessSnapshot.Take().Where(p => pids.Contains(p.Pid)), (1 << 29, "cod.exe")], // 게임이 떠 있는 것으로 보이게 함 (이름 단계에서 걸러져 열지 않음)
                SessionId = ProcessMemory.SessionId,
                Describe = ProcessMemory.Describe,
                Trim = ProcessMemory.Trim,
                MemoryPriority = ProcessMemory.MemoryPriority,
                SetMemoryPriority = ProcessMemory.SetMemoryPriority,
                IoPriority = ProcessMemory.IoPriority,
                SetIoPriority = ProcessMemory.SetIoPriority,
                CpuClass = ProcessMemory.CpuPriorityClass,
                SetCpuClass = ProcessMemory.SetCpuPriorityClass,
                AvailableMib = () => 100000,
                WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                SelfPid = Environment.ProcessId,
                ReadState = () => state,
                WriteState = text => state = text,
                DeleteState = () => state = null,
            };
            var log = new ListLog();
            var yielder = new BackgroundYield(() => new AsterSettings(1000, [Games.Make("cod", "cod.exe")]) with { Memory = new MemorySettings(BackgroundCpuPriority: true) }, env, log);

            yielder.Begin("test", settle: false);

            Assert.Equal([ProcessMemory.CpuClassBelowNormal, ProcessMemory.CpuClassBelowNormal, 0x80], pids.Select(p => ProcessMemory.CpuPriorityClass(p)!.Value));
            Assert.Contains("\"cpu_priority\": 32", state, StringComparison.Ordinal);
            Assert.Contains(log.Lines, l => l.Contains("CPU priority lowered for 2", StringComparison.Ordinal));

            yielder.End();

            Assert.Equal([ProcessMemory.CpuClassNormal, ProcessMemory.CpuClassNormal, 0x80], pids.Select(p => ProcessMemory.CpuPriorityClass(p)!.Value));
            Assert.Null(state);
            Assert.Contains(log.Lines, l => l.Contains("CPU priority restored for 2 processes", StringComparison.Ordinal));
        }
        finally
        {
            foreach (Process c in children)
            {
                c.Kill();
                c.WaitForExit();
                c.Dispose();
            }

            Directory.Delete(dir, true);
        }
    }
}

/// 실제 Windows 호출: 시험 프로세스 자신과 자신이 띄운 자식 프로세스의 I/O 우선순위만 바꿉니다 (다른 프로세스는 건드리지 않음, 끝나면 원래 값으로). 관리자 권한은 필요 없습니다.
[Trait("Category", "Live")]
public sealed class ProcessIoPriorityLiveTests
{
    [Fact]
    public void LoweringTheProcessIsInheritedByANewChildAndRestoringBringsTheOriginalBack()
    {
        int self = Environment.ProcessId;
        int? original = ProcessMemory.IoPriority(self);
        Assert.NotNull(original);
        Assert.True(ProcessMemory.SetIoPriority(self, ProcessMemory.IoPriorityLow));
        try
        {
            Assert.Equal(ProcessMemory.IoPriorityLow, ProcessMemory.IoPriority(self));
            using Process child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
            try
            {
                Assert.Equal(ProcessMemory.IoPriorityLow, ProcessMemory.IoPriority(child.Id)); // 낮춘 부모가 띄운 자식은 낮음으로 시작
                Assert.True(ProcessMemory.SetIoPriority(child.Id, ProcessMemory.IoPriorityNormal));
                Assert.Equal(ProcessMemory.IoPriorityNormal, ProcessMemory.IoPriority(child.Id));
            }
            finally
            {
                child.Kill();
                child.WaitForExit();
            }

            Assert.Null(ProcessMemory.IoPriority(1 << 30)); // 없는 프로세스는 예외 없이 null/false
            Assert.False(ProcessMemory.SetIoPriority(1 << 30, ProcessMemory.IoPriorityLow));
        }
        finally
        {
            Assert.True(ProcessMemory.SetIoPriority(self, original.Value));
        }

        Assert.Equal(original, ProcessMemory.IoPriority(self));
    }

    [Fact]
    public void AWindowsFolderChildAndItsOwnChildStartedByTheLoweredTestProcessInheritLowAndAreRestorable()
    {
        int self = Environment.ProcessId;
        int? original = ProcessMemory.IoPriority(self);
        Assert.NotNull(original);
        Assert.True(ProcessMemory.SetIoPriority(self, ProcessMemory.IoPriorityLow));
        try
        {
            using Process shell = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c ping -n 30 127.0.0.1 >nul") { CreateNoWindow = true, UseShellExecute = false })!;
            try
            {
                ProcessInfo? info = ProcessMemory.Describe(shell.Id);
                Assert.NotNull(info);
                Assert.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), info.Value.Path, StringComparison.OrdinalIgnoreCase); // 경로 규칙으로 빠지는 프로세스
                Assert.Equal(self, info.Value.Parent);
                Assert.Equal(ProcessMemory.IoPriorityLow, ProcessMemory.IoPriority(shell.Id));
                int? grandchild = null;
                for (int i = 0; i < 100 && grandchild is null; i++)
                {
                    grandchild = Process.GetProcessesByName("ping").Select(p => p.Id).Cast<int?>().FirstOrDefault(id => ProcessMemory.Describe(id!.Value)?.Parent == shell.Id);
                    if (grandchild is null)
                    {
                        Thread.Sleep(50);
                    }
                }

                Assert.NotNull(grandchild);
                Assert.Equal(ProcessMemory.IoPriorityLow, ProcessMemory.IoPriority(grandchild.Value)); // 낮은 값을 물려받은 손자
                Assert.True(ProcessMemory.SetIoPriority(shell.Id, ProcessMemory.IoPriorityNormal));
                Assert.Equal(ProcessMemory.IoPriorityNormal, ProcessMemory.IoPriority(shell.Id));
            }
            finally
            {
                foreach (Process p in Process.GetProcessesByName("ping").Where(p => ProcessMemory.Describe(p.Id)?.Parent == shell.Id))
                {
                    p.Kill();
                }

                shell.Kill();
                shell.WaitForExit();
            }
        }
        finally
        {
            Assert.True(ProcessMemory.SetIoPriority(self, original.Value));
        }
    }
}

/// 실제 Windows 호출: 읽기만 합니다. 시험이 직접 띄운 자식 프로세스의 부모 pid 가 시험 프로세스로 나오는지.
[Trait("Category", "Live")]
public sealed class ProcessParentLiveTests
{
    [Fact]
    public void DescribeReportsTheCreatingProcessAsTheParentOfAChildProcess()
    {
        using Process child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        try
        {
            ProcessInfo? info = ProcessMemory.Describe(child.Id);
            Assert.NotNull(info);
            Assert.Equal(Environment.ProcessId, info.Value.Parent);
        }
        finally
        {
            child.Kill();
        }
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
