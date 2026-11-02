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
    private readonly ProcessTree _tree;
    private readonly DevicePaths _devices;
    private readonly FileNames _names;
    private readonly List<TraceRecord> _records = [];
    private readonly List<(int Index, ulong Key, string? ObjName)> _pending = [];
    private readonly LiveListener? _listener;
    private volatile int _active;
    private volatile bool _sawTarget;

    public KernelCollector(KernelTraceEventParser kernel, IReadOnlyCollection<string> exeNames, DevicePaths devices, LiveListener? listener = null)
    {
        _tree = new ProcessTree(exeNames);
        _devices = devices;
        _listener = listener;
        _names = new FileNames((name, offset, size) => _listener?.OnLateRead?.Invoke(_devices.ToDosPath(name), offset, size));
        kernel.ProcessStart += OnProcessStart;
        kernel.ProcessDCStart += OnProcessStart; // 세션 시작 때 이미 떠 있던 프로세스
        kernel.ProcessStop += d =>
        {
            _tree.Stop(d.ProcessID);
            _active = _tree.ActiveCount;
            _records.Add(TraceRecord.Stop(d.TimeStamp, d.ProcessID));
        };
        kernel.FileIOName += d => _names.NameKey(d.FileKey, d.FileName);
        kernel.FileIOFileCreate += d => _names.NameKey(d.FileKey, d.FileName);
        kernel.FileIOFileRundown += d => _names.NameKey(d.FileKey, d.FileName);
        kernel.FileIOCreate += d =>
        {
            _names.NameObject(d.FileObject, d.FileName);
            if (_listener is not null && !string.IsNullOrEmpty(d.FileName) && _tree.IsTarget(d.ProcessID))
            {
                _listener.OnOpen(_devices.ToDosPath(d.FileName));
            }
        };
        kernel.FileIOClose += d => _names.CloseObject(d.FileObject);
        kernel.FileIORead += OnRead;
    }

    /// 이름을 끝내 못 찾은 읽기 수 (Finish 뒤에 읽음).
    public int Unresolved { get; private set; }

    /// 대상 프로세스 트리가 지금 하나라도 살아 있는가 / 한 번이라도 본 적이 있는가 (다른 스레드에서 읽음).
    public bool TargetsActive => _active > 0;

    public bool SawTarget => _sawTarget;

    /// 실시간 알림 수: 읽는 순간 이름이 풀린 읽기 / 이름이 늦게 와서 늦게 알린 읽기 (Stop 뒤에 읽음).
    public long ResolvedReads => _names.Resolved;

    public long LateReads => _names.Late;

    public List<TraceRecord> Finish()
    {
        var dropped = new HashSet<int>();
        foreach ((int index, ulong key, string? objName) in _pending)
        {
            string? name = _names.Settle(key, objName);
            if (name is null)
            {
                dropped.Add(index);
                Unresolved++;
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
            result.Add(r.Kind == RecordKind.Read ? r with { Path = _devices.ToDosPath(r.Path) } : r);
        }

        return result;
    }

    private void OnProcessStart(ProcessTraceData d)
    {
        // 짧은 ImageFileName (15자) 보다 전체 경로가 있으면 그것을, 없으면 명령줄 첫 토큰을 먼저 씁니다.
        string image = !string.IsNullOrEmpty(d.KernelImageFileName) ? d.KernelImageFileName
            : !string.IsNullOrEmpty(d.CommandLine) ? FirstToken(d.CommandLine)
            : d.ImageFileName ?? "";
        if (_tree.Start(d.ProcessID, d.ParentID, image))
        {
            _sawTarget = true;
        }

        _active = _tree.ActiveCount;
        _records.Add(TraceRecord.Start(d.TimeStamp, d.ProcessID, d.ParentID, image));
    }

    private void OnRead(FileIOReadWriteTraceData d)
    {
        if (!_tree.IsTarget(d.ProcessID))
        {
            return;
        }

        (string? name, string? fallback) = _names.AtRead(d.FileKey, d.FileObject);
        if (name is null)
        {
            _pending.Add((_records.Count, d.FileKey, fallback));
        }

        if (_listener is not null && _names.ResolveNow(d.FileKey, d.FileObject, d.Offset, d.IoSize) is { } live)
        {
            _listener.OnRead(_devices.ToDosPath(live), d.Offset, d.IoSize); // 못 푼 읽기는 FileNames 가 보류했다가 이름이 오면 OnLateRead 로 알림
        }

        _records.Add(TraceRecord.Read(d.TimeStamp, d.ProcessID, name ?? "", d.Offset, d.IoSize));
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
}
