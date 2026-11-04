using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 모든 예열 읽기 경로 (핫 집합 스트림, 앞부분, 기록 없는 통째·프로필) 가 같은 "디스크 읽기 중" 표시를 세우고 어떻게 끝나도 내리는가,
/// 그리고 그 표시와 재배치 시작 표시가 _gate 아래에서 한 번에 확인돼 틈이 없는가.
public sealed class PrewarmReadMarkTests
{
    private const long Mib = 1L << 20;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private readonly ListLog _log = new();
    private readonly CallbackStreamer _streamer = new();
    private long _hours;

    private string Label { get; set; } = "full";

    private Func<CancellationToken, (StreamStats, bool)> ColdRead { get; set; } = _ => (new StreamStats(Mib, 1, 0, 0.1), true);

    private static GameSettings Game(string name) => Games.Make(name, name + ".exe");

    private static GameSettings OtherDisk(string name) => Games.Make(name, name + ".exe") with { OriginRoot = @"C:\Games\" + name };

    // cold 가 참이면 기록 없는 게임 (통째·프로필 읽기), 아니면 핫 집합 스트림. 시계는 호출마다 1 시간 흘러 쿨다운이 걸리지 않음.
    private Prewarmer Make(bool cold, AutoSettings? auto, params GameSettings[] games)
    {
        var p = new Prewarmer(games, _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = _ => false,
            ZoneBytes = _ => Mib,
            TraceExists = _ => !cold,
            ColdPlanner = (_, _, _, _) => new ColdPlan(Label, [new ReadOp(@"D:\g\a.bin", 0, Mib)], Mib, null),
            ColdRead = (_, ct) => ColdRead(ct),
        }, _log, null, () => DateTimeOffset.UnixEpoch + TimeSpan.FromHours(Interlocked.Increment(ref _hours)));
        p.SetAuto(auto ?? AutoSettings.Default);
        return p;
    }

    [Theory]
    [InlineData("ok")]
    [InlineData("io")]
    [InlineData("cancel")]
    [InlineData("partial")]
    public void WholeFolderReadMarksTheDiskWhileItReadsAndClearsItHoweverItEnds(string how)
    {
        Prewarmer p = Make(true, null, Game("g"));
        bool during = false;
        ColdRead = _ =>
        {
            during = p.StreamingOnDisk("g");
            if (how == "io")
            {
                throw new IOException("read");
            }

            if (how == "cancel")
            {
                throw new OperationCanceledException();
            }

            return (new StreamStats(Mib, 1, 0, 0.1), how == "ok");
        };

        PrewarmResult r = p.Run(Game("g"), "t");

        Assert.True(during);
        Assert.False(p.StreamingOnDisk("g"));
        Assert.Equal(how == "ok", r.Ran);
        Assert.True(p.TryBeginRelocation("g")); // 읽기 표시가 남아 있지 않음
    }

    [Fact]
    public void EngineProfileReadMarksTheDiskToo()
    {
        Prewarmer p = Make(true, AutoSettings.Default with { FullPrewarm = false, EngineProfiles = true }, Game("g"));
        Label = "profile";
        bool during = false;
        ColdRead = _ =>
        {
            during = p.StreamingOnDisk("g");
            return (new StreamStats(Mib, 1, 0, 0.1), true);
        };

        PrewarmResult r = p.Run(Game("g"), "t");

        Assert.Equal("profile", r.Mode);
        Assert.True(during);
        Assert.False(p.StreamingOnDisk("g"));
    }

    [Fact]
    public void HotStreamMarkIsClearedEvenWhenTheStreamThrows()
    {
        Prewarmer p = Make(false, null, Game("g"));
        _streamer.OnStream = () => throw new IOException("stream");

        PrewarmResult r = p.Run(Game("g"), "t");

        Assert.False(r.Ran);
        Assert.False(p.StreamingOnDisk("g"));
        Assert.True(p.TryBeginRelocation("g"));
    }

