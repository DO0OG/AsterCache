using System.Globalization;
using System.Security.Cryptography;
using AsterCache.Core.Json;

namespace AsterCache.Core.Trace;

/// 물리 읽기 집계를 회차 추적의 곁 파일로 읽고 씁니다.
public static class IoSummaryStore
{
    public const int Version = 4;

    /// 곁 파일에 같이 적는 지연의 뜻 (읽는 사람이 순수 서비스 시간으로 오해하지 않게).
    public const string LatencyNote = "Per request: from the moment it went down the disk stack until completion, so waiting in front of the device is included and it is not pure service time. Overlapping requests make the sum larger than the real stall time. Per block rows split a request's latency across the blocks it touched in proportion to bytes.";
    public const string Suffix = ".io.json";
    public const int MaxStandaloneFiles = 32;
    /// 이전 시도 횟수 뒤에도 읽기 방식 집계를 두 플레이분 모으기 위한 상한.
    public const int MaxTried = 4;

    public static string RunPath(string runPath) => runPath + Suffix;

    public static string StandalonePath(string rawTracePath, int slot)
    {
        if (slot < 1 || slot > MaxStandaloneFiles)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        return $"{rawTracePath}.io-only.{slot:D2}.json";
    }

    public static IReadOnlyList<string> StandalonePaths(string rawTracePath) =>
        Enumerable.Range(1, MaxStandaloneFiles).Select(slot => StandalonePath(rawTracePath, slot)).ToArray();

    /// 플레이마다 남기는 디스크 전용 요약의 슬롯 수 (게임마다 최근 이만큼만 남음). 근거: 한 개가 이 PC 의 Call of Duty 에서 25~39 MiB 라 12 개면 약 360 MiB 이고 (재지 않은 어림),
    /// 경기 가중 점수가 최근 플레이를 앞세워 보는 데 열두 번이면 충분하다고 봤습니다.
    public const int MaxPlayDiskSummaries = 12;

    /// 디스크 전용 요약 슬롯의 이름: 읽기 방식이 담긴 단독 집계 (.io-only.NN.json) 의 슬롯과 따로 두어 서로 덮지 않습니다.
    /// 이 이름 열두 개가 이 모듈이 만들고 지우는 전부입니다 (접두사로 찾아 지우지 않으므로 이름이 비슷한 다른 게임의 파일은 건드리지 않음).
    public static string PlayDiskPath(string rawTracePath, int slot)
    {
        if (slot < 1 || slot > MaxPlayDiskSummaries)
        {
            throw new ArgumentOutOfRangeException(nameof(slot));
        }

        return $"{rawTracePath}.io-disk.{slot:D2}.json";
    }

    public static IReadOnlyList<string> PlayDiskPaths(string rawTracePath) =>
        Enumerable.Range(1, MaxPlayDiskSummaries).Select(slot => PlayDiskPath(rawTracePath, slot)).ToArray();

    public static string TriedPath(string rawTracePath) => rawTracePath + ".io.tried";

    /// 집계 곁 파일이 낡았는데 잠겨 지우지 못했을 때 곁에 남기는 무효 표지 (잠긴 파일 자체는 고칠 수 없으므로 따로 둠). 있으면 읽기가 거부합니다.
    public static string InvalidPath(string sidecarPath) => sidecarPath + InvalidSuffix;

    public const string InvalidSuffix = ".invalid";

    public static void MarkInvalid(string sidecarPath) => File.WriteAllText(InvalidPath(sidecarPath), "1");

    public static int ReadTried(string rawTracePath) => ReadTriedOrNull(rawTracePath) ?? 0;

