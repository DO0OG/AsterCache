using System.Diagnostics;
using System.Runtime.InteropServices;
using AsterCache.Core.Prefetch;
using AsterCache.Windows.Native;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Windows;

/// 끝난 사유: 끝까지 만짐, 취소됨, 캐시에 없어 디스크 읽기가 섞임 (그래서 멈춤).
public enum TouchEnd
{
    Completed,
    Canceled,
    NeedsDiskReads,
}

/// 만지기 결과: 만진 바이트, 걸린 시간 (초), 느린 조각 수, 열기·매핑·읽기 오류 수, 끝난 사유, 디스크에서 읽힌 것으로 본 바이트,
/// 스파스 파일이라 열지 않고 건너뛴 바이트 (파일 크기로 자르지 않은 요청 길이, 만진 것도 빠진 것도 아님).
public readonly record struct TouchResult(long Bytes, double Seconds, int SlowPieces, int Errors, TouchEnd End, long DiskBytes = 0, long SkippedBytes = 0);

/// 파일 캐시에 이미 있는 범위를 순서대로 다시 읽어 (작업 집합에 올렸다 놓음) 대기 목록의 맨 뒤로 보냅니다. 디스크 읽기는 하지 않는 것이 전제이고, 캐시에 없는 페이지를 만나면 읽기가 되므로 뷰마다 시스템의 페이지 읽기 누계 (SystemPageReads) 가 얼마나 늘었는지 봐서 디스크에서 읽힌 양이 쌓이면 멈춥니다.
/// 범위마다 뷰를 따로 매핑해 만지고 바로 풀어야 만진 순서대로 밀려납니다 (한 뷰에서 여러 범위를 만들고 끝에 한 번에 풀면 주소 순서). 고정 (잠금) 은 하지 않으며 뷰는 최대 ViewBytes 만 한 번에 열어 둡니다.
/// 읽기는 ReadProcessMemory (자기 프로세스) 로 해서 디스크 오류가 예외가 아니라 반환값으로 옵니다.
/// 스파스 속성 파일은 섹션을 열기 전에 건너뜁니다: 같은 PC 의 시험 파일에서 스파스 파일은 새 섹션으로 만지면 캐시에 있어도 디스크에서 다시 읽혔고 일반 파일은 그렇지 않았습니다. 속성은 읽기만 합니다.
public static unsafe class StandbyTouch
{
    public const int PieceBytes = 1 << 20;
    public const long ViewBytes = 64L << 20;

    // 디스크 읽기 감지 (실측 E12, 같은 PC 의 HDD 256 MiB 시험 파일을 64 MiB 뷰로 만짐): 캐시에 없으면 2.74 초에 PageReadCount 가 +66,115 쪽 (= 258 MiB, 읽은 양과 같음),
    // 캐시에 있으면 0.13~0.15 초에 +65~128 쪽 (다른 프로세스의 읽기), 아무것도 안 하면 2 초에 +449 쪽. 그래서 뷰를 만지는 동안 늘어난 쪽 수에서 다른 프로세스의 읽기로 볼 허용치를 뺀 나머지를
    // 디스크에서 읽힌 양으로 누적하고, 누적이 DiskReadStopBytes 를 넘으면 멈춥니다. 뷰마다 조금씩만 밀려나 있어도 누적으로 잡히고, 뷰 하나가 최대 64 MiB 이므로 최악에도 한 뷰 (+ 그 앞 32 MiB 이하) 까지만 디스크에서 읽고 멈춥니다.

    /// 뷰 하나에서 다른 프로세스의 읽기로 보는 쪽 수 (만지는 시간과 무관한 몫).
    public const int NoisePages = 2;

    /// 만지는 시간에 비례해 다른 프로세스의 읽기로 보는 쪽 수 (초당): 8 MiB/s, 실측 잡음 (초당 225 쪽 안팎) 의 약 9 배.
    public const double NoisePagesPerSecond = 2048.0;

    /// 디스크에서 읽힌 것으로 본 양이 이 값을 넘으면 멈춥니다.
    public const long DiskReadStopBytes = 32L << 20;

