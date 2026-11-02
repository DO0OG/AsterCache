using System.Buffers.Binary;
using System.Text;
using AsterCache.Core.Prefetch;
using Xunit;

namespace AsterCache.Tests.Core;

/// 시험용 프리페치 (SCCA) 바이트 조립기: libscca 문서의 v30 (변형 1·2)·v31 배치를 그대로 씁니다.
internal static class PfFixture
{
    public const string Volume = @"\VOLUME{01d8a1b2c3d4e5f6-1a2b3c4d}";
    public const uint Serial = 0x1A2B3C4D;

    public static byte[] Build(string exe, IReadOnlyList<(string Path, uint Flags, uint Blocks)> files, IReadOnlyList<(string Device, uint Serial)>? volumes = null,
        uint version = 30, int variant = 1, uint runCount = 7, long lastRunFileTime = 133_700_000_000_000_000L, uint hash = 0xAABBCCDD)
    {
        volumes ??= [(Volume, Serial)];
        int infoSize = version == 30 && variant == 1 ? 220 : 212;
        int metricsAt = 84 + infoSize;
        int chainsAt = metricsAt + (32 * files.Count);
        int namesAt = chainsAt + (8 * files.Count);
        var names = new MemoryStream();
        var nameAt = new List<int>();
        foreach ((string path, _, _) in files)
        {
            nameAt.Add((int)names.Length);
            names.Write(Encoding.Unicode.GetBytes(path + "\0"));
        }

        int volAt = namesAt + (int)names.Length;
        var paths = new MemoryStream();
        var pathAt = new List<int>();
        foreach ((string device, _) in volumes)
        {
            pathAt.Add((96 * volumes.Count) + (int)paths.Length);
            paths.Write(Encoding.Unicode.GetBytes(device + "\0"));
        }

        int total = volAt + (96 * volumes.Count) + (int)paths.Length;
        byte[] d = new byte[total];
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0), version);
        Encoding.ASCII.GetBytes("SCCA").CopyTo(d, 4);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(12), (uint)total);
        Encoding.Unicode.GetBytes(exe).CopyTo(d, 16);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(76), hash);
        void U32(int at, long v) => BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), (uint)v);
        U32(84, metricsAt);
        U32(88, files.Count);
        U32(92, chainsAt);
        U32(96, files.Count);
        U32(100, namesAt);
        U32(104, names.Length);
        U32(108, volAt);
        U32(112, volumes.Count);
        U32(116, total - volAt);
        BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(84 + 44), lastRunFileTime);
        U32(84 + (variant == 1 && version == 30 ? 124 : 116), runCount);
        for (int i = 0; i < files.Count; i++)
        {
            int m = metricsAt + (32 * i);
            U32(m, i);
            U32(m + 4, 1);
            U32(m + 8, files[i].Blocks);
            U32(m + 12, nameAt[i]);
            U32(m + 16, files[i].Path.Length);
            U32(m + 20, files[i].Flags);
        }

        names.ToArray().CopyTo(d, namesAt);
        for (int i = 0; i < volumes.Count; i++)
        {
            int v = volAt + (96 * i);
            U32(v, pathAt[i]);
            U32(v + 4, volumes[i].Device.Length);
            BinaryPrimitives.WriteInt64LittleEndian(d.AsSpan(v + 8), 133_000_000_000_000_000L);
            U32(v + 16, volumes[i].Serial);
        }

        paths.ToArray().CopyTo(d, volAt + (96 * volumes.Count));
        return d;
    }
}

public sealed class PfParserTests
{
    private static readonly (string, uint, uint)[] Three =
    [
        (Volume(@"\GAMES\BENCH\BENCH.EXE"), 0x200, 4),
        (Volume(@"\WINDOWS\SYSTEM32\KERNEL32.DLL"), 0x02, 0),
        (Volume(@"\GAMES\BENCH\DATA\A.PAK"), 0x01, 20),
    ];

    private static string Volume(string rest) => PfFixture.Volume + rest;

    [Theory]
    [InlineData(30u, 1)]
    [InlineData(30u, 2)]
    [InlineData(31u, 2)]
    public void ParsesFilesVolumesRunCountAndLastRun(uint version, int variant)
    {
        byte[] pf = PfFixture.Build("BENCH.EXE", Three, version: version, variant: variant);

        PfInfo? info = PfParser.Parse(pf);

        Assert.NotNull(info);
        Assert.Equal(version, info.Version);
        Assert.Equal("BENCH.EXE", info.ExeName);
        Assert.Equal(0xAABBCCDDu, info.Hash);
        Assert.Equal(7u, info.RunCount);
        Assert.Equal(new DateTimeOffset(DateTime.FromFileTimeUtc(133_700_000_000_000_000L)), info.LastRun);
        Assert.Equal(3, info.Files.Count);
        Assert.Equal(new PfFile(Volume(@"\GAMES\BENCH\BENCH.EXE"), 0x200), info.Files[0]);
        Assert.Equal(new PfFile(Volume(@"\GAMES\BENCH\DATA\A.PAK"), 0x01), info.Files[2]);
        PfVolume v = Assert.Single(info.Volumes);
        Assert.Equal(PfFixture.Volume, v.DevicePath);
        Assert.Equal(PfFixture.Serial, v.Serial);
        Assert.Equal(new DateTimeOffset(DateTime.FromFileTimeUtc(133_000_000_000_000_000L)), v.Created);
    }

    [Fact]
    public void EmptyFileListAndSeveralVolumesAreFine()
    {
        PfInfo? info = PfParser.Parse(PfFixture.Build("X.EXE", [], [(PfFixture.Volume, 1), (@"\VOLUME{02-0000BEEF}", 0xBEEF)]));

        Assert.NotNull(info);
        Assert.Empty(info.Files);
        Assert.Equal([1u, 0xBEEFu], info.Volumes.Select(v => v.Serial));
    }

