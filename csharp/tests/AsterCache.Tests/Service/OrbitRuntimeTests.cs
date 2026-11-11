using System.Collections;
using System.Reflection;
using AsterCache.Core.Json;
using AsterCache.Core.Plan;
using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Service;

public sealed class OrbitRuntimeTests : IDisposable
{
    private const long Segment = 64L << 20;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_orbit_runtime_" + Guid.NewGuid().ToString("N"));
    private readonly GameSettings _game;
    private readonly List<ReadOp> _ops;
    private readonly List<ReadOp> _tail;

    public OrbitRuntimeTests()
    {
        Directory.CreateDirectory(_dir);
        _game = new GameSettings("foo", ["foo.exe"], Path.Combine(_dir, "foo.prefetch.json"), Path.Combine(_dir, "foo.plan.json"),
            @"D:\Games\Foo", [@"D:\Games"], "GAMES", 60);
        _ops =
        [
            new ReadOp(@"D:\Games\Foo\head.bin", 0, Segment),
            new ReadOp(@"D:\Games\Foo\a.bin", 0, Segment),
            new ReadOp(@"D:\Games\Foo\b.bin", 0, Segment),
        ];
        _tail = _ops.Skip(1).ToList();
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private string ModePath => Path.Combine(_dir, OrbitRuntime.ModeFileName);

    private JObject ShadowRow(string name) =>
        (JObject)((JObject)((JObject)((JObject)JsonRead.ReadFile(Path.Combine(_dir, "auto_status.json"))!)["games"]!)[name]!)["shadow_orbit"]!;

    private static DeviceFit Fit() => new(10, 100, 19, 19, new DeviceEnvelope(0.5, 2, 0.5, 2, OrbitEnvelope.Version));

    private OrbitModelSnapshot Snapshot(double? lambdaLow = 0.01, IReadOnlyList<OrbitEpisode>? episodes = null) =>
        new(episodes ?? [new OrbitEpisode([new OrbitBlock(_tail[0].Path, 0), new OrbitBlock(_tail[1].Path, 0)], 0.4, 0.6, 0.8, 100)],
            lambdaLow, 0.02, 0.03, 5, 0);

    private OrbitRuntime Runtime(string? mode = null, DeviceFit? fit = null, OrbitModelSnapshot? snapshot = null,
        Func<Orbit3Input, CancellationToken, Orbit3Decision>? decide = null,
        Func<GameSettings, CancellationToken, OrbitModelSnapshot>? load = null, AutoStatus? status = null, bool noDevice = false)
    {
        if (mode is not null)
        {
            OrbitRuntime.WriteMode(ModePath, mode);
        }

        return new OrbitRuntime(_dir, null, status, new ListLog(), device: _ => noDevice ? null : fit ?? Fit(), headBytes: _ => Segment,
            loadModel: load ?? ((_, _) => snapshot ?? Snapshot()), decide: decide ?? ((_, _) => new Orbit3Decision([1, 0], "safe_gain", 1, 1, 2, 1)));
    }

    [Fact]
    public void MissingModeDefaultsToActiveAndMalformedModeUsesShadow()
    {
        OrbitRuntime runtime = Runtime();

        Assert.Equal("active", runtime.Mode);
        File.WriteAllText(ModePath, "{broken");
        Assert.Equal("shadow", runtime.Mode);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("shadow")]
    [InlineData("no_device")]
    [InlineData("no_episode")]
    [InlineData("no_hazard")]
    [InlineData("no_gain")]
    [InlineData("d_low_not_positive")]
    public void BaselineModesAndMissingInputsPreserveTheExactOriginalOps(string scenario)
    {
        string? mode = scenario == "shadow" ? "shadow" : scenario == "off" ? "off" : null;
        DeviceFit? fit = scenario == "no_device" ? null : Fit();
        OrbitModelSnapshot snapshot = scenario switch
        {
            "no_episode" => Snapshot(episodes: []),
            "no_hazard" => Snapshot(lambdaLow: null),
            _ => Snapshot(),
        };
        int decisions = 0;
        int loads = 0;
        OrbitRuntime runtime = Runtime(mode, fit, snapshot,
            decide: (_, _) => { decisions++; return new Orbit3Decision(null, scenario == "d_low_not_positive" ? "d_low_not_positive" : "no_safe_gain", 0, 0, 1, 1); },
            noDevice: scenario == "no_device",
            load: (_, _) => { loads++; return snapshot; });

        List<ReadOp> result = runtime.Reorder(_game, _ops);

        Assert.Same(_ops, result);
        Assert.Equal(_ops, result);
        Assert.Equal(scenario is "shadow" or "no_gain" or "d_low_not_positive" ? 1 : 0, decisions);
        Assert.Equal(scenario is "off" or "no_device" ? 0 : 1, loads);
    }

    [Fact]
    public void EpisodeRegressionReturnsAndRecordsTheOriginalOps()
    {
        const long firstBytes = 2_000_000_000;
        const long secondBytes = 500_000_000;
        var set = new OrbitSegmentSet(
            [new ReadOp("file", 0, firstBytes), new ReadOp("file", firstBytes, secondBytes)],
            0,
            0,
            [new OrbitSegment(0, 1, firstBytes, 0, 0), new OrbitSegment(1, 1, secondBytes, firstBytes, 0)]);
        var input = new Orbit3Input(set,
        [
            new Orbit3Episode([(0, firstBytes)], 0.01, 0.01, 0.01),
            new Orbit3Episode([(1, secondBytes)], 1, 1, 1)
        ], 0.001, 0.001, 0.001, new Orbit3Device(0, 1_000_000, 1, 1, 1, 1), 0.9);
        Orbit3Decision decision = Orbit3.Decide(input);
        OrbitRuntime runtime = Runtime(decide: (_, _) => decision);

        List<ReadOp> result = runtime.Reorder(_game, _ops);

        Assert.Null(decision.Order);
        Assert.Equal("episode_regression", decision.Reason);
        Assert.Same(_ops, result);
        JObject plan = (JObject)JsonRead.ReadFile(OrbitModel.PathFor(_game))!;
        JObject recorded = (JObject)plan["decision"]!;
        Assert.Equal("episode_regression", recorded["reason"]);
        Assert.Equal(decision.WorstEpisodeDelayMs, recorded["worst_episode_delay_ms"]);
    }

    [Fact]
    public void ForcedCandidateUsesTheFixedLambdaOnlyWhenTheHazardIsMissingOrHasNoEvents()
    {
        OrbitRuntime.ForcedCandidate noHazard = Runtime(snapshot: Snapshot(lambdaLow: null)).ForceCandidate(_game, _ops, 2.0);
        OrbitRuntime.ForcedCandidate noEvents = Runtime(snapshot: new OrbitModelSnapshot(Snapshot().Episodes, 0, 0, 0.03, 5, 0)).ForceCandidate(_game, _ops, 2.0);
        OrbitRuntime.ForcedCandidate measured = Runtime(snapshot: Snapshot()).ForceCandidate(_game, _ops, 2.0);

        Assert.NotNull(noHazard.Ops);
        Assert.Equal("bench_override", noHazard.LambdaSource);
        Assert.Equal(2.0, noHazard.LambdaPerMin);
        Assert.NotNull(noEvents.Ops);
        Assert.Equal("bench_override", noEvents.LambdaSource);
        Assert.NotNull(measured.Ops);
        Assert.Equal("model", measured.LambdaSource);
        Assert.Equal(0.02 * 60000, measured.LambdaPerMin!.Value, 6); // 모형 값은 밀리초당 -> 분당으로 환산해 기록
    }

    [Fact]
    public void ForcedCandidateStillReportsMissingDeviceOrEpisodesEvenWithTheFixedLambda()
    {
        Assert.Equal("no_device_calibration", Runtime(noDevice: true).ForceCandidate(_game, _ops, 2.0).Reason);

        OrbitRuntime.ForcedCandidate noEpisode = Runtime(snapshot: Snapshot(episodes: [])).ForceCandidate(_game, _ops, 2.0);

        Assert.Null(noEpisode.Ops);
        Assert.Equal("no_episode_model", noEpisode.Reason);
    }

    [Fact]
    public void ServiceReorderNeverUsesTheFixedLambdaAndKeepsNoHazardModelWhenHazardIsMissing()
    {
        var log = new ListLog();
        int decisions = 0;
        var runtime = new OrbitRuntime(_dir, null, null, log, device: _ => Fit(), headBytes: _ => Segment,
            loadModel: (_, _) => Snapshot(lambdaLow: null), decide: (_, _) => { decisions++; return new Orbit3Decision([1, 0], "safe_gain", 1, 1, 2, 1); });

        List<ReadOp> result = runtime.Reorder(_game, _ops);

        Assert.Same(_ops, result);
        Assert.Equal(0, decisions);
        Assert.Contains(log.Lines, line => line.Contains("reason no_hazard_model", StringComparison.Ordinal));
    }

    [Fact]
    public void EpisodePathsAreMappedToTheOpPathSpellingBeforeSegmentMapping()
    {
        Orbit3Input? captured = null;
        OrbitModelSnapshot upper = Snapshot(episodes:
            [new OrbitEpisode([new OrbitBlock(_tail[0].Path.ToUpperInvariant(), 0), new OrbitBlock(_tail[1].Path.ToUpperInvariant(), 0)], 0.4, 0.6, 0.8, 100)]);
        OrbitRuntime runtime = Runtime(snapshot: upper, decide: (input, _) =>
        {
            captured = input;
            return new Orbit3Decision(null, "no_safe_gain", 0, 0, 1, 1);
        });

        Assert.Same(_ops, runtime.Reorder(_game, _ops));
        Assert.Equal(1, captured!.ModelConfidence);
    }

    [Fact]
    public void ActiveGainReordersOnlyTheTailAndPreservesTheOpMultiset()
    {
        Orbit3Input? captured = null;
        OrbitRuntime runtime = Runtime(decide: (input, _) =>
        {
            captured = input;
            return new Orbit3Decision([1, 0], "safe_gain", 1, 1, 2, 1);
        });

        List<ReadOp> result = runtime.Reorder(_game, _ops);

        Assert.NotSame(_ops, result);
        Assert.Equal(1, captured!.Set.HeadCount);
        Assert.Equal(1, captured.ModelConfidence);
        Assert.Equal(_ops[0], result[0]);
        Assert.Equal([_ops[0], _ops[2], _ops[1]], result);
        Assert.Equal(_ops.OrderBy(Key), result.OrderBy(Key));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BaselineCancellationAndTimeoutPreserveTheExactOriginalOps(bool timeout)
    {
        var status = new AutoStatus(Path.Combine(_dir, "auto_status.json"), new ListLog());
        status.Set(_game.Name, "manual", null, AutoStage.Discovered, null);
        using var cancelled = new CancellationTokenSource();
        Func<GameSettings, CancellationToken, OrbitModelSnapshot> load = (_, token) =>
        {
            // 전체 시험을 병렬로 돌리면 결정 예산 타이머가 늦게 발화하므로 대기 상한을 넉넉히 둡니다.
            token.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
            token.ThrowIfCancellationRequested();
            return Snapshot();
        };
        OrbitRuntime runtime = timeout ? Runtime(load: load, status: status) : Runtime(status: status);
        if (!timeout)
        {
            cancelled.Cancel();
        }

        List<ReadOp> result = runtime.Reorder(_game, _ops, timeout ? default : cancelled.Token);

        Assert.Same(_ops, result);
        Assert.Equal(timeout ? "timeout" : "cancelled", ShadowRow(_game.Name)["orbit_reason"]);
    }

    [Fact]
    public void BaselineTwoConsecutiveCalculationExceptionsDemoteAndKeepTheSameOps()
    {
        OrbitRuntime runtime = Runtime(load: (_, _) => throw new InvalidOperationException("calculation failed"));

        List<ReadOp> first = runtime.Reorder(_game, _ops);
        Assert.Equal("active", runtime.Mode);
        List<ReadOp> second = runtime.Reorder(_game, _ops);

        Assert.Same(_ops, first);
        Assert.Same(_ops, second);
        Assert.Equal("shadow", runtime.Mode);
        JObject mode = (JObject)JsonRead.ReadFile(ModePath)!;
        Assert.Equal("calculation_error", mode["reason"]);
        Assert.True(DateTimeOffset.TryParse((string?)mode["demoted_at"], out _));
    }

    [Fact]
    public void ShadowStatusAddsNewKeysAndTheExistingReaderIgnoresThem()
    {
        string path = Path.Combine(_dir, "auto_status.json");
        var status = new AutoStatus(path, new ListLog());
        status.Set(_game.Name, "manual", null, AutoStage.Discovered, null);
        OrbitRuntime runtime = Runtime("shadow", status: status);

        Assert.Same(_ops, runtime.Reorder(_game, _ops));

        JObject root = (JObject)JsonRead.ReadFile(path)!;
        JObject row = (JObject)((JObject)((JObject)root["games"]!)[_game.Name]!)["shadow_orbit"]!;
        Assert.Equal("shadow", row["mode"]);
        Assert.Equal(false, row["applied"]);
        Assert.Equal(19L, row["device_n"]);
        Assert.Equal(5L, row["value_runs"]);
        Assert.Equal(0.02, row["lambda"]);
        Assert.Equal(1.0, row["d_low"]);
        Assert.NotNull(new AutoStatus(path, new ListLog()).Get(_game.Name));
        Assert.Null(new AutoStatus(path, new ListLog()).Get(_game.Name)!.OrbitShadow);
    }

    [Fact]
    public void DemotionIsAtomicAndLeavesTheModeInShadow()
    {
        OrbitRuntime runtime = Runtime();

        Assert.True(runtime.Demote("coverage_invalidated"));
        Assert.False(runtime.Demote("second_reason"));

        JObject mode = (JObject)JsonRead.ReadFile(ModePath)!;
        Assert.Equal("shadow", mode["mode"]);
        Assert.Equal("coverage_invalidated", mode["reason"]);
        Assert.True(DateTimeOffset.TryParse((string?)mode["demoted_at"], out _));
        Assert.False(File.Exists(ModePath + ".tmp"));
    }

    [Fact]
    public void RuntimeRecordKeepsTheOrbit2rShadowFieldsAndAddsOnlyNewKeys()
    {
        var status = new AutoStatus(Path.Combine(_dir, "auto_status.json"), new ListLog());
        status.Set(_game.Name, "manual", null, AutoStage.Discovered, null);
        OrbitShadowRecord Shadow() => new("orbit", "planned", 7, 3, 1.5, DateTimeOffset.UtcNow,
            WorstLossMs: 2.5, CandidateRatio: 0.4, AllocatedBytes: 99, Fingerprint: status.ReplanGeneration(_game.Name));
        status.RecordOrbitShadow(_game.Name, Shadow());
        OrbitRuntime runtime = Runtime("shadow", status: status);

        Assert.Same(_ops, runtime.Reorder(_game, _ops));
        status.RecordOrbitShadow(_game.Name, Shadow()); // 그림자 계획이 나중에 다시 기록돼도 새 키가 남음

        JObject row = ShadowRow(_game.Name);
        Assert.Equal("orbit", row["selected"]);
        Assert.Equal("planned", row["reason"]);
        Assert.Equal(7L, row["blocks"]);
        Assert.Equal(2.5, row["worst_loss_ms"]);
        Assert.Equal(99L, row["alloc_bytes"]);
        Assert.Equal("shadow", row["mode"]);
        Assert.Equal(false, row["applied"]);
        Assert.True(row.ContainsKey("orbit_reason"));
        Assert.Equal(19L, row["device_n"]);
    }

    [Fact]
    public void ConsecutiveExceptionCountIsKeptPerGame()
    {
        GameSettings other = _game with { Name = "bar" };
        OrbitRuntime runtime = Runtime(load: (_, _) => throw new InvalidOperationException("calculation failed"));

        _ = runtime.Reorder(_game, _ops);
        _ = runtime.Reorder(other, _ops);
        Assert.Equal("active", runtime.Mode); // 게임이 번갈아 실패해도 한 게임의 연속 횟수는 1
        _ = runtime.Reorder(_game, _ops);

        Assert.Equal("shadow", runtime.Mode);
    }

    [Fact]
    public void ModeFileIsReadAgainOnlyWhenItsStampChanges()
    {
        OrbitRuntime runtime = Runtime("active");
        Assert.Equal("active", runtime.Mode);
        DateTime stamp = File.GetLastWriteTimeUtc(ModePath);

        File.WriteAllText(ModePath, File.ReadAllText(ModePath).Replace("active", "shadow")); // 같은 길이
        File.SetLastWriteTimeUtc(ModePath, stamp);
        Assert.Equal("active", runtime.Mode); // 수정 시각이 같아 캐시

        File.SetLastWriteTimeUtc(ModePath, stamp.AddSeconds(5));
        Assert.Equal("shadow", runtime.Mode);
        Assert.Equal(new DateTimeOffset(stamp.AddSeconds(5), TimeSpan.Zero), runtime.ModeChangedAt);
    }

    [Fact]
    public void ModeWriteDoesNotShareItsTempFileWithAnotherWriter()
    {
        using var other = new FileStream(ModePath + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None); // 다른 쓰기가 같은 이름을 잡고 있는 상태

        OrbitRuntime.WriteMode(ModePath, "off");

        Assert.Equal("off", Runtime().Mode);
    }

    [Fact]
    public void ModeChangedAtIsNullWithoutAModeFile()
    {
        Assert.Null(Runtime().ModeChangedAt);
    }

    [Fact]
    public void RestartWithoutModeFileHasNoCutoffEvenWhenPlansContainDecisions()
    {
        string plans = Path.Combine(_dir, "plans");
        Directory.CreateDirectory(plans);
        DateTimeOffset older = new(2026, 11, 8, 20, 0, 0, TimeSpan.Zero);
        DateTimeOffset latest = new(2026, 11, 9, 3, 0, 0, TimeSpan.Zero);
        foreach ((string name, DateTimeOffset at) in new[] { ("a", older), ("b", latest) })
        {
            File.WriteAllText(Path.Combine(plans, name + ".orbit.json"), PyJson.Dumps(new JObject
            {
                ["decision"] = new JObject { ["at"] = at.ToString("O") }
            }));
        }

        OrbitRuntime restarted = Runtime();

        Assert.Null(restarted.ModeChangedAt);
    }

    [Fact]
    public void ModeChangedAtIsTheFirstObservationOfAVanishedModeFile()
    {
        DateTimeOffset now = new(2026, 11, 9, 0, 0, 0, TimeSpan.Zero);
        var runtime = new OrbitRuntime(_dir, null, null, new ListLog(), device: _ => Fit(), now: () => now);
        OrbitRuntime.WriteMode(ModePath, "off");
        Assert.NotNull(runtime.ModeChangedAt);

        File.Delete(ModePath);
        Assert.Equal(now, runtime.ModeChangedAt);
        now = now.AddMinutes(5);
        Assert.Equal("active", runtime.Mode);
        Assert.Equal(now.AddMinutes(-5), runtime.ModeChangedAt); // 처음 관측한 시각이 유지됨
    }

    [Fact]
    public void ModeOffReturnsTheSameOpsWithoutWritingTheStatusFile()
    {
        string path = Path.Combine(_dir, "auto_status.json");
        OrbitRuntime runtime = Runtime("off", status: new AutoStatus(path, new ListLog()));

        List<ReadOp> result = runtime.Reorder(_game, _ops);

        Assert.Same(_ops, result);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void BaselineStreamerKeepsThePrewarmOpsWhenReorderIsMissingThrowsOrReturnsNothing()
    {
        GameSettings game = MakeStreamGame();
        long Total(Func<GameSettings, List<ReadOp>, CancellationToken, List<ReadOp>>? reorder, out bool reordered)
        {
            var streamer = new PrefetchStreamer { Reorder = reorder };
            var totals = new List<long>();
            _ = streamer.StreamPrewarmCoordinated(game, CancellationToken.None, (_, total) => totals.Add(total), 1L << 20, 0, () => false, true);
            reordered = streamer.WasPrewarmReordered(game);
            return totals[0];
        }

        long baseline = Total(null, out bool none);
        long thrown = Total((_, _, _) => throw new InvalidOperationException("boom"), out bool afterThrow);
        long nothing = Total((_, _, _) => null!, out bool afterNull);
        long same = Total((_, ops, _) => ops, out bool afterSame);
        long changed = Total((_, ops, _) => [new ReadOp(ops[0].Path, 0, 4096)], out bool afterChange);

        Assert.True(baseline > 4096);
        Assert.Equal([baseline, baseline, baseline], [thrown, nothing, same]);
        Assert.Equal([false, false, false, false], [none, afterThrow, afterNull, afterSame]);
        Assert.Equal(4096, changed); // 바뀐 목록일 때만 스트림 목록이 달라짐
        Assert.True(afterChange);
    }

    [Fact]
    public void ReorderReceivesTheStreamStopTokenAndACancelledDecisionIssuesNoRead()
    {
        GameSettings game = MakeStreamGame();
        using var stop = new CancellationTokenSource();
        CancellationToken seen = default;
        int progress = 0;
        var streamer = new PrefetchStreamer
        {
            Reorder = (_, ops, token) =>
            {
                seen = token;
                stop.Cancel();
                token.ThrowIfCancellationRequested();
                return ops;
            },
        };

        (StreamStats stats, bool complete) = streamer.StreamPrewarmCoordinated(game, stop.Token, (_, _) => progress++, 1L << 20, 0, () => true, true);

        Assert.Equal(stop.Token, seen);
        Assert.False(complete);
        Assert.True(stats.Aborted);
        Assert.Equal(0, progress);
    }

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 1)]
    public void BaselineMetadataIsReadForCalibrationOnlyWhenTheHookIsEnabled(bool hook, bool enabled, int expected)
    {
        int antiCheatCalls = 0;
        int hookCalls = 0;
        var prewarmer = new Prewarmer([_game], new CapturingStreamer(), new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = _ => false,
            ZoneBytes = _ => 2L << 20,
            TraceExists = _ => true,
            AntiCheatOf = _ =>
            {
                antiCheatCalls++;
                return null;
            },
            DeviceCalibration = hook ? (_, _, _, _, _) => hookCalls++ : null,
            DeviceCalibrationEnabled = enabled ? null : () => false,
        }, new ListLog());

        _ = prewarmer.Run(_game, "test");

        Assert.Equal(expected, antiCheatCalls);
        Assert.Equal(expected, hookCalls);
    }

