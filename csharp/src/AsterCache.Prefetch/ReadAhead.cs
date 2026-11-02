using AsterCache.Windows;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Prefetch;

/// 선행 읽기가 파일에 닿는 곳. 시험은 가짜로 바꿉니다. 실패는 IOException·UnauthorizedAccessException 으로 알립니다.
public interface IReadAheadIo : IDisposable
{
    /// [offset, offset + count) 를 읽어 (파일 캐시에 올려) 실제로 읽은 바이트 수를 돌려줍니다. 파일 끝이면 count 보다 작습니다.
    int Read(string path, long offset, int count);

    /// 낮은 I/O 우선순위 힌트를 붙이지 못한 핸들 수 (힌트는 실패해도 읽기는 계속함).
    int LowPriorityFailures => 0;
}

/// 실제 읽기: 읽기 전용, 공유 READ|WRITE|DELETE, 일반 버퍼드 읽기, 열 때마다 낮은 I/O 우선순위 힌트. 게임이 배타적으로 연 파일은 열기에서 실패합니다.
/// 선택 근거: 버퍼드 읽기는 한 번의 ReadFile 로 끝나 단순하고 읽은 쪽이 파일 캐시에 그대로 남습니다 (매핑 + PrefetchVirtualMemory 는 핸들·매핑 관리가 더 듦).
/// SequentialScan 힌트는 뒤쪽 페이지를 일찍 내보낼 수 있어 쓰지 않습니다. 마지막으로 읽은 파일 하나의 핸들만 들고 있습니다 (스레드 하나만 부름).
public sealed class FileReadAheadIo : IReadAheadIo
{
    private byte[] _buffer = new byte[ReadAhead.ChunkBytes]; // 스트림 모드의 큰 요청이 오면 한 번 키움
    private readonly IoPriorityHint _hint = new(_ => { }); // 실패는 LowPriorityFailures 로만 셈
    private string? _path;
    private SafeFileHandle? _handle;
    private int _lowFailures;

    public int LowPriorityFailures => Volatile.Read(ref _lowFailures);

    public int Read(string path, long offset, int count)
    {
        if (_handle is null || !string.Equals(_path, path, StringComparison.OrdinalIgnoreCase))
        {
            _handle?.Dispose();
            _handle = null;
            _handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.None);
            _path = path;
            if (!_hint.SetLow(_handle))
            {
                Interlocked.Increment(ref _lowFailures);
            }
        }

        if (_buffer.Length < count)
        {
            _buffer = new byte[count];
        }

