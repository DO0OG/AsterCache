using AsterCache.Core.Trace;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace AsterCache.Tracer;

/// 커널 파서 이벤트 → 대상 프로세스 트리의 TraceRecord. EtlReader (파일) 와 LiveSession (실시간) 이 함께 씁니다.
/// 파일 이름은 직접 짠 표로 풉니다: FileIOName / FileIOFileCreate / FileIOFileRundown 은 FileKey, FileIOCreate 는 FileObject 를 열쇠로 삼습니다.
/// 읽기는 FileKey 이름이 이미 있으면 그것을, 없으면 Finish 에서 (정리 (rundown) 이벤트는 세션 끝에 옴) FileKey 이름을 먼저, 없으면 읽을 때의 FileObject 이름을 씁니다.
/// FileObject 주소는 닫힌 뒤 다른 파일에 재사용되고 이벤트가 유실되면 옛 이름이 남으므로, 실제 .etl 로 확인한 대로 FileKey 이름을 우선합니다.
/// 실시간 리스너에는 이 기록 규칙과 따로, 읽는 순간 FileKey → 열려 있는 FileObject 이름으로 바로 알리고 (OnRead), 그래도 모르면 이름이 올 때 늦게 알립니다 (OnLateRead). 기록 (Finish) 은 이 때문에 달라지지 않습니다.
internal sealed class KernelCollector
{
    private const int IrpPagingIo = 0x02; // IrpFlags 의 페이징 읽기 비트 (TraceEvent 열거형 이름은 Windows 이름과 어긋나 값으로 판정)
    private const int MaxPendingReadModeGroups = IoSummaryCollector.MaxReadModeFiles;
    // 등록 실행 파일 시작 시각 기록 상한 (맵 로드마다 재시작하는 게임도 하루 플레이에 수십 번이라 넉넉한 값).
    private const int MaxTargetStarts = 512;
    // 원시 기록 동결 뒤 읽기 방식 집계용 FileKey·FileObject 이름 표 상한.
    private const int MaxReadModeNames = 1 << 18;

    private readonly object _rawLock = new();
    private readonly ProcessTree _tree;
    private readonly DevicePaths _devices;
    private readonly FileNames _names;
    private readonly List<TraceRecord> _records = [];
    private readonly List<(int Index, ulong Key, string? ObjName)> _pending = [];
    private readonly Dictionary<PendingReadModeKey, PendingReadModeTotals> _pendingReadModes = [];
    private List<TraceRecord>? _frozenRecords;
    private volatile bool _rawFrozen; // 원시 기록 확정 뒤에는 읽기 방식 집계에 필요한 이름·프로세스 추적만 계속
    private bool _readModesFlushed;
    private readonly HashSet<ulong> _written = []; // 이미 쓰기로 기록한 FileKey (쓰기 이벤트는 많아서 파일마다 한 번만)
    private readonly ReadWindow _window = new(); // 닫힌 구간의 읽기·쓰기는 기록하지 않음 (이름·프로세스 이벤트는 계속 처리)
    private volatile LiveListener? _listener; // 첫 구간이 끝나면 Pause 가 뗌
    private volatile int _active;
    private volatile bool _sawTarget;
    private readonly IoSummaryCollector? _ioSummary;
    private readonly DateTimeOffset _originUtc;
    private readonly List<IoSummaryWindow> _rawWindows = [];
    private readonly List<long> _targetStartsUs = [];
    private long? _rawWindowStartedUs;

