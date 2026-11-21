using System.Collections.Concurrent;
using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 가 게임이 실행되는 동안 유지 목록 (중요도 순서) 을 서비스의 매핑 뷰로 붙들어 두는 곳 (설정 memory.keep_hold).
/// 대기 목록을 주기마다 다시 만지는 방식 (Prewarm.PlayTouch) 은 만진 페이지가 대기 목록에 있는 동안 게임의 읽기나 다른 프로그램의 메모리 사용에 밀려나므로, 작업 집합에 올려 두고 뷰를 닫지 않아 대기 목록에서 빼 둡니다 (ViewHold).
/// 붙드는 때: 시작 만지기가 끝난 직후. 가변 파일·스파스 파일·순위 목록을 만든 뒤 바뀐 파일은 붙들지 않고, 안티치트 게이트가 고정을 막는 게임과 가변 파일 정보가 없는 게임 (원시 트레이스가 없음) 에서는 하지 않습니다 (그 게임은 지금의 다시 만지기로 남음).
/// 붙드는 동안: 몇 초마다 가용 메모리가 하한 아래인지 봐서 덜 중요한 쪽부터 내놓고, 2 분마다 붙든 범위를 다시 읽어 메모리 관리자가 줄여 간 조각을 하드 폴트로 확인합니다. 게임이 끝나면 모두 내놓고 닫습니다.
public sealed partial class Prewarmer
{
    /// 붙드는 양의 상한: 5 GiB. 근거: 2026-11-21 Call of Duty 플레이의 블록 단위 디스크 읽기 기록에서 순위 앞 2·3·4·5·6 GiB 가 경기 중 캐시 경유 읽기 지연의 32·39·42·47·49% 를 덮었고 5 GiB 를 넘기면 거의 늘지 않았습니다 (한 플레이의 근사 값).
    public const long HoldTargetCapBytes = 5L << 30;

    /// 붙든 뒤에도 가용 메모리가 남아 있어야 하는 하한의 최솟값 (MiB): 하한은 max(이 값, prewarm.min_free_ram_mib).
    public const long HoldMinFloorMib = 2048;

    /// 가용 메모리가 하한 아래인지 보는 간격 (GlobalMemoryStatusEx 한 번, 비용은 거의 없음).
    public static readonly TimeSpan HoldWatchEvery = TimeSpan.FromSeconds(5);

    /// 하한에 가깝거나 (HoldNearFloorMib 안) 붙든 지 얼마 안 됐을 (HoldEarlyWindow 안) 때의 확인 간격: 게임이 시작 뒤 수십 초 동안 10 GiB 를 잡으면 5 초에 2 GiB 가 줄 수 있어 하한에서 내놓기를 놓치지 않게 합니다.
    /// 메모리 관리자가 잠그지 않은 서비스 작업 집합을 먼저 줄여 주는지는 낮은 가용 메모리에서 확인하지 못했으므로 서비스가 직접 내놓는 쪽을 믿습니다.
    public static readonly TimeSpan HoldWatchFast = TimeSpan.FromSeconds(1);

    /// 가용 메모리가 하한 + 이 값 (MiB) 아래면 HoldWatchFast 간격으로 봅니다.
    public const long HoldNearFloorMib = 2048;

    /// 붙든 뒤 이 시간 동안은 HoldWatchFast 간격으로 봅니다 (게임이 로딩하며 메모리를 가장 많이 잡는 때).
    public static readonly TimeSpan HoldEarlyWindow = TimeSpan.FromMinutes(3);

    /// 게임 실행 여부를 보는 간격 (프로세스 스냅샷 한 번이라 HoldWatchFast 마다 보지 않음).
    public static readonly TimeSpan HoldGameCheckEvery = TimeSpan.FromSeconds(5);

    /// 붙들기에서 _touchSerial 을 놓지 않고 이어 처리하는 배치 수: 이 수만큼 읽은 뒤 (또는 속도 제한으로 쉴 때) 잠금을 놓아 다른 만지기 (다른 실행 파일의 시작 만지기) 가 끼어들 수 있게 합니다. 잠금을 다시 잡을 때마다 하드 폴트 기준값을 새로 잡습니다 (조회 2 회).
    public const int HoldLockBatches = 8;

    /// 붙들기와 같은 일을 한 번에 하는 단위: 이만큼 읽을 때마다 하드 폴트를 조회하고 (경계), 가용 메모리가 하한에 닿았는지 봅니다.
    public const long HoldBatchBytes = 16L << 20;

    /// 한 번의 붙들기 (또는 점검의 다시 채우기, 빠진 조각을 건너뛰는 다시 만지기) 에서 디스크에서 읽는 양의 상한: 32 MiB (StandbyTouch.DiskReadStopBytes 와 같은 값). 넘으면 그 회차는 거기서 끝납니다.
    public const long KeepDiskReadCapBytes = 32L << 20;

    /// 하한 아래일 때 부족한 양에 더해 내놓는 여유 (MiB): 내놓은 뒤 바로 다시 하한 아래가 되지 않게.
    public const long HoldReleaseMarginMib = 256;

    /// 붙들 목표가 이보다 작으면 붙들지 않습니다.
    public const long HoldMinTargetBytes = 64L << 20;

    private readonly ConcurrentDictionary<string, (long Held, long WsBase)> _holdHeld = new(StringComparer.OrdinalIgnoreCase); // 게임별로 지금 붙든 바이트와 붙들기 전 서비스 작업 집합
    private readonly object _touchSerial = new(); // 이 프로세스의 하드 폴트 누계로 경계를 판정하는 일 (시작 만지기, 붙들기, 점검, 다시 만지기) 이 겹치지 않게
    private readonly Dictionary<string, KeepMemory> _keepMemory = new(StringComparer.OrdinalIgnoreCase); // 게임별로 이 플레이에서 빠진 것으로 기억한 범위, _gate 로 보호

