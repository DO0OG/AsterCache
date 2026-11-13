using System.ComponentModel;
using AsterCache.Core.Apply;
using AsterCache.Core.Logging;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Prefetch;

namespace AsterCache.Service;

/// Prewarmer 의 파일 목록 예열 (WarmFiles) 과 읽기 묶음 실행.
public sealed partial class Prewarmer
{
    /// 게임 설치 폴더 (origin_root) 아래의 지정한 파일들을 순서대로 파일 캐시로 읽습니다 (챔피언 선택 선읽기 같은 임의 목록). 읽은 바이트를 돌려주고, 읽지 않았으면 0.
    /// 경로는 정규화한 뒤 origin_root 아래의 것만 (".."·다른 드라이브·루트 밖 링크는 건너뜀), 이미 파일 캐시에 있는 파일은 예산을 쓰지 않고 건너뜁니다.
    /// 예산은 budgetBytes 와 공유 RAM 예산 (예열 예산, 가용 RAM - 여유 - 이미 올린 양) 중 작은 쪽. 다른 예열이 돌고 있거나 같은 디스크에서 재배치가 시작했으면 읽지 않습니다. ct 로 멈춥니다 (예외를 내지 않음).
    public long WarmFiles(GameSettings g, IReadOnlyList<string> paths, long budgetBytes, CancellationToken ct)
    {
        if (budgetBytes <= 0 || paths.Count == 0 || ct.IsCancellationRequested)
        {
            return 0;
        }

        if (PolicyBlocked(g, "warm files"))
        {
            return 0;
        }

        if (!PlanGuards.Inside(g.OriginRoot, g.AllowedRoots, RealPaths.Real))
        {
            LogSkipped(g.Name, "outside allowed roots", $"{g.Name}: warm files skipped (origin_root resolves outside allowed roots)");
            return 0;
        }

        if (DriveHasRunningGame(g))
        {
            LogSkipped(g.Name, "game running", () => $"{g.Name}: warm files skipped (a game is running on this drive){BlockerNote(g)}");
            return 0;
        }

        var ops = new List<ReadOp>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            if (ct.IsCancellationRequested)
            {
                return 0;
            }

            if (FileWarm.Allowed(g.OriginRoot, path, RealPaths.Real) is not { } full || !seen.Add(full))
            {
                continue;
            }

            try
            {
                var file = new FileInfo(full);
                ReadOp op = new(full, 0, file.Exists ? file.Length : 0);
                if (op.Length > 0 && (file.Attributes & FileAttributes.ReparsePoint) == 0)
                {
                    ops.Add(op);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 열 수 없는 파일은 건너뜀
            }
        }

        if (ops.Count == 0)
        {
            return 0;
        }

        if (!Monitor.TryEnter(_run))
        {
            LogSkipped(g.Name, "another prewarm", $"{g.Name}: warm files skipped (another prewarm is running)");
            return 0;
        }

        try
        {
            using IDisposable work = TrackGameWork(g.Name);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct); // 같은 디스크 게임 시작 (NoteGameStart) 이 끊을 수 있게 호출자 ct 와 연결
            if (!TryMarkReading(g.Name, () =>
            {
                (_coldReading, _warmStop) = (g.Name, stop);
                _driveObservedAt = _now();
            }))
            {
                LogSkipped(g.Name, RelocationRunning, $"{g.Name}: warm files skipped ({RelocationRunning})");
                return 0;
            }

            try
            {
                // 표본 읽기도 읽기 표시 안에서 (재배치가 시작했으면 위에서 이미 막힘). 표시를 세운 뒤 게임이 떴으면 NoteGameStart 와 겹쳐도 놓치지 않게 다시 봄.
                if (DriveHasRunningGame(g))
                {
                    LogSkipped(g.Name, "game running", () => $"{g.Name}: warm files skipped (a game is running on this drive){BlockerNote(g)}");
                    return 0;
                }

                var uncached = new List<ReadOp>();
                foreach (ReadOp op in ops)
                {
                    bool? cached = FileCached(g, op, stop.Token);
                    if (cached is null)
                    {
                        return 0;
                    }

                    if (!cached.Value)
                    {
                        uncached.Add(op);
                    }
                }

                ops = uncached;
                long held = ColdMib(null);
                long physMib = PhysCapMib();
                long ramMib = Math.Min(_env.AvailableRamMib() - g.Prewarm.MinFreeRamMib - held, physMib - held);
                long budget = Math.Min(budgetBytes, Math.Max(0, ramMib) << 20);
                ops = PrefetchStreamer.Head(ops, budget);
                if (ops.Count == 0 || stop.IsCancellationRequested)
                {
                    return 0;
                }

                InvalidateResidencyProbe(g.Name);
                (StreamStats s, bool complete) = ReadFileBatches(g, ops, stop);
                _log.Info($"{g.Name}: warm files {(complete ? "done" : "cancelled")}: {s.Bytes >> 20} MiB of {ops.Sum(o => o.Length) >> 20} MiB ({ops.Count} files) in {s.Seconds:F2} s, {s.Errors} errors");
                return s.Bytes;
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or UnauthorizedAccessException or Win32Exception)
            {
                _log.Warn($"{g.Name}: warm files stopped: {e.GetType().Name}");
                return 0;
            }
            finally
            {
                (_coldReading, _warmStop) = (null, null);
            }
        }
        finally
        {
            Monitor.Exit(_run);
        }
    }

    // 파일 목록을 FolderWarm.BatchBytes 묶음으로 읽되 묶음 경계마다 공통 실행 판정을 다시 봅니다 (시작 알림을 놓친 등록 exe 도 잡도록). 판정이 참이면 취소합니다.
    private (StreamStats Stats, bool Complete) ReadFileBatches(GameSettings g, List<ReadOp> ops, CancellationTokenSource stop)
    {
        StreamStats sum = new(0, 0, 0, 0);
        int at = 0;
        long taken = 0; // ops[at] 에서 이미 묶음에 넣은 길이 (큰 파일은 묶음 경계에서 나눔)
        while (at < ops.Count)
        {
            if (RunningBlocksRead(g) || ExternalLoadWhilePaused(g, stop.Token) || DiskSampleUnknown(g))
            {
                stop.Cancel();
            }

            if (stop.IsCancellationRequested)
            {
                return (sum with { Aborted = true }, false);
            }

            List<ReadOp> batch = [];
            long size = 0;
            while (at < ops.Count && size < FolderWarm.BatchBytes)
            {
                ReadOp op = ops[at];
                long take = Math.Min(op.Length - taken, FolderWarm.BatchBytes - size);
                batch.Add(op with { Offset = op.Offset + taken, Length = take });
                size += take;
                taken += take;
                if (taken >= op.Length)
                {
                    (at, taken) = (at + 1, 0);
                }
            }

            using DiskReadCoordinator.Lease? lease = _coordinator?.TryBegin(g.OriginRoot, "warm files", stop.Token);
            if (_coordinator is not null && (lease is null || !lease.IsValid))
            {
                return (sum with { Aborted = true }, false);
            }

            (StreamStats s, bool complete) = _env.FileRead(batch, lease?.Token ?? stop.Token);
            sum = sum with
            {
                Bytes = sum.Bytes + s.Bytes,
                Requests = sum.Requests + s.Requests,
                Errors = sum.Errors + s.Errors,
                Seconds = sum.Seconds + s.Seconds,
                Aborted = sum.Aborted || s.Aborted,
                RequestedBytes = sum.RequestedBytes + s.RequestedBytes,
                HintAcceptedBytes = sum.HintAcceptedBytes + s.HintAcceptedBytes,
            };
            if (!complete)
            {
                return (sum, false);
            }
        }

        // 마지막 묶음을 다 읽은 뒤의 취소·중단은 완료로 치지 않음
        return (sum, !stop.IsCancellationRequested && !sum.Aborted);
    }
}
