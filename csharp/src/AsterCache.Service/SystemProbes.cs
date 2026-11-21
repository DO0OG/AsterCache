using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using AsterCache.Core.Logging;
using AsterCache.Windows;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace AsterCache.Service;

/// Windows 프리페처가 켜져 있는가 (읽기만 함). 프리페치 기록 (.pf) 으로 첫 실행 없이 boot 트레이스를 만들려면 프리페처가 응용 프로그램을 기록하고 있어야 합니다.
public static class SystemProbes
{
    private const string PrefetchKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters";
    private const int ApplicationBit = 1; // EnablePrefetcher: 1 = 응용 프로그램, 2 = 부팅, 3 = 둘 다

    public static bool PrefetcherOn() => PrefetcherOn(SysMainRunning, EnablePrefetcher);

    /// SysMain 서비스가 돌고 있고 EnablePrefetcher 의 응용 프로그램 비트가 켜져 있을 때만 참 (값을 읽지 못하면 거짓).
    public static bool PrefetcherOn(Func<bool> sysMainRunning, Func<int?> enablePrefetcher) => enablePrefetcher() is { } v && (v & ApplicationBit) != 0 && sysMainRunning();

    private static bool SysMainRunning()
    {
        try
        {
            using var sc = new ServiceController("SysMain");
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false; // 서비스가 없거나 읽을 수 없음
        }
    }

    private static int? EnablePrefetcher()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(PrefetchKey);
            return key?.GetValue("EnablePrefetcher") as int?;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}

/// 링크·정션을 푼 실제 경로 (GetFinalPathNameByHandle). 열 수 없으면 null.
public static partial class RealPaths
{
    private const uint BackupSemantics = 0x02000000; // 디렉터리도 열기
    private const uint ShareAll = 7;
    private const uint OpenExisting = 3;
    private const int BufferChars = 32768;

    public static string? Resolve(string path)
    {
        using SafeFileHandle h = CreateFileW(path, 0, ShareAll, 0, OpenExisting, BackupSemantics, 0);
        if (h.IsInvalid)
        {
            return null;
        }

        char[] buf = new char[BufferChars];
        uint n = GetFinalPathNameByHandleW(h, buf, (uint)buf.Length, 0);
        if (n == 0 || n >= buf.Length)
        {
            return null;
        }

        string s = new(buf, 0, (int)n);
        return s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + s[8..] : s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }

    /// ApplyHooks.RealPath 용: 풀 수 없으면 (아직 없는 경로 등) 정규화만.
    public static string Real(string path) => Resolve(path) ?? Path.GetFullPath(path);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial SafeFileHandle CreateFileW(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] filePath, uint filePathSize, uint flags);
}

/// 볼륨이 놓인 물리 디스크의 처리량 (PhysicalDisk(N 문자들)\Disk Bytes/sec, PDH 영어 카운터 이름이라 한국어 Windows 에서도 동작).
/// 같은 물리 디스크의 다른 볼륨 활동도 보도록 인스턴스 이름의 "N C: D:" 에서 볼륨 문자가 속한 디스크를 찾아 합칩니다.
/// 하드디스크는 같은 표본의 % Idle Time 도 함께 봅니다 (Load): 흩어진 작은 읽기는 처리량이 낮아도 디스크 시간을 채우기 때문입니다.
public static partial class DiskActivity
{
    /// 이보다 많이 읽고 쓰는 중이면 바쁜 것으로 봅니다 (바이트/초).
    public const double BusyBytesPerSecond = 4 << 20;

    /// 탐색이 드는 디스크 (하드디스크) 에서 % Idle Time 이 이 값 미만이면 처리량이 낮아도 바쁜 것으로 봅니다.
    /// 근거 (이 PC 의 하드디스크 실측): 아무도 안 쓸 때 표본 대부분이 98~100 (64 이상은 39개 중 38개, 순간 급증 때만 0~70), 64 KiB 를 초당 30회 읽는 다른 프로세스는 매 표본 23~64,
    /// 하드디스크를 꽉 채우는 순차·흩어진 읽기는 0~1. 한가한 쪽의 보통 값 (88 이상) 과 읽기 30회/초의 최대 (64) 사이에서 읽는 쪽으로 여유를 두고 정함.
    /// SSD 는 병렬로 처리해 쉬는 시간 비율이 실제 부하를 나타내지 않으므로 (NVMe 가 3 MiB/s 에서 4% 로 나옴) 쓰지 않음.
    public const double BusyIdlePercent = 75;

