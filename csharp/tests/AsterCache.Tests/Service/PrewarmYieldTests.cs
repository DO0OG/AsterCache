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

    // 100 MiB 묶음 여덟 개를 차례로 읽고 (묶음마다 4 초가 흐름) 묶음마다 진행을 알림 (StreamPrewarmCore 의 Batches 와 같은 순서: 취소 확인 → 읽기 → 진행).
    private sealed class BatchStreamer(PrewarmYieldTests t) : IGameStreamer
    {
        public Action<int>? OnIssue { get; set; }

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            progress?.Invoke(0, 800 * Mib);
            long done = 0;
            for (int i = 0; i < 8 && !stop.IsCancellationRequested; i++)
            {
                t._events.Add("issue");
                t._now += TimeSpan.FromSeconds(4);
                done += 100 * Mib;
                OnIssue?.Invoke(i);
                progress?.Invoke(done, 800 * Mib);
            }

            return (new StreamStats(done, 8, 0, 0.01), !stop.IsCancellationRequested);
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
}
