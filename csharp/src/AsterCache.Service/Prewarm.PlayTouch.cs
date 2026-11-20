using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 가 게임이 실행되는 동안 그 게임이 예열해 둔 캐시의 앞쪽을 메모리 안에서 다시 만져 유지하는 곳.
/// 경기 중에도 게임이 새로 읽는 양 (분당 0.8~1.7 GiB) 에 밀려 예열한 페이지가 7~14 분 안에 대기 목록에서 빠지므로, 시작 만지기 (TouchAtGameStart) 한 번으로는 경기 중반에 이미 없습니다.
/// 범위·순서는 시작 만지기와 같고 (Solar.TouchFront: 느린 조각, 곧 캐시에 없는 곳에서 멈추고 그 뒤는 읽지 않음) 디스크는 읽지 않으며, 빠진 부분은 게임이 끝난 뒤 재예열에 맡깁니다.
public sealed partial class Prewarmer
{
    /// 게임이 실행되는 동안 캐시를 다시 만지는 주기: 배경 양보가 세션 중 유지하는 주기 (BackgroundYield.MaintainEvery) 와 같은 2 분. 배경 프로세스가 양보한 메모리를 게임과 다른 프로그램이 다시 쓰는 속도가 그 주기로 재어졌으므로 그에 맞춰 만집니다.
    public static readonly TimeSpan PlayTouchEvery = BackgroundYield.MaintainEvery;

    /// 한 번에 만지는 양의 상한: 그때 사용 가능한 물리 메모리의 이 비율. 대기 목록은 만진 양과 두 주기 사이에 게임이 새로 읽는 양을 함께 담아야 하는데,
    /// 경기 중 조사 값은 사용 가능 메모리 약 11 GiB 와 분당 최대 1.7 GiB (두 주기 3.4 GiB) 라서 만지는 양이 7.6 GiB 이하여야 합니다. 절반은 그 안 (5.5 GiB) 이고 메모리가 달라져도 같은 비율로 따라갑니다.
    public const double PlayTouchAvailableFraction = 0.5;

    /// 만지는 속도의 상한 (초당): 256 MiB. 5.5 GiB 를 약 20 초에 걸쳐 만져 메모리 대역을 한꺼번에 쓰지 않게 합니다.
    public const long PlayTouchBytesPerSecond = 256L << 20;

    private readonly HashSet<string> _playTouching = new(StringComparer.OrdinalIgnoreCase); // 실행 중 캐시 유지가 도는 게임 (한 게임에 하나만), _gate 로 보호
    private readonly CancellationTokenSource _playStop = new(); // 서비스 종료 때 유지를 멈추는 신호 (Shutdown 이 취소)

    internal void StopPlayTouch() => _playStop.Cancel();

