using System.Globalization;
using AsterCache.Core.Json;
using AsterCache.Core.Prefetch;

namespace AsterCache.Core.Trace;

/// 파일 한 개의 현재 크기와 수정 시각 (UTC ticks). 저장한 목록이 지금 파일과 맞는지 보는 표지입니다.
public readonly record struct LoadRefillFileStat(long Size, long MTimeTicks);

/// 한 플레이의 I/O 집계와 그 안에서 등록 실행 파일이 새로 시작한 시각 (집계의 관찰 시작을 0 으로 한 마이크로초).
public sealed record LoadRefillSource(IoSummary Summary, IReadOnlyList<long> StartsUs);

/// 로드 구간에서 처음 접근한 64 KiB 블록 하나. Count 는 그 블록이 나온 플레이 수, LatencyUs 는 플레이별 지연 합의 합입니다.
/// Score 는 플레이 전체 기록의 순위 목록에서만 채우는 경기 가중 점수 (LoadRefillPlanner.PlayOrder 가 씀): 0 이면 구간을 모르는 목록이라 Count 로 봅니다.
public sealed record LoadRefillBlock(string Path, long Block, int Count, long LatencyUs, int Score = 0);

/// 모은 로드 구간 블록 목록 (점수 순)과 모으는 과정의 집계.
public sealed record LoadRefillList(
    IReadOnlyList<LoadRefillBlock> Blocks,
    IReadOnlyDictionary<string, LoadRefillFileStat> Files,
    int Plays,
    int Loads,
    int PlaysWithoutStarts,
    long SkippedNoCacheBlocks,
    long DroppedByCapBlocks)
{
    public long Bytes => Blocks.Count * (long)LoadBoundaries.Block;
}

/// 앞선 플레이들의 로드 구간 블록을 모아 점수 순으로 줄이고, 읽을 때는 디스크 위치 순 목록으로 바꾸는 순수 함수입니다.
public static class LoadRefillPlanner
{
    /// 실행 파일 시작부터 이 시간 안에 처음 접근한 블록이 그 로드의 블록입니다.
    /// 근거: 실제 플레이 기록에서 맵 로드의 읽기가 시작 뒤 120 초까지 늘어났고 (단계 스트림이 첫 휴지에서 멈춘 뒤에도), 시작 스트림 상한 기본값 (start_stream_cap_seconds) 과 같습니다.
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(120);

    /// 목록 크기의 고정 상한. 근거: 실제 로드 한 번의 창 안 첫 접근 블록이 4.1 GiB (가장 큰 첫 로드) 였고 세 로드의 합집합이 약 2.9 GiB 였으며,
    /// 로드 중 사용 가능 메모리는 약 11 GiB 였으므로 그 절반 아래에서 가장 큰 로드 하나를 거의 담는 값으로 4 GiB 를 둡니다. 실제 크기는 메모리 게이트가 다시 줄입니다.
    public const long CapBytes = 4L << 30;

    /// 플레이 전체 기록으로 만드는 순위 목록의 고정 상한. 근거: 게임 사이 예열 예산이 5.8 GiB 안팎 (게임이 많거나 메모리가 넉넉하면 8~11 GiB) 이고,
    /// 조사에서 예산 안에 담긴 순위 앞쪽 4~5.5 GiB 가 경기 중 지연의 대부분을 덮었으므로 예산 수준인 8 GiB 까지만 둡니다 (그 이상은 예산 밖이라 읽히지 않음).
    public const long RankedCapBytes = 8L << 30;

    /// 게임이 스스로 쓰는 파일의 블록이 순위에 들려면 나와야 하는 플레이 수. 근거: 2026-11-21 Call of Duty 플레이에서 가변 파일 디스크 읽기 블록의 81% (지연으로는 96%) 가 이전 세 플레이 중 하나에서도 읽힌 블록이었고,
    /// 한 플레이에만 나온 블록은 그 플레이에 새로 쓴 데이터일 수 있어 (처음 보는 블록은 지연 3% 이하) 두 플레이 이상 반복해 읽힌 블록만 정적 파일의 블록과 같은 점수 (플레이 수, 지연 합) 로 줄 세웁니다.
    public const int WrittenMinPlays = 2;

    /// 유지·붙들기 목록에서 경기 가중 점수 순으로 앞에 두는 양 (스파스 파일 블록은 이 양에 세지 않음). 근거: 과거 플레이 교차 검증 (w121) 에서 2 GiB 까지는 이 점수 순이 지금 순위 (플레이 수, 지연 합) 보다 경기·뒤쪽 로딩의 페이징 읽기를 더 많이 덮었고
    /// (일반 파일 경기 16.9 → 22.8%, 뒤쪽 로딩 15.6 → 27.8%), 3~5 GiB 에서는 비슷하거나 지금 순위가 앞섰습니다.
    public const long PlayFrontBytes = 2L << 30;

