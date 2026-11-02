using Microsoft.Diagnostics.Tracing;

namespace AsterCache.Tracer;

/// .etl 파일 (wpr -start FileIO 나 LiveSession 이 만든 것) 을 읽어 대상 프로세스 트리의 읽기만 모읍니다.
/// 이 PC 의 드라이브 대응으로 경로를 바꾸므로, 다른 PC 에서 수집한 .etl 은 devices 를 직접 넘겨야 합니다.
public static class EtlReader
{
    public static List<TraceRecord> Read(string etlPath, IReadOnlyCollection<string> exeNames, DevicePaths? devices = null) =>
        ReadWithStats(etlPath, exeNames, devices).Records;

    /// Read 와 같고, 이름을 끝내 못 푼 읽기 수와 .etl 에 기록된 유실 이벤트 수도 돌려줍니다 (진단용).
    public static (List<TraceRecord> Records, int Unresolved, long EventsLost) ReadWithStats(string etlPath, IReadOnlyCollection<string> exeNames, DevicePaths? devices = null)
    {
        using var source = new ETWTraceEventSource(etlPath);
        var collector = new KernelCollector(source.Kernel, exeNames, devices ?? DevicePaths.FromSystem());
        source.Process();
        List<TraceRecord> records = collector.Finish();
        return (records, collector.Unresolved, source.EventsLost);
    }

    /// 변환 결과를 눈으로 확인하는 진단 출력: 요약 한 줄 + 처음 count 건의 읽기 (이벤트명, pid, 오프셋, 길이, 경로). 경로는 공백이 있을 수 있어 마지막에 둡니다.
    public static IEnumerable<string> Describe(IReadOnlyList<TraceRecord> records, int unresolved, long eventsLost, int count)
    {
        yield return $"etl records={records.Count} reads={records.Count(r => r.Kind == RecordKind.Read)} process_starts={records.Count(r => r.Kind == RecordKind.ProcessStart)} unresolved_reads={unresolved} events_lost={eventsLost}";
        foreach (TraceRecord r in records.Where(r => r.Kind == RecordKind.Read).Take(Math.Max(0, count)))
        {
            yield return $"FileIORead pid={r.Pid} offset={r.Offset} length={r.Length} path={r.Path}";
        }
    }
}
