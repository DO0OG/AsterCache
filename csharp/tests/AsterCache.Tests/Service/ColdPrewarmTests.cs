using AsterCache.Core.Json;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Profiles;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 기록 없는 게임의 통째·프로필 예열 (Prewarmer): 대상 판정, 상한·예산, 건너뜀 이유, 취소, 기록이 생기면 핫 집합으로 전환. 읽기는 가짜, 계획은 가짜 또는 임시 폴더의 실제 열거.
public sealed class ColdPrewarmTests : IDisposable
{
    private const long Mib = 1L << 20;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_cold_" + Guid.NewGuid().ToString("N"));
    private readonly FakeStreamer _streamer = new();
    private readonly ListLog _log = new();
    private readonly List<(long Full, long Profile)> _caps = [];
    private readonly List<IReadOnlyList<ReadOp>> _reads = [];
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;

    public ColdPrewarmTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private bool GameRunning { get; set; }

    private long AvailableMib { get; set; } = 16000;

    private long PhysicalBytes { get; set; } = 64L << 30;

    private double? Disk { get; set; } = 0;

    private bool TraceExists { get; set; }

    private long? ZoneMib { get; set; }

    private bool RawTraceExists { get; set; }

    /// 읽기 가짜가 돌려줄 바이트 (null 이면 계획의 합).
    private long? ReadBytes { get; set; }

    private bool ReadCompletes { get; set; } = true;

    private string? Refusal { get; set; }

    /// 부트스트랩 S2 (프리페치 기록으로 boot 트레이스 만들기) 가짜: 부른 횟수와 결과. 만들었으면 true 를 돌려주고 원시·boot 트레이스가 생긴 것으로 침.
    private bool PrefetchMakesBoot { get; set; }

    private int _bootAsked;

    private int _made;

    private double? ProbeMs { get; set; } = 0.5;

    private readonly List<IReadOnlyList<(string Path, long Offset, int Bytes)>> _probed = [];

    private Func<GameSettings, long, long, CancellationToken, ColdPlan> Planner { get; set; } = (_, _, _, _) => new ColdPlan("full", [new ReadOp(@"D:\g\a.bin", 0, 10 * Mib)], 10 * Mib, null);