    /// 지금 붙든 양 (MiB): 플레이 기록의 가용 메모리에 더해 기록합니다 (붙들어서 줄어 보인 가용 메모리가 게임의 몫으로 배워져 다음 예열 예산이 줄지 않게).
    /// 점검 사이에 메모리 관리자가 줄여 간 페이지는 이미 대기 목록으로 가 가용에 들어 있으므로 더하면 가용을 두 번 세게 됩니다: 붙든 양을 서비스 작업 집합의 증가분 (붙들기 전 값과의 차이) 으로 제한해 줄여 간 만큼이 더해지지 않게 합니다.
    internal long KeepHeldMib
    {
        get
        {
            if (_holdHeld.IsEmpty)
            {
                return 0;
            }

            long workingSet = _env.WorkingSetBytes();
            return _holdHeld.Values.Sum(h => Math.Min(h.Held, Math.Max(0, workingSet - h.WsBase))) >> 20;
        }
    }

    private void SetHeld(string game, long held) => _holdHeld.AddOrUpdate(game, (held, _env.WorkingSetBytes()), (_, old) => (held, old.WsBase));

    private enum HoldOutcome
    {
        Retry, // 일시적인 사유로 붙들지 못함: 다음 회차에 다시 시도
        No, // 붙들지 않음 (설정·안티치트·정보 없음·메모리에 없음): 지금의 다시 만지기로 남음
        Done, // 붙들고 게임이 끝날 때까지 돌았음
    }

    private GameSettings? GameOf(string name) => _games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    private static long HoldFloorMib(GameSettings game) => Math.Max(HoldMinFloorMib, game.Prewarm.MinFreeRamMib);

    /// 붙들 목표 (바이트): 기존 예산 (가용 메모리의 PlayTouchAvailableFraction), 붙든 뒤에도 가용 메모리가 하한 위에 남는 양, HoldTargetCapBytes 가운데 가장 작은 것.
    internal static long HoldTargetBytes(long availMib, long floorMib) => Math.Min(HoldTargetCapBytes, Math.Min((long)(availMib * PlayTouchAvailableFraction) << 20, Math.Max(0, availMib - floorMib) << 20));

    // 읽는 스레드의 I/O 우선순위와 CPU 우선순위를 이 범위 안에서만 낮춥니다 (게임의 읽기와 CPU 를 다투지 않게). 스레드 배경 모드는 메모리 우선순위까지 낮추므로 쓰지 않습니다.
    private sealed class LowPriorityReader : IDisposable
    {
        private readonly PrewarmEnv _env;
        private readonly Thread _thread = Thread.CurrentThread;
        private readonly ThreadPriority _before;

        public LowPriorityReader(PrewarmEnv env)
        {
            _env = env;
            _before = _thread.Priority;
            _thread.Priority = ThreadPriority.BelowNormal;
            _ = env.SetReadIoLow(true);
        }

        public void Dispose()
        {
            _ = _env.SetReadIoLow(false);
            _thread.Priority = _before;
        }
    }

    private void NoteHoldSkip(GameSettings game, string exe, string detail, ref string? last)
    {
        if (detail != last)
        {
            _log.Info($"{game.Name}: cache hold while playing skipped ({exe}: {detail})");
            last = detail;
        }
    }

    // ---- 목록을 배치 단위로 걸으며 빠진 조각을 건너뜀 (붙들기와 지금의 다시 만지기가 함께 씀) ----

    /// 한 배치를 처리한 결과: 시작한 범위 수, 만진 바이트, 디스크에서 읽힌 바이트, 디스크 읽기가 섞여 경계로 보고 멈췄는가.
    private readonly record struct WalkStep(int Processed, long Consumed, long Disk, bool Boundary);

    private sealed record WalkResult(List<ReadOp> Kept, List<ReadOp> Missing, long Disk, int Boundaries, long SkippedBytes, long LeftBytes, bool CapReached, bool Stopped);

    // 목록을 중요한 쪽부터 HoldBatchBytes 씩 처리합니다. 경계를 만나면 그 파일의 이어지는 범위 (목록에서 바로 뒤따르는 같은 파일의 범위) 를 건너뛰고 다음 범위로 계속하며,
    // 디스크에서 읽은 양이 diskCap 을 넘으면 거기서 끝냅니다. mayContinue 가 거짓이 되면 (가용 메모리가 하한에 닿음) 멈춥니다.
    private static WalkResult Walk(IReadOnlyList<ReadOp> reads, long diskCap, CancellationToken ct, Func<bool> mayContinue, Func<IReadOnlyList<ReadOp>, WalkStep> step)
    {
        List<ReadOp> rest = [.. reads.Where(r => r.Length > 0)];
        List<ReadOp> kept = [];
        List<ReadOp> missing = []; // 경계를 만난 범위와 건너뛴 이어지는 범위: 호출한 쪽이 빠진 것으로 기억할 수 있게
        int next = 0;
        long disk = 0;
        long skipped = 0;
        int boundaries = 0;
        string? skipPath = null;
        bool capReached = false;
        bool stopped = false;
        while (next < rest.Count)
        {
            if (ct.IsCancellationRequested || !mayContinue())
            {
                stopped = true;
                break;
            }

            if (skipPath is not null)
            {
                for (; next < rest.Count && string.Equals(rest[next].Path, skipPath, StringComparison.OrdinalIgnoreCase); next++)
                {
                    skipped += rest[next].Length;
                    missing.Add(rest[next]);
                }

                skipPath = null;
                continue;
            }

            List<ReadOp> batch = TakeBatch(rest, ref next);
            WalkStep s = step(batch);
            disk += s.Disk;
            if (s.Boundary)
            {
                boundaries++;
                kept.AddRange(PrefetchStreamer.Head(batch, s.Consumed));
                int started = Math.Clamp(s.Processed, 1, batch.Count);
                List<ReadOp> tail = batch.GetRange(started, batch.Count - started);
                skipped += Math.Max(0, batch.Sum(o => o.Length) - s.Consumed - tail.Sum(o => o.Length)); // 경계를 만난 범위의 읽지 않은 나머지
                rest.InsertRange(next, tail);
                skipPath = batch[started - 1].Path;
                missing.Add(batch[started - 1]);
            }
            else
            {
                kept.AddRange(batch);
            }

            if (disk > diskCap)
            {
                capReached = true;
                break;
            }
        }

        return new WalkResult(kept, missing, disk, boundaries, skipped, rest.Skip(next).Sum(o => o.Length), capReached, stopped);
    }

