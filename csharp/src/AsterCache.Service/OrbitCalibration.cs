using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using AsterCache.Core;
using AsterCache.Core.Discovery;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan;
using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 장치 표본 수집, 분할 conformal 구간, 온라인 감시, <데이터 폴더>\disk_model.json 저장.
/// 실제 읽기는 모두 주입된 읽기기로만 하고, 아래 조건이 하나라도 어긋나면 읽기 0회로 돌아옵니다 (스트림은 그대로 진행).
/// 대여 유효 · 그 디스크의 게임이 실행·종료 유예 중이 아님 · 디스크 유휴 · 안티치트 허용 · 취소 안 됨.
public sealed class OrbitCalibration
{
    public const string FileName = "disk_model.json";
    private const int Sector = 4096;

    private readonly object _lock = new();
    private readonly object _rngLock = new();
    private readonly string _path;
    private readonly ILog _log;
    private readonly Func<string, long, int, CancellationToken, TimeSpan?> _read;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string, bool> _diskBusy;
    private readonly Func<GameSettings, bool> _gameActive;
    private readonly Random _random;
    private readonly Func<string?, CapabilityOutcome> _decide;
    private readonly Func<GameSettings, string> _diskKey;
    private readonly Dictionary<string, DiskModel> _disks;
    private readonly Dictionary<string, List<List<(string Path, long Offset)>>> _recent = []; // 디스크 키별 최근 스트림들이 읽은 (파일, 오프셋), _lock 으로 보호
    private readonly bool _persist; // false 면 기존 파일을 읽거나 옮기지 못한 상태라 파일을 덮지 않습니다

    /// read: (경로, 오프셋, 길이, 취소) → 걸린 시간, 실패면 null. diskBusy: 드라이브 문자 ("D:") → 바쁜가 (DiskActivity.IsBusy). gameActive: 그 게임이나 같은 디스크의 다른 게임이 실행·종료 유예 중인가.
    public OrbitCalibration(string dataDir, ILog log, Func<string, long, int, CancellationToken, TimeSpan?> read, Func<DateTimeOffset> now, Func<string, bool> diskBusy,
        Func<GameSettings, bool> gameActive, Random random, Func<string?, CapabilityOutcome>? decide = null, Func<GameSettings, string>? diskKey = null)
    {
        (_path, _log, _read, _now, _diskBusy, _gameActive, _random) = (Path.Combine(dataDir, FileName), log, read, now, diskBusy, gameActive, random);
        // 실제 읽기 게이트: 안티치트가 없거나 vanguard 일 때만 허용 (ShadowPlan 은 계획만 세워 항상 허용이라 맞지 않음)
        _decide = decide ?? (kind => Capabilities.Decide(CapabilityFeature.NewPrefetch, kind).Outcome);
        _diskKey = diskKey ?? (g => DiskKey(g.OriginRoot));
        (_disks, _persist) = Load(_path, log);
    }

    private sealed class DiskModel
    {
        // Boot: 서비스 정지 상태에서 CLI 가 모은 부트스트랩 표본 (저장 형식의 source = "bootstrap")
        public List<(double Ms, long Dist, bool Boot)> Seek { get; } = [];

        public List<(double Mbps, bool Boot)> Thr { get; } = [];

        public List<int> SeekMisses { get; } = [];

        public List<int> ThrMisses { get; } = [];

        public DateTimeOffset? InvalidatedAt { get; set; }

        public int PdhStreak { get; set; }
    }

    /// 물리 디스크 키: 볼륨 루트 문자 ("D:"), 모르면 빈 문자열.
    public static string DiskKey(string path) => PlanFile.SplitDrive(path).ToUpperInvariant();

    /// 서비스가 쓰는 장치 키: 물리 디스크 번호로 만든 키, 조회에 실패하면 드라이브 문자.
    public static string PhysicalDiskKey(GameSettings game) => VolumeDisks.PhysicalKey(game.OriginRoot) ?? DiskKey(game.OriginRoot);

    /// 그 게임의 장치 키 (포락선 조회·무효화에 같은 키를 쓰게 함).
    public string KeyOf(GameSettings game) => _diskKey(game);

    /// 지금 쓸 수 있는 장치 구간, 보정 없음이면 null.
    public DeviceFit? Envelope(string diskKey)
    {
        lock (_lock)
        {
            return _disks.TryGetValue(diskKey, out DiskModel? m) ? FitOf(m) : null;
        }
    }

