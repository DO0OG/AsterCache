using System.Diagnostics;
using System.Runtime.InteropServices;
using AsterCache.Core.Prefetch;
using AsterCache.Windows.Native;

namespace AsterCache.Windows;

/// 붙들기 한 배치의 결과: 붙든 바이트, 디스크에서 읽힌 것으로 본 바이트, 스파스라 건너뛴 바이트, 오류 수 (열기·매핑·읽기·파일 끝 밖), 느린 조각 수, 새로 연 뷰 수, 읽기 수를 조회한 횟수,
/// 이 프로세스의 디스크 읽기 수 (하드 폴트) 증가, 걸린 시간 (초), 쓴 CPU 시간 (초), 디스크 읽기가 섞여 경계로 보고 멈췄으면 그때 읽던 범위의 배치 안 위치 (그 뒤 범위는 읽지 않았음, 없으면 -1).
public readonly record struct HoldBatchResult(long Bytes, long DiskBytes, long SkippedBytes, int Errors, int SlowPieces, int Views, int Checks, long HardFaults, double Seconds, double CpuSeconds, int BoundaryIndex, long ImageBytes = 0, long SmallBytes = 0);

// ImageBytes: 실행 가능한 이미지 파일 (첫 두 바이트가 MZ) 이거나 읽지 못해 붙들지 않은 바이트, SmallBytes: MinFileBytes 보다 작은 파일이라 붙들지 않은 바이트.

/// 점검 한 번의 결과: 작업 집합에 있는지 확인한 바이트, 그중 메모리 관리자가 작업 집합에서 줄여 간 (Valid 가 아닌) 바이트, 그것을 다시 읽어 올리다 디스크에서 읽힌 바이트, 붙든 목록에서 뺀 바이트 (바뀐 파일의 몫은 제외),
/// 다시 읽기 상한에 닿아 멈췄는가, 크기·수정 시각이 바뀌어 뷰를 닫은 파일, 읽기 수를 조회한 횟수, 하드 폴트 증가, 걸린 시간 (초), 쓴 CPU 시간 (초),
/// 작업 집합 조회가 (세 번 시도하고도) 실패해 판단하지 못한 구간 수와 마지막 Win32 오류 번호.
public readonly record struct HoldCheckResult(long CheckedBytes, long TrimmedBytes, long RefilledBytes, long DroppedBytes, bool CapReached, IReadOnlyList<string> ChangedFiles, int Checks, long HardFaults, double Seconds, double CpuSeconds, int QueryFailures = 0, int QueryError = 0);

/// 게임이 쓰는 파일이 바뀌어 5 초 확인이 뷰를 닫은 기록: 파일과 그때 내놓은 바이트.
public readonly record struct WrittenRelease(string Path, long Bytes);

/// 게임이 실행되는 동안 유지 목록을 서비스의 매핑 뷰로 붙들어 두는 곳 (ViewHold 의 인터페이스, 시험은 가짜로 바꿉니다).
public interface IKeepHold : IDisposable
{
    /// 지금 붙든 바이트.
    long HeldBytes { get; }

    /// 뷰를 열어 둔 파일 수와 뷰 수.
    int Files { get; }

    int Views { get; }

    /// 게임이 플레이 중에 쓰는 파일 (MarkWritten 으로 알린 것) 에서 지금 붙든 바이트와 그 파일 수.
    long WrittenHeldBytes { get; }

    int WrittenFiles { get; }

    /// 게임이 쓰는 파일을 알립니다 (붙들기 전에 한 번): 이 파일들은 ReleaseChangedWritten 이 따로 자주 확인합니다.
    void MarkWritten(IReadOnlyCollection<string> paths);

    /// 게임이 쓰는 파일만 크기·수정 시각·생성 시각이 붙들 때와 다른지 봅니다 (파일을 열지 않는 속성 조회). 바뀌었거나 없어졌으면 그 파일의 뷰를 바로 닫고 (다른 파일은 그대로) 이 플레이에서는 다시 붙들지 않습니다.
    IReadOnlyList<WrittenRelease> ReleaseChangedWritten();

