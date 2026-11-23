using AsterCache.Core.Discovery;
using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 가 게임이 실행되는 동안 예열에 든 스파스 속성 파일의 블록을 버퍼드 읽기로 주기마다 다시 읽는 곳 (설정 memory.keep_sparse).
/// 스파스 파일은 섹션 (매핑) 을 만들면 그 파일의 캐시가 파일 전체에서 버려지므로 (2026-11-14 실험) 붙들기·다시 만지기·사다리가 모두 건너뛰고, 그래서 예열해 둔 스파스 블록이 가장 먼저 밀려납니다.
/// 이 유지는 매핑·뷰·PrefetchVirtualMemory 를 부르지 않고 일반 버퍼드 ReadFile 만 씁니다 (IBlockReader). 대상은 유지 목록 (예열 선택을 중요한 순서로 놓은 것, RefreshKeepList) 안의 스파스 파일 구간이라
/// 따로 순위·크기를 두지 않습니다: 스파스 블록은 주 순위 목록과 같은 점수로 한 목록에 섞여 (LoadRefillPlans.RebuildRanked) 예열 예산 안에 든 것만 예열되고, 그것만 이 유지의 대상입니다.
/// 2026-11-23 실험 (V4): 10 초마다 버퍼드로 다시 읽은 파일은 메모리 압박 뒤에도 3 회 모두 99.9~100% 남았고 다시 읽지 않은 스파스 파일은 0% 남았습니다 (스파스 속성 유무는 차이 없음).
/// 한계: SSD 에서 파일 전체를 10 초마다 읽은 조건이고, 이 유지의 주기 (20~30 초) 와 순위 목록 범위에서 같은지는 재지 않았습니다. 실제 플레이 로그의 회차별 디스크 읽기량 (끝 줄) 으로 확인합니다.
/// 붙들기·다시 만지기와 따로 도는 스레드에서 돕니다 (KeepCacheWhilePlaying 이 시작하고 끝냄). 순위 목록이 있는 게임에서만 돕니다: 트레이스를 금지하는 안티치트 게임은 새 순위 목록이 만들어지지 않고 (붙들기 게이트와 같은 범위), 그 게임에는 이 유지의 대상도 없습니다.
/// 읽기는 캐시에 있으면 메모리 복사라 디스크를 쓰지 않습니다. 밀려난 블록을 디스크에서 다시 읽게 되는 경우는 붙들기의 다시 붙들기와 같은 조건을 따릅니다 (HoldAgainBlocker, HoldAgainRoundCap, 플레이 합계 HoldAgainDiskCapBytes 를 함께 셈).
/// 버퍼드 읽기는 읽기 전에 그 블록이 캐시에 있는지 알 수 없고 이 프로세스의 하드 폴트 수에도 잡히지 않으므로 (2026-11-22 실험: 콜드 읽기 후에도 +0) 블록 하나를 읽는 데 걸린 시간으로 디스크로 갔는지 판정합니다.
public sealed partial class Prewarmer
{
    /// 게임 시작 뒤 이 시간 동안은 SparseKeepEarlyEvery 마다, 그 뒤는 SparseKeepEvery 마다 한 회차를 돕니다. 게임이 시작 뒤 3 분 안에 메모리를 가장 많이 가져가기 때문입니다 (붙들기의 HoldEarlyWindow 와 같은 값).
    public static readonly TimeSpan SparseKeepEarlyWindow = TimeSpan.FromMinutes(3);

    /// 시작 뒤 첫 3 분의 회차 주기: 20 초. 근거: 2026-11-22 실제 플레이 기록에서 시작 뒤 150~180 초에 우선순위 5 대기 목록 재사용이 초당 47~63 MiB 였으므로 20 초에 최대 약 1.3 GiB 가 재사용되고, 경기 중 우선순위 5 대기 양 (약 4~6 GiB) 보다 작습니다.
    public static readonly TimeSpan SparseKeepEarlyEvery = TimeSpan.FromSeconds(20);

    /// 3 분 뒤의 회차 주기: 30 초. 근거: 같은 기록에서 그 뒤 재사용은 2 분에 0.7~1.6 GiB 이고 초당 최대 63 MiB 로 보아도 30 초에 약 1.9 GiB 라 경기 중 우선순위 5 대기 양 (약 4~6 GiB) 보다 작습니다.
    public static readonly TimeSpan SparseKeepEvery = TimeSpan.FromSeconds(30);

    /// 디스크로 간 읽기로 보는 기준의 고정분: 2 ms. 메모리에서 복사하는 읽기는 MiB 당 1 ms 미만이고 하드디스크 읽기는 위치 이동만으로 수 ms 이상입니다 (2 ms + 길이 × MiB 당 1 ms 를 넘으면 디스크로 간 것으로 봄).
    public const double SparseDiskReadBaseSeconds = 0.002;

