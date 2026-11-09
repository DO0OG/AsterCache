using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Prefetch;
using AsterCache.Core.Settings;

namespace AsterCache.Service;

public sealed class OrbitRuntime
{
    public const string ModeFileName = "orbit_mode.json";

    private readonly string _modePath;
    private readonly OrbitCalibration? _calibration;
    private readonly AutoStatus? _status;
    private readonly ILog _log;
    private readonly bool _enabled;
    private readonly Func<GameSettings, CancellationToken, OrbitModelSnapshot> _loadModel;
    private readonly Func<GameSettings, DeviceFit?> _device;
    private readonly Func<GameSettings, long> _headBytes;
    private readonly Func<Orbit3Input, CancellationToken, Orbit3Decision> _decide;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, int> _exceptionStreak = new(StringComparer.OrdinalIgnoreCase); // 게임별 연속 계산 예외 횟수
    private readonly object _modeLock = new();
    private (DateTime Stamp, long Length, string Mode)? _modeCache; // 수정 시각과 길이가 같으면 파일을 다시 읽지 않음
    private bool _modeFileSeen; // 모드 파일이 있던 것을 본 적이 있는지
    private DateTimeOffset? _modeMissingSince; // 파일이 사라진 것을 처음 관측한 시각

    public OrbitRuntime(string dataDir, OrbitCalibration? calibration, AutoStatus? status, ILog log, bool enabled = true,
        Func<GameSettings, CancellationToken, OrbitModelSnapshot>? loadModel = null, Func<GameSettings, DeviceFit?>? device = null,
        Func<GameSettings, long>? headBytes = null, Func<Orbit3Input, CancellationToken, Orbit3Decision>? decide = null,
        Func<DateTimeOffset>? now = null)
    {
        _modePath = Path.Combine(dataDir, ModeFileName);
        _calibration = calibration;
        _status = status;
        _log = log;
        _enabled = enabled;
        _loadModel = loadModel ?? ((game, token) =>
        {
            token.ThrowIfCancellationRequested();
            OrbitModelSnapshot snapshot = OrbitModel.Load(game, AutoTracer.MinExtraRunBytes);
            token.ThrowIfCancellationRequested();
            return snapshot;
        });
        _device = device ?? (game => _calibration?.Envelope(_calibration.KeyOf(game)));
        _headBytes = headBytes ?? PrefetchStreamer.HeadBytesOf;
        _decide = decide ?? Orbit3.Decide;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public string ModePath => _modePath;

    public string Mode => ReadMode();

    /// 모드 파일을 마지막으로 바꾼 시각입니다. 결과 판정이 그 이후 표본만 쓰게 하는 기준입니다.
    /// 파일이 있었다가 사라졌으면 사라진 것을 처음 관측한 시각이고, 처음부터 없으면 null 입니다.
    public DateTimeOffset? ModeChangedAt => ObserveModeFile(File.Exists(_modePath));

    private DateTimeOffset? ObserveModeFile(bool exists)
    {
        lock (_modeLock)
        {
            if (exists)
            {
                _modeFileSeen = true;
                _modeMissingSince = null;
                return new DateTimeOffset(File.GetLastWriteTimeUtc(_modePath), TimeSpan.Zero);
            }

            if (_modeFileSeen)
            {
                _modeMissingSince ??= _now();
            }

            return _modeMissingSince;
        }
    }

    public List<ReadOp> Reorder(GameSettings game, List<ReadOp> ops) => Reorder(game, ops, CancellationToken.None);

    internal List<ReadOp> Reorder(GameSettings game, List<ReadOp> ops, CancellationToken cancellationToken)
    {
        if (!_enabled)
        {
            return ops;
        }

        string mode = ReadMode(); // 결정 한 번에 한 번만 읽음
        if (mode == "off")
        {
            return ops; // off 는 스트림마다 상태 파일 쓰기·로그를 남기지 않음
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Orbit3Constants.DecisionBudgetMs);
        CancellationToken token = budget.Token;
        var clock = Stopwatch.StartNew();
        OrbitModelSnapshot? snapshot = null;
        DeviceFit? fit = null;
        OrbitSegmentSet? set = null;
        Orbit3Decision? decision = null;
        int blocks = 0;
        string reason;
        try
        {
            token.ThrowIfCancellationRequested();
            fit = _device(game);
            if (fit is null)
            {
                reason = "no_device_calibration";
                ResetExceptionStreak(game);
                return FinishBaseline(game, ops, mode, reason, fit, snapshot, decision, set, blocks, clock);
            }

            snapshot = _loadModel(game, token);
            token.ThrowIfCancellationRequested();
            if (snapshot.Episodes.Count == 0)
            {
                reason = "no_episode_model";
                ResetExceptionStreak(game);
                return FinishBaseline(game, ops, mode, reason, fit, snapshot, decision, set, blocks, clock);
            }

            if (snapshot.LambdaLow is not { } lambdaLow || snapshot.LambdaHat is not { } lambdaHat || snapshot.LambdaHigh is not { } lambdaHigh)
            {
                reason = "no_hazard_model";
                ResetExceptionStreak(game);
                return FinishBaseline(game, ops, mode, reason, fit, snapshot, decision, set, blocks, clock);
            }

            set = OrbitSegments.Build(ops, _headBytes(game));
            (List<Orbit3Episode> episodes, int totalBlocks, double confidence) = MapEpisodes(snapshot, set, ops, token);
            var device = new Orbit3Device(fit.SeekHatMs, fit.ThroughputHatMBps * 1000,
                fit.Envelope.SeekMin, fit.Envelope.SeekMax, fit.Envelope.ThroughputMin, fit.Envelope.ThroughputMax);
            var input = new Orbit3Input(set, episodes, lambdaLow, lambdaHat, lambdaHigh, device, confidence);
            blocks = totalBlocks;
            decision = _decide(input, token);
            if (decision.Reason == "calculation_error")
            {
                if (OnCalculationException(game, "calculation_error"))
                {
                    mode = "shadow";
                }
            }
            else
            {
                ResetExceptionStreak(game);
            }

            token.ThrowIfCancellationRequested();
            bool shouldApply = mode == "active" && decision.Order is not null;
            List<ReadOp> result = shouldApply ? OrbitApply.Reorder(ops, set, decision.Order) : ops;
            bool applied = !ReferenceEquals(result, ops);
            reason = decision.Reason;
            if (!PersistDecision(game, mode))
            {
                result = ops;
                applied = false;
                reason = "decision_record_failed";
            }

            Record(game, mode, applied, reason, fit, snapshot, decision, set, blocks, clock.Elapsed.TotalMilliseconds);
            return result;
        }
        catch (OperationCanceledException)
        {
            ResetExceptionStreak(game);
            reason = cancellationToken.IsCancellationRequested ? "cancelled" : "timeout";
            return FinishBaseline(game, ops, mode, reason, fit, snapshot, decision, set, blocks, clock);
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            if (OnCalculationException(game, "calculation_error"))
            {
                mode = "shadow";
            }

            _log.Warn($"{game.Name}: orbit decision failed ({e.GetType().Name})");
            return FinishBaseline(game, ops, mode, "calculation_error", fit, snapshot, decision, set, blocks, clock);
        }
    }

    public bool Demote(string reason)
    {
        if (!_enabled || ReadMode() != "active")
        {
            return false;
        }

        try
        {
            var root = new JObject
            {
                ["mode"] = "shadow",
                ["demoted_at"] = _now().ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["reason"] = reason,
            };
            WriteAtomic(_modePath, root);
            lock (_modeLock)
            {
                _modeCache = null;
            }

            _log.Warn($"orbit mode changed to shadow: {reason}");
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _log.Warn("orbit mode was not changed: " + e.GetType().Name);
            return false;
        }
    }

    public static void WriteMode(string path, string mode)
    {
        if (mode is not ("off" or "shadow" or "active"))
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }

        WriteAtomic(path, new JObject { ["mode"] = mode });
    }

