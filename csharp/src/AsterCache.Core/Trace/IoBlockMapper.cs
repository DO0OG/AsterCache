namespace AsterCache.Core.Trace;

/// 파일 안 한 구간이 놓인 디스크 위치 (바이트). 파일 시스템 클러스터 단위 값은 조회하는 쪽이 바이트로 바꿔 줍니다.
public readonly record struct IoFileExtent(long DiskOffset, long FileOffset, long Length);

/// 끝난 뒤 디스크 읽기 요청을 파일의 (파일, 64 KiB 블록) 로 바꿉니다 (순수 로직: 파일 배치는 AddFile 로 받음).
/// 요청 하나가 구간·블록·파일 경계를 넘으면 바이트는 겹친 만큼, 지연은 바이트 비례로 나눠 총합을 보존합니다 (잔여는 누적 나눗셈이 마지막 조각으로 보냄).
/// 이벤트 시점에 파일을 아는 방법은 쓰지 않습니다: 디스크 읽기 이벤트의 IRP·파일 이름으로는 이을 수 없고 (실측), 디스크 위치를 파일 배치로 바꾸는 쪽만 통했습니다.
public sealed class IoBlockMapper
{
    public const int BlockSizeBytes = 64 * 1024;

    /// 블록 행 상한. 한 행이 곁 파일에서 약 120 B 라 상한에서 30 MB 안팎 (이전 집계의 블록 표 상한과 같은 값).
    public const int MaxBlocks = 250000;

    private const int Linked = 0;
    private const int NoFile = 1;
    private const int NotConverted = 2;

    private readonly IoRequestSnapshot _snapshot;
    private readonly int _maxBlocks;
    private readonly Dictionary<uint, DiskRequests> _byDisk = [];
    private readonly HashSet<uint> _skippedDisks = [];
    private readonly List<IoSummaryFile> _files = [];
    private readonly List<Interval> _intervals = [];
    private readonly Dictionary<(int File, long Block), BlockTotals> _blocks = [];
    private long _blockTableDropped;

    public IoBlockMapper(IoRequestSnapshot snapshot, int maxBlocks = MaxBlocks)
    {
        _snapshot = snapshot;
        _maxBlocks = maxBlocks;
        foreach (IGrouping<uint, IoRequest> group in snapshot.Requests.Where(r => r.Bytes > 0 && r.Count > 0).GroupBy(r => r.Disk))
        {
            _byDisk[group.Key] = new DiskRequests([.. group.OrderBy(r => r.Offset)]);
        }
    }

    /// 변환하지 않는 디스크 (여러 디스크에 걸친 볼륨): 파일 구간 밖의 읽기를 "파일 구간 밖" 이 아니라 "변환 안 함" 으로 셉니다.
    public void SkipDisk(uint disk) => _skippedDisks.Add(disk);

    /// 파일 하나의 할당 구간을 더합니다. 요청이 하나도 닿지 않는 구간은 버려 메모리가 요청 수에 묶이게 합니다.
    /// convertible 이 거짓 (압축·암호화 파일) 이면 구간만 남겨 그 위치의 읽기를 "변환 안 함" 으로 셉니다.
    public void AddFile(string path, bool changedDuringSession, bool convertible, uint disk, IEnumerable<IoFileExtent> extents)
    {
        if (!_byDisk.TryGetValue(disk, out DiskRequests? requests))
        {
            return;
        }

        int file = -1;
        foreach (IoFileExtent extent in extents)
        {
            if (extent.Length <= 0 || extent.DiskOffset < 0 || extent.FileOffset < 0 || extent.DiskOffset > long.MaxValue - extent.Length
                || !requests.Overlaps(extent.DiskOffset, extent.DiskOffset + extent.Length))
            {
                continue;
            }

            if (convertible && file < 0)
            {
                file = _files.Count;
                _files.Add(new IoSummaryFile(path, changedDuringSession));
            }

            _intervals.Add(new Interval(disk, extent.DiskOffset, extent.DiskOffset + extent.Length, file, extent.FileOffset));
        }
    }