    private Prewarmer Make(AutoSettings? auto = null, params GameSettings[] games)
    {
        var p = new Prewarmer(games, _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => AvailableMib,
            PhysicalRamBytes = () => PhysicalBytes,
            GameRunning = _ => GameRunning,
            ZoneBytes = _ => ZoneMib is { } z ? z * Mib : null,
            TraceExists = _ => TraceExists,
            RawTraceExists = _ => RawTraceExists,
            DiskBytesPerSecond = _ => Disk,
            ColdRefusal = _ => Refusal,
            PrefetchBoot = _ =>
            {
                _bootAsked++;
                RawTraceExists |= PrefetchMakesBoot;
                return PrefetchMakesBoot;
            },
            ProbeSamples = samples =>
            {
                _probed.Add(samples);
                return ProbeMs is { } ms ? new ProbeResult(8, ms > CacheProbe.ColdThresholdMs ? 8 : 0, ms) : null;
            },
            ColdPlanner = (g, full, profile, ct) =>
            {
                _caps.Add((full, profile));
                return Planner(g, full, profile, ct);
            },
            ColdRead = (ops, _) =>
            {
                _reads.Add(ops);
                return (new StreamStats(ReadBytes ?? ops.Sum(o => o.Length), ops.Count, 0, 0.5), ReadCompletes);
            },
        }, _log, Path.Combine(_dir, _made++ == 0 ? "status.json" : $"status{_made}.json"), () => _now); // 처음 것만 status.json: 같은 파일을 쓰면 다음 Prewarmer 가 이전 예열 기록을 이어받음 (재시작 시험은 따로 있음)
        p.SetAuto(auto ?? AutoSettings.Default);
        return p;
    }

    private static GameSettings Game(string name, int minFree = 1000) =>
        Games.Make(name, name + ".exe") with { Prewarm = PrewarmSettings.Default with { MinFreeRamMib = minFree } };

    private Prewarmer MakeOne(AutoSettings? auto = null, int minFree = 1000) => Make(auto, Game("g", minFree));

    // ---- 대상 판정 ----

    [Fact]
    public void GameWithoutAnyTraceIsWarmedAndTheResultNamesTheMode()
    {
        Prewarmer p = MakeOne();

        PrewarmResult r = p.Run(Game("g"), "service start");

        Assert.True(r.Ran);
        Assert.Equal("full", r.Mode);
        Assert.Equal(10 * Mib, r.Stats!.Bytes);
        Assert.Empty(_streamer.Calls); // 핫 집합 스트림은 부르지 않음
        Assert.NotNull(p.LastWarmed("g"));
        Assert.Contains(_log.Lines, l => l.Contains("prewarm done (service start, full)", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("prewarm done (service start, full)", StringComparison.Ordinal) && l.Contains(" hint MB/s,", StringComparison.Ordinal));
        var g = (JObject)((JObject)((JObject)JsonRead.ReadFile(Path.Combine(_dir, "status.json"))!)["games"]!)["g"]!;
        Assert.Equal(("ok", "full"), (g["result"], g["mode"]));
    }

    [Fact]
    public void PrefetchRecordIsTriedBeforeTheColdWarmAndWinsWhenItMakesABootTrace()
    {
        PrefetchMakesBoot = true;
        Prewarmer p = MakeOne();

        PrewarmResult r = p.Run(Game("g"), "service start");

        Assert.Equal(1, _bootAsked);
        Assert.Empty(_caps); // S3 (통째·프로필 예열) 은 계획조차 만들지 않음
        Assert.Empty(_reads);
        Assert.False(r.Ran); // 계획이 만들어지기 전이라 핫 집합 예열은 아직 (자동 모드가 계획한 뒤 새 트레이스로 예열)
    }

    [Fact]
    public void WithoutAPrefetchRecordTheColdWarmStillRuns()
    {
        Prewarmer p = MakeOne();

        PrewarmResult r = p.Run(Game("g"), "service start");

        Assert.Equal(1, _bootAsked);
        Assert.True(r.Ran);
        Assert.Equal("full", r.Mode);
    }

    [Fact]
    public void PrefetchRecordIsNotTriedOnARefusedDriveOrWhenTheGameAlreadyHasATrace()
    {
        PrefetchMakesBoot = true;
        Refusal = "not an HDD";
        MakeOne().Run(Game("g"), "t");
        Assert.Equal(0, _bootAsked);

        Refusal = null;
        RawTraceExists = true;
        MakeOne().Run(Game("g"), "t");
        Assert.Equal(0, _bootAsked);
    }

    [Fact]
    public void ColdWarmedEventCarriesTheProfileLabel()
    {
        Planner = (_, _, _, _) => new ColdPlan("profile Unity+Source", [new ReadOp(@"D:\g\a.bin", 0, Mib)], Mib, null);
        Prewarmer p = MakeOne();
        var seen = new List<(string, string)>();
        p.ColdWarmed += (g, label) => seen.Add((g.Name, label));

        p.Run(Game("g"), "t");

        Assert.Equal([("g", "profile Unity+Source")], seen);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AnyTraceFileMeansTheHotSetPathNotTheFolder(bool trace, bool raw)
    {
        (TraceExists, RawTraceExists) = (trace, raw);
        Prewarmer p = MakeOne();

        Assert.False(p.IsCold(Game("g")));
        PrewarmResult r = p.Run(Game("g"), "t");

        Assert.Empty(_caps); // 폴더를 열거하지 않음
        Assert.Equal("no plan file", r.SkipReason); // ZoneBytes 가 null 이라 기존 이유
        Assert.Null(r.Mode);
    }

    [Fact]
    public void FeatureSwitchesAndAutoModeTurnItOff()
    {
        Assert.False(MakeOne(AutoSettings.Default with { FullPrewarm = false, EngineProfiles = false }).IsCold(Game("g")));
        Assert.False(MakeOne(AutoSettings.Default with { Enabled = false }).IsCold(Game("g")));
        Assert.True(MakeOne(AutoSettings.Default with { FullPrewarm = false }).IsCold(Game("g")));
        Assert.True(MakeOne(AutoSettings.Default with { EngineProfiles = false }).IsCold(Game("g")));
    }

    [Fact]
    public void TraceAppearingSwitchesTheSameGameToTheHotSetPathAndForgetsTheColdAmount()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        Prewarmer p = Make(AutoSettings.Default with { FullPrewarmMaxMib = 100000 }, a, b);
        ReadBytes = 3000 * Mib;
        Assert.Equal("full", p.Run(a, "first").Mode);
        _caps.Clear();
        p.Run(b, "second");
        Assert.Equal((AvailableMib - 1000 - 3000) * Mib, _caps.Single().Full); // a 가 올린 3000 MiB 를 뺌

        (TraceExists, ZoneMib) = (true, 100); // 기록과 계획이 생김
        _caps.Clear();
        PrewarmResult hot = p.Run(a, "new trace", ignoreCooldown: true);
        Assert.True(hot.Ran);
        Assert.Null(hot.Mode);
        Assert.Single(_streamer.Calls);
        Assert.Empty(_caps); // 폴더를 열거하지 않음

        (TraceExists, ZoneMib) = (false, null);
        _now += TimeSpan.FromHours(1);
        p.Run(b, "third");
        Assert.Equal((AvailableMib - 1000) * Mib, _caps.Single().Full); // 핫 집합이 된 a 는 더 이상 세지 않음 (b 자신의 이전 양도 제외)
    }

    // ---- 건너뜀 이유 ----

    [Fact]
    public void GameRunningCooldownAndBusyDriveSkipWithoutEnumeratingTheFolder()
    {
        Prewarmer p = MakeOne();

        GameRunning = true;
        Assert.Equal("game is running", p.Run(Game("g"), "t").SkipReason);
        GameRunning = false;

        Disk = 4.0 * Mib; // BusyBytesPerSecond 와 같은 4 MiB/s
        Assert.Equal("drive busy", p.Run(Game("g"), "t").SkipReason);
        Disk = null; // 읽지 못하면 바쁜 것으로 봄
        Assert.Equal("drive busy", p.Run(Game("g"), "t").SkipReason);
        Disk = 4.0 * Mib - 1;
        Assert.Empty(_caps);

        Assert.True(p.Run(Game("g"), "t").Ran);
        Assert.StartsWith("cooldown", p.Run(Game("g"), "t", ignoreCooldown: true).SkipReason, StringComparison.Ordinal); // 쿨다운은 항상 지킴
        _now += TimeSpan.FromMinutes(31);
        Assert.True(p.Run(Game("g"), "t").Ran);
    }

    [Fact]
    public void LowMemorySkipsWhenNothingIsLeftAfterTheReserve()
    {
        AvailableMib = 1000;
        Prewarmer p = MakeOne(minFree: 1000);

        PrewarmResult r = p.Run(Game("g"), "t");

        Assert.False(r.Ran);
        Assert.StartsWith("low memory", r.SkipReason, StringComparison.Ordinal);
        Assert.Empty(_caps);
    }

    [Fact]
    public void PlannerSkipReasonIsPassedThrough()
    {
        Planner = (_, _, _, _) => ColdPlan.None("no engine profile matched");
        Prewarmer p = MakeOne();

        PrewarmResult r = p.Run(Game("g"), "t");

        Assert.Equal((false, "no engine profile matched"), (r.Ran, r.SkipReason));
        Assert.Null(p.LastWarmed("g"));
        Assert.Empty(_reads);
    }

    [Fact]
    public void MissingFolderAndPlannerFailuresAreSkippedOrFailedWithoutThrowing()
    {
        Planner = (_, _, _, _) => throw new DirectoryNotFoundException();
        Prewarmer p = MakeOne();
        Assert.Equal("install folder not found", p.Run(Game("g"), "t").SkipReason);

        Planner = (_, _, _, _) => throw new UnauthorizedAccessException();
        Assert.Equal("failed: UnauthorizedAccessException", p.Run(Game("g"), "t").SkipReason);
    }

    // ---- 상한과 예산 ----

    [Fact]
    public void AutoBudgetIsTheSmallerOfAQuarterOfRamAndAvailableMinusReserve()
    {
        (AvailableMib, PhysicalBytes) = (3000, 64L << 30); // 가용 - 여유 = 2000 < 16384
        MakeOne(minFree: 1000).Run(Game("g"), "t");
        Assert.Equal([(1024 * Mib, 2000 * Mib)], _caps); // 통째 상한 기본 1024 MiB, 프로필은 RAM 예산

        _caps.Clear();
        (AvailableMib, PhysicalBytes) = (60000, 16L << 30); // 물리 RAM / 4 = 4096 < 59000
        MakeOne(minFree: 1000).Run(Game("g"), "t");
        Assert.Equal([(1024 * Mib, 4096 * Mib)], _caps);
    }

    [Fact]
    public void ExplicitMaxCapsTheWholeFolderLimitAndTheProfileButNeverExceedsAvailableMemory()
    {
        AvailableMib = 3000;
        MakeOne(AutoSettings.Default with { FullPrewarmMaxMib = 500 }).Run(Game("g"), "t");
        MakeOne(AutoSettings.Default with { FullPrewarmMaxMib = 100000 }).Run(Game("g"), "t");

        Assert.Equal([(500 * Mib, 500 * Mib), (2000 * Mib, 2000 * Mib)], _caps);
    }

    [Fact]
    public void SwitchesSelectTheCapsPassedToThePlanner()
    {
        AvailableMib = 3000;
        MakeOne(AutoSettings.Default with { FullPrewarm = false }).Run(Game("g"), "t");
        MakeOne(AutoSettings.Default with { EngineProfiles = false }).Run(Game("g"), "t");

        Assert.Equal([(0L, 2000 * Mib), (1024 * Mib, 0L)], _caps);
    }

    [Fact]
    public void BudgetIsSharedWithOtherGamesWarmedInTheSameBatchAndLater()
    {
        AvailableMib = 3000; // 예산 2000 MiB
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        GameSettings c = Game("c");
        Prewarmer p = Make(null, a, b, c);
        ReadBytes = 1500 * Mib;

        IReadOnlyList<PrewarmResult> r = p.RunAll([a, b, c], "batch");

        Assert.Equal([(1024 * Mib, 2000 * Mib), (500 * Mib, 500 * Mib)], _caps); // 통째 상한 기본 1024 MiB. c 는 예산이 없어 계획까지 가지 않음
        Assert.Equal([true, true, false], r.Select(x => x.Ran));
        Assert.Equal("batch memory budget", r[2].SkipReason);
    }

    [Fact]
    public void ColdAmountCountsAgainstTheHotSetBatchBudget()
    {
        AvailableMib = 20000;
        PhysicalBytes = 16L << 30; // 4096 MiB
        GameSettings cold = Game("cold");
        GameSettings hot = Game("hot");
        var p = new Prewarmer([cold, hot], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => AvailableMib,
            PhysicalRamBytes = () => PhysicalBytes,
            GameRunning = _ => false,
            ZoneBytes = g => g.Name == "hot" ? 3000 * Mib : null,
            TraceExists = g => g.Name == "hot",
            ColdPlanner = (_, _, _, _) => new ColdPlan("full", [new ReadOp(@"D:\g\a.bin", 0, 2000 * Mib)], 2000 * Mib, null),
            ColdRead = (ops, _) => (new StreamStats(2000 * Mib, 1, 0, 1), true),
        }, _log, null, () => _now);

        IReadOnlyList<PrewarmResult> r = p.RunAll([cold, hot], "batch");

        Assert.Equal([true, false], r.Select(x => x.Ran)); // 2000 + 3000 > 4096
        Assert.Equal("batch memory budget", r[1].SkipReason);
    }

    // ---- 실제 열거로 상한 판정 (Windows 임시 폴더, 가짜 읽기) ----

    private static GameSettings RealGame(string root) => Game("g") with { OriginRoot = root, AllowedRoots = [root], ExeNames = ["Game.exe"] };

    private Prewarmer MakeReal(AutoSettings auto, string root)
    {
        var p = new Prewarmer([RealGame(root)], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 100000,
            PhysicalRamBytes = () => 1000L << 30,
            GameRunning = _ => false,
            ZoneBytes = _ => null,
            TraceExists = _ => false,
            DiskBytesPerSecond = _ => 0,
            ColdPlanner = (g, full, profile, ct) => FolderWarm.Plan(g.OriginRoot, g.ExeNames, full, profile, EngineProfiles.Builtin, ct),
            ColdRead = (ops, _) =>
            {
                _reads.Add(ops);
                return (new StreamStats(ops.Sum(o => o.Length), ops.Count, 0, 0.1), true);
            },
        }, _log, null, () => _now);
        p.SetAuto(auto);
        return p;
    }

    private string UnityFolder()
    {
        string root = Path.Combine(_dir, "unity");
        foreach ((string rel, long size) in new[]
        {
            ("Game.exe", 1L), ("UnityPlayer.dll", 1 * Mib), ("Game_Data/globalgamemanagers", 1 * Mib), ("Game_Data/resources.assets", 100 * Mib),
            ("Game_Data/StreamingAssets/movie.bin", 50 * Mib),
        })
        {
            string path = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using FileStream fs = File.Create(path);
            fs.SetLength(size);
        }

        return root;
    }

    [Fact]
    public void SmallFolderIsWarmedWholeAndLargeFolderByTheEngineProfile()
    {
        string root = UnityFolder(); // 합 152 MiB + 1 B

        PrewarmResult whole = MakeReal(AutoSettings.Default with { FullPrewarmMaxMib = 200 }, root).Run(RealGame(root), "t");
        PrewarmResult profile = MakeReal(AutoSettings.Default with { FullPrewarmMaxMib = 110 }, root).Run(RealGame(root), "t");
        PrewarmResult noProfile = MakeReal(AutoSettings.Default with { FullPrewarmMaxMib = 110, EngineProfiles = false }, root).Run(RealGame(root), "t");

        Assert.Equal(("full", 152 * Mib + 1), (whole.Mode, whole.Stats!.Bytes));
        Assert.Equal("profile Unity", profile.Mode);
        Assert.Equal(102 * Mib, profile.Stats!.Bytes); // movie.bin 은 빠짐, exe 는 공통 규칙 아님
        Assert.DoesNotContain(_reads[1], o => o.Path.EndsWith("movie.bin", StringComparison.Ordinal));
        Assert.False(noProfile.Ran);
        Assert.Contains("larger", noProfile.SkipReason, StringComparison.Ordinal);
    }

    [Fact]
    public void ProfileBudgetCutsTheTotal()
    {
        string root = UnityFolder();

        PrewarmResult r = MakeReal(AutoSettings.Default with { FullPrewarmMaxMib = 10 }, root).Run(RealGame(root), "t");

        Assert.Equal(("profile Unity", 10 * Mib), (r.Mode, r.Stats!.Bytes));
    }

    // ---- 취소 ----

    [Fact]
    public void CancelledBeforeStartDoesNotEnumerateOrRead()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Prewarmer p = MakeOne();

        PrewarmResult r = p.Run(Game("g"), "t", ct: cts.Token);

        Assert.Equal((false, Prewarmer.Cancelled), (r.Ran, r.SkipReason));
        Assert.Empty(_caps);
        Assert.Empty(_reads);
    }

    [Fact]
    public void CancelledWhileReadingIsNotCountedAsWarmed()
    {
        ReadCompletes = false; // 읽는 중 게임이 시작돼 끊김
        ReadBytes = 3 * Mib;
        Prewarmer p = MakeOne();

        PrewarmResult r = p.Run(Game("g"), "t");

        Assert.Equal((false, Prewarmer.Cancelled), (r.Ran, r.SkipReason));
        Assert.Equal(3 * Mib, r.Stats!.Bytes);
        Assert.Null(p.LastWarmed("g")); // 다시 시도할 수 있게
        Assert.Contains(_log.Lines, l => l.Contains("prewarm cancelled", StringComparison.Ordinal));
    }

    [Fact]
    public void PlannerCancellationIsReportedAsCancelled()
    {
        Planner = (_, _, _, _) => throw new OperationCanceledException();

        Assert.Equal(Prewarmer.Cancelled, MakeOne().Run(Game("g"), "t").SkipReason);
    }

    // ---- 배치 진입점 ----

    [Fact]
    public void ServiceStartAndTriggerWarmColdGamesAndPassTheCancelToken()
    {
        using var cts = new CancellationTokenSource();
        GameSettings g = Game("g") with { Prewarm = PrewarmSettings.Default with { TriggerExes = ["steam.exe"] } };
        CancellationToken seen = default;
        Planner = (_, _, _, ct) =>
        {
            seen = ct;
            return new ColdPlan("full", [new ReadOp(@"D:\g\a.bin", 0, Mib)], Mib, null);
        };
        Prewarmer p = Make(null, g);

        Assert.True(Assert.Single(p.RunServiceStart(null, cts.Token)).Ran);
        Assert.Equal(cts.Token, seen);

        _now += TimeSpan.FromHours(1);
        Assert.True(Assert.Single(p.OnProcessStart(new ProcessStart(1, "steam.exe", 0), null, cts.Token)).Ran);
    }

    // ---- 드라이브 거부 (시스템 볼륨·SSD·판정 불가) ----

    [Theory]
    [InlineData("system volume")]
    [InlineData("not an HDD")]
    [InlineData("drive type unknown")]
    public void RefusedDriveIsNotWarmedOnServiceStartOrTriggerAndTheReasonIsLoggedOncePerGame(string reason)
    {
        Refusal = reason;
        GameSettings g = Game("g") with { Prewarm = PrewarmSettings.Default with { TriggerExes = ["steam.exe"] } };
        Prewarmer p = Make(null, g);

        PrewarmResult start = Assert.Single(p.RunServiceStart());
        PrewarmResult trigger = Assert.Single(p.OnProcessStart(new ProcessStart(1, "steam.exe", 0)));

        Assert.False(start.Ran);
        Assert.Equal(reason, start.SkipReason);
        Assert.Equal(reason, trigger.SkipReason);
        Assert.Empty(_caps);
        Assert.Empty(_reads);
        Assert.Null(p.LastWarmed("g"));
        Assert.Single(_log.Lines, l => l.Contains("prewarm without a record skipped", StringComparison.Ordinal));
    }

    // ---- 기록 없이 예열한 게임의 재예열 ----

    private static AutoSettings RewarmAuto => AutoSettings.Default with { RewarmMinutes = 10 };

    [Fact]
    public void ColdWarmedGameIsProbedFromItsOwnFileListAndRewarmedWhenCold()
    {
        Planner = (_, _, _, _) => new ColdPlan("full", [.. Enumerable.Range(0, 20).Select(i => new ReadOp($@"D:\g{i}.bin", 0, Mib))], 20 * Mib, null);
        Prewarmer p = MakeOne();
        p.Run(Game("g"), "t");
        _reads.Clear();
        _caps.Clear();

        p.Tick(RewarmAuto);
        _now += TimeSpan.FromMinutes(10);
        ProbeMs = 0.5; // 따뜻함
        p.Tick(RewarmAuto);
        Assert.Empty(_reads);
        IReadOnlyList<(string Path, long Offset, int Bytes)> samples = Assert.Single(_probed);
        Assert.Equal(8, samples.Count);
        Assert.Equal(8, samples.Select(s => s.Path).Distinct().Count()); // 고르게 8 곳

        _now += TimeSpan.FromMinutes(31); // 쿨다운(30 분)이 지난 뒤
        ProbeMs = 9; // 식음
        p.Tick(RewarmAuto);
        Assert.Single(_reads); // 같은 기록 없는 경로로 다시 예열 
        Assert.Single(_caps);
    }

    [Fact]
    public void ColdRewarmKeepsTheSafetyRules()
    {
        Prewarmer p = MakeOne();
        p.Run(Game("g"), "t");
        _reads.Clear();
        ProbeMs = 9;

        GameRunning = true; // 게임 실행 중이면 점검 자체를 안 함
        p.Tick(RewarmAuto);
        _now += TimeSpan.FromMinutes(10);
        p.Tick(RewarmAuto);
        Assert.Empty(_probed);

        GameRunning = false;
        Disk = 100 << 20; // 드라이브가 바쁘면 점검 안 함
        _now += TimeSpan.FromMinutes(10);
        p.Tick(RewarmAuto);
        Assert.Empty(_probed);

        Disk = 0;
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 취소되면 읽지 않음
        _now += TimeSpan.FromMinutes(10);
        p.Tick(RewarmAuto, cts.Token);
        Assert.Empty(_reads);
    }

    [Fact]
    public void RefusedDriveIsNotRewarmed()
    {
        Prewarmer p = MakeOne();
        p.Run(Game("g"), "t");
        _reads.Clear();
        ProbeMs = 9;
        Refusal = "not an HDD";

        p.Tick(RewarmAuto);
        _now += TimeSpan.FromMinutes(10);
        p.Tick(RewarmAuto);

        Assert.Empty(_reads);
    }

    [Fact]
    public void SetGamesForgetsColdAmountsOfRemovedGames()
    {
        GameSettings a = Game("a");
        GameSettings b = Game("b");
        Prewarmer p = Make(AutoSettings.Default with { FullPrewarmMaxMib = 100000 }, a, b);
        ReadBytes = 1000 * Mib;
        p.Run(a, "t");
        p.SetGames([b]);
        _caps.Clear();

        p.Run(b, "t");

        Assert.Equal((AvailableMib - 1000) * Mib, _caps.Single().Full); // a 의 1000 MiB 는 더 이상 세지 않음
    }
}