    public KernelCollector(KernelTraceEventParser kernel, IReadOnlyCollection<string> exeNames, DevicePaths devices, LiveListener? listener = null,
        IoSummaryCollector? ioSummary = null)
        : this(exeNames, devices, listener, ioSummary)
    {
        kernel.ProcessStart += d => OnProcessStart(d, alreadyRunning: false);
        kernel.ProcessDCStart += d => OnProcessStart(d, alreadyRunning: true); // 세션 시작 때 이미 떠 있던 프로세스
        kernel.ProcessStop += d => StopProcess(d.TimeStamp, d.ProcessID);
        kernel.FileIOName += d => NameKey(d.FileKey, d.FileName);
        kernel.FileIOFileCreate += d => NameKey(d.FileKey, d.FileName);
        kernel.FileIOFileRundown += d => NameKey(d.FileKey, d.FileName);
        kernel.FileIOCreate += d => OpenFile(d.ProcessID, d.FileObject, d.FileName, unchecked((uint)d.CreateOptions));
        kernel.FileIOClose += d => CloseObject(d.FileObject);
        kernel.FileIORead += d => AddRead(d.TimeStamp, d.ProcessID, d.FileKey, d.FileObject, d.Offset, d.IoSize, unchecked((uint)d.IoFlags));
        kernel.FileIOWrite += d => AddWrite(d.TimeStamp, d.ProcessID, d.FileKey, d.FileObject);
        if (_ioSummary is not null)
        {
            kernel.DiskIORead += d => AddDiskRead(d.DiskNumber, d.ByteOffset, d.TransferSize, d.ElapsedTimeMSec, d.TimeStamp, ((int)d.IrpFlags & IrpPagingIo) != 0);
        }
    }

    /// 이벤트 파서 없이 만드는 쪽 (시험이 이벤트 처리 메서드를 직접 부름).
    internal KernelCollector(IReadOnlyCollection<string> exeNames, DevicePaths devices, LiveListener? listener = null, IoSummaryCollector? ioSummary = null)
    {
        _tree = new ProcessTree(exeNames);
        _devices = devices;
        _listener = listener;
        _ioSummary = ioSummary;
        _originUtc = ioSummary?.ObservedAtUtc ?? DateTimeOffset.UtcNow;
        _rawWindowStartedUs = _ioSummary is null ? null : 0;
        _names = new FileNames((name, offset, size) => _listener?.OnLateRead?.Invoke(_devices.ToDosPath(name), offset, size));
    }

    /// 이름을 끝내 못 찾은 읽기 수 (Finish 뒤에 읽음).
    public int Unresolved { get; private set; }

    /// 대상 프로세스 트리가 지금 하나라도 살아 있는가 / 한 번이라도 본 적이 있는가 (다른 스레드에서 읽음).
    public bool TargetsActive => _active > 0;

    public bool SawTarget => _sawTarget;

    /// 실시간 알림 수: 읽는 순간 이름이 풀린 읽기 / 이름이 늦게 와서 늦게 알린 읽기 (Stop 뒤에 읽음).
    public long ResolvedReads => _names.Resolved;

    public long LateReads => _names.Late;

    /// 읽기 수집을 멈추고 (세션·이름표·프로세스 추적은 그대로) 실시간 알림을 뗍니다. 알림은 첫 구간에서만 쓰입니다.
    /// 처리 스레드가 이미 잡은 알림은 뗀 뒤 몇 건 더 나갈 수 있음 (학습 요약 수치가 한두 건 어긋날 수 있을 뿐 해롭지 않음).
    public void Pause()
    {
        Pause(DateTimeOffset.UtcNow);
    }

    internal void Pause(DateTimeOffset atUtc)
    {
        lock (_rawLock)
        {
            CloseRawWindow(atUtc);
            _window.Close();
            _listener = null;
        }
    }

    public void Resume()
    {
        Resume(DateTimeOffset.UtcNow);
    }

    internal void Resume(DateTimeOffset atUtc)
    {
        lock (_rawLock)
        {
            _rawWindowStartedUs = _ioSummary is null ? null : Math.Max(0, SessionTimeUs(atUtc));
            _window.Reopen();
        }
    }

    /// 처리 스레드가 끝난 뒤에 부릅니다: 요청 표와 총계를 내줍니다 (파일·블록으로의 변환은 IoBlockMapper 가 따로).
    public IoRequestSnapshot? FinishIoRequests(DateTimeOffset endedAtUtc, long eventsLost)
    {
        if (_ioSummary is null)
        {
            return null;
        }

        lock (_rawLock)
        {
            CloseRawWindow(endedAtUtc);
            FlushPendingReadModes();
            return _ioSummary.Snapshot(endedAtUtc, _rawWindows, eventsLost, RawTraceZeroUs) with { TargetStartsUs = [.. _targetStartsUs] };
        }
    }

