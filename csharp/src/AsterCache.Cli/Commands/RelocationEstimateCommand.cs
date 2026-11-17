using AsterCache.Core.I18n;
using AsterCache.Core.Json;
using AsterCache.Core.Model;
using AsterCache.Core.Plan;
using AsterCache.Core.Settings;
using AsterCache.Core.Trace;
using AsterCache.Service;
using AsterCache.Windows;

namespace AsterCache.Cli.Commands;

/// <summary>현재 파일 배치와 물리 읽기 집계로 재배치 효과를 읽기 전용으로 추정합니다.</summary>
internal static class RelocationEstimateCommand
{
    private const long Mib = 1024 * 1024;
    private static readonly long[] DefaultZones = [2048, 4096, 8192, 16384];
    private const string AttrsUnreadablePrefix = "attrs_unreadable: ";

    public static readonly ArgOption[] Options =
    [
        new("io-summary", ArgKind.Many, Required: true),
        new("origin-root", Required: true),
        new("plan"),
        new("zone-mib"),
        new("zone-start-lcn", Type: ArgType.Int),
    ];

    internal sealed record FileLayoutSnapshot(
        bool Exists,
        bool LengthKnown,
        long Length,
        string? GuardReason,
        IReadOnlyList<Extent>? Extents,
        int? ClusterBytes,
        IReadOnlyList<DiskExtent>? DiskExtents,
        string? LayoutFailure);

    private sealed record PolicyExclusions(IReadOnlyDictionary<string, string> ByPath, bool Complete, string Source);

    private sealed class InputEntry(string path, string status, string? sessionId, IoSummary? summary, string? detail)
    {
        public string Path { get; } = path;
        public string Status { get; set; } = status;
        public string? SessionId { get; } = sessionId;
        public IoSummary? Summary { get; } = summary;
        public string? Detail { get; set; } = detail;
        public long FilesOutsideOriginRoot { get; set; }
    }

