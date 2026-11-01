using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using AsterCache.Core.Prefetch;
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
}

/// 읽기 결과 (Python read_ops 의 stats).
public sealed record StreamStats(long Bytes, long Requests, long Errors, double Seconds);

/// ops 를 파일 캐시로 가져옵니다. 쓰기 없음. Python prefetch/reader.py read_ops (method read | pvm).
public static class ZoneStreamer
{
    public const int DefaultChunkBytes = 1 << 20;
    private const long Page = 4096;

    public static StreamStats Run(IReadOnlyList<ReadOp> ops, StreamMethod method = StreamMethod.Pvm, int chunkBytes = DefaultChunkBytes)
    {
        var watch = Stopwatch.StartNew();
        (long bytes, long requests, long errors) = method == StreamMethod.Pvm ? RunPvm(ops) : RunRead(ops, chunkBytes);
        return new StreamStats(bytes, requests, errors, Math.Round(watch.Elapsed.TotalSeconds, 4));
    }

    // Python _worker(reuse=True, threads=1): 한 줄씩 순서대로, 파일 핸들 유지, 오류가 나면 그 핸들을 버리고 계속.
    private static (long, long, long) RunRead(IReadOnlyList<ReadOp> ops, int chunk)
    {
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

    // Python _read_pvm(per_file=False): 어느 단계든 실패하면 오류 1 로 전체를 멈춥니다 (바이트·요청은 호출이 성공했을 때만 셈).
    private static (long, long, long) RunPvm(IReadOnlyList<ReadOp> ops)
    {
        var views = new Dictionary<string, FileMapping>();
        var sizes = new Dictionary<string, long>();
        var ranges = new List<Win32MemoryRangeEntry>();
        long bytes = 0;
        long requests = 0;
        long errors = 0;
        try
        {
            long need = 0;
            foreach (ReadOp op in ops)
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
                need += n;
            }

            if (ranges.Count > 0)
            {
                if (MemoryPrefetch.Prefetch(CollectionsMarshal.AsSpan(ranges)))
                {
                    bytes += need;
                    requests += ranges.Count;
                }
                else
                {
                    errors++;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception)
        {
            errors++;
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
}
