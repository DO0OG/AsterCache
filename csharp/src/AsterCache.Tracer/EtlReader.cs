using Microsoft.Diagnostics.Tracing;

namespace AsterCache.Tracer;

/// .etl 파일 (wpr -start FileIO 나 LiveSession 이 만든 것) 을 읽어 대상 프로세스 트리의 읽기만 모읍니다.
/// 이 PC 의 드라이브 대응으로 경로를 바꾸므로, 다른 PC 에서 수집한 .etl 은 devices 를 직접 넘겨야 합니다.
public static class EtlReader
{
    public static List<TraceRecord> Read(string etlPath, IReadOnlyCollection<string> exeNames, DevicePaths? devices = null)
    {
        using var source = new ETWTraceEventSource(etlPath);
        var collector = new KernelCollector(source.Kernel, exeNames, devices ?? DevicePaths.FromSystem());
        source.Process();
        return collector.Finish();
    }
}
