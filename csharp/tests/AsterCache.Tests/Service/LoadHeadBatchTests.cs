using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 묶음 예열 순서: 로딩 앞부분 (tier0) 을 모든 게임에서 먼저, 그 뒤 게임별 전체 (tier1 포함).
public sealed class LoadHeadBatchTests
{
    private readonly OrderStreamer _streamer = new();
    private bool _bRunning;

    private Prewarmer Make(string? statusPath, params GameSettings[] games) => new(games, _streamer, new PrewarmEnv
    {
        AvailableRamMib = () => 16000,
        GameRunning = g => g.Name == "b" && _bRunning,
        ZoneBytes = _ => 100L << 20,
        TraceExists = _ => true,
    }, new ListLog(), statusPath, () => DateTimeOffset.UnixEpoch);

    private static GameSettings Game(string name) => Games.Make(name, name + ".exe");

    [Fact]
    public void AllGamesLoadHeadsFirstThenEachGameFully()
    {
        Prewarmer p = Make(null, Game("a"), Game("b"));

        IReadOnlyList<PrewarmResult> results = p.RunAll([Game("a"), Game("b")], "t");

        Assert.Equal(["head a", "head b", "full a", "full b"], _streamer.Calls);
        Assert.All(results, r => Assert.True(r.Ran));
    }

    [Fact]
    public void GamesThatWillBeSkippedAndSingleGamesGetNoSeparateHeadPass()
    {
        _bRunning = true;
        GameSettings a = Game("a") with { OriginRoot = @"E:\Games" }, c = Game("c") with { OriginRoot = @"E:\Games\c" }; // b 와 같은 드라이브의 게임은 b 가 도는 동안 읽지 않으므로 다른 드라이브에 둠
        Prewarmer p = Make(null, a, Game("b"), c);

        p.RunAll([a, Game("b"), c], "t");

        Assert.Equal(["head a", "head c", "full a", "full c"], _streamer.Calls);

        _streamer.Calls.Clear();
        Make(null, Game("a")).RunAll([Game("a")], "t");
        Assert.Equal(["full a"], _streamer.Calls);
    }

    [Fact]
    public void CancelledBatchLoadsNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Make(null, Game("a"), Game("b")).RunServiceStart([Game("a"), Game("b")], cts.Token);

        Assert.Empty(_streamer.Calls);
    }

    [Fact]
    public void StatusFileTellsTheLoadHeadOfWarmedGames()
    {
        string dir = Directory.CreateTempSubdirectory("astercache-head-").FullName;
        try
        {
            string path = Path.Combine(dir, "prewarm_status.json");

            Make(path, Game("a")).RunAll([Game("a")], "t");

            Assert.Contains("\"head_mib\": 5", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class OrderStreamer : IGameStreamer
    {
        public List<string> Calls { get; } = [];

        public StreamStats Stream(GameSettings game)
        {
            Calls.Add("full " + game.Name);
            return new StreamStats(100, 2, 0, 0.01);
        }

        public void StreamHead(GameSettings game, CancellationToken stop) => Calls.Add("head " + game.Name);

        public long? LoadHeadBytes(GameSettings game) => 5L << 20;
    }
}
