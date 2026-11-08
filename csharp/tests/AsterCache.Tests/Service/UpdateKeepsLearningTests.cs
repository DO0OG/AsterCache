using System.Security.Cryptography;
using AsterCache.Core.Apply;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Tracer;
using Xunit;

namespace AsterCache.Tests.Service;

/// 일부 파일만 바뀐 업데이트: 바뀐 파일의 범위만 트레이스·예열 목록에서 빼고 나머지 학습으로 같은 존에 다시 계획합니다 (절반 이상이 바뀌면 전체를 .stale 로 치움).
public sealed partial class AutoRelocatorTests
{
    private const string OnlyChangedLine = "only the 1 changed file(s) removed from the trace";
    private const string SetAsideLine = "source files changed since the plan (game update), trace and plan set aside as .stale";

    private readonly List<IReadOnlySet<string>> _dropped = [];

    // 업데이트 감지 때 불리는 가짜: 바뀐 파일의 행을 Sessions 에서 빼고, 트레이스 파일 시각과 다시 학습 표지를 지금으로 (운영 구현과 같은 효과).
    private void DropRowsFromSessions()
    {
        _w.DropChanged = (g, paths) =>
        {
            _dropped.Add(paths);
            Sessions = [[.. Sessions[0].Where(e => !paths.Contains(e.Path))]];
            _w.FileTimes[AutoTracer.RawTracePath(g)] = _w.Now;
            _w.FileTimes[AutoTracer.UpdatedMarkPath(g)] = _w.Now;
        };
    }

    // 업데이트가 파일을 새로 써서 다른 자리 (존 밖) 에 놓음: 수정 시각도 바뀜.
    private void UpdateRewritten(string file)
    {
        File.SetLastWriteTimeUtc(file, File.GetLastWriteTimeUtc(file).AddHours(1));
        Vol.Ext[file] = [new Extent(0, file == F2 ? 150_000 : 160_000, (1 << 20) / Cb)];
    }