    /// 실행 직후 로딩 구간 밖 (경기·뒤쪽 로딩) 에서 한 번만 닿은 페이징 읽기의 점수 (그 밖의 플레이는 1점). 근거: w121 교차 검증의 규칙 R2 (경기·후속 로딩 단일 접촉 페이징 3점, 시각을 나눌 수 없는 행 1점).
    public const int PlayPhaseWeight = 3;

    /// 플레이들의 로드 구간 블록 합집합. 블록은 (여러 플레이에서 나온 횟수 내림, 지연 합 내림, 경로, 번호) 순이고 capBytes 를 넘는 뒷부분은 자릅니다.
    /// stat 은 지금 있는 파일의 크기·수정 시각을 돌려주고 (없거나 허용 밖·가변 파일이면 null → 그 파일 블록은 뺌), 블록이 파일 끝을 넘으면 뺍니다.
    /// 캐시를 거치지 않고만 읽힌 블록 (페이징 요청 0) 과 관찰 중 바뀐 파일은 뺍니다. 시작 시각이 없는 집계는 건너뛰고 PlaysWithoutStarts 로 셉니다.
    /// window 가 null 이면 시작 시각과 무관하게 플레이 전체 구간의 블록을 모읍니다 (시작 시각이 없는 집계도 한 플레이로 세고 Loads 는 0).
    /// window 가 null 일 때는 블록마다 경기 가중 점수도 셉니다: 페이징 읽기가 난 플레이마다 1점, 그 읽기가 한 번만 닿았고 (Requests 1) 실행 파일 시작 뒤 Window 가 지난 (경기·뒤쪽 로딩) 시각이면 PlayPhaseWeight 점.
    /// 시작 시각이 없거나 첫 시작보다 앞선 행은 구간을 나눌 수 없어 1점입니다.
    /// isWritten 이 있으면 (순위 목록이 게임이 쓰는 파일도 담을 때) 그 파일의 블록은 WrittenMinPlays 개 이상의 플레이에서 나온 것만 담습니다.
    /// appendOnly 가 참인 파일 (크기가 줄어든 적이 없어 덧붙이기로 보는 파일) 은 관찰 중 수정된 플레이의 읽기도 점수에 넣습니다: 이미 쓰인 위치의 내용은 바뀌지 않으므로 그 플레이에서 읽힌 위치 (시작 때 크기 안이든 덧붙인 뒤든) 는 다음 플레이에도 있습니다.
    public static LoadRefillList Collect(IEnumerable<LoadRefillSource> sources, Func<string, LoadRefillFileStat?> stat, TimeSpan? window, long capBytes, Func<string, bool>? isWritten = null, Func<string, bool>? appendOnly = null)
    {
        long windowUs = window is { } w ? (long)w.TotalMicroseconds : 0;
        var stats = new Dictionary<string, LoadRefillFileStat?>(StringComparer.OrdinalIgnoreCase);
        var merged = new Dictionary<string, Dictionary<long, (int Count, long LatencyUs, int Score)>>(StringComparer.OrdinalIgnoreCase);
        int plays = 0;
        int loads = 0;
        int withoutStarts = 0;
        long skippedNoCache = 0;
        foreach (LoadRefillSource source in sources)
        {
            long[] starts = [.. source.StartsUs.Distinct().Order()];
            if (window is not null && starts.Length == 0)
            {
                withoutStarts++;
                continue;
            }

            plays++;
            IoSummary summary = source.Summary;
            var loadsHit = new HashSet<int>();
            foreach (IoSummaryRow row in summary.Rows)
            {
                int index = 0;
                if (window is not null)
                {
                    int at = Array.BinarySearch(starts, row.FirstSessionUs);
                    index = at >= 0 ? at : ~at - 1; // 첫 접근 시각 이하의 가장 늦은 시작
                    if (index < 0 || row.FirstSessionUs - starts[index] >= windowUs)
                    {
                        continue;
                    }
                }

                IoSummaryFile file = summary.Files[row.FileNumber];
                if (file.ChangedDuringSession && appendOnly?.Invoke(file.Path) != true)
                {
                    continue;
                }

                if (!stats.TryGetValue(file.Path, out LoadRefillFileStat? current))
                {
                    stats[file.Path] = current = stat(file.Path);
                }

                if (current is not { Size: > 0 } now || row.BlockNumber > (now.Size - 1) / LoadBoundaries.Block)
                {
                    continue;
                }

                if (row.PagingRequests == 0)
                {
                    skippedNoCache++; // 캐시 없이만 읽힌 블록은 메모리에 채워도 게임 읽기가 쓰지 못함
                    continue;
                }

                if (!merged.TryGetValue(file.Path, out Dictionary<long, (int Count, long LatencyUs, int Score)>? blocks))
                {
                    merged[file.Path] = blocks = [];
                }

                int points = window is null && row.Requests == 1 && OutsideLoadWindow(starts, row.FirstSessionUs) ? PlayPhaseWeight : 1;
                blocks[row.BlockNumber] = blocks.TryGetValue(row.BlockNumber, out (int Count, long LatencyUs, int Score) old)
                    ? (old.Count + 1, old.LatencyUs + row.LatencySumUs, old.Score + points)
                    : (1, row.LatencySumUs, points);
                if (window is not null)
                {
                    loadsHit.Add(index);
                }
            }

            loads += loadsHit.Count;
        }

        var all = new List<LoadRefillBlock>();
        foreach ((string path, Dictionary<long, (int Count, long LatencyUs, int Score)> blocks) in merged)
        {
            int minPlays = isWritten?.Invoke(path) == true ? WrittenMinPlays : 1;
            all.AddRange(blocks.Where(b => b.Value.Count >= minPlays).Select(b => new LoadRefillBlock(path, b.Key, b.Value.Count, b.Value.LatencyUs, window is null ? b.Value.Score : 0)));
        }

        all.Sort((a, b) =>
        {
            int c = b.Count.CompareTo(a.Count);
            if (c == 0)
            {
                c = b.LatencyUs.CompareTo(a.LatencyUs);
            }

            if (c == 0)
            {
                c = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
            }

            return c != 0 ? c : a.Block.CompareTo(b.Block);
        });
        long keep = Math.Max(0, capBytes) / LoadBoundaries.Block;
        int kept = (int)Math.Min(all.Count, keep);
        List<LoadRefillBlock> result = all.GetRange(0, kept);
        var files = new Dictionary<string, LoadRefillFileStat>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in result.Select(b => b.Path).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            files[path] = stats[path]!.Value;
        }

