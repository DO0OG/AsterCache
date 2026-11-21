using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;

namespace AsterCache.Service;

/// 시작 뒤 한 초 (`Second`, 시작 틱 기준 정수 초) 동안의 프레임 요약: 프레임 수, 가장 긴 Present 간격 (ms), 50 ms·100 ms 를 넘은 간격 수.
/// 1 초 이상 걸린 간격은 끊김이 아니라 멈춤 (`Pauses`, 가장 긴 것 `PauseMaxMs`) 으로 따로 셉니다.
internal readonly record struct FrameSecond(long Second, int Frames, double MaxMs, int Over50Ms, int Over100Ms, int Pauses = 0, double PauseMaxMs = 0);

/// 게임 프로세스의 프레임 간격 표본 공급원. 프레임 시각을 얻는 방법 (ETW) 과 집계를 분리해 시험이 가짜로 바꿀 수 있게 합니다.
internal interface IFrameSource : IDisposable
{
    /// 이 프로세스 번호의 이벤트만 셉니다. 서비스가 이미 아는 값만 넘깁니다 (프로세스를 열지 않음).
    void AddProcess(int pid);

    /// `throughSecond` 이하의 확정된 초를 한 번씩만 돌려줍니다 (프레임이 없던 초는 없음). 표본 루프 스레드에서만 부릅니다.
    IReadOnlyList<FrameSecond> TakeSettled(long throughSecond);

    /// 게임 프로세스에서 받은 Present 이벤트 수 (0 이면 이벤트가 안 오는 게임: 0 프레임과 구분함).
    long AcceptedEvents { get; }

    /// 세션이 버퍼 부족으로 잃은 이벤트 수.
    long EventsLost { get; }

    /// 확정된 초에 늦게 도착해 버린 이벤트 수 (그 이벤트가 끝내는 간격은 기록에서 빠짐).
    long EventsLate { get; }

    /// 같은 프로세스의 더 늦은 시각 이벤트보다 뒤에 도착한 이벤트 수 (간격은 이벤트 시각 순서로 셈).
    long EventsOutOfOrder { get; }
}

/// Present 시작 이벤트의 (프로세스, 시각) 을 확정 때까지 모아 두었다가, 확정할 때 프로세스별로 이벤트 시각 순서로 정렬해 이웃한 이벤트 사이의 간격을 셉니다 (도착 순서가 아님).
/// 이벤트 처리 스레드와 표본 루프 스레드가 함께 쓰며, 처리 경로 (`Add`) 는 고정 크기 배열에 쓰기만 하고 메모리를 할당하지 않습니다. 정렬과 집계는 표본 루프 스레드의 `Take` 에서 합니다.
internal sealed class FrameBuckets(long originTick, long frequency, long startSecond = 0)
{
    public const int MaxProcesses = 16;   // shortcut: 한 게임이 16 개를 넘는 실행 파일을 띄우면 나머지는 세지 않음, 필요해지면 늘림
    public const int MaxPending = 65536;  // 확정 전에 쥐는 이벤트 수 (300 fps 한 프로세스로 약 3.6 분어치): 넘치면 늦은 이벤트처럼 버림
    public const int MaxAheadSeconds = 64; // 아직 가져가지 않은 가장 이른 초보다 이만큼 이상 앞선 시각은 시계 이상으로 보고 버림

    /// 이 이상 걸린 간격은 프레임 간격이 아니라 Present 가 멈춘 것 (최소화·창 가림·로딩 화면·응답 없음) 으로 봅니다.
    /// 30 fps 에서도 정상 간격은 33 ms 이므로 1 초는 정상 프레임 간격이 아니며, 끊김 수 (50 ms·100 ms 초과) 와 구간의 가장 긴 간격에는 넣지 않고 따로 셉니다.
    public const double PauseSeconds = 1;