    /// 지난 호출 뒤 구간이 받은 읽기 집계를 읽고 비웁니다 (EventsLost 는 세션이 채움).
    public WindowStats TakeWindowStats()
    {
        return new WindowStats(_window.Take(), _active, 0);
    }

    /// 이름 표 크기와 모은 기록 수 (세션이 긴 만큼 메모리를 재 보는 용도, Stop 뒤에 읽음).
    public (int Names, int Records) Footprint
    {
        get
        {
            lock (_rawLock)
            {
                return (_names.Count, _records.Count);
            }
        }
    }

    /// 서비스가 등록 실행 파일의 시작을 감지해 이 세션을 열었음: 그 시작은 세션에 이미 떠 있던 프로세스 (DCStart) 로 들어와 기록에서 빠지므로 관찰 시작 (0) 의 시작으로 넣습니다.
    internal void NoteTriggerStart()
    {
        if (_ioSummary is null)
        {
            return;
        }

        lock (_rawLock)
        {
            if (_targetStartsUs.Count < MaxTargetStarts && !_targetStartsUs.Contains(0))
            {
                _targetStartsUs.Add(0);
            }
        }
    }

    public long? RawTraceZeroUs { get; private set; }

    public void FreezeRawRecords()
    {
        lock (_rawLock)
        {
            if (_rawFrozen)
            {
                return;
            }

            _frozenRecords = FinishRecords();
            DateTime? rawStart = _records.Count == 0 ? null : _records[0].Time;
            RawTraceZeroUs = rawStart is { } start ? SessionTimeUs(start) : null;
            _records.Clear();
            _pending.Clear();
            if (_ioSummary is not null)
            {
                _names.LimitNewEntriesAfterFreeze(MaxReadModeNames);
            }

            _rawFrozen = true;
        }
    }

    public List<TraceRecord> Finish()
    {
        lock (_rawLock)
        {
            if (_frozenRecords is not null)
            {
                return [.. _frozenRecords];
            }

            List<TraceRecord> records = FinishRecords();
            if (_ioSummary is not null && RawTraceZeroUs is null && _records.Count > 0)
            {
                RawTraceZeroUs = SessionTimeUs(_records[0].Time);
            }

            return records;
        }
    }

    private List<TraceRecord> FinishRecords()
    {
        var dropped = new HashSet<int>();
        foreach ((int index, ulong key, string? objName) in _pending)
        {
            string? name = _names.Settle(key, objName);
            if (name is null)
            {
                dropped.Add(index);
                if (_records[index].Kind == RecordKind.Read)
                {
                    Unresolved++; // 쓰기 기록의 미해결은 읽기 손실이 아니므로 세지 않음
                }
            }
            else
            {
                _records[index] = _records[index] with { Path = name };
            }
        }

        var result = new List<TraceRecord>(_records.Count - dropped.Count);
        for (int i = 0; i < _records.Count; i++)
        {
            if (dropped.Contains(i))
            {
                continue;
            }

            TraceRecord r = _records[i];
            result.Add(r.Kind is RecordKind.Read or RecordKind.Write ? r with { Path = _devices.ToDosPath(r.Path) } : r);
        }

        return result;
    }

    private void OnProcessStart(ProcessTraceData d, bool alreadyRunning)
    {
        // 짧은 ImageFileName (15자) 보다 전체 경로가 있으면 그것을, 없으면 명령줄 첫 토큰을 먼저 씁니다.
        string image = !string.IsNullOrEmpty(d.KernelImageFileName) ? d.KernelImageFileName
            : !string.IsNullOrEmpty(d.CommandLine) ? FirstToken(d.CommandLine)
            : d.ImageFileName ?? "";
        StartProcess(d.TimeStamp, d.ProcessID, d.ParentID, image, alreadyRunning);
    }