    // rest[next..] 에서 HoldBatchBytes 만큼 떼어 냅니다 (걸친 범위는 잘라 나머지를 남김).
    private static List<ReadOp> TakeBatch(List<ReadOp> rest, ref int next)
    {
        List<ReadOp> batch = [];
        long room = HoldBatchBytes;
        while (next < rest.Count && room > 0)
        {
            ReadOp op = rest[next];
            if (op.Length <= room)
            {
                batch.Add(op);
                room -= op.Length;
                next++;
            }
            else
            {
                batch.Add(new ReadOp(op.Path, op.Offset, room));
                rest[next] = new ReadOp(op.Path, op.Offset + room, op.Length - room);
                room = 0;
            }
        }

        return batch;
    }

    // 만진 바이트가 consumed 일 때 시작한 범위 수 (경계가 있던 범위까지).
    private static int RangesStarted(IReadOnlyList<ReadOp> batch, long consumed)
    {
        long before = 0;
        for (int i = 0; i < batch.Count; i++)
        {
            if (before >= consumed)
            {
                return Math.Max(1, i);
            }

            before += batch[i].Length;
        }

        return batch.Count;
    }

    // ---- 두 패스 다시 만지기 (붙들지 않는 게임) ----

    /// 이 플레이에서 알게 된 범위: 빠진 것으로 확인하고 건너뛴 범위 (다음 회차부터는 만지지 않음) 와 메모리에 있다고 확인한 범위 (디스크 읽기 누적 상한 뒤에는 이것만 만짐).
    /// 같은 게임의 다른 실행 파일이 떠도 이어 쓰고, 등록 실행 파일이 모두 끝나면 버립니다. _touchSerial·_gate 아래에서만 씀.
    private sealed class KeepMemory
    {
        private readonly Dictionary<string, List<(long Start, long End)>> _missing = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<(long Start, long End)>> _resident = new(StringComparer.OrdinalIgnoreCase);

        public long MissingBytes; // 기억한 빠진 범위의 합
        public long DiskBytes; // 이 플레이에서 이 만지기가 디스크에서 읽은 양의 합
        public long ListBytes; // 만지려던 목록의 크기 (가장 큰 값)
        public bool Finished; // 빠진 범위가 대부분이라 이 플레이의 유지를 끝냈는가 (다른 실행 파일이 떠도 다시 시작하지 않음)

        public void AddMissing(ReadOp op)
        {
            MissingBytes += RangeSets.Uncovered(SetOf(_missing, op.Path), op.Offset, op.Offset + op.Length).Sum(u => u.To - u.From);
            RangeSets.Add(SetOf(_missing, op.Path), op.Offset, op.Offset + op.Length);
        }

        public void AddResident(ReadOp op) => RangeSets.Add(SetOf(_resident, op.Path), op.Offset, op.Offset + op.Length);

        /// 목록에서 기억한 빠진 범위를 뺀 나머지 (순서 그대로).
        public List<ReadOp> Without(IReadOnlyList<ReadOp> reads)
        {
            List<ReadOp> kept = [];
            foreach (ReadOp op in reads)
            {
                if (!_missing.TryGetValue(op.Path, out List<(long Start, long End)>? set))
                {
                    kept.Add(op);
                    continue;
                }

                kept.AddRange(RangeSets.Uncovered(set, op.Offset, op.Offset + op.Length).Select(u => new ReadOp(op.Path, u.From, u.To - u.From)));
            }

            return kept;
        }

        /// 목록에서 메모리에 있다고 확인한 범위만 (순서 그대로).
        public List<ReadOp> ResidentWithin(IReadOnlyList<ReadOp> reads)
        {
            List<ReadOp> within = [];
            foreach (ReadOp op in reads)
            {
                if (!_resident.TryGetValue(op.Path, out List<(long Start, long End)>? set))
                {
                    continue;
                }

                long cursor = op.Offset;
                long end = op.Offset + op.Length;
                foreach ((long from, long to) in RangeSets.Uncovered(set, op.Offset, end)) // 확인하지 못한 빈 곳 사이사이가 확인된 범위
                {
                    if (from > cursor)
                    {
                        within.Add(new ReadOp(op.Path, cursor, from - cursor));
                    }

                    cursor = to;
                }

                if (cursor < end)
                {
                    within.Add(new ReadOp(op.Path, cursor, end - cursor));
                }
            }

            return within;
        }

