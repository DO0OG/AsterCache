using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using AsterCache.Core;
using AsterCache.Core.Json;
using AsterCache.Core.Logging;
using AsterCache.Core.Plan.Orbit;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Tracer;

namespace AsterCache.Service;

public sealed record OrbitBlock(string Path, long Block);

public sealed record OrbitEpisode(IReadOnlyList<OrbitBlock> Blocks, double PLow, double PHat, double PHigh, double DMs)
{
    public IReadOnlyList<OrbitBlockRange> Ranges { get; init; } = Array.Empty<OrbitBlockRange>();
}

public sealed record OrbitModelSnapshot(IReadOnlyList<OrbitEpisode> Episodes, double? LambdaLow, double? LambdaHat, double? LambdaHigh, int Runs, double? UnmatchedRatio);

/// 예열 판단에 쓸 회차별 에피소드와 중단 위험을 저장합니다.
public sealed class OrbitModel : IDisposable
{
    private const int LegacyFormatVersion = 1;
    private const int FormatVersion = 2;
    private const int MaxEpisodes = 96;
    private const int MaxFiles = 100_000;
    private const int MaxRanges = 1_000_000;
    private static readonly TimeSpan BuildBudget = TimeSpan.FromMilliseconds(Orbit3Constants.ModelBuildBudgetMs);
    private static readonly TimeSpan ScheduleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(2);
    private static readonly object ActiveBuildLock = new();
    private static readonly ConcurrentDictionary<Guid, ScheduledBuild> ActiveBuilds = new();
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, SnapshotCache> SnapshotCaches = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BlockKeyComparer KeyComparer = new();
    private readonly object _scheduleLock = new();
    private readonly Dictionary<string, ScheduledBuild> _running = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ScheduledBuild> _scheduled = [];
    private bool _stopped;

    public static string PathFor(string plansDir, string game) => Path.Combine(plansDir, game + ".orbit.json");

    public static string PathFor(GameSettings game) => PathFor(Path.GetDirectoryName(game.Plan) ?? string.Empty, game.Name);

    internal static void ForgetSnapshots(IEnumerable<GameSettings> games)
    {
        foreach (GameSettings game in games)
        {
            _ = SnapshotCaches.TryRemove(PathFor(game), out _);
        }
    }

    public void Dispose()
    {
        ScheduledBuild[] scheduled;
        lock (_scheduleLock)
        {
            _stopped = true;
            scheduled = [.. _scheduled];
            foreach (ScheduledBuild build in scheduled)
            {
                Cancel(build.Stop);
            }
        }

        CancelAndWait(scheduled);
    }