    private readonly object _gate = new();
    private readonly int[] _pids = new int[MaxProcesses];
    private readonly long[] _lastArrival = new long[MaxProcesses];
    private readonly long[] _lastSettled = new long[MaxProcesses];
    private readonly long[] _times = new long[MaxPending];
    private readonly byte[] _procs = new byte[MaxPending];
    private readonly long _pauseTicks = (long)(PauseSeconds * frequency);
    private readonly long _over50Ticks = frequency * 50 / 1000;
    private readonly long _over100Ticks = frequency * 100 / 1000;
    private int _count;
    private int _pending;
    private long _next = startSecond; // 아직 가져가지 않은 가장 이른 초 (다시 열었으면 그 시각의 초부터: 닫혀 있던 때의 초를 늦은 이벤트로 세지 않음)
    private long _accepted;
    private long _late;
    private long _outOfOrder;

    private sealed class Totals
    {
        public int Frames;
        public long MaxTicks;
        public int Over50;
        public int Over100;
        public int Pauses;
        public long PauseMaxTicks;
    }

    public long Accepted { get { lock (_gate) { return _accepted; } } }

    /// 이미 가져간 초에 늦게 도착해 버린 이벤트 수.
    public long Late { get { lock (_gate) { return _late; } } }

    /// 같은 프로세스의 더 늦은 시각 이벤트보다 뒤에 도착한 이벤트 수.
    public long OutOfOrder { get { lock (_gate) { return _outOfOrder; } } }

    public void AddProcess(int pid)
    {
        lock (_gate)
        {
            if (_count < MaxProcesses && Array.IndexOf(_pids, pid, 0, _count) < 0)
            {
                _pids[_count++] = pid;
            }
        }
    }

    /// 게임 프로세스의 이벤트면 true (다른 프로세스는 버림).
    public bool Add(int pid, long qpc)
    {
        lock (_gate)
        {
            int index = Array.IndexOf(_pids, pid, 0, _count);
            if (index < 0)
            {
                return false;
            }

            _accepted++;
            if (qpc < _lastArrival[index])
            {
                _outOfOrder++;
            }
            else
            {
                _lastArrival[index] = qpc;
            }

            long relative = qpc - originTick;
            long second = relative / frequency;
            if (relative < 0 || second < _next || second - _next >= MaxAheadSeconds || _pending == MaxPending)
            {
                _late++;
                return true;
            }

            _times[_pending] = qpc;
            _procs[_pending] = (byte)index;
            _pending++;
            return true;
        }
    }

    /// `throughSecond` 이하의 이벤트를 확정해 초별 요약으로 돌려줍니다 (한 번 가져간 초는 다시 나오지 않음).
    public List<FrameSecond> Take(long throughSecond)
    {
        List<(byte Proc, long Qpc)> taken = [];
        lock (_gate)
        {
            long limit = throughSecond == long.MaxValue ? long.MaxValue : (throughSecond + 1) * frequency + originTick;
            int kept = 0;
            for (int i = 0; i < _pending; i++)
            {
                if (_times[i] < limit)
                {
                    taken.Add((_procs[i], _times[i]));
                }
                else
                {
                    _times[kept] = _times[i];
                    _procs[kept++] = _procs[i];
                }
            }

            _pending = kept;
            if (throughSecond != long.MaxValue)
            {
                _next = Math.Max(_next, throughSecond + 1);
            }

            taken.Sort((a, b) => a.Proc != b.Proc ? a.Proc.CompareTo(b.Proc) : a.Qpc.CompareTo(b.Qpc));
            SortedDictionary<long, Totals> seconds = [];
            long previous = 0;
            int previousProc = -1;
            foreach ((byte proc, long qpc) in taken)
            {
                if (proc != previousProc)
                {
                    previous = _lastSettled[proc]; // 이전 확정까지의 마지막 이벤트: 확정 경계를 건너는 간격도 셈
                    previousProc = proc;
                }

                long second = (qpc - originTick) / frequency;
                if (!seconds.TryGetValue(second, out Totals? t))
                {
                    seconds[second] = t = new Totals();
                }

                t.Frames++;
                if (previous != 0)
                {
                    long delta = qpc - previous;
                    if (delta >= _pauseTicks)
                    {
                        t.Pauses++;
                        t.PauseMaxTicks = Math.Max(t.PauseMaxTicks, delta);
                    }
                    else
                    {
                        t.MaxTicks = Math.Max(t.MaxTicks, delta);
                        t.Over50 += delta > _over50Ticks ? 1 : 0;
                        t.Over100 += delta > _over100Ticks ? 1 : 0;
                    }
                }

                previous = qpc;
                _lastSettled[proc] = qpc;
            }

            return [.. seconds.Select(s => new FrameSecond(s.Key, s.Value.Frames, s.Value.MaxTicks * 1000.0 / frequency, s.Value.Over50, s.Value.Over100, s.Value.Pauses, s.Value.PauseMaxTicks * 1000.0 / frequency))];
        }
    }
}