        private static List<(long Start, long End)> SetOf(Dictionary<string, List<(long Start, long End)>> sets, string path)
        {
            if (!sets.TryGetValue(path, out List<(long Start, long End)>? set))
            {
                sets[path] = set = [];
            }

            return set;
        }
    }

    private KeepMemory KeepMemoryOf(string game)
    {
        lock (_gate)
        {
            if (!_keepMemory.TryGetValue(game, out KeepMemory? memory))
            {
                _keepMemory[game] = memory = new KeepMemory();
            }

            return memory;
        }
    }

    // 직렬화 잠금을 잡았다 놓는 묶음: 읽는 일을 조각 묶음 (GroupBytes, HoldLockBatches) 마다 놓아 다른 만지기 (다른 실행 파일의 시작 만지기) 가 끼어들 수 있게 합니다.
    // 잠금을 잡을 때마다 StandbyTouch.Round 를 새로 시작해 이 프로세스의 하드 폴트 기준값을 새로 잡습니다 (놓은 사이 다른 스레드가 낸 읽기가 섞이지 않게).
    private sealed class SerialGroup(object gate) : IDisposable
    {
        private IDisposable? _round;
        private bool _locked;

        public int Batches { get; private set; }

        public void Enter()
        {
            if (_locked)
            {
                return;
            }

            Monitor.Enter(gate);
            (_locked, Batches) = (true, 0);
            _round = StandbyTouch.Round();
        }

        public void CountBatch() => Batches++;

        public void Exit()
        {
            if (!_locked)
            {
                return;
            }

            _round?.Dispose();
            _round = null;
            Monitor.Exit(gate);
            _locked = false;
        }

        public void Dispose() => Exit();
    }

    /// 두 패스 만지기 한 번의 합계: 메모리에 있던 바이트, 이번에 만지려던 바이트, 찾기 패스가 읽은 바이트와 순서 패스가 만진 바이트, 디스크 읽기, 새로 확인한 경계 수, 걸린 시간·CPU·뷰·조회·하드 폴트·오류,
    /// 찾기가 회차 상한에 닿아 확인하지 못하고 남긴 바이트, 새 범위를 찾지 않았는가 (플레이 상한 뒤).
    private sealed record RememberTally(long InMemory, long Total, long Found, long Ordered, long Disk, int Boundaries, double Seconds, double Cpu, int Maps, int Checks, long HardFaults, int Errors, long Left, bool CapReached, bool FindSkipped);

    // 한 배치를 순서 패스로 만집니다 (속도 제한은 호출한 쪽이 잠금 밖에서 쉼): 마지막 조회 뒤 만진 구간의 디스크 읽기도 셈.
    private TouchResult TouchBatch(IReadOnlyList<ReadOp> batch, CancellationToken ct)
    {
        TouchResult r = _env.TouchRanges(batch, ct);
        TouchResult tail = StandbyTouch.Settle();
        r = r with { DiskBytes = r.DiskBytes + tail.DiskBytes, HardFaults = r.HardFaults + tail.HardFaults, Checks = r.Checks + tail.Checks };
        return tail.End == TouchEnd.Completed || r.End != TouchEnd.Completed ? r : r with { End = tail.End, Reason = tail.Reason };
    }

