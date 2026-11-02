using AsterCache.Cli.Commands;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Tests.Prefetch;
using AsterCache.Tests.Windows;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Cli;

/// aster learn: 인자, 출력 형식, trace.json 저장. 세션은 가짜입니다 (관리자 ETW 는 실행하지 않음).
public sealed class LearnCommandTests : IDisposable
{
    private const long Mib = 1L << 20;
    private const string GameFile = @"D:\Games\Foo\a.bin";
    private readonly LearnDeps _saved = LearnCommand.Deps;
    private readonly FakeReadIo _io = new();
    private FakeLearnSession? _session;

    public LearnCommandTests()
    {
        _io.Sizes[GameFile] = 20 * Mib;
        LearnCommand.Deps = Deps(waitForReadAhead: 3 * Mib, neverSeen: false);
    }

    public void Dispose() => LearnCommand.Deps = _saved;

    private LearnDeps Deps(long waitForReadAhead, bool neverSeen) => new()
    {
        Open = (_, listener) => _session = new FakeLearnSession(listener, _io, waitForReadAhead, neverSeen),
        OpenIo = () => _io,
        AvailableRamMib = () => 1 << 20,
        PhysicalRamBytes = () => 16L << 30,
        Poll = TimeSpan.FromMilliseconds(5),
    };

    [Fact]
    public void JsonOutputHasTheCountersAndSavesTheTraceWhenOutIsGiven()
    {
        using var dir = new TempDir();
        string trace = dir.Combine("t.json");

        (int code, string text, _) = CliTests.Run("learn", "--exe", "foo.exe", "--root", @"D:\Games\Foo", "--window-mib", "4", "--seconds", "30", "--mode", "gap", "--out", trace, "--json");

        Assert.Equal(0, code);
        var o = (JObject)JsonRead.Parse(text)!;
        Assert.Equal(
            ["read_ahead_bytes", "game_read_bytes", "hit_ratio", "events", "events_lost", "resolved_reads", "late_reads", "unresolved_reads", "sequential_files", "hit_bytes", "gap_count", "yielded_requests", "read_ahead_while_idle_bytes", "saturated_ms", "paused_requests", "out", "trace_events"],
            o.Select(kv => kv.Key));
        Assert.Equal(3 * Mib, o["read_ahead_bytes"]);
        Assert.Equal(8 * Mib, o["game_read_bytes"]);
        Assert.Equal(0.0, o["hit_ratio"]);
        Assert.Equal(8L, o["events"]);
        Assert.Equal(7L, o["resolved_reads"]); // 세션이 알려 준 집계 (가짜)
        Assert.Equal(2L, o["late_reads"]);
        Assert.Equal(1L, o["unresolved_reads"]);
        Assert.Equal(1L, o["sequential_files"]);
        Assert.Equal(0L, o["hit_bytes"]);
        Assert.Equal(1L, o["gap_count"]); // 읽기가 끝난 뒤 틈 하나에서 3 MiB
        Assert.Equal(0L, o["yielded_requests"]);
        Assert.Equal(3 * Mib, o["read_ahead_while_idle_bytes"]);
        Assert.Equal((0L, 0L), (o["saturated_ms"], o["paused_requests"]));
        Assert.Equal(trace, o["out"]);
        Assert.Equal(1L, o["trace_events"]);
        Assert.True(File.Exists(trace));
        Assert.True(_session!.Disposed);
        Assert.True(_io.Disposed);
    }

    [Fact]
    public void DefaultOutputIsOneSummaryLineAndNothingIsSavedWithoutOut()
    {
        (int code, string text, _) = CliTests.Run("learn", "--exe", "foo.exe", "bar.exe", "--root", @"D:\Games\Foo", "--window-mib", "4");

        Assert.Equal(0, code);
        Assert.Equal("learn: read ahead 3 MiB, game read 8 MiB, hit 0.0%, events 8" + Environment.NewLine, text);
    }