    /// 시작 만지기가 끝난 뒤 같은 작업 안에서 부릅니다: 등록 실행 파일이 하나도 실행 중이 아닐 때까지 PlayTouchEvery 마다 그 게임 목록의 앞쪽을 메모리 안에서 다시 만집니다.
    /// 이미 이 게임의 유지가 돌고 있으면 (런처와 본체가 이어 뜬 경우) 바로 끝납니다. 회차마다 읽기 중재의 시작 스트림과 같은 대여를 새로 받아 (그 게임의 끝·다른 게임의 시작으로 취소) 돌고, 서비스가 멈추면 기다리던 것도 바로 끝납니다.
    private void KeepCacheWhilePlaying(GameSettings started, string exe)
    {
        lock (_gate)
        {
            if (!_playTouching.Add(started.Name))
            {
                return;
            }
        }

        int rounds = 0;
        bool noted = false; // 이 유지가 로그를 한 줄이라도 남겼는가 (끝 줄을 남길지)
        string? lastSkip = null;
        string ended = string.Empty; // 끝 줄에 붙일 사유
        try
        {
            CancellationToken stop = _playStop.Token;
            while (true)
            {
                _env.Sleep(PlayTouchEvery, stop);
                if (stop.IsCancellationRequested || _games.FirstOrDefault(g => string.Equals(g.Name, started.Name, StringComparison.OrdinalIgnoreCase)) is not { } game || !_env.GameRunning(game))
                {
                    break; // 서비스가 멈추거나 설정에서 빠지거나 등록 실행 파일이 모두 끝남
                }

                (string Reason, string Detail)? skip = TouchWhilePlaying(game, exe, out bool touched, out bool empty);
                if (touched)
                {
                    (rounds, noted, lastSkip) = (rounds + 1, true, null);
                    if (empty)
                    {
                        ended = " (nothing left in memory)"; // 같은 줄을 되풀이하고 느린 조각을 디스크에서 읽지 않게 이 회차에서 끝냄: 빠진 것은 게임이 끝난 뒤 재예열에 맡김
                        break;
                    }
                }
                else if (skip is { } why && why.Reason != lastSkip)
                {
                    _log.Info($"{game.Name}: cache keeping while playing skipped ({exe}: {why.Detail})"); // 사유가 바뀔 때만 한 줄
                    (noted, lastSkip) = (true, why.Reason);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _ = _playTouching.Remove(started.Name);
            }

            if (noted)
            {
                _log.Info($"{started.Name}: cache keeping ended after {rounds} rounds{ended}");
            }
        }
    }

    /// 한 회차. 시작 만지기와 같은 게이트 (사다리 켜짐, 재배치 중 아님, 메모리에 둔 목록, 만질 것) 를 지나야 하고, 사용 가능 메모리가 min_free_ram_mib 아래면 건너뜁니다.
    /// 읽는 스레드는 I/O 우선순위와 CPU 우선순위를 이 호출 안에서만 낮춥니다. 스레드 배경 모드는 페이지의 메모리 우선순위까지 낮춰 만진 페이지가 낮은 우선순위 대기 목록으로 가므로 쓰지 않습니다.
    /// 로그를 남길 사유로 건너뛰면 그 사유 (같은 사유가 이어지는지 가르는 값, 로그에 붙는 수치) 를 돌려주고, 로그 없이 건너뛰거나 만졌으면 null. empty: 목록이 메모리에 하나도 남아 있지 않아 만진 것이 없음.
    private (string Reason, string Detail)? TouchWhilePlaying(GameSettings game, string exe, out bool touched, out bool empty)
    {
        touched = empty = false;
        if (!_solar.Enabled || Relocating(game.Name) || _solar.HasNothingToTouch(game.Name))
        {
            return null;
        }

        lock (_gate)
        {
            if (!(_last.ContainsKey(game.Name) || _warmAtServiceStart.Contains(game.Name) || _solar.HasMark(game.Name))) // 메모리에 둔 목록이 없음
            {
                return null;
            }
        }

        long availMib = _env.AvailableRamMib();
        if (availMib < game.Prewarm.MinFreeRamMib)
        {
            return ("low memory", $"available {availMib} MiB < reserve {game.Prewarm.MinFreeRamMib} MiB");
        }

        if (LadderReads(CoveredPart(game)) is not { Count: > 0 } all)
        {
            return null;
        }

        long cap = (long)(availMib * PlayTouchAvailableFraction) << 20;
        IReadOnlyList<ReadOp> ordered = all;
        if (_env.RankedRows(game) is { } ranked)
        {
            ordered = PlayRanking.Reorder(all, ranked); // 순위 목록에 든 블록을 순위 순으로 앞에: 자르는 한도 안에 경기 중 지연이 큰 블록이 먼저 들게 (순위 밖 범위는 지금 순서로 뒤에)
        }

        IReadOnlyList<ReadOp> reads = PrefetchStreamer.Head(ordered, cap); // 목록 앞쪽 (가장 중요한 부분) 만
        using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "play touch", default, game.OriginRoot, allowWhileYielded: true, preserveGameKey: game.Name);
        if (_coordinator is not null && (lease is null || !lease.IsValid))
        {
            return ("disk yielded", "disk yielded");
        }

        lease?.NoteReadIssue("play touch", 0, false); // 양보 진단 로그에 이 단계가 보이게
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lease?.Token ?? default, _playStop.Token);
        Thread reader = Thread.CurrentThread;
        ThreadPriority cpuBefore = reader.Priority;
        reader.Priority = ThreadPriority.BelowNormal; // 게임의 CPU 를 다투지 않게: 이 스레드에서 되돌림
        _ = _env.SetReadIoLow(true); // 시작 만지기와 같이 이 호출 안에서만 낮은 I/O 우선순위
        try
        {
            bool none = false;
            touched = _solar.TouchFront(game, reads, linked.Token, exe, ThrottledTouch, keeping: true, noneInMemory: () => none = true) is not null;
            if (none && !ReferenceEquals(ordered, all))
            {
                // 순위 순서의 맨 앞이 메모리에 없음 (순위 목록이 마지막 예열 뒤에 바뀌었거나 그 블록이 빠짐): 목록 전체를 잃었다고 보지 않고 순위 없는 순서로 한 번 더 시도
                none = false;
                touched = _solar.TouchFront(game, PrefetchStreamer.Head(all, cap), linked.Token, exe, ThrottledTouch, keeping: true, noneInMemory: () => none = true) is not null;
            }

            empty = none;
        }
        finally
        {
            _ = _env.SetReadIoLow(false);
            reader.Priority = cpuBefore;
        }

