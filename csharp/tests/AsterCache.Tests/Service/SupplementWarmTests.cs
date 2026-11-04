using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 시작 예열이 상한으로 일부만 읽은 핫 집합을 유휴 때 한 번 보충하는가 (상태 기록, 취소, 예산, 재예열 점검, 재시작).
public sealed class SupplementWarmTests : IDisposable
{
    private const long Mib = 1L << 20;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_supp_" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly CapStreamer _streamer = new();
    private DateTimeOffset _now = new(2026, 11, 4, 12, 0, 0, TimeSpan.Zero);
    private bool _running;
    private bool _relocating;
    private double? _disk = 0;
    private long _avail = 16000;
    private ProbeResult? _probe;

    public SupplementWarmTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string StatusPath => Path.Combine(_dir, "prewarm_status.json");

    private sealed class CapStreamer : IGameStreamer
    {
        public List<GameSettings> Seen { get; } = [];

        public Func<GameSettings, bool>? OnStream { get; set; }

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) Stream(GameSettings game, CancellationToken stop, Action<long, long>? progress)
        {
            Seen.Add(game);
            bool go = OnStream?.Invoke(game) ?? true;
            return (new StreamStats(Math.Min(100L, game.MaxStreamMib) * Mib, 1, 0, 0.01), go && !stop.IsCancellationRequested);
        }
    }

    // 핫 집합 100 MiB, 실제 환경처럼 max_stream_mib 로 자른 값을 돌려줌.
    private Prewarmer Make(GameSettings game, string? statusPath = null)
    {
        var p = new Prewarmer([game], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = _ => _running,
            ZoneBytes = g => Math.Min(100L, g.MaxStreamMib) * Mib,
            TraceExists = _ => true,
            DiskBytesPerSecond = _ => _disk,
            ProbeReadMs = _ => _probe,
        }, _log, statusPath ?? StatusPath, () => _now);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 40 });
        p.Relocating = _ => _relocating;
        return p;
    }

    private static GameSettings Game() => Games.Make("g", "g.exe");

    private static readonly AutoSettings NoRewarm = AutoSettings.Default with { Enabled = false };

    // 30 초 간격 틱 n 번.
    private void Ticks(Prewarmer p, int n, AutoSettings? auto = null)
    {
        for (int i = 0; i < n; i++)
        {
            p.Tick(auto ?? NoRewarm);
            _now += TimeSpan.FromSeconds(30);
        }
    }

    private JObject Status(string game) => (JObject)((JObject)((JObject)JsonRead.ReadFile(StatusPath)!)["games"]!)[game]!;

    [Fact]
    public void StartupCapLeavesTheRestPendingAndTheStatusRecordsWarmedAndHot()
    {
        Prewarmer p = Make(Game());

        PrewarmResult r = Assert.Single(p.RunServiceStart());

        Assert.True(r.Ran);
        Assert.Equal(40, _streamer.Seen[0].MaxStreamMib); // 시작 상한으로 줄인 사본
        Assert.True(p.SupplementPending("g"));
        JObject s = Status("g");
        Assert.Equal((40L, 100L), ((long)s["warmed_mib"]!, (long)s["hot_mib"]!));
    }

    [Fact]
    public void WithoutATruncationNothingIsPending()
    {
        Prewarmer p = Make(Game());
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 1024 });

        Assert.True(Assert.Single(p.RunServiceStart()).Ran);

        Assert.False(p.SupplementPending("g"));
        Assert.False(Status("g").ContainsKey("warmed_mib"));
    }

    [Fact]
    public void IdleDriveReadsTheRestAfterSixtySecondsExactlyOnce()
    {
        GameSettings g = Game();
        Prewarmer p = Make(g);
        p.RunServiceStart();

        Ticks(p, 2); // 한가함을 막 보기 시작해서 30 초 뒤
        Assert.Single(_streamer.Seen);

        Ticks(p, 1); // 60 초 한가함
        Assert.Equal(2, _streamer.Seen.Count);
        Assert.Equal(g.MaxStreamMib, _streamer.Seen[1].MaxStreamMib); // 설정 상한 그대로: 전체 핫 집합
        Assert.False(p.SupplementPending("g"));
        JObject s = Status("g");
        Assert.Equal((100L, 100L), ((long)s["warmed_mib"]!, (long)s["hot_mib"]!));

        Ticks(p, 6);
        Assert.Equal(2, _streamer.Seen.Count); // 한 번뿐
    }

    [Theory]
    [InlineData("game")]
    [InlineData("relocation")]
    [InlineData("busy")]
    [InlineData("unreadable")]
    public void NoSupplementWhileTheGameRunsRelocatesOrTheDriveIsBusy(string why)
    {
        Prewarmer p = Make(Game());
        p.RunServiceStart();
        (_running, _relocating, _disk) = (why == "game", why == "relocation", why == "busy" ? 50 << 20 : why == "unreadable" ? null : 0);

        Ticks(p, 10);

        Assert.Single(_streamer.Seen);
        Assert.True(p.SupplementPending("g"));
        _running = _relocating = false; // 한가해지면 처음부터 60 초를 다시 셈
        _disk = 0;
        Ticks(p, 2);
        Assert.Single(_streamer.Seen);
        Ticks(p, 1);
        Assert.Equal(2, _streamer.Seen.Count);
    }

    [Fact]
    public void GameStartDuringTheSupplementStopsItAndItIsRetriedLater()
    {
        Prewarmer p = Make(Game());
        p.RunServiceStart();
        bool first = true;
        _streamer.OnStream = g =>
        {
            if (g.MaxStreamMib != 40 && first)
            {
                first = false;
                p.NoteGameStart("g"); // 보충 읽기 도중 게임 시작: 다음 묶음 경계에서 멈춤
            }

            return true;
        };

        Ticks(p, 3);

        Assert.Equal(2, _streamer.Seen.Count);
        Assert.True(p.SupplementPending("g")); // 끝까지 읽지 못해 남음
        Assert.Contains(_log.Lines, l => l.Contains("supplement prewarm cancelled", StringComparison.Ordinal));

        Ticks(p, 3);
        Assert.Equal(3, _streamer.Seen.Count);
        Assert.False(p.SupplementPending("g"));
    }

    [Fact]
    public void SupplementStaysInsideTheMemoryBudget()
    {
        Prewarmer p = Make(Game());
        p.RunServiceStart();
        _avail = 100 + 4096 - 1; // 핫 집합 100 MiB + 기본 여유 4096 MiB 에 1 MiB 모자람

        Ticks(p, 6);

        Assert.Single(_streamer.Seen);
        Assert.True(p.SupplementPending("g"));
        Assert.Contains(_log.Lines, l => l.Contains("supplement prewarm skipped", StringComparison.Ordinal));

        _avail = 16000;
        Ticks(p, 3);
        Assert.Equal(2, _streamer.Seen.Count);
    }

    [Fact]
    public void WarmSamplesDoNotSkipTheRewarmCheckUntilTheSupplementIsDone()
    {
        Prewarmer p = Make(Game());
        p.RunServiceStart();
        _probe = new ProbeResult(8, 0, 0.3); // 표본은 따뜻함
        AutoSettings on = AutoSettings.Default with { Enabled = true, RewarmMinutes = 10 };

        _disk = 50 << 20; // 첫 틱: 재예열 주기만 시작, 보충은 바쁨 때문에 대기 안 함
        Ticks(p, 1, on);
        _now += TimeSpan.FromMinutes(11);
        _disk = 0; // 주기가 되면 한가함: 유휴가 막 시작됐으므로 재예열 점검도 보충하지 않음 (60 초 연속 유휴 판정을 거침)
        Ticks(p, 1, on);

        Assert.Single(_streamer.Seen);
        Assert.True(p.SupplementPending("g"));
        Assert.Contains(_log.Lines, l => l.Contains("rewarm check: samples are warm but the rest", StringComparison.Ordinal));

        Ticks(p, 1, on); // 30 초 뒤: 아직 모자람
        Assert.Single(_streamer.Seen);
        Ticks(p, 1, on); // 60 초 뒤: 보충
        Assert.Equal(2, _streamer.Seen.Count);
        Assert.False(p.SupplementPending("g"));

        _now += TimeSpan.FromMinutes(11);
        Ticks(p, 1, on); // 보충이 끝난 뒤에는 따뜻하면 건너뜀
        Assert.Equal(2, _streamer.Seen.Count);
        Assert.Contains(_log.Lines, l => l.Contains("rewarm check: warm", StringComparison.Ordinal));
    }

    [Fact]
    public void SupplementWhenIdleWaitsForSixtySecondsOfContinuousIdleAndBusyResetsIt()
    {
        GameSettings g = Game();
        Prewarmer p = Make(g);
        p.RunServiceStart();
        Assert.Single(_streamer.Seen);

        Assert.False(p.SupplementWhenIdle(g, default)); // 유휴 첫 호출: 읽지 않고 시각만 기록
        _now += TimeSpan.FromSeconds(59);
        Assert.False(p.SupplementWhenIdle(g, default));
        _disk = 50 << 20; // 바빠지면 유휴 시각을 지움
        _now += TimeSpan.FromSeconds(30);
        Assert.False(p.SupplementWhenIdle(g, default));
        _disk = 0;
        Assert.False(p.SupplementWhenIdle(g, default)); // 다시 처음부터
        _now += TimeSpan.FromSeconds(59);
        Assert.False(p.SupplementWhenIdle(g, default));
        Assert.Single(_streamer.Seen);

        _now += TimeSpan.FromSeconds(1);
        Assert.True(p.SupplementWhenIdle(g, default));
        Assert.Equal(2, _streamer.Seen.Count);
    }

    [Fact]
    public void RestartedServiceKeepsThePendingSupplementFromTheStatusFile()
    {
        Prewarmer first = Make(Game());
        first.RunServiceStart();

        Prewarmer second = Make(Game()); // 같은 상태 파일, 부팅은 하루 전

        Assert.True(second.SupplementPending("g"));
        Ticks(second, 3);
        Assert.Equal(2, _streamer.Seen.Count);
        Assert.False(second.SupplementPending("g"));
    }

    [Fact]
    public void LostStatusFileIsNotAnErrorAndNothingIsPending()
    {
        Prewarmer first = Make(Game());
        first.RunServiceStart();
        File.Delete(StatusPath);

        Prewarmer second = Make(Game());

        Assert.False(second.SupplementPending("g"));
        Ticks(second, 4);
        Assert.Single(_streamer.Seen);
    }

    [Fact]
    public void AFullRunAtTheConfiguredCapClearsThePendingSupplement()
    {
        GameSettings g = Game();
        Prewarmer p = Make(g);
        p.RunServiceStart();
        Assert.True(p.SupplementPending("g"));

        Assert.True(p.Run(g, "t", ignoreCooldown: true).Ran);

        Assert.False(p.SupplementPending("g"));
    }

    [Fact]
    public void AFailedSupplementReadIsGivenUpAndNotRetried()
    {
        Prewarmer p = Make(Game());
        p.RunServiceStart();
        _streamer.OnStream = g => g.MaxStreamMib == 40 ? true : throw new IOException("supplement read failed");

        Ticks(p, 3);

        Assert.Equal(2, _streamer.Seen.Count);
        Assert.False(p.SupplementPending("g"));
        Assert.Contains(_log.Lines, l => l.Contains("supplement given up", StringComparison.Ordinal));

        Ticks(p, 6);
        Assert.Equal(2, _streamer.Seen.Count); // 다음 틱에서 다시 읽지 않음
    }
}