    [Fact]
    public void WindowDefaultsTo512MibAndModeToStreamWhenNotGiven()
    {
        _io.Sizes[GameFile] = 1000 * Mib;
        LearnCommand.Deps = Deps(waitForReadAhead: 76 * Mib, neverSeen: false);

        (int code, string text, _) = CliTests.Run("learn", "--exe", "foo.exe", "--root", @"D:\Games\Foo", "--seconds", "30", "--json");

        Assert.Equal(0, code);
        var o = (JObject)JsonRead.Parse(text)!;
        Assert.Equal(76 * Mib, o["read_ahead_bytes"]); // 창 [7, 519) MiB 가 넉넉해서 게임 읽기 8 MiB × 1.5 + 64 MiB 상한에서 멈춤 (창 64 였다면 63 MiB)
        Assert.Equal(0L, o["gap_count"]); // 스트림 모드: 틈을 기다리지 않음
    }

    [Fact]
    public void BoundedModeIsAcceptedAndCapsReadAheadAtGameReadsPlus32Mib()
    {
        _io.Sizes[GameFile] = 1000 * Mib;
        LearnCommand.Deps = Deps(waitForReadAhead: 40 * Mib, neverSeen: false);

        (int code, string text, _) = CliTests.Run("learn", "--exe", "foo.exe", "--root", @"D:\Games\Foo", "--mode", "bounded", "--seconds", "30", "--json");

        Assert.Equal(0, code);
        var o = (JObject)JsonRead.Parse(text)!;
        Assert.Equal(8 * Mib + 32 * Mib, o["read_ahead_bytes"]); // 게임 읽기 8 MiB x 1.0 + 32 MiB (스트림이면 48 MiB 이상까지 감)
    }

    [Fact]
    public void AdaptiveModeIsAcceptedAndKeepsTheBoundedCapUnlessTheSaturationThresholdIsLow()
    {
        _io.Sizes[GameFile] = 1000 * Mib;
        LearnCommand.Deps = Deps(waitForReadAhead: 40 * Mib, neverSeen: false);

        (int code, string text, _) = CliTests.Run("learn", "--exe", "foo.exe", "--root", @"D:\Games\Foo", "--mode", "adaptive", "--seconds", "30", "--json");

        Assert.Equal(0, code);
        var o = (JObject)JsonRead.Parse(text)!;
        Assert.Equal((8 * Mib) + (32 * Mib), o["read_ahead_bytes"]); // 게임 읽기 8 MiB 가 500 ms 에 16 MiB/s: 기본 기준 80 미만
        Assert.Equal(0L, o["saturated_ms"]);
    }

    [Fact]
    public void SaturationMbpsReachesTheEngineAndSaturatedMsIsReported()
    {
        _io.Sizes[GameFile] = 1000 * Mib;
        LearnCommand.Deps = Deps(waitForReadAhead: 40 * Mib, neverSeen: false);
        (int code, string text, _) = CliTests.Run("learn", "--exe", "foo.exe", "--root", @"D:\Games\Foo", "--mode", "adaptive", "--saturation-mbps", "1", "--seconds", "30", "--json");
        var o = (JObject)JsonRead.Parse(text)!;
        Assert.Equal(0, code);
        Assert.True((long)o["saturated_ms"]! > 0); // 8 MiB / 0.5 s 가 기준 1 MiB/s 이상이라 창이 비는 약 500 ms 동안 쉼
    }

    [Fact]
    public void SeqRunsAndGapMsReachTheEngine()
    {
        LearnCommand.Deps = Deps(waitForReadAhead: 0, neverSeen: false);
        (int code, string text, _) = CliTests.Run("learn", "--exe", "foo.exe", "--root", @"D:\Games\Foo", "--seq-runs", "20", "--json");
        var o = (JObject)JsonRead.Parse(text)!;
        Assert.Equal(0, code);
        Assert.Equal((0L, 0L), (o["read_ahead_bytes"], o["sequential_files"])); // 연속 20 번이 필요해 8 번 읽기로는 판정 안 됨

        LearnCommand.Deps = Deps(waitForReadAhead: 3 * Mib, neverSeen: false);
        (code, text, _) = CliTests.Run("learn", "--exe", "foo.exe", "--root", @"D:\Games\Foo", "--mode", "gap", "--gap-ms", "100000", "--seconds", "1", "--json");
        o = (JObject)JsonRead.Parse(text)!;
        Assert.Equal(0, code);
        Assert.Equal((0L, 1L, 0L), (o["read_ahead_bytes"], o["sequential_files"], o["gap_count"])); // 판정은 됐지만 틈이 오지 않음
    }

