using System.Buffers.Binary;
using System.ComponentModel;
using System.Security.Cryptography;
using AsterCache.Windows.Native;

namespace AsterCache.Windows;

/// 이동 전후 내용 비교용 SHA-256 표본. Python mover.sample_digest.
public static class SampleDigest
{
    /// 처음·가운데·끝 span 바이트 + extra 의 모든 (offset, length) (옮긴 범위) + 크기에 대한 SHA-256 (소문자 hex).
    /// 손상된 이동이 캐시된 페이지에 가려지지 않도록 기본은 언버퍼드 읽기이고, 언버퍼드 열기가 실패하면 버퍼드로 읽습니다.
    public static string Compute(string path, long size, IEnumerable<(long Offset, long Length)>? extra = null, long span = 1 << 20, bool unbuffered = true, IoPriorityHint? hint = null)
    {
        var spans = new HashSet<(long, long)>
        {
            (0, Math.Min(span, size)),
            (Math.Max(0, (size / 2) - (span / 2)), Math.Min(span, size)),
            (Math.Max(0, size - span), Math.Min(span, size)),
        };
        foreach ((long o, long n) in extra ?? Enumerable.Empty<(long, long)>())
        {
            if (o >= 0 && o < size)
            {
                spans.Add((o, Math.Min(n, size - o)));
            }
        }

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> sizeLe = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(sizeLe, size);
        hash.AppendData(sizeLe);
        using var reader = new UnbufferedReader(Win32.ShareAll, hint);
        foreach ((long off, long n) in spans.OrderBy(s => s.Item1).ThenBy(s => s.Item2))
        {
            if (n <= 0)
            {
                continue;
            }

            byte[] data;
            try
            {
                if (!unbuffered)
                {
                    throw new IOException("buffered");
                }

                data = reader.Read(path, off, n);
            }
            catch (Exception e) when (e is IOException or Win32Exception or UnauthorizedAccessException)
            {
                data = ReadBuffered(path, off, n, hint);
            }

            hash.AppendData(data);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static byte[] ReadBuffered(string path, long offset, long length, IoPriorityHint? hint)
    {
        using Microsoft.Win32.SafeHandles.SafeFileHandle h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        hint?.SetLow(h);
        var buf = new byte[length];
        int total = 0;
        while (total < length)
        {
            int got = RandomAccess.Read(h, buf.AsSpan(total), offset + total);
            if (got == 0)
            {
                break;
            }

            total += got;
        }

        return total == length ? buf : buf[..total];
    }
}
