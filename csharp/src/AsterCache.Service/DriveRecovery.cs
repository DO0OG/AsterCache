using AsterCache.Core.Logging;
using AsterCache.Windows;

namespace AsterCache.Service;

/// 읽기 속도를 잰 한 곳 (LCN 과 MB/s).
public readonly record struct ProbeSpot(long Lcn, double Mbps);

/// 쓰기 직전 기준 속도: 대상 존 (비어 있어 무효면 null) 과 쓰지 않을 기준 구간 (없으면 null).
public sealed record DriveBaseline(ProbeSpot? Zone, ProbeSpot? Ref);

/// 한 표본의 판정: Pass (기준의 90% 이상), Bad (미달), Unknown (바쁨·실패·상한 초과·기준 무효). Unknown 은 연속 횟수에 넣지도 끊지도 않습니다.
public enum RecoveryVerdict
{
    Pass,
    Unknown,
    Bad,
}

/// 쓰기 (재배치) 뒤 드라이브가 회복할 때까지의 상태. probe 는 2 분마다 기준 속도와 견줘 연속 2 회 90% 이상이면 회복으로 보고, 상한 (limit) 이 지나면 회복으로 칩니다.
/// 표본은 매번 64 MiB 씩 옮겨 읽고 (HDD 자체 캐시 적중 방지), 디스크 순차 상한을 넘는 값은 캐시 적중으로 보고 판정에서 뺍니다.
/// 기준이 없는 추적 (고정 대기) 은 limit 이 지나야 끝납니다. 시각은 AutoEnv.Now, 표본은 AutoEnv.ProbeMbps (시험은 가짜). Poll 은 틱 스레드에서, IsRecovering 은 아무 스레드에서 부릅니다.
/// status 가 있으면 추적을 auto_status.json 의 drive_recovery 에 남기고, 만들어질 때 (서비스 시작) 아직 안 끝난 추적을 되살립니다. 기준이 없거나 무효인 추적은 남은 시간 동안 고정 대기합니다.
public sealed class DriveRecovery : IDriveRecovery
{
    public const double RecoveredFraction = 0.9;
    public const int RecoveredStreak = 2;
    public const double CeilingMbps = 250; // 비버퍼드 참조 기준값이 없을 때의 디스크 순차 상한이자 유효한 기준값의 한계 (넘으면 캐시 적중 의심)
    public const double CeilingFactor = 1.5; // 비버퍼드 참조 기준값이 있으면 그 값 × 이 배수
    public static readonly TimeSpan SampleEvery = TimeSpan.FromMinutes(2);

    private readonly AutoEnv _env;
    private readonly ILog _log;
    private readonly AutoStatus? _status;
    private readonly Dictionary<string, double> _warmMax = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _samples = new(StringComparer.Ordinal); // 표본을 재는 중인 드라이브별 취소 (게임 시작이 같은 디스크의 표본을 멈춤)

    public DriveRecovery(AutoEnv env, ILog log, AutoStatus? status = null)
    {
        _env = env;
        _log = log;
        _status = status;
        Restore();
    }

    /// 드라이브에서 예열이 실제로 낸 MB/s 를 알림: 진단 로그용일 뿐 판정에는 쓰이지 않습니다 (OS 캐시 적중이 섞여 있음).
    public void NoteWarmMbps(string drive, double mbps)
    {
        if (double.IsFinite(mbps) && mbps > 0)
        {
            lock (_lock)
            {
                string key = Key(drive);
                _warmMax[key] = Math.Max(_warmMax.GetValueOrDefault(key), mbps);
            }
        }
    }

