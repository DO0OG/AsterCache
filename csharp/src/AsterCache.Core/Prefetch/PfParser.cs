using System.Buffers.Binary;
using System.Text;

namespace AsterCache.Core.Prefetch;

/// 프리페치 기록이 가리킨 파일 하나: NT 경로 (\VOLUME{...}\...), 플래그.
/// 지표 항목의 오프셋 8 값은 읽지 않습니다: libscca 형식 문서에서 이 칸은 블록 수가 아니라 시간 값 (평균 소요 시간, v23 이상)이고,
/// 실제 기록으로도 값 × 512 KiB 가 파일 크기를 크게 넘는 경우가 대부분이라 (측정: 약 85%) 읽을 길이로 쓸 수 없습니다.
public sealed record PfFile(string Path, uint Flags);

/// 볼륨 하나: 장치 경로 (파일 경로의 앞부분과 같음), 만든 시각, 일련번호 (GetVolumeInformation 의 값과 같음).
public sealed record PfVolume(string DevicePath, DateTimeOffset? Created, uint Serial);

public sealed record PfInfo(uint Version, string ExeName, uint Hash, uint RunCount, DateTimeOffset? LastRun, IReadOnlyList<PfFile> Files, IReadOnlyList<PfVolume> Volumes);

/// 압축을 푼 Windows 프리페치 (SCCA) 바이트 해석, 버전 30 (Windows 10)·31 (Windows 11).
/// 형식 근거: libyal/libscca "Windows Prefetch File (PF) format" (File information v30 variant 1·2, v31, file metrics v23+, volume information v30).
/// 받은 바이트는 믿지 않습니다: 모든 오프셋·개수를 범위 검사하고, 틀리면 예외 없이 null.
public static class PfParser
{
    public const int MaxFiles = 100_000;
    public const int MaxVolumes = 64;
    public const int MaxNameChars = 32_767;

    /// 파일 정보 시작 (머리 84 바이트 뒤). 변형 1 은 220 바이트 (지표 배열이 304), 변형 2 와 v31 은 212 바이트 (296).
    private const int InfoAt = 84;
    private const int VariantOneMetricsAt = 304;
    private const int MetricSize = 32;
    private const int VolumeSize = 96;

    public static PfInfo? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length < InfoAt + 212 || U32(d, 4) != 0x41434353u /* "SCCA" */ || U32(d, 0) is not (30 or 31) || U32(d, 12) > d.Length)
        {
            return null;
        }

        uint version = U32(d, 0);
        string exe = Str(d.Slice(16, 60));
        uint metricsAt = U32(d, InfoAt);
        uint metricCount = U32(d, InfoAt + 4);
        uint namesAt = U32(d, InfoAt + 16);
        uint namesSize = U32(d, InfoAt + 20);
        uint volumesAt = U32(d, InfoAt + 24);
        uint volumeCount = U32(d, InfoAt + 28);
        uint runCount = U32(d, InfoAt + (version == 30 && metricsAt == VariantOneMetricsAt ? 124 : 116));
        if (metricCount > MaxFiles || volumeCount > MaxVolumes || !Fits(d, namesAt, namesSize) || !Fits(d, metricsAt, (long)metricCount * MetricSize)
            || !Fits(d, volumesAt, (long)volumeCount * VolumeSize))
        {
            return null;
        }

        var files = new List<PfFile>((int)metricCount);
        for (int i = 0; i < metricCount; i++)
        {
            ReadOnlySpan<byte> m = d.Slice((int)metricsAt + (i * MetricSize), MetricSize);
            uint nameAt = U32(m, 12);
            uint chars = U32(m, 16);
            if (chars > MaxNameChars || (long)nameAt + (2L * chars) > namesSize)
            {
                return null;
            }

            files.Add(new PfFile(Str(d.Slice((int)(namesAt + nameAt), (int)(2 * chars))), U32(m, 20)));
        }

        var volumes = new List<PfVolume>((int)volumeCount);
        for (int i = 0; i < volumeCount; i++)
        {
            ReadOnlySpan<byte> v = d.Slice((int)volumesAt + (i * VolumeSize), VolumeSize);
            uint pathAt = U32(v, 0);
            uint chars = U32(v, 4);
            if (chars > MaxNameChars || !Fits(d, (long)volumesAt + pathAt, 2L * chars))
            {
                return null;
            }

            volumes.Add(new PfVolume(Str(d.Slice((int)(volumesAt + pathAt), (int)(2 * chars))), Time(BinaryPrimitives.ReadUInt64LittleEndian(v[8..])), U32(v, 16)));
        }

        return new PfInfo(version, exe, U32(d, 76), runCount, Time(BinaryPrimitives.ReadUInt64LittleEndian(d[(InfoAt + 44)..])), files, volumes);
    }

    private static uint U32(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d[at..]);

    private static bool Fits(ReadOnlySpan<byte> d, long at, long size) => at >= 0 && size >= 0 && at + size <= d.Length;

    /// NUL 이 나오면 거기까지 (고정 길이 칸 안의 UTF-16 문자열).
    private static string Str(ReadOnlySpan<byte> b)
    {
        string s = Encoding.Unicode.GetString(b);
        int nul = s.IndexOf('\0', StringComparison.Ordinal);
        return nul < 0 ? s : s[..nul];
    }

    private const ulong MaxFileTime = 2650467743999999999UL; // DateTime.MaxValue 의 FILETIME

    private static DateTimeOffset? Time(ulong fileTime) => fileTime is > 0 and <= MaxFileTime ? new DateTimeOffset(DateTime.FromFileTimeUtc((long)fileTime)) : null;
}
