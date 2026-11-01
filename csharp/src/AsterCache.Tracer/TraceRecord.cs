namespace AsterCache.Tracer;

public enum RecordKind
{
    ProcessStart,
    ProcessStop,
    Read,
}

/// ETW 에서 모은 한 건. ProcessStart: Path = 실행 파일 경로 (또는 이름), ParentPid 사용.
/// Read: Path = 드라이브 문자 경로, Offset·Length 사용. 도착(시간) 순서를 그대로 유지합니다.
public readonly record struct TraceRecord(RecordKind Kind, DateTime Time, int Pid, int ParentPid, string Path, long Offset, long Length)
{
    public static TraceRecord Start(DateTime time, int pid, int parentPid, string image) => new(RecordKind.ProcessStart, time, pid, parentPid, image, 0, 0);

    public static TraceRecord Stop(DateTime time, int pid) => new(RecordKind.ProcessStop, time, pid, 0, "", 0, 0);

    public static TraceRecord Read(DateTime time, int pid, string path, long offset, long length) => new(RecordKind.Read, time, pid, 0, path, offset, length);
}
