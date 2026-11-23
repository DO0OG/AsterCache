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
/// 붙들기·다시 만지기와 따로 도는 스레드에서 돕니다 (KeepCacheWhilePlaying 이 시작하고 끝냄). 붙들기와 같은 안티치트 게이트 (HoldGate) 를 따르고 순위 목록이 있는 게임에서만 돕니다:
/// 붙들기가 금지된 게임은 이전 기록으로 만든 순위 목록이 남아 있어도 게임 중에 스파스 파일을 읽지 않습니다 (붙들기·다시 만지기·사다리가 스파스 파일을 건너뛰므로 main 도 읽지 않음).
/// 읽기는 캐시에 있으면 메모리 복사라 디스크를 쓰지 않습니다. 밀려난 블록을 디스크에서 다시 읽게 되는 경우는 붙들기의 다시 붙들기와 같은 조건을 따릅니다 (HoldAgainBlocker, 회차 KeepDiskReadCapBytes, 이 유지만의 플레이 합계 SparsePlayDiskCapBytes).
/// 버퍼드 읽기는 읽기 전에 그 블록이 캐시에 있는지 알 수 없고 이 프로세스의 하드 폴트 수에도 잡히지 않으므로 (2026-11-22 실험: 콜드 읽기 후에도 +0) 읽은 시간으로 디스크로 갔는지 판정합니다:
/// 캐시 관리자의 미리 읽기를 끈 읽기 도구로 블록 하나의 시간과 이어진 묶음의 누적 시간을 보고, 넘으면 묶음 전체를 디스크로 셉니다 (2026-11-23 하드디스크 시험으로 정한 기준, SOLAR 문서).
public sealed partial class Prewarmer
{
    /// 게임 시작 뒤 이 시간 동안은 SparseKeepEarlyEvery 마다, 그 뒤는 SparseKeepEvery 마다 한 회차를 돕니다. 게임이 시작 뒤 3 분 안에 메모리를 가장 많이 가져가기 때문입니다 (붙들기의 HoldEarlyWindow 와 같은 값).
    public static readonly TimeSpan SparseKeepEarlyWindow = TimeSpan.FromMinutes(3);

    /// 시작 뒤 첫 3 분의 회차 주기: 20 초. 근거: 2026-11-22 실제 플레이 기록에서 시작 뒤 150~180 초에 우선순위 5 대기 목록 재사용이 초당 47~63 MiB 였으므로 20 초에 최대 약 1.3 GiB 가 재사용되고, 경기 중 우선순위 5 대기 양 (약 4~6 GiB) 보다 작습니다.
    public static readonly TimeSpan SparseKeepEarlyEvery = TimeSpan.FromSeconds(20);

    /// 3 분 뒤의 회차 주기: 30 초. 근거: 같은 기록에서 그 뒤 재사용은 2 분에 0.7~1.6 GiB 이고 초당 최대 63 MiB 로 보아도 30 초에 약 1.9 GiB 라 경기 중 우선순위 5 대기 양 (약 4~6 GiB) 보다 작습니다.
    public static readonly TimeSpan SparseKeepEvery = TimeSpan.FromSeconds(30);

    /// 블록 하나가 디스크로 간 읽기로 보는 기준의 고정분: 0.05 ms. 캐시에 있는 64 KiB 의 복사는 수 µs 에서 수십 µs 이고 (2026-11-23 하드디스크 D: 시험: 중앙 7~8 µs, 99.9% 가 0.04~0.2 ms, 최대 0.56 ms),
    /// 캐시 관리자의 미리 읽기를 끄고 (RandomAccess) 디스크에서 읽은 64 KiB 는 중앙 0.4~0.6 ms 이고 99.9% 가 0.15 ms 를 넘었습니다. 0.05 ms + 길이 × MiB 당 1.6 ms (64 KiB 에 0.15 ms) 를 넘으면 디스크로 간 것으로 봅니다.
    /// 캐시 적중을 디스크로 잘못 세는 쪽이 디스크를 덜 쓰는 안전한 방향입니다.
    public const double SparseDiskReadBaseSeconds = 0.00005;

