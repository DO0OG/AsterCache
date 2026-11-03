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

/// 읽기 결과 (Python read_ops 의 stats).
public sealed record StreamStats(long Bytes, long Requests, long Errors, double Seconds);

/// ops 를 파일 캐시로 가져옵니다. 쓰기 없음. Python prefetch/reader.py read_ops (method read | pvm).
public static class ZoneStreamer
{
    public const int DefaultChunkBytes = 1 << 20;

    /// 취소 토큰을 확인하는 간격: 이만큼 읽을 때마다 (Pvm 은 이 크기 조각마다) 확인해서, 64 MiB 묶음 도중에도 게임이 뜨면 멈춥니다. Overlapped 는 확인하지 않음.
    public const long CancelCheckBytes = 8L << 20;
    private const long Page = 4096;

    public static StreamStats Run(IReadOnlyList<ReadOp> ops, StreamMethod method = StreamMethod.Pvm, int chunkBytes = DefaultChunkBytes, int queueDepth = 2, CancellationToken ct = default)
    {
        var watch = Stopwatch.StartNew();
        (long bytes, long requests, long errors) = method switch
        {
            StreamMethod.Pvm => RunPvm(ops, ct),
            StreamMethod.Overlapped => OverlappedReader.Run(ops, queueDepth, chunkBytes),
            _ => RunRead(ops, chunkBytes, ct),
        };
        return new StreamStats(bytes, requests, errors, Math.Round(watch.Elapsed.TotalSeconds, 4));
    }

    // Python _worker(reuse=True, threads=1): 한 줄씩 순서대로, 파일 핸들 유지, 오류가 나면 그 핸들을 버리고 계속.
    private static (long, long, long) RunRead(IReadOnlyList<ReadOp> ops, int chunk, CancellationToken ct)
    {
        long sinceCheck = 0;
        long bytes = 0;
        long requests = 0;
        long errors = 0;
        byte[] buf = new byte[chunk];
        var handles = new Dictionary<string, SafeFileHandle>();
        try
        {
            foreach (ReadOp op in ops)
            {
                try
                {
                    if (!handles.TryGetValue(op.Path, out SafeFileHandle? h))
                    {
                        h = File.OpenHandle(op.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        handles[op.Path] = h;
                    }

                    long left = op.Length;
                    long pos = op.Offset;
                    while (left > 0)
                    {
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
    private static (long, long, long) RunPvm(IReadOnlyList<ReadOp> ops, CancellationToken ct)
    {
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

                    if (!views.TryGetValue(op.Path, out FileMapping? view))
                    {
                        views[op.Path] = view = FileMapping.Open(op.Path);
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

            // 범위를 CancelCheckBytes 크기 조각으로 나눠 조각마다 한 번씩 부르고 (순서 그대로), 조각 사이에서 취소를 확인합니다.
            List<(Win32MemoryRangeEntry[] Slice, long Bytes, int Ranges)> slices = Slices(ranges, pads);
            for (int i = 0; i < slices.Count; i++)
            {
                if (MemoryPrefetch.Prefetch(slices[i].Slice))
                {
                    bytes += slices[i].Bytes;
                    requests += slices[i].Ranges;
                }
                else
                {
                    errors++;
                }

                if (i < slices.Count - 1 && ct.IsCancellationRequested)
                {
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
