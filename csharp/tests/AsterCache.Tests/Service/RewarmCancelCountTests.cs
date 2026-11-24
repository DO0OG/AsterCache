using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using Xunit;

namespace AsterCache.Tests.Service;

/// 식은 앞부분 다시 읽기: 끝까지 읽은 회차만 하루 2번 상한에 세고, 끊긴 시도는 횟수를 쓰지 않는 대신 읽은 양이 하루 읽기 상한에 실제 읽은 만큼 들어가 되풀이해도 그 상한에서 멈추는가.
public sealed class RewarmCancelCountTests : IDisposable
{
    private const long Mib = 1L << 20;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_rwcancel_" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly FileStreamer _streamer = new();
    private DateTimeOffset _now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private bool _running;

    public RewarmCancelCountTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private string StatusPath => Path.Combine(_dir, "prewarm_status.json");

    // 5 MiB 묶음 (파일 하나) 마다 progress 를 부르고, 읽은 양이 CancelAfterMib 이상이면 다른 게임이 뜬 것으로 점검이 끊음 (마지막 묶음은 읽은 채로 끊김).
    private sealed class FileStreamer : IGameStreamer
    {
        public int Streams { get; set; }

        public long CancelAfterMib { get; set; } = 1L << 30;

        public double SecondsPerMib { get; set; } = 0.1; // 디스크 속도 (10 MiB/s)

        public Action<bool>? SetRunning { get; set; }

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) StreamPrewarmCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes, long skipBytes, Func<bool> canIssue,
            bool allowReorder, Action<string, long, bool>? noteIssue, Action<IReadOnlyList<ReadOp>>? afterBatch)
        {
            Streams++;
            long total = (game.MaxStreamSet ? game.MaxStreamMib : 1000) * Mib;
            long done = 0;
            progress?.Invoke(0, total);
            while (done < total)
            {
                if (stop.IsCancellationRequested || !canIssue())
                {
                    return (Stats(done, aborted: true), false);
                }

                done += 5 * Mib;
                SetRunning?.Invoke(done >= CancelAfterMib * Mib);
                progress?.Invoke(done, total);
            }

            return (Stats(done), !stop.IsCancellationRequested);
        }

        private StreamStats Stats(long bytes, bool aborted = false) => new(bytes, 1, 0, bytes / (double)Mib * SecondsPerMib, aborted, RequestedBytes: bytes);
    }

    private Prewarmer StartedPartial(GameSettings g, AutoSettings? auto = null)
    {
        _streamer.SetRunning = running => _running = running;
        var p = new Prewarmer([g], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = _ => _running,
            ZoneBytes = game => Math.Min(1000, game.MaxStreamMib) * Mib,
            TraceExists = _ => true,
            DiskBytesPerSecond = _ => 0,
        }, _log, StatusPath, () => _now);
        p.SetAuto(auto ?? AutoSettings.Default with { StartupWarmMaxMib = 40 });
        Assert.Single(p.RunServiceStart());
        Assert.True(p.SupplementPending("g"));
        _streamer.Streams = 0;
        return p;
    }

    private static GameSettings Game() => Games.Make("g", "g.exe");

    // 다시 채우기 한 번 (5 분 뒤): cancelAfterMib 만큼 읽으면 끊김.
    private PrewarmResult Rewarm(Prewarmer p, GameSettings g, long cancelAfterMib = 1L << 30)
    {
        _now += TimeSpan.FromMinutes(5);
        _streamer.CancelAfterMib = cancelAfterMib;
        PrewarmResult r = p.RewarmPartialGame(g, CancellationToken.None, null);
        _running = false;
        return r;
    }

    [Fact]
    public void ACutOffRewarmDoesNotUseTheDailyCountAndOnlyTwoReadsToTheEndStopIt()
    {
        GameSettings g = Game();
        Prewarmer p = StartedPartial(g);

        for (int i = 0; i < 4; i++)
        {
            Assert.False(Rewarm(p, g, cancelAfterMib: 25).Ran); // 다른 게임이 떠 도중에 끊김
        }

        Assert.Null(p.PartialRewarmPause("g")); // 끊긴 시도는 횟수를 쓰지 않음
        Assert.Null(p.NotePartialColdAgain("g", TimeSpan.FromMinutes(20))); // 끝까지 읽은 적이 없으니 "곧 다시 식음" 의 기준도 없음

        Assert.True(Rewarm(p, g).Ran);
        Assert.Null(p.PartialRewarmPause("g")); // 끝까지 읽은 한 번

        Assert.False(Rewarm(p, g, cancelAfterMib: 25).Ran);
        Assert.Null(p.PartialRewarmPause("g"));
        Assert.True(Rewarm(p, g).Ran);
        Assert.NotNull(p.PartialRewarmPause("g")); // 끝까지 읽은 두 번: 하루 2번 상한
    }

    [Fact]
    public void RepeatedCutOffRewarmsStopWhereTheDailyReadLimitIsReached()
    {
        GameSettings g = Game();
        Prewarmer p = StartedPartial(g, AutoSettings.Default with { StartupWarmMaxMib = 40, IdleReadGibPerDay = 1 });
        using IDisposable idle = DiskReadLedger.Scope(ReadPurpose.Idle); // 주기 점검과 같이 계기 없는 읽기로 셈

        for (int i = 0; i < 80; i++)
        {
            Assert.False(Rewarm(p, g, cancelAfterMib: 25).Ran); // 매번 25 MiB 를 읽고 끊김 (상한에 닿으면 읽지 않고 건너뜀)
        }

        int streams = _streamer.Streams;
        long used = p.DiskReads.Used(@"D:\", ReadPurpose.Idle).Bytes;
        Assert.Equal(streams * 25 * Mib, used); // 끊긴 시도가 읽은 만큼이 그대로 들어감
        Assert.InRange(used, (1024 - 40) * Mib, 1024 * Mib); // 남은 양이 창 (40 MiB) 보다 작아지면 더 읽지 않음: 하루 합계가 상한 안
        Assert.True(streams < 80, $"streams {streams}");

        for (int i = 0; i < 5; i++)
        {
            Assert.False(Rewarm(p, g, cancelAfterMib: 25).Ran);
        }

        Assert.Equal(streams, _streamer.Streams); // 상한에 닿은 뒤에는 읽지 않음
        Assert.Equal(used, p.DiskReads.Used(@"D:\", ReadPurpose.Idle).Bytes);
        Assert.Null(p.PartialRewarmPause("g")); // 하루 횟수는 한 번도 쓰이지 않음
    }

    [Fact]
    public void ARereadOfWhatIsStillInMemoryCountsOnlyItsReadTimeAtTheDriveLimit()
    {
        GameSettings g = Game();
        Prewarmer p = StartedPartial(g);
        using IDisposable idle = DiskReadLedger.Scope(ReadPurpose.Idle);
        _streamer.SecondsPerMib = 0.001; // 1000 MiB/s: 메모리에 남은 범위를 읽는 속도 (디스크 상한 250 MiB/s 의 4 배)

        Assert.True(Rewarm(p, g).Ran); // 40 MiB 를 다시 읽음 (0.04 s)

        Assert.Equal(10 * Mib, p.DiskReads.Used(@"D:\", ReadPurpose.Idle).Bytes); // 읽은 시간 x 250 MiB/s = 10 MiB: 읽은 양 (40 MiB) 의 4 분의 1
    }
}