/// `Microsoft-Windows-DXGI` 의 Present 시작 이벤트 (번호 42) 만 받는 일반 ETW 실시간 세션 하나. 게임·런처·안티치트 프로세스를 열지 않고, 후킹도 없습니다.
/// 커널 로거는 쓰지 않고 세션 이름은 서비스 전용입니다. 같은 이름의 세션이 서비스 비정상 종료로 남아 있으면 새로 만들 때 TraceEventSession 이 치웁니다 (EtwProcessWatcher 와 같음).
/// 이벤트 머리의 프로세스 번호와 QPC 시각만 읽고 페이로드는 풀지 않습니다. 이벤트 시각은 Stopwatch 틱과 같은 시계라는 가정이며, `--frame-test` 가 이를 실측합니다.
internal sealed class FrameTimeSession : IFrameSource
{
    public const string SessionName = "AsterCache-FrameTimes";

    /// 진단 (`--frame-test`) 이 쓰는 이름: 서비스 세션 (`SessionName` 과 칸 번호가 붙은 이름) 을 건드리지 않게 다름.
    public const string TestSessionName = "AsterCache-FrameTimes-Test";
    public const int MaxSlots = 4;

    /// 세션 버퍼를 이 간격으로 내려 보냅니다 (확정에 몇 초를 기다리므로 EtwProcessWatcher 의 50 ms 만큼 촘촘할 필요가 없음).
    public static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(500);

    private static readonly Guid Dxgi = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private const ulong DxgiKeywords = 0x8000000000000002;
    private const int PresentStartId = 42;
    private const int BufferSizeMb = 4;

    private readonly TraceEventSession _session;
    private readonly FrameBuckets _buckets;
    private readonly string _name;
    private readonly Timer _flusher;
    private readonly Task _pump;
    private readonly ConcurrentDictionary<int, long>? _diagnostics;
    private readonly ConcurrentDictionary<int, long> _ids = new();
    private long _lost;
    private long _firstDeltaTicks = long.MinValue;
    private long _lastDeltaTicks = long.MinValue;
    private long _maxLagTicks;
    private int _disposed;