    /// 블록 하나의 기준의 길이 비례분: MiB 당 1.6 ms (64 KiB 에 0.1 ms).
    public const double SparseDiskReadSecondsPerMiB = 0.0016;

    /// 묶음 (이어진 블록 최대 SparseGroupBlocks 개) 의 누적 시간 기준: 0.05 ms + 길이 × MiB 당 0.8 ms (1 MiB 에 0.85 ms). 블록마다는 기준 안이어도 합이 이를 넘으면 (조금씩 느린 읽기가 이어짐) 묶음 전체를 디스크로 셉니다.
    public const double SparseGroupBaseSeconds = 0.00005;

    public const double SparseGroupSecondsPerMiB = 0.0008;

    /// 한 묶음의 블록 수: 16 (1 MiB). 블록 기준을 넘은 블록은 그 블록만 디스크로 세고, 블록마다는 기준 안인데 누적 시간만 기준을 넘은 묶음은 묶음 전체를 셉니다.
    public const int SparseGroupBlocks = 16;

    /// 한 회차의 읽기를 나누는 묶음 크기: 16 MiB (붙들기의 HoldBatchBytes 와 같음). 묶음마다 디스크 읽기 조건 (로딩·대기열) 을 다시 보고 묶음 사이에 SparseBatchPause 만큼 쉬어 복사가 한꺼번에 몰리지 않게 합니다.
    public const long SparseBatchBytes = HoldBatchBytes;

    /// 묶음 사이에 쉬는 시간: 20 ms. 읽기 스레드가 코어를 연이어 차지하지 않고 게임 스레드에 양보하게 합니다.
    public static readonly TimeSpan SparseBatchPause = TimeSpan.FromMilliseconds(20);

    /// 게임이 끝나 스레드를 멈출 때 기다리는 시간의 상한 (한 묶음을 읽는 시간보다 훨씬 긺).
    public static readonly TimeSpan SparseJoinWait = TimeSpan.FromSeconds(10);

    /// 이 길이를 읽는 데 걸린 시간 (초) 이 이 값을 넘으면 디스크로 간 읽기로 봅니다.
    internal static double SparseDiskReadSeconds(long lengthBytes) => SparseDiskReadBaseSeconds + (lengthBytes / 1048576.0 * SparseDiskReadSecondsPerMiB);

    /// 한 플레이에서 이 유지가 디스크에서 다시 읽는 양의 상한: 512 MiB. 다시 붙들기의 상한 (HoldAgainDiskCapBytes 1 GiB, 그 합계와 따로 셈) 에 더하지 않는 이 유지만의 상한이라, 잘못 디스크로 센 양이 다시 붙들기를 일찍 멈추게 하지 않습니다.
    /// 근거: 유지 대상인 스파스 구간은 예열 예산 안의 몇백 MiB (이 PC 의 계획 306 MiB) 이고 회차 상한 (32 MiB) 의 16 회분입니다. 실측으로 고른 값이 아닙니다. 두 상한을 합친 플레이당 최악은 1.5 GiB (다시 붙들기 1 GiB + 이 유지 512 MiB) 입니다.
    public const long SparsePlayDiskCapBytes = 512L << 20;

    /// 이 길이의 묶음을 읽는 데 걸린 시간 (초) 의 합이 이 값을 넘으면 (블록마다는 기준 안이어도) 묶음 전체를 디스크로 간 읽기로 봅니다.
    internal static double SparseGroupSeconds(long lengthBytes) => SparseGroupBaseSeconds + (lengthBytes / 1048576.0 * SparseGroupSecondsPerMiB);

    // 블록 i 에서 시작하는 한 묶음의 끝: 같은 파일에서 바로 이어지는 블록을 최대 SparseGroupBlocks 개.
    private static int SparseGroupEnd(IReadOnlyList<ReadOp> blocks, int i, int count)
    {
        int to = i + 1;
        while (to < count && to - i < SparseGroupBlocks && blocks[to].Offset == blocks[to - 1].Offset + blocks[to - 1].Length && string.Equals(blocks[to].Path, blocks[to - 1].Path, StringComparison.OrdinalIgnoreCase))
        {
            to++;
        }

        return to;
    }

