using System.ComponentModel;
using System.Diagnostics;
using AsterCache.Core.Prefetch;
using AsterCache.Prefetch.Modes;
using AsterCache.Windows;
using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Prefetch;

/// 존 순서 읽기 방식. Pvm 이 기본 (측정상 read 보다 약 2배 빠름, 결과 prefetch_methods_20261102), Read 는 폴백.
public enum StreamMethod
{
    /// 파일마다 읽기 전용 매핑 후 모든 범위를 존 순서 WIN32_MEMORY_RANGE_ENTRY 배열로 PrefetchVirtualMemory 한 번.
    Pvm,

    /// 버퍼드 ReadFile 을 큐 깊이 1 (엄격한 존 순서), 파일 핸들 유지, 조각 크기 chunkBytes (기본 1 MiB) 로 반복.
    Read,

    /// FILE_FLAG_OVERLAPPED 버퍼드 읽기를 queueDepth 개까지 동시에, 발행은 항상 존 순서 (Modes/OverlappedReader).
    Overlapped,
}

/// 읽기 결과 (Python read_ops 의 stats). Aborted: 취소로 일부를 건너뜀. RequestedBytes: 요청한 길이 합 (파일 크기로 자르기 전).
/// HintAcceptedBytes: Pvm 에서 PrefetchVirtualMemory 가 받아들인 바이트일 뿐 실제로 상주한다는 뜻이 아님 (Bytes 에도 같은 값이 들어감, 상주 추정은 CacheProbe 로).
/// BufferedBytes: Pvm 에서 스파스 파일이라 매핑하지 않고 버퍼드로 읽은 바이트 (Bytes 에는 들어가고 HintAcceptedBytes 에는 안 들어감).
public sealed record StreamStats(long Bytes, long Requests, long Errors, double Seconds, bool Aborted = false, long RequestedBytes = 0, long HintAcceptedBytes = 0)
{
    public long GapBytes { get; init; }

    public long BufferedBytes { get; init; }

    /// 예열 완료 로그 줄에 붙이는 말 (스파스 파일을 버퍼드로 읽은 것이 MiB 로 0 이면 빈 문자열).
    public string BufferedNote() => BufferedBytes >> 20 > 0 ? $", {BufferedBytes >> 20} MiB of sparse files by buffered read" : string.Empty;
}

/// ops 를 파일 캐시로 가져옵니다. 쓰기 없음. Python prefetch/reader.py read_ops (method read | pvm).
public static class ZoneStreamer
{
    public const int DefaultChunkBytes = 1 << 20;

    /// 취소 토큰을 확인하는 간격: 이만큼 읽을 때마다 (Pvm 은 이 크기 조각마다) 확인해서, 64 MiB 묶음 도중에도 게임이 뜨면 멈춥니다. Overlapped 는 확인하지 않음.
    public const long CancelCheckBytes = 8L << 20;
    private const long Page = 4096;

    public static StreamStats Run(IReadOnlyList<ReadOp> ops, StreamMethod method = StreamMethod.Pvm, int chunkBytes = DefaultChunkBytes, int queueDepth = 2, CancellationToken ct = default,
        Action<string, long, bool>? noteIssue = null)
    {
        var watch = Stopwatch.StartNew();
        bool aborted = false;
        long hint = 0;
        long buffered = 0;
        (long bytes, long requests, long errors) = method switch
        {
            StreamMethod.Pvm => RunPvmOrBuffered(ops, chunkBytes, ct, out aborted, out hint, out buffered, static slice => MemoryPrefetch.Prefetch(slice), FileGuards.FileAttributes, noteIssue),
            StreamMethod.Overlapped => OverlappedReader.Run(ops, queueDepth, chunkBytes, ct, out aborted),
            _ => RunRead(ops, chunkBytes, ct, out aborted),
        };
        return new StreamStats(bytes, requests, errors, Math.Round(watch.Elapsed.TotalSeconds, 4), aborted, ops.Sum(o => o.Length), hint) { BufferedBytes = buffered };
    }

