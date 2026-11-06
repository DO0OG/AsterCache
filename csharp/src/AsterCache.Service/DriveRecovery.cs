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
    public const double CeilingFactor = 1.25; // 비버퍼드 참조 기준값이 있으면 (존 기준이 더 크면 그 값) × 이 배수 (CeilingMbps 를 넘지 않음)
    public const double SuspectFactor = 1.1; // 쓰기 직후 SuspectWindow 안의 표본이 기준의 이 배수를 넘으면 디스크 캐시 의심
    public static readonly TimeSpan SuspectWindow = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan SampleEvery = TimeSpan.FromMinutes(2);

    private readonly AutoEnv _env;
    private readonly ILog _log;
    private readonly AutoStatus? _status;
    private readonly Dictionary<string, double> _warmMax = new(StringComparer.Ordinal);
    private readonly HashSet<string> _gameLogged = new(StringComparer.Ordinal); // "game running" 로 표본을 미룬 드라이브 (게임이 끝날 때까지 로그는 한 번)
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

    /// 그 드라이브의 순차 읽기 상한 (MB/s): 유효한 기준값 (존과 참조 중 큰 쪽, 존이 참조보다 빠른 디스크가 있어서) 의 1.25 배 (최대 CeilingMbps), 유효한 기준이 하나도 없으면 CeilingMbps (그때는 판정하지 않고 고정 대기).
    public double CeilingFor(string drive)
    {
        lock (_lock)
        {
            double top = _baselines.TryGetValue(Key(drive), out DriveBaseline? b) ? Math.Max(Valid(b.Zone), Valid(b.Ref)) : 0;
            return top > 0 ? Math.Min(top * CeilingFactor, CeilingMbps) : CeilingMbps;
        }
    }

    private readonly object _lock = new();
    private readonly Dictionary<string, Track> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DriveBaseline> _baselines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _finished = new(StringComparer.Ordinal);

    // 추적의 세션 안 경과는 Stopwatch 로 잰다 (실행 중 시스템 시계가 튀어도 회복 대기가 일찍 끝나거나 늘어나지 않게). offset 은 만들 때 이미 지난 시간 (업데이트 시각 기준 추적).
    private sealed class Track(DriveBaseline? baseline, DateTimeOffset started, TimeSpan limit, TimeSpan offset, long startTicks, TimeSpan hardLimit)
    {
        /// 추가 재배치를 보류하는 절대 상한 (Limit 이상). 기준이 없는 고정 대기는 Limit 과 같음.
        public TimeSpan HardLimit { get; } = hardLimit > limit ? hardLimit : limit;

        /// Limit 에 이르렀지만 회복을 확인하지 못해 판정을 unknown 으로 남기고 HardLimit 까지 계속 보류 중.
        public bool Expired { get; set; }

        public TimeSpan Offset { get; } = offset;

        public long StartTicks { get; } = startTicks;

        public DriveBaseline? Baseline { get; } = baseline;

        public DateTimeOffset Started { get; } = started;

        public TimeSpan Limit { get; } = limit;

        public TimeSpan Next { get; set; } = SampleEvery; // 추적 경과 기준

        public int Streak { get; set; }

        public bool Logged { get; set; }

        public bool CacheLogged { get; set; }

        public int Rotation { get; set; } = 1; // 기준은 0 번 칸에서 쟀음

        public RecoveryVerdict? Verdict { get; set; }

        /// 같은 물리 디스크에서 게임이 도는 동안 경과를 멈춘 시작 시점 (Stopwatch 틱), 멈추지 않았으면 null. 멈춘 시간은 Paused 에 쌓여 경과에서 빠짐.
        public long? PausedAt { get; set; }

        public TimeSpan Paused { get; set; }

        public DateTimeOffset? PausedSince { get; set; } // 멈춘 시점의 시계 (저장용)
    }

    private Track NewTrack(DriveBaseline? baseline, DateTimeOffset started, TimeSpan limit, TimeSpan? hardLimit = null) =>
        new(baseline, started, limit, TimeSpan.FromTicks(Math.Max(0, (_env.Now() - started).Ticks)), _env.Ticks(), baseline is null ? limit : hardLimit ?? limit);

    private TimeSpan Elapsed(Track t) => t.Offset + System.Diagnostics.Stopwatch.GetElapsedTime(t.StartTicks, t.PausedAt ?? _env.Ticks()) - t.Paused;

    public bool IsRecovering(string drive)
    {
        lock (_lock)
        {
            string key = Key(drive);
            return _active.Any(a => (a.Key == key || _env.SameDisk(a.Key, key)) && Elapsed(a.Value) < a.Value.Limit); // 같은 물리 디스크의 다른 볼륨 쓰기도 이 드라이브를 느리게 함
        }
    }

    public bool IsRelocationHeld(string drive)
    {
        lock (_lock)
        {
            string key = Key(drive);
            return _active.Any(a => (a.Key == key || _env.SameDisk(a.Key, key)) && Elapsed(a.Value) < a.Value.HardLimit);
        }
    }

    /// 쓰기를 마친 직후: baseline 이 있으면 probe, 없으면 limit 만큼 고정 대기. limit 이 0 이하면 기다리지 않음.
    /// hardLimit (probe 만): limit 에 이르러도 회복을 확인하지 못하면 판정을 unknown 으로 남기고 이 시간까지 추가 재배치를 계속 보류합니다 (IsRelocationHeld). 생략하면 limit 과 같음.
    public void Begin(string drive, DriveBaseline? baseline, TimeSpan limit, TimeSpan? hardLimit = null)
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

            Add(key, NewTrack(baseline, _env.Now(), limit, hardLimit));
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
                Add(key, NewTrack(baseline, updatedAt, limit));
            }

            return _active.TryGetValue(key, out Track? t) && Elapsed(t) < t.Limit;
        }
    }

    /// 틱마다: 상한이 지난 추적을 끝내고, 표본 차례가 된 추적을 잽니다 (표본 1~2 초, 2 분마다).
    public void Poll()
    {
        List<(string Key, Track Track)> due = [];
        lock (_lock)
        {
            foreach ((string key, Track t) in _active.ToList())
            {
                bool protectedNow = GameRunning(key);
                SetPaused(key, t, protectedNow); // 게임이 도는 동안은 플레이 시간이 상한을 깎지 않게 경과를 멈춤
                TimeSpan elapsed = Elapsed(t);
                if (elapsed >= t.HardLimit)
                {
                    Finish(key);
                    if (t.Baseline is not null)
                    {
                        _log.Info(t.HardLimit > t.Limit
                            ? $"drive {key} recovery not confirmed after the {(int)t.HardLimit.TotalMinutes} min hard limit, relocation hold released"
                            : $"drive {key} recovery wait ended at the {(int)t.Limit.TotalMinutes} min limit");
                    }

                    continue;
                }

                if (elapsed >= t.Limit && !t.Expired)
                {
                    t.Expired = true;
                    t.Verdict = RecoveryVerdict.Unknown; // 회복을 확인하지 못한 채 상한: 판정은 unknown, 표본은 계속 재서 회복하면 풀림
                    Save(key, t);
                    _log.Info($"drive {key} recovery not confirmed at the {(int)t.Limit.TotalMinutes} min limit, relocation stays on hold until recovered or {(int)t.HardLimit.TotalMinutes} min");
                }

                if (t.Baseline is not null && elapsed >= t.Next && !protectedNow)
                {
                    due.Add((key, t));
                }
            }
        }

        foreach ((string key, Track t) in due)
        {
            Step(key, t);
        }
    }

    private void Step(string key, Track t)
    {
        (double? zone, double? reference)? got = Sample(key, t.Baseline!, t.Rotation, out bool deferred);
        lock (_lock)
        {
            if (deferred)
            {
                return; // 게임 때문에 미룬 회차: 다음 시각·칸 순서·연속 횟수를 쓰지 않고 게임이 끝난 뒤 처음부터
            }

            t.Rotation++;
            TimeSpan now = Elapsed(t);
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

            if (now < SuspectWindow && (Suspect(t.Baseline!.Zone, got.Value.zone) || Suspect(t.Baseline.Ref, got.Value.reference)))
            {
                if (!t.CacheLogged)
                {
                    t.CacheLogged = true;
                    _log.Info($"drive {key} recovery sample (zone {Show(got.Value.zone)}, ref {Show(got.Value.reference)} MB/s) is over {SuspectFactor}x the baseline right after the write (disk cache suspected), not counted");
                }

                Note(key, t, RecoveryVerdict.Unknown); // 쓰기 직후 기준보다 빠름: 캐시 의심, 판정에서 빼고 연속 횟수는 그대로
                return;
            }

            bool ok = Meets(t.Baseline!.Zone, got.Value.zone) && Meets(t.Baseline.Ref, got.Value.reference);
            t.Streak = ok ? t.Streak + 1 : 0;
            if (t.Streak >= RecoveredStreak)
            {
                Finish(key);
                _log.Info($"drive {key} recovered after {(int)now.TotalMinutes} min");
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
            foreach ((string key, Track t) in _active)
            {
                if (_env.SameDisk(gameDrive, key))
                {
                    SetPaused(key, t, true); // 시작 알림이 감시 주기보다 빠르므로 여기서 먼저 멈춤 (Poll 이 실제 실행 여부로 다시 판정)
                }
            }

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
    // deferred: 게임이 돌거나 시작해서 시작하지 않았거나 멈춘 회차 (판정 불가이고 회차를 쓰지 않음).
    private (double? Zone, double? Ref)? Sample(string key, DriveBaseline b, int rotation, out bool deferred)
    {
        deferred = false;
        using var cts = new CancellationTokenSource();
        lock (_lock)
        {
            _samples[key] = cts;
        }

        var round = new ProbeRound(cts.Token, (b.Zone is null ? 0 : 1) + (b.Ref is null ? 0 : 1));
        long begin = System.Diagnostics.Stopwatch.GetTimestamp();
        string? reason = null;
        try
        {
            if (GameRunning(key))
            {
                deferred = true;
                lock (_lock)
                {
                    reason = _gameLogged.Add(key) ? "game running" : null; // 게임이 도는 동안은 표본을 시작하지 않음 (처리량이 0 이어도)
                }

                return null;
            }

            lock (_lock)
            {
                _ = _gameLogged.Remove(key);
            }

            if (Busy(_env, key))
            {
                return null;
            }

            long step = _env.ProbeStepLcn(key) * DiskProbe.RotationIndex(rotation); // 0 번 칸은 기준 측정이 읽은 곳이라 건너뜀
            double? zone = b.Zone is { } z ? _env.ProbeMbps(key, z.Lcn + step, round) : null;
            if (b.Zone is not null && b.Ref is not null && zone is not null && (cts.IsCancellationRequested || GameRunning(key) || Busy(_env, key)))
            {
                reason = cts.IsCancellationRequested ? "game started" : GameRunning(key) ? "game running" : "disk busy between samples";
                deferred = reason != "disk busy between samples";
                return null;
            }

            double? reference = b.Ref is { } r ? _env.ProbeMbps(key, r.Lcn + step, round) : null;
            deferred = cts.IsCancellationRequested;
            return (b.Zone is not null && zone is null) || (b.Ref is not null && reference is null) ? null : (zone, reference);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
        finally
        {
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

    // 디스크 활동을 못 읽거나 임계 이상이면 바쁨 (기준 측정도 같은 판정을 씀).
    internal static bool Busy(AutoEnv env, string key) => env.DiskBytesPerSecond(key) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond;

    // 같은 물리 디스크에서 등록 게임이 도는 중인가. 판정 함수가 연결되지 않았거나 조회가 실패하면 보호 중으로 봄 (게임 디스크를 읽지 않는 쪽).
    private bool GameRunning(string key)
    {
        try
        {
            return _env.GameRunningOnDisk?.Invoke(key) ?? true;
        }
        catch (Exception)
        {
            return true;
        }
    }

    // 호출하는 쪽이 잠금을 잡고 있어야 합니다. 멈추거나 이어갈 때만 저장 (멈춘 동안 재시작해도 플레이 시간이 다시 깎이지 않게 잔여 기준으로 남김).
    private void SetPaused(string key, Track t, bool paused)
    {
        if (paused == (t.PausedAt is not null))
        {
            return;
        }

        if (paused)
        {
            t.PausedAt = _env.Ticks();
            t.PausedSince = _env.Now();
            _log.Info($"drive {key} recovery clock paused (game running)");
        }
        else
        {
            t.Paused += System.Diagnostics.Stopwatch.GetElapsedTime(t.PausedAt!.Value, _env.Ticks());
            t.PausedAt = null;
            t.PausedSince = null;
            _ = _gameLogged.Remove(key);
            _log.Info($"drive {key} recovery clock resumed, {(int)(t.HardLimit - Elapsed(t)).TotalMinutes} min left");
        }

        Save(key, t);
    }

    private static bool Suspect(ProbeSpot? baseline, double? now) => baseline is { } b && now > b.Mbps * SuspectFactor;

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
        if (t.Expired)
        {
            return; // 상한 뒤에는 판정을 unknown 으로 남김
        }

        if (t.Verdict != verdict)
        {
            t.Verdict = verdict;
            Save(key, t);
        }
    }

    // Started 는 멈춘 시간을 뺀 가상 시작: 멈춘 동안은 멈춘 시점 (PausedSince) 기준으로 남아 잔여 시간이 시계를 따라 줄지 않음.
    private void Save(string key, Track t)
    {
        DateTimeOffset start = t.PausedSince is { } p ? p - Elapsed(t) : t.Started + t.Paused;
        _status?.SetDriveRecovery(new DriveRecoveryRecord(key, start, start + t.Limit, t.Baseline?.Zone, t.Baseline?.Ref, t.Verdict?.ToString().ToLowerInvariant(), t.HardLimit > t.Limit ? start + t.HardLimit : null, t.PausedSince));
    }

    // 기준값이 있고 전부 유한한 양수이며 디스크 순차 상한 이하인가 (아니면 판정할 수 없음: 상한 초과 기준은 캐시 적중 의심).
    private static bool ValidBaseline(DriveBaseline b) =>
        (b.Zone is not null || b.Ref is not null) && Positive(b.Zone) && Positive(b.Ref);

    private static bool Positive(ProbeSpot? s) => s is null || Valid(s) > 0;

    // 유효한 기준 구간의 MB/s, 없거나 무효이면 0.
    private static double Valid(ProbeSpot? s) => s is { } v && double.IsFinite(v.Mbps) && v.Mbps > 0 && v.Mbps <= CeilingMbps ? v.Mbps : 0;

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
            DateTimeOffset at = r.PausedSince ?? now; // 멈춘 채 저장됐으면 그 시점의 잔여 시간 (멈춘 동안 지난 시간은 깎지 않음)
            TimeSpan limit = r.Until - r.Started;
            TimeSpan left = r.Until - at;
            DriveBaseline? baseline = new DriveBaseline(r.Zone, r.Ref) is var b && ValidBaseline(b) ? b : null;
            TimeSpan hardLimit = baseline is not null && r.HardUntil is { } hardUntil ? hardUntil - r.Started : limit; // 상한 뒤에도 보류 중이던 추적은 절대 상한까지 되살림
            TimeSpan hardLeft = baseline is not null && r.HardUntil is { } hu ? hu - at : left;
            if (hardLeft <= TimeSpan.Zero) // 상한 잔여가 0 이어도 절대 상한이 남았으면 되살림 (Until == Started 로 저장된 만료 추적 포함)
            {
                _finished[key] = now;
                _status.ClearDriveRecovery(r.Drive);
                continue;
            }

            left = left > limit ? limit : left; // 시계가 뒤로 가 남은 시간이 원래 상한보다 크면 상한으로 자름
            hardLeft = hardLeft > hardLimit ? hardLimit : hardLeft;
            left = left < TimeSpan.Zero ? TimeSpan.Zero : left; // 상한은 지났지만 절대 상한 전: 이미 만료된 추적
            if (baseline is not null)
            {
                _baselines[key] = baseline;
            }

            Track t = NewTrack(baseline, now, left, hardLeft);
            t.Verdict = Enum.TryParse(r.Verdict, true, out RecoveryVerdict v) ? v : null;
            t.Expired = left == TimeSpan.Zero && baseline is not null;
            t.Next = TimeSpan.Zero; // 시작 직후 한 번 바로 잼 (바쁘면 건너뜀)
            if (r.PausedSince is { } pausedSince)
            {
                (t.PausedAt, t.PausedSince) = (_env.Ticks(), pausedSince); // 멈춘 채 저장됐으면 첫 Poll 의 실행 판정까지 멈춤 유지 (그 전에 다시 종료해도 PausedSince 를 잃지 않음)
            }

            _active[key] = t;
            Save(key, t); // Started = now, Until = now + left 로 저장값을 맞춤 (멈춘 채면 PausedSince 기준이라 저장값 그대로)
            _log.Info($"drive {key} recovery restored, {(int)hardLeft.TotalMinutes} min left ({(baseline is null ? "fixed wait" : "probe")})");
        }
    }

    private static string Key(string drive) => drive.TrimEnd('\\').ToUpperInvariant();
}