        int total = 0;
        while (total < count)
        {
            int n = RandomAccess.Read(_handle, _buffer.AsSpan(total, count - total), offset + total);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    public void Dispose() => _handle?.Dispose();
}

/// 선행 읽기 설정. Roots 밖 파일은 무시, WindowBytes 는 게임이 읽은 곳부터 앞으로 읽을 길이, MaxTotalBytes 는 선행 읽기 총량 상한.
/// 가용 RAM (MiB) − 이번 요청 이 MinFreeRamMib 아래로 내려가면 RAM 이 돌아올 때까지 쉽니다. NowMs: 단조 증가 시계 (ms, 시험이 주입, 기본 Environment.TickCount64).
/// GapMs: (틈 모드) 게임의 마지막 읽기 알림 뒤 이만큼 조용해야 선행 읽기를 합니다 (0 이면 틈 규칙 없음). SeqRuns: 순차로 판정하는 데 필요한 연속 증가 읽기 횟수.
/// Stream: true (기본) 면 순차 판정된 파일은 틈을 기다리지 않고 StreamChunkBytes 단위로 앞쪽을 바로 읽으며 게임 읽기가 와도 멈추지 않음 (GapMs 무시). false 면 틈 전용.
/// Bounded: Stream 과 함께 켜면 (bounded 모드) 총량 상한이 게임 읽기 x 1.0 + 32 MiB 로 줄고, 게임 읽기 알림 뒤 BoundedQuietMs 동안은 새 요청을 내지 않음 (진행 중인 요청은 끝냄).
/// Adaptive: (adaptive 모드, 켜면 Stream·Bounded 도 켠 것으로 봄) bounded 규칙 위에 포화·경합 감지를 더함. 최근 SaturationWindowMs 창의 게임 읽기 처리량이 SaturationMbps (MiB/s) 이상이면 새 요청을 내지 않고,
/// 우리 요청의 지연이 직전 창 평균의 2 배 이상이면 ContentionPauseMs 쉽니다.
public sealed record ReadAheadOptions(
    IReadOnlyList<string> Roots, long WindowBytes, long MaxTotalBytes, long MinFreeRamMib, Func<long> AvailableRamMib, Func<long>? NowMs = null,
    long GapMs = ReadAhead.DefaultGapMs, int SeqRuns = ReadAhead.DefaultSeqRuns, bool Stream = true, bool Bounded = false, bool Adaptive = false, int SaturationMbps = ReadAhead.DefaultSaturationMbps);

/// 선행 읽기 통계. HitBytes: 게임이 읽은 바이트 중 우리가 그보다 먼저 선읽기를 끝낸 구간에 든 바이트 (적중 추정).
/// ResolvedReads: 읽는 순간 이름이 풀려 전달된 읽기, LateReads: 이름이 나중에 와서 늦게 전달된 읽기 (GameReadBytes·HitBytes 에는 넣지 않음).
/// SequentialFiles: 순차로 판정된 적이 있는 파일 수. GapCount: 선행 읽기를 낸 틈의 수, YieldedRequests: 틈 도중 게임 읽기가 와서 멈춘 횟수,
/// ReadAheadWhileIdleBytes: 게임 읽기 알림이 하나도 끼지 않은 요청으로 읽은 바이트 (ReadAheadBytes 와의 차이는 요청 도중 게임 읽기가 겹친 몫).
/// SaturatedMs: (adaptive) 포화 때문에 할 일이 있는데도 쉰 누적 ms, PausedRequests: (adaptive) 요청 지연이 급증해 쉰 횟수.
public readonly record struct ReadAheadStats(
    long ReadAheadBytes, long GameReadBytes, long HitBytes, long Events, int FailedFiles, long ResolvedReads, long LateReads, int SequentialFiles, int LowPriorityFailures,
    long GapCount = 0, long YieldedRequests = 0, long ReadAheadWhileIdleBytes = 0, long SaturatedMs = 0, long PausedRequests = 0)
{
    public double HitRatio => GameReadBytes == 0 ? 0 : (double)HitBytes / GameReadBytes;
}

/// 실시간 선행 읽기: 게임이 (파일, 오프셋, 길이) 를 읽었다는 알림을 받아, 그 뒤쪽 창을 미리 읽어 파일 캐시에 올립니다.
/// 규칙: 같은 파일에서 오프셋이 늘어나는 읽기가 연달아 SeqRuns 번 (직전 읽기 끝과의 간격 ≤ SequentialGapBytes) 이고 그 파일의 게임 읽기 누적이 SequentialMinBytes 이상이면 순차로 보고,
/// 읽기 알림마다 [offset, offset + 창) 중 아직 맡지 않은 부분을 큐에 넣습니다 (게임이 읽은 구간 자체는 읽지 않음). 역행·큰 간격 읽기가 오면 큐를 비우고, 연달아 2 번이면 순차 판정이 풀립니다
/// (풀린 뒤에는 처음부터 다시 판정). 열기 알림은 세기만 합니다 (파일을 통째로 읽지 않음).
/// 스트림 모드 (기본): 순차 파일의 [게임이 읽은 곳, + 창) 를 틈 없이 바로 읽고 게임 읽기가 와도 멈추지 않습니다.
/// 틈 모드: 게임의 마지막 읽기 알림 (대상 루트 밖 포함) 뒤 GapMs 이상 조용할 때만 요청을 내고, 알림이 오면 진행 중인 요청 하나만 끝낸 채 멈춥니다.
/// bounded 모드: 스트림처럼 순차 파일만 StreamChunkBytes 단위로 읽되, 게임의 마지막 읽기 알림 뒤 BoundedQuietMs 동안은 새 요청을 내지 않고 (진행 중 요청은 끝냄)
/// 총량은 min(MaxTotalBytes, 게임 읽기량 × 1.0 + BoundedSlackBytes) 를 넘지 않습니다.
/// 스트림·틈 모드의 선행 읽기 총량은 min(MaxTotalBytes, 게임 읽기량 × 1.5 + CapSlackBytes) 를 넘지 않습니다.
/// 가장 최근 알림이 온 파일의 요청이 앞서고, 한 파일 안에서는 오프셋 오름차순입니다. 늦게 온 이름의 읽기 (OnLateRead) 는 판단에는 쓰되 적중 계산에는 넣지 않습니다.
/// 작업자 스레드 하나가 ChunkBytes (스트림은 StreamChunkBytes) 단위 동기 읽기를 하나씩 합니다 (큐 깊이 1). 열기·읽기 실패는 그 파일을 실패로 두고 다시 시도하지 않습니다.
/// 알림 메서드는 ETW 처리 스레드에서 부르므로 짧게 끝나고 던지지 않습니다. 시험은 Start 대신 Step 을 직접 부를 수 있습니다.
public sealed class ReadAhead : IDisposable
{
    public const int ChunkBytes = 1 << 20;