    /// 그 디스크에 저장된 표본 수 (탐색, 전송률).
    public (int Seek, int Thr) SampleCounts(string diskKey)
    {
        lock (_lock)
        {
            return _disks.TryGetValue(diskKey, out DiskModel? m) ? (m.Seek.Count, m.Thr.Count) : (0, 0);
        }
    }

    /// 실제 읽기로 쓰는 읽기기: 호출마다 비버퍼 핸들을 열고 닫습니다. shortcut: 첫 열기 시간이 지연에 섞임 (모든 표본이 같으니 구간은 그대로 유효), 핸들을 길게 쥐면 게임 파일을 잡고 있게 되므로 지금은 이대로.
    public static TimeSpan? UnbufferedRead(string path, long offset, int bytes, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return null;
        }

        try
        {
            using var reader = new UnbufferedReader(hint: new IoPriorityHint(_ => { })); // 실패는 무시하고 보통 우선순위로 읽음
            long start = Stopwatch.GetTimestamp();
            byte[] got = reader.Read(path, offset, bytes);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(start);
            return got.Length == bytes ? elapsed : null; // 짧게 읽힌 것은 버림
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// 핫 집합 스트림 직전에 한 번 부릅니다. 시도한 읽기 바이트 합계 (조건이 어긋나면 0).
    public long Sample(GameSettings game, IReadOnlyList<ReadOp> ops, string? antiCheatKind, DiskReadCoordinator.Lease? lease, CancellationToken ct) =>
        lease is { IsValid: true } ? Probe([(game, antiCheatKind)], ops, lease, ct, false, out _) : 0;

    /// 부트스트랩 표본 하나: 대여 없이 읽으므로 호출한 쪽 (CLI) 이 서비스 정지와 게임 미실행을 확인해야 합니다. 나머지 조건과 저장은 Sample 과 같고, 표본에 bootstrap 표시가 붙습니다.
    public long SampleBootstrap(GameSettings game, IReadOnlyList<ReadOp> ops, string? antiCheatKind, CancellationToken ct) =>
        SampleBootstrap([(game, antiCheatKind)], ops, ct);

    /// 같은 디스크의 여러 게임 예열 목록을 합친 풀 하나에서 표본을 뽑습니다: 게이트 (게임 실행·안티치트 허용) 는 포함된 게임 전부에 대해 확인합니다.
    public long SampleBootstrap(IReadOnlyList<(GameSettings Game, string? AntiCheatKind)> games, IReadOnlyList<ReadOp> ops, CancellationToken ct) =>
        SampleBootstrap(games, ops, ct, out _);

    /// 위와 같고, 읽기가 0 회였던 이유를 reason 으로 돌려줍니다 (cancelled | anticheat | game_active | disk_busy | no_disk_key | no_far_position, 읽었으면 null).
    public long SampleBootstrap(IReadOnlyList<(GameSettings Game, string? AntiCheatKind)> games, IReadOnlyList<ReadOp> ops, CancellationToken ct, out string? reason) =>
        Probe(games, ops, null, ct, true, out reason);

    private long Probe(IReadOnlyList<(GameSettings Game, string? AntiCheatKind)> games, IReadOnlyList<ReadOp> ops, DiskReadCoordinator.Lease? lease, CancellationToken ct, bool bootstrap, out string? reason)
    {
        reason = null;
        if (games.Count == 0)
        {
            reason = "no_games";
            return 0;
        }

        GameSettings game = games[0].Game;
        string disk = _diskKey(game);
        reason = ct.IsCancellationRequested ? "cancelled"
            : disk.Length == 0 ? "no_disk_key"
            : games.Any(g => _decide(g.AntiCheatKind) != CapabilityOutcome.Allow) ? "anticheat"
            : games.Any(g => _gameActive(g.Game)) ? "game_active"
            : _diskBusy(DiskKey(game.OriginRoot)) ? "disk_busy" : null; // 바쁨 판정은 드라이브 문자 기준
        if (reason is not null)
        {
            return 0;
        }

        using var linked = lease is null ? CancellationTokenSource.CreateLinkedTokenSource(ct) : CancellationTokenSource.CreateLinkedTokenSource(ct, lease.Token);
        CancellationToken token = linked.Token;
        Segment[] segments = Segments(ops);
        long bytes = 0;
        var seek = new List<(double Ms, long Dist)>();
        var thr = new List<double>();
        long? previous = null; // 직전에 성공한 읽기의 목록 내 누적 위치
        var mine = new List<(string Path, long Offset)>(); // 이번 스트림이 낸 읽기
        List<(string Path, long Offset)> recent;
        lock (_lock)
        {
            recent = _recent.TryGetValue(disk, out List<List<(string Path, long Offset)>>? past) ? [.. past.SelectMany(x => x)] : [];
        }

        // 최근 읽은 위치 (이전 스트림과 이번 스트림) 에서 MinProbeGapBytes 안의 위치는 후보에서 뺍니다: 드라이브 캐시 적중 방지
        ILookup<string, long> Avoid() => recent.Concat(mine).ToLookup(r => r.Path, r => r.Offset, StringComparer.OrdinalIgnoreCase);

        // 디스크 바쁨은 자기 표본 읽기 때문에 참이 될 수 있어 시작 때만 보고, 게임 활성은 읽기마다 다시 봅니다
        bool CanIssue(int size) => (lease?.IsValid ?? true) && !token.IsCancellationRequested && !games.Any(g => _gameActive(g.Game)) && bytes + size <= Orbit3Constants.MaxProbeBytesPerStream;

        for (int i = 0; i < Orbit3Constants.SeekProbeReads && CanIssue(Orbit3Constants.SeekProbeBytes); i++)
        {
            // 직전 읽기와 목록 내 거리가 가까우면 후보에서 빠지고, 남은 후보가 없으면 이 읽기는 건너뜀
            if (Pick(segments, Orbit3Constants.SeekProbeBytes, Avoid(), previous) is not { } spot)
            {
                continue;
            }

            mine.Add((spot.Path, spot.Offset));
            bytes += Orbit3Constants.SeekProbeBytes;
            if (_read(spot.Path, spot.Offset, Orbit3Constants.SeekProbeBytes, token) is not { Ticks: > 0 } elapsed)
            {
                previous = null; // 실패한 읽기 뒤의 머리 위치는 모름
                continue;
            }

            if (previous is { } from)
            {
                seek.Add((elapsed.TotalMilliseconds, Math.Abs(spot.Position - from)));
            }

            previous = spot.Position;
        }

        for (int i = 0; i < Orbit3Constants.ThroughputProbeReads && CanIssue(Orbit3Constants.ThroughputProbeBytes); i++)
        {
            if (Pick(segments, Orbit3Constants.ThroughputProbeBytes, Avoid(), null) is not { } spot)
            {
                continue;
            }

            mine.Add((spot.Path, spot.Offset));
            bytes += Orbit3Constants.ThroughputProbeBytes;
            if (_read(spot.Path, spot.Offset, Orbit3Constants.ThroughputProbeBytes, token) is { Ticks: > 0 } elapsed)
            {
                thr.Add(Orbit3Constants.ThroughputProbeBytes / 1e6 / elapsed.TotalSeconds);
            }
        }

        lock (_lock)
        {
            if (mine.Count > 0)
            {
                if (!_recent.TryGetValue(disk, out List<List<(string Path, long Offset)>>? streams))
                {
                    _recent[disk] = streams = [];
                }

                streams.Add(mine);
                Trim(streams, Orbit3Constants.RecentProbeStreams);
            }
        }

        // 한 스트림의 표본 12개는 같은 SMR 상태에서 나와 교환 가능하지 않으므로 스트림 하나를 관측값 하나로 셉니다 (보정 켜짐은 스트림 2×19 개 이상).
        // shortcut: 명목 seek 가 거리를 무시해 잔차가 커짐, 실측 이탈률이 높으면 거리 구간별 명목값으로
        Add(disk, seek.Count > 0 ? (Median(seek.Select(s => s.Ms)), (long)Median(seek.Select(s => (double)s.Dist))) : null, thr.Count > 0 ? Median(thr) : null, bootstrap);
        if (bytes == 0)
        {
            reason = token.IsCancellationRequested ? "cancelled" : games.Any(g => _gameActive(g.Game)) ? "game_active" : "no_far_position";
        }

        return bytes;
    }

    /// 스트림 하나의 관측값을 넣습니다 (없는 지표는 null): 넣기 전에 스트림 시작 때의 구간에 드는지 지표별 misses 에 기록하고, 이항 검정에서 걸리면 포락선을 무효화합니다.
    internal void Add(string disk, (double Ms, long Dist)? seek, double? thr, bool bootstrap = false)
    {
        lock (_lock)
        {
            DiskModel m = ModelOf(disk);
            DeviceFit? fit = FitOf(m);
            if (seek is { } s)
            {
                Observe(m.SeekMisses, fit is not null ? OrbitEnvelope.SeekInside(fit, s.Ms) : null);
                m.Seek.Add((s.Ms, s.Dist, bootstrap));
                Trim(m.Seek);
            }

            if (thr is { } mbps)
            {
                Observe(m.ThrMisses, fit is not null ? OrbitEnvelope.ThroughputInside(fit, mbps) : null);
                m.Thr.Add((mbps, bootstrap));
                Trim(m.Thr);
            }

            if (Rejects(m.SeekMisses) || Rejects(m.ThrMisses))
            {
                Invalidate(disk, m, "miss count too high for alpha");
            }

            Save();
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] v = [.. values.Order()];
        return v.Length % 2 == 1 ? v[v.Length / 2] : (v[(v.Length / 2) - 1] + v[v.Length / 2]) / 2;
    }

    /// PDH 교차 확인: 스트림 평균 처리량 (B/s). 하한 (beta_hat * ThroughputMin) 보다 낮은 스트림이 PdhAlarmStreak 연속이면 무효화하고 true.
    public bool NoteStreamThroughput(string disk, double bytesPerSecond)
    {
        if (!double.IsFinite(bytesPerSecond) || bytesPerSecond <= 0)
        {
            return false;
        }

        lock (_lock)
        {
            DiskModel m = ModelOf(disk);
            if (FitOf(m) is not { } fit || bytesPerSecond / 1e6 >= fit.ThroughputHatMBps * fit.Envelope.ThroughputMin)
            {
                m.PdhStreak = 0;
                return false;
            }

            if (++m.PdhStreak < Orbit3Constants.PdhAlarmStreak)
            {
                return false;
            }

            Invalidate(disk, m, "pdh throughput below envelope");
            Save();
            return true;
        }
    }

    private readonly record struct Segment(string Path, long Offset, long Length, long Start);

    private readonly record struct Spot(string Path, long Offset, long Position);

    private static Segment[] Segments(IReadOnlyList<ReadOp> ops)
    {
        var list = new List<Segment>();
        long start = 0;
        foreach (ReadOp op in ops.Where(o => o.Length > 0 && o.Offset >= 0))
        {
            list.Add(new Segment(op.Path, op.Offset, op.Length, start));
            start += op.Length;
        }

        return [.. list];
    }

    private static long FloorDiv(long a, long b) => (a - (((a % b) + b) % b)) / b;

    /// 크기 size 읽기 하나를 4096 정렬 오프셋으로, 완전히 op 범위 안에 들어가게 고릅니다 (범위가 모자라면 그 op 는 제외).
    /// avoid: 파일별로 최근 읽은 오프셋, 그 MinProbeGapBytes 안의 위치는 후보에서 뺍니다. around: 목록 내 누적 위치, 그 MinProbeGapBytes 안도 뺍니다.
    /// 남은 후보 위치 전체에서 고르게 뽑아, 후보가 없으면 null. (뽑은 뒤 거르는 재시도는 큰 파일 몇 개에 후보가 몰린 풀에서 실패가 잦아 후보를 먼저 줄입니다.)
    private Spot? Pick(Segment[] segments, int size, ILookup<string, long> avoid, long? around)
    {
        const long gap = Orbit3Constants.MinProbeGapBytes;
        var free = new List<(Segment Seg, long First, long From, long To)>(); // 4096 간격 후보 번호의 [From, To)
        long total = 0;
        foreach (Segment s in segments)
        {
            long first = (s.Offset + Sector - 1) / Sector * Sector;
            long last = s.Offset + s.Length < size ? -1 : (s.Offset + s.Length - size) / Sector * Sector; // 음수 나눗셈이 0 으로 잘리는 것을 막음
            if (last < first)
            {
                continue;
            }

            var cuts = new List<(long Lo, long Hi)>(); // 막힌 오프셋 구간 (닫힌 구간)
            cuts.AddRange(avoid[s.Path].Select(r => (r - gap + 1, r + gap - 1)));
            if (around is { } p)
            {
                cuts.Add((p - gap + 1 - s.Start + s.Offset, p + gap - 1 - s.Start + s.Offset));
            }

            long n = ((last - first) / Sector) + 1;
            long cursor = 0;
            foreach ((long lo, long hi) in cuts.OrderBy(c => c.Lo))
            {
                long a = Math.Max(0, -FloorDiv(first - lo, Sector)); // lo 이상인 첫 후보 번호
                long b = Math.Min(n - 1, FloorDiv(hi - first, Sector)); // hi 이하인 마지막 후보 번호
                if (a > b)
                {
                    continue;
                }

                if (a > cursor)
                {
                    free.Add((s, first, cursor, a));
                    total += a - cursor;
                }

                cursor = Math.Max(cursor, b + 1);
            }

            if (cursor < n)
            {
                free.Add((s, first, cursor, n));
                total += n - cursor;
            }
        }

        if (total == 0)
        {
            return null;
        }

        lock (_rngLock)
        {
            long pick = _random.NextInt64(total);
            foreach ((Segment seg, long first, long from, long to) in free)
            {
                if (pick < to - from)
                {
                    long offset = first + ((from + pick) * Sector);
                    return new Spot(seg.Path, offset, seg.Start + (offset - seg.Offset));
                }

                pick -= to - from;
            }
        }

        return null;
    }

    private DiskModel ModelOf(string disk)
    {
        if (!_disks.TryGetValue(disk, out DiskModel? m))
        {
            _disks[disk] = m = new DiskModel();
        }

        return m;
    }

    private static DeviceFit? FitOf(DiskModel m) =>
        OrbitEnvelope.Fit([.. Fitting(m.Seek, s => s.Boot).Select(s => s.Ms)], [.. Fitting(m.Thr, t => t.Boot).Select(t => t.Mbps)]);

    // 부트스트랩과 운영 표본은 한 창에 시간순으로 넣되, 운영 표본이 보정 켜짐 기준의 2배 이상 쌓이면 부트스트랩은 뺍니다.
    private static IEnumerable<T> Fitting<T>(List<T> samples, Func<T, bool> boot) =>
        samples.Count(x => !boot(x)) >= Orbit3Constants.MinCalibrationSamples * 2 ? samples.Where(x => !boot(x)) : samples;

    private static void Observe(List<int> misses, bool? inside)
    {
        if (inside is { } ok)
        {
            misses.Add(ok ? 0 : 1);
            Trim(misses, Orbit3Constants.MonitorWindow);
        }
    }

    private static void Trim<T>(List<T> list, int max = Orbit3Constants.DiskModelMaxSamples)
    {
        if (list.Count > max)
        {
            list.RemoveRange(0, list.Count - max);
        }
    }

    private static bool Rejects(List<int> misses) =>
        misses.Count >= Orbit3Constants.MinCalibrationSamples
        && OrbitEnvelope.BinomialUpperTail(misses.Count, misses.Sum(), Orbit3Constants.Alpha) < Orbit3Constants.MonitorPValue;

    // 이전 표본은 지워 적합에 쓰이지 않게 하고 (표본에 시각을 따로 두지 않으므로), 새 표본이 충분히 쌓이면 구간이 다시 생깁니다.
    private void Invalidate(string disk, DiskModel m, string why)
    {
        m.InvalidatedAt = _now();
        m.Seek.Clear();
        m.Thr.Clear();
        m.SeekMisses.Clear();
        m.ThrMisses.Clear();
        m.PdhStreak = 0;
        _log.Warn($"orbit device envelope invalidated for {disk}: {why}");
    }

    private static string SourceOf(bool boot) => boot ? "bootstrap" : "stream";

    // source 가 없는 옛 파일은 stream
    private static bool IsBoot(JObject o) => o.TryGetValue("source", out object? v) && v is "bootstrap";

    private void Save()
    {
        if (!_persist)
        {
            return; // 기존 파일을 읽지도 옮기지도 못했으면 덮지 않습니다 (이번 실행은 메모리에만 둡니다)
        }

        var disks = new JObject();
        foreach ((string key, DiskModel m) in _disks)
        {
            disks.Add(key, new JObject
            {
                ["seek"] = m.Seek.Select(s => new JObject { ["ms"] = s.Ms, ["dist"] = s.Dist, ["source"] = SourceOf(s.Boot) }).ToList(),
                ["thr"] = m.Thr.Select(t => new JObject { ["mbps"] = t.Mbps, ["source"] = SourceOf(t.Boot) }).ToList(),
                ["misses"] = new JObject { ["seek"] = m.SeekMisses.ToList(), ["thr"] = m.ThrMisses.ToList() },
                ["invalidated_at"] = m.InvalidatedAt?.ToString("O", CultureInfo.InvariantCulture),
            });
        }

        try
        {
            string tmp = _path + ".tmp";
            DurableFile.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(PyJson.Dumps(new JObject { ["version"] = 1L, ["disks"] = disks })));
            File.Move(tmp, _path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn("disk model not written: " + e.GetType().Name);
        }
    }

    /// 기존 파일을 읽습니다. 깨졌으면 .bad 로 옮기고 새로 시작 (보정 없음). Persist 가 false 면 읽지 못했거나 옮기지 못한 것이라 이번 실행 동안 파일을 덮지 않습니다.
    private static (Dictionary<string, DiskModel> Disks, bool Persist) Load(string path, ILog log)
    {
        try
        {
            return (File.Exists(path) ? Parse(JsonRead.ReadFile(path)) : [], true);
        }
        catch (PyValueException)
        {
            string bad = File.Exists(path + ".bad") ? path + ".bad." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) : path + ".bad";
            try
            {
                File.Move(path, bad);
                return ([], true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Warn("disk model kept, not overwritten: " + e.GetType().Name);
                return ([], false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn("disk model not read, not overwritten: " + e.GetType().Name);
            return ([], false);
        }
    }

    private static Dictionary<string, DiskModel> Parse(object? root)
    {
        if (root is not JObject top || !top.TryGetValue("version", out object? v) || v is not 1L || !top.TryGetValue("disks", out object? d) || d is not JObject disks)
        {
            throw new PyValueException("disk model has an unexpected shape");
        }

        var result = new Dictionary<string, DiskModel>(StringComparer.Ordinal);
        foreach ((string key, object? value) in disks)
        {
            if (value is not JObject entry)
            {
                throw new PyValueException("disk entry is not an object");
            }

            var m = new DiskModel();
            foreach (JObject s in ListOf(entry, "seek").OfType<JObject>())
            {
                if (Positive(s, "ms") is { } ms && s.TryGetValue("dist", out object? dist) && Number(dist) is >= 0 and { } dd)
                {
                    m.Seek.Add((ms, (long)dd, IsBoot(s)));
                }
            }

            foreach (JObject t in ListOf(entry, "thr").OfType<JObject>())
            {
                if (Positive(t, "mbps") is { } mbps)
                {
                    m.Thr.Add((mbps, IsBoot(t)));
                }
            }
            if (entry.TryGetValue("misses", out object? misses) && misses is JObject missEntry) // 예전 배열 형식은 버림
            {
                m.SeekMisses.AddRange(ListOf(missEntry, "seek").OfType<long>().Where(x => x is 0 or 1).Select(x => (int)x));
                m.ThrMisses.AddRange(ListOf(missEntry, "thr").OfType<long>().Where(x => x is 0 or 1).Select(x => (int)x));
            }

            if (entry.TryGetValue("invalidated_at", out object? at) && at is string text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset parsed))
            {
                m.InvalidatedAt = parsed;
            }

            Trim(m.Seek);
            Trim(m.Thr);
            Trim(m.SeekMisses, Orbit3Constants.MonitorWindow);
            Trim(m.ThrMisses, Orbit3Constants.MonitorWindow);
            result[key] = m;
        }

        return result;
    }

    private static List<object?> ListOf(JObject entry, string key) => entry.TryGetValue(key, out object? v) && v is List<object?> list ? list : [];

    private static double? Number(object? v) => v switch { long l => l, double d when double.IsFinite(d) => d, _ => null };

    private static double? Positive(JObject o, string key) => o.TryGetValue(key, out object? v) && Number(v) is > 0 and { } n ? n : null;
}
