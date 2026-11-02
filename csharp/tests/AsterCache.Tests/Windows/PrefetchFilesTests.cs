using System.Buffers.Binary;
using System.Runtime.InteropServices;
using AsterCache.Core.Json;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Trace;
using AsterCache.Tests.Core;
using AsterCache.Windows;
using Xunit;

namespace AsterCache.Tests.Windows;

public sealed class PrefetchFilesTests : IDisposable
{
    private const ushort XpressHuff = 4;
    private static readonly IReadOnlyDictionary<uint, string> Letters = new Dictionary<uint, string> { [PfFixture.Serial] = "D:", [0x11111111] = "E:" };
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // ---- 압축 (실제 ntdll 로 왕복) ----

    [DllImport("ntdll.dll")]
    private static extern int RtlGetCompressionWorkSpaceSize(ushort format, out uint compressWork, out uint fragmentWork);

    [DllImport("ntdll.dll")]
    private static extern int RtlCompressBuffer(ushort format, byte[] uncompressed, uint uncompressedSize, [Out] byte[] compressed, uint compressedSize, uint chunkSize, out uint finalSize, byte[] workSpace);

    /// 프리페처가 쓰는 것과 같은 MAM 형식 파일: "MAM\x04" + 해제 크기 + XPRESS_HUFF 압축 본문.
    private static byte[] Mam(byte[] raw)
    {
        Assert.Equal(0, RtlGetCompressionWorkSpaceSize(XpressHuff, out uint work, out _));
        byte[] packed = new byte[raw.Length + 4096];
        Assert.Equal(0, RtlCompressBuffer(XpressHuff, raw, (uint)raw.Length, packed, (uint)packed.Length, 4096, out uint final, new byte[work]));
        byte[] mam = new byte[8 + final];
        mam[0] = (byte)'M';
        mam[1] = (byte)'A';
        mam[2] = (byte)'M';
        mam[3] = 4;
        BinaryPrimitives.WriteUInt32LittleEndian(mam.AsSpan(4), (uint)raw.Length);
        packed.AsSpan(0, (int)final).CopyTo(mam.AsSpan(8));
        return mam;
    }

    private static readonly (string, uint, uint)[] Files =
    [
        (PfFixture.Volume + @"\GAMES\BENCH\BENCH.EXE", 0x200, 2),
        (PfFixture.Volume + @"\WINDOWS\SYSTEM32\KERNEL32.DLL", 0x02, 1),
        (PfFixture.Volume + @"\GAMES\BENCH\DATA\A.PAK", 0, 3),
        (PfFixture.Volume + @"\GAMES\BENCH\DATA\B.PAK", 0x01, 3), // 미리 읽지 않음
        (PfFixture.Volume + @"\GAMES\BENCH\DATA\C.PAK", 0, 0), // 블록 수 없음
        (PfFixture.Volume + @"\GAMES\BENCH\DATA\MISSING.PAK", 0, 1),
        (PfFixture.Volume + @"\GAMES\BENCH\DATA\A.PAK", 0, 1), // 같은 파일 두 번째
        (@"\VOLUME{01-11111111}\OTHER\X.BIN", 0, 1), // 다른 볼륨
        (@"\DEVICE\HARDDISKVOLUME9\GAMES\BENCH\Z.BIN", 0, 1), // 볼륨 목록에 없는 장치
        (PfFixture.Volume + @"\GAMES\BENCHMARK\Q.BIN", 0, 1), // 이름만 비슷한 폴더
    ];

    [Fact]
    public void CompressedFileRoundTripsThroughTheRealNtdll()
    {
        byte[] raw = PfFixture.Build("BENCH.EXE", Files);

        byte[]? back = PrefetchFiles.Decompress(Mam(raw));

        Assert.Equal(raw, back);
        Assert.NotNull(PfParser.Parse(back));
    }