    /// 한 플레이의 스파스 유지 상태: 유지 목록 (붙들기와 따로 이 스레드가 가짐), 거기서 뽑은 스파스 블록, 돈 회차 수, 어느 회차든 닿은 가장 먼 곳, 디스크로 판정한 읽기의 누적.
    private sealed class SparseKeep
    {
        public readonly KeepList List = new();
        public IReadOnlyList<ReadOp>? Source; // Blocks 를 뽑은 때의 List.Ordered (같은 객체인 동안 다시 뽑지 않음)
        public IReadOnlyList<ReadOp> Blocks = []; // 중요한 순서의 64 KiB 블록
        public bool[] Known = []; // Blocks 와 같은 번호: 앞 회차가 읽어 메모리에 있다고 확인한 블록 (조건이 안 맞는 회차는 이 블록만 읽음)
        public int Rounds;
        public int Reached; // 어느 회차든 읽은 가장 뒤 블록의 번호 + 1
        public int DeferredRounds; // 디스크 읽기가 허용되지 않아 (로딩·대기열·플레이 합계를 다 씀) 디스크로 간 첫 블록에서 끝나도록 읽은 회차
        public long DiskBytes;
        public string Exe = string.Empty; // 로그에 붙는 실행 파일 이름
        public string? LastSkip; // 마지막으로 로그에 남긴 건너뜀 사유 (사유가 바뀔 때만 한 줄을 남기고, 읽은 회차가 나오면 비움)
        public readonly Dictionary<string, int> Skips = []; // 사유별로 건너뛴 (또는 아무것도 읽지 못한) 회차 수: 끝 줄에 집계
        public int FailedReads; // 열 수 없거나 읽기가 실패한 블록 읽기 수
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
        var keep = new SparseKeep { Exe = exe };
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
                    // 붙들기와 같은 안티치트 게이트: 붙들기가 금지된 게임 (트레이스를 금지하는 안티치트, 탐지 실패) 은 예전 집계로 만든 순위 목록이 남아 있어도 게임 중에 스파스 파일을 읽지 않음 (main 도 읽지 않음)
                    if (_pins.HoldGate(game).Blocker is { } cheat)
                    {
                        _log.Info($"{game.Name}: sparse blocks kept while playing skipped ({exe}: anti-cheat: {cheat})");
                        break;
                    }