    private const uint FormatDouble = 0x200;
    private const uint MoreData = 0x800007D2;
    private const int SampleMs = 1000;
    private const long CacheMs = 2000;
    private static readonly PhysicalSampler Shared = new(() => SampleAll(() => Thread.Sleep(SampleMs)), () => Environment.TickCount64);

    // PDH_FMT_COUNTERVALUE_ITEM_W: 이름 포인터, 값 (상태 4 바이트, 정렬 4 바이트 더, double).
    [StructLayout(LayoutKind.Sequential)]
    private struct CounterItem
    {
        public nint Name;
        public uint Status;
        public double Value;
    }

    /// 서비스 종료 때 표본 스레드를 멈춥니다.
    public static void StopSampling() => Shared.Stop();

    /// 처리량 (바이트/초) 을 부하로 바꿈: 탐색이 드는 디스크에서 쉬는 시간 비율이 BusyIdlePercent 미만이면 처리량이 낮아도 BusyBytesPerSecond 로 올려 바쁨으로 보게 함. 비율을 모르면 처리량 그대로.
    public static double Load(double bytesPerSecond, double? idlePercent, bool seeks) => seeks && idlePercent < BusyIdlePercent ? Math.Max(bytesPerSecond, BusyBytesPerSecond) : bytesPerSecond;

    /// letter ("D:") 가 놓인 물리 디스크(들)의 부하 합 (바이트/초, 처리량이며 하드디스크는 Load 로 올린 값). 디스크를 찾지 못하거나 읽지 못하면 null.
    public static double? BytesPerSecond(string letter) => Shared.BytesPerSecond(letter);

    /// 서비스 시작에서 수집을 미리 켬 (첫 표본이 없는 구간을 시작 직후 몇 초로 줄임). 시험은 부르지 않음.
    public static void Start() => Shared.Start();

    /// letter 가 놓인 디스크의 처리량을 호출자 스레드에서 직접 잼: settle() 로 기다린 뒤 PDH 를 수집하고, window() 로 기다린 뒤 다시 수집해 그 사이 (window 구간) 만 잽니다.
    /// 공유 표본 (Read) 은 2 초마다 1 초 창 하나라 창이 언제 시작했는지 모르므로, 읽기를 멈춘 구간만 재야 할 때 (우리 읽기가 섞이면 안 될 때) 씁니다. 수집 실패·매핑 없음은 (null, Warming): 모름.
    /// settle 은 멈춘 직후 진행 중이던 읽기가 빠져나가게 하는 앞부분입니다. 새 쿼리를 열고 닫는 수 ms 의 비용이 있어 틱 경로에서는 부르지 않습니다.
    public static (double? Bps, SampleState State) MeasureWindow(string letter, Action settle, Action window)
    {
        settle();
        Dictionary<string, double>? data = null;
        try
        {
            data = SampleAll(window);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (Reported.TryAdd("window measure", 0)) // 카운터가 없는 시스템에서 관측마다 (10 초) 반복하지 않게 한 번만
            {
                Log?.Info($"DiskActivity: window measure failed ({e.GetType().Name})");
            }
        }

        return data is not null && Sum(letter, data) is { } bps ? (bps, SampleState.Ok) : (null, SampleState.Warming);
    }

    /// 값과 상태를 한 번에 읽음 (Warming·Stale 과 Broken 을 구분하는 호출자용). 바쁨 판정은 IsBusy(bps, state).
    /// keepStalled: 게임 보호 판정용. 수집이 GiveUpMs 넘게 멈춘 정체는 Broken 으로 넘기지 않고 Warming·Stale 로 유지 (정체는 고장이 아님).
    /// holdLast: 양보 판단용. 값을 못 읽게 된 지 HoldMs 안이면 고장 대신 직전 값을 이어 씀 ("바쁨/모름" 으로 막는 호출자는 쓰지 않음).
    public static (double? Bps, SampleState State) Read(string letter, bool keepStalled = false, bool holdLast = false) => Shared.Read(letter, keepStalled, holdLast);

    /// 바쁜가, 표본이 아직 없거나 낡았으면 바쁨, 읽지 못하는 고장은 바쁘지 않음 (기존 동작: 고장 때문에 영구히 막지 않음). keepStalled 는 Read 와 같음. 양보 판단 전용이라 직전 값을 이어 씀 (holdLast).
    public static bool BusyUnlessBroken(string letter, bool keepStalled = false) => Shared.BusyUnlessBroken(letter, keepStalled);

