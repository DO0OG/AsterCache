using AsterCache.Core.Prefetch;

namespace AsterCache.Core.Trace;

/// 트레이스에 남은 대상 프로세스의 파일 읽기 하나.
public readonly record struct GameRead(string Path, DateTimeOffset TimeUtc, long Offset, long Length);

/// 서비스가 스스로 읽은 구간 (SelfReadLog) 과 겹치는 디스크 읽기를 가려냅니다. 관찰 시작 (originUtc) 을 0 으로 한 마이크로초로 시각을 비교합니다.
/// 서비스의 읽기는 시작 시각부터 끝난 뒤 Slack 안에 디스크로 내려갑니다 (매핑 읽기는 호출이 끝난 뒤에도 이어질 수 있음).
/// 같은 시간대에 게임도 같은 구간을 읽은 기록이 있으면 서비스 읽기로 보지 않습니다 (게임이 읽은 블록을 잃지 않는 쪽이 안전).
/// 서비스 구간이 질의 구간 전체를 덮을 때만 서비스 읽기로 봅니다: 일부만 겹치면 나머지는 서비스가 읽지 않은 바이트라 (게임의 큰 읽기가 서비스 구간 끝에 걸친 경우 등) 통째로 빼면 게임 읽기를 잃습니다.
/// 서비스 구간은 4 KiB 페이지 경계로 넓혀 둡니다 (캐시·매핑 읽기는 페이지 단위).
public sealed class SelfReadFilter
{
    public static readonly TimeSpan Slack = TimeSpan.FromSeconds(10);

    private const long Page = 4096;
    private const int CancelCheckMask = 0xFF;

    private readonly Dictionary<string, FileRanges> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationToken _ct;
    private int _calls;

    public SelfReadFilter(DateTimeOffset originUtc, IEnumerable<SelfReadBatch> batches, IEnumerable<GameRead> gameReads, CancellationToken ct = default)
    {
        _ct = ct;
        long Us(DateTimeOffset time) => (long)(time.ToUniversalTime() - originUtc).TotalMicroseconds;
        long slackUs = (long)Slack.TotalMicroseconds;
        var own = new Dictionary<string, List<Range>>(StringComparer.OrdinalIgnoreCase);
        foreach (SelfReadBatch batch in batches)
        {
            long from = Us(batch.StartedUtc);
            long to = Us(batch.EndedUtc) + slackUs;
            foreach (ReadOp op in batch.Ops)
            {
                if (op.Length > 0 && op.Offset >= 0 && op.Offset <= long.MaxValue - op.Length - Page)
                {
                    GetList(own, op.Path).Add(new Range(op.Offset / Page * Page, (op.Offset + op.Length + Page - 1) / Page * Page, from, to));
                }
            }
        }

        var game = new Dictionary<string, List<Range>>(StringComparer.OrdinalIgnoreCase);
        foreach (GameRead read in gameReads)
        {
            if (read.Length > 0 && read.Offset >= 0 && read.Offset <= long.MaxValue - read.Length && own.ContainsKey(read.Path))
            {
                long at = Us(read.TimeUtc);
                GetList(game, read.Path).Add(new Range(read.Offset, read.Offset + read.Length, at, at));
            }
        }

        foreach ((string path, List<Range> ranges) in own)
        {
            _files[path] = new FileRanges(new RangeIndex(ranges), new RangeIndex(game.GetValueOrDefault(path) ?? []));
        }
    }

    /// path 의 [offset, offset+length) 를 timeUs (관찰 시작 기준) 에 처음 디스크에서 읽은 것이 서비스의 읽기인가. 취소되면 OperationCanceledException.
    public bool IsSelf(string path, long offset, long length, long timeUs)
    {
        if (length <= 0 || !_files.TryGetValue(path, out FileRanges? file))
        {
            return false;
        }

        if ((++_calls & CancelCheckMask) == 0)
        {
            _ct.ThrowIfCancellationRequested();
        }

        long end = offset + length;
        List<(long Start, long End)>? covered = null;
        foreach (Range span in file.Own.Overlapping(offset, end))
        {
            if (timeUs < span.FromUs || timeUs > span.ToUs)
            {
                continue;
            }

            if (file.Game.Overlapping(offset, end).Any(read => read.FromUs <= span.ToUs && read.ToUs >= span.FromUs))
            {
                return false;
            }

            (covered ??= []).Add((Math.Max(span.Start, offset), Math.Min(span.End, end)));
        }

        if (covered is null)
        {
            return false;
        }

        covered.Sort();
        long reach = offset;
        foreach ((long start, long stop) in covered)
        {
            if (start > reach)
            {
                return false;
            }

            reach = Math.Max(reach, stop);
        }

        return reach >= end;
    }

    private static List<Range> GetList(Dictionary<string, List<Range>> map, string path)
    {
        if (!map.TryGetValue(path, out List<Range>? list))
        {
            map[path] = list = [];
        }

        return list;
    }

    private readonly record struct Range(long Start, long End, long FromUs, long ToUs);

    private sealed record FileRanges(RangeIndex Own, RangeIndex Game);

    /// 시작 순으로 놓고 앞에서부터 가장 늦은 끝을 쌓아 둔 표: 구간과 겹치는 항목을 뒤에서부터 걸어 찾습니다.
    private sealed class RangeIndex
    {
        private readonly Range[] _sorted;
        private readonly long[] _maxEnds;

        public RangeIndex(List<Range> ranges)
        {
            _sorted = [.. ranges.OrderBy(r => r.Start)];
            _maxEnds = new long[_sorted.Length];
            long maxEnd = 0;
            for (int i = 0; i < _sorted.Length; i++)
            {
                maxEnd = Math.Max(maxEnd, _sorted[i].End);
                _maxEnds[i] = maxEnd;
            }
        }

        public IEnumerable<Range> Overlapping(long start, long end)
        {
            int low = 0;
            int high = _sorted.Length;
            while (low < high)
            {
                int mid = low + ((high - low) / 2);
                if (_sorted[mid].Start < end)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            for (int i = low - 1; i >= 0 && _maxEnds[i] > start; i--)
            {
                if (_sorted[i].End > start)
                {
                    yield return _sorted[i];
                }
            }
        }
    }
}