                    if (_env.RankedRows(game) is null)
                    {
                        NoteSparseSkip(game, keep, "no ranked block list (no trace of this game)", count: false);
                        break; // 순위 목록이 없음 (트레이스가 없는 게임): 이 유지를 하지 않음
                    }
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
            if (noted || keep.Skips.Count > 0 || ended.Length > 0)
            {
                string skipped = keep.Skips.Count > 0 ? $", skipped rounds: {string.Join(", ", keep.Skips.Select(s => $"{s.Key} x{s.Value}"))}" : string.Empty;
                string failed = keep.FailedReads > 0 ? $", {keep.FailedReads} block reads failed" : string.Empty;
                _log.Info($"{started.Name}: sparse blocks kept while playing ended after {keep.Rounds} rounds ({exe}): reached {keep.Blocks.Take(keep.Reached).Sum(b => b.Length) >> 20} of {keep.Blocks.Sum(b => b.Length) >> 20} MiB of the list, {keep.DiskBytes >> 20} MiB read from disk (judged by read time), {keep.DeferredRounds} rounds with disk reads held back (loading phase, game drive queue, or the disk reads of the play spent){skipped}{failed}{ended}");
            }
        }
    }

    /// 예열이 스파스 범위를 메모리 예열에 넣어도 게임 중 유지가 그 블록을 이어 둘 수 있는가: 게임이 시작할 때 이 유지를 시작하는 조건 (시작 스트림 허용 auto.start_stream_enabled, 사다리 memory.standby_ladder, 설정 memory.keep_sparse, 읽기 도구) 과
    /// 첫 회차가 통과해야 하는 조건 (붙들기 안티치트 게이트, 순위 목록) 을 모두 만족하는 게임. 아니면 예열 예산이 모자랄 때 스파스 범위를 메모리 예열에서 빼는 이전 판단을 그대로 씁니다 (유지할 수 없는 스파스에 예산을 쓰지 않음).
    /// 유지를 시작하는 곳이 보는 조건은 모두 여기에 모읍니다. 실행마다 정해지는 일시 상태 (실행 직후 종료가 되풀이돼 시작 만지기와 유지를 물리는 backOff) 는 예열 때 알 수 없어 보지 않습니다.
    internal bool SparseKeepable(GameSettings game) =>
        _env.StartStreamEnabled() && _solar.Enabled && _env.KeepSparseEnabled() && _env.OpenBlockReader is not null && _pins.HoldGate(game).Blocker is null && _env.RankedRows(game) is not null;

    // 유지 목록에서 스파스 파일 구간을 중요한 순서의 64 KiB 블록으로 뽑습니다 (목록이 바뀌었을 때만). 안티치트 구성요소 파일은 붙들기와 같이 뺍니다. 실행 이미지는 매핑하지 않는 읽기라 따로 거르지 않습니다.
    // 스파스 구간이 없으면 false (그 사유는 why: 사유 이름과 로그에 붙일 수치).
    private bool LoadSparseBlocks(GameSettings game, SparseKeep keep, out (string Reason, string? Detail) why)
    {
        why = default;
        GameSettings? window = SparseWindowOf(game);
        if (!(window is { } w ? RefreshSparseWindowList(game, w, keep.List) : RefreshKeepList(game, keep.List)))
        {
            why = ("keep list unavailable", "the planned prewarm list could not be read");
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
            keep.Known = new bool[blocks.Count];
        }

        if (keep.Blocks.Count > 0)
        {
            return true;
        }

        // 예열 선택에 스파스 구간이 없는 사유: 예열이 스파스 범위를 메모리 예열에서 뺐는가 (예산 판정), 아니면 예열 목록에 스파스 파일이 원래 없는가
        string detail = $"{keep.List.Ordered.Count} ranges, {keep.List.Ordered.Sum(o => o.Length) >> 20} MiB in the keep list";
        why = window is null && CoveredPart(game).SkipSparse ? ("sparse files left out of the memory prewarm", detail) : ("no sparse range in the keep list", detail);
        return false;
    }

    // 처음 예열이 스파스 범위를 넣어 읽은 창 (window, 스파스 표식 없음) 의 유지 목록: 그 뒤의 사다리·보충·표본은 스파스를 뺀 읽은 범위 (CoveredPart) 를 보므로 스파스 유지만 이 창을 따로 봅니다.
    // 창 밖의 스파스는 예열하지 않았으므로 읽지 않습니다. 입력이 바뀌지 않는 동안은 다시 계산하지 않음 (RefreshKeepList 와 같음).
    private bool RefreshSparseWindowList(GameSettings game, GameSettings window, KeepList keep)
    {
        string? input = _env.ListStamp(window);
        string? stamp = input is null ? null : $"{input}|{window.MaxStreamMib}|sparse window";
        if (stamp is not null && stamp == keep.Stamp && keep.All.Count > 0)
        {
            return true;
        }

        if (LadderReads(window) is not { Count: > 0 } all)
        {
            return false;
        }

        keep.Stamp = stamp;
        keep.All = all;
        keep.Ordered = _solar.ImportanceOrder(game, all);
        return true;
    }

    // 스파스 유지가 회차를 건너뛰거나 끝낸 사유를 사유가 바뀔 때만 한 줄 남기고 (플레이당), count 이면 끝 줄의 사유별 집계에 더합니다 (끝내는 사유는 그 줄 하나로 충분해 세지 않음).
    private void NoteSparseSkip(GameSettings game, SparseKeep keep, string reason, string? detail = null, bool count = true)
    {
        if (count)
        {
            keep.Skips[reason] = keep.Skips.GetValueOrDefault(reason) + 1;
        }

        if (reason != keep.LastSkip)
        {
            keep.LastSkip = reason;
            _log.Info($"{game.Name}: sparse blocks kept while playing skipped ({keep.Exe}: {reason}{(detail is null ? string.Empty : $", {detail}")})");
        }
    }

    /// 한 회차: 스파스 블록을 맨 앞 (가장 중요한 블록) 부터 이어진 블록 묶음 (최대 SparseGroupBlocks 개) 단위로 버퍼드 읽기로 읽습니다. 가용 메모리가 붙들기의 하한 아래면 건너뛰고, 묶음마다 다시 봐서 읽는 도중 아래로 내려가면 그 회차를 끝냅니다 (밀려나는 중인 것을 디스크에서 다시 읽지 않기 위함).
    /// 디스크로 판정한 읽기는 붙들기의 다시 붙들기와 같은 조건에서만 허용합니다: 로딩 판정이 로딩이 아니고 게임 드라이브 대기열이 0 일 때, 회차 KeepDiskReadCapBytes 와 이 유지만의 플레이 합계 SparsePlayDiskCapBytes (다시 붙들기의 합계와 따로 셈) 안에서. 조건은 묶음마다 다시 봅니다.
    /// 허용되면 묶음을 모두 읽은 뒤 판정합니다: SparseDiskReadSeconds 를 넘은 블록은 그 블록만, 블록마다는 기준 안인데 걸린 시간의 합이 SparseGroupSeconds 를 넘은 묶음은 묶음 전체 (최대 1 MiB) 를 디스크에서 읽은 것으로 세고, 센 양이 회차 상한에 닿으면 그 회차만 끝냅니다 (다음 회차가 맨 앞부터 다시 읽되 앞은 메모리라 빨리 지나가 더 나아감).
    /// 허용되지 않으면 (상한이 0) 앞 회차가 읽어 메모리에 있다고 확인한 블록 (SparseKeep.Known) 만 읽고 기준을 넘는 첫 블록에서 끝냅니다 (그 블록은 읽은 것이라 디스크로 센 것으로 기록): 확인하지 않은 블록은 읽지 않으므로 로딩 중 한 회차가 디스크에 올리는 것은 밀려난 블록 하나 (64 KiB) 입니다.
    /// 읽기 도구는 캐시 관리자의 미리 읽기를 끄고 (RandomAccess) 열어 읽은 만큼만 디스크로 갑니다. 읽는 스레드의 I/O 우선순위만 이 호출 안에서 낮추고 CPU 우선순위는 보통으로 둡니다: CPU 우선순위가 낮으면 게임 스레드에 밀려 메모리 복사가 길어져 디스크 읽기로 잘못 셉니다.
    /// 디스크로 센 읽기는 서비스 자신의 읽기로 기록해 (SelfReadLog, ServiceDiskReads) 다음 집계와 로딩 판정이 게임의 읽기로 세지 않게 합니다. 읽기 중재 대여 (양보 중이면 건너뜀) 와 초 단위 기록 표시는 다른 유지 회차와 같습니다.
    /// 읽는 동안 _touchSerial 은 잡지 않습니다: 버퍼드 읽기는 이 프로세스의 하드 폴트 수에 잡히지 않으므로 (위 설명) 다른 만지기의 경계 판정에 섞이지 않고, 디스크로 간 읽기를 기다리며 붙들기 점검을 막을 까닭이 없습니다.
    private SparseRoundEnd KeepSparseRound(GameSettings game, SparseKeep keep, CancellationToken stop, ref bool noted)
    {
        if (!_solar.Enabled)
        {
            NoteSparseSkip(game, keep, "standby ladder is off");
            return SparseRoundEnd.Skipped;
        }

        if (Relocating(game.Name))
        {
            NoteSparseSkip(game, keep, "layout relocation is running");
            return SparseRoundEnd.Skipped;
        }

        if (_env.OpenBlockReader is not { } open)
        {
            NoteSparseSkip(game, keep, "no block reader");
            return SparseRoundEnd.Skipped;
        }

        bool recorded;
        lock (_gate)
        {
            recorded = _last.ContainsKey(game.Name) || _warmAtServiceStart.Contains(game.Name) || _solar.HasMark(game.Name); // 메모리에 둔 목록이 있는가
        }

        if (!recorded)
        {
            NoteSparseSkip(game, keep, "no prewarm record of a list in memory");
            return SparseRoundEnd.Skipped;
        }

        long availMib = _env.AvailableRamMib();
        if (availMib < HoldMinFloorMib)
        {
            NoteSparseSkip(game, keep, "low memory", $"available {availMib} MiB < floor {HoldMinFloorMib} MiB");
            return SparseRoundEnd.Skipped;
        }

        bool listed;
        (string Reason, string? Detail) why;
        using (new LowPriorityReader(_env)) // 목록을 다시 계산하는 일은 낮은 CPU 우선순위로 (읽기 구간과 따로)
        {
            listed = LoadSparseBlocks(game, keep, out why);
        }

        if (!listed)
        {
            NoteSparseSkip(game, keep, why.Reason, why.Detail);
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
            NoteSparseSkip(game, keep, "disk read lease refused");
            return SparseRoundEnd.Skipped;
        }

        lease?.NoteReadIssue("play sparse", 0, false); // 양보 진단 로그에 이 단계가 보이게
        using var activity = ServiceActivity.Begin(ServiceOp.Keep); // 초 단위 기록의 동작 표시
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lease?.Token ?? default, stop);
        using IBlockReader reader = open();
        IReadOnlyList<ReadOp> blocks = keep.Blocks;
        int count = (int)Math.Min(blocks.Count, HoldTargetBytes(availMib, HoldMinFloorMib) / LoadBoundaries.Block); // 읽는 양은 붙들기의 목표와 같은 식으로 지금 가용 메모리에 맞춰 앞쪽 (가장 중요한 부분) 만
        bool[] known = keep.Known;
        long roundDisk = 0;
        List<ReadOp> diskReads = []; // 디스크로 센 블록 (서비스 자신의 읽기 기록용)
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        SparseRoundEnd end = SparseRoundEnd.Completed;
        bool deferred = false;
        string? heldBack = null; // 디스크 읽기를 허용하지 않은 첫 사유 (읽은 블록이 없는 회차의 사유)
        string? skipWhy = null; // 읽는 도중 회차가 끝난 사유 (Skipped 일 때)
        int readBlocks = 0;
        int failedBlocks = 0;
        long sincePause = 0;
        _ = _env.SetReadIoLow(true);
        try
        {
            for (int i = 0; i < count && end == SparseRoundEnd.Completed;)
            {
                if (_env.AvailableRamMib() < HoldMinFloorMib)
                {
                    end = SparseRoundEnd.Skipped; // 읽는 도중 하한 아래로 내려감: 붙들기처럼 묶음마다 봄
                    skipWhy = "memory fell below the floor during the round";
                    break;
                }

                string? blockedBy = HoldAgainBlocker(game); // 묶음마다 다시 봄: 읽는 도중 로딩이 시작될 수 있음
                long roundCap = blockedBy is not null ? 0 : Math.Min(KeepDiskReadCapBytes, Math.Max(0, SparsePlayDiskCapBytes - keep.DiskBytes));
                deferred |= roundCap == 0;
                if (roundCap == 0)
                {
                    heldBack ??= blockedBy ?? "disk reads of the play spent";
                }

                int to = SparseGroupEnd(blocks, i, count);
                long groupBytes = 0;
                if (roundCap == 0)
                {
                    for (; i < to && end == SparseRoundEnd.Completed; i++)
                    {
                        if (!known[i])
                        {
                            continue; // 메모리에 있다고 확인하지 않은 블록은 디스크 읽기가 허용되지 않는 회차에서는 읽지 않음
                        }

                        if (linked.IsCancellationRequested)
                        {
                            end = stop.IsCancellationRequested ? SparseRoundEnd.Canceled : SparseRoundEnd.Skipped; // 읽기 중재가 이 회차만 취소함 (게임 끝·다른 게임 시작): 다음 회차에 새 대여로
                            skipWhy = "disk read lease canceled the round";
                            break;
                        }

                        ReadOp block = blocks[i];
                        groupBytes += block.Length;
                        if (reader.Read(block.Path, block.Offset, (int)block.Length) is not { } seconds)
                        {
                            failedBlocks++;
                            continue; // 열 수 없거나 읽기 실패: 이 회차에서 그 파일은 건너뜀
                        }

                        readBlocks++;
                        keep.Reached = Math.Max(keep.Reached, i + 1);
                        if (seconds > SparseDiskReadSeconds(block.Length))
                        {
                            diskReads.Add(block);
                            roundDisk += block.Length;
                            end = SparseRoundEnd.DiskStop; // 밀려난 블록: 그 블록은 이미 읽었으므로 센 뒤 이 회차를 끝냄
                        }
                    }
                }
                else
                {
                    double sum = 0;
                    long readBytes = 0;
                    long slowBytes = 0; // 블록 기준을 넘은 블록의 바이트
                    List<ReadOp> read = [];
                    for (; i < to; i++)
                    {
                        if (linked.IsCancellationRequested)
                        {
                            end = stop.IsCancellationRequested ? SparseRoundEnd.Canceled : SparseRoundEnd.Skipped;
                            skipWhy = "disk read lease canceled the round";
                            break;
                        }

                        ReadOp block = blocks[i];
                        groupBytes += block.Length;
                        if (reader.Read(block.Path, block.Offset, (int)block.Length) is not { } seconds)
                        {
                            failedBlocks++;
                            continue;
                        }

                        readBlocks++;
                        known[i] = true; // 읽었으므로 (디스크에서 읽었어도) 지금은 메모리에 있음
                        keep.Reached = Math.Max(keep.Reached, i + 1);
                        read.Add(block);
                        readBytes += block.Length;
                        sum += seconds;
                        slowBytes += seconds > SparseDiskReadSeconds(block.Length) ? block.Length : 0;
                    }

                    bool sumSlow = sum > SparseGroupSeconds(readBytes);
                    if (end == SparseRoundEnd.Completed && (slowBytes > 0 || sumSlow))
                    {
                        diskReads.AddRange(read); // 서비스 자신의 읽기 기록에는 묶음 전체를 남김 (디스크 읽기가 없던 범위를 남겨도 게임의 읽기 집계에서 뺄 것이 없을 뿐임)
                        roundDisk += sumSlow ? readBytes : slowBytes; // 센 양: 누적 기준을 넘었으면 묶음 전체, 아니면 블록 기준을 넘은 블록만
                        if (roundDisk >= roundCap)
                        {
                            end = SparseRoundEnd.DiskStop;
                        }
                    }
                }

                sincePause += groupBytes;
                if (end == SparseRoundEnd.Completed && i < count && sincePause >= SparseBatchBytes)
                {
                    sincePause = 0;
                    _env.Sleep(SparseBatchPause, linked.Token); // 16 MiB 마다: 게임 스레드에 양보
                }
            }
        }
        finally
        {
            _ = _env.SetReadIoLow(false);
            SelfReadLog.Note(diskReads, startedUtc, DateTimeOffset.UtcNow); // 디스크 읽기 집계가 이 읽기를 게임의 읽기로 세지 않게
            NoteServiceRead(game, roundDisk); // 로딩 판정이 서비스 자신의 읽기를 게임의 읽기에서 뺌
            keep.DiskBytes += roundDisk;
        }

        keep.FailedReads += failedBlocks;
        if (end == SparseRoundEnd.Skipped)
        {
            NoteSparseSkip(game, keep, skipWhy ?? "round skipped");
        }

        if (end != SparseRoundEnd.Canceled && end != SparseRoundEnd.Skipped)
        {
            keep.Rounds++;
            keep.DeferredRounds += deferred ? 1 : 0;
            if (readBlocks > 0)
            {
                keep.LastSkip = null; // 읽은 회차가 나오면 같은 사유가 다시 생길 때 한 줄을 또 남김
            }
            else
            {
                // 목록은 있는데 한 블록도 읽지 못한 회차: 디스크 읽기를 허용하지 않아 메모리에 있다고 확인한 블록이 없었거나, 모든 읽기가 실패했거나, 목록에서 읽을 양이 0 이었음
                string reason = failedBlocks > 0 ? "nothing read: block reads failed" : count == 0 ? "nothing read: no room above the floor" : $"nothing read: {heldBack ?? "no block to read"}";
                NoteSparseSkip(game, keep, reason, failedBlocks > 0 ? $"{failedBlocks} reads failed (a file could not be opened or read)" : null);
            }
        }

        return end;
    }
}
