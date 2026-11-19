using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;

namespace AsterCache.Service;

/// 단계 스트림 대상을 고르고 멈춤 조건을 판정하는 순수 규칙입니다.
internal static class StageStreamRule
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);
    public static TimeSpan EvidenceWindow => TimeSpan.FromSeconds(LoadBoundaries.WindowSeconds);
    public static readonly TimeSpan QuietToRaise = TimeSpan.FromSeconds(5);
    public const long EvidenceBytes = RuntimeLoadBoundary.MinimumPeakBytes;
    private const long Mib = 1L << 20;

    /// 첫 접근 순 앞부분과 첫 단계 블록을 유지하고 이후 단계 블록은 뺍니다.
    public static List<ReadOp>? FirstStageOps(IReadOnlyList<ReadOp> ops, long headBytes, TraceStageIndex? index, IReadOnlySet<string>? volatilePaths = null)
    {
        if (index is null || index.Stages.Count < 2)
        {
            return null;
        }

        HashSet<string> volatileSet = VolatileSet(volatilePaths);
        var later = Blocks(index.Stages.Skip(1));
        later.ExceptWith(Blocks(index.Stages.Take(1)));

        var result = new List<ReadOp>();
        long headLeft = Math.Max(0, headBytes);
        foreach (ReadOp op in ops)
        {
            if (op.Offset < 0 || op.Length <= 0 || op.Offset > long.MaxValue - op.Length)
            {
                continue;
            }

            long head = Math.Min(op.Length, headLeft);
            if (head > 0 && !volatileSet.Contains(op.Path))
            {
                Add(result, new ReadOp(op.Path, op.Offset, head));
            }

            headLeft -= head;
            long offset = op.Offset + head;
            long end = op.Offset + op.Length;
            if (offset >= end || volatileSet.Contains(op.Path))
            {
                continue;
            }

            while (offset < end)
            {
                long block = offset / LoadBoundaries.Block;
                long nextBlock = block >= long.MaxValue / LoadBoundaries.Block - 1 ? long.MaxValue : (block + 1) * LoadBoundaries.Block;
                long next = Math.Min(end, nextBlock);
                if (!later.Contains((op.Path, block)))
                {
                    Add(result, new ReadOp(op.Path, offset, next - offset));
                }

                offset = next;
            }
        }

        return result;
    }

    /// 현재 시작 목록과 단계 범위의 바이트 교집합을 단계의 첫 읽기 순으로 돌려줍니다.
    public static List<ReadOp> StageOps(IReadOnlyList<ReadOp> ops, TraceStage stage, IReadOnlySet<string>? volatilePaths = null)
    {
        HashSet<string> volatileSet = VolatileSet(volatilePaths);
        var byPath = new Dictionary<string, List<ReadOp>>(StringComparer.OrdinalIgnoreCase);
        foreach (ReadOp op in ops)
        {
            if (op.Offset < 0 || op.Length <= 0 || op.Offset > long.MaxValue - op.Length || volatileSet.Contains(op.Path))
            {
                continue;
            }

            if (!byPath.TryGetValue(op.Path, out List<ReadOp>? fileOps))
            {
                fileOps = [];
                byPath.Add(op.Path, fileOps);
            }

            fileOps.Add(op);
        }

        foreach (List<ReadOp> fileOps in byPath.Values)
        {
            fileOps.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        }

        var result = new List<ReadOp>();
        foreach (TraceEvent range in stage.Ranges)
        {
            if (range.Offset < 0 || range.Length <= 0 || range.Offset > long.MaxValue - range.Length
                || volatileSet.Contains(range.Path) || !byPath.TryGetValue(range.Path, out List<ReadOp>? fileOps))
            {
                continue;
            }

            long rangeEnd = range.Offset + range.Length;
            int at = FirstPossible(fileOps, range.Offset);
            var intersections = new List<(long Start, long End, string Path)>();
            for (; at < fileOps.Count && fileOps[at].Offset < rangeEnd; at++)
            {
                ReadOp op = fileOps[at];
                long start = Math.Max(range.Offset, op.Offset);
                long end = Math.Min(rangeEnd, op.Offset + op.Length);
                if (start < end)
                {
                    intersections.Add((start, end, op.Path));
                }
            }

            intersections.Sort((a, b) => a.Start.CompareTo(b.Start));
            foreach ((long start, long end, string path) in Merge(intersections))
            {
                Add(result, new ReadOp(path, start, end - start));
            }
        }

        return result;
    }

    public static string? LaterSkip(DateTimeOffset now, DateTimeOffset? lastForKey)
    {
        if (lastForKey is not { } last || now >= last + Cooldown)
        {
            return null;
        }

        long seconds = Math.Max(0, (long)Math.Ceiling((last + Cooldown - now).TotalSeconds));
        return $"cooldown {seconds} s";
    }

    public static bool LoadEvidence(long newPidReadBytes) => newPidReadBytes >= EvidenceBytes;

    /// 로드 구간 목록은 시작 뒤 이 시간 안의 블록이라, 이보다 늦게 처리된 시작에는 쓰지 않습니다 (옛 동작으로).
    public static TimeSpan RefillMaxStartAge => LoadRefillPlanner.Window;

    /// 로드 구간 목록으로 흘리기 전 보조 시작 (오류 처리기·짧은 재시작) 을 거르는 짧은 확인: 새 프로세스가 이 시간 안에 이만큼 읽었으면 로드로 봅니다.
    /// 근거: 실제 로드의 첫 10 초 읽기가 3 MiB 였던 사례를 놓치지 않는 값 (그 1/3) 이고, 아무것도 읽지 않는 보조 프로세스와 곧 끝나는 프로세스는 걸립니다. 실측 전에 정한 값입니다.
    public const long RefillEvidenceBytes = 1L << 20;

    public static readonly TimeSpan RefillEvidenceWindow = TimeSpan.FromSeconds(10);

    /// 로드 구간 목록의 크기: 고정 상한과 메모리 게이트가 허용하는 양 (가용 − 예비) 중 작은 값.
    public static long RefillCapBytes(long availableMib, long reserveMib) =>
        Math.Min(LoadRefillPlanner.CapBytes, Math.Max(0, availableMib - reserveMib) * Mib);

    public static bool WantLow(long gameReadDeltaBytes, double seconds) => seconds > 0 && gameReadDeltaBytes / seconds >= DiskActivity.BusyBytesPerSecond;

    /// 이후 exe 의 스트림은 낮음으로 시작해, 게임 읽기가 QuietToRaise 동안 조용했을 때만 보통으로 올리고 다시 바빠지면 바로 낮춥니다.
    /// busy 는 WantLow 결과, quietSince 는 조용해지기 시작한 시각(바쁘면 null). 호출한 쪽이 돌려준 QuietSince 를 다음 호출에 넘깁니다.
    public static (bool Low, DateTimeOffset? QuietSince) NextPriority(bool low, DateTimeOffset? quietSince, bool busy, DateTimeOffset now)
    {
        if (busy)
        {
            return (true, null);
        }

        DateTimeOffset since = quietSince ?? now;
        return (low && now - since < QuietToRaise, since);
    }

    public static bool MemoryStop(long availableMib, long reserveMib, long nextBytes)
    {
        if (nextBytes <= 0)
        {
            return false;
        }

        if (availableMib < reserveMib)
        {
            return true;
        }

        long remainingMib = availableMib - reserveMib;
        return remainingMib <= long.MaxValue / Mib && remainingMib * Mib < nextBytes;
    }

    public static long ReserveMib(GameSettings game) => Math.Max(game.Prewarm.MinFreeRamMib, Prewarmer.GapFillReserveMib);

    public static long SkippedVolatileBytes(IEnumerable<ReadOp> ops, IReadOnlySet<string>? volatilePaths)
    {
        HashSet<string> volatileSet = VolatileSet(volatilePaths);
        return ops.Where(op => volatileSet.Contains(op.Path)).Sum(op => Math.Max(0, op.Length));
    }

    private static HashSet<string> VolatileSet(IReadOnlySet<string>? paths) => paths is null
        ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        : new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);

    private static HashSet<(string Path, long Block)> Blocks(IEnumerable<TraceStage> stages)
    {
        var result = new HashSet<(string Path, long Block)>(new BlockKeyComparer());
        foreach (TraceStage stage in stages)
        {
            foreach (TraceEvent range in stage.Ranges)
            {
                if (range.Offset < 0 || range.Length <= 0 || range.Offset > long.MaxValue - range.Length)
                {
                    continue;
                }

                long first = range.Offset / LoadBoundaries.Block;
                long last = (range.Offset + range.Length - 1) / LoadBoundaries.Block;
                for (long block = first; block <= last; block++)
                {
                    result.Add((range.Path, block));
                }
            }
        }

        return result;
    }

    private static int FirstPossible(IReadOnlyList<ReadOp> ops, long offset)
    {
        int low = 0;
        int high = ops.Count;
        while (low < high)
        {
            int mid = low + (high - low) / 2;
            if (ops[mid].Offset < offset)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        int at = Math.Max(0, low - 1);
        while (at > 0 && ops[at - 1].Offset + ops[at - 1].Length > offset)
        {
            at--;
        }

        return at;
    }

    private static IEnumerable<(long Start, long End, string Path)> Merge(List<(long Start, long End, string Path)> ranges)
    {
        if (ranges.Count == 0)
        {
            yield break;
        }

        long start = ranges[0].Start;
        long end = ranges[0].End;
        string path = ranges[0].Path;
        for (int i = 1; i < ranges.Count; i++)
        {
            (long nextStart, long nextEnd, string nextPath) = ranges[i];
            if (nextStart <= end)
            {
                end = Math.Max(end, nextEnd);
                continue;
            }

            yield return (start, end, path);
            (start, end, path) = (nextStart, nextEnd, nextPath);
        }

        yield return (start, end, path);
    }

    private static void Add(List<ReadOp> result, ReadOp op)
    {
        if (result.Count > 0 && string.Equals(result[^1].Path, op.Path, StringComparison.OrdinalIgnoreCase)
            && result[^1].Offset + result[^1].Length == op.Offset)
        {
            result[^1] = result[^1] with { Length = result[^1].Length + op.Length };
        }
        else
        {
            result.Add(op);
        }
    }

    private sealed class BlockKeyComparer : IEqualityComparer<(string Path, long Block)>
    {
        public bool Equals((string Path, long Block) x, (string Path, long Block) y) => x.Block == y.Block && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Path, long Block) obj) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path), obj.Block);
    }
}