    /// 뷰를 만지는 데 걸린 시간 동안 디스크가 읽을 수 있다고 보는 양 (초당): 256 MiB/s. 이 서비스가 예열하는 대상은 HDD 의 게임이라 연속 읽기의 넉넉한 상한이고, 더 빠른 장치에서는 실제 읽기가 덜 세어질 뿐 읽기 자체가 빨라 해가 작습니다.
    // 페이지 읽기 누계는 시스템 전체 값이라 다른 프로세스의 몰림 (같은 PC 에서 0.25 초에 3만~8만 쪽) 이 뷰를 만지는 순간과 겹치면 뷰 전체가 디스크 읽기로 보이기 때문에, 메모리에서 20 ms 에 끝난 뷰는 이 상한으로 몇 MiB 까지만 셉니다.
    public const long MaxDiskBytesPerSecond = 256L << 20;

    /// 페이지 읽기 누계를 읽을 수 없을 때만 쓰는 뷰 단위 시간 기준: 64 MiB 당 이 시간 (ms) 에 비례한 시간을 넘으면 그 뷰는 디스크에서 읽힌 것으로 봅니다 (캐시는 64 MiB 에 약 30 ms, HDD 는 250 ms 이상).
    public const double ViewSlowMsPer64Mib = 256.0;

    // 조각 단위 시간 감시는 안전망이라 느슨합니다 (HDD 기준): 캐시에 없는 1 MiB 를 HDD 에서 읽으면 탐색 + 전송으로 15 ms 이상이고 캐시에 있으면 1 ms 미만인데,
    // 다른 프로그램이 CPU 를 모두 쓰면 선점만으로 수 ms 가 흔히 들어갑니다. 그래서 선점으로는 넘기 어려운 기준만 느린 조각으로 세고, 누적 시간이 길 때만 멈춥니다.
    // (SSD 에서 캐시에 없는 읽기는 이 기준에 걸리지 않을 수 있으나, 게임이 돌지 않을 때만 만지므로 해가 작습니다.)

    /// 1 MiB 당 이 시간 (ms) 보다 오래 걸린 조각은 느린 조각.
    public const double SlowMsPerMib = 20.0;

    /// 작은 조각이 선점 한 번으로 느리다고 세어지지 않게 하는 하한 (ms).
    public const double MinSlowMs = 4.0;

    /// 느린 조각 (실패한 읽기 포함) 의 누적 시간이 이 값 (ms) 을 넘으면 디스크 읽기가 섞인 것으로 보고 멈춥니다.
    public const double SlowBudgetMs = 2000.0;

    private const long Granularity = 64 << 10; // 뷰 시작은 할당 단위로 내림
    private const int MaxOpenFiles = 64;

    private sealed class Tally
    {
        public long Bytes;
        public int Slow;
        public int Errors;
        public double SlowMs;
        public long Disk;
        public long Skipped;
        public required Func<uint?> PageReads;
        public required Func<string, uint> Attributes;
        public required Func<long> Timestamp;
        public readonly Dictionary<string, bool> Sparse = new(StringComparer.OrdinalIgnoreCase); // 이 Run 에서 이미 속성을 본 파일
        public TouchEnd End = TouchEnd.Completed;
    }

    /// 뷰를 ms 동안 만지는 사이 늘어난 페이지 읽기 수 (부호 없는 뺄셈 결과) 가운데 디스크에서 읽힌 것으로 볼 바이트: 잡음 허용치를 빼고 뷰 크기와 그 시간 동안 디스크가 읽을 수 있는 양 (MaxDiskBytesPerSecond) 을 넘지 않게.
    public static long DiskBytesOf(uint pagesRead, long viewBytes, double ms)
    {
        long allowed = NoisePages + (long)(NoisePagesPerSecond * ms / 1000.0);
        long bytes = Math.Clamp((pagesRead - allowed) * 4096L, 0, Math.Max(0, viewBytes));
        return Math.Min(bytes, (long)(MaxDiskBytesPerSecond * ms / 1000.0));
    }

    /// 페이지 읽기 누계가 없을 때: 뷰를 만지는 데 걸린 시간이 바이트에 비례한 기준 (하한 MinSlowMs) 을 넘었는가.
    public static bool ViewTooSlow(double ms, long viewBytes) => ms > Math.Max(MinSlowMs, ViewSlowMsPer64Mib * viewBytes / ViewBytes);

