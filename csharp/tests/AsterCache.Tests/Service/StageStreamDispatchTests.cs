using System.Diagnostics;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 단계 스트림 디스패처: 가짜 시계·가짜 읽기 카운터·가짜 스트리머로 첫 실행 파일 (미리 흘리기)·이후 실행 파일·끄기를 시험합니다.
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
    private Action? _onSleep;
    private bool _stage = true;
    private IReadOnlyList<(int Pid, string Exe)> _processes = [];

    private GameDispatcher Dispatcher() =>
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
                return [.. _counters];
            },
            Timestamp = () => (long)(_sec * Stopwatch.Frequency),
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
        Assert.Contains(_log.Lines, l => l.Contains("streamed ", StringComparison.Ordinal) && !l.Contains("ahead stage", StringComparison.Ordinal));
        Assert.Empty(_io);
    }

    [Fact]
    public void FirstExeStreamsItsStageThenTheNextStageAtLowPriorityOnTheReadingThread()
    {
        RunningFirst();
        _streamer.FirstPlan = new StageFirstPlan([A], "sp26-cod.exe", [B], 5L << 20);
        _streamer.OnBatch = (_, _) => _sec += 2;

        Assert.True(Dispatcher().Handle(Start(5, "cod.exe")));

        Assert.Equal([A.Path, B.Path], _streamer.Phases);
        Assert.Contains(_log.Lines, l => l.Contains("; ahead stage sp26-cod.exe 192 MiB", StringComparison.Ordinal) && l.Contains("completed), yielded", StringComparison.Ordinal) && l.Contains("; skipped volatile 5 MiB", StringComparison.Ordinal));
        Assert.True(_io[0].Low); // 미리 흘리기는 낮음으로 시작
        Assert.False(_io[^1].Low); // 끝나면 보통으로 되돌림
        Assert.Single(_io.Select(c => c.Thread).Distinct());
        Assert.Equal(Environment.CurrentManagedThreadId, _io[0].Thread);
    }

    [Fact]
    public void AheadStopsWhenTheNextStageProcessAppears()
    {
        RunningFirst();
        _streamer.FirstPlan = new StageFirstPlan([A], "sp26-cod.exe", [B], 0);
        _streamer.OnBatch = (phase, index) =>
        {
            _sec += 2;
            if (phase == B.Path && index == 0)
            {
                _counters.Add((9, "sp26-cod.exe", 0)); // 다음 단계 프로세스가 뜸
            }
        };

        Assert.True(Dispatcher().Handle(Start(5, "cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("ahead stage sp26-cod.exe", StringComparison.Ordinal) && l.Contains("stopped, next stage started", StringComparison.Ordinal));
        Assert.True(_streamer.BatchCount[B.Path] < 3); // 나머지 묶음은 읽지 않음
    }

    [Fact]
    public void AheadAndEvidenceWaitEndWhenAnotherGamesStartIsQueued()
    {
        RunningFirst();
        _streamer.FirstPlan = new StageFirstPlan([A], "sp26-cod.exe", [B], 0);
        _streamer.OnBatch = (phase, index) =>
        {
            _sec += 2;
            if (phase == B.Path && index == 0)
            {
                _queuedOther = true;
            }
        };

        Assert.True(Dispatcher().Handle(Start(5, "cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("ahead stage sp26-cod.exe", StringComparison.Ordinal) && l.Contains("stopped, another game started", StringComparison.Ordinal));
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
        _streamer.FirstPlan = new StageFirstPlan([A], "sp26-cod.exe", [B], 0); // 꺼져 있으면 계획을 묻지도 않음

        Assert.True(Dispatcher().Handle(Start(5, "cod.exe")));

        Assert.Equal(["all"], _streamer.Phases);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("ahead stage", StringComparison.Ordinal) || l.Contains("memory reserve", StringComparison.Ordinal));
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

        Assert.Contains(_log.Lines, l => l.Contains("stage stream sp26-cod.exe", StringComparison.Ordinal) && l.EndsWith("; skipped volatile 7 MiB", StringComparison.Ordinal));
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
        _streamer.FirstPlan = new StageFirstPlan([A], "sp26-cod.exe", [B], 0);
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0);
        _backingOff = true;
        GameDispatcher d = Dispatcher();

        Assert.False(d.Handle(Start(5, "cod.exe")));
        _sec = 100;
        _processes = [(5, "cod.exe"), (6, "sp26-cod.exe")];
        _counters.Add((6, "sp26-cod.exe", 500L << 20));
        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));

        Assert.Equal(2, _log.Lines.Count(l => l.Contains("cod: start stream skipped (quick exit guard: backing off)", StringComparison.Ordinal)));
        Assert.Empty(_streamer.Phases); // 스트림·미리 흘리기·증거 대기 없음
        Assert.Equal(0, _lookups); // 프로세스·읽기 카운터 조회도 없음
        Assert.DoesNotContain(_log.Lines, l => l.Contains("stage stream", StringComparison.Ordinal) || l.Contains("ahead stage", StringComparison.Ordinal));
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
    public void LaterStreamAndAheadStopAtTheCap()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _streamer.LaterPlans = _ => new StageLaterPlan([B], true, 0);
        _streamer.OnBatch = (_, _) => _sec += 70; // 첫 묶음에서 상한 (120 초) 을 넘음

        Assert.False(d.Handle(Start(6, "sp26-cod.exe")));
        Assert.Contains(_log.Lines, l => l.Contains("stage stream sp26-cod.exe", StringComparison.Ordinal) && l.Contains("stopped, cap", StringComparison.Ordinal));

        var other = new StageStreamDispatchTests();
        other.RunningFirst();
        other._streamer.FirstPlan = new StageFirstPlan([A], "sp26-cod.exe", [B], 0);
        other._streamer.OnBatch = (phase, _) => other._sec += phase == B.Path ? 70 : 1;
        Assert.True(other.Dispatcher().Handle(other.Start(5, "cod.exe")));
        Assert.Contains(other._log.Lines, l => l.Contains("ahead stage sp26-cod.exe", StringComparison.Ordinal) && l.Contains("stopped, cap", StringComparison.Ordinal));
    }

    [Fact]
    public void MemoryGateStopsTheAheadStreamToo()
    {
        RunningFirst();
        _streamer.FirstPlan = new StageFirstPlan([A], "sp26-cod.exe", [B], 0);
        _streamer.OnBatch = (phase, _) =>
        {
            _sec += 2;
            if (phase == B.Path)
            {
                _available = 4096 + 10;
            }
        };

        Assert.True(Dispatcher().Handle(Start(5, "cod.exe")));

        Assert.Contains(_log.Lines, l => l.Contains("ahead stage sp26-cod.exe", StringComparison.Ordinal) && l.Contains("stopped, memory reserve", StringComparison.Ordinal));
    }

    [Fact]
    public void ServiceStopEndsTheEvidenceWaitAndTheAheadAtOnce()
    {
        GameDispatcher d = DispatcherAfterFirstExe();
        _streamer.LaterPlans = _ => new StageLaterPlan([A], false, 0);
        using var stop = new CancellationTokenSource();
        _onSleep = stop.Cancel;

        Assert.Throws<OperationCanceledException>(() => d.Handle(Start(6, "sp26-cod.exe"), stop.Token));
        Assert.True(_sec < 105); // 10 초를 다 기다리지 않음

        var other = new StageStreamDispatchTests();
        other.RunningFirst();
        other._streamer.FirstPlan = new StageFirstPlan([A], "sp26-cod.exe", [B], 0);
        using var stop2 = new CancellationTokenSource();
        other._streamer.OnBatch = (phase, _) =>
        {
            other._sec += 1;
            if (phase == B.Path)
            {
                stop2.Cancel();
            }
        };
        Assert.True(other.Dispatcher().Handle(other.Start(5, "cod.exe"), stop2.Token));
        Assert.Contains(other._log.Lines, l => l.Contains("ahead stage sp26-cod.exe", StringComparison.Ordinal) && l.Contains("stopped, cancelled", StringComparison.Ordinal));
        Assert.Equal(1, other._streamer.BatchCount[B.Path]);
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

    private sealed class StageFakeStreamer : IGameStreamer
    {
        public StageFirstPlan? FirstPlan { get; set; }
        public Func<string, StageLaterPlan> LaterPlans { get; set; } = _ => new StageLaterPlan([], false, 0);
        public Action<string, int>? OnBatch { get; set; }
        public List<string> Phases { get; } = [];
        public Dictionary<string, int> BatchCount { get; } = [];
        public IReadOnlyList<ReadOp>? StartOpsList { get; set; }
        public bool FinalCheck { get; set; } = true;
        public int CanIssueCalls { get; private set; }

        public IReadOnlyList<ReadOp>? StartOps(GameSettings game, CancellationToken stop) => StartOpsList;

        public StreamStats Stream(GameSettings game) => new(0, 0, 0, 0);

        public StageFirstPlan? PlanFirstStage(GameSettings game, CancellationToken stop) => FirstPlan;

        public StageLaterPlan PlanLaterStage(GameSettings game, string exe, CancellationToken stop) => LaterPlans(exe);

        public (StreamStats Stats, bool Complete) StreamCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue) =>
            Run("all", 192L << 20, progress, canIssue);

        // FolderWarm.Read 로 실제 묶음 규칙 (64 MiB 이상이 모일 때까지 op 단위) 을 그대로 따름
        public (StreamStats Stats, bool Complete) StreamOpsCoordinated(GameSettings game, IReadOnlyList<ReadOp> ops, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue)
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
                if (!canIssue())
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

        private (StreamStats Stats, bool Complete) Run(string phase, long total, Action<long, long>? progress, Func<bool> canIssue)
        {
            Phases.Add(phase);
            const int batches = 3;
            long done = 0;
            for (int i = 0; i < batches; i++)
            {
                if (!canIssue())
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