    private static string[] Paths(PlanFile plan) => [.. plan.Order.Select(o => o.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    [Fact]
    public void UpdateOfSomeFilesDropsOnlyThoseRangesAndPlansAgainFromTheRestIntoTheSameZone()
    {
        UsePrewarm();
        DropRowsFromSessions();
        long start = DoneAndReadyToReplan(out string trace);
        string run1 = TraceWriter.RunPath(trace, 1);
        _w.Files.Add(run1);
        _w.FileTimes[run1] = _w.Now - TimeSpan.FromDays(1);
        Assert.Equal([F1, F2], Paths(PlanFile.Load(_game.Plan)));
        int streams = Streams;
        int moves = Vol.Moves.Count;
        int daily = _status.RelocationsWithin(AutoRelocator.DailyWindow);

        UpdateRewritten(F2); // 핫 바이트의 40 %
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Equal([F2], Assert.Single(_dropped).Order().ToArray());
        Assert.Contains(trace, _w.Files); // 트레이스는 그대로 두고 바뀐 파일의 행만 뺌
        Assert.DoesNotContain(trace + ".stale", _w.Files);
        Assert.False(File.Exists(_game.Plan));
        Assert.True(File.Exists(_game.Plan + ".stale")); // 이전 존 시작을 알려 주는 옛 계획
        Assert.True(File.Exists(JournalPath + ".old")); // 끝난 저널은 보관
        Assert.Equal(AutoStage.Discovered, Entry.Stage);
        Assert.Null(Entry.RelocatedAt);
        Assert.StartsWith("game updated, will trace again on next run", Entry.Detail, StringComparison.Ordinal); // 업데이트 뒤 지연 규칙도 같이 지킴
        Assert.Contains(_log.Lines, l => l.Contains(OnlyChangedLine, StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains(SetAsideLine, StringComparison.Ordinal));
        Assert.Equal(daily, _status.RelocationsWithin(AutoRelocator.DailyWindow));
        Assert.True(AutoTracer.NextTrace(_game, _settings.Auto, WorldEnv(), _status).Wanted); // 다음 플레이로 바뀐 파일을 다시 학습

        Run(TimeSpan.FromMinutes(20)); // 업데이트 뒤 지연 (15 분) 이 지나면 남은 학습으로 다시 계획

        Assert.Equal(AutoStage.Done, Entry.Stage);
        PlanFile plan = PlanFile.Load(_game.Plan);
        Assert.Equal([start], _w.PlanStarts); // 이전 존 시작에 맞춰 계획
        Assert.Equal(start, plan.Zone.StartLcn);
        Assert.Equal([F1], Paths(plan)); // 바뀐 파일의 범위는 계획에서 빠지고 나머지는 남음
        Assert.Equal(moves, Vol.Moves.Count); // 이미 옮긴 조각은 다시 옮기지 않음
        Assert.DoesNotContain(_log.Lines, l => l.Contains("looking for a new one", StringComparison.Ordinal));
        Assert.True(Streams > streams); // 남은 학습으로 바로 예열

        Assert.True(AutoTracer.NextTrace(_game, _settings.Auto, WorldEnv(), _status).Wanted); // 계획을 마친 뒤에도 새 플레이가 생길 때까지 다시 학습 대기
        string run2 = TraceWriter.RunPath(trace, 2);
        _w.Files.Add(run2);
        _w.FileTimes[run2] = _w.Now;
        Assert.False(AutoTracer.NextTrace(_game, _settings.Auto, WorldEnv(), _status).Wanted);
    }

    [Fact]
    public void UpdateOfHalfOrMoreOfTheHotDataSetsEverythingAsideAsBefore()
    {
        Sessions = [Run((F1, 0), (F2, 0), (F1, 4), (F2, 8))]; // 두 파일이 핫 바이트의 절반씩
        DropRowsFromSessions();
        DoneAndReadyToReplan(out string trace);
        Assert.Single(PlanFile.Load(_game.Plan).Order.GroupBy(o => o.Path).Select(f => f.Sum(o => o.Clusters)).Distinct()); // 두 파일의 핫 양이 같음

        UpdateRewritten(F2);
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Empty(_dropped);
        Assert.DoesNotContain(trace, _w.Files);
        Assert.Contains(trace + ".stale", _w.Files);
        Assert.False(File.Exists(_game.Plan));
        Assert.True(File.Exists(_game.Plan + ".stale"));
        Assert.Equal(AutoStage.Discovered, Entry.Stage);
        Assert.Contains(_log.Lines, l => l.Contains(SetAsideLine, StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains(OnlyChangedLine, StringComparison.Ordinal));
    }

    [Fact]
    public void FilesWhoseFingerprintCannotBeReadCountAsChanged()
    {
        string f3 = Path.Combine(Root, "f3.bin");
        File.WriteAllBytes(f3, RandomNumberGenerator.GetBytes(1 << 20));
        Files[f3] = [new Extent(0, 11_000, (1 << 20) / Cb)];
        Vol.Ext[f3] = [new Extent(0, 11_000, (1 << 20) / Cb)];
        Sessions = [Run((F1, 0), (F1, 4), (F1, 12), (F2, 0), (f3, 0))]; // F1 60 %, F2 20 %, f3 20 %
        DropRowsFromSessions();
        DoneAndReadyToReplan(out _);

        File.SetLastWriteTimeUtc(f3, File.GetLastWriteTimeUtc(f3).AddHours(1)); // f3 는 바뀜, F2 는 읽을 수 없음 (잠김): 모르면 바뀐 것으로 침
        _w.Stat = p => p == F2 ? throw new IOException("locked") : PlanFile.FingerprintOf(p);
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Equal([F2, f3], Assert.Single(_dropped).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(_log.Lines, l => l.Contains("only the 2 changed file(s) removed from the trace", StringComparison.Ordinal));
        Assert.True(File.Exists(_game.Plan + ".stale"));
    }

    [Fact]
    public void AFailedDropFallsBackToSettingTheWholeTraceAside()
    {
        DoneAndReadyToReplan(out string trace);
        UpdateRewritten(F2);
        _w.DropChanged = (_, _) => throw new PyValueException("bad trace file"); // 읽을 수 없는 run 파일

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Contains(_log.Lines, l => l.Contains("changed ranges could not be removed from the trace (PyValueException), setting the whole trace aside", StringComparison.Ordinal));
        Assert.DoesNotContain(trace, _w.Files); // 업데이트 처리가 10 분마다 재시도만 하며 멈추지 않고 기존 전체 치우기로 감
        Assert.Contains(trace + ".stale", _w.Files);
        Assert.False(File.Exists(_game.Plan));
        Assert.True(File.Exists(_game.Plan + ".stale"));
        Assert.Equal(AutoStage.Discovered, Entry.Stage);
        Assert.Contains(_log.Lines, l => l.Contains(SetAsideLine, StringComparison.Ordinal));
    }

    // 트레이스가 존보다 커서 계획 (F1, F2) 밖의 파일 f3 (계획 뒤 바뀜: 실제 수정 시각이 가짜 시계보다 늦음) 과 f4 (계획 전부터 그대로) 가 있는 상태.
    private (string F3, string F4) OutsideThePlan()
    {
        string f3 = Path.Combine(Root, "f3.bin");
        string f4 = Path.Combine(Root, "f4.bin");
        foreach (string f in new[] { f3, f4 })
        {
            File.WriteAllBytes(f, RandomNumberGenerator.GetBytes(1 << 20));
            Files[f] = [new Extent(0, f == f3 ? 11_000 : 12_000, (1 << 20) / Cb)];
            Vol.Ext[f] = [new Extent(0, f == f3 ? 11_000 : 12_000, (1 << 20) / Cb)];
        }

        DropRowsFromSessions();
        DoneAndReadyToReplan(out _);
        Sessions = [[.. Sessions[0], .. Run((f3, 0), (f4, 0))]];
        File.SetLastWriteTimeUtc(f4, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        UpdateRewritten(F2); // 계획 안의 파일 하나 (40 %)
        return (f3, f4);
    }

    private void AssertOutsideFileDroppedAndTheRestPlanned(string f3, string f4)
    {
        Assert.Equal([F2, f3], Assert.Single(_dropped).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(_log.Lines, l => l.Contains("only the 2 changed file(s) removed from the trace", StringComparison.Ordinal));

        Run(TimeSpan.FromMinutes(20));

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Equal([F1, f4], Paths(PlanFile.Load(_game.Plan))); // 바뀐 f3 의 옛 오프셋이 빈 자리를 채우지 않음
        string[] warm = [.. ReadWarmRows(_game.Trace).Select(r => r.Path).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        Assert.Equal([F1, f4], warm);
    }

    [Fact]
    public void ChangedFilesOutsideThePlanAreDroppedFromTheNewPlanInputAndThePrewarmListButUnchangedOnesStay()
    {
        (string f3, string f4) = OutsideThePlan();
        Assert.True(PlanFile.Load(_game.Plan).Stats.ContainsKey("planned_at")); // 계획을 만든 시각이 기준

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        AssertOutsideFileDroppedAndTheRestPlanned(f3, f4);
    }

    [Fact]
    public void AFileOutsideThePlanWhoseModifiedTimeWasRolledBackIsCaughtByItsChangeTime()
    {
        (string f3, string f4) = OutsideThePlan();
        File.SetLastWriteTimeUtc(f3, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)); // 런처가 수정 시각을 매니페스트 시각으로 되돌림: 수정 시각만 보면 안 바뀐 파일
        _w.ChangeTime = p => p == f3 ? new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero) : DateTimeOffset.MinValue; // 그래도 ChangeTime 은 계획 뒤

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        AssertOutsideFileDroppedAndTheRestPlanned(f3, f4);
    }

    [Fact]
    public void AFileOutsideThePlanWhoseChangeTimeCannotBeReadCountsAsChanged()
    {
        (string f3, string f4) = OutsideThePlan();
        File.SetLastWriteTimeUtc(f3, new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc));
        _w.ChangeTime = p => p == f3 ? null : DateTimeOffset.MinValue; // 알 수 없음 = 바뀜

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        AssertOutsideFileDroppedAndTheRestPlanned(f3, f4);
    }

    [Fact]
    public void ReSavingThePlanOrMergingTheTraceAfterTheUpdateDoesNotMoveTheReferenceTimePastIt()
    {
        (string f3, string f4) = OutsideThePlan();
        DateTimeOffset later = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero); // 업데이트 뒤 계획 파일이 다시 저장되고 (stamp·Touch) 트레이스가 병합됨
        _w.FileTimes[_game.Plan] = later;
        _w.FileTimes[AutoTracer.RawTracePath(_game)] = later;

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        AssertOutsideFileDroppedAndTheRestPlanned(f3, f4);
    }

    [Fact]
    public void APlanWithoutThePlannedTimeFallsBackToTheEarlierOfThePlanAndTraceFileTimes()
    {
        (string f3, string f4) = OutsideThePlan();
        PlanFile plan = PlanFile.Load(_game.Plan);
        var stats = new JObject();
        foreach ((string key, object? value) in plan.Stats.Where(kv => kv.Key != "planned_at")) // 옛 계획: 만든 시각 표지가 없음
        {
            stats.Add(key, value);
        }

        (plan with { Stats = stats }).Save(_game.Plan);
        _w.FileTimes[_game.Plan] = _w.Now;
        _w.FileTimes[AutoTracer.RawTracePath(_game)] = _w.Now + TimeSpan.FromDays(1);

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        AssertOutsideFileDroppedAndTheRestPlanned(f3, f4);
    }

    [Fact]
    public void AnUnreadableTraceSetsEverythingAsideInsteadOfDroppingPartly()
    {
        DoneAndReadyToReplan(out string trace);
        UpdateRewritten(F2);
        _w.TracePaths = _ => throw new PyValueException("bad trace file");

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Empty(_dropped);
        Assert.Contains(trace + ".stale", _w.Files);
        Assert.True(File.Exists(_game.Plan + ".stale"));
        Assert.Contains(_log.Lines, l => l.Contains(SetAsideLine, StringComparison.Ordinal));
    }

    [Fact]
    public void AFailedDropDoesNotOverwriteAnOriginalStaleCopyWhenSettingTheTraceAside()
    {
        DoneAndReadyToReplan(out string trace);
        string run1 = TraceWriter.RunPath(trace, 1);
        _w.Files.Add(run1);
        _w.Files.Add(trace + ".stale"); // 일부 정리가 원본을 .stale 로 복사해 둔 뒤 쓰기 단계에서 실패한 상태
        _w.Files.Add(run1 + ".stale");
        UpdateRewritten(F2);
        _w.DropChanged = (_, _) => throw new IOException("disk full");

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.DoesNotContain(_w.Moved, m => m.To == trace + ".stale" || m.To == run1 + ".stale"); // 원본 .stale 은 그대로
        Assert.Contains(trace + ".stale.partial", _w.Files);
        Assert.Contains(run1 + ".stale.partial", _w.Files);
        Assert.DoesNotContain(trace, _w.Files);
        Assert.Equal(AutoStage.Discovered, Entry.Stage);
    }

    [Fact]
    public void AnOlderStaleCopyIsNotOverwrittenWhenALaterBigPatchSetsTheWholeTraceAside()
    {
        Sessions = [Run((F1, 0), (F2, 0), (F1, 4), (F2, 8))]; // 절반씩이라 전체를 치움
        DoneAndReadyToReplan(out string trace);
        string run1 = TraceWriter.RunPath(trace, 1);
        _w.Files.Add(run1);
        _w.Files.Add(trace + ".stale"); // 첫 업데이트가 남긴 원본 (이번 업데이트의 것이 아님)
        UpdateRewritten(F2);

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.DoesNotContain(_w.Moved, m => m.To == trace + ".stale"); // 첫 업데이트의 원본은 그대로
        Assert.Contains(trace + ".stale.update", _w.Files);
        Assert.Contains(run1 + ".stale", _w.Files); // 없던 이름은 그대로 .stale
        Assert.DoesNotContain(trace, _w.Files);
    }

    [Fact]
    public void ANewTraceIsStillWarmedWhileOnlyTheNextRunTraceIsAwaited()
    {
        UseWiredPrewarm();
        DoneAndReadyToReplan(out string trace);
        _status.ResetForReplan("bench", AutoRelocator.ReplanDetail); // 다음 플레이에서 다시 트레이스하는 상태 (곧 재계획하는 상태가 아님)
        _w.Now += TimeSpan.FromMinutes(16); // 업데이트 지연이 끝남
        _w.FileTimes[trace] = _w.Now; // 마지막 예열보다 새 트레이스
        int streams = Streams;

        Tick();

        Assert.True(Streams > streams);
    }

    [Fact]
    public void TheKeptReplanHoldSurvivesTheZoneLockWaitUntilThePlanIsMade()
    {
        UseAntiCheat("vanguard");
        Prewarmer prewarm = UseWiredPrewarm();
        DoneAndReadyToReplan(out _);
        UpdateAndDetect();
        _w.Now += TimeSpan.FromMinutes(16); // 지연은 끝남
        Func<GameSettings, IDisposable?> lockOk = _w.Lock;
        _w.Lock = g => g.Name == "bench" && !_w.Files.Contains(_game.Plan) ? null : lockOk(g); // 계획이 나올 때까지 잠금 대기
        int streams = Streams;

        Tick(); // 잠금 대기 Note 로 단계가 바뀜

        Assert.Equal(AutoStage.WaitingIdle, Entry.Stage);
        Assert.True(AutoRelocator.ReplanKept(_status, "bench")); // 보류 판정은 단계가 아니라 재계획 대기 표시를 따름
        Assert.Equal(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason);
        Assert.Equal(streams, Streams);

        _w.Lock = lockOk;
        Tick();

        Assert.False(AutoRelocator.ReplanKept(_status, "bench"));
        Assert.NotEqual(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason);
    }

    [Fact]
    public void UpdateOnAPartiallyAppliedPlanDropsOnlyTheChangedRangesAndKeepsTheMovedParts()
    {
        DropRowsFromSessions();
        PlanFile first = Make().Resolve(20000);
        Assert.Equal("write_cap", Apply(first, Partial(32)).Stopped);
        long moved = Vol.Moves.Sum(m => m.N);
        first.Save(_game.Plan);
        WaitingForTheRest();
        Vol.Moves.Clear();
        Fingerprint real = PlanFile.FingerprintOf(F2);
        _w.Stat = p => p == F2 ? new Fingerprint(p, real.Size + 1, real.MtimeNs) : PlanFile.FingerprintOf(p); // F2 만 바뀜 (핫 바이트의 40 %)
        Func<GameSettings, PlanFile, IReadOnlyList<PlanFile>, IReadOnlyList<IReadOnlyList<TraceEvent>>?, PlanFile> inner = _w.ReplanResidual!;
        _w.ReplanResidual = (g, c, o, sessions) =>
        {
            _w.Stat = PlanFile.FingerprintOf; // 새 계획이 바뀐 원본을 기준으로 삼은 뒤에는 다시 바뀐 것으로 보지 않게
            return inner(g, c, o, sessions);
        };

        Run(TimeSpan.FromMinutes(4));
        RunUntilDone();

        Assert.True(Entry.Stage == AutoStage.Done, Why());
        Assert.Equal([F2], Assert.Single(_dropped).Order().ToArray());
        Assert.Contains(AutoTracer.RawTracePath(_game), _w.Files); // 아직 끝나지 않은 계획에서도 트레이스는 치우지 않음
        Assert.DoesNotContain(AutoTracer.RawTracePath(_game) + ".stale", _w.Files);
        Assert.Contains(_log.Lines, l => l.Contains("partially applied plan replanned", StringComparison.Ordinal));
        Assert.Equal([F1], Paths(PlanFile.Load(_game.Plan)));
        Assert.True(Vol.Moves.Sum(m => m.N) <= 80 - moved); // 이미 옮긴 조각은 다시 옮기지 않음
        Assert.Single(JournalArchives());
        Assert.Equal(0, Remaining(PlanFile.Load(_game.Plan)));
    }

    [Fact]
    public void UpdateOnAPartiallyAppliedPlanDropsTheChangedRangesEvenWhenTheTraceBytesSayItIsABigPatch()
    {
        DropRowsFromSessions();
        PlanFile first = Make().Resolve(20000);
        Assert.Equal("write_cap", Apply(first, Partial(32)).Stopped);
        first.Save(_game.Plan);
        WaitingForTheRest();
        Vol.Moves.Clear();
        string f3 = Path.Combine(Root, "f3.bin"); // 계획 밖 파일: 계획 기준으로는 F2 의 40 % 뿐이지만 트레이스 바이트로는 절반 이상이 바뀐 큰 패치
        File.WriteAllBytes(f3, RandomNumberGenerator.GetBytes(1 << 20));
        Files[f3] = [new Extent(0, 11_000, (1 << 20) / Cb)];
        Vol.Ext[f3] = [new Extent(0, 11_000, (1 << 20) / Cb)];
        Sessions = [[.. Sessions[0], .. Run((f3, 1), (f3, 2), (f3, 3), (f3, 4), (f3, 5))]];
        Fingerprint real = PlanFile.FingerprintOf(F2);
        _w.Stat = p => p == F2 ? new Fingerprint(p, real.Size + 1, real.MtimeNs) : PlanFile.FingerprintOf(p);
        Func<GameSettings, PlanFile, IReadOnlyList<PlanFile>, IReadOnlyList<IReadOnlyList<TraceEvent>>?, PlanFile> inner = _w.ReplanResidual!;
        _w.ReplanResidual = (g, c, o, sessions) =>
        {
            _w.Stat = PlanFile.FingerprintOf;
            return inner(g, c, o, sessions);
        };

        Run(TimeSpan.FromMinutes(4));
        RunUntilDone();

        Assert.Equal([F2, f3], Assert.Single(_dropped).Order(StringComparer.Ordinal).ToArray()); // 바이트 기준 판정은 부분 적용 계획에서 꺼져 바뀐 범위가 빠짐
        Assert.Equal([F1], Paths(PlanFile.Load(_game.Plan)));
    }

    [Fact]
    public void PostUpdateWaitLineDescribesTheRecoveryCheckOrTheFixedMinutes()
    {
        Assert.Equal("a: waiting 15 min after update (drive may still be reorganizing)", AutoRelocator.PostUpdateLine("a", 15));
        Assert.Equal("a: waiting after update until the drive has recovered (up to 60 min)", AutoRelocator.PostUpdateLine("a", 60, probed: true));
    }

    [Fact]
    public void AnUpdateThatChangesMostOfTheTraceBytesOutsideThePlanSetsEverythingAsideEvenIfThePlanShareIsSmall()
    {
        (string f3, _) = OutsideThePlan(); // 계획 안에서는 F2 가 40 % 뿐
        Sessions = [[.. Sessions[0], .. Run((f3, 1), (f3, 2), (f3, 3), (f3, 4), (f3, 5))]]; // 바뀐 f3 이 트레이스 바이트의 절반 이상

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Empty(_dropped);
        Assert.Contains(AutoTracer.RawTracePath(_game) + ".stale", _w.Files);
        Assert.Contains(_log.Lines, l => l.Contains(SetAsideLine, StringComparison.Ordinal));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("changed file(s) removed from the trace", StringComparison.Ordinal));
    }

    // Worker 와 같은 연결의 예열기: 업데이트 지연과 재계획 중 조회를 자동 상태에서 얻음.
    private Prewarmer UseWiredPrewarm()
    {
        var prewarm = new Prewarmer(_settings.Games, _streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _avail,
            GameRunning = _ => _w.Running,
            ZoneBytes = _ => 1 << 20,
            TraceExists = _ => true,
        }, _log, null, () => _w.Now)
        {
            PostUpdateWait = g => AutoRelocator.PostUpdateWait(_status, _settings.Auto, g.Name, _w.Now),
            ReplanStart = g => AutoRelocator.Replanning(_status, g.Name)?.At,
            ReplanKept = g => AutoRelocator.ReplanKept(_status, g.Name),
        };
        prewarm.SetAuto(_settings.Auto);
        _relocator = new AutoRelocator(WorldEnv(), _status, _log, () => _settings, prewarm);
        return prewarm;
    }

    [Fact]
    public void NoNewPrewarmStartsAfterAnUpdateUntilTheReplanEnds()
    {
        DropRowsFromSessions();
        Prewarmer prewarm = UseWiredPrewarm();
        DoneAndReadyToReplan(out _);
        UpdateRewritten(F2);
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick(); // 업데이트 감지 (바뀐 범위만 빼고 다시 계획 대기)
        Assert.NotNull(AutoRelocator.Replanning(_status, "bench"));
        int streams = Streams;

        Assert.Equal(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason); // 업데이트 뒤 지연 (15 분) 중
        _w.Now += TimeSpan.FromMinutes(16); // 지연은 끝났지만 아직 재계획 전
        Assert.Null(AutoRelocator.PostUpdateWait(_status, _settings.Auto, "bench", _w.Now));
        Assert.Equal(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason);
        Assert.Equal(streams, Streams);
        Assert.Equal(AutoStage.Discovered, Entry.Stage);

        Tick(); // 새 계획을 만들면 업데이트 재계획 상태에서 벗어나 보류가 풀림 (재배치는 유휴를 더 기다림)
        Assert.Null(AutoRelocator.Replanning(_status, "bench"));
        Assert.NotEqual(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason);
        RunUntilDone();

        Assert.Equal(AutoStage.Done, Entry.Stage);
        Assert.Null(AutoRelocator.Replanning(_status, "bench"));
        Assert.True(Streams > streams);
        Assert.True(prewarm.Run(_game, "service start").Ran || prewarm.LastWarmed("bench") is not null);
    }

    [Fact]
    public void AfterEverythingIsSetAsideTheHoldEndsWithTheDelayAndDoesNotWaitForTheNextPlay()
    {
        Sessions = [Run((F1, 0), (F2, 0), (F1, 4), (F2, 8))]; // 절반씩이라 전체를 치움
        DropRowsFromSessions();
        Prewarmer prewarm = UseWiredPrewarm();
        DoneAndReadyToReplan(out _);
        UpdateRewritten(F2);
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        Assert.StartsWith(AutoRelocator.ReplanDetail, Entry.Detail, StringComparison.Ordinal);
        Assert.False(AutoRelocator.ReplanKept(_status, "bench")); // 다음 플레이에서 다시 트레이스: 곧 재계획할 상태가 아님
        Assert.Equal(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason); // 지연 (15 분) 동안은 기다림

        _w.Now += TimeSpan.FromMinutes(16);

        Assert.NotEqual(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason); // 지연이 끝나면 다음 플레이를 기다리지 않고 예열 재개
    }

    [Fact]
    public void ATraceForbiddenGameIsHeldUntilItsKeptTraceReplanEnds()
    {
        UseAntiCheat("vanguard");
        Prewarmer prewarm = UseWiredPrewarm();
        DoneAndReadyToReplan(out _);
        UpdateAndDetect();
        Assert.True(AutoRelocator.ReplanKept(_status, "bench"));

        _w.Now += TimeSpan.FromMinutes(16); // 지연은 끝났지만 아직 재계획 전
        Assert.Equal(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason);

        Tick(); // 유지한 트레이스로 다시 계획

        Assert.False(AutoRelocator.ReplanKept(_status, "bench"));
        Assert.NotEqual(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason);
    }

    [Fact]
    public void AKeptHoldEndsWhenTheKeptTraceDisappears()
    {
        UseAntiCheat("vanguard");
        DoneAndReadyToReplan(out _);
        UpdateAndDetect();
        Assert.True(AutoRelocator.ReplanKept(_status, "bench"));

        Assert.True(_w.Files.Remove(AutoTracer.RawTracePath(_game))); // 사용자 삭제·백신 격리
        _w.Now += TimeSpan.FromMinutes(16); // 업데이트 뒤 지연이 끝난 뒤
        Tick();

        Assert.False(AutoRelocator.ReplanKept(_status, "bench")); // 영영 풀리지 않는 보류가 아니라 다음 플레이에서 다시 트레이스하는 상태
        Assert.StartsWith(AutoRelocator.ReplanDetail, Entry.Detail, StringComparison.Ordinal);
    }

    // 진짜 트레이스 읽기처럼: 없는 파일의 행은 빠지고 길이 0 행은 예외.
    private void LoadTraceLikeTheRealLoader() => _w.LoadTraceOverride = () =>
        Sessions[0].Any(e => e.Length <= 0) ? throw new PyValueException("bad row") : [.. Sessions[0].Where(e => File.Exists(e.Path))];

    [Fact]
    public void ChangedBytesAreCountedFromTheOriginalRowsSoDeletedFilesStillMakeABigPatchWhole()
    {
        (string f3, _) = OutsideThePlan();
        Sessions = [[.. Sessions[0], .. Run((f3, 1), (f3, 2), (f3, 3), (f3, 4), (f3, 5))]]; // 지워진 f3 이 트레이스 바이트의 절반 이상
        File.Delete(f3);
        LoadTraceLikeTheRealLoader(); // 읽어 들이면 f3 의 행이 빠져 작은 패치로 보임

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.Empty(_dropped);
        Assert.Contains(AutoTracer.RawTracePath(_game) + ".stale", _w.Files);
        Assert.Contains(_log.Lines, l => l.Contains(SetAsideLine, StringComparison.Ordinal));
    }

    [Fact]
    public void AZeroLengthRowInTheTraceDoesNotBreakTheChangedShareCalculation()
    {
        (string f3, _) = OutsideThePlan();
        Sessions = [[.. Sessions[0], new TraceEvent(f3, 0, 0)]];
        LoadTraceLikeTheRealLoader(); // 진짜 읽기는 길이 0 행에서 bad row 예외

        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();

        Assert.DoesNotContain(_log.Lines, l => l.Contains("update check failed", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("changed file(s) removed from the trace", StringComparison.Ordinal)); // 작은 패치로 판정
        Assert.Single(_dropped);
    }

    [Fact]
    public void TheHoldAlsoEndsWhenTheReplanFailsAndTheStalePlanIsRestored()
    {
        DropRowsFromSessions();
        Prewarmer prewarm = UseWiredPrewarm();
        DoneAndReadyToReplan(out _);
        UpdateRewritten(F2);
        _w.Now += AutoRelocator.StaleCheckEvery;
        Tick();
        _w.Now += TimeSpan.FromMinutes(16);
        Assert.Equal(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason);
        _w.MakePlanAt = (_, _, _, _, _) => throw new PyValueException("zone overlaps hot data in a cycle; choose another zone");
        _w.MakePlan = (_, _, _) => throw new PyValueException("zone overlaps hot data in a cycle; choose another zone");

        Tick(); // 새 계획 실패 → 낡은 계획을 되살려 done (stale plan)

        Assert.Equal((AutoStage.Done, AutoRelocator.StalePlanDetail), (Entry.Stage, Entry.Detail));
        Assert.Null(AutoRelocator.Replanning(_status, "bench"));
        Assert.NotEqual(Prewarmer.PostUpdateDelay, prewarm.Run(_game, "service start").SkipReason);
    }

    // ---- 트레이스·예열 목록 파일 다시 쓰기 (실제 파일) ----

    private static void WriteRows(string path, params TraceEvent[] rows) => TraceWriter.WriteFile(path, rows);

    private static string[] RowPaths(string path) => ((List<object?>)JsonRead.ReadFile(path)!).Select(r => TraceEvents.FromJson(r).Path).ToArray();

    [Fact]
    public void DropChangedRewritesTheTraceRunFilesAndPrewarmListsKeepingOriginalsOnceAndMarksRelearning()
    {
        string plans = Path.Combine(Root, "plans2");
        Directory.CreateDirectory(plans);
        GameSettings g = _game with { Plan = Path.Combine(plans, "bench.plan.json"), Trace = Path.Combine(plans, "bench.plan.json.prefetch.json") };
        string raw = AutoTracer.RawTracePath(g);
        string run1 = TraceWriter.RunPath(raw, 1);
        string run2 = TraceWriter.RunPath(raw, 2);
        string warmOnly = PlanOutput.WarmOnlyPath(g.Trace);
        var a = new TraceEvent(@"D:\g\A.bin", 0, 65536);
        var b = new TraceEvent(@"D:\g\b.bin", 0, 65536);
        WriteRows(raw, a, b);
        WriteRows(run1, a, b);
        WriteRows(run2, a); // 바뀐 파일이 없는 파일은 건드리지 않음
        WriteRows(g.Trace, b, a);
        WriteRows(warmOnly, b);
        byte[] rawBefore = File.ReadAllBytes(raw);
        byte[] run2Before = File.ReadAllBytes(run2);
        HashSet<string> changed = new(StringComparer.OrdinalIgnoreCase) { @"d:\g\B.BIN" };

        using DataLock? gate = DataLock.TryAcquire(g.Plan);
        Assert.NotNull(gate);
        AutoTracer.DropChanged(g, changed);

        Assert.Equal([a.Path], RowPaths(raw));
        Assert.Equal([a.Path], RowPaths(run1));
        Assert.Equal([a.Path], RowPaths(g.Trace));
        Assert.Empty(RowPaths(warmOnly));
        Assert.Equal(rawBefore, File.ReadAllBytes(raw + ".stale")); // 원본은 .stale 로
        Assert.Equal(2, RowPaths(run1 + ".stale").Length);
        Assert.Equal(2, RowPaths(g.Trace + ".stale").Length);
        Assert.False(File.Exists(run2 + ".stale")); // 빠진 행이 없으면 복사도 다시 쓰기도 없음
        Assert.Equal(run2Before, File.ReadAllBytes(run2));
        Assert.True(File.Exists(AutoTracer.UpdatedMarkPath(g)));
        Assert.True(File.Exists(AutoTracer.RotatedMarkPath(g))); // 줄어든 run 이 다음 병합에서 빠지지 않게 정리된 병합본을 기준으로 유지

        DateTime markTime = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(AutoTracer.UpdatedMarkPath(g), markTime);
        AutoTracer.DropChanged(g, changed); // 중간에 멈췄다 다시 불려도 .stale 은 원본 그대로
        Assert.Equal(rawBefore, File.ReadAllBytes(raw + ".stale"));
        Assert.Equal(markTime, File.GetLastWriteTimeUtc(AutoTracer.UpdatedMarkPath(g))); // 다시 쓴 파일이 없으면 재학습 표지를 새로 쓰지 않음
    }

    [Fact]
    public void ASecondDropDoesNotOverwriteTheOriginalStaleCopyOfTheFirstUpdate()
    {
        string plans = Path.Combine(Root, "plans4");
        Directory.CreateDirectory(plans);
        GameSettings g = _game with { Plan = Path.Combine(plans, "bench.plan.json"), Trace = Path.Combine(plans, "bench.plan.json.prefetch.json") };
        string raw = AutoTracer.RawTracePath(g);
        var a = new TraceEvent(@"D:\g\A.bin", 0, 65536);
        var b = new TraceEvent(@"D:\g.bin", 0, 65536);
        WriteRows(raw, a, b);
        byte[] original = File.ReadAllBytes(raw);

        using DataLock? gate = DataLock.TryAcquire(g.Plan);
        Assert.NotNull(gate);
        AutoTracer.DropChanged(g, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { b.Path }); // 첫 업데이트
        AutoTracer.DropChanged(g, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { a.Path }); // 두 번째 업데이트: 정리된 병합본에서 또 뺌

        Assert.Empty(RowPaths(raw));
        Assert.Equal(original, File.ReadAllBytes(raw + ".stale")); // 첫 정리 전 원본이 그대로 (정리본으로 덮이지 않음)
        Assert.Equal([a.Path], RowPaths(raw + AutoRelocator.UpdateSuffix)); // 계획 시각을 모르면 이번 원본은 따로 보관
    }

    [Fact]
    public void WithoutAPlanTimeTheStaleUpdateCopyIsOverwrittenWithThisUpdatesOriginalAndTheFirstStaleStays()
    {
        string plans = Path.Combine(Root, "plans8");
        Directory.CreateDirectory(plans);
        GameSettings g = _game with { Plan = Path.Combine(plans, "bench.plan.json"), Trace = Path.Combine(plans, "bench.plan.json.prefetch.json") };
        string raw = AutoTracer.RawTracePath(g);
        var a = new TraceEvent(@"D:\g\A.bin", 0, 65536);
        var b = new TraceEvent(@"D:\g\b.bin", 0, 65536);
        var c = new TraceEvent(@"D:\g\c.bin", 0, 65536);
        WriteRows(raw, a, b, c);
        byte[] first = File.ReadAllBytes(raw);

        using DataLock? gate = DataLock.TryAcquire(g.Plan);
        Assert.NotNull(gate);
        AutoTracer.DropChanged(g, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { b.Path });
        AutoTracer.DropChanged(g, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { c.Path }); // .stale 과 .stale.update 가 모두 있는 상태
        WriteRows(raw, a, b);
        byte[] third = File.ReadAllBytes(raw);
        AutoTracer.DropChanged(g, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { b.Path }); // 계획 시각 없이 또 업데이트

        Assert.Equal(first, File.ReadAllBytes(raw + ".stale")); // 맨 처음 원본은 유지
        Assert.Equal(third, File.ReadAllBytes(raw + AutoRelocator.UpdateSuffix)); // 이번 원본이 덮여 사라지지 않고 보관
    }

    private string OlderUpdateFixture(string plans, out GameSettings g, long plannedUnix)
    {
        Directory.CreateDirectory(plans);
        g = _game with { Plan = Path.Combine(plans, "bench.plan.json"), Trace = Path.Combine(plans, "bench.plan.json.prefetch.json") };
        new PlanFile(PlanFile.CurrentVersion, Root, PlanFile.SplitDrive(Root), 4096, 65536, new PlanZone(0, 1), [], [], [], new JObject { { "planned_at", plannedUnix } }).Save(g.Plan);
        return AutoTracer.RawTracePath(g);
    }

    [Fact]
    public void AnOlderStaleCopyFromAnEarlierUpdateIsKeptAndThisUpdatesOriginalGoesToStaleUpdate()
    {
        DateTime planned = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        string raw = OlderUpdateFixture(Path.Combine(Root, "plans5"), out GameSettings g, new DateTimeOffset(planned).ToUnixTimeSeconds());
        var a = new TraceEvent(@"D:\g\A.bin", 0, 65536);
        var b = new TraceEvent(@"D:\g\b.bin", 0, 65536);
        var c = new TraceEvent(@"D:\g\c.bin", 0, 65536);
        WriteRows(raw, a, b, c);
        WriteRows(raw + ".stale", a); // 현재 계획보다 먼저 있던 이전 업데이트의 보관본
        File.SetLastWriteTimeUtc(raw + ".stale", planned.AddDays(-30));
        byte[] older = File.ReadAllBytes(raw + ".stale");
        byte[] thisOriginal = File.ReadAllBytes(raw);

        using DataLock? gate = DataLock.TryAcquire(g.Plan);
        Assert.NotNull(gate);
        AutoTracer.DropChanged(g, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { b.Path });
        Assert.Equal(older, File.ReadAllBytes(raw + ".stale")); // 이전 보관본은 그대로
        Assert.Equal(thisOriginal, File.ReadAllBytes(raw + AutoRelocator.UpdateSuffix)); // 이번 업데이트의 원본

        AutoTracer.DropChanged(g, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { c.Path }); // 같은 업데이트에서 다시 불림
        Assert.Equal(older, File.ReadAllBytes(raw + ".stale"));
        Assert.Equal(thisOriginal, File.ReadAllBytes(raw + AutoRelocator.UpdateSuffix)); // 이미 정리된 파일로 덮지 않음
        Assert.Equal([a.Path], RowPaths(raw));
    }

    [Fact]
    public void AStaleCopyMadeForThisUpdateIsLeftAloneWithoutAStaleUpdateCopy()
    {
        DateTime planned = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        string raw = OlderUpdateFixture(Path.Combine(Root, "plans6"), out GameSettings g, new DateTimeOffset(planned).ToUnixTimeSeconds());
        var a = new TraceEvent(@"D:\g\A.bin", 0, 65536);
        var b = new TraceEvent(@"D:\g\b.bin", 0, 65536);
        WriteRows(raw, a, b);
        File.Copy(raw, raw + ".stale");
        File.SetLastWriteTimeUtc(raw + ".stale", planned.AddDays(1)); // 계획을 만든 뒤에 보관한 사본 = 이번 업데이트의 것

        using DataLock? gate = DataLock.TryAcquire(g.Plan);
        Assert.NotNull(gate);
        AutoTracer.DropChanged(g, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { b.Path });

        Assert.False(File.Exists(raw + AutoRelocator.UpdateSuffix));
        Assert.Equal([a.Path], RowPaths(raw));
    }

    [Fact]
    public void AStaleCopyMadeInTheSameSecondAsThePlanIsNotTakenForThisUpdate()
    {
        DateTime planned = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        string raw = OlderUpdateFixture(Path.Combine(Root, "plans7"), out GameSettings g, new DateTimeOffset(planned).ToUnixTimeSeconds());
        var a = new TraceEvent(@"D:\g\A.bin", 0, 65536);
        var b = new TraceEvent(@"D:\g.bin", 0, 65536);
        WriteRows(raw, a, b);
        WriteRows(raw + ".stale", a);
        File.SetLastWriteTimeUtc(raw + ".stale", planned.AddMilliseconds(500)); // planned_at 은 초 단위로 잘리므로 이 계획을 만들기 직전의 이전 업데이트 사본일 수 있음
        byte[] older = File.ReadAllBytes(raw + ".stale");

        using DataLock? gate = DataLock.TryAcquire(g.Plan);
        Assert.NotNull(gate);
        AutoTracer.DropChanged(g, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { b.Path });

        Assert.Equal(older, File.ReadAllBytes(raw + ".stale"));
        Assert.True(File.Exists(raw + AutoRelocator.UpdateSuffix));
    }

    [Fact]
    public void DropChangedLeavesEverythingAloneWhenAnyRunFileCannotBeRead()
    {
        string plans = Path.Combine(Root, "plans3");
        Directory.CreateDirectory(plans);
        GameSettings g = _game with { Plan = Path.Combine(plans, "bench.plan.json"), Trace = Path.Combine(plans, "bench.plan.json.prefetch.json") };
        string raw = AutoTracer.RawTracePath(g);
        var a = new TraceEvent(@"D:\g\A.bin", 0, 65536);
        var b = new TraceEvent(@"D:\g\b.bin", 0, 65536);
        WriteRows(raw, a, b);
        WriteRows(TraceWriter.RunPath(raw, 1), a, b);
        File.WriteAllText(TraceWriter.RunPath(raw, 2), "{\"truncat"); // 깨진 run 파일
        byte[] rawBefore = File.ReadAllBytes(raw);

        using DataLock? gate = DataLock.TryAcquire(g.Plan);
        Assert.NotNull(gate);
        Assert.ThrowsAny<Exception>(() => AutoTracer.DropChanged(g, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { b.Path }));

        Assert.Equal(rawBefore, File.ReadAllBytes(raw)); // 읽기를 모두 마친 뒤에야 쓰므로 아무것도 바뀌지 않음
        Assert.False(File.Exists(raw + ".stale"));
        Assert.False(File.Exists(AutoTracer.UpdatedMarkPath(g)));
    }

    [Fact]
    public void DropChangedRequiresTheZoneLock()
    {
        Assert.Throws<InvalidOperationException>(() => AutoTracer.DropChanged(_game with { Plan = Path.Combine(Root, "plans", "nolock.plan.json") }, new HashSet<string>()));
    }
}
