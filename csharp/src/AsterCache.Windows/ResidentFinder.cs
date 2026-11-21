using System.Diagnostics;
using System.Runtime.InteropServices;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Trace;
using AsterCache.Windows.Native;

namespace AsterCache.Windows;

/// 찾기 한 번의 결과: 메모리에 있던 (읽은) 범위, 빠진 것으로 확인하고 건너뛴 범위, 읽은 바이트, 디스크에서 읽힌 것으로 본 바이트, 열기·매핑·읽기 오류 수, 경계를 확인한 횟수, 읽기 수를 조회한 횟수와 하드 폴트 증가,
/// 새로 연 뷰 수, 걸린 시간 (초), 쓴 CPU 시간 (초), 디스크 읽기 상한에 닿아 멈췄는가, 취소됐는가, 스파스라 건너뛴 바이트, 확인하지 못하고 남긴 바이트.
public sealed record FindResult(List<ReadOp> Resident, List<ReadOp> Missing, long Bytes, long DiskBytes, int Errors, int Boundaries, int Checks, long HardFaults, int Maps, double Seconds, double CpuSeconds, bool CapReached, bool Canceled, long SparseBytes, long LeftBytes);

/// 조각을 읽는 곳 (시험이 가짜로 바꿉니다): 파일을 열어 길이를 알려 주고 범위를 읽어 작업 집합에 올렸다 놓습니다.
internal interface IPieceSource : IDisposable
{
    /// 새로 연 뷰 수.
    int Maps { get; }

    /// 파일의 길이. 열 수 없으면 OpenFailed, 스파스 파일이라 열지 않으면 Sparse.
    long Open(string path);

    /// [offset, offset + length) 를 읽습니다 (한 조각). 읽을 수 없으면 false. ms: 읽는 데 걸린 시간.
    bool Read(string path, long offset, long length, out double ms);

    public const long OpenFailed = -1;
    public const long Sparse = -2;
}

/// 유지 목록을 중요한 순서로 걸으며 메모리에 있는 범위를 모으고 (읽어서 작업 집합에 올렸다 놓음), 빠진 조각을 싸게 알아채 건너뜁니다.
/// 빠진 조각 알아채기: 조각 (1 MiB) 하나를 읽는 데 걸린 시간이 의심 기준 (StandbyTouch 의 의심 조각 기준) 보다 길면 8 MiB 를 채우길 기다리지 않고 바로 이 프로세스의 디스크 읽기 수 (하드 폴트) 를 조회합니다 (조회 사이 PromptCheckMinGapMs 이상).
/// 조회 사이 증가분이 PromptFaults 이상이면 빠짐 (근거: 같은 PC 의 실측에서 메모리에 있는 범위는 조회 사이 +0~1, 캐시에 없는 64 KiB 블록 하나는 2~3). 빠진 조각을 하나 읽는 비용은 그 조각 하나 (1 MiB 이하) 입니다.
/// 빠짐을 확인하면 그 뒤의 skipBytes (목록 순서로 이어지는 범위, 시작 SkipBaseBytes) 만 건너뛰어 (읽지 않고) Missing 에 더하고, 건너뛴 다음 지점을 다시 확인합니다: 또 빠져 있으면 폭을 두 배로 (SkipMaxBytes 까지), 메모리에 있으면 SkipBaseBytes 로 되돌립니다.
/// 그래서 Missing 은 실제로 확인한 경계 뒤에서 건너뛴 구간뿐입니다. 느리지 않은 조각은 FaultCheckBytes (64 MiB) 마다 조회하며 이때도 증가분이 PromptFaults 이상이어야 빠짐입니다 (CPU 경합만으로 느려진 조각은 증가분이 없으면 빠짐으로 치지 않음).
/// 디스크에서 읽힌 양이 diskCap 에 닿으면 새 범위를 더 확인하지 않고 멈춥니다 (남은 범위는 만지지 않음, LeftBytes). 스파스 파일은 열지 않고 건너뜁니다.
public static class ResidentFinder
{
    /// 빠짐을 확인한 뒤 처음 건너뛰는 폭 (목록 순서로 이어지는 범위의 바이트).
    public const long SkipBaseBytes = 8L << 20;

