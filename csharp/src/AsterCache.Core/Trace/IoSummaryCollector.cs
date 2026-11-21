using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AsterCache.Core.Trace;

/// 디스크 읽기 하나 (같은 디스크 번호·바이트 위치·크기의 완료를 합친 것). 시각은 관찰 시작을 0 으로 한 마이크로초입니다.
public sealed record IoRequest(
    uint Disk,
    long Offset,
    long Bytes,
    long Count,
    long LatencySumUs,
    long LatencyMaxUs,
    long FirstSessionUs,
    long PagingCount);

/// 관찰이 끝난 뒤의 모음: 변환 (파일 배치 조회) 에 필요한 요청 표와 변환과 무관한 총계.
public sealed record IoRequestSnapshot(
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset EndedAtUtc,
    IReadOnlyList<IoSummaryWindow> RawWindows,
    long? RawTraceZeroUs,
    long EventsLost,
    IoClassTotals Total,
    long LatencyMaxUs,
    IoClassTotals OtherDisk,
    IoClassTotals NoFile,
    IoClassTotals Dropped,
    long PagingReads,
    long PagingBytes,
    IReadOnlyList<IoRequest> Requests,
    IReadOnlyList<IoSummaryReadMode>? ReadModes = null,
    long ReadModeFilesDropped = 0,
    long ReadModeReadsUnnamed = 0,
    IReadOnlyList<IoFlagCount>? ReadModeFlags = null,
    long ReadModeFlagsOther = 0,
    long ReadModeDroppedRequests = 0,
    bool ReadModeFilesDroppedExact = true,
    SelfReadFilter? SelfReads = null,
    IReadOnlyList<SelfReadBatch>? SelfReadBatches = null,
    long SelfReadDroppedOps = 0,
    IReadOnlyList<long>? TargetStartsUs = null,
    IReadOnlyList<IoSummaryWriteFile>? WriteFiles = null);

/// 디스크 읽기 완료 이벤트를 (디스크, 바이트 위치, 크기) 요청 단위로 합칩니다. 이벤트 콜백 안에서 부르므로 사전 갱신 말고는 하지 않습니다 (파일 조회·문자열·JSON·로그 없음).
/// 디스크 읽기 이벤트의 IRP 는 논리 읽기의 IRP 와 다른 객체라 이벤트 시점에는 파일을 알 수 없습니다 (실측): 파일·블록은 끝난 뒤 IoBlockMapper 가 배치 표로 정합니다.
/// 처리 스레드 하나에서만 부릅니다 (잠금 없음). Snapshot 은 처리 스레드가 끝난 뒤에 부릅니다.
public sealed class IoSummaryCollector
{
    private const uint IrpNoCache = 0x01;
    private const uint IrpPagingIo = 0x02;
    private const uint IrpSynchronousPagingIo = 0x40;

    /// 파일 열기 옵션 (NtCreateFile CreateOptions) 중 기록하는 비트: 순차 (FILE_SEQUENTIAL_ONLY), 캐시 없음 (FILE_NO_INTERMEDIATE_BUFFERING), 임의 접근 (FILE_RANDOM_ACCESS).
    public const uint OpenSequential = 0x4;
    public const uint OpenNoBuffering = 0x8;
    public const uint OpenRandomAccess = 0x800;
    public const uint OpenOptionsMask = OpenSequential | OpenNoBuffering | OpenRandomAccess;

    /// 고유 요청 수 상한. 항목 하나가 사전 한 칸 (키 24 B + 값 40 B + 사전 오버헤드) 이라 상한에서 약 20 MB 로 어림잡은 값이며 실제 플레이로 재지 않았습니다.
    public const int MaxRequests = 1 << 18;

    /// 대상 프로세스에서 읽은 파일별 표시 집계 상한.
    public const int MaxReadModeFiles = 1 << 18;

    /// 읽기 방식 집계 상한을 넘긴 파일의 경로를 중복 제거해 보관하는 상한.
    public const int MaxDroppedReadModePaths = 1 << 16;

    /// 요청 표시 종류별 집계 상한. 넘는 값은 그 밖으로 합칩니다.
    public const int MaxReadModeFlags = 32;

    /// 쓰기를 모으는 파일 수 상한 (넘는 파일의 쓰기는 모으지 않음)과 저장하는 파일 수 (쓴 바이트가 많은 순): 항목이 파일마다 구간 목록 하나라 가볍지만 로그·설정 파일이 많은 게임에서도 저장 크기가 일정하게 합니다.
    public const int MaxWriteFilesTracked = 1 << 12;

