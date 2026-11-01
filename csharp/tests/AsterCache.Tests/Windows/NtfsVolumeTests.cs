using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AsterCache.Core;
using AsterCache.Core.Volume;
using AsterCache.Windows;
using AsterCache.Windows.Native;
using Xunit;

namespace AsterCache.Tests.Windows;

public sealed class NtfsVolumeTests
{
    private static uint Ctl(uint function, uint method) => (9u << 16) | (function << 2) | method;

    [Fact]
    public void StructSizesAndCodes()
    {
        if (nint.Size == 8)
        {
            Assert.Equal(32, Unsafe.SizeOf<MoveFileData>());
            Assert.Equal(16, Unsafe.SizeOf<Win32MemoryRangeEntry>());
        }

        Assert.Equal(96, Unsafe.SizeOf<NtfsVolumeDataBuffer>());
        Assert.Equal(40, Unsafe.SizeOf<FileBasicInfo>());
        Assert.Equal(
            (Ctl(25, 0), Ctl(27, 3), Ctl(29, 0), Ctl(28, 3)),
            (Win32.FsctlGetNtfsVolumeData, Win32.FsctlGetVolumeBitmap, Win32.FsctlMoveFile, Win32.FsctlGetRetrievalPointers));
    }

    [Fact]
    public void OpenRefusesBeforeTouchingTheVolumeWhenNotAdmin()
    {
        Assert.Throws<NotAdminException>(() => NtfsVolume.Open("D:", () => false));
        Assert.Throws<NotAdminException>(() => Elevation.RequireAdmin(() => false));
    }

    [Fact]
    public void OpenWithTheRealTokenMatchesElevation()
    {
        if (!Elevation.IsAdmin())
        {
            Assert.Throws<NotAdminException>(() => NtfsVolume.Open("D:"));
            return;
        }

        // 승격된 환경에서는 읽기 전용 핸들이므로 열어 보기만 합니다.
        using NtfsVolume v = NtfsVolume.Open("D:");
        Assert.Equal("D:", v.Letter);
        Assert.True(v.ClusterBytes > 0);
    }

    [Fact]
    public void RefreshParsesVolumeData()
    {
        var d = new NtfsVolumeDataBuffer { TotalClusters = 1000, FreeClusters = 400, BytesPerCluster = 4096, MftZoneStart = 10, MftZoneEnd = 90 };
        using var v = new FakeVolume(null, "X:") { Reply = (true, 0, MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref d, 1)).ToArray()) };
        v.Refresh();
        Assert.Equal((4096, 1000L, 400L, (10L, 90L)), (v.ClusterBytes, v.TotalClusters, v.FreeClusters, v.MftZone));
        Assert.Equal(Win32.FsctlGetNtfsVolumeData, v.LastCode);

        v.Reply = (false, 5, Array.Empty<byte>());
        Assert.Equal(5, Assert.Throws<Win32Exception>(v.Refresh).NativeErrorCode);
    }

    [Fact]
    public void BitmapIsFetchedInChunks()
    {
        const int nbits = 5000;
        byte[] src = new byte[(nbits + 7) / 8];
        foreach (int i in new[] { 3, 4, 999, 2500, 4999 })
        {
            src[i >> 3] |= (byte)(1 << (i & 7));
        }

        using var v = new FakeVolume(null, "X:")
        {
            Handler = (code, input, outLen) =>
            {
                long start = BinaryPrimitives.ReadInt64LittleEndian(input);
                int from = (int)(start / 8);
                int take = Math.Min(outLen - 16, src.Length - from);
                byte[] output = new byte[16 + take];
                BinaryPrimitives.WriteInt64LittleEndian(output, start);
                BinaryPrimitives.WriteInt64LittleEndian(output.AsSpan(8), nbits - start);
                src.AsSpan(from, take).CopyTo(output.AsSpan(16));
                return from + take < src.Length ? (false, Win32.ErrorMoreData, output) : (true, 0, output);
            },
        };

        VolumeBitmap bm = v.Bitmap(64);

        Assert.True(v.Calls > 5);
        Assert.Equal(nbits, bm.NBits);
        for (int i = 0; i < nbits; i++)
        {
            Assert.Equal(((src[i >> 3] >> (i & 7)) & 1) != 0, bm.Used(i));
        }
    }

    [Fact]
    public void MoveSendsMoveFileDataAndReportsFailureAsWin32Exception()
    {
        using var tmp = new TempDir();
        tmp.WriteRandom("f.bin", 4096);
        using var v = new FakeVolume(null, "X:") { Reply = (true, 0, Array.Empty<byte>()) };

        v.Move(tmp.Combine("f.bin"), 7, 900, 3);

        Assert.Equal(Win32.FsctlMoveFile, v.LastCode);
        MoveFileData req = MemoryMarshal.Read<MoveFileData>(v.LastInput);
        Assert.Equal((7L, 900L, 3u), (req.StartingVcn, req.StartingLcn, req.ClusterCount));
        Assert.NotEqual(0, req.FileHandle);

        v.Reply = (false, 112, Array.Empty<byte>()); // ERROR_DISK_FULL
        Assert.Equal(112, Assert.Throws<Win32Exception>(() => v.Move(tmp.Combine("f.bin"), 0, 0, 1)).NativeErrorCode);
        Assert.Throws<Win32Exception>(() => v.Move(tmp.Combine("missing.bin"), 0, 0, 1));
    }

    private sealed class FakeVolume : NtfsVolume
    {
        public FakeVolume(Microsoft.Win32.SafeHandles.SafeFileHandle? handle, string letter)
            : base(handle, letter)
        {
        }

        public (bool Ok, int Error, byte[] Output) Reply { get; set; }

        public Func<uint, byte[], int, (bool, int, byte[])>? Handler { get; set; }

        public int Calls { get; private set; }

        public uint LastCode { get; private set; }

        public byte[] LastInput { get; private set; } = Array.Empty<byte>();

        protected override (bool Ok, int Error, byte[] Output) Ioctl(uint code, ReadOnlySpan<byte> input, int outputLength)
        {
            Calls++;
            LastCode = code;
            LastInput = input.ToArray();
            return Handler is null ? Reply : Handler(code, LastInput, outputLength);
        }
    }
}
