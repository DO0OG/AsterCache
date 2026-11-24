using System.Globalization;
using AsterCache.Core.Json;
using AsterCache.Core.Prefetch;

namespace AsterCache.Core.Trace;

/// 파일 한 개의 현재 크기와 수정 시각 (UTC ticks). 저장한 목록이 지금 파일과 맞는지 보는 표지입니다.
public readonly record struct LoadRefillFileStat(long Size, long MTimeTicks);

/// 한 플레이의 I/O 집계와 그 안에서 등록 실행 파일이 새로 시작한 시각 (집계의 관찰 시작을 0 으로 한 마이크로초).
public sealed record LoadRefillSource(IoSummary Summary, IReadOnlyList<long> StartsUs);

/// 로드 구간에서 처음 접근한 64 KiB 블록 하나. Count 는 그 블록이 나온 플레이 수, LatencyUs 는 플레이별 지연 합의 합입니다.
/// Score 는 플레이 전체 기록의 순위 목록에서만 채우는 경기 가중 점수 (Count + 2 × 가중 회차 수 + Extra): 0 이면 구간을 모르는 목록입니다.
/// Extra 는 그 가운데 플레이마다 남기는 디스크 전용 요약이 더한 점수입니다 (순위 블록을 다시 만들지 않고 이 부분만 갱신하려고 따로 저장).
/// 순위 목록의 PlayOrder 는 Score 내림 순서의 블록 번호 순열, SparseFiles 는 스파스 파일 (둘 다 순위 파일을 쓸 때 정해 파일에 같이 저장).
public sealed record LoadRefillBlock(string Path, long Block, int Count, long LatencyUs, int Score = 0, int Extra = 0);

