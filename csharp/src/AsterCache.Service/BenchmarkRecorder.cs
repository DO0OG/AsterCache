using System.Globalization;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Metrics;

namespace AsterCache.Service;

/// 로딩 표본 기록기 (<데이터 폴더>\benchmark_samples.json, 최근 MaxSamples 건). ZC-10 기록 틀이며 실측 값은 아직 없습니다.
/// 게임 시작 때 잰 캐시 표본 도달 시각은 실제 준비 시각이 아니므로 ProbeReadyMs 대리 지표로만 남기고, TReadyMs 는 항상 null (unobserved) 입니다.
/// 경과 구간은 서비스 가동 시간 기준입니다 (설치 후 경과는 알 수 없음). 기록은 LoadBoundary 감시 스레드에서만 부릅니다.
public sealed class BenchmarkRecorder
{
    public const int MaxSamples = 200;

    private readonly object _lock = new();
    private readonly string _path;
    private readonly ILog _log;
    private readonly DateTimeOffset _serviceStart;
    private readonly List<LoadSample> _samples;
    private readonly bool _persist; // false 면 기존 파일을 읽거나 옮기지 못한 상태라 파일을 덮지 않습니다

    private readonly List<(LoadSample Sample, DateTimeOffset At)> _gateSamples = []; // 이번 실행에서 기록한 표본과 시각 (판정용, 파일에는 쓰지 않음)

    public Action<string, GateResult>? GateEvaluated { get; set; }

    /// 판정에 쓸 표본의 하한 시각 (마지막 모드 변경 시각): 그 이전 표본은 판정에서 빼서, 되돌린 직후 옛 표본으로 다시 강등되지 않게 합니다. null 이면 제한 없음.
    public Func<DateTimeOffset?>? GateSince { get; set; }

    public BenchmarkRecorder(string path, ILog log, DateTimeOffset? serviceStart = null)
    {
        _path = path;
        _log = log;
        _serviceStart = serviceStart ?? DateTimeOffset.UtcNow;
        (_samples, _persist) = Load(path, log); // 서비스 시작 때 한 번만 읽고, 이후에는 메모리의 목록을 씁니다
    }

    /// 서비스 가동 시간 (5분 이후는 모두 Min5).
    public static ElapsedBucket BucketOf(TimeSpan sinceServiceStart) => sinceServiceStart switch
    {
        { TotalSeconds: < 10 } => ElapsedBucket.Sec0,
        { TotalSeconds: < 30 } => ElapsedBucket.Sec10,
        { TotalSeconds: < 60 } => ElapsedBucket.Sec30,
        { TotalSeconds: < 300 } => ElapsedBucket.Sec60,
        _ => ElapsedBucket.Min5,
    };

    /// 경계 감시 한 회차가 끝날 때 한 건. probeReadyMs 는 도달했을 때만 값이 있고, 미도달이나 측정 불가면 null.
    public void Record(string game, DateTimeOffset gameStart, long? probeReadyMs, Arm arm = Arm.Baseline)
    {
        // 시나리오는 아직 알 수 없으므로 S0 로 고정하지 않고 Unknown 으로 남깁니다
        Record(new LoadSample(game, Scenario.Unknown, arm, BucketOf(gameStart - _serviceStart), null, ProbeReadyMs: probeReadyMs, ElapsedBasis: "service_uptime"));
    }

    public void Record(LoadSample sample)
    {
        GateResult? rejected;
        lock (_lock)
        {
            _samples.Add(sample);
            _gateSamples.Add((sample, DateTimeOffset.UtcNow));
            if (_gateSamples.Count > MaxSamples)
            {
                _gateSamples.RemoveRange(0, _gateSamples.Count - MaxSamples);
            }

            if (_samples.Count > MaxSamples)
            {
                _samples.RemoveRange(0, _samples.Count - MaxSamples);
            }

            if (_persist)
            {
                try
                {
                    string tmp = _path + ".tmp";
                    DurableFile.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(MetricsJson.DumpSamples(_samples)));
                    File.Move(tmp, _path, true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    _log.Warn("benchmark samples not written: " + e.GetType().Name);
                }
            }

            rejected = EvaluateReadyLocked(sample.GameKey);
        }

        if (rejected is not null)
        {
            GateEvaluated?.Invoke(sample.GameKey, rejected);
        }
    }

    // 주의: 실제 준비 시각 관측이 없어 (TReadyMs·GameBuild 가 null) 현재 서비스 표본으로는 판정이 생기지 않습니다. 준비 시각 관측이 붙은 뒤에야 동작합니다.
    private GateResult? EvaluateReadyLocked(string game)
    {
        DateTimeOffset? since = GateSince?.Invoke();
        foreach (IGrouping<string, LoadSample> build in _gateSamples.Where(g => g.Sample.GameKey == game && g.Sample.GameBuild is not null && (since is null || g.At > since)).Select(g => g.Sample).GroupBy(s => s.GameBuild!, StringComparer.Ordinal))
        {
            LoadSample[] samples = [.. build];
            LoadSample[] baseline = [.. samples.Where(s => s.Arm == Arm.Baseline && s.TReadyMs is >= 0)];
            LoadSample[] candidate = [.. samples.Where(s => s.Arm == Arm.Candidate && s.TReadyMs is >= 0)];
            if (baseline.Length < ResultGate.MinSamples || candidate.Length < ResultGate.MinSamples)
            {
                continue;
            }

            GateResult result = ResultGate.Evaluate(baseline, candidate);
            if (result.Verdict == ResultGate.Reject)
            {
                return result;
            }
        }

        return null;
    }

    /// 기존 기록을 읽습니다. Persist 가 false 면 읽지 못했거나 깨진 파일을 옮기지 못한 것이라 이번 실행 동안 파일을 덮지 않습니다.
    private static (List<LoadSample> Samples, bool Persist) Load(string path, ILog log)
    {
        try
        {
            List<LoadSample> samples = File.Exists(path) ? MetricsJson.ParseSamples(ReadShared(path)).TakeLast(MaxSamples).ToList() : [];
            return (samples, true);
        }
        catch (PyValueException)
        {
            // 파일 전체가 JSON 이 아니면 .bad 로 옮겨 보관하고 새로 시작합니다 (항목 하나의 오류는 ParseSamples 가 그 항목만 뺍니다). 옛 .bad 는 덮지 않고 시각을 붙입니다
            string bad = File.Exists(path + ".bad") ? path + ".bad." + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) : path + ".bad";
            try
            {
                File.Move(path, bad);
                return ([], true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log.Warn("benchmark samples kept, not overwritten: " + e.GetType().Name);
                return ([], false);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log.Warn("benchmark samples not read, not overwritten: " + e.GetType().Name);
            return ([], false);
        }
    }

    /// 쓰는 중이거나 교체 중인 파일도 읽도록 삭제 공유를 허용합니다 (File.ReadAllText 는 교체와 경합하면 실패합니다).
    private static string ReadShared(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd();
    }
}