    [Fact]
    public void ZeroTimesAreNull()
    {
        PfInfo? info = PfParser.Parse(PfFixture.Build("X.EXE", Three, lastRunFileTime: 0));

        Assert.Null(info!.LastRun);
    }

    [Fact]
    public void WrongVersionSignatureOrSizeIsRejected()
    {
        byte[] ok = PfFixture.Build("X.EXE", Three);
        byte[] v26 = (byte[])ok.Clone();
        v26[0] = 26;
        byte[] sig = (byte[])ok.Clone();
        sig[4] = (byte)'X';
        byte[] size = (byte[])ok.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(size.AsSpan(12), (uint)ok.Length + 1); // 파일 크기가 실제보다 큼 = 잘림

        Assert.Null(PfParser.Parse(v26));
        Assert.Null(PfParser.Parse(sig));
        Assert.Null(PfParser.Parse(size));
        Assert.Null(PfParser.Parse([]));
        Assert.Null(PfParser.Parse(ok.AsSpan(0, 100)));
    }

    [Theory]
    [InlineData(84, 0xFFFFFFF0u)] // 지표 배열 오프셋이 파일 밖
    [InlineData(88, 0xFFFFFFFFu)] // 지표 개수 터무니없음
    [InlineData(100, 0x7FFFFFFFu)] // 파일 이름 영역 오프셋
    [InlineData(104, 0xFFFFFFFFu)] // 파일 이름 영역 크기
    [InlineData(108, 0xFFFFFFFFu)] // 볼륨 정보 오프셋
    [InlineData(112, 5000u)] // 볼륨 개수가 많음
    public void CorruptHeaderFieldsGiveNullNotAnException(int at, uint value)
    {
        byte[] pf = PfFixture.Build("X.EXE", Three);
        BinaryPrimitives.WriteUInt32LittleEndian(pf.AsSpan(at), value);

        Assert.Null(PfParser.Parse(pf));
    }

    [Fact]
    public void NameOutsideTheNameAreaIsRejected()
    {
        byte[] pf = PfFixture.Build("X.EXE", Three);
        int metricsAt = (int)BinaryPrimitives.ReadUInt32LittleEndian(pf.AsSpan(84));
        BinaryPrimitives.WriteUInt32LittleEndian(pf.AsSpan(metricsAt + 16), 30_000); // 글자 수가 영역보다 큼

        Assert.Null(PfParser.Parse(pf));
    }

    [Fact]
    public void RandomCorruptionNeverThrows()
    {
        byte[] ok = PfFixture.Build("X.EXE", Three);
        var rnd = new Random(1234);
        for (int i = 0; i < 2000; i++)
        {
            byte[] bad = (byte[])ok.Clone();
            for (int k = 0; k < 1 + (i % 6); k++)
            {
                bad[rnd.Next(bad.Length)] = (byte)rnd.Next(256);
            }

            _ = PfParser.Parse(bad.AsSpan(0, rnd.Next(bad.Length + 1)));
            _ = PfParser.Parse(bad);
        }
    }

    // ---- 실제 기록 (Windows 11 메모장, 압축을 푼 SCCA v31; 이름·SID·볼륨 번호는 같은 길이로 바꿔 익명화) ----

    private static byte[] RealFixture() => PlanFixtures.Bytes("prefetch/NOTEPAD.EXE-D1583A9.scca");

    [Fact]
    public void ARealWindows11RecordParsesToItsKnownValues()
    {
        PfInfo? info = PfParser.Parse(RealFixture());

        Assert.NotNull(info);
        Assert.Equal(31u, info.Version);
        Assert.Equal("NOTEPAD.EXE", info.ExeName);
        Assert.Equal(0x0D1583A9u, info.Hash);
        Assert.Equal(76u, info.RunCount);
        Assert.NotNull(info.LastRun);
        Assert.Equal(270, info.Files.Count);
        Assert.Equal(@"\VOLUME{01d705d430adfc9e-12345678}\WINDOWS\SYSTEM32\NTDLL.DLL", info.Files[0].Path);
        Assert.Equal(0x100u, info.Files[0].Flags);
        Assert.Equal(2, info.Volumes.Count);
        Assert.Equal(@"\VOLUME{01d705d430adfc9e-12345678}", info.Volumes[0].DevicePath);
        Assert.Equal(0x12345678u, info.Volumes[0].Serial);
        Assert.All(info.Files, f => Assert.StartsWith(@"\VOLUME{", f.Path, StringComparison.Ordinal));
    }

    [Fact]
    public void TheRealRecordHoldsNoUserNameHostNameOrAccountId()
    {
        byte[] raw = RealFixture();
        foreach (string secret in new[] { Environment.UserName, Environment.MachineName }.Where(x => x.Length >= 3))
        {
            Assert.False(raw.AsSpan().IndexOf(Encoding.Unicode.GetBytes(secret.ToUpperInvariant())) >= 0, "UTF-16 대문자");
            Assert.False(raw.AsSpan().IndexOf(Encoding.Unicode.GetBytes(secret.ToLowerInvariant())) >= 0, "UTF-16 소문자");
            Assert.False(raw.AsSpan().IndexOf(Encoding.ASCII.GetBytes(secret)) >= 0, "ASCII");
        }

        string text = Encoding.Unicode.GetString(raw, 0, raw.Length & ~1).ToUpperInvariant();
        Assert.DoesNotContain(@"\USERS\" + Environment.UserName.ToUpperInvariant() + @"\", text, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"S-1-5-21-(?!1111111111-222222222-3333333333)\d", text); // 계정 SID 는 바꾼 값만
    }
}