    public const int MaxWriteFilesSaved = 256;

    /// 파일 하나의 쓴 위치 구간 상한: 이어 붙는 쓰기는 한 구간으로 합치므로 덧붙이기만 하는 파일은 하나로 끝나고, 군데군데 덮어쓰는 파일은 이 수까지만 남깁니다 (넘으면 RangesTruncated).
    public const int MaxWriteRangesPerFile = 64;

    private readonly IReadOnlySet<uint>? _gameDisks;
    private readonly int _maxRequests;
    private readonly string[]? _readModeRoots;
    private readonly int _maxReadModeFiles;
    private readonly int _maxDroppedReadModePaths;
    private readonly Dictionary<RequestKey, RequestTotals> _requests = [];
    private readonly Dictionary<string, ReadModeTotals> _readModes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, uint> _openOptions = new(StringComparer.OrdinalIgnoreCase); // 파일별로 열 때 준 옵션 (OpenOptionsMask 비트의 합집합)
    private readonly Dictionary<string, WriteTotals> _writes = new(StringComparer.OrdinalIgnoreCase); // 파일별 쓰기 기록
    private readonly HashSet<string> _droppedReadModePaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, long> _readModeFlags = [];
    private IoClassTotals _total;
    private IoClassTotals _otherDisk;
    private IoClassTotals _noFile;
    private IoClassTotals _dropped;
    private long _latencyMaxUs;
    private long _pagingReads;
    private long _pagingBytes;
    private long _readModeFilesDropped;
    private long _readModeDroppedRequests;
    private long _readModeReadsUnnamed;
    private long _readModeFlagsOther;
    private bool _readModeFilesDroppedExact = true;
    private readonly SelfReadObservation _selfReads = SelfReadLog.Begin(); // 관찰 동안 서비스가 읽은 구간 (Snapshot 이 끝냄)

    /// gameDiskNumbers: 집계 루트가 놓인 볼륨의 디스크 번호들. 모르면 (null) 모든 디스크의 완료를 요청 표에 담습니다.
    public IoSummaryCollector(DateTimeOffset observedAtUtc, IReadOnlySet<uint>? gameDiskNumbers = null, int maxRequests = MaxRequests,
        IReadOnlyCollection<string>? readModeRoots = null, int maxReadModeFiles = MaxReadModeFiles,
        int maxDroppedReadModePaths = MaxDroppedReadModePaths)
    {
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
        _gameDisks = gameDiskNumbers;
        _maxRequests = maxRequests;
        _readModeRoots = readModeRoots?.Where(root => !string.IsNullOrWhiteSpace(root)).Select(ReadModeRootPrefix).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _maxReadModeFiles = maxReadModeFiles;
        _maxDroppedReadModePaths = Math.Max(0, maxDroppedReadModePaths);
    }

    public DateTimeOffset ObservedAtUtc { get; }

    /// 대상 프로세스의 파일 읽기 요청 표시를 파일별로 더합니다. path 가 없으면 이름을 끝내 못 찾은 읽기로 셉니다.
    public void AddProcessRead(string? path, long requests, long bytes, uint flags)
    {
        requests = Math.Max(0, requests);
        bytes = Math.Max(0, bytes);
        if (requests == 0)
        {
            return;
        }

        if (_readModeFlags.TryGetValue(flags, out long known))
        {
            _readModeFlags[flags] = known + requests;
        }
        else if (_readModeFlags.Count < MaxReadModeFlags)
        {
            _readModeFlags.Add(flags, requests);
        }
        else
        {
            _readModeFlagsOther += requests;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            _readModeReadsUnnamed += requests;
            return;
        }

        if (!IsReadModePath(path))
        {
            return;
        }

        if (!_readModes.TryGetValue(path, out ReadModeTotals totals))
        {
            if (_readModes.Count >= _maxReadModeFiles)
            {
                if (_droppedReadModePaths.Contains(path))
                {
                    return;
                }

                if (_droppedReadModePaths.Count < _maxDroppedReadModePaths)
                {
                    _droppedReadModePaths.Add(path);
                    _readModeFilesDropped++;
                }
                else
                {
                    _readModeDroppedRequests += requests;
                    _readModeFilesDroppedExact = false;
                }

                return;
            }

            totals = default;
        }

        bool paging = (flags & (IrpPagingIo | IrpSynchronousPagingIo)) != 0;
        if (paging)
        {
            totals.PagingRequests += requests;
            totals.PagingBytes += bytes;
        }
        else if ((flags & IrpNoCache) != 0)
        {
            totals.NoCacheRequests += requests;
            totals.NoCacheBytes += bytes;
        }
        else
        {
            totals.CacheRequests += requests;
            totals.CacheBytes += bytes;
        }

        _readModes[path] = totals;
    }