    /// 스트림 모드 요청 크기. 게임 읽기와 선행 읽기가 한 디스크에서 1 MiB 씩 번갈아 나오면 전환마다 회전 지연이 약 7 ms 라 처리 시간이 2.1 배 (E7b 실측 1.96 배와 일치),
    /// 16 MiB 단위면 1.07 배라서 한 번의 버퍼드 읽기로 16 MiB 를 읽습니다 (매핑 + PrefetchVirtualMemory 는 더 복잡해서 쓰지 않음).
    public const int StreamChunkBytes = 16 << 20;
    public const long SequentialGapBytes = 4L << 20;
    public const int DefaultSeqRuns = 6;
    public const long DefaultGapMs = 100;
    public const long SequentialMinBytes = 8L << 20;
    public const long CapSlackBytes = 64L << 20;

    /// bounded 모드: 총량 = 게임 읽기 x 1.0 + 32 MiB, 게임 읽기 알림 뒤 50 ms 는 새 요청을 내지 않음.
    public const long BoundedSlackBytes = 32L << 20;
    public const long BoundedQuietMs = 50;
    public const long AutoMaxCapMib = 4096;

    /// adaptive 모드: 게임 읽기 처리량 창 500 ms. 기준은 HDD 순차 135~170 MB/s 의 약 50% 인 80 MiB/s (E7e: 포화 구간에서 선행이 게임을 늦춤).
    public const long SaturationWindowMs = 500;
    public const int DefaultSaturationMbps = 80;

    /// adaptive 모드: 우리 요청 지연이 직전 창 평균의 2 배 이상이면 200 ms 쉼.
    public const long ContentionPauseMs = 200;
    private const long ContentionFactor = 2;

    private static readonly TimeSpan IdleWait = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ThrottleWait = TimeSpan.FromMilliseconds(10);

    private readonly object _lock = new();
    private readonly ReadAheadOptions _options;
    private readonly IReadAheadIo _io;
    private readonly Func<long> _now;
    private readonly string[] _roots;
    private readonly Dictionary<string, FileState> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<FileState> _recent = new(); // 앞이 가장 최근
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private volatile bool _stop;
    private volatile bool _throttled;
    private long _readAhead;
    private long _gameRead;
    private long _hit;
    private long _events;
    private long _lastReadMs;
    private long _readSeq;
    private long _gaps;
    private long _yielded;
    private long _idleBytes;
    private bool _gapActive;
    private long _resolved;
    private long _late;
    private int _sequential;
    private int _failed;
    private readonly Queue<(long At, long Bytes)> _samples = new(); // adaptive: 최근 창의 게임 읽기 (ETW 읽기 알림 전부, 루트 밖 포함)
    private readonly Queue<(long At, long Ms)> _latencies = new(); // adaptive: 최근 창의 우리 요청 지연
    private long _sampleBytes;
    private long _latencySum;
    private long _pauseUntil;
    private long _satMark = -1;
    private long _saturatedMs;
    private long _paused;