    public IoSummary Build(long filesScanned, long filesChanged, long filesUnreadable, long volumesSkipped, long scanMs, bool scanIncomplete)
    {
        _intervals.Sort((a, b) => a.Disk != b.Disk ? a.Disk.CompareTo(b.Disk) : a.Start.CompareTo(b.Start));
        IoClassTotals linked = default;
        IoClassTotals noFile = _snapshot.NoFile;
        IoClassTotals notConverted = default;
        Span<long> bytes = stackalloc long[3];
        Span<long> latency = stackalloc long[3];
        foreach (IoRequest request in _snapshot.Requests)
        {
            if (request.Bytes <= 0 || request.Count <= 0)
            {
                continue;
            }

            bytes.Clear();
            latency.Clear();
            Place(request, bytes, latency);
            int dominant = bytes[Linked] >= bytes[NoFile] && bytes[Linked] >= bytes[NotConverted] ? Linked : bytes[NoFile] >= bytes[NotConverted] ? NoFile : NotConverted;
            linked = linked.Plus(dominant == Linked ? request.Count : 0, bytes[Linked] * request.Count, latency[Linked]);
            noFile = noFile.Plus(dominant == NoFile ? request.Count : 0, bytes[NoFile] * request.Count, latency[NoFile]);
            notConverted = notConverted.Plus(dominant == NotConverted ? request.Count : 0, bytes[NotConverted] * request.Count, latency[NotConverted]);
        }

        var rows = new List<IoSummaryRow>(_blocks.Count);
        foreach (((int file, long block), BlockTotals totals) in _blocks)
        {
            rows.Add(new IoSummaryRow(file, block, totals.Requests, totals.Bytes, totals.LatencySumUs, totals.LatencyMaxUs, totals.FirstSessionUs, totals.PagingRequests));
        }

        rows.Sort((a, b) => a.FileNumber != b.FileNumber ? a.FileNumber.CompareTo(b.FileNumber) : a.BlockNumber.CompareTo(b.BlockNumber));
        var quality = new IoSummaryQuality(
            _snapshot.Total, _snapshot.LatencyMaxUs, linked, _snapshot.OtherDisk, noFile, notConverted, _snapshot.Dropped,
            _snapshot.PagingReads, _snapshot.PagingBytes, _snapshot.EventsLost, _blockTableDropped,
            filesScanned, filesChanged, filesUnreadable, volumesSkipped, scanMs, scanIncomplete,
            _snapshot.ReadModeFilesDropped, _snapshot.ReadModeReadsUnnamed, _snapshot.ReadModeFlags, _snapshot.ReadModeFlagsOther, _snapshot.EndedAtUtc,
            _snapshot.ReadModeDroppedRequests, _snapshot.ReadModeFilesDroppedExact);
        long? zero = _snapshot.RawTraceZeroUs is { } z && Math.Abs(z) <= long.MaxValue / TimeSpan.TicksPerMicrosecond ? z : null;
        return new IoSummary(_snapshot.ObservedAtUtc, _snapshot.EndedAtUtc, _snapshot.RawWindows, quality, _files, rows, Guid.NewGuid().ToString("N"),
            null, zero, zero is { } at ? _snapshot.ObservedAtUtc.AddTicks(at * TimeSpan.TicksPerMicrosecond) : null, _snapshot.ReadModes ?? []);
    }

    // 요청 하나를 위치 순으로 걸으며 분류 (바이트·지연 누적) 하고 파일에 닿은 부분은 블록에 더합니다.
    private void Place(IoRequest request, Span<long> bytes, Span<long> latency)
    {
        long position = request.Offset;
        long end = request.Offset + request.Bytes;
        long consumed = 0;
        while (position < end)
        {
            int index = LastAtOrBefore(request.Disk, position);
            int kind;
            long part;
            if (index >= 0 && _intervals[index].Disk == request.Disk && position < _intervals[index].End)
            {
                Interval interval = _intervals[index];
                part = Math.Min(end, interval.End) - position;
                kind = interval.File >= 0 ? Linked : NotConverted;
                if (interval.File >= 0)
                {
                    AddBlocks(interval.File, interval.FileStart + (position - interval.Start), part, request, consumed);
                }
            }
            else
            {
                long next = index + 1 < _intervals.Count && _intervals[index + 1].Disk == request.Disk ? _intervals[index + 1].Start : long.MaxValue;
                part = Math.Min(end, next) - position;
                kind = _skippedDisks.Contains(request.Disk) ? NotConverted : NoFile;
            }

            bytes[kind] += part;
            latency[kind] += LatencyUpTo(request, consumed + part) - LatencyUpTo(request, consumed);
            consumed += part;
            position += part;
        }
    }

