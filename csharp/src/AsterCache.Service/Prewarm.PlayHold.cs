using System.Collections.Concurrent;
using AsterCache.Core.Discovery;
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

    /// 붙들기 전용 하한 (MiB): 2 GiB (예열의 prewarm.min_free_ram_mib 와 분리). 가용 메모리에는 즉시 재사용할 수 있는 대기 목록이 들어 있고, 붙든 페이지는 잠그지 않아 Windows 가 필요할 때 줄일 수 있으며,
    /// 하한 감시가 1~5 초 주기로 덜 중요한 쪽부터 내놓으므로 4 GiB 처럼 높게 잡을 까닭이 없습니다 (이 PC 의 플레이 기록 둘의 최소 가용 3.9~5.7 GiB 에서 4 GiB 를 하한으로 하면 붙든 것을 거의 다 내놓게 됨).
    public const long HoldMinFloorMib = 2048;

    /// 점검이 줄여 간 쪽을 다시 읽어 올리려면 가용 메모리가 하한 + 이 값 (MiB) 이상이어야 합니다: 그렇지 않으면 다시 채우지 않고 그 구간을 붙든 목록에서 뺍니다 (메모리 관리자와 2 분 주기로 주고받지 않게).
    public const long HoldRefillMarginMib = 1024;

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

    /// 게임이 쓰는 파일을 붙든 동안 그 파일의 크기·수정 시각·생성 시각이 바뀌었는지 보는 간격 (하한 감시의 느린 간격과 같음). 파일을 열지 않는 속성 조회 한 번이라 비용은 거의 없고, 바뀌면 그 파일의 뷰만 바로 닫습니다.
    /// 5 초인 근거: 붙든 뷰가 있는 파일에서 막히는 것은 크기를 줄이는 쓰기뿐이고 (오류 1224), 게임이 덧붙이기·덮어쓰기를 한 뒤 줄이기까지 이 시간 안에 닫아야 실패 위험 창이 짧습니다. 조건을 통과한 파일은 줄어든 적이 없으므로 위험은 그 첫 쓰기 뒤에만 생깁니다.
    public static readonly TimeSpan HoldWrittenCheckEvery = TimeSpan.FromSeconds(5);

    // 게임이 쓰는 파일을 붙들지 않은 사유 (PrewarmEnv.HoldRules 가 돌려주고 시작 줄이 사유별로 바이트를 남김).
    internal const string HoldWrittenNoHistory = "written file: not enough size history";
    internal const string HoldWrittenShrank = "written file: shrank before";
    internal const string HoldWrittenUpdated = "written file: game updated or update unknown";

    // 캐시 없는 읽기가 많아 붙들면 손해가 큰 파일을 붙들지 않은 사유 (시작 줄이 바이트를 남김).
    internal const string HoldNoCacheRead = "read without the cache";

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

    /// 다시 붙들기 (하한 때문에 내놓은 것을 되붙듦) 와 스파스 블록 유지 (Prewarm.PlaySparse) 가 한 플레이에서 경기 중 디스크로 다시 읽는 양의 합의 상한: 1 GiB. 다 쓰면 그 플레이에서는 캐시에 남아 있는 부분만 붙들고 (스파스 유지는 읽고) 디스크 읽기가 필요한 배치 (블록) 에서 멈춥니다.
    /// 둘을 합쳐 세는 근거: 두 일 모두 게임이 도는 중에 밀려난 블록을 디스크에서 되읽어 게임의 읽기와 같은 디스크를 쓰므로, 게임에 주는 영향의 한도는 읽는 일 하나하나가 아니라 합이어야 합니다 (스파스 블록은 예열 예산 안의 몇백 MiB 라 합쳐도 서로를 굶기지 않음).
    /// 근거 (설계 3.2): 다시 붙들기의 디스크 읽기는 회차 상한 (KeepDiskReadCapBytes) 만 있으면 경기 내내 되풀이될 수 있어 플레이 합계에도 상한을 둡니다. 1 GiB 는 처음 붙들기 목표의 5분의 1 이고 실측으로 고른 값이 아닙니다.
    public const long HoldAgainDiskCapBytes = 1L << 30;

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

    /// 붙들 목표 (바이트): 기존 예산 (가용 메모리의 PlayTouchAvailableFraction), 붙든 뒤에도 가용 메모리가 하한 위에 남는 양, HoldTargetCapBytes 가운데 가장 작은 것.
    internal static long HoldTargetBytes(long availMib, long floorMib) => Math.Min(HoldTargetCapBytes, Math.Min((long)(availMib * PlayTouchAvailableFraction) << 20, Math.Max(0, availMib - floorMib) << 20));

    /// 다시 붙들 목표 (바이트): 처음 붙들기와 같은 식이되 하한에 점검의 여유 (HoldRefillMarginMib) 를 더해, 다시 붙든 뒤에도 가용 메모리가 하한 + 1 GiB 이상 남게 합니다 (내놓기는 하한 아래에서 일어나므로 1 GiB 안의 흔들림에서는 내놓기와 번갈아 되풀이되지 않고, 그보다 크게 흔들리면 2분 점검마다 한 번까지 되풀이될 수 있음).
    internal static long HoldAgainTargetBytes(long availMib, long floorMib) => HoldTargetBytes(availMib, floorMib + HoldRefillMarginMib);

    // 다시 붙들기를 지금 하지 않을 사유 (없으면 null): 로딩 판정이 로딩 (실행 파일 시작·신호 모두) 이거나, 게임 드라이브의 디스크 대기열이 0 이 아니거나 모름. 판정이 없는 환경 (시험·기록 없음) 은 막지 않습니다.
    private string? HoldAgainBlocker(GameSettings game) => LoadPhases is not { } board ? null
        : board.StateOf(game.Name) == LoadState.Loading ? "loading phase"
        : board.QueueOf(game.Name) is not 0 ? "game drive queue is not 0 or unknown"
        : null;

    private const string HoldAgainNoRoom = "available memory leaves no room above the floor and the margin";

    // 다시 붙들 때 한 회차의 디스크 읽기 상한: 회차 상한과 이 플레이의 남은 합계 가운데 작은 값 (0 이면 디스크 읽기가 필요한 배치에서 멈춤).
    private static long HoldAgainRoundCap(KeepMemory memory) => Math.Min(KeepDiskReadCapBytes, Math.Max(0, HoldAgainDiskCapBytes - memory.HoldAgainDiskBytes));

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

    private sealed record WalkResult(List<ReadOp> Kept, List<ReadOp> Missing, List<ReadOp> Hit, long Disk, int Boundaries, long SkippedBytes, long LeftBytes, bool CapReached, bool Stopped);

    // 목록을 중요한 쪽부터 HoldBatchBytes 씩 처리합니다. 경계를 만나면 (skipSameFile 이면) 그 파일의 이어지는 범위 (목록에서 바로 뒤따르는 같은 파일의 범위) 를 건너뛰고 다음 범위로 계속하며,
    // 디스크에서 읽은 양이 diskCap 을 넘으면 거기서 끝냅니다. mayContinue 가 거짓이 되면 (가용 메모리가 하한에 닿음) 멈춥니다.
    private static WalkResult Walk(IReadOnlyList<ReadOp> reads, long diskCap, CancellationToken ct, Func<bool> mayContinue, Func<IReadOnlyList<ReadOp>, WalkStep> step, bool skipSameFile = true)
    {
        List<ReadOp> rest = [.. reads.Where(r => r.Length > 0)];
        List<ReadOp> kept = [];
        List<ReadOp> missing = []; // 경계를 만난 범위와 건너뛴 이어지는 범위: 호출한 쪽이 빠진 것으로 기억할 수 있게
        List<ReadOp> hit = []; // 그 가운데 경계를 만난 (메모리에 없다고 확인한) 범위만
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
                skipPath = skipSameFile ? batch[started - 1].Path : null; // 같은 파일의 이어지는 범위를 건너뛰는 것은 붙들기만 (다시 만지기는 경계가 난 범위 하나만 뺌)
                missing.Add(batch[started - 1]);
                hit.Add(batch[started - 1]);
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

        return new WalkResult(kept, missing, hit, disk, boundaries, skipped, rest.Skip(next).Sum(o => o.Length), capReached, stopped);
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

    /// 이 플레이에서 알게 된 범위: 메모리에 있다고 확인한 집합 (회차는 이 집합만 만짐), 빠진 것으로 확인한 구멍 (다시 만지지 않음, 이 플레이 동안), 집합 밖 다시 확인의 디스크 읽기 누적.
    /// 같은 게임의 다른 실행 파일이 떠도 이어 쓰고 등록 실행 파일이 모두 끝나면 버립니다. 시작 만지기와 유지 회차가 같은 객체를 동시에 고칠 수 있으므로 읽고 쓰는 모든 경로가 자기 잠금 안에서 돕니다.
    private sealed class KeepMemory
    {
        private readonly object _lock = new();
        private readonly Dictionary<string, List<(long Start, long End)>> _missing = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<(long Start, long End)>> _resident = new(StringComparer.OrdinalIgnoreCase);
        private long _missingBytes;
        private long _diskBytes;
        private long _listBytes;
        private long _holdAgainDiskBytes;
        private bool _finished;
        private bool _holdReleased;
        private bool _holdWaiting;
        private string? _holdRequest;

        /// 기억한 빠진 구멍의 합.
        public long MissingBytes
        {
            get
            {
                lock (_lock)
                {
                    return _missingBytes;
                }
            }
        }

        /// 이 플레이에서 이 만지기가 디스크에서 읽은 양의 합.
        public long DiskBytes
        {
            get
            {
                lock (_lock)
                {
                    return _diskBytes;
                }
            }
        }

        /// 만지려던 목록의 크기 (가장 큰 값).
        public long ListBytes
        {
            get
            {
                lock (_lock)
                {
                    return _listBytes;
                }
            }
        }

        /// 대부분이 빠져 이 플레이의 유지를 끝냈는가 (다른 실행 파일이 떠도 다시 시작하지 않음).
        public bool Finished
        {
            get
            {
                lock (_lock)
                {
                    return _finished;
                }
            }

            set
            {
                lock (_lock)
                {
                    _finished = value;
                }
            }
        }

        /// 이 플레이에서 하한 때문에 붙든 것을 모두 내놓은 적이 있는가: 그 뒤의 붙들기는 다시 붙들기 조건을 따릅니다.
        public bool HoldReleased
        {
            get
            {
                lock (_lock)
                {
                    return _holdReleased;
                }
            }

            set
            {
                lock (_lock)
                {
                    _holdReleased = value;
                }
            }
        }

        /// 붙들기가 하한 때문에 모두 내놓고 끝나 유지 루프가 다시 붙들기를 기다리는 중인가 (붙들기가 도는 동안은 거짓).
        public bool HoldWaiting
        {
            get
            {
                lock (_lock)
                {
                    return _holdWaiting;
                }
            }

            set
            {
                lock (_lock)
                {
                    _holdWaiting = value;
                }
            }
        }

        /// 기다리는 중에 같은 게임의 다른 실행 파일이 떴음을 알립니다 (붙든 것이 남아 있거나 하한 때문에 내놓은 적이 없으면 아무것도 안 함: 그때는 main 처럼 새 시작이 아무것도 하지 않음).
        public void RequestHold(string exe)
        {
            lock (_lock)
            {
                if (_holdWaiting && _holdReleased)
                {
                    _holdRequest = exe;
                }
            }
        }

        /// 알린 실행 파일 이름을 한 번만 돌려줍니다 (없으면 null).
        public string? TakeHoldRequest()
        {
            lock (_lock)
            {
                string? exe = _holdRequest;
                _holdRequest = null;
                return exe;
            }
        }

        /// 다시 붙들기와 스파스 블록 유지가 이 플레이에서 디스크에서 읽은 양의 합.
        public long HoldAgainDiskBytes
        {
            get
            {
                lock (_lock)
                {
                    return _holdAgainDiskBytes;
                }
            }
        }

        public void AddHoldAgainDisk(long bytes)
        {
            lock (_lock)
            {
                _holdAgainDiskBytes += bytes;
            }
        }

        public void AddDisk(long bytes)
        {
            lock (_lock)
            {
                _diskBytes += bytes;
            }
        }

        public void NoteListBytes(long bytes)
        {
            lock (_lock)
            {
                _listBytes = Math.Max(_listBytes, bytes);
            }
        }

        /// 집합의 크기 (메모리에 있다고 확인한 범위의 합).
        public long ResidentBytes()
        {
            lock (_lock)
            {
                return _resident.Values.Sum(set => set.Sum(r => r.End - r.Start));
            }
        }

        /// 빠진 것으로 확인한 구멍을 더합니다 (집합에서도 뺌).
        public void AddMissing(ReadOp op)
        {
            lock (_lock)
            {
                _missingBytes += RangeSets.Uncovered(SetOf(_missing, op.Path), op.Offset, op.Offset + op.Length).Sum(u => u.To - u.From);
                RangeSets.Add(SetOf(_missing, op.Path), op.Offset, op.Offset + op.Length);
                RangeSets.Remove(SetOf(_resident, op.Path), op.Offset, op.Offset + op.Length);
            }
        }

        public void AddResident(ReadOp op)
        {
            lock (_lock)
            {
                RangeSets.Add(SetOf(_resident, op.Path), op.Offset, op.Offset + op.Length);
            }
        }

        /// old 범위들을 집합에서 빼고 now 범위들을 더합니다 (보호 패스가 확인한 범위로 집합을 바꿈: 확인하지 못한 범위는 집합에서 빠져 집합 밖이 됨).
        public void Replace(IReadOnlyList<ReadOp> old, IReadOnlyList<ReadOp> now)
        {
            lock (_lock)
            {
                foreach (ReadOp op in old)
                {
                    RangeSets.Remove(SetOf(_resident, op.Path), op.Offset, op.Offset + op.Length);
                }

                foreach (ReadOp op in now)
                {
                    RangeSets.Add(SetOf(_resident, op.Path), op.Offset, op.Offset + op.Length);
                }
            }
        }

        /// 집합에서 뺍니다 (구멍으로 기억하지는 않음: 다음에 집합 밖을 다시 확인할 때 메모리에 다시 올라와 있으면 돌아옴).
        public void RemoveResident(ReadOp op)
        {
            lock (_lock)
            {
                RangeSets.Remove(SetOf(_resident, op.Path), op.Offset, op.Offset + op.Length);
            }
        }

        /// 목록에서 기억한 구멍을 뺀 나머지 (순서 그대로).
        public List<ReadOp> Without(IReadOnlyList<ReadOp> reads)
        {
            lock (_lock)
            {
                return Subtract(reads, _missing);
            }
        }

        /// 목록에서 구멍과 집합을 뺀 나머지 (집합 밖: 아직 확인하지 않았거나 경계 뒤로 남은 범위, 순서 그대로).
        public List<ReadOp> Unconfirmed(IReadOnlyList<ReadOp> reads)
        {
            lock (_lock)
            {
                return Subtract([.. Subtract(reads, _missing)], _resident);
            }
        }

        /// 목록에서 메모리에 있다고 확인한 범위만 (순서 그대로).
        public List<ReadOp> ResidentWithin(IReadOnlyList<ReadOp> reads)
        {
            lock (_lock)
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
        }

        private static List<ReadOp> Subtract(IReadOnlyList<ReadOp> reads, Dictionary<string, List<(long Start, long End)>> sets)
        {
            List<ReadOp> kept = [];
            foreach (ReadOp op in reads)
            {
                if (!sets.TryGetValue(op.Path, out List<(long Start, long End)>? set))
                {
                    kept.Add(op);
                    continue;
                }

                kept.AddRange(RangeSets.Uncovered(set, op.Offset, op.Offset + op.Length).Select(u => new ReadOp(op.Path, u.From, u.To - u.From)));
            }

            return kept;
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

        /// 잠금을 놓고 아주 짧게 쉽니다: 잠금 (Monitor) 은 공정하지 않아 놓자마자 다시 잡으면 기다리던 스레드 (다른 실행 파일의 시작 만지기) 가 차례를 못 얻으므로, 놓은 뒤 한 번 양보해 대기자가 잡게 합니다. 다시 잡는 것은 Enter 가 함.
        public void Release()
        {
            if (!_locked)
            {
                return;
            }

            Exit();
            Thread.Sleep(1);
        }

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

    /// 두 패스 만지기 한 번의 합계: 메모리에 있던 바이트, 이번에 만지려던 바이트 (구멍을 뺀 목록), 찾기 패스가 읽은 바이트와 순서 패스가 만진 바이트, 디스크 읽기, 새로 확인한 경계 수, 걸린 시간·CPU·뷰·조회·하드 폴트·오류,
    /// 찾기가 확인하지 않고 남긴 바이트, 찾기가 회차 상한에 닿았는가, 새 범위를 찾지 않았는가 (플레이 상한 뒤).
    /// 집합 크기 (회차 뒤), 경계 때문에 뺀 양, 이번 회차에 구멍으로 뺀 양, 순서 패스와 찾기 패스의 시간·CPU 는 로그용입니다.
    private sealed record RememberTally(long InMemory, long Total, long Found, long Ordered, long Disk, int Boundaries, double Seconds, double Cpu, int Maps, int Checks, long HardFaults, int Errors, long Left, bool CapReached, bool FindSkipped,
        long SetBytes = 0, long BoundaryCut = 0, long Holes = 0, double OrderSeconds = 0, double OrderCpu = 0, double FindSeconds = 0, double FindCpu = 0, bool OrderCapReached = false);

    /// 순서 패스 하나의 결과: 만진 바이트, 디스크 읽기, 경계 수, 걸린 시간·CPU, 뷰·조회·하드 폴트·오류.
    private readonly record struct PassTally(long Bytes, long Disk, int Boundaries, double Seconds, double Cpu, int Maps, int Checks, long HardFaults, int Errors, bool CapReached = false);

    // 한 배치를 순서 패스로 만집니다: 마지막 조회 뒤 만진 구간의 디스크 읽기도 셈.
    private TouchResult TouchBatch(IReadOnlyList<ReadOp> batch, CancellationToken ct)
    {
        TouchResult r = _env.TouchRanges(batch, ct);
        TouchResult tail = StandbyTouch.Settle();
        r = r with { DiskBytes = r.DiskBytes + tail.DiskBytes, HardFaults = r.HardFaults + tail.HardFaults, Checks = r.Checks + tail.Checks };
        return tail.End == TouchEnd.Completed || r.End != TouchEnd.Completed ? r : r with { End = tail.End, Reason = tail.Reason };
    }

    // 순서 패스: ranges (중요도 순서) 를 덜 중요한 것부터 다시 만져 가장 중요한 것이 대기 목록의 가장 새 자리에 오게 합니다. 속도 제한도 쉼도 없습니다 (낮은 CPU·I/O 우선순위는 호출한 쪽이 이미 걺).
    // 직렬화 잠금은 HoldLockBatches 배치마다 놓고 한 번 양보한 뒤 다시 잡습니다 (속도 제한으로 쉬지 않음). 배치가 디스크 읽기를 만나면 (그 사이 밀려났음) 그 범위 하나만 집합에서 빼고 (구멍으로 기억하지 않고 같은 파일의 뒤 범위도 건드리지 않음) 계속합니다.
    // stopAtBoundary 이면 (경기 안 로딩 중) 읽기 상한 (KeepOrderDiskCapBytes) 에 더해 디스크 읽기가 난 (경계) 배치에서도 끝냅니다: 호출한 쪽이 이번에 메모리에 있다고 확인한 범위만 넘기므로 경계는 그 사이 밀려난 경우뿐이고, 조회 잡음 (배치마다 +1 쯤의 하드 폴트) 은 경계가 아니라 멈추지 않습니다.
    // 경계가 아니어도 읽기 합이 상한을 넘으면 끝나므로 (흩어진 빈 블록이 여러 배치에 있는 경우) 일반 회차보다 많이 읽지 않습니다.
    private PassTally OrderPass(IReadOnlyList<ReadOp> ranges, KeepMemory memory, SerialGroup group, CancellationToken ct, bool stopAtBoundary = false)
    {
        bool hitBoundary = false;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        TouchResult sum = new(0, 0, 0, 0, TouchEnd.Completed);
        WalkResult walk = Walk(LadderOrder.Reverse(ranges), KeepOrderDiskCapBytes, ct, () => !hitBoundary, batch => // 회차당 읽기 상한 (보호 패스가 이미 가장 중요한 것부터 집합 전체를 만졌으므로 여기서 끝나도 가장 중요한 쪽의 보호는 깨지지 않음)
        {
            group.Enter();
            TouchResult r = TouchBatch(batch, ct);
            sum = new TouchResult(sum.Bytes + r.Bytes, sum.Seconds + r.Seconds, sum.SlowPieces + r.SlowPieces, sum.Errors + r.Errors, r.End, sum.DiskBytes + r.DiskBytes, sum.SkippedBytes + r.SkippedBytes,
                sum.CpuSeconds + r.CpuSeconds, sum.Maps + r.Maps, sum.HardFaults + r.HardFaults, r.Reason, sum.Checks + r.Checks, null);
            bool boundary = r.End == TouchEnd.NeedsDiskReads;
            hitBoundary |= boundary && stopAtBoundary;
            group.CountBatch();
            if (group.Batches >= HoldLockBatches)
            {
                group.Release();
            }

            long consumed = boundary ? r.Bytes + r.SkippedBytes : batch.Sum(o => o.Length);
            return new WalkStep(boundary ? RangesStarted(batch, consumed) : batch.Count, consumed, r.DiskBytes, boundary);
        }, skipSameFile: false);
        group.Exit();
        foreach (ReadOp op in walk.Missing)
        {
            memory.RemoveResident(op); // 그 사이 밀려난 범위 하나: 집합에서만 뺌
        }

        return new PassTally(sum.Bytes, walk.Disk, walk.Boundaries, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds, sum.CpuSeconds, sum.Maps, sum.Checks, sum.HardFaults, sum.Errors, walk.CapReached);
    }

    // 목록 (중요도 순서) 에서 기억한 구멍을 빼고 만집니다. 회차는 집합 P (메모리에 있다고 확인한 범위, 중요도 순서) 가 비어 있지 않으면 이 순서로 돕니다.
    // 1) 보호 패스: P 를 가장 중요한 것부터 걸으며 만집니다 (ResidentFinder, 가장 중요한 쪽이 회차 시작과 함께 바로 다시 만져짐). 빠진 조각은 하드 폴트 조회로 싸게 알아채 그 조각과 뒤 8 MiB 만 P 에서 빼고 계속하고,
    //    이어진 두 지점이 빠졌으면 경계로 보아 그 뒤 (덜 중요한 쪽) 를 모두 P 에서 빼고 끝냅니다. 이 패스의 디스크 읽기가 KeepProtectDiskCapBytes 를 넘어도 같습니다 (그 지점을 경계로 봄).
    // 2) 순서 패스: 보호 패스 뒤 남은 P 만 덜 중요한 것부터 가장 중요한 것까지 다시 만져 가장 중요한 것이 대기 목록의 가장 새 자리에 오게 합니다. 읽기가 나면 그 범위만 P 에서 빼고 계속하되, 읽기가 회차당 KeepOrderDiskCapBytes 를 넘으면 거기서 끝납니다 (보호 패스가 먼저 돌았으므로 중단해도 가장 중요한 쪽의 보호는 깨지지 않음).
    // 3) 집합 밖 다시 확인 (findNew, 곧 플레이 누적 상한 안일 때만): 1)·2) 가 끝난 뒤에, P 에 없는 범위 (아직 확인하지 않았거나 경계·건너뛴 구간) 를 중요한 순서로 조금 확인해 메모리에 있으면 P 에 넣습니다.
    //    디스크 읽기는 회차 diskCap (P 가 비어 있으면 KeepEmptySetRoundDiskCapBytes), 플레이 KeepPlayDiskCapBytes 안이고 플레이 누적에는 이 읽기만 셉니다. 한 지점만 빠졌으면 그 조각을 구멍으로 기억하고 계속, 이어진 두 지점이면 경계로 보아 그 뒤는 이번에 확인하지 않습니다.
    // P 가 비어 있던 회차 (시작 만지기 뒤 첫 회차) 는 3) 만 돌고, 찾기가 모은 범위를 한 번 더 순서 패스로 만져 순서를 바로잡습니다 (방금 확인한 범위라 읽기가 없어야 함).
    // loading (경기 안 로딩, 실행 파일 시작 없이 신호로 잡힌 로딩) 이면 디스크 읽기를 줄입니다: 보호 패스와 집합이 비었을 때의 찾기 패스는 상한 KeepLoadingProtectDiskCapBytes, 집합이 있으면 집합 밖 찾기는 하지 않고,
    // 보호 패스가 상한이나 경계에서 멈춰도 확인하지 못한 범위를 집합에서 빼지 않고 (빠진 것으로 확인한 조각만 뺌), 순서 패스는 이번에 확인한 범위에만 돌아 경계 배치나 읽기 상한 (KeepOrderDiskCapBytes) 에서 끝납니다. 메모리에 있는 범위를 다시 만지는 일은 그대로 돕니다.
    private RememberTally TouchRemembering(IReadOnlyList<ReadOp> importanceOrdered, KeepMemory memory, bool findNew, long diskCap, CancellationToken ct, bool loading = false)
    {
        memory.NoteListBytes(importanceOrdered.Sum(o => o.Length));
        List<ReadOp> candidates = memory.Without(importanceOrdered);
        long total = candidates.Sum(o => o.Length);
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        List<ReadOp> set = memory.ResidentWithin(candidates);
        using var group = new SerialGroup(_touchSerial);
        FindResult? protect = null;
        PassTally order = default;
        if (set.Count > 0)
        {
            group.Enter();
            protect = _env.FindResident(set, loading ? KeepLoadingProtectDiskCapBytes : KeepProtectDiskCapBytes, ct, (bytes, seconds) =>
            {
                group.Release(); // 묶음마다 잠금을 놓고 한 번 양보해 다른 만지기가 끼어들 수 있게 (쉬는 것은 아님)
                group.Enter();
            });
            group.Exit();
            if (!loading)
            {
                memory.Replace(set, protect.Resident); // 확인한 범위만 P 에 남음: 빠진 조각·건너뛴 구간·경계 뒤는 P 에서 빠져 집합 밖이 됨
            }
            else
            {
                foreach (ReadOp op in protect.Missing)
                {
                    memory.RemoveResident(op); // 빠진 것으로 확인한 조각만 뺌: 상한 때문에 확인하지 못한 범위는 집합에 그대로 둠
                }
            }

            order = OrderPass(loading ? protect.Resident : memory.ResidentWithin(candidates), memory, group, ct, loading);
        }

        FindResult? f = null;
        PassTally fresh = default;
        long holes = 0;
        List<ReadOp> outside = findNew && (!loading || set.Count == 0) && !ct.IsCancellationRequested ? memory.Unconfirmed(candidates) : []; // 경기 안 로딩 중에는 집합이 비었을 때만 찾음 (그래야 시작 직후 첫 회차가 집합을 만듦)
        if (outside.Count > 0)
        {
            group.Enter();
            f = _env.FindResident(outside, loading ? KeepLoadingProtectDiskCapBytes : set.Count == 0 ? KeepEmptySetRoundDiskCapBytes : diskCap, ct, (bytes, seconds) =>
            {
                group.Release();
                group.Enter();
            });
            group.Exit();
            foreach (ReadOp op in f.Missing)
            {
                memory.AddMissing(op);
                holes += op.Length;
            }

            foreach (ReadOp op in f.Resident)
            {
                memory.AddResident(op);
            }

            if (set.Count == 0 && f.Resident.Count > 0)
            {
                fresh = OrderPass(f.Resident, memory, group, ct, loading);
            }
        }

        memory.AddDisk(f?.DiskBytes ?? 0); // 플레이 누적 상한에는 집합 밖 다시 확인의 읽기만 셈 (보호·순서 패스는 각자의 방어가 있음)
        long found = (protect?.Bytes ?? 0) + (f?.Bytes ?? 0);
        long foundDisk = (protect?.DiskBytes ?? 0) + (f?.DiskBytes ?? 0);
        long inMemory = Math.Max(0, found - foundDisk);
        long disk = foundDisk + order.Disk + fresh.Disk;
        long boundaryCut = (protect?.LeftBytes ?? 0) + (f is { BoundaryHit: true } ? f.LeftBytes : 0);
        double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
        return new RememberTally(inMemory, total, found, order.Bytes + fresh.Bytes, disk, (protect?.Boundaries ?? 0) + (f?.Boundaries ?? 0) + order.Boundaries + fresh.Boundaries, elapsed,
            order.Cpu + fresh.Cpu + (protect?.CpuSeconds ?? 0) + (f?.CpuSeconds ?? 0), order.Maps + fresh.Maps + (protect?.Maps ?? 0) + (f?.Maps ?? 0), order.Checks + fresh.Checks + (protect?.Checks ?? 0) + (f?.Checks ?? 0),
            order.HardFaults + fresh.HardFaults + (protect?.HardFaults ?? 0) + (f?.HardFaults ?? 0), order.Errors + fresh.Errors + (protect?.Errors ?? 0) + (f?.Errors ?? 0), f?.LeftBytes ?? 0, f?.CapReached ?? false, !findNew,
            memory.ResidentBytes(), boundaryCut, holes, order.Seconds + fresh.Seconds, order.Cpu + fresh.Cpu, (protect?.Seconds ?? 0) + (f?.Seconds ?? 0), (protect?.CpuSeconds ?? 0) + (f?.CpuSeconds ?? 0), order.CapReached || fresh.CapReached);
    }

    // 로그에 붙는 회차 설명: 집합 크기, 경계 때문에 뺀 양, 이번에 구멍으로 뺀 양, 두 패스의 시간과 CPU.
    private static string TallyDetail(RememberTally t) =>
        $", set {t.SetBytes >> 20} MiB, {t.BoundaryCut >> 20} MiB cut off by a boundary, {t.Holes >> 20} MiB left out as holes, order pass {t.OrderSeconds:F1} s (cpu {t.OrderCpu:F1} s), find pass {t.FindSeconds:F1} s (cpu {t.FindCpu:F1} s){(t.OrderCapReached ? ", order pass stopped at the disk read cap" : string.Empty)}";

    // 게임 실행 파일이 시작할 때 사다리 순서로 만지다 경계에서 멈춘 경우: 첫 경계에서 끝내지 않고 같은 두 패스로 목록을 만집니다 (새로 안 빠진 범위는 같은 기억에 더함).
    // 다른 게임의 먼저 재사용될 양은 메모리에 있던 만큼 줄입니다. 호출하는 쪽이 _touchSerial 을 쥐지 않은 채 부릅니다 (두 패스가 묶음마다 직접 잡고 놓음).
    // findNew 가 false 이면 (같은 게임의 다른 프로세스가 이미 실행 중) 집합 밖 범위를 디스크에서 확인하지 않고 이미 메모리에 있다고 아는 범위만 만집니다.
    private void TouchAfterStartBoundary(GameSettings game, IReadOnlyList<ReadOp> readOrder, string exe, CancellationToken stop, bool findNew = true)
    {
        KeepMemory memory = KeepMemoryOf(game.Name);
        long availMib = _env.AvailableRamMib();
        IReadOnlyList<ReadOp> ordered = PrefetchStreamer.Head(_solar.ImportanceOrder(game, readOrder), (long)(Math.Max(0, availMib) * PlayTouchAvailableFraction) << 20);
        RememberTally t = TouchRemembering(ordered, memory, findNew && memory.DiskBytes < KeepPlayDiskCapBytes, KeepRoundDiskCapBytes, stop);
        NoteServiceRead(game, t.Disk);
        if (t.Boundaries == 0 && !t.CapReached && !t.FindSkipped && !stop.IsCancellationRequested)
        {
            lock (_touchSerial)
            {
                _ = _solar.Order(game, readOrder, stop, startExe: exe); // 경계가 없음 (빠짐 표시가 낡았거나 앞의 판정이 잡음으로 멈춤): 처음부터 사다리 한 번
            }

            return;
        }

        _solar.NoteKept(game.Name, t.InMemory);
        _log.Info($"{game.Name}: cache touched at game start ({exe}) past the first missing stretch: {t.InMemory >> 20} MiB of {t.Total >> 20} MiB in memory (found {t.Found >> 20} MiB, ordered {t.Ordered >> 20} MiB), in {t.Seconds:F1} s (cpu {t.Cpu:F1} s, {t.Maps} views, {t.Checks} checks), {t.Disk >> 20} MiB read from disk ({t.HardFaults} hard faults), {t.Boundaries} missing stretches remembered ({memory.MissingBytes >> 20} MiB of the list in all){(t.Left > 0 ? $", {t.Left >> 20} MiB left unconfirmed" : string.Empty)}{TallyDetail(t)}, left to the rewarm check{(findNew ? string.Empty : ", disk reads skipped (the game is already running)")}");
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
        (string? blocker, string? cheatKind) = _pins.HoldGate(game); // 붙들기 전용 게이트: 트레이스를 금지하는 안티치트에서는 하지 않고 리코셰는 허용 (고정 게이트와 별개)
        if (blocker is { } cheat)
        {
            NoteHoldSkip(game, exe, $"anti-cheat: {cheat}", ref lastSkip); // 열린 뷰를 게임 실행 내내 들고 있는 일도 하지 않음
            outcome = HoldOutcome.No;
            return null;
        }

        if (_env.HoldExclusion(game) is not { } rules)
        {
            NoteHoldSkip(game, exe, "no volatile file information yet, no trace was recorded", ref lastSkip);
            outcome = HoldOutcome.No;
            return null;
        }

        Func<string, string?> excluded = rules.Reason;
        floorMib = HoldMinFloorMib;
        long availMib = _env.AvailableRamMib();
        KeepMemory memory = KeepMemoryOf(game.Name);
        bool again = memory.HoldReleased; // 하한 때문에 모두 내놓은 뒤의 붙들기 (2 분 재시도): 처음 붙들기와 달리 다시 붙들기 조건을 따름
        if (again && HoldAgainBlocker(game) is { } blocked)
        {
            NoteHoldSkip(game, exe, $"hold again deferred: {blocked}", ref lastSkip);
            return null;
        }

        targetBytes = again ? HoldAgainTargetBytes(availMib, floorMib) : HoldTargetBytes(availMib, floorMib);
        if (targetBytes < HoldMinTargetBytes)
        {
            NoteHoldSkip(game, exe, again ? $"hold again deferred: {HoldAgainNoRoom}" : $"available {availMib} MiB leaves no room above the floor {floorMib} MiB", ref lastSkip);
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
        long cheatFileBytes = 0;
        long noCacheBytes = 0;
        long writtenNoHistory = 0;
        long writtenShrank = 0;
        long writtenUpdated = 0;
        List<ReadOp> eligible = [];
        foreach (ReadOp op in keep.Ordered)
        {
            if (!reasons.TryGetValue(op.Path, out string? reason))
            {
                reasons[op.Path] = reason = _env.IsSparse(op.Path) ? "sparse" : AntiCheat.IsProcessName(Path.GetFileName(op.Path)) ? "anti-cheat file" : excluded(op.Path); // 안티치트 구성요소 파일 (예: 리코셰 드라이버) 은 붙들지 않음
            }

            if (reason is null)
            {
                eligible.Add(op);
            }
            else if (reason == "sparse")
            {
                sparseBytes += op.Length;
            }
            else if (reason == "anti-cheat file")
            {
                cheatFileBytes += op.Length;
            }
            else if (reason == HoldNoCacheRead)
            {
                noCacheBytes += op.Length;
            }
            else if (reason == HoldWrittenNoHistory)
            {
                writtenNoHistory += op.Length;
            }
            else if (reason == HoldWrittenShrank)
            {
                writtenShrank += op.Length;
            }
            else if (reason == HoldWrittenUpdated)
            {
                writtenUpdated += op.Length;
            }
            else
            {
                excludedBytes += op.Length;
            }
        }

        string[] writtenPaths = rules.IsWritten is { } isWritten ? [.. eligible.Select(o => o.Path).Distinct(StringComparer.OrdinalIgnoreCase).Where(isWritten)] : [];

        IReadOnlyList<ReadOp> reads = PrefetchStreamer.Head(eligible, targetBytes);
        long total = reads.Sum(o => o.Length);
        IKeepHold hold = open();
        hold.MarkWritten(writtenPaths); // 게임이 쓰는 파일은 붙든 뒤 5 초마다 바뀌었는지 봄
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
            long images = 0;
            long smalls = 0;
            WalkResult walk = WalkHold(hold, reads, again ? HoldAgainRoundCap(memory) : KeepDiskReadCapBytes, floorMib, linked.Token, r =>
            {
                (images, smalls) = (images + r.ImageBytes, smalls + r.SmallBytes);
                (cpu, checks, errors, hardFaults, diskOnly) = (cpu + r.CpuSeconds, checks + r.Checks, errors + r.Errors, hardFaults + r.HardFaults, diskOnly + r.DiskBytes);
            });

            NoteServiceRead(game, walk.Disk);
            if (again)
            {
                memory.AddHoldAgainDisk(walk.Disk);
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
            string excludedText = sparseBytes + excludedBytes + cheatFileBytes + images + smalls + noCacheBytes > 0
                ? $", not held: {excludedBytes >> 20} MiB in volatile or changed files, {sparseBytes >> 20} MiB in sparse files, {cheatFileBytes >> 20} MiB in anti-cheat files, {images >> 20} MiB in executable images (MZ) or unreadable files, {smalls >> 20} MiB in files under {ViewHold.MinFileBytes >> 20} MiB, {noCacheBytes >> 20} MiB in files where holding loses more than it gains"
                : string.Empty;
            string writtenText = rules.IsWritten is null ? string.Empty
                : $", game-written files: {hold.WrittenHeldBytes >> 20} MiB held in {hold.WrittenFiles} files, not held by condition: {writtenNoHistory >> 20} MiB with too little size history, {writtenShrank >> 20} MiB that shrank before, {writtenUpdated >> 20} MiB after a game update or with the update unknown";
            _log.Info($"{game.Name}: cache held while playing ({exe}): {hold.HeldBytes >> 20} MiB of {targetBytes >> 20} MiB target ({total >> 20} MiB list) in {hold.Files} files, {hold.Views} views, in {elapsed:F1} s (cpu {cpu:F1} s, {checks} checks), {walk.Disk >> 20} MiB read from disk ({hardFaults} hard faults){ErrorsText(errors)}{missing}{excludedText}{writtenText}{stopped}{(walk.Boundaries > 0 ? HoldProbe(reads, total, linked.Token) : string.Empty)}{(cheatKind is null ? string.Empty : $" (anti-cheat: {cheatKind}, hold allowed)")}{(again ? $", held again after the memory floor released everything: disk reads of the play {memory.HoldAgainDiskBytes >> 20} MiB of {HoldAgainDiskCapBytes >> 20} MiB, available {availMib} MiB before" : string.Empty)}");
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

    // 목록을 HoldBatchBytes 씩 붙들며 걷습니다 (처음 붙들기와 다시 붙들기 공통): 직렬화 잠금은 HoldLockBatches 배치마다 (또는 속도 상한으로 쉬기 전에) 놓고, 가용 메모리가 하한에 닿으면 멈추고,
    // 디스크에서 읽은 양이 diskCap 을 넘으면 끝냅니다. 배치마다 결과를 each 에 넘깁니다.
    private WalkResult WalkHold(IKeepHold hold, IReadOnlyList<ReadOp> reads, long diskCap, long floor, CancellationToken ct, Action<HoldBatchResult>? each = null)
    {
        bool locked = false; // _touchSerial 을 쥐고 있는가: HoldLockBatches 배치마다, 그리고 속도 제한으로 쉬기 전에 놓아 다른 만지기가 끼어들 수 있게 함
        int inGroup = 0;
        try
        {
            return Walk(reads, diskCap, ct, () => _env.AvailableRamMib() - (HoldBatchBytes >> 20) >= floor, batch =>
            {
                if (!locked)
                {
                    Monitor.Enter(_touchSerial);
                    (locked, inGroup) = (true, 0);
                    hold.Rebase(); // 잠금을 놓은 사이 다른 스레드가 낸 읽기가 이번 판정에 섞이지 않게
                }

                HoldBatchResult r = hold.Hold(batch, ct);
                each?.Invoke(r);
                double rest = r.BoundaryIndex < 0 ? r.Bytes / (double)PlayTouchBytesPerSecond - r.Seconds : 0; // 붙드는 속도 상한: 게임이 로딩하는 동안 페이지를 몰아서 옮기지 않게
                if (++inGroup >= HoldLockBatches || rest > 0)
                {
                    Monitor.Exit(_touchSerial);
                    locked = false;
                    if (rest > 0)
                    {
                        _env.Sleep(TimeSpan.FromSeconds(rest), ct);
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
        long heldAgainTotal = 0;
        long writtenReleasedBytes = 0;
        int writtenReleasedFiles = 0;
        int checks = 0;
        string? lastSkip = null;
        string ended = string.Empty;
        bool off = false;
        bool releasedForMemory = false; // 하한이나 메모리 부족으로 붙든 것이 모두 사라져 끝남: 게임이 도는 동안 2 분마다 다시 붙들기를 시도 (Retry)
        TimeSpan elapsed = TimeSpan.Zero; // 붙든 뒤 쉰 시간의 합 (시험이 시계를 흉내 내지 않아도 되게 시계 대신 씀)
        TimeSpan lastGameCheck = TimeSpan.Zero;
        TimeSpan lastCheck = TimeSpan.Zero;
        TimeSpan lastWrittenCheck = TimeSpan.Zero;
        long availMib = _env.AvailableRamMib();

        // 게임이 쓰는 파일이 붙들 때와 달라졌으면 그 파일만 풀고 모두 풀려 붙든 것이 없으면 true. 붙들기를 시작하는 동안 (수 초~수십 초) 이미 바뀐 파일을 붙든 직후에 바로 한 번 잡으려고 루프 앞에서도 부릅니다.
        bool CheckWritten()
        {
            foreach (WrittenRelease r in hold.ReleaseChangedWritten())
            {
                (writtenReleasedBytes, writtenReleasedFiles) = (writtenReleasedBytes + r.Bytes, writtenReleasedFiles + 1);
                _log.Info($"{name}: cache hold released a game-written file the game changed ({exe}): {r.Path}, {r.Bytes >> 20} MiB, {elapsed.TotalSeconds:F0} s after the hold started, {hold.HeldBytes >> 20} MiB still held");
            }

            SetHeld(name, hold.HeldBytes);
            if (hold.HeldBytes <= 0)
            {
                ended = " (nothing left held)";
                return true;
            }

            return false;
        }

        // 붙든 것이 모두 사라지면 끝내되, 게임이 도는 동안 다시 붙들 수 있게 표시합니다 (호출한 쪽의 2 분 재시도).
        void EndForMemory(string why)
        {
            (ended, releasedForMemory) = (why, true);
            KeepMemoryOf(name).HoldReleased = true;
        }

        try
        {
            if (hold.WrittenFiles > 0 && CheckWritten())
            {
                return HoldOutcome.No; // 붙든 것이 모두 풀림: finally 가 닫고 끝 줄을 남김. 게임이 도는 동안은 지금의 다시 만지기가 이어받음
            }

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
                        EndForMemory(" (everything released for memory)");
                        break;
                    }
                }

                if (elapsed - lastWrittenCheck >= HoldWrittenCheckEvery && hold.WrittenFiles > 0)
                {
                    lastWrittenCheck = elapsed;
                    if (CheckWritten())
                    {
                        off = true; // 게임이 쓰는 파일이 모두 바뀌어 끝남: 다시 붙들지 않고 다시 만지기가 이어받음
                        break;
                    }
                }

                if (elapsed - lastCheck >= PlayTouchEvery && (game ?? GameOf(name)) is { } current)
                {
                    lastCheck = elapsed;
                    (long trimmed, long refilled, long dropped) = CheckHeld(hold, current, exe, floorMib, ref lastSkip, LoadPhases?.Defers(name, "hold check disk reads") == true); // 로딩 중이면 점검은 돌되 다시 읽기를 작은 상한으로 줄임
                    (trimmedTotal, refilledTotal, droppedTotal, checks) = (trimmedTotal + trimmed, refilledTotal + refilled, droppedTotal + dropped, checks + 1);
                    SetHeld(name, hold.HeldBytes);
                    if (hold.HeldBytes <= 0)
                    {
                        EndForMemory(" (nothing left held)");
                        break;
                    }

                    long again = HoldAgain(hold, current, exe, floorMib, ref lastSkip); // 하한 때문에 내놓은 부분을 조건이 맞을 때 되붙듦
                    availMib = _env.AvailableRamMib(); // 다시 붙든 (또는 하한에서 멈춘) 뒤의 가용으로 다음 내놓기 감시 간격을 정함
                    if (again > 0)
                    {
                        (heldAgainTotal, lastSkip) = (heldAgainTotal + again, null);
                        SetHeld(name, hold.HeldBytes);
                    }
                }
            }
        }
        finally
        {
            long held = hold.HeldBytes;
            hold.Dispose(); // 덜 중요한 것부터 내놓고 뷰를 닫음
            _log.Info($"{name}: cache hold ended after {checks} checks ({exe}): {held >> 20} MiB released at the end, peak {peak >> 20} MiB of {targetBytes >> 20} MiB target, {releasedTotal >> 20} MiB released for memory, {trimmedTotal >> 20} MiB reduced by the memory manager, {refilledTotal >> 20} MiB read again from disk, {droppedTotal >> 20} MiB taken off the hold{(heldAgainTotal > 0 ? $", {heldAgainTotal >> 20} MiB held again after the memory floor released them" : string.Empty)}{(writtenReleasedFiles > 0 ? $", {writtenReleasedFiles} game-written files ({writtenReleasedBytes >> 20} MiB) released after the game changed them" : string.Empty)}{ended}");
        }

        return off ? HoldOutcome.No : releasedForMemory ? HoldOutcome.Retry : HoldOutcome.Done; // 설정이 꺼졌으면 남은 시간은 지금의 다시 만지기가 이어받음, 메모리 때문에 모두 사라졌으면 호출한 쪽의 2 분 재시도가 다시 붙듦
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

    // 2 분 점검에서 하한 때문에 내놓았던 부분을 중요한 순서로 다시 붙듭니다. 조건 (모두 맞아야 함): 로딩 판정이 로딩이 아님, 게임 드라이브의 디스크 대기열 0, 다시 붙든 뒤에도 가용이 하한 + HoldRefillMarginMib 이상 남는 목표 (HoldAgainTargetBytes) 가 최소 (HoldMinTargetBytes) 이상,
    // 회차 읽기 상한 (KeepDiskReadCapBytes) 과 이 플레이의 합계 상한 (HoldAgainDiskCapBytes) 안. 내놓기는 이 조건과 무관하게 가용 < 하한에서 바로 일어납니다. 메모리에 없던 범위는 다시 읽어 보지 않고 (GiveUp) 다시 붙든 바이트를 돌려줍니다.
    private long HoldAgain(IKeepHold hold, GameSettings game, string exe, long floorMib, ref string? lastSkip)
    {
        if (hold.ReleasedRanges(1).Count == 0)
        {
            return 0;
        }

        long availMib = _env.AvailableRamMib();
        long target = HoldAgainTargetBytes(availMib, floorMib);
        if ((HoldAgainBlocker(game) ?? (target < HoldMinTargetBytes ? HoldAgainNoRoom : null)) is { } why)
        {
            NoteHoldSkip(game, exe, $"hold again deferred: {why}", ref lastSkip);
            return 0;
        }

        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "play hold again", default, game.OriginRoot, allowWhileYielded: true, preserveGameKey: game.Name);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            NoteHoldSkip(game, exe, "hold again deferred: disk yielded", ref lastSkip);
            return 0;
        }

        lease?.NoteReadIssue("play hold again", 0, false);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lease?.Token ?? default, _playStop.Token);
        using var low = new LowPriorityReader(_env);
        using var activity = ServiceActivity.Begin(ServiceOp.Hold);
        KeepMemory memory = KeepMemoryOf(game.Name);
        long before = hold.HeldBytes;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        WalkResult walk = WalkHold(hold, hold.ReleasedRanges(target), HoldAgainRoundCap(memory), floorMib, linked.Token);
        hold.GiveUp(walk.Hit); // 메모리에 없다고 확인한 범위만 포기함: 같은 파일의 건너뛴 뒤 범위는 확인하지 않았으므로 다음 점검이 다시 봄
        NoteServiceRead(game, walk.Disk);
        memory.AddHoldAgainDisk(walk.Disk);
        long held = hold.HeldBytes - before;
        _log.Info($"{game.Name}: cache held again after the memory floor released it ({exe}): {held >> 20} MiB of {target >> 20} MiB target, {walk.Disk >> 20} MiB read from disk, disk reads of the play {memory.HoldAgainDiskBytes >> 20} MiB of {HoldAgainDiskCapBytes >> 20} MiB, {walk.Boundaries} missing stretches skipped ({walk.SkippedBytes >> 20} MiB not held), in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s, available {availMib} MiB before, {hold.HeldBytes >> 20} MiB held{(walk.Stopped ? $", stopped at the memory floor ({floorMib} MiB)" : walk.CapReached ? ", disk read cap reached" : string.Empty)}");
        return held;
    }

    // 붙든 쪽이 작업 집합에 있는지 보고 (QueryWorkingSetEx) 메모리 관리자가 줄여 간 쪽만 다시 읽어 올립니다. 줄여 간 양, 디스크에서 다시 읽은 양, 목록에서 뺀 양을 돌려줍니다.
    // loading 이면 바뀐 파일 내놓기와 작업 집합 조회는 그대로 하고, 줄여 간 구간을 디스크에서 다시 읽는 양만 KeepLoadingCheckDiskCapBytes 로 줄이며 상한에 닿아도 남은 구간을 붙들기에서 빼지 않습니다.
    private (long Trimmed, long Refilled, long Dropped) CheckHeld(IKeepHold hold, GameSettings game, string exe, long floorMib, ref string? lastSkip, bool loading = false)
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
            bool refill = _env.AvailableRamMib() >= floorMib + HoldRefillMarginMib; // 가용 메모리에 여유가 없으면 줄여 간 쪽을 다시 읽어 오지 않고 그 구간을 목록에서 뺌
            r = hold.Recheck(loading ? KeepLoadingCheckDiskCapBytes : KeepDiskReadCapBytes, linked.Token, refill, dropOnCap: !loading);
        }

        NoteServiceRead(game, r.RefilledBytes);

        if (r.QueryFailures > 0)
        {
            NoteHoldSkip(game, exe, $"check: the working set query failed for {r.QueryFailures} ranges (error {r.QueryError}), those were not judged", ref lastSkip); // 같은 오류는 한 번만 남김
        }

        string cap = r.CapReached ? $", refill cap reached, {r.DroppedBytes >> 20} MiB taken off the hold" : r.DroppedBytes > 0 ? $", {r.DroppedBytes >> 20} MiB taken off the hold (unreadable)" : string.Empty;
        string changed = r.ChangedFiles.Count > 0 ? $", {r.ChangedFiles.Count} changed file(s) released ({r.ChangedFiles[0]})" : string.Empty;
        _log.Info($"{game.Name}: cache hold checked ({exe}): {r.CheckedBytes >> 20} MiB looked up in the working set in {r.Seconds:F1} s (cpu {r.CpuSeconds:F1} s, {r.Checks} checks), {r.TrimmedBytes >> 20} MiB reduced by the memory manager, {r.RefilledBytes >> 20} MiB of it read from disk again ({r.HardFaults} hard faults){cap}{changed}, {hold.HeldBytes >> 20} MiB held");
        return (r.TrimmedBytes, r.RefilledBytes, r.DroppedBytes);
    }
}