    public static Func<GameSettings, bool> GameActiveOnDisk(IReadOnlyList<GameSettings> games,
        Func<string, string, bool> sameDisk, Func<GameSettings, bool> gameRunning) => game =>
        games.Any(other => sameDisk(game.OriginRoot, other.OriginRoot) && gameRunning(other));

    /// 벤치 후보 결과: 순서 (없으면 null), 이유 (후보가 없을 때 왜 없는지), 위험률 출처 (model | bench_override | 위험률 단계 전이면 null)와 분당 값.
    public sealed record ForcedCandidate(List<ReadOp>? Ops, string Reason, string? LambdaSource = null, double? LambdaPerMin = null);

    public static ForcedCandidate ForceCandidateOps(GameSettings game, List<ReadOp> incumbent, double lambdaPerMin)
    {
        string plansDir = Path.GetDirectoryName(game.Plan) ?? string.Empty;
        string dataDir = Path.GetDirectoryName(plansDir) ?? plansDir;
        var calibration = new OrbitCalibration(dataDir, QuietLog.Instance, (_, _, _, _) => null,
            () => DateTimeOffset.UtcNow, _ => false, _ => false, Random.Shared, diskKey: OrbitCalibration.PhysicalDiskKey);
        var runtime = new OrbitRuntime(dataDir, calibration, null, QuietLog.Instance);
        return runtime.ForceCandidate(game, incumbent, lambdaPerMin);
    }