        return null;
    }

    /// 범위를 StandbyTouch.ViewBytes 씩 끊어 만지고, 한 덩어리를 만지는 데 걸린 시간이 PlayTouchBytesPerSecond 로 만졌을 때보다 짧으면 그 차이만큼 쉽니다.
    /// 디스크에서 읽힌 양은 덩어리를 넘어 합쳐서 StandbyTouch.DiskReadStopBytes 를 넘으면 멈춥니다 (한 번에 만질 때와 같은 한도). 취소·멈춤 사유는 덩어리의 결과를 그대로 따릅니다.
    internal TouchResult ThrottledTouch(IReadOnlyList<ReadOp> ranges, CancellationToken ct)
    {
        TouchResult total = new(0, 0, 0, 0, TouchEnd.Completed);
        foreach (List<ReadOp> batch in Batches(ranges, StandbyTouch.ViewBytes))
        {
            if (ct.IsCancellationRequested)
            {
                return total with { End = TouchEnd.Canceled };
            }

            TouchResult r = _env.TouchRanges(batch, ct);
            double rest = r.End == TouchEnd.Completed ? r.Bytes / (double)PlayTouchBytesPerSecond - r.Seconds : 0;
            if (rest > 0)
            {
                _env.Sleep(TimeSpan.FromSeconds(rest), ct);
            }

            total = new TouchResult(total.Bytes + r.Bytes, total.Seconds + r.Seconds + Math.Max(0, rest), total.SlowPieces + r.SlowPieces, total.Errors + r.Errors, r.End,
                total.DiskBytes + r.DiskBytes, total.SkippedBytes + r.SkippedBytes);
            if (total.End == TouchEnd.Completed && total.DiskBytes > StandbyTouch.DiskReadStopBytes)
            {
                return total with { End = TouchEnd.NeedsDiskReads };
            }

            if (total.End != TouchEnd.Completed)
            {
                break;
            }
        }

        return total;
    }

    // 범위를 순서대로 batchBytes 씩 끊습니다 (걸친 범위는 잘라서).
    private static IEnumerable<List<ReadOp>> Batches(IReadOnlyList<ReadOp> ranges, long batchBytes)
    {
        var batch = new List<ReadOp>();
        long room = batchBytes;
        foreach (ReadOp range in ranges)
        {
            for (long done = 0; done < range.Length;)
            {
                long n = Math.Min(room, range.Length - done);
                batch.Add(new ReadOp(range.Path, range.Offset + done, n));
                (done, room) = (done + n, room - n);
                if (room == 0)
                {
                    yield return batch;
                    batch = [];
                    room = batchBytes;
                }
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }
}
