using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 끊긴 다시 채우기 (재예열) 의 이어 읽기: 끝까지 읽은 위치를 목록의 지문과 함께 남기고, 다음 다시 채우기가 같은 목록·같은 파일·메모리에 남은 앞부분일 때만 거기부터 읽음.
/// 이어 읽었을 때만 같은 회차로 세고, 되풀이해 끊겨도 하루 읽기가 회차 수와 창 크기로 묶이는지.
public sealed class RewarmResumeTests : IDisposable
{
    private const long Mib = 1L << 20;
    private const long OpMib = 5; // 읽기 한 묶음 (가짜 스트리머는 목록의 파일 하나씩)
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_rwres_" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly ListStreamer _streamer;
    private DateTimeOffset _now = new(2026, 11, 24, 12, 0, 0, TimeSpan.Zero);
    private bool _running;
    private TimeSpan _uptime = TimeSpan.FromDays(1);
    private ProbeResult? _front = new ProbeResult(8, 0, 0.3); // 읽은 앞부분의 표본 (따뜻함)
    private readonly List<(string Path, long Offset, int Bytes)> _sampled = [];
    private readonly Dictionary<string, long> _stamps = new(StringComparer.OrdinalIgnoreCase);

    public RewarmResumeTests()
    {
        Directory.CreateDirectory(_dir);
        for (int i = 0; i < 200; i++)
        {
            File.WriteAllText(FilePath(i), "x");
        }

        _streamer = new ListStreamer(this);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private string StatusPath => Path.Combine(_dir, "prewarm_status.json");

    private string FilePath(int i) => Path.Combine(_dir, $"f{i}.bin");

    private sealed record StreamCall(long SkipBytes, long WindowMib, List<string> Read);

    // 목록은 5 MiB 파일 200 개 (1000 MiB, max_stream_mib 로 잘림). 묶음 (파일 하나) 을 읽을 때마다 progress 를 부르고, 취소 신호가 서면 더 읽지 않음 (실제 스트림과 같이 마지막 묶음은 읽은 채로 끊김).
    private sealed class ListStreamer(RewarmResumeTests owner) : IGameStreamer
    {
        public List<StreamCall> Calls { get; } = [];

        public long Salt { get; set; } // 계획이 바뀐 것을 흉내: 목록의 시작 위치가 달라짐

        public long CancelAfterMib { get; set; } = 1L << 30; // 이번 읽기에서 이만큼 읽으면 다른 게임이 떠 점검이 끊음

        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public IReadOnlyList<ReadOp>? PrewarmStreamOps(GameSettings game, long maxBytes, CancellationToken stop)
        {
            long limit = game.MaxStreamSet ? game.MaxStreamMib : 1000;
            return [.. Enumerable.Range(0, 200).Where(i => (i + 1) * OpMib <= limit).Select(i => new ReadOp(owner.FilePath(i), Salt, OpMib * Mib))];
        }

        public (StreamStats Stats, bool Complete) StreamPrewarmCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes, long skipBytes, Func<bool> canIssue,
            bool allowReorder, Action<string, long, bool>? noteIssue, Action<IReadOnlyList<ReadOp>>? afterBatch)
        {
            List<ReadOp> ops = PrefetchStreamer.Tail(PrewarmStreamOps(game, maxBytes, stop)!, skipBytes);
            var call = new StreamCall(skipBytes, game.MaxStreamSet ? game.MaxStreamMib : 1000, []);
            Calls.Add(call);
            long total = ops.Sum(o => o.Length);
            long done = 0;
            progress?.Invoke(0, total);
            foreach (ReadOp op in ops)
            {
                if (stop.IsCancellationRequested || !canIssue())
                {
                    return (Stats(done, aborted: true), false);
                }

                call.Read.Add(Path.GetFileName(op.Path));
                done += op.Length;
                owner._running = done >= CancelAfterMib * Mib; // 이만큼 읽은 때 다른 게임이 뜸
                progress?.Invoke(done, total);
            }

            return (Stats(done), !stop.IsCancellationRequested);
        }

        private static StreamStats Stats(long bytes, bool aborted = false) => new(bytes, 1, 0, bytes / (double)Mib / 10, aborted, RequestedBytes: bytes);
    }

