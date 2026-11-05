using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 부팅 조용 구간 (BootQuiet): 부팅 5 분 안에 뜬 서비스만 구간이 있고, 5 분 뒤 게임 드라이브 60 초 유휴에 끝나며, 보류 로그는 무엇별로 구간당 한 번.
/// 재시작 뒤 새 트레이스 판정: 부팅 전에 예열한 트레이스 (원시·boot 트레이스가 있는 게임) 는 LastWarmed 가 돌려줌.
public sealed class BootQuietTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    private const long Minute = 60_000;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_bootquiet_" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private DateTimeOffset _now = T0;
    private long _uptimeMs = Minute;
    private bool _idle;

    public BootQuietTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private BootQuiet Make() => new(() => _uptimeMs, () => _idle, _log, () => _now);

    private string StatusPath => Path.Combine(_dir, "prewarm_status.json");

    private Prewarmer MakePrewarm(bool rawTrace) => new([Games.Make("a", "a.exe")], new FakeStreamer(), new PrewarmEnv
    {
        AvailableRamMib = () => 16000,
        GameRunning = _ => false,
        ZoneBytes = _ => 100L << 20,
        TraceExists = _ => true,
        RawTraceExists = _ => rawTrace,
        UptimeMs = () => _uptimeMs,
    }, _log, StatusPath, () => T0);

    [Fact]
    public void ServiceStartedAfterBootDelayIsNeverQuiet()
    {
        _uptimeMs = 6 * Minute;
        BootQuiet q = Make();

        Assert.False(q.Active);
        Assert.False(q.Defers("layout check and reapply"));
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void DefersEachKindOfWorkOnceWhileQuiet()
    {
        BootQuiet q = Make();

        Assert.True(q.Defers("layout check and reapply"));
        Assert.True(q.Defers("layout check and reapply"));
        Assert.True(q.Defers("auto relocation and new trace prewarm"));
        Assert.Single(_log.Lines, line => line.Contains("layout check and reapply deferred: boot quiet period", StringComparison.Ordinal));
        Assert.Single(_log.Lines, line => line.Contains("auto relocation and new trace prewarm deferred: boot quiet period", StringComparison.Ordinal));
    }

    [Fact]
    public void QuietEndsOnlyAfterBootDelayAndSixtySecondsOfIdleDisk()
    {
        _idle = true;
        BootQuiet q = Make();
        q.Refresh(); // 아직 5 분 전: 유휴와 무관하게 구간
        Assert.True(q.Active);

        _uptimeMs = 5 * Minute;
        q.Refresh(); // 유휴 시작
        _now = _now.AddSeconds(30);
        q.Refresh();
        Assert.True(q.Active);

        _now = _now.AddSeconds(30);
        q.Refresh(); // 60 초 유휴
        Assert.False(q.Active);
        Assert.False(q.Defers("layout check and reapply"));
    }

    [Fact]
    public void BusyDiskRestartsTheIdleWindow()
    {
        _idle = true;
        BootQuiet q = Make(); // 부팅 5 분 안에 뜬 서비스
        _uptimeMs = 5 * Minute;
        q.Refresh();

        _now = _now.AddSeconds(50);
        _idle = false;
        q.Refresh();

        _idle = true;
        _now = _now.AddSeconds(20);
        q.Refresh();
        _now = _now.AddSeconds(59);
        q.Refresh();
        Assert.True(q.Active);

        _now = _now.AddSeconds(1);
        q.Refresh();
        Assert.False(q.Active);
    }

    [Fact]
    public void WarmedTraceBeforeRebootIsNotNewAgainWhenTheGameHasATrace()
    {
        File.WriteAllText(StatusPath, """{"games":{"a":{"result":"ok","at":"2026-11-05T09:00:00.0000000+00:00"}}}""");

        Prewarmer p = MakePrewarm(rawTrace: true);

        Assert.Equal(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero), p.LastWarmed("a"));
    }

    [Fact]
    public void WarmedBeforeRebootIsNotRestoredForAGameWithoutATrace()
    {
        File.WriteAllText(StatusPath, """{"games":{"a":{"result":"ok","at":"2026-11-05T09:00:00.0000000+00:00"}}}""");

        Prewarmer p = MakePrewarm(rawTrace: false);

        Assert.Null(p.LastWarmed("a"));
    }

    [Fact]
    public void WarmedAfterBootIsStillLastWarmed()
    {
        File.WriteAllText(StatusPath, """{"games":{"a":{"result":"ok","at":"2026-11-05T09:59:30.0000000+00:00"}}}""");

        Prewarmer p = MakePrewarm(rawTrace: false);

        Assert.Equal(new DateTimeOffset(2026, 11, 5, 9, 59, 30, TimeSpan.Zero), p.LastWarmed("a"));
    }
}