    /// 이 프로세스의 디스크 읽기 수 (하드 폴트) 기준값을 지금 값으로 다시 잡습니다: 붙들기·점검 한 회차를 시작할 때 부릅니다 (그 사이 다른 스레드가 낸 읽기가 이번 판정에 섞이지 않게).
    void Rebase();

    /// 범위들을 순서대로 읽어 (이미 붙든 구간은 건너뜀) 작업 집합에 올린 채 뷰를 닫지 않습니다. 디스크 읽기가 섞여 경계를 만나면 거기서 멈추고 그 위치를 돌려줍니다.
    HoldBatchResult Hold(IReadOnlyList<ReadOp> batch, CancellationToken ct);

    /// 붙든 것을 덜 중요한 쪽 (나중에 붙든 쪽) 부터 bytes 만큼 내놓고 (VirtualUnlock) 내놓은 바이트를 돌려줍니다. 내놓은 범위는 ReleasedRanges 로 다시 붙들 수 있습니다 (점검이 목록에서 뺀 구간은 아님).
    long Release(long bytes);

    /// Release 로 내놓은 뒤 아직 다시 붙들지 않은 범위를 중요한 순서로 maxBytes 까지 돌려줍니다 (이 플레이에서 바뀌었거나 닫은 파일은 뺌). Hold 로 붙들면 목록에서 빠집니다.
    IReadOnlyList<ReadOp> ReleasedRanges(long maxBytes);

    /// 다시 붙들지 않기로 한 범위 (메모리에 없던 범위 등): ReleasedRanges 에 더 나오지 않습니다.
    void GiveUp(IReadOnlyList<ReadOp> ranges);

    /// 바뀐 파일의 뷰를 닫고, 붙든 쪽이 작업 집합에 있는지 QueryWorkingSetEx (자기 프로세스, 접근 비트를 건드리지 않음) 로 보아 빠진 쪽만 다시 읽어 올립니다.
    /// 디스크에서 다시 읽힌 양이 refillStopBytes 를 넘으면 그 구간을 목록에서 빼고, 뒤의 빠진 쪽이 있는 구간도 읽지 않고 뺍니다.
    /// refill 이 거짓이면 (가용 메모리에 여유가 없음) 줄여 간 쪽을 다시 읽어 올리지 않고 그 구간을 붙든 목록에서 뺍니다.
    /// dropOnCap 이 거짓이면 (게임이 로딩 중이라 읽기 상한을 작게 준 점검) 상한에 닿아도 읽지 못한 구간을 붙든 목록에서 빼지 않고 그대로 둡니다 (읽을 수 없는 구간은 그래도 뺌).
    HoldCheckResult Recheck(long refillStopBytes, CancellationToken ct, bool refill = true, bool dropOnCap = true);
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
    private readonly HashSet<string> _written = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Seg> _segs = []; // 붙든 구간 (붙든 순서 = 중요한 순서), 맨 뒤가 가장 덜 중요
    private readonly List<ReadOp> _released = []; // Release 로 내놓은 범위 (내놓은 순서 = 덜 중요한 쪽부터): 다시 붙들었거나 포기한 부분은 Claimed 에 들어 있어 ReleasedRanges 가 거름
    private nint _buffer;
    private nint _query; // QueryWorkingSetEx 항목 버퍼
    private int _queryFailures; // 이번 점검에서 조회가 실패해 판단하지 못한 구간 수
    private int _queryError; // 그때의 마지막 Win32 오류
    private uint? _faults;
    private uint? _pages;
    private long _held;