    private Prewarmer Make(GameSettings game)
    {
        var p = new Prewarmer([game], _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = _ => _running,
            ZoneBytes = g => Math.Min(1000, g.MaxStreamMib) * Mib,
            TraceExists = _ => true,
            DiskBytesPerSecond = _ => 0,
            UptimeMs = () => (long)_uptime.TotalMilliseconds,
            FileStamp = path => new Fingerprint(path, 1, _stamps.GetValueOrDefault(path)),
            ProbeSamples = samples =>
            {
                _sampled.AddRange(samples);
                return _front;
            },
        }, _log, StatusPath, () => _now);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = 40 });
        return p;
    }

    private static GameSettings Game() => Games.Make("g", "g.exe");

    // 서비스 시작 예열이 startMib 만 읽은 부분 상태 (기본 창 40 MiB = 파일 8 개).
    private Prewarmer StartedPartial(GameSettings g, int startMib = 40)
    {
        Prewarmer p = Make(g);
        p.SetAuto(AutoSettings.Default with { StartupWarmMaxMib = startMib });
        PrewarmResult started = Assert.Single(p.RunServiceStart());
        Assert.True(started.Ran, started.SkipReason + " | " + string.Join(" | ", _log.Lines));
        Assert.True(p.SupplementPending("g"));
        _streamer.Calls.Clear();
        return p;
    }

    // 다시 채우기 한 번: 5 분 뒤에 시작해 cancelAfterMib 만큼 읽으면 끊김 (기본은 끝까지).
    private PrewarmResult Rewarm(Prewarmer p, GameSettings g, long cancelAfterMib = 1L << 30, long? roomMib = null)
    {
        _now += TimeSpan.FromMinutes(5);
        _streamer.CancelAfterMib = cancelAfterMib;
        PrewarmResult r = p.RewarmPartialGame(g, CancellationToken.None, roomMib);
        _running = false;
        return r;
    }

    private JObject Status() => (JObject)((JObject)((JObject)JsonRead.ReadFile(StatusPath)!)["games"]!)["g"]!;

    [Fact]
    public void ACancelledRewarmResumesAfterTheLastBatchThatWasReadAndDoesNotReadThatRangeAgain()
    {
        GameSettings g = Game();
        Prewarmer p = StartedPartial(g);

        PrewarmResult first = Rewarm(p, g, cancelAfterMib: 25); // 파일 5 개째를 읽은 뒤 끊김 (끝까지 읽은 묶음은 4 개 = 20 MiB)
        Assert.False(first.Ran);
        Assert.Equal(["f0.bin", "f1.bin", "f2.bin", "f3.bin", "f4.bin"], _streamer.Calls[0].Read);
        Assert.False(first.Resumed);

        PrewarmResult second = Rewarm(p, g);

        Assert.True(second.Ran);
        Assert.True(second.Resumed);
        Assert.Equal(20 * Mib, _streamer.Calls[1].SkipBytes);
        Assert.Equal(["f4.bin", "f5.bin", "f6.bin", "f7.bin"], _streamer.Calls[1].Read); // 끝까지 읽은 앞의 4 개는 다시 읽지 않음
        Assert.All(_sampled, s => Assert.Contains(Path.GetFileName(s.Path), (string[])["f0.bin", "f1.bin", "f2.bin", "f3.bin"])); // 표본은 이미 읽은 앞부분에서만
        Assert.False(Status().ContainsKey("rewarm_resume")); // 끝까지 읽었으니 위치는 지움
    }

    [Theory]
    [InlineData("file")]
    [InlineData("plan")]
    [InlineData("window")]
    public void TheSavedPositionIsDroppedWhenTheFileThePlanOrTheWindowChanged(string change)
    {
        GameSettings g = Game();
        Prewarmer p = StartedPartial(g, startMib: change == "window" ? 600 : 40);
        Assert.False(Rewarm(p, g, cancelAfterMib: 25).Ran);
        Assert.True(Status().ContainsKey("rewarm_resume"));
        long? room = null;
        switch (change)
        {
            case "file":
                _stamps[FilePath(2)] = 7; // 파일이 교체됨 (수정 시각이 바뀜)
                break;
            case "plan":
                _streamer.Salt = 1; // 같은 파일의 다른 범위를 읽는 계획
                break;
            default:
                room = 300; // 메모리 예산이 줄어 창이 600 MiB 에서 300 MiB 로 줄어듦
                break;
        }

        _sampled.Clear();
        PrewarmResult second = Rewarm(p, g, roomMib: room);

        Assert.False(second.Resumed);
        Assert.Equal(0, _streamer.Calls[1].SkipBytes);
        Assert.Equal("f0.bin", _streamer.Calls[1].Read[0]); // 처음부터 읽음
        Assert.Empty(_sampled); // 지문이 달라 앞부분 표본도 읽지 않음
        Assert.Contains(_log.Lines, line => line.Contains("cancelled rewarm position of 20 MiB dropped (the read list or its files changed)", StringComparison.Ordinal));
    }

    [Fact]
    public void AFrontThatLeftMemoryIsReadFromTheStartAndCountsANewRound()
    {
        GameSettings g = Game();
        Prewarmer p = StartedPartial(g);
        Assert.False(Rewarm(p, g, cancelAfterMib: 25).Ran);
        Assert.Null(p.PartialRewarmPause("g")); // 한 회차
        _front = new ProbeResult(8, 8, 120.0); // 읽어 둔 앞부분이 밀려남

        PrewarmResult second = Rewarm(p, g, cancelAfterMib: 25);

        Assert.False(second.Resumed);
        Assert.Equal(0, _streamer.Calls[1].SkipBytes);
        Assert.Equal("f0.bin", _streamer.Calls[1].Read[0]);
        Assert.Contains(_log.Lines, line => line.Contains("dropped (the front part is no longer in memory)", StringComparison.Ordinal));
        Assert.NotNull(p.PartialRewarmPause("g")); // 새 회차라 하루 2번 상한에 닿음
    }

    [Fact]
    public void AResumedReadStaysInTheRoundOfTheCancelledOneAndOnlyAFinishedReadStartsTheColdAgainRest()
    {
        GameSettings g = Game();
        Prewarmer p = StartedPartial(g);
        Assert.False(Rewarm(p, g, cancelAfterMib: 25).Ran);
        Assert.Null(p.NotePartialColdAgain("g", TimeSpan.FromMinutes(20))); // 끊긴 읽기는 "곧 다시 식음" 의 기준이 아님

        Assert.True(Rewarm(p, g).Resumed);
        Assert.Null(p.PartialRewarmPause("g")); // 끊긴 시도와 이어 읽은 시도가 한 회차

        DateTimeOffset finishedAt = _now;
        _now += TimeSpan.FromMinutes(10);
        Assert.Equal(finishedAt + TimeSpan.FromHours(24), p.NotePartialColdAgain("g", TimeSpan.FromMinutes(20))); // 끝까지 읽은 때로부터 24시간
    }

    [Fact]
    public void RepeatedCancelsFinishTheWindowWithinOneRoundAndReadNoMoreThanTheWindowPlusTheBatchesThatWereCut()
    {
        GameSettings g = Game();
        Prewarmer p = StartedPartial(g);
        using IDisposable idle = DiskReadLedger.Scope(ReadPurpose.Idle); // 주기 점검과 같이 계기 없는 읽기로 셈

        int attempts = 0;
        PrewarmResult last;
        do
        {
            last = Rewarm(p, g, cancelAfterMib: 10); // 매번 파일 두 개를 읽고 끊김
            attempts++;
        }
        while (!last.Ran && attempts < 20);

        long readMib = _streamer.Calls.Sum(c => c.Read.Count) * OpMib;
        Assert.True(last.Ran);
        Assert.True(attempts <= 9, $"attempts {attempts}"); // 이어 읽지 않으면 40 MiB 창은 영영 끝나지 않음 (매번 처음부터 10 MiB 만 읽고 끊김)
        Assert.Equal(readMib * Mib, p.DiskReads.Used(@"D:\", ReadPurpose.Idle).Bytes); // 하루 읽기 집계는 실제로 읽은 만큼 (건너뛴 앞부분은 세지 않음)
        Assert.True(readMib <= 40 + attempts * OpMib, $"read {readMib} MiB"); // 창 + 끊길 때마다 다시 읽는 마지막 묶음
        Assert.Null(p.PartialRewarmPause("g")); // 회차는 하나
    }

    [Fact]
    public void WhenTheFrontKeepsLeavingMemoryEveryRestartIsANewRoundSoTheDailyLimitStopsTheReads()
    {
        GameSettings g = Game();
        Prewarmer p = StartedPartial(g);
        _front = new ProbeResult(8, 8, 120.0);

        int attempts = 0;
        while (p.PartialRewarmPause("g") is null && attempts < 20)
        {
            _ = Rewarm(p, g, cancelAfterMib: 10);
            attempts++;
        }

        Assert.Equal(Prewarmer.MaxPartialRewarmsPerDay, attempts); // 취소를 되풀이해도 회차는 매번 새로 세어져 2 번에서 멈춤
        Assert.Equal(attempts * 2, _streamer.Calls.Sum(c => c.Read.Count)); // 시도마다 파일 두 개 (10 MiB)
        Assert.All(_streamer.Calls, c => Assert.Equal(0, c.SkipBytes));
    }

    [Fact]
    public void TheSavedPositionSurvivesAServiceRestartButNotAReboot()
    {
        GameSettings g = Game();
        Prewarmer p = StartedPartial(g);
        Assert.False(Rewarm(p, g, cancelAfterMib: 25).Ran);
        JObject saved = (JObject)Status()["rewarm_resume"]!;
        Assert.Equal(20L * Mib, (long)saved["bytes"]!);

        Prewarmer restarted = Make(g); // 서비스만 다시 시작 (같은 상태 파일, 부팅은 하루 전)
        PrewarmResult resumed = Rewarm(restarted, g, cancelAfterMib: 25);
        Assert.True(resumed.Resumed);
        Assert.Equal(20 * Mib, _streamer.Calls[^1].SkipBytes);

        _uptime = TimeSpan.FromMinutes(1); // 그 사이 컴퓨터를 다시 켬: 파일 캐시가 비었으므로 저장한 위치를 쓰지 않음
        Prewarmer rebooted = Make(g);
        _ = Rewarm(rebooted, g);
        Assert.Equal(0, _streamer.Calls[^1].SkipBytes);
    }

    [Fact]
    public void AnOldFormatStatusFileLoadsWithoutAPositionAndTreatsTheLastReadAsFinished()
    {
        GameSettings g = Game();
        Prewarmer p = StartedPartial(g);
        Assert.True(Rewarm(p, g).Ran);
        JObject entry = Status();
        Assert.True(entry.ContainsKey("partial_rewarm_done_at"));
        string text = File.ReadAllText(StatusPath);
        text = text.Replace("partial_rewarm_done_at", "partial_rewarm_done_unused", StringComparison.Ordinal); // 예전 형식: 끝까지 읽은 시각이 따로 없음
        File.WriteAllText(StatusPath, text);

        Prewarmer old = Make(g);

        Assert.Equal(_now + TimeSpan.FromHours(24), old.NotePartialColdAgain("g", TimeSpan.FromMinutes(20))); // 마지막으로 읽은 시각이 기준 (그때와 같음)
        _ = Rewarm(old, g);
        Assert.Equal(0, _streamer.Calls[^1].SkipBytes); // 저장한 위치가 없으면 처음부터 읽음
    }
}
