using System.Reflection;
using System.Text.Json.Nodes;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// SOLAR 판정 (순수 함수), 본체 (표식·만지기·로그), 예열·유지 틱과의 연결 (가짜 환경과 가짜 스트리머).
public sealed class SolarTests
{
    private const long Mib = 1L << 20;
    private readonly ListLog _log = new();
    private readonly List<IReadOnlyList<ReadOp>> _touched = [];
    private DateTimeOffset _now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    private bool _enabled = true;
    private MemoryLists? _lists = new(0, 0, 8000 * Mib);
    private int _listQueries;
    private TouchResult _result = new(100 * Mib, 0.4, 0, 0, TouchEnd.Completed);

    private static readonly ReadOp[] Reads = [new ReadOp(@"D:\a\1.bin", 0, 10 * Mib), new ReadOp(@"D:\a\2.bin", 0, 10 * Mib), new ReadOp(@"D:\a\1.bin", 50 * Mib, 10 * Mib)];

    private static GameSettings Game(string name) => Games.Make(name, name + ".exe");

    private Action? _onTouch;

    private PrewarmEnv Env() => new()
    {
        AvailableRamMib = () => 20000,
        GameRunning = _ => false,
        ZoneBytes = _ => 1000 * Mib,
        TraceExists = _ => true,
        StandbyLadderEnabled = () => _enabled,
        Lists = () =>
        {
            _listQueries++;
            return _lists;
        },
        TouchRanges = (ranges, ct) =>
        {
            _touched.Add(ranges);
            _onTouch?.Invoke();
            return ct.IsCancellationRequested ? _result with { End = TouchEnd.Canceled } : _result;
        },
    };

    private Solar Make() => new(Env(), _log, () => _now);

    // ---- 판정 (순수 함수) ----

    private static LadderMark Mark(long? repurposedMib, long aheadMib, DateTimeOffset at) => new(at, repurposedMib is { } r ? r * Mib : null, aheadMib * Mib, 0, []);

    // 다른 게임이 만지기 전에 가졌던 표식 (그때 만진 양과 만든 시각만 의미 있음)
    private static LadderMark Previous(long touchedMib, DateTimeOffset at) => new(at, null, 0, touchedMib * Mib, []);

    [Theory]
    [InlineData(10000, 3999, false)] // A 10000 MiB 의 0.4 배 = 4000 MiB 미만
    [InlineData(10000, 4000, true)]
    [InlineData(1000, 511, false)] // A 가 작으면 하한 512 MiB (0.4 x 1000 = 400 보다 큼)
    [InlineData(1000, 512, true)]
    [InlineData(0, 512, true)]
    [InlineData(0, 0, false)] // 재사용이 없으면 아무 일도 하지 않음
    public void RefreshIsDueWhenCacheReuseGrowsByAFractionOfWhatIsAheadWithAFloor(long aheadMib, long grownMib, bool due)
    {
        LadderMark mark = Mark(5000, aheadMib, _now);

        Assert.Equal(due, SolarPolicy.RefreshDue(mark, new MemoryLists(0, (5000 + grownMib) * Mib), _now));
    }

    [Fact]
    public void WithoutMemoryListsTheTimeRuleOfTenMinutesApplies()
    {
        LadderMark mark = Mark(5000, 10000, _now);

        Assert.False(SolarPolicy.RefreshDue(mark, null, _now + TimeSpan.FromMinutes(9.9)));
        Assert.True(SolarPolicy.RefreshDue(mark, null, _now + TimeSpan.FromMinutes(10)));
        Assert.False(SolarPolicy.RefreshDue(Mark(null, 10000, _now), new MemoryLists(0, 1L << 40), _now + TimeSpan.FromMinutes(1))); // 표식을 만들 때 못 읽었으면 지금 값이 있어도 시간으로
        Assert.True(SolarPolicy.RefreshDue(Mark(null, 10000, _now), new MemoryLists(0, 1L << 40), _now + TimeSpan.FromMinutes(10)));
    }

    [Fact]
    public void AheadIsStandbyMinusWhatThisGameTouchedAndNeverNegative()
    {
        Assert.Equal(7000 * Mib, SolarPolicy.Ahead(8000 * Mib, 1000 * Mib));
        Assert.Equal(0, SolarPolicy.Ahead(500 * Mib, 1000 * Mib));
    }

    [Theory]
    [InlineData(6000, 2500, 400, false)] // 표식 A 6000 MiB 가 다른 게임이 만져 2500 MiB 로 줄었음: 남은 여유 2100 MiB, 비율 기준 (하한 512 MiB) 에도 못 미침
    [InlineData(6000, 2500, 453, true)] // 남은 여유 2047 MiB < 2 GiB
    [InlineData(1500, 1500, 100, false)] // 표식을 만들 때 A 가 4 GiB 미만이면 남은 여유 기준은 적용하지 않음
    [InlineData(1500, 1500, 599, false)] // 비율 기준 0.4 x 1500 = 600 MiB
    [InlineData(1500, 1500, 600, true)]
    public void RefreshIsAlsoDueWhenWhatIsLeftAheadFallsUnderTwoGiBUnlessTheMarkWasSmallToBeginWith(long markedMib, long aheadMib, long grownMib, bool due)
    {
        LadderMark mark = Mark(5000, markedMib, _now) with { AheadBytes = aheadMib * Mib };

        Assert.Equal(due, SolarPolicy.RefreshDue(mark, new MemoryLists(0, (5000 + grownMib) * Mib), _now));
    }

    [Theory]
    [InlineData(-1, 300, 7000, 6700)] // X 의 이전 표식이 내 표식보다 먼저 (그때 X 의 페이지가 내 앞에 있었음): 줄임
    [InlineData(1, 300, 7000, 7000)] // X 의 이전 표식이 내 표식보다 나중: X 의 페이지는 원래 내 뒤였으므로 줄이지 않음
    [InlineData(0, 300, 7000, 7000)] // 같은 시각도 먼저가 아님
    [InlineData(99, 300, 7000, 7000)] // X 에 이전 표식이 없었음 (새로 예열된 게임): 줄이지 않음
    [InlineData(-1, 300, 200, 6800)] // min(이전 표식이 만진 300, 이번에 만진 200) = 200: 이번에 적게 만졌으면 그만큼만
    [InlineData(-1, 100, 200, 6900)] // min(100, 200) = 100: 이전 표식이 적게 만졌으면 (나머지는 디스크에서 새로 읽은 것) 그만큼만
    [InlineData(-1, 9000, 9000, 0)] // 0 미만이 되지 않음
    public void WhatIsAheadFallsOnlyByWhatTheOthersMovedFromAheadOfUsToBehindUs(int previousIsEarlier, long previousTouchedMib, long touchedNowMib, long expectedMib)
    {
        LadderMark mine = Mark(0, 7000, _now);
        LadderMark? previous = previousIsEarlier == 99 ? null : Previous(previousTouchedMib, _now + TimeSpan.FromMinutes(previousIsEarlier));

        Assert.Equal(expectedMib * Mib, SolarPolicy.AheadAfterOthersTouched(mine, [(previous, touchedNowMib * Mib)]));
    }

    [Fact]
    public void TheGroupAheadIsStandbyMinusWhatThisGameAndTheOnesTouchedAfterItTouched()
    {
        Assert.Equal([4000 * Mib, 5000 * Mib, 7000 * Mib], SolarPolicy.GroupAhead(10000 * Mib, [1000 * Mib, 2000 * Mib, 3000 * Mib]));
        Assert.Equal([0L, 0L, 500 * Mib], SolarPolicy.GroupAhead(3500 * Mib, [1000 * Mib, 2000 * Mib, 3000 * Mib]));
    }

    // ---- 본체 ----

    [Fact]
    public void OrderTouchesTheReadsInReverseAndLeavesAMarkWithTheAheadAmount()
    {
        Solar solar = Make();

        TouchEnd? end = solar.Order(Game("a"), Reads, CancellationToken.None);

        Assert.Equal(TouchEnd.Completed, end);
        Assert.Equal([new ReadOp(@"D:\a\1.bin", 50 * Mib, 10 * Mib), new ReadOp(@"D:\a\2.bin", 0, 10 * Mib), new ReadOp(@"D:\a\1.bin", 0, 10 * Mib)], _touched.Single());
        Assert.True(solar.HasMark("a"));
        Assert.Contains("a: ladder ordered 100 MiB in 0.4 s", _log.Lines.Single());
    }

    [Fact]
    public void OrderDoesNothingWhenTheSettingIsOffOrThereIsNothingToTouch()
    {
        _enabled = false;
        Solar solar = Make();

        Assert.Null(solar.Order(Game("a"), Reads, CancellationToken.None));
        _enabled = true;
        Assert.Null(solar.Order(Game("a"), [], CancellationToken.None));
        Assert.Empty(_touched);
        Assert.False(solar.HasMark("a"));
    }

    [Theory]
    [InlineData(TouchEnd.Canceled, "ladder stopped after 100 MiB")]
    [InlineData(TouchEnd.NeedsDiskReads, "ladder was too slow without disk reads seen (3 slow pieces), skipped")]
    public void AnInterruptedOrderLeavesNoMark(TouchEnd end, string text)
    {
        Solar solar = Make();
        Assert.Equal(TouchEnd.Completed, solar.Order(Game("a"), Reads, CancellationToken.None));
        _result = new TouchResult(100 * Mib, 0.4, 3, 0, end);

        Assert.Equal(end, solar.Order(Game("a"), Reads, CancellationToken.None));

        Assert.False(solar.HasMark("a"));
        Assert.Contains(text, _log.Lines.Last());
    }

    [Fact]
    public void BytesSeenReadFromDiskAreAppendedToTheLogLine()
    {
        _result = _result with { DiskBytes = 20 * Mib };

        _ = Make().Order(Game("a"), Reads, CancellationToken.None);

        Assert.EndsWith("MiB in 0.4 s, 20 MiB read from disk", _log.Lines.Single());
    }

    [Fact]
    public void ALadderThatFindsDataMissingFlagsTheGameOnceAndItIsClearedWhenTaken()
    {
        Solar solar = Make();
        _result = new TouchResult(12 * Mib, 0.6, 36, 0, TouchEnd.NeedsDiskReads, 37 * Mib);

        _ = solar.Order(Game("a"), Reads, CancellationToken.None);

        Assert.True(solar.HasMissing("a"));
        Assert.Equal("Info: a: ladder found data missing from memory after 12 of 30 MiB (36 slow pieces, 37 MiB read from disk), left to the rewarm check", _log.Lines.Single());
        Assert.True(solar.TakeMissing("a"));
        Assert.False(solar.TakeMissing("a"));
        Assert.False(solar.HasMissing("a"));
    }

    [Fact]
    public void ARefreshThatFindsDataMissingFlagsTheGameAndALaterCompleteLadderClearsIt()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _result = _result with { SlowPieces = 5, End = TouchEnd.NeedsDiskReads, DiskBytes = 32 * Mib };
        _ = solar.Refresh(Game("a"), CancellationToken.None);
        Assert.True(solar.HasMissing("a"));
        Assert.False(solar.HasMark("a"));