    /// 시도 횟수. 파일이 없거나 숫자가 아니면 0, 파일은 있는데 읽지 못하면 null (읽기 실패를 "없음" 으로 보지 않게).
    public static int? ReadTriedOrNull(string rawTracePath)
    {
        try
        {
            return File.Exists(TriedPath(rawTracePath))
                && int.TryParse(File.ReadAllText(TriedPath(rawTracePath)).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                    ? Math.Clamp(n, 0, MaxTried)
                    : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void WriteTried(string rawTracePath, int tries) =>
        File.WriteAllText(TriedPath(rawTracePath), Math.Clamp(tries, 0, MaxTried).ToString(CultureInfo.InvariantCulture));

    public static IoSummary? Read(string path)
    {
        if (!File.Exists(path) || File.Exists(InvalidPath(path)))
        {
            return null;
        }

        try
        {
            if (JsonRead.ReadFile(path) is not JObject root
                || !Long(root, "version", out long version) || version is not (3 or Version)
                || !Long(root, "block_size", out long blockSize) || blockSize != IoBlockMapper.BlockSizeBytes
                || !String(root, "latency_unit", out string? unit) || unit != "us"
                || !String(root, "observed_at_utc", out string? startedText)
                || !String(root, "ended_at_utc", out string? endedText)
                || !String(root, "session_id", out string? sessionId) || !Guid.TryParseExact(sessionId, "N", out _)
                || !DateTimeOffset.TryParse(startedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset started)
                || !DateTimeOffset.TryParse(endedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset ended)
                || !root.TryGetValue("files", out object? filesValue) || filesValue is not List<object?> fileRows
                || !root.TryGetValue("rows", out object? rowsValue) || rowsValue is not List<object?> dataRows
                || !root.TryGetValue("raw_windows", out object? windowsValue) || windowsValue is not List<object?> windowRows
                || !root.TryGetValue("quality", out object? qualityValue) || qualityValue is not JObject q)
            {
                return null;
            }

            var files = new List<IoSummaryFile>(fileRows.Count);
            foreach (object? file in fileRows)
            {
                if (file is not JObject fileRow
                    || !String(fileRow, "path", out string? name) || string.IsNullOrWhiteSpace(name)
                    || !fileRow.TryGetValue("changed_during_session", out object? changedValue) || changedValue is not bool changed)
                {
                    return null;
                }

                long? fileSize = null; // 디스크 전용 요약만 가진 선택 항목 (둘 다 있거나 둘 다 없어야 함)
                long? fileTicks = null;
                if (fileRow.ContainsKey("size") || fileRow.ContainsKey("mtime_ticks"))
                {
                    if (!Long(fileRow, "size", out long sizeValue) || sizeValue < 0 || !Long(fileRow, "mtime_ticks", out long ticksValue))
                    {
                        return null;
                    }

                    (fileSize, fileTicks) = (sizeValue, ticksValue);
                }

                files.Add(new IoSummaryFile(name, changed, fileSize, fileTicks));
            }

            var rows = new List<IoSummaryRow>(dataRows.Count);
            foreach (object? value in dataRows)
            {
                if (value is not JObject row
                    || !Long(row, "file", out long fileNumber) || fileNumber < 0 || fileNumber >= files.Count
                    || !Long(row, "block", out long block) || block < 0
                    || !Long(row, "requests", out long requests) || requests < 0
                    || !Long(row, "bytes", out long bytes) || bytes < 0
                    || !Long(row, "latency_sum_us", out long sum) || sum < 0
                    || !Long(row, "latency_max_us", out long max) || max < 0
                    || !Long(row, "first_session_us", out long first) || first < 0
                    || !Long(row, "paging_requests", out long paging) || paging < 0)
                {
                    return null;
                }

                long uncertain = 0; // 디스크 전용 요약만 가진 선택 항목
                if (row.ContainsKey("uncertain_requests") && (!Long(row, "uncertain_requests", out uncertain) || uncertain < 0 || uncertain > requests))
                {
                    return null;
                }

                rows.Add(new IoSummaryRow((int)fileNumber, block, requests, bytes, sum, max, first, paging, uncertain));
            }

            var readModes = new List<IoSummaryReadMode>();
            if (version == Version)
            {
                if (!root.TryGetValue("read_modes", out object? modesValue) || modesValue is not List<object?> modeRows)
                {
                    return null;
                }

                var modePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (object? value in modeRows)
                {
                    if (value is not JObject mode || !String(mode, "path", out string? name) || string.IsNullOrWhiteSpace(name)
                        || !modePaths.Add(name)
                        || !Long(mode, "cache_requests", out long cacheRequests) || cacheRequests < 0
                        || !Long(mode, "cache_bytes", out long cacheBytes) || cacheBytes < 0
                        || !Long(mode, "nocache_requests", out long noCacheRequests) || noCacheRequests < 0
                        || !Long(mode, "nocache_bytes", out long noCacheBytes) || noCacheBytes < 0
                        || !Long(mode, "paging_requests", out long pagingRequests) || pagingRequests < 0
                        || !Long(mode, "paging_bytes", out long pagingBytes) || pagingBytes < 0)
                    {
                        return null;
                    }

                    long openOptions = 0; // 옛 집계에는 없는 선택 항목
                    if (mode.TryGetValue("open_options", out object? optionsValue) && (optionsValue is not long parsed || parsed < 0 || parsed > uint.MaxValue || ((openOptions = parsed) & ~(long)IoSummaryCollector.OpenOptionsMask) != 0))
                    {
                        return null;
                    }

                    readModes.Add(new IoSummaryReadMode(name, cacheRequests, cacheBytes, noCacheRequests, noCacheBytes, pagingRequests, pagingBytes, (uint)openOptions));
                }
            }

            List<long>? targetStarts = null; // 옛 집계에는 없는 선택 항목
            if (root.TryGetValue("target_starts_us", out object? startsValue) && startsValue is not null)
            {
                if (startsValue is not List<object?> startRows)
                {
                    return null;
                }

                targetStarts = new List<long>(startRows.Count);
                foreach (object? value in startRows)
                {
                    if (value is not long startUs)
                    {
                        return null;
                    }

                    targetStarts.Add(startUs);
                }
            }

            var windows = new List<IoSummaryWindow>(windowRows.Count);
            foreach (object? value in windowRows)
            {
                if (value is not JObject window
                    || !Long(window, "start_us", out long start) || start < 0
                    || !Long(window, "end_us", out long end) || end < start)
                {
                    return null;
                }

                windows.Add(new IoSummaryWindow(start, end));
            }

            if (!ReadQuality(q, (int)version, out IoSummaryQuality? quality))
            {
                return null;
            }

            string? rawFingerprint = null;
            if (root.TryGetValue("raw_fingerprint", out object? fingerprintValue) && fingerprintValue is not null)
            {
                if (fingerprintValue is not string fingerprint || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
                {
                    return null;
                }

                rawFingerprint = fingerprint;
            }

            long? rawTraceZeroUs = null;
            if (root.TryGetValue("raw_trace_zero_us", out object? rawZeroValue) && rawZeroValue is not null)
            {
                if (rawZeroValue is not long rawZero)
                {
                    return null;
                }

                rawTraceZeroUs = rawZero;
            }

            DateTimeOffset? rawTraceZeroAtUtc = null;
            if (root.TryGetValue("raw_trace_zero_utc", out object? rawZeroUtcValue) && rawZeroUtcValue is not null)
            {
                if (rawZeroUtcValue is not string rawZeroUtcText
                    || !DateTimeOffset.TryParse(rawZeroUtcText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset rawZeroUtc))
                {
                    return null;
                }

                rawTraceZeroAtUtc = rawZeroUtc.ToUniversalTime();
            }

            if ((rawTraceZeroUs is null) != (rawTraceZeroAtUtc is null))
            {
                return null;
            }

            if (rawTraceZeroUs is { } zeroUs)
            {
                if (Math.Abs(zeroUs) > long.MaxValue / TimeSpan.TicksPerMicrosecond)
                {
                    return null;
                }

                DateTimeOffset expectedRawZeroUtc = started.ToUniversalTime().AddTicks(zeroUs * TimeSpan.TicksPerMicrosecond);
                if (expectedRawZeroUtc != rawTraceZeroAtUtc)
                {
                    return null;
                }
            }

            List<IoSummaryWriteFile>? writeFiles = ReadWriteFiles(root);
            if (writeFiles is null || !ReadPlay(root, out IoSummaryPlay? play))
            {
                return null;
            }

            return new IoSummary(started, ended, windows, quality!, files, rows, sessionId!, rawFingerprint, rawTraceZeroUs, rawTraceZeroAtUtc, readModes, targetStarts, writeFiles.Count == 0 ? null : writeFiles, play);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or OverflowException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    public static void Write(string path, IoSummary summary)
    {
        string sessionId = Guid.TryParseExact(summary.SessionId, "N", out _) ? summary.SessionId : Guid.NewGuid().ToString("N");
        var windows = summary.RawWindows.Select(window => (object?)new JObject
        {
            { "start_us", window.StartedUs },
            { "end_us", window.EndedUs },
        }).ToList();
        var files = summary.Files.Select(file =>
        {
            var row = new JObject
            {
                { "path", file.Path },
                { "changed_during_session", file.ChangedDuringSession },
            };
            if (file.Size is { } size && file.MTimeTicks is { } ticks)
            {
                row.Add("size", size);
                row.Add("mtime_ticks", ticks);
            }

            return (object?)row;
        }).ToList();
        var rows = summary.Rows.Select(row =>
        {
            var json = new JObject
            {
                { "file", (long)row.FileNumber },
                { "block", row.BlockNumber },
                { "requests", row.Requests },
                { "bytes", row.Bytes },
                { "latency_sum_us", row.LatencySumUs },
                { "latency_max_us", row.LatencyMaxUs },
                { "first_session_us", row.FirstSessionUs },
                { "paging_requests", row.PagingRequests },
            };
            if (row.UncertainRequests > 0)
            {
                json.Add("uncertain_requests", row.UncertainRequests); // 디스크 전용 요약에만
            }

            return (object?)json;
        }).ToList();
        var readModes = (summary.ReadModes ?? []).Select(mode => (object?)new JObject
        {
            { "path", mode.Path },
            { "cache_requests", mode.CacheRequests },
            { "cache_bytes", mode.CacheBytes },
            { "nocache_requests", mode.NoCacheRequests },
            { "nocache_bytes", mode.NoCacheBytes },
            { "paging_requests", mode.PagingRequests },
            { "paging_bytes", mode.PagingBytes },
            { "open_options", (long)mode.OpenOptions },
        }).ToList();
        var root = new JObject
        {
            { "version", (long)Version },
            { "block_size", (long)IoBlockMapper.BlockSizeBytes },
            { "latency_unit", "us" },
            { "latency_note", LatencyNote },
            { "session_id", sessionId },
            { "raw_fingerprint", summary.RawFingerprint },
            { "raw_trace_zero_us", summary.RawTraceZeroUs },
            { "raw_trace_zero_utc", summary.RawTraceZeroAtUtc?.ToString("O", CultureInfo.InvariantCulture) },
            { "observed_at_utc", summary.ObservedAtUtc.ToString("O", CultureInfo.InvariantCulture) },
            { "ended_at_utc", summary.EndedAtUtc.ToString("O", CultureInfo.InvariantCulture) },
            { "raw_windows", windows },
            { "quality", QualityJson(summary.Quality) },
            { "files", files },
            { "rows", rows },
            { "read_modes", readModes },
            { "target_starts_us", (summary.TargetStartsUs ?? []).Select(us => (object?)us).ToList() },
        };
        if (summary.WriteFiles is { Count: > 0 } writeFiles)
        {
            root.Add("write_files", writeFiles.Select(WriteFileJson).ToList()); // 쓰기를 기록한 집계에만 (옛 판은 이 항목을 모르고 무시함)
        }

        if (summary.Play is { } play)
        {
            root.Add("play", new JObject
            {
                { "disk_only", play.DiskOnly },
                { "load_phases_us", play.LoadPhases.Select(PairJson).ToList() },
                { "unobserved_us", play.Unobserved.Select(PairJson).ToList() },
            }); // 디스크 전용 요약에만 (옛 판은 이 항목을 모르고 무시함)
        }

        string temp = path + ".tmp";
        string text = PyJson.Dumps(root, indent: 1, ensureAscii: false) + "\n";
        File.WriteAllBytes(temp, PyJson.Utf8.GetBytes(PyJson.ToCrlf(text)));
        File.Move(temp, path, true);
        try
        {
            DeleteIfExists(InvalidPath(path)); // 교체가 성공한 뒤에만: 교체가 실패하면 낡은 집계가 표지 없이 다시 유효해지지 않게
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // 표지를 못 지우면 새 집계가 무효로 읽히는 쪽 (안전한 쪽) 으로 남고 다음 정리가 치움
        }
    }

    private static object? PairJson(IoSummaryWindow window) => new List<object?> { window.StartedUs, window.EndedUs };

    // 디스크 전용 요약의 부가 정보 (없으면 null 이 정상, 있는데 모양이 다르면 false).
    private static bool ReadPlay(JObject root, out IoSummaryPlay? play)
    {
        play = null;
        if (!root.TryGetValue("play", out object? value) || value is null)
        {
            return true;
        }

        if (value is not JObject o || !o.TryGetValue("disk_only", out object? diskOnly) || diskOnly is not bool only
            || !ReadPairs(o, "load_phases_us", out List<IoSummaryWindow>? phases) || !ReadPairs(o, "unobserved_us", out List<IoSummaryWindow>? unobserved))
        {
            return false;
        }

        play = new IoSummaryPlay(only, phases!, unobserved!);
        return true;
    }

    private static bool ReadPairs(JObject o, string key, out List<IoSummaryWindow>? pairs)
    {
        pairs = null;
        if (!o.TryGetValue(key, out object? value) || value is not List<object?> rows)
        {
            return false;
        }

        pairs = new List<IoSummaryWindow>(rows.Count);
        foreach (object? row in rows)
        {
            if (row is not List<object?> { Count: 2 } cells || cells[0] is not long start || cells[1] is not long end || end < start)
            {
                return false;
            }

            pairs.Add(new IoSummaryWindow(start, end));
        }

        return true;
    }

    private static object? WriteFileJson(IoSummaryWriteFile file) => new JObject
    {
        { "path", file.Path },
        { "writes", file.Writes },
        { "bytes", file.Bytes },
        { "nocache_writes", file.NoCacheWrites },
        { "nocache_bytes", file.NoCacheBytes },
        { "first_us", file.FirstUs },
        { "last_us", file.LastUs },
        { "ranges", file.Ranges.Select(r => (object?)new List<object?> { r.Offset, r.Length }).ToList() },
        { "ranges_truncated", file.RangesTruncated },
    };

    // 쓰기 기록 항목 (옛 집계에는 없음): 없으면 빈 목록, 있는데 모양이 다르면 null (집계 전체를 읽지 않음).
    private static List<IoSummaryWriteFile>? ReadWriteFiles(JObject root)
    {
        var files = new List<IoSummaryWriteFile>();
        if (!root.TryGetValue("write_files", out object? value) || value is null)
        {
            return files;
        }

        if (value is not List<object?> rows)
        {
            return null;
        }

        foreach (object? row in rows)
        {
            if (row is not JObject o || !String(o, "path", out string? path) || string.IsNullOrWhiteSpace(path)
                || !Long(o, "writes", out long writes) || writes < 0
                || !Long(o, "bytes", out long bytes) || bytes < 0
                || !Long(o, "nocache_writes", out long noCacheWrites) || noCacheWrites < 0
                || !Long(o, "nocache_bytes", out long noCacheBytes) || noCacheBytes < 0
                || !Long(o, "first_us", out long first) || first < 0
                || !Long(o, "last_us", out long last) || last < first
                || !o.TryGetValue("ranges", out object? rangesValue) || rangesValue is not List<object?> rangeRows
                || !o.TryGetValue("ranges_truncated", out object? truncatedValue) || truncatedValue is not bool truncated)
            {
                return null;
            }

            var ranges = new List<IoWriteRange>(rangeRows.Count);
            foreach (object? range in rangeRows)
            {
                if (range is not List<object?> { Count: 2 } cells || cells[0] is not long offset || offset < 0 || cells[1] is not long length || length <= 0)
                {
                    return null;
                }

                ranges.Add(new IoWriteRange(offset, length));
            }

            files.Add(new IoSummaryWriteFile(path, writes, bytes, noCacheWrites, noCacheBytes, first, last, ranges, truncated));
        }

        return files;
    }

    /// 집계 곁 파일을 옮기되 무효 표지를 함께 옮깁니다 (move 는 실제 이동). 무효 집계가 표지 없이 보이는 순간이 없게 목적지 표지를 먼저 만들고,
    /// 정상 집계를 옮긴 자리에 남은 옛 표지는 옮긴 뒤에 치웁니다. 실패하면 예외 (이미 만든 목적지 표지는 안전한 쪽이라 둠).
    public static void MoveSummary(string source, string destination, Action<string, string> move)
    {
        bool invalid = File.Exists(InvalidPath(source));
        if (invalid)
        {
            MarkInvalid(destination);
        }

        move(source, destination);
        DeleteIfExists(InvalidPath(invalid ? source : destination));
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    public static void WriteForRun(string rawTracePath, IoSummary summary)
    {
        string fingerprint = RawFingerprint(rawTracePath);
        Write(RunPath(rawTracePath), summary with { RawFingerprint = fingerprint });
    }

    public static IoSummary? ReadForRun(string rawTracePath)
    {
        IoSummary? summary = Read(RunPath(rawTracePath));
        if (summary?.RawFingerprint is not { } fingerprint)
        {
            return null;
        }

        try
        {
            return string.Equals(fingerprint, RawFingerprint(rawTracePath), StringComparison.OrdinalIgnoreCase) ? summary : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static IoSummary? ReadStandalone(string path)
    {
        IoSummary? summary = Read(path);
        return summary is { RawFingerprint: not null, SessionId.Length: > 0 } ? summary : null;
    }

    public static IoSummary? ReadStandaloneForRaw(string path, string rawTracePath)
    {
        IoSummary? summary = ReadStandalone(path);
        if (summary?.RawFingerprint is not { } fingerprint)
        {
            return null;
        }

        try
        {
            return string.Equals(fingerprint, RawFingerprint(rawTracePath), StringComparison.OrdinalIgnoreCase) ? summary : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string WriteStandalone(string rawTracePath, IoSummary summary)
    {
        string[] paths = StandalonePaths(rawTracePath).ToArray();
        string? target = paths.FirstOrDefault(path => !File.Exists(path));
        if (target is null)
        {
            target = paths.OrderBy(path => File.GetLastWriteTimeUtc(path)).First();
        }

        File.Delete(target);
        string sessionId = Guid.TryParseExact(summary.SessionId, "N", out _) ? summary.SessionId : Guid.NewGuid().ToString("N");
        Write(target, summary with { RawFingerprint = RawFingerprint(rawTracePath), SessionId = sessionId });
        return target;
    }

    /// 플레이마다 남기는 디스크 전용 요약을 빈 슬롯 (없으면 가장 오래된 슬롯) 에 씁니다. 슬롯은 열두 개뿐이라 열두 번째 뒤에는 가장 오래된 것을 덮으므로 게임마다 최근 MaxPlayDiskSummaries 개만 남고,
    /// 읽기 방식이 담긴 단독 집계의 슬롯과 이름이 달라 서로 덮지 않습니다. 원시 트레이스의 지문에 묶지 않습니다 (원시 트레이스는 회차마다 다시 쓰이고, 세대는 요약의 파일 크기·수정 시각으로 대조함).
    public static string WritePlayDisk(string rawTracePath, IoSummary summary)
    {
        string[] paths = [.. PlayDiskPaths(rawTracePath)];
        string target = paths.FirstOrDefault(path => !File.Exists(path)) ?? paths.OrderBy(path => File.GetLastWriteTimeUtc(path)).First();
        File.Delete(target);
        string sessionId = Guid.TryParseExact(summary.SessionId, "N", out _) ? summary.SessionId : Guid.NewGuid().ToString("N");
        Write(target, summary with { RawFingerprint = null, SessionId = sessionId });
        return target;
    }

    /// 디스크 전용 요약 슬롯 파일을 읽습니다: 다른 종류의 집계가 이 이름에 들어 있으면 null.
    public static IoSummary? ReadPlayDisk(string path) => Read(path) is { IsDiskOnly: true } summary ? summary : null;

    public static string RawFingerprint(string rawTracePath)
    {
        using FileStream stream = File.OpenRead(rawTracePath);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    private static JObject QualityJson(IoSummaryQuality q) => new()
    {
        { "disk_total", ClassJson(q.Total) },
        { "latency_max_us", q.LatencyMaxUs },
        { "linked", ClassJson(q.Linked) },
        { "other_disk", ClassJson(q.OtherDisk) },
        { "no_file", ClassJson(q.NoFile) },
        { "not_converted", ClassJson(q.NotConverted) },
        { "dropped", ClassJson(q.Dropped) },
        { "self_read", ClassJson(q.SelfRead) },
        { "paging_reads", q.PagingReads },
        { "paging_bytes", q.PagingBytes },
        { "events_lost", q.EventsLost },
        { "block_table_dropped", q.BlockTableDropped },
        { "files_scanned", q.FilesScanned },
        { "files_changed", q.FilesChanged },
        { "files_unreadable", q.FilesUnreadable },
        { "volumes_skipped", q.VolumesSkipped },
        { "scan_ms", q.ScanMs },
        { "scan_incomplete", q.ScanIncomplete },
        { "read_modes_files_dropped", q.ReadModeFilesDropped },
        { "read_modes_dropped_requests", q.ReadModeDroppedRequests },
        { "read_modes_files_dropped_exact", q.ReadModeFilesDroppedExact },
        { "read_modes_reads_unnamed", q.ReadModeReadsUnnamed },
        { "read_modes_flag_counts", (q.ReadModeFlagCounts ?? []).Select(flag => (object?)new JObject
            {
                { "flags", (long)flag.Flags },
                { "reads", flag.Reads },
            }).ToList() },
        { "read_modes_flag_count_other", q.ReadModeFlagCountOther },
        { "read_modes_observed_until_utc", q.ReadModesObservedUntilUtc?.ToString("O", CultureInfo.InvariantCulture) },
        { "truncated", q.Truncated },
    };

    private static JObject ClassJson(IoClassTotals t) => new()
    {
        { "reads", t.Reads },
        { "bytes", t.Bytes },
        { "latency_sum_us", t.LatencySumUs },
    };

    private static bool ReadQuality(JObject o, int version, out IoSummaryQuality? quality)
    {
        quality = null;
        string[] classes = ["disk_total", "linked", "other_disk", "no_file", "not_converted", "dropped"];
        string[] numbers = ["latency_max_us", "paging_reads", "paging_bytes", "events_lost", "block_table_dropped", "files_scanned", "files_changed", "files_unreadable", "volumes_skipped", "scan_ms"];
        var totals = new IoClassTotals[classes.Length];
        for (int i = 0; i < classes.Length; i++)
        {
            if (!o.TryGetValue(classes[i], out object? value) || value is not JObject c
                || !Long(c, "reads", out long reads) || !Long(c, "bytes", out long bytes) || !Long(c, "latency_sum_us", out long latency)
                || reads < 0 || bytes < 0 || latency < 0)
            {
                return false;
            }

            totals[i] = new IoClassTotals(reads, bytes, latency);
        }

        var values = new long[numbers.Length];
        for (int i = 0; i < numbers.Length; i++)
        {
            if (!Long(o, numbers[i], out values[i]) || values[i] < 0)
            {
                return false;
            }
        }

        if (!o.TryGetValue("scan_incomplete", out object? incomplete) || incomplete is not bool scanIncomplete)
        {
            return false;
        }

        long readModeFilesDropped = 0;
        long readModeDroppedRequests = 0;
        bool readModeFilesDroppedExact = true;
        long readModeReadsUnnamed = 0;
        IoFlagCount[]? readModeFlagCounts = null;
        long readModeFlagCountOther = 0;
        DateTimeOffset? readModesObservedUntilUtc = null;
        if (version >= 4)
        {
            if (!Long(o, "read_modes_files_dropped", out readModeFilesDropped) || readModeFilesDropped < 0
                || !Long(o, "read_modes_reads_unnamed", out readModeReadsUnnamed) || readModeReadsUnnamed < 0
                || !Long(o, "read_modes_flag_count_other", out readModeFlagCountOther) || readModeFlagCountOther < 0
                || !o.TryGetValue("read_modes_flag_counts", out object? flagsValue) || flagsValue is not List<object?> flagRows
                || flagRows.Count > IoSummaryCollector.MaxReadModeFlags)
            {
                return false;
            }

            var flags = new List<IoFlagCount>(flagRows.Count);
            var seenFlags = new HashSet<uint>();
            foreach (object? value in flagRows)
            {
                if (value is not JObject flag || !Long(flag, "flags", out long rawFlags) || rawFlags < 0 || rawFlags > uint.MaxValue
                    || !Long(flag, "reads", out long reads) || reads < 0 || !seenFlags.Add((uint)rawFlags))
                {
                    return false;
                }

                flags.Add(new IoFlagCount((uint)rawFlags, reads));
            }

            readModeFlagCounts = flags.Count == 0 ? null : flags.ToArray();
            if (o.TryGetValue("read_modes_dropped_requests", out object? droppedRequestsValue)
                && (!Long(o, "read_modes_dropped_requests", out readModeDroppedRequests) || readModeDroppedRequests < 0))
            {
                return false;
            }

            if (o.TryGetValue("read_modes_files_dropped_exact", out object? droppedExactValue))
            {
                if (droppedExactValue is not bool droppedExact)
                {
                    return false;
                }

                readModeFilesDroppedExact = droppedExact;
            }

            if (!o.TryGetValue("read_modes_observed_until_utc", out object? observedValue))
            {
                return false;
            }

            if (observedValue is not null)
            {
                if (observedValue is not string observedText
                    || !DateTimeOffset.TryParse(observedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset observedUntil))
                {
                    return false;
                }

                readModesObservedUntilUtc = observedUntil.ToUniversalTime();
            }
        }

        IoClassTotals selfRead = default;
        if (o.TryGetValue("self_read", out object? selfValue))
        {
            if (selfValue is not JObject self || !Long(self, "reads", out long selfReads) || !Long(self, "bytes", out long selfBytes) || !Long(self, "latency_sum_us", out long selfLatency)
                || selfReads < 0 || selfBytes < 0 || selfLatency < 0)
            {
                return false;
            }

            selfRead = new IoClassTotals(selfReads, selfBytes, selfLatency);
        }

        quality = new IoSummaryQuality(totals[0], values[0], totals[1], totals[2], totals[3], totals[4], totals[5],
            values[1], values[2], values[3], values[4], values[5], values[6], values[7], values[8], values[9], scanIncomplete,
            readModeFilesDropped, readModeReadsUnnamed, readModeFlagCounts, readModeFlagCountOther, readModesObservedUntilUtc,
            readModeDroppedRequests, readModeFilesDroppedExact, selfRead);
        return o.TryGetValue("truncated", out object? truncatedValue)
            && truncatedValue is bool truncated
            && truncated == quality.Truncated;
    }

    private static bool Long(JObject o, string key, out long value)
    {
        value = 0;
        if (o.TryGetValue(key, out object? raw) && raw is long number)
        {
            value = number;
            return true;
        }

        return false;
    }

    private static bool String(JObject o, string key, out string? value)
    {
        value = null;
        if (o.TryGetValue(key, out object? raw) && raw is string text)
        {
            value = text;
            return true;
        }

        return false;
    }
}
