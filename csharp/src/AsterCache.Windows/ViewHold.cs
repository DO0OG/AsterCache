using System.Diagnostics;
using System.Runtime.InteropServices;
using AsterCache.Core.Prefetch;
using AsterCache.Windows.Native;

namespace AsterCache.Windows;

/// 붙들기 한 배치의 결과: 붙든 바이트, 디스크에서 읽힌 것으로 본 바이트, 스파스라 건너뛴 바이트, 오류 수 (열기·매핑·읽기·파일 끝 밖), 느린 조각 수, 새로 연 뷰 수, 읽기 수를 조회한 횟수,
/// 이 프로세스의 디스크 읽기 수 (하드 폴트) 증가, 걸린 시간 (초), 쓴 CPU 시간 (초), 디스크 읽기가 섞여 경계로 보고 멈췄으면 그때 읽던 범위의 배치 안 위치 (그 뒤 범위는 읽지 않았음, 없으면 -1).
public readonly record struct HoldBatchResult(long Bytes, long DiskBytes, long SkippedBytes, int Errors, int SlowPieces, int Views, int Checks, long HardFaults, double Seconds, double CpuSeconds, int BoundaryIndex);

/// 점검 한 번의 결과: 다시 읽어 본 바이트, 메모리 관리자가 줄여 디스크에서 다시 읽힌 바이트, 붙든 목록에서 뺀 바이트 (바뀐 파일의 몫은 제외), 다시 읽기 상한에 닿아 멈췄는가,
/// 크기·수정 시각이 바뀌어 뷰를 닫은 파일, 읽기 수를 조회한 횟수, 하드 폴트 증가, 걸린 시간 (초), 쓴 CPU 시간 (초).
public readonly record struct HoldCheckResult(long CheckedBytes, long RefilledBytes, long DroppedBytes, bool CapReached, IReadOnlyList<string> ChangedFiles, int Checks, long HardFaults, double Seconds, double CpuSeconds);

/// 게임이 실행되는 동안 유지 목록을 서비스의 매핑 뷰로 붙들어 두는 곳 (ViewHold 의 인터페이스, 시험은 가짜로 바꿉니다).
public interface IKeepHold : IDisposable
{
    /// 지금 붙든 바이트.
    long HeldBytes { get; }

    /// 뷰를 열어 둔 파일 수와 뷰 수.
    int Files { get; }

    int Views { get; }

    /// 이 프로세스의 디스크 읽기 수 (하드 폴트) 기준값을 지금 값으로 다시 잡습니다: 붙들기·점검 한 회차를 시작할 때 부릅니다 (그 사이 다른 스레드가 낸 읽기가 이번 판정에 섞이지 않게).
    void Rebase();

    /// 범위들을 순서대로 읽어 (이미 붙든 구간은 건너뜀) 작업 집합에 올린 채 뷰를 닫지 않습니다. 디스크 읽기가 섞여 경계를 만나면 거기서 멈추고 그 위치를 돌려줍니다.
    HoldBatchResult Hold(IReadOnlyList<ReadOp> batch, CancellationToken ct);

    /// 붙든 것을 덜 중요한 쪽 (나중에 붙든 쪽) 부터 bytes 만큼 내놓고 (VirtualUnlock) 내놓은 바이트를 돌려줍니다. 내놓은 범위는 다시 붙들지 않습니다.
    long Release(long bytes);

    /// 바뀐 파일의 뷰를 닫고, 붙든 범위를 중요한 쪽부터 다시 읽어 메모리 관리자가 줄여 간 조각을 하드 폴트로 확인합니다. 디스크에서 다시 읽힌 양이 refillStopBytes 를 넘으면 그 범위를 목록에서 빼고 멈춥니다.
    HoldCheckResult Recheck(long refillStopBytes, CancellationToken ct);
}