    /// 읽지 못하면 바쁜 것으로 간주 (보수적: 재적용을 미룸).
    public static bool IsBusy(string letter) => BytesPerSecond(letter) is not { } bps || bps >= BusyBytesPerSecond;

    /// 실패 사유 (로그 문구이자 판정 기준).
    public const string CounterUnavailable = "counter_unavailable";
    public const string InstanceUnmapped = "instance_unmapped";
    public const string ValueInvalid = "value_invalid";
    public const string StatusText = "disk activity unavailable; optimization paused";

    /// 인스턴스 이름 ("0", "0 C: D:") → 값. letter 가 이름에 들어 있는 인스턴스의 합, 하나도 없으면 null.
    public static double? Sum(string letter, IReadOnlyDictionary<string, double> instances) => Sum(letter, instances, out _);

    /// 사유: 매핑 없음 = instance_unmapped, 유한하지 않거나 음수인 값이 있음 = value_invalid (그 경우 letter 와 무관하게 null).
    public static double? Sum(string letter, IReadOnlyDictionary<string, double> instances, out string? reason) => Sum(letter, instances, [], out reason, out _);

    /// remembered: 앞서 이 문자가 붙어 있던 디스크 번호. 문자가 붙은 인스턴스가 없으면 그 번호의 인스턴스로 이어 읽음. disks: 다음에 기억할 디스크 번호.
    public static double? Sum(string letter, IReadOnlyDictionary<string, double> instances, IReadOnlyCollection<string> remembered, out string? reason, out IReadOnlyCollection<string> disks)
    {
        disks = remembered;
        if (instances.Values.Any(v => !double.IsFinite(v) || v < 0))
        {
            reason = ValueInvalid;
            return null;
        }

        IReadOnlyList<string> picked = PickInstances(letter, instances.Keys, remembered, out disks);
        reason = picked.Count > 0 ? null : InstanceUnmapped;
        return picked.Count > 0 ? picked.Sum(name => instances[name]) : null;
    }

    private static string Key(string letter) => letter.TrimEnd(':', '\\').ToUpperInvariant() + ":";

    // 인스턴스 이름 ("0", "0 C: D:") 앞의 디스크 번호, 숫자가 아니면 null (_Total).
    private static string? DiskNumber(string name) => uint.TryParse(name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), out uint n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    /// letter 가 붙은 인스턴스를 고름. 붙은 디스크 번호가 기억에 없는 번호를 포함하면 새 매핑이라 그 번호들로 기억을 바꿈 (disks).
    /// 붙은 번호가 모두 기억 안에 있으면 일부 인스턴스만 문자를 놓친 것이라 기억을 줄이지 않고 문자 없이 번호만 남은 인스턴스 ("2") 도 함께 고름.
    /// 하나도 없으면 remembered 번호의 인스턴스 ("0", "0 E:") 를 고름 (disks 는 그대로): 카운터가 한 표본에서 문자를 놓쳐도 이어 읽음.
    /// 번호는 앞자리가 아니라 첫 토큰 전체로 비교함 ("1" 이 "10" 을 고르지 않음).
    public static IReadOnlyList<string> PickInstances(string letter, IEnumerable<string> names, IReadOnlyCollection<string> remembered, out IReadOnlyCollection<string> disks)
    {
        string key = Key(letter);
        string[] all = [.. names];
        string[] mapped = [.. all.Where(name => name.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: > 1 } parts && uint.TryParse(parts[0], out _) && parts.Skip(1).Contains(key, StringComparer.OrdinalIgnoreCase))];
        disks = remembered;
        if (mapped.Length == 0)
        {
            return [.. all.Where(name => DiskNumber(name) is { } d && remembered.Contains(d))];
        }

        string[] numbers = [.. mapped.Select(n => DiskNumber(n)!).Distinct()];
        if (numbers.Any(n => !remembered.Contains(n)))
        {
            disks = numbers;
            return mapped;
        }