        return new LoadRefillList(result, files, plays, loads, withoutStarts, skippedNoCache, all.Count - kept);
    }

    // 첫 접근 시각이 어느 실행 파일 시작 뒤 Window 가 지난 때인가 (시작이 없거나 첫 시작보다 앞서면 구간을 모르므로 거짓).
    private static bool OutsideLoadWindow(long[] starts, long firstUs)
    {
        int at = Array.BinarySearch(starts, firstUs);
        int index = at >= 0 ? at : ~at - 1;
        return index >= 0 && firstUs - starts[index] >= (long)Window.TotalMicroseconds;
    }

    /// 유지·붙들기가 쓰는 순서: 앞 frontBytes (스파스 파일 블록은 세지 않음) 는 경기 가중 점수 순 (같으면 목록 순서), 그 뒤는 목록 순서 (지금 순위) 그대로입니다. 같은 블록은 한 번만 나옵니다.
    /// 점수가 있는 블록이 하나도 없으면 (구간을 모르는 예전 목록) 목록 그대로를 돌려줍니다.
    public static IReadOnlyList<LoadRefillBlock> PlayOrder(IReadOnlyList<LoadRefillBlock> ranked, Func<string, bool> isSparse, long frontBytes = PlayFrontBytes)
    {
        if (ranked.All(b => b.Score == 0))
        {
            return ranked;
        }

        var sparse = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        long left = Math.Max(0, frontBytes) / LoadBoundaries.Block;
        var taken = new bool[ranked.Count];
        var result = new List<LoadRefillBlock>(ranked.Count);
        foreach (int i in Enumerable.Range(0, ranked.Count).OrderByDescending(i => ranked[i].Score == 0 ? ranked[i].Count : ranked[i].Score).ThenBy(i => i))
        {
            if (left <= 0)
            {
                break;
            }

            LoadRefillBlock block = ranked[i];
            if (!sparse.TryGetValue(block.Path, out bool s))
            {
                sparse[block.Path] = s = isSparse(block.Path);
            }

            taken[i] = true;
            result.Add(block);
            left -= s ? 0 : 1;
        }

        result.AddRange(ranked.Where((_, i) => !taken[i]));
        return result;
    }

    /// 점수 순 블록의 앞에서 capBytes 만큼을 골라 이어진 블록끼리 합친 뒤 디스크 위치 순 (배치를 모르는 파일은 맨 뒤 경로·오프셋 순) 읽기 목록으로 만듭니다.
    /// 파일이 지금 저장할 때와 다르거나 (크기·수정 시각, current 가 null 이면 없음) 그 파일 블록은 뺍니다. 틈은 메우지 않아 고른 블록만 읽습니다.
    public static List<ReadOp> ToOps(LoadRefillList list, Func<string, LoadRefillFileStat?> current, long capBytes, Func<string, FileLayoutInfo?>? layout, CancellationToken cancellationToken = default)
    {
        long keep = Math.Max(0, capBytes) / LoadBoundaries.Block;
        var valid = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
        long taken = 0;
        foreach (LoadRefillBlock block in list.Blocks)
        {
            if (taken >= keep)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!valid.TryGetValue(block.Path, out bool ok))
            {
                valid[block.Path] = ok = list.Files.TryGetValue(block.Path, out LoadRefillFileStat saved) && current(block.Path) is { } now && now == saved;
            }

            if (!ok)
            {
                continue;
            }

            if (!byPath.TryGetValue(block.Path, out List<long>? blocks))
            {
                byPath[block.Path] = blocks = [];
            }

            blocks.Add(block.Block);
            taken++;
        }

        var ranges = new List<ReadRange>();
        foreach ((string path, List<long> blocks) in byPath)
        {
            blocks.Sort();
            long size = list.Files[path].Size;
            long first = blocks[0];
            long last = first;
            for (int i = 1; i <= blocks.Count; i++)
            {
                if (i < blocks.Count && blocks[i] == last + 1)
                {
                    last = blocks[i];
                    continue;
                }

                long offset = first * LoadBoundaries.Block;
                ranges.Add(new ReadRange(path, offset, Math.Min(size, (last + 1) * LoadBoundaries.Block) - offset));
                if (i < blocks.Count)
                {
                    first = last = blocks[i];
                }
            }
        }

        return Scheduler.Schedule(ranges, "physical", null, 0, layout, null, cancellationToken: cancellationToken).Ops;
    }

    /// 점수 순 블록을 그 순서 그대로 블록 하나씩의 행으로 바꿉니다 (예열 선택이 앞 단계 행과 겹치는 구간을 빼고 예산 순서로 쓰고, 유지가 만지는 순서를 정하는 데 씀).
    /// 파일이 지금 저장할 때와 다르면 (크기·수정 시각, current 가 null 이면 없음) 그 파일 블록은 뺍니다. 마지막 블록은 파일 끝에서 자릅니다.
    public static List<TraceEvent> ToRows(LoadRefillList list, Func<string, LoadRefillFileStat?> current, CancellationToken cancellationToken = default)
    {
        var valid = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<TraceEvent>(list.Blocks.Count);
        foreach (LoadRefillBlock block in list.Blocks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!valid.TryGetValue(block.Path, out bool ok))
            {
                valid[block.Path] = ok = list.Files.TryGetValue(block.Path, out LoadRefillFileStat saved) && current(block.Path) is { } now && now == saved;
            }

            if (ok)
            {
                long offset = block.Block * LoadBoundaries.Block;
                rows.Add(new TraceEvent(block.Path, offset, Math.Min(LoadBoundaries.Block, list.Files[block.Path].Size - offset)));
            }
        }

        return rows;
    }
}

