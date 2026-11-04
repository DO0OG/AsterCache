using AsterCache.Core.Settings;
using AsterCache.Core.Tier;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tests.Core;
using AsterCache.Tests.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 서비스의 SSD 보조 저장 틱: 기본 꺼짐에서는 아무것도 건드리지 않고, 켜면 재배치가 끝난 (또는 재배치가 꺼진) HDD 게임을 유휴일 때 한 번에 하나씩 적용하며,
/// 끄거나 게임을 제거하거나 설정을 바꾸면 되돌립니다. 파일 시스템은 메모리 가짜이고 저널만 임시 폴더입니다.
public sealed class AutoTierTests : IDisposable
{
    private const string Plans = @"C:\p";
    private readonly TempDir _dir = new();
    private readonly FakeWorld _world = new();
    private readonly MemTierFs _fs = new();
    private readonly ListLog _log = new();
    private readonly AutoStatus _status;
    private readonly TierEngine _engine;
    private readonly AutoTier _tier;
    private readonly ReapplyGate _gate = new();
    private readonly byte[] _a = new byte[4000];
    private readonly byte[] _b = new byte[6000];

    public AutoTierTests()
    {
        new Random(3).NextBytes(_a);
        new Random(4).NextBytes(_b);
        _world.Hdd["C:"] = false; // 시스템 드라이브는 SSD, D: 는 기본값 HDD
        _status = new AutoStatus(null, _log, () => _world.Now);
        _engine = new TierEngine(_fs, _log, _dir.Combine("tier"), () => _world.Now);
        _tier = new AutoTier(_world.Env(), _status, _log, _engine);
        AddGame("Foo", @"D:\Games\Foo");
        AddGame("Bar", @"D:\Games\Bar");
    }

    public void Dispose() => _dir.Dispose();

    private static GameSettings Game(string name, string root) =>
        new(name, [name.ToLowerInvariant() + ".exe"], Path.Combine(Plans, name + ".prefetch.json"), Path.Combine(Plans, name + ".plan.json"), root, [root], "GAMES", 60);

    private readonly List<GameSettings> _games = [];

    private void AddGame(string name, string root)
    {
        GameSettings g = Game(name, root);
        _games.Add(g);
        _fs.Put(root + @"\a.bin", _a);
        _fs.Put(root + @"\b.bin", _b);
        _world.Files.Add(AutoTracer.RawTracePath(g));
    }

    private AsterSettings Settings(bool on = true, int budget = 8) => new(50, _games) { Auto = AutoSettings.Default with { TraceRuns = 1, SsdTier = new SsdTierSettings(on, budget, "auto") } };

    private void SetTrace() => _world.LoadSessions = () => [[new TraceEvent(@"D:\Games\Foo\a.bin", 0, 4000), new TraceEvent(@"D:\Games\Foo\b.bin", 0, 600), new TraceEvent(@"D:\Games\Bar\a.bin", 0, 4000)]];

    // 유휴 판정이 시작되고 (첫 틱) 유휴 창이 지난 뒤 (둘째 틱) 적용합니다.
    private void IdleTicks(AsterSettings s, bool relocateOn = false)
    {
        _tier.Tick(s, relocateOn, _gate, default);
        _world.Now += s.Auto.IdleWindow + TimeSpan.FromSeconds(1);
        _tier.Tick(s, relocateOn, _gate, default);
    }

    private static string Orig(string p) => MemTierFs.N(p + TierEngine.OrigSuffix);

    private bool Linked(string p) => _fs.Links.ContainsKey(MemTierFs.N(p));

    [Fact]
    public void DefaultOffTouchesNothingAndCreatesNoJournalFolder()
    {
        SetTrace();
        AsterSettings s = Settings(on: false);
        int mutations = _fs.Mutations;
        var before = _fs.Files.ToDictionary(kv => kv.Key, kv => kv.Value);

        IdleTicks(s);
        IdleTicks(new AsterSettings(50, _games)); // 설정에 ssd_tier 가 아예 없을 때 (AutoSettings.Default)

        Assert.Equal(mutations, _fs.Mutations);
        Assert.Equal(before.Keys.Order(), _fs.Files.Keys.Order());
        Assert.Empty(_fs.Links);
        Assert.False(Directory.Exists(_dir.Combine("tier")));
        Assert.Null(_tier.Tag("Foo"));
    }

