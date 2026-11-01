using System.Buffers.Binary;
using System.Security.Cryptography;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

/// tests/test_relayout.py 의 sample_digest 시험 포팅.
public sealed class SampleDigestTests
{
    [Fact]
    public void UnbufferedMatchesBufferedAndSeesTailChange()
    {
        using var tmp = new TempDir();
        byte[] data = tmp.WriteRandom("f.bin", (3 * (1 << 20)) + 12345);
        string p = tmp.Combine("f.bin");

        string a = SampleDigest.Compute(p, data.Length, unbuffered: true);

        Assert.Equal(a, SampleDigest.Compute(p, data.Length, unbuffered: false));
        Assert.Equal(64, a.Length);
        data[^1] ^= 1; // 끝 표본의 비트 하나
        File.WriteAllBytes(p, data);
        Assert.NotEqual(a, SampleDigest.Compute(p, data.Length, unbuffered: true));
    }

    [Fact]
    public void FollowsTheDocumentedRecipe()
    {
        using var tmp = new TempDir();
        byte[] data = tmp.WriteRandom("f.bin", 3 * (1 << 20));
        const int span = 1 << 20;
        // 표본: 처음, 가운데, 끝 (크기 3 MiB 에서 겹치지 않는 세 조각) + 크기 (little-endian int64).
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] size = new byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(size, data.Length);
        h.AppendData(size);
        h.AppendData(data.AsSpan(0, span));
        h.AppendData(data.AsSpan((data.Length / 2) - (span / 2), span));
        h.AppendData(data.AsSpan(data.Length - span, span));

        Assert.Equal(Convert.ToHexStringLower(h.GetHashAndReset()), SampleDigest.Compute(tmp.Combine("f.bin"), data.Length));
    }

    [Fact]
    public void MovedRangesAreHashed()
    {
        using var tmp = new TempDir();
        byte[] data = tmp.WriteRandom("f.bin", 8 << 20);
        string p = tmp.Combine("f.bin");
        int hole = (2 * (1 << 20)) + (1 << 19); // 처음·가운데·끝 1 MiB 표본 밖
        (long, long)[] extra = { (hole - 100, 200) };

        string plain = SampleDigest.Compute(p, data.Length);
        string ranged = SampleDigest.Compute(p, data.Length, extra);
        data[hole] ^= 1;
        File.WriteAllBytes(p, data);

        Assert.Equal(plain, SampleDigest.Compute(p, data.Length)); // 옮긴 범위 없이는 안 보임
        Assert.NotEqual(ranged, SampleDigest.Compute(p, data.Length, extra));
    }

    [Fact]
    public void UnbufferedReaderReturnsExactlyTheRequestedRange()
    {
        using var tmp = new TempDir();
        byte[] data = tmp.WriteRandom("f.bin", 100_000);
        using var r = new UnbufferedReader();

        Assert.Equal(data.AsSpan(5000, 7000).ToArray(), r.Read(tmp.Combine("f.bin"), 5000, 7000));
        Assert.Equal(data.AsSpan(99_000).ToArray(), r.Read(tmp.Combine("f.bin"), 99_000, 5000)); // 파일 끝을 넘으면 짧게
        Assert.Throws<System.ComponentModel.Win32Exception>(() => r.Read(tmp.Combine("missing.bin"), 0, 10));
    }
}