/// 게임별 로드 구간 블록 목록 파일 (계획 폴더, 원시 트레이스 옆). 임시 파일에 쓴 뒤 바꿔 끼웁니다.
public static class LoadRefillStore
{
    public const int Version = 1;
    public const string Suffix = ".loadrefill.json";

    public static string PathFor(string rawTracePath) => rawTracePath + Suffix;

    /// 플레이 전체 기록의 블록 순위 목록 (같은 형식, 로드 구간 목록과 따로 둠: 계획·존 밖 덧붙임이 쓰는 prefetch.json·warmonly.json 에 섞이지 않음).
    public const string RankedSuffix = ".ranked.json";

    public static string RankedPathFor(string rawTracePath) => rawTracePath + RankedSuffix;

    /// 목록을 보강하려고 추가로 추적한 횟수를 남기는 곁 파일 (서비스를 다시 시작해도 유지, 원시 트레이스를 다시 쓸 때 지워짐).
    public static string TriedPath(string rawTracePath) => rawTracePath + ".loadrefill.tried";

    public static int ReadTried(string rawTracePath)
    {
        try
        {
            return File.Exists(TriedPath(rawTracePath)) && int.TryParse(File.ReadAllText(TriedPath(rawTracePath)).Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public static void WriteTried(string rawTracePath, int tries) =>
        File.WriteAllText(TriedPath(rawTracePath), tries.ToString(CultureInfo.InvariantCulture));

    public static void Write(string path, LoadRefillList list, DateTimeOffset builtAtUtc)
    {
        var paths = list.Files.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();
        var index = paths.Select((p, i) => (p, i)).ToDictionary(x => x.p, x => (long)x.i, StringComparer.OrdinalIgnoreCase);
        var root = new JObject
        {
            { "version", (long)Version },
            { "block_size", (long)LoadBoundaries.Block },
            { "built_at_utc", builtAtUtc.ToString("O", CultureInfo.InvariantCulture) },
            { "plays", (long)list.Plays },
            { "loads", (long)list.Loads },
            { "plays_without_starts", (long)list.PlaysWithoutStarts },
            { "skipped_nocache_blocks", list.SkippedNoCacheBlocks },
            { "dropped_by_cap_blocks", list.DroppedByCapBlocks },
            { "files", paths.Select(p => (object?)new JObject
                {
                    { "path", p },
                    { "size", list.Files[p].Size },
                    { "mtime_ticks", list.Files[p].MTimeTicks },
                }).ToList() },
            { "blocks", list.Blocks.Select(b => (object?)(b.Score == 0 ? new List<object?> { index[b.Path], b.Block, (long)b.Count, b.LatencyUs } : new List<object?> { index[b.Path], b.Block, (long)b.Count, b.LatencyUs, (long)b.Score })).ToList() }, // 다섯째 칸 (경기 가중 점수) 은 순위 목록에만 있음
        };

        string temp = path + ".tmp";
        string text = PyJson.Dumps(root, ensureAscii: false) + "\n";
        DurableFile.WriteAllBytes(temp, PyJson.Utf8.GetBytes(PyJson.ToCrlf(text)));
        File.Move(temp, path, true);
    }

    /// 없거나 읽을 수 없거나 형식이 다르면 null (목록은 부가 정보라 없으면 지금 동작 그대로).
    public static LoadRefillList? Read(string path)
    {
        try
        {
            if (!File.Exists(path)
                || JsonRead.ReadFile(path) is not JObject root
                || !Long(root, "version", out long version) || version != Version
                || !Long(root, "block_size", out long blockSize) || blockSize != LoadBoundaries.Block
                || !Long(root, "plays", out long plays) || !Long(root, "loads", out long loads)
                || !Long(root, "plays_without_starts", out long withoutStarts)
                || !Long(root, "skipped_nocache_blocks", out long skipped) || !Long(root, "dropped_by_cap_blocks", out long dropped)
                || !root.TryGetValue("files", out object? filesValue) || filesValue is not List<object?> fileRows
                || !root.TryGetValue("blocks", out object? blocksValue) || blocksValue is not List<object?> blockRows)
            {
                return null;
            }

            var paths = new List<string>(fileRows.Count);
            var files = new Dictionary<string, LoadRefillFileStat>(StringComparer.OrdinalIgnoreCase);
            foreach (object? row in fileRows)
            {
                if (row is not JObject file || !file.TryGetValue("path", out object? name) || name is not string text || text.Length == 0
                    || !Long(file, "size", out long size) || size < 0 || !Long(file, "mtime_ticks", out long ticks))
                {
                    return null;
                }

                paths.Add(text);
                files[text] = new LoadRefillFileStat(size, ticks);
            }

            var blocks = new List<LoadRefillBlock>(blockRows.Count);
            foreach (object? row in blockRows)
            {
                if (row is not List<object?> { Count: 4 or 5 } cells || cells[0] is not long file || file < 0 || file >= paths.Count
                    || cells[1] is not long block || block < 0 || cells[2] is not long count || count < 1 || count > int.MaxValue
                    || cells[3] is not long latency || latency < 0
                    || cells.Count == 5 && (cells[4] is not long score || score < count || score > int.MaxValue))
                {
                    return null;
                }

                blocks.Add(new LoadRefillBlock(paths[(int)file], block, (int)count, latency, cells.Count == 5 ? (int)(long)cells[4]! : 0));
            }

            return new LoadRefillList(blocks, files, (int)plays, (int)loads, (int)withoutStarts, skipped, dropped);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or OverflowException or FormatException or ArgumentException)
        {
            return null;
        }
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
}
