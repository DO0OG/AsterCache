using System.Diagnostics;
using AsterCache.Core.Apply;
using AsterCache.Core.Prefetch;
using AsterCache.Prefetch;
using AsterCache.Windows;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Service;

/// 파일 목록 예열 (Prewarmer.WarmFiles) 의 보조: 경로 검사와 낮은 I/O 우선순위 읽기.
public static class FileWarm
{
    /// 캐시 점검 표본을 고르려고 큰 파일을 나누는 조각 크기: 앞쪽 몇 곳만이 아니라 파일 전체에서 고르게 재기 위함.
    public const long ProbePieceBytes = 64L << 20;
    private const int ChunkBytes = 1 << 20;

    /// path 를 정규화해 originRoot 아래의 파일 경로로 돌려줍니다 (링크는 풀어서 검사). 상대 경로, ".." 조각, 대체 스트림, 접두어 (\\?\), 루트 밖 (다른 드라이브 포함) 은 null.
    public static string? Allowed(string originRoot, string path, Func<string, string> real)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Contains('\0') || !Path.IsPathRooted(path) || path.AsSpan(Math.Min(2, path.Length)).Contains(':')
            || path.Split('\\', '/').Contains(".."))
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(path);
            return PlanGuards.Inside(full, [originRoot], real) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    /// 큰 파일 op 를 ProbePieceBytes 조각으로 나눈 목록 (캐시 점검 표본용).
    public static List<ReadOp> Pieces(ReadOp op)
    {
        var pieces = new List<ReadOp>();
        for (long at = 0; at < op.Length; at += ProbePieceBytes)
        {
            pieces.Add(new ReadOp(op.Path, op.Offset + at, Math.Min(ProbePieceBytes, op.Length - at)));
        }

        return pieces;
    }

    /// ops 를 FolderWarm.Read 묶음으로 읽되, 파일마다 낮은 I/O 우선순위 힌트를 붙인 버퍼드 읽기로 (게임·클라이언트의 I/O 를 밀어내지 않게). ct 는 1 MiB 마다 확인합니다.
    public static (StreamStats Stats, bool Complete) Read(IReadOnlyList<ReadOp> ops, CancellationToken ct) =>
        FolderWarm.Read(ops, ct, batch => ReadLow(batch, ct));

    private static StreamStats ReadLow(IReadOnlyList<ReadOp> batch, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var hint = new IoPriorityHint(_ => { }); // 실패해도 보통 우선순위로 계속
        byte[] buf = new byte[ChunkBytes];
        long bytes = 0;
        long requests = 0;
        long errors = 0;
        foreach (ReadOp op in batch)
        {
            if (ct.IsCancellationRequested)
            {
                break;
            }

            try
            {
                using SafeFileHandle h = File.OpenHandle(op.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                hint.SetLow(h);
                long at = op.Offset;
                long end = op.Offset + op.Length;
                while (at < end && !ct.IsCancellationRequested)
                {
                    int n = RandomAccess.Read(h, buf.AsSpan(0, (int)Math.Min(buf.Length, end - at)), at);
                    if (n <= 0)
                    {
                        break;
                    }

                    at += n;
                    bytes += n;
                    requests++;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                errors++; // 열 수 없는 파일은 건너뛰고 계속
            }
        }

        return new StreamStats(bytes, requests, errors, Math.Round(watch.Elapsed.TotalSeconds, 4), ct.IsCancellationRequested, batch.Sum(o => o.Length));
    }
}
