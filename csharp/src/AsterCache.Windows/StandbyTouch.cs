using System.Diagnostics;
using System.Runtime.InteropServices;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Trace;
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

/// 디스크 읽기가 섞여 멈췄을 때 어느 기준에 걸렸는가: 이 프로세스의 디스크 읽기 수 (하드 폴트) 가 허용치를 넘음 (또는 읽은 양이 한도를 넘음), 아니면 안전망 (디스크 읽기가 확인된 느린 조각의 시간 합, 또는 읽기 수를 읽을 수 없을 때의 느린 조각 시간 합).
public enum StopReason
{
    None,
    HardFaults,
    SlowTime,
}

/// 만지기 결과: 만진 바이트, 걸린 시간 (초), 느린 조각 수, 열기·매핑·읽기 오류 수, 끝난 사유, 디스크에서 읽힌 것으로 본 바이트,
/// 스파스 파일이라 열지 않고 건너뛴 바이트 (파일 크기로 자르지 않은 요청 길이, 만진 것도 빠진 것도 아님), 만지는 스레드가 쓴 CPU 시간 (초), 뷰를 매핑한 횟수,
/// 이 프로세스의 디스크 읽기 수 (하드 폴트, 읽을 수 없었으면 0), 멈춘 기준, 읽기 수를 조회한 횟수 (조회 한 번이 약 4 ms CPU), 오류의 종류별 수 (오류가 있을 때만, 기록용).
public readonly record struct TouchResult(long Bytes, double Seconds, int SlowPieces, int Errors, TouchEnd End, long DiskBytes = 0, long SkippedBytes = 0, double CpuSeconds = 0, int Maps = 0, long HardFaults = 0, StopReason Reason = StopReason.None, int Checks = 0, TouchErrors? ErrorKinds = null);

/// 파일 캐시에 이미 있는 범위를 순서대로 다시 읽어 (작업 집합에 올렸다 놓음) 대기 목록의 맨 뒤로 보냅니다. 디스크 읽기는 하지 않는 것이 전제이고, 캐시에 없는 페이지를 만나면 읽기가 되므로 이 프로세스의 디스크 읽기 수 (ProcessFaults, 하드 폴트) 가 얼마나 늘었는지 봐서 허용치를 넘으면 (곧 캐시에 남은 앞부분의 경계에 닿으면) 멈춥니다.
/// 읽기 수는 조회 비용 (약 4 ms) 때문에 조각마다가 아니라 FaultCheckBytes 만큼 만질 때마다, 그리고 시간이 오래 걸린 조각이 나왔을 때 (SuspectCheckBytes 이상 만진 뒤) 봅니다. 시간만으로는 멈추지 않습니다: 게임이 모든 코어를 쓰는 동안에는 메모리에 있는 범위도 느리게 나오기 때문입니다.
/// 같은 파일에서 목록상 바로 이어지는 범위는 주소 폭이 ViewBytes 이하인 동안 뷰 하나로 매핑해 만집니다. 뷰를 풀 때 페이지는 주소 순서로 작업 집합에서 나가므로 합친 범위 사이의 순서는 그 폭 안에서만 흐려집니다.
/// 고정 (잠금) 은 하지 않으며 뷰는 최대 ViewBytes 만 한 번에 열어 둡니다. 읽기는 ReadProcessMemory (자기 프로세스) 로 해서 디스크 오류가 예외가 아니라 반환값으로 옵니다.
/// 스파스 속성 파일은 섹션을 열기 전에 건너뜁니다: 같은 PC 의 시험 파일에서 스파스 파일은 새 섹션으로 만지면 캐시에 있어도 디스크에서 다시 읽혔고 일반 파일은 그렇지 않았습니다. 속성은 읽기만 합니다.
public static unsafe class StandbyTouch
{
    public const int PieceBytes = 1 << 20;
    public const long ViewBytes = 64L << 20;

    // 디스크 읽기 판정 (실측, 같은 PC 의 HDD 시험 파일): 메모리에 있는 범위를 만지면 이 프로세스의 하드 폴트가 +0 (다른 프로세스가 같은 디스크를 읽고 모든 코어를 쓰는 중에도), 캐시에 없는 범위는 1 MiB 당 약 19 회 (평균 약 54 KiB 마다 한 번),
    // 캐시에 없는 64 KiB 블록 하나는 2~3 회. 그래서 조회 사이에 늘어난 읽기 수가 BoundaryFaults 를 넘으면 캐시에 남은 부분의 경계에 닿은 것으로 봅니다 (약 1.7 MiB 어치까지는 흩어진 빈 블록으로 보고 지나감).

