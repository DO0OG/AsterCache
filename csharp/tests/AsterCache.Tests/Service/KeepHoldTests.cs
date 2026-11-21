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
    private int _opened;
    private long _avail = 20000;
    private long _reuse; // 대기 목록 재사용 누계 (조회마다 _reuseStep 만큼 늘어남, 음수면 조회 불가)
    private long _reuseStep = 1100;
    private long _trimmed; // 메모리 관리자가 줄여 간 양 (서비스 작업 집합에서 빠짐)
    private Func<long>? _availFn;
    private Action<TimeSpan, CancellationToken>? _sleep;
    private Func<IReadOnlyList<ReadOp>, TouchResult>? _touch;

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
        public int Disposed;
        public Exception? Throw;
        public Func<HoldCheckResult>? NextCheck;
        public Action? OnHold;
        private long _held;

        public long HeldBytes => _held;

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
            }

            _held += bytes;
            return new HoldBatchResult(bytes, 0, 0, 0, 0, 1, 1, 0, 0.1, 0.05, -1);
        }

        public long Release(long bytes)
        {
            long released = Math.Min(_held, bytes);
            _held -= released;
            Released.Add(released);
            return released;
        }

        public HoldCheckResult Recheck(long refillStopBytes, CancellationToken ct)
        {
            Rechecks++;
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
            Lists = () => _reuseStep < 0 ? null : new MemoryLists(0, _reuse += _reuseStep * Mib, 8000 * Mib),
            TouchRanges = (ranges, ct) =>
            {
                _touched.Add(ranges);
                Assert.True(!_serialChecked || Monitor.IsEntered(TouchSerial(_prewarmer!)), "touching must run under the serialization lock");
                return _touch?.Invoke(ranges) ?? new TouchResult(ranges.Sum(o => o.Length), 0.1, 0, 0, TouchEnd.Completed);
            },
            ListStamp = _ => "s1",
            IsSparse = path => _sparse.Contains(path),
            KeepHoldEnabled = () => _keepHold,
            OpenHold = () =>
            {
                _opened++;
                return _hold;
            },
            HoldExclusion = _ => _hasVolatileInfo ? path => _excluded.Contains(path) ? "volatile" : null : null,
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
        _avail = 4100;
        RunFor(20);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(0, _opened);
        Assert.Contains(_log.Lines, l => l.Contains("cache hold while playing skipped (a.exe: available 4100 MiB leaves no room above the floor 4096 MiB)"));
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
        // 가용 = 4196 - 붙든 MiB: 목표 100 MiB (하한 위에 남는 양), 붙든 양이 84 MiB 를 넘으면 (가용 - 16 < 하한) 다음 배치를 시작하지 않음
        ReadOp[] big = [.. Enumerable.Range(0, 20).Select(i => Op(10 + i, 0, 8))];
        Prewarmer p = Make(big);
        _availFn = () => 4196 - (_hold.HeldBytes >> 20);
        RunFor(0);

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal(6, _hold.Batches.Count);
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe): 96 MiB of 100 MiB target") && l.Contains("stopped at the memory floor (4096 MiB"));
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
        Assert.Contains(_log.Lines, l => l.Contains("cache held while playing (a.exe): 32 MiB") && l.Contains("8 MiB in volatile or changed files and 8 MiB in sparse files not held"));
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
        RunFor(5, e => _avail = e == 1 ? 3000 : 20000); // 첫 확인에서만 하한 (4096) 아래

        p.TouchAtGameStart(Game("a"), "a.exe");

        Assert.Equal([(4096 - 3000 + 256) * Mib], _hold.Released); // 부족한 양 + 여유
        Assert.Contains(_log.Lines, l => l.Contains("cache hold released 1352 MiB for memory (a.exe): available 3000 MiB < floor 4096 MiB, 2744 MiB still held"));
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
                _avail = 5000; // 하한 (4096) + 2048 아래
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

    // ---- 붙들지 않는 게임의 다시 만지기: 재사용에 맞춘 시점, 빠진 범위를 기억하고 건너뜀 ----

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
    public void TheThresholdIsHalfOfWhatIsLeftAheadOfTheListUpToOneGiBWithAFloor()
    {
        Assert.Equal(1L << 30, Prewarmer.ReuseThreshold(new MemoryLists(0, 0, 20000 * Mib), 100 * Mib)); // 앞의 여유가 크면 1 GiB
        Assert.Equal(1000 * Mib, Prewarmer.ReuseThreshold(new MemoryLists(0, 0, 3000 * Mib), 1000 * Mib)); // (3000 - 1000) / 2
        Assert.Equal(64 * Mib, Prewarmer.ReuseThreshold(new MemoryLists(0, 0, 500 * Mib), 1000 * Mib)); // 여유가 없으면 하한 64 MiB
    }

    [Fact]
    public void AMissingRangeIsRememberedSoLaterRoundsDoNotTouchItAndCostNoDiskRead()
    {
        // 파일 2 의 범위는 디스크에서 읽어야 함 (경계): 처음 발견할 때 한 번만 디스크 읽기를 일으키고 그 뒤 회차는 만지지 않음
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        _touch = ranges =>
        {
            int at = ranges.ToList().FindIndex(o => o.Path == F(2));
            return at < 0 ? new TouchResult(ranges.Sum(o => o.Length), 0.1, 0, 0, TouchEnd.Completed)
                : new TouchResult((ranges.Take(at).Sum(o => o.Length)) + Mib, 0.1, 3, 0, TouchEnd.NeedsDiskReads, 2 * Mib, 0, 0, 0, 40, StopReason.HardFaults);
        };
        RunFor(45);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.True(KeptLines() >= 3);
        Assert.Equal(2, _touched.Count(c => c.Any(o => o.Path == F(2)))); // 시작 만지기 (사다리) 의 첫 시도와 그 뒤 걷기의 발견뿐: 회차는 만지지 않음
        Assert.Contains(_log.Lines, l => l.Contains("a: cache kept while playing (a.exe)") && l.Contains("0 new missing stretches remembered (16 MiB of the 48 MiB list in all)") && l.Contains("0 MiB read from disk"));
    }

    [Fact]
    public void TheDiskReadsOfARoundAreCappedAndTheRoundSaysSo()
    {
        bool armed = false;
        _touch = ranges => armed && ranges.Any(o => o.Path != F(0))
            ? new TouchResult(Mib, 0.1, 3, 0, TouchEnd.NeedsDiskReads, 5 * Mib, 0, 0, 0, 80, StopReason.HardFaults) // 경계마다 5 MiB 를 읽음: 두 번째에서 상한 8 MiB 를 넘음
            : new TouchResult(ranges.Sum(o => o.Length), 0.1, 0, 0, TouchEnd.Completed);
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(12, e => armed = e >= 1);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Contains(_log.Lines, l => l.Contains("cache kept while playing (a.exe)") && l.Contains("10 MiB read from disk") && l.Contains("disk read cap of the round reached"));
    }

    [Fact]
    public void WhenTheDiskReadsOfThePlayReachTheCapTheKeepingEnds()
    {
        bool armed = false;
        _touch = ranges => armed ? new TouchResult(40 * Mib, 0.1, 3, 0, TouchEnd.NeedsDiskReads, 30 * Mib, 0, 0, 0, 480, StopReason.HardFaults) : new TouchResult(ranges.Sum(o => o.Length), 0.1, 0, 0, TouchEnd.Completed);
        _reuseStep = 1100;
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(200, e => armed = e >= 1);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Contains(_log.Lines, l => l.Contains("cache keeping ended after") && l.Contains("disk reads of this play reached 64 MiB"));
    }

    [Fact]
    public void WhenMostOfTheListIsRememberedAsMissingTheKeepingEnds()
    {
        bool armed = false;
        _touch = ranges => armed ? new TouchResult(Mib, 0.1, 3, 0, TouchEnd.NeedsDiskReads, Mib, 0, 0, 0, 20, StopReason.HardFaults) : new TouchResult(ranges.Sum(o => o.Length), 0.1, 0, 0, TouchEnd.Completed);
        GameSettings cheat = Cheat("a");
        Prewarmer p = Make(Reads, cheat);
        RunFor(200, e => armed = e >= 1);

        p.TouchAtGameStart(cheat, "a.exe");

        Assert.Contains(_log.Lines, l => l.Contains("cache keeping ended after") && l.Contains("most of the list is remembered as missing"));
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
}