    internal static void StopUnder(string directory)
    {
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        ScheduledBuild[] scheduled;
        lock (ActiveBuildLock)
        {
            scheduled = ActiveBuilds.Values
                .Where(build => Path.GetFullPath(build.Key).StartsWith(root, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        CancelAndWait(scheduled);
    }

    private static void CancelAndWait(ScheduledBuild[] scheduled)
    {
        foreach (ScheduledBuild build in scheduled)
        {
            Cancel(build.Stop);
        }

        var clock = Stopwatch.StartNew();
        foreach (ScheduledBuild build in scheduled)
        {
            TimeSpan remaining = StopWait - clock.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            if (build.Thread != Thread.CurrentThread)
            {
                _ = build.Thread.Join(remaining);
            }
        }
    }

    public void Schedule(GameSettings game, ILog log, long minRunBytes = AutoTracer.MinExtraRunBytes) =>
        Schedule(game, log, minRunBytes, null);

    internal void Schedule(GameSettings game, ILog log, long minRunBytes, Action<CancellationToken>? afterTempWrite)
    {
        string key = PathFor(game);
        lock (_scheduleLock)
        {
            if (_stopped)
            {
                return;
            }

            if (_running.TryGetValue(key, out ScheduledBuild? old))
            {
                Cancel(old.Stop);
            }

            var stop = new CancellationTokenSource();
            ScheduledBuild? scheduled = null;
            var thread = new Thread(() => RunScheduled(scheduled!, game, log, minRunBytes, afterTempWrite))
            { IsBackground = true, Priority = ThreadPriority.BelowNormal };
            scheduled = new ScheduledBuild(this, key, stop, thread);
            _running[key] = scheduled;
            _scheduled.Add(scheduled);
            lock (ActiveBuildLock)
            {
                ActiveBuilds[scheduled.Id] = scheduled;
                thread.Start();
            }
        }
    }

    public void CancelGame(string game)
    {
        lock (_scheduleLock)
        {
            foreach ((string path, ScheduledBuild build) in _running.ToArray())
            {
                if (string.Equals(System.IO.Path.GetFileName(path), game + ".orbit.json", StringComparison.OrdinalIgnoreCase))
                {
                    _running.Remove(path);
                    Cancel(build.Stop);
                }
            }
        }
    }

    public static void CancelInFlightGame(string game)
    {
        ScheduledBuild[] builds;
        lock (ActiveBuildLock)
        {
            builds = ActiveBuilds.Values
                .Where(build => string.Equals(Path.GetFileName(build.Key), game + ".orbit.json", StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        foreach (ScheduledBuild build in builds)
        {
            Cancel(build.Stop);
        }
    }

    private static void RunScheduled(ScheduledBuild scheduled, GameSettings game, ILog log, long minRunBytes, Action<CancellationToken>? afterTempWrite)
    {
        try
        {
            // 연속 병합을 합쳐 같은 회차 파일을 반복해서 읽지 않습니다.
            if (scheduled.Stop.Token.WaitHandle.WaitOne(ScheduleDelay))
            {
                return;
            }

            using var timeout = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(scheduled.Stop.Token, timeout.Token);
            timeout.CancelAfter(BuildBudget);
            var clock = Stopwatch.StartNew();
            bool saved = Build(game, minRunBytes, BuildBudget, linked.Token, afterTempWrite);
            if (!saved && !scheduled.Stop.IsCancellationRequested && (timeout.IsCancellationRequested || clock.Elapsed >= BuildBudget))
            {
                log.Warn($"{game.Name}: 모형 구축 시간이 초과되었습니다.");
            }
        }
        catch (Exception e)
        {
            log.Warn($"{game.Name}: orbit model build failed ({e.GetType().Name})");
        }
        finally
        {
            OrbitModel owner = scheduled.Owner;
            lock (owner._scheduleLock)
            {
                if (owner._running.TryGetValue(scheduled.Key, out ScheduledBuild? current) && ReferenceEquals(current, scheduled))
                {
                    owner._running.Remove(scheduled.Key);
                }

                owner._scheduled.Remove(scheduled);
            }

            lock (ActiveBuildLock)
            {
                _ = ActiveBuilds.TryRemove(scheduled.Id, out _);
            }

            scheduled.Stop.Dispose();
        }
    }

    public static bool Build(GameSettings game, long minRunBytes = AutoTracer.MinExtraRunBytes, CancellationToken cancellationToken = default) =>
        Build(game, minRunBytes, BuildBudget, cancellationToken);

    public static bool Build(GameSettings game, long minRunBytes, TimeSpan budget, CancellationToken cancellationToken)
        => Build(game, minRunBytes, budget, cancellationToken, null);

    internal static bool Build(GameSettings game, long minRunBytes, TimeSpan budget, CancellationToken cancellationToken, Action<CancellationToken>? afterTempWrite)
    {
        if (minRunBytes < 0 || budget <= TimeSpan.Zero)
        {
            return false;
        }

        var clock = Stopwatch.StartNew();
        try
        {
            CheckBuild(clock, budget, cancellationToken);
            string? inputs = Inputs(game, minRunBytes);
            if (inputs is null)
            {
                return false;
            }

            List<RunData> runs = ReadLatestRuns(Path.GetDirectoryName(game.Plan) ?? string.Empty, game.Name, minRunBytes, clock, budget, cancellationToken);
            IReadOnlyList<TraceEvent>? commonRows = TracePhases.CommonOf(game, minRunBytes);
            HashSet<BlockKey> common = commonRows is null
                ? new HashSet<BlockKey>(KeyComparer)
                : CommonBlocks(commonRows, clock, budget, cancellationToken);
            List<EpisodeClass> classes = runs.Count < Orbit3Constants.MinValueRuns
                ? []
                : BuildClasses(runs, common, clock, budget, cancellationToken);
            HashSet<BlockKey> modeled = new(classes.SelectMany(c => c.Blocks), KeyComparer);
            double unmatched = UnmatchedRatio(runs, common, modeled);
            List<EpisodeClass> selected = classes
                .OrderByDescending(c => c.Starts.Count * (double)c.Blocks.Count)
                .ThenBy(c => c.Order)
                .Take(MaxEpisodes)
                .ToList();
            double tail = Orbit3Constants.Alpha / Math.Max(1, selected.Count);
            var episodes = new List<StoredEpisode>(selected.Count);
            foreach (EpisodeClass episode in selected)
            {
                CheckBuild(clock, budget, cancellationToken);
                episodes.Add(ToStoredEpisode(episode, runs.Count, 1 - tail));
            }

            List<string> files = episodes.SelectMany(episode => episode.Ranges)
                .Select(range => range.Path)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .ToList();
            var fileIndexes = files.Select((path, index) => (path, index))
                .ToDictionary(item => item.path, item => item.index, StringComparer.OrdinalIgnoreCase);

            CheckBuild(clock, budget, cancellationToken);
            if (!string.Equals(inputs, Inputs(game, minRunBytes), StringComparison.Ordinal))
            {
                return false;
            }

            string path = PathFor(game);
            lock (Gate(path))
            {
                CheckBuild(clock, budget, cancellationToken);
                if (!string.Equals(inputs, Inputs(game, minRunBytes), StringComparison.Ordinal))
                {
                    return false;
                }

                JObject? previous = ReadRoot(path);
                JObject root = new()
                {
                    { "version", (long)FormatVersion },
                    { "inputs", inputs },
                    { "files", files.Cast<object?>().ToList() },
                    { "episodes", episodes.Select(episode => SerializeEpisode(episode, fileIndexes)).Cast<object?>().ToList() },
                    { "unmatched", unmatched },
                    { "hazard", PreserveObject(previous, "hazard", DefaultHazard()) },
                    { "decision", PreserveObject(previous, "decision", DefaultDecision()) },
                };
                WriteAtomic(path, root, clock, budget, cancellationToken, afterTempWrite);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (BuildBudgetException)
        {
            return false;
        }
    }

    public static OrbitModelSnapshot Load(string plansDir, string game, long minRunBytes = AutoTracer.MinExtraRunBytes)
    {
        string path = PathFor(plansDir, game);
        try
        {
            string? currentInputs = Inputs(plansDir, game, minRunBytes);
            FileInfo info = new(path);
            long length = info.Exists ? info.Length : -1;
            long stampTicks = info.Exists ? info.LastWriteTimeUtc.Ticks : -1;
            if (currentInputs is not null && info.Exists
                && SnapshotCaches.TryGetValue(path, out SnapshotCache? cached)
                && cached.Length == length && cached.StampTicks == stampTicks
                && cached.MinRunBytes == minRunBytes && string.Equals(cached.Inputs, currentInputs, StringComparison.Ordinal))
            {
                return cached.Snapshot;
            }

            JObject? root = ReadRoot(path);
            Hazard? hazard = ReadHazard(root);
            double? lambdaHat = null;
            double? lambdaLow = null;
            double? lambdaHigh = null;
            if (hazard is { ExposureSeconds: > 0 })
            {
                (double low, double high) = OrbitStats.Garwood(hazard.Events, 1 - Orbit3Constants.Alpha);
                double exposureMs = hazard.ExposureSeconds * 1000;
                lambdaLow = low / exposureMs;
                lambdaHat = hazard.Events / exposureMs;
                lambdaHigh = high / exposureMs;
            }

            bool current = root is not null && currentInputs is not null
                && root.TryGetValue("inputs", out object? inputValue) && inputValue is string savedInputs
                && string.Equals(savedInputs, currentInputs, StringComparison.Ordinal);
            List<StoredEpisode> stored = current ? ReadEpisodes(root!) : [];
            var episodes = new List<OrbitEpisode>();
            if (stored.Count > 0)
            {
                double confidence = 1 - Orbit3Constants.Alpha / stored.Count;
                foreach (StoredEpisode episode in stored)
                {
                    (double low, double high) = OrbitStats.ClopperPearson(episode.RunsSeen, episode.RunsTotal, confidence);
                    episodes.Add(new OrbitEpisode([], low, (double)episode.RunsSeen / episode.RunsTotal, high, episode.DMs)
                    {
                        Ranges = episode.Ranges,
                    });
                }
            }

            double? unmatched = current ? ReadUnmatched(root!) : null;
            int runCount = stored.Count == 0 ? 0 : stored[0].RunsTotal;
            var snapshot = new OrbitModelSnapshot(episodes, lambdaLow, lambdaHat, lambdaHigh, runCount, unmatched);
            if (current && info.Exists)
            {
                SnapshotCaches[path] = new SnapshotCache(length, stampTicks, minRunBytes, currentInputs!, snapshot);
            }

            return snapshot;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or ArgumentException or OverflowException)
        {
            return new OrbitModelSnapshot([], null, null, null, 0, null);
        }
    }

    public static OrbitModelSnapshot Load(string plansDir, GameSettings game, long minRunBytes = AutoTracer.MinExtraRunBytes) =>
        Load(plansDir, game.Name, minRunBytes);

    public static OrbitModelSnapshot Load(GameSettings game, long minRunBytes = AutoTracer.MinExtraRunBytes) =>
        Load(Path.GetDirectoryName(game.Plan) ?? string.Empty, game.Name, minRunBytes);

    /// 오프라인 재채점은 입력 trace 원본이 없어도 저장된 orbit.json 모형을 그대로 평가합니다.
    public static OrbitModelSnapshot LoadForRescore(string orbitPath)
    {
        try
        {
            JObject? root = ReadRoot(orbitPath);
            Hazard? hazard = ReadHazard(root);
            double? lambdaLow = null;
            double? lambdaHat = null;
            double? lambdaHigh = null;
            if (hazard is { ExposureSeconds: > 0 })
            {
                (double low, double high) = OrbitStats.Garwood(hazard.Events, 1 - Orbit3Constants.Alpha);
                double exposureMs = hazard.ExposureSeconds * 1000;
                lambdaLow = low / exposureMs;
                lambdaHat = hazard.Events / exposureMs;
                lambdaHigh = high / exposureMs;
            }

            List<StoredEpisode> stored = root is null ? [] : ReadEpisodes(root);
            double confidence = stored.Count == 0 ? 0 : 1 - Orbit3Constants.Alpha / stored.Count;
            var episodes = stored.Select(episode =>
            {
                (double low, double high) = OrbitStats.ClopperPearson(episode.RunsSeen, episode.RunsTotal, confidence);
                return new OrbitEpisode([], low, (double)episode.RunsSeen / episode.RunsTotal, high, episode.DMs)
                {
                    Ranges = episode.Ranges,
                };
            }).ToArray();
            return new OrbitModelSnapshot(episodes, lambdaLow, lambdaHat, lambdaHigh,
                stored.Count == 0 ? 0 : stored[0].RunsTotal, root is null ? null : ReadUnmatched(root));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or ArgumentException or OverflowException)
        {
            return new OrbitModelSnapshot([], null, null, null, 0, null);
        }
    }

    public static void NoteExposure(GameSettings game, double seconds, bool interrupted)
    {
        if (!double.IsFinite(seconds) || seconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seconds));
        }

        string path = PathFor(game);
        lock (Gate(path))
        {
            JObject? previous = ReadRoot(path);
            Hazard current = ReadHazard(previous) ?? new Hazard(0, 0);
            if (interrupted && current.Events == long.MaxValue)
            {
                throw new InvalidOperationException("event count is full");
            }

            double exposure = current.ExposureSeconds + seconds;
            if (!double.IsFinite(exposure))
            {
                throw new ArgumentOutOfRangeException(nameof(seconds));
            }

            JObject root = RootWithPreserved(previous, new JObject
            {
                { "events", current.Events + (interrupted ? 1 : 0) },
                { "exposure_s", exposure },
            }, PreserveObject(previous, "decision", DefaultDecision()));
            WriteAtomic(path, root, null, TimeSpan.MaxValue, CancellationToken.None);
        }
    }

    public static void SetDecision(GameSettings game, string id, DateTimeOffset at, string modeApplied,
        string? reason = null, double? worstEpisodeDelayMs = null)
    {
        if (modeApplied is not ("off" or "shadow" or "active"))
        {
            throw new ArgumentOutOfRangeException(nameof(modeApplied));
        }

        if (worstEpisodeDelayMs is { } delay && !double.IsFinite(delay))
        {
            throw new ArgumentOutOfRangeException(nameof(worstEpisodeDelayMs));
        }

        ArgumentNullException.ThrowIfNull(id);
        string path = PathFor(game);
        lock (Gate(path))
        {
            JObject? previous = ReadRoot(path);
            var decision = new JObject
            {
                { "id", id },
                { "at", at.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) },
                { "mode_applied", modeApplied },
                { "reason", reason },
                { "worst_episode_delay_ms", worstEpisodeDelayMs },
            };
            JObject root = RootWithPreserved(previous, PreserveObject(previous, "hazard", DefaultHazard()), decision);
            WriteAtomic(path, root, null, TimeSpan.MaxValue, CancellationToken.None);
        }
    }

    private static JObject RootWithPreserved(JObject? previous, JObject hazard, JObject decision)
    {
        var root = new JObject
        {
            { "version", (long)FormatVersion },
            { "inputs", previous is not null && previous.TryGetValue("inputs", out object? inputs) && inputs is string text ? text : string.Empty },
            { "files", previous is not null && previous.TryGetValue("files", out object? files) && files is List<object?> fileRows ? fileRows : new List<object?>() },
            { "episodes", previous is not null && previous.TryGetValue("episodes", out object? episodes) && episodes is List<object?> rows ? rows : new List<object?>() },
            { "hazard", hazard },
            { "decision", decision },
        };
        if (previous is not null && previous.TryGetValue("unmatched", out object? unmatched))
        {
            root.Add("unmatched", unmatched);
        }

        return root;
    }

    private static JObject DefaultHazard() => new() { { "events", 0L }, { "exposure_s", 0.0 } };

    private static JObject DefaultDecision() => new()
    {
        { "id", string.Empty },
        { "at", DateTimeOffset.UnixEpoch.ToString("O", CultureInfo.InvariantCulture) },
        { "mode_applied", "off" },
        { "reason", string.Empty },
        { "worst_episode_delay_ms", null },
    };

    private static JObject PreserveObject(JObject? previous, string key, JObject fallback) =>
        previous is not null && previous.TryGetValue(key, out object? value) && value is JObject valueObject ? valueObject : fallback;

    private static Hazard? ReadHazard(JObject? root)
    {
        if (root is null || !root.TryGetValue("hazard", out object? value) || value is not JObject hazard
            || !hazard.TryGetValue("events", out object? eventValue) || eventValue is not long events || events < 0
            || !hazard.TryGetValue("exposure_s", out object? exposureValue) || !AsDouble(exposureValue, out double exposure) || exposure < 0)
        {
            return null;
        }

        return new Hazard(events, exposure);
    }

    private static List<StoredEpisode> ReadEpisodes(JObject root)
    {
        if (!root.TryGetValue("files", out object? filesValue) || filesValue is not List<object?> fileRows
            || fileRows.Count > MaxFiles || !root.TryGetValue("episodes", out object? value)
            || value is not List<object?> rows || rows.Count > MaxEpisodes)
        {
            return [];
        }

        var files = new List<string>(fileRows.Count);
        var knownFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (object? item in fileRows)
        {
            if (item is not string path || path.Length == 0 || !knownFiles.Add(path))
            {
                return [];
            }

            files.Add(path);
        }

        var result = new List<StoredEpisode>(rows.Count);
        long totalRanges = 0;
        foreach (object? row in rows)
        {
            if (row is not JObject episode || !episode.TryGetValue("ranges", out object? rangesValue) || rangesValue is not List<object?> rangeRows
                || rangeRows.Count == 0 || (totalRanges += rangeRows.Count) > MaxRanges
                || !episode.TryGetValue("runs_seen", out object? seenValue) || seenValue is not long seen || seen <= 0
                || !episode.TryGetValue("runs_total", out object? totalValue) || totalValue is not long total || total < seen || total > AutoTracer.MaxRunFiles
                || !episode.TryGetValue("d_ms", out object? dValue) || !AsDouble(dValue, out double dMs) || dMs < 0)
            {
                return [];
            }

            var ranges = new List<OrbitBlockRange>(rangeRows.Count);
            var previousEnds = new Dictionary<int, long>();
            foreach (object? rangeRow in rangeRows)
            {
                if (rangeRow is not List<object?> parts || parts.Count != 3 || parts[0] is not long fileIndexValue
                    || fileIndexValue < 0 || fileIndexValue >= files.Count || parts[1] is not long start || start < 0
                    || parts[2] is not long count || count <= 0 || !ValidBlockRange(start, count))
                {
                    return [];
                }

                int fileIndex = (int)fileIndexValue;
                if (previousEnds.TryGetValue(fileIndex, out long previousEnd) && start < previousEnd)
                {
                    return [];
                }

                previousEnds[fileIndex] = checked(start + count);
                ranges.Add(new OrbitBlockRange(files[fileIndex], start, count));
            }

            result.Add(new StoredEpisode(ranges, (int)seen, (int)total, dMs));
        }

        return result;
    }

    private static bool ValidBlockRange(long start, long count)
    {
        long maxBlock = long.MaxValue / TraceWriter.Block;
        return start <= maxBlock && count <= maxBlock - start + 1;
    }

    private static bool AsDouble(object? value, out double result)
    {
        result = value switch
        {
            long integer => integer,
            double number => number,
            _ => double.NaN,
        };
        return double.IsFinite(result);
    }

    private static double? ReadUnmatched(JObject root)
    {
        if (!root.TryGetValue("unmatched", out object? value) || !AsDouble(value, out double ratio) || ratio < 0 || ratio > 1)
        {
            return null;
        }

        return ratio;
    }

    private static JObject? ReadRoot(string path)
    {
        try
        {
            if (!TryReadVersion(path, out long version) || version is not (LegacyFormatVersion or FormatVersion)
                || JsonRead.ReadFile(path) is not JObject root)
            {
                return null;
            }

            return version == LegacyFormatVersion ? UpgradeLegacyRoot(root) : root;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private static bool TryReadVersion(string path, out long version)
    {
        version = 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> bytes = stackalloc byte[256];
        int read = 0;
        while (read < bytes.Length)
        {
            int count = stream.Read(bytes[read..]);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        string prefix = PyJson.Utf8.GetString(bytes[..read]);
        int key = prefix.IndexOf("\"version\"", StringComparison.Ordinal);
        if (key < 0)
        {
            return false;
        }

        int colon = prefix.IndexOf(':', key + 9);
        if (colon < 0)
        {
            return false;
        }

        int start = colon + 1;
        while (start < prefix.Length && char.IsWhiteSpace(prefix[start]))
        {
            start++;
        }

        int end = start;
        while (end < prefix.Length && char.IsAsciiDigit(prefix[end]))
        {
            end++;
        }

        return end > start && long.TryParse(prefix.AsSpan(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out version);
    }

    private static JObject UpgradeLegacyRoot(JObject legacy)
    {
        var episodes = new List<StoredEpisode>();
        if (legacy.TryGetValue("episodes", out object? episodesValue) && episodesValue is List<object?> rows && rows.Count <= MaxEpisodes)
        {
            foreach (object? row in rows)
            {
                if (row is not JObject episode || !episode.TryGetValue("blocks", out object? blocksValue)
                    || blocksValue is not List<object?> blockRows
                    || !episode.TryGetValue("runs_seen", out object? seenValue) || seenValue is not long seen || seen <= 0
                    || !episode.TryGetValue("runs_total", out object? totalValue) || totalValue is not long total || total < seen || total > AutoTracer.MaxRunFiles
                    || !episode.TryGetValue("d_ms", out object? dValue) || !AsDouble(dValue, out double dMs) || dMs < 0)
                {
                    episodes.Clear();
                    break;
                }

                var blocks = new HashSet<BlockKey>(KeyComparer);
                foreach (object? blockRow in blockRows)
                {
                    if (blockRow is not List<object?> pair || pair.Count != 2 || pair[0] is not string path || path.Length == 0
                        || pair[1] is not long block || block < 0)
                    {
                        blocks.Clear();
                        break;
                    }

                    blocks.Add(new BlockKey(path, block));
                }

                if (blocks.Count == 0)
                {
                    episodes.Clear();
                    break;
                }

                episodes.Add(new StoredEpisode(CompressBlocks(blocks), (int)seen, (int)total, dMs));
            }
        }

        return VersionTwoRoot(legacy, episodes);
    }

    private static JObject VersionTwoRoot(JObject previous, IReadOnlyList<StoredEpisode> episodes)
    {
        List<string> files = episodes.SelectMany(episode => episode.Ranges)
            .Select(range => range.Path)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.Ordinal)
            .ToList();
        var fileIndexes = files.Select((path, index) => (path, index))
            .ToDictionary(item => item.path, item => item.index, StringComparer.OrdinalIgnoreCase);
        var root = new JObject
        {
            { "version", (long)FormatVersion },
            { "inputs", previous.TryGetValue("inputs", out object? inputValue) && inputValue is string inputs ? inputs : string.Empty },
            { "files", files.Cast<object?>().ToList() },
            { "episodes", episodes.Select(episode => SerializeEpisode(episode, fileIndexes)).Cast<object?>().ToList() },
        };
        if (previous.TryGetValue("unmatched", out object? unmatched))
        {
            root.Add("unmatched", unmatched);
        }

        if (previous.TryGetValue("hazard", out object? hazard))
        {
            root.Add("hazard", hazard);
        }

        if (previous.TryGetValue("decision", out object? decision))
        {
            root.Add("decision", decision);
        }

        return root;
    }

    private static void WriteAtomic(string path, JObject root, Stopwatch? clock, TimeSpan budget, CancellationToken cancellationToken, Action<CancellationToken>? afterTempWrite = null)
    {
        string temp = path + ".tmp";
        byte[] bytes = PyJson.Utf8.GetBytes(PyJson.ToCrlf(PyJson.Dumps(root, indent: 2, ensureAscii: false) + "\n"));
        CheckBuild(clock, budget, cancellationToken);
        try
        {
            DurableFile.WriteAllBytes(temp, bytes);
            afterTempWrite?.Invoke(cancellationToken);
            CheckBuild(clock, budget, cancellationToken);
            File.Move(temp, path, true);
            _ = SnapshotCaches.TryRemove(path, out _);
        }
        finally
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // 임시 파일 정리는 다음 저장을 막지 않게 둡니다.
            }
        }
    }

    private static object Gate(string path) => Gates.GetOrAdd(path, _ => new object());

    private static void Cancel(CancellationTokenSource stop)
    {
        try
        {
            stop.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 작업 스레드가 이미 정리했습니다.
        }
    }

    private static void CheckBuild(Stopwatch? clock, TimeSpan budget, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (clock is not null && clock.Elapsed >= budget)
        {
            throw new BuildBudgetException();
        }
    }

    private static string? Inputs(GameSettings game, long minRunBytes) => Inputs(Path.GetDirectoryName(game.Plan) ?? string.Empty, game.Name, minRunBytes, AutoTracer.LegacyRun1Path(game));

    private static string? Inputs(string plansDir, string game, long minRunBytes) =>
        Inputs(plansDir, game, minRunBytes, Path.Combine(plansDir, game + ".trace.legacy-run1"));

    private static string? Inputs(string plansDir, string game, long minRunBytes, string legacyMark)
    {
        var text = new StringBuilder();
        string raw = Path.Combine(plansDir, game + ".trace.json");
        for (int number = 1; number <= AutoTracer.MaxRunFiles; number++)
        {
            string runPath = TraceWriter.RunPath(raw, number);
            var info = new FileInfo(runPath);
            if (info.Exists)
            {
                text.Append(number.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(Path.GetFileName(runPath)).Append(':')
                    .Append(info.Length.ToString(CultureInfo.InvariantCulture)).Append(':')
                    .Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append(';');
            }
        }

        if (text.Length == 0)
        {
            return null;
        }

        text.Append("min:").Append(minRunBytes.ToString(CultureInfo.InvariantCulture)).Append(';');
        if (File.Exists(legacyMark))
        {
            text.Append("legacy;");
        }

        return text.ToString();
    }

    private static HashSet<BlockKey> CommonBlocks(IReadOnlyList<TraceEvent>? common, Stopwatch clock, TimeSpan budget, CancellationToken cancellationToken)
    {
        var result = new HashSet<BlockKey>(KeyComparer);
        if (common is null)
        {
            return result;
        }

        foreach (TraceEvent e in common)
        {
            if (e.Offset < 0 || e.Length <= 0 || e.Offset > long.MaxValue - e.Length + 1)
            {
                continue;
            }

            long first = e.Offset / TraceWriter.Block;
            long last = (e.Offset + e.Length - 1) / TraceWriter.Block;
            for (long block = first; block <= last; block++)
            {
                if ((block & 255) == 0)
                {
                    CheckBuild(clock, budget, cancellationToken);
                }

                result.Add(new BlockKey(e.Path, block));
            }
        }

        return result;
    }

    private static double UnmatchedRatio(IReadOnlyList<RunData> runs, HashSet<BlockKey> common, HashSet<BlockKey> modeled)
    {
        long total = 0;
        long unmatched = 0;
        foreach (RunData run in runs)
        {
            foreach (BlockKey key in run.Blocks)
            {
                if (common.Contains(key))
                {
                    continue;
                }

                total++;
                if (!modeled.Contains(key))
                {
                    unmatched++;
                }
            }
        }

        return total == 0 ? 0 : (double)unmatched / total;
    }

    private static List<RunData> ReadLatestRuns(string plansDir, string game, long minRunBytes, Stopwatch clock, TimeSpan budget, CancellationToken cancellationToken)
    {
        string raw = Path.Combine(plansDir, game + ".trace.json");
        bool legacyRun1 = File.Exists(Path.Combine(plansDir, game + ".trace.legacy-run1"));
        var runs = new List<RunData>();
        for (int number = AutoTracer.MaxRunFiles; number >= 1 && runs.Count < Orbit3Constants.MaxValueRuns; number--)
        {
            CheckBuild(clock, budget, cancellationToken);
            string path = TraceWriter.RunPath(raw, number);
            if (!File.Exists(path))
            {
                continue;
            }

            List<TraceEvent> rows;
            try
            {
                rows = JsonRead.ReadFile(path) is List<object?> values ? [.. values.Select(TraceEvents.FromJson)] : throw new PyValueException("bad trace file");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or OverflowException)
            {
                continue;
            }

            CheckBuild(clock, budget, cancellationToken);
            long bytes;
            try
            {
                bytes = rows.Sum(e => e.Length);
            }
            catch (OverflowException)
            {
                continue;
            }

            if (!(number == 1 && legacyRun1) && bytes < minRunBytes)
            {
                continue;
            }

            // TracePhases.Compute는 비공개이므로 같은 크기 기준을 쓰고 범위 오류 행만 제외합니다.
            List<TraceEvent> events = rows.Where(e => e.Offset >= 0 && e.Length > 0 && e.Offset <= long.MaxValue - e.Length + 1).ToList();
            var blocks = new HashSet<BlockKey>(KeyComparer);
            foreach (TraceEvent e in events)
            {
                AddBlocks(e, blocks, null, clock, budget, cancellationToken);
            }

            runs.Add(new RunData(number, events, blocks));
        }

        runs.Reverse();
        return runs;
    }

    private static void AddBlocks(TraceEvent e, HashSet<BlockKey> target, HashSet<BlockKey>? excluded, Stopwatch clock, TimeSpan budget, CancellationToken cancellationToken)
    {
        long first = e.Offset / TraceWriter.Block;
        long last = (e.Offset + e.Length - 1) / TraceWriter.Block;
        for (long block = first; block <= last; block++)
        {
            if ((block & 255) == 0)
            {
                CheckBuild(clock, budget, cancellationToken);
            }

            var key = new BlockKey(e.Path, block);
            if (excluded is null || !excluded.Contains(key))
            {
                target.Add(key);
            }
        }
    }

    private static List<EpisodeClass> BuildClasses(IReadOnlyList<RunData> runs, HashSet<BlockKey> common, Stopwatch clock, TimeSpan budget, CancellationToken cancellationToken)
    {
        var classes = new List<EpisodeClass>();
        int order = 0;
        // 회차 번호·구간 시작 시각 순으로 훑고 기준을 처음 넘긴 클래스에 합쳐 결과를 고정합니다.
        foreach (RunData run in runs)
        {
            List<EpisodeObservation> observations = BuildObservations(run, common, clock, budget, cancellationToken);
            foreach (EpisodeObservation observation in observations)
            {
                CheckBuild(clock, budget, cancellationToken);
                EpisodeClass? found = null;
                for (int i = 0; i < classes.Count; i++)
                {
                    if ((i & 63) == 0)
                    {
                        CheckBuild(clock, budget, cancellationToken);
                    }

                    if (Jaccard(classes[i].Blocks, observation.Blocks) >= Orbit3Constants.EpisodeJaccard)
                    {
                        found = classes[i];
                        break;
                    }
                }

                if (found is null)
                {
                    found = new EpisodeClass(order++);
                    classes.Add(found);
                }

                found.Add(observation);
            }
        }

        return classes;
    }

    private static List<EpisodeObservation> BuildObservations(RunData run, HashSet<BlockKey> common, Stopwatch clock, TimeSpan budget, CancellationToken cancellationToken)
    {
        var result = new List<EpisodeObservation>();
        var blocks = new HashSet<BlockKey>(KeyComparer);
        long? start = null;
        long? previous = null;
        foreach (TraceEvent e in run.Events.Where(e => e.TMs is not null).OrderBy(e => e.TMs))
        {
            CheckBuild(clock, budget, cancellationToken);
            long time = e.TMs!.Value;
            if (previous is { } last && time - last >= Orbit3Constants.EpisodeGapMs)
            {
                AddObservation(result, run.Number, start, blocks);
                blocks = new HashSet<BlockKey>(KeyComparer);
                start = null;
            }

            start ??= time;
            AddBlocks(e, blocks, common, clock, budget, cancellationToken);
            previous = time;
        }

        AddObservation(result, run.Number, start, blocks);
        return result;
    }

    private static void AddObservation(List<EpisodeObservation> target, int run, long? start, HashSet<BlockKey> blocks)
    {
        if (start is { } time && blocks.Count > 0)
        {
            target.Add(new EpisodeObservation(run, time, blocks));
        }
    }

    private static double Jaccard(HashSet<BlockKey> left, HashSet<BlockKey> right)
    {
        int intersection = 0;
        HashSet<BlockKey> smaller = left.Count < right.Count ? left : right;
        HashSet<BlockKey> larger = ReferenceEquals(smaller, left) ? right : left;
        foreach (BlockKey key in smaller)
        {
            if (larger.Contains(key))
            {
                intersection++;
            }
        }

        return intersection / (double)(left.Count + right.Count - intersection);
    }

    private static StoredEpisode ToStoredEpisode(EpisodeClass episode, int runsTotal, double confidence)
    {
        (double low, double high) = OrbitStats.ClopperPearson(episode.Starts.Count, runsTotal, confidence);
        long[] starts = episode.Starts.Values.Order().ToArray();
        double median = starts.Length % 2 == 1 ? starts[starts.Length / 2] : (starts[starts.Length / 2 - 1] + (double)starts[starts.Length / 2]) / 2;
        List<OrbitBlockRange> ranges = CompressBlocks(episode.Blocks);
        return new StoredEpisode(ranges, episode.Starts.Count, runsTotal, median, low, (double)episode.Starts.Count / runsTotal, high);
    }

    private static List<OrbitBlockRange> CompressBlocks(IEnumerable<BlockKey> blocks)
    {
        var result = new List<OrbitBlockRange>();
        foreach (IGrouping<string, BlockKey> group in blocks.GroupBy(block => block.Path, StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key, StringComparer.Ordinal))
        {
            string path = group.Select(block => block.Path)
                .OrderBy(value => value, StringComparer.Ordinal)
                .First();
            long[] values = group.Select(block => block.Block).Distinct().Order().ToArray();
            if (values.Length == 0)
            {
                continue;
            }

            long start = values[0];
            long previous = start;
            for (int index = 1; index < values.Length; index++)
            {
                long current = values[index];
                if (previous < long.MaxValue && current == previous + 1)
                {
                    previous = current;
                    continue;
                }

                result.Add(new OrbitBlockRange(path, start, previous - start + 1));
                start = previous = current;
            }

            result.Add(new OrbitBlockRange(path, start, previous - start + 1));
        }

        return result;
    }

    private static JObject SerializeEpisode(StoredEpisode episode, IReadOnlyDictionary<string, int> fileIndexes) => new()
    {
        { "ranges", episode.Ranges.Select(range => (object?)new List<object?> { (long)fileIndexes[range.Path], range.StartBlock, range.Count }).ToList() },
        { "runs_seen", (long)episode.RunsSeen },
        { "runs_total", (long)episode.RunsTotal },
        { "d_ms", episode.DMs },
    };

    private sealed class EpisodeClass(int order)
    {
        public int Order { get; } = order;
        public HashSet<BlockKey> Blocks { get; } = new(KeyComparer);
        public Dictionary<int, long> Starts { get; } = [];

        public void Add(EpisodeObservation observation)
        {
            if (!Starts.TryGetValue(observation.Run, out long start) || observation.StartMs < start)
            {
                Starts[observation.Run] = observation.StartMs;
            }

            Blocks.UnionWith(observation.Blocks);
        }
    }

    private sealed record RunData(int Number, IReadOnlyList<TraceEvent> Events, HashSet<BlockKey> Blocks);

    private sealed record EpisodeObservation(int Run, long StartMs, HashSet<BlockKey> Blocks);

    private sealed record StoredEpisode(IReadOnlyList<OrbitBlockRange> Ranges, int RunsSeen, int RunsTotal, double DMs, double PLow = 0, double PHat = 0, double PHigh = 1);

    private sealed record SnapshotCache(long Length, long StampTicks, long MinRunBytes, string Inputs, OrbitModelSnapshot Snapshot);

    private sealed record Hazard(long Events, double ExposureSeconds);

    private sealed class ScheduledBuild(OrbitModel owner, string key, CancellationTokenSource stop, Thread thread)
    {
        public Guid Id { get; } = Guid.NewGuid();

        public OrbitModel Owner { get; } = owner;

        public string Key { get; } = key;

        public CancellationTokenSource Stop { get; } = stop;

        public Thread Thread { get; } = thread;
    }

    private readonly record struct BlockKey(string Path, long Block);

    private sealed class BlockKeyComparer : IEqualityComparer<BlockKey>
    {
        public bool Equals(BlockKey x, BlockKey y) => x.Block == y.Block && StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path);

        public int GetHashCode(BlockKey value) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path), value.Block);
    }

    private sealed class BuildBudgetException : Exception
    {
    }
}