    /// 조회 한 번 사이에 늘어난 디스크 읽기 수가 이 값을 넘으면 경계로 보고 멈춥니다.
    public const uint BoundaryFaults = 32;

    /// 한 번의 디스크 읽기 (하드 폴트) 가 읽는 양의 상한: 64 KiB (페이지 읽기 묶음). 읽은 양을 셀 때 읽기 수 × 이 값과 시스템 페이지 읽기 증가분 가운데 작은 쪽을 씁니다.
    public const long FaultReadBytes = 64L << 10;

    /// 읽기 수를 보는 간격: 이만큼 만질 때마다 한 번 (조회 비용 약 4 ms 를 만지는 일에 비해 작게). 경계 뒤에서 디스크에서 읽는 양의 최악이 이만큼입니다.
    public const long FaultCheckBytes = 64L << 20;

    /// 시간이 오래 걸린 조각 (조각이 SuspectMsPerMib 보다 느림) 이 나오면 앞의 조회 뒤 이만큼 이상 만졌을 때 바로 읽기 수를 봅니다 (디스크가 느려 경계 뒤의 읽기가 오래 걸려도 이 정도에서 멈춤).
    public const long SuspectCheckBytes = 8L << 20;

    /// 당겨진 조회는 앞의 조회 뒤 만지는 데 이 시간 (ms) 이상 걸렸을 때만 합니다: CPU 가 바빠 의심 조각이 흔할 때 조회 (약 4 ms 의 CPU) 가 8 MiB 마다 이어지지 않게 (회차당 조회가 64 MiB 마다 한 번 수준으로 묶임).
    public const double SuspectCheckMinMs = 100.0;

    /// 느린 조각의 시간을 안전망의 합에 더하려면 그 조회 구간의 읽기 수 증가가 이 값 이상이어야 합니다. 메모리에 있는 범위를 만질 때도 조회 사이에 +1 쯤의 잡음이 실측되었고 (조회·코드가 다시 올라오는 몫),
    /// 캐시에 없는 64 KiB 블록 하나는 2~3 회라 4 회는 빈 블록 둘 이상입니다: 잡음이 낀 구간의 CPU 선점 시간이 쌓여 멈추지 않게.
    public const uint SafetyNetMinFaults = 4;

    /// 1 MiB 당 이 시간 (ms) 보다 오래 걸린 조각은 의심 조각 (메모리에서는 1 MiB 에 약 0.3 ms): 읽기 수를 볼 때가 되었는지와 서비스 자신의 읽기 기록에 남길지만 정함. CPU 선점만으로도 나올 수 있어 이것만으로는 멈추지 않습니다.
    public const double SuspectMsPerMib = 2.0;

    /// 작은 조각이 선점 한 번으로 의심 조각이 되지 않게 하는 하한 (ms).
    public const double SuspectMinMs = 2.0;

    /// 디스크에서 읽힌 것으로 본 양이 이 값을 넘으면 멈춥니다.
    public const long DiskReadStopBytes = 32L << 20;

    // 조각 단위 시간 감시는 최후의 안전망입니다 (HDD 기준): 캐시에 없는 1 MiB 를 HDD 에서 읽으면 탐색 + 전송으로 15 ms 이상이고 캐시에 있으면 1 ms 미만인데, 다른 프로그램이 CPU 를 모두 쓰면 선점만으로 수 ms 가 흔히 들어갑니다.
    // 그래서 느린 조각의 시간은 그 사이 디스크 읽기가 확인되었을 때만 합에 더하고 (읽기 수를 읽을 수 없으면 모두 더함), 합이 길 때만 멈춥니다.

    /// 1 MiB 당 이 시간 (ms) 보다 오래 걸린 조각은 느린 조각.
    public const double SlowMsPerMib = 20.0;

    /// 작은 조각이 선점 한 번으로 느리다고 세어지지 않게 하는 하한 (ms).
    public const double MinSlowMs = 4.0;

