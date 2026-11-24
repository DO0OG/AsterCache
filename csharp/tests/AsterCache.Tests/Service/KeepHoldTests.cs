using System.Reflection;
using System.Text.Json.Nodes;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;
using AsterCache.Service;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Service;

/// 게임 실행 중 유지 (붙들기 memory.keep_hold 와 붙들지 않는 게임의 빠진 범위를 기억하는 다시 만지기): 가짜 환경과 가짜 붙들기 (IKeepHold) 로
/// 대상·순서·건너뛰기·읽기 상한·하한과 내놓기·점검·끝낼 때·예외·안티치트 게이트·설정 스위치·재사용에 맞춘 시점·직렬화를 확인합니다.
public sealed class KeepHoldTests
{
    private const long Mib = 1L << 20;
    private readonly ListLog _log = new();
    private readonly List<IReadOnlyList<ReadOp>> _touched = [];
    private readonly HashSet<string> _running = [];
    private readonly FakeHold _hold = new();
    private readonly HashSet<string> _sparse = [];
    private readonly HashSet<string> _excluded = [];
    private DateTimeOffset _now = new(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
    private bool _keepHold = true;
    private bool _ladder = true;
    private bool _hasVolatileInfo = true;
    private string? _detected; // 설치 폴더에서 감지한 안티치트 종류
    private int _opened;
    private long _avail = 20000;
    private long _reuse; // 대기 목록 재사용 누계 (조회마다 _reuseStep 만큼 늘어남, 음수면 조회 불가)
    private long _reuseStep = 1100;
    private long _trimmed; // 메모리 관리자가 줄여 간 양 (서비스 작업 집합에서 빠짐)
    private Func<long>? _availFn;
    private Action<TimeSpan, CancellationToken>? _sleep;
    private readonly HashSet<string> _findMissing = []; // 찾기 패스가 빠진 것으로 확인하는 파일 (그 파일의 범위마다 경계 하나)
    private int _probeCalls; // 표본 읽기를 부른 횟수
    private long _findDiskPerMiss = 512 * 1024; // 빠진 범위 하나를 알아채는 데 읽는 양
    private readonly List<IReadOnlyList<ReadOp>> _findCalls = []; // 찾기 패스가 받은 목록
    private Action<long, double, Action<long, double>>? _findGroups; // 찾기 패스 도중 읽은 양이 쌓이는 때 (읽은 양, 시간, 알림) 를 흉내
    private long _p5 = -1; // 우선순위 5 대기 양 (MiB, 음수면 모름)
    private Func<IReadOnlyList<ReadOp>, TouchResult?>? _touchOverride; // 순서 패스 배치의 만지기 결과를 바꿈 (디스크 읽기를 흉내)
    private Func<string?>? _findBoundaryOf; // 찾기 패스가 부를 때마다 이 파일에서 이어진 두 지점의 빠짐 (경계) 을 확인하고 멈춤 (null 이면 없음)
    private readonly List<long> _findCaps = []; // 찾기 패스가 받은 디스크 읽기 상한
    private bool _writtenOn = true; // memory.keep_hold_written_files
    private readonly HashSet<string> _writtenFiles = []; // 게임이 쓰는 파일로 판정되는 경로
    private readonly Dictionary<string, string> _writtenReasons = new(StringComparer.OrdinalIgnoreCase); // 게임이 쓰는 파일을 붙들지 않는 사유 (없으면 조건 충족)

    private static string F(int n) => @"D:\a\" + n + ".bin";

    private static ReadOp Op(int file, long offsetMib, long lengthMib) => new(F(file), offsetMib * Mib, lengthMib * Mib);

    private static GameSettings Game(string name) => Games.Make(name, name + ".exe");

    private static GameSettings Cheat(string name) => Game(name) with { AntiCheat = "vanguard" }; // 고정을 막는 게임: 붙들지 않고 다시 만지기로 유지

    private static object TouchSerial(Prewarmer p) => typeof(Prewarmer).GetField("_touchSerial", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(p)!;

    private sealed class Streamer(IReadOnlyList<ReadOp> all) : IGameStreamer
    {
        public StreamStats Stream(GameSettings game) => new(30 * Mib, 1, 0, 0.01);

        public IReadOnlyList<ReadOp>? PlannedPrewarmReads(GameSettings game) => all;
    }

    /// 붙들기 가짜: 이 경로의 파일은 메모리에 없음 (읽던 범위에서 경계), 내놓기·점검·닫기를 기록합니다.
    private sealed class FakeHold : IKeepHold
    {
        public readonly List<IReadOnlyList<ReadOp>> Batches = [];
        public readonly List<long> Released = [];
        public readonly HashSet<string> Missing = new(StringComparer.OrdinalIgnoreCase);
        public long DiskPerMiss = 4 * Mib;
        public bool Measured = true; // 묶음의 하드 폴트 수를 시작과 끝 모두 읽었는가 (거짓이면 디스크 읽기를 가려내지 못한 묶음)
        public long PieceBeforeBoundary = Mib; // 경계를 만나기 전에 읽은 조각 (0 이면 경계에서 하나도 붙들지 못함)
        public int Rebases;
        public int Rechecks;
        public readonly List<bool> Refills = [];
        public readonly List<long> RecheckCaps = [];
        public readonly List<bool> DropOnCaps = [];
        public long ImageBytesPerBatch;
        public long SmallBytesPerBatch;
        public int Disposed;
        public Exception? Throw;
        public Func<HoldCheckResult>? NextCheck;
        public Action? OnHold;
        public int ErrorsPerBatch; // 배치마다 돌려줄 열기 오류 수 (공유 없이 열린 파일을 흉내)
        public readonly HashSet<string> WrittenMarked = new(StringComparer.OrdinalIgnoreCase);
        public readonly HashSet<string> ChangedWritten = new(StringComparer.OrdinalIgnoreCase); // 게임이 바꾼 것으로 돌려줄 파일 (5 초 확인이 닫음)
        public int WrittenChecks;
        public static readonly string Back = F(90); // 내놓은 부분을 대신하는 가짜 범위의 경로
        public long PendingBytes; // 내놓았고 아직 다시 붙들지 않은 양
        public readonly List<ReadOp> GivenUp = [];
        public List<ReadOp> PendingOps = []; // 내놓았고 아직 다시 붙들지 않은 범위를 직접 정함 (비어 있으면 PendingBytes 의 가짜 범위)
        public readonly HashSet<(string Path, long Offset)> MissingRanges = []; // 이 범위에서만 경계를 만남
        private readonly Dictionary<string, long> _writtenBytes = new(StringComparer.OrdinalIgnoreCase);
        private long _held;
        private readonly List<ReadOp> _ranges = []; // 지금 붙든 범위 (붙든 순서)

        public long HeldBytes => _held;

        public IReadOnlyList<ReadOp> HeldRanges() => [.. _ranges];

        public long WrittenHeldBytes => _writtenBytes.Values.Sum();

        public int WrittenFiles => _writtenBytes.Count(p => p.Value > 0);

        public void MarkWritten(IReadOnlyCollection<string> paths) => WrittenMarked.UnionWith(paths);

        public IReadOnlyList<WrittenRelease> ReleaseChangedWritten()
        {
            WrittenChecks++;
            List<WrittenRelease> released = [];
            foreach (string path in ChangedWritten.Where(p => _writtenBytes.TryGetValue(p, out long b) && b > 0).ToList())
            {
                released.Add(new WrittenRelease(path, _writtenBytes[path]));
                _ = _ranges.RemoveAll(o => o.Path == path);
                _held -= _writtenBytes[path];
                _writtenBytes[path] = 0;
            }

            return released;
        }

        public int Files => 3;

        public int Views => 3;

        public void Rebase() => Rebases++;

        public HoldBatchResult Hold(IReadOnlyList<ReadOp> batch, CancellationToken ct)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            OnHold?.Invoke();
            Batches.Add(batch);
            long bytes = 0;
            for (int i = 0; i < batch.Count; i++)
            {
                if (Missing.Contains(batch[i].Path) || MissingRanges.Contains((batch[i].Path, batch[i].Offset)))
                {
                    bytes += PieceBeforeBoundary; // 경계를 만나기 전에 읽은 한 조각
                    _held += bytes;
                    _ranges.AddRange(batch.Take(i));
                    if (PieceBeforeBoundary > 0)
                    {
                        _ranges.Add(new ReadOp(batch[i].Path, batch[i].Offset, PieceBeforeBoundary));
                    }
                    return new HoldBatchResult(bytes, DiskPerMiss, 0, 0, 0, 0, 1, 100, 0.1, 0.05, i, Measured: Measured);
                }

                bytes += batch[i].Length;
                if (WrittenMarked.Contains(batch[i].Path))
                {
                    _writtenBytes[batch[i].Path] = _writtenBytes.GetValueOrDefault(batch[i].Path) + batch[i].Length;
                }
            }

            _held += bytes;
            _ranges.AddRange(batch);
            PendingBytes = Math.Max(0, PendingBytes - batch.Where(o => o.Path == Back).Sum(o => o.Length));
            _ = PendingOps.RemoveAll(batch.Contains);
            return new HoldBatchResult(bytes, 0, 0, ErrorsPerBatch, 0, 1, 1, 0, 0.1, 0.05, -1, ImageBytesPerBatch, SmallBytesPerBatch, Measured);
        }

        public long Release(long bytes)
        {
            long released = Math.Min(_held, bytes);
            for (long left = released; left > 0 && _ranges.Count > 0;)
            {
                ReadOp last = _ranges[^1];
                long n = Math.Min(left, last.Length);
                _ranges.RemoveAt(_ranges.Count - 1);
                if (n < last.Length)
                {
                    _ranges.Add(new ReadOp(last.Path, last.Offset, last.Length - n));
                }

                left -= n;
            }

            _held -= released;
            PendingBytes += released;
            Released.Add(released);
            return released;
        }

        public IReadOnlyList<ReadOp> ReleasedRanges(long maxBytes)
        {
            if (PendingOps.Count > 0)
            {
                long total = 0;
                return [.. PendingOps.TakeWhile(o => (total += o.Length) - o.Length < maxBytes)];
            }

            return PendingBytes > 0 && maxBytes > 0 ? [new ReadOp(Back, 0, Math.Min(PendingBytes, maxBytes))] : [];
        }

        public void GiveUp(IReadOnlyList<ReadOp> ranges)
        {
            GivenUp.AddRange(ranges);
            _ = PendingOps.RemoveAll(ranges.Contains);
            PendingBytes = Math.Max(0, PendingBytes - ranges.Where(o => o.Path == Back).Sum(o => o.Length));
        }

        public HoldCheckResult Recheck(long refillStopBytes, CancellationToken ct, bool refill = true, bool dropOnCap = true, Func<bool>? memoryLow = null)
        {
            Rechecks++;
            Refills.Add(refill);
            RecheckCaps.Add(refillStopBytes);
            DropOnCaps.Add(dropOnCap);
            return NextCheck?.Invoke() ?? new HoldCheckResult(_held, 0, 0, 0, false, [], 1, 0, 0.5, 0.2);
        }

        public void Dispose()
        {
            Disposed++;
            _held = 0;
            _ranges.Clear();
        }
    }

    // 8 MiB 범위 여섯 개 (파일 1~5, 파일 2 는 두 번)
    private static readonly ReadOp[] Reads = [Op(1, 0, 8), Op(2, 0, 8), Op(2, 8, 8), Op(3, 0, 8), Op(4, 0, 8), Op(5, 0, 8)];

    private Prewarmer Make(IReadOnlyList<ReadOp> reads, GameSettings? game = null)
    {
        GameSettings a = game ?? Game("a");
        var streamer = new Streamer(reads);
        var p = new Prewarmer([a, Game("b")], streamer, new PrewarmEnv
        {
            AvailableRamMib = () => _availFn?.Invoke() ?? _avail,
            GameRunning = g => _running.Contains(g.Name),
            ZoneBytes = g => Math.Min(30, g.StreamCapBytes(30 * Mib) / Mib) * Mib,
            TraceExists = _ => true,
            PhysicalRamBytes = () => 64L << 30,
            ProbeReadMs = _ => new ProbeResult(8, 0, 0.2),
            DiskBytesPerSecond = _ => 0,
            CommonFirstOps = (_, _, _) => ([], 0),
            ColdRead = (ops, _) => (new StreamStats(ops.Sum(o => o.Length), 1, 0, 0.01), true),
            StandbyLadderEnabled = () => _ladder,
            Lists = () => _reuseStep < 0 ? null : new MemoryLists(0, _reuse += _reuseStep * Mib, 8000 * Mib, _p5 < 0 ? -1 : _p5 * Mib),
            FindResident = (ops, cap, ct, onGroup) => FakeFind(ops, cap, onGroup),
            TouchRanges = (ranges, ct) =>
            {
                _touched.Add(ranges);
                Assert.True(!_serialChecked || Monitor.IsEntered(TouchSerial(_prewarmer!)), "touching must run under the serialization lock");
                return _touchOverride?.Invoke(ranges) ?? new TouchResult(ranges.Sum(o => o.Length), 0.1, 0, 0, TouchEnd.Completed);
            },
            ListStamp = _ => "s1",
            IsSparse = path => _sparse.Contains(path),
            KeepHoldEnabled = () => _keepHold,
            KeepProbeSamples = (_, _) =>
            {
                _probeCalls++;
                return null;
            },
            AntiCheatOf = _ => _detected,
            OpenHold = () =>
            {
                _opened++;
                return _hold;
            },
            HoldExclusion = _ => _hasVolatileInfo
                ? new HoldFileRules(path => _excluded.Contains(path) ? "volatile" : _writtenReasons.GetValueOrDefault(path), _writtenOn ? path => _writtenFiles.Contains(path) : null)
                : null,
            WorkingSetBytes = () => (1000 * Mib) + _hold.HeldBytes - _trimmed,
            Sleep = (t, ct) => _sleep?.Invoke(t, ct),
        }, _log, null, () => _now);
        Assert.True(p.Run(a, "service start").Ran);
        _now += TimeSpan.FromMinutes(2);
        _touched.Clear();
        _prewarmer = p;
        p.Footprint = _ => (0, 3); // 배운 게임 몫이 있음 (0 MiB 라 목표 식은 그대로): 몫이 없을 때의 목표는 따로 시험
        return p;
    }

    private Prewarmer? _prewarmer;

    // 찾기 패스 가짜: _findMissing 의 파일 범위는 빠진 것 (범위마다 경계 하나, 디스크 읽기 _findDiskPerMiss), 나머지는 메모리에 있음. 디스크 읽기가 cap 에 닿으면 멈추고 나머지는 확인하지 않음.
    private FindResult FakeFind(IReadOnlyList<ReadOp> ops, long cap, Action<long, double> onGroup)
    {
        _findCalls.Add(ops);
        _findCaps.Add(cap);
        string? boundaryFile = _findBoundaryOf?.Invoke();
        _findGroups?.Invoke(0, 0, onGroup);
        List<ReadOp> resident = [];
        List<ReadOp> missing = [];
        long disk = 0;
        int boundaries = 0;
        long left = 0;
        bool capReached = false;
        bool boundaryHit = false;
        for (int i = 0; i < ops.Count; i++)
        {
            if (capReached)
            {
                left += ops[i].Length;
                continue;
            }

            if (boundaryFile is not null && string.Equals(ops[i].Path, boundaryFile, StringComparison.OrdinalIgnoreCase))
            {
                (disk, boundaries) = (disk + (2 * _findDiskPerMiss), boundaries + 2);
                left += ops.Skip(i).Sum(o => o.Length); // 경계: 그 뒤 (덜 중요한 쪽) 는 확인하지 않고 남김
                boundaryHit = true;
                break;
            }

            if (_findMissing.Contains(ops[i].Path))
            {
                missing.Add(ops[i]);
                (disk, boundaries) = (disk + _findDiskPerMiss, boundaries + 1);
                capReached = disk >= cap;
            }
            else
            {
                resident.Add(ops[i]);
            }
        }

        long bytes = resident.Sum(o => o.Length);
        return new FindResult(resident, missing, bytes, disk, 0, boundaries, boundaries + 1, boundaries * 40, 1, 0.2, 0.1, capReached, false, 0, left, boundaryHit);
    }
    private bool _serialChecked; // 켜면 TouchRanges 가 직렬화 잠금 안에서 불리는지 확인 (시작 만지기의 첫 호출은 잠금 안에서 부르지 않는 시험 시작 단계를 제외)

    // 붙든 동안 (또는 다시 만지는 동안) seconds 초가 지난 뒤 등록 실행 파일이 끝남. each 는 지난 시간 (초) 을 받아 부름. 쉬는 시간은 시계 대신 이 값으로 셈.
    private void RunFor(double seconds, Action<double>? each = null)
    {
        _ = _running.Add("a");
        double elapsed = 0;
        _sleep = (t, ct) =>
        {
            elapsed += t.TotalSeconds;
            WaitWhileTheSerializationLockIsBusy(); // 가짜 쉬기는 즉시 끝나므로 붙든 동안 따로 도는 스레드 (유지 회차) 가 잠금을 쥔 사이에 잠금을 기다리지 않는 확인이 이어서 건너뛰어지지 않게 함
            each?.Invoke(elapsed);
            if (elapsed > seconds)
            {
                _ = _running.Remove("a");
            }
        };
    }

    // 직렬화 잠금이 풀릴 때까지 (최대 약 50 ms) 쉽니다: 잠금을 기다리지 않는 경로 (이어 붙들기·2 분 점검·다시 붙들기) 는 잠금이 쥐여 있으면 다음 초로 미루는데, 시험의 가짜 쉬기는 시간이 흐르지 않아 그 다음 초가 바로 와 버립니다.
    private void WaitWhileTheSerializationLockIsBusy()
    {
        if (_prewarmer is null)
        {
            return;
        }

        object gate = TouchSerial(_prewarmer);
        for (int i = 0; i < 50; i++)
        {
            if (Monitor.TryEnter(gate))
            {
                Monitor.Exit(gate);
                return;
            }

            Thread.Sleep(1);
        }
    }

    private static List<(string Path, long OffsetMib)> Flat(IEnumerable<IReadOnlyList<ReadOp>> batches) =>
        [.. batches.SelectMany(b => b).Select(o => (o.Path, o.Offset / Mib))];

    // ---- 붙드는 때·대상·순서 ----

    [Fact]
    public void RightAfterTheStartTouchTheKeepListIsHeldInImportanceOrderInBatchesAndClosedWhenTheGameEnds()
    {
        Prewarmer p = Make(Reads);
        long heldInside = -1;
        RunFor(2, e => heldInside = e == 1 ? p.KeepHeldMib : heldInside);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(1, _opened);
        Assert.Equal([(F(1), 0), (F(2), 0), (F(2), 8), (F(3), 0), (F(4), 0), (F(5), 0)], Flat(_hold.Batches)); // 중요한 것부터, 16 MiB 배치 셋
        Assert.Equal(3, _hold.Batches.Count);
        Assert.Contains(_log.Lines, l => l.Contains("a: cache held while playing (a.exe): 48 MiB of 5120 MiB target (48 MiB list) in 3 files, 3 views"));
        Assert.Equal(48, heldInside); // 붙든 동안 플레이 기록이 가용 메모리에 더해 기록할 양
        Assert.Equal(0, p.KeepHeldMib);
        Assert.True(_hold.Disposed >= 1);
        Assert.Contains("a: cache hold ended after 0 checks (a.exe): 48 MiB released at the end, peak 48 MiB of 5120 MiB target", _log.Lines.Last());
        Assert.DoesNotContain(_log.Lines, l => l.Contains("cache kept while playing")); // 붙든 동안 대기 목록 다시 만지기는 돌지 않음
        Assert.True(_hold.Rebases >= 1);
    }

    [Fact]
    public void TheTargetIsTheSmallestOfTheBudgetTheRoomAboveTheFloorAndFiveGiB()
    {
        Assert.Equal(5L << 30, Prewarmer.HoldTargetBytes(20000, 4096)); // 상한 5 GiB
        Assert.Equal(1904 * Mib, Prewarmer.HoldTargetBytes(6000, 4096)); // 하한 위에 남는 양 (6000 - 4096), 예산 3000 MiB 보다 작음
        Assert.Equal(2500 * Mib, Prewarmer.HoldTargetBytes(5000, 2048)); // 예산 (가용의 절반)
        Assert.Equal(0, Prewarmer.HoldTargetBytes(4000, 4096)); // 하한 아래
    }

    [Fact]
    public void WhenTheFloorLeavesNoRoomNothingIsHeldAndTheKeepTouchingStays()
    {
        Prewarmer p = Make(Reads);
        _avail = 2100;
        RunFor(20);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(0, _opened);
        Assert.Contains(_log.Lines, l => l.Contains("cache hold while playing skipped (a.exe: available 2100 MiB leaves no room above the floor 2048 MiB)"));
    }

    [Fact]
    public void AMissingStretchIsSkippedWithItsFollowingRangesOfTheSameFileAndTheListContinues()
    {
        _hold.Missing.Add(F(2));
        Prewarmer p = Make(Reads);
        RunFor(1);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal([(F(1), 0), (F(2), 0), (F(3), 0), (F(4), 0), (F(5), 0), (F(2), 8)], Flat(_hold.Batches)); // 파일 2 의 두 번째 범위는 건너뛰고 뒤의 파일 3~5 를 계속 붙든 뒤, 건너뛴 범위는 이어 붙들기가 한 번 다시 봄
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe): 33 MiB of 5120 MiB target") && l.Contains("1 missing stretches skipped (15 MiB not held)")); // 읽지 않은 나머지 7 MiB + 건너뛴 두 번째 범위 8 MiB
    }