    // 스파스 속성 파일은 섹션 (CreateFileMapping) 을 만드는 순간 그 파일의 캐시 페이지가 버려지므로 매핑하지 않고 버퍼드 읽기 (RunRead) 로 읽습니다.
    // 범위 목록을 스파스 여부가 같은 연속 구간으로 나눠 앞에서부터 차례로 처리하므로 읽는 순서는 그대로입니다. 속성을 조회할 수 없으면 스파스로 보지 않습니다.
    internal static (long, long, long) RunPvmOrBuffered(IReadOnlyList<ReadOp> ops, int chunk, CancellationToken ct, out bool aborted, out long hintAccepted, out long buffered,
        Func<Win32MemoryRangeEntry[], bool> prefetch, Func<string, uint> attributes, Action<string, long, bool>? noteIssue = null)
    {
        aborted = false;
        hintAccepted = 0;
        buffered = 0;
        if (ct.IsCancellationRequested)
        {
            aborted = ops.Count > 0;
            return (0, 0, 0); // 이미 취소됨: 속성 조회도 읽기도 하지 않음 (RunPvm·RunRead 와 같은 계약)
        }

        var sparse = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var parts = new List<(bool Sparse, int From, int To)>();
        for (int i = 0; i < ops.Count; i++)
        {
            if (!sparse.TryGetValue(ops[i].Path, out bool isSparse))
            {
                sparse[ops[i].Path] = isSparse = FileGuards.IsSparse(ops[i].Path, attributes);
            }

            if (parts.Count > 0 && parts[^1].Sparse == isSparse)
            {
                parts[^1] = (isSparse, parts[^1].From, i + 1);
            }
            else
            {
                parts.Add((isSparse, i, i + 1));
            }
        }

        if (parts.Count == 1 && !parts[0].Sparse)
        {
            return RunPvm(ops, ct, out aborted, out hintAccepted, prefetch, noteIssue); // 스파스 파일이 없으면 이전과 완전히 같음
        }

        long bytes = 0;
        long requests = 0;
        long errors = 0;
        byte[]? buffer = null; // 스파스 구간마다 1 MiB 를 새로 할당하지 않고 한 번의 호출 안에서 재사용
        foreach ((bool isSparse, int from, int to) in parts)
        {
            if (ct.IsCancellationRequested)
            {
                aborted = true; // 앞 구간이 끝난 뒤의 취소: 뒤 구간은 읽지 않음
                break;
            }

            IReadOnlyList<ReadOp> part = parts.Count == 1 ? ops : [.. ops.Skip(from).Take(to - from)];
            bool partAborted;
            long b;
            long r;
            long e;
            if (isSparse)
            {
                noteIssue?.Invoke("buffered read of sparse files", part.Sum(o => o.Length), true);
                // 앞읽기를 꺼서 요청한 범위만 읽음: 켜 두면 흩어진 목록에서 요청보다 13~23% 더 읽힘 (같은 PC 의 시험 파일 측정)
                (b, r, e) = RunRead(part, chunk, ct, out partAborted, buffer ??= new byte[chunk], FileOptions.RandomAccess);
                buffered += b;
            }
            else
            {
                (b, r, e) = RunPvm(part, ct, out partAborted, out long hint, prefetch, noteIssue);
                hintAccepted += hint;
            }

            (bytes, requests, errors) = (bytes + b, requests + r, errors + e);
            if (partAborted)
            {
                aborted = true;
                break;
            }
        }

        return (bytes, requests, errors);
    }