    [Fact]
    public void EnabledAppliesOneGamePerIdlePeriodAndShowsTheTag()
    {
        SetTrace();
        AsterSettings s = Settings();

        IdleTicks(s);

        Assert.True(Linked(@"D:\Games\Foo\a.bin") && Linked(@"D:\Games\Foo\b.bin")); // 트레이스가 읽은 파일 둘 다 예산 안
        Assert.False(Linked(@"D:\Games\Bar\a.bin")); // 한 번에 한 게임
        Assert.Equal(_a, _fs.Files[Orig(@"D:\Games\Foo\a.bin")]);
        Assert.StartsWith(@"C:\AsterCache\tier\Foo\", _fs.Links[MemTierFs.N(@"D:\Games\Foo\a.bin")]);
        Assert.Equal("ssd tier 0.0 GB", _tier.Tag("Foo"));
        Assert.Contains(_log.Lines, l => l.Contains("Foo: ssd tier picked 2 file(s)"));

        IdleTicks(s); // 다음 유휴 기간에 다음 게임
        Assert.True(Linked(@"D:\Games\Bar\a.bin"));
    }

    [Fact]
    public void WaitsForRelocationToFinishUnlessRelocationIsOff()
    {
        SetTrace();
        AsterSettings s = Settings();

        IdleTicks(s, relocateOn: true);
        Assert.Empty(_fs.Links); // 재배치가 아직 끝나지 않음

        _status.Complete("Foo", "manual", _world.Now, counted: false, null);
        IdleTicks(s, relocateOn: true);
        Assert.True(Linked(@"D:\Games\Foo\a.bin"));
        Assert.False(Linked(@"D:\Games\Bar\a.bin"));
    }

    [Fact]
    public void RunningGameNonHddAndAntiCheatGamesAreLeftAlone()
    {
        SetTrace();
        AsterSettings s = Settings();
        _world.RunningExes.Add("foo.exe");
        IdleTicks(s);
        Assert.Empty(_fs.Links); // 등록 게임이 돌면 아무 게임도 시작하지 않음 (Bar 도)

        _world.RunningExes.Clear();
        _world.Hdd["D:"] = false; // 대상 드라이브가 SSD
        IdleTicks(s);
        Assert.Empty(_fs.Links);

        _world.Hdd["D:"] = true;
        _world.AntiCheats[@"D:\Games\Foo"] = "eac";
        IdleTicks(s);
        Assert.False(Linked(@"D:\Games\Foo\a.bin"));
        Assert.True(Linked(@"D:\Games\Bar\a.bin")); // 안티치트 게임만 제외
        Assert.Contains(_log.Lines, l => l.Contains("Foo: ssd tier skipped, anti-cheat found (eac)"));
    }

    [Fact]
    public void ASettingsChangeToAntiCheatBeatsTheCachedNoneAndRevertsTheActiveTier()
    {
        SetTrace();
        IdleTicks(Settings());
        Assert.True(Linked(@"D:\Games\Foo\a.bin")); // 안티치트 없음으로 캐시된 상태에서 적용됨

        _games[0] = _games[0] with { AntiCheat = "vanguard" };
        IdleTicks(Settings());

        Assert.False(Linked(@"D:\Games\Foo\a.bin"));
        Assert.Equal(_a, _fs.Files[MemTierFs.N(@"D:\Games\Foo\a.bin")]); // 원본으로 복원
    }

    [Fact]
    public void AKernelAntiCheatScanRejectsTheTierAndIsCached()
    {
        SetTrace();
        AsterSettings s = Settings();
        _world.AntiCheats[@"D:\Games\Foo"] = "kernel";
        IdleTicks(s);
        Assert.False(Linked(@"D:\Games\Foo.bin"));
        Assert.Contains(_log.Lines, l => l.Contains("Foo: ssd tier skipped, anti-cheat found (kernel)"));

        _world.AntiCheats[@"D:\Games\Foo"] = null; // 찾은 값은 캐시돼 다시 열거하지 않음 (모름과 달리)
        IdleTicks(s);
        Assert.False(Linked(@"D:\Games\Foo.bin"));
    }

    [Fact]
    public void AnUnknownAntiCheatScanRejectsTheTierAndIsNotCached()
    {
        SetTrace();
        AsterSettings s = Settings();
        _world.AntiCheats[@"D:\Games\Foo"] = AutoEnv.AntiCheatUnknown;
        IdleTicks(s);
        Assert.False(Linked(@"D:\Games\Foo\a.bin"));

        _world.AntiCheats[@"D:\Games\Foo"] = null; // 다음 탐지는 없음으로 나옴: 앞의 모름이 캐시되지 않았어야 함
        IdleTicks(s);
        Assert.True(Linked(@"D:\Games\Foo\a.bin"));
    }

    [Theory]
    [InlineData("kernel")]
    [InlineData("ricochet")]
    [InlineData(AutoEnv.AntiCheatUnknown)]
    public void AnActiveTierWithoutARegisteredValueIsRevertedWhenTheFolderShowsAnAntiCheat(string cheat)
    {
        SetTrace();
        AsterSettings s = Settings();
        IdleTicks(s);
        Assert.True(Linked(@"D:\Games\Foo\a.bin")); // 저장값 없이, 탐지 없음으로 적용됨

        _world.AntiCheats[@"D:\Games\Foo"] = cheat; // 적용 뒤 폴더에 마커가 생김
        _tier.Tick(s, false, _gate, default);

        Assert.False(Linked(@"D:\Games\Foo\a.bin"));
        Assert.Equal(_a, _fs.Files[MemTierFs.N(@"D:\Games\Foo\a.bin")]); // 원본으로 복원

        IdleTicks(s);
        Assert.False(Linked(@"D:\Games\Foo\a.bin")); // 다시 적용하지 않음
    }

    [Fact]
    public void ADetectedAntiCheatKeepsWhatTheGameWroteThroughTheLinkBeforeRestoring()
    {
        SetTrace();
        AsterSettings s = Settings();
        IdleTicks(s);
        string game = @"D:\Games\Foo\a.bin";
        Assert.True(Linked(game));
        byte[] written = new byte[_a.Length]; // 게임이 같은 크기로 링크 대상을 수정하고 종료함
        new Random(9).NextBytes(written);
        _fs.Put(_fs.Links[MemTierFs.N(game)], written);

        _world.AntiCheats[@"D:\Games\Foo"] = "kernel";
        _tier.Tick(s, false, _gate, default);

        Assert.False(Linked(game));
        Assert.Equal(written, _fs.Files[MemTierFs.N(game)]); // 변경분 보존
        Assert.Equal(_a, _fs.Files[Orig(game)]); // 옛 원본은 지우지 않음
    }

    [Fact]
    public void ARevertKeepsWhatTheGameWroteEvenWhenTheJournalCannotBeSaved()
    {
        SetTrace();
        AsterSettings s = Settings();
        IdleTicks(s);
        string game = @"D:\Games\Foo\a.bin";
        byte[] written = new byte[_a.Length];
        new Random(9).NextBytes(written);
        _fs.Put(_fs.Links[MemTierFs.N(game)], written);
        string journal = _engine.JournalPath("Foo");
        File.SetAttributes(journal, FileAttributes.ReadOnly); // 저널 저장이 실패하도록 함
        try
        {
            _ = _engine.Revert("Foo", "test");
        }
        finally
        {
            File.SetAttributes(journal, FileAttributes.Normal);
        }

        Assert.False(Linked(game));
        Assert.Equal(written, _fs.Files[MemTierFs.N(game)]); // 변경분 보존
        Assert.Equal(_a, _fs.Files[Orig(game)]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFailingCheckKeepsThatFileAndStillRevertsTheRest(bool statFails)
    {
        SetTrace();
        AsterSettings s = Settings();
        IdleTicks(s);
        string bad = @"D:\Games\Foo\a.bin", good = @"D:\Games\Foo\b.bin";
        string target = _fs.Links[MemTierFs.N(bad)];
        _fs.Faulty.Add(MemTierFs.N(statFails ? target : bad));

        TierRevertReport r = _engine.Revert("Foo", "test");

        Assert.Equal(1, r.Failed);
        Assert.Equal(1, r.Reverted);
        Assert.True(Linked(bad)); // 검사에 실패한 항목은 링크·사본 그대로
        Assert.Equal(_a, _fs.Files[Orig(bad)]);
        Assert.False(Linked(good));
        Assert.Equal(_b, _fs.Files[MemTierFs.N(good)]);
    }

    [Fact]
    public void GameRunningOnAnotherDriveDoesNotBlockTiering()
    {
        SetTrace();
        AddGame("Qux", @"E:\Games\Qux");
        _world.RunningExes.Add("qux.exe");

        IdleTicks(Settings());

        Assert.True(Linked(@"D:\Games\Foo\a.bin"));
    }

    [Fact]
    public void NoSsdFoundSkipsQuietlyWithTheReasonLoggedOnce()
    {
        SetTrace();
        _world.Hdd["C:"] = true;
        AsterSettings s = Settings();

        IdleTicks(s);
        IdleTicks(s);

        Assert.Empty(_fs.Links);
        Assert.Equal(0, _fs.Mutations);
        Assert.Single(_log.Lines, l => l.Contains("ssd tier skipped: no SSD found"));
    }

    [Fact]
    public void DisablingOrRemovingTheGameOrChangingTheBudgetRevertsTheLinks()
    {
        SetTrace();
        IdleTicks(Settings());
        IdleTicks(Settings());
        Assert.True(Linked(@"D:\Games\Foo\a.bin") && Linked(@"D:\Games\Bar\a.bin"));

        _tier.Tick(Settings(budget: 4), false, _gate, default); // 예산이 달라짐: Foo·Bar 의 링크를 되돌림 (다시 적용은 유휴 뒤)
        Assert.Empty(_fs.Links);
        Assert.Equal(_a, _fs.Files[MemTierFs.N(@"D:\Games\Foo\a.bin")]);

        IdleTicks(Settings(budget: 4));
        IdleTicks(Settings(budget: 4));
        Assert.True(Linked(@"D:\Games\Foo\a.bin") && Linked(@"D:\Games\Bar\a.bin"));

        AsterSettings withoutBar = new(50, [_games[0]]) { Auto = Settings(budget: 4).Auto };
        _tier.Tick(withoutBar, false, _gate, default); // 게임 제거
        Assert.False(Linked(@"D:\Games\Bar\a.bin"));
        Assert.Equal(_a, _fs.Files[MemTierFs.N(@"D:\Games\Bar\a.bin")]);
        Assert.True(Linked(@"D:\Games\Foo\a.bin"));

        _tier.Tick(Settings(on: false), false, _gate, default); // 꺼짐
        Assert.Empty(_fs.Links);
        Assert.Equal(_b, _fs.Files[MemTierFs.N(@"D:\Games\Foo\b.bin")]);
        Assert.Null(_tier.Tag("Foo"));
        Assert.True(_fs.Files.ContainsKey(MemTierFs.N(@"C:\AsterCache\tier\Foo\Games\Foo\a.bin"))); // 사본은 남음

        int mutations = _fs.Mutations;
        _tier.Tick(Settings(on: false), false, _gate, default);
        Assert.Equal(mutations, _fs.Mutations); // 이미 되돌렸으면 조용
    }

    [Fact]
    public void RevertIsDeferredWhileAGameOnTheSameDiskRunsAndLogsTheReasonOnce()
    {
        SetTrace();
        IdleTicks(Settings());
        Assert.True(Linked(@"D:\Games\Foo\a.bin"));
        _world.RunningExes.Add("bar.exe"); // Foo 와 같은 D: 의 다른 등록 게임

        _tier.Tick(Settings(on: false), false, _gate, default); // 꺼짐
        _tier.Tick(Settings(on: false), false, _gate, default);

        Assert.True(Linked(@"D:\Games\Foo\a.bin")); // 링크로 열린 파일이 있을 수 있어 미룸
        Assert.Single(_log.Lines, l => l.Contains("Foo: ssd tier revert deferred (disabled)"));

        AsterSettings withoutFoo = new(50, [_games[1]]) { Auto = Settings(on: false).Auto };
        _tier.Tick(withoutFoo, false, _gate, default); // 게임 제거도 마찬가지
        Assert.True(Linked(@"D:\Games\Foo\a.bin"));

        _world.RunningExes.Clear();
        _tier.Tick(Settings(on: false), false, _gate, default);
        Assert.Empty(_fs.Links);
    }

    [Fact]
    public void RevertForLowFreeSpaceIsDeferredWhileASameDiskGameRuns()
    {
        SetTrace();
        AsterSettings s = Settings();
        IdleTicks(s);
        _fs.Free = TierEngine.MinFreeFor(_fs.Total) - 1;
        _world.RunningExes.Add("bar.exe");

        _tier.Tick(s, false, _gate, default);

        Assert.True(Linked(@"D:\Games\Foo\a.bin"));
        _world.RunningExes.Clear();
        _tier.Tick(s, false, _gate, default);
        Assert.False(Linked(@"D:\Games\Foo\a.bin"));
    }

    [Fact]
    public void LowFreeSpaceRevertsTheOldestGameFirstAndStopsApplying()
    {
        SetTrace();
        AsterSettings s = Settings();
        IdleTicks(s); // Foo
        _world.Now += TimeSpan.FromHours(1);
        IdleTicks(s); // Bar (더 나중)
        _fs.Free = TierEngine.MinFreeFor(_fs.Total) - 1;

        _tier.Tick(s, false, _gate, default);

        Assert.False(Linked(@"D:\Games\Foo\a.bin")); // 가장 오래된 게임부터
        Assert.True(Linked(@"D:\Games\Bar\a.bin"));
        Assert.Contains(_log.Lines, l => l.Contains("Foo: ssd tier revert (ssd free space low)"));
    }

    [Fact]
    public void RevertGameClearsTheLinksAndLetsTheGameBeTieredAgain()
    {
        SetTrace();
        AsterSettings s = Settings();
        IdleTicks(s);
        Assert.True(_tier.IsActive("Foo"));

        _tier.RevertGame("Foo", "game updated");

        Assert.False(_tier.IsActive("Foo"));
        Assert.Empty(_fs.Links);
        Assert.Null(_tier.Tag("Foo"));
        IdleTicks(s);
        Assert.True(Linked(@"D:\Games\Foo\a.bin"));
    }

    [Fact]
    public void RelocatorTickDrivesTheTierAndTagsTheStatusColumn()
    {
        SetTrace();
        AsterSettings s = Settings();
        foreach (GameSettings g in _games)
        {
            _status.Complete(g.Name, "manual", _world.Now, counted: false, null); // 재배치가 끝난 게임
        }

        var relocator = new AutoRelocator(_world.Env(), _status, _log, () => s, null, _tier);

        relocator.Tick(_gate, default);
        Assert.Empty(_fs.Links); // 첫 틱은 유휴 판정을 시작할 뿐
        _world.Now += s.Auto.IdleWindow + TimeSpan.FromSeconds(1);
        relocator.Tick(_gate, default);

        Assert.True(Linked(@"D:\Games\Foo\a.bin"));
        Assert.Equal("ssd tier 0.0 GB", _status.Get("Foo")!.Detail); // 상태 열 표시 (영어 상수, 트레이가 번역)
        Assert.Null(_status.Get("Bar")!.Detail);
    }
}