/// 파일마다 섹션을 만들고 파일 핸들은 바로 닫은 채, 큰 뷰 (파일 전체 또는 WindowBytes 창) 를 열어 두고 유지 목록의 범위만 읽어 서비스 작업 집합에 올려 둡니다 (읽지 않은 쪽은 메모리를 쓰지 않음).
/// 뷰를 닫지 않으면 대기 목록으로 가지 않아 게임의 읽기나 다른 프로그램의 메모리 사용이 재사용 순서에서 이 페이지를 건드리지 못합니다. 페이지를 잠그지 않고 (VirtualLock·작업 집합 최소값 강제 없음) 내놓을 때는 잠그지 않은 범위에 VirtualUnlock 을 불러 작업 집합에서 뺍니다 (반환값 FALSE·오류 158 은 정상).
/// 파일 핸들을 닫은 섹션과 뷰는 다른 프로세스의 덮어쓰기·늘리기·바꿔 끼우기·삭제·배타적 열기를 막지 않고 크기 줄이기만 오류 1224 로 막으므로, 호출하는 쪽이 가변 파일을 미리 빼고 (점검이 크기·수정 시각이 바뀐 파일의 뷰를 닫음) 씁니다.
/// 읽기는 ReadProcessMemory (자기 프로세스) 로 해서 디스크 오류가 예외가 아니라 반환값으로 옵니다. 하드 폴트 판정 상수는 StandbyTouch 와 같습니다. 모든 메서드는 한 잠금 아래에서 돕니다.
public sealed unsafe class ViewHold : IKeepHold
{
    /// 파일마다 뷰 하나가 덮는 크기: 이보다 큰 파일은 이 크기의 창마다 뷰를 엽니다 (64 비트 주소 공간이라 파일 전체도 문제없지만 창을 나눠 두면 한 뷰가 덮는 범위가 정해짐).
    public const long WindowBytes = 1L << 30;

    private const long PageBytes = 4096;

    private readonly object _lock = new();
    private readonly Func<uint?> _hardFaults;
    private readonly Func<uint?> _pageReads;
    private readonly Func<string, uint> _attributes;
    private readonly Dictionary<string, FileEntry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Seg> _segs = []; // 붙든 구간 (붙든 순서 = 중요한 순서), 맨 뒤가 가장 덜 중요
    private nint _buffer;
    private uint? _faults;
    private uint? _pages;
    private long _held;

    private sealed class FileEntry(string path)
    {
        public string Path { get; } = path;
        public StandbyTouch.Section? Section;
        public long Length;
        public (long Size, long Ticks)? Stamp; // 처음 열 때의 크기·수정 시각
        public bool Dead; // 열 수 없어 붙들지 않음
        public bool Sparse; // 스파스라 붙들지 않음
        public readonly Dictionary<long, Window> Windows = [];
        public readonly List<(long Start, long End)> Claimed = []; // 이미 붙든 (또는 내놓은) 구간, 겹치지 않게 합쳐 정렬: 같은 구간을 두 번 붙들지 않고 내놓은 구간을 다시 붙들지 않음
    }

    private sealed class Window
    {
        public required FileEntry File { get; init; }
        public required long Index { get; init; }
        public required long Start { get; init; }
        public required long Length { get; init; }
        public nint View;
        public long Held;
    }

    private sealed class Seg
    {
        public required Window Window { get; init; }
        public required long Offset { get; init; }
        public long Length;
        public long Live; // 아직 붙들고 있는 바이트 (앞에서부터, 내놓을 때는 뒤에서부터 줄어듦)
    }

    private sealed class Tally
    {
        public long Bytes;
        public long Disk;
        public long Skipped;
        public long Faults;
        public long SinceCheck;
        public double SinceMs;
        public double SlowMs;
        public int Errors;
        public int Slow;
        public int Views;
        public int Checks;
        public bool Signal = true;
        public bool Boundary;
        public bool Canceled;
    }

    public ViewHold(Func<uint?>? hardFaults = null, Func<uint?>? pageReads = null, Func<string, uint>? attributes = null)
    {
        _hardFaults = hardFaults ?? ProcessFaults.Query;
        _pageReads = pageReads ?? SystemPageReads.Query;
        _attributes = attributes ?? FileGuards.FileAttributes;
    }

    public long HeldBytes
    {
        get
        {
            lock (_lock)
            {
                return _held;
            }
        }
    }

    public int Files
    {
        get
        {
            lock (_lock)
            {
                return _files.Values.Count(f => f.Windows.Count > 0);
            }
        }
    }

    public int Views
    {
        get
        {
            lock (_lock)
            {
                return _files.Values.Sum(f => f.Windows.Count);
            }
        }
    }

    /// 붙든 구간의 주소 범위 (시험이 작업 집합에 있는지 확인하는 데만 씀).
    internal IReadOnlyList<(nint Address, long Length)> LiveRanges()
    {
        lock (_lock)
        {
            return [.. _segs.Where(s => s.Live > 0).Select(s => (s.Window.View + (nint)(s.Offset - s.Window.Start), s.Live))];
        }
    }