    // Python _worker(reuse=True, threads=1): 한 줄씩 순서대로, 파일 핸들 유지, 오류가 나면 그 핸들을 버리고 계속.
    private static (long, long, long) RunRead(IReadOnlyList<ReadOp> ops, int chunk, CancellationToken ct, out bool aborted, byte[]? buffer = null, FileOptions options = FileOptions.None)
    {
        aborted = false;
        if (ct.IsCancellationRequested)
        {
            aborted = ops.Count > 0;
            return (0, 0, 0); // 이미 취소됨: 아무것도 읽지 않음
        }

        long sinceCheck = 0;
        long bytes = 0;
        long requests = 0;
        long errors = 0;
        byte[] buf = buffer ?? new byte[chunk];
        var handles = new Dictionary<string, SafeFileHandle>();
        try
        {
            foreach (ReadOp op in ops)
            {
                if (ct.IsCancellationRequested)
                {
                    aborted = true;
                    return (bytes, requests, errors);
                }

                try
                {
                    if (!handles.TryGetValue(op.Path, out SafeFileHandle? h))
                    {
                        h = File.OpenHandle(op.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, options);
                        handles[op.Path] = h;
                    }

                    long left = op.Length;
                    long pos = op.Offset;
                    while (left > 0)
                    {
                        if (ct.IsCancellationRequested)
                        {
                            aborted = true;
                            return (bytes, requests, errors);
                        }

                        int got = RandomAccess.Read(h, buf.AsSpan(0, (int)Math.Min(left, chunk)), pos);
                        if (got == 0)
                        {
                            break;
                        }

                        left -= got;
                        pos += got;
                        bytes += got;
                        sinceCheck += got;
                        if (sinceCheck >= CancelCheckBytes)
                        {
                            sinceCheck = 0;
                            if (ct.IsCancellationRequested)
                            {
                                aborted = true;
                                return (bytes, requests, errors);
                            }
                        }
                    }

                    requests++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    errors++;
                    if (handles.Remove(op.Path, out SafeFileHandle? bad))
                    {
                        bad.Dispose();
                    }
                }
            }
        }
        finally
        {
            foreach (SafeFileHandle h in handles.Values)
            {
                h.Dispose();
            }
        }

        return (bytes, requests, errors);
    }