    [Fact]
    public void StopsWhenSecondsPassEvenIfTheGameNeverStarts()
    {
        LearnCommand.Deps = Deps(waitForReadAhead: 0, neverSeen: true);

        (int code, string text, _) = CliTests.Run("learn", "--exe", "foo.exe", "--root", @"D:\Games\Foo", "--seconds", "1", "--json");

        Assert.Equal(0, code);
        Assert.NotNull(_session);
        Assert.Equal(0L, ((JObject)JsonRead.Parse(text)!)["events"]);
    }

    [Theory]
    [InlineData("learn", "--exe", "a.exe", "--root", @"D:\g", "--mode", "burst")]
    [InlineData("learn", "--root", @"D:\g")]
    [InlineData("learn", "--exe", "a.exe")]
    [InlineData("learn", "--exe", "a.exe", "--root", @"D:\g", "--window-mib", "x")]
    [InlineData("learn", "--exe", "a.exe", "--root", @"D:\g", "--seq-runs", "0")]
    [InlineData("learn", "--exe", "a.exe", "--root", @"D:\g", "--gap-ms", "x")]
    [InlineData("learn", "--exe", "a.exe", "--root", @"D:\g", "--saturation-mbps", "0")]
    [InlineData("learn", "--exe", "a.exe", "--root", @"D:\g", "--saturation-mbps", "10001")]
    [InlineData("learn", "--exe", "a.exe", "--root", @"D:\g", "--saturation-mbps", "x")]
    public void MissingOrMalformedArgumentsAreUsageErrors(params string[] args)
    {
        (int code, _, string err) = CliTests.Run(args);

        Assert.Equal(2, code);
        Assert.NotEmpty(err);
        Assert.Null(_session);
    }

    [Theory]
    [InlineData("--seconds")]
    [InlineData("--window-mib")]
    [InlineData("--max-mib")]
    public void NumbersBelowOneAreUsageErrors(string option)
    {
        (int code, _, _) = CliTests.Run("learn", "--exe", "a.exe", "--root", @"D:\g", option, "0");

        Assert.Equal(2, code);
        Assert.Null(_session);
    }

    [Fact]
    public void NotAdminIsReportedWithTheStandardCode()
    {
        LearnCommand.Deps = new LearnDeps { Open = (_, _) => throw new NotAdminException() };

        (int code, string text, _) = CliTests.Run("learn", "--exe", "a.exe", "--root", @"D:\g");

        Assert.Equal(3, code);
        Assert.Equal("not_admin", ((JObject)JsonRead.Parse(text)!)["error"]);
    }
}

/// 세션을 열면 게임이 파일 하나를 읽은 것처럼 알림을 보내고, 선행 읽기가 기대한 만큼 끝나면 프로세스가 끝난 것으로 알립니다.
internal sealed class FakeLearnSession : ILearnSession
{
    private readonly FakeReadIo _io;
    private readonly long _waitFor;

    public FakeLearnSession(LiveListener listener, FakeReadIo io, long waitForReadAhead, bool neverSeen)
    {
        _io = io;
        _waitFor = waitForReadAhead;
        SawTarget = !neverSeen;
        if (!neverSeen)
        {
            listener.OnOpen(@"D:\Games\Outside\x.bin"); // 허용 루트 밖: 무시됨
            for (int i = 0; i < 8; i++)
            {
                listener.OnRead(@"D:\Games\Foo\a.bin", i * 1048576L, 1048576); // 1 MiB 순차 여덟 번: 순차로 판정돼 틈이 나면 선행 읽기가 시작됨
            }
        }
    }

    public bool Disposed { get; private set; }

    public bool SawTarget { get; }

    public bool TargetsActive => _io.ReadBytes < _waitFor;

    public long EventsLost => 0;

    public ReadNameStats NameStats => new(7, 2, 1);

    public IReadOnlyList<TraceRecord> Stop() =>
    [
        TraceRecord.Start(DateTime.UtcNow, 100, 1, "foo.exe"),
        TraceRecord.Read(DateTime.UtcNow, 100, @"D:\Games\Foo\a.bin", 0, 65536),
    ];

    public void Dispose() => Disposed = true;
}
