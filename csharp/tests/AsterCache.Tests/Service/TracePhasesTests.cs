using AsterCache.Core.Json;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Tracer;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 트레이스 범위의 common / map_initial 구분 (여러 회차에서 반복해 읽힌 것만 common) 과 시작 예열의 common 먼저 읽기.
public sealed class TracePhasesTests : IDisposable
{
    private const long Block = 65536;
    private const string A = @"D:\Games\Foo\a.bin";
    private const string B = @"D:\Games\Foo\b.bin";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "aster_phase_" + Guid.NewGuid().ToString("N"));
    private readonly GameSettings _game;

    public TracePhasesTests()
    {
        Directory.CreateDirectory(_dir);
        string plan = Path.Combine(_dir, "foo.plan.json");
        _game = new GameSettings("foo", ["foo.exe"], plan + ".prefetch.json", plan, @"D:\Games\Foo", [@"D:\Games\Foo"], "GAMES", 1440);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    private string Raw => AutoTracer.RawTracePath(_game);

    private void Run(int n, params (string Path, long Block, long Count)[] reads) => Run(_game, n, reads);

    private static void Run(GameSettings g, int n, params (string Path, long Block, long Count)[] reads) =>
        TraceWriter.WriteFile(TraceWriter.RunPath(AutoTracer.RawTracePath(g), n), reads.Select(r => new TraceEvent(r.Path, r.Block * Block, r.Count * Block)));

    // 회차 자료로 common 을 계산해 저장한 뒤 읽음 (Merge 끝과 같은 순서)
    private IReadOnlyList<TraceEvent>? Split(long minRunBytes = 0)
    {
        TracePhases.Recount(_game, minRunBytes);
        return TracePhases.CommonOf(_game, minRunBytes);
    }

    [Fact]
    public void BlocksReadInEveryRunAreCommonAndTheRestAreMapInitial()
    {
        Run(1, (A, 0, 10), (B, 100, 5));
        Run(2, (A, 4, 10), (B, 0, 5)); // a.bin 4..9 가 모든 회차에서 겹침
        Run(3, (A, 0, 20), (B, 200, 5));

        IReadOnlyList<TraceEvent> common = Split()!;

        TraceEvent only = Assert.Single(common);
        Assert.Equal((A, 4 * Block, 6 * Block), (only.Path, only.Offset, only.Length)); // 일부 회차에서만 읽은 블록과 b.bin 은 common 이 아님
    }

    [Fact]
    public void TooFewValidRunsPromoteNothing()
    {
        Run(1, (A, 0, 10));
        Run(2, (A, 0, 10));
        Assert.Null(Split()); // 두 회차

        Run(3, (A, 0, 1));
        Assert.Null(Split(5 * Block)); // 세 번째 회차는 읽기량이 모자라 유효하지 않음

        Run(3, (A, 0, 10));
        Assert.NotNull(Split(5 * Block)); // 유효 회차 세 번
    }

    [Fact]
    public void NoRunFilesMeansNoSplitAndNothingIsWritten()
    {
        Assert.Null(Split());
        Assert.False(File.Exists(TracePhases.CommonPath(_game)));

        File.WriteAllText(AutoTracer.BootTracePath(_game), "[]"); // 공유 프로필 (boot) 만 있는 게임
        File.WriteAllText(Raw, "[]"); // run 파일 없는 옛 형식의 병합본
        Assert.Null(Split());
        Assert.False(File.Exists(TracePhases.CommonPath(_game)));
    }

    [Fact]
    public void ResultIsSavedBesideTheTraceAndRecomputedWhenARunChanges()
    {
        Run(1, (A, 0, 10));
        Run(2, (A, 0, 10));
        Run(3, (A, 0, 10));
        Assert.Equal(10 * Block, Assert.Single(Split()!).Length);

        var saved = (JObject)JsonRead.ReadFile(TracePhases.CommonPath(_game))!;
        Assert.Equal(1L, saved["version"]);
        Assert.Equal(3L, saved["runs"]);
        Assert.Single((List<object?>)saved["ranges"]!);

        Run(4, (A, 0, 4)); // 새 회차가 겹침을 줄임
        Assert.Equal(4 * Block, Assert.Single(Split()!).Length);
        Assert.Equal(4L, ((JObject)JsonRead.ReadFile(TracePhases.CommonPath(_game))!)["runs"]);
    }

    [Fact]
    public void ExistingTraceReadersDoNotSeeTheNewFile()
    {
        Run(1, (A, 0, 10));
        Run(2, (A, 0, 10));
        Run(3, (A, 0, 10));
        File.WriteAllText(Raw, "[]");
        string? before = AutoTracer.TraceFingerprint(Raw);
        Func<string, bool> exists = File.Exists;

        TracePhases.Recount(_game, 0);

        Assert.True(File.Exists(TracePhases.CommonPath(_game)));
        Assert.Equal(3, AutoTracer.LastRunNumber(_game, exists)); // run 파일 번호·원시 트레이스·유효 회차 수는 그대로
        Assert.Equal(before, AutoTracer.TraceFingerprint(Raw));
        Assert.Equal(3, AutoTracer.RunCount(_game, exists, null));
        Assert.DoesNotContain(TracePhases.CommonPath(_game), Enumerable.Range(1, AutoTracer.MaxRunFiles).Select(n => TraceWriter.RunPath(Raw, n)));
    }

    [Fact]
    public void PrewarmOnlyLoadsTheSavedCommonAndNeverComputesFromRunFiles()
    {
        Run(1, (A, 0, 10));
        Run(2, (A, 0, 10));
        Run(3, (A, 0, 10));

        // run 파일을 독점 잠금: 읽기를 시도하면 IOException 으로 드러남 (읽기 호출 수를 셀 이음새가 없어 접근 시도로 확인)
        var locks = Enumerable.Range(1, 3).Select(n => new FileStream(TraceWriter.RunPath(Raw, n), FileMode.Open, FileAccess.Read, FileShare.None)).ToList();
        try
        {
            Assert.Throws<IOException>(() => JsonRead.ReadFile(TraceWriter.RunPath(Raw, 1))); // 잠금이 실제로 읽기를 막는지 확인
            Assert.Null(TracePhases.CommonOf(_game, 0)); // 저장본이 없으면 run 파일을 읽어 계산하지 않음 (계산은 Merge 끝에서만)
            Assert.False(File.Exists(TracePhases.CommonPath(_game)));
        }
        finally
        {
            foreach (FileStream s in locks)
            {
                s.Dispose();
            }
        }
    }

    [Fact]
    public void BrokenOrOtherVersionSavedCommonIsIgnored()
    {
        Run(1, (A, 0, 10));
        Run(2, (A, 0, 10));
        Run(3, (A, 0, 10));
        TracePhases.Recount(_game, 0);
        var saved = (JObject)JsonRead.ReadFile(TracePhases.CommonPath(_game))!;

        saved["version"] = 2L;
        File.WriteAllText(TracePhases.CommonPath(_game), PyJson.Dumps(saved, ensureAscii: false));
        Assert.Null(TracePhases.CommonOf(_game, 0)); // 버전이 다른 저장본

        File.WriteAllText(TracePhases.CommonPath(_game), "{ broken");
        Assert.Null(TracePhases.CommonOf(_game, 0)); // 깨진 저장본

        TracePhases.Recount(_game, 0);
        Assert.Single(TracePhases.CommonOf(_game, 0)!);
    }

    [Fact]
    public void SavedCommonWithABrokenRowIsIgnoredWithoutThrowing()
    {
        Run(1, (A, 0, 10));
        Run(2, (A, 0, 10));
        Run(3, (A, 0, 10));
        string path = TracePhases.CommonPath(_game);

        void Broken(Action<List<object?>> change) // 저장본을 다시 쓰고 첫 행만 깨뜨림
        {
            TracePhases.Recount(_game, 0);
            var saved = (JObject)JsonRead.ReadFile(path)!;
            change((List<object?>)saved["ranges"]!);
            File.WriteAllText(path, PyJson.Dumps(saved, ensureAscii: false));
        }

        Broken(rows => ((JObject)rows[0]!)["offset"] = "x");
        Assert.Null(TracePhases.CommonOf(_game, 0)); // 범위 값이 숫자가 아님

        Broken(rows => ((JObject)rows[0]!)["length"] = -Block);
        Assert.Null(TracePhases.CommonOf(_game, 0)); // 음수 길이

        Broken(rows => rows[0] = new JObject { { "offset", 0L }, { "length", Block } });
        Assert.Null(TracePhases.CommonOf(_game, 0)); // 경로 누락
    }

    [Fact]
    public void WithinKeepsTheStreamOrderAndClipsToTheCommonRanges()
    {
        TraceEvent[] common = [new(A, 10, 20), new(A, 100, 10), new(B, 0, 5)];
        ReadOp[] ops = [new(B, 0, 50), new(A, 0, 15), new(A, 25, 100), new(@"D:\Games\Foo\c.bin", 0, 9)];

        List<ReadOp> result = TracePhases.Within(common, ops);

        Assert.Equal([new ReadOp(B, 0, 5), new ReadOp(A, 10, 5), new ReadOp(A, 25, 5), new ReadOp(A, 100, 10)], result);
    }

    [Fact]
    public void TheRealEnvironmentHasNoCommonFirstReadForAGameWithoutRunFiles()
    {
        File.WriteAllText(AutoTracer.BootTracePath(_game), "[]"); // 트레이스 금지 게임·공유 프로필만 있는 게임

        Assert.Empty(PrewarmEnv.Create().CommonFirstOps(_game, 0, 0).Ops);
    }

    [Fact]
    public void PrewarmReadsTheCommonRangesBeforeTheHotSetStreamAndDoesNothingExtraWithoutThem()
    {
        var calls = new List<string>();
        IReadOnlyList<ReadOp> common = [new ReadOp(A, 0, Block)];
        Prewarmer Make(IReadOnlyList<ReadOp> ops) => new([_game], new OrderStreamer(calls), new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = _ => false,
            ZoneBytes = _ => 100L << 20,
            TraceExists = _ => true,
            CommonFirstOps = (_, _, _) => (ops, ops.Sum(o => o.Length)),
            ColdRead = (read, _) =>
            {
                calls.Add("common " + string.Join(',', read.Select(o => o.Path)));
                return (new StreamStats(read.Sum(o => o.Length), 1, 0, 0.01), true);
            },
        }, new ListLog(), null, () => DateTimeOffset.UnixEpoch);

        Assert.True(Make(common).Run(_game, "test").Ran);
        Assert.Equal(["common " + A, "stream"], calls);

        calls.Clear();
        Assert.True(Make([]).Run(_game, "test").Ran);
        Assert.Equal(["stream"], calls); // common 이 없으면 기존과 같음
    }

    [Fact]
    public void CommonIsNotReadWhenTheWholeStreamFailsTheFinalReserveCheck()
    {
        var calls = new List<string>();
        Prewarmer Make(long ramMib, long streamTotal) => new([_game], new OrderStreamer(calls), new PrewarmEnv
        {
            AvailableRamMib = () => ramMib,
            GameRunning = _ => false,
            ZoneBytes = _ => 100L << 20,
            TraceExists = _ => true,
            CommonFirstOps = (_, _, _) => ([new ReadOp(A, 0, Block)], streamTotal), // 공통은 작아도 이번 스트림 전체가 최종 예약을 넘김
            ColdRead = (read, _) =>
            {
                calls.Add("common " + string.Join(',', read.Select(o => o.Path)));
                return (new StreamStats(read.Sum(o => o.Length), 1, 0, 0.01), true);
            },
        }, new ListLog(), null, () => DateTimeOffset.UnixEpoch);

        Make(16000, Block).Run(_game, "test"); // 대조군: 여유가 있으면 common 을 먼저 읽음
        Assert.Contains(calls, c => c.StartsWith("common", StringComparison.Ordinal));

        calls.Clear();
        Make(8000, 3950L << 20).Run(_game, "test"); // 최종 예약 (4096 MiB) 을 넘김
        Assert.DoesNotContain(calls, c => c.StartsWith("common", StringComparison.Ordinal));
    }

    [Fact]
    public void CommonReadStopsBeforeAnyBatchWhenTheDiskLeaseIsLost()
    {
        var calls = new List<string>();
        var coordinator = new DiskReadCoordinator(new VolumeDisks(_ => new HashSet<uint> { 1 }, () => 0), new ListLog());
        var prewarmer = new Prewarmer([_game], new OrderStreamer(calls), new PrewarmEnv
        {
            AvailableRamMib = () => 16000,
            GameRunning = _ => false,
            ZoneBytes = _ => 100L << 20,
            TraceExists = _ => true,
            CommonFirstOps = (_, _, _) =>
            {
                coordinator.Yield(_game.OriginRoot, "game started"); // 공통 읽기 직전에 디스크 대여가 무효가 됨
                return ([new ReadOp(A, 0, Block)], Block);
            },
            ColdRead = (read, _) =>
            {
                calls.Add("common");
                return (new StreamStats(read.Sum(o => o.Length), 1, 0, 0.01), true);
            },
        }, new ListLog(), null, () => DateTimeOffset.UnixEpoch, coordinator);

        prewarmer.Run(_game, "test");

        Assert.DoesNotContain("common", calls);
    }

    private sealed class OrderStreamer(List<string> calls) : IGameStreamer
    {
        public StreamStats Stream(GameSettings game) => throw new NotSupportedException();

        public (StreamStats Stats, bool Complete) StreamPrewarm(GameSettings game, CancellationToken stop, Action<long, long>? progress, long maxBytes)
        {
            calls.Add("stream");
            progress?.Invoke(0, 100);
            return (new StreamStats(100, 1, 0, 0.01), !stop.IsCancellationRequested);
        }
    }
}
