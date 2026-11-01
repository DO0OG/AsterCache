using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace AsterCache.Tracer;

/// 커널 파서 이벤트 → 대상 프로세스 트리의 TraceRecord. EtlReader (파일) 와 LiveSession (실시간) 이 함께 씁니다.
/// 파일 이름은 직접 짠 표로 풉니다: FileIOName / FileIOFileCreate / FileIOFileRundown 은 FileKey, FileIOCreate 는 FileObject 를 열쇠로 삼고
/// (TraceEvent 의 FileName 속성과 같은 규칙), 읽기는 FileKey 먼저, 없으면 FileObject 로 찾습니다.
/// 읽기가 이름보다 먼저 오는 경우 (실행 전에 열린 파일은 이름이 정리 (rundown) 이벤트로 세션 끝에 옴) 는 Finish 에서 한꺼번에 풉니다.
internal sealed class KernelCollector
{
    private readonly ProcessTree _tree;
    private readonly DevicePaths _devices;
    private readonly Dictionary<ulong, string> _names = [];
    private readonly List<TraceRecord> _records = [];
    private readonly List<(int Index, ulong Key, ulong Obj)> _pending = [];

    public KernelCollector(KernelTraceEventParser kernel, IReadOnlyCollection<string> exeNames, DevicePaths devices)
    {
        _tree = new ProcessTree(exeNames);
        _devices = devices;
        kernel.ProcessStart += OnProcessStart;
        kernel.ProcessDCStart += OnProcessStart; // 세션 시작 때 이미 떠 있던 프로세스
        kernel.ProcessStop += d =>
        {
            _tree.Stop(d.ProcessID);
            _records.Add(TraceRecord.Stop(d.TimeStamp, d.ProcessID));
        };
        kernel.FileIOName += d => AddName(d.FileKey, d.FileName);
        kernel.FileIOFileCreate += d => AddName(d.FileKey, d.FileName);
        kernel.FileIOFileRundown += d => AddName(d.FileKey, d.FileName);
        kernel.FileIOCreate += d => AddName(d.FileObject, d.FileName);
        kernel.FileIORead += OnRead;
    }

    /// 이름을 끝내 못 찾은 읽기 수 (Finish 뒤에 읽음).
    public int Unresolved { get; private set; }

    public List<TraceRecord> Finish()
    {
        var dropped = new HashSet<int>();
        foreach ((int index, ulong key, ulong obj) in _pending)
        {
            string? name = Lookup(key, obj);
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
        _tree.Start(d.ProcessID, d.ParentID, image);
        _records.Add(TraceRecord.Start(d.TimeStamp, d.ProcessID, d.ParentID, image));
    }

    private void OnRead(FileIOReadWriteTraceData d)
    {
        if (!_tree.IsTarget(d.ProcessID))
        {
            return;
        }

        string? name = Lookup(d.FileKey, d.FileObject);
        if (name is null)
        {
            _pending.Add((_records.Count, d.FileKey, d.FileObject));
        }

        _records.Add(TraceRecord.Read(d.TimeStamp, d.ProcessID, name ?? "", d.Offset, d.IoSize));
    }

    private void AddName(ulong id, string? name)
    {
        if (id != 0 && !string.IsNullOrEmpty(name))
        {
            _names[id] = name;
        }
    }

    private string? Lookup(ulong key, ulong obj) =>
        _names.TryGetValue(key, out string? n) ? n : _names.TryGetValue(obj, out n) ? n : null;

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