    public void Rebase()
    {
        lock (_lock)
        {
            _ = _hardFaults(); // 첫 조회는 버립니다 (StandbyTouch.Run 과 같은 이유: 조회 버퍼와 코드가 다시 올라오며 나는 읽기가 기준값에 들어가지 않게)
            (_faults, _pages) = (_hardFaults(), _pageReads());
        }
    }

    public HoldBatchResult Hold(IReadOnlyList<ReadOp> batch, CancellationToken ct)
    {
        lock (_lock)
        {
            long started = Stopwatch.GetTimestamp();
            double cpuBefore = StandbyTouch.ThreadCpuSeconds();
            var tally = new Tally { Signal = _faults is not null };
            int boundary = -1;
            for (int i = 0; i < batch.Count && !tally.Boundary && !tally.Canceled; i++)
            {
                HoldRange(batch[i], tally, ct);
                if (tally.Boundary)
                {
                    boundary = i;
                }
            }

            if (!tally.Boundary && !tally.Canceled && tally.SinceCheck > 0)
            {
                Check(tally);
                boundary = tally.Boundary ? batch.Count - 1 : -1;
            }

            return new HoldBatchResult(tally.Bytes, tally.Disk, tally.Skipped, tally.Errors, tally.Slow, tally.Views, tally.Checks, tally.Faults, Stopwatch.GetElapsedTime(started).TotalSeconds, Math.Max(0, StandbyTouch.ThreadCpuSeconds() - cpuBefore), boundary);
        }
    }

    public long Release(long bytes)
    {
        lock (_lock)
        {
            long released = 0;
            for (int i = _segs.Count - 1; i >= 0 && released < bytes; i--)
            {
                Seg s = _segs[i];
                if (s.Live <= 0)
                {
                    continue;
                }

                released += Unlock(s, Math.Min(s.Live, bytes - released));
            }

            CompactSegs();
            return released;
        }
    }

    public HoldCheckResult Recheck(long refillStopBytes, CancellationToken ct)
    {
        lock (_lock)
        {
            long started = Stopwatch.GetTimestamp();
            double cpuBefore = StandbyTouch.ThreadCpuSeconds();
            List<string> changed = [];
            foreach (FileEntry f in _files.Values.Where(f => f.Windows.Count > 0).ToList())
            {
                if (Changed(f))
                {
                    changed.Add(f.Path);
                    foreach (Seg s in _segs.Where(s => s.Window.File == f && s.Live > 0).ToList())
                    {
                        _ = Unlock(s, s.Live, close: false);
                    }

                    CloseFile(f);
                }
            }

            CompactSegs();
            var tally = new Tally { Signal = _faults is not null };
            long dropped = 0;
            bool capReached = false;
            Seg? current = null;
            for (int i = 0; i < _segs.Count && !tally.Canceled && !capReached; i++)
            {
                current = _segs[i];
                if (!ReadHeld(current, tally, refillStopBytes, ct))
                {
                    capReached = tally.Disk > refillStopBytes;
                    dropped += Unlock(current, current.Live);
                }
            }

            if (!capReached && !tally.Canceled && tally.SinceCheck > 0)
            {
                Check(tally);
                if (tally.Disk > refillStopBytes && current is { Live: > 0 })
                {
                    capReached = true;
                    dropped += Unlock(current, current.Live);
                }
            }

            CompactSegs();
            return new HoldCheckResult(tally.Bytes, tally.Disk, dropped, capReached, changed, tally.Checks, tally.Faults, Stopwatch.GetElapsedTime(started).TotalSeconds, Math.Max(0, StandbyTouch.ThreadCpuSeconds() - cpuBefore));
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _ = ReleaseAllLocked();
            foreach (FileEntry f in _files.Values)
            {
                CloseFile(f);
            }

            _files.Clear();
            if (_buffer != 0)
            {
                NativeMemory.Free((void*)_buffer);
                _buffer = 0;
            }
        }
    }

    private long ReleaseAllLocked()
    {
        long released = 0;
        for (int i = _segs.Count - 1; i >= 0; i--)
        {
            if (_segs[i].Live > 0)
            {
                released += Unlock(_segs[i], _segs[i].Live);
            }
        }

        CompactSegs();
        return released;
    }

    // ---- 붙들기 ----