    /// 연속으로 빠져 있을 때 건너뛰는 폭의 상한: 128 MiB. 폭이 크면 한 확인으로 많이 건너뛰지만 그 사이에 메모리에 있는 부분을 빠진 것으로 기억할 수 있어 두 배씩만 늘리고 상한을 둡니다.
    public const long SkipMaxBytes = 128L << 20;

    /// 하드 폴트 증가가 이 값 이상이면 빠짐: 메모리에 있는 범위는 +0~1, 빠진 64 KiB 블록 하나는 2~3 (StandbyTouch 의 실측).
    public const uint PromptFaults = 2;

    /// 의심 조각이 나와도 앞의 조회 뒤 이 시간 (ms) 이 지나야 다시 조회합니다: CPU 가 바빠 의심 조각이 흔할 때 조회 (약 4 ms CPU) 가 조각마다 일어나지 않게.
    public const double PromptCheckMinGapMs = 20.0;

    /// 읽은 양이 이만큼 쌓일 때마다 호출하는 쪽에 알립니다 (직렬화 잠금을 놓고 속도 제한으로 쉬게): 128 MiB.
    public const long GroupBytes = 128L << 20;

    /// ordered: 중요한 순서의 범위. onGroup: 읽은 양이 GroupBytes 쌓일 때 (그 양, 걸린 시간 초) 로 부름: 부른 뒤 읽기 수 기준값을 새로 잡습니다. hardFaults·pageReads·attributes·source 는 시험이 가짜로 바꿉니다.
    public static FindResult Find(IReadOnlyList<ReadOp> ordered, long diskCap, CancellationToken ct, Action<long, double>? onGroup = null, Func<uint?>? hardFaults = null, Func<uint?>? pageReads = null, Func<string, uint>? attributes = null)
    {
        using var source = new MappedPieceSource(attributes ?? FileGuards.FileAttributes);
        return Find(ordered, diskCap, ct, onGroup, hardFaults, pageReads, source);
    }