        _result = _result with { SlowPieces = 0, End = TouchEnd.Completed };
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);

        Assert.False(solar.HasMissing("a"));
        Assert.True(solar.HasMark("a"));
    }

    [Fact]
    public void ALadderThatEndedSlowWithoutAnyDiskReadSeenClearsTheMarkButDoesNotFlagTheGame()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _result = new TouchResult(10 * Mib, 0.6, 5, 1, TouchEnd.NeedsDiskReads);

        _ = solar.Refresh(Game("a"), CancellationToken.None);
        Assert.False(solar.HasMark("a"));
        Assert.False(solar.HasMissing("a"));
        Assert.Equal("Info: a: cache refresh was too slow without disk reads seen (5 slow pieces, 1 errors), left to the rewarm check", _log.Lines.Last());

        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        Assert.False(solar.HasMissing("a"));
        Assert.Equal("Info: a: ladder was too slow without disk reads seen (5 slow pieces, 1 errors), skipped", _log.Lines.Last());
    }

    [Fact]
    public void RestoreMissingFlagsAgainOnlyWhileThereIsNoMark()
    {
        Solar solar = Make();
        solar.RestoreMissing("a");
        Assert.True(solar.HasMissing("a"));

        _ = solar.Order(Game("a"), Reads, CancellationToken.None); // 사다리가 서서 표시가 지워짐
        solar.RestoreMissing("a");

        Assert.False(solar.HasMissing("a"));
    }

    [Fact]
    public void ACanceledLadderDoesNotFlagAndPruneDropsTheFlagOfAGameThatLeftTheSettings()
    {
        Solar solar = Make();
        _result = _result with { End = TouchEnd.Canceled };
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        Assert.False(solar.HasMissing("a"));

        _result = _result with { End = TouchEnd.NeedsDiskReads, DiskBytes = 32 * Mib };
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _ = solar.Order(Game("b"), Reads, CancellationToken.None);
        solar.Prune(new HashSet<string>(["b"], StringComparer.OrdinalIgnoreCase));

        Assert.False(solar.HasMissing("a"));
        Assert.True(solar.HasMissing("b"));
    }

    [Fact]
    public void ErrorsAreAppendedToTheLogLine()
    {
        _result = _result with { Errors = 2 };

        _ = Make().Order(Game("a"), Reads, CancellationToken.None);

        Assert.EndsWith("MiB in 0.4 s, 2 errors", _log.Lines.Single());
    }

    [Fact]
    public void SparseBytesNotTouchedAreAppendedToEveryLadderAndRefreshLineAndTheMissingLineKeepsTheWholeListAsN()
    {
        Solar solar = Make();
        _result = _result with { SkippedBytes = 12 * Mib };

        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _ = solar.Refresh(Game("a"), CancellationToken.None);
        Assert.EndsWith("MiB in 0.4 s, 12 MiB in sparse files not touched", _log.Lines.ElementAt(0));
        Assert.EndsWith("MiB ahead), 12 MiB in sparse files not touched", _log.Lines.ElementAt(1));

        _result = _result with { End = TouchEnd.Canceled };
        _ = solar.Refresh(Game("a"), CancellationToken.None);
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        Assert.EndsWith("cache refresh stopped after 100 MiB, 12 MiB in sparse files not touched", _log.Lines.ElementAt(2));
        Assert.EndsWith("ladder stopped after 100 MiB, 12 MiB in sparse files not touched", _log.Lines.ElementAt(3));

        _result = _result with { End = TouchEnd.Completed };
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _result = new TouchResult(6 * Mib, 0.6, 3, 0, TouchEnd.NeedsDiskReads, 37 * Mib, 12 * Mib);
        _ = solar.Refresh(Game("a"), CancellationToken.None);
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        Assert.Equal("Info: a: cache refresh found data missing from memory after 6 of 30 MiB (3 slow pieces, 37 MiB read from disk), left to the rewarm check, 12 MiB in sparse files not touched, since the ladder 0 min ago: 0 MiB repurposed of 7900 MiB ahead", _log.Lines.ElementAt(5)); // 표식이 있던 게임의 빠짐 줄에는 그 뒤의 값이 붙음 (아래 사다리 줄은 표식이 이미 지워져 없음)
        Assert.Equal("Info: a: ladder found data missing from memory after 6 of 30 MiB (3 slow pieces, 37 MiB read from disk), left to the rewarm check, 12 MiB in sparse files not touched", _log.Lines.ElementAt(6));
    }

    [Fact]
    public void ALadderWhoseRangesAreAllInSparseFilesTouchedNothingSoItLeavesNoMarkNoMissingFlagAndLogsOnce()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        solar.RestoreMissing("b");
        _result = new TouchResult(0, 0.0, 0, 0, TouchEnd.Completed, 0, 30 * Mib);

        Assert.Equal(TouchEnd.Completed, solar.Order(Game("b"), Reads, CancellationToken.None));
        Assert.Equal(TouchEnd.Completed, solar.Order(Game("b"), Reads, CancellationToken.None));
        Assert.Equal(TouchEnd.Completed, solar.Refresh(Game("a"), CancellationToken.None));

        Assert.False(solar.HasMark("b"));
        Assert.False(solar.HasMissing("b")); // 이미 서 있던 빠짐 표시도 지움
        Assert.True(solar.HasNothingToTouch("b"));
        Assert.Single(_log.Lines, l => l == "Info: b: ladder has nothing to touch (30 MiB in sparse files not touched)"); // 두 번 판정해도 줄은 한 번
        Assert.False(solar.HasMark("a")); // 만진 것이 없는 새로 고침은 표식을 지워 틱마다 되풀이하지 않음
        Assert.False(solar.HasMissing("a"));
        Assert.EndsWith("a: cache refreshed in memory: 0 MiB in 0.0 s (0 MiB repurposed of 7900 MiB ahead), 30 MiB in sparse files not touched", _log.Lines.Last());
    }

    [Fact]
    public void TheNothingToTouchMemoryEndsWhenALadderTouchesSomethingFindsDataMissingIsForgottenOrThePruneDropsTheGame()
    {
        Solar solar = Make();
        TouchResult allSparse = new(0, 0.0, 0, 0, TouchEnd.Completed, 0, 30 * Mib);
        string[] games = ["a", "b", "c", "d"];
        _result = allSparse;
        foreach (string g in games)
        {
            _ = solar.Order(Game(g), Reads, CancellationToken.None);
        }

        _result = new TouchResult(100 * Mib, 0.4, 0, 0, TouchEnd.Completed);
        _ = solar.Order(Game("a"), Reads, CancellationToken.None); // 이번에는 만짐
        _result = new TouchResult(12 * Mib, 0.6, 3, 0, TouchEnd.NeedsDiskReads, 37 * Mib);
        _ = solar.Order(Game("b"), Reads, CancellationToken.None); // 디스크 읽기를 만남
        solar.Forget("c");
        solar.Prune(new HashSet<string>(["a", "b", "c"], StringComparer.OrdinalIgnoreCase));

        Assert.All(games, g => Assert.False(solar.HasNothingToTouch(g)));
    }

    [Fact]
    public void RefreshTouchesTheStoredLadderAgainAndLogsTheReuseAgainstWhatWasAhead()
    {
        Solar solar = Make();
        _lists = new MemoryLists(0, 5000 * Mib, 8000 * Mib);
        _ = solar.Order(Game("a"), Reads, CancellationToken.None); // A = 8000 - 100 = 7900 MiB
        _lists = new MemoryLists(0, 8500 * Mib, 8000 * Mib);

        Assert.Equal(TouchEnd.Completed, solar.Refresh(Game("a"), CancellationToken.None));

        Assert.Equal(2, _touched.Count);
        Assert.Equal(_touched[0], _touched[1]); // 같은 사다리를 다시 만짐
        Assert.Contains("a: cache refreshed in memory: 100 MiB in 0.4 s (3500 MiB repurposed of 7900 MiB ahead)", _log.Lines.Last());
    }

    [Fact]
    public void RefreshWithoutAMarkOrWithTheSettingOffTouchesNothing()
    {
        Solar solar = Make();
        Assert.Null(solar.Refresh(Game("a"), CancellationToken.None));
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _enabled = false;

        Assert.Null(solar.Refresh(Game("a"), CancellationToken.None));
        Assert.Single(_touched);
    }

    [Fact]
    public void RefreshThatNeedsDiskReadsClearsTheMarkSoItIsNotRetriedUntilANewLadderExists()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _result = new TouchResult(10 * Mib, 0.6, 5, 1, TouchEnd.NeedsDiskReads, 32 * Mib);

        Assert.Equal(TouchEnd.NeedsDiskReads, solar.Refresh(Game("a"), CancellationToken.None));

        Assert.False(solar.HasMark("a"));
        Assert.Contains("a: cache refresh found data missing from memory after 10 of 30 MiB (5 slow pieces, 32 MiB read from disk, 1 errors), left to the rewarm check", _log.Lines.Last());
        Assert.Empty(solar.DueGroup([Game("a")], _now + TimeSpan.FromHours(1)));
    }

    [Fact]
    public void ACanceledRefreshKeepsTheMark()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _result = _result with { End = TouchEnd.Canceled };

        _ = solar.Refresh(Game("a"), CancellationToken.None);

        Assert.True(solar.HasMark("a"));
    }

    [Fact]
    public void DueGroupListsEveryMarkedGameInTheInputOrderWhenOneOfThemIsDueAndReadsTheListsOnceOnlyWhenSomethingIsMarked()
    {
        Solar solar = Make();
        Assert.Empty(solar.DueGroup([Game("a")], _now));
        Assert.Equal(0, _listQueries);

        _lists = new MemoryLists(0, 0, 1000 * Mib);
        _ = solar.Order(Game("a"), Reads, CancellationToken.None); // A 는 작아 증가분 하한 512 MiB 가 기준
        _lists = new MemoryLists(0, 0, 20000 * Mib);
        _ = solar.Order(Game("b"), Reads, CancellationToken.None);
        _ = solar.Order(Game("c"), Reads, CancellationToken.None); // A = 19900 MiB: 증가분 600 MiB 로는 때가 아님
        _lists = new MemoryLists(0, 100 * Mib, 20000 * Mib);
        Assert.Empty(solar.DueGroup([Game("c"), Game("a"), Game("b")], _now)); // 아무도 때가 아님
        _lists = new MemoryLists(0, 600 * Mib, 20000 * Mib); // a 만 때가 됨
        int before = _listQueries;

        IReadOnlyList<GameSettings> group = solar.DueGroup([Game("c"), Game("x"), Game("a"), Game("b")], _now);

        Assert.Equal(["c", "a", "b"], group.Select(g => g.Name)); // 때가 된 a 만이 아니라 표식이 있는 모두, 입력 순서 그대로
        Assert.Equal(before + 1, _listQueries);
    }

    [Fact]
    public void ADueGameThatCannotBeTouchedNowDoesNotStartTheGroup()
    {
        Solar solar = Make();
        _lists = new MemoryLists(0, 0, 1000 * Mib);
        _ = solar.Order(Game("a"), Reads, CancellationToken.None); // A 가 작아 증가분 512 MiB 에 때가 됨
        _lists = new MemoryLists(0, 0, 20000 * Mib);
        _ = solar.Order(Game("b"), Reads, CancellationToken.None);
        _lists = new MemoryLists(0, 600 * Mib, 20000 * Mib);

        Assert.Empty(solar.DueGroup([Game("a"), Game("b")], _now, g => g.Name != "a")); // 때가 된 a 가 막혀 있음
        Assert.Equal(["a", "b"], solar.DueGroup([Game("a"), Game("b")], _now, g => g.Name != "b").Select(g => g.Name)); // 막힌 것은 때가 아닌 b: 시작하고 묶음은 전체
    }

    [Fact]
    public void AGroupIsNotDueAgainWithinSixtySecondsOfTheLastGroupRefresh()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        solar.Regroup([("a", 100 * Mib, null)]);
        _lists = new MemoryLists(0, 9000 * Mib, 8000 * Mib); // 때가 됨

        Assert.Empty(solar.DueGroup([Game("a")], _now + TimeSpan.FromSeconds(59)));
        Assert.Single(solar.DueGroup([Game("a")], _now + TimeSpan.FromSeconds(60)));
    }

    [Fact]
    public void RegroupWritesTheTouchedMarksFromOneInstantAndLowersTheOtherOnesByWhatWasTouched()
    {
        Solar solar = Make();
        foreach (string name in new[] { "a", "b", "c", "d" })
        {
            _ = solar.Order(Game(name), Reads, CancellationToken.None); // 표식 (A 는 아래에서 다시 쓰거나 줄어듦: d 는 이 시점에 7900 MiB)
        }

        _lists = new MemoryLists(0, 777 * Mib, 8000 * Mib);
        DateTimeOffset before = _now - TimeSpan.FromMinutes(1); // 이 게임들의 이전 표식은 d 보다 먼저 만들어졌음: 그 페이지는 d 앞에 있었음
        solar.Regroup([("a", 1000 * Mib, Previous(1000, before)), ("b", 2000 * Mib, Previous(2000, before)), ("c", 3000 * Mib, Previous(3000, before))]); // a 가 가장 먼저 (가장 오래됨) 만짐
        _lists = new MemoryLists(0, 877 * Mib, 8000 * Mib);
        foreach (string name in new[] { "a", "b", "c", "d" })
        {
            _ = solar.Refresh(Game(name), CancellationToken.None);
        }

        string[] lines = [.. _log.Lines.Where(l => l.Contains("cache refreshed in memory"))];
        Assert.Contains("a: cache refreshed in memory: 100 MiB in 0.4 s (100 MiB repurposed of 2000 MiB ahead)", lines[0]); // 8000 - (1000 + 2000 + 3000)
        Assert.Contains("b: cache refreshed in memory: 100 MiB in 0.4 s (100 MiB repurposed of 3000 MiB ahead)", lines[1]); // 8000 - (2000 + 3000)
        Assert.Contains("c: cache refreshed in memory: 100 MiB in 0.4 s (100 MiB repurposed of 5000 MiB ahead)", lines[2]); // 8000 - 3000
        Assert.Contains("d: cache refreshed in memory: 100 MiB in 0.4 s (877 MiB repurposed of 1900 MiB ahead)", lines[3]); // 만지지 않은 표식: 기준값은 그대로, A 에서 만진 합 6000 MiB 를 뺌
    }

    [Fact]
    public void WhatTheOthersTouchedNeverTakesWhatIsAheadBelowZero()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _ = solar.Order(Game("b"), Reads, CancellationToken.None);

        solar.Regroup([("b", 20000 * Mib, Previous(20000, _now - TimeSpan.FromMinutes(1)))]);
        _ = solar.Refresh(Game("a"), CancellationToken.None);

        Assert.Contains("a: cache refreshed in memory: 100 MiB in 0.4 s (0 MiB repurposed of 0 MiB ahead)", _log.Lines.Last());
    }

    [Theory]
    [InlineData(1000, 300, 300)] // min(이전 표식이 만진 1000, 이번에 만진 300)
    [InlineData(300, 1000, 300)] // min(300, 1000): 이번에 더 만진 것은 디스크에서 새로 읽은 것
    public void ALadderOfAGameThatHadAnEarlierMarkLowersWhatIsAheadOfTheOtherMarksByTheSmallerOfTheTwoAmounts(long earlierMib, long nowMib, long reducedMib)
    {
        Solar solar = Make();
        _result = _result with { Bytes = earlierMib * Mib };
        _ = solar.Order(Game("b"), Reads, CancellationToken.None); // b 의 이전 표식
        _now += TimeSpan.FromMinutes(1);
        _result = _result with { Bytes = 100 * Mib };
        _ = solar.Order(Game("a"), Reads, CancellationToken.None); // A = 7900 MiB, b 의 이전 표식보다 나중
        _now += TimeSpan.FromMinutes(1);
        _result = _result with { Bytes = nowMib * Mib };

        _ = solar.Order(Game("b"), Reads, CancellationToken.None);
        _result = _result with { Bytes = 100 * Mib };
        _ = solar.Refresh(Game("a"), CancellationToken.None);

        Assert.Contains($"(0 MiB repurposed of {7900 - reducedMib} MiB ahead)", _log.Lines.Last());
    }

    [Fact]
    public void AFirstLadderOrOneWhoseEarlierMarkIsLaterThanTheOthersLeavesWhatIsAheadAlone()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None); // A = 7900 MiB
        _now += TimeSpan.FromMinutes(1);
        _ = solar.Order(Game("b"), Reads, CancellationToken.None); // b 는 이전 표식이 없었음: 이미 캐시에 있던 페이지의 위치를 모르므로 줄이지 않음
        _now += TimeSpan.FromMinutes(1);
        _ = solar.Order(Game("b"), Reads, CancellationToken.None); // b 의 이전 표식은 a 의 표식보다 나중: 그 페이지는 원래 a 뒤였음

        _ = solar.Refresh(Game("a"), CancellationToken.None);

        Assert.Contains("(0 MiB repurposed of 7900 MiB ahead)", _log.Lines.Last());
    }

    // 묶음으로 새로 고친 a (먼저 만짐) 와 b: A 는 8000 - (1000 + 2000) = 5000 MiB 와 8000 - 2000 = 6000 MiB
    private Solar RegroupedPair()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _ = solar.Order(Game("b"), Reads, CancellationToken.None);
        solar.Regroup([("a", 1000 * Mib, null), ("b", 2000 * Mib, null)]);
        return solar;
    }

    [Fact]
    public void AfterAGroupRefreshReTouchingOnlyTheLaterGameLeavesTheEarlierOnesAheadAlone()
    {
        Solar solar = RegroupedPair();
        _result = _result with { Bytes = 300 * Mib };

        _ = solar.Order(Game("b"), Reads, CancellationToken.None); // b 의 이전 표식은 a 보다 나중: b 의 페이지는 원래 a 뒤였음
        _result = _result with { Bytes = 100 * Mib };
        _ = solar.Refresh(Game("a"), CancellationToken.None);

        Assert.Contains("(0 MiB repurposed of 5000 MiB ahead)", _log.Lines.Last());
    }

    [Fact]
    public void AfterAGroupRefreshReTouchingOnlyTheEarlierGameLowersTheLaterOnesAhead()
    {
        Solar solar = RegroupedPair();
        _result = _result with { Bytes = 300 * Mib };

        _ = solar.Order(Game("a"), Reads, CancellationToken.None); // a 의 이전 표식은 b 보다 먼저 (같은 묶음 안의 순서): a 의 페이지는 b 앞에 있었음
        _result = _result with { Bytes = 100 * Mib };
        _ = solar.Refresh(Game("b"), CancellationToken.None);

        Assert.Contains("(0 MiB repurposed of 5700 MiB ahead)", _log.Lines.Last()); // 6000 - min(이전 표식이 만진 1000, 이번 300)
    }

    [Fact]
    public void RefreshReportsTheBytesItTouchedOnlyWhenItFinishedTheWholeLadder()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _ = solar.Order(Game("b"), Reads, CancellationToken.None);
        long? got = null;

        _ = solar.Refresh(Game("a"), CancellationToken.None, (_, bytes) => got = bytes);
        Assert.Equal(100 * Mib, got);

        got = null;
        _result = new TouchResult(10 * Mib, 0.6, 5, 0, TouchEnd.NeedsDiskReads, 32 * Mib);
        _ = solar.Refresh(Game("b"), CancellationToken.None, (_, bytes) => got = bytes);
        Assert.Null(got);
    }

    [Fact]
    public void PruneDropsTheMarksOfGamesThatLeftTheSettings()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _ = solar.Order(Game("b"), Reads, CancellationToken.None);

        solar.Prune(new HashSet<string>(["b"], StringComparer.OrdinalIgnoreCase));

        Assert.False(solar.HasMark("a"));
        Assert.True(solar.HasMark("b"));
    }

    [Fact]
    public void TheSettingDefaultsToOnIsReadAndRejectsBadValues()
    {
        Assert.True(ParseMemory("{}").StandbyLadder);
        Assert.True(ParseMemory("""{"background_yield":false}""").StandbyLadder);
        Assert.False(ParseMemory("""{"standby_ladder":false}""").StandbyLadder);
        Assert.True(ParseMemory("""{"standby_ladder":false}""").BackgroundYield);
        Assert.Throws<SettingsException>(() => ParseMemory("""{"standby_ladder":"yes"}"""));
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

    // ---- 예열·유지 틱과의 연결 ----

    private sealed class PlannedStreamer : IGameStreamer
    {
        public List<(string Game, int MaxStreamMib)> Calls { get; } = [];

        public bool Complete { get; set; } = true;

        /// 핫 집합 크기 (MiB).
        public long ZoneMib { get; set; } = 30;

        public IReadOnlyList<ReadOp> All { get; set; } = SolarTests.Reads;

        public Action? DuringStream { get; set; }

        /// 참이면 스트림이 읽기 전에 예외로 실패함.
        public bool Fail { get; set; }

        /// 묶음 점검 (진행 알림) 을 직접 부르는 읽는 도중의 일 (메모리 한도 등).
        public Action<Action<long, long>>? OnProgress { get; set; }

        /// 실제로 읽었다고 알릴 양 (MiB), 없으면 받은 범위 전부.
        public long? ReadMib { get; set; }

        /// 묶음을 아는 스트리머처럼 묶음마다 진행 알림과 afterBatch 를 부를 읽기 묶음들 (비어 있으면 묶음 없이 한 번에 읽음).
        public List<ReadOp[]> Batches { get; } = [];

        /// 묶음의 진행 알림과 afterBatch 사이에서 (묶음 번호) 부르는 일: 점검이 스트림을 멈춘 것처럼 게임 시작을 알림.
        public Action<int>? BetweenProgressAndTouch { get; set; }

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) StreamPrewarmCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes, long skipBytes, Func<bool> canIssue, bool allowReorder,
            Action<string, long, bool>? noteIssue, Action<IReadOnlyList<ReadOp>>? afterBatch)
        {
            if (Batches.Count == 0)
            {
                return StreamPrewarm(game, stop, progress, maxBytes);
            }

            Calls.Add((game.Name, game.MaxStreamMib));
            long total = Batches.Sum(b => b.Sum(o => o.Length));
            long done = 0;
            progress?.Invoke(0, total);
            for (int i = 0; i < Batches.Count; i++)
            {
                if (stop.IsCancellationRequested)
                {
                    return (new StreamStats(done, i, 0, 0.01, Aborted: true), false);
                }

                done += Batches[i].Sum(o => o.Length);
                progress?.Invoke(done, total);
                BetweenProgressAndTouch?.Invoke(i);
                afterBatch?.Invoke(Batches[i]);
            }

            return (new StreamStats(done, Batches.Count, 0, 0.01), true);
        }

        public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes)
        {
            Calls.Add((game.Name, game.MaxStreamMib));
            if (Fail)
            {
                throw new IOException("read failed");
            }

            long total = Math.Min(game.MaxStreamMib == 0 ? ZoneMib : game.MaxStreamMib, ZoneMib) * Mib;
            progress?.Invoke(0, total);
            if (OnProgress is not null && progress is not null)
            {
                OnProgress(progress);
            }

            DuringStream?.Invoke();
            return (new StreamStats(ReadMib is { } read ? read * Mib : total, 1, 0, 0.01), Complete && !stop.IsCancellationRequested);
        }

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress) => StreamPrewarm(game, stop, progress, 0);

        /// 부분 예열 뒤 표본을 고르는 기준과 같게 MaxStreamMib 로 잘린 읽은 범위.
        public IReadOnlyList<ReadOp>? PlannedPrewarmReads(GameSettings game) => PrefetchStreamer.Head(All, game.MaxStreamSet ? (long)game.MaxStreamMib * Mib : long.MaxValue);
    }

    private readonly PlannedStreamer _streamer = new();
    private readonly HashSet<string> _running = [];
    private bool _diskBusy;
    private bool _diskUnknown; // 참이면 드라이브 처리량을 읽을 수 없음 (null)
    private long _avail = 20000;
    private ProbeResult? _probe = new ProbeResult(8, 0, 0.2); // warm (null: 표본을 읽을 수 없음)
    private readonly HashSet<string> _coldGames = []; // 이 게임들의 표본은 식음 (나머지는 _probe)
    private IReadOnlyList<ReadOp> _common = [];
    private int _probeReads; // 표본 읽기 호출 수
    private bool _resumeProbe; // 참이면 끊긴 보충이 이어 읽을 위치를 확인하는 표본 훅이 있음 (warm)
    private bool _commonProgress; // 참이면 공통 블록 읽기가 op 하나마다 진행을 알림 (실제 서비스의 묶음 진행처럼), 아니면 진행 없는 읽기

    private Prewarmer MakePrewarmer(params GameSettings[] games)
    {
        PrewarmEnv env = Env();
        return new Prewarmer(games, _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = g => _running.Contains(g.Name),
            ZoneBytes = g => Math.Min(_streamer.ZoneMib, g.StreamCapBytes(_streamer.ZoneMib * Mib) / Mib) * Mib,
            TraceExists = env.TraceExists,
            PhysicalRamBytes = () => 64L << 30,
            ProbeReadMs = g =>
            {
                _probeReads++;
                return _coldGames.Contains(g.Name) ? new ProbeResult(8, 8, 5.0) : _probe;
            },
            DiskBytesPerSecond = _ => _diskUnknown ? null : _diskBusy ? 1e9 : 0,
            CommonFirstOps = (_, _, _) => (_common, _common.Sum(o => o.Length)),
            ColdRead = (ops, _) => (new StreamStats(ops.Sum(o => o.Length), 1, 0, 0.01), true),
            ProbeDone = _resumeProbe ? (_, _) => _probe : null,
            ColdReadWithIssue = _commonProgress ? ReadCommonWithProgress : null,
            StandbyLadderEnabled = env.StandbyLadderEnabled,
            Lists = env.Lists,
            TouchRanges = env.TouchRanges,
        }, _log, null, () => _now);
    }

    // 실제 서비스의 묶음 읽기처럼 op 하나를 읽을 때마다 진행 (읽은 양 누계, 전체) 을 알림.
    private static (StreamStats Stats, bool Complete) ReadCommonWithProgress(IReadOnlyList<ReadOp> ops, CancellationToken ct, Action<long, long> progress, Action<string, long, bool> noteIssue)
    {
        long done = 0;
        long total = ops.Sum(o => o.Length);
        foreach (ReadOp op in ops)
        {
            done += op.Length;
            progress(done, total);
        }

        return (new StreamStats(done, ops.Count, 0, 0.01), true);
    }

    private static AutoSettings Auto() => AutoSettings.Default with { RewarmMinutes = 10 };

    private void GrowReuse(long mib) => _lists = new MemoryLists(0, mib * Mib, 8000 * Mib);

    private static ReadOp[] Reversed(IEnumerable<ReadOp> readOrder) => [.. readOrder.Reverse()];

    [Fact]
    public void AfterTheStreamFinishesTheWholeReadRangeIsTouchedInReverseWithTheCommonBlockLast()
    {
        _common = [new ReadOp(@"D:\a\c.bin", 0, 5 * Mib)];
        Prewarmer p = MakePrewarmer(Game("a"));

        Assert.True(p.Run(Game("a"), "service start").Ran);

        Assert.Equal([new ReadOp(@"D:\a\1.bin", 50 * Mib, 10 * Mib), new ReadOp(@"D:\a\2.bin", 0, 10 * Mib), new ReadOp(@"D:\a\1.bin", 0, 10 * Mib), _common[0]], _touched[1]);
        Assert.Equal(2, _touched.Count);
        Assert.Equal(_common, _touched[0]); // 공통 블록은 읽은 직후 한 번 다시 만짐
        Assert.True(p.Solar.HasMark("a"));
    }

    [Fact]
    public void ACanceledStreamIsNotTouchedAndASettingOffTouchesNothing()
    {
        _streamer.Complete = false;
        Prewarmer cancelled = MakePrewarmer(Game("a"));
        _ = cancelled.Run(Game("a"), "service start");
        _streamer.Complete = true;
        _enabled = false;
        Prewarmer off = MakePrewarmer(Game("b"));

        Assert.True(off.Run(Game("b"), "service start").Ran);

        Assert.Empty(_touched);
        Assert.False(cancelled.Solar.HasMark("a"));
    }

    [Fact]
    public void AGameThatStartsDuringTheStreamIsNotTouched()
    {
        Prewarmer? p = null;
        _streamer.DuringStream = () => p!.NoteGameStart("a");
        p = MakePrewarmer(Game("a"));

        _ = p.Run(Game("a"), "service start");

        Assert.Empty(_touched);
        Assert.False(p.Solar.HasMark("a"));
    }

    [Fact]
    public void AGameThatStartsDuringTheLadderStopsItAtOnceAndLeavesNoMark()
    {
        Prewarmer? p = null;
        _onTouch = () => p!.NoteGameStart("a"); // 만지는 도중 게임 시작: 스트림 중지 신호가 만지기의 토큰으로 이어짐
        p = MakePrewarmer(Game("a"));

        _ = p.Run(Game("a"), "service start");

        Assert.Single(_touched);
        Assert.False(p.Solar.HasMark("a"));
        Assert.Contains(_log.Lines, l => l.Contains("a: ladder stopped after"));
    }

    [Fact]
    public void KeepStreamOnGameStartSkipsTheLadderBecauseTheStreamLeaseIsNotCanceledByTheGame()
    {
        Prewarmer p = MakePrewarmer(Game("a"));
        p.SetAuto(AutoSettings.Default with { KeepStreamOnGameStart = true });

        Assert.True(p.Run(Game("a"), "service start").Ran);

        Assert.Empty(_touched);
    }

    private static ReadOp[][] TwoBatches() => [[Reads[0], Reads[1]], [Reads[2]]];

    [Fact]
    public void EveryBatchTheStreamReadsIsTouchedRightAfterInReadOrderThenTheLadderRunsOnceAndTheDoneLineShowsTheCost()
    {
        _streamer.Batches.AddRange(TwoBatches());
        Prewarmer p = MakePrewarmer(Game("a"));

        PrewarmResult result = p.Run(Game("a"), "service start");

        Assert.True(result.Ran);
        Assert.Equal(30 * Mib, result.Stats!.Bytes);
        Assert.Equal(3, _touched.Count);
        Assert.Equal([Reads[0], Reads[1]], _touched[0]); // 묶음 순서대로, 묶음 안은 읽은 순서
        Assert.Equal([Reads[2]], _touched[1]);
        Assert.Equal(Reversed(Reads), _touched[2]); // 끝의 사다리는 그대로 한 번
        Assert.True(p.Solar.HasMark("a"));
        Assert.Contains(_log.Lines, l => l.Contains("a: prewarm done (service start)") && l.EndsWith(", requeued 200 MiB in 0.8 s", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCommonBlockIsTouchedRightAfterItIsReadBeforeTheStreamBatches()
    {
        _common = [new ReadOp(@"D:\c.bin", 0, 5 * Mib)];
        _streamer.Batches.AddRange(TwoBatches());
        Prewarmer p = MakePrewarmer(Game("a"));

        Assert.True(p.Run(Game("a"), "service start").Ran);

        Assert.Equal(4, _touched.Count);
        Assert.Equal(_common, _touched[0]);
        Assert.Equal([Reads[0], Reads[1]], _touched[1]);
    }

    [Fact]
    public void WithTheSettingOffOrKeepStreamOnGameStartNoBatchIsTouchedAndTheStreamResultIsTheSame()
    {
        _streamer.Batches.AddRange(TwoBatches());
        Prewarmer on = MakePrewarmer(Game("a"));
        PrewarmResult expected = on.Run(Game("a"), "service start");
        _touched.Clear();
        _enabled = false;
        Prewarmer off = MakePrewarmer(Game("b"));

        PrewarmResult disabled = off.Run(Game("b"), "service start");
        _enabled = true;
        Prewarmer keep = MakePrewarmer(Game("c"));
        keep.SetAuto(AutoSettings.Default with { KeepStreamOnGameStart = true });
        PrewarmResult kept = keep.Run(Game("c"), "service start");

        Assert.Empty(_touched);
        Assert.All([disabled, kept], r => Assert.Equal((expected.Ran, expected.Stats!.Bytes, expected.Stats.Requests), (r.Ran, r.Stats!.Bytes, r.Stats.Requests)));
        Assert.Contains(_log.Lines, l => l.StartsWith("Info: b: prewarm done", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.StartsWith("Info: c: prewarm done", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => (l.StartsWith("Info: b:", StringComparison.Ordinal) || l.StartsWith("Info: c:", StringComparison.Ordinal)) && l.Contains("requeued", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDoneLineShowsTheSparseBytesTheBatchTouchesSkippedAndAllSparseBatchesLeaveNoMark()
    {
        _streamer.Batches.AddRange(TwoBatches());
        _result = _result with { SkippedBytes = 20 * Mib };
        Prewarmer mixed = MakePrewarmer(Game("a"));

        Assert.True(mixed.Run(Game("a"), "service start").Ran);

        Assert.Contains(_log.Lines, l => l.Contains("a: prewarm done (service start)") && l.EndsWith(", requeued 200 MiB in 0.8 s (40 MiB in sparse files not touched)", StringComparison.Ordinal));

        _streamer.Batches.Clear();
        _streamer.Batches.AddRange(TwoBatches());
        _result = new TouchResult(0, 0.0, 0, 0, TouchEnd.Completed, 0, 50 * Mib);
        Prewarmer allSparse = MakePrewarmer(Game("b"));

        Assert.True(allSparse.Run(Game("b"), "service start").Ran);

        Assert.Contains(_log.Lines, l => l.Contains("b: prewarm done (service start)") && l.EndsWith(", requeued 0 MiB in 0.0 s (100 MiB in sparse files not touched)", StringComparison.Ordinal));
        Assert.False(allSparse.Solar.HasMark("b"));
        Assert.False(allSparse.Solar.HasMissing("b"));
    }

    [Theory]
    [InlineData(TouchEnd.NeedsDiskReads, 2, ", requeued 10 MiB in 0.2 s, requeue stopped by disk reads")] // 첫 묶음이 디스크 읽기로 멈추면 남은 묶음은 만지지 않음 (사다리는 그대로)
    [InlineData(TouchEnd.Canceled, 3, ", requeued 20 MiB in 0.4 s")] // 취소로 끝난 만지기는 디스크 읽기가 아니므로 다음 묶음도 만짐
    public void ARequeueThatStopsOnDiskReadsStopsTheRestOfTheBatchesAndNeitherItNorACancelChangesWhatTheStreamReads(TouchEnd end, int touches, string tail)
    {
        _streamer.Batches.AddRange(TwoBatches());
        _result = new TouchResult(10 * Mib, 0.2, 2, 0, end, end == TouchEnd.NeedsDiskReads ? 40 * Mib : 0);
        Prewarmer p = MakePrewarmer(Game("a"));

        PrewarmResult result = p.Run(Game("a"), "service start");

        Assert.True(result.Ran);
        Assert.Equal(30 * Mib, result.Stats!.Bytes);
        Assert.Equal(touches, _touched.Count);
        Assert.Equal(Reversed(Reads), _touched[^1]); // 사다리는 그대로 한 번
        Assert.Contains(_log.Lines, l => l.Contains("a: prewarm done (service start)") && l.EndsWith(tail, StringComparison.Ordinal));
    }

    [Fact]
    public void TheCommonBlockIsTouchedPerReadBatchByTheAmountReadSoFarWhenTheReadReportsProgress()
    {
        _commonProgress = true;
        _common = [new ReadOp(@"D:\a\c1.bin", 0, 5 * Mib), new ReadOp(@"D:\a\c2.bin", 0, 5 * Mib), new ReadOp(@"D:\a\c3.bin", 0, 5 * Mib)];
        Prewarmer p = MakePrewarmer(Game("a"));

        Assert.True(p.Run(Game("a"), "service start").Ran);

        Assert.Equal(4, _touched.Count); // 공통 블록 묶음 3개 + 사다리
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal([_common[i]], _touched[i]); // 읽은 양으로 자른 새로 읽은 만큼만, 읽은 순서대로
        }
    }

    // 이어 읽기 (끊긴 보충이 읽은 앞부분 1000 MiB 를 건너뜀) 를 시작할 수 있는 상태.
    private Prewarmer ResumableSupplement(ReadOp[] all)
    {
        _resumeProbe = true;
        Prewarmer p = ShortFinishedAndWaiting(all, waitForMemory: false);
        _touched.Clear();
        return p;
    }

    [Fact]
    public void AResumedSupplementTouchesTheSkippedFrontBeforeReadingTheTailAndTheDoneLineShowsIt()
    {
        ReadOp[] all = HotSet(50);
        Prewarmer p = ResumableSupplement(all);
        int logBefore = _log.Lines.Count;
        _streamer.Batches.Add([.. all.Skip(10).Take(20)]); // 꼬리를 읽는 묶음
        _streamer.Batches.Add([.. all.Skip(30)]);
        int touchesWhenTheTailIsRead = -1;
        _streamer.BetweenProgressAndTouch = i =>
        {
            if (i == 0)
            {
                touchesWhenTheTailIsRead = _touched.Count;
            }
        };

        Assert.True(p.SupplementWhenIdle(Game("a"), CancellationToken.None));

        Assert.Equal(1, touchesWhenTheTailIsRead); // 첫 꼬리 묶음을 읽을 때 건너뛴 앞부분은 이미 만짐
        Assert.Equal([.. all.Take(10)], _touched[0]); // 건너뛴 앞 1000 MiB, 목록 순서
        Assert.Equal([.. all.Skip(10).Take(20)], _touched[1]);
        Assert.Equal([.. all.Skip(30)], _touched[2]);
        Assert.Equal(4, _touched.Count); // 앞부분 + 묶음 2개 + 사다리
        Assert.Contains(_log.Lines.Skip(logBefore), l => l.Contains("a: supplement prewarm read the first") && l.EndsWith("errors), requeued 300 MiB in 1.2 s, the partial window remains pending", StringComparison.Ordinal)); // 앞부분 + 꼬리 묶음 2개
    }

    [Fact]
    public void ASkippedFrontTouchThatStopsOnDiskReadsEndsOnlyItselfAndTheTailStillRunsAndIsTouched()
    {
        ReadOp[] all = HotSet(50);
        Prewarmer p = ResumableSupplement(all);
        int logBefore = _log.Lines.Count;
        _streamer.Batches.Add([.. all.Skip(10)]);
        int touch = 0;
        _onTouch = () =>
        {
            touch++;
            _result = _result with { End = touch == 1 ? TouchEnd.NeedsDiskReads : TouchEnd.Completed, DiskBytes = touch == 1 ? 40 * Mib : 0 };
        };

        Assert.True(p.SupplementWhenIdle(Game("a"), CancellationToken.None));

        Assert.Equal([.. all.Take(10)], _touched[0]); // 앞부분 만지기가 디스크 읽기로 멈춤
        Assert.Equal([.. all.Skip(10)], _touched[1]); // 그래도 꼬리 묶음은 만짐
        Assert.Contains(_log.Lines.Skip(logBefore), l => l.Contains("a: supplement prewarm read the first") && l.Contains(", requeued 200 MiB in 0.8 s, requeue stopped by disk reads, the partial window", StringComparison.Ordinal)); // 앞부분 만지기만 멈춤: 꼬리 묶음은 만짐 (앞부분 100 MiB + 꼬리 묶음 100 MiB)
    }

    [Fact]
    public void TheSupplementDoneLineShowsWhatWasRequeuedWhileReading()
    {
        ReadOp[] all = HotSet(50);
        (_streamer.All, _streamer.ZoneMib) = (all, 5000);
        _common = [all[0]];
        _avail = 7000; // 앞부분만 읽음
        Prewarmer p = MakePrewarmer(Game("a"));
        Assert.True(p.Run(Game("a"), "service start").Ran);
        _streamer.Batches.Add([.. all.Take(25)]);
        _streamer.Batches.Add([.. all.Skip(25)]);
        _touched.Clear();
        _avail = 20000;
        Assert.False(p.SupplementWhenIdle(Game("a"), CancellationToken.None));
        _now += TimeSpan.FromSeconds(61);

        Assert.True(p.SupplementWhenIdle(Game("a"), CancellationToken.None));

        Assert.False(p.SupplementPending("a"));
        Assert.Contains(_log.Lines, l => l.Contains("a: supplement prewarm done") && l.EndsWith(", requeued 300 MiB in 1.2 s", StringComparison.Ordinal)); // 공통 블록 + 묶음 2개
    }

    [Fact]
    public void ASkippedFrontIsNotTouchedWhenTheSettingIsOff()
    {
        ReadOp[] all = HotSet(50);
        Prewarmer p = ResumableSupplement(all);
        _enabled = false;
        int callsBefore = _streamer.Calls.Count;

        Assert.True(p.SupplementWhenIdle(Game("a"), CancellationToken.None));

        Assert.Equal(callsBefore + 1, _streamer.Calls.Count); // 이어 읽기는 했고
        Assert.Empty(_touched); // 만지기는 없음
    }

    [Fact]
    public void ARequeueThatThrowsWarnsOnceAndTheStreamStillSucceeds()
    {
        _streamer.Batches.AddRange(TwoBatches());
        _onTouch = () => throw new IOException("view failed");
        Prewarmer p = MakePrewarmer(Game("a"));

        PrewarmResult result = p.Run(Game("a"), "service start");

        Assert.True(result.Ran);
        Assert.Equal(30 * Mib, result.Stats!.Bytes);
        Assert.Single(_log.Lines, l => l.Contains("a: requeue of the read batches skipped: IOException", StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("requeued", StringComparison.Ordinal));
    }

    [Fact]
    public void WhenTheStreamIsStoppedAfterABatchTheNextBatchIsNotTouched()
    {
        Prewarmer? p = null;
        _streamer.Batches.AddRange(TwoBatches());
        _streamer.BetweenProgressAndTouch = i =>
        {
            if (i == 1)
            {
                p!.NoteGameStart("a"); // 두 번째 묶음의 점검이 게임 시작으로 스트림을 멈춤
            }
        };
        p = MakePrewarmer(Game("a"));

        PrewarmResult result = p.Run(Game("a"), "service start");

        Assert.False(result.Ran);
        Assert.Single(_touched); // 첫 묶음만, 두 번째와 사다리는 없음
        Assert.Equal([Reads[0], Reads[1]], _touched[0]);
        Assert.False(p.Solar.HasMark("a"));
    }

    [Fact]
    public void TheTickRefreshesOnlyWhenTheReuseReachesTheThreshold()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start"); // A = 8000 - 100 MiB (만진 양은 가짜 결과의 100 MiB)
        _touched.Clear();
        _now += TimeSpan.FromMinutes(5); // 방금 (1 분 안) 예열한 게임은 새로 고치지 않음

        GrowReuse(1000 + 3000); // 0.4 x 7900 = 3160 MiB 미만
        p.Tick(Auto());
        Assert.Empty(_touched);

        GrowReuse(1000 + 3200);
        p.Tick(Auto());
        Assert.Single(_touched);
        Assert.Contains("a: cache refreshed in memory", _log.Lines.Last());

        p.Tick(Auto()); // 새 표식 기준으로 다시 세므로 바로 또 하지는 않음
        Assert.Single(_touched);
    }

    [Fact]
    public void TheTickDoesNotTouchWhileTheGameRunsOrTheTokenIsCanceled()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);

        _ = _running.Add("a");
        p.Tick(Auto());
        _ = _running.Remove("a");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        p.Tick(Auto(), cts.Token);

        Assert.Empty(_touched);
        p.Tick(Auto());
        Assert.Single(_touched);
    }

    [Fact]
    public void TheTickDoesNotTouchWhileATriggerPrewarmWaitsOrReads()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"), Game("b"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);

        using (p.BeginTriggerWait([Game("b")])) // 트리거가 기다리거나 읽는 동안의 표시
        {
            p.Tick(Auto());
            Assert.Empty(_touched);
        }

        p.Tick(Auto());
        Assert.Single(_touched);
    }

    [Fact]
    public void AStreamStoppedByATriggerPreemptionIsNotLaddered()
    {
        Prewarmer? p = null;
        MethodInfo preempt = typeof(Prewarmer).GetMethod("PreemptForTrigger", BindingFlags.NonPublic | BindingFlags.Instance)!;
        _streamer.DuringStream = () => preempt.Invoke(p, [new GameSettings[] { Game("b") }]); // 트리거 경로가 부르는 정지 신호
        p = MakePrewarmer(Game("a"), Game("b"));

        PrewarmResult result = p.Run(Game("a"), "service start");

        Assert.False(result.Ran);
        Assert.Equal(Prewarmer.PreemptedByTrigger, result.CancelDetail);
        Assert.Empty(_touched);
        Assert.False(p.Solar.HasMark("a"));
    }

    [Fact]
    public void ATriggerThatStartedWaitingJustBeforeTheStreamEndedSkipsTheLadderToo()
    {
        Prewarmer? p = null;
        IDisposable? wait = null;
        _streamer.DuringStream = () => wait = p!.BeginTriggerWait([Game("b")]); // 정지 신호를 놓친 경우: 스트림은 끝까지 읽고 사다리 시작 때 표시를 봄
        p = MakePrewarmer(Game("a"), Game("b"));

        _ = p.Run(Game("a"), "service start");
        wait?.Dispose();

        Assert.Empty(_touched);
    }

    [Fact]
    public void TheTickDoesNotTouchWhileAnotherGameRunsOnTheSameDisk()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"), Game("b")); // 둘 다 D:
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);

        _ = _running.Add("b"); // 같은 디스크의 다른 게임 (읽기 카운터를 못 읽으므로 한가하다고 보지 않음)
        p.Tick(Auto());

        Assert.Empty(_touched);
    }

    [Fact]
    public void RefreshWaitsForTheRewarmCheckSamplesAndLeavesColdOnesToIt()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        _streamer.Calls.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);
        _probe = new ProbeResult(8, 8, 5.0); // 이미 식음

        p.Tick(Auto());

        Assert.Empty(_touched); // 식은 캐시는 만지지 않음
        Assert.False(p.Solar.HasMark("a"));
        Assert.Empty(_streamer.Calls); // 재예열은 부르지 않음
        Assert.Contains(_log.Lines, l => l.Contains("a: cache refresh skipped, the samples are cold") && l.EndsWith("left to the rewarm check, since the ladder 5 min ago: 19000 MiB repurposed of 7900 MiB ahead", StringComparison.Ordinal)); // 표식이 있었으므로 그 뒤의 값이 붙음
    }

    [Fact]
    public void RefreshThatCannotReadItsSamplesLeavesTheGameToTheRewarmCheckWithoutRepeatingItself()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);
        _probe = null; // 표본을 읽을 수 없음

        p.Tick(Auto());
        _now += TimeSpan.FromSeconds(30);
        GrowReuse(30000);
        p.Tick(Auto());

        Assert.Empty(_touched);
        Assert.False(p.Solar.HasMark("a"));
        Assert.Single(_log.Lines, l => l.Contains("a: cache refresh skipped, there are no readable samples, left to the rewarm check")); // 표식을 지웠으니 30 초마다 되풀이하지 않음
    }

    [Fact]
    public void WhenTheRefreshNeedsDiskReadsNoRewarmIsCalledAndTheLadderReturnsOnlyAfterTheRewarmCheckFindsItWarm()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        _streamer.Calls.Clear();
        GrowReuse(20000);
        _result = _result with { Bytes = 10 * Mib, SlowPieces = 4, End = TouchEnd.NeedsDiskReads, DiskBytes = 40 * Mib };
        _now += TimeSpan.FromMinutes(5);
        AutoSettings auto = Auto();

        p.Tick(auto); // 새로 고침이 디스크 읽기를 요구 -> 표식만 지우고 재예열은 부르지 않음
        Assert.Single(_touched);
        Assert.False(p.Solar.HasMark("a"));
        Assert.Contains(_log.Lines, l => l.EndsWith("a: cache refresh found data missing from memory after 10 of 30 MiB (4 slow pieces, 40 MiB read from disk), left to the rewarm check, since the ladder 5 min ago: 19000 MiB repurposed of 7900 MiB ahead", StringComparison.Ordinal));

        _result = _result with { SlowPieces = 0, DiskBytes = 0, End = TouchEnd.Completed };
        for (int i = 0; i < 3; i++)
        {
            _now += TimeSpan.FromMinutes(2);
            GrowReuse(20000 + (i + 1) * 5000L);
            p.Tick(auto); // 표식이 없으니 재사용이 늘어도 다시 시도하지 않음
        }

        Assert.Single(_touched);
        Assert.Empty(_streamer.Calls);

        _now += TimeSpan.FromMinutes(6); // 첫 틱 뒤 10 분이 지나 재예열 점검 차례: 표본이 warm 이어도 다시 채움 표시 때문에 재예열
        p.Tick(auto);

        Assert.Single(_streamer.Calls);
        Assert.Equal(2, _touched.Count); // 재예열 직후의 사다리
        Assert.True(p.Solar.HasMark("a"));
        Assert.False(p.Solar.HasMissing("a"));
        Assert.Contains(_log.Lines, l => l == "Info: a: rewarm check: part of the list is not in memory (found by the ladder), prewarming again (cooldown ignored)");
    }

    // 예열 직후 사다리가 디스크 읽기를 만난 상태 (표식 없음, 다시 채움 표시 있음) 의 게임 하나, 첫 틱까지 지난 상태.
    private Prewarmer FlaggedGame(out AutoSettings auto)
    {
        _result = _result with { SlowPieces = 3, End = TouchEnd.NeedsDiskReads, DiskBytes = 32 * Mib };
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        Assert.True(p.Solar.HasMissing("a"));
        Assert.False(p.Solar.HasMark("a"));
        _touched.Clear();
        _streamer.Calls.Clear();
        auto = Auto();
        p.Tick(auto); // 첫 틱은 다음 점검 시각만 정함
        return p;
    }

    [Fact]
    public void ARewarmThatStillFindsDataMissingIsRepeatedOnlyOncePerCheckPeriod()
    {
        Prewarmer p = FlaggedGame(out AutoSettings auto);

        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        Assert.Single(_streamer.Calls); // 표본은 warm 인데도 한 번 다시 읽음
        Assert.True(p.Solar.HasMissing("a")); // 재예열 뒤 사다리가 또 디스크 읽기를 만나 다시 섬

        _now += TimeSpan.FromMinutes(2);
        p.Tick(auto);
        Assert.Single(_streamer.Calls); // 주기 안에서는 되풀이하지 않음

        _now += TimeSpan.FromMinutes(9);
        p.Tick(auto);
        Assert.Equal(2, _streamer.Calls.Count);
    }

    [Fact]
    public void AfterASuccessfulRefillTheLadderMarkStandsAndLaterChecksDoNotPrewarmAgain()
    {
        Prewarmer p = FlaggedGame(out AutoSettings auto);
        _result = _result with { SlowPieces = 0, DiskBytes = 0, End = TouchEnd.Completed };

        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        Assert.Single(_streamer.Calls);
        Assert.True(p.Solar.HasMark("a"));

        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void AChecksPulledInByAGameExitDoNotRefillBeforeRewarmMinutesHavePassedSinceTheLastPrewarm()
    {
        Prewarmer p = FlaggedGame(out AutoSettings auto);
        p.SetAuto(auto);
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        Assert.Single(_streamer.Calls);
        Assert.True(p.Solar.HasMissing("a")); // 재예열 뒤 사다리가 또 디스크 읽기를 만나 표시가 다시 섬

        _now += TimeSpan.FromMinutes(1);
        p.NoteGameExit(); // 다른 게임이 끝남: 점검을 2 분 뒤로 당김
        _now += TimeSpan.FromMinutes(2.5);
        p.Tick(auto);

        Assert.Single(_streamer.Calls); // 마지막 예열 뒤 10 분 전이라 다시 읽지 않고 표시를 둠
        Assert.True(p.Solar.HasMissing("a"));
        Assert.Contains(_log.Lines, l => l.Contains("a: rewarm check: part of the list is not in memory (found by the ladder), waiting until 10 min after the last prewarm"));

        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);

        Assert.Equal(2, _streamer.Calls.Count);
    }

    [Fact]
    public void AFailedRewarmIsNotRetriedByPulledInChecksBeforeRewarmMinutesHavePassed()
    {
        Prewarmer p = FlaggedGame(out AutoSettings auto);
        p.SetAuto(auto);
        _streamer.Fail = true; // 스트림이 돌지 않고 예외: 마지막 성공 예열 시각은 그대로
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        Assert.Single(_streamer.Calls);
        Assert.True(p.Solar.HasMissing("a"));

        _now += TimeSpan.FromMinutes(1);
        p.NoteGameExit(); // 점검을 2 분 뒤로 당김
        _now += TimeSpan.FromMinutes(2.5);
        p.Tick(auto);
        Assert.Single(_streamer.Calls); // 마지막 시도 뒤 10 분 전이라 다시 시도하지 않음
        Assert.True(p.Solar.HasMissing("a"));

        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        Assert.Equal(2, _streamer.Calls.Count);
    }

    [Fact]
    public void ARewarmThatIsSkippedBeforeReadingKeepsTheFlagForTheNextCheck()
    {
        Prewarmer p = FlaggedGame(out AutoSettings auto);
        _avail = 100; // 메모리가 모자라 읽기 전에 건너뜀
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);

        Assert.Empty(_streamer.Calls);
        Assert.True(p.Solar.HasMissing("a"));

        _avail = 20000;
        _result = _result with { SlowPieces = 0, DiskBytes = 0, End = TouchEnd.Completed };
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);

        Assert.Single(_streamer.Calls);
        Assert.False(p.Solar.HasMissing("a"));
        Assert.True(p.Solar.HasMark("a"));
    }

    // 목록 전체가 스파스 파일이라 만질 것이 없었던 게임 하나, 첫 틱까지 지난 상태.
    private Prewarmer AllSparseGame(out AutoSettings auto)
    {
        _result = new TouchResult(0, 0.0, 0, 0, TouchEnd.Completed, 0, 100 * Mib);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        Assert.True(p.Solar.HasNothingToTouch("a"));
        Assert.False(p.Solar.HasMark("a"));
        _touched.Clear();
        _streamer.Calls.Clear();
        auto = Auto();
        p.Tick(auto);
        return p;
    }

    [Fact]
    public void RewarmChecksDoNotTryTheLadderAgainForAGameWhoseListIsAllSparseButANewStreamJudgesItAgain()
    {
        Prewarmer p = AllSparseGame(out AutoSettings auto);
        int logBefore = _log.Lines.Count;

        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);

        Assert.Empty(_touched); // 점검이 둘 돌아도 만지기 호출이 없고
        Assert.Empty(_streamer.Calls);
        Assert.DoesNotContain(_log.Lines.Skip(logBefore), l => l.Contains("ladder", StringComparison.Ordinal)); // 사다리 줄도 없음
        Assert.Single(_log.Lines, l => l.Contains("ladder has nothing to touch", StringComparison.Ordinal)); // 처음 한 줄뿐

        _result = new TouchResult(100 * Mib, 0.4, 0, 0, TouchEnd.Completed); // 파일 속성이 바뀌었다면
        Assert.True(p.Run(Game("a"), "service start", ignoreCooldown: true).Ran);

        Assert.NotEmpty(_touched); // 새 예열 스트림 뒤의 사다리는 다시 판정함
        Assert.True(p.Solar.HasMark("a"));
        Assert.False(p.Solar.HasNothingToTouch("a"));
    }

    [Fact]
    public void WhileTheFlagStandsTheLadderTickDoesNotTouchTheGame()
    {
        Prewarmer p = FlaggedGame(out AutoSettings auto);
        GrowReuse(30000);
        _now += TimeSpan.FromMinutes(5);

        p.Tick(auto);

        Assert.Empty(_touched);
        Assert.Empty(_streamer.Calls);
        Assert.True(p.Solar.HasMissing("a"));
    }

    [Fact]
    public void TheFlagIsKeptWhileTheRewarmCheckConditionsBlockOrTheCheckIsOff()
    {
        Prewarmer p = FlaggedGame(out AutoSettings auto);

        _ = _running.Add("a"); // 게임 실행 중
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        _ = _running.Remove("a");
        _diskBusy = true; // 디스크가 바쁨
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto);
        _diskBusy = false;
        using (p.BeginTriggerWait([Game("b")])) // 트리거 대기 중
        {
            _now += TimeSpan.FromMinutes(10.5);
            p.Tick(auto);
        }

        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto with { RewarmMinutes = 0 }); // 점검 끔
        p.Tick(auto with { Enabled = false }); // 자동 모드 끔

        Assert.Empty(_streamer.Calls);
        Assert.True(p.Solar.HasMissing("a"));

        p.Tick(auto); // 끈 동안 다음 점검 시각이 지워져 다시 정함
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto); // 조건이 풀리면 한 번 채움
        Assert.Single(_streamer.Calls);
    }

    [Fact]
    public void ARecentlyPrewarmedGameIsLeftAloneByTheRefreshTick()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromSeconds(30); // 1 분 안

        p.Tick(Auto());

        Assert.Empty(_touched);
    }

    [Fact]
    public void WithSeveralGamesTheMostRecentlyPrewarmedOneIsTouchedLast()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"), Game("b"), Game("c"));
        foreach (string name in new[] { "b", "a", "c" })
        {
            _now += TimeSpan.FromMinutes(1);
            Assert.True(p.Run(Game(name), "service start").Ran);
        }

        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);
        int logged = _log.Lines.Count; // 예열 스트림 앞의 보호가 남긴 줄은 뺌

        p.Tick(Auto());

        string[] refreshed = _log.Lines.Skip(logged).Where(l => l.Contains("cache refreshed in memory")).Select(l => l.Split(':')[1].Trim()).ToArray();
        Assert.Equal(["b", "a", "c"], refreshed); // 가장 오래 전에 예열한 b 부터, 가장 최근 c 가 마지막
    }

    [Fact]
    public void AGameWithoutAMarkGetsItsFirstLadderWhenTheRewarmCheckFindsItWarm()
    {
        GrowReuse(1000);
        _enabled = false; // 서비스를 다시 시작한 뒤처럼 예열 직후 사다리가 없었음
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _enabled = true;
        Assert.Empty(_touched);
        Assert.False(p.Solar.HasMark("a"));

        AutoSettings auto = Auto();
        p.Tick(auto); // 첫 틱은 다음 점검 시각만 정함
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto); // 점검: 표본이 warm -> 첫 사다리

        Assert.Equal(3, _touched.Single().Count);
        Assert.True(p.Solar.HasMark("a"));
    }

    private static ReadOp[] HotSet(int files) => [.. Enumerable.Range(0, files).Select(i => new ReadOp($@"D:\a\f{i}.bin", 0, 100 * Mib))];

    [Fact]
    public void AfterAPartialPrewarmIsSuppliedTheLadderCoversTheWholeHotSetWithTheCommonBlockLast()
    {
        ReadOp[] all = HotSet(50);
        _streamer.All = all;
        _streamer.ZoneMib = 5000;
        _common = [all[0]];
        _avail = 7000; // 핫 집합 5000 MiB + 예비 4096 MiB 가 다 들어가지 않아 앞부분만 읽음
        Prewarmer p = MakePrewarmer(Game("a"));
        Assert.True(p.Run(Game("a"), "service start").Ran);
        Assert.True(p.SupplementPending("a"));

        Assert.Equal(2, _touched.Count); // 공통 블록의 다시 만지기 한 번, 그리고 사다리 한 번
        Assert.Equal(_common, _touched[0]);
        ReadOp[] partial = [.. _touched[1]];
        Assert.DoesNotContain(all[49], partial); // 읽은 앞부분까지만
        Assert.Equal(_common[0], partial[^1]);
        Assert.True(partial.Length < 51);
        _touched.Clear();

        _avail = 20000;
        Assert.False(p.SupplementWhenIdle(Game("a"), CancellationToken.None));
        _now += TimeSpan.FromSeconds(61);
        Assert.True(p.SupplementWhenIdle(Game("a"), CancellationToken.None));

        Assert.False(p.SupplementPending("a"));
        Assert.Equal(2, _touched.Count); // 공통 블록의 다시 만지기, 사다리 한 번 (이어 읽을 위치를 확인하는 표본 훅이 없어 처음부터 읽으므로 건너뛴 앞부분은 없음)
        ReadOp[] ladder = [.. _touched[^1]];
        Assert.Equal([.. Reversed([.. _common, .. all])], ladder); // 보충분의 뒤쪽 -> ... -> 앞부분 -> 공통 블록
        Assert.Equal(all[49], ladder[0]);
        Assert.Equal(_common[0], ladder[^1]);
    }

    [Fact]
    public void AStreamFinishedShortAtTheMemoryLimitLaddersTheRangeItReadAndNotTheRestOfTheWindow()
    {
        ReadOp[] all = HotSet(50);
        (_streamer.All, _streamer.ZoneMib) = (all, 5000);
        _avail = 4096 + 256 + 3000; // 창 3000
        _streamer.ReadMib = 1000;
        _streamer.OnProgress = progress =>
        {
            _avail = 4096 + 800; // 읽는 도중 들어가는 범위가 800 으로 줄어 읽은 1000 이 이미 넘음: 취소가 아니라 마무리
            progress(1000 * Mib, 3000 * Mib);
        };
        Prewarmer p = MakePrewarmer(Game("a"));

        Assert.True(p.Run(Game("a"), "service start").Ran);

        Assert.Equal((1000L, 5000L), p.PartialInfo("a"));
        Assert.Equal(Reversed(all.Take(10)), _touched.Single()); // 스트림이 읽은 창 (3000 MiB) 의 목록 중 읽은 앞 1000 MiB (파일 10 개) 만: 나머지 구간은 만지지 않음
        Assert.True(p.Solar.HasMark("a"));
    }

    [Fact]
    public void ASupplementFinishedShortAtTheMemoryLimitLaddersTheRangeItReadToo()
    {
        ReadOp[] all = HotSet(50);
        (_streamer.All, _streamer.ZoneMib) = (all, 5000);
        _avail = 4096 + 256 + 3000;
        Prewarmer p = MakePrewarmer(Game("a"));
        Assert.True(p.Run(Game("a"), "service start").Ran); // 창 3000 을 끝까지 읽음
        _touched.Clear();
        _avail = 4096 + 256 + 4300; // 보충 창 4300
        _streamer.ReadMib = 3300;
        _streamer.OnProgress = progress =>
        {
            _avail = 4096 + 3300;
            progress(3300 * Mib, 4300 * Mib);
        };

        Assert.False(p.SupplementWhenIdle(Game("a"), CancellationToken.None));
        _now += TimeSpan.FromSeconds(61);
        Assert.True(p.SupplementWhenIdle(Game("a"), CancellationToken.None));

        Assert.Equal((3300L, 5000L), p.PartialInfo("a"));
        Assert.Equal(Reversed(all.Take(33)), _touched.Single()); // 보충 창 (4300 MiB) 의 목록 중 읽은 앞 3300 MiB 만
    }

    [Fact]
    public void AStreamStoppedByMemoryBeforeReadingAnythingIsNotTouched()
    {
        ReadOp[] all = HotSet(50);
        (_streamer.All, _streamer.ZoneMib) = (all, 5000);
        _avail = 4096 + 256 + 3000;
        _streamer.ReadMib = 0;
        _streamer.OnProgress = progress =>
        {
            _avail = 4096 + 100;
            progress(0, 3000 * Mib); // 아직 아무것도 읽지 못한 채 들어가는 범위가 256 MiB 미만: 취소
        };
        Prewarmer p = MakePrewarmer(Game("a"));

        Assert.False(p.Run(Game("a"), "service start").Ran);

        Assert.Empty(_touched);
        Assert.False(p.Solar.HasMark("a"));
    }

    // 한도로 마무리한 일반 예열 (읽은 1000 MiB, 창 3000) 뒤 보충 시도가 메모리 부족으로 막힌 상태를 만듦.
    private Prewarmer ShortFinishedAndWaiting(ReadOp[] all, bool waitForMemory)
    {
        (_streamer.All, _streamer.ZoneMib) = (all, 5000);
        _avail = 4096 + 256 + 3000; // 창 3000
        _streamer.ReadMib = 1000;
        _streamer.OnProgress = progress =>
        {
            _avail = 4096 + 800; // 읽는 도중 들어가는 범위가 800 으로 줄어 읽은 1000 이 이미 넘음: 마무리
            progress(1000 * Mib, 3000 * Mib);
        };
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        Assert.True(p.Run(Game("a"), "service start").Ran);
        Assert.Equal(Reversed(all.Take(10)), _touched.Single()); // 마무리한 스트림도 끝난 직후 읽은 범위의 사다리를 만듦 (아래 시나리오는 사다리가 없는 상태에서 시작하므로 표식을 지움)
        p.Solar.Forget("a");
        _touched.Clear();
        _streamer.OnProgress = null;
        _avail = waitForMemory ? 4096 + 256 + 1100 : 20000; // 대기: 들어가는 범위 1100 은 읽은 1000 보다 256 이상 크지 않음 / 아니면 곧 더 읽을 수 있음
        _now += TimeSpan.FromSeconds(30);
        Assert.False(p.SupplementWhenIdle(Game("a"), CancellationToken.None)); // 한가함을 막 보기 시작
        _now += TimeSpan.FromSeconds(61);
        return p;
    }

    [Fact]
    public void AGameWhoseSupplementWaitsForMemoryGetsItsFirstLadderOverTheRangeItReadWhenTheRewarmCheckFindsItWarm()
    {
        ReadOp[] all = HotSet(50);
        Prewarmer p = ShortFinishedAndWaiting(all, waitForMemory: true);
        Assert.False(p.SupplementWhenIdle(Game("a"), CancellationToken.None)); // 보충: 읽은 양보다 의미 있게 더 들어가지 않아 읽지 않음
        Assert.Empty(_touched);

        AutoSettings auto = Auto();
        p.Tick(auto); // 첫 틱은 다음 점검 시각만 정함
        _now += TimeSpan.FromMinutes(10.5);
        p.Tick(auto); // 점검: 표본이 warm, 보충은 메모리를 기다리는 중 -> 첫 사다리

        Assert.Equal([.. Reversed(all.Take(10))], _touched.Single()); // 창 3000 의 목록 중 실제로 읽은 앞 1000 MiB (파일 10 개) 만
        Assert.True(p.Solar.HasMark("a"));
    }

    [Fact]
    public void AGameWhoseSupplementCanSoonReadMoreHasNoWaitingLadderTarget()
    {
        ReadOp[] all = HotSet(50);
        Prewarmer p = ShortFinishedAndWaiting(all, waitForMemory: false); // 메모리는 충분해 곧 더 읽을 수 있음 (메모리를 기다리는 상태가 아님)

        Assert.Null(p.WaitingSupplementLadderReads(Game("a"))); // 재예열 점검은 사다리를 만들지 않고 보충에 맡김
    }

    [Fact]
    public void TheWaitingLadderTargetIsTheFrontOfTheReadWindowNotAReplannedList()
    {
        ReadOp[] all = HotSet(50);
        Prewarmer p = ShortFinishedAndWaiting(all, waitForMemory: true);
        Assert.False(p.SupplementWhenIdle(Game("a"), CancellationToken.None)); // 메모리를 기다리는 상태가 됨

        Assert.Equal(all.Take(10), p.WaitingSupplementLadderReads(Game("a"))); // 읽은 창 (3000 MiB) 목록의 앞 1000 MiB
    }

    [Fact]
    public void AWaitingSupplementLadderThatFindsDataMissingLeavesTheRefillMarkAndIsNotRepeatedNorRefilledWhileTheSupplementIsPending()
    {
        ReadOp[] all = HotSet(50);
        Prewarmer p = ShortFinishedAndWaiting(all, waitForMemory: true);
        Assert.False(p.SupplementWhenIdle(Game("a"), CancellationToken.None));
        _result = _result with { SlowPieces = 4, End = TouchEnd.NeedsDiskReads, DiskBytes = 40 * Mib }; // 사다리가 캐시에 없는 부분을 만남
        AutoSettings auto = Auto();
        p.Tick(auto);
        _now += TimeSpan.FromMinutes(10.5);

        p.Tick(auto); // 점검: 보충이 메모리를 기다리는 게임의 첫 사다리가 디스크 읽기를 감지해 멈춤

        Assert.Single(_touched);
        Assert.True(p.Solar.HasMissing("a")); // 표식을 지우고 다시 채움 표시를 세움
        Assert.False(p.Solar.HasMark("a"));
        int callsBefore = _streamer.Calls.Count;
        _now += TimeSpan.FromMinutes(10.5);

        p.Tick(auto); // 다음 점검

        Assert.Single(_touched); // 표시가 있는 게임은 다시 만들지 않음
        Assert.Equal(callsBefore, _streamer.Calls.Count); // 보충이 대기 중이라 표시로 인한 재예열도 읽지 못하고 건너뛰기를 되풀이하지 않음 (읽기 호출 없음)
    }

    // ---- 묶음 새로 고침과 스트림 앞의 보호 ----

    // a (더 오래전, 순위가 낮고 A 가 작음: b 의 트리거 사다리가 이전 표식 없이 100 MiB 를 만져 3900 에서 3800 MiB) 와 b (최근, A 7900 MiB) 를 예열해 둘 다 표식이 있고 4 분 넘게 지난 상태. b 는 트리거 경로로 예열해 스트림 앞의 보호가 a 를 만지지 않게 함.
    // 재사용 누계는 1000 MiB 에서 시작: a 는 1520 MiB (0.4 x 3800) 늘면 때가 되고 b 는 3160 MiB 가 필요. c 는 예열하지 않은 세 번째 게임.
    private Prewarmer TwoMarkedGames()
    {
        _lists = new MemoryLists(0, 1000 * Mib, 4000 * Mib);
        Prewarmer p = MakePrewarmer(Game("a"), Game("b"), Game("c"));
        Assert.True(p.Run(Game("a"), "service start").Ran);
        GrowReuse(1000);
        _now += TimeSpan.FromMinutes(2);
        Assert.True(p.Run(Game("b"), "launcher", trigger: true).Ran);
        _now += TimeSpan.FromMinutes(2);
        _touched.Clear();
        return p;
    }

    [Fact]
    public void WhenOnlyOneMarkedGameIsDueTheTickRefreshesTheWholeGroupLowestRankFirstAndWritesTheMarksFromTheTouchOrder()
    {
        Prewarmer p = TwoMarkedGames();
        _onTouch = () => _lists = new MemoryLists(0, 2600 * Mib, 9000 * Mib); // 만진 직후의 캐시 대기 바이트 C
        GrowReuse(1000 + 1600); // a: 0.4 x 3800 = 1520 MiB 에 이름, b: 0.4 x 7900 = 3160 MiB 에는 못 미침
        int logged = _log.Lines.Count;

        p.Tick(Auto());

        Assert.Equal(2, _touched.Count);
        Assert.All(_touched, t => Assert.Equal(Reversed(Reads), t));
        string[] lines = [.. _log.Lines.Skip(logged).Where(l => l.Contains("cache refreshed in memory"))];
        Assert.Contains("a: cache refreshed in memory: 100 MiB in 0.4 s (1600 MiB repurposed of 3800 MiB ahead)", lines[0]); // 때가 되지 않은 b 도 함께, 순위가 낮은 a 가 먼저
        Assert.Contains("b: cache refreshed in memory: 100 MiB in 0.4 s (1600 MiB repurposed of 7900 MiB ahead)", lines[1]);
        _onTouch = null;
        _lists = new MemoryLists(0, 2700 * Mib, 9000 * Mib);
        _ = p.Solar.Refresh(Game("a"), CancellationToken.None);
        _ = p.Solar.Refresh(Game("b"), CancellationToken.None);
        string[] after = [.. _log.Lines.TakeLast(2)];
        Assert.Contains("(100 MiB repurposed of 8800 MiB ahead)", after[0]); // 9000 - (100 + 100)
        Assert.Contains("(100 MiB repurposed of 8900 MiB ahead)", after[1]); // 9000 - 100
    }

    [Fact]
    public void AColdGameLeavesTheGroupAndAGameTheGateSkipsKeepsItsMarkWithWhatWasAheadLoweredByWhatTheOthersTouched()
    {
        _lists = new MemoryLists(0, 1000 * Mib, 4000 * Mib);
        Prewarmer p = MakePrewarmer(Game("a"), Game("b"), Game("c"));
        Assert.True(p.Run(Game("a"), "service start").Ran); // A = 3900 MiB
        GrowReuse(1000);
        _now += TimeSpan.FromMinutes(2);
        Assert.True(p.Run(Game("b"), "launcher", trigger: true).Ran);
        _now += TimeSpan.FromMinutes(2);
        Assert.True(p.Run(Game("c"), "launcher", trigger: true).Ran); // A: b 7900, c 7900 MiB
        _now += TimeSpan.FromSeconds(30); // c 는 방금 (1 분 안) 예열: 게이트가 건너뜀
        _touched.Clear();
        _coldGames.Add("a");
        GrowReuse(1000 + 1600); // a: 0.4 x 3900 = 1560 MiB 에 이름
        int logged = _log.Lines.Count;

        p.Tick(Auto());

        Assert.Single(_touched); // b 만 만짐
        Assert.False(p.Solar.HasMark("a")); // 식은 게임은 표식을 지우고 재예열 점검에 맡김
        Assert.Contains(_log.Lines.Skip(logged), l => l.Contains("a: cache refresh skipped, the samples are cold"));
        Assert.True(p.Solar.HasMark("c"));
        _coldGames.Clear();
        _ = p.Solar.Refresh(Game("c"), CancellationToken.None);
        Assert.Contains("(1600 MiB repurposed of 7800 MiB ahead)", _log.Lines.Last()); // 7900 - min(b 의 이전 표식이 만진 100, 이번에 만진 100): b 의 이전 표식이 c 보다 먼저, 기준값은 그대로
        _ = p.Solar.Refresh(Game("b"), CancellationToken.None);
        Assert.Contains("(0 MiB repurposed of 7900 MiB ahead)", _log.Lines.Last()); // b 의 새 표식: 대기 8000 - 자기가 만진 100, 기준값은 이번 시점
    }

    [Fact]
    public void TheGroupIsNotRefreshedAgainWithinSixtySecondsEvenWhenDue()
    {
        Prewarmer p = TwoMarkedGames();
        GrowReuse(1000 + 1600);
        p.Tick(Auto());
        Assert.Equal(2, _touched.Count);
        GrowReuse(30000);
        _now += TimeSpan.FromSeconds(30);

        p.Tick(Auto());

        Assert.Equal(2, _touched.Count);
        _now += TimeSpan.FromSeconds(31);
        p.Tick(Auto());
        Assert.Equal(4, _touched.Count);
    }

    [Fact]
    public void AStreamThatIsNotATriggerTouchesTheOtherMarkedGamesLowestRankFirstBeforeItReadsAndTheDoneLineShowsIt()
    {
        Prewarmer p = TwoMarkedGames();
        int touchesAtStream = -1;
        _streamer.DuringStream = () => touchesAtStream = _touched.Count;
        int logged = _log.Lines.Count;

        PrewarmResult result = p.Run(Game("c"), "service start");

        Assert.True(result.Ran);
        Assert.Equal(2, touchesAtStream); // 스트림이 읽는 동안에는 a, b 를 이미 만졌음
        Assert.Equal(5, _touched.Count); // 그리고 사다리 직전의 보호 (a, b) 와 이 게임의 사다리
        Assert.All(_touched, t => Assert.Equal(Reversed(Reads), t));
        string[] lines = [.. _log.Lines.Skip(logged)];
        int a = Array.FindIndex(lines, l => l.Contains("a: cache refreshed in memory"));
        int b = Array.FindIndex(lines, l => l.Contains("b: cache refreshed in memory"));
        int done = Array.FindIndex(lines, l => l.Contains("c: prewarm done (service start)"));
        Assert.True(a >= 0 && a < b && b < done); // 순위가 낮은 a 가 먼저
        Assert.Contains(", protected 2 games (400 MiB in ", lines[done]); // 앞과 사다리 직전, 두 번 합산 (서로 다른 게임은 2)
        Assert.EndsWith(" s), 2 times", lines[done]);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("a: prewarm done") && l.Contains("protected")); // 표식 있는 다른 게임이 없던 스트림에는 꼬리가 없음
    }

    [Fact]
    public void ATriggerStreamDoesNotProtectTheOtherGamesButItsLadderLowersWhatIsAheadOfThem()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"), Game("b"));
        Assert.True(p.Run(Game("b"), "launcher", trigger: true).Ran); // b 의 이전 표식 (100 MiB)
        _now += TimeSpan.FromMinutes(2);
        Assert.True(p.Run(Game("a"), "launcher", trigger: true).Ran); // A = 7900 MiB, b 의 표식보다 나중
        _now += TimeSpan.FromMinutes(2);
        _touched.Clear();
        int touchesAtStream = -1;
        _streamer.DuringStream = () => touchesAtStream = _touched.Count;
        int logged = _log.Lines.Count;

        Assert.True(p.Run(Game("b"), "launcher", ignoreCooldown: true, trigger: true).Ran);

        Assert.Equal(0, touchesAtStream); // 트리거 예열은 늦추지 않음
        Assert.Single(_touched); // b 의 사다리만
        Assert.DoesNotContain(_log.Lines.Skip(logged), l => l.Contains("protected"));
        _ = p.Solar.Refresh(Game("a"), CancellationToken.None);
        Assert.Contains("(0 MiB repurposed of 7800 MiB ahead)", _log.Lines.Last()); // 7900 - min(b 의 이전 표식이 만진 100, 이번에 만진 100)
    }

    [Fact]
    public void AStreamLeavesGamesWithoutAMarkAloneAndWithTheSettingOffItIsTheSameAsBefore()
    {
        GrowReuse(1000);
        _enabled = false;
        Prewarmer unmarked = MakePrewarmer(Game("a"), Game("b"));
        Assert.True(unmarked.Run(Game("a"), "service start").Ran); // 표식 없음
        _enabled = true;
        _now += TimeSpan.FromMinutes(2);
        int touchesAtStream = -1;
        _streamer.DuringStream = () => touchesAtStream = _touched.Count;

        Assert.True(unmarked.Run(Game("b"), "service start").Ran);

        Assert.Equal(0, touchesAtStream);
        _touched.Clear();
        Prewarmer marked = TwoMarkedGames();
        _enabled = false;
        _touched.Clear();

        Assert.True(marked.Run(Game("c"), "service start").Ran);

        Assert.Empty(_touched);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("c: prewarm done") && l.Contains("protected"));
    }

    [Fact]
    public void AColdOtherGameIsNotTouchedByTheProtectionBecauseTheSampleCheckStaysInPlace()
    {
        Prewarmer p = TwoMarkedGames();
        _coldGames.Add("a");
        int touchesAtStream = -1;
        _streamer.DuringStream = () => touchesAtStream = _touched.Count;
        int logged = _log.Lines.Count;

        Assert.True(p.Run(Game("c"), "service start").Ran);

        Assert.Equal(1, touchesAtStream); // b 만
        Assert.False(p.Solar.HasMark("a"));
        Assert.Contains(_log.Lines.Skip(logged), l => l.Contains("a: cache refresh skipped, the samples are cold"));
    }

    [Fact]
    public void AGameStartDuringTheProtectionStopsItAtOnceAndTheRestOfTheMarkedGamesAreNotTouched()
    {
        Prewarmer p = TwoMarkedGames();
        _onTouch = () => p.NoteGameStart("c");
        int logged = _log.Lines.Count;

        PrewarmResult result = p.Run(Game("c"), "service start");

        Assert.False(result.Ran);
        Assert.Single(_touched);
        Assert.Contains(_log.Lines.Skip(logged), l => l.Contains("a: cache refresh stopped after"));
        Assert.DoesNotContain(_log.Lines.Skip(logged), l => l.Contains("b: cache refresh"));
    }

    [Fact]
    public void AProtectionThatFindsDataMissingOrThrowsLeavesTheStreamResultTheSame()
    {
        Prewarmer missing = TwoMarkedGames();
        _result = _result with { End = TouchEnd.NeedsDiskReads, SlowPieces = 3, DiskBytes = 32 * Mib };

        PrewarmResult result = missing.Run(Game("c"), "service start");

        Assert.True(result.Ran);
        Assert.Equal(30 * Mib, result.Stats!.Bytes);
        Assert.True(missing.Solar.HasMissing("a")); // 빠진 부분은 재예열 점검에 맡김
        Assert.DoesNotContain(_log.Lines, l => l.Contains("c: prewarm done") && l.Contains("protected")); // 끝까지 만진 게임이 없으면 꼬리 없음

        _result = new TouchResult(100 * Mib, 0.4, 0, 0, TouchEnd.Completed);
        Prewarmer throwing = TwoMarkedGames();
        _onTouch = () => throw new IOException("boom");

        PrewarmResult thrown = throwing.Run(Game("c"), "service start");

        Assert.True(thrown.Ran);
        Assert.Equal(30 * Mib, thrown.Stats!.Bytes);
        Assert.Contains(_log.Lines, l => l.Contains("a: cache refresh failed: IOException"));
    }

    [Fact]
    public void AGameThatAGateKeepsBlockedWhileItIsDueDoesNotMakeTheOtherGamesBeTouchedAgainAndAgain()
    {
        GameSettings e = Game("e") with { OriginRoot = @"E:\Games\e" }; // 다른 디스크
        _lists = new MemoryLists(0, 1000 * Mib, 4000 * Mib);
        Prewarmer p = MakePrewarmer(e, Game("b"), Game("c"));
        Assert.True(p.Run(e, "service start").Ran); // A = 3900 MiB
        GrowReuse(1000);
        _now += TimeSpan.FromMinutes(2);
        Assert.True(p.Run(Game("b"), "launcher", trigger: true).Ran);
        _now += TimeSpan.FromMinutes(2);
        Assert.True(p.Run(Game("c"), "launcher", trigger: true).Ran);
        _now += TimeSpan.FromMinutes(2);
        _touched.Clear();
        _ = _running.Add("e"); // e 는 실행 중: 게이트가 막음
        GrowReuse(1000 + 1600); // e 만 때가 됨 (b, c 는 3160 MiB 가 필요)

        for (int i = 0; i < 4; i++)
        {
            p.Tick(Auto());
            _now += TimeSpan.FromSeconds(61);
        }

        Assert.Empty(_touched); // 막힌 e 때문에 b, c 가 되풀이해 만져지지 않음
        Assert.True(p.Solar.HasMark("e"));
        GrowReuse(1000 + 3200); // 이제 b, c 자신이 때가 됨

        p.Tick(Auto());

        Assert.Equal(2, _touched.Count); // b 와 c 만 (막힌 e 는 건너뜀)
    }

    [Fact]
    public void AGameWhoseDiskReadLeaseIsRefusedKeepsItsMarkAndIsSkippedWithoutALog()
    {
        Prewarmer p = TwoMarkedGames();
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log);
        p.SetDiskReadCoordinator(coordinator);
        coordinator.Yield(@"D:\Games\x", "game start", "x"); // 같은 디스크에서 게임이 시작해 양보 중: 대여 거절
        GrowReuse(1000 + 1600);
        int logged = _log.Lines.Count;

        p.Tick(Auto());

        Assert.Empty(_touched);
        Assert.True(p.Solar.HasMark("a"));
        Assert.True(p.Solar.HasMark("b"));
        Assert.DoesNotContain(_log.Lines.Skip(logged), l => l.Contains("cache refresh skipped"));
    }

    [Fact]
    public void ATriggerThatStartsWaitingDuringTheProtectionStopsItBeforeTheNextGame()
    {
        Prewarmer p = TwoMarkedGames();
        IDisposable? wait = null;
        _onTouch = () => wait ??= p.BeginTriggerWait([Game("b")]); // a 를 만지는 도중 같은 디스크의 트리거 예열이 기다리기 시작함
        int logged = _log.Lines.Count;

        _ = p.Run(Game("c"), "service start");
        wait?.Dispose();

        Assert.Single(_touched); // a 만: b 와 이 게임의 사다리는 만지지 않음
        Assert.DoesNotContain(_log.Lines.Skip(logged), l => l.Contains("b: cache refresh"));
    }

    [Fact]
    public void ARewarmStreamAndASupplementStreamProtectTheOtherGamesBeforeTheyReadToo()
    {
        Prewarmer p = TwoMarkedGames();
        int touchesAtStream = -1;
        _streamer.DuringStream = () => touchesAtStream = _touched.Count;

        Assert.True(p.Run(Game("c"), "rewarm", ignoreCooldown: true).Ran);

        Assert.Equal(2, touchesAtStream); // 재예열: a, b 를 이미 만졌음

        ReadOp[] all = HotSet(50);
        (_streamer.All, _streamer.ZoneMib) = (all, 5000);
        _avail = 7000; // 핫 집합 5000 MiB + 예비 4096 MiB 가 다 들어가지 않아 앞부분만 읽음
        Prewarmer partial = TwoMarkedGames();
        Assert.True(partial.Run(Game("c"), "service start").Ran);
        Assert.True(partial.SupplementPending("c"));
        _touched.Clear();
        _avail = 20000;
        touchesAtStream = -1;
        Assert.False(partial.SupplementWhenIdle(Game("c"), CancellationToken.None));
        _now += TimeSpan.FromSeconds(61);

        Assert.True(partial.SupplementWhenIdle(Game("c"), CancellationToken.None));

        Assert.Equal(2, touchesAtStream); // 보충: 읽기 전에 a, b 를 만졌음
        Assert.Contains(_log.Lines, l => l.Contains("c: supplement prewarm done") && l.Contains(", protected 2 games ("));
    }

    [Fact]
    public void ASupplementThatReadAFullPlanAndFinishedShortAtTheMemoryLimitDoesNotLadderItsReadRange()
    {
        ReadOp[] all = HotSet(50);
        (_streamer.All, _streamer.ZoneMib) = (all, 5000);
        _avail = 4096 + 256 + 3000;
        Prewarmer p = MakePrewarmer(Game("a"));
        Assert.True(p.Run(Game("a"), "service start").Ran); // 창 3000 을 끝까지 읽음
        _touched.Clear();
        _avail = 4096 + 256 + 6000; // 이번에는 전체가 들어감: 틈 메우기 예산이 있는 전체 경로 (창 없음)
        _streamer.ReadMib = 3300;
        _streamer.OnProgress = progress =>
        {
            _avail = 4096 + 3300;
            progress(3300 * Mib, 5000 * Mib);
        };

        Assert.False(p.SupplementWhenIdle(Game("a"), CancellationToken.None));
        _now += TimeSpan.FromSeconds(61);
        Assert.True(p.SupplementWhenIdle(Game("a"), CancellationToken.None));

        Assert.Equal((3300L, 5000L), p.PartialInfo("a"));
        Assert.Empty(_touched); // 예산이 있는 계획으로 읽은 범위는 창으로 다시 계획한 목록과 같다고 알 수 없어 만지지 않음 (재예열 점검의 첫 사다리가 받침)
    }

    // ---- 새로 고침이 필요한 때에 돌게 ----

    [Fact]
    public void TheTickRefreshesWhileTheDriveIsBusyButTheRewarmCheckStillSkipsAndDoesNotReadAgain()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        _streamer.Calls.Clear();
        AutoSettings auto = Auto();
        p.Tick(auto); // 첫 틱은 다음 점검 시각만 정함
        _diskBusy = true; // 다른 프로세스가 같은 드라이브를 씀
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);

        p.Tick(auto);

        Assert.Single(_touched); // 디스크를 읽지 않는 새로 고침은 돌고
        Assert.Contains("a: cache refreshed in memory", _log.Lines.Last());
        _now += TimeSpan.FromMinutes(6);
        GrowReuse(40000);

        p.Tick(auto); // 재예열 점검 차례

        Assert.Contains(_log.Lines, l => l == "Info: a: rewarm check skipped (the game drive is busy or its load is unknown)"); // 디스크에서 다시 읽을 수 있는 경로는 그대로 막힘
        Assert.Empty(_streamer.Calls);
    }

    [Fact]
    public void ABusyDriveDoesNotLiftTheGatesThatARunningGameAndTheExitGraceSetOnTheRefresh()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);
        _diskBusy = true;

        _ = _running.Add("a"); // 그 게임 실행 중
        p.Tick(Auto());
        _ = _running.Remove("a");
        var exits = new GameExitTracker(() => _now); // 종료 직후
        HashSet<string> none = new(StringComparer.OrdinalIgnoreCase);
        exits.ObserveStarted(Game("a"));
        _ = exits.Poll([Game("a")], none);
        p.SetGameExitTracker(exits);
        _now += GameExitTracker.Grace;
        Assert.Single(exits.Poll([Game("a")], none));
        AutoSettings auto = Auto() with { RewarmAfterExitMin = 3 };
        p.Tick(auto);

        Assert.Empty(_touched);
        _now += TimeSpan.FromMinutes(4);
        p.Tick(auto); // 유예가 지나면 바쁜 드라이브에서도 돎

        Assert.Single(_touched);
    }

    [Fact]
    public void AGameOutsideTheWarmBudgetIsNotRefreshedEvenWhileTheDriveIsBusy()
    {
        _streamer.ZoneMib = 30000;
        _avail = 100000;
        _lists = new MemoryLists(0, 1000 * Mib, 40000 * Mib);
        Prewarmer p = MakePrewarmer(Game("a"), Game("b"), Game("c"));
        p.WarmRank = games => games; // 순위가 있어야 예산 (물리 메모리 64 GiB 의 절반) 을 계산함
        foreach (string name in new[] { "a", "b", "c" })
        {
            _now += TimeSpan.FromMinutes(2);
            Assert.True(p.Run(Game(name), "service start").Ran);
        }

        Assert.DoesNotContain("a", p.WarmBudget()!.Value.In); // 가장 먼저 예열한 a 는 예산 밖
        _touched.Clear();
        _diskBusy = true;
        GrowReuse(1000 + 20000);
        _now += TimeSpan.FromMinutes(5);

        p.Tick(Auto());

        Assert.Equal(2, _touched.Count); // b, c 만
        Assert.True(p.Solar.HasMark("a"));
    }

    [Fact]
    public void ASampleThatIsColdWhileTheDriveIsBusyKeepsTheMarkAndTouchesAndLogsNothingButOnAnIdleDriveItForgetsTheMark()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);
        _coldGames.Add("a");
        _diskBusy = true;
        int logged = _log.Lines.Count;

        p.Tick(Auto());

        Assert.Empty(_touched);
        Assert.True(p.Solar.HasMark("a")); // 바쁜 드라이브의 느린 표본은 식은 증거가 아님: 표식을 지우지 않음
        Assert.DoesNotContain(_log.Lines.Skip(logged), l => l.Contains("cache refresh"));
        _diskBusy = false;
        _now += TimeSpan.FromSeconds(30);

        p.Tick(Auto()); // 바쁘지 않을 때의 cold 는 지금처럼

        Assert.Empty(_touched);
        Assert.False(p.Solar.HasMark("a"));
        Assert.Contains(_log.Lines.Skip(logged), l => l.Contains("a: cache refresh skipped, the samples are cold"));
    }

    // 스트림 도중의 보호 시험용: 묶음 3 개 (각 1 op) 를 읽는 스트림.
    private void ThreeSingleOpBatches() => _streamer.Batches.AddRange([[Reads[0]], [Reads[1]], [Reads[2]]]);

    private int[] TouchSizes() => [.. _touched.Select(t => t.Count)];

    [Fact]
    public void AStreamProtectsTheOtherMarkedGamesBetweenBatchesWhenTheyAreDueAndTheDoneLineSumsItUp()
    {
        Prewarmer p = TwoMarkedGames();
        ThreeSingleOpBatches();
        _streamer.BetweenProgressAndTouch = i =>
        {
            if (i == 1)
            {
                _now += TimeSpan.FromSeconds(61); // 앞의 보호 뒤 묶음 최소 간격이 지나고
                GrowReuse(1000 + 5000); // a, b 모두 때가 됨
            }
        };
        int logged = _log.Lines.Count;

        PrewarmResult result = p.Run(Game("c"), "service start");

        Assert.True(result.Ran);
        Assert.Equal([3, 3, 1, 1, 3, 3, 1, 3, 3, 3], TouchSizes()); // 앞의 보호 (a, b) → 묶음 0, 묶음 1 (읽기 → 점검 → 다시 만지기) → 보호 (a, b) → 묶음 2 → 사다리 직전의 보호 (a, b) → c 의 사다리
        string done = _log.Lines.Skip(logged).Single(l => l.Contains("c: prewarm done (service start)"));
        Assert.Contains(", protected 2 games (600 MiB in ", done); // 서로 다른 게임은 2, 양은 세 번 합산
        Assert.EndsWith(" s), 3 times", done);
    }

    [Fact]
    public void TheStreamChecksWhetherTheOthersAreDueAtMostOnceEveryTenSeconds()
    {
        Prewarmer p = TwoMarkedGames();
        _streamer.Batches.AddRange([[Reads[0]], [Reads[1]], [Reads[2]], [Reads[0]], [Reads[1]]]);
        List<int> queries = [];
        _streamer.BetweenProgressAndTouch = i =>
        {
            queries.Add(_listQueries);
            _now += TimeSpan.FromSeconds(i == 0 ? 61 : 4); // 첫 묶음에서 앞의 보호 뒤 1 분을 넘기고, 그 뒤는 묶음마다 4 초
        };

        Assert.True(p.Run(Game("c"), "service start").Ran);

        // 판정은 첫 묶음 (61 초) 과 그 뒤 12 초째 묶음에서만: 사이의 묶음 (4, 8 초) 은 통계를 읽지 않음
        Assert.Equal([1, 0, 0, 1], queries.Zip(queries.Skip(1), (before, after) => after - before));
    }

    [Fact]
    public void TheStreamDoesNotProtectBetweenBatchesWithinSixtySecondsOfTheLastGroupRefreshEvenWhenDue()
    {
        Prewarmer p = TwoMarkedGames();
        ThreeSingleOpBatches();
        _streamer.BetweenProgressAndTouch = i =>
        {
            if (i == 1)
            {
                _now += TimeSpan.FromSeconds(30);
                GrowReuse(1000 + 5000); // 때는 됐지만 앞의 보호 (묶음 새로 고침) 뒤 60 초가 안 지남
            }
        };

        Assert.True(p.Run(Game("c"), "service start").Ran);

        Assert.Equal([3, 3, 1, 1, 1, 3, 3, 3], TouchSizes()); // 앞의 보호와 사다리 직전의 보호만 (도중에는 없음)
    }

    [Fact]
    public void ATriggerStreamDoesNotProtectBetweenBatches()
    {
        Prewarmer p = TwoMarkedGames();
        ThreeSingleOpBatches();
        _streamer.BetweenProgressAndTouch = i =>
        {
            if (i == 1)
            {
                _now += TimeSpan.FromSeconds(61);
                GrowReuse(1000 + 5000);
            }
        };

        Assert.True(p.Run(Game("c"), "launcher", trigger: true).Ran);

        Assert.Equal([1, 1, 1, 3], TouchSizes()); // 앞의 보호도 도중의 보호도 없음: 묶음 다시 만지기와 c 의 사다리만
    }

    [Fact]
    public void AGameStartOrATriggerWaitStopsTheProtectionBetweenBatches()
    {
        Prewarmer started = TwoMarkedGames();
        ThreeSingleOpBatches();
        _streamer.BetweenProgressAndTouch = i =>
        {
            if (i == 1)
            {
                _now += TimeSpan.FromSeconds(61);
                GrowReuse(1000 + 5000);
                started.NoteGameStart("c");
            }
        };

        Assert.False(started.Run(Game("c"), "service start").Ran);

        Assert.Equal([3, 3, 1], TouchSizes()); // 게임 시작: 점검이 스트림을 멈추고 도중의 보호도 없음

        _touched.Clear();
        _streamer.Batches.Clear();
        Prewarmer waiting = TwoMarkedGames();
        ThreeSingleOpBatches();
        IDisposable? wait = null;
        _streamer.BetweenProgressAndTouch = i =>
        {
            if (i == 1)
            {
                _now += TimeSpan.FromSeconds(61);
                GrowReuse(1000 + 5000);
                wait ??= waiting.BeginTriggerWait([Game("b")]);
            }
        };

        _ = waiting.Run(Game("c"), "service start");
        wait?.Dispose();

        Assert.DoesNotContain(TouchSizes().Skip(4), size => size == 3); // 선점 표시가 서면 도중의 보호는 없음 (사다리도 없음)
    }

    [Fact]
    public void AProtectionBetweenBatchesThatThrowsLeavesTheStreamResultTheSame()
    {
        Prewarmer p = TwoMarkedGames();
        ThreeSingleOpBatches();
        bool armed = false;
        _onTouch = () =>
        {
            if (armed && _touched[^1].Count == 3)
            {
                throw new IOException("boom");
            }
        };
        _streamer.BetweenProgressAndTouch = i =>
        {
            if (i == 1)
            {
                _now += TimeSpan.FromSeconds(61);
                GrowReuse(1000 + 5000);
                armed = true;
            }
        };

        PrewarmResult result = p.Run(Game("c"), "service start");

        Assert.True(result.Ran);
        Assert.Equal(30 * Mib, result.Stats!.Bytes);
        Assert.Contains(_log.Lines, l => l.Contains("a: cache refresh failed: IOException"));
    }

    [Fact]
    public void TheDoneLineShowsHowManyTimesTheOthersWereProtectedOnlyWhenItWasMoreThanOnce()
    {
        Assert.Equal(", protected 2 games (3 MiB in 0.1 s)", new RequeueTally(0, 0, false, 0, 2, 3 * Mib, 0.1, 1).Note());
        Assert.Equal(", protected 2 games (3 MiB in 0.1 s), 3 times", new RequeueTally(0, 0, false, 0, 2, 3 * Mib, 0.1, 3).Note());
        Assert.Equal(", protected 2 games (4 MiB in 0.3 s), 2 times", (new RequeueTally(0, 0, false, 0, 2, 3 * Mib, 0.1, 1) + new RequeueTally(0, 0, false, 0, 0, 1 * Mib, 0.2, 1)).Note());
    }

    [Fact]
    public void AGameTheServiceStartSkippedAsAlreadyWarmGetsItsLadderAtOnceWithoutReadingTheSamplesAgain()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));

        PrewarmResult result = Assert.Single(p.RunServiceStart([Game("a")]));

        Assert.False(result.Ran);
        Assert.Equal("already warm", result.SkipReason);
        Assert.True(p.Solar.HasMark("a"));
        Assert.Equal(Reversed(Reads), _touched.Single());
        Assert.Equal(1, _probeReads); // 건너뛸 때의 표본 판정 한 번뿐
        Assert.Empty(_streamer.Calls);
    }

    [Fact]
    public void SeveralGamesSkippedAtServiceStartAreLadderedLowestRankFirstAndTheMarksAreWrittenAsAGroup()
    {
        Prewarmer p = MakePrewarmer(Game("a"), Game("b"));
        _lists = new MemoryLists(0, 1000 * Mib, 8000 * Mib);

        Assert.Equal(2, p.RunServiceStart([Game("a"), Game("b")]).Count);

        Assert.Equal(2, _touched.Count);
        string[] ordered = [.. _log.Lines.Where(l => l.Contains("ladder ordered")).Select(l => l.Split(':')[1].Trim())];
        Assert.Equal(["b", "a"], ordered); // 순위가 낮은 b 먼저
        _ = p.Solar.Refresh(Game("b"), CancellationToken.None);
        Assert.Contains("(0 MiB repurposed of 7800 MiB ahead)", _log.Lines.Last()); // 8000 - (b 100 + a 100)
        _ = p.Solar.Refresh(Game("a"), CancellationToken.None);
        Assert.Contains("(0 MiB repurposed of 7900 MiB ahead)", _log.Lines.Last()); // 8000 - a 100
    }

    [Fact]
    public void AGameWhoseSupplementWaitsForMemoryIsLadderedOverTheRangeItReadAtServiceStartToo()
    {
        ReadOp[] all = HotSet(50);
        Prewarmer p = ShortFinishedAndWaiting(all, waitForMemory: true);
        Assert.False(p.SupplementWhenIdle(Game("a"), CancellationToken.None));
        Assert.Empty(_touched);

        _ = p.RunServiceStart([Game("a")]);

        Assert.Equal([.. Reversed(all.Take(10))], _touched.Single()); // 읽은 앞 1000 MiB 만 (재예열 점검의 LadderWaitingSupplement 와 같은 대상)
        Assert.True(p.Solar.HasMark("a"));
    }

    [Fact]
    public void AServiceStartLadderFollowsTheGatesTheGameRunningAndTheSettingOff()
    {
        Prewarmer running = MakePrewarmer(Game("a"));
        _ = _running.Add("a");
        _ = running.RunServiceStart([Game("a")]);
        _ = _running.Remove("a");
        _enabled = false;
        Prewarmer off = MakePrewarmer(Game("b"));
        _ = off.RunServiceStart([Game("b")]);

        Assert.Empty(_touched);
        Assert.False(running.Solar.HasMark("a"));
    }

    [Fact]
    public void TheDiagnosticTailShowsWhatHappenedSinceTheLadderOnTheRewarmCheckLineWhetherTheMarkWasClearedByTheRefreshOrStillStands()
    {
        GrowReuse(1000);
        Prewarmer cleared = MakePrewarmer(Game("a"));
        _ = cleared.Run(Game("a"), "service start");
        AutoSettings auto = Auto();
        cleared.Tick(auto);
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);
        _coldGames.Add("a");
        cleared.Tick(auto); // 새로 고침의 표본이 식음: 표식을 지우되 그 값을 기억함
        Assert.False(cleared.Solar.HasMark("a"));
        _now += TimeSpan.FromMinutes(5.5);

        cleared.Tick(auto); // 재예열 점검도 식음

        Assert.Contains(_log.Lines, l => l.StartsWith("Info: a: rewarm check: cold (", StringComparison.Ordinal) && l.EndsWith("prewarming again (cooldown ignored), since the ladder 10 min ago: 19000 MiB repurposed of 7900 MiB ahead", StringComparison.Ordinal));

        _coldGames.Clear();
        GrowReuse(1000);
        Prewarmer standing = MakePrewarmer(Game("b"));
        _ = standing.Run(Game("b"), "service start");
        standing.Tick(auto);
        _now += TimeSpan.FromMinutes(10.5);
        _coldGames.Add("b");

        standing.Tick(auto); // 표식이 서 있는 채 점검이 식음을 봄

        Assert.Contains(_log.Lines, l => l.StartsWith("Info: b: rewarm check: cold (", StringComparison.Ordinal) && l.EndsWith("prewarming again (cooldown ignored), since the ladder 10 min ago: 0 MiB repurposed of 7900 MiB ahead", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDiagnosticTailIsLeftOutWhenThereWasNoMarkAndShowsOnlyTheTimeWhenTheListsCannotBeRead()
    {
        _enabled = false; // 표식 없이 예열
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _enabled = true;
        AutoSettings auto = Auto();
        p.Tick(auto);
        _now += TimeSpan.FromMinutes(10.5);
        _coldGames.Add("a");

        p.Tick(auto);

        Assert.Contains(_log.Lines, l => l.StartsWith("Info: a: rewarm check: cold (", StringComparison.Ordinal) && l.EndsWith("prewarming again (cooldown ignored)", StringComparison.Ordinal));

        _lists = null;
        Solar solar = Make();
        _ = solar.Order(Game("b"), Reads, CancellationToken.None);
        _now += TimeSpan.FromMinutes(3);
        _result = new TouchResult(10 * Mib, 0.6, 5, 0, TouchEnd.NeedsDiskReads, 32 * Mib);
        _ = solar.Order(Game("b"), Reads, CancellationToken.None);
        Assert.EndsWith("left to the rewarm check, since the ladder 3 min ago", _log.Lines.Last()); // 대기 목록을 읽을 수 없으면 시간만
    }

    [Fact]
    public void ALadderThatFindsDataMissingForAGameThatHadAMarkShowsWhatHappenedSinceIt()
    {
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _now += TimeSpan.FromMinutes(3);
        _result = new TouchResult(10 * Mib, 0.6, 5, 0, TouchEnd.NeedsDiskReads, 32 * Mib);

        _ = solar.Order(Game("a"), Reads, CancellationToken.None);

        Assert.EndsWith("left to the rewarm check, since the ladder 3 min ago: 0 MiB repurposed of 7900 MiB ahead", _log.Lines.Last());
    }

    [Theory]
    [InlineData(false)] // 처리량을 읽을 수 있고 바쁨
    [InlineData(true)] // 처리량을 모름 (늘 바쁨으로 봄)
    public void ABusyColdSampleIsOverlookedOnlyOnceThenTheMarkIsDroppedAndNothingIsReadOrTouchedAgain(bool throughputUnknown)
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"), Game("b"));
        _ = p.Run(Game("a"), "service start");
        _ = p.Run(Game("b"), "service start");
        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);
        _coldGames.Add("a"); // b 는 warm 인 채 표식이 있음
        (_diskBusy, _diskUnknown) = (!throughputUnknown, throughputUnknown);
        int logged = _log.Lines.Count;

        p.Tick(Auto());

        Assert.True(p.Solar.HasMark("a")); // 첫 회: 봐 줌 (표식 유지, 로그 없음)
        Assert.DoesNotContain(_log.Lines.Skip(logged), l => l.Contains("a: cache refresh"));
        _now += TimeSpan.FromSeconds(61);

        p.Tick(Auto());

        Assert.False(p.Solar.HasMark("a")); // 둘째 회: 바쁘지 않을 때와 같게 표식을 지우고 재예열 점검에 맡김
        Assert.Contains(_log.Lines.Skip(logged), l => l.Contains("a: cache refresh skipped, the samples are cold") && l.Contains(", since the ladder 6 min ago"));
        int probes = _probeReads;
        int touches = _touched.Count;
        for (int i = 0; i < 3; i++)
        {
            _now += TimeSpan.FromSeconds(61);
            GrowReuse(30000 + i * 10000L);
            p.Tick(Auto());
        }

        Assert.Equal(probes + 3, _probeReads); // 그 뒤에는 a 의 표본을 읽지 않음 (b 의 표본만: 틱마다 한 번)
        Assert.All(_touched.Skip(touches), t => Assert.Equal(Reversed(Reads), t)); // 만지기는 b 만
    }

    [Fact]
    public void AWarmSampleAfterAnOverlookedBusyColdSampleGivesTheMarkItsOneOverlookAgain()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);
        _coldGames.Add("a");
        _diskBusy = true;
        p.Tick(Auto()); // 봐 줌
        _coldGames.Clear();
        _now += TimeSpan.FromSeconds(61);
        p.Tick(Auto()); // 바쁜 드라이브에서 warm: 새로 고침이 돌고 새 표식이 섬
        Assert.Single(_touched);
        GrowReuse(40000);
        _coldGames.Add("a");
        _now += TimeSpan.FromSeconds(61);
        int logged = _log.Lines.Count;

        p.Tick(Auto()); // 다시 바쁜 cold: 횟수가 0 으로 돌아갔으므로 한 번 더 봐 줌

        Assert.True(p.Solar.HasMark("a"));
        Assert.DoesNotContain(_log.Lines.Skip(logged), l => l.Contains("a: cache refresh"));
    }

    [Fact]
    public void WhenTheDriveLoadIsUnknownTheRefreshStillRunsAndTheRewarmCheckStaysBlocked()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _touched.Clear();
        _streamer.Calls.Clear();
        AutoSettings auto = Auto();
        p.Tick(auto);
        _diskUnknown = true;
        GrowReuse(20000);
        _now += TimeSpan.FromMinutes(5);

        p.Tick(auto);

        Assert.Single(_touched);
        _now += TimeSpan.FromMinutes(6);
        p.Tick(auto);
        Assert.Contains(_log.Lines, l => l == "Info: a: rewarm check skipped (the game drive is busy or its load is unknown)");
        Assert.Empty(_streamer.Calls);
    }

    [Fact]
    public void ATriggerRunOfAGameWithoutAMarkMakesNoLadderBeforeItsStream()
    {
        _enabled = false;
        Prewarmer p = MakePrewarmer(Game("a"));
        _ = p.Run(Game("a"), "service start");
        _enabled = true;
        _touched.Clear();
        int touchesAtStream = -1;
        _streamer.DuringStream = () => touchesAtStream = _touched.Count;
        _now += TimeSpan.FromMinutes(5);

        Assert.True(p.Run(Game("a"), "launcher", ignoreCooldown: true, trigger: true).Ran);

        Assert.Equal(0, touchesAtStream); // 서비스 시작 직후 사다리는 서비스 시작 경로 전용: 트리거 경로는 스트림 전에 만지지 않음
    }

    private void StreamWithProtectionFromAMarkedStreamGame(bool streamGameHasAMark)
    {
        Prewarmer p = TwoMarkedGames();
        if (streamGameHasAMark)
        {
            Assert.True(p.Run(Game("c"), "service start").Ran); // c 의 이전 표식 (100 MiB)
            _now += TimeSpan.FromMinutes(2);
        }

        _touched.Clear();
        ThreeSingleOpBatches();
        _streamer.BetweenProgressAndTouch = i =>
        {
            if (i == 1)
            {
                _now += TimeSpan.FromSeconds(61);
                _lists = new MemoryLists(0, 20000 * Mib, 9000 * Mib); // 대기 바이트 C = 9000 MiB, a, b 모두 때가 됨
            }
        };
        _ = p.Run(Game("c"), "rewarm", ignoreCooldown: true);
        _onTouch = null;
        _ = p.Solar.Refresh(Game("a"), CancellationToken.None);
        _ = p.Solar.Refresh(Game("b"), CancellationToken.None);
    }

    [Fact]
    public void AProtectionDuringAStreamTakesWhatTheStreamReadSoFarOutOfTheAheadOfTheNewMarks()
    {
        StreamWithProtectionFromAMarkedStreamGame(streamGameHasAMark: false);

        string[] after = [.. _log.Lines.TakeLast(2)];
        // 스트림 끝의 사다리 직전 보호가 a, b, c 를 만진 순서대로 한 묶음으로 다시 표식해 (실제 순서) 도중의 값을 덮음: 최종값은 9000 - 300 (a), 9000 - 200 (b)
        Assert.Contains("of 8700 MiB ahead", after[0]);
        Assert.Contains("of 8800 MiB ahead", after[1]);
    }

    // 도중의 보호를 받은 게임 e (다른 디스크) 가 스트림 끝에서는 게임 실행으로 막혀 건너뛰어지는 경우: 도중에 만든 표식의 A 와 끝의 줄이기를 함께 고정함.
    // 도중의 보호 시점: C = 9000, 스트림이 만진 양 = 묶음 2 개 x 100 MiB, e 가 만진 양 100 MiB.
    private void ProtectedMidStreamThenSkippedAtTheEnd(bool streamGameHasAMark)
    {
        GameSettings e = Game("e") with { OriginRoot = @"E:\Games\e" };
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(e, Game("c"));
        Assert.True(p.Run(e, "service start").Ran);
        if (streamGameHasAMark)
        {
            _now += TimeSpan.FromMinutes(2);
            Assert.True(p.Run(Game("c"), "service start").Ran); // c 의 이전 표식 (100 MiB)
        }

        _now += TimeSpan.FromMinutes(2);
        _touched.Clear();
        ThreeSingleOpBatches();
        _streamer.BetweenProgressAndTouch = i =>
        {
            if (i == 1)
            {
                _now += TimeSpan.FromSeconds(61);
                _lists = new MemoryLists(0, 20000 * Mib, 9000 * Mib); // e 가 때가 됨
            }

            if (i == 2)
            {
                _ = _running.Add("e"); // 도중의 보호는 끝났고, 끝에서는 e 가 실행 중이라 막힘
            }
        };

        Assert.True(p.Run(Game("c"), "rewarm", ignoreCooldown: true).Ran);
        _ = p.Solar.Refresh(e, CancellationToken.None);
    }

    [Fact]
    public void WhenTheStreamGameHadNoMarkAProtectedGameThatTheEndSkipsIsLoweredOnlyByTheEndLadderNotTwice()
    {
        ProtectedMidStreamThenSkippedAtTheEnd(streamGameHasAMark: false);

        // 도중: 이전 표식이 없으면 스트림 몫을 빼지 않아 9000 - 100 (e) = 8900, 끝: 건너뛴 e 가 사다리 100 MiB 전체만큼 줄어 8800 (둘 다 빼면 8600)
        Assert.Contains("of 8800 MiB ahead", _log.Lines.Last());
    }

    [Fact]
    public void WhenTheStreamGameHadAMarkAProtectedGameThatTheEndSkipsIsLoweredByTheShareTheEndLadderDoesNotTakeAndThenByTheEndLadder()
    {
        ProtectedMidStreamThenSkippedAtTheEnd(streamGameHasAMark: true);

        // 도중: 스트림 몫 200 에서 이전 표식이 만진 100 을 뺀 100 만 빼 9000 - 100 - 100 = 8800, 끝: 이전 표식이 e 의 표식보다 먼저라 min(100, 100) = 100 이 더 줄어 8700
        Assert.Contains("of 8700 MiB ahead", _log.Lines.Last());
    }

    [Fact]
    public void WhenTheStreamGameHadAMarkTheEndLadderAndTheProtectionDoNotLowerWhatIsAheadTwice()
    {
        StreamWithProtectionFromAMarkedStreamGame(streamGameHasAMark: true);

        string[] after = [.. _log.Lines.TakeLast(2)];
        // 이전 표식이 있어도 같은 결과: 스트림 끝의 사다리 직전 보호가 a, b, c 를 만진 순서대로 한 묶음으로 다시 표식해 9000 - 300 (a), 9000 - 200 (b)
        Assert.Contains("of 8700 MiB ahead", after[0]);
        Assert.Contains("of 8800 MiB ahead", after[1]);
    }

    [Fact]
    public void TheStreamShareToTakeOutIsWhatTheStreamReadMinusWhatTheEndLadderLowersAnywayAndNothingWithoutAPreviousMark()
    {
        Assert.Equal(0, SolarPolicy.StreamAheadLess(200 * Mib, 0)); // 이전 표식이 없으면 끝의 사다리가 전체를 줄이므로 빼지 않음
        Assert.Equal(100 * Mib, SolarPolicy.StreamAheadLess(200 * Mib, 100 * Mib));
        Assert.Equal(0, SolarPolicy.StreamAheadLess(100 * Mib, 300 * Mib));
    }

    [Fact]
    public void AGameStartOrAPreemptionWhileTheEndProtectionRunsLeavesNoLadderAndNoStoppedLadderLine()
    {
        Prewarmer started = TwoMarkedGames();
        bool armed = false;
        ThreeSingleOpBatches();
        _streamer.BetweenProgressAndTouch = i => armed = i == 2; // 마지막 묶음 뒤 사다리 직전의 보호부터 (보호 만지기는 3 op, 묶음은 1 op)
        _onTouch = () =>
        {
            if (armed && _touched[^1].Count == 3)
            {
                started.NoteGameStart("c");
            }
        };

        _ = started.Run(Game("c"), "service start");

        Assert.DoesNotContain(_log.Lines, l => l.Contains("c: ladder ordered") || l.Contains("c: ladder stopped"));
        Assert.False(started.Solar.HasMark("c"));

        _onTouch = null;
        _touched.Clear();
        _streamer.Batches.Clear();
        Prewarmer waiting = TwoMarkedGames();
        int logged = _log.Lines.Count;
        ThreeSingleOpBatches();
        IDisposable? wait = null;
        armed = false;
        _streamer.BetweenProgressAndTouch = i => armed = i == 2;
        _onTouch = () =>
        {
            if (armed && _touched[^1].Count == 3)
            {
                wait ??= waiting.BeginTriggerWait([Game("b")]);
            }
        };

        _ = waiting.Run(Game("c"), "service start");
        wait?.Dispose();

        Assert.DoesNotContain(_log.Lines.Skip(logged), l => l.Contains("c: ladder ordered") || l.Contains("c: ladder stopped"));
        Assert.False(waiting.Solar.HasMark("c"));
    }

    [Fact]
    public void ASupplementThatReadItsWindowToTheEndProtectsTheOtherMarkedGamesRightBeforeItsLadder()
    {
        ReadOp[] all = HotSet(50);
        (_streamer.All, _streamer.ZoneMib) = (all, 5000);
        _avail = 7000; // 핫 집합 5000 MiB + 예비 4096 MiB 가 다 들어가지 않아 앞부분만 읽음
        Prewarmer p = TwoMarkedGames();
        Assert.True(p.Run(Game("c"), "service start").Ran);
        Assert.True(p.SupplementPending("c"));
        _touched.Clear();
        _avail = 20000;
        Assert.False(p.SupplementWhenIdle(Game("c"), CancellationToken.None));
        _now += TimeSpan.FromSeconds(61);
        int logged = _log.Lines.Count;

        Assert.True(p.SupplementWhenIdle(Game("c"), CancellationToken.None));

        string[] lines = [.. _log.Lines.Skip(logged)];
        int a = Array.FindLastIndex(lines, l => l.Contains("a: cache refreshed in memory"));
        int b = Array.FindLastIndex(lines, l => l.Contains("b: cache refreshed in memory"));
        int ladder = Array.FindIndex(lines, l => l.Contains("c: ladder ordered"));
        Assert.True(a >= 0 && a < b && b < ladder); // 사다리 직전의 보호 (a, b) 가 이 게임의 사다리보다 먼저
        Assert.Contains(_log.Lines.Skip(logged), l => l.Contains("c: supplement prewarm done") && l.Contains(", protected 2 games (") && l.EndsWith(" s), 2 times"));
    }

    [Theory]
    [InlineData(99, 300, 200, 6800)] // 이전 표식이 없으면 이번에 만진 200 MiB 전체
    [InlineData(99, 300, 9000, 0)] // 0 미만이 되지 않음
    [InlineData(-1, 300, 200, 6800)] // 이전 표식이 먼저면 기존 규칙 min(300, 200)
    [InlineData(-1, 100, 200, 6900)]
    [InlineData(1, 300, 200, 7000)] // 이전 표식이 나중이면 줄이지 않음
    public void WithoutAPreviousMarkTheStreamEndRuleLowersWhatIsAheadByEverythingTheLadderTouched(int previousIsEarlier, long previousTouchedMib, long touchedNowMib, long expectedMib)
    {
        LadderMark mine = Mark(0, 7000, _now);
        LadderMark? previous = previousIsEarlier == 99 ? null : Previous(previousTouchedMib, _now + TimeSpan.FromMinutes(previousIsEarlier));

        Assert.Equal(expectedMib * Mib, SolarPolicy.AheadAfterOthersTouched(mine, [(previous, touchedNowMib * Mib)], withoutPreviousAll: true));
    }

    [Fact]
    public void ALadderThatFollowsAProtectionRemarksTheProtectedGamesAndItselfAsOneGroupAndLowersTheRestOnlyByItsOwnTouch()
    {
        Solar solar = Make();
        _ = solar.Order(Game("x"), Reads, CancellationToken.None); // 보호에서 빠질 게임, A = 7900
        _now += TimeSpan.FromMinutes(1);
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _now += TimeSpan.FromMinutes(1);
        solar.Regroup([("a", 100 * Mib, null)]); // 보호가 a 를 만짐 (x 는 건너뜀)

        _ = solar.Order(Game("c"), Reads, CancellationToken.None, protectedFirst: [("a", 100 * Mib)]);

        _ = solar.Refresh(Game("a"), CancellationToken.None);
        Assert.Contains("of 7800 MiB ahead", _log.Lines.Last()); // 8000 - (a 100 + c 100)
        _ = solar.Refresh(Game("c"), CancellationToken.None);
        Assert.Contains("of 7900 MiB ahead", _log.Lines.Last()); // 8000 - c 100
        _ = solar.Refresh(Game("x"), CancellationToken.None);
        Assert.Contains("of 7800 MiB ahead", _log.Lines.Last()); // 건너뛴 x: c 가 만진 100 만 (a 의 몫은 보호가 이미 반영), c 에 이전 표식이 없어도 전체
    }

    [Fact]
    public void ALadderAfterAStreamLowersTheOthersByItsWholeTouchEvenWhenNoGameWasProtectedButAnyOtherLadderStaysTheSame()
    {
        Solar solar = Make();
        _ = solar.Order(Game("x"), Reads, CancellationToken.None);
        _ = solar.Order(Game("c"), Reads, CancellationToken.None, protectedFirst: []); // 보호를 못 한 스트림 끝의 사다리
        _ = solar.Refresh(Game("x"), CancellationToken.None);
        Assert.Contains("of 7800 MiB ahead", _log.Lines.Last());

        Solar plain = Make();
        _ = plain.Order(Game("x"), Reads, CancellationToken.None);
        _ = plain.Order(Game("c"), Reads, CancellationToken.None); // 스트림 끝이 아닌 첫 사다리는 지금과 같음
        _ = plain.Refresh(Game("x"), CancellationToken.None);
        Assert.Contains("of 7900 MiB ahead", _log.Lines.Last());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheEndOfAStreamProtectsTheOtherMarkedGamesRightBeforeItsLadderAndMarksThemAllAsOneGroupWhetherOrNotTheStreamGameHadAMark(bool streamGameHasAMark)
    {
        Prewarmer p = TwoMarkedGames();
        if (streamGameHasAMark)
        {
            Assert.True(p.Run(Game("c"), "service start").Ran);
            _now += TimeSpan.FromMinutes(2);
        }

        _touched.Clear();
        _onTouch = () => _lists = new MemoryLists(0, 2600 * Mib, 9000 * Mib); // 만진 직후의 캐시 대기 바이트 C
        int logged = _log.Lines.Count;

        Assert.True(p.Run(Game("c"), "rewarm", ignoreCooldown: true).Ran);

        Assert.Equal(5, _touched.Count); // 앞의 보호 (a, b), 사다리 직전의 보호 (a, b), c 의 사다리
        string[] lines = [.. _log.Lines.Skip(logged)];
        int a = Array.FindLastIndex(lines, l => l.Contains("a: cache refreshed in memory"));
        int b = Array.FindLastIndex(lines, l => l.Contains("b: cache refreshed in memory"));
        int ladder = Array.FindIndex(lines, l => l.Contains("c: ladder ordered"));
        Assert.True(a >= 0 && a < b && b < ladder); // 사다리보다 먼저, 순위가 낮은 a 부터
        _onTouch = null;
        _ = p.Solar.Refresh(Game("a"), CancellationToken.None);
        _ = p.Solar.Refresh(Game("b"), CancellationToken.None);
        _ = p.Solar.Refresh(Game("c"), CancellationToken.None);
        string[] after = [.. _log.Lines.TakeLast(3)];
        Assert.Contains("of 8700 MiB ahead", after[0]); // C 9000 - (a, b, c 가 만진 300): 이전 표식 유무와 무관, 이중으로 줄지 않음
        Assert.Contains("of 8800 MiB ahead", after[1]);
        Assert.Contains("of 8900 MiB ahead", after[2]);
    }

    [Fact]
    public void ATriggerStreamWithoutAPreviousMarkLowersWhatIsAheadOfTheOtherGamesByEverythingItsLadderTouched()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"), Game("b"));
        Assert.True(p.Run(Game("a"), "service start").Ran); // A = 7900 MiB
        _now += TimeSpan.FromMinutes(2);
        _touched.Clear();
        int touchesAtStream = -1;
        _streamer.DuringStream = () => touchesAtStream = _touched.Count;

        Assert.True(p.Run(Game("b"), "launcher", trigger: true).Ran);

        Assert.Equal(0, touchesAtStream);
        Assert.Single(_touched); // 사다리 직전의 보호도 없음
        _ = p.Solar.Refresh(Game("a"), CancellationToken.None);
        Assert.Contains("(0 MiB repurposed of 7800 MiB ahead)", _log.Lines.Last()); // 7900 - b 가 만진 100 전체
    }

    [Fact]
    public async Task WhenTheProtectionCannotRunAtTheEndOfAStreamTheLadderStillLowersTheOthersByItsWholeTouch()
    {
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(Game("a"), Game("c"));
        Assert.True(p.Run(Game("a"), "service start").Ran); // A = 7900 MiB
        _now += TimeSpan.FromMinutes(2);
        _touched.Clear();
        object rewarm = typeof(Prewarmer).GetField("_rewarm", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(p)!;
        object groupLock = typeof(Rewarmer).GetField("_groupLock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(rewarm)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task holder = Task.Run(() =>
        {
            lock (groupLock) // 유지 틱의 묶음 새로 고침이 도는 중: 보호는 기다리지 않고 건너뜀
            {
                held.Set();
                release.Wait();
            }
        });
        held.Wait();
        try
        {
            Assert.True(p.Run(Game("c"), "service start").Ran);
        }
        finally
        {
            release.Set();
            await holder;
        }

        Assert.Single(_touched); // c 의 사다리만
        _ = p.Solar.Refresh(Game("a"), CancellationToken.None);
        Assert.Contains("of 7800 MiB ahead", _log.Lines.Last());
    }

    [Fact]
    public void AGameThatAGateKeepsOutOfTheProtectionStillHasItsAheadLoweredByTheStreamEndLadder()
    {
        GameSettings e = Game("e") with { OriginRoot = @"E:\Games\e" }; // 다른 디스크
        GrowReuse(1000);
        Prewarmer p = MakePrewarmer(e, Game("c"));
        Assert.True(p.Run(e, "service start").Ran); // A = 7900 MiB
        _now += TimeSpan.FromMinutes(2);
        _touched.Clear();
        _ = _running.Add("e"); // e 는 실행 중: 게이트가 막음

        Assert.True(p.Run(Game("c"), "service start").Ran);

        Assert.Single(_touched);
        _ = p.Solar.Refresh(e, CancellationToken.None);
        Assert.Contains("of 7800 MiB ahead", _log.Lines.Last());
    }

    [Fact]
    public void AStreamFinishedShortAtTheMemoryLimitProtectsTheOtherMarkedGamesBeforeItsLadderToo()
    {
        ReadOp[] all = HotSet(50);
        (_streamer.All, _streamer.ZoneMib) = (all, 5000);
        Prewarmer p = MakePrewarmer(Game("a"), Game("b"));
        Assert.True(p.Run(Game("a"), "service start").Ran); // 끝까지 읽음
        _now += TimeSpan.FromMinutes(2);
        _touched.Clear();
        _avail = 4096 + 256 + 3000;
        _streamer.ReadMib = 1000;
        _streamer.OnProgress = progress =>
        {
            _avail = 4096 + 800;
            progress(1000 * Mib, 3000 * Mib);
        };

        Assert.True(p.Run(Game("b"), "service start").Ran);

        Assert.Equal((1000L, 5000L), p.PartialInfo("b"));
        Assert.Equal([50, 50, 10], [.. _touched.Select(t => t.Count)]); // 앞의 보호 (a), 사다리 직전의 보호 (a), 읽은 앞 1000 MiB 의 사다리
    }

    [Fact]
    public void TailsThatWouldShowZeroMibAreLeftOut()
    {
        Assert.Equal(string.Empty, new RequeueTally(0, 0, false, 700 * 1024).Note());
        Assert.Equal(string.Empty, new RequeueTally(700 * 1024, 0.1, false).Note());
        Assert.Equal(", requeued 5 MiB in 1.0 s", new RequeueTally(5 * Mib, 1.0, false, 700 * 1024).Note());
        Assert.Equal(", requeued 5 MiB in 1.0 s (2 MiB in sparse files not touched)", new RequeueTally(5 * Mib, 1.0, false, 2 * Mib).Note());
        Assert.Equal(string.Empty, new RequeueTally(0, 0, false, 0, 2, 500 * 1024, 0.1).Note());
        Assert.Equal(", protected 2 games (3 MiB in 0.1 s)", new RequeueTally(0, 0, false, 0, 2, 3 * Mib, 0.1).Note());
        Assert.Equal(string.Empty, new StreamStats(5 * Mib, 1, 0, 1) { BufferedBytes = 700 * 1024 }.BufferedNote());

        _result = _result with { SkippedBytes = 700 * 1024 };
        Solar solar = Make();
        _ = solar.Order(Game("a"), Reads, CancellationToken.None);
        _ = solar.Refresh(Game("a"), CancellationToken.None);
        Assert.All(_log.Lines, l => Assert.DoesNotContain("sparse files not touched", l));
        _result = new TouchResult(10 * Mib, 0.6, 5, 0, TouchEnd.NeedsDiskReads, 32 * Mib, 700 * 1024);
        _ = solar.Order(Game("b"), Reads, CancellationToken.None);
        Assert.EndsWith("left to the rewarm check", _log.Lines.Last());
    }
}