/// 모은 로드 구간 블록 목록 (점수 순)과 모으는 과정의 집계.
public sealed record LoadRefillList(
    IReadOnlyList<LoadRefillBlock> Blocks,
    IReadOnlyDictionary<string, LoadRefillFileStat> Files,
    int Plays,
    int Loads,
    int PlaysWithoutStarts,
    long SkippedNoCacheBlocks,
    long DroppedByCapBlocks,
    IReadOnlyList<int>? PlayOrder = null,
    IReadOnlySet<string>? SparseFiles = null)
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
    /// 시작 시각이 없거나 첫 시작보다 앞선 행, 이벤트 유실·상한 초과가 있는 회차의 행은 구간을 나눌 수 없어 1점입니다. 120초는 실행 직후 로딩의 근사입니다 (로드가 더 길 수 있음).
    /// 점수는 요약이 이미 가진 정보로만 셉니다: 서비스 자신의 읽기로 판정돼 요약에서 빠진 행은 점수에 들지 않고 (서비스 읽기와 겹친 한 번짜리 페이징 읽기가 빠질 수 있어 점수는 낮게 잡히는 쪽), 캐시를 거치지 않은 행 (페이징 0) 도 뺍니다.
    /// 지금 파일의 크기·수정 시각이 목록을 만들 때와 다르면 (ToRows) 그 파일의 블록은 읽을 때 빠집니다.
    /// isWritten 이 있으면 (순위 목록이 게임이 쓰는 파일도 담을 때) 그 파일의 블록은 WrittenMinPlays 개 이상의 플레이에서 나온 것만 담습니다.
    /// appendOnly 가 참인 파일 (크기가 줄어든 적이 없어 덧붙이기로 보는 파일) 은 관찰 중 수정된 플레이의 읽기도 점수에 넣습니다: 이미 쓰인 위치의 내용은 바뀌지 않으므로 그 플레이에서 읽힌 위치 (시작 때 크기 안이든 덧붙인 뒤든) 는 다음 플레이에도 있습니다.
    /// scoreOnly (플레이마다 남기는 디스크 전용 요약, window 가 null 일 때만 씀) 는 경기 가중 점수에만 더합니다: Count·지연 합·목록에 드는 블록과 그 순서는 sources 만으로 정해져 이 요약이 있어도 바뀌지 않습니다 (초기 로드 목록·예열 순위의 입력을 그대로 둠).
    /// 그래서 디스크 전용 요약에서만 나온 블록은 목록에 들지 못해 점수를 받지 못하고, 원시 트레이스가 그 블록을 목록에 올린 뒤에야 점수가 합쳐집니다. 채점 규칙은 ScoreDiskOnly 의 설명에 있습니다.
    public static LoadRefillList Collect(IEnumerable<LoadRefillSource> sources, Func<string, LoadRefillFileStat?> stat, TimeSpan? window, long capBytes, Func<string, bool>? isWritten = null, Func<string, bool>? appendOnly = null,
        IEnumerable<LoadRefillSource>? scoreOnly = null)
    {
        long windowUs = window is { } w ? (long)w.TotalMicroseconds : 0;
        var stats = new Dictionary<string, LoadRefillFileStat?>(StringComparer.OrdinalIgnoreCase);
        var merged = new Dictionary<string, Dictionary<long, (int Count, long LatencyUs, int Score)>>(StringComparer.OrdinalIgnoreCase);
        var extras = new Dictionary<(string Path, long Block), int>(PathBlockComparer.Instance); // 디스크 전용 요약이 더한 점수
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
            bool exact = summary.Quality.EventsLost == 0 && !summary.Quality.Truncated; // 이벤트 유실이나 상한 초과가 있는 회차는 첫 접근 시각을 믿을 수 없어 구간 가중 없이 1점
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

                int points = window is null && exact && row.Requests == 1 && OutsideLoadWindow(starts, row.FirstSessionUs) ? PlayPhaseWeight : 1;
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

        if (window is null && scoreOnly is not null)
        {
            foreach (LoadRefillSource source in scoreOnly)
            {
                ScoreDiskOnly(source, (path, block) => merged.TryGetValue(path, out Dictionary<long, (int Count, long LatencyUs, int Score)>? blocks) && blocks.ContainsKey(block),
                    path => stats.GetValueOrDefault(path), appendOnly, (path, block, points) =>
                    {
                        (int Count, long LatencyUs, int Score) old = merged[path][block];
                        merged[path][block] = old with { Score = old.Score + points };
                        extras[(path, block)] = extras.GetValueOrDefault((path, block)) + points;
                    });
            }
        }

        var all = new List<LoadRefillBlock>();
        foreach ((string path, Dictionary<long, (int Count, long LatencyUs, int Score)> blocks) in merged)
        {
            int minPlays = isWritten?.Invoke(path) == true ? WrittenMinPlays : 1;
            all.AddRange(blocks.Where(b => b.Value.Count >= minPlays).Select(b => new LoadRefillBlock(path, b.Key, b.Value.Count, b.Value.LatencyUs, window is null ? b.Value.Score : 0, extras.GetValueOrDefault((path, b.Key)))));
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

    /// 순위 목록을 다시 만들지 않고 디스크 전용 요약의 점수 (Extra) 만 새로 셉니다: 블록·순서·플레이 수·지연 합과 파일의 크기·수정 시각 (목록을 만든 때의 세대) 은 그대로 두고, 원시 요약으로 센 점수 (Score - Extra) 에 새 Extra 를 더해 점수 순서를 다시 정합니다.
    /// 요약이 본 파일의 크기·수정 시각은 지금 파일이 아니라 목록에 저장된 세대와 대조하므로 파일 속성을 조회하지 않습니다. 관찰 중 바뀐 파일의 행은 덧붙이기 판정이 없어 점수가 없습니다 (순위 목록을 다시 만들 때 덧붙이기 파일로 판정되면 그 행도 셈).
    /// 취소되면 OperationCanceledException (목록은 바뀌지 않음).
    public static LoadRefillList Rescore(LoadRefillList list, IEnumerable<LoadRefillSource> scoreOnly, CancellationToken ct = default)
    {
        var position = new Dictionary<(string Path, long Block), int>(PathBlockComparer.Instance);
        for (int i = 0; i < list.Blocks.Count; i++)
        {
            position[(list.Blocks[i].Path, list.Blocks[i].Block)] = i;
        }

        int[] extra = new int[list.Blocks.Count];
        foreach (LoadRefillSource source in scoreOnly)
        {
            ScoreDiskOnly(source, (path, block) => position.ContainsKey((path, block)), path => list.Files.TryGetValue(path, out LoadRefillFileStat saved) ? saved : null, null,
                (path, block, points) => extra[position[(path, block)]] += points, ct);
        }

        LoadRefillList rescored = list with { Blocks = [.. list.Blocks.Select((b, i) => b with { Score = b.Score - b.Extra + extra[i], Extra = extra[i] })] };
        return WithPlayOrder(rescored, path => list.SparseFiles?.Contains(path) == true);
    }

    // 디스크 전용 요약 하나를 경기 가중 점수에만 더합니다 (listed 가 참인 블록만, Count·지연 합은 그대로). 채점 규칙:
    // - 캐시를 거치지 않은 행 (페이징 0), 관찰 중 바뀐 파일 (덧붙이기 파일 제외), 파일 크기·수정 시각이 목록의 세대 (generation) 와 다른 파일 (요약을 남긴 뒤 게임이 파일을 바꿈) 의 행은 점수 없음.
    // - 한 번만 닿고 (Requests 1) 실행 파일 시작 뒤 Window 가 지난 때에 처음 닿았으며 이벤트 유실·상한 초과가 없고 출처가 확실한 행은 PlayPhaseWeight 점, 그 밖의 행은 1점.
    // - 서비스 자신의 읽기와 겹쳐 게임의 읽기인지 알 수 없는 요청 (UncertainRequests) 이 섞인 행은 3점을 받지 못하고 1점입니다: 디스크 전용 요약은 게임이 같은 구간을 읽었는지 알 수 없으므로 겹친 행을 모두 빼면
    //   붙들기·유지가 읽은 블록이 플레이마다 점수를 잃어 순위에서 밀려나는 되먹임이 생기고, 모두 3점으로 세면 서비스가 읽은 블록이 스스로 점수를 올립니다. 1점은 그 플레이에서 그 블록에 닿은 읽기가 있었다는 만큼만 셉니다.
    // - 이벤트 유실이나 상한 초과가 있는 회차는 첫 접근 시각을 믿을 수 없어 구간 가중 없이 1점 (읽기가 일부 빠졌을 수 있어도 점수를 깎지는 않음). 이 규칙은 점수를 셀 페이징 읽기 행에만 걸립니다 (페이징 0 인 행은 어느 경우에도 점수 없음).
    private static void ScoreDiskOnly(LoadRefillSource source, Func<string, long, bool> listed, Func<string, LoadRefillFileStat?> generation, Func<string, bool>? appendOnly, Action<string, long, int> add, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        long[] starts = [.. source.StartsUs.Distinct().Order()];
        IoSummary summary = source.Summary;
        bool exact = summary.Quality.EventsLost == 0 && !summary.Quality.Truncated;
        int seen = 0;
        foreach (IoSummaryRow row in summary.Rows)
        {
            if ((++seen & 0xFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            IoSummaryFile file = summary.Files[row.FileNumber];
            if (row.PagingRequests == 0 || (file.ChangedDuringSession && appendOnly?.Invoke(file.Path) != true) || !listed(file.Path, row.BlockNumber))
            {
                continue;
            }

            if (file.Size is { } size && file.MTimeTicks is { } ticks && generation(file.Path) is { } now && (now.Size != size || now.MTimeTicks != ticks))
            {
                continue;
            }

            add(file.Path, row.BlockNumber, exact && row.Requests == 1 && row.UncertainRequests == 0 && OutsideLoadWindow(starts, row.FirstSessionUs) ? PlayPhaseWeight : 1);
        }
    }

    private sealed class PathBlockComparer : IEqualityComparer<(string Path, long Block)>
    {
        public static readonly PathBlockComparer Instance = new();

        public bool Equals((string Path, long Block) a, (string Path, long Block) b) => a.Block == b.Block && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Path, long Block) k) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.Path), k.Block);
    }

    // 첫 접근 시각이 어느 실행 파일 시작 뒤 Window 가 지난 때인가 (시작이 없거나 첫 시작보다 앞서면 구간을 모르므로 거짓).
    private static bool OutsideLoadWindow(long[] starts, long firstUs)
    {
        int at = Array.BinarySearch(starts, firstUs);
        int index = at >= 0 ? at : ~at - 1;
        return index >= 0 && firstUs - starts[index] >= (long)Window.TotalMicroseconds;
    }

    /// 순위 목록에 경기 가중 점수 순서 (점수 내림, 같으면 목록 순서의 블록 번호 순열) 와 스파스 파일 표지를 더합니다.
    /// 순위 파일을 다시 쓸 때 (게임이 꺼져 있을 때) 한 번만 부르고 파일에 같이 저장하므로, 게임 중에는 정렬도 파일 속성 조회도 하지 않습니다.
    /// 점수가 있는 블록이 하나도 없으면 (구간을 모르는 예전 목록) 목록 그대로를 돌려줍니다.
    public static LoadRefillList WithPlayOrder(LoadRefillList list, Func<string, bool> isSparse)
    {
        if (list.Blocks.All(b => b.Score == 0))
        {
            return list;
        }

        int[] order = [.. Enumerable.Range(0, list.Blocks.Count).OrderByDescending(i => list.Blocks[i].Score).ThenBy(i => i)];
        var sparse = new HashSet<string>(list.Files.Keys.Where(isSparse), StringComparer.OrdinalIgnoreCase);
        return list with { PlayOrder = order, SparseFiles = sparse };
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
    /// valid 는 파일별 대조 결과의 공유 표 (같은 목록의 다른 순서를 이어 만들 때 파일 조회를 되풀이하지 않게).
    public static List<TraceEvent> ToRows(LoadRefillList list, Func<string, LoadRefillFileStat?> current, CancellationToken cancellationToken = default, Dictionary<string, bool>? valid = null)
        => ToRows(list, list.Blocks, current, cancellationToken, valid);

    /// ToRows 와 같되 blocks (list 의 블록을 다른 순서로 놓은 것) 의 순서로 행을 만듭니다. 파일 대조는 list 의 저장된 크기·수정 시각 기준입니다.
    public static List<TraceEvent> ToRows(LoadRefillList list, IEnumerable<LoadRefillBlock> blocks, Func<string, LoadRefillFileStat?> current, CancellationToken cancellationToken = default, Dictionary<string, bool>? valid = null)
    {
        valid ??= new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<TraceEvent>(list.Blocks.Count);
        foreach (LoadRefillBlock block in blocks)
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
            { "blocks", list.Blocks.Select(b => (object?)new List<object?> { index[b.Path], b.Block, (long)b.Count, b.LatencyUs }).ToList() },
        };
        if (list.PlayOrder is { } order && order.Count == list.Blocks.Count && list.Blocks.Any(b => b.Score != 0))
        {
            // 경기 가중 점수와 그 순서는 블록 행 (네 칸 그대로) 과 따로 둠: 이 항목들을 모르는 이전 판은 무시하고 행을 그대로 읽어 순위를 잃지 않음 (순위 목록에만 있음).
            // 쉼표로 이은 문자열로 저장해 값이 이상해도 (JSON 숫자 범위 오류로 파일 전체가 읽히지 않는 일 없이) 이 항목만 버릴 수 있게 함.
            root.Add("play_scores", string.Join(',', list.Blocks.Select(b => b.Score.ToString(CultureInfo.InvariantCulture))));
            root.Add("play_order", string.Join(',', order.Select(i => i.ToString(CultureInfo.InvariantCulture))));
            root.Add("play_extra", string.Join(',', list.Blocks.Select(b => b.Extra.ToString(CultureInfo.InvariantCulture)))); // 점수 가운데 디스크 전용 요약이 더한 몫 (없는 파일은 0 으로 읽음)
            root.Add("play_sparse", string.Join(',', paths.Select((p, i) => (p, i)).Where(x => list.SparseFiles?.Contains(x.p) == true).Select(x => x.i.ToString(CultureInfo.InvariantCulture))));
        }

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
                if (row is not List<object?> { Count: 4 } cells || cells[0] is not long file || file < 0 || file >= paths.Count
                    || cells[1] is not long block || block < 0 || cells[2] is not long count || count < 1 || count > int.MaxValue
                    || cells[3] is not long latency || latency < 0)
                {
                    return null;
                }

                blocks.Add(new LoadRefillBlock(paths[(int)file], block, (int)count, latency));
            }

            // 경기 가중 점수 (선택 항목): 하나라도 맞지 않으면 점수만 버리고 (예전 순서 그대로) 네 칸 블록 행은 그대로 읽음
            if (ReadPlayExtras(root, blocks, paths) is { } extras)
            {
                return new LoadRefillList(extras.Blocks, files, (int)plays, (int)loads, (int)withoutStarts, skipped, dropped, extras.Order, extras.Sparse);
            }

            return new LoadRefillList(blocks, files, (int)plays, (int)loads, (int)withoutStarts, skipped, dropped);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PyValueException or OverflowException or FormatException or ArgumentException)
        {
            return null;
        }
    }

    // 점수 (디스크 전용 요약이 더한 몫 Extra 를 뺀 기본 점수가 플레이 수 + 2 × 가중 회차 수 이므로 플레이 수 이상 3배 이하이고 같은 홀짝, Extra 는 0 이상 3 × 디스크 전용 요약의 최대 수 이하),
    // 순서 (블록 번호 순열), 스파스 파일 번호가 모두 맞을 때만 돌려줍니다. play_extra 가 없으면 (예전 파일) Extra 는 모두 0 입니다.
    private static (List<LoadRefillBlock> Blocks, int[] Order, HashSet<string> Sparse)? ReadPlayExtras(JObject root, List<LoadRefillBlock> blocks, List<string> paths)
    {
        int[]? extras = null;
        if (root.TryGetValue("play_extra", out object? extraValue) && (extraValue is not string extraText || Ints(extraText) is not { } parsedExtras || (extras = parsedExtras).Length != blocks.Count))
        {
            return null;
        }

        if (!root.TryGetValue("play_scores", out object? scoresValue) || scoresValue is not string scoresText
            || !root.TryGetValue("play_order", out object? orderValue) || orderValue is not string orderText
            || !root.TryGetValue("play_sparse", out object? sparseValue) || sparseValue is not string sparseText
            || Ints(scoresText) is not { } scores || scores.Length != blocks.Count
            || Ints(orderText) is not { } order || order.Length != blocks.Count
            || Ints(sparseText) is not { } sparse || sparse.Any(i => i < 0 || i >= paths.Count))
        {
            return null;
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            int count = blocks[i].Count;
            long extra = extras?[i] ?? 0;
            long basis = scores[i] - extra; // 원시 요약만으로 센 점수
            if (extra > 3L * IoSummaryStore.MaxPlayDiskSummaries || basis < count || basis > 3L * count || (basis - count) % 2 != 0)
            {
                return null;
            }
        }

        var seen = new bool[blocks.Count];
        foreach (int i in order)
        {
            if (i < 0 || i >= blocks.Count || seen[i])
            {
                return null;
            }

            seen[i] = true;
        }

        return ([.. blocks.Select((b, i) => b with { Score = scores[i], Extra = extras?[i] ?? 0 })], order, new HashSet<string>(sparse.Select(i => paths[i]), StringComparer.OrdinalIgnoreCase));
    }

    // 쉼표로 이은 정수 문자열 (빈 문자열은 빈 목록), 하나라도 정수가 아니거나 범위를 벗어나면 null.
    private static int[]? Ints(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        string[] parts = text.Split(',');
        var values = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out values[i]))
            {
                return null;
            }
        }

        return values;
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