    internal static FindResult Find(IReadOnlyList<ReadOp> ordered, long diskCap, CancellationToken ct, Action<long, double>? onGroup, Func<uint?>? hardFaults, Func<uint?>? pageReads, IPieceSource source, Func<double>? clockMs = null)
    {
        hardFaults ??= ProcessFaults.Query;
        pageReads ??= SystemPageReads.Query;
        Func<double> now = clockMs ?? (() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        long started = Stopwatch.GetTimestamp();
        DateTimeOffset startedUtc = DateTimeOffset.UtcNow;
        double cpuBefore = StandbyTouch.ThreadCpuSeconds();
        List<ReadOp> resident = [];
        List<ReadOp> missing = [];
        List<ReadOp> diskRanges = [];
        List<ReadOp> pending = []; // 앞의 조회 뒤 읽은 조각 (읽기가 확인되면 서비스 자신의 읽기 기록에 남김)
        var lengths = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0;
        long disk = 0;
        long sparse = 0;
        long faults = 0;
        long sinceCheck = 0;
        long sinceGroup = 0;
        int errors = 0;
        int boundaries = 0;
        int checks = 0;
        bool capReached = false;
        bool canceled = false;
        bool signal = true;
        uint? baseFaults = null;
        uint? basePages = null;
        double slowMs = 0; // 읽기 수를 읽을 수 없을 때의 안전망: 느린 조각의 시간 합
        double lastCheckMs = now();
        double groupStartMs = now();
        long skipBytes = SkipBaseBytes; // 다음에 빠짐을 확인했을 때 건너뛸 폭
        long skipLeft = 0; // 지금 건너뛰는 중인 남은 폭
        bool probing = false; // 건너뛴 직후의 첫 조각인가 (여기서 또 빠짐이면 폭을 늘림)
        int next = 0;

        void Rebase()
        {
            _ = hardFaults();
            (baseFaults, basePages) = (hardFaults(), pageReads());
            signal = baseFaults is not null;
            (sinceCheck, lastCheckMs) = (0, now());
        }

        // 읽기 수를 조회해 앞의 조회 뒤 늘어난 만큼을 셈에 더함. 빠짐 (증가분이 PromptFaults 이상) 이면 true.
        bool Check()
        {
            sinceCheck = 0;
            lastCheckMs = now();
            checks++;
            uint? current = hardFaults();
            uint? pagesNow = pageReads();
            if (current is not { } faultsNow || baseFaults is not { } before)
            {
                signal = false;
                (baseFaults, basePages) = (current, pagesNow);
                pending.Clear();
                return false;
            }

            uint delta = unchecked(faultsNow - before);
            long pages = basePages is { } pagesBefore && pagesNow is { } pagesAfter ? unchecked(pagesAfter - pagesBefore) : long.MaxValue / 4096;
            (baseFaults, basePages) = (current, pagesNow);
            faults += delta;
            if (delta > 0)
            {
                disk += Math.Min(pages * 4096L, delta * StandbyTouch.FaultReadBytes);
                diskRanges.AddRange(pending);
            }

            pending.Clear();
            return delta >= PromptFaults;
        }

        void AddResident(ReadOp piece)
        {
            if (resident.Count > 0 && resident[^1] is { } last && string.Equals(last.Path, piece.Path, StringComparison.OrdinalIgnoreCase) && last.Offset + last.Length == piece.Offset)
            {
                resident[^1] = last with { Length = last.Length + piece.Length };
            }
            else
            {
                resident.Add(piece);
            }
        }

        Rebase();
        for (; next < ordered.Count && !capReached && !canceled; next++)
        {
            ReadOp op = ordered[next];
            long pos = op.Offset;
            long end = op.Offset + op.Length;
            if (op.Length <= 0)
            {
                continue;
            }

            if (skipLeft > 0)
            {
                long skipped = Math.Min(skipLeft, op.Length);
                missing.Add(new ReadOp(op.Path, pos, skipped)); // 빠짐을 확인한 뒤 건너뛴 구간: 열지도 읽지도 않음
                (pos, skipLeft) = (pos + skipped, skipLeft - skipped);
                if (pos >= end)
                {
                    continue;
                }
            }

            if (!lengths.TryGetValue(op.Path, out long length))
            {
                lengths[op.Path] = length = source.Open(op.Path);
            }

            if (length == IPieceSource.Sparse)
            {
                sparse += end - pos;
                continue;
            }

            if (length < 0 || pos < 0 || pos >= length)
            {
                errors++;
                continue;
            }

            end = Math.Min(end, length);
            while (pos < end && !capReached && !canceled)
            {
                if (ct.IsCancellationRequested)
                {
                    canceled = true;
                    break;
                }

                if (skipLeft > 0)
                {
                    long skipped = Math.Min(skipLeft, end - pos);
                    missing.Add(new ReadOp(op.Path, pos, skipped));
                    (pos, skipLeft) = (pos + skipped, skipLeft - skipped);
                    continue;
                }

                long n = Math.Min(StandbyTouch.PieceBytes, end - pos);
                if (!source.Read(op.Path, pos, n, out double ms))
                {
                    errors++;
                    break; // 이 범위의 나머지는 읽지 않음
                }

                var piece = new ReadOp(op.Path, pos, n);
                AddResident(piece);
                pending.Add(piece);
                (bytes, sinceCheck, sinceGroup, pos) = (bytes + n, sinceCheck + n, sinceGroup + n, pos + n);
                bool suspect = ms > Math.Max(StandbyTouch.SuspectMinMs, StandbyTouch.SuspectMsPerMib * n / StandbyTouch.PieceBytes);
                if (!signal && ms > Math.Max(StandbyTouch.MinSlowMs, StandbyTouch.SlowMsPerMib * n / StandbyTouch.PieceBytes))
                {
                    slowMs += ms;
                    capReached |= slowMs > StandbyTouch.SlowBudgetMs; // 읽기 수를 읽을 수 없으면 빠짐을 가를 수 없어 시간 안전망으로만 멈춤
                }

                bool due = signal && (sinceCheck >= StandbyTouch.FaultCheckBytes || (suspect && now() - lastCheckMs >= PromptCheckMinGapMs));
                if (due && Check())
                {
                    boundaries++;
                    (skipLeft, probing) = (skipBytes, true);
                    skipBytes = Math.Min(skipBytes * 2, SkipMaxBytes); // 건너뛴 다음 지점도 빠져 있으면 폭을 늘림
                    capReached |= disk >= diskCap;
                }
                else if (probing)
                {
                    (probing, skipBytes) = (false, SkipBaseBytes); // 건너뛴 다음 지점이 메모리에 있음: 폭을 되돌림
                }

                if (sinceGroup >= GroupBytes && onGroup is not null)
                {
                    onGroup(sinceGroup, (now() - groupStartMs) / 1000.0);
                    (sinceGroup, groupStartMs) = (0, now());
                    Rebase();
                }
            }
        }

        if (!capReached && !canceled && sinceCheck > 0 && signal && Check())
        {
            boundaries++; // 끝의 조회: 건너뛸 범위는 남지 않음
        }

        capReached |= disk >= diskCap;
        if (diskRanges.Count > 0)
        {
            SelfReadLog.Note(diskRanges, startedUtc, DateTimeOffset.UtcNow); // 디스크 읽기 집계가 이 읽기를 게임의 읽기로 세지 않게
        }

        long left = capReached || canceled ? ordered.Skip(next).Sum(o => Math.Max(0, o.Length)) : 0;
        return new FindResult(resident, missing, bytes, disk, errors, boundaries, checks, faults, source.Maps, Stopwatch.GetElapsedTime(started).TotalSeconds, Math.Max(0, StandbyTouch.ThreadCpuSeconds() - cpuBefore), capReached, canceled, sparse, left);
    }

    /// 실제 조각 읽기: 파일마다 섹션 (파일 핸들은 닫음) 을 열고 64 MiB 뷰를 필요할 때 열어 ReadProcessMemory (자기 프로세스, 디스크 오류가 예외가 아니라 반환값으로 옴) 로 읽습니다. 뷰는 같은 파일의 이어지는 범위에 다시 쓰고 파일이 바뀌면 풉니다.
    private sealed unsafe class MappedPieceSource(Func<string, uint> attributes) : IPieceSource
    {
        private const long Granularity = 64 << 10;
        private readonly Dictionary<string, StandbyTouch.Section?> _sections = new(StringComparer.OrdinalIgnoreCase);
        private readonly byte* _buffer = (byte*)NativeMemory.Alloc(StandbyTouch.PieceBytes);
        private nint _view;
        private string? _viewPath;
        private long _viewStart;
        private long _viewEnd;

        public int Maps { get; private set; }

        public long Open(string path)
        {
            if (FileGuards.IsSparse(path, attributes))
            {
                return IPieceSource.Sparse;
            }

            StandbyTouch.Section? section = StandbyTouch.Section.Open(path, out _, out _);
            _sections[path] = section;
            return section?.Length ?? IPieceSource.OpenFailed;
        }

        public bool Read(string path, long offset, long length, out double ms)
        {
            ms = 0;
            StandbyTouch.Section? section = _sections.GetValueOrDefault(path);
            if (section is null)
            {
                return false;
            }

            if (_view == 0 || !string.Equals(_viewPath, path, StringComparison.OrdinalIgnoreCase) || offset < _viewStart || offset + length > _viewEnd)
            {
                CloseView();
                _viewStart = offset & ~(Granularity - 1);
                long viewLength = Math.Min(StandbyTouch.ViewBytes, section.Length - _viewStart);
                _view = section.Map(_viewStart, viewLength);
                if (_view == 0)
                {
                    return false;
                }

                (_viewPath, _viewEnd, Maps) = (path, _viewStart + viewLength, Maps + 1);
            }

            long t0 = Stopwatch.GetTimestamp();
            bool ok = NativeMethods.ReadProcessMemory(NativeMethods.GetCurrentProcess(), _view + (nint)(offset - _viewStart), _buffer, (nuint)length, out nuint got) && (long)got == length;
            ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            return ok;
        }

        private void CloseView()
        {
            if (_view != 0)
            {
                _ = NativeMethods.UnmapViewOfFile(_view);
                _view = 0;
            }
        }

        public void Dispose()
        {
            CloseView();
            foreach (StandbyTouch.Section? s in _sections.Values)
            {
                s?.Dispose();
            }

            _sections.Clear();
            NativeMemory.Free(_buffer);
        }
    }
}