    internal void StartProcess(DateTime time, int pid, int parentPid, string image, bool alreadyRunning = false)
    {
        if (_rawFrozen && _ioSummary is null)
        {
            return;
        }

        lock (_rawLock)
        {
            if (_ioSummary is not null && !alreadyRunning && _targetStartsUs.Count < MaxTargetStarts && _tree.IsRegistered(image))
            {
                _targetStartsUs.Add(Math.Max(0, SessionTimeUs(time))); // 맵 로드 때 다시 뜨는 실행 파일의 시각 (집계가 로드 구간을 가르는 기준)
            }

            if (_tree.Start(pid, parentPid, image))
            {
                _sawTarget = true;
            }

            _active = _tree.ActiveCount;
            if (!_rawFrozen)
            {
                _records.Add(TraceRecord.Start(time, pid, parentPid, image));
            }
        }
    }

    internal void StopProcess(DateTime time, int pid)
    {
        if (_rawFrozen && _ioSummary is null)
        {
            return;
        }

        lock (_rawLock)
        {
            bool target = _tree.IsTarget(pid);
            _tree.Stop(pid);
            _active = _tree.ActiveCount;
            if (target && !_rawFrozen)
            {
                _records.Add(TraceRecord.Stop(time, pid)); // 대상이 아닌 프로세스의 종료는 TraceBuilder 에서도 할 일이 없어 담지 않음
            }
        }
    }

    internal void NameKey(ulong key, string? name)
    {
        if (_rawFrozen && _ioSummary is null)
        {
            return;
        }

        lock (_rawLock)
        {
            _names.NameKey(key, name);
        }
    }

    internal void CloseObject(ulong fileObject)
    {
        if (_rawFrozen && _ioSummary is null)
        {
            return;
        }

        lock (_rawLock)
        {
            _names.CloseObject(fileObject);
        }
    }

    internal void OpenFile(int pid, ulong fileObject, string? name, uint createOptions = 0)
    {
        if (_rawFrozen && _ioSummary is null)
        {
            return;
        }

        lock (_rawLock)
        {
            _names.NameObject(fileObject, name);
            if (_ioSummary is not null && createOptions != 0 && !string.IsNullOrEmpty(name) && _tree.IsTarget(pid))
            {
                _ioSummary.AddOpen(_devices.ToDosPath(name), createOptions); // 열 때 준 옵션 (순차·임의 접근·캐시 없음): 입출력 요약의 파일별 읽기 방식에 붙음
            }

            if (!_rawFrozen && _listener is { } listener && !string.IsNullOrEmpty(name) && _tree.IsTarget(pid))
            {
                listener.OnOpen(_devices.ToDosPath(name));
            }
        }
    }

    internal void AddRead(DateTime time, int pid, ulong key, ulong fileObject, long offset, long size, uint ioFlags = 0)
    {
        if (_rawFrozen && _ioSummary is null)
        {
            return;
        }

        lock (_rawLock)
        {
            if ((_rawFrozen && _ioSummary is null) || !_tree.IsTarget(pid))
            {
                return;
            }

            bool recordRaw = !_rawFrozen && _window.IsOpen;
            (string? name, string? fallback) = _names.AtRead(key, fileObject);
            if (_ioSummary is not null)
            {
                if (name is not null)
                {
                    _ioSummary.AddProcessRead(_devices.ToDosPath(name), 1, size, ioFlags);
                }
                else
                {
                    AddPendingReadMode(key, fallback, size, ioFlags);
                }
            }

            if (!recordRaw)
            {
                return;
            }

            _window.Count();
            if (name is null)
            {
                _pending.Add((_records.Count, key, fallback));
            }

            if (_listener is { } listener && _names.ResolveNow(key, fileObject, offset, size) is { } live)
            {
                listener.OnRead(_devices.ToDosPath(live), offset, size); // 못 푼 읽기는 FileNames 가 보류했다가 이름이 오면 OnLateRead 로 알림
            }

            _records.Add(TraceRecord.Read(time, pid, name ?? "", offset, size));
        }
    }

