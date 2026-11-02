using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

namespace AsterCache.Tracer;

/// 커널 파서 이벤트 → 대상 프로세스 트리의 TraceRecord. EtlReader (파일) 와 LiveSession (실시간) 이 함께 씁니다.
/// 파일 이름은 직접 짠 표로 풉니다: FileIOName / FileIOFileCreate / FileIOFileRundown 은 FileKey, FileIOCreate 는 FileObject 를 열쇠로 삼습니다.
/// 읽기는 FileKey 이름이 이미 있으면 그것을, 없으면 Finish 에서 (정리 (rundown) 이벤트는 세션 끝에 옴) FileKey 이름을 먼저, 없으면 읽을 때의 FileObject 이름을 씁니다.
/// FileObject 주소는 닫힌 뒤 다른 파일에 재사용되고 이벤트가 유실되면 옛 이름이 남으므로, 실제 .etl 로 확인한 대로 FileKey 이름을 우선합니다.
internal sealed class KernelCollector
{
    private readonly ProcessTree _tree;
    private readonly DevicePaths _devices;
    private readonly FileNames _names = new();
    private readonly List<TraceRecord> _records = [];
    private readonly List<(int Index, ulong Key, string? ObjName)> _pending = [];

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
        kernel.FileIOName += d => _names.NameKey(d.FileKey, d.FileName);
        kernel.FileIOFileCreate += d => _names.NameKey(d.FileKey, d.FileName);
        kernel.FileIOFileRundown += d => _names.NameKey(d.FileKey, d.FileName);
        kernel.FileIOCreate += d => _names.NameObject(d.FileObject, d.FileName);
        kernel.FileIOClose += d => _names.CloseObject(d.FileObject);
        kernel.FileIORead += OnRead;
    }

    /// 이름을 끝내 못 찾은 읽기 수 (Finish 뒤에 읽음).
    public int Unresolved { get; private set; }

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
        _tree.Start(d.ProcessID, d.ParentID, image);
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