    private sealed class FileEntry(string path)
    {
        public string Path { get; } = path;
        public StandbyTouch.Section? Section;
        public long Length;
        public (long Size, long Ticks, long Created)? Stamp; // 처음 열 때의 크기·수정 시각·생성 시각 (임시 파일을 이름만 바꿔 끼운 교체는 생성 시각이 달라짐)
        public bool Written; // 게임이 플레이 중에 쓰는 파일: 점검과 별개로 자주 확인함
        public bool Dead; // 열 수 없거나 (또는 바뀌어 닫아서) 붙들지 않음
        public bool Sparse; // 스파스라 붙들지 않음
        public bool Image; // 실행 가능한 이미지 (MZ) 이거나 읽지 못해 붙들지 않음
        public bool Small; // MinFileBytes 보다 작아 붙들지 않음
        public readonly Dictionary<long, Window> Windows = [];
        public readonly List<(long Start, long End)> Claimed = []; // 이미 붙든 (또는 다시 붙들지 않기로 한) 구간, 겹치지 않게 합쳐 정렬: 같은 구간을 두 번 붙들지 않음. Release 로 내놓은 구간은 여기서 빠져 ReleasedRanges 로 다시 붙들 수 있고, 점검이 뺀 구간·바뀐 파일·GiveUp 한 구간은 남아 다시 붙들지 않음
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
        public long Images;
        public long Smalls;
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

    /// 이보다 작은 파일은 붙들지 않습니다: 16 MiB. 색인·설정류 작은 파일은 붙들어 얻는 이득이 작고 (페이지 수 천 개 미만) 게임이 다시 쓰거나 줄일 가능성이 커서 (붙든 뷰가 있으면 크기 줄이기가 오류 1224 로 실패) 가변 표시가 아직 없어도 뺍니다.
    public const long MinFileBytes = 16L << 20;

    private readonly long _minFileBytes;

    public ViewHold(Func<uint?>? hardFaults = null, Func<uint?>? pageReads = null, Func<string, uint>? attributes = null, long minFileBytes = MinFileBytes)
    {
        _minFileBytes = minFileBytes;
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

    public long WrittenHeldBytes
    {
        get
        {
            lock (_lock)
            {
                return _segs.Where(s => s.Window.File.Written).Sum(s => s.Live);
            }
        }
    }

    public int WrittenFiles
    {
        get
        {
            lock (_lock)
            {
                return _files.Values.Count(f => f.Written && f.Windows.Count > 0);
            }
        }
    }

    public void MarkWritten(IReadOnlyCollection<string> paths)
    {
        lock (_lock)
        {
            _written.UnionWith(paths);
        }
    }

    public IReadOnlyList<WrittenRelease> ReleaseChangedWritten()
    {
        lock (_lock)
        {
            List<WrittenRelease> released = [];
            foreach (FileEntry f in _files.Values.Where(f => f.Written && f.Windows.Count > 0).ToList())
            {
                if (Changed(f))
                {
                    released.Add(new WrittenRelease(f.Path, CloseChanged(f)));
                }
            }

            if (released.Count > 0)
            {
                CompactSegs();
            }

            return released;
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

            return new HoldBatchResult(tally.Bytes, tally.Disk, tally.Skipped, tally.Errors, tally.Slow, tally.Views, tally.Checks, tally.Faults, Stopwatch.GetElapsedTime(started).TotalSeconds, Math.Max(0, StandbyTouch.ThreadCpuSeconds() - cpuBefore), boundary, tally.Images, tally.Smalls);
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

                long end = s.Offset + s.Live;
                long took = Unlock(s, Math.Min(s.Live, bytes - released));
                released += took;
                NoteReleased(s.Window.File, end - took, took);
            }

            CompactSegs();
            return released;
        }
    }

    // 내놓은 범위를 기억하고 붙든 구간 표시에서 뺍니다 (그래야 같은 범위를 다시 붙들 수 있음).
    private void NoteReleased(FileEntry file, long from, long length)
    {
        if (length > 0)
        {
            RangeSets.Remove(file.Claimed, from, from + length);
            _released.Add(new ReadOp(file.Path, from, length));
        }
    }