    internal static Func<IReadOnlyList<(int Pid, string Exe)>> RunningProcesses { get; set; } = ProcessSnapshot.Take;
    internal static Func<string> ConfigPath { get; set; } = () => Path.Combine(SettingsStore.DefaultDataDir, "config.json");
    internal static Func<string, FileLayoutSnapshot> LayoutReader { get; set; } = ReadLayout;
    internal static Func<string, AsterSettings> SettingsReader { get; set; } = path => AntiCheatGuard.Load(path, Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "plans"));
    internal static Func<string, string, bool> SameDisk { get; set; } = VolumeDisks.SameDisk;

    public static int RunRaw(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        ParsedArgs parsed = ArgParser.Parse(Options, args, L.En);
        long[] zones = ParseZones(parsed.Text("zone-mib"));
        long? explicitStart = parsed.Int("zone-start-lcn");
        if (explicitStart < 0)
        {
            throw new ArgException("--zone-start-lcn must be non-negative");
        }

        string originRoot = FullPathOrOriginal(PathText.Str(parsed.Text("origin-root")!));
        if (originRoot.Length == 0)
        {
            throw new ArgException("--origin-root needs a game folder");
        }

        if (!Directory.Exists(originRoot))
        {
            throw new ArgException("--origin-root does not exist");
        }

        var entries = new List<InputEntry>();
        var sessions = new List<RelocationEstimateSession>();
        var seenSessions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string input in parsed.List("io-summary"))
        {
            string path = PathText.Str(input);
            if (File.Exists(IoSummaryStore.InvalidPath(path)))
            {
                entries.Add(new InputEntry(path, "invalid_marker", null, null, "invalid marker exists; summary was not read"));
                stderr.WriteLine($"Skipped {path}: invalid marker exists.");
                continue;
            }

            if (!File.Exists(path))
            {
                entries.Add(new InputEntry(path, "missing_summary", null, null, "summary file does not exist"));
                stderr.WriteLine($"Skipped {path}: summary file does not exist.");
                continue;
            }

            IoSummary? summary = IoSummaryStore.Read(path);
            if (summary is null)
            {
                entries.Add(new InputEntry(path, "invalid_summary", null, null, "summary format is invalid or unsupported"));
                stderr.WriteLine($"Skipped {path}: summary format is invalid or unsupported.");
                continue;
            }

            if (!seenSessions.Add(summary.SessionId))
            {
                entries.Add(new InputEntry(path, "duplicate_session", summary.SessionId, summary, "session_id was already counted"));
                stderr.WriteLine($"Skipped {path}: duplicate session_id {summary.SessionId}.");
                continue;
            }

            entries.Add(new InputEntry(path, "used", summary.SessionId, summary, null));
        }

        if (entries.All(entry => entry.Summary is null))
        {
            var noInputs = new JObject
            {
                { "origin_root", originRoot },
                { "inputs", entries.Select(EntryJson).ToList() },
                { "combined", null },
                { "notes", Notes(policyAvailable: false) },
                { "error", "no_valid_io_summaries" },
            };
            return AsterCli.Emit(stdout, noInputs, 1);
        }

        PlanFile? plan = parsed.Text("plan") is { } planPath ? PlanFile.Load(PathText.Str(planPath)) : null;
        AsterSettings? settings;
        try
        {
            settings = ReadOptionalSettings();
        }
        catch (Exception e) when (e is SettingsException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return AsterCli.Emit(stdout, new JObject
            {
                { "error", "settings_unavailable" },
                { "detail", e.Message },
            }, 1);
        }

        if (AntiCheatGuard.RefuseTrace(stdout, originRoot, []) is { } antiCheatRefusal)
        {
            return antiCheatRefusal;
        }

        if (RefuseForRunningGame(settings, originRoot, stdout) is { } gameRefusal)
        {
            return gameRefusal;
        }

        PolicyExclusions policy = ReadPolicyExclusions(settings, plan, originRoot);
        var outsideFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var layoutCache = new Dictionary<string, FileLayoutSnapshot>(StringComparer.OrdinalIgnoreCase);
        var volumeCache = new Dictionary<string, (int? ClusterBytes, IReadOnlyList<DiskExtent>? DiskExtents, string? DeviceKey)>(StringComparer.OrdinalIgnoreCase);
        var zoneStarts = new Dictionary<string, RelocationEstimateZoneStart>(StringComparer.OrdinalIgnoreCase);
        // 계획 대상 판정용: 정규화한 경로별로 한 번만 묶습니다.
        ILookup<string, PlanRange>? planRanges = plan?.Order.ToLookup(range => FullPathOrOriginal(range.Path), StringComparer.OrdinalIgnoreCase);
        foreach (InputEntry entry in entries.Where(entry => entry.Summary is not null))
        {
            IoSummary summary = entry.Summary!;
            var insideRoot = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (IoSummaryFile file in summary.Files)
            {
                string path = FullPathOrOriginal(file.Path);
                if (IsWithin(path, originRoot))
                {
                    _ = insideRoot.Add(path);
                }
                else
                {
                    _ = outsideFiles.Add(path);
                }
            }

            entry.FilesOutsideOriginRoot = summary.Files.Select(file => FullPathOrOriginal(file.Path))
                .Where(path => !IsWithin(path, originRoot)).Distinct(StringComparer.OrdinalIgnoreCase).LongCount();

            if (entry.Status == "duplicate_session")
            {
                continue;
            }

            var blocks = new List<RelocationEstimateBlock>(summary.Rows.Count);
            foreach (IoSummaryRow row in summary.Rows)
            {
                IoSummaryFile file = summary.Files[row.FileNumber];
                string fullPath = FullPathOrOriginal(file.Path);
                if (!insideRoot.Contains(fullPath))
                {
                    continue;
                }

                if (!layoutCache.TryGetValue(fullPath, out FileLayoutSnapshot? layout))
                {
                    layout = LayoutReader(fullPath);
                    layoutCache[fullPath] = layout;
                }

                string volumeKey = VolumeKey(fullPath);
                if (!volumeCache.TryGetValue(volumeKey, out var volume))
                {
                    // 볼륨 정보와 구역 시작은 클러스터 크기와 단일 디스크 위치가 유효한 조회 결과로만 정합니다.
                    if (layout.ClusterBytes is > 0 && layout.DiskExtents is { Count: 1 } disks)
                    {
                        volume = (layout.ClusterBytes, layout.DiskExtents, $"disk:{disks[0].Disk}");
                        volumeCache[volumeKey] = volume;
                        bool planOnVolume = plan is not null && SameVolume(plan.Volume, volumeKey);
                        long startLcn = explicitStart ?? (planOnVolume && plan!.Zone.StartLcn is { } planStart ? planStart : 0);
                        string source = explicitStart is not null ? "argument"
                            : planOnVolume && plan!.Zone.StartLcn is not null ? "plan"
                            : "default_zero";
                        long diskByte;
                        try
                        {
                            diskByte = checked(disks[0].StartingOffset + checked(startLcn * layout.ClusterBytes.Value));
                        }
                        catch (OverflowException)
                        {
                            return AsterCli.Emit(stdout, new JObject
                            {
                                { "error", "zone_start_lcn_out_of_range" },
                                { "volume", volumeKey },
                            }, 1);
                        }

                        zoneStarts[volumeKey] = new RelocationEstimateZoneStart(volumeKey, volume.DeviceKey, startLcn, diskByte, source,
                            planOnVolume ? plan!.Zone.Clusters : null);
                    }
                    else
                    {
                        volume = (null, null, null);
                    }
                }

                long offset;
                try
                {
                    offset = checked(row.BlockNumber * RelocationEstimate.BlockSizeBytes);
                }
                catch (OverflowException)
                {
                    blocks.Add(MakeBlock(file, row, fullPath, volumeKey, volume.DeviceKey, 0, 0,
                        EstimateBlockClass.MissingSection, "block_offset_overflow", [], null, null, null));
                    continue;
                }

                EstimateBlockClass classification = Classification(file, layout, offset, out string? reason);
                long logicalBytes = layout.LengthKnown && layout.Exists && offset < layout.Length
                    ? Math.Min(RelocationEstimate.BlockSizeBytes, layout.Length - offset)
                    : 0;
                IReadOnlyList<RelocationEstimatePhysicalExtent>? mappedPositions = Position(layout, offset, logicalBytes, out long occupiedBytes);
                if (mappedPositions is null && classification == EstimateBlockClass.Movable)
                {
                    classification = EstimateBlockClass.LayoutUnavailable;
                    reason = "layout_coordinate_overflow";
                }

                IReadOnlyList<RelocationEstimatePhysicalExtent> positions = mappedPositions ?? [];
                if (classification == EstimateBlockClass.ChangedDuringSession)
                {
                    positions = [];
                    occupiedBytes = 0;
                }

                if (classification == EstimateBlockClass.Movable && positions.Count == 0)
                {
                    classification = EstimateBlockClass.MissingSection;
                    reason = "block has no current allocated extent";
                }

                bool? inPlan = plan is null ? null : PlanCoverage(plan, planRanges!, fullPath, offset);
                string? policyReason = PolicyReason(fullPath, policy);
                bool? policyCandidate = classification == EstimateBlockClass.Movable
                    ? policyReason is not null ? false : policy.Complete ? true : null
                    : null;
                blocks.Add(MakeBlock(file, row, fullPath, volumeKey, volume.DeviceKey, logicalBytes, occupiedBytes,
                    classification, reason, positions, inPlan, policyCandidate, policyReason));
            }

            sessions.Add(new RelocationEstimateSession(entry.Path, summary.SessionId, summary.ObservedAtUtc, blocks,
                summary.Quality.Truncated, summary.Quality.EventsLost, summary.Quality.BlockTableDropped));
        }

        RelocationEstimateReport report;
        try
        {
            report = RelocationEstimate.Calculate(sessions, zones, zoneStarts.Values.ToList(), plan is not null);
        }
        catch (OverflowException)
        {
            return AsterCli.Emit(stdout, new JObject { { "error", "estimate_value_out_of_range" } }, 1);
        }

        var resultsBySource = report.Sessions.ToDictionary(result => result.SourcePath, StringComparer.OrdinalIgnoreCase);
        var duplicateIds = new HashSet<string>(report.DuplicateSessionIds, StringComparer.OrdinalIgnoreCase);
        foreach (InputEntry entry in entries.Where(entry => entry.Summary is not null))
        {
            IoSummary summary = entry.Summary!;
            if (entry.Status == "duplicate_session")
            {
                continue;
            }

            if (duplicateIds.Contains(summary.SessionId))
            {
                entry.Status = "duplicate_session";
                entry.Detail = "session_id was already counted";
            }
            else if (resultsBySource.ContainsKey(entry.Path))
            {
                entry.Status = "used";
                entry.Detail = null;
            }
        }

        JObject output = new()
        {
            { "origin_root", originRoot },
            { "files_outside_origin_root", (long)outsideFiles.Count },
            { "policy_exclusion_source", policy.Source },
            { "inputs", entries.Select(entry => InputResultJson(entry, resultsBySource)).ToList() },
            { "zone_starts", zoneStarts.Values.OrderBy(start => start.VolumeKey, StringComparer.OrdinalIgnoreCase).Select(ZoneStartJson).ToList() },
            { "read_mode", "unknown" },
            { "combined", AnalysisJson(report.Combined) },
            { "notes", Notes(policy.Complete) },
        };

        foreach (RelocationEstimateSessionResult result in report.Sessions)
        {
            stderr.WriteLine($"Estimated {Path.GetFileName(result.SourcePath)}: {result.Analysis.Total.Blocks} blocks, {MiB(result.Analysis.Total.ReadBytes):0.0} MiB read.");
        }

        stderr.WriteLine($"Combined estimate: {report.Combined.Total.Blocks} unique blocks from {report.Sessions.Count} session(s).");
        return AsterCli.Emit(stdout, output);
    }

    private static AsterSettings? ReadOptionalSettings()
    {
        string config = ConfigPath();
        try
        {
            _ = File.GetAttributes(config);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        return SettingsReader(config);
    }

    private static int? RefuseForRunningGame(AsterSettings? settings, string originRoot, TextWriter output)
    {
        if (settings is null)
        {
            return null;
        }

        HashSet<string> running = new(RunningProcesses().Select(process => process.Exe), StringComparer.OrdinalIgnoreCase);
        GameSettings? game = settings.Games.FirstOrDefault(candidate => candidate.ExeNames.Any(running.Contains)
            && SameDisk(originRoot, candidate.OriginRoot));
        return game is null
            ? null
            : AsterCli.Emit(output, new JObject
            {
                { "error", "game_running" },
                { "game", game.Name },
                { "detail", "a registered game is running on the target disk" },
            }, 1);
    }

    private static PolicyExclusions ReadPolicyExclusions(AsterSettings? settings, PlanFile? plan, string originRoot)
    {
        if (settings?.Games.FirstOrDefault(game => SamePath(game.OriginRoot, originRoot)) is { } game
            && TryReadVolatileMarks(AutoTracer.VolatilePath(game), out IReadOnlyDictionary<string, string> marks))
        {
            return new PolicyExclusions(NormalizeExclusions(marks), true, "trace_marks");
        }

        if (plan is not null && TryReadPlanExclusions(plan, out IReadOnlyDictionary<string, string> planExclusions))
        {
            return new PolicyExclusions(NormalizeExclusions(planExclusions), true, "plan");
        }

        return new PolicyExclusions(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), false, "unavailable");
    }

    private static bool TryReadVolatileMarks(string path, out IReadOnlyDictionary<string, string> marks)
    {
        marks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            _ = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }

        try
        {
            if (JsonRead.ReadFile(path) is not JObject json)
            {
                return false;
            }

            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach ((string file, object? value) in json)
            {
                if (value is not string reason)
                {
                    return false;
                }

                result[FullPathOrOriginal(file)] = reason;
            }

            marks = result;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryReadPlanExclusions(PlanFile plan, out IReadOnlyDictionary<string, string> exclusions)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool found = false;
        if (plan.Stats.TryGetValue(VolatileFiles.StatsKey, out object? value))
        {
            if (value is not JObject json)
            {
                exclusions = result;
                return false;
            }

            found = true;
            foreach ((string path, object? reason) in json)
            {
                result[FullPathOrOriginal(path)] = reason as string ?? "excluded by plan";
            }
        }

        if (plan.Stats.TryGetValue(VolatileFiles.RuleKey, out object? rule) && rule is long version && version >= VolatileFiles.RuleVersion)
        {
            found = true;
        }

        exclusions = result;
        return found;
    }

    private static IReadOnlyDictionary<string, string> NormalizeExclusions(IReadOnlyDictionary<string, string> source)
    {
        var normalized = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach ((string path, string reason) in source)
        {
            normalized[FullPathOrOriginal(path)] = reason;
        }

        return normalized;
    }

    private static string? PolicyReason(string path, PolicyExclusions policy)
    {
        if (VolatileFiles.NameReason(path) is { } nameReason)
        {
            return nameReason;
        }

        return policy.ByPath.TryGetValue(path, out string? reason) ? reason : null;
    }

    private static FileLayoutSnapshot ReadLayout(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return new FileLayoutSnapshot(false, false, 0, null, null, null, null, "missing_file");
        }
        catch (DirectoryNotFoundException)
        {
            return new FileLayoutSnapshot(false, false, 0, null, null, null, null, "missing_file");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FileLayoutSnapshot(true, false, 0, null, null, null, null, "file_metadata_unavailable");
        }

        if (IoLayoutScan.HasReparsePoint(path))
        {
            return new FileLayoutSnapshot(true, false, 0, null, null, null, null, "reparse_point_path");
        }

        long length;
        try
        {
            length = new FileInfo(path).Length;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return new FileLayoutSnapshot(true, false, 0, null, null, null, null, "file_metadata_unavailable");
        }

        string? guard = FileGuards.SkipReason(path);
        IReadOnlyList<Extent>? extents = FileLayout.AllocatedExtents(path);
        int? clusterBytes = FileLayout.ClusterBytes(path);
        IReadOnlyList<DiskExtent>? diskExtents = VolumeDisks.DiskExtentsOf(path);
        string fileSystem = path.Length >= 2 && path[1] == ':' ? VolumeInfo.FileSystem(path[..2]) : string.Empty;
        string? failure = !IoLayoutScan.ConvertsFileSystem(fileSystem) ? "unsupported_file_system"
            : extents is null ? "extent_query_failed"
            : clusterBytes is not > 0 ? "cluster_size_unavailable"
            : diskExtents is null ? "volume_disk_layout_unavailable"
            : diskExtents.Count != 1 ? "multi_disk_volume"
            : null;
        return new FileLayoutSnapshot(true, true, length, guard, extents, clusterBytes, diskExtents, failure);
    }

    private static EstimateBlockClass Classification(IoSummaryFile file, FileLayoutSnapshot layout, long offset, out string? reason)
    {
        reason = null;
        if (!layout.Exists)
        {
            return EstimateBlockClass.MissingFile;
        }

        if (layout.LayoutFailure is "file_metadata_unavailable" or "reparse_point_path")
        {
            reason = layout.LayoutFailure;
            return EstimateBlockClass.LayoutUnavailable;
        }

        if (!layout.LengthKnown)
        {
            reason = layout.LayoutFailure ?? "file_metadata_unavailable";
            return EstimateBlockClass.LayoutUnavailable;
        }

        if (offset >= layout.Length)
        {
            reason = "block starts beyond end of file";
            return EstimateBlockClass.MissingSection;
        }

        if (file.ChangedDuringSession)
        {
            return EstimateBlockClass.ChangedDuringSession;
        }

        if (layout.GuardReason is { } guard)
        {
            if (guard.StartsWith(AttrsUnreadablePrefix, StringComparison.Ordinal))
            {
                reason = guard;
                return EstimateBlockClass.LayoutUnavailable;
            }

            reason = guard;
            return EstimateBlockClass.Unmovable;
        }

        if (layout.LayoutFailure is { } failure)
        {
            reason = failure;
            return EstimateBlockClass.LayoutUnavailable;
        }

        // 클러스터가 블록보다 크면 블록 시작이 클러스터 경계가 아니라 점유 길이를 정확히 셀 수 없습니다.
        if (layout.ClusterBytes > RelocationEstimate.BlockSizeBytes)
        {
            reason = "cluster_larger_than_block";
            return EstimateBlockClass.LayoutUnavailable;
        }

        return EstimateBlockClass.Movable;
    }

    private static IReadOnlyList<RelocationEstimatePhysicalExtent>? Position(
        FileLayoutSnapshot layout,
        long offset,
        long logicalBytes,
        out long occupiedBytes)
    {
        occupiedBytes = 0;
        if (layout.Extents is null || layout.ClusterBytes is not > 0 || layout.DiskExtents is not { Count: 1 } disks || logicalBytes <= 0)
        {
            return [];
        }

        try
        {
            long cluster = layout.ClusterBytes.Value;
            long logicalEnd = checked(offset + logicalBytes);
            long occupiedEnd = checked(((logicalEnd + cluster - 1) / cluster) * cluster);
            var pieces = new List<(long Vcn, RelocationEstimatePhysicalExtent Physical)>();
            foreach (Extent extent in layout.Extents.Where(item => item.Clusters > 0 && item.Lcn >= 0).OrderBy(item => item.Vcn))
            {
                long fileStart = checked(extent.Vcn * cluster);
                long fileEnd = checked(checked(extent.Vcn + extent.Clusters) * cluster);
                long pieceStart = Math.Max(offset, fileStart);
                long pieceEnd = Math.Min(occupiedEnd, fileEnd);
                if (pieceStart >= pieceEnd)
                {
                    continue;
                }

                long fileDelta = checked(pieceStart - fileStart);
                long lcn = checked(extent.Lcn + fileDelta / cluster);
                long physicalStart = checked(disks[0].StartingOffset + checked(lcn * cluster) + fileDelta % cluster);
                long physicalEnd = checked(physicalStart + checked(pieceEnd - pieceStart));
                pieces.Add((pieceStart, new RelocationEstimatePhysicalExtent(physicalStart, physicalEnd)));
            }

            IReadOnlyList<RelocationEstimatePhysicalExtent> result = pieces.OrderBy(piece => piece.Vcn).Select(piece => piece.Physical).ToArray();
            foreach (RelocationEstimatePhysicalExtent piece in result)
            {
                occupiedBytes = checked(occupiedBytes + checked(piece.EndByte - piece.StartByte));
            }

            return result;
        }
        catch (OverflowException)
        {
            occupiedBytes = 0;
            return null;
        }
    }

    private static bool? PlanCoverage(PlanFile plan, ILookup<string, PlanRange> ranges, string path, long offset)
    {
        if (plan.ClusterBytes <= 0)
        {
            return null;
        }

        if (!SameVolume(plan.Volume, VolumeKey(path)))
        {
            return false;
        }

        try
        {
            long end = checked(offset + RelocationEstimate.BlockSizeBytes);
            long firstVcn = offset / plan.ClusterBytes;
            long lastVcn = checked((end - 1) / plan.ClusterBytes + 1);
            return ranges[path].Any(range => range.Vcn < lastVcn && firstVcn < checked(range.Vcn + range.Clusters));
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static RelocationEstimateBlock MakeBlock(IoSummaryFile file, IoSummaryRow row, string path, string volumeKey,
        string? deviceKey, long logicalBytes, long occupiedBytes, EstimateBlockClass classification, string? reason,
        IReadOnlyList<RelocationEstimatePhysicalExtent> positions, bool? inPlan, bool? policyCandidate, string? policyReason) =>
        new(path, row.BlockNumber, row.Requests, row.Bytes, row.LatencySumUs, row.LatencyMaxUs, row.FirstSessionUs, row.PagingRequests,
            volumeKey, deviceKey, logicalBytes, occupiedBytes, classification, reason, positions, inPlan, policyCandidate, policyReason);

    private static string FullPathOrOriginal(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    private static bool SamePath(string first, string second) =>
        string.Equals(FullPathOrOriginal(first).TrimEnd('\\', '/'), FullPathOrOriginal(second).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static string VolumeKey(string path) => (Path.GetPathRoot(path)?.TrimEnd('\\', '/') ?? string.Empty).ToUpperInvariant();

    private static bool SameVolume(string planVolume, string volumeKey) =>
        string.Equals(planVolume.TrimEnd('\\', '/'), volumeKey.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private static bool IsWithin(string path, string directory)
    {
        string root = FullPathOrOriginal(directory).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        string fullPath = FullPathOrOriginal(path);
        string rootWithoutSeparator = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath.TrimEnd('\\', '/'), rootWithoutSeparator.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static long[] ParseZones(string? text)
    {
        if (text is null)
        {
            return DefaultZones;
        }

        string[] tokens = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            throw new ArgException("--zone-mib needs one or more comma-separated positive integers");
        }

        var sizes = new List<long>(tokens.Length);
        foreach (string token in tokens)
        {
            if (!long.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long size)
                || size is < 1 or > 1_048_576)
            {
                throw new ArgException("--zone-mib values must be between 1 and 1048576");
            }

            if (!sizes.Contains(size))
            {
                sizes.Add(size);
            }
        }

        return [.. sizes];
    }

    private static object InputResultJson(InputEntry entry, IReadOnlyDictionary<string, RelocationEstimateSessionResult> results)
    {
        var json = new JObject
        {
            { "path", entry.Path },
            { "status", entry.Status },
            { "session_id", entry.SessionId },
            { "detail", entry.Detail },
            { "files_outside_origin_root", entry.FilesOutsideOriginRoot },
        };
        if (entry.Summary is { } summary)
        {
            json.Add("quality", new JObject
            {
                { "truncated", summary.Quality.Truncated },
                { "events_lost", summary.Quality.EventsLost },
                { "block_table_dropped", summary.Quality.BlockTableDropped },
                { "files_changed", summary.Quality.FilesChanged },
                { "scan_incomplete", summary.Quality.ScanIncomplete },
            });
        }

        if (entry.Status == "used" && results.TryGetValue(entry.Path, out RelocationEstimateSessionResult? result))
        {
            json.Add("estimate", AnalysisJson(result.Analysis));
        }

        return json;
    }

    private static object EntryJson(InputEntry entry) => new JObject
    {
        { "path", entry.Path },
        { "status", entry.Status },
        { "session_id", entry.SessionId },
        { "detail", entry.Detail },
        { "files_outside_origin_root", entry.FilesOutsideOriginRoot },
    };

    private static JObject AnalysisJson(RelocationEstimateAnalysis analysis) => new()
    {
        { "total", TotalsJson(analysis.Total) },
        { "classification", new JObject
            {
                { "movable", TotalsJson(analysis.Movable) },
                { "unmovable_by_reason", GroupJson(analysis.Unmovable) },
                { "changed_during_session", TotalsJson(analysis.ChangedDuringSession) },
                { "missing_file", TotalsJson(analysis.MissingFile) },
                { "missing_section", TotalsJson(analysis.MissingSection) },
                { "layout_unavailable", GroupJson(analysis.LayoutUnavailable) },
            }
        },
        { "plan_coverage", analysis.PlanCoverage is { } coverage ? CoverageJson(coverage) : null },
        { "current_policy", new JObject
            {
                { "attribute_movable", TotalsJson(analysis.Movable) },
                { "candidate", TotalsJson(analysis.PolicyCoverage.Candidate) },
                { "excluded_by_reason", GroupJson(analysis.PolicyCoverage.ExcludedByReason) },
                { "unknown", TotalsJson(analysis.PolicyCoverage.Unknown) },
            }
        },
        { "paging", new JObject
            {
                { "non_paging", TotalsJson(analysis.NonPaging) },
                { "paging_only", TotalsJson(analysis.PagingOnly) },
                { "unknown", TotalsJson(analysis.PagingUnknown) },
                { "read_mode", "unknown" },
            }
        },
        { "sequence", new JObject
            {
                { "current", SequenceJson(analysis.CurrentSequence) },
                { "zones", analysis.Scenarios.Select(ScenarioJson).ToList() },
            }
        },
    };

    private static JObject CoverageJson(RelocationEstimatePlanCoverage coverage) => new()
    {
        { "inside", TotalsJson(coverage.Inside) },
        { "outside", TotalsJson(coverage.Outside) },
        { "unknown", TotalsJson(coverage.Unknown) },
    };

    private static object ScenarioJson(RelocationEstimateScenario scenario) => new JObject
    {
        { "zone_mib", scenario.ZoneMib },
        { "current_zone_coverage", CoverageJson(scenario.CurrentZoneCoverage) },
        { "by_latency", SelectionJson(scenario.ByLatency) },
        { "by_requests", SelectionJson(scenario.ByRequests) },
    };

    private static object SelectionJson(RelocationEstimateSelection selection) => new JObject
    {
        { "criterion", selection.Criterion },
        { "selected", TotalsJson(selection.Selected) },
        { "selected_read_bytes_ratio", selection.SelectedReadBytesRatio },
        { "selected_latency_ratio", selection.SelectedLatencyRatio },
        { "moved_blocks", selection.MovedBlocks },
        { "bytes_to_move", selection.BytesToMove },
        { "blocks_already_in_zone", selection.BlocksAlreadyInZone },
        { "required_contiguous_bytes", selection.RequiredContiguousBytes },
        { "virtual_sequence", SequenceJson(selection.VirtualSequence) },
    };

    private static object ZoneStartJson(RelocationEstimateZoneStart start) => new JObject
    {
        { "volume", start.VolumeKey },
        { "device", start.DeviceKey },
        { "start_lcn", start.StartLcn },
        { "disk_start_byte", start.StartByte },
        { "source", start.Source },
        { "plan_zone_clusters", start.PlanZoneClusters },
    };

    private static JObject TotalsJson(RelocationEstimateTotals totals) => new()
    {
        { "blocks", totals.Blocks },
        { "requests", totals.Requests },
        { "read_bytes", totals.ReadBytes },
        { "latency_sum_us", totals.LatencySumUs },
        { "latency_max_us", totals.LatencyMaxUs },
        { "paging_requests", totals.PagingRequests },
    };

    private static JObject GroupJson(IReadOnlyDictionary<string, RelocationEstimateTotals> groups)
    {
        var json = new JObject();
        foreach ((string reason, RelocationEstimateTotals totals) in groups.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            json.Add(reason, TotalsJson(totals));
        }

        return json;
    }

    private static JObject SequenceJson(RelocationEstimateSequence sequence) => new()
    {
        { "blocks_with_location", sequence.BlocksWithLocation },
        { "blocks_without_location", sequence.BlocksWithoutLocation },
        { "transitions", sequence.Transitions },
        { "movement_bytes", sequence.MovementBytes },
        { "contiguous_runs", sequence.ContiguousRuns },
        { "median_distance_bytes", sequence.MedianDistanceBytes },
        { "p90_distance_bytes", sequence.P90DistanceBytes },
        { "maximum_distance_bytes", sequence.MaximumDistanceBytes },
    };

    private static List<object?> Notes(bool policyAvailable)
    {
        var notes = new List<object?>
        {
            "block order uses each block's first observed time; repeated-read order is not available",
            "movement distance is a disk LCN-position comparison, not actual head travel or loading-time improvement",
            "layout is queried now and may differ from the layout during play",
            "latency sums include overlapping requests as separate contributions",
            "read-mode classification is unknown unless a supported read-mode aggregate is available",
            "a zone is assumed to be contiguously free from its start position, and the plan's fixed target positions are not counted as inside the zone",
        };
        if (!policyAvailable)
        {
            notes.Add("policy exclusions are not reflected");
        }

        return notes;
    }

    private static double MiB(long bytes) => bytes / (double)Mib;
}