    // 범위 하나: 이미 붙든 구간을 뺀 나머지를 조각 (StandbyTouch.PieceBytes) 마다 읽어 붙듭니다.
    private void HoldRange(ReadOp range, Tally tally, CancellationToken ct)
    {
        if (range.Length <= 0)
        {
            return;
        }

        FileEntry? file = EntryFor(range, tally);
        if (file is null)
        {
            return;
        }

        if (range.Offset < 0 || range.Offset >= file.Length)
        {
            tally.Errors++;
            return;
        }

        long end = range.Offset + Math.Min(range.Length, file.Length - range.Offset);
        foreach ((long from, long to) in Uncovered(file.Claimed, range.Offset, end))
        {
            long pos = from;
            for (long n; pos < to && !tally.Boundary && !tally.Canceled && (n = HoldPiece(file, pos, to, tally, ct)) > 0; pos += n)
            {
            }

            Claim(file.Claimed, from, pos);
            if (pos < to)
            {
                break; // 경계를 만났거나 취소됐거나 읽지 못함: 이 범위의 나머지는 건너뜀
            }
        }
    }

    // 조각 하나를 읽어 구간에 더합니다. 읽은 바이트를 돌려주고, 읽지 못했거나 (매핑·읽기 실패) 취소됐으면 0.
    private long HoldPiece(FileEntry file, long pos, long to, Tally tally, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            tally.Canceled = true;
            return 0;
        }

        Window? window = WindowFor(file, pos, tally);
        if (window is null)
        {
            return 0;
        }

        long n = Math.Min(StandbyTouch.PieceBytes, Math.Min(to, window.Start + window.Length) - pos);
        long t0 = Stopwatch.GetTimestamp();
        EnsureBuffer();
        bool ok = NativeMethods.ReadProcessMemory(NativeMethods.GetCurrentProcess(), window.View + (nint)(pos - window.Start), (byte*)_buffer, (nuint)n, out nuint got) && (long)got == n;
        double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
        if (!ok)
        {
            tally.Errors++;
            tally.SlowMs += ms; // 망가진 디스크에서 범위마다 오래 실패해도 멈추게 실패한 시간도 셈
            tally.Boundary |= !tally.Signal && tally.SlowMs > StandbyTouch.SlowBudgetMs;
            return 0;
        }