    public IReadOnlyList<ReadOp> ReleasedRanges(long maxBytes)
    {
        lock (_lock)
        {
            List<ReadOp> pending = [];
            Dictionary<FileEntry, bool> usable = [];
            long total = 0;
            bool Usable(FileEntry f)
            {
                if (!usable.TryGetValue(f, out bool ok))
                {
                    usable[f] = ok = !f.Dead && !Changed(f); // 파일마다 속성 조회는 한 번
                }

                return ok;
            }

            for (int i = _released.Count - 1; i >= 0; i--) // 나중에 내놓은 쪽이 더 중요함
            {
                ReadOp op = _released[i];
                if (!_files.TryGetValue(op.Path, out FileEntry? file) || !Usable(file))
                {
                    _released.RemoveAt(i); // 바뀌었거나 닫은 파일: 다시 붙들지 않음
                    continue;
                }

                List<(long From, long To)> left = RangeSets.Uncovered(file.Claimed, op.Offset, op.Offset + op.Length);
                if (left.Count == 0)
                {
                    _released.RemoveAt(i); // 모두 다시 붙들었거나 포기함
                    continue;
                }

                foreach ((long from, long to) in left)
                {
                    if (total < maxBytes)
                    {
                        long n = Math.Min(to - from, maxBytes - total);
                        pending.Add(new ReadOp(op.Path, from, n));
                        total += n;
                    }
                }
            }

            return pending;
        }
    }

    public void GiveUp(IReadOnlyList<ReadOp> ranges)
    {
        lock (_lock)
        {
            foreach (ReadOp op in ranges)
            {
                if (_files.TryGetValue(op.Path, out FileEntry? file))
                {
                    RangeSets.Add(file.Claimed, op.Offset, op.Offset + op.Length);
                }
            }
        }
    }

