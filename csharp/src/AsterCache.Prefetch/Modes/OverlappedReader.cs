using AsterCache.Core.Prefetch;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Prefetch.Modes;

/// 순서 보장 오버랩 읽기: FILE_FLAG_OVERLAPPED 버퍼드 핸들로 조각을 항상 ops 순서대로 발행하고, 미완료가 depth 개를 넘으면
/// 가장 오래된 것부터 기다립니다. Python prefetch/reader.py _read_overlapped. RandomAccess.ReadAsync 는 호출 안에서 ReadFile 을 발행하므로 발행 순서가 곧 호출 순서입니다.
public static class OverlappedReader
{
    public delegate void PieceSink(string path, long offset, ReadOnlyMemory<byte> data);

    private readonly record struct Piece(Task<int> Task, int Buffer, string Path, long Offset);

    /// 바이트·요청(성공한 조각 수)·오류. 파일 크기로 자르고, 열기 실패는 op 당 오류 1, 조각 읽기 실패는 조각당 오류 1.
    public static (long Bytes, long Requests, long Errors) Run(IReadOnlyList<ReadOp> ops, int depth, int chunkBytes, PieceSink? sink = null) =>
        Run(ops, depth, chunkBytes, CancellationToken.None, out _, sink);

    /// ct 가 취소되면 새 조각을 더 내지 않고 (이미 낸 읽기는 끝까지 기다림) aborted 를 참으로 돌려줍니다. 제출 전마다 확인합니다.
    public static (long Bytes, long Requests, long Errors) Run(IReadOnlyList<ReadOp> ops, int depth, int chunkBytes, CancellationToken ct, out bool aborted, PieceSink? sink = null)
    {
        aborted = false;
        depth = Math.Clamp(depth, 1, 64);
        long bytes = 0;
        long requests = 0;
        long errors = 0;
        var handles = new Dictionary<string, SafeFileHandle?>();
        var buffers = new byte[depth][];
        var free = new Stack<int>();
        for (int i = 0; i < depth; i++)
        {
            buffers[i] = new byte[chunkBytes];
            free.Push(i);
        }

        var pending = new Queue<Piece>();

        void Finish()
        {
            Piece p = pending.Dequeue();
            try
            {
                int got = p.Task.GetAwaiter().GetResult();
                if (got > 0)
                {
                    bytes += got;
                    requests++;
                    sink?.Invoke(p.Path, p.Offset, buffers[p.Buffer].AsMemory(0, got));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors++;
            }

            free.Push(p.Buffer);
        }

        try
        {
            foreach (ReadOp op in ops)
            {
                if (ct.IsCancellationRequested)
                {
                    aborted = true;
                    break;
                }

                if (!handles.TryGetValue(op.Path, out SafeFileHandle? h))
                {
                    try
                    {
                        h = File.OpenHandle(op.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        h = null;
                    }

                    handles[op.Path] = h;
                }

                if (h is null)
                {
                    errors++;
                    continue;
                }

                long left = Math.Min(op.Length, RandomAccess.GetLength(h) - op.Offset);
                long pos = op.Offset;
                while (left > 0)
                {
                    int m = (int)Math.Min(left, chunkBytes);
                    if (free.Count == 0)
                    {
                        Finish();
                    }

                    if (ct.IsCancellationRequested)
                    {
                        aborted = true;
                        break;
                    }

                    int b = free.Pop();
                    try
                    {
                        pending.Enqueue(new Piece(RandomAccess.ReadAsync(h, buffers[b].AsMemory(0, m), pos).AsTask(), b, op.Path, pos));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        errors++;
                        free.Push(b);
                    }

                    pos += m;
                    left -= m;
                }

                if (aborted)
                {
                    break;
                }
            }

            while (pending.Count > 0)
            {
                Finish();
            }
        }
        finally
        {
            while (pending.Count > 0)
            {
                try
                {
                    Finish(); // 예외로 나왔어도 발행한 읽기가 끝나야 버퍼를 놓을 수 있음
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    errors++;
                }
            }

            foreach (SafeFileHandle? h in handles.Values)
            {
                h?.Dispose();
            }
        }

        return (bytes, requests, errors);
    }
}
