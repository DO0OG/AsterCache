using AsterCache.Core.Discovery;
using AsterCache.Core.Json;
using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class OrbitCalibrationTests : IDisposable
{
    private const string Disk = "D:";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ac-orbit-cal-" + Guid.NewGuid().ToString("N"));
    private readonly ListLog _log = new();
    private readonly DiskReadCoordinator _coordinator;
    private readonly GameSettings _game = Games.Make("Foo", "Foo.exe");
    private readonly List<(string Path, long Offset, int Length)> _reads = [];
    private readonly DateTimeOffset _t0 = new(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
    private bool _gameActive;
    private bool _diskBusy;
    private CapabilityOutcome _decision = CapabilityOutcome.Allow;
    private Func<int, long, int, TimeSpan?> _latency = (_, _, length) => length == 4096 ? TimeSpan.FromMilliseconds(10) : TimeSpan.FromMilliseconds(40);

    public OrbitCalibrationTests()
    {
        Directory.CreateDirectory(_dir);
        _coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), _log);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private string ModelPath => Path.Combine(_dir, OrbitCalibration.FileName);

    private static readonly ReadOp[] Ops =
    [
        new(@"D:\Games\Foo\a.pak", 4096, 64L << 30), // 표본 위치 사이 최소 거리를 둘 만큼 큼
        new(@"D:\Games\Foo\b.pak", 1000, 2L << 20),
        new(@"D:\Games\Foo\tiny.pak", 0, 2000), // 4 KiB 가 들어가지 않는 op
        new(@"D:\Games\Foo\c.pak", 0, 1 << 20), // 4 MiB 가 들어가지 않는 op
    ];

    private OrbitCalibration Make(int seed = 1) => new(_dir, _log, (path, offset, length, _) =>
    {
        _reads.Add((path, offset, length));
        return _latency(_reads.Count, offset, length);
    }, () => _t0, letter => _diskBusy && letter == Disk, _ => _gameActive, new Random(seed), _ => _decision);

    private DiskReadCoordinator.Lease Lease() => _coordinator.TryBegin(_game.OriginRoot, "calibration")!;

    // 스트림 하나 = 관측값 하나: 보통 스트림은 중심 (10 ms, 100 MB/s) 의 +-2% 안, 이탈 스트림은 인자로 크기를 키움
    private static void Feed(OrbitCalibration cal, int count, int start = 0, double seekLog = 0, double thrLog = 0)
    {
        for (int i = start; i < start + count; i++)
        {
            double wobble = Math.Exp(0.01 * ((i % 5) - 2));
            cal.Add(Disk, (10 * wobble * Math.Exp(seekLog), i), 100 * wobble * Math.Exp(thrLog));
        }
    }

    [Fact]
    public void One_sample_reads_twelve_small_and_three_large_within_the_byte_cap()
    {
        OrbitCalibration cal = Make();
        using DiskReadCoordinator.Lease lease = Lease();

        long bytes = cal.Sample(_game, Ops, null, lease, default);

        Assert.Equal(12 + 3, _reads.Count);
        Assert.Equal(12, _reads.Count(r => r.Length == Orbit3Constants.SeekProbeBytes));
        Assert.Equal(3, _reads.Count(r => r.Length == Orbit3Constants.ThroughputProbeBytes));
        Assert.Equal(_reads.Sum(r => (long)r.Length), bytes);
        Assert.True(bytes <= Orbit3Constants.MaxProbeBytesPerStream);
        Assert.All(_reads, r =>
        {
            Assert.Equal(0, r.Offset % 4096);
            ReadOp op = Ops.Single(o => o.Path == r.Path);
            Assert.True(r.Offset >= op.Offset && r.Offset + r.Length <= op.Offset + op.Length); // 읽기 범위가 그 op 안
        });
        Assert.All(_reads.Where(r => r.Length == Orbit3Constants.ThroughputProbeBytes), r => Assert.Equal(@"D:\Games\Foo\a.pak", r.Path)); // 4 MiB 가 들어가는 op 는 하나뿐
        Assert.DoesNotContain(_reads, r => r.Path.EndsWith("tiny.pak", StringComparison.Ordinal));
    }

    [Fact]
    public void Sample_records_one_median_observation_per_metric_in_the_file()
    {
        Make().Sample(_game, Ops, null, Lease(), default);

        var disk = (JObject)((JObject)JsonRead.ReadFile(ModelPath)!)["disks"]!;
        var entry = (JObject)disk[Disk]!;
        Assert.Equal(1L, ((JObject)JsonRead.ReadFile(ModelPath)!)["version"]);
        var seek = (List<object?>)entry["seek"]!;
        Assert.Single(seek); // 스트림 하나 = 관측값 하나 (지연 11 개의 중앙값)
        Assert.Equal(10.0, Convert.ToDouble(((JObject)seek[0]!)["ms"]), 6);
        Assert.True(Convert.ToInt64(((JObject)seek[0]!)["dist"]) > 0);
        Assert.Single((List<object?>)entry["thr"]!);
        Assert.Equal(4194304 / 1e6 / 0.04, (double)((JObject)((List<object?>)entry["thr"]!)[0]!)["mbps"]!, 6); // 4 MiB / 40 ms (1e6 B 단위)
        Assert.Null(entry["invalidated_at"]);
    }

    [Fact]
    public void Failed_reads_are_dropped_but_still_count_against_the_cap()
    {
        _latency = (_, _, _) => null;
        OrbitCalibration cal = Make();

        long bytes = cal.Sample(_game, Ops, null, Lease(), default);

        Assert.Equal(Orbit3Constants.MaxProbeBytesPerStream, bytes);
        var entry = (JObject)((JObject)((JObject)JsonRead.ReadFile(ModelPath)!)["disks"]!)[Disk]!;
        Assert.Empty((List<object?>)entry["seek"]!);
        Assert.Empty((List<object?>)entry["thr"]!);
    }

    [Fact]
    public void No_op_that_fits_means_no_reads()
    {
        long bytes = Make().Sample(_game, [new ReadOp(@"D:\Games\Foo\tiny.pak", 0, 2000)], null, Lease(), default);

        Assert.Equal(0, bytes);
        Assert.Empty(_reads);
    }

    [Fact]
    public void No_coordinator_lease_means_no_reads()
    {
        Assert.Equal(0, Make().Sample(_game, Ops, null, null, default));
        Assert.Empty(_reads);
    }

    [Fact]
    public void Invalid_lease_means_no_reads()
    {
        using DiskReadCoordinator.Lease lease = Lease();
        _coordinator.Yield(_game.OriginRoot, "another game starts", "Other"); // 양보가 걸리면 대여가 무효

        Assert.False(lease.IsValid);
        Assert.Equal(0, Make().Sample(_game, Ops, null, lease, default));
        Assert.Empty(_reads);
    }

    [Fact]
    public void Running_or_exiting_game_means_no_reads()
    {
        _gameActive = true;
        Assert.Equal(0, Make().Sample(_game, Ops, null, Lease(), default));
        Assert.Empty(_reads);
    }

    [Fact]
    public void SameDiskGameActivityBlocksCalibrationReads()
    {
        GameSettings other = Games.Make("Other", "other.exe") with { OriginRoot = @"D:\Games\Other" };
        Func<GameSettings, bool> active = PrewarmEnv.GameActiveOnDisk([_game, other], (_, _) => true, g => g.Name == "Other");
        var cal = new OrbitCalibration(_dir, _log, (path, offset, length, _) =>
        {
            _reads.Add((path, offset, length));
            return TimeSpan.FromMilliseconds(10);
        }, () => _t0, _ => false, active, new Random(1));

        Assert.Equal(0, cal.Sample(_game, Ops, null, Lease(), default));
        Assert.Empty(_reads);
    }

    [Fact]
    public void Busy_disk_means_no_reads()
    {
        _diskBusy = true;
        Assert.Equal(0, Make().Sample(_game, Ops, null, Lease(), default));
        Assert.Empty(_reads);
    }

    [Theory]
    [InlineData(CapabilityOutcome.Block)]
    [InlineData(CapabilityOutcome.Unknown)]
    public void Anti_cheat_that_is_not_allowed_means_no_reads(CapabilityOutcome decision)
    {
        _decision = decision;
        Assert.Equal(0, Make().Sample(_game, Ops, "eac", Lease(), default));
        Assert.Empty(_reads);
    }

    [Fact]
    public void Cancelled_token_means_no_reads()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Equal(0, Make().Sample(_game, Ops, null, Lease(), cts.Token));
        Assert.Empty(_reads);
    }

    [Fact]
    public void Cancelling_in_the_middle_stops_before_the_next_read()
    {
        using var cts = new CancellationTokenSource();
        _latency = (count, _, _) =>
        {
            if (count == 3)
            {
                cts.Cancel();
            }

            return TimeSpan.FromMilliseconds(10);
        };

        Make().Sample(_game, Ops, null, Lease(), cts.Token);

        Assert.Equal(3, _reads.Count);
    }

    [Theory]
    [InlineData("vanguard", 15)]
    public void DefaultCapabilityGateAllowsVanguardCalibrationReads(string antiCheat, int expectedReads)
    {
        var cal = new OrbitCalibration(_dir, _log, (path, offset, length, _) =>
        {
            _reads.Add((path, offset, length));
            return _latency(_reads.Count, offset, length);
        }, () => _t0, _ => false, _ => false, new Random(1));

        cal.Sample(_game, Ops, antiCheat, Lease(), default);

        Assert.Equal(expectedReads, _reads.Count);
    }

    [Fact]
    public void CallerCancellationStopsCalibrationWhenKeepStreamIsEnabled()
    {
        using var caller = new CancellationTokenSource();
        _latency = (count, _, _) =>
        {
            if (count == 1)
            {
                caller.Cancel();
            }

            return TimeSpan.FromMilliseconds(10);
        };
        OrbitCalibration cal = Make();
        var streamer = new FakeStreamer();
        var prewarmer = new Prewarmer([_game], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = _ => false,
            ZoneBytes = _ => 1L << 30,
            TraceExists = _ => true,
            DeviceCalibration = (g, _, antiCheat, lease, ct) => cal.Sample(g, Ops, antiCheat, lease, ct),
        }, _log, null, () => _t0, coordinator: _coordinator);
        prewarmer.SetAuto(AutoSettings.Default with { KeepStreamOnGameStart = true });

        _ = prewarmer.Run(_game, "test", ct: caller.Token);

        Assert.Single(_reads);
    }

    [Fact]
    public void Lease_becoming_invalid_in_the_middle_stops_before_the_next_read()
    {
        using DiskReadCoordinator.Lease lease = Lease();
        _latency = (count, _, _) =>
        {
            if (count == 5)
            {
                _coordinator.Yield(_game.OriginRoot, "another game starts", "Other");
            }

            return TimeSpan.FromMilliseconds(10);
        };

        Make().Sample(_game, Ops, null, lease, default);

        Assert.Equal(5, _reads.Count);
    }

    [Fact]
    public void Game_becoming_active_in_the_middle_stops_before_the_next_read()
    {
        _latency = (count, _, _) =>
        {
            if (count == 4)
            {
                _gameActive = true;
            }

            return TimeSpan.FromMilliseconds(10);
        };

        Make().Sample(_game, Ops, null, Lease(), default);

        Assert.Equal(4, _reads.Count);
    }

    [Fact]
    public void Disk_turning_busy_by_its_own_reads_does_not_stop_the_sample()
    {
        _latency = (_, _, _) =>
        {
            _diskBusy = true;
            return TimeSpan.FromMilliseconds(10);
        };

        Make().Sample(_game, Ops, null, Lease(), default);

        Assert.Equal(15, _reads.Count);
    }

    [Theory]
    [InlineData("eac", 0)]
    [InlineData(null, 15)]
    public void Default_gate_follows_the_real_read_decision(string? kind, int reads)
    {
        var cal = new OrbitCalibration(_dir, _log, (path, offset, length, _) =>
        {
            _reads.Add((path, offset, length));
            return TimeSpan.FromMilliseconds(10);
        }, () => _t0, _ => false, _ => false, new Random(1));

        cal.Sample(_game, Ops, kind, Lease(), default);

        Assert.Equal(reads, _reads.Count);
    }

    [Fact]
    public void Disk_key_can_be_injected()
    {
        var cal = new OrbitCalibration(_dir, _log, (_, _, _, _) => TimeSpan.FromMilliseconds(10), () => _t0, _ => false, _ => false, new Random(1), _ => CapabilityOutcome.Allow, _ => "PhysicalDrive1");

        cal.Sample(_game, Ops, null, Lease(), default);

        var disks = (JObject)((JObject)JsonRead.ReadFile(ModelPath)!)["disks"]!;
        Assert.True(disks.ContainsKey("PhysicalDrive1"));
    }

    [Fact]
    public void Same_position_twice_is_skipped_after_the_retry_cap()
    {
        ReadOp[] one = [new(@"D:\Games\Foo.pak", 0, 4096)]; // 고를 수 있는 위치가 하나뿐
        Make().Sample(_game, one, null, Lease(), default);

        Assert.Single(_reads); // 첫 읽기만 나가고 거리 0 읽기는 건너뜀
    }

    [Fact]
    public void Non_positive_or_non_finite_stream_throughput_is_ignored_and_keeps_the_streak()
    {
        OrbitCalibration cal = Make();
        Feed(cal, 40);
        Assert.False(cal.NoteStreamThroughput(Disk, 1)); // 하한 아래 1회

        Assert.False(cal.NoteStreamThroughput(Disk, 0));
        Assert.False(cal.NoteStreamThroughput(Disk, -5));
        Assert.False(cal.NoteStreamThroughput(Disk, double.NaN));
        Assert.True(cal.NoteStreamThroughput(Disk, 1)); // 무시된 값이 연속을 끊지 않아 2회째
    }

    [Fact]
    public void Envelope_needs_nineteen_calibration_streams_per_metric()
    {
        OrbitCalibration cal = Make();
        Feed(cal, 18);
        Assert.Null(cal.Envelope(Disk)); // 스트림 18 개

        Feed(cal, 18, 18); // 36 개: 보정 창 18
        Assert.Null(cal.Envelope(Disk));

        Feed(cal, 1, 36); // 37 개: 적합 18 + 보정 19
        Assert.Equal(19, cal.Envelope(Disk)!.SeekN);

        Feed(cal, 1, 37); // 38 개: 적합 19 + 보정 19
        DeviceFit fit = cal.Envelope(Disk)!;
        Assert.Equal((19, 19), (fit.SeekN, fit.ThroughputN));
        Assert.Equal(10.0, fit.SeekHatMs, 1);
        Assert.Equal(100.0, fit.ThroughputHatMBps, 0);
        Assert.Null(cal.Envelope("E:"));
    }

    [Fact]
    public void Missing_one_series_gives_no_envelope()
    {
        OrbitCalibration cal = Make();
        for (int i = 0; i < 40; i++)
        {
            cal.Add(Disk, (10, i), null);
        }

        Assert.Null(cal.Envelope(Disk));
    }

    // 구간이 처음 생긴 37 번째 스트림 직후부터 기록이 쌓입니다. 이탈 크기를 0.08 씩 키워 앞선 이탈이 넓힌 구간도 매번 벗어나게 합니다.
    private OrbitCalibration WithMisses(int misses, bool seek)
    {
        OrbitCalibration cal = Make();
        Feed(cal, 37);
        Assert.NotNull(cal.Envelope(Disk));
        for (int i = 0; i < misses; i++)
        {
            Feed(cal, 1, 37 + i, seekLog: seek ? 0.08 * (i + 1) : 0, thrLog: seek ? 0 : 0.08 * (i + 1));
        }

        Feed(cal, 19 - misses, 37 + misses); // 기록 19 개 (n=19)
        return cal;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Four_misses_in_nineteen_keep_the_envelope_and_five_invalidate_one_metric_alone(bool seek)
    {
        Assert.NotNull(WithMisses(4, seek).Envelope(Disk)); // P(X>=4 | 19, 0.05) = 0.0132 >= 0.01

        File.Delete(ModelPath); // 같은 폴더의 앞선 기록을 이어받지 않게 함
        OrbitCalibration five = WithMisses(5, seek); // P(X>=5) = 0.0020 < 0.01: 한 지표만 이탈해도 무효
        Assert.Null(five.Envelope(Disk));
        var entry = (JObject)((JObject)((JObject)JsonRead.ReadFile(ModelPath)!)["disks"]!)[Disk]!;
        Assert.Equal(_t0, DateTimeOffset.Parse((string)entry["invalidated_at"]!, null, System.Globalization.DateTimeStyles.RoundtripKind));
        Assert.Contains(_log.Lines, l => l.Contains("invalidated", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalidated_envelope_comes_back_after_enough_new_streams()
    {
        OrbitCalibration cal = WithMisses(5, true);
        Assert.Null(cal.Envelope(Disk));

        Feed(cal, 36); // 아직 모자람 (보정 창 18)
        Assert.Null(cal.Envelope(Disk));
        Feed(cal, 2, 36);
        Assert.NotNull(cal.Envelope(Disk));
    }

    [Fact]
    public void Two_slow_streams_in_a_row_invalidate_through_the_pdh_check()
    {
        OrbitCalibration cal = Make();
        Feed(cal, 40);
        DeviceFit fit = cal.Envelope(Disk)!;
        double floor = fit.ThroughputHatMBps * fit.Envelope.ThroughputMin * 1e6;

        Assert.False(cal.NoteStreamThroughput(Disk, floor * 0.5)); // 1 회
        Assert.False(cal.NoteStreamThroughput(Disk, floor * 1.5)); // 정상 스트림이 연속을 끊음
        Assert.False(cal.NoteStreamThroughput(Disk, floor * 0.5));
        Assert.NotNull(cal.Envelope(Disk));

        Assert.True(cal.NoteStreamThroughput(Disk, floor * 0.5)); // 연속 2 회
        Assert.Null(cal.Envelope(Disk));
        Assert.False(cal.NoteStreamThroughput(Disk, 0)); // 구간이 없으면 아무 일도 없음
    }

    [Fact]
    public void Model_file_round_trips_and_keeps_only_the_latest_two_hundred()
    {
        OrbitCalibration cal = Make();
        Feed(cal, 250);
        DeviceFit before = cal.Envelope(Disk)!;

        var entry = (JObject)((JObject)((JObject)JsonRead.ReadFile(ModelPath)!)["disks"]!)[Disk]!;
        var seek = (List<object?>)entry["seek"]!;
        Assert.Equal(200, seek.Count);
        Assert.Equal(200, ((List<object?>)entry["thr"]!).Count);
        Assert.Equal(50L, ((JObject)seek[0]!)["dist"]); // 오래된 50 개를 버림
        var misses = (JObject)entry["misses"]!;
        Assert.Equal(Orbit3Constants.MonitorWindow, ((List<object?>)misses["seek"]!).Count);
        Assert.Equal(Orbit3Constants.MonitorWindow, ((List<object?>)misses["thr"]!).Count);

        DeviceFit after = Make().Envelope(Disk)!;
        Assert.Equal(before.Envelope, after.Envelope);
        Assert.Equal((before.SeekHatMs, before.ThroughputHatMBps), (after.SeekHatMs, after.ThroughputHatMBps));
        Assert.False(File.Exists(ModelPath + ".tmp"));
    }

    [Fact]
    public void Broken_file_is_moved_aside_and_means_no_envelope()
    {
        File.WriteAllText(ModelPath, "{ not json");

        OrbitCalibration cal = Make();

        Assert.Null(cal.Envelope(Disk));
        Assert.False(File.Exists(ModelPath));
        Assert.True(File.Exists(ModelPath + ".bad"));
        Feed(cal, 2); // 이후에는 새 파일을 씀
        Assert.True(File.Exists(ModelPath));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"version\": 2, \"disks\": {}}")]
    [InlineData("{\"version\": 1, \"disks\": {\"D:\": 5}}")]
    public void Wrong_shaped_file_is_moved_aside(string text)
    {
        File.WriteAllText(ModelPath, text);

        Assert.Null(Make().Envelope(Disk));
        Assert.True(File.Exists(ModelPath + ".bad"));
    }

    [Fact]
    public void Bad_entries_are_dropped_not_fatal()
    {
        File.WriteAllText(ModelPath, "{\"version\": 1, \"disks\": {\"D:\": {\"seek\": [{\"ms\": -1, \"dist\": 3}, {\"ms\": \"x\"}, 7], \"thr\": [{\"mbps\": 0}], \"misses\": {\"seek\": [0, 5, 1], \"thr\": \"x\"}, \"invalidated_at\": \"nope\"}}}");

        Assert.Null(Make().Envelope(Disk));
        Assert.False(File.Exists(ModelPath + ".bad"));
    }

    [Fact]
    public void Old_array_shaped_misses_are_dropped_without_error()
    {
        File.WriteAllText(ModelPath, "{\"version\": 1, \"disks\": {\"D:\": {\"seek\": [{\"ms\": 10, \"dist\": 3}], \"thr\": [{\"mbps\": 100}], \"misses\": [0, 1, 0], \"invalidated_at\": null}}}");

        OrbitCalibration cal = Make();

        Assert.False(File.Exists(ModelPath + ".bad"));
        Feed(cal, 40);
        Assert.NotNull(cal.Envelope(Disk));
        var misses = (JObject)((JObject)((JObject)((JObject)JsonRead.ReadFile(ModelPath)!)["disks"]!)[Disk]!)["misses"]!;
        Assert.NotEmpty((List<object?>)misses["seek"]!);
    }

    [Fact]
    public void Prewarm_hook_runs_before_the_stream_and_its_exception_does_not_stop_it()
    {
        int calls = 0;
        bool leaseSeen = false;
        var streamer = new FakeStreamer();
        var prewarmer = new Prewarmer([_game], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = _ => false,
            ZoneBytes = _ => 1L << 30,
            TraceExists = _ => true,
            DeviceCalibration = (g, ops, antiCheat, lease, ct) =>
            {
                calls++;
                leaseSeen = lease is { IsValid: true };
                throw new InvalidOperationException("boom");
            },
        }, _log, null, () => _t0, coordinator: _coordinator);

        PrewarmResult r = prewarmer.Run(_game, "test");

        Assert.Equal(1, calls);
        Assert.True(leaseSeen);
        Assert.True(r.Ran);
        Assert.Contains(_log.Lines, l => l.Contains("device calibration skipped", StringComparison.Ordinal));
    }

    [Fact]
    public void Injected_disk_key_names_the_model_while_busy_check_still_uses_the_drive_letter()
    {
        string? busyArg = null;
        var cal = new OrbitCalibration(_dir, _log, (path, offset, length, _) =>
        {
            _reads.Add((path, offset, length));
            return _latency(_reads.Count, offset, length);
        }, () => _t0, letter => { busyArg = letter; return false; }, _ => false, new Random(1), _ => _decision, _ => "disk7");

        Assert.Equal("disk7", cal.KeyOf(_game));
        _ = cal.Sample(_game, Ops, null, Lease(), default);

        Assert.Equal(Disk, busyArg);
        var disks = (JObject)((JObject)JsonRead.ReadFile(ModelPath)!)["disks"]!;
        Assert.True(disks.ContainsKey("disk7"));
        Assert.False(disks.ContainsKey(Disk));
    }

    private static List<(string Path, long Offset)> Positions(IEnumerable<(string Path, long Offset, int Length)> reads) => [.. reads.Select(r => (r.Path, r.Offset))];

    [Fact]
    public void Probe_reads_stay_at_least_the_gap_away_from_recently_read_positions()
    {
        OrbitCalibration cal = Make();
        ReadOp[] ops = [new(@"D:\Games\Foo\big.pak", 0, 64L << 30)];

        for (int i = 0; i < 4; i++)
        {
            cal.Sample(_game, ops, null, Lease(), default);
        }

        List<(string Path, long Offset)> got = Positions(_reads);
        Assert.Equal(4 * 15, got.Count);
        for (int i = 0; i < got.Count; i++)
        {
            for (int j = i + 1; j < got.Count; j++)
            {
                Assert.True(Math.Abs(got[i].Offset - got[j].Offset) >= Orbit3Constants.MinProbeGapBytes, $"{i} and {j} are too close");
            }
        }
    }

    [Fact]
    public void A_pool_too_small_for_the_gap_leaves_no_sample_once_its_positions_were_read()
    {
        OrbitCalibration cal = Make();
        ReadOp[] small = [new(@"D:\Games\Wall\a.pak", 0, 64L << 20)]; // 어느 두 위치도 최소 거리 안

        cal.Sample(_game, small, null, Lease(), default);
        int first = _reads.Count;
        long bytes = cal.Sample(_game, small, null, Lease(), default);

        Assert.True(first <= 1);
        Assert.Equal(0, bytes);
        Assert.Equal(first, _reads.Count);
    }

    [Fact]
    public void A_pooled_list_draws_files_of_every_game()
    {
        OrbitCalibration cal = Make();
        GameSettings bar = Games.Make("Bar", "Bar.exe");
        ReadOp[] pool = [new(@"D:\Games\Foo\a.pak", 0, 64L << 30), new(@"D:\Games\Bar\b.pak", 0, 64L << 30)];

        long bytes = cal.SampleBootstrap([(_game, null), (bar, null)], pool, default);

        Assert.True(bytes > 0);
        Assert.Contains(_reads, r => r.Path.Contains("Foo", StringComparison.Ordinal));
        Assert.Contains(_reads, r => r.Path.Contains("Bar", StringComparison.Ordinal));
    }

    [Fact]
    public void A_running_game_anywhere_in_the_pool_means_no_reads()
    {
        GameSettings bar = Games.Make("Bar", "Bar.exe");
        var cal = new OrbitCalibration(_dir, _log, (path, offset, length, _) =>
        {
            _reads.Add((path, offset, length));
            return TimeSpan.FromMilliseconds(10);
        }, () => _t0, _ => false, g => g.Name == "Bar", new Random(1), _ => CapabilityOutcome.Allow);
        ReadOp[] pool = [new(@"D:\Games\Foo\a.pak", 0, 64L << 30), new(@"D:\Games\Bar\b.pak", 0, 64L << 30)];

        Assert.Equal(0, cal.SampleBootstrap([(_game, null), (bar, null)], pool, default));
        Assert.Empty(_reads);
    }

    [Fact]
    public void A_stream_in_a_modest_pool_gets_all_fifteen_reads_for_every_seed()
    {
        ReadOp[] pool = [new(@"D:\Games\Foo.pak", 0, 3L << 30), new(@"D:\Games\Foo.pak", 0, 3L << 30)]; // 15 개의 128 MiB 창이 들어가는 크기

        for (int seed = 1; seed <= 30; seed++)
        {
            _reads.Clear();
            Make(seed).SampleBootstrap(_game, pool, null, default);
            Assert.Equal(15, _reads.Count);
        }
    }

    // 실기 모양: 게임 3개, 파일 수백 개, 대부분 64 MiB 미만, 큰 파일 몇 개
    private static ReadOp[] RealisticPool()
    {
        var ops = new List<ReadOp>();
        foreach (string game in new[] { "Frostpunk", "MapleStory", "Wallpaper" })
        {
            for (int i = 0; i < 150; i++)
            {
                ops.Add(new ReadOp($@"D:\Games\{game}\f{i}.pak", 0, ((i % 60) + 1) << 20));
            }

            ops.Add(new ReadOp($@"D:\Games\{game}\big.pak", 0, 6L << 30));
        }

        return [.. ops];
    }

    [Fact]
    public void A_realistically_shaped_pool_gives_bytes_on_every_early_stream_with_no_reason()
    {
        OrbitCalibration cal = Make();
        (GameSettings, string?)[] games = [(_game, null), (Games.Make("Bar", "Bar.exe"), null), (Games.Make("Baz", "Baz.exe"), null)];
        ReadOp[] pool = RealisticPool();

        for (int i = 0; i < 5; i++)
        {
            Assert.True(cal.SampleBootstrap(games, pool, default, out string? reason) > 0, $"stream {i}: {reason}");
            Assert.Null(reason);
        }
    }

    [Fact]
    public void Zero_byte_samples_name_the_reason()
    {
        (GameSettings, string?)[] one = [(_game, null)];
        string? reason;
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();

        Assert.Equal(0, Make().SampleBootstrap(one, Ops, cancelled.Token, out reason));
        Assert.Equal("cancelled", reason);
        _gameActive = true;
        Make().SampleBootstrap(one, Ops, default, out reason);
        Assert.Equal("game_active", reason);
        _gameActive = false;
        _diskBusy = true;
        Make().SampleBootstrap(one, Ops, default, out reason);
        Assert.Equal("disk_busy", reason);
        _diskBusy = false;
        _decision = CapabilityOutcome.Block;
        Make().SampleBootstrap(one, Ops, default, out reason);
        Assert.Equal("anticheat", reason);
        _decision = CapabilityOutcome.Allow;
        OrbitCalibration cal = Make();
        ReadOp[] small = [new(@"D:\Games\Wall\a.pak", 0, 64L << 20)];
        cal.SampleBootstrap(one, small, default, out _);
        cal.SampleBootstrap(one, small, default, out reason);
        Assert.Equal("no_far_position", reason);
        Make().SampleBootstrap(one, Ops, default, out reason);
        Assert.Null(reason);
    }

    private static double Wobble(int i) => Math.Exp(0.01 * ((i % 5) - 2));

    [Fact]
    public void SampleBootstrapReadsWithoutALeaseAndSavesTheBootstrapSource()
    {
        long bytes = Make().SampleBootstrap(_game, Ops, null, default);

        Assert.Equal(12 + 3, _reads.Count);
        Assert.True(bytes > 0);
        var entry = (JObject)((JObject)((JObject)JsonRead.ReadFile(ModelPath)!)["disks"]!)[Disk]!;
        Assert.Equal("bootstrap", ((JObject)((List<object?>)entry["seek"]!)[0]!)["source"]);
        Assert.Equal("bootstrap", ((JObject)((List<object?>)entry["thr"]!)[0]!)["source"]);
    }

    [Fact]
    public void StreamSamplesAreSavedWithTheStreamSource()
    {
        Make().Sample(_game, Ops, null, Lease(), default);

        var entry = (JObject)((JObject)((JObject)JsonRead.ReadFile(ModelPath)!)["disks"]!)[Disk]!;
        Assert.Equal("stream", ((JObject)((List<object?>)entry["seek"]!)[0]!)["source"]);
    }

    [Fact]
    public void SampleBootstrapKeepsTheOtherReadGates()
    {
        _gameActive = true;
        Assert.Equal(0, Make().SampleBootstrap(_game, Ops, null, default));
        _gameActive = false;
        _diskBusy = true;
        Assert.Equal(0, Make().SampleBootstrap(_game, Ops, null, default));
        _diskBusy = false;
        _decision = CapabilityOutcome.Block;
        Assert.Equal(0, Make().SampleBootstrap(_game, Ops, null, default));
        Assert.Empty(_reads);
    }

    [Fact]
    public void FileWithoutSourceIsReadAsStreamSamples()
    {
        var old = new JObject
        {
            ["version"] = 1L,
            ["disks"] = new JObject
            {
                [Disk] = new JObject
                {
                    ["seek"] = Enumerable.Range(0, 40).Select(i => (object?)new JObject { ["ms"] = 10.0 * Wobble(i), ["dist"] = (long)i }).ToList(),
                    ["thr"] = Enumerable.Range(0, 40).Select(i => (object?)new JObject { ["mbps"] = 100.0 * Wobble(i) }).ToList(),
                },
            },
        };
        File.WriteAllText(ModelPath, PyJson.Dumps(old));
        OrbitCalibration cal = Make();

        Assert.NotNull(cal.Envelope(Disk));
        cal.Add(Disk, (10, 1), 100);

        var entry = (JObject)((JObject)((JObject)JsonRead.ReadFile(ModelPath)!)["disks"]!)[Disk]!;
        Assert.All((List<object?>)entry["seek"]!, s => Assert.Equal("stream", ((JObject)s!)["source"]));
        Assert.All((List<object?>)entry["thr"]!, t => Assert.Equal("stream", ((JObject)t!)["source"]));
    }

    [Fact]
    public void BootstrapSamplesLeaveTheFitOnceStreamSamplesReachTwiceTheMinimum()
    {
        OrbitCalibration cal = Make();
        for (int i = 0; i < 40; i++)
        {
            cal.Add(Disk, (10 * Wobble(i), i), 100 * Wobble(i), bootstrap: true);
        }

        int stream = (2 * Orbit3Constants.MinCalibrationSamples) - 1;
        for (int i = 0; i < stream; i++)
        {
            cal.Add(Disk, (10 * Wobble(i), i), 100 * Wobble(i));
        }

        int all = 40 + stream;
        Assert.Equal(all - (all / 2), cal.Envelope(Disk)!.ThroughputN); // 부트스트랩이 같은 창에 시간순으로 들어 있음

        cal.Add(Disk, (10, 1), 100); // 운영 표본이 최소 개수의 2배

        Assert.Equal(stream + 1 - ((stream + 1) / 2), cal.Envelope(Disk)!.ThroughputN); // 운영 표본만 적합에 씀
        var entry = (JObject)((JObject)((JObject)JsonRead.ReadFile(ModelPath)!)["disks"]!)[Disk]!;
        Assert.Equal(40, ((List<object?>)entry["thr"]!).Count(t => (string?)((JObject)t!)["source"] == "bootstrap")); // 파일에는 그대로 남음
    }
}