    [Fact]
    public void LargeRepetitiveInputRoundTrips()
    {
        byte[] raw = new byte[300_000];
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] = (byte)(i % 251 < 200 ? i % 7 : i);
        }

        Assert.Equal(raw, PrefetchFiles.Decompress(Mam(raw)));
    }

    [Fact]
    public void UncompressedSccaPassesThroughAndEverythingElseIsNull()
    {
        byte[] raw = PfFixture.Build("BENCH.EXE", Files);
        byte[] mam = Mam(raw);
        byte[] wrongFormat = (byte[])mam.Clone();
        wrongFormat[3] = 3;
        byte[] sizeLies = (byte[])mam.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(sizeLies.AsSpan(4), (uint)raw.Length + 100);
        byte[] huge = (byte[])mam.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(huge.AsSpan(4), uint.MaxValue);

        Assert.Equal(raw, PrefetchFiles.Decompress(raw));
        Assert.Null(PrefetchFiles.Decompress(wrongFormat));
        Assert.Null(PrefetchFiles.Decompress(sizeLies));
        Assert.Null(PrefetchFiles.Decompress(huge));
        Assert.Null(PrefetchFiles.Decompress(mam.AsSpan(0, 8)));
        Assert.Null(PrefetchFiles.Decompress(mam.AsSpan(0, 20)));
        Assert.Null(PrefetchFiles.Decompress([]));
        Assert.Null(PrefetchFiles.Decompress(new byte[64]));
    }

    [Fact]
    public void CorruptCompressedBodyNeverThrows()
    {
        byte[] mam = Mam(PfFixture.Build("BENCH.EXE", Files));
        var rnd = new Random(77);
        for (int i = 0; i < 300; i++)
        {
            byte[] bad = (byte[])mam.Clone();
            for (int k = 0; k < 3; k++)
            {
                bad[8 + rnd.Next(bad.Length - 8)] = (byte)rnd.Next(256);
            }

            _ = PrefetchFiles.Decompress(bad);
        }
    }

    // ---- 이름·경로·매칭 ----

    [Fact]
    public void CandidatesAreNamedUpperExeDashEightHexDigits()
    {
        foreach (string name in new[] { "BENCH.EXE-0A1B2C3D.pf", "BENCH.EXE-FFFFFFFF.pf", "BENCH.EXE-0A1B2C3.pf", "BENCH.EXE-0A1B2C3G.pf", "BENCH.EXE-0A1B2C3D.txt", "OTHERBENCH.EXE-0A1B2C3D.pf", "BENCH.EXE.pf" })
        {
            File.WriteAllBytes(_dir.Combine(name), [1]);
        }

        IReadOnlyList<string> found = PrefetchFiles.Candidates(_dir.Path, "bench.exe");

        Assert.Equal(["BENCH.EXE-0A1B2C3D.pf", "BENCH.EXE-FFFFFFFF.pf"], found.Select(Path.GetFileName));
        Assert.Empty(PrefetchFiles.Candidates(_dir.Combine("nope"), "bench.exe"));
    }

    [Fact]
    public void LongExeNamesAreCutAtTwentyNineCharacters()
    {
        string exe = "A_VERY_LONG_GAME_EXECUTABLE_NAME_X.EXE"; // 38 자
        string file = exe.ToUpperInvariant()[..29] + "-00000001.pf";
        File.WriteAllBytes(_dir.Combine(file), [1]);

        Assert.Equal([file], PrefetchFiles.Candidates(_dir.Path, exe).Select(Path.GetFileName));
    }

    [Fact]
    public void NtPathsMapToDriveLettersBySerialNumber()
    {
        PfInfo info = PfParser.Parse(PfFixture.Build("BENCH.EXE", Files, [(PfFixture.Volume, PfFixture.Serial), (@"\DEVICE\HARDDISKVOLUME3", 0x11111111)]))!;

        Assert.Equal(@"D:\GAMES\BENCH\BENCH.EXE", PrefetchFiles.DosPath(info.Files[0].Path, info.Volumes, Letters));
        Assert.Equal(@"D:\games\x", PrefetchFiles.DosPath(PfFixture.Volume.ToLowerInvariant() + @"\games\x", info.Volumes, Letters)); // 장치 경로 대소문자 무시
        Assert.Equal(@"E:\A.BIN", PrefetchFiles.DosPath(@"\DEVICE\HARDDISKVOLUME3\A.BIN", info.Volumes, Letters));
        Assert.Null(PrefetchFiles.DosPath(@"\DEVICE\HARDDISKVOLUME30\A.BIN", info.Volumes, Letters)); // 접두만 같은 다른 장치
        Assert.Null(PrefetchFiles.DosPath(@"\DEVICE\HARDDISKVOLUME9\A.BIN", info.Volumes, Letters));
        Assert.Null(PrefetchFiles.DosPath(info.Files[0].Path, info.Volumes, new Dictionary<uint, string>())); // 일련번호에 맞는 드라이브 없음
    }

    [Fact]
    public void MatchNeedsTheExeUnderTheOriginRoot()
    {
        PfInfo info = PfParser.Parse(PfFixture.Build("BENCH.EXE", Files))!;

        Assert.True(PrefetchFiles.Matches(info, "bench.exe", @"D:\Games\Bench", Letters));
        Assert.False(PrefetchFiles.Matches(info, "bench.exe", @"D:\Games\Other", Letters)); // 다른 설치 폴더의 같은 이름 exe
        Assert.False(PrefetchFiles.Matches(info, "other.exe", @"D:\Games\Bench", Letters));
        Assert.False(PrefetchFiles.Matches(info, "bench.exe", @"E:\Games\Bench", Letters));
    }

    // ---- boot 트레이스 변환 ----

    private string Game => @"D:\GAMES\BENCH";

    private static Func<string, long?> Sizes(Dictionary<string, long> sizes) => p => sizes.TryGetValue(p, out long n) ? n : null;

    [Fact]
    public void BootTraceKeepsOnlyRootFilesWithoutTheNoPrefetchFlagAndUsesTheWholeFileLength()
    {
        PfInfo info = PfParser.Parse(PfFixture.Build("BENCH.EXE", Files))!;
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            [@"D:\GAMES\BENCH\BENCH.EXE"] = 10L << 20, // 지표 값과 무관하게 파일 크기 전체
            [@"D:\GAMES\BENCH\DATA\A.PAK"] = 100_000,
            [@"D:\GAMES\BENCH\DATA\B.PAK"] = 5_000_000, // 플래그 0x01
            [@"D:\GAMES\BENCH\DATA\C.PAK"] = 777_777,
            [@"D:\GAMES\BENCH\Z.BIN"] = 10, // 장치를 몰라서 못 바꿈
            [@"D:\GAMES\BENCHMARK\Q.BIN"] = 10, // 루트 밖
            [@"E:\OTHER\X.BIN"] = 10, // 루트 밖
            [@"C:\WINDOWS\SYSTEM32\KERNEL32.DLL"] = 10,
        };

        List<TraceEvent> rows = PrefetchFiles.ToBootTrace(info, Game, Letters, Sizes(sizes));

        Assert.Equal(
            [
                (@"D:\GAMES\BENCH\BENCH.EXE", 0L, 10L << 20, 0L),
                (@"D:\GAMES\BENCH\DATA\A.PAK", 0L, 100_000L, 0L),
                (@"D:\GAMES\BENCH\DATA\C.PAK", 0L, 777_777L, 0L),
            ],
            rows.Select(r => (r.Path, r.Offset, r.Length, r.TMs!.Value))); // 기록의 순서 그대로, MISSING.PAK 은 파일이 없어 빠지고 A.PAK 두 번째도 빠짐
    }

    [Fact]
    public void BootTraceLengthIsTheWholeFileWhateverTheMetricValueIs()
    {
        PfInfo info = PfParser.Parse(PfFixture.Build("BENCH.EXE", [(PfFixture.Volume + @"\GAMES\BENCH\A.BIN", 0, 3)]))!;

        TraceEvent e = Assert.Single(PrefetchFiles.ToBootTrace(info, Game, Letters, _ => 100L << 20));

        Assert.Equal(100L << 20, e.Length);
    }

    [Fact]
    public void BootTraceFileLoadsThroughTheNormalTraceLoaderAndKeepsTheSourceMark()
    {
        byte[] data = _dir.WriteRandom("a.bin", 5000);
        string file = _dir.Combine("a.bin");
        var rows = new List<TraceEvent> { new(file, 0, 4000, 8.0, 0), new(file, 0, 999_999, 8.0, 0) };
        string boot = _dir.Combine("g.boot.trace.json");

        File.WriteAllBytes(boot, PrefetchFiles.BootTraceBytes(rows));

        List<TraceEvent> loaded = TraceLoader.LoadRanges(boot, _dir.Path);
        Assert.Equal([4000L, 5000L], loaded.Select(e => e.Length)); // 파일 크기로 잘림
        Assert.Equal("prefetch", (JsonRead.ReadFile(boot) as JObject)!["source"]);
        Assert.Equal(5000, data.Length);
    }

    [Fact]
    public void BootTraceFindsTheMostRecentMatchingRecordInTheFolder()
    {
        string root = _dir.Combine("game");
        Directory.CreateDirectory(root);
        string exe = Path.Combine(root, "bench.exe");
        string data = Path.Combine(root, "a.pak");
        File.WriteAllBytes(exe, new byte[3000]);
        File.WriteAllBytes(data, new byte[4000]);
        string rest = root[2..].ToUpperInvariant();
        string volume = @"\VOLUME{01d8a1b2c3d4e5f6-1a2b3c4d}";
        var map = new Dictionary<uint, string> { [0x1A2B3C4D] = root[..2].ToUpperInvariant() };
        byte[] Pf(long lastRun, string dataName) => Mam(PfFixture.Build("BENCH.EXE", [(volume + rest + @"\BENCH.EXE", 0x200, 1), (volume + rest + @"\" + dataName, 0, 1)], lastRunFileTime: lastRun));
        string pfs = _dir.Combine("Prefetch");
        Directory.CreateDirectory(pfs);
        File.WriteAllBytes(Path.Combine(pfs, "BENCH.EXE-00000001.pf"), Pf(133_000_000_000_000_000L, "OLD.PAK")); // 더 오래된 기록 (없는 파일)
        File.WriteAllBytes(Path.Combine(pfs, "BENCH.EXE-00000002.pf"), Pf(133_700_000_000_000_000L, "A.PAK"));
        File.WriteAllBytes(Path.Combine(pfs, "BENCH.EXE-00000003.pf"), [1, 2, 3]); // 깨진 기록은 건너뜀

        List<TraceEvent>? rows = PrefetchFiles.BootTrace(["bench.exe"], root, pfs, map);

        Assert.NotNull(rows);
        Assert.Equal([exe.ToUpperInvariant(), data.ToUpperInvariant()], rows.Select(r => r.Path.ToUpperInvariant()));
        Assert.Equal([3000L, 4000L], rows.Select(r => r.Length));
        Assert.Null(PrefetchFiles.BootTrace(["bench.exe"], _dir.Combine("elsewhere"), pfs, map)); // 설치 폴더가 다르면 맞는 기록 없음
        Assert.Null(PrefetchFiles.BootTrace(["nothing.exe"], root, pfs, map));
        Assert.Null(PrefetchFiles.BootTrace(["bench.exe"], root, _dir.Combine("no-such-dir"), map));
    }

    [Fact]
    public void DriveSerialsFindsTheSystemDrive()
    {
        IReadOnlyDictionary<uint, string> map = PrefetchFiles.DriveSerials();

        Assert.All(map.Values, v => Assert.Matches("^[A-Z]:$", v));
    }
}