    /// 대상 프로세스의 파일 쓰기 한 건을 더합니다 (이름을 모르는 쓰기·집계 경로 밖의 쓰기는 모으지 않음). flags 의 IRP_NOCACHE 는 캐시 없이 쓴 요청 표시, sessionTimeUs 는 관찰 시작을 0 으로 한 마이크로초.
    public void AddProcessWrite(string? path, long offset, long bytes, uint flags, long sessionTimeUs)
    {
        if (string.IsNullOrWhiteSpace(path) || offset < 0 || bytes <= 0 || !IsReadModePath(path))
        {
            return;
        }

        if (!_writes.TryGetValue(path, out WriteTotals? totals))
        {
            if (_writes.Count >= MaxWriteFilesTracked)
            {
                return;
            }

            _writes[path] = totals = new WriteTotals(Math.Max(0, sessionTimeUs));
        }

        totals.Add(offset, bytes, (flags & IrpNoCache) != 0 && (flags & (IrpPagingIo | IrpSynchronousPagingIo)) == 0, Math.Max(0, sessionTimeUs));
    }

    /// 대상 프로세스가 파일을 열 때 준 옵션을 더합니다 (기록만 함: 읽기 집계와 같은 경로 범위·상한). 읽기 집계 상한만큼의 파일까지만 담습니다.
    public void AddOpen(string path, uint createOptions)
    {
        uint options = createOptions & OpenOptionsMask;
        if (options == 0 || string.IsNullOrWhiteSpace(path) || !IsReadModePath(path))
        {
            return;
        }

        if (_openOptions.TryGetValue(path, out uint known))
        {
            _openOptions[path] = known | options;
        }
        else if (_openOptions.Count < _maxReadModeFiles)
        {
            _openOptions.Add(path, options);
        }
    }

    public void AddDiskRead(uint disk, long offset, long bytes, long latencyUs, long sessionTimeUs, bool paging)
    {
        bytes = Math.Max(0, bytes);
        latencyUs = Math.Max(0, latencyUs);
        sessionTimeUs = Math.Max(0, sessionTimeUs);
        _total = _total.Plus(1, bytes, latencyUs);
        _latencyMaxUs = Math.Max(_latencyMaxUs, latencyUs);
        if (paging)
        {
            _pagingReads++;
            _pagingBytes += bytes;
        }

        if (_gameDisks is not null && !_gameDisks.Contains(disk))
        {
            _otherDisk = _otherDisk.Plus(1, bytes, latencyUs);
            return;
        }

        if (bytes == 0 || offset < 0 || offset > long.MaxValue - bytes)
        {
            _noFile = _noFile.Plus(1, bytes, latencyUs); // 위치를 알 수 없는 완료는 변환할 수 없음
            return;
        }

        var key = new RequestKey(disk, offset, bytes);
        ref RequestTotals totals = ref CollectionsMarshal.GetValueRefOrNullRef(_requests, key);
        if (!Unsafe.IsNullRef(ref totals))
        {
            totals.Add(latencyUs, sessionTimeUs, paging);
            return;
        }

        if (_requests.Count >= _maxRequests)
        {
            _dropped = _dropped.Plus(1, bytes, latencyUs); // 넘친 요청은 0회로 취급하지 않고 건수·바이트·지연 합을 남김
            return;
        }

        var first = default(RequestTotals);
        first.Add(latencyUs, sessionTimeUs, paging);
        _requests.Add(key, first);
    }