    /// 그 드라이브의 순차 읽기 상한 (MB/s): 비버퍼드 참조 기준값의 1.5 배, 참조 기준이 없으면 CeilingMbps.
    public double CeilingFor(string drive)
    {
        lock (_lock)
        {
            return _baselines.TryGetValue(Key(drive), out DriveBaseline? b) && b.Ref is { } r ? r.Mbps * CeilingFactor : CeilingMbps;
        }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Track> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DriveBaseline> _baselines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _finished = new(StringComparer.Ordinal);

    private sealed class Track(DriveBaseline? baseline, DateTimeOffset started, TimeSpan limit)
    {
        public DriveBaseline? Baseline { get; } = baseline;

        public DateTimeOffset Started { get; } = started;

        public TimeSpan Limit { get; } = limit;

        public DateTimeOffset Next { get; set; } = started + SampleEvery;

        public int Streak { get; set; }

        public bool Logged { get; set; }

        public bool CacheLogged { get; set; }

        public int Rotation { get; set; } = 1; // 기준은 0 번 칸에서 쟀음

        public RecoveryVerdict? Verdict { get; set; }
    }

    public bool IsRecovering(string drive)
    {
        lock (_lock)
        {
            string key = Key(drive);
            return _active.Any(a => (a.Key == key || _env.SameDisk(a.Key, key)) && _env.Now() - a.Value.Started < a.Value.Limit); // 같은 물리 디스크의 다른 볼륨 쓰기도 이 드라이브를 느리게 함
        }
    }

    /// 쓰기를 마친 직후: baseline 이 있으면 probe, 없으면 limit 만큼 고정 대기. limit 이 0 이하면 기다리지 않음.
    public void Begin(string drive, DriveBaseline? baseline, TimeSpan limit)
    {
        if (limit <= TimeSpan.Zero)
        {
            return;
        }

        lock (_lock)
        {
            string key = Key(drive);
            if (baseline is not null)
            {
                _baselines[key] = baseline;
            }

            Add(key, new Track(baseline, _env.Now(), limit));
        }
    }

    /// 업데이트 뒤 지연 (업데이트 시각 updatedAt 기준): 이 드라이브에 쓰기 전 기준값이 있으면 probe 판정으로 아직 회복 중인지, 없으면 null (호출하는 쪽이 고정 분을 씀).
    public bool? WatchUpdate(string drive, DateTimeOffset updatedAt, TimeSpan limit)
    {
        lock (_lock)
        {
            string key = Key(drive);
            if (!_baselines.TryGetValue(key, out DriveBaseline? baseline))
            {
                return null;
            }

            if (!_active.ContainsKey(key) && _env.Now() - updatedAt < limit && !(_finished.TryGetValue(key, out DateTimeOffset done) && updatedAt <= done))
            {
                Add(key, new Track(baseline, updatedAt, limit));
            }

            return _active.TryGetValue(key, out Track? t) && _env.Now() - t.Started < t.Limit;
        }
    }

    /// 틱마다: 상한이 지난 추적을 끝내고, 표본 차례가 된 추적을 잽니다 (표본 1~2 초, 2 분마다).
    public void Poll()
    {
        DateTimeOffset now = _env.Now();
        List<(string Key, Track Track)> due = [];
        lock (_lock)
        {
            foreach ((string key, Track t) in _active.ToList())
            {
                if (now - t.Started >= t.Limit)
                {
                    Finish(key);
                    if (t.Baseline is not null)
                    {
                        _log.Info($"drive {key} recovery wait ended at the {(int)t.Limit.TotalMinutes} min limit");
                    }
                }
                else if (t.Baseline is not null && now >= t.Next)
                {
                    due.Add((key, t));
                }
            }
        }

        foreach ((string key, Track t) in due)
        {
            Step(key, t, now);
        }
    }

    private void Step(string key, Track t, DateTimeOffset now)
    {
        (double? zone, double? reference)? got = Sample(key, t.Baseline!, t.Rotation++);
        lock (_lock)
        {
            t.Next = now + SampleEvery;
            if (!_active.TryGetValue(key, out Track? cur) || cur != t)
            {
                return;
            }

            if (got is null || !ValidBaseline(t.Baseline!))
            {
                Note(key, t, RecoveryVerdict.Unknown); // 바쁘거나 못 잼 (또는 기준 무효): 연속 횟수는 그대로 (단순하게 건너뛴 회차는 세지 않음)
                return;
            }

            double ceiling = CeilingFor(key);
            if (got.Value.zone > ceiling || got.Value.reference > ceiling)
            {
                if (!t.CacheLogged)
                {
                    t.CacheLogged = true;
                    _log.Info($"drive {key} recovery sample {Show(Math.Max(got.Value.zone ?? 0, got.Value.reference ?? 0))} MB/s is above the {Show(ceiling)} MB/s sequential ceiling (disk cache hit), not counted{WarmNote(key)}");
                }

                Note(key, t, RecoveryVerdict.Unknown); // 캐시 적중: 판정에서 빼고 연속 횟수는 그대로
                return;
            }

            bool ok = Meets(t.Baseline!.Zone, got.Value.zone) && Meets(t.Baseline.Ref, got.Value.reference);
            t.Streak = ok ? t.Streak + 1 : 0;
            if (t.Streak >= RecoveredStreak)
            {
                Finish(key);
                _log.Info($"drive {key} recovered after {(int)(now - t.Started).TotalMinutes} min");
                return;
            }

            Note(key, t, ok ? RecoveryVerdict.Pass : RecoveryVerdict.Bad);
            if (!ok && !t.Logged)
            {
                t.Logged = true;
                _log.Info($"drive {key} recovering after relocation (zone {Show(got.Value.zone)} MB/s / {Show(t.Baseline.Zone?.Mbps)} baseline, ref {Show(got.Value.reference)}/{Show(t.Baseline.Ref?.Mbps)})");
            }
        }
    }

    /// 게임이 gameDrive 에서 시작했다: 같은 물리 디스크 (AutoEnv.SameDisk) 에서 재는 중인 표본을 취소합니다 (그 표본은 판정 불가).
    public void YieldTo(string gameDrive)
    {
        lock (_lock)
        {
            foreach ((string key, CancellationTokenSource cts) in _samples)
            {
                if (_env.SameDisk(gameDrive, key))
                {
                    cts.Cancel();
                }
            }
        }
    }

    // 디스크가 바쁘거나 (읽을 수 없어도) 표본이 실패하거나 취소되면 null. 존과 참조 사이에 디스크 활동을 다시 봅니다.
    private (double? Zone, double? Ref)? Sample(string key, DriveBaseline b, int rotation)
    {
        using var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _samples[key] = cts;
        }

        var round = new ProbeRound(cts.Token, (b.Zone is null ? 0 : 1) + (b.Ref is null ? 0 : 1));
        long begin = System.Diagnostics.Stopwatch.GetTimestamp();
        string? reason = null;
        DiskProbe.Current = round;
        try
        {
            if (Busy(key))
            {
                return null;
            }

            long step = _env.ProbeStepLcn(key) * (rotation % DiskProbe.RotationSteps);
            double? zone = b.Zone is { } z ? _env.ProbeMbps(key, z.Lcn + step) : null;
            if (b.Zone is not null && b.Ref is not null && zone is not null && (cts.IsCancellationRequested || Busy(key)))
            {
                reason = cts.IsCancellationRequested ? "game started" : "disk busy between samples";
                return null;
            }

            double? reference = b.Ref is { } r ? _env.ProbeMbps(key, r.Lcn + step) : null;
            return (b.Zone is not null && zone is null) || (b.Ref is not null && reference is null) ? null : (zone, reference);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
            DiskProbe.Current = null;
            lock (_lock)
            {
                _ = _samples.Remove(key);
            }

            reason ??= round.Aborted;
            if (round.BytesRead > 0 || reason is not null)
            {
                _log.Info($"drive {key} recovery sample read {round.BytesRead >> 20} MiB in {(long)System.Diagnostics.Stopwatch.GetElapsedTime(begin).TotalMilliseconds} ms" + (reason is null ? string.Empty : $", aborted ({reason})"));
            }
        }
    }