    /// 느린 조각 (실패한 읽기 포함) 의 누적 시간이 이 값 (ms) 을 넘으면 멈춥니다.
    public const double SlowBudgetMs = 2000.0;

    private const long Granularity = 64 << 10; // 뷰 시작은 할당 단위로 내림
    private const int MaxOpenFiles = 64;

    [ThreadStatic]
    private static Meter? _round;

    // 한 회차 (Run 여러 번) 동안 이어 쓰는 읽기 수의 기준값
    private sealed class Meter
    {
        public uint? Faults;
        public uint? Pages;
        public long SinceBytes; // 마지막 조회 뒤 만졌지만 아직 조회하지 않은 바이트 (다음 Run 이 이어 세고, Settle 이 마무리)
        public Func<uint?>? HardFaults;
        public Func<uint?>? PageReads;
    }

    private sealed class RoundScope(Meter? previous) : IDisposable
    {
        public void Dispose() => _round = previous;
    }

    /// 이 스레드의 Run 여러 번을 한 회차로 묶습니다 (using): 호출마다 읽기 수를 처음부터 다시 조회하지 않고 앞 호출이 끝난 값을 이어 써 조회 횟수를 줄입니다.
    public static IDisposable Round()
    {
        var round = new RoundScope(_round);
        _round = new Meter();
        return round;
    }

    private sealed class Tally
    {
        public long Bytes;
        public int Slow;
        public int Errors;
        public readonly TouchErrors Kinds = new(); // Errors 와 같은 단위로 센 종류별 수 (기록용)
        public readonly Dictionary<string, (TouchErrorKind Kind, int Code)> OpenFailures = new(StringComparer.OrdinalIgnoreCase); // 열지 못한 파일의 종류와 오류 번호
        public int LastReadCode; // 가장 최근 읽기 실패의 Win32 오류 번호 (같은 뷰에서 포기한 나머지 부분도 같은 번호로 셈)
        public double SlowMs;
        public double SlowSince; // 앞의 조회 뒤 나온 느린 조각의 시간 합 (그 사이 디스크 읽기가 확인되면 SlowMs 에 더함)
        public long Disk;
        public long Skipped;
        public int Maps;
        public long Faults;
        public long SinceCheck;
        public double SinceCheckMs; // 앞의 조회 뒤 조각을 만지는 데 걸린 시간 합
        public int Checks;
        public bool Signal = true; // 읽기 수를 읽을 수 있는가
        public uint? BaselineFaults;
        public uint? BaselinePages;
        public StopReason Reason;
        public readonly List<ReadOp> Pending = []; // 앞의 조회 뒤 나온 의심 조각 (읽기가 확인되면 서비스 자신의 읽기 기록에 남김)
        public readonly List<ReadOp> DiskRanges = [];
        public required Func<uint?> HardFaults;
        public required Func<uint?> PageReads;
        public required Func<string, uint> Attributes;
        public required Func<long> Timestamp;
        public readonly Dictionary<string, bool> Sparse = new(StringComparer.OrdinalIgnoreCase); // 이 Run 에서 이미 속성을 본 파일
        public TouchEnd End = TouchEnd.Completed;
    }

    /// 범위들을 주어진 순서대로 만집니다 (먼저 만진 것이 먼저 밀려남). 열기·매핑·읽기 실패는 그 범위를 건너뛰고 오류로 셉니다.
    public static TouchResult Run(IReadOnlyList<ReadOp> ranges, CancellationToken ct) => Run(ranges, ct, ProcessFaults.Query);

    /// hardFaults: 이 프로세스의 디스크 읽기 수 누계 (읽을 수 없으면 null, 시험은 가짜로 바꿈).
    public static TouchResult Run(IReadOnlyList<ReadOp> ranges, CancellationToken ct, Func<uint?> hardFaults) => Run(ranges, ct, hardFaults, SystemPageReads.Query);

    /// pageReads: 시스템 전체의 페이지 읽기 누계 (읽은 양을 셀 때 읽기 수 × FaultReadBytes 의 상한을 좁히는 데만 씀, 읽을 수 없으면 null).
    public static TouchResult Run(IReadOnlyList<ReadOp> ranges, CancellationToken ct, Func<uint?> hardFaults, Func<uint?> pageReads) => Run(ranges, ct, hardFaults, pageReads, FileGuards.FileAttributes);

