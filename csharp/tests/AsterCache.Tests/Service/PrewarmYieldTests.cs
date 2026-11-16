using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 핫 집합 예열 스트림의 양보: 판정 (순수 함수), 시작 상태, 외부 부하가 생기면 취소 없이 낮은 I/O 우선순위로 읽고 사라지면 보통으로 돌아오며, 끝나면 (취소·예외 포함) 보통으로 되돌림.
public sealed class PrewarmYieldTests
{
    private const long Mib = 1L << 20;
    private const double Busy = 100 * Mib;
    private readonly ListLog _log = new();
    private readonly List<string> _events = []; // 호출 순서: observe (관측 멈춤), low / normal (I/O 우선순위), issue (묶음 발행)
    private readonly BatchStreamer _streamer;
    private DateTimeOffset _now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private long _uptimeMs = (long)TimeSpan.FromHours(2).TotalMilliseconds;
    private double _bps;
    private DiskActivity.SampleState _state = DiskActivity.SampleState.Ok;

    public PrewarmYieldTests() => _streamer = new BatchStreamer(this);

    // 100 MiB 묶음 (기본 여덟 개) 를 차례로 읽고 (묶음마다 4 초가 흐름) 묶음마다 진행을 알림 (StreamPrewarmCore 의 Batches 와 같은 순서: 취소 확인 → 읽기 → 진행).
    private sealed class BatchStreamer(PrewarmYieldTests t) : IGameStreamer
    {
        public Action<int>? OnIssue { get; set; }

        public int Batches { get; set; } = 8;

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            progress?.Invoke(0, Batches * 100 * Mib);
            long done = 0;
            for (int i = 0; i < Batches && !stop.IsCancellationRequested; i++)
            {
                t._events.Add("issue");
                t._now += TimeSpan.FromSeconds(4);
                done += 100 * Mib;
                OnIssue?.Invoke(i);
                progress?.Invoke(done, Batches * 100 * Mib);
            }