    private bool Busy(string key) => _env.DiskBytesPerSecond(key) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond;

    private static bool Meets(ProbeSpot? baseline, double? now) => baseline is not { } b || now >= b.Mbps * RecoveredFraction;

    private string WarmNote(string key) => _warmMax.TryGetValue(key, out double w) ? $" (prewarm peak {Show(w)} MB/s)" : string.Empty;

    private static string Show(double? v) => v is { } x ? Math.Round(x).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";

    private void Finish(string key)
    {
        _ = _active.Remove(key);
        _finished[key] = _env.Now();
        _status?.ClearDriveRecovery(key);
    }

    // 호출하는 쪽이 잠금을 잡고 있어야 합니다.
    private void Add(string key, Track t)
    {
        _active[key] = t;
        Save(key, t);
    }

    private void Note(string key, Track t, RecoveryVerdict verdict)
    {
        if (t.Verdict != verdict)
        {
            t.Verdict = verdict;
            Save(key, t);
        }
    }

    private void Save(string key, Track t) =>
        _status?.SetDriveRecovery(new DriveRecoveryRecord(key, t.Started, t.Started + t.Limit, t.Baseline?.Zone, t.Baseline?.Ref, t.Verdict?.ToString().ToLowerInvariant()));

    // 기준값이 있고 전부 유한한 양수이며 디스크 순차 상한 이하인가 (아니면 판정할 수 없음: 상한 초과 기준은 캐시 적중 의심).
    private static bool ValidBaseline(DriveBaseline b) =>
        (b.Zone is not null || b.Ref is not null) && Positive(b.Zone) && Positive(b.Ref);

