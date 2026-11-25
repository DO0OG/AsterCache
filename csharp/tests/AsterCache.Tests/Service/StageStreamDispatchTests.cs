using System.Diagnostics;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 단계 스트림 디스패처: 가짜 시계·가짜 읽기 카운터·가짜 스트리머로 첫 실행 파일 (앞부분 + 첫 단계)·이후 실행 파일·끄기를 시험합니다.
public sealed class StageStreamDispatchTests
{
    private static readonly GameSettings Game = Games.Make("cod", "cod.exe", "sp26-cod.exe", "Battle.net.exe");
    private static readonly ReadOp A = new(@"D:\Games\cod\a.bin", 0, 192L << 20);
    private static readonly ReadOp B = new(@"D:\Games\cod\b.bin", 0, 192L << 20);

    private readonly StageFakeStreamer _streamer = new();
    private readonly ListLog _log = new();
    private readonly List<(bool Low, int Thread)> _io = [];
    private readonly List<(int Pid, string Exe, long Bytes)> _counters = [];
    private double _sec;
    private long _available = 20_000;
    private bool _queuedOther;
    private bool _backingOff;
    private int _lookups; // 프로세스·읽기 카운터 조회 횟수
    private ManualResetEventSlim? _samplerGate; // 있으면 표본 스레드가 시계를 읽다가 멈춤 (진행 중인 표본을 흉내)
    private readonly ManualResetEventSlim _samplerEntered = new(); // 표본 스레드가 시계 읽기에 들어섰음을 알림
    private Thread? _handleThread; // Handle 을 부르는 (시험) 스레드
    private int _namedLookups; // 이름 있는 읽기 카운터 조회 횟수
    private Action? _onSleep;
    private bool _stage = true;
    private MemoryLists? _lists = new(long.MaxValue, 0); // 메모리 목록 조회 결과 (null 은 조회 실패)
    private IReadOnlyList<(int Pid, string Exe)> _processes = [];

    private GameDispatcher Dispatcher()
    {
        _handleThread = Thread.CurrentThread;
        return Create();
    }

    private GameDispatcher Create() =>
        new([Game, Games.Make("other", "other.exe")], _streamer, _log, () => DateTimeOffset.UnixEpoch.AddSeconds(_sec), new DispatchEnv
        {
            BackingOff = _ => _backingOff,
            OtherProcesses = (g, s) =>
            {
                _lookups++;
                return DispatchEnv.RegisteredOthers(g, _processes, s.Pid);
            },
            Age = s => TimeSpan.FromSeconds(_sec - s.Timestamp),
            ReadCounters = _ =>
            {
                _lookups++;
                return [.. _counters.Select(c => (c.Pid, c.Bytes))];
            },
            ReadCountersWithExeNames = _ =>
            {
                _lookups++;
                _namedLookups++;
                return [.. _counters];
            },
            Timestamp = () =>
            {
                if (_samplerGate is { } gate && Thread.CurrentThread != _handleThread)
                {
                    _samplerEntered.Set();
                    gate.Wait();
                }

                return (long)(_sec * Stopwatch.Frequency);
            },
            Sleep = (span, _) =>
            {
                _sec += span.TotalSeconds;
                _onSleep?.Invoke();
            },
            SetReadIoLow = low =>
            {
                _io.Add((low, Environment.CurrentManagedThreadId));
                return true;
            },
            AvailableMib = () => _available,
            Lists = () => _lists,
            OtherGameQueued = _ => _queuedOther,
            StageStreamEnabled = () => _stage,
        });

    private ProcessStart Start(int pid, string exe) => new(pid, exe, (long)_sec);

    private bool Contains(string text) => _log.Lines.Any(l => l.Contains(text, StringComparison.Ordinal));

    private void RunningFirst()
    {
        _processes = [(5, "cod.exe")];
        _counters.Add((5, "cod.exe", 0));
    }

    [Fact]
    public void FirstExeWithoutStageInfoStreamsTheWholeListAsBefore()
    {
        RunningFirst();

        Assert.True(Dispatcher().Handle(Start(5, "cod.exe")));

        Assert.Equal(["all"], _streamer.Phases);
        Assert.Contains(_log.Lines, l => l.Contains("streamed ", StringComparison.Ordinal) && l.EndsWith("MB/s)", StringComparison.Ordinal));
        Assert.Empty(_io);
    }

    [Fact]
    public void FirstExeWithAStagePlanStreamsOnlyItsOpsAndNothingElse()
    {
        RunningFirst();
        _streamer.FirstOps = [A];
        _streamer.OnBatch = (_, _) => _sec += 2;

        Assert.True(Dispatcher().Handle(Start(5, "cod.exe")));

        Assert.Equal([A.Path], _streamer.Phases); // 다음 단계는 그 실행 파일이 뜰 때 이후 실행 파일 경로가 흘림
        Assert.Contains(_log.Lines, l => l.Contains(": streamed ", StringComparison.Ordinal) && l.EndsWith("MB/s)", StringComparison.Ordinal));
        Assert.Empty(_io); // 첫 스트림은 우선순위를 바꾸지 않음
        Assert.Equal(0, _namedLookups);
    }

