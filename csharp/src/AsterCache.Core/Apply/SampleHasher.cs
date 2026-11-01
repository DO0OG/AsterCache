using System.Buffers.Binary;
using System.ComponentModel;
using System.Security.Cryptography;

namespace AsterCache.Core.Apply;

/// 이동 전후 비교용 SHA-256 표본의 계산 규칙 (Python mover.sample_digest). 읽기는 주입합니다: 언버퍼드 읽기 (Windows 쪽 구현)
/// 가 실패하면 버퍼드 읽기로 다시 읽습니다. 규칙은 Windows/SampleDigest 와 같습니다.
public static class SampleHasher
{
    public const long DefaultSpan = 1 << 20;

    /// 처음·가운데·끝 span 바이트 + extra 의 모든 (offset, length) + 크기 (int64 리틀엔디안) 의 SHA-256, 소문자 hex.
    /// readUnbuffered 가 null 이면 버퍼드로만 읽습니다. read(path, offset, length) 는 length 바이트(파일 끝이면 그보다 적게)를 돌려줍니다.
    public static string Compute(string path, long size, IEnumerable<(long Offset, long Length)>? extra,
        Func<string, long, long, byte[]>? readUnbuffered, Func<string, long, long, byte[]>? readBuffered = null, long span = DefaultSpan)
    {
        readBuffered ??= ReadBuffered;
        var spans = new HashSet<(long, long)>
        {
            (0, Math.Min(span, size)),
            (Math.Max(0, (size / 2) - (span / 2)), Math.Min(span, size)),
            (Math.Max(0, size - span), Math.Min(span, size)),
        };
        foreach ((long o, long n) in extra ?? [])
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
        foreach ((long off, long n) in spans.OrderBy(s => s.Item1).ThenBy(s => s.Item2))
        {
            if (n <= 0)
            {
                continue;
            }

            byte[] data;
            try
            {
                data = readUnbuffered is null ? throw new IOException("buffered") : readUnbuffered(path, off, n);
            }
            catch (Exception e) when (e is IOException or Win32Exception or UnauthorizedAccessException)
            {
                data = readBuffered(path, off, n);
            }

            hash.AppendData(data);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static byte[] ReadBuffered(string path, long offset, long length)
    {
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        f.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[length];
        int got = f.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
        return got == buf.Length ? buf : buf[..got];
    }
}
