using AsterCache.Core.Logging;

namespace AsterCache.Service;

/// 읽기 속도를 잰 한 곳 (LCN 과 MB/s).
public readonly record struct ProbeSpot(long Lcn, double Mbps);

/// 쓰기 직전 기준 속도: 대상 존 (비어 있어 무효면 null) 과 쓰지 않을 기준 구간 (없으면 null).
public sealed record DriveBaseline(ProbeSpot? Zone, ProbeSpot? Ref);

/// 쓰기 (재배치) 뒤 드라이브가 회복할 때까지의 상태. probe 는 2 분마다 기준 속도와 견줘 연속 2 회 90% 이상이면 회복으로 보고, 상한 (limit) 이 지나면 회복으로 칩니다.
/// 기준이 없는 추적 (고정 대기) 은 limit 이 지나야 끝납니다. 시각은 AutoEnv.Now, 표본은 AutoEnv.ProbeMbps (시험은 가짜). Poll 은 틱 스레드에서, IsRecovering 은 아무 스레드에서 부릅니다.
public sealed class DriveRecovery(AutoEnv env, ILog log) : IDriveRecovery
{
    public const double RecoveredFraction = 0.9;
    public const int RecoveredStreak = 2;
    public static readonly TimeSpan SampleEvery = TimeSpan.FromMinutes(2);

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
    }

    public bool IsRecovering(string drive)
    {
        lock (_lock)
        {
            return _active.TryGetValue(Key(drive), out Track? t) && env.Now() - t.Started < t.Limit;
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

            _active[key] = new Track(baseline, env.Now(), limit);
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

            if (!_active.ContainsKey(key) && env.Now() - updatedAt < limit && !(_finished.TryGetValue(key, out DateTimeOffset done) && updatedAt <= done))
            {
                _active[key] = new Track(baseline, updatedAt, limit);
            }

            return _active.TryGetValue(key, out Track? t) && env.Now() - t.Started < t.Limit;
        }
    }

    /// 틱마다: 상한이 지난 추적을 끝내고, 표본 차례가 된 추적을 잽니다 (표본 1~2 초, 2 분마다).
    public void Poll()
    {
        DateTimeOffset now = env.Now();
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
                        log.Info($"drive {key} recovery wait ended at the {(int)t.Limit.TotalMinutes} min limit");
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
        (double? zone, double? reference)? got = Sample(key, t.Baseline!);
        lock (_lock)
        {
            t.Next = now + SampleEvery;
            if (got is null || !_active.TryGetValue(key, out Track? cur) || cur != t)
            {
                return; // 바쁘거나 못 잼: 연속 횟수는 그대로 (단순하게 건너뛴 회차는 세지 않음)
            }

            bool ok = Meets(t.Baseline!.Zone, got.Value.zone) && Meets(t.Baseline.Ref, got.Value.reference);
            t.Streak = ok ? t.Streak + 1 : 0;
            if (t.Streak >= RecoveredStreak)
            {
                Finish(key);
                log.Info($"drive {key} recovered after {(int)(now - t.Started).TotalMinutes} min");
            }
            else if (!ok && !t.Logged)
            {
                t.Logged = true;
                log.Info($"drive {key} recovering after relocation (zone {Show(got.Value.zone)} MB/s / {Show(t.Baseline.Zone?.Mbps)} baseline, ref {Show(got.Value.reference)}/{Show(t.Baseline.Ref?.Mbps)})");
            }
        }
    }

    // 디스크가 바쁘거나 (읽을 수 없어도) 표본이 실패하면 null.
    private (double? Zone, double? Ref)? Sample(string key, DriveBaseline b)
    {
        try
        {
            if (env.DiskBytesPerSecond(key) is not { } bps || bps >= DiskActivity.BusyBytesPerSecond)
            {
                return null;
            }

            double? zone = b.Zone is { } z ? env.ProbeMbps(key, z.Lcn) : null;
            double? reference = b.Ref is { } r ? env.ProbeMbps(key, r.Lcn) : null;
            return (b.Zone is not null && zone is null) || (b.Ref is not null && reference is null) ? null : (zone, reference);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return null;
        }
    }

    private static bool Meets(ProbeSpot? baseline, double? now) => baseline is not { } b || now >= b.Mbps * RecoveredFraction;

    private static string Show(double? v) => v is { } x ? Math.Round(x).ToString(System.Globalization.CultureInfo.InvariantCulture) : "-";

    private void Finish(string key)
    {
        _ = _active.Remove(key);
        _finished[key] = env.Now();
    }

    private static string Key(string drive) => drive.TrimEnd('\\').ToUpperInvariant();
}