    public ReadAhead(ReadAheadOptions options, IReadAheadIo io)
    {
        _options = options.Adaptive ? options with { Stream = true, Bounded = true } : options;
        _io = io;
        _now = options.NowMs ?? (() => Environment.TickCount64);
        _roots = [.. options.Roots.Select(r => r.TrimEnd('\\', '/'))];
    }

    /// 작업자 스레드가 예외로 끝났으면 그 예외.
    public Exception? Fault { get; private set; }

    public ReadAheadStats Stats
    {
        get
        {
            lock (_lock)
            {
                return new ReadAheadStats(_readAhead, _gameRead, _hit, _events, _failed, _resolved, _late, _sequential, _io.LowPriorityFailures, _gaps, _yielded, _idleBytes, _saturatedMs, _paused);
            }
        }
    }

    /// max_total_mib 설정값 → 바이트. 0 이하면 자동: min(물리 RAM / 4, 4096 MiB).
    public static long ResolveMaxBytes(int maxTotalMib, long physicalRamBytes) =>
        (maxTotalMib > 0 ? maxTotalMib : Math.Min(physicalRamBytes / 4 >> 20, AutoMaxCapMib)) << 20;

    /// 게임이 파일을 열었다 (세기만 함: 열었다고 읽지는 않음).
    public void OnOpen(string path)
    {
        if (!InRoot(path))
        {
            return;
        }

        lock (_lock)
        {
            _events++;
        }
    }

    /// 게임이 파일의 [offset, offset + length) 를 읽었다 (읽는 순간 이름이 풀린 읽기).
    public void OnRead(string path, long offset, long length) => Note(path, offset, length, late: false);

    /// 읽을 때는 이름을 몰라 보류됐다가 이름이 나중에 와서 늦게 전달된 읽기. 순차 판단과 큐에는 쓰지만 적중·게임 읽기량에는 넣지 않습니다.
    public void OnLateRead(string path, long offset, long length) => Note(path, offset, length, late: true);

