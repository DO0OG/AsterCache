using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// Prewarmer 가 게임이 실행되는 동안 그 게임이 예열해 둔 캐시의 앞쪽을 메모리 안에서 다시 만져 유지하는 곳.
/// 경기 중에도 게임이 새로 읽는 양 (분당 0.8~1.7 GiB) 에 밀려 예열한 페이지가 7~14 분 안에 대기 목록에서 빠지므로, 시작 만지기 (TouchAtGameStart) 한 번으로는 경기 중반에 이미 없습니다.
/// 범위·순서는 시작 만지기와 같고 (Solar.TouchFront: 중요도 순서의 앞에서부터, 캐시에 없는 곳 곧 이 프로세스의 디스크 읽기 수가 허용치를 넘는 경계에서 멈추고 그 뒤는 읽지 않음) 디스크는 읽지 않으며, 빠진 부분은 게임이 끝난 뒤 재예열에 맡깁니다.
/// 게임이 메모리를 가져가면 대기 목록의 오래된 쪽부터 밀려나므로, 사다리·시작 만지기가 덜 중요한 것부터 만들어 둔 순서 (Solar.ImportanceOrder) 대로 살아남은 것은 중요도 순 목록의 앞부분이고 이 회차는 그 앞부분의 경계까지를 다시 만집니다.
/// 목록은 플레이마다 한 번만 계산하고 입력 파일이 바뀐 때만 다시 계산합니다.
public sealed partial class Prewarmer
{
    /// 게임이 실행되는 동안 캐시를 다시 만지는 주기: 배경 양보가 세션 중 유지하는 주기 (BackgroundYield.MaintainEvery) 와 같은 2 분. 배경 프로세스가 양보한 메모리를 게임과 다른 프로그램이 다시 쓰는 속도가 그 주기로 재어졌으므로 그에 맞춰 만집니다.
    public static readonly TimeSpan PlayTouchEvery = BackgroundYield.MaintainEvery;

    /// 한 번에 만지는 양의 상한: 그때 사용 가능한 물리 메모리의 이 비율. 대기 목록은 만진 양과 두 주기 사이에 게임이 새로 읽는 양을 함께 담아야 하는데,
    /// 경기 중 조사 값은 사용 가능 메모리 약 11 GiB 와 분당 최대 1.7 GiB (두 주기 3.4 GiB) 라서 만지는 양이 7.6 GiB 이하여야 합니다. 절반은 그 안 (5.5 GiB) 이고 메모리가 달라져도 같은 비율로 따라갑니다.
    public const double PlayTouchAvailableFraction = 0.5;

    /// 만지는 속도의 상한 (초당): 256 MiB. 더 빠른 PC 가 메모리 대역을 한꺼번에 쓰지 않게 합니다. 실제 게임 파일 실측에서는 한 패스가 1 GiB 에 약 5 초 (초당 약 200 MiB) 라 페이지를 옮기는 속도가 이 상한보다 먼저 한도가 됩니다.
    public const long PlayTouchBytesPerSecond = 256L << 20;

    /// 회차를 돌리는 하한: 지난 회차 (또는 시작 만지기) 가 끝난 뒤 대기 목록 (우선순위 5~7) 에서 재사용된 양 (MemoryLists.CacheRepurposedBytes 의 증가) 이 이보다 작으면 그 회차를 건너뜁니다 (2 분마다 값만 확인). 값을 읽을 수 없으면 건너뛰지 않습니다.
    /// 근거: 대기 목록에서 밀려난 것이 없으면 유지 대상도 밀려나지 않았고, 두 회차 사이에 잃을 수 있는 양은 그 사이의 재사용량을 넘지 않습니다. 회차는 살아남은 양에 비례해 비싸고 (실제 파일 실측: 1 GiB 한 패스에 CPU 약 1.1~1.3 초, 약 5 초, 회차는 두 패스),
    /// 회차 수는 "재사용 속도 ÷ 이 하한" 으로 정해집니다. 회차가 한 번 돌면 그 사이 새로 들어온 페이지가 유지 대상보다 오래된 쪽에 서므로 다음 구간의 재사용은 그것부터 가져가고, 하한을 올려서 잃는 것은 첫 구간에 덜 중요한 끝에서 하한만큼 (유지 한도 약 4~5 GiB 의 약 4분의 1) 입니다.
    /// 재사용 누계의 경기 중 값은 재지 않았고, 설치 뒤 회차 줄의 건너뛴 횟수로 확인합니다.
    public const long PlayTouchMinReuseBytes = 1L << 30;

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
        int skippedRounds = 0; // 마지막으로 돈 회차 뒤 건너뛴 회차 수 (다음에 도는 회차의 줄에 적음)
        int skippedTotal = 0;
        int emptyRounds = 0; // 메모리에 하나도 없던 회차가 연속으로 몇 번인가
        long? reuseAtLastRound = _env.Lists()?.CacheRepurposedBytes; // 시작 만지기가 끝난 때의 대기 목록 재사용 누계
        var keep = new KeepList(); // 이 플레이의 유지 목록 (입력이 바뀔 때만 다시 계산)
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