    [Fact]
    public void OrbitModelSidecarIsNotEnumeratedAsAPlan()
    {
        string plan = Path.Combine(_dir, "foo.plan.json");
        File.WriteAllText(plan, "{}");
        File.WriteAllText(Path.Combine(_dir, "foo.orbit.json"), "{}");

        Assert.Equal(new[] { plan }, Directory.EnumerateFiles(_dir, "*.plan.json"));
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("memory_partial", false)]
    [InlineData("resume", false)]
    public void PrewarmerAllowsReorderOnlyWithoutPendingPartialState(string state, bool expected)
    {
        var streamer = new CapturingStreamer();
        var prewarmer = MakePrewarmer(streamer);
        if (state == "memory_partial")
        {
            SetDictionary(prewarmer, "_partial", _game.Name, (1L, 2L));
            SetDictionary(prewarmer, "_memoryPartial", _game.Name, (1L, 2L));
        }
        else if (state == "resume")
        {
            SetDictionary(prewarmer, "_resume", _game.Name, (2L, 1L << 20));
        }

        _ = prewarmer.Run(_game, "test");

        Assert.Equal(expected, Assert.Single(streamer.AllowReorder));
    }

    [Theory]
    [InlineData(1L << 20, 0L, false, true, 1)]
    [InlineData(1L << 20, 1L, false, true, 0)]
    [InlineData(0L, 0L, false, true, 0)]
    [InlineData(1L << 20, 0L, true, true, 0)]
    [InlineData(1L << 20, 0L, false, false, 0)]
    public void PrefetchStreamerKeepsBaselineForSkippedPartialCappedOrPendingStreams(long maxBytes, long skipBytes, bool capped, bool allowReorder, int expectedCalls)
    {
        GameSettings game = MakeStreamGame() with { MaxStreamSet = capped };
        var streamer = new PrefetchStreamer();
        using var stop = new CancellationTokenSource();
        int calls = 0;
        streamer.Reorder = (_, ops, _) =>
        {
            calls++;
            stop.Cancel();
            return [.. ops];
        };

        _ = streamer.StreamPrewarmCoordinated(game, stop.Token, null, maxBytes, skipBytes, () => false, allowReorder);

        Assert.Equal(expectedCalls, calls);
        Assert.Equal(expectedCalls > 0, streamer.WasPrewarmReordered(game));
    }