    [Fact]
    public void TheDiskReadCapEndsTheFirstPassAndTheRestOfTheListIsLeftToContinueWith()
    {
        _hold.Missing.Add(F(2));
        _hold.Missing.Add(F(4));
        _hold.DiskPerMiss = 20 * Mib; // 두 번째 경계에서 40 MiB 가 읽혀 상한 32 MiB 를 넘음
        Prewarmer p = Make(Reads);
        RunFor(1);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.DoesNotContain(Flat(_hold.Batches).Take(4), b => b.Path == F(5)); // 첫 패스는 파일 5 를 방문하지 않음
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe)") && l.Contains("disk read cap reached") && l.Contains("left to continue with")); // 처음 붙들기는 상한에서 끝나고 남은 범위는 이어 붙들기로 넘김
    }

    [Fact]
    public void HoldingStopsWhenTheAvailableMemoryReachesTheFloorWhileHolding()
    {
        // 가용 = 2148 - 붙든 MiB: 목표 100 MiB (하한 위에 남는 양), 붙든 양이 84 MiB 를 넘으면 (가용 - 16 < 하한) 다음 배치를 시작하지 않음
        ReadOp[] big = [.. Enumerable.Range(0, 20).Select(i => Op(10 + i, 0, 8))];
        Prewarmer p = Make(big);
        _availFn = () => 2148 - (_hold.HeldBytes >> 20);
        RunFor(0);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(6, _hold.Batches.Count);
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe): 96 MiB of 100 MiB target") && l.Contains("stopped at the memory floor (2048 MiB"));
    }

    [Fact]
    public void HoldingRunsUnderTheSerializationLockInGroupsNotAcrossTheWholeWalk()
    {
        ReadOp[] gib = [.. Enumerable.Range(0, 2).Select(i => Op(20 + i, 0, 1024))]; // 2 GiB: 배치 128 개
        Prewarmer p = Make(gib);
        object serial = TouchSerial(p);
        int insideHold = 0;
        int outsideHold = 0;
        _hold.OnHold = () => _ = Monitor.IsEntered(serial) ? insideHold++ : outsideHold++;
        bool enteredDuringSleep = false;
        RunFor(5, _ => enteredDuringSleep |= Monitor.IsEntered(serial));

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(128, insideHold); // 붙들기 호출은 모두 잠금 안
        Assert.Equal(0, outsideHold);
        Assert.False(enteredDuringSleep); // 쉬는 동안은 잠금을 쥐지 않음 (다른 실행 파일의 시작 만지기가 끼어들 수 있음)
        Assert.True(_hold.Rebases >= 128 / Prewarmer.HoldLockBatches); // 잠금을 다시 잡을 때마다 하드 폴트 기준값을 다시 잡음
    }

    // ---- 붙들지 않는 파일 ----

    [Fact]
    public void SparseVolatileAndChangedFilesAreNotHeld()
    {
        _sparse.Add(F(3));
        _excluded.Add(F(4));
        Prewarmer p = Make(Reads);
        RunFor(0);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal([(F(1), 0), (F(2), 0), (F(2), 8), (F(5), 0)], Flat(_hold.Batches));
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe): 32 MiB") && l.Contains("not held: 8 MiB in volatile or changed files, 8 MiB in sparse files"));
    }

    [Fact]
    public void AGameWithoutVolatileFileInformationIsNotHeldAndTheKeepTouchingStays()
    {
        _hasVolatileInfo = false;
        Prewarmer p = Make(Reads);
        RunFor(20);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(0, _opened);
        Assert.Contains(_log.Lines, l => l.Contains("cache hold while playing skipped (a.exe: no volatile file information yet, no trace was recorded)"));
        Assert.Contains(_log.Lines, l => l.Contains("cache kept while playing (a.exe)")); // 지금의 다시 만지기
    }

    [Fact]
    public void ABlockedAntiCheatGameIsNeverHeldAndIsKeptByTheRememberingTouching()
    {
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(20);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Equal(0, _opened);
        Assert.Contains(_log.Lines, l => l.Contains("cache hold while playing skipped (a.exe: anti-cheat: vanguard)"));
        Assert.Contains(_log.Lines, l => l.Contains("a: cache kept while playing (a.exe): 48 MiB of 48 MiB in memory"));
    }

    [Fact]
    public void ARicochetGameIsHeldAndTheStartLineNamesTheAntiCheat()
    {
        GameSettings ricochet = Game("a") with { AntiCheat = "ricochet" }; // 고정은 금지지만 붙들기는 허용
        Prewarmer p = Make(Reads, ricochet);
        RunFor(2);

        p.TouchAtGameStart(ricochet, "a.exe");

        Assert.Equal(1, _opened);
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe)") && l.EndsWith("(anti-cheat: ricochet, hold allowed)"));
    }

    [Theory]
    [InlineData("eac")]
    [InlineData("battleye")]
    [InlineData("vanguard")]
    [InlineData("kernel")]
    [InlineData("unknown")]
    public void AGameWhoseAntiCheatForbidsTracingIsNotHeldAndIsKeptByTheTouching(string kind)
    {
        GameSettings game = Game("a") with { AntiCheat = kind };
        Prewarmer p = Make(Reads, game);
        RunFor(20);

        p.TouchAtGameStart(game, "a.exe");

        Assert.Equal(0, _opened);
        Assert.Contains(_log.Lines, l => l.Contains($"cache hold while playing skipped (a.exe: anti-cheat: {kind})"));
        Assert.Contains(_log.Lines, l => l.Contains("cache kept while playing (a.exe)"));
    }

    [Theory]
    [InlineData("ricochet", "vanguard")]
    [InlineData(null, "eac")]
    [InlineData("ricochet", "battleye")]
    public void ADetectedForbiddenKindBlocksEvenWhenTheSettingNamesAnAllowedOneOrNone(string? configured, string detected)
    {
        _detected = detected;
        GameSettings game = Game("a") with { AntiCheat = configured };
        Prewarmer p = Make(Reads, game);
        RunFor(20);

        p.TouchAtGameStart(game, "a.exe");

        Assert.Equal(0, _opened);
        Assert.Contains(_log.Lines, l => l.Contains($"cache hold while playing skipped (a.exe: anti-cheat: {detected})"));
    }

    [Fact]
    public void ADetectedRicochetWithNoSettingIsHeld()
    {
        _detected = "ricochet";
        Prewarmer p = Make(Reads);
        RunFor(2);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(1, _opened);
        Assert.Contains(_log.Lines, l => l.EndsWith("(anti-cheat: ricochet, hold allowed)"));
    }

    [Fact]
    public void ASharingViolationOnOneFileDoesNotEndTheHoldAndAnotherExeDoesNotHoldAgain()
    {
        // 게임이 공유 없이 연 파일은 ViewHold 가 열기 오류로 세고 건너뜀 (붙들기 전체는 계속): 가짜 붙들기는 오류가 있는 배치를 돌려줌
        GameSettings ricochet = Game("a") with { AntiCheat = "ricochet" };
        Prewarmer p = Make(Reads, ricochet);
        _hold.ErrorsPerBatch = 2;
        RunFor(2, e =>
        {
            if (e == 1)
            {
                _now += Prewarmer.StartTouchMinGap;
                p.TouchAtGameStart(ricochet, "sp26-cod.exe"); // 같은 게임의 다른 실행 파일이 뜸: 다시 붙들지 않음
            }
        });

        p.TouchAtGameStart(ricochet, "cod.exe");

        Assert.Equal(1, _opened);
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (cod.exe)") && l.Contains("6 errors"));
        Assert.Single(_log.Lines, l => l.Contains("cache hold ended"));
    }

    [Fact]
    public void AnAntiCheatComponentFileIsNotHeldAndTheLogCountsWhatWasLeftOut()
    {
        ReadOp[] list = [Op(1, 0, 8), new ReadOp(@"D:\a\Randgrid.sys", 0, 8 * Mib), Op(3, 0, 8)];
        _hold.ImageBytesPerBatch = 5 * Mib;
        _hold.SmallBytesPerBatch = 2 * Mib;
        GameSettings ricochet = Game("a") with { AntiCheat = "ricochet" };
        Prewarmer p = Make(list, ricochet);
        RunFor(2);

        p.TouchAtGameStart(ricochet, "a.exe");

        Assert.DoesNotContain(_hold.Batches.SelectMany(b => b), o => o.Path.EndsWith("Randgrid.sys"));
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe)") && l.Contains("8 MiB in anti-cheat files") && l.Contains("5 MiB in executable images (MZ) or unreadable files") && l.Contains("2 MiB in files under 16 MiB"));
    }

    [Fact]
    public void TheCheckDoesNotRefillWhenTheAvailableMemoryIsWithinTheMarginOfTheFloorButDoesWhenThereIsRoom()
    {
        Prewarmer p = Make(Reads);
        RunFor(250, e => _avail = e < 200 ? 2500 : 20000); // 첫 점검 (120 초): 하한 2048 + 1024 보다 낮음, 둘째 (240 초): 여유 있음

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal([false, true], _hold.Refills);
    }

    [Fact]
    public void TheHoldGateAnswersTheSameForTheSameGameOnEveryCallEvenWhenTheSettingNamesAnAllowedKind()
    {
        var env = new PrewarmEnv
        {
            AvailableRamMib = () => 20000,
            GameRunning = _ => false,
            ZoneBytes = _ => 0,
            TraceExists = _ => true,
            AntiCheatOf = _ => "eac", // 폴더에서는 금지 종류가 감지됨
        };
        var keeper = new PinKeeper(env, _log);
        GameSettings game = Game("a") with { AntiCheat = "ricochet" }; // 설정은 허용 종류

        (string? first, _) = keeper.HoldGate(game);
        (string? second, _) = keeper.HoldGate(game);
        (string? third, string? kind) = keeper.HoldGate(game);

        Assert.Equal("eac", first);
        Assert.Equal("eac", second); // 캐시가 설정값이 아니라 감지값을 담아 두 번째 호출부터 풀리지 않음
        Assert.Equal("eac", third);
        Assert.Equal("ricochet", kind);
    }

    [Fact]
    public void WithTheSwitchOffTheRememberingTouchingRunsAndNothingIsHeld()
    {
        _keepHold = false;
        Prewarmer p = Make(Reads);
        RunFor(20);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(0, _opened);
        Assert.Contains(_log.Lines, l => l.Contains("a: cache kept while playing (a.exe): 48 MiB of 48 MiB in memory") && l.Contains("0 new missing stretches remembered"));
    }

    [Fact]
    public void SparseRangesAheadInTheImportanceOrderDoNotEatTheByteLimitOfTheRememberingTouching()
    {
        _keepHold = false;
        ReadOp[] gib = [.. Enumerable.Range(0, 12).Select(i => Op(30 + i, 0, 1024))];
        _sparse.Add(F(30));
        _sparse.Add(F(31));
        _sparse.Add(F(32)); // 중요한 순서 앞쪽의 스파스 범위 3 GiB: 만지지 않으므로 가용 메모리의 절반 (10,000 MiB) 상한을 먹으면 안 됨
        Prewarmer p = Make(gib);
        RunFor(20);

        p.TouchAtGameStart(Game("a"), "a.exe");

        List<ReadOp> looked = [.. _findCalls.SelectMany(r => r)]; // 회차가 확인하려고 넘긴 범위 (자른 뒤의 목록)
        Assert.NotEmpty(looked);
        Assert.DoesNotContain(looked, o => _sparse.Contains(o.Path));
        Assert.True(looked.Max(o => o.Offset + o.Length) > 0 && looked.Select(o => o.Path).Distinct().Count() >= 9, $"{looked.Select(o => o.Path).Distinct().Count()} ordinary files looked at");
    }

    [Fact]
    public void TheReuseThresholdListSizeKeepsTheSparseRangesSoTheRememberingTouchingRunsAsOftenAsBeforeAndTheTouchedTargetsLeaveThemOut()
    {
        _keepHold = false;
        _sparse.Add(F(3));
        Prewarmer p = Make(Reads); // 8 MiB 범위 여섯 개 (48 MiB) 가운데 파일 3 (8 MiB) 이 스파스
        Type listType = typeof(Prewarmer).GetNestedType("KeepList", BindingFlags.NonPublic)!;
        object list = Activator.CreateInstance(listType)!;

        bool ok = (bool)typeof(Prewarmer).GetMethod("RefreshKeepList", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(p, [Game("a"), list])!;

        Assert.True(ok);
        Assert.Equal(48 * Mib, (long)listType.GetField("Bytes")!.GetValue(list)!); // 재사용 문턱을 정하는 목록 크기는 스파스 범위도 포함 (이 변경 전과 같음)
        var touchable = (IReadOnlyList<ReadOp>)listType.GetField("Touchable")!.GetValue(list)!;
        Assert.Equal(40 * Mib, touchable.Sum(o => o.Length)); // 만질 대상은 스파스를 뺀 목록
        Assert.DoesNotContain(touchable, o => o.Path == F(3));
    }

    // ---- 하한·내놓기·점검 ----

    [Fact]
    public void BelowTheFloorTheLeastImportantAreReleasedByTheMissingAmountAndAreNotHeldAgainBeforeTheTwoMinuteCheck()
    {
        ReadOp[] gib = [.. Enumerable.Range(0, 4).Select(i => Op(20 + i, 0, 1024))];
        Prewarmer p = Make(gib);
        RunFor(5, e => _avail = e == 1 ? 1500 : 20000); // 첫 확인에서만 하한 (2048) 아래

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal([(2048 - 1500 + 256) * Mib], _hold.Released); // 부족한 양 + 여유
        Assert.Contains(_log.Lines, l => l.Contains("cache hold released 804 MiB for memory (a.exe): available 1500 MiB < floor 2048 MiB, 3292 MiB still held"));
        Assert.Equal(256, _hold.Batches.Count); // 내놓은 뒤 2 분 점검 전에는 다시 붙들지 않음: 붙들기 호출은 처음 한 번뿐
    }

    [Fact]
    public void TheFloorIsWatchedEverySecondEarlyOrNearTheFloorAndEveryFiveSecondsOtherwise()
    {
        Prewarmer p = Make(Reads);
        List<double> sleeps = [];
        RunFor(400, _ => { });
        Action<TimeSpan, CancellationToken> inner = _sleep!;
        _sleep = (t, ct) =>
        {
            sleeps.Add(t.TotalSeconds);
            inner(t, ct);
        };

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(1, sleeps[0]); // 붙든 직후 3 분은 1 초
        Assert.Equal(1, sleeps[179]);
        Assert.Equal(5, sleeps[180]); // 가용이 하한에서 멀면 그 뒤는 5 초
        Assert.Equal(5, sleeps[^1]);
    }

    [Fact]
    public void WithinTwoGiBOfTheFloorTheFloorIsWatchedEverySecondEvenAfterTheEarlyWindow()
    {
        Prewarmer p = Make(Reads);
        List<double> late = [];
        double elapsed = 0;
        _ = _running.Add("a");
        _sleep = (t, ct) =>
        {
            elapsed += t.TotalSeconds;
            if (elapsed > 190)
            {
                _avail = 3500; // 하한 (2048) + 2048 아래
                late.Add(t.TotalSeconds);
            }

            if (elapsed > 200)
            {
                _ = _running.Remove("a");
            }
        };

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Contains(1.0, late.Skip(1)); // 하한에 가까우면 3 분이 지나도 1 초
    }

    [Fact]
    public void EverythingReleasedForMemoryEndsTheHold()
    {
        Prewarmer p = Make(Reads);
        RunFor(5, e => _avail = e == 1 ? 100 : 20000);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Contains(_log.Lines, l => l.Contains("cache hold ended") && l.Contains("(everything released for memory)"));
        Assert.Equal(0, p.KeepHeldMib);
    }

    [Fact]
    public void EveryTwoMinutesTheHeldRangesAreCheckedAndTheOutcomeIsLogged()
    {
        _hold.NextCheck = () => new HoldCheckResult(40 * Mib, 6 * Mib, 4 * Mib, 8 * Mib, true, [F(3)], 2, 120, 1.5, 0.4);
        Prewarmer p = Make(Reads);
        RunFor(250);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(2, _hold.Rechecks); // 120 초와 240 초
        Assert.Contains(_log.Lines, l => l.Contains("cache hold checked (a.exe): 40 MiB looked up in the working set in 1.5 s (cpu 0.4 s, 2 checks), 6 MiB reduced by the memory manager, 4 MiB of it read from disk again (120 hard faults), refill cap reached, 8 MiB taken off the hold, 1 changed file(s) released"));
        Assert.Contains("cache hold ended after 2 checks", _log.Lines.Last(l => l.Contains("cache hold ended")));
    }

    [Fact]
    public void WhileLoadingTheCheckOfTheHeldRangesStillRunsWithASmallRefillCapThatNeverTakesRangesOffTheHoldAndItsReadsAreCounted()
    {
        _hold.NextCheck = () => new HoldCheckResult(40 * Mib, 6 * Mib, 4 * Mib, 0, false, [], 2, 120, 1.5, 0.4);
        Prewarmer p = Make(Reads);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        RunFor(370, e => _ = board.Add("a", new LoadingSample(e, 100 * Mib, null, false))); // 경기 안 로딩 (실행 파일 시작 없음): 읽기가 계속 많아 상한 (240 초) 으로만 끝남: 초 245

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(3, _hold.Rechecks); // 120 초와 240 초는 로딩 중, 360 초는 로딩이 끝난 뒤
        Assert.Equal([Prewarmer.KeepLoadingCheckDiskCapBytes, Prewarmer.KeepLoadingCheckDiskCapBytes, Prewarmer.KeepDiskReadCapBytes], _hold.RecheckCaps);
        Assert.Equal([false, false, true], _hold.DropOnCaps); // 로딩 중에는 상한에 닿아도 구간을 붙들기에서 빼지 않음
        Assert.Equal(12 * Mib, board.ServiceReads.Total(Game("a").OriginRoot)); // 점검마다 다시 읽은 4 MiB 를 서비스 자신의 읽기로 셈
        Assert.Contains(_log.Lines, l => l.Contains("a: loading phase ended after 244 s (240 s limit); held back: hold check disk reads"));
    }

    [Fact]
    public void WhatTheMemoryManagerReducedIsNotAddedToTheRecordedAvailableMemory()
    {
        // 기록하는 값: 실제 가용 + 붙든 양. 점검 사이에 메모리 관리자가 줄여 간 쪽은 이미 가용 (대기 목록) 에 들어 있으므로 서비스 작업 집합의 증가분으로 상한을 둠
        Prewarmer p = Make(Reads);
        long held = -1;
        long afterTrim = -1;
        RunFor(3, e =>
        {
            if (e == 1)
            {
                held = p.KeepHeldMib;
                _trimmed = 28 * Mib;
                afterTrim = p.KeepHeldMib;
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(48, held);
        Assert.Equal(20, afterTrim); // 붙든 48 MiB 가운데 28 MiB 가 작업 집합에서 빠졌으면 20 MiB 만 더함
    }

    [Fact]
    public void AnotherExeOfTheSameGameDuringTheHoldDoesNotHoldAgain()
    {
        Prewarmer p = Make(Reads);
        RunFor(2, e =>
        {
            if (e == 1)
            {
                _now += Prewarmer.StartTouchMinGap;
                p.TouchAtGameStart(Game("a"), "b.exe"); // 런처 뒤에 본체가 이어 뜸
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(1, _opened);
        Assert.Equal(3, _hold.Batches.Count);
        Assert.Single(_log.Lines, l => l.Contains("cache hold ended"));
    }

    // ---- 끝낼 때·예외·서비스 정지 ----

    [Fact]
    public void AnExceptionWhileHoldingEndsOnlyTheHoldAndClosesWhatWasHeld()
    {
        _hold.Throw = new InvalidOperationException("boom");
        Prewarmer p = Make(Reads);
        RunFor(5);

        p.TouchAtGameStart(Game("a"), "a.exe"); // 예외가 서비스로 번지지 않음

        Assert.Contains(_log.Lines, l => l.Contains("a: cache hold while playing stopped: InvalidOperationException"));
        Assert.True(_hold.Disposed >= 1);
        Assert.Equal(0, p.KeepHeldMib);
    }

    [Fact]
    public void TheServiceStoppingClosesTheHold()
    {
        Prewarmer p = Make(Reads);
        _ = _running.Add("a");
        _sleep = (t, ct) => p.Shutdown();

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.True(_hold.Disposed >= 1);
        Assert.Equal(0, p.KeepHeldMib);
        Assert.Contains(_log.Lines, l => l.Contains("cache hold ended"));
    }

    [Fact]
    public void TurningTheSwitchOffWhileHoldingClosesTheHoldAndTheRememberingTouchingTakesOver()
    {
        Prewarmer p = Make(Reads);
        RunFor(60, e =>
        {
            if (e >= 2)
            {
                _keepHold = false;
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Contains(_log.Lines, l => l.Contains("cache hold ended") && l.Contains("(turned off in the settings)"));
        Assert.Contains(_log.Lines, l => l.Contains("a: cache kept while playing (a.exe)")); // 남은 시간은 지금의 다시 만지기
    }

    [Fact]
    public void TurningTheLadderSettingOffWhileHoldingClosesTheHold()
    {
        Prewarmer p = Make(Reads);
        RunFor(60, e =>
        {
            if (e >= 2)
            {
                _ladder = false;
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Contains(_log.Lines, l => l.Contains("cache hold ended") && l.Contains("(turned off in the settings)"));
        Assert.True(_hold.Disposed >= 1);
    }

    // ---- 붙들지 않는 게임의 다시 만지기: 재사용에 맞춘 시점, 두 패스, 빠진 범위를 기억하고 건너뜀 ----

    private int KeptLines() => _log.Lines.Count(l => l.Contains("cache kept while playing ("));

    [Fact]
    public void ARoundRunsAsSoonAsTheReuseCrossesTheThresholdWithoutWaitingTwoMinutes()
    {
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        double firstSeen = -1;
        RunFor(60, e =>
        {
            if (firstSeen < 0 && KeptLines() > 0)
            {
                firstSeen = e;
            }
        });

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.InRange(firstSeen, 1, 15); // 두 번째 확인 (최소 간격 10 초) 에서 돔: 2 분 주기가 아님
    }

    [Fact]
    public void WhileLoadingTheKeepRoundStillRetouchesWhatIsInMemoryButLooksForNothingNewAndReadsLittleAndKeepsTheSetWhenTheCapIsHit()
    {
        _findBoundaryOf = () => F(3); // 첫 회차: 파일 3 에서 경계, 그 앞 (파일 1, 2) 만 집합에 들어감
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        int touchedAtLoadingStart = 0;
        int touchedAtLoadingEnd = 0;
        RunFor(40, e =>
        {
            if (e >= 15)
            {
                _ = board.Add("a", new LoadingSample(e, e == 15 ? 100 * Mib : 0, null, false)); // 초 15 에 읽기 신호로 로딩이 시작됨 (실행 파일 시작 없음): 조용한 상태가 초 20 부터 15 초 이어져 초 35 에 끝남
            }

            if (e == 15)
            {
                _findBoundaryOf = () => F(2); // 로딩 중 회차의 보호 패스가 파일 2 에서 경계를 만나 멈춤: 집합에서 빼지 않아야 함
                touchedAtLoadingStart = _touched.Count;
            }

            if (e == 35)
            {
                _findBoundaryOf = null;
                touchedAtLoadingEnd = _touched.Count;
            }
        });

        p.TouchAtGameStart(cheat, "a.exe");

        // 호출: 초 10 (첫 회차, 집합 밖 찾기), 초 20·30 (로딩: 보호 패스만, 2 MiB 상한), 초 40 (보호 패스 + 집합 밖 찾기)
        Assert.Equal([Prewarmer.KeepEmptySetRoundDiskCapBytes, Prewarmer.KeepLoadingProtectDiskCapBytes, Prewarmer.KeepLoadingProtectDiskCapBytes, Prewarmer.KeepProtectDiskCapBytes, Prewarmer.KeepRoundDiskCapBytes], _findCaps);
        Assert.True(touchedAtLoadingEnd > touchedAtLoadingStart, "the in-memory range is retouched while loading"); // 순서 패스는 로딩 중에도 돎
        Assert.Equal(24 * Mib, _findCalls[3].Sum(o => o.Length)); // 로딩 중 파일 2 에서 경계를 만나 멈췄어도 집합은 그대로 (파일 1, 2 의 24 MiB, 잘렸다면 8 MiB)
        Assert.Equal(3 * Mib, board.ServiceReads.Total(cheat.OriginRoot)); // 경계마다 알아챈 읽기 1 MiB 씩 (초 10·20·30) 이 서비스 자신의 읽기로 셈
        Assert.Contains(_log.Lines, l => l.Contains("a: loading phase ended after 20 s (quiet for 15 s); held back: keep round disk reads"));
        Assert.Contains(_log.Lines, l => l.Contains("disk reads limited (loading phase)"));
    }

    [Fact]
    public void ASignalLoadingFromTheStartStillLetsTheFirstRoundFindTheSetWithTheSmallCapAndTheOrderPassOnlyStopsAtABatchThatReadsFromDisk()
    {
        ReadOp[] gib = [.. Enumerable.Range(0, 3).Select(i => Op(30 + i, 0, 1024))]; // 16 MiB 배치로 192 개
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(gib, cheat);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        int at15 = 0;
        int at25 = 0;
        int at35 = 0;
        RunFor(40, e =>
        {
            _ = board.Add("a", new LoadingSample(e, 100 * Mib, null, false)); // 초 5 부터 읽기가 계속 많은 경기 안 로딩 (집합이 아직 비어 있음)
            if (e == 15)
            {
                at15 = _touched.Count;
                _touchOverride = ranges => new TouchResult(ranges.Sum(o => o.Length), 0.1, 0, 0, TouchEnd.Completed) { DiskBytes = 64L << 10 }; // 배치마다 조회 잡음 (64 KiB) 만큼 읽힌 것으로 보임: 경계가 아님
            }

            if (e == 25)
            {
                at25 = _touched.Count;
                _touchOverride = ranges => new TouchResult(Mib, 0.1, 0, 0, TouchEnd.NeedsDiskReads) { DiskBytes = Mib }; // 밀려난 범위를 만남: 경계
            }

            if (e == 35)
            {
                at35 = _touched.Count;
            }
        });

        p.TouchAtGameStart(cheat, "a.exe");

        // 초 10: 집합이 비어 있어도 첫 회차가 2 MiB 상한의 찾기로 집합을 만듦 (시작 로딩이 끝나기를 기다리지 않음), 초 20·30·40: 보호 패스만
        Assert.Equal([.. Enumerable.Repeat(Prewarmer.KeepLoadingProtectDiskCapBytes, 4)], _findCaps);
        Assert.Equal(192, at25 - at15); // 잡음은 경계가 아니라 순서 패스가 확인한 3 GiB 전체를 만짐
        Assert.Equal(1, at35 - at25); // 경계 배치에서 끝남
    }

    [Fact]
    public void ARoundDuringALoadingThatStartedWithAnExecutableStartRunsExactlyAsOutsideLoading()
    {
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        RunFor(40, e => _ = board.Add("a", new LoadingSample(e, e == 5 ? 100 * Mib : 0, null, e == 5))); // 초 5 에 실행 파일이 뜸 (읽기 신호도 참): 조용한 상태가 초 10 부터 이어져 초 25 에 끝남

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Equal(Prewarmer.KeepEmptySetRoundDiskCapBytes, _findCaps[0]); // 초 10 의 첫 회차: 로딩이 아닐 때와 같은 찾기 상한
        Assert.Equal(Prewarmer.KeepProtectDiskCapBytes, _findCaps[1]);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("loading phase)", StringComparison.Ordinal) || l.Contains("held back", StringComparison.Ordinal));
        Assert.Contains(_log.Lines, l => l.Contains("a: loading phase ended after 20 s (quiet for 15 s)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFirstRoundsThresholdUsesTheRealSizeOfTheKeepListNotZero()
    {
        // 우선순위 5 대기 4 GiB, 목록 3 GiB: 문턱 512 MiB. 목록 크기를 0 으로 계산하면 1 GiB 라 첫 회차가 10 초 뒤가 아니라 20 초 뒤에 돔
        _p5 = 4096;
        _reuseStep = 300;
        ReadOp[] gib = [.. Enumerable.Range(0, 3).Select(i => Op(30 + i, 0, 1024))];
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(gib, cheat);
        double firstSeen = -1;
        RunFor(40, e =>
        {
            if (firstSeen < 0 && KeptLines() > 0)
            {
                firstSeen = e;
            }
        });

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.InRange(firstSeen, 1, 15);
    }

    [Fact]
    public void WithoutStandbyCacheReuseNoRoundRuns()
    {
        _reuseStep = 0;
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(600);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Equal(0, KeptLines());
        Assert.Single(_touched); // 시작 만지기뿐
    }

    [Fact]
    public void RoundsAreAtLeastTenSecondsApartEvenWhenTheReuseIsHuge()
    {
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(60);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Equal(6, KeptLines()); // 10, 20, ..., 60 초
    }

    [Fact]
    public void WhenTheStandbyListsCannotBeReadARoundRunsEveryTwoMinutes()
    {
        _reuseStep = -1;
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(250);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Equal(2, KeptLines()); // 120 초와 240 초
        Assert.Contains(_log.Lines, l => l.Contains("called by the fixed period"));
    }

    [Fact]
    public void TheThresholdIsHalfOfWhatIsLeftAheadOfTheListInPriorityFiveUpToOneGiBWithAFloor()
    {
        Assert.Equal(1L << 30, Prewarmer.ReuseThreshold(new MemoryLists(0, 0, 20000 * Mib), 100 * Mib)); // 앞의 여유가 크면 1 GiB
        Assert.Equal(1000 * Mib, Prewarmer.ReuseThreshold(new MemoryLists(0, 0, 3000 * Mib), 1000 * Mib)); // (3000 - 1000) / 2
        Assert.Equal(64 * Mib, Prewarmer.ReuseThreshold(new MemoryLists(0, 0, 500 * Mib), 1000 * Mib)); // 여유가 없으면 하한 64 MiB
        Assert.Equal(500 * Mib, Prewarmer.ReuseThreshold(new MemoryLists(0, 0, 20000 * Mib, 2000 * Mib), 1000 * Mib)); // 우선순위 6·7 의 대기는 앞의 여유로 세지 않음: 5 만 (2000 - 1000) / 2
    }

    [Fact]
    public void EvenWhenTheLeastImportantHalfIsMissingTheMostImportantPartIsFoundAndTouchedLastInTwoPasses()
    {
        // 목록 [1, 2, 2', 3, 4, 5] 의 덜 중요한 쪽 (4, 5) 이 빠져 있음: 찾기는 중요한 순서로 1~3 을 모으고 순서 패스가 그것을 덜 중요한 것부터 만져 가장 중요한 1 이 마지막
        _findMissing.Add(F(4));
        _findMissing.Add(F(5));
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(12);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Equal(Reads.Select(o => o.Path), _findCalls[0].Select(o => o.Path)); // 찾기 패스는 중요한 순서로 걸음
        List<ReadOp> order = [.. _touched.Skip(1).SelectMany(b => b)]; // 시작 만지기 (사다리) 뒤의 순서 패스
        Assert.Equal(new ReadOp(F(1), 0, 8 * Mib), order[^1]); // 가장 중요한 것이 마지막
        Assert.Equal(new ReadOp(F(3), 0, 8 * Mib), order[0]); // 모은 것 가운데 가장 덜 중요한 것부터
        Assert.DoesNotContain(order, o => o.Path == F(4) || o.Path == F(5)); // 빠진 것은 만지지 않음
    }

    [Fact]
    public void AMissingRangeIsRememberedSoLaterRoundsDoNotLookAtItOrTouchItAndCostNoDiskRead()
    {
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        _findMissing.Add(F(2));
        RunFor(45);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.True(KeptLines() >= 3);
        Assert.Equal(1, _findCalls.Count(c => c.Any(o => o.Path == F(2)))); // 처음 발견하는 회차뿐: 그 뒤 회차는 찾지도 만지지도 않음
        Assert.Equal(1, _touched.Count(c => c.Any(o => o.Path == F(2)))); // 시작 만지기의 사다리 한 번 외에는 순서 패스도 만지지 않음
        Assert.Contains(_log.Lines, l => l.Contains("a: cache kept while playing (a.exe)") && l.Contains("0 new missing stretches remembered (16 MiB of the 48 MiB list in all)") && l.Contains("0 MiB read from disk"));
    }

    [Fact]
    public void TheRoundCapStopsTheFindButTheOrderPassStillRunsOnWhatWasCollected()
    {
        _findMissing.Add(F(2));
        _findMissing.Add(F(3));
        _findMissing.Add(F(4));
        _findDiskPerMiss = 20 * Mib; // 경계마다 20 MiB: 두 번째에서 집합이 빈 회차의 상한 32 MiB 를 넘음
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(12);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Contains(_log.Lines, l => l.Contains("cache kept while playing (a.exe)") && l.Contains("disk read cap of the round reached") && l.Contains("MiB left unconfirmed"));
        List<ReadOp> order = [.. _touched.Skip(1).SelectMany(b => b)];
        Assert.Contains(order, o => o.Path == F(1)); // 모은 범위는 순서 패스가 만짐
        Assert.DoesNotContain(order, o => o.Path == F(5)); // 확인하지 못한 범위는 이번에 만지지 않음
    }

    [Fact]
    public void AfterThePlayDiskCapNoNewRangeIsLookedForButTheConfirmedOnesAreStillTouchedAndTheKeepingDoesNotEnd()
    {
        _findMissing.Add(F(3));
        _findMissing.Add(F(4));
        _findMissing.Add(F(5));
        _findDiskPerMiss = 33 * Mib; // 첫 회차 (상한 32 MiB) 와 둘째 회차 (상한 8 MiB) 가 경계 하나에서 각각 상한: 두 회차 만에 플레이 상한 64 MiB 를 넘음 (33, 66)
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(100);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Equal(2, _findCalls.Count(c => c.Any(o => o.Path == F(3) || o.Path == F(4) || o.Path == F(5)))); // 집합 밖 다시 확인은 둘째 회차에서 66 MiB: 그 뒤는 하지 않음 (보호 패스 호출은 따로)
        Assert.Contains(_log.Lines, l => l.Contains("no new range looked for"));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("cache keeping ended") && l.Contains("disk reads of this play"));
        string last = _log.Lines.Last(l => l.Contains("cache kept while playing ("));
        Assert.Contains("no new range looked for", last); // 끝까지 회차가 계속 돔
        IReadOnlyList<ReadOp> lastRound = _touched[^1];
        Assert.DoesNotContain(lastRound, o => o.Path == F(3) || o.Path == F(4) || o.Path == F(5)); // 확인된 범위 (1, 2) 만 만짐
    }

    [Fact]
    public void EvenWhenEverythingIsRememberedAsMissingTheKeepingDoesNotEndWhileTheGameRuns()
    {
        foreach (int f in new[] { 1, 2, 3, 4, 5 })
        {
            _findMissing.Add(F(f));
        }

        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(60); // 게임은 계속 실행 중

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.True(KeptLines() >= 3); // 빠진 범위가 목록의 대부분이어도 유지는 게임이 끝날 때까지 이어짐
        Assert.DoesNotContain(_log.Lines, l => l.Contains("most of the list is remembered as missing"));
    }

    [Fact]
    public void AnUnconfirmedStretchAheadOfTheMostImportantFileDoesNotEndTheKeepingEither()
    {
        _findBoundaryOf = () => F(1); // 가장 중요한 파일 앞에서 이어진 빠짐: 목록 전체가 경계 뒤
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(45);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.True(KeptLines() >= 3);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("cache keeping ended after") && l.Contains("most of the list"));
    }

    [Fact]
    public void TheMemoryAndTheDiskReadTotalOfThePlayAreKeptWhenAnotherExeOfTheSameGameStartsWhileItRuns()
    {
        _findMissing.Add(F(2));
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(25, e =>
        {
            if (e == 15)
            {
                _now += Prewarmer.StartTouchMinGap;
                p.TouchAtGameStart(cheat, "b.exe"); // 유지가 도는 동안 다른 실행 파일이 뜸: 새 유지는 시작하지 않음
            }
        });

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Equal(1, _findCalls.Count(c => c.Any(o => o.Path == F(2)))); // 빠진 범위의 기억이 유지됨
        Assert.Single(_log.Lines, l => l.Contains("cache keeping ended after"));
    }

    [Fact]
    public void TheRoundReleasesTheSerializationLockBetweenGroupsLetsAnotherTouchInAndNeverRestsForASpeedLimit()
    {
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        object serial = TouchSerial(p);
        bool before = false;
        bool after = false;
        bool waiterGotItBetween = false;
        bool started = false;
        _findGroups = (_, _, onGroup) =>
        {
            if (started)
            {
                return;
            }

            started = true;
            before = Monitor.IsEntered(serial); // 찾기는 잠금 안에서 시작
            using var got = new ManualResetEventSlim();
            var waiter = new Thread(() =>
            {
                lock (serial) // 다른 실행 파일의 시작 만지기를 흉내: 잠금을 기다림
                {
                    got.Set();
                }
            });
            waiter.Start();
            Thread.Sleep(100); // 대기자가 잠금에서 기다리는 중
            onGroup(128 * Mib, 0.1); // 128 MiB 를 읽음: 잠금을 놓고 양보한 뒤 다시 잡음
            waiterGotItBetween = got.IsSet; // 놓은 사이 대기자가 잡았음
            after = Monitor.IsEntered(serial);
            waiter.Join();
        };
        RunFor(12);
        int shortSleeps = 0;
        Action<TimeSpan, CancellationToken> inner = _sleep!;
        _sleep = (t, ct) =>
        {
            if (t < TimeSpan.FromSeconds(1)) // 속도 제한으로 쉬는 것은 없음
            {
                shortSleeps++;
            }

            inner(t, ct);
        };

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.True(before);
        Assert.True(waiterGotItBetween);
        Assert.True(after);
        Assert.Equal(0, shortSleeps);
    }

    [Fact]
    public void ThePlayCapCountsOnlyTheReadsOfTheLookOutsideTheSetNotThoseOfTheProtectAndOrderPasses()
    {
        _findBoundaryOf = () => F(4); // 집합 밖 (4, 5) 이 매 회차 남음
        _touchOverride = ranges => KeptLines() >= 1 ? new TouchResult(ranges.Sum(o => o.Length), 0.1, 0, 0, TouchEnd.Completed, 30 * Mib) : null; // 회차의 순서 패스 배치마다 디스크 읽기 30 MiB 로 보고 (경계 없이)
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(45);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.True(KeptLines() >= 4);
        Assert.True(_findCalls.Count(c => c.Any(o => o.Path == F(4))) >= 4); // 순서 패스가 수백 MiB 를 읽은 것으로 보고해도 집합 밖 다시 확인은 매 회차 계속됨
        Assert.DoesNotContain(_log.Lines, l => l.Contains("no new range looked for"));
    }

    [Fact]
    public void WhenTheTailWasPushedOutTheProtectPassTouchesTheMostImportantFirstCutsTheTailAndTheOrderPassTouchesTheRestLeastImportantFirstToTheEnd()
    {
        _findBoundaryOf = () => KeptLines() >= 1 ? F(4) : null; // 첫 회차 뒤 꼬리 (4, 5) 가 밀려남
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(25);

        p.TouchAtGameStart(cheat, "a.exe");

        IReadOnlyList<ReadOp> protect = _findCalls[1]; // 둘째 회차의 첫 찾기 = 보호 패스: 집합 전체를 가장 중요한 것부터
        Assert.Equal(new ReadOp(F(1), 0, 8 * Mib), protect[0]);
        Assert.Equal(KeepHoldTests.Reads.Select(o => o.Path), protect.Select(o => o.Path));
        Assert.Equal(Prewarmer.KeepProtectDiskCapBytes, _findCaps[1]);
        IReadOnlyList<ReadOp> lastBatch = _touched[^1];
        IReadOnlyList<ReadOp> firstOfRound = _touched[^2];
        Assert.Equal(new ReadOp(F(1), 0, 8 * Mib), lastBatch[^1]); // 순서 패스의 끝: 가장 중요한 것이 두 번째로 (회차 처음의 보호 패스에 이어) 만져짐
        Assert.Equal(new ReadOp(F(3), 0, 8 * Mib), firstOfRound[0]); // 경계에서 꼬리를 뺀 뒤 남은 것 가운데 가장 덜 중요한 것부터
        Assert.DoesNotContain(_touched.TakeLast(2).SelectMany(b => b), o => o.Path == F(4) || o.Path == F(5)); // 경계 뒤는 이번 회차에 만지지 않음
    }

    [Fact]
    public void AnOrderPassThatReadsFromDiskInSeveralBatchesBelowTheCapStillRunsToTheMostImportantEnd()
    {
        _touchOverride = ranges => KeptLines() >= 1 && ranges.Any(o => o.Path == F(5) || o.Path == F(3))
            ? new TouchResult(8 * Mib, 0.1, 0, 0, TouchEnd.NeedsDiskReads, 10 * Mib, 0, 0, 0, 0, StopReason.HardFaults) // 배치마다 10 MiB: 세 번이라 합 30 MiB 로 순서 패스 상한 (32 MiB) 아래
            : null;
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(25);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Equal(new ReadOp(F(1), 0, 8 * Mib), _touched[^1][^1]); // 읽기가 여러 번 나도 상한 아래면 가장 중요한 쪽까지 만짐
    }

    [Fact]
    public void AnOrderPassThatReadsMoreThanTheCapEndsThereAndTheProtectPassHadAlreadyTouchedTheWholeSet()
    {
        _touchOverride = ranges => KeptLines() >= 1
            ? new TouchResult(8 * Mib, 0.1, 0, 0, TouchEnd.NeedsDiskReads, 20 * Mib, 0, 0, 0, 0, StopReason.HardFaults) // 배치마다 20 MiB: 둘째 배치에서 상한 32 MiB 를 넘음
            : null;
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(25);

        p.TouchAtGameStart(cheat, "a.exe");

        IReadOnlyList<ReadOp> protect = _findCalls[1]; // 둘째 회차의 보호 패스: 집합 전체를 가장 중요한 것부터 이미 만짐
        Assert.Equal(Reads.Select(o => o.Path), protect.Select(o => o.Path));
        Assert.Equal(new ReadOp(F(1), 0, 8 * Mib), protect[0]);
        Assert.DoesNotContain(_touched[^1], o => o.Path == F(1)); // 순서 패스는 상한에 닿아 가장 중요한 쪽까지 가지 않고 끝남
        Assert.Contains(_log.Lines, l => l.Contains("order pass stopped at the disk read cap"));
    }

    [Fact]
    public void ARoundWithAnEmptySetOnlyLooksOutsideItWithTheLargerCapAndLaterRoundsUseTheProtectAndTheNormalCaps()
    {
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(25);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Equal(Prewarmer.KeepEmptySetRoundDiskCapBytes, _findCaps[0]); // 집합이 빈 첫 회차는 집합 밖 확인만, 회차 상한을 크게
        Assert.Equal(Prewarmer.KeepProtectDiskCapBytes, _findCaps[1]); // 집합이 생긴 뒤에는 보호 패스가 먼저
        Assert.Equal(Reads.Select(o => o.Path), _findCalls[0].Select(o => o.Path));
    }

    [Fact]
    public void LaterRoundsTouchOnlyTheConfirmedSetAndDoNotRunTheFindWhenNothingIsOutsideIt()
    {
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(35);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.True(KeptLines() >= 3);
        Assert.Equal(KeptLines(), _findCalls.Count); // 회차마다 찾기는 하나: 첫 회차는 집합 밖 (집합 만들기), 그 뒤는 보호 패스 (집합 밖이 없어 다시 확인은 안 함)
        Assert.All(_findCalls.Skip(1), c => Assert.Equal(Reads.Select(o => o.Path), c.Select(o => o.Path))); // 보호 패스는 집합 전체를 가장 중요한 것부터
        IReadOnlyList<ReadOp> last = _touched[^1];
        Assert.Equal(new ReadOp(F(1), 0, 8 * Mib), last[^1]); // 순서 패스: 가장 중요한 것이 마지막
    }

    [Fact]
    public void ABoundaryCutsOffTheLessImportantRestFromTheSetButItIsLookedAtAgainOutsideTheSetEveryRound()
    {
        _findBoundaryOf = () => F(3); // 파일 3 에서 경계: 1, 2 만 집합에 들고 3~5 는 집합 밖
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(25);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Contains(_log.Lines, l => l.Contains("cache kept while playing (a.exe)") && l.Contains("set 24 MiB") && l.Contains("24 MiB cut off by a boundary") && l.Contains("0 MiB left out as holes"));
        Assert.True(_findCalls.Count >= 2);
        Assert.Equal([F(3), F(4), F(5)], _findCalls.Skip(1).First(c => c.Any(o => o.Path == F(3))).Select(o => o.Path)); // 다음 회차의 집합 밖 다시 확인은 집합 밖만 중요한 순서로 (보호 패스는 집합만)
        Assert.DoesNotContain(_touched[^1], o => o.Path == F(3) || o.Path == F(4) || o.Path == F(5)); // 순서 패스는 집합만 만짐
    }

    [Fact]
    public void AnOrderPassBatchThatReadFromDiskRemovesOnlyThatRangeFromTheSetAndTheSameFilesLaterRangesAreStillTouched()
    {
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        bool fired = false;
        _touchOverride = ranges =>
        {
            if (!fired && KeptLines() >= 1 && ranges.Any(o => o.Path == F(3))) // 둘째 회차의 순서 패스에서 파일 3 의 배치가 디스크 읽기를 만남
            {
                fired = true;
                return new TouchResult(8 * Mib, 0.1, 0, 0, TouchEnd.NeedsDiskReads, 4 * Mib, 0, 0, 0, 0, StopReason.HardFaults);
            }

            return null;
        };
        RunFor(35);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.True(fired);
        Assert.Contains(_findCalls, c => c.Count == 1 && c[0].Path == F(3)); // 집합에서 빠진 그 범위만 다음 회차에 집합 밖으로 다시 확인 (구멍으로 기억하지 않음)
        Assert.Contains(_touched.Skip(1), b => b.Any(o => o.Path == F(2) && o.Offset == 8 * Mib)); // 같은 파일 뒤 범위도 계속 만짐 (같은 파일을 통째로 건너뛰지 않음)
        Assert.Contains(_log.Lines, l => l.Contains("cache kept while playing (a.exe)") && l.Contains("0 new missing stretches remembered (0 MiB of the 48 MiB list in all)"));
    }

    [Fact]
    public void ARoundTouchesTheLeastImportantFirstSoTheMostImportantIsLastAndRunsUnderTheSerializationLock()
    {
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        _serialChecked = true;
        RunFor(12);

        p.TouchAtGameStart(cheat, "a.exe");

        List<IReadOnlyList<ReadOp>> round = [.. _touched.Skip(1)]; // 시작 만지기 (사다리 한 번) 다음의 회차
        Assert.Equal(new ReadOp(F(1), 0, 8 * Mib), round[^1][^1]); // 가장 중요한 (목록 맨 앞) 블록이 마지막
        Assert.Equal(new ReadOp(F(5), 0, 8 * Mib), round[0][0]); // 가장 덜 중요한 것부터
    }

    // ---- 설정·동작 표시 ----

    [Fact]
    public void TheSwitchDefaultsToOnIsReadAndRejectsBadValues()
    {
        Assert.True(ParseMemory("{}").KeepHold);
        Assert.False(ParseMemory("""{"keep_hold":false}""").KeepHold);
        Assert.True(ParseMemory("""{"keep_hold":false}""").StandbyLadder);
        Assert.Throws<SettingsException>(() => ParseMemory("""{"keep_hold":"yes"}"""));
        Assert.True(ParseMemory("{}").KeepHoldWrittenFiles); // 기본 켜짐
        Assert.False(ParseMemory("""{"keep_hold_written_files":false}""").KeepHoldWrittenFiles);
        Assert.True(ParseMemory("""{"keep_hold_written_files":false}""").KeepHold); // 서로 독립
        Assert.Throws<SettingsException>(() => ParseMemory("""{"keep_hold_written_files":1}"""));
        Assert.True(ParseMemory("{}").KeepSparse); // 기본 켜짐
        Assert.False(ParseMemory("""{"keep_sparse":false}""").KeepSparse);
        Assert.True(ParseMemory("""{"keep_sparse":false}""").KeepHold); // 서로 독립
        Assert.Throws<SettingsException>(() => ParseMemory("""{"keep_sparse":"no"}"""));
    }

    private static MemorySettings ParseMemory(string memory)
    {
        var root = new JsonObject
        {
            ["games"] = new JsonArray(new JsonObject
            {
                ["name"] = "mw4",
                ["exe_names"] = new JsonArray("game.exe"),
                ["trace"] = @"C:\ProgramData\AsterCache\plans\mw4.prefetch.json",
                ["plan"] = @"C:\ProgramData\AsterCache\plans\mw4.plan.json",
                ["origin_root"] = @"D:\Games\MW4",
                ["allowed_roots"] = new JsonArray(@"D:\Games"),
                ["volume_label"] = "GAMES",
            }),
            ["memory"] = JsonNode.Parse(memory),
        };
        return SettingsStore.Parse(root.ToJsonString(), @"C:\ProgramData\AsterCache\plans").Memory;
    }

    [Fact]
    public void TheHoldCheckAndReleaseAreShownInTheServiceActivityOfTheSecond()
    {
        var activity = new ServiceActivity();
        using (activity.Enter(ServiceOp.Hold))
        {
        }

        using (activity.Enter(ServiceOp.Check))
        {
        }

        using (activity.Enter(ServiceOp.Release))
        {
        }

        Assert.Equal("hold+check+release", activity.Collect());
    }

    [Fact]
    public void TheAvailableMemoryRecordedForThePlayAddsWhatIsHeldSoTheLearnedFootprintDoesNotGrowByIt()
    {
        // 기록하는 값: 실제 가용 + 붙든 양. 붙들면 실제 가용이 붙든 양만큼 줄어 보이지만 기록은 그대로라 (시작 가용 - 최소 가용) 로 배우는 게임의 몫에 섞이지 않음
        long startAvail = 20000;
        long recordedDuringHold = -1;
        long actualDuringHold = -1;
        Prewarmer prewarmer = Make(Reads);
        _availFn = () => startAvail - (_hold.HeldBytes >> 20);
        RunFor(2, e =>
        {
            if (e == 1)
            {
                actualDuringHold = _availFn!();
                recordedDuringHold = actualDuringHold + prewarmer.KeepHeldMib;
            }
        });

        prewarmer.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(startAvail - 48, actualDuringHold);
        Assert.Equal(startAvail, recordedDuringHold);
        Assert.Equal(0, prewarmer.KeepHeldMib); // 게임이 끝나면 더하지 않음
    }

    // ---- 게임이 쓰는 파일 ----

    [Fact]
    public void AWrittenFileThatMeetsTheConditionsIsHeldAndTheOnesThatDoNotAreCountedByReasonInTheStartLine()
    {
        foreach (int n in new[] { 1, 2, 3, 4, 5 })
        {
            _ = _writtenFiles.Add(F(n));
        }

        _writtenReasons[F(3)] = Prewarmer.HoldWrittenNoHistory;
        _writtenReasons[F(4)] = Prewarmer.HoldWrittenShrank;
        _writtenReasons[F(5)] = Prewarmer.HoldWrittenUpdated;
        Prewarmer p = Make(Reads);
        RunFor(0);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal([(F(1), 0), (F(2), 0), (F(2), 8)], Flat(_hold.Batches));
        Assert.Equal([F(1), F(2)], _hold.WrittenMarked.Order().ToArray()); // 조건을 채운 쓰는 파일만 5 초 확인 대상
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe): 24 MiB")
            && l.Contains("game-written files: 24 MiB held in 2 files, not held by condition: 8 MiB with too little size history, 8 MiB that shrank before, 8 MiB after a game update or with the update unknown"));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("MiB in volatile or changed files")); // 조건 때문에 빠진 양은 가변·바뀐 파일 몫에 섞이지 않음
    }

    [Fact]
    public void TheWrittenFilesAreCheckedEveryFiveSecondsAndOnlyTheChangedOneIsReleasedAndNotHeldAgain()
    {
        _ = _writtenFiles.Add(F(1));
        _ = _writtenFiles.Add(F(2));
        Prewarmer p = Make(Reads);
        RunFor(30, e =>
        {
            if (e == 7)
            {
                _ = _hold.ChangedWritten.Add(F(1)); // 게임이 7 초에 첫 쓰기
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.InRange(_hold.WrittenChecks, 5, 7); // 31 초 동안 5 초마다
        string[] released = [.. _log.Lines.Where(l => l.Contains("released a game-written file the game changed"))];
        _ = Assert.Single(released);
        Assert.Contains(F(1), released[0]);
        Assert.Contains("8 MiB, 10 s after the hold started, 40 MiB still held", released[0]); // 7 초에 바뀐 것을 다음 5 초 확인 (10 초) 에 닫음, 다른 파일은 그대로
        Assert.Equal(3, _hold.Batches.Count); // 다시 붙들지 않음
        Assert.Contains("1 game-written files (8 MiB) released after the game changed them", _log.Lines.Last(l => l.Contains("cache hold ended")));
    }

    [Fact]
    public void WithoutAnyWrittenFileHeldTheFiveSecondCheckDoesNotRunButTheStartLineStillSaysSo()
    {
        Prewarmer p = Make(Reads);
        RunFor(30);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(0, _hold.WrittenChecks);
        Assert.Contains(_log.Lines, l => l.Contains("game-written files: 0 MiB held in 0 files, not held by condition: 0 MiB with too little size history"));
    }

    [Fact]
    public void WhenTheSettingIsOffWrittenFilesAreNeitherCheckedNorMentioned()
    {
        _writtenOn = false;
        _ = _writtenFiles.Add(F(1));
        Prewarmer p = Make(Reads);
        RunFor(30);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Empty(_hold.WrittenMarked);
        Assert.Equal(0, _hold.WrittenChecks);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("game-written files"));
    }

    [Fact]
    public void ReleasingEveryHeldFileAfterChangesEndsTheHold()
    {
        foreach (int n in new[] { 1, 2, 3, 4, 5 })
        {
            _ = _writtenFiles.Add(F(n));
        }

        Prewarmer p = Make(Reads);
        RunFor(60, e =>
        {
            if (e == 2)
            {
                foreach (int n in new[] { 1, 2, 3, 4, 5 })
                {
                    _ = _hold.ChangedWritten.Add(F(n));
                }
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(5, _log.Lines.Count(l => l.Contains("released a game-written file the game changed")));
        Assert.Contains(_log.Lines, l => l.Contains("cache hold ended") && l.Contains("(nothing left held)"));
    }

    [Fact]
    public void AFileThatChangedWhileTheHoldWasStartingIsReleasedRightAfterTheStartNotOnlyAtTheFirstFiveSecondCheck()
    {
        _ = _writtenFiles.Add(F(1));
        _ = _writtenFiles.Add(F(2));
        Prewarmer p = Make(Reads);
        _hold.OnHold = () => _hold.ChangedWritten.Add(F(1)); // 붙드는 동안 게임이 바꿈
        RunFor(2);

        p.TouchAtGameStart(Game("a"), "a.exe");

        string[] released = [.. _log.Lines.Where(l => l.Contains("released a game-written file the game changed"))];
        _ = Assert.Single(released);
        Assert.Contains(F(1), released[0]);
        Assert.Contains("0 s after the hold started", released[0]); // 루프의 첫 5 초 확인 전에 바로 잡음
        Assert.True(_hold.WrittenChecks >= 1);
    }

    [Fact]
    public void AFileWhoseHoldingWouldLoseMoreThanItGainsIsNotHeldAndTheStartLineCountsItsBytes()
    {
        _writtenReasons[F(3)] = Prewarmer.HoldNoCacheRead;
        _writtenReasons[F(4)] = Prewarmer.HoldNoCacheRead;
        Prewarmer p = Make(Reads);
        RunFor(0);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal([(F(1), 0), (F(2), 0), (F(2), 8), (F(5), 0)], Flat(_hold.Batches));
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe): 32 MiB") && l.Contains("16 MiB in files where holding loses more than it gains"));
    }

    // ---- 다시 붙들기 (하한 때문에 내놓은 것을 되붙듦) ----

    private static void Feed(LoadPhaseBoard board, double e, bool loading, long? queue)
    {
        _ = board.Add("a", new LoadingSample(e, loading ? 100 * Mib : 0, null, false)); // 읽기가 많으면 신호 로딩, 아니면 조용함
        if (queue is { } q)
        {
            board.NoteQueue("a", q);
        }
    }

    private static void SpendHoldAgainDisk(Prewarmer p, long bytes)
    {
        object memory = typeof(Prewarmer).GetMethod("KeepMemoryOf", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(p, ["a"])!;
        _ = memory.GetType().GetMethod("AddHoldAgainDisk")!.Invoke(memory, [bytes]);
    }

    private static ReadOp[] Gib() => [.. Enumerable.Range(0, 4).Select(i => Op(20 + i, 0, 1024))];

    [Fact]
    public void WhenEverythingWasReleasedForMemoryTheKeepingContinuesAndTheTwoMinuteRetryHoldsAgainOnceTheMemoryIsBack()
    {
        Prewarmer p = Make(Reads);
        RunFor(250, e => _avail = e == 1 ? 100 : 20000);

        p.TouchAtGameStart(Game("a"), "a.exe");

        List<string> lines = [.. _log.Lines];
        int released = lines.FindIndex(l => l.Contains("(everything released for memory)"));
        int again = lines.FindIndex(l => l.Contains("a: cache held while playing (a.exe)") && l.Contains("held again after the memory floor released everything"));
        Assert.True(released >= 0 && again > released, "the hold is held again after it ended for memory");
        Assert.Equal(2, _opened);
        Assert.Contains(lines.Skip(released + 1).Take(again - released - 1), l => l.Contains("cache kept while playing")); // 그 사이 다시 만지기 유지가 이어짐
        Assert.Contains("48 MiB of 5120 MiB target", lines[again]);
        Assert.Contains("disk reads of the play 0 MiB of 1024 MiB", lines[again]);
    }

    [Theory]
    [InlineData("loading", "loading phase")]
    [InlineData("busy", "game drive queue is not 0 or unknown")]
    [InlineData("unknown", "game drive queue is not 0 or unknown")]
    [InlineData("room", "available memory leaves no room above the floor and the margin")]
    public void TheRetryAfterEverythingWasReleasedWaitsForTheLoadingPhaseTheDriveQueueAndRoomAboveTheFloorAndLogsTheReasonOnce(string why, string text)
    {
        Prewarmer p = Make(Reads);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        RunFor(420, e =>
        {
            bool blocked = e < 300;
            _avail = e == 1 ? 100 : why == "room" && blocked ? 3100 : 20000; // 3100 - 2048 - 1024 = 28 MiB < 64 MiB
            Feed(board, e, why == "loading" && blocked && e > 100, why == "busy" && blocked ? 2 : why == "unknown" && blocked ? null : 0);
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        string line = Assert.Single(_log.Lines, l => l.Contains("hold again deferred")); // 두 번의 재시도 (120·240 초) 가 같은 사유로 미뤄져도 한 줄
        Assert.Contains(text, line);
        Assert.Equal(2, _opened); // 사유가 풀린 뒤의 재시도 (360 초) 에서 다시 붙듦
    }

    [Fact]
    public void TheRetryAfterEverythingWasReleasedAlsoWaitsForALoadingThatStartedWithAnExecutableStart()
    {
        Prewarmer p = Make(Reads);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        RunFor(250, e =>
        {
            _avail = e == 1 ? 100 : 20000;
            _ = board.Add("a", new LoadingSample(e, e <= 130 ? 100 * Mib : 0, null, e == 100)); // 초 100 에 실행 파일이 뜸: 초 130 까지 이어지는 로딩
            board.NoteQueue("a", 0);
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        _ = Assert.Single(_log.Lines, l => l.Contains("hold again deferred: loading phase")); // 120 초 재시도
        Assert.Equal(2, _opened); // 240 초 재시도
    }

    [Fact]
    public void TheFirstHoldIgnoresTheHoldAgainConditionsEvenWhileLoadingWithABusyQueue()
    {
        Prewarmer p = Make(Reads);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        _ = board.Add("a", new LoadingSample(0, 100 * Mib, null, true)); // 실행 파일 시작 로딩
        board.NoteQueue("a", 9);
        RunFor(2);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal([(F(1), 0), (F(2), 0), (F(2), 8), (F(3), 0), (F(4), 0), (F(5), 0)], Flat(_hold.Batches));
        Assert.Contains(_log.Lines, l => l.Contains("a: cache held while playing (a.exe): 48 MiB of 5120 MiB target"));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("hold again") || l.Contains("held again"));
    }

    [Fact]
    public void TheHoldAgainReadsFromDiskOnlyUpToTheRoundCapAndTheRestOfThePlayBudget()
    {
        Prewarmer p = Make(Reads);
        bool armed = false;
        RunFor(130, e =>
        {
            _avail = e == 1 ? 100 : 20000;
            if (e > 1 && !armed)
            {
                armed = true;
                _hold.Missing.Add(F(3)); // 첫 붙들기가 끝난 뒤: 파일 3 이 메모리에서 밀려남 (그 범위에서 경계, 디스크 읽기 12 MiB)
                _hold.DiskPerMiss = 12 * Mib;
                SpendHoldAgainDisk(p, Prewarmer.HoldAgainDiskCapBytes - (10 * Mib)); // 플레이 합계 상한까지 10 MiB 남음
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        string again = Assert.Single(_log.Lines, l => l.Contains("held again after the memory floor released everything"));
        Assert.Contains("25 MiB of", again); // 파일 1 과 파일 2 (24 MiB) 에 파일 3 의 경계 앞 한 조각
        Assert.Contains("disk read cap reached", again); // 12 MiB > 남은 10 MiB: 파일 4·5 는 붙들지 않음
        Assert.Contains("disk reads of the play 1026 MiB of 1024 MiB", again); // 경계를 알아채는 읽기만큼은 넘을 수 있음
    }

    [Fact]
    public void WithThePlayBudgetUsedUpTheHoldAgainIssuesNoRequestThatCouldReadTheDiskAndSaysWhyOnce()
    {
        Prewarmer p = Make(Reads);
        bool armed = false;
        RunFor(250, e =>
        {
            _avail = e == 1 ? 100 : 20000;
            if (e > 1 && !armed)
            {
                armed = true;
                SpendHoldAgainDisk(p, Prewarmer.HoldAgainDiskCapBytes); // 다 씀
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(1, _opened); // 120 초와 240 초의 재시도 모두 붙들기를 새로 열지 않음
        Assert.DoesNotContain(_log.Lines, l => l.Contains("held again after the memory floor released everything"));
        string line = Assert.Single(_log.Lines, l => l.Contains("hold again deferred")); // 두 번의 재시도가 같은 사유라 한 줄
        Assert.Contains("the disk read cap of the play is used", line);
    }

    [Fact]
    public void WhenAnotherExecutableStartsAfterEverythingWasReleasedItsFirstHoldFollowsThePlayBudgetToo()
    {
        Prewarmer p = Make(Reads);
        bool armed = false;
        RunFor(60, e =>
        {
            _avail = e == 1 ? 100 : 20000;
            if (e > 1 && !armed)
            {
                armed = true;
                SpendHoldAgainDisk(p, Prewarmer.HoldAgainDiskCapBytes); // 이 플레이의 합계를 다 씀
            }

            if (e == 11)
            {
                _now += Prewarmer.StartTouchMinGap;
                p.TouchAtGameStart(Game("a"), "b.exe"); // 모두 내놓은 뒤 같은 게임의 다른 실행 파일이 뜸: 내놓은 범위를 다시 읽으므로 합계를 따름
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(1, _opened);
        Assert.DoesNotContain(_log.Lines, l => l.Contains("cache held while playing (b.exe)"));
        Assert.Contains(_log.Lines, l => l.Contains("hold again deferred: the disk read cap of the play is used"));
    }

    [Fact]
    public void WithThePlayBudgetUsedUpTheTwoMinuteCheckDoesNotHoldTheReleasedPartAgain()
    {
        Prewarmer p = Make(Gib());
        bool armed = false;
        RunFor(130, e =>
        {
            _avail = e == 1 ? 1500 : 20000;
            if (e > 1 && !armed)
            {
                armed = true;
                SpendHoldAgainDisk(p, Prewarmer.HoldAgainDiskCapBytes);
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal((2048 - 1500 + 256) * Mib, _hold.PendingBytes); // 내놓은 쪽은 그대로 남음
        Assert.DoesNotContain(_log.Lines, l => l.Contains("cache held again after the memory floor released it"));
        Assert.Contains(_log.Lines, l => l.Contains("hold again deferred: the disk read cap of the play is used"));
    }

    [Fact]
    public void AfterAPartialReleaseTheTwoMinuteCheckHoldsTheReleasedPartAgain()
    {
        Prewarmer p = Make(Gib());
        RunFor(130, e => _avail = e == 1 ? 1500 : 20000);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal([(2048 - 1500 + 256) * Mib], _hold.Released);
        Assert.Equal(0, _hold.PendingBytes);
        Assert.Contains(_log.Lines, l => l.Contains("a: cache held again after the memory floor released it (a.exe): 804 MiB of 5120 MiB target, 0 MiB read from disk, disk reads of the play 0 MiB of 1024 MiB"));
        Assert.Contains("804 MiB held again after the memory floor released them", _log.Lines.Last(l => l.Contains("cache hold ended")));
        Assert.Equal(1, _opened); // 같은 붙들기 안에서 다시 붙듦
    }

    [Theory]
    [InlineData("loading", "loading phase")]
    [InlineData("busy", "game drive queue is not 0 or unknown")]
    [InlineData("unknown", "game drive queue is not 0 or unknown")]
    [InlineData("room", "available memory leaves no room above the floor and the margin")]
    public void AfterAPartialReleaseTheTwoMinuteCheckWaitsForTheLoadingPhaseTheDriveQueueAndRoomAboveTheFloorAndLogsTheReasonOnce(string why, string text)
    {
        Prewarmer p = Make(Gib());
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        RunFor(250, e =>
        {
            bool blocked = e < 200;
            _avail = e == 1 ? 1500 : why == "room" && blocked ? 3100 : 20000;
            Feed(board, e, why == "loading" && e is > 100 and <= 130, why == "busy" && blocked ? 2 : why == "unknown" && blocked ? null : 0);
            if (e >= 240)
            {
                Thread.Sleep(20); // 다시 붙들기는 잠금이 쥐여 있으면 다음 초로 미룸: 가짜 쉬기는 즉시 끝나므로 유지 회차가 잠금을 놓을 시간을 줌
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        string line = Assert.Single(_log.Lines, l => l.Contains("hold again deferred")); // 120 초 점검만 미룸
        Assert.Contains(text, line);
        Assert.Equal(0, _hold.PendingBytes); // 240 초 점검에서 다시 붙듦
        _ = Assert.Single(_log.Lines, l => l.Contains("cache held again after the memory floor released it"));
    }

    [Fact]
    public void ReleasingAndHoldingAgainDoNotAlternateWhenTheAvailableMemoryWobblesJustAboveTheFloor()
    {
        Prewarmer p = Make(Gib());
        long extra = 0; // 게임이 잡은 메모리 (MiB)
        long lowest = long.MaxValue;
        _availFn = () => 8000 - (_hold.HeldBytes >> 20) - extra;
        RunFor(700, e =>
        {
            extra = e < 2 ? 0 : e < 10 ? 3000 : e < 150 ? 0 : e % 20 < 10 ? 900 : 0; // 초 2~10 에 하한 아래로, 초 150 부터는 하한 + 1 GiB 바로 위에서 오르내림
            lowest = e > 130 ? Math.Min(lowest, _availFn()) : lowest;
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        _ = Assert.Single(_hold.Released); // 내놓기는 한 번뿐
        _ = Assert.Single(_log.Lines, l => l.Contains("cache held again after the memory floor released it"));
        Assert.True(lowest >= Prewarmer.HoldMinFloorMib + Prewarmer.HoldRefillMarginMib, $"lowest available {lowest}"); // 다시 붙든 뒤에도 가용이 하한 + 1 GiB 이상
    }

    [Fact]
    public void TheHoldAgainTargetAlwaysLeavesTheFloorPlusOneGiBAvailable()
    {
        for (long avail = 0; avail <= 40000; avail += 37)
        {
            long target = Prewarmer.HoldAgainTargetBytes(avail, Prewarmer.HoldMinFloorMib) >> 20;
            Assert.True(target == 0 || avail - target >= Prewarmer.HoldMinFloorMib + Prewarmer.HoldRefillMarginMib, $"avail {avail} target {target}");
            Assert.True(target <= avail / 2 && target <= 5120, $"avail {avail} target {target}");
        }

        Assert.Equal(28 * Mib, Prewarmer.HoldAgainTargetBytes(3100, 2048));
        Assert.Equal(5L << 30, Prewarmer.HoldAgainTargetBytes(20000, 2048));
    }

    [Fact]
    public void ReleasingEveryGameWrittenFileDoesNotStopTheKeepingTouchingEither()
    {
        foreach (int n in new[] { 1, 2, 3, 4, 5 })
        {
            _ = _writtenFiles.Add(F(n));
        }

        Prewarmer p = Make(Reads);
        RunFor(60, e =>
        {
            if (e == 2)
            {
                foreach (int n in new[] { 1, 2, 3, 4, 5 })
                {
                    _ = _hold.ChangedWritten.Add(F(n));
                }
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Contains(_log.Lines, l => l.Contains("cache kept while playing")); // 붙든 것이 모두 풀린 뒤에도 다시 만지기 유지가 이어짐
        Assert.Equal(1, _opened); // 다시 붙들지는 않음
    }

    // ---- 검토 반영 (R1 감시 간격, R2 포기 범위, R3 다른 실행 파일 시작, 번갈아 되풀이) ----

    [Fact]
    public void RightAfterAHoldAgainTheFloorIsWatchedEverySecondEvenWhenTheAvailableMemoryBeforeItWasHigh()
    {
        Prewarmer p = Make(Gib());
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        long extra = 0;
        _availFn = () => 8000 - (_hold.HeldBytes >> 20) - extra;
        RunFor(260, e =>
        {
            extra = e < 2 ? 0 : e < 10 ? 3000 : 0; // 초 2~10 에 하한 아래로 내려가 일부를 내놓음
            Feed(board, e, false, e < 200 ? 2 : 0); // 첫 점검 (120 초) 은 대기열 때문에 미루고 둘째 (240 초) 에서 다시 붙듦
        });
        List<double> after = [];
        Action<TimeSpan, CancellationToken> inner = _sleep!;
        _sleep = (t, ct) =>
        {
            if (_log.Lines.Any(l => l.Contains("cache held again after")))
            {
                after.Add(t.TotalSeconds);
            }

            inner(t, ct);
        };

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.NotEmpty(after);
        Assert.Equal(1, after[0]); // 다시 붙들기 전 가용은 5304 MiB (5 초 간격) 였지만 붙든 뒤는 4000 MiB (하한 + 2 GiB 아래): 1 초
    }

    [Fact]
    public void TheHoldAgainGivesUpOnlyTheRangeWhereAMissingStretchWasFoundAndTriesTheLaterRangesOfTheSameFileAtTheNextCheck()
    {
        Prewarmer p = Make(Gib());
        ReadOp[] ops = [.. Enumerable.Range(0, 4).Select(i => new ReadOp(F(60), i * 8 * Mib, 8 * Mib))];
        RunFor(250, e =>
        {
            _avail = e == 1 ? 1500 : 20000;
            if (e == 2)
            {
                _hold.PendingBytes = 0;
                _hold.PendingOps = [.. ops];
                _ = _hold.MissingRanges.Add((F(60), 8 * Mib)); // 둘째 범위에서 경계: 그 뒤 두 범위는 같은 파일이라 건너뜀 (확인하지 않음)
            }

            if (e == 130)
            {
                _hold.MissingRanges.Clear();
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal([ops[1]], _hold.GivenUp); // 메모리에 없다고 확인한 범위만 포기
        Assert.Empty(_hold.PendingOps); // 건너뛴 뒤 두 범위는 240 초 점검에서 붙듦
        Assert.Equal(2, _log.Lines.Count(l => l.Contains("cache held again after the memory floor released it")));
    }

    [Fact]
    public void WhenAnotherExecutableStartsWhileWaitingToHoldAgainAfterEverythingWasReleasedTheHoldStartsAsAtAnExecutableStartWithoutTheHoldAgainConditions()
    {
        Prewarmer p = Make(Reads);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        RunFor(60, e =>
        {
            _avail = e == 1 ? 100 : 20000;
            if (e >= 6)
            {
                _ = board.Add("a", new LoadingSample(e, 100 * Mib, null, e == 6)); // 실행 파일 시작 로딩과 바쁜 대기열: 다시 붙들기 조건이면 미룸
                board.NoteQueue("a", 9);
            }

            if (e == 11)
            {
                _now += Prewarmer.StartTouchMinGap;
                p.TouchAtGameStart(Game("a"), "b.exe"); // 런처 뒤에 본체가 이어 뜸
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(2, _opened);
        Assert.Contains(_log.Lines, l => l.Contains("a: cache held while playing (b.exe): 48 MiB of 5120 MiB target") && !l.Contains("held again"));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("hold again deferred"));
    }

    [Fact]
    public void AfterAnUnmeasuredBatchAnotherExecutableOfTheSameGameDoesNotStartANewHoldEvenAfterEverythingWasReleased()
    {
        _hold.Measured = false; // 첫 패스의 첫 묶음에서 계측하지 못함: 이 플레이에서는 더 붙들지 않음
        Prewarmer p = Make(Reads);
        RunFor(40, e =>
        {
            _avail = e == 1 ? 100 : 20000; // 모두 내놓음
            if (e == 11)
            {
                _now += Prewarmer.StartTouchMinGap;
                p.TouchAtGameStart(Game("a"), "b.exe"); // 모두 내놓은 뒤 같은 게임의 다른 실행 파일이 뜸
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(1, _opened); // 새 붙들기를 열지 않음
        Assert.DoesNotContain(_log.Lines, l => l.Contains("cache held while playing (b.exe)"));
        _ = Assert.Single(_log.Lines, l => l.Contains("cache hold stops reading from the disk for this play"));
    }

    [Fact]
    public void WhenAnotherExecutableStartsWhileSomethingIsStillHeldNothingIsHeldAgain()
    {
        Prewarmer p = Make(Gib());
        RunFor(40, e =>
        {
            _avail = e == 1 ? 1500 : 20000;
            if (e == 10)
            {
                _now += Prewarmer.StartTouchMinGap;
                p.TouchAtGameStart(Game("a"), "b.exe");
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(1, _opened); // main 과 같이 붙든 것이 남아 있으면 새 실행 파일 시작이 붙들기에 영향을 주지 않음
    }

    [Fact]
    public void WhenTheAvailableMemorySwingsAcrossTheFloorTheHoldAgainHappensOnlyAtTheTwoMinuteChecks()
    {
        Prewarmer p = Make(Gib());
        long extra = 0;
        _availFn = () => 8000 - (_hold.HeldBytes >> 20) - extra;
        RunFor(700, e => extra = e < 2 ? 0 : e % 70 < 30 ? 4500 : 0); // 하한 아래로 내려갔다 하한 + 1 GiB 위로 올라오기를 되풀이

        p.TouchAtGameStart(Game("a"), "a.exe");

        int again = _log.Lines.Count(l => l.Contains("cache held again after the memory floor released it"));
        Assert.True(_hold.Released.Count >= 2, $"released {_hold.Released.Count}");
        Assert.InRange(again, 1, _hold.Rechecks); // 다시 붙들기는 2분 점검 때만, 점검마다 한 번까지
    }

    // ---- 로딩 중 점검·순서 패스 (직전 검토의 후속) ----

    [Fact]
    public void WhileALoadingThatStartedWithAnExecutableStartTheCheckOfTheHeldRangesRunsExactlyAsOutsideLoading()
    {
        _hold.NextCheck = () => new HoldCheckResult(40 * Mib, 6 * Mib, 4 * Mib, 0, false, [], 2, 120, 1.5, 0.4);
        Prewarmer p = Make(Reads);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        RunFor(130, e => _ = board.Add("a", new LoadingSample(e, 100 * Mib, null, e == 1))); // 초 1 에 실행 파일이 뜨고 읽기가 계속 많음: 120 초 점검은 실행 파일 시작 로딩 중

        p.TouchAtGameStart(Game("a"), "a.exe");
        for (int s = 131; s <= 160; s++)
        {
            _ = board.Add("a", new LoadingSample(s, 0, null, false)); // 조용해져 로딩이 끝남
        }

        Assert.Equal([Prewarmer.KeepDiskReadCapBytes], _hold.RecheckCaps); // 로딩이 없을 때와 같은 상한
        Assert.Equal([true], _hold.DropOnCaps);
        Assert.Equal([true], _hold.Refills);
        Assert.Contains(_log.Lines, l => l.Contains("a: loading phase ended after") && !l.Contains("held back")); // 미룬 일 없음
    }

    [Fact]
    public void WhileLoadingTheOrderPassStopsAtTheRoundDiskReadCapEvenWhenNoBatchIsABoundary()
    {
        ReadOp[] gib = [.. Enumerable.Range(0, 3).Select(i => Op(30 + i, 0, 1024))];
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(gib, cheat);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        int at15 = 0;
        int at25 = 0;
        RunFor(40, e =>
        {
            _ = board.Add("a", new LoadingSample(e, 100 * Mib, null, false)); // 초 5 부터 읽기가 계속 많은 경기 안 로딩
            if (e == 15)
            {
                at15 = _touched.Count;
                _touchOverride = ranges => new TouchResult(ranges.Sum(o => o.Length), 0.1, 0, 0, TouchEnd.Completed) { DiskBytes = 8 * Mib }; // 흩어진 빈 블록: 배치마다 8 MiB 가 디스크에서 읽힘 (경계는 아님)
            }

            if (e == 25)
            {
                at25 = _touched.Count;
            }
        });

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Equal(5, at25 - at15); // 초 20 의 회차: 8 MiB 씩 다섯 배치 (40 MiB) 에서 32 MiB 상한을 넘어 끝남, 3 GiB 전체 (192 배치) 를 만지지 않음
    }

    // ---- 붙들기를 먼저, 이어 붙들기, 붙든 동안의 유지 회차 ----

    private static object MemoryOf(Prewarmer p) => typeof(Prewarmer).GetMethod("KeepMemoryOf", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(p, ["a"])!;

    // 처음 붙들기가 상한에서 멈추게: 파일 2·4 가 메모리에 없고 경계마다 20 MiB 가 읽혀 두 번째 경계에서 상한 (32 MiB) 을 넘음. 남은 범위는 파일 2 의 두 번째 범위 (건너뜀) 와 파일 5 (방문하지 않음).
    private void StopTheFirstHoldAtTheCap()
    {
        _hold.Missing.Add(F(2));
        _hold.Missing.Add(F(4));
        _hold.DiskPerMiss = 20 * Mib;
    }

    [Fact]
    public void TheHoldRunsBeforeTheStartTouchAndTheStartTouchLeavesTheHeldRangesOut()
    {
        _hold.Missing.Add(F(2));
        Prewarmer p = Make(Reads);
        int touchedAtFirstHold = -1;
        _hold.OnHold = () => touchedAtFirstHold = touchedAtFirstHold < 0 ? _touched.Count : touchedAtFirstHold;
        RunFor(3);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(0, touchedAtFirstHold); // 첫 붙들기 배치가 불릴 때 시작 만지기는 아직 아무것도 만지지 않음
        string[] paths = [.. _touched.SelectMany(b => b).Select(o => o.Path).Distinct()];
        Assert.Equal([F(2)], paths); // 붙든 파일 1·3·4·5 는 다시 만지지 않고 붙들지 못한 파일 2 만 만짐
    }

    [Fact]
    public void AGameThatCannotBeHeldStartsWithTheStartTouchAsBefore()
    {
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(3);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Equal(0, _opened);
        Assert.Empty(_hold.Batches);
        string[] paths = [.. _touched.SelectMany(b => b).Select(o => o.Path).Distinct()];
        Assert.Equal([F(1), F(2), F(3), F(4), F(5)], paths.Order()); // 붙들기가 금지된 게임은 시작 만지기가 전체 목록을 만짐
    }

    [Fact]
    public void TheHoldLogsHowManySecondsAfterTheExecutableStartedAndHowMuchIsLeftToContinueWith()
    {
        StopTheFirstHoldAtTheCap();
        Prewarmer p = Make(Reads);
        RunFor(1);

        p.TouchAtGameStart(Game("a"), "a.exe", false, _now - TimeSpan.FromSeconds(3));

        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe)") && l.Contains("started 3.0 s after the executable started") && l.Contains("MiB left to continue with (8 MiB skipped after a missing stretch, 8 MiB not visited)"));
    }

    [Fact]
    public void AfterTheFirstHoldStoppedAtTheDiskReadCapTheRestIsHeldInRankOrderInFourMiBBatches()
    {
        StopTheFirstHoldAtTheCap();
        Prewarmer p = Make(Reads);
        var perSecond = new List<int>();
        RunFor(10, e =>
        {
            if (e == 1)
            {
                _hold.DiskPerMiss = 0; // 이어 붙들 때는 디스크 읽기가 없음
            }

            perSecond.Add(_hold.Batches.Count);
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        List<(string Path, long OffsetMib)> flat = Flat(_hold.Batches);
        Assert.Equal([(F(2), 8), (F(5), 0), (F(5), 4)], flat.Skip(4)); // 건너뛴 파일 2 의 범위를 먼저, 그다음 방문하지 않은 파일 5 (순위 순), 한 번에 4 MiB
        Assert.All(_hold.Batches.Skip(2), b => Assert.True(b.Sum(o => o.Length) <= 4 * Mib));
        Assert.Equal([1, 2], perSecond.Zip(perSecond.Skip(1), (a, b) => b - a).Where(d => d > 0)); // 첫 확인은 빠진 조각에서 묶음 하나로 끝나고, 다음 확인이 메모리에 있는 파일 5 의 두 묶음을 이어서 붙듦
        Assert.Equal(flat.Count, flat.Distinct().Count()); // 같은 범위를 두 번 요청하지 않음
        Assert.Contains(_log.Lines, l => l.Contains("cache hold continuing after the first pass (a.exe): holding") && l.Contains("MiB left"));
        Assert.Contains(_log.Lines, l => l.Contains("cache hold continuing after the first pass (a.exe): finished"));
        Assert.Contains(_log.Lines, l => l.Contains("cache hold ended") && l.Contains("9 MiB held by continuing after the first pass (3 requests, 0 MiB read from disk, 0 MiB left)"));
    }

    [Fact]
    public void AFirstHoldThatEndedWithZeroMiBContinuesWithTheRest()
    {
        foreach (int n in new[] { 1, 2, 3, 4, 5 })
        {
            _hold.Missing.Add(F(n));
        }

        _hold.PieceBeforeBoundary = 0;
        _hold.DiskPerMiss = Mib;
        Prewarmer p = Make(Reads);
        RunFor(6, e =>
        {
            if (e == 1)
            {
                _hold.Missing.Clear(); // 메모리에 다시 올라옴
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe): 0 MiB of 5120 MiB target") && l.Contains("MiB left to continue with"));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("nothing of the"));
        Assert.Equal(1, _opened);
        Assert.Contains(_log.Lines, l => l.Contains("cache hold ended") && l.Contains("held by continuing after the first pass"));
    }

    [Theory]
    [InlineData("loading", "loading phase")]
    [InlineData("busy", "game drive queue is not 0 or unknown")]
    [InlineData("unknown", "game drive queue is not 0 or unknown")]
    [InlineData("room", "available memory leaves no room above the floor and the margin")]
    public void TheContinuationIssuesNothingWhileTheConditionsFailAndLogsTheReasonOnceAndResumesAfterwards(string why, string text)
    {
        StopTheFirstHoldAtTheCap();
        Prewarmer p = Make(Reads);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        int before = -1;
        int atEndOfBlock = -1;
        RunFor(45, e =>
        {
            bool blocked = e < 6; // 로딩은 읽기가 조용해진 뒤 15 초 더 이어져 그만큼 늦게 풀림
            if (e == 1)
            {
                _hold.DiskPerMiss = 0;
                before = _hold.Batches.Count;
            }

            _avail = why == "room" && blocked ? 3100 : 20000; // 3100 - 2048 - 1024 = 28 MiB < 64 MiB
            Feed(board, e, why == "loading" && blocked, why == "busy" && blocked ? 2 : why == "unknown" && blocked ? null : 0);
            if (e == 5)
            {
                atEndOfBlock = _hold.Batches.Count;
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(before, atEndOfBlock); // 막힌 동안 발행 0
        _ = Assert.Single(_log.Lines, l => l.Contains("cache hold continuing after the first pass") && l.Contains($"deferred: {text}")); // 같은 사유는 한 줄
        Assert.Contains(_hold.Batches.SelectMany(b => b), o => o.Path == F(5)); // 풀린 뒤 이어 붙듦
    }

    [Fact]
    public void TheDiskReadsOfTheContinuationCountAgainstThePlayBudgetSharedWithHoldingAgain()
    {
        StopTheFirstHoldAtTheCap();
        Prewarmer p = Make(Reads);
        RunFor(4, e =>
        {
            if (e == 1)
            {
                _hold.DiskPerMiss = 3 * Mib; // 이어 붙들 때 건너뛴 범위에서 경계를 알아채는 읽기
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Contains(_log.Lines, l => l.Contains("cache hold continuing after the first pass") && l.Contains("3 MiB read from disk (disk reads of the play 3 MiB of 1024 MiB)"));
    }

    [Fact]
    public void ARangeThatHitAMissingStretchIsDroppedTogetherWithTheFollowingRangesOfTheSameFileAndHeldRangesAreRemoved()
    {
        Prewarmer p = Make(Reads);
        object memory = MemoryOf(p);
        Type t = memory.GetType();
        ReadOp[] pending = [Op(1, 0, 8), Op(1, 8, 8), Op(1, 16, 8), Op(2, 0, 8), Op(3, 0, 8)];
        _ = t.GetMethod("SetHoldPending")!.Invoke(memory, [pending]);

        _ = t.GetMethod("FinishHoldPending")!.Invoke(memory, [new[] { Op(3, 0, 4) }, new[] { Op(1, 0, 4) }]); // 파일 3 의 앞 4 MiB 를 붙들었고 파일 1 의 첫 범위에서 경계를 만남

        var left = (List<ReadOp>)t.GetMethod("HoldPending")!.Invoke(memory, null)!;
        Assert.Equal([Op(2, 0, 8), new ReadOp(F(3), 4 * Mib, 4 * Mib)], left); // 파일 1 의 이어지는 범위는 함께 빠지고 붙든 앞 4 MiB 도 빠짐
    }

    [Fact]
    public void WhileTheHoldCoversOnlyPartOfTheListTheKeepRoundsTouchTheRestAndLeaveTheHeldRangesOut()
    {
        _hold.Missing.Add(F(2)); // 파일 2 는 붙들지 못함
        Prewarmer p = Make(Reads);
        RunFor(40, e =>
        {
            if (e is >= 11 and <= 25) // 회차는 감시와 따로 도므로 (가짜 쉬기는 즉시 끝남) 로그가 남을 때까지 기다림
            {
                _ = SpinWait.SpinUntil(() => _log.Lines.Any(l => l.Contains("cache kept while playing (a.exe)")), 1000);
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        List<string> lines = [.. _log.Lines];
        int kept = lines.FindIndex(l => l.Contains("cache kept while playing (a.exe)"));
        int ended = lines.FindIndex(l => l.Contains("cache hold ended"));
        Assert.True(kept >= 0 && kept < ended, "a keep round runs while the hold is active");
        Assert.Contains("MiB held by the hold left out", lines[kept]);
        string[] roundPaths = [.. _touched.SelectMany(b => b).Select(o => o.Path).Distinct()];
        Assert.Equal([F(2)], roundPaths); // 붙든 범위는 유지 대상에서 빠짐
    }

    [Fact]
    public void TheFloorWatchOfTheHoldIsNotBlockedWhileTheStartTouchIsStillRunning()
    {
        _hold.Missing.Add(F(2)); // 붙들지 못한 범위가 있어야 시작 만지기가 할 일이 있음
        Prewarmer p = Make(Reads);
        bool sawRelease = false;
        int calls = 0;
        _touchOverride = ranges =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                sawRelease = SpinWait.SpinUntil(() => _hold.Released.Count > 0, 10000); // 시작 만지기가 오래 걸리는 동안 감시가 내놓기를 함
            }

            return null;
        };
        RunFor(30, e =>
        {
            if (e == 2)
            {
                _avail = 2000; // 하한 (2048) 아래
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.True(sawRelease, "the hold watch released for memory while the start touch was running");
    }

    [Fact]
    public void WhenThePlayBudgetOfDiskReadsIsUsedTheContinuationStopsWithOneLine()
    {
        StopTheFirstHoldAtTheCap();
        Prewarmer p = Make(Reads);
        int before = -1;
        RunFor(12, e =>
        {
            if (e == 1)
            {
                _hold.DiskPerMiss = 0;
                before = _hold.Batches.Count;
                SpendHoldAgainDisk(p, Prewarmer.HoldAgainDiskCapBytes - (2 * Mib)); // 플레이 합계 상한까지 2 MiB: 한 번 요청분 (4 MiB) 보다 적음
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(before, _hold.Batches.Count); // 더 요청하지 않음
        _ = Assert.Single(_log.Lines, l => l.Contains("cache hold continuing after the first pass") && l.Contains("stopped: the disk read cap of the play is used"));
    }

    [Fact]
    public void ADeferralIsLoggedOnlyWhenItsKindChangesNotWhenTheQueueFlapsBetweenZeroAndBusy()
    {
        StopTheFirstHoldAtTheCap();
        Prewarmer p = Make(Reads);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        RunFor(40, e =>
        {
            if (e == 1)
            {
                _hold.DiskPerMiss = 0;
            }

            Feed(board, e, false, e % 2 == 0 ? 0 : 3); // 대기열이 1 초마다 0 과 3 으로 바뀜
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        string[] lines = [.. _log.Lines.Where(l => l.Contains("cache hold continuing after the first pass"))];
        Assert.Single(lines, l => l.Contains("deferred: game drive queue is not 0 or unknown")); // 같은 종류의 미룸은 한 번
        Assert.True(lines.Length <= 4, string.Join(" || ", lines)); // 붙드는 중 · 미룸 · 다 씀 정도
    }

    [Fact]
    public void AFastCheckHoldsUpToSixteenFourMiBBatchesInMemoryAndEndsAtTheFirstMissingStretch()
    {
        ReadOp[] big = [Op(1, 0, 8), .. Enumerable.Range(0, 10).Select(i => Op(10 + i, 0, 16))];
        _hold.Missing.Add(F(1));
        _hold.DiskPerMiss = 40 * Mib; // 첫 패스가 첫 경계에서 상한을 넘어 끝남: 나머지가 모두 남음
        Prewarmer p = Make(big);
        var perSecond = new List<int>();
        RunFor(20, e =>
        {
            if (e == 1)
            {
                _hold.DiskPerMiss = 0;
                _ = _hold.Missing.Add(F(15)); // 이어 붙들 후보 가운데 메모리에 없는 파일 하나
            }

            perSecond.Add(_hold.Batches.Count);
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        int[] perCheck = [.. perSecond.Zip(perSecond.Skip(1), (a, b) => b - a).Where(d => d > 0)];
        Assert.Contains(16, perCheck); // 메모리에 있는 범위는 한 번의 확인에 64 MiB (4 MiB 묶음 16개) 까지
        Assert.All(perCheck, d => Assert.True(d <= 16));
        int missingBatch = _hold.Batches.ToList().FindIndex(b => b.Any(o => o.Path == F(15)));
        Assert.True(missingBatch >= 0);
        Assert.Contains(missingBatch + 1, perSecond); // 빠진 파일의 묶음이 그 확인의 마지막 묶음임 (그 뒤 묶음은 다음 확인에서)
        Assert.All(_hold.Batches.Skip(1), b => Assert.True(b.Sum(o => o.Length) <= 4 * Mib));
    }

    [Fact]
    public void TheKeepRoundsWhileHoldingWaitForTheStartTouchToEnd()
    {
        _hold.Missing.Add(F(2));
        Prewarmer p = Make(Reads);
        bool recorded = false;
        int keptBeforeTheTouchEnded = -1;
        _touchOverride = ranges =>
        {
            _ = SpinWait.SpinUntil(() => Volatile.Read(ref recorded), 10000); // 시작 만지기가 끝나지 않은 동안
            return null;
        };
        RunFor(30, e =>
        {
            if (e == 12)
            {
                Thread.Sleep(300); // 회차가 돌 수 있었다면 돌 시간을 줌
                keptBeforeTheTouchEnded = KeptLines();
                Volatile.Write(ref recorded, true);
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(0, keptBeforeTheTouchEnded);
    }

    [Fact]
    public void TheKeepRoundsWhileHoldingDoNotRunWhileTheLoadingPhaseIsOn()
    {
        _hold.Missing.Add(F(2));
        Prewarmer p = Make(Reads);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        RunFor(60, e =>
        {
            _ = board.Add("a", new LoadingSample(e, 100 * Mib, null, e == 1)); // 실행 파일 시작으로 시작한 로딩이 읽기가 계속 많아 이어짐
            Thread.Sleep(2);
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(0, KeptLines());
    }

    [Fact]
    public void AGameThatCannotBeHeldTakesTheReuseBaselineAfterTheStartTouchAsBefore()
    {
        GameSettings cheat = Cheat("a");
        _reuseStep = 0;
        Prewarmer p = Make(Reads, cheat);
        _touchOverride = ranges =>
        {
            _reuse += 5000 * Mib; // 시작 만지기 동안의 재사용 (기준값에 들어가면 첫 회차가 바로 돎)
            return null;
        };
        RunFor(60);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Equal(0, KeptLines()); // 시작 만지기가 끝난 뒤 늘어난 재사용이 없으므로 2 분 주기 전에는 회차가 없음
    }

    [Fact]
    public void TheFloorReleaseDoesNotWaitForTheSerializationLockAtTheTwoMinuteCheck()
    {
        Prewarmer p = Make(Reads);
        bool sawRelease = false;
        Task? holder = null;
        RunFor(130, e =>
        {
            if (e == 119)
            {
                using var taken = new ManualResetEventSlim();
                holder = Task.Factory.StartNew(() =>
                {
                    lock (TouchSerial(p)) // 다른 경로 (시작 만지기·유지 회차) 가 잠금을 쥔 때
                    {
                        taken.Set();
                        sawRelease = SpinWait.SpinUntil(() => _hold.Released.Count > 0, 10000);
                    }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                taken.Wait();
            }

            if (e == 121)
            {
                _avail = 2000; // 하한 (2048) 아래: 2 분 점검 (초 120) 이 잠금을 못 잡아 미뤄져도 내놓기는 바로 일어나야 함
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        holder?.Wait();
        Assert.True(sawRelease, "the floor release happened while another path held the serialization lock");
    }

    [Fact]
    public void TheFirstHoldTargetLeavesRoomForWhatTheGameWillStillTake()
    {
        _avail = 14000; // 목표 식 그대로면 min(5 GiB, 7000, 11952) = 5120 MiB
        Prewarmer p = Make(Reads);
        p.Footprint = _ => (9979, 3); // 14000 - 2048 - 9979 = 1973 MiB
        RunFor(1);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe): 48 MiB of 1973 MiB target"));
    }

    [Fact]
    public void WhenDiskReadsCannotBeToldApartACheckContinuesWithOneBatchOnly()
    {
        ReadOp[] big = [Op(1, 0, 8), .. Enumerable.Range(0, 10).Select(i => Op(10 + i, 0, 16))];
        _hold.Missing.Add(F(1));
        _hold.DiskPerMiss = 40 * Mib; // 첫 패스가 첫 경계에서 상한을 넘어 끝남: 나머지가 모두 남음
        Prewarmer p = Make(big);
        var perSecond = new List<int>();
        RunFor(20, e =>
        {
            if (e == 1)
            {
                _hold.DiskPerMiss = 0;
                _hold.Measured = false; // 하드 폴트 수를 읽을 수 없는 환경
            }

            perSecond.Add(_hold.Batches.Count);
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        int[] perCheck = [.. perSecond.Zip(perSecond.Skip(1), (a, b) => b - a).Where(d => d > 0)];
        _ = Assert.Single(perCheck); // 디스크 읽기를 알아챌 수 없으면 그 묶음 (4 MiB) 하나로 이 플레이의 이어 붙들기가 끝남: 다음 확인에서 더 요청하지 않음
        Assert.Equal(1, perCheck[0]);
    }

    [Fact]
    public void AnUnmeasuredBatchIsCountedInFullAgainstTheSharedPlayBudgetAndEndsTheContinuationForTheWholePlay()
    {
        StopTheFirstHoldAtTheCap();
        Prewarmer p = Make(Reads);
        int before = -1;
        RunFor(40, e =>
        {
            if (e == 1)
            {
                (_hold.DiskPerMiss, _hold.Measured) = (0, false); // 하드 폴트 수를 읽을 수 없어 디스크 읽기를 가려내지 못함
                before = _hold.Batches.Count;
                SpendHoldAgainDisk(p, Prewarmer.HoldAgainDiskCapBytes - (6 * Mib)); // 플레이 합계 상한까지 6 MiB 남음
            }
        });

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(before + 1, _hold.Batches.Count); // 계측하지 못한 묶음 하나로 끝: 이 플레이에서는 이어 붙들기를 더 하지 않음
        Assert.Contains(_log.Lines, l => l.Contains("cache hold continuing after the first pass") && l.Contains("disk reads of the play 1019 MiB of 1024 MiB")); // 그 묶음 (1 MiB) 이 공유 예산에서 빠짐
        _ = Assert.Single(_log.Lines, l => l.Contains("cache hold stops reading from the disk for this play"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AfterAnUnmeasuredBatchTheProbeAndTheCheckReadBackAreSkippedButTheFloorReleaseAndTheReasonLogStayOnce(bool unmeasured)
    {
        _hold.Missing.Add(F(2)); // 첫 패스가 경계를 만남 (표본 읽기가 붙는 자리)
        _hold.Measured = !unmeasured;
        Prewarmer p = Make(Reads);
        RunFor(135, e => _avail = e is >= 125 and <= 127 ? 1500 : 20000); // 2 분 점검은 초 120, 초 125 에 하한 아래로 내려가 내놓음

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.NotEmpty(_hold.Released); // 내놓기 판단은 평소대로
        Assert.Equal(unmeasured, _probeCalls == 0); // 계측하지 못한 플레이에서는 표본 읽기도 하지 않음 (계측 가능하면 첫 패스의 경계마다 읽음)
        Assert.Equal([!unmeasured], _hold.Refills.Take(1)); // 점검의 다시 읽기도 하지 않음 (줄여 간 쪽은 목록에서 뺌)
        Assert.Equal(unmeasured ? 1 : 0, _log.Lines.Count(l => l.Contains("cache hold stops reading from the disk for this play")));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("disk reads cannot be told apart"));
    }

    [Fact]
    public void AnUnmeasuredFirstPassStopsAfterThatBatchHoldsNothingMoreAndDoesNotTellTheLoadingJudgmentAboutTheConservativeAmount()
    {
        ReadOp[] big = [.. Enumerable.Range(0, 6).Select(i => Op(10 + i, 0, 16))]; // 16 MiB 배치 여섯 개
        _hold.Measured = false;
        Prewarmer p = Make(big);
        var board = new LoadPhaseBoard(_log);
        p.LoadPhases = board;
        RunFor(20);

        p.TouchAtGameStart(Game("a"), "a.exe");

        _ = Assert.Single(_hold.Batches); // 첫 패스는 계측하지 못한 첫 묶음에서 끝 (남은 묶음을 발행하지 않음)
        Assert.Equal(0, board.ServiceReads.Total(Game("a").OriginRoot)); // 보수적으로 센 양은 로딩 판정에 넘기지 않음
        _ = Assert.Single(_log.Lines, l => l.Contains("cache hold stops reading from the disk for this play"));
        Assert.DoesNotContain(_log.Lines, l => l.Contains("cache hold continuing after the first pass"));
    }

    [Fact]
    public void WithoutALearnedGameFootprintTheFirstHoldTargetUsesAQuarterOfTheAvailableMemory()
    {
        _avail = 14000; // 몫을 알면 min(5 GiB, 7000, 11952) = 5120 MiB, 모르면 14000 / 4 = 3500 MiB
        Prewarmer p = Make(Reads);
        p.Footprint = null;
        RunFor(1);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe): 48 MiB of 3500 MiB target"));
    }
}
