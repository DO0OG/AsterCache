namespace AsterCache.Core.Trace;

public sealed record IoSummaryWindow(long StartedUs, long EndedUs);

/// 파일 표의 한 행. ChangedDuringSession 은 관찰 시작 뒤에 쓰인 파일이라 끝난 뒤 읽은 배치가 읽을 때의 배치와 다를 수 있다는 표시입니다.
public sealed record IoSummaryFile(string Path, bool ChangedDuringSession = false);

/// (파일, 64 KiB 블록) 하나에 닿은 디스크 읽기. 한 요청이 여러 블록에 걸치므로 Requests 는 "이 블록에 닿은 요청 수"이고 요청 단위 총계는 품질 수치에 따로 있습니다.
public sealed record IoSummaryRow(
    int FileNumber,
    long BlockNumber,
    long Requests,
    long Bytes,
    long LatencySumUs,
    long LatencyMaxUs,
    long FirstSessionUs,
    long PagingRequests);

/// 한 분류의 요청 수·바이트·지연 합 (지연은 디스크 스택에 내려간 때부터 완료까지라 장치 앞 대기를 포함합니다).
public readonly record struct IoClassTotals(long Reads, long Bytes, long LatencySumUs)
{
    public IoClassTotals Plus(long reads, long bytes, long latencyUs) => new(Reads + reads, Bytes + bytes, LatencySumUs + latencyUs);
}

/// 완료 총계 = 블록에 연결됨 + 다른 디스크 + 파일 구간 밖 + 변환 안 함 + 상한 초과로 버림.
public sealed record IoSummaryQuality(
    IoClassTotals Total,
    long LatencyMaxUs,
    IoClassTotals Linked,
    IoClassTotals OtherDisk,
    IoClassTotals NoFile,
    IoClassTotals NotConverted,
    IoClassTotals Dropped,
    long PagingReads,
    long PagingBytes,
    long EventsLost,
    long BlockTableDropped = 0,
    long FilesScanned = 0,
    long FilesChanged = 0,
    long FilesUnreadable = 0,
    long VolumesSkipped = 0,
    long ScanMs = 0,
    bool ScanIncomplete = false)
{
    public bool Truncated => Dropped.Reads > 0 || BlockTableDropped > 0 || ScanIncomplete;
}

public sealed record IoSummary(
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset EndedAtUtc,
    IReadOnlyList<IoSummaryWindow> RawWindows,
    IoSummaryQuality Quality,
    IReadOnlyList<IoSummaryFile> Files,
    IReadOnlyList<IoSummaryRow> Rows,
    string SessionId = "",
    string? RawFingerprint = null,
    long? RawTraceZeroUs = null,
    DateTimeOffset? RawTraceZeroAtUtc = null)
{
    public string FormatLogLine()
    {
        IoSummaryQuality q = Quality;
        double linked = q.Total.Bytes == 0 ? 0 : 100d * q.Linked.Bytes / q.Total.Bytes;
        double seconds = Math.Max(0, (EndedAtUtc - ObservedAtUtc).TotalSeconds);
        return $"I/O summary: observed {seconds:0.0}s, {q.Total.Reads} disk reads/{Mib(q.Total.Bytes):0.0} MiB, block linked {q.Linked.Reads} reads/{Mib(q.Linked.Bytes):0.0} MiB ({linked:0.0}% of bytes), "
            + $"other disk {q.OtherDisk.Reads} reads/{Mib(q.OtherDisk.Bytes):0.0} MiB, no file {q.NoFile.Reads} reads/{Mib(q.NoFile.Bytes):0.0} MiB, not converted {q.NotConverted.Reads} reads/{Mib(q.NotConverted.Bytes):0.0} MiB, "
            + $"dropped {q.Dropped.Reads} reads/{Mib(q.Dropped.Bytes):0.0} MiB, latency {q.Total.LatencySumUs / 1000d:0.0} ms sum/{q.LatencyMaxUs / 1000d:0.0} ms max, {q.EventsLost} events lost, "
            + $"truncated {q.Truncated} (blocks {q.BlockTableDropped}), {q.FilesScanned} files scanned in {q.ScanMs} ms, {q.FilesChanged} changed during session";
    }

    private static double Mib(long bytes) => bytes / (double)(1 << 20);
}
