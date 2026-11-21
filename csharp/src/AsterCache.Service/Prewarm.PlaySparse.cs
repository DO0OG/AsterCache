using AsterCache.Core.Discovery;
using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 가 게임이 실행되는 동안 스파스 속성 파일의 순위 블록 (최대 SparseRankedCapBytes) 을 버퍼드 읽기로 주기마다 다시 읽어 메모리에 남기는 곳 (설정 memory.keep_sparse).
/// 스파스 파일은 섹션 (매핑) 을 만들면 그 파일의 캐시가 파일 전체에서 버려지므로 (2026-11-14 실험) 붙들기·다시 만지기·순위 목록 어디에도 넣지 못하고, 그 결과 경기 중 순위 밖 디스크 지연의 93% 가 스파스 파일의 읽기였습니다 (2026-11-22 조사).
/// 그래서 이 유지는 매핑·뷰·PrefetchVirtualMemory 를 부르지 않고 일반 버퍼드 ReadFile 만 씁니다 (IBlockReader). 대기 목록의 페이지를 읽으면 다시 최근에 쓴 것이 되어 경기 중 새로 읽히는 양에 밀려나지 않습니다.
/// 붙들기·다시 만지기와 따로 도는 스레드에서 돕니다 (KeepCacheWhilePlaying 이 시작하고 끝냄): 첫 회차는 시작 만지기가 끝난 직후이고, 그 뒤는 SparseKeepEarlyEvery / SparseKeepEvery 마다 돕니다.
/// 버퍼드 읽기는 하드 폴트 수로 디스크 읽기를 셀 수 없으므로 블록 하나를 읽는 데 걸린 시간으로 디스크로 갔는지 판정하고, 이어진 두 블록이 디스크로 갔으면 거기를 경계로 보고 뒤 (덜 중요한 쪽) 를 이 플레이의 유지 대상에서 뺍니다.
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

    /// 한 회차에서 디스크에서 읽는 양의 상한: 4 MiB. 넘으면 이 플레이의 스파스 유지를 끝냅니다 (읽는 블록이 64 KiB 라 디스크로 간 블록이 64 개). 이어진 두 블록의 경계 판정이 먼저 막으므로 흩어진 블록이 이만큼 밀려난 경우의 방어입니다.
    public const long SparseRoundDiskCapBytes = 4L << 20;

    /// 한 플레이에서 이 유지가 디스크에서 읽는 양의 누적 상한: 32 MiB (붙들기의 KeepDiskReadCapBytes 와 같은 값). 넘으면 이 플레이의 스파스 유지를 끝냅니다: 경기 중 디스크 읽기를 늘리지 않기 위한 한도입니다.
    public const long SparsePlayDiskCapBytes = 32L << 20;

    /// 게임이 끝나 스레드를 멈출 때 기다리는 시간의 상한 (한 블록을 읽는 시간보다 훨씬 긺).
    public static readonly TimeSpan SparseJoinWait = TimeSpan.FromSeconds(10);

    /// 이 길이를 읽는 데 걸린 시간 (초) 이 이 값을 넘으면 디스크로 간 읽기로 봅니다.
    internal static double SparseDiskReadSeconds(long lengthBytes) => SparseDiskReadBaseSeconds + (lengthBytes / 1048576.0 * SparseDiskReadSecondsPerMiB);

    /// 한 플레이의 스파스 유지 상태: 중요한 순서의 남은 블록 (경계를 만나면 그 지점부터 뒤가 빠짐), 돈 회차 수, 디스크에서 읽은 누적 양.
    private sealed class SparseKeep
    {
        public IReadOnlyList<ReadOp> Blocks = [];
        public int Rounds;
        public long DiskBytes;
    }

    private enum SparseRoundEnd
    {
        Completed, // 목록 끝까지 읽음
        Skipped, // 조건이 맞지 않아 읽지 않음 (메모리 하한 아래·읽기 양보·재배치 중·메모리에 둔 목록 없음): 다음 회차에 다시
        Boundary, // 이어진 두 블록이 디스크로 가 뒤를 뺌
        DiskCap, // 디스크 읽기 상한: 이 플레이의 유지를 끝냄
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

                if (first)
                {
                    if (!LoadSparseList(game, keep))
                    {
                        break; // 스파스 순위 목록이 없음 (스파스 파일을 읽은 기록이 없거나 옛 설치본): 할 일이 없음
                    }

                    noted = true;
                    _log.Info($"{game.Name}: sparse blocks kept while playing: {keep.Blocks.Sum(b => b.Length) >> 20} MiB in {keep.Blocks.Select(b => b.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count()} files (target {LoadRefillPlanner.SparseRankedCapBytes >> 20} MiB)");
                }

                SparseRoundEnd end = KeepSparseRound(game, keep, stop);
                if (end == SparseRoundEnd.Canceled)
                {
                    break;
                }

                if (end == SparseRoundEnd.DiskCap)
                {
                    ended = " (disk read cap reached)";
                    break;
                }

                if (keep.Blocks.Count == 0)
                {
                    ended = " (nothing left in the list)";
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
                _log.Info($"{started.Name}: sparse blocks kept while playing ended after {keep.Rounds} rounds ({exe}): {keep.Blocks.Sum(b => b.Length) >> 20} MiB left in the list, {keep.DiskBytes >> 20} MiB read from disk{ended}");
            }
        }
    }

    // 스파스 순위 행을 이 플레이의 읽기 목록 (중요한 순서) 으로 바꿉니다. 안티치트 구성요소 파일은 지금의 붙들기와 같이 뺍니다. 실행 이미지는 매핑하지 않는 읽기라 겹칠 일이 없어 따로 거르지 않습니다. 목록이 비면 false.
    private bool LoadSparseList(GameSettings game, SparseKeep keep)
    {
        if (_env.SparseRankedRows(game) is not { Count: > 0 } rows)
        {
            return false;
        }

        List<ReadOp> blocks = [.. rows.Where(r => r.Length > 0 && !AntiCheat.IsProcessName(Path.GetFileName(r.Path))).Select(r => new ReadOp(r.Path, r.Offset, r.Length))];
        keep.Blocks = PrefetchStreamer.Head(blocks, LoadRefillPlanner.SparseRankedCapBytes);
        return keep.Blocks.Count > 0;
    }

    /// 한 회차: 남은 블록을 가장 중요한 것부터 버퍼드 읽기로 읽습니다. 가용 메모리가 붙들기의 하한 아래면 건너뜁니다 (밀려나는 중인 것을 디스크에서 다시 읽지 않기 위함).
    /// 이어진 두 블록이 디스크로 갔으면 거기서 멈추고 첫 블록부터 뒤를 뺍니다. 회차·플레이 디스크 읽기 상한을 넘으면 거기서 멈추고 (DiskCap) 이 플레이의 유지를 끝냅니다. 열 수 없는 파일은 그 회차에서 건너뜁니다.
    /// 읽는 스레드의 I/O 우선순위와 CPU 우선순위는 이 호출 안에서만 낮추고, 읽기 중재의 대여 (양보 중이면 건너뜀) 와 초 단위 기록 표시는 다른 유지 회차와 같습니다.
    private SparseRoundEnd KeepSparseRound(GameSettings game, SparseKeep keep, CancellationToken stop)
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

        if (_env.AvailableRamMib() < HoldMinFloorMib)
        {
            return SparseRoundEnd.Skipped;
        }

        using var low = new LowPriorityReader(_env);
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "play sparse", default, game.OriginRoot, allowWhileYielded: true, preserveGameKey: game.Name);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            return SparseRoundEnd.Skipped;
        }

        lease?.NoteReadIssue("play sparse", 0, false); // 양보 진단 로그에 이 단계가 보이게
        using var activity = ServiceActivity.Begin(ServiceOp.Keep); // 초 단위 기록의 동작 표시
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lease?.Token ?? default, stop);
        using IBlockReader reader = open();
        SparseRoundEnd end = SparseRoundEnd.Completed;
        long roundDisk = 0;
        int streak = 0; // 이어서 디스크로 간 블록 수
        IReadOnlyList<ReadOp> blocks = keep.Blocks;
        for (int i = 0; i < blocks.Count; i++)
        {
            if (linked.IsCancellationRequested)
            {
                return stop.IsCancellationRequested ? SparseRoundEnd.Canceled : SparseRoundEnd.Skipped; // 읽기 중재가 이 회차만 취소함 (게임 끝·다른 게임 시작): 다음 회차에 새 대여로
            }

            ReadOp block = blocks[i];
            if (reader.Read(block.Path, block.Offset, (int)block.Length) is not { } seconds)
            {
                continue; // 열 수 없거나 읽기 실패: 이 회차에서 그 파일은 건너뜀
            }

            if (seconds <= SparseDiskReadSeconds(block.Length))
            {
                streak = 0;
                continue;
            }

            roundDisk += block.Length;
            keep.DiskBytes += block.Length;
            streak++;
            if (streak >= 2)
            {
                long dropped = blocks.Skip(i - 1).Sum(b => b.Length);
                keep.Blocks = [.. blocks.Take(i - 1)]; // 첫 블록부터 뒤 (덜 중요한 쪽) 는 이 플레이의 유지 대상에서 뺌
                _log.Info($"{game.Name}: sparse blocks kept while playing: boundary after {keep.Blocks.Sum(b => b.Length) >> 20} MiB, {dropped >> 20} MiB left out for the rest of this play");
                end = SparseRoundEnd.Boundary;
            }

            if (roundDisk > SparseRoundDiskCapBytes || keep.DiskBytes > SparsePlayDiskCapBytes)
            {
                end = SparseRoundEnd.DiskCap;
            }

            if (end != SparseRoundEnd.Completed)
            {
                break;
            }
        }

        keep.Rounds++;
        return end;
    }
}