    public IoRequestSnapshot Snapshot(DateTimeOffset endedAtUtc, IReadOnlyList<IoSummaryWindow> rawWindows, long eventsLost, long? rawTraceZeroUs)
    {
        var requests = new List<IoRequest>(_requests.Count);
        foreach ((RequestKey key, RequestTotals totals) in _requests)
        {
            requests.Add(new IoRequest(key.Disk, key.Offset, key.Bytes, totals.Count, totals.LatencySumUs, totals.LatencyMaxUs, totals.FirstSessionUs, totals.Paging));
        }

        IoSummaryReadMode[] readModes = _readModes.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new IoSummaryReadMode(pair.Key, pair.Value.CacheRequests, pair.Value.CacheBytes, pair.Value.NoCacheRequests, pair.Value.NoCacheBytes,
                pair.Value.PagingRequests, pair.Value.PagingBytes, _openOptions.GetValueOrDefault(pair.Key))).ToArray();
        IoFlagCount[] flags = _readModeFlags.OrderBy(pair => pair.Key).Select(pair => new IoFlagCount(pair.Key, pair.Value)).ToArray();
        (IReadOnlyList<SelfReadBatch> selfBatches, long selfDropped) = _selfReads.End();
        IoSummaryWriteFile[] writes = [.. _writes.OrderByDescending(pair => pair.Value.Bytes).ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Take(MaxWriteFilesSaved)
            .Select(pair => pair.Value.ToFile(pair.Key))];
        return new IoRequestSnapshot(ObservedAtUtc, endedAtUtc.ToUniversalTime(), rawWindows.ToArray(), rawTraceZeroUs, Math.Max(0, eventsLost),
            _total, _latencyMaxUs, _otherDisk, _noFile, _dropped, _pagingReads, _pagingBytes, requests, readModes, _readModeFilesDropped,
            _readModeReadsUnnamed, flags.Length == 0 ? null : flags, _readModeFlagsOther, _readModeDroppedRequests, _readModeFilesDroppedExact,
            SelfReadBatches: selfBatches, SelfReadDroppedOps: selfDropped, WriteFiles: writes.Length == 0 ? null : writes);
    }

    private bool IsReadModePath(string path)
    {
        if (_readModeRoots is null)
        {
            return true;
        }

        foreach (string root in _readModeRoots)
        {
            string exactRoot = root.EndsWith(Path.DirectorySeparatorChar) ? root[..^1] : root;
            if (path.Equals(exactRoot, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ReadModeRootPrefix(string root)
    {
        string fullPath = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (fullPath.Length == 2 && fullPath[1] == ':')
        {
            return fullPath + Path.DirectorySeparatorChar;
        }

        return fullPath + Path.DirectorySeparatorChar;
    }

    private struct ReadModeTotals
    {
        public long CacheRequests;
        public long CacheBytes;
        public long NoCacheRequests;
        public long NoCacheBytes;
        public long PagingRequests;
        public long PagingBytes;
    }

    // 파일 하나의 쓰기 기록: 이어 붙는 쓰기 (직전 구간의 끝이나 그 안에서 시작) 는 마지막 구간을 늘리고, 아니면 새 구간을 엽니다 (상한을 넘으면 더하지 않고 표시만).
    private sealed class WriteTotals
    {
        private readonly List<IoWriteRange> _ranges = [];
        private readonly long _firstUs;
        private bool _truncated;
        private long _lastUs;

        public WriteTotals(long firstUs) => _firstUs = _lastUs = firstUs;

        public long Writes { get; private set; }

        public long Bytes { get; private set; }

        public long NoCacheWrites { get; private set; }

        public long NoCacheBytes { get; private set; }

        public void Add(long offset, long bytes, bool noCache, long sessionTimeUs)
        {
            Writes++;
            Bytes += bytes;
            if (noCache)
            {
                NoCacheWrites++;
                NoCacheBytes += bytes;
            }

            _lastUs = Math.Max(_lastUs, sessionTimeUs);
            if (_ranges.Count > 0 && _ranges[^1] is { } last && offset >= last.Offset && offset <= last.Offset + last.Length)
            {
                _ranges[^1] = last with { Length = Math.Max(last.Length, offset + bytes - last.Offset) };
            }
            else if (_ranges.Count < MaxWriteRangesPerFile)
            {
                _ranges.Add(new IoWriteRange(offset, bytes));
            }
            else
            {
                _truncated = true;
            }
        }

        public IoSummaryWriteFile ToFile(string path) => new(path, Writes, Bytes, NoCacheWrites, NoCacheBytes, _firstUs, _lastUs, [.. _ranges], _truncated);
    }

    private readonly record struct RequestKey(uint Disk, long Offset, long Bytes);

    private struct RequestTotals
    {
        public long Count;
        public long Paging;
        public long LatencySumUs;
        public long LatencyMaxUs;
        public long FirstSessionUs;

        public void Add(long latencyUs, long sessionTimeUs, bool paging)
        {
            FirstSessionUs = Count == 0 ? sessionTimeUs : Math.Min(FirstSessionUs, sessionTimeUs);
            Count++;
            Paging += paging ? 1 : 0;
            LatencySumUs += latencyUs;
            LatencyMaxUs = Math.Max(LatencyMaxUs, latencyUs);
        }
    }
}