    // Python _read_pvm 과 달리 파일 하나의 열기·매핑 실패 (게임이 배타적으로 연 파일 등) 는 그 파일만 오류 1 로 건너뛰고 나머지는 계속합니다 (바이트·요청은 호출이 성공했을 때만 셈).
    // prefetch: 조각 한 번의 PrefetchVirtualMemory 호출 (시험에서 가짜로 바꿔 호출 수와 취소 시점을 확인).
    internal static (long, long, long) RunPvm(IReadOnlyList<ReadOp> ops, CancellationToken ct, out bool aborted, out long hintAccepted,
        Func<Win32MemoryRangeEntry[], bool> prefetch, Action<string, long, bool>? noteIssue = null)
    {
        aborted = false;
        hintAccepted = 0;
        if (ct.IsCancellationRequested)
        {
            aborted = ops.Count > 0;
            return (0, 0, 0); // 이미 취소됨: 매핑도 PVM 호출도 하지 않음
        }

        var views = new Dictionary<string, FileMapping>();
        var sizes = new Dictionary<string, long>();
        var ranges = new List<Win32MemoryRangeEntry>();
        var pads = new List<long>(); // 범위 앞쪽 쪽(page) 정렬로 늘어난 바이트 (읽은 바이트에서 뺌)
        long bytes = 0;
        long requests = 0;
        long errors = 0;
        var failed = new HashSet<string>();
        try
        {
            long need = 0;
            foreach (ReadOp op in ops)
            {
                if (ct.IsCancellationRequested)
                {
                    aborted = true; // 열거 도중 취소: 이후 파일은 매핑하지 않음
                    break;
                }

                if (failed.Contains(op.Path))
                {
                    continue;
                }

                try
                {
                    if (!sizes.TryGetValue(op.Path, out long size))
                    {
                        sizes[op.Path] = size = new FileInfo(op.Path).Length;
                    }

                    long n = Math.Min(op.Length, size - op.Offset);
                    if (n <= 0)
                    {
                        continue;
                    }

                    if (ct.IsCancellationRequested)
                    {
                        aborted = true;
                        break;
                    }

                    if (!views.TryGetValue(op.Path, out FileMapping? view))
                    {
                        views[op.Path] = view = FileMapping.Open(op.Path);
                    }

                    if (ct.IsCancellationRequested)
                    {
                        aborted = true;
                        break;
                    }

                    long a = op.Offset - (op.Offset % Page);
                    ranges.Add(new Win32MemoryRangeEntry { VirtualAddress = view.BaseAddress + (nint)a, NumberOfBytes = (nuint)(op.Offset + n - a) });
                    pads.Add(op.Offset - a);
                    need += n;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
                {
                    errors++;
                    failed.Add(op.Path);
                }
            }

            // 범위를 CancelCheckBytes 크기 조각으로 나눠 조각마다 한 번씩 부르고 (순서 그대로), 열거 직전과 모든 호출의 직전·직후에 취소를 확인합니다.
            if (ct.IsCancellationRequested)
            {
                aborted |= ranges.Count > 0;
                return (bytes, requests, errors);
            }

            List<(Win32MemoryRangeEntry[] Slice, long Bytes, int Ranges)> slices = Slices(ranges, pads);
            long totalBytes = slices.Sum(slice => slice.Bytes);
            for (int i = 0; i < slices.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    aborted = true;
                    break;
                }

                noteIssue?.Invoke($"pvm fragment {i + 1}/{slices.Count} started, progress {bytes}/{totalBytes} bytes", slices[i].Bytes, true);
                if (ct.IsCancellationRequested)
                {
                    aborted = true;
                    break;
                }

                bool accepted = prefetch(slices[i].Slice);
                if (accepted)
                {
                    bytes += slices[i].Bytes;
                    hintAccepted += slices[i].Bytes;
                    requests += slices[i].Ranges;
                }
                else
                {
                    errors++;
                }

                noteIssue?.Invoke($"pvm fragment {i + 1}/{slices.Count} completed, progress {bytes}/{totalBytes} bytes", slices[i].Bytes, true);

                if (i < slices.Count - 1 && ct.IsCancellationRequested)
                {
                    aborted = true;
                    break;
                }
            }
        }
        finally
        {
            foreach (FileMapping v in views.Values)
            {
                v.Dispose();
            }
        }

        return (bytes, requests, errors);
    }

    // Bytes: 읽은 바이트 (범위 앞쪽 정렬분 제외), Ranges: 이 조각에서 끝까지 담긴 범위 수 (조각에 걸친 범위는 마지막 조각에서만 셈).
    private static List<(Win32MemoryRangeEntry[] Slice, long Bytes, int Ranges)> Slices(List<Win32MemoryRangeEntry> ranges, List<long> pads)
    {
        var slices = new List<(Win32MemoryRangeEntry[], long, int)>();
        var cur = new List<Win32MemoryRangeEntry>();
        long span = 0;
        long bytes = 0;
        int whole = 0;
        for (int k = 0; k < ranges.Count; k++)
        {
            Win32MemoryRangeEntry r = ranges[k];
            long off = 0;
            while (off < (long)r.NumberOfBytes)
            {
                long take = Math.Min((long)r.NumberOfBytes - off, CancelCheckBytes - span);
                cur.Add(new Win32MemoryRangeEntry { VirtualAddress = r.VirtualAddress + (nint)off, NumberOfBytes = (nuint)take });
                span += take;
                bytes += take - (off == 0 ? pads[k] : 0);
                off += take;
                whole += off == (long)r.NumberOfBytes ? 1 : 0;
                if (span >= CancelCheckBytes)
                {
                    slices.Add(([.. cur], bytes, whole));
                    (cur, span, bytes, whole) = ([], 0, 0, 0);
                }
            }
        }

        if (cur.Count > 0)
        {
            slices.Add(([.. cur], bytes, whole));
        }

        return slices;
    }
}