    /// 디스크로 간 읽기로 보는 기준의 길이 비례분: MiB 당 1 ms (메모리 복사 속도의 위쪽 한계).
    public const double SparseDiskReadSecondsPerMiB = 0.001;

    /// 한 회차의 읽기를 나누는 묶음 크기: 16 MiB (붙들기의 HoldBatchBytes 와 같음). 묶음마다 디스크 읽기 조건 (로딩·대기열) 을 다시 보고 묶음 사이에 SparseBatchPause 만큼 쉬어 복사가 한꺼번에 몰리지 않게 합니다.
    public const long SparseBatchBytes = HoldBatchBytes;

    /// 묶음 사이에 쉬는 시간: 20 ms. 읽기 스레드가 코어를 연이어 차지하지 않고 게임 스레드에 양보하게 합니다.
    public static readonly TimeSpan SparseBatchPause = TimeSpan.FromMilliseconds(20);

    /// 게임이 끝나 스레드를 멈출 때 기다리는 시간의 상한 (한 묶음을 읽는 시간보다 훨씬 긺).
    public static readonly TimeSpan SparseJoinWait = TimeSpan.FromSeconds(10);

    /// 이 길이를 읽는 데 걸린 시간 (초) 이 이 값을 넘으면 디스크로 간 읽기로 봅니다.
    internal static double SparseDiskReadSeconds(long lengthBytes) => SparseDiskReadBaseSeconds + (lengthBytes / 1048576.0 * SparseDiskReadSecondsPerMiB);

    /// 한 플레이의 스파스 유지 상태: 유지 목록 (붙들기와 따로 이 스레드가 가짐), 거기서 뽑은 스파스 블록, 돈 회차 수, 어느 회차든 닿은 가장 먼 곳, 디스크로 판정한 읽기의 누적.
    private sealed class SparseKeep
    {
        public readonly KeepList List = new();
        public IReadOnlyList<ReadOp>? Source; // Blocks 를 뽑은 때의 List.Ordered (같은 객체인 동안 다시 뽑지 않음)
        public IReadOnlyList<ReadOp> Blocks = []; // 중요한 순서의 64 KiB 블록
        public int Rounds;
        public int Reached; // 어느 회차든 읽은 가장 뒤 블록의 번호 + 1
        public int DeferredRounds; // 디스크 읽기가 허용되지 않아 (로딩·대기열·플레이 합계를 다 씀) 디스크로 간 첫 블록에서 끝나도록 읽은 회차
        public long DiskBytes;
    }

    private enum SparseRoundEnd
    {
        Completed, // 목록 끝까지 읽음
        Skipped, // 조건이 맞지 않아 읽지 않음 (메모리 하한 아래·읽기 양보·재배치 중·메모리에 둔 목록 없음·유지 목록 없음): 다음 회차에 다시
        DiskStop, // 디스크로 간 읽기가 이 회차의 상한 (또는 조건이 안 맞아 0) 에 닿아 이 회차만 끝남: 다음 회차가 이어서 더 나아감
        Canceled, // 서비스가 멈춤
    }

    /// 게임이 끝날 때까지 스파스 유지 회차를 돕니다 (붙들기·다시 만지기 곁 스레드). 예외는 이 기능만 끝내고 서비스의 다른 동작과 플레이 기록은 멈추지 않습니다.
    internal void KeepSparseWhilePlaying(GameSettings started, string exe, CancellationToken stop)
    {
        var keep = new SparseKeep();
        string ended = string.Empty;
        bool noted = false; // 시작 줄을 남겼는가 (끝 줄을 남길지)
        TimeSpan elapsed = TimeSpan.Zero; // 쉰 시간의 합 (시험이 시계를 흉내 내지 않아도 되게 시계 대신 씀)
        try
        {
            for (bool first = true; ; first = false)
            {
                if (!first)
                {
                    TimeSpan interval = elapsed < SparseKeepEarlyWindow ? SparseKeepEarlyEvery : SparseKeepEvery;
                    _env.Sleep(interval, stop);
                    elapsed += interval;
                }

                if (stop.IsCancellationRequested || GameOf(started.Name) is not { } game || !_env.GameRunning(game))
                {
                    break; // 서비스가 멈추거나 설정에서 빠지거나 등록 실행 파일이 모두 끝남
                }

                if (!_env.KeepSparseEnabled())
                {
                    ended = " (turned off in the settings)";
                    break;
                }

                if (first && _env.RankedRows(game) is null)
                {
                    break; // 순위 목록이 없음 (트레이스가 없거나 트레이스를 금지하는 안티치트 게임): 이 유지를 하지 않음
                }

                SparseRoundEnd end = KeepSparseRound(game, keep, stop, ref noted);
                if (end == SparseRoundEnd.Canceled)
                {
                    break;
                }
            }
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            ended = $" (stopped: {e.GetType().Name})";
            _log.Warn($"{started.Name}: sparse blocks kept while playing stopped: {e.GetType().Name}");
        }
        finally
        {
            if (noted)
            {
                _log.Info($"{started.Name}: sparse blocks kept while playing ended after {keep.Rounds} rounds ({exe}): reached {keep.Blocks.Take(keep.Reached).Sum(b => b.Length) >> 20} of {keep.Blocks.Sum(b => b.Length) >> 20} MiB of the list, {keep.DiskBytes >> 20} MiB read from disk (judged by read time), {keep.DeferredRounds} rounds with disk reads held back (loading phase, game drive queue, or the disk reads of the play spent){ended}");
            }
        }
    }