    public HoldCheckResult Recheck(long refillStopBytes, CancellationToken ct, bool refill = true, bool dropOnCap = true)
    {
        lock (_lock)
        {
            long started = Stopwatch.GetTimestamp();
            double cpuBefore = StandbyTouch.ThreadCpuSeconds();
            List<string> changed = [];
            (_queryFailures, _queryError) = (0, 0);
            foreach (FileEntry f in _files.Values.Where(f => f.Windows.Count > 0).ToList())
            {
                if (Changed(f))
                {
                    changed.Add(f.Path);
                    _ = CloseChanged(f);
                }
            }

            CompactSegs();
            var tally = new Tally { Signal = _faults is not null };
            long queried = 0;
            long trimmed = 0;
            long dropped = 0;
            bool capReached = false;
            Seg? lastRead = null;
            for (int i = 0; i < _segs.Count && !tally.Canceled; i++)
            {
                Seg seg = _segs[i];
                if (seg.Live <= 0)
                {
                    continue;
                }

                List<(nint Address, long Length)> missing = NotResident(seg, ref queried);
                trimmed += missing.Sum(m => m.Length);
                if (missing.Count == 0)
                {
                    continue;
                }

                if (capReached && !dropOnCap)
                {
                    continue; // 읽기 상한에 닿은 뒤: 남은 구간은 이번에 되찾지 않되 붙들기에서 빼지 않음 (다음 점검이 이어 되찾음)
                }

                if (capReached || !refill)
                {
                    dropped += Unlock(seg, seg.Live); // 읽기 상한에 닿은 뒤이거나 가용 메모리에 여유가 없음: 줄여 간 쪽이 있는 구간은 디스크로 되찾지 않고 목록에서 뺌
                    continue;
                }

                lastRead = seg;
                bool failed = false;
                foreach ((nint address, long length) in missing)
                {
                    if (!ReadRange(address, length, tally, refillStopBytes, ct, out failed) || failed)
                    {
                        break;
                    }
                }

                if (failed || tally.Disk > refillStopBytes)
                {
                    capReached = tally.Disk > refillStopBytes;
                    if (failed || dropOnCap)
                    {
                        dropped += Unlock(seg, seg.Live); // 읽을 수 없는 구간이거나 상한을 넘긴 구간 (dropOnCap 이 거짓이면 상한만 넘긴 구간은 그대로 둠)
                    }
                }
            }

            if (!capReached && !tally.Canceled && tally.SinceCheck > 0)
            {
                Check(tally);
                if (tally.Disk > refillStopBytes && lastRead is { Live: > 0 })
                {
                    capReached = true;
                    if (dropOnCap)
                    {
                        dropped += Unlock(lastRead, lastRead.Live);
                    }
                }
            }

            CompactSegs();
            return new HoldCheckResult(queried, trimmed, tally.Disk, dropped, capReached, changed, tally.Checks, tally.Faults, Stopwatch.GetElapsedTime(started).TotalSeconds, Math.Max(0, StandbyTouch.ThreadCpuSeconds() - cpuBefore), _queryFailures, _queryError);
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

            if (_query != 0)
            {
                NativeMemory.Free((void*)_query);
                _query = 0;
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
        foreach ((long from, long to) in RangeSets.Uncovered(file.Claimed, range.Offset, end))
        {
            long pos = from;
            for (long n; pos < to && !tally.Boundary && !tally.Canceled && (n = HoldPiece(file, pos, to, tally, ct)) > 0; pos += n)
            {
            }

            RangeSets.Add(file.Claimed, from, pos);
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

            file.Written = _written.Contains(range.Path);
            file.Stamp = Stat(range.Path);
            if (file.Stamp is { Size: var size } && size < _minFileBytes)
            {
                file.Small = true;
                tally.Smalls += range.Length;
                return null;
            }

            switch (IsExecutableImage(range.Path))
            {
                case true: // 실행 가능한 이미지 (예: 실행 파일, 드라이버): 데이터 섹션으로 붙들지 않음
                    file.Image = true;
                    tally.Images += range.Length;
                    return null;
                case null: // 읽지 못함: 붙들지 않는 쪽으로
                    file.Dead = true;
                    tally.Errors++;
                    return null;
            }

            file.Section = StandbyTouch.Section.Open(range.Path, out _, out _);
            file.Dead = file.Section is null;
            file.Length = file.Section?.Length ?? 0;
        }

        if (file.Sparse)
        {
            tally.Skipped += range.Length;
            return null;
        }

        if (file.Small || file.Image)
        {
            if (file.Small)
            {
                tally.Smalls += range.Length;
            }
            else
            {
                tally.Images += range.Length;
            }

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

    // 파일의 첫 두 바이트가 MZ 인가 (확장자가 아니라 내용으로 판정). 읽지 못하면 null.
    private static bool? IsExecutableImage(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            Span<byte> head = stackalloc byte[2];
            return stream.Read(head) == 2 && head[0] == (byte)'M' && head[1] == (byte)'Z';
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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

    // 한 번에 QueryWorkingSetEx 로 묻는 쪽 수 (항목이 쪽마다 16 바이트라 256 KiB 버퍼).
    private const int QueryPages = 16384;

    // QueryWorkingSetEx 를 최대 세 번 부릅니다 (자기 프로세스의 의사 핸들이라 핸들 수명 문제는 없음; 문서에 간헐 실패 사유는 없고 항목 수가 크기 한도 안이므로 ERROR_BAD_LENGTH 는 나지 않음).
    // 모두 실패하면 마지막 Win32 오류를 남기고 false: 호출한 쪽은 그 구간을 판단하지 못한 것으로 둡니다.
    private bool Query(nuint* info, int pages)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (NativeMethods.QueryWorkingSetEx(NativeMethods.GetCurrentProcess(), info, (uint)(pages * 16)))
            {
                return true;
            }

            _queryError = Marshal.GetLastWin32Error();
            Thread.Yield();
        }

        _queryFailures++;
        return false;
    }

    // 구간에서 작업 집합에 없는 (Valid 가 아닌) 쪽을 이어진 범위로 묶어 돌려줍니다: 붙든 구간의 쪽 경계 안쪽만 묻습니다. 쪽 단위로 묻는 비용은 붙든 양에 비례하며 (1 GiB 가 26 만 쪽) 아직 실제 양으로 재지 않았습니다.
    // 조회할 수 없으면 (호출 실패) 빈 목록: 그 구간은 확인하지 못한 것으로 두고 건드리지 않습니다.
    private List<(nint Address, long Length)> NotResident(Seg seg, ref long queried)
    {
        List<(nint, long)> runs = [];
        long begin = seg.Window.View + (seg.Offset - seg.Window.Start);
        long from = (begin + PageBytes - 1) & ~(PageBytes - 1);
        long to = (begin + seg.Live) & ~(PageBytes - 1);
        if (_query == 0)
        {
            _query = (nint)NativeMemory.Alloc((nuint)QueryPages * 16);
        }

        nuint* info = (nuint*)_query;
        for (long at = from; at < to;)
        {
            int pages = (int)Math.Min(QueryPages, (to - at) / PageBytes);
            for (int i = 0; i < pages; i++)
            {
                info[2 * i] = (nuint)(at + (i * PageBytes));
            }

            if (!Query(info, pages))
            {
                return []; // 조회 실패: 이 구간은 판단하지 못한 것으로 두고 줄어든 것으로 보지 않음 (다시 읽지도, 목록에서 빼지도 않음)
            }

            queried += pages * PageBytes;
            for (int i = 0; i < pages; i++)
            {
                if ((info[(2 * i) + 1] & 1) != 0)
                {
                    continue; // Valid: 작업 집합에 있음
                }

                long address = at + (i * PageBytes);
                if (runs.Count > 0 && runs[^1].Item1 + runs[^1].Item2 == address)
                {
                    runs[^1] = (runs[^1].Item1, runs[^1].Item2 + PageBytes);
                }
                else
                {
                    runs.Add(((nint)address, PageBytes));
                }
            }

            at += pages * PageBytes;
        }

        return runs;
    }

    // 주소 범위를 1 MiB 씩 읽어 작업 집합에 다시 올립니다 (자기 프로세스, 디스크 오류는 반환값으로 옵니다). 끝까지 읽었으면 true. 읽을 수 없으면 failed, 읽기 상한을 넘기거나 취소되면 false.
    private bool ReadRange(nint address, long length, Tally tally, long refillStopBytes, CancellationToken ct, out bool failed)
    {
        failed = false;
        for (long done = 0; done < length;)
        {
            if (ct.IsCancellationRequested)
            {
                tally.Canceled = true;
                return false;
            }

            long n = Math.Min(StandbyTouch.PieceBytes, length - done);
            long t0 = Stopwatch.GetTimestamp();
            EnsureBuffer();
            bool ok = NativeMethods.ReadProcessMemory(NativeMethods.GetCurrentProcess(), address + (nint)done, (byte*)_buffer, (nuint)n, out nuint got) && (long)got == n;
            double ms = Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
            if (!ok)
            {
                tally.Errors++;
                failed = true; // 읽을 수 없는 구간 (파일이 줄었거나 디스크 오류)
                return false;
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

    // 바뀐 파일의 붙든 구간을 모두 내놓고 뷰를 닫습니다 (이 플레이에서 다시 붙들지 않음). 내놓은 바이트를 돌려줍니다.
    private long CloseChanged(FileEntry file)
    {
        long released = 0;
        foreach (Seg s in _segs.Where(s => s.Window.File == file && s.Live > 0).ToList())
        {
            released += Unlock(s, s.Live, close: false);
        }

        CloseFile(file);
        file.Dead = true;
        return released;
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

    // 처음 열 때와 크기·수정 시각·생성 시각이 다른가 (파일이 없어졌어도 바뀐 것으로 봄).
    private static bool Changed(FileEntry file) => file.Stamp is not { } stamp || Stat(file.Path) is not { } now || now != stamp;

    private static (long Size, long Ticks, long Created)? Stat(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.Length, info.LastWriteTimeUtc.Ticks, info.CreationTimeUtc.Ticks) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
