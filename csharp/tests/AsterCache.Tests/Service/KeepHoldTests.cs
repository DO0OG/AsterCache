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
        public int Rebases;
        public int Rechecks;
        public readonly List<bool> Refills = [];
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
        private readonly Dictionary<string, long> _writtenBytes = new(StringComparer.OrdinalIgnoreCase);
        private long _held;

        public long HeldBytes => _held;

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
                if (Missing.Contains(batch[i].Path))
                {
                    bytes += Mib; // 경계를 만나기 전에 읽은 한 조각
                    _held += bytes;
                    return new HoldBatchResult(bytes, DiskPerMiss, 0, 0, 0, 0, 1, 100, 0.1, 0.05, i);
                }

                bytes += batch[i].Length;
                if (WrittenMarked.Contains(batch[i].Path))
                {
                    _writtenBytes[batch[i].Path] = _writtenBytes.GetValueOrDefault(batch[i].Path) + batch[i].Length;
                }
            }

            _held += bytes;
            return new HoldBatchResult(bytes, 0, 0, ErrorsPerBatch, 0, 1, 1, 0, 0.1, 0.05, -1, ImageBytesPerBatch, SmallBytesPerBatch);
        }

        public long Release(long bytes)
        {
            long released = Math.Min(_held, bytes);
            _held -= released;
            Released.Add(released);
            return released;
        }

        public HoldCheckResult Recheck(long refillStopBytes, CancellationToken ct, bool refill = true)
        {
            Rechecks++;
            Refills.Add(refill);
            return NextCheck?.Invoke() ?? new HoldCheckResult(_held, 0, 0, 0, false, [], 1, 0, 0.5, 0.2);
        }

        public void Dispose()
        {
            Disposed++;
            _held = 0;
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
            each?.Invoke(elapsed);
            if (elapsed > seconds)
            {
                _ = _running.Remove("a");
            }
        };
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

        Assert.Equal([(F(1), 0), (F(2), 0), (F(3), 0), (F(4), 0), (F(5), 0)], Flat(_hold.Batches)); // 파일 2 의 두 번째 범위는 건너뛰고 뒤의 파일 3~5 를 계속 붙듦
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe): 33 MiB of 5120 MiB target") && l.Contains("1 missing stretches skipped (15 MiB not held)")); // 읽지 않은 나머지 7 MiB + 건너뛴 두 번째 범위 8 MiB
    }

    [Fact]
    public void TheDiskReadCapEndsTheRoundAndTheRestOfTheListIsNotHeld()
    {
        _hold.Missing.Add(F(2));
        _hold.Missing.Add(F(4));
        _hold.DiskPerMiss = 20 * Mib; // 두 번째 경계에서 40 MiB 가 읽혀 상한 32 MiB 를 넘음
        Prewarmer p = Make(Reads);
        RunFor(1);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.DoesNotContain(_hold.Batches.SelectMany(b => b), o => o.Path == F(5));
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe)") && l.Contains("disk read cap reached"));
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

    // ---- 하한·내놓기·점검 ----

    [Fact]
    public void BelowTheFloorTheLeastImportantAreReleasedByTheMissingAmountAndNeverHeldAgain()
    {
        ReadOp[] gib = [.. Enumerable.Range(0, 4).Select(i => Op(20 + i, 0, 1024))];
        Prewarmer p = Make(gib);
        RunFor(5, e => _avail = e == 1 ? 1500 : 20000); // 첫 확인에서만 하한 (2048) 아래

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal([(2048 - 1500 + 256) * Mib], _hold.Released); // 부족한 양 + 여유
        Assert.Contains(_log.Lines, l => l.Contains("cache hold released 804 MiB for memory (a.exe): available 1500 MiB < floor 2048 MiB, 3292 MiB still held"));
        Assert.Equal(256, _hold.Batches.Count); // 내놓은 뒤에는 다시 붙들지 않음: 붙들기 호출은 처음 한 번뿐
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
        Assert.Contains("cache hold ended after 2 checks", _log.Lines.Last());
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
        Assert.Contains("1 game-written files (8 MiB) released after the game changed them", _log.Lines.Last());
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
        Assert.Contains("(nothing left held)", _log.Lines.Last());
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
}
