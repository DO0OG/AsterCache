using System.Collections.Concurrent;
using AsterCache.Core.Discovery;
using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 가 게임이 실행되는 동안 유지 목록 (중요도 순서) 을 서비스의 매핑 뷰로 붙들어 두는 곳 (설정 memory.keep_hold).
/// 대기 목록을 주기마다 다시 만지는 방식 (Prewarm.PlayTouch) 은 만진 페이지가 대기 목록에 있는 동안 게임의 읽기나 다른 프로그램의 메모리 사용에 밀려나므로, 작업 집합에 올려 두고 뷰를 닫지 않아 대기 목록에서 빼 둡니다 (ViewHold).
/// 붙드는 때: 게임 실행 파일이 뜬 직후, 시작 만지기보다 먼저 (시작 만지기는 붙든 범위를 뺀 나머지를 만짐). 가변 파일·스파스 파일·순위 목록을 만든 뒤 바뀐 파일은 붙들지 않고, 안티치트 게이트가 고정을 막는 게임과 가변 파일 정보가 없는 게임 (원시 트레이스가 없음) 에서는 하지 않습니다 (그 게임은 지금의 다시 만지기로 남음). 처음 붙들기가 멈춰 남은 범위는 조건이 맞을 때 1 초마다 조금씩 이어 붙들고, 붙들지 못한 나머지 범위는 다시 만지기 회차가 돕니다 (Prewarm.PlayTouch).
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

    /// 다시 붙들기 (하한 때문에 내놓은 것을 되붙듦) 가 한 플레이에서 디스크로 읽는 양의 합의 상한: 1 GiB. 다 쓰면 그 플레이에서는 캐시에 남아 있는 부분만 붙들고 디스크 읽기가 필요한 배치에서 멈춥니다.
    /// 근거 (설계 3.2): 다시 붙들기의 디스크 읽기는 회차 상한 (KeepDiskReadCapBytes) 만 있으면 경기 내내 되풀이될 수 있어 플레이 합계에도 상한을 둡니다. 1 GiB 는 처음 붙들기 목표의 5분의 1 이고 실측으로 고른 값이 아닙니다.
    public const long HoldAgainDiskCapBytes = 1L << 30;

    /// 이어 붙들기 (처음 붙들기가 멈춰 남은 범위를 붙듦) 가 조건을 확인하는 간격: HoldWatchFast 와 같은 1 초.
    public static readonly TimeSpan HoldContinueEvery = HoldWatchFast;

    /// 이어 붙들기가 한 번에 새로 요청하는 양의 상한: 4 MiB. 근거: 2026-11-23 Call of Duty 플레이에서 첫 붙들기 뒤 대기열이 0 이고 로딩이 아닌 구간이 343 초였고, 1 초마다 4 MiB 씩이면 그 구간의 상한이 343 × 4 = 1,372 MiB 라
    /// 플레이 합계 읽기 상한 (HoldAgainDiskCapBytes 1 GiB) 과 같은 자릿수입니다. 요청을 이만큼으로 작게 끊어야 새 로딩 신호가 오면 다음 발행을 바로 멈출 수 있습니다. 실측으로 고른 값이 아닙니다.
    public const long HoldContinueBytes = 4L << 20;

    /// 이어 붙들기가 한 번의 확인 (1 초) 에서 이어서 요청하는 묶음 수의 상한: 16 묶음 (64 MiB). 묶음마다 하한·로딩·대기열 조건을 다시 보고, 디스크 읽기나 빠진 조각이 보이거나 하드 폴트 수를 읽지 못한 (계측하지 못한) 묶음이면 그 확인을 거기서 끝내므로 확인당 디스크에서 읽는 양의 최악은 묶음 하나 (HoldContinueBytes) 로 그대로입니다.
    /// 메모리에 있는 범위가 초당 4 MiB 로만 붙으면 5 GiB 에 조건이 맞는 시간으로 1,280 초가 필요하고, 64 MiB 면 약 80 초입니다 (게임이 메모리를 잡는 속도에는 못 미칠 수 있음). 실측으로 고른 값이 아닙니다.
    public const int HoldContinueBatchesPerCheck = 16;

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

    // 이어 붙들기가 한 번 붙든 동안 (KeepHeld 한 번) 쌓은 양과 마지막으로 로그에 남긴 상태: 상태가 바뀔 때만 한 줄 남깁니다.
    private sealed class HoldContinuation
    {
        public long Held;
        public long Disk;
        public int Issues;
        public string? State; // 붙드는 중·다 씀·멈춤 가운데 마지막으로 남긴 상태
        public string? LastDeferred; // 마지막으로 남긴 미룸 사유 (같은 사유는 다시 남기지 않음)
    }

    private GameSettings? GameOf(string name) => _games.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));

    /// 붙들 목표 (바이트): 기존 예산 (가용 메모리의 PlayTouchAvailableFraction), 붙든 뒤에도 가용 메모리가 하한 위에 남는 양, HoldTargetCapBytes 가운데 가장 작은 것.
    internal static long HoldTargetBytes(long availMib, long floorMib, double fraction = PlayTouchAvailableFraction) => Math.Min(HoldTargetCapBytes, Math.Min((long)(availMib * fraction) << 20, Math.Max(0, availMib - floorMib) << 20));

    /// 플레이 기록으로 배운 게임 몫이 아직 없을 때 처음 붙들기 목표에 쓰는 가용 메모리의 비율: 4 분의 1 (몫을 알면 PlayTouchAvailableFraction 의 절반). 게임이 얼마를 잡을지 모르는 처음 몇 플레이에서는 덜 붙들어
    /// 게임이 메모리를 잡으며 하한 내놓기가 되풀이될 일을 줄이려는 것이고, 재서 고른 값이 아닙니다. 다시 붙들기·이어 붙들기의 식은 그대로입니다.
    public const double HoldUnknownFootprintFraction = 0.25;

    /// 다시 붙들 목표 (바이트): 처음 붙들기와 같은 식이되 하한에 점검의 여유 (HoldRefillMarginMib) 를 더해, 다시 붙든 뒤에도 가용 메모리가 하한 + 1 GiB 이상 남게 합니다 (내놓기는 하한 아래에서 일어나므로 1 GiB 안의 흔들림에서는 내놓기와 번갈아 되풀이되지 않고, 그보다 크게 흔들리면 2분 점검마다 한 번까지 되풀이될 수 있음).
    internal static long HoldAgainTargetBytes(long availMib, long floorMib) => HoldTargetBytes(availMib, floorMib + HoldRefillMarginMib);

    // 다시 붙들기를 지금 하지 않을 사유 (없으면 null): 이 플레이에서 디스크 읽기를 가려내지 못한 적이 있거나, 로딩 판정이 로딩 (실행 파일 시작·신호 모두) 이거나, 게임 드라이브의 디스크 대기열이 0 이 아니거나 모름. 판정이 없는 환경 (시험·기록 없음) 은 막지 않습니다.
    private string? HoldAgainBlocker(GameSettings game) => KeepMemoryOf(game.Name).HoldDiskUnmeasured ? "disk reads cannot be told apart in this play"
        : LoadPhases is not { } board ? null
        : board.StateOf(game.Name) == LoadState.Loading ? "loading phase"
        : board.QueueOf(game.Name) is not 0 ? "game drive queue is not 0 or unknown"
        : null;

    // 처음 붙들기 목표에서 하한에 더하는 양 (MiB): 게임이 이 뒤에 더 잡을 것으로 알려진 양 (플레이 기록으로 배운 몫에서 이 플레이 시작 뒤 줄어든 가용을 뺌). 몫을 모르면 0 이라 목표 식은 그대로입니다.
    // 실행 직후에는 게임 몫이 가용에서 아직 빠지지 않아 그대로 붙들면 게임이 메모리를 잡으며 하한 내놓기가 되풀이될 수 있습니다 (메모리가 작은 PC).
    private long GameStillToTakeMib(GameSettings game, KeepMemory memory, long availMib) =>
        FootprintOf(game)?.Mib is { } footprint ? Math.Max(0, footprint - Math.Max(0, (memory.StartAvailMib ?? availMib) - availMib)) : 0;

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

    /// 한 배치를 처리한 결과: 시작한 범위 수, 만진 바이트, 디스크에서 읽힌 바이트 (예산에 빼는 양), 디스크 읽기가 섞여 경계로 보고 멈췄는가, 하드 폴트 수를 읽지 못해 계측하지 못한 배치인가 (그러면 Walk 가 바로 끝냄), 로딩 판정에 넘길 계측된 디스크 읽기.
    private readonly record struct WalkStep(int Processed, long Consumed, long Disk, bool Boundary, bool Unmeasured = false, long NotedDisk = 0);

    private sealed record WalkResult(List<ReadOp> Kept, List<ReadOp> Missing, List<ReadOp> Hit, long Disk, int Boundaries, long SkippedBytes, long LeftBytes, bool CapReached, bool Stopped, List<ReadOp> Unvisited, bool Unmeasured = false, long NotedDisk = 0);

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
        long noted = 0;
        bool unmeasured = false;
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
            noted += s.NotedDisk;
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

            if (s.Unmeasured)
            {
                unmeasured = true; // 디스크 읽기를 가려내지 못한 배치: 같은 걸음에서 뒤쪽 범위를 더 발행하지 않고 남김
                break;
            }

            if (disk > diskCap)
            {
                capReached = true;
                break;
            }
        }

        List<ReadOp> unvisited = [.. rest.Skip(next)]; // 끝낼 때까지 걸음이 닿지 않은 범위: 호출한 쪽이 이어 붙들 수 있게
        return new WalkResult(kept, missing, hit, disk, boundaries, skipped, unvisited.Sum(o => o.Length), capReached, stopped, unvisited, unmeasured, noted);
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
        private bool _holdDiskUnmeasured;
        private bool _holdWaiting;
        private string? _holdRequest;
        private DateTimeOffset? _startedAt;
        private long? _startAvailMib;
        private List<ReadOp> _holdPending = [];

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

        /// 이 플레이에서 붙들기가 하드 폴트 수를 읽지 못해 디스크 읽기를 가려내지 못한 묶음을 만났는가: 켜지면 이 플레이에서는 디스크를 읽을 수 있는 붙들기 요청 (다시 붙들기·이어 붙들기) 을 더 하지 않습니다 (이미 붙든 것은 그대로).
        public bool HoldDiskUnmeasured
        {
            get
            {
                lock (_lock)
                {
                    return _holdDiskUnmeasured;
                }
            }

            set
            {
                lock (_lock)
                {
                    _holdDiskUnmeasured = value;
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

        /// 이 플레이의 첫 실행 파일이 시작한 시각 (붙들기 로그의 경과 시간 기준).
        public DateTimeOffset? StartedAt
        {
            get
            {
                lock (_lock)
                {
                    return _startedAt;
                }
            }
        }

        /// 이 플레이의 첫 실행 파일이 시작했을 때의 가용 메모리 (MiB).
        public long? StartAvailMib
        {
            get
            {
                lock (_lock)
                {
                    return _startAvailMib;
                }
            }
        }

        public void NoteStarted(DateTimeOffset at, long availMib)
        {
            lock (_lock)
            {
                _startedAt ??= at;
                _startAvailMib ??= availMib;
            }
        }

        /// 처음 붙들기가 멈춰 아직 붙들지 않은 범위의 합 (이어 붙들기 대상).
        public long HoldPendingBytes
        {
            get
            {
                lock (_lock)
                {
                    return _holdPending.Sum(o => o.Length);
                }
            }
        }

        /// 이어 붙들 후보를 처음 붙들기가 본 순서 (중요한 순서) 그대로 바꿔 둡니다.
        public void SetHoldPending(IReadOnlyList<ReadOp> ops)
        {
            lock (_lock)
            {
                _holdPending = [.. ops.Where(o => o.Length > 0)];
            }
        }

        public List<ReadOp> HoldPending()
        {
            lock (_lock)
            {
                return [.. _holdPending];
            }
        }

        /// 붙들었거나 (processed) 메모리에 없다고 확인한 (hit) 범위를 이어 붙들 후보에서 뺍니다. 경계를 만난 범위 뒤에 이어지는 같은 파일의 범위도 함께 뺍니다 (처음 붙들기가 같은 파일의 이어지는 범위를 건너뛰는 것과 같은 이유).
        /// 다시 붙들기가 붙든 범위도 이 길로 빼 같은 범위를 두 번 요청하지 않게 합니다.
        public void FinishHoldPending(IReadOnlyList<ReadOp> processed, IReadOnlyList<ReadOp> hit)
        {
            lock (_lock)
            {
                if (_holdPending.Count == 0)
                {
                    return;
                }

                foreach (ReadOp h in hit)
                {
                    int i = _holdPending.FindIndex(p => string.Equals(p.Path, h.Path, StringComparison.OrdinalIgnoreCase) && p.Offset < h.Offset + h.Length && h.Offset < p.Offset + p.Length);
                    if (i < 0)
                    {
                        continue;
                    }

                    int j = i + 1;
                    while (j < _holdPending.Count && string.Equals(_holdPending[j].Path, h.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        j++;
                    }

                    _holdPending.RemoveRange(i, j - i); // 경계를 만난 범위의 나머지와 뒤 범위 (처음 붙들기도 경계를 만난 범위의 읽지 않은 나머지는 다시 보지 않음)
                }

                var done = new Dictionary<string, List<(long Start, long End)>>(StringComparer.OrdinalIgnoreCase);
                foreach (ReadOp op in processed.Concat(hit))
                {
                    RangeSets.Add(SetOf(done, op.Path), op.Offset, op.Offset + op.Length);
                }

                _holdPending = Subtract(_holdPending, done);
            }
        }

        /// 다시 붙들기가 이 플레이에서 디스크에서 읽은 양의 합.
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
        IReadOnlyList<ReadOp> ordered = PrefetchStreamer.Head(WithoutSparse(_solar.ImportanceOrder(game, readOrder)), (long)(Math.Max(0, availMib) * PlayTouchAvailableFraction) << 20); // 스파스 범위는 만지지 않으므로 상한을 먹지 않게 자르기 전에 뺌
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
    // afterStart: 처음 붙들기 뒤 (붙들지 못했으면 바로) 한 번 부르는 일 (시작 만지기): 붙든 동안은 이 일이 도는 사이에도 감시가 멈추지 않게 감시를 따로 돌립니다. onTick: 감시가 돌 때마다 부름 (유지 회차).
    private HoldOutcome HoldWhilePlaying(GameSettings started, string exe, KeepList keep, ref string? lastSkip, Action<IKeepHold?>? afterStart = null, Action<IKeepHold, TimeSpan>? onTick = null)
    {
        IKeepHold? hold = null;
        try
        {
            hold = StartHold(started, exe, keep, ref lastSkip, out HoldOutcome outcome, out long floorMib, out long targetBytes);
            if (hold is null)
            {
                afterStart?.Invoke(null);
                return outcome;
            }

            if (afterStart is null)
            {
                return KeepHeld(hold, started.Name, exe, floorMib, targetBytes, onTick);
            }

            IKeepHold watched = hold;
            Task<HoldOutcome> watch = Task.Factory.StartNew(() => KeepHeld(watched, started.Name, exe, floorMib, targetBytes, onTick), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default); // 게임이 끝날 때까지 도는 감시라 전용 스레드
            try
            {
                afterStart(hold);
            }
            catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
            {
                _log.Warn($"{started.Name}: cache touch at game start stopped: {e.GetType().Name}"); // 붙든 것과 감시는 그대로 이어감
            }

            return watch.GetAwaiter().GetResult();
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

        targetBytes = again ? HoldAgainTargetBytes(availMib, floorMib) : HoldTargetBytes(availMib, floorMib + GameStillToTakeMib(game, memory, availMib), FootprintOf(game) is null ? HoldUnknownFootprintFraction : PlayTouchAvailableFraction);
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
        string sinceStart = memory.StartedAt is { } startedAt ? $", started {Math.Max(0, (_now() - startedAt).TotalSeconds):F1} s after the executable started" : string.Empty;
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
                (cpu, checks, errors, hardFaults, diskOnly) = (cpu + r.CpuSeconds, checks + r.Checks, errors + r.Errors, hardFaults + r.HardFaults, diskOnly + DiskOf(r));
            });

            NoteServiceRead(game, walk.NotedDisk); // 계측하지 못한 묶음의 보수적 양은 예산에만 쓰고 로딩 판정에는 넘기지 않음
            if (again)
            {
                memory.AddHoldAgainDisk(walk.Disk);
            }

            // 멈춰서 남은 범위 (건너뛴 범위 중 메모리에 없다고 확인하지 않은 것과 방문하지 않은 범위) 를 중요한 순서로 기억해 이어 붙듦 (처음 붙들기가 0 MiB 로 끝나도 같음)
            memory.SetHoldPending([.. walk.Missing.Where(m => !walk.Hit.Contains(m)), .. walk.Unvisited]);
            if (walk.Unmeasured)
            {
                StopHoldReads(game, exe, memory, hold);
            }

            long pendingBytes = memory.HoldPendingBytes;
            double elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(seconds0).TotalSeconds;
            if (linked.IsCancellationRequested)
            {
                hold.Dispose();
                outcome = HoldOutcome.Retry;
                return null; // 게임이 끝났거나 서비스가 멈춤: 호출한 쪽 루프가 곧 끝남
            }

            if (hold.HeldBytes <= 0 && pendingBytes == 0)
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
            string left = pendingBytes > 0 ? $", {pendingBytes >> 20} MiB left to continue with ({Math.Max(0, pendingBytes - walk.LeftBytes) >> 20} MiB skipped after a missing stretch, {walk.LeftBytes >> 20} MiB not visited)" : string.Empty;
            _log.Info($"{game.Name}: cache held while playing ({exe}): {hold.HeldBytes >> 20} MiB of {targetBytes >> 20} MiB target ({total >> 20} MiB list) in {hold.Files} files, {hold.Views} views, in {elapsed:F1} s{sinceStart} (cpu {cpu:F1} s, {checks} checks), {walk.Disk >> 20} MiB read from disk ({hardFaults} hard faults){ErrorsText(errors)}{missing}{left}{excludedText}{writtenText}{stopped}{(walk.Boundaries > 0 ? HoldProbe(reads, total, linked.Token) : string.Empty)}{(cheatKind is null ? string.Empty : $" (anti-cheat: {cheatKind}, hold allowed)")}{(again ? $", held again after the memory floor released everything: disk reads of the play {memory.HoldAgainDiskBytes >> 20} MiB of {HoldAgainDiskCapBytes >> 20} MiB, available {availMib} MiB before" : string.Empty)}");
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

                return new WalkStep(r.BoundaryIndex + 1, r.Bytes + r.SkippedBytes, DiskOf(r), r.BoundaryIndex >= 0, !r.Measured, r.Measured ? r.DiskBytes : 0);
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

    // 묶음에서 디스크에서 읽힌 것으로 셀 양: 하드 폴트 수를 읽지 못해 계측하지 못한 묶음은 읽은 양 전부 (읽기 상한과 공유 예산이 풀리지 않게 보수적으로).
    private static long DiskOf(HoldBatchResult r) => r.Measured ? r.DiskBytes : Math.Max(r.DiskBytes, r.Bytes);

    private static string ErrorsText(int errors) => errors > 0 ? $", {errors} errors" : string.Empty;

    // reads 에서 붙들고 있는 범위를 뺍니다 (순서 그대로): 붙든 범위는 작업 집합에 있어 대기 목록에서 빠져 있으므로 다시 만질 까닭이 없습니다.
    private static List<ReadOp> WithoutHeld(IReadOnlyList<ReadOp> reads, IReadOnlyList<ReadOp> held)
    {
        var sets = new Dictionary<string, List<(long Start, long End)>>(StringComparer.OrdinalIgnoreCase);
        foreach (ReadOp h in held)
        {
            if (!sets.TryGetValue(h.Path, out List<(long Start, long End)>? set))
            {
                sets[h.Path] = set = [];
            }

            RangeSets.Add(set, h.Offset, h.Offset + h.Length);
        }

        return [.. reads.SelectMany(op => sets.TryGetValue(op.Path, out List<(long Start, long End)>? set)
            ? RangeSets.Uncovered(set, op.Offset, op.Offset + op.Length).Select(u => new ReadOp(op.Path, u.From, u.To - u.From))
            : [op])];
    }

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
    // onTick: 감시가 한 번 돌 때마다 (붙든 시간) 부름: 붙들지 못한 나머지 범위의 유지 회차를 따로 돌리는 데 씁니다 (이 감시를 막지 않게 부른 쪽이 따로 돌림).
    // 처음 붙들기가 멈춰 남은 범위가 있으면 1 초마다 조건을 확인해 한 번에 HoldContinueBytes 씩 이어 붙듭니다 (다시 붙들기와 같은 경로, HoldAgain).
    private HoldOutcome KeepHeld(IKeepHold hold, string name, string exe, long floorMib, long targetBytes, Action<IKeepHold, TimeSpan>? onTick = null)
    {
        CancellationToken stop = _playStop.Token;
        KeepMemory memory = KeepMemoryOf(name);
        var cont = new HoldContinuation();
        string? lastContinueSkip = null;
        TimeSpan lastContinue = TimeSpan.Zero;
        bool againDue = false; // 2 분 점검 뒤 내놓은 부분을 다시 붙들 차례 (직렬화 잠금이 쥐여 있으면 다음 초로 미룸)
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
                TimeSpan interval = availMib < floorMib + HoldNearFloorMib || elapsed < HoldEarlyWindow || memory.HoldPendingBytes > 0 ? HoldWatchFast : HoldWatchEvery;
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
                if (availMib < floorMib && hold.HeldBytes > 0)
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

                onTick?.Invoke(hold, elapsed);
                if (memory.HoldPendingBytes > 0 && elapsed - lastContinue >= HoldContinueEvery && (game ?? GameOf(name)) is { } continuing)
                {
                    lastContinue = elapsed;
                    if (HoldAgain(hold, continuing, exe, floorMib, ref lastContinueSkip, cont) > 0)
                    {
                        SetHeld(name, hold.HeldBytes);
                        peak = Math.Max(peak, hold.HeldBytes);
                    }
                }

                // 2 분 점검은 직렬화 잠금을 기다리지 않습니다: 다른 경로 (시작 만지기, 유지 회차) 가 쥐고 있으면 다음 초로 미뤄 같은 스레드의 하한 내놓기가 멈추지 않게 합니다.
                if (elapsed - lastCheck >= PlayTouchEvery && (game ?? GameOf(name)) is { } current && Monitor.TryEnter(_touchSerial))
                {
                    lastCheck = elapsed;
                    (long trimmed, long refilled, long dropped) result;
                    try
                    {
                        result = CheckHeld(hold, current, exe, floorMib, ref lastSkip, LoadPhases?.Defers(name, "hold check disk reads") == true); // 로딩 중이면 점검은 돌되 다시 읽기를 작은 상한으로 줄임
                    }
                    finally
                    {
                        Monitor.Exit(_touchSerial);
                    }

                    (trimmedTotal, refilledTotal, droppedTotal, checks) = (trimmedTotal + result.trimmed, refilledTotal + result.refilled, droppedTotal + result.dropped, checks + 1);
                    SetHeld(name, hold.HeldBytes);
                    if (hold.HeldBytes <= 0 && memory.HoldPendingBytes == 0)
                    {
                        if (peak <= 0 && cont.Held <= 0)
                        {
                            (ended, off) = (" (nothing was held)", true); // 처음부터 붙든 것이 없고 이어 붙들 것도 없음: 지금의 다시 만지기가 이어받음
                            break;
                        }

                        EndForMemory(" (nothing left held)");
                        break;
                    }

                    againDue = true;
                }

                if (againDue && (game ?? GameOf(name)) is { } rehold)
                {
                    long? again = HoldAgain(hold, rehold, exe, floorMib, ref lastSkip); // 하한 때문에 내놓은 부분을 조건이 맞을 때 되붙듦 (잠금이 쥐여 있으면 null: 다음 초에 다시)
                    if (again is not null)
                    {
                        againDue = false;
                        availMib = _env.AvailableRamMib(); // 다시 붙든 (또는 하한에서 멈춘) 뒤의 가용으로 다음 내놓기 감시 간격을 정함
                        if (again > 0)
                        {
                            (heldAgainTotal, lastSkip) = (heldAgainTotal + again.Value, null);
                            SetHeld(name, hold.HeldBytes);
                        }
                    }
                }
            }
        }
        finally
        {
            long held = hold.HeldBytes;
            hold.Dispose(); // 덜 중요한 것부터 내놓고 뷰를 닫음
            _log.Info($"{name}: cache hold ended after {checks} checks ({exe}): {held >> 20} MiB released at the end, peak {peak >> 20} MiB of {targetBytes >> 20} MiB target, {releasedTotal >> 20} MiB released for memory, {trimmedTotal >> 20} MiB reduced by the memory manager, {refilledTotal >> 20} MiB read again from disk, {droppedTotal >> 20} MiB taken off the hold{(heldAgainTotal > 0 ? $", {heldAgainTotal >> 20} MiB held again after the memory floor released them" : string.Empty)}{(cont.Issues > 0 ? $", {cont.Held >> 20} MiB held by continuing after the first pass ({cont.Issues} requests, {cont.Disk >> 20} MiB read from disk, {memory.HoldPendingBytes >> 20} MiB left)" : string.Empty)}{(writtenReleasedFiles > 0 ? $", {writtenReleasedFiles} game-written files ({writtenReleasedBytes >> 20} MiB) released after the game changed them" : string.Empty)}{ended}");
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

    // 하한 때문에 내놓았던 부분을 중요한 순서로 다시 붙듭니다 (cont 가 null, 2 분 점검 뒤). 같은 조건과 같은 길로 처음 붙들기가 멈춰 남은 범위도 이어 붙듭니다 (cont 가 있음: 1 초마다 불러 4 MiB 한 묶음씩 최대 HoldContinueBatchesPerCheck 묶음).
    // 조건 (묶음마다 모두 맞아야 함): 로딩 판정이 로딩이 아님 (실행 파일 시작·신호 모두), 게임 드라이브의 디스크 대기열 0 (모르면 미룸), 다시 붙든 뒤에도 가용이 하한 + HoldRefillMarginMib 이상 남는 목표 (HoldAgainTargetBytes) 가 최소 (HoldMinTargetBytes) 이상,
    // 회차 읽기 상한 (KeepDiskReadCapBytes) 과 이 플레이의 합계 상한 (HoldAgainDiskCapBytes: 다시 붙들기와 이어 붙들기가 함께 씀) 안. 내놓기는 이 조건과 무관하게 가용 < 하한에서 바로 일어납니다.
    // 이어 붙들기는 한 묶음에서 디스크 읽기나 빠진 조각이 보이거나 하드 폴트 수를 읽지 못한 (계측하지 못한) 묶음이면 그 확인을 거기서 끝내므로 (다음 묶음의 범위가 메모리에 있는지는 알 수 없음) 한 번의 확인에서 디스크에서 읽는 양은 묶음 하나 (4 MiB) 안입니다.
    // 메모리에 없던 범위는 다시 읽어 보지 않습니다 (다시 붙들기는 GiveUp, 이어 붙들기는 후보에서 뺌). 이어 붙들기 안에서는 같은 범위를 두 번 요청하지 않게 붙든 범위도 후보에서 뺍니다.
    // 직렬화 잠금은 기다리지 않고 못 잡으면 이번 확인을 건너뜁니다 (감시 스레드가 하한 내놓기를 놓치지 않게): 이어 붙들기는 0, 다시 붙들기는 null (호출한 쪽이 다음 초에 다시 부름). 다시 붙든 바이트를 돌려줍니다.
    private long? HoldAgain(IKeepHold hold, GameSettings game, string exe, long floorMib, ref string? lastSkip, HoldContinuation? cont = null)
    {
        KeepMemory memory = KeepMemoryOf(game.Name);
        if (cont is null ? hold.ReleasedRanges(1).Count == 0 : memory.HoldPendingBytes == 0)
        {
            return 0;
        }

        if (cont is not null && HoldAgainDiskCapBytes - memory.HoldAgainDiskBytes < HoldContinueBytes)
        {
            memory.SetHoldPending([]); // 플레이 합계 상한을 다 씀: 이 플레이에서는 다시 풀리지 않으므로 후보를 비우고 한 번만 남김 (감시도 1 초 간격으로 돌 까닭이 없음)
            NoteContinue(game, exe, cont, "stopped: the disk read cap of the play is used", hold, memory);
            return 0;
        }

        string what = cont is null ? "hold again" : "hold continue";
        string label = cont is null ? "play hold again" : "play hold continue";
        bool busy = false;
        long before = hold.HeldBytes;
        long diskTotal = 0;
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        long availMib = 0;
        long target = 0;
        WalkResult? last = null;
        DiskReadCoordinator.Lease? lease = null;
        CancellationTokenSource? linked = null;
        LowPriorityReader? low = null;
        IDisposable? activity = null;
        try
        {
            for (int batch = 0; batch < (cont is null ? 1 : HoldContinueBatchesPerCheck); batch++)
            {
                availMib = _env.AvailableRamMib();
                target = HoldAgainTargetBytes(availMib, floorMib);
                string? why = HoldAgainBlocker(game)
                    ?? (target < HoldMinTargetBytes ? HoldAgainNoRoom
                        : cont is not null && hold.HeldBytes >= HoldTargetCapBytes ? "the held amount is at the target cap"
                        : cont is not null && HoldAgainDiskCapBytes - memory.HoldAgainDiskBytes < HoldContinueBytes ? "the disk read cap of the play is used"
                        : null);
                if (why is not null)
                {
                    if (cont is null)
                    {
                        NoteHoldSkip(game, exe, $"{what} deferred: {why}", ref lastSkip);
                    }
                    else if (batch == 0 || last is not null)
                    {
                        NoteContinue(game, exe, cont, $"deferred: {why}", hold, memory);
                    }

                    break;
                }

                if (lease is null)
                {
                    lease = _coordinator?.TryBegin(game.OriginRoot, label, default, game.OriginRoot, allowWhileYielded: true, preserveGameKey: game.Name);
                    if (_coordinator is not null && (lease is null || !lease.IsValid))
                    {
                        if (cont is null)
                        {
                            NoteHoldSkip(game, exe, $"{what} deferred: disk yielded", ref lastSkip);
                        }
                        else
                        {
                            NoteContinue(game, exe, cont, "deferred: disk yielded", hold, memory);
                        }

                        break;
                    }

                    lease?.NoteReadIssue(label, 0, false);
                    linked = CancellationTokenSource.CreateLinkedTokenSource(lease?.Token ?? default, _playStop.Token);
                    low = new LowPriorityReader(_env);
                    activity = ServiceActivity.Begin(ServiceOp.Hold);
                }

                IReadOnlyList<ReadOp> candidates = cont is null ? hold.ReleasedRanges(target) : PrefetchStreamer.Head(memory.HoldPending(), Math.Min(Math.Min(target, HoldContinueBytes), HoldTargetCapBytes - hold.HeldBytes));
                if (candidates.Count == 0)
                {
                    break;
                }

                // 직렬화 잠금은 기다리지 않음: 시작 만지기나 유지 회차가 쥐고 있으면 이번 확인을 건너뛰어 (다음 초에 다시) 하한에 따른 내놓기가 막히지 않게 합니다.
                if (!Monitor.TryEnter(_touchSerial))
                {
                    busy = true;
                    break;
                }

                WalkResult walk;
                try
                {
                    walk = WalkHold(hold, candidates, HoldAgainRoundCap(memory), floorMib, linked!.Token);
                }
                finally
                {
                    Monitor.Exit(_touchSerial);
                }

                last = walk;
                if (cont is null)
                {
                    hold.GiveUp(walk.Hit); // 메모리에 없다고 확인한 범위만 포기함: 같은 파일의 건너뛴 뒤 범위는 확인하지 않았으므로 다음 점검이 다시 봄
                }

                memory.FinishHoldPending(walk.Kept, cont is null ? [] : walk.Hit);
                NoteServiceRead(game, walk.NotedDisk);
                memory.AddHoldAgainDisk(walk.Disk);
                diskTotal += walk.Disk;
                if (walk.Unmeasured)
                {
                    StopHoldReads(game, exe, memory, hold);
                }

                if (cont is not null)
                {
                    cont.Issues++;
                }

                if (walk.Disk > 0 || walk.Boundaries > 0 || walk.Stopped || walk.CapReached || linked.IsCancellationRequested || memory.HoldPendingBytes == 0 || walk.Unmeasured) // 계측하지 못한 묶음 (시작이나 끝의 하드 폴트 조회 실패) 이면 다음 묶음의 범위가 메모리에 있는지 알 수 없으므로 잇지 않음
                {
                    break; // 디스크 읽기나 빠진 조각이 보이면 이 확인은 여기서 끝 (다음 묶음의 범위가 메모리에 있는지는 알 수 없음)
                }
            }
        }
        finally
        {
            activity?.Dispose();
            low?.Dispose();
            linked?.Dispose();
            lease?.Dispose();
        }

        long held = hold.HeldBytes - before;
        if (busy && last is null)
        {
            return cont is null ? null : 0;
        }

        if (cont is not null)
        {
            if (last is not null)
            {
                (cont.Held, cont.Disk) = (cont.Held + Math.Max(0, held), cont.Disk + diskTotal);
                NoteContinue(game, exe, cont, memory.HoldPendingBytes == 0 ? "finished" : "holding", hold, memory);
            }

            return held;
        }

        if (last is { } walked)
        {
            _log.Info($"{game.Name}: cache held again after the memory floor released it ({exe}): {held >> 20} MiB of {target >> 20} MiB target, {walked.Disk >> 20} MiB read from disk, disk reads of the play {memory.HoldAgainDiskBytes >> 20} MiB of {HoldAgainDiskCapBytes >> 20} MiB, {walked.Boundaries} missing stretches skipped ({walked.SkippedBytes >> 20} MiB not held), in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds:F1} s, available {availMib} MiB before, {hold.HeldBytes >> 20} MiB held{(walked.Stopped ? $", stopped at the memory floor ({floorMib} MiB)" : walked.CapReached ? ", disk read cap reached" : string.Empty)}");
        }

        return held;
    }

    // 하드 폴트 수를 읽지 못해 디스크 읽기를 가려내지 못한 묶음을 만남: 이 플레이에서는 디스크를 읽을 수 있는 붙들기 요청 (다시 붙들기·이어 붙들기) 을 더 하지 않고 이어 붙들 후보를 비웁니다 (이미 붙든 것, 하한 내놓기, 2 분 점검의 내놓기 판정은 그대로). 사유는 한 번만 남깁니다.
    private void StopHoldReads(GameSettings game, string exe, KeepMemory memory, IKeepHold hold)
    {
        memory.SetHoldPending([]);
        if (memory.HoldDiskUnmeasured)
        {
            return;
        }

        memory.HoldDiskUnmeasured = true;
        _log.Info($"{game.Name}: cache hold stops reading from the disk for this play ({exe}): the number of disk reads of the process could not be read for a batch, so nothing that could read the disk is held again or continued ({hold.HeldBytes >> 20} MiB stay held)");
    }

    // 이어 붙들기의 상태를 누계와 함께 한 줄 남깁니다 (1 초마다 부르므로 매번 남기지 않음): 붙드는 중·다 씀·멈춤은 처음 바뀔 때만, 미룸은 사유의 종류가 바뀔 때만
    // (대기열 값이 바뀌는 것은 같은 종류이고, 미뤘다가 다시 붙들기 시작해도 같은 사유로 다시 미루면 남기지 않음).
    private void NoteContinue(GameSettings game, string exe, HoldContinuation cont, string state, IKeepHold hold, KeepMemory memory)
    {
        if (state.StartsWith("deferred", StringComparison.Ordinal))
        {
            if (state == cont.LastDeferred)
            {
                return;
            }

            cont.LastDeferred = state;
        }
        else
        {
            if (state == cont.State)
            {
                return;
            }

            cont.State = state;
        }

        _log.Info($"{game.Name}: cache hold continuing after the first pass ({exe}): {state}, {cont.Held >> 20} MiB held so far by continuing in {cont.Issues} requests of at most {HoldContinueBytes >> 20} MiB, {cont.Disk >> 20} MiB read from disk (disk reads of the play {memory.HoldAgainDiskBytes >> 20} MiB of {HoldAgainDiskCapBytes >> 20} MiB), {memory.HoldPendingBytes >> 20} MiB left, {hold.HeldBytes >> 20} MiB held");
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

        NoteServiceRead(game, r.Measured ? r.RefilledBytes : 0); // 계측하지 못한 점검의 보수적 양은 로딩 판정에 넘기지 않음

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