    private void AddBlocks(int file, long fileOffset, long length, IoRequest request, long consumed)
    {
        long position = fileOffset;
        long remaining = length;
        while (remaining > 0)
        {
            long block = position / BlockSizeBytes;
            long piece = Math.Min(remaining, (block + 1) * BlockSizeBytes - position);
            long latency = LatencyUpTo(request, consumed + piece) - LatencyUpTo(request, consumed);
            if (_blocks.TryGetValue((file, block), out BlockTotals? totals))
            {
                totals.Add(request, piece, latency);
            }
            else if (_blocks.Count < _maxBlocks)
            {
                totals = new BlockTotals();
                totals.Add(request, piece, latency);
                _blocks.Add((file, block), totals);
            }
            else
            {
                _blockTableDropped++; // 넘친 블록은 0회로 취급하지 않고 건수를 남김
            }

            position += piece;
            remaining -= piece;
            consumed += piece;
        }
    }

    // 요청 앞쪽 x 바이트까지의 지연 몫 (누적 나눗셈이라 조각 합이 지연 합과 정확히 같음).
    private static long LatencyUpTo(IoRequest request, long x) => (long)((Int128)request.LatencySumUs * x / request.Bytes);

    // (디스크, 시작) 이 (disk, position) 이하인 마지막 구간의 위치, 없으면 -1.
    private int LastAtOrBefore(uint disk, long position)
    {
        int low = 0;
        int high = _intervals.Count - 1;
        int found = -1;
        while (low <= high)
        {
            int mid = low + ((high - low) / 2);
            Interval interval = _intervals[mid];
            if (interval.Disk < disk || (interval.Disk == disk && interval.Start <= position))
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }

    private readonly record struct Interval(uint Disk, long Start, long End, int File, long FileStart);

    /// 디스크 하나의 요청을 시작 순으로 놓고 앞에서부터 가장 늦은 끝을 쌓아 둔 표: 어떤 구간에 닿는 요청이 있는지 이진 탐색 한 번으로 압니다.
    private sealed class DiskRequests
    {
        private readonly long[] _starts;
        private readonly long[] _maxEnds;

        public DiskRequests(IoRequest[] sorted)
        {
            _starts = new long[sorted.Length];
            _maxEnds = new long[sorted.Length];
            long maxEnd = 0;
            for (int i = 0; i < sorted.Length; i++)
            {
                _starts[i] = sorted[i].Offset;
                maxEnd = Math.Max(maxEnd, sorted[i].Offset + sorted[i].Bytes);
                _maxEnds[i] = maxEnd;
            }
        }

        public bool Overlaps(long start, long end)
        {
            int low = 0;
            int high = _starts.Length;
            while (low < high)
            {
                int mid = low + ((high - low) / 2);
                if (_starts[mid] < end)
                {
                    low = mid + 1;
                }
                else
                {
                    high = mid;
                }
            }

            return low > 0 && _maxEnds[low - 1] > start;
        }
    }

    private sealed class BlockTotals
    {
        public long Requests;
        public long Bytes;
        public long LatencySumUs;
        public long LatencyMaxUs;
        public long FirstSessionUs = long.MaxValue;
        public long PagingRequests;

        public void Add(IoRequest request, long bytes, long latencyUs)
        {
            Requests += request.Count;
            Bytes += bytes * request.Count;
            LatencySumUs += latencyUs;
            LatencyMaxUs = Math.Max(LatencyMaxUs, request.LatencyMaxUs);
            FirstSessionUs = Math.Min(FirstSessionUs, request.FirstSessionUs);
            PagingRequests += request.PagingCount;
        }
    }
}
