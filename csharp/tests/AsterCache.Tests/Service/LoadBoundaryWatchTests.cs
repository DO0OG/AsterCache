using AsterCache.Core.Json;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임 시작 때 로딩 경계 도달 시간 (load_boundary_ms): 도달 판정, 120 초 상한, 경계 없는 게임, auto_status.json 왕복.
public sealed class LoadBoundaryWatchTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
    private readonly string _dir = Directory.CreateTempSubdirectory("astercache-lb-").FullName;
    private readonly ListLog _log = new();
    private readonly GameSettings _game = Games.Make("a", "a.exe");
    private DateTimeOffset _clock = T0;
    private int _probes;

    public void Dispose() => Directory.Delete(_dir, true);

    private string StatusPath => Path.Combine(_dir, "auto_status.json");

    private AutoStatus Status()
    {
        var s = new AutoStatus(StatusPath, _log, () => T0);
        s.Set("a", "manual", null, AutoStage.Done, null);
        return s;
    }

    private static LoadBoundary Boundary(int files) =>
        new(5000, [.. Enumerable.Range(0, files).Select(i => new TraceEvent($"f{i}.bin", 0, 64 << 20))], (long)files << 26, 0);

    // slowUntil 번째 표본 (1 부터) 전까지는 느린 표본이 있고 그 뒤로 모두 빠름.
    private int? Run(AutoStatus status, int slowUntil, LoadBoundary? b)
    {
        _probes = 0;
        return LoadBoundaryWatch.Run(_game, status, _log, default, () => _clock, d => _clock += d, _ => b, s =>
        {
            _probes++;
            return new ProbeResult(s.Count, _probes < slowUntil ? 1 : 0, 1.0);
        });
    }

    [Fact]
    public void ReachedAtTheProbeWhereNoSampleIsSlowAndRecorded()
    {
        AutoStatus status = Status();

        Assert.Equal(9000, Run(status, 4, Boundary(20))); // 4 번째 표본 = 3 초 간격 3 번 뒤
        Assert.Equal(4, _probes);
        Assert.Equal(9000, status.Get("a")!.LoadBoundaryMs);
        Assert.Contains(_log.Lines, l => l.Contains("a: load boundary reached in 9000 ms", StringComparison.Ordinal));
    }

    [Fact]
    public void SamplesTheLastRangeOfTier0First()
    {
        IReadOnlyList<(string Path, long Offset, int Bytes)>? seen = null;
        LoadBoundaryWatch.Run(_game, Status(), _log, default, () => _clock, d => _clock += d, _ => Boundary(20), s =>
        {
            seen = s;
            return new ProbeResult(s.Count, 0, 1.0);
        });

        Assert.All(seen!, s => Assert.Equal(("f19.bin", 4096), (s.Path, s.Bytes)));
    }

    [Fact]
    public void NotReachedWithin120SecondsLeavesNoRecordAndLogsIt()
    {
        AutoStatus status = Status();

        Assert.Null(Run(status, int.MaxValue, Boundary(6)));
        Assert.Equal(41, _probes); // 0, 3, ... 120 초
        Assert.Null(status.Get("a")!.LoadBoundaryMs);
        Assert.Contains(_log.Lines, l => l.Contains("a: load boundary not reached within 120 s", StringComparison.Ordinal));
    }

    [Fact]
    public void RoundsNeverReuseOrNeighbourEarlierPositions()
    {
        var rounds = new List<IReadOnlyList<(string Path, long Offset, int Bytes)>>();
        LoadBoundaryWatch.Run(_game, Status(), _log, default, () => _clock, d => _clock += d, _ => Boundary(20), s =>
        {
            rounds.Add(s);
            return new ProbeResult(s.Count, rounds.Count < 5 ? 1 : 0, 1.0);
        });

        var all = rounds.SelectMany(r => r).ToList();
        Assert.Equal(40, all.Count);
        Assert.All(all.Select((a, i) => (a, i)), x => Assert.DoesNotContain(all.Skip(x.i + 1), b => b.Path == x.a.Path && Math.Abs(b.Offset - x.a.Offset) < LoadBoundaryWatch.MinGap));
    }

    [Fact]
    public void RunningOutOfFreshPositionsStopsWithoutRecord()
    {
        AutoStatus status = Status();

        Assert.Null(Run(status, int.MaxValue, Boundary(1))); // 64 곳 = 8 회차 뒤 소진
        Assert.Equal(8, _probes);
        Assert.Null(status.Get("a")!.LoadBoundaryMs);
        Assert.Contains(_log.Lines, l => l.Contains("a: load boundary not measurable (no unread sample positions left)", StringComparison.Ordinal));
    }

    [Fact]
    public void ProbeReadsDoNotMakeTheNextRoundLookReached()
    {
        var cached = new HashSet<(string, long)>();
        int? ms = LoadBoundaryWatch.Run(_game, Status(), _log, default, () => _clock, d => _clock += d, _ => Boundary(20), s =>
        {
            int slow = s.Count(x => !cached.Contains((x.Path, x.Offset)));
            foreach (var x in s)
            {
                cached.Add((x.Path, x.Offset));
            }

            return new ProbeResult(s.Count, slow, 1.0); // 안 읽은 위치는 느림
        });

        Assert.Null(ms); // 매 회차 새 위치라 계속 느림 → 상한까지 도달 못 함
    }

    [Fact]
    public void GameWithoutBoundaryIsIgnored()
    {
        AutoStatus status = Status();

        Assert.Null(Run(status, 1, null));
        Assert.Null(LoadBoundaryWatch.Run(_game, status, _log, default, () => _clock, d => _clock += d, _ => Boundary(0), _ => throw new InvalidOperationException()));
        Assert.Equal(0, _probes);
        Assert.Null(status.Get("a")!.LoadBoundaryMs);
    }

    [Fact]
    public void RecordRoundTripsAndOldFilesStillLoad()
    {
        AutoStatus status = Status();
        status.RecordLoadBoundary("a", 9000, T0);

        var game = (JObject)((JObject)((JObject)JsonRead.ReadFile(StatusPath)!)["games"]!)["a"]!;
        Assert.Equal((9000L, "2026-11-03T09:00:00.0000000+00:00"), (game["load_boundary_ms"], game["load_boundary_at"]));
        Assert.Equal((9000, T0), (new AutoStatus(StatusPath, _log, () => T0).Get("a")!.LoadBoundaryMs, new AutoStatus(StatusPath, _log, () => T0).Get("a")!.LoadBoundaryAt));

        AutoStatus plain = Status();
        plain.Set("b", "manual", null, AutoStage.Done, null);
        var b = (JObject)((JObject)((JObject)JsonRead.ReadFile(StatusPath)!)["games"]!)["b"]!;
        Assert.DoesNotContain("load_boundary_ms", b.Select(kv => kv.Key)); // 재지 않은 게임은 키 없음
        Assert.Null(new AutoStatus(StatusPath, _log, () => T0).Get("b")!.LoadBoundaryMs);
    }
}