    // 목록 (중요도 순서) 에서 기억한 빠진 범위를 빼고 두 패스로 만집니다.
    // 1) 찾기 패스: 가장 중요한 것부터 걸으며 메모리에 있는 범위를 모읍니다. 빠진 조각은 시간이 오래 걸린 조각의 하드 폴트 조회로 싸게 알아채 (ResidentFinder) 일정 폭만 건너뛰고 그 구간을 빠진 범위로 기억합니다. 디스크 읽기가 diskCap 에 닿으면 새 범위를 더 확인하지 않고 남은 것은 이번에 만지지 않습니다.
    //    findNew 가 아니면 (플레이 누적 상한을 넘김) 찾지 않고 지금까지 메모리에 있다고 확인한 범위만 씁니다.
    // 2) 순서 패스: 모은 범위만 덜 중요한 것부터 다시 만져 가장 중요한 것이 대기 목록의 가장 새 자리에 오게 합니다 (디스크를 읽지 않는 것이 전제: 읽기가 나면 그 범위를 빠진 것으로 기억하고 계속).
    // 두 패스 모두 _touchSerial 을 묶음마다 놓고 다시 잡으며 (SerialGroup), 속도 제한 (PlayTouchBytesPerSecond) 으로 쉴 때는 잠금 밖에서 쉽니다.
    private RememberTally TouchRemembering(IReadOnlyList<ReadOp> importanceOrdered, KeepMemory memory, bool findNew, long diskCap, CancellationToken ct)
    {
        memory.ListBytes = Math.Max(memory.ListBytes, importanceOrdered.Sum(o => o.Length));
        List<ReadOp> candidates = memory.Without(importanceOrdered);
        long total = candidates.Sum(o => o.Length);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        double cpu = 0;
        long found = 0;
        long findDisk = 0;
        long hardFaults = 0;
        int boundaries = 0;
        int maps = 0;
        int checks = 0;
        int errors = 0;
        long left = 0;
        bool capReached = false;
        using var group = new SerialGroup(_touchSerial);
        IReadOnlyList<ReadOp> resident;
        if (findNew)
        {
            group.Enter();
            FindResult f = _env.FindResident(candidates, diskCap, ct, (bytes, seconds) =>
            {
                group.Exit(); // 묶음마다 잠금을 놓아 다른 만지기가 끼어들 수 있게, 속도 제한으로 쉴 때는 잠금 밖에서
                double rest = (bytes / (double)PlayTouchBytesPerSecond) - seconds;
                if (rest > 0)
                {
                    _env.Sleep(TimeSpan.FromSeconds(rest), ct);
                }

                group.Enter();
            });
            group.Exit();
            foreach (ReadOp op in f.Missing)
            {
                memory.AddMissing(op);
            }

            foreach (ReadOp op in f.Resident)
            {
                memory.AddResident(op);
            }

            (resident, found, findDisk, boundaries, capReached, left) = (f.Resident, f.Bytes, f.DiskBytes, f.Boundaries, f.CapReached, f.LeftBytes);
            (cpu, hardFaults, maps, checks, errors) = (f.CpuSeconds, f.HardFaults, f.Maps, f.Checks, f.Errors);
        }
        else
        {
            resident = memory.ResidentWithin(candidates);
        }

        List<ReadOp> ladder = LadderOrder.Reverse(resident);
        TouchResult sum = new(0, 0, 0, 0, TouchEnd.Completed);
        WalkResult walk = Walk(ladder, KeepRoundDiskCapBytes, ct, () => true, batch =>
        {
            group.Enter();
            TouchResult r = TouchBatch(batch, ct);
            sum = new TouchResult(sum.Bytes + r.Bytes, sum.Seconds + r.Seconds, sum.SlowPieces + r.SlowPieces, sum.Errors + r.Errors, r.End, sum.DiskBytes + r.DiskBytes, sum.SkippedBytes + r.SkippedBytes,
                sum.CpuSeconds + r.CpuSeconds, sum.Maps + r.Maps, sum.HardFaults + r.HardFaults, r.Reason, sum.Checks + r.Checks, null);
            bool boundary = r.End == TouchEnd.NeedsDiskReads;
            double rest = boundary ? 0 : (r.Bytes / (double)PlayTouchBytesPerSecond) - r.Seconds;
            group.CountBatch();
            if (group.Batches >= HoldLockBatches || rest > 0)
            {
                group.Exit();
                if (rest > 0)
                {
                    _env.Sleep(TimeSpan.FromSeconds(rest), ct);
                }
            }

            long consumed = boundary ? r.Bytes + r.SkippedBytes : batch.Sum(o => o.Length);
            return new WalkStep(boundary ? RangesStarted(batch, consumed) : batch.Count, consumed, r.DiskBytes, boundary);
        });
        group.Exit();
        foreach (ReadOp op in walk.Missing)
        {
            memory.AddMissing(op); // 순서 패스에서 디스크 읽기가 난 범위: 그 사이 밀려났음
        }

        memory.DiskBytes += findDisk + walk.Disk;
        long inMemory = findNew ? Math.Max(0, found - findDisk) : Math.Max(0, sum.Bytes - sum.DiskBytes);
        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        return new RememberTally(inMemory, total, found, sum.Bytes, findDisk + walk.Disk, boundaries + walk.Boundaries, elapsed, cpu + sum.CpuSeconds, maps + sum.Maps, checks + sum.Checks, hardFaults + sum.HardFaults, errors + sum.Errors, left, capReached || walk.CapReached, !findNew);
    }

    // 게임 실행 파일이 시작할 때 사다리 순서로 만지다 경계에서 멈춘 경우: 첫 경계에서 끝내지 않고 같은 두 패스로 목록을 만집니다 (새로 안 빠진 범위는 같은 기억에 더함).
    // 다른 게임의 먼저 재사용될 양은 메모리에 있던 만큼 줄입니다. 호출하는 쪽이 _touchSerial 을 쥐지 않은 채 부릅니다 (두 패스가 묶음마다 직접 잡고 놓음).
    private void TouchAfterStartBoundary(GameSettings game, IReadOnlyList<ReadOp> readOrder, string exe, CancellationToken stop)
    {
        KeepMemory memory = KeepMemoryOf(game.Name);
        long availMib = _env.AvailableRamMib();
        IReadOnlyList<ReadOp> ordered = PrefetchStreamer.Head(_solar.ImportanceOrder(game, readOrder), (long)(Math.Max(0, availMib) * PlayTouchAvailableFraction) << 20);
        RememberTally t = TouchRemembering(ordered, memory, memory.DiskBytes < KeepPlayDiskCapBytes, KeepRoundDiskCapBytes, stop);
        if (t.Boundaries == 0 && !t.CapReached && !t.FindSkipped && !stop.IsCancellationRequested)
        {
            lock (_touchSerial)
            {
                _ = _solar.Order(game, readOrder, stop, startExe: exe); // 경계가 없음 (빠짐 표시가 낡았거나 앞의 판정이 잡음으로 멈춤): 처음부터 사다리 한 번
            }

            return;
        }

        _solar.NoteKept(game.Name, t.InMemory);
        _log.Info($"{game.Name}: cache touched at game start ({exe}) past the first missing stretch: {t.InMemory >> 20} MiB of {t.Total >> 20} MiB in memory (found {t.Found >> 20} MiB, ordered {t.Ordered >> 20} MiB), in {t.Seconds:F1} s (cpu {t.Cpu:F1} s, {t.Maps} views, {t.Checks} checks), {t.Disk >> 20} MiB read from disk ({t.HardFaults} hard faults), {t.Boundaries} missing stretches remembered ({memory.MissingBytes >> 20} MiB of the list in all){(t.Left > 0 ? $", {t.Left >> 20} MiB left unconfirmed" : string.Empty)}, left to the rewarm check");
    }

    // ---- 붙들기 ----