    /// 범위들을 주어진 순서대로 만집니다 (먼저 만진 것이 먼저 밀려남). 열기·매핑·읽기 실패는 그 범위를 건너뛰고 오류로 셉니다.
    public static TouchResult Run(IReadOnlyList<ReadOp> ranges, CancellationToken ct) => Run(ranges, ct, SystemPageReads.Query);

    /// pageReads: 시스템 전체의 디스크에서 읽어 들인 쪽 수 누계 (읽을 수 없으면 null, 시험은 가짜로 바꿈).
    public static TouchResult Run(IReadOnlyList<ReadOp> ranges, CancellationToken ct, Func<uint?> pageReads) => Run(ranges, ct, pageReads, FileGuards.FileAttributes);

    /// attributes: 파일 속성 조회 (예외를 던지면 스파스로 보지 않고 열기를 시도함, 시험은 가짜로 바꿈).
    /// timestamp: 뷰를 만지는 데 걸린 시간을 재는 시계 (Stopwatch 틱, 기본은 Stopwatch.GetTimestamp, 시험은 가짜로 바꿈).
    public static TouchResult Run(IReadOnlyList<ReadOp> ranges, CancellationToken ct, Func<uint?> pageReads, Func<string, uint> attributes, Func<long>? timestamp = null)
    {
        long started = Stopwatch.GetTimestamp();
        var tally = new Tally { PageReads = pageReads, Attributes = attributes, Timestamp = timestamp ?? Stopwatch.GetTimestamp };
        var sections = new Dictionary<string, Section?>(StringComparer.OrdinalIgnoreCase);
        byte* buffer = (byte*)NativeMemory.Alloc(PieceBytes);
        try
        {
            foreach (ReadOp range in ranges)
            {
                if (ct.IsCancellationRequested)
                {
                    tally.End = TouchEnd.Canceled;
                    break;
                }

                TouchRange(range, sections, buffer, tally, ct);
                if (tally.End != TouchEnd.Completed)
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (Section? s in sections.Values)
            {
                s?.Dispose();
            }

            NativeMemory.Free(buffer);
        }

        return new TouchResult(tally.Bytes, Stopwatch.GetElapsedTime(started).TotalSeconds, tally.Slow, tally.Errors, tally.End, tally.Disk, tally.Skipped);
    }

    private static void TouchRange(ReadOp range, Dictionary<string, Section?> sections, byte* buffer, Tally tally, CancellationToken ct)
    {
        if (IsSparse(range.Path, tally))
        {
            tally.Skipped += Math.Max(0, range.Length);
            return;
        }

        if (!sections.TryGetValue(range.Path, out Section? section))
        {
            if (sections.Count >= MaxOpenFiles)
            {
                foreach (Section? s in sections.Values)
                {
                    s?.Dispose();
                }

                sections.Clear();
            }

            sections[range.Path] = section = Section.Open(range.Path);
        }

        if (section is null || range.Offset < 0 || range.Offset >= section.Length)
        {
            tally.Errors++;
            return;
        }

        long end = range.Offset + Math.Min(range.Length, section.Length - range.Offset); // 파일이 줄었으면 끝에서 자름
        for (long pos = range.Offset; pos < end && tally.End == TouchEnd.Completed;)
        {
            long viewEnd = Math.Min(end, pos + ViewBytes);
            long aligned = pos & ~(Granularity - 1);
            nint view = section.Map(aligned, viewEnd - aligned);
            if (view == 0)
            {
                tally.Errors++;
                return;
            }

            try
            {
                uint? before = tally.PageReads();
                long t0 = tally.Timestamp();
                if (!TouchView(view, aligned, pos, viewEnd, buffer, tally, ct))
                {
                    return;
                }

                if (tally.End == TouchEnd.Completed)
                {
                    NoteDiskReads(tally, before, tally.PageReads(), viewEnd - pos, Stopwatch.GetElapsedTime(t0, tally.Timestamp()).TotalMilliseconds);
                }
            }
            finally
            {
                NativeMethods.UnmapViewOfFile(view);
            }

            pos = viewEnd;
        }
    }

    // 스파스 속성 (FILE_ATTRIBUTE_SPARSE_FILE) 인가: 파일마다 한 번만 조회합니다. 조회할 수 없으면 추측하지 않고 false (열기를 시도하고, 실패하면 오류로 셈).
    private static bool IsSparse(string path, Tally tally)
    {
        if (!tally.Sparse.TryGetValue(path, out bool sparse))
        {
            sparse = FileGuards.IsSparse(path, tally.Attributes);
            tally.Sparse[path] = sparse;
        }

        return sparse;
    }

    // 끝까지 만진 뷰에서 디스크로 읽힌 양: 페이지 읽기 누계가 있으면 그 증가분으로, 없으면 시간으로 (느리면 뷰 전체) 봅니다. 쌓인 양이 DiskReadStopBytes 를 넘으면 멈춤.
    private static void NoteDiskReads(Tally tally, uint? before, uint? after, long viewBytes, double ms)
    {
        tally.Disk += before is { } b && after is { } a ? DiskBytesOf(unchecked(a - b), viewBytes, ms) : ViewTooSlow(ms, viewBytes) ? viewBytes : 0;
        if (tally.Disk > DiskReadStopBytes)
        {
            tally.End = TouchEnd.NeedsDiskReads;
        }
    }

    // 뷰 안의 [from, to) 를 조각마다 읽습니다. 읽기가 실패하면 이 뷰의 나머지를 포기하고 false.
    private static bool TouchView(nint view, long viewStart, long from, long to, byte* buffer, Tally tally, CancellationToken ct)
    {
        for (long pos = from; pos < to;)
        {
            if (ct.IsCancellationRequested)
            {
                tally.End = TouchEnd.Canceled;
                return true;
            }

            int n = (int)Math.Min(PieceBytes, to - pos);
            long t0 = Stopwatch.GetTimestamp();
            bool ok = NativeMethods.ReadProcessMemory(NativeMethods.GetCurrentProcess(), view + (nint)(pos - viewStart), buffer, (nuint)n, out nuint got) && (long)got == n;
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (!ok)
            {
                tally.Errors++;
                tally.SlowMs += ms; // 망가진 디스크에서 범위마다 오래 실패해도 멈추게 실패한 시간도 셈
                if (tally.SlowMs > SlowBudgetMs)
                {
                    tally.End = TouchEnd.NeedsDiskReads;
                }

                return false;
            }

            tally.Bytes += n;
            if (ms > Math.Max(MinSlowMs, SlowMsPerMib * n / PieceBytes))
            {
                tally.Slow++;
                tally.SlowMs += ms;
                if (tally.SlowMs > SlowBudgetMs)
                {
                    tally.End = TouchEnd.NeedsDiskReads;
                    return true;
                }
            }

            pos += n;
        }

        return true;
    }

    /// 파일 하나의 읽기 전용 매핑 객체 (뷰는 범위마다 따로 만듭니다). 공유 모드는 FileMapping.Open 과 같습니다.
    private sealed class Section : IDisposable
    {
        private readonly SafeFileHandle _mapping;

        private Section(SafeFileHandle mapping, long length)
        {
            _mapping = mapping;
            Length = length;
        }

        public long Length { get; }

        public static Section? Open(string path)
        {
            try
            {
                using SafeFileHandle file = NativeMethods.CreateFileW(path, Win32.GenericRead, Win32.ShareAll, 0, Win32.OpenExisting, 0, 0);
                if (file.IsInvalid)
                {
                    return null;
                }

                long length = RandomAccess.GetLength(file);
                if (length <= 0)
                {
                    return null; // 크기 0 파일은 매핑할 수 없음
                }

                SafeFileHandle mapping = NativeMethods.CreateFileMappingW(file, 0, Win32.PageReadonly, 0, 0, null);
                if (mapping.IsInvalid)
                {
                    mapping.Dispose();
                    return null;
                }

                return new Section(mapping, length);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        public nint Map(long offset, long bytes) => NativeMethods.MapViewOfFile(_mapping, Win32.FileMapRead, (uint)(offset >> 32), (uint)offset, (nuint)bytes);

        public void Dispose() => _mapping.Dispose();
    }
}