    /// 디스크 읽기 완료: 잠금 없이 요청 표만 갱신합니다 (이 이벤트는 처리 스레드 하나에서만 오고, 원시 기록 상태와 만나지 않음).
    internal void AddDiskRead(int diskNumber, long byteOffset, long bytes, double elapsedMilliseconds, DateTime time, bool paging) =>
        _ioSummary?.AddDiskRead(diskNumber >= 0 ? (uint)diskNumber : uint.MaxValue, byteOffset, bytes, ToMicroseconds(elapsedMilliseconds), SessionTimeUs(time), paging);

    private void CloseRawWindow(DateTimeOffset atUtc)
    {
        if (_rawWindowStartedUs is not { } started)
        {
            return;
        }

        _rawWindows.Add(new IoSummaryWindow(started, Math.Max(started, SessionTimeUs(atUtc))));
        _rawWindowStartedUs = null;
    }

    private void AddPendingReadMode(ulong key, string? fallback, long bytes, uint flags)
    {
        var pendingKey = new PendingReadModeKey(key, fallback, flags);
        if (_pendingReadModes.TryGetValue(pendingKey, out PendingReadModeTotals totals))
        {
            totals.Requests++;
            totals.Bytes += Math.Max(0, bytes);
            _pendingReadModes[pendingKey] = totals;
            return;
        }

        if (_pendingReadModes.Count >= MaxPendingReadModeGroups)
        {
            _ioSummary!.AddProcessRead(null, 1, bytes, flags); // 이름 대기 표가 찼으면 더 저장하지 않고 미해결 읽기로 남김
            return;
        }

        _pendingReadModes.Add(pendingKey, new PendingReadModeTotals(1, Math.Max(0, bytes)));
    }

    private void FlushPendingReadModes()
    {
        if (_readModesFlushed || _ioSummary is null)
        {
            return;
        }

        foreach ((PendingReadModeKey key, PendingReadModeTotals totals) in _pendingReadModes)
        {
            string? name = _names.Settle(key.FileKey, key.Fallback);
            _ioSummary.AddProcessRead(name is null ? null : _devices.ToDosPath(name), totals.Requests, totals.Bytes, key.Flags);
        }

        _pendingReadModes.Clear();
        _readModesFlushed = true;
    }

    /// 관찰 시작을 0 으로 한 마이크로초. 관찰 시작보다 앞선 이벤트는 음수로 남겨 원시 0점과의 차이가 잘리지 않게 합니다 (시간 원점을 쓰는 쪽이 따로 0 으로 자름).
    private long SessionTimeUs(DateTimeOffset time) => (long)(time.ToUniversalTime() - _originUtc).TotalMicroseconds;

    private long SessionTimeUs(DateTime time) => SessionTimeUs(new DateTimeOffset(time));

    private static long ToMicroseconds(double milliseconds) =>
        double.IsFinite(milliseconds) && milliseconds > 0 && milliseconds < long.MaxValue / 1000d
            ? (long)Math.Round(milliseconds * 1000d, MidpointRounding.AwayFromZero)
            : 0;

    internal void AddWrite(DateTime time, int pid, ulong key, ulong fileObject)
    {
        // 쓰기도 수집 구간 안만 봄: 예전 창마다 세션과 같은 범위라 가변 파일 판정이 달라지지 않고, 구간 밖에서 표가 자라지 않음
        if (_rawFrozen)
        {
            return;
        }

        lock (_rawLock)
        {
            if (_rawFrozen || !_window.IsOpen || !_tree.IsTarget(pid) || !_written.Add(key))
            {
                return;
            }

            (string? name, string? fallback) = _names.AtRead(key, fileObject);
            if (name is null)
            {
                _pending.Add((_records.Count, key, fallback));
            }

            _records.Add(TraceRecord.Write(time, pid, name ?? ""));
        }
    }

    private static string FirstToken(string commandLine)
    {
        string s = commandLine.TrimStart();
        if (s.StartsWith('"'))
        {
            int close = s.IndexOf('"', 1);
            return close < 0 ? s[1..] : s[1..close];
        }

        int space = s.IndexOf(' ');
        return space < 0 ? s : s[..space];
    }

    private readonly record struct PendingReadModeKey(ulong FileKey, string? Fallback, uint Flags);

    private record struct PendingReadModeTotals(long Requests, long Bytes);
}