        AddSeg(window, pos, n);
        AfterPiece(tally, n, ms);
        return n;
    }

    // 조각을 읽은 뒤의 셈과 판정 (붙들기·다시 읽기 공통): 느린 조각, 의심 조각, 읽기 수를 조회할 때.
    private void AfterPiece(Tally tally, long n, double ms)
    {
        tally.Bytes += n;
        tally.SinceCheck += n;
        tally.SinceMs += ms;
        if (ms > Math.Max(StandbyTouch.MinSlowMs, StandbyTouch.SlowMsPerMib * n / StandbyTouch.PieceBytes))
        {
            tally.Slow++;
            if (!tally.Signal)
            {
                tally.SlowMs += ms;
                tally.Boundary |= tally.SlowMs > StandbyTouch.SlowBudgetMs; // 읽기 수를 읽을 수 없을 때의 안전망: 느린 조각의 시간 합 (디스크 읽기와 CPU 선점을 가를 수 없음)
            }
        }

        if (!tally.Signal)
        {
            return;
        }

        bool suspect = ms > Math.Max(StandbyTouch.SuspectMinMs, StandbyTouch.SuspectMsPerMib * n / StandbyTouch.PieceBytes);
        if (tally.SinceCheck >= StandbyTouch.FaultCheckBytes || (suspect && tally.SinceCheck >= StandbyTouch.SuspectCheckBytes && tally.SinceMs >= StandbyTouch.SuspectCheckMinMs))
        {
            Check(tally);
        }
    }

    // 읽기 수를 조회해 앞의 조회 뒤 늘어난 만큼을 셈에 더하고, 경계 (허용치 초과) 나 읽은 양 한도에 닿았으면 멈춤 표시.
    private void Check(Tally tally)
    {
        tally.SinceCheck = 0;
        tally.SinceMs = 0;
        tally.Checks++;
        uint? now = _hardFaults();
        uint? pagesNow = _pageReads();
        if (now is not { } faultsNow || _faults is not { } before)
        {
            (tally.Signal, _faults, _pages) = (false, now, pagesNow);
            return;
        }

        uint delta = unchecked(faultsNow - before);
        long pages = _pages is { } pagesBefore && pagesNow is { } pagesAfter ? unchecked(pagesAfter - pagesBefore) : long.MaxValue / PageBytes;
        (_faults, _pages) = (faultsNow, pagesNow);
        tally.Faults += delta;
        if (delta > 0)
        {
            tally.Disk += Math.Min(pages * PageBytes, delta * StandbyTouch.FaultReadBytes);
        }

        if (delta > StandbyTouch.BoundaryFaults || tally.Disk > StandbyTouch.DiskReadStopBytes)
        {
            tally.Boundary = true;
        }
    }

    private FileEntry? EntryFor(ReadOp range, Tally tally)
    {
        if (!_files.TryGetValue(range.Path, out FileEntry? file))
        {
            _files[range.Path] = file = new FileEntry(range.Path);
            file.Sparse = FileGuards.IsSparse(range.Path, _attributes); // 스파스 파일은 새 섹션으로 만지면 캐시에 있어도 디스크에서 다시 읽힘
            if (file.Sparse)
            {
                tally.Skipped += range.Length;
                return null;
            }

            file.Stamp = Stat(range.Path);
            file.Section = StandbyTouch.Section.Open(range.Path, out _, out _);
            file.Dead = file.Section is null;
            file.Length = file.Section?.Length ?? 0;
        }

        if (file.Sparse)
        {
            tally.Skipped += range.Length;
            return null;
        }

        if (file.Dead)
        {
            tally.Errors++;
            return null;
        }

        if (file.Section is null)
        {
            file.Section = StandbyTouch.Section.Open(range.Path, out _, out _); // 내놓은 뒤 닫은 섹션: 같은 파일의 아직 붙들지 않은 범위가 남았을 때만 다시 엶
            if (file.Section is null)
            {
                tally.Errors++;
                return null;
            }
        }

        return file;
    }

    private Window? WindowFor(FileEntry file, long pos, Tally tally)
    {
        long index = pos / WindowBytes;
        if (file.Windows.TryGetValue(index, out Window? window))
        {
            return window.View == 0 ? null : window;
        }

        long start = index * WindowBytes;
        long length = Math.Min(WindowBytes, file.Length - start);
        nint view = file.Section!.Map(start, length);
        window = new Window { File = file, Index = index, Start = start, Length = length, View = view };
        file.Windows[index] = window;
        if (view == 0)
        {
            tally.Errors++;
            return null; // 이 창은 다시 시도하지 않음
        }

        tally.Views++;
        return window;
    }

    private void AddSeg(Window window, long offset, long length)
    {
        if (_segs.Count > 0 && _segs[^1] is { } last && last.Window == window && last.Offset + last.Length == offset && last.Live == last.Length)
        {
            last.Length += length;
            last.Live += length;
        }
        else
        {
            _segs.Add(new Seg { Window = window, Offset = offset, Length = length, Live = length });
        }

        window.Held += length;
        _held += length;
    }

    private void EnsureBuffer()
    {
        if (_buffer == 0)
        {
            _buffer = (nint)NativeMemory.Alloc(StandbyTouch.PieceBytes);
        }
    }

    // ---- 다시 읽기 ----

    // 구간 하나를 다시 읽습니다. 끝까지 읽었으면 true, 읽기 상한에 닿았거나 읽지 못했거나 취소됐으면 false (읽지 못한 구간은 호출한 쪽이 목록에서 뺌).
    private bool ReadHeld(Seg seg, Tally tally, long refillStopBytes, CancellationToken ct)
    {
        Window window = seg.Window;
        for (long done = 0; done < seg.Live;)
        {
            if (ct.IsCancellationRequested)
            {
                tally.Canceled = true;
                return true; // 취소는 구간을 빼지 않음
            }

            long n = Math.Min(StandbyTouch.PieceBytes, seg.Live - done);
            long t0 = Stopwatch.GetTimestamp();
            EnsureBuffer();
            bool ok = NativeMethods.ReadProcessMemory(NativeMethods.GetCurrentProcess(), window.View + (nint)(seg.Offset + done - window.Start), (byte*)_buffer, (nuint)n, out nuint got) && (long)got == n;
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (!ok)
            {
                tally.Errors++;
                return false; // 읽을 수 없는 구간 (파일이 줄었거나 디스크 오류): 붙든 목록에서 뺌
            }

            AfterPiece(tally, n, ms);
            tally.Boundary = false; // 다시 읽기는 경계를 찾지 않음: 디스크에서 다시 읽힌 양의 상한만 봄
            if (tally.Disk > refillStopBytes)
            {
                return false;
            }

            done += n;
        }

        return true;
    }

    // ---- 내놓기·닫기 ----

    // 구간의 뒤쪽 bytes 를 작업 집합에서 뺍니다 (VirtualUnlock: 잠그지 않은 범위라 FALSE 와 오류 158 이 정상). 뺀 바이트를 돌려줍니다. 뷰에 붙든 것이 남지 않으면 뷰를 닫습니다 (close).
    private long Unlock(Seg seg, long bytes, bool close = true)
    {
        long take = Math.Min(seg.Live, bytes);
        if (take <= 0)
        {
            return 0;
        }

        long start = seg.Window.View + (seg.Offset - seg.Window.Start) + (seg.Live - take);
        long from = (start + PageBytes - 1) & ~(PageBytes - 1); // 쪽 경계 안쪽만: 이웃 구간과 나눠 쓰는 가장자리 쪽은 건드리지 않음
        long to = (start + take) & ~(PageBytes - 1);
        if (to > from)
        {
            _ = NativeMethods.VirtualUnlock((nint)from, (nuint)(to - from));
        }

        seg.Live -= take;
        seg.Window.Held -= take;
        _held -= take;
        if (close && seg.Window.Held <= 0)
        {
            CloseWindow(seg.Window);
        }

        return take;
    }

    private void CompactSegs() => _ = _segs.RemoveAll(s => s.Live <= 0);

    private void CloseWindow(Window window)
    {
        if (window.View != 0)
        {
            _ = NativeMethods.UnmapViewOfFile(window.View);
            window.View = 0;
        }

        _ = window.File.Windows.Remove(window.Index);
        if (window.File.Windows.Count == 0)
        {
            window.File.Section?.Dispose();
            window.File.Section = null;
        }
    }

    // 파일의 뷰를 모두 닫고 이 파일의 구간을 목록에서 뺍니다 (구간은 호출한 쪽이 이미 내놓음).
    private void CloseFile(FileEntry file)
    {
        foreach (Window w in file.Windows.Values.ToList())
        {
            w.Held = 0;
            CloseWindow(w);
        }

        file.Section?.Dispose();
        file.Section = null;
    }

    // 처음 열 때와 크기·수정 시각이 다른가 (파일이 없어졌어도 바뀐 것으로 봄).
    private static bool Changed(FileEntry file) => file.Stamp is not { } stamp || Stat(file.Path) is not { } now || now != stamp;

    private static (long Size, long Ticks)? Stat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    // ---- 구간 계산 ----

    // [from, to) 에서 이미 붙든 구간을 뺀 나머지 (앞에서부터).
    private static List<(long From, long To)> Uncovered(List<(long Start, long End)> claimed, long from, long to)
    {
        List<(long, long)> result = [];
        long cursor = from;
        for (int i = FirstEndingAfter(claimed, from); i < claimed.Count && claimed[i].Start < to && cursor < to; i++)
        {
            if (claimed[i].Start > cursor)
            {
                result.Add((cursor, claimed[i].Start));
            }

            cursor = Math.Max(cursor, claimed[i].End);
        }

        if (cursor < to)
        {
            result.Add((cursor, to));
        }

        return result;
    }

    // [from, to) 를 붙든 구간에 더합니다 (맞닿거나 겹치는 구간과 합침).
    private static void Claim(List<(long Start, long End)> claimed, long from, long to)
    {
        if (to <= from)
        {
            return;
        }

        int first = FirstEndingAfter(claimed, from - 1); // 끝이 from 과 맞닿는 구간부터
        int last = first;
        long start = from;
        long end = to;
        while (last < claimed.Count && claimed[last].Start <= to)
        {
            (start, end) = (Math.Min(start, claimed[last].Start), Math.Max(end, claimed[last].End));
            last++;
        }

        claimed.RemoveRange(first, last - first);
        claimed.Insert(first, (start, end));
    }

    // 끝이 value 보다 큰 첫 구간의 위치 (구간이 합쳐 정렬돼 있어 끝도 정렬됨).
    private static int FirstEndingAfter(List<(long Start, long End)> claimed, long value)
    {
        int low = 0;
        int high = claimed.Count;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (claimed[mid].End > value)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
        }

        return low;
    }
}