    // 유지 목록에서 스파스 파일 구간을 중요한 순서의 64 KiB 블록으로 뽑습니다 (목록이 바뀌었을 때만). 안티치트 구성요소 파일은 붙들기와 같이 뺍니다. 실행 이미지는 매핑하지 않는 읽기라 따로 거르지 않습니다.
    // 스파스 구간이 없으면 false.
    private bool LoadSparseBlocks(GameSettings game, SparseKeep keep)
    {
        if (!RefreshKeepList(game, keep.List))
        {
            return false;
        }

        if (!ReferenceEquals(keep.Source, keep.List.Ordered))
        {
            keep.Source = keep.List.Ordered;
            var sparse = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            List<ReadOp> blocks = [];
            foreach (ReadOp op in keep.List.Ordered)
            {
                if ((sparse.TryGetValue(op.Path, out bool s) ? s : sparse[op.Path] = _env.IsSparse(op.Path) && !AntiCheat.IsProcessName(Path.GetFileName(op.Path))) && op.Length > 0)
                {
                    for (long at = op.Offset; at < op.Offset + op.Length; at += LoadBoundaries.Block)
                    {
                        blocks.Add(new ReadOp(op.Path, at, Math.Min(LoadBoundaries.Block, op.Offset + op.Length - at)));
                    }
                }
            }

            keep.Blocks = blocks;
        }

        return keep.Blocks.Count > 0;
    }