    // 붙들기를 시작하고 게임이 끝날 때까지 돕니다. 붙들지 못하면 사유별로 Retry (다음 회차에 다시) 나 No (지금의 다시 만지기로 남음).
    // 붙들기 중의 예외는 이 기능만 끝내고 (붙든 것은 닫음) 서비스의 다른 동작과 플레이 기록은 멈추지 않습니다.
    private HoldOutcome HoldWhilePlaying(GameSettings started, string exe, KeepList keep, ref string? lastSkip)
    {
        IKeepHold? hold = null;
        try
        {
            hold = StartHold(started, exe, keep, ref lastSkip, out HoldOutcome outcome, out long floorMib, out long targetBytes);
            if (hold is null)
            {
                return outcome;
            }

            return KeepHeld(hold, started.Name, exe, floorMib, targetBytes);
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            _log.Warn($"{started.Name}: cache hold while playing stopped: {e.GetType().Name}");
            return HoldOutcome.No; // 아래 finally 가 붙든 것을 닫음
        }
        finally
        {
            hold?.Dispose();
            _ = _holdHeld.TryRemove(started.Name, out _);
        }
    }

    private IKeepHold? StartHold(GameSettings started, string exe, KeepList keep, ref string? lastSkip, out HoldOutcome outcome, out long floorMib, out long targetBytes)
    {
        (outcome, floorMib, targetBytes) = (HoldOutcome.Retry, 0, 0);
        if (_env.OpenHold is not { } open)
        {
            outcome = HoldOutcome.No;
            return null;
        }

        if (!_solar.Enabled || Relocating(started.Name) || _solar.HasNothingToTouch(started.Name))
        {
            return null;
        }

        lock (_gate)
        {
            if (!(_last.ContainsKey(started.Name) || _warmAtServiceStart.Contains(started.Name) || _solar.HasMark(started.Name))) // 메모리에 둔 목록이 없음
            {
                return null;
            }
        }

        GameSettings game = GameOf(started.Name) ?? started;
        if (_pins.PinBlocker(game) is { } cheat)
        {
            NoteHoldSkip(game, exe, $"anti-cheat: {cheat}", ref lastSkip); // 고정이 건너뛰는 게임: 열린 뷰를 게임 실행 내내 들고 있는 일도 하지 않음
            outcome = HoldOutcome.No;
            return null;
        }

        if (_env.HoldExclusion(game) is not { } excluded)
        {
            NoteHoldSkip(game, exe, "no volatile file information yet, no trace was recorded", ref lastSkip);
            outcome = HoldOutcome.No;
            return null;
        }

        floorMib = HoldFloorMib(game);
        long availMib = _env.AvailableRamMib();
        targetBytes = HoldTargetBytes(availMib, floorMib);
        if (targetBytes < HoldMinTargetBytes)
        {
            NoteHoldSkip(game, exe, $"available {availMib} MiB leaves no room above the floor {floorMib} MiB", ref lastSkip);
            return null;
        }

        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "play hold", default, game.OriginRoot, allowWhileYielded: true, preserveGameKey: game.Name);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            NoteHoldSkip(game, exe, "disk yielded", ref lastSkip);
            return null;
        }

        lease?.NoteReadIssue("play hold", 0, false); // 양보 진단 로그에 이 단계가 보이게
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lease?.Token ?? default, _playStop.Token);
        using var low = new LowPriorityReader(_env);
        if (!RefreshKeepList(game, keep))
        {
            return null;
        }

        // 붙들 수 없는 파일을 뺍니다: 스파스 (붙들면 캐시에 있어도 디스크에서 다시 읽힘), 가변·순위 목록 뒤 바뀐 파일 (붙든 뷰가 있으면 크기 줄이기가 오류 1224 로 실패하고 캐시 없는 쓰기와 섞이면 낡은 내용이 남을 수 있음)
        var reasons = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        long sparseBytes = 0;
        long excludedBytes = 0;
        List<ReadOp> eligible = [];
        foreach (ReadOp op in keep.Ordered)
        {
            if (!reasons.TryGetValue(op.Path, out string? reason))
            {
                reasons[op.Path] = reason = _env.IsSparse(op.Path) ? "sparse" : excluded(op.Path);
            }

            if (reason is null)
            {
                eligible.Add(op);
            }
            else if (reason == "sparse")
            {
                sparseBytes += op.Length;
            }
            else
            {
                excludedBytes += op.Length;
            }
        }

        IReadOnlyList<ReadOp> reads = PrefetchStreamer.Head(eligible, targetBytes);
        long total = reads.Sum(o => o.Length);
        IKeepHold hold = open();
        _ = _holdHeld.AddOrUpdate(game.Name, (0, _env.WorkingSetBytes()), (_, _) => (0, _env.WorkingSetBytes())); // 붙들기 전의 서비스 작업 집합 (되먹임에 더하는 양의 상한)
        try
        {
            using var activity = ServiceActivity.Begin(ServiceOp.Hold); // 초 단위 기록의 동작 표시
            long seconds0 = System.Diagnostics.Stopwatch.GetTimestamp();
            double cpu = 0;
            int checks = 0;
            int errors = 0;
            long hardFaults = 0;
            long diskOnly = 0;
            long floor = floorMib;
            WalkResult walk;
            bool locked = false; // _touchSerial 을 쥐고 있는가: HoldLockBatches 배치마다, 그리고 속도 제한으로 쉬기 전에 놓아 다른 만지기가 끼어들 수 있게 함
            int inGroup = 0;
            try
            {
                walk = Walk(reads, KeepDiskReadCapBytes, linked.Token, () => _env.AvailableRamMib() - (HoldBatchBytes >> 20) >= floor, batch =>
                {
                    if (!locked)
                    {
                        Monitor.Enter(_touchSerial);
                        (locked, inGroup) = (true, 0);
                        hold.Rebase(); // 잠금을 놓은 사이 다른 스레드가 낸 읽기가 이번 판정에 섞이지 않게
                    }

                    HoldBatchResult r = hold.Hold(batch, linked.Token);
                    (cpu, checks, errors, hardFaults, diskOnly) = (cpu + r.CpuSeconds, checks + r.Checks, errors + r.Errors, hardFaults + r.HardFaults, diskOnly + r.DiskBytes);
                    double rest = r.BoundaryIndex < 0 ? r.Bytes / (double)PlayTouchBytesPerSecond - r.Seconds : 0; // 붙드는 속도 상한: 게임이 로딩하는 동안 페이지를 몰아서 옮기지 않게
                    if (++inGroup >= HoldLockBatches || rest > 0)
                    {
                        Monitor.Exit(_touchSerial);
                        locked = false;
                        if (rest > 0)
                        {
                            _env.Sleep(TimeSpan.FromSeconds(rest), linked.Token);
                        }
                    }

                    return new WalkStep(r.BoundaryIndex + 1, r.Bytes + r.SkippedBytes, r.DiskBytes, r.BoundaryIndex >= 0);
                });
            }
            finally
            {
                if (locked)
                {
                    Monitor.Exit(_touchSerial);
                }
            }

            double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(seconds0).TotalSeconds;
            if (linked.IsCancellationRequested)
            {
                hold.Dispose();
                outcome = HoldOutcome.Retry;
                return null; // 게임이 끝났거나 서비스가 멈춤: 호출한 쪽 루프가 곧 끝남
            }

            if (hold.HeldBytes <= 0)
            {
                _log.Info($"{game.Name}: cache hold while playing ({exe}): nothing of the {total >> 20} MiB list was in memory ({walk.Disk >> 20} MiB read from disk{ErrorsText(errors)}), left to the cache touching{HoldProbe(reads, total, linked.Token)}");
                hold.Dispose();
                outcome = HoldOutcome.No;
                return null;
            }

            string stopped = walk.Stopped ? $", stopped at the memory floor ({floorMib} MiB, available {_env.AvailableRamMib()} MiB)" : walk.CapReached ? ", disk read cap reached" : string.Empty;
            string missing = walk.Boundaries > 0 ? $", {walk.Boundaries} missing stretches skipped ({walk.SkippedBytes >> 20} MiB not held)" : string.Empty;
            string excludedText = sparseBytes + excludedBytes > 0 ? $", {excludedBytes >> 20} MiB in volatile or changed files and {sparseBytes >> 20} MiB in sparse files not held" : string.Empty;
            _log.Info($"{game.Name}: cache held while playing ({exe}): {hold.HeldBytes >> 20} MiB of {targetBytes >> 20} MiB target ({total >> 20} MiB list) in {hold.Files} files, {hold.Views} views, in {elapsed:F1} s (cpu {cpu:F1} s, {checks} checks), {walk.Disk >> 20} MiB read from disk ({hardFaults} hard faults){ErrorsText(errors)}{missing}{excludedText}{stopped}{(walk.Boundaries > 0 ? HoldProbe(reads, total, linked.Token) : string.Empty)}");
            SetHeld(game.Name, hold.HeldBytes);
            outcome = HoldOutcome.Done;
            return hold;
        }
        catch
        {
            hold.Dispose();
            throw;
        }
    }

    private static string ErrorsText(int errors) => errors > 0 ? $", {errors} errors" : string.Empty;

    // 표본 확인 (기록용): 메모리에 없는 조각이 있었을 때 목록 전체에서 고른 16 곳이 메모리에 있는지를 로그 꼬리 (앞에 쉼표) 로 남깁니다. 실패해도 빈 문자열.
    private string HoldProbe(IReadOnlyList<ReadOp> reads, long total, CancellationToken ct)
    {
        try
        {
            IReadOnlyList<(ReadOp Op, long Offset)> picks = KeepProbe.Pick(reads);
            return picks.Count > 0 && _env.KeepProbeSamples([.. picks.Select(p => p.Op)], ct) is { } faults ? ", " + KeepProbe.Describe(picks, faults, total) : string.Empty;
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            return string.Empty;
        }
    }

    // ---- 붙든 동안 ----

    // 붙든 뒤 게임이 끝날 때까지: 가용 메모리가 하한 아래면 내놓고 (하한에 가깝거나 붙든 지 얼마 안 됐으면 HoldWatchFast, 아니면 HoldWatchEvery 마다 봄), PlayTouchEvery 마다 붙든 범위를 점검합니다. 끝나면 (호출한 쪽 finally 가) 붙든 것을 모두 내놓고 닫습니다.
    private HoldOutcome KeepHeld(IKeepHold hold, string name, string exe, long floorMib, long targetBytes)
    {
        CancellationToken stop = _playStop.Token;
        long peak = hold.HeldBytes;
        long releasedTotal = 0;
        long droppedTotal = 0;
        long refilledTotal = 0;
        long trimmedTotal = 0;
        int checks = 0;
        string? lastSkip = null;
        string ended = string.Empty;
        bool off = false;
        TimeSpan elapsed = TimeSpan.Zero; // 붙든 뒤 쉰 시간의 합 (시험이 시계를 흉내 내지 않아도 되게 시계 대신 씀)
        TimeSpan lastGameCheck = TimeSpan.Zero;
        TimeSpan lastCheck = TimeSpan.Zero;
        long availMib = _env.AvailableRamMib();
        try
        {
            while (true)
            {
                TimeSpan interval = availMib < floorMib + HoldNearFloorMib || elapsed < HoldEarlyWindow ? HoldWatchFast : HoldWatchEvery;
                _env.Sleep(interval, stop);
                elapsed += interval;
                GameSettings? game = null;
                if (stop.IsCancellationRequested || ((elapsed - lastGameCheck >= HoldGameCheckEvery || interval >= HoldGameCheckEvery) && ((game = GameOf(name)) is null || !_env.GameRunning(game))))
                {
                    break; // 서비스가 멈추거나 설정에서 빠지거나 등록 실행 파일이 모두 끝남
                }

                if (game is not null)
                {
                    lastGameCheck = elapsed;
                }

                if (!_env.KeepHoldEnabled() || !_solar.Enabled)
                {
                    (ended, off) = (" (turned off in the settings)", true);
                    break;
                }

                availMib = _env.AvailableRamMib();
                if (availMib < floorMib)
                {
                    releasedTotal += ReleaseBelowFloor(hold, name, exe, floorMib, availMib);
                    SetHeld(name, hold.HeldBytes);
                    if (hold.HeldBytes <= 0)
                    {
                        ended = " (everything released for memory)";
                        break;
                    }
                }

                if (elapsed - lastCheck >= PlayTouchEvery && (game ?? GameOf(name)) is { } current)
                {
                    lastCheck = elapsed;
                    (long trimmed, long refilled, long dropped) = CheckHeld(hold, current, exe, ref lastSkip);
                    (trimmedTotal, refilledTotal, droppedTotal, checks) = (trimmedTotal + trimmed, refilledTotal + refilled, droppedTotal + dropped, checks + 1);
                    SetHeld(name, hold.HeldBytes);
                    if (hold.HeldBytes <= 0)
                    {
                        ended = " (nothing left held)";
                        break;
                    }
                }
            }
        }
        finally
        {
            long held = hold.HeldBytes;
            hold.Dispose(); // 덜 중요한 것부터 내놓고 뷰를 닫음
            _log.Info($"{name}: cache hold ended after {checks} checks ({exe}): {held >> 20} MiB released at the end, peak {peak >> 20} MiB of {targetBytes >> 20} MiB target, {releasedTotal >> 20} MiB released for memory, {trimmedTotal >> 20} MiB reduced by the memory manager, {refilledTotal >> 20} MiB read again from disk, {droppedTotal >> 20} MiB taken off the hold{ended}");
        }

        return off ? HoldOutcome.No : HoldOutcome.Done; // 설정이 꺼졌으면 남은 시간은 지금의 다시 만지기가 이어받음
    }

    // 가용 메모리가 하한 아래: 부족한 양 (과 약간의 여유) 만큼 덜 중요한 쪽부터 내놓습니다. 내놓은 양을 돌려줍니다.
    private long ReleaseBelowFloor(IKeepHold hold, string name, string exe, long floorMib, long availMib)
    {
        using var activity = ServiceActivity.Begin(ServiceOp.Release);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long released = hold.Release((floorMib - availMib + HoldReleaseMarginMib) << 20);
        _log.Info($"{name}: cache hold released {released >> 20} MiB for memory ({exe}): available {availMib} MiB < floor {floorMib} MiB, {hold.HeldBytes >> 20} MiB still held, in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F2} s");
        return released;
    }

    // 붙든 쪽이 작업 집합에 있는지 보고 (QueryWorkingSetEx) 메모리 관리자가 줄여 간 쪽만 다시 읽어 올립니다. 줄여 간 양, 디스크에서 다시 읽은 양, 목록에서 뺀 양을 돌려줍니다.
    private (long Trimmed, long Refilled, long Dropped) CheckHeld(IKeepHold hold, GameSettings game, string exe, ref string? lastSkip)
    {
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "play hold check", default, game.OriginRoot, allowWhileYielded: true, preserveGameKey: game.Name);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            NoteHoldSkip(game, exe, "check: disk yielded", ref lastSkip);
            return (0, 0, 0);
        }

        lease?.NoteReadIssue("play hold check", 0, false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lease?.Token ?? default, _playStop.Token);
        using var low = new LowPriorityReader(_env);
        using var activity = ServiceActivity.Begin(ServiceOp.Check);
        HoldCheckResult r;
        lock (_touchSerial)
        {
            hold.Rebase();
            r = hold.Recheck(KeepDiskReadCapBytes, linked.Token);
        }

        string cap = r.CapReached ? $", refill cap reached, {r.DroppedBytes >> 20} MiB taken off the hold" : r.DroppedBytes > 0 ? $", {r.DroppedBytes >> 20} MiB taken off the hold (unreadable)" : string.Empty;
        string changed = r.ChangedFiles.Count > 0 ? $", {r.ChangedFiles.Count} changed file(s) released ({r.ChangedFiles[0]})" : string.Empty;
        _log.Info($"{game.Name}: cache hold checked ({exe}): {r.CheckedBytes >> 20} MiB looked up in the working set in {r.Seconds:F1} s (cpu {r.CpuSeconds:F1} s, {r.Checks} checks), {r.TrimmedBytes >> 20} MiB reduced by the memory manager, {r.RefilledBytes >> 20} MiB of it read from disk again ({r.HardFaults} hard faults){cap}{changed}, {hold.HeldBytes >> 20} MiB held");
        return (r.TrimmedBytes, r.RefilledBytes, r.DroppedBytes);
    }
}
