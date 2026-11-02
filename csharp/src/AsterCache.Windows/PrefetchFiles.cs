using AsterCache.Core.Json;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Trace;
using AsterCache.Windows.Native;

namespace AsterCache.Windows;

/// Windows 프리페치 기록 (C:\Windows\Prefetch\*.pf) 읽기: 압축 해제, 게임 exe 와 맞는 기록 찾기, boot 트레이스 변환.
/// C:\Windows\Prefetch 읽기는 관리자가 필요해서 서비스 (SYSTEM) 에서만 쓰입니다. 읽기 전용이고 게임 프로세스는 건드리지 않습니다.
public static class PrefetchFiles
{
    /// boot 트레이스 파일의 "source" 값: 이 표시가 있으면 프리페치 기록으로 만든 것 (가져온 프로필의 boot 와 구분).
    public const string BootSource = "prefetch";

    public const long MaxFileBytes = 16L << 20;
    public const uint MaxUncompressedBytes = 32u << 20;

    /// 파일 이름 앞부분 (실행 파일 이름, 대문자) 은 29 자까지.
    private const int NamePrefixChars = 29;
    private const ushort CompressionFormatXpressHuff = 4; // COMPRESSION_FORMAT_XPRESS_HUFF

    // 지표 플래그 0x01 = "do not prefetch" (libscca file metrics flags): 프리페처가 미리 읽지 않는 파일이라 boot 트레이스에서 뺍니다.
    private const uint DoNotPrefetch = 0x01;

    /// MAM 압축 (머리 "MAM\x04" + 해제 크기, XPRESS Huffman) 을 풉니다. 압축 안 된 SCCA 는 그대로 돌려주고, 그 밖이나 틀린 입력은 null.
    public static unsafe byte[]? Decompress(ReadOnlySpan<byte> file)
    {
        if (file.Length >= 8 && file[0] == 'M' && file[1] == 'A' && file[2] == 'M' && file[3] == 4)
        {
            uint size = BitConverter.ToUInt32(file[4..8]);
            if (size is 0 or > MaxUncompressedBytes || file.Length == 8 || NativeMethods.RtlGetCompressionWorkSpaceSize(CompressionFormatXpressHuff, out _, out uint workSize) != 0)
            {
                return null;
            }

            var result = new byte[size];
            var work = new byte[Math.Max(workSize, 1)];
            fixed (byte* src = file[8..])
            fixed (byte* dst = result)
            fixed (byte* ws = work)
            {
                return NativeMethods.RtlDecompressBufferEx(CompressionFormatXpressHuff, dst, size, src, (uint)(file.Length - 8), out uint final, ws) == 0 && final == size ? result : null;
            }
        }

        return file.Length >= 8 && file[4] == 'S' && file[5] == 'C' && file[6] == 'C' && file[7] == 'A' ? file.ToArray() : null;
    }