    // 벤치 후보 전용입니다. 위험률이 없거나 사건이 0이면 고정 값을 쓰며, 서비스 결정 (Reorder) 에는 이 값을 쓰지 않습니다.
    internal ForcedCandidate ForceCandidate(GameSettings game, List<ReadOp> incumbent, double lambdaPerMin, CancellationToken cancellationToken = default)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(Orbit3Constants.DecisionBudgetMs);
        try
        {
            string? why = BuildInput(game, incumbent, budget.Token, lambdaPerMin, out Orbit3Input? input, out _, out _, out _, out _, out string? source, out double? perMin);
            if (why is not null || input is null)
            {
                return new ForcedCandidate(null, why ?? "no_input", source, perMin);
            }

            Orbit3Decision decision = Orbit3.DecideCandidate(input, budget.Token);
            return decision.Order is null
                ? new ForcedCandidate(null, decision.Reason, source, perMin)
                : new ForcedCandidate(OrbitApply.Reorder(incumbent, input.Set, decision.Order), decision.Reason, source, perMin);
        }
        catch (OperationCanceledException)
        {
            return new ForcedCandidate(null, "timeout");
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            return new ForcedCandidate(null, "calculation_error");
        }
    }

    private List<ReadOp> FinishBaseline(GameSettings game, List<ReadOp> ops, string mode, string reason,
        DeviceFit? fit, OrbitModelSnapshot? snapshot, Orbit3Decision? decision, OrbitSegmentSet? set, int blocks, Stopwatch clock)
    {
        if (mode != "off" && !PersistDecision(game, mode))
        {
            reason = "decision_record_failed";
        }

        Record(game, mode, false, reason, fit, snapshot, decision, set, blocks, clock.Elapsed.TotalMilliseconds);
        return ops;
    }

    private string? BuildInput(GameSettings game, List<ReadOp> ops, CancellationToken token, double lambdaPerMin,
        out Orbit3Input? input, out string reason, out OrbitModelSnapshot? snapshot, out DeviceFit? fit, out OrbitSegmentSet? set,
        out string? lambdaSource, out double? lambdaPerMinUsed)
    {
        input = null;
        reason = string.Empty;
        snapshot = null;
        fit = _device(game);
        set = null;
        lambdaSource = null;
        lambdaPerMinUsed = null;
        if (fit is null)
        {
            reason = "no_device_calibration";
            return reason;
        }

        snapshot = _loadModel(game, token);
        token.ThrowIfCancellationRequested();
        if (snapshot.Episodes.Count == 0)
        {
            reason = "no_episode_model";
            return reason;
        }

        double lambdaLow;
        double lambdaHat;
        double lambdaHigh;
        if (snapshot.LambdaLow is { } low && snapshot.LambdaHat is > 0 and { } hat && snapshot.LambdaHigh is { } high)
        {
            (lambdaLow, lambdaHat, lambdaHigh) = (low, hat, high);
            lambdaSource = "model";
            lambdaPerMinUsed = hat * 60000;
        }
        else
        {
            lambdaLow = lambdaHat = lambdaHigh = lambdaPerMin / 60000; // 분당 값을 밀리초당으로
            lambdaSource = "bench_override";
            lambdaPerMinUsed = lambdaPerMin;
        }

        set = OrbitSegments.Build(ops, _headBytes(game));
        (List<Orbit3Episode> episodes, _, double confidence) = MapEpisodes(snapshot, set, ops, token);
        var device = new Orbit3Device(fit.SeekHatMs, fit.ThroughputHatMBps * 1000,
            fit.Envelope.SeekMin, fit.Envelope.SeekMax, fit.Envelope.ThroughputMin, fit.Envelope.ThroughputMax);
        input = new Orbit3Input(set, episodes, lambdaLow, lambdaHat, lambdaHigh, device, confidence);
        return null;
    }

    // 에피소드 블록 경로를 예열 목록의 표기(대소문자)로 맞춰 사상합니다 (MapBlocks 는 Ordinal 비교).
    private static (List<Orbit3Episode> Episodes, int Blocks, double Confidence) MapEpisodes(
        OrbitModelSnapshot snapshot, OrbitSegmentSet set, List<ReadOp> ops, CancellationToken token)
    {
        var canonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ReadOp op in ops)
        {
            canonical.TryAdd(op.Path, op.Path);
        }

        var rangesByEpisode = new List<IReadOnlyList<OrbitBlockRange>>(snapshot.Episodes.Count);
        foreach (OrbitEpisode episode in snapshot.Episodes)
        {
            token.ThrowIfCancellationRequested();
            rangesByEpisode.Add(CanonicalRanges(episode, canonical));
        }

        IReadOnlyList<Orbit3BlockMapping> mapped = Orbit3.MapBlockRanges(set, rangesByEpisode, token);
        var episodes = new List<Orbit3Episode>(snapshot.Episodes.Count);
        int totalBlocks = 0;
        int unmatched = 0;
        for (int index = 0; index < snapshot.Episodes.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            OrbitEpisode episode = snapshot.Episodes[index];
            Orbit3BlockMapping mapping = mapped[index];
            totalBlocks = checked(totalBlocks + (int)mapping.Blocks);
            unmatched = checked(unmatched + (int)mapping.Unmatched);
            episodes.Add(new Orbit3Episode(mapping.Parts, episode.PLow, episode.PHat, episode.PHigh));
        }

        token.ThrowIfCancellationRequested();
        return (episodes, totalBlocks, totalBlocks == 0 ? 0 : Math.Clamp(1 - unmatched / (double)totalBlocks, 0, 1));
    }

    private static IReadOnlyList<OrbitBlockRange> CanonicalRanges(OrbitEpisode episode, IReadOnlyDictionary<string, string> canonical)
    {
        IEnumerable<OrbitBlockRange> source = episode.Ranges.Count > 0
            ? episode.Ranges
            : episode.Blocks.Select(block => new OrbitBlockRange(block.Path, block.Block, 1));
        var groups = new Dictionary<string, List<OrbitBlockRange>>(StringComparer.Ordinal);
        foreach (OrbitBlockRange range in source)
        {
            string path = canonical.GetValueOrDefault(range.Path, range.Path);
            if (!groups.TryGetValue(path, out List<OrbitBlockRange>? pathRanges))
            {
                pathRanges = [];
                groups.Add(path, pathRanges);
            }

            pathRanges.Add(range with { Path = path });
        }

        var result = new List<OrbitBlockRange>();
        foreach ((string path, List<OrbitBlockRange> pathRanges) in groups.OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            OrbitBlockRange[] sorted = pathRanges.OrderBy(range => range.StartBlock).ThenBy(range => range.Count).ToArray();
            foreach (OrbitBlockRange range in sorted)
            {
                if (range.StartBlock < 0 || range.Count <= 0)
                {
                    result.Add(range);
                    continue;
                }

                if (result.Count > 0 && result[^1].Path == path && result[^1].StartBlock >= 0 && result[^1].Count > 0
                    && range.StartBlock <= result[^1].StartBlock + result[^1].Count)
                {
                    OrbitBlockRange previous = result[^1];
                    long end = Math.Max(previous.StartBlock + previous.Count, range.StartBlock + range.Count);
                    result[^1] = previous with { Count = end - previous.StartBlock };
                    continue;
                }

                result.Add(range);
            }
        }

        return result;
    }

    // 게임별 연속 횟수가 기준에 닿으면 강등하고, 강등됐으면 참을 돌려줍니다.
    private bool OnCalculationException(GameSettings game, string reason)
    {
        if (_exceptionStreak.AddOrUpdate(game.Name, 1, (_, n) => n + 1) < Orbit3Constants.ExceptionDemoteStreak)
        {
            return false;
        }

        _ = _exceptionStreak.TryRemove(game.Name, out _);
        return Demote(reason);
    }

    private void ResetExceptionStreak(GameSettings game) => _exceptionStreak.TryRemove(game.Name, out _);

    private bool PersistDecision(GameSettings game, string mode)
    {
        if (mode == "off")
        {
            return true;
        }

        try
        {
            OrbitModel.SetDecision(game, Guid.NewGuid().ToString("N"), _now(), mode);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or ArgumentException)
        {
            _log.Warn($"{game.Name}: orbit decision was not saved ({e.GetType().Name})");
            return false;
        }
    }

    private void Record(GameSettings game, string mode, bool applied, string reason, DeviceFit? fit,
        OrbitModelSnapshot? snapshot, Orbit3Decision? decision, OrbitSegmentSet? set, int blocks, double ms)
    {
        _status?.RecordOrbitRuntime(game.Name, new OrbitRuntimeRecord(mode, applied, reason,
            fit is null ? null : Math.Min(fit.SeekN, fit.ThroughputN), snapshot?.Runs, snapshot?.LambdaHat, decision?.DLow, _status.ReplanGeneration(game.Name)));
        _log.Info($"{game.Name}: orbit {mode}, applied {applied}, reason {reason}, blocks {blocks}, segments {set?.Segments.Count ?? 0}, {Math.Round(ms, 1)} ms");
    }

    // 파일이 없으면 active, 깨졌으면 shadow. 수정 시각과 길이가 그대로면 캐시를 씁니다.
    private string ReadMode()
    {
        try
        {
            var info = new FileInfo(_modePath);
            if (!info.Exists)
            {
                ObserveModeFile(false);
                return "active";
            }

            (DateTime Stamp, long Length) key = (info.LastWriteTimeUtc, info.Length);
            ObserveModeFile(true);
            lock (_modeLock)
            {
                if (_modeCache is { } hit && (hit.Stamp, hit.Length) == key)
                {
                    return hit.Mode;
                }
            }

            string mode = ParseMode(_modePath);
            lock (_modeLock)
            {
                _modeCache = (key.Stamp, key.Length, mode);
            }

            return mode;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            return "shadow";
        }
    }

    private static string ParseMode(string path)
    {
        try
        {
            object? root = JsonRead.ReadFile(path);
            if (root is JObject obj && obj.TryGetValue("mode", out object? value) && value is string mode
                && mode is "off" or "shadow" or "active")
            {
                return mode;
            }

            return "shadow";
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return "active";
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
        {
            return "shadow";
        }
    }

    private static void WriteAtomic(string path, JObject root)
    {
        string full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        string tmp = full + "." + Guid.NewGuid().ToString("N") + ".tmp"; // 명령줄과 서비스가 동시에 써도 임시 파일이 겹치지 않게
        try
        {
            DurableFile.WriteAllBytes(tmp, PyJson.Utf8.GetBytes(PyJson.Dumps(root, indent: 1, ensureAscii: false) + "\n"));
            File.Move(tmp, full, true);
        }
        catch
        {
            try
            {
                File.Delete(tmp);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 지우지 못해도 다음 쓰기에는 영향 없음
            }

            throw;
        }
    }

    private sealed class QuietLog : ILog
    {
        public static QuietLog Instance { get; } = new();

        public void Write(LogLevel level, string message, Exception? exception = null)
        {
        }
    }
}