    [Fact]
    public void MemoryGateStopsTheFirstStreamAndNeverTouchesTheReserve()
    {
        RunningFirst();
        _available = 4096 + 10; // 예비량 4,096 MiB 를 빼면 10 MiB: 다음 묶음 (64 MiB) 보다 작음
        _streamer.OnBatch = (_, _) => _sec += 2;

        Assert.False(Dispatcher().Handle(Start(5, "cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("start stream stopped at memory reserve after ", StringComparison.Ordinal));
        Assert.Empty(_streamer.BatchCount);
    }

    [Fact]
    public void DisabledSettingKeepsTheOldBehaviorWithoutAnyMemoryGate()
    {
        RunningFirst();
        _stage = false;
        _available = 100; // 게이트가 있으면 멈췄을 값
        _streamer.FirstOps = [A]; // 꺼져 있으면 계획을 묻지도 않음

        Assert.True(Dispatcher().Handle(Start(5, "cod.exe")));

        Assert.Equal(["all"], _streamer.Phases);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("memory reserve", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledSettingStillSkipsALaterExeAsAlreadyRunning()
    {
        _stage = false;
        RunningFirst();
        GameDispatcher d = Dispatcher();
        Assert.True(d.Handle(Start(5, "cod.exe")));
        _sec = 100;
        _processes = [(5, "cod.exe"), (6, "sp26-cod.exe")];

        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("start stream skipped (game already running: cod.exe(5))", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("stage stream", StringComparison.Ordinal));
    }

    private GameDispatcher DispatcherAfterFirstExe()
    {
        RunningFirst();
        GameDispatcher d = Dispatcher();
        Assert.True(d.Handle(Start(5, "cod.exe")));
        _streamer.Phases.Clear();
        _streamer.BatchCount.Clear();
        _sec = 100; // 첫 프로세스는 관측상 Settle 보다 오래됨
        _processes = [(5, "cod.exe"), (6, "sp26-cod.exe")];
        _counters.Add((6, "sp26-cod.exe", 0));
        return d;
    }

    [Fact]
    public void LaterExeWithItsStageStreamsAtLowPriorityAndRaisesWhenTheGameIsQuiet()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0);
        int batch = 0;
        _streamer.OnBatch = (_, _) =>
        {
            _sec += 4; // 게임 프로세스의 읽기: 처음 한 번만 바쁨, 그 뒤 조용
            if (batch++ == 0)
            {
                _counters[1] = (6, "sp26-cod.exe", 400L << 20);
            }
        };

        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("stage stream sp26-cod.exe 192 MiB in ", StringComparison.Ordinal) && l.Contains("completed), yielded", StringComparison.Ordinal));
        Assert.Equal([B.Path], _streamer.Phases);
        Assert.Equal([true, false], _io.Select(c => c.Low)); // 낮음으로 시작, 끝에 보통으로 되돌림 (중간 전환은 BigOp 시험이 묶음별로 고정)
    }

    [Fact]
    public void LaterExeWithoutStageInfoStreamsOnlyAfterTheNewProcessShowsLoadEvidence()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _streamer.LaterPlans = _ => new StageLaterPlan([A, B], false, 0);
        _streamer.OnBatch = (_, _) => _sec += 2;
        _counters[1] = (6, "sp26-cod.exe", 500L << 20); // 누적은 크지만 대기 동안 늘지 않음
        _onSleep = () => _counters[1] = (6, "sp26-cod.exe", _counters[1].Bytes + (1L << 20) / 3);

        Assert.False(d.Handle(Start(6, "sp26-cod.exe"))); // 대기 시작 뒤 10 초 안에 64 MiB 가 안 늘어남

        Assert.Contains(_log.Lines, l => l.Contains("stage stream skipped (sp26-cod.exe: no load evidence, 3 MiB in 10 s)", StringComparison.Ordinal));
        Assert.Empty(_streamer.Phases);
    }

    [Fact]
    public void LaterExeWithoutStageInfoStreamsWhenTheEvidenceIsThere()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _streamer.LaterPlans = _ => new StageLaterPlan([A, B], false, 0);
        _streamer.OnBatch = (_, _) => _sec += 2;
        _counters[1] = (6, "sp26-cod.exe", 500L << 20);
        _onSleep = () => _counters[1] = (6, "sp26-cod.exe", _counters[1].Bytes + (80L << 20)); // 대기 동안 80 MiB 늘어남

        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Equal([A.Path], _streamer.Phases); // 가짜 스트리머는 첫 목록 항목 이름으로 구분
        Assert.Contains(_log.Lines, l => l.Contains("stage stream sp26-cod.exe", StringComparison.Ordinal));
    }