        return [.. mapped, .. all.Where(name => !name.Contains(' ', StringComparison.Ordinal) && DiskNumber(name) is { } d && remembered.Contains(d))];
    }

    private static readonly ConcurrentDictionary<string, byte> Reported = new(StringComparer.Ordinal);
    private static bool _namesLogged;

    /// 서비스 시작에서 꽂는 서비스 로그 (없으면 기록하지 않음 — 시험·CLI).
    public static ILog? Log { get; set; }

    // 값을 못 읽게 된 뒤 양보 판단이 직전 값을 이어 쓰는 시간: 인스턴스 목록이 잠시 다시 만들어지는 정도의 끊김을 건너뜀.
    private const long HoldMs = 10_000;

    // 같은 드라이브의 끊김·회복 로그 쌍을 남기는 최소 간격: 사이에 생긴 끊김은 횟수로 합침.
    private const long LogPairEveryMs = 600_000;

    /// 표본 상태: Ok = 최신 표본이 있음, Warming = 첫 표본 전, Stale = 마지막 성공 표본이 StaleMs 이상 지남, Broken = 수집 실패·카운터 쓸 수 없음·수집이 GiveUpMs 넘게 멈춤.
    public enum SampleState { Ok, Warming, Stale, Broken }

    /// 상태와 값을 한 번 읽은 결과로 바쁨을 판정: Ok 는 값으로, Warming·Stale 은 바쁨 (보류), Broken 은 바쁘지 않음 (고장 때문에 예열이 영구히 멈추지 않게).
    public static bool IsBusy(double? bps, SampleState state) => state switch
    {
        SampleState.Ok => bps >= BusyBytesPerSecond,
        SampleState.Broken => false,
        _ => true,
    };

    /// 표본은 호출자 스레드에서 잡지 않습니다: 처음 부를 때 시작한 백그라운드 스레드가 CacheMs 간격으로 모든 물리 디스크 인스턴스를 잡아 최신 표본만 바꿔 두고, 호출은 그것을 읽기만 합니다 (표본 대기로 막히지 않음).
    /// start: 수집 루프를 돌리는 방법 (기본 백그라운드 스레드, 시험은 아무것도 안 하고 Refresh·Tick 을 직접 부름). log: 수집 실패 기록 (기본 DiskActivity.Log).
    public sealed class PhysicalSampler(Func<IReadOnlyDictionary<string, double>?> sample, Func<long> nowMs, Action<Action>? start = null, Func<ILog?>? log = null)
    {
        // Seq: 게시 순번. 오래된 표본을 늦게 처리하는 호출이 더 새 표본으로 갱신된 상태를 되돌리지 못하게 비교함.
        internal sealed record Snapshot(long At, IReadOnlyDictionary<string, double>? Data, long Seq);

        // 표본 간격 (CacheMs) 의 두 배: 한 번 놓쳐도 이어지고, 두 번 연속 놓치면 낡은 값으로 판정하지 않음.
        private const long StaleMs = CacheMs * 2;

        // 수집이 이만큼 (60 초, 정상 간격의 30 배) 나오지 않으면 스레드가 죽거나 멈춘 것으로 보고 Broken 으로 넘김: 영구 보류 방지.
        private const long GiveUpMs = 60_000;
        private const string SampleStalled = "sample_stalled";
        private readonly CancellationTokenSource _stop = new();
        private volatile Snapshot? _latest;
        private long _seq;
        private int _stalledWarned; // 정체 경고를 이미 남겼는가 (0/1)
        private long _startedAt = long.MinValue; // 수집을 시작한 시각, 시작 전에는 MinValue

        /// 수집을 시작 (한 번만).
        public void Start()
        {
            if (Interlocked.CompareExchange(ref _startedAt, nowMs(), long.MinValue) == long.MinValue)
            {
                (start ?? StartThread)(Loop);
            }
        }

        /// 수집 루프를 끝냅니다 (서비스 종료·시험 정리). 이미 시작한 표본 한 번은 끝까지 가고, 그 뒤 루프가 빠져나옵니다.
        // shortcut: Stop 뒤에는 같은 인스턴스를 다시 시작할 수 없음 (Start 는 한 번만), 서비스 재시작은 새 프로세스라 필요해지면 새 인스턴스를 만들 것.
        public void Stop() => _stop.Cancel();

        public double? BytesPerSecond(string letter) => Read(letter).Bps;

        /// 바쁜가 (IsBusy(bps, state) 규칙), 값과 상태는 같은 한 번의 읽기에서 나옴.
        public bool BusyUnlessBroken(string letter, bool keepStalled = false)
        {
            (double? bps, SampleState state) = Read(letter, keepStalled, holdLast: true);
            return IsBusy(bps, state);
        }

        /// 최신 스냅샷 하나에서 값과 상태를 함께 돌려줌 (값과 상태를 따로 읽다 사이에 표본이 게시되는 경쟁을 막음).
        /// holdLast: 고장 (Broken) 이어도 처음 못 읽게 된 표본에서 HoldMs 가 지나지 않았으면 마지막으로 읽은 값을 Ok 로 돌려줌 (양보 판단 전용).
        public (double? Bps, SampleState State) Read(string letter, bool keepStalled = false, bool holdLast = false)
        {
            Start();
            (double? bps, SampleState state) = Evaluate(letter, keepStalled, _latest);
            if (holdLast && state == SampleState.Broken)
            {
                lock (_letters)
                {
                    if (State(letter) is { GoodAt: not null, DownSince: { } since } st && nowMs() - since < HoldMs)
                    {
                        return (st.GoodBps, SampleState.Ok);
                    }
                }
            }

            return (bps, state);
        }

        internal Snapshot? Latest => _latest;

        // 상태 갱신과 로그 대기열 적재는 잠금 안에서, 로그 출력은 잠금 밖에서 함 (느린 로그가 다른 드라이브의 읽기를 막지 않게).
        internal (double? Bps, SampleState State) Evaluate(string letter, bool keepStalled, Snapshot? snap)
        {
            (double? Bps, SampleState State) result;
            lock (_letters)
            {
                result = EvaluateLocked(letter, keepStalled, snap, nowMs());
            }

            Flush();
            return result;
        }

        private (double? Bps, SampleState State) EvaluateLocked(string letter, bool keepStalled, Snapshot? snap, long now)
        {
            if (snap is null)
            {
                long started = Volatile.Read(ref _startedAt);
                return now - started < GiveUpMs ? (null, SampleState.Warming) : Stalled(letter, keepStalled ? SampleState.Warming : SampleState.Broken, started);
            }

            LetterState st = State(letter);
            bool older = snap.Seq < st.Seq; // 이미 더 새 표본으로 갱신된 상태는 되돌리지 않음
            st.Seq = Math.Max(st.Seq, snap.Seq);
            if (snap.Data is null)
            {
                if (!older)
                {
                    Down(st, letter, CounterUnavailable, snap.At);
                }

                return (null, SampleState.Broken);
            }

            if (now - snap.At >= StaleMs)
            {
                return now - snap.At < GiveUpMs ? (null, SampleState.Stale) : Stalled(letter, keepStalled ? SampleState.Stale : SampleState.Broken, snap.At);
            }

            double? bps = Sum(letter, snap.Data, st.Disks, out string? reason, out IReadOnlyCollection<string> disks);
            if (reason is not null)
            {
                if (!older)
                {
                    Down(st, letter, reason, snap.At);
                }

                return (null, SampleState.Broken);
            }

            if (!older)
            {
                st.Disks = disks;
                st.GoodBps = bps!.Value;
                st.GoodAt = snap.At;
                Recovered(st, letter, snap.At);
            }

            return (bps, SampleState.Ok);
        }

        // 드라이브 문자 하나의 기억: 마지막으로 붙어 있던 디스크 번호, 마지막으로 읽은 값, 끊김 진행 상태와 로그 합치기, 반영한 마지막 표본 순번.
        private sealed class LetterState
        {
            public IReadOnlyCollection<string> Disks = [];
            public double GoodBps;
            public long? GoodAt;
            public long? DownSince;
            public string? DownReason;
            public bool DownLogged;
            public long? PairAt;
            public int Merged;
            public long Seq;
        }

        private readonly Dictionary<string, LetterState> _letters = [];
        private readonly Queue<(string Message, bool Warn)> _out = new(); // _letters 잠금으로 보호, 순서대로 출력할 로그
        private int _draining;

        // _letters 잠금 안에서만 부름.
        private LetterState State(string letter)
        {
            string key = Key(letter);
            if (!_letters.TryGetValue(key, out LetterState? st))
            {
                _letters[key] = st = new LetterState();
            }

            return st;
        }

        // 대기열을 순서대로 출력: 한 스레드만 출력하고 (나머지는 바로 돌아감, 비운 뒤 다시 확인하므로 놓치지 않음) 호출자 스레드는 _letters 를 쥔 채 쓰지 않음.
        private void Flush()
        {
            while (Interlocked.CompareExchange(ref _draining, 1, 0) == 0)
            {
                try
                {
                    while (true)
                    {
                        (string Message, bool Warn) item;
                        lock (_letters)
                        {
                            if (!_out.TryDequeue(out item))
                            {
                                break;
                            }
                        }

                        Note(item.Message, item.Warn);
                    }
                }
                finally
                {
                    Volatile.Write(ref _draining, 0);
                }

                lock (_letters)
                {
                    if (_out.Count == 0)
                    {
                        return;
                    }
                }
            }
        }

        // 값을 못 읽는 상태에 들어감 (at: 최초 실패 표본의 시각): 같은 상태가 이어지는 동안은 다시 남기지 않고, 같은 드라이브의 로그 쌍은 LogPairEveryMs 에 한 번만 남김 (사이의 끊김은 횟수로 합침). _letters 잠금 안에서만 부름.
        private void Down(LetterState st, string letter, string reason, long at)
        {
            if (st.DownSince is not null)
            {
                return;
            }

            st.DownSince = at;
            st.DownReason = reason;
            st.DownLogged = st.PairAt is not { } last || at - last >= LogPairEveryMs;
            if (!st.DownLogged)
            {
                st.Merged++;
                return;
            }

            st.PairAt = at;
            _out.Enqueue(($"DiskActivity: {letter} {StatusText} ({reason}{(st.Merged > 0 ? $", {st.Merged} short outage(s) merged since the last report" : string.Empty)})", false));
            st.Merged = 0;
        }

        // 다시 읽게 됨: 끊김을 남겼던 경우에만 지속 시간과 함께 회복을 남김. _letters 잠금 안에서만 부름.
        private void Recovered(LetterState st, string letter, long at)
        {
            if (st.DownSince is not { } since)
            {
                return;
            }

            st.DownSince = null;
            if (st.DownLogged)
            {
                _out.Enqueue(($"DiskActivity: {letter} disk activity available again after {Math.Max(0, at - since) / 1000} s ({st.DownReason})", false));
            }
        }

        // 정체가 GiveUpMs 를 넘음 (since: 정체가 시작된 시각): 경고는 정체 진입마다 한 번 (새 표본이 게시되면 다시 켜짐). state 는 호출자가 정함 (기본 Broken, 게임 보호 판정은 정체 상태 유지). _letters 잠금 안에서만 부름.
        private (double?, SampleState) Stalled(string letter, SampleState state, long since)
        {
            Down(State(letter), letter, SampleStalled, since);
            if (Interlocked.Exchange(ref _stalledWarned, 1) == 0)
            {
                _out.Enqueue(($"DiskActivity: samples stalled for over {GiveUpMs / 1000} s, giving up on {letter} (other callers treat it as broken, game protection gates stay busy)", true));
            }

            return (null, state);
        }

        /// 표본을 한 번 잡아 최신으로 교체 (실패·예외는 Data = null 로 기록해 다음 간격에 다시 시도). 로그가 던져도 밖으로 나가지 않음.
        internal void Refresh()
        {
            IReadOnlyDictionary<string, double>? data = null;
            try
            {
                data = sample();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Note($"DiskActivity: sample failed ({e.GetType().Name})");
            }

            Publish(data);
        }

        // 표본을 게시하고, 이미 읽은 적 있는 드라이브는 여기서 상태를 갱신함 (읽는 빈도와 상관없이 최초 실패 시각·기억한 디스크 번호를 놓치지 않음).
        private void Publish(IReadOnlyDictionary<string, double>? data)
        {
            var snap = new Snapshot(nowMs(), data, Interlocked.Increment(ref _seq));
            _latest = snap;
            Volatile.Write(ref _stalledWarned, 0);
            string[] letters;
            lock (_letters)
            {
                letters = [.. _letters.Keys];
            }

            foreach (string letter in letters)
            {
                Evaluate(letter, false, snap);
            }
        }

        /// 한 주기: 표본을 잡고, 수집에 걸린 시간을 뺀 다음 대기 시간 (ms) 을 돌려줌 (주기 = CacheMs).
        internal int Tick()
        {
            long t0 = nowMs();
            Refresh();
            return (int)Math.Clamp(CacheMs - (nowMs() - t0), 0, CacheMs);
        }

        private void Note(string message, bool warn = false)
        {
            try
            {
                ILog? sink = log?.Invoke() ?? Log;
                if (warn)
                {
                    sink?.Warn(message);
                }
                else
                {
                    sink?.Info(message);
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // 로그 기록 실패는 수집 스레드를 죽이지 않음 (예: 콘솔 출력 파이프가 끊김)
            }
        }

        private void Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                int wait = (int)CacheMs;
                try
                {
                    wait = Tick();
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Publish(null); // 예상 밖 실패: 고장 표본을 게시하고 계속 재시도
                    Note($"DiskActivity: sampler loop failed ({e.GetType().Name})");
                }

                _ = _stop.Token.WaitHandle.WaitOne(wait);
            }
        }

        private static void StartThread(Action loop) => new Thread(() => loop()) { IsBackground = true, Name = "DiskActivity sampler" }.Start(); // 백그라운드라 프로세스 종료를 막지 않음
    }

    // wait: 두 번의 PDH 수집 사이를 기다리는 방법 (수집 스레드는 SampleMs 동안 잠, 창 측정은 호출자가 정한 멈춤).
    // 처리량과 쉬는 시간 비율은 같은 쿼리·같은 두 번의 수집에서 읽음 (표본 주기와 수집 횟수는 그대로). 쉬는 시간 비율을 못 읽으면 처리량만으로 판정 (기존 동작).
    private static Dictionary<string, double>? SampleAll(Action wait)
    {
        if (PdhOpenQueryW(null, 0, out nint query) != 0)
        {
            return null;
        }

        try
        {
            if (PdhAddEnglishCounterW(query, @"\PhysicalDisk(*)\Disk Bytes/sec", 0, out nint counter) != 0)
            {
                return null;
            }

            bool hasIdle = PdhAddEnglishCounterW(query, @"\PhysicalDisk(*)\% Idle Time", 0, out nint idleCounter) == 0;
            if (PdhCollectQueryData(query) != 0)
            {
                return null;
            }

            wait();
            if (PdhCollectQueryData(query) != 0 || ReadInstances(counter) is not { } result)
            {
                return null;
            }

            Dictionary<string, double>? idle = hasIdle ? ReadInstances(idleCounter) : null;
            foreach (string name in result.Keys.ToArray())
            {
                result[name] = Load(result[name], idle is not null && idle.TryGetValue(name, out double percent) ? percent : null, idle is not null && Seeks(name));
            }

            if (!_namesLogged)
            {
                _namesLogged = true;
                Log?.Info("DiskActivity: PhysicalDisk instances " + string.Join(", ", result.Keys));
            }

            return result;
        }
        finally
        {
            PdhCloseQuery(query);
        }
    }

    /// 그래픽 메모리 전용 사용량 합계 (성능 카운터 `GPU Adapter Memory` 의 Dedicated Usage, 어댑터마다 한 인스턴스): 쿼리를 한 번 열어 두고 부를 때마다 수집합니다. 프로세스를 열지 않습니다.
    public sealed class GpuDedicatedMemory : IDisposable
    {
        private readonly nint _query;
        private readonly nint _counter;

        private GpuDedicatedMemory(nint query, nint counter) => (_query, _counter) = (query, counter);

        /// 이 PC 에 카운터가 없거나 열지 못하면 null.
        public static GpuDedicatedMemory? Open()
        {
            if (PdhOpenQueryW(null, 0, out nint query) != 0)
            {
                return null;
            }

            if (PdhAddEnglishCounterW(query, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out nint counter) != 0 || PdhCollectQueryData(query) != 0)
            {
                _ = PdhCloseQuery(query);
                return null;
            }

            return new GpuDedicatedMemory(query, counter);
        }

        /// 모든 어댑터의 전용 사용량 합계 (MiB). 읽지 못하면 null.
        public int? Mib() => PdhCollectQueryData(_query) == 0 && ReadInstances(_counter) is { } values ? (int)Math.Min(int.MaxValue, values.Values.Sum() / 1_048_576d) : null;

        public void Dispose() => _ = PdhCloseQuery(_query);
    }

    // 카운터 하나의 인스턴스별 값 (한 인스턴스라도 읽지 못하면 전체를 믿지 않음: 보수적).
    private static unsafe Dictionary<string, double>? ReadInstances(nint counter)
    {
        uint size = 0;
        if (PdhGetFormattedCounterArrayW(counter, FormatDouble, ref size, out _, 0) != MoreData)
        {
            return null;
        }

        nint buffer = Marshal.AllocHGlobal((nint)size);
        try
        {
            if (PdhGetFormattedCounterArrayW(counter, FormatDouble, ref size, out uint count, buffer) != 0)
            {
                return null;
            }

            var result = new Dictionary<string, double>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                CounterItem item = *((CounterItem*)buffer + i);
                if (item.Status != 0)
                {
                    return null;
                }

                result[Marshal.PtrToStringUni(item.Name) ?? string.Empty] = item.Value;
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // 인스턴스 ("0 C: D:") 의 디스크가 탐색이 드는 종류 (하드디스크) 인가: 디스크 번호별로 한 번만 물어 기억함. 문자가 없거나 알 수 없으면 아님 (처리량만으로 판정), 모르는 값은 기억하지 않아 다음 표본에 다시 물음.
    private static readonly ConcurrentDictionary<string, bool> SeekDisks = new(StringComparer.Ordinal);

    private static bool Seeks(string instance)
    {
        string[] parts = instance.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !uint.TryParse(parts[0], out _))
        {
            return false;
        }

        if (SeekDisks.TryGetValue(parts[0], out bool known))
        {
            return known;
        }

        return DiskInfo.Query(parts[1]).IncursSeekPenalty is { } seeks && SeekDisks.GetOrAdd(parts[0], seeks);
    }

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint bufferSize, out uint itemCount, nint buffer);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhOpenQueryW(string? dataSource, nint userData, out nint query);

    [LibraryImport("pdh.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint PdhAddEnglishCounterW(nint query, string path, nint userData, out nint counter);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCollectQueryData(nint query);

    [LibraryImport("pdh.dll")]
    private static partial uint PdhCloseQuery(nint query);
}

/// 설정·계획 폴더가 관리자/SYSTEM 전용인지 (설계 2.1: 서비스는 SYSTEM 이라 일반 사용자가 쓸 수 있는 폴더의 계획을 믿으면 안 됨).
public static class AclCheck
{
    private static readonly SecurityIdentifier[] Trusted =
    [
        new(WellKnownSidType.LocalSystemSid, null),
        new(WellKnownSidType.BuiltinAdministratorsSid, null),
        new(WellKnownSidType.CreatorOwnerSid, null),
        new("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"), // TrustedInstaller
    ];

    private const FileSystemRights WriteMask = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
        | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// 폴더를 바꾸거나 권한을 바꿀 수 있는 신뢰 밖 주체의 SID 목록 (비어 있으면 안전).
    public static IReadOnlyList<string> UntrustedWriters(string directory) => UntrustedWriters(new DirectoryInfo(directory).GetAccessControl());

    /// 파일(config.json) 쪽 같은 검사: 소유자와 쓰기 권한.
    public static IReadOnlyList<string> UntrustedFileWriters(string file) => UntrustedWriters(new FileInfo(file).GetAccessControl());

    public static IReadOnlyList<string> UntrustedWriters(FileSystemSecurity security)
    {
        var bad = new SortedSet<string>(StringComparer.Ordinal);
        if (security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner && !Trusted.Contains(owner))
        {
            bad.Add("owner:" + owner.Value);
        }

        foreach (FileSystemAccessRule r in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (r.AccessControlType == AccessControlType.Allow && (r.FileSystemRights & WriteMask) != 0
                && r.IdentityReference is SecurityIdentifier sid && !Trusted.Contains(sid))
            {
                bad.Add(sid.Value);
            }
        }

        return bad.ToList();
    }
}

/// 전체 CPU 사용률 (GetSystemTimes): 호출 사이의 사용 시간 비율.
public sealed partial class CpuLoad
{
    private long _idle;
    private long _total;
    private bool _primed;

    /// 직전 호출 뒤의 CPU 사용률 (0..100). 처음 부르거나 읽지 못하면 null.
    public double? Percent()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user))
        {
            return null;
        }

        long total = kernel + user; // kernel 에는 idle 이 포함됨
        double? result = null;
        if (_primed && total > _total)
        {
            result = 100.0 * (1.0 - ((double)(idle - _idle) / (total - _total)));
        }

        (_idle, _total, _primed) = (idle, total, true);
        return result;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemTimes(out long idle, out long kernel, out long user);
}

/// 전원 상태 (GetSystemPowerStatus).
public static partial class PowerProbe
{
    private const byte AcOnline = 1;
    private const byte NoBattery = 128;
    private const byte Unknown = 255;

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    /// AC 연결, 배터리 없음, 알 수 없음은 true (방해될 일이 없음). 배터리면 남은 비율이 minBatteryPercent 이상일 때만 true. 조회 자체가 실패하면 false (보수).
    public static bool PowerOk(int minBatteryPercent) =>
        Decide(GetSystemPowerStatus(out PowerStatus s), s.AcLineStatus, s.BatteryFlag, s.BatteryLifePercent, minBatteryPercent);

    /// PowerOk 의 판정 (시험용으로 분리): queried 는 GetSystemPowerStatus 가 성공했는가.
    public static bool Decide(bool queried, byte acLineStatus, byte batteryFlag, byte batteryLifePercent, int minBatteryPercent)
    {
        if (!queried)
        {
            return false;
        }

        if (acLineStatus is AcOnline or Unknown || (batteryFlag & NoBattery) != 0)
        {
            return true;
        }

        return batteryLifePercent != Unknown && batteryLifePercent >= minBatteryPercent;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out PowerStatus status);
}