    /// .pf 한 파일을 읽어 해석합니다. 못 읽거나 (권한·없음) 형식이 틀리면 null.
    public static PfInfo? Read(string path)
    {
        try
        {
            if (new FileInfo(path).Length is > MaxFileBytes or 0)
            {
                return null;
            }

            return Decompress(File.ReadAllBytes(path)) is { } raw ? PfParser.Parse(raw) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// 폴더의 .pf 중 이름이 <EXE 이름 대문자 앞 29 자>-<해시 8 자리>.pf 인 것 (폴더를 못 열면 빈 목록).
    public static IReadOnlyList<string> Candidates(string prefetchDir, string exeName)
    {
        string upper = exeName.ToUpperInvariant();
        string prefix = (upper.Length > NamePrefixChars ? upper[..NamePrefixChars] : upper) + "-";
        try
        {
            return [.. Directory.EnumerateFiles(prefetchDir, "*.pf").Where(f => IsCandidate(Path.GetFileName(f), prefix)).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsCandidate(string name, string prefix) =>
        name.Length == prefix.Length + 8 + 3 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.EndsWith(".pf", StringComparison.OrdinalIgnoreCase)
        && name.AsSpan(prefix.Length, 8).ToString().All(char.IsAsciiHexDigit);

    /// NT 볼륨 경로 (\VOLUME{...}\DIR\X) 를 드라이브 경로 (D:\DIR\X) 로. 볼륨의 일련번호로 드라이브 문자를 찾고, 모르면 (또는 같은 일련번호의 드라이브가 둘이면) null.
    public static string? DosPath(string ntPath, IReadOnlyList<PfVolume> volumes, IReadOnlyDictionary<uint, string> letters)
    {
        PfVolume? best = null;
        foreach (PfVolume v in volumes)
        {
            int n = v.DevicePath.Length;
            if (n > 0 && ntPath.StartsWith(v.DevicePath, StringComparison.OrdinalIgnoreCase) && (ntPath.Length == n || ntPath[n] == '\\') && (best is null || n > best.DevicePath.Length))
            {
                best = v;
            }
        }

        return best is not null && letters.TryGetValue(best.Serial, out string? letter) ? letter.TrimEnd('\\') + ntPath[best.DevicePath.Length..] : null;
    }

    /// 파일 목록에 originRoot 아래의 exeName (게임 exe) 이 들어 있는 기록인가.
    public static bool Matches(PfInfo info, string exeName, string originRoot, IReadOnlyDictionary<uint, string> letters) =>
        info.Files.Any(f => string.Equals(Path.GetFileName(f.Path), exeName, StringComparison.OrdinalIgnoreCase)
            && DosPath(f.Path, info.Volumes, letters) is { } dos && TraceLoader.IsRelativeTo(dos, originRoot));

    /// 기록의 파일 목록 (프리페처가 읽은 순서) 을 boot 트레이스로: originRoot 안의 파일만, 플래그 0x01 (미리 읽지 않음) 제외,
    /// 길이 = 파일 크기 전체 (지표 항목에 믿을 만한 읽은 길이가 없음, PfFile 주석 참고), t_ms 0. 없는 파일·같은 파일 두 번째는 버립니다.
    public static List<TraceEvent> ToBootTrace(PfInfo info, string originRoot, IReadOnlyDictionary<uint, string> letters, Func<string, long?>? fileSize = null)
    {
        fileSize ??= p => File.Exists(p) ? new FileInfo(p).Length : null;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<TraceEvent>();
        foreach (PfFile f in info.Files)
        {
            if ((f.Flags & DoNotPrefetch) != 0 || DosPath(f.Path, info.Volumes, letters) is not { } dos || !TraceLoader.IsRelativeTo(dos, originRoot)
                || fileSize(dos) is not { } size || size <= 0 || !seen.Add(dos))
            {
                continue;
            }

            rows.Add(new TraceEvent(dos, 0, size, TMs: 0));
        }

        return rows;
    }

    /// 게임 (exe 이름들, 설치 루트) 의 boot 트레이스: 가장 최근에 실행한 맞는 기록으로 변환합니다. 기록이 없거나 읽을 수 없으면 null.
    public static List<TraceEvent>? BootTrace(IReadOnlyCollection<string> exeNames, string originRoot, string? prefetchDir = null,
        IReadOnlyDictionary<uint, string>? letters = null, Func<string, long?>? fileSize = null)
    {
        prefetchDir ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Prefetch");
        PfInfo? best = null;
        foreach (string exe in exeNames)
        {
            foreach (string file in Candidates(prefetchDir, exe))
            {
                letters ??= DriveSerials();
                if (Read(file) is { } info && Matches(info, exe, originRoot, letters) && (best is null || (info.LastRun ?? DateTimeOffset.MinValue) > (best.LastRun ?? DateTimeOffset.MinValue)))
                {
                    best = info;
                }
            }
        }

        return best is null ? null : ToBootTrace(best, originRoot, letters!, fileSize);
    }

    /// 파일 본문: {"source": "prefetch", "ranges": [...]} (TraceLoader 가 "ranges" 를 읽고 나머지 키는 무시). CRLF, BOM 없음.
    public static byte[] BootTraceBytes(IReadOnlyList<TraceEvent> events)
    {
        var o = new JObject { { "source", BootSource }, { "ranges", events.Select(e => (object?)e.ToJson()).ToList() } };
        return PyJson.Utf8.GetBytes(PyJson.ToCrlf(PyJson.Dumps(o, ensureAscii: false) + "\n"));
    }

    /// 볼륨 일련번호 → 드라이브 문자 ("D:"): 고정·이동식 드라이브만. 같은 일련번호가 둘이면 어느 쪽인지 모르므로 뺍니다.
    public static unsafe IReadOnlyDictionary<uint, string> DriveSerials()
    {
        var map = new Dictionary<uint, string>();
        var dup = new HashSet<uint>();
        foreach (DriveInfo d in DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable))
        {
            uint serial = 0;
            if (NativeMethods.GetVolumeInformationW(d.Name, null, 0, &serial, null, null, null, 0) && !map.TryAdd(serial, d.Name[..2].ToUpperInvariant()))
            {
                dup.Add(serial);
            }
        }

        dup.ToList().ForEach(s => map.Remove(s));
        return map;
    }
}