    [Fact]
    public void InterruptedReorderedStreamDoesNotSaveOrReuseAResumeOffset()
    {
        var streamer = new CapturingStreamer { WasReordered = true, Complete = false, BytesRead = 3L << 19 }; // 이전 진전 (1 MiB) 보다 더 읽고 끊김: 진전 없는 끊김은 재시도 기한까지 미뤄지므로
        var prewarmer = MakePrewarmer(streamer);
        SetDictionary(prewarmer, "_partial", _game.Name, (1L, 2L));

        Assert.False(prewarmer.Supplement(_game, CancellationToken.None));
        Assert.Empty(ReadDictionary(prewarmer, "_resume"));
        streamer.Complete = true;
        streamer.BytesRead = 2L << 20;

        Assert.True(prewarmer.Supplement(_game, CancellationToken.None));

        Assert.Equal(new long[] { 0, 0 }, streamer.SkipBytes);
        Assert.Empty(ReadDictionary(prewarmer, "_resume"));
    }

    private Prewarmer MakePrewarmer(CapturingStreamer streamer) => new([_game], streamer, new PrewarmEnv
    {
        AvailableRamMib = () => 16000,
        GameRunning = _ => false,
        ZoneBytes = _ => 2L << 20,
        TraceExists = _ => true,
    }, new ListLog());