    private void Note(string path, long offset, long length, bool late)
    {
        if (length <= 0 || offset < 0)
        {
            return;
        }

        bool inRoot = InRoot(path);
        lock (_lock)
        {
            _lastReadMs = _now(); // 루트 밖 읽기도 게임이 디스크를 쓰는 중이라는 뜻
            if (_options.Adaptive && !late)
            {
                _samples.Enqueue((_lastReadMs, length));
                _sampleBytes += length;
                TrimSamples(_lastReadMs);
            }

            _readSeq++;
            if (_gapActive)
            {
                _gapActive = false;
                _yielded++;
            }

            if (!inRoot)
            {
                return;
            }

            _events++;
            FileState f = Touch(path);
            if (late)
            {
                _late++;
            }
            else
            {
                _resolved++;
                _gameRead += length;
                _hit += f.Done.Overlap(offset, offset + length); // 우리가 먼저 끝낸 구간만 적중
            }

            f.Covered.Add(offset, offset + length);
            f.Pending.Remove(offset, offset + length); // 게임이 이미 읽었으니 큐에서 뺌
            Follow(f, offset, length);
        }

        _wake.Set();
    }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Loop) { IsBackground = true, Name = "ReadAhead", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// 요청 하나 (틈 모드 1 MiB, 스트림 모드 16 MiB 읽기) 를 처리합니다. 할 일이 없거나 (총량 상한, RAM 하한, 틈 모드에서 틈이 아님) 쉬어야 하면 false.
    public bool Step()
    {
        (FileState File, long Offset, int Count)? work;
        long seq;
        lock (_lock)
        {
            work = Next();
            seq = _readSeq;
            if (work is null)
            {
                _gapActive = false; // 할 일이 없거나 쉬는 중: 이어진 틈이 아님
            }
            else if (!_gapActive && (!_options.Stream || _options.Bounded)) // 스트림 모드는 틈이라는 개념이 없어 GapCount 를 세지 않음
            {
                _gapActive = true;
                _gaps++;
            }
        }

        if (work is not { } w)
        {
            return false;
        }

        long startMs = _now();
        int? got = Try(() => _io.Read(w.File.Path, w.Offset, w.Count));
        long endMs = _now();
        lock (_lock)
        {
            if (_options.Adaptive && got == w.Count && w.Count == StreamChunkBytes)
            {
                CheckContention(endMs, endMs - startMs); // 끝에서 잘린 요청은 짧아서 비교에서 뺌
            }

            if (got is not { } n)
            {
                Fail(w.File);
                return true;
            }

            _readAhead += n;
            if (_readSeq == seq)
            {
                _idleBytes += n; // 요청 도중 게임 읽기가 끼지 않음
            }

            w.File.Done.Add(w.Offset, w.Offset + n);
            if (n < w.Count)
            {
                w.File.Pending.Remove(w.Offset + n, long.MaxValue); // 파일 끝
            }
        }

        return true;
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _io.Dispose();
    }

    private void Loop()
    {
        try
        {
            while (!_stop)
            {
                if (!Step())
                {
                    _wake.WaitOne(_throttled ? ThrottleWait : IdleWait);
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Fault = e;
        }
    }

    // 잠금 안에서. 가장 최근 파일부터 가장 앞 오프셋 한 요청. 틈 모드에서는 게임 읽기가 GapMs 안에 있었으면 쉼.
    private (FileState File, long Offset, int Count)? Next()
    {
        _throttled = false;
        long satMark = _satMark; // 포화로 쉬는 시간은 바로 앞 확인 이후로만 이어서 셈
        _satMark = -1;
        bool bounded = _options.Bounded;
        long remaining = Math.Min(_options.MaxTotalBytes, bounded ? _gameRead + BoundedSlackBytes : (_gameRead * 3 / 2) + CapSlackBytes) - _readAhead;
        if (remaining <= 0)
        {
            return null;
        }

        bool stream = _options.Stream;
        int count = (int)Math.Min(stream ? StreamChunkBytes : ChunkBytes, remaining);
        foreach (FileState f in _recent)
        {
            if (f.Failed || f.Pending.First is not { } p)
            {
                continue;
            }

            if (_options.Adaptive)
            {
                long now = _now();
                bool saturated = IsSaturated(now);
                if (saturated)
                {
                    _saturatedMs += satMark >= 0 ? now - satMark : 0;
                    _satMark = now;
                }

                if (saturated || now < _pauseUntil)
                {
                    _throttled = true; // 포화이거나 경합으로 쉬는 중: 곧 다시 봄
                    return null;
                }
            }

            if ((!stream && _now() - _lastReadMs < _options.GapMs) || (bounded && _now() - _lastReadMs < BoundedQuietMs))
            {
                _throttled = true; // 게임이 방금 읽음: 틈이 날 때까지 쉼
                return null;
            }

            if (_options.AvailableRamMib() - (((long)count + (1 << 20) - 1) >> 20) < _options.MinFreeRamMib)
            {
                return null; // RAM 이 모자람: 쉬었다가 다시 봄
            }

            int n = (int)Math.Min(count, p.End - p.Start);
            f.Pending.Remove(p.Start, p.Start + n);
            return (f, p.Start, n);
        }

        return null;
    }

    // 잠금 안에서. now - 창 이하의 읽기 표본을 버림.
    private void TrimSamples(long now)
    {
        while (_samples.TryPeek(out (long At, long Bytes) s) && s.At <= now - SaturationWindowMs)
        {
            _sampleBytes -= s.Bytes;
            _samples.Dequeue();
        }
    }

    // 잠금 안에서. 최근 창의 게임 읽기 처리량 (바이트 / 창 시간) 이 기준 이상인가: bytes / 0.5 s >= mbps MiB/s.
    private bool IsSaturated(long now)
    {
        TrimSamples(now);
        return _sampleBytes * 1000 >= (long)_options.SaturationMbps * (1L << 20) * SaturationWindowMs;
    }

    // 잠금 안에서. 이번 요청 지연이 직전 창 (요청 끝 기준 500 ms) 평균의 2 배 이상이면 쉼. 시계가 거칠어 평균이 0 이면 판단하지 않음.
    private void CheckContention(long endMs, long ms)
    {
        while (_latencies.TryPeek(out (long At, long Ms) l) && l.At <= endMs - SaturationWindowMs)
        {
            _latencySum -= l.Ms;
            _latencies.Dequeue();
        }

        if (_latencies.Count > 0 && _latencySum > 0 && ms * _latencies.Count >= ContentionFactor * _latencySum)
        {
            _pauseUntil = endMs + ContentionPauseMs;
            _paused++;
        }

        _latencies.Enqueue((endMs, ms));
        _latencySum += ms;
    }

    // 잠금 안에서. 순차 판정을 갱신하고, 순차이면 [offset, offset + 창) 을 큐에 넣고 아니면 큐를 비움.
    private void Follow(FileState f, long offset, long length)
    {
        bool step = f.LastEnd >= 0 && offset > f.LastStart && offset - f.LastEnd <= SequentialGapBytes;
        f.LastStart = offset;
        f.LastEnd = offset + length;
        f.ReadBytes += length;
        if (!step)
        {
            f.Breaks++;
            if (!f.Sequential || f.Breaks >= 2)
            {
                f.Sequential = false;
                f.Run = 0;
            }

            while (f.Pending.First is { } p)
            {
                f.Covered.Remove(p.Start, p.End); // 큐에만 있던 구간은 맡지 않은 것으로 되돌림 (순차가 재개되면 다시 큐에 들어감)
                f.Pending.Remove(p.Start, p.End);
            }

            return;
        }

        f.Run++;
        f.Breaks = 0;
        if (!f.Sequential)
        {
            if (f.Run < _options.SeqRuns || f.ReadBytes < SequentialMinBytes)
            {
                return;
            }

            f.Sequential = true;
            if (!f.EverSequential)
            {
                f.EverSequential = true;
                _sequential++;
            }
        }

        Queue(f, offset, offset + _options.WindowBytes);
    }

    private static void Queue(FileState f, long start, long end)
    {
        if (f.Failed)
        {
            return;
        }

        foreach ((long s, long e) in f.Covered.Gaps(start, end))
        {
            f.Covered.Add(s, e);
            f.Pending.Add(s, e);
        }
    }

    private FileState Touch(string path)
    {
        if (!_files.TryGetValue(path, out FileState? f))
        {
            _files[path] = f = new FileState(path);
            f.Node = _recent.AddFirst(f);
        }
        else if (f.Node != _recent.First)
        {
            _recent.Remove(f.Node!);
            _recent.AddFirst(f.Node!);
        }

        return f;
    }

    private void Fail(FileState f)
    {
        if (!f.Failed)
        {
            f.Failed = true;
            f.Pending.Clear();
            _failed++;
        }
    }

    private bool InRoot(string path)
    {
        foreach (string r in _roots)
        {
            if (path.Length > r.Length && path[r.Length] == '\\' && path.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static T? Try<T>(Func<T> read) where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private sealed class FileState(string path)
    {
        public string Path { get; } = path;

        public LinkedListNode<FileState>? Node { get; set; }

        /// 큐에 넣었거나 읽었거나 게임이 읽은 모든 구간.
        public IntervalSet Covered { get; } = new();

        /// 큐에 있고 아직 시작하지 않은 구간.
        public IntervalSet Pending { get; } = new();

        /// 선행 읽기를 끝낸 구간.
        public IntervalSet Done { get; } = new();

        public long LastStart { get; set; }

        public long LastEnd { get; set; } = -1;

        /// 직전 읽기에 이어진 순차 읽기 횟수.
        public int Run { get; set; }

        /// 연달아 온 역행·큰 간격 읽기 횟수 (순차 판정 중에만 의미 있음).
        public int Breaks { get; set; }

        /// 지금 순차로 판정돼 있는가 / 판정된 적이 있는가.
        public bool Sequential { get; set; }

        public bool EverSequential { get; set; }

        /// 이 파일에서 알림이 온 읽기 바이트 누적 (늦은 알림 포함).
        public long ReadBytes { get; set; }

        public bool Failed { get; set; }
    }
}
