using System.Globalization;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Tests.Cli;
using Xunit;

namespace AsterCache.Tests.Service;

/// 예열 순서 점수 (최근성 + 시간대 습관), 플레이 이력 상한·저장, 예산 경계, status 의 prewarm_score.
public sealed class PrewarmScoreTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 20, 0, 0, TimeSpan.Zero); // 월요일 저녁

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_prewarmscore_" + Guid.NewGuid().ToString("N"));

    public PrewarmScoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private static GameSettings Game(string name, PrewarmSettings? p = null) => Games.Make(name, name + ".exe") with { Prewarm = p ?? PrewarmSettings.Default };

    private static IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> Plays(params (string Name, DateTimeOffset[] At)[] games) =>
        games.ToDictionary(g => g.Name, g => (IReadOnlyList<DateTimeOffset>)g.At);

    private static IReadOnlyList<GameSettings> Ordered(IEnumerable<GameSettings> games, IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> plays, int n = 0) =>
        AutoMode.Recent(games, plays.ToDictionary(kv => kv.Key, kv => kv.Value.Max()), n, plays, Now);

    private static IReadOnlyList<string> Order(IEnumerable<GameSettings> games, IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> plays, int n = 0) =>
        [.. Ordered(games, plays, n).Select(g => g.Name)];

    [Fact]
    public void ScoreIsRecencyHalvingEverySevenDaysPlusTheHabitShare()
    {
        Assert.Equal(0, PrewarmScore.Compute([], Now));
        Assert.Equal(1, PrewarmScore.Compute([Now], Now), 6);
        Assert.Equal(0.5, PrewarmScore.Compute([Now.AddDays(-7)], Now), 6); // 이력 하나면 같은 요일·시각이어도 습관은 0
        Assert.Equal(0.5 + 1, PrewarmScore.Compute([Now.AddDays(-14), Now.AddDays(-7)], Now), 6);
        Assert.Equal(0.5 + 0.5, PrewarmScore.Compute([Now.AddDays(-8), Now.AddDays(-7)], Now), 6); // 둘 중 하나만 같은 요일
    }

    [Theory]
    [InlineData(120, 0.5)] // ±2 시간은 포함
    [InlineData(-120, 0.5)]
    [InlineData(121, 0)]
    public void HabitWindowIsPlusMinusTwoHoursOnTheSameWeekday(int minutes, double habitShare)
    {
        DateTimeOffset near = Now.AddDays(-7).AddMinutes(minutes);
        DateTimeOffset far = Now.AddDays(-30).AddHours(-12); // 요일 다름 (30 일 전 = 월요일에서 이틀 어긋남)

        double score = PrewarmScore.Compute([far, near], Now);

        Assert.Equal(Math.Pow(2, -(Now - near).TotalDays / PrewarmScore.HalfLifeDays) + habitShare, score, 6);
    }

    [Fact]
    public void NoHistoryOrOnePlayEachKeepsTheRecentOrder()
    {
        GameSettings[] games = [Game("a"), Game("b"), Game("c"), Game("d")];

        // a 는 정확히 7 일 전 같은 시각 (습관이 있어 보이지만 이력이 하나뿐), d 는 이력 없음
        IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> plays = Plays(("a", [Now.AddDays(-7)]), ("b", [Now.AddDays(-2)]), ("c", [Now.AddDays(-3)]));

        Assert.Equal(["b", "c", "a"], Order(games, plays));
        Assert.Equal(["b", "c"], Order(games, plays, 2));
        Assert.Equal(["b", "c", "a"], AutoMode.Recent(games, plays.ToDictionary(kv => kv.Key, kv => kv.Value[0]), 0).Select(g => g.Name)); // plays 를 안 주면 옛 최근순 그대로
    }

    [Fact]
    public void GameWithATimeOfDayHabitComesBeforeAMoreRecentOne()
    {
        GameSettings[] games = [Game("habit"), Game("recent")];
        IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> plays = Plays(
            ("habit", [Now.AddDays(-28), Now.AddDays(-21), Now.AddDays(-14), Now.AddDays(-7).AddMinutes(30)]), // 매주 월요일 저녁
            ("recent", [Now.AddDays(-3).AddHours(-8), Now.AddDays(-1).AddHours(-10)])); // 마지막이 더 최근이지만 시간대가 다름

        Assert.Equal(["habit", "recent"], Order(games, plays));
    }

    [Fact]
    public void EqualScoresFallBackToTheMostRecentThenName()
    {
        IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> same = Plays(("x", [Now.AddDays(-1)]), ("y", [Now.AddDays(-1)]));
        IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> vanished = Plays(("x", [Now.AddDays(-2000)]), ("y", [Now.AddDays(-1999)]));

        Assert.Equal(["x", "y"], Order([Game("y"), Game("x")], same));
        Assert.Equal(["y", "x"], Order([Game("x"), Game("y")], vanished)); // 점수가 0 으로 사라져도 최근순
    }

    [Fact]
    public void BatchTakesGamesInScoreOrderAndStopsAtTheBudget()
    {
        var streamer = new FakeStreamer();
        PrewarmSettings off = PrewarmSettings.Default with { OnServiceStart = false, MinFreeRamMib = 0 };
        GameSettings[] games = [Game("a", off), Game("b", off), Game("c", off)];
        var prewarm = new Prewarmer(games, streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 2500, // 존 1000 MiB 둘은 들어가고 (2000), 셋째는 3000 > 2500 이라 안 들어감
            GameRunning = _ => false,
            ZoneBytes = _ => 1000L << 20,
            TraceExists = _ => true,
        }, new ListLog(), null, () => Now);
        prewarm.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 1 << 20 }); // 이 시험은 점수 순서와 RAM 예산만 봄
        IReadOnlyDictionary<string, IReadOnlyList<DateTimeOffset>> plays = Plays(
            ("a", [Now.AddDays(-1)]), // 최근성 0.9
            ("b", [Now.AddDays(-14), Now.AddDays(-7)]), // 0.5 + 습관 1
            ("c", [Now.AddDays(-4)])); // 0.67

        IReadOnlyList<PrewarmResult> results = prewarm.RunServiceStart(Ordered(games, plays));

        Assert.Equal(["b", "a"], streamer.Calls.Select(c => c.Game));
        Assert.Equal([("b", true), ("a", true), ("c", false)], results.Select(r => (r.Game, r.Ran)));
        Assert.Equal("batch memory budget", results[2].SkipReason);
    }

    [Fact]
    public void StatusKeepsTheLatestThirtyPlaysAndWritesThemOut()
    {
        string path = Path.Combine(_dir, "auto_status.json");
        var status = new AutoStatus(path, new ListLog(), () => Now);
        for (int i = 0; i < 35; i++)
        {
            status.MarkPlayed("g", "auto", AutoStage.Discovered, Now.AddHours(i));
        }

        IReadOnlyList<DateTimeOffset> plays = status.Plays()["g"];
        Assert.Equal(AutoStatus.MaxPlays, plays.Count);
        Assert.Equal((Now.AddHours(5), Now.AddHours(34)), (plays[0], plays[^1]));
        var game = (JObject)((JObject)((JObject)JsonRead.ReadFile(path)!)["games"]!)["g"]!;
        Assert.Equal(30, ((List<object?>)game["plays"]!).Count);
        Assert.Equal(plays, new AutoStatus(path, new ListLog(), () => Now).Plays()["g"]);
    }

    [Fact]
    public void OldEntryWithOnlyLastPlayedCountsAsOnePlayAndKeepsGrowingFromThere()
    {
        string path = Path.Combine(_dir, "auto_status.json");
        File.WriteAllText(path, "{\"games\": {\"old\": {\"stage\": \"done\", \"last_played\": \"2026-10-30T10:00:00+00:00\"}, \"never\": {\"stage\": \"done\"}}}");
        var status = new AutoStatus(path, new ListLog(), () => Now);

        Assert.Equal([new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.Zero)], status.Plays()["old"]);
        Assert.False(status.Plays().ContainsKey("never"));

        status.MarkPlayed("old", "auto", AutoStage.Done, Now);

        Assert.Equal(2, status.Plays()["old"].Count);
    }

    [Fact]
    public void StatusCommandAddsPrewarmScoreToEveryGame()
    {
        DateTimeOffset now = DateTimeOffset.Now;
        string file = Path.Combine(_dir, "auto_status.json");
        string Iso(DateTimeOffset t) => t.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        File.WriteAllText(file, "{\"games\": {"
            + $"\"habit\": {{\"stage\": \"done\", \"last_played\": \"{Iso(now.AddDays(-7))}\", \"plays\": [\"{Iso(now.AddDays(-14))}\", \"{Iso(now.AddDays(-7))}\"]}},"
            + $"\"legacy\": {{\"stage\": \"done\", \"last_played\": \"{Iso(now)}\"}},"
            + "\"never\": {\"stage\": \"done\"}}}");

        (int code, string text, _) = CliTests.Run("status", "--prewarm-status", Path.Combine(_dir, "none.json"), "--auto-status", file);

        Assert.Equal(0, code);
        var games = (JObject)((JObject)((JObject)JsonRead.Parse(text)!)["auto"]!)["games"]!;
        Assert.InRange(Convert.ToDouble(((JObject)games["habit"]!)["prewarm_score"], CultureInfo.InvariantCulture), 1.49, 1.51);
        Assert.InRange(Convert.ToDouble(((JObject)games["legacy"]!)["prewarm_score"], CultureInfo.InvariantCulture), 0.99, 1.0);
        Assert.Null(((JObject)games["never"]!)["prewarm_score"]);
    }
}