            return (new StreamStats(done, Batches, 0, 0.01), !stop.IsCancellationRequested);
        }
    }

    private readonly List<TimeSpan> _slept = [];
    private Func<GameSettings, Action, Action, (double? Bps, DiskActivity.SampleState State)>? _window;

    private Prewarmer Make(bool lowFails = false) => new([Games.Make("a", "a.exe")], _streamer, Env(lowFails), _log, null, () => _now);

    private PrewarmEnv Env(bool lowFails = false) => new()
    {
        AvailableRamMib = () => 16000,
        GameRunning = _ => false,
        ZoneBytes = _ => 800 * Mib,
        TraceExists = _ => true,
        UptimeMs = () => _uptimeMs,
        Sleep = (t, _) =>
        {
            _events.Add("observe");
            _slept.Add(t);
            (_now, _uptimeMs) = (_now + t, _uptimeMs + (long)t.TotalMilliseconds);
        },
        ProbeReadMs = _ => new ProbeResult(8, 8, 5.0), // 식음 (서비스 시작 분류용)
        DiskSample = _ => (_bps, _state),
        DiskWindowSample = _window,
        SetReadIoLow = low =>
        {
            _events.Add(low ? "low" : "normal");
            return !lowFails;
        },
    };

    [Theory]
    [InlineData(true, Busy, DiskActivity.SampleState.Ok, ReadLoad.Cancel)] // 다른 게임이 돌고 바쁨: 기존 규칙대로 취소
    [InlineData(false, Busy, DiskActivity.SampleState.Ok, ReadLoad.Yield)] // 게임 없이 바쁨: 취소 없이 양보
    [InlineData(false, 0.0, DiskActivity.SampleState.Ok, ReadLoad.Normal)] // 한가함
    [InlineData(true, 0.0, DiskActivity.SampleState.Ok, ReadLoad.Normal)] // 다른 게임이 있어도 한가하면 그대로
    [InlineData(false, null, DiskActivity.SampleState.Broken, ReadLoad.Yield)] // 표본을 읽을 수 없으면 양보
    [InlineData(false, null, DiskActivity.SampleState.Warming, ReadLoad.Yield)]
    [InlineData(false, null, DiskActivity.SampleState.Stale, ReadLoad.Yield)]
    [InlineData(true, null, DiskActivity.SampleState.Broken, ReadLoad.Yield)] // 고장은 바쁨으로 보지 않아 취소하지 않음 (기존 규칙)
    public void DecideCancelsOnlyForABusyDiskWithAnotherGameAndOtherwiseYieldsUnlessIdle(bool otherGame, double? bps, DiskActivity.SampleState state, ReadLoad expected) =>
        Assert.Equal(expected, PrewarmYield.Decide(otherGame, bps, state));

    [Theory]
    [InlineData("trigger steam.exe", 7_200_000L, true)] // 런처 계기는 부팅과 무관하게 양보로 시작
    [InlineData("service start", 600_000L, true)] // 가동 15 분 미만
    [InlineData("service start", 900_000L, false)]
    [InlineData("rewarm", 7_200_000L, false)]
    public void StartsYieldingForALauncherTriggerOrWithinFifteenMinutesOfBoot(string why, long uptimeMs, bool expected) =>
        Assert.Equal(expected, PrewarmYield.StartsYielding(why, uptimeMs));

    [Fact]
    public void ALoadThatAppearsMidStreamLowersThePriorityWithoutCancellingAndItsEndRaisesItAgain()
    {
        _streamer.OnIssue = i => _bps = i is >= 2 and < 5 ? Busy : 0; // 세 번째~다섯 번째 묶음 동안 다른 읽기가 있음
        Prewarmer p = Make();

        PrewarmResult r = p.Run(Games.Make("a", "a.exe"), "rewarm");

        Assert.True(r.Ran); // 취소되지 않고 끝까지 읽음
        Assert.Equal(8, _events.Count(e => e == "issue"));
        Assert.Equal(["low", "normal"], _events.Where(e => e is "low" or "normal"));
        Assert.True(_events.IndexOf("low") > _events.IndexOf("issue"), "보통으로 시작: 관측은 10 초 뒤");
        Assert.Single(_log.Lines, l => l.Contains("a: prewarm yields to other disk activity (low I/O priority)", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("prewarm done (rewarm)", StringComparison.Ordinal) && l.Contains(", yielded ", StringComparison.Ordinal));
    }

    [Fact]
    public void AQuietDiskNeverYieldsAndLogsNoYieldedTime()
    {
        Prewarmer p = Make();

        Assert.True(p.Run(Games.Make("a", "a.exe"), "rewarm").Ran);

        Assert.DoesNotContain(_events, e => e is "low" or "normal");
        Assert.Contains("observe", _events); // 10 초마다 관측은 함
        Assert.DoesNotContain(_log.Lines, l => l.Contains("yield", StringComparison.Ordinal));
    }

    [Fact]
    public void ACancelledStreamPutsThePriorityBackToNormal()
    {
        _bps = Busy;
        using var cts = new CancellationTokenSource();
        _streamer.OnIssue = i =>
        {
            if (_events.Contains("low"))
            {
                cts.Cancel();
            }
        };
        Prewarmer p = Make();

        PrewarmResult r = p.Run(Games.Make("a", "a.exe"), "rewarm", ct: cts.Token);

        Assert.False(r.Ran);
        Assert.Equal(["low", "normal"], _events.Where(e => e is "low" or "normal"));
        Assert.Equal("normal", _events.Last(e => e is "low" or "normal"));
    }

    [Fact]
    public void AStreamThatThrowsPutsThePriorityBackToNormal()
    {
        _bps = Busy;
        _streamer.OnIssue = i =>
        {
            if (_events.Contains("low"))
            {
                throw new InvalidOperationException("boom");
            }
        };
        Prewarmer p = Make();

        Assert.Throws<InvalidOperationException>(() => p.Run(Games.Make("a", "a.exe"), "rewarm"));

        Assert.Equal(["low", "normal"], _events.Where(e => e is "low" or "normal"));
    }

    [Fact]
    public void ALauncherTriggerStartsYieldingAndObservesBeforeTheFirstBatch()
    {
        _bps = Busy;
        Prewarmer p = Make();

        Assert.True(p.Run(Games.Make("a", "a.exe"), "trigger steam.exe").Ran);

        Assert.Equal(["observe", "low"], _events.Take(2)); // 첫 묶음 직전에 관측하고 바쁘면 낮음
        Assert.Equal("issue", _events[2]);
    }

    [Fact]
    public void ALauncherTriggerOnAnIdleDiskRaisesToNormalRightAwayWithoutEverLowering()
    {
        Prewarmer p = Make();

        Assert.True(p.Run(Games.Make("a", "a.exe"), "trigger steam.exe").Ran);

        Assert.Equal(["observe", "issue"], _events.Take(2));
        Assert.DoesNotContain("low", _events);
    }

    [Fact]
    public void AStreamWithinFifteenMinutesOfBootStartsYielding()
    {
        _uptimeMs = 10 * 60_000;
        _bps = Busy;
        Prewarmer p = Make();

        Assert.True(p.Run(Games.Make("a", "a.exe"), "service start").Ran);

        Assert.Equal(["observe", "low", "issue"], _events.Take(3));
    }

    [Fact]
    public void AStreamLongAfterBootStartsNormalWithoutAnObservationBeforeTheFirstBatch()
    {
        _bps = Busy;
        Prewarmer p = Make();

        Assert.True(p.Run(Games.Make("a", "a.exe"), "service start").Ran);

        Assert.Equal("issue", _events[0]);
    }

    [Fact]
    public void AFailedPriorityChangeKeepsReadingAndLogsNoYield()
    {
        _bps = Busy;
        Prewarmer p = Make(lowFails: true);

        Assert.True(p.Run(Games.Make("a", "a.exe"), "trigger steam.exe").Ran);

        Assert.DoesNotContain(_log.Lines, l => l.Contains("yields to other disk activity", StringComparison.Ordinal));
        Assert.DoesNotContain("normal", _events); // 바뀌지 않았으니 되돌릴 것도 없음
    }

    // 멈춘 구간만 재는 가짜: settle·window 를 부르는 순서를 기록하고 멈춘 구간의 외부 처리량을 돌려줌. 공유 표본 (DiskSample) 은 우리 읽기가 섞인 값을 보여 줌.
    private void UseWindow(double? externalBps, DiskActivity.SampleState state = DiskActivity.SampleState.Ok) => _window = (_, settle, window) =>
    {
        _events.Add("measure-start");
        settle();
        window();
        _events.Add("measure-end");
        return (externalBps, state);
    };

    [Fact]
    public void OurOwnReadsBeforeThePauseDoNotMakeAnIdleDiskLookBusy()
    {
        _bps = Busy; // 공유 표본 창이 멈춤 앞의 우리 읽기를 포함한 값 (옛 방식은 이것을 읽어 양보로 떨어짐)
        UseWindow(0); // 멈춘 구간의 외부 처리량은 0
        Prewarmer p = Make();

        Assert.True(p.Run(Games.Make("a", "a.exe"), "trigger steam.exe").Ran);

        Assert.DoesNotContain("low", _events);
        Assert.Equal(["measure-start", "observe", "observe", "measure-end", "issue"], _events.Take(5)); // 앞부분 비움 + 뒷부분 측정, 첫 묶음 직전
        Assert.True(_events.Count(e => e == "measure-end") >= 2, "10 초마다 다시 잼");
    }

    [Fact]
    public void ALoadMeasuredInsideThePauseYieldsEvenIfTheSharedSampleLooksIdle()
    {
        _bps = 0;
        UseWindow(Busy);
        Prewarmer p = Make();

        Assert.True(p.Run(Games.Make("a", "a.exe"), "trigger steam.exe").Ran);

        Assert.Equal(["measure-start", "observe", "observe", "measure-end", "low"], _events.Take(5));
    }

    [Fact]
    public void AFailedWindowMeasureYieldsBecauseTheLoadIsUnknown()
    {
        UseWindow(null, DiskActivity.SampleState.Warming);
        Prewarmer p = Make();

        Assert.True(p.Run(Games.Make("a", "a.exe"), "trigger steam.exe").Ran);

        Assert.Contains("low", _events);
    }

    [Fact]
    public void ThePauseKeepsItsTotalLengthWhenSplitIntoDrainAndMeasure()
    {
        _window = (_, settle, window) =>
        {
            settle();
            window();
            return (0, DiskActivity.SampleState.Ok);
        };
        Prewarmer p = Make();

        Assert.True(p.Run(Games.Make("a", "a.exe"), "trigger steam.exe").Ran);

        Assert.Equal(TimeSpan.FromSeconds(1), _slept[0] + _slept[1]); // 첫 관측: 200 ms + 800 ms
    }

    // 로딩 앞부분 읽기: 게임이 둘 이상인 묶음에서 전체 예열보다 먼저 한 번씩 읽음.
    private sealed class HeadStreamer(PrewarmYieldTests t) : IGameStreamer
    {
        public StreamStats Stream(GameSettings game) => new(100 * Mib, 1, 0, 0.01);

        public void StreamHead(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            t._events.Add("head");
            progress?.Invoke(0, 100 * Mib);
            progress?.Invoke(50 * Mib, 100 * Mib);
        }
    }

    [Fact]
    public void TheLoadHeadReadYieldsLikeTheHotSetStreamAndRestoresNormal()
    {
        _uptimeMs = 10_000;
        _bps = Busy;
        UseWindow(Busy);
        GameSettings[] games = [Games.Make("a", "a.exe"), Games.Make("b", "b.exe")];
        var p = new Prewarmer(games, new HeadStreamer(this), Env(), _log, null, () => _now);

        p.RunServiceStart();

        int head = _events.IndexOf("head");
        Assert.True(head >= 0);
        int low = _events.IndexOf("low", head);
        Assert.True(low > head, "앞부분 읽기 도중 낮음으로 바뀜");
        Assert.True(_events.IndexOf("normal", low) > low, "앞부분 읽기가 끝나면 보통으로 되돌림");
    }

    [Fact]
    public void TimeSpentOnNonDiskWorkWhileYieldingIsLeftOutOfTheYieldedSeconds()
    {
        var ry = new ReadYield(_ => true, startYielding: false);
        ry.Exclude(3); // 낮은 우선순위가 아닐 때의 시간은 애초에 양보 시간이 아님
        Assert.True(ry.Apply(low: true, _now));

        ry.Exclude(2); // 낮은 동안 다시 만지는 데 쓴 시간
        Assert.True(ry.Finish(_now + TimeSpan.FromSeconds(5)));

        Assert.Equal(3, ry.Seconds, precision: 3);
    }

    // 관측 멈춤마다 시각을 모으는 가짜: 외부 처리량은 outcomes 순서대로 (남으면 마지막 값 반복).
    private List<DateTimeOffset> ObserveWith(params (double? Bps, DiskActivity.SampleState State)[] outcomes)
    {
        List<DateTimeOffset> at = [];
        _window = (_, settle, window) =>
        {
            settle();
            window();
            at.Add(_now);
            return outcomes[Math.Min(at.Count - 1, outcomes.Length - 1)];
        };
        return at;
    }

    private static List<double> Gaps(List<DateTimeOffset> at) => [.. at.Zip(at.Skip(1), (a, b) => (b - a).TotalSeconds)];

    [Fact]
    public void AfterThreeIdleObservationsInARowTheObservationPauseWaitsThirtySeconds()
    {
        _streamer.Batches = 20; // 80 초
        List<DateTimeOffset> at = ObserveWith((0, DiskActivity.SampleState.Ok));
        Prewarmer p = Make();

        Assert.True(p.Run(Games.Make("a", "a.exe"), "trigger steam.exe").Ran);

        List<double> gaps = Gaps(at);
        Assert.Equal(4, at.Count); // 처음, 10 초 간격 두 번, 그 뒤 30 초
        Assert.All(gaps.Take(2), g => Assert.InRange(g, 10, 15));
        Assert.InRange(gaps[2], 30, 35);
    }

    [Fact]
    public void ABusyObservationBringsTheTenSecondIntervalBackAtOnce()
    {
        _streamer.Batches = 25; // 100 초
        (double?, DiskActivity.SampleState) idle = (0, DiskActivity.SampleState.Ok);
        List<DateTimeOffset> at = ObserveWith(idle, idle, idle, (Busy, DiskActivity.SampleState.Ok), idle);
        Prewarmer p = Make();

        Assert.True(p.Run(Games.Make("a", "a.exe"), "trigger steam.exe").Ran);

        List<double> gaps = Gaps(at);
        Assert.InRange(gaps[2], 30, 35); // 한가함 세 번 뒤 30 초
        Assert.InRange(gaps[3], 10, 15); // 바쁨 한 번 뒤 바로 10 초
        Assert.InRange(gaps[4], 10, 15); // 한가함 연속이 처음부터 다시 셈
    }

    [Theory]
    [InlineData(DiskActivity.SampleState.Warming)]
    [InlineData(DiskActivity.SampleState.Stale)]
    public void AnObservationWithoutAUsableSampleIsNotCountedAsIdle(DiskActivity.SampleState state)
    {
        _streamer.Batches = 20; // 80 초
        List<DateTimeOffset> at = ObserveWith((null, state));
        Prewarmer p = Make();

        Assert.True(p.Run(Games.Make("a", "a.exe"), "trigger steam.exe").Ran);

        Assert.True(at.Count >= 6, "간격이 늘지 않고 10 초마다 잼");
        Assert.All(Gaps(at), g => Assert.InRange(g, 10, 15));
    }

    [Fact]
    public void ReadYieldCountsReadBytesAndTimeAtEachPriorityAndTheSwitches()
    {
        var ry = new ReadYield(_ => true, startYielding: false);
        ry.NoteRead(0, _now);
        ry.NoteRead(100 * Mib, _now + TimeSpan.FromSeconds(4)); // 보통
        Assert.True(ry.Apply(low: true, _now + TimeSpan.FromSeconds(4)));
        ry.NoteRead(300 * Mib, _now + TimeSpan.FromSeconds(12)); // 낮음
        Assert.False(ry.Apply(low: false, _now + TimeSpan.FromSeconds(12)));
        ry.NoteRead(0, _now + TimeSpan.FromSeconds(13)); // 새 읽기 목록: 기준만 다시 잡음

        Assert.Equal(new ReadMix(100 * Mib, 4, 200 * Mib, 8, 2), ry.Mix);
        Assert.Equal(", read 100 MiB in 4.0 s at normal I/O priority and 200 MiB in 8.0 s at low (2 switches)", ry.Mix.Note());
        Assert.Equal(1, ry.ToNormal);
        Assert.Equal(string.Empty, new ReadMix(100 * Mib, 4, 0, 0, 0).Note()); // 낮음을 쓰지 않았으면 줄은 그대로
    }

    [Fact]
    public void TheDoneLineAddsTheReadMixAndOnlyTheFirstReturnToNormalIsLogged()
    {
        _streamer.Batches = 20;
        _streamer.OnIssue = i => _bps = i is (>= 2 and < 5) or (>= 8 and < 11) ? Busy : 0; // 두 번 낮아졌다가 돌아옴
        Prewarmer p = Make();

        Assert.True(p.Run(Games.Make("a", "a.exe"), "rewarm").Ran);

        Assert.Single(_log.Lines, l => l.Contains("a: prewarm back to normal I/O priority after ", StringComparison.Ordinal));
        string done = Assert.Single(_log.Lines, l => l.Contains("prewarm done (rewarm)", StringComparison.Ordinal));
        Assert.Matches(@", yielded [\d.]+ s, read \d+ MiB in [\d.]+ s at normal I/O priority and [1-9]\d* MiB in [\d.]+ s at low \([1-9]\d* switches\)", done);
    }

    [Fact]
    public void ALoadHeadReadLogsItsStartAndEndWithTheAmountAndTime()
    {
        GameSettings[] games = [Games.Make("a", "a.exe"), Games.Make("b", "b.exe")];
        var p = new Prewarmer(games, new HeadStreamer(this), Env(), _log, null, () => _now);

        p.RunAll(games, "rewarm");

        Assert.Single(_log.Lines, l => l.Contains("a: load head prewarm started", StringComparison.Ordinal));
        Assert.Single(_log.Lines, l => l.Contains("a: load head prewarm done: 50 MiB in ", StringComparison.Ordinal));
    }

    [Fact]
    public void AGameWhoseLoadHeadIsNotReadLogsTheReason()
    {
        GameSettings[] games = [Games.Make("a", "a.exe"), Games.Make("b", "b.exe")];
        PrewarmEnv env = Env();
        var p = new Prewarmer(games, new HeadStreamer(this), new PrewarmEnv
        {
            AvailableRamMib = env.AvailableRamMib,
            GameRunning = env.GameRunning,
            ZoneBytes = g => g.Name == "b" ? null : 800 * Mib,
            TraceExists = env.TraceExists,
            UptimeMs = env.UptimeMs,
            Sleep = env.Sleep,
            ProbeReadMs = env.ProbeReadMs,
            DiskSample = env.DiskSample,
            SetReadIoLow = env.SetReadIoLow,
        }, _log, null, () => _now);

        p.RunAll(games, "rewarm");

        Assert.Single(_log.Lines, l => l.Contains("b: load head prewarm skipped (no plan file)", StringComparison.Ordinal));
    }

    [Fact]
    public void PreparationLongerThanFiveSecondsLogsEachStepOnce()
    {
        Prewarmer p = Make();

        var heads = new Prewarmer.HeadTimes { Planning = 30.4, Reading = 1.1 };
        p.LogPrepared("trigger steam.exe", sampled: 3.2, ordered: 0.1, planned: 1.5, heads, headsTotal: 52.9);
        p.LogPrepared("trigger steam.exe", sampled: 1, ordered: 1, planned: 1, new Prewarmer.HeadTimes(), headsTotal: 1); // 5 초 미만은 남기지 않음

        string line = Assert.Single(_log.Lines, l => l.Contains("prewarm prepared in ", StringComparison.Ordinal));
        Assert.Contains("prewarm prepared in 57.7 s before the per-game reads [trigger steam.exe]: cooldown and cold samples 3.2 s, order 0.1 s, lists and layout lookups 1.5 s, load head plans and layout lookups 30.4 s, load head reads 1.1 s, other waits in load heads 21.4 s", line, StringComparison.Ordinal);
    }

    // 앞부분 스트리머: 첫 읽기를 내기 전 계획 (planMs) 과 읽기 (readMs) 에 실제로 시간을 씀 (준비 시간 줄이 올바른 칸에 넣는지 보려고). 읽기 전 진행 0 은 알리지 않음 (실제 스트리머와 같음).
    private sealed class SlowHeadStreamer(PrewarmYieldTests t, int planMs, int readMs) : IGameStreamer
    {
        public StreamStats Stream(GameSettings game) => new(100 * Mib, 1, 0, 0.01);

        public void StreamHeadCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue)
        {
            noteIssue?.Invoke("head schedule", 0, false);
            Thread.Sleep(planMs); // 목록 계산·배치 조회
            noteIssue?.Invoke("stream batch", 100 * Mib, true);
            Thread.Sleep(readMs); // 읽기
            t._now += TimeSpan.FromSeconds(4);
            progress?.Invoke(100 * Mib, 200 * Mib);
            t._now += TimeSpan.FromSeconds(4);
            progress?.Invoke(200 * Mib, 200 * Mib);
        }
    }

    private sealed class CancelingHeadStreamer(CancellationTokenSource caller) : IGameStreamer
    {
        public StreamStats Stream(GameSettings game) => new(100 * Mib, 1, 0, 0.01);

        public void StreamHeadCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, Func<bool> canIssue, Action<string, long, bool>? noteIssue)
        {
            caller.Cancel(); // 계획 도중 서비스 종료 등으로 호출자 토큰이 취소됨
            throw new OperationCanceledException(caller.Token);
        }
    }

    [Fact]
    public void ACancelledStreamCountsTheLastBatchAtItsPriority()
    {
        _streamer.Batches = 20;
        _bps = Busy;
        using var cts = new CancellationTokenSource();
        _streamer.OnIssue = i =>
        {
            if (i == 5)
            {
                cts.Cancel(); // 여섯 번째 묶음 (600 MiB) 을 읽은 직후 취소: 진행 알림이 취소 확인에 걸려도 그 양을 세어야 함
            }
        };
        Prewarmer p = Make();

        PrewarmResult r = p.Run(Games.Make("a", "a.exe"), "rewarm", ct: cts.Token);

        Assert.False(r.Ran);
        string line = Assert.Single(_log.Lines, l => l.Contains("prewarm cancelled (rewarm)", StringComparison.Ordinal));
        Assert.Matches(@"after 600 MiB \([^)]*\), read 300 MiB in [\d.]+ s at normal I/O priority and 300 MiB in [\d.]+ s at low \(1 switches\)", line); // 보통 세 묶음 + 낮음 세 묶음
    }

    [Fact]
    public void TheLoadHeadFirstBatchIsCountedAtItsPriorityEvenWithoutAZeroProgressCallback()
    {
        _uptimeMs = 10_000;
        UseWindow(Busy);
        GameSettings[] games = [Games.Make("a", "a.exe"), Games.Make("b", "b.exe")];
        var p = new Prewarmer(games, new SlowHeadStreamer(this, 0, 0), Env(), _log, null, () => _now);

        p.RunAll(games, "rewarm");

        string line = Assert.Single(_log.Lines, l => l.Contains("a: load head prewarm done:", StringComparison.Ordinal));
        Assert.Matches(@"done: 200 MiB in [\d.]+ s, read 100 MiB in 4\.0 s at normal I/O priority and 100 MiB in 5\.0 s at low \(1 switches\)", line);
    }

    [Fact]
    public void ThePreparedLineSeparatesTheLoadHeadPlanFromTheLoadHeadRead()
    {
        GameSettings[] games = [Games.Make("a", "a.exe"), Games.Make("b", "b.exe")];
        var p = new Prewarmer(games, new SlowHeadStreamer(this, planMs: 150, readMs: 250), Env(), _log, null, () => _now) { PrepareLogAbove = TimeSpan.Zero };

        p.RunAll(games, "rewarm");

        string line = Assert.Single(_log.Lines, l => l.Contains("prewarm prepared in ", StringComparison.Ordinal));
        System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(line, @"load head plans and layout lookups ([\d.]+) s, load head reads ([\d.]+) s");
        Assert.True(m.Success, line);
        Assert.True(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) >= 0.2, "게임 둘의 계획 150 ms 씩이 계획 칸에");
        Assert.True(double.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture) >= 0.4, "게임 둘의 읽기 250 ms 씩이 읽기 칸에");
    }

    [Fact]
    public void ACallerCancelDuringTheLoadHeadStillLogsTheHeadEndAndThePreparedLine()
    {
        _uptimeMs = 10_000;
        _bps = Busy;
        UseWindow(Busy);
        using var cts = new CancellationTokenSource();
        GameSettings[] games = [Games.Make("a", "a.exe"), Games.Make("b", "b.exe")];
        var p = new Prewarmer(games, new CancelingHeadStreamer(cts), Env(), _log, null, () => _now) { PrepareLogAbove = TimeSpan.Zero };

        try
        {
            p.RunServiceStart(ct: cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 호출자 취소는 예외로 나올 수 있음
        }

        Assert.Single(_log.Lines, l => l.Contains("a: load head prewarm interrupted (caller cancelled) after ", StringComparison.Ordinal));
        Assert.Single(_log.Lines, l => l.Contains("prewarm prepared in ", StringComparison.Ordinal));
    }
}