    private GameSettings MakeStreamGame()
    {
        string root = Path.Combine(_dir, "stream_game");
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "asset.bin");
        File.WriteAllBytes(path, new byte[1 << 20]);
        string trace = Path.Combine(_dir, "stream_game.prefetch.json");
        string plan = Path.Combine(_dir, "stream_game.plan.json");
        new PlanFile(PlanFile.CurrentVersion, root, PlanFile.SplitDrive(root), 4096, 65536,
            new PlanZone(0, 1), [], [], [], new JObject()).Save(plan);
        TraceWriter.Write([[new TraceEvent(path, 0, 65536)]], trace);
        return new GameSettings("stream_game", ["game.exe"], trace, plan, root, [root], "GAMES", 60);
    }

    private static void SetDictionary(Prewarmer prewarmer, string field, string game, object value) =>
        ReadDictionary(prewarmer, field)[game] = value;

    private static IDictionary ReadDictionary(Prewarmer prewarmer, string field) =>
        (IDictionary)typeof(Prewarmer).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(prewarmer)!;

    private sealed class CapturingStreamer : IGameStreamer
    {
        public List<bool> AllowReorder { get; } = [];
        public List<long> SkipBytes { get; } = [];
        public bool Complete { get; set; } = true;
        public bool WasReordered { get; set; }
        public long BytesRead { get; set; } = 2L << 20;

        public StreamStats Stream(GameSettings game) => new(BytesRead, 1, 0, 0.01);

        public (StreamStats Stats, bool Complete) StreamPrewarmCoordinated(GameSettings game, CancellationToken stop, Action<long, long>? progress,
            long maxBytes, long skipBytes, Func<bool> canIssue, bool allowReorder)
        {
            AllowReorder.Add(allowReorder);
            SkipBytes.Add(skipBytes);
            progress?.Invoke(0, Math.Max(0, ((long)game.MaxStreamMib << 20) - skipBytes));
            return (new StreamStats(BytesRead, 1, 0, 0.01), Complete && !stop.IsCancellationRequested);
        }

        public bool WasPrewarmReordered(GameSettings game) => WasReordered;
    }

    private static string Key(ReadOp op) => $"{op.Path}|{op.Offset}|{op.Length}";
}