    /// attributes: 파일 속성 조회 (예외를 던지면 스파스로 보지 않고 열기를 시도함, 시험은 가짜로 바꿈).
    /// timestamp: 조각을 만지는 데 걸린 시간을 재는 시계 (Stopwatch 틱, 기본은 Stopwatch.GetTimestamp, 시험은 가짜로 바꿈).
    public static TouchResult Run(IReadOnlyList<ReadOp> ranges, CancellationToken ct, Func<uint?> hardFaults, Func<uint?> pageReads, Func<string, uint> attributes, Func<long>? timestamp = null)
    {
        long started = Stopwatch.GetTimestamp();
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        double cpuBefore = ThreadCpuSeconds();
        Meter? meter = _round;
        var tally = new Tally { HardFaults = hardFaults, PageReads = pageReads, Attributes = attributes, Timestamp = timestamp ?? Stopwatch.GetTimestamp };
        if (meter?.Faults is { } carried)
        {
            (tally.BaselineFaults, tally.BaselinePages, tally.SinceCheck) = (carried, meter.Pages, meter.SinceBytes);
        }
        else
        {
            _ = hardFaults(); // 첫 조회는 버립니다: 조회 버퍼와 코드가 다시 올라오며 나는 읽기 (게임이 메모리를 가져간 뒤 쉬다 깬 때 수십 회) 가 기준값 뒤 증가분에 들어가지 않게
            tally.BaselineFaults = hardFaults();
            tally.BaselinePages = pageReads();
        }

        tally.Signal = tally.BaselineFaults is not null;
        var sections = new Dictionary<string, Section?>(StringComparer.OrdinalIgnoreCase);
        byte* buffer = (byte*)NativeMemory.Alloc(PieceBytes);
        try
        {
            for (int i = 0; i < ranges.Count; i++)
            {
                if (ct.IsCancellationRequested)
                {
                    tally.End = TouchEnd.Canceled;
                    break;
                }

                i = TouchWindow(ranges, i, sections, buffer, tally, ct);
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

        // 마지막 조회: 따로 부른 Run 은 항상, 한 회차 (Round) 안의 Run 은 만진 양이 어느 정도일 때만 (나머지는 다음 Run 이 이어 세고, 회차의 끝은 Settle 이 조회)
        if (tally.Signal && tally.SinceCheck > 0 && (meter is null || tally.SinceCheck >= SuspectCheckBytes))
        {
            Check(tally);
        }

        if (meter is not null)
        {
            (meter.Faults, meter.Pages, meter.SinceBytes, meter.HardFaults, meter.PageReads) = (tally.BaselineFaults, tally.BaselinePages, tally.Signal ? tally.SinceCheck : 0, hardFaults, pageReads);
        }

        if (tally.DiskRanges.Count > 0)
        {
            SelfReadLog.Note(tally.DiskRanges, startedUtc, DateTimeOffset.UtcNow); // 디스크 읽기 집계가 이 읽기를 게임의 읽기로 세지 않게
        }

        return new TouchResult(tally.Bytes, Stopwatch.GetElapsedTime(started).TotalSeconds, tally.Slow, tally.Errors, tally.End, tally.Disk, tally.Skipped, Math.Max(0, ThreadCpuSeconds() - cpuBefore), tally.Maps, tally.Faults, tally.Reason, tally.Checks, tally.Errors > 0 ? tally.Kinds : null);
    }

    /// 한 회차 (Round) 의 마지막에 부릅니다: 마지막 조회 뒤 만졌지만 아직 조회하지 않은 구간이 있으면 읽기 수를 조회해 그 구간의 디스크 읽기와 경계를 알려 줍니다 (회차 밖이거나 남은 구간이 없으면 빈 결과).
    /// 이 구간의 읽기가 허용치를 넘으면 End 가 NeedsDiskReads 입니다.
    public static TouchResult Settle()
    {
        Meter? meter = _round;
        if (meter is not { SinceBytes: > 0, HardFaults: { } hardFaults, PageReads: { } pageReads, Faults: { } faults })
        {
            return new TouchResult(0, 0, 0, 0, TouchEnd.Completed);
        }

        var tally = new Tally { HardFaults = hardFaults, PageReads = pageReads, Attributes = _ => 0, Timestamp = Stopwatch.GetTimestamp, BaselineFaults = faults, BaselinePages = meter.Pages, SinceCheck = meter.SinceBytes };
        meter.SinceBytes = 0;
        Check(tally);
        (meter.Faults, meter.Pages) = (tally.BaselineFaults, tally.BaselinePages);
        return new TouchResult(0, 0, 0, 0, tally.End, tally.Disk, 0, 0, 0, tally.Faults, tally.Reason, tally.Checks);
    }

    /// 표본 한 곳의 크기: 64 KiB (한 번의 디스크 읽기가 읽는 양의 상한과 같음).
    public const int ProbeBytes = 64 << 10;

    /// 표본 확인 (기록용): 범위마다 따로 만지고 그 앞뒤의 이 프로세스의 디스크 읽기 수 (하드 폴트) 증가를 돌려줍니다 (범위당 하나). 메모리에 있으면 0 (잡음 +1 쯤), 없으면 한 블록이 2~3 회입니다.
    /// 열 수 없거나 스파스 파일이거나 파일 끝 밖이면 -1, 읽기 수를 읽을 수 없으면 null. 캐시에 없는 범위는 읽히므로 (범위당 최대 ProbeBytes) 호출하는 쪽이 개수를 적게 (16 곳) 잡습니다. 판정·순서에는 쓰지 않습니다.
    public static int[]? Probe(IReadOnlyList<ReadOp> samples, CancellationToken ct) => Probe(samples, ct, ProcessFaults.Query, FileGuards.FileAttributes);

    public static int[]? Probe(IReadOnlyList<ReadOp> samples, CancellationToken ct, Func<uint?> hardFaults, Func<string, uint> attributes)
    {
        uint? first = hardFaults(); // 이 조회는 판정에 쓰지 않습니다 (Run 과 같은 이유). 표본 확인이 시작할 때의 값이라 회차 기준값 보정의 시작점으로만 씀
        if (hardFaults() is not { } before)
        {
            return null;
        }

        int[] faults = new int[samples.Count];
        Array.Fill(faults, -1);
        Meter? round = _round; // 같은 회차 (Round) 의 다음 Run 이 이어 쓰는 기준값
        uint start = first ?? before;
        uint? pagesStart = round?.PageReads?.Invoke();
        byte* buffer = (byte*)NativeMemory.Alloc(ProbeBytes);
        try
        {
            for (int i = 0; i < samples.Count && !ct.IsCancellationRequested; i++)
            {
                ReadOp range = samples[i];
                if (FileGuards.IsSparse(range.Path, attributes) || Section.Open(range.Path, out _, out _) is not { } section)
                {
                    continue;
                }

                using (section)
                {
                    long end = EndOf(range, section);
                    if (range.Offset < 0 || range.Offset >= section.Length || end <= range.Offset)
                    {
                        continue;
                    }

                    long viewStart = range.Offset & ~(Granularity - 1);
                    nint view = section.Map(viewStart, end - viewStart);
                    if (view == 0)
                    {
                        continue;
                    }

                    try
                    {
                        bool ok = NativeMethods.ReadProcessMemory(NativeMethods.GetCurrentProcess(), view + (nint)(range.Offset - viewStart), buffer, (nuint)Math.Min(ProbeBytes, end - range.Offset), out _);
                        if (hardFaults() is not { } after)
                        {
                            return null;
                        }

                        faults[i] = ok ? (int)Math.Min(int.MaxValue, unchecked(after - before)) : -1;
                        before = after;
                    }
                    finally
                    {
                        NativeMethods.UnmapViewOfFile(view);
                    }
                }
            }
        }
        finally
        {
            NativeMemory.Free(buffer);

            // 표본이 낸 읽기는 회차의 기준값에 더해 다음 Run 의 경계 판정에 섞이지 않게 합니다 (표본 확인은 기록만 하고 판정에 영향을 주지 않음).
            if (round is { Faults: { } known })
            {
                round.Faults = unchecked(known + (before - start));
                if (round.Pages is { } knownPages && pagesStart is { } pages0 && round.PageReads?.Invoke() is { } pages1)
                {
                    round.Pages = unchecked(knownPages + (pages1 - pages0));
                }
            }
        }

        return faults;
    }

    // 이 스레드가 지금까지 쓴 CPU 시간 (초): 만지는 스레드에서만 부르므로 같은 스레드의 앞뒤 값의 차가 만지는 데 쓴 CPU 시간.
    internal static double ThreadCpuSeconds()
    {
        long creation, exit, kernel, user;
        return NativeMethods.GetThreadTimes(NativeMethods.GetCurrentThread(), &creation, &exit, &kernel, &user) ? (kernel + user) / 1e7 : 0;
    }

    // 읽기 수를 조회해 앞의 조회 뒤 늘어난 만큼을 셈에 더하고, 경계 (허용치 초과), 읽은 양 한도, 안전망 (읽기가 확인된 느린 조각의 시간 합) 에 닿았으면 멈춤 표시.
    private static void Check(Tally tally)
    {
        tally.SinceCheck = 0;
        tally.SinceCheckMs = 0;
        tally.Checks++;
        uint? faultsNow = tally.HardFaults();
        uint? pagesNow = tally.PageReads();
        if (faultsNow is not { } now || tally.BaselineFaults is not { } before)
        {
            tally.Signal = false; // 이후는 느린 조각의 시간 합만 안전망으로 (디스크 읽기와 CPU 선점을 가를 수 없음)
            tally.SlowMs += tally.SlowSince;
            (tally.SlowSince, tally.BaselineFaults) = (0, faultsNow);
            tally.Pending.Clear();
            StopIfSlow(tally);
            return;
        }

        uint delta = unchecked(now - before);
        tally.BaselineFaults = now;
        long pages = tally.BaselinePages is { } pagesBefore && pagesNow is { } pagesAfter ? unchecked(pagesAfter - pagesBefore) : long.MaxValue / 4096;
        tally.BaselinePages = pagesNow;
        tally.Faults += delta;
        if (delta > 0)
        {
            tally.Disk += Math.Min(pages * 4096L, delta * FaultReadBytes); // 읽기가 있었던 구간만: 다른 프로세스의 읽기가 섞인 시스템 값은 읽기 수가 있을 때의 상한으로만 씀
            tally.DiskRanges.AddRange(tally.Pending);
        }

        if (delta >= SafetyNetMinFaults)
        {
            tally.SlowMs += tally.SlowSince; // 읽기가 분명히 있었던 구간의 느린 조각 시간만 안전망에 더함 (잡음 1~3 회와 CPU 선점은 더하지 않음)
        }

        tally.SlowSince = 0;
        tally.Pending.Clear();
        if (delta > BoundaryFaults || tally.Disk > DiskReadStopBytes)
        {
            (tally.End, tally.Reason) = (TouchEnd.NeedsDiskReads, StopReason.HardFaults);
            return;
        }

        StopIfSlow(tally);
    }

    private static void StopIfSlow(Tally tally)
    {
        if (tally.SlowMs > SlowBudgetMs)
        {
            (tally.End, tally.Reason) = (TouchEnd.NeedsDiskReads, StopReason.SlowTime);
        }
    }

    // 파일의 섹션을 돌려줍니다 (이 Run 에서 이미 열었으면 그것, 열린 파일이 MaxOpenFiles 개면 모두 닫고 비운 뒤). 열 수 없으면 null.
    private static Section? OpenSection(string path, Dictionary<string, Section?> sections, Tally tally)
    {
        if (!sections.TryGetValue(path, out Section? section))
        {
            if (sections.Count >= MaxOpenFiles)
            {
                foreach (Section? s in sections.Values)
                {
                    s?.Dispose();
                }

                sections.Clear();
            }

            if (tally.OpenFailures.ContainsKey(path))
            {
                sections[path] = section = null;
            }
            else
            {
                sections[path] = section = Section.Open(path, out TouchErrorKind kind, out int code);
                if (section is null)
                {
                    tally.OpenFailures[path] = (kind, code);
                }
            }
        }

        return section;
    }

    private static long EndOf(ReadOp range, Section section) => range.Offset + Math.Min(range.Length, section.Length - range.Offset); // 파일이 줄었으면 끝에서 자름

    // 범위 하나: ranges[first] 와, 같은 파일에서 바로 뒤따르는 범위들을 합쳐 (전체 주소 폭이 ViewBytes 이하인 동안) 뷰 하나로 만집니다. 마지막으로 처리한 범위의 위치를 돌려줍니다.
    private static int TouchWindow(IReadOnlyList<ReadOp> ranges, int first, Dictionary<string, Section?> sections, byte* buffer, Tally tally, CancellationToken ct)
    {
        ReadOp range = ranges[first];
        if (IsSparse(range.Path, tally))
        {
            tally.Skipped += Math.Max(0, range.Length);
            return first;
        }

        Section? section = OpenSection(range.Path, sections, tally);
        if (section is null || range.Offset < 0 || range.Offset >= section.Length)
        {
            tally.Errors++;
            (TouchErrorKind kind, int code) = section is null ? tally.OpenFailures[range.Path] : (TouchErrorKind.BeyondEnd, 0);
            tally.Kinds.Add(kind, 1, range.Path, code, kind == TouchErrorKind.Open ? range.Length : 0);
            return first;
        }

        long end = EndOf(range, section);
        if (end <= range.Offset)
        {
            return first; // 길이 0: 매핑 길이 0 은 파일 전체를 뜻하므로 열지 않음
        }

        long windowStart = range.Offset & ~(Granularity - 1);
        if (end - windowStart > ViewBytes)
        {
            // 뷰 하나에 안 들어가는 긴 범위: 이 범위만 뷰 단위로 나눠 만짐
            for (long pos = range.Offset; pos < end && tally.End == TouchEnd.Completed;)
            {
                long viewEnd = Math.Min(end, pos + ViewBytes);
                if (!TouchWindowView(section, range.Path, pos & ~(Granularity - 1), [(pos, viewEnd)], buffer, tally, ct))
                {
                    break;
                }

                pos = viewEnd;
            }

            return first;
        }

        var parts = new List<(long From, long To)> { (range.Offset, end) };
        long windowEnd = end;
        int last = first;
        for (int j = first + 1; j < ranges.Count; j++)
        {
            ReadOp next = ranges[j];
            if (next.Length <= 0 || !string.Equals(next.Path, range.Path, StringComparison.OrdinalIgnoreCase) || next.Offset < 0 || next.Offset >= section.Length)
            {
                break;
            }

            long nextEnd = EndOf(next, section);
            long nextStart = next.Offset & ~(Granularity - 1);
            if (Math.Max(windowEnd, nextEnd) - Math.Min(windowStart, nextStart) > ViewBytes)
            {
                break;
            }

            parts.Add((next.Offset, nextEnd));
            (windowStart, windowEnd) = (Math.Min(windowStart, nextStart), Math.Max(windowEnd, nextEnd));
            last = j;
        }

        _ = TouchWindowView(section, range.Path, windowStart, parts, buffer, tally, ct);
        return last;
    }

    // 뷰 하나 ([viewStart, 부분들의 끝 중 가장 먼 곳)) 를 매핑하고 부분마다 순서대로 만진 뒤 풉니다. 읽기가 실패해 이 뷰의 나머지를 포기했거나 매핑하지 못했거나 멈췄으면 false.
    private static bool TouchWindowView(Section section, string path, long viewStart, List<(long From, long To)> parts, byte* buffer, Tally tally, CancellationToken ct)
    {
        nint view = section.Map(viewStart, parts.Max(p => p.To) - viewStart);
        if (view == 0)
        {
            int code = Marshal.GetLastPInvokeError();
            tally.Errors += parts.Count; // 합친 부분 모두 만지지 못함
            tally.Kinds.Add(TouchErrorKind.Map, parts.Count, path, code);
            return false;
        }

        tally.Maps++;
        try
        {
            for (int i = 0; i < parts.Count; i++)
            {
                (long from, long to) = parts[i];
                if (!TouchPieces(view, viewStart, path, from, to, buffer, tally, ct))
                {
                    tally.Errors += parts.Count - i - 1; // 읽기가 실패해 포기한 뷰의 나머지 부분도 오류로 셈
                    tally.Kinds.Add(TouchErrorKind.Read, parts.Count - i - 1, path, tally.LastReadCode);
                    return false;
                }

                if (tally.End != TouchEnd.Completed)
                {
                    return false;
                }
            }

            return true;
        }
        finally
        {
            NativeMethods.UnmapViewOfFile(view);
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

    // 뷰 안의 [from, to) 를 조각마다 읽습니다. 읽기가 실패하면 이 뷰의 나머지를 포기하고 false.
    private static bool TouchPieces(nint view, long viewStart, string path, long from, long to, byte* buffer, Tally tally, CancellationToken ct)
    {
        for (long pos = from; pos < to;)
        {
            if (ct.IsCancellationRequested)
            {
                tally.End = TouchEnd.Canceled;
                return true;
            }

            int n = (int)Math.Min(PieceBytes, to - pos);
            long t0 = tally.Timestamp();
            bool ok = NativeMethods.ReadProcessMemory(NativeMethods.GetCurrentProcess(), view + (nint)(pos - viewStart), buffer, (nuint)n, out nuint got) && (long)got == n;
            int readCode = ok ? 0 : Marshal.GetLastPInvokeError();
            double ms = Stopwatch.GetElapsedTime(t0, tally.Timestamp()).TotalMilliseconds;
            if (!ok)
            {
                tally.Errors++;
                tally.LastReadCode = readCode;
                tally.Kinds.Add(TouchErrorKind.Read, 1, path, readCode);
                tally.SlowMs += ms; // 망가진 디스크에서 범위마다 오래 실패해도 멈추게 실패한 시간도 셈
                StopIfSlow(tally);
                return false;
            }

            tally.Bytes += n;
            tally.SinceCheck += n;
            tally.SinceCheckMs += ms;
            bool slow = ms > Math.Max(MinSlowMs, SlowMsPerMib * n / PieceBytes);
            tally.Slow += slow ? 1 : 0;
            if (!tally.Signal)
            {
                if (slow)
                {
                    tally.SlowMs += ms;
                    StopIfSlow(tally);
                    if (tally.End != TouchEnd.Completed)
                    {
                        return true;
                    }
                }
            }
            else
            {
                bool suspect = ms > Math.Max(SuspectMinMs, SuspectMsPerMib * n / PieceBytes);
                tally.SlowSince += slow ? ms : 0;
                if (suspect)
                {
                    tally.Pending.Add(new ReadOp(path, pos, n));
                }

                if (tally.SinceCheck >= FaultCheckBytes || (suspect && tally.SinceCheck >= SuspectCheckBytes && tally.SinceCheckMs >= SuspectCheckMinMs))
                {
                    Check(tally);
                    if (tally.End != TouchEnd.Completed)
                    {
                        return true;
                    }
                }
            }

            pos += n;
        }

        return true;
    }

    /// 파일 하나의 읽기 전용 매핑 객체 (뷰는 범위마다 따로 만듭니다). 공유 모드는 FileMapping.Open 과 같습니다.
    internal sealed class Section : IDisposable
    {
        private readonly SafeFileHandle _mapping;

        private Section(SafeFileHandle mapping, long length)
        {
            _mapping = mapping;
            Length = length;
        }

        public long Length { get; }

        /// 열지 못하면 null 이고 kind·code 에 이유를 돌려줍니다: 열기 실패와 그 Win32 오류 번호, 크기 0 (TouchErrors.EmptyFile), 섹션 만들기 실패 (Map 종류).
        public static Section? Open(string path, out TouchErrorKind kind, out int code)
        {
            (kind, code) = (TouchErrorKind.Open, 0);
            try
            {
                using SafeFileHandle file = NativeMethods.CreateFileW(path, Win32.GenericRead, Win32.ShareAll, 0, Win32.OpenExisting, 0, 0);
                if (file.IsInvalid)
                {
                    code = Marshal.GetLastPInvokeError();
                    return null;
                }

                long length = RandomAccess.GetLength(file);
                if (length <= 0)
                {
                    code = TouchErrors.EmptyFile;
                    return null; // 크기 0 파일은 매핑할 수 없음
                }

                SafeFileHandle mapping = NativeMethods.CreateFileMappingW(file, 0, Win32.PageReadonly, 0, 0, null);
                if (mapping.IsInvalid)
                {
                    (kind, code) = (TouchErrorKind.Map, Marshal.GetLastPInvokeError());
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