    private static bool Positive(ProbeSpot? s) => s is not { } v || (double.IsFinite(v.Mbps) && v.Mbps > 0 && v.Mbps <= CeilingMbps);

    // 서비스 시작: 저장된 추적 중 아직 안 끝난 것을 되살립니다 (상한이 지난 것은 지움). 남은 시간은 저장된 종료 시각 (Until) 기준이고, 원래 상한보다 크면 상한으로 자릅니다 (시계가 뒤로 간 경우). 기준이 없거나 무효이면 고정 대기.
    private void Restore()
    {
        if (_status is null)
        {
            return;
        }

        DateTimeOffset now = _env.Now();
        foreach (DriveRecoveryRecord r in _status.DriveRecoveries())
        {
            string key = Key(r.Drive);
            TimeSpan limit = r.Until - r.Started;
            TimeSpan left = r.Until - now;
            DriveBaseline? baseline = new DriveBaseline(r.Zone, r.Ref) is var b && ValidBaseline(b) ? b : null;
            if (limit <= TimeSpan.Zero || left <= TimeSpan.Zero)
            {
                _finished[key] = now;
                _status.ClearDriveRecovery(r.Drive);
                continue;
            }

            left = left > limit ? limit : left; // 시계가 뒤로 가 남은 시간이 원래 상한보다 크면 상한으로 자름
            if (baseline is not null)
            {
                _baselines[key] = baseline;
            }

            var t = new Track(baseline, now, left) { Verdict = Enum.TryParse(r.Verdict, true, out RecoveryVerdict v) ? v : null };
            t.Next = now; // 시작 직후 한 번 바로 잼 (바쁘면 건너뜀)
            _active[key] = t;
            Save(key, t); // Started = now, Until = now + left 로 저장값을 맞춤
            _log.Info($"drive {key} recovery restored, {(int)left.TotalMinutes} min left ({(baseline is null ? "fixed wait" : "probe")})");
        }
    }

    private static string Key(string drive) => drive.TrimEnd('\\').ToUpperInvariant();
}