    /// 한 회차: 스파스 블록을 맨 앞 (가장 중요한 블록) 부터 버퍼드 읽기로 읽습니다. 가용 메모리가 붙들기의 하한 아래면 건너뜁니다 (밀려나는 중인 것을 디스크에서 다시 읽지 않기 위함).
    /// 디스크로 판정한 읽기는 붙들기의 다시 붙들기와 같은 조건에서만 허용합니다: 로딩 판정이 로딩이 아니고 게임 드라이브 대기열이 0 일 때, 회차 KeepDiskReadCapBytes 와 플레이 합계 HoldAgainDiskCapBytes (다시 붙들기의 읽기와 함께 셈) 안에서.
    /// 조건이 맞지 않으면 상한이 0 이라 캐시에 있는 블록은 계속 읽고 (디스크를 쓰지 않음) 디스크로 간 첫 블록에서 이 회차를 끝냅니다. 회차는 매번 목록의 맨 앞부터라 앞 회차가 닿은 곳까지는 메모리라 빨리 지나가고 다음 회차가 이어서 더 나아갑니다.
    /// 읽는 스레드의 I/O 우선순위만 이 호출 안에서 낮추고 CPU 우선순위는 보통으로 둡니다: CPU 우선순위가 낮으면 게임 스레드에 밀려 메모리 복사가 길어져 디스크 읽기로 잘못 셉니다.
    /// 디스크로 판정한 읽기는 서비스 자신의 읽기로 기록해 (SelfReadLog, ServiceDiskReads) 다음 집계와 로딩 판정이 게임의 읽기로 세지 않게 합니다. 읽기 중재 대여 (양보 중이면 건너뜀) 와 초 단위 기록 표시는 다른 유지 회차와 같습니다.
    /// 읽는 동안 _touchSerial 은 잡지 않습니다: 버퍼드 읽기는 이 프로세스의 하드 폴트 수에 잡히지 않으므로 (위 설명) 다른 만지기의 경계 판정에 섞이지 않고, 디스크로 간 읽기를 기다리며 붙들기 점검을 막을 까닭이 없습니다.
    private SparseRoundEnd KeepSparseRound(GameSettings game, SparseKeep keep, CancellationToken stop, ref bool noted)
    {
        if (!_solar.Enabled || Relocating(game.Name) || _env.OpenBlockReader is not { } open)
        {
            return SparseRoundEnd.Skipped;
        }

        lock (_gate)
        {
            if (!(_last.ContainsKey(game.Name) || _warmAtServiceStart.Contains(game.Name) || _solar.HasMark(game.Name))) // 메모리에 둔 목록이 없음
            {
                return SparseRoundEnd.Skipped;
            }
        }

        long availMib = _env.AvailableRamMib();
        if (availMib < HoldMinFloorMib)
        {
            return SparseRoundEnd.Skipped;
        }

        bool listed;
        using (new LowPriorityReader(_env)) // 목록을 다시 계산하는 일은 낮은 CPU 우선순위로 (읽기 구간과 따로)
        {
            listed = LoadSparseBlocks(game, keep);
        }

        if (!listed)
        {
            return SparseRoundEnd.Skipped;
        }

        if (!noted)
        {
            noted = true;
            _log.Info($"{game.Name}: sparse blocks kept while playing: {keep.Blocks.Sum(b => b.Length) >> 20} MiB in {keep.Blocks.Select(b => b.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()} files");
        }

        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "play sparse", default, game.OriginRoot, allowWhileYielded: true, preserveGameKey: game.Name);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            return SparseRoundEnd.Skipped;
        }

        lease?.NoteReadIssue("play sparse", 0, false); // 양보 진단 로그에 이 단계가 보이게
        using var activity = ServiceActivity.Begin(ServiceOp.Keep); // 초 단위 기록의 동작 표시
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lease?.Token ?? default, stop);
        using IBlockReader reader = open();
        KeepMemory memory = KeepMemoryOf(game.Name);
        IReadOnlyList<ReadOp> blocks = keep.Blocks;
        int count = (int)Math.Min(blocks.Count, HoldTargetBytes(availMib, HoldMinFloorMib) / LoadBoundaries.Block); // 읽는 양은 붙들기의 목표와 같은 식으로 지금 가용 메모리에 맞춰 앞쪽 (가장 중요한 부분) 만
        long roundDisk = 0;
        List<ReadOp> diskReads = []; // 디스크로 판정한 블록 (서비스 자신의 읽기 기록용)
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        SparseRoundEnd end = SparseRoundEnd.Completed;
        bool deferred = false;
        _ = _env.SetReadIoLow(true);
        try
        {
            for (int i = 0; i < count && end == SparseRoundEnd.Completed;)
            {
                int to = i;
                for (long bytes = 0; to < count && bytes < SparseBatchBytes; to++)
                {
                    bytes += blocks[to].Length;
                }

                bool blocked = HoldAgainBlocker(game) is not null; // 묶음마다 다시 봄: 읽는 도중 로딩이 시작될 수 있음
                long roundCap = blocked ? 0 : HoldAgainRoundCap(memory); // 0 이면 디스크로 간 첫 블록에서 끝남
                deferred |= roundCap == 0;
                for (; i < to && end == SparseRoundEnd.Completed; i++)
                {
                    if (linked.IsCancellationRequested)
                    {
                        end = stop.IsCancellationRequested ? SparseRoundEnd.Canceled : SparseRoundEnd.Skipped; // 읽기 중재가 이 회차만 취소함 (게임 끝·다른 게임 시작): 다음 회차에 새 대여로
                        break;
                    }

                    ReadOp block = blocks[i];
                    if (reader.Read(block.Path, block.Offset, (int)block.Length) is not { } seconds)
                    {
                        continue; // 열 수 없거나 읽기 실패: 이 회차에서 그 파일은 건너뜀
                    }

                    keep.Reached = Math.Max(keep.Reached, i + 1);
                    if (seconds <= SparseDiskReadSeconds(block.Length))
                    {
                        continue;
                    }

                    diskReads.Add(block);
                    roundDisk += block.Length;
                    if (roundDisk >= roundCap)
                    {
                        end = SparseRoundEnd.DiskStop;
                    }
                }

                if (end == SparseRoundEnd.Completed && i < count)
                {
                    _env.Sleep(SparseBatchPause, linked.Token); // 묶음 사이: 게임 스레드에 양보
                }
            }
        }
        finally
        {
            _ = _env.SetReadIoLow(false);
            SelfReadLog.Note(diskReads, startedUtc, DateTimeOffset.UtcNow); // 디스크 읽기 집계가 이 읽기를 게임의 읽기로 세지 않게
            NoteServiceRead(game, roundDisk); // 로딩 판정이 서비스 자신의 읽기를 게임의 읽기에서 뺌
            memory.AddHoldAgainDisk(roundDisk); // 다시 붙들기와 합쳐 이 플레이의 디스크 읽기 합계에 셈
            keep.DiskBytes += roundDisk;
        }

        if (end != SparseRoundEnd.Canceled && end != SparseRoundEnd.Skipped)
        {
            keep.Rounds++;
            keep.DeferredRounds += deferred ? 1 : 0;
        }

        return end;
    }
}