                if (reuseAtLastRound is { } reuseBefore && _env.Lists() is { } lists && lists.CacheRepurposedBytes - reuseBefore < PlayTouchMinReuseBytes)
                {
                    (skippedRounds, skippedTotal, noted) = (skippedRounds + 1, skippedTotal + 1, true);
                    continue; // 대기 목록에서 밀려난 것이 (거의) 없음: 유지 대상도 밀려나지 않았으니 다시 만질 까닭이 없음
                }

                (string Reason, string Detail)? skip = TouchWhilePlaying(game, exe, keep, skippedRounds, out bool touched, out bool empty);
                if (touched)
                {
                    (rounds, noted, lastSkip, skippedRounds) = (rounds + 1, true, null, 0);
                    reuseAtLastRound = _env.Lists()?.CacheRepurposedBytes;
                    emptyRounds = empty ? emptyRounds + 1 : 0;
                    if (emptyRounds >= 2)
                    {
                        ended = " (nothing left in memory in two rounds in a row)"; // 같은 줄을 되풀이하고 디스크 읽기가 섞이지 않게 연속 두 회차가 같을 때만 끝냄 (한 번은 잡음일 수 있음): 빠진 것은 게임이 끝난 뒤 재예열에 맡김
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
                _log.Info($"{started.Name}: cache keeping ended after {rounds} rounds{(skippedTotal > 0 ? $" ({skippedTotal} rounds skipped, standby cache reuse under {PlayTouchMinReuseBytes >> 20} MiB since the last round)" : string.Empty)}{ended}");
            }
        }
    }

    /// 한 플레이의 유지 목록: 입력 (계획·트레이스·순위 목록 파일, 부분 예열 범위, 스파스 표시) 이 그대로인 동안 회차마다 다시 계산하지 않고 이 목록을 씁니다.
    private sealed class KeepList
    {
        public string? Stamp; // 목록을 만든 때의 입력 표지 (null: 표지를 읽지 못해 재사용하지 않음)
        public IReadOnlyList<ReadOp> All = []; // 사다리 대상 (읽은 순서)
        public IReadOnlyList<ReadOp> Ordered = []; // All 의 중요도 순서 (순위 목록이 없으면 All 과 같은 객체)
    }

    // 유지 목록이 있으면 입력이 바뀌지 않았는지 보고 (메타데이터 조회뿐), 없거나 바뀌었으면 새로 계산합니다. 계획할 수 없으면 false.
    private bool RefreshKeepList(GameSettings game, KeepList keep)
    {
        GameSettings covered = CoveredPart(game);
        string? input = _env.ListStamp(covered);
        string? stamp = input is null ? null : $"{input}|{covered.MaxStreamMib}|{covered.SkipSparse}";
        if (stamp is not null && stamp == keep.Stamp && keep.All.Count > 0)
        {
            return true;
        }

        if (LadderReads(covered) is not { Count: > 0 } all)
        {
            return false;
        }

        keep.Stamp = stamp;
        keep.All = all;
        keep.Ordered = _solar.ImportanceOrder(game, all);
        return true;
    }

    /// 한 회차. 시작 만지기와 같은 게이트 (사다리 켜짐, 재배치 중 아님, 메모리에 둔 목록, 만질 것) 를 지나야 하고, 사용 가능 메모리가 min_free_ram_mib 아래면 건너뜁니다.
    /// 읽는 스레드는 I/O 우선순위와 CPU 우선순위를 이 호출 안에서만 낮춥니다 (목록을 다시 계산하는 일도 낮은 CPU 우선순위로 돎). 스레드 배경 모드는 페이지의 메모리 우선순위까지 낮춰 만진 페이지가 낮은 우선순위 대기 목록으로 가므로 쓰지 않습니다.
    /// 로그를 남길 사유로 건너뛰면 그 사유 (같은 사유가 이어지는지 가르는 값, 로그에 붙는 수치) 를 돌려주고, 로그 없이 건너뛰었거나 만졌으면 null. empty: 목록이 메모리에 하나도 남아 있지 않아 만진 것이 없음.
    private (string Reason, string Detail)? TouchWhilePlaying(GameSettings game, string exe, KeepList keep, int skippedBefore, out bool touched, out bool empty)
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

        Thread reader = Thread.CurrentThread;
        ThreadPriority cpuBefore = reader.Priority;
        reader.Priority = ThreadPriority.BelowNormal; // 게임의 CPU 를 다투지 않게: 이 스레드에서 되돌림
        try
        {
            if (!RefreshKeepList(game, keep))
            {
                return null;
            }

            IReadOnlyList<ReadOp> all = keep.All;
            IReadOnlyList<ReadOp> ordered = keep.Ordered;
            long cap = (long)(availMib * PlayTouchAvailableFraction) << 20;
            IReadOnlyList<ReadOp> reads = PrefetchStreamer.Head(ordered, cap); // 목록 앞쪽 (가장 중요한 부분) 만
            using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(game.OriginRoot, "play touch", default, game.OriginRoot, allowWhileYielded: true, preserveGameKey: game.Name);
            if (_coordinator is not null && (lease is null || !lease.IsValid))
            {
                return ("disk yielded", "disk yielded");
            }

            lease?.NoteReadIssue("play touch", 0, false); // 양보 진단 로그에 이 단계가 보이게
            using var activity = ServiceActivity.Begin(ServiceOp.Keep); // 초 단위 기록의 동작 표시
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(lease?.Token ?? default, _playStop.Token);
            _ = _env.SetReadIoLow(true); // 시작 만지기와 같이 이 호출 안에서만 낮은 I/O 우선순위
            try
            {
                using IDisposable round = StandbyTouch.Round(); // 이 회차의 Run 여러 번이 디스크 읽기 수의 기준값을 이어 씀
                bool none = false;
                touched = _solar.TouchFront(game, reads, linked.Token, exe, ThrottledTouch, keeping: true, noneInMemory: () => none = true, skippedBefore: skippedBefore) is not null;
                if (none && !ReferenceEquals(ordered, all))
                {
                    // 중요도 순서의 맨 앞이 메모리에 없음 (순위 목록이 마지막 예열 뒤에 바뀌었거나 그 블록이 빠짐): 목록 전체를 잃었다고 보지 않고 순위 없는 순서로 한 번 더 시도
                    none = false;
                    touched = _solar.TouchFront(game, PrefetchStreamer.Head(all, cap), linked.Token, exe, ThrottledTouch, keeping: true, noneInMemory: () => none = true, skippedBefore: skippedBefore) is not null;
                }

                empty = none;
            }
            finally
            {
                _ = _env.SetReadIoLow(false);
            }
        }
        finally
        {
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
                total.DiskBytes + r.DiskBytes, total.SkippedBytes + r.SkippedBytes, total.CpuSeconds + r.CpuSeconds, total.Maps + r.Maps, total.HardFaults + r.HardFaults, r.Reason, total.Checks + r.Checks, TouchErrors.Combine(total.ErrorKinds, r.ErrorKinds));
            if (total.End == TouchEnd.Completed && total.DiskBytes > StandbyTouch.DiskReadStopBytes)
            {
                return total with { End = TouchEnd.NeedsDiskReads, Reason = StopReason.HardFaults };
            }

            if (total.End != TouchEnd.Completed)
            {
                return total;
            }
        }

        TouchResult tail = StandbyTouch.Settle(); // 회차 끝 꼬리 (마지막 조회 뒤 만진 구간) 의 디스크 읽기도 셈
        total = total with { DiskBytes = total.DiskBytes + tail.DiskBytes, HardFaults = total.HardFaults + tail.HardFaults, Checks = total.Checks + tail.Checks };
        return tail.End == TouchEnd.Completed ? total : total with { End = tail.End, Reason = tail.Reason };
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