    [Fact]
    public void EvidenceWaitEndsAtOnceWhenAnotherGamesStartIsQueued()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _streamer.LaterPlans = _ => new StageLaterPlan([A], false, 0);
        _queuedOther = true;

        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("stage stream skipped (sp26-cod.exe: another game started)", StringComparison.Ordinal));
        Assert.Equal(100, _sec); // 기다리지 않음
    }

    [Fact]
    public void LaterBoundaryCountsOnlyTheNewProcessReads()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0);
        int batch = 0;
        _streamer.OnBatch = (_, _) =>
        {
            _sec += 5;
            _counters[0] = (5, "cod.exe", (batch + 1) * (300L << 20)); // 먼저 떠 있던 프로세스의 큰 읽기 (섞이면 경계가 됨)
            batch++;
        };

        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.DoesNotContain(_log.Lines, l => l.Contains("load boundary", StringComparison.Ordinal));
    }

    [Fact]
    public void LaterExeHonorsTheSixtySecondCooldownPerExe()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0);
        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));
        _streamer.Phases.Clear();

        _sec = 130;
        _processes = [(5, "cod.exe"), (7, "sp26-cod.exe")];
        _counters.Add((7, "sp26-cod.exe", 0));
        Assert.False(d.Handle(Start(7, "sp26-cod.exe")));
        Assert.Contains(_log.Lines, l => l.Contains("stage stream skipped (sp26-cod.exe: cooldown ", StringComparison.Ordinal));
        Assert.Empty(_streamer.Phases);

        _sec = 170;
        Assert.True(d.Handle(Start(8, "sp26-cod.exe")));
        Assert.Equal([B.Path], _streamer.Phases);
    }

    [Fact]
    public void LaterExeWithNoBlocksIsSkippedWithTheReason()
    {
        GameDispatcher d = DispatcherAfterFirstExe();

        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("stage stream skipped (sp26-cod.exe: no blocks in the start list)", StringComparison.Ordinal));
    }

    [Fact]
    public void LauncherExeIsNotALaterStartAndKeepsTheOldPath()
    {
        RunningFirst();
        GameDispatcher d = Dispatcher();
        Assert.True(d.Handle(Start(5, "cod.exe")));
        _sec = 100;
        _processes = [(5, "cod.exe"), (6, "Battle.net.exe")];

        d.Handle(Start(6, "Battle.net.exe"));

        Assert.DoesNotContain(_log.Lines, l => l.Contains("stage stream", StringComparison.Ordinal));
    }

    [Fact]
    public void VolatileSkippedBytesAreReportedOnTheLaterLine()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 7L << 20);

        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("stage stream sp26-cod.exe", StringComparison.Ordinal) && l.EndsWith("; skipped volatile 7 MiB; source trace stage", StringComparison.Ordinal));
    }

    [Fact]
    public void LaterMemoryGateStopsTheStreamWithTheReason()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0);
        _available = 4096; // 가용 − 예비량 = 0

        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("stage stream sp26-cod.exe", StringComparison.Ordinal) && l.Contains("stopped, memory reserve", StringComparison.Ordinal));
    }

    [Fact]
    public void BackingOffSessionStreamsNothingForTheFirstOrALaterExeAndDoesNotLookAtProcesses()
    {
        RunningFirst();
        _streamer.FirstOps = [A];
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0);
        _backingOff = true;
        GameDispatcher d = Dispatcher();

        Assert.False(d.Handle(Start(5, "cod.exe")));
        _sec = 100;
        _processes = [(5, "cod.exe"), (6, "sp26-cod.exe")];
        _counters.Add((6, "sp26-cod.exe", 500L << 20));
        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Equal(2, _log.Lines.Count(l => l.Contains("cod: start stream skipped (quick exit guard: backing off)", StringComparison.Ordinal)));
        Assert.Empty(_streamer.Phases); // 스트림·증거 대기 없음
        Assert.Equal(0, _lookups); // 프로세스·읽기 카운터 조회도 없음
        Assert.DoesNotContain(_log.Lines, l => l.Contains("stage stream", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanningTimeCountsTowardsTheCapAndTheLoggedTimeLikeInTheOriginalOrder()
    {
        RunningFirst();
        _streamer.StartOpsList = [A];
        _streamer.OnStartOps = _ => _sec += 130; // 계획이 상한 (120 초) 을 넘김
        Assert.False(Dispatcher().Handle(Start(5, "cod.exe")));
        Assert.Contains(_log.Lines, l => l.Contains("start stream stopped at cap after ", StringComparison.Ordinal));
        Assert.Empty(_streamer.BatchCount);
        Assert.True(_streamer.StartOpsToken.CanBeCanceled); // 상한 취소에 연결된 토큰으로 계획을 만듦

        var other = new StageStreamDispatchTests();
        other.RunningFirst();
        other._streamer.StartOpsList = [A];
        other._streamer.OnStartOps = _ => other._sec += 5;
        other._streamer.OnBatch = (_, _) => other._sec += 2;
        Assert.True(other.Dispatcher().Handle(other.Start(5, "cod.exe")));
        Assert.Contains(other._log.Lines, l => l.Contains(", 11.000 s (", StringComparison.Ordinal)); // 계획 5 초 + 묶음 6 초
    }

    [Fact]
    public async Task DisabledStreamReportsTheCapWhenTheSampleInFlightWhileTheSamplerStopsSetsIt()
    {
        RunningFirst();
        _stage = false;
        _streamer.FinalCheck = false;
        var gate = new ManualResetEventSlim();
        _samplerGate = gate;
        Thread handle = Thread.CurrentThread;
        int hookDone = 0;
        Task<(bool Entered, bool Blocked)>? helper = null;
        _streamer.OnBatch = (phase, index) =>
        {
            if (index == 0)
            {
                // 표본 스레드가 시계 읽기에서 멈추면, 스트림이 돌아간 뒤 Handle 스레드가 표본 합류 (또는 잠금) 에서 막힌 것을 보고 풀어 줌.
                // 한계: 제품 코드가 "합류 도중" 을 알리는 지점이 없어 Handle 스레드의 대기 상태로 식별함. 게이트는 어떤 경우에도 finally 에서 풀어 시험이 멈추지 않게 하고, 시간 초과는 아래에서 실패로 단언함
                helper = Task.Run(() =>
                {
                    bool entered = false;
                    bool blocked = false;
                    try
                    {
                        entered = _samplerEntered.Wait(TimeSpan.FromSeconds(30));
                        blocked = SpinWait.SpinUntil(() => Volatile.Read(ref hookDone) != 0 && (handle.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(30));
                    }
                    finally
                    {
                        _sec = 500; // 풀린 표본이 읽는 시각은 상한을 넘음
                        gate.Set();
                    }

                    return (entered, blocked);
                });
            }

            if (index == 2)
            {
                Assert.True(_samplerEntered.Wait(TimeSpan.FromSeconds(30))); // 실제 표본 스레드가 시계를 읽다 멈출 때까지
                Volatile.Write(ref hookDone, 1);
            }
        };

        bool streamed = Dispatcher().Handle(Start(5, "cod.exe"));

        Assert.NotNull(helper);
        Assert.Equal((true, true), await helper.WaitAsync(TimeSpan.FromSeconds(60))); // 표본 진입과 합류 대기를 모두 실제로 봄 (시간 초과는 실패)
        Assert.False(streamed);
        Assert.Contains(_log.Lines, l => l.Contains("start stream stopped at cap after ", StringComparison.Ordinal)); // 표본 합류 뒤에 플래그를 읽음
        Assert.DoesNotContain(_log.Lines, l => l.Contains("start stream cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutAStagePlanOrWhenDisabledNoNamedCounterLookupHappens()
    {
        RunningFirst();
        _streamer.OnBatch = (_, _) => _sec += 2;
        Assert.True(Dispatcher().Handle(Start(5, "cod.exe"))); // 켠 상태, 단계 정보 없음
        Assert.Equal(0, _namedLookups);

        _streamer.FirstOps = [A]; // 켠 상태, 단계 정보 있음 (첫 스트림은 이름 있는 조회를 하지 않음)
        _sec = 400;
        Assert.True(Dispatcher().Handle(Start(5, "cod.exe")));
        Assert.Equal(0, _namedLookups);

        _stage = false;
        _sec = 700;
        _processes = [(7, "cod.exe")];
        _counters.Clear();
        _counters.Add((7, "cod.exe", 0));
        _streamer.FirstOps = [A];
        Assert.True(Dispatcher().Handle(Start(7, "cod.exe"))); // 끈 상태
        Assert.Equal(0, _namedLookups);
    }

    [Fact]
    public void PlanningRunsUnderTheLiveSamplerAndTheCapCancelsTheTokenGivenToThePlanner()
    {
        RunningFirst();
        _streamer.StartOpsList = [A];
        _streamer.OnStartOps = token =>
        {
            _sec += 130; // 실제 표본 스레드가 계획 도중 이 시각을 보고 상한 취소를 거는지 신호로 기다림
            Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30)));
        };

        Assert.False(Dispatcher().Handle(Start(5, "cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("start stream stopped at cap after ", StringComparison.Ordinal));
        Assert.Empty(_streamer.BatchCount);
    }

    [Fact]
    public void EvidenceThatArrivesAfterTheWindowExpiredWhileSleepingIsNotAccepted()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _streamer.LaterPlans = _ => new StageLaterPlan([A], false, 0);
        _sec = 109.5; // 시작 (100) 뒤 9.5 초에 대기 진입
        _counters[1] = (6, "sp26-cod.exe", 500L << 20);
        _onSleep = () => _counters[1] = (6, "sp26-cod.exe", _counters[1].Bytes + (80L << 20)); // 잠든 사이 80 MiB, 깨어나면 10.5 초

        Assert.False(d.Handle(new ProcessStart(6, "sp26-cod.exe", 100)));

        Assert.Contains(_log.Lines, l => l.Contains("stage stream skipped (sp26-cod.exe: no load evidence", StringComparison.Ordinal));
        Assert.Empty(_streamer.Phases);
    }

    [Fact]
    public void BigOpIsCutSoThePriorityFollowsTheGameBatchByBatch()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        ReadOp big = new(@"D:\Games\cod\big.bin", 0, 384L << 20);
        _streamer.LaterPlans = _ => new StageLaterPlan([big], true, 0);
        _streamer.OnBatch = (_, index) =>
        {
            _sec += 6;
            if (index == 2)
            {
                _counters[0] = (5, "cod.exe", 400L << 20); // 먼저 떠 있던 게임 프로세스가 읽기 시작 (경계는 새 pid 만 보지만 양보 신호는 게임 전체)
            }
        };

        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Equal(6, _streamer.BatchCount[big.Path]); // 384 MiB 한 op 가 여섯 묶음으로 나뉨
        Assert.Equal(6, _streamer.CanIssueCalls); // 묶음마다 판정
        Assert.Equal([true, false, true, false], _io.Select(c => c.Low)); // 낮음 → 조용해서 보통 → 다시 바빠져 낮음 → 끝에 보통
    }

    [Fact]
    public void MemoryGateIsCheckedPerPieceAndSeesTheLargestBatchThatCanComeNext()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        ReadOp big = new(@"D:\Games\cod\big.bin", 0, 384L << 20);
        _streamer.LaterPlans = _ => new StageLaterPlan([big], true, 0);
        _streamer.OnBatch = (_, index) =>
        {
            _sec += 2;
            if (index == 1)
            {
                _available = 4096 + 80; // 64 MiB 보다는 크지만 다음 묶음이 될 수 있는 96 MiB 보다 작음
            }
        };

        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Equal(2, _streamer.BatchCount[big.Path]); // 중간 묶음 뒤 다음 조각에서 멈춤
        Assert.Contains(_log.Lines, l => l.Contains("stage stream sp26-cod.exe", StringComparison.Ordinal) && l.Contains("stopped, memory reserve", StringComparison.Ordinal));
    }

    [Fact]
    public void FirstStreamWithTheGateReadsTheCutStartListAndStopsMidwayOnMemory()
    {
        RunningFirst();
        ReadOp big = new(@"D:\Games\cod\big.bin", 0, 384L << 20);
        _streamer.StartOpsList = [big];
        _streamer.OnBatch = (_, index) =>
        {
            _sec += 2;
            if (index == 2)
            {
                _available = 4096 + 10;
            }
        };

        Assert.False(Dispatcher().Handle(Start(5, "cod.exe")));

        Assert.Equal(3, _streamer.BatchCount[big.Path]);
        Assert.Contains(_log.Lines, l => l.Contains("start stream stopped at memory reserve after ", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledFirstStreamIgnoresTheCutStartListAndTheGate()
    {
        RunningFirst();
        _stage = false;
        _streamer.StartOpsList = [new ReadOp(@"D:\Games\cod\big.bin", 0, 384L << 20)];
        _available = 10;

        Assert.True(Dispatcher().Handle(Start(5, "cod.exe")));

        Assert.Equal(["all"], _streamer.Phases); // 꺼져 있으면 시작 목록을 묻지도 않고 지금 경로 그대로
    }

    [Fact]
    public void LaterLoadBoundaryIsReachedFromTheNewProcessReadsAlone()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        ReadOp big = new(@"D:\Games\cod\big.bin", 0, 384L << 20);
        _streamer.LaterPlans = _ => new StageLaterPlan([big], true, 0);
        _streamer.OnBatch = (_, index) =>
        {
            _sec += 15;
            _counters[1] = (6, "sp26-cod.exe", (index + 1) * (100L << 20)); // 새 프로세스만 계속 읽음
        };

        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("stage stream sp26-cod.exe", StringComparison.Ordinal) && l.Contains("stopped, load boundary", StringComparison.Ordinal)); // 60 초 한도의 경계
    }

    [Fact]
    public void LaterStreamStopsAtTheCap()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0);
        _streamer.OnBatch = (_, _) => _sec += 70; // 첫 묶음에서 상한 (120 초) 을 넘음

        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));
        Assert.Contains(_log.Lines, l => l.Contains("stage stream sp26-cod.exe", StringComparison.Ordinal) && l.Contains("stopped, cap", StringComparison.Ordinal));
    }

    [Fact]
    public void ServiceStopEndsTheEvidenceWaitAtOnce()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _streamer.LaterPlans = _ => new StageLaterPlan([A], false, 0);
        using var stop = new CancellationTokenSource();
        _onSleep = stop.Cancel;

        Assert.Throws<OperationCanceledException>(() => d.Handle(Start(6, "sp26-cod.exe"), stop.Token));
        Assert.True(_sec < 105); // 10 초를 다 기다리지 않음
    }

    [Fact]
    public void SameStartGroupDoesNotBecomeALaterExeWhenTheFirstStreamRunsLong()
    {
        RunningFirst();
        _streamer.OnBatch = (_, _) => _sec += 15; // 첫 스트림이 45 초
        GameDispatcher d = Dispatcher();
        Assert.True(d.Handle(Start(5, "cod.exe")));
        _streamer.Phases.Clear();
        _processes = [(5, "cod.exe"), (6, "sp26-cod.exe")];
        _counters.Add((6, "sp26-cod.exe", 900L << 20));

        Assert.False(d.Handle(new ProcessStart(6, "sp26-cod.exe", 2))); // 첫 exe 와 2 초 차이로 뜬 같은 시작 무리

        Assert.Empty(_streamer.Phases);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("stage stream", StringComparison.Ordinal) || l.Contains("already running", StringComparison.Ordinal)); // 60 초 쿨다운이 조용히 건너뜀
    }

    [Fact]
    public void ALaterExeWhoseStartIsOlderThanTheEvidenceWindowIsNotStreamedEvenWithBigCounters()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _streamer.LaterPlans = _ => new StageLaterPlan([A], false, 0);
        _counters[1] = (6, "sp26-cod.exe", 900L << 20);

        Assert.False(d.Handle(new ProcessStart(6, "sp26-cod.exe", 80))); // 처리 시점에 20 초 지난 시작

        Assert.Contains(_log.Lines, l => l.Contains("stage stream skipped (sp26-cod.exe: no load evidence", StringComparison.Ordinal));
        Assert.Empty(_streamer.Phases);
    }

    [Fact]
    public void RestartOfTheOnlyProcessWithTheStageSettingOnTakesTheOldPath()
    {
        RunningFirst();
        GameDispatcher d = Dispatcher();
        Assert.True(d.Handle(Start(5, "cod.exe")));
        _sec = 700; // 쿨다운 밖, 이전 프로세스는 끝남
        _processes = [(7, "cod.exe")];
        _counters.Clear();
        _counters.Add((7, "cod.exe", 0));

        Assert.True(d.Handle(Start(7, "cod.exe")));

        Assert.Equal(["all", "all"], _streamer.Phases);
        Assert.Equal(2, _log.Lines.Count(l => l.Contains(": streamed ", StringComparison.Ordinal)));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("stage stream", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledStreamKeepsTheCancelledWordingWhenOnlyTheFinalCheckHitsTheCap()
    {
        RunningFirst();
        _stage = false;
        _streamer.FinalCheck = false; // 스트리머가 끝 확인을 하지 않아, 디스패처의 마지막 확인에서 처음 상한을 만남
        _streamer.OnBatch = (_, index) =>
        {
            if (index == 2)
            {
                _sec += 200;
            }
        };

        Assert.False(Dispatcher().Handle(Start(5, "cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("start stream cancelled after ", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("stopped at cap", StringComparison.Ordinal));
    }

    private static readonly ReadOp Big = new(@"D:\Games\codig.bin", 0, 384L << 20);

    private void WithRefill(long mib = 384)
    {
        _streamer.Refill = new LoadRefillPlan([new ReadOp(Big.Path, 0, mib << 20)], 2, 3);
        _counters[1] = (6, "sp26-cod.exe", 3L << 20); // 로드 첫 10 초에 3 MiB 읽은 실제 사례: 짧은 확인을 통과해야 함
    }

    [Fact]
    public void RefillStartThatReadsAlmostNothingIsSkippedAfterTheShortCheck()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        WithRefill();
        _counters[1] = (6, "sp26-cod.exe", 200L << 10); // 보조 시작: 10 초 동안 200 KiB

        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("stage stream skipped (sp26-cod.exe: no load evidence, 200 KiB in 10 s)", StringComparison.Ordinal));
        Assert.Equal(110, _sec); // 디스패처를 최대 10 초만 붙잡음
        Assert.Empty(_streamer.Phases);
    }

    [Fact]
    public void RefillStartWhoseProcessIsGoneIsSkippedAtOnce()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        WithRefill();
        _counters.RemoveAt(1);

        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("stage stream skipped (sp26-cod.exe: no load evidence, the process is gone)", StringComparison.Ordinal));
        Assert.Equal(100, _sec);
    }

    [Fact]
    public void RefillStartPassesAsSoonAsTheReadsReachTheEvidenceWithinTheWindow()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        WithRefill(64);
        _counters[1] = (6, "sp26-cod.exe", 0);
        int sleeps = 0;
        _onSleep = () =>
        {
            if (++sleeps == 3)
            {
                _counters[1] = (6, "sp26-cod.exe", 2L << 20);
            }
        };

        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Equal([Big.Path], _streamer.Phases);
        Assert.Equal(103, _sec);
    }

    [Fact]
    public void RefillCheckEndsAtOnceWhenAnotherGamesStartIsQueued()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        WithRefill();
        _counters[1] = (6, "sp26-cod.exe", 0);
        _queuedOther = true;

        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("stage stream skipped (sp26-cod.exe: another game started)", StringComparison.Ordinal));
        Assert.Equal(100, _sec);
    }

    [Fact]
    public void RefillStreamStopsWhenAnotherGamesStartIsQueuedSoTheDispatcherIsNotHeld()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        WithRefill();
        _streamer.OnBatch = (_, _) =>
        {
            _sec += 5;
            _queuedOther = true; // 첫 묶음 뒤에 다른 게임의 시작이 큐에 들어옴
        };

        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("stage stream sp26-cod.exe", StringComparison.Ordinal) && l.Contains("stopped, another game started", StringComparison.Ordinal));
        Assert.Equal(1, _streamer.BatchCount[Big.Path]);
    }

    [Fact]
    public void RefillListIsStreamedPastTheFirstQuietAndStopsAtTheEndOfTheList()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        WithRefill();
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0); // 목록이 있으면 쓰지 않음
        _streamer.OnBatch = (_, _) =>
        {
            _sec += 15;
            _counters[1] = (6, "sp26-cod.exe", 300L << 20); // 첫 묶음 뒤로는 읽기가 멈춤 (옛 경계 판정이면 여기서 멈춤)
        };

        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Equal([Big.Path], _streamer.Phases);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("load boundary", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("stage stream sp26-cod.exe 384 MiB in ", StringComparison.Ordinal) && l.Contains("completed), yielded", StringComparison.Ordinal)
            && l.EndsWith("; source load history (2 plays, 3 loads, 384 MiB planned)", StringComparison.Ordinal));
    }

    [Fact]
    public void RefillListStopsAtTheTimeCapAndKeepsLowPriority()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        WithRefill();
        _streamer.OnBatch = (_, _) => _sec += 70;

        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("stage stream sp26-cod.exe", StringComparison.Ordinal) && l.Contains("stopped, cap", StringComparison.Ordinal));
        Assert.True(_io[0].Low); // 낮은 우선순위로 시작
    }

    [Fact]
    public void RefillListStartsWithoutWaitingForLoadEvidence()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        WithRefill(64);
        _streamer.LaterPlans = _ => new StageLaterPlan([A, B], false, 0); // 목록이 없었다면 읽기 증거를 기다림
        _counters[1] = (6, "sp26-cod.exe", 3L << 20); // 로드 첫 10 초 읽기가 3 MiB 뿐

        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Equal([Big.Path], _streamer.Phases);
        Assert.Equal(100, _sec); // 기다리지 않음
        Assert.DoesNotContain(_log.Lines, l => l.Contains("no load evidence", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutAListTheStageStreamIsAsBeforeAndNamesItsSource()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0);

        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Equal([B.Path], _streamer.Phases);
        Assert.Contains(_log.Lines, l => l.Contains("stage stream sp26-cod.exe", StringComparison.Ordinal) && l.EndsWith("; source trace stage", StringComparison.Ordinal));
        Assert.Equal(1, _streamer.RefillCalls);
    }

    [Fact]
    public void RefillCapIsTheSmallerOfTheFixedCapAndWhatTheMemoryGateAllows()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _available = 4096 + 1000; // 예비량 4,096 MiB 를 빼면 1,000 MiB
        _ = d.Handle(Start(6, "sp26-cod.exe"));
        Assert.Equal(1000L << 20, _streamer.LastRefillCap);

        _available = 40_000;
        _sec = 300;
        _ = d.Handle(Start(7, "sp26-cod.exe"));
        Assert.Equal(LoadRefillPlanner.CapBytes, _streamer.LastRefillCap);
    }

    [Fact]
    public void AStartHandledLongAfterItsWindowIgnoresTheListAndKeepsTheOldPath()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        WithRefill();
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0);
        _sec = 300;

        Assert.True(d.Handle(new ProcessStart(6, "sp26-cod.exe", 100))); // 시작이 200 초 전

        Assert.Equal([B.Path], _streamer.Phases);
        Assert.Contains(_log.Lines, l => l.EndsWith("; source trace stage", StringComparison.Ordinal));
    }

    [Fact]
    public void ACooldownRunsFromTheEndOfARefillSoAnOverlappingStartOfTheSameLoadDoesNotStreamTwice()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        WithRefill(); // 384 MiB: 묶음 여섯 개
        _streamer.OnBatch = (_, _) => _sec += 15; // 스트림이 약 90 초 (시작 100 → 끝 190)
        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));
        _streamer.Phases.Clear();

        _sec = 235; // 시작에서 135 초 (옛 기준이면 대기 끝), 끝에서 45 초
        _processes = [(5, "cod.exe"), (7, "sp26-cod.exe")];
        _counters.Add((7, "sp26-cod.exe", 0));
        Assert.False(d.Handle(Start(7, "sp26-cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("stage stream skipped (sp26-cod.exe: cooldown ", StringComparison.Ordinal));
        Assert.Empty(_streamer.Phases);
    }

    private const long Mib = 1L << 20;

    private string EndLine() => _log.Lines.Single(l => l.Contains("stream reads: ", StringComparison.Ordinal));

    [Fact]
    public void EnoughHeadroomIssuesEveryBatchWithoutWaitingAndRecordsTheMinimum()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        d.LoadPhases = new LoadPhaseBoard(_log);
        _lists = new MemoryLists(10_000 * Mib, 0);
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0);
        _streamer.OnBatch = (_, _) => _sec += 2;

        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Equal(3, _streamer.BatchCount[B.Path]);
        Assert.EndsWith("; memory headroom held 0 batches 0.0 s, min 10000 MiB", EndLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void ShortHeadroomIssuesNoBatchAndTheStreamStillEndsAtTheTimeCap()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        d.LoadPhases = new LoadPhaseBoard(_log);
        _lists = new MemoryLists(64 * Mib + 255 * Mib, 0); // 묶음 + 여분 256 MiB 에 1 MiB 모자람
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0);

        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.False(_streamer.BatchCount.ContainsKey(B.Path));
        Assert.Contains(_log.Lines, l => l.Contains("stage stream sp26-cod.exe", StringComparison.Ordinal) && l.Contains("stopped, cap", StringComparison.Ordinal));
        Assert.Contains("; memory headroom held 1 batches 12", EndLine(), StringComparison.Ordinal); // 상한 120 초 동안 기다림
        Assert.EndsWith("s, min 319 MiB", EndLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void HeadroomThatReturnsLetsTheWaitingBatchGoAndTheRestFollow()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        d.LoadPhases = new LoadPhaseBoard(_log);
        _lists = new MemoryLists(100 * Mib, 0);
        int sleeps = 0;
        _onSleep = () =>
        {
            if (++sleeps == 2)
            {
                _lists = new MemoryLists(10_000 * Mib, 0);
            }
        };
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0);
        _streamer.OnBatch = (_, _) => { }; // 가짜 스트리머는 이 훅이 있어야 묶음 번호를 올림

        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Equal(3, _streamer.BatchCount[B.Path]);
        Assert.EndsWith("; memory headroom held 1 batches 2.0 s, min 100 MiB", EndLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedLookupHoldsEveryBatchAndLogsOnlyOncePerStream()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        d.LoadPhases = new LoadPhaseBoard(_log);
        _lists = null;
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0);

        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.False(_streamer.BatchCount.ContainsKey(B.Path));
        Assert.Single(_log.Lines, l => l.Contains("stage stream memory lists unavailable", StringComparison.Ordinal));
        Assert.EndsWith("s, min none", EndLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheStartStreamHoldsOnShortHeadroomToo()
    {
        RunningFirst();
        _streamer.FirstOps = [A];
        _lists = new MemoryLists(100 * Mib, 0);
        GameDispatcher d = Dispatcher();
        d.LoadPhases = new LoadPhaseBoard(_log);

        Assert.False(d.Handle(Start(5, "cod.exe")));

        Assert.Empty(_streamer.BatchCount);
        Assert.Contains(_log.Lines, l => l.Contains("start stream stopped at cap after ", StringComparison.Ordinal));
        Assert.Contains("; memory headroom held 1 batches ", EndLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledSettingNeverLooksAtTheMemoryLists()
    {
        RunningFirst();
        _stage = false;
        _lists = null; // 조회가 되면 막혔을 값
        GameDispatcher d = Dispatcher();
        d.LoadPhases = new LoadPhaseBoard(_log);

        Assert.True(d.Handle(Start(5, "cod.exe")));

        Assert.Equal(["all"], _streamer.Phases);
        Assert.EndsWith("; memory headroom held 0 batches 0.0 s, min none", EndLine(), StringComparison.Ordinal);
    }

    [Fact]
    public void BatchesIssuedBackToBackShareOneLookupSoEachOneIsSubtractedFromIt()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        d.LoadPhases = new LoadPhaseBoard(_log);
        ReadOp big = new(@"D:\Games\codig.bin", 0, 384L << 20);
        _lists = new MemoryLists(384 * Mib, 0); // 묶음 64 MiB + 여분 256 MiB 면 묶음마다 64 MiB 씩 줄어 셋째 묶음에서 모자람
        _streamer.LaterPlans = _ => new StageLaterPlan([big], true, 0);
        _streamer.OnBatch = (_, _) => { }; // 시계가 가지 않아 조회는 250 ms 안에서 재사용됨

        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Equal(6, _streamer.BatchCount[big.Path]);
        Assert.EndsWith("; memory headroom held 2 batches 2.0 s, min 256 MiB", EndLine(), StringComparison.Ordinal); // 조회마다 두 묶음만 허락
    }

    [Fact]
    public void TheHeadroomIsJudgedWithTheRealSizeOfTheNextBatchNotTheLargestPossible()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        ReadOp big = new(@"D:\Games\codig.bin", 0, 384L << 20);
        _lists = new MemoryLists(320 * Mib, 0); // 64 MiB 묶음 + 256 MiB 에 딱 맞음 (최대 묶음 96 MiB 로 보면 모자람)
        _streamer.LaterPlans = _ => new StageLaterPlan([big], true, 0);
        double firstBatchAt = -1;
        _streamer.OnBatch = (_, index) => firstBatchAt = index == 0 ? _sec : firstBatchAt;

        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Equal(100, firstBatchAt); // 기다리지 않고 첫 묶음을 냄
    }

    [Fact]
    public void AfterTheLastBatchTheEndCheckNeverWaitsForMemory()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        d.LoadPhases = new LoadPhaseBoard(_log);
        _lists = new MemoryLists(10_000 * Mib, 0);
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0);
        _streamer.OnBatch = (_, index) =>
        {
            _sec += 2;
            if (index == 2)
            {
                _lists = new MemoryLists(0, 0); // 마지막 묶음을 낸 뒤 여유가 사라짐
            }
        };

        Assert.True(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Equal(3, _streamer.BatchCount[B.Path]);
        Assert.EndsWith("; memory headroom held 0 batches 0.0 s, min 10000 MiB", EndLine(), StringComparison.Ordinal);
        Assert.Equal(106, _sec); // 종료 확인에서 기다리지 않음
    }

    [Fact]
    public void TheLookupPeriodIsSharedByConsecutiveStreams()
    {
        RunningFirst();
        _lists = new MemoryLists(10_000 * Mib, 0);
        _streamer.FirstOps = [A];
        _streamer.OnBatch = (_, _) => { }; // 시계가 가지 않아 두 스트림이 250 ms 안에 이어짐
        int lookups = 0;
        GameDispatcher d = new([Game, Games.Make("other", "other.exe")], _streamer, _log, () => DateTimeOffset.UnixEpoch.AddSeconds(_sec), new DispatchEnv
        {
            ReadCounters = _ => [.. _counters.Select(c => (c.Pid, c.Bytes))],
            Timestamp = () => (long)(_sec * Stopwatch.Frequency),
            Lists = () =>
            {
                lookups++;
                return _lists;
            },
        });

        Assert.True(d.Handle(Start(5, "cod.exe")));
        Assert.True(d.Handle(Start(7, "other.exe")));

        Assert.Equal(2, _streamer.Phases.Count); // 스트림이 둘 돌았음
        Assert.Equal(1, lookups);
    }

    private sealed class StageFakeStreamer : IGameStreamer
    {
        public LoadRefillPlan? Refill { get; set; }
        public long LastRefillCap { get; private set; }
        public int RefillCalls { get; private set; }

        public LoadRefillPlan? PlanLoadRefill(GameSettings game, long capBytes, CancellationToken stop)
        {
            RefillCalls++;
            LastRefillCap = capBytes;
            return Refill;
        }

        public IReadOnlyList<ReadOp>? FirstOps { get; set; }
        public Func<string, StageLaterPlan> LaterPlans { get; set; } = _ => new StageLaterPlan([], false, 0);
        public Action<string, int>? OnBatch { get; set; }
        public List<string> Phases { get; } = [];
        public Dictionary<string, int> BatchCount { get; } = [];
        public IReadOnlyList<ReadOp>? StartOpsList { get; set; }
        public bool FinalCheck { get; set; } = true;
        public Action<CancellationToken>? OnStartOps { get; set; }
        public CancellationToken StartOpsToken { get; private set; }
        public int CanIssueCalls { get; private set; }

        public IReadOnlyList<ReadOp>? StartOps(GameSettings game, CancellationToken stop)
        {
            StartOpsToken = stop;
            OnStartOps?.Invoke(stop);
            return StartOpsList;
        }

        public StreamStats Stream(GameSettings game) => new(0, 0, 0, 0);

        public IReadOnlyList<ReadOp>? PlanFirstStage(GameSettings game, CancellationToken stop) => FirstOps;

        public StageLaterPlan PlanLaterStage(GameSettings game, string exe, CancellationToken stop) => LaterPlans(exe);

        public (StreamStats Stats, bool Complete) StreamCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue) =>
            Run("all", 192L << 20, progress, canIssue, null);

        public (StreamStats Stats, bool Complete) StreamCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue, Func<long, bool>? admit) =>
            Run("all", 192L << 20, progress, canIssue, admit);

        public (StreamStats Stats, bool Complete) StreamOpsCoordinated(GameSettings game, IReadOnlyList<ReadOp> ops, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue) =>
            StreamOpsCoordinated(game, ops, stop, progress, canIssue, noteIssue, null);

        // FolderWarm.Read 로 실제 묶음 규칙 (64 MiB 이상이 모일 때까지 op 단위) 을 그대로 따름. admit 는 실제 스트리머처럼 묶음 앞에서 그 크기로 부르고 (canIssue 를 대신함), 종료 확인은 canIssue 만 씀.
        public (StreamStats Stats, bool Complete) StreamOpsCoordinated(GameSettings game, IReadOnlyList<ReadOp> ops, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue, Func<long, bool>? admit)
        {
            string phase = ops[0].Path;
            Phases.Add(phase);
            long total = ops.Sum(o => o.Length);
            long done = 0;
            int index = 0;
            (StreamStats Stats, bool Complete) r = FolderWarm.Read(ops, stop, batch =>
            {
                CanIssueCalls++;
                long bytes = batch.Sum(o => o.Length);
                if (!(admit?.Invoke(bytes) ?? canIssue()))
                {
                    return new StreamStats(0, 0, 0, 0, Aborted: true, RequestedBytes: bytes);
                }

                BatchCount[phase] = index + 1;
                OnBatch?.Invoke(phase, index++);
                done += bytes;
                progress?.Invoke(done, total);
                return new StreamStats(bytes, 1, 0, 0.01);
            });
            return !canIssue() ? (r.Stats with { Aborted = true }, false) : r;
        }

        private (StreamStats Stats, bool Complete) Run(string phase, long total, Action<long, long>? progress, Func<bool> canIssue, Func<long, bool>? admit)
        {
            Phases.Add(phase);
            const int batches = 3;
            long done = 0;
            for (int i = 0; i < batches; i++)
            {
                if (!(admit?.Invoke(total / batches) ?? canIssue()))
                {
                    return (new StreamStats(done, 1, 0, 0.01, Aborted: true), false);
                }

                BatchCount[phase] = i + 1;
                OnBatch?.Invoke(phase, i);
                done += total / batches;
                progress?.Invoke(done, total);
            }

            return !FinalCheck || canIssue() ? (new StreamStats(done, 1, 0, 0.01), true) : (new StreamStats(done, 1, 0, 0.01, Aborted: true), false);
        }
    }
}