    private FrameTimeSession(string name, long originTick, bool diagnostic)
    {
        _name = name;
        _buckets = new FrameBuckets(originTick, Stopwatch.Frequency, Math.Max(0, (Stopwatch.GetTimestamp() - originTick) / Stopwatch.Frequency - 1));
        _diagnostics = diagnostic ? new ConcurrentDictionary<int, long>() : null;
        _session = new TraceEventSession(name) { BufferSizeMB = BufferSizeMb };
        try
        {
            var options = new TraceEventProviderOptions { EventIDsToEnable = [PresentStartId] };
            _session.EnableProvider(Dxgi, TraceEventLevel.Verbose, DxgiKeywords, options);
            _session.Source.AllEvents += OnEvent;
            _flusher = new Timer(_ => _ = EtwFlush.Flush(name), null, FlushInterval, FlushInterval);
            _pump = Task.Run(_session.Source.Process);
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    /// 세션을 엽니다. 권한이나 세션 수 한도 때문에 못 열면 예외 (호출자가 프레임 항목만 "unavailable" 로 둠). slot 은 동시에 도는 게임마다 다른 이름을 줍니다.
    public static FrameTimeSession Open(int slot, long originTick) => new(NameOf(slot), originTick, diagnostic: false);

    /// 진단용: 모든 프로세스의 이벤트를 프로세스 번호별로 셉니다 (`--frame-test`, 서비스와 다른 세션 이름).
    public static FrameTimeSession OpenDiagnostic(long originTick) => new(TestSessionName, originTick, diagnostic: true);

    /// 진단용: 한 프로세스를 서비스가 쓰는 경로로 집계합니다 (`--frame-test <초> <pid>`, 서비스와 다른 세션 이름).
    public static FrameTimeSession OpenTest(long originTick) => new(TestSessionName, originTick, diagnostic: false);

    public static string NameOf(int slot) => slot == 0 ? SessionName : $"{SessionName}-{slot}";

    /// 서비스 시작 때: 비정상 종료로 남은 세션을 치웁니다 (없으면 아무것도 안 함).
    public static void StopStale()
    {
        for (int slot = 0; slot < MaxSlots; slot++)
        {
            try
            {
                if (TraceEventSession.GetActiveSession(NameOf(slot)) is { } stale)
                {
                    stale.Stop();
                    stale.Dispose();
                }
            }
            catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
            {
                // 치우지 못해도 서비스 시작을 막지 않음: 다음 세션 생성이 같은 이름을 다시 치움
            }
        }
    }

    public long AcceptedEvents => _buckets.Accepted;

    public long EventsLost => Interlocked.Read(ref _lost);

    public long EventsLate => _buckets.Late;

    public long EventsOutOfOrder => _buckets.OutOfOrder;

    /// 진단용: 프로세스 번호별 받은 이벤트 수.
    public IReadOnlyDictionary<int, long> PerProcess => _diagnostics ?? new ConcurrentDictionary<int, long>();

    /// 진단용: 받은 DXGI 이벤트의 번호별 수 (번호 필터가 42 만 남기는지 확인).
    public IReadOnlyDictionary<int, long> PerEventId => _ids;

    /// 진단용: 첫 이벤트와 마지막 이벤트의 (이벤트 시각 - 받은 순간의 Stopwatch 틱): 같은 시계·같은 주파수면 둘 다 지연만큼의 작은 음수이고 서로 비슷함.
    public long FirstDeltaTicks => Interlocked.Read(ref _firstDeltaTicks);

    public long LastDeltaTicks => Interlocked.Read(ref _lastDeltaTicks);

    /// 진단용: 가장 큰 전달 지연 (틱, 양수): 이벤트 시각에서 받은 순간까지.
    public long MaxLagTicks => Interlocked.Read(ref _maxLagTicks);

    public void AddProcess(int pid) => _buckets.AddProcess(pid);

    public IReadOnlyList<FrameSecond> TakeSettled(long throughSecond)
    {
        try
        {
            Interlocked.Exchange(ref _lost, _session.EventsLost);
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            // 유실 수를 못 읽으면 직전 값 유지
        }

        return _buckets.Take(throughSecond);
    }

    private void OnEvent(TraceEvent data)
    {
        if (data.ProviderGuid != Dxgi)
        {
            return;
        }

        if (_diagnostics is not null)
        {
            _ = _ids.AddOrUpdate((int)data.ID, 1, (_, v) => v + 1);
        }

        if ((int)data.ID != PresentStartId)
        {
            return;
        }

        if (_diagnostics is not null)
        {
            _ = _diagnostics.AddOrUpdate(data.ProcessID, 1, (_, v) => v + 1);
            long delta = QpcOf(data) - Stopwatch.GetTimestamp();
            _ = Interlocked.CompareExchange(ref _firstDeltaTicks, delta, long.MinValue);
            _ = Interlocked.Exchange(ref _lastDeltaTicks, delta);
            long lag = -delta;
            long seen;
            while (lag > (seen = Interlocked.Read(ref _maxLagTicks)) && Interlocked.CompareExchange(ref _maxLagTicks, lag, seen) != seen)
            {
            }

            return;
        }

        _ = _buckets.Add(data.ProcessID, QpcOf(data));
    }

    // 이벤트 머리의 QPC 틱을 그대로 씀: TimeStampRelativeMSec 는 세션 시작 기준 실수 ms 라 Stopwatch 틱과 맞추려면 변환이 더 필요함 (구현이 "권장하지 않음" 으로 표시)
#pragma warning disable CS0618
    private static long QpcOf(TraceEvent data) => data.TimeStampQPC;
#pragma warning restore CS0618

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _flusher.Dispose();
        try
        {
            _session.Stop();
            _ = _pump.Wait(TimeSpan.FromSeconds(3));
        }
        catch (Exception e) when (e is not (StackOverflowException or OutOfMemoryException))
        {
            // 정지 실패는 세션 정리를 막지 않음
        }

        _session.Dispose();
    }
}