    [Fact]
    public void HeadPrewarmMarksTheDiskWhileItReadsAndClearsItAfterwards()
    {
        Prewarmer p = Make(false, null, Game("a"), Game("b"));
        var during = new List<bool>();
        _streamer.OnHead = () => during.Add(p.StreamingOnDisk("a"));

        p.RunAll([Game("a"), Game("b")], "t");

        Assert.Equal([true, true], during); // 앞부분 묶음에서 두 게임 모두
        Assert.False(p.StreamingOnDisk("a"));
        Assert.True(p.TryBeginRelocation("a"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelocationStartMarkStopsReadsOnTheSameDiskUntilItEnds(bool cold)
    {
        Prewarmer p = Make(cold, null, Game("g"), Game("h"), OtherDisk("c"));
        int reads = 0;
        ColdRead = _ =>
        {
            reads++;
            return (new StreamStats(Mib, 1, 0, 0.1), true);
        };
        _streamer.OnStream = () => reads++;

        Assert.True(p.TryBeginRelocation("G")); // 대소문자 무시

        foreach (string name in new[] { "g", "h" }) // 같은 게임, 같은 디스크의 다른 게임
        {
            PrewarmResult r = p.Run(Game(name), "t");
            Assert.False(r.Ran);
            Assert.Equal(Prewarmer.RelocationRunning, r.SkipReason);
            Assert.Null(p.LastWarmed(name)); // 읽지 않았으므로 예열한 것으로 기록하지 않음
        }

        Assert.Equal(0, reads);
        Assert.False(p.StreamingOnDisk("g"));
        Assert.True(p.Run(OtherDisk("c"), "t").Ran); // 다른 디스크는 막지 않음
        Assert.Equal(1, reads);

        p.EndRelocation("g");

        Assert.True(p.Run(Game("g"), "t").Ran);
        Assert.Equal(2, reads);
    }

    [Fact]
    public void HeadPrewarmDoesNotReadWhileARelocationStarted()
    {
        Prewarmer p = Make(false, null, Game("a"), Game("b"));
        int heads = 0;
        _streamer.OnHead = () => heads++;
        Assert.True(p.TryBeginRelocation("a"));

        IReadOnlyList<PrewarmResult> results = p.RunAll([Game("a"), Game("b")], "t");

        Assert.Equal(0, heads);
        Assert.All(results, r => Assert.Equal(Prewarmer.RelocationRunning, r.SkipReason));

        p.EndRelocation("a");
        p.RunAll([Game("a"), Game("b")], "t");

        Assert.Equal(2, heads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RelocationCannotStartWhileAReadIsInProgressAndTheRefusalLeavesNoMark(bool cold)
    {
        Prewarmer p = Make(cold, null, Game("g"), Game("h"));
        var refused = new List<bool>();
        Action probe = () =>
        {
            refused.Add(!p.TryBeginRelocation("g"));
            refused.Add(!p.TryBeginRelocation("h")); // 같은 디스크의 다른 게임
        };
        ColdRead = _ =>
        {
            probe();
            return (new StreamStats(Mib, 1, 0, 0.1), true);
        };
        _streamer.OnStream = probe;

        Assert.True(p.Run(Game("g"), "t").Ran);

        Assert.Equal([true, true], refused);
        Assert.True(p.Run(Game("h"), "t").Ran); // 거부된 시도가 재배치 표시를 남겼다면 막혔을 것
    }

    [Fact]
    public async Task ReadMarkAndRelocationMarkAreNeverBothHeld()
    {
        Prewarmer p = Make(true, null, Game("g"));
        int inside = 0;
        int active = 0;
        int violations = 0;
        int reads = 0;
        ColdRead = _ =>
        {
            Interlocked.Increment(ref reads);
            Interlocked.Exchange(ref inside, 1);
            if (Volatile.Read(ref active) == 1)
            {
                Interlocked.Increment(ref violations);
            }

            Thread.SpinWait(200);
            if (Volatile.Read(ref active) == 1)
            {
                Interlocked.Increment(ref violations);
            }

            Interlocked.Exchange(ref inside, 0);
            return (new StreamStats(Mib, 1, 0, 0.1), true);
        };
        using var stop = new CancellationTokenSource();
        Task reader = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                p.Run(Game("g"), "t");
            }
        });

        int began = 0;
        long deadline = Environment.TickCount64 + 20_000;
        while ((began < 500 || Volatile.Read(ref reads) < 500) && Environment.TickCount64 < deadline) // 읽기 쪽 스레드가 늦게 떠도 양쪽이 충분히 겹칠 때까지
        {
            if (p.TryBeginRelocation("g"))
            {
                began++;
                Interlocked.Exchange(ref active, 1);
                Thread.SpinWait(200);
                if (Volatile.Read(ref inside) == 1)
                {
                    Interlocked.Increment(ref violations);
                }

                Interlocked.Exchange(ref active, 0);
                p.EndRelocation("g");
            }

            Thread.Yield();
        }

        stop.Cancel();
        await reader.WaitAsync(Bound);
        Assert.Equal(0, violations);
        Assert.True(began >= 500);
        Assert.True(Volatile.Read(ref reads) >= 500);
    }

    private sealed class CallbackStreamer : IGameStreamer
    {
        public Action? OnStream { get; set; }

        public Action? OnHead { get; set; }

        public StreamStats Stream(GameSettings game)
        {
            OnStream?.Invoke();
            return new StreamStats(100, 2, 0, 0.01);
        }

        public void StreamHead(GameSettings game, CancellationToken stop) => OnHead?.Invoke();
    }
}
